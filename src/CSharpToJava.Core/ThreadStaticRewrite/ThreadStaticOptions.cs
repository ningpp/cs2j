namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>
/// How an inline initializer of a [ThreadStatic] field is materialized
/// (design §2/§2.1).
/// </summary>
public enum ThreadStaticInitializerPolicy
{
    /// <summary>
    /// CLR-faithful default (design §2 decision table / §2.1): the holder is
    /// constructed parameterless and the initializer is moved into the static
    /// constructor, so only the first thread to touch the type observes it.
    /// </summary>
    FirstThreadFaithful,

    /// <summary>
    /// Explicit opt-in: <c>new ThreadLocal&lt;T&gt;(() =&gt; INIT)</c> gives every
    /// thread the initial value; deviates from official [ThreadStatic] semantics.
    /// </summary>
    EveryThread,
}

/// <summary>How field accesses inside expression-tree lambdas (<c>Expression&lt;...&gt;</c>) are handled.</summary>
public enum ThreadStaticExpressionTreePolicy
{
    /// <summary>Skip the whole field plan with CS2J1205 Warning (Error under --strict).</summary>
    Skip,

    /// <summary>Skip the whole field plan and report CS2J1205 as an Error.</summary>
    Error,
}

/// <summary>Options for the thread-static (C09) preprocessing pass.</summary>
public sealed record ThreadStaticOptions
{
    /// <summary>
    /// Inline initializer materialization policy. Defaults to
    /// <see cref="ThreadStaticInitializerPolicy.FirstThreadFaithful"/> (official
    /// CLR semantics, design §2 decision table / §2.1); EveryThread is an
    /// explicit opt-in only.
    /// </summary>
    public ThreadStaticInitializerPolicy InitializerPolicy { get; init; } = ThreadStaticInitializerPolicy.FirstThreadFaithful;

    /// <summary>Warnings (skipped plans, residuals) become errors.</summary>
    public bool Strict { get; init; }

    /// <summary>Emit Info-level diagnostics (TS001/TS002).</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>Reserved for the C02 combination holder shape (M2+); v1 always skips generic containers.</summary>
    public bool RewriteGenericContainingTypes { get; init; }

    /// <summary>How to treat field accesses inside expression-tree lambdas.</summary>
    public ThreadStaticExpressionTreePolicy ExpressionTreePolicy { get; init; } = ThreadStaticExpressionTreePolicy.Skip;
}
