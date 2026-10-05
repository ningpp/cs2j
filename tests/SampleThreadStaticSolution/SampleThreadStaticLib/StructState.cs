namespace SampleThreadStaticLib;

/// <summary>
/// Mutable value type stored in a thread-static field (case 3). Mutations through
/// a method and through a field must be temp-lifted and written back (TS-5).
/// </summary>
public struct CounterVec
{
    public int X;
    public int Y;

    public void Bump()
    {
        X++;
        Y += 2;
    }
}

public static class StructState
{
    [ThreadStatic] private static CounterVec _vec;

    /// <summary>One method-mutation site and one field-mutation site (TS-5).</summary>
    public static void BumpCurrent()
    {
        _vec.Bump();
        _vec.X += 10;
    }

    public static CounterVec Snapshot() => _vec;
}
