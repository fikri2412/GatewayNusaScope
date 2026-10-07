using System.Text.Json;
using AiGateway.Core.Domain;

namespace AiGateway.Core.Proxy;

public static class ErrorTypes
{
    public const string InvalidRequest = "invalid_request_error", Authentication = "authentication_error",
        Permission = "permission_error", NotFound = "not_found_error", RateLimit = "rate_limit_error",
        Server = "server_error", Upstream = "upstream_error";
}

/// <summary>
/// Respons data plane: status HTTP dan body JSON siap kirim. Bila <see cref="StreamBody"/> terisi, respons adalah SSE:
/// endpoint mengirim header lalu menjalankannya (body diabaikan) dan pencatatan pemakaian dilakukan di dalamnya.
/// </summary>
public sealed record GatewayResponse(int Status, string Body, string? RetryAfter = null, Func<Stream, CancellationToken, Task>? StreamBody = null)
{
    /// <summary>Error berformat OpenAI: <c>{"error":{"message","type","param","code"}}</c>.</summary>
    public static GatewayResponse Error(int status, string type, string code, string message, string? retryAfter = null) =>
        new(status, JsonSerializer.Serialize(new { error = new { message, type, param = (string?)null, code } }), retryAfter);
}

/// <summary>Pemanggil yang sudah terautentikasi lewat API key.</summary>
public sealed record GatewayCaller(ApiKey Key, Guid RequestId, string? ClientIp, string? UserAgent, string? Tags)
{
    public long TenantId => Key.TenantId;
    public long ProjectId => Key.ProjectId;
}
