using MaiaAI.Core.Entities;
using MaiaAI.Core.Results;

namespace AIEngineAPI.Contracts;

public sealed record RecommendationDto(
    int      RecommendationId,
    int      FailureId,
    string   SuggestedAction,
    string   FixCategory,
    decimal  ConfidenceScore,
    string?  Explanation,
    bool     AutoFixAvailable,
    bool?    OperatorApproved,
    bool     IsExecuted,
    DateTime RecommendedAt,
    string?  ErrorTypeCode,
    int      ErrorTypeId,
    int?     JobTypeId,
    int?     FixPolicyRuleId,
    bool?    PolicyIsAutoHealEligible)
{
    public static RecommendationDto From(
        AiRecommendation r,
        int?             fixPolicyRuleId         = null,
        bool?            policyIsAutoHealEligible = null) => new(
        r.RecommendationId,
        r.FailureId,
        r.SuggestedAction,
        r.FixCategory.ToString(),
        r.ConfidenceScore,
        r.Explanation,
        r.AutoFixAvailable,
        r.OperatorApproved,
        r.IsExecuted,
        r.RecommendedAt,
        r.ErrorType?.Code,
        r.ErrorTypeId,
        r.Failure?.JobTypeId,
        fixPolicyRuleId,
        policyIsAutoHealEligible);

    public static RecommendationDto From(RecommendationListItem item) =>
        From(item.Recommendation, item.FixPolicyRuleId, item.PolicyIsAutoHealEligible);
}
