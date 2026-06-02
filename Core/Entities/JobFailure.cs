using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Entities;

public class JobFailure
{
    public int FailureId { get; set; }
    public int JobId { get; set; }
    public int JobTypeId { get; set; }
    public int? ErrorTypeId { get; set; }
    public int? MonitoredJobId { get; set; }
    public string? StepName { get; set; }
    public string? SourceId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime DetectedAt { get; set; }

    /// <summary>Where the error was DETECTED — log file for FS scans,
    /// "db://..." pseudo-URI for DB scans. Required; legacy field.</summary>
    public required string SourceLogPath { get; set; }

    /// <summary>The INPUT file the failing process was operating on.
    /// Distinct semantic from SourceLogPath: log is "where the error message
    /// appeared," SourceFilePath is "what the process was reading/writing
    /// when it failed." Populated only when the matching scan rule configures
    /// extraction (ScanCheckRule.InputPathPattern for FS, .FilePathColumn for
    /// DB). NULL when no configuration is present — operators using
    /// {sourceFilePath} placeholder against a null value get a specific
    /// error from PlaceholderResolver pointing at the config to add.</summary>
    public string? SourceFilePath { get; set; }

    public JobStatus Status { get; set; }

    public JobType? JobType { get; set; }
    public ErrorType? ErrorType { get; set; }
    public MonitoredJob? MonitoredJob { get; set; }
    public ICollection<AiRecommendation> Recommendations { get; set; } = [];
    public ICollection<FixExecutionLog> FixExecutionLogs { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}
