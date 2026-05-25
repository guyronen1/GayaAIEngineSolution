using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Application.Remediation;

public sealed class ExecuteFixesUseCase(
    IRecommendationRepository recommendations,
    IFixLogRepository fixLogs,
    IAuditRepository audit,
    IJobRepository jobs,
    IFixEngine fixEngine,
    ILogger<ExecuteFixesUseCase> logger) : IExecuteFixesUseCase
{
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var pending = await recommendations.GetPendingAsync(ct);

        foreach (var rec in pending)
        {
            ct.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Executing fix for Failure {FailureId}: [{Category}] {Action}",
                rec.FailureId, rec.FixCategory, rec.SuggestedAction);

            var success = await fixEngine.ExecuteAsync(rec, ct);
            var trigger = rec.OperatorApproved == true ? TriggerType.OperatorApproved : TriggerType.AutoHeal;

            await fixLogs.SaveAsync(new FixExecutionLog
            {
                FailureId        = rec.FailureId,
                RecommendationId = rec.RecommendationId,
                ExecutedAction   = rec.SuggestedAction,
                TriggerType      = trigger,
                ExecutedBy       = nameof(ExecuteFixesUseCase),
                Success          = success,
                ResultDetail     = success
                    ? "Fix applied successfully."
                    : "Automatic fix did not complete.",
                ExecutedAt       = DateTime.Now,
            }, ct);

            await audit.WriteAsync(new AuditLog
            {
                FailureId = rec.FailureId,
                EventType = success ? "FixExecuted" : "FixFailed",
                Actor     = nameof(ExecuteFixesUseCase),
                Detail    = $"{(success ? "Executed" : "Failed")} {rec.FixCategory} fix " +
                            $"for recommendation {rec.RecommendationId}.",
                Timestamp = DateTime.Now,
            }, ct);

            var newStatus = success ? JobStatus.Resolved : JobStatus.ManualRequired;
            await jobs.UpdateStatusAsync(rec.FailureId, newStatus, ct);

            if (success)
            {
                await recommendations.MarkExecutedAsync(rec.RecommendationId, ct);
                logger.LogInformation("Fix executed successfully for Failure {FailureId}", rec.FailureId);
            }
            else
            {
                logger.LogWarning(
                    "Fix failed for Failure {FailureId} — requires manual intervention", rec.FailureId);
            }
        }
    }
}
