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
        var topK = opts.TryGetValue("--top-k", out var tk) && int.TryParse(tk, out var k) ? k : 10;

        if (!File.Exists(inPath))
        {
            return Fail($"Queries file not found: {inPath}. Run 'memory eval gen-queries' first.");
        }

        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var raw = await File.ReadAllTextAsync(inPath).ConfigureAwait(false);
        using var queriesDoc = JsonDocument.Parse(raw);
        var queries = queriesDoc.RootElement.GetProperty("queries").EnumerateArray()
            .Select(q => new
            {
                noteId = q.GetProperty("noteId").GetGuid(),
                query = q.GetProperty("query").GetString() ?? "",
            })
            .ToList();

        Console.WriteLine($"Running {queries.Count} queries against {apiUrl}/api/search (top_k={topK})…");

        var resp = await http.PostAsJsonAsync("/api/eval/run", new { queries, topK }).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var resultJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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

        Console.WriteLine();
        Console.WriteLine("=== Per-query (rank of gold note in results) ===");
        Console.WriteLine($"  {"rank",4}  query");
        foreach (var p in root.GetProperty("perQuery").EnumerateArray())
        {
            var rank = p.GetProperty("rank").GetInt32();
            var query = p.GetProperty("query").GetString() ?? "";
            var rankStr = rank < 0 ? "  --" : $"{rank,4}";
            var preview = query.Length > 90 ? query[..90] + "…" : query;
            Console.WriteLine($"  {rankStr}  {preview}");
        }
        return 0;
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
            else if (k.StartsWith("--") && i + 1 < args.Length) { opts[k] = args[++i]; }
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

            Flags (both sub-commands):
              --api-url <url>       Memory.Api base (default http://localhost:5566 or MEMORY_API_URL)
              --api-key <bearer>    bearer token (or MEMORY_API_KEY)

            gen-queries flags:
              --count <N>           how many notes to sample (default 30)
              --out <path>          output JSON path (default eval-queries.json)

            run flags:
              --in <path>           queries JSON path (default eval-queries.json)
              --top-k <K>           how deep to search before counting "not found" (default 10)

            Typical loop:
              memory eval gen-queries --count 30   # ~30s, one shot — keep this file stable
              memory eval run                       # baseline
              # … tweak Reranker/GraphRetrieval/QueryExpansion via env vars …
              memory eval run                       # compare
            """);
}
