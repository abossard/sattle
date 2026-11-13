using System.Collections.Concurrent;
using System.Text;

namespace Sattle.Runtime;

/// <summary>A fixed-width column in segments of 65,536 entries, with one presence bit per entry. The first segment grows by doubling up to the full size, so a small column stays small; every later segment is full size and comes from a shared pool, and goes back to it when the column is released. The only allocation unit of operator state, graph tables and dictionaries.</summary>
public sealed class Segments<T> where T : unmanaged
{
    public const int Shift = 16, Size = 1 << Shift, Mask = Size - 1;
    static readonly ConcurrentBag<T[]> Pool = [];
    const int PoolCap = 64;

    T[][] data = [new T[64]];
    ulong[][] present = [new ulong[1]];

    /// <summary>The number of entries the column can hold without growing.</summary>
    public int Capacity => data.Length == 1 ? data[0].Length : data.Length * Size;

    public T this[int i]
    {
        get => i < Capacity ? data[i >> Shift][i & Mask] : default;
        set { Ensure(i); data[i >> Shift][i & Mask] = value; present[i >> Shift][(i & Mask) >> 6] |= 1UL << (i & 63); }
    }

    public bool Has(int i) => i < Capacity && (present[i >> Shift][(i & Mask) >> 6] & (1UL << (i & 63))) != 0;

    /// <summary>Makes entry `i` absent; its value reads as the default.</summary>
    public void Clear(int i)
    {
        if (i >= Capacity) return;
        data[i >> Shift][i & Mask] = default;
        present[i >> Shift][(i & Mask) >> 6] &= ~(1UL << (i & 63));
    }

    public void Ensure(int i)
    {
        if (i < Capacity) return;
        if (data.Length == 1 && i < Size)
        {
            var n = data[0].Length; while (n <= i) n *= 2;
            Array.Resize(ref data[0], n); Array.Resize(ref present[0], (n + 63) >> 6);
            return;
        }
        if (data[0].Length < Size) { Array.Resize(ref data[0], Size); Array.Resize(ref present[0], Size >> 6); }
        var segments = (i >> Shift) + 1;
        var old = data.Length;
        Array.Resize(ref data, segments); Array.Resize(ref present, segments);
        for (var s = old; s < segments; s++) { data[s] = Pool.TryTake(out var a) ? a : new T[Size]; present[s] = new ulong[Size >> 6]; }
    }

    /// <summary>Gives the full segments back to the pool and empties the column.</summary>
    public void Release()
    {
        for (var s = 1; s < data.Length; s++) if (Pool.Count < PoolCap) { Array.Clear(data[s]); Pool.Add(data[s]); }
        data = [new T[64]]; present = [new ulong[1]];
    }

    /// <summary>Bytes held: values and presence bits.</summary>
    public long Footprint => (long)Capacity * System.Runtime.CompilerServices.Unsafe.SizeOf<T>() + Capacity / 8;
}

/// <summary>Stored values of variable length, each kept once and counted. A value is the bytes the caller gives, so the caller writes equal values with equal bytes (a number without its scale, a text as its number). `Hold` finds or stores a value and counts one more use; `Release` counts one less, and at zero the value is gone: its code is free for another value and its bytes are cleared. When more bytes are free than in use, the values in use are copied together. A code is valid while the value is held.</summary>
public sealed class Values
{
    const int Page = 1 << 16;
    List<byte[]> pages = [new byte[Page]];
    int filling, used;                                 // the page that takes the next short value, and how much of it is used
    readonly Segments<long> start = new();             // page << 32 | offset
    readonly Segments<int> length = new(), uses = new(), freeCodes = new();
    int codes, free;                                   // codes ever given, and how many of them are free now
    int[] index = new int[64]; int mask = 63;
    long liveBytes, deadBytes;

    /// <summary>The values held.</summary>
    public int Count => codes - free;
    /// <summary>The bytes of the values held.</summary>
    public long HeldBytes => liveBytes;

    static int Hash(ReadOnlySpan<byte> v)
    {
        var h = (ulong)v.Length;
        for (; v.Length >= 8; v = v[8..]) h = Table.Mix(h, BitConverter.ToInt64(v));
        long tail = 0; foreach (var b in v) tail = tail << 8 | b;
        h = Table.Mix(h, tail);
        return (int)(h ^ h >> 32);
    }

    public ReadOnlySpan<byte> Bytes(int code) => pages[(int)(start[code] >> 32)].AsSpan((int)start[code], length[code]);

    /// <summary>The array that holds the bytes of `code` and where they start in it, for readers that take an array and an offset.</summary>
    public (byte[] Page, int Offset) Slice(int code) => (pages[(int)(start[code] >> 32)], (int)start[code]);

