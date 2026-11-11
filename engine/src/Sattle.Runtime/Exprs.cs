using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Counts runtime conditions that cannot be checked statically (section 3.5): division by zero, overflow.</summary>
public sealed class RuntimeFaults
{
    public long DivisionByZero;
    /// <summary>Rows without a value in a column an operator requires (the time of a `retain`): they are dropped.</summary>
    public long MissingRequired;
    public long Overflow;
}

/// <summary>A compiled expression: evaluates a whole batch column-at-a-time (vectorized interpreter, section 11.1 step 4). The returned column is valid until the next Eval of this node.</summary>
public abstract class VExpr
{
    public bool IsDec { get; protected init; }
    protected Column Out = null!;
    public abstract Column Eval(Batch b);
    protected Column Scratch(int n)
    {
        Out ??= new Column(IsDec);
        Out.Ensure(n);
        Out.P = null;
        return Out;
    }
    /// <summary>Marks row i absent in the scratch column.</summary>
    protected static void Absent(Column c, int i, int n)
    {
        if (c.P is null) { c.P = new bool[Math.Max(n, c.IsDec ? c.D.Length : c.L.Length)]; Array.Fill(c.P, true); }
        else if (c.P.Length < n) { var np = new bool[n]; Array.Copy(c.P, np, c.P.Length); for (var k = c.P.Length; k < n; k++) np[k] = true; c.P = np; }
        c.P[i] = false;
    }
    protected static bool AllPresent(Column a) => a.P is null;
}

public static class ExprCompiler
{
    public static VExpr Compile(TExpr e, Layout layout, Interner it, RuntimeFaults faults, Func<TId, VExpr>? idCompiler = null)
    {
        switch (e)
        {
            case TField f: return new VField(layout.Index(f.Name), f.Type.Ty.Tag == Tag.Decimal);
            case TLit l: return new VLit(l.Value, it);
            case TId id when idCompiler is not null: return idCompiler(id);
            case TId: throw new InvalidOperationException("identity expressions are compiled by the plan");
            case TCall c:
                {
                    var args = c.Args.Select(a => Compile(a, layout, it, faults, idCompiler)).ToArray();
                    var tags = c.Args.Select(a => a.Type.Ty.Tag).ToArray();
                    return c.Fn switch
                    {
                        "Equal" or "NotEqual" => new VCmp(c.Fn, args[0], args[1], tags[0] == Tag.Decimal || tags[1] == Tag.Decimal, null),
                        "LessThan" or "LessOrEqual" or "GreaterThan" or "GreaterOrEqual" => new VCmp(c.Fn, args[0], args[1], tags[0] == Tag.Decimal || tags[1] == Tag.Decimal, tags[0] is Tag.Text or Tag.Ent ? it : null),
                        "And" or "Or" => new VLogic(c.Fn, args),
                        "Not" => new VLogic("Not", args),
                        "Add" or "Subtract" or "Multiply" => new VArith(c.Fn, args[0], args[1], c.Type.Ty.Tag == Tag.Decimal, faults),
                        "Divide" => new VArith("Divide", args[0], args[1], true, faults),
                        "Negate" => new VArith("Negate", args[0], args[0], c.Type.Ty.Tag == Tag.Decimal, faults),
                        "ToDecimal" => new VToDecimal(args[0]),
                        "If" => new VIf(args[0], args[1], args[2], c.Type.Ty.Tag == Tag.Decimal),
                        "Coalesce" => new VCoalesce(args, c.Type.Ty.Tag == Tag.Decimal),
                        "IsPresent" => new VIsPresent(args[0]),
                        "Rate" => new VRate(args[0], c.Args[0].Type.Role.Period),
                        "LowerInvariant" or "Trim" or "Concat" => new VText(c.Fn, args, it),
                        "EntityId" => args[0],            // an entity column holds the handle of its id text: the same column read as Text
                        _ => throw new NotSupportedException($"function {c.Fn} has no vectorized implementation"),
                    };
                }
            default: throw new InvalidOperationException();
        }
    }
}

sealed class VField : VExpr
{
    public int Index { get; }
    public VField(int index, bool isDec) { Index = index; IsDec = isDec; }
    public override Column Eval(Batch b) => b.Cols[Index];
}

