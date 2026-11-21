using System.Numerics;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>String to dense int table. Text values, entity ids and relationship ids share one table per engine, so a handle names the same element in every graph.
/// A code is young when it is minted for an adapter row (`InternYoung`) or by an expression of a stateless step (`InternComputed` while `MintYoung` is on); every other code is old. A young code becomes old when it is interned old, or when a batch that holds it is escaped: the engine escapes every input of a stateful operator, so every code that reaches state is old. `Collect`, at the end of a step, frees the codes still young (the texts of rows a step dropped); a freed code is reused, the smallest first. An old code is never freed. While a holder (a row writer with rows, a batch not yet ingested) is registered, nothing is freed.
/// Which number a text gets therefore depends on where the input was cut into steps. No result may depend on code numbers, only on the texts: codes are compared for equality, and an order by code is an order of convenience that no output shows.
/// Storage: no string object per text. A text is its bytes in pages of 64 KB, one byte per character when every character is below 256, else two; per code its place, its length with the young mark, and its hash; one open-addressing index of codes. `Get` makes the string; a small cache keeps the strings asked for last, and `Text` the value of each cached string.</summary>
public sealed class Interner : ITextTable
{
    const int Page = 1 << 16, CacheSize = 16384;
    List<byte[]> pages = [new byte[Page]];
    int filling, used;
    readonly Segments<int> start = new();             // page << 16 | offset (pages are 64 KB, a long text is a page of its own); absent for a free code
    readonly Segments<int> meta = new(), hashes = new();   // stored bytes << 3 | form << 1 | young; form: 0 one byte per character, 1 two, 2 an id (`IdForm`)
    int codes;
    long liveBytes, deadBytes;
    int[] index = new int[64]; int mask = 63;
    readonly List<int> minted = [];
    readonly PriorityQueue<int, int> free = new();
    readonly HashSet<object> holders = new(ReferenceEqualityComparer.Instance);
    readonly string?[] cache = new string?[CacheSize]; readonly int[] cached = new int[CacheSize];
    readonly TextValue?[] values = new TextValue?[CacheSize];
    public Interner() => Array.Fill(cached, -1);
    /// <summary>The number of slots: live codes and freed ones.</summary>
    public int Count => codes;
    /// <summary>The number of live texts.</summary>
    public int Live => codes - free.Count;
    /// <summary>On while the engine runs a stateless step (Filter, Map): `InternComputed` mints young.</summary>
    public bool MintYoung { get; set; }
    public int Intern(string s) => Intern(s.AsSpan());
    public int Intern(ReadOnlySpan<char> s) { var i = Find(s); if (i >= 0) { Old(i); return i; } return Add(s, false); }
    /// <summary>The code of a text an adapter row carries: young when the text is new.</summary>
    public int InternYoung(string s) => InternYoung(s.AsSpan());
    public int InternYoung(ReadOnlySpan<char> s) { var i = Find(s); return i >= 0 ? i : Add(s, true); }
    /// <summary>The code of a text an expression computed: young when new and a stateless step runs, else old.</summary>
    public int InternComputed(string s) => MintYoung ? InternYoung(s) : Intern(s);
    public bool IsYoung(int code) => (uint)code < (uint)codes && (meta[code] & 1) != 0;
    const int Narrow = 0, Wide = 1, IdForm = 2;
    /// <summary>The value of a base32 character (a to z, 2 to 7) by its code, 255 for any other character below 128.</summary>
    static ReadOnlySpan<byte> Base32Value => [255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 26, 27, 28, 29, 30, 31, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 255, 255, 255, 255, 255];
    const string Base32Chars = "abcdefghijklmnopqrstuvwxyz234567";
    /// <summary>An element id as `KeyText.Id` mints it, a letter, a colon and 26 base32 characters (130 bits), packed into `into`: the letter, then the characters 5 bits each in three little-endian words of 12, 12 and 2 characters (8, 8 and 2 bytes), 19 bytes for 28 characters. False for any other text.</summary>
    static bool TryPackId(ReadOnlySpan<char> s, Span<byte> into)
    {
        if (s.Length != 28 || s[1] != ':' || s[0] is < 'a' or > 'z') return false;
        ulong a = 0, b = 0, c = 0;
        for (var i = 0; i < 26; i++)
        {
            var ch = s[2 + i];
            if (ch >= 128 || Base32Value[ch] is var v && v == 255) return false;
            if (i < 12) a = a << 5 | v; else if (i < 24) b = b << 5 | v; else c = c << 5 | v;
        }
        into[0] = (byte)s[0];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(into[1..], a);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(into[9..], b);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(into[17..], (ushort)c);
        return true;
    }
    static string UnpackId(ReadOnlySpan<byte> p) => string.Create(28, (p[0], System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(p[1..]), System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(p[9..]), (ulong)System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(p[17..])), static (into, w) =>
    {
        into[0] = (char)w.Item1; into[1] = ':';
        for (var i = 0; i < 12; i++) into[2 + i] = Base32Chars[(int)(w.Item2 >> (5 * (11 - i)) & 31)];
        for (var i = 0; i < 12; i++) into[14 + i] = Base32Chars[(int)(w.Item3 >> (5 * (11 - i)) & 31)];
        for (var i = 0; i < 2; i++) into[26 + i] = Base32Chars[(int)(w.Item4 >> (5 * (1 - i)) & 31)];
    });
    void Old(int code) { var m = meta[code]; if ((m & 1) != 0) meta[code] = m & ~1; }

