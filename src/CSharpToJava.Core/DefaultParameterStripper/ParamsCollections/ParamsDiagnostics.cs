namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Diagnostic codes CS2J0600–CS2J0619 for the C48 params-collections rules
/// (design §6). CS2J0615–0618 are reserved for future declaration-kind
/// registrations. Messages follow the strip-default diagnostic channel
/// (<see cref="CSharpToJava.Core.DefaultParameterStripper.DefaultParameterStripDiagnostic"/>);
/// severity is resolved by <see cref="SeverityFor"/> per the design table:
/// strict mode turns the Error/Warning codes into errors.
/// </summary>
public static class ParamsDiagnostics
{
    // ------------------------------------------------------------------
    // Codes
    // ------------------------------------------------------------------

    /// <summary>Info: params site inventory (declarations/call sites, per-kind buckets, verbose).</summary>
    public const string Inventory = "CS2J0600";

    /// <summary>Error (strict)/Warning: params type illegal or undecidable (outside the legal set, error type, baseline-illegal shape defensively hit).</summary>
    public const string UnsupportedType = "CS2J0601";

    /// <summary>Error/Warning: call-site symbol resolution failed; the site is kept and no name/arity guessing is done.</summary>
    public const string SymbolResolutionFailed = "CS2J0602";

    /// <summary>Info: classic array params call site passed through (or explicitized when ExpandArrayCallSites is on).</summary>
    public const string ArrayCallSite = "CS2J0603";

    /// <summary>Info: Span/ReadOnlySpan expansion constructs an array (stack→heap hint; C06 boundary).</summary>
    public const string SpanAsArray = "CS2J0604";

    /// <summary>Info: K3/K4/K5 collection expansion (builder factory / List initializer / interface concrete-class buckets; M2: also the K3 unbindable residual path).</summary>
    public const string CollectionExpanded = "CS2J0605";

    /// <summary>Error/Warning: collection-initializer pattern broken — no accessible parameterless ctor / Add inaccessible or unresolvable (never guessed).</summary>
    public const string InitializerPatternBroken = "CS2J0606";

    /// <summary>Error (strict)/Warning: [CollectionBuilder] type blocked by the gray-release Error policy (CLI default; M2 normal Emit path no longer produces this code). Message carries builder type and factory name.</summary>
    public const string CollectionBuilderBlocked = "CS2J0607";

    /// <summary>Error/Warning: Span with a ref-struct element exceeds the array shape (points at C06).</summary>
    public const string RefStructElement = "CS2J0608";

    /// <summary>Info: form-B single collection argument passed through (reference identity preserved).</summary>
    public const string SingleCollectionPassthrough = "CS2J0609";

    /// <summary>Error/Warning: IOperation and syntax slot conclusions conflict (defensive; site kept).</summary>
    public const string SlotConflict = "CS2J0610";

    /// <summary>Info/Warning: expansion segment nests a collection expression/spread that C36 did not process first.</summary>
    public const string NestedCollectionExpression = "CS2J0611";

    /// <summary>Error/Warning: materialization consistency failure inside a virtual slot family (fail-fast per adjudication 3).</summary>
    public const string VirtualFamilyIncomplete = "CS2J0612";

    /// <summary>Info: forwarding-overload empty params slot filled with a non-array shape (PC-7).</summary>
    public const string OverloadEmptySlotFilled = "CS2J0613";

    /// <summary>
    /// Info: a call to a metadata/framework (non-source) params collection is
    /// passed through unchanged. Such a declaration is never stripped (only
    /// source declarations are planned), so the comma-form call stays legal and
    /// no explicit collection needs to be synthesized.
    /// </summary>
    public const string MetadataCollectionPassthrough = "CS2J0614";

    /// <summary>Reserved for future declaration-kind registrations (CS2J0615–CS2J0618).</summary>
    public const string Reserved = "CS2J0615";

    /// <summary>Error (strict)/Warning: residual gate — non-array params modifier left without a skip pairing, or idempotency failure.</summary>
    public const string ResidualGate = "CS2J0619";

    // ------------------------------------------------------------------
    // Message templates
    // ------------------------------------------------------------------

    public const string UnsupportedTypeMessage =
        "params parameter type '{0}' is not a legal C# 13 params collection type; site kept unchanged ({1})";

    public const string RefStructElementMessage =
        "params type '{0}' has a ref-struct element type '{1}' which cannot be carried by an explicit array; site kept unchanged (see C06)";

    public const string InitializerPatternBrokenMessage =
        "type '{0}' does not expose an accessible parameterless constructor and accessible instance Add method; site kept unchanged";

