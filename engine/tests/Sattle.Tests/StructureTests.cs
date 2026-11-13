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

    /// <summary>Two key columns (one 4 bytes, one 8) and two value columns; small key spaces so rows come and go and their numbers are reused. With an arranged prefix the table finds its rows through the arrangement by the first column and keeps no index of its own.</summary>
    [Theory]
    [InlineData(1, 4, 8, 0)] [InlineData(2, 8, 4, 0)] [InlineData(3, 8, 8, 0)] [InlineData(4, 4, 4, 0)]
    [InlineData(5, 4, 8, 1)] [InlineData(6, 8, 4, 1)] [InlineData(7, 4, 4, 2)]
    public void A_table_keeps_the_weights_of_a_z_set_and_removes_a_row_at_weight_zero(int seed, int keyWidth, int valueWidth, int arrangedPrefix)
    {
        var rnd = new Random(seed);
        var t = new Table([keyWidth, 8, valueWidth, 8], keyColumns: 2, arrangedPrefix);
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

    [Fact]
    public void A_table_refuses_a_column_width_other_than_4_or_8_an_arranged_prefix_longer_than_its_key_and_setting_a_key_column()
    {
        Assert.Throws<ArgumentException>(() => new Table([2], 1));
        Assert.Throws<ArgumentException>(() => new Table([8, 8], 1, arrangedPrefix: 2));
        var t = new Table([8, 8], 1);
        var r = t.Add([5], 1);
        Assert.Throws<InvalidOperationException>(() => t.Set(r, 0, 6));
    }
}
