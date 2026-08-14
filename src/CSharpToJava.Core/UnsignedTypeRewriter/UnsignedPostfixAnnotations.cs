using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// Carried from the semantic pass to the syntax-only postfix pass so that only
/// postfix increments/decrements on verified unsigned, side-effect-free targets
/// are hoisted/rewritten.
/// </summary>
internal static class UnsignedPostfixAnnotations
{
    public static readonly SyntaxAnnotation Postfix = new("Cs2jUnsignedPostfix");
}
