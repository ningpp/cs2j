using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// C# 无符号类型预处理器公共入口。将 byte/ushort/uint/ulong 关键字替换为
/// System.* 全名,并把无符号类型上的运算符/cast 重写为
/// Cs2jUnsigned.UnsignedMath 静态调用。clean 文件字节级保真;改写文件保留
/// 原始 trivia 直接输出(中间过程代码不做格式化),并在输出前做语法校验——
/// 任何情况下都不产出语法损坏的中间 C#。
/// </summary>
public sealed class UnsignedTypeRewriter
{
    public UnsignedTypeRewriterResult Rewrite(
        string sourceCode,
        SemanticModel? semanticModel = null,
        UnsignedTypeRewriterOptions? options = null)
    {
        options ??= new UnsignedTypeRewriterOptions();
        var stats = new UnsignedTypeRewriterStatistics { FilesScanned = 1 };
        var diagnostics = new List<UnsignedTypeRewriterDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!UnsignedTypeSyntaxRewriter.IsDirty(root, options))
        {
            stats.FilesSkippedClean = 1;
            return new UnsignedTypeRewriterResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new UnsignedTypeSyntaxRewriter(semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        var postfixRewriter = new UnsignedPostfixRewriter(stats);
        newRoot = (CompilationUnitSyntax)postfixRewriter.Visit(newRoot)!;

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            // 兜底:重写结果无法产出合法 C#,保留原始文件并报告错误。
            stats.FilesTransformed = 0;
            stats.FilesSkippedClean = 1;
            diagnostics.Add(new UnsignedTypeRewriterDiagnostic(
                UnsignedTypeSeverity.Error,
                "unsigned rewrite produced invalid C# syntax; the original file was preserved."));
            return new UnsignedTypeRewriterResult(sourceCode, false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new UnsignedTypeRewriterResult(outputCode, true, diagnostics, stats);
    }

    /// <summary>
    /// 输出未格式化文本并做一次重新解析校验:重写器所有构造点都用
    /// WithTriviaFrom/trivia 提升保留了原始空白,产物仅供后续转换消费,
    /// 无需调用 Roslyn Formatter(多 span 格式化在超长数组初始化行上会
    /// 随 span 数超线性变慢)。返回 null 表示重写结果含语法错误,调用方
    /// 保留原始文件。
    /// </summary>
    private static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var outputCode = root.ToFullString();
        var reparsed = CSharpSyntaxTree.ParseText(outputCode);
        return reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)
            ? null
            : outputCode;
    }
}
