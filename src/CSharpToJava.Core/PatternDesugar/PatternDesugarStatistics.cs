namespace CSharpToJava.Core.PatternDesugar;

/// <summary>Per-file counters, merged by the CLI adapter across the tree.</summary>
public sealed class PatternDesugarStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int FilesDirtyNoRewrite { get; set; }
    public int PatternSwitchesRewritten { get; set; }
    public int NonIntegerSwitchesRewritten { get; set; }
    public int CatchFiltersRewritten { get; set; }
    public int CatchTypeParamRewritten { get; set; }
    public int GoverningTempsIntroduced { get; set; }
    public int EndLabelsIntroduced { get; set; }
    public int UnsupportedSkipped { get; set; }
    public int ResidualCount { get; set; }
}