    /// <summary>The stored bytes of a text, in `buffer` when it fits, and its form: an id packed, else one or two bytes per character. A text has one form, so equal texts have equal bytes.</summary>
    static ReadOnlySpan<byte> Encode(ReadOnlySpan<char> s, Span<byte> buffer, out int form, out byte[]? rented)
    {
        rented = null;
        if (TryPackId(s, buffer)) { form = IdForm; return buffer[..19]; }
        form = Narrow;
        foreach (var c in s) if (c > 0xFF) { form = Wide; break; }       // a loop: `ContainsAnyExceptInRange` allocated 96 B a call, so a known text cost an allocation
        if (form == Wide) return System.Runtime.InteropServices.MemoryMarshal.AsBytes(s);
        if (s.Length > buffer.Length) buffer = rented = System.Buffers.ArrayPool<byte>.Shared.Rent(s.Length);
        for (var i = 0; i < s.Length; i++) buffer[i] = (byte)s[i];
        return buffer[..s.Length];
    }

    static int Hash(ReadOnlySpan<byte> v, int form)
    {
        var h = (ulong)v.Length << 2 | (uint)form;
        for (; v.Length >= 8; v = v[8..]) h = Table.Mix(h, BitConverter.ToInt64(v));
        long tail = 0; foreach (var b in v) tail = tail << 8 | b;
        h = Table.Mix(h, tail);
        return (int)(h ^ h >> 32);
    }

    ReadOnlySpan<byte> Bytes(int code) { var at = start[code]; return pages[(int)((uint)at >> 16)].AsSpan(at & 0xFFFF, meta[code] >> 3); }

    int Find(ReadOnlySpan<char> s)
    {
        Span<byte> buffer = stackalloc byte[256];
        var b = Encode(s, buffer, out var form, out var rented);
        var h = Hash(b, form); var w = form << 1; var found = -1;
        for (var k = h & mask; index[k] != 0; k = (k + 1) & mask)
        {
            var c = index[k] - 1;
            if (hashes[c] == h && (meta[c] & 6) == w && Bytes(c).SequenceEqual(b)) { found = c; break; }
        }
        if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        return found;
    }

    int Add(ReadOnlySpan<char> s, bool young)
    {
        var i = free.TryDequeue(out var f, out _) ? f : codes++;
        Store(i, s, young);
        if (young) minted.Add(i);
        return i;
    }

    void Store(int code, ReadOnlySpan<char> s, bool young)
    {
        Span<byte> buffer = stackalloc byte[256];
        var b = Encode(s, buffer, out var form, out var rented);
        Write(code, b);
        meta[code] = b.Length << 3 | form << 1 | (young ? 1 : 0);
        hashes[code] = Hash(b, form);
        if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        liveBytes += b.Length;
        if ((long)Live * 4 > (long)index.Length * 3) Rehash(); else Place(code);
    }

