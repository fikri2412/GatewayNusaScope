using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AiGateway.Core.Auth;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AiGateway.Api.Auth;

/// <summary>Nama kebijakan otorisasi. Hierarki peran: owner mencakup admin, admin mencakup viewer.</summary>
public static class Policies
{
    public const string TenantViewer = "tenant-viewer", TenantAdmin = "tenant-admin", TenantOwner = "tenant-owner",
        PlatformAdmin = "platform-admin", Authenticated = "authenticated";
}

public static class ClaimNames
{
    public const string TenantId = "tenant_id", Stamp = "stamp";
}

public sealed record AccessToken(string Value, int ExpiresInSeconds);

/// <summary>Menerbitkan access token admin (HS256, berumur pendek). Refresh token ada di <see cref="AuthService"/>.</summary>
public sealed class JwtTokenService(IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth, TimeProvider clock)
{
    public AccessToken Issue(AuthUser user)
    {
        var now = clock.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(auth.Value.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new(ClaimNames.Stamp, user.SecurityStamp),
        };
        if (user.TenantId is { } tenantId) claims.Add(new Claim(ClaimNames.TenantId, tenantId.ToString()));

        var token = new JwtSecurityToken(
            jwt.Value.Issuer, jwt.Value.Audience, claims,
            notBefore: now.UtcDateTime, expires: now.Add(lifetime).UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey(jwt.Value), SecurityAlgorithms.HmacSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), (int)lifetime.TotalSeconds);
    }

    internal static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));
}

public static class AuthSetup
{
    public static IServiceCollection AddGatewayAuth(this IServiceCollection services)
    {
        services.AddSingleton<JwtTokenService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                bearer.MapInboundClaims = false; // pakai nama claim asli (sub, role, ...)
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = JwtTokenService.SigningKey(jwt.Value),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = ClaimTypes.Role,
                };
                bearer.Events = new JwtBearerEvents
                {
                    // Token hanya sah selama user masih aktif dan security stamp-nya belum berganti
                    // (ganti password, ubah peran, nonaktifkan akun, atau reset membatalkan semua token beredar).
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal!;
                        if (!long.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)
                            || principal.FindFirstValue(ClaimNames.Stamp) is not { } stamp
                            || await context.HttpContext.RequestServices.GetRequiredService<AuthService>()
                                   .FindActiveAsync(userId, stamp, context.HttpContext.RequestAborted) is null)
                            context.Fail("Sesi tidak lagi berlaku.");
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Authenticated, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.TenantViewer, p => p.RequireRole(Roles.Owner, Roles.Admin, Roles.Viewer))
            .AddPolicy(Policies.TenantAdmin, p => p.RequireRole(Roles.Owner, Roles.Admin))
            .AddPolicy(Policies.TenantOwner, p => p.RequireRole(Roles.Owner))
            .AddPolicy(Policies.PlatformAdmin, p => p.RequireRole(Roles.PlatformAdmin));
        return services;
    }
}
