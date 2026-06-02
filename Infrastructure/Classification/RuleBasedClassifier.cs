using System.Text.RegularExpressions;
using MaiaAI.Core.Entities;
using MaiaAI.Core.Interfaces;
using MaiaAI.Core.Results;

namespace MaiaAI.Infrastructure.Classification;

/// <summary>
/// Pattern-matching classifier driven by ClassificationRules from the database.
/// When a MonitoredJob is set on the failure, per-job rule overrides are used first.
///
/// <para>Match semantics: case-insensitive substring containment. The single
/// supported wildcard is <c>*</c>, which matches any run of characters (including
/// none). All other regex metacharacters are treated as literal text.</para>
///
/// <para>To plug in ML: implement <see cref="IClassificationStrategy"/> and swap
/// this registration.</para>
/// </summary>
public sealed class RuleBasedClassifier(
    IClassificationRuleRepository ruleRepo,
    IMonitoredJobRepository monitoredJobRepo,
    ILogParser parser) : IClassificationStrategy
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public async Task<ClassificationResult?> ClassifyAsync(
        JobFailure job,
        string logContent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(logContent))
            return null;

        var rules = job.MonitoredJobId.HasValue
            ? await monitoredJobRepo.GetEffectiveRulesAsync(job.MonitoredJobId.Value, ct)
            : await ruleRepo.GetByJobTypeAsync(job.JobTypeId, ct);

        var lines = parser.ParseLog(logContent);

        foreach (var rule in rules)
        {
            var match = lines.FirstOrDefault(l => Matches(l, rule.Pattern));

            if (match is not null)
            {
                return new ClassificationResult
                {
                    FailureId       = job.FailureId,
                    JobId           = job.JobId,
                    JobTypeId       = job.JobTypeId,
                    // Forward to downstream so the suggestion generator can
                    // pick a per-job override over the default policy.
                    MonitoredJobId  = job.MonitoredJobId,
                    ErrorTypeId     = rule.ErrorTypeId,
                    ErrorTypeCode   = rule.ErrorType?.Code ?? string.Empty,
                    RawError        = match.Trim(),
                    Confidence      = (double)rule.Confidence,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Case-insensitive substring match with optional <c>*</c> wildcards.
    /// Patterns without <c>*</c> use the fast <see cref="string.Contains(string, StringComparison)"/>
    /// path; patterns with <c>*</c> are compiled to a regex that escapes every other
    /// character so regex metacharacters (<c>.</c>, <c>+</c>, <c>[</c>, etc.) are
    /// treated literally. ReDoS-safe by construction (no nested quantifiers,
    /// no backreferences) but a 50ms timeout is enforced as defence-in-depth.
    /// </summary>
    private static bool Matches(string line, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        if (!pattern.Contains('*'))
            return line.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        var regex = string.Join(".*", pattern.Split('*').Select(Regex.Escape));

        try
        {
            return Regex.IsMatch(line, regex, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
