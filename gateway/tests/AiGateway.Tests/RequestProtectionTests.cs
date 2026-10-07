using System.Net;
using System.Text;

namespace AiGateway.Tests;

/// <summary>
/// Middleware proteksi request untuk API admin: batas ukuran body (413) dan header keamanan + no-store.
/// Limit auth dibiarkan default di kelas ini supaya tidak bentrok dengan request lain.
/// </summary>
public class RequestProtectionTests(TestDb db) : GatewayTestBase(db)
{
    [Fact]
    public async Task Admin_body_over_the_limit_is_rejected_with_413()
    {
        // Security:MaxAdminRequestBytes default 1 MiB; body di atasnya harus ditolak middleware, bukan oleh
        // endpoint (jangan bergantung pada kredensial).
        var filler = new string('x', (1024 * 1024) + 16);
        using var content = new StringContent("{\"email\":\"" + filler + "\"}", Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync("/admin/api/auth/login", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request_too_large", ErrorCode(await BodyAsync(response)));
    }

    [Fact]
    public async Task Admin_api_responses_have_security_headers_and_are_not_cached()
    {
        // Tanpa token: 401, tetapi header keamanan dan no-store tetap dipasang middleware untuk semua respons.
        using var response = await Client.GetAsync("/admin/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}