    /// <summary>The code of a value that is held, or -1.</summary>
    public int Find(ReadOnlySpan<byte> v)
    {
        for (var h = Hash(v) & mask; index[h] != 0; h = (h + 1) & mask)
            if (Bytes(index[h] - 1).SequenceEqual(v)) return index[h] - 1;
        return -1;
    }

    /// <summary>One more use of `v`: its code, stored now if it was not held.</summary>
    public int Hold(ReadOnlySpan<byte> v)
    {
        var code = Find(v);
        if (code >= 0) { uses[code]++; return code; }
        code = free > 0 ? freeCodes[--free] : codes++;
        Store(code, v);
        uses[code] = 1; liveBytes += v.Length;
        if (Count * 2 > index.Length) Rehash(); else Place(code);
        return code;
    }

    /// <summary>One more use of a value that is held.</summary>
    public void Hold(int code) => uses[code]++;

    /// <summary>One use less; at zero the value is gone.</summary>
    public void Release(int code)
    {
        if (--uses[code] > 0) return;
        Unplace(code);
        var bytes = pages[(int)(start[code] >> 32)].AsSpan((int)start[code], length[code]);
        bytes.Clear();
        liveBytes -= bytes.Length; deadBytes += bytes.Length;
        uses.Clear(code); length.Clear(code); start.Clear(code);
        freeCodes[free++] = code;
        if (Count == 0) Reset();
        else if (deadBytes > liveBytes + Page) Compact();
    }

    void Store(int code, ReadOnlySpan<byte> v)
    {
        if (v.Length > Page) { pages.Add(v.ToArray()); start[code] = (long)(pages.Count - 1) << 32; }       // a long value is a page of its own
        else
        {
            if (used + v.Length > Page) { pages.Add(new byte[Page]); filling = pages.Count - 1; used = 0; }
            v.CopyTo(pages[filling].AsSpan(used));
            start[code] = (long)filling << 32 | (uint)used;
            used += v.Length;
        }
        length[code] = v.Length;
    }

    void Reset()
    {
        pages = [new byte[Page]]; filling = 0; used = 0; codes = 0; free = 0; liveBytes = 0; deadBytes = 0;
        start.Release(); length.Release(); uses.Release(); freeCodes.Release();
        index = new int[64]; mask = 63;
    }

    /// <summary>Copies the values in use together into new pages; their codes stay.</summary>
    void Compact()
    {
        var old = pages; pages = [new byte[Page]]; filling = 0; used = 0; deadBytes = 0;
        for (var code = 0; code < codes; code++)
            if (uses[code] > 0) Store(code, old[(int)(start[code] >> 32)].AsSpan((int)start[code], length[code]));
    }

    void Place(int code)
    {
        var h = Hash(Bytes(code)) & mask;
        while (index[h] != 0) h = (h + 1) & mask;
        index[h] = code + 1;
    }

    void Unplace(int code)
    {
        var h = Hash(Bytes(code)) & mask;
        while (index[h] != code + 1) h = (h + 1) & mask;
        Table.Shift(index, mask, h, c => Hash(Bytes(c - 1)) & mask);
    }

    void Rehash()
    {
        index = new int[index.Length * 2]; mask = index.Length - 1;
        for (var c = 0; c < codes; c++) if (uses[c] > 0) Place(c);
    }

    /// <summary>Bytes of memory held: pages, the per-code columns and the index.</summary>
    public long Footprint() => pages.Sum(p => (long)p.Length) + start.Footprint + length.Footprint + uses.Footprint + freeCodes.Footprint + index.Length * 4L;
}

/// <summary>A Z-set of rows: fixed-width columns (4 or 8 bytes) in segments, one `i64` weight per row, and one open-addressing index on the key columns, which come first. Adding a weight to a key finds or creates its row; a row whose weight reaches zero is removed and its number is reused. Iteration order is not part of the contract. One shape: a row is always found through the index on its whole key. An `Arrangement` is a second way in, by some of the key columns, for the callers that want every row with one value.</summary>
public sealed class Table
{
    readonly int keys;
    readonly bool[] wide;
    readonly Segments<int>?[] narrowCols;
    readonly Segments<long>?[] wideCols;
    readonly Segments<long> weight = new();
    readonly Segments<int> freeRows = new();
    int free, rows;
    int[] index = new int[64]; int mask = 63;
    readonly List<Arrangement> arrangements = [];

    /// <summary>`widths` are 4 or 8 bytes per column; the first `keyColumns` columns identify a row.</summary>
    public Table(ReadOnlySpan<int> widths, int keyColumns)
    {
        keys = keyColumns;
        wide = new bool[widths.Length]; narrowCols = new Segments<int>[widths.Length]; wideCols = new Segments<long>[widths.Length];
        for (var c = 0; c < widths.Length; c++)
        {
            wide[c] = widths[c] switch { 4 => false, 8 => true, _ => throw new ArgumentException($"column width {widths[c]}: 4 or 8") };
            if (wide[c]) wideCols[c] = new(); else narrowCols[c] = new();
        }
    }

