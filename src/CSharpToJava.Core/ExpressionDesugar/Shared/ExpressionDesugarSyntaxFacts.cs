using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Shared syntax/semantic facts for the expression-desugar family.
/// </summary>
internal static class ExpressionDesugarSyntaxFacts
{
    /// <summary>Marks a trigger node intentionally accounted for (skip/diagnostic-only).</summary>
    public static readonly SyntaxAnnotation ExplainedAnnotation =
        new("ed-explained");

    /// <summary>Marks a subtree emitted by a rule (nested triggers inside are explained).</summary>
    public static readonly SyntaxAnnotation ExplainedSubtreeAnnotation =
        new("ed-explained-subtree");

    private const string ExpressionTreePrefix =
        "global::System.Linq.Expressions.Expression<";

    /// <summary>
    /// True when <paramref name="node"/> is syntactically inside a lambda that
    /// converts to <c>System.Linq.Expressions.Expression&lt;T&gt;</c>. The
    /// semantic criterion mirrors the repository-wide
    /// <c>Analysis.ExpressionTreeFacts</c> 口径 (converted-type prefix), applied
    /// to any node by walking its enclosing lambdas.
    /// </summary>
    public static bool IsInExpressionTree(SemanticModel? model, SyntaxNode node)
    {
        if (model == null)
            return false;

        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (ancestor is not LambdaExpressionSyntax lambda)
                continue;

            var convertedType = model.GetTypeInfo(lambda).ConvertedType;
            if (convertedType == null)
                continue;

            var fullName = convertedType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);
            if (fullName.StartsWith(ExpressionTreePrefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recovers the type a real per-project build would bind when the merged
    /// analysis compilation reports an ambiguous simple name (CS0104). The pass
    /// compilation unions every project's global usings, so a name such as
    /// <c>Dictionary</c> can bind to both the corpus' renamed source type
    /// (<c>dotnet.System.Collections.Generic.Dictionary</c>) and the BCL
    /// metadata type; a real build never sees both (projects importing the
    /// renamed namespace remove the implicit BCL one), so the single
    /// source-declared candidate is that build's binding. Anything else (no
    /// candidate, several source candidates) stays an error type so the caller
    /// still reports the site instead of guessing.
    /// </summary>
    public static ITypeSymbol? PreferSourceDeclaredCandidate(ITypeSymbol? type)
    {
        if (type is not IErrorTypeSymbol error)
            return type;

        ITypeSymbol? match = null;
        foreach (var candidate in error.CandidateSymbols)
        {
            if (candidate is not ITypeSymbol candidateType || !IsDeclaredInSource(candidateType))
                continue;

            if (match != null && !SymbolEqualityComparer.Default.Equals(match, candidateType))
                return type;

            match = candidateType;
        }

        return match == null ? type : MaterializeWithErrorTypeArguments(match, error);
    }

    /// <summary>
    /// Candidate symbols are reported as open definitions
    /// (<c>Dictionary&lt;TKey, TValue&gt;</c>), while the error type already
    /// carries the constructed arguments the site bound (<c>Dictionary&lt;int, string&gt;</c>).
    /// Re-construct the source definition with those arguments instead of
    /// emitting the definition's own type parameters.
    /// </summary>
    private static ITypeSymbol MaterializeWithErrorTypeArguments(
        ITypeSymbol sourceCandidate, IErrorTypeSymbol error)
    {
        if (sourceCandidate is not INamedTypeSymbol named
            || error is not INamedTypeSymbol errorNamed
            || !named.IsGenericType
            || (named.ContainingType != null && named.ContainingType.IsGenericType)
            || named.TypeParameters.Length != errorNamed.TypeArguments.Length)
        {
            return sourceCandidate;
        }

        for (var index = 0; index < named.TypeParameters.Length; index++)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    named.TypeArguments[index], named.TypeParameters[index]))
            {
                return sourceCandidate;
            }
        }

