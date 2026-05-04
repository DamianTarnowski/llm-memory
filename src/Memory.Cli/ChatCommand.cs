using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Memory.Cli;

internal static class ChatCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] is "help" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var (apiUrl, apiKey) = ParseFlags(args);
        if (string.IsNullOrEmpty(apiUrl))
        {
            apiUrl = Environment.GetEnvironmentVariable("MEMORY_API_URL") ?? "http://localhost:5566";
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("MEMORY_API_KEY")
                ?? throw new InvalidOperationException("--api-key or MEMORY_API_KEY required.");
        }

        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        Console.WriteLine($"""
            memory chat — connected to {apiUrl}
            Commands:
              <free text>            save the line as an episode
              /search <query>        hybrid search the project
              /reflect [scope]       generate a reflection (scope optional)
              /entity <name>         look up an entity + neighbours
              /related <note-id>     show notes linked to this one (A-MEM)
              /stats                 episode/note/entity counts
              /exit                  quit
            """);

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null || line.Trim() is "/exit" or "/quit") return 0;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                if (line.StartsWith("/search ", StringComparison.OrdinalIgnoreCase))
                {
                    await DoSearch(http, line["/search ".Length..]).ConfigureAwait(false);
                }
                else if (line.StartsWith("/reflect", StringComparison.OrdinalIgnoreCase))
                {
                    await DoReflect(http, line.Length > 8 ? line[9..].Trim() : "interactive").ConfigureAwait(false);
                }
                else if (line.StartsWith("/entity ", StringComparison.OrdinalIgnoreCase))
                {
                    await DoEntity(http, line["/entity ".Length..]).ConfigureAwait(false);
                }
                else if (line.StartsWith("/related ", StringComparison.OrdinalIgnoreCase))
                {
                    await DoRelated(http, line["/related ".Length..]).ConfigureAwait(false);
                }
                else if (line.Equals("/stats", StringComparison.OrdinalIgnoreCase))
                {
                    await DoStats(http).ConfigureAwait(false);
                }
                else
                {
                    Console.WriteLine("(no /save endpoint yet — use Mcp.Stdio's save_episode or call /api/episodes via the API)");
                    Console.WriteLine("(saving via REPL is on the v1 todo — for now use Claude Code with the MCP tool, or curl /mcp)");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  error: {ex.Message}");
            }
        }
    }

    private static async Task DoSearch(HttpClient http, string query)
    {
        var body = new { query, maxResults = 5 };
        var resp = await http.PostAsJsonAsync("/api/search", body).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var doc = JsonDocument.Parse(json);
        var hits = doc.RootElement.GetProperty("hits");
        var total = doc.RootElement.GetProperty("totalCandidates").GetInt32();
        Console.WriteLine($"  total candidates: {total}");
        foreach (var h in hits.EnumerateArray())
        {
            var s = h.GetProperty("score").GetDouble();
            var c = h.GetProperty("content").GetString();
            var truncated = (c ?? "").Length > 120 ? c![..120] + "…" : c ?? "";
            Console.WriteLine($"  [{s:0.000}] {truncated}");
        }
    }

    private static async Task DoReflect(HttpClient http, string scope)
    {
        // No /api/reflect-now endpoint yet — call MCP HTTP /mcp/tools/call instead
        var body = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "reflect", arguments = new { scope, maxNotes = 30 } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var resp = await http.SendAsync(req).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var dataLine = raw.Split('\n').FirstOrDefault(l => l.StartsWith("data:"));
        if (dataLine is null) { Console.WriteLine($"  (no data) {raw[..Math.Min(200, raw.Length)]}"); return; }
        var data = JsonDocument.Parse(dataLine[5..].Trim());
        var summary = JsonDocument.Parse(data.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Console.WriteLine($"  notes considered: {summary.RootElement.GetProperty("notesConsidered").GetInt32()}");
        Console.WriteLine($"  summary:\n{summary.RootElement.GetProperty("summary").GetString()}");
    }

    private static async Task DoEntity(HttpClient http, string name)
    {
        var resp = await http.GetAsync($"/api/entities?name={Uri.EscapeDataString(name)}&limit=1").ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        Console.WriteLine($"  {json}");
    }

    private static async Task DoRelated(HttpClient http, string noteId)
    {
        // No dedicated REST endpoint — invoke MCP find_related_notes
        var body = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "find_related_notes", arguments = new { noteId, maxResults = 10 } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var resp = await http.SendAsync(req).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var dataLine = raw.Split('\n').FirstOrDefault(l => l.StartsWith("data:"));
        if (dataLine is null) { Console.WriteLine($"  (no data) {raw[..Math.Min(200, raw.Length)]}"); return; }
        var data = JsonDocument.Parse(dataLine[5..].Trim());
        var payload = JsonDocument.Parse(data.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        var rels = payload.RootElement.GetProperty("relations");
        Console.WriteLine($"  {rels.GetArrayLength()} relations:");
        foreach (var r in rels.EnumerateArray())
        {
            var rt = r.GetProperty("relationType").GetString();
            var conf = r.GetProperty("confidence").GetDouble();
            var sim = r.GetProperty("similarity").GetDouble();
            Console.WriteLine($"    [{rt} conf={conf:0.00} sim={sim:0.00}]");
        }
    }

    private static async Task DoStats(HttpClient http)
    {
        var episodes = (await http.GetFromJsonAsync<JsonElement[]>("/api/episodes?limit=500").ConfigureAwait(false))?.Length ?? 0;
        var notes = (await http.GetFromJsonAsync<JsonElement[]>("/api/notes?limit=500").ConfigureAwait(false))?.Length ?? 0;
        var entities = (await http.GetFromJsonAsync<JsonElement[]>("/api/entities?limit=500").ConfigureAwait(false))?.Length ?? 0;
        var reflections = (await http.GetFromJsonAsync<JsonElement[]>("/api/reflections?limit=200").ConfigureAwait(false))?.Length ?? 0;
        Console.WriteLine($"  episodes={episodes} notes={notes} entities={entities} reflections={reflections}");
    }

    private static (string? apiUrl, string? apiKey) ParseFlags(string[] args)
    {
        string? url = null;
        string? key = null;
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--api-url": url = args[i + 1]; break;
                case "--api-key": key = args[i + 1]; break;
            }
        }
        return (url, key);
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            memory chat — interactive REPL connected to a running Memory.Api.

            Required: --api-key (or MEMORY_API_KEY env var); generate via `memory api-key create`.
            Optional: --api-url (default http://localhost:5566 or MEMORY_API_URL).

            Examples:
              memory chat --api-url http://localhost:5566 --api-key memk_...
              MEMORY_API_KEY=memk_... memory chat

            Commands inside the REPL:
              /search <query>     hybrid search across project memory
              /reflect [scope]    LLM reflection over recent notes
              /entity <name>      fetch entity + neighbours
              /related <note-id>  list A-MEM linked notes
              /stats              counts of episodes/notes/entities/reflections
              /exit               quit
            """);
}
