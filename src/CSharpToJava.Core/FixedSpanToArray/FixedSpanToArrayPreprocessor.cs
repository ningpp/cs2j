using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.FixedSpanToArray;

/// <summary>
/// Independent ABI pass <c>fixed-span-to-array</c> (stage Abi). It turns a
/// span-backed <c>fixed</c> statement into a managed-array <c>fixed</c> so the
/// later <c>unsafe-ffm</c> R3 array path can lower it:
/// <list type="bullet">
/// <item><c>fixed (T* p = span)</c>
/// (span is <c>ReadOnlySpan&lt;T&gt;</c>/<c>Span&lt;T&gt;</c>) →</item>
/// <item><c>T[] __cs2j_fixed_N = span.ToArray(); fixed (T* p = __cs2j_fixed_N)</c>
/// for the read-only shape;</item>
/// </list>
/// Whether writable <c>Span&lt;T&gt;</c> targets are materialized is decided by the
/// host chain, not here: it is only needed when <c>unsafe-ffm</c> runs in the
/// same chain (the Java ABI path); the default keeps them verbatim. Writable
/// spans are preserved by default (FSA001) and only lowered when
/// <see cref="FixedSpanToArrayOptions.LowerWritableSpans"/> is enabled,
/// because the write-back copies the whole span back on every execution
/// (<c>CopyTo</c> over the caller's buffer), turning an O(bytes written) access into
/// O(caller buffer) — a complexity regression for large buffers. Opting in produces:
/// <code>
/// var __cs2j_span_N = span;
/// T[] __cs2j_fixed_M = __cs2j_span_N.ToArray();
/// try { fixed (T* p = __cs2j_fixed_M) { body } }
/// finally { global::System.MemoryExtensions.AsSpan(__cs2j_fixed_M).CopyTo(__cs2j_span_N); }
/// </code>
/// <c>&amp;MemoryMarshal.GetReference(span)</c> initializers are unwrapped to the
/// span argument first. Arrays, strings, stackalloc, pointers and any other
/// fixed source are preserved for <c>unsafe-ffm</c>; span shapes that cannot be
/// proven safe (multiple variables, <c>await</c>/<c>yield</c> in the body, or a
/// writable span observed directly inside the body) stay verbatim with FSA001.
/// Clean files are byte-for-byte identical and re-running the pass on its own
/// output is a no-op.
/// </summary>
public sealed class FixedSpanToArrayPreprocessor
{
    private const string MemoryMarshalTypeName = "System.Runtime.InteropServices.MemoryMarshal";
    private const string FixedNamePrefix = "__cs2j_fixed_";
    private const string SpanNamePrefix = "__cs2j_span_";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private readonly FixedSpanToArrayOptions _options;

    public FixedSpanToArrayPreprocessor(FixedSpanToArrayOptions? options = null)
    {
        _options = options ?? new FixedSpanToArrayOptions();
    }

