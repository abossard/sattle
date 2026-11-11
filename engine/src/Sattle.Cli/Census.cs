using System.Reflection;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>Retained size of the engine by structure, measured by walking the object graph with reflection (no heap profiler on the machine). Sizes are estimates from the type layout (object header 16 bytes, fields, array and string payloads, 8-byte alignment); the total is compared with the GC's own count. Every object is counted once, for the first root that reaches it, so the order of the roots matters: shared lookups first, operator state last.</summary>
static class Census
{
    sealed class Acc { public long Objects, Bytes; }
    static readonly Dictionary<Type, FieldInfo[]> fieldCache = [];
    static readonly Dictionary<Type, int> sizeCache = [];
    static readonly Dictionary<Type, bool> refCache = [];

    static FieldInfo[] Fields(Type t)
    {
        if (fieldCache.TryGetValue(t, out var f)) return f;
        var list = new List<FieldInfo>();
        for (var c = t; c is not null && c != typeof(object); c = c.BaseType)
            list.AddRange(c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        return fieldCache[t] = [.. list];
    }

    static int PrimSize(Type t) => Type.GetTypeCode(t) switch
    {
        TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte => 1,
        TypeCode.Char or TypeCode.Int16 or TypeCode.UInt16 => 2,
        TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 4,
        TypeCode.Decimal => 16,
        _ => 8,
    };

    /// <summary>The size of a value of type t inside an object or array.</summary>
    static int ValueSize(Type t)
    {
        if (!t.IsValueType) return 8;
        if (sizeCache.TryGetValue(t, out var s)) return s;
        if (t.IsPrimitive) return sizeCache[t] = PrimSize(t);
        if (t.IsEnum) return sizeCache[t] = PrimSize(Enum.GetUnderlyingType(t));
        if (t == typeof(decimal)) return sizeCache[t] = 16;
        var sum = 0; var max = 1;
        foreach (var f in Fields(t)) { var fs = ValueSize(f.FieldType); sum += fs; max = Math.Max(max, Math.Min(8, fs)); }
        sum = (sum + max - 1) / max * max;
        return sizeCache[t] = Math.Max(sum, 1);
    }

    static bool HasRefs(Type t)
    {
        if (!t.IsValueType) return true;
        if (t.IsPrimitive || t.IsEnum || t == typeof(decimal)) return false;
        if (refCache.TryGetValue(t, out var b)) return b;
        refCache[t] = false;
        return refCache[t] = Fields(t).Any(f => HasRefs(f.FieldType));
    }

    static long Align(long n) => (n + 7) / 8 * 8;

    static long ObjectSize(object o)
    {
        var t = o.GetType();
        if (o is string s) return Align(22 + 2L * s.Length);
        if (o is Array a) return Align(24 + (long)a.Length * ValueSize(t.GetElementType()!));
        var inst = 16L + Fields(t).Sum(f => (long)ValueSize(f.FieldType));
        return Math.Max(24, Align(inst));
    }

    static bool Skip(Type t) => (t.Namespace is { } ns && ns.StartsWith("System.Reflection", StringComparison.Ordinal)) || typeof(Type).IsAssignableFrom(t) || t == typeof(Thread) || typeof(System.Runtime.InteropServices.SafeHandle).IsAssignableFrom(t) || t.Name is "RuntimeType" or "RuntimeTypeHandle";

    static string TypeName(Type t) => t.IsArray ? TypeName(t.GetElementType()!) + "[]" : t.IsGenericType ? t.Name + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">" : t.Name;

    sealed class Walker
    {
        public readonly HashSet<object> Visited = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<string, Acc> ByLabel = [], ByGraph = [], ByType = [];
        public readonly Dictionary<(string, string), Acc> ByLabelType = [];
        readonly Stack<object> stack = new();
        string label = "", graph = "";

        public void Root(string lbl, string g, object? root)
        {
            if (root is null) return;
            label = lbl; graph = g;
            Push(root);
            while (stack.Count > 0) Visit(stack.Pop());
        }

        void Push(object? o)
        {
            if (o is null) return;
            var t = o.GetType();
            if (t.IsValueType) { stack.Push(o); return; }                       // a boxed struct: its references only
            if (Skip(t)) return;
            if (Visited.Add(o)) stack.Push(o);
        }

        static void Bump(Dictionary<string, Acc> d, string k, long size) { if (!d.TryGetValue(k, out var a)) d[k] = a = new Acc(); a.Objects++; a.Bytes += size; }

        void Visit(object o)
        {
            var t = o.GetType();
            if (!t.IsValueType)
            {
                var size = ObjectSize(o); var tn = TypeName(t);
                Bump(ByLabel, label, size); Bump(ByGraph, graph, size); Bump(ByType, tn, size);
                var key = (label, tn); if (!ByLabelType.TryGetValue(key, out var a)) ByLabelType[key] = a = new Acc(); a.Objects++; a.Bytes += size;
            }
            if (t.IsArray)
            {
                if (!HasRefs(t.GetElementType()!)) return;
                if (o is object?[] oa) { foreach (var x in oa) Push(x); return; }
                foreach (var x in (System.Collections.IEnumerable)o) Push(x);       // boxes structs; their reference fields are walked below
                return;
            }
            if (t == typeof(string)) return;
            if (typeof(Delegate).IsAssignableFrom(t)) return;                     // a callback is its own size; following its target would pull the engine into whatever holds a callback
            foreach (var f in Fields(t))
            {
                if (!HasRefs(f.FieldType)) continue;
                object? v; try { v = f.GetValue(o); } catch { continue; }
                Push(v);
            }
        }
    }

    /// <summary>The roots of an engine: shared lookups first, then the stores, the state of every graph, the output layers of every graph (each field on its own), the operator state by operator kind.</summary>
    public static List<(string Label, string Graph, object? Root)> EngineRoots(Engine e, params (string Label, object? Root)[] extra)
    {
        var roots = new List<(string, string, object?)>
        {
            ("interner", "-", e.Interner), ("identities", "-", e.Identities), ("telemetry", "-", e.Telemetry),
        };
        foreach (var (n, v) in Expand("store", e.Store))
        {
            if (n == "store.graphs" && v is System.Collections.IDictionary gd)
            {
                foreach (System.Collections.DictionaryEntry kv in gd) foreach (var (n2, v2) in Expand("store.graph", kv.Value!)) roots.Add((n2, kv.Key.ToString() ?? "-", v2));
                roots.Add(("store.graphs (dictionary itself)", "-", v));
            }
            else roots.Add((n, "-", v));
        }
        foreach (var g in e.Graphs) foreach (var (n, v) in Expand("graph.state", g.State)) roots.Add((n, g.Name, v));
        foreach (var g in e.Graphs) foreach (var (n, v) in Expand("graph.sink", g.Out)) roots.Add((n, g.Name, v));
        foreach (var g in e.Graphs) foreach (var op in g.Plan.Ops) roots.Add(("op:" + op.GetType().Name, g.Name, op));
        foreach (var (l, r) in extra) roots.Add((l, "-", r));
        roots.Add(("engine.rest", "-", e));
        foreach (var asm in new[] { typeof(Engine).Assembly, typeof(Sattle.Core.Kernel).Assembly, typeof(Sattle.Language.Catalog).Assembly })
            foreach (var t in asm.GetTypes().Where(t => !t.IsGenericTypeDefinition))
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(f => !f.IsLiteral && HasRefs(f.FieldType)))
                { object? v; try { v = f.GetValue(null); } catch { continue; } if (v is not null) roots.Add(("statics", "-", v)); }
        return roots;
    }

