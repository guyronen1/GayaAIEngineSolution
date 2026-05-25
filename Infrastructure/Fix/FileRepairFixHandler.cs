using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

public sealed class FileRepairFixHandler(ILogger<FileRepairFixHandler> logger) : IFixHandler
{
    public FixCategory Category => FixCategory.FileRepair;

    public async Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Checking file availability for Failure {FailureId}",
            recommendation.FailureId);

        // TODO: verify source file existence; alert operator if missing
        await Task.CompletedTask;
        return false;
    }
}