sealed class VLit : VExpr
{
    readonly long l; readonly decimal d; readonly bool isNull;
    int filled = -1;
    public VLit(Value v, Interner it)
    {
        if (v is DecimalValue q) { IsDec = true; d = q.Q; }
        else if (v is NullValue) isNull = true;
        else l = Codec.ToCode(v, it);
    }
    public override Column Eval(Batch b)
    {
        var o = Scratch(b.N);
        if (filled >= b.N && !isNull) return o;
        if (IsDec) Array.Fill(o.D, d, 0, b.N); else Array.Fill(o.L, l, 0, b.N);
        if (isNull) { o.P = new bool[Math.Max(b.N, 1)]; }
        filled = b.N;
        return o;
    }
}

/// <summary>`text`: set when text is ordered. A code says whether two texts are equal, not which comes first, so an order comparison reads the texts.</summary>
sealed class VCmp(string fn, VExpr a, VExpr b, bool dec, Interner? text) : VExpr
{
    readonly bool decCompare = dec;
    public override Column Eval(Batch batch)
    {
        var x = a.Eval(batch); var y = b.Eval(batch);
        var n = batch.N;
        Out ??= new Column(false); Out.Ensure(n); Out.P = null;
        var o = Out;
        for (var i = 0; i < n; i++)
        {
            if ((x.P is not null && !x.P[i]) || (y.P is not null && !y.P[i])) { Absent(o, i, n); continue; }
            int c;
            if (decCompare)
            {
                var xv = x.IsDec ? x.D[i] : x.L[i]; var yv = y.IsDec ? y.D[i] : y.L[i];
                c = xv.CompareTo(yv);
            }
            else if (text is not null) c = string.CompareOrdinal(text.Get((int)x.L[i]), text.Get((int)y.L[i]));
            else c = x.L[i].CompareTo(y.L[i]);
            o.L[i] = fn switch { "Equal" => c == 0, "NotEqual" => c != 0, "LessThan" => c < 0, "LessOrEqual" => c <= 0, "GreaterThan" => c > 0, _ => c >= 0 } ? 1 : 0;
        }
        return o;
    }
}

sealed class VLogic(string fn, VExpr[] args) : VExpr
{
    public override Column Eval(Batch batch)
    {
        var n = batch.N;
        Out ??= new Column(false); Out.Ensure(n); Out.P = null;
        var o = Out;
        var cols = args.Select(x => x.Eval(batch)).ToArray();
        for (var i = 0; i < n; i++)
        {
            var absent = false; var all = fn != "Or"; var any = false;
            foreach (var c in cols)
            {
                if (c.P is not null && !c.P[i]) { absent = true; break; }
                var v = c.L[i] != 0;
                all &= v; any |= v;
            }
            if (absent) { Absent(o, i, n); continue; }
            o.L[i] = fn == "Not" ? (cols[0].L[i] == 0 ? 1 : 0) : fn == "And" ? (all ? 1 : 0) : (any ? 1 : 0);
        }
        return o;
    }
}

sealed class VArith : VExpr
{
    readonly string fn; readonly VExpr a, b; readonly RuntimeFaults faults;
    public VArith(string fn, VExpr a, VExpr b, bool dec, RuntimeFaults faults) { this.fn = fn; this.a = a; this.b = b; IsDec = dec; this.faults = faults; }
    public override Column Eval(Batch batch)
    {
        var x = a.Eval(batch); var y = fn == "Negate" ? x : b.Eval(batch);
        var n = batch.N;
        var o = Scratch(n);
        for (var i = 0; i < n; i++)
        {
            if ((x.P is not null && !x.P[i]) || (y.P is not null && !y.P[i])) { Absent(o, i, n); continue; }
            if (IsDec)
            {
                var xv = x.IsDec ? x.D[i] : x.L[i]; var yv = y.IsDec ? y.D[i] : y.L[i];
                try
                {
                    switch (fn)
                    {
                        case "Add": o.D[i] = xv + yv; break;
                        case "Subtract": o.D[i] = xv - yv; break;
                        case "Multiply": o.D[i] = xv * yv; break;
                        case "Negate": o.D[i] = -xv; break;
                        default:
                            if (yv == 0) { faults.DivisionByZero++; Absent(o, i, n); } else o.D[i] = xv / yv;
                            break;
                    }
                }
                catch (OverflowException) { faults.Overflow++; Absent(o, i, n); }
            }
            else
            {
                try
                {
                    o.L[i] = fn switch { "Add" => checked(x.L[i] + y.L[i]), "Subtract" => checked(x.L[i] - y.L[i]), "Multiply" => checked(x.L[i] * y.L[i]), _ => -x.L[i] };
                }
                catch (OverflowException) { faults.Overflow++; Absent(o, i, n); }
            }
        }
        return o;
    }
}