    void Write(int code, ReadOnlySpan<byte> b)
    {
        if (b.Length >= Page) { NewPage(b.ToArray()); start[code] = (pages.Count - 1) << 16; return; }      // a long text is a page of its own
        if (used + b.Length >= Page) { NewPage(new byte[Page]); filling = pages.Count - 1; used = 0; }       // an offset stays below 65,536
        b.CopyTo(pages[filling].AsSpan(used));
        start[code] = filling << 16 | used;
        used += b.Length;
    }

    /// <summary>A page number takes the high 16 bits of a start: 65,536 pages, 4 GB of text.</summary>
    void NewPage(byte[] page)
    {
        if (pages.Count == 1 << 16) throw new InvalidOperationException("the interner holds 4 GB of text, its limit");
        pages.Add(page);
    }

    void Place(int code)
    {
        var k = hashes[code] & mask;
        while (index[k] != 0) k = (k + 1) & mask;
        index[k] = code + 1;
    }

    void Rehash()
    {
        index = new int[index.Length * 2]; mask = index.Length - 1;
        for (var c = 0; c < codes; c++) if (start.Has(c)) Place(c);
    }

    /// <summary>Makes every young code of the batch's selected rows old: the batch is about to reach state. Columns the layout types as numbers are skipped; untyped ones are read as codes, where a false hit only keeps a text.</summary>
    public void Escape(Batch b)
    {
        if (minted.Count == 0) return;
        var typed = b.Layout.Interned;
        for (var c = 0; c < b.Cols.Length; c++)
        {
            var col = b.Cols[c];
            if (col.IsDec || (typed is not null && c < typed.Length && !typed[c])) continue;
            for (var k = 0; k < b.Rows; k++)
            {
                var v = col.L[b.RowAt(k)];
                if ((ulong)v < (ulong)codes) Old((int)v);
            }
        }
    }
    /// <summary>Registers something that holds young codes outside the engine (a row writer with rows, a batch not yet ingested): nothing is freed until it is released.</summary>
    public void Hold(object holder) => holders.Add(holder);
    public void Release(object holder) => holders.Remove(holder);
    /// <summary>Whether a holder is registered (nothing is freed while one is).</summary>
    public bool Holding => holders.Count > 0;
    /// <summary>The end of a step: frees the codes still young, unless a holder is registered.</summary>
    public void Collect()
    {
        if (holders.Count > 0 || minted.Count == 0) return;
        foreach (var i in minted)
        {
            if ((meta[i] & 1) == 0) continue;
            var k = hashes[i] & mask;
            while (index[k] != i + 1) k = (k + 1) & mask;
            Table.Shift(index, mask, k, c => hashes[c - 1] & mask);
            var n = Bytes(i).Length; liveBytes -= n; deadBytes += n;
            start.Clear(i); meta.Clear(i); hashes.Clear(i);
            if (cached[i & (CacheSize - 1)] == i) { cached[i & (CacheSize - 1)] = -1; cache[i & (CacheSize - 1)] = null; values[i & (CacheSize - 1)] = null; }
            free.Enqueue(i, i);
        }
        minted.Clear();
        if (deadBytes > liveBytes + Page) Compact();
    }

    /// <summary>Copies the texts in use together into new pages; their codes stay.</summary>
    void Compact()
    {
        var old = pages; pages = [new byte[Page]]; filling = 0; used = 0; deadBytes = 0;
        for (var c = 0; c < codes; c++)
        {
            if (!start.Has(c)) continue;
            var at = start[c];
            Write(c, old[(int)((uint)at >> 16)].AsSpan(at & 0xFFFF, meta[c] >> 3));
        }
    }

