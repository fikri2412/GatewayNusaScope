using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using AiGateway.Core.Proxy;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Catalog;

public sealed record ParsedModel(
    string Id, string Name, string ApiFamily, bool Disabled, int? Context, int? MaxInput, int? MaxOutput,
    bool Tools, bool Vision, decimal? Input, decimal? Output, decimal? CacheRead, decimal? CacheWrite,
    IReadOnlyList<PriceTier> ExtraTiers);

public sealed record ParsedProvider(string Key, string Name, string BaseUrl, IReadOnlyList<ParsedModel> Models);

public sealed record CatalogSyncResult(
    int TemplatesAdded, int ModelsAdded, int ModelsUpdated, int ModelsSkippedManual, int ModelsUnsupported);

/// <summary>
/// Menarik katalog dari OpenCode Console: <c>GET /console/api/v2/config</c> dengan service key (izin apa pun
/// boleh membaca). Mengisi <c>provider_templates</c> dan <c>catalog_models</c> (harga USD per 1 juta token, batas
/// konteks, kemampuan, keluarga API, status disabled). Key tidak disimpan. Baris bersumber <c>manual</c> tidak ditimpa.
/// </summary>
public sealed class OpenCodeCatalogSync(
    GatewayDbContext db, IHttpClientFactory httpFactory, OutboundSecurityPolicy outbound, IOptions<ProxyOptions> proxyOptions)
{
    public async Task<CatalogSyncResult> SyncAsync(string serviceKey, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceKey)) throw GatewayException.BadRequest("missing_key", "Service key OpenCode wajib diisi.");
        var uri = outbound.RequireHttpsUri(url, "invalid_catalog_url"); // URL katalog pun lewat pemeriksaan anti-SSRF yang sama

        JsonNode? root;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + serviceKey);
            using var response = await httpFactory.CreateClient(ChatProxy.HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new GatewayException(502, "catalog_sync_failed", $"OpenCode menjawab HTTP {(int)response.StatusCode}.");
            root = JsonNode.Parse(await BoundedContent.ReadStringAsync(response.Content, proxyOptions.Value.MaxResponseBytes, ct));
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or ResponseTooLargeException or OperationCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            throw new GatewayException(502, "catalog_sync_failed", "Katalog OpenCode tidak bisa dibaca.");
        }

        IReadOnlyList<ParsedProvider> providers;
        try { providers = Parse(root ?? throw new GatewayException(502, "catalog_sync_failed", "Respons katalog kosong.")); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new GatewayException(502, "catalog_sync_failed", "Format katalog OpenCode tidak dikenali.");
        }

        int templatesAdded = 0, added = 0, updated = 0, skipped = 0, unsupported = 0;
        // Satu transaksi untuk seluruh sinkronisasi: data upstream yang tidak muat kolom (atau melanggar CHECK)
        // membatalkan semuanya, bukan menyisakan katalog separuh jalan.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var provider in providers)
            {
                var template = await db.ProviderTemplates.FirstOrDefaultAsync(t => t.Code == provider.Key, ct);
                if (template is null)
                {
                    template = new ProviderTemplate
                    {
                        Code = provider.Key, Name = provider.Name,
                        DefaultBaseUrl = provider.BaseUrl.TrimEnd('/') + "/",
                    };
                    db.ProviderTemplates.Add(template);
                    templatesAdded++;
                }
                template.SyncKind ??= SyncKinds.OpenCodeConfig;
                template.SyncUrl ??= url;
                await db.SaveChangesAsync(ct);

                var existing = await db.CatalogModels.Where(m => m.TemplateId == template.Id).ToDictionaryAsync(m => m.UpstreamModel, ct);
                foreach (var m in provider.Models)
                {
                    if (m.ApiFamily != ApiFamilies.OpenAiChat) unsupported++;
                    if (existing.TryGetValue(m.Id, out var row))
                    {
                        if (row.Source == CatalogSources.Manual) { skipped++; continue; }
                        Apply(row, m);
                        updated++;
                    }
                    else
                    {
                        var row2 = new CatalogModel { TemplateId = template.Id, UpstreamModel = m.Id, DisplayName = m.Name };
                        Apply(row2, m);
                        db.CatalogModels.Add(row2);
                        added++;
                    }
                }
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new GatewayException(502, "catalog_sync_failed", "Katalog OpenCode tidak bisa disimpan (data upstream tidak valid).");
        }
        return new CatalogSyncResult(templatesAdded, added, updated, skipped, unsupported);
    }

    private static void Apply(CatalogModel row, ParsedModel m)
    {
        row.DisplayName = m.Name;
        row.ApiFamily = m.ApiFamily;
        row.Enabled = !m.Disabled;
        row.ContextWindow = m.Context;
        row.MaxInputTokens = m.MaxInput;
        row.MaxOutputTokens = m.MaxOutput;
        row.SupportsTools = m.Tools;
        row.SupportsVision = m.Vision;
        row.InputPricePer1M = m.Input;
        row.OutputPricePer1M = m.Output;
        row.CacheReadPricePer1M = m.CacheRead;
        row.CacheWritePricePer1M = m.CacheWrite;
        row.ExtraTiersJson = m.ExtraTiers.Count == 0 ? null : JsonSerializer.Serialize(m.ExtraTiers);
        row.Source = CatalogSources.Discovered;
    }

    /// <summary>
    /// Parse format config V2 OpenCode: <c>{providers:{key:{name,settings.baseURL,package,models:{id:{...}}}}}</c>.
    /// Provider yang kuncinya tidak muat sebagai <c>code</c> template atau base URL-nya bukan https &lt;= 500 karakter
    /// dilewati: baris seperti itu tidak akan lolos validasi saat dipakai, dan tidak boleh menggagalkan sinkronisasi.
    /// </summary>
    public static IReadOnlyList<ParsedProvider> Parse(JsonNode root)
    {
        var result = new List<ParsedProvider>();
        if (root["providers"] is not JsonObject providers) return result;

        foreach (var (key, node) in providers)
        {
            if (node is not JsonObject p || p["models"] is not JsonObject models) continue;
            var baseUrl = p["settings"]?["baseURL"]?.GetValue<string>() ?? "";
            if (!PlatformService.IsValidCode(key) || !IsUsableBaseUrl(baseUrl)) continue;
            var providerPackage = p["package"]?.GetValue<string>();
            var parsed = new List<ParsedModel>();
            foreach (var (id, mn) in models)
            {
                if (mn is not JsonObject m) continue;
                var family = FamilyOf(m["package"]?.GetValue<string>() ?? providerPackage);
                if (family is null) continue;

                var tiers = new List<PriceTier>();
                PriceTier? baseTier = null;
                foreach (var c in (m["cost"] as JsonArray) ?? [])
                {
                    if (c is not JsonObject cost) continue;
                    var tier = new PriceTier(0, Dec(cost["input"]) ?? 0, Dec(cost["output"]) ?? 0, Dec(cost["cache"]?["read"]), Dec(cost["cache"]?["write"]));
                    if (cost["tier"] is JsonObject t)
                    {
                        // tier konteks: berlaku bila prompt melebihi "size" token.
                        if (t["type"]?.GetValue<string>() == "context" && t["size"]?.GetValue<int>() is { } size)
                            tiers.Add(tier with { MinInputTokens = size + 1 });
                    }
                    else baseTier ??= tier;
                }

                var caps = m["capabilities"];
                var input = caps?["input"] as JsonArray;
                parsed.Add(new ParsedModel(
                    id, m["name"]?.GetValue<string>() ?? id, family,
                    m["disabled"]?.GetValue<bool>() ?? false,
                    Int(m["limit"]?["context"]), Int(m["limit"]?["input"]), Int(m["limit"]?["output"]),
                    caps?["tools"]?.GetValue<bool>() ?? false,
                    input?.Any(x => x?.GetValue<string>() == "image") ?? false,
                    baseTier?.Input, baseTier?.Output, baseTier?.CacheRead, baseTier?.CacheWrite, tiers));
            }
            var name = p["name"]?.GetValue<string>() ?? key;
            result.Add(new ParsedProvider(key, name.Length <= 100 ? name : name[..100], baseUrl, parsed));
        }
        return result;
    }

    /// <summary>Base URL template harus URL https absolut yang muat kolom <c>nvarchar(500)</c> setelah diberi '/' di akhir.</summary>
    private static bool IsUsableBaseUrl(string baseUrl) =>
        baseUrl.Length is > 0 and < 500 && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string? FamilyOf(string? package) => package switch
    {
        "aisdk:@ai-sdk/openai-compatible" => ApiFamilies.OpenAiChat,
        "aisdk:@ai-sdk/openai" => ApiFamilies.OpenAiResponses,
        "aisdk:@ai-sdk/anthropic" => ApiFamilies.AnthropicMessages,
        "aisdk:@ai-sdk/google" => ApiFamilies.GoogleGenerate,
        _ => null, // keluarga yang tidak dikenal (mis. systemone) dilewati
    };

    private static decimal? Dec(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
