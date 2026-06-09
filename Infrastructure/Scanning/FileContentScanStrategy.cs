using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Core.Results;
using MaiaAI.Core.Scanning;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Scanning;

/// <summary>
/// Structured extraction from INPUT DATA files (XML in v1) — distinct from
/// FileSystemScanStrategy's keyword-line matching over logs. Two operator modes,
/// both driven by one rule shape:
///   1. Filename signals failure — a file whose name matches the rule's
///      TargetField pattern IS a failure (e.g. *WARNING*.xml). No predicate.
///   2. Content inspection — extract a value (ExtractorLocator) and test it
///      against a predicate; failure only when satisfied.
/// Either way the rule may extract an identifier (IdentifierLocator) for SourceId.
///
/// Walk is file-outer / rule-inner ("walk-once-apply-many"): each file is
/// considered once, all rules whose filename pattern matches are applied, and
/// the per-file content watermark is updated once after — the only shape
/// consistent with the per-(job, file) ScanContentWatermarks grain.
/// </summary>
public sealed class FileContentScanStrategy(
    IJobRepository                   jobRepo,
    IScanWatermarkRepository         watermarks,
    IClassifyJobsUseCase             classify,
    IGenerateSuggestionsUseCase      suggest,
    IEnumerable<IFileContentExtractor> extractors,
    ILogger<FileContentScanStrategy> logger) : IScanStrategy
{
    private readonly Dictionary<FileFormat, IFileContentExtractor> _extractors =
        extractors.ToDictionary(e => e.Format);

    public ScanType ScanType => ScanType.FileContent;

    public async Task<ScanResult> ScanAsync(MonitoredJob job, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(job.LogFolder))
            throw new InvalidOperationException(
                $"Job '{job.Name}' has no LogFolder configured for FileContent scan.");

        var rules = job.ScanCheckRules
            .Where(r => r.IsActive && r.CheckType == CheckType.FileContent)
            .ToList();

        var result = new ScanResult
        {
            JobName  = job.Name,
            ScanType = ScanType.FileContent,
            Detail   = $"Folder: {job.LogFolder} | Rules: {rules.Count}" +
                       (job.IncludeSubfolders ? " | recursive" : ""),
        };

        if (rules.Count == 0)
        {
            logger.LogWarning("FileContentScan '{Job}': no active FileContent rules — nothing to scan", job.Name);
            return result;
        }

        if (!Directory.Exists(job.LogFolder))
        {
            logger.LogWarning("FileContentScan '{Job}': folder not found: {Folder}", job.Name, job.LogFolder);
            return result;
        }

        var searchOption = job.IncludeSubfolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        // No-pattern enumerate (avoid Win32 glob quirks) — filter per-rule in code
        // via the FilenamePattern DSL, identical to FileSystemScanStrategy.
        var allFiles = Directory.EnumerateFiles(job.LogFolder, "*", searchOption).ToList();

        var created = new List<JobFailure>();

        foreach (var file in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file);

            // Which rules' filename pattern matches this file? (file-outer/rule-inner)
            var matchingRules = rules
                .Where(r => FilenamePattern.Matches(fileName, r.TargetField))
                .ToList();
            if (matchingRules.Count == 0) continue;

            // Content watermark: skip unchanged files. New file → null → process.
            var mtime        = File.GetLastWriteTime(file);
            var lastModified = await watermarks.GetContentWatermarkAsync(job.MonitoredJobId, file, ct);
            if (lastModified is not null && mtime <= lastModified.Value)
                continue;

            var producedFailure = false;
            var oversize        = false;

            foreach (var rule in matchingRules)
            {
                if (rule.ExtractorType is null ||
                    !_extractors.TryGetValue(rule.ExtractorType.Value, out var extractor))
                {
                    logger.LogWarning(
                        "FileContentScan '{Job}': rule {RuleId} has no usable extractor (ExtractorType={Type}) — skipping",
                        job.Name, rule.CheckRuleId, rule.ExtractorType);
                    continue;
                }

                try
                {
                    // ── Primary value + predicate ────────────────────────────
                    // Only parse when a locator is configured. A pure
                    // filename-match rule (no ExtractorLocator AND no
                    // IdentifierLocator) never opens the file — so the 5MB cap is
                    // never consulted and a 6MB *WARNING*.xml still produces a
                    // failure. Intentional: if we don't read it, its size is moot.
                    string? primary = null;
                    if (!string.IsNullOrWhiteSpace(rule.ExtractorLocator))
                        primary = await extractor.ExtractAsync(file, rule.ExtractorLocator, ct);

                    if (rule.ExtractorPredicateType is { } predicateType)
                    {
                        if (string.IsNullOrWhiteSpace(rule.ExtractorLocator))
                        {
                            logger.LogWarning(
                                "FileContentScan '{Job}': rule {RuleId} has a predicate but no ExtractorLocator — skipping",
                                job.Name, rule.CheckRuleId);
                            continue;
                        }
                        if (primary is null)
                        {
                            // Predicate set but value not extractable — can't decide; skip.
                            // Counted on ScanRunHistory so a locator that matches
                            // nothing (or a file missing the expected element)
                            // surfaces instead of failing silently.
                            result.PredicateUnevaluableSkips++;
                            logger.LogWarning(
                                "FileContentScan '{Job}': rule {RuleId} predicate set but value not extractable from {File} — skipping",
                                job.Name, rule.CheckRuleId, fileName);
                            continue;
                        }
                        if (!PredicateSatisfied(predicateType, primary, rule.ExtractorPredicateValue))
                            continue;   // value doesn't satisfy → no failure
                    }

                    // ── Identifier (SourceId), with filename fallback ─────────
                    string sourceId;
                    if (!string.IsNullOrWhiteSpace(rule.IdentifierLocator))
                    {
                        var id = await extractor.ExtractAsync(file, rule.IdentifierLocator, ct);
                        if (id is null)
                        {
                            sourceId = Path.GetFileNameWithoutExtension(file);
                            result.IdentifierExtractionFailures++;
                            logger.LogWarning(
                                "FileContentScan '{Job}': identifier extraction failed for {File} (rule {RuleId}, locator {Locator}) — using filename as SourceId",
                                job.Name, fileName, rule.CheckRuleId, rule.IdentifierLocator);
                        }
                        else sourceId = id;
                    }
                    else sourceId = Path.GetFileNameWithoutExtension(file);

                    var failure = new JobFailure
                    {
                        JobId          = 0,
                        JobTypeId      = job.JobTypeId,
                        MonitoredJobId = job.MonitoredJobId,
                        StepName       = fileName,                      // matches FS convention
                        SourceId       = sourceId,
                        ErrorMessage   = BuildMessage(rule, primary, fileName),
                        SourceLogPath  = file,                          // required; the data file is where detected
                        SourceFilePath = file,                          // the input file for {sourceFilePath}
                        Status         = JobStatus.Failed,
                        DetectedAt     = DateTime.Now,
                    };

                    failure = await jobRepo.SaveAsync(failure, ct);
                    created.Add(failure);
                    producedFailure = true;

                    logger.LogInformation(
                        "FileContentScan '{Job}': rule {RuleId} matched {File} — FailureId {FailureId} (SourceId={SourceId})",
                        job.Name, rule.CheckRuleId, fileName, failure.FailureId, sourceId);
                }
                catch (FileContentTooLargeException ex)
                {
                    result.OversizeFileSkips++;
                    logger.LogWarning(
                        "FileContentScan '{Job}': {File} skipped — {Size} bytes exceeds {Cap} byte cap",
                        job.Name, fileName, ex.SizeBytes, ex.CapBytes);
                    oversize = true;
                    break;   // can't parse this file — stop applying rules to it
                }
            }

            // Traceability: an examined (new/changed) file that matched rules but
            // produced no failure — predicate not satisfied, or extraction yielded
            // nothing. Info-level so operators can see why a specific file didn't
            // fire without enabling Debug. (Oversize already logged its own Warning.)
            if (!producedFailure && !oversize)
                logger.LogInformation(
                    "FileContentScan '{Job}': {File} processed, no failure produced (no matching rule satisfied)",
                    job.Name, fileName);

            // One watermark write per examined (new/changed) file, after all rules —
            // including oversize/no-match, so an unchanged file isn't reprocessed
            // (and an oversize file isn't re-counted) every tick.
            await watermarks.UpsertContentWatermarkAsync(job.MonitoredJobId, file, mtime, ct);
        }

        result.FailuresDetected = created.Count;
        if (created.Count == 0) return result;

        var classifications = await classify.ExecuteAsync(created, ct);
        result.Classifications = classifications.Count;

        await suggest.ExecuteAsync(classifications, ct);
        result.Recommendations = classifications.Count;

        return result;
    }

    /// <summary>
    /// "{rule.Description}: {primaryValue} (file: {filename})". Description falls
    /// back to "FileContent match" so the message is never empty; primaryValue is
    /// empty for pattern-only rules (acceptable). RuleBasedClassifier matches its
    /// patterns against this text, so the format is intentionally predictable.
    /// </summary>
    private static string BuildMessage(ScanCheckRule rule, string? primary, string fileName)
    {
        var desc = string.IsNullOrWhiteSpace(rule.Description) ? "FileContent match" : rule.Description.Trim();
        return $"{desc}: {primary} (file: {fileName})";
    }

    private static bool PredicateSatisfied(ScanPredicateType type, string? actual, string? expected)
    {
        actual   ??= string.Empty;
        expected ??= string.Empty;
        return type switch
        {
            ScanPredicateType.Equals      => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            ScanPredicateType.NotEquals   => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            ScanPredicateType.Contains    => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            ScanPredicateType.NotContains => !actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _                             => false,
        };
    }
}
