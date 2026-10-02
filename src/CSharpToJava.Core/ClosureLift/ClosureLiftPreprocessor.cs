using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Entry point for the closure-lift preprocessor.
/// M1 (this task): audit only. It runs <see cref="CaptureAnalyzer"/> over each
/// file and emits CS2J3207 rows carrying the *prospective* holder class /
/// instance / field names; <see cref="ClosureLiftResult.Changed"/> is always
/// false and the output equals the input byte for byte. M2 Task 12 replaces
/// the audit tail with Analyze → Plan → Generate → Rewrite.
/// In project mode one instance is reused across files; the deterministic
/// cross-file class-name table lives on <see cref="_naming"/> (mirrors E02).
/// </summary>
public sealed class ClosureLiftPreprocessor
{
    private readonly ClosureLiftNaming _naming = new();

    public ClosureLiftResult Preprocess(
        string sourceCode,
        SemanticModel? model = null,
        ClosureLiftOptions? options = null)
    {
        options ??= new ClosureLiftOptions();
        var stats = new ClosureLiftStatistics { FilesScanned = 1 };
        var diagnostics = new List<ClosureLiftDiagnostic>();

        SemanticModel? resolvedModel = model;
        if (resolvedModel == null)
        {
            resolvedModel = BuildStandaloneModel(sourceCode, out var parseError);
            if (parseError != null)
            {
                stats.FilesSkippedClean = 1;
                return new ClosureLiftResult(
                    sourceCode, false, Array.Empty<ClosureLiftDiagnostic>(), stats);
            }
        }

        var tree = resolvedModel.SyntaxTree;
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Open the per-file naming session: different files continue the host's
        // class-name index; re-entering the same file (same path + contents) is
        // idempotent instead of advancing it (M1 repeated-run / M2 round-trip).
        _naming.BeginFile(tree.FilePath + "\0" + sourceCode);

        // Fast path: no lambdas and no residual anonymous methods (E05 output).
        if (!root.DescendantNodes().Any(n =>
                n is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            stats.FilesSkippedClean = 1;
            return new ClosureLiftResult(
                sourceCode, false, Array.Empty<ClosureLiftDiagnostic>(), stats);
        }

        var analysis = new CaptureAnalyzer(resolvedModel, options, diagnostics).Analyze(root);

        // Analyzer owns CS2J3201-3206 rows; bucket them for statistics here.
        foreach (var d in diagnostics)
        {
            if (d.Code != ClosureLiftCodes.AuditRow)
                stats.AddSkipped(d.Code);
        }

        if (analysis.AllSymbols.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            return new ClosureLiftResult(sourceCode, false, diagnostics, stats);
        }

        // M1 audit-only tail. No counter named *Generated and no FilesTransformed
        // ever move here — nothing is generated or transformed. Task 11 replaces
        // this with the real pipeline gated on !options.AuditOnly.
        EmitAuditRows(analysis, resolvedModel, stats, diagnostics);
        return new ClosureLiftResult(sourceCode, false, diagnostics, stats);
    }

    /// <summary>
    /// Emit one CS2J3207 Info row per liftable symbol. Prospective naming uses
    /// the exact M2 grouping key preview (design §4.3: host member + declaring
    /// scope block; foreach per-iteration groups carry their own block) and the
    /// same naming API Task 11 will call, so audit names == generated names.
    /// Skipped symbols already carry their CS2J3201-3206 rows and never reserve names.
    /// </summary>
    private void EmitAuditRows(
        ClosureAnalysisResult analysis,
        SemanticModel model,
        ClosureLiftStatistics stats,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        foreach (var member in analysis.Members)
        {
            var hostKey = model.GetDeclaredSymbol(member.HostType) is { } hostSymbol
                ? hostSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : member.HostType.Identifier.Text;

            var liftable = member.Symbols.Where(s => !s.Skipped).ToList();
            var groups = liftable
                .GroupBy(s => (object?)s.ScopeBlock ?? s.EnclosingMember)
                .OrderBy(g => g.Min(s => s.DeclaringReference.Span.Start))
                .ToList();

            // Same helper the M2 planner (Task 11) uses; do not diverge.
            var routineNames = CollectRoutineLocalNames(member.Routine);
            var groupIndex = 0;
            foreach (var group in groups)
            {
                var className = _naming.ReserveClassName(hostKey, member.RoutineName);
                var instanceName = ClosureLiftNaming.ReserveInstanceName(groupIndex, routineNames);
                groupIndex++;

                var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
                if (group.Any(s => s.IsForeachIteration))
                    stats.ForeachPerIterationGroups++;

                foreach (var symbol in group)
                {
                    var fieldName = ClosureLiftNaming.GetFieldName(symbol.Name, usedFieldNames);
                    stats.CapturesByReason[symbol.Reason] =
                        stats.CapturesByReason.TryGetValue(symbol.Reason, out var n) ? n + 1 : 1;

                    var lineSpan = symbol.DeclaringReference.SyntaxTree
                        .GetLineSpan(symbol.DeclaringReference.Span);
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var shape = symbol.IsForeachIteration ? "foreach-per-iteration" : "shared";

                    diagnostics.Add(new ClosureLiftDiagnostic(
                        ClosureLiftSeverity.Info,
                        ClosureLiftCodes.AuditRow,
                        $"[audit] {member.RoutineName}.{symbol.Name}: {symbol.Reason} ({shape}), " +
                        $"sites={symbol.Sites.Count}, quoted={symbol.QuotedSites.Count}, " +
                        $"holder={className}, instance={instanceName}, field={fieldName}, type={symbol.TypeDisplay}",
                        null, line, member.RoutineName, symbol.Name, symbol.Reason));
                }
            }
        }
    }

    /// <summary>
    /// Seed set for per-routine instance reservation: the routine parameters plus
    /// every declarator / foreach iteration variable / local function name inside
    /// the routine body. Seeding nested-scope names is conservative (it can only
    /// force a _2 suffix); the M2 planner must call this identical helper.
    /// </summary>
    private static HashSet<string> CollectRoutineLocalNames(SyntaxNode routine)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (routine is BaseMethodDeclarationSyntax method)
        {
            foreach (var parameter in method.ParameterList.Parameters)
            {
                var text = parameter.Identifier.Text;
                if (text.Length > 0)
                    names.Add(text);
            }
        }

        foreach (var declarator in routine.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            names.Add(declarator.Identifier.Text);
        foreach (var foreachLoop in routine.DescendantNodes().OfType<ForEachStatementSyntax>())
            names.Add(foreachLoop.Identifier.Text);
        foreach (var localFunction in routine.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            names.Add(localFunction.Identifier.Text);

        return names;
    }

    private static SemanticModel BuildStandaloneModel(string sourceCode, out string? parseError)
    {
        parseError = null;
        var tree = CSharpSyntaxTree.ParseText(sourceCode);

        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            parseError = string.Join("; ", errors.Select(e => e.GetMessage()));
        }

        var compilation = CSharpCompilation.Create("closure-lift-standalone",
            new[] { tree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        return compilation.GetSemanticModel(tree);
    }
}
