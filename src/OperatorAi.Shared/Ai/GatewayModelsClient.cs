using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Options;

namespace OperatorAi.Shared.Ai;

public sealed class GatewayModelsException(Exception? inner = null)
    : Exception("Katalog model gateway sedang tidak tersedia. Coba lagi.", inner);

/// <summary>Katalog pada scope key instalasi; tidak menyimpan katalog kedua atau memilih model default.</summary>
public sealed class GatewayModelsClient(HttpClient http, IOptions<AiOptions> options)
{
    private const int MaxResponseBytes = 1024 * 1024, MaxModels = 1000;

    public async Task<IReadOnlyList<ModelInfoDto>> ListAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.Value.BaseUrl), "models"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, deadline.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("object", out var kind)
                || kind.ValueKind != JsonValueKind.String || kind.GetString() != "list"
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() > MaxModels)
                throw new FormatException("Katalog model tidak valid.");
            if (data.GetArrayLength() == 0) return Array.Empty<ModelInfoDto>();

            var models = new List<ModelInfoDto>(data.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String)
                    throw new FormatException("Alias model tidak valid.");
                var alias = ModelSnapshot.Validate(id.GetString());
                if (!seen.Add(alias)) throw new FormatException("Alias model duplikat.");
                var capabilities = OptionalObject(row, "capabilities");
                var tools = OptionalBoolean(capabilities, "tools");
                var streaming = OptionalBoolean(capabilities, "streaming");
                var vision = OptionalBoolean(capabilities, "vision");
                var readiness = OptionalObject(row, "readiness");
                var available = OptionalBoolean(readiness, "available");
                var maxOutput = OptionalPositiveInteger(OptionalObject(row, "limits"), "max_output_tokens");
                // Legacy metadata tidak dianggap terkonfirmasi. Alasan lokal/allowlisted, bukan teks remote.
                var reason = available != true ? available is null ? "readiness_unknown" : "no_eligible_route"
                    : tools is null ? "tools_unknown" : tools == false ? "tools_unsupported" : null;
                models.Add(new ModelInfoDto(alias, tools, streaming, available == true, maxOutput, reason, vision));
            }
            return models;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException
            or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new GatewayModelsException(ex);
        }
    }

    private static JsonElement? OptionalObject(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Metadata model harus berupa objek.");
        return value;
    }

    private static bool? OptionalBoolean(JsonElement? row, string name)
    {
        if (row is null || !row.Value.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException("Kapabilitas model harus bool atau null."),
        };
    }

    private static int? OptionalPositiveInteger(JsonElement? row, string name)
    {
        if (row is null || !row.Value.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt32(out var limit) || limit <= 0) throw new FormatException("Batas output model tidak valid.");
        return limit;
    }
}

public static class GatewayModelsServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayModelsClient(this IServiceCollection services) =>
        services.AddTransient(sp => new GatewayModelsClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("gateway-settings"),
            sp.GetRequiredService<IOptions<AiOptions>>()));
}
