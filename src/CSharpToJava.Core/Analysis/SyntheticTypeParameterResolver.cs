using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// How a synthetic (lifted/synthesized) member can express one carried type
/// parameter (upgrade plan §6.4-R1, S-03): the plan never silently drops a
/// parameter — every entry is in exactly one of these three states.
/// </summary>
public enum SyntheticTypeParameterState
{
    /// <summary>A `where` clause exists in syntax and binds without error.</summary>
    Expressible,

    /// <summary>The parameter carries no constraints at all.</summary>
    Unconstrained,

    /// <summary>The parameter must be carried but its constraint surface cannot
    /// be reproduced safely (degraded model, unresolved constraint, missing
    /// clause). <see cref="SyntheticTypeParameter.Reason"/> is always set.</summary>
    NotExpressible,
}

/// <summary>Why a parameter entered the carried set (deterministic precedence:
/// Own &gt; Signature &gt; Body &gt; ConstraintClosure).</summary>
public enum SyntheticTypeParameterOrigin
{
    /// <summary>Declared by the lifted member itself (local function/method).</summary>
    Own,

    /// <summary>Referenced by the delegate-derived / explicit signature types.</summary>
    Signature,

    /// <summary>Referenced by an identifier in the body.</summary>
    Body,

    /// <summary>Pulled in transitively by a carried `where` clause (S-08).</summary>
    ConstraintClosure,
}

/// <summary>One carried type parameter of a synthetic member.</summary>
/// <param name="Name">Name as declared by its owner.</param>
/// <param name="Constraint">Original `where` clause syntax, or null when none.</param>
/// <param name="Origin">Why it is carried (see <see cref="SyntheticTypeParameterOrigin"/>).</param>
/// <param name="State">Explicit three-state expressibility (S-03).</param>
/// <param name="StableName">Deterministic rename when the declared name collides
/// with an earlier carried parameter; null when the original name is safe.</param>
/// <param name="Reason">Why the parameter is <see cref="SyntheticTypeParameterState.NotExpressible"/>.</param>
public sealed record SyntheticTypeParameter(
    string Name,
    TypeParameterConstraintClauseSyntax? Constraint,
    SyntheticTypeParameterOrigin Origin,
    SyntheticTypeParameterState State,
    string? StableName = null,
    string? Reason = null)
{
    /// <summary>The identifier the synthesized member must declare.</summary>
    public string RenderName => StableName ?? Name;
}

/// <summary>One plan-level diagnostic. Codes are stable so callers can map them
/// to preprocess diagnostics without parsing text.</summary>
/// <param name="Code">STP001 not expressible, STP002 shadowing rename,
/// STP003 degraded body reference.</param>
/// <param name="ParameterName">The parameter the row is about, when known.</param>
/// <param name="Message">Human-readable detail (never contains a stack trace).</param>
public sealed record SyntheticTypeParameterDiagnostic(
    string Code,
    string? ParameterName,
    string Message);

/// <summary>
/// Output of <see cref="SyntheticTypeParameterResolver"/> (upgrade plan
/// §6.4-R1 "五件套"): ① ordered carried parameters with constraint closure,
/// ② explicit site type arguments, ③ per-parameter three-state diagnostics,
/// ④ host-visible parameters are never redeclared, ⑤ shadowing is disambiguated
/// with a deterministic stable name.
/// </summary>
public sealed record SyntheticTypeParameterPlan(
    IReadOnlyList<SyntheticTypeParameter> Parameters,
    IReadOnlyList<string> TypeArguments,
    IReadOnlyList<SyntheticTypeParameterDiagnostic> Diagnostics)
{
    public static readonly SyntheticTypeParameterPlan Empty = new(
        Array.Empty<SyntheticTypeParameter>(),
        Array.Empty<string>(),
        Array.Empty<SyntheticTypeParameterDiagnostic>());
}

