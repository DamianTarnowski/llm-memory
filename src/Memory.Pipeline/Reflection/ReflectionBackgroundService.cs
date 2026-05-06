using Memory.Domain;
using Memory.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Reflection;

internal sealed class ReflectionBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<ReflectionScheduleOptions> options,
    ILogger<ReflectionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Reflection background service is disabled (set ReflectionSchedule:Enabled=true to turn on).");
            return;
        }

        if (opts.Tenants.Count == 0)
        {
            logger.LogWarning(
                "ReflectionSchedule:Enabled=true but no Tenants[] configured — nothing to reflect against. " +
                "Add ReflectionSchedule:Tenants:[{{Organization, User, Project}}, ...] to schedule per-project reflection.");
            return;
        }

        logger.LogInformation(
            "Reflection background service starting: initial delay {InitialDelay}, interval {Interval}, max notes/run {Max}, tenants {TenantCount}.",
            opts.InitialDelay, opts.Interval, opts.MaxNotesPerRun, opts.Tenants.Count);

        try
        {
            await Task.Delay(opts.InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var t in opts.Tenants)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await RunForTenantAsync(opts, t, stoppingToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(opts.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunForTenantAsync(ReflectionScheduleOptions opts, ReflectionTenant t, CancellationToken ct)
    {
        try
        {
            using var diScope = scopeFactory.CreateScope();
            var tenantCtx = diScope.ServiceProvider.GetRequiredService<ITenantContext>();
            using var tenantScope = tenantCtx.BeginScope(new TenantScope(
                new OrganizationId(t.Organization),
                new UserId(t.User),
                new ProjectId(t.Project)));

            var pipeline = diScope.ServiceProvider.GetRequiredService<IReflectionPipeline>();
            var result = await pipeline.ReflectAsync(
                new ReflectionRequest(opts.Scope, opts.MaxNotesPerRun),
                ct).ConfigureAwait(false);

            logger.LogInformation(
                "Scheduled reflection: project={ProjectId} notes_considered={Notes} reflection_id={ReflectionId}",
                t.Project, result.NotesConsidered, result.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled reflection failed for project={ProjectId}; will retry next interval.", t.Project);
        }
    }
}
