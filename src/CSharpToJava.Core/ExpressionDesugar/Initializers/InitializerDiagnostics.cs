using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// CS2J0530–CS2J0545 diagnostic codes for the C24 object/collection-initializer
/// rule (design §6). Append-only range owned by this rule; string codes (not
/// enum) so future family rules cannot accidentally share members.
/// CS2J0543/CS2J0544 remain reserved inside the range; CS2J0546–CS2J0549 are
/// reserved for later C24 milestones and are deliberately not defined yet.
/// </summary>
internal static class InitializerDiagnosticCodes
{
    /// <summary>Info: object initializer site expanded (property/field/indexer sub-counts in statistics).</summary>
    public const string CS2J0530 = "CS2J0530";

    /// <summary>Error(strict)/Warning: member not writable (init-only/readonly/get-only/unresolved).</summary>
    public const string CS2J0531 = "CS2J0531";

    /// <summary>Info: collection initializer site expanded (Add classification sub-counts in statistics).</summary>
    public const string CS2J0532 = "CS2J0532";

    /// <summary>Error(strict)/Warning: no applicable instance/extension Add; Add binding unresolvable.</summary>
    public const string CS2J0533 = "CS2J0533";

    /// <summary>Info: dictionary index initializer <c>[k] = v</c> expanded to index assignments.</summary>
    public const string CS2J0534 = "CS2J0534";

    /// <summary>Error(strict)/Warning: target-typed <c>new()</c> target type not inferrable.</summary>
    public const string CS2J0535 = "CS2J0535";

    /// <summary>Info: target-typed <c>new()</c> explicitized to <c>new T(…)</c> (nullable target counted).</summary>
    public const string CS2J0536 = "CS2J0536";

    /// <summary>Error(strict)/Warning: site in a v1-unsupported statement-lowering position.</summary>
    public const string CS2J0537 = "CS2J0537";

    /// <summary>Error(strict)/Warning: not liftable and not expressible with zero temporaries.</summary>
    public const string CS2J0538 = "CS2J0538";

    /// <summary>Info: Add binding classification count (extension/multi/fluent/params/fallback), verbose.</summary>
    public const string CS2J0539 = "CS2J0539";

    /// <summary>Info: nested-member-initializer getter temporary emitted (chain evaluated once), verbose.</summary>
    public const string CS2J0540 = "CS2J0540";

    /// <summary>Error(strict)/Warning: <c>dynamic</c> receiver / dynamic Add cannot be statically bound (C25).</summary>
    public const string CS2J0541 = "CS2J0541";

    /// <summary>Info: initializer inside an expression-tree lambda skipped per RewriteInExpressionTrees=false.</summary>
    public const string CS2J0542 = "CS2J0542";

    /// <summary>Error(strict)/Warning: residual gate — leftover initializer node without a paired diagnostic.</summary>
    public const string CS2J0545 = "CS2J0545";

    /// <summary>Info (verbose): whole-tree initializer residual gate balanced (CLI AfterFiles).</summary>
    public const string CS2J0546 = "CS2J0546";
}

/// <summary>How a code's severity is decided (design §6 level column).</summary>
internal enum InitializerSeverityClass
{
    Info,

    /// <summary>Error under strict options, Warning otherwise (node preserved when non-strict).</summary>
    StrictError,
}

/// <summary>Default levels and message templates for the CS2J0530–CS2J0545 range.</summary>
internal static class InitializerDiagnostics
{
    public static InitializerSeverityClass SeverityClass(string code) => code switch
    {
        InitializerDiagnosticCodes.CS2J0530 => InitializerSeverityClass.Info,
        InitializerDiagnosticCodes.CS2J0531 => InitializerSeverityClass.StrictError,
        InitializerDiagnosticCodes.CS2J0532 => InitializerSeverityClass.Info,
        InitializerDiagnosticCodes.CS2J0533 => InitializerSeverityClass.StrictError,
        InitializerDiagnosticCodes.CS2J0534 => InitializerSeverityClass.Info,
        InitializerDiagnosticCodes.CS2J0535 => InitializerSeverityClass.StrictError,
        InitializerDiagnosticCodes.CS2J0536 => InitializerSeverityClass.Info,
        InitializerDiagnosticCodes.CS2J0537 => InitializerSeverityClass.StrictError,
        InitializerDiagnosticCodes.CS2J0538 => InitializerSeverityClass.StrictError,
        InitializerDiagnosticCodes.CS2J0539 => InitializerSeverityClass.Info,
        InitializerDiagnosticCodes.CS2J0540 => InitializerSeverityClass.Info,
        InitializerDiagnosticCodes.CS2J0541 => InitializerSeverityClass.StrictError,
        InitializerDiagnosticCodes.CS2J0542 => InitializerSeverityClass.Info,
        InitializerDiagnosticCodes.CS2J0545 => InitializerSeverityClass.StrictError,
        InitializerDiagnosticCodes.CS2J0546 => InitializerSeverityClass.Info,
        _ => InitializerSeverityClass.Info,
    };

