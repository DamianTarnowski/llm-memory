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

        logger.LogInformation(
            "Reflection background service starting: initial delay {InitialDelay}, interval {Interval}, max notes/run {Max}.",
            opts.InitialDelay, opts.Interval, opts.MaxNotesPerRun);

        try
        {
            await Task.Delay(opts.InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(opts, stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(opts.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(ReflectionScheduleOptions opts, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            if (tenant.Current is null)
            {
                logger.LogDebug("No active tenant scope; skipping reflection cycle.");
                return;
            }

            var pipeline = scope.ServiceProvider.GetRequiredService<IReflectionPipeline>();
            var result = await pipeline.ReflectAsync(
                new ReflectionRequest(opts.Scope, opts.MaxNotesPerRun),
                ct).ConfigureAwait(false);

            logger.LogInformation(
                "Scheduled reflection: project={ProjectId} notes_considered={Notes} reflection_id={ReflectionId}",
                tenant.Current.Project, result.NotesConsidered, result.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled reflection failed; will retry next interval.");
        }
    }
}
