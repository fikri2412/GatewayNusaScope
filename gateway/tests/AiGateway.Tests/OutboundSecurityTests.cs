using System.Net;
using System.Net.Sockets;
using System.Text;
using AiGateway.Core.Catalog;
using AiGateway.Core.Common;
using AiGateway.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

public class OutboundSecurityTests
{
    private static OutboundSecurityPolicy Policy(params string[] allowed) =>
        new(new OutboundSecurityOptions { AllowedPrivateNetworks = allowed });

    private static ServiceProvider BuildProvider(params string[] allowed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            allowed.SelectMany((v, i) => new[] { new KeyValuePair<string, string?>($"Security:AllowedPrivateNetworks:{i}", v) })).Build();
        var services = new ServiceCollection();
        services.AddOutboundSecurity(config);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.0", false)] [InlineData("10.255.255.255", false)] [InlineData("9.255.255.255", true)] [InlineData("11.0.0.0", true)]
    [InlineData("100.64.0.0", false)] [InlineData("100.63.255.255", true)] [InlineData("100.128.0.0", true)] // CGNAT
    [InlineData("127.0.0.1", false)] [InlineData("126.255.255.255", true)] [InlineData("128.0.0.0", true)]
    [InlineData("169.254.1.1", false)] [InlineData("169.253.255.255", true)] [InlineData("169.255.0.0", true)]
    [InlineData("172.16.0.0", false)] [InlineData("172.31.255.255", false)] [InlineData("172.15.255.255", true)] [InlineData("172.32.0.0", true)]
    [InlineData("192.168.0.0", false)] [InlineData("192.168.255.255", false)] [InlineData("192.169.0.0", true)]
    [InlineData("198.51.100.1", false)] [InlineData("198.51.99.255", true)] [InlineData("203.0.113.1", false)] [InlineData("203.0.114.1", true)]
    [InlineData("224.0.0.1", false)] [InlineData("223.255.255.255", true)] [InlineData("255.255.255.255", false)]
    [InlineData("8.8.8.8", true)] [InlineData("1.1.1.1", true)]
    [InlineData("::", false)] [InlineData("::1", false)] [InlineData("::7f00:1", false)] // ::7f00:1 = 127.0.0.1 versi IPv4-compatible
    [InlineData("fe80::1", false)] [InlineData("fec0::1", false)] [InlineData("fc00::1", false)] [InlineData("fdff::1", false)] [InlineData("fbff::1", true)]
    [InlineData("ff02::1", false)] [InlineData("100::1", false)] [InlineData("2001:db8::1", false)] [InlineData("2001:4860:4860::8888", true)]
    [InlineData("64:ff9b::7f00:1", false)] // NAT64 menuju 127.0.0.1
    [InlineData("::ffff:127.0.0.1", false)] [InlineData("::ffff:10.0.0.1", false)] [InlineData("::ffff:8.8.8.8", true)]
    public void Address_ranges_are_enforced_at_their_boundaries(string ip, bool allowed) =>
        Assert.Equal(allowed, Policy().IsAddressAllowed(IPAddress.Parse(ip)));

    [Fact]
    public void Allowed_networks_open_only_the_configured_range()
    {
        var policy = Policy("10.0.0.0/8", "192.168.7.7");
        Assert.True(policy.IsAddressAllowed(IPAddress.Parse("10.20.30.40")));
        Assert.True(policy.IsAddressAllowed(IPAddress.Parse("::ffff:10.20.30.40"))); // mapped memakai rentang IPv4
        Assert.True(policy.IsAddressAllowed(IPAddress.Parse("192.168.7.7")));
        Assert.False(policy.IsAddressAllowed(IPAddress.Parse("192.168.7.8")));
        Assert.False(policy.IsAddressAllowed(IPAddress.Parse("127.0.0.1"))); // tidak ikut terbuka
        Assert.False(policy.IsAddressAllowed(IPAddress.Parse("172.16.0.1"))); // privat lain di luar rentang yang dibuka
    }

    [Fact]
    public void Allowed_hosts_are_matched_exactly_after_normalization()
    {
        var policy = Policy("db.internal.example");
        Assert.True(policy.IsHostAllowed("db.internal.example"));
        Assert.True(policy.IsHostAllowed("DB.Internal.Example."));
        Assert.False(policy.IsHostAllowed("evil.example"));
        Assert.False(policy.IsHostAllowed("db.internal.example.evil"));
    }

    [Theory]
    [InlineData("bukan alamat!!")]
    [InlineData("host name")]
    [InlineData("10.0.0.0/99")]
    [InlineData("")]
    public void Invalid_config_entries_fail_fast(string entry)
    {
        var errors = OutboundSecurityPolicy.ValidateOptions(new OutboundSecurityOptions { AllowedPrivateNetworks = [entry] });
        Assert.Single(errors);
        Assert.Contains(entry, errors[0]);
        Assert.Throws<InvalidOperationException>(() => Policy(entry));
    }

    [Fact]
    public void Valid_config_entries_are_accepted()
    {
        var options = new OutboundSecurityOptions { AllowedPrivateNetworks = ["10.0.0.0/8", "192.168.7.7", "::1", "db.internal.example"] };
        Assert.Empty(OutboundSecurityPolicy.ValidateOptions(options));
        _ = new OutboundSecurityPolicy(options);
    }

