using System.Net;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// GA-08: route yang katalog template-nya menyatakan family yang tidak bisa diteruskan (mis. <c>google_generate</c>)
/// tidak boleh dikirim dengan protokol Chat Completions. Provider custom tanpa template dan model di luar katalog
/// tetap OpenAI-compatible yang sah. "Siap dipakai" di <c>/v1/models</c> mengikuti eligibilitas yang sama.
/// </summary>
public class UnsupportedFamilyTests(TestDb db) : FamilyProxyTestBase(db)
{
    private async Task<JsonNode> ModelAsync(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new("Bearer", apiKey);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ((JsonArray)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!).Single()!;
    }

    [Fact]
    public async Task A_route_with_an_unsupported_family_is_never_sent_to_the_provider()
    {
        var s = await SetupFamilyAsync(ApiFamilies.GoogleGenerate);

        var response = await PostChatAsync(s.ApiKey, Chat());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("unsupported_upstream_family", (string?)body["error"]!["code"]);
        Assert.Empty(Upstream.Requests); // tidak ada panggilan dengan protokol yang salah
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal((UsageStatuses.Error, 0, "unsupported_upstream_family"), (log.Status, log.Attempts, log.DeniedReason));
    }

    [Fact]
    public async Task Readiness_is_false_when_the_only_route_has_an_unsupported_family()
    {
        var s = await SetupFamilyAsync(ApiFamilies.GoogleGenerate);

        var model = await ModelAsync(s.ApiKey);

        Assert.Equal((false, "no_available_provider"), ((bool?)model["readiness"]!["available"], (string?)model["readiness"]!["reason"]));
    }

    [Fact]
    public async Task An_unsupported_route_falls_back_to_a_forwardable_route()
    {
        var s = await SetupFamilyAsync(ApiFamilies.GoogleGenerate); // route 1: family tidak didukung (up1.test)
        await WithTenantAsync(s.TenantId, async (prov, ctx) =>
        {
            var second = await prov.CreateCustomProviderAsync("up2", "https://up2.test/v1", "sk-second-5678", null, null, null, default);
            ctx.ModelRoutes.Add(new ModelRoute { ModelId = s.ModelId, ProviderId = second.Id, UpstreamModel = "real-model-2", Priority = 1 });
            await ctx.SaveChangesAsync();
            return 0;
        });
        Upstream.Respond = _ => FakeUpstream.Json(200, FakeUpstream.OkBody);

        var response = await PostChatAsync(s.ApiKey, Chat());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("up2.test", Assert.Single(Upstream.Requests).Uri.Host); // hanya route yang bisa diteruskan yang dipanggil
        var model = await ModelAsync(s.ApiKey);
        Assert.Equal(true, (bool?)model["readiness"]!["available"]);
    }

    [Fact]
    public async Task A_custom_provider_without_a_template_is_still_forwarded_as_chat_completions()
    {
        var s = await SetupAsync(); // provider custom, tanpa template/katalog

        var response = await PostChatAsync(s.ApiKey, Chat());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("/chat/completions", Assert.Single(Upstream.Requests).Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(ApiFamilies.AnthropicMessages)]
    [InlineData(ApiFamilies.OpenAiResponses)]
    public async Task Forwardable_non_chat_families_stay_ready(string family)
    {
        var s = await SetupFamilyAsync(family);

        var model = await ModelAsync(s.ApiKey);

        Assert.Equal(true, (bool?)model["readiness"]!["available"]);
    }
}
