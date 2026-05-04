using System.Text.Json;

namespace Memory.Pipeline.Tests;

internal sealed record LiveLlmCreds(
    string Endpoint,
    string ApiKey,
    string ChatDeployment,
    string EmbeddingDeployment)
{
    public static bool TryLoad(out LiveLlmCreds creds)
    {
        var endpoint = Environment.GetEnvironmentVariable("MEMORY_TEST_LLM_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("MEMORY_TEST_LLM_KEY");
        var chat = Environment.GetEnvironmentVariable("MEMORY_TEST_LLM_CHAT") ?? "gpt-5.5";
        var embed = Environment.GetEnvironmentVariable("MEMORY_TEST_LLM_EMBED") ?? "text-embedding-3-large";

        if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(key))
        {
            creds = new LiveLlmCreds(endpoint, key, chat, embed);
            return true;
        }

        // Fallback: read src/Memory.Mcp.Stdio/appsettings.Local.json (gitignored)
        // so test runs locally without any env-var setup.
        var path = LocateStdioAppsettings();
        if (path is null)
        {
            creds = null!;
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var azure = doc.RootElement.GetProperty("Llm").GetProperty("AzureOpenAi");
            var ep = azure.GetProperty("Endpoint").GetString();
            var ak = azure.GetProperty("ApiKey").GetString();
            var cd = azure.TryGetProperty("ChatDeployment", out var c) ? c.GetString() : chat;
            var ed = azure.TryGetProperty("EmbeddingDeployment", out var e) ? e.GetString() : embed;
            if (string.IsNullOrEmpty(ep) || string.IsNullOrEmpty(ak))
            {
                creds = null!;
                return false;
            }
            creds = new LiveLlmCreds(ep, ak, cd ?? chat, ed ?? embed);
            return true;
        }
        catch
        {
            creds = null!;
            return false;
        }
    }

    private static string? LocateStdioAppsettings()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Memory.Mcp.Stdio", "appsettings.Local.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