    [Theory]
    [InlineData("http://x.test/v1")]
    [InlineData("ftp://x.test/v1")]
    [InlineData("https://user:pw@x.test/v1")]
    [InlineData("https://x.test/v1?k=v")]
    [InlineData("https://x.test/v1#frag")]
    [InlineData("not a url")]
    [InlineData("https://127.0.0.1/v1")]
    [InlineData("https://10.1.2.3/v1")]
    [InlineData("https://[::1]/v1")]
    [InlineData("https://[::ffff:10.1.2.3]/v1")]
    public void Unsafe_urls_are_rejected_at_the_trust_boundary(string url)
    {
        var ex = Assert.Throws<GatewayException>(() => Policy().RequireHttpsUri(url, "invalid_base_url"));
        Assert.Equal((400, "invalid_base_url"), (ex.Status, ex.Code));
    }

    [Fact]
    public void Public_literal_and_hostname_urls_pass_the_boundary_check()
    {
        var policy = Policy();
        Assert.Equal("https://api.example.com/v1/", policy.RequireHttpsUri("https://api.example.com/v1/", "x").ToString());
        Assert.Equal("https://8.8.8.8/v1", policy.RequireHttpsUri("https://8.8.8.8/v1", "x").ToString());
        Assert.NotNull(Policy("10.0.0.0/8").RequireHttpsUri("https://10.1.2.3/v1", "x")); // literal privat yang diizinkan eksplisit
    }

    [Fact]
    public void Handler_disables_redirects_and_environment_proxy()
    {
        using var sp = BuildProvider();
        using var handler = OutboundSecurity.CreateHandler(sp);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task Client_cannot_reach_loopback_or_private_literals()
    {
        using var sp = BuildProvider();
        using var client = new HttpClient(OutboundSecurity.CreateHandler(sp));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://127.0.0.1/"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://[::ffff:a00:1]/")); // ::ffff:a00:1 = 10.0.0.1
    }

    /// <summary>
    /// Uji jaringan sungguhan (bukan mock). Jalankan dengan GATEWAY_NETWORK_SMOKE=1:
    /// <c>dotnet test --filter Network_smoke</c>. Lewati bila variabel tidak diset.
    /// </summary>
    [Fact]
    public async Task Network_smoke_real_dns_tls_and_socket()
    {
        if (Environment.GetEnvironmentVariable("GATEWAY_NETWORK_SMOKE") != "1") return;

        // 1) Publik sungguhan: DNS + TLS + validasi sertifikat.
        using (var sp = BuildProvider())
        using (var client = new HttpClient(OutboundSecurity.CreateHandler(sp)))
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://example.com/")).StatusCode);

        // 2) Privat yang diizinkan eksplisit: socket sungguhan ke listener loopback.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var conn = await listener.AcceptTcpClientAsync();
                var stream = conn.GetStream();
                var buffer = new byte[2048];
                await stream.ReadExactlyAsync(buffer.AsMemory());
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
            });
            using (var sp = BuildProvider("127.0.0.0/8"))
            using (var client = new HttpClient(OutboundSecurity.CreateHandler(sp)))
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"http://127.0.0.1:{port}/")).StatusCode);
            await serve;
        }
        finally { listener.Stop(); }

        // 3) Hostname yang resolve ke loopback tetap ditolak tanpa izin eksplisit.
        using (var sp = BuildProvider())
        using (var client = new HttpClient(OutboundSecurity.CreateHandler(sp)))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://localhost/"));
    }
}

/// <summary>Bukti jalur nyata: validasi anti-SSRF aktif lewat DI gateway (provider tenant dan sinkronisasi katalog).</summary>
public class OutboundSecurityWiringTests(TestDb db) : GatewayTestBase(db)
{
    [Fact]
    public async Task Provider_creation_rejects_unsafe_urls_but_accepts_public_literal()
    {
        var s = await SetupAsync();
        await WithTenantAsync(s.TenantId, async (prov, _) =>
        {
            foreach (var url in new[] { "http://x.test/v1", "https://127.0.0.1/v1", "https://[::1]/v1", "https://169.254.1.1/v1" })
            {
                var ex = await Assert.ThrowsAsync<GatewayException>(() =>
                    prov.CreateCustomProviderAsync("p-" + Guid.NewGuid().ToString("N"), url, "k-0123456789", null, null, null, default));
                Assert.Equal((400, "invalid_base_url"), (ex.Status, ex.Code));
            }
            var ok = await prov.CreateCustomProviderAsync("pub", "https://8.8.8.8/v1", "k-0123456789", null, null, null, default);
            Assert.Equal("https://8.8.8.8/v1", ok.BaseUrl);
            return true;
        });
    }

    [Fact]
    public async Task Catalog_sync_rejects_non_https_url_before_any_lookup()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<OpenCodeCatalogSync>();
        var ex = await Assert.ThrowsAsync<GatewayException>(() => sync.SyncAsync("oc_sk_test", "http://catalog.test/c", default));
        Assert.Equal((400, "invalid_catalog_url"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task Discovery_revalidates_base_url_leftover_from_before_the_rule()
    {
        var s = await SetupAsync();
        var providerId = await WithTenantAsync(s.TenantId, async (prov, _) =>
            (await prov.CreateCustomProviderAsync("disc", "https://up1.test/v1", UpstreamKey, null, null, "models", default)).Id);

        await using (var ctx = Db.NewContext(s.TenantId))
        {
            var row = await ctx.Providers.SingleAsync(p => p.Id == providerId);
            row.BaseUrl = "http://127.0.0.1/v1"; // seolah baris lama dari sebelum aturan https
            await ctx.SaveChangesAsync();
        }

        var ex = await WithTenantAsync(s.TenantId, (prov, _) =>
            Assert.ThrowsAsync<GatewayException>(() => prov.DiscoverModelsAsync(providerId, default)));
        Assert.Equal((400, "invalid_base_url"), (ex.Status, ex.Code));
    }
}
