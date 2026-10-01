using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// CS2J0570–CS2J0589 diagnostic codes for the C36 collection-expression rule
/// (design §6). Append-only range owned by this rule; string codes (not enum)
/// so future family rules cannot accidentally share members.
/// </summary>
internal static class CollectionExprDiagnosticCodes
{
    /// <summary>Info: collection-expression site total (per-kind buckets live in statistics).</summary>
    public const string CS2J0570 = "CS2J0570";

    /// <summary>Error(strict)/Warning: ConvertedType missing/error type/no target inference (mirrors CS9176).</summary>
    public const string CS2J0571 = "CS2J0571";

    /// <summary>Info: array target expanded (with/without spread counted separately).</summary>
    public const string CS2J0572 = "CS2J0572";

    /// <summary>Info: mutable concrete collection target expanded (zero-temp/statement counted separately).</summary>
    public const string CS2J0573 = "CS2J0573";

    /// <summary>Info: interface target expanded to the specification concrete type (dictionary split counted).</summary>
    public const string CS2J0574 = "CS2J0574";

    /// <summary>Info: spread element expanded (AddRange/span-foreach/enumerable-foreach counted separately).</summary>
    public const string CS2J0575 = "CS2J0575";

    /// <summary>Error(strict)/Warning: spread source not enumerable or undecidable.</summary>
    public const string CS2J0576 = "CS2J0576";

    /// <summary>Info: empty collection [] expanded (per target kind).</summary>
    public const string CS2J0577 = "CS2J0577";

    /// <summary>Info: Span/ReadOnlySpan target built as a heap array (stack→heap note; C06 closes).</summary>
    public const string CS2J0578 = "CS2J0578";

    /// <summary>Info: nested collection expression expanded (depth counted).</summary>
    public const string CS2J0579 = "CS2J0579";

    /// <summary>Error(strict, v1)/Warning: [CollectionBuilder] target unsupported in v1; message names builder/method.</summary>
    public const string CS2J0580 = "CS2J0580";

    /// <summary>Error(strict)/Warning: table-out interface/abstract class/multidimensional array with no concrete form.</summary>
    public const string CS2J0581 = "CS2J0581";

    /// <summary>Error(strict)/Warning: ref struct element / Span stack form outside the v1 array boundary (see C06).</summary>
    public const string CS2J0582 = "CS2J0582";

    /// <summary>Error(strict)/Warning: spread-bearing collection at a non-statement-liftable position (field/attribute/default/query).</summary>
    public const string CS2J0583 = "CS2J0583";

    /// <summary>Info: CE-GUARD inventory summary (verbose, matches the CLI inventory section).</summary>
    public const string CS2J0584 = "CS2J0584";

    /// <summary>Error(declaration, strict)/Warning(call site): interface static abstract/virtual member.</summary>
    public const string CS2J0585 = "CS2J0585";

    /// <summary>Info(parameter)/Warning(residual shapes): scoped inventory; C01 owns parameter stripping.</summary>
    public const string CS2J0586 = "CS2J0586";

    /// <summary>Error(declaration)/Warning(application): generic attribute (Java annotations cannot be generic).</summary>
    public const string CS2J0587 = "CS2J0587";

    /// <summary>Info: collection expression inside an expression tree (baseline CS9175; theoretically unreachable).</summary>
    public const string CS2J0588 = "CS2J0588";

    /// <summary>Error(strict)/Warning: residual gate — leftover collection/spread node without a paired diagnostic.</summary>
    public const string CS2J0589 = "CS2J0589";
}

/// <summary>How a code's severity is decided (design §6 level column).</summary>
internal enum CollectionExprSeverityClass
{
    Info,

    /// <summary>Error under strict options, Warning otherwise (node preserved when non-strict).</summary>
    StrictError,

    /// <summary>Error at declaration sites under strict, Warning at use/call sites.</summary>
    DeclarationError,

    /// <summary>Info for one shape, Warning for another.</summary>
    ShapeMixed,
}

/// <summary>Default levels and message templates for the CS2J0570–CS2J0589 range.</summary>
internal static class CollectionExprDiagnostics
{
    public static CollectionExprSeverityClass SeverityClass(string code) => code switch
    {
        CollectionExprDiagnosticCodes.CS2J0570 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0571 => CollectionExprSeverityClass.StrictError,
        CollectionExprDiagnosticCodes.CS2J0572 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0573 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0574 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0575 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0576 => CollectionExprSeverityClass.StrictError,
        CollectionExprDiagnosticCodes.CS2J0577 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0578 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0579 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0580 => CollectionExprSeverityClass.StrictError,
        CollectionExprDiagnosticCodes.CS2J0581 => CollectionExprSeverityClass.StrictError,
        CollectionExprDiagnosticCodes.CS2J0582 => CollectionExprSeverityClass.StrictError,
        CollectionExprDiagnosticCodes.CS2J0583 => CollectionExprSeverityClass.StrictError,
        CollectionExprDiagnosticCodes.CS2J0584 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0585 => CollectionExprSeverityClass.DeclarationError,
        CollectionExprDiagnosticCodes.CS2J0586 => CollectionExprSeverityClass.ShapeMixed,
        CollectionExprDiagnosticCodes.CS2J0587 => CollectionExprSeverityClass.DeclarationError,
        CollectionExprDiagnosticCodes.CS2J0588 => CollectionExprSeverityClass.Info,
        CollectionExprDiagnosticCodes.CS2J0589 => CollectionExprSeverityClass.StrictError,
        _ => CollectionExprSeverityClass.Info,
    };

