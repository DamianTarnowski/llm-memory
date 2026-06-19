using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Memory.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IMcpServerBuilder AddMemoryMcpTools(this IMcpServerBuilder builder)
    {
        builder.WithToolsFromAssembly(typeof(MemoryTools).Assembly);
        builder.WithResourcesFromAssembly(typeof(MemoryResources).Assembly);
        builder.WithPromptsFromAssembly(typeof(MemoryPrompts).Assembly);
        return builder;
    }
}
