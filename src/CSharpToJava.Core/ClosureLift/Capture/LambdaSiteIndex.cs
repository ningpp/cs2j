using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// One lambda site classified by the M1 index: the enclosing routine is a
/// supported analysis root (design D21) and the lambda is not excluded
/// outright (quoted / synthesized / initializer-host). <see cref="SpanStart"/>
/// is the site anchor — unique per source occurrence.
/// </summary>
internal sealed record StaticLambdaSite(
    int SpanStart,
    LambdaExpressionSyntax Lambda,
    SyntaxNode Routine,
    TypeDeclarationSyntax HostType,
    string RoutineName,
    /// <summary>C# 9 `static` modifier present (D10: same path, analysis still verifies).</summary>
    bool HasStaticKeyword,
    bool IsAsync);

/// <summary>
/// C45 M1 site index (design §4: Capture/LambdaSiteIndex). Enumerates the
/// statement lambdas of one CompilationUnit against the read-only model and
/// decides the zero-capture (static-method) candidates (D10):
///   - quoted Expression&lt;TDelegate&gt; lambdas are excluded whole (D18, shared
///     <see cref="ExpressionTreeFacts"/> predicate);
///   - nodes carrying the cs2j_closure_lift annotation are excluded (§3.2);
///   - field/property-initializer and residual-local-function hosts stay out
///     (D18③/D21 — C32 rows already own the non-static part of that family);
///   - a candidate must capture no local/parameter/range symbol, no `this`
///     (implicit, explicit or `base`), reference no local function and no
///     enclosing-method type parameter (D19 is M2).
/// The single capture-classification implementation
/// (<see cref="CaptureClassification.CollectCapturedSymbols"/>) is reused —
/// query-only, no second implementation (design §4 note).
/// </summary>
internal static class LambdaSiteIndex
{
    private static readonly Func<SyntaxNode, bool> DescendNotIntoLocalFunctions
        = static d => d is not LocalFunctionStatementSyntax;

    public static IReadOnlyList<StaticLambdaSite> CollectSites(
        CompilationUnitSyntax analysisRoot,
        SemanticModel model)
    {
        var sites = new List<StaticLambdaSite>();

        foreach (var lambda in analysisRoot.DescendantNodes().OfType<LambdaExpressionSyntax>())
        {
            // §3.2 idempotence: never re-analyze synthesized nodes.
            if (ClosureLiftAnnotations.IsAnnotated(lambda))
                continue;

            if (ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
                continue; // D18: quoted expression-tree lambdas are skipped whole.

            if (ResolveRoutine(lambda) is not { } routine)
                continue; // initializer host / residual local function: C32 rows own these.

            var hostType = routine.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (hostType is not (ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax))
                continue; // interface / top-level statements: no insertable host type (D21).

            if (!IsZeroCapture(model, lambda, routine))
                continue;

            sites.Add(new StaticLambdaSite(
                lambda.SpanStart,
                lambda,
                routine,
                hostType,
                GetRoutineName(routine, model, hostType),
                lambda.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
                lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)));
        }

        // Deterministic order: lambda SpanStart (design D9 k sequence input).
        sites.Sort(static (a, b) => a.Lambda.SpanStart.CompareTo(b.Lambda.SpanStart));
        return sites;
    }

    /// <summary>
    /// Mirrors the C32 analyzer's routine resolution: methods, constructors,
    /// destructors, operators, conversion operators and accessors are roots;
    /// lambdas under residual local functions or field/property initializers
    /// resolve to nothing (M1 does not lift them).
    /// </summary>
    private static SyntaxNode? ResolveRoutine(LambdaExpressionSyntax lambda)
    {
        for (var current = lambda.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case AccessorDeclarationSyntax:
                case BaseMethodDeclarationSyntax:
                    return current;
                case LocalFunctionStatementSyntax:
                case PropertyDeclarationSyntax:
                case IndexerDeclarationSyntax:
                case BaseFieldDeclarationSyntax:
                    return null;
            }
        }

