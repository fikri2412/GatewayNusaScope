using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiGateway.Core.Proxy;

/// <summary>
/// Akses JSON untuk adaptor family API. Indexer <see cref="JsonNode"/> melempar bila node bukan objek; input klien dan
/// upstream tidak dipercaya, jadi semua pembacaan field lewat sini.
/// </summary>
internal static class JsonAccess
{
    public static JsonNode? Get(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? LongOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    /// <summary>Token terukur: nol sah; hilang, negatif, overflow, atau tipe lain tidak diketahui.</summary>
    public static int? TokenCount(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var count) && count >= 0) return count;
        return value.TryGetValue<long>(out var wide) && wide is >= 0 and <= int.MaxValue ? (int)wide : null;
    }

    public static JsonObject? ParseObject(string? json)
    {
        try { return json is null ? null : JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Teks dari isi pesan yang pada peran ini hanya boleh berisi teks (system/developer, tool): string atau larik
    /// bagian <c>{type:"text",text}</c>. Bagian non-teks ditolak, bukan dibuang diam-diam.
    /// </summary>
    public static bool TryTextOf(JsonNode? content, out string text, out string error)
    {
        error = "";
        switch (content)
        {
            case null:
                text = "";
                return true;
            case JsonValue value when value.TryGetValue<string>(out var plain):
                text = plain;
                return true;
            case JsonArray parts:
                var builder = new StringBuilder();
                foreach (var part in parts)
                {
                    if (Str(Get(part, "type")) != "text")
                    {
                        text = "";
                        error = $"Only text content parts are supported for this role, not '{Str(Get(part, "type"))}'.";
                        return false;
                    }
                    builder.Append(Str(Get(part, "text")));
                }
                text = builder.ToString();
                return true;
            default:
                text = "";
                error = "Message content must be a string or an array of parts.";
                return false;
        }
    }
}
