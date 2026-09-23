using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// C# 无符号类型预处理器公共入口。将 byte/ushort/uint/ulong 关键字替换为
/// System.* 全名,并把无符号类型上的运算符/cast 重写为
/// Cs2jUnsigned.UnsignedMath 静态调用。clean 文件字节级保真;改写文件只对
/// 被改写的片段做局部格式化,并在输出前做语法校验——任何情况下都不产出
/// 语法损坏的中间 C#。
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

        if (!UnsignedTypeSyntaxRewriter.IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new UnsignedTypeRewriterResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new UnsignedTypeSyntaxRewriter(semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        var postfixRewriter = new UnsignedPostfixRewriter(stats);
        newRoot = (CompilationUnitSyntax)postfixRewriter.Visit(newRoot)!;

        var outputCode = FormatAndValidate(newRoot, diagnostics, stats);
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
    /// 优先只格式化被改写的片段(整文件重排会放大 diff);若局部格式化结果
    /// 解析不通过,退回整文件格式化、再退回未格式化文本。所有候选都要通过
    /// 重新解析校验,返回 null 表示全部失败。
    /// </summary>
    private static string? FormatAndValidate(
        CompilationUnitSyntax root,
        List<UnsignedTypeRewriterDiagnostic> diagnostics,
        UnsignedTypeRewriterStatistics stats)
    {
        using var workspace = new AdhocWorkspace();

        var candidates = new List<string>();
        try
        {
            var spans = GetRewrittenSpans(root);
            if (spans.Count > 0)
            {
                var spanFormatted = Formatter.Format(root, spans, workspace);
                candidates.Add(spanFormatted.ToFullString());
            }
        }
        catch
        {
            // 局部格式化失败不致命,继续尝试整文件格式化。
        }

        try
        {
            candidates.Add(Formatter.Format(root, workspace).ToFullString());
        }
        catch
        {
            // 忽略:仍可用未格式化文本。
        }

        candidates.Add(root.ToFullString());

        foreach (var candidate in candidates)
        {
            var reparsed = CSharpSyntaxTree.ParseText(candidate);
            if (!reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 取被标记节点的最外层 Span(嵌套标记只保留最外层,保证 span 两两不重叠)。
    /// </summary>
    internal static List<Microsoft.CodeAnalysis.Text.TextSpan> GetRewrittenSpans(SyntaxNode root)
    {
        var annotated = root
            .GetAnnotatedNodes(UnsignedRewriteAnnotations.Kind)
            .Where(n => n is ExpressionSyntax or StatementSyntax)
            .ToList();
        if (annotated.Count == 0)
        {
            return new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        }

        var annotatedSet = new HashSet<SyntaxNode>(annotated);
        return annotated
            .Where(n => !n.Ancestors().Any(annotatedSet.Contains))
            .Select(n => n.Span)
            .Distinct()
            .OrderBy(s => s.Start)
            .ToList();
    }
}
