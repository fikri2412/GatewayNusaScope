using System.Net;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Proxy;

/// <param name="Caller">Terisi bila key valid (juga saat ditolak sesudahnya, agar penolakan bisa dicatat).</param>
/// <param name="Failure">Terisi bila permintaan harus ditolak.</param>
public sealed record AuthResult(GatewayCaller? Caller, GatewayResponse? Failure, string? DeniedReason);

public sealed class ApiKeyAuthenticator(GatewayDbContext db)
{
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    /// <summary>Verifikasi key, lalu set tenant konteks dari key (tidak pernah dari klien).</summary>
    public async Task<AuthResult> AuthenticateAsync(
        string? authorization, IPAddress? clientIp, string? userAgent, string? tags, Guid requestId, CancellationToken ct)
    {
        const string scheme = "Bearer ";
        if (authorization is null || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return Fail(401, ErrorTypes.Authentication, "missing_api_key",
                "You must provide an API key in the Authorization header as 'Bearer <key>'.");

        var presented = authorization[scheme.Length..].Trim();
        if (!ApiKeyGenerator.TryGetPrefix(presented, out var prefix))
            return Fail(401, ErrorTypes.Authentication, "invalid_api_key", "Incorrect API key provided.");

        // Satu-satunya pencarian tanpa tenant: tenant baru diketahui dari key ini. Lewat stored procedure
        // (EXECUTE AS 'gateway_api_key_reader') karena tabel api_keys dilindungi RLS dan tenant belum diketahui.
        List<ApiKey> rows;
        try
        {
            rows = await db.ApiKeys.FromSqlRaw("EXEC dbo.api_key_bootstrap @key_prefix = {0}", prefix)
                .IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        }
        // ponytail: fallback untuk database tanpa script tenant-security.sql (dev/test); hapus saat semua
        // environment menjalankan script (prosedur api_key_bootstrap + policy RLS) secara wajib.
        catch (SqlException ex) when (ex.Number == 2812)
        {
            rows = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking()
                .Where(k => k.KeyPrefix == prefix).ToListAsync(ct);
        }
        var key = rows.FirstOrDefault();
        // Hash selalu dihitung dan dibandingkan, agar waktu tidak membocorkan apakah prefix ada.
        var hashOk = ApiKeyGenerator.Matches(presented, key?.KeyHash ?? new string('0', 64));
        if (key is null || !hashOk)
            return Fail(401, ErrorTypes.Authentication, "invalid_api_key", "Incorrect API key provided.");

        var caller = new GatewayCaller(key, requestId, clientIp?.ToString(), Truncate(userAgent, 300), Truncate(tags, 400));
        var now = DateTime.UtcNow;
        db.CurrentTenantId = key.TenantId;
        if (key.RevokedAt is not null)
            return Deny(caller, 401, ErrorTypes.Authentication, "api_key_revoked", "This API key has been revoked.");
        if (key.ExpiresAt is not null && key.ExpiresAt <= now)
            return Deny(caller, 401, ErrorTypes.Authentication, "api_key_expired", "This API key has expired.");

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == key.TenantId, ct);
        if (tenant is null || tenant.Status != TenantStatuses.Active)
            return Deny(caller, 403, ErrorTypes.Permission, "tenant_suspended", "This account is suspended.");

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == key.ProjectId, ct);
        if (project is null)
            return Deny(caller, 403, ErrorTypes.Permission, "project_deleted", "The project for this API key no longer exists.");
        if (project.Status != ProjectStatuses.Active)
            return Deny(caller, 403, ErrorTypes.Permission, "project_suspended", "This project is suspended.");

        if (!IpAllowList.IsAllowed(key.AllowedIpsJson, clientIp))
            return Deny(caller, 403, ErrorTypes.Permission, "ip_not_allowed", "Requests from this IP address are not allowed for this API key.");

        // Tulis last_used_at paling sering sekali per menit agar jalur panas tidak menulis tiap permintaan.
        if (key.LastUsedAt is null || key.LastUsedAt < now - LastUsedGranularity)
            await db.ApiKeys.Where(k => k.Id == key.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);

        return new AuthResult(caller, null, null);
    }

    private static AuthResult Fail(int status, string type, string code, string message) =>
        new(null, GatewayResponse.Error(status, type, code, message), code);

    private static AuthResult Deny(GatewayCaller caller, int status, string type, string code, string message) =>
        new(caller, GatewayResponse.Error(status, type, code, message), code);

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
