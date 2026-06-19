using System.Threading.Channels;
using Memory.Domain;
using Memory.Llm;
using Memory.Pipeline.Linking;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Ingestion;

internal interface IEmbeddingBackfillQueue
{
    ValueTask EnqueueAsync(TenantScope scope, IReadOnlyList<NoteId> noteIds, CancellationToken ct = default);
}

internal sealed record EmbeddingBackfillItem(TenantScope Scope, NoteId NoteId, int Attempt = 0);

internal sealed class EmbeddingBackfillOptions
{
    public const string SectionName = "EmbeddingBackfill";
    public bool Enabled { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 15;
}

internal sealed class EmbeddingBackfillQueue : IEmbeddingBackfillQueue
{
    private readonly Channel<EmbeddingBackfillItem> _channel = Channel.CreateUnbounded<EmbeddingBackfillItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<EmbeddingBackfillItem> Reader => _channel.Reader;

    public async ValueTask EnqueueAsync(TenantScope scope, IReadOnlyList<NoteId> noteIds, CancellationToken ct = default)
    {
        foreach (var noteId in noteIds)
        {
            await _channel.Writer.WriteAsync(new EmbeddingBackfillItem(scope, noteId), ct).ConfigureAwait(false);
        }
    }

    public ValueTask EnqueueAsync(EmbeddingBackfillItem item, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(item, ct);
}

internal sealed class EmbeddingBackfillService(
    EmbeddingBackfillQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<EmbeddingBackfillOptions> options,
    ILogger<EmbeddingBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!options.Value.Enabled)
            {
                continue;
            }

            await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(EmbeddingBackfillItem item, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            using var tenantScope = tenant.BeginScope(item.Scope);

            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var exists = await db.NoteEmbeddings.AnyAsync(e => e.NoteId == item.NoteId, ct).ConfigureAwait(false);
            if (exists) return;

            var note = await db.Notes
                .Where(n => n.Id == item.NoteId && n.SupersededAt == null)
                .Select(n => new { n.Id, n.Project, n.Content })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (note is null) return;

            var llm = scope.ServiceProvider.GetRequiredService<ILlmGateway>();
            var llmOptions = scope.ServiceProvider.GetRequiredService<IOptions<LlmOptions>>().Value;
            var embedding = await llm.GetEmbeddings()
                .GenerateAsync(new[] { note.Content }, cancellationToken: ct)
                .ConfigureAwait(false);
            var vec = embedding[0].Vector.ToArray();

            db.NoteEmbeddings.Add(new NoteEmbedding
            {
                NoteId = note.Id,
                Project = note.Project,
                EmbeddingModel = llmOptions.EmbeddingModel,
                Dimensions = vec.Length,
                Embedding = vec,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var linker = scope.ServiceProvider.GetRequiredService<INoteLinker>();
            var linked = await linker.LinkRecentNoteAsync(note.Id, vec, ct).ConfigureAwait(false);
            if (linked > 0)
            {
                logger.LogInformation("Embedding backfill linked note {NoteId} to {Count} prior notes.", note.Id, linked);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var opts = options.Value;
            if (item.Attempt + 1 < Math.Max(1, opts.MaxAttempts))
            {
                logger.LogWarning(ex, "Embedding backfill failed for note {NoteId}; retrying attempt {Attempt}.", item.NoteId, item.Attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.RetryDelaySeconds)), ct).ConfigureAwait(false);
                await queue.EnqueueAsync(item with { Attempt = item.Attempt + 1 }, ct).ConfigureAwait(false);
                return;
            }

            logger.LogError(ex, "Embedding backfill failed permanently for note {NoteId}.", item.NoteId);
        }
    }
}
