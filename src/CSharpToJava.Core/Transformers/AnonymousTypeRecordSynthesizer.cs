using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers;

/// <summary>
/// Describes a single field of a synthesized Java record generated from a C# anonymous type.
/// </summary>
public sealed class SynthesizedRecordField
{
    public string Name { get; }
    public string JavaType { get; }

    public SynthesizedRecordField(string name, string javaType)
    {
        Name = name;
        JavaType = javaType;
    }
}

/// <summary>
/// A synthesized Java record definition corresponding to a C# anonymous type.
/// </summary>
public sealed class SynthesizedRecordInfo
{
    public string RecordName { get; }
    public IReadOnlyList<SynthesizedRecordField> Fields { get; }

    /// <summary>
    /// Structural key used for deduplication — sorted comma-separated "name:type" pairs.
    /// </summary>
    public string StructuralKey { get; }

    public SynthesizedRecordInfo(string recordName, IReadOnlyList<SynthesizedRecordField> fields, string structuralKey)
    {
        RecordName = recordName;
        Fields = fields;
        StructuralKey = structuralKey;
    }
}

/// <summary>
/// Synthesizes Java record declarations from C# anonymous object creation expressions.
/// <para>
/// When targeting Java 25, anonymous types like <c>new { e.FirstName, e.LastName }</c> are
/// converted to local record classes instead of <c>Map&lt;String, Object&gt;</c>, preserving
/// type safety and enabling accessor methods.
/// </para>
/// </summary>
public static class AnonymousTypeRecordSynthesizer
{
    /// <summary>
    /// Extract the fields from an anonymous object creation expression and either return an existing
    /// record (if a structurally identical one was already registered) or register a new one.
    /// Returns the <see cref="SynthesizedRecordInfo"/> to use for construction and the Java expression
    /// string <c>new RecordName(val1, val2, ...)</c>.
    /// </summary>
    public static (SynthesizedRecordInfo record, string constructorCall) SynthesizeForAnonymousType(
        AnonymousObjectCreationExpressionSyntax node,
        ConversionContext context,
        Func<ExpressionSyntax, string> transformExpression)
    {
        var fields = ExtractFields(node, context, transformExpression);
        var structuralKey = BuildStructuralKey(fields);

        // Check for an existing record with the same structure
        if (context.TryGetSynthesizedRecord(structuralKey, out var existing))
        {
            var call = BuildConstructorCall(existing!, node, transformExpression);
            return (existing!, call);
        }

        // Derive a name heuristic from the enclosing variable declaration
        var recordName = DeriveRecordName(node, context);
        var record = new SynthesizedRecordInfo(recordName, fields, structuralKey);
        context.RegisterSynthesizedRecord(record);

        // Re-fetch the record after registration because Register() may have renamed it
        // to avoid name collisions with previously registered records.
        if (context.TryGetSynthesizedRecord(structuralKey, out var registered) && registered != null)
            record = registered;

        var ctorCall = BuildConstructorCall(record, node, transformExpression);
        return (record, ctorCall);
    }

    /// <summary>
    /// Extracts field names and Java types from an anonymous object creation expression.
    /// </summary>
    private static List<SynthesizedRecordField> ExtractFields(
        AnonymousObjectCreationExpressionSyntax node,
        ConversionContext context,
        Func<ExpressionSyntax, string> transformExpression)
    {
        var fields = new List<SynthesizedRecordField>();
        foreach (var member in node.Initializers)
        {
            string name;
            if (member.NameEquals != null)
            {
                name = member.NameEquals.Name.Identifier.Text;
            }
            else if (member.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                name = memberAccess.Name.Identifier.Text;
            }
            else
            {
                name = $"field{fields.Count}";
            }

            // Resolve Java type via semantic model
            string javaType = "Object";
            if (context.SemanticModel != null)
            {
                var typeInfo = context.SemanticModel.GetTypeInfo(member.Expression);
                if (typeInfo.Type != null && !typeInfo.Type.IsAnonymousType)
                {
                    javaType = context.MapType(typeInfo.Type);
                }
                else if (typeInfo.Type != null && typeInfo.Type.IsAnonymousType
                         && member.Expression is AnonymousObjectCreationExpressionSyntax nestedAnon)
                {
                    // Recursively synthesize a record for nested anonymous types
                    var (nestedRecord, _) = SynthesizeForAnonymousType(nestedAnon, context, transformExpression);
                    javaType = nestedRecord.RecordName;
                }
            }

            // camelCase field name for Java convention, then escape Java keywords
            var fieldName = ConversionContext.EscapeJavaKeyword(ToCamelCase(name));
            fields.Add(new SynthesizedRecordField(fieldName, javaType));
        }
        return fields;
    }

