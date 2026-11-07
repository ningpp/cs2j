namespace CSharpToJava.Core.GenericConstraint;

/// <summary>
/// Options for the <c>generic-constraint</c> (R2/CN) preprocessing pass.
/// Every switch maps 1:1 onto a CN rule family; the pass never re-implements
/// the shapes the upstream <c>generic</c> pass already removed (CN-01/03/07),
/// it only audits their residual occurrences.
/// </summary>
public sealed record GenericConstraintOptions
{
    /// <summary>CN-04: explicitize <c>T?</c> to <c>Nullable&lt;T&gt;</c> when T has a struct constraint.</summary>
    public bool RewriteNullableValueType { get; init; } = true;

    /// <summary>CN-09: <c>Enum.Parse/GetValues/GetNames&lt;T&gt;</c> to the non-generic overload plus <c>typeof(T)</c>.</summary>
    public bool RewriteEnumStaticCalls { get; init; } = true;

    /// <summary>CN-10: <c>Delegate.Combine/Remove&lt;T&gt;</c> to the non-generic call plus a <c>(T)</c> cast.</summary>
    public bool RewriteDelegateCombineRemove { get; init; } = true;

    /// <summary>CN-08/CN-11: report multi-dimensional type-parameter arrays and <c>where T : Enum</c> as DeclaredUnsupported.</summary>
    public bool ReportDeclaredUnsupported { get; init; } = true;

    /// <summary>CN-12: report an unexpressible constraint that has no dependent site anywhere in the tree.</summary>
    public bool ReportUnexpressibleConstraints { get; init; } = true;

    /// <summary>Emit Info-level diagnostics (currently none are produced on the normal path).</summary>
    public bool ReportInfo { get; init; }
}