    public int Columns => wide.Length;
    /// <summary>Rows with a nonzero weight.</summary>
    public int Count { get; private set; }
    /// <summary>One past the highest row number in use; a row below it is live when its weight is nonzero.</summary>
    public int RowLimit => rows;

    public long Get(int row, int col) => wide[col] ? wideCols[col]![row] : narrowCols[col]![row];
    /// <summary>The weight of a row; 0 for a row that is not there (also for the -1 a `Find` or an `Add` gives).</summary>
    public long Weight(int row) => row >= 0 && row < rows ? weight[row] : 0;
    public bool Live(int row) => Weight(row) != 0;

    /// <summary>Sets a column that is not part of the key.</summary>
    public void Set(int row, int col, long v)
    {
        if (col < keys) throw new InvalidOperationException("a key column is set when its row is created");
        Put(row, col, v);
    }

    void Put(int row, int col, long v) { if (wide[col]) wideCols[col]![row] = v; else narrowCols[col]![row] = checked((int)v); }

    /// <summary>One step of the hash of a key: every bit of `x` reaches every bit of the result (keys that differ only in their high bits, like multiples of 65,536, must not share the low bits the index uses).</summary>
    internal static ulong Mix(ulong h, long x)
    {
        var z = h + (ulong)x + 0x9E3779B97F4A7C15UL;
        z = (z ^ z >> 30) * 0xBF58476D1CE4E5B9UL; z = (z ^ z >> 27) * 0x94D049BB133111EBUL;
        return z ^ z >> 31;
    }

    internal ulong HashOf(int row, ReadOnlySpan<int> cols) { var h = 0UL; foreach (var c in cols) h = Mix(h, Get(row, c)); return h; }

    internal static ulong HashOf(ReadOnlySpan<long> key) { var h = 0UL; foreach (var x in key) h = Mix(h, x); return h; }

    internal bool Equal(int row, ReadOnlySpan<int> cols, ReadOnlySpan<long> key)
    {
        for (var i = 0; i < key.Length; i++) if (Get(row, cols[i]) != key[i]) return false;
        return true;
    }

    int[] KeyCols => keyCols ??= [.. Enumerable.Range(0, keys)];
    int[]? keyCols;

    /// <summary>The row of `key`, or -1.</summary>
    public int Find(ReadOnlySpan<long> key)
    {
        for (var h = (int)(HashOf(key) & (ulong)mask); index[h] != 0; h = (h + 1) & mask)
            if (Equal(index[h] - 1, KeyCols, key)) return index[h] - 1;
        return -1;
    }

    /// <summary>Adds `w` to the weight of `key`'s row, creating it with weight `w` when absent. Returns the row, or -1 when its weight became zero and it was removed.</summary>
    public int Add(ReadOnlySpan<long> key, long w)
    {
        var row = Find(key);
        if (row < 0)
        {
            if (w == 0) return -1;
            row = free > 0 ? freeRows[--free] : rows++;
            for (var c = 0; c < keys; c++) Put(row, c, key[c]);
            weight[row] = w;
            Count++;
            if (Count * 2 > index.Length) Rehash(); else Place(row);
            foreach (var a in arrangements) a.Link(row);
            return row;
        }
        var n = weight[row] + w;
        if (n != 0) { weight[row] = n; return row; }
        Remove(row);
        return -1;
    }

    void Remove(int row)
    {
        foreach (var a in arrangements) a.Unlink(row);
        Unplace(row);
        weight[row] = 0;
        for (var c = 0; c < wide.Length; c++) if (wide[c]) wideCols[c]!.Clear(row); else narrowCols[c]!.Clear(row);
        freeRows[free++] = row;
        if (--Count == 0) { rows = 0; free = 0; freeRows.Release(); }       // an empty table holds no list of free row numbers either
    }

    void Place(int row)
    {
        var h = (int)(HashOf(row, KeyCols) & (ulong)mask);
        while (index[h] != 0) h = (h + 1) & mask;
        index[h] = row + 1;
    }

    /// <summary>Removes `row` from the index by backward shift, so no tombstones are left.</summary>
    void Unplace(int row)
    {
        var h = (int)(HashOf(row, KeyCols) & (ulong)mask);
        while (index[h] != row + 1) h = (h + 1) & mask;
        Shift(index, mask, h, r => (int)(HashOf(r - 1, KeyCols) & (ulong)mask));
    }

