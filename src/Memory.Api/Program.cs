using Memory.Llm;
using Memory.Mcp;
using Memory.Pipeline;
using Memory.Storage;
using Memory.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();

builder.Services.AddMemoryTenancy();
builder.Services.AddMemoryStorage(builder.Configuration);
builder.Services.AddMemoryLlm(builder.Configuration);
builder.Services.AddMemoryPipeline();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .AddMemoryMcpTools();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new
{
    service = "Memory.Api",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
    docs = "/openapi/v1.json"
}));

app.MapMcp("/mcp");

app.Run();

public partial class Program;
