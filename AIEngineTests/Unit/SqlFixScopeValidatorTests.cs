using MaiaAI.Core.Interfaces;
using MaiaAI.Infrastructure.Fix;
using Xunit;

namespace AIEngineTests.Unit;

/// <summary>
/// Layer-1 write-guard for SqlScript fix payloads. Confirms a fix must be a
/// scoped UPDATE/DELETE ({sourceId} in WHERE) or EXEC ({sourceId} as a param);
/// bulk / unscoped writes are rejected at save. Documents the known limit: it
/// catches accidental missing-WHERE, not deliberate tautology bypasses.
/// </summary>
public class SqlFixScopeValidatorTests
{
    private readonly ISqlFixScopeValidator _v = new SqlFixScopeValidator();

    private static bool Ok(string? r) => r is null;

    // ── Rejected: unscoped writes ────────────────────────────────────────────

    [Fact] // the exact payload from the live incident
    public void NoWhereUpdate_Rejected()
        => Assert.False(Ok(_v.Validate("update dbo.files set FileStatusCode=1")));

    [Fact]
    public void NoWhereDelete_Rejected()
        => Assert.False(Ok(_v.Validate("delete from dbo.Files")));

    [Fact] // WHERE present but not scoped to the failing row
    public void WhereWithoutSourceId_Rejected()
        => Assert.False(Ok(_v.Validate("update dbo.Files set FileStatusCode=1 where Active=1")));

    [Fact] // {failureId} is MAIA's PK, not a key into the operator's table — doesn't count
    public void FailureIdInWhere_Rejected()
        => Assert.False(Ok(_v.Validate("update dbo.Files set x=1 where id={failureId}")));

    [Fact]
    public void Select_Rejected()
        => Assert.False(Ok(_v.Validate("select * from dbo.Files")));

    [Fact]
    public void ExecWithoutSourceId_Rejected()
        => Assert.False(Ok(_v.Validate("EXEC sp_WipeAll")));

    [Fact]
    public void Unparseable_Rejected()
        => Assert.False(Ok(_v.Validate("this is not sql ((")));

    [Fact] // multi-statement: one scoped, one bulk → whole payload rejected
    public void MultiStatement_OneUnscoped_Rejected()
        => Assert.False(Ok(_v.Validate(
            "update dbo.Files set x=1 where id='{sourceId}'; delete from dbo.Events")));

    // ── Accepted: scoped writes ──────────────────────────────────────────────

    [Fact]
    public void ScopedUpdate_Ok()
        => Assert.True(Ok(_v.Validate("update dbo.Files set FileStatusCode=1 where id='{sourceId}'")));

    [Fact] // {sourceId} substitution is case-insensitive (matches the executor)
    public void ScopedUpdate_MixedCasePlaceholder_Ok()
        => Assert.True(Ok(_v.Validate("update dbo.Files set FileStatusCode=1 where id='{SourceId}'")));

    [Fact]
    public void ScopedDelete_Ok()
        => Assert.True(Ok(_v.Validate("delete from dbo.Files where id='{sourceId}'")));

    [Fact]
    public void ExecWithSourceIdParam_Ok()
        => Assert.True(Ok(_v.Validate("EXEC dbo.sp_FixOne @id='{sourceId}'")));

    [Fact] // "ConnectionName|SQL" prefix is stripped before parsing
    public void ConnectionPrefix_Stripped_Ok()
        => Assert.True(Ok(_v.Validate("B2BTest|update dbo.Files set x=1 where id='{sourceId}'")));

    [Fact]
    public void MultiStatement_AllScoped_Ok()
        => Assert.True(Ok(_v.Validate(
            "update dbo.Files set x=1 where id='{sourceId}'; delete from dbo.Events where FileId='{sourceId}'")));

    [Fact] // unquoted (numeric-key) placeholder still resolves inside the WHERE
    public void ScopedUpdate_UnquotedPlaceholder_Ok()
        => Assert.True(Ok(_v.Validate("update dbo.Files set x=1 where id={sourceId}")));
}
