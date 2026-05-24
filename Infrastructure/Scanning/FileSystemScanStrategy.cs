using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Core.Results;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Scanning;

public sealed class FileSystemScanStrategy(
    IDirectoryPipelineUseCase       pipeline,
    ILogReader                      logReader,
    IJobRepository                  jobRepo,
    IClassifyJobsUseCase            classify,
    IGenerateSuggestionsUseCase     suggest,
    ILogger<FileSystemScanStrategy> logger) : IScanStrategy
{
    public ScanType ScanType => ScanType.FileSystem;

    public async Task<ScanResult> ScanAsync(MonitoredJob job, CancellationToken ct = default)
    {
        if (job.LogFolder is null)
            throw new InvalidOperationException($"Job '{job.Name}' has no LogFolder configured for FileSystem scan.");

        var patterns = (job.SearchPatterns ?? "*.log")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var keywordRules = job.ScanCheckRules
            .Where(r => r.IsActive && r.CheckType == CheckType.ErrorKeyword)
            .ToList();

        var result = new ScanResult
        {
            JobName  = job.Name,
            ScanType = ScanType.FileSystem,
            Detail   = $"Folder: {job.LogFolder} | Patterns: {string.Join(", ", patterns)}"
        };

        if (keywordRules.Count == 0)
        {
            // No keyword rules — full pipeline mode (scan all log lines)
            foreach (var pattern in patterns)
            {
                var r = await pipeline.ExecuteAsync(job.LogFolder, pattern, false, ct);
                result.FailuresDetected += r.JobsCreated;
                result.Classifications  += r.Classifications;
                result.Recommendations  += r.Recommendations;
            }
            return result;
        }

        // Keyword mode: flag any file whose lines contain one of the configured keywords
        if (!Directory.Exists(job.LogFolder))
        {
            logger.LogWarning("FileSystemScan '{Job}': folder not found: {Folder}", job.Name, job.LogFolder);
            return result;
        }

        var created = new List<JobFailure>();

        foreach (var pattern in patterns)
        {
            var files = Directory.GetFiles(job.LogFolder, pattern, SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                var content = await logReader.ReadAsync(file, ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var rule in keywordRules)
                {
                    // Strip glob-style wildcards — Contains() already does substring match
                    var keyword = rule.TargetField.Trim('*').Trim();
                    if (string.IsNullOrEmpty(keyword)) continue;

                    var matchLine = lines.FirstOrDefault(l =>
                        l.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                    if (matchLine is null) continue;

                    // Dedup: skip if an open failure already exists for this file + keyword
                    if (await jobRepo.HasOpenFailureAsync(job.MonitoredJobId, file, keyword, ct))
                    {
                        logger.LogDebug(
                            "FileSystemScan '{Job}': open failure already exists for {File} + '{Keyword}' — skipping",
                            job.Name, Path.GetFileName(file), keyword);
                        continue;
                    }

                    var excerpt = matchLine.Trim();
                    if (excerpt.Length > 500) excerpt = excerpt[..500];

                    var failure = new JobFailure
                    {
                        JobId          = 0,
                        JobTypeId      = job.JobTypeId,
                        MonitoredJobId = job.MonitoredJobId,
                        StepName       = Path.GetFileName(file),
                        SourceId       = Path.GetFileName(file),
                        ErrorMessage   = $"[{keyword}] {Path.GetFileName(file)}: {excerpt}",
                        SourceLogPath  = file,
                        Status         = JobStatus.Failed,
                        DetectedAt     = DateTime.UtcNow,
                    };

                    failure = await jobRepo.SaveAsync(failure, ct);
                    created.Add(failure);

                    logger.LogInformation(
                        "FileSystemScan '{Job}': keyword '{Keyword}' matched in {File}",
                        job.Name, keyword, Path.GetFileName(file));
                }
            }
        }

        result.FailuresDetected = created.Count;
        if (created.Count == 0) return result;

        var classifications = await classify.ExecuteAsync(created, ct);
        result.Classifications = classifications.Count;

        await suggest.ExecuteAsync(classifications, ct);
        result.Recommendations = classifications.Count;

        return result;
    }
}
