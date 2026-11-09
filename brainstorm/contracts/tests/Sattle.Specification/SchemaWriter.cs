using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sattle.Model;
using Sattle.Projections;
using Sattle.Documents;
using Sattle.Runtime;
namespace Sattle.Specification;

// Test tooling only: mechanically derives wire shape from the actual C# DTOs.
internal sealed class SchemaWriter
{
    readonly JsonObject definitions = new();
    readonly HashSet<Type> active = [];
    readonly NullabilityInfoContext nullability = new();
    static string Name(Type t) => t.Name.Split('`')[0];
    static string Camel(string s) => JsonNamingPolicy.CamelCase.ConvertName(s);
    JsonNode Ref(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying is not null) return new JsonObject { ["anyOf"] = new JsonArray(Ref(underlying), new JsonObject { ["type"] = "null" }) };
        if (t == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (t == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (t == typeof(decimal) || t == typeof(double)) return new JsonObject { ["type"] = "number" };
        if (t.IsPrimitive) return new JsonObject { ["type"] = "integer" };
        if (t.IsEnum) return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(t).Select(x => (JsonNode?)JsonValue.Create(Camel(x))).ToArray()) };
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            return new JsonObject { ["type"] = "array", ["items"] = Ref(t.GetGenericArguments()[0]) };
        var name = Name(t);
        if (active.Add(t))
        {
            var derived = t.GetCustomAttributes<JsonDerivedTypeAttribute>().ToArray();
            if (derived.Length > 0)
                definitions[name] = new JsonObject { ["oneOf"] = new JsonArray(derived.Select(x => Ref(x.DerivedType)).ToArray()) };
            else
            {
                var props = new JsonObject(); var required = new JsonArray();
                if (t.BaseType is { } parent)
                {
                    var tag = parent.GetCustomAttributes<JsonDerivedTypeAttribute>().FirstOrDefault(x => x.DerivedType == t);
                    if (tag is not null) { props["kind"] = new JsonObject { ["const"] = JsonValue.Create((string)tag.TypeDiscriminator!) }; required.Add("kind"); }
                }
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var pn = Camel(p.Name); var shape = Ref(p.PropertyType);
                    if (!p.PropertyType.IsValueType && nullability.Create(p).ReadState == NullabilityState.Nullable)
                        shape = new JsonObject { ["anyOf"] = new JsonArray(shape, new JsonObject { ["type"] = "null" }) };
                    if (p.PropertyType == typeof(string) && p.Name == "Value" && t.IsValueType) shape["minLength"] = 1;
                    if (t == typeof(IntegerValue) && p.Name == "Value" || t == typeof(WeightedFact) && p.Name == "Weight") shape["pattern"] = "^(0|-?[1-9][0-9]*)$";
                    if (t == typeof(DecimalValue) && p.Name == "Value") shape["pattern"] = "^-?(0|[1-9][0-9]*)(\\.[0-9]+)?$";
                    if (t == typeof(InstantValue) && p.Name == "Value") shape["format"] = "date-time";
                    if (p.Name == "SchemaVersion") shape["const"] = "1.0";
                    if (t == typeof(Heading) && p.Name == "Level") { shape["minimum"] = 1; shape["maximum"] = 6; }
                    if (t == typeof(ProjectionDefinition) && p.Name == "RetainedInputsPerElement") shape["minimum"] = 0;
                    if (p.Name is "MaximumFacts" or "MaximumIterations" or "MaximumBlocks" or "MaximumSteps") shape["minimum"] = 1;
                    props[pn] = shape; required.Add(pn);
                }
                definitions[name] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false };
            }
        }
        return new JsonObject { ["$ref"] = "#/$defs/" + name };
    }
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        Type[] roots = [typeof(GraphPatch), typeof(SignedDelta), typeof(OntologyDefinition), typeof(ProjectionDefinition),
            typeof(DocumentProjectionDefinition), typeof(DocumentSnapshot), typeof(DocumentPatch), typeof(GraphDescriptor), typeof(IdentityEntry), typeof(AlgebraRequest), typeof(EngineInput), typeof(GraphSpaceDefinition)];
        foreach (var root in roots)
        {
            var w = new SchemaWriter(); var reference = w.Ref(root);
            var schema = new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["title"] = Name(root),
                ["$ref"] = reference["$ref"]!.GetValue<string>(), ["$defs"] = w.definitions };
            File.WriteAllText(Path.Combine(directory, Name(root) + ".schema.json"), schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
