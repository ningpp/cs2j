using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// A call/declaration position at which a synthesized params collection is
/// bound: the speculative Add probe below binds as if written at this exact
/// position, so accessibility (protected/internal) and conversions match the
/// real call site.
/// </summary>
public sealed record ParamsBindingSite(SemanticModel Model, int Position);

/// <summary>
/// M2 semantic binding helpers for C48 (design §4 PC-3/PC-9, R7 discipline):
/// the K4 collection-initializer <c>Add</c> and the K3 builder factory are
/// resolved through real compiler/SemanticModel binding instead of name/arity
/// guessing ("never guess add/put", same rule as C24/C36).
///
/// K3 factory resolution first consumes the compiler's own answer —
/// <see cref="ICollectionExpressionOperation.ConstructMethod"/> (Roslyn 5.0,
/// M2 probe §13) — and falls back to the symbol-driven C36 CE-10 analyzer
/// (<see cref="CollectionBuilderAnalyzer.TryResolveFactory"/>) for positions
/// without an operation (forwarding overloads, declaration gate).
/// </summary>
internal static class ParamsCollectionBinding
{
    private const string ReadOnlySpanOfTMetadataName = "System.ReadOnlySpan`1";

    // ------------------------------------------------------------------
    // K4: collection-pattern Add binding
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves the actual accessible instance <c>Add</c> method that a
    /// collection initializer emitted for <paramref name="type"/> would bind.
    ///
    /// Primary path (when <paramref name="site"/> sits inside an ordinary
    /// method/constructor body): a foreign speculative tree containing
    /// <c>new global::C(){ default }</c> is bound at the site position and
    /// <c>SemanticModel.GetCollectionInitializerSymbolInfo</c> returns the
    /// compiler-resolved Add symbol (probe: List/HashSet/FluentBag resolve,
    /// Dictionary&lt;K,V&gt;'s two-argument Add fails with
    /// OverloadResolutionFailure, extension-only Add is not selected).
    ///
    /// Fallback (abstract/interface/delegate declarations, local functions,
    /// field-initializer sites): the same collection-pattern rule evaluated
    /// symbolically — an accessible non-static non-generic instance method
    /// named Add with exactly one value parameter whose type implicitly
    /// accepts the IEnumerable&lt;T&gt; element type (mirrors C36
    /// TryFindPatternAdd; a compilable params declaration already passed the
    /// compiler's own check, so this only narrows defensive false positives).
    /// </summary>
    public static IMethodSymbol? TryBindInitializerAdd(
        INamedTypeSymbol type,
        ITypeSymbol elementType,
        Compilation compilation,
        ParamsBindingSite? site)
    {
        var speculative = TryBindInitializerAddViaSpeculativeModel(type, site);
        if (speculative != null)
        {
            return speculative;
        }

        return FindInitializerAddSymbolically(type, elementType, compilation);
    }

    private static IMethodSymbol? TryBindInitializerAddViaSpeculativeModel(
        INamedTypeSymbol type,
        ParamsBindingSite? site)
    {
        if (site == null)
        {
            return null;
        }

        // The speculative method-body binder only exists for ordinary
        // method/constructor bodies containing the position. Local functions,
        // lambdas (their position is inside an enclosing body and works when
        // that body is a regular method) and field/property initializers
        // without an enclosing body fall through to symbolic resolution.
        BaseMethodDeclarationSyntax? enclosingBody = null;
        try
        {
            var node = site.Model.SyntaxTree.GetRoot()
                .FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(site.Position, 0));
            enclosingBody = node.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Body != null);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (enclosingBody?.Body == null)
        {
            return null;
        }

        var typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var auxTree = CSharpSyntaxTree.ParseText(
            "using System.Collections.Generic; " +
            $"class Cs2jAddProbe {{ void _cs2j_probe() {{ var _cs2j_p = new {typeFqn}(){{ default }}; }} }}");
        var auxMethod = auxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (auxMethod == null)
        {
            return null;
        }

        SemanticModel? speculative;
        try
        {
            if (!site.Model.TryGetSpeculativeSemanticModelForMethodBody(
                    site.Position, auxMethod, out speculative))
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        var probeElement = auxTree.GetRoot().DescendantNodes()
            .OfType<InitializerExpressionSyntax>()
            .SelectMany(i => i.Expressions)
            .FirstOrDefault();
        if (probeElement == null)
        {
            return null;
        }

        var symbolInfo = speculative.GetCollectionInitializerSymbolInfo(probeElement);
        var add = symbolInfo.Symbol as IMethodSymbol
                  ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (add == null)
        {
            return null;
        }

        // Params collections require an INSTANCE Add (CS9227 rejects
        // extension-only types); a compiler-bound extension candidate here
        // means the pattern does not hold for params purposes.
        if (add.IsStatic || add.IsExtensionMethod || add.MethodKind != MethodKind.Ordinary)
        {
            return null;
        }

        return add;
    }

