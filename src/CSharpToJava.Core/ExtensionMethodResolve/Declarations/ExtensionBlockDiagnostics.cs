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
    /// target) or another block member kind M2 does not materialize.
    /// </summary>
    public const string Unsupported = "CS2J1209";

    /// <summary>
    /// Probe-confirmed receiver shape that cannot be materialized (e.g. a
    /// pointer receiver CS0214/CS1103): the whole extension block is left
    /// unchanged.
    /// </summary>
    public const string UnsupportedReceiver = "CS2J1211";

    /// <summary>
    /// Post-rewrite recompile signature collision (CS0102/CS0111 attributed
    /// to an EMD plan): the plan group rolls back. Code declared by M2; the
    /// candidate-vs-baseline error diff is the detection path.
    /// </summary>
    public const string SignatureCollision = "CS2J1212";

    /// <summary>
    /// Startup parse self-check failed: the minimal extension-block fragment
    /// does not parse under the current Roslyn (block support missing); the
    /// pass cannot run and blocks are left unchanged.
    /// </summary>
    public const string ParseProbeFailed = "CS2J1213";

    /// <summary>
    /// M3: post-rewrite identity assertion failed (design §4.6): a rewritten
    /// block site does not rebind to the generated host member; the plan is
    /// reported/rolled back. Warning by default; strict upgrades to Error.
    /// </summary>
    public const string IdentityFailed = "CS2J1222";

    /// <summary>
    /// M3: an extension member declared in an external assembly (metadata
    /// only) has no in-compilation declaration C49 can materialize; the site
    /// is report-only. Same diagnostic family as the C19 metadata boundary.
    /// </summary>
    public const string ExternalMetadata = "CS2J1203";
}

/// <summary>Message templates for C49 M1 diagnostics.</summary>
internal static class ExtensionBlockMessages
{
    public const string PropertyChainGate =
        "C# 14 extension block materialization of extension properties/operators (EMD-PROP/EMD-OP) requires the "
        + "member-desugar consumption chain, which is not enabled in this run; the extension block "
        + "is left unchanged.";

    public const string BlockIndexer =
        "An indexer inside a C# 14 'extension' block has no legal classic form in the current language "
        + "(Roslyn reports CS9282); C49 cannot materialize it. The extension block is left unchanged.";

    public static string UnsupportedMember(string kind)
        => $"C# 14 'extension' block member kind '{kind}' is not supported by C49 M2 (only methods, "
           + "properties, static members and operators are materialized); the extension block is left unchanged.";

    public static string UnsupportedReceiver(string detail)
        => $"C# 14 'extension' block receiver cannot be materialized ({detail}); the extension block is left unchanged.";

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

    public const string OperatorCompoundDeferred =
        "Compound assignment to a C# 14 extension block operator at this position has a side-effecting "
        + "receiver outside a statement and cannot be single-evaluation lowered in C49 M2; the sugar is retained.";

    public const string StaticPropertyCompoundDeferred =
        "Compound assignment (or inc/dec) to a type-name qualified C# 14 extension block static property "
        + "is not rewritten in C49 M2; the sugar is retained.";

    // ---- M3 messages ----

    /// <summary>Extension operator site inside an Expression<TDelegate>: skipped with a paired diagnostic.</summary>
    public const string ExpressionTreeOperatorSkipped =
        "A C# 14 extension block operator site inside an Expression<TDelegate> tree is not rewritten in C49 M3 "
        + "(the site plan is skipped); the sugar is retained.";

    /// <summary>Null-conditional extension-property write/compound outside a statement: skipped.</summary>
    public static string ConditionalWriteDeferred(string shape)
        => $"Null-conditional extension property {shape} at this position is not a statement expression and cannot be "
           + "single-evaluation lowered in C49 M3; the sugar is retained.";

    /// <summary>Null-conditional ++/-- over extension property: CS1059 pairing.</summary>
    public const string ConditionalIncrementDeferred =
        "Null-conditional '++'/'--' over a C# 14 extension property is rejected by Roslyn (CS1059); C49 M3 leaves "
        + "the sugar with this paired diagnostic.";

    /// <summary>External-assembly extension member report (CS2J1203 family).</summary>
    public static string ExternalMetadataSite(string assembly, string member)
        => $"C# 14 extension member '{member}' is declared in external assembly '{assembly}' which has no in-compilation "
           + "extension block; C49 M3 reports the site and does not synthesize companion declarations.";
}
