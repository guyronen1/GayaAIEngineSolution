using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MaiaAI.Infrastructure.DataAccess.Repositories;

public sealed class SqlScanWatermarkRepository(IDbContextFactory<AiDbContext> factory)
    : IScanWatermarkRepository
{
    // ── File watermarks ──────────────────────────────────────────────────────

    public async Task<long> GetFileOffsetAsync(int monitoredJobId, string filePath, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanFileWatermarks
            .FirstOrDefaultAsync(w => w.MonitoredJobId == monitoredJobId && w.FilePath == filePath, ct);
        return wm?.ByteOffset ?? 0;
    }

    public async Task UpdateFileOffsetAsync(int monitoredJobId, string filePath, long byteOffset, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanFileWatermarks
            .FirstOrDefaultAsync(w => w.MonitoredJobId == monitoredJobId && w.FilePath == filePath, ct);

        if (wm is null)
            db.ScanFileWatermarks.Add(new ScanFileWatermark
            {
                MonitoredJobId = monitoredJobId,
                FilePath       = filePath,
                ByteOffset     = byteOffset,
                LastScannedAt  = DateTime.Now,
            });
        else
        {
            wm.ByteOffset    = byteOffset;
            wm.LastScannedAt = DateTime.Now;
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Database watermarks ──────────────────────────────────────────────────

    public async Task<string?> GetDbWatermarkAsync(int checkRuleId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanDbWatermarks
            .FirstOrDefaultAsync(w => w.CheckRuleId == checkRuleId, ct);
        return wm?.WatermarkValue;
    }

    public async Task UpdateDbWatermarkAsync(int checkRuleId, string watermarkValue, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wm = await db.ScanDbWatermarks
            .FirstOrDefaultAsync(w => w.CheckRuleId == checkRuleId, ct);

        if (wm is null)
            db.ScanDbWatermarks.Add(new ScanDbWatermark
            {
                CheckRuleId    = checkRuleId,
                WatermarkValue = watermarkValue,
                LastScannedAt  = DateTime.Now,
            });
        else
        {
            wm.WatermarkValue = watermarkValue;
            wm.LastScannedAt  = DateTime.Now;
        }

        await db.SaveChangesAsync(ct);
    }
}