    /// <summary>Rewrite one file using the caller's whole-tree semantic model.</summary>
    public FixedSpanToArrayResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new FixedSpanToArrayStatistics();
        var diagnostics = new List<FixedSpanToArrayDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // cs2j-generated support/registry artifacts are not user fixed surface.
        if (IsCs2jGeneratedSupportFile(filePath))
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var reservedNames = CollectIdentifierNames(root);
        var rewriter = new SpanFixedRewriter(
            _options, semanticModel, reservedNames, statistics, diagnostics, filePath);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (statistics.SpanFixedStatementsLowered == 0)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var outputCode = newRoot.ToFullString();
        if (!ParsesWithoutErrors(outputCode))
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new FixedSpanToArrayDiagnostic(
                FixedSpanToArraySeverity.Warning,
                FixedSpanToArrayDiagnosticCode.FSA010,
                "fixed-span-to-array rewrite failed local parse validation; the original " +
                "file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new FixedSpanToArrayResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    // ---- rewrite engine ----

    private sealed class SpanFixedRewriter : CSharpSyntaxRewriter
    {
        private readonly FixedSpanToArrayOptions _options;
        private readonly SemanticModel _model;
        private readonly HashSet<string> _reservedNames;
        private readonly FixedSpanToArrayStatistics _statistics;
        private readonly List<FixedSpanToArrayDiagnostic> _diagnostics;
        private readonly string _filePath;
        private int _nextFixedIndex;
        private int _nextSpanIndex;

        public SpanFixedRewriter(
            FixedSpanToArrayOptions options,
            SemanticModel model,
            HashSet<string> reservedNames,
            FixedSpanToArrayStatistics statistics,
            List<FixedSpanToArrayDiagnostic> diagnostics,
            string filePath)
        {
            _options = options;
            _model = model;
            _reservedNames = reservedNames;
            _statistics = statistics;
            _diagnostics = diagnostics;
            _filePath = filePath;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var statements = ExpandStatements(node.Statements);
            return node.WithStatements(List(statements));
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var statements = ExpandStatements(node.Statements);
            return node.WithStatements(List(statements));
        }

        public override SyntaxNode? VisitFixedStatement(FixedStatementSyntax node)
        {
            // Embedded statement position (e.g. `if (c) fixed (...)` without
            // braces): the declarations / try-finally need a block wrapper.
            if (!TryLower(node, out var replacement))
            {
                return base.VisitFixedStatement(node);
            }

            var statements = replacement
                .Select(statement => statement
                    .WithLeadingTrivia(ElasticCarriageReturnLineFeed))
                .ToList();
            var block = Block(List(statements))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithCloseBraceToken(
                    Token(SyntaxKind.CloseBraceToken)
                        .WithLeadingTrivia(ElasticCarriageReturnLineFeed));
            return block;
        }

        private List<StatementSyntax> ExpandStatements(SyntaxList<StatementSyntax> statements)
        {
            var expanded = new List<StatementSyntax>(statements.Count);
            foreach (var statement in statements)
            {
                if (statement is FixedStatementSyntax fixedStatement)
                {
                    if (TryLower(fixedStatement, out var replacement))
                    {
                        var leading = fixedStatement.GetLeadingTrivia();
                        var indentation = GetIndentation(leading);

                        expanded.Add(replacement[0]
                            .WithLeadingTrivia(leading)
                            .WithTrailingTrivia(ElasticCarriageReturnLineFeed));
                        for (var i = 1; i < replacement.Count; i++)
                        {
                            expanded.Add(replacement[i].WithLeadingTrivia(indentation));
                        }

                        continue;
                    }

                    // Already analyzed (preserved or not owned): visiting the
                    // fixed statement must not re-run the analysis/diagnostics.
                    expanded.Add((StatementSyntax)base.VisitFixedStatement(fixedStatement)!);
                    continue;
                }

                expanded.Add((StatementSyntax)Visit(statement)!);
            }

            return expanded;
        }

        /// <summary>
        /// Build the replacement statement list for a span-backed fixed statement,
        /// or return false for every fixed source the pass does not own.
        /// </summary>
        private bool TryLower(
            FixedStatementSyntax node,
            out List<StatementSyntax> replacement)
        {
            replacement = new List<StatementSyntax>();

            if (node.Declaration.Type is not PointerTypeSyntax pointerType)
            {
                if (HasSpanLikeInitializer(node))
                    Reject(node, "the fixed declaration type is not a pointer type.");
                return false;
            }

            if (!HasSpanLikeInitializer(node))
                return false;

            if (node.Declaration.Variables.Count != 1)
            {
                Reject(node,
                    "the fixed statement declares multiple pointer variables; a span-backed " +
                    "initializer cannot be lowered as a single array.");
                return false;
            }

            var declarator = node.Declaration.Variables[0];
            var initializer = declarator.Initializer?.Value;
            if (initializer is null)
                return false;

            var unwrapped = UnwrapParenthesized(initializer);
            if (IsStackAllocExpression(unwrapped))
                return false;

            ExpressionSyntax spanExpression;
            var isGetReference = false;
            if (TryExtractGetReference(unwrapped, _model, out var referenceArgument))
            {
                isGetReference = true;
                spanExpression = referenceArgument;
            }
            else
            {
                spanExpression = unwrapped;
            }

            var spanType = _model.GetTypeInfo(spanExpression).Type;
            if (!IsSpanLike(spanType, out var isReadOnly, out var spanElement) || spanElement is null)
            {
                if (isGetReference)
                {
                    Reject(node,
                        "MemoryMarshal.GetReference is not applied to a Span/ReadOnlySpan " +
                        "expression.");
                    return false;
                }

                // Not a span source (array/string/stackalloc/pointer): unsafe-ffm owns it.
                return false;
            }

            var pointerElement = GetPointerElementType(node, declarator);
            if (pointerElement is null
                || !SymbolEqualityComparer.Default.Equals(pointerElement, spanElement))
            {
                Reject(node,
                    "the fixed pointer element type does not match the span element type.");
                return false;
            }

            if (isReadOnly && !_options.LowerReadOnlySpans)
            {
                Reject(node, "ReadOnlySpan fixed lowering is disabled by options.");
                return false;
            }

            if (!isReadOnly && !_options.LowerWritableSpans)
            {
                Reject(node,
                    "writable Span fixed statements are preserved by default: " +
                    "materializing the whole span and copying it back on every " +
                    "execution changes the caller buffer's cost; enable " +
                    "LowerWritableSpans to opt in.");
                return false;
            }

            var body = node.Statement;
            if (body.DescendantNodesAndSelf()
                .Any(n => n is AwaitExpressionSyntax or YieldStatementSyntax))
            {
                Reject(node,
                    "the fixed body contains await/yield, which cannot be safely moved into " +
                    "a try/finally write-back.");
                return false;
            }

            if (!isReadOnly && !IsWritableBodySafe(body, spanExpression, _model))
            {
                Reject(node,
                    "the writable span is observed directly inside the fixed body; array " +
                    "materialization plus a final write-back would change read-after-write " +
                    "semantics.");
                return false;
            }

            var elementType = pointerType.ElementType;
            var arrayName = AllocateName(FixedNamePrefix, ref _nextFixedIndex);

            var visitedBody = (StatementSyntax)Visit(body)!;

            if (isReadOnly)
            {
                var arrayDeclarator = BuildArrayDeclaration(
                    elementType,
                    arrayName,
                    ToArrayInvocation(spanExpression));
                replacement.Add(arrayDeclarator);
                replacement.Add(BuildLoweredFixed(node, visitedBody, arrayName));
            }
            else
            {
                var spanName = AllocateName(SpanNamePrefix, ref _nextSpanIndex);
                replacement.Add(BuildVarDeclaration(spanName, spanExpression));
                replacement.Add(BuildArrayDeclaration(
                    elementType,
                    arrayName,
                    ToArrayInvocation(IdentifierName(spanName))));
                replacement.Add(BuildTryFinally(
                    BuildLoweredFixed(node, visitedBody, arrayName),
                    arrayName,
                    spanName));
            }

            _statistics.SpanFixedStatementsLowered++;
            return true;
        }

        /// <summary>
        /// True when any declarator initializer binds to a Span/ReadOnlySpan value
        /// (directly or through <c>&amp;MemoryMarshal.GetReference(span)</c>).
        /// </summary>
        private bool HasSpanLikeInitializer(FixedStatementSyntax node)
        {
            foreach (var declarator in node.Declaration.Variables)
            {
                if (declarator.Initializer?.Value is not { } initializer)
                    continue;

                var unwrapped = UnwrapParenthesized(initializer);
                if (IsStackAllocExpression(unwrapped))
                    continue;

                if (TryExtractGetReference(unwrapped, _model, out var referenceArgument))
                {
                    if (IsSpanLike(_model.GetTypeInfo(referenceArgument).Type, out _, out _))
                        return true;
                    continue;
                }

                if (IsSpanLike(_model.GetTypeInfo(unwrapped).Type, out _, out _))
                    return true;
            }

            return false;
        }

        private ITypeSymbol? GetPointerElementType(
            FixedStatementSyntax node,
            VariableDeclaratorSyntax declarator)
        {
            if (_model.GetTypeInfo(node.Declaration.Type).Type is IPointerTypeSymbol declared)
                return declared.PointedAtType;

            if (declarator.Initializer?.Value is { } initializer
                && _model.GetTypeInfo(initializer).Type is IPointerTypeSymbol inferred)
            {
                return inferred.PointedAtType;
            }

            return null;
        }

        private string AllocateName(string prefix, ref int nextIndex)
        {
            while (true)
            {
                var name = prefix + nextIndex++;
                if (_reservedNames.Add(name))
                    return name;
            }
        }

        private void Reject(FixedStatementSyntax node, string reason)
        {
            _statistics.SpanFixedStatementsPreserved++;

            var line = 1;
            var column = 1;
            try
            {
                var location = node.GetLocation().GetLineSpan();
                line = location.StartLinePosition.Line + 1;
                column = location.StartLinePosition.Character + 1;
            }
            catch
            {
                // Location unavailable: fall back to (1,1).
            }

            _diagnostics.Add(new FixedSpanToArrayDiagnostic(
                FixedSpanToArraySeverity.Warning,
                FixedSpanToArrayDiagnosticCode.FSA001,
                "span fixed statement preserved: " + reason,
                _filePath, line, column));
        }
    }

    // ---- shape helpers ----

    /// <summary>
    /// Matches <c>&amp;MemoryMarshal.GetReference(span)</c> and returns the span
    /// argument. Falls back to the syntactic name when the symbol does not bind.
    /// </summary>
    private static bool TryExtractGetReference(
        ExpressionSyntax expression,
        SemanticModel model,
        out ExpressionSyntax spanArgument)
    {
        spanArgument = null!;

        if (expression is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.AddressOfExpression
            } address)
        {
            return false;
        }

        var operand = UnwrapParenthesized(address.Operand);
        if (operand is not InvocationExpressionSyntax invocation
            || invocation.ArgumentList.Arguments.Count != 1)
        {
            return false;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText != "GetReference")
        {
            return false;
        }

        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        var isMemoryMarshal = method is not null
            ? method.ContainingType?.ToDisplayString() == MemoryMarshalTypeName
            : memberAccess.Expression.ToString()
                .Contains("MemoryMarshal", StringComparison.Ordinal);
        if (!isMemoryMarshal)
            return false;

        spanArgument = invocation.ArgumentList.Arguments[0].Expression;
        return true;
    }

