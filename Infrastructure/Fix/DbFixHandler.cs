using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

public sealed class DbFixHandler(ILogger<DbFixHandler> logger) : IFixHandler
{
    public FixCategory Category => FixCategory.DbFix;

    public async Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Attempting DB connectivity fix for Failure {FailureId}",
            recommendation.FailureId);

        // TODO: test SQL Server connection; trigger reconnect / connection pool reset
        await Task.CompletedTask;
        return false;
    }
}
