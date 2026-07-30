using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

public sealed class ReadOnlyStructMaker
{
    /// <summary>单文件入口（用于测试）：分析单个文件中的 struct。</summary>
    public ReadOnlyStructMakerResult MakeReadOnly(
        SyntaxTree tree,
        SemanticModel semanticModel,
        ReadOnlyStructMakerOptions? options = null)
    {
        options ??= new ReadOnlyStructMakerOptions();
        var stats = new ReadOnlyStructMakerStatistics();
        var diagnostics = new List<ReadOnlyStructMakerDiagnostic>();
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var structs = root.DescendantNodes().OfType<StructDeclarationSyntax>().ToList();
        stats.StructsScanned = structs.Count;

        var analyzer = new StructAnalyzer(options, semanticModel);
        var analysisResults = new Dictionary<StructDeclarationSyntax, AnalyzeResult>();

        foreach (var structSyntax in structs)
        {
            var symbol = semanticModel.GetDeclaredSymbol(structSyntax);
            if (symbol == null) continue;

            var result = analyzer.Analyze(symbol, structSyntax);
            analysisResults[structSyntax] = result;

            switch (result.Level)
            {
                case ConversionLevel.Skip:
                    stats.StructsSkipped++;
                    stats.Level0_Skipped++;
                    if (options.ReportSkipped)
                        diagnostics.Add(new(ReadOnlyStructSeverity.Info, result.QualifiedName ?? "?",
                            result.Reason, result.Level, result.Pattern));
                    break;
                case ConversionLevel.NotConvertible:
                    stats.StructsFailed++;
                    diagnostics.Add(new(ReadOnlyStructSeverity.Warning, result.QualifiedName ?? "?",
                        result.Reason, result.Level, result.Pattern));
                    break;
            }
        }

        // Rewrite eligible structs
        var rewriter = new ReadOnlyStructRewriter(analysisResults, options);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var changed = newRoot.ToFullString() != root.ToFullString();

        // Format only when changes were made (to fix spacing in generated nodes)
        if (changed)
        {
            using var workspace = new AdhocWorkspace();
            newRoot = (CompilationUnitSyntax)Formatter.Format(newRoot, workspace);
        }

        if (changed)
        {
            foreach (var (syntax, result) in analysisResults.Where(kv => kv.Value.ShouldRewrite))
            {
                stats.StructsConverted++;
                switch (result.Level)
                {
                    case ConversionLevel.DirectAdd: stats.Level1_DirectAdd++; break;
                    case ConversionLevel.PropertyConvert: stats.Level2_PropertyConvert++; break;
                    case ConversionLevel.DataContainer: stats.Level3_DataContainer++; break;
                    case ConversionLevel.MethodMigrate: stats.Level5_MethodMigrate++; break;
                }
                diagnostics.Add(new(ReadOnlyStructSeverity.Info, result.QualifiedName ?? "?",
                    result.Reason, result.Level, result.Pattern));
            }
        }

        return new ReadOnlyStructMakerResult(
            newRoot.ToFullString(), changed,
            new Dictionary<string, string>(), diagnostics, stats);
    }
}
