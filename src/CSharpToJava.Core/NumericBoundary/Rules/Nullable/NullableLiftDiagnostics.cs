using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// Message builders for the C22 diagnostic codes CS2J5350-5359 (design §6).
/// Every message carries the <c>[NL-x]</c> rule tag. M0 defines the templates
/// but the rule is read-only: no diagnostic is emitted. M1 wires
/// CS2J5350/5351/5357, M1/M2 wire the CS2J5352-5356 error sites.
/// </summary>
internal static class NullableLiftDiagnostics
{
    public const string ReasonDynamic = "dynamic";
    public const string ReasonUserDefinedOperator = "user-defined-operator";
    public const string ReasonExpressionTree = "expression-tree";
    public const string ReasonComplexTarget = "complex-target";
    public const string ReasonUnliftableContext = "unliftable-context";
    public const string ReasonSpecialNumeric = "special-numeric";
    public const string ReasonResidual = "residual";

    public const string TempRoleLeft = "a";
    public const string TempRoleRight = "b";
    public const string TempRoleOld = "o";
    public const string TempRoleDefault = "d";

    /// <summary>CS2J5350 per-file lifted-site summary (verbose-only Info, §4.7 buckets).</summary>
    public static string SiteInfo(int totalSites, string breakdown) =>
        $"[NL-1] C22 nullable-lift explicitized {totalSites} lifted Nullable<T> site(s) ({breakdown}) " +
        $"({PreprocessDiagnosticCodes.NullableLiftSiteInfo})";

    /// <summary>CS2J5351 per-file temporary hoist summary (verbose-only Info, by role).</summary>
    public static string TempInfo(int totalTemps, string roles) =>
        $"[NL-2] C22 nullable-lift hoisted {totalTemps} temporary variable(s) ({roles}) " +
        $"({PreprocessDiagnosticCodes.NullableLiftTempInfo})";

    /// <summary>CS2J5352 dynamically bound lifted site (decision 22; force cannot clear).</summary>
    public static string Dynamic(string siteDescription) =>
        $"[NL-13] lifted Nullable<T> site '{siteDescription}' is dynamically bound and cannot be " +
        $"classified statically ({PreprocessDiagnosticCodes.NullableLiftDynamic})";

    /// <summary>CS2J5353 user-defined lifted operator (decision 21; waits for C14).</summary>
    public static string UserDefinedOperator(string siteDescription) =>
        $"[NL-13] lifted site '{siteDescription}' binds a user-defined operator on Nullable<T>; " +
        "the guarded operator-call target form waits for C14 member-desugar, so the site cannot be " +
        $"explicitized yet ({PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator})";

    /// <summary>CS2J5354 lifted site inside an expression-tree lambda (decision 22).</summary>
    public static string ExpressionTree(string siteDescription) =>
        $"[NL-13] lifted Nullable<T> site '{siteDescription}' is inside an expression-tree lambda; " +
        "rewriting it would change the expression tree shape " +
        $"({PreprocessDiagnosticCodes.NullableLiftExpressionTree})";

    /// <summary>CS2J5355 complex read-modify-write target not owned after M3 decision 17.</summary>
    public static string ComplexTarget(string siteDescription) =>
        $"[NL-10] lifted increment/compound target '{siteDescription}' is an event, conditional " +
        "(?./?[]), ref-returning, qualified-field or named/ref-argument read-modify-write site; " +
        "get/set properties, positional indexers and array elements were explicitized in M3, this " +
        $"remaining shape cannot be single-evaluation expanded yet ({PreprocessDiagnosticCodes.NullableLiftComplexTarget})";

    /// <summary>CS2J5355 unstable lifted operand in an un-hoistable context (M1; decision 2).</summary>
    public static string UnliftableContext(string siteDescription) =>
        $"[NL-2] lifted Nullable<T> site '{siteDescription}' has an unstable operand but sits in an " +
        "anchor-free or conditional/short-circuit/initializer context where a single-evaluation " +
        $"temporary cannot be hoisted ({PreprocessDiagnosticCodes.NullableLiftComplexTarget})";

    /// <summary>CS2J5356 C41 special-numeric nullable domain (decision 20).</summary>
    public static string SpecialNumeric(string siteDescription) =>
        $"[NL-13] lifted Nullable<T> site '{siteDescription}' operates on a special numeric kind " +
        $"(nint/Half/Int128/UInt128/…) owned by C41 ({PreprocessDiagnosticCodes.NullableLiftSpecialNumeric})";

    /// <summary>CS2J5357 NL-14 residual gate (strict → Error).</summary>
    public static string Residual(string residualSites) =>
        $"[NL-14] residual built-in lifted Nullable<T> site(s) survived in user-scope output: " +
        $"{residualSites}; strict mode fails ({PreprocessDiagnosticCodes.NullableLiftResidual})";
}
