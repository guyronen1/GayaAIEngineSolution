using MaiaAI.Core.Entities;

namespace MaiaAI.Core.Interfaces;

public interface IFixLogRepository
{
    Task SaveAsync(FixExecutionLog log, CancellationToken ct = default);
}
