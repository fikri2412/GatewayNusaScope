using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiGateway.Core.WebSearch;

/// <summary>
/// Helper I/O penyedia: deadline total per panggilan (header + body), pembacaan body terbatas,
/// pemotongan output ternormalisasi, dan parsing JSON ketat.
/// Tidak pernah mencatat URL, header, kunci, atau body mentah.
/// </summary>
internal static class SearchProviderHttp
{
    /// <summary>Batas body respons error (hanya untuk pencocokan override, tidak pernah dikembalikan).</summary>
    public const int MaxErrorBodyBytes = 8 * 1024;

    public const int MaxSnippetChars = 1_000;
    public const int MaxContentChars = 20_000;
    public const int MaxExtractChars = 100_000;

    public static Uri BuildUri(string baseUri, string relativePath) =>
        new(new Uri(baseUri.TrimEnd('/') + "/"), relativePath);

    /// <summary>
    /// Kirim permintaan dengan deadline total (<paramref name="timeout"/>) mencakup header dan pembacaan body
    /// hingga <paramref name="maxResponseBytes"/> byte. Respons non-2xx dilempar sebagai <see cref="SearchProviderException"/>.
    /// </summary>
    public static async Task<JsonDocument> SendJsonAsync(
        HttpClient client, HttpRequestMessage message, TimeSpan timeout, int maxResponseBytes, CancellationToken ct)
    {
        var result = await SendRawAsync(client, message, timeout, maxResponseBytes, ct);
        if (!result.Success)
            throw new SearchProviderException(result.StatusCode, result.Body, result.RetryAfter);
        return ParseJson(result.Body);
    }

    /// <summary>Seperti <see cref="SendJsonAsync"/> tetapi mengembalikan status/body mentah (untuk kegagalan per-URL).</summary>
    public static async Task<(bool Success, HttpStatusCode StatusCode, string Body, TimeSpan? RetryAfter)> SendRawAsync(
        HttpClient client, HttpRequestMessage message, TimeSpan timeout, int maxResponseBytes, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : timeout);

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var body = await ReadBodyAsync(
            response,
            response.IsSuccessStatusCode ? maxResponseBytes : MaxErrorBodyBytes,
            deadline.Token);
        return (response.IsSuccessStatusCode, response.StatusCode, body, ParseRetryAfter(response.Headers.RetryAfter));
    }

    /// <summary>Baca body sebagai UTF-8 maksimal <paramref name="maxBytes"/> byte (sisa stream tidak dibaca).</summary>
    public static async Task<string> ReadBodyAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var limit = Math.Max(1, maxBytes);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(Math.Min(limit, 64 * 1024));
        var pool = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var remaining = limit;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(pool.AsMemory(0, Math.Min(pool.Length, remaining)), ct);
                if (read == 0) break;
                buffer.Write(pool, 0, read);
                remaining -= read;
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pool);
        }
    }

    /// <summary>Parse JSON ketat; body 2xx yang bukan JSON valid dianggap kesalahan protokol penyedia.</summary>
    public static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new SearchProviderProtocolException("Respons penyedia bukan JSON yang valid.");
        }
    }

    public static string Truncate(string? value, int maxChars, out bool truncated)
    {
        truncated = value is { Length: > 0 } && value.Length > maxChars;
        return truncated ? value![..maxChars] : value ?? "";
    }

    public static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retry)
    {
        if (retry == null) return null;
        if (retry.Delta.HasValue) return retry.Delta.Value;
        if (retry.Date.HasValue)
        {
            var diff = retry.Date.Value - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.FromSeconds(1);
        }
        return null;
    }

    public static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool TryGetArray(JsonElement element, string property, out JsonElement array)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        array = default;
        return false;
    }
}
