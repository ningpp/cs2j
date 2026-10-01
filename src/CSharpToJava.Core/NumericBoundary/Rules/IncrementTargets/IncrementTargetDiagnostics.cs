using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// Message builders for the C17 diagnostic codes (design §6). M0 defines the
/// templates but the classifier is read-only and no diagnostic is emitted;
/// M1/M2 wire them to concrete sites.
/// </summary>
internal static class IncrementTargetDiagnostics
{
    public const string ReasonNativeInt = "native-int";
    public const string ReasonLifted = "lifted";
    public const string ReasonDynamic = "dynamic";
    public const string ReasonExpressionTree = "expression-tree";
    public const string ReasonCustomOperator = "custom-operator";
    public const string ReasonNoAnchor = "no-anchor";
    public const string ReasonEvent = "event";
    public const string ReasonPointer = "pointer";
    public const string ReasonConditional = "conditional";

    public const string TempRoleReceiver = "receiver";
    public const string TempRoleIndexArg = "indexArg";
    public const string TempRoleOldValue = "oldValue";
    public const string TempRoleNewValue = "newValue";
    public const string TempRoleRefAlias = "refAlias";

    /// <summary>CS2J5330 per-file expansion summary (verbose-only Info).</summary>
    public static string SiteInfo(
        int totalSites, int property, int indexer, int enumArray,
        int enumSimple, int unsignedResidual, int postfixLifted, int unsignedMathCalls)
    {
        var parts = new List<string>
        {
            $"{property} property",
            $"{indexer} indexer",
            $"{enumArray} enum-array",
            $"{enumSimple} enum-residual",
            $"{unsignedResidual} unsigned-residual",
        };
        var message = $"C17 increment-targets expanded {totalSites} site(s) ({string.Join(", ", parts)}); " +
            $"{postfixLifted} postfix site(s) lifted";
        if (unsignedMathCalls > 0)
        {
            message += $"; {unsignedMathCalls} Cs2jUnsigned.UnsignedMath call(s)";
        }

        return message;
    }

    /// <summary>CS2J5331 per-file temporary summary (verbose-only Info).</summary>
    public static string TempInfo(IReadOnlyDictionary<string, int> tempsByRole)
    {
        var rendered = tempsByRole
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Value} {pair.Key}");
        return "C17 increment-targets hoisted "
            + $"{tempsByRole.Values.Sum()} temporary variable(s) ({string.Join(", ", rendered)})";
    }

    public static string IllegalTarget(string siteDescription, string pointerOrOther) =>
        $"increment/decrement target '{siteDescription}' is not a rewritable storage location " +
        $"({pointerOrOther}); events accept only +=/-= (C13) and pointer targets belong to C06 " +
        $"({PreprocessDiagnosticCodes.IncrementTargetIllegalTarget})";

    public static string NativeInt(string siteDescription) =>
        $"increment/decrement on a platform-width integer target '{siteDescription}' is not supported; " +
        $"the arithmetic core belongs to C41 ({PreprocessDiagnosticCodes.IncrementTargetNativeInt})";

    public static string DecimalExpanded(string siteDescription) =>
        $"decimal increment/decrement '{siteDescription}' was expanded to 'old ± 1m'; the Java-side " +
        $"method surface is owned by C34 ({PreprocessDiagnosticCodes.IncrementTargetDecimalInfo})";

    public static string NullableLifted(string siteDescription) =>
        $"increment/decrement '{siteDescription}' is a lifted nullable numeric/enum operation and " +
        $"belongs to C22 ({PreprocessDiagnosticCodes.IncrementTargetNullableLifted})";

    public static string Dynamic(string siteDescription) =>
        $"dynamically bound increment/decrement '{siteDescription}' cannot be normalized " +
        $"({PreprocessDiagnosticCodes.IncrementTargetDynamic})";

    public static string ExpressionTree(string siteDescription) =>
        $"increment/decrement '{siteDescription}' is inside an expression-tree lambda and rewriting " +
        $"would change the tree shape ({PreprocessDiagnosticCodes.IncrementTargetExpressionTree})";

    public static string CustomOperator(string siteDescription) =>
        $"increment/decrement '{siteDescription}' binds a user-defined op_Increment/op_Decrement; C14 " +
        "defers ++/-- sites to C17 and the desugared naming contract is not frozen, so the operator " +
        $"method cannot be invoked from source ({PreprocessDiagnosticCodes.IncrementTargetCustomOperator})";

    public static string NoAnchor(string siteDescription) =>
        $"value-consumed increment/decrement '{siteDescription}' has no safe single-execution hoist " +
        "anchor (condition/query/lambda/initializer/attribute/null-conditional); rewrite deferred " +
        $"({PreprocessDiagnosticCodes.IncrementTargetNoAnchor})";

    public static string RefReturn(string siteDescription) =>
        $"ref-returning increment target '{siteDescription}' is expanded via a ref local alias; the " +
        $"Java half depends on the C01 holder ({PreprocessDiagnosticCodes.IncrementTargetRefReturn})";

    public static string Residual(string siteDescription) =>
        $"owned increment/decrement node '{siteDescription}' survived C17 normalization in user-scope " +
        $"output; strict mode fails ({PreprocessDiagnosticCodes.IncrementTargetResidual})";
}
