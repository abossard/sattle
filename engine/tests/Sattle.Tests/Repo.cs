using Sattle.Language;

namespace Sattle.Tests;

/// <summary>Locates the repository folders from the test binary location.</summary>
public static class Repo
{
    public static string Root { get; } = FindRoot();
    public static string Definitions => Path.Combine(Root, "engine", "definitions");
    /// <summary>The folder of the local data (`fixtures`, `captures`): `lab` of the repository, or the folder the environment variable SATTLE_LAB names. With an empty folder the tests run as on a fresh clone, where neither is there.</summary>
    static string Lab => Environment.GetEnvironmentVariable("SATTLE_LAB") is { Length: > 0 } folder ? folder : Path.Combine(Root, "lab");
    /// <summary>The scrubbed excerpt of a real capture. Its data files are generated locally and are not in the repository.</summary>
    public static string Fixtures => Path.Combine(Lab, "fixtures");
    /// <summary>The synthetic estate: a capture with invented names only, in the repository.</summary>
    public static string Estate => Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");
    public static string Captures => Path.Combine(Lab, "captures");
    /// <summary>The real captures, oldest first (`sattle replay latest` takes the last).</summary>
    public static IReadOnlyList<string> CaptureDirs => Sattle.Cli.Program.Captures(Captures);

    /// <summary>The local space binding for the real captures (gitignored, next to them): the tracked `acme.space.sattle` has invented workload arguments, this file has the real ones. Null when it is not there.</summary>
    public static string? Overlay => File.Exists(Path.Combine(Captures, "space-overlay.sattle")) ? Path.Combine(Captures, "space-overlay.sattle") : null;

    /// <summary>The tracked space binding for `lab/fixtures` (a different cluster than the invented estate the tracked `acme.space` binds).</summary>
    public static string FixturesOverlay => Path.Combine(Root, "engine", "overlays", "fixtures.space.sattle");

    /// <summary>The space binding `sattle replay DIR` applies to `dir`: the local overlay for the real captures (null when absent), the tracked one for `lab/fixtures`, none otherwise.</summary>
    public static string? OverlayFor(string dir) => IsCapture(dir) ? Overlay : Path.GetFullPath(dir).TrimEnd('/') == Path.GetFullPath(Fixtures).TrimEnd('/') ? FixturesOverlay : null;

    public static bool IsCapture(string dir) => Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Captures), StringComparison.Ordinal);

    /// <summary>The definitions, with the local overlay replacing `acme.space` when `forCaptures` and the overlay exists.</summary>
    public static List<SourceFile> DefinitionFiles(bool forCaptures) => DefinitionFilesWith(forCaptures ? Overlay : null);

    /// <summary>The definitions `sattle replay DIR` reads for `dir`: with the overlay it applies.</summary>
    public static List<SourceFile> DefinitionFilesFor(string dir) => DefinitionFilesWith(OverlayFor(dir));

    static List<SourceFile> DefinitionFilesWith(string? overlay)
    {
        var files = Catalog.ReadDirectory(Definitions).ToList();
        if (overlay is not null) { var i = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal)); files[i] = files[i] with { Text = File.ReadAllText(overlay) }; }
        return files;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(Catalog Cat, LoadResult Result)>> WithOverlay = new();

    /// <summary>The checked catalog for replaying `dir`: the examples, or with the overlay `sattle replay` applies to it (the local one for the real captures when it exists, the tracked one for `lab/fixtures`).</summary>
    public static Catalog CatalogFor(string dir, out LoadResult result)
    {
        if (OverlayFor(dir) is not { } overlay) return LoadExamples(out result);
        var loaded = WithOverlay.GetOrAdd(overlay, o => new(() => { var cat = new Catalog(); var res = cat.Load(DefinitionFilesWith(o), updatePins: true); return (cat, res); })).Value;
        result = loaded.Result; return loaded.Cat;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Clusters = new();
    /// <summary>The cluster name the records of a capture carry (read from its Hubble flows, as `sattle replay` does).</summary>
    public static string ClusterOf(string dir) => Clusters.GetOrAdd(dir, d => Sattle.Cli.Program.DetectCluster(d));
    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    static readonly Lazy<(Catalog Cat, LoadResult Result)> Examples = new(() =>
    {
        var cat = new Catalog();
        var res = cat.Load(Catalog.ReadDirectory(Definitions), updatePins: true);
        return (cat, res);
    });

    /// <summary>The example modules, pinned (digests are filled in by the loader on first use, as `check --update-pins` does). One checked catalog per test run: engines and hosts never mutate a catalog, so the tests share it; a test that needs its own loads `new Catalog()`.</summary>
    public static Catalog LoadExamples(out LoadResult result)
    {
        result = Examples.Value.Result;
        return Examples.Value.Cat;
    }
}