    internal static void Shift(int[] index, int mask, int hole, Func<int, int> home)
    {
        index[hole] = 0;
        for (var j = (hole + 1) & mask; index[j] != 0; j = (j + 1) & mask)
        {
            var k = home(index[j]);
            if (((j - k) & mask) >= ((j - hole) & mask)) { index[hole] = index[j]; index[j] = 0; hole = j; }
        }
    }

    void Rehash()
    {
        index = new int[index.Length * 2]; mask = index.Length - 1;
        for (var r = 0; r < rows; r++) if (weight[r] != 0) Place(r);
    }

    /// <summary>The live rows, in no particular order.</summary>
    public IEnumerable<int> Rows() { for (var r = 0; r < rows; r++) if (weight[r] != 0) yield return r; }

    /// <summary>A second way into the rows, by the values of some key columns, kept up to date as rows come and go. Only key columns: a column that `Set` writes can change under an arrangement, which would then keep the row under the value it had.</summary>
    public Arrangement Arrange(params int[] cols)
    {
        if (cols.Length == 0 || cols.Any(c => c < 0 || c >= keys)) throw new ArgumentException($"an arrangement is by key columns (the first {keys}); got {string.Join(", ", cols)}");
        var a = new Arrangement(this, cols);
        arrangements.Add(a);
        foreach (var r in Rows()) a.Link(r);
        return a;
    }

    public long Footprint() => wideCols.Sum(c => c?.Footprint ?? 0) + narrowCols.Sum(c => c?.Footprint ?? 0) + weight.Footprint + freeRows.Footprint + index.Length * 4L + arrangements.Sum(a => a.Footprint());
}

/// <summary>The rows of a table grouped by the values of some of its columns: an open-addressing index from a value to the first row with it, and the rows with one value chained through a next and a previous row number.</summary>
public sealed class Arrangement
{
    readonly Table t;
    readonly int[] cols;
    readonly Segments<int> next = new(), prev = new();   // row + 1, 0 is none
    int[] index = new int[64]; int mask = 63;
    public int Values { get; private set; }

    internal Arrangement(Table table, int[] columns) { t = table; cols = columns; }

    /// <summary>The number of columns it is arranged by.</summary>
    public int Width => cols.Length;

    int Home(int row) => (int)(t.HashOf(row, cols) & (ulong)mask);

    int Slot(ReadOnlySpan<long> value)
    {
        for (var s = (int)(Table.HashOf(value) & (ulong)mask); index[s] != 0; s = (s + 1) & mask)
            if (t.Equal(index[s] - 1, cols, value)) return s;
        return -1;
    }

    /// <summary>The rows whose columns hold `value`, as they are now: the caller may add and remove rows while it goes through them.</summary>
    public List<int> Rows(ReadOnlySpan<long> value)
    {
        var rows = new List<int>();
        for (var r = First(value); r >= 0; r = Next(r)) rows.Add(r);
        return rows;
    }

    /// <summary>The first row with `value` and the one after a row, -1 at the end: a walk without a list, for a caller that does not change the table while it walks.</summary>
    public int First(ReadOnlySpan<long> value) { var s = Slot(value); return s < 0 ? -1 : index[s] - 1; }
    public int Next(int row) => next[row] - 1;

    internal void Link(int row)
    {
        for (var s = Home(row); ; s = (s + 1) & mask)
        {
            if (index[s] == 0)
            {
                index[s] = row + 1; next[row] = 0; prev[row] = 0; Values++;
                if (Values * 2 > index.Length) Rehash();
                return;
            }
            var head = index[s] - 1;
            if (SameValue(head, row)) { next[row] = head + 1; prev[row] = 0; prev[head] = row + 1; index[s] = row + 1; return; }
        }
    }

    internal void Unlink(int row)
    {
        var p = prev[row] - 1; var n = next[row] - 1;
        if (p >= 0) { next[p] = n + 1; if (n >= 0) prev[n] = p + 1; }
        else
        {
            var s = Home(row);
            while (index[s] != row + 1) s = (s + 1) & mask;
            if (n >= 0) { index[s] = n + 1; prev[n] = 0; }
            else { Table.Shift(index, mask, s, r => Home(r - 1)); Values--; }
        }
        next.Clear(row); prev.Clear(row);
    }

    bool SameValue(int a, int b)
    {
        foreach (var c in cols) if (t.Get(a, c) != t.Get(b, c)) return false;
        return true;
    }

    void Rehash()
    {
        var old = index;
        index = new int[old.Length * 2]; mask = index.Length - 1;
        foreach (var head in old)
        {
            if (head == 0) continue;
            var s = Home(head - 1);
            while (index[s] != 0) s = (s + 1) & mask;
            index[s] = head;
        }
    }

    public long Footprint() => next.Footprint + prev.Footprint + index.Length * 4L;
}
