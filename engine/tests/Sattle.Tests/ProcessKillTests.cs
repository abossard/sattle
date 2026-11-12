using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>T-RESTART (premise J1 of Proposition 11.9, Proposition 10.8): `sattle serve` of the estate into a SQLite store, in its own process, killed (not an exception) at a generated journal position, sometimes in the middle of writing an entry or a seal. The restarted space equals a space that processed exactly the journaled inputs without interruption (a fresh store with the same entries): digest, sequence number, heads and `/space`. A cut last entry is refused or discarded, never half applied.</summary>
[Trait("Category", "Slow")]
public class ProcessKillTests
{
    static string Cli => Path.Combine(Repo.Root, "engine", "src", "Sattle.Cli", "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0", "sattle.dll");
    static int FreePort() { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    static long Entries(string path)
    {
        try
        {
            using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); c.Open();
            using var q = c.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM journal";
            return (long)q.ExecuteScalar()!;
        }
        catch (SqliteException) { return 0; }
    }
    static HostOptions Options => new() { Cluster = "synth" };
    static (string Digest, long Seq, string Space) Of(SpaceHost h) => (h.StateDigest(), h.Seq, h.SpaceInfo().ToJsonString());

    [Theory]
    [InlineData(1, 0)] [InlineData(2, 25)] [InlineData(3, 7)] [InlineData(4, 0)]
    public void A_process_killed_at_a_generated_journal_position_restarts_to_the_uninterrupted_state(int seed, int sealEvery)
    {
        var rnd = new Random(seed);
        var path = Path.Combine(Path.GetTempPath(), $"sattle-kill-{Guid.NewGuid():N}.db");
        try
        {
            var target = rnd.Next(15, 160);
            var psi = new ProcessStartInfo("dotnet", $"\"{Cli}\" serve \"{EstateTests.EstateDir}\" --cluster synth --store sqlite={path} --port {FreePort()} {(sealEvery > 0 ? $"--seal-every {sealEvery}" : "--no-seal")}")
            { WorkingDirectory = Path.Combine(Repo.Root, "engine"), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using (var p = Process.Start(psi)!)
            {
                p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { }; p.BeginOutputReadLine(); p.BeginErrorReadLine();
                var sw = Stopwatch.StartNew();
                while (Entries(path) < target && !p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(rnd.Next(0, 3));
                if (!p.HasExited) p.Kill(entireProcessTree: true);          // a kill, wherever the process is: between entries, inside one, inside a seal
                p.WaitForExit();
            }
            var entries = new SqliteGraphStore(path).Let(s => { using (s) return s.JournalRead().ToList(); });
            Assert.True(entries.Count > 1, $"the killed process journaled {entries.Count} entries");
            (string Digest, long Seq, string Space) restarted, fresh;
            using (var h = SpaceHost.OpenDurable(new SqliteGraphStore(path), null, Options)) { Assert.True(h.Restored); restarted = Of(h); }
            var memory = new InMemoryGraphStore();
            foreach (var e in entries) ((IJournalStore)memory).JournalAppend(e);
            using (var h = SpaceHost.OpenDurable(memory, null, Options with { UseSeal = false })) fresh = Of(h);
            Assert.Equal(fresh, restarted);
            // a last entry cut in the middle of its write: refused or discarded, never half applied
            var last = entries[^1];
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open(); using var ins = c.CreateCommand();
                ins.CommandText = "INSERT INTO journal(n, body) VALUES ((SELECT MAX(n) + 1 FROM journal), $b)"; ins.Parameters.AddWithValue("$b", last[..(last.Length / 2)]); ins.ExecuteNonQuery();
            }
            try
            {
                using var h = SpaceHost.OpenDurable(new SqliteGraphStore(path), null, Options with { UseSeal = false });
                Assert.Equal(restarted.Digest, h.StateDigest());              // discarded
            }
            catch (Exception e) when (e is HostError or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { }    // refused
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }
}

static class LetExtension
{
    public static TR Let<T, TR>(this T x, Func<T, TR> f) => f(x);
}
