using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Stub registrations for the merged preprocessor families from
/// prompts/preprocessor-catalog.md §3.3 and the preprocessor-foundation plan
/// (Step 8). Every entry is <see cref="PassStatus.Stub"/> and disabled by
/// default: disabled stubs never enter the execution plan (no directory, no
/// work), while explicitly enabling one fails loudly with CS2J9001 instead of
/// silently producing unprocessed output.
///
/// RunAfter anchors record the future position in the chain without changing
/// the current executed sequence (stubs never execute). Intermediate directory
/// names follow the <c>.cs2j-&lt;id&gt;-src</c> convention.
/// </summary>
internal static class PassStubCatalog
{
    /// <summary>Stable kebab-case ids of the placeholder passes registered in Step 8.</summary>
    public static class Ids
    {
        public const string LexicalNormalize = "lexical-normalize";
        public const string PartialMerge = "partial-merge";
        public const string ForeachSource = "foreach-source";
        public const string ClosedMetadata = "closed-metadata";
        // Ids.VarianceAudit flipped to Implemented in PassCatalog (C33 M1a:
        // audit-only skeleton, default-off); the id constant moved there.
        public const string VarianceAudit = "variance-audit";
        // Ids.UnsafeFfm flipped to Implemented in PassCatalog (C06 M0);
        // the id constant stays here by the iterator-lowering precedent (§3.4).
        public const string UnsafeFfm = "unsafe-ffm";
        public const string ClosureLift = "closure-lift";
        public const string IteratorLowering = "iterator-lowering";
        public const string NumericBoundary = "numeric-boundary";
        public const string ThreadStatic = "thread-static";
        public const string AttributeNormalize = "attribute-normalize";
        // Ids.ExtensionMethods flipped to Implemented in PassCatalog (C19 M0);
        // the id constant moved to PassIds (generic-deepen/member-desugar precedent).
        // The separate extension-members stub (C49) was consolidated into the
        // extension-methods pass (C19, C49); no id/descriptor remains.
        public const string EqualityConcat = "equality-concat";
        public const string LinqChannel = "linq-channel";
        // Ids.AsyncNormalize flipped to Implemented in PassCatalog (C18;
        // default-off / long-term opt-in, catalogItems narrowed to "C18");
        // the id constant stays here by the iterator-lowering / closure-lift
        // precedent (design §3.2/§3.4). C38 widens the same descriptor later.
        public const string AsyncNormalize = "async-normalize";
        public const string ExpressionDesugar = "expression-desugar";
        // Ids.PlatformIsolation flipped to Implemented in PassCatalog (C42 M1:
        // Report-mode audit only, default-off); the id constant stays here by
        // the iterator-lowering / unsafe-ffm precedent (§3.4).
        public const string PlatformIsolation = "platform-isolation";
        public const string ConcurrencyNormalize = "concurrency-normalize";
        public const string DynamicDesugar = "dynamic-desugar";
    }

    public static IReadOnlyList<PassDescriptor> Stubs { get; } = new List<PassDescriptor>
    {
        // Ids.ClosedMetadata flipped to Implemented in PassCatalog (C30 M2');
        // the descriptor lives there and no stub entry remains.
        // Ids.VarianceAudit flipped to Implemented in PassCatalog (C33 M1a);
        // the descriptor lives there and no stub entry remains.
        // Ids.LexicalNormalize flipped to Implemented in PassCatalog (C28);
        // the id constant stays here by the iterator-lowering precedent (§3.4).
        // Ids.PartialMerge flipped to Implemented in PassCatalog (C20);
        // the id constant stays here by the iterator-lowering precedent (§3.4).
        // Ids.ForeachSource flipped to Implemented in PassCatalog (C16);
        // the id constant stays here by the iterator-lowering precedent (§3.4).
        // Ids.UnsafeFfm flipped to Implemented in PassCatalog (C06 M0);
        // the descriptor lives there and no stub entry remains.
        // Ids.ClosureLift flipped to Implemented in PassCatalog (C32 M2);
        // the descriptor lives there and no stub entry remains.
        // Ids.NumericBoundary flipped to Implemented in PassCatalog (C15);
        // the id constant stays here by the iterator-lowering precedent (§3.4).
        new(
            Ids.ThreadStatic,
            "Thread-static rewrite",
            ".cs2j-thread-static-src",
            PassStage.Declarative,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Generic },
            description: "Move [ThreadStatic] and ThreadLocal hidden state into explicit C# source form.",
            catalogItems: "C09"),
        new(
            Ids.AttributeNormalize,
            "Attribute normalization",
            ".cs2j-attribute-normalize-src",
            PassStage.Declarative,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.GenericDeepen },
            description: "Normalize attributes including the Caller* info family and P/Invoke annotations.",
            catalogItems: "C10"),
        // Ids.ExtensionMethods flipped to Implemented in PassCatalog (C19 M0);
        // the descriptor (five anchors + make-readonly + iterator-lowering)
        // lives there and no stub entry remains.
        // The C49 extension-members stub was consolidated into that same
        // pass (catalogItems "C19, C49"); no stub entry remains.
        new(
            Ids.LinqChannel,
            "LINQ channelization",
            ".cs2j-linq-channel-src",
            PassStage.ControlFlow,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.ExtensionMethods, PassIds.AnonymousTypeLift },
            description: "Channel LINQ usage, including the IQueryable three-way routing acceptance gate.",
            catalogItems: "C26"),
        // Ids.AsyncNormalize flipped to Implemented in PassCatalog (C18;
        // default-off, catalogItems "C18", C38 async streams reserved in the
        // description); the descriptor lives there and no stub entry remains.
        // Ids.ExpressionDesugar flipped to Implemented in PassCatalog (C36 M0:
        // only the collection-expression analysis skeleton is registered,
        // default-off / no rewrite); the id constant stays here by the
        // iterator-lowering / equality-concat precedent (§3.4).
        // Ids.AnonymousTypeLift flipped to Implemented in PassCatalog (C21 M1-M4);
        // the descriptor lives there and no stub entry remains.
        // Ids.EqualityConcat flipped to Implemented in PassCatalog (C47).
        // Ids.PlatformIsolation flipped to Implemented in PassCatalog (C42);
        // the descriptor lives there and no stub entry remains.
        // Ids.ConcurrencyNormalize flipped to Implemented in PassCatalog (C29);
        // the descriptor lives there and no stub entry remains.
        new(
            Ids.DynamicDesugar,
            "Dynamic desugaring",
            ".cs2j-dynamic-desugar-src",
            PassStage.Abi,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.EliminateGoto },
            description: "Desugar dynamic (late-bound) calls into explicit dispatch forms.",
            catalogItems: "C25"),
    };
}
