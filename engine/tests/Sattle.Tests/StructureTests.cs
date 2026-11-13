using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The four structures of the core (core step 1): segments, the stored values, the table and its arrangements, each against a model built on `Dictionary` under generated operations. Inserting, retracting, a weight of zero removing the row and the reuse of its number; iteration order is not compared.</summary>
public class StructureTests
{
    [Theory]
    [InlineData(1, 100)] [InlineData(2, 70_000)] [InlineData(3, 200_000)]
    public void Segments_hold_what_was_set_and_nothing_that_was_cleared(int seed, int range)
    {
        var rnd = new Random(seed);
        var s = new Segments<long>(); var model = new Dictionary<int, long>();
        for (var step = 0; step < 20_000; step++)
        {
            var i = rnd.Next(range);
            if (rnd.Next(4) == 0) { s.Clear(i); model.Remove(i); }
            else { var v = rnd.NextInt64(); s[i] = v; model[i] = v; }
        }
        foreach (var (i, v) in model) { Assert.True(s.Has(i)); Assert.Equal(v, s[i]); }
        for (var k = 0; k < 2_000; k++) { var i = rnd.Next(range); Assert.Equal(model.ContainsKey(i), s.Has(i)); }
        Assert.True(range > Segments<long>.Size || s.Capacity <= Segments<long>.Size, "a column within one segment stays one segment");
        s.Release();
        Assert.False(s.Has(0));
        Assert.Equal(64, s.Capacity);
    }

    /// <summary>Values are kept once and counted: a value has one code while it is held, a released value is gone and its code may name another, and when nothing is held nothing is kept. Against a model that counts uses per value, under generated holds and releases, with values longer than a page among them.</summary>
    [Theory]
    [InlineData(1, 50)] [InlineData(2, 2_000)] [InlineData(3, 20_000)]
    public void Values_are_kept_once_while_they_are_held_and_gone_when_released(int seed, int distinct)
    {
        var rnd = new Random(seed);
        var values = new Values(); var model = new Dictionary<string, (int Code, int Uses)>(StringComparer.Ordinal);
        byte[] Bytes(string v) => System.Text.Encoding.UTF8.GetBytes(v);
        string Value() => rnd.Next(2_000) == 0 ? new string('x', 70_000 + rnd.Next(10)) : "ü-" + rnd.Next(distinct) + (rnd.Next(2) == 0 ? "-日本" : "");
        for (var step = 0; step < 6 * distinct; step++)
        {
            if (model.Count > 0 && rnd.Next(5) < 2)
            {
                var (v, held) = model.ElementAt(rnd.Next(model.Count));
                values.Release(held.Code);
                if (held.Uses == 1) model.Remove(v); else model[v] = (held.Code, held.Uses - 1);
            }
            else
            {
                var v = Value(); var code = values.Hold(Bytes(v));
                if (model.TryGetValue(v, out var held)) { Assert.Equal(held.Code, code); model[v] = (code, held.Uses + 1); }
                else { Assert.DoesNotContain(model.Values, x => x.Code == code); model[v] = (code, 1); }        // a new value never takes the code of one that is held
            }
        }
        Assert.Equal(model.Count, values.Count);
        Assert.Equal(model.Keys.Sum(v => (long)Bytes(v).Length), values.HeldBytes);
        foreach (var (v, held) in model) { Assert.Equal(held.Code, values.Find(Bytes(v))); Assert.True(values.Bytes(held.Code).SequenceEqual(Bytes(v))); }
        Assert.Equal(-1, values.Find("not a value"u8));
        foreach (var (_, held) in model) for (var i = 0; i < held.Uses; i++) values.Release(held.Code);
        Assert.Equal((0, 0L), (values.Count, values.HeldBytes));
        Assert.True(values.Footprint() < 200_000, $"{values.Footprint():N0} bytes kept when nothing is held");
    }

