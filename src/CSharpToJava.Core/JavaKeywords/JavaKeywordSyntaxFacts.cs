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
    /// JK-0：strip(name) 或 decap(strip(name)) 任一是 Java 关键字即命中。
    /// </summary>
    public static bool NeedsEscape(string name)
    {
        var s = StripAt(name);
        return JavaNaming.IsJavaKeyword(s) || JavaNaming.IsJavaKeyword(Decap(s));
    }

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

    /// <summary>判断 lambda 是否赋给 Expression&lt;TDelegate&gt;（表达式树）。</summary>
    public static bool IsExpressionTreeLambda(SemanticModel model, LambdaExpressionSyntax lambda)
    {
        var typeInfo = model.GetTypeInfo(lambda);
        var convertedType = typeInfo.ConvertedType;
        if (convertedType == null) return false;
        var fullName = convertedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName.StartsWith("global::System.Linq.Expressions.Expression<", StringComparison.Ordinal);
    }
}
