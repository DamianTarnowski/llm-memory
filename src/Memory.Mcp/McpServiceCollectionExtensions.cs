using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Memory.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IMcpServerBuilder AddMemoryMcpTools(this IMcpServerBuilder builder)
    {
        builder.WithToolsFromAssembly(typeof(MemoryTools).Assembly);
        return builder;
    }
}
