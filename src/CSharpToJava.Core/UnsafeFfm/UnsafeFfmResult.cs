namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// 单文件 RewriteFile 汇总（设计 §4：诊断 + 统计 + Changed）。
/// <see cref="Changed"/> 为 false 时 <see cref="OutputCode"/> 即原始源码
/// （调用方按原样拷贝，不落盘改写）。
/// </summary>
public sealed record UnsafeFfmResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required UnsafeFfmStatistics Statistics { get; init; }
    public required IReadOnlyList<UnsafeFfmDiagnostic> Diagnostics { get; init; }

    /// <summary>是否含 Error 级诊断。</summary>
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);
}
