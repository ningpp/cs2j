using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Family options for the <c>expression-desugar</c> pass.
/// The family owns a fixed rule order
/// (Interpolations(C23) -&gt; Initializers(C24) -&gt; NullConditional(C35) -&gt;
/// CollectionExpr(C36)); each rule appends its own subsection. This object is
/// append-only: later rules add nullable/initialized subsection properties
/// without changing existing ones (design §3.3).
/// </summary>
public sealed class ExpressionDesugarOptions
{
    /// <summary>C36 collection-expression desugaring subsection.</summary>
    public CollectionExprOptions CollectionExpr { get; init; } = new();

    // Future append positions (C23 anchor / C24 / C35 land later):
    //   public InterpolationOptions? Interpolations { get; init; }   // C23
    //   public InitializerOptions?    Initializers  { get; init; }   // C24
    //   public NullConditionalOptions? NullConditional { get; init; }// C35

    /// <summary>Default options instance.</summary>
    public static ExpressionDesugarOptions Default { get; } = new();
}
