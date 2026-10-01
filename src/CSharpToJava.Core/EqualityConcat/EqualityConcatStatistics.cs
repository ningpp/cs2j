namespace CSharpToJava.Core.EqualityConcat;

/// <summary>Statistics collected by the equality/concat preprocessor (design §4).</summary>
public sealed class EqualityConcatStatistics
{
    // Common file fields.
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;

    /// <summary>string ==/!= sites rewritten to string.Equals (EC-1).</summary>
    public int StringEqualsRewritten;

    /// <summary>object identity sites rewritten to object.ReferenceEquals (EC-2).</summary>
    public int ObjectReferenceRewritten;

    /// <summary>Pure left-add chains folded into a single string.Concat (EC-3).</summary>
    public int ConcatChainsFolded;

    /// <summary>Total operands inside the folded Concat argument lists (EC-3).</summary>
    public int ConcatOperandsTotal;

    /// <summary>string += compound assignments rewritten (EC-4).</summary>
    public int ConcatCompoundRewritten;

    /// <summary>delegate ==/!= sites rewritten to DelegateOps.Equals (EC-6).</summary>
    public int DelegateEqualsRewritten;

    /// <summary>record class/struct synthesized == sites rewritten to object.Equals (EC-7).</summary>
    public int RecordEqualsRewritten;

    /// <summary>Temporary variables lifted for unstable += targets (EC-4).</summary>
    public int TemporariesLifted;

    /// <summary>Expression-tree sites skipped with CS2J0472 (Info).</summary>
    public int ExpressionTreeSitesSkipped;

    /// <summary>Lifted ==/!= sites left for C22, reported with CS2J0474 (EC-8).</summary>
    public int LiftedEqualityPendingC22;

    /// <summary>Tuple residual sites (incl. null edge) reported with CS2J0476 (EC-9).</summary>
    public int TupleNullEdgeSites;

    /// <summary>Sites classified through the operation-missing fallback path.</summary>
    public int FallbackClassifications;

    /// <summary>Residual sites counted by the post-rewrite gate.</summary>
    public int ResidualCount;

    /// <summary>Sites skipped by diagnostic code (code -> count).</summary>
    public readonly Dictionary<string, int> SkippedByReason = new();

    internal void AddSkip(string code)
    {
        SkippedByReason.TryGetValue(code, out var count);
        SkippedByReason[code] = count + 1;
    }
}
