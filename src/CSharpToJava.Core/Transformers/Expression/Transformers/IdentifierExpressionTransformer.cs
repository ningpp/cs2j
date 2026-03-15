using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles identifier and member access expressions.
/// </summary>
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
            SyntaxKind.PredefinedType => BoxedTypeName((PredefinedTypeSyntax)node),
            SyntaxKind.GenericName => TransformGenericName((GenericNameSyntax)node, context),
            SyntaxKind.SimpleMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            SyntaxKind.PointerMemberAccessExpression => TransformPointerMemberAccess((MemberAccessExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Identifier expression kind {node.Kind()} not supported.")
        };

    private string TransformIdentifier(IdentifierNameSyntax node, ConversionContext context)
    {
        var name = node.Identifier.Text;

        // Check for using aliases
        if (context.IsAlias(name))
        {
            var javaType = context.MapAliasToJavaType(name);
            if (javaType != null) return javaType;
        }

        return ConversionContext.EscapeJavaKeyword(name);
    }

    private string BoxedTypeName(PredefinedTypeSyntax node)
    {
        // Map C# predefined types to Java types
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
        var member = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);
        return $"{target}.{member}";
    }

    private string TransformPointerMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        // C# pointer member access (ptr->member) has no direct Java equivalent
        context.Diagnostics.Warning("Pointer member access (->) has no Java equivalent - unsafe code not supported", node.GetLocation());
        var facade = ExpressionTransformerFacade.Instance;
        var target = facade.Transform(node.Expression, context);
        var member = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);
        return $"/* unsafe: pointer member access */ {target}.{member}";
    }
}
