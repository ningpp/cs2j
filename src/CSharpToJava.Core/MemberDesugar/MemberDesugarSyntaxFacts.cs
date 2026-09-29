using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar;

internal static class MemberDesugarSyntaxFacts
{
    public static readonly SyntaxAnnotation RewriteAnnotation = new("Cs2jMemberDesugarRewrite");

    /// <summary>表达式树包含判定（设计决策 12 / 风险 8）：祖先 lambda 的转换类型是
    /// System.Linq.Expressions.Expression&lt;Delegate&gt;。</summary>
    public static bool IsInsideExpressionTree(ExpressionSyntax node, SemanticModel model)
    {
        for (var current = (SyntaxNode?)node; current != null; current = current.Parent)
        {
            if (current is not LambdaExpressionSyntax lambda) continue;
            var converted = model.GetTypeInfo(lambda).ConvertedType;
            if (converted is INamedTypeSymbol { Name: "Expression", Arity: 1 }
                && converted.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions")
                return true;
        }
        return false;
    }

    /// <summary>dynamic 接收者/表达式判定（CS2J5118；force 不解除）。</summary>
    public static bool IsDynamic(ExpressionSyntax node, SemanticModel model)
    {
        if (model.GetTypeInfo(node).Type is { TypeKind: TypeKind.Dynamic }) return true;
        if (node is MemberAccessExpressionSyntax { Expression: { } recv }
            && model.GetTypeInfo(recv).Type is { TypeKind: TypeKind.Dynamic })
            return true;
        return false;
    }

    /// <summary>临时变量名：沿用全仓 __cs2j 前缀惯例（设计决策 7）。</summary>
    public static string TempName(ref int counter, string hint = "tmp")
        => $"__cs2j_{hint}{counter++}";
}
