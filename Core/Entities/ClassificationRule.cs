namespace MaiaAI.Core.Entities;

public class ClassificationRule
{
    public int RuleId { get; set; }
    public int JobTypeId { get; set; }
    public int ErrorTypeId { get; set; }
    public required string Pattern { get; set; }
    public decimal Confidence { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }

    public JobType? JobType { get; set; }
    public ErrorType? ErrorType { get; set; }
    public ICollection<MonitoredJobRule> MonitoredJobRules { get; set; } = [];
}
