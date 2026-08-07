using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 相等语义 rewriter（Task 26，M3，spec §2.6.4）。
/// 语法树层改写（沿用 Task 19/23/24 模式）：IR 方法体为 raw 文本且不携带类型信息，
/// record/struct/覆写 Equals 的类的 ==/!=/.Equals() 调用点判定必须在语法树 + 语义
/// 模型上完成（前端 InvocationExpressionTransformer/BinaryExpressionTransformer 的
/// Objects.equals 等 Java 形态无 TS 分叉，统一在此预处理）：
/// ① 原始类型/字符串/普通引用类型/可空 null 的 ==/!= 保持运算符形态，由
///    BinaryExpressionTransformer 的 TS 分叉发射 ===/!==（值/引用语义两端一致）；
/// ② record/struct/覆写 Equals 的类的 == → CsObject.Equals(a, b)、!= →
///    !CsObject.Equals(a, b)（CsObject.equals 深比较、优先实例 equals）；
/// ③ 实例 .Equals(x)：接收者为 record/struct/覆写 Equals 类 → CsObject.Equals(a, x)；
///    字符串/原始类型/普通类 → (a == x)（TS ===，值/引用语义一致）；
/// ④ 静态 Equals(a, b)（object./string./无接收者）→ CsObject.Equals(a, b)；
/// ⑤ ReferenceEquals(a, b) → (a == b)（TS ===）；
/// ⑥ GetHashCode() 直译 hashCode()（既有映射，无需改写）。
/// CsObject 登记进 context.TsCompatSymbols 生成 @csharptotypescript/compat 导入。
/// 已知限制（登记于计划 Task 26）：显式 operator== 的引用类型类不改写（Task 27
/// 运算符重载处理调用点）；IEquatable&lt;T&gt; 单实现（未覆写 object.Equals）按普通类；
/// 动态/类型参数接收者保守跳过。
/// </summary>
public sealed class TsEqualityRewriter : CSharpSyntaxRewriter
{
    private readonly ConversionContext _context;
    private readonly SemanticModel _semanticModel;

    public TsEqualityRewriter(ConversionContext context)
    {
        _context = context;
        _semanticModel = context.SemanticModel!;
    }

    /// <summary>相等目标类别：仅任务范围内类型参与 CsObject.equals 改写。</summary>
    private enum EqualityKind
    {
        Primitive,  // 原始类型/字符串/枚举/委托/普通引用类型/object/null 字面量 → ===
        Value,      // record/struct/覆写 Equals 的类 → CsObject.equals
        Unknown,    // 错误类型/类型参数/无法判定 → 保守不动
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var kind = node.Kind();
        if (kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression))
        {
            return base.VisitBinaryExpression(node);
        }

