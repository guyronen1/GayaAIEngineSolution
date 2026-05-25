namespace MaiaAI.Core.Results;

public sealed class ClassificationResult
{
    public int FailureId { get; init; }
    public int JobId { get; init; }
    public int JobTypeId { get; init; }
    public int ErrorTypeId { get; init; }
    public required string ErrorTypeCode { get; init; }
    public required string RawError { get; init; }
    public double Confidence { get; init; }
}
