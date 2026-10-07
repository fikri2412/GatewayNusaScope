using System.Security.Cryptography;
using System.Text;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Maintenance;

public sealed record DeliveryOutcome(int Sent, int Retried, int Failed);

/// <summary>
/// Kirim antrean <c>webhook_deliveries</c> yang jatuh tempo: POST payload JSON, tanda tangan HMAC-SHA256
/// di header <c>X-Gateway-Signature</c>, timeout, dan backoff eksponensial sampai percobaan habis.
/// URL wajib https dan hanya boleh keluar lewat klien bernama <see cref="HttpClientName"/> (handler anti-SSRF).
/// </summary>
public sealed class WebhookDeliveryService(
    GatewayDbContext db, IHttpClientFactory httpFactory, WebhookSecretProtector protector,
    IOptions<MaintenanceOptions> options, TimeProvider clock, ILogger<WebhookDeliveryService> log)
{
    public const string HttpClientName = "webhook";

    public async Task<DeliveryOutcome> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.Set<WebhookDelivery>().IgnoreQueryFilters()
            .Where(d => d.Status == DeliveryStatuses.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).Take(o.DeliveryBatchSize).ToListAsync(ct);

        var sent = 0; var retried = 0; var failed = 0;
        foreach (var delivery in due)
        {
            var (success, responseStatus, error, retriable) = await TrySendAsync(delivery, ct);
            delivery.Attempts++;
            delivery.ResponseStatus = responseStatus;
            if (error is { Length: > 500 }) error = error[..500];
            delivery.LastError = error;

            if (success)
            {
                delivery.Status = DeliveryStatuses.Succeeded;
                delivery.DeliveredAt = now;
                sent++;
            }
            else if (!retriable || delivery.Attempts >= o.WebhookMaxAttempts)
            {
                delivery.Status = DeliveryStatuses.Failed;
                failed++;
                log.LogWarning("Pengiriman webhook {DeliveryId} gagal permanen setelah {Attempts} percobaan: {Error}",
                    delivery.Id, delivery.Attempts, delivery.LastError);
            }
            else
            {
                delivery.NextAttemptAt = now.AddSeconds(Backoff(o, delivery.Attempts));
                retried++;
            }
        }
        if (due.Count > 0)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                log.LogError(ex, "Gagal menyimpan status pengiriman webhook");
            }
        }
        return new DeliveryOutcome(sent, retried, failed);
    }

    private async Task<(bool Success, int? Status, string? Error, bool Retriable)> TrySendAsync(
        WebhookDelivery delivery, CancellationToken ct)
    {
        var webhook = await db.Set<Webhook>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == delivery.WebhookId, ct);
        if (webhook is null) return (false, null, "Webhook sudah dihapus.", false);
        if (!webhook.Enabled) return (false, null, "Webhook dinonaktifkan.", false);
        if (!Uri.TryCreate(webhook.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (false, null, "URL webhook bukan https yang valid.", false);

        var payload = delivery.PayloadJson ?? "{}";
        try
        {
            var secret = protector.Unprotect(webhook.TenantId, webhook.SigningSecretEncrypted);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("X-Gateway-Signature", "sha256=" + Signature(secret, payload));
            request.Headers.TryAddWithoutValidation("X-Gateway-Event", "quota_threshold");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.WebhookTimeoutSeconds));
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return (true, status, null, false);
            // 408/429 dan 5xx dicoba lagi; 4xx lain menandakan konfigurasi penerima salah.
            return (false, status, $"HTTP {status}.", status is 408 or 429 or >= 500);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, "Timeout.", true);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message, true);
        }
    }

    /// <summary>Tanda tangan hex huruf kecil dari HMAC-SHA256(secret, payload) untuk verifikasi penerima.</summary>
    public static string Signature(string secret, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    private static double Backoff(MaintenanceOptions o, int attempts) =>
        Math.Min(o.WebhookBackoffMaxSeconds, o.WebhookBackoffBaseSeconds * Math.Pow(2, Math.Min(attempts - 1, 20)));
}
