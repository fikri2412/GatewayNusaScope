using System.Net;
using System.Text;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Dasar test route non-chat: family ditentukan katalog template provider (provider custom tanpa template selalu
/// Chat Completions, sudah dicakup ProxyTests), jadi skenario dibuat dengan template + baris katalog ber-family.
/// </summary>
public abstract class FamilyProxyTestBase(TestDb db) : GatewayTestBase(db)
{
    protected async Task<Scenario> SetupFamilyAsync(string family)
    {
        var s = await SetupAsync();
        await WithTenantAsync(s.TenantId, async (_, ctx) =>
        {
            var template = new ProviderTemplate { Code = "fam-" + Guid.NewGuid().ToString("N")[..8], Name = "T", DefaultBaseUrl = "https://up1.test/v1/" };
            ctx.ProviderTemplates.Add(template);
            await ctx.SaveChangesAsync();
            ctx.CatalogModels.Add(new CatalogModel { TemplateId = template.Id, UpstreamModel = "real-model", DisplayName = "Real", ApiFamily = family });
            (await ctx.Providers.SingleAsync(p => p.Id == s.ProviderId)).TemplateId = template.Id;
            await ctx.SaveChangesAsync();
            return 0;
        });
        return s;
    }

    protected static HttpResponseMessage Sse(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };

    protected static string Event(string type, string json) => $"event: {type}\ndata: {json}\n\n";

    /// <summary>Percakapan dengan dua system, tool call + hasilnya, lalu pesan user lagi; tool_choice required.</summary>
    protected const string ToolConversation = """
        {"model":"gpt-test","max_tokens":300,"tool_choice":"required",
         "tools":[{"type":"function","function":{"name":"get_time","description":"jam","parameters":{"type":"object","properties":{"tz":{"type":"string"}}}}}],
         "messages":[
           {"role":"system","content":"Aturan A"},
           {"role":"system","content":"Aturan B"},
           {"role":"user","content":"jam berapa?"},
           {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_time","arguments":"{\"tz\":\"Asia/Jakarta\"}"}}]},
           {"role":"tool","tool_call_id":"call_1","content":"10:00"},
           {"role":"user","content":"terima kasih"}]}
        """;
}
