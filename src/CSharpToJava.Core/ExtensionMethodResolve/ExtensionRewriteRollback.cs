namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// One compile error key for the M4 build-failure rollback gate
/// (design §4.4-4): file (absolute path INSIDE the intermediate tree),
/// Roslyn diagnostic id and message — the same triple shape the
/// java-keywords iterative rollback diffs against.
/// </summary>
public sealed record ExtensionRewriteErrorKey(string File, string Id, string Message);

/// <summary>Rollback verdict for the candidate-vs-baseline compile-error diff.</summary>
public enum ExtensionRollbackVerdict
{
    /// <summary>No new errors: accept every rewritten file.</summary>
    NoNewErrors,

    /// <summary>All new errors attribute to exactly one rewritten file: revert that file only.</summary>
    ExcludeFiles,

    /// <summary>Attribution is impossible/multi-file (CS2J1200): replay every rewritten file from the originals.</summary>
    WholeTreeRollback,
}

/// <summary>
/// Result of <see cref="ExtensionRewriteRollback.Plan"/>: the verdict, the
/// single file to revert (when <see cref="ExtensionRollbackVerdict.ExcludeFiles"/>)
/// and the diffed new error set (observability; the adapter emits EM001 Info
/// per error plus CS2J1200 on whole-tree rollback).
/// </summary>
public sealed record ExtensionRollbackPlan(
    ExtensionRollbackVerdict Verdict,
    IReadOnlyList<string> FilesToRevert,
    IReadOnlyList<ExtensionRewriteErrorKey> NewErrors)
{
    public static ExtensionRollbackPlan Accept(IReadOnlyList<ExtensionRewriteErrorKey> newErrors)
        => new(ExtensionRollbackVerdict.NoNewErrors, Array.Empty<string>(), newErrors);

    public static ExtensionRollbackPlan Exclude(string file, IReadOnlyList<ExtensionRewriteErrorKey> newErrors)
        => new(ExtensionRollbackVerdict.ExcludeFiles, new[] { file }, newErrors);

    public static ExtensionRollbackPlan WholeTree(IReadOnlyList<ExtensionRewriteErrorKey> newErrors)
        => new(ExtensionRollbackVerdict.WholeTreeRollback, Array.Empty<string>(), newErrors);
}

/// <summary>
/// M4 candidate-tree rollback planning (pure Core, no filesystem): diffs new
/// compile errors against the baseline set, then applies the unique-file
/// attribution rule:
/// <list type="bullet">
/// <item>no new errors → accept;</item>
/// <item>every new error locates in ONE file that this pass rewrote →
/// revert exactly that file (all of its rewrites), replay the original copy;</item>
/// <item>an unlocated error, an error in a file the pass did not rewrite, or
/// errors spread over multiple files → whole-tree rollback (CS2J1200).</item>
/// </list>
/// Reverted files must be byte-identical copies of the inputs (the caller
/// performs the File.Copy replay); residual scans exclude them.
/// </summary>
public static class ExtensionRewriteRollback
{
    public static ExtensionRollbackPlan Plan(
        IEnumerable<ExtensionRewriteErrorKey> baselineErrors,
        IEnumerable<ExtensionRewriteErrorKey> candidateErrors,
        IReadOnlyCollection<string> rewrittenFiles)
    {
        ArgumentNullException.ThrowIfNull(baselineErrors);
        ArgumentNullException.ThrowIfNull(candidateErrors);
        ArgumentNullException.ThrowIfNull(rewrittenFiles);

        var baseline = new HashSet<ExtensionRewriteErrorKey>(baselineErrors);
        var rewritten = new HashSet<string>(rewrittenFiles, StringComparer.OrdinalIgnoreCase);

        var newErrors = candidateErrors
            .Where(key => !baseline.Contains(key))
            .ToList();
        if (newErrors.Count == 0)
        {
            return ExtensionRollbackPlan.Accept(Array.Empty<ExtensionRewriteErrorKey>());
        }

        var attributedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var error in newErrors)
        {
            if (string.IsNullOrEmpty(error.File) || !rewritten.Contains(error.File))
            {
                return ExtensionRollbackPlan.WholeTree(newErrors);
            }

            attributedFiles.Add(error.File);
            if (attributedFiles.Count > 1)
            {
                return ExtensionRollbackPlan.WholeTree(newErrors);
            }
        }

        return attributedFiles.Count == 1
            ? ExtensionRollbackPlan.Exclude(attributedFiles.Single(), newErrors)
            : ExtensionRollbackPlan.WholeTree(newErrors);
    }
}
