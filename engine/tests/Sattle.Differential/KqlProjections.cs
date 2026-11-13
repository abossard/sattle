using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Sattle.Kql;
using Sattle.Language;

namespace Sattle.Differential;

/// <summary>With `SATTLE_PROJECTIONS=kql` every definitions folder the tests read gives its projections from the `.kql` files next to it (`sattle replay --projections kql` does the same). Unset, nothing changes. The translation loads a whole catalog, so it is done once per distinct content of the files read.</summary>
static class KqlProjections
{
    static readonly ConcurrentDictionary<string, Lazy<List<SourceFile>>> Translated = new();

    [ModuleInitializer]
    internal static void Install()
    {
        if (Environment.GetEnvironmentVariable("SATTLE_PROJECTIONS") != "kql") return;
        Catalog.AfterRead = (files, dir) =>
        {
            var kql = KqlFiles.ReadDirectory(dir);
            var key = string.Join("\u001e", files.Concat(kql).Select(f => f.Path + "\u001f" + f.Text));
            return [.. Translated.GetOrAdd(key, _ => new(() =>
            {
                var put = KqlFiles.Of(files, kql);
                return put.Ok ? put.Files : throw new InvalidOperationException(string.Join("\n", put.Diagnostics));
            })).Value];
        };
    }
}
