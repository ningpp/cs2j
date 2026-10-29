using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Utilities;

/// <summary>
/// C# static members of a generic type may use the type parameters of that
/// type (e.g. <c>static Set&lt;T&gt; Add(Set&lt;T&gt; a, Set&lt;T&gt; b)</c> on
/// <c>class Set&lt;T&gt;</c>). Java forbids referencing a class type parameter
/// from a static context, so every enclosing type parameter the member actually
/// references — in the signature or the body — must be re-declared as a member
/// (method) type parameter.
/// </summary>
internal static class StaticMemberTypeParameterHelper
{
    /// <summary>
    /// Promotes the enclosing generic type parameters referenced by a static
    /// member declaration to method-level type parameters. Non-referenced
    /// parameters are deliberately left out (matching the C# member's shape).
    /// </summary>
    public static void PromoteReferencedEnclosingTypeParameters(
        JavaMethodDeclaration method,
        SyntaxNode declaration,
        bool isStatic,
        ISymbol? memberSymbol,
        ConversionContext context)
    {
        if (!isStatic)
            return;

        memberSymbol ??= context.GetDeclaredSymbol(declaration);
        if (memberSymbol == null)
            return;

        var enclosingTypeParameters = CollectEnclosingTypeParameters(memberSymbol);
        if (enclosingTypeParameters.Count == 0)
            return;

        var methodOwnNames = method.TypeParameters
            .Select(tp => tp.Name)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = enclosingTypeParameters
            .Where(tp => !methodOwnNames.Contains(tp.Name))
            .ToList();
        if (candidates.Count == 0)
            return;

        var referenced = CollectReferencedTypeParameters(declaration, candidates, context);
        if (referenced.Count == 0)
            return;

        // A promoted parameter's bound may mention another enclosing type
        // parameter (e.g. <T, U extends Comparable<T>>): Java requires that
        // parameter to be declared too.
        ExpandBoundDependencies(referenced, candidates, context);

        var promoted = candidates
            .Where(tp => referenced.Any(r => SymbolEqualityComparer.Default.Equals(r, tp)))
            .Select(tp => ResolveJavaTypeParameter(tp, context))
            .ToList();
        for (var i = promoted.Count - 1; i >= 0; i--)
            method.TypeParameters.Insert(0, promoted[i]);
    }

    /// <summary>
    /// Type parameters of the containing type and all of its enclosing types,
    /// outermost first.
    /// </summary>
    private static List<ITypeParameterSymbol> CollectEnclosingTypeParameters(ISymbol memberSymbol)
    {
        var declaringTypes = new List<INamedTypeSymbol>();
        for (var type = memberSymbol.ContainingType; type != null; type = type.ContainingType)
            declaringTypes.Add(type);
        declaringTypes.Reverse();

        var result = new List<ITypeParameterSymbol>();
        foreach (var type in declaringTypes)
            foreach (var typeParameter in type.TypeParameters)
            {
                if (typeParameter.DeclaringMethod != null)
                    continue;
                if (result.Any(existing => SymbolEqualityComparer.Default.Equals(existing, typeParameter)))
                    continue;
                result.Add(typeParameter);
            }

        return result;
    }

    /// <summary>
    /// Finds the enclosing type parameters the member declaration references.
    /// References are confirmed through the semantic model so a local variable
    /// or a method-level type parameter that happens to share the class type
    /// parameter's name is not misclassified.
    /// </summary>
    private static List<ITypeParameterSymbol> CollectReferencedTypeParameters(
        SyntaxNode declaration,
        List<ITypeParameterSymbol> candidates,
        ConversionContext context)
    {
        var byName = new Dictionary<string, ITypeParameterSymbol>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
            byName[candidate.Name] = candidate;

        var referenced = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        foreach (var identifier in declaration.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (!byName.TryGetValue(identifier.Identifier.ValueText, out var candidate))
                continue;
            if (context.GetSymbolInfo(identifier).Symbol is ITypeParameterSymbol found
                && SymbolEqualityComparer.Default.Equals(found, candidate))
            {
                referenced.Add(candidate);
            }
        }

        return candidates.Where(referenced.Contains).ToList();
    }

    /// <summary>Adds enclosing type parameters referenced by an already selected parameter's bounds.</summary>
    private static void ExpandBoundDependencies(
        List<ITypeParameterSymbol> referenced,
        List<ITypeParameterSymbol> candidates,
        ConversionContext context)
    {
        for (var i = 0; i < referenced.Count; i++)
        {
            var bounds = ResolveJavaTypeParameter(referenced[i], context).Bounds;
            foreach (var bound in bounds)
                foreach (var candidate in candidates)
                {
                    if (referenced.Any(r => SymbolEqualityComparer.Default.Equals(r, candidate)))
                        continue;
                    if (Regex.IsMatch(bound, $@"\b{Regex.Escape(candidate.Name)}\b"))
                        referenced.Add(candidate);
                }
        }
    }

    /// <summary>
    /// Preferred shape is the class-level Java type parameter (it already
    /// carries mapped bounds); fall back to one built from the Roslyn symbol.
    /// </summary>
    private static JavaTypeParameter ResolveJavaTypeParameter(
        ITypeParameterSymbol typeParameter,
        ConversionContext context)
    {
        var declared = context.CurrentType?.TypeParameters
            .FirstOrDefault(tp => tp.Name == typeParameter.Name);
        if (declared != null)
            return declared;

        var javaTypeParameter = new JavaTypeParameter(typeParameter.Name);
        foreach (var constraint in typeParameter.ConstraintTypes)
        {
            var bound = context.MapType(constraint);
            if (!string.IsNullOrEmpty(bound) && bound != "Object")
                javaTypeParameter.Bounds.Add(bound);
        }

        return javaTypeParameter;
    }
}
