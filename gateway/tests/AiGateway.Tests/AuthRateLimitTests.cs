using System.Net;
using System.Net.Http.Json;

namespace AiGateway.Tests;

/// <summary>
/// Rate limit endpoint auth per IP. Hanya satu test di kelas ini: <c>Security:AuthRequestsPerMinute = 1</c>
/// berlaku untuk seluruh host kelas ini, jadi request auth lain akan ikut tertolak.
/// </summary>
public class AuthRateLimitTests(TestDb db) : GatewayTestBase(db, authRequestsPerMinute: 1)
{
    [Fact]
    public async Task Second_login_in_the_window_gets_429_with_retry_after()
    {
        var email = $"nobody-{Guid.NewGuid():N}@example.test";

        using (var first = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = "wrong-password-123" }))
            Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        using var second = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = "wrong-password-123" });

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("auth_rate_limited", ErrorCode(await BodyAsync(second)));
        var retryAfter = second.Headers.RetryAfter;
        Assert.NotNull(retryAfter);
        // Sisa jendela fixed 1 menit: nilainya bergantung waktu test, jadi diperiksa rentangnya.
        Assert.InRange(retryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
    }
}
