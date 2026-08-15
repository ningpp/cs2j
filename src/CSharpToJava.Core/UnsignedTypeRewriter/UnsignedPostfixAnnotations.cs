using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// Carried from the semantic pass to the syntax-only postfix pass so that only
/// postfix increments/decrements on verified unsigned, side-effect-free targets
/// are hoisted/rewritten. The data stores the target SpecialType name.
/// </summary>
internal static class UnsignedPostfixAnnotations
{
    private const string Kind = "Cs2jUnsignedPostfix";

    public static SyntaxAnnotation For(SpecialType target)
        => new(Kind, target.ToString());

    public static bool Has(SyntaxNode node)
        => node.GetAnnotations(Kind).Any();

    public static SpecialType GetTarget(SyntaxNode node)
    {
        var annotation = node.GetAnnotations(Kind).FirstOrDefault();
        return annotation != null
            && Enum.TryParse<SpecialType>(annotation.Data, out var target)
            ? target
            : SpecialType.None;
    }
}
