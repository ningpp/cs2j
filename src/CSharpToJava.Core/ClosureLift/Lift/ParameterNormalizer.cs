using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// One normalized parameter slot (design §4: Lift/ParameterNormalizer, D12).
/// </summary>
/// <param name="MethodParameterName">Name the lifted named-method parameter receives.</param>
/// <param name="IsSynthesizedName">True when the lambda slot was unnamed or a
/// discard and the name was synthesized (`_cs2jArg_&lt;i&gt;`).</param>
internal sealed record ParameterSlot(string MethodParameterName, bool IsSynthesizedName);

/// <summary>
/// D12 parameter normalization shared by the M1 static path
/// (<see cref="StaticLambdaHandler"/>) and the M2/M3 instance path
/// (<see cref="LambdaMethodLifter"/>):
/// <list type="number">
/// <item>discard slots — unreferenced single <c>_</c> (C# 7 discard-shaped
/// parameter) and duplicated <c>_</c> (C# 9 discard parameters) — plus the
/// defensive empty-identifier slot receive deterministic synthesized names
/// <c>_cs2jArg_&lt;i&gt;</c>. A single <c>_</c> that the own body actually reads
/// is a real C# 7/8 parameter name and stays verbatim;</item>
/// <item>every slot is emitted with the delegate-derived explicit type
/// (<see cref="ResolvedParameter.TypeDisplay"/>), so implicitly-typed and
/// E05-synthesized (<c>p0</c>/<c>p1</c>) parameters gain types;</item>
/// <item>lambda parameter attribute lists migrate onto the named-method
/// parameter (ref/out/in/scoped modifiers never arrive here — they trip
/// CS2J4501 during signature resolution).</item>
/// </list>
/// Discard slots never have body references that bind to them, so migrating
/// the body verbatim needs no rename map (a reference to a real single-
/// underscore parameter keeps the name <c>_</c> end to end).
/// </summary>
internal static class ParameterNormalizer
{
    /// <summary>
    /// Freezes the named-method parameter slots for one lambda. Returns null
    /// only on a lambda/delegate arity mismatch (structural anomaly; callers
    /// keep the lambda residual).
    /// </summary>
    /// <param name="synthesizedCount">Number of slots receiving synthesized
    /// names (feeds DiscardParametersNormalized).</param>
    internal static IReadOnlyList<ParameterSlot>? Build(
        LambdaExpressionSyntax lambda,
        ResolvedLambdaSignature signature,
        out int synthesizedCount)
    {
        var parameters = GetLambdaParameters(lambda);
        synthesizedCount = 0;
        if (parameters.Count != signature.Parameters.Count)
            return null;

        var underscoreCount = 0;
        foreach (var parameter in parameters)
        {
            if (parameter.Identifier.ValueText == "_")
                underscoreCount += 1;
        }

        // A single underscore is a real parameter name iff the own body reads
        // it through this lambda's scope; duplicated underscores are always
        // C# 9 discard slots (a body reference would be a compile error).
        var underscoreIsReal = underscoreCount == 1 && OwnBodyReferencesUnderscore(lambda);

        var slots = new List<ParameterSlot>(parameters.Count);
        for (var i = 0; i < parameters.Count; i++)
        {
            var text = parameters[i].Identifier.ValueText;
            if (text.Length == 0 || (text == "_" && !underscoreIsReal))
            {
                slots.Add(new ParameterSlot(ClosureLiftNaming.GetSynthesizedArgName(i), true));
                synthesizedCount += 1;
            }
            else
            {
                slots.Add(new ParameterSlot(text, false));
            }
        }

        return slots;
    }

    /// <summary>
    /// Builds the named-method parameter list: migrated attribute lists,
    /// synthesized/original names and explicit delegate-derived types.
    /// </summary>
    internal static List<ParameterSyntax> BuildMethodParameters(
        LambdaExpressionSyntax lambda,
        ResolvedLambdaSignature signature,
        IReadOnlyList<ParameterSlot> slots)
    {
        var lambdaParameters = GetLambdaParameters(lambda);
        var result = new List<ParameterSyntax>(signature.Parameters.Count);
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            result.Add(SyntaxFactory.Parameter(
                    lambdaParameters[i].AttributeLists,
                    SyntaxFactory.TokenList(),
                    type: null,
                    SyntaxFactory.Identifier(slots[i].MethodParameterName),
                    @default: null)
                .WithType(SyntaxFactory.ParseTypeName(signature.Parameters[i].TypeDisplay)));
        }

        return result;
    }

    /// <summary>
    /// True when an <c>_</c> variable access inside the lambda binds to the
    /// lambda's own single-underscore parameter. A nested lambda declaring its
    /// own <c>_</c> parameter shadows the binding for references below it.
    /// </summary>
    private static bool OwnBodyReferencesUnderscore(LambdaExpressionSyntax owner)
    {
        foreach (var identifier in owner.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != "_" || !IsVariableAccessPosition(identifier))
                continue;

            var shadowed = false;
            for (var current = identifier.Parent;
                 current != null && current != owner;
                 current = current.Parent)
            {
                if (current is LambdaExpressionSyntax nested
                    && GetLambdaParameters(nested).Any(p => p.Identifier.ValueText == "_"))
                {
                    shadowed = true;
                    break;
                }
            }

            if (!shadowed)
                return true;
        }

        return false;
    }

    private static bool IsVariableAccessPosition(IdentifierNameSyntax node)
    {
        if (node.Parent is NameColonSyntax || CaptureClassification.IsInsideNameOf(node))
            return false;
        if (node.Parent is MemberAccessExpressionSyntax member && member.Name == node)
            return false;
        if (node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node)
            return false;
        if (node.Parent is NameEqualsSyntax)
            return false;

        return true;
    }

    internal static IReadOnlyList<ParameterSyntax> GetLambdaParameters(LambdaExpressionSyntax lambda)
        => lambda switch
        {
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters,
            SimpleLambdaExpressionSyntax simple => new SyntaxList<ParameterSyntax>(simple.Parameter),
            _ => Array.Empty<ParameterSyntax>(),
        };
}
