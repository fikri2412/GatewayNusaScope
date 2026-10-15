using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;

namespace OperatorAi.Api.Auth;

/// <summary>JWT HS256 berumur <c>Auth:TokenMinutes</c>. Klaim: <c>sub</c> = id user, <c>name</c> = username, <c>sec</c> = SecurityStamp.</summary>
public sealed class TokenIssuer(IOptions<AuthOptions> options, TimeProvider time)
{
    private const string Issuer = "operator-ai", Audience = "operator-ai";

    public (string Token, DateTime ExpiresAt) Create(User user)
    {
        var o = options.Value;
        var expires = time.GetUtcNow().UtcDateTime.AddMinutes(o.TokenMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(), ["name"] = user.Username, ["sec"] = user.SecurityStamp.ToString("N"),
            },
            SigningCredentials = new SigningCredentials(Key(o), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    /// <summary>Dipakai middleware JwtBearer di <c>Program.cs</c> dan test, supaya aturan validasinya satu tempat.</summary>
    public static TokenValidationParameters Validation(AuthOptions o) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = Key(o),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = "name",
        ClockSkew = TimeSpan.Zero, // satu server, satu jam
    };

    private static SymmetricSecurityKey Key(AuthOptions o) => new(Encoding.UTF8.GetBytes(o.JwtKey));
}

public static class UserClaims
{
    /// <summary>Id user dari klaim <c>sub</c>. Pemanggil sudah lewat fallback policy (token valid), jadi klaim ini selalu ada.</summary>
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new InvalidOperationException("Token tanpa klaim sub.");
}

/// <summary>
/// Klaim <c>sec</c> harus sama dengan <c>users.security_stamp</c>. Dipakai event <c>OnTokenValidated</c> JwtBearer,
/// jadi berlaku juga untuk SignalR (token lewat query): atur ulang password langsung mencabut token akses lama.
/// </summary>
public static class SessionGuard
{
    public static async Task<bool> IsValidAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue("sub"), out var id)
            || !Guid.TryParse(principal.FindFirstValue("sec"), out var stamp))
            return false;
        var stored = await db.Users.Where(u => u.Id == id).Select(u => u.SecurityStamp).FirstOrDefaultAsync(ct);
        return stored == stamp; // user hilang = stamp default, token selalu ditolak
    }
}
