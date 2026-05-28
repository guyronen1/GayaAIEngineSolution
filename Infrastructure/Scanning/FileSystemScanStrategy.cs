using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Core.Results;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Scanning;

public sealed class FileSystemScanStrategy(
    IDirectoryPipelineUseCase       pipeline,
    IJobRepository                  jobRepo,
    IScanWatermarkRepository        watermarks,
    IClassifyJobsUseCase            classify,
    IGenerateSuggestionsUseCase     suggest,
    ILogger<FileSystemScanStrategy> logger) : IScanStrategy
{
    /// <summary>Hard cap on failures created per file per keyword per scan.
    /// Protects against a pathologically error-filled log chunk spawning thousands of rows.</summary>
    private const int MaxFailuresPerKeywordPerScan = 100;

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

        logger.LogInformation(
            "FileSystemScan '{Job}': total ScanCheckRules={Total}, active ErrorKeyword rules={Keywords}",
            job.Name, job.ScanCheckRules.Count, keywordRules.Count);

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
                var (content, newOffset) = await ReadNewContentAsync(job.MonitoredJobId, file, ct);
                if (string.IsNullOrWhiteSpace(content))
                {
                    if (newOffset > 0)
                        await watermarks.UpdateFileOffsetAsync(job.MonitoredJobId, file, newOffset, ct);
                    continue;
                }

                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var rule in keywordRules)
                {
                    // Strip glob-style wildcards — Contains() already does substring match
                    var keyword = rule.TargetField.Trim('*').Trim();
                    if (string.IsNullOrEmpty(keyword)) continue;

                    // Every matching line in the new content becomes a failure.
                    // No HasOpenFailureAsync check — the watermark already prevents replays
                    // of old content, so leftover Failed-status rows from prior scans must
                    // not block new errors from being reported.
                    // Within this scan, dedup by exact line text so identical lines spammed
                    // in the same chunk don't create N identical failures.
                    var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var matchesForKeyword = 0;

                    foreach (var rawLine in lines)
                    {
                        if (matchesForKeyword >= MaxFailuresPerKeywordPerScan)
                        {
                            logger.LogWarning(
                                "FileSystemScan '{Job}': hit cap of {Cap} failures for keyword '{Keyword}' in {File} — remaining matches in this chunk skipped",
                                job.Name, MaxFailuresPerKeywordPerScan, keyword, Path.GetFileName(file));
                            break;
                        }

                        if (!rawLine.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var excerpt = rawLine.Trim();
                        if (excerpt.Length == 0) continue;
                        if (!seenInBatch.Add(excerpt))
                            continue; // identical line text already produced a failure this scan
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
                            DetectedAt     = DateTime.Now,
                        };

                        failure = await jobRepo.SaveAsync(failure, ct);
                        created.Add(failure);
                        matchesForKeyword++;

                        logger.LogInformation(
                            "FileSystemScan '{Job}': keyword '{Keyword}' matched in {File} — FailureId {FailureId}",
                            job.Name, keyword, Path.GetFileName(file), failure.FailureId);
                    }
                }

                await watermarks.UpdateFileOffsetAsync(job.MonitoredJobId, file, newOffset, ct);
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

    private async Task<(string Content, long NewOffset)> ReadNewContentAsync(
        int monitoredJobId, string filePath, CancellationToken ct)
    {
        var fromOffset = await watermarks.GetFileOffsetAsync(monitoredJobId, filePath, ct);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fromOffset > stream.Length)
            fromOffset = 0; // file was rotated or truncated

        if (fromOffset == stream.Length)
            return (string.Empty, fromOffset); // nothing new

        stream.Seek(fromOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var content      = await reader.ReadToEndAsync(ct);
        return (content, stream.Position);
    }
}