    /// <summary>Two key columns (one 4 bytes, one 8) and two value columns; small key spaces so rows come and go and their numbers are reused. An arrangement by the first key column, made before the rows and made after them, gives the rows of each value.</summary>
    [Theory]
    [InlineData(1, 4, 8)] [InlineData(2, 8, 4)] [InlineData(3, 8, 8)] [InlineData(4, 4, 4)]
    public void A_table_keeps_the_weights_of_a_z_set_and_removes_a_row_at_weight_zero(int seed, int keyWidth, int valueWidth)
    {
        var rnd = new Random(seed);
        var t = new Table([keyWidth, 8, valueWidth, 8], keyColumns: 2);
        var byFirst = t.Arrange(0);
        var model = new Dictionary<(long, long), (long W, long V)>();
        for (var step = 0; step < 50_000; step++)
        {
            var key = (rnd.Next(-40, 40), rnd.NextInt64(-30, 30));
            var w = rnd.Next(-2, 3);
            var row = t.Add([key.Item1, key.Item2], w);
            var before = model.GetValueOrDefault(key);
            var now = before.W + w;
            if (now == 0) { model.Remove(key); Assert.Equal(-1, row); continue; }
            Assert.True(row >= 0);
            if (before.W == 0) { Assert.Equal(0, t.Get(row, 2)); before.V = 0; }                // a new row, possibly a reused number, starts with empty values
            var v = before.V;
            if (rnd.Next(3) == 0) { v = rnd.Next(-1000, 1000); t.Set(row, 2, v); }
            model[key] = (now, v);
        }
        Assert.Equal(model.Count, t.Count);
        Assert.Equal(model.Keys.Order(), t.Rows().Select(r => (t.Get(r, 0), t.Get(r, 1))).Order());
        foreach (var (key, (w, v)) in model)
        {
            var r = t.Find([key.Item1, key.Item2]);
            Assert.Equal(w, t.Weight(r)); Assert.Equal(v, t.Get(r, 2));
        }
        Assert.Equal(-1, t.Find([1000, 1000]));
        // the arrangement by the first key column, kept up to date, and one built afterwards, equal the model grouped the same way
        var later = t.Arrange(0);
        var groups = model.Keys.GroupBy(k => k.Item1).ToDictionary(g => g.Key, g => g.Select(k => k.Item2).Order().ToList());
        Assert.Equal(groups.Count, byFirst.Values);
        Assert.Equal(groups.Count, later.Values);
        for (var a = -41; a <= 40; a++)
        {
            var expected = groups.GetValueOrDefault(a) ?? [];
            Assert.Equal(expected, byFirst.Rows([a]).Select(r => t.Get(r, 1)).Order());
            Assert.Equal(expected, later.Rows([a]).Select(r => t.Get(r, 1)).Order());
        }
    }

    /// <summary>Code review 2: keys that differ only in their high bits share no chain of the index. Before, 60,000 keys that are multiples of 65,536 took 32.5 s, sequential ones 13 ms.</summary>
    [Fact]
    public void An_index_takes_keys_that_are_multiples_of_65536_as_fast_as_sequential_ones()
    {
        static TimeSpan Fill(Func<int, long> key)
        {
            var t = new Table([8], keyColumns: 1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 60_000; i++) t.Add([key(i)], 1);
            return sw.Elapsed;
        }
        Fill(i => i);
        var sequential = Fill(i => i); var shifted = Fill(i => (long)i << 16);
        Assert.True(shifted < sequential * 20 + TimeSpan.FromMilliseconds(200), $"sequential {sequential.TotalMilliseconds:F0} ms, multiples of 65,536 {shifted.TotalMilliseconds:F0} ms");
    }

    /// <summary>Code review 2: the rows of a value are given as they are when asked, so a caller may remove them and add others while it goes through them. Before, the walk followed the removed row into the rows of another value.</summary>
    [Fact]
    public void The_rows_of_a_value_stay_rows_of_that_value_while_the_caller_removes_and_adds()
    {
        var t = new Table([4, 4], keyColumns: 2); var byFirst = t.Arrange(0);
        t.Add([2, 0], 1); t.Add([1, 0], 1); t.Add([1, 1], 1);
        var seen = new List<(long, long)>();
        foreach (var r in byFirst.Rows([1]))
        {
            seen.Add((t.Get(r, 0), t.Get(r, 1)));
            t.Add([1, t.Get(r, 1)], -1);
            t.Add([2, 10 + seen.Count], 1);
        }
        Assert.Equal(new[] { (1L, 0L), (1L, 1L) }, seen.Order());
    }

    /// <summary>Code review 2: a value longer than a page is a page of its own and leaves the page being filled as it is. Before, each such value left a fresh page of 65,536 bytes behind it.</summary>
    [Fact]
    public void A_value_longer_than_a_page_costs_its_own_bytes_and_no_page_beside_them()
    {
        var d = new Values(); var before = d.Footprint();
        var v = new byte[70_000]; var codes = new List<int>();
        for (var i = 0; i < 100; i++) { v[0] = (byte)i; codes.Add(d.Hold(v)); }
        Assert.Equal(100, codes.Distinct().Count());
        var small = d.Hold("short"u8);
        for (var i = 0; i < 100; i++) { v[0] = (byte)i; Assert.Equal(codes[i], d.Find(v)); }
        Assert.True(d.Bytes(small).SequenceEqual("short"u8));
        Assert.True(d.Footprint() - before < 100 * 80_000, $"{(d.Footprint() - before) / 100:N0} bytes per value of 70,000 bytes");
    }

