using MaiaAI.Core.Entities;

namespace MaiaAI.Core.Interfaces;

public interface IAuditRepository
{
    Task WriteAsync(AuditLog audit, CancellationToken ct = default);
}
