namespace CSharpToJava.Core.CompatMethodReplacement;

/// <summary>Aggregated statistics for the <c>compat-method-replacement</c> pass.</summary>
public sealed class CompatMethodReplacementStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>Method bodies replaced by a registry entry.</summary>
    public int MethodsReplaced { get; set; }

    /// <summary>Methods that already carried the replacement marker (idempotent re-run).</summary>
    public int MethodsAlreadyReplaced { get; set; }

    /// <summary>Near misses reported as CMR001 (same type/method name, different signature).</summary>
    public int SignatureMismatches { get; set; }

    /// <summary>Registry entries that never matched a declaration in the processed file set.</summary>
    public int EntriesUnused { get; set; }

    /// <summary>Keys of the registry entries that matched at least one declaration.</summary>
    public HashSet<string> MatchedEntryKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Merge another file's statistics into this accumulator.</summary>
    public void MergeFrom(CompatMethodReplacementStatistics source)
    {
        ArgumentNullException.ThrowIfNull(source);

        FilesScanned += source.FilesScanned;
        FilesTransformed += source.FilesTransformed;
        FilesSkippedClean += source.FilesSkippedClean;
        MethodsReplaced += source.MethodsReplaced;
        MethodsAlreadyReplaced += source.MethodsAlreadyReplaced;
        SignatureMismatches += source.SignatureMismatches;
        MatchedEntryKeys.UnionWith(source.MatchedEntryKeys);
    }
}
