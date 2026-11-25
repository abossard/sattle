using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>What a host outside the engine adds to the command line: store kinds by the name before `=` in `--store KIND=SPEC`, and the live source of `serve --live` when no `--follow FILE` is given. The engine's own command line has none.</summary>
public sealed record Extensions(IReadOnlyDictionary<string, Func<string, IDurableStore>> Stores, Func<Program.Options, ILiveSource>? LiveSource = null)
{
    public static readonly Extensions None = new(new Dictionary<string, Func<string, IDurableStore>>());
}
