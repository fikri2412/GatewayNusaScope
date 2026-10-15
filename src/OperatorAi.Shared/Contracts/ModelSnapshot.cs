using System.Text.Json;

namespace OperatorAi.Shared.Contracts;

/// <summary>Alias wajib milik snapshot workflow; tidak pernah memakai pilihan conversation terbaru atau default.</summary>
public static class ModelSnapshot
{
    public static string Validate(string? alias)
    {
        if (alias is not { Length: > 0 and <= 100 } || char.IsWhiteSpace(alias[0]) || char.IsWhiteSpace(alias[^1]))
            throw new FormatException("Model wajib berupa alias gateway 1–100 karakter tanpa whitespace di tepi.");
        foreach (var ch in alias)
            if (char.IsControl(ch)) throw new FormatException("Alias model mengandung karakter kontrol.");
        return alias;
    }

    public static string Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Snapshot model harus berupa objek JSON.");
        string? model = null;
        var found = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.NameEquals("model")) continue;
            if (found || property.Value.ValueKind != JsonValueKind.String)
                throw new FormatException("Snapshot model duplikat atau bukan string.");
            found = true;
            model = property.Value.GetString();
        }
        return Validate(model);
    }

    /// <summary>Hanya konfirmasi server pada saat enqueue; snapshot lama/unknown tidak menjanjikan streaming.</summary>
    public static bool SupportsStreaming(string? json) => Supports(json, "supportsStreaming");

    /// <summary>Vision hanya boleh dipakai setelah konfirmasi server saat enqueue; unknown tidak dianggap didukung.</summary>
    public static bool SupportsVision(string? json) => Supports(json, "supportsVision");

    /// <summary>Mode penalaran (low/high) yang disnapshot saat pesan masuk antrean; null = bawaan model.</summary>
    public static string? ReasoningEffort(string? json)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("reasoningEffort") && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }

    private static bool Supports(string? json, string name)
    {
        if (json is null) return false;
        using var document = JsonDocument.Parse(json);
        var found = false;
        var enabled = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException($"Snapshot {name} duplikat atau bukan boolean.");
            found = true;
            enabled = property.Value.GetBoolean();
        }
        return enabled;
    }
}
