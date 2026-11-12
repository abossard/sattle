using System.Text;
using System.Text.RegularExpressions;
using Kusto.Language;
using Kusto.Language.Symbols;

var dir = args[0];
const string Node = "(id:string, key:string, kind:string, name:string, cluster:string, namespace:string, phase:string, ready:bool, externalName:string, address:string, replicas:long, pool:string, resourceId:string, type:string, parentId:string, linkedId:string, clientId:string, principalId:string, scope:string, capId:string, owner:string, status:string, variable:string, host:string, hostName:string, meter:string, ruleId:string, ruleType:string, ruleMeter:string, ruleAbove:real, ruleEnter:long, ruleBelow:real, ruleExit:long, ruleSeverity:long, ruleInvert:bool, own:long, severity:long, state:string, sourceKey:string, targetKey:string, how:string, detail:string, readyPods:long, resourceType:string)";
const string Edge = "(id:string)";
GraphModelSymbol G(string n) => new(n, "GraphNodes", "GraphNodes", Array.Empty<string>());
var lib = File.ReadAllText(Path.Combine(dir, "library.kql"));
var fns = new List<Symbol>();
foreach (Match m in Regex.Matches(lib, @"(?ms)^let (\w+) = \((.*?)\) \{\s*(.*?)\s*\};\s*$"))
    fns.Add(new FunctionSymbol(m.Groups[1].Value, "(" + m.Groups[2].Value + ")", "{ " + m.Groups[3].Value + " }"));
var db = new DatabaseSymbol("db", new Symbol[] {
    G("master"), G("serviceTree"), G("structure"), G("deploymentHealth"), Table("GraphNodes", Node), Table("GraphEdges", Edge),
    new TableSymbol("Telemetry", "(graph:string, id:string, property:string, windowStart:datetime, value:real, noData:bool)") }.Concat(fns));
var globals = GlobalState.Default.WithDatabase(db);
Console.WriteLine($"library functions: {string.Join(", ", fns.Select(f => f.Name))}");
// the library itself, checked as functions over the Telemetry table
foreach (var f in fns.Cast<FunctionSymbol>()) { var c = KustoCode.ParseAndAnalyze("let x = 1; print x", globals); }
var libCode = KustoCode.ParseAndAnalyze(lib + "\nprint 1", GlobalState.Default.WithDatabase(new DatabaseSymbol("db", new TableSymbol("Telemetry", "(graph:string, id:string, property:string, windowStart:datetime, value:real, noData:bool)"))));
var libDiags = libCode.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
Console.WriteLine($"library.kql: {libDiags.Count} error(s)"); foreach (var d in libDiags) Console.WriteLine($"  {d.Code}: {d.Message} at '{Snip(lib, d.Start)}'");

var step = new Regex(@"^(?<kind>nodes|edges)\s+(?:(?<label>\w+)\s+)?(?:on\s+(?<id>\w+)|from\s+(?<src>\w+)\s+to\s+(?<dst>\w+))(?:\s+labels\s+(?<labels>\w+))?(?:\s+with\s*\((?<with>.*?)\))?\s*=\s*(?<expr>.*)$", RegexOptions.Singleline);
int total = 0, totalErr = 0;
foreach (var file in Directory.GetFiles(dir, "*.kql").Where(f => !f.EndsWith("library.kql")).OrderBy(f => f))
{
    var text = File.ReadAllText(file);
    var stmts = Split(text);
    var prelude = new StringBuilder(); int nSteps = 0, nErr = 0; string? header = null;
    foreach (var st in stmts)
    {
        var s = st.Trim();
        if (s.StartsWith("graph ")) { header = s; continue; }
        if (s.StartsWith("declare ") || s.StartsWith("let ")) { prelude.Append(s).Append(";\n"); continue; }
        var m = step.Match(s);
        if (!m.Success) { Console.WriteLine($"{Path.GetFileName(file)}: unrecognised statement: {s[..Math.Min(80, s.Length)]}"); nErr++; continue; }
        nSteps++;
        var q = prelude + m.Groups["expr"].Value;
        var code = KustoCode.ParseAndAnalyze(q, globals);
        var errs = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
        var cols = (code.ResultType as TableSymbol)?.Columns.Select(c => c.Name).ToHashSet() ?? new HashSet<string>();
        foreach (var need in new[] { m.Groups["id"].Value, m.Groups["src"].Value, m.Groups["dst"].Value, m.Groups["labels"].Value }.Where(x => x.Length > 0))
            if (!cols.Contains(need)) errs.Add(new Diagnostic("STEP", $"result has no column '{need}' (columns: {string.Join(",", cols)})"));
        foreach (var d in errs) { nErr++; Console.WriteLine($"{Path.GetFileName(file)} [{m.Groups["kind"].Value} {m.Groups["label"].Value}]: {d.Code}: {d.Message} at '{Snip(q, d.Start)}'"); }
    }
    Console.WriteLine($"{Path.GetFileName(file)}: header '{header}', {nSteps} steps, {nErr} error(s)");
    total += nSteps; totalErr += nErr;
}
Console.WriteLine($"total: {total} steps, {totalErr} error(s)");

static TableSymbol Table(string name, string schema) => new TableSymbol(name, schema.Trim('(', ')').Split(',').Select(c => c.Trim().Split(':')).Select(p => new ColumnSymbol(p[0], p[1] switch { "string" => ScalarTypes.String, "bool" => ScalarTypes.Bool, "long" => ScalarTypes.Long, "real" => ScalarTypes.Real, "datetime" => ScalarTypes.DateTime, _ => ScalarTypes.Dynamic })));
static string Snip(string s, int at) { var a = Math.Max(0, at - 20); return s.Substring(a, Math.Min(60, s.Length - a)).Replace("\n", " "); }
static List<string> Split(string text)
{
    var res = new List<string>(); var sb = new StringBuilder(); int depth = 0; bool inStr = false; char q = '\0';
    foreach (var line in text.Split('\n'))
    {
        var l = line; var ci = IndexOfComment(l); if (ci >= 0) l = l[..ci];
        foreach (var ch in l + "\n")
        {
            if (inStr) { sb.Append(ch); if (ch == q) inStr = false; continue; }
            if (ch is '"' or '\'') { inStr = true; q = ch; sb.Append(ch); continue; }
            if (ch is '(' or '[' or '{') depth++;
            if (ch is ')' or ']' or '}') depth--;
            if (ch == ';' && depth == 0) { if (sb.ToString().Trim().Length > 0) res.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
    }
    if (sb.ToString().Trim().Length > 0) res.Add(sb.ToString());
    return res;
}
static int IndexOfComment(string l) { bool s = false; char q = '\0'; for (int i = 0; i + 1 < l.Length; i++) { var c = l[i]; if (s) { if (c == q) s = false; continue; } if (c is '"' or '\'') { s = true; q = c; continue; } if (c == '/' && l[i + 1] == '/') return i; } return -1; }
