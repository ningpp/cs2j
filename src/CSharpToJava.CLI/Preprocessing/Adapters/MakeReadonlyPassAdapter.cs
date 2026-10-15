using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the make-readonly pass. Drives its own multi-phase orchestration:
/// the per-file ReadOnlyStructMaker rewrite (bare parse, one shared maker) followed
/// by the fixed chain of cross-file/tree fix-ups in
/// <see cref="ReadOnlyStructPassEngine"/>. Tree preparation, restore and the
/// streaming build/test verification reuse the runner stage helpers verbatim.
/// </summary>
internal sealed class MakeReadonlyPassAdapter : ProjectPassAdapter
{
    private readonly ReadOnlyStructMakerOptions _makerOptions;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly List<ReadOnlyStructMakerDiagnostic> _diagnostics = new();
    private readonly ReadOnlyStructMakerStatistics _statistics = new();

    public MakeReadonlyPassAdapter(
        ReadOnlyStructMakerOptions makerOptions,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _makerOptions = makerOptions;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.MakeReadonly);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override string CompilationName => "readonly-preprocess";

    public ReadOnlyStructMakerStatistics Statistics => _statistics;
    public IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics => _diagnostics;

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;
        var sourceRoot = tree.SourceRoot;
        var intermediateRoot = tree.IntermediateRoot;

