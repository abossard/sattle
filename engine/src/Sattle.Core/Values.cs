using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Sattle.Core;

public enum Kind { Text, Integer, Decimal, Boolean, Instant, Null, Entity, List }

/// <summary>Graph values (Ch. 1, Def. 1.1). Constructors enforce the normal form of Def. 1.3, so every Value is normal.</summary>
public abstract record Value
{
    public abstract Kind Kind { get; }
    public static readonly Value Null = NullValue.Instance;
    public static Value Text(string s) => new TextValue(s);
    public static Value Integer(BigInteger n) => new IntegerValue(n);
    public static Value Integer(long n) => new IntegerValue(n);
    public static Value Decimal(decimal q) => new DecimalValue(q);
    public static Value Boolean(bool b) => b ? BooleanValue.True : BooleanValue.False;
    public static Value Instant(long ns) => new InstantValue(ns);
    public static Value Entity(string id) => new EntityValue(id);
    public static Value List(IEnumerable<Value> items) => new ListValue([.. items]);
}

public sealed record TextValue : Value
{
    public string S { get; }
    public TextValue(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (char.IsSurrogate(s[i]))
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                throw new ArgumentException("lone surrogate in text (N4)");
            }
        S = s;
    }
    public override Kind Kind => Kind.Text;
}
public sealed record IntegerValue(BigInteger N) : Value { public override Kind Kind => Kind.Integer; }
public sealed record DecimalValue(decimal Q) : Value { public override Kind Kind => Kind.Decimal; }
public sealed record BooleanValue(bool B) : Value
{
    public static readonly BooleanValue True = new(true), False = new(false);
    public override Kind Kind => Kind.Boolean;
}
/// <summary>Instant as nanoseconds since the Unix epoch (range about 1678 to 2262, a deviation from the unbounded instant of Def. 1.1).</summary>
public sealed record InstantValue(long Ns) : Value { public override Kind Kind => Kind.Instant; }
public sealed record NullValue : Value
{
    public static readonly NullValue Instance = new();
    public override Kind Kind => Kind.Null;
}
public sealed record EntityValue(string Id) : Value { public override Kind Kind => Kind.Entity; }
public sealed record ListValue(ImmutableArray<Value> Items) : Value
{
    public override Kind Kind => Kind.List;
    public bool Equals(ListValue? o) => o is not null && Items.AsSpan().SequenceEqual(o.Items.AsSpan());
    public override int GetHashCode() { var h = new HashCode(); foreach (var i in Items) h.Add(i); return h.ToHashCode(); }
}

