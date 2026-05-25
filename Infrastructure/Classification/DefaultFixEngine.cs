using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Classification;

/// <summary>
/// Executes fixes by looking up the FixPolicyRule for the recommendation's error type,
/// then dispatching to the matching IFixActionExecutor (ApiCall, StoredProcedure, Script, Manual).
///
/// Falls back to IFixHandler (FixCategory-based) when no policy rule is configured,
/// preserving backward compatibility for jobs without explicit policies.
/// </summary>
public sealed class DefaultFixEngine(
    IFixPolicyRepository              policyRepo,
    IEnumerable<IFixActionExecutor>   actionExecutors,
    IEnumerable<IFixHandler>          categoryHandlers,
    ILogger<DefaultFixEngine>         logger) : IFixEngine
{
    public async Task<bool> ExecuteAsync(
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        var jobTypeId = recommendation.Failure?.JobTypeId;

        // ── Policy-driven path (preferred) ──────────────────────────────────
        if (recommendation.ErrorTypeId > 0 && jobTypeId is > 0)
        {
            var policy = await policyRepo.GetForAsync(jobTypeId.Value, recommendation.ErrorTypeId, ct);
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
        var handler = categoryHandlers.FirstOrDefault(h => h.Category == recommendation.FixCategory);
        if (handler is not null)
            return await handler.HandleAsync(recommendation, ct);

        logger.LogWarning(
            "No fix strategy found for Failure {FailureId} (FixCategory {Category})",
            recommendation.FailureId, recommendation.FixCategory);
        return false;
    }

    private async Task<bool> ExecuteByPolicyAsync(
        FixPolicyRule    policy,
        AiRecommendation recommendation,
        CancellationToken ct)
    {
        if (policy.ActionType == FixActionType.Manual)
        {
            logger.LogInformation(
                "Failure {FailureId}: policy rule {RuleId} requires manual intervention",
                recommendation.FailureId, policy.RuleId);
            return false;
        }

        var executor = actionExecutors.FirstOrDefault(e => e.ActionType == policy.ActionType);
        if (executor is null)
        {
            logger.LogWarning(
                "No IFixActionExecutor registered for ActionType {ActionType} (rule {RuleId})",
                policy.ActionType, policy.RuleId);
            return false;
        }

        logger.LogInformation(
            "Executing {ActionType} fix for Failure {FailureId} via policy rule {RuleId}",
            policy.ActionType, recommendation.FailureId, policy.RuleId);

        return await executor.ExecuteAsync(policy.ActionPayload, recommendation, ct);
    }
}
