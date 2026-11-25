using System.Globalization;
using Sattle.Core;
using Sattle.Language;
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

    [Theory]
    [InlineData(1, 100)] [InlineData(2, 70_000)] [InlineData(3, 200_000)]
    public void Bits_hold_what_was_added_and_nothing_that_was_removed(int seed, int range)
    {
        var rnd = new Random(seed);
        var s = new Bits(); var model = new HashSet<int>();
        for (var step = 0; step < 20_000; step++)
        {
            var i = rnd.Next(range);
            if (rnd.Next(4) == 0) { s.Remove(i); model.Remove(i); }
            else Assert.Equal(model.Add(i), s.Add(i));
        }
        for (var i = 0; i < range + 70_000; i++) Assert.Equal(model.Contains(i), s.Has(i));
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

    /// <summary>Two key columns (one 4 bytes, one 8) and two value columns; small key spaces so rows come and go and their numbers are reused. An arrangement by the first key column, made before the rows and made after them, gives the rows of each value. With `large`, from the middle on the 8-byte columns get values that need 8 bytes, so a column held in 4 bytes per entry is copied to 8 while it has rows.</summary>
    [Theory]
    [InlineData(1, 4, 8, false)] [InlineData(2, 8, 4, false)] [InlineData(3, 8, 8, false)] [InlineData(4, 4, 4, false)] [InlineData(5, 4, 8, true)] [InlineData(6, 8, 8, true)]
    public void A_table_keeps_the_weights_of_a_z_set_and_removes_a_row_at_weight_zero(int seed, int keyWidth, int valueWidth, bool large)
    {
        var rnd = new Random(seed);
        var t = new Table([keyWidth, 8, valueWidth, 8], keyColumns: 2);
        var byFirst = t.Arrange(0);
        var model = new Dictionary<(long, long), (long W, long V)>();
        for (var step = 0; step < 50_000; step++)
        {
            var wide = large && step >= 25_000;
            var key = (rnd.Next(-40, 40), rnd.NextInt64(-30, 30) << (wide && rnd.Next(2) == 0 ? 40 : 0));
            var w = rnd.Next(-2, 3);
            var row = t.Add([key.Item1, key.Item2], w);
            var before = model.GetValueOrDefault(key);
            var now = before.W + w;
            if (now == 0) { model.Remove(key); Assert.Equal(-1, row); continue; }
            Assert.True(row >= 0);
            if (before.W == 0) { Assert.Equal(0, t.Get(row, 2)); before.V = 0; }                // a new row, possibly a reused number, starts with empty values
            var v = before.V;
            if (rnd.Next(3) == 0) { v = wide && valueWidth == 8 ? rnd.NextInt64(long.MinValue, long.MaxValue) : rnd.Next(-1000, 1000); t.Set(row, 2, v); }
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

    [Theory]
    [InlineData(1, 4)] [InlineData(2, 8)] [InlineData(3, 8)]
    public void A_table_without_key_columns_keeps_its_rows_by_number_and_removes_one_at_weight_zero(int seed, int width)
    {
        var rnd = new Random(seed);
        var t = new Table([width, 4], keyColumns: 0);
        var model = new Dictionary<int, (long W, long V)>();
        for (var step = 0; step < 50_000; step++)
        {
            if (model.Count == 0 || rnd.Next(3) == 0)
            {
                var w = rnd.Next(1, 3) * (rnd.Next(2) == 0 ? 1 : -1);
                var v = width == 8 && seed == 3 ? rnd.NextInt64(long.MinValue, long.MaxValue) : rnd.Next(-1000, 1000);
                var row = t.Insert(w); t.Set(row, 0, v); t.Set(row, 1, step);
                Assert.False(model.ContainsKey(row));
                model[row] = (w, v);
                continue;
            }
            var r = model.Keys.ElementAt(rnd.Next(model.Count));
            var dw = rnd.Next(-2, 3);
            var now = model[r].W + dw;
            Assert.Equal(now == 0 ? -1 : r, t.AddAt(r, dw));
            if (now == 0) model.Remove(r); else model[r] = (now, model[r].V);
        }
        Assert.Equal(model.Count, t.Count);
        Assert.Equal(model.Keys.Order(), t.Rows().Order());
        foreach (var (r, (w, v)) in model) { Assert.Equal(w, t.Weight(r)); Assert.Equal(v, t.Get(r, 0)); }
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

    /// <summary>#115: an arrangement whose values are one row each keeps its index and no links, also after rows come and go. Before, every linked row wrote a next and a previous row number of 0, 8 bytes per row.</summary>
    [Fact]
    public void An_arrangement_of_one_row_per_value_keeps_no_links()
    {
        var t = new Table([4, 4], keyColumns: 2); var byFirst = t.Arrange(0);
        for (var i = 0; i < 20_000; i++) t.Add([i, i], 1);
        for (var i = 0; i < 20_000; i += 2) { t.Add([i, i], -1); t.Add([i, i + 1], 1); }
        Assert.Equal(20_000, byFirst.Values);
        for (var i = 0; i < 20_000; i++) Assert.Equal(i % 2 == 0 ? i + 1L : i, t.Get(byFirst.First([i]), 1));
        Assert.True(byFirst.Footprint() <= 65_536 * 4 + 1_000, $"{byFirst.Footprint():N0} bytes");
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

    /// <summary>#115: a merge finds every relationship at the loser, whether its graph holds it there (the sink keeps no endpoint row for it then, the graph's adjacency has it) or not: held back by another with the same (source, target, type), only updated, or with its create taken back.</summary>
    [Theory]
    [InlineData("held")] [InlineData("heldBack")] [InlineData("updateOnly")] [InlineData("createRetracted")]
    public void A_merge_moves_a_relationship_at_the_loser_whether_or_not_its_graph_holds_it(string state)
    {
        var it = new Interner(); var sink = new OutputBuilder(it); var g = new Graph(it);
        sink.RegisterRelSink(0, (s, t) => $"r:{s}>{t}");
        const string id = "r:e:b>e:c";
        void Rel(string rel, bool update, long w) => sink.Contribute(it.Intern(rel), true, 0, update, "T", "e:b", "e:c", update ? Props.Of(("p", Value.Integer(1))) : Props.Empty, w);
        switch (state)
        {
            case "held": Rel(id, false, 1); break;
            case "heldBack": Rel("r:0", false, 1); Rel(id, false, 1); break;                       // the smaller id holds the tuple
            case "updateOnly": Rel(id, true, 1); break;
            case "createRetracted": Rel(id, false, 1); Rel(id, true, 1); g.Apply(sink.Flush(g)); Rel(id, false, -1); break;
        }
        g.Apply(sink.Flush(g));
        Assert.Equal(state == "held", g.Rel(id) is not null);
        Assert.Contains((it.Intern(id), it.Intern("r:e:a>e:c")), sink.Remap(it.Intern("e:b"), it.Intern("e:a"), g));
    }

    /// <summary>#115: an element whose one row is a create its graph holds keeps only its sink and weight, from the flush after the one that wrote it. Whatever arrives next for it, a retraction, the same row again, an update, another create, a retraction at another decimal scale, a merge or another relationship that claims its (source, target, type) for a while, the graph and what the sink holds come out as when every row arrives in one flush.</summary>
    [Theory]
    [InlineData("retract")] [InlineData("reemit")] [InlineData("update")] [InlineData("otherCreate")] [InlineData("retractAtOtherScale")] [InlineData("merge")] [InlineData("claimedByAnother")]
    public void An_element_kept_as_its_graph_holds_it_resolves_as_if_its_rows_were_kept(string next)
    {
        (Graph Graph, (int, int, long) Held) Run(bool flushBetween)
        {
            var it = new Interner(); var sink = new OutputBuilder(it); var g = new Graph(it);
            sink.RegisterRelSink(0, (s, t) => $"r:{s}>{t}");
            Props P(string key, string d) => Props.Of((key, Value.Decimal(decimal.Parse(d, System.Globalization.CultureInfo.InvariantCulture))), ("n", Value.Text("v")));
            void E(int sinkNo, bool update, Props props, long w) => sink.Contribute(it.Intern("e:b"), false, sinkNo, update, "T", null, null, props, w);
            void R(long w) => sink.Contribute(it.Intern("r:e:b>e:c"), true, 0, false, "L", "e:b", "e:c", P("d", "1"), w);
            void C(long w) => sink.Contribute(it.Intern("r:0"), true, 0, false, "L", "e:b", "e:c", P("d", "2"), w);
            E(0, false, P("d", "2.50"), 1); R(1);
            if (flushBetween) { g.Apply(sink.Flush(g)); g.Apply(sink.Flush(g)); Assert.Equal((2, 0, 0L), sink.Held); }
            switch (next)
            {
                case "retract": E(0, false, P("d", "2.50"), -1); R(-1); break;
                case "reemit": E(0, false, P("d", "2.50"), -1); E(0, false, P("d", "2.50"), 1); R(-1); R(1); break;
                case "update": E(1, true, P("u", "7"), 1); break;
                case "otherCreate": E(1, false, P("d", "3"), 1); break;
                case "retractAtOtherScale": E(0, false, P("d", "2.5"), -1); break;
                case "merge": sink.Remap(it.Intern("e:b"), it.Intern("e:a"), g); break;
                case "claimedByAnother": C(1); g.Apply(sink.Flush(g)); C(-1); break;          // a smaller id holds the tuple, then lets it go
            }
            g.Apply(sink.Flush(g)); g.Apply(sink.Flush(g));
            return (g, sink.Held);
        }
        var kept = Run(true); var all = Run(false);
        Assert.True(kept.Graph.SameAs(all.Graph), $"{kept.Graph.Encode()}\n{all.Graph.Encode()}");
        Assert.Equal(all.Held, kept.Held);
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

    /// <summary>#110: an emit writes its fragment straight from its columns, with the bytes of the same properties given as a map, so a row written one way is cancelled by its retraction written the other way, and written both ways it is one fragment. Every kind of cell an emit reads, an absent one, and properties named out of order; for an entity and a relationship.</summary>
    [Theory]
    [InlineData(Tag.Text, "Main Street")] [InlineData(Tag.Text, "")] [InlineData(Tag.Integer, "-42")] [InlineData(Tag.Integer, "9223372036854775807")]
    [InlineData(Tag.Decimal, "2.5")] [InlineData(Tag.Decimal, "2.50")] [InlineData(Tag.Decimal, "-1.10")] [InlineData(Tag.Decimal, "0.000")] [InlineData(Tag.Decimal, "79228162514264337593543950335")] [InlineData(Tag.Decimal, "0.0000000000000000000000000001")]
    [InlineData(Tag.Boolean, "true")] [InlineData(Tag.Boolean, "false")] [InlineData(Tag.Instant, "-1")] [InlineData(Tag.Instant, "1762784742000000000")] [InlineData(Tag.Ent, "e:other")] [InlineData(Tag.Text, null)]
    public void An_emit_row_written_from_its_columns_is_the_fragment_of_its_property_map(Tag tag, string? cell)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var rel in new[] { false, true })
        {
            var it = new Interner(); var sink = new OutputBuilder(it);
            Value v = tag switch
            {
                Tag.Text => Value.Text(cell ?? ""), Tag.Integer => Value.Integer(long.Parse(cell!, inv)), Tag.Decimal => Value.Decimal(decimal.Parse(cell!, inv)),
                Tag.Boolean => Value.Boolean(bool.Parse(cell!)), Tag.Instant => Value.Instant(long.Parse(cell!, inv)), _ => Value.Entity(cell!),
            };
            var col = new Column(tag == Tag.Decimal); col.Ensure(1);
            if (cell is null) col.SetAbsent(0, 1); else Codec.Set(col, 0, v, it);
            var other = new Column(false); other.Ensure(1); Codec.Set(other, 0, Value.Integer(7), it);
            Column[] cols = [other, col];
            var shape = new FragmentShape(it, 3, rel, false, "T", [("z", 0, Tag.Integer), ("a", 1, tag)]);
            var props = cell is null ? Props.Of(("z", Value.Integer(7))) : Props.Of(("z", Value.Integer(7)), ("a", v));
            var (e, s, d) = (it.Intern("e:x"), rel ? it.Intern("e:s") : -1, rel ? it.Intern("e:t") : -1);
            var (sn, tn) = rel ? ("e:s", "e:t") : ((string?)null, (string?)null);

            Assert.True(sink.Contribute(shape, e, s, d, cols, 0, 1));
            sink.Contribute(e, rel, 3, false, "T", sn, tn, props, -1);
            Assert.Equal((0, 0, 0L), sink.Held);
            Assert.True(sink.Contribute(shape, e, s, d, cols, 0, 1));
            sink.Contribute(e, rel, 3, false, "T", sn, tn, props, 1);
            Assert.Equal((1, 1), (sink.Held.Contributions, sink.Held.Fragments));
            if (cell is null) Assert.False(sink.Contribute(new FragmentShape(it, 3, rel, true, "T", [("a", 1, tag)]), e, s, d, cols, 0, 1));      // an update that sets nothing says nothing
            Assert.Equal((1, 1), (sink.Held.Contributions, sink.Held.Fragments));
        }
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

    /// <summary>#175 A6: a decimal identity component is cached by its value as integers and instants are by their number: a value at another scale is the same key,
    /// another value is not, and a hit allocates nothing.</summary>
    [Theory]
    [InlineData("1.5", "1.50", true)]
    [InlineData("1.5", "1.5", true)]
    [InlineData("0", "0.000", true)]
    [InlineData("1.5", "1.6", false)]
    [InlineData("-2.25", "2.25", false)]
    public void A_code_cache_finds_a_decimal_by_its_value(string set, string get, bool hit)
    {
        var c = new CodeCache();
        decimal s = decimal.Parse(set, CultureInfo.InvariantCulture), g = decimal.Parse(get, CultureInfo.InvariantCulture);
        Assert.False(c.TryGet(g, out _));
        c.Set(s, 7);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var found = c.TryGet(g, out var code);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Equal(hit, found);
        if (hit) Assert.Equal(7, code);
        Assert.False(c.TryGet((long)s, out _));          // a decimal key and an integer key are apart
    }
}
