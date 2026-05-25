using AIEngineAPI.Contracts;
using MaiaAI.Core.Entities;
using MaiaAI.Core.Enums;
using MaiaAI.Core.Interfaces;
using MaiaAI.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AIEngineAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController(
    IMonitoredJobRepository        jobRepo,
    IClassificationRuleRepository  ruleRepo,
    IDbContextFactory<AiDbContext> dbFactory) : ControllerBase
{
    // ── Lookup data ──────────────────────────────────────────────────────────

    [HttpGet("job-types")]
    public async Task<IActionResult> GetJobTypes(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var types = await db.JobTypes.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync(ct);
        return Ok(types.Select(t => new { t.JobTypeId, t.Name, t.Description }));
    }

    [HttpGet("error-types")]
    public async Task<IActionResult> GetErrorTypes(
        [FromQuery] bool includeInactive = false,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = includeInactive ? db.ErrorTypes.AsQueryable() : db.ErrorTypes.Where(t => t.IsActive);
        var types = await q.OrderBy(t => t.Code).ToListAsync(ct);
        return Ok(types.Select(t => new
        {
            t.ErrorTypeId, t.Code, t.DisplayName, t.Description,
            Severity = t.Severity.ToString(),
            t.IsActive,
        }));
    }

    [HttpPost("error-types")]
    public async Task<IActionResult> CreateErrorType([FromBody] UpsertErrorTypeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code))        return BadRequest(new { Message = "Code is required." });
        if (string.IsNullOrWhiteSpace(req.DisplayName)) return BadRequest(new { Message = "DisplayName is required." });
        if (!Enum.TryParse<Severity>(req.Severity, ignoreCase: true, out var severity))
            return BadRequest(new { Message = $"Unknown Severity '{req.Severity}'. Expected: Low, Medium, High, Critical." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.ErrorTypes.AnyAsync(t => t.Code == req.Code, ct))
            return Conflict(new { Message = $"ErrorType with Code '{req.Code}' already exists." });

        var et = new ErrorType
        {
            Code        = req.Code,
            DisplayName = req.DisplayName,
            Description = req.Description,
            Severity    = severity,
            IsActive    = req.IsActive,
        };
        db.ErrorTypes.Add(et);
        await db.SaveChangesAsync(ct);
        return Ok(new { et.ErrorTypeId });
    }

    [HttpPut("error-types/{id:int}")]
    public async Task<IActionResult> UpdateErrorType(int id, [FromBody] UpsertErrorTypeRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<Severity>(req.Severity, ignoreCase: true, out var severity))
            return BadRequest(new { Message = $"Unknown Severity '{req.Severity}'. Expected: Low, Medium, High, Critical." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var et = await db.ErrorTypes.FindAsync([id], ct);
        if (et is null) return NotFound();

        // Code is the natural key — block accidental collisions on rename
        if (!string.Equals(et.Code, req.Code, StringComparison.Ordinal)
            && await db.ErrorTypes.AnyAsync(t => t.Code == req.Code && t.ErrorTypeId != id, ct))
            return Conflict(new { Message = $"ErrorType with Code '{req.Code}' already exists." });

        et.Code        = req.Code;
        et.DisplayName = req.DisplayName;
        et.Description = req.Description;
        et.Severity    = severity;
        et.IsActive    = req.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("error-types/{id:int}")]
    public async Task<IActionResult> DeleteErrorType(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var et = await db.ErrorTypes.FindAsync([id], ct);
        if (et is null) return NotFound();

        // Soft delete — referenced from JobFailures / ClassificationRules / FixPolicyRules / AIRecommendations
        // with RESTRICT FKs. A hard DELETE would fail; flipping IsActive is the right primitive.
        et.IsActive = false;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Monitored Jobs ───────────────────────────────────────────────────────

    [HttpGet("monitored-jobs")]
    public async Task<IActionResult> GetAllJobs(CancellationToken ct)
    {
        var jobs = await jobRepo.GetAllWithRulesAsync(ct);
        return Ok(jobs.Select(MonitoredJobDto.From));
    }

    [HttpPost("monitored-jobs")]
    public async Task<IActionResult> CreateJob([FromBody] UpsertMonitoredJobRequest req, CancellationToken ct)
    {
        var job = new MonitoredJob
        {
            Name                   = req.Name,
            DisplayName            = req.DisplayName,
            JobTypeId              = req.JobTypeId,
            ScanTypeId             = req.ScanTypeId,
            LogFolder              = req.LogFolder,
            SearchPatterns         = req.SearchPatterns,
            ConnectionName         = req.ConnectionName,
            LogSourceUrl           = req.LogSourceUrl,
            PollingIntervalSeconds = req.PollingIntervalSeconds,
            IsActive               = req.IsActive,
            Description            = req.Description,
            CreatedAt              = DateTime.Now,
        };
        var saved = await jobRepo.SaveAsync(job, ct);
        return Ok(new { saved.MonitoredJobId });
    }

    [HttpPut("monitored-jobs/{id:int}")]
    public async Task<IActionResult> UpdateJob(int id, [FromBody] UpsertMonitoredJobRequest req, CancellationToken ct)
    {
        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null) return NotFound();

        job.Name                   = req.Name;
        job.DisplayName            = req.DisplayName;
        job.JobTypeId              = req.JobTypeId;
        job.ScanTypeId             = req.ScanTypeId;
        job.LogFolder              = req.LogFolder;
        job.SearchPatterns         = req.SearchPatterns;
        job.ConnectionName         = req.ConnectionName;
        job.LogSourceUrl           = req.LogSourceUrl;
        job.PollingIntervalSeconds = req.PollingIntervalSeconds;
        job.IsActive               = req.IsActive;
        job.Description            = req.Description;

        await jobRepo.UpdateAsync(job, ct);
        return NoContent();
    }

    [HttpDelete("monitored-jobs/{id:int}")]
    public async Task<IActionResult> DeleteJob(int id, CancellationToken ct)
    {
        await jobRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    // ── Scan Check Rules ─────────────────────────────────────────────────────

    [HttpPost("monitored-jobs/{jobId:int}/scan-rules")]
    public async Task<IActionResult> CreateScanRule(int jobId, [FromBody] UpsertScanCheckRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = new ScanCheckRule
        {
            MonitoredJobId  = jobId,
            CheckType       = Enum.Parse<CheckType>(req.CheckType),
            SourceTable     = req.SourceTable,
            TargetField     = req.TargetField,
            MinValue        = req.MinValue,
            MaxValue        = req.MaxValue,
            ExpectedValue   = req.ExpectedValue,
            WatermarkColumn = req.WatermarkColumn,
            SourceIdColumn  = req.SourceIdColumn,
            Severity        = Enum.Parse<Severity>(req.Severity),
            Description     = req.Description,
            IsActive        = true,
        };
        db.ScanCheckRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return Ok(new { rule.CheckRuleId });
    }

    [HttpPut("scan-rules/{id:int}")]
    public async Task<IActionResult> UpdateScanRule(int id, [FromBody] UpsertScanCheckRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ScanCheckRules.FindAsync([id], ct);
        if (rule is null) return NotFound();

        rule.CheckType       = Enum.Parse<CheckType>(req.CheckType);
        rule.SourceTable     = req.SourceTable;
        rule.TargetField     = req.TargetField;
        rule.MinValue        = req.MinValue;
        rule.MaxValue        = req.MaxValue;
        rule.ExpectedValue   = req.ExpectedValue;
        rule.WatermarkColumn = req.WatermarkColumn;
        rule.SourceIdColumn  = req.SourceIdColumn;
        rule.Severity        = Enum.Parse<Severity>(req.Severity);
        rule.Description     = req.Description;
        rule.IsActive        = req.IsActive;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("scan-rules/{id:int}")]
    public async Task<IActionResult> DeleteScanRule(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ScanCheckRules.FindAsync([id], ct);
        if (rule is null) return NotFound();
        rule.IsActive = false;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Per-job Classification Rules ────────────────────────────────────────

    [HttpPost("monitored-jobs/{jobId:int}/classification-rules")]
    public async Task<IActionResult> CreateJobClassificationRule(
        int jobId, [FromBody] UpsertJobClassificationRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.MonitoredJobs.FindAsync([jobId], ct);
        if (job is null) return NotFound();

        var rule = new ClassificationRule
        {
            JobTypeId   = job.JobTypeId,
            ErrorTypeId = req.ErrorTypeId,
            Pattern     = req.Pattern,
            Confidence  = req.Confidence,
            Priority    = req.Priority,
            IsActive    = req.IsActive,
            CreatedBy   = "operator",
        };
        db.ClassificationRules.Add(rule);
        await db.SaveChangesAsync(ct);

        db.MonitoredJobRules.Add(new MonitoredJobRule
        {
            MonitoredJobId = jobId,
            RuleId         = rule.RuleId,
            IsActive       = true,
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { rule.RuleId });
    }

    [HttpPost("monitored-jobs/{jobId:int}/classification-rules/{ruleId:int}/link")]
    public async Task<IActionResult> LinkJobClassificationRule(int jobId, int ruleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.MonitoredJobs.FindAsync([jobId], ct) is null) return NotFound("Job not found");
        if (await db.ClassificationRules.FindAsync([ruleId], ct) is null) return NotFound("Rule not found");
        var exists = await db.MonitoredJobRules
            .AnyAsync(r => r.MonitoredJobId == jobId && r.RuleId == ruleId, ct);
        if (exists) return Conflict("Rule already linked to this job");
        db.MonitoredJobRules.Add(new MonitoredJobRule { MonitoredJobId = jobId, RuleId = ruleId, IsActive = true });
        await db.SaveChangesAsync(ct);
        return Ok(new { ruleId });
    }

    [HttpDelete("monitored-jobs/{jobId:int}/classification-rules/{ruleId:int}")]
    public async Task<IActionResult> DeleteJobClassificationRule(int jobId, int ruleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var link = await db.MonitoredJobRules
            .FirstOrDefaultAsync(r => r.MonitoredJobId == jobId && r.RuleId == ruleId, ct);
        if (link is null) return NotFound();
        db.MonitoredJobRules.Remove(link);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Fix Policy Rules ─────────────────────────────────────────────────────

    [HttpGet("fix-policy-rules")]
    public async Task<IActionResult> GetFixPolicyRules([FromQuery] int? jobTypeId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.FixPolicyRules.Include(r => r.ErrorType).Where(r => r.Enabled);
        if (jobTypeId.HasValue) q = q.Where(r => r.JobTypeId == jobTypeId.Value);
        var rules = await q.OrderBy(r => r.ErrorTypeId).ToListAsync(ct);
        return Ok(rules.Select(r => new
        {
            r.RuleId, r.JobTypeId, r.ErrorTypeId,
            ErrorTypeCode      = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.ActionToApply,
            FixCategory        = r.FixCategory.ToString(),
            ActionType         = r.ActionType.ToString(),
            r.ActionPayload, r.IsAutoHealEligible, r.Enabled,
        }));
    }

    [HttpGet("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> GetFixPolicyRule(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var r = await db.FixPolicyRules
            .Include(p => p.ErrorType)
            .FirstOrDefaultAsync(p => p.RuleId == id, ct);
        if (r is null) return NotFound();
        return Ok(new
        {
            r.RuleId, r.JobTypeId, r.ErrorTypeId,
            ErrorTypeCode = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.ActionToApply,
            FixCategory   = r.FixCategory.ToString(),
            ActionType    = r.ActionType.ToString(),
            r.ActionPayload, r.IsAutoHealEligible, r.Enabled,
        });
    }

    [HttpPost("fix-policy-rules")]
    public async Task<IActionResult> CreateFixPolicyRule([FromBody] UpsertFixPolicyRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!Enum.TryParse<FixCategory>(req.FixCategory, out var fixCategory))
            return BadRequest($"Unknown FixCategory: '{req.FixCategory}'");
        if (!Enum.TryParse<FixActionType>(req.ActionType, out var actionType))
            return BadRequest($"Unknown ActionType: '{req.ActionType}'");

        var rule = new FixPolicyRule
        {
            JobTypeId          = req.JobTypeId,
            ErrorTypeId        = req.ErrorTypeId,
            ActionToApply      = req.ActionToApply,
            FixCategory        = fixCategory,
            ActionType         = actionType,
            ActionPayload      = req.ActionPayload,
            IsAutoHealEligible = req.IsAutoHealEligible,
            Enabled            = req.Enabled,
            CreatedBy          = "operator",
            ActionTimestamp    = DateTime.Now,
        };
        db.FixPolicyRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return Ok(new { rule.RuleId });
    }

    [HttpPut("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> UpdateFixPolicyRule(int id, [FromBody] UpsertFixPolicyRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.FixPolicyRules.FindAsync([id], ct);
        if (rule is null) return NotFound();
        if (!Enum.TryParse<FixCategory>(req.FixCategory, out var fixCat))
            return BadRequest($"Unknown FixCategory: '{req.FixCategory}'");
        if (!Enum.TryParse<FixActionType>(req.ActionType, out var actType))
            return BadRequest($"Unknown ActionType: '{req.ActionType}'");

        rule.ErrorTypeId        = req.ErrorTypeId;
        rule.ActionToApply      = req.ActionToApply;
        rule.FixCategory        = fixCat;
        rule.ActionType         = actType;
        rule.ActionPayload      = req.ActionPayload;
        rule.IsAutoHealEligible = req.IsAutoHealEligible;
        rule.Enabled            = req.Enabled;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("fix-policy-rules/{id:int}")]
    public async Task<IActionResult> DeleteFixPolicyRule(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.FixPolicyRules.FindAsync([id], ct);
        if (rule is null) return NotFound();
        rule.Enabled = false;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Classification Rules ─────────────────────────────────────────────────

    [HttpGet("classification-rules")]
    public async Task<IActionResult> GetAllClassificationRules(CancellationToken ct)
    {
        var rules = await ruleRepo.GetAllAsync(ct);
        return Ok(rules.Select(r => new
        {
            r.RuleId, r.JobTypeId,
            JobTypeName  = r.JobType?.Name ?? r.JobTypeId.ToString(),
            r.ErrorTypeId,
            ErrorTypeCode = r.ErrorType?.Code ?? r.ErrorTypeId.ToString(),
            r.Pattern, r.Confidence, r.Priority, r.IsActive, r.CreatedBy
        }));
    }

    [HttpPost("classification-rules")]
    public async Task<IActionResult> CreateClassificationRule([FromBody] UpsertClassificationRuleRequest req, CancellationToken ct)
    {
        var rule = new ClassificationRule
        {
            JobTypeId   = req.JobTypeId,
            ErrorTypeId = req.ErrorTypeId,
            Pattern     = req.Pattern,
            Confidence  = req.Confidence,
            Priority    = req.Priority,
            IsActive    = true,
            CreatedBy   = "operator",
        };
        var saved = await ruleRepo.SaveAsync(rule, ct);
        return Ok(new { saved.RuleId });
    }

    [HttpPut("classification-rules/{id:int}")]
    public async Task<IActionResult> UpdateClassificationRule(int id, [FromBody] UpsertClassificationRuleRequest req, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.ClassificationRules.FindAsync([id], ct);
        if (rule is null) return NotFound();

        rule.JobTypeId   = req.JobTypeId;
        rule.ErrorTypeId = req.ErrorTypeId;
        rule.Pattern     = req.Pattern;
        rule.Confidence  = req.Confidence;
        rule.Priority    = req.Priority;
        rule.IsActive    = req.IsActive;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("classification-rules/{id:int}")]
    public async Task<IActionResult> DeleteClassificationRule(int id, CancellationToken ct)
    {
        await ruleRepo.DeleteAsync(id, ct);
        return NoContent();
    }
}

// ── Request contracts ────────────────────────────────────────────────────────

public sealed record UpsertMonitoredJobRequest(
    string  Name,
    string? DisplayName,
    int     JobTypeId,
    int     ScanTypeId,
    string? LogFolder,
    string? SearchPatterns,
    string? ConnectionName,
    string? LogSourceUrl,
    int     PollingIntervalSeconds,
    bool    IsActive,
    string? Description);

public sealed record UpsertScanCheckRuleRequest(
    string   CheckType,
    string?  SourceTable,
    string   TargetField,
    decimal? MinValue,
    decimal? MaxValue,
    string?  ExpectedValue,
    string?  WatermarkColumn,
    string?  SourceIdColumn,
    string   Severity,
    string?  Description,
    bool     IsActive = true);

public sealed record UpsertClassificationRuleRequest(
    int     JobTypeId,
    int     ErrorTypeId,
    string  Pattern,
    decimal Confidence,
    int     Priority,
    bool    IsActive = true);

public sealed record UpsertJobClassificationRuleRequest(
    int     ErrorTypeId,
    string  Pattern,
    decimal Confidence,
    int     Priority,
    bool    IsActive = true);

public sealed record UpsertFixPolicyRuleRequest(
    int     JobTypeId,
    int     ErrorTypeId,
    string  ActionToApply,
    string  FixCategory,
    string  ActionType,
    string? ActionPayload,
    bool    IsAutoHealEligible,
    bool    Enabled);

public sealed record UpsertErrorTypeRequest(
    string  Code,
    string  DisplayName,
    string? Description,
    string  Severity,
    bool    IsActive = true);
