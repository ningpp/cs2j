namespace SampleNumericBoundaryLib.Nullable;

/// <summary>
/// C22 M2 NL-10/NL-11 oracle fixture: lifted prefix/postfix increment and
/// lifted compound assignment. A null receiver stays null; postfix captures
/// the old value (the <c>o</c> role); the boundary cell
/// <c>(int.MaxValue)++</c> wraps to <c>int.MinValue</c> in this unchecked library.
/// Each <c>Native*</c> member pairs with the hand-written guarded shape.
/// </summary>
public static class NullableIncrementSamples
{
    // ── NL-10 prefix ──

    public static int? NativePrefix(int? start)
    {
        var x = start;
        ++x;
        return x;
    }

    public static int? GuardedPrefix(int? start)
    {
        var x = start;
        x = x.HasValue ? (int?)(x.Value + 1) : (int?)null;
        return x;
    }

    // ── NL-10 postfix: returns the old value, stores the guarded new value ──

    public static (int? oldValue, int? newValue) NativePostfix(int? start)
    {
        var x = start;
        var old = x++;
        return (old, x);
    }

    public static (int? oldValue, int? newValue) GuardedPostfix(int? start)
    {
        var x = start;
        var old = x;
        x = old.HasValue ? (int?)(old.Value + 1) : (int?)null;
        return (old, x);
    }

    public static (int? oldValue, int? newValue) NativePostfixDecrement(int? start)
    {
        var x = start;
        var old = x--;
        return (old, x);
    }

    public static (int? oldValue, int? newValue) GuardedPostfixDecrement(int? start)
    {
        var x = start;
        var old = x;
        x = old.HasValue ? (int?)(old.Value - 1) : (int?)null;
        return (old, x);
    }

    // ── NL-11 lifted compound assignment ──

    public static int? NativeCompound(int? start, int? y)
    {
        var x = start;
        x += y;
        return x;
    }

    public static int? GuardedCompound(int? start, int? y)
    {
        var x = start;
        x = x.HasValue && y.HasValue ? (int?)(x.Value + y.Value) : (int?)null;
        return x;
    }

    public static int? NativeCompoundOneSided(int? start, int y)
    {
        var x = start;
        x += y;
        return x;
    }

    public static int? GuardedCompoundOneSided(int? start, int y)
    {
        var x = start;
        x = x.HasValue ? (int?)(x.Value + y) : (int?)null;
        return x;
    }
}
