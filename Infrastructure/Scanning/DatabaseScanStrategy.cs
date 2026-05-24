using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Interfaces.UseCases;
using MaiaAI.Core.Results;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MaiaAI.Infrastructure.Scanning;

/// <summary>
/// Runs active database ScanCheckRules against their SourceTables.
/// Supports ColumnRange and ValueEquals checks.
/// When WatermarkColumn is set on a rule, each scan only reads rows whose
/// WatermarkColumn value exceeds the last stored watermark — so the same row
/// is never reported twice. The first scan processes all existing rows and
/// sets the watermark baseline.
/// </summary>
public sealed class DatabaseScanStrategy(
    IConfiguration                config,
    IJobRepository                jobRepo,
    IScanWatermarkRepository      watermarks,
    IClassifyJobsUseCase          classify,
    IGenerateSuggestionsUseCase   suggest,
    ILogger<DatabaseScanStrategy> logger) : IScanStrategy
{
    private static readonly HashSet<CheckType> SupportedTypes =
        [CheckType.ColumnRange, CheckType.ValueEquals];

    public ScanType ScanType => ScanType.Database;

    public async Task<ScanResult> ScanAsync(MonitoredJob job, CancellationToken ct = default)
    {
        var rules = job.ScanCheckRules
            .Where(r => r.IsActive && SupportedTypes.Contains(r.CheckType))
            .ToList();

        if (rules.Count == 0)
            throw new InvalidOperationException(
                $"Job '{job.Name}' has no active database ScanCheckRules. " +
                "Add rules with CheckType 'ColumnRange' or 'ValueEquals'.");

        var missingTable = rules.Where(r => string.IsNullOrWhiteSpace(r.SourceTable)).ToList();
        if (missingTable.Count > 0)
            throw new InvalidOperationException(
                $"Job '{job.Name}': {missingTable.Count} rule(s) have no SourceTable — " +
                $"rule IDs: {string.Join(", ", missingTable.Select(r => r.CheckRuleId))}.");

        var connStr = config.GetConnectionString(job.ConnectionName ?? "DefaultConnection");
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException(
                $"Connection string '{job.ConnectionName ?? "DefaultConnection"}' not found in configuration.");

        var result = new ScanResult
        {
            JobName  = job.Name,
            ScanType = ScanType.Database,
            Detail   = string.Join(", ", rules.Select(RuleDescription))
        };

        var created = new List<JobFailure>();

        foreach (var rule in rules)
        {
            if (!IsRuleValid(rule))
            {
                logger.LogWarning("ScanCheckRule {Id} on job '{Job}' is missing required value config — skipping",
                    rule.CheckRuleId, job.Name);
                continue;
            }

            // Get the current watermark for this rule (null = first scan ever)
            string? watermark = rule.WatermarkColumn is not null
                ? await watermarks.GetDbWatermarkAsync(rule.CheckRuleId, ct)
                : null;

            var rows = await QueryMatchingRowsAsync(connStr, rule.SourceTable!, rule, watermark, ct);

            // Always advance the watermark so the next scan starts after the highest value seen.
            // On first scan with no results we still set it to the current table max so we don't
            // re-scan historical rows on the next run.
            if (rule.WatermarkColumn is not null)
            {
                var newWatermark = rows.Count > 0
                    ? rows.Max(r => r.WatermarkValue ?? string.Empty)
                    : await QueryCurrentMaxAsync(connStr, rule.SourceTable!, rule.WatermarkColumn, ct);

                if (newWatermark is not null)
                    await watermarks.UpdateDbWatermarkAsync(rule.CheckRuleId, newWatermark, ct);
            }
            else if (rows.Count > 0)
            {
                // No watermark column configured — fall back to open-failure dedup
                if (await jobRepo.HasOpenFailureAsync(job.MonitoredJobId, rule.SourceTable!, rule.TargetField, ct))
                {
                    logger.LogDebug(
                        "DatabaseScan '{Job}': open failure already exists for [{Table}].[{Column}] — skipping",
                        job.Name, rule.SourceTable, rule.TargetField);
                    continue;
                }
            }

            if (rows.Count == 0) continue;

            foreach (var (rowKey, value, wmValue, srcValue) in rows)
            {
                var failure = new JobFailure
                {
                    JobId          = 0,
                    JobTypeId      = job.JobTypeId,
                    MonitoredJobId = job.MonitoredJobId,
                    StepName       = rule.SourceTable,
                    SourceId       = srcValue ?? rowKey,
                    ErrorMessage   = BuildRowMessage(rule, rowKey, value, wmValue, srcValue),
                    SourceLogPath  = $"db://{job.ConnectionName ?? "DefaultConnection"}/{rule.SourceTable}",
                    Status         = JobStatus.Failed,
                    DetectedAt     = DateTime.UtcNow,
                };

                failure = await jobRepo.SaveAsync(failure, ct);
                created.Add(failure);
            }

            logger.LogInformation(
                "DatabaseScan '{Job}': [{Table}].[{Column}] — {Count} row(s) matched rule {RuleId} ({CheckType})",
                job.Name, rule.SourceTable, rule.TargetField, rows.Count, rule.CheckRuleId, rule.CheckType);
        }

        result.FailuresDetected = created.Count;
        if (created.Count == 0) return result;

        var classifications = await classify.ExecuteAsync(created, ct);
        result.Classifications = classifications.Count;

        await suggest.ExecuteAsync(classifications, ct);
        result.Recommendations = classifications.Count;

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsRuleValid(ScanCheckRule rule) => rule.CheckType switch
    {
        CheckType.ColumnRange => rule.MinValue.HasValue || rule.MaxValue.HasValue,
        CheckType.ValueEquals => !string.IsNullOrWhiteSpace(rule.ExpectedValue),
        _                     => false
    };

    private static string RuleDescription(ScanCheckRule r) => r.CheckType switch
    {
        CheckType.ColumnRange => $"[{r.SourceTable}].[{r.TargetField}] ∈ [{r.MinValue?.ToString() ?? "−∞"}, {r.MaxValue?.ToString() ?? "+∞"}]",
        CheckType.ValueEquals => $"[{r.SourceTable}].[{r.TargetField}] = {r.ExpectedValue}",
        _                     => $"[{r.SourceTable}].[{r.TargetField}]"
    };

    private static string BuildRowMessage(ScanCheckRule rule, string rowKey, object value, string? wmValue, string? srcValue)
    {
        var rowId = srcValue is not null  ? $"{rule.SourceIdColumn}={srcValue}"
                  : wmValue  is not null  ? $"{rule.WatermarkColumn}={wmValue}"
                  : $"row#{rowKey}";

        return rule.CheckType switch
        {
            CheckType.ColumnRange =>
                $"[{rule.SourceTable}].[{rule.TargetField}] = {value} is outside " +
                $"range [{rule.MinValue?.ToString() ?? "−∞"}, {rule.MaxValue?.ToString() ?? "+∞"}] ({rowId})" +
                (rule.Description is not null ? $" — {rule.Description}" : ""),
            CheckType.ValueEquals =>
                $"[{rule.SourceTable}].[{rule.TargetField}] = {value} matches error value {rule.ExpectedValue} ({rowId})" +
                (rule.Description is not null ? $" — {rule.Description}" : ""),
            _ =>
                $"[{rule.SourceTable}].[{rule.TargetField}] = {value} ({rowId})"
        };
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    private static async Task<List<(string RowKey, object Value, string? WatermarkValue, string? SourceIdValue)>> QueryMatchingRowsAsync(
        string connStr, string sourceTable, ScanCheckRule rule, string? watermark, CancellationToken ct)
    {
        var (filterClause, filterParams) = BuildFilterClause(rule);
        var watermarkFilter = rule.WatermarkColumn is not null && watermark is not null
            ? $" AND [{rule.WatermarkColumn}] > @Watermark"
            : string.Empty;

        // WatermarkColumn and SourceIdColumn are extra columns selected for tracking and identity.
        // All column names and table come from admin config and are bracketed.
        // All filter values are always parameterised.
        var quotedTable = QuoteTable(sourceTable);
        var wmSelect  = rule.WatermarkColumn is not null
            ? $", CAST([{rule.WatermarkColumn}] AS NVARCHAR(100)) AS _WatermarkVal"
            : string.Empty;
        var srcSelect = rule.SourceIdColumn is not null
            ? $", CAST([{rule.SourceIdColumn}] AS NVARCHAR(100)) AS _SourceIdVal"
            : string.Empty;

        var orderBy = rule.WatermarkColumn is not null
            ? $"ORDER BY [{rule.WatermarkColumn}] ASC"
            : "ORDER BY (SELECT NULL)";

        var sql = $"""
            SELECT TOP 500
                CAST(ROW_NUMBER() OVER ({orderBy}) AS NVARCHAR(20)) AS _RowKey,
                [{rule.TargetField}]
                {wmSelect}
                {srcSelect}
            FROM {quotedTable}
            WHERE {filterClause}{watermarkFilter}
            """;

        var rows = new List<(string, object, string?, string?)>();
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        foreach (var (name, value) in filterParams)
            cmd.Parameters.AddWithValue(name, value);
        if (watermarkFilter.Length > 0)
            cmd.Parameters.AddWithValue("@Watermark", watermark!);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var rowKey  = reader.GetString(0);
            var val     = reader.IsDBNull(1) ? (object)"NULL" : reader.GetValue(1);
            var nextCol = 2;
            var wmVal   = rule.WatermarkColumn is not null
                ? (!reader.IsDBNull(nextCol++) ? reader.GetString(nextCol - 1) : null)
                : null;
            var srcVal  = rule.SourceIdColumn is not null
                ? (!reader.IsDBNull(nextCol)   ? reader.GetString(nextCol)     : null)
                : null;
            rows.Add((rowKey, val, wmVal, srcVal));
        }

        return rows;
    }

    private static async Task<string?> QueryCurrentMaxAsync(
        string connStr, string sourceTable, string watermarkColumn, CancellationToken ct)
    {
        var sql = $"SELECT CAST(MAX([{watermarkColumn}]) AS NVARCHAR(100)) FROM {QuoteTable(sourceTable)}";
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd    = new SqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull || result is null ? null : result.ToString();
    }

    /// <summary>
    /// Converts "dbo.Files" → "[dbo].[Files]", or "Files" → "[Files]".
    /// Prevents the bracketing bug where [dbo.Files] is treated as a literal name.
    /// </summary>
    private static string QuoteTable(string sourceTable)
    {
        var parts = sourceTable.Split('.');
        return string.Join(".", parts.Select(p => $"[{p.Trim('[', ']')}]"));
    }

    private static (string Clause, List<(string Name, object Value)> Params) BuildFilterClause(ScanCheckRule rule)
    {
        var p = new List<(string, object)>();
        if (rule.CheckType == CheckType.ValueEquals)
        {
            p.Add(("@ExactVal", rule.ExpectedValue!));
            return ($"[{rule.TargetField}] = @ExactVal", p);
        }
        // ColumnRange
        var conditions = new List<string>();
        if (rule.MinValue.HasValue) { conditions.Add($"[{rule.TargetField}] < @Min"); p.Add(("@Min", rule.MinValue.Value)); }
        if (rule.MaxValue.HasValue) { conditions.Add($"[{rule.TargetField}] > @Max"); p.Add(("@Max", rule.MaxValue.Value)); }
        return (string.Join(" OR ", conditions), p);
    }
}
