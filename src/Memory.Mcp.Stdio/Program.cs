using Memory.Llm;
using Memory.Mcp;
using Memory.Mcp.Stdio;
using Memory.Pipeline;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "MEMORY_");

builder.Services.AddOptions<StdioTenantOptions>()
    .Bind(builder.Configuration.GetSection(StdioTenantOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddMemoryTenancy();
builder.Services.AddMemoryStorage(builder.Configuration);
builder.Services.AddMemoryLlm(builder.Configuration);
builder.Services.AddMemoryPipeline();

builder.Services.AddHostedService<TenantBootstrapper>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .AddMemoryMcpTools();

await builder.Build().RunAsync();