        return named.Construct(errorNamed.TypeArguments.ToArray());
    }

    private static bool IsDeclaredInSource(ITypeSymbol type)
    {
        foreach (var location in type.Locations)
        {
            if (location.IsInSource)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Renders <paramref name="type"/> as a type syntax that a real per-project
    /// build resolves to the same type the original source bound.
    ///
    /// The pass analyzes the whole corpus as ONE compilation, so a simple name
    /// can bind to a source type declared by ANOTHER project: e.g. in
    /// <c>dotnet.System.Text.RegularExpressions</c> the ancestor namespace
    /// <c>dotnet.System.Text</c> holds System.Private.CoreLib's
    /// <c>dotnet.System.Text.StringBuilder</c>, which shadows the BCL type
    /// imported by <c>using System.Text;</c>. A fully-qualified global name of
    /// that symbol (<c>global::dotnet.System.Text.StringBuilder</c>) does not
    /// exist in the regex project (CS0234). Source-declared components are
    /// therefore rendered in the minimal form that binds at the site position;
    /// the real project's own using/namespace rules then reproduce the original
    /// binding. Metadata-only types keep the dependency-free global form.
    /// </summary>
    public static TypeSyntax RenderProjectVisibleType(
        ITypeSymbol type, SemanticModel? model, int position)
    {
        if (model == null || position < 0
            || !ContainsForeignSourceType(type, model))
        {
            return ParseGlobalType(type);
        }

        return RenderType(type, model, position);
    }

    private static TypeSyntax ParseGlobalType(ITypeSymbol type)
        => SyntaxFactory.ParseTypeName(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    private static TypeSyntax RenderType(
        ITypeSymbol type, SemanticModel model, int position)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return SyntaxFactory.ArrayType(
                    RenderType(array.ElementType, model, position),
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression()))));

            case IPointerTypeSymbol pointer:
                return SyntaxFactory.PointerType(
                    RenderType(pointer.PointedAtType, model, position));

            case INamedTypeSymbol { IsAnonymousType: true }:
                return ParseGlobalType(type);

            case INamedTypeSymbol named when IsForeignSourceType(named, model):
            {
                var minimal = SyntaxFactory.ParseTypeName(
                    named.ToMinimalDisplayString(model, position));
                return minimal.ContainsDiagnostics
                       || !BindsToTypeAtPosition(minimal, model, position, named)
                    ? ParseGlobalType(named)
                    : minimal;
            }

            case INamedTypeSymbol named:
            {
                // Metadata wrapper (List<T>, Nullable<T>, ...): keep the global
                // name and render only the arguments that came from analysis
                // source, so the wrapper never depends on a source-file using.
                var definitionSyntax = ParseGlobalType(named.ConstructedFrom);
                var generic = definitionSyntax switch
                {
                    GenericNameSyntax direct => direct,
                    QualifiedNameSyntax { Right: GenericNameSyntax right } => right,
                    AliasQualifiedNameSyntax { Name: GenericNameSyntax alias } => alias,
                    _ => null,
                };
                if (generic == null)
                    return ParseGlobalType(named);

                var arguments = SyntaxFactory.SeparatedList(
                    named.TypeArguments.Select(
                        argument => RenderType(argument, model, position)));
                return definitionSyntax.ReplaceNode(
                    generic,
                    generic.WithTypeArgumentList(
                        SyntaxFactory.TypeArgumentList(arguments)));
            }

            default:
                return ParseGlobalType(type);
        }
    }

    /// <summary>
    /// True when <paramref name="type"/> is declared in the analysis compilation
    /// but only by syntax trees OTHER than the current file (i.e. another
    /// project's source leaked into the merged compilation). A type declared in
    /// the current file keeps its dependency-free global rendering.
    /// </summary>
    private static bool IsForeignSourceType(
        INamedTypeSymbol type, SemanticModel model)
    {
        if (type.ContainingAssembly == null
            || !SymbolEqualityComparer.Default.Equals(
                type.ContainingAssembly, model.Compilation.Assembly))
        {
            return false;
        }

        var hasSourceLocation = false;
        foreach (var location in type.Locations)
        {
            if (!location.IsInSource)
                continue;

            if (location.SourceTree == model.SyntaxTree)
                return false;

            hasSourceLocation = true;
        }

        return hasSourceLocation;
    }

    /// <summary>
    /// Recursively true when <paramref name="type"/> or any array/pointer
    /// element or generic argument is declared by a foreign analysis source
    /// tree (see <see cref="IsForeignSourceType"/>).
    /// </summary>
    private static bool ContainsForeignSourceType(ITypeSymbol type, SemanticModel model)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return ContainsForeignSourceType(array.ElementType, model);
            case IPointerTypeSymbol pointer:
                return ContainsForeignSourceType(pointer.PointedAtType, model);
            case INamedTypeSymbol named:
                if (IsForeignSourceType(named, model))
                    return true;

                return named.TypeArguments.Any(
                    argument => ContainsForeignSourceType(argument, model));
            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="typeSyntax"/> binds to <paramref name="expected"/>
    /// at the site position of the analysis compilation. A minimal rendering
    /// that would re-bind to a different symbol is not trustworthy.
    /// </summary>
    private static bool BindsToTypeAtPosition(
        TypeSyntax typeSyntax, SemanticModel model, int position, ITypeSymbol expected)
    {
        try
        {
            return model.GetSpeculativeSymbolInfo(
                    position, typeSyntax, SpeculativeBindingOption.BindAsTypeOrNamespace)
                .Symbol is ITypeSymbol resolved
                && SymbolEqualityComparer.Default.Equals(resolved, expected);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when statement sequences can be lifted at <paramref name="node"/>:
    /// an enclosing statement anchor exists (pre-insert / embedded-block wrap)
    /// or an expression-bodied clause can be expanded. Field/property/event
    /// initializers, attribute arguments, default parameter values and query
    /// clauses are anchor-free (design §2.4 可写上下文).
    /// </summary>
    public static bool IsInLiftableContext(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            switch (current)
            {
                case StatementSyntax:
                case ArrowExpressionClauseSyntax:
                    return true;

                // Anchor-free contexts (checked outer-first).
                case FieldDeclarationSyntax:
                case EventFieldDeclarationSyntax:
                case PropertyDeclarationSyntax { Initializer: not null }:
                case AttributeSyntax:
                case AttributeArgumentSyntax:
                // A default parameter value is reached via ParameterSyntax.
                case ParameterSyntax:
                case QueryClauseSyntax:
                    return false;
            }

            current = current.Parent;
        }

        return false;
    }
}
