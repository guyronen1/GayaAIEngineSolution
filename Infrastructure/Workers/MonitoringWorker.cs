using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Workers;

/// <summary>
/// Drives the scan pipeline by claiming MonitoredJob leases from the DB and
/// running scans for each claimed job in parallel. Multiple instances can run
/// concurrently — the DB lease serialises per-job execution.
/// </summary>
public sealed class MonitoringWorker(
    IServiceScopeFactory      scopeFactory,
    ILogger<MonitoringWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private const int MaxParallelism = 4;
    private const int ClaimBatchSize = MaxParallelism;

    private readonly string _leasedBy =
        $"host={Environment.MachineName};pid={Environment.ProcessId};runId={Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MonitoringWorker started as {LeasedBy}", _leasedBy);

        // Startup drain — runs once before the polling loop to clear approvals or
        // auto-heals that accumulated while this process was down.
        await DrainPendingFixesAsync("startup", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<ClaimedJobLease> claimed;
                using (var scope = scopeFactory.CreateScope())
                {
                    var leases = scope.ServiceProvider.GetRequiredService<IMonitoredJobLeaseRepository>();
                    claimed = await leases.ClaimAsync(_leasedBy, ClaimBatchSize, stoppingToken);
                }

                if (claimed.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                logger.LogInformation("Claimed {Count} job(s) for scan", claimed.Count);

                await Parallel.ForEachAsync(
                    claimed,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxParallelism,
                        CancellationToken      = stoppingToken,
                    },
                    async (lease, ct) => await RunOneJobAsync(lease, ct));

                // Post-tick drain — one call after the parallel batch completes. Picks up
                // auto-heals generated during this tick plus any operator approvals that
                // arrived while we were scanning. Gated on claimed.Count > 0 so idle ticks
                // don't burn cycles on an empty pending query.
                await DrainPendingFixesAsync("post-tick", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MonitoringWorker tick failed");
                // Don't hot-loop on a persistent failure
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }

        logger.LogInformation("MonitoringWorker stopped");
    }

    private async Task DrainPendingFixesAsync(string trigger, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var execute = scope.ServiceProvider.GetRequiredService<IExecuteFixesUseCase>();
            await execute.ExecuteAsync(ct);
            logger.LogDebug("MonitoringWorker: {Trigger} drain complete", trigger);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MonitoringWorker: {Trigger} drain failed", trigger);
        }
    }

    private async Task RunOneJobAsync(ClaimedJobLease lease, CancellationToken hostCt)
    {
        // Per-job timeout = lease duration. If the scan exceeds it, we cancel
        // and another worker will eventually steal the (now-expired) lease.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        jobCts.CancelAfter(TimeSpan.FromSeconds(lease.LeaseDurationSeconds));

        // Fresh DI scope per job so each scan has its own DbContext, repos, strategies.
        using var scope = scopeFactory.CreateScope();
        var jobRepo     = scope.ServiceProvider.GetRequiredService<IMonitoredJobRepository>();
        var leaseRepo   = scope.ServiceProvider.GetRequiredService<IMonitoredJobLeaseRepository>();
        var historyRepo = scope.ServiceProvider.GetRequiredService<IScanRunHistoryRepository>();
        var strategies  = scope.ServiceProvider.GetServices<IScanStrategy>();

        var outcome = JobRunOutcome.Success;
        string? error = null;
        var pollingIntervalSeconds = 300;
        var startedAt = DateTime.Now;
        var failures = 0; var classifications = 0; var recommendations = 0;
        var identifierExtractionFailures = 0; var oversizeFileSkips = 0; var predicateUnevaluableSkips = 0;

        try
        {
            var job = await jobRepo.GetByIdAsync(lease.MonitoredJobId, jobCts.Token);
            if (job is null)
            {
                outcome = JobRunOutcome.Failed;
                error   = $"MonitoredJob {lease.MonitoredJobId} not found at scan time";
                logger.LogWarning(error);
                return;
            }

            pollingIntervalSeconds = job.PollingIntervalSeconds;

            var strategy = strategies.FirstOrDefault(s => s.ScanType == job.ScanType);
            if (strategy is null)
            {
                outcome = JobRunOutcome.Failed;
                error   = $"No scan strategy for ScanType '{job.ScanType}'";
                logger.LogWarning("{Error} on job '{Name}'", error, job.Name);
                return;
            }

            logger.LogInformation(
                "MonitoringWorker: [{ScanType}] scan for job '{Name}' (lease {Seconds}s)",
                job.ScanType, job.Name, lease.LeaseDurationSeconds);

            var result = await strategy.ScanAsync(job, jobCts.Token);
            failures        = result.FailuresDetected;
            classifications = result.Classifications;
            recommendations = result.Recommendations;
            identifierExtractionFailures = result.IdentifierExtractionFailures;
            oversizeFileSkips            = result.OversizeFileSkips;
            predicateUnevaluableSkips    = result.PredicateUnevaluableSkips;

            logger.LogInformation(
                "MonitoredJob '{Name}' [{ScanType}]: {Failures} failures, " +
                "{Classifications} classified, {Recommendations} recommendations — {Detail}",
                job.Name, job.ScanType, failures, classifications, recommendations, result.Detail);
        }
        catch (OperationCanceledException) when (!hostCt.IsCancellationRequested)
        {
            outcome = JobRunOutcome.Timeout;
            error   = $"Job exceeded lease duration ({lease.LeaseDurationSeconds}s)";
            logger.LogWarning("Scan timed out for job {JobId}", lease.MonitoredJobId);
        }
        catch (Exception ex)
        {
            outcome = JobRunOutcome.Failed;
            error   = ex.Message;
            logger.LogError(ex, "Scan failed for job {JobId}", lease.MonitoredJobId);
        }
        finally
        {
            var completedAt = DateTime.Now;

            // Release uses the host token, not jobCts — we want to record outcome
            // even when the run timed out.
            try
            {
                var stillOurs = await leaseRepo.ReleaseAsync(
                    lease.MonitoredJobId, _leasedBy, outcome,
                    pollingIntervalSeconds, error, hostCt);

                if (!stillOurs)
                {
                    outcome = JobRunOutcome.Stolen;
                    logger.LogWarning(
                        "Lease for job {JobId} was stolen before release — results recorded but lease state untouched",
                        lease.MonitoredJobId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to release lease for job {JobId}", lease.MonitoredJobId);
            }

            // Append history row regardless of stolen/timeout/failed — append-only audit
            // of every scan attempt. Wrapped in its own try so a history-write failure
            // never bubbles into the worker loop.
            try
            {
                var durationMs = (int)Math.Clamp((completedAt - startedAt).TotalMilliseconds, 0, int.MaxValue);
                await historyRepo.SaveAsync(new ScanRunHistory
                {
                    MonitoredJobId   = lease.MonitoredJobId,
                    LeasedBy         = _leasedBy,
                    StartedAt        = startedAt,
                    CompletedAt      = completedAt,
                    DurationMs       = durationMs,
                    Outcome          = outcome,
                    Error            = error is null ? null : (error.Length > 2000 ? error[..2000] : error),
                    FailuresDetected = failures,
                    Classifications  = classifications,
                    Recommendations  = recommendations,
                    IdentifierExtractionFailures = identifierExtractionFailures,
                    OversizeFileSkips            = oversizeFileSkips,
                    PredicateUnevaluableSkips    = predicateUnevaluableSkips,
                }, hostCt);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write ScanRunHistory for job {JobId}", lease.MonitoredJobId);
            }
        }
    }
}
