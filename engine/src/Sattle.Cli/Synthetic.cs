using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>A deterministic synthetic estate in the row contracts of the examples: instances, services, endpoints and flows. For tests and the Q14 benchmark; carries no real identifiers.</summary>
public sealed class SyntheticWorld(IEngineInput engine, int instances, int services, int seed = 1, string site = "synth")
{
    public int Instances { get; } = instances;
    public int Services { get; } = services;
    readonly Random rnd = new(seed);
    string InstanceName(int i) => $"instance-{i}";
    string Ns(int s) => $"ns{s % 5}";
    int ServiceOf(int p) => p % Services;

    public void LoadStructure(long seq = 1)
    {
        var it = engine.Interner;
        var w = new RowWriter(engine.RowLayout("instances"), it);
        for (var i = 0; i < Instances; i++)
        {
            w.Begin(); w.Text("site", site); w.Text("uid", $"uid-{i}"); w.Text("namespace", Ns(ServiceOf(i))); w.Text("name", InstanceName(i)); w.Text("phase", "Running"); w.Text("teamLabel", null);
        }
        engine.Ingest("instances", w.Build()!, seq);
        var sw = new RowWriter(engine.RowLayout("services"), it);
        for (var s = 0; s < Services; s++) { sw.Begin(); sw.Text("site", site); sw.Text("namespace", Ns(s)); sw.Text("name", $"svc-{s}"); }
        engine.Ingest("services", sw.Build()!, seq + 1);
        var ew = new RowWriter(engine.RowLayout("endpoints"), it);
        for (var i = 0; i < Instances; i++)
        {
            ew.Begin(); ew.Text("site", site); ew.Text("namespace", Ns(ServiceOf(i))); ew.Text("service", $"svc-{ServiceOf(i)}"); ew.Text("address", $"10.0.{i / 250}.{i % 250}"); ew.Text("instanceUid", $"uid-{i}");
        }
        engine.Ingest("endpoints", ew.Build()!, seq + 2);
    }

    /// <summary>One batch of flows: random instance pairs spread over [startNs, startNs + spanNs).</summary>
    public Batch Flows(int count, long startNs, long spanNs, double dropRate = 0.02, int pairsLimit = 0)
    {
        var it = engine.Interner;
        var layout = engine.RowLayout("flows");
        var w = new RowWriter(layout, it);
        int cTime = w.Col("time"), cSite = w.Col("site"), cVerdict = w.Col("verdict"), cSn = w.Col("srcNamespace"), cSp = w.Col("srcInstance"), cDn = w.Col("dstNamespace"), cDp = w.Col("dstInstance");
        var cl = it.Intern(site); var fwd = it.Intern("FORWARDED"); var drp = it.Intern("DROPPED");
        for (var i = 0; i < count; i++)
        {
            var a = rnd.Next(pairsLimit > 0 ? Math.Min(pairsLimit, Instances) : Instances); var b = (a * 7 + 1 + rnd.Next(3)) % Instances;
            w.Begin();
            w.Instant(cTime, startNs + (long)(rnd.NextDouble() * spanNs)); w.Code(cSite, cl); w.Code(cVerdict, rnd.NextDouble() < dropRate ? drp : fwd);
            w.Code(cSn, it.Intern(Ns(ServiceOf(a)))); w.Code(cSp, it.Intern(InstanceName(a))); w.Code(cDn, it.Intern(Ns(ServiceOf(b)))); w.Code(cDp, it.Intern(InstanceName(b)));
        }
        return w.Build()!;
    }
}