/// <summary>Normal-form text of scalars (N1 to N3) and instants.</summary>
public static class Norm
{
    public static string Decimal(decimal q)
    {
        var s = q.ToString("G29", CultureInfo.InvariantCulture);
        if (s.Contains('E')) s = q.ToString("F28", CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s is "-0" or "" ? "0" : s;
    }

    public static string Instant(long ns)
    {
        var sec = Math.DivRem(ns, 1_000_000_000L, out var frac);
        if (frac < 0) { sec--; frac += 1_000_000_000L; }
        var dt = DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
        var s = dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (frac != 0) s += "." + frac.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');
        return s + "Z";
    }

    /// <summary>RFC 3339 with offset or Z, up to 9 fractional digits. Leap seconds are rejected (N3).</summary>
    public static bool TryParseInstant(ReadOnlySpan<char> t, out long ns)
    {
        ns = 0;
        if (t.Length < 20 || t[4] != '-' || t[7] != '-' || (t[10] != 'T' && t[10] != 't') || t[13] != ':' || t[16] != ':') return false;
        if (!int.TryParse(t[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(t.Slice(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mo) ||
            !int.TryParse(t.Slice(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var d) ||
            !int.TryParse(t.Slice(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(t.Slice(14, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mi) ||
            !int.TryParse(t.Slice(17, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var s)) return false;
        if (s > 59 || h > 23 || mi > 59 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Max(y, 1), mo)) return false;
        var i = 19;
        long frac = 0;
        if (i < t.Length && t[i] == '.')
        {
            var st = ++i;
            while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
            var digits = i - st;
            if (digits is < 1 or > 9) return false;
            frac = long.Parse(t[st..i], NumberStyles.None, CultureInfo.InvariantCulture);
            for (var k = digits; k < 9; k++) frac *= 10;
        }
        long offSec = 0;
        if (i < t.Length && (t[i] == 'Z' || t[i] == 'z')) i++;
        else if (i + 6 == t.Length && (t[i] == '+' || t[i] == '-') && t[i + 3] == ':')
        {
            if (!int.TryParse(t.Slice(i + 1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var oh) ||
                !int.TryParse(t.Slice(i + 4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var om)) return false;
            offSec = (oh * 3600 + om * 60) * (t[i] == '-' ? -1 : 1);
            i += 6;
        }
        else return false;
        if (i != t.Length) return false;
        var secs = new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero).ToUnixTimeSeconds() - offSec;
        try { ns = checked(secs * 1_000_000_000L + frac); } catch (OverflowException) { return false; }
        return true;
    }
}

/// <summary>RFC 8785 serialization of the trees of Ch. 1 and Ch. 3. No JSON numbers occur, so the number rule of JCS is never exercised (Rule 1.2).</summary>
public static class Jcs
{
    public static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    public static void Tree(StringBuilder sb, Value v)
    {
        switch (v)
        {
            case NullValue: sb.Append("{\"kind\":\"null\"}"); return;
            case TextValue t: Leaf(sb, "text", t.S); return;
            case IntegerValue i: Leaf(sb, "integer", i.N.ToString(CultureInfo.InvariantCulture)); return;
            case DecimalValue d: Leaf(sb, "decimal", Norm.Decimal(d.Q)); return;
            case InstantValue n: Leaf(sb, "instant", Norm.Instant(n.Ns)); return;
            case EntityValue e: Leaf(sb, "entity", e.Id); return;
            case BooleanValue b: sb.Append("{\"kind\":\"boolean\",\"value\":").Append(b.B ? "true" : "false").Append('}'); return;
            case ListValue l:
                sb.Append("{\"kind\":\"list\",\"value\":[");
                for (var k = 0; k < l.Items.Length; k++) { if (k > 0) sb.Append(','); Tree(sb, l.Items[k]); }
                sb.Append("]}");
                return;
            default: throw new InvalidOperationException();
        }
    }

    static void Leaf(StringBuilder sb, string kind, string value)
    {
        sb.Append("{\"kind\":\"").Append(kind).Append("\",\"value\":");
        Str(sb, value);
        sb.Append('}');
    }

    public static string Encode(Value v) { var sb = new StringBuilder(); Tree(sb, v); return sb.ToString(); }

    public static void Props(StringBuilder sb, Props p)
    {
        sb.Append('{');
        var first = true;
        foreach (var (k, v) in p.Items)
        {
            if (!first) sb.Append(',');
            first = false;
            Str(sb, k); sb.Append(':'); Tree(sb, v);
        }
        sb.Append('}');
    }

    public static void Element(StringBuilder sb, Element e)
    {
        switch (e)
        {
            case Entity en:
                sb.Append("{\"id\":"); Str(sb, en.Id); sb.Append(",\"props\":"); Props(sb, en.Props); sb.Append(",\"type\":"); Str(sb, en.Type); sb.Append('}');
                break;
            case Rel r:
                sb.Append("{\"id\":"); Str(sb, r.Id); sb.Append(",\"props\":"); Props(sb, r.Props);
                sb.Append(",\"source\":"); Str(sb, r.Source); sb.Append(",\"target\":"); Str(sb, r.Target); sb.Append(",\"type\":"); Str(sb, r.Type); sb.Append('}');
                break;
            case OntologyState o:
                sb.Append("{\"digest\":"); Str(sb, o.Digest); sb.Append(",\"id\":"); Str(sb, o.Id); sb.Append(",\"version\":"); Str(sb, o.Version); sb.Append('}');
                break;
        }
    }

    public static string Encode(Element e) { var sb = new StringBuilder(); Element(sb, e); return sb.ToString(); }
}

public static class Digests
{
    public static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    public static string Of(string canonicalJson) => "sha256:" + Sha256Hex(canonicalJson);

    const string B32 = "abcdefghijklmnopqrstuvwxyz234567";
    public static string Base32(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder();
        int buf = 0, bits = 0;
        foreach (var x in b)
        {
            buf = (buf << 8) | x; bits += 8;
            while (bits >= 5) { sb.Append(B32[(buf >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(B32[(buf << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
