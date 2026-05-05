using System.Text.Json;
using Memory.Domain;
using Memory.Pipeline;
using Memory.Tenancy;
using Microsoft.Extensions.Options;

namespace Memory.Api;

/// <summary>
/// First connector — periodically scans a configured folder for <c>*.md</c> files
/// and ingests any new or changed ones as episodes through the standard pipeline.
/// State (path → last-write-time) is persisted to a JSON sidecar so restarts
/// don't re-ingest the world.
///
/// Disabled by default. Tenant scope is required (the hosted service has no
/// per-request scope) and is configured under <c>MarkdownConnector</c>:
/// <code>
/// "MarkdownConnector": {
///   "Enabled": true,
///   "Path": "C:/Users/me/notes",
///   "OrganizationId": "…",
///   "UserId": "…",
///   "ProjectId": "…",
///   "PollIntervalSeconds": 60,
///   "StateFile": "C:/ProgramData/memory/md-watcher.json"
/// }
/// </code>
/// FileSystemWatcher would be lower-latency but is flaky over network / WSL
/// paths and emits multiple events per save; a 60-second poll is cheap, simple,
/// and matches how often a human would save .md notes anyway.
/// </summary>
public sealed class MarkdownFolderWatcher(
    IOptions<MarkdownConnectorOptions> options,
    IServiceProvider services,
    ILogger<MarkdownFolderWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("MarkdownFolderWatcher disabled.");
            return;
        }
        if (string.IsNullOrWhiteSpace(opts.Path) || !Directory.Exists(opts.Path))
        {
            logger.LogWarning("MarkdownFolderWatcher: path '{Path}' is missing or doesn't exist; service will idle.", opts.Path);
            return;
        }
        if (opts.OrganizationId == Guid.Empty || opts.UserId == Guid.Empty || opts.ProjectId == Guid.Empty)
        {
            logger.LogWarning("MarkdownFolderWatcher: tenant scope (OrganizationId / UserId / ProjectId) is incomplete; service will idle.");
            return;
        }

        var state = LoadState(opts.StateFile);
        logger.LogInformation("MarkdownFolderWatcher watching {Path} every {Interval}s ({Known} files known).",
            opts.Path, opts.PollIntervalSeconds, state.Count);

        var tenantScope = new TenantScope(
            new OrganizationId(opts.OrganizationId),
            new UserId(opts.UserId),
            new ProjectId(opts.ProjectId));

        var interval = TimeSpan.FromSeconds(Math.Max(5, opts.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(opts, state, tenantScope, stoppingToken).ConfigureAwait(false);
                SaveState(opts.StateFile, state);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MarkdownFolderWatcher scan failed; will retry next interval.");
            }

            try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task ScanOnceAsync(
        MarkdownConnectorOptions opts,
        Dictionary<string, long> state,
        TenantScope tenantScope,
        CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(opts.Path!, "*.md", SearchOption.AllDirectories).ToList();
        var ingested = 0;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            var lastWrite = File.GetLastWriteTimeUtc(file).Ticks;
            if (state.TryGetValue(file, out var prev) && prev == lastWrite) continue;

            string body;
            try { body = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false); }
            catch (IOException) { continue; /* still being written */ }

            var (front, content) = SplitFrontmatter(body);
            if (string.IsNullOrWhiteSpace(content))
            {
                state[file] = lastWrite;
                continue;
            }

            using var scope = services.CreateScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            using var _ = tenant.BeginScope(tenantScope);
            var pipeline = scope.ServiceProvider.GetRequiredService<IIngestionPipeline>();

            try
            {
                var result = await pipeline.IngestAsync(new IngestionRequest(
                    Source: $"md-watcher:{Path.GetFileName(file)}",
                    Content: content,
                    OccurredAt: null,
                    Metadata: new Dictionary<string, string> { ["watcher_path"] = file }
                ), ct).ConfigureAwait(false);

                if (result.Skipped)
                {
                    logger.LogInformation("md-watcher: skipped {File} (reason: {Reason}, score: {Score:F2})",
                        Path.GetFileName(file), result.SkipReason, result.ImportanceScore);
                }
                else
                {
                    logger.LogInformation("md-watcher: ingested {File} ({Notes} notes, {Entities} entities)",
                        Path.GetFileName(file), result.Notes.Count, result.EntitiesUpserted.Count);
                    ingested++;
                }
                state[file] = lastWrite;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "md-watcher: ingestion failed for {File}; will retry next scan.", file);
            }
        }

        if (ingested > 0)
        {
            logger.LogInformation("md-watcher: scan complete, {Ingested} files ingested this pass.", ingested);
        }
    }

    private static (string? frontmatter, string body) SplitFrontmatter(string raw)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, raw.Trim());
        }
        var end = raw.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (null, raw.Trim());
        var front = raw[3..end].Trim();
        var body = raw[(end + 4)..].TrimStart('\r', '\n').Trim();
        return (front, body);
    }

    private Dictionary<string, long> LoadState(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, long>>(json)
                ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveState(string? path, Dictionary<string, long> state)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "md-watcher: failed to persist state to {Path}; in-memory only.", path);
        }
    }
}

public sealed class MarkdownConnectorOptions
{
    public const string SectionName = "MarkdownConnector";

    public bool Enabled { get; set; } = false;
    public string? Path { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Sidecar file storing per-path last-write ticks. Defaults to the user temp dir.</summary>
    public string? StateFile { get; set; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "memory", "md-watcher-state.json");
}
