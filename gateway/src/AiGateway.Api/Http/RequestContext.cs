using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AiGateway.Api.Auth;
using AiGateway.Core.Audit;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Http;

/// <summary>
/// Setelah autentikasi: isi <see cref="CurrentActor"/> dan tenant konteks dari token (tidak pernah dari body/query).
/// Tenant yang disuspend hanya boleh membaca: semua perubahan di /admin/api ditolak (kecuali endpoint auth).
/// </summary>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, GatewayDbContext db, CurrentActor actor)
    {
        actor.Ip = http.Connection.RemoteIpAddress?.ToString();
        var user = http.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            if (long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)) actor.UserId = userId;
            if (long.TryParse(user.FindFirstValue(ClaimNames.TenantId), out var tenantId))
            {
                actor.TenantId = tenantId;
                db.CurrentTenantId = tenantId;
            }
        }

        if (actor.TenantId is { } tid && IsTenantMutation(http.Request)
            && await db.Tenants.AsNoTracking().Where(t => t.Id == tid).Select(t => t.Status).FirstOrDefaultAsync(http.RequestAborted) != TenantStatuses.Active)
            throw new GatewayException(403, "tenant_suspended", "Akun ini sedang disuspend; hanya bisa melihat data.");

        await next(http);
    }

    private static bool IsTenantMutation(HttpRequest request) =>
        request.Path.StartsWithSegments("/admin/api")
        && !request.Path.StartsWithSegments("/admin/api/auth")
        && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
}

/// <summary>Semua error API berbentuk <c>{"error":{"message","type","param","code"}}</c>; error tak terduga tidak membocorkan detail.</summary>
public sealed class GatewayExceptionHandler(ILogger<GatewayExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, code, message) = exception switch
        {
            GatewayException g => (g.Status, g.Code, g.Message),
            BadHttpRequestException b => (b.StatusCode, b.StatusCode == 413 ? "request_too_large" : "bad_request", "Permintaan tidak valid."),
            OperationCanceledException when http.RequestAborted.IsCancellationRequested => (499, "client_closed_request", "Permintaan dibatalkan."),
            _ => (500, "internal_error", "Terjadi kesalahan di server."),
        };
        if (status >= 500) log.LogError(exception, "Error tak tertangani pada {Method} {Path}", http.Request.Method, http.Request.Path);

        var type = status switch
        {
            401 => "authentication_error", 403 => "permission_error", 404 => "not_found_error", 429 => "rate_limit_error",
            >= 500 => "server_error", _ => "invalid_request_error",
        };
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { error = new { message, type, param = (string?)null, code } }, ct);
        return true;
    }
}
