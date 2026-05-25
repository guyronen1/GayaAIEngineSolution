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
    public required string SourceLogPath { get; set; }
    public JobStatus Status { get; set; }

    public JobType? JobType { get; set; }
    public ErrorType? ErrorType { get; set; }
    public MonitoredJob? MonitoredJob { get; set; }
    public ICollection<AiRecommendation> Recommendations { get; set; } = [];
    public ICollection<FixExecutionLog> FixExecutionLogs { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}
