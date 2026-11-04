using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.PointerDerefIndex;

/// <summary>
/// Independent read-only post-pass <c>pointer-deref-index</c> (pass stage
/// Expression, runs after <c>unsafe-ffm</c>, <c>stackalloc-lowering</c> and
/// <c>ref-struct-lowering</c>). It scans a whole file with its
/// <see cref="SemanticModel"/> and lowers provable pointer indirections into
/// pointer element access, which is the form the Java converter already
/// supports (<c>MemorySegment.get</c>):
/// <list type="bullet">
/// <item><c>*p</c> → <c>p[0]</c>;</item>
/// <item><c>*(p + k)</c> / <c>*(k + p)</c> → <c>p[k]</c>;</item>
/// <item><c>*(p - k)</c> → <c>p[-(k)]</c>;</item>
/// <item><c>*(T*)expr</c> → <c>((T*)expr)[0]</c>; the same rule applies to
/// <c>*(castExpr + k)</c> → <c>(castExpr)[k]</c>.</item>
/// </list>
/// The replacement is an lvalue wherever the original dereference was one, so
/// assignments / compound assignments / <c>++</c>/<c>--</c> keep working.
/// Dereferences whose operand is an increment/decrement or an assignment are
/// preserved verbatim (PDI001), because rewriting would change evaluation or
/// lvalue semantics. Dereferences that cannot be proven to operate on a pointer
/// are preserved verbatim as well. Address-of (<c>&amp;expr</c>) is never touched.
/// Clean files are returned byte-for-byte and re-running the pass on its own
/// output is a no-op (idempotent).
/// </summary>
public sealed class PointerDerefIndexPreprocessor
{
    private readonly PointerDerefIndexOptions _options;

    public PointerDerefIndexPreprocessor(PointerDerefIndexOptions? options = null)
    {
        _options = options ?? new PointerDerefIndexOptions();
    }

    /// <summary>
    /// Rewrite one file using the caller's whole-tree semantic model.
    /// </summary>
    public PointerDerefIndexResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new PointerDerefIndexStatistics();
        var diagnostics = new List<PointerDerefIndexDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // cs2j-generated support/registry artifacts are not user pointer surface.
        if (IsCs2jGeneratedSupportFile(filePath) || !_options.LowerDereferences)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var rewriter = new DerefIndexRewriter(semanticModel, filePath, statistics, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (newRoot.IsEquivalentTo(root))
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var outputCode = newRoot.ToFullString();
        if (!ParsesWithoutErrors(outputCode))
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new PointerDerefIndexDiagnostic(
                PointerDerefIndexSeverity.Warning,
                PointerDerefIndexDiagnosticCode.PDI010,
                "pointer-deref-index rewrite failed local parse validation; the original " +
                "file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new PointerDerefIndexResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static bool IsCs2jGeneratedSupportFile(string? filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith("_Cs2j", StringComparison.Ordinal)
               && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static PointerDerefIndexResult Rollback(
        string sourceCode,
        PointerDerefIndexStatistics statistics,
        IReadOnlyList<PointerDerefIndexDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };

    private static bool ParsesWithoutErrors(string code)
    {
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(
                code, new CSharpParseOptions(LanguageVersion.Latest));
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Post-order rewriter: nested dereferences are indexed first, so
    /// <c>**pp</c> becomes <c>pp[0][0]</c> in one pass (idempotent).
    /// </summary>
    private sealed class DerefIndexRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly string _filePath;
        private readonly PointerDerefIndexStatistics _statistics;
        private readonly List<PointerDerefIndexDiagnostic> _diagnostics;

        public DerefIndexRewriter(
            SemanticModel model,
            string filePath,
            PointerDerefIndexStatistics statistics,
            List<PointerDerefIndexDiagnostic> diagnostics)
        {
            _model = model;
            _filePath = filePath;
            _statistics = statistics;
            _diagnostics = diagnostics;
        }

        public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            // Children first: an inner dereference in the operand is already
            // an element access when the outer one is classified.
            var visited = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;

            if (!node.IsKind(SyntaxKind.PointerIndirectionExpression))
            {
                return visited;
            }

            if (IsSideEffectReceiver(node.Operand))
            {
                Preserve(node, "the dereferenced operand is an increment/decrement or " +
                    "assignment; rewriting would change evaluation/lvalue semantics.");
                return visited;
            }

            var operandType = _model.GetTypeInfo(node.Operand).Type;
            if (operandType is not IPointerTypeSymbol)
            {
                Preserve(node, "the semantic model does not prove the dereferenced operand " +
                    "has a pointer type.");
                return visited;
            }

            ExpressionSyntax? receiver = null;
            ExpressionSyntax? index = null;

            var classified = Unwrap(node.Operand);
            var visitedInner = Unwrap(visited.Operand);

            if (classified is BinaryExpressionSyntax binary
                && visitedInner is BinaryExpressionSyntax visitedBinary)
            {
                var leftType = _model.GetTypeInfo(binary.Left).Type;
                var rightType = _model.GetTypeInfo(binary.Right).Type;
                var leftPointer = leftType is IPointerTypeSymbol;
                var rightPointer = rightType is IPointerTypeSymbol;

                if (binary.IsKind(SyntaxKind.AddExpression) && leftPointer && !rightPointer)
                {
                    receiver = visitedBinary.Left;
                    index = visitedBinary.Right;
                }
                else if (binary.IsKind(SyntaxKind.AddExpression) && rightPointer && !leftPointer)
                {
                    receiver = visitedBinary.Right;
                    index = visitedBinary.Left;
                }
                else if (binary.IsKind(SyntaxKind.SubtractExpression) && leftPointer && !rightPointer)
                {
                    receiver = visitedBinary.Left;
                    index = Negate(visitedBinary.Right);
                }
            }

            if (receiver == null)
            {
                receiver = visited.Operand;
                index = LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0));
            }

            if (IsSideEffectReceiver(receiver))
            {
                Preserve(node, "the dereferenced pointer expression has an " +
                    "increment/decrement or assignment receiver; rewriting would change " +
                    "evaluation/lvalue semantics.");
                return visited;
            }

            var elementAccess = ElementAccessExpression(MakeReceiver(receiver))
                .WithArgumentList(BracketedArgumentList(
                    SingletonSeparatedList(Argument(Trim(index)))))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());

