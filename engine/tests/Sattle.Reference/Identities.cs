using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Sattle.Core;

namespace Sattle.Reference;

/// <summary>An identity key: a domain and its canonical components (Def. 1.9).</summary>
public sealed record IdentityKey(string Domain, ImmutableArray<string> Components)
{
    public string Text => Domain + "\u001f" + string.Join('\u001f', Components);
}

/// <summary>An asserted equivalence of two keys (a `Link` row that was once present). Timeless: a later retraction of the row does not withdraw it (Theorem 1.5).</summary>
public sealed record LinkFact(IdentityKey Left, IdentityKey Right)
{
    public string Text => Left.Text + "\u001e" + Right.Text;
}

/// <summary>Identity as a function of the set of asserted links (Proposition 1.8): the keys form classes under the links, and the id of a class is the smallest id allocated for any of its keys.</summary>
public sealed class Identities(string tenant, IEnumerable<LinkFact> links)
{
    readonly string tenant = tenant;
    readonly ImmutableArray<ImmutableSortedSet<string>> classes = Classes(links);
    readonly ImmutableSortedDictionary<string, IdentityKey> keys = links.SelectMany(l => new[] { l.Left, l.Right }).DistinctBy(k => k.Text).ToImmutableSortedDictionary(k => k.Text, k => k, StringComparer.Ordinal);

    /// <summary>Allocation (Def. 1.9, Theorem 1.4): a pure function of tenant, domain and components.</summary>
    public static string Allocate(string tenant, IdentityKey key)
    {
        var sb = new StringBuilder("[");
        Jcs.Str(sb, tenant); sb.Append(','); Jcs.Str(sb, key.Domain);
        foreach (var c in key.Components) { sb.Append(','); Jcs.Str(sb, Jcs.Encode(Value.Text(c))); }
        sb.Append(']');
        return "e:" + Digests.Base32(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..26];
    }

    /// <summary>The relationship id: a hash of tenant, output module, key rule and the endpoint ids (Def. 5.9).</summary>
    public static string RelationshipId(string tenant, string outputModule, string keyRule, string source, string target)
    {
        var sb = new StringBuilder("[");
        Jcs.Str(sb, tenant); sb.Append(','); Jcs.Str(sb, outputModule); sb.Append(','); Jcs.Str(sb, keyRule); sb.Append(','); Jcs.Str(sb, source); sb.Append(','); Jcs.Str(sb, target); sb.Append(']');
        return "r:" + Digests.Base32(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..26];
    }

    public string IdOf(IdentityKey key)
    {
        var own = classes.FirstOrDefault(c => c.Contains(key.Text));
        return own is null ? Allocate(tenant, key) : own.Select(k => Allocate(tenant, keys[k])).Min(StringComparer.Ordinal)!;
    }

    /// <summary>Canonical text of a component; null when the value has no canonical form.</summary>
    public static string? Canonicalize(string canonicalizer, string v) => canonicalizer switch
    {
        "Exact" => v,
        "LowerInvariant" => v.ToLowerInvariant(),
        "Trim" => v.Trim(),
        "IpCanonical" => IPAddress.TryParse(v, out var ip) ? ip.ToString() : null,
        "UuidLower" => Guid.TryParse(v, out var g) ? g.ToString("D") : null,
        _ => throw new Undefined($"canonicalizer {canonicalizer}"),
    };

    /// <summary>Connected components of the link graph, by repeated merging until nothing changes.</summary>
    static ImmutableArray<ImmutableSortedSet<string>> Classes(IEnumerable<LinkFact> links)
    {
        var sets = links.Select(l => ImmutableSortedSet.Create(StringComparer.Ordinal, l.Left.Text, l.Right.Text)).ToImmutableArray();
        while (true)
        {
            var merged = false;
            for (var i = 0; i < sets.Length && !merged; i++)
                for (var j = i + 1; j < sets.Length && !merged; j++)
                    if (sets[i].Overlaps(sets[j])) { sets = sets.SetItem(i, sets[i].Union(sets[j])).RemoveAt(j); merged = true; }
            if (!merged) return sets;
        }
    }
}
