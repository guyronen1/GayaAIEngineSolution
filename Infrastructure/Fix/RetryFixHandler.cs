using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

public sealed class RetryFixHandler(ILogger<RetryFixHandler> logger) : IFixHandler
{
    public FixCategory Category => FixCategory.Retry;

    public async Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Retrying Failure {FailureId} — action: {Action}",
            recommendation.FailureId, recommendation.SuggestedAction);

        // TODO: trigger actual job-retry mechanism (DTSX re-run, SQL Agent retry, etc.)
        await Task.CompletedTask;
        return true;
    }
}
