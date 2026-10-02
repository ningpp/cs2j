using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// C48 Collect pass: walks the same declaration kinds as
/// <see cref="CSharpToJava.Core.DefaultParameterStripper.DefaultParameterCatalog.Collect"/>
/// (methods, constructors, local functions, delegates, record/class/struct
/// primary constructors) and registers every method-like symbol whose last
/// parameter carries <c>params</c>, classified through
/// <see cref="ParamsTargetClassifier"/>.
///
/// The params flag is read exclusively from
/// <c>IParameterSymbol.IsParams</c>/<c>IsParamsCollection</c> — source
/// parameter symbols never carry the params attributes (M0 probe Q2), so
/// attributes must not be consulted.
///
/// Classification failures (error types, types outside the legal set) emit
/// CS2J0601 (Error in strict mode) without interrupting the walk — baseline
/// illegal shapes are unreachable in compilable input (probe Q9), so this is a
/// defensive net only.
///
/// Virtual slot families are grouped per probe Q10: the override chain
/// (<c>OverriddenMethod</c>) plus explicit interface implementations and the
/// interface members this symbol implements
/// (<c>AllInterfaces</c>/<c>FindImplementationForInterfaceMember</c>).
/// </summary>
internal sealed class ParamsSignatureCollector
{
    /// <summary>Depth guard for the override chain, mirroring the default-parameter catalog.</summary>
    private const int MaxLookupDepth = 8;

    public void Collect(
        SyntaxTree tree,
        SemanticModel model,
        ParamsCollectionsOptions options,
        ParamsCollectionsResult result)
    {
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    RegisterDeclared(model, method, method.ParameterList?.Parameters ?? default, options, result);
                    break;
                case ConstructorDeclarationSyntax ctor:
                    RegisterDeclared(model, ctor, ctor.ParameterList?.Parameters ?? default, options, result);
                    break;
                case LocalFunctionStatementSyntax local when local.ParameterList != null:
                    RegisterDeclared(model, local, local.ParameterList.Parameters, options, result);
                    break;
                case DelegateDeclarationSyntax @delegate when @delegate.ParameterList != null:
                    RegisterDelegate(model, @delegate, options, result);
                    break;
                case TypeDeclarationSyntax type when type.ParameterList is { Parameters.Count: > 0 }:
                    RegisterPrimaryConstructor(model, type, options, result);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    private static void RegisterDeclared(
        SemanticModel model,
        SyntaxNode declaration,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        ParamsCollectionsOptions options,
        ParamsCollectionsResult result)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        if (model.GetDeclaredSymbol(declaration) is not IMethodSymbol symbol)
        {
            return;
        }

        RegisterSymbol(symbol, declaration, model.Compilation, options, result);
    }

