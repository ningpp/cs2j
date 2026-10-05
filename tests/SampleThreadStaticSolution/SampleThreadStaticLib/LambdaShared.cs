namespace SampleThreadStaticLib;

/// <summary>
/// Case 5: a thread-static field shared by a lambda and by ordinary methods.
/// Sites captured inside the lambda rewrite to .Value exactly like call-site
/// uses, and storage stays per-thread.
/// </summary>
public static class LambdaShared
{
    [ThreadStatic] internal static int Tally;

    public static int ViaLambda(int add)
    {
        Func<int, int> plus = n => Tally + n;
        Tally += add;
        return plus(add);
    }

    public static int Current => Tally;

    public static void Reset() => Tally = 0;
}
