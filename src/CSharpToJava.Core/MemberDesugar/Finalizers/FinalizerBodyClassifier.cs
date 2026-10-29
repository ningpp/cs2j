using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Finalizers;

/// <summary>
/// FN-2 终结器体分类（纯静态，设计 §2 决策 4 / §4.2）。全部判定基于 SemanticModel
/// 符号绑定，不做文本匹配：
/// Extern（无体/extern）→ Empty（无有效语句，Debug.Assert/空语句/注释为白名单噪声）→
/// DisposePattern（唯一有效语句为本类型 Dispose()/Dispose(false)）→ NativeRelease
/// （Marshal.Free*/NativeLibrary.Free）→ SafeHandleOnly（符号流仅限 SafeHandle/CriticalHandle
/// 派生字段与其成员）→ Other。单文件降级时 SafeHandleOnly/NativeRelease 保守退化为 Other。
/// </summary>
internal static class FinalizerBodyClassifier
{
    public static FinalizerBodyKind Classify(
        DestructorDeclarationSyntax node,
        SemanticModel model,
        IMethodSymbol destructor,
        bool degraded)
    {
        // 无体（extern ~T();，Body == null；带 extern 修饰同样落此）。
        if (node.Body is null || node.Modifiers.Any(SyntaxKind.ExternKeyword))
            return FinalizerBodyKind.Extern;

        var effective = node.Body.Statements
            .Where(s => !IsNoise(s, model))
            .ToList();
        if (effective.Count == 0)
            return FinalizerBodyKind.Empty;

        if (effective.Count == 1 && IsDisposePatternCall(effective[0], model, destructor))
            return FinalizerBodyKind.DisposePattern;

        if (IsDisposeOwnedMember(effective, model, destructor))
            return FinalizerBodyKind.DisposeOwnedMember;

        if (ContainsNativeRelease(effective, model))
            return degraded ? FinalizerBodyKind.Other : FinalizerBodyKind.NativeRelease;

        if (!degraded && IsSafeHandleOnly(effective, model))
            return FinalizerBodyKind.SafeHandleOnly;

        return FinalizerBodyKind.Other;
    }

