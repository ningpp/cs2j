using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>Stable kebab-case pass identifiers shared by the CLI and the registry.</summary>
internal static class PassIds
{
    public const string Conditional = "conditional";
    public const string LocalFunctionLift = "local-function-lift";
    public const string NullCoalesce = "null-coalesce";
    public const string Enum = "enum";
    public const string EnumDeclaration = "enum-declaration";
    public const string Delegate = "delegate";
    public const string Tuple = "tuple";
    public const string AnonymousTypeLift = "anonymous-type-lift";
    public const string ForeachDeconstruction = "foreach-deconstruction";
    public const string PatternDesugar = "pattern-desugar";
    public const string StripDefault = "strip-default";
    public const string RefOutIn = "ref-out-in";
    public const string Unsigned = "unsigned";
    public const string Generic = "generic";
    public const string ThreadStatic = "thread-static";
    public const string GenericDeepen = "generic-deepen";
    public const string VarianceAudit = "variance-audit";
    public const string ClosedMetadata = "closed-metadata";
    public const string RecordReadOnly = "record-readonly";
    public const string ArrayMethod = "array-method";
    public const string IndexRange = "index-range";
    public const string MakeReadonly = "make-readonly";
    public const string AttributeNormalize = "attribute-normalize";
    public const string StructValueSemantics = "struct-value-semantics";
    public const string EliminateGoto = "eliminate-goto";
    public const string MemberDesugar = "member-desugar";
    public const string JavaKeywords = "java-keywords";
    public const string ExtensionMethods = "extension-methods";
}

