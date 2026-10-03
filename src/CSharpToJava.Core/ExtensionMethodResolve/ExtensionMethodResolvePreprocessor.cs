using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Core entry for the extension-method-resolve pass (C19): bind the
/// whole-tree compilation once, analyze reduced extension call sites, then
/// rewrite each file and expose the residual scan. No cross-file generated
/// products exist for this pass.
///
/// M0 behavior: rules are empty — Analyze selects nothing, RewriteFile always
/// returns Changed=false (the runner then performs a byte-level copy,
/// preserving BOM), and the residual scan reports zero findings.
/// </summary>
public sealed class ExtensionMethodResolvePreprocessor
{
    private Compilation? _compilation;
    private ExtensionMethodResolveOptions _options = new();

    public void BindCompilation(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
    }

    /// <summary>Whole-tree analysis (bindings/diagnostics sorted deterministically per file+span). M0 is a no-op.</summary>
    public void Analyze(ExtensionMethodResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        _options = options;

        // M0: ExtensionCallBinder selects no sites. M1 enumerates every syntax
        // tree here and builds the per-file binding tables.
    }

    /// <summary>
    /// Rewrite the syntax tree backing <paramref name="model"/>. M0 returns the
    /// original tree text unchanged; the adapter treats Changed=false as a
    /// verbatim byte copy.
    /// </summary>
    public ExtensionFileResult RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        ArgumentNullException.ThrowIfNull(model);

        var stats = new ExtensionMethodResolveStatistics
        {
            FilesScanned = 1,
            FilesClean = 1,
        };
        var diagnostics = new List<ExtensionDiagnostic>();

        // M0: no bindings → no rewrite. Keep the output equal to the parsed
        // tree text; callers must rely on Changed=false (never write this text)
        // so the original bytes — including BOM — are preserved.
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        return new ExtensionFileResult(root.ToFullString(), Changed: false, diagnostics, stats);
    }

    /// <summary>Post-rewrite residual scan over the recompiled intermediate tree.</summary>
    public Dictionary<string, int> ResidualScan(Compilation rewritten)
        => ExtensionMethodResidualScanner.Scan(rewritten, _options);
}
