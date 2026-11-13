using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql;

/// <summary>The definitions with the projections written in KQL put in: each module that has files `module.projection.kql` printed again
/// from the operators the KQL texts give, so the catalog loads plain text through its one loader and checker. `Facts` are the facts put in, by projection id.
/// `Places` gives each operator (by its id, which printing keeps) the file and the place in its KQL text it comes from; the catalog holds no places.</summary>
public sealed record KqlFiles(List<SourceFile> Files, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<string, IReadOnlyList<Element>> Facts,
    IReadOnlyDictionary<string, (string File, int Start, int End)> Places)
{
    public bool Ok => !Diagnostics.Any(d => d.IsError);

    /// <summary>The projections in KQL of a definitions folder, `module.projection.kql`.</summary>
    public static List<SourceFile> ReadDirectory(string dir) =>
        [.. Directory.EnumerateFiles(dir, "*.kql").Order(StringComparer.Ordinal).Select(f => new SourceFile(f, File.ReadAllText(f)))];

    /// <summary>`files` with the projections of `kql` added to their modules, each in place of a projection of the same name that the module's text declares.
    /// A KQL text states all of its projection: inputs, history and description. `loaded` is the catalog of `files` when the caller has it.</summary>
    public static KqlFiles Of(IReadOnlyList<SourceFile> files, IReadOnlyList<SourceFile> kql, Catalog? loaded = null)
    {
        var result = files.ToList(); var diags = new List<Diagnostic>(); var facts = new Dictionary<string, IReadOnlyList<Element>>(StringComparer.Ordinal);
        var places = new Dictionary<string, (string File, int Start, int End)>(StringComparer.Ordinal);
        if (kql.Count == 0) return new(result, diags, facts, places);
        var cat = loaded;
        if (cat is null)
        {
            cat = new Catalog();
            var res = cat.Load(files, updatePins: true);
            if (res.Diagnostics.Any(d => d.IsError)) return new(result, [.. res.Diagnostics.Where(d => d.IsError)], facts, places);
        }
        Diagnostic Err(SourceFile f, string message) => new("KqlFile", "error", "", message, f.Path, 1, 1);
        foreach (var f in kql.Where(f => Path.GetFileNameWithoutExtension(f.Path).LastIndexOf('.') <= 0)) diags.Add(Err(f, "a projection in KQL is named module.projection.kql"));
        foreach (var byModule in kql.Where(f => Path.GetFileNameWithoutExtension(f.Path).LastIndexOf('.') > 0).GroupBy(f => Path.GetFileNameWithoutExtension(f.Path) is var n && n.LastIndexOf('.') is > 0 and var dot ? n[..dot] : n))
        {
            var module = byModule.Key; var mid = Ids.Module(module);
            var at = result.FindIndex(f => Regex.IsMatch(f.Text, $@"(?m)^module\s+{Regex.Escape(module)}\s*[@;]"));
            if (at < 0) { diags.AddRange(byModule.Select(f => Err(f, $"there is no module {module}: a projection in KQL is named module.projection.kql"))); continue; }
            var g = cat.Graph.Clone(); var ok = true;
            foreach (var f in byModule)
            {
                var name = Path.GetFileNameWithoutExtension(f.Path)[(module.Length + 1)..];
                var pid = Ids.Child(mid, "projection", name);
                var low = KqlFrontEnd.Lower(f.Text, cat.Graph, module, f.Path);
                diags.AddRange(low.Diagnostics);
                if (!low.Ok) { ok = false; continue; }
                if (low.ProjectionId != pid) { diags.Add(Err(f, $"the graph line names {Ids.Name(low.ProjectionId)}, the file name {name}")); ok = false; continue; }
                bool Under(string id) => id.StartsWith(pid + "/", StringComparison.Ordinal);
                var put = low.Facts.Elements().ToList();
                var err = g.Apply([.. g.Rels.Where(r => r.Source == pid || r.Target == pid || Under(r.Source) || Under(r.Target)).Select(Edit.Remove), .. g.Entities.Where(e => e.Id == pid || Under(e.Id)).Select(Edit.Remove)])
                    ?? g.Apply([.. put.Select(Edit.Create)]);
                if (err is not null) { diags.Add(Err(f, "the operators do not fit the module: " + err)); ok = false; continue; }
                facts[pid] = put;
                foreach (var (op, (start, end)) in low.OperatorPlaces) places[op] = (f.Path, start, end);
            }
            if (ok) result[at] = result[at] with { Text = Printer.Print(g, module) };
        }
        return new(result, diags, facts, places);
    }
}
