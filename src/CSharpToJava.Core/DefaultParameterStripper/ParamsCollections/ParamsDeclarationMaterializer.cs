using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// C48 declaration materializer (T14, R-a hook): strips the <c>params</c>
/// modifier from non-array params-collection declarations (strategy B) and
/// re-issues <c>ParamCollectionAttribute</c> so reflection-driven consumers
/// keep observing the params nature (M0 probe Q13: the attribute carries a
/// parameterless constructor and only exists on emitted metadata, never on
/// source symbols).
///
/// Virtual slot families (override / interface implementation chains, probe
/// Q10) are materialized consistently: when any family member has no
/// registered declaration plan, the whole family is left untouched with
/// CS2J0612 (fail-fast per adjudication 3 — a half-stripped family is worse
/// than an unstripped one because it silently changes overload resolution).
///
/// K1 array declarations keep the params modifier (strategy A, Java varargs
/// direct mapping); xUnit/NUnit test methods keep it as well — their
/// [InlineData] values bind positionally through reflection, and stripping
/// params would change the accepted argument count (same hazard the E08
/// default-value stripper avoids).
/// </summary>
internal sealed class ParamsDeclarationMaterializer
{
    private readonly SemanticModel _model;
    private readonly ParamsCollectionsOptions _options;
    private readonly ParamsCollectionsResult _plans;
    private readonly List<DefaultParameterStripDiagnostic> _diagnostics;
    private readonly HashSet<string> _toleratedResiduals;

    public ParamsDeclarationMaterializer(
        SemanticModel model,
        ParamsCollectionsOptions options,
        ParamsCollectionsResult plans,
        List<DefaultParameterStripDiagnostic> diagnostics,
        HashSet<string> toleratedResiduals)
    {
        _model = model;
        _options = options;
        _plans = plans;
        _diagnostics = diagnostics;
        _toleratedResiduals = toleratedResiduals;
    }

    public bool Changed { get; private set; }