    public const string CollectionBuilderBlockedMessage =
        "type '{0}' declares [CollectionBuilder('{1}', \"{2}\")]; v1 does not emit builder factories; site kept unchanged";

    /// <summary>
    /// M2 (PC-9) residual: under the Emit policy the builder factory could be
    /// bound neither from the compiler's IOperation ConstructMethod nor from
    /// symbol resolution (C36 analyzer); the declaration/site is kept and the
    /// message carries the builder type and factory name (design §6: 0607
    /// retired, normal path reports CS2J0605).
    /// </summary>
    public const string CollectionBuilderUnresolvedMessage =
        "type '{0}' declares [CollectionBuilder('{1}', \"{2}\")]; the builder factory could not be bound at this site " +
        "(neither the compiler-bound ConstructMethod nor symbol resolution resolved an accessible factory); site kept unchanged";

    public const string VirtualFamilyIncompleteMessage =
        "virtual slot family for '{0}' could not be resolved completely; the declaration was not materialized";

    public const string SymbolResolutionFailedMessage =
        "call site target '{0}' has no registered params declaration plan; the site is kept unchanged (no guessing)";

    public const string ScopeExcludedMessage =
        "params type '{0}' is excluded by the current scope/rollout policy; site kept unchanged";

    public const string CharFaceExcludedMessage =
        "params type '{0}' is excluded in v1 (ReadOnlySpan<char> char face, C52 implicit span conversion boundary); site kept unchanged";

    public const string SlotConflictMessage =
        "IOperation and syntax slot conclusions conflict for call site '{0}'; site kept unchanged";

    public const string OverloadEmptySlotFilledMessage =
        "forwarding overload fills the empty params slot '{0}' with explicit shape '{1}'";

    public const string MetadataCollectionPassthroughMessage =
        "call site target '{0}' is a metadata/framework params collection that is never stripped; " +
        "the comma-form call is kept unchanged";

    /// <summary>M2 CS2J0611: synthesized params expansion nesting a collection expression/spread (C36 must run first).</summary>
    public const string NestedCollectionExpressionMessage =
        "params expansion segment of call site '{0}' contains a collection expression/spread element ('{1}'); " +
        "the C36 collection-expression desugaring must run before C48; site kept unchanged";

    public const string ResidualGateMessage =
        "non-array params modifier remains on '{0}' without a matching skip pairing; residual/idempotency gate failed";

    // ------------------------------------------------------------------
    // Message builders
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the message for a declaration-side skip: ref-struct element,
    /// broken initializer pattern, or a type outside the legal params set.
    /// Shared by the signature collector (collect phase) and the declaration
    /// materializer (strip phase) so both phases report identical text.
    /// </summary>
    public static string BuildSkipMessage(ParamsTargetClassification classification, string memberDisplay)
    {
        var typeName = classification.ParameterType.ToDisplayString();
        return classification.SkipReason switch
        {
            RefStructElement when classification.ElementType != null
                => string.Format(RefStructElementMessage, typeName, classification.ElementType.ToDisplayString()),
            InitializerPatternBroken
                => string.Format(InitializerPatternBrokenMessage, typeName),
            _
                => string.Format(UnsupportedTypeMessage, typeName, memberDisplay),
        };
    }

    // ------------------------------------------------------------------
    // Severity policy
    // ------------------------------------------------------------------

    /// <summary>Codes that stay Info regardless of strictness.</summary>
    private static bool IsInfo(string code)
        => code is Inventory
            or ArrayCallSite
            or SpanAsArray
            or CollectionExpanded
            or SingleCollectionPassthrough
            or OverloadEmptySlotFilled
            or MetadataCollectionPassthrough;

    /// <summary>
    /// Resolves the severity for a code: Info codes stay Info, CS2J0611 is
    /// Info/Warning, everything else is Error in strict mode and Warning
    /// otherwise (design §6).
    /// </summary>
    public static DefaultParameterStripSeverity SeverityFor(string code, bool strict)
    {
        if (IsInfo(code))
        {
            return DefaultParameterStripSeverity.Info;
        }

        if (code == NestedCollectionExpression)
        {
            return strict
                ? DefaultParameterStripSeverity.Warning
                : DefaultParameterStripSeverity.Info;
        }

        return strict
            ? DefaultParameterStripSeverity.Error
            : DefaultParameterStripSeverity.Warning;
    }

    public static DefaultParameterStripDiagnostic Create(string code, string message, bool strict)
        => new(SeverityFor(code, strict), $"{code}: {message}");
}
