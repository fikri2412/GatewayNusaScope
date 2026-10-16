using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiGateway.Core.Proxy;

/// <summary>
/// Menyusun satu event SSE yang datanya terpecah di beberapa baris <c>data:</c> (spesifikasi SSE menggabungkannya
/// dengan <c>\n</c>) menjadi satu objek JSON. Sengaja <b>kompatibel mundur</b>: baris yang sudah berupa objek JSON
/// utuh diproses langsung, baris yang tidak diawali <c>{</c> (mis. teks bebas atau <c>[DONE]</c>) tidak pernah ditahan,
/// dan hanya JSON yang <i>diawali <c>{</c> tetapi belum lengkap</i> ditahan sampai gabungannya valid. Sisa yang tidak
/// pernah valid diserahkan kembali lewat <see cref="TakeLeftovers"/> di batas event/akhir stream/luapan batas, supaya
/// pemanggil memperlakukannya persis seperti baris tak valid sebelumnya (diteruskan apa adanya atau diabaikan).
/// </summary>
internal sealed class SseDataAssembler
{
    // Batas menjaga biaya parse ulang (O(n) per baris tertahan) dan memori dari event yang tidak pernah lengkap.
    private const int MaxLines = 200, MaxChars = 256 * 1024;

    private readonly List<string> _raw = [];
    private readonly List<string> _payloads = [];
    private int _chars;

    /// <summary>true bila batas baris/karakter terlampaui; pemanggil harus segera memanggil <see cref="TakeLeftovers"/>.</summary>
    public bool Overflowed { get; private set; }

    public bool HasPending => _raw.Count > 0;

    /// <summary>
    /// Tawarkan satu baris <c>data:</c>. Mengembalikan objek JSON bila baris ini (sendiri, atau bersama baris yang
    /// tertahan) membentuk objek utuh. Bila tidak: <paramref name="held"/> true = baris ditahan menunggu lanjutan;
    /// false = bukan calon objek JSON, pemanggil memperlakukannya seperti baris tak valid biasa.
    /// </summary>
    public JsonObject? Offer(string rawLine, string payload, out bool held)
    {
        held = false;
        if (_raw.Count == 0)
        {
            if (!payload.TrimStart().StartsWith('{')) return null;
            if (TryParseObject(payload) is { } whole) return whole;
        }

        _raw.Add(rawLine);
        _payloads.Add(payload);
        _chars += payload.Length;
        held = true;
        if (TryParseObject(string.Join('\n', _payloads)) is { } joined)
        {
            Clear();
            held = false;
            return joined;
        }
        if (_raw.Count > MaxLines || _chars > MaxChars) Overflowed = true;
        return null;
    }

    /// <summary>Baris mentah yang masih tertahan, urut seperti diterima; mengosongkan penahan.</summary>
    public IReadOnlyList<string> TakeLeftovers()
    {
        var left = _raw.ToList();
        Clear();
        return left;
    }

    public void Clear()
    {
        _raw.Clear();
        _payloads.Clear();
        _chars = 0;
        Overflowed = false;
    }

    /// <summary>Objek JSON dari teks, atau null bila bukan JSON/bukan objek.</summary>
    internal static JsonObject? TryParseObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
