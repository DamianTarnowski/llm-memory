using Memory.Api;
using Memory.Domain;
using Memory.Llm;
using Memory.Mcp;
using Memory.Pipeline;
using Memory.Secrets;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Secret-source chain — order goes fallback → primary; later providers win the merge.
// Each connector is opt-in: skip the call to drop a layer entirely. Each is also
// optional internally — when its source is unconfigured/unreachable it contributes
// no keys and the chain falls through to the previous provider.
builder.Configuration
    .AddSecretsJsonFile("appsettings.Local.json")     // baseline: local file (gitignored)
    .AddSecretsOpenBao()                               // secondary: opt-in via MEMORY_BAO_ADDR + token / AppRole
    .AddSecretsAzureKeyVault();                        // primary: opt-in via MEMORY_KV_URI + DefaultAzureCredential

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

// OpenBao admin client (optional — null when MEMORY_BAO_ADDR / token are unset).
// /api/secrets/* endpoints + the Memory.Web admin UI use this. Wrapped in a tiny
// holder so DI can express "client may be null" without bumping into nullable-
// reference-type / class-constraint complaints on AddSingleton.
builder.Services.AddSingleton(new Memory.Api.VaultClientHolder(VaultClientFactory.FromEnvironment()));

// Markdown folder watcher — first connector. Scans a configured folder for *.md
// files and ingests them. Disabled unless MarkdownConnector:Enabled=true.
builder.Services.AddOptions<Memory.Api.MarkdownConnectorOptions>()
    .Bind(builder.Configuration.GetSection(Memory.Api.MarkdownConnectorOptions.SectionName));
builder.Services.AddHostedService<Memory.Api.MarkdownFolderWatcher>();

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
        .Select(n => new { id = n.Id.Value, content = n.Content, contextDescription = n.ContextDescription, keywords = n.Keywords, tags = n.Tags, kind = n.Kind.ToString(), createdAt = n.CreatedAt })
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

// Streaming chat: search memory + stream LLM answer as SSE.
// Frames: data: {"delta":"..."}\n\n  ... data: [DONE]\n\n
app.MapPost("/api/chat", async (
    Memory.Api.ChatRequestBody body,
    ISearchPipeline pipeline,
    ILlmGateway llm,
    HttpContext context,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Query))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("query is required", ct);
        return;
    }

    var maxHits = Math.Clamp(body.MaxHits ?? 5, 1, 30);
    var contextBudget = Math.Clamp(body.MaxContextTokens ?? 2000, 200, 8000);

    var search = await pipeline.SearchAsync(
        new SearchRequest(body.Query, maxHits, MaxTokens: contextBudget), ct).ConfigureAwait(false);

    var contextLines = search.Abstain
        ? new List<string>()
        : search.Hits.Select((h, i) => $"[{i + 1}] {h.Content}").ToList();
    var contextStr = contextLines.Count > 0
        ? string.Join("\n", contextLines)
        : "(no relevant memory found)";

    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering

    // Lead frame: tell the client what context was found before the LLM starts.
    var meta = System.Text.Json.JsonSerializer.Serialize(new
    {
        type = "context",
        hits = search.Hits.Count,
        candidates = search.TotalCandidates,
        abstain = search.Abstain,
        abstainReason = search.AbstainReason,
    });
    await context.Response.WriteAsync($"data: {meta}\n\n", ct).ConfigureAwait(false);
    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

    const string sysPrompt = """
        You are a helpful assistant with access to the user's personal memory store. The
        Context below is the result of a hybrid search (vector + BM25 + graph PPR). Use it
        as your source of truth. If the context is empty or doesn't actually answer the
        question, say so plainly — don't fabricate.

        Be concise. Cite hits inline as [1], [2] when you use them so the user can trace
        what you relied on. Match the user's tone and language.
        """;

    var messages = new List<Microsoft.Extensions.AI.ChatMessage>
    {
        new(Microsoft.Extensions.AI.ChatRole.System, sysPrompt),
        new(Microsoft.Extensions.AI.ChatRole.User, $"Context:\n{contextStr}\n\nQuestion: {body.Query}"),
    };

    try
    {
        await foreach (var update in llm.GetChat().GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            if (string.IsNullOrEmpty(update.Text)) continue;
            var frame = System.Text.Json.JsonSerializer.Serialize(new { type = "delta", delta = update.Text });
            await context.Response.WriteAsync($"data: {frame}\n\n", ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
    }
    catch (Exception ex)
    {
        var errFrame = System.Text.Json.JsonSerializer.Serialize(new { type = "error", message = ex.Message });
        await context.Response.WriteAsync($"data: {errFrame}\n\n", ct).ConfigureAwait(false);
    }

    await context.Response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
});