    /// <summary>
    /// Applies C48 materialization to a declaration that has already been
    /// through the E08 pass. <paramref name="originalDeclaration"/> must belong
    /// to the semantic model's tree (symbol lookup); <paramref name="target"/>
    /// is the declaration as rewritten so far. Returns the (possibly) updated
    /// declaration.
    /// </summary>
    public T Materialize<T>(SyntaxNode originalDeclaration, T target)
        where T : SyntaxNode
    {
        var plan = TryGetPlan(originalDeclaration);
        if (plan == null)
        {
            return target;
        }

        var paramsParameter = GetParamsParameter(target, plan.ParamsSlotIndex);
        if (paramsParameter == null)
        {
            return target;
        }

        if (plan.Strategy == ParamsDeclStrategy.KeepParams)
        {
            // Declaration kept (array / unsupported / builder / scope / char
            // face). Classic arrays are legal residuals by design; every other
            // keep reason is recorded so the residual verifier can pair it
            // with the collect-phase skip diagnostic.
            if (plan.Classification.Kind != ParamsParamKind.Array)
            {
                _toleratedResiduals.Add(ParamsResidualKey.Build(originalDeclaration, paramsParameter));
            }

            return target;
        }

        if (originalDeclaration is MethodDeclarationSyntax method && DefaultParameterDeclarationRewriter.IsTestMethod(method))
        {
            // [InlineData] supplies values positionally through reflection —
            // materializing params would change the accepted arity.
            _toleratedResiduals.Add(ParamsResidualKey.Build(originalDeclaration, paramsParameter));
            return target;
        }

        if (!IsFamilyComplete(plan))
        {
            _diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.VirtualFamilyIncomplete,
                string.Format(ParamsDiagnostics.VirtualFamilyIncompleteMessage, plan.Symbol.ToDisplayString()),
                _options.Strict));
            _plans.Statistics.RecordSkipped(ParamsDiagnostics.VirtualFamilyIncomplete);
            _toleratedResiduals.Add(ParamsResidualKey.Build(originalDeclaration, paramsParameter));
            return target;
        }

        var rewritten = StripParamsModifier(paramsParameter);
        if (rewritten == paramsParameter)
        {
            return target;
        }

        Changed = true;
        _plans.Statistics.ParamsDeclarationsMaterialized++;

        return ReplaceParamsParameter(target, paramsParameter, rewritten);
    }

    // ------------------------------------------------------------------
    // Plan resolution (mirrors ParamsSignatureCollector registration)
    // ------------------------------------------------------------------

    private ParamsDeclPlan? TryGetPlan(SyntaxNode declaration)
    {
        IMethodSymbol? symbol = declaration switch
        {
            MethodDeclarationSyntax method => _model.GetDeclaredSymbol(method),
            ConstructorDeclarationSyntax ctor => _model.GetDeclaredSymbol(ctor),
            LocalFunctionStatementSyntax local => _model.GetDeclaredSymbol(local),
            DelegateDeclarationSyntax delegateDeclaration
                => (_model.GetDeclaredSymbol(delegateDeclaration) as INamedTypeSymbol)?.DelegateInvokeMethod,
            TypeDeclarationSyntax typeDeclaration when typeDeclaration.ParameterList is { Parameters.Count: > 0 }
                => _model.GetDeclaredSymbol(typeDeclaration) is INamedTypeSymbol typeSymbol
                    ? ParamsSignatureCollector.ResolvePrimaryConstructorSymbol(typeSymbol, typeDeclaration.ParameterList.Parameters)
                    : null,
            _ => null,
        };

        if (symbol == null)
        {
            return null;
        }

        return _plans.TryGetDeclaration(symbol, out var plan) ? plan : null;
    }

    // ------------------------------------------------------------------
    // Virtual family consistency (probe Q10, adjudication 3)
    // ------------------------------------------------------------------

    /// <summary>
    /// True when every member of the params slot family has its own registered
    /// declaration plan — i.e. its declaring file was collected and will be
    /// materialized by its own strip invocation.
    /// </summary>
    private bool IsFamilyComplete(ParamsDeclPlan plan)
    {
        foreach (var member in plan.VirtualFamily)
        {
            if (!_plans.Declarations.ContainsKey(member))
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Syntax rewriting
    // ------------------------------------------------------------------

    private static ParameterSyntax? GetParamsParameter(SyntaxNode declaration, int paramsSlotIndex)
    {
        var parameterList = declaration switch
        {
            MethodDeclarationSyntax method => method.ParameterList,
            ConstructorDeclarationSyntax ctor => ctor.ParameterList,
            LocalFunctionStatementSyntax local => local.ParameterList,
            DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.ParameterList,
            TypeDeclarationSyntax typeDeclaration => typeDeclaration.ParameterList,
            _ => null,
        };

        if (parameterList == null || paramsSlotIndex >= parameterList.Parameters.Count)
        {
            return null;
        }

        var parameter = parameterList.Parameters[paramsSlotIndex];
        return parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)) ? parameter : null;
    }

    /// <summary>
    /// Drops only the params keyword (keeping ref/out/in/this/scoped and
    /// attributes).
    ///
    /// NOTE (deviation from the plan's adjudication 3): the plan called for
    /// re-issuing <c>ParamCollectionAttribute</c> on the stripped parameter,
    /// but the C# compiler REJECTS explicit application of that attribute with
    /// error CS0674 ("Do not use 'System.ParamArrayAttribute'/
    /// 'System.Runtime.CompilerServices.ParamCollectionAttribute'; use the
    /// params keyword instead") — the attribute can only enter metadata through
    /// the compiler's own params lowering (the M0 probe observed it post-emit,
    /// Q13, and never compiled an explicit application). The metadata-loss
    /// risk (design R5) therefore stands as a registered limitation.
    /// </summary>
    private static ParameterSyntax StripParamsModifier(ParameterSyntax parameter)
    {
        var keptModifiers = SyntaxFactory.TokenList(
            parameter.Modifiers.Where(m => !m.IsKind(SyntaxKind.ParamsKeyword)));
        if (keptModifiers.Count == parameter.Modifiers.Count)
        {
            return parameter; // nothing to strip (defensive)
        }

        return parameter.WithModifiers(keptModifiers);
    }

    private static T ReplaceParamsParameter<T>(T declaration, ParameterSyntax original, ParameterSyntax rewritten)
        where T : SyntaxNode
    {
        SyntaxNode result = declaration switch
        {
            MethodDeclarationSyntax method => method.ReplaceNode(original, rewritten),
            ConstructorDeclarationSyntax ctor => ctor.ReplaceNode(original, rewritten),
            LocalFunctionStatementSyntax local => local.ReplaceNode(original, rewritten),
            DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.ReplaceNode(original, rewritten),
            TypeDeclarationSyntax typeDeclaration => typeDeclaration.ReplaceNode(original, rewritten),
            _ => declaration,
        };

        return result is T typed ? typed : declaration;
    }
}
