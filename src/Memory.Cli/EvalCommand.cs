using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Memory.Cli;

/// <summary>
/// Retrieval evaluation. Two-step flow: <c>gen-queries</c> samples N notes from
/// the project and asks the LLM (server-side) to write one realistic query per
/// note that the note answers; the note id is the gold answer. <c>run</c>
/// pushes those (noteId, query) pairs through <c>/api/search</c> and reports
/// Recall@K + MRR. Run before/after pipeline changes to see if your tweak
/// actually helped — without numbers, every search "improvement" is a guess.
/// </summary>
internal static class EvalCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            PrintHelp();
            return 0;
        }

        return args[0] switch
        {
            "gen-queries" => await GenQueriesAsync(args[1..]).ConfigureAwait(false),
            "run" => await RunEvalAsync(args[1..]).ConfigureAwait(false),
            "sweep" => await SweepEvalAsync(args[1..]).ConfigureAwait(false),
            "gate" => await GateEvalAsync(args[1..]).ConfigureAwait(false),
            _ => Fail($"Unknown eval sub-command '{args[0]}'. See 'memory eval help'."),
        };
    }

    private static async Task<int> GenQueriesAsync(string[] args)
    {
        var (apiUrl, apiKey, opts) = ParseFlags(args);
        if (apiKey is null) return Fail("--api-key (or MEMORY_API_KEY) required.");

        var count = opts.TryGetValue("--count", out var c) && int.TryParse(c, out var n) ? n : 30;
        var outPath = opts.GetValueOrDefault("--out", "eval-queries.json");

        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        Console.WriteLine($"Generating {count} eval queries via {apiUrl}/api/eval/queries…");
        Console.WriteLine("(one LLM call per note, ~1-2s each)");

        var resp = await http.PostAsJsonAsync("/api/eval/queries", new { count }).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        await File.WriteAllTextAsync(outPath, json).ConfigureAwait(false);
        var doc = JsonDocument.Parse(json);
        Console.WriteLine($"Wrote {doc.RootElement.GetProperty("count").GetInt32()} queries → {outPath}");
        return 0;
    }

    private static async Task<int> RunEvalAsync(string[] args)
    {
        var (apiUrl, apiKey, opts) = ParseFlags(args);
        if (apiKey is null) return Fail("--api-key (or MEMORY_API_KEY) required.");

        var inPath = opts.GetValueOrDefault("--in", "eval-queries.json");
        var outPath = opts.GetValueOrDefault("--out", "");
        var topK = opts.TryGetValue("--top-k", out var tk) && int.TryParse(tk, out var k) ? k : 10;

        if (!File.Exists(inPath))
        {
            return Fail($"Queries file not found: {inPath}. Run 'memory eval gen-queries' first.");
        }

        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var queries = await LoadQueriesAsync(inPath).ConfigureAwait(false);

        var route = BuildRoute(opts, topK);
        var body = new Dictionary<string, object?>
        {
            ["queries"] = queries,
            ["topK"] = topK,
            ["context"] = new { caller = "memory-eval", activeProject = opts.GetValueOrDefault("--project") },
        };
        if (route.Count > 0)
        {
            body["route"] = route;
        }

        Console.WriteLine($"Running {queries.Count} queries against {apiUrl}/api/search (top_k={topK})…");
        if (route.Count > 0)
        {
            Console.WriteLine($"Profile route: {JsonSerializer.Serialize(route)}");
        }

        var resp = await http.PostAsJsonAsync("/api/eval/run", body).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var resultJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            await File.WriteAllTextAsync(outPath, resultJson).ConfigureAwait(false);
            Console.WriteLine($"Wrote eval result -> {outPath}");
        }
        using var resultDoc = JsonDocument.Parse(resultJson);
        var root = resultDoc.RootElement;

        Console.WriteLine();
        Console.WriteLine("=== Aggregate ===");
        Console.WriteLine($"  N            {root.GetProperty("n").GetInt32()}");
        Console.WriteLine($"  Recall@1     {root.GetProperty("recallAt1").GetDouble():P1}");
        Console.WriteLine($"  Recall@3     {root.GetProperty("recallAt3").GetDouble():P1}");
        Console.WriteLine($"  Recall@5     {root.GetProperty("recallAt5").GetDouble():P1}");
        Console.WriteLine($"  Recall@{topK,-2}    {root.GetProperty("recallAt10").GetDouble():P1}");
        Console.WriteLine($"  MRR          {root.GetProperty("mrr").GetDouble():F4}");
        Console.WriteLine($"  Not found    {root.GetProperty("notFound").GetInt32()}");
        Console.WriteLine($"  Abstained    {root.GetProperty("abstained").GetInt32()}");
        Console.WriteLine($"  Mean latency {root.GetProperty("meanLatencyMs").GetDouble():F0} ms");
        Console.WriteLine($"  P50 latency  {root.GetProperty("p50LatencyMs").GetDouble():F0} ms");
        Console.WriteLine($"  P95 latency  {root.GetProperty("p95LatencyMs").GetDouble():F0} ms");

        Console.WriteLine();
        Console.WriteLine("=== Per-query (rank of gold note in results) ===");
        Console.WriteLine($"  {"rank",4}  {"ms",6}  query");
        foreach (var p in root.GetProperty("perQuery").EnumerateArray())
        {
            var rank = p.GetProperty("rank").GetInt32();
            var latencyMs = p.GetProperty("latencyMs").GetInt64();
            var query = p.GetProperty("query").GetString() ?? "";
            var rankStr = rank < 0 ? "  --" : $"{rank,4}";
            var preview = query.Length > 90 ? query[..90] + "…" : query;
            Console.WriteLine($"  {rankStr}  {latencyMs,6}  {preview}");
        }
        return 0;
    }

    private static async Task<int> SweepEvalAsync(string[] args)
    {
        var (apiUrl, apiKey, opts) = ParseFlags(args);
        if (apiKey is null) return Fail("--api-key (or MEMORY_API_KEY) required.");

        var inPath = opts.GetValueOrDefault("--in", "eval-queries.json");
        var outDir = opts.GetValueOrDefault("--out-dir", "eval-results");
        var topK = opts.TryGetValue("--top-k", out var tk) && int.TryParse(tk, out var k) ? k : 10;

        if (!File.Exists(inPath))
        {
            return Fail($"Queries file not found: {inPath}. Run 'memory eval gen-queries' first.");
        }

        var queries = await LoadQueriesAsync(inPath).ConfigureAwait(false);
        var profiles = SelectProfiles(opts.GetValueOrDefault("--profiles"), topK);
        Directory.CreateDirectory(outDir);

        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var summary = new List<Dictionary<string, object?>>();
        Console.WriteLine($"Running sweep: {profiles.Count} profiles x {queries.Count} queries against {apiUrl}/api/search");

        foreach (var profile in profiles)
        {
            var body = new Dictionary<string, object?>
            {
                ["queries"] = queries,
                ["topK"] = topK,
                ["context"] = new { caller = "memory-eval-sweep", activeProject = opts.GetValueOrDefault("--project") },
            };
            if (profile.Route is { Count: > 0 })
            {
                body["route"] = profile.Route;
            }

            Console.WriteLine($"- {profile.Name}");
            var resp = await http.PostAsJsonAsync("/api/eval/run", body).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var resultJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var resultPath = Path.Combine(outDir, $"{profile.Name}.json");
            await File.WriteAllTextAsync(resultPath, resultJson).ConfigureAwait(false);

            using var resultDoc = JsonDocument.Parse(resultJson);
            var root = resultDoc.RootElement;
            var row = new Dictionary<string, object?>
            {
                ["profile"] = profile.Name,
                ["n"] = root.GetProperty("n").GetInt32(),
                ["recallAt1"] = root.GetProperty("recallAt1").GetDouble(),
                ["recallAt3"] = root.GetProperty("recallAt3").GetDouble(),
                ["recallAt5"] = root.GetProperty("recallAt5").GetDouble(),
                ["recallAtK"] = root.GetProperty("recallAt10").GetDouble(),
                ["mrr"] = root.GetProperty("mrr").GetDouble(),
                ["notFound"] = root.GetProperty("notFound").GetInt32(),
                ["abstained"] = root.GetProperty("abstained").GetInt32(),
                ["meanLatencyMs"] = root.GetProperty("meanLatencyMs").GetDouble(),
                ["p50LatencyMs"] = root.GetProperty("p50LatencyMs").GetInt64(),
                ["p95LatencyMs"] = root.GetProperty("p95LatencyMs").GetInt64(),
                ["resultPath"] = resultPath,
            };
            summary.Add(row);
            Console.WriteLine($"  R@3={row["recallAt3"]:P1} MRR={row["mrr"]:F4} abstained={row["abstained"]} p95={row["p95LatencyMs"]}ms");
        }

        var summaryJson = JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            input = inPath,
            topK,
            profiles = summary,
        }, new JsonSerializerOptions { WriteIndented = true });
        var summaryPath = Path.Combine(outDir, "summary.json");
        await File.WriteAllTextAsync(summaryPath, summaryJson).ConfigureAwait(false);

        var markdownPath = Path.Combine(outDir, "summary.md");
        await File.WriteAllTextAsync(markdownPath, BuildSweepMarkdown(summary, topK)).ConfigureAwait(false);
        Console.WriteLine($"Wrote summary -> {summaryPath}");
        Console.WriteLine($"Wrote markdown -> {markdownPath}");
        return 0;
    }

    private static async Task<int> GateEvalAsync(string[] args)
    {
        var (_, _, opts) = ParseFlags(args);
        var summaryPath = opts.GetValueOrDefault("--summary", Path.Combine("eval-results", "summary.json"));
        var profileName = opts.GetValueOrDefault("--profile", "memory-light");

        if (!File.Exists(summaryPath))
        {
            return Fail($"Summary file not found: {summaryPath}. Run 'memory eval sweep' first.");
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryPath).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            return Fail($"Invalid summary file: {summaryPath} does not contain profiles[].");
        }

        JsonElement? profile = null;
        foreach (var candidate in profiles.EnumerateArray())
        {
            if (candidate.TryGetProperty("profile", out var name)
                && string.Equals(name.GetString(), profileName, StringComparison.OrdinalIgnoreCase))
            {
                profile = candidate;
                break;
            }
        }

        if (profile is null)
        {
            return Fail($"Profile '{profileName}' not found in {summaryPath}.");
        }

        var p = profile.Value;
        var checks = new List<(string Name, double Actual, string Op, double Threshold, bool Passed)>
        {
            BuildMinCheck("recallAt1", p, opts, "--min-recall-at-1", defaultValue: 0),
            BuildMinCheck("recallAt3", p, opts, "--min-recall-at-3", defaultValue: 0.95),
            BuildMinCheck("recallAt5", p, opts, "--min-recall-at-5", defaultValue: 0),
            BuildMinCheck("mrr", p, opts, "--min-mrr", defaultValue: 0.90),
            BuildMaxCheck("abstained", p, opts, "--max-abstained", defaultValue: 0),
            BuildMaxCheck("notFound", p, opts, "--max-not-found", defaultValue: 0),
            BuildMaxCheck("p95LatencyMs", p, opts, "--max-p95-ms", defaultValue: 1000),
            BuildMaxCheck("meanLatencyMs", p, opts, "--max-mean-ms", defaultValue: double.PositiveInfinity),
        };

        var activeChecks = checks.Where(c => !double.IsNaN(c.Threshold)).ToArray();
        Console.WriteLine($"Eval gate: {summaryPath} profile={profileName}");
        foreach (var c in activeChecks)
        {
            Console.WriteLine($"  {(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Actual:0.####} {c.Op} {c.Threshold:0.####}");
        }

        if (activeChecks.Any(c => !c.Passed))
        {
            Console.Error.WriteLine("Eval gate failed.");
            return 2;
        }

        Console.WriteLine("Eval gate passed.");
        return 0;
    }

    private static Dictionary<string, object?> BuildRoute(Dictionary<string, string> opts, int topK)
    {
        var route = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["maxResults"] = topK,
            ["routerModel"] = "memory-eval",
        };

        if (opts.TryGetValue("--mode", out var mode)) route["mode"] = mode;
        if (opts.ContainsKey("--no-reranker")) route["useReranker"] = false;
        if (opts.ContainsKey("--no-graph")) route["useGraph"] = false;
        if (opts.ContainsKey("--no-expansion")) route["useQueryExpansion"] = false;
        if (opts.ContainsKey("--no-vector")) route["useVectorSearch"] = false;
        if (opts.ContainsKey("--no-bm25")) route["useBm25Search"] = false;
        if (opts.ContainsKey("--no-image")) route["useImageSearch"] = false;
        if (opts.TryGetValue("--vector-weight", out var vw) && double.TryParse(vw, out var vectorWeight)) route["vectorWeight"] = vectorWeight;
        if (opts.TryGetValue("--bm25-weight", out var bw) && double.TryParse(bw, out var bm25Weight)) route["bm25Weight"] = bm25Weight;
        if (opts.TryGetValue("--graph-weight", out var gw) && double.TryParse(gw, out var graphWeight)) route["graphWeight"] = graphWeight;
        if (opts.TryGetValue("--image-weight", out var iw) && double.TryParse(iw, out var imageWeight)) route["imageWeight"] = imageWeight;

        return route.Count == 2 && !opts.ContainsKey("--mode")
            ? new Dictionary<string, object?>()
            : route;
    }

    private static async Task<List<object>> LoadQueriesAsync(string inPath)
    {
        var raw = await File.ReadAllTextAsync(inPath).ConfigureAwait(false);
        using var queriesDoc = JsonDocument.Parse(raw);
        return queriesDoc.RootElement.GetProperty("queries").EnumerateArray()
            .Select(q =>
            {
                var memoryTypes = ReadMemoryTypes(q);
                return new
                {
                    noteId = q.GetProperty("noteId").GetGuid(),
                    query = q.GetProperty("query").GetString() ?? "",
                    memoryTypes,
                } as object;
            })
            .ToList();
    }

    private static string[]? ReadMemoryTypes(JsonElement query)
    {
        if (query.TryGetProperty("memoryTypes", out var types) && types.ValueKind == JsonValueKind.Array)
        {
            var values = types.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return values.Length == 0 ? null : values;
        }

        if (query.TryGetProperty("memoryType", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var value = type.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : [value];
        }

        return null;
    }

    private sealed record EvalProfile(string Name, Dictionary<string, object?>? Route);

    private static List<EvalProfile> SelectProfiles(string? requested, int topK)
    {
        var all = new Dictionary<string, EvalProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["baseline"] = new("baseline", null),
            ["memory-light"] = new("memory-light", Route(topK, mode: "memory_light")),
            ["memory-medium"] = new("memory-medium", Route(topK, mode: "memory_medium")),
            ["heavy-rag"] = new("heavy-rag", Route(topK, mode: "heavy_rag")),
            ["graph-rag"] = new("graph-rag", Route(topK, mode: "graph_rag")),
            ["no-reranker"] = new("no-reranker", Route(topK, useReranker: false)),
            ["no-graph"] = new("no-graph", Route(topK, useGraph: false)),
            ["bm25-only"] = new("bm25-only", Route(topK, useVectorSearch: false, useGraph: false, useReranker: false, useQueryExpansion: false, useImageSearch: false)),
            ["vector-only"] = new("vector-only", Route(topK, useBm25Search: false, useGraph: false, useReranker: false, useQueryExpansion: false, useImageSearch: false)),
        };

        if (string.IsNullOrWhiteSpace(requested))
        {
            return new[] { "baseline", "memory-light", "memory-medium", "heavy-rag", "graph-rag", "no-reranker", "no-graph" }
                .Select(name => all[name])
                .ToList();
        }

        var selected = new List<EvalProfile>();
        foreach (var name in requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!all.TryGetValue(name, out var profile))
            {
                throw new ArgumentException($"Unknown profile '{name}'. Known profiles: {string.Join(", ", all.Keys)}");
            }
            selected.Add(profile);
        }
        return selected;
    }

    private static Dictionary<string, object?> Route(
        int topK,
        string? mode = null,
        bool? useVectorSearch = null,
        bool? useBm25Search = null,
        bool? useGraph = null,
        bool? useReranker = null,
        bool? useQueryExpansion = null,
        bool? useImageSearch = null)
    {
        var route = new Dictionary<string, object?>
        {
            ["maxResults"] = topK,
            ["routerModel"] = "memory-eval-sweep",
        };
        if (mode is not null) route["mode"] = mode;
        if (useVectorSearch.HasValue) route["useVectorSearch"] = useVectorSearch.Value;
        if (useBm25Search.HasValue) route["useBm25Search"] = useBm25Search.Value;
        if (useGraph.HasValue) route["useGraph"] = useGraph.Value;
        if (useReranker.HasValue) route["useReranker"] = useReranker.Value;
        if (useQueryExpansion.HasValue) route["useQueryExpansion"] = useQueryExpansion.Value;
        if (useImageSearch.HasValue) route["useImageSearch"] = useImageSearch.Value;
        return route;
    }

    private static string BuildSweepMarkdown(IReadOnlyList<Dictionary<string, object?>> rows, int topK)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Memory eval sweep");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine($"| Profile | R@1 | R@3 | R@5 | R@{topK} | MRR | Abstain | Not found | Mean ms | P95 ms |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            sb.AppendLine(
                $"| {row["profile"]} | {Pct(row["recallAt1"])} | {Pct(row["recallAt3"])} | {Pct(row["recallAt5"])} | {Pct(row["recallAtK"])} | {Num(row["mrr"])} | {row["abstained"]} | {row["notFound"]} | {row["meanLatencyMs"]:F0} | {row["p95LatencyMs"]} |");
        }
        return sb.ToString();

        static string Pct(object? value) => value is double d ? d.ToString("P1") : "";
        static string Num(object? value) => value is double d ? d.ToString("F4") : "";
    }

    private static (string Name, double Actual, string Op, double Threshold, bool Passed) BuildMinCheck(
        string property,
        JsonElement profile,
        Dictionary<string, string> opts,
        string flag,
        double defaultValue)
    {
        var threshold = ReadThreshold(opts, flag, defaultValue);
        var actual = ReadMetric(profile, property);
        return (property, actual, ">=", threshold, double.IsNaN(threshold) || actual >= threshold);
    }

    private static (string Name, double Actual, string Op, double Threshold, bool Passed) BuildMaxCheck(
        string property,
        JsonElement profile,
        Dictionary<string, string> opts,
        string flag,
        double defaultValue)
    {
        var threshold = ReadThreshold(opts, flag, defaultValue);
        var actual = ReadMetric(profile, property);
        return (property, actual, "<=", threshold, double.IsNaN(threshold) || actual <= threshold);
    }

    private static double ReadThreshold(Dictionary<string, string> opts, string flag, double defaultValue)
    {
        if (!opts.TryGetValue(flag, out var raw))
        {
            return defaultValue;
        }

        if (string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return double.NaN;
        }

        return double.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static double ReadMetric(JsonElement profile, string property)
    {
        if (!profile.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            _ => 0,
        };
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 64;
    }

    private static (string apiUrl, string? apiKey, Dictionary<string, string> opts) ParseFlags(string[] args)
    {
        var url = Environment.GetEnvironmentVariable("MEMORY_API_URL") ?? "http://localhost:5566";
        var key = Environment.GetEnvironmentVariable("MEMORY_API_KEY");
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var k = args[i];
            if (k == "--api-url" && i + 1 < args.Length) { url = args[++i]; }
            else if (k == "--api-key" && i + 1 < args.Length) { key = args[++i]; }
            else if (k.StartsWith("--"))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    opts[k] = args[++i];
                }
                else
                {
                    opts[k] = "true";
                }
            }
        }
        return (url, key, opts);
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            memory eval — retrieval evaluation harness

            Sub-commands:
              gen-queries  Sample N recent notes; LLM writes one query per note. Saves to file.
              run          Replay queries through /api/search and report Recall@K + MRR.
              sweep        Run several built-in retrieval profiles against one stable query file.
              gate         Fail fast when a sweep summary misses quality/latency thresholds.

            Flags (both sub-commands):
              --api-url <url>       Memory.Api base (default http://localhost:5566 or MEMORY_API_URL)
              --api-key <bearer>    bearer token (or MEMORY_API_KEY)

            gen-queries flags:
              --count <N>           how many notes to sample (default 30)
              --out <path>          output JSON path (default eval-queries.json)

            run flags:
              --in <path>           queries JSON path (default eval-queries.json)
              --out <path>          write aggregate/per-query JSON result
              --top-k <K>           how deep to search before counting "not found" (default 10)
              --mode <mode>         route mode: memory_light, memory_medium, heavy_rag, graph_rag
              --no-reranker         disable reranker for ablation
              --no-graph            disable graph retrieval for ablation
              --no-expansion        disable query expansion for ablation
              --no-vector           disable vector retrieval for ablation
              --no-bm25             disable BM25 retrieval for ablation
              --*-weight <N>        vector/bm25/graph/image stream weight override

            sweep flags:
              --in <path>           queries JSON path (default eval-queries.json)
              --out-dir <dir>       output directory (default eval-results)
              --top-k <K>           how deep to search before counting "not found" (default 10)
              --profiles <csv>      optional profile list; known profiles:
                                     baseline,memory-light,memory-medium,heavy-rag,graph-rag,
                                     no-reranker,no-graph,bm25-only,vector-only

            gate flags:
              --summary <path>      sweep summary JSON (default eval-results/summary.json)
              --profile <name>      profile to gate (default memory-light)
              --min-recall-at-3 N   default 0.95
              --min-mrr N           default 0.90
              --max-p95-ms N        default 1000
              --max-abstained N     default 0
              --max-not-found N     default 0
              Use value 'off' to disable an individual threshold.

            Typical loop:
              memory eval gen-queries --count 30   # ~30s, one shot — keep this file stable
              memory eval run --out baseline.json
              # … tweak Reranker/GraphRetrieval/QueryExpansion via env vars …
              memory eval run --mode graph_rag --out graph.json
              memory eval sweep --in eval-queries.json --out-dir eval-results
              memory eval gate --summary eval-results/summary.json --profile memory-light
            """);
}
