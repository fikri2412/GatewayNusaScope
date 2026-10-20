using System.Text.Json.Nodes;
using OperatorAi.Shared.Contracts;
using OperatorAi.Worker.Specialist.Diagrams;

namespace OperatorAi.Tests;

/// <summary>
/// 6D: output AI hanya diterima sebagai graf terbatas yang divalidasi ketat, dan satu-satunya sumber Mermaid adalah
/// serializer aplikasi — label hostile tidak boleh menjadi sintaks.
/// </summary>
public class DiagramGraphTests
{
    private static JsonObject Valid() => JsonNode.Parse("""
        {
          "judul": "Alur persetujuan", "ringkasan": "Permintaan diperiksa sebelum disetujui.",
          "nodes": [
            { "id": "n1", "kind": "start", "label": "Permintaan masuk" },
            { "id": "n2", "kind": "decision", "label": "Data lengkap?" },
            { "id": "n3", "kind": "end", "label": "Disetujui" }
          ],
          "edges": [ { "from": "n1", "to": "n2", "label": "" }, { "from": "n2", "to": "n3", "label": "Ya" } ],
          "catatan": ["Langkah perbaikan data belum dijelaskan dalam PRD."]
        }
        """)!.AsObject();

    private static string Fail(JsonObject graph) => Assert.Throws<FormatException>(() => DiagramGraph.Parse(graph.ToJsonString())).Message;

    [Fact]
    public void A_valid_graph_parses_and_collapses_whitespace_in_labels()
    {
        var json = Valid();
        json["nodes"]![0]!["label"] = "  Permintaan \n\t masuk  ";
        json["edges"]![0]!.AsObject().Remove("label");                      // label edge boleh hilang

        var graph = DiagramGraph.Parse(json.ToJsonString());

        Assert.Equal("Permintaan masuk", graph.Nodes[0].Label);
        Assert.Equal("", graph.Edges[0].Label);
        Assert.Equal(3, graph.Nodes.Count);
    }

    [Fact]
    public void A_markdown_fence_around_the_json_is_tolerated_but_prose_around_it_is_not()
    {
        var fenced = "```json\n" + Valid().ToJsonString() + "\n```";
        Assert.Equal(3, DiagramGraph.Parse(fenced).Nodes.Count);
        Assert.Throws<FormatException>(() => DiagramGraph.Parse("Ini grafnya: " + Valid().ToJsonString()));
    }

    [Fact]
    public void Fields_outside_the_schema_are_rejected_instead_of_ignored()
    {
        var withCode = Valid();
        withCode["mermaid"] = "flowchart TD\nA --> B";
        Fail(withCode);

        var nodeStyle = Valid();
        nodeStyle["nodes"]![0]!["style"] = "fill:#f00";
        Fail(nodeStyle);
    }

    [Fact]
    public void Node_identity_and_edge_references_must_be_consistent()
    {
        var duplicate = Valid();
        duplicate["nodes"]![1]!["id"] = "n1";
        Assert.Contains("duplikat", Fail(duplicate));

        var badKind = Valid();
        badKind["nodes"]![1]!["kind"] = "subgraph";
        Assert.Contains("kind", Fail(badKind));

        var dangling = Valid();
        dangling["edges"]![1]!["to"] = "n9";
        Assert.Contains("edges[1].to", Fail(dangling));

        var hostileId = Valid();
        hostileId["nodes"]![0]!["id"] = "n1[x]\nclick";
        Assert.Contains("nodes[0].id", Fail(hostileId));
    }

