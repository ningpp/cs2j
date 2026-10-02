namespace SampleClosureLiftLib;

/// <summary>
/// Static / captureless lambdas (design D12): closure-lift must emit nothing
/// for this file. Behavior coverage guards against accidental rewrites.
/// </summary>
public static class CapturelessControls
{
    private static readonly Func<int, int> Square = static x => x * x;

    public static int Run(int x)
    {
        Func<int, int> addOne = static v => v + 1;
        return addOne(x) + Square(x); // x=3 -> 13
    }
}
