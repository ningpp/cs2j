using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

/// <summary>
/// C#→C# 预处理器：按给定符号集求值并彻底移除条件编译指令
/// （#if/#elif/#else/#endif/#define/#undef，可选 #region/#endregion）。
///
/// 条件求值完全交给 Roslyn（C# spec §6.5 预处理阶段）：解析时非激活分支成为
/// DisabledTextTrivia，#define/#undef 只影响其后分支。本类型只做 trivia 过滤：
/// 删除禁用文本与指令 trivia（连同指令行行首缩进），token 序列保持不变。
/// 后置校验：输出重解析零错、零残留目标指令、token 流与输入一致；失败则原样透传。
/// 输入存在任何解析错误（含指令畸形，如未配对 #endif、#define 晚于首个 token、
/// 指令不在行首、非法条件表达式、激活的 #error）时：报告 Error 诊断并返回原文。
/// </summary>
public sealed class ConditionalDirectiveStripper
{
    public ConditionalDirectiveStripResult Strip(string sourceCode, ConditionalDirectiveStripOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        options ??= new ConditionalDirectiveStripOptions();
        var diagnostics = new List<ConditionalDirectiveDiagnostic>();
        var symbols = options.Symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(symbols);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions);
        var root = tree.GetCompilationUnitRoot();

        foreach (var error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(new(
                ConditionalDirectiveSeverity.Error,
                $"{error.Id}: {error.GetMessage()}",
                error.Location.GetLineSpan().StartLinePosition.Line + 1));
        }