    /// <summary>Code review 2, defect 21: a relationship at a node with many relationships costs what one between two fresh nodes costs. Before, each new relationship walked every relationship of its endpoint: 8,000 at one node took 4.95 s against 0.03 s spread out.</summary>
    [Fact]
    public void A_relationship_at_a_hub_costs_as_much_as_one_at_its_own_endpoints()
    {
        static double Seconds(int n, bool hub)
        {
            var it = new Interner(); var sink = new OutputBuilder(it);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < n; i++) sink.Contribute(it.Intern("r:" + i), true, 0, false, "T", hub ? "e:hub" : "s:" + i, "e:" + i, Props.Empty, 1);
            for (var i = 0; i < n; i++) sink.Contribute(it.Intern("r:" + i), true, 0, false, "T", hub ? "e:hub" : "s:" + i, "e:" + i, Props.Empty, -1);
            return sw.Elapsed.TotalSeconds;
        }
        Seconds(1_000, true);
        var spread = Seconds(8_000, false); var one = Seconds(8_000, true);
        Assert.True(one < 20 * Math.Max(spread, 0.05), $"{spread:F2} s at distinct endpoints, {one:F2} s at one endpoint");
    }

    /// <summary>Code review 2, defect 20: what a sink keeps about an element is compared as a value, so a number retracted at another scale (2.5 and 2.50) cancels what it was added with. Before, the two were two entries, one at +1 and one at -1, and the element stayed.</summary>
    [Theory]
    [InlineData("2.5", "2.50")] [InlineData("0", "0.000")] [InlineData("-1.10", "-1.1")] [InlineData("100", "100.0000000000")] [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    public void A_sink_cancels_a_contribution_with_its_retraction_written_at_another_decimal_scale(string added, string retracted)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var it = new Interner(); var sink = new OutputBuilder(it); var g = new Graph(); var e = it.Intern("e:x");
        sink.Contribute(e, false, 0, false, "T", null, null, Props.Of(("d", Value.Decimal(decimal.Parse(added, inv)))), 1); g.Apply(sink.Flush(g));
        Assert.NotNull(g.Entity("e:x"));
        Assert.Equal((1, 1), (sink.Held.Contributions, sink.Held.Fragments));
        sink.Contribute(e, false, 0, false, "T", null, null, Props.Of(("d", Value.Decimal(decimal.Parse(retracted, inv)))), -1); g.Apply(sink.Flush(g));
        Assert.Null(g.Entity("e:x"));
        Assert.Equal((0, 0, 0L), sink.Held);
    }

    /// <summary>Code review 2: a sink keeps a value only while a row uses it. One element whose property takes 20,000 values in turn holds one value at the end, and 20,000 elements that came and went hold nothing. Before, every value ever written stayed.</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void A_sink_holds_a_value_only_while_a_row_uses_it(bool manyElements)
    {
        var it = new Interner(); var sink = new OutputBuilder(it); var g = new Graph();
        Props P(int i) => Props.Of(("n", Value.Integer(i)), ("t", Value.Text("event " + i)));
        if (manyElements)
        {
            foreach (var w in new long[] { 1, -1 })
            {
                for (var i = 0; i < 20_000; i++) sink.Contribute(it.Intern("e:" + i), false, 0, false, "T", null, null, P(i), w);
                g.Apply(sink.Flush(g));
                Assert.Equal(w > 0 ? (20_000, 20_000) : (0, 0), (sink.Held.Contributions, sink.Held.Fragments));
            }
            Assert.Equal(0, g.EntityCount);
            Assert.Equal(0L, sink.Held.FragmentBytes);
        }
        else
        {
            var e = it.Intern("e:x");
            for (var v = 0; v < 20_000; v++)
            {
                if (v > 0) sink.Contribute(e, false, 0, false, "T", null, null, P(v - 1), -1);
                sink.Contribute(e, false, 0, false, "T", null, null, P(v), 1);
                Assert.Null(g.Apply(sink.Flush(g)));
            }
            Assert.Equal((1, 1), (sink.Held.Contributions, sink.Held.Fragments));
            Assert.InRange(sink.Held.FragmentBytes, 1, 64);
            Assert.Equal(new IntegerValue(19_999), g.Entity("e:x")!.Props.Get("n"));
        }
    }

    /// <summary>Code review 2: a row that is not there has weight 0, also the -1 a `Find` gives.</summary>
    [Fact]
    public void The_weight_of_no_row_is_zero()
    {
        var t = new Table([8], keyColumns: 1);
        Assert.Equal(0, t.Weight(t.Find([5])));
        Assert.False(t.Live(t.Add([5], 0)));
    }

    [Fact]
    public void A_table_refuses_a_column_width_other_than_4_or_8_an_arrangement_by_a_column_that_is_no_key_and_setting_a_key_column()
    {
        Assert.Throws<ArgumentException>(() => new Table([2], 1));
        Assert.Throws<ArgumentException>(() => new Table([8, 8], 1).Arrange(1));          // a value column changes under `Set`: an arrangement by it would keep the row under the old value
        var t = new Table([8, 8], 1);
        var r = t.Add([5], 1);
        Assert.Throws<InvalidOperationException>(() => t.Set(r, 0, 6));
    }
}
