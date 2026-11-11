using System.Collections;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>A snapshot of the running state of an engine (and of a host): the object graph reachable from a root, written field by field, and loaded back into the freshly constructed objects of an engine built from the same definitions. What is configuration (compiled expressions, typed definitions, layouts, delegates, stores) is not written: the fresh engine already has it. What is state (group tables, window accumulators, join indexes, identity bindings, element layers, telemetry windows, the hot graphs, the interner) is.
/// The snapshot is tied to the build that wrote it (the module version id of this assembly is in the header of the seal): a seal that this build cannot load is not loaded, and the journal is replayed instead.</summary>
public static class StateSnapshot
{
    static readonly byte[] Magic = "SATTLESNAP1"u8.ToArray();

    // ---- what is not state ----

    static bool IsDelegate(Type t) => typeof(Delegate).IsAssignableFrom(t) || (t.IsGenericType && t.GetGenericArguments().Any(IsDelegate)) || (t.HasElementType && IsDelegate(t.GetElementType()!));

    static Type Core(Type t) { while (t.HasElementType) t = t.GetElementType()!; return t; }

    /// <summary>Configuration of a compiled plan, not state.</summary>
    static bool Skip(FieldInfo f)
    {
        var t = f.FieldType;
        if (IsDelegate(t)) return true;
        var c = Core(t);
        if (c == typeof(object)) return true;                                          // locks
        if (typeof(VExpr).IsAssignableFrom(c)) return true;                             // compiled expressions (their caches are rebuilt). Row layouts are written: an object that exists only in the snapshot (a row writer made when its first row came) needs its own
        if (c.Assembly == typeof(Catalog).Assembly || c == typeof(OntologyView)) return true;     // typed definitions and the ontology views built from them
        if (typeof(IGraphStore).IsAssignableFrom(c) || typeof(IJournalStore).IsAssignableFrom(c) || typeof(Engine) == c || typeof(SpaceHost) == c) return true;
        if (c == typeof(IEngineInput) || c == typeof(IRowSink)) return true;            // the sink a replay writes to is the host, not part of its state
        if (typeof(Stream).IsAssignableFrom(c) || typeof(Type).IsAssignableFrom(c)) return true;
        return false;
    }

    static IEnumerable<FieldInfo> Fields(Type t)
    {
        for (var b = t; b is not null && b != typeof(object) && b != typeof(ValueType); b = b.BaseType)
            foreach (var f in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!Skip(f)) yield return f;
    }

    static string Key(FieldInfo f) => f.DeclaringType!.FullName + "." + f.Name;

    // ---- value tags ----

    const byte TStatic = 19;
    const byte TNull = 0, TTrue = 1, TFalse = 2, TInt = 3, TLong = 4, TDec = 5, TDouble = 6, TString = 7, TRef = 8, TEnum = 9, TStruct = 10, TByte = 11, TShort = 12, TUInt = 13, TULong = 14, TChar = 15, TFloat = 16, TSByte = 17, TUShort = 18;
    const byte KClass = 1, KArray = 2, KList = 3, KDict = 4, KSet = 5, KQueue = 6, KStack = 7, KGraph = 8, KInterner = 9;

    // ---- singletons: `Props.Empty`, `Value.True` and the like are shared instances that other code compares by identity or counts on being unique ----