    /// <summary>System.Span&lt;T&gt;/System.ReadOnlySpan&lt;T&gt; 判定。</summary>
    private static bool IsSpanLike(
        ITypeSymbol? type,
        out bool isReadOnly,
        out ITypeSymbol? element)
    {
        isReadOnly = false;
        element = null;

        if (type is INamedTypeSymbol named
            && named.IsRefLikeType
            && named.IsGenericType
            && named.TypeArguments.Length == 1)
        {
            var definition = named.OriginalDefinition.ToDisplayString();
            if (definition == "System.ReadOnlySpan<T>")
            {
                isReadOnly = true;
                element = named.TypeArguments[0];
                return true;
            }

            if (definition == "System.Span<T>")
            {
                element = named.TypeArguments[0];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The writable span may only be observed as <c>.Length</c>/<c>.IsEmpty</c>
    /// inside the body. Element access, slicing or escaping the span would let the
    /// body observe uncommitted writes and is preserved (FSA001).
    /// </summary>
    private static bool IsWritableBodySafe(
        StatementSyntax body,
        ExpressionSyntax spanExpression,
        SemanticModel model)
    {
        var simpleSymbol = GetSimpleSymbol(spanExpression, model);
        if (simpleSymbol is null)
            return true;

        foreach (var identifier in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(identifier).Symbol, simpleSymbol))
            {
                continue;
            }

            if (identifier.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Expression == identifier
                && memberAccess.Name.Identifier.ValueText is "Length" or "IsEmpty")
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static ISymbol? GetSimpleSymbol(
        ExpressionSyntax expression,
        SemanticModel model)
    {
        var unwrapped = UnwrapParenthesized(expression);
        return unwrapped switch
        {
            IdentifierNameSyntax identifier => model.GetSymbolInfo(identifier).Symbol,
            MemberAccessExpressionSyntax memberAccess
                when memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                     && memberAccess.Expression is ThisExpressionSyntax
                => model.GetSymbolInfo(memberAccess).Symbol,
            _ => null,
        };
    }

    // ---- construction ----

    private static FixedStatementSyntax BuildLoweredFixed(
        FixedStatementSyntax node,
        StatementSyntax body,
        string arrayName)
    {
        var declarator = node.Declaration.Variables[0]
            .WithInitializer(EqualsValueClause(
                Token(SyntaxKind.EqualsToken).WithTrailingTrivia(Space),
                IdentifierName(arrayName)));
        var declaration = node.Declaration
            .WithVariables(SingletonSeparatedList(declarator));
        return node.WithDeclaration(declaration).WithStatement(body);
    }

    private static LocalDeclarationStatementSyntax BuildArrayDeclaration(
        TypeSyntax elementType,
        string name,
        ExpressionSyntax initializer)
    {
        var arrayType = ArrayType(elementType.WithoutTrivia())
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier()))
            .WithTrailingTrivia(Space);
        return LocalDeclarationStatement(VariableDeclaration(
            arrayType,
            SingletonSeparatedList(VariableDeclarator(
                    Identifier(name).WithTrailingTrivia(Space))
                .WithInitializer(EqualsValueClause(
                    Token(SyntaxKind.EqualsToken).WithTrailingTrivia(Space),
                    initializer.WithoutTrivia())))));
    }

    private static LocalDeclarationStatementSyntax BuildVarDeclaration(
        string name,
        ExpressionSyntax initializer)
    {
        return LocalDeclarationStatement(VariableDeclaration(
            IdentifierName("var").WithTrailingTrivia(Space),
            SingletonSeparatedList(VariableDeclarator(
                    Identifier(name).WithTrailingTrivia(Space))
                .WithInitializer(EqualsValueClause(
                    Token(SyntaxKind.EqualsToken).WithTrailingTrivia(Space),
                    initializer.WithoutTrivia())))));
    }

    private static TryStatementSyntax BuildTryFinally(
        FixedStatementSyntax fixedStatement,
        string arrayName,
        string spanName)
    {
        var copyBack = ExpressionStatement(ParseExpression(
                $"global::System.MemoryExtensions.AsSpan({arrayName}).CopyTo({spanName})"))
            .WithLeadingTrivia(ElasticCarriageReturnLineFeed);

        var tryBlock = Block(
                fixedStatement.WithLeadingTrivia(ElasticCarriageReturnLineFeed))
            .WithCloseBraceToken(
                Token(SyntaxKind.CloseBraceToken)
                    .WithLeadingTrivia(ElasticCarriageReturnLineFeed));
        var finallyBlock = Block(copyBack)
            .WithCloseBraceToken(
                Token(SyntaxKind.CloseBraceToken)
                    .WithLeadingTrivia(ElasticCarriageReturnLineFeed));

        return TryStatement(
            tryBlock,
            default(SyntaxList<CatchClauseSyntax>),
            FinallyClause(finallyBlock));
    }

    private static InvocationExpressionSyntax ToArrayInvocation(
        ExpressionSyntax receiver)
    {
        var target = receiver.WithoutTrivia();
        var inner = target is IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax
            or InvocationExpressionSyntax
            ? target
            : ParenthesizedExpression(target);

        return InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                inner,
                IdentifierName("ToArray")));
    }

    /// <summary>
    /// <c>stackalloc T[n]</c> / <c>stackalloc[] {...}</c> expressions (including a
    /// <c>checked(...)</c> wrapper) are owned by unsafe-ffm R4 and must never be
    /// mistaken for a Span source.
    /// </summary>
    private static bool IsStackAllocExpression(ExpressionSyntax expression)
    {
        var current = UnwrapParenthesized(expression);
        while (current is CheckedExpressionSyntax checkedExpression)
        {
            current = UnwrapParenthesized(checkedExpression.Expression);
        }

        return current is StackAllocArrayCreationExpressionSyntax
            or ImplicitStackAllocArrayCreationExpressionSyntax;
    }

    private static ExpressionSyntax UnwrapParenthesized(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static SyntaxTriviaList GetIndentation(SyntaxTriviaList leading)
    {
        for (var i = leading.Count - 1; i >= 0; i--)
        {
            var trivia = leading[i];
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                return TriviaList(trivia);

            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                break;
        }

        return TriviaList();
    }

    private static HashSet<string> CollectIdentifierNames(SyntaxNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
                names.Add(token.ValueText);
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
            var reparsed = CSharpSyntaxTree.ParseText(candidate, ParseOptions);
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    private static FixedSpanToArrayResult Rollback(
        string sourceCode,
        FixedSpanToArrayStatistics statistics,
        IReadOnlyList<FixedSpanToArrayDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
}
