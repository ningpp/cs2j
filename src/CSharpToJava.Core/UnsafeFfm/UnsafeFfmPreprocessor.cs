using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 unsafe-ffm 预处理 pass 公共门面（C#→C#，设计 §4）。单次 parse：
/// 直接使用调用方已解析树的 <see cref="SemanticModel"/>，规则在内存中顺序
/// 合批（M0 仅链 R1 <see cref="UnsafeContextStripper"/>；后续里程碑在此
/// 追加 R2–R8），R1 输出经 unsafe 上下文残留自检后格式化并做本地解析校验。
/// 拒绝优先：任一 Error 即文件 <see cref="UnsafeFfmResult.Changed"/>=false、
/// 原文保留；输出可被自身二次安全驱动（幂等）。
/// </summary>
public sealed class UnsafeFfmPreprocessor
{
    private readonly UnsafeFfmOptions _options;

    public UnsafeFfmPreprocessor(UnsafeFfmOptions? options = null)
    {
        _options = options ?? new UnsafeFfmOptions();
    }

    /// <summary>
    /// 改写单个文件。语义模型由调用方在整树编译（allowUnsafe）上一次性提供。
    /// </summary>
    public UnsafeFfmResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new UnsafeFfmStatistics();
        var diagnostics = new List<UnsafeFfmDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // M0：仅 R1。R2–R8 在后续里程碑按序接入同一棵树（单次 parse 合批）。
        var newRoot = (CompilationUnitSyntax)new UnsafeContextStripper(statistics).Visit(root)!;

        // R1 残留自检（M0 只查 unsafe 块/unsafe 修饰符；pointer/fixed/stackalloc
        // 全量 residual 闸门随后续里程碑激活）：命中即规则缺口，UF010 拒绝。
        if (HasUnsafeContextResidual(newRoot))
        {
            statistics.ResidualCount++;
            statistics.Rejections++;
            diagnostics.Add(new UnsafeFfmDiagnostic(
                UnsafeFfmSeverity.Error,
                UnsafeFfmDiagnosticCode.UF010,
                "unsafe-context residual gate: the rewritten tree still contains an unsafe block " +
                "or unsafe modifier after R1 (internal rule gap); the original file was preserved.",
                filePath, 0, 0));
            return new UnsafeFfmResult
            {
                OutputCode = sourceCode,
                Changed = false,
                Statistics = statistics,
                Diagnostics = diagnostics,
            };
        }

        if (newRoot.IsEquivalentTo(root))
        {
            statistics.FilesSkippedClean++;
            return new UnsafeFfmResult
            {
                OutputCode = sourceCode,
                Changed = false,
                Statistics = statistics,
                Diagnostics = diagnostics,
            };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new UnsafeFfmDiagnostic(
                UnsafeFfmSeverity.Warning,
                UnsafeFfmDiagnosticCode.UF010,
                "unsafe-ffm rewrite failed local parse validation; the original file was preserved " +
                "pending whole-tree build/test verification.",
                filePath, 0, 0));
            return new UnsafeFfmResult
            {
                OutputCode = sourceCode,
                Changed = false,
                Statistics = statistics,
                Diagnostics = diagnostics,
            };
        }

        statistics.FilesTransformed++;
        return new UnsafeFfmResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// R1 残留扫描：仍存在 <see cref="UnsafeStatementSyntax"/> 或 unsafe keyword
    /// token（任何位置的 unsafe 修饰符）即视为残留。
    /// </summary>
    private static bool HasUnsafeContextResidual(CompilationUnitSyntax root)
    {
        if (root.DescendantNodes().OfType<UnsafeStatementSyntax>().Any()) return true;
        return root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.UnsafeKeyword));
    }

    /// <summary>整树格式化 → 原文候选，每个候选重新解析零 Error 才返回。</summary>
    private static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                candidates.Add(Formatter.Format(root, workspace).ToFullString());
            }
            catch
            {
                // 格式化失败不致命，原文候选兜底。
            }
        }
        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch
            {
                return false;
            }
        });
    }
}
