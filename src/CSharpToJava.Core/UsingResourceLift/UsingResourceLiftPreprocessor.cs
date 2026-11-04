using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UsingResourceLift;

/// <summary>
/// Independent control-flow pass <c>using-resource-lift</c> (stage ControlFlow,
/// runs right after <c>unsafe-ref-lowering</c>). The Java statement gate rejects
/// any <c>using</c> resource expression whose conversion produces pending
/// post statements ("ref/out arguments producing read-backs in a using resource
/// expression"). This pass moves such resource expressions into an ordinary
/// local declaration immediately before the <c>using</c>, so the resource is a
/// plain identifier:
/// <list type="bullet">
/// <item><c>using (T x = expr) { body }</c> →
/// <c>T x = expr;</c> <c>using (x) { body }</c>;</item>
/// <item><c>using (expr) { body }</c> →
/// <c>var __cs2j_using_N = expr;</c> <c>using (__cs2j_using_N) { body }</c>.</item>
/// </list>
/// "Read-back" means any <c>ref</c>/<c>out</c> argument in the resource
/// expression, or a <c>ByRef&lt;T&gt;</c> factory call. Every other using
/// statement is preserved verbatim; <c>await using</c>, using declarations and
/// multiple-variable declarations stay untouched (USL001 for the multi-variable
/// shape when it carries a read-back). The lifted <c>using (x)</c> form keeps
/// the implicit try/finally Dispose semantics. Clean files are byte-for-byte
/// identical and re-running the pass on its own output is a no-op.
/// </summary>
public sealed class UsingResourceLiftPreprocessor
{
    private readonly UsingResourceLiftOptions _options;

    public UsingResourceLiftPreprocessor(UsingResourceLiftOptions? options = null)
    {
        _options = options ?? new UsingResourceLiftOptions();
    }

    /// <summary>Rewrite one file using the caller's whole-tree semantic model.</summary>
    public UsingResourceLiftResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new UsingResourceLiftStatistics();
        var diagnostics = new List<UsingResourceLiftDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // cs2j-generated support/registry artifacts are not user using surface.
        if (IsCs2jGeneratedSupportFile(filePath))
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var reservedNames = CollectIdentifierNames(root);
        var rewriter = new LiftRewriter(
            _options, reservedNames, statistics, diagnostics, filePath);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (statistics.UsingResourcesLifted == 0)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var outputCode = newRoot.ToFullString();
        if (!ParsesWithoutErrors(outputCode))
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new UsingResourceLiftDiagnostic(
                UsingResourceLiftSeverity.Warning,
                UsingResourceLiftDiagnosticCode.USL010,
                "using-resource-lift rewrite failed local parse validation; the original " +
                "file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new UsingResourceLiftResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    // ---- rewrite engine ----

    private sealed class LiftRewriter : CSharpSyntaxRewriter
    {
        private readonly UsingResourceLiftOptions _options;
        private readonly HashSet<string> _reservedNames;
        private readonly UsingResourceLiftStatistics _statistics;
        private readonly List<UsingResourceLiftDiagnostic> _diagnostics;
        private readonly string _filePath;
        private int _nextNameIndex;

        public LiftRewriter(
            UsingResourceLiftOptions options,
            HashSet<string> reservedNames,
            UsingResourceLiftStatistics statistics,
            List<UsingResourceLiftDiagnostic> diagnostics,
            string filePath)
        {
            _options = options;
            _reservedNames = reservedNames;
            _statistics = statistics;
            _diagnostics = diagnostics;
            _filePath = filePath;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var statements = ExpandStatements(node.Statements);
            return node.WithStatements(SyntaxFactory.List(statements));
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var statements = ExpandStatements(node.Statements);
            return node.WithStatements(SyntaxFactory.List(statements));
        }

        public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
        {
            if (!TryLift(node, out var first, out var second))
            {
                return base.VisitUsingStatement(node);
            }

            // Embedded statement position (e.g. `if (c) using (...)` without
            // braces): the two statements need a block wrapper.
            var leading = node.GetLeadingTrivia();
            var indentation = GetIndentation(leading);

            var visitedFirst = (StatementSyntax)Visit(first
                .WithLeadingTrivia(SyntaxFactory.TriviaList())
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed))!;
            var visitedSecond = (StatementSyntax)Visit(second
                .WithLeadingTrivia(indentation))!;

            return SyntaxFactory.Block(visitedFirst, visitedSecond)
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }

        private List<StatementSyntax> ExpandStatements(SyntaxList<StatementSyntax> statements)
        {
            var expanded = new List<StatementSyntax>(statements.Count);
            foreach (var statement in statements)
            {
                if (statement is UsingStatementSyntax usingStatement
                    && TryLift(usingStatement, out var first, out var second))
                {
                    var leading = usingStatement.GetLeadingTrivia();
                    var indentation = GetIndentation(leading);

                    expanded.Add((StatementSyntax)Visit(first
                        .WithLeadingTrivia(leading)
                        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed))!);
                    expanded.Add((StatementSyntax)Visit(second
                        .WithLeadingTrivia(indentation))!);
                    continue;
                }

                expanded.Add((StatementSyntax)Visit(statement)!);
            }

            return expanded;
        }

