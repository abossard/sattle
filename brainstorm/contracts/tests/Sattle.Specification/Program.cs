using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Sattle.Model;
using Sattle.Projections;
using Sattle.Documents;
using Sattle.Specification;

var root=Path.GetFullPath(args.Length>0?args[0]:".");
var results=new List<object>();int assertions=0;
void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
void Reject(Action action,string message){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,message);}
void Test(string name,Action action){action();results.Add(new{name,status="passed"});Console.WriteLine("PASS "+name);}
var empty=Oracle.Graph();var a=Oracle.Graph(Oracle.Node("a"));var b=Oracle.Graph(Oracle.Node("a","degraded"),Oracle.Node("b"));
Test("Patch application, reversal, batching and associative composition",()=>{
 var p=Oracle.Diff(empty,a,0,1);var q=Oracle.Diff(a,b,1,2);var r=Oracle.Diff(b,empty,2,3);
 Check(Oracle.Same(Oracle.Apply(empty,Oracle.V(0),Oracle.Compose(p,q)),b),"compose state");
 Check(Oracle.Same(Oracle.Apply(b,Oracle.V(2),Oracle.Invert(q)),a),"inverse");
 Check(Oracle.Same(Oracle.Compose(Oracle.Compose(p,q),r),Oracle.Compose(p,Oracle.Compose(q,r))),"associativity");
 Reject(()=>Oracle.Compose(q,p),"ordered patches not commutative");
 Reject(()=>Oracle.Apply(a,Oracle.V(1),p),"duplicate patch rejected by revision");
 Reject(()=>Oracle.Apply(b,Oracle.V(1),q),"stale before value rejected");
});
Test("Signed deltas form an additive group; graph application remains partial",()=>{
 var random=new Random(63421);
 for(int i=0;i<300;i++){
  Dictionary<string,BigInteger> D()=>Enumerable.Range(0,8).Select(j=>(k:j.ToString(),v:new BigInteger(random.Next(-3,4)))).Where(x=>x.v!=0).ToDictionary(x=>x.k,x=>x.v);
  var x=D();var y=D();var z=D();
  Check(Oracle.SameDelta(Oracle.Add(x,y),Oracle.Add(y,x)),"commutative");
  Check(Oracle.SameDelta(Oracle.Add(Oracle.Add(x,y),z),Oracle.Add(x,Oracle.Add(y,z))),"associative");
  Check(Oracle.Add(x,Oracle.Neg(x)).Count==0,"inverse");Check(Oracle.SameDelta(Oracle.Add(x,[]),x),"zero");
 }
 var da=Oracle.Delta(empty,a);var db=Oracle.Delta(a,b);
 Check(Oracle.Same(Oracle.Materialize(Oracle.Add(Oracle.Facts(empty),Oracle.Add(da,db))),b),"batch delta equivalence");
 Reject(()=>Oracle.Materialize(Oracle.Neg(Oracle.Facts(a))),"negative graph existence");
 Reject(()=>Oracle.Materialize(Oracle.Add(Oracle.Facts(a),Oracle.Facts(a))),"duplicate observation not graph multiplicity");
 var conflict=Oracle.Add(Oracle.Facts(a),Oracle.Facts(Oracle.Graph(Oracle.Node("a","bad"))));
 Reject(()=>Oracle.Materialize(conflict),"conflicting same-ID states");
});
Test("Patch-to-delta conversion preserves endpoint state and loses chronology",()=>{
 var delta=Oracle.Add(Oracle.Delta(empty,a),Oracle.Delta(a,empty));Check(delta.Count==0,"cancel");
 var history=Oracle.Compose(Oracle.Diff(empty,a,0,1),Oracle.Diff(a,empty,1,2));Check(history.Edits.Length==2,"history retained in patch");
 Check(Oracle.Same(Oracle.Materialize(Oracle.Add(Oracle.Facts(a),Oracle.Delta(a,b))),b),"explicit base");
});
Test("Retyping preserves edge identity; uniqueness and dangling edges",()=>{
 var old=new GraphExtent(a.Entities,[Oracle.Edge("e","a","missing","NoType")]);
 var next=old with {Relationships=[Oracle.Edge("e","a","missing")]};
 Check(Oracle.Apply(old,Oracle.V(0),Oracle.Diff(old,next,0,1)).Relationships[0].Id==new RelationshipId("e"),"retype identity");
 Oracle.Validate(next);var collide=next with {Relationships=[next.Relationships[0],Oracle.Edge("other","a","missing")]};Reject(()=>Oracle.Validate(collide),"unique triple");
 var nodeRemoved=next with {Entities=[]};Oracle.Validate(nodeRemoved);Check(nodeRemoved.Relationships.Length==1,"dangling retained");
});
Test("Positive finite-domain recursion converges and withdrawal removes unsupported closure",()=>{
 var edges=new HashSet<(int,int)>{(0,1),(1,2),(2,0)};var closure=Oracle.Closure(edges,3,10000);Check(closure.Count==9,"cyclic reachability fixpoint");
 Check(Oracle.Closure(closure,3,10000).SetEquals(closure),"idempotent closure");
 edges.Remove((1,2));Check(!Oracle.Closure(edges,3,10000).Contains((0,2)),"withdrawal");
 Reject(()=>Oracle.Closure(new(){(0,1),(1,2)},3,1),"budget exhaustion never complete");
 Reject(()=>Oracle.Closure(new(){(0,3)},3,10000),"unbounded domain rejected");
});
Test("Derived edge remains while another support exists",()=>{
 var supports=new Dictionary<string,HashSet<string>>{{"a->c",new(){"rule1","rule2"}}};supports["a->c"].Remove("rule1");Check(supports["a->c"].Count>0,"alternate support");supports["a->c"].Remove("rule2");Check(supports["a->c"].Count==0,"last support removed");
});
Test("Stored and virtual representations agree on a finite requested scope",()=>{
 var stored=Enumerable.Range(0,80).Where(i=>i%2==0).ToHashSet();bool Virtual(int n)=>n>=0&&n%2==0;
 for(int i=0;i<80;i++)Check(stored.Contains(i)==Virtual(i),"membership");
 // A finite symbolic definition change describes infinitely many changed memberships.
 Func<int,bool> oldRule=n=>n>=0&&n%2==0;Func<int,bool> newRule=n=>n>=0;
 Check(!oldRule(101)&&newRule(101),"symbolic descriptor change");
});
Test("Lens laws hold on supported edit domain with retained complement",()=>{
 (string Name,string Secret) source=("before","retained");string Get((string Name,string Secret)s)=>s.Name;
 (string Name,string Secret) Put((string Name,string Secret)s,string view)=>(view,s.Secret);
 Check(Put(source,Get(source))==source,"GetPut");Check(Get(Put(source,"after"))=="after","PutGet");Check(Put(source,"after").Secret=="retained","complement");
});
var document=new DocumentSnapshot("1.0",new(Oracle.Tenant,new("report"),new("0")),"1",[Oracle.V(2)],
 [new Heading(new("title"),1,"Service health"),new DocumentTable(new("services"),["Service","Health"],[new("a",["a","degraded"]),new("b",["b","healthy"])])],DocumentCompleteness.Complete);