    private static IMethodSymbol? FindInitializerAddSymbolically(
        INamedTypeSymbol type,
        ITypeSymbol elementType,
        Compilation compilation)
    {
        var csharpCompilation = compilation as CSharpCompilation;
        IMethodSymbol? identityMatch = null;
        IMethodSymbol? firstMatch = null;

        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers("Add"))
            {
                if (member is not IMethodSymbol method
                    || method.IsStatic
                    || method.IsExtensionMethod
                    || method.IsGenericMethod
                    || method.MethodKind != MethodKind.Ordinary
                    || method.Parameters.Length != 1)
                {
                    continue;
                }

                var parameter = method.Parameters[0];
                if (parameter.RefKind is RefKind.Out or RefKind.Ref)
                {
                    continue;
                }

                if (!IsAccessible(method, compilation.Assembly))
                {
                    continue;
                }

                if (csharpCompilation != null
                    && !csharpCompilation.ClassifyConversion(elementType, parameter.Type).IsImplicit)
                {
                    continue;
                }

                // Prefer the exact Add(T) (identity conversion) over wider
                // overloads such as Add(object)/Add(long); the emitted
                // initializer text is rebound by the compiler anyway, this
                // choice only feeds the gate/stats, but it must be stable.
                identityMatch ??= SymbolEqualityComparer.Default.Equals(parameter.Type, elementType)
                    ? method
                    : null;
                firstMatch ??= method;
            }
        }

        return identityMatch ?? firstMatch;
    }

    // ------------------------------------------------------------------
    // K3: builder factory binding
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts the factory element type X from the bound factory's single
    /// parameter: <c>Create(ReadOnlySpan&lt;X&gt;)</c> (the compiler-required
    /// shape, CS9187) or defensively <c>Create(X[])</c>.
    /// </summary>
    public static ITypeSymbol? TryGetFactoryElementType(IMethodSymbol factory)
    {
        if (factory.Parameters.Length != 1)
        {
            return null;
        }

        var parameterType = factory.Parameters[0].Type;
        if (parameterType is INamedTypeSymbol ros
            && ros.IsGenericType
            && ros.OriginalDefinition.ToDisplayString() == "System.ReadOnlySpan<T>")
        {
            return ros.TypeArguments is { Length: 1 } ? ros.TypeArguments[0] : null;
        }

        return parameterType is IArrayTypeSymbol { Rank: 1 } array ? array.ElementType : null;
    }

    /// <summary>
    /// Validates a factory symbol taken from
    /// <see cref="ICollectionExpressionOperation.ConstructMethod"/>: static,
    /// single ROS/array parameter. Returns the factory + its element type, or
    /// null when the shape is unexpected (defensive — the probe showed the
    /// bound Create on every compilable K3 site, including empty forms).
    /// </summary>
    public static (IMethodSymbol Factory, ITypeSymbol ElementType)? AcceptConstructFactory(
        IMethodSymbol? constructMethod)
    {
        if (constructMethod is not { IsStatic: true, MethodKind: MethodKind.Ordinary })
        {
            return null;
        }

        var elementType = TryGetFactoryElementType(constructMethod);
        return elementType == null ? null : (constructMethod, elementType);
    }

    /// <summary>
    /// Symbol-driven factory resolution (declaration gate / forwarding overload
    /// / call-site fallback): delegates to the proven C36 CE-10 analyzer,
    /// which accepts exactly one accessible static Create with a single
    /// ReadOnlySpan&lt;X&gt; parameter (X[]/params X[] only as a defensive
    /// superset), constructs generic arity from the target's element type and
    /// verifies every fed element converts. Zero candidates → null (CS2J0607).
    /// </summary>
    public static (IMethodSymbol Factory, ITypeSymbol ElementType)? ResolveFactory(
        Compilation compilation,
        INamedTypeSymbol targetType,
        CollectionBuilderInfo builderInfo,
        IReadOnlyList<ITypeSymbol?> elementFedTypes)
    {
        if (compilation is not CSharpCompilation csharpCompilation)
        {
            return null;
        }

        return CollectionBuilderAnalyzer.TryResolveFactory(
                csharpCompilation,
                targetType,
                builderInfo.BuilderType,
                builderInfo.FactoryMethodName,
                elementFedTypes,
                out var resolved,
                out _)
            && resolved != null
            ? (resolved.Factory, resolved.ElementType)
            : null;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Accessibility from the current assembly: public/not-applicable always,
    /// every other accessibility when the member is declared in this assembly
    /// (mirrors C36 TargetTypeResolver.IsAccessible; cross-assembly protected
    /// members are conservatively excluded).
    /// </summary>
    public static bool IsAccessible(ISymbol symbol, IAssemblySymbol currentAssembly)
        => symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.NotApplicable
           || SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, currentAssembly);
}