        private bool TryLift(
            UsingStatementSyntax node,
            out StatementSyntax first,
            out UsingStatementSyntax second)
        {
            first = null!;
            second = null!;

            // `await using` has different disposal semantics; leave it alone.
            if (node.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
            {
                return false;
            }

            if (node.Declaration is { } declaration)
            {
                if (!_options.LiftDeclaredResources)
                {
                    return false;
                }

                var hasReadBack = declaration.Variables.Any(variable =>
                    variable.Initializer is { } initializer
                    && ContainsReadBack(initializer.Value));
                if (!hasReadBack)
                {
                    return false;
                }

                if (declaration.Variables.Count != 1)
                {
                    Skip(
                        node,
                        "the using declaration has multiple variables; the ref/out " +
                        "read-back resource cannot be lifted into a single local declaration.");
                    return false;
                }

                var declarator = declaration.Variables[0];
                first = SyntaxFactory.LocalDeclarationStatement(declaration);
                second = node
                    .WithDeclaration(null)
                    .WithExpression(SyntaxFactory.IdentifierName(
                        declarator.Identifier.WithoutTrivia()));
                _statistics.UsingResourcesLifted++;
                return true;
            }

            if (node.Expression is { } expression)
            {
                if (!_options.LiftExpressionResources)
                {
                    return false;
                }

                if (!ContainsReadBack(expression))
                {
                    return false;
                }

                var name = AllocateName();
                var declarator = SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(name).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.Token(SyntaxKind.EqualsToken)
                            .WithTrailingTrivia(SyntaxFactory.Space),
                        expression.WithoutTrivia()));
                var variableDeclaration = SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var")
                        .WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.SingletonSeparatedList(declarator));

                first = SyntaxFactory.LocalDeclarationStatement(variableDeclaration);
                second = node.WithExpression(SyntaxFactory.IdentifierName(name));
                _statistics.UsingResourcesLifted++;
                return true;
            }

            return false;
        }

        private string AllocateName()
        {
            while (true)
            {
                var name = "__cs2j_using_" + _nextNameIndex++;
                if (_reservedNames.Add(name))
                {
                    return name;
                }
            }
        }

        private void Skip(UsingStatementSyntax node, string reason)
        {
            _statistics.UsingResourcesSkipped++;

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

            _diagnostics.Add(new UsingResourceLiftDiagnostic(
                UsingResourceLiftSeverity.Warning,
                UsingResourceLiftDiagnosticCode.USL001,
                "using resource preserved: " + reason,
                _filePath, line, column));
        }
    }

    // ---- shape helpers ----

    /// <summary>
    /// True when the resource expression contains any <c>ref</c>/<c>out</c>
    /// argument or a <c>ByRef&lt;T&gt;</c> factory call. Those are the shapes the
    /// Java statement gate reports as read-backs in a using resource.
    /// </summary>
    private static bool ContainsReadBack(ExpressionSyntax expression)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ArgumentSyntax argument
                    when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                         || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                    return true;

                case InvocationExpressionSyntax invocation
                    when IsByRefInvocation(invocation):
                    return true;
            }
        }

        return false;
    }

    private static bool IsByRefInvocation(InvocationExpressionSyntax invocation)
        => invocation.Expression
            .DescendantNodesAndSelf()
            .OfType<GenericNameSyntax>()
            .Any(generic => generic.Identifier.ValueText == "ByRef");

    private static SyntaxTriviaList GetIndentation(SyntaxTriviaList leading)
    {
        for (var i = leading.Count - 1; i >= 0; i--)
        {
            var trivia = leading[i];
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                return SyntaxFactory.TriviaList(trivia);
            }

            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                break;
            }
        }

        return SyntaxFactory.TriviaList();
    }

    private static HashSet<string> CollectIdentifierNames(SyntaxNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                names.Add(token.ValueText);
            }
        }

        return names;
    }

    private static bool IsCs2jGeneratedSupportFile(string? filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith("_Cs2j", StringComparison.Ordinal)
               && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ParsesWithoutErrors(string candidate)
    {
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(
                candidate, new CSharpParseOptions(LanguageVersion.Latest));
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    private static UsingResourceLiftResult Rollback(
        string sourceCode,
        UsingResourceLiftStatistics statistics,
        IReadOnlyList<UsingResourceLiftDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
}
