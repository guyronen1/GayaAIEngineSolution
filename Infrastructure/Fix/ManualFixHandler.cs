using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

public sealed class ManualFixHandler(ILogger<ManualFixHandler> logger) : IFixHandler
{
    public FixCategory Category => FixCategory.Manual;

    public async Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Failure {FailureId} requires manual intervention — auto-fix skipped",
            recommendation.FailureId);

        await Task.CompletedTask;
        return false;
    }
}
