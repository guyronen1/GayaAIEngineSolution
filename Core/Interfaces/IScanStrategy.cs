using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Results;

namespace MaiaAI.Core.Interfaces;

/// <summary>
/// One implementation per ScanType.
/// Registered as IEnumerable&lt;IScanStrategy&gt; — consumers pick by ScanType.
/// </summary>
public interface IScanStrategy
{
    ScanType ScanType { get; }

    Task<ScanResult> ScanAsync(MonitoredJob job, CancellationToken ct = default);
}
