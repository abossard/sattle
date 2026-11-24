using Sattle.Language;

namespace Sattle.Tests;

/// <summary>Locates the repository folders from the test binary location.</summary>
public static class Repo
{
    public static string Root { get; } = FindRoot();
    public static string Definitions => Path.Combine(Root, "engine", "definitions");
    /// <summary>The projection languages the tests read: the registration of the CLI, the one place that names a language. An engine test names none.</summary>
    public static IProjectionSource Projections => Sattle.Cli.Program.Projections;
    /// <summary>The synthetic estate: a capture with invented names only, in the repository.</summary>
    public static string Estate => Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    /// <summary>The definitions.</summary>
    public static List<SourceFile> DefinitionFiles() => DefinitionFilesFor(null);

    /// <summary>The definitions `sattle replay DIR` reads for `dir`: with the arguments file of the directory when it has one (`space-arguments.json`).</summary>
    public static List<SourceFile> DefinitionFilesFor(string? dir, string? folder = null)
    {
        var files = Catalog.ReadDirectory(folder ?? Definitions).ToList();
        return dir is not null && Sattle.Cli.Program.ArgumentsOf(dir) is { } arguments ? Sattle.Cli.Program.WithArguments(files, arguments) : files;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(Catalog Cat, LoadResult Result)>> WithArguments = new();

    /// <summary>The checked catalog for replaying `dir`: the examples, or with the arguments file of the directory when it has one.</summary>
    public static Catalog CatalogFor(string dir, out LoadResult result)
    {
        if (Sattle.Cli.Program.ArgumentsOf(dir) is null) return LoadExamples(out result);
        var loaded = WithArguments.GetOrAdd(Path.GetFullPath(dir), _ => new(() => { var cat = new Catalog(Projections); var res = cat.Load(DefinitionFilesFor(dir), updatePins: true); return (cat, res); })).Value;
        result = loaded.Result; return loaded.Cat;
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    static readonly Lazy<(Catalog Cat, LoadResult Result)> Examples = new(() =>
    {
        var cat = new Catalog(Projections);
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
