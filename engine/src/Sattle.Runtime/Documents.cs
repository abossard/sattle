using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Sattle.Core;

namespace Sattle.Runtime;

/// <summary>Document projections (PROTOCOL.md, Version 3.4): a graph whose output ontology is sattle.doc holds Block entities, and this one renderer turns them into Markdown. Rendering is a function of the graph state: blocks are ordered by (order, id), data text is escaped, so equal states give equal bytes and one digest per revision.</summary>
public static class Documents
{
    public const string Module = "sattle.doc";
    public sealed record Block(string Id, string Kind, string Order, Props Props)
    {
        public string? Text(string p) => Props.Text("d:" + Module + "/prop:" + p);
        public long? Int(string p) => Props.Get("d:" + Module + "/prop:" + p) is IntegerValue i ? (long)i.N : null;
    }

    public static List<Block> BlocksOf(Graph g) =>
        [.. g.Entities.Where(e => Ids.Name(e.Type) == "Block" && Ids.ModuleOf(e.Type) == Module)
            .Select(e => new Block(e.Id, e.Props.Text("d:" + Module + "/prop:kind") ?? "paragraph", e.Props.Text("d:" + Module + "/prop:order") ?? "", e.Props))
            .OrderBy(b => b.Order, StringComparer.Ordinal).ThenBy(b => b.Id, StringComparer.Ordinal)];

    const string Special = "\\`*_{}[]<>()#+-.!|~";
    /// <summary>Data text cannot create structure: markdown punctuation is escaped and line breaks become spaces.</summary>
    public static string Escape(string? s)
    {
        if (s is null) return "";
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch is '\r' or '\n') { sb.Append(' '); continue; }
            if (Special.Contains(ch)) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static string[] Cells(Block b) => [Escape(b.Text("a")), Escape(b.Text("b")), Escape(b.Text("c")), b.Int("n") is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : Escape(b.Text("d"))];

    public static string Markdown(IReadOnlyList<Block> blocks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            switch (b.Kind)
            {
                case "heading": sb.Append(new string('#', (int)Math.Clamp(b.Int("level") ?? 1, 1, 6))).Append(' ').Append(Escape(b.Text("text"))).Append("\n\n"); break;
                case "item": sb.Append("- ").Append(Escape(b.Text("text"))).Append('\n'); if (i + 1 == blocks.Count || blocks[i + 1].Kind is not ("item" or "stat")) sb.Append('\n'); break;
                case "stat": sb.Append("- ").Append(Escape(b.Text("text"))).Append(": ").Append(b.Int("n")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "").Append('\n'); if (i + 1 == blocks.Count || blocks[i + 1].Kind is not ("item" or "stat")) sb.Append('\n'); break;
                case "header":
                    sb.Append("| ").Append(string.Join(" | ", Cells(b))).Append(" |\n| --- | --- | --- | --- |\n");
                    if (i + 1 == blocks.Count || blocks[i + 1].Kind != "row") sb.Append('\n');
                    break;
                case "row":
                    sb.Append("| ").Append(string.Join(" | ", Cells(b))).Append(" |\n");
                    if (i + 1 == blocks.Count || blocks[i + 1].Kind != "row") sb.Append('\n');
                    break;
                default: sb.Append(Escape(b.Text("text"))).Append("\n\n"); break;
            }
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    public static string Digest(string markdown) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));

    public static JsonNode Json(long revision, IReadOnlyList<Block> blocks, string digest) => new JsonObject
    {
        ["revision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture), ["digest"] = digest,
        ["blocks"] = new JsonArray([.. blocks.Select(b =>
        {
            var o = new JsonObject { ["id"] = b.Id, ["kind"] = b.Kind, ["order"] = b.Order };
            foreach (var p in new[] { "text", "a", "b", "c", "d" }) if (b.Text(p) is { } t) o[p] = t;
            if (b.Int("level") is { } l) o["level"] = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (b.Int("n") is { } n) o["n"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (JsonNode)o;
        })]),
    };
}
