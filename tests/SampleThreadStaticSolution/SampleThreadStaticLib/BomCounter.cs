namespace SampleThreadStaticLib;

/// <summary>
/// Hit file carrying a UTF-8 BOM: the rewritten output must keep the BOM.
/// </summary>
public static class BomCounter
{
    [ThreadStatic] private static int _bomHits;

    public static int Increment() => ++_bomHits;

    public static int Current => _bomHits;
}
