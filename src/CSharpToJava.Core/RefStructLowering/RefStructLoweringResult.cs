namespace CSharpToJava.Core.RefStructLowering;

/// <summary>
/// 单文件 RewriteFile 汇总。<see cref="Changed"/> 为 false 时
/// <see cref="OutputCode"/> 即原始源码（调用方按原样拷贝）。
/// </summary>
public sealed record RefStructLoweringResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required RefStructLoweringStatistics Statistics { get; init; }
    public required IReadOnlyList<RefStructLoweringDiagnostic> Diagnostics { get; init; }

    /// <summary>是否含 Error 级诊断。</summary>
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == RefStructLoweringSeverity.Error);
}
