namespace MaiaAI.Core.Interfaces;

public interface IScanWatermarkRepository
{
    // ── File watermarks ──────────────────────────────────────────────────────
    /// <summary>Returns the last byte offset read for this file, or 0 if never scanned.</summary>
    Task<long> GetFileOffsetAsync(int monitoredJobId, string filePath, CancellationToken ct = default);
    Task UpdateFileOffsetAsync(int monitoredJobId, string filePath, long byteOffset, CancellationToken ct = default);

    // ── Database watermarks ──────────────────────────────────────────────────
    /// <summary>Returns the last watermark value seen for this rule, or null if never scanned.</summary>
    Task<string?> GetDbWatermarkAsync(int checkRuleId, CancellationToken ct = default);
    Task UpdateDbWatermarkAsync(int checkRuleId, string watermarkValue, CancellationToken ct = default);
}
