namespace SampleExtApp;

/// <summary>
/// This file deliberately contains NO extension-method sugar. The
/// extension-methods pass must byte-copy it unchanged (preserving the UTF-8
/// BOM this file is saved with).
/// </summary>
public static class CleanMarker
{
    public const string Marker = "clean-file-bytes-preserved";
}
