namespace CSharpToJava.Core.GenericPreprocess;

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

    /// <summary>Propagate token needs across forwarding call edges and override families.</summary>
    public bool PropagateNestedTokens { get; init; } = true;

    /// <summary>Run the post-rewrite residual scan (strict turns findings into errors).</summary>
    public bool RunResidualScan { get; init; } = true;

    /// <summary>Residual findings and warnings become errors.</summary>
    public bool Strict { get; init; }

    /// <summary>Emit Info-level diagnostics.</summary>
    public bool ReportInfo { get; init; } = true;
}
