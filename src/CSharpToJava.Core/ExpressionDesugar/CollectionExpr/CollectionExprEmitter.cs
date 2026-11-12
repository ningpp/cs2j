using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Result of a spread-bearing site's statement lowering: the declaration +
/// Add/AddRange/foreach statements, the reference expression that replaces the
/// site (<c>__ed_n.ToArray()</c> for array/Span targets; <c>__ed_n</c> for
/// concrete targets) and the per-spread expansion buckets for statistics.
/// </summary>
/// <param name="EmittedInitializerNodes">
/// C24-residual initializer nodes this emission introduces (the
/// <c>var __ed_n = new List&lt;T&gt;(){}</c> temp declaration's empty
/// collection initializer). The C24 whole-tree gate pairs on-disk residuals
/// by count, and C24 analyzes before C36 rewrites, so the rewriter reports
/// this back through <c>InitializerStatistics.EmittedByCollectionExprNodes</c>
/// (W-17②/G-41: cross-pass emitted nodes were silently unpaired).
/// </param>
internal sealed record StatementEmission(
    IReadOnlyList<StatementSyntax> Statements,
    ExpressionSyntax Reference,
    IReadOnlyList<SpreadExpansion> Expansions,
    int EmittedInitializerNodes = 0);

/// <summary>
/// Emission for C36 (CE-2/3/4/7/10; design §4):
/// <list type="bullet">
/// <item>zero-temp array: <c>new T[]{ e0, e1 }</c>, empty site → <c>new T[]{}</c>;</item>
/// <item>zero-temp mutable concrete: <c>new global::C&lt;T&gt;(){ e0, e1 }</c>,
/// empty site → <c>new global::C&lt;T&gt;()</c> (no initializer); T4 interface
/// targets construct <c>new global::...List&lt;T&gt;(){...}</c>;</item>
/// <item>zero-temp builder factory:
/// <c>global::Builder.Create&lt;...&gt;(new TElem[]{...})</c>, empty site →
/// <c>Create(new TElem[]{})</c>;</item>
/// <item>spread-bearing: <c>var __ed_n = new ...(){}</c> plus strict
/// source-order Add / AddRange / foreach-Add; array/Span targets close with
/// ToArray(), collection-builder targets wrap ToArray() in the factory call,
/// concrete/interface targets reference the temp directly.</item>
/// </list>
/// Type syntax is always rendered from symbols via
/// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> (global-qualified,
/// no using dependency). Every element is emitted explicitly — OfType
/// filtering is forbidden. Constant folding is never performed.
/// </summary>
internal static class CollectionExprEmitter
{
    private const string ListOfTMetadataName = "System.Collections.Generic.List`1";

    /// <summary>Parses a global-qualified type name rendered from a symbol.</summary>
    public static TypeSyntax GlobalType(ITypeSymbol type)
        => SyntaxFactory.ParseTypeName(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    /// <summary>CE-2: <c>new T[]{ e0, ... }</c> (empty elements → <c>new T[]{}</c>).</summary>
    public static ExpressionSyntax BuildZeroTempArray(
        ITypeSymbol elementType,
        IReadOnlyList<ExpressionSyntax> elements)
    {
        var arrayType = SyntaxFactory.ArrayType(
            GlobalType(elementType),
            SyntaxFactory.SingletonList(
                SyntaxFactory.ArrayRankSpecifier(
                    SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                        SyntaxFactory.OmittedArraySizeExpression()))));

        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.ArrayInitializerExpression,
            SyntaxFactory.SeparatedList(elements));

