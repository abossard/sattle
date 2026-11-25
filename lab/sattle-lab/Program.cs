using Sattle.Cli;
using Sattle.Runtime;

namespace Sattle.Lab;

/// <summary>`sattle-lab`: the engine's command line with what the lab adds. The Azure store (`--store cosmos=ENDPOINT[,db=NAME][,drop]`), Event Hubs as the live source (`serve --live --eh-namespace NS`), and the benchmarks on a synthetic estate (`bench q14`, `bench estate`, `bench gen-estate`). Every other command is `sattle`'s.</summary>
public static class LabProgram
{
    const string Usage = """
        sattle-lab COMMAND ...   the commands of sattle, and:
          --store cosmos=ENDPOINT[,db=NAME][,drop]       Entra authentication; the emulator's well-known key only for a localhost endpoint; drop deletes the database when the run ends
          serve --live --eh-namespace NS | --eh-resource-group GROUP [--hub sattle-ingest] [--group $Default] [--from latest|earliest]
          bench q14 [--rows N] [--pods N] [--services N] [--per-window N] [--trace-alloc]
          bench estate [--pods N] [--services N] [--resources N] [--windows N] [--census]
          bench gen-estate --nodes N [--minutes 10] [--pairs-per-node 0.24] --out DIR
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return Sattle.Cli.Program.Run(["--help"], Console.Out); }
        var opened = new List<CosmosGraphStore>();
        var ext = new Extensions(
            new Dictionary<string, Func<string, IDurableStore>> { ["cosmos"] = spec => { var s = OpenCosmos(spec); opened.Add(s); return s; } },
            EventHubsSource.FromOptions);
        try
        {
            if (args[0] == "bench" && args.Length > 1 && args[1] is "q14" or "estate" or "gen-estate")
            {
                var o = Sattle.Cli.Program.Options.Parse(args[1..]);
                return args[1] switch { "q14" => LabBench.Q14(o), "estate" => LabBench.Estate(o), _ => LabBench.GenEstate(o) };
            }
            return Sattle.Cli.Program.Run(args, Console.Out, ext);
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        finally
        {
            foreach (var s in opened) foreach (var (kind, (calls, ru)) in s.Charges.OrderBy(x => x.Key)) Console.WriteLine($"cosmos {kind}: {calls:N0} request(s), charge {ru:N1}");
        }
    }

    static CosmosGraphStore OpenCosmos(string spec)
    {
        var parts = spec.Split(',');
        string? Opt(string k) => parts.Skip(1).Select(x => x.Split('=', 2)).Where(x => x[0] == k && x.Length == 2).Select(x => x[1]).FirstOrDefault();
        return CosmosGraphStore.Open(parts[0], Opt("db") ?? "sattle", parts.Skip(1).Contains("drop"));
    }
}
