using CSharpToJava.Core.Analysis;
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
/// Closure-lift adapter over the shared
/// <see cref="SyntheticTypeParameterResolver"/> (upgrade plan §6.4-R1). The
/// unified resolver owns the semantic rules (symbol ownership, constraint
/// closure, three-state expressibility, shadowing); this adapter keeps the
/// closure-lift record shape so M1/M2/M3 consumers are unchanged.
/// <para>
/// A type parameter is carried only when reference identity says it belongs to
/// an enclosing routine: the delegate-derived signature, an identifier the
/// lambda body binds, or transitively a carried `where` clause
/// (`where T : IFoo&lt;U&gt;` carries U). Type-declaration parameters stay in
/// scope and are never redeclared.
/// </para>
/// </summary>
internal static class LiftedTypeParameterResolver
{
    public static LiftedTypeParameterSet Resolve(
        SemanticModel model,
        LambdaExpressionSyntax lambda,
        SyntaxNode routine)
    {
        if (model.GetDeclaredSymbol(routine) is not IMethodSymbol { TypeParameters.Length: > 0 })
            return LiftedTypeParameterSet.Empty;

        return ToSet(SyntheticTypeParameterResolver.Resolve(
            model,
            lambda,
            body: lambda));
    }

    /// <summary>
    /// M2/M3 (holder / display class) path: a class nested at type level sees
    /// the host TYPE's parameters, so only the routine's own method arity is
    /// redeclared. The routine itself is the reference root because its
    /// signature and every lifted lambda body live inside it.
    /// </summary>
    public static LiftedTypeParameterSet ResolveForRoutine(
        SemanticModel model,
        SyntaxNode routine)
    {
        if (model.GetDeclaredSymbol(routine) is not IMethodSymbol { TypeParameters.Length: > 0 })
            return LiftedTypeParameterSet.Empty;

        return ToSet(SyntheticTypeParameterResolver.Resolve(
            model,
            routine,
            body: routine));
    }

    private static LiftedTypeParameterSet ToSet(SyntheticTypeParameterPlan plan)
    {
        if (plan.Parameters.Count == 0)
            return LiftedTypeParameterSet.Empty;

        var parameters = plan.Parameters
            .Select(static parameter => new LiftedTypeParameter(
                parameter.RenderName,
                parameter.Constraint))
            .ToArray();

        return new LiftedTypeParameterSet(parameters, plan.TypeArguments.ToArray());
    }
}
