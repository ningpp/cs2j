using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.VarianceAudit;

/// <summary>
/// Process-wide (one audit run) cache of the D12 "baseline compile errors" flag.
/// The errors D12 cares about ("class-level out/in, illegal variance positions" and
/// parse errors) are all <em>declaration</em> diagnostics: resolving them never requires
/// binding method bodies. <see cref="CSharpCompilation.GetDeclarationDiagnostics"/>
/// answers the question for the whole compilation in one cached pass; calling
/// <c>SemanticModel.GetDiagnostics()</c> per tree forced every method body of every
/// file to bind (the single largest cost of the audit on a half-million-LOC tree).
/// </summary>
internal sealed class VarianceBaselineCache
{
    private readonly Compilation _compilation;
    private readonly Lazy<HashSet<string>> _errorFiles;

    public VarianceBaselineCache(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
        _errorFiles = new Lazy<HashSet<string>>(BuildErrorFileSet);
    }

    public bool HasErrors(SyntaxTree? tree)
        => tree != null && _errorFiles.Value.Contains(tree.FilePath);

    public bool HasErrors(SemanticModel model) => HasErrors(model.SyntaxTree);

    /// <summary>Number of trees whose baseline compilation reports declaration errors.</summary>
    public int CountErrorTrees()
    {
        var errorFiles = _errorFiles.Value;
        var count = 0;
        foreach (var tree in _compilation.SyntaxTrees)
        {
            if (errorFiles.Contains(tree.FilePath))
            {
                count++;
            }
        }

        return count;
    }

    private HashSet<string> BuildErrorFileSet()
    {
        var errorFiles = new HashSet<string>(StringComparer.Ordinal);

        // Declaration diagnostics cover parse errors plus all symbol/type errors visible
        // without descending into method bodies (variance-declaration errors, undefined
        // types in signatures, constraint violations, ...).

        foreach (var diagnostic in _compilation.GetDeclarationDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Location.SourceTree is { FilePath: { Length: > 0 } filePath })
            {
                errorFiles.Add(filePath);
            }
        }

        return errorFiles;
    }
}
