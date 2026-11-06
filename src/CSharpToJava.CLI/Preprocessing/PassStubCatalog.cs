using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Stub registrations for the merged preprocessor families from
/// prompts/preprocessor-catalog.md §3.3 and the preprocessor-foundation plan
/// (Step 8). Every entry is <see cref="PassStatus.Stub"/> and disabled by
/// default: disabled stubs never enter the execution plan (no directory, no
/// work), while explicitly enabling one fails loudly with CS2J9001 instead of
/// silently producing unprocessed output.
/// <para>
/// The list is currently EMPTY: every historical stub has been flipped to an
/// Implemented descriptor in <see cref="PassCatalog"/>. Pass id constants
/// live in <see cref="PassIds"/> only (single source of truth, upgrade plan
/// 附录 I.5 step 1) — the former duplicate <c>PassStubCatalog.Ids</c> constant
/// set was merged there and removed.
/// </para>
/// </summary>
internal static class PassStubCatalog
{
    public static IReadOnlyList<PassDescriptor> Stubs { get; } = new List<PassDescriptor>
    {
        // All historical stubs (ClosedMetadata/VarianceAudit/LexicalNormalize/
        // PartialMerge/ForeachSource/UnsafeFfm/ClosureLift/NumericBoundary/
        // ThreadStatic/AttributeNormalize/ExtensionMethods/LinqChannel/
        // AsyncNormalize/ExpressionDesugar/AnonymousTypeLift/EqualityConcat/
        // PlatformIsolation/ConcurrencyNormalize/DynamicDesugar) flipped to
        // Implemented in PassCatalog; their descriptors live there and no
        // stub entry remains.
    };
}