    /// <summary>
    /// Builds a structural key for deduplication: sorted "name:type" pairs joined by comma.
    /// </summary>
    private static string BuildStructuralKey(IReadOnlyList<SynthesizedRecordField> fields)
    {
        // Use insertion order (not sorted) — anonymous types with the same properties in
        // different orders are structurally different in C#.
        return string.Join(",", fields.Select(f => $"{f.Name}:{f.JavaType}"));
    }

    /// <summary>
    /// Builds a <c>new RecordName(val1, val2, ...)</c> expression string.
    /// </summary>
    private static string BuildConstructorCall(
        SynthesizedRecordInfo record,
        AnonymousObjectCreationExpressionSyntax node,
        Func<ExpressionSyntax, string> transformExpression)
    {
        var args = new List<string>();
        foreach (var member in node.Initializers)
        {
            args.Add(transformExpression(member.Expression));
        }
        return $"new {record.RecordName}({string.Join(", ", args)})";
    }

    /// <summary>
    /// Derives a record name from the context: uses the left-hand variable name
    /// of the enclosing assignment/declaration, singularized and PascalCased.
    /// Falls back to <c>AnonymousRecord{N}</c>.
    /// </summary>
    private static string DeriveRecordName(
        AnonymousObjectCreationExpressionSyntax node,
        ConversionContext context)
    {
        // Walk ancestors to find a variable name hint
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is VariableDeclaratorSyntax declarator)
            {
                var raw = declarator.Identifier.Text;
                return SingularizePascalCase(raw);
            }
            if (ancestor is AssignmentExpressionSyntax assignment
                && assignment.Left is IdentifierNameSyntax identifier)
            {
                return SingularizePascalCase(identifier.Identifier.Text);
            }
        }

        return context.GenerateSyntheticName("AnonymousRecord");
    }

    /// <summary>
    /// Attempt to singularize and PascalCase a variable name.
    /// e.g. "highEarners" → "HighEarner", "items" → "Item", "result" → "Result",
    ///      "categories" → "Category", "addresses" → "Address"
    /// </summary>
    private static string SingularizePascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return "AnonymousRecord";

        // PascalCase
        var pascal = char.ToUpper(name[0]) + name.Substring(1);

        // Singularization rules (ordered from most specific to least):
        // "ies" → "y" (e.g. Categories → Category, Entries → Entry)
        if (pascal.Length > 4 && pascal.EndsWith("ies", StringComparison.Ordinal))
        {
            pascal = pascal.Substring(0, pascal.Length - 3) + "y";
        }
        // "ves" → "fe" (e.g. Wives → Wife, Knives → Knife)
        else if (pascal.Length > 4 && pascal.EndsWith("ves", StringComparison.Ordinal))
        {
            pascal = pascal.Substring(0, pascal.Length - 3) + "fe";
        }
        // "ses" / "xes" / "zes" / "ches" / "shes" → strip "es" (e.g. Addresses → Address, Boxes → Box)
        else if (pascal.Length > 4
            && pascal.EndsWith("es", StringComparison.Ordinal)
            && (pascal.EndsWith("ses", StringComparison.Ordinal)
                || pascal.EndsWith("xes", StringComparison.Ordinal)
                || pascal.EndsWith("zes", StringComparison.Ordinal)
                || pascal.EndsWith("ches", StringComparison.Ordinal)
                || pascal.EndsWith("shes", StringComparison.Ordinal)))
        {
            pascal = pascal.Substring(0, pascal.Length - 2);
        }
        // Generic trailing "s" (not "ss", "us") → strip trailing "s"
        else if (pascal.Length > 3 && pascal.EndsWith("s", StringComparison.Ordinal)
            && !pascal.EndsWith("ss", StringComparison.Ordinal)
            && !pascal.EndsWith("us", StringComparison.Ordinal))
        {
            pascal = pascal.Substring(0, pascal.Length - 1);
        }

        return pascal;
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (char.IsLower(name[0])) return name;
        return char.ToLower(name[0]) + name.Substring(1);
    }
}
