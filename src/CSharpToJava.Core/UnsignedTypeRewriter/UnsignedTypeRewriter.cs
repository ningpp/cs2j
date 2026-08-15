using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// C# 无符号类型预处理器公共入口。将 byte/ushort/uint/ulong 关键字替换为
/// System.* 全名，并把无符号类型上的运算符/cast 重写为
/// Cs2jUnsigned.UnsignedMath 静态调用。clean 文件字节级保真。
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

        using var workspace = new AdhocWorkspace();
        newRoot = (CompilationUnitSyntax)Formatter.Format(newRoot, workspace);

        stats.FilesTransformed = 1;
        return new UnsignedTypeRewriterResult(newRoot.ToFullString(), true, diagnostics, stats);
    }
}
