using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles assignment expressions (simple, compound, and shifted assignments).
/// </summary>
[TransformerRegistration]
public class AssignmentTransformer : IExpressionTransformer
{
    static AssignmentTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxKind.AddAssignmentExpression,
            SyntaxKind.SubtractAssignmentExpression,
            SyntaxKind.MultiplyAssignmentExpression,
            SyntaxKind.DivideAssignmentExpression,
            SyntaxKind.ModuloAssignmentExpression,
            SyntaxKind.AndAssignmentExpression,
            SyntaxKind.OrAssignmentExpression,
            SyntaxKind.ExclusiveOrAssignmentExpression,
            SyntaxKind.LeftShiftAssignmentExpression,
            SyntaxKind.RightShiftAssignmentExpression,
            SyntaxKind.CoalesceAssignmentExpression,   // Fix 3: register ??=
        }, new AssignmentTransformer());
    }

    private static readonly Lazy<AssignmentTransformer> _instance = new(() => new());
    public static AssignmentTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.SimpleAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "=", context),
            SyntaxKind.AddAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "+=", context),
            SyntaxKind.SubtractAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "-=", context),
            SyntaxKind.MultiplyAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "*=", context),
            SyntaxKind.DivideAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "/=", context),
            SyntaxKind.ModuloAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "%=", context),
            SyntaxKind.AndAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "&=", context),
            SyntaxKind.OrAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "|=", context),
            SyntaxKind.ExclusiveOrAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "^=", context),
            SyntaxKind.LeftShiftAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "<<=", context),
            SyntaxKind.RightShiftAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, ">>=", context),
            SyntaxKind.CoalesceAssignmentExpression => TransformCoalesceAssignment((AssignmentExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Assignment expression kind {node.Kind()} not supported.")
        };

    private string TransformAssignment(AssignmentExpressionSyntax node, string op, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var leftNode = node.Left;
        var rightNode = node.Right;

        // Fix 4: Detect event += / -= using semantic model → listener methods
        if ((op == "+=" || op == "-=") && leftNode is MemberAccessExpressionSyntax evtMa)
        {
            if (context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IEventSymbol evt)
            {
                var receiver = facade.Transform(evtMa.Expression, context);
                var handler = facade.Transform(rightNode, context);
                string method = op == "+="
                    ? $"add{evt.Name}Listener"
                    : $"remove{evt.Name}Listener";
                return $"{receiver}.{method}({handler})";
            }
        }

        // Fix 1: Detect property assignments using semantic model → setter calls (simple assignment only)
        if (op == "=" && leftNode is MemberAccessExpressionSyntax propMa)
        {
            if (context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IPropertySymbol prop)
            {
                var receiver = facade.Transform(propMa.Expression, context);
                var right = facade.Transform(rightNode, context);
                string setter = "set" + char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..];
                return $"{receiver}.{setter}({right})";
            }
        }

        // Fix 2: Detect indexer assignments → put/set methods (simple assignment only)
        if (op == "=" && leftNode is ElementAccessExpressionSyntax ela)
        {
            if (context.SemanticModel?.GetSymbolInfo(ela).Symbol is IPropertySymbol { IsIndexer: true })
            {
                var target = facade.Transform(ela.Expression, context);
                var argList = ela.ArgumentList.Arguments;
                var right = facade.Transform(rightNode, context);

                if (argList.Count == 1)
                {
                    var argExpr = argList[0].Expression;
                    var argType = context.SemanticModel?.GetTypeInfo(argExpr).Type;
                    bool isIntIndex = argType?.SpecialType is
                        SpecialType.System_Int32 or SpecialType.System_Int64 or
                        SpecialType.System_Int16 or SpecialType.System_Byte;
                    string method = isIntIndex ? "set" : "put";
                    var arg0 = facade.Transform(argExpr, context);
                    return $"{target}.{method}({arg0}, {right})";
                }

                // Multi-argument indexer: use put with all args (best effort)
                var transformedArgs = string.Join(", ", argList.Select(a => facade.Transform(a.Expression, context)));
                return $"{target}.put({transformedArgs}, {right})";
            }
        }

        var left = facade.Transform(leftNode, context);
        var rightStr = facade.Transform(rightNode, context);
        return $"{left} {op} {rightStr}";
    }

    // Fix 3 + Fix 5: Handle ??= (CoalesceAssignment), avoiding double evaluation of complex LHS
    private string TransformCoalesceAssignment(AssignmentExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var right = facade.Transform(node.Right, context);

        if (node.Left is IdentifierNameSyntax)
        {
            // Simple identifier: a ??= b → inject pre-statement: if (a == null) a = b
            var leftStr = facade.Transform(node.Left, context);
            context.AddPreStatement($"if ({leftStr} == null) {leftStr} = {right}");
            // Return the identifier; statement transformer will filter it as a non-statement
            return leftStr;
        }

        if (node.Left is MemberAccessExpressionSyntax memberAccess)
        {
            // Fix 5: Extract receiver to a temp variable to avoid double evaluation
            // e.g. GetContainer().Value ??= x → var __tmp = GetContainer(); if (__tmp.Value == null) __tmp.Value = x;
            var tmpName = context.GenerateSyntheticName("__nullCoalTemp");
            var receiver = facade.Transform(memberAccess.Expression, context);
            var memberName = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
            context.AddPreStatement($"var {tmpName} = {receiver}");
            context.AddPreStatement($"if ({tmpName}.{memberName} == null) {tmpName}.{memberName} = {right}");
            return $"{tmpName}.{memberName}";
        }

        // General complex case (best effort — evaluates LHS expression string once but may have side effects)
        var lhs = facade.Transform(node.Left, context);
        context.AddPreStatement($"if ({lhs} == null) {lhs} = {right}");
        return lhs;
    }
}
