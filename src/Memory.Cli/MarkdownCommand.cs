using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Memory.Cli;

/// <summary>
/// Markdown round-trip — export notes as Obsidian-compatible files (YAML
/// frontmatter + body) for portability or backup, or import a folder of
/// markdown files as fresh episodes through the normal ingestion pipeline.
///
/// Export shape (per note, one file):
///   ---
///   id: <guid>
///   kind: Decision|Learning|Error|Pattern|Observation|General
///   created: 2026-05-05T10:23:00Z
///   tags: [tag1, tag2]
///   keywords: [k1, k2, k3]
///   context: short context description
///   ---
///   &lt;note body&gt;
///
/// Import: each .md file's body becomes the content of one episode; the LLM
/// re-extracts notes/entities/relationships per the standard pipeline. The
/// frontmatter on imported files is informational only (we don't bypass
/// extraction with it — that would skip embedding + linking + bi-temporal).
/// </summary>
internal static class MarkdownCommand
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
            "export" => await ExportAsync(args[1..]).ConfigureAwait(false),
            "import" => await ImportAsync(args[1..]).ConfigureAwait(false),
            _ => Fail($"Unknown md sub-command '{args[0]}'. See 'memory md help'."),
        };
    }

    private static async Task<int> ExportAsync(string[] args)
    {
        var (apiUrl, apiKey, opts) = ParseFlags(args);
        if (apiKey is null) return Fail("--api-key (or MEMORY_API_KEY) required.");

        var outDir = opts.GetValueOrDefault("--out-dir", "./memory-export");
        var limit = opts.TryGetValue("--limit", out var l) && int.TryParse(l, out var lim) ? lim : 2000;

        Directory.CreateDirectory(outDir);

        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var notes = await http.GetFromJsonAsync<JsonElement[]>($"/api/notes?limit={limit}").ConfigureAwait(false)
            ?? Array.Empty<JsonElement>();

        var written = 0;
        foreach (var n in notes)
        {
            var id = n.GetProperty("id").GetGuid();
            var content = n.GetProperty("content").GetString() ?? "";
            var context = n.GetProperty("contextDescription").GetString() ?? "";
            var kind = n.GetProperty("kind").GetString() ?? "General";
            var created = n.GetProperty("createdAt").GetDateTimeOffset();
            var tags = n.TryGetProperty("tags", out var tagsEl)
                ? tagsEl.EnumerateArray().Select(t => t.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                : Array.Empty<string>();
            var keywords = n.TryGetProperty("keywords", out var kwEl)
                ? kwEl.EnumerateArray().Select(t => t.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                : Array.Empty<string>();

            var slug = Slugify(content);
            var filename = $"{created:yyyy-MM-dd}_{kind.ToLowerInvariant()}_{slug}_{id.ToString("N")[..8]}.md";
            var path = Path.Combine(outDir, filename);

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"id: {id}");
            sb.AppendLine($"kind: {kind}");
            sb.AppendLine($"created: {created:O}");
            if (tags.Length > 0) sb.AppendLine($"tags: [{string.Join(", ", tags)}]");
            if (keywords.Length > 0) sb.AppendLine($"keywords: [{string.Join(", ", keywords)}]");
            if (!string.IsNullOrEmpty(context)) sb.AppendLine($"context: {YamlEscape(context)}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(content);

            await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
            written++;
        }

        Console.WriteLine($"Wrote {written} notes → {Path.GetFullPath(outDir)}/");
        return 0;
    }

    private static async Task<int> ImportAsync(string[] args)
    {
        var (apiUrl, apiKey, opts) = ParseFlags(args);
        if (apiKey is null) return Fail("--api-key (or MEMORY_API_KEY) required.");

        var inDir = opts.GetValueOrDefault("--in-dir", "./memory-import");
        if (!Directory.Exists(inDir))
        {
            return Fail($"Directory not found: {inDir}");
        }

        var dryRun = opts.ContainsKey("--dry-run");

        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var files = Directory.GetFiles(inDir, "*.md", SearchOption.AllDirectories);
        Console.WriteLine($"Found {files.Length} .md files in {inDir}/");
        if (dryRun) Console.WriteLine("(dry-run — no API calls)");

        var ok = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var file in files)
        {
            var raw = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            var (front, body) = SplitFrontmatter(raw);
            if (string.IsNullOrWhiteSpace(body))
            {
                Console.WriteLine($"  skip (empty body)    {Path.GetFileName(file)}");
                skipped++;
                continue;
            }

            if (dryRun)
            {
                Console.WriteLine($"  dry  ({body.Length,5} chars) {Path.GetFileName(file)}");
                continue;
            }

            try
            {
                var resp = await http.PostAsJsonAsync("/api/episodes", new
                {
                    content = body,
                    source = $"md-import:{Path.GetFileName(file)}",
                }).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var skip = doc.RootElement.GetProperty("skipped").GetBoolean();
                if (skip)
                {
                    var reason = doc.RootElement.TryGetProperty("skipReason", out var r) ? r.GetString() : "filtered";
                    Console.WriteLine($"  skip ({reason})  {Path.GetFileName(file)}");
                    skipped++;
                }
                else
                {
                    var noteCount = doc.RootElement.GetProperty("noteIds").GetArrayLength();
                    Console.WriteLine($"  ok   ({noteCount} notes) {Path.GetFileName(file)}");
                    ok++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL {Path.GetFileName(file)}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Imported: {ok} ok, {skipped} skipped, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static (string? frontmatter, string body) SplitFrontmatter(string raw)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, raw.Trim());
        }
        var end = raw.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (null, raw.Trim());
        var front = raw[3..end].Trim();
        var body = raw[(end + 4)..].TrimStart('\r', '\n').Trim();
        return (front, body);
    }

    private static string Slugify(string content)
    {
        var first40 = content.Length > 40 ? content[..40] : content;
        var sb = new StringBuilder();
        foreach (var c in first40.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "note";
    }

    private static string YamlEscape(string value) =>
        value.Contains(':') || value.Contains('#') || value.Contains('\n')
            ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : value;

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
            else if (k == "--dry-run") { opts[k] = "true"; }
            else if (k.StartsWith("--") && i + 1 < args.Length) { opts[k] = args[++i]; }
        }
        return (url, key, opts);
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            memory md — markdown import/export

            Sub-commands:
              export   Write each active note as an .md file with YAML frontmatter.
              import   Read a folder of .md files; each becomes one episode through the
                       normal extraction + embedding + linking pipeline. Frontmatter is
                       informational only — we don't skip extraction (that would skip
                       embeddings, A-MEM linking, bi-temporal supersession, etc).

            Flags (both):
              --api-url <url>      Memory.Api base (default http://localhost:5566 or MEMORY_API_URL)
              --api-key <bearer>   bearer token (or MEMORY_API_KEY)

            export flags:
              --out-dir <dir>      output directory (default ./memory-export)
              --limit <N>          max notes to export (default 2000)

            import flags:
              --in-dir <dir>       input directory (default ./memory-import)
              --dry-run            list files but don't POST anything

            Examples:
              memory md export --out-dir ./obsidian-vault
              memory md import --in-dir ./obsidian-vault --dry-run
              memory md import --in-dir ./obsidian-vault
            """);
}
