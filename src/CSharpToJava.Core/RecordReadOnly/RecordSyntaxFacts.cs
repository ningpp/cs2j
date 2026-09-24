using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

/// <summary>
/// 纯语法/语义小工具。所有判据集中在这里，Analyzer/Plan/各 Rewriter 共用，
/// 保证"什么可写/什么算副作用/什么是纯 getter"全特性只有一套口径。
/// </summary>
internal static class RecordSyntaxFacts
{
    public static readonly SyntaxAnnotation RewriteAnnotation = new("Cs2jRecordReadOnlyRewrite");

    /// <summary>标识符/简单成员访问/纯索引器（参数为常量/标识符）以外一律视为有副作用。</summary>
    public static bool IsReceiverSideEffectFree(ExpressionSyntax receiver)
    {
        foreach (var node in receiver.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax:
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                case SimpleLambdaExpressionSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                case AwaitExpressionSyntax:
                case AssignmentExpressionSyntax:
                case QueryExpressionSyntax:
                    return false;
                case PrefixUnaryExpressionSyntax pre when pre.Kind()
                    is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    return false;
                case PostfixUnaryExpressionSyntax post when post.Kind()
                    is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                    return false;
                // arr[F()] 已被上面的 InvocationExpression 覆盖；这里再拦 ++ 下标等。
                case ElementAccessExpressionSyntax element when element.ArgumentList.Arguments
                    .Any(a => a.Expression is InvocationExpressionSyntax
                        or AssignmentExpressionSyntax
                        or PrefixUnaryExpressionSyntax
                        or PostfixUnaryExpressionSyntax):
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 复合赋值/自增改写为 with 前必须证明 getter 读一次无外部可观察副作用：
    /// 仅接受自动属性（含字段被 RS-5 提升后生成的自动属性）与字段访问。
    /// </summary>
    public static bool IsPureGetter(ISymbol? member)
    {
        return member is IFieldSymbol
            || (member is IPropertySymbol { GetMethod: { } getMethod } property
                && IsAutoAccessor(getMethod)
                && !property.ReturnsByRef && !property.ReturnsByRefReadonly);
    }

    public static bool IsAutoAccessor(IMethodSymbol accessor)
    {
        // 编译器合成（自动属性 get/set/init）或源里没有访问器体（get;）都算自动。
        if (accessor.IsImplicitlyDeclared) return true;
        foreach (var decl in accessor.DeclaringSyntaxReferences)
        {
            if (decl.GetSyntax() is AccessorDeclarationSyntax { Body: null, ExpressionBody: null })
                return true;
        }
        return false;
    }

    public static bool IsVisibleOutsideType(ISymbol symbol)
    {
        var accessibility = symbol.DeclaredAccessibility;
        return accessibility is Accessibility.Public
            or Accessibility.Internal
            or Accessibility.Protected
            or Accessibility.ProtectedOrInternal;
    }

    public static bool IsPrivate(ISymbol symbol)
        => symbol.DeclaredAccessibility == Accessibility.Private;

    /// <summary>全限定类型名语法（global:: 前缀），泛型携带 type argument。</summary>
    public static TypeSyntax GlobalType(ITypeSymbol symbol)
        => SyntaxFactory.ParseTypeName(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    public static bool IsInTree(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

    /// <summary>找 record 声明上的 StructLayout 特性，返回 LayoutKind 实参文本（Explicit/Sequential/Auto）。</summary>
    public static bool HasExplicitLayout(INamedTypeSymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != "System.Runtime.InteropServices.StructLayoutAttribute")
                continue;
            if (attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is int kind
                && kind == 2) // LayoutKind.Explicit == 2
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsRefReturning(IMethodSymbol method)
        => (method.ReturnsByRef || method.ReturnsByRefReadonly) && IsVisibleOutsideType(method);

    /// <summary>把 set 访问器关键字替换为 init（保留 trivia）；非 set 访问器原样。</summary>
    public static AccessorDeclarationSyntax WithInitKeyword(AccessorDeclarationSyntax accessor)
    {
        if (!accessor.IsKind(SyntaxKind.SetAccessorDeclaration)) return accessor;
        var keyword = SyntaxFactory.Token(SyntaxKind.InitKeyword)
            .WithLeadingTrivia(accessor.Keyword.LeadingTrivia)
            .WithTrailingTrivia(accessor.Keyword.TrailingTrivia);
        return accessor.WithKeyword(keyword);
    }
}
