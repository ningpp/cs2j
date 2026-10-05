namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>
/// How an inline initializer of a [ThreadStatic] field is materialized.
/// M1 implements <see cref="EveryThread"/> only; <see cref="FirstThreadFaithful"/>
/// (cctor injection) is reserved for M2.
/// </summary>
public enum ThreadStaticInitializerPolicy
{
    /// <summary><c>new ThreadLocal&lt;T&gt;(() => INIT)</c>: every thread gets the initial value.</summary>
    EveryThread,

    /// <summary>M2: initialize only on the first thread (CLR-faithful), via injected cctor assignments.</summary>
    FirstThreadFaithful,
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
    /// <summary>Inline initializer materialization policy. M1 implements EveryThread only.</summary>
    public ThreadStaticInitializerPolicy InitializerPolicy { get; init; } = ThreadStaticInitializerPolicy.EveryThread;

    /// <summary>Warnings (skipped plans, residuals) become errors.</summary>
    public bool Strict { get; init; }

    /// <summary>Emit Info-level diagnostics (TS001/TS002).</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>Reserved for the C02 combination holder shape (M2+); v1 always skips generic containers.</summary>
    public bool RewriteGenericContainingTypes { get; init; }

    /// <summary>How to treat field accesses inside expression-tree lambdas.</summary>
    public ThreadStaticExpressionTreePolicy ExpressionTreePolicy { get; init; } = ThreadStaticExpressionTreePolicy.Skip;
}
