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
        // Implicit method-group conversion: `SomeType.Method` passed directly as
        // a delegate argument (no explicit `new Func(...)`) → Java method ref.
        if (context.GetSymbolInfo(node).Symbol is IMethodSymbol groupMethod
            && context.GetTypeInfo(node).ConvertedType?.TypeKind == TypeKind.Delegate)
        {
            return ObjectCreationTransformer.EmitDelegateMethodReference(node, groupMethod, context);
        }
        // Primitive static fields on keyword qualifiers: int.MaxValue (the
        // predefined keyword, not the System.Int32 qualified name) must become
        // Integer.MAX_VALUE — Java primitives cannot be dereferenced.
        if (context.GetSymbolInfo(node).Symbol is IFieldSymbol { IsStatic: true } primitiveField)
        {
            // compat Decimal keeps its extremes under Java-style names.
            if (primitiveField.ContainingType?.SpecialType == SpecialType.System_Decimal
                && primitiveField.Name is "MaxValue" or "MinValue")
            {
                context.AddImport("io.github.ningpp.compat.Decimal");
                return $"Decimal.{(primitiveField.Name == "MaxValue" ? "MAX_VALUE" : "MIN_VALUE")}";
            }
            if (TryRenderPrimitiveStaticField(primitiveField, out var primitiveFieldText))
            {
                return primitiveFieldText;
            }
        }

        // System.Console.Error/Out are stream properties, not bean accessors:
        // they map directly to java.lang.System's static err/out fields.
        if (context.GetSymbolInfo(node).Symbol
            is IPropertySymbol { ContainingType: { } consoleType } consoleStream
            && consoleType.ToDisplayString() == "System.Console")
        {
            if (consoleStream.Name == "Error") return "System.err";
            if (consoleStream.Name == "Out") return "System.out";
        }

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
            if (TryTransformNullableMember(property, receiver, context, out var nullableMember))
            {
                return nullableMember;
            }

            // Arrays expose a public final length field in Java.
            if (property.Name == "Length"
                && context.GetTypeInfo(node.Expression).Type is IArrayTypeSymbol)
            {
                return $"{receiver}.length";
            }

            // C# string.Length -> java.lang.String.length(). Keyed on the semantic
            // special type: ToDisplayString() spells System.String as the keyword
            // "string", which misses the "System.String" rows in TypeMappings.json.
            if (property.Name == "Length"
                && property.ContainingType?.SpecialType == SpecialType.System_String)
            {
                return $"{receiver}.length()";
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

    /// <summary>
    /// Renders integral primitive constant fields (<c>int.MaxValue</c> →
    /// <c>Integer.MAX_VALUE</c>). Signed types map to the Java wrapper
    /// constants. C# byte/uint are stored in Java signed primitives with the
    /// same bit width, so their extremes render as narrowing-cast literals
    /// (<c>byte.MaxValue</c> → <c>(byte)255</c>, <c>uint.MaxValue</c> →
    /// <c>(int)4294967295L</c>). float/double MinValue semantics differ
    /// (smallest-positive vs most-negative) and stay on dedicated paths.
    /// </summary>
    private static bool TryRenderPrimitiveStaticField(IFieldSymbol field, out string rendered)
    {
        rendered = "";
        switch (field.ContainingType?.SpecialType)
        {
            case SpecialType.System_Byte when field.Name is "MaxValue" or "MinValue":
                rendered = field.Name == "MaxValue" ? "(byte)255" : "(byte)0";
                return true;
            case SpecialType.System_UInt32 when field.Name is "MaxValue" or "MinValue":
                rendered = field.Name == "MaxValue" ? "(int)4294967295L" : "0";
                return true;
        }

        var wrapper = field.ContainingType?.SpecialType switch
        {
            SpecialType.System_SByte => "Byte",
            SpecialType.System_Int16 => "Short",
            SpecialType.System_Int32 => "Integer",
            SpecialType.System_Int64 => "Long",
            _ => null,
        };
        if (wrapper == null) return false;
        var constant = field.Name switch
        {
            "MaxValue" => "MAX_VALUE",
            "MinValue" => "MIN_VALUE",
            _ => null,
        };
        if (constant == null) return false;
        rendered = $"{wrapper}.{constant}";
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
        IPropertySymbol property, string receiver, ConversionContext context, out string mapped)
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
                mapped = UnboxNullableValue(owner.TypeArguments[0], receiver, context);
                return true;
            default:
                return false;
        }
    }

    private static string UnboxNullableValue(ITypeSymbol underlying, string receiver, ConversionContext context)
    {
        // C# Nullable<T>.Value throws InvalidOperationException (mapped to
        // IllegalStateException) on null; plain Java unboxing would NPE. Route
        // primitive unwraps through the compat Nullable guard.
        if (underlying.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Char
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double)
        {
            context.AddImport("io.github.ningpp.compat.Nullable");
            return $"Nullable.value({receiver})";
        }
        // Enums and other value types are Java reference types already.
        return receiver;
    }

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
