using System.Text;
using OperatorAi.Shared.Contracts;

namespace OperatorAi.Worker.Specialist.Diagrams;

/// <summary>
/// Graf tervalidasi → sumber Mermaid flowchart. Satu-satunya pembentuk sintaks Mermaid di pipeline 6D: id node
/// dibuat aplikasi (<c>N1..Nn</c>, bukan id dari AI), semua label dibungkus tanda kutip dan karakter di luar daftar
/// aman ditulis sebagai entity code Mermaid. Tanpa directive init, click/href, HTML, atau CSS dari AI/user; gaya node
/// hanya <c>classDef</c> tetap milik aplikasi.
/// </summary>
public static class MermaidFlowchart
{
    private const string AllowedPunctuation = ".,:;!?-_/+=*@'$";

    public static string Serialize(DiagramGraph graph)
    {
        var nodeIds = new Dictionary<string, string>(graph.Nodes.Count, StringComparer.Ordinal);
        var terminals = new List<string>();
        var decisions = new List<string>();
        var text = new StringBuilder("flowchart TD\n");

        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];
            var id = "N" + (i + 1);
            nodeIds[node.Id] = id;
            var label = '"' + Escape(node.Label) + '"';
            switch (node.Kind)
            {
                case DiagramGraph.Start or DiagramGraph.End:
                    text.Append("    ").Append(id).Append("([").Append(label).Append("])\n");
                    terminals.Add(id);
                    break;
                case DiagramGraph.Decision:
                    text.Append("    ").Append(id).Append('{').Append(label).Append("}\n");
                    decisions.Add(id);
                    break;
                default:
                    text.Append("    ").Append(id).Append('[').Append(label).Append("]\n");
                    break;
            }
        }

        foreach (var edge in graph.Edges)
        {
            text.Append("    ").Append(nodeIds[edge.From]);
            text.Append(edge.Label.Length == 0 ? " --> " : " -- \"" + Escape(edge.Label) + "\" --> ");
            text.Append(nodeIds[edge.To]).Append('\n');
        }

        text.Append("    classDef terminalNode fill:#d1fae5,stroke:#047857,stroke-width:2px\n");
        text.Append("    classDef decisionNode fill:#fef3c7,stroke:#b45309,stroke-width:2px\n");
        if (terminals.Count > 0) text.Append("    class ").AppendJoin(',', terminals).Append(" terminalNode\n");
        if (decisions.Count > 0) text.Append("    class ").AppendJoin(',', decisions).Append(" decisionNode\n");
        return text.ToString();
    }

    /// <summary>
    /// Huruf, angka, spasi, dan tanda baca pada <see cref="AllowedPunctuation"/> ditulis apa adanya; karakter lain
    /// (kutip, #, &amp;, &lt;, &gt;, kurung, |, backtick, %, dst.) menjadi <c>#kode;</c> sehingga tidak pernah dibaca
    /// sebagai sintaks.
    /// </summary>
    public static string Escape(string label)
    {
        var text = new StringBuilder(label.Length);
        foreach (var rune in label.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || rune.Value == ' ' || (rune.IsAscii && AllowedPunctuation.Contains((char)rune.Value)))
                text.Append(rune.ToString());
            else
                text.Append('#').Append(rune.Value).Append(';');
        }
        return text.ToString();
    }
}
