using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// One type parameter of the ENCLOSING method that the lifted static method
/// must redeclare. The synthesized method sits at type level: host-TYPE
/// parameters are still in scope inside a nested member, but an enclosing
/// METHOD's type parameters are not — without redeclaration the rendered
/// signature/body names an undeclared type (CS0246).
/// </summary>
/// <param name="Name">Parameter name as declared by the enclosing method.</param>
/// <param name="Constraint">The matching `where` clause, or null when unconstrained.</param>
internal sealed record LiftedTypeParameter(string Name, TypeParameterConstraintClauseSyntax? Constraint);

/// <summary>
/// The redeclaration set for one lifted static method plus the type-argument
/// list its delegate site needs. <see cref="TypeArguments"/> is EMPTY when
/// every carried parameter is visible in the delegate-derived signature (the
/// method group converts by inference); otherwise it lists every carried
/// parameter in declaration order, because method-group type inference
/// (CS0411) cannot supply parameters that only appear in a `where` clause.
/// </summary>
/// <param name="Parameters">Parameters the lifted method redeclares.</param>
/// <param name="TypeArguments">Explicit site type arguments; empty = infer.</param>
internal sealed record LiftedTypeParameterSet(
    IReadOnlyList<LiftedTypeParameter> Parameters,
    IReadOnlyList<string> TypeArguments)
{
    public static readonly LiftedTypeParameterSet Empty =
        new(Array.Empty<LiftedTypeParameter>(), Array.Empty<string>());
}

/// <summary>
/// Semantic (not textual) resolution of the enclosing-method type parameters a
/// lifted lambda actually references. A type parameter is carried only when
/// reference identity says it belongs to the enclosing method:
///   - the delegate-derived signature rendered into the lifted method
///     (parameter/return types, e.g. `Func&lt;TEdge, int&gt;` → `TEdge`);
///   - every identifier/type name the lambda body binds to such a parameter
///     (covers `typeof(T)`, casts, constructed types);
///   - transitively, parameters named by the carried `where` clauses, so a
///     `where T : IFoo&lt;U&gt;` stays compilable.
/// Declaration order is the enclosing method's order, and a lambda that
/// references no enclosing method type parameter keeps the exact previous
/// (non-generic) output.
/// </summary>
internal static class LiftedTypeParameterResolver
{
    public static LiftedTypeParameterSet Resolve(
        SemanticModel model,
        LambdaExpressionSyntax lambda,
        SyntaxNode routine)
    {
        if (model.GetDeclaredSymbol(routine) is not IMethodSymbol { TypeParameters.Length: > 0 } routineSymbol)
            return LiftedTypeParameterSet.Empty;

        var clauses = routine is MethodDeclarationSyntax method
            ? method.ConstraintClauses
            : default;

        var referenced = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        // Subset visible in the delegate-derived signature: those are the ones
        // method-group conversion can infer at the site.
        var signatureVisible = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        var delegateType = model.GetTypeInfo(lambda).ConvertedType as INamedTypeSymbol;
        if (delegateType?.DelegateInvokeMethod is { } invoke)
        {
            foreach (var parameter in invoke.Parameters)
                AddFromType(parameter.Type, signatureVisible);
            AddFromType(invoke.ReturnType, signatureVisible);
        }

        foreach (var node in lambda.DescendantNodesAndSelf())
        {
            if (node is not (IdentifierNameSyntax or GenericNameSyntax))
                continue;

            // GetSymbolInfo resolves both expression and type positions; member
            // names (`x.T`) and locals bind to their own symbols, so only true
            // references to the enclosing method's parameters are collected.
            if (model.GetSymbolInfo(node).Symbol is ITypeParameterSymbol reference && IsOwnedBy(reference))
                referenced.Add(reference);
        }

        // Constraint dependencies: a carried clause may name further method type
        // parameters (`where T : IFoo<U>`, `where T : U`) that then must be
        // declared too.
        bool changed;
        do
        {
            changed = false;
            foreach (var parameter in referenced.OfType<ITypeParameterSymbol>().ToList())
            {
                var clause = ClauseFor(parameter.Name);
                if (clause == null)
                    continue;

                foreach (var node in clause.DescendantNodes())
                {
                    if (node is not (IdentifierNameSyntax or GenericNameSyntax))
                        continue;
                    if (model.GetSymbolInfo(node).Symbol is ITypeParameterSymbol dependency
                        && IsOwnedBy(dependency)
                        && referenced.Add(dependency))
                    {
                        changed = true;
                    }
                }
            }
        }
        while (changed);

        var parameters = routineSymbol.TypeParameters
            .Where(referenced.Contains)
            .Select(parameter => new LiftedTypeParameter(parameter.Name, ClauseFor(parameter.Name)))
            .ToArray();

        var inferable = new HashSet<string>(
            signatureVisible.OfType<ITypeParameterSymbol>().Select(static p => p.Name),
            StringComparer.Ordinal);
        var typeArguments = parameters.Any(p => !inferable.Contains(p.Name))
            ? parameters.Select(static p => p.Name).ToArray()
            : Array.Empty<string>();

        return new LiftedTypeParameterSet(parameters, typeArguments);

        void AddFromType(ITypeSymbol? type, HashSet<ISymbol>? visibleInSignature = null)
        {
            switch (type)
            {
                case ITypeParameterSymbol parameter when IsOwnedBy(parameter):
                    referenced.Add(parameter);
                    visibleInSignature?.Add(parameter);
                    break;
                case IArrayTypeSymbol array:
                    AddFromType(array.ElementType, visibleInSignature);
                    break;
                case IPointerTypeSymbol pointer:
                    AddFromType(pointer.PointedAtType, visibleInSignature);
                    break;
                case INamedTypeSymbol named:
                    foreach (var argument in named.TypeArguments)
                        AddFromType(argument, visibleInSignature);
                    break;
            }
        }

        bool IsOwnedBy(ITypeParameterSymbol parameter)
            => SymbolEqualityComparer.Default.Equals(
                parameter.ContainingSymbol.OriginalDefinition, routineSymbol.OriginalDefinition);

        TypeParameterConstraintClauseSyntax? ClauseFor(string name)
            => clauses.FirstOrDefault(clause => clause.Name.Identifier.ValueText == name);
    }
}
