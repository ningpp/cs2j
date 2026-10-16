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

    /// <summary>
    /// 跨程序集同名类型镜像合并判定。CLI 整树编译把不属于同一 msbuild 工程的
    /// 同名类型声明折进同一 Roslyn 符号（真实跨文件 partial 类型的每个声明都带
    /// <c>partial</c> 修饰；被合并进来的测试镜像声明不带 → 基线 CS0260）。这类
    /// 人为合并类型上展开属性/事件会与镜像声明撞访问器槽名（CS0082「already
    /// reserves a member ... get_X」/CS0111），而真实 msbuild 构建按程序集隔离，
    /// 该合并并不存在。故此类符号上的成员一律不展开，保留原声明。
    /// </summary>
    public static bool IsMergedMirrorType(INamedTypeSymbol? type)
    {
        if (type == null) return false;
        var refs = type.DeclaringSyntaxReferences;
        if (refs.Length <= 1) return false;
        var anyPartial = false;
        var anyNonPartial = false;
        foreach (var reference in refs)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration) continue;
            if (declaration.Modifiers.Any(SyntaxKind.PartialKeyword)) anyPartial = true;
            else anyNonPartial = true;
        }
        return anyPartial && anyNonPartial;
    }
}
