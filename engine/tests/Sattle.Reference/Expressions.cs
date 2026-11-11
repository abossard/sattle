using System.Collections.Immutable;
using System.Numerics;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Reference;

/// <summary>Expressions of the closed function set (Def. 5.3), one row at a time. An absent operand makes the result absent, except in `IsPresent`, `Coalesce` and the branch of an `If` that is not taken. Integers are unbounded, decimals are .NET decimals (README, deviations).</summary>
public static class Expressions
{
    public static Value Eval(TExpr e, Func<string, Value> field, Identities? ids = null) => e switch
    {
        TField f => field(f.Name),
        TLit l => l.Value,
        TId id => ids is null ? throw new Undefined("an identity expression outside a sink") : KeyOf(id, field) is { } k ? Value.Entity(ids.IdOf(k)) : Value.Null,
        TCall c => Call(c, field, ids),
        _ => throw new Undefined($"expression {e.GetType().Name}"),
    };

    /// <summary>The identity key an `IdOf` denotes on a row; null when a component is absent or has no canonical form (the emit then skips the row, Def. 5.9).</summary>
    public static IdentityKey? KeyOf(TId id, Func<string, Value> field)
    {
        var comps = id.Comps.Select(c => Eval(c.Value, field) is TextValue t ? Identities.Canonicalize(c.Canonicalizer, t.S) : null).ToImmutableArray();
        return comps.Any(c => c is null) ? null : new IdentityKey(id.DomainId, comps!);
    }

    static Value Call(TCall c, Func<string, Value> field, Identities? ids)
    {
        Value Arg(int i) => Eval(c.Args[i], field, ids);
        switch (c.Fn)
        {
            case "IsPresent": return Value.Boolean(Arg(0) is not NullValue);
            case "Coalesce": return c.Args.Select((_, i) => Arg(i)).FirstOrDefault(v => v is not NullValue) is { } hit ? Typed(hit, c.Type.Ty) : Value.Null;
            case "If":
                {
                    var cond = Arg(0);
                    return cond is NullValue ? Value.Null : Typed(((BooleanValue)cond).B ? Arg(1) : Arg(2), c.Type.Ty);
                }
        }
        var args = c.Args.Select((_, i) => Arg(i)).ToArray();
        if (args.Any(a => a is NullValue)) return Value.Null;
        switch (c.Fn)
        {
            case "Equal": return Value.Boolean(Canon.Compare(args[0], args[1]) == 0);
            case "NotEqual": return Value.Boolean(Canon.Compare(args[0], args[1]) != 0);
            case "LessThan": return Value.Boolean(Canon.Compare(args[0], args[1]) < 0);
            case "LessOrEqual": return Value.Boolean(Canon.Compare(args[0], args[1]) <= 0);
            case "GreaterThan": return Value.Boolean(Canon.Compare(args[0], args[1]) > 0);
            case "GreaterOrEqual": return Value.Boolean(Canon.Compare(args[0], args[1]) >= 0);
            case "And": return Value.Boolean(args.All(a => ((BooleanValue)a).B));
            case "Or": return Value.Boolean(args.Any(a => ((BooleanValue)a).B));
            case "Not": return Value.Boolean(!((BooleanValue)args[0]).B);
            case "Add" or "Subtract" or "Multiply" when c.Type.Ty.Tag == Tag.Integer:
                {
                    BigInteger x = Canon.Integer(args[0]), y = Canon.Integer(args[1]);
                    return Value.Integer(c.Fn switch { "Add" => x + y, "Subtract" => x - y, _ => x * y });
                }
            case "Add" or "Subtract" or "Multiply":
                {
                    decimal x = Canon.Number(args[0]), y = Canon.Number(args[1]);
                    try { return Value.Decimal(c.Fn switch { "Add" => x + y, "Subtract" => x - y, _ => x * y }); }
                    catch (OverflowException) { return Value.Null; }
                }
            case "Divide":
                {
                    decimal x = Canon.Number(args[0]), y = Canon.Number(args[1]);
                    if (y == 0) return Value.Null;
                    try { return Value.Decimal(x / y); } catch (OverflowException) { return Value.Null; }
                }
            case "Negate": return args[0] is IntegerValue n ? Value.Integer(-n.N) : Value.Decimal(-Canon.Number(args[0]));
            case "ToDecimal": return Value.Decimal(Canon.Number(args[0]));
            case "Rate": return Value.Decimal(Canon.Number(args[0]) / (c.Args[0].Type.Role.Period / 1_000_000_000m));
            case "Concat": return Value.Text(string.Concat(args.Select(a => ((TextValue)a).S)));
            case "LowerInvariant": return Value.Text(((TextValue)args[0]).S.ToLowerInvariant());
            case "Trim": return Value.Text(((TextValue)args[0]).S.Trim());
            case "EntityId": return Value.Text(((EntityValue)args[0]).Id);
            default: throw new Undefined($"function {c.Fn}");
        }
    }

    /// <summary>No numeric widening except where a Decimal result is declared for an Integer branch (Def. 5.4).</summary>
    static Value Typed(Value v, Ty declared) => declared.Tag == Tag.Decimal && v is IntegerValue i ? Value.Decimal((decimal)i.N) : v;
}
