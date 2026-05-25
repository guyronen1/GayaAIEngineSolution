using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Core.Results;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Application.Classification;

public sealed class ClassifyJobsUseCase(
    IJobRepository jobs,
    IClassificationStrategy classifier,
    ILogReader logReader,
    ILogger<ClassifyJobsUseCase> logger) : IClassifyJobsUseCase
{
    /// <summary>Classifies all failed jobs that have not yet been classified (ErrorTypeId is null).</summary>
    public async Task<IReadOnlyList<ClassificationResult>> ExecuteAsync(CancellationToken ct = default)
    {
        var unclassified = await jobs.GetUnclassifiedAsync(ct);
        return await ClassifyManyAsync(unclassified, ct);
    }

    /// <summary>Classifies a specific set of job failures (e.g. just-created ones).</summary>
    public async Task<IReadOnlyList<ClassificationResult>> ExecuteAsync(
        IEnumerable<JobFailure> jobList,
        CancellationToken ct = default)
        => await ClassifyManyAsync(jobList, ct);

    private async Task<IReadOnlyList<ClassificationResult>> ClassifyManyAsync(
        IEnumerable<JobFailure> jobList, CancellationToken ct)
    {
        var results = new List<ClassificationResult>();

        foreach (var job in jobList)
        {
            ct.ThrowIfCancellationRequested();

            // For file-based failures use the log file; for DB/API failures fall back to ErrorMessage.
            var logContent = await logReader.ReadAsync(job.SourceLogPath, ct);
            if (string.IsNullOrWhiteSpace(logContent))
                logContent = job.ErrorMessage ?? string.Empty;

            var result = await classifier.ClassifyAsync(job, logContent, ct);

            if (result is not null)
            {
                await jobs.UpdateClassificationAsync(job.FailureId, result, ct);
                results.Add(result);
                logger.LogInformation(
                    "Job {JobId} classified as {ErrorTypeCode} (confidence {Confidence:P0})",
                    job.JobId, result.ErrorTypeCode, result.Confidence);
            }
        }

        return results;
    }
}
