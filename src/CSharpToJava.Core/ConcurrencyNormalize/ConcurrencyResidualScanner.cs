using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ConcurrencyNormalize;

/// <summary>
/// 残余闸门扫描器（设计 §4.1/§7.4）：重写后扫描输出树全部 System.Threading.Monitor
/// 绑定站点（Enter/TryEnter/Exit/Wait/Pulse/PulseAll），与 plan 集合（active/skipped
/// 均计，回退排除的 plan 描述的正是回退后的原样代码）对账；无归属残余 &gt;0 → CS2J2970。
/// 闸门零未知 Monitor 站点。
/// </summary>
internal static class ConcurrencyResidualScanner
{
    public static IReadOnlyList<ConcurrencyDiagnostic> Scan(
        Compilation compilation,
        IReadOnlyList<ConcurrencyRegionPlan> plans,
        ConcurrencyStatistics? statistics)
    {
        var diagnostics = new List<ConcurrencyDiagnostic>();
        var monitorType = compilation.GetTypeByMetadataName("System.Threading.Monitor");
        if (monitorType == null)
        {
            if (statistics != null) statistics.ResidualMonitorCalls = 0;
            return diagnostics;
        }

        // (文件, 方法限定名) → 归属 plan 存在。
        var ownedMethods = new HashSet<(string File, string Method)>(
            plans.Select(p => p.TouchedFiles.Select(f => (File: NormalizePath(f), Method: p.MethodQualifiedName)))
                .SelectMany(x => x));

        var residual = 0;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var filePath = NormalizePath(tree.FilePath);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) continue;
                var containingType = method.OriginalDefinition?.ContainingType;
                if (containingType == null
                    || !SymbolEqualityComparer.Default.Equals(containingType, monitorType))
                    continue;

                residual++;
                var qualifiedName = ResolveEnclosingMethodQualifiedName(model, invocation);
                if (qualifiedName == null || !ownedMethods.Contains((filePath, qualifiedName)))
                {
                    var lineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
                    diagnostics.Add(new ConcurrencyDiagnostic(
                        ConcurrencySeverity.Warning,
                        ConcurrencyRule.CN2,
                        ConcurrencyDiagnosticCodes.ResidualMonitorGate,
                        "residual Monitor call has no owning region plan and no skip " +
                        "diagnostic; the residual gate requires zero unknown Monitor sites " +
                        "(2910/2921/2930 sites are whitelisted).",
                        tree.FilePath,
                        lineSpan.Line + 1,
                        lineSpan.Character + 1,
                        qualifiedName));
                }
            }
        }

        if (statistics != null) statistics.ResidualMonitorCalls = residual;
        return diagnostics;
    }

    private static string? ResolveEnclosingMethodQualifiedName(SemanticModel model, SyntaxNode node)
    {
        // 与 MonitorRewriteAnalyzer.EnclosingOrdinaryMethod 同口径：lambda/局部函数
        // 归入其所在普通方法（plan 以普通方法符号为键）。
        var symbol = model.GetEnclosingSymbol(node.SpanStart);
        while (symbol != null)
        {
            if (symbol is IMethodSymbol method
                && method.MethodKind is MethodKind.Ordinary
                    or MethodKind.Constructor
                    or MethodKind.StaticConstructor
                    or MethodKind.PropertyGet
                    or MethodKind.PropertySet
                    or MethodKind.EventAdd
                    or MethodKind.EventRemove)
            {
                return method.ToDisplayString();
            }
            symbol = symbol.ContainingSymbol;
        }
        return null;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/');
}
