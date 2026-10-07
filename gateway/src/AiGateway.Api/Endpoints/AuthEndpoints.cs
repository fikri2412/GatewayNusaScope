using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AiGateway.Api.Auth;
using AiGateway.Api.Http;
using AiGateway.Core.Audit;
using AiGateway.Core.Auth;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Endpoints;

public sealed record LoginRequest(string? Email, string? Password);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record RedeemRequest(string? Token, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record UserSummary(long Id, string Email, string DisplayName, string Role, Guid? TenantId, string? TenantName);

public sealed record TokenResponse(
    string AccessToken, string TokenType, int ExpiresIn, string RefreshToken, DateTime RefreshExpiresAt, UserSummary User);

/// <summary>Login admin (tenant maupun platform) dan siklus sesi. Semua jawaban tidak boleh di-cache.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/api/auth");
        g.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(ctx);
        });

        g.MapPost("login", LoginAsync).AllowAnonymous().RequireRateLimiting(RequestProtection.AuthPolicy);
        g.MapPost("refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting(RequestProtection.AuthPolicy);
        g.MapPost("logout", LogoutAsync).AllowAnonymous();
        g.MapPost("accept-invite", (RedeemRequest r, AuthService a, AuditWriter w, CancellationToken ct) => RedeemAsync(TokenPurposes.Invite, "auth.invite_accepted", r, a, w, ct)).AllowAnonymous().RequireRateLimiting(RequestProtection.AuthPolicy);
        g.MapPost("reset-password", (RedeemRequest r, AuthService a, AuditWriter w, CancellationToken ct) => RedeemAsync(TokenPurposes.ResetPassword, "auth.password_reset", r, a, w, ct)).AllowAnonymous().RequireRateLimiting(RequestProtection.AuthPolicy);
        g.MapPost("change-password", ChangePasswordAsync).RequireAuthorization(Policies.Authenticated);
        g.MapGet("me", MeAsync).RequireAuthorization(Policies.Authenticated);
        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, HttpContext http, AuthService auth, JwtTokenService jwt, GatewayDbContext db, AuditWriter audit, CancellationToken ct)
    {
        try
        {
            var result = await auth.LoginAsync(request.Email ?? "", request.Password ?? "", http.Connection.RemoteIpAddress?.ToString(),
                http.Request.Headers.UserAgent, ct);
            await audit.WriteAsync("auth.login", "user", result.User.Id.ToString(), tenantId: result.User.TenantId, userId: result.User.Id);
            return Results.Ok(await ToResponseAsync(result, jwt, db, ct));
        }
        catch (GatewayException ex) when (ex.Code is "invalid_credentials" or "account_locked")
        {
            await audit.WriteAsync("auth.login_failed", detail: new { email = (request.Email ?? "").Trim().ToLowerInvariant(), reason = ex.Code });
            throw;
        }
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request, HttpContext http, AuthService auth, JwtTokenService jwt, GatewayDbContext db, CancellationToken ct)
    {
        var result = await auth.RefreshAsync(request.RefreshToken ?? "", http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent, ct);
        return Results.Ok(await ToResponseAsync(result, jwt, db, ct));
    }

    private static async Task<IResult> LogoutAsync(RefreshRequest request, AuthService auth, CancellationToken ct)
    {
        await auth.LogoutAsync(request.RefreshToken ?? "", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RedeemAsync(
        string purpose, string action, RedeemRequest request, AuthService auth, AuditWriter audit, CancellationToken ct)
    {
        await auth.RedeemTokenAsync(request.Token ?? "", purpose, request.Password ?? "", ct);
        await audit.WriteAsync(action);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request, ClaimsPrincipal user, AuthService auth, AuditWriter audit, CancellationToken ct)
    {
        var userId = long.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
        await auth.ChangePasswordAsync(userId, request.CurrentPassword ?? "", request.NewPassword ?? "", ct);
        await audit.WriteAsync("auth.password_changed", "user", userId.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, GatewayDbContext db, CancellationToken ct)
    {
        var userId = long.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        return Results.Ok(await SummaryAsync(AuthUser.From(user), db, ct));
    }

    private static async Task<TokenResponse> ToResponseAsync(LoginResult result, JwtTokenService jwt, GatewayDbContext db, CancellationToken ct)
    {
        var token = jwt.Issue(result.User);
        return new TokenResponse(token.Value, "Bearer", token.ExpiresInSeconds, result.RefreshToken, result.RefreshExpiresAt,
            await SummaryAsync(result.User, db, ct));
    }

    private static async Task<UserSummary> SummaryAsync(AuthUser user, GatewayDbContext db, CancellationToken ct)
    {
        var tenant = user.TenantId is { } id
            ? await db.Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.PublicId, t.Name }).FirstOrDefaultAsync(ct)
            : null;
        return new UserSummary(user.Id, user.Email, user.DisplayName, user.Role, tenant?.PublicId, tenant?.Name);
    }
}