    /// <summary>Resolves the effective severity for <paramref name="code"/>.</summary>
    public static ExpressionDesugarSeverity ResolveSeverity(string code, bool strict)
        => SeverityClass(code) switch
        {
            InitializerSeverityClass.StrictError
                => strict ? ExpressionDesugarSeverity.Error : ExpressionDesugarSeverity.Warning,
            _ => ExpressionDesugarSeverity.Info,
        };

    // ---- Message templates ({0}.. placeholders, invariant wording) ----

    public const string ObjectExpandedMessage =
        "Object initializer expanded: {0} member assignment(s) " +
        "({1} property/{2} field/{3} indexer).";

    public const string MemberNotWritableMessage =
        "Member '{0}' on type '{1}' is not writable after construction ({2}); " +
        "init-only members require the member-desugar pass (C11/C27) to relax " +
        "the accessor first. Site left unchanged.";

    public const string CollectionExpandedMessage =
        "Collection initializer expanded: {0} Add call(s) " +
        "(single {1}/multi {2}/extension {3}/params {4}/fluent {5}/" +
        "enumerable-fallback {6}).";

    public const string AddUnresolvedMessage =
        "No applicable instance or extension 'Add' method for collection " +
        "initializer element on type '{0}' (baseline CS1922/CS0117 family); " +
        "the Add binding cannot be resolved. Site left unchanged.";

    public const string DictionaryIndexExpandedMessage =
        "Dictionary index initializer '[{0}] = …' expanded to index-assignment " +
        "statement(s).";

    public const string TargetTypeMissingMessage =
        "Target-typed 'new()' has no inferrable converted type (missing or error " +
        "type; baseline CS8754/CS9176 family). Site left unchanged.";

    public const string TargetTypedNewExpandedMessage =
        "Target-typed 'new()' explicitized to 'new {0}(…)'.{1}";

    public const string UnsupportedContextMessage =
        "Initializer sits in a position this milestone cannot statement-lower " +
        "({0}); supported contexts: local declaration, return/throw, assignment, " +
        "expression statement, using. Site left unchanged.";

    public const string NotLiftableMessage =
        "Initializer cannot be lifted (no enclosing writable statement) and has " +
        "no zero-temporary form ({0}: field/property initializer, attribute " +
        "argument, default parameter value, constant/query context or expression " +
        "tree). Site left unchanged.";

    public const string AddClassifiedMessage =
        "Add binding classified as {0} ({1} argument(s)); {2}.";

    public const string NestedMemberGetterMessage =
        "Nested member initializer: getter chain for '{0}' evaluated once into a " +
        "temporary, then mutated in place.";

    public const string DynamicReceiverMessage =
        "Initializer receiver is 'dynamic'; the Add/member binding cannot be " +
        "statically decided (see C25 for dynamic handling). Site left unchanged.";

    public const string ExpressionTreeMessage =
        "Initializer inside an expression tree is skipped " +
        "(RewriteInExpressionTrees=false); the site is left unchanged.";

    public const string ResidualMessage =
        "{0} unaccounted object/collection/complex-element initializer or bare " +
        "'new()' node(s) remain after expression-desugar processing.";

    /// <summary>
    /// CS2J0545 (M3 CLI whole-tree gate): {0}=residual nodes on disk,
    /// {1}=paired nodes, {2}=unpaired file list.
    /// </summary>
    public const string TreeResidualMessage =
        "whole-tree residual gate failed: {0} object/collection/complex-element " +
        "initializer or bare 'new()' node(s) found in the intermediate tree but " +
        "{1} are paired with the per-file accounting (explained preserved sites + " +
        "coexistence-deferred sites); unpaired files: {2}.";

    /// <summary>
    /// CS2J0546 (M3, Info, verbose): whole-tree initializer residual gate
    /// balanced summary.
    /// </summary>
    public const string InventorySummaryMessage =
        "whole-tree residual gate balanced ({0} residual node(s) = {1} paired " +
        "node(s)).";
}