app.MapPost("/api/episodes", async (
    IIngestionPipeline pipeline,
    IImageDescriber describer,
    Memory.Api.IngestEpisodeRequest body,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Content) && (body.Images is null || body.Images.Count == 0))
    {
        return Results.BadRequest(new { error = "content or at least one image is required" });
    }

    var content = body.Content ?? string.Empty;

    // Multimodal — describe each image and prepend to the content. The captioner
    // produces dense, retrievable text; the rest of the pipeline (extractor,
    // embedder, A-MEM linker, search) treats the captioned image as plain text.
    if (body.Images is { Count: > 0 } images)
    {
        var captions = new System.Text.StringBuilder();
        foreach (var img in images)
        {
            if (string.IsNullOrEmpty(img.Data)) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(img.Data); }
            catch (FormatException) { continue; }
            var mime = string.IsNullOrEmpty(img.MimeType) ? "image/png" : img.MimeType;
            try
            {
                var caption = await describer.DescribeAsync(bytes, mime, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    captions.AppendLine($"[image: {(string.IsNullOrEmpty(img.Caption) ? mime : img.Caption)}]");
                    captions.AppendLine(caption);
                    captions.AppendLine();
                }
            }
            catch { /* skip non-vision-capable provider failures */ }
        }
        if (captions.Length > 0)
        {
            content = captions.ToString() + (string.IsNullOrEmpty(content) ? "" : "\n" + content);
        }
    }

    var result = await pipeline.IngestAsync(new IngestionRequest(
        Source: body.Source ?? "api",
        Content: content,
        OccurredAt: body.OccurredAt,
        Metadata: body.Metadata),
        ct);
    return Results.Ok(new
    {
        episodeId = result.EpisodeId?.Value,
        noteIds = result.Notes.Select(n => n.Value).ToArray(),
        entityIds = result.EntitiesUpserted.Select(e => e.Value).ToArray(),
        skipped = result.Skipped,
        skipReason = result.SkipReason,
        importanceScore = result.ImportanceScore,
    });
});

