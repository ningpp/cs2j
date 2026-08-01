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

        // Update call sites for L5 migrated void methods
        if (options.UpdateCallSites)
        {
            var migratedVoidMethods = new Dictionary<string, string>(); // methodName -> structName
            var structFieldNames = new Dictionary<string, List<string>>(); // structName -> field names
            foreach (var (structSyntax, result) in analysisResults.Where(kv => kv.Value.Level == ConversionLevel.MethodMigrate))
            {
                if (result.MethodMigrations == null) continue;
                foreach (var migration in result.MethodMigrations.Where(m => m.Type == MigrationType.VoidToStruct))
                {
                    migratedVoidMethods[migration.Method.Name] = structSyntax.Identifier.Text;
                }

                // Collect instance field names for this struct
                var fields = structSyntax.Members.OfType<FieldDeclarationSyntax>()
                    .Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .ToList();
                structFieldNames[structSyntax.Identifier.Text] = fields;
            }

            if (migratedVoidMethods.Count > 0)
            {
                var callSiteUpdater = new CallSiteUpdater(migratedVoidMethods, semanticModel, structFieldNames);
                newRoot = (CompilationUnitSyntax)callSiteUpdater.Visit(newRoot)!;
                stats.CallSitesUpdated = migratedVoidMethods.Count; // approximate
            }
        }

        // Update call sites for L5 migrated property setters (WithXxx methods).
        // L5 migration converts public property setters to WithXxx() methods and removes
        // the setters. Object initializers and external assignments that used those setters
        // must be updated to use constructor calls or WithXxx() calls.
        if (options.UpdateCallSites)
        {
            var l5WithMethodStructs = new Dictionary<string, HashSet<string>>();

            foreach (var (structSyntax, result) in analysisResults.Where(kv => kv.Value.Level == ConversionLevel.MethodMigrate))
            {
                var structName = structSyntax.Identifier.Text;
                // Scan the REWRITTEN tree for WithXxx methods (property setter migrations)
                var rewrittenStruct = newRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
                    .FirstOrDefault(s => s.Identifier.Text == structName);
                if (rewrittenStruct == null) continue;

                var withMethodProps = rewrittenStruct.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.Text.StartsWith("With", StringComparison.Ordinal)
                                && m.ParameterList.Parameters.Count == 1
                                && m.ReturnType is IdentifierNameSyntax rt
                                && rt.Identifier.Text == structName)
                    .Select(m => m.Identifier.Text.Substring(4))
                    .ToHashSet();

                if (withMethodProps.Count > 0)
                    l5WithMethodStructs[structName] = withMethodProps;
            }

            if (l5WithMethodStructs.Count > 0)
            {
                var l5AssignmentRewriter = new AssignmentRewriter(l5WithMethodStructs, semanticModel);
                l5AssignmentRewriter.BuildVariableTypeMap(root);
                newRoot = (CompilationUnitSyntax)l5AssignmentRewriter.Visit(newRoot)!;
            }
        }

        // Update call sites for L4 public field assignments
        if (options.EnablePublicFieldConversion)
        {
            var publicFieldStructs = new Dictionary<string, HashSet<string>>();

            foreach (var (structSyntax, result) in analysisResults.Where(kv => kv.Value.Level == ConversionLevel.PublicFieldToProperty))
            {
                var structName = structSyntax.Identifier.Text;
                var publicFields = structSyntax.Members.OfType<FieldDeclarationSyntax>()
                    .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .ToHashSet();
                publicFieldStructs[structName] = publicFields;
            }

            if (publicFieldStructs.Count > 0)
            {
                var assignmentRewriter = new AssignmentRewriter(publicFieldStructs, semanticModel);
                // Build variable type map from the ORIGINAL tree (before ReadOnlyStructRewriter modified it)
                // This allows us to verify that field assignment receivers are actually target structs
                assignmentRewriter.BuildVariableTypeMap(root);
                newRoot = (CompilationUnitSyntax)assignmentRewriter.Visit(newRoot)!;
            }
        }

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
                    case ConversionLevel.PublicFieldToProperty:
                        stats.Level4_PublicFieldToProperty++;
                        // Count public fields converted
                        var publicFieldCount = syntax.Members.OfType<FieldDeclarationSyntax>()
                            .SelectMany(f => f.Declaration.Variables)
                            .Count(v => syntax.Members.OfType<FieldDeclarationSyntax>()
                                .Any(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) &&
                                          f.Declaration.Variables.Contains(v)));
                        stats.PublicFieldsConverted += publicFieldCount;
                        break;
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
