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
    public const string ForeachDeconstruction = "foreach-deconstruction";
    public const string PatternDesugar = "pattern-desugar";
    public const string StripDefault = "strip-default";
    public const string RefOutIn = "ref-out-in";
    public const string Unsigned = "unsigned";
    public const string Generic = "generic";
    public const string GenericDeepen = "generic-deepen";
    public const string RecordReadOnly = "record-readonly";
    public const string ArrayMethod = "array-method";
    public const string IndexRange = "index-range";
    public const string MakeReadonly = "make-readonly";
    public const string StructValueSemantics = "struct-value-semantics";
    public const string EliminateGoto = "eliminate-goto";
    public const string MemberDesugar = "member-desugar";
    public const string JavaKeywords = "java-keywords";
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
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Conditional },
            needsSemanticModel: false,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Strip #line directives, audit #error/extern-alias/global-using forms and optionally normalize file-scoped namespaces; pure-syntax idempotent; normalize raw/u8/verbatim/\\e literals to regular C# lexical forms.",
            catalogItems: "C28, C43"),
        new(
            PassStubCatalog.Ids.PartialMerge,
            "Partial type merge",
            ".cs2j-partial-merge-src",
            PassStage.Lexical,
            PassStatus.Implemented,
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
                         "and the C24 initializer rules desugar object/collection initializers into explicit statements.",
            catalogItems: "C23, C24, C36"),
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
            // C32 M2 ships default-off; M3 owns the enable-by-default review (design §8 gate 6).
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.RefOutIn },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Lift mutably captured locals/parameters into deterministic private holder classes before lambdas (C32 M2).",
            catalogItems: "C32"),
        new(
            PassStubCatalog.Ids.IteratorLowering,
            "Iterator lowering",
            ".cs2j-iterator-lowering-src",
            PassStage.StateMachine,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.MakeReadonly, PassStubCatalog.Ids.ClosureLift },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Lower C# iterator methods into explicit state machine classes.",
            catalogItems: "C04"),
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
            description: "Desugar properties (C11), indexers (C12), events (C13) and operators (C14) into backing fields plus explicit get_X/set_X/get_Item/add_X/remove_X methods.",
            catalogItems: "C11, C12, C13, C14, C39, C50"),
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
                PassStubCatalog.Ids.ExtensionMembers,
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
