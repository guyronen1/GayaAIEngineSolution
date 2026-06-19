using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AIEngineTests.Integration;

/// <summary>
/// THE cutover gate. Drives the real pipeline (WebApplicationFactory) from an
/// exhaustive (endpoint × verb × role) table across the entire route inventory and
/// asserts the expected authorization outcome for every cell:
///
///   • anonymous            → 401 on every protected route (login/me/health excepted)
///   • role below the floor → 403
///   • role at/above floor  → NOT 401/403 (authorization passed; 200/400/404/… are fine)
///
/// Rationale: the fallback policy is default-AUTHENTICATED, not default-admin, so a
/// forgotten [Authorize(RequireAdmin)] on a write would silently fall through to "any
/// authenticated user" — a privilege-escalation hole invisible until exploited. This
/// matrix is the compensating control. A missed attribute fails a cell here. Green is
/// the go/no-go for enforcement cutover. New routes MUST be added to the table.
/// </summary>
public sealed class AuthorizationMatrixTests : IAsyncLifetime
{
    private enum Tier { User = 1, Operator = 2, Admin = 3 }

    private sealed record Route(string Method, string Path, Tier Tier);

    // Full inventory. Path ids use "1"/"x" — authorization runs before model binding,
    // so the values only matter for the authorized (pass) case, where any non-401/403
    // status counts. Keep this in lockstep with the controllers.
    private static readonly Route[] Routes =
    {
        // ── User tier: operational reads (DataController, UnconfiguredController) ──
        new("GET", "/api/data/recommendations", Tier.User),
        new("GET", "/api/data/failures", Tier.User),
        new("GET", "/api/data/failures/1/status", Tier.User),
        new("GET", "/api/data/monitored-jobs", Tier.User),
        new("GET", "/api/data/worker-status", Tier.User),
        new("GET", "/api/data/analytics/failures-over-time", Tier.User),
        new("GET", "/api/data/analytics/failures-by-job", Tier.User),
        new("GET", "/api/data/analytics/resolution-mix", Tier.User),
        new("GET", "/api/data/dashboard-stats", Tier.User),
        new("GET", "/api/data/scan-runs", Tier.User),
        new("GET", "/api/unconfigured/clusters", Tier.User),
        new("GET", "/api/unconfigured/policy-gaps", Tier.User),

        // ── Operator tier: config reads + actions + manual triggers ──
        new("GET", "/api/config/job-types", Tier.Operator),
        new("GET", "/api/config/error-types", Tier.Operator),
        new("GET", "/api/config/monitored-jobs", Tier.Operator),
        new("GET", "/api/config/monitored-jobs/1", Tier.Operator),
        new("GET", "/api/config/fix-policy-rules", Tier.Operator),
        new("GET", "/api/config/fix-policy-rules/1", Tier.Operator),
        new("GET", "/api/config/classification-rules", Tier.Operator),
        new("POST", "/api/recommendations/1/approve", Tier.Operator),
        new("POST", "/api/recommendations/1/reject", Tier.Operator),
        new("POST", "/api/recommendations/1/retry", Tier.Operator),
        new("POST", "/api/failures/1/mark-resolved", Tier.Operator),
        new("GET", "/api/jobscan/1", Tier.Operator),
        new("POST", "/api/jobscan/1", Tier.Operator),
        new("GET", "/api/jobscan/by-name/x", Tier.Operator),
        new("POST", "/api/jobscan/by-name/x", Tier.Operator),
        new("POST", "/api/jobscan/scan-all", Tier.Operator),
        new("GET", "/api/jobscan/classify-pending", Tier.Operator),
        new("POST", "/api/jobscan/classify-pending", Tier.Operator),
        new("POST", "/api/fix/generate-suggestions", Tier.Operator),
        new("POST", "/api/fix/execute-fixes", Tier.Operator),
        new("POST", "/api/classification/classify-failures", Tier.Operator),
        new("POST", "/api/process/run-pipeline", Tier.Operator),
        new("POST", "/api/pipeline/run-directory", Tier.Operator),
        new("POST", "/api/logparser/parse", Tier.Operator),
        new("POST", "/api/logparser/extract-first", Tier.Operator),

        // ── Admin tier: config writes (ConfigController) ──
        new("POST", "/api/config/error-types", Tier.Admin),
        new("PUT", "/api/config/error-types/1", Tier.Admin),
        new("DELETE", "/api/config/error-types/1", Tier.Admin),
        new("POST", "/api/config/monitored-jobs", Tier.Admin),
        new("PUT", "/api/config/monitored-jobs/1", Tier.Admin),
        new("DELETE", "/api/config/monitored-jobs/1", Tier.Admin),
        new("POST", "/api/config/monitored-jobs/1/scan-sources", Tier.Admin),
        new("PUT", "/api/config/scan-sources/1", Tier.Admin),
        new("DELETE", "/api/config/scan-sources/1", Tier.Admin),
        new("POST", "/api/config/monitored-jobs/1/scan-rules", Tier.Admin),
        new("PUT", "/api/config/scan-rules/1", Tier.Admin),
        new("DELETE", "/api/config/scan-rules/1", Tier.Admin),
        new("POST", "/api/config/scan-sources/1/scan-rules", Tier.Admin),
        new("POST", "/api/config/monitored-jobs/1/classification-rules", Tier.Admin),
        new("POST", "/api/config/monitored-jobs/1/classification-rules/1/link", Tier.Admin),
        new("DELETE", "/api/config/monitored-jobs/1/classification-rules/1", Tier.Admin),
        new("POST", "/api/config/fix-policy-rules", Tier.Admin),
        new("PUT", "/api/config/fix-policy-rules/1", Tier.Admin),
        new("DELETE", "/api/config/fix-policy-rules/1", Tier.Admin),
        new("POST", "/api/config/classification-rules", Tier.Admin),
        new("PUT", "/api/config/classification-rules/1", Tier.Admin),
        new("DELETE", "/api/config/classification-rules/1", Tier.Admin),

        // ── Admin tier: maintenance + user management ──
        new("POST", "/api/admin/scan-history/cleanup", Tier.Admin),
        new("POST", "/api/admin/worker/pause", Tier.Admin),
        new("POST", "/api/admin/worker/resume", Tier.Admin),
        new("GET", "/api/users", Tier.Admin),
        new("POST", "/api/users", Tier.Admin),
        new("PUT", "/api/users/1", Tier.Admin),
        new("POST", "/api/users/1/reset-password", Tier.Admin),
    };

