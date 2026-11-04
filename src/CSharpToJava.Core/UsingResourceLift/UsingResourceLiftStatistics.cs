namespace CSharpToJava.Core.UsingResourceLift;

/// <summary>Aggregated statistics for the <c>using-resource-lift</c> pass.</summary>
public sealed class UsingResourceLiftStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>Using resource expressions lifted into a preceding local declaration.</summary>
    public int UsingResourcesLifted { get; set; }

    /// <summary>Using statements that could not be lifted and were preserved (USL001).</summary>
    public int UsingResourcesSkipped { get; set; }
}
