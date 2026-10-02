using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// CS2J0501–CS2J0519 diagnostic codes for the C23 interpolation rule
/// (design §6). Append-only range; string constants (not enum) so future
/// family rules cannot accidentally share members.
/// </summary>
internal static class InterpolationDiagnosticCodes
{
    /// <summary>Info: interpolation site explicitly expanded to string.Concat (IE-CONCAT).</summary>
    public const string CS2J0501 = "CS2J0501";

    /// <summary>Info: interpolation site explicitly expanded to string.Format (IE-FORMAT).</summary>
    public const string CS2J0502 = "CS2J0502";

    /// <summary>Info: fully constant interpolation folded to a string literal (IE-FOLD).</summary>
    public const string CS2J0503 = "CS2J0503";

    /// <summary>Error(strict)/Warning: binding is a custom InterpolatedStringHandler (hard gate G0).</summary>
    public const string CS2J0510 = "CS2J0510";

    /// <summary>Warning: binding is FormattableString/IFormattable; node preserved.</summary>
    public const string CS2J0511 = "CS2J0511";

    /// <summary>Error(strict)/Warning: binding undecidable (error type/missing metadata); never guessed.</summary>
    public const string CS2J0512 = "CS2J0512";

    /// <summary>Error(strict)/Warning: ref struct (no provable non-virtual ToString override) or pointer hole.</summary>
    public const string CS2J0513 = "CS2J0513";

    /// <summary>Error(strict)/Warning: interpolation alignment component (',n') is not a compile-time constant.</summary>
    public const string CS2J0514 = "CS2J0514";

    /// <summary>Info: interpolation inside an expression tree is skipped per options (Q4).</summary>
    public const string CS2J0515 = "CS2J0515";

    /// <summary>Error(strict)/Warning: residual gate — leftover interpolation without a paired explanation.</summary>
    public const string CS2J0516 = "CS2J0516";
}

/// <summary>How a code's severity is decided (design §6 level column).</summary>
internal enum InterpolationSeverityClass
{
    Info,

    /// <summary>Always a warning (intentional, explained preservation).</summary>
    Warning,

    /// <summary>Error under strict options, Warning otherwise (node preserved when non-strict).</summary>
    StrictError,
}

/// <summary>Default levels and message templates for the CS2J0501–CS2J0519 range.</summary>
internal static class InterpolationDiagnostics
{
    public static InterpolationSeverityClass SeverityClass(string code) => code switch
    {
        InterpolationDiagnosticCodes.CS2J0501 => InterpolationSeverityClass.Info,
        InterpolationDiagnosticCodes.CS2J0502 => InterpolationSeverityClass.Info,
        InterpolationDiagnosticCodes.CS2J0503 => InterpolationSeverityClass.Info,
        InterpolationDiagnosticCodes.CS2J0510 => InterpolationSeverityClass.StrictError,
        InterpolationDiagnosticCodes.CS2J0511 => InterpolationSeverityClass.Warning,
        InterpolationDiagnosticCodes.CS2J0512 => InterpolationSeverityClass.StrictError,
        InterpolationDiagnosticCodes.CS2J0513 => InterpolationSeverityClass.StrictError,
        InterpolationDiagnosticCodes.CS2J0514 => InterpolationSeverityClass.StrictError,
        InterpolationDiagnosticCodes.CS2J0515 => InterpolationSeverityClass.Info,
        InterpolationDiagnosticCodes.CS2J0516 => InterpolationSeverityClass.StrictError,
        _ => InterpolationSeverityClass.Info,
    };

    /// <summary>Resolves the effective severity under strict/non-strict options.</summary>
    public static ExpressionDesugarSeverity ResolveSeverity(string code, bool strict)
        => SeverityClass(code) switch
        {
            InterpolationSeverityClass.Info => ExpressionDesugarSeverity.Info,
            InterpolationSeverityClass.Warning => ExpressionDesugarSeverity.Warning,
            InterpolationSeverityClass.StrictError
                => strict ? ExpressionDesugarSeverity.Error : ExpressionDesugarSeverity.Warning,
            _ => ExpressionDesugarSeverity.Info,
        };

    // ---- Message templates ({0}.. placeholders, invariant wording) ----

    public const string ConcatMessage =
        "Interpolated string site explicitly expanded to string.Concat (IE-CONCAT).";

    /// <summary>
    /// CS2J0502 (IE-FORMAT): the site was explicitly expanded to one
    /// string.Format(composite, h0, …) call — alignment/format clauses pass
    /// into the composite and non-whitelist IFormattable holes keep current-
    /// culture provider dispatch.
    /// </summary>
    public const string FormatMessage =
        "Interpolated string site explicitly expanded to string.Format " +
        "(IE-FORMAT): alignment/format clauses pass through into the composite " +
        "format and holes format with the current culture.";

    /// <summary>
    /// CS2J0514: the C# language requires an interpolation alignment
    /// component (<c>,n</c>) to be a compile-time constant (the baseline
    /// compiler rejects this shape); the defensive mirror leaves the site
    /// unchanged instead of guessing a width.
    /// </summary>
    public const string NonConstantAlignmentMessage =
        "Interpolation alignment component must be a compile-time constant " +
        "integer expression; the site is left unchanged rather than emitted " +
        "with a runtime alignment width.";

    public const string FoldMessage =
        "Fully constant interpolation folded to a string literal (IE-FOLD).";

    /// <summary>
    /// CS2J0510 (hard gate G0). {0}=handler type full name. The message must
    /// name the three observable differences and forbid Concat/Format
    /// downgrade (Q1: Error/Keep only).
    /// </summary>
    public const string CustomHandlerMessage =
        "Interpolated string binds to custom handler '{0}': its append semantics, " +
        "argument evaluation count (holes may evaluate zero or multiple times) " +
        "and conditional construction (constructor arguments evaluated ahead via " +
        "[InterpolatedStringHandlerArgument], guarded by the 'out bool append' " +
        "parameter) differ from the default string handler; downgrading to " +
        "string.Concat/string.Format would force argument evaluation and invent a " +
        "string result. Rewrite the call site explicitly, or set " +
        "HandlerPolicy=Keep to preserve it.";

    public const string FormattableStringMessage =
        "Interpolated string binds to System.FormattableString/IFormattable, an " +
        "explicit runtime shape (composite format, boxed arguments, " +
        "Invariant/CurrentCulture members); the site is preserved unchanged.";

    public const string UnknownBindingMessage =
        "Interpolated string binding cannot be classified (error type or missing " +
        "conversion metadata); the site is left unchanged rather than guessed.";

    public const string RefStructOrPointerMessage =
        "Interpolation hole of ref struct/pointer type '{0}' cannot materialize as " +
        "an object argument to string.Concat and the type declares no provably " +
        "non-virtual parameterless ToString override; the site is left unchanged.";

    public const string ExpressionTreeMessage =
        "Interpolated string inside an Expression<T> lambda is preserved " +
        "(expression-tree lowering deferred; rewriting it would change the tree a " +
        "query provider observes).";

    public const string ResidualMessage =
        "{0} unaccounted interpolated string node(s) remain after expression-desugar " +
        "processing without a paired skip diagnostic.";
}
