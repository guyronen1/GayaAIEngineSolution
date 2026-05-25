using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// Strategy for executing a specific category of fix.
/// Register one implementation per FixCategory; DefaultFixEngine dispatches to the matching handler.
/// </summary>
public interface IFixHandler
{
    FixCategory Category { get; }
    Task<bool> HandleAsync(AiRecommendation recommendation, CancellationToken ct = default);
}