    static readonly Lazy<Dictionary<object, (Type Owner, string Field)>> Singletons = new(() =>
    {
        var map = new Dictionary<object, (Type, string)>(ReferenceEqualityComparer.Instance);
        foreach (var asm in new[] { typeof(Graph).Assembly, typeof(StateSnapshot).Assembly })
            foreach (var t in asm.GetTypes())
            {
                if (!t.IsClass || t.IsGenericTypeDefinition || t.ContainsGenericParameters) continue;
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!f.IsInitOnly || !f.FieldType.IsClass || f.FieldType == typeof(string) || IsDelegate(f.FieldType) || typeof(IEnumerable).IsAssignableFrom(f.FieldType) || f.FieldType.IsArray) continue;
                    if (f.FieldType.Assembly != asm && !f.FieldType.IsAssignableFrom(t)) continue;
                    object? v; try { v = f.GetValue(null); } catch (Exception) { continue; }
                    if (v is null || v is IEnumerable || map.ContainsKey(v)) continue;
                    map[v] = (t, f.Name);
                }
            }
        return map;
    });

    // ---- the comparers of sorted collections: the engine uses descending orders (max aggregates, last), which are not data ----

    static byte ComparerKind(Type t, object o)
    {
        if (!t.IsGenericType || !t.GetGenericTypeDefinition().Name.StartsWith("Sorted", StringComparison.Ordinal)) return 0;
        var key = t.GetGenericArguments()[0];
        var cmp = o.GetType().GetProperty("Comparer")?.GetValue(o);
        if (cmp is null) return 0;
        var def = typeof(Comparer<>).MakeGenericType(key).GetProperty("Default")!.GetValue(null);
        if (ReferenceEquals(cmp, def)) return 0;
        if (key == typeof(long)) return 1;
        if (key == typeof(decimal)) return 2;
        if (key == typeof((decimal, decimal))) return 3;
        throw new NotSupportedException($"comparer of {t}");
    }

    static object? MakeComparer(Type t, byte kind)
    {
        if (kind == 0) return null;
        return kind switch
        {
            1 => Comparer<long>.Create((x, y) => y.CompareTo(x)),
            2 => Comparer<decimal>.Create((x, y) => y.CompareTo(x)),
            3 => Comparer<(decimal Ord, decimal Val)>.Create((x, y) => x.Ord != y.Ord ? y.Ord.CompareTo(x.Ord) : y.Val.CompareTo(x.Val)),
            _ => throw new InvalidDataException("comparer kind " + kind),
        };
    }

    // ============================================== writing ==============================================

    sealed class Writer
    {
        readonly List<object> objs = [];
        readonly Dictionary<object, int> ids = new(ReferenceEqualityComparer.Instance);
        readonly List<string> typeNames = []; readonly Dictionary<Type, int> typeIdx = [];
        readonly List<string> names = []; readonly Dictionary<string, int> nameIdx = [];
        readonly MemoryStream body = new(); readonly BinaryWriter w;
        public Writer() { w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true); }

        int Id(object o)
        {
            if (!ids.TryGetValue(o, out var i)) { i = objs.Count; objs.Add(o); ids[o] = i; }
            return i;
        }
        int TypeOf(Type t) { if (!typeIdx.TryGetValue(t, out var i)) { i = typeNames.Count; typeNames.Add(t.AssemblyQualifiedName!); typeIdx[t] = i; } return i; }
        int NameOf(string n) { if (!nameIdx.TryGetValue(n, out var i)) { i = names.Count; names.Add(n); nameIdx[n] = i; } return i; }
        void V(int v) { w.Write7BitEncodedInt(v); }

        void Val(object? v)
        {
            switch (v)
            {
                case null: w.Write(TNull); return;
                case bool b: w.Write(b ? TTrue : TFalse); return;
                case int i: w.Write(TInt); w.Write(i); return;
                case long l: w.Write(TLong); w.Write(l); return;
                case decimal d: w.Write(TDec); foreach (var x in decimal.GetBits(d)) w.Write(x); return;
                case double d: w.Write(TDouble); w.Write(d); return;
                case float f: w.Write(TFloat); w.Write(f); return;
                case string s: w.Write(TString); w.Write(s); return;
                case byte b: w.Write(TByte); w.Write(b); return;
                case sbyte b: w.Write(TSByte); w.Write(b); return;
                case short s: w.Write(TShort); w.Write(s); return;
                case ushort s: w.Write(TUShort); w.Write(s); return;
                case uint u: w.Write(TUInt); w.Write(u); return;
                case ulong u: w.Write(TULong); w.Write(u); return;
                case char c: w.Write(TChar); w.Write(c); return;
            }
            var t = v.GetType();
            if (!t.IsValueType && Singletons.Value.TryGetValue(v, out var single)) { w.Write(TStatic); V(TypeOf(single.Owner)); w.Write(single.Field); return; }
            if (t.IsEnum) { w.Write(TEnum); V(TypeOf(t)); w.Write(Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (t.IsValueType)
            {
                w.Write(TStruct); V(TypeOf(t));
                var fs = Fields(t).ToList(); V(fs.Count);
                foreach (var f in fs) { V(NameOf(Key(f))); Val(f.GetValue(v)); }
                return;
            }
            w.Write(TRef); V(Id(v));
        }

        public byte[] Run(object root)
        {
            Id(root);
            for (var i = 0; i < objs.Count; i++) Record(objs[i]);
            using var outp = new MemoryStream();
            using (var gz = new GZipStream(outp, CompressionLevel.Fastest, leaveOpen: true))
            using (var bw = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(Magic);
                bw.Write7BitEncodedInt(typeNames.Count); foreach (var t in typeNames) bw.Write(t);
                bw.Write7BitEncodedInt(names.Count); foreach (var n in names) bw.Write(n);
                bw.Write7BitEncodedInt(objs.Count);
                bw.Flush(); body.Position = 0; body.CopyTo(gz);
            }
            return outp.ToArray();
        }

        void Record(object o)
        {
            switch (o)
            {
                case Interner it:
                    w.Write(KInterner); V(it.Count); for (var i = 0; i < it.Count; i++) w.Write(it.Get(i)); return;
                case Graph g:
                    {
                        w.Write(KGraph);
                        var els = g.Entities.Cast<Element>().Concat(g.Rels).ToList();
                        V(els.Count); foreach (var e in els) Val(e);
                        return;
                    }
                case Array a:
                    {
                        var et = a.GetType().GetElementType()!;
                        w.Write(KArray); V(TypeOf(a.GetType())); V(a.Length);
                        if (a is decimal[] da) { w.Write((byte)1); var bytes = MemoryMarshal.AsBytes(da.AsSpan()).ToArray(); w.Write(bytes.Length); w.Write(bytes); }
                        else if (a.Rank == 1 && et.IsPrimitive && et != typeof(nint) && et != typeof(nuint))
                        {
                            w.Write((byte)1);
                            var bytes = new byte[Buffer.ByteLength(a)]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
                            w.Write(bytes.Length); w.Write(bytes);
                        }
                        else { w.Write((byte)0); if (a.Rank != 1) throw new NotSupportedException("multi-dimensional array " + a.GetType()); foreach (var x in a) Val(x); }
                        return;
                    }
            }
            var t = o.GetType();
            if (t.IsGenericType)
            {
                var d = t.GetGenericTypeDefinition();
                if (d == typeof(List<>) || d == typeof(Queue<>)) { w.Write(d == typeof(List<>) ? KList : KQueue); V(TypeOf(t)); var items = ((IEnumerable)o).Cast<object?>().ToList(); V(items.Count); foreach (var x in items) Val(x); return; }
                if (d == typeof(Stack<>)) { w.Write(KStack); V(TypeOf(t)); var items = ((IEnumerable)o).Cast<object?>().ToList(); items.Reverse(); V(items.Count); foreach (var x in items) Val(x); return; }   // enumerates top first: store bottom first
                if (d == typeof(HashSet<>) || d == typeof(SortedSet<>)) { w.Write(KSet); V(TypeOf(t)); w.Write(ComparerKind(t, o)); var items = ((IEnumerable)o).Cast<object?>().ToList(); V(items.Count); foreach (var x in items) Val(x); return; }
                if (d == typeof(Dictionary<,>) || d == typeof(SortedDictionary<,>) || d == typeof(SortedList<,>))
                {
                    w.Write(KDict); V(TypeOf(t)); w.Write(ComparerKind(t, o)); var m = (IDictionary)o; V(m.Count);
                    foreach (DictionaryEntry e in m) { Val(e.Key); Val(e.Value); }
                    return;
                }
            }
            if (o is IEnumerable and not string && t.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true) throw new NotSupportedException("collection " + t);
            w.Write(KClass); V(TypeOf(t));
            var fs = Fields(t).ToList(); V(fs.Count);
            foreach (var f in fs) { V(NameOf(Key(f))); Val(f.GetValue(o)); }
        }
    }

    public static byte[] Save(object root) => new Writer().Run(root);

    static bool SameVal(object? a, object? b)
    {
        if (a is StructVal x && b is StructVal y) return x.Type == y.Type && x.Fields.Length == y.Fields.Length && x.Fields.Zip(y.Fields).All(p => p.First.Name == p.Second.Name && SameVal(p.First.Val, p.Second.Val));
        return Equals(a, b);
    }

    /// <summary>Where two snapshots first differ, as a description of the object (diagnostics and tests); null when they hold the same objects.</summary>
    public static string? FirstDifference(byte[] a, byte[] b)
    {
        var ra = new Reader(a).Records; var rb = new Reader(b).Records;
        for (var i = 0; i < Math.Min(ra.Length, rb.Length); i++)
        {
            var x = ra[i]; var y = rb[i];
            if (x.Kind != y.Kind || x.Type != y.Type || x.Items.Length != y.Items.Length || x.Fields.Length != y.Fields.Length) return $"object {i} ({x.Type}): shape differs";
            for (var k = 0; k < x.Items.Length; k++) if (!SameVal(x.Items[k], y.Items[k])) return $"object {i} ({x.Type}, kind {x.Kind}), item {k}: {x.Items[k]} against {y.Items[k]}";
            for (var k = 0; k < x.Fields.Length; k++) if (x.Fields[k].Name != y.Fields[k].Name || !SameVal(x.Fields[k].Val, y.Fields[k].Val)) return $"object {i} ({x.Type}), field {x.Fields[k].Name}: {x.Fields[k].Val} against {y.Fields[k].Val}";
            if ((x.Raw is null) != (y.Raw is null) || (x.Raw is not null && !x.Raw.AsSpan().SequenceEqual(y.Raw))) return $"object {i} ({x.Type}): raw content differs";
            if ((x.Strings is null) != (y.Strings is null) || (x.Strings is not null && !x.Strings.AsSpan().SequenceEqual(y.Strings))) return $"object {i}: interner differs";
        }
        return ra.Length == rb.Length ? null : $"{ra.Length} against {rb.Length} objects";
    }

    /// <summary>How many objects of each type a snapshot holds (diagnostics and tests).</summary>
    public static Dictionary<string, int> Histogram(byte[] data) => new Reader(data).Records.GroupBy(r => r.Kind == KGraph ? "Graph" : r.Kind == KInterner ? "Interner" : r.Type.ToString()).ToDictionary(g => g.Key, g => g.Count());

    // ============================================== reading ==============================================

    sealed record RefVal(int Id);
    sealed record StructVal(Type Type, (string Name, object? Val)[] Fields);
    sealed class Rec { public byte Comparer; public byte Kind; public Type Type = null!; public (string Name, object? Val)[] Fields = []; public object?[] Items = []; public byte[]? Raw; public string[]? Strings; }

    sealed class Reader
    {
        readonly BinaryReader r; readonly Type[] types; readonly string[] names; readonly Rec[] recs;
        public Reader(byte[] data)
        {
            var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            r = new BinaryReader(gz, Encoding.UTF8);
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("not a state snapshot");
            types = new Type[r.Read7BitEncodedInt()];
            for (var i = 0; i < types.Length; i++) types[i] = Type.GetType(r.ReadString(), throwOnError: true)!;
            names = new string[r.Read7BitEncodedInt()]; for (var i = 0; i < names.Length; i++) names[i] = r.ReadString();
            recs = new Rec[r.Read7BitEncodedInt()];
            for (var i = 0; i < recs.Length; i++) recs[i] = ReadRec();
        }
        public Rec[] Records => recs;

        object? Val()
        {
            switch (r.ReadByte())
            {
                case TNull: return null;
                case TTrue: return true;
                case TFalse: return false;
                case TInt: return r.ReadInt32();
                case TLong: return r.ReadInt64();
                case TDec: { var b = new int[4]; for (var i = 0; i < 4; i++) b[i] = r.ReadInt32(); return new decimal(b); }
                case TDouble: return r.ReadDouble();
                case TFloat: return r.ReadSingle();
                case TString: return r.ReadString();
                case TByte: return r.ReadByte();
                case TSByte: return r.ReadSByte();
                case TShort: return r.ReadInt16();
                case TUShort: return r.ReadUInt16();
                case TUInt: return r.ReadUInt32();
                case TULong: return r.ReadUInt64();
                case TChar: return r.ReadChar();
                case TRef: return new RefVal(r.Read7BitEncodedInt());
                case TStatic: { var t = types[r.Read7BitEncodedInt()]; var n = r.ReadString(); return t.GetField(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null); }
                case TEnum: { var t = types[r.Read7BitEncodedInt()]; return Enum.ToObject(t, r.ReadInt64()); }
                case TStruct:
                    {
                        var t = types[r.Read7BitEncodedInt()]; var n = r.Read7BitEncodedInt(); var fs = new (string, object?)[n];
                        for (var i = 0; i < n; i++) { var nm = names[r.Read7BitEncodedInt()]; fs[i] = (nm, Val()); }
                        return new StructVal(t, fs);
                    }
                default: throw new InvalidDataException("bad value tag");
            }
        }

        Rec ReadRec()
        {
            var rec = new Rec { Kind = r.ReadByte() };
            switch (rec.Kind)
            {
                case KInterner: { var n = r.Read7BitEncodedInt(); rec.Strings = new string[n]; for (var i = 0; i < n; i++) rec.Strings[i] = r.ReadString(); rec.Type = typeof(Interner); break; }
                case KGraph: { rec.Type = typeof(Graph); var n = r.Read7BitEncodedInt(); rec.Items = new object?[n]; for (var i = 0; i < n; i++) rec.Items[i] = Val(); break; }
                case KArray:
                    {
                        rec.Type = types[r.Read7BitEncodedInt()]; var n = r.Read7BitEncodedInt(); var raw = r.ReadByte();
                        if (raw == 1) { var len = r.ReadInt32(); rec.Raw = r.ReadBytes(len); rec.Items = new object?[n]; }
                        else { rec.Items = new object?[n]; for (var i = 0; i < n; i++) rec.Items[i] = Val(); }
                        break;
                    }
                case KList or KQueue or KStack or KSet: { rec.Type = types[r.Read7BitEncodedInt()]; if (rec.Kind == KSet) rec.Comparer = r.ReadByte(); var n = r.Read7BitEncodedInt(); rec.Items = new object?[n]; for (var i = 0; i < n; i++) rec.Items[i] = Val(); break; }
                case KDict: { rec.Type = types[r.Read7BitEncodedInt()]; rec.Comparer = r.ReadByte(); var n = r.Read7BitEncodedInt(); rec.Items = new object?[n * 2]; for (var i = 0; i < n * 2; i++) rec.Items[i] = Val(); break; }
                case KClass:
                    {
                        rec.Type = types[r.Read7BitEncodedInt()]; var n = r.Read7BitEncodedInt(); rec.Fields = new (string, object?)[n];
                        for (var i = 0; i < n; i++) { var nm = names[r.Read7BitEncodedInt()]; rec.Fields[i] = (nm, Val()); }
                        break;
                    }
                default: throw new InvalidDataException("bad record kind");
            }
            return rec;
        }
    }

    static readonly Dictionary<Type, Dictionary<string, FieldInfo>> fieldCache = [];
    static Dictionary<string, FieldInfo> FieldMap(Type t)
    {
        lock (fieldCache) { if (!fieldCache.TryGetValue(t, out var m)) fieldCache[t] = m = Fields(t).ToDictionary(Key, f => f); return m; }
    }

    /// <summary>Loads a snapshot into `root`, an object of the same shape that was built from the same definitions: the objects of the fresh engine are reused wherever the snapshot has an object at the same place, so their compiled parts stay.</summary>
    public static void Load(byte[] data, object root)
    {
        var rd = new Reader(data); var recs = rd.Records;
        var made = new object?[recs.Length];

        void Bind(int id, object existing)
        {
            if (made[id] is not null) return;
            var rec = recs[id];
            if (existing.GetType() != rec.Type) return;
            if (existing is Array ea && (ea.Rank != 1 || ea.Length != rec.Items.Length)) return;     // an array of another length is replaced, not filled
            made[id] = existing;
            switch (rec.Kind)
            {
                case KClass:
                    {
                        var map = FieldMap(rec.Type);
                        foreach (var (name, val) in rec.Fields)
                            if (val is RefVal rv && map.TryGetValue(name, out var f) && f.GetValue(existing) is { } child) Bind(rv.Id, child);
                        break;
                    }
                case KList when existing is IList list:
                    for (var i = 0; i < rec.Items.Length && i < list.Count; i++) if (rec.Items[i] is RefVal rv && list[i] is { } e) Bind(rv.Id, e);
                    break;
                case KArray when existing is Array arr && arr.Rank == 1:
                    for (var i = 0; i < rec.Items.Length && i < arr.Length; i++) if (rec.Items[i] is RefVal rv && arr.GetValue(i) is { } e) Bind(rv.Id, e);
                    break;
                case KDict when existing is IDictionary dict:
                    for (var i = 0; i + 1 < rec.Items.Length; i += 2)
                        if (rec.Items[i] is not RefVal && rec.Items[i + 1] is RefVal rv && rec.Items[i] is { } k && dict.Contains(k) && dict[k] is { } e) Bind(rv.Id, e);
                    break;
            }
        }
        Bind(0, root);

        object Create(Rec rec) => rec.Kind switch
        {
            KClass => RuntimeHelpers.GetUninitializedObject(rec.Type),
            KArray => Array.CreateInstance(rec.Type.GetElementType()!, rec.Items.Length),
            KGraph => new Graph(),
            KInterner => new Interner(),
            KDict or KSet when rec.Comparer != 0 => Activator.CreateInstance(rec.Type, MakeComparer(rec.Type, rec.Comparer))!,
            _ => Activator.CreateInstance(rec.Type)!,
        };
        for (var i = 0; i < recs.Length; i++) made[i] ??= Create(recs[i]);

        // `existing` is what the fresh object holds in that place: a struct whose configuration fields were not written (a compiled expression next to its state) keeps them
        object? Conv(object? v, object? existing = null)
        {
            switch (v)
            {
                case RefVal rv: return made[rv.Id];
                case StructVal sv:
                    {
                        var box = existing is not null && existing.GetType() == sv.Type ? existing : RuntimeHelpers.GetUninitializedObject(sv.Type); var map = FieldMap(sv.Type);
                        foreach (var (name, val) in sv.Fields) if (map.TryGetValue(name, out var f)) f.SetValue(box, Conv(val, f.GetValue(box)));
                        return box;
                    }
                default: return v;
            }
        }

        // objects and arrays first, then the collections that hash their keys (a key object must have its fields before it is added), then the graphs
        for (var pass = 0; pass < 2; pass++)
        for (var i = 0; i < recs.Length; i++)
        {
            var rec = recs[i]; var o = made[i]!;
            if ((rec.Kind is KDict or KSet) != (pass == 1)) continue;
            switch (rec.Kind)
            {
                case KInterner: ((Interner)o).ReplaceAll(rec.Strings!); break;
                case KGraph: break;      // after every other record is filled: the elements it holds are objects of this snapshot
                case KArray:
                    {
                        var a = (Array)o; var et = a.GetType().GetElementType()!;
                        if (rec.Raw is not null) { if (a is decimal[] da) rec.Raw.CopyTo(MemoryMarshal.AsBytes(da.AsSpan())); else Buffer.BlockCopy(rec.Raw, 0, a, 0, rec.Raw.Length); }
                        else for (var k = 0; k < rec.Items.Length; k++) a.SetValue(Conv(rec.Items[k], rec.Items[k] is StructVal && k < a.Length ? a.GetValue(k) : null), k);
                        _ = et;
                        break;
                    }
                case KList: { var l = (IList)o; l.Clear(); foreach (var x in rec.Items) l.Add(Conv(x)); break; }
                case KQueue: { var m = rec.Type.GetMethod("Enqueue")!; rec.Type.GetMethod("Clear")!.Invoke(o, null); foreach (var x in rec.Items) m.Invoke(o, [Conv(x)]); break; }
                case KStack: { var m = rec.Type.GetMethod("Push")!; rec.Type.GetMethod("Clear")!.Invoke(o, null); foreach (var x in rec.Items) m.Invoke(o, [Conv(x)]); break; }
                case KSet: { var m = rec.Type.GetMethod("Add")!; rec.Type.GetMethod("Clear")!.Invoke(o, null); foreach (var x in rec.Items) m.Invoke(o, [Conv(x)]); break; }
                case KDict: { var d = (IDictionary)o; d.Clear(); for (var k = 0; k < rec.Items.Length; k += 2) d.Add(Conv(rec.Items[k])!, Conv(rec.Items[k + 1])); break; }
                case KClass:
                    {
                        var map = FieldMap(rec.Type);
                        foreach (var (name, val) in rec.Fields) if (map.TryGetValue(name, out var f)) f.SetValue(o, Conv(val, val is StructVal ? f.GetValue(o) : null));
                        break;
                    }
            }
        }
        LoadGraphs(recs, made, v => Conv(v));
    }

    static void LoadGraphs(Rec[] recs, object?[] made, Func<object?, object?> conv)
    {
        for (var i = 0; i < recs.Length; i++)
        {
            if (recs[i].Kind != KGraph) continue;
            var g = (Graph)made[i]!; var edits = new List<Edit>(50_000);
            foreach (var item in recs[i].Items) { edits.Add(Edit.Create((Element)conv(item)!)); if (edits.Count == 50_000) { Apply(g, edits); edits.Clear(); } }
            Apply(g, edits);
        }
    }

    static void Apply(Graph g, List<Edit> edits)
    {
        if (edits.Count == 0) return;
        // entities before relationships: the list is written that way, and a chunk boundary never splits an endpoint from its relationship's turn
        var err = g.Apply(edits);
        if (err is not null) throw new InvalidDataException("graph in the snapshot does not apply: " + err);
    }
}