    public bool TryFind(string s, out int i) { i = Find(s); return i >= 0; }
    /// <summary>Every slot by code, null where the code is free (a seal).</summary>
    public IReadOnlyList<string?> Slots => [.. Enumerable.Range(0, codes).Select(c => start.Has(c) ? Get(c) : null)];
    /// <summary>Replaces the whole table (restoring a snapshot): the same strings get the same codes, the free slots are free again, every code is old.</summary>
    public void ReplaceAll(IReadOnlyList<string?> all)
    {
        pages = [new byte[Page]]; filling = 0; used = 0; liveBytes = 0; deadBytes = 0;
        start.Release(); meta.Release(); hashes.Release();
        index = new int[64]; mask = 63; codes = 0;
        minted.Clear(); free.Clear(); holders.Clear();
        Array.Fill(cached, -1); Array.Clear(cache); Array.Clear(values);
        foreach (var x in all)
        {
            var i = codes++;
            if (x is null) free.Enqueue(i, i); else Store(i, x, false);
        }
    }
    /// <summary>The text of a code as a value, made once while its string is in the cache (a slot that takes another code lets go of its value).</summary>
    public Value Text(int i)
    {
        var s = Get(i);
        return values[i & (CacheSize - 1)] ??= (TextValue)Value.Text(s);
    }
    public string Get(int i)
    {
        var slot = i & (CacheSize - 1);
        if (cached[slot] == i) return cache[slot]!;
        var s = Decode(i);
        cached[slot] = i; cache[slot] = s; values[slot] = null;
        return s;
    }

    string Decode(int i)
    {
        if ((uint)i >= (uint)codes || !start.Has(i)) throw new InvalidOperationException($"text {i} was freed");
        var b = Bytes(i);
        return ((meta[i] >> 1) & 3) switch
        {
            Wide => new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(b)),
            IdForm => UnpackId(b),
            _ => System.Text.Encoding.Latin1.GetString(b),
        };
    }

    /// <summary>A reader for one match partition. The writer holds the graph and its codes fixed until every partition joins.
    /// Graph ids, endpoints and property texts have already been interned by graph apply. The decoding cache belongs to this reader.</summary>
    internal sealed class MatchReader(Interner owner) : ITextTable
    {
        const int Size = 256;
        readonly (int Code, string? Text, Value? Value)[] cache = new (int, string?, Value?)[Size];
        public void Clear() => Array.Clear(cache);
        public bool TryFind(string text, out int code) => owner.TryFind(text, out code);
        public int Intern(string text) => TryFind(text, out var code) && !owner.IsYoung(code) ? code : throw new InvalidOperationException("match read a text outside the committed graph");
        public string Get(int code)
        {
            ref var slot = ref cache[code & (Size - 1)];
            if (slot.Code != code || slot.Text is null) slot = (code, owner.Decode(code), null);
            return slot.Text;
        }
        public Value Text(int code)
        {
            var text = Get(code);
            return cache[code & (Size - 1)].Value ??= Value.Text(text);
        }
    }
}

/// <summary>Column layout of a row type: names, and which columns hold decimals (all others hold 64-bit codes: Integer, Instant, Boolean, interned Text, element handles).</summary>
public sealed class Layout
{
    public string[] Names { get; }
    public bool[] IsDec { get; }
    readonly Dictionary<string, int> index;
    /// <summary>Per column: true when the 64-bit code is an index into the interner, false for raw numbers (Integer, Decimal, Boolean, Instant). Any other type, the entity and relationship references and a type added later, counts as a code: a column the engine does not know to be a number is escaped (`Interner.Escape`), where a false hit only keeps a text. Null when the layout was built without types.</summary>
    public bool[]? Interned { get; }
    public Layout(string[] names, bool[] isDec, bool[]? interned = null) { Names = names; IsDec = isDec; Interned = interned; index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal); }
    public static Layout Of(Schema s) => new([.. s.Cols.Select(c => c.Name)], [.. s.Cols.Select(c => c.T.Ty.Tag == Tag.Decimal)], [.. s.Cols.Select(c => IsCode(c.T.Ty.Tag))]);
    /// <summary>Whether a column of this type holds codes of the text table: every type that is not a number.</summary>
    public static bool IsCode(Tag t) => t is not (Tag.Integer or Tag.Decimal or Tag.Boolean or Tag.Instant);
    public int Count => Names.Length;
    public int Index(string name) => index.TryGetValue(name, out var i) ? i : throw new KeyNotFoundException($"column {name} not in layout [{string.Join(", ", Names)}]");
    public bool Has(string name) => index.ContainsKey(name);
}

