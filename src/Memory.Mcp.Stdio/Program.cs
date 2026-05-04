using Memory.Domain;
using Memory.Llm;
using Memory.Mcp;
using Memory.Mcp.Stdio;
using Memory.Pipeline;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "MEMORY_");

builder.Services.AddOptions<StdioTenantOptions>()
    .Bind(builder.Configuration.GetSection(StdioTenantOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<ITenantContext>(sp =>
{
    var t = sp.GetRequiredService<IOptions<StdioTenantOptions>>().Value;
    if (t.OrganizationId == Guid.Empty || t.UserId == Guid.Empty || t.ProjectId == Guid.Empty)
    {
        throw new InvalidOperationException(
            "Tenant scope is not configured. Provide Tenant:OrganizationId, Tenant:UserId, Tenant:ProjectId via appsettings.Local.json or environment variables (e.g. MEMORY_Tenant__ProjectId=...).");
    }
    return new FixedTenantContext(new TenantScope(
        new OrganizationId(t.OrganizationId),
        new UserId(t.UserId),
        new ProjectId(t.ProjectId)));
});

builder.Services.AddMemoryStorage(builder.Configuration);
builder.Services.AddMemoryLlm(builder.Configuration);
builder.Services.AddMemoryPipeline(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .AddMemoryMcpTools();

await builder.Build().RunAsync();
