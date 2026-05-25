using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Fix;

/// <summary>
/// Executes a fix by calling an HTTP endpoint.
/// ActionPayload = URL; supports {failureId} placeholder substitution.
/// Example: http://jobs.internal/api/retry/{failureId}
/// </summary>
public sealed class ApiCallExecutor(
    IHttpClientFactory httpClientFactory,
    ILogger<ApiCallExecutor> logger) : IFixActionExecutor
{
    public FixActionType ActionType => FixActionType.ApiCall;

    public async Task<bool> ExecuteAsync(
        string? payload,
        AiRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            logger.LogError("ApiCallExecutor: ActionPayload (URL) is required for Failure {FailureId}",
                recommendation.FailureId);
            return false;
        }

        var url = payload.Replace("{failureId}", recommendation.FailureId.ToString(),
            StringComparison.OrdinalIgnoreCase);

        try
        {
            var client   = httpClientFactory.CreateClient("FixEngine");
            var response = await client.PostAsync(url, content: null, ct);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "ApiCallExecutor: POST {Url} succeeded ({StatusCode}) for Failure {FailureId}",
                    url, (int)response.StatusCode, recommendation.FailureId);
                return true;
            }

            logger.LogWarning(
                "ApiCallExecutor: POST {Url} returned {StatusCode} for Failure {FailureId}",
                url, (int)response.StatusCode, recommendation.FailureId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ApiCallExecutor: HTTP call to {Url} failed for Failure {FailureId}",
                url, recommendation.FailureId);
            return false;
        }
    }
}
