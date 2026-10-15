using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the closure-lift pass (C32 M2): registered as Implemented but
/// default-off (reachable via `preprocess --pass closure-lift` /
/// `--enable-pass closure-lift`); the default plan never includes it.
/// Processing performs the real holder-lift rewrite (Changed files return
/// PassFileRewrite); AuditOnly keeps every file verbatim with CS2J3207 rows,
/// and AfterFilesAsync runs the intermediate-tree residual gate.
/// </summary>
internal sealed class ClosureLiftPassAdapter : ProjectPassAdapter
{
    private readonly ClosureLiftOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    /// <summary>Test-project root directories (see <see cref="IsInTestProject"/>).</summary>
    private HashSet<string>? _testProjectDirectories;

    private readonly ClosureLiftPreprocessor _preprocessor = new();
    private readonly List<ClosureLiftDiagnostic> _diagnostics = new();
    private readonly ClosureLiftStatistics _statistics = new();

    public ClosureLiftPassAdapter(
        ClosureLiftOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool strict)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _strict = strict;
    }

    public override PassDescriptor Descriptor
        => PassCatalog.ImplementedById(PassStubCatalog.Ids.ClosureLift);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public ClosureLiftStatistics Statistics => _statistics;
    public IReadOnlyList<ClosureLiftDiagnostic> Diagnostics => _diagnostics;

    // Standard .NET (net6+) ImplicitUsings set, plus System.Text (see below).
    // The spine compilation parses source files directly, so the SDK-generated
    // global usings are absent and bare `Func<int, int>`-style references
    // surface as error types — the named-method stage resolves lambda target
    // delegates through the semantic model and would freeze otherwise liftable
    // files (design D8). Files that already `using` these namespaces are
    // unaffected; the tree mirrors the real project build closely enough for
    // delegate-type resolution.
    //
    // System.Text is added on top of the SDK default set: the regex production
    // project declares its whole using set through csproj `<Using>` items
    // (including System.Text) and its files carry no file-level
    // `using System.Text;`. Match.cs binds the captured `SegmentStringBuilder`
    // local through that namespace; without the global using it binds to an
    // error type and the read-only capture path emits a spurious CS2J4502 that
    // freezes a Java-bound production file (full-chain blocker). The spine's
    // per-project global-usings synthesis cannot be reused here as-is: applied
    // compilation-wide it pulls in every project's whole `<Using>` set
    // (System.Diagnostics, System.Reflection, …), perturbing lambda capture
    // classification across dozens of unrelated files.
    private const string ImplicitGlobalUsings =
        """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Text;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    public override IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
        => new[] { CSharpSyntaxTree.ParseText(ImplicitGlobalUsings, CreateParseOptions(tree)) };

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        // 记录测试工程目录（与 unsafe-ffm 同法）：测试工程不是 Java 转换输入，
        // 其 closure-lift 每一条 Error 都只是「文件原样保留」，需要降级（见
        // RewriteFileAsync）。
        if (tree.ProjectGraph is { } graph)
        {
            _testProjectDirectories = graph.ProjectsInTopologicalOrder
                .Where(p => p.Kind == ProjectKind.Test)
                .Select(p => NormalizeDirectory(p.ProjectDirectory))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        var inTestProject = IsInTestProject(context.SourceFilePath);
        foreach (var diagnostic in result.Diagnostics)
        {
            var effective = diagnostic;

            // 与 unsafe-ffm UF050 先例一致：测试工程不是 Java 转换输入（链只对
            // 生产工程产出 Java；测试工程是 .NET 运行时预言机）。closure-lift 的
            // 每一条 Error 都意味着「整个文件原样保留」，对不产出 Java 的测试工程
            // 没有实际影响；而 pass 编译只用基础元数据引用集（无 xunit 等包），
            // 测试工程对 xunit/外部库类型的捕获会被解析成 error type 而误报
            // CS2J4502。故测试工程内的 Error 降级为 Warning，源文件仍原样通过
            // C# verify，全链得以继续。
            if (inTestProject && diagnostic.Severity == ClosureLiftSeverity.Error)
            {
                effective = diagnostic with { Severity = ClosureLiftSeverity.Warning };
            }

            _diagnostics.Add(effective.WithFilePath(context.RelativePath));
        }

        if (_verbose)
        {
            string status;
            if (_options.AuditOnly)
                status = "audit";
            else if (result.Changed)
                status = "transformed";
            else if (result.Diagnostics.Any(d => d.Code != ClosureLiftCodes.AuditRow))
                status = "blocked (copy)";
            else
                status = "clean (copy)";
            Console.WriteLine($"[closure-lift] {status}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true, PreserveBom = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // Design §7.3 gate: re-parse every .cs under the intermediate tree in
        // audit terms. Transformed files are 0 by construction; frozen files
        // (any CS2J3201-3206 row) keep their original captures and are owned
        // by their per-file rows — M3 gate §8.4 excludes such owned Warning
        // rows when judging the tree.
        var residual = 0;
        var treeStatementLambdas = 0;
        foreach (var csFile in Directory.EnumerateFiles(
                     tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csFile);

            // One parse per file drives both gates: files without lambda syntax
            // short-circuit to zero captures/statement-lambdas inside the
            // preprocessor without building the two per-file semantic models
            // (the tree walk dominates otherwise).
            var (fileCount, statementLambdas, anonymousMethods) =
                _preprocessor.CountResiduals(text);
            if (fileCount > 0 && _verbose)
            {
                Console.WriteLine(
                    $"[closure-lift] residual gate: {fileCount} capture(s) in {Path.GetRelativePath(tree.IntermediateRoot, csFile)}");
            }
            residual += fileCount;

            // C45 M2 (design §5-4): statement-lambda residual is measured
            // tree-wide for visibility; the per-file CS2J4510 gate inside the
            // preprocessor owns the ENFORCEMENT (unaccounted residuals freeze
            // their file), so no duplicate gate row is emitted here. Each
            // residual anonymous method is a CS2J4503 row (E05 output missing).
            treeStatementLambdas += statementLambdas;
            if (anonymousMethods > 0)
            {
                var relative = Path.GetRelativePath(tree.IntermediateRoot, csFile);
                foreach (var line in CountAnonymousMethodLines(text))
                {
                    _diagnostics.Add(new ClosureLiftDiagnostic(
                        ClosureLiftSeverity.Error,
                        ClosureLiftCodes.ResidualAnonymousMethodGate,
                        $"Residual anonymous method in the intermediate tree; delegate pass output expected (design §6).",
                        relative,
                        line));
                }
            }
        }

        // Measured in the intermediate tree (same '=' discipline as E02's
        // LocalFunctionsResidual), never added on top of per-file statistics.
        _statistics.ResidualMutableCaptures = residual;
        // Tree-wide measurement replaces (never adds on top of) the per-file
        // merge so the summary shows exactly one residual statement count.
        _statistics.ResidualStatementLambdas = treeStatementLambdas;

        if (residual > 0)
        {
            var severity = _strict
                ? ClosureLiftSeverity.Error
                : ClosureLiftSeverity.Warning;
            _diagnostics.Add(new ClosureLiftDiagnostic(
                severity,
                ClosureLiftCodes.UnresolvableSymbol,
                $"C32 residual gate: {residual} mutable lambda capture(s) remain in the intermediate tree; " +
                "the complete capture-site set cannot be guaranteed for those file(s)."));
        }

        return Task.CompletedTask;
    }

    /// <summary>1-based line numbers of anonymous-method nodes (gate rows).</summary>
    private static IEnumerable<int> CountAnonymousMethodLines(string text)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text);
        foreach (var anonymous in tree.GetRoot()
                     .DescendantNodes()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousMethodExpressionSyntax>())
        {
            yield return anonymous.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Warning,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Error,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet build failed with exit code {exitCode} during closure-lift audit."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Error,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet test failed with exit code {exitCode} during closure-lift audit."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ClosureLiftSeverity.Error);

    public override string? BuildChainSummaryLine()
    {
        var blocked = _statistics.SkippedByReason.Values.Sum();
        var summary = $"closure-lift: {_statistics.FilesTransformed} transformed, " +
                      $"{_statistics.FilesSkippedClean} clean, " +
                      $"{_statistics.HolderClassesGenerated} holder class(es), " +
                      $"{_statistics.CapturesLifted} capture(s) lifted, " +
                      $"{blocked} blocked, " +
                      $"{_statistics.ResidualMutableCaptures} residual";

        // C45 segment appears only when C45 work happened, so the C32 summary
        // stays byte-identical whenever the pass runs idle for M1 (design §5).
        var c45Work = _statistics.LambdaMethodsLifted
                      + _statistics.DelegateSitesRewritten
                      + _statistics.ResidualStatementLambdas
                      + _statistics.NestedDisplayClasses
                      + _statistics.RecursiveLambdas
                      + _statistics.DiscardParametersNormalized;
        if (c45Work > 0)
        {
            var displayClasses = _statistics.DisplayClassesGenerated > _statistics.HolderClassesGenerated
                ? _statistics.DisplayClassesGenerated
                : _statistics.HolderClassesGenerated;
            summary += $", {displayClasses} display class(es)";
            if (_statistics.NestedDisplayClasses > 0)
                summary += $" ({_statistics.NestedDisplayClasses} nested)";
            summary += $", {_statistics.LambdaMethodsLifted} lambda method(s) " +
                       $"({_statistics.StaticLambdaMethods} static, {_statistics.ThisOnlyLambdas} this-only";
            if (_statistics.RecursiveLambdas > 0)
                summary += $", {_statistics.RecursiveLambdas} recursive";
            summary += "), " +
                       $"{_statistics.DelegateSitesRewritten} delegate site(s), ";
            if (_statistics.DiscardParametersNormalized > 0)
                summary += $"{_statistics.DiscardParametersNormalized} discard parameter(s) normalized, ";
            summary += $"{_statistics.ExpressionTreeLambdasSkipped} quoted skipped, " +
                       $"{_statistics.ResidualStatementLambdas} stmt lambda(s) residual " +
                       $"({_statistics.AccountedResidualLambdas} accounted)";
        }

        return summary;
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => verbose || d.Severity != ClosureLiftSeverity.Info)
            .Select(d => $"[{d.Severity}] {d.FilePath}:{d.Line}: {d.Message}")
            .ToList();

    private void MergeStatistics(ClosureLiftStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.HolderClassesGenerated += source.HolderClassesGenerated;
        _statistics.HolderInstancesGenerated += source.HolderInstancesGenerated;
        _statistics.CapturesLifted += source.CapturesLifted;
        _statistics.SitesRewritten += source.SitesRewritten;
        _statistics.ForeachPerIterationGroups += source.ForeachPerIterationGroups;
        _statistics.ResidualMutableCaptures += source.ResidualMutableCaptures;
        _statistics.StaticLambdaMethods += source.StaticLambdaMethods;
        _statistics.LambdaMethodsLifted += source.LambdaMethodsLifted;
        _statistics.DelegateSitesRewritten += source.DelegateSitesRewritten;
        // ResidualStatementLambdas is merged per-file into AccountedResidual (the
        // tree-wide count lands in ResidualStatementLambdas via AfterFilesAsync);
        // keeping the two buckets disjoint avoids double counting.
        _statistics.AccountedResidualLambdas += source.AccountedResidualLambdas;
        _statistics.DisplayClassesGenerated += source.DisplayClassesGenerated;
        _statistics.ThisOnlyLambdas += source.ThisOnlyLambdas;
        _statistics.NestedDisplayClasses += source.NestedDisplayClasses;
        _statistics.RecursiveLambdas += source.RecursiveLambdas;
        _statistics.DiscardParametersNormalized += source.DiscardParametersNormalized;
        _statistics.ExpressionTreeLambdasSkipped += source.ExpressionTreeLambdasSkipped;

        foreach (var kvp in source.CapturesByReason)
        {
            if (!_statistics.CapturesByReason.TryAdd(kvp.Key, kvp.Value))
                _statistics.CapturesByReason[kvp.Key] += kvp.Value;
        }

        foreach (var kvp in source.SkippedByReason)
        {
            if (!_statistics.SkippedByReason.TryAdd(kvp.Key, kvp.Value))
                _statistics.SkippedByReason[kvp.Key] += kvp.Value;
        }
    }

    /// <summary>
    /// True when the source file lives under a test-project root (unsafe-ffm 同法).
    /// </summary>
    private bool IsInTestProject(string sourceFilePath)
    {
        if (_testProjectDirectories is not { Count: > 0 } roots)
        {
            return false;
        }

        var directory = NormalizeDirectory(Path.GetDirectoryName(sourceFilePath));
        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            if (roots.Contains(NormalizeDirectory(current.FullName)))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static string NormalizeDirectory(string? path)
        => path == null
            ? string.Empty
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
