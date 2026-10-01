namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// One file's whole-tree residual reconciliation (CE-12 M3 CLI gate).
/// </summary>
/// <param name="File">Tree-relative file path.</param>
/// <param name="ResidualNodes">
/// CollectionExpressionSyntax/SpreadElementSyntax nodes found in the
/// intermediate file on disk.
/// </param>
/// <param name="PairedNodes">
/// Nodes explained by paired skips: one per skip-paired collection site plus
/// the spread elements carried by preserved sites.
/// </param>
internal sealed record CollectionExprTreeGateFile(
    string File,
    int ResidualNodes,
    int PairedNodes)
{
    public bool Balanced => ResidualNodes == PairedNodes;
}

/// <summary>Aggregated gate result.</summary>
internal sealed record CollectionExprTreeGateResult(
    int ResidualNodes,
    int PairedNodes,
    IReadOnlyList<CollectionExprTreeGateFile> Files)
{
    public bool Balanced =>
        ResidualNodes == PairedNodes && Files.All(f => f.Balanced);

    /// <summary>Files whose on-disk residual count does not match its pairing.</summary>
    public IEnumerable<CollectionExprTreeGateFile> UnpairedFiles
        => Files.Where(f => !f.Balanced);
}

/// <summary>
/// CE-12 whole-tree residual reconciliation (design §7.5, M3 CLI gate).
///
/// The per-file residual gate (see <c>CollectionExprRule.CheckResiduals</c>)
/// pairs every residual node inside the in-memory formatted tree; this
/// tree-level reconciliation re-parses the intermediate files AS WRITTEN and
/// verifies the on-disk truth matches the aggregated skip accounting. A
/// preserved collection site counts once; each spread it carries adds one
/// more residual node (<see cref="CollectionExprStatistics.SkippedSpreadElements"/>).
/// </summary>
internal static class CollectionExprTreeGate
{
    /// <summary>
    /// Skip codes that legitimately leave a collection/spread node behind
    /// (design §7.5 pairing list).
    /// </summary>
    public static readonly string[] PairedSkipCodes =
    {
        CollectionExprDiagnosticCodes.CS2J0571,
        CollectionExprDiagnosticCodes.CS2J0576,
        CollectionExprDiagnosticCodes.CS2J0580,
        CollectionExprDiagnosticCodes.CS2J0581,
        CollectionExprDiagnosticCodes.CS2J0582,
        CollectionExprDiagnosticCodes.CS2J0583,
        CollectionExprDiagnosticCodes.CS2J0588,
    };

    /// <summary>
    /// Sums the paired skip sites of one file: per-site skip counts (only the
    /// pairing codes above) plus the spread elements those preserved sites
    /// carry.
    /// </summary>
    public static int CountPairedNodes(CollectionExprStatistics fileStatistics)
    {
        var sites = 0;
        foreach (var code in PairedSkipCodes)
        {
            fileStatistics.SkippedByReason.TryGetValue(code, out var count);
            sites += count;
        }

        return sites + fileStatistics.SkippedSpreadElements;
    }

    /// <summary>
    /// Reconciles per-file on-disk residual node counts against per-file
    /// paired counts. Only files with residual nodes or paired nodes take
    /// part; clean files never get in the way.
    /// </summary>
    public static CollectionExprTreeGateResult Reconcile(
        IReadOnlyDictionary<string, int> residualByFile,
        IReadOnlyDictionary<string, int> pairedByFile)
    {
        var files = new HashSet<string>(
            residualByFile.Keys.Concat(pairedByFile.Keys),
            StringComparer.OrdinalIgnoreCase);

        var perFile = new List<CollectionExprTreeGateFile>();
        var residualTotal = 0;
        var pairedTotal = 0;

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            residualByFile.TryGetValue(file, out var residual);
            pairedByFile.TryGetValue(file, out var paired);

            if (residual == 0 && paired == 0)
                continue;

            perFile.Add(new CollectionExprTreeGateFile(file, residual, paired));
            residualTotal += residual;
            pairedTotal += paired;
        }

        return new CollectionExprTreeGateResult(residualTotal, pairedTotal, perFile);
    }
}
