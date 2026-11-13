using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql;

/// <summary>The projections written in KQL, for the catalog: a file `module.projection.kql` among the definition files gives the projection of that name
/// in its module, with the place in the file each operator comes from. Holds no state: `new Catalog(new KqlSource())`.</summary>
public sealed class KqlSource : IProjectionSource
{
    public ProjectionFacts Lower(Graph catalog, string module, IReadOnlyList<SourceFile> files)
    {
        var projections = new Dictionary<string, IReadOnlyList<Element>>(StringComparer.Ordinal);
        var places = new Dictionary<string, (string File, Pos Pos)>(StringComparer.Ordinal);
        var diags = new List<Diagnostic>();
        foreach (var f in files.Where(f => f.Path.EndsWith(".kql", StringComparison.Ordinal) && ModuleOf(f.Path) == module).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var pid = Ids.Child(Ids.Module(module), "projection", Path.GetFileNameWithoutExtension(f.Path)[(module.Length + 1)..]);
            var low = KqlFrontEnd.Lower(f.Text, catalog, module, f.Path);
            diags.AddRange(low.Diagnostics);
            if (!low.Ok) continue;
            if (low.ProjectionId != pid) { diags.Add(new Diagnostic("KqlFile", "error", pid, $"the graph line names {Ids.Name(low.ProjectionId)}, the file name {Ids.Name(pid)}", f.Path, 1, 1)); continue; }
            projections[pid] = [.. low.Facts.Elements()];
            places[pid] = (f.Path, PosOf(f.Text, KqlFrontEnd.Header.Match(f.Text).Index));
            foreach (var (op, (start, _)) in low.OperatorPlaces) places[op] = (f.Path, PosOf(f.Text, start));
        }
        return new ProjectionFacts(projections, places, diags);
    }

    /// <summary>The module a file `module.projection.kql` belongs to.</summary>
    static string ModuleOf(string path) => Path.GetFileNameWithoutExtension(path) is var n && n.LastIndexOf('.') is > 0 and var dot ? n[..dot] : "";

    static Pos PosOf(string text, int offset)
    {
        var line = 1; var col = 1;
        for (var i = 0; i < Math.Min(offset, text.Length); i++) { if (text[i] == '\n') { line++; col = 1; } else col++; }
        return new Pos(line, col);
    }
}
