using System.Text;
using Sattle.Core;

namespace Sattle.Tests;

/// <summary>Fixed UTF-8 encodings for the graphs of Definition 3.5.</summary>
public class GraphEncodingTests
{
    [Fact]
    public void Empty_graph_is_an_empty_array()
    {
        Assert.Equal("[]"u8.ToArray(), Encoding.UTF8.GetBytes(new Graph().Encode()));
    }

    [Fact]
    public void Entity_ontology_and_relationship_have_canonical_pairs()
    {
        const string releaseDigest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var service = new Entity("svc", "Service", Props.Of(
            ("label", Value.Text("café\"\\\n\u0001")),
            ("args", Value.List([
                Value.Integer(9007199254740993L), Value.Decimal(1.500m), Value.Instant(-1),
                Value.Entity("db"), Value.Null, Value.Boolean(false)
            ]))));
        var relationship = new Rel("svc", "svc", "db", "Calls", Props.Empty);
        var graph = new Graph();
        Assert.Null(graph.Apply([
            Edit.Create(relationship),
            Edit.Create(new OntologyState("demo", "2", releaseDigest)),
            Edit.Create(service)
        ]));

        const string expected =
            """[["e:svc",{"id":"svc","props":{"args":{"kind":"list","value":[""" +
            """{"kind":"integer","value":"9007199254740993"},{"kind":"decimal","value":"1.5"},""" +
            """{"kind":"instant","value":"1969-12-31T23:59:59.999999999Z"},{"kind":"entity","value":"db"},""" +
            """{"kind":"null"},{"kind":"boolean","value":false}]},"label":{"kind":"text","value":"café\"\\\n\u0001"}},"type":"Service"}],""" +
            """["o",{"digest":"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","id":"demo","version":"2"}],""" +
            """["r:svc",{"id":"svc","props":{},"source":"svc","target":"db","type":"Calls"}]]""";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(graph.Encode()));
        // Independently computed from the three literal pairs using Python hashlib and little-endian integers.
        Assert.Equal("sha256:9b90dcc0f91abfbbfbae7f478b079cc63732b4d663f95992099cd3441d48ba44", graph.Digest());
    }

    [Fact]
    public void Entity_ids_sort_by_utf16_code_units()
    {
        var graph = new Graph();
        Assert.Null(graph.Apply([Edit.Create(new Entity("Ａ", "T", Props.Empty))]));
        Assert.Null(graph.Apply([Edit.Create(new Entity("😀", "T", Props.Empty))]));

        // U+1F600 begins with D83D in UTF-16, which sorts before FF21.
        const string expected =
            """[["e:😀",{"id":"😀","props":{},"type":"T"}],["e:Ａ",{"id":"Ａ","props":{},"type":"T"}]]""";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(graph.Encode()));
    }
}
