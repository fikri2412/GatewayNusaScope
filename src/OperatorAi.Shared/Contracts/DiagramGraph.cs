using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperatorAi.Shared.Contracts;

public sealed record DiagramNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label);

public sealed record DiagramEdge(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("label")] string Label);

/// <summary>
/// Graf alur logika (6D) yang disusun AI dari PRD. AI tidak pernah menulis Mermaid: hanya graf terbatas ini yang
/// diterima, divalidasi ketat (id unik, rujukan node, enum <see cref="Kinds"/>, teks plain, batas ukuran, tanpa
/// field tambahan), lalu serializer aplikasi yang membentuk sintaks Mermaid dengan id dan escaping terkontrol.
/// </summary>
public sealed record DiagramGraph(
    [property: JsonPropertyName("judul")] string Judul,
    [property: JsonPropertyName("ringkasan")] string Ringkasan,
    [property: JsonPropertyName("nodes")] IReadOnlyList<DiagramNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<DiagramEdge> Edges,
    [property: JsonPropertyName("catatan")] IReadOnlyList<string> Catatan)
{
    public const int MaxNodes = 50, MaxEdges = 100, MaxLabelChars = 200, MaxSummaryChars = 2000,
        MaxNotes = 30, MaxNoteChars = 500;

    public const string Start = "start", Process = "process", Decision = "decision", End = "end";

    public static readonly string[] Kinds = [Start, Process, Decision, End];

    // Field yang tidak dikenal (mis. "mermaid", "style", "click") ditolak, bukan diabaikan diam-diam.
    private static readonly JsonSerializerOptions Strict =
        new(JsonSerializerOptions.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>
    /// Output AI adalah input tidak tepercaya: buang pembungkus blok kode bila ada, tolak yang tidak sesuai skema dengan
    /// <see cref="FormatException"/>, dan kembalikan graf dengan teks yang sudah dinormalisasi (spasi dirapatkan).
    /// </summary>
    public static DiagramGraph Parse(string json)
    {
        var text = string.IsNullOrWhiteSpace(json) ? "" : StripFence(json);
        if (text.Length == 0) throw new FormatException("Output AI kosong; JSON graf alur wajib ada.");

        DiagramGraph? graph;
        try
        {
            graph = JsonSerializer.Deserialize<DiagramGraph>(text, Strict);
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Path is { Length: > 0 } path
                ? $"JSON graf alur tidak valid pada {path}."
                : "Output AI bukan JSON graf alur yang valid.", ex);
        }

        if (graph is null) throw new FormatException("JSON graf alur harus berupa objek.");
        return Validate(graph);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    private static DiagramGraph Validate(DiagramGraph g)
    {
        var judul = Plain(g.Judul, "judul", 200);
        var ringkasan = Plain(g.Ringkasan, "ringkasan", MaxSummaryChars);

        if (g.Nodes is null) throw new FormatException("Field 'nodes' wajib ada.");
        if (g.Edges is null) throw new FormatException("Field 'edges' wajib ada.");
        if (g.Catatan is null) throw new FormatException("Field 'catatan' wajib ada (boleh array kosong).");
        if (g.Nodes.Count < 2) throw new FormatException("Graf alur minimal memuat 2 node.");
        if (g.Nodes.Count > MaxNodes) throw new FormatException($"Graf alur maksimal {MaxNodes} node (ada {g.Nodes.Count}).");
        if (g.Edges.Count < 1) throw new FormatException("Graf alur minimal memuat 1 edge.");
        if (g.Edges.Count > MaxEdges) throw new FormatException($"Graf alur maksimal {MaxEdges} edge (ada {g.Edges.Count}).");
        if (g.Catatan.Count > MaxNotes) throw new FormatException($"Field 'catatan' maksimal {MaxNotes} butir (ada {g.Catatan.Count}).");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<DiagramNode>(g.Nodes.Count);
        for (var i = 0; i < g.Nodes.Count; i++)
        {
            var n = g.Nodes[i] ?? throw new FormatException($"nodes[{i}] tidak boleh null.");
            if (!IsId(n.Id)) throw new FormatException($"nodes[{i}].id harus diawali huruf lalu huruf/angka/garis bawah (maks 32 karakter).");
            if (!ids.Add(n.Id)) throw new FormatException($"nodes[{i}].id '{n.Id}' duplikat.");
            if (!Kinds.Contains(n.Kind, StringComparer.Ordinal))
                throw new FormatException($"nodes[{i}].kind harus salah satu dari {string.Join("/", Kinds)}.");
            nodes.Add(n with { Label = Plain(n.Label, $"nodes[{i}].label", MaxLabelChars) });
        }

        var edges = new List<DiagramEdge>(g.Edges.Count);
        for (var i = 0; i < g.Edges.Count; i++)
        {
            var e = g.Edges[i] ?? throw new FormatException($"edges[{i}] tidak boleh null.");
            if (e.From is null || !ids.Contains(e.From)) throw new FormatException($"edges[{i}].from merujuk node yang tidak ada.");
            if (e.To is null || !ids.Contains(e.To)) throw new FormatException($"edges[{i}].to merujuk node yang tidak ada.");
            // Label edge boleh kosong atau hilang; node wajib berlabel.
            edges.Add(e with { Label = Plain(e.Label ?? "", $"edges[{i}].label", MaxLabelChars, required: false) });
        }

        var notes = new List<string>(g.Catatan.Count);
        for (var i = 0; i < g.Catatan.Count; i++) notes.Add(Plain(g.Catatan[i], $"catatan[{i}]", MaxNoteChars));

        return new DiagramGraph(judul, ringkasan, nodes, edges, notes);
    }

    /// <summary>Teks plain satu baris: spasi/baris baru dirapatkan, karakter kontrol/format tak terlihat ditolak.</summary>
    private static string Plain(string? value, string name, int max, bool required = true)
    {
        var text = new StringBuilder(value?.Length ?? 0);
        var pendingSpace = false;
        foreach (var c in value ?? "")
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                throw new FormatException($"Field '{name}' memuat karakter kontrol/tak terlihat.");
            if (pendingSpace) text.Append(' ');
            pendingSpace = false;
            text.Append(c);
        }

        if (required && text.Length == 0) throw new FormatException($"Field '{name}' wajib diisi.");
        if (text.Length > max) throw new FormatException($"Field '{name}' maksimal {max} karakter (ada {text.Length}).");
        return text.ToString();
    }

    private static bool IsId(string? id)
    {
        if (id is not { Length: > 0 and <= 32 } || !char.IsAsciiLetter(id[0])) return false;
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>Buang pembungkus markdown hanya di luar objek; backtick di dalam nilai JSON tetap data.</summary>
    private static string StripFence(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstLine = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLine < 0 || lastFence <= firstLine) return text;
        return text[(firstLine + 1)..lastFence].Trim();
    }
}
