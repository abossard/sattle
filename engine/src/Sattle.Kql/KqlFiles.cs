using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql;

/// <summary>The definitions with the projections written in KQL put in: each module that has files `module.projection.kql` printed again
/// from the operators the KQL texts give, so the catalog loads plain text through its one loader and checker. `Facts` are the facts put in, by projection id:
/// the operators of the KQL text, under the projection node of today's text (its purpose and the history it keeps, which the KQL text does not state).</summary>
public sealed record KqlFiles(List<SourceFile> Files, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<string, IReadOnlyList<Element>> Facts)
{
    public bool Ok => !Diagnostics.Any(d => d.IsError);

    /// <summary>The projections in KQL of a definitions folder, `module.projection.kql`.</summary>
    public static List<SourceFile> ReadDirectory(string dir) =>
        [.. Directory.EnumerateFiles(dir, "*.kql").Order(StringComparer.Ordinal).Select(f => new SourceFile(f, File.ReadAllText(f)))];

    /// <summary>`files` with the projections of `kql` in place of the ones of the same module and name. The inputs of a KQL projection are the graph ports of the projection it replaces.
    /// `loaded` is the catalog of `files` when the caller has it.</summary>
    public static KqlFiles Of(IReadOnlyList<SourceFile> files, IReadOnlyList<SourceFile> kql, Catalog? loaded = null)
    {
        var result = files.ToList(); var diags = new List<Diagnostic>(); var facts = new Dictionary<string, IReadOnlyList<Element>>(StringComparer.Ordinal);
        if (kql.Count == 0) return new(result, diags, facts);
        var cat = loaded;
        if (cat is null)
        {
            cat = new Catalog();
            var res = cat.Load(files, updatePins: true);
            if (res.Diagnostics.Any(d => d.IsError)) return new(result, [.. res.Diagnostics.Where(d => d.IsError)], facts);
        }
        Diagnostic Err(SourceFile f, string message) => new("KqlFile", "error", "", message, f.Path, 1, 1);
        foreach (var byModule in kql.GroupBy(f => Path.GetFileNameWithoutExtension(f.Path) is var n && n.LastIndexOf('.') is > 0 and var dot ? n[..dot] : n))
        {
            var module = byModule.Key; var mid = Ids.Module(module);
            var at = result.FindIndex(f => Regex.IsMatch(f.Text, $@"(?m)^module\s+{Regex.Escape(module)}\s+@"));
            if (at < 0) { diags.AddRange(byModule.Select(f => Err(f, $"there is no module {module}: a projection in KQL is named module.projection.kql"))); continue; }
            var g = cat.Graph.Clone(); var d = new DefView(cat.Graph); var ok = true;
            foreach (var f in byModule)
            {
                var name = Path.GetFileNameWithoutExtension(f.Path)[(module.Length + 1)..];
                var pid = Ids.Child(mid, "projection", name);
                if (cat.Graph.Entity(pid) is null) { diags.Add(Err(f, $"module {module} declares no projection {name} for this file to replace")); ok = false; continue; }
                var inputs = d.Children(pid, "port").Where(p => d.Text(p, "kind") == "graph").ToDictionary(p => d.Text(p, "name")!, p => Ids.ModuleOf(d.Target(p, "expects")!));
                var rows = d.Children(pid, "port").Where(p => d.Text(p, "kind") == "rows").ToDictionary(p => d.Text(p, "name")!, p => d.Target(p, "rowType")!);
                var low = KqlFrontEnd.Lower(f.Text, cat.Graph, module, inputs, f.Path, rows);
                diags.AddRange(low.Diagnostics);
                if (!low.Ok) { ok = false; continue; }
                if (low.ProjectionId != pid) { diags.Add(Err(f, $"the graph line names {Ids.Name(low.ProjectionId)}, the file name {name}")); ok = false; continue; }
                bool Under(string id) => id.StartsWith(pid + "/", StringComparison.Ordinal);
                // the projection node keeps what the KQL text does not state: its purpose, the history it keeps and its description; whether it is forward follows its rules
                var keep = new HashSet<string>(new[] { "purpose", "retain", "description" }.Select(Ids.KProp), StringComparer.Ordinal);
                Entity Settled(Entity mine) => mine with { Props = Props.Of(mine.Props.Items.Where(kv => !keep.Contains(kv.Key)).Concat(cat.Graph.Entity(pid)!.Props.Items.Where(kv => keep.Contains(kv.Key)))) };
                var put = low.Facts.Elements().Select(e => e is Entity { Id: var id } en && id == pid ? Settled(en) : e).ToList();
                // an ingestion projection is not forward and so has rules; until the edit stage gives rules a form in KQL, one readonly rule governs all its steps
                var err = g.Apply([.. g.Rels.Where(r => r.Source == pid || r.Target == pid || Under(r.Source) || Under(r.Target)).Select(Edit.Remove), .. g.Entities.Where(e => e.Id == pid || Under(e.Id)).Select(Edit.Remove)])
                    ?? g.Apply([.. put.Select(Edit.Create)]);
                if (err is not null) { diags.Add(Err(f, "the operators do not fit the module: " + err)); ok = false; continue; }
                facts[pid] = put;
            }
            if (ok) result[at] = result[at] with { Text = Printer.Print(g, module) };
        }
        return new(result, diags, facts);
    }
}
