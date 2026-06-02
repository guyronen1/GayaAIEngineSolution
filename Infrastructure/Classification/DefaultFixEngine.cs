using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Classification;

/// <summary>
/// Executes fixes by looking up the FixPolicyRule for the recommendation's error type,
/// then dispatching to the matching IFixActionExecutor (ApiCall, StoredProcedure, Script,
/// SqlScript, CopyFile, Manual). Composite policies are orchestrated inline — each step
/// is dispatched to its own action executor, with a per-step FixExecutionLog row.
///
/// Falls back to IFixHandler (FixCategory-based) when no policy rule is configured,
/// preserving backward compatibility for jobs without explicit policies.
/// </summary>
public sealed class DefaultFixEngine(
    IFixPolicyRepository              policyRepo,
    IEnumerable<IFixActionExecutor>   actionExecutors,
    IEnumerable<IFixHandler>          categoryHandlers,
    IFixLogRepository                 fixLogs,
    ILogger<DefaultFixEngine>         logger) : IFixEngine
{
    public async Task<FixOutcome> ExecuteAsync(
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        var jobTypeId      = recommendation.Failure?.JobTypeId;
        var monitoredJobId = recommendation.Failure?.MonitoredJobId;

        // ── Policy-driven path (preferred) ──────────────────────────────────
        if (recommendation.ErrorTypeId > 0 && jobTypeId is > 0)
        {
            // Pass MonitoredJobId so an enabled per-job override wins over
            // the JobType-level default for this rec's failure context.
            // Repo handles the priority internally (null monitoredJobId
            // simply skips the override layer).
            var policy = await policyRepo.GetForAsync(
                jobTypeId.Value, recommendation.ErrorTypeId, monitoredJobId, ct);
            if (policy is { Enabled: true })
                return await ExecuteByPolicyAsync(policy, recommendation, ct);

            logger.LogInformation(
                "Policy lookup miss: jobTypeId={JobTypeId} errorTypeId={ErrorTypeId} " +
                "recommendationId={RecommendationId} — falling back to dictionary/category handler",
                jobTypeId, recommendation.ErrorTypeId, recommendation.RecommendationId);
        }
        else if (jobTypeId is null)
        {
            // Failure navigation wasn't loaded — operational bug, not a config issue.
            logger.LogWarning(
                "Failure navigation missing on Recommendation {RecommendationId} — " +
                "cannot perform (JobTypeId + ErrorTypeId) policy lookup",
                recommendation.RecommendationId);
        }

        // ── Fallback: FixCategory handler ───────────────────────────────────
        // Manual category handler is also a no-op — return NoAutomatedAction
        // so the caller can route the failure to AwaitingManualAction (when
        // operator-approved) rather than treating it as a fix failure.
        if (recommendation.FixCategory == FixCategory.Manual)
        {
            logger.LogInformation(
                "Failure {FailureId}: Manual FixCategory — no automated step (fallback path)",
                recommendation.FailureId);
            return FixOutcome.NoAutomatedAction;
        }

        var handler = categoryHandlers.FirstOrDefault(h => h.Category == recommendation.FixCategory);
        if (handler is not null)
            return await handler.HandleAsync(recommendation, ct) ? FixOutcome.Success : FixOutcome.Failed;

        logger.LogWarning(
            "No fix strategy found for Failure {FailureId} (FixCategory {Category})",
            recommendation.FailureId, recommendation.FixCategory);
        return FixOutcome.Failed;
    }

    private async Task<FixOutcome> ExecuteByPolicyAsync(
        FixPolicyRule    policy,
        AiRecommendation recommendation,
        CancellationToken ct)
    {
        // The fundamental fix vs the old shape: distinguish "policy says no
        // automated action exists" (NoAutomatedAction) from "executor was
        // attempted and failed" (Failed). The caller decides the JobStatus
        // transition; both used to collapse to ManualRequired which lost
        // information.
        if (policy.ActionType == FixActionType.Manual)
        {
            logger.LogInformation(
                "Failure {FailureId}: policy rule {RuleId} requires manual intervention",
                recommendation.FailureId, policy.RuleId);
            return FixOutcome.NoAutomatedAction;
        }

        // Composite: iterate steps best-effort. Every step runs; any failure
        // produces FixOutcome.Failed at the end (caller routes failure to
        // ManualRequired). Per-step FixExecutionLog row captures the per-step
        // outcome so the operator can see which step needs manual cleanup.
        if (policy.ActionType == FixActionType.Composite)
            return await ExecuteCompositeAsync(policy, recommendation, ct);

        var executor = actionExecutors.FirstOrDefault(e => e.ActionType == policy.ActionType);
        if (executor is null)
        {
            logger.LogWarning(
                "No IFixActionExecutor registered for ActionType {ActionType} (rule {RuleId})",
                policy.ActionType, policy.RuleId);
            return FixOutcome.Failed;
        }

        logger.LogInformation(
            "Executing {ActionType} fix for Failure {FailureId} via policy rule {RuleId}",
            policy.ActionType, recommendation.FailureId, policy.RuleId);

        var ok = await executor.ExecuteAsync(policy.ActionPayload, recommendation, ct);
        return ok ? FixOutcome.Success : FixOutcome.Failed;
    }

    private async Task<FixOutcome> ExecuteCompositeAsync(
        FixPolicyRule    policy,
        AiRecommendation recommendation,
        CancellationToken ct)
    {
        if (policy.Steps.Count == 0)
        {
            // Defensive: controller validation prevents this, but a hand-edited
            // row could still slip through. Treat as misconfiguration → Failed.
            logger.LogWarning(
                "Composite policy rule {RuleId} has no steps — treating as failure (Failure {FailureId})",
                policy.RuleId, recommendation.FailureId);
            return FixOutcome.Failed;
        }

        logger.LogInformation(
            "Executing composite fix for Failure {FailureId} via policy rule {RuleId} ({StepCount} steps)",
            recommendation.FailureId, policy.RuleId, policy.Steps.Count);

        var anyFailed = false;
        // Steps are eager-loaded ordered by StepOrder in SqlFixPolicyRepository;
        // re-sort defensively here in case some future caller bypasses the repo.
        var orderedSteps = policy.Steps.OrderBy(s => s.StepOrder).ToList();
        var trigger      = recommendation.OperatorApproved == true
            ? TriggerType.OperatorApproved
            : TriggerType.AutoHeal;

        foreach (var step in orderedSteps)
        {
            ct.ThrowIfCancellationRequested();

            var executor = actionExecutors.FirstOrDefault(e => e.ActionType == step.ActionType);
            bool   stepOk;
            string resultDetail;

            if (executor is null)
            {
                logger.LogWarning(
                    "Composite rule {RuleId} step {StepOrder}: no executor for {ActionType}",
                    policy.RuleId, step.StepOrder, step.ActionType);
                stepOk       = false;
                resultDetail = $"Step {step.StepOrder} ({step.ActionType}): no executor registered.";
            }
            else
            {
                logger.LogInformation(
                    "Composite rule {RuleId} step {StepOrder} ({ActionType}) running for Failure {FailureId}",
                    policy.RuleId, step.StepOrder, step.ActionType, recommendation.FailureId);
                stepOk = await executor.ExecuteAsync(step.ActionPayload, recommendation, ct);
                resultDetail = stepOk
                    ? $"Step {step.StepOrder} ({step.ActionType}) succeeded."
                    : $"Step {step.StepOrder} ({step.ActionType}) failed.";
            }

            if (!stepOk) anyFailed = true;

            // Per-step FixExecutionLog row — what the operator scans to see
            // which sub-action of the composite needs manual cleanup. The
            // ExecutedAction string is the step's Description (operator-set)
            // when present, else "Step N: <ActionType>" so the row is
            // self-describing without a join to FixPolicyRuleSteps.
            var stepLabel = string.IsNullOrWhiteSpace(step.Description)
                ? $"Step {step.StepOrder}: {step.ActionType}"
                : $"Step {step.StepOrder}: {step.Description}";

            await fixLogs.SaveAsync(new FixExecutionLog
            {
                FailureId        = recommendation.FailureId,
                RecommendationId = recommendation.RecommendationId,
                ExecutedAction   = stepLabel,
                TriggerType      = trigger,
                ExecutedBy       = $"{nameof(DefaultFixEngine)}.Composite",
                Success          = stepOk,
                ResultDetail     = resultDetail,
                ExecutedAt       = DateTime.Now,
            }, ct);
        }

        return anyFailed ? FixOutcome.Failed : FixOutcome.Success;
    }
}
