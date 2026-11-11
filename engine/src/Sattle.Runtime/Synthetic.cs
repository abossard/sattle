using Sattle.Core;

namespace Sattle.Runtime;

/// <summary>A deterministic synthetic estate in the row contracts of the examples: pods, services, endpoints and Hubble flows. For tests and the Q14 benchmark; carries no real identifiers.</summary>
public sealed class SyntheticWorld(IEngineInput engine, int pods, int services, int seed = 1, string cluster = "synth")
{
    public int Pods { get; } = pods;
    public int Services { get; } = services;
    readonly Random rnd = new(seed);
    int pod(int i) => i;
    string PodName(int i) => $"pod-{i}";
    string Ns(int s) => $"ns{s % 5}";
    int ServiceOf(int p) => p % Services;

    public void LoadStructure(long seq = 1)
    {
        var it = engine.Interner;
        var w = new RowWriter(engine.RowLayout("pods"), it);
        for (var i = 0; i < Pods; i++)
        {
            w.Begin(); w.Text("cluster", cluster); w.Text("uid", $"uid-{i}"); w.Text("namespace", Ns(ServiceOf(i))); w.Text("name", PodName(i)); w.Text("phase", "Running"); w.Text("teamLabel", null);
        }
        engine.Ingest("pods", w.Build()!, seq);
        var sw = new RowWriter(engine.RowLayout("services"), it);
        for (var s = 0; s < Services; s++) { sw.Begin(); sw.Text("cluster", cluster); sw.Text("namespace", Ns(s)); sw.Text("name", $"svc-{s}"); }
        engine.Ingest("services", sw.Build()!, seq + 1);
        var ew = new RowWriter(engine.RowLayout("endpoints"), it);
        for (var i = 0; i < Pods; i++)
        {
            ew.Begin(); ew.Text("cluster", cluster); ew.Text("namespace", Ns(ServiceOf(i))); ew.Text("service", $"svc-{ServiceOf(i)}"); ew.Text("address", $"10.0.{i / 250}.{i % 250}"); ew.Text("podUid", $"uid-{i}");
        }
        engine.Ingest("endpoints", ew.Build()!, seq + 2);
    }

    /// <summary>One batch of flows: random pod pairs spread over [startNs, startNs + spanNs).</summary>
    public Batch Flows(int count, long startNs, long spanNs, double dropRate = 0.02, int pairsLimit = 0)
    {
        var it = engine.Interner;
        var layout = engine.RowLayout("flows");
        var w = new RowWriter(layout, it);
        int cTime = w.Col("time"), cCluster = w.Col("cluster"), cVerdict = w.Col("verdict"), cSn = w.Col("srcNamespace"), cSp = w.Col("srcPod"), cDn = w.Col("dstNamespace"), cDp = w.Col("dstPod");
        var cl = it.Intern(cluster); var fwd = it.Intern("FORWARDED"); var drp = it.Intern("DROPPED");
        for (var i = 0; i < count; i++)
        {
            var a = rnd.Next(pairsLimit > 0 ? Math.Min(pairsLimit, Pods) : Pods); var b = (a * 7 + 1 + rnd.Next(3)) % Pods;
            w.Begin();
            w.Instant(cTime, startNs + (long)(rnd.NextDouble() * spanNs)); w.Code(cCluster, cl); w.Code(cVerdict, rnd.NextDouble() < dropRate ? drp : fwd);
            w.Code(cSn, it.Intern(Ns(ServiceOf(a)))); w.Code(cSp, it.Intern(PodName(a))); w.Code(cDn, it.Intern(Ns(ServiceOf(b)))); w.Code(cDp, it.Intern(PodName(b)));
        }
        return w.Build()!;
    }
}
