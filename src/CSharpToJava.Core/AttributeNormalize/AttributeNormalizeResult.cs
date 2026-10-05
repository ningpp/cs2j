namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// 单文件 attribute-normalize 结果（design §4.1）。
/// </summary>
public sealed record AttributeNormalizeFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<AttributeNormalizeDiagnostic> Diagnostics,
    AttributeNormalizeStatistics Statistics);
