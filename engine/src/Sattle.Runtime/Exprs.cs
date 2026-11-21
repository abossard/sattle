using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Counts runtime conditions that cannot be checked statically (section 3.5): division by zero, overflow.</summary>
public sealed class RuntimeFaults
{
    public long DivisionByZero;
    /// <summary>Rows without a value in a column an operator requires (the time of a `retain`): they are dropped.</summary>
    public long MissingRequired;
    /// <summary>Rows that give a Threshold parameters that are no state machine (`above` not greater than `below`, a run of less than one window): they are dropped.</summary>
    public long InvalidParameter;
    public long Overflow;
}

/// <summary>A compiled expression: evaluates a whole batch column-at-a-time (vectorized interpreter, section 11.1 step 4). The returned column is valid until the next Eval of this node.</summary>
public abstract class VExpr
{
    public bool IsDec { get; protected init; }
    protected Column Out = null!;
    public abstract Column Eval(Batch b);
    /// <summary>The expression's output column for a batch of n rows, every row present. Its arrays, the presence array too, are kept from call to call: the column is valid until the next evaluation.</summary>
    protected Column Scratch(int n)
    {
        Out ??= new Column(IsDec);
        Out.Ensure(n);
        if (Out.P is not null) Array.Fill(Out.P, true, 0, n);
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
                        "Equal" or "NotEqual" or "LessThan" or "LessOrEqual" or "GreaterThan" or "GreaterOrEqual" => new VCmp(c.Fn, args[0], args[1], tags[0] == Tag.Decimal || tags[1] == Tag.Decimal),
                        "And" or "Or" => new VLogic(c.Fn, args),
                        "Not" => new VLogic("Not", args),
                        "Add" or "Subtract" or "Multiply" => new VArith(c.Fn, args[0], args[1], c.Type.Ty.Tag == Tag.Decimal, faults),
                        "Divide" => new VArith("Divide", args[0], args[1], true, faults),
                        "Shift" => new VArith("Add", args[0], args[1], false, faults),
                        "Negate" => new VArith("Negate", args[0], args[0], c.Type.Ty.Tag == Tag.Decimal, faults),
                        "ToDecimal" => new VToDecimal(args[0]),
                        "If" => new VIf(args[0], args[1], args[2], c.Type.Ty.Tag == Tag.Decimal),
                        "Coalesce" => new VCoalesce(args, c.Type.Ty.Tag == Tag.Decimal),
                        "IsPresent" => new VIsPresent(args[0]),
                        "Rate" => new VRate(args[0], c.Args[0].Type.Role.Period),
                        "StartsWith" => new VTextPredicate(args[0], args[1], it, (s, term) => s.StartsWith(term, StringComparison.OrdinalIgnoreCase)),
                        "EndsWith" => new VTextPredicate(args[0], args[1], it, (s, term) => s.EndsWith(term, StringComparison.OrdinalIgnoreCase)),
                        "Contains" => new VTextPredicate(args[0], args[1], it, (s, term) => s.Contains(term, StringComparison.OrdinalIgnoreCase)),
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
    public VLit(Value v, Interner it)
    {
        if (v is DecimalValue q) { IsDec = true; d = q.Q; }
        else if (v is NullValue) isNull = true;
        else l = Codec.ToCode(v, it);
    }
    /// <summary>Filled on every call: the column's arrays are working arrays that another expression may have used since (`WorkingArrays`).</summary>
    public override Column Eval(Batch b)
    {
        var o = Scratch(b.N);
        if (IsDec) Array.Fill(o.D, d, 0, b.N); else Array.Fill(o.L, l, 0, b.N);
        if (isNull) for (var i = 0; i < Math.Max(b.N, 1); i++) Absent(o, i, Math.Max(b.N, 1));
        return o;
    }
}

/// <summary>Texts are compared only for equality: their codes say whether two are equal.</summary>
sealed class VCmp(string fn, VExpr a, VExpr b, bool dec) : VExpr
{
    readonly bool decCompare = dec;
    public override Column Eval(Batch batch)
    {
        var x = a.Eval(batch); var y = b.Eval(batch);
        var n = batch.N;
        var o = Scratch(n);
        for (var i = 0; i < n; i++)
        {
            if ((x.P is not null && !x.P[i]) || (y.P is not null && !y.P[i])) { Absent(o, i, n); continue; }
            int c;
            if (decCompare)
            {
                var xv = x.IsDec ? x.D[i] : x.L[i]; var yv = y.IsDec ? y.D[i] : y.L[i];
                c = xv.CompareTo(yv);
            }
            else c = x.L[i].CompareTo(y.L[i]);
            o.L[i] = fn switch { "Equal" => c == 0, "NotEqual" => c != 0, "LessThan" => c < 0, "LessOrEqual" => c <= 0, "GreaterThan" => c > 0, _ => c >= 0 } ? 1 : 0;
        }
        return o;
    }
}

sealed class VLogic(string fn, VExpr[] args) : VExpr
{
    readonly Column[] cols = new Column[args.Length];
    public override Column Eval(Batch batch)
    {
        var n = batch.N;
        var o = Scratch(n);
        for (var a = 0; a < args.Length; a++) cols[a] = args[a].Eval(batch);
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
    readonly VExpr[] args; readonly Column[] cols;
    public VCoalesce(VExpr[] args, bool dec) { this.args = args; IsDec = dec; cols = new Column[args.Length]; }
    public override Column Eval(Batch batch)
    {
        for (var a = 0; a < args.Length; a++) cols[a] = args[a].Eval(batch);
        var n = batch.N; var o = Scratch(n);
        for (var i = 0; i < n; i++)
        {
            Column? hit = null;
            foreach (var c in cols) if (c.P is null || c.P[i]) { hit = c; break; }
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
        var x = a.Eval(batch); var o = Scratch(batch.N);
        for (var i = 0; i < batch.N; i++) o.L[i] = x.P is null || x.P[i] ? 1 : 0;
        return o;
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

sealed class VTextPredicate(VExpr text, VExpr term, Interner interner, Func<string, string, bool> matches) : VExpr
{
    public override Column Eval(Batch batch)
    {
        var values = text.Eval(batch);
        var terms = term.Eval(batch);
        var result = Scratch(batch.N);
        for (var i = 0; i < batch.N; i++)
        {
            if ((values.P is not null && !values.P[i]) || (terms.P is not null && !terms.P[i]))
            {
                Absent(result, i, batch.N);
                continue;
            }
            result.L[i] = matches(interner.Get((int)values.L[i]), interner.Get((int)terms.L[i])) ? 1 : 0;
        }
        return result;
    }
}

sealed class VText : VExpr
{
    readonly string fn; readonly VExpr[] args; readonly Interner it; readonly Column[] cols;
    readonly CodeCache cache = new();
    public VText(string fn, VExpr[] args, Interner it) { this.fn = fn; this.args = args; this.it = it; cols = new Column[args.Length]; }
    public override Column Eval(Batch batch)
    {
        for (var a = 0; a < args.Length; a++) cols[a] = args[a].Eval(batch);
        var n = batch.N; var o = Scratch(n);
        for (var i = 0; i < n; i++)
        {
            var absent = false;
            foreach (var c in cols) if (c.P is not null && !c.P[i]) { absent = true; break; }
            if (absent) { Absent(o, i, n); continue; }
            if (fn == "Concat") { o.L[i] = it.InternComputed(string.Concat(cols.Select(c => it.Get((int)c.L[i])))); continue; }
            var code = (int)cols[0].L[i];
            if (!cache.TryGet(code, out var r))
            {
                var s = it.Get(code); r = it.InternComputed(fn == "Trim" ? s.Trim() : s.ToLowerInvariant());
                if (!it.IsYoung(code) && !it.IsYoung(r)) cache.Set(code, r);         // a young code may be freed and given to another text: only old to old is kept
            }
            o.L[i] = r;
        }
        return o;
    }
}