        // Parse all C# files into syntax trees. The historical pass uses bare parse
        // options (no preprocessor symbols, default language version).
        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        // Files whose source text contains no `struct` keyword declare no structs
        // (the modifier/declaration keyword must appear literally), so the maker can
        // never rewrite them. Recording them here lets the per-file maker loop skip
        // the whole-tree struct walk for the vast majority of files.
        var structBearingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in tree.CsFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            var parsed = CSharpSyntaxTree.ParseText(sourceCode, path: csFile);
            syntaxTrees[csFile] = parsed;
            if (sourceCode.IndexOf("struct", StringComparison.Ordinal) >= 0)
                structBearingFiles.Add(csFile);
        }

        var compilation = CSharpCompilation.Create(
            CompilationName,
            syntaxTrees.Values,
            GetMetadataReferences(),
            CreateCompilationOptions());

        var maker = new ReadOnlyStructMaker();
        var globalMigratedMethods = new Dictionary<string, string>(); // methodName -> structName

        foreach (var sourceFile in tree.SourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // The solution file was already copied (and rewritten to drop
                // unsupported projects) in the project-file loop above; copying it
                // again here would restore the unfiltered original.
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tree.ResetScratchFile(sourceFile, destinationFile);
                continue;
            }

            if (!syntaxTrees.TryGetValue(sourceFile, out var parsedTree))
            {
                tree.ResetScratchFile(sourceFile, destinationFile);
                continue;
            }

            // cs2j-generated support/registration files (_Cs2jTupleTypes.*.cs etc.)
            // are authored for exact semantics — notably the tuple replacement
            // structs MUST stay mutable with public fields (t.Item = x / ref t.Item /
            // --t.Item). The readonly-struct shaping would turn them into get-only
            // properties and produce CS0200 at the rewritten call sites.
            if (IsGeneratedSupportFile(sourceFile))
            {
                tree.ResetScratchFile(sourceFile, destinationFile);
                continue;
            }

            // Struct-free source (no `struct` keyword): the maker would scan zero
            // structs and report no change, so the whole-tree analysis walk is skipped
            // and the file is treated exactly like an unchanged file.
            if (!structBearingFiles.Contains(sourceFile))
            {
                tree.ResetScratchFile(sourceFile, destinationFile);
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(parsedTree);
            var result = maker.MakeReadOnly(parsedTree, semanticModel, _makerOptions);

            ReadOnlyStructPassEngine.MergeStatistics(_statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                _diagnostics.Add(diagnostic with { FilePath = relativePath });
            }

            // Collect migrated void method names for cross-file call site update
            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostic.Level == ConversionLevel.MethodMigrate && diagnostic.StructName != null)
                {
                    // The diagnostic name comes from ToDisplayString and carries type
                    // parameters for generic structs ("ArrayBuilder<T>"); the scan in
                    // UpdateCrossFileCallSites matches against the bare identifier.
                    var structKey = diagnostic.StructName;
                    var genericIdx = structKey.IndexOf('<');
                    if (genericIdx > 0) structKey = structKey.Substring(0, genericIdx);
                    // We'll detect method names from the output code in the second pass
                    globalMigratedMethods[structKey] = structKey;
                }
            }

            if (!result.Changed)
            {
                tree.ResetScratchFile(sourceFile, destinationFile);
            }
            else
            {
                // The maker historically writes UTF-8 without BOM even when the source
                // carried one; unchanged files are copied verbatim instead.
                await File.WriteAllTextAsync(destinationFile, result.OutputCode!, new System.Text.UTF8Encoding(false));
            }
        }

        // The readonly-struct rewrite relies on modern C# features (e.g. covariant
        // return types for interface implementations). Older target frameworks default
        // to old language versions, so pin the latest version in the intermediate builds.
        // Runs after the per-file copy loop because project files may also be copied there.
        ReadOnlyStructPassEngine.EnsureLangVersionLatest(intermediateRoot);

        // The intermediate tree is built without assembly signing. When a project grants
        // InternalsVisibleTo with a PublicKey but nothing is actually signed, the compiler
        // rejects the unsigned friend assembly (CS0281), which also hides internal virtual
        // members from test overrides (CS0115). Strip the PublicKey in that case.
        ReadOnlyStructPassEngine.NormalizeFriendAssemblyAccess(intermediateRoot);

        // Test-process culture normalization: .NET Framework localizes framework
        // exception messages through the thread UI culture. Inject a module-initializer
        // shim into each supported C# test project that pins the UI culture to en-US.
        ReadOnlyStructPassEngine.InjectTestUiCultureShims(
            intermediateRoot, sourceRoot, tree.ProjectGraph, tree.Exclusion, _verbose);

        var sourceFileList = tree.SourceFiles.ToList();

        // Every cross-file pass below re-reads the intermediate tree and re-parses
        // every output file. Most files are untouched by any pass, so a shared cache
        // (validated by the freshly read text) lets them reuse the same syntax tree
        // instead of paying a full parse per pass. Cross-pass tree reuse is safe:
        // trees are immutable and each pass builds its own compilation around them.
        var treeCache = new ReadOnlyStructPassEngine.OutputTreeCache();

        // Second pass: update call sites across all files for migrated methods
        if (globalMigratedMethods.Count > 0 && _makerOptions.UpdateCallSites)
        {
            await ReadOnlyStructPassEngine.UpdateCrossFileCallSites(
                intermediateRoot, sourceRoot, sourceFileList, globalMigratedMethods, treeCache);
        }

        // Third pass: update cross-file read accesses for L4 public field conversions
        if (_makerOptions.EnablePublicFieldConversion)
        {
            await ReadOnlyStructPassEngine.UpdateCrossFileFieldReads(
                intermediateRoot, sourceRoot, sourceFileList, treeCache);
        }

        // Third-and-a-half pass: hoist ref/out arguments that target readonly struct
        // fields into temps with WithXxx write-back (CS0192).
        await ReadOnlyStructPassEngine.HoistRefReadonlyFieldArguments(
            intermediateRoot, sourceRoot, sourceFileList, treeCache);

        // Third-and-three-quarter pass: widen transformed interface members to
        // covariant returns and rewrite interface call sites to reassign the receiver.
        await ReadOnlyStructPassEngine.TransformMigratedInterfaces(
            intermediateRoot, sourceRoot, sourceFileList, treeCache);

        // Companion pass: concrete class implementers widen void methods to return
        // `this` so the widened interface contract is satisfied.
        await ReadOnlyStructPassEngine.FixConcreteInterfaceImplementers(
            intermediateRoot, sourceRoot, sourceFileList, treeCache);

        // Final interface-compat step: add explicit bridge implementations to classes.
        await ReadOnlyStructPassEngine.AddClassInterfaceBridges(
            intermediateRoot, sourceRoot, sourceFileList, treeCache);

        // Fourth pass: normalize constructors of readonly structs so each field is
        // assigned exactly once (Java final fields allow a single assignment).
        await ReadOnlyStructPassEngine.NormalizeReadOnlyStructConstructors(
            intermediateRoot, sourceRoot, sourceFileList, treeCache);

        // Fifth pass: mark structs in inactive #if regions readonly too.
        await ReadOnlyStructPassEngine.MarkDisabledRegionStructsReadonly(
            intermediateRoot, sourceRoot, sourceFileList, treeCache);

        // Restore NuGet packages, verify build/tests, then drop bin/ outputs.
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(tree.Layout, intermediateRoot);
        if (restoreTarget != null)
        {
            context.Restore();
            await context.VerifyBuildAndTestsAsync();
            ProjectPassRunner.DeleteBuildOutputs(intermediateRoot);
        }
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("MakeReadonlyPassAdapter drives the file loop itself.");

    /// <summary>
    /// Files generated by cs2j passes/support libraries follow the
    /// <c>_Cs2j*</c> top-level naming convention (e.g.
    /// <c>_Cs2jTupleTypes.&lt;project&gt;.cs</c>). They are pre-authored for
    /// exact C# semantics and must not be shaped as user code.
    /// </summary>
    internal static bool IsGeneratedSupportFile(string sourceFilePath)
    {
        var fileName = Path.GetFileName(sourceFilePath);
        return fileName.StartsWith("_Cs2j", StringComparison.Ordinal)
            && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ReadOnlyStructMakerDiagnostic(
            ReadOnlyStructSeverity.Warning, "<project>",
            $"dotnet restore failed with exit code {exitCode}. " +
            "Package references may not be resolved.",
            null, null));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ReadOnlyStructMakerDiagnostic(
            ReadOnlyStructSeverity.Error, "<project>",
            $"dotnet build failed with exit code {exitCode} after readonly transformation. " +
            "The readonly-struct rewrite produced code that does not compile.",
            null, null));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ReadOnlyStructMakerDiagnostic(
            ReadOnlyStructSeverity.Error, "<project>",
            $"dotnet test failed with exit code {exitCode} after readonly transformation.",
            null, null));

    public override void ReportVerifyCommandResult(string verb, string targetPath, int exitCode)
    {
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"dotnet {verb} exited with code {exitCode} for {targetPath}");
        }
        else
        {
            Console.WriteLine($"[make-readonly] dotnet {verb} succeeded for {targetPath}");
        }
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ReadOnlyStructSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"make-readonly: {_statistics.StructsConverted} converted, " +
           $"{_statistics.StructsSkipped} skipped, " +
           $"{_statistics.StructsFailed} failed (scanned {_statistics.StructsScanned})";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ReadOnlyStructSeverity.Info || verbose)
            .Select(d => $"[{d.Severity}] {d.FilePath}: {d.StructName}: {d.Reason}")
            .ToList();
}