        return null;
    }

    private static bool IsZeroCapture(SemanticModel model, LambdaExpressionSyntax lambda, SyntaxNode routine)
    {
        var lambdaBody = GetBodyRoot(lambda);
        if (lambdaBody == null)
            return false;

        // Synthesized anonymous-function method symbol owns the lambda's own
        // parameters (Roslyn 5 GetSymbolInfo shape, same as the C32 analyzer).
        var lambdaSymbol = model.GetSymbolInfo(lambda).Symbol as IMethodSymbol;
        var routineSymbol = model.GetDeclaredSymbol(routine) as IMethodSymbol;

        // 1. No captured local/parameter/range symbol (single implementation, E02
        //    extract). The LAMBDA node is the walk root — a bare expression body
        //    would hide its own root identifier from DescendantNodes().
        if (CaptureClassification
            .CollectCapturedSymbols(model, lambda, lambdaSymbol, DescendNotIntoLocalFunctions)
            .Count > 0)
        {
            return false;
        }

        // D4 (M3): the lambda's OWN parameters and locals that a NESTED lambda
        // references are invisible to step 1 (they belong to this lambda), but
        // a static host method cannot host the nested capturing lambda — the
        // value only exists during this lambda's invocation. Such an ancestor
        // lambda must take the display-class instance path.
        if (CaptureClassification.OwnsStateForNestedLambda(model, lambda))
            return false;

        foreach (var node in lambda.DescendantNodes(SkipOwnParameterSubtrees))
        {
            // 2. this / base capture: explicit forms (D7 is M2).
            if (node is ThisExpressionSyntax or BaseExpressionSyntax)
                return false;

            // 3. A nested local function keeps its own capture scope — the
            //    lifted static method could not host it safely in M1.
            if (node is LocalFunctionStatementSyntax)
                return false;

            if (node is not (IdentifierNameSyntax or GenericNameSyntax))
                continue;

            var name = (ExpressionSyntax)node;
            if (name.Parent is NameColonSyntax || CaptureClassification.IsInsideNameOf(name))
                continue;
            // Skip the member NAME of a qualified access (`x.M`, `a?.M`, `Type.P`):
            // the receiver expression is analyzed on its own.
            if (name.Parent is MemberAccessExpressionSyntax member && member.Name == name)
                continue;
            if (name.Parent is MemberBindingExpressionSyntax binding && binding.Name == name)
                continue;
            // Object/anonymous initializers name members of the NEW object, not
            // members of `this` (`new C { Prop = 1 }`, `new { A = x }`).
            if (name.Parent is NameEqualsSyntax)
                continue;
            if (name.Parent is AssignmentExpressionSyntax targetAssignment
                && targetAssignment.Left == name
                && targetAssignment.Parent is InitializerExpressionSyntax)
            {
                continue;
            }
            // A generic name is only meaningful when it denotes a member or a type;
            // type-argument identifiers are reached through the same walk.
            if (node is GenericNameSyntax
                && name.Parent is not InvocationExpressionSyntax
                && name.Parent?.Parent is not InvocationExpressionSyntax)
            {
                continue;
            }

            var symbol = model.GetSymbolInfo(name).Symbol;
            switch (symbol)
            {
                // 4. Reference to a local function — the lifted method cannot call it.
                case IMethodSymbol { MethodKind: MethodKind.LocalFunction }:
                    return false;

                // 5. Implicit-this reference to an instance member (D7 is M2).
                case IFieldSymbol { IsStatic: false }:
                case IPropertySymbol { IsStatic: false }:
                case IEventSymbol { IsStatic: false }:
                case IMethodSymbol { IsStatic: false, IsExtensionMethod: false }:
                    return false;

                // 6. Enclosing-method type parameter (D19 generic surface is M2).
                //    Type parameters of a nested local function are its own and stay legal.
                case ITypeParameterSymbol typeParameter:
                    if (typeParameter.ContainingSymbol is IMethodSymbol containing
                        && SymbolEqualityComparer.Default.Equals(containing, routineSymbol))
                    {
                        return false;
                    }

                    break;

                // 7. External local/parameter of ErrorType: not zero capture —
                //    M2's CollectFacts owns the CS2J4502 row (never synthesize
                //    a static method whose body could not bind).
                case ILocalSymbol local when local.Type.TypeKind == TypeKind.Error
                    && !SymbolEqualityComparer.Default.Equals(local.ContainingSymbol, lambdaSymbol):
                case IParameterSymbol parameter when parameter.Type.TypeKind == TypeKind.Error
                    && !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, lambdaSymbol):
                    return false;
            }
        }

        return true;

        // D12④: the lambda's own parameter list (names, explicit types and
        // migrated attribute lists such as `[Marker] int _`) is signature, not
        // body — an attribute constructor is an instance method symbol but it
        // is never an implicit-this capture of the lambda.
        bool SkipOwnParameterSubtrees(SyntaxNode node)
        {
            if (node is ParameterListSyntax list && list.Parent == lambda)
                return false;
            if (node is ParameterSyntax parameter
                && parameter.Parent is SimpleLambdaExpressionSyntax simple
                && simple == lambda)
            {
                return false;
            }

            return true;
        }
    }

    private static SyntaxNode? GetBodyRoot(LambdaExpressionSyntax lambda) => lambda switch
    {
        ParenthesizedLambdaExpressionSyntax parenthesized
            => (SyntaxNode?)parenthesized.Block ?? parenthesized.ExpressionBody,
        SimpleLambdaExpressionSyntax simple
            => (SyntaxNode?)simple.Block ?? simple.ExpressionBody,
        _ => null,
    };

    /// <summary>
    /// Naming anchor (D9: Cs2jLambda_&lt;HostMember&gt;_&lt;k&gt;). Same spelling rules
    /// as the C32 analyzer's routine names; explicit-interface implementations
    /// keep their dots (member-part sanitization happens in the naming table).
    /// </summary>
    internal static string GetRoutineName(SyntaxNode routine, SemanticModel model, TypeDeclarationSyntax hostType)
    {
        if (routine is AccessorDeclarationSyntax accessor)
        {
            string containerName;
            if (accessor.Ancestors().OfType<IndexerDeclarationSyntax>().FirstOrDefault() != null)
                containerName = "Indexer";
            else
            {
                containerName = accessor.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
                    ?? accessor.Ancestors().OfType<EventDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
                    ?? "Accessor";
            }

            return $"{containerName}_{accessor.Keyword.Text}";
        }

        var declared = routine is BaseMethodDeclarationSyntax bm ? model.GetDeclaredSymbol(bm) : null;
        return declared?.Name ?? routine switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            DestructorDeclarationSyntax d => d.Identifier.Text,
            _ => hostType.Identifier.Text,
        };
    }
}
