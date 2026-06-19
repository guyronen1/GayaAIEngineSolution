using AIEngineAPI.Auth;
using MaiaAI.Core.Configuration;
using MaiaAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIEngineAPI.Controllers;

/// <summary>
/// Authentication endpoints. Login/logout manage the httpOnly session cookie;
/// change-password is self-service; /me lets the SPA discover the current identity.
///
/// Phase 1 note: there is no global enforcement yet, so these all respond as written
/// regardless of authentication state. <c>[AllowAnonymous]</c> on login/me is for when
/// the Phase-3 fallback policy lands (they must stay reachable without a session).
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(
    IAuthService          auth,
    ICurrentUserAccessor  currentUser,
    AuthOptions           options) : ControllerBase
{
    public sealed record LoginRequest(string Username, string Password);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "MissingCredentials", message = "Username and password are required." });

        var result = await auth.LoginAsync(req.Username, req.Password, ct);
        if (!result.Success)
            // Single generic message for unknown user / disabled / bad password —
            // don't leak which it was.
            return Unauthorized(new { error = "InvalidCredentials", message = "Invalid username or password." });

        Response.Cookies.Append(options.CookieName, result.Token!, SessionCookie.Build(options));

        return Ok(new
        {
            username           = result.User!.Username,
            role               = result.User.Role?.Name,
            mustChangePassword = result.MustChangePassword,
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(options.CookieName, out var token) && !string.IsNullOrEmpty(token))
            await auth.LogoutAsync(token, ct);

        Response.Cookies.Delete(options.CookieName, SessionCookie.BuildForDelete(options));
        return NoContent();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        // Self-service: identity comes from the authenticated principal, never the body.
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return Unauthorized();
        if (req is null || string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { error = "MissingNewPassword", message = "A new password is required." });

        var ok = await auth.ChangePasswordAsync(userId, req.CurrentPassword ?? string.Empty, req.NewPassword, ct);
        if (!ok)
            return BadRequest(new { error = "CurrentPasswordIncorrect", message = "Current password is incorrect." });

        return NoContent();
    }

    /// <summary>Skip the one-time change-password prompt without changing it. Clears
    /// the user's MustChangePassword flag so the prompt doesn't recur. The change is
    /// optional — this is the "Skip for now" action.</summary>
    [HttpPost("dismiss-password-change")]
    public async Task<IActionResult> DismissPasswordChange(CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return Unauthorized();
        await auth.DismissPasswordChangeAsync(userId, ct);
        return NoContent();
    }

    /// <summary>Current identity for the SPA. Anonymous returns 200 with
    /// <c>authenticated:false</c> (not 401) so the client can branch without tripping
    /// its 401-redirect interceptor.</summary>
    [AllowAnonymous]
    [HttpGet("me")]
    public IActionResult Me()
    {
        if (!currentUser.IsAuthenticated)
            return Ok(new { authenticated = false });

        return Ok(new
        {
            authenticated      = true,
            username           = currentUser.UserName,
            role               = currentUser.Role?.ToString(),
            mustChangePassword = currentUser.MustChangePassword,
        });
    }
}
