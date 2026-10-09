namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>How token sites inside quoted expression-tree lambdas are reported (GD-EXPRTREE).</summary>
public enum GenericDeepenExpressionTreePolicy
{
    /// <summary>Never rewrite tree nodes; emit CS2J1110 Info (Warning under strict).</summary>
    Skip,

    /// <summary>Never rewrite tree nodes; emit CS2J1110 Warning (Error under strict).</summary>
    Error,
}

/// <summary>Options for the generic-deepen (C02) preprocessing pass.</summary>
public sealed record GenericDeepenOptions
{
    /// <summary>GD-ANCHOR: insert token anchor statements at method entries.</summary>
    public bool RewriteTypeOfAnchors { get; init; } = true;

    /// <summary>GD-ISAS-B: lower type-parameter declaration patterns into statement form.</summary>
    public bool RewriteIsAsPatterns { get; init; } = true;

    /// <summary>GD-CATCH: lower catch(T) into a filtered catch.</summary>
    public bool RewriteCatchTypeParam { get; init; } = true;

    /// <summary>GD-STATIC: hoist class type parameters into static methods.</summary>
    public bool RewriteStaticMembers { get; init; } = true;

    /// <summary>GD-FIELD: replace static generic fields with holder accessors.</summary>
    public bool RewriteStaticFieldHolders { get; init; } = true;

    /// <summary>GD-NEST: propagate token needs across forwarding edges, override families and nested closures.</summary>
    public bool PropagateNestedTokens { get; init; } = true;

    /// <summary>Run the post-rewrite residual scan (strict turns findings into errors).</summary>
    public bool RunResidualScan { get; init; } = true;

    /// <summary>Residual findings and downgradeable warnings become errors.</summary>
    public bool Strict { get; init; }

    /// <summary>Emit Info-level diagnostics.</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>GD-EXPRTREE: reporting policy for token sites inside expression-tree lambdas.</summary>
    public GenericDeepenExpressionTreePolicy ExpressionTreePolicy { get; init; } =
        GenericDeepenExpressionTreePolicy.Skip;

    /// <summary>Optional skip/force overrides JSON path (spec §6; same shape as the closed-metadata gate).</summary>
    public string? OverridesPath { get; init; }
}
