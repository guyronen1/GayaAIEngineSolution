using MaiaAI.Core.Enums;

namespace MaiaAI.Core.Results;

public sealed class ScanResult
{
    public required string   JobName          { get; init; }
    public required ScanType ScanType         { get; init; }
    public int               FailuresDetected { get; set; }
    public int               Classifications  { get; set; }
    public int               Recommendations  { get; set; }
    public string?           Detail           { get; set; }
}
