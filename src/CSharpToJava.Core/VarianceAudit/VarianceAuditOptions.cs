namespace CSharpToJava.Core.VarianceAudit;

/// <summary>
/// Options for the C33 variance-audit preprocessing pass. The pass is read-only:
/// it never rewrites source and has no Mode (no rewrite mode exists, design D1/D11).
/// </summary>
public sealed class VarianceAuditOptions
{
    /// <summary>
    /// M2 gate: unsafe use sites become errors and abort the chain. Default false
    /// keeps the audit non-blocking (only Info/Warning are reported).
    /// </summary>
    public bool Strict;

    /// <summary>Emit Info-level diagnostics to the chain diagnostic stream (design D10).</summary>
    public bool ReportInfo = true;

    /// <summary>D9 mapped-mutability table switch (emergency kill; default on).</summary>
    public bool CheckMappedMutability = true;

    /// <summary>
    /// Report bit: the generic (E10) pass is present in the running chain. Injected
    /// by <c>CliPreprocessChain</c> from the resolved plan; false when the audit runs
    /// standalone over the raw tree (design D6/R4).
    /// </summary>
    public bool ChainHasGeneric;

    /// <summary>Report bit: the generic-deepen (C02) pass is present in the running chain.</summary>
    public bool ChainHasGenericDeepen;
}