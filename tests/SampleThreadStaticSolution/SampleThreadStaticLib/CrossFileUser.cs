namespace SampleThreadStaticLib;

/// <summary>
/// Case 4: cross-file read/write sites — every field here is DECLARED in another
/// file of this compilation. The whole-tree bind rewrites both sides together.
/// </summary>
public static class CrossFileUser
{
    public static int BumpAcrossFiles(int delta)
    {
        PlainCounter.Hits += delta;
        LambdaShared.Tally = delta;
        var snapshot = PlainCounter.Hits;
        return snapshot + LambdaShared.Tally;
    }

    public static int Peek() => PlainCounter.Hits + LambdaShared.Tally;
}
