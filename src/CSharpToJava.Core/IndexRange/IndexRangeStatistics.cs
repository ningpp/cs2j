namespace CSharpToJava.Core.IndexRange;

/// <summary>Statistics collected by the index-range preprocessor.</summary>
public sealed class IndexRangeStatistics
{
    // Common five fields.
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;

    /// <summary>x[^n] element-access sites rewritten.</summary>
    public int HatElementSites;

    /// <summary>x[a..b] slice sites rewritten (all receiver kinds).</summary>
    public int RangeSliceSites;

    /// <summary>Standalone ^n sites (reported, never rewritten).</summary>
    public int StandaloneIndexSites;

    /// <summary>Standalone range sites (reported, never rewritten).</summary>
    public int StandaloneRangeSites;

    /// <summary>Receiver temporary variables lifted.</summary>
    public int ReceiverTempsLifted;

    /// <summary>Boundary/operand temporary variables lifted.</summary>
    public int BoundTempsLifted;

    /// <summary>Length/count values read via temp.</summary>
    public int LengthTempReads;

    /// <summary>Cs2jArray.ArrayHelper.Slice call sites emitted.</summary>
    public int ArrayHelperCalls;

    /// <summary>String Substring specializations emitted.</summary>
    public int StringSpecializations;

    /// <summary>Sites skipped by diagnostic code (code -> count).</summary>
    public readonly Dictionary<string, int> SkippedByReason = new();

    internal void AddSkip(string code)
    {
        SkippedByReason.TryGetValue(code, out var count);
        SkippedByReason[code] = count + 1;
    }
}