/// <summary>
/// Single semantic owner for "which type parameters must a lifted member
/// redeclare" (upgrade plan §6.4-R1, I12). Resolution is symbol-based
/// (<see cref="SymbolEqualityComparer.Default"/> on the containing symbol's
/// original definition) — never name guessing. A degraded model yields
/// <see cref="SyntheticTypeParameterState.NotExpressible"/> plus a diagnostic,
/// never an exception.
/// <para>
/// Candidate order is the host declaration order: the lifted member's own
/// parameters first, then enclosing method/local-function parameters from
/// outermost to innermost. Type-declaration parameters are already visible in
/// a nested member and are therefore never candidates (requirement ④).
/// </para>
/// </summary>
public static class SyntheticTypeParameterResolver
{
    public const string NotExpressibleCode = "STP001";
    public const string ShadowedCode = "STP002";
    public const string DegradedReferenceCode = "STP003";

    /// <summary>
    /// Resolves the carried type-parameter set for <paramref name="member"/>.
    /// </summary>
    /// <param name="model">Semantic model of the tree the nodes belong to.</param>
    /// <param name="member">The node being lifted/synthesized: a lambda, local
    /// function, method or another executable declaration.</param>
    /// <param name="ownTypeParameterList">Optional own type-parameter list;
    /// derived from <paramref name="member"/> when omitted.</param>
    /// <param name="ownConstraintClauses">Optional own `where` clauses; derived
    /// from <paramref name="member"/> when omitted.</param>
    /// <param name="body">Body to scan for references; defaults to
    /// <paramref name="member"/> itself (signature + body).</param>
    /// <param name="signatureTypes">Explicit signature types (delegate parameter/
    /// return types or a method signature). For a lambda with no explicit list,
    /// the converted delegate type is used.</param>
    public static SyntheticTypeParameterPlan Resolve(
        SemanticModel model,
        SyntaxNode member,
        TypeParameterListSyntax? ownTypeParameterList = null,
        SyntaxList<TypeParameterConstraintClauseSyntax> ownConstraintClauses = default,
        SyntaxNode? body = null,
        IReadOnlyList<ITypeSymbol?>? signatureTypes = null)
    {
        if (member is null)
            return SyntheticTypeParameterPlan.Empty;

        ownTypeParameterList ??= OwnTypeParameterList(member);
        if (ownConstraintClauses.Count == 0)
            ownConstraintClauses = OwnConstraintClauses(member);
        signatureTypes ??= DelegatedSignatureTypes(model, member) ?? Array.Empty<ITypeSymbol?>();

        var candidates = new List<Candidate>();
        var ordinal = 0;

        AddCandidates(
            ownTypeParameterList, ownConstraintClauses,
            model.GetDeclaredSymbol(member) as IMethodSymbol,
            isOwn: true);

        // Enclosing method-like declarations, outermost → innermost. Type
        // declarations are the boundary: their parameters stay in scope.
        var owners = new List<SyntaxNode>();
        for (var current = member.Parent;
             current is not null && current is not TypeDeclarationSyntax;
             current = current.Parent)
        {
            if (current is MethodDeclarationSyntax
                or LocalFunctionStatementSyntax
                or AccessorDeclarationSyntax
                or ConstructorDeclarationSyntax
                or DestructorDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax)
            {
                owners.Add(current);
            }
        }

        owners.Reverse();
        foreach (var owner in owners)
        {
            var (list, clauses) = owner switch
            {
                MethodDeclarationSyntax method => (method.TypeParameterList, method.ConstraintClauses),
                LocalFunctionStatementSyntax local => (local.TypeParameterList, local.ConstraintClauses),
                _ => (null, default),
            };

            AddCandidates(
                list,
                clauses,
                model.GetDeclaredSymbol(owner) as IMethodSymbol,
                isOwn: false);
        }

        if (candidates.Count == 0)
            return SyntheticTypeParameterPlan.Empty;

        var diagnostics = new List<SyntheticTypeParameterDiagnostic>();
        MarkOwnCandidates();

        // Signature references first (they decide method-group inference).
        var signatureVisible = new HashSet<Candidate>();
        foreach (var type in signatureTypes)
            CollectTypeReferences(type, SyntheticTypeParameterOrigin.Signature, signatureVisible);

        // Body references.
        var bodyRoot = body ?? member;
        foreach (var node in bodyRoot.DescendantNodesAndSelf())
        {
            if (node is not (IdentifierNameSyntax or GenericNameSyntax))
                continue;

            if (model.GetSymbolInfo(node).Symbol is ITypeParameterSymbol reference
                && Find(reference) is { } candidate)
            {
                Mark(candidate, SyntheticTypeParameterOrigin.Body);
                continue;
            }

            // Degraded fallback: the model cannot bind the identifier. Never
            // guess by name for a bound candidate; only an unbound candidate
            // with the same spelling is marked conservative (NotExpressible).
            if (node is IdentifierNameSyntax identifier)
            {
                var degraded = candidates.FirstOrDefault(c =>
                    c.Symbol is null && c.Name == identifier.Identifier.ValueText);
                if (degraded is not null && Mark(degraded, SyntheticTypeParameterOrigin.Body))
                {
                    diagnostics.Add(new SyntheticTypeParameterDiagnostic(
                        DegradedReferenceCode,
                        degraded.Name,
                        $"Body reference to type parameter '{degraded.Name}' could not be bound; " +
                        "the parameter is marked NotExpressible."));
                }
            }
        }

        // Constraint closure to a fixed point (S-08): `where T : IFoo<U>` must
        // carry U as well, recursively, in host declaration order.
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in candidates)
            {
                if (!candidate.Referenced || candidate.Clause is null)
                    continue;

                foreach (var node in candidate.Clause.DescendantNodes())
                {
                    if (node is not (IdentifierNameSyntax or GenericNameSyntax))
                        continue;

                    if (model.GetSymbolInfo(node).Symbol is ITypeParameterSymbol dependency
                        && Find(dependency) is { } dependent)
                    {
                        changed |= Mark(dependent, SyntheticTypeParameterOrigin.ConstraintClosure);
                    }
                }
            }
        }
        while (changed);

        var parameterNames = candidates.Select(static c => c.Name).ToHashSet(StringComparer.Ordinal);
        var hostVisible = HostVisibleTypeParameterNames(member);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new List<SyntheticTypeParameter>();

        foreach (var candidate in candidates.Where(static c => c.Referenced))
        {
            string? stableName = null;
            if (!used.Add(candidate.Name))
            {
                stableName = StableNameFor(candidate, used, parameterNames, hostVisible);
                used.Add(stableName);
                diagnostics.Add(new SyntheticTypeParameterDiagnostic(
                    ShadowedCode,
                    candidate.Name,
                    $"Type parameter '{candidate.Name}' shadows an earlier carried parameter; " +
                    $"renamed to '{stableName}'."));
            }

            var (state, reason) = EvaluateCore(model, candidate);
            if (state == SyntheticTypeParameterState.NotExpressible)
            {
                diagnostics.Add(new SyntheticTypeParameterDiagnostic(
                    NotExpressibleCode,
                    candidate.Name,
                    $"Type parameter '{candidate.Name}' cannot be safely expressed: {reason}"));
            }

            parameters.Add(new SyntheticTypeParameter(
                candidate.Name,
                candidate.Clause,
                candidate.Origin,
                state,
                stableName,
                reason));
        }

        var inferable = candidates
            .Where(c => c.Referenced && signatureVisible.Contains(c))
            .Select(static c => c.Name)
            .ToHashSet(StringComparer.Ordinal);
        var typeArguments = parameters.Count > 0
            && parameters.Any(p => !inferable.Contains(p.RenderName))
            ? parameters.Select(static p => p.RenderName).ToArray()
            : Array.Empty<string>();

        return new SyntheticTypeParameterPlan(parameters, typeArguments, diagnostics);

        void AddCandidates(
            TypeParameterListSyntax? list,
            SyntaxList<TypeParameterConstraintClauseSyntax> clauses,
            IMethodSymbol? ownerSymbol,
            bool isOwn)
        {
            if (list is null)
                return;

            for (var i = 0; i < list.Parameters.Count; i++)
            {
                var name = list.Parameters[i].Identifier.ValueText;
                if (name.Length == 0)
                    continue;

                var symbol = ownerSymbol is not null && i < ownerSymbol.TypeParameters.Length
                    ? ownerSymbol.TypeParameters[i]
                    : null;
                var clause = clauses.FirstOrDefault(c => c.Name.Identifier.ValueText == name);
                candidates.Add(new Candidate(ordinal++, name, symbol, clause, isOwn));
            }
        }

        void MarkOwnCandidates()
        {
            foreach (var candidate in candidates.Where(static c => c.IsOwn))
                Mark(candidate, SyntheticTypeParameterOrigin.Own);
        }

        bool Mark(Candidate candidate, SyntheticTypeParameterOrigin origin)
        {
            var added = !candidate.Referenced;
            candidate.Referenced = true;
            if (origin < candidate.Origin)
                candidate.Origin = origin;
            return added;
        }

        Candidate? Find(ITypeParameterSymbol symbol)
            => candidates.FirstOrDefault(c =>
                c.Symbol is not null && SymbolEqualityComparer.Default.Equals(c.Symbol, symbol));

        void CollectTypeReferences(
            ITypeSymbol? type,
            SyntheticTypeParameterOrigin origin,
            HashSet<Candidate> visible)
        {
            switch (type)
            {
                case ITypeParameterSymbol parameter:
                    if (Find(parameter) is { } candidate)
                    {
                        Mark(candidate, origin);
                        visible.Add(candidate);
                    }
                    break;
                case IArrayTypeSymbol array:
                    CollectTypeReferences(array.ElementType, origin, visible);
                    break;
                case IPointerTypeSymbol pointer:
                    CollectTypeReferences(pointer.PointedAtType, origin, visible);
                    break;
                case INamedTypeSymbol named:
                    foreach (var argument in named.TypeArguments)
                        CollectTypeReferences(argument, origin, visible);
                    break;
                case IFunctionPointerTypeSymbol functionPointer:
                    CollectTypeReferences(functionPointer.Signature.ReturnType, origin, visible);
                    foreach (var parameter in functionPointer.Signature.Parameters)
                        CollectTypeReferences(parameter.Type, origin, visible);
                    break;
            }
        }
    }

    private static (SyntheticTypeParameterState State, string? Reason) EvaluateCore(
        SemanticModel model,
        Candidate candidate)
    {
        if (candidate.Symbol is null)
            return (SyntheticTypeParameterState.NotExpressible,
                "type parameter symbol could not be bound (degraded model)");
        if (candidate.Symbol.TypeKind == TypeKind.Error)
            return (SyntheticTypeParameterState.NotExpressible,
                "type parameter symbol is an error type");

        if (!HasConstraints(candidate.Symbol))
            return (SyntheticTypeParameterState.Unconstrained, null);

        if (candidate.Clause is null)
            return (SyntheticTypeParameterState.NotExpressible,
                "the constraint clause is not available in syntax");

        foreach (var constraint in candidate.Clause.Constraints)
        {
            if (constraint is not TypeConstraintSyntax typeConstraint)
                continue;

            // `unmanaged` / `notnull` are keyword constraints, not type
            // references: GetTypeInfo returns null/error for them. The symbol
            // already records the constraint, so they must not be mistaken for
            // an unresolvable constraint type.
            if (IsKeywordConstraint(typeConstraint, candidate.Symbol))
                continue;

            var type = model.GetTypeInfo(typeConstraint.Type).Type;
            if (type is null || type.TypeKind == TypeKind.Error)
                return (SyntheticTypeParameterState.NotExpressible,
                    $"constraint '{typeConstraint.Type}' could not be bound");
        }

        if (candidate.Symbol.ConstraintTypes.Any(static t => t.TypeKind == TypeKind.Error))
            return (SyntheticTypeParameterState.NotExpressible,
                "a constraint type is an error type");

        return (SyntheticTypeParameterState.Expressible, null);
    }

    private static bool IsKeywordConstraint(
        TypeConstraintSyntax constraint, ITypeParameterSymbol symbol)
    {
        var text = constraint.Type.ToString();
        return (text == "unmanaged" && symbol.HasUnmanagedTypeConstraint)
            || (text == "notnull" && symbol.HasNotNullConstraint);
    }

    private static bool HasConstraints(ITypeParameterSymbol symbol)
        => symbol.ConstraintTypes.Length > 0
           || symbol.HasReferenceTypeConstraint
           || symbol.HasValueTypeConstraint
           || symbol.HasUnmanagedTypeConstraint
           || symbol.HasConstructorConstraint
           || symbol.HasNotNullConstraint
           || symbol.AllowsRefLikeType;

    private static string StableNameFor(
        Candidate candidate,
        HashSet<string> used,
        HashSet<string> parameterNames,
        HashSet<string> hostVisible)
    {
        var ordinal = candidate.Ordinal + 1;
        var stable = $"{candidate.Name}_{ordinal}";
        var suffix = 2;
        while (used.Contains(stable) || parameterNames.Contains(stable) || hostVisible.Contains(stable))
            stable = $"{candidate.Name}_{ordinal}_{suffix++}";

        return stable;
    }

    private static HashSet<string> HostVisibleTypeParameterNames(SyntaxNode member)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in member.AncestorsAndSelf().OfType<TypeDeclarationSyntax>())
        {
            if (type.TypeParameterList is not { } list)
                continue;
            foreach (var parameter in list.Parameters)
                names.Add(parameter.Identifier.ValueText);
        }

        return names;
    }

    private static TypeParameterListSyntax? OwnTypeParameterList(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax method => method.TypeParameterList,
        LocalFunctionStatementSyntax local => local.TypeParameterList,
        _ => null,
    };

    private static SyntaxList<TypeParameterConstraintClauseSyntax> OwnConstraintClauses(SyntaxNode member)
        => member switch
        {
            MethodDeclarationSyntax method => method.ConstraintClauses,
            LocalFunctionStatementSyntax local => local.ConstraintClauses,
            _ => default,
        };

    /// <summary>For a lambda, the delegate-derived signature types (parameter and
    /// return types of the converted delegate); null otherwise.</summary>
    private static IReadOnlyList<ITypeSymbol?>? DelegatedSignatureTypes(
        SemanticModel model,
        SyntaxNode member)
    {
        if (member is not LambdaExpressionSyntax)
            return null;

        if (model.GetTypeInfo(member).ConvertedType is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
            return null;

        var types = new List<ITypeSymbol?>(invoke.Parameters.Length + 1);
        foreach (var parameter in invoke.Parameters)
            types.Add(parameter.Type);
        types.Add(invoke.ReturnType);
        return types;
    }

    private sealed class Candidate
    {
        public Candidate(
            int ordinal,
            string name,
            ITypeParameterSymbol? symbol,
            TypeParameterConstraintClauseSyntax? clause,
            bool isOwn)
        {
            Ordinal = ordinal;
            Name = name;
            Symbol = symbol;
            Clause = clause;
            IsOwn = isOwn;
        }

        public int Ordinal { get; }
        public string Name { get; }
        public ITypeParameterSymbol? Symbol { get; }
        public TypeParameterConstraintClauseSyntax? Clause { get; }
        public bool IsOwn { get; }
        public bool Referenced { get; set; }
        public SyntheticTypeParameterOrigin Origin { get; set; } = SyntheticTypeParameterOrigin.ConstraintClosure;
    }
}
