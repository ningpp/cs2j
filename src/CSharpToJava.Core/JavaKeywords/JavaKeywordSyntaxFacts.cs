using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// 纯语法/语义小工具。保留字判定直接委托 <see cref="JavaNaming"/>，不维护副本。
/// </summary>
internal static class JavaKeywordSyntaxFacts
{
    public static readonly SyntaxAnnotation RewriteAnnotation = new("Cs2jJavaKeywordRewrite");

    /// <summary>剥 @ 前缀（与 EscapeJavaKeyword 同款）。</summary>
    public static string StripAt(string name)
        => name.StartsWith("@", StringComparison.Ordinal) ? name[1..] : name;

    /// <summary>首字母小写（与转换器 MethodTransformer.JavaName 同款）。</summary>
    public static string Decap(string s)
        => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    /// <summary>
    /// JK-0（纯名字谓词）：strip(name) 或 decap(strip(name)) 任一是 Java 关键字即命中。
    /// 仅用于调用点/partial 回退判断；声明点候选收集须用按符号种类分流的
    /// <see cref="NeedsEscape(ISymbol, string)"/>。
    /// </summary>
    public static bool NeedsEscape(string name)
    {
        var s = StripAt(name);
        return JavaNaming.IsJavaKeyword(s) || JavaNaming.IsJavaKeyword(Decap(s));
    }

    /// <summary>
    /// JK-0（按符号种类分流）：strip(name) 命中保留字对所有种类生效；
    /// decap(strip(name)) 命中仅对转换器会首字母小写的种类生效——普通方法、
    /// 局部函数、显式接口实现。字段/属性/形参/局部/类型的名字是 C# 侧的
    /// 序列化、反射与公开 API 契约，转换器不会 decap，故不能因 decap 命中而改名。
    /// </summary>
    public static bool NeedsEscape(ISymbol symbol, string name)
    {
        var s = StripAt(name);
        if (JavaNaming.IsJavaKeyword(s)) return true;
        return IsDecappedMethodName(symbol) && JavaNaming.IsJavaKeyword(Decap(s));
    }

    /// <summary>转换器仅对方法名做首字母小写（MethodTransformer.JavaName / GetLocalFunctionJavaName）。</summary>
    public static bool IsDecappedMethodName(ISymbol symbol)
        => symbol is IMethodSymbol
        {
            MethodKind: MethodKind.Ordinary
                or MethodKind.LocalFunction
                or MethodKind.ExplicitInterfaceImplementation,
        };

    /// <summary>JR-0 新名 = strip(name) + "Value"（与 EscapeJavaKeyword 完全一致）。</summary>
    public static string NewName(string name)
        => StripAt(name) + "Value";

    /// <summary>碰撞消歧：首撞从 2 起追加序号。</summary>
    public static string CollisionSuffix(string baseNewName, int suffix)
        => suffix <= 0 ? baseNewName : baseNewName + (suffix + 1);

    public static bool IsInTree(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

    /// <summary>判断表达式是否为纯标识符或简单成员访问（可安全复用文本，免提升临时变量）。</summary>
    public static bool IsSimpleExpression(ExpressionSyntax expr)
    {
        return expr is IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax
            or LiteralExpressionSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax
            or PredefinedTypeSyntax;
    }

    /// <summary>判断 lambda 是否赋给 Expression&lt;TDelegate&gt;（表达式树）；判定提取到共享 <see cref="ExpressionTreeFacts"/>。</summary>
    public static bool IsExpressionTreeLambda(SemanticModel model, LambdaExpressionSyntax lambda)
        => ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda);
}
