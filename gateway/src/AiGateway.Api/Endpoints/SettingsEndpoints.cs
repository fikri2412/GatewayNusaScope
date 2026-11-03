using System.Text;
using System.Text.Json;
using AiGateway.Api.Auth;
using AiGateway.Core.AppStatus;
using AiGateway.Core.Audit;
using AiGateway.Core.Common;
using AiGateway.Core.Domain;
using AiGateway.Core.Proxy;
using AiGateway.Core.Reports;
using AiGateway.Core.Settings;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace AiGateway.Api.Endpoints;

public sealed record SettingDto(string Namespace, string Key, string Value, string ValueType, string? Description, DateTime UpdatedAt);

public sealed record SettingInput(string? Value, string? ValueType, string? Description);

public sealed record ClientSettingDto(string Key, string Value, string ValueType);

public sealed record ClientSettingsResponse(string Version, IReadOnlyList<ClientSettingDto> Items);

/// <summary>
/// Pengaturan aplikasi per tenant (G6). Admin/owner menulis, viewer membaca (<c>/admin/api/settings</c>); aplikasi
/// klien membaca namespace-nya dengan API key gateway (<c>/client/api/settings</c>, ETag untuk polling murah).
/// Nilai disimpan polos: bukan tempat secret.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/api/settings");
        g.MapGet("", ListAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapPut("{ns}/{key}", SetAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapDelete("{ns}/{key}", DeleteAsync).RequireAuthorization(Policies.TenantAdmin);

        app.MapGet("/client/api/settings", ClientReadAsync);
        app.MapGet("/client/api/usage", ClientUsageAsync);
        app.MapGet("/admin/api/app-status", StatusListAsync).RequireAuthorization(Policies.TenantViewer);
        app.MapPost("/client/api/app-status", ClientStatusAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(string? @namespace, AppSettingsService settings, CancellationToken ct) =>
        Results.Ok((await settings.ListAsync(@namespace, ct)).Select(ToDto).ToList());

    private static async Task<IResult> SetAsync(
        string ns, string key, SettingInput r, AppSettingsService settings, CurrentActor actor, AuditWriter audit, CancellationToken ct)
    {
        var (setting, previous) = await settings.SetAsync(ns, key, r.Value, r.ValueType, r.Description, actor.UserId, ct);
        await audit.WriteAsync("setting.set", "setting", $"{setting.Namespace}/{setting.Key}",
            new { setting.ValueType, Old = Clip(previous), New = Clip(setting.Value) });
        return Results.Ok(ToDto(setting));
    }

    private static async Task<IResult> DeleteAsync(
        string ns, string key, AppSettingsService settings, AuditWriter audit, CancellationToken ct)
    {
        var setting = await settings.DeleteAsync(ns, key, ct);
        await audit.WriteAsync("setting.delete", "setting", $"{setting.Namespace}/{setting.Key}",
            new { setting.ValueType, Old = Clip(setting.Value) });
        return Results.NoContent();
    }

    /// <summary>
    /// Dibaca aplikasi klien dengan API key (key yang sama dengan data plane). Penolakan memakai format error yang sama,
    /// tetapi tidak dicatat sebagai pemakaian AI. <c>304</c> bila <c>If-None-Match</c> cocok dengan versi isi.
    /// </summary>
    private static async Task<IResult> ClientReadAsync(
        string? @namespace, HttpContext http, ApiKeyAuthenticator auth, AppSettingsService settings, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        http.Response.Headers["X-Request-Id"] = requestId.ToString();
        var result = await auth.AuthenticateAsync(
            http.Request.Headers.Authorization, http.Connection.RemoteIpAddress, http.Request.Headers.UserAgent, null, requestId, ct);
        if (result.Failure is { } failure)
            return Results.Text(failure.Body, "application/json", Encoding.UTF8, failure.Status);

        var snapshot = await settings.SnapshotAsync(@namespace, ct);
        var etag = $"\"{snapshot.Version}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "no-cache";
        if (http.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Tag == etag || t.Tag == "*"))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Ok(new ClientSettingsResponse(
            snapshot.Version, snapshot.Items.Select(s => new ClientSettingDto(s.Key, s.Value, s.ValueType)).ToList()));
    }

    /// <summary>
    /// GB-04: pemakaian dan sisa kuota efektif untuk key pemanggil (hanya datanya sendiri; <c>from</c>/<c>to</c> YYYY-MM-DD, maks 31 hari,
    /// bawaan 7 hari terakhir). Key yang sama dengan data plane dan penolakan yang sama (dicabut, kedaluwarsa, IP, tenant/project
    /// ditangguhkan), tetapi read-only dan tidak dicatat sebagai pemakaian AI.
    /// </summary>
    private static async Task<IResult> ClientUsageAsync(
        string? from, string? to, HttpContext http, ApiKeyAuthenticator auth, ClientUsageReport report, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        http.Response.Headers["X-Request-Id"] = requestId.ToString();
        var result = await auth.AuthenticateAsync(
            http.Request.Headers.Authorization, http.Connection.RemoteIpAddress, http.Request.Headers.UserAgent, null, requestId, ct);
        if (result.Failure is { } failure)
            return Results.Text(failure.Body, "application/json", Encoding.UTF8, failure.Status);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await report.RunAsync(result.Caller!, from, to, ct));
    }

    /// <summary>
    /// Laporan status aplikasi klien (monitoring G6). Key yang sama dengan data plane: tenant/project/key diambil dari
    /// hasil autentikator, tidak pernah dari body, dan penolakan tidak dicatat sebagai pemakaian AI. Body maksimum
    /// 64 KB; nilai di luar allowlist ditolak.
    /// </summary>
    private static async Task<IResult> ClientStatusAsync(
        HttpContext http, ApiKeyAuthenticator auth, AppStatusService status, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        http.Response.Headers["X-Request-Id"] = requestId.ToString();
        var result = await auth.AuthenticateAsync(
            http.Request.Headers.Authorization, http.Connection.RemoteIpAddress, http.Request.Headers.UserAgent, null, requestId, ct);
        if (result.Failure is { } failure)
            return Results.Text(failure.Body, "application/json", Encoding.UTF8, failure.Status);

        var limit = AppStatusService.MaxBodyBytes;
        if (http.Request.ContentLength > limit)
            throw new GatewayException(StatusCodes.Status413PayloadTooLarge, "request_too_large", $"Body laporan melebihi {limit} bytes.");
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limit;

        AppStatusReportInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<AppStatusReportInput>(http.Request.Body, JsonSerializerOptions.Web, ct);
        }
        catch (JsonException)
        {
            input = null;
        }
        if (input is null)
            throw GatewayException.BadRequest("invalid_json", "Body laporan harus berupa objek JSON.");

        return Results.Ok(new AppStatusReceivedDto(await status.UpsertAsync(result.Caller!, input, ct)));
    }

    /// <summary>
    /// Laporan terakhir tenant ini (viewer boleh membaca). Namespace kosong menampilkan semua namespace yang dilaporkan;
    /// versi configured per namespace dihitung dari pengaturan saat ini, bukan disamakan dengan yang dilaporkan aplikasi.
    /// </summary>
    private static async Task<IResult> StatusListAsync(string? @namespace, AppStatusService status, CancellationToken ct) =>
        Results.Ok(await status.ListAsync(@namespace, ct));

    private static SettingDto ToDto(AppSetting s) => new(s.Namespace, s.Key, s.Value, s.ValueType, s.Description, s.UpdatedAt);

    /// <summary>Nilai di audit dipotong: pengaturan bisa panjang dan audit tidak dimaksudkan menyimpan salinan penuh.</summary>
    private static string? Clip(string? value) => value is { Length: > 200 } ? value[..200] + "…" : value;
}
