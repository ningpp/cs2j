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

            // Honor TypeMappings property→method renames (e.g.
            // ThreadLocal<T>.IsValueCreated → isValueCreated()); the C09
            // ThreadLocal compat surface uses bean-style accessors that do not
            // follow the default getXxx derivation). Field-style renames
            // (length/size) must NOT be consumed here: they stay getXxx and the
            // lowering pipeline turns them into field access.
            var mappedProperty = context.TypeMappings.MapMethod(
                property.ContainingType.ToDisplayString(), property.Name);
            if (JavaNaming.IsBeanAccessorStyleName(mappedProperty))
            {
                return $"{receiver}.{mappedProperty}()";
            }

            return $"{receiver}.get{member.Substring(0, 1).ToUpperInvariant()}{member.Substring(1)}()";
        }

        return $"{receiver}.{member}";
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
