using Memory.Api;
using Memory.Domain;
using Memory.Llm;
using Memory.Mcp;
using Memory.Pipeline;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddOpenApi();

builder.Services.AddCors(opts => opts.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

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

app.UseCors();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseMiddleware<TenantHeaderMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    service = "Memory.Api",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
    docs = "/openapi/v1.json",
    mcp = "/mcp",
    auth = "Bearer <api-key>  OR  X-Memory-Org-Id / X-Memory-User-Id / X-Memory-Project-Id",
}));

app.MapGet("/api/episodes", async (MemoryDbContext db, int limit = 50, CancellationToken ct = default) =>
    await db.Episodes
        .OrderByDescending(e => e.IngestedAt)
        .Take(Math.Clamp(limit <= 0 ? 50 : limit, 1, 500))
        .Select(e => new { id = e.Id.Value, source = e.Source, content = e.Content, ingestedAt = e.IngestedAt, occurredAt = e.OccurredAt })
        .ToListAsync(ct));

app.MapGet("/api/notes", async (MemoryDbContext db, int limit = 50, CancellationToken ct = default) =>
    await db.Notes
        .OrderByDescending(n => n.CreatedAt)
        .Take(Math.Clamp(limit <= 0 ? 50 : limit, 1, 500))
        .Select(n => new { id = n.Id.Value, content = n.Content, contextDescription = n.ContextDescription, keywords = n.Keywords, tags = n.Tags, createdAt = n.CreatedAt })
        .ToListAsync(ct));

app.MapGet("/api/reflections", async (MemoryDbContext db, int limit = 50, CancellationToken ct = default) =>
    await db.Reflections
        .OrderByDescending(r => r.GeneratedAt)
        .Take(Math.Clamp(limit <= 0 ? 20 : limit, 1, 100))
        .Select(r => new { id = r.Id.Value, scope = r.Scope, summary = r.Summary, generatedAt = r.GeneratedAt, generatorModel = r.GeneratorModel })
        .ToListAsync(ct));

app.MapGet("/api/entities", async (IGraphContext graph, ITenantContext tenant, string? name = null, int limit = 50, CancellationToken ct = default) =>
{
    var scope = tenant.Require();
    var entities = await graph.GetEntitiesAsync(
        scope.Project,
        nameFilter: name,
        limit: Math.Clamp(limit <= 0 ? 50 : limit, 1, 200),
        ct);
    return entities.Select(e => new
    {
        id = e.Id.Value,
        name = e.Name,
        kind = e.Kind,
        attributes = e.Attributes,
        firstSeenAt = e.FirstSeenAt,
        lastSeenAt = e.LastSeenAt,
    });
});

app.MapPost("/api/search", async (ISearchPipeline pipeline, SearchPostBody body, CancellationToken ct) =>
{
    var result = await pipeline.SearchAsync(new SearchRequest(body.Query, body.MaxResults ?? 20), ct);
    return Results.Ok(new
    {
        totalCandidates = result.TotalCandidates,
        hits = result.Hits.Select(h => new
        {
            noteId = h.NoteId.Value,
            content = h.Content,
            score = h.Score,
            relatedEntityIds = h.RelatedEntities.Select(e => e.Value).ToArray(),
        }),
    });
});

app.MapMcp("/mcp");

app.Run();

public sealed record SearchPostBody(string Query, int? MaxResults);

public partial class Program;
