using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles identifier and member access expressions.
/// </summary>
[TransformerRegistration]
public class IdentifierExpressionTransformer : IExpressionTransformer
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
            SyntaxKind.PredefinedType => TransformPredefinedType((PredefinedTypeSyntax)node),
            SyntaxKind.GenericName => TransformGenericName((GenericNameSyntax)node, context),
            SyntaxKind.SimpleMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            SyntaxKind.PointerMemberAccessExpression => TransformPointerMemberAccess((MemberAccessExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Identifier expression kind {node.Kind()} not supported.")
        };

    private string TransformIdentifier(IdentifierNameSyntax node, ConversionContext context)
    {
        var name = node.Identifier.Text;

        // Fix 2: resolve LINQ 'let' clause variables inlined via QueryLetAliases
        if (context.QueryLetAliases.TryGetValue(name, out var letAlias))
            return letAlias;

        // Check for using aliases — Fix 5: chain alias resolution through type-registry
        if (context.IsAlias(name))
        {
            var javaType = context.MapAliasToJavaType(name);
            if (javaType != null)
            {
                // If MapAliasToJavaType returned a simple (unqualified) name, apply a
                // secondary type-registry lookup to pick up any package-mapping entries.
                if (!javaType.Contains('.'))
                {
                    var remapped = context.TypeMappings.MapType(javaType);
                    if (remapped != javaType)
                        return remapped;
                }
                return javaType;
            }
        }

        // Check if the identifier resolves to a property — generate getter() for reads,
        // or the camelCase backing-field name when it appears on the LHS of an assignment
        // (AssignmentTransformer will wrap that into a setXxx() call).
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IPropertySymbol identProp)
        {
            bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax asgn && asgn.Left == node;
            if (!isLhsOfAssignment)
            {
                var getter = "get" + char.ToUpperInvariant(identProp.Name[0]) + identProp.Name[1..];
                return $"{getter}()";
            }
            // LHS: return camelCase so AssignmentTransformer can build setXxx(rhs)
            return char.ToLower(identProp.Name[0]) + identProp.Name[1..];
        }

        // When this identifier is an out/ref parameter, any use as a receiver must go through
        // .value so that member accesses like p.X or p.X = 1 become p.value.X / p.value.setX(1).
        // Direct assignment (p = value → p.value = value) is handled separately by AssignmentTransformer
        // with an early return that never reaches this path.
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IParameterSymbol outParam
            && (outParam.RefKind == RefKind.Out || outParam.RefKind == RefKind.Ref))
        {
            return $"{ConversionContext.EscapeJavaKeyword(outParam.Name)}.value";
        }

        return ConversionContext.EscapeJavaKeyword(name);
    }

    // Fix 3 & 4: Replaced duplicate local BoxedTypeName with TransformPredefinedType.
    // Uses boxed types in generic-argument positions; delegates to ExpressionTransformerHelpers
    // (the canonical BoxedTypeName source) to avoid divergence.
    private string TransformPredefinedType(PredefinedTypeSyntax node)
    {
        // Fix 3: generic type arguments require boxed types (e.g., List<Integer> not List<int>)
        if (node.Parent is TypeArgumentListSyntax)
            return ExpressionTransformerHelpers.BoxedTypeName(node);

        // Non-generic context: use Java primitive / value types
        var typeName = node.Keyword.Text;
        return typeName switch
        {
            "int" => "int",
            "long" => "long",
            "short" => "short",
            "byte" => "byte",
            "sbyte" => "byte",
            "uint" => "int",
            "ulong" => "long",
            "ushort" => "short",
            "float" => "float",
            "double" => "double",
            "bool" => "boolean",
            "char" => "char",
            "string" => "String",
            "object" => "Object",
            "void" => "void",
            _ => typeName
        };
    }

    private string TransformGenericName(GenericNameSyntax node, ConversionContext context)
    {
        var name = ConversionContext.EscapeJavaKeyword(node.Identifier.Text);
        var typeArgs = new List<string>();

        foreach (var typeArg in node.TypeArgumentList.Arguments)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(typeArg);
            if (typeInfo.HasValue && typeInfo.Value.Type != null)
            {
                typeArgs.Add(context.MapType(typeInfo.Value.Type));
            }
            else
            {
                typeArgs.Add(typeArg.ToString());
            }
        }

        return $"{name}<{string.Join(", ", typeArgs)}>";
    }

    private string TransformMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Fix: Generic type static member access — C# allows Set<T>.Method() but Java requires Set.Method().
        // Strip type arguments from the receiver whenever it is a generic name expression.
        if (node.Expression is GenericNameSyntax genericExprName)
        {
            var rawReceiver = ConversionContext.EscapeJavaKeyword(genericExprName.Identifier.Text);
            var rawMember   = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);
            return $"{rawReceiver}.{rawMember}";
        }

        // Fix: Primitive type static member access — C# double.MaxValue → Java Double.MAX_VALUE etc.
        if (node.Expression is PredefinedTypeSyntax primTypeSyntax)
        {
            var boxedName  = ExpressionTransformerHelpers.BoxedTypeName(primTypeSyntax);
            var rawMember  = node.Name.Identifier.Text;
            var mappedMember = MapPrimitiveStaticFieldName(primTypeSyntax.Keyword.Text, rawMember);
            // If the mapping already produced a self-contained expression (e.g. "(-Double.MAX_VALUE)")
            // don't prefix it with the boxed type name — that would create "Double.(-Double.MAX_VALUE)".
            if (mappedMember.StartsWith("(") || mappedMember.StartsWith("-"))
                return mappedMember;
            return $"{boxedName}.{mappedMember}";
        }

        var target = facade.Transform(node.Expression, context);
        var memberName = node.Name.Identifier.Text;

        // Fix 1 & 2: consult member-name mapping and generate property getters
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IPropertySymbol prop)
        {
            // Fix 1: check TypeMappings for a configured method/member name mapping
            var typeName = prop.ContainingType.ToDisplayString();
            var mappedMethod = context.TypeMappings.MapMethod(typeName, prop.Name);
            if (mappedMethod != null)
            {
                // If the mapped value is a fully-qualified Java field (contains a dot, e.g.
                // "java.util.Locale.ROOT") emit it directly without a receiver prefix or ().
                // For array.length: Java arrays expose length as a public final field, not a
                // method — emit without parentheses.
                // Otherwise it is a method name (e.g. "size") — emit as target.method().
                if (mappedMethod.Contains('.'))
                    return mappedMethod;
                if (prop.ContainingType.SpecialType == SpecialType.System_Array)
                    return $"{target}.{mappedMethod}";
                return $"{target}.{mappedMethod}()";
            }

            // Fix 2: no mapping configured — generate getXxx() for read accesses
            bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax assign && assign.Left == node;
            if (!isLhsOfAssignment)
            {
                var getter = "get" + char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..];
                return $"{target}.{getter}()";
            }
        }

        var member = ConversionContext.EscapeJavaKeyword(memberName);
        return $"{target}.{member}";
    }

    /// <summary>
    /// Maps C# primitive-type static field/property names to their Java equivalents.
    /// e.g. double.MaxValue → MAX_VALUE, double.PositiveInfinity → POSITIVE_INFINITY
    /// Note: for double/float, MinValue in C# is the most-negative finite value
    ///       (-MAX_VALUE in Java), not the smallest positive value (Java's MIN_VALUE).
    /// </summary>
    private static string MapPrimitiveStaticFieldName(string primitiveKeyword, string memberName)
        => (primitiveKeyword, memberName) switch
        {
            // Double/float MinValue = most negative finite → negate MAX_VALUE
            ("double" or "float", "MinValue") => $"(-{(primitiveKeyword == "double" ? "Double" : "Float")}.MAX_VALUE)",
            (_, "MaxValue")          => "MAX_VALUE",
            (_, "MinValue")          => "MIN_VALUE",
            (_, "Epsilon")           => "MIN_VALUE",
            (_, "PositiveInfinity")  => "POSITIVE_INFINITY",
            (_, "NegativeInfinity")  => "NEGATIVE_INFINITY",
            (_, "NaN")               => "NaN",
            // Static methods used as non-invocation members — pass through
            (_, "IsInfinity")        => "isInfinite",
            (_, "IsPositiveInfinity")=> "isInfinite",
            (_, "IsNegativeInfinity")=> "isInfinite",
            (_, "IsNaN")             => "isNaN",
            _                        => memberName
        };

    private string TransformPointerMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        // C# pointer member access (ptr->member) has no direct Java equivalent
        context.Diagnostics.Warning("Pointer member access (->) has no Java equivalent - unsafe code not supported", node.GetLocation());
        var facade = ExpressionTransformerFacade.Instance;
        var target = facade.Transform(node.Expression, context);
        var member = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);
        // Fix 6: note that unsafe pointer semantics cannot be reproduced in Java
        return $"/* WARNING: C# unsafe pointer dereference — Java does not support pointer arithmetic. */ {target}.{member}";
    }
}
