using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.PointerDiffInline;

/// <summary>
/// Independent expression pass <c>pointer-diff-inline</c> (stage Expression,
/// runs after <c>ref-out-in</c> and before <c>unsafe-ffm</c>). It finds
/// file-local C# helpers whose only purpose is "difference of two pointers of
/// the same element type, converted to an integer" and inlines them at their
/// call sites:
/// <list type="bullet">
/// <item><c>static int PtrDiff(byte* a, byte* b) =&gt; (int)(a - b)</c>;</item>
/// <item><c>static int PtrDiff(char* a, char* b)</c> whose body adds the usual
/// integral casts plus the <c>(Byte*)</c> reinterpret and <c>&gt;&gt;&gt; 1</c>
/// so the final value equals the <c>char*</c> element difference.</item>
/// </list>
/// An eligible call <c>PtrDiff(x, y)</c> becomes <c>(int)(x - y)</c> (the cast
/// follows the helper's return type), and the declaration is removed once no
/// reference (call site, method group, <c>nameof</c>, reflection string or
/// cross-file identifier) remains. Only private static source methods with two
/// same-element-type pointer parameters and an integral return type are
/// considered; every other two-pointer helper is preserved with PDI001. Clean
/// files are byte-for-byte identical and re-running the pass on its own output
/// is a no-op (idempotent).
/// </summary>
public sealed class PointerDiffInlinePreprocessor
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private readonly PointerDiffInlineOptions _options;

    public PointerDiffInlinePreprocessor(PointerDiffInlineOptions? options = null)
    {
        _options = options ?? new PointerDiffInlineOptions();
    }

    /// <summary>Rewrite one file using the caller's whole-tree semantic model.</summary>
    public PointerDiffInlineResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new PointerDiffInlineStatistics();
        var diagnostics = new List<PointerDiffInlineDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // cs2j-generated support/registry artifacts are not user helper surface.
        if (IsCs2jGeneratedSupportFile(filePath) || !_options.InlinePointerDifferences)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var inScopeCount = 0;
        var candidates = CollectCandidates(
            root, semanticModel, filePath, statistics, diagnostics, ref inScopeCount);

        if (candidates.Count == 0)
        {
            statistics.PointerDiffMethodsPreserved = inScopeCount;
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var deletable = new HashSet<MethodDeclarationSyntax>();
        foreach (var candidate in candidates)
        {
            // Only delete a helper once at least one call site in this file was
            // actually inlined and no other reference remains anywhere in the
            // compilation.
            if (HasInlinableCallSiteAndNoOtherReference(candidate, root, semanticModel))
                deletable.Add(candidate.Declaration);
        }

        var rewriter = new InlineRewriter(semanticModel, candidates, deletable, statistics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        statistics.PointerDiffCallSitesInlined = rewriter.CallSitesInlined;
        statistics.PointerDiffMethodsInlined = rewriter.MethodsRemoved;
        statistics.PointerDiffMethodsPreserved = inScopeCount - rewriter.MethodsRemoved;

        if (rewriter.CallSitesInlined == 0 && rewriter.MethodsRemoved == 0)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var outputCode = newRoot.ToFullString();
        if (!ParsesWithoutErrors(outputCode))
        {
            statistics.FilesSkippedClean++;
            statistics.PointerDiffCallSitesInlined = 0;
            statistics.PointerDiffMethodsInlined = 0;
            statistics.PointerDiffMethodsPreserved = inScopeCount;
            diagnostics.Add(new PointerDiffInlineDiagnostic(
                PointerDiffInlineSeverity.Warning,
                PointerDiffInlineDiagnosticCode.PDI010,
                "pointer-diff-inline rewrite failed local parse validation; the original " +
                "file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new PointerDiffInlineResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    // ---- candidate analysis ----

    private static List<Candidate> CollectCandidates(
        CompilationUnitSyntax root,
        SemanticModel model,
        string filePath,
        PointerDiffInlineStatistics statistics,
        List<PointerDiffInlineDiagnostic> diagnostics,
        ref int inScopeCount)
    {
        var candidates = new List<Candidate>();

        foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration) is not IMethodSymbol method)
                continue;

            if (method.Parameters.Length != 2)
                continue;
            if (method.Parameters[0].Type is not IPointerTypeSymbol leftPointer
                || method.Parameters[1].Type is not IPointerTypeSymbol rightPointer)
            {
                continue;
            }
            if (!IsIntegralType(method.ReturnType))
                continue;

            // Only methods whose body actually performs pointer arithmetic are
            // candidate helpers; a plain two-pointer user method (for example
            // `int M(byte* p, byte* q) => PtrDiff(p, q)`) is not touched or
            // diagnosed.
            var bodyExpression = TryGetSingleReturnExpression(declaration);
            if (bodyExpression is null || !ContainsPointerArithmetic(bodyExpression, model))
                continue;

            inScopeCount++;

            var reason = FindRejectionReason(
                method, leftPointer, rightPointer, declaration, bodyExpression, model);
            if (reason != null)
            {
                diagnostics.Add(new PointerDiffInlineDiagnostic(
                    PointerDiffInlineSeverity.Warning,
                    PointerDiffInlineDiagnosticCode.PDI001,
                    "pointer-arithmetic helper preserved: " + reason,
                    filePath,
                    declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    declaration.GetLocation().GetLineSpan().StartLinePosition.Character + 1));
                continue;
            }

            candidates.Add(new Candidate(
                declaration,
                method,
                method.ReturnType.ToDisplayString()));
        }

        return candidates;
    }

    private static string? FindRejectionReason(
        IMethodSymbol method,
        IPointerTypeSymbol leftPointer,
        IPointerTypeSymbol rightPointer,
        MethodDeclarationSyntax declaration,
        ExpressionSyntax bodyExpression,
        SemanticModel model)
    {
        if (!declaration.Modifiers.Any(SyntaxKind.PrivateKeyword)
            || !declaration.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            return "the helper is not declared private static.";
        }

        if (method.IsGenericMethod || method.TypeParameters.Length > 0)
            return "the helper declares type parameters.";

        if (!SymbolEqualityComparer.Default.Equals(
                leftPointer.PointedAtType, rightPointer.PointedAtType))
        {
            return "the two pointer parameters do not point to the same element type.";
        }

        if (method.Parameters.Any(parameter =>
                parameter.RefKind != RefKind.None
                || parameter.IsParams
                || parameter.IsOptional))
        {
            return "a pointer parameter is ref/out/in/params/optional.";
        }

        if (leftPointer.PointedAtType.TypeKind == TypeKind.Error
            || rightPointer.PointedAtType.TypeKind == TypeKind.Error)
        {
            return "the pointer element type could not be resolved.";
        }

        var scale = EvaluateDiffScale(
            bodyExpression, method.Parameters[0], method.Parameters[1], model);
        if (scale is null || scale.Value.Numerator != scale.Value.Denominator)
        {
            return "the body is not a provable same-type pointer difference " +
                "(constant offsets, extra operands or a cross-type reinterpret that " +
                "does not cancel out are preserved).";
        }

        return null;
    }

    /// <summary>
    /// The method body must be exactly one <c>return expr;</c> (or an expression
    /// body). Any other statement, or a return of a different shape, is preserved.
    /// </summary>
    private static ExpressionSyntax? TryGetSingleReturnExpression(MethodDeclarationSyntax declaration)
    {
        if (declaration.ExpressionBody is { } expressionBody)
            return expressionBody.Expression;

        if (declaration.Body is not { } body || body.Statements.Count != 1)
            return null;

        return body.Statements[0] is ReturnStatementSyntax { Expression: { } expression }
            ? expression
            : null;
    }

    // ---- body shape evaluation ----

    /// <summary>
    /// Returns the symbolic scale <c>num/den</c> of
    /// <c>(left - right) * num / den</c>, or null when the expression is not a
    /// pure pointer difference of the two parameters in the given order.
    /// </summary>
    private static (long Numerator, long Denominator)? EvaluateDiffScale(
        ExpressionSyntax expression,
        IParameterSymbol left,
        IParameterSymbol right,
        SemanticModel model)
    {
        expression = UnwrapParenthesized(expression);

        if (expression is CastExpressionSyntax cast)
        {
            var target = model.GetTypeInfo(cast.Type).Type;
            if (target is IPointerTypeSymbol || IsIntegralType(target))
                return EvaluateDiffScale(cast.Expression, left, right, model);
            return null;
        }

        if (expression is BinaryExpressionSyntax binary)
        {
            if (binary.IsKind(SyntaxKind.UnsignedRightShiftExpression))
            {
                var shift = GetLiteralShift(binary.Right);
                if (shift is null || shift.Value > 30)
                    return null;

                var inner = EvaluateDiffScale(binary.Left, left, right, model);
                if (inner is null)
                    return null;

                var (numerator, denominator) = inner.Value;
                return (numerator, denominator * (1L << shift.Value));
            }

            if (binary.IsKind(SyntaxKind.SubtractExpression))
            {
                var leftOperand = TryGetPointerOperand(binary.Left, model);
                var rightOperand = TryGetPointerOperand(binary.Right, model);
                if (leftOperand is null || rightOperand is null)
                    return null;

                if (!SymbolEqualityComparer.Default.Equals(leftOperand.Value.Parameter, left)
                    || !SymbolEqualityComparer.Default.Equals(rightOperand.Value.Parameter, right))
                {
                    return null;
                }

                if (!SymbolEqualityComparer.Default.Equals(
                        leftOperand.Value.PointedAtType, rightOperand.Value.PointedAtType))
                {
                    return null;
                }

                if (left.Type is not IPointerTypeSymbol parameterPointer)
                    return null;

                if (SymbolEqualityComparer.Default.Equals(
                        parameterPointer.PointedAtType, leftOperand.Value.PointedAtType))
                {
                    return (1, 1);
                }

                var parameterSize = TryGetSize(parameterPointer.PointedAtType);
                var subtractionSize = TryGetSize(leftOperand.Value.PointedAtType);
                if (parameterSize is null || subtractionSize is null)
                    return null;

                return (parameterSize.Value, subtractionSize.Value);
            }
        }

        return null;
    }

    /// <summary>
    /// True when the return expression contains an addition/subtraction whose
    /// operands are both pointer-typed. This is the narrow "pointer-arithmetic
    /// helper" scope used for diagnostics: ordinary methods that merely take two
    /// pointer parameters are left alone.
    /// </summary>
    private static bool ContainsPointerArithmetic(ExpressionSyntax expression, SemanticModel model)
    {
        foreach (var binary in expression.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>())
        {
            if (!binary.IsKind(SyntaxKind.AddExpression)
                && !binary.IsKind(SyntaxKind.SubtractExpression))
            {
                continue;
            }

            if (IsPointerTyped(binary.Left, model) && IsPointerTyped(binary.Right, model))
                return true;
        }

        return false;
    }

    private static bool IsPointerTyped(ExpressionSyntax expression, SemanticModel model)
        => model.GetTypeInfo(UnwrapParenthesized(expression)).Type is IPointerTypeSymbol;

    private static (IParameterSymbol? Parameter, ITypeSymbol PointedAtType)? TryGetPointerOperand(
        ExpressionSyntax expression,
        SemanticModel model)
    {
        expression = UnwrapParenthesized(expression);

        ITypeSymbol? type;
        ExpressionSyntax inner;
        if (expression is CastExpressionSyntax cast
            && model.GetTypeInfo(cast.Type).Type is IPointerTypeSymbol castPointer)
        {
            type = castPointer;
            inner = UnwrapParenthesized(cast.Expression);
        }
        else
        {
            type = model.GetTypeInfo(expression).Type;
            inner = expression;
        }

        if (type is not IPointerTypeSymbol pointer)
            return null;

        var parameter = inner is IdentifierNameSyntax identifier
            ? model.GetSymbolInfo(identifier).Symbol as IParameterSymbol
            : null;
        if (parameter is null)
            return null;

        return (parameter, pointer.PointedAtType);
    }

    private static int? GetLiteralShift(ExpressionSyntax expression)
    {
        expression = UnwrapParenthesized(expression);
        if (expression is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.NumericLiteralExpression))
        {
            return null;
        }

        return literal.Token.Value switch
        {
            int value when value >= 0 => value,
            uint value when value <= 30 => (int)value,
            _ => null,
        };
    }

    private static ExpressionSyntax UnwrapParenthesized(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression;
    }

    private static bool IsIntegralType(ITypeSymbol? type)
        => type?.SpecialType is
            SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64;

    private static long? TryGetSize(ITypeSymbol type)
        => type.SpecialType switch
        {
            SpecialType.System_SByte => 1,
            SpecialType.System_Byte => 1,
            SpecialType.System_Boolean => 1,
            SpecialType.System_Char => 2,
            SpecialType.System_Int16 => 2,
            SpecialType.System_UInt16 => 2,
            SpecialType.System_Int32 => 4,
            SpecialType.System_UInt32 => 4,
            SpecialType.System_Single => 4,
            SpecialType.System_Int64 => 8,
            SpecialType.System_UInt64 => 8,
            SpecialType.System_Double => 8,
            SpecialType.System_IntPtr => 8,
            SpecialType.System_UIntPtr => 8,
            _ => null,
        };

    // ---- reference analysis ----

    private static bool HasInlinableCallSiteAndNoOtherReference(
        Candidate candidate,
        CompilationUnitSyntax root,
        SemanticModel model)
    {
        var inlinableSites = 0;
        var hasRemainingReference = false;

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol resolved
                || !SymbolEqualityComparer.Default.Equals(resolved, candidate.Symbol))
            {
                continue;
            }

            if (IsInlinableInvocation(invocation, model))
                inlinableSites++;
            else
                hasRemainingReference = true;
        }

        if (inlinableSites == 0 || hasRemainingReference)
            return false;

        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != candidate.Symbol.Name)
                continue;

            // Method declaration names (the helper's own and any same-named
            // overload) are definitions, not references.
            if (identifier.Parent is MethodDeclarationSyntax declaration
                && declaration.Identifier == identifier.Identifier)
            {
                continue;
            }

            // An invocation name that this pass rewrites is not a remaining
            // reference; method groups / nameof() / delegate creations / other
            // ordinary uses are.
            if (IsNameOfInlinableInvocation(identifier, model))
                continue;

            hasRemainingReference = true;
            break;
        }

        if (hasRemainingReference)
            return false;

        if (ContainsNameReference(root, candidate.Symbol.Name))
            return false;

        return !HasReferenceInOtherTrees(candidate.Symbol, model);
    }

    private static bool IsNameOfInlinableInvocation(IdentifierNameSyntax identifier, SemanticModel model)
    {
        InvocationExpressionSyntax? invocation = null;
        if (identifier.Parent is InvocationExpressionSyntax direct
            && direct.Expression == identifier)
        {
            invocation = direct;
        }
        else if (identifier.Parent is MemberAccessExpressionSyntax memberAccess
                 && memberAccess.Name == identifier
                 && memberAccess.Parent is InvocationExpressionSyntax memberInvocation
                 && memberInvocation.Expression == memberAccess)
        {
            invocation = memberInvocation;
        }

        return invocation != null && IsInlinableInvocation(invocation, model);
    }

    private static bool IsInlinableInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel model)
    {
        if (invocation.ArgumentList.Arguments.Count != 2)
            return false;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            // Static helpers can only be reached through a type/namespace
            // receiver. Anything else would make dropping the receiver change
            // evaluation, so it is preserved.
            var receiver = model.GetSymbolInfo(memberAccess.Expression).Symbol;
            if (receiver is not (INamedTypeSymbol or INamespaceSymbol))
                return false;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.NameColon != null
                || !argument.RefKindKeyword.IsKind(SyntaxKind.None))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsNameReference(SyntaxNode root, string methodName)
    {
        foreach (var token in root.DescendantTokens())
        {
            if ((token.IsKind(SyntaxKind.StringLiteralToken)
                 || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
                && token.ValueText.Contains(methodName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Conservative cross-file guard: any same-name identifier or reflection
    /// string in another source tree keeps the declaration. A false positive only
    /// leaves a harmless private helper behind; a false negative would break the
    /// intermediate build.
    /// </summary>
    private static bool HasReferenceInOtherTrees(IMethodSymbol method, SemanticModel model)
    {
        foreach (var tree in model.Compilation.SyntaxTrees)
        {
            if (ReferenceEquals(tree, model.SyntaxTree))
                continue;

            foreach (var token in tree.GetRoot().DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken)
                    && token.ValueText == method.Name)
                {
                    return true;
                }

                if ((token.IsKind(SyntaxKind.StringLiteralToken)
                     || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
                    && token.ValueText.Contains(method.Name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---- rewrite ----

    private sealed record Candidate(
        MethodDeclarationSyntax Declaration,
        IMethodSymbol Symbol,
        string ReturnTypeText);

    private sealed class InlineRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly IReadOnlyList<Candidate> _candidates;
        private readonly HashSet<MethodDeclarationSyntax> _deletable;
        private readonly PointerDiffInlineStatistics _statistics;

        public InlineRewriter(
            SemanticModel model,
            IReadOnlyList<Candidate> candidates,
            HashSet<MethodDeclarationSyntax> deletable,
            PointerDiffInlineStatistics statistics)
        {
            _model = model;
            _candidates = candidates;
            _deletable = deletable;
            _statistics = statistics;
        }

        public int CallSitesInlined { get; private set; }
        public int MethodsRemoved { get; private set; }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (_deletable.Contains(node))
            {
                // The helper's body is a pure pointer difference, so it cannot
                // contain a call site that still needs visiting.
                MethodsRemoved++;
                return null;
            }

            return base.VisitMethodDeclaration(node);
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            // Inline nested invocations first, then classify the (original) node
            // with the semantic model.
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

            var candidate = FindCandidate(node);
            if (candidate is null || !IsInlinableInvocation(node, _model))
                return visited;

            // Arguments were already rewritten by base.Visit; use their visited
            // form but keep each expression evaluated exactly once.
            var arguments = visited.ArgumentList.Arguments;
            if (arguments.Count != 2)
                return visited;

            var left = MakeOperand(arguments[0].Expression);
            var right = MakeOperand(arguments[1].Expression);
            var difference = BinaryExpression(
                SyntaxKind.SubtractExpression,
                left,
                Token(SyntaxKind.MinusToken).WithLeadingTrivia(Space).WithTrailingTrivia(Space),
                right);

            ExpressionSyntax replacement = CastExpression(
                    ParseTypeName(candidate.ReturnTypeText),
                    ParenthesizedExpression(difference))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());

            // A cast expression binds tighter than a suffix operation:
            // `PtrDiff(a, b).Foo` must become `((int)(a - b)).Foo`.
            if (RequiresSuffixParentheses(node))
                replacement = ParenthesizedExpression(replacement);

            _statistics.PointerDiffCallSitesInlined++;
            CallSitesInlined++;
            return replacement;
        }

        private Candidate? FindCandidate(InvocationExpressionSyntax invocation)
        {
            var symbol = _model.GetSymbolInfo(invocation).Symbol;
            foreach (var candidate in _candidates)
            {
                if (symbol is IMethodSymbol method
                    && SymbolEqualityComparer.Default.Equals(method, candidate.Symbol))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool RequiresSuffixParentheses(InvocationExpressionSyntax node)
            => node.Parent switch
            {
                MemberAccessExpressionSyntax memberAccess when memberAccess.Expression == node => true,
                ElementAccessExpressionSyntax elementAccess when elementAccess.Expression == node => true,
                InvocationExpressionSyntax invocation when invocation.Expression == node => true,
                _ => false,
            };

        private static ExpressionSyntax MakeOperand(ExpressionSyntax expression)
        {
            var trimmed = expression.WithoutTrivia();
            return trimmed switch
            {
                IdentifierNameSyntax
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
    }

    // ---- helpers ----

    private static bool IsCs2jGeneratedSupportFile(string? filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith("_Cs2j", StringComparison.Ordinal)
               && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ParsesWithoutErrors(string code)
    {
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(code, ParseOptions);
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    private static PointerDiffInlineResult Rollback(
        string sourceCode,
        PointerDiffInlineStatistics statistics,
        IReadOnlyList<PointerDiffInlineDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
}