public sealed class Column : IWorkingArrays
{
    public long[] L = [];
    public decimal[] D = [];
    public bool[]? P; // presence, null means every row is present
    public bool IsDec;
    public Column(bool isDec) { IsDec = isDec; }
    public void Ensure(int n)
    {
        if (IsDec) { if (D.Length < n) D = WorkingArrays.Grow(D, n, this); }
        else if (L.Length < n) L = WorkingArrays.Grow(L, n, this);
        if (P is not null && P.Length < n) { var old = P.Length; P = WorkingArrays.Grow(P, n, this); Array.Fill(P, true, old, P.Length - old); }
    }
    bool IWorkingArrays.Listed { get; set; }
    /// <summary>Back to the first size, every row present; large arrays go back to the pool (`WorkingArrays`).</summary>
    void IWorkingArrays.Cut()
    {
        if (IsDec) { WorkingArrays.Return(D); D = new decimal[WorkingArrays.Small]; }
        else { WorkingArrays.Return(L); L = new long[WorkingArrays.Small]; }
        if (P is not null) WorkingArrays.Return(P);
        P = null;
    }
    public bool Present(int i) => P is null || P[i];
    public void ClearPresence() => P = null;
    public void SetAbsent(int i, int n) { if (P is null) { P = new bool[Math.Max(n, IsDec ? D.Length : L.Length)]; Array.Fill(P, true); } P[i] = false; }
}

/// <summary>A signed row batch (a Z-set slice): columns, weights (null means +1 for every row), optional selection vector. `Cols` may hold columns behind those the layout names: the ones a `Map` carries for lineage, which only a `Group` and a sink read; every other reader goes by the layout.</summary>
public sealed class Batch : IWorkingArrays
{
    public Layout Layout { get; }
    public Column[] Cols { get; }
    public int N;
    public long[]? W;
    public int[]? Sel;   // selected row indexes in ascending order; null = all
    public int SelN;
    public Batch(Layout layout, int capacity = 0)
    {
        Layout = layout; Cols = new Column[layout.Count];
        for (var i = 0; i < Cols.Length; i++) { Cols[i] = new Column(layout.IsDec[i]); if (capacity > 0) Cols[i].Ensure(capacity); }
    }
    Batch(Layout layout, Column[] cols, int n, long[]? w) { Layout = layout; Cols = cols; N = n; W = w; view = true; }
    /// <summary>The batch shows another batch's columns and weights (`Project`, `WithSelection`, `WithColumns`).</summary>
    readonly bool view;
    bool IWorkingArrays.Listed { get; set; }
    /// <summary>Empty, its weights back to the first size; a view lets go of the columns and weights it shows (`WorkingArrays`).</summary>
    void IWorkingArrays.Cut()
    {
        N = 0; Sel = null; SelN = 0;
        if (view) { Array.Clear(Cols); W = null; }
        else if (W is not null) { WorkingArrays.Return(W); W = new long[WorkingArrays.Small]; }
    }

    public int Rows => Sel is null ? N : SelN;
    public int RowAt(int k) => Sel is null ? k : Sel[k];
    public long Weight(int row) => W is null ? 1 : W[row];
    public void Ensure(int n) { foreach (var c in Cols) c.Ensure(n); if (W is not null && W.Length < n) W = WorkingArrays.Grow(W, n, this); }
    public void Clear() { N = 0; Sel = null; SelN = 0; foreach (var c in Cols) c.P = null; }

    /// <summary>A view sharing the column arrays with a new layout and column mapping (no data copy).</summary>
    public Batch Project(Layout layout, int[] source)
    {
        var cols = new Column[source.Length];
        for (var i = 0; i < cols.Length; i++) cols[i] = Cols[source[i]];
        return new Batch(layout, cols, N, W) { Sel = Sel, SelN = SelN };
    }
    public Batch WithSelection(int[] sel, int n) => new(Layout, Cols, N, W) { Sel = sel, SelN = n };
    public Batch WithColumns(Layout layout, Column[] cols) => new(layout, cols, N, W) { Sel = Sel, SelN = SelN };

