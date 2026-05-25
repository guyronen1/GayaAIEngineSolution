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

    public Severity Severity    { get; set; } = Severity.Medium;
    public string?  Description { get; set; }
    public bool     IsActive    { get; set; } = true;

    public MonitoredJob? MonitoredJob { get; set; }
}