        // 显式 operator==/!= 的引用类型类不改写（Task 27 运算符重载处理调用点）；
        // record 合成的 operator==（IsImplicitlyDeclared）与 struct 的 operator==
        // 仍按类型类别改写（spec：record/struct 的 == → CsObject.equals）。
        if (_semanticModel.GetSymbolInfo(node).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } userOp
            && !userOp.IsImplicitlyDeclared
            && userOp.ContainingType is { IsValueType: false })
        {
            return base.VisitBinaryExpression(node);
        }

        var leftKind = Classify(node.Left);
        var rightKind = Classify(node.Right);
        var rewritten = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        if (leftKind != EqualityKind.Value || rightKind != EqualityKind.Value)
        {
            return rewritten;
        }

        var equalsCall = BuildCsObjectEquals(rewritten.Left, rewritten.Right);
        if (kind == SyntaxKind.NotEqualsExpression)
        {
            return SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression, equalsCall)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
        return equalsCall
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var rewritten = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        var symbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;

        // 静态 Equals(a, b)（object.Equals/string.Equals/静态类内无接收者）→ CsObject.Equals(a, b)
        if (symbol is { IsStatic: true, Name: "Equals", Parameters.Length: 2 }
            && symbol.ContainingType?.SpecialType is SpecialType.System_Object or SpecialType.System_String
            && rewritten.ArgumentList.Arguments.Count == 2)
        {
            return BuildCsObjectEquals(
                rewritten.ArgumentList.Arguments[0].Expression,
                rewritten.ArgumentList.Arguments[1].Expression,
                node);
        }

        // ReferenceEquals(a, b) → (a == b)（前端 ReferenceEquals 分支对标识符/成员访问
        // 形态均产出 == raw；改写为二元表达式统一走 TS === 发射，结构化/raw 两路一致）
        if (symbol is { Name: "ReferenceEquals", Parameters.Length: 2, IsStatic: true }
            && rewritten.ArgumentList.Arguments.Count == 2)
        {
            return SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                rewritten.ArgumentList.Arguments[0].Expression,
                rewritten.ArgumentList.Arguments[1].Expression)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }

        // 实例 .Equals(x)：成员访问形态，1 参，非 base 接收者
        if (symbol is { IsStatic: false, Name: "Equals", Parameters.Length: 1 }
            && node.Expression is MemberAccessExpressionSyntax memberAccess
            && !memberAccess.Expression.IsKind(SyntaxKind.BaseExpression)
            && rewritten.ArgumentList.Arguments.Count == 1)
        {
            var receiver = rewritten.Expression is MemberAccessExpressionSyntax rewrittenAccess
                ? rewrittenAccess.Expression
                : memberAccess.Expression;
            var receiverKind = Classify(memberAccess.Expression);
            if (receiverKind == EqualityKind.Value)
            {
                // record/struct/覆写 Equals 的类 → CsObject.Equals(receiver, arg)
                return BuildCsObjectEquals(receiver, rewritten.ArgumentList.Arguments[0].Expression, node);
            }
            if (receiverKind == EqualityKind.Primitive)
            {
                // 字符串/原始类型/普通引用类 → (receiver == arg) → TS ===
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    receiver,
                    rewritten.ArgumentList.Arguments[0].Expression)
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());
            }
        }

        return rewritten;
    }

    private InvocationExpressionSyntax BuildCsObjectEquals(ExpressionSyntax left, ExpressionSyntax right)
    {
        UseCompat("CsObject");
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("CsObject"),
                // 方法名用 C# 大小写 Equals：前端映射表 Equals→equals 发射为
                // CsObject.equals(...)，且 UnaryExpressionTransformer 的布尔命名
                // 启发式（IsBooleanMethodName("Equals")）使 ! 前缀正确发射（未知
                // 符号调用不会被误降级为 == 0）。
                SyntaxFactory.IdentifierName("Equals")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(left),
                    SyntaxFactory.Argument(right),
                })));
    }

    private InvocationExpressionSyntax BuildCsObjectEquals(ExpressionSyntax left, ExpressionSyntax right, SyntaxNode original)
    {
        return BuildCsObjectEquals(left, right)
            .WithLeadingTrivia(original.GetLeadingTrivia())
            .WithTrailingTrivia(original.GetTrailingTrivia());
    }

    private void UseCompat(string symbol)
    {
        _context.TsCompatSymbols.Add(symbol);
    }

    private EqualityKind Classify(ExpressionSyntax expr)
    {
        // null/default 字面量：保持运算符形态（可空 == null → === null，spec §1.4.7）
        if (expr.IsKind(SyntaxKind.NullLiteralExpression)
            || expr.IsKind(SyntaxKind.DefaultLiteralExpression))
        {
            return EqualityKind.Primitive;
        }
        return ClassifyType(_semanticModel.GetTypeInfo(expr).Type);
    }

    private static EqualityKind ClassifyType(ITypeSymbol? type)
    {
        if (type is null || type.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
        {
            return EqualityKind.Unknown;
        }
        if (type.TypeKind == TypeKind.Enum || type.TypeKind == TypeKind.Delegate)
        {
            return EqualityKind.Primitive;
        }
        if (IsPrimitiveOrString(type))
        {
            return EqualityKind.Primitive;
        }
        if (type.IsValueType)
        {
            // Nullable<T>：按底层类型（int? == null → === null；S? == null 亦 ===）
            if (type is INamedTypeSymbol named
                && named.IsGenericType
                && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                && named.TypeArguments.Length == 1)
            {
                return ClassifyType(named.TypeArguments[0]);
            }
            // struct / record struct（含 DateTime/Decimal/Guid 等 BCL 值类型）→ CsObject.equals
            return EqualityKind.Value;
        }
        if (type.IsRecord)
        {
            return EqualityKind.Value;
        }
        if (OverridesObjectEquals(type))
        {
            return EqualityKind.Value;
        }
        // 普通类/接口/object → ===（C# 默认引用相等与 TS === 一致）
        return EqualityKind.Primitive;
    }

    private static bool IsPrimitiveOrString(ITypeSymbol type)
    {
        return type.SpecialType is
            SpecialType.System_Boolean or
            SpecialType.System_Byte or
            SpecialType.System_SByte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_Char or
            SpecialType.System_String;
    }

    private static bool OverridesObjectEquals(ITypeSymbol type)
    {
        foreach (var member in type.GetMembers("Equals"))
        {
            if (member is IMethodSymbol { Parameters.Length: 1, IsOverride: true } method
                && method.OverriddenMethod is { } overridden
                && overridden.ContainingType?.SpecialType == SpecialType.System_Object)
            {
                return true;
            }
        }
        return false;
    }
}
