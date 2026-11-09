using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sattle.Model;
using Sattle.Documents;
namespace Sattle.Specification;

// Finite, deliberately slow mathematical oracle. Never referenced by src/.
internal static class Oracle
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
    public static string Encode<T>(T x) => JsonSerializer.Serialize(x, Json);
    public static readonly TenantId Tenant = new("demo");
    public static readonly ProjectionId Projection = new("network");
    public static readonly OntologyRef Ontology = new("infra", "1", new("sha256:example"));
    public static GraphVersion V(int n) => new(Tenant, new("g"), new(n.ToString()));
    public static EntityState Node(string id, string status = "healthy") => new(new(id), new("Service"), [new(new("health"), new TextValue(status))]);
    public static RelationshipState Edge(string id, string from, string to, string type = "Calls") => new(new(id), new(from), new(to), new(type), []);
    public static bool Same<T>(T a, T b) => Encode(a) == Encode(b);
    public static GraphExtent Graph(params EntityState[] entities) => new([..entities.OrderBy(n => n.Id.Value, StringComparer.Ordinal)], []);
    public static GraphExtent Canon(GraphExtent g) => new([..g.Entities.Select(e=>e with {Properties=[..e.Properties.OrderBy(p=>p.Property.Value,StringComparer.Ordinal)]}).OrderBy(e=>e.Id.Value,StringComparer.Ordinal)], [..g.Relationships.Select(e=>e with {Properties=[..e.Properties.OrderBy(p=>p.Property.Value,StringComparer.Ordinal)]}).OrderBy(e=>e.Id.Value,StringComparer.Ordinal)]);
    public static void Validate(GraphExtent g)
    {
        if (g.Entities.Select(e=>e.Id).Distinct().Count()!=g.Entities.Length || g.Relationships.Select(e=>e.Id).Distinct().Count()!=g.Relationships.Length) throw new InvalidOperationException("duplicate ID");
        if (g.Relationships.Select(e=>(e.Source,e.Target,e.Type)).Distinct().Count()!=g.Relationships.Length) throw new InvalidOperationException("edge uniqueness");
        foreach(var ps in g.Entities.Select(e=>e.Properties).Concat(g.Relationships.Select(e=>e.Properties)))
            if(ps.Select(p=>p.Property).Distinct().Count()!=ps.Length) throw new InvalidOperationException("duplicate property");
        // Missing endpoints are deliberately valid (ADR-084).
    }
    public static GraphPatch Diff(GraphExtent before, GraphExtent after, int from, int to)
    {
        before=Canon(before); after=Canon(after); Validate(before); Validate(after); var edits=new List<GraphEdit>();
        var bn=before.Entities.ToDictionary(n=>n.Id);var an=after.Entities.ToDictionary(n=>n.Id);
        foreach(var id in bn.Keys.Union(an.Keys).OrderBy(x=>x.Value,StringComparer.Ordinal))
        {bn.TryGetValue(id,out var b);an.TryGetValue(id,out var a);if(!Same(b,a))edits.Add(new EntityEdit(id,b,a));}
        var be=before.Relationships.ToDictionary(n=>n.Id);var ae=after.Relationships.ToDictionary(n=>n.Id);
        foreach(var id in be.Keys.Union(ae.Keys).OrderBy(x=>x.Value,StringComparer.Ordinal))
        {be.TryGetValue(id,out var b);ae.TryGetValue(id,out var a);if(!Same(b,a))edits.Add(new RelationshipEdit(id,b,a));}
        return new("1.0",V(from),V(to),Projection,DiffProfile.ExtensionalFinite,[..edits],new([],"1"));
    }
    public static GraphExtent Apply(GraphExtent before, GraphVersion current, GraphPatch patch)
    {
        if(current != patch.From || patch.From.Tenant!=patch.To.Tenant || patch.From.Graph!=patch.To.Graph) throw new InvalidOperationException("revision/scope mismatch");
        before=Canon(before); var ns=before.Entities.ToDictionary(n=>n.Id);var es=before.Relationships.ToDictionary(e=>e.Id);
        foreach(var edit in patch.Edits)
        {
            switch(edit)
            {
                case EntityEdit n:
                    ns.TryGetValue(n.Id,out var nb);if(!Same(nb,n.Before)||n.After is not null&&n.After.Id!=n.Id)throw new InvalidOperationException("entity precondition");
                    if(n.After is null)ns.Remove(n.Id);else ns[n.Id]=n.After;break;
                case RelationshipEdit e:
                    es.TryGetValue(e.Id,out var eb);if(!Same(eb,e.Before)||e.After is not null&&e.After.Id!=e.Id)throw new InvalidOperationException("edge precondition");
                    if(e.After is null)es.Remove(e.Id);else es[e.Id]=e.After;break;
                default: throw new NotSupportedException("Finite oracle only");
            }
        }
        var after=Canon(new([..ns.Values],[..es.Values]));Validate(after);return after;
    }
    public static GraphPatch Invert(GraphPatch p) => p with { From=p.To,To=p.From,Edits=[..p.Edits.Reverse().Select(e=>e switch {
        EntityEdit n => (GraphEdit)new EntityEdit(n.Id,n.After,n.Before),
        RelationshipEdit r => new RelationshipEdit(r.Id,r.After,r.Before), _=>throw new NotSupportedException() })] };
    public static GraphPatch Compose(GraphPatch a, GraphPatch b)
    {
        if(a.To!=b.From || a.Projection!=b.Projection || a.Profile!=b.Profile)throw new InvalidOperationException("nonadjacent");
        return a with {To=b.To,Edits=[..a.Edits,..b.Edits]};
    }
    public static Dictionary<string,BigInteger> Facts(GraphExtent g) => Canon(g).Entities.Select(x=>(GraphFact)new EntityFact(x))
        .Concat(Canon(g).Relationships.Select(x=>(GraphFact)new RelationshipFact(x))).ToDictionary(Encode,_=>BigInteger.One);
    public static Dictionary<string,BigInteger> Add(Dictionary<string,BigInteger> a, Dictionary<string,BigInteger> b)
    {var c=new Dictionary<string,BigInteger>(a);foreach(var (k,v)in b){c[k]=c.GetValueOrDefault(k)+v;if(c[k]==0)c.Remove(k);}return c;}
    public static Dictionary<string,BigInteger> Neg(Dictionary<string,BigInteger> a)=>a.ToDictionary(x=>x.Key,x=>-x.Value);
    public static bool SameDelta(Dictionary<string,BigInteger>a,Dictionary<string,BigInteger>b)=>a.Count==b.Count&&a.All(x=>b.GetValueOrDefault(x.Key)==x.Value);
    public static Dictionary<string,BigInteger> Delta(GraphExtent a,GraphExtent b)=>Add(Neg(Facts(a)),Facts(b));
    public static GraphExtent Materialize(Dictionary<string,BigInteger> facts)
    {
        if(facts.Values.Any(x=>x!=1))throw new InvalidOperationException("not set-valued");
        var decoded=facts.Keys.Select(k=>JsonSerializer.Deserialize<GraphFact>(k,Json)!).ToArray();
        var graph=Canon(new([..decoded.OfType<EntityFact>().Select(f=>f.State)],[..decoded.OfType<RelationshipFact>().Select(f=>f.State)]));Validate(graph);return graph;
    }
    public static HashSet<(int,int)> Closure(HashSet<(int,int)> edges,int nodes,int budget)
    {
        if(edges.Any(e=>e.Item1<0||e.Item2<0||e.Item1>=nodes||e.Item2>=nodes))throw new InvalidOperationException("outside domain");
        var result=new HashSet<(int,int)>(edges);var work=0;
        while(true)
        {
            var added=new HashSet<(int,int)>();
            foreach(var(a,b)in result)foreach(var(c,d)in result)
            {if(++work>budget)throw new InvalidOperationException("incomplete budget");if(b==c&&!result.Contains((a,d)))added.Add((a,d));}
            if(added.Count==0)return result;result.UnionWith(added);
        }
    }
    public static string Escape(string text)=>text.Replace("\\","\\\\").Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;")
        .Replace("|","\\|").Replace("*","\\*").Replace("_","\\_").Replace("[","\\[").Replace("]","\\]").Replace("`","\\`")
        .Replace("#","\\#").Replace("!","\\!").Replace("\r\n"," ").Replace("\n"," ").Replace("\r"," ");
    public static string Markdown(DocumentSnapshot doc)
    {
        var b=new StringBuilder();
        foreach(var block in doc.Blocks)
        {
            switch(block)
            {
                case Heading h:b.Append(new string('#',h.Level)).Append(' ').Append(Escape(h.Text)).Append("\n\n");break;
                case Paragraph p:b.Append(Escape(p.Text)).Append("\n\n");break;
                case DocumentTable t:
                    b.Append("| ").AppendJoin(" | ",t.Columns.Select(Escape)).Append(" |\n| ").AppendJoin(" | ",t.Columns.Select(_=>"---")).Append(" |\n");
                    foreach(var r in t.Rows)b.Append("| ").AppendJoin(" | ",r.Cells.Select(Escape)).Append(" |\n");b.Append('\n');break;
                default:throw new NotSupportedException("Oracle renderer subset: heading, paragraph, table");
            }
        }
        return b.ToString();
    }
    public static DocumentSnapshot ApplyDocument(DocumentSnapshot d,DocumentPatch p)
    {
        if(d.Version!=p.From||!d.Blocks.Select(b=>b.Id).SequenceEqual(p.BeforeOrder)||!Same(d.Sources,p.BeforeSources)||d.ProjectionVersion!=p.BeforeProjectionVersion||d.Completeness!=p.BeforeCompleteness)throw new InvalidOperationException("document precondition");
        var blocks=d.Blocks.ToDictionary(b=>b.Id);
        foreach(var edit in p.Edits){blocks.TryGetValue(edit.Id,out var old);if(!Same(old,edit.Before))throw new InvalidOperationException("block precondition");if(edit.After is null)blocks.Remove(edit.Id);else if(edit.After.Id!=edit.Id)throw new InvalidOperationException("block ID");else blocks[edit.Id]=edit.After;}
        if(p.AfterOrder.Distinct().Count()!=blocks.Count||p.AfterOrder.Length!=blocks.Count||p.AfterOrder.Any(id=>!blocks.ContainsKey(id)))throw new InvalidOperationException("document order");
        return d with {Version=p.To,Sources=p.AfterSources,ProjectionVersion=p.AfterProjectionVersion,Blocks=[..p.AfterOrder.Select(id=>blocks[id])],Completeness=p.AfterCompleteness};
    }
}
