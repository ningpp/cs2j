using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericConstraint;

/// <summary>
/// Per-file CN-rule rewriter. It only touches shapes that survive the upstream
/// <c>generic</c> pass; CN-01/03/07 residuals are audited (counted/preserved),
/// never re-implemented.
/// </summary>
internal sealed class GenericConstraintRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly GenericConstraintOptions _options;
    private readonly GenericConstraintStatistics _statistics;
    private readonly IReadOnlySet<ITypeParameterSymbol> _dependentParameters;
    private readonly List<GenericConstraintDiagnostic> _diagnostics = new();

    public GenericConstraintRewriter(
        SemanticModel model,
        GenericConstraintOptions options,
        GenericConstraintStatistics statistics,
        IReadOnlySet<ITypeParameterSymbol> dependentParameters)
    {
        _model = model;
        _options = options;
        _statistics = statistics;
        _dependentParameters = dependentParameters;
    }

    public bool Changed { get; private set; }

    public IReadOnlyList<GenericConstraintDiagnostic> Diagnostics => _diagnostics;

    // ---------------------------------------------------------------------
    // CN-02 / CN-05: reference-typed or unconstrained parameters are preserved
    // (Java null semantics carry them). No diagnostics.
    // ---------------------------------------------------------------------

    public override SyntaxNode? VisitDefaultExpression(DefaultExpressionSyntax node)
    {
        var visited = base.VisitDefaultExpression(node);
        var parameter = ResolveTypeParameter(node.Type);
        if (parameter == null)
        {
            return visited;
        }

        // CN-01 residual (the generic pass already removed every rewritable
        // zero-value site): audit only, never rewrite here.
        if (HasValueSemantics(parameter))
        {
            _statistics.CountPreserved(GenericConstraintRule.CN01);
            return visited;
        }

        _statistics.CountPreserved(GenericConstraintRule.CN02);
        return visited;
    }

    public override SyntaxNode? VisitNullableType(NullableTypeSyntax node)
    {
        var visited = base.VisitNullableType(node);
        var parameter = ResolveTypeParameter(node.ElementType);
        if (parameter == null)
        {
            return visited;
        }

        if (!HasStructConstraint(parameter))
        {
            // CN-05: T? over a reference-typed / unconstrained parameter stays.
            _statistics.CountPreserved(GenericConstraintRule.CN05);
            return visited;
        }

        if (!_options.RewriteNullableValueType)
        {
            _statistics.CountPreserved(GenericConstraintRule.CN04);
            return visited;
        }

        if (IsInsideQuotedExpressionLambda(node))
        {
            _statistics.CountPreserved(GenericConstraintRule.CN04);
            Warn(GenericConstraintRule.CN04, node,
                $"T? for value-type-constrained '{parameter.Name}' sits inside an expression tree; " +
                "Nullable<T> explicitization would change the observed tree shape (CN-04).");
            return visited;
        }

        Changed = true;
        _statistics.CountRewritten(GenericConstraintRule.CN04);
        return SyntaxFactory.ParseTypeName($"global::System.Nullable<{parameter.Name}>")
            .WithTriviaFrom(node);
    }

    /// <summary>
    /// CN-04 cascade: <c>x == null</c> / <c>x != null</c> on a Nullable value-type
    /// parameter becomes an explicit <c>HasValue</c> test.
    /// </summary>
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var visited = base.VisitBinaryExpression(node);

        if (!node.IsKind(SyntaxKind.EqualsExpression) && !node.IsKind(SyntaxKind.NotEqualsExpression))
        {
            return visited;
        }

        if (!TryGetNullComparisonOperand(node, out var operand, out var equals))
        {
            return visited;
        }

        var parameter = ResolveNullableTypeParameter(operand);
        if (parameter == null)
        {
            return visited;
        }

        if (IsInsideQuotedExpressionLambda(node))
        {
            _statistics.CountPreserved(GenericConstraintRule.CN04);
            Warn(GenericConstraintRule.CN04, node,
                $"null comparison on Nullable<{parameter.Name}> sits inside an expression tree (CN-04).");
            return visited;
        }

        Changed = true;
        _statistics.CountRewritten(GenericConstraintRule.CN04);
        return BuildHasValueTest(operand, equals).WithTriviaFrom(node);
    }

    /// <summary>CN-04 cascade for <c>x is null</c> / <c>x is not null</c>.</summary>
    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
        var visited = base.VisitIsPatternExpression(node);

        if (!TryGetNullTest(node.Pattern, out var testsForNull))
        {
            return visited;
        }

        var operand = node.Expression;

        var parameter = ResolveNullableTypeParameter(operand);
        if (parameter == null)
        {
            return visited;
        }

        if (IsInsideQuotedExpressionLambda(node))
        {
            _statistics.CountPreserved(GenericConstraintRule.CN04);
            Warn(GenericConstraintRule.CN04, node,
                $"null pattern on Nullable<{parameter.Name}> sits inside an expression tree (CN-04).");
            return visited;
        }

        Changed = true;
        _statistics.CountRewritten(GenericConstraintRule.CN04);
        return BuildHasValueTest(operand, equals: testsForNull).WithTriviaFrom(node);
    }

    // ---------------------------------------------------------------------
    // CN-09 / CN-10: generic static BCL calls become their non-generic
    // overload plus an explicit typeof(T) token site.
    // ---------------------------------------------------------------------

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = base.VisitInvocationExpression(node);

        if (node.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName }
            || visited is not InvocationExpressionSyntax visitedInvocation)
        {
            return visited;
        }

        var method = ResolveMethod(node);
        var containingType = method?.ContainingType;

        if (containingType?.SpecialType == SpecialType.System_Enum)
        {
            return RewriteEnumCall(node, visitedInvocation, genericName, method!.Name);
        }

        if (containingType?.SpecialType == SpecialType.System_Delegate
            || IsSyntacticDelegateReceiver(node.Expression as MemberAccessExpressionSyntax))
        {
            return RewriteDelegateCall(node, visitedInvocation, genericName, method?.Name ?? genericName.Identifier.ValueText);
        }

        return visited;
    }

    /// <summary>
    /// CN-10 fallback: the generic <c>Delegate.Combine/Remove&lt;T&gt;</c> shape has
    /// no BCL counterpart on every TFM, so an unresolved generic call is still
    /// recognized syntactically when the receiver spells <c>Delegate</c>.
    /// </summary>
    private static bool IsSyntacticDelegateReceiver(MemberAccessExpressionSyntax? member)
    {
        if (member?.Name is not GenericNameSyntax { Identifier.ValueText: "Combine" or "Remove" })
        {
            return false;
        }

        var text = member.Expression.WithoutTrivia().ToString().Replace(" ", string.Empty);
        return text is "Delegate" or "System.Delegate" or "global::System.Delegate";
    }

    private SyntaxNode? RewriteEnumCall(
        InvocationExpressionSyntax original,
        InvocationExpressionSyntax visited,
        GenericNameSyntax genericName,
        string methodName)
    {
        if (methodName is not ("Parse" or "GetValues" or "GetNames" or "TryParse"))
        {
            return visited;
        }

        var parameter = ResolveSingleTypeArgument(genericName);
        if (parameter == null)
        {
            _statistics.CountPreserved(GenericConstraintRule.CN09);
            Warn(GenericConstraintRule.CN09, original,
                $"Enum.{methodName}<T> type argument is not a resolvable type parameter (CN-09).");
            return visited;
        }

        // TryParse needs an `out T` slot; the non-generic overload only yields
        // `out object`, so the safe rewrite is not shape-preserving here.
        if (methodName == "TryParse")
        {
            _statistics.CountPreserved(GenericConstraintRule.CN09);
            Warn(GenericConstraintRule.CN09, original,
                $"Enum.TryParse<{parameter.Name}> has no shape-preserving non-generic overload; " +
                "the call is preserved (CN-09).");
            return visited;
        }

        if (IsInsideQuotedExpressionLambda(original))
        {
            _statistics.CountPreserved(GenericConstraintRule.CN09);
            Warn(GenericConstraintRule.CN09, original,
                $"Enum.{methodName}<{parameter.Name}> sits inside an expression tree (CN-09).");
            return visited;
        }

        var typeOf = SyntaxFactory.TypeOfExpression(SyntaxFactory.IdentifierName(parameter.Name));

        if (methodName == "GetNames")
        {
            if (visited.ArgumentList.Arguments.Count != 0)
            {
                _statistics.CountPreserved(GenericConstraintRule.CN09);
                Warn(GenericConstraintRule.CN09, original,
                    $"Enum.GetNames<{parameter.Name}> carries unexpected arguments (CN-09).");
                return visited;
            }

            Changed = true;
            _statistics.CountRewritten(GenericConstraintRule.CN09);
            return BuildGlobalStaticCall("Enum", "GetNames", WithLeadingArgument(visited.ArgumentList, typeOf))
                .WithTriviaFrom(original);
        }

        if (methodName == "GetValues")
        {
            if (visited.ArgumentList.Arguments.Count != 0)
            {
                _statistics.CountPreserved(GenericConstraintRule.CN09);
                Warn(GenericConstraintRule.CN09, original,
                    $"Enum.GetValues<{parameter.Name}> carries unexpected arguments (CN-09).");
                return visited;
            }

            Changed = true;
            _statistics.CountRewritten(GenericConstraintRule.CN09);
            var arrayCall = BuildGlobalStaticCall(
                "Enum", "GetValues", WithLeadingArgument(visited.ArgumentList, typeOf));
            return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName($"{parameter.Name}[]"), arrayCall)
                .WithTriviaFrom(original);
        }

        Changed = true;
        _statistics.CountRewritten(GenericConstraintRule.CN09);
        var parseCall = BuildGlobalStaticCall(
            "Enum", "Parse", WithLeadingArgument(visited.ArgumentList, typeOf));
        return SyntaxFactory.CastExpression(
                SyntaxFactory.IdentifierName(parameter.Name), parseCall)
            .WithTriviaFrom(original);
    }

    private SyntaxNode? RewriteDelegateCall(
        InvocationExpressionSyntax original,
        InvocationExpressionSyntax visited,
        GenericNameSyntax genericName,
        string methodName)
    {
        if (methodName is not ("Combine" or "Remove"))
        {
            return visited;
        }

        var parameter = ResolveSingleTypeArgument(genericName);
        if (parameter == null)
        {
            _statistics.CountPreserved(GenericConstraintRule.CN10);
            Warn(GenericConstraintRule.CN10, original,
                $"Delegate.{methodName}<T> type argument is not a resolvable type parameter (CN-10).");
            return visited;
        }

        if (IsInsideQuotedExpressionLambda(original))
        {
            _statistics.CountPreserved(GenericConstraintRule.CN10);
            Warn(GenericConstraintRule.CN10, original,
                $"Delegate.{methodName}<{parameter.Name}> sits inside an expression tree (CN-10).");
            return visited;
        }

        Changed = true;
        _statistics.CountRewritten(GenericConstraintRule.CN10);
        var call = BuildGlobalStaticCall("Delegate", methodName, visited.ArgumentList);
        return SyntaxFactory.CastExpression(
                SyntaxFactory.IdentifierName(parameter.Name), call)
            .WithTriviaFrom(original);
    }

    // ---------------------------------------------------------------------
    // CN-08: multi-dimensional / jagged type-parameter arrays stay verbatim
    // and are declared unsupported.
    // ---------------------------------------------------------------------

    public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
    {
        var visited = base.VisitArrayCreationExpression(node);

        if (!_options.ReportDeclaredUnsupported
            || !IsTypeParameterElement(node.Type.ElementType)
            || !IsMultiDimensionalOrJagged(node.Type))
        {
            return visited;
        }

        _statistics.CountUnsupported(GenericConstraintRule.CN08);
        Warn(GenericConstraintRule.CN08, node,
            "multi-dimensional / jagged type-parameter array is declared unsupported; " +
            "the expression is preserved verbatim (CN-08).",
            GenericConstraintCodes.DeclaredUnsupported);
        return visited;
    }

    // ---------------------------------------------------------------------
    // CN-06: value-semantic boxing stays an explicit `(object)t` cast and is
    // never reported (the Java half owns the boxing decision).
    // ---------------------------------------------------------------------

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var visited = base.VisitCastExpression(node);

        if (node.Type is not PredefinedTypeSyntax predefined
            || !predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword))
        {
            return visited;
        }

        var parameter = ResolveExpressionTypeParameter(node.Expression);
        if (parameter != null && HasValueSemantics(parameter))
        {
            _statistics.CountPreserved(GenericConstraintRule.CN06);
        }

        return visited;
    }

    // ---------------------------------------------------------------------
    // CN-11: `where T : Enum` is declared unsupported (the enum declaration
    // keeps its real enum shape); CN-12 warns for unexpressible constraints
    // with no dependent site anywhere in the tree.
    // ---------------------------------------------------------------------

    public override SyntaxNode? VisitTypeParameterConstraintClause(TypeParameterConstraintClauseSyntax node)
    {
        var visited = base.VisitTypeParameterConstraintClause(node);

        var hasEnumConstraint = false;
        foreach (var constraint in node.Constraints)
        {
            if (constraint is TypeConstraintSyntax typeConstraint && IsEnumConstraint(typeConstraint))
            {
                hasEnumConstraint = true;
                if (_options.ReportDeclaredUnsupported)
                {
                    _statistics.CountUnsupported(GenericConstraintRule.CN11);
                    Warn(GenericConstraintRule.CN11, constraint,
                        $"type parameter '{node.Name.Identifier.ValueText}' is constrained to System.Enum; " +
                        "the real enum shape is preserved (CN-11).",
                        GenericConstraintCodes.DeclaredUnsupported);
                }
            }
        }

        if (!_options.ReportUnexpressibleConstraints || hasEnumConstraint)
        {
            return visited;
        }

        var hasUnexpressible = node.Constraints.Any(constraint => IsUnexpressibleConstraint(constraint));
        if (!hasUnexpressible)
        {
            return visited;
        }

        var parameter = ResolveClauseTypeParameter(node);
        if (parameter != null && _dependentParameters.Contains(parameter))
        {
            // The dependent CN rule owns the site; no double reporting.
            return visited;
        }

        _statistics.CountPreserved(GenericConstraintRule.CN12);
        Warn(GenericConstraintRule.CN12, node,
            $"constraint clause for '{node.Name.Identifier.ValueText}' is not expressible in Java " +
            "and has no dependent site; the constraint text is preserved (CN-12).");
        return visited;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private ITypeParameterSymbol? ResolveTypeParameter(TypeSyntax? type)
    {
        if (type == null)
        {
            return null;
        }

        try
        {
            return _model.GetSymbolInfo(type).Symbol as ITypeParameterSymbol
                ?? _model.GetTypeInfo(type).Type as ITypeParameterSymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private ITypeParameterSymbol? ResolveExpressionTypeParameter(ExpressionSyntax expression)
    {
        try
        {
            return _model.GetTypeInfo(expression).Type as ITypeParameterSymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private ITypeParameterSymbol? ResolveNullableTypeParameter(ExpressionSyntax expression)
    {
        try
        {
            var type = _model.GetTypeInfo(expression).Type;

            // Roslyn may model `T?` (T : struct) either as Nullable<T> or as the
            // annotated type parameter itself; accept both spellings.
            if (type is ITypeParameterSymbol direct)
            {
                return HasValueSemantics(direct) ? direct : null;
            }

            if (type is not INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                } nullable
                || nullable.TypeArguments.Length != 1
                || nullable.TypeArguments[0] is not ITypeParameterSymbol parameter
                || !HasValueSemantics(parameter))
            {
                return null;
            }

            return parameter;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private ITypeParameterSymbol? ResolveSingleTypeArgument(GenericNameSyntax genericName)
    {
        if (genericName.TypeArgumentList.Arguments.Count != 1)
        {
            return null;
        }

        return ResolveTypeParameter(genericName.TypeArgumentList.Arguments[0]);
    }

    private IMethodSymbol? ResolveMethod(InvocationExpressionSyntax invocation)
    {
        try
        {
            return _model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private ITypeParameterSymbol? ResolveClauseTypeParameter(TypeParameterConstraintClauseSyntax clause)
    {
        foreach (var ancestor in clause.Ancestors())
        {
            TypeParameterListSyntax? list = ancestor switch
            {
                MethodDeclarationSyntax method => method.TypeParameterList,
                LocalFunctionStatementSyntax local => local.TypeParameterList,
                ClassDeclarationSyntax type => type.TypeParameterList,
                StructDeclarationSyntax type => type.TypeParameterList,
                InterfaceDeclarationSyntax type => type.TypeParameterList,
                RecordDeclarationSyntax type => type.TypeParameterList,
                DelegateDeclarationSyntax type => type.TypeParameterList,
                _ => null,
            };

            if (list == null)
            {
                continue;
            }

            var parameter = list.Parameters.FirstOrDefault(candidate =>
                candidate.Identifier.ValueText == clause.Name.Identifier.ValueText);
            if (parameter == null)
            {
                continue;
            }

            try
            {
                return _model.GetDeclaredSymbol(parameter) as ITypeParameterSymbol;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return null;
    }

    private bool HasValueSemantics(ITypeParameterSymbol parameter)
        => parameter.HasValueTypeConstraint
           || parameter.HasUnmanagedTypeConstraint
           || parameter.ConstraintTypes.Any(type =>
               type.SpecialType is SpecialType.System_Enum or SpecialType.System_ValueType)
           || parameter.ConstraintTypes.OfType<ITypeParameterSymbol>().Any(HasValueSemantics);

    private bool HasStructConstraint(ITypeParameterSymbol parameter)
        => parameter.HasValueTypeConstraint
           || parameter.HasUnmanagedTypeConstraint
           || parameter.ConstraintTypes.OfType<ITypeParameterSymbol>().Any(HasStructConstraint);

    private bool IsTypeParameterElement(TypeSyntax type)
        => ResolveTypeParameter(type) != null;

    private bool IsMultiDimensionalOrJagged(ArrayTypeSyntax array)
        => array.ElementType is ArrayTypeSyntax
           || array.RankSpecifiers.Count != 1
           || array.RankSpecifiers[0].Sizes.Count != 1;

    private bool IsEnumConstraint(TypeConstraintSyntax constraint)
    {
        try
        {
            if (_model.GetSymbolInfo(constraint.Type).Symbol is INamedTypeSymbol named
                && named.SpecialType == SpecialType.System_Enum)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        var text = constraint.Type.WithoutTrivia().ToString().Replace(" ", string.Empty);
        return text is "Enum" or "System.Enum" or "global::System.Enum";
    }

    private bool IsUnexpressibleConstraint(TypeParameterConstraintSyntax constraint)
    {
        switch (constraint)
        {
            case ClassOrStructConstraintSyntax classOrStruct:
                return classOrStruct.IsKind(SyntaxKind.StructConstraint);
            case ConstructorConstraintSyntax:
                return true;
            case TypeConstraintSyntax typeConstraint:
                var text = typeConstraint.Type.WithoutTrivia().ToString().Replace(" ", string.Empty);
                if (text is "unmanaged" or "notnull")
                {
                    return true;
                }

                try
                {
                    return _model.GetSymbolInfo(typeConstraint.Type).Symbol is ITypeParameterSymbol;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    private bool IsInsideQuotedExpressionLambda(SyntaxNode node)
        => CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(_model, node);

    private static bool TryGetNullComparisonOperand(
        BinaryExpressionSyntax binary,
        out ExpressionSyntax operand,
        out bool equals)
    {
        equals = binary.IsKind(SyntaxKind.EqualsExpression);

        if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression)
            && binary.Left is ExpressionSyntax left)
        {
            operand = left;
            return true;
        }

        if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression)
            && binary.Right is ExpressionSyntax right)
        {
            operand = right;
            return true;
        }

        operand = null!;
        return false;
    }

    private static bool TryGetNullTest(PatternSyntax pattern, out bool testsForNull)
    {
        testsForNull = true;
        if (pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal }
            && literal.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return true;
        }

        if (pattern is UnaryPatternSyntax unary
            && unary.OperatorToken.IsKind(SyntaxKind.NotKeyword))
        {
            testsForNull = false;
            return unary.Pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax inner }
                && inner.IsKind(SyntaxKind.NullLiteralExpression);
        }

        testsForNull = true;
        return false;
    }

    private static ExpressionSyntax BuildHasValueTest(ExpressionSyntax operand, bool equals)
    {
        // Keep a simple receiver unwrapped: downstream dominance analysis
        // (variance-audit VA020) matches `x.HasValue` by symbol, and a
        // parenthesized receiver loses that symbol.
        var receiver = NeedsParentheses(operand)
            ? SyntaxFactory.ParenthesizedExpression(operand.WithoutTrivia())
            : operand.WithoutTrivia();

        var hasValue = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver,
            SyntaxFactory.IdentifierName("HasValue"));

        return equals
            ? SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, hasValue)
            : hasValue;
    }

    private static bool NeedsParentheses(ExpressionSyntax expression)
        => expression is not (IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax
            or InvocationExpressionSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax
            or ParenthesizedExpressionSyntax
            or ObjectCreationExpressionSyntax);

    private static InvocationExpressionSyntax BuildGlobalStaticCall(
        string typeName,
        string methodName,
        ArgumentListSyntax arguments)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseName("global::System." + typeName),
                SyntaxFactory.IdentifierName(methodName)),
            arguments);

    private static ArgumentListSyntax WithLeadingArgument(
        ArgumentListSyntax arguments,
        ExpressionSyntax leading)
        => arguments.WithArguments(arguments.Arguments.Insert(0, SyntaxFactory.Argument(leading)));

    private void Warn(
        GenericConstraintRule rule,
        SyntaxNode node,
        string message,
        string code = GenericConstraintCodes.ConstraintNotExpressible)
    {
        var start = node.GetLocation().GetLineSpan().StartLinePosition;
        _diagnostics.Add(new GenericConstraintDiagnostic(
            GenericConstraintSeverity.Warning,
            rule,
            code,
            message,
            Line: start.Line + 1));
    }
}
