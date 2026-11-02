using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.GotoEliminator;

/// <summary>
/// C# goto/label 预处理器公共入口。将含 goto/label 的 C# 源转换为等价的无 goto/label C#。
/// 流程：dirty 检测 → Pass1 去糖 goto case/default → Pass2 状态机 → Formatter.Format。
/// clean 文件字节级保真（直接返回原字符串）。
/// </summary>
public sealed class GotoEliminator
{
    public GotoEliminatorResult Eliminate(string sourceCode, GotoEliminatorOptions? options = null)
    {
        options ??= new GotoEliminatorOptions();
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        return Eliminate(root, options);
    }

    /// <summary>
    /// Node-level entry: identical to the text entry (a re-parse of the text
    /// produces an equivalent tree); lets an in-process caller chain this
    /// after another rewriting pass without paying the re-parse.
    /// </summary>
    internal GotoEliminatorResult Eliminate(
        CompilationUnitSyntax root, GotoEliminatorOptions? options = null)
    {
        options ??= new GotoEliminatorOptions();
        var stats = new GotoEliminatorStatistics();
        stats.FilesScanned = 1;

        bool dirty = IsDirty(root);
        if (!dirty)
        {
            stats.FilesSkippedClean = 1;
            return new GotoEliminatorResult(root.ToFullString(), Changed: false, Array.Empty<GotoEliminatorDiagnostic>(), stats);
        }

        // Pass 1: goto case/default 去糖为合成标签
        var desugared = GotoCaseDesugarer.Run(root);
        // Pass 2: 状态机
        var builder = new StateMachineBuilder();
        var rewritten = (CompilationUnitSyntax)builder.Visit(desugared)!;

        // 格式化以规整间距（SyntaxFactory 构造的节点缺省无 trivia）。
        // 状态机输出与 iterator lowering 的合成块同形（纯 SyntaxFactory 语句），
        // 复用其分块预格式化：合成块并行隔离格式化 + 末次全树补齐，
        // 输出与单遍全树 Format 逐字节一致（见 FormatSynthesized 注释）。
        using var workspace = new AdhocWorkspace();
        var formatted = CSharpToJava.Core.IteratorLowering.IteratorLoweringPreprocessor
            .FormatSynthesized(rewritten, workspace);

        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;
        stats.MethodsTransformed = builder.TransformedMethods;
        stats.MethodsSkipped = builder.SkippedMethods;
        stats.GotosEliminated = builder.GotosEliminated;
        return new GotoEliminatorResult(code, Changed: true, builder.Diagnostics, stats);
    }

    /// <summary>dirty 判定：语法树中存在任意 GotoStatementSyntax（含 goto case/default）或 LabeledStatementSyntax。</summary>
    private static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n => n is GotoStatementSyntax or LabeledStatementSyntax);
}
