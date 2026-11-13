using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Operators the cases of research note 13 need, run on a small world of atoms and bonds: a bond has a direction in the graph, and a chemist reads it in both.</summary>
public class KqlOperatorTests
{
    const string Chem = """
        module t.chem @ 1.0.0;
        entity Atom { tag: Text; symbol: Text; arrived: Integer; }
        relationship Bond (Atom) -> (Atom) kind composition;
        identity atom for Atom version "1" { tag: Text by exact; }
        rows AtomRow { tag: Text; symbol: Text; arrived: Integer; }
        rows BondRow { a: Text; b: Text; }
        """;

    const string Ingest = """
        graph ingest : t.chem;
        input atoms : AtomRow;
        input bonds : BondRow;
        nodes Atom on atom with (rule = "observed", description = "atoms and bonds as recorded") = atoms | project atom = identity("atom", tag), tag, symbol, arrived;
        edges Bond from a to b with (rule = "observed") = bonds | project a = identity("atom", a), b = identity("atom", b);
        """;

    const string Mol = """
        module t.mol @ 1.0.0;
        import t.chem @ 1.0.0 sha256:… as c;
        entity Seen { tag: Text; }
        entity Element { symbol: Text; newest: Text; arrived: Integer; }
        relationship Near (Seen) -> (Seen) kind communication;
        identity element for Element version "1" { symbol: Text by exact; }
        """;

    const string Space = """
        module t.space @ 1.0.0;
        import t.chem @ 1.0.0 sha256:… as c;
        import t.mol @ 1.0.0 sha256:… as m;
        space s {
          source atoms adapter "sattle.authoring" rows c.AtomRow;
          source bonds adapter "sattle.authoring" rows c.BondRow;
          master graph master = c.ingest(atoms: atoms, bonds: bonds);
          graph near = m.near(master: master);
        }
        """;

    const string Head = "graph near : t.mol;\ninput master : t.chem;\n"
        + "nodes Seen on atom = graph(\"master\") | graph-match (a) where labels(a) has \"Atom\" project atom = a.id, tag = a.tag;\n";

    /// <summary>Water and a lone carbon: O1 bonds to H1, and H2 bonds to O1, so the two bonds point different ways.</summary>
    static readonly (string Tag, string Symbol, long Arrived)[] Atoms = [("O1", "O", 1), ("H1", "H", 2), ("H2", "H", 3), ("C1", "C", 4)];
    static readonly (string A, string B)[] Bonds = [("O1", "H1"), ("H2", "O1")];

    /// <summary>The `near` graph after the atoms and bonds are ingested, with the view written in KQL.</summary>
    static Graph Near(string view)
    {
        var cat = new Catalog(new KqlSource());
        var res = cat.Load([new SourceFile("t.chem.sattle", Chem), new SourceFile("t.mol.sattle", Mol), new SourceFile("t.space.sattle", Space),
            new SourceFile("t.chem.ingest.kql", Ingest), new SourceFile("t.mol.near.kql", view)], updatePins: true);
        Assert.True(!res.Diagnostics.Any(d => d.IsError), string.Join("\n", res.Diagnostics.Where(d => d.IsError)));
        using var e = new Engine(cat, cat.Spaces.Keys.Single());
        var aw = new RowWriter(e.RowLayout("atoms"), e.Interner);
        foreach (var (tag, symbol, arrived) in Atoms) { aw.Begin(1); aw.Text("tag", tag); aw.Text("symbol", symbol); aw.Int(aw.Col("arrived"), arrived); }
        e.Ingest("atoms", aw.Build()!, 1);
        var bw = new RowWriter(e.RowLayout("bonds"), e.Interner);
        foreach (var (a, b) in Bonds) { bw.Begin(1); bw.Text("a", a); bw.Text("b", b); }
        e.Ingest("bonds", bw.Build()!, 2);
        return e.Graph("near").State;
    }

    static string Text(Entity e, string prop) => ((TextValue)e.Props.Get("d:t.mol/prop:" + prop)!).S;

    /// <summary>Who each atom is bonded to, read without the bond's direction: written undirected, as the union of both directions, or as a path of one edge in either direction, the answer is each bond twice.</summary>
    [Theory]
    [InlineData("a path without a direction", "graph(\"master\") | graph-match (x)-[p*1..1]-(y) where labels(x) has \"Atom\" and all(p, labels(p) has \"Bond\") project atom = x.id, other = y.id")]
    public void Each_bond_is_read_in_both_directions(string what, string pairs)
    {
        var g = Near(Head + $"edges Near from atom to other = {pairs};\n");
        var tag = g.Entities.ToDictionary(x => x.Id, x => Text(x, "tag"));
        var got = g.Rels.Select(r => $"{tag[r.Source]}-{tag[r.Target]}").Order(StringComparer.Ordinal);
        Assert.True(got.SequenceEqual(["H1-O1", "H2-O1", "O1-H1", "O1-H2"]), $"{what}: " + string.Join(", ", got));
    }

    /// <summary>The atom of each element that arrived last: arg_max with its columns named, with * for every other column, and with the greatest order named.</summary>
    [Theory]
    [InlineData("summarize arg_max(arrived, newest) by symbol")]
    [InlineData("summarize arg_max(arrived, *) by symbol")]
    [InlineData("summarize greatest = arg_max(arrived, newest) by symbol | project symbol, newest, arrived = greatest")]
    public void Arg_max_gives_the_atom_of_each_element_that_arrived_last(string summary)
    {
        var g = Near(Head + "nodes Element on el = graph(\"master\") | graph-match (a) where labels(a) has \"Atom\" project symbol = a.symbol, newest = a.tag, arrived = a.arrived"
            + $" | {summary} | project el = identity(\"element\", symbol), symbol, newest, arrived;\n");
        var got = g.Entities.Where(x => Ids.Name(x.Type) == "Element").Select(x => $"{Text(x, "symbol")}:{Text(x, "newest")}").Order(StringComparer.Ordinal);
        Assert.Equal(["C:C1", "H:H2", "O:O1"], got);
    }
}
