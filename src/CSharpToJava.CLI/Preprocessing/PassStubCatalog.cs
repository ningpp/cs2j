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
        public const string VarianceAudit = "variance-audit";
        public const string UnsafeFfm = "unsafe-ffm";
        public const string ClosureLift = "closure-lift";
        public const string IteratorLowering = "iterator-lowering";
        public const string NumericBoundary = "numeric-boundary";
        public const string ThreadStatic = "thread-static";
        public const string AttributeNormalize = "attribute-normalize";
        public const string ExtensionMethods = "extension-methods";
        public const string ExtensionMembers = "extension-members";
        public const string EqualityConcat = "equality-concat";
        public const string LinqChannel = "linq-channel";
        public const string AsyncNormalize = "async-normalize";
        public const string ExpressionDesugar = "expression-desugar";
        public const string AnonymousTypeLift = "anonymous-type-lift";
        public const string PlatformIsolation = "platform-isolation";
        public const string ConcurrencyNormalize = "concurrency-normalize";
        public const string DynamicDesugar = "dynamic-desugar";
    }

    public static IReadOnlyList<PassDescriptor> Stubs { get; } = new List<PassDescriptor>
    {
        new(
            Ids.LexicalNormalize,
            "Lexical normalization",
            ".cs2j-lexical-normalize-src",
            PassStage.Lexical,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Conditional },
            needsSemanticModel: false,
            description: "Normalize file-scoped namespaces, global/static usings, extern aliases and raw/u8/verbatim literals; pure-syntax idempotent batching pilot.",
            catalogItems: "C28, C43 (observation)"),
        new(
            Ids.PartialMerge,
            "Partial type merge",
            ".cs2j-partial-merge-src",
            PassStage.Lexical,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Conditional },
            needsSemanticModel: false,
            description: "Merge partial type declarations across the tree before the semantic passes.",
            catalogItems: "C20"),
        new(
            Ids.ForeachSource,
            "Foreach source rewrite",
            ".cs2j-foreach-source-src",
            PassStage.ControlFlow,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.ForeachDeconstruction },
            description: "Normalize foreach iteration sources and ref/ref-struct enumerators.",
            catalogItems: "C16, C40"),
        new(
            Ids.ClosedMetadata,
            "Closed-type metadata",
            ".cs2j-closed-metadata-src",
            PassStage.Abi,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.GenericDeepen },
            description: "Consume closed java.lang.reflect.Type metadata keyed by generic-deepen tokens (P2; blocked on PatchClosed* archaeology).",
            catalogItems: "C30"),
        new(
            Ids.VarianceAudit,
            "Variance and constraint audit",
            ".cs2j-variance-audit-src",
            PassStage.Declarative,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Generic },
            description: "Audit out/in variance and struct/new() constraints; report usage sites needing rewrite (P3).",
            catalogItems: "C33"),
        new(
            Ids.UnsafeFfm,
            "Unsafe to FFM",
            ".cs2j-unsafe-ffm-src",
            PassStage.Abi,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.StructValueSemantics },
            description: "Lower pointers/stackalloc/fixed buffers/extern to index, Span and FFM forms.",
            catalogItems: "C06"),
        new(
            Ids.ClosureLift,
            "Closure/display class lift",
            ".cs2j-closure-lift-src",
            PassStage.StateMachine,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.RefOutIn },
            description: "Lift lambda/anonymous-method captures into deterministic display classes.",
            catalogItems: "C32, C45"),
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
        new(
            Ids.ExtensionMethods,
            "Extension method rewrite",
            ".cs2j-extension-methods-src",
            PassStage.Expression,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[]
            {
                PassIds.Delegate,
                PassIds.Tuple,
                PassIds.RefOutIn,
                PassIds.Unsigned,
                PassIds.ArrayMethod,
            },
            description: "Rewrite extension methods after every support-library-injecting pass.",
            catalogItems: "C19"),
        new(
            Ids.ExtensionMembers,
            "C# 14 extension member rewrite",
            ".cs2j-extension-members-src",
            PassStage.Declarative,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { Ids.ExtensionMethods },
            description: "Rewrite C# 14 extension blocks (extension properties/operators/static extensions) back to classic static classes with explicit static members.",
            catalogItems: "C49"),
        new(
            Ids.LinqChannel,
            "LINQ channelization",
            ".cs2j-linq-channel-src",
            PassStage.ControlFlow,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { Ids.ExtensionMethods, Ids.AnonymousTypeLift },
            description: "Channel LINQ usage, including the IQueryable three-way routing acceptance gate.",
            catalogItems: "C26"),
        new(
            Ids.AsyncNormalize,
            "Async normalization",
            ".cs2j-async-normalize-src",
            PassStage.StateMachine,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { Ids.ClosureLift },
            description: "Normalize async/await, async streams and async disposal into explicit state/bridge forms.",
            catalogItems: "C18, C38 (C44 subtopic)"),
        new(
            Ids.ExpressionDesugar,
            "Expression desugaring",
            ".cs2j-expression-desugar-src",
            PassStage.Expression,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.NullCoalesce },
            description: "Desugar expression/collection sugar into explicit statements with single-evaluation temporaries.",
            catalogItems: "C23, C24, C35, C36"),
        new(
            Ids.AnonymousTypeLift,
            "Anonymous type lift",
            ".cs2j-anonymous-type-lift-src",
            PassStage.Declarative,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Tuple },
            description: "Lift anonymous types into named generated types before the LINQ channel.",
            catalogItems: "C21"),
        // Ids.EqualityConcat flipped to Implemented in PassCatalog (C47).
        new(
            Ids.PlatformIsolation,
            "Platform boundary isolation",
            ".cs2j-platform-isolation-src",
            PassStage.Abi,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.EliminateGoto },
            description: "Isolate or reject external platform contracts (P/Invoke, extern) at the boundary.",
            catalogItems: "C42"),
        new(
            Ids.ConcurrencyNormalize,
            "Concurrency normalization",
            ".cs2j-concurrency-normalize-src",
            PassStage.Declarative,
            PassStatus.Stub,
            defaultEnabled: false,
            // No strong functional dependency; the late anchor keeps the naming
            // pass last and makes --list-passes placement deterministic.
            runAfterIds: new[] { PassIds.EliminateGoto },
            description: "Normalize concurrency primitives and synchronization constructs.",
            catalogItems: "C29"),
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