app.MapPost("/api/search", async (ISearchPipeline pipeline, SearchPostBody body, CancellationToken ct) =>
{
    IReadOnlyList<NoteKind>? kinds = null;
    if (body.Kinds is { Count: > 0 })
    {
        kinds = body.Kinds
            .Select(k => Enum.TryParse<NoteKind>(k, ignoreCase: true, out var v) ? (NoteKind?)v : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
    }
    var result = await pipeline.SearchAsync(new SearchRequest(body.Query, body.MaxResults ?? 20, body.Tags, body.Since, body.Until, kinds, body.MaxTokens), ct);
    return Results.Ok(new
    {
        totalCandidates = result.TotalCandidates,
        abstain = result.Abstain,
        abstainReason = result.AbstainReason,
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

// --- /api/secrets/* — admin proxy onto OpenBao ---------------------------
//   Memory.Web's /secrets page hits these instead of talking to Vault directly,
//   so the root token never leaves the API process. All endpoints return 503
//   when no MEMORY_BAO_* env vars are configured.

app.MapGet("/api/secrets/status", (Memory.Api.VaultClientHolder vaultHolder) => Results.Ok(new
{
    configured = vaultHolder.Client is not null,
    address = VaultClientFactory.Address,
    mount = VaultClientFactory.KvMount,
}));

app.MapGet("/api/secrets/paths", async (Memory.Api.VaultClientHolder vaultHolder, string? folder, CancellationToken ct) =>
{
    var vault = vaultHolder.Client;
    if (vault is null) return Results.StatusCode(503);
    try
    {
        var listing = await vault.V1.Secrets.KeyValue.V2.ReadSecretPathsAsync(
            path: string.IsNullOrEmpty(folder) ? "" : folder,
            mountPoint: VaultClientFactory.KvMount).ConfigureAwait(false);
        return Results.Ok(new { paths = listing?.Data?.Keys?.ToArray() ?? Array.Empty<string>() });
    }
    catch (VaultSharp.Core.VaultApiException ex) when ((int)ex.HttpStatusCode == 404)
    {
        return Results.Ok(new { paths = Array.Empty<string>() });
    }
});

app.MapGet("/api/secrets/data", async (Memory.Api.VaultClientHolder vaultHolder, string path, CancellationToken ct) =>
{
    var vault = vaultHolder.Client;
    if (vault is null) return Results.StatusCode(503);
    try
    {
        var read = await vault.V1.Secrets.KeyValue.V2.ReadSecretAsync(
            path: path, mountPoint: VaultClientFactory.KvMount).ConfigureAwait(false);
        var data = read.Data?.Data ?? new Dictionary<string, object>();
        return Results.Ok(new
        {
            path,
            keys = data.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""),
            version = read.Data?.Metadata?.Version ?? 0,
            createdAt = read.Data?.Metadata?.CreatedTime,
        });
    }
    catch (VaultSharp.Core.VaultApiException ex) when ((int)ex.HttpStatusCode == 404)
    {
        return Results.Ok(new { path, keys = new Dictionary<string, string>(), version = 0, createdAt = (DateTimeOffset?)null });
    }
});

app.MapPut("/api/secrets/data", async (Memory.Api.VaultClientHolder vaultHolder, SecretDataPostBody body, CancellationToken ct) =>
{
    var vault = vaultHolder.Client;
    if (vault is null) return Results.StatusCode(503);
    var data = body.Keys?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value) ?? new Dictionary<string, object?>();
    var written = await vault.V1.Secrets.KeyValue.V2.WriteSecretAsync(
        path: body.Path,
        data: data,
        mountPoint: VaultClientFactory.KvMount).ConfigureAwait(false);
    return Results.Ok(new { path = body.Path, version = written?.Data?.Version ?? 0 });
});

app.MapDelete("/api/secrets/data", async (Memory.Api.VaultClientHolder vaultHolder, string path, CancellationToken ct) =>
{
    var vault = vaultHolder.Client;
    if (vault is null) return Results.StatusCode(503);
    await vault.V1.Secrets.KeyValue.V2.DeleteMetadataAsync(
        path: path, mountPoint: VaultClientFactory.KvMount).ConfigureAwait(false);
    return Results.NoContent();
});

// --- /api/eval/* — retrieval evaluation harness -------------------------
//   gen-queries: for each note, LLM generates one realistic question the note
//   answers. The note's id is the gold answer. run: pipeline.search per
//   question, measure rank of the gold note in results, aggregate to
//   Recall@K + MRR. Used to compare ablations across pipeline configs.

app.MapPost("/api/eval/queries", async (
    Memory.Api.EvalQueriesRequest req,
    MemoryDbContext db,
    ILlmGateway llm,
    CancellationToken ct) =>
{
    var count = Math.Clamp(req.Count ?? 30, 1, 200);
    var notes = await db.Notes
        .Where(n => n.SupersededAt == null)
        .OrderByDescending(n => n.CreatedAt)
        .Take(count)
        .Select(n => new { n.Id, n.Content, n.ContextDescription })
        .ToListAsync(ct);

    const string sysPrompt = """
        You are generating evaluation queries for a personal knowledge-base retriever.
        Given a single note, output ONE realistic, specific question that a user might
        type into a search box whose answer is contained in this note. The question
        should be standalone (not require additional context to make sense) and should
        NOT quote the note verbatim — paraphrase the topic.
        Output ONLY the question, no preamble, no quote marks, no trailing punctuation
        beyond a question mark.
        """;

    var results = new List<object>();
    var chat = llm.GetChat();
    foreach (var n in notes)
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, sysPrompt),
            new(Microsoft.Extensions.AI.ChatRole.User, n.Content),
        };
        try
        {
            var resp = await chat.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var query = (resp.Text ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(query)) continue;
            results.Add(new { noteId = n.Id.Value, query, contentPreview = n.Content[..Math.Min(120, n.Content.Length)] });
        }
        catch { /* skip notes the LLM rejects */ }
    }

    return Results.Ok(new { count = results.Count, queries = results });
});

