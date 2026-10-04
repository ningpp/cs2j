namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Diagnostic codes used by C49 M1 extension-block materialization. All
/// diagnostics reuse the extension-pass <c>ExtensionDiagnostic</c> record
/// (rule <c>Ext14</c>); only codes M1 actually emits are declared here.
/// </summary>
public static class ExtensionBlockCodes
{
    /// <summary>
    /// EMD-PROP gate: block properties require the member-desugar consumption
    /// chain to be enabled downstream; when the chain is unavailable the
    /// block materialization is refused (no unconsumable output is produced).
    /// </summary>
    public const string PropertyChainGate = "CS2J1208";

    /// <summary>
    /// Unsupported extension-block construct: an indexer inside a block (the
    /// Roslyn 5.0 form always compiles with CS9282 — no legal materialization
    /// target) or another block member kind M1 does not materialize.
    /// </summary>
    public const string Unsupported = "CS2J1209";

    /// <summary>
    /// Startup parse self-check failed: the minimal extension-block fragment
    /// does not parse under the current Roslyn (block support missing); the
    /// pass cannot run and blocks are left unchanged.
    /// </summary>
    public const string ParseProbeFailed = "CS2J1213";
}

/// <summary>Message templates for C49 M1 diagnostics.</summary>
internal static class ExtensionBlockMessages
{
    public const string PropertyChainGate =
        "C# 14 extension block materialization of extension properties (EMD-PROP) requires the "
        + "member-desugar consumption chain, which is not enabled in this run; the extension block "
        + "is left unchanged.";

    public const string BlockIndexer =
        "An indexer inside a C# 14 'extension' block has no legal classic form in the current language "
        + "(Roslyn reports CS9282); C49 cannot materialize it. The extension block is left unchanged.";

    public static string UnsupportedMember(string kind)
        => $"C# 14 'extension' block member kind '{kind}' is not supported by C49 M1 (only methods "
           + "and properties are materialized); the extension block is left unchanged.";

    public const string ParseProbeFailed =
        "C49 startup parse self-check failed: the minimal C# 14 extension-block fragment is not "
        + "accepted by the bound Roslyn build; extension blocks are left unchanged.";

    public static string ConditionalDeferred(bool isProperty)
        => (isProperty
            ? "Null-conditional extension property access"
            : "Null-conditional extension method call")
           + " at this position cannot be safely single-evaluation lowered (no statement/arrow/lambda "
           + "anchor or an unsupported chain shape); the sugar is retained pending a later milestone.";

    public const string ExpressionTreeSiteSkipped =
        "C# 14 extension block member call site inside an Expression<TDelegate> tree is skipped in C49 M1; "
        + "the sugar is retained.";
}
