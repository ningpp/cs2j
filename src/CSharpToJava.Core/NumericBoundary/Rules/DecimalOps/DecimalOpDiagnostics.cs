using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>Message builders for the C34 diagnostic codes (design §6).</summary>
internal static class DecimalOpDiagnostics
{
    public const string ReasonLifted = "lifted";
    public const string ReasonUserOperator = "user-operator";
    public const string ReasonExpressionTree = "expression-tree";
    public const string ReasonDynamic = "dynamic";
    public const string ReasonUnsupportedOperand = "unsupported-operand";
    public const string ReasonNoAnchor = "no-anchor";
    public const string ReasonSupportMissing = "unsigned-support-missing";

    public const string TempRoleReceiver = "receiver";
    public const string TempRoleIndexArg = "indexArg";
    public const string TempRoleValue = "value";

    public static string UnsupportedOperand(string siteDescription, string operandText) =>
        $"decimal operator site '{siteDescription}' has an operand of unsupported type " +
        $"'{operandText}': only decimal and signed/narrow integer operands are permitted ({PreprocessDiagnosticCodes.DecimalOpsUnsupportedOperand})";

    public static string Dynamic(string siteDescription) =>
        $"dynamically bound decimal operation '{siteDescription}' cannot be normalized " +
        $"({PreprocessDiagnosticCodes.DecimalOpsDynamic})";

    public static string ExpressionTree(string siteDescription) =>
        $"decimal operation '{siteDescription}' is inside an expression-tree lambda and cannot " +
        $"be rewritten ({PreprocessDiagnosticCodes.DecimalOpsExpressionTree})";

    public static string NoAnchor(string siteDescription) =>
        $"complex-target decimal compound assignment '{siteDescription}' has no safe hoist anchor " +
        $"(condition/lambda/initializer/query/attribute argument); rewrite deferred " +
        $"({PreprocessDiagnosticCodes.DecimalOpsNoAnchor})";

    public static string UnsignedSupportMissing(string method) =>
        $"emitted call to '{method}' but the Cs2jUnsigned.UnsignedMath support surface is not " +
        $"available in this compilation ({PreprocessDiagnosticCodes.DecimalOpsUnsignedSupportMissing})";

    public static string ResidualOperator(string siteDescription) =>
        $"built-in decimal operator '{siteDescription}' survived C34 normalization in user-scope " +
        $"output; strict mode fails ({PreprocessDiagnosticCodes.DecimalOpsResidualOperator})";
}
