namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// One file's whole-tree residual reconciliation (IN-13 M3 CLI gate).
/// </summary>
/// <param name="File">Tree-relative file path.</param>
/// <param name="ResidualNodes">
/// Object/collection/complex initializer expressions and bare <c>new()</c>
/// nodes found in the intermediate file on disk.
/// </param>
/// <param name="PairedNodes">
/// Nodes accounted for by the per-file pairing accounting: residual nodes of
/// explained preserved sites plus residual nodes of whole-file
/// C36-coexistence-deferred sites. Lowered sites emit no initializer syntax,
/// so they contribute zero.
/// </param>
internal sealed record InitializerTreeGateFile(
    string File,
    int ResidualNodes,
    int PairedNodes)
{
    public bool Balanced => ResidualNodes == PairedNodes;
}

/// <summary>Aggregated gate result.</summary>
internal sealed record InitializerTreeGateResult(
    int ResidualNodes,
    int PairedNodes,
    IReadOnlyList<InitializerTreeGateFile> Files)
{
    public bool Balanced =>
        ResidualNodes == PairedNodes && Files.All(f => f.Balanced);

    /// <summary>Files whose on-disk residual count does not match its pairing.</summary>
    public IEnumerable<InitializerTreeGateFile> UnpairedFiles
        => Files.Where(f => !f.Balanced);
}

/// <summary>
/// IN-13 whole-tree residual reconciliation (design §7.5, M3 CLI gate).
///
/// The per-file gate (see <c>InitializersRule.CheckResiduals</c>) pairs every
/// residual node inside the in-memory formatted tree (explained annotation or
/// whole-file coexistence deferral); this tree-level reconciliation re-parses
/// the intermediate files AS WRITTEN and verifies the on-disk truth matches the
/// aggregated accounting. A preserved/deferred site may carry more than one
/// residual node (nested creations), so pairing is node-count based
/// (<see cref="InitializerStatistics.PairedResidualNodes"/>), exactly mirroring
/// <c>CollectionExprTreeGate</c>'s structure for C36.
/// </summary>
internal static class InitializerTreeGate
{
    /// <summary>
    /// Paired on-disk residual nodes for one file: residual nodes of explained
    /// preserved sites plus residual nodes of whole-file deferred sites.
    /// </summary>
    public static int CountPairedNodes(InitializerStatistics fileStatistics)
        => fileStatistics.PairedResidualNodes;

    /// <summary>
    /// Reconciles per-file on-disk residual node counts against per-file
    /// paired counts. Only files with residual nodes or paired nodes take part;
    /// clean files never get in the way.
    /// </summary>
    public static InitializerTreeGateResult Reconcile(
        IReadOnlyDictionary<string, int> residualByFile,
        IReadOnlyDictionary<string, int> pairedByFile)
    {
        var files = new HashSet<string>(
            residualByFile.Keys.Concat(pairedByFile.Keys),
            StringComparer.OrdinalIgnoreCase);

        var perFile = new List<InitializerTreeGateFile>();
        var residualTotal = 0;
        var pairedTotal = 0;

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            residualByFile.TryGetValue(file, out var residual);
            pairedByFile.TryGetValue(file, out var paired);

            if (residual == 0 && paired == 0)
                continue;

            perFile.Add(new InitializerTreeGateFile(file, residual, paired));
            residualTotal += residual;
            pairedTotal += paired;
        }

        return new InitializerTreeGateResult(residualTotal, pairedTotal, perFile);
    }
}
