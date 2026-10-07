using System.Net;
using AiGateway.Core.Domain;
using AiGateway.Core.Proxy;
using AiGateway.Core.Security;

namespace AiGateway.Tests;

public class ApiKeyTests
{
    [Fact]
    public void Generated_keys_roundtrip_and_only_the_exact_key_matches()
    {
        var (key, prefix, hash) = ApiKeyGenerator.Generate();

        Assert.True(ApiKeyGenerator.TryGetPrefix(key, out var parsed));
        Assert.Equal(prefix, parsed);
        Assert.StartsWith("gw_", key);
        Assert.Equal(64, hash.Length);
        Assert.True(ApiKeyGenerator.Matches(key, hash));
        Assert.False(ApiKeyGenerator.Matches(key + "x", hash));
        Assert.NotEqual(key, ApiKeyGenerator.Generate().Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gw_")]
    [InlineData("sk_0123456789_secretsecretsecret")]
    [InlineData("gw_0123456789secretsecretsecret")] // tanpa pemisah
    [InlineData("gw_ZZZZZZZZZZ_secretsecretsecret")] // prefix bukan hex
    [InlineData("gw_0123456789_")]
    public void Malformed_keys_are_rejected_before_any_lookup(string key) =>
        Assert.False(ApiKeyGenerator.TryGetPrefix(key, out _));
}

public class IpAllowListTests
{
    [Theory]
    [InlineData(null, "198.51.100.1", true)]
    [InlineData("[]", "198.51.100.1", true)]
    [InlineData("""["198.51.100.1"]""", "198.51.100.1", true)]
    [InlineData("""["198.51.100.1"]""", "198.51.100.2", false)]
    [InlineData("""["10.0.0.0/8"]""", "10.20.30.40", true)]
    [InlineData("""["10.0.0.0/8"]""", "11.0.0.1", false)]
    [InlineData("""["2001:db8::/32"]""", "2001:db8::1", true)]
    [InlineData("""["198.51.100.1"]""", "::ffff:198.51.100.1", true)] // IPv4 yang dipetakan ke IPv6
    [InlineData("""["::ffff:198.51.100.1"]""", "198.51.100.1", true)] // entri IPv4-mapped, klien IPv4
    [InlineData("""["::ffff:198.51.100.1"]""", "::ffff:198.51.100.1", true)] // kedua sisi IPv4-mapped
    [InlineData("""["::ffff:198.51.100.0/120"]""", "198.51.100.1", true)] // CIDR IPv4-mapped
    [InlineData("""["::ffff:198.51.100.0/120"]""", "198.51.101.1", false)]
    [InlineData("{broken", "198.51.100.1", false)] // JSON rusak ditolak (fail closed)
    public void Matches_exact_addresses_and_cidr_ranges(string? json, string ip, bool expected) =>
        Assert.Equal(expected, IpAllowList.IsAllowed(json, IPAddress.Parse(ip)));

    [Fact]
    public void Unknown_client_ip_is_denied_when_a_list_exists() =>
        Assert.False(IpAllowList.IsAllowed("""["198.51.100.1"]""", null));
}

public class RoutingAndPricingTests
{
    private static ModelRoute Route(int priority, int weight, string upstream) =>
        new() { UpstreamModel = upstream, Priority = priority, Weight = weight };

    [Fact]
    public void Routes_are_ordered_by_priority_and_disabled_routes_are_dropped()
    {
        var routes = new[] { Route(2, 1, "c"), Route(0, 1, "a"), Route(1, 1, "b"), new ModelRoute { UpstreamModel = "off", Enabled = false } };

        var ordered = RouteSelector.Order(routes, new Random(1));

        Assert.Equal(["a", "b", "c"], ordered.Select(r => r.UpstreamModel).ToArray());
    }

    [Fact]
    public void Weights_split_traffic_within_a_priority_and_the_rest_become_fallbacks()
    {
        var routes = new[] { Route(0, 9, "heavy"), Route(0, 1, "light") };
        var rng = new Random(42);

        var heavyFirst = Enumerable.Range(0, 2000).Count(_ => RouteSelector.Order(routes, rng)[0].UpstreamModel == "heavy");

        Assert.InRange(heavyFirst, 1700, 1900); // ~90%
        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal(2, RouteSelector.Order(routes, rng).Count));
    }

    private static ModelPrice Price(DateTime from, int min, decimal input, decimal output, decimal? cacheRead = null) =>
        new() { EffectiveFrom = from, MinInputTokens = min, InputPricePer1M = input, OutputPricePer1M = output, CacheReadPricePer1M = cacheRead };

    [Fact]
    public void Cost_bills_cached_prompt_tokens_at_the_cache_read_price()
    {
        var price = Price(DateTime.UtcNow, 0, 1m, 2m, cacheRead: 0.1m);

        // 6 baru x 1 + 4 cache x 0.1 + 20 output x 2 = 46.4 per juta
        Assert.Equal(0.0000464m, Pricing.Cost(price, inputTokens: 10, cachedTokens: 4, outputTokens: 20));
        // tanpa harga cache: ditagih harga input
        Assert.Equal(0.00005m, Pricing.Cost(Price(DateTime.UtcNow, 0, 1m, 2m), 10, 4, 20));
        // cached tidak boleh melebihi prompt
        Assert.Equal(Pricing.Cost(price, 10, 10, 0), Pricing.Cost(price, 10, 999, 0));
    }

    [Fact]
    public void Price_selection_uses_latest_effective_set_then_the_tier_for_the_prompt_size()
    {
        var now = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        var prices = new[]
        {
            Price(now.AddDays(-30), 0, 1m, 1m),
            Price(now.AddDays(-1), 0, 2m, 2m),
            Price(now.AddDays(-1), 200_001, 4m, 4m),
            Price(now.AddDays(+1), 0, 9m, 9m), // belum berlaku
        };

        Assert.Equal(2m, Pricing.Pick(prices, now, 200_000)!.InputPricePer1M);
        Assert.Equal(4m, Pricing.Pick(prices, now, 200_001)!.InputPricePer1M);
        Assert.Equal(1m, Pricing.Pick(prices, now.AddDays(-10), 5)!.InputPricePer1M);
        Assert.Null(Pricing.Pick(prices, now.AddDays(-60), 5));
    }
}

public class TenantConcurrencyGateTests
{
    [Fact]
    public void Slots_are_per_tenant_and_denied_until_released()
    {
        var gate = new TenantConcurrencyGate();

        Assert.True(gate.TryAcquire(7, 1));
        Assert.False(gate.TryAcquire(7, 1)); // tenant 7 penuh
        Assert.True(gate.TryAcquire(8, 1));  // tenant lain tidak ikut penuh

        gate.Release(7);
        Assert.True(gate.TryAcquire(7, 1));
        gate.Release(7);
        gate.Release(8);
    }

    [Fact]
    public void Releasing_without_a_slot_does_not_grant_extra_room()
    {
        var gate = new TenantConcurrencyGate();

        gate.Release(99);

        Assert.True(gate.TryAcquire(99, 1));
        Assert.False(gate.TryAcquire(99, 1));
    }
}
