using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Entities;

/// <summary>
/// One check rule within a MonitoredJob scan.
/// A job can have many rules — e.g. check column A AND column B for a Database scan.
/// </summary>
public class ScanCheckRule
{
    public int CheckRuleId    { get; set; }
    public int MonitoredJobId { get; set; }

    public CheckType CheckType { get; set; }

    /// <summary>SQL table to query for ColumnRange checks, e.g. "dbo.Orders".</summary>
    public string? SourceTable { get; set; }

    /// <summary>
    /// Column name (ColumnRange), keyword phrase (ErrorKeyword),
    /// or response field/path (ResponseContains).
    /// </summary>
    public required string TargetField { get; set; }

    /// <summary>Lower bound for ColumnRange — rows below this are flagged.</summary>
    public decimal? MinValue { get; set; }

    /// <summary>Upper bound for ColumnRange — rows above this are flagged.</summary>
    public decimal? MaxValue { get; set; }

    /// <summary>Expected value for StatusCode or equality checks.</summary>
    public string? ExpectedValue { get; set; }

    /// <summary>
    /// Column used as a scan cursor for database rules (e.g. "CreatedAt", "UpdateDate").
    /// When set, each scan only reads rows newer than the last watermark value,
    /// so the same row is never reported twice. Leave null to scan all rows every time.
    /// </summary>
    public string? WatermarkColumn { get; set; }

    /// <summary>
    /// Column holding the row's unique identity (PK or unique key, e.g. "Id", "FileGuid").
    /// Stored as JobFailure.SourceId so the fix executor can act on the exact row.
    /// </summary>
    public string? SourceIdColumn { get; set; }

    /// <summary>
    /// Database scans only. Column on the source row that holds the input
    /// file path. Read alongside the rule's check and stuffed into
    /// JobFailure.SourceFilePath so {sourceFilePath}-using fix steps can
    /// act on the file the failing process was operating on.
    ///
    /// v1: no join logic; if the path lives on a related table the
    /// operator puts the JOIN into SourceTable directly.
    /// </summary>
    public string? FilePathColumn { get; set; }

    /// <summary>
    /// FileSystem scans only. Regex with capture group #1 = input file path
    /// extracted from the error line. Compiled with 50ms timeout. NULL =
    /// FS scan leaves JobFailure.SourceFilePath null for this rule.
    ///
    /// Distinct DSL from the wildcard-style classification patterns —
    /// full regex applies here because capture groups are required.
    /// </summary>
    public string? InputPathPattern { get; set; }

    public Severity Severity    { get; set; } = Severity.Medium;
    public string?  Description { get; set; }
    public bool     IsActive    { get; set; } = true;

    public MonitoredJob? MonitoredJob { get; set; }
}
