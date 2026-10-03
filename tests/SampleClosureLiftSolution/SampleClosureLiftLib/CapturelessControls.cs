namespace SampleClosureLiftLib;

/// <summary>
/// Static / captureless lambdas (design D12): the method-body lambda lifts
/// onto the static path. Behavior coverage guards against accidental rewrites.
/// The helper is a plain method instead of a field-initializer lambda:
/// D18③ (M3) makes every lambda hosted by a field/property/indexer
/// initializer a CS2J4504 unsupported host, including captureless statics.
/// </summary>
public static class CapturelessControls
{
    public static int Run(int x)
    {
        Func<int, int> addOne = static v => v + 1;
        return addOne(x) + Square(x); // x=3 -> 13
    }

    private static int Square(int x) => x * x;
}
