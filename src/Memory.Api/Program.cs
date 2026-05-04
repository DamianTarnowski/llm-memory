using Memory.Api;
using Memory.Llm;
using Memory.Mcp;
using Memory.Pipeline;
using Memory.Storage;
using Memory.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddOpenApi();

builder.Services.AddMemoryTenancy();
builder.Services.AddMemoryStorage(builder.Configuration);
builder.Services.AddMemoryLlm(builder.Configuration);
builder.Services.AddMemoryPipeline(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .AddMemoryMcpTools();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapOpenApi();

app.UseMiddleware<TenantHeaderMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    service = "Memory.Api",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
    docs = "/openapi/v1.json",
    mcp = "/mcp",
    tenantHeaders = new[]
    {
        TenantHeaderMiddleware.OrgHeader,
        TenantHeaderMiddleware.UserHeader,
        TenantHeaderMiddleware.ProjectHeader,
    },
}));

app.MapMcp("/mcp");

app.Run();

public partial class Program;
