using System.Security.Cryptography;
using System.Text;

namespace Sattle.Cli;

/// <summary>A response body and its media type.</summary>
public sealed record Payload(string Text, string Type);

/// <summary>ETags are digests of the content they name: the SHA-256 of the media type and the body. Nothing else goes into a tag. A revision number, a catalog revision or a sequence number is unique only within one run of one store (two runs with different definitions reach the same catalog revision), so none of them can identify content across runs; they are used only to decide how long a computed tag stays valid in memory. The tag of a resource is therefore the same in two runs that serve the same bytes, and different in two runs that serve different bytes.</summary>
public sealed class Etags
{
    readonly Dictionary<string, (string Stamp, string Tag)> memo = [];
    readonly object gate = new();

    public static string Of(Payload p) => "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(p.Type + "\n" + p.Text)))[..32] + "\"";

    /// <summary>`key` names the resource (route, graph, revision, query), `stamp` changes whenever the content under that key may have changed within this run (host instance, sequence number, catalog revision). A client's tag that matches the tag remembered for an unchanged stamp is answered without building the body. Otherwise the body is built and its digest decides: equal to the client's tag is a 304, anything else a 200.</summary>
    public (Payload? Body, string Tag, bool NotModified) Decide(string key, string stamp, IReadOnlyCollection<string> ifNoneMatch, Func<Payload> make)
    {
        lock (gate)
            if (memo.TryGetValue(key, out var m) && m.Stamp == stamp && ifNoneMatch.Contains(m.Tag)) return (null, m.Tag, true);
        var body = make(); var tag = Of(body);
        lock (gate) { if (memo.Count > 5000) memo.Clear(); memo[key] = (stamp, tag); }
        return ifNoneMatch.Contains(tag) ? (null, tag, true) : (body, tag, false);
    }
}
