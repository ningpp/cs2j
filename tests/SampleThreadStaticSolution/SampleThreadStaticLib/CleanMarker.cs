namespace SampleThreadStaticLib;

/// <summary>
/// A completely clean compilation unit: no thread-static attribute anywhere.
/// The pass must reproduce this file byte-for-byte in the intermediate tree.
/// </summary>
public static class CleanMarker
{
    public const string Marker = "clean-file-byte-identical";

    public static int Describe(int value) => value * 2;
}
