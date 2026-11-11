using System.Text;
using Sattle.Core;

namespace Sattle.Tests;

/// <summary>Ch. 1: normal forms, canonical encoding, Theorem 1.1, Rule 1.2.</summary>
public class ValueTests
{
    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("0.000", "0")]
    [InlineData("-0.0", "0")]
    [InlineData("100", "100")]
    [InlineData("-12.340", "-12.34")]
    [InlineData("0.0000001", "0.0000001")]
    public void N2_decimal_normal_form(string input, string expected) =>
        Assert.Equal(expected, Norm.Decimal(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("2026-11-10T10:41:59.591658996Z", "2026-11-10T10:41:59.591658996Z")]
    [InlineData("2026-11-10T10:41:59.500000000Z", "2026-11-10T10:41:59.5Z")]
    [InlineData("2026-11-10T11:41:59+01:00", "2026-11-10T10:41:59Z")]
    [InlineData("2026-11-10T10:41:59.0Z", "2026-11-10T10:41:59Z")]
    [InlineData("1969-12-31T23:59:59.5Z", "1969-12-31T23:59:59.5Z")]
    public void N3_instant_normal_form(string input, string expected)
    {
        Assert.True(Norm.TryParseInstant(input, out var ns));
        Assert.Equal(expected, Norm.Instant(ns));
    }

    [Theory]
    [InlineData("2026-11-10T10:41:60Z")]
    [InlineData("2026-13-10T10:41:59Z")]
    [InlineData("2026-11-10 10:41:59Z")]
    [InlineData("2026-11-10T10:41:59.1234567890Z")]
    [InlineData("2026-11-10T10:41:59")]
    public void N3_rejects_leap_seconds_and_malformed(string input) => Assert.False(Norm.TryParseInstant(input, out _));

    [Fact]
    public void N4_lone_surrogates_are_rejected() => Assert.Throws<ArgumentException>(() => Value.Text("a\ud800b"));

    [Fact]
    public void Integers_and_decimals_of_equal_numeral_differ()
    {
        Assert.NotEqual(Value.Integer(1), Value.Decimal(1m));
        Assert.NotEqual(Jcs.Encode(Value.Integer(1)), Jcs.Encode(Value.Decimal(1m)));
    }

    [Fact]
    public void Rule_1_2_no_json_numbers_in_encodings()
    {
        var big = Value.Integer(System.Numerics.BigInteger.Parse("1794300264000000000123"));
        var enc = Jcs.Encode(Value.List([big, Value.Instant(1_794_300_264_000_000_000), Value.Decimal(1.25m)]));
        Assert.Equal("{\"kind\":\"list\",\"value\":[{\"kind\":\"integer\",\"value\":\"1794300264000000000123\"},{\"kind\":\"instant\",\"value\":\"2026-11-10T08:44:24Z\"},{\"kind\":\"decimal\",\"value\":\"1.25\"}]}", enc);
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a\"b\\c", "\"a\\\"b\\\\c\"")]
    [InlineData("\u0001\n\t", "\"\\u0001\\n\\t\"")]
    [InlineData("é😀", "\"é😀\"")]
    public void Rfc8785_string_serialization(string s, string expected)
    {
        var sb = new StringBuilder(); Jcs.Str(sb, s);
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void Property_maps_encode_independent_of_supply_order()
    {
        var a = Props.Of(("b", Value.Integer(1)), ("a", Value.Text("x")), ("é", Value.Null));
        var b = Props.Of(("é", Value.Null), ("a", Value.Text("x")), ("b", Value.Integer(1)));
        Assert.Equal(a, b);
        Assert.Equal(Jcs.Encode(new Entity("e", "T", a)), Jcs.Encode(new Entity("e", "T", b)));
        Assert.Throws<ArgumentException>(() => Props.Of(("a", Value.Null), ("a", Value.Null)));
    }

    [Fact]
    public void Digest_of_the_empty_graph_is_sha256_of_the_empty_array() =>
        Assert.Equal("sha256:4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945", new Graph().Digest());

    [Fact]
    public void Theorem_1_1_equality_iff_equal_encoding()
    {
        for (var s = 0; s < 3000; s++)
        {
            var g = new Gen(s);
            var (a, b) = (g.Value(), g.Value());
            Assert.Equal(a.Equals(b), Jcs.Encode(a) == Jcs.Encode(b));
        }
    }

    /// <summary>Two catalogs that never saw each other allocate the same id for the same key, whatever else they resolved before; another tenant, domain or component gives another id.</summary>
    [Theory]
    [InlineData("lab", "d:m/identity:pod", "a", "b", true)]
    [InlineData("other", "d:m/identity:pod", "a", "b", false)]
    [InlineData("lab", "d:m/identity:node", "a", "b", false)]
    [InlineData("lab", "d:m/identity:pod", "a", "c", false)]
    [InlineData("lab", "d:m/identity:pod", "ab", "", false)]       // the components are separate: (a, b) is not (ab, "")
    public void Theorem_1_4_allocation_is_a_pure_function_of_the_key(string tenant, string domain, string first, string second, bool same)
    {
        static string Allocate(string tenant, string domain, string[] components, params string[] resolvedBefore)
        {
            var interner = new Sattle.Runtime.Interner(); foreach (var s in resolvedBefore) interner.Intern(s);
            var catalog = new Sattle.Runtime.IdentityCatalog(tenant, interner);
            var d = catalog.Domain(domain, "x", ["one", "two"], ["Exact", "Exact"], null);
            foreach (var s in resolvedBefore) catalog.Resolve(d, [interner.Intern(s), interner.Intern(s)]);
            return interner.Get(catalog.Resolve(d, [.. components.Select(c => (long)interner.Intern(c))]));
        }
        var reference = Allocate("lab", "d:m/identity:pod", ["a", "b"]);
        Assert.Equal(same, reference == Allocate(tenant, domain, [first, second], "zzz", "b", "a"));
    }
}
