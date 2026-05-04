namespace Memory.Cli;

internal static class HelpCommand
{
    public static void Print()
    {
        Console.WriteLine(
            """
            memory — LLM Memory CLI

            Commands:
              init       Seed a fresh organization / user / project tenant scope into a clean database
                            and emit the matching Tenant config block for Memory.Mcp.Stdio.
              api-key    Manage Memory.Api bearer-token API keys (create/list/revoke).

            Examples:
              memory init \
                --connection-string "Host=localhost;Database=llm_memory;Username=postgres;Password=postgres" \
                --org "MyOrg" \
                --user-email "me@example.com" \
                --user-name "Me" \
                --project "default" \
                --embedding-model "text-embedding-3-large"

            Defaults are applied when flags are omitted. The connection string falls back to env var MEMORY_CONNSTR.
            """);
    }

    public static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'memory help' for usage.");
        return 64;
    }
}