app.MapPost("/api/eval/run", async (
    Memory.Api.EvalRunRequest req,
    ISearchPipeline pipeline,
    CancellationToken ct) =>
{
    if (req.Queries is null or { Count: 0 })
    {
        return Results.BadRequest(new { error = "queries[] is required" });
    }
    var topK = Math.Clamp(req.TopK ?? 10, 1, 50);

    var perQuery = new List<EvalPerQuery>(req.Queries.Count);
    foreach (var q in req.Queries)
    {
        var result = await pipeline.SearchAsync(new SearchRequest(q.Query, topK), ct).ConfigureAwait(false);
        var rank = -1;
        for (var i = 0; i < result.Hits.Count; i++)
        {
            if (result.Hits[i].NoteId.Value == q.NoteId)
            {
                rank = i + 1;
                break;
            }
        }
        perQuery.Add(new EvalPerQuery(q.NoteId, q.Query, rank, result.TotalCandidates));
    }

    int Recall(int k) => perQuery.Count(p => p.Rank > 0 && p.Rank <= k);
    var n = perQuery.Count;
    double Mrr() => n == 0 ? 0 : perQuery.Sum(p => p.Rank > 0 ? 1.0 / p.Rank : 0.0) / n;

    return Results.Ok(new
    {
        n,
        recallAt1 = n == 0 ? 0 : (double)Recall(1) / n,
        recallAt3 = n == 0 ? 0 : (double)Recall(3) / n,
        recallAt5 = n == 0 ? 0 : (double)Recall(5) / n,
        recallAt10 = n == 0 ? 0 : (double)Recall(Math.Min(10, topK)) / n,
        mrr = Mrr(),
        notFound = perQuery.Count(p => p.Rank < 0),
        perQuery,
    });
});

app.MapWebhooks();
app.MapMcp("/mcp");

app.Run();

public sealed record SearchPostBody(
    string Query,
    int? MaxResults,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    IReadOnlyList<string>? Kinds = null,
    int? MaxTokens = null);

public sealed record SecretDataPostBody(string Path, Dictionary<string, string>? Keys);
public sealed record EvalPerQuery(Guid NoteId, string Query, int Rank, int TotalCandidates);

namespace Memory.Api
{
    public sealed record EvalQueriesRequest(int? Count);
    public sealed record EvalRunRequest(IReadOnlyList<EvalQueryItem> Queries, int? TopK);
    public sealed record EvalQueryItem(Guid NoteId, string Query);

    public sealed record IngestEpisodeRequest(
        string Content,
        string? Source = null,
        DateTimeOffset? OccurredAt = null,
        Dictionary<string, string>? Metadata = null,
        IReadOnlyList<EpisodeImage>? Images = null);

    public sealed record EpisodeImage(string Data, string? MimeType = null, string? Caption = null);

    public sealed record ChatRequestBody(
        string Query,
        int? MaxHits = 5,
        int? MaxContextTokens = 2000);
}

public partial class Program;
