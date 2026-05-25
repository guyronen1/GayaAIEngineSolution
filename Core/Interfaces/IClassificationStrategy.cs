using MaiaAI.Core.Entities;
using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// Abstraction for classifying a job failure from its log content.
/// Swap in ML-based or LLM-based implementations without touching use cases.
/// </summary>
public interface IClassificationStrategy
{
    Task<ClassificationResult?> ClassifyAsync(
        JobFailure job,
        string logContent,
        CancellationToken ct = default);
}
