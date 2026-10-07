using System.Net;
using AiGateway.Api.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

/// <summary>Parsing dan validasi <c>Security:TrustedProxies</c> (IP masuk KnownProxies, CIDR masuk KnownNetworks).</summary>
public class TrustedProxiesOptionsTests
{
    private static IConfiguration Config(params string[] proxies) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            proxies.Select((v, i) => new KeyValuePair<string, string?>($"Security:TrustedProxies:{i}", v))).Build();

    [Fact]
    public void Ip_and_cidr_entries_are_added_without_dropping_loopback_defaults()
    {
        var options = RequestProtection.BuildForwardedHeadersOptions(["10.1.2.3", "10.0.0.0/8", "::1"]);

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit); // satu hop saja, tidak menelan seluruh rantai X-Forwarded-For
        Assert.Contains(IPAddress.Parse("10.1.2.3"), options.KnownProxies);
        Assert.Contains(options.KnownNetworks, n => n.Contains(IPAddress.Parse("10.0.0.5")));
        Assert.Contains(IPAddress.IPv6Loopback, options.KnownProxies); // default ASP.NET Core dipertahankan
        Assert.Contains(options.KnownNetworks, n => n.Contains(IPAddress.Parse("127.0.0.1")));
    }

    [Theory]
    [InlineData("bukan-ip")]
    [InlineData("10.0.0.0/99")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.1/")]
    [InlineData("proxy.example.com")] // hostname bukan alamat proxy: harus IP/CIDR
    public void Invalid_entries_fail_with_the_entry_name(string entry)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RequestProtection.BuildForwardedHeadersOptions([entry]));
        Assert.Contains("Security:TrustedProxies", ex.Message);
        Assert.Contains(entry, ex.Message);
    }

    [Fact]
    public void Startup_rejects_invalid_trusted_proxies()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddRequestProtection(Config("proxy.example.com")));
        Assert.Contains("proxy.example.com", ex.Message);
    }
}

/// <summary>
/// X-Forwarded-For lewat pipeline sungguhan: dipakai bila peer-nya proxy loopback (default), diabaikan bila
/// peer-nya bukan proxy tepercaya. IP hasilnya terlihat di usage_logs.client_ip.
/// </summary>
public class ForwardedHeadersTests(TestDb db) : GatewayTestBase(db)
{
    [Fact]
    public async Task Forwarded_for_is_trusted_only_from_a_trusted_proxy()
    {
        var s = await SetupAsync();
        const string forwarded = "203.0.113.7"; // TEST-NET-3; bukan IP peer mana pun di test ini

        var fromProxy = await PostChatAsync(s.ApiKey, Chat(), r =>
        {
            r.Headers.Add("X-Test-Ip", "127.0.0.1"); // peer loopback: proxy tepercaya
            r.Headers.Add("X-Forwarded-For", forwarded);
        });
        var fromStranger = await PostChatAsync(s.ApiKey, Chat(), r =>
        {
            r.Headers.Add("X-Test-Ip", "198.51.100.9"); // peer langsung: header tidak boleh dipercaya
            r.Headers.Add("X-Forwarded-For", forwarded);
        });
        Assert.Equal(HttpStatusCode.OK, fromProxy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fromStranger.StatusCode);

        var proxyRequest = Guid.Parse(fromProxy.Headers.GetValues("X-Request-Id").Single());
        var strangerRequest = Guid.Parse(fromStranger.Headers.GetValues("X-Request-Id").Single());
        await using var ctx = Db.NewContext(s.TenantId);
        var ips = await ctx.UsageLogs.AsNoTracking()
            .Where(l => l.RequestId == proxyRequest || l.RequestId == strangerRequest)
            .ToDictionaryAsync(l => l.RequestId, l => l.ClientIp);
        Assert.Equal(forwarded, ips[proxyRequest]);
        Assert.Equal("198.51.100.9", ips[strangerRequest]);
    }
}