            _statistics.PointerDerefsRewritten++;
            return elementAccess;
        }

        private static ExpressionSyntax Negate(ExpressionSyntax expression)
            => PrefixUnaryExpression(
                SyntaxKind.UnaryMinusExpression,
                ParenthesizedExpression(Trim(expression)));

        /// <summary>
        /// Parenthesize receivers whose text would otherwise bind the index to
        /// the wrong sub-expression (cast, binary, conditional, prefix operator).
        /// </summary>
        private static ExpressionSyntax MakeReceiver(ExpressionSyntax expression)
        {
            var trimmed = Trim(expression);
            return trimmed switch
            {
                IdentifierNameSyntax
                    or GenericNameSyntax
                    or QualifiedNameSyntax
                    or MemberAccessExpressionSyntax
                    or ElementAccessExpressionSyntax
                    or InvocationExpressionSyntax
                    or ParenthesizedExpressionSyntax
                    or ThisExpressionSyntax
                    or BaseExpressionSyntax
                    or PostfixUnaryExpressionSyntax => trimmed,
                _ => ParenthesizedExpression(trimmed),
            };
        }

        private static ExpressionSyntax Trim(ExpressionSyntax expression)
            => expression.WithoutLeadingTrivia().WithoutTrailingTrivia();

        /// <summary>Strip parenthesis wrappers (kept for trivia-free classification).</summary>
        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            var current = expression;
            while (current is ParenthesizedExpressionSyntax parenthesized)
            {
                current = parenthesized.Expression;
            }

            return current;
        }

        /// <summary>
        /// True for receiver shapes whose rewriting would change evaluation
        /// count or lvalue-ness: <c>p++</c>, <c>++p</c>, <c>p--</c>,
        /// <c>--p</c>, and assignments.
        /// </summary>
        private static bool IsSideEffectReceiver(ExpressionSyntax expression)
        {
            var inner = Unwrap(expression);
            return inner switch
            {
                PostfixUnaryExpressionSyntax post
                    when post.IsKind(SyntaxKind.PostIncrementExpression)
                         || post.IsKind(SyntaxKind.PostDecrementExpression) => true,
                PrefixUnaryExpressionSyntax pre
                    when pre.IsKind(SyntaxKind.PreIncrementExpression)
                         || pre.IsKind(SyntaxKind.PreDecrementExpression) => true,
                AssignmentExpressionSyntax => true,
                _ => false,
            };
        }

        private void Preserve(PrefixUnaryExpressionSyntax node, string reason)
        {
            _statistics.PointerDerefsPreserved++;

            var line = 1;
            var column = 1;
            try
            {
                var span = node.GetLocation().GetLineSpan();
                line = span.StartLinePosition.Line + 1;
                column = span.StartLinePosition.Character + 1;
            }
            catch
            {
                // Location unavailable: fall back to (1,1).
            }

            _diagnostics.Add(new PointerDerefIndexDiagnostic(
                PointerDerefIndexSeverity.Warning,
                PointerDerefIndexDiagnosticCode.PDI001,
                "pointer dereference preserved: " + reason,
                _filePath, line, column));
        }
    }
}