    /// <summary>One root per field of the object, so a structure is named by its field.</summary>
    static IEnumerable<(string, object?)> Expand(string prefix, object o)
    {
        foreach (var f in Fields(o.GetType()))
        {
            if (!HasRefs(f.FieldType)) continue;
            object? v; try { v = f.GetValue(o); } catch { continue; }
            if (v is not null) yield return (prefix + "." + f.Name, v);
        }
    }

    /// <summary>Live bytes after a forced compacting collection (what the last collection promoted); GC.GetTotalMemory reads higher on the same heap.</summary>
    public static long LiveBytes()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
        return GC.GetGCMemoryInfo().PromotedBytes;
    }

    public static void Run(string title, IReadOnlyList<(string Label, string Graph, object? Root)> roots, long masterEntities, long masterRels, TextWriter w)
    {
        var live = LiveBytes();
        var gc = GC.GetTotalMemory(true);
        var wk = new Walker();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (l, g, r) in roots) wk.Root(l, g, r);
        var total = wk.ByLabel.Values.Sum(a => a.Bytes); var objects = wk.ByLabel.Values.Sum(a => a.Objects);
        w.WriteLine($"## census {title}: {objects:N0} objects, {total / 1e6:F0} MB estimated from layouts, live bytes after a forced compacting collection {live / 1e6:F0} MB (coverage {100.0 * total / Math.Max(1, live):F0} %), GC.GetTotalMemory {gc / 1e6:F0} MB, walk {sw.Elapsed.TotalSeconds:F0} s; master {masterEntities:N0} entities, {masterRels:N0} relationships");
        w.WriteLine("structure | objects | MB | bytes per master node | top types");
        foreach (var (l, a) in wk.ByLabel.OrderByDescending(x => x.Value.Bytes).Take(40))
        {
            var tops = wk.ByLabelType.Where(x => x.Key.Item1 == l).OrderByDescending(x => x.Value.Bytes).Take(3).Select(x => $"{x.Key.Item2} {x.Value.Objects:N0}x/{x.Value.Bytes / 1e6:F0}MB");
            w.WriteLine($"{l} | {a.Objects:N0} | {a.Bytes / 1e6:F1} | {a.Bytes / (double)Math.Max(1, masterEntities):F0} | {string.Join("; ", tops)}");
        }
        w.WriteLine("graph | objects | MB (state, sink layers and operator state of the graph)");
        foreach (var (l, a) in wk.ByGraph.OrderByDescending(x => x.Value.Bytes)) w.WriteLine($"{l} | {a.Objects:N0} | {a.Bytes / 1e6:F1}");
        w.WriteLine("type | objects | MB (top 40 by size)");
        foreach (var (l, a) in wk.ByType.OrderByDescending(x => x.Value.Bytes).Take(40)) w.WriteLine($"{l} | {a.Objects:N0} | {a.Bytes / 1e6:F1}");
    }
}
