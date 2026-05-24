using MaiaAI.Core.Entities;

namespace MaiaAI.Core.Interfaces;

public interface IOperatorActionRepository
{
    Task SaveAsync(OperatorAction action, CancellationToken ct = default);
}
