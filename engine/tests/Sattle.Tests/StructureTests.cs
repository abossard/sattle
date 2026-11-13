using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The four structures of the core (core step 1): segments, the code dictionary, the table and its arrangements, each against a model built on `Dictionary` under generated operations. Inserting, retracting, a weight of zero removing the row and the reuse of its number; iteration order is not compared.</summary>
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

    [Theory]
    [InlineData(1, 1_000)] [InlineData(2, 50_000)]
    public void The_code_dictionary_gives_one_dense_code_per_value_in_order_of_first_appearance(int seed, int values)
    {
        var rnd = new Random(seed);
        var d = new CodeDictionary(); var model = new Dictionary<string, int>(StringComparer.Ordinal);
        string Value() => rnd.Next(1_000) == 0 ? new string('x', 70_000 + rnd.Next(10)) : "ü-" + rnd.Next(values) + (rnd.Next(2) == 0 ? "-日本" : "");
        for (var step = 0; step < 3 * values; step++)
        {
            var v = Value();
            if (!model.TryGetValue(v, out var expected)) model[v] = expected = model.Count;
            Assert.Equal(expected, d.Code(v));
        }
        Assert.Equal(model.Count, d.Count);
        foreach (var (v, c) in model) { Assert.Equal(v, d.Text(c)); Assert.True(d.TryFind(System.Text.Encoding.UTF8.GetBytes(v), out var f)); Assert.Equal(c, f); }
        Assert.False(d.TryFind("not a value"u8, out _));
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
        var d = new CodeDictionary(); var before = d.Footprint();
        var v = new byte[70_000];
        for (var i = 0; i < 100; i++) { v[0] = (byte)i; Assert.Equal(i, d.Code(v)); }
        Assert.Equal(100, d.Code("short"u8));
        for (var i = 0; i < 100; i++) { v[0] = (byte)i; Assert.Equal(i, d.Code(v)); }
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
            for (var i = 0; i < n; i++) sink.Contribute(it.Intern("r:" + i), true, 0, false, "T", hub ? "e:hub" : "s:" + i, "e:" + i, Sattle.Core.Props.Empty, 1);
            for (var i = 0; i < n; i++) sink.Contribute(it.Intern("r:" + i), true, 0, false, "T", hub ? "e:hub" : "s:" + i, "e:" + i, Sattle.Core.Props.Empty, -1);
            return sw.Elapsed.TotalSeconds;
        }
        Seconds(1_000, true);
        var spread = Seconds(8_000, false); var one = Seconds(8_000, true);
        Assert.True(one < 20 * Math.Max(spread, 0.05), $"{spread:F2} s at distinct endpoints, {one:F2} s at one endpoint");
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