    public bool IsEmpty => Rows == 0;
}

/// <summary>An owner of working arrays: a column, or a batch's weights.</summary>
interface IWorkingArrays { bool Listed { get; set; } void Cut(); }

/// <summary>The working arrays of a step: the output batches and columns that operators and compiled expressions keep from call to call. One that grows past its first size (`Small` rows) while the engine runs a step takes a power-of-two array from a pool, and when the step ends it is cut back to its first size and its array goes back to the pool. The arrays of a large change are shared by the operators of the steps that follow instead of each keeping its own largest call; the pool drops an array that no step took for `Keep` steps. Nothing read after a step lives in them: the engine reads an operator's output within its step.</summary>
public static class WorkingArrays
{
    public const int Small = 64, Keep = 256;
    [ThreadStatic] static List<IWorkingArrays>? grown;
    [ThreadStatic] static bool open;
    /// <summary>Steps closed on this thread: the age of an array in the pool.</summary>
    [ThreadStatic] static long steps;
    /// <summary>The engine starts the operators of a step on this thread.</summary>
    public static void Open() => open = true;
    /// <summary>Partition buffers are owned by a single preparation call, including when its work is scheduled on the writer.</summary>
    internal readonly struct Pause : IDisposable
    {
        readonly bool wasOpen;
        public Pause() { wasOpen = open; open = false; }
        public void Dispose() => open = wasOpen;
    }
    /// <summary>A reused view that shows a batch past its first size: it lets go of it when the step ends.</summary>
    public static void Shows(Batch view) => List(view);
    /// <summary>The step's operators are done and their outputs read: what grew is cut and its arrays go back to the pool.</summary>
    public static void Close()
    {
        open = false; steps++;
        Pool<long>.Drop(); Pool<decimal>.Drop(); Pool<bool>.Drop();
        if (grown is not { Count: > 0 }) return;
        foreach (var x in grown) { x.Cut(); x.Listed = false; }
        grown.Clear();
    }

    /// <summary>`a` grown to hold `n` rows, its rows kept and the rest zero. Past `Small` while a step is open, from the pool; the owner is cut when the step ends.</summary>
    internal static T[] Grow<T>(T[] a, int n, IWorkingArrays owner)
    {
        if (!open || n <= Small) { Array.Resize(ref a, Math.Max(n, Small)); return a; }
        var b = Pool<T>.Rent(n);
        Array.Copy(a, b, a.Length); Array.Clear(b, a.Length, b.Length - a.Length);
        Return(a);
        List(owner);
        return b;
    }
    internal static void Return<T>(T[] a) { if (a.Length > Small && System.Numerics.BitOperations.IsPow2(a.Length)) Pool<T>.Give(a); }
    static void List(IWorkingArrays x) { if (!open || x.Listed) return; x.Listed = true; (grown ??= []).Add(x); }

    /// <summary>Free arrays by power-of-two length, the oldest first.</summary>
    static class Pool<T>
    {
        [ThreadStatic] static List<(T[] A, long Given)>?[]? free;
        public static T[] Rent(int n)
        {
            var k = System.Numerics.BitOperations.Log2(System.Numerics.BitOperations.RoundUpToPowerOf2((uint)n));
            if (free?[k] is { Count: > 0 } f) { var a = f[^1].A; f.RemoveAt(f.Count - 1); return a; }
            return new T[1 << k];
        }
        public static void Give(T[] a) { free ??= new List<(T[], long)>?[32]; (free[System.Numerics.BitOperations.Log2((uint)a.Length)] ??= []).Add((a, steps)); }
        public static void Drop()
        {
            if (free is null) return;
            foreach (var f in free)
            {
                if (f is null) continue;
                var n = 0; while (n < f.Count && steps - f[n].Given > Keep) n++;
                if (n > 0) f.RemoveRange(0, n);
            }
        }
    }
}

