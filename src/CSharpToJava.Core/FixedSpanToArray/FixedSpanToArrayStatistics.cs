namespace CSharpToJava.Core.FixedSpanToArray;

/// <summary>Aggregated statistics for the <c>fixed-span-to-array</c> pass.</summary>
public sealed class FixedSpanToArrayStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>Span/ReadOnlySpan fixed statements rewritten to fixed over array.</summary>
    public int SpanFixedStatementsLowered { get; set; }

    /// <summary>Span-like fixed statements that could not be lowered safely (FSA001).</summary>
    public int SpanFixedStatementsPreserved { get; set; }
}
