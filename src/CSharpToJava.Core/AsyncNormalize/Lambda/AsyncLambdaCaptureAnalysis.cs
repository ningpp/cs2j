using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.Lambda;

/// <summary>
/// async lambda / async 局部函数的捕获分类（设计 D3 四型）。
/// 主路径为 <see cref="SemanticModel.AnalyzeDataFlow(Microsoft.CodeAnalysis.SyntaxNode)"/>
/// 的 <c>Captured</c> 集——经单元测试实证：零捕获为空集；this 捕获为
/// <see cref="IParameterSymbol.IsThis"/>；局部/形参/foreach 迭代变量各自列出；
/// lambda 自身形参只进 DataFlowsIn、不进 Captured。
/// DataFlow 面不可用（Succeeded=false/抛异常）时退化为语法+符号面判定，
/// 两条路径的分类结果均由测试锁定。
/// </summary>
internal static class AsyncLambdaCaptureAnalysis
{
    /// <summary>
    /// 不带 <c>?</c> 可空引用修饰的 global:: 全限定渲染格式（禁用上下文产物保持干净，
    /// 避免在未开 nullable 的输出树里引入 CS8632 噪声）。
    /// </summary>
    public static readonly SymbolDisplayFormat GlobalFullyQualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public sealed record CaptureResult(
        AsyncLambdaCaptureKind Kind,
        IReadOnlyList<ISymbol> CapturedVariables);

    public static CaptureResult Classify(
        SemanticModel model,
        SyntaxNode body,
        IMethodSymbol? boundaryMethod,
        INamedTypeSymbol? hostTypeSymbol)
    {
        var captured = new List<ISymbol>();
        var hasThis = false;

        DataFlowAnalysis? dataFlow = null;
        var dataFlowWorks = false;
        try
        {
            dataFlow = model.AnalyzeDataFlow(body);
            dataFlowWorks = dataFlow.Succeeded;
        }
        catch (ArgumentException)
        {
            // 跨度不合法（残缺/基线不编译树）时退化为语法面。
            dataFlowWorks = false;
        }

        if (dataFlowWorks)
        {
            foreach (var symbol in dataFlow!.Captured)
            {
                if (symbol is IParameterSymbol { IsThis: true })
                {
                    hasThis = true;
                    continue;
                }

                if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
                    captured.Add(symbol);
            }

            return new CaptureResult(
                captured.Count > 0
                    ? AsyncLambdaCaptureKind.CapturedLocals
                    : hasThis
                        ? AsyncLambdaCaptureKind.ThisOnly
                        : AsyncLambdaCaptureKind.ZeroCapture,
                captured);
        }

        return ClassifySyntactically(model, body, boundaryMethod, hostTypeSymbol);
    }

    /// <summary>
    /// 退化路径（测试直接覆盖）：this = 显式 this/base 表达式 或 对宿主类型实例成员的
    /// 隐式 this 访问；局部/形参 = <see cref="CaptureClassification.CollectCapturedSymbols"/>。
    /// </summary>
    public static CaptureResult ClassifySyntactically(
        SemanticModel model,
        SyntaxNode body,
        IMethodSymbol? boundaryMethod,
        INamedTypeSymbol? hostTypeSymbol)
    {
        var captured = CaptureClassification.CollectCapturedSymbols(model, body, boundaryMethod)
            .Select(static info => info.Symbol)
            .ToList();

        var hasThis = body.DescendantNodesAndSelf()
            .Any(n => n is ThisExpressionSyntax or BaseExpressionSyntax);

        if (!hasThis && hostTypeSymbol != null)
        {
            foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Parent is MemberAccessExpressionSyntax memberAccess
                    && memberAccess.Name == identifier)
                {
                    continue;
                }

                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol))
                    continue;
                if (symbol.IsStatic)
                    continue;
                if (SymbolEqualityComparer.Default.Equals(
                        symbol.ContainingType?.OriginalDefinition, hostTypeSymbol.OriginalDefinition)
                    || HostTypeContains(hostTypeSymbol, symbol.ContainingType))
                {
                    hasThis = true;
                    break;
                }
            }
        }

        var kind = captured.Count > 0
            ? AsyncLambdaCaptureKind.CapturedLocals
            : hasThis
                ? AsyncLambdaCaptureKind.ThisOnly
                : AsyncLambdaCaptureKind.ZeroCapture;
        return new CaptureResult(kind, captured);
    }

    private static bool HostTypeContains(INamedTypeSymbol hostType, INamedTypeSymbol? memberType)
    {
        if (memberType == null) return false;
        var current = hostType;
        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, memberType.OriginalDefinition))
                return true;
            current = current.BaseType;
        }
        return false;
    }
}
