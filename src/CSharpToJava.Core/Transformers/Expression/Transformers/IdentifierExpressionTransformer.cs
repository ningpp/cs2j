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
        var target = facade.Transform(node.Expression, context);
        var memberName = node.Name.Identifier.Text;

        // Fix 1 & 2: consult member-name mapping and generate property getters
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IPropertySymbol prop)
        {
            // Fix 1: check TypeMappings for a configured method/member name mapping
            var typeName = prop.ContainingType.ToDisplayString();
            var mappedMethod = context.TypeMappings.MapMethod(typeName, prop.Name);
            if (mappedMethod != null)
                return $"{target}.{mappedMethod}";

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