        return SyntaxFactory.ArrayCreationExpression(arrayType, initializer);
    }

    /// <summary>
    /// CE-3/CE-4: <c>new global::C&lt;T&gt;(){ e0, ... }</c>; an empty site
    /// emits <c>new global::C&lt;T&gt;()</c> without an initializer. For
    /// interface targets <paramref name="targetType"/> is the selected
    /// <c>List&lt;T&gt;</c> concrete type.
    /// </summary>
    public static ExpressionSyntax BuildZeroTempCollection(
        INamedTypeSymbol targetType,
        IReadOnlyList<ExpressionSyntax> elements)
    {
        var creation = SyntaxFactory.ObjectCreationExpression(GlobalType(targetType))
            .WithArgumentList(SyntaxFactory.ArgumentList());

        if (elements.Count > 0)
        {
            creation = creation.WithInitializer(
                SyntaxFactory.InitializerExpression(
                    SyntaxKind.CollectionInitializerExpression,
                    SyntaxFactory.SeparatedList(elements)));
        }

        return creation;
    }

    /// <summary>
    /// T6 constructible concrete class/struct: an empty site emits
    /// <c>new global::T()</c> (the no-required-parameter constructor); a
    /// non-empty site emits <c>new global::T(new TElem[]{ e0, ... })</c>,
    /// which binds to the single <c>T[]</c>/<c>ReadOnlySpan&lt;T&gt;</c>
    /// parameter (array elements are passed through in source order).
    /// </summary>
    public static ExpressionSyntax BuildConstructibleCollection(
        INamedTypeSymbol targetType,
        ITypeSymbol? elementType,
        IReadOnlyList<ExpressionSyntax> elements)
    {
        var creation = SyntaxFactory.ObjectCreationExpression(GlobalType(targetType))
            .WithArgumentList(SyntaxFactory.ArgumentList());

        if (elements.Count == 0)
            return creation;

        return creation.WithArgumentList(
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        BuildZeroTempArray(elementType!, elements)))));
    }

    /// <summary>
    /// CE-10: <c>global::BuilderType.CreateMethod&lt;TypeArgs&gt;(arg)</c>
    /// where arg is a zero-temp <c>new TElem[]{...}</c> (implicit array →
    /// ReadOnlySpan&lt;TElem&gt; conversion binds at the call). The factory
    /// symbol is already constructed when generic (e.g.
    /// <c>ImmutableArray.Create&lt;int&gt;</c>), so its concrete type
    /// arguments are rendered explicitly. Empty sites emit <c>new TElem[]{}</c>.
    /// </summary>
    public static ExpressionSyntax BuildBuilderFactory(
        IMethodSymbol factory,
        ITypeSymbol elementType,
        IReadOnlyList<ExpressionSyntax> elements)
    {
        var receiver = GlobalType(factory.ContainingType);

        SimpleNameSyntax methodName;
        if (factory.TypeArguments is { Length: > 0 } typeArguments)
        {
            methodName = SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(factory.Name),
                SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SeparatedList<TypeSyntax>(
                        typeArguments.Select(GlobalType))));
        }
        else
        {
            methodName = SyntaxFactory.IdentifierName(factory.Name);
        }

        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver,
            methodName);

        var argument = SyntaxFactory.Argument(
            BuildZeroTempArray(elementType, elements));

        return SyntaxFactory.InvocationExpression(
            memberAccess,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(argument)));
    }

    /// <summary>
    /// CE-10 spread form: invokes the resolved factory with a single
    /// pre-built argument expression, e.g. <c>Create(__ed_n.ToArray())</c>
    /// (the temp already holds every element in source order).
    /// </summary>
    public static ExpressionSyntax BuildBuilderFactoryInvocation(
        IMethodSymbol factory,
        ExpressionSyntax argumentExpression)
    {
        var receiver = GlobalType(factory.ContainingType);

        SimpleNameSyntax methodName;
        if (factory.TypeArguments is { Length: > 0 } typeArguments)
        {
            methodName = SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(factory.Name),
                SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SeparatedList<TypeSyntax>(
                        typeArguments.Select(GlobalType))));
        }
        else
        {
            methodName = SyntaxFactory.IdentifierName(factory.Name);
        }

        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver,
            methodName);

        return SyntaxFactory.InvocationExpression(
            memberAccess,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(argumentExpression))));
    }

    /// <summary>
    /// CE-5/6/7 (M2: CE-4/10 too): builds the temp declaration plus the
    /// strict source-order statement sequence for a spread-bearing site.
    /// Temp name, lift ordering and foreach variables come from the
    /// Analyze-phase deterministic allocation (CE-8); AddRange vs foreach is
    /// decided per spread in the plan (never recomputed here).
    /// </summary>
    /// <param name="model">semantic model (original tree) for symbol lookups.</param>
    /// <param name="plan">site plan (TempName and spread shapes pre-allocated).</param>
    /// <param name="visitedElements">elements of the visited collection node (1:1 with the plan).</param>
    public static StatementEmission BuildStatementSequence(
        SemanticModel model,
        CollectionExprPlan plan,
        IReadOnlyList<CollectionElementSyntax> visitedElements)
    {
        var tempId = SyntaxFactory.IdentifierName(plan.TempName!);
        var statements = new List<StatementSyntax>();
        var expansions = new List<SpreadExpansion>();

        // var __ed_n = new List<TElem>(){};  (array/Span/interface/builder path)
        // var __ed_n = new global::C<T>();   (T3 concrete target path)
        ExpressionSyntax tempCreation;
        if (plan.UsesListTemp)
        {
            var listDefinition = model.Compilation
                .GetTypeByMetadataName(ListOfTMetadataName)!;
            var listType = listDefinition.Construct(plan.TargetElementType!);
            tempCreation = SyntaxFactory.ObjectCreationExpression(GlobalType(listType))
                .WithArgumentList(SyntaxFactory.ArgumentList())
                .WithInitializer(
                    SyntaxFactory.InitializerExpression(
                        SyntaxKind.CollectionInitializerExpression,
                        SyntaxFactory.SeparatedList<ExpressionSyntax>()));
        }
        else
        {
            tempCreation = SyntaxFactory.ObjectCreationExpression(
                    GlobalType(plan.EffectiveConstructedType!))
                .WithArgumentList(SyntaxFactory.ArgumentList());
        }

        statements.Add(
            ExpressionDesugarTempLifter.DeclareVar(plan.TempName!, tempCreation));

        for (var i = 0; i < plan.Elements.Count; i++)
        {
            var elementPlan = plan.Elements[i];

            if (elementPlan.Kind == CollectionElementKind.Expression)
            {
                var expression = ((ExpressionElementSyntax)visitedElements[i]).Expression;
                statements.Add(SyntaxFactory.ExpressionStatement(
                    SpreadExpander.MemberInvoke(tempId, "Add", expression)));
                continue;
            }

            // Spread: the visited source expression is inlined exactly once.
            var spreadSyntax = (SpreadElementSyntax)visitedElements[i];
            var source = spreadSyntax.Expression;
            var sourceKind = elementPlan.Spread!.SourceKind;

            var (spreadStatements, expansion) = SpreadExpander.BuildExpansion(
                sourceKind,
                source,
                tempId,
                elementPlan.Spread.LoopVariable!,
                elementPlan.Spread.UseAddRange);
            statements.AddRange(spreadStatements);
            expansions.Add(expansion);
        }

        // Final reference at the original site:
        //  T1/T2 array/Span  -> __ed_n.ToArray()
        //  T5 builder        -> global::B.Create<...>(__ed_n.ToArray())
        //  T4 interface / T3 -> __ed_n  (List<T> converts to the interface)
        ExpressionSyntax reference;
        if (plan.EmitsArrayForm)
        {
            reference = ToArrayInvocation(tempId);
        }
        else if (plan.TargetKind == TargetKind.CollectionBuilder)
        {
            reference = BuildBuilderFactoryInvocation(
                plan.BuilderFactory!,
                ToArrayInvocation(tempId));
        }
        else
        {
            reference = tempId;
        }

        return new StatementEmission(
            statements,
            reference,
            expansions,
            InitializerSyntaxFacts.CountResidualNodes(tempCreation));
    }

    /// <summary>
    /// True when a spread-bearing site with no statement anchor can be lowered
    /// to an equivalent expression instead of a lifted statement sequence.
    ///
    /// The site is inline-expressible when its target has an expression form
    /// (<c>T[]</c>/Span&lt;T&gt;, List&lt;T&gt;/constructible interface,
    /// [CollectionBuilder] factory) and every spread source is an ordinary
    /// enumerable expression whose element type reaches the target element
    /// type (identity, or an implicit reference conversion for the covariant
    /// array/List shapes). Span sources are only expressible as the pure
    /// <c>source.ToArray()</c> form; mixing one with other elements keeps the
    /// site on CS2J0583, as do attribute/default-value targets that cannot
    /// hold a call.
    /// </summary>
    public static bool CanInlineSpread(
        SemanticModel model,
        TargetResolution resolution,
        IReadOnlyList<ElementPlan> elements)
    {
        if (resolution.TargetElementType == null)
            return false;

        var allowsReferenceConversion = false;
        switch (resolution.Kind)
        {
            case TargetKind.Array:
            case TargetKind.Interface:
                allowsReferenceConversion = true;
                break;
            case TargetKind.MutableConcrete:
                // Only List<T> has the IEnumerable<T> constructor this form
                // needs; any other T3 concrete target keeps CS2J0583.
                if (resolution.ConvertedType is not INamedTypeSymbol concrete
                    || concrete.OriginalDefinition.ToDisplayString()
                        != "System.Collections.Generic.List<T>")
                {
                    return false;
                }

                allowsReferenceConversion = true;
                break;
            case TargetKind.Span:
                break;
            case TargetKind.CollectionBuilder when resolution.BuilderFactory != null:
                break;
            default:
                return false;
        }

        var hasSpread = false;
        foreach (var element in elements)
        {
            if (element.Kind != CollectionElementKind.Spread)
                continue;

            var spread = element.Spread;
            if (spread == null || spread.SourceKind == SpreadSourceKind.Unknown)
                return false;

            // Span/ReadOnlySpan is a ref struct and cannot enter the
            // Enumerable.Concat chain; its own ToArray() only covers the pure
            // single-spread site.
            if (spread.SourceKind == SpreadSourceKind.Span && elements.Count > 1)
                return false;

            if (!AcceptsSpreadElement(
                    model.Compilation,
                    spread.ElementType,
                    resolution.TargetElementType,
                    allowsReferenceConversion))
            {
                return false;
            }

            hasSpread = true;
        }

        return hasSpread;
    }

    private static bool AcceptsSpreadElement(
        Compilation compilation,
        ITypeSymbol? source,
        ITypeSymbol target,
        bool allowsReferenceConversion)
    {
        if (source == null)
            return false;
        if (SymbolEqualityComparer.Default.Equals(source, target))
            return true;

        return allowsReferenceConversion
               && !source.IsValueType
               && !target.IsValueType
               && compilation.ClassifyConversion(source, target).IsImplicit;
    }

    /// <summary>
    /// Inline lowering for an anchor-free spread site (see
    /// <see cref="CanInlineSpread"/>). Elements and spreads keep their source
    /// order and are evaluated exactly once at the site's own position:
    /// <list type="bullet">
    /// <item><c>T[]</c>/Span targets → <c>Enumerable.ToArray(chain)</c>;</item>
    /// <item>List/interface targets → <c>new List&lt;T&gt;(Enumerable.ToArray(chain))</c>;</item>
    /// <item>[CollectionBuilder] targets → <c>Factory.Create(Enumerable.ToArray(chain))</c>.</item>
    /// </list>
    /// <c>chain</c> is the single spread source or the left-to-right
    /// <c>Enumerable.Concat</c> of one <c>new T[]{ element }</c> segment per
    /// ordinary element and one source segment per spread. A pure Span spread
    /// uses its own <c>source.ToArray()</c> (a ref struct has no other form).
    /// </summary>
    public static ExpressionSyntax BuildInlineSpread(
        SemanticModel model,
        CollectionExprPlan plan,
        IReadOnlyList<CollectionElementSyntax> visitedElements)
    {
        var elementType = plan.TargetElementType!;
        ExpressionSyntax? chain = null;
        ExpressionSyntax? onlySpreadSource = null;
        var onlySpreadKind = SpreadSourceKind.Unknown;
        var spreadCount = 0;

        for (var i = 0; i < plan.Elements.Count; i++)
        {
            var elementPlan = plan.Elements[i];
            ExpressionSyntax segment;
            if (elementPlan.Kind == CollectionElementKind.Expression)
            {
                segment = BuildZeroTempArray(
                    elementType,
                    new[] { ((ExpressionElementSyntax)visitedElements[i]).Expression });
            }
            else
            {
                var spread = elementPlan.Spread!;
                spreadCount++;
                onlySpreadSource = ((SpreadElementSyntax)visitedElements[i]).Expression;
                onlySpreadKind = spread.SourceKind;
                segment = onlySpreadSource;
            }

            chain = chain == null
                ? segment
                : EnumerableInvoke(model, "Concat", chain, segment);
        }

        ExpressionSyntax arrayExpression;
        if (spreadCount == 1
            && plan.Elements.Count == 1
            && onlySpreadKind == SpreadSourceKind.Span)
        {
            arrayExpression = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    onlySpreadSource!,
                    SyntaxFactory.IdentifierName("ToArray")),
                SyntaxFactory.ArgumentList());
        }
        else
        {
            arrayExpression = EnumerableInvoke(model, "ToArray", chain!);
        }

        switch (plan.TargetKind)
        {
            case TargetKind.CollectionBuilder:
                return BuildBuilderFactoryInvocation(
                    plan.BuilderFactory!, arrayExpression);

            case TargetKind.MutableConcrete:
            case TargetKind.Interface:
                return SyntaxFactory.ObjectCreationExpression(
                        GlobalType(plan.EffectiveConstructedType!))
                    .WithArgumentList(
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(arrayExpression))));

            default:
                return arrayExpression;
        }
    }

    /// <summary>
    /// <c>global::System.Linq.Enumerable.Method(args…)</c> — fully qualified,
    /// so the emission never depends on a source-file using directive.
    /// </summary>
    private static ExpressionSyntax EnumerableInvoke(
        SemanticModel model,
        string methodName,
        params ExpressionSyntax[] arguments)
    {
        var enumerable = model.Compilation
            .GetTypeByMetadataName("System.Linq.Enumerable")!;

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                GlobalType(enumerable),
                SyntaxFactory.IdentifierName(methodName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(arguments.Select(SyntaxFactory.Argument))));
    }

    /// <summary><c>temp.ToArray()</c> (List&lt;T&gt;, BCL-only; no System.Linq).</summary>
    private static InvocationExpressionSyntax ToArrayInvocation(ExpressionSyntax tempId)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                tempId,
                SyntaxFactory.IdentifierName("ToArray")),
            SyntaxFactory.ArgumentList());

    /// <summary>Extracts ordinary element expressions from a visited zero-temp site.</summary>
    public static IReadOnlyList<ExpressionSyntax> ExtractExpressions(
        IReadOnlyList<CollectionElementSyntax> visitedElements)
    {
        var expressions = new ExpressionSyntax[visitedElements.Count];
        for (var i = 0; i < visitedElements.Count; i++)
            expressions[i] = ((ExpressionElementSyntax)visitedElements[i]).Expression;
        return expressions;
    }
}
