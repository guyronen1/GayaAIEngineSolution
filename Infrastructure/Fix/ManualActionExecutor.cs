using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

/// <summary>
/// No-op executor for Manual policies — logs that operator action is required and returns false.
/// </summary>
public sealed class ManualActionExecutor(ILogger<ManualActionExecutor> logger) : IFixActionExecutor
{
    public FixActionType ActionType => FixActionType.Manual;

    public Task<bool> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        logger.LogWarning(
            "Failure {FailureId} requires manual operator intervention — automated fix skipped",
            recommendation.FailureId);
        return Task.FromResult(false);
    }
}
