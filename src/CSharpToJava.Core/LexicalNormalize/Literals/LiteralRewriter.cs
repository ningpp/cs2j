using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// Single-Visit orchestrator (design D11): one tree walk applies the literal
/// rules in order LL-1/LL-6 (LL-2/LL-3 are appended in M2). Interpolated
/// strings are counted by LL-4 and left intact, but base Visit still descends
/// into their holes so independent literals there get rewritten.
/// </summary>
internal sealed class LiteralRewriter : CSharpSyntaxRewriter
{
    private readonly LexicalNormalizeOptions _options;
    private readonly LiteralRewriteRecorder _recorder = new();

    public LiteralRewriter(LexicalNormalizeOptions options)
    {
        _options = options;
    }

    public LiteralRewriteRecorder Recorder => _recorder;

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        ExpressionSyntax? replacement = null;

        if (_options.NormalizeRawLiterals)
        {
            replacement = RawStringLiteralRewriter.TryRewrite(node, _recorder);
        }

        if (replacement is null && _options.NormalizeEscEscapes)
        {
            replacement = EscEscapeLiteralRewriter.TryRewrite(node, _recorder);
        }

        if (replacement is null && _options.NormalizeUtf8Literals)
        {
            replacement = Utf8SuffixLiteralRewriter.TryRewrite(node, _recorder);
        }

        if (replacement is null && _options.NormalizeVerbatimLiterals)
        {
            replacement = VerbatimLiteralRewriter.TryRewrite(node, _recorder);
        }

        return (SyntaxNode?)replacement ?? node;
    }

    public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
    {
        var startKind = node.StringStartToken.Kind();
        if (LiteralSyntaxFacts.IsInterpolatedRaw(startKind))
        {
            _recorder.InterpolatedRaw++;
        }
        else if (LiteralSyntaxFacts.IsInterpolatedVerbatim(startKind))
        {
            _recorder.InterpolatedVerbatim++;
        }
        else
        {
            _recorder.InterpolatedRegular++;
        }

        // LL-4: the outer node and its text tokens stay untouched; descending
        // still normalizes independent literals used inside the holes.
        return base.VisitInterpolatedStringExpression(node);
    }
}
