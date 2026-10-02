namespace CSharpToJava.Core.ClosureLift;

/// <summary>Generated storage shape for lifted captures (design D1).</summary>
public enum HolderForm
{
    /// <summary>Default and only acceptance shape: private sealed holder class with public fields.</summary>
    HolderClass,

    /// <summary>Non-default audit/regression comparator: single-element T[] arrays (design §4.4 tail).</summary>
    SingleElementArray,
}

/// <summary>
/// Options for the closure-lift pass. Four knobs only (design §4.7); no
/// per-syntax toggles. AuditOnly is the M1 mode; the CLI never exposes a
/// dedicated option — reachable via the manual sdk harness until M3.
/// </summary>
/// <param name="Strict">Residuals/skips are Errors (file kept verbatim) when true; downgraded to Warnings otherwise.</param>
/// <param name="AuditOnly">M1: analyze and emit CS2J3207 rows only; Changed is always false.</param>
/// <param name="HolderForm">HolderClass (default) or the non-default array comparator.</param>
/// <param name="Verbose">Emit per-file Info lines.</param>
public sealed record ClosureLiftOptions(
    bool Strict = true,
    bool AuditOnly = false,
    HolderForm HolderForm = HolderForm.HolderClass,
    bool Verbose = false);