    private static void RegisterDelegate(
        SemanticModel model,
        DelegateDeclarationSyntax declaration,
        ParamsCollectionsOptions options,
        ParamsCollectionsResult result)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol delegateType)
        {
            return;
        }

        var invoke = delegateType.DelegateInvokeMethod;
        if (invoke == null)
        {
            return;
        }

        RegisterSymbol(invoke, declaration, model.Compilation, options, result);
    }

    private static void RegisterPrimaryConstructor(
        SemanticModel model,
        TypeDeclarationSyntax type,
        ParamsCollectionsOptions options,
        ParamsCollectionsResult result)
    {
        if (model.GetDeclaredSymbol(type) is not INamedTypeSymbol typeSymbol)
        {
            return;
        }

        var ctor = ResolvePrimaryConstructorSymbol(typeSymbol, type.ParameterList!.Parameters);
        if (ctor == null)
        {
            return;
        }

        RegisterSymbol(ctor, type, model.Compilation, options, result);
    }

    /// <summary>
    /// Resolves the instance constructor symbol declared by a record / class /
    /// struct primary constructor. Shared with the declaration materializer so
    /// both phases map the same syntax node onto the same symbol.
    /// </summary>
    internal static IMethodSymbol? ResolvePrimaryConstructorSymbol(
        INamedTypeSymbol typeSymbol,
        SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        return typeSymbol.InstanceConstructors.FirstOrDefault(c =>
            c.Parameters.Length == parameters.Count
            && (parameters.Count == 0 || c.Parameters[0].Name == parameters[0].Identifier.ValueText));
    }

    private static void RegisterSymbol(
        IMethodSymbol symbol,
        SyntaxNode declaration,
        Compilation compilation,
        ParamsCollectionsOptions options,
        ParamsCollectionsResult result)
    {
        if (symbol.Parameters.Length == 0)
        {
            return;
        }

        var lastParameter = symbol.Parameters[^1];
        if (!lastParameter.IsParams)
        {
            return;
        }

        var classification = ParamsTargetClassifier.Classify(lastParameter.Type, compilation);
        result.Statistics.RecordSite(classification.Kind);

        var strategy = DecideStrategy(classification, symbol, options, result);

        result.AddDeclaration(new ParamsDeclPlan(
            declaration,
            symbol,
            strategy,
            symbol.Parameters.Length - 1,
            classification,
            CollectVirtualFamily(symbol)));
    }

    // ------------------------------------------------------------------
    // Strategy decision (declaration side; call sites gate off the strategy)
    // ------------------------------------------------------------------

    /// <summary>
    /// Decides the declaration-side strategy. K1 arrays keep the params
    /// modifier (strategy A). Everything that cannot — or must not, in v1 — be
    /// materialized also keeps the modifier, with its skip diagnostic emitted
    /// here exactly once per declaration: unsupported types (0601),
    /// [CollectionBuilder] types (0607), ref-struct elements (0608), scope
    /// exclusions (0601) and the ReadOnlySpan&lt;char&gt; char face (0601,
    /// C52 boundary). Call sites of these declarations stay untouched.
    /// </summary>
    private static ParamsDeclStrategy DecideStrategy(
        ParamsTargetClassification classification,
        IMethodSymbol symbol,
        ParamsCollectionsOptions options,
        ParamsCollectionsResult result)
    {
        if (classification.Kind == ParamsParamKind.Array)
        {
            return ParamsDeclStrategy.KeepParams;
        }

        if (classification.SkipReason is string skipReason)
        {
            result.Diagnostics.Add(ParamsDiagnostics.Create(
                skipReason,
                ParamsDiagnostics.BuildSkipMessage(classification, symbol.ToDisplayString()),
                options.Strict));
            result.Statistics.RecordSkipped(skipReason);
            return ParamsDeclStrategy.KeepParams;
        }

        if (classification.Kind == ParamsParamKind.Builder)
        {
            // v1 blocks every builder site (adjudication 4: the K3 emission
            // path follows C36 CE-10 in M2).
            var builderType = classification.BuilderInfo?.BuilderType.ToDisplayString() ?? "<unknown>";
            var factoryName = classification.BuilderInfo?.FactoryMethodName ?? "<unknown>";
            result.Diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.CollectionBuilderBlocked,
                string.Format(ParamsDiagnostics.CollectionBuilderBlockedMessage,
                    classification.ParameterType.ToDisplayString(), builderType, factoryName),
                options.Strict));
            result.Statistics.RecordSkipped(ParamsDiagnostics.CollectionBuilderBlocked);
            return ParamsDeclStrategy.KeepParams;
        }

        if (classification.Kind == ParamsParamKind.Span
            && classification.ElementType?.SpecialType == SpecialType.System_Char)
        {
            result.Diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.UnsupportedType,
                string.Format(ParamsDiagnostics.CharFaceExcludedMessage,
                    classification.ParameterType.ToDisplayString()),
                options.Strict));
            result.Statistics.RecordSkipped(ParamsDiagnostics.UnsupportedType);
            return ParamsDeclStrategy.KeepParams;
        }

        if (!IsKindInScope(classification.Kind, options.Scope))
        {
            result.Diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.UnsupportedType,
                string.Format(ParamsDiagnostics.ScopeExcludedMessage,
                    classification.ParameterType.ToDisplayString()),
                options.Strict));
            result.Statistics.RecordSkipped(ParamsDiagnostics.UnsupportedType);
            return ParamsDeclStrategy.KeepParams;
        }

        return ParamsDeclStrategy.Materialize;
    }

    /// <summary>Per-kind gray-release gate (design §2.4 Scope; default All).</summary>
    public static bool IsKindInScope(ParamsParamKind kind, ParamsCollectionsScope scope)
        => scope switch
        {
            ParamsCollectionsScope.All => true,
            ParamsCollectionsScope.Span => kind == ParamsParamKind.Span,
            ParamsCollectionsScope.MutableCollection => kind == ParamsParamKind.Initializable,
            ParamsCollectionsScope.Interface => kind == ParamsParamKind.Interface,
            ParamsCollectionsScope.CollectionBuilder => kind == ParamsParamKind.Builder,
            _ => true,
        };

    // ------------------------------------------------------------------
    // Virtual slot family (probe Q10)
    // ------------------------------------------------------------------

    /// <summary>
    /// Collects the override/interface family of a params slot: the symbol
    /// itself, its override chain, its explicit interface implementations and
    /// the interface members it implements. All family members carry identical
    /// params flags (probe Q10), so strategy B can materialize the family
    /// consistently.
    /// </summary>
    private static ImmutableArray<IMethodSymbol> CollectVirtualFamily(IMethodSymbol symbol)
    {
        var family = new List<IMethodSymbol>();

        void AddIfMissing(IMethodSymbol? candidate)
        {
            if (candidate == null)
            {
                return;
            }

            foreach (var existing in family)
            {
                if (SymbolEqualityComparer.Default.Equals(existing, candidate))
                {
                    return;
                }
            }

            family.Add(candidate);
        }

        var current = symbol;
        var depth = 0;
        while (current != null && depth++ < MaxLookupDepth)
        {
            AddIfMissing(current);
            foreach (var explicitImplementation in current.ExplicitInterfaceImplementations)
            {
                AddIfMissing(explicitImplementation);
            }

            current = current.OverriddenMethod;
        }

        if (symbol.ContainingType is INamedTypeSymbol containingType)
        {
            foreach (var implementedInterface in containingType.AllInterfaces)
            {
                foreach (var member in implementedInterface.GetMembers())
                {
                    if (member is not IMethodSymbol interfaceMethod)
                    {
                        continue;
                    }

                    var implementation =
                        containingType.FindImplementationForInterfaceMember(interfaceMethod);
                    if (implementation != null
                        && SymbolEqualityComparer.Default.Equals(
                            implementation.OriginalDefinition, symbol.OriginalDefinition))
                    {
                        AddIfMissing(interfaceMethod);
                    }
                }
            }
        }

        return family.ToImmutableArray();
    }
}
