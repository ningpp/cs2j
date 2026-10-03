using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>residual 闸门结果（设计 §4 组件表 / D11）。</summary>
public sealed class ResidualGateResult
{
    public required int ResidualCount { get; init; }
    public required IReadOnlyList<PlatformIsolationDiagnostic> Diagnostics { get; init; }
    public bool HasErrors => ResidualCount > 0;
}

/// <summary>
/// C42 M2 residual 闸门（设计 D11）：对 apply 后的输出树双扫（语法节点 + 三表
/// 语义身份），<c>generated/**</c> 与配置 suppress 登记面豁免；任一非登记残留
/// → PI031 Error，文件不产出。
/// </summary>
public static class PlatformResidualGate
{
    public static ResidualGateResult Scan(
        Compilation outputCompilation,
        Func<string, bool>? isGeneratedFile,
        PlatformIsolationConfig? config)
    {
        ArgumentNullException.ThrowIfNull(outputCompilation);

        var inventory = BoundaryInventoryBuilder.Build(outputCompilation);
        var diagnostics = new List<PlatformIsolationDiagnostic>();
        var residual = 0;

        foreach (var site in inventory.Sites)
        {
            if (isGeneratedFile != null && isGeneratedFile(site.File))
            {
                continue;
            }

            if (PlatformIsolationConfigMatcher.FindSuppressEntry(config, site) != null)
            {
                continue;
            }

            residual++;
            diagnostics.Add(new PlatformIsolationDiagnostic(
                PlatformIsolationDiagnosticCodes.ResidualGateViolated,
                CoreDiagnosticSeverity.Error,
                $"Residual platform boundary site '{site.Identity}' ({site.Code}) at " +
                $"{site.File}:{site.Line + 1}:{site.Column} survived platform-isolation.",
                site.File));
        }

        return new ResidualGateResult
        {
            ResidualCount = residual,
            Diagnostics = diagnostics,
        };
    }
}