using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Entities;

public class AiRecommendation
{
    public int RecommendationId { get; set; }
    public int FailureId { get; set; }
    public int ErrorTypeId { get; set; }
    public required string SuggestedAction { get; set; }
    public FixCategory FixCategory { get; set; }
    public decimal ConfidenceScore { get; set; }
    public string? Explanation { get; set; }
    public DateTime RecommendedAt { get; set; }
    public bool AutoFixAvailable { get; set; }
    public bool? OperatorApproved { get; set; }
    public bool IsExecuted { get; set; }

    public JobFailure? Failure { get; set; }
    public ErrorType? ErrorType { get; set; }
    public ICollection<OperatorAction> OperatorActions { get; set; } = [];
}