    /// <summary>
    /// Resolves the effective severity. <paramref name="atDeclaration"/>
    /// applies to 0585/0587 (declaration Error vs. call/application Warning).
    /// </summary>
    public static ExpressionDesugarSeverity ResolveSeverity(
        string code,
        bool strict,
        bool atDeclaration = true,
        bool parameterShape = true)
        => SeverityClass(code) switch
        {
            CollectionExprSeverityClass.Info => ExpressionDesugarSeverity.Info,
            CollectionExprSeverityClass.StrictError
                => strict ? ExpressionDesugarSeverity.Error : ExpressionDesugarSeverity.Warning,
            CollectionExprSeverityClass.DeclarationError
                => atDeclaration && strict
                    ? ExpressionDesugarSeverity.Error
                    : ExpressionDesugarSeverity.Warning,
            CollectionExprSeverityClass.ShapeMixed // CS2J0586: parameter Info, residual Warning
                => parameterShape ? ExpressionDesugarSeverity.Info : ExpressionDesugarSeverity.Warning,
            _ => ExpressionDesugarSeverity.Info,
        };

    // ---- Message templates ({0}.. placeholders, invariant wording) ----

    public const string MissingTargetMessage =
        "Collection expression target type is missing or is an error type; there is " +
        "no type to infer (baseline C# reports CS9176 here). Site left unchanged.";

    public const string BuilderV1Message =
        "Target type carries [CollectionBuilder(builderType: {0}, methodName: {1})]; v1 " +
        "cannot emit builder factory calls. Site left unchanged (M2 emits via IOperation).";

    /// <summary>
    /// M2 CS2J0580: the builder attribute was read but no unique factory
    /// could be resolved; {2} carries the concrete symbol-resolution reason.
    /// </summary>
    public const string BuilderUnresolvedMessage =
        "Target type carries [CollectionBuilder(builderType: {0}, methodName: {1})] " +
        "but the factory call cannot be emitted: {2}. Site left unchanged.";

    public const string TableOutMessage =
        "No known concrete construction form for collection target '{0}' (table-out " +
        "interface/abstract class/multidimensional array). Site left unchanged.";

    /// <summary>
    /// M2 CS2J0581 variant for interface targets the C# compiler itself
    /// rejects (CS9174): {0} target display, {1} baseline note.
    /// </summary>
    public const string TableOutBaselineMessage =
        "No known concrete construction form for collection target '{0}' ({1}). " +
        "Site left unchanged.";

    public const string SpreadNotEnumerableMessage =
        "Spread source is not enumerable or its static type cannot be determined; no " +
        "AddRange/foreach expansion is possible. Site left unchanged.";

    public const string RefStructElementMessage =
        "Collection element type '{0}' is a ref struct; a Span<{0}> target cannot " +
        "be built as a heap array (baseline CS9244/CS0611; stackalloc/Span " +
        "lowering is owned by C06). Site left unchanged.";

    public const string NotLiftableMessage =
        "A collection expression containing a spread sits in a position that cannot " +
        "be statement-lifted ({0}: field/property initializer, attribute argument, " +
        "default parameter value or query clause). Site left unchanged.";

    /// <summary>
    /// M2 CS2J0583 (CE-10/E): spread-bearing site on a <c>?:</c> branch or as
    /// the right operand of <c>??</c>. Prefix-hoisting would eagerly evaluate
    /// the not-taken branch; context lowering is tightened when the C24
    /// conditional guard / context lowering merges.
    /// </summary>
    public const string NotLiftableConditionalMessage =
        "A collection expression containing a spread is used as a " +
        "conditional-expression ('?:') branch or as the right operand of '??'; " +
        "statement pre-hoisting would evaluate the not-taken branch, so the site " +
        "is left unchanged (tightened in M2; context-sensitive lowering lands " +
        "with the C24 conditional-guard/context-lowering work).";

    public const string SpanArrayMessage =
        "Span/ReadOnlySpan target constructed as a heap array (stack-to-heap semantic " +
        "note; full stack form is owned by C06).";

    public const string StaticAbstractMessage =
        "Interface static abstract/virtual {0} '{1}'; Java has no equivalent feature " +
        "(future companion mode = companion-class static methods plus monomorphization).";

    public const string ScopedMessage =
        "'scoped' {0} site inventoried; parameter stripping is owned by the C01 pass; " +
        "residual local shapes await C06/C01 completion.";

    public const string GenericAttributeMessage =
        "Generic attribute '{0}'; Java annotations cannot carry type parameters " +
        "(long-term normalization belongs to C10).";

    public const string ExpressionTreeMessage =
        "Collection expression inside an expression tree (baseline CS9175 makes this " +
        "unreachable in compilable input). Site left unchanged.";

    public const string ResidualMessage =
        "{0} unaccounted collection-expression/spread node(s) remain after " +
        "expression-desugar processing.";
}
