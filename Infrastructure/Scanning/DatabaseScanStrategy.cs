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
    ISqlQueryRunner               sqlRunner,
    ILogger<DatabaseScanStrategy> logger) : IScanStrategy
{
    private static readonly HashSet<CheckType> SupportedTypes =
        [CheckType.ColumnRange, CheckType.ValueEquals, CheckType.SqlQuery];

    // Code-side cap for SqlQuery (can't inject TOP into an arbitrary query/proc).
    private const int MaxSqlQueryRows = 500;

    public ScanType ScanType => ScanType.Database;

    public async Task<ScanResult> ScanAsync(MonitoredJob job, ScanSource source, CancellationToken ct = default)
    {
        var rules = source.ScanCheckRules
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

        var connStr = config.GetConnectionString(source.ConnectionName ?? "DefaultConnection");
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException(
                $"Connection string '{source.ConnectionName ?? "DefaultConnection"}' not found in configuration.");

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

            // Short, stable label used for BOTH the failure's StepName (nvarchar(200))
            // and the no-watermark dedup key. For SqlQuery the SourceTable IS the
            // (possibly multi-line, multi-KB) query, so it can't serve as either —
            // use the rule Description or a per-rule label. For table rules this is
            // just the table name, exactly as before.
            var stepName = rule.CheckType == CheckType.SqlQuery
                ? (string.IsNullOrWhiteSpace(rule.Description) ? $"SqlQuery #{rule.CheckRuleId}" : rule.Description!)
                : rule.SourceTable!;
            var conn = source.ConnectionName ?? "DefaultConnection";
            var sourceLogPath = rule.CheckType == CheckType.SqlQuery
                ? $"db://{conn}/query"
                : $"db://{conn}/{rule.SourceTable}";

            // Get the current watermark for this rule (null = first scan ever).
            // SqlQuery rules carry no WatermarkColumn (deferred), so this is null.
            string? watermark = rule.WatermarkColumn is not null
                ? await watermarks.GetDbWatermarkAsync(rule.CheckRuleId, ct)
                : null;

            var rows = rule.CheckType == CheckType.SqlQuery
                ? await QuerySqlAsync(connStr, rule, ct)
                : await QueryMatchingRowsAsync(connStr, rule.SourceTable!, rule, watermark, ct);

            if (rule.CheckType == CheckType.SqlQuery && rows.Count >= MaxSqlQueryRows)
                logger.LogWarning(
                    "DatabaseScan '{Job}': SqlQuery rule {RuleId} hit the {Cap}-row cap — results may be truncated.",
                    job.Name, rule.CheckRuleId, MaxSqlQueryRows);

            // Advance the watermark to the highest WatermarkColumn value seen this scan.
            // When zero rows matched, take MAX over rows that satisfy the rule's filter —
            // NOT MAX over the whole table, because a future-dated healthy row would jump
            // the watermark past any current-dated unhealthy row inserted next.
            // (SqlQuery has no watermark, so this whole block is skipped.)
            if (rule.WatermarkColumn is not null)
            {
                var newWatermark = rows.Count > 0
                    ? rows.Max(r => r.WatermarkValue ?? string.Empty)
                    : await QueryFilteredMaxAsync(connStr, rule.SourceTable!, rule.WatermarkColumn, rule, ct);

                if (newWatermark is not null)
                    await watermarks.UpdateDbWatermarkAsync(rule.CheckRuleId, newWatermark, ct);
            }
            else if (rows.Count > 0)
            {
                // No watermark column configured — fall back to open-failure dedup
                // keyed on StepName (table name for table rules, the SqlQuery label
                // for SqlQuery rules).
                if (await jobRepo.HasOpenFailureAsync(job.MonitoredJobId, stepName, rule.TargetField, ct))
                {
                    logger.LogDebug(
                        "DatabaseScan '{Job}': open failure already exists for '{Step}' — skipping",
                        job.Name, stepName);
                    continue;
                }
            }

            if (rows.Count == 0) continue;

            foreach (var (rowKey, value, wmValue, srcValue, filePathValue) in rows)
            {
                var failure = new JobFailure
                {
                    JobId          = 0,
                    JobTypeId      = job.JobTypeId,          // identity from the job
                    MonitoredJobId = job.MonitoredJobId,
                    ScanSourceId   = source.ScanSourceId,    // which source produced it
                    StepName       = stepName,
                    SourceId       = srcValue ?? rowKey,
                    ErrorMessage   = BuildRowMessage(rule, rowKey, value, wmValue, srcValue),
                    SourceLogPath  = sourceLogPath,
                    SourceFilePath = filePathValue,   // null when rule.FilePathColumn unset
                    Status         = JobStatus.Failed,
                    DetectedAt     = DateTime.Now,
                };

                failure = await jobRepo.SaveAsync(failure, ct);
                created.Add(failure);
            }

            logger.LogInformation(
                "DatabaseScan '{Job}': {Step} — {Count} row(s) matched rule {RuleId} ({CheckType})",
                job.Name, stepName, rows.Count, rule.CheckRuleId, rule.CheckType);
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
        // SqlQuery: the query (SourceTable) + the value column (TargetField) are all
        // that's needed — Option A, every returned row is a failure (no predicate).
        CheckType.SqlQuery    => !string.IsNullOrWhiteSpace(rule.SourceTable) && !string.IsNullOrWhiteSpace(rule.TargetField),
        _                     => false
    };

    private static string RuleDescription(ScanCheckRule r) => r.CheckType switch
    {
        CheckType.ColumnRange => $"[{r.SourceTable}].[{r.TargetField}] ∈ [{r.MinValue?.ToString() ?? "−∞"}, {r.MaxValue?.ToString() ?? "+∞"}]",
        CheckType.ValueEquals => $"[{r.SourceTable}].[{r.TargetField}] = {r.ExpectedValue}",
        CheckType.SqlQuery    => $"SqlQuery → [{r.TargetField}]",
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
            // Predictable, classifier-matchable shape; Description leads when set.
            CheckType.SqlQuery =>
                $"{rule.Description ?? "SqlQuery match"}: [{rule.TargetField}] = {value} ({rowId})",
            _ =>
                $"[{rule.SourceTable}].[{rule.TargetField}] = {value} ({rowId})"
        };
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// CheckType.SqlQuery path. Runs the operator's query/EXEC verbatim through
    /// the ISqlQueryRunner seam and projects each returned row into the shared
    /// row tuple. EVERY returned row is a failure — Option A: the operator's
    /// WHERE/JOIN is the filter, there is no extra predicate. TargetField names
    /// the value column shown in the message; SourceIdColumn (optional) names the
    /// SourceId column. Columns are read BY NAME because the result shape is
    /// operator-defined.
    /// </summary>
    private async Task<List<(string RowKey, object Value, string? WatermarkValue, string? SourceIdValue, string? FilePathValue)>> QuerySqlAsync(
        string connStr, ScanCheckRule rule, CancellationToken ct)
    {
        var resultRows = await sqlRunner.ExecuteAsync(connStr, rule.SourceTable!, MaxSqlQueryRows, ct);
        var rows = new List<(string, object, string?, string?, string?)>(resultRows.Count);

        var rowIndex = 0;
        foreach (var row in resultRows)
        {
            rowIndex++;

            // Missing TargetField is a config error affecting every row — fail the
            // scan with a clear, actionable message rather than silently producing
            // nothing. The worker records it as a Failed scan-run for this source.
            if (!row.TryGetValue(rule.TargetField, out var targetVal))
                throw new InvalidOperationException(
                    $"SqlQuery rule {rule.CheckRuleId}: result set has no column '{rule.TargetField}' (TargetField). " +
                    $"Columns returned: {(row.Keys.Any() ? string.Join(", ", row.Keys) : "(none)")}.");

            var value = targetVal ?? "NULL";

            // SourceIdColumn optional; absent/empty/null → fall back to row index
            // downstream via `srcValue ?? rowKey`.
            string? srcVal = null;
            if (!string.IsNullOrWhiteSpace(rule.SourceIdColumn)
                && row.TryGetValue(rule.SourceIdColumn!, out var sv) && sv is not null)
                srcVal = sv.ToString();

            // WatermarkValue + FilePathValue are unused for SqlQuery v1.
            rows.Add((rowIndex.ToString(), value, null, srcVal, null));
        }

        return rows;
    }

    private static async Task<List<(string RowKey, object Value, string? WatermarkValue, string? SourceIdValue, string? FilePathValue)>> QueryMatchingRowsAsync(
        string connStr, string sourceTable, ScanCheckRule rule, string? watermark, CancellationToken ct)
    {
        var (filterClause, filterParams) = BuildFilterClause(rule);
        var watermarkFilter = rule.WatermarkColumn is not null && watermark is not null
            ? $" AND [{rule.WatermarkColumn}] > @Watermark"
            : string.Empty;

        // WatermarkColumn / SourceIdColumn / FilePathColumn are extra columns
        // projected for tracking, identity, and composite-fix path capture.
        // All column names and the table come from admin config and are bracketed.
        // All filter values are always parameterised.
        // FilePathColumn supports a dotted "alias.Column" form (rare) — bracket
        // only the column portion so a JOIN encoded in SourceTable still works.
        var quotedTable = QuoteTable(sourceTable);
        // Style 121 = ISO `yyyy-mm-dd hh:mi:ss.fffffff` (full datetime2 precision).
        // For non-date columns the style is silently ignored and you get the default text form.
        var wmSelect  = rule.WatermarkColumn is not null
            ? $", CONVERT(NVARCHAR(50), [{rule.WatermarkColumn}], 121) AS _WatermarkVal"
            : string.Empty;
        var srcSelect = rule.SourceIdColumn is not null
            ? $", CAST([{rule.SourceIdColumn}] AS NVARCHAR(100)) AS _SourceIdVal"
            : string.Empty;
        var fpSelect  = !string.IsNullOrEmpty(rule.FilePathColumn)
            ? $", CAST({QuoteColumnRef(rule.FilePathColumn)} AS NVARCHAR(500)) AS _FilePathVal"
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
                {fpSelect}
            FROM {quotedTable}
            WHERE {filterClause}{watermarkFilter}
            """;

        var rows = new List<(string, object, string?, string?, string?)>();
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
                ? (!reader.IsDBNull(nextCol++) ? reader.GetString(nextCol - 1) : null)
                : null;
            var fpVal   = !string.IsNullOrEmpty(rule.FilePathColumn)
                ? (!reader.IsDBNull(nextCol)   ? reader.GetString(nextCol)     : null)
                : null;
            rows.Add((rowKey, val, wmVal, srcVal, fpVal));
        }

        return rows;
    }

    /// <summary>
    /// Bracket a single column reference, supporting an optional "alias.Column"
    /// form used when the operator put a JOIN into SourceTable. Examples:
    ///   "FilePath"       → "[FilePath]"
    ///   "j.FilePath"     → "j.[FilePath]"
    /// Only the column part is bracketed so the alias resolves naturally.
    /// </summary>
    private static string QuoteColumnRef(string columnRef)
    {
        var dot = columnRef.LastIndexOf('.');
        return dot < 0
            ? $"[{columnRef}]"
            : $"{columnRef[..dot]}.[{columnRef[(dot + 1)..]}]";
    }

    /// <summary>
    /// MAX(watermark) over rows that satisfy the rule's filter clause.
    /// Used as the baseline when a scan returns zero matching rows, so the watermark only
    /// ever advances within the population the rule would actually report on.
    /// </summary>
    private static async Task<string?> QueryFilteredMaxAsync(
        string connStr, string sourceTable, string watermarkColumn, ScanCheckRule rule, CancellationToken ct)
    {
        var (filterClause, filterParams) = BuildFilterClause(rule);
        var sql = $"SELECT CONVERT(NVARCHAR(50), MAX([{watermarkColumn}]), 121) " +
                  $"FROM {QuoteTable(sourceTable)} WHERE {filterClause}";

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in filterParams)
            cmd.Parameters.AddWithValue(name, value);

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
            return ($"([{rule.TargetField}] = @ExactVal)", p);
        }
        // ColumnRange — wrap in parentheses so callers can safely AND
        // additional conditions onto this clause without the precedence bug
        // where (a OR b) AND c parses as a OR (b AND c). The watermark filter
        // in QueryMatchingRowsAsync is exactly that "additional AND" case;
        // unparenthesized, it would bypass the OR's left branch entirely.
        var conditions = new List<string>();
        if (rule.MinValue.HasValue) { conditions.Add($"[{rule.TargetField}] < @Min"); p.Add(("@Min", rule.MinValue.Value)); }
        if (rule.MaxValue.HasValue) { conditions.Add($"[{rule.TargetField}] > @Max"); p.Add(("@Max", rule.MaxValue.Value)); }
        return ($"({string.Join(" OR ", conditions)})", p);
    }
}
