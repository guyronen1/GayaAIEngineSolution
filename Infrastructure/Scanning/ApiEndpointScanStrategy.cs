using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Core.Results;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Scanning;

/// <summary>
/// GETs LogSourceUrl and treats non-2xx responses or bodies containing "error"/"exception" as failures.
/// </summary>
public sealed class ApiEndpointScanStrategy(
    IHttpClientFactory          httpFactory,
    IJobRepository              jobRepo,
    IClassifyJobsUseCase        classify,
    IGenerateSuggestionsUseCase suggest,
    ILogger<ApiEndpointScanStrategy> logger) : IScanStrategy
{
    public ScanType ScanType => ScanType.ApiEndpoint;

    public async Task<ScanResult> ScanAsync(MonitoredJob job, CancellationToken ct = default)
    {
        if (job.LogSourceUrl is null)
            throw new InvalidOperationException($"Job '{job.Name}' has no LogSourceUrl configured for ApiEndpoint scan.");

        var result = new ScanResult
        {
            JobName  = job.Name,
            ScanType = ScanType.ApiEndpoint,
            Detail   = $"URL: {job.LogSourceUrl}"
        };

        string?             statusStr    = null;
        string              responseBody = string.Empty;
        bool                isFailure;

        try
        {
            var http     = httpFactory.CreateClient();
            var response = await http.GetAsync(job.LogSourceUrl, ct);
            statusStr    = response.StatusCode.ToString();
            responseBody = await response.Content.ReadAsStringAsync(ct);

            isFailure = !response.IsSuccessStatusCode
                     || responseBody.Contains("error",     StringComparison.OrdinalIgnoreCase)
                     || responseBody.Contains("exception", StringComparison.OrdinalIgnoreCase)
                     || responseBody.Contains("failed",    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            statusStr    = "Unreachable";
            responseBody = ex.Message;
            isFailure    = true;
        }

        if (!isFailure)
            return result;

        var snippet = responseBody.Length > 500 ? responseBody[..500] : responseBody;
        var failure = new JobFailure
        {
            JobId          = 0,
            JobTypeId      = job.JobTypeId,
            MonitoredJobId = job.MonitoredJobId,
            StepName       = "ApiEndpointCheck",
            SourceId       = job.LogSourceUrl,
            ErrorMessage   = $"API check failed: status={statusStr}, body={snippet}",
            SourceLogPath  = job.LogSourceUrl,
            Status         = JobStatus.Failed,
            DetectedAt     = DateTime.Now,
        };

        failure = await jobRepo.SaveAsync(failure, ct);
        result.FailuresDetected = 1;

        logger.LogInformation("ApiEndpointScan '{Job}': failure detected at {Url} — status {Status}",
            job.Name, job.LogSourceUrl, statusStr);

        var classifications = await classify.ExecuteAsync([failure], ct);
        result.Classifications = classifications.Count;

        await suggest.ExecuteAsync(classifications, ct);
        result.Recommendations = classifications.Count;

        return result;
    }
}
