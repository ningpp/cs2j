using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata.ConsumerSurfaces;

/// <summary>
/// How a configured consumer surface handles generic type arguments
/// (CM-ENUM-3/5/6). Only <see cref="TypeArguments"/> is consumed by the
/// collector today; the remaining policies are registered data for the
/// reflection/Yaml consumers (CM-ENUM-5/6) and never alter the current walk.
/// </summary>
public enum ClosedGenericArgPolicy
{
    /// <summary>No generic argument handling.</summary>
    None,

    /// <summary>The entry's own method type arguments are the closed types (CM-ENUM-3).</summary>
    TypeArguments,

    /// <summary>MakeGeneric*-style call: enumerate receiver and argument closures (CM-ENUM-5).</summary>
    MakeGeneric,

    /// <summary>Read-only generic surface: record the fact, never register (CM-ENUM-6).</summary>
    ReadOnly,
}

/// <summary>Execution timing of a consumer surface (upgrade plan §6.4-R3 [v5]).</summary>
public enum ClosedExecutionKind
{
    /// <summary>Guard may be inserted lexically before the consuming statement.</summary>
    Immediate,

    /// <summary>Deferred enumeration; registration moves to the actual iteration entry.</summary>
    Deferred,

    /// <summary>Expression-tree metadata; the tree itself stays untouched.</summary>
    ExpressionTree,

    /// <summary>Factory creates the consumer later; registration happens at the factory entry.</summary>
    Factory,

    /// <summary>Async stream; registration happens at the first MoveNextAsync entry.</summary>
    AsyncStream,
}

/// <summary>
/// One configured consumer-surface entry. <see cref="Assembly"/> is the
/// defining assembly identity; a source-declared shadow of the same type name
/// (test fixtures compile their own) still matches, preserving the historical
/// type-name-only matching.
/// </summary>
public sealed record ClosedTypeConsumerSurfaceEntry(
    string Assembly,
    string Type,
    IReadOnlyList<string> Methods,
    ClosedGenericArgPolicy GenericArgPolicy,
    bool RequiresGuard,
    ClosedExecutionKind ExecutionKind);

/// <summary>
/// Symbol matcher over one named consumer-surface profile. The Core only
/// implements "match a symbol against a configured entry"; library names live
/// exclusively in <c>config/ClosedTypeConsumerSurfaces.json</c>.
/// </summary>
public interface IClosedTypeConsumerProfile
{
    /// <summary>Profile name as written in the configuration file.</summary>
    string Name { get; }

    /// <summary>Configured entries in file order.</summary>
    IReadOnlyList<ClosedTypeConsumerSurfaceEntry> Entries { get; }

    /// <summary>True when the method matches a configured entry (type + method (+ assembly)).</summary>
    bool IsEntry(IMethodSymbol method);
}
