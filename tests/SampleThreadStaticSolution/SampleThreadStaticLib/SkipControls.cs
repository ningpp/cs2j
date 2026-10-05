namespace SampleThreadStaticLib;

/// <summary>
/// Skip control (case 6): a thread-static field inside a generic container.
/// Per-closed-type storage cannot be expressed after Java erasure, so v1 skips
/// the whole plan with CS2J1204 and the attribute is retained. The field type
/// intentionally does NOT reference T: it is the container that is generic.
/// </summary>
public sealed class GenericBox<T>
{
    [ThreadStatic] private static int _hits;

    public static int Bump() => ++_hits;

    public static int Current => _hits;
}

/// <summary>
/// Skip control (case 7): [ThreadStatic] on an INSTANCE field is unsupported
/// (CS2J1201); the field and attribute stay untouched.
/// </summary>
public sealed class InstanceSkip
{
#pragma warning disable CA2259 // deliberate skip control: [ThreadStatic] on an instance field must stay untouched (CS2J1201)
    [ThreadStatic] private int _value;
#pragma warning restore CA2259

    public void Set(int value) => _value = value;

    public int Get() => _value;
}