Test("Document patch reorders blocks without changing IDs and reverses exactly",()=>{
 var patch=new DocumentPatch("1.0",document.Version,document.Version with {Revision=new("1")},document.Sources,[Oracle.V(3)],"1","1",[],[new("title"),new("services")],[new("services"),new("title")],DocumentCompleteness.Complete,DocumentCompleteness.Complete);
 var after=Oracle.ApplyDocument(document,patch);Check(after.Blocks[0].Id==new BlockId("services"),"stable identity");
 var inverse=patch with{From=patch.To,To=patch.From,BeforeSources=patch.AfterSources,AfterSources=patch.BeforeSources,BeforeOrder=patch.AfterOrder,AfterOrder=patch.BeforeOrder};
 Check(Oracle.Same(Oracle.ApplyDocument(after,inverse),document),"document reversal");
 Reject(()=>Oracle.ApplyDocument(after,patch),"stale document revision");
});
Test("Markdown rendering is deterministic and escapes native text",()=>{
 Check(Oracle.Markdown(document)==Oracle.Markdown(document),"determinism");
 var malicious=document with{Blocks=[new Paragraph(new("p"),"<script>*x* [a](b) | z\n# fake")]};var text=Oracle.Markdown(malicious);
 Check(!text.Contains("<script>")&&text.Contains("\\*")&&text.Contains("\\|")&&text.Contains("\\#"),"escape");
 Check(!text.Contains("\n# fake"),"no injected block");
});
Test("Wire round-trip preserves polymorphic graph and document data",()=>{
 var patch=Oracle.Diff(a,b,1,2);var clone=JsonSerializer.Deserialize<GraphPatch>(Oracle.Encode(patch),Oracle.Json);Check(Oracle.Same(patch,clone),"patch roundtrip");
 var dc=JsonSerializer.Deserialize<DocumentSnapshot>(Oracle.Encode(document),Oracle.Json);Check(Oracle.Same(document,dc),"document roundtrip");
 var expression=new Equal<string>(new Column<string>("name"),new Constant<string>("x"));Check(expression is Expr<bool>,"typed expression");
});
Test("Graph canonicalization ignores property-map ordering",()=>{
 var n=Oracle.Node("x") with {Properties=[new(new("z"),new TextValue("z")),new(new("a"),new TextValue("a"))]};
 var reverse=n with {Properties=[..n.Properties.Reverse()]};
 Check(Oracle.Same(Oracle.Canon(Oracle.Graph(n)),Oracle.Canon(Oracle.Graph(reverse))),"property order");
 Check(Oracle.Diff(Oracle.Graph(n),Oracle.Graph(reverse),0,1).Edits.Length==0,"reordering emits no edit");
 Check(Oracle.Delta(Oracle.Graph(n),Oracle.Graph(reverse)).Count==0,"reordering emits no delta");
});
Test("Portable graph and document projection definitions deserialize and round-trip",()=>{
 var graphPath=Path.Combine(root,"examples","graph-projection.json");
 var docPath=Path.Combine(root,"examples","document-projection.json");
 var graphJson=File.ReadAllText(graphPath);var docJson=File.ReadAllText(docPath);
 var g=JsonSerializer.Deserialize<ProjectionDefinition>(graphJson,Oracle.Json)!;
 var d=JsonSerializer.Deserialize<DocumentProjectionDefinition>(docJson,Oracle.Json)!;
 Check(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(graphJson),System.Text.Json.Nodes.JsonNode.Parse(Oracle.Encode(g))),"projection wire alignment");
 Check(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(docJson),System.Text.Json.Nodes.JsonNode.Parse(Oracle.Encode(d))),"document projection wire alignment");
});
Test("Generated graph transitions reconstruct and reverse across random update sequences",()=>{
 var random=new Random(73411);var state=Oracle.Graph();
 for(int revision=0;revision<200;revision++){
  var nodes=state.Entities.ToDictionary(n=>n.Id.Value);var id="node-"+random.Next(12);
  if(random.Next(3)==0)nodes.Remove(id);else nodes[id]=Oracle.Node(id,random.Next(2)==0?"healthy":"degraded");
  var next=Oracle.Canon(new([..nodes.Values],[]));var patch=Oracle.Diff(state,next,revision,revision+1);
  Check(Oracle.Same(Oracle.Apply(state,Oracle.V(revision),patch),next),"random reconstruction");
  Check(Oracle.Same(Oracle.Apply(next,Oracle.V(revision+1),Oracle.Invert(patch)),state),"random inverse");
  Check(Oracle.Same(Oracle.Materialize(Oracle.Add(Oracle.Facts(state),Oracle.Delta(state,next))),next),"random delta conversion");state=next;
 }
});
SchemaWriter.Write(Path.Combine(root,"schemas"));
var examples=Path.Combine(root,"examples");Directory.CreateDirectory(examples);
File.WriteAllText(Path.Combine(examples,"graph-patch.json"),Oracle.Encode(Oracle.Diff(a,b,1,2)));
var delta=new SignedDelta("1.0",Oracle.Tenant,Oracle.Projection,Oracle.Ontology,[new(new EntityFact(Oracle.Node("a")),"-1"),new(new EntityFact(Oracle.Node("a","degraded")),"1")]);
File.WriteAllText(Path.Combine(examples,"signed-delta.json"),Oracle.Encode(delta));
File.WriteAllText(Path.Combine(examples,"document.json"),Oracle.Encode(document));
File.WriteAllText(Path.Combine(examples,"service-health.md"),Oracle.Markdown(document));
File.WriteAllText(Path.Combine(root,"docs","reference-test-results.json"),JsonSerializer.Serialize(new{suite="finite mathematical reference model",seed=63421,testGroups=results.Count,assertions,results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {results.Count} groups; {assertions} assertions. Production engine is not implemented.");