        if (diagnostics.Count > 0)
        {
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        foreach (var warning in tree.GetDiagnostics().Where(d =>
                     d.Severity == DiagnosticSeverity.Warning && d.Id == "CS1030"))
        {
            diagnostics.Add(new(
                ConditionalDirectiveSeverity.Warning,
                $"#warning directive preserved: {warning.GetMessage()}",
                warning.Location.GetLineSpan().StartLinePosition.Line + 1));
        }

        if (!HasTargets(root, options.RemoveRegions))
        {
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        var rewriter = new RemovingRewriter(options.RemoveRegions);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var output = rewritten.ToFullString();

        if (!VerifyOutput(output, parseOptions, root, options.RemoveRegions))
        {
            diagnostics.Add(new(
                ConditionalDirectiveSeverity.Error,
                "internal post-verification failed (output would not parse cleanly or token stream changed); file left unchanged"));
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        var changed = !string.Equals(sourceCode, output, StringComparison.Ordinal);
        return new ConditionalDirectiveStripResult(
            output,
            changed,
            diagnostics,
            new()
            {
                DirectivesRemoved = rewriter.DirectivesRemoved,
                DisabledBranchesRemoved = rewriter.DisabledBranchesRemoved,
            });
    }

    /// <summary>
    /// 指令删除判定采用白名单反转：仅保留非条件类指令（region/line/pragma/nullable/
    /// error/warning/reference/load/shebang），其余（If/Elif/Else/EndIf/Define/Undef
    /// 及未来新增的未知指令种类）一律删除——满足“所有情况”的防御式要求。
    /// region 默认保留，RemoveRegions 开启时删除。
    /// </summary>
    internal static bool IsRemovedDirective(SyntaxKind kind, bool removeRegions = false) => kind switch
    {
        SyntaxKind.RegionDirectiveTrivia or SyntaxKind.EndRegionDirectiveTrivia => removeRegions,
        SyntaxKind.LineDirectiveTrivia
            or SyntaxKind.PragmaWarningDirectiveTrivia
            or SyntaxKind.PragmaChecksumDirectiveTrivia
            or SyntaxKind.NullableDirectiveTrivia
            or SyntaxKind.ErrorDirectiveTrivia
            or SyntaxKind.WarningDirectiveTrivia
            or SyntaxKind.ReferenceDirectiveTrivia
            or SyntaxKind.LoadDirectiveTrivia
            or SyntaxKind.ShebangDirectiveTrivia
            or SyntaxKind.BadDirectiveTrivia => false,
        _ => true,
    };

    internal static bool IsTargetTrivia(SyntaxTrivia trivia, bool removeRegions)
    {
        if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
        {
            return true;
        }

        if (trivia.GetStructure() is not DirectiveTriviaSyntax directive)
        {
            return false;
        }

        // 处于非激活条件分支内的 #region/#endregion：Roslyn 将其实例化为真实指令
        // trivia 而非禁用文本，需随所在分支一并删除（IsActive == false）。
        if (directive.Kind() is SyntaxKind.RegionDirectiveTrivia or SyntaxKind.EndRegionDirectiveTrivia)
        {
            return removeRegions || !directive.IsActive;
        }

        return IsRemovedDirective(directive.Kind(), removeRegions);
    }

    private static bool HasTargets(CompilationUnitSyntax root, bool removeRegions)
    {
        foreach (var token in root.DescendantTokens())
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                if (IsTargetTrivia(trivia, removeRegions))
                {
                    return true;
                }
            }

            foreach (var trivia in token.TrailingTrivia)
            {
                if (IsTargetTrivia(trivia, removeRegions))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool VerifyOutput(
        string output,
        CSharpParseOptions parseOptions,
        CompilationUnitSyntax originalRoot,
        bool removeRegions)
    {
        var verifyTree = CSharpSyntaxTree.ParseText(output, parseOptions);
        if (verifyTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return false;
        }

        if (HasTargets(verifyTree.GetCompilationUnitRoot(), removeRegions))
        {
            return false;
        }

        return TokenSequenceEqual(originalRoot, verifyTree.GetCompilationUnitRoot());
    }

    private static bool TokenSequenceEqual(CompilationUnitSyntax left, CompilationUnitSyntax right)
    {
        using var leftTokens = left.DescendantTokens().GetEnumerator();
        using var rightTokens = right.DescendantTokens().GetEnumerator();
        while (true)
        {
            var hasLeft = leftTokens.MoveNext();
            var hasRight = rightTokens.MoveNext();
            if (hasLeft != hasRight)
            {
                return false;
            }

            if (!hasLeft)
            {
                return true;
            }

            if (leftTokens.Current.RawKind != rightTokens.Current.RawKind ||
                leftTokens.Current.Text != rightTokens.Current.Text)
            {
                return false;
            }
        }
    }

    private sealed class RemovingRewriter(bool removeRegions) : CSharpSyntaxRewriter
    {
        internal int DirectivesRemoved;
        internal int DisabledBranchesRemoved;

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            var visited = base.VisitToken(token);
            var leading = Filter(visited.LeadingTrivia);
            var trailing = Filter(visited.TrailingTrivia);
            if (leading.Count == visited.LeadingTrivia.Count && trailing.Count == visited.TrailingTrivia.Count)
            {
                return visited;
            }

            return visited.WithLeadingTrivia(leading).WithTrailingTrivia(trailing);
        }

        private SyntaxTriviaList Filter(SyntaxTriviaList list)
        {
            if (list.Count == 0)
            {
                return list;
            }

            List<SyntaxTrivia>? result = null;
            for (var i = 0; i < list.Count; i++)
            {
                var trivia = list[i];
                if (!IsTargetTrivia(trivia, removeRegions))
                {
                    result?.Add(trivia);
                    continue;
                }

                result ??= new List<SyntaxTrivia>(list.Take(i));
                if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                {
                    DisabledBranchesRemoved++;
                }
                else
                {
                    DirectivesRemoved++;
                }

                // 合法文件中指令必为行首非空白，紧邻其前的纯水平空白即指令行缩进；
                // 一并删除避免残留纯空白行（指令 trivia 自带行尾 EOL，删之即整行消失）。
                while (result.Count > 0 && result[^1].IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    result.RemoveAt(result.Count - 1);
                }
            }

            return result == null ? list : SyntaxFactory.TriviaList(result);
        }
    }
}
