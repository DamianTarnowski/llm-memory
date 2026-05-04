namespace Memory.Pipeline.Tests;

/// <summary>
/// Marks a test that requires real LLM calls. Skips when MEMORY_LIVE_LLM_TESTS != "1"
/// or when no Llm:AzureOpenAi:ApiKey is found via env / appsettings.Local.json.
/// </summary>
public class LiveLlmFactAttribute : FactAttribute
{
    public LiveLlmFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEMORY_LIVE_LLM_TESTS") != "1")
        {
            Skip = "Set MEMORY_LIVE_LLM_TESTS=1 to run live LLM integration tests (uses real provider — costs money).";
            return;
        }

        if (!LiveLlmCreds.TryLoad(out _))
        {
            Skip = "No live LLM credentials found (set MEMORY_TEST_LLM_ENDPOINT/KEY env vars or fill in src/Memory.Mcp.Stdio/appsettings.Local.json).";
        }
    }
}
