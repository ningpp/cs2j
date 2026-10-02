namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Per-kind rollout gate for the C48 params-collections rules (design §2.4 D8,
/// §8-4 gray-release order). <see cref="All"/> processes every supported params
/// target kind; the other members narrow processing to a single kind so the
/// rule can be released one family at a time.
/// </summary>
public enum ParamsCollectionsScope
{
    All,

    /// <summary>Only K2 System.Span&lt;T&gt; / ReadOnlySpan&lt;T&gt; sites.</summary>
    Span,

    /// <summary>Only K4 concrete collection-initializer types (List&lt;T&gt; etc.).</summary>
    MutableCollection,

    /// <summary>Only K5 five-interface sites.</summary>
    Interface,

    /// <summary>Only K3 [CollectionBuilder] sites (gated by the Builder policy).</summary>
    CollectionBuilder,
}

/// <summary>
/// Policy for <c>[CollectionBuilder]</c> params types. v1 only uses
/// <see cref="Error"/>: the site is blocked with CS2J0607 and left unchanged.
/// <see cref="Emit"/> is reserved for the M2 factory-emission path.
/// </summary>
public enum CollectionBuilderPolicy
{
    Error,
    Emit,
}

/// <summary>
/// Options for the C48 params-collections rule set, nested inside
/// <see cref="CSharpToJava.Core.DefaultParameterStripper.DefaultParameterStripperOptions"/>.
/// Null or <see cref="Enabled"/>=false keeps the enclosing strip-default pass
/// byte-for-byte identical to its pre-C48 behavior.
/// </summary>
/// <param name="Enabled">Master switch; the rules are opt-in (default off).</param>
/// <param name="Strict">Strict mode turns the skip-family codes (CS2J06xx) into errors that fail the pass; otherwise they warn.</param>
/// <param name="Scope">Per-kind gray-release gate; only sites of the selected kind(s) are processed.</param>
/// <param name="ExpandArrayCallSites">
/// D8 sub-switch: when false (default), classic <c>params T[]</c> declarations keep
/// their modifier (strategy A) and their comma call sites stay comma-separated
/// (Java varargs maps them directly). When true, array expansion call sites are
/// explicitized as well.
/// </param>
/// <param name="Builder">CollectionBuilder handling; v1 blocks with CS2J0607.</param>
public sealed record ParamsCollectionsOptions(
    bool Enabled = false,
    bool Strict = true,
    ParamsCollectionsScope Scope = ParamsCollectionsScope.All,
    bool ExpandArrayCallSites = false,
    CollectionBuilderPolicy Builder = CollectionBuilderPolicy.Error);