    /// <summary>白名单噪声：空语句、System.Diagnostics.Debug.Assert(...) 调用（注释是 trivia，
    /// 天然不在 Statements 内）。允许出现在 Empty/DisposePattern 判定中。</summary>
    private static bool IsNoise(StatementSyntax statement, SemanticModel model)
    {
        if (statement.IsKind(SyntaxKind.EmptyStatement)) return true;
        if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation })
            return false;
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
        return method is { Name: "Assert" }
            && method.ContainingType?.ToDisplayString() == "System.Diagnostics.Debug";
    }

    private static bool IsDisposePatternCall(
        StatementSyntax statement, SemanticModel model, IMethodSymbol destructor)
    {
        if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation })
            return false;
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
        if (method.Name != "Dispose") return false;
        var containingType = destructor.ContainingType;
        if (containingType == null
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType, containingType))
            return false;

        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0) return true; // Dispose()
        if (args.Count == 1 && args[0].Expression.IsKind(SyntaxKind.FalseLiteralExpression))
            return true;            // Dispose(false)
        return false;
    }

    /// <summary>
    /// 「释放自有成员」终结器体：唯一有效语句是接收者为本类型实例字段/属性的无参
    /// <c>Dispose()</c> 调用，可被一个 null 守卫包裹：
    /// <c>if (Logger != null) Logger.Dispose();</c>（AGL LayeredLayoutEngine）。
    /// 只认 Dispose；flush/日志等其它清理逻辑仍落 Other。接收者允许是 C11 属性降糖
    /// 后的 <c>get_X()</c> 访问器调用，保证重复运行/已降糖输入幂等。
    /// </summary>
    private static bool IsDisposeOwnedMember(
        IReadOnlyList<StatementSyntax> effective, SemanticModel model, IMethodSymbol destructor)
    {
        if (effective.Count != 1) return false;

        InvocationExpressionSyntax? disposeCall;
        switch (effective[0])
        {
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax direct }:
                disposeCall = direct;
                break;
            case IfStatementSyntax { Else: null } guard
                when guard.Statement is ExpressionStatementSyntax
                    { Expression: InvocationExpressionSyntax guarded }
                    && guarded.Expression is MemberAccessExpressionSyntax guardedAccess
                    && IsNullGuardOn(guard.Condition, guardedAccess.Expression, model):
                disposeCall = guarded;
                break;
            default:
                return false;
        }

        if (disposeCall.ArgumentList.Arguments.Count != 0) return false;
        if (disposeCall.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
        if (model.GetSymbolInfo(disposeCall).Symbol is not IMethodSymbol { Name: "Dispose" }) return false;
        return IsOwnedMemberReceiver(memberAccess.Expression, model, destructor);
    }

    private static bool IsNullGuardOn(
        ExpressionSyntax condition, ExpressionSyntax disposed, SemanticModel model)
    {
        condition = UnwrapParentheses(condition);
        switch (condition)
        {
            // if (!object.ReferenceEquals(x, null)) x.Dispose();
            case PrefixUnaryExpressionSyntax { Operand: var operand }
                when condition.IsKind(SyntaxKind.LogicalNotExpression):
                return IsReferenceEqualsNullCheck(
                    UnwrapParentheses(operand), disposed, model);

            // if (x != null) / if (null != x)
            case BinaryExpressionSyntax neq when neq.IsKind(SyntaxKind.NotEqualsExpression):
                return (IsNullLiteral(neq.Left) && SameExpression(neq.Right, disposed, model))
                    || (IsNullLiteral(neq.Right) && SameExpression(neq.Left, disposed, model));

            // if (x is not null)
            case IsPatternExpressionSyntax isPattern
                when isPattern.Pattern is UnaryPatternSyntax
                    { Pattern: ConstantPatternSyntax { Expression: var nullPattern } }:
                return IsNullLiteral(nullPattern)
                    && SameExpression(isPattern.Expression, disposed, model);

            default:
                return false;
        }
    }

    private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private static bool IsReferenceEqualsNullCheck(
        ExpressionSyntax expression, ExpressionSyntax disposed, SemanticModel model)
    {
        if (expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 2 } invocation)
            return false;
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { Name: "ReferenceEquals" } method
            || method.ContainingType?.SpecialType != SpecialType.System_Object)
            return false;
        var first = invocation.ArgumentList.Arguments[0].Expression;
        var second = invocation.ArgumentList.Arguments[1].Expression;
        return (IsNullLiteral(second) && SameExpression(first, disposed, model))
            || (IsNullLiteral(first) && SameExpression(second, disposed, model));
    }

    private static bool SameExpression(
        ExpressionSyntax left, ExpressionSyntax right, SemanticModel model)
    {
        var leftSymbol = model.GetSymbolInfo(left).Symbol;
        var rightSymbol = model.GetSymbolInfo(right).Symbol;
        if (leftSymbol != null && rightSymbol != null)
            return SymbolEqualityComparer.Default.Equals(leftSymbol, rightSymbol);
        return string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }

    private static bool IsNullLiteral(ExpressionSyntax expression)
        => expression.IsKind(SyntaxKind.NullLiteralExpression);

    /// <summary>接收者必须是本类型的实例字段/属性（自有资源），不接受 this、静态成员
    /// 或其它类型的成员链——那些形态没有「本对象持有的资源」这一释放语义。
    /// SafeHandle/CriticalHandle 成员有自己的关键终结器，归 SafeHandleOnly 家族
    /// （单文件降级时仍保守落 Other），不在本分类内接管。</summary>
    private static bool IsOwnedMemberReceiver(
        ExpressionSyntax receiver, SemanticModel model, IMethodSymbol destructor)
    {
        var containingType = destructor.ContainingType;
        if (containingType == null) return false;

        ITypeSymbol? memberType;
        if (receiver is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } getter
            && model.GetSymbolInfo(getter).Symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol owner } accessor)
        {
            if (accessor.IsStatic
                || !SymbolEqualityComparer.Default.Equals(owner.ContainingType, containingType))
                return false;
            memberType = owner.Type;
        }
        else
        {
            memberType = model.GetSymbolInfo(receiver).Symbol switch
            {
                IFieldSymbol { IsStatic: false } field
                    when SymbolEqualityComparer.Default.Equals(field.ContainingType, containingType)
                    => field.Type,
                IPropertySymbol { IsStatic: false } property
                    when SymbolEqualityComparer.Default.Equals(property.ContainingType, containingType)
                    => property.Type,
                _ => null,
            };
        }

        if (memberType == null) return false;
        var safeHandle = model.Compilation.GetTypeByMetadataName(
            "System.Runtime.InteropServices.SafeHandle");
        var criticalHandle = model.Compilation.GetTypeByMetadataName(
            "System.Runtime.InteropServices.CriticalHandle");
        return !IsHandleType(memberType, safeHandle, criticalHandle);
    }

    private static bool ContainsNativeRelease(
        IReadOnlyList<StatementSyntax> effective, SemanticModel model)
    {
        var compilation = model.Compilation;
        var marshal = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.Marshal");
        var nativeLibrary = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.NativeLibrary");

        foreach (var invocation in effective.SelectMany(s => s.DescendantNodes())
                     .OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) continue;
            var container = method.ContainingType;
            if (container == null) continue;
            if (SymbolEqualityComparer.Default.Equals(container.OriginalDefinition, marshal)
                && method.Name is "FreeHGlobal" or "FreeCoTaskMem" or "DestroyStructure")
                return true;
            if (SymbolEqualityComparer.Default.Equals(container.OriginalDefinition, nativeLibrary)
                && method.Name == "Free")
                return true;
        }
        return false;
    }

    /// <summary>有效语句的全部符号流仅限 SafeHandle/CriticalHandle 派生字段及其成员，
    /// 且至少访问一个句柄字段；出现局部声明/对象创建/事件/非句柄字段或方法即落 Other。
    /// 只扫描有效语句（白名单噪声已在分类入口剥离）。</summary>
    private static bool IsSafeHandleOnly(
        IReadOnlyList<StatementSyntax> effective, SemanticModel model)
    {
        var compilation = model.Compilation;
        var safeHandle = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.SafeHandle");
        var criticalHandle = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.CriticalHandle");
        if (safeHandle == null && criticalHandle == null) return false;

        var effectiveNodes = effective.SelectMany(s => s.DescendantNodes()).ToList();

        // 局部声明/对象创建在 M1 目标形态中不存在，出现即保守 Other。
        if (effectiveNodes.Any(n =>
                n is LocalDeclarationStatementSyntax or ObjectCreationExpressionSyntax))
            return false;

        var sawHandleField = false;
        foreach (var node in effectiveNodes)
        {
            // 调用的方法必须声明在 SafeHandle/CriticalHandle 派生类型上（Dispose/ReleaseHandle 等）。
            if (node is InvocationExpressionSyntax invocation)
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
                if (!IsHandleType(method.ContainingType, safeHandle, criticalHandle)) return false;
                continue;
            }

            if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) continue;
            switch (model.GetSymbolInfo(node).Symbol)
            {
                case null:
                    continue;
                case IFieldSymbol field:
                    if (!IsHandleType(field.Type, safeHandle, criticalHandle)) return false;
                    sawHandleField = true;
                    break;
                case IPropertySymbol property:
                    if (!IsHandleType(property.ContainingType, safeHandle, criticalHandle)) return false;
                    break;
                case IMethodSymbol:
                    // 方法级约束在 invocation 分支处理；其余方法形态（如属性访问器）宽容放行。
                    break;
                default:
                    // IEventSymbol / ILocalSymbol / IParameterSymbol 等：非纯句柄形态。
                    return false;
            }
        }

        return sawHandleField;
    }

    private static bool IsHandleType(
        ITypeSymbol? type, INamedTypeSymbol? safeHandle, INamedTypeSymbol? criticalHandle)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (safeHandle != null
                && SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, safeHandle))
                return true;
            if (criticalHandle != null
                && SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, criticalHandle))
                return true;
        }
        return false;
    }
}
