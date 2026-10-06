using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Collections.Generic;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Expression;

[TransformerRegistration]
public class IdentifierExpressionTransformer : IIRExpressionTransformer
{
    static IdentifierExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.IdentifierName,
            SyntaxKind.PredefinedType,
            SyntaxKind.GenericName,
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxKind.PointerMemberAccessExpression
        }, new IdentifierExpressionTransformer());
    }

    private static readonly Lazy<IdentifierExpressionTransformer> _instance = new(() => new());
    public static IdentifierExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.IdentifierName => TransformIdentifier((IdentifierNameSyntax)node, context),
            SyntaxKind.PredefinedType => ((PredefinedTypeSyntax)node).Keyword.Text,
            SyntaxKind.GenericName => TransformGenericName((GenericNameSyntax)node, context),
            SyntaxKind.SimpleMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            SyntaxKind.PointerMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            _ => throw new System.NotSupportedException($"Kind {node.Kind()}")
        };

    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
        => new JavaRawExpression(Transform(node, context), "void");

    // ── IdentifierName ──────────────────────────────────────────────────────

    private static string TransformIdentifier(IdentifierNameSyntax node, ConversionContext context)
    {
        var symbol = context.GetSymbolInfo(node).Symbol;
        // Property accessed by value → getXxx()
        if (symbol is IPropertySymbol p
            && !(node.Parent is AssignmentExpressionSyntax a && a.Left == node))
            return $"get{p.Name}()";
        // Local / parameter / field / default → just the name
        return ConversionContext.EscapeJavaKeyword(node.Identifier.Text);
    }

    // ── Member access ───────────────────────────────────────────────────────

    private static string TransformMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        var receiver = ExpressionTransformerFacade.Instance.Transform(node.Expression, context);
        var symbol = context.GetSymbolInfo(node).Symbol;
        var member = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);

        // Static members qualified by a type with an explicit type mapping: when
        // the mapped Java type keeps the same simple name as the C# type, rewrite
        // the qualifier to that simple name and register its import
        // (System.StringComparison.OrdinalIgnoreCase → StringComparison.OrdinalIgnoreCase
        // + import). Mapped wrapper types with a different Java simple name keep
        // their original receiver, because their members own dedicated renderings
        // (IPAddress.Loopback → getLoopbackAddress, Double.NaN → the boxed
        // wrapper constant) and the invocation/property passes handle them.
        if (symbol is { IsStatic: true }
            && TryGetMappedStaticContainingType(symbol, context,
                out var mappedStaticType, out var mappedImports)
            && mappedStaticType == symbol.ContainingType.Name)
        {
            receiver = mappedStaticType;
            foreach (var import in mappedImports)
            {
                context.AddImport(import);
            }
        }

        // Property (read) → getXxx()
        if (symbol is IPropertySymbol property
            && !(node.Parent is AssignmentExpressionSyntax a && a.Left == node))
        {
            // C22: System.Nullable<T> surfaces — HasValue is a reference null
            // check and Value is a guarded unbox on the Java wrapper (the C22
            // preprocessor only leaves .Value inside a HasValue guard).
            if (TryTransformNullableMember(property, receiver, out var nullableMember))
            {
                return nullableMember;
            }

            // Arrays expose a public final length field in Java.
            if (property.Name == "Length"
                && context.GetTypeInfo(node.Expression).Type is IArrayTypeSymbol)
            {
                return $"{receiver}.length";
            }

            // Honor TypeMappings property→method renames (e.g.
            // ThreadLocal<T>.IsValueCreated → isValueCreated()); the C09
            // ThreadLocal compat surface uses bean-style accessors that do not
            // follow the default getXxx derivation).
            var mappedProperty = context.TypeMappings.MapMethod(
                property.ContainingType.ToDisplayString(), property.Name);
            if (JavaNaming.IsBeanAccessorStyleName(mappedProperty))
            {
                return $"{receiver}.{mappedProperty}()";
            }

            // Field-style mapped names: arrays were handled above; String/StringBuilder
            // Length → length() as a method-style accessor (project-pipeline companion
            // of the single-file LowerProperty pass). Collection size() likewise.
            // The receiver-type gate is required because other mapped types reuse the
            // "length" spelling (System.Array → CSharpArray.Length maps to getLength()).
            var lengthHost = property.ContainingType.ToDisplayString();
            if (mappedProperty == "length"
                && (lengthHost == "System.String"
                    || lengthHost == "System.Text.StringBuilder"))
            {
                return $"{receiver}.length()";
            }

            if (mappedProperty == "size")
            {
                return $"{receiver}.size()";
            }

            return $"{receiver}.get{member.Substring(0, 1).ToUpperInvariant()}{member.Substring(1)}()";
        }

        // Array .Length that the semantic model does not surface as a property.
        if (member == "Length"
            && context.GetTypeInfo(node.Expression).Type is IArrayTypeSymbol)
        {
            return $"{receiver}.length";
        }

        return $"{receiver}.{member}";
    }

    /// <summary>
    /// Resolves the mapped Java simple type name for a static member's containing type
    /// when an explicit type mapping exists (<c>System.StringComparison</c> →
    /// <c>StringComparison</c> + compat import). Returns false for project types and
    /// types that merely fall back to their simple name. This lookup is pure: callers
    /// decide whether to register the imports.
    /// </summary>
    private static bool TryGetMappedStaticContainingType(
        ISymbol symbol,
        ConversionContext context,
        out string mappedType,
        out IReadOnlyList<string> mappedImports)
    {
        mappedType = string.Empty;
        mappedImports = Array.Empty<string>();

        if (symbol.ContainingType is not INamedTypeSymbol type)
        {
            return false;
        }

        var display = type.ToDisplayString();
        var fqn = $"{type.ContainingNamespace}.{type.Name}";
        var configKey = context.TypeMappings.HasTypeMapping(display) ? display
            : context.TypeMappings.HasTypeMapping(fqn) ? fqn
            : null;
        if (configKey == null)
        {
            return false;
        }

        mappedType = context.TypeMappings.MapType(configKey);
        mappedImports = context.TypeMappings.GetRequiredImports(configKey);
        return true;
    }

    // ── System.Nullable<T> member surface (C22) ─────────────────────────────

    /// <summary>
    /// Maps <c>Nullable&lt;T&gt;.HasValue</c> to a reference null check and
    /// <c>.Value</c> to the guarded primitive unbox accessor. Enum/struct
    /// underlying types are reference types in Java, so <c>.Value</c> is the
    /// receiver itself.
    /// </summary>
    private static bool TryTransformNullableMember(
        IPropertySymbol property, string receiver, out string mapped)
    {
        mapped = "";
        var owner = property.ContainingType;
        if (owner is null || owner.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
        {
            return false;
        }

        switch (property.Name)
        {
            case "HasValue":
                mapped = $"({receiver} != null)";
                return true;
            case "Value":
                mapped = UnboxNullableValue(owner.TypeArguments[0], receiver);
                return true;
            default:
                return false;
        }
    }

    private static string UnboxNullableValue(ITypeSymbol underlying, string receiver) =>
        underlying.SpecialType switch
        {
            SpecialType.System_Boolean => $"{receiver}.booleanValue()",
            SpecialType.System_Byte => $"{receiver}.byteValue()",
            SpecialType.System_Char => $"{receiver}.charValue()",
            SpecialType.System_Int16 => $"{receiver}.shortValue()",
            SpecialType.System_Int32 => $"{receiver}.intValue()",
            SpecialType.System_Int64 => $"{receiver}.longValue()",
            SpecialType.System_Single => $"{receiver}.floatValue()",
            SpecialType.System_Double => $"{receiver}.doubleValue()",
            // Enums and other value types are Java reference types already.
            _ => receiver,
        };

    // ── Generic name ────────────────────────────────────────────────────────

    private static string TransformGenericName(GenericNameSyntax node, ConversionContext context)
    {
        var name = ConversionContext.EscapeJavaKeyword(node.Identifier.Text);
        var args = new List<string>();
        foreach (var a in node.TypeArgumentList.Arguments)
        {
            var t = context.GetTypeInfo(a).Type;
            args.Add(t != null ? context.MapType(t) : "Object");
        }
        return $"{name}<{string.Join(", ", args)}>";
    }

    // ── Tuple helpers (consumed by other transformers) ──────────────────────

    internal static bool IsTupleItemName(string memberName, out int index)
    {
        index = 0;
        if (!memberName.StartsWith("Item") || memberName.Length < 5 || !int.TryParse(memberName[4..], out var n))
            return false;
        index = n;
        return true;
    }

    internal static int TryGetTupleElementIndex(INamedTypeSymbol tupleType, string elementName)
    {
        if (IsTupleItemName(elementName, out var idx)) return idx - 1;
        var fields = tupleType.TupleElements;
        for (int i = 0; i < fields.Length; i++)
            if (fields[i].Name == elementName) return i;
        return -1;
    }
}