sealed class VToDecimal : VExpr
{
    readonly VExpr a;
    public VToDecimal(VExpr a) { this.a = a; IsDec = true; }
    public override Column Eval(Batch batch)
    {
        var x = a.Eval(batch); var o = Scratch(batch.N);
        for (var i = 0; i < batch.N; i++) { if (x.P is not null && !x.P[i]) { Absent(o, i, batch.N); continue; } o.D[i] = x.L[i]; }
        return o;
    }
}

sealed class VIf : VExpr
{
    readonly VExpr c, t, e;
    public VIf(VExpr c, VExpr t, VExpr e, bool dec) { this.c = c; this.t = t; this.e = e; IsDec = dec; }
    public override Column Eval(Batch batch)
    {
        var cc = c.Eval(batch); var tt = t.Eval(batch); var ee = e.Eval(batch); var n = batch.N;
        var o = Scratch(n);
        for (var i = 0; i < n; i++)
        {
            if (cc.P is not null && !cc.P[i]) { Absent(o, i, n); continue; }
            var src = cc.L[i] != 0 ? tt : ee;
            if (src.P is not null && !src.P[i]) { Absent(o, i, n); continue; }
            if (IsDec) o.D[i] = src.IsDec ? src.D[i] : src.L[i]; else o.L[i] = src.L[i];
        }
        return o;
    }
}

sealed class VCoalesce : VExpr
{
    readonly VExpr[] args;
    public VCoalesce(VExpr[] args, bool dec) { this.args = args; IsDec = dec; }
    public override Column Eval(Batch batch)
    {
        var cols = args.Select(a => a.Eval(batch)).ToArray(); var n = batch.N; var o = Scratch(n);
        for (var i = 0; i < n; i++)
        {
            var hit = cols.FirstOrDefault(c => c.P is null || c.P[i]);
            if (hit is null) { Absent(o, i, n); continue; }
            if (IsDec) o.D[i] = hit.IsDec ? hit.D[i] : hit.L[i]; else o.L[i] = hit.L[i];
        }
        return o;
    }
}

sealed class VIsPresent(VExpr a) : VExpr
{
    public override Column Eval(Batch batch)
    {
        var x = a.Eval(batch); Out ??= new Column(false); Out.Ensure(batch.N); Out.P = null;
        for (var i = 0; i < batch.N; i++) Out.L[i] = x.P is null || x.P[i] ? 1 : 0;
        return Out;
    }
}

/// <summary>Rate: a delta value over its window, in units per second. Decimal division by the period in seconds.</summary>
sealed class VRate : VExpr
{
    readonly VExpr a; readonly decimal seconds;
    public VRate(VExpr a, long periodNs) { this.a = a; IsDec = true; seconds = periodNs / 1_000_000_000m; }
    public override Column Eval(Batch batch)
    {
        var x = a.Eval(batch); var o = Scratch(batch.N);
        for (var i = 0; i < batch.N; i++) { if (x.P is not null && !x.P[i]) { Absent(o, i, batch.N); continue; } o.D[i] = (x.IsDec ? x.D[i] : x.L[i]) / seconds; }
        return o;
    }
}

sealed class VText : VExpr
{
    readonly string fn; readonly VExpr[] args; readonly Interner it;
    readonly Dictionary<int, int> cache = [];
    public VText(string fn, VExpr[] args, Interner it) { this.fn = fn; this.args = args; this.it = it; }
    public override Column Eval(Batch batch)
    {
        var cols = args.Select(a => a.Eval(batch)).ToArray(); var n = batch.N; var o = Scratch(n);
        for (var i = 0; i < n; i++)
        {
            if (cols.Any(c => c.P is not null && !c.P[i])) { Absent(o, i, n); continue; }
            if (fn == "Concat") { o.L[i] = it.Intern(string.Concat(cols.Select(c => it.Get((int)c.L[i])))); continue; }
            var code = (int)cols[0].L[i];
            if (!cache.TryGetValue(code, out var r)) { var s = it.Get(code); cache[code] = r = it.Intern(fn == "Trim" ? s.Trim() : s.ToLowerInvariant()); }
            o.L[i] = r;
        }
        return o;
    }
}
