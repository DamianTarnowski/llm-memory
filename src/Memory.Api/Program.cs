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
    health = "/api/health",
    auth = "Bearer <api-key>  OR  X-Memory-Org-Id / X-Memory-User-Id / X-Memory-Project-Id",
}));

app.MapGet("/api/health", async (MemoryDbContext db, ILlmGateway llm, CancellationToken ct) =>
{
    var dbStatus = "ok";
    var dbLatencyMs = 0L;
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
        sw.Stop();
        dbLatencyMs = sw.ElapsedMilliseconds;
    }
    catch (Exception ex)
    {
        dbStatus = $"fail: {ex.GetType().Name}: {ex.Message}";
    }

    string llmStatus;
    try
    {
        var chat = llm.GetChat();
        llmStatus = chat is null ? "fail: gateway returned null" : "ok";
    }
    catch (Exception ex)
    {
        llmStatus = $"unconfigured: {ex.Message[..Math.Min(120, ex.Message.Length)]}";
    }

    var allOk = dbStatus == "ok" && llmStatus == "ok";
    var payload = new
    {
        status = allOk ? "healthy" : "degraded",
        timestamp = DateTimeOffset.UtcNow,
        db = new { status = dbStatus, latencyMs = dbLatencyMs },
        llm = new { status = llmStatus },
    };
    return allOk ? Results.Ok(payload) : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
});

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

app.MapGet("/api/edges", async (IGraphContext graph, ITenantContext tenant, int limit = 200, CancellationToken ct = default) =>
{
    var scope = tenant.Require();
    var edges = await graph.GetEdgesAsync(scope.Project, ct: ct);
    return edges.Take(Math.Clamp(limit, 1, 1000)).Select(e => new
    {
        id = e.Id.Value,
        from = e.From.Value,
        to = e.To.Value,
        relation = e.Relation,
        recordedAt = e.RecordedAt,
        invalidatedAt = e.InvalidatedAt,
    });
});

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
    var result = await pipeline.SearchAsync(new SearchRequest(body.Query, body.MaxResults ?? 20, body.Tags, body.Since, body.Until), ct);
    return Results.Ok(new
    {
        totalCandidates = result.TotalCandidates,
        hits = result.Hits.Select(h => new
        {
            noteId = h.NoteId.Value,
            content = h.Content,
            score = h.Score,
            relatedEntityIds = h.RelatedEntities.Select(e => e.Value).ToArray(),
            provenance = h.Provenance is null ? null : new
            {
                fromVector = h.Provenance.FromVector,
                fromBm25 = h.Provenance.FromBm25,
                fromGraph = h.Provenance.FromGraph,
                vectorScore = h.Provenance.VectorScore,
                bm25Score = h.Provenance.Bm25Score,
                graphScore = h.Provenance.GraphScore,
                rerankerScore = h.Provenance.RerankerScore,
            },
        }),
    });
});

app.MapMcp("/mcp");

app.Run();

public sealed record SearchPostBody(
    string Query,
    int? MaxResults,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

public partial class Program;
