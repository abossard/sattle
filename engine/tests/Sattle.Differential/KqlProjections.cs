using System.Runtime.CompilerServices;
using Sattle.Kql;
using Sattle.Language;

namespace Sattle.Differential;

/// <summary>With `SATTLE_PROJECTIONS=kql` every definitions folder the tests read gives its projections from the `.kql` files next to it (`sattle replay --projections kql` does the same). Unset, nothing changes.</summary>
static class KqlProjections
{
    [ModuleInitializer]
    internal static void Install()
    {
        if (Environment.GetEnvironmentVariable("SATTLE_PROJECTIONS") != "kql") return;
        Catalog.AfterRead = (files, dir) =>
        {
            var put = KqlFiles.Of(files, KqlFiles.ReadDirectory(dir));
            return put.Ok ? put.Files : throw new InvalidOperationException(string.Join("\n", put.Diagnostics));
        };
    }
}
