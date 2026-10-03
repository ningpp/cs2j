namespace SampleVarianceAuditLib;

/// <summary>
/// One site per constraint kind the audit inventories (VA020-VA027, design D5a).
/// The forms are all legal C#; the converter's Java emission has no equivalent for
/// several of them, which is exactly what the read-only audit reports.
/// </summary>
public static class ConstraintSamples
{
    /// <summary>VA020 (struct dropped) + VA027 (default(T) value semantics).</summary>
    public static T Zero<T>() where T : struct => default(T);

    /// <summary>VA021: today this emits no converter diagnostic at all (design §1.1#9).</summary>
    public static void TouchUnmanaged<T>() where T : unmanaged
    {
    }

    /// <summary>VA022: new() constraint; the construction point is E10's Activator product.</summary>
    public static T CreateWithNew<T>() where T : new()
        => global::System.Activator.CreateInstance<T>();

    /// <summary>VA024: notnull is compile-time only.</summary>
    public static void RequireNotNull<T>(T value) where T : notnull => _ = value;

    /// <summary>VA026: class constraint (Java reference types are the default).</summary>
    public static T Coalesce<T>(T? value, T fallback) where T : class => value ?? fallback;
}

public class ConstraintBase
{
    public virtual void Describe<T>() where T : IComparable<T>
    {
    }
}

/// <summary>VA025: the 'default' constraint is pure override signature metadata.</summary>
public sealed class ConstraintDerived : ConstraintBase
{
    public override void Describe<T>() where T : default
    {
    }
}