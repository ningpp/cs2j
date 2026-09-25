using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.ArrayMethodRewriter;

/// <summary>
/// 数组方法预处理器公共入口。把 C# 中所有"纯数组相关、且 Java 没有对应
/// 语法/语义/用法"的构造改写为 Cs2jArray.ArrayHelper.XXX(...) 静态调用。
/// clean 文件字节级保真；改写文件只对被改写的片段做局部格式化，并在输出前
/// 做语法校验——任何情况下都不产出语法损坏的中间 C#。
/// </summary>
public sealed class ArrayMethodRewriter
{
    public ArrayMethodRewriterResult Rewrite(
        string sourceCode,
        SemanticModel? semanticModel = null,
        ArrayMethodRewriterOptions? options = null)
    {
        options ??= new ArrayMethodRewriterOptions();
        var stats = new ArrayMethodRewriterStatistics { FilesScanned = 1 };
        var diagnostics = new List<ArrayMethodRewriterDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!ArrayMethodSyntaxRewriter.IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new ArrayMethodRewriterResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new ArrayMethodSyntaxRewriter(semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        // 检查是否实际产生了改写（统计计数器是否非零）
        var actuallyChanged = stats.InstanceMemberRewrites > 0
            || stats.StaticArrayCalls > 0
            || stats.ResizeStatements > 0
            || stats.ForeachFlattened > 0
            || stats.RankInlined > 0
            || stats.MultiDimLengthRewrites > 0;

        if (!actuallyChanged)
        {
            stats.FilesSkippedClean = 1;
            return new ArrayMethodRewriterResult(sourceCode, false, diagnostics, stats);
        }

        var outputCode = FormatAndValidate(newRoot, diagnostics, stats);
        if (outputCode == null)
        {
            stats.FilesTransformed = 0;
            stats.FilesSkippedClean = 1;
            diagnostics.Add(new ArrayMethodRewriterDiagnostic(
                ArrayMethodSeverity.Error,
                "array rewrite produced invalid C# syntax; the original file was preserved."));
            return new ArrayMethodRewriterResult(sourceCode, false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new ArrayMethodRewriterResult(outputCode, true, diagnostics, stats);
    }

    private static string? FormatAndValidate(
        CompilationUnitSyntax root,
        List<ArrayMethodRewriterDiagnostic> diagnostics,
        ArrayMethodRewriterStatistics stats)
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
            // 局部格式化失败不致命，继续尝试整文件格式化。
        }

        try
        {
            candidates.Add(Formatter.Format(root, workspace).ToFullString());
        }
        catch
        {
            // 忽略：仍可用未格式化文本。
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

    internal static List<Microsoft.CodeAnalysis.Text.TextSpan> GetRewrittenSpans(SyntaxNode root)
    {
        var annotated = root
            .GetAnnotatedNodes(ArrayRewriteAnnotations.Kind)
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