/// <summary>
/// Descriptor table for the preprocess passes. Ordering is locked to the historical
/// ConvertProject chain via RunAfter anchors — the topological sort must produce
/// exactly that sequence, which Step 4 asserts in a regression test.
/// </summary>
internal static class PassCatalog
{
    public static IReadOnlyList<PassDescriptor> Implemented { get; } = new List<PassDescriptor>
    {
        new(
            PassIds.Conditional,
            "Conditional directive",
            ProjectConditionalDirectivePreprocessor.IntermediateDirectoryName,
            PassStage.Lexical,
            PassStatus.Implemented,
            defaultEnabled: true,
            needsSemanticModel: false,
            description: "Strip/adapt C# preprocessor directives (text pass with per-file symbol resolution)."),
        new(
            PassStubCatalog.Ids.LexicalNormalize,
            "Lexical normalization",
            ".cs2j-lexical-normalize-src",
            PassStage.Lexical,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Conditional },
            needsSemanticModel: false,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Strip #line directives, audit #error/extern-alias/global-using forms and optionally normalize file-scoped namespaces (--ln-normalize-file-scoped); C43 raw/u8/verbatim/\\e literal normalization is opt-in via --ln-normalize-literals (P0 rule-level default-off, C43 design §8 sdk profile gate).",
            catalogItems: "C28, C43"),
        new(
            PassStubCatalog.Ids.PartialMerge,
            "Partial type merge",
            ".cs2j-partial-merge-src",
            PassStage.Lexical,
            PassStatus.Implemented,
            // P0 rollback (2026-10-06): DEFAULT-OFF again. The design rules the
            // pass long-term opt-in until M3 (C#13 member-level partial pairing
            // collapse) and the sdk dual-equivalence profile land; v1 only keeps
            // member-level duplicates and the all-on policy flip (3147e093)
            // predated those gates. --enable-pass partial-merge opts in.
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Conditional, PassStubCatalog.Ids.LexicalNormalize },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Merge partial type declarations across the tree before the semantic passes.",
            catalogItems: "C20"),
        new(
            PassIds.LocalFunctionLift,
            "Local function lift",
            ProjectLocalFunctionLiftPreprocessor.IntermediateDirectoryName,
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Conditional, PassStubCatalog.Ids.PartialMerge },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Lift local functions to class-level private static methods."),
        new(
            PassIds.NullCoalesce,
            "Null coalesce",
            ProjectNullCoalescePreprocessor.IntermediateDirectoryName,
            PassStage.Expression,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.LocalFunctionLift },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Rewrite ?? / ??= expressions into Java-compatible forms."),
        new(
            PassStubCatalog.Ids.ExpressionDesugar,
            "Expression desugaring",
            ".cs2j-expression-desugar-src",
            PassStage.Expression,
            PassStatus.Implemented,
            defaultEnabled: true,
            // Physical placement right after null-coalesce plus declaration-order
            // tiebreak keeps this pass immediately after null-coalesce (C36 plan §2).
            runAfterIds: new[] { PassIds.NullCoalesce },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Desugar expression sugar into explicit statements; the C23 " +
                         "interpolation rule rewrites interpolated strings into string.Concat/string.Format " +
                         "(opt-in via --interpolation-expand), the C36 collection-expression rules " +
                         "rewrite collection expressions into explicit allocation/factory calls, " +
                         "and the C24 initializer rules desugar object/collection initializers into explicit statements; " +
                         "the C51 null-conditional assignment rule lowers assignment-terminal ?./?[] sites into guarded statements.",
            catalogItems: "C23, C24, C35, C36, C51"),
        new(
            PassIds.Enum,
            "Enum",
            ProjectEnumPreprocessor.IntermediateDirectoryName,
            PassStage.Expression,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.NullCoalesce },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Convert [Flags] enums and bitwise/HasFlag usage to prelude helpers."),
        new(
            PassIds.EnumDeclaration,
            "Enum declaration rewrite",
            ".cs2j-enum-declaration-src",
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Enum },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Normalize [Flags]/non-int/explicit-value enums into enum-like sealed classes and rewrite every use site tree-wide.",
            catalogItems: "C05"),
        new(
            PassIds.Delegate,
            "Delegate",
            ProjectDelegatePreprocessor.IntermediateDirectoryName,
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Enum },
            supportLibrary: PassSupportLibrary.Cs2jPrelude,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Rewrite delegate declarations and invocations; inject the cs2j prelude."),
        new(
            PassIds.Tuple,
            "Tuple to struct",
            ProjectTupleToStructPreprocessor.IntermediateDirectoryName,
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Delegate },
            supportLibrary: PassSupportLibrary.Cs2jTuples,
            description: "Distribute value-tuple usage into generated struct files."),
        new(
            PassIds.AnonymousTypeLift,
            "Anonymous type lift",
            ".cs2j-anonymous-type-lift-src",
            PassStage.Declarative,
            PassStatus.Implemented,
            // C21 M1-M4 flipped together (adapter + registry in one delivery).
            // All-preprocessors-default-on policy: the §8 observation period is
            // over and the pass ships default-on; --disable-pass opts out.
            // Declared immediately after tuple so the Kahn declaration-order
            // tiebreak locks the slot tuple → anonymous-type-lift →
            // foreach-deconstruction (both only anchor tuple; design §3.2).
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Tuple },
            supportLibrary: PassSupportLibrary.None,
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Lift anonymous object creations into named internal sealed immutable classes before the LINQ channel.",
            catalogItems: "C21"),
        new(
            PassIds.ForeachDeconstruction,
            "Foreach deconstruction",
            ProjectForeachDeconstructionPreprocessor.IntermediateDirectoryName,
            PassStage.Expression,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Tuple },
            description: "Desugar foreach deconstruction into indexer loops."),
        new(
            PassStubCatalog.Ids.ForeachSource,
            "Foreach source rewrite",
            ".cs2j-foreach-source-src",
            PassStage.ControlFlow,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.ForeachDeconstruction },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Lower foreach over string (ToCharArray), residual deconstruction forms, enumerator adapters and ref foreach.",
            catalogItems: "C16, C40"),
        new(
            PassIds.PatternDesugar,
            "Pattern desugaring",
            ".cs2j-pattern-desugar-src",
            PassStage.ControlFlow,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.NullCoalesce, PassIds.Tuple, PassIds.ForeachDeconstruction },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Reduce pattern switches, non-integer governing switches, catch filters and type-parameter catches into if-chains and boolean expressions.",
            catalogItems: "C03, C37"),
        new(
            PassIds.StripDefault,
            "Strip default parameters",
            ProjectDefaultParameterStripper.IntermediateDirectoryName,
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.ForeachDeconstruction },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Remove default parameter values and synthesize overloads."),
        new(
            PassIds.RefOutIn,
            "Ref/out/in",
            ProjectRefOutInPreprocessor.IntermediateDirectoryName,
            PassStage.Abi,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.StripDefault, PassStubCatalog.Ids.ForeachSource },
            supportLibrary: PassSupportLibrary.Cs2jRefOutIn,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Box ref/out/in parameters through the RefOutIn support project.",
            catalogItems: "C01"),
        new(
            PassIds.Unsigned,
            "Unsigned types",
            ProjectUnsignedTypePreprocessor.IntermediateDirectoryName,
            PassStage.Abi,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.RefOutIn },
            supportLibrary: PassSupportLibrary.Cs2jUnsignedMath,
            description: "Map unsigned integer types and math to the unsigned support project."),
        new(
            PassStubCatalog.Ids.NumericBoundary,
            "Numeric boundary explicitization",
            ".cs2j-numeric-boundary-src",
            PassStage.Expression,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Unsigned },
            supportLibrary: PassSupportLibrary.Cs2jUnsignedMath,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Explicitize checked/unchecked integer arithmetic (overflow checks and stripping); " +
                         "C34 decimal operator normalization is co-located (default on; kill-switch --no-decimal-ops).",
            catalogItems: "C15, C34, C17, C22"),
        new(
            PassIds.Generic,
            "Generic",
            ProjectGenericPreprocessor.IntermediateDirectoryName,
            PassStage.Abi,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Unsigned },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Deepen generic rewrites for Java erasure compatibility."),
        new(
            PassIds.ThreadStatic,
            "Thread-static rewrite",
            Adapters.ThreadStaticPassAdapter.IntermediateDirectoryName,
            PassStage.Declarative,
            PassStatus.Implemented,
            // C09 flipped to DEFAULT-ON on 2026-10-06 after the §8 gates:
            // fixture Release build/test green, sdk corpus = 1 hit rewritten
            // with 0 skip / 0 residual, zero repo regression, and the Java
            // half consumes the holders via Cs2jThreadLocal (design §9 M2
            // 校注). --disable-pass thread-static opts out.
            defaultEnabled: true,
            // Physically inserted between generic and generic-deepen: both
            // anchor generic only, so the Kahn declaration-order tiebreak
            // locks the slot generic -> thread-static -> generic-deepen
            // (design §3.1/§11).
            runAfterIds: new[] { PassIds.Generic },
            supportLibrary: PassSupportLibrary.None,
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Rewrite [ThreadStatic] fields into explicit ThreadLocal<T> holders with .Value access.",
            catalogItems: "C09"),
        new(
            PassIds.GenericDeepen,
            "Generic erasure deepening",
            Adapters.GenericDeepenPassAdapter.IntermediateDirectoryName,
            PassStage.Abi,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Generic },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Make reified generic runtime information explicit for Java type erasure (typeof(T)/is-as T/static generic fields).",
            catalogItems: "C02"),
        new(
            PassIds.ClosedMetadata,
            "Closed-type metadata registration",
            Adapters.ClosedMetadataPassAdapter.IntermediateDirectoryName,
            PassStage.Abi,
            PassStatus.Implemented,
            // C30 M2' skeleton: no-op by design (analyze produces an empty
            // registration plan, every file stays a byte copy); the collector/
            // generator land in M3. Default-enabled per the all-on policy.
            // Declared before variance-audit so the Kahn declaration-order
            // tiebreak keeps the locked slot generic-deepen -> closed-metadata.
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.GenericDeepen },
            supportLibrary: PassSupportLibrary.None,
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Register closed generic types as explicit System.Type construction trees consumed by the Java reflection side (M2' skeleton: zero rewrites pending the M3 collector/generator).",
            catalogItems: "C30"),
        new(
            PassIds.VarianceAudit,
            "Variance and constraint audit",
            ".cs2j-variance-audit-src",
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Generic },
            needsSemanticModel: true,
            verifyBuildByDefault: false,
            verifyTestsByDefault: false,
            supportLibrary: PassSupportLibrary.None,
            description: "Read-only audit of out/in variance and struct/unmanaged/new()/notnull/default/class constraints; writes variance-audit-report.json.",
            catalogItems: "C33"),
        new(
            PassIds.RecordReadOnly,
            "Record readonly",
            ProjectRecordReadOnlyPreprocessor.IntermediateDirectoryName,
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Generic },
            description: "Convert non-readonly records to immutable form with whole-tree rollback."),
        new(
            PassIds.ArrayMethod,
            "Array method",
            ProjectArrayMethodPreprocessor.IntermediateDirectoryName,
            PassStage.Expression,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.Generic, PassIds.RecordReadOnly },
            supportLibrary: PassSupportLibrary.Cs2jArray,
            description: "Rewrite static array helpers and multi-dimensional arrays; inject the array support project."),
        new(
            PassIds.IndexRange,
            "Index/range desugaring",
            ".cs2j-index-range-src",
            PassStage.Expression,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.ArrayMethod },
            supportLibrary: PassSupportLibrary.Cs2jArray,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Desugar ^/.. (System.Index/System.Range) into length-based int indices and (offset, count) calls; inject the array support project.",
            catalogItems: "C31"),
        new(
            PassIds.MakeReadonly,
            "Make readonly struct",
            ProjectReadOnlyStructPreprocessor.IntermediateDirectoryName,
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.ArrayMethod },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Make eligible structs readonly with whole-tree analysis and shim injection."),
        new(
            PassIds.AttributeNormalize,
            "Attribute normalization",
            ".cs2j-attribute-normalize-src",
            PassStage.Declarative,
            PassStatus.Implemented,
            // C10 flipped to DEFAULT-ON on 2026-10-06 after the six §8 gates
            // (design §13): fixture + sdk intermediate-tree Release build and
            // tests green, strict sdk triage at zero CS2J1310/1320/1321/1322,
            // platform diagnostics proven identical, JSON converter output
            // byte-identical on/off. --disable-pass attribute-normalize opts out.
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.GenericDeepen, PassIds.MakeReadonly },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Fill Caller* info arguments at call sites; inventory JSON/serialization and P/Invoke attributes.",
            catalogItems: "C10"),
        new(
            PassIds.StructValueSemantics,
            "Struct value semantics",
            ".cs2j-struct-value-semantics-src",
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.MakeReadonly },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Make struct copy/mutation/readonly boundaries explicit with defensive clones.",
            catalogItems: "C07"),
        new(
            PassStubCatalog.Ids.UnsafeFfm,
            "Unsafe to FFM",
            ".cs2j-unsafe-ffm-src",
            PassStage.Abi,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.StructValueSemantics },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Strip unsafe blocks and unsafe modifiers from types/members/local functions " +
                "(C06 M0; fixed buffers, stackalloc, pointers and extern normalization follow in later milestones).",
            catalogItems: "C06"),
        new(
            PassStubCatalog.Ids.ClosureLift,
            "Closure/display class lift",
            ".cs2j-closure-lift-src",
            PassStage.StateMachine,
            PassStatus.Implemented,
            // C32 M2; default-enabled per the all-on policy (M3 enable review superseded).
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.RefOutIn },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Lift mutably captured locals/parameters into deterministic private holder classes (C32 M2) and lift lambdas/anonymous methods into display classes plus named methods (C45 M1-M3).",
            catalogItems: "C32, C45"),
        new(
            PassStubCatalog.Ids.IteratorLowering,
            "Iterator lowering",
            ".cs2j-iterator-lowering-src",
            PassStage.StateMachine,
            PassStatus.Implemented,
            // DEFAULT-OFF (opt-in via --enable-pass iterator-lowering).
            // M1 analyzer/state machine core, M2 try-finally protected regions,
            // M3 using/lock normalization, and M4a restricted locals
            // (ref struct/ref local/pointer non-crossing yields stay real
            // locals; crossing shapes are CS2J0403), CS9238/CS9239 mirrors,
            // yield-bearing switch flattening, struct-iterator by-value this,
            // and residual lambda CS2J0405/local-function CS2J0404 defensive
            // gates are implemented. Default-on awaits the M4b gate review
            // (sdk subset profiling, Release three-project green, residual=0).
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.MakeReadonly, PassStubCatalog.Ids.ClosureLift },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Analyze C# iterator methods and lower the supported subset into explicit state machine classes (M1); try-finally protected regions with full Dispose/yield-break cleanup chains (M2); using statement/declaration and lock normalization (M3); non-crossing ref struct/ref local/pointer locals kept as MoveNext locals, yield-bearing switch flattening and struct-iterator by-value this capture (M4). Unsupported forms skip with CS2J0402/CS2J0403/CS2J0404/CS2J0405/CS2J0406 diagnostics.",
            catalogItems: "C04"),
        new(
            PassStubCatalog.Ids.AsyncNormalize,
            "Async normalization",
            ".cs2j-async-normalize-src",
            PassStage.StateMachine,
            PassStatus.Implemented,
            // C18 default-on under the all-preprocessors-default-on policy
            // (the long-term opt-in ruling of design §2 D14 is superseded);
            // --disable-pass async-normalize opts out.
            defaultEnabled: true,
            // Closure-lift is now Implemented/default-on and is the real
            // capture contract (D12); make-readonly stays the nearest enabled
            // predecessor anchor (C04 precedent). Declared right after
            // iterator-lowering so the Kahn declaration-order tiebreak locks
            // the slot: iterator-lowering -> async-normalize ->
            // extension-methods -> eliminate-goto.
            runAfterIds: new[] { PassIds.MakeReadonly, PassStubCatalog.Ids.ClosureLift },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Normalize async/await method bodies: lift zero/this-capture async lambdas and residual async local functions into named async methods, strip ConfigureAwait(bool), and diagnose async void and unsupported awaiters explicitly; the await sites themselves are preserved. C38 expands 'await foreach' to explicit IAsyncEnumerator drives with a faithful in-flight-exception dispose guard, expands 'await using' to try/finally plus DisposeAsync, resolves WithCancellation/ConfigureAwait chains, and lowers async-iterator methods (async IAsyncEnumerable/IEnumerator + yield) to explicit M4 joint state machines; CS2J1844 remains only for local functions, unsupported v1 shapes and the kill-switch.",
            catalogItems: "C18, C38 (C44 subtopic)"),
        new(
            PassIds.ExtensionMethods,
            "Extension method explicitness",
            Adapters.ExtensionMethodResolvePassAdapter.IntermediateDirectoryName,
            PassStage.Expression,
            PassStatus.Implemented,
            // Default-enabled per the all-on policy (initial opt-in superseded).
            defaultEnabled: true,
            // Five support-visibility anchors (delegate/tuple/ref-out-in/unsigned/array-method)
            // plus EM-ORDER (appendix ruling 1): make-readonly and iterator-lowering both
            // consume reduced sugar shapes and must run before this pass.
            runAfterIds: new[]
            {
                PassIds.Delegate,
                PassIds.Tuple,
                PassIds.RefOutIn,
                PassIds.Unsigned,
                PassIds.ArrayMethod,
                PassIds.MakeReadonly,
                PassStubCatalog.Ids.IteratorLowering,
            },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Explicitize extension method calls to static host calls (x.M(a) -> global::Host.M(x, a)); LINQ hosts excluded. C49: C# 14 extension blocks are normalized in the same pass.",
            catalogItems: "C19, C49"),
        new(
            PassIds.EliminateGoto,
            "Eliminate goto",
            ProjectGotoPreprocessor.IntermediateDirectoryName,
            PassStage.ControlFlow,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.MakeReadonly, PassStubCatalog.Ids.IteratorLowering },
            needsSemanticModel: false,
            description: "Eliminate goto/labeled statements into structured loops (syntax pass)."),
        new(
            PassIds.MemberDesugar,
            "Member desugaring",
            ".cs2j-member-desugar-src",
            PassStage.Declarative,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.StructValueSemantics, PassStubCatalog.Ids.ClosureLift },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Desugar properties (C11), indexers (C12), events (C13), operators (C14), finalizers (C39), signatures (C46), the field keyword (C50) and record with/init/required (C27) into backing fields plus explicit methods/members.",
            catalogItems: "C11, C12, C13, C14, C39, C46, C50, C27"),
        new(
            PassStubCatalog.Ids.LinqChannel,
            "LINQ channelization",
            ".cs2j-linq-channel-src",
            PassStage.ControlFlow,
            PassStatus.Implemented,
            // C26 M4 (2026-10-06): flipped default-on after the §8 gates
            // (three-way routing 100% pinned, EF/SDK rescans LC0010=0 on
            // healthy compiles, dual-run suppression wired, M4 GroupBy
            // closeout keeps the build-verified intermediate compilable).
            // --no-linq-rewrite maps to --disable-pass linq-channel.
            defaultEnabled: true,
            // member-desugar / extension-methods / anonymous-type-lift output
            // is what the channel consumes; eliminate-goto is the historical
            // control-flow predecessor. Declared directly after member-desugar
            // so the Kahn declaration-order tiebreak pins the locked slot
            // member-desugar -> linq-channel -> concurrency-normalize.
            runAfterIds: new[]
            {
                PassIds.ExtensionMethods,
                PassIds.AnonymousTypeLift,
                PassIds.EliminateGoto,
            },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Channel LINQ usage through the two-phase route-aware pipeline " +
                         "(query desugaring plus the stock procedural rewriter); M1 attaches the " +
                         "IQueryable three-way routing gate in observe-only mode.",
            catalogItems: "C26"),
        new(
            PassStubCatalog.Ids.ConcurrencyNormalize,
            "Concurrency normalization",
            ".cs2j-concurrency-normalize-src",
            PassStage.Declarative,
            PassStatus.Implemented,
            // C29 default-enabled per the all-on policy: the lock/volatile direct
            // mappings cover the main path and the normalization adds the canonical
            // Monitor forms (design §3.4/§8 opt-in ruling superseded).
            defaultEnabled: true,
            // No strong functional dependency; the late anchor keeps the naming
            // pass last and makes --list-passes placement deterministic.
            runAfterIds: new[] { PassIds.EliminateGoto },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Normalize explicit Monitor enter/try-enter/wait/pulse calls into canonical lock-based C# forms; " +
                         "Java half landed: honest intrinsic compat Monitor bridge (TryEnter explicitly unsupported, CS2J2911), " +
                         "compat Volatile VarHandle bridge, and System.Threading.Lock 分型 (enter/try/finally instead of synchronized).",
            catalogItems: "C29"),
        new(
            PassStubCatalog.Ids.EqualityConcat,
            "Equality/concat call-site materialization",
            ".cs2j-equality-concat-src",
            PassStage.Expression,
            PassStatus.Implemented,
            defaultEnabled: true,
            // Design §3.2: anchored right after tuple.
            runAfterIds: new[] { PassIds.Tuple },
            supportLibrary: PassSupportLibrary.Cs2jPrelude,
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Materialize string ==/!= and + call sites, (object) escapes, delegate value equality and record == by symbol-bound semantics.",
            catalogItems: "C47"),
        new(
            PassStubCatalog.Ids.PlatformIsolation,
            "Platform boundary isolation",
            ".cs2j-platform-isolation-src",
            PassStage.Abi,
            PassStatus.Implemented,
            defaultEnabled: true,
            // M1/M2 anchor eliminate-goto only (M3 adds unsafe-ffm, §3.2).
            // Declaration order here keeps platform -> concurrency -> dynamic
            // (the late stubs) and stays before java-keywords.
            runAfterIds: new[] { PassIds.EliminateGoto },
            needsSemanticModel: true,
            // The adapter closes verification in Report mode (M1); the dual-on
            // default applies to the M2 Stub/Facade apply modes (§3.1).
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Audit platform / interop boundaries into a machine-readable inventory and report " +
                         "before Java conversion (C42 M1: Report mode only; no source rewrite).",
            catalogItems: "C42"),
        new(
            PassStubCatalog.Ids.DynamicDesugar,
            "Dynamic desugaring",
            ".cs2j-dynamic-desugar-src",
            PassStage.Abi,
            PassStatus.Implemented,
            // C25 AUDIT flipped to DEFAULT-ON on 2026-10-06 after the §8 audit
            // gates (design §8/§13): audit only copies the tree + emits a
            // byte-stable report and never rewrites, so the default chain
            // gains zero behavioral output change (fixture/sdk verified).
            // REWRITE stays long-term opt-in via --dynamic-mode Rewrite
            // (gates 6-9 remain open); --disable-pass dynamic-desugar opts
            // out of audit entirely.
            defaultEnabled: true,
            // Late ABI-tail pass like platform-isolation: anchors
            // eliminate-goto only. Declared immediately after
            // platform-isolation so the Kahn declaration-order tiebreak
            // locks the tail slot platform-isolation -> dynamic-desugar ->
            // java-keywords (design §3.2/§3.3).
            runAfterIds: new[] { PassIds.EliminateGoto },
            supportLibrary: PassSupportLibrary.None,
            needsSemanticModel: true,
            // M2 Rewrite verifies build/tests on the intermediate tree; the
            // M1 Audit adapter forces both off exactly like the C42 Report
            // adapter (audit output is byte-identical to its input).
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Audit dynamic (late-bound) sites into a named inventory and dynamic-audit-report.json " +
                         "(C25 M1: Audit mode only, zero source rewrites); M2 concretizes the provable P1 subset.",
            catalogItems: "C25"),
        new(
            PassIds.JavaKeywords,
            "Java reserved words",
            ProjectJavaReservedWordPreprocessor.IntermediateDirectoryName,
            PassStage.FinalNaming,
            PassStatus.Implemented,
            defaultEnabled: true,
            // The naming pass stays last: it also anchors the late-chain stubs so
            // the topological order keeps it at the tail as those families land.
            runAfterIds: new[]
            {
                PassIds.EliminateGoto,
                PassStubCatalog.Ids.PlatformIsolation,
                PassStubCatalog.Ids.ConcurrencyNormalize,
                PassStubCatalog.Ids.DynamicDesugar,
                PassIds.ExtensionMethods,
                PassStubCatalog.Ids.EqualityConcat,
            },
            description: "Rename identifiers clashing with Java reserved words; runs last before conversion."),
    };

    private static readonly Dictionary<string, PassDescriptor> ById =
        Implemented.ToDictionary(p => p.Id, StringComparer.Ordinal);

    /// <summary>Get an implemented descriptor by <see cref="PassIds"/> constant.</summary>
    public static PassDescriptor ImplementedById(string id) => ById[id];

    /// <summary>Build the validated registry (implemented passes plus the Step 8 stub catalog).</summary>
    public static PassRegistry BuildRegistry()
        => PassRegistry.Create(Implemented.Concat(PassStubCatalog.Stubs));
}
