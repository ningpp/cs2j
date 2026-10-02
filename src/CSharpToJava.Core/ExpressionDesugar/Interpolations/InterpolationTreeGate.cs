namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// One file's whole-tree residual reconciliation for C23 (the AfterFiles
/// CLI gate; design §5.3/§7.5).
/// </summary>
/// <param name="File">Tree-relative file path.</param>
/// <param name="ResidualNodes">
/// InterpolatedStringExpressionSyntax nodes found in the intermediate file
/// on disk (re-parsed as written).
/// </param>
/// <param name="PairedNodes">
/// Nodes explained by paired preserves: one per preserved outer site plus
/// the nested interpolation nodes those outers carry verbatim.
/// </param>
internal sealed record InterpolationTreeGateFile(
    string File,
    int ResidualNodes,
    int PairedNodes)
{
    public bool Balanced => ResidualNodes == PairedNodes;
}

/// <summary>Aggregated C23 gate result.</summary>
internal sealed record InterpolationTreeGateResult(
    int ResidualNodes,
    int PairedNodes,
    IReadOnlyList<InterpolationTreeGateFile> Files)
{
    public bool Balanced =>
        ResidualNodes == PairedNodes && Files.All(f => f.Balanced);

    /// <summary>Files whose on-disk residual count does not match its pairing.</summary>
    public IEnumerable<InterpolationTreeGateFile> UnpairedFiles
        => Files.Where(f => !f.Balanced);
}

/// <summary>
/// C23 whole-tree residual reconciliation (design §7.5, mirrors
/// <c>CollectionExprTreeGate</c> CE-12). The per-file residual gate
/// (<c>InterpolationRule.CheckResiduals</c>) pairs every residual node
/// inside the in-memory formatted tree via explained annotations; this
/// tree-level reconciliation re-parses the intermediate files AS WRITTEN
/// and verifies the on-disk truth matches the per-file preserve accounting
/// (<see cref="InterpolationStatistics.CountPairedNodes"/>), covering files
/// copied verbatim as well. Runs ONLY when the C23 rule is enabled.
/// </summary>
internal static class InterpolationTreeGate
{
    /// <summary>
    /// Reconciles per-file on-disk residual node counts against per-file
    /// paired counts. Only files with residual or paired nodes take part;
    /// clean files never get in the way.
    /// </summary>
    public static InterpolationTreeGateResult Reconcile(
        IReadOnlyDictionary<string, int> residualByFile,
        IReadOnlyDictionary<string, int> pairedByFile)
    {
        var files = new HashSet<string>(
            residualByFile.Keys.Concat(pairedByFile.Keys),
            StringComparer.OrdinalIgnoreCase);

        var perFile = new List<InterpolationTreeGateFile>();
        var residualTotal = 0;
        var pairedTotal = 0;

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            residualByFile.TryGetValue(file, out var residual);
            pairedByFile.TryGetValue(file, out var paired);

            if (residual == 0 && paired == 0)
                continue;

            perFile.Add(new InterpolationTreeGateFile(file, residual, paired));
            residualTotal += residual;
            pairedTotal += paired;
        }

        return new InterpolationTreeGateResult(residualTotal, pairedTotal, perFile);
    }
}
