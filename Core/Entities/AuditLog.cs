namespace MaiaAI.Core.Entities;

public class AuditLog
{
    public int AuditId { get; set; }
    public int FailureId { get; set; }
    public required string EventType { get; set; }
    public required string Actor { get; set; }
    public string? Detail { get; set; }
    public DateTime Timestamp { get; set; }

    public JobFailure? Failure { get; set; }
}