/// <summary>Per-engine element id handles and a few conversions between values and columns.</summary>
public static class Codec
{
    public static long ToCode(Value v, Interner it) => ToCode(v, (ITextTable)it);
    internal static long ToCode(Value v, ITextTable it) => v switch
    {
        TextValue t => it.Intern(t.S),
        IntegerValue i => (long)i.N,
        BooleanValue b => b.B ? 1 : 0,
        InstantValue n => n.Ns,
        EntityValue e => it.Intern(e.Id),
        _ => throw new InvalidOperationException($"no 64-bit code for {v.Kind}"),
    };

    public static Value ToValue(long code, Ty ty, Interner it) => ty.Tag switch
    {
        Tag.Text => it.Text((int)code),
        Tag.Integer => Value.Integer(code),
        Tag.Boolean => Value.Boolean(code != 0),
        Tag.Instant => Value.Instant(code),
        Tag.Ent => Value.Entity(it.Get((int)code)),
        _ => throw new InvalidOperationException($"no value for {ty}"),
    };

    public static Value ToValue(Column c, int row, Ty ty, Interner it) =>
        !c.Present(row) ? Value.Null : ty.Tag == Tag.Decimal ? Value.Decimal(c.D[row]) : ToValue(c.L[row], ty, it);

    public static void Set(Column c, int row, Value v, Interner it)
    {
        if (v is NullValue) { c.SetAbsent(row, row + 1); return; }
        if (c.IsDec) c.D[row] = v is DecimalValue d ? d.Q : throw new InvalidOperationException($"decimal column cannot hold {v.Kind}");
        else c.L[row] = ToCode(v, it);
    }
}

/// <summary>Writes source rows into a batch by field name (cold path) or column index (hot path). Optional fields given null are absent, and so is every column that is not written before the next row begins. Without a text table (`it` null: rows of a type no source binds, which are counted and never read) its texts are not kept.</summary>
public sealed class RowWriter(Layout layout, Interner? it) : IDisposable
{
    BatchBuilder bb = new(layout);
    int row = -1;
    readonly bool[] written = new bool[layout.Count];
    /// <summary>Added to every instant written: a capture replayed again as later history carries its event times forward with its capture clock.</summary>
    public long InstantOffset;
    public Layout Layout { get; } = layout;
    public int Rows => bb.Batch.N;
    public int Col(string name) => Layout.Index(name);
    public void Begin(long weight = 1) { Close(); it?.Hold(this); row = bb.Add(weight); Array.Clear(written); }
    public void Text(int col, string? v) { written[col] = true; if (v is null) bb.Absent(row, col); else bb.Batch.Cols[col].L[row] = it?.InternYoung(v) ?? 0; }
    public void Text(int col, ReadOnlySpan<char> v) { written[col] = true; bb.Batch.Cols[col].L[row] = it is null ? 0 : it.InternYoung(v); }
    public void Code(int col, long code) { written[col] = true; bb.Batch.Cols[col].L[row] = code; }
    public void Int(int col, long v) { written[col] = true; bb.Batch.Cols[col].L[row] = v; }
    public void Instant(int col, long ns) { written[col] = true; bb.Batch.Cols[col].L[row] = ns + InstantOffset; }
    public void Dec(int col, decimal v) { written[col] = true; bb.Batch.Cols[col].D[row] = v; }
    public void Absent(int col) { written[col] = true; bb.Absent(row, col); }
    public void Text(string col, string? v) => Text(Col(col), v);
    public void Instant(string col, long ns) => Instant(Col(col), ns);
    /// <summary>The rows written so far. The batch holds their young codes until the engine ingests it.</summary>
    public Batch? Build() { Close(); var b = bb.Build(); bb = new BatchBuilder(Layout); row = -1; if (b is not null) it?.Hold(b); it?.Release(this); return b; }
    /// <summary>Takes back the rows written after the first `rows` (a record that turned out to be malformed after it had written some).</summary>
    public void Truncate(int rows) { if (rows >= bb.Batch.N) return; bb.Batch.N = rows; row = -1; if (rows == 0) it?.Release(this); }
    /// <summary>Drops the rows not built yet and releases what they hold: a writer that a failure left behind does not keep the interner from freeing.</summary>
    public void Dispose() => Truncate(0);
    void Close() { if (row < 0) return; for (var c = 0; c < written.Length; c++) if (!written[c]) bb.Absent(row, c); }
}