    [Fact]
    public void Size_limits_are_enforced_at_their_exact_boundary()
    {
        JsonObject Chain(int nodes, int edges)
        {
            var graph = Valid();
            var list = new JsonArray { new JsonObject { ["id"] = "s", ["kind"] = "start", ["label"] = "Mulai" } };
            for (var i = 1; i < nodes; i++) list.Add(new JsonObject { ["id"] = "p" + i, ["kind"] = "process", ["label"] = "Langkah " + i });
            graph["nodes"] = list;
            var links = new JsonArray();
            for (var i = 0; i < edges; i++) links.Add(new JsonObject { ["from"] = "s", ["to"] = "p1", ["label"] = "" });
            graph["edges"] = links;
            return graph;
        }

        Assert.Equal(DiagramGraph.MaxNodes, DiagramGraph.Parse(Chain(DiagramGraph.MaxNodes, 1).ToJsonString()).Nodes.Count);
        Assert.Contains("maksimal 50 node", Fail(Chain(DiagramGraph.MaxNodes + 1, 1)));
        Assert.Equal(DiagramGraph.MaxEdges, DiagramGraph.Parse(Chain(2, DiagramGraph.MaxEdges).ToJsonString()).Edges.Count);
        Assert.Contains("maksimal 100 edge", Fail(Chain(2, DiagramGraph.MaxEdges + 1)));
        Assert.Contains("memuat 1 edge", Fail(Chain(2, 0)));

        var label = Valid();
        label["nodes"]![0]!["label"] = new string('a', DiagramGraph.MaxLabelChars);
        Assert.Equal(DiagramGraph.MaxLabelChars, DiagramGraph.Parse(label.ToJsonString()).Nodes[0].Label.Length);
        label["nodes"]![0]!["label"] = new string('a', DiagramGraph.MaxLabelChars + 1);
        Assert.Contains("maksimal 200 karakter", Fail(label));
    }

    [Fact]
    public void A_graph_without_nodes_or_title_is_rejected()
    {
        var noNodes = Valid();
        noNodes["nodes"] = new JsonArray();
        Fail(noNodes);

        var noTitle = Valid();
        noTitle["judul"] = "   ";
        Fail(noTitle);
    }

    [Fact]
    public void Invisible_control_characters_in_text_are_rejected()
    {
        var graph = Valid();
        graph["nodes"]![0]!["label"] = "Mulai\u202Eterbalik";             // bidi override
        Assert.Contains("kontrol", Fail(graph));
    }

    [Fact]
    public void Hostile_labels_never_become_mermaid_syntax()
    {
        var graph = Valid();
        graph["nodes"]![1]!["label"] = "x\"] %%{init: {\"securityLevel\":\"loose\"}}%% click N1 href \"http://evil.test\" A[<img src=x onerror=1>]";
        graph["edges"]![1]!["label"] = "a --> b | `c` %% #1";

        var mermaid = MermaidFlowchart.Serialize(DiagramGraph.Parse(graph.ToJsonString()));
        var lines = mermaid.TrimEnd().Split('\n');

        // 1 header + 3 node + 2 edge + 2 classDef + 2 class: label tidak bisa menambah baris/pernyataan.
        Assert.Equal(10, lines.Length);
        Assert.DoesNotContain("%%", mermaid);
        Assert.DoesNotContain("<", mermaid);
        // Satu-satunya kutip ganda adalah pembatas label buatan serializer: 0 (tanpa label) atau tepat sepasang.
        Assert.All(lines, line => Assert.Contains(line.Count(c => c == '"'), new[] { 0, 2 }));
        Assert.Contains("N2{\"", mermaid);                               // node keputusan tetap rhombus bentukan aplikasi
    }

    [Fact]
    public void Escape_keeps_readable_text_and_codes_everything_that_could_be_syntax()
    {
        Assert.Equal("Cek status: OK, 5 #62; 3?", MermaidFlowchart.Escape("Cek status: OK, 5 > 3?"));
        Assert.Equal("a#34;b#35;c#40;d#41;e#124;f#96;g#37;h#38;i", MermaidFlowchart.Escape("a\"b#c(d)e|f`g%h&i"));
        Assert.Equal("Pembayaran Rp5.000.000 é 日本", MermaidFlowchart.Escape("Pembayaran Rp5.000.000 é 日本"));
        Assert.Equal("#128512;", MermaidFlowchart.Escape("\U0001F600"));         // di luar BMP → satu kode, bukan dua surrogate
    }

    [Fact]
    public void Node_ids_in_the_output_are_application_ids_not_the_ids_from_the_AI()
    {
        var graph = Valid();
        graph["nodes"]![0]!["id"] = "end";                                // kata kunci Mermaid yang merusak diagram bila dipakai apa adanya
        graph["edges"]![0]!["from"] = "end";

        var mermaid = MermaidFlowchart.Serialize(DiagramGraph.Parse(graph.ToJsonString()));

        Assert.Contains("N1([\"Permintaan masuk\"])", mermaid);
        Assert.Contains("N1 --> N2", mermaid);
        Assert.DoesNotContain("end -->", mermaid);
    }
}