    private AuthTestFactory _factory = null!;
    private HttpClient _anon = null!, _user = null!, _operator = null!, _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = new AuthTestFactory();
        await _factory.SeedUsersAsync();
        _anon     = _factory.CreateClient(Opts());
        _user     = await LoggedIn(AuthTestFactory.UserUser);
        _operator = await LoggedIn(AuthTestFactory.OperatorUser);
        _admin    = await LoggedIn(AuthTestFactory.AdminUser);
    }

    public Task DisposeAsync()
    {
        _anon.Dispose(); _user.Dispose(); _operator.Dispose(); _admin.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static WebApplicationFactoryClientOptions Opts() => new() { HandleCookies = true };

    private async Task<HttpClient> LoggedIn(string username)
    {
        var client = _factory.CreateClient(Opts());
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username, password = AuthTestFactory.Password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return client;
    }

    private static bool Passed(HttpStatusCode s) =>
        s != HttpStatusCode.Unauthorized && s != HttpStatusCode.Forbidden;

    private static async Task<HttpStatusCode> Send(HttpClient client, Route r)
    {
        using var req = new HttpRequestMessage(new HttpMethod(r.Method), r.Path);
        if (r.Method is "POST" or "PUT")
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req);
        return resp.StatusCode;
    }

    [Fact]
    public async Task Every_route_enforces_its_tier_for_every_role()
    {
        var failures = new List<string>();

        foreach (var r in Routes)
        {
            // Anonymous: 401 everywhere (these routes are all protected).
            var anon = await Send(_anon, r);
            if (anon != HttpStatusCode.Unauthorized)
                failures.Add($"ANON  {r.Method,-6} {r.Path} → {(int)anon} (expected 401)");

            // User
            var user = await Send(_user, r);
            if (r.Tier == Tier.User)
            {
                if (!Passed(user)) failures.Add($"USER  {r.Method,-6} {r.Path} → {(int)user} (expected pass)");
            }
            else if (user != HttpStatusCode.Forbidden)
                failures.Add($"USER  {r.Method,-6} {r.Path} → {(int)user} (expected 403)");

            // Operator
            var op = await Send(_operator, r);
            if (r.Tier <= Tier.Operator)
            {
                if (!Passed(op)) failures.Add($"OPER  {r.Method,-6} {r.Path} → {(int)op} (expected pass)");
            }
            else if (op != HttpStatusCode.Forbidden)
                failures.Add($"OPER  {r.Method,-6} {r.Path} → {(int)op} (expected 403)");

            // Admin: passes everything.
            var admin = await Send(_admin, r);
            if (!Passed(admin))
                failures.Add($"ADMIN {r.Method,-6} {r.Path} → {(int)admin} (expected pass)");
        }

        Assert.True(failures.Count == 0,
            $"Authorization matrix failures ({failures.Count}):\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task Anonymous_routes_are_reachable_without_a_session()
    {
        // login (valid creds) succeeds anonymously
        var login = await _anon.PostAsJsonAsync("/api/auth/login",
            new { username = AuthTestFactory.AdminUser, password = AuthTestFactory.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // me is reachable anonymously and reports not-authenticated
        var me = await _factory.CreateClient(Opts()).GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // health probes are anonymous
        Assert.Equal(HttpStatusCode.OK, (await _anon.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _anon.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Auth_self_service_requires_a_session()
    {
        // logout + change-password fall to the fallback policy → 401 when anonymous.
        var logout = await _anon.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);

        var change = await _anon.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "x", newPassword = "y" });
        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
    }

    [Fact]
    public async Task MustChangePassword_is_a_soft_prompt_not_a_block()
    {
        var client = await LoggedIn(AuthTestFactory.MustChangeUser);

        // NOT blocked — the change is optional now (the hard-blocking middleware was
        // removed). A must-change user can use the app normally.
        var access = (await client.GetAsync("/api/data/worker-status")).StatusCode;
        Assert.True(access != HttpStatusCode.Forbidden && access != HttpStatusCode.Unauthorized,
            $"must-change user should not be blocked, got {(int)access}");

        // The flag is still surfaced so the SPA can show the one-time prompt.
        var meBody = await (await client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync();
        Assert.Contains("\"mustChangePassword\":true", meBody);

        // "Skip" clears the flag without changing the password…
        var dismiss = await client.PostAsync("/api/auth/dismiss-password-change", null);
        Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);

        // …so the prompt no longer recurs.
        var meAfter = await (await client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync();
        Assert.Contains("\"mustChangePassword\":false", meAfter);

        client.Dispose();
    }

    [Fact]
    public async Task Changing_password_also_clears_the_prompt()
    {
        var client = await LoggedIn(AuthTestFactory.MustChangeUser);

        var change = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = AuthTestFactory.Password, newPassword = "Rotated!9876" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var meAfter = await (await client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync();
        Assert.Contains("\"mustChangePassword\":false", meAfter);

        client.Dispose();
    }
}
