using CommandLine;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.GotoEliminator;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Pipeline.Planning;
using CSharpToJava.Core.UnsignedTypeRewriter;
using CSharpToJava.Core.RefOutInPreprocessor;
using CSharpToJava.Core.ArrayMethodRewriter;
using CSharpToJava.Core.DefaultParameterStripper;
using CSharpToJava.Core.LocalFunctionLift;
using CoreLocalFunctionLiftOptions = CSharpToJava.Core.LocalFunctionLift.LocalFunctionLiftOptions;
using ParamsCollectionsOptions = CSharpToJava.Core.DefaultParameterStripper.ParamsCollections.ParamsCollectionsOptions;
using ParamsCollectionsScope = CSharpToJava.Core.DefaultParameterStripper.ParamsCollections.ParamsCollectionsScope;
using CollectionBuilderPolicy = CSharpToJava.Core.DefaultParameterStripper.ParamsCollections.CollectionBuilderPolicy;
using CSharpToJava.Core.NullCoalesceRewriter;
using CSharpToJava.Core.EnumPreprocessor;
using CSharpToJava.Core.DelegatePreprocessor;
using CSharpToJava.TypeMapping;
using CSharpToJava.Core.Workspace;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.CLI.Preprocessing;
using System.Text.Json;

namespace CSharpToJava.CLI;

public class Program
{
    static async Task<int> Main(string[] args)
    {
        // Register MSBuild locator early, before any Roslyn workspace APIs.
        SolutionLoader.EnsureMSBuildRegistered();
        return await MainImpl(args);
    }

    internal static async Task<int> MainImpl(string[] args)
    {
        using var parser = new Parser(settings => settings.AllowMultiInstance = true);
        return await parser.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions, ArrayMethodOptions, ForeachDeconstructionVerbOptions, NullCoalesceVerbOptions, DelegatePreprocessVerbOptions>(args)
            .MapResult(
                (ConvertOptions opts) => ConvertFile(opts),
                (ConvertProjectOptions opts) => ConvertProject(opts),
                (AnalyzeOptions opts) => AnalyzeProject(opts),
                (EliminateGotoOptions opts) => EliminateGoto(opts),
                (MakeReadOnlyOptions opts) => MakeReadonly(opts),
                (UnsignedTypeOptions opts) => UnsignedType(opts),
                (GenericPreprocessOptions opts) => GenericPreprocess(opts),
                (StripDefaultOptions opts) => StripDefault(opts),
                (LocalFunctionLiftOptions opts) => LocalFunctionLiftAsync(opts),
                (TupleToStructVerbOptions opts) => TupleToStructVerbAsync(opts),
                (StripConditionalOptions opts) => StripConditionalCommand.RunAsync(opts),
                (RecordReadOnlyVerbOptions opts) => RecordReadOnlyVerbAsync(opts),
                (ArrayMethodOptions opts) => ArrayMethod(opts),
                (ForeachDeconstructionVerbOptions opts) => ForeachDeconstructionAsync(opts),
                (NullCoalesceVerbOptions opts) => NullCoalesceAsync(opts),
                (DelegatePreprocessVerbOptions opts) => DelegatePreprocessAsync(opts),
                _ => parser.ParseArguments<EnumPreprocessVerbOptions, RefOutInVerbOptions, JavaKeywordsVerbOptions, PreprocessVerbOptions>(args)
                    .MapResult(
                        (EnumPreprocessVerbOptions opts) => EnumPreprocessAsync(opts),
                        (RefOutInVerbOptions opts) => RefOutInAsync(opts),
                        (JavaKeywordsVerbOptions opts) => JavaKeywordsVerbAsync(opts),
                        (PreprocessVerbOptions opts) => PreprocessVerbAsync(opts),
                        _ => Task.FromResult(1))
            );
    }

    private static async Task<int> ConvertFile(ConvertOptions opts)
    {
        try
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);

            var options = new ConversionOptions
            {
                TargetJavaVersion = Enum.Parse<JavaVersion>(opts.JavaVersion, true),
                TypeMappingConfigPath = opts.MappingConfig,
                JavaMetadataPath = ResolveJavaMetadataPath(opts.MappingConfig),
                GenerateJavaDoc = opts.GenerateJavaDoc,
                UseRecords = opts.UseRecords,
                UseOptionalForNullable = opts.UseOptionalForNullable,
                EnableLinqRewrite = opts.EnableLinqRewrite,
                PreferStreamApi = opts.PreferStreamApi,
                EnableParallelProjectPasses = opts.EnableParallelProjectPasses,
                UseValueClass = opts.UseValueClass,
            };

            var pipeline = new ConversionPipeline();
            var result = pipeline.Convert(new ConversionRequest
            {
                SourceCode = sourceCode,
                Options = options,
                FileName = opts.Input
            });

            if (!result.Success)
            {
                if (result.Diagnostics.Count > 0)
                {
                    foreach (var diag in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"[{diag.Severity}] {FormatDiagnostic(diag)}");
                    }
                }

                return 1;
            }

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.GeneratedCode, new System.Text.UTF8Encoding(false));
                Console.WriteLine($"Successfully converted to: {opts.Output}");
            }
            else
            {
                Console.WriteLine(result.GeneratedCode);
            }

            if (opts.Verbose && result.Diagnostics.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Diagnostics:");
                foreach (var diag in result.Diagnostics)
                {
                    var prefix = diag.Severity switch
                    {
                        DiagnosticSeverity.Error => "ERROR",
                        DiagnosticSeverity.Warning => "WARNING",
                        _ => "INFO"
                    };
                    Console.WriteLine($"  [{prefix}] {FormatDiagnostic(diag)}");
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (opts.Verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static async Task<int> ConvertProject(ConvertProjectOptions opts)
    {
        try
        {
            opts.FingerprintSourcePath = opts.Source;
            opts.FingerprintInputRoot = ResolveInputRoot(opts.Source);

            var hasDirectorySource = Directory.Exists(opts.Source);
            var hasProjectSource = File.Exists(opts.Source)
                && (Path.GetExtension(opts.Source).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(opts.Source).Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(opts.Source).Equals(".slnx", StringComparison.OrdinalIgnoreCase));
            if (!hasDirectorySource && !hasProjectSource)
            {
                Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
                return 1;
            }

            if (Directory.Exists(opts.Destination))
            {
                if (!opts.Force)
                {
                    Console.Error.WriteLine($"Error: Destination directory already exists: {opts.Destination}. Use --force to overwrite.");
                    return 1;
                }
            }
            else
            {
                Directory.CreateDirectory(opts.Destination);
            }

            opts.ParsedExtraDependencies = ParseExtraDependencies(opts.ExtraDependencies);

            var paramsCollectionsOptions = BuildParamsCollectionsOptions(
                opts.EnableParamsCollections,
                opts.ParamsCollectionsScope,
                opts.ParamsCollectionsExpandArrays,
                opts.LocalFunctionStrict,
                out var paramsScopeError);
            if (paramsScopeError != null)
            {
                Console.Error.WriteLine($"Error: {paramsScopeError}");
                return 1;
            }

            // ---- Unified preprocess chain -------------------------------------
            // The registry is the single source of truth for pass identity,
            // ordering (locked via RunAfter) and defaults; legacy --no-xxx/enable
            // flags and explicit --enable-pass/--disable-pass only override the
            // enablement; all execution/output details live on the adapters.
            var chain = await CliPreprocessChain.RunAsync(new CliChainRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = opts.Force,
                Verbose = opts.Verbose,
                FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
                VerifyBuild = opts.VerifyBuild,
                VerifyTests = opts.VerifyTests,
                Strict = opts.LocalFunctionStrict,
                TupleNamespace = opts.TupleNamespace,
                MakerOptionsFactory = () => new CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructMakerOptions
                {
                    ForceReadOnlyQualifiedNames = LoadReadOnlyForceOverrides(opts.ReadOnlyOverrides),
                },
                RecordReadOnlyOptionsFactory = () => BuildRecordReadOnlyOptions(
                    opts.RecordReadOnlyOverrides, strict: false),
                JavaKeywordOptionsFactory = () => BuildJavaKeywordOptions(
                    opts.JavaKeywordsOverrides, strict: false),
                MemberDesugarOptionsFactory = () => BuildMemberDesugarOptions(
                    opts.MemberDesugarOverrides, strict: false),
                StructValueSemanticsOptionsFactory = () => BuildStructValueSemanticsOptions(
                    opts.StructValueSemanticsOverrides),
                VarianceAuditOptionsFactory = () => BuildVarianceAuditOptions(
                    opts.VarianceStrict, opts.VarianceNoReportInfo),
                EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions
                {
                    Strict = opts.LocalFunctionStrict,
                    ReportInfo = opts.Verbose,
                },
                LexicalNormalizeOptionsFactory = () =>
                    new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions
                {
                    ReportInfo = opts.Verbose,
                },
                EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions
                {
                    Strict = opts.LocalFunctionStrict,
                },
                UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions
                {
                    Strict = opts.LocalFunctionStrict,
                },
                ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions
                {
                    CollectionExpr = new CSharpToJava.Core.ExpressionDesugar.CollectionExpr.CollectionExprOptions
                    {
                        Strict = opts.LocalFunctionStrict,
                    },
                    // C23 rule-level opt-in: default off keeps pre-C23
                    // byte-identical behavior; the family pass itself (C36)
                    // stays default-on. HandlerPolicy=Error (Q1: Error/Keep
                    // only), FormattableString preserved (0511), expression-
                    // tree canonical lowering deferred (M4/Q4).
                    Interpolations = new CSharpToJava.Core.ExpressionDesugar.Interpolations.InterpolationOptions
                    {
                        Enabled = opts.InterpolationExpand,
                        Strict = opts.LocalFunctionStrict,
                        HandlerPolicy = CSharpToJava.Core.ExpressionDesugar.Interpolations.HandlerUnsupportedPolicy.Error,
                        KeepFormattableString = true,
                        RewriteInExpressionTrees = false,
                        FoldConstants = true,
                    },
                },
                PlatformIsolationOptionsFactory = () => BuildPlatformIsolationOptions(
                    opts.PlatformReportOnly, opts.PlatformMode, opts.PlatformStrict, opts.PlatformConfig),
                LegacyToggles = PassChainFactory.BuildLegacyToggles(opts),
                EnablePassIds = opts.EnablePasses,
                DisablePassIds = opts.DisablePasses,
                EnableDecimalOps = opts.DecimalOpsEffective,
                ParamsCollections = paramsCollectionsOptions,
                ExtensionMappingConfigPath = opts.MappingConfig,
                ExtensionMethodStrict = opts.ExtensionMethodStrict,
                ExtensionOverridesPath = opts.ExtensionOverridesPath,
            });

            if (!chain.Success)
            {
                return 1;
            }

            opts.Source = chain.FinalSourcePath;
            opts.EnabledPassIntermediateDirectoryNames = chain.ExecutedIntermediateDirectoryNames;
            var executedSupportLibraries = chain.ExecutedSupportLibraries;
            if (chain.UsingGotoPreprocessedSource)
            {
                // eliminate-goto historically reassigned (=, not ??) the pointers.
                opts.FingerprintSourcePath = chain.GotoRepointedSourcePath;
                opts.FingerprintInputRoot = chain.GotoRepointedInputRoot;
                opts.UsingGotoPreprocessedSource = true;
            }

            var options = new ConversionOptions
            {
                TargetJavaVersion = Enum.Parse<JavaVersion>(opts.JavaVersion, true),
                TypeMappingConfigPath = opts.MappingConfig,
                JavaMetadataPath = ResolveJavaMetadataPath(opts.MappingConfig),
                GenerateJavaDoc = opts.GenerateJavaDoc,
                UseRecords = opts.UseRecords,
                UseOptionalForNullable = opts.UseOptionalForNullable,
                EnableLinqRewrite = opts.EnableLinqRewrite,
                PreferStreamApi = opts.PreferStreamApi,
                UseValueClass = opts.UseValueClass,
            };

            // Try MSBuild-based loading first for full semantic resolution.
            IReadOnlyList<WorkspaceProject>? workspaceProjects = null;
            try
            {
                using var loader = new SolutionLoader();
                var progress = opts.Verbose ? new Progress<string>(msg => Console.WriteLine(msg)) : null;
                workspaceProjects = await loader.TryOpenAsync(opts.Source, progress);
            }
            catch (Exception ex)
            {
                if (opts.Verbose)
                {
                    Console.WriteLine($"MSBuild loading failed, falling back to directory scan: {ex.Message}");
                }
            }

            if (workspaceProjects != null && workspaceProjects.Count > 0)
            {
                workspaceProjects = NormalizeWorkspaceProjects(workspaceProjects, opts.Verbose);
                // 各 pass 注入的内部支撑库（Cs2jUnsignedMath/Cs2jArray/Cs2jRefOutIn/Cs2jPrelude）：
                // 仅作符号来源，不为其规划 Maven 模块、不转换其源文件。
                // 按本次实际执行的 pass 集合过滤；放在指纹快照之前——helper 文件不再
                // 进入 Java 输出，不应参与缓存键。
                // C19 M3（附录裁决 7）：仅当过滤后仍有用户工程存活时才剔除支撑库。
                // 松散 .cs 输入（无用户 csproj）的 MSBuild 入口只可能解析到注入的
                // Cs2jPrelude 工程，保持其遗留入口行为不变（ConvertProjectGotoIntegrationTests
                // 锁定）；真实 sln/多工程图必有用户工程存活，支撑库照常剔除。
                var supportFilteredWorkspaceProjects = workspaceProjects
                    .Where(p => !PassChainFactory.IsExecutedSupportLibraryProject(
                        executedSupportLibraries, p.FilePath))
                    .ToList();
                if (supportFilteredWorkspaceProjects.Count > 0)
                {
                    workspaceProjects = supportFilteredWorkspaceProjects;
                }
                var inputFingerprintSnapshot = BuildWorkspaceInputFingerprintSnapshot(opts, workspaceProjects);
                if (!opts.NoCache && TryReusePreviousProjectOutputs(opts.Destination, inputFingerprintSnapshot, opts.Verbose))
                {
                    return 0;
                }

                workspaceProjects = FilterUnsupportedWorkspaceProjects(
                    workspaceProjects,
                    opts.Verbose,
                    LoadCompatNamespacePrefixes(opts.MappingConfig));
                if (workspaceProjects.Count == 0)
                {
                    Console.Error.WriteLine("Error: All discovered projects were excluded by platform rules.");
                    return 1;
                }

                if (opts.Verbose)
                {
                    Console.WriteLine($"MSBuild resolved {workspaceProjects.Count} project(s)");
                }

                return await ConvertFromWorkspaceMultiModule(opts, options, workspaceProjects, inputFingerprintSnapshot);
            }

            if (workspaceProjects != null && workspaceProjects.Count == 0)
            {
                if (opts.Verbose)
                {
                    Console.WriteLine("MSBuild loaded the project but produced empty compilations; falling back to directory scan.");
                }
                workspaceProjects = null;
            }

            // Fall back to manual project discovery (no MSBuild SDK available).
            if (ProjectDiscovery.TryResolveProjectEntry(opts.Source, out var entryProject))
            {
                var graph = ProjectDiscovery.LoadProjectGraph(entryProject);
                var inputFingerprintSnapshot = BuildProjectGraphInputFingerprintSnapshot(opts, graph);
                if (!opts.NoCache && TryReusePreviousProjectOutputs(opts.Destination, inputFingerprintSnapshot, opts.Verbose))
                {
                    return 0;
                }

                graph = FilterUnsupportedProjectGraph(
                    graph,
                    opts.Verbose,
                    LoadCompatNamespacePrefixes(opts.MappingConfig));
                if (graph.ProjectsInTopologicalOrder.Count == 0)
                {
                    Console.Error.WriteLine("Error: All discovered projects were excluded by platform rules.");
                    return 1;
                }

                return await ConvertFromProjectGraph(opts, options, graph, inputFingerprintSnapshot);
            }

            var manualInputFingerprintSnapshot = BuildManualInputFingerprintSnapshot(opts);
            if (!opts.NoCache && TryReusePreviousProjectOutputs(opts.Destination, manualInputFingerprintSnapshot, opts.Verbose))
            {
                return 0;
            }

            var outputRoot = Path.Combine(opts.Destination, "src", "main", "java");
            var outputSession = OutputIncrementalWriteSession.Create(opts.Destination, opts.Source);

            if (opts.Force && Directory.Exists(outputRoot) && !outputSession.HasPreviousManifest)
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            if (!Directory.Exists(outputRoot))
            {
                Directory.CreateDirectory(outputRoot);
            }

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(opts.Source, options);
            int successCount = 0;
            int failureCount = 0;
            var planningResults = new List<ConversionResult>();
            var passProfileEntries = new List<PassProfileEntry>();

            AddProjectPassProfileEntry(
                passProfileEntries,
                pipeline.LastProjectPassMetrics,
                results,
                GetSourceName(opts.Source));

            foreach (var result in results)
            {
                if (ShouldAnalyzeForCompatibilityPlanning(result))
                {
                    planningResults.Add(result);
                }

                if (result.FileName == null) continue;

                if (TryWriteConvertedFile(result, opts.Source, outputRoot, outputSession, out var outputPath))
                {
                    successCount++;
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                    }
                }
                else
                {
                    failureCount++;
                    EmitFailureResult(result);
                }
            }

            await WriteSingleModulePom(opts, CreateSingleModulePlan(opts, includeTests: false, planningResults), outputSession);

            GeneratedProjectAssets.WriteRootFiles(opts.Destination, outputSession);
            var passProfileSnapshot = await WritePassProfileSnapshot(opts.Destination, passProfileEntries, outputSession);
            await WriteCanarySummarySnapshot(opts.Destination, opts.Source, results, passProfileSnapshot, outputSession);
            WriteInputFingerprintSnapshot(opts.Destination, manualInputFingerprintSnapshot, outputSession);
            if (opts.LinqReport)
                WriteLinqReport(opts.Destination, pipeline.LastLinqStatistics, outputSession);
            await outputSession.SaveAsync();

            Console.WriteLine();
            Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed");

            return failureCount > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (opts.Verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static async Task<int> ConvertFromProjectGraph(
        ConvertProjectOptions opts,
        ConversionOptions options,
        ProjectGraph graph,
        InputFingerprintSnapshot inputFingerprintSnapshot)
    {
        if (graph.ProjectsInTopologicalOrder.Count == 0)
        {
            Console.Error.WriteLine($"Error: No project discovered from {graph.RootProjectPath}");
            return 1;
        }

        return await ConvertFromProjectGraphMultiModule(opts, options, graph, inputFingerprintSnapshot);

    }

    private static async Task<int> ConvertFromWorkspaceMultiModule(
        ConvertProjectOptions opts,
        ConversionOptions options,
        IReadOnlyList<WorkspaceProject> projects,
        InputFingerprintSnapshot inputFingerprintSnapshot)
    {
        var outputSession = OutputIncrementalWriteSession.Create(opts.Destination, opts.Source);
        // Build module plan from workspace projects.
        var mainProjects = projects.Where(p => !p.IsTestProject).ToList();
        var testProjects = projects.Where(p => p.IsTestProject).ToList();

        if (opts.Force && Directory.Exists(opts.Destination) && !outputSession.HasPreviousManifest)
        {
            ClearDestinationForFreshConversion(opts.Destination, GetPreservedDestinationDirectories(opts));
        }

        int successCount = 0;
        int failureCount = 0;
        int copiedResourceCount = 0;
        var modulePlans = new List<JavaModulePlan>();
        var convertedModuleCount = 1;
        var canaryResults = new List<ConversionResult>();
        var passProfileEntries = new List<PassProfileEntry>();
        CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? workspaceLinqStatistics = null;

        // Sort projects topologically so that dependencies (which may register
        // default factory methods) are converted before the projects that depend on them.
        var sortedProjects = SortProjectsTopologically(projects);

        // Convert each workspace project as its own module.
        foreach (var project in sortedProjects)
        {
            if (!opts.IncludeTests && project.IsTestProject) continue;

            var moduleName = MultiModulePlanner.NormalizeModuleName(project.Name);
            convertedModuleCount++;
            var moduleRoot = Path.Combine(opts.Destination, moduleName);

            var isTest = project.IsTestProject;
            var javaRoot = isTest
                ? Path.Combine(moduleRoot, "src", "test", "java")
                : Path.Combine(moduleRoot, "src", "main", "java");
            var resourcesRoot = isTest
                ? Path.Combine(moduleRoot, "src", "test", "resources")
                : Path.Combine(moduleRoot, "src", "main", "resources");

            Directory.CreateDirectory(javaRoot);
            Directory.CreateDirectory(resourcesRoot);

            // Copy project resources (CopyToOutputDirectory files from .csproj).
            // .resx files become Java ResourceBundle .properties at their embedded
            // logical-name path so SR.getString (ResourceManager → ResourceBundle)
            // resolves them instead of falling back to "{Name}".
            string? rootNamespace = null;
            foreach (var resource in project.ResourceItems)
            {
                if (Path.GetExtension(resource.SourcePath).Equals(".resx", StringComparison.OrdinalIgnoreCase))
                {
                    rootNamespace ??= ResxConverter.ReadRootNamespace(project.FilePath);
                    var logicalName = ResxConverter.ComputeLogicalName(rootNamespace, resource.RelativePath);
                    var destination = Path.Combine(resourcesRoot,
                        logicalName.Replace('.', Path.DirectorySeparatorChar) + ".properties");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var properties = ResxConverter.ConvertToProperties(resource.SourcePath);
                    outputSession.WriteTextFile(destination, properties, OutputIncrementalEntryKind.CopiedResource);
                    copiedResourceCount++;
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Resource(resx): {resource.SourcePath} -> {destination}");
                    }
                    continue;
                }

                var plainDestination = Path.Combine(resourcesRoot, resource.RelativePath);
                if (outputSession.CopyFile(resource.SourcePath, plainDestination, OutputIncrementalEntryKind.CopiedResource))
                {
                    copiedResourceCount++;
                }

                if (opts.Verbose)
                {
                    Console.WriteLine($"Resource: {resource.SourcePath} -> {plainDestination}");
                }
            }

            if (opts.Verbose)
            {
                Console.WriteLine($"Converting module [{(isTest ? "Test" : "Main")}]: {moduleName}");
            }

            var emitFilePaths = new HashSet<string>(
                project.Documents
                    .Where(d => d.FilePath != null)
                    .Select(d => Path.GetFullPath(d.FilePath!)),
                StringComparer.OrdinalIgnoreCase);

            var pipeline = new ProjectConversionPipeline(options);
            var results = await pipeline.ConvertProjectAsync(
                project.Compilation,
                emitFilePaths,
                project.Name,
                project.FilePath,
                project.ProjectReferences,
                project.IsTestProject);
            MergeLinqStatistics(ref workspaceLinqStatistics, pipeline.LastLinqStatistics);
            canaryResults.AddRange(results);
            AddProjectPassProfileEntry(passProfileEntries, pipeline.LastPassMetrics, results, project.Name, moduleName);

            foreach (var result in results)
            {
                if (result.FileName == null) continue;
                if (TryWriteConvertedFile(result, project.Directory, javaRoot, outputSession, out var outputPath))
                {
                    successCount++;
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                    }
                }
                else
                {
                    failureCount++;
                    EmitFailureResult(result);
                }
            }

            var deps = new List<JavaDependency>(WorkspacePlanBuilder.DefaultDependenciesForModule(
                opts.MavenGroupId,
                moduleName));
            foreach (var refPath in project.ProjectReferences)
            {
                var refProject = projects.FirstOrDefault(p =>
                    string.Equals(p.FilePath, refPath, StringComparison.OrdinalIgnoreCase));
                if (refProject != null)
                {
                    var isRefTest = refProject.IsTestProject;
                    deps.Add(WorkspacePlanBuilder.InternalModuleRef(
                        opts.MavenGroupId,
                        MultiModulePlanner.NormalizeModuleName(refProject.Name),
                        isTest && isRefTest ? JavaDependencyScope.Test : JavaDependencyScope.Compile,
                        isTest && isRefTest ? "test-jar" : null));
                }
            }
            deps.AddRange(GetExtraDependencies(opts));

                        var modulePlan = new JavaModulePlan
            {
                ModuleName = moduleName,
                IsTestOnly = isTest,
                SourceSets = isTest ? new JavaSourceSets { TestSources = ["test"] } : new JavaSourceSets(),
                Dependencies = WorkspacePlanBuilder.FilterSelfDependencies(
                    WorkspacePlanBuilder.MergeDependencies(deps),
                    opts.MavenGroupId,
                    moduleName),
                RequiredCompatPacks = [],
                RequiredRuntimeBridges = [],
                ProduceTestJar = isTest,
            };

            modulePlans.Add(modulePlan);
            await WriteMultiModulePom(opts, moduleRoot, modulePlan, outputSession);
        }

        // Post-pass: scan generated Java imports to discover implicit module
        // dependencies that were not captured by C# project references (e.g.
        // framework types like System.Uri mapped to another module).
        var moduleRoots = modulePlans
            .Select(mp => Path.Combine(opts.Destination, mp.ModuleName))
            .ToList();
        var implicitDeps = DiscoverImplicitModuleDependencies(modulePlans, moduleRoots, opts.MavenGroupId);
        if (implicitDeps.Count > 0)
        {
            // Re-generate POM files for modules whose dependencies changed.
            foreach (var kvp in implicitDeps)
            {
                var modulePlan = kvp.Key;
                var additionalDeps = kvp.Value;
                var updatedDeps = new List<JavaDependency>(modulePlan.Dependencies);
                updatedDeps.AddRange(additionalDeps);
                var newPlan = new JavaModulePlan
                {
                    ModuleName = modulePlan.ModuleName,
                    IsTestOnly = modulePlan.IsTestOnly,
                    SourceSets = modulePlan.SourceSets,
                    Dependencies = WorkspacePlanBuilder.FilterSelfDependencies(
                        WorkspacePlanBuilder.MergeDependencies(updatedDeps),
                        opts.MavenGroupId,
                        modulePlan.ModuleName),
                    RequiredCompatPacks = modulePlan.RequiredCompatPacks,
                    RequiredRuntimeBridges = modulePlan.RequiredRuntimeBridges,
                    ProduceTestJar = modulePlan.ProduceTestJar,
                };
                var idx = modulePlans.IndexOf(modulePlan);
                modulePlans[idx] = newPlan;
                var mr = Path.Combine(opts.Destination, newPlan.ModuleName);
                await WriteMultiModulePom(opts, mr, newPlan, outputSession);
            }
        }

        var workspacePlan = BuildWorkspacePlan(opts, modulePlans);
        await WriteParentPom(opts, workspacePlan, outputSession);
        await WriteWorkspacePlanManifest(opts.Destination, workspacePlan, outputSession);

        GeneratedProjectAssets.WriteRootFiles(opts.Destination, outputSession);
        var passProfileSnapshot = await WritePassProfileSnapshot(opts.Destination, passProfileEntries, outputSession);
        await WriteCanarySummarySnapshot(opts.Destination, opts.Source, canaryResults, passProfileSnapshot, outputSession);
        WriteInputFingerprintSnapshot(opts.Destination, inputFingerprintSnapshot, outputSession);
        if (opts.LinqReport)
            WriteLinqReport(opts.Destination, workspaceLinqStatistics, outputSession);
        await outputSession.SaveAsync();

        Console.WriteLine();
        Console.WriteLine($"Conversion complete (MSBuild): {successCount} succeeded, {failureCount} failed, {copiedResourceCount} resources copied, modules={convertedModuleCount}");

        return failureCount > 0 ? 1 : 0;
    }

    private static async Task<int> ConvertFromProjectGraphMultiModule(
        ConvertProjectOptions opts,
        ConversionOptions options,
        ProjectGraph graph,
        InputFingerprintSnapshot inputFingerprintSnapshot)
    {
        var plan = MultiModulePlanner.Build(graph, opts.IncludeTests);
        if (plan.ModulesInBuildOrder.Count == 0)
        {
            Console.Error.WriteLine("Error: No modules planned for conversion.");
            return 1;
        }

        var outputSession = OutputIncrementalWriteSession.Create(opts.Destination, opts.Source);

        if (opts.Force && Directory.Exists(opts.Destination) && !outputSession.HasPreviousManifest)
        {
            ClearDestinationForFreshConversion(opts.Destination, GetPreservedDestinationDirectories(opts));
        }

        int successCount = 0;
        int failureCount = 0;
        int copiedResourceCount = 0;
        var modulePlans = new List<JavaModulePlan>();
        var canaryResults = new List<ConversionResult>();
        var passProfileEntries = new List<PassProfileEntry>();
        CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? projectGraphLinqStatistics = null;

        var pipeline = new ConversionPipeline();

        foreach (var module in plan.ModulesInBuildOrder)
        {
            var moduleRoot = Path.Combine(opts.Destination, module.Name);
            var mainJavaRoot = Path.Combine(moduleRoot, "src", "main", "java");
            var testJavaRoot = Path.Combine(moduleRoot, "src", "test", "java");
            var mainResourcesRoot = Path.Combine(moduleRoot, "src", "main", "resources");
            var testResourcesRoot = Path.Combine(moduleRoot, "src", "test", "resources");

            if (!module.IsTestOnly)
            {
                Directory.CreateDirectory(mainJavaRoot);
                Directory.CreateDirectory(mainResourcesRoot);
            }

            if (module.HasTestSources)
            {
                Directory.CreateDirectory(testJavaRoot);
                Directory.CreateDirectory(testResourcesRoot);

                // Write JUnit Platform configuration with per-test timeout to prevent hangs
                var junitPlatformProps = Path.Combine(testResourcesRoot, "junit-platform.properties");
                outputSession.WriteTextFile(junitPlatformProps, "junit.jupiter.execution.timeout.default = 60s\n", OutputIncrementalEntryKind.BuildFile);
            }

            if (opts.Verbose)
            {
                Console.WriteLine($"Converting module: {module.Name}");
            }

            var moduleResults = new List<ConversionResult>();

            foreach (var assignment in module.Assignments)
            {
                var targetJavaRoot = assignment.AsTestSources ? testJavaRoot : mainJavaRoot;
                var targetResourcesRoot = assignment.AsTestSources ? testResourcesRoot : mainResourcesRoot;

                if (opts.Verbose)
                {
                    var slot = assignment.AsTestSources ? "test" : "main";
                    Console.WriteLine($"  Project [{assignment.Project.Kind}] -> {slot}: {assignment.Project.Name}");
                }

                var semanticContextDirs = GetReferencedProjectDirectories(assignment.Project, graph);
                var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                    assignment.Project.ProjectDirectory,
                    options,
                    semanticContextDirs,
                    assignment.Project.Name,
                    assignment.Project.ProjectFilePath,
                    assignment.Project.ProjectReferences,
                    assignment.Project.Kind == ProjectKind.Test);
                MergeLinqStatistics(ref projectGraphLinqStatistics, pipeline.LastLinqStatistics);
                canaryResults.AddRange(results);
                moduleResults.AddRange(results.Where(result => !string.IsNullOrEmpty(result.GeneratedCode)));
                AddProjectPassProfileEntry(passProfileEntries, pipeline.LastProjectPassMetrics, results, assignment.Project.Name, module.Name);
                foreach (var result in results)
                {
                    if (result.FileName == null) continue;

                    if (TryWriteConvertedFile(result, assignment.Project.ProjectDirectory, targetJavaRoot, outputSession, out var outputPath))
                    {
                        successCount++;
                        if (opts.Verbose)
                        {
                            Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                            foreach (var diag in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info))
                            {
                                Console.WriteLine($"  [INFO] {FormatDiagnostic(diag)}");
                            }
                        }
                    }
                    else
                    {
                        failureCount++;
                        EmitFailureResult(result);
                    }
                }

                foreach (var resource in assignment.Project.ResourceItems)
                {
                    var destination = Path.Combine(targetResourcesRoot, resource.RelativePath);
                    if (outputSession.CopyFile(resource.SourcePath, destination, OutputIncrementalEntryKind.CopiedResource))
                    {
                        copiedResourceCount++;
                    }

                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Resource: {resource.SourcePath} -> {destination}");
                    }
                }
            }

            var modulePlan = PlannedModuleToJavaModulePlan(
                module,
                opts.MavenGroupId,
                GetExtraDependencies(opts));
            modulePlans.Add(modulePlan);
            await WriteMultiModulePom(opts, moduleRoot, modulePlan, outputSession);
        }

        // Post-pass: scan generated Java imports to discover implicit module
        // dependencies that were not captured by C# project references (e.g.
        // framework types like System.Uri mapped to another module).
        var moduleRoots2 = modulePlans
            .Select(mp => Path.Combine(opts.Destination, mp.ModuleName))
            .ToList();
        var implicitDeps2 = DiscoverImplicitModuleDependencies(modulePlans, moduleRoots2, opts.MavenGroupId);
        if (implicitDeps2.Count > 0)
        {
            foreach (var kvp in implicitDeps2)
            {
                var modulePlan = kvp.Key;
                var additionalDeps = kvp.Value;
                var updatedDeps = new List<JavaDependency>(modulePlan.Dependencies);
                updatedDeps.AddRange(additionalDeps);
                var newPlan = new JavaModulePlan
                {
                    ModuleName = modulePlan.ModuleName,
                    IsTestOnly = modulePlan.IsTestOnly,
                    SourceSets = modulePlan.SourceSets,
                    Dependencies = WorkspacePlanBuilder.FilterSelfDependencies(
                        WorkspacePlanBuilder.MergeDependencies(updatedDeps),
                        opts.MavenGroupId,
                        modulePlan.ModuleName),
                    RequiredCompatPacks = modulePlan.RequiredCompatPacks,
                    RequiredRuntimeBridges = modulePlan.RequiredRuntimeBridges,
                    ProduceTestJar = modulePlan.ProduceTestJar,
                };
                var idx = modulePlans.IndexOf(modulePlan);
                modulePlans[idx] = newPlan;
                var mr = Path.Combine(opts.Destination, newPlan.ModuleName);
                await WriteMultiModulePom(opts, mr, newPlan, outputSession);
            }
        }

        var workspacePlan = BuildWorkspacePlan(opts, modulePlans);
        await WriteParentPom(opts, workspacePlan, outputSession);
        await WriteWorkspacePlanManifest(opts.Destination, workspacePlan, outputSession);

        GeneratedProjectAssets.WriteRootFiles(opts.Destination, outputSession);
        var passProfileSnapshot = await WritePassProfileSnapshot(opts.Destination, passProfileEntries, outputSession);
        await WriteCanarySummarySnapshot(opts.Destination, opts.Source, canaryResults, passProfileSnapshot, outputSession);
        WriteInputFingerprintSnapshot(opts.Destination, inputFingerprintSnapshot, outputSession);
        if (opts.LinqReport)
            WriteLinqReport(opts.Destination, projectGraphLinqStatistics, outputSession);
        await outputSession.SaveAsync();

        Console.WriteLine();
        Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed, {copiedResourceCount} resources copied, modules={plan.ModulesInBuildOrder.Count}");

        return failureCount > 0 ? 1 : 0;
    }

    private static string MakeUniqueModuleName(string preferredName, IEnumerable<string> existingNames)
    {
        var used = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (used.Add(preferredName))
        {
            return preferredName;
        }

        var index = 2;
        while (true)
        {
            var candidate = preferredName + "-" + index;
            if (used.Add(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static bool TryWriteConvertedFile(
        ConversionResult result,
        string sourceRoot,
        string outputRoot,
        OutputIncrementalWriteSession outputSession,
        out string outputPath)
    {
        outputPath = string.Empty;

        if (string.IsNullOrEmpty(result.GeneratedCode) || string.IsNullOrEmpty(result.FileName))
        {
            return false;
        }

        bool isGeneratedFile = !string.IsNullOrEmpty(result.Package) &&
            !Path.IsPathFullyQualified(result.FileName) &&
            !result.FileName.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase);

        if (isGeneratedFile && !string.IsNullOrEmpty(result.Package))
        {
            var packageDir = result.Package.Replace('.', Path.DirectorySeparatorChar);
            outputPath = Path.Combine(outputRoot, packageDir, result.FileName);
            if (!result.FileName.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = Path.ChangeExtension(outputPath, ".java");
            }
        }
        else
        {
            var relativePath = Path.IsPathFullyQualified(result.FileName)
                ? Path.GetRelativePath(sourceRoot, result.FileName)
                : result.FileName;
            outputPath = Path.Combine(outputRoot, Path.ChangeExtension(relativePath, ".java"));
        }
        var generatedCode = result.GeneratedCode;
        outputSession.WriteTextFile(outputPath, generatedCode, OutputIncrementalEntryKind.GeneratedSource);
        return true;
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    internal static void ClearDestinationForFreshConversion(
        string destinationRoot,
        IReadOnlySet<string>? preservedDirectoryNames = null)
    {
        if (!Directory.Exists(destinationRoot))
        {
            Directory.CreateDirectory(destinationRoot);
            return;
        }

        var fullDestinationPath = Path.GetFullPath(destinationRoot);
        if (string.IsNullOrWhiteSpace(fullDestinationPath)
            || Path.GetPathRoot(fullDestinationPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(fullDestinationPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException($"Refusing to clear unsafe destination path: {destinationRoot}");
        }

        foreach (var file in Directory.EnumerateFiles(fullDestinationPath))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(fullDestinationPath))
        {
            if (preservedDirectoryNames?.Contains(Path.GetFileName(directory)) == true)
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlySet<string>? GetPreservedDestinationDirectories(ConvertProjectOptions opts)
    {
        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // node_modules holds npm dependencies, not generated artifacts: preserving it
        // across regenerations avoids a full npm install on every conversion round.
        preserved.Add("node_modules");
        // Preserve the intermediate tree of every pass the resolved chain enabled;
        // the set is populated by ConvertProject from the registry-driven pass plan.
        if (opts.EnabledPassIntermediateDirectoryNames != null)
        {
            foreach (var intermediateDirectoryName in opts.EnabledPassIntermediateDirectoryNames)
            {
                preserved.Add(intermediateDirectoryName);
            }
        }

        return preserved.Count > 0 ? preserved : null;
    }

    private static IReadOnlyList<string> GetReferencedProjectDirectories(DiscoveredProject project, ProjectGraph graph)
    {
        var byProjectPath = graph.ProjectsInTopologicalOrder
            .ToDictionary(p => p.ProjectFilePath, p => p, StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(project.ProjectReferences);

        while (queue.Count > 0)
        {
            var referencePath = queue.Dequeue();
            if (!visited.Add(referencePath))
            {
                continue;
            }

            if (!byProjectPath.TryGetValue(referencePath, out var referencedProject))
            {
                continue;
            }

            result.Add(referencedProject.ProjectDirectory);
            foreach (var next in referencedProject.ProjectReferences)
            {
                queue.Enqueue(next);
            }
        }

        return result.ToList();
    }

    private static async Task<int> LocalFunctionLiftAsync(LocalFunctionLiftOptions opts)
    {
        var liftOpts = new CoreLocalFunctionLiftOptions(
            CapturesAtEnd: opts.CapturesAtEnd,
            AllowRefInAsyncCallers: opts.AllowRefInAsync,
            AllowInterfaceHost: opts.AllowInterfaceHost,
            Verbose: opts.Verbose);
        var preprocessor = new LocalFunctionLiftPreprocessor();

        // Single-file mode
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var result = preprocessor.Preprocess(sourceCode, model: null, liftOpts);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose)
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.Message}");

            if (result.Statistics.LocalFunctionsResidual > 0 && opts.Strict)
                return 1;
            return result.Diagnostics.Any(d => d.Severity == LocalFunctionLiftSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectLocalFunctionLiftPreprocessor.PreprocessAsync(
            new ProjectLocalFunctionLiftRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                Options = liftOpts,
                FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                VerifyBuild = opts.VerifyBuild,
                VerifyTests = opts.VerifyTests,
                Strict = opts.Strict,
            });

        Console.WriteLine(
            $"local-function-lift: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.LocalFunctionsLifted} lifted, " +
            $"{preprocessResult.Statistics.LocalFunctionsSkipped} skipped, " +
            $"{preprocessResult.Statistics.LocalFunctionsResidual} residual");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
            Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        if (opts.Strict && preprocessResult.Diagnostics.Any(d => d.Severity == LocalFunctionLiftSeverity.Warning))
            return 1;
        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> AnalyzeProject(AnalyzeOptions opts)
    {
        try
        {
            if (!Directory.Exists(opts.Source))
            {
                Console.Error.WriteLine($"Error: Source directory not found: {opts.Source}");
                return 1;
            }

            TypeMappingRegistry registry;
            try
            {
                registry = new TypeMappingRegistry(opts.MappingConfig);
            }
            catch (TypeMappingConfigurationException ex)
            {
                Console.Error.WriteLine($"Configuration Error: {ex.Message}");
                Console.Error.WriteLine($"Expected location: {ex.ConfigPath}");
                return 1;
            }

            // Analyze project
            var csFiles = Directory.GetFiles(opts.Source, "*.cs", SearchOption.AllDirectories);

            var typeUsage = new Dictionary<string, HashSet<string>>();
            var missingMappings = new HashSet<string>();

            Console.WriteLine("Analyzing project...");
            Console.WriteLine();

            foreach (var file in csFiles)
            {
                try
                {
                    var sourceCode = await File.ReadAllTextAsync(file);
                    var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
                    var root = tree.GetRoot();

                    // Collect type usage
                    var typeNames = root.DescendantNodes()
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax>()
                        .Select(t => t.ToString())
                        .Distinct();

                    foreach (var typeName in typeNames)
                    {
                        var mappedType = registry.MapType(typeName);
                        if (mappedType == typeName)
                        {
                            // 可能缺少映射
                            if (typeName.Contains(".") && !typeName.StartsWith("System."))
                            {
                                missingMappings.Add(typeName);
                            }
                        }

                        if (!typeUsage.ContainsKey(typeName))
                        {
                            typeUsage[typeName] = new HashSet<string>();
                        }
                        typeUsage[typeName].Add(file);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Could not analyze {file}: {ex.Message}");
                }
            }

            // 生成报告
            Console.WriteLine("Type Mapping Report");
            Console.WriteLine("===================");
            Console.WriteLine();

            Console.WriteLine("Most Common Types:");
            foreach (var (type, files) in typeUsage.OrderByDescending(kvp => kvp.Value.Count).Take(20))
            {
                var mapped = registry.MapType(type);
                var hasMapping = mapped != type;
                Console.WriteLine($"  {type} -> {mapped} {(hasMapping ? "OK" : "?")} ({files.Count} files)");
            }

            if (missingMappings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Potential Missing Mappings:");
                foreach (var type in missingMappings.OrderBy(t => t))
                {
                    Console.WriteLine($"  {type}");
                }
            }

            // 保存报告
            if (opts.Report != null)
            {
                var report = new
                {
                    GeneratedAt = DateTime.UtcNow,
                    TypeUsage = typeUsage.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new { Count = kvp.Value.Count, Files = kvp.Value.ToList() }
                    ),
                    MissingMappings = missingMappings.ToList()
                };

                var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(opts.Report, json, new System.Text.UTF8Encoding(false));
                Console.WriteLine();
                Console.WriteLine($"Report saved to: {opts.Report}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static Task WriteSingleModulePom(
        ConvertProjectOptions opts,
        JavaModulePlan modulePlan,
        OutputIncrementalWriteSession outputSession)
    {
        var plan = BuildWorkspacePlan(opts, [modulePlan]);

        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateRootBuildFile(plan, false);
        var pomPath = Path.Combine(opts.Destination, generator.BuildFileName);
        outputSession.WriteTextFile(pomPath, pomContent, OutputIncrementalEntryKind.BuildFile);
        return WriteWorkspacePlanManifest(opts.Destination, plan, outputSession);
    }

    private static JavaModulePlan CreateSingleModulePlan(
        ConvertProjectOptions opts,
        bool includeTests,
        IReadOnlyList<ConversionResult> convertedResults)
    {
        var moduleName = new DirectoryInfo(opts.Destination).Name;
        var deps = new List<JavaDependency>();
        deps.AddRange(WorkspacePlanBuilder.DefaultDependenciesForModule(
            opts.MavenGroupId,
            moduleName));
        deps.AddRange(GetExtraDependencies(opts));

        return new JavaModulePlan
        {
            ModuleName = moduleName,
            IsTestOnly = false,
            SourceSets = includeTests ? new JavaSourceSets { TestSources = ["test"] } : new JavaSourceSets(),
            Dependencies = WorkspacePlanBuilder.FilterSelfDependencies(
                WorkspacePlanBuilder.MergeDependencies(deps),
                opts.MavenGroupId,
                moduleName),
            RequiredCompatPacks = [],
            RequiredRuntimeBridges = [],
        };
    }

    private static bool ShouldAnalyzeForCompatibilityPlanning(ConversionResult result)
    {
        return result.Success
            && !string.IsNullOrEmpty(result.GeneratedCode);
    }

    private static Task WriteMultiModulePom(
        ConvertProjectOptions opts,
        string moduleRoot,
        JavaModulePlan modulePlan,
        OutputIncrementalWriteSession outputSession)
    {
        var plan = BuildWorkspacePlan(opts, [modulePlan]);

        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateModuleBuildFile(plan, modulePlan);
        var pomPath = Path.Combine(moduleRoot, generator.BuildFileName);
        outputSession.WriteTextFile(pomPath, pomContent, OutputIncrementalEntryKind.BuildFile);
        return Task.CompletedTask;
    }

    private static Task WriteParentPom(
        ConvertProjectOptions opts,
        JavaWorkspacePlan plan,
        OutputIncrementalWriteSession outputSession)
    {
        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateRootBuildFile(plan, true);
        var pomPath = Path.Combine(opts.Destination, generator.BuildFileName);
        outputSession.WriteTextFile(pomPath, pomContent, OutputIncrementalEntryKind.BuildFile);
        return Task.CompletedTask;
    }

    private static JavaWorkspacePlan BuildWorkspacePlan(ConvertProjectOptions opts, IReadOnlyList<JavaModulePlan> modulePlans)
    {
        var builder = new WorkspacePlanBuilder()
            .GroupId(opts.MavenGroupId)
            .ArtifactId(new DirectoryInfo(opts.Destination).Name)
            .Version(opts.MavenVersion)
            .JavaVersion(opts.JavaVersion);

        foreach (var modulePlan in modulePlans)
        {
            builder.AddModule(modulePlan);
        }

        return builder.Build();
    }

    private static Task WriteWorkspacePlanManifest(
        string destinationRoot,
        JavaWorkspacePlan plan,
        OutputIncrementalWriteSession outputSession)
    {
        var serializer = new JavaWorkspacePlanJsonSerializer();
        var manifestPath = Path.Combine(destinationRoot, "cs2j-workspace-plan.json");
        outputSession.WriteTextFile(manifestPath, serializer.Serialize(plan), OutputIncrementalEntryKind.WorkspaceManifest);
        return Task.CompletedTask;
    }

    private static Task<PassProfileSnapshot?> WritePassProfileSnapshot(
        string destinationRoot,
        IReadOnlyList<PassProfileEntry> entries,
        OutputIncrementalWriteSession outputSession)
    {
        if (entries.Count == 0)
        {
            return Task.FromResult<PassProfileSnapshot?>(null);
        }

        var snapshot = PassProfileSnapshotBuilder.Build(entries);
        var serializer = new PassProfileSnapshotJsonSerializer();
        var snapshotPath = Path.Combine(destinationRoot, "cs2j-pass-profile.json");
        outputSession.WriteTextFile(snapshotPath, serializer.Serialize(snapshot), OutputIncrementalEntryKind.PassProfile);
        return Task.FromResult<PassProfileSnapshot?>(snapshot);
    }

    private static Task WriteCanarySummarySnapshot(
        string destinationRoot,
        string sourcePath,
        IReadOnlyList<ConversionResult> results,
        PassProfileSnapshot? passProfileSnapshot,
        OutputIncrementalWriteSession outputSession)
    {
        if (results.Count == 0)
        {
            return Task.CompletedTask;
        }

        var snapshot = CanarySummarySnapshotBuilder.Build(GetSourceName(sourcePath), sourcePath, results, passProfileSnapshot);
        var serializer = new CanarySummarySnapshotJsonSerializer();
        var summaryPath = Path.Combine(destinationRoot, "cs2j-canary-summary.json");
        outputSession.WriteTextFile(summaryPath, serializer.Serialize(snapshot), OutputIncrementalEntryKind.CanarySummary);
        return Task.CompletedTask;
    }

    private static void WriteInputFingerprintSnapshot(
        string destinationRoot,
        InputFingerprintSnapshot snapshot,
        OutputIncrementalWriteSession outputSession)
    {
        var serializer = new InputFingerprintSnapshotJsonSerializer();
        var snapshotPath = Path.Combine(destinationRoot, InputFingerprintSnapshot.FileName);
        outputSession.WriteTextFile(snapshotPath, serializer.Serialize(snapshot), OutputIncrementalEntryKind.InputFingerprint);
    }

    private static void WriteLinqReport(
        string destinationRoot,
        CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? stats,
        OutputIncrementalWriteSession outputSession)
    {
        if (stats == null)
        {
            return;
        }

        var report = new System.Text.Json.Nodes.JsonObject
        {
            ["desugaredQueryCount"] = stats.DesugaredQueryCount,
            ["rewrittenChainCount"] = stats.RewrittenChainCount,
            ["rewrittenMethodCount"] = stats.RewrittenMethodCount,
            ["skippedChainCount"] = stats.SkippedChains.Count,
        };

        if (stats.SkippedChains.Count > 0)
        {
            var skippedArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var skip in stats.SkippedChains)
            {
                skippedArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["reason"] = skip.Reason.ToString(),
                    ["line"] = skip.LineNumber,
                    ["method"] = skip.MethodName,
                    ["message"] = skip.Message,
                });
            }
            report["skippedChains"] = skippedArray;
        }

        var uncovered = stats.GetUncoveredOperatorsByFrequency();
        if (uncovered.Count > 0)
        {
            var uncoveredArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var kv in uncovered)
            {
                uncoveredArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["operator"] = kv.Key,
                    ["occurrences"] = kv.Value,
                });
            }
            report["uncoveredOperators"] = uncoveredArray;
        }

        var json = report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var reportPath = Path.Combine(destinationRoot, "cs2j-linq-report.json");
        outputSession.WriteTextFile(reportPath, json, OutputIncrementalEntryKind.Report);
    }

    private static void MergeLinqStatistics(
        ref CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? aggregated,
        CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? incoming)
    {
        if (incoming == null)
        {
            return;
        }

        aggregated ??= new CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics();
        aggregated.MergeFrom(incoming);
    }

    private static bool TryReusePreviousProjectOutputs(
        string destinationRoot,
        InputFingerprintSnapshot currentSnapshot,
        bool verbose)
    {
        var snapshotPath = Path.Combine(destinationRoot, InputFingerprintSnapshot.FileName);
        var outputManifestPath = Path.Combine(destinationRoot, OutputIncrementalWriteSession.ManifestFileName);
        if (!File.Exists(snapshotPath) || !File.Exists(outputManifestPath))
        {
            return false;
        }

        try
        {
            var serializer = new InputFingerprintSnapshotJsonSerializer();
            var previousSnapshot = serializer.Deserialize(File.ReadAllText(snapshotPath));
            if (!currentSnapshot.Matches(previousSnapshot))
            {
                return false;
            }

            Console.WriteLine("Inputs unchanged; reusing existing outputs.");
            if (verbose)
            {
                Console.WriteLine($"Input fingerprint matched: {snapshotPath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.WriteLine($"Failed to evaluate input fingerprint snapshot, continuing with conversion: {ex.Message}");
            }

            return false;
        }
    }

    private static InputFingerprintSnapshot BuildWorkspaceInputFingerprintSnapshot(
        ConvertProjectOptions opts,
        IReadOnlyList<WorkspaceProject> projects)
    {
        var commonRoot = opts.UsingGotoPreprocessedSource
            ? GetFingerprintInputRoot(opts)
            : GetCommonRoot(projects.Select(project => project.Directory).Append(GetFingerprintInputRoot(opts)));
        return BuildInputFingerprintSnapshot(opts, EnumerateInputFiles(commonRoot, opts.Destination));
    }

    private static InputFingerprintSnapshot BuildProjectGraphInputFingerprintSnapshot(
        ConvertProjectOptions opts,
        ProjectGraph graph)
    {
        var commonRoot = opts.UsingGotoPreprocessedSource
            ? GetFingerprintInputRoot(opts)
            : GetCommonRoot(graph.ProjectsInTopologicalOrder.Select(project => project.ProjectDirectory).Append(GetFingerprintInputRoot(opts)));
        return BuildInputFingerprintSnapshot(opts, EnumerateInputFiles(commonRoot, opts.Destination));
    }

    private static InputFingerprintSnapshot BuildManualInputFingerprintSnapshot(ConvertProjectOptions opts)
    {
        var sourceRoot = GetFingerprintInputRoot(opts);
        return BuildInputFingerprintSnapshot(opts, EnumerateInputFiles(sourceRoot, opts.Destination));
    }

    /// <summary>
    /// Sorts workspace projects in topological order (dependencies first) so that
    /// factory methods and other cross-project state registered during dependency
    /// conversion are available when converting dependent projects.
    /// </summary>
    private static IReadOnlyList<WorkspaceProject> SortProjectsTopologically(
        IReadOnlyList<WorkspaceProject> projects)
    {
        var projectByPath = projects.ToDictionary(
            p => Path.GetFullPath(p.FilePath),
            p => p,
            StringComparer.OrdinalIgnoreCase);

        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WorkspaceProject>();

        foreach (var project in projects)
        {
            Visit(project);
        }

        return result;

        void Visit(WorkspaceProject project)
        {
            var path = Path.GetFullPath(project.FilePath);
            if (visited.TryGetValue(path, out var state))
            {
                if (state == 2) return; // Already fully processed
                // state == 1 means cycle — break it gracefully
                return;
            }

            visited[path] = 1; // Currently visiting

            // Visit dependencies first
            foreach (var refPath in project.ProjectReferences)
            {
                var fullPath = Path.GetFullPath(refPath);
                if (projectByPath.TryGetValue(fullPath, out var depProject))
                {
                    Visit(depProject);
                }
            }

            visited[path] = 2; // Fully processed
            result.Add(project);
        }
    }

    /// <summary>
    /// Loads the explicit namespace mappings (compat layer coverage) from the
    /// type-mapping config so the project exclusion planner can allow Windows-only
    /// projects whose platform namespaces are backed by a compatibility package.
    /// Returns null when the config cannot be loaded, which keeps the default
    /// conservative exclusion behavior.
    /// </summary>
    private static IReadOnlyCollection<string>? LoadCompatNamespacePrefixes(string? mappingConfig)
    {
        try
        {
            return new TypeMappingRegistry(mappingConfig).GetExplicitNamespaceMappings();
        }
        catch (TypeMappingConfigurationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Build the C42 platform-isolation options from the CLI flags. Report mode
    /// is the M1 default; <c>--platform-report-only</c> forces it. Non-Report
    /// modes are parsed here and rejected by the adapter until M2/M3.
    /// </summary>
    private static CSharpToJava.Core.PlatformIsolation.PlatformIsolationOptions BuildPlatformIsolationOptions(
        bool reportOnly,
        string mode,
        bool strict,
        string? configPath)
    {
        var effectiveMode = reportOnly
            ? CSharpToJava.Core.PlatformIsolation.PlatformIsolationMode.Report
            : Enum.Parse<CSharpToJava.Core.PlatformIsolation.PlatformIsolationMode>(mode, ignoreCase: true);

        return new CSharpToJava.Core.PlatformIsolation.PlatformIsolationOptions
        {
            Mode = effectiveMode,
            Strict = strict,
            ConfigPath = configPath,
        };
    }

    private static ProjectGraph FilterUnsupportedProjectGraph(
        ProjectGraph graph,
        bool verbose,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        var exclusions = ProjectConversionExclusionPlanner.Plan(graph, compatNamespacePrefixes);
        if (exclusions.Count == 0)
        {
            return graph;
        }

        WriteProjectExclusionSummary(exclusions.Values, verbose);

        var includedPaths = new HashSet<string>(
            graph.ProjectsInTopologicalOrder
                .Select(project => Path.GetFullPath(project.ProjectFilePath))
                .Where(path => !exclusions.ContainsKey(path)),
            StringComparer.OrdinalIgnoreCase);

        var filteredProjects = graph.ProjectsInTopologicalOrder
            .Where(project => includedPaths.Contains(Path.GetFullPath(project.ProjectFilePath)))
            .Select(project => new DiscoveredProject
            {
                Name = project.Name,
                ProjectFilePath = project.ProjectFilePath,
                ProjectDirectory = project.ProjectDirectory,
                Kind = project.Kind,
                ProjectReferences = project.ProjectReferences
                    .Select(Path.GetFullPath)
                    .Where(includedPaths.Contains)
                    .ToList(),
                ResourceItems = project.ResourceItems,
            })
            .ToList();

        return new ProjectGraph
        {
            RootProjectPath = graph.RootProjectPath,
            ProjectsInTopologicalOrder = filteredProjects,
        };
    }

    private static IReadOnlyList<WorkspaceProject> NormalizeWorkspaceProjects(
        IReadOnlyList<WorkspaceProject> projects,
        bool verbose)
    {
        var groupedProjects = projects
            .GroupBy(project => Path.GetFullPath(project.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groupedProjects.All(group => group.Count() == 1))
        {
            return projects;
        }

        var normalizedProjects = new List<WorkspaceProject>(groupedProjects.Count);
        foreach (var group in groupedProjects)
        {
            // Multi-targeted projects (e.g. net472;net6.0-windows) appear once per TFM.
            // MSBuildWorkspace does not guarantee a stable TFM order, so pick the
            // highest-ranked TFM deterministically. Windows desktop TFMs are preferred
            // because their reference assemblies (WindowsDesktop shared framework)
            // resolve System.Drawing/Windows.Forms types to the cs2j compat surface,
            // while net4xx resolves them to the real framework assemblies whose
            // TypeMappings entries (e.g. System.Drawing.Color -> java.awt.Color) do
            // not match the compat types.
            var selectedProject = group
                .OrderByDescending(project => TargetFrameworkRank(project.Name))
                .First();
            normalizedProjects.Add(selectedProject);

            if (verbose && group.Count() > 1)
            {
                var names = string.Join(", ", group.Select(project => project.Name));
                Console.WriteLine($"Collapsing multi-target workspace project '{selectedProject.FilePath}' to '{selectedProject.Name}' from [{names}]");
            }
        }

        return normalizedProjects;
    }

    /// <summary>
    /// Deterministic preference rank for a TFM-suffixed Roslyn project name
    /// (e.g. "GraphViewerGDI (net6.0-windows)"). Windows variants rank above
    /// plain framework TFMs of the same version; higher versions rank higher.
    /// Unknown names rank 0 and keep their original (stable) order.
    /// </summary>
    internal static int TargetFrameworkRank(string projectName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            projectName,
            @"\((?<tfm>net(?:coreapp)?\d+(?:\.\d+)?(?:-windows)?)\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return 0;
        }

        var tfm = match.Groups["tfm"].Value;
        var versionText = tfm
            .Replace("netcoreapp", "net", StringComparison.OrdinalIgnoreCase)
            .TrimStart("net".ToCharArray());
        var versionEnd = versionText.IndexOf('-');
        if (versionEnd >= 0)
        {
            versionText = versionText[..versionEnd];
        }

        // Normalize the digit part to a comparable value: "6.0" -> 6.0, "472" -> 4.72,
        // "48" -> 4.8, "2.0" -> 2.0. .NET Framework TFMs (net4xx) are written without
        // a dot in the TFM name but represent 4.<xx>.
        double value;
        if (versionText.Contains('.', StringComparison.Ordinal))
        {
            if (!double.TryParse(versionText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return 0;
            }
        }
        else if (versionText.Length > 1 && versionText[0] == '4'
                 && int.TryParse(versionText[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var frameworkMinor))
        {
            value = 4 + frameworkMinor / Math.Pow(10, versionText.Length - 1);
        }
        else if (!double.TryParse(versionText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return 0;
        }

        var rank = (int)(value * 100);
        if (tfm.EndsWith("-windows", StringComparison.OrdinalIgnoreCase))
        {
            rank += 1000;
        }

        return rank;
    }

    private static IReadOnlyList<WorkspaceProject> FilterUnsupportedWorkspaceProjects(
        IReadOnlyList<WorkspaceProject> projects,
        bool verbose,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        var exclusions = ProjectConversionExclusionPlanner.Plan(projects, compatNamespacePrefixes);
        if (exclusions.Count == 0)
        {
            return projects;
        }

        WriteProjectExclusionSummary(exclusions.Values, verbose);

        var includedPaths = new HashSet<string>(
            projects
                .Select(project => Path.GetFullPath(project.FilePath))
                .Where(path => !exclusions.ContainsKey(path)),
            StringComparer.OrdinalIgnoreCase);

        return projects
            .Where(project => includedPaths.Contains(Path.GetFullPath(project.FilePath)))
            .Select(project => new WorkspaceProject
            {
                Name = project.Name,
                FilePath = project.FilePath,
                Directory = project.Directory,
                Compilation = project.Compilation,
                Documents = project.Documents,
                ProjectReferences = project.ProjectReferences
                    .Select(Path.GetFullPath)
                    .Where(includedPaths.Contains)
                    .ToList(),
                IsTestProject = project.IsTestProject,
                ResourceItems = project.ResourceItems,
            })
            .ToList();
    }

    private static void WriteProjectExclusionSummary(
        IEnumerable<ProjectConversionExclusion> exclusions,
        bool verbose)
    {
        var orderedExclusions = exclusions
            .OrderBy(exclusion => exclusion.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Skipping {orderedExclusions.Count} project(s) excluded by platform rules.");
        if (!verbose)
        {
            return;
        }

        foreach (var exclusion in orderedExclusions)
        {
            Console.WriteLine($"  Skipped: {exclusion.ProjectName} ({exclusion.Reason})");
        }
    }

    private static InputFingerprintSnapshot BuildInputFingerprintSnapshot(
        ConvertProjectOptions opts,
        IReadOnlyList<string> inputFiles)
    {
        var fingerprintSourcePath = GetFingerprintSourcePath(opts);
        return InputFingerprintSnapshotBuilder.Build(new InputFingerprintBuildRequest
        {
            SourceName = GetSourceName(fingerprintSourcePath),
            SourcePath = fingerprintSourcePath,
            InputFilePaths = inputFiles,
            OptionTokens = GetProjectConversionOptionTokens(opts),
            TemplateFilePaths = GeneratedProjectAssets.GetTemplateAssetPaths(),
            ToolAssemblyPaths = GetToolAssemblyPaths(),
            MappingConfigPath = opts.MappingConfig,
        });
    }

    internal static IReadOnlyList<string> GetProjectConversionOptionTokens(ConvertProjectOptions opts)
    {
        var tokens = new List<string>
        {
            // 产物指纹按目标区分（Task 37）：Java/TS 目标产物布局不同，令牌必须含 target，
            // 否则 --target 切换会被增量缓存错误跳过。
            $"target={opts.Target}",
            $"java-version={opts.JavaVersion}",
            $"mapping-config={(string.IsNullOrWhiteSpace(opts.MappingConfig) ? "<default>" : Path.GetFullPath(opts.MappingConfig))}",
            $"use-records={opts.UseRecords}",
            $"use-optional={opts.UseOptionalForNullable}",
            $"generate-javadoc={opts.GenerateJavaDoc}",
            $"enable-linq-rewrite={opts.EnableLinqRewrite}",
            $"prefer-stream-api={opts.PreferStreamApi?.ToString() ?? "default"}",
            $"maven-group-id={opts.MavenGroupId}",
            $"maven-version={opts.MavenVersion}",
            $"include-tests={opts.IncludeTests}",
            $"extra-deps={GetNormalizedExtraDependencyToken(opts)}",
        };

        // 每个注册 pass 一个开关令牌（注册表拓扑序，含默认关 pass），新增 pass 时
        // 令牌自动覆盖；切换预处理组合会使旧产物指纹失效一次（预期行为）。
        var legacyToggles = PassChainFactory.BuildLegacyToggles(opts);
        foreach (var descriptor in PassCatalog.BuildRegistry().Ordered)
        {
            var enabled = legacyToggles.TryGetValue(descriptor.Id, out var flag)
                ? flag
                : descriptor.DefaultEnabled;
            tokens.Add($"pass:{descriptor.Id}={enabled}");
        }

        return tokens;
    }

    private static string GetFingerprintSourcePath(ConvertProjectOptions opts) =>
        string.IsNullOrWhiteSpace(opts.FingerprintSourcePath) ? opts.Source : opts.FingerprintSourcePath!;

    private static string GetFingerprintInputRoot(ConvertProjectOptions opts) =>
        string.IsNullOrWhiteSpace(opts.FingerprintInputRoot)
            ? ResolveInputRoot(GetFingerprintSourcePath(opts))
            : opts.FingerprintInputRoot!;

    private static IReadOnlyList<JavaDependency> ParseExtraDependencies(string? extraDependencies)
    {
        if (string.IsNullOrWhiteSpace(extraDependencies))
        {
            return [];
        }

        var dependencies = new List<JavaDependency>();
        foreach (var coordinate in extraDependencies.Split(','))
        {
            if (string.IsNullOrWhiteSpace(coordinate))
            {
                throw new ArgumentException("Extra dependency coordinates cannot be empty.");
            }

            dependencies.Add(WorkspacePlanBuilder.ExternalDependency(coordinate));
        }

        return WorkspacePlanBuilder.MergeDependencies(dependencies);
    }

    private static IReadOnlyList<JavaDependency> GetExtraDependencies(ConvertProjectOptions opts) =>
        opts.ParsedExtraDependencies ?? [];

    private static string GetNormalizedExtraDependencyToken(ConvertProjectOptions opts)
    {
        var dependencies = GetExtraDependencies(opts);
        return dependencies.Count == 0
            ? "<none>"
            : string.Join(",", dependencies.Select(dependency =>
                $"{dependency.GroupId}:{dependency.ArtifactId}:{dependency.Version}:{dependency.Scope}"));
    }

    private static IReadOnlyList<string> GetToolAssemblyPaths()
    {
        return new[]
        {
            typeof(Program).Assembly.Location,
            typeof(ConversionPipeline).Assembly.Location,
            typeof(ProjectConversionPipeline).Assembly.Location,
            typeof(SolutionLoader).Assembly.Location,
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static IReadOnlyList<string> EnumerateInputFiles(string rootPath, string destinationRoot)
    {
        if (File.Exists(rootPath))
        {
            return new[] { Path.GetFullPath(rootPath) };
        }

        var fullRootPath = Path.GetFullPath(rootPath);
        var fullDestinationPath = Path.GetFullPath(destinationRoot);
        var ignoredDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            ".idea",
            "bin",
            "obj",
            "node_modules",
        };

        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(fullRootPath);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            if (IsSameOrDescendantPath(currentDirectory, fullDestinationPath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                files.Add(Path.GetFullPath(file));
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (ignoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                {
                    continue;
                }

                if (IsSameOrDescendantPath(subDirectory, fullDestinationPath))
                {
                    continue;
                }

                pendingDirectories.Push(subDirectory);
            }
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveInputRoot(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        return File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;
    }

    /// <summary>
    /// Auto-discovers the Java standard-library metadata directory (<c>config/java/</c>)
    /// as a sibling of the type mapping configuration file.
    /// </summary>
    private static string? ResolveJavaMetadataPath(string? mappingConfig)
    {
        // Determine the config base directory from the mapping config path
        string configDir;
        if (!string.IsNullOrEmpty(mappingConfig))
        {
            var fullPath = Path.IsPathRooted(mappingConfig) ? mappingConfig : Path.Combine(Directory.GetCurrentDirectory(), mappingConfig);
            configDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        }
        else
        {
            configDir = Path.Combine(Directory.GetCurrentDirectory(), "config");
        }

        var javaDir = Path.Combine(configDir, "java");
        return Directory.Exists(Path.Combine(javaDir, "java.base")) ? javaDir : null;
    }

    private static string GetCommonRoot(IEnumerable<string> paths)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return Path.GetFullPath(".");
        }

        var commonRoot = normalizedPaths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (normalizedPaths.Any(path => !IsSameOrDescendantPath(path, commonRoot)))
        {
            var parent = Directory.GetParent(commonRoot);
            if (parent == null)
            {
                break;
            }

            commonRoot = parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return commonRoot;
    }

    private static bool IsSameOrDescendantPath(string path, string candidateAncestor)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullAncestor = Path.GetFullPath(candidateAncestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullPath, fullAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ancestorWithSeparator = fullAncestor + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(ancestorWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddProjectPassProfileEntry(
        List<PassProfileEntry> entries,
        IReadOnlyList<Cs2jPassMetric> passMetrics,
        IEnumerable<ConversionResult> results,
        string? projectName,
        string? moduleName = null)
    {
        var entry = PassProfileEntryBuilder.CreateProjectEntry(passMetrics, results, projectName, moduleName);
        if (entry != null)
        {
            entries.Add(entry);
        }
    }

    private static string GetSourceName(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (File.Exists(fullPath))
        {
            return Path.GetFileNameWithoutExtension(fullPath);
        }

        return Path.GetFileName(fullPath);
    }

    private static JavaModulePlan PlannedModuleToJavaModulePlan(
        PlannedModule module,
        string groupId,
        IReadOnlyList<JavaDependency>? extraDependencies = null)
    {
        var deps = new List<JavaDependency>(WorkspacePlanBuilder.DefaultDependenciesForModule(
            groupId,
            module.Name));

        foreach (var dep in module.CompileDependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            deps.Add(WorkspacePlanBuilder.InternalModuleRef(groupId, dep));
        }

        foreach (var dep in module.TestDependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            deps.Add(WorkspacePlanBuilder.InternalModuleRef(groupId, dep, JavaDependencyScope.Test));
        }
        deps.AddRange(extraDependencies ?? []);

        return new JavaModulePlan
        {
            ModuleName = module.Name,
            IsTestOnly = module.IsTestOnly,
            SourceSets = module.HasTestSources
                ? new JavaSourceSets { TestSources = ["test"] }
                : new JavaSourceSets(),
            Dependencies = WorkspacePlanBuilder.FilterSelfDependencies(
                WorkspacePlanBuilder.MergeDependencies(deps),
                groupId,
                module.Name),
            RequiredCompatPacks = [],
            RequiredRuntimeBridges = [],
        };
    }

    /// <summary>
    /// Scans generated Java files to discover implicit module-to-module dependencies
    /// that were not captured by C# project references. This happens when a C# project
    /// uses framework types (e.g. <c>System.Uri</c>) that the converter maps to another
    /// generated module (e.g. <c>system-private-uri</c>), but there is no C# project
    /// reference to link them.
    /// </summary>
    /// <param name="modulePlans">All module plans (used to know module names and existing deps).</param>
    /// <param name="moduleRoots">File-system path to each module root (parallel to modulePlans).</param>
    /// <param name="groupId">Maven groupId for internal module references.</param>
    /// <returns>A dictionary mapping module plan to a list of additional internal dependencies to add.</returns>
    private static Dictionary<JavaModulePlan, List<JavaDependency>> DiscoverImplicitModuleDependencies(
        IReadOnlyList<JavaModulePlan> modulePlans,
        IReadOnlyList<string> moduleRoots,
        string groupId)
    {
        var result = new Dictionary<JavaModulePlan, List<JavaDependency>>();

        if (modulePlans.Count != moduleRoots.Count || modulePlans.Count == 0)
            return result;

        // Step 1: Build maps:
        //  a) package -> set of module names (for wildcard imports)
        //  b) fully-qualified class name -> set of module names (for specific imports)
        // Multiple modules can share the same package (e.g. a framework
        // package declared by several converted modules).
        // We scan each module's .java files for `package` declarations and
        // public class names.
        var packageToModules = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var classToModules = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < modulePlans.Count; i++)
        {
            var moduleName = modulePlans[i].ModuleName;
            var moduleRoot = moduleRoots[i];
            if (!Directory.Exists(moduleRoot))
                continue;

            foreach (var javaFile in Directory.EnumerateFiles(
                moduleRoot, "*.java", SearchOption.AllDirectories))
            {
                var (pkg, className) = TryReadPackageAndClassName(javaFile);
                if (pkg != null)
                {
                    if (!packageToModules.TryGetValue(pkg, out var moduleSet))
                    {
                        moduleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        packageToModules[pkg] = moduleSet;
                    }
                    moduleSet.Add(moduleName);

                    // Also build class -> module mapping for precise specific-import matching.
                    if (className != null)
                    {
                        var fqn = pkg + "." + className;
                        if (!classToModules.TryGetValue(fqn, out var classModuleSet))
                        {
                            classModuleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            classToModules[fqn] = classModuleSet;
                        }
                        classModuleSet.Add(moduleName);
                    }
                }
            }
        }

        if (packageToModules.Count == 0)
            return result;

        // Step 2: For each module, scan .java files for `import` statements
        // and map them to module names.
        var existingDepModules = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < modulePlans.Count; i++)
        {
            var moduleName = modulePlans[i].ModuleName;
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { moduleName };
            foreach (var dep in modulePlans[i].Dependencies)
            {
                if (dep.IsInternal || dep.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                    existing.Add(dep.ArtifactId);
            }
            existingDepModules[moduleName] = existing;
        }

        for (var i = 0; i < modulePlans.Count; i++)
        {
            var modulePlan = modulePlans[i];
            var moduleRoot = moduleRoots[i];
            if (!Directory.Exists(moduleRoot))
                continue;

            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var javaFile in Directory.EnumerateFiles(
                moduleRoot, "*.java", SearchOption.AllDirectories))
            {
                foreach (var imp in ReadImports(javaFile))
                {
                    // Handle wildcard imports: "import foo.bar.*" means
                    // importing all types in package foo.bar. We strip the
                    // trailing ".*" so it becomes an exact package match.
                    var effectiveImp = imp;
                    var isWildcard = false;
                    if (imp.EndsWith(".*", StringComparison.OrdinalIgnoreCase))
                    {
                        effectiveImp = imp[..^2]; // remove trailing ".*"
                        isWildcard = true;
                    }

                    // For wildcard imports, only do exact package match.
                    // "import foo.bar.*" imports types from package foo.bar
                    // only, NOT from sub-packages like foo.bar.baz.
                    if (isWildcard)
                    {
                        if (packageToModules.TryGetValue(effectiveImp, out var depModules))
                        {
                            // If the current module itself declares this package,
                            // it can resolve types from this package locally.
                            // Only add other modules if the current module does
                            // NOT declare the package. This avoids spurious
                            // dependencies between modules that happen to share
                            // the same package.
                            if (!depModules.Contains(modulePlan.ModuleName))
                            {
                                foreach (var dm in depModules)
                                    discovered.Add(dm);
                            }
                        }
                        continue;
                    }

                    // For specific imports: "import foo.bar.Baz"
                    // Try exact class match first (using the class -> module map).
                    // This is more precise than parent package matching when
                    // multiple modules share the same package.
                    if (classToModules.TryGetValue(imp, out var classModules))
                    {
                        // If the current module itself defines this class,
                        // it doesn't need a dependency on any other module
                        // for this class. Only add other modules if the
                        // current module does NOT define the class.
                        if (!classModules.Contains(modulePlan.ModuleName))
                        {
                            foreach (var cm in classModules)
                                discovered.Add(cm);
                        }
                        continue;
                    }

                    // Try exact package match (Baz is a package itself).
                    if (packageToModules.TryGetValue(imp, out var depModules2))
                    {
                        foreach (var dm in depModules2)
                            discovered.Add(dm);
                        continue;
                    }

                    // Try parent package match: find the longest matching prefix.
                    // e.g. import "dotnet.system.Uri" -> package "dotnet.system" -> module "system-private-uri"
                    // Multiple modules may share the package; add all matches.
                    // NOTE: This is a fallback for classes not found in our
                    // class map (e.g. inner classes, generated classes).
                    int bestLen = -1;
                    HashSet<string>? bestMatches = null;
                    foreach (var kvp in packageToModules)
                    {
                        if (imp.StartsWith(kvp.Key + ".", StringComparison.OrdinalIgnoreCase)
                            && kvp.Key.Length > bestLen)
                        {
                            bestLen = kvp.Key.Length;
                            bestMatches = kvp.Value;
                        }
                    }
                    if (bestMatches != null)
                    {
                        // If the current module itself declares the matching
                        // package, it can resolve the imported class locally.
                        // Only add other modules if the current module does
                        // NOT declare the package.
                        if (!bestMatches.Contains(modulePlan.ModuleName))
                        {
                            foreach (var bm in bestMatches)
                                discovered.Add(bm);
                        }
                    }
                }
            }

            // Filter out self and already-existing dependencies.
            discovered.ExceptWith(existingDepModules[modulePlan.ModuleName]);

            // Never add test-only modules as dependencies for main modules —
            // test modules depend on main modules, not the reverse.
            discovered.RemoveWhere(depModule =>
                modulePlans.FirstOrDefault(mp =>
                    mp.ModuleName.Equals(depModule, StringComparison.OrdinalIgnoreCase))
                    is { IsTestOnly: true });

            // Filter out dependencies that would create a cycle.
            // A cycle happens when module A depends on module B and B already
            // depends on A (directly or transitively).
            discovered.RemoveWhere(depModule =>
                WouldCreateCycle(modulePlans, modulePlan.ModuleName, depModule, existingDepModules));

            if (discovered.Count > 0)
            {
                var depsToAdd = new List<JavaDependency>();
                foreach (var depModule in discovered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    depsToAdd.Add(WorkspacePlanBuilder.InternalModuleRef(groupId, depModule));
                }
                result[modulePlan] = depsToAdd;
            }
        }

        // Post-cycle-detection: build the full graph (existing + discovered) and
        // remove discovered edges that create cycles. This is needed because
        // the per-module WouldCreateCycle check above only sees dependencies
        // that existed before this pass — it misses cycles formed by two new
        // edges (e.g. A discovers B and B discovers A in the same pass).
        if (result.Count > 0)
        {
            var fullGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in existingDepModules)
                fullGraph[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in result)
            {
                var moduleName = kvp.Key.ModuleName;
                if (!fullGraph.TryGetValue(moduleName, out var deps))
                {
                    deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    fullGraph[moduleName] = deps;
                }
                foreach (var d in kvp.Value)
                    deps.Add(d.ArtifactId);
            }

            foreach (var kvp in result.ToList())
            {
                var moduleName = kvp.Key.ModuleName;
                var toRemove = new List<JavaDependency>();
                foreach (var dep in kvp.Value)
                {
                    fullGraph[moduleName].Remove(dep.ArtifactId);
                    bool createsCycle = CanReach(fullGraph, dep.ArtifactId, moduleName);
                    fullGraph[moduleName].Add(dep.ArtifactId);
                    if (createsCycle)
                        toRemove.Add(dep);
                }
                if (toRemove.Count > 0)
                {
                    var updated = kvp.Value.Except(toRemove).ToList();
                    if (updated.Count == 0)
                        result.Remove(kvp.Key);
                    else
                        result[kvp.Key] = updated;
                }
            }
        }

        return result;
    }

    private static bool CanReach(
        Dictionary<string, HashSet<string>> graph,
        string from, string to)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(from);
        visited.Add(from);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Equals(to, StringComparison.OrdinalIgnoreCase))
                return true;
            if (graph.TryGetValue(current, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (visited.Add(dep))
                        queue.Enqueue(dep);
                }
            }
        }
        return false;
    }

    private static bool WouldCreateCycle(
        IReadOnlyList<JavaModulePlan> modulePlans,
        string sourceModule,
        string targetModule,
        Dictionary<string, HashSet<string>> existingDepModules)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(targetModule);
        visited.Add(targetModule);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Equals(sourceModule, StringComparison.OrdinalIgnoreCase))
                return true;
            if (existingDepModules.TryGetValue(current, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (visited.Add(dep))
                        queue.Enqueue(dep);
                }
            }
        }
        return false;
    }

    private static string? TryReadPackageName(string javaFilePath)
    {
        try
        {
            using var reader = new StreamReader(javaFilePath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("package ", StringComparison.Ordinal))
                {
                    // Extract the package name: "package foo.bar;" -> "foo.bar"
                    var rest = trimmed["package ".Length..];
                    var semi = rest.IndexOf(';');
                    if (semi >= 0)
                        rest = rest[..semi];
                    return rest.Trim();
                }
                // Skip empty lines and comments before package declaration
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                    continue;
                // If we hit any other line, the package declaration is missing or past
                break;
            }
        }
        catch
        {
            // Ignore file read errors
        }
        return null;
    }

    /// <summary>
    /// Reads the package declaration and public class/interface/enum name
    /// from a Java source file. The class name is derived from the file name
    /// (Java requires public top-level class name to match file name) and
    /// verified by scanning for a matching declaration.
    /// </summary>
    private static (string? package, string? className) TryReadPackageAndClassName(
        string javaFilePath)
    {
        var pkg = TryReadPackageName(javaFilePath);
        if (pkg == null)
            return (null, null);

        // Java requires the public top-level class name to match the file name.
        var fileName = Path.GetFileNameWithoutExtension(javaFilePath);
        return (pkg, fileName);
    }

    private static List<string> ReadImports(string javaFilePath)
    {
        var imports = new List<string>();
        try
        {
            using var reader = new StreamReader(javaFilePath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("import ", StringComparison.Ordinal))
                {
                    // "import foo.bar.Baz;" -> "foo.bar.Baz"
                    var rest = trimmed["import ".Length..];
                    var semi = rest.IndexOf(';');
                    if (semi >= 0)
                        rest = rest[..semi];
                    rest = rest.Trim();
                    if (rest.Length > 0)
                        imports.Add(rest);
                }
            }
        }
        catch
        {
            // Ignore file read errors
        }
        return imports;
    }

    private static string FormatDiagnostic(DiagnosticMessage diag)
    {
        var label = diag.Code != null
            ? $"{diag.Code}{(diag.Category != null ? $" [{diag.Category}]" : string.Empty)}: "
            : diag.Category != null
                ? $"[{diag.Category}] "
                : string.Empty;

        if (diag.Location == null || !diag.Location.IsInSource)
        {
            return label + diag.Message;
        }

        var lineSpan = diag.Location.GetLineSpan();
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        return $"{label}{diag.Message} ({lineSpan.Path}:{line}:{column})";
    }

    /// <summary>
    /// Emits a structured failure report for a failed conversion result.
    /// Includes the source file path (when available) to aid error location.
    /// </summary>
    private static void EmitFailureResult(ConversionResult result)
    {
        if (!string.IsNullOrEmpty(result.SourceFilePath)
            && !string.Equals(result.SourceFilePath, result.FileName, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Failed: {result.FileName} (source: {result.SourceFilePath})");
        }
        else
        {
            Console.Error.WriteLine($"Failed: {result.FileName}");
        }
        foreach (var diag in result.Diagnostics)
        {
            Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
        }
    }

    private static async Task<int> UnsignedType(UnsignedTypeOptions opts)
    {
        var rewriterOptions = new UnsignedTypeRewriterOptions(
            Verbose: opts.Verbose,
            Strict: opts.Strict,
            EnableOperatorRewrite: !opts.NoOperatorRewrite,
            EnableCastRewrite: !opts.NoCastRewrite,
            EnableLiteralRewrite: !opts.NoLiteralRewrite);
        var rewriter = new UnsignedTypeRewriter();

        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "unsigned-type",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true));

            var result = rewriter.Rewrite(sourceCode, compilation.GetSemanticModel(tree), rewriterOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));

                if (result.Statistics.OperatorsRewritten > 0 || result.Statistics.CastsRewritten > 0)
                {
                    var outputDir = Path.GetDirectoryName(Path.GetFullPath(opts.Output)) ?? ".";
                    var extensionPath = Path.Combine(outputDir, UnsignedMathSupportFiles.SingleFileHelperFileName);
                    await File.WriteAllTextAsync(extensionPath, UnsignedMathSupportFiles.GetUnsignedMathFileContent(), new System.Text.UTF8Encoding(false));
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Wrote extension file: {extensionPath}");
                    }
                }

                if (opts.Verbose)
                {
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
                }
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
            }

            return result.Diagnostics.Any(d => d.Severity == UnsignedTypeSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }

        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }

        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
            new ProjectUnsignedTypePreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                RewriterOptions = rewriterOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
            });

        Console.WriteLine(
            $"unsigned-type: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.TypesReplaced} types replaced, " +
            $"{preprocessResult.Statistics.OperatorsRewritten} operators rewritten, " +
            $"{preprocessResult.Statistics.LiteralsRewritten} literals rewritten");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> RefOutInAsync(RefOutInVerbOptions opts)
    {
        var rewriterOptions = new RefOutInRewriterOptions(
            Verbose: opts.Verbose,
            Strict: opts.Strict);
        var rewriter = new RefOutInRewriter();

        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "ref-out-in",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true));

            var result = rewriter.Rewrite(sourceCode, compilation.GetSemanticModel(tree), rewriterOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (result.Changed)
                {
                    var outputDir = Path.GetDirectoryName(Path.GetFullPath(opts.Output)) ?? ".";
                    var helperPath = Path.Combine(outputDir, RefOutInSupportFiles.SingleFileHelperFileName);
                    await File.WriteAllTextAsync(helperPath, RefOutInSupportFiles.GetSingleFileHelperContent(),
                        new System.Text.UTF8Encoding(false));
                    if (opts.Verbose)
                        Console.WriteLine($"Wrote support file: {helperPath}");
                }
                if (opts.Verbose)
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
            }

            return result.Diagnostics.Any(d => d.Severity == RefOutInSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }

        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }

        var preprocessResult = await ProjectRefOutInPreprocessor.PreprocessAsync(
            new ProjectRefOutInPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Verbose = opts.Verbose,
                RewriterOptions = rewriterOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
                VerifyBuild = !opts.NoVerifyBuild,
                VerifyTests = !opts.NoVerifyTests,
            });

        Console.WriteLine(
            $"ref-out-in: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.RefParamsRewritten} ref params, " +
            $"{preprocessResult.Statistics.OutParamsRewritten} out params, " +
            $"{preprocessResult.Statistics.CallSitesLifted} call sites lifted, " +
            $"{preprocessResult.Statistics.LockedContractMembers} locked members");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            var showInfo = diagnostic.Severity != RefOutInSeverity.Info || opts.Verbose;
            if (showInfo)
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
        }

        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> ForeachDeconstructionAsync(ForeachDeconstructionVerbOptions opts)
    {
        var rewriterOptions = new CSharpToJava.Core.ForeachDeconstruction.ForeachDeconstructionOptions
        {
            Strict = opts.Strict,
            ReportInfo = true,
        };

        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "foreach-deconstruction",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true));

            var preprocessor = new CSharpToJava.Core.ForeachDeconstruction.ForeachDeconstructionPreprocessor();
            var result = preprocessor.Preprocess(sourceCode, compilation.GetSemanticModel(tree), rewriterOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose)
                {
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
                }
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
            }

            return result.Diagnostics.Any(d => d.Severity == CSharpToJava.Core.ForeachDeconstruction.ForeachDeconstructionSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }

        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }

        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectForeachDeconstructionPreprocessor.PreprocessAsync(
            new ProjectForeachDeconstructionPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                RewriterOptions = rewriterOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
            });

        Console.WriteLine(
            $"foreach-deconstruction: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.ForeachDeconstructionsLowered} lowered, " +
            $"{preprocessResult.Statistics.ForeachDeconstructionsSkipped} skipped");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> NullCoalesceAsync(NullCoalesceVerbOptions opts)
    {
        var coreOptions = new NullCoalesceOptions(Verbose: opts.Verbose, Strict: opts.Strict);
        var rewriter = new NullCoalesceRewriter();

        // Single-file mode
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "null-coalesce-standalone",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var model = compilation.GetSemanticModel(tree);
            var result = rewriter.Preprocess(sourceCode, model, coreOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose)
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.Message}");

            return result.Diagnostics.Any(d => d.Severity == NullCoalesceSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectNullCoalescePreprocessor.PreprocessAsync(
            new ProjectNullCoalesceRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                Options = coreOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
                VerifyBuild = !opts.NoVerifyBuild,
                VerifyTests = !opts.NoVerifyTests,
                Strict = opts.Strict,
            });

        Console.WriteLine(
            $"null-coalesce: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.NullCoalescingRewritten} ?? rewritten, " +
            $"{preprocessResult.Statistics.NullCoalescingAssignRewritten} ??= rewritten, " +
            $"{preprocessResult.Statistics.TempVariablesGenerated} temp vars, " +
            $"{preprocessResult.Statistics.ResidualCount} residual");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
            Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> DelegatePreprocessAsync(DelegatePreprocessVerbOptions opts)
    {
        var coreOptions = new DelegatePreprocessorOptions(Verbose: opts.Verbose, Strict: opts.Strict);
        var rewriter = new DelegatePreprocessor();

        // Single-file mode
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "delegate-preprocess-standalone",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var model = compilation.GetSemanticModel(tree);
            var result = rewriter.Preprocess(sourceCode, model, coreOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose)
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.Message}");

            return result.Diagnostics.Any(d => d.Severity == DelegatePreprocessorSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectDelegatePreprocessor.PreprocessAsync(
            new ProjectDelegatePreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                Options = coreOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
                VerifyBuild = !opts.NoVerifyBuild,
                VerifyTests = !opts.NoVerifyTests,
                Strict = opts.Strict,
            });

        Console.WriteLine(
            $"delegate: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.CombineRewritten} combine, " +
            $"{preprocessResult.Statistics.RemoveRewritten} remove, " +
            $"{preprocessResult.Statistics.CompoundAssignRewritten} compound, " +
            $"{preprocessResult.Statistics.AnonymousMethodRewritten} anonymous method, " +
            $"{preprocessResult.Statistics.SkippedUnsupported} skipped, " +
            $"{preprocessResult.Statistics.ResidualCount} residual");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
            Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> EnumPreprocessAsync(EnumPreprocessVerbOptions opts)
    {
        var coreOptions = new EnumPreprocessorOptions(Verbose: opts.Verbose, Strict: opts.Strict);
        var rewriter = new EnumPreprocessor();

        // Single-file mode
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "enum-preprocess-standalone",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var model = compilation.GetSemanticModel(tree);
            var result = rewriter.Preprocess(sourceCode, model, coreOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose)
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.Message}");

            return result.Diagnostics.Any(d => d.Severity == EnumPreprocessorSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectEnumPreprocessor.PreprocessAsync(
            new ProjectEnumPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                Options = coreOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
                VerifyBuild = !opts.NoVerifyBuild,
                VerifyTests = !opts.NoVerifyTests,
                Strict = opts.Strict,
            });

        Console.WriteLine(
            $"enum: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.FilesDirtyNoRewrite} dirty-no-rewrite, " +
            $"{preprocessResult.Statistics.BitwiseOrRewritten + preprocessResult.Statistics.BitwiseAndRewritten + preprocessResult.Statistics.BitwiseXorRewritten} bitwise, " +
            $"{preprocessResult.Statistics.BitwiseNotRewritten} ~, " +
            $"{preprocessResult.Statistics.CompoundAssignRewritten} compound, " +
            $"{preprocessResult.Statistics.HasFlagRewritten} HasFlag, " +
            $"{preprocessResult.Statistics.NullableLiftedRewritten} lifted, " +
            $"{preprocessResult.Statistics.ResidualCount} residual");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
            Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> EliminateGoto(EliminateGotoOptions opts)
    {
        var elim = new GotoEliminator();
        var goOpts = new GotoEliminatorOptions(opts.Verbose, opts.Strict);

        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var result = elim.Eliminate(sourceCode, goOpts);
            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose) Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else Console.Write(result.OutputCode);
            return DiagnosticsExitCode(result);
        }

        // 目录模式
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i/-o) or (-s/-d).");
            return 1;
        }
        if (!Directory.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source directory not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);
        bool inPlace = string.Equals(
            Path.GetFullPath(opts.Source.TrimEnd('\\', '/')),
            Path.GetFullPath(opts.Destination.TrimEnd('\\', '/')),
            StringComparison.OrdinalIgnoreCase);
        int transformed = 0, clean = 0, failed = 0;
        foreach (var file in Directory.GetFiles(opts.Source, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(opts.Source, file);
            var dst = Path.Combine(opts.Destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var sourceCode = await File.ReadAllTextAsync(file);
            var result = elim.Eliminate(sourceCode, goOpts);
            if (!result.Changed)
            {
                // 字节复制保真（in-place 模式下源==目标，跳过自复制）
                if (!inPlace) File.Copy(file, dst, overwrite: true);
                clean++;
                if (opts.Verbose) Console.WriteLine($"clean (copy): {rel}");
            }
            else
            {
                // 原子写：先 .tmp 再替换
                await File.WriteAllTextAsync(dst + ".tmp", result.OutputCode, new System.Text.UTF8Encoding(false));
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(dst + ".tmp", dst);
                transformed++;
                if (opts.Verbose) Console.WriteLine($"transformed: {rel}");
            }
            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.MethodName}: {d.Message}");
            if (result.Diagnostics.Any(d => d.Severity == GotoEliminatorSeverity.Warning)) failed++;
        }
        // 非 .cs 文件字节复制（in-place 模式下源==目标，跳过自复制）
        if (!inPlace)
        {
            foreach (var file in Directory.GetFiles(opts.Source, "*.*", SearchOption.AllDirectories)
                         .Where(f => !f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var rel = Path.GetRelativePath(opts.Source, file);
                var dst = Path.Combine(opts.Destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
            }
        }
        Console.WriteLine($"eliminate-goto: {transformed} transformed, {clean} clean, {failed} with-warnings");
        return failed > 0 ? 1 : 0;
    }

    private static int DiagnosticsExitCode(GotoEliminatorResult result)
    {
        foreach (var d in result.Diagnostics)
            Console.Error.WriteLine($"[{d.Severity}] {d.MethodName}: {d.Message}");
        return result.Diagnostics.Any(d => d.Severity == GotoEliminatorSeverity.Warning) ? 1 : 0;
    }

    /// <summary>
    /// Loads the force-readonly override list (qualified struct names) from a JSON
    /// config. The default config is <c>config/ReadOnlyStructOverrides.json</c> next
    /// to the current directory or the repo root, so both project conversions share
    /// the same converter code while only the configured types are forced.
    /// </summary>
    private static IReadOnlySet<string> LoadReadOnlyForceOverrides(string? explicitPath)
    {
        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "config", "ReadOnlyStructOverrides.json");
            if (!File.Exists(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                string? found = null;
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "config", "ReadOnlyStructOverrides.json");
                    if (File.Exists(candidate))
                    {
                        found = candidate;
                        break;
                    }
                    dir = dir.Parent;
                }
                if (found != null) path = found;
            }
        }

        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("forceReadOnly", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    names.Add(item.GetString()!);
                }
            }
            return names;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[Warning] Failed to load readonly-struct overrides from {path}: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private sealed class RecordReadOnlyOverrideSets
    {
        public string[]? Force { get; set; }
        public string[]? Skip { get; set; }
    }

    private static CSharpToJava.Core.RecordReadOnly.RecordReadOnlyOptions BuildRecordReadOnlyOptions(
        string? explicitPath,
        bool strict,
        IEnumerable<string>? extraForce = null,
        IEnumerable<string>? extraSkip = null,
        bool convertRecordClasses = false)
    {
        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "config", "RecordReadOnlyOverrides.json");
            if (!File.Exists(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "config", "RecordReadOnlyOverrides.json");
                    if (File.Exists(candidate)) { path = candidate; break; }
                    dir = dir.Parent;
                }
            }
        }

        var force = new HashSet<string>(StringComparer.Ordinal);
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (path != null && File.Exists(path))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<RecordReadOnlyOverrideSets>(
                    File.ReadAllText(path)) ?? new RecordReadOnlyOverrideSets();
                if (parsed.Force != null) force.UnionWith(parsed.Force);
                if (parsed.Skip != null) skip.UnionWith(parsed.Skip);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Warning] Failed to load record-readonly overrides from {path}: {ex.Message}");
            }
        }
        if (extraForce != null) force.UnionWith(extraForce);
        if (extraSkip != null) skip.UnionWith(extraSkip);

        return new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyOptions
        {
            ForceQualifiedNames = force,
            SkipQualifiedNames = skip,
            Strict = strict,
            ConvertRecordClasses = convertRecordClasses,
        };
    }

    private sealed class JavaKeywordsOverrideSets
    {
        public string[]? Force { get; set; }
        public string[]? Skip { get; set; }
    }

    private static CSharpToJava.Core.JavaKeywords.JavaKeywordOptions BuildJavaKeywordOptions(
        string? explicitPath,
        bool strict,
        IEnumerable<string>? extraForce = null,
        IEnumerable<string>? extraSkip = null)
    {
        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "config", "JavaKeywordsOverrides.json");
            if (!File.Exists(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "config", "JavaKeywordsOverrides.json");
                    if (File.Exists(candidate)) { path = candidate; break; }
                    dir = dir.Parent;
                }
            }
        }

        var force = new HashSet<string>(StringComparer.Ordinal);
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (path != null && File.Exists(path))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<JavaKeywordsOverrideSets>(
                    File.ReadAllText(path)) ?? new JavaKeywordsOverrideSets();
                if (parsed.Force != null) force.UnionWith(parsed.Force);
                if (parsed.Skip != null) skip.UnionWith(parsed.Skip);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Warning] Failed to load java-keywords overrides from {path}: {ex.Message}");
            }
        }
        if (extraForce != null) force.UnionWith(extraForce);
        if (extraSkip != null) skip.UnionWith(extraSkip);

        return new CSharpToJava.Core.JavaKeywords.JavaKeywordOptions
        {
            ForceQualifiedNames = force,
            SkipQualifiedNames = skip,
            Strict = strict,
        };
    }

    private static CSharpToJava.Core.VarianceAudit.VarianceAuditOptions BuildVarianceAuditOptions(
        bool strict,
        bool noReportInfo)
        => new()
        {
            Strict = strict,
            ReportInfo = !noReportInfo,
        };

    private sealed class MemberDesugarOverrideSets
    {
        public string[]? Force { get; set; }
        public string[]? Skip { get; set; }
        /// <summary>C39 rules.finalizers 子节（缺省整节 → Reject 默认）。</summary>
        public FinalizerSets? Finalizers { get; set; }
        /// <summary>C46 rules.signatures 子节（缺省整节 → Enabled=false，零执行）。</summary>
        public SignatureSets? Signatures { get; set; }

        /// <summary>C50 rules.fieldKeyword 子节（缺省整节 → Enabled=false 默认关）。</summary>
        public FieldKeywordSets? FieldKeyword { get; set; }
        /// <summary>C27 rules.records 子节（缺省整节 → Enabled=false 默认关）。</summary>
        public RecordSets? Records { get; set; }
    }

    /// <summary>rules.signatures 配置形态（C46 设计 §5/§12）。Enabled 默认 false（主闸门）。</summary>
    private sealed class SignatureSets
    {
        public bool? Enabled { get; set; }
        public bool? Strict { get; set; }
        public bool? ReportInfo { get; set; }
        public string[]? SkipSignatures { get; set; }
        public bool? AllowDivergentInterfaceBodies { get; set; }
    }

    /// <summary>rules.fieldKeyword 配置形态（C50，设计 §12 默认关子集开关）。</summary>
    private sealed class FieldKeywordSets
    {
        public bool? Enabled { get; set; }
    }

    /// <summary>rules.records 配置形态（C27，设计 §12 默认关子集开关）。JSON PascalCase 大小写敏感，
    /// 形态 <c>{"Records":{"Enabled":true}}</c>。</summary>
    private sealed class RecordSets
    {
        public bool? Enabled { get; set; }
    }

    /// <summary>rules.finalizers 配置形态（C39 设计 §2 决策 9）。M1 仅 Policy 生效；
    /// ForceRemoveTypes/EmitRemovalMarkerComment/ExtractRemovedBody 为 M2 承载透传。</summary>
    private sealed class FinalizerSets
    {
        public string? Policy { get; set; }
        public string[]? ForceRemoveTypes { get; set; }
        public bool? EmitRemovalMarkerComment { get; set; }
        public bool? ExtractRemovedBody { get; set; }
    }

    private static CSharpToJava.Core.MemberDesugar.MemberDesugarOptions BuildMemberDesugarOptions(
        string? explicitPath,
        bool strict,
        IEnumerable<string>? extraForce = null,
        IEnumerable<string>? extraSkip = null)
    {
        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "config", "MemberDesugarOverrides.json");
            if (!File.Exists(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "config", "MemberDesugarOverrides.json");
                    if (File.Exists(candidate)) { path = candidate; break; }
                    dir = dir.Parent;
                }
            }
        }

        var force = new HashSet<string>(StringComparer.Ordinal);
        var skip = new HashSet<string>(StringComparer.Ordinal);
        MemberDesugarOverrideSets? parsed = null;
        if (path != null && File.Exists(path))
        {
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<MemberDesugarOverrideSets>(
                    File.ReadAllText(path)) ?? new MemberDesugarOverrideSets();
                if (parsed.Force != null) force.UnionWith(parsed.Force);
                if (parsed.Skip != null) skip.UnionWith(parsed.Skip);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Warning] Failed to load member-desugar overrides from {path}: {ex.Message}");
            }
        }
        if (extraForce != null) force.UnionWith(extraForce);
        if (extraSkip != null) skip.UnionWith(extraSkip);

        return new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions
        {
            ForceQualifiedNames = force,
            SkipQualifiedNames = skip,
            Strict = strict,
            Finalizers = BuildFinalizerOptions(parsed?.Finalizers, path),
            Signatures = BuildSignatureOptions(parsed?.Signatures),
            FieldKeyword = BuildFieldKeywordOptions(parsed?.FieldKeyword),
            Records = BuildRecordOptions(parsed?.Records),
        };
    }

    /// <summary>映射 rules.signatures 子节（C46）：缺失/非法 → 默认（Enabled=false、Strict=true、
    /// ReportInfo=true、空 SkipSignatures、AllowDivergentInterfaceBodies=false），不致命。</summary>
    private static CSharpToJava.Core.MemberDesugar.Signatures.SignatureOptions BuildSignatureOptions(
        SignatureSets? sets)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (sets?.SkipSignatures != null) skip.UnionWith(sets.SkipSignatures);

        return new CSharpToJava.Core.MemberDesugar.Signatures.SignatureOptions
        {
            Enabled = sets?.Enabled ?? false,
            Strict = sets?.Strict ?? true,
            ReportInfo = sets?.ReportInfo ?? true,
            SkipSignatures = skip,
            AllowDivergentInterfaceBodies = sets?.AllowDivergentInterfaceBodies ?? false,
        };
    }

    /// <summary>映射 rules.fieldKeyword 子节（C50）：Enabled 缺省为 false（默认关、不抢跑）。
    /// 配置缺失/解析失败按 false 继续（沿用族惯例）。</summary>
    private static CSharpToJava.Core.MemberDesugar.Properties.FieldKeywordOptions
        BuildFieldKeywordOptions(FieldKeywordSets? sets)
        => new()
        {
            Enabled = sets?.Enabled ?? false,
        };

    /// <summary>映射 rules.records 子节（C27）：Enabled 缺省为 false（默认关、不抢跑，设计 §12）。
    /// 配置缺失/解析失败按 false 继续（沿用族惯例）。</summary>
    private static CSharpToJava.Core.MemberDesugar.Records.RecordOptions
        BuildRecordOptions(RecordSets? sets)
        => new()
        {
            Enabled = sets?.Enabled ?? false,
        };

    /// <summary>映射 rules.finalizers 子节（C39）：Policy 大小写不敏感映射 Reject/Remove/CleanerGuide，
    /// 缺失/非法值回退 Reject；沿用族惯例（配置缺失/解析失败 → Warning 空集继续，不致命）。</summary>
    private static CSharpToJava.Core.MemberDesugar.Finalizers.FinalizerOptions BuildFinalizerOptions(
        FinalizerSets? sets, string? sourcePath)
    {
        var policy = CSharpToJava.Core.MemberDesugar.Finalizers.FinalizerPolicy.Reject;
        if (!string.IsNullOrWhiteSpace(sets?.Policy))
        {
            if (Enum.TryParse<CSharpToJava.Core.MemberDesugar.Finalizers.FinalizerPolicy>(
                    sets.Policy, ignoreCase: true, out var parsedPolicy))
                policy = parsedPolicy;
            else
                Console.Error.WriteLine(
                    $"[Warning] Invalid rules.finalizers Policy '{sets.Policy}' in {sourcePath}; " +
                    "falling back to Reject.");
        }

        var forceRemove = new HashSet<string>(StringComparer.Ordinal);
        if (sets?.ForceRemoveTypes != null) forceRemove.UnionWith(sets.ForceRemoveTypes);

        return new CSharpToJava.Core.MemberDesugar.Finalizers.FinalizerOptions
        {
            Policy = policy,
            ForceRemoveTypes = forceRemove,
            EmitRemovalMarkerComment = sets?.EmitRemovalMarkerComment ?? false,
            ExtractRemovedBody = sets?.ExtractRemovedBody ?? false,
        };
    }

    private sealed class StructValueSemanticsOverrideSets
    {
        public string[]? Force { get; set; }
        public string[]? Skip { get; set; }
    }

    /// <summary>
    /// 加载 config/StructValueSemanticsOverrides.json（{ "skip": [], "force": [] }）。
    /// 路径解析与 record-readonly 同口径（CWD → AppContext.BaseDirectory 向上查找）；
    /// 文件可选；JSON 解析失败 → Warning 按空集继续，不致命（设计 §5）。
    /// </summary>
    private static CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions
        BuildStructValueSemanticsOptions(string? explicitPath)
    {
        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "config", "StructValueSemanticsOverrides.json");
            if (!File.Exists(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "config", "StructValueSemanticsOverrides.json");
                    if (File.Exists(candidate)) { path = candidate; break; }
                    dir = dir.Parent;
                }
            }
        }

        var force = new HashSet<string>(StringComparer.Ordinal);
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (path != null && File.Exists(path))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<StructValueSemanticsOverrideSets>(
                    File.ReadAllText(path)) ?? new StructValueSemanticsOverrideSets();
                if (parsed.Force != null) force.UnionWith(parsed.Force);
                if (parsed.Skip != null) skip.UnionWith(parsed.Skip);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Warning] Failed to load struct-value-semantics overrides from {path}: {ex.Message}");
            }
        }

        return new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions
        {
            ForceQualifiedNames = force,
            SkipQualifiedNames = skip,
        };
    }

    /// <summary>
    /// 独立 java-keywords verb：单文件 -i/-o（只允许自包含，JK090）或
    /// 项目 -s/-d（整树驱动 + 计划级回退）。
    /// </summary>
    private static async Task<int> JavaKeywordsVerbAsync(JavaKeywordsVerbOptions opts)
    {
        var options = BuildJavaKeywordOptions(
            opts.Overrides,
            strict: opts.Strict,
            extraForce: opts.Force,
            extraSkip: opts.Skip);

        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                sourceCode,
                new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                    Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest),
                path: Path.GetFullPath(opts.Input));
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "java-keywords-file",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            var preprocessor =
                new CSharpToJava.Core.JavaKeywords.JavaReservedWordPreprocessor(options);
            preprocessor.BindCompilation(compilation);

            // 单文件模式自包含闸门（JK090）：跨文件声明/引用/站点的 plan 一律排除
            var nonSelfContained = preprocessor.Plans
                .Where(p => p.TouchedFiles.Any(f => !string.Equals(
                        Path.GetFullPath(f), tree.FilePath, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Id)
                .ToList();
            if (nonSelfContained.Count > 0)
            {
                preprocessor.ExcludePlans(nonSelfContained);
            }

            var statistics = new CSharpToJava.Core.JavaKeywords.JavaKeywordStatistics();
            var diagnostics = new List<CSharpToJava.Core.JavaKeywords.JavaKeywordDiagnostic>();
            var result = preprocessor.RewriteFile(
                sourceCode, compilation.GetSemanticModel(tree), statistics, diagnostics);
            foreach (var id in nonSelfContained)
            {
                var plan = preprocessor.Plans.First(p => p.Id == id);
                diagnostics.Add(new CSharpToJava.Core.JavaKeywords.JavaKeywordDiagnostic(
                    CSharpToJava.Core.JavaKeywords.JavaKeywordSeverity.Warning,
                    CSharpToJava.Core.JavaKeywords.JavaKeywordDiagnosticCode.JK090,
                    "plan is not self-contained within the single input file (declarations, " +
                    "references, or boolean sites live outside the file); skipped in single-file mode.",
                    tree.FilePath, 0, 0, plan.SymbolName));
            }

            if (opts.Output != null)
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
            else
                Console.Write(result.OutputCode);

            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine(diagnostic.ToMsBuildString());

            return options.Strict && statistics.PlansSkipped > 0 ? 1 : 0;
        }

        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectJavaReservedWordPreprocessor.PreprocessAsync(
            new ProjectJavaReservedWordPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                Options = options,
                FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
            });

        Console.WriteLine(
            $"java-keywords: {preprocessResult.Statistics.SymbolsRenamed} symbol(s) renamed, " +
            $"{preprocessResult.Statistics.OperatorTypesDowngraded} operator type(s) downgraded, " +
            $"{preprocessResult.Statistics.BooleanSitesRewritten} boolean site(s) rewritten, " +
            $"{preprocessResult.Statistics.NameofLiteralized} nameof(s) literalized, " +
            $"{preprocessResult.Statistics.PlansSkipped} plan(s) skipped, " +
            $"{preprocessResult.Statistics.PlansRolledBack} plan(s) rolled back");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            if (diagnostic.Severity != CSharpToJava.Core.JavaKeywords.JavaKeywordSeverity.Info
                || opts.Verbose)
            {
                Console.Error.WriteLine(diagnostic.ToMsBuildString());
            }
        }

        return preprocessResult.Success ? 0 : 1;
    }

    /// <summary>
    /// Generic <c>preprocess</c> verb: lists the catalog (<c>--list-passes</c>) or
    /// runs the default chain / an explicitly selected set of registered passes
    /// against a project or solution without performing Java conversion.
    /// </summary>
    private static async Task<int> PreprocessVerbAsync(PreprocessVerbOptions opts)
    {
        try
        {
            if (opts.ListPasses)
            {
                PassListFormatter.Print(PassCatalog.BuildRegistry());
                return 0;
            }

            if (string.IsNullOrWhiteSpace(opts.Source) || string.IsNullOrWhiteSpace(opts.Destination))
            {
                Console.Error.WriteLine("Error: provide --list-passes or (-s <source> -d <destination>).");
                return 1;
            }

            var hasDirectorySource = Directory.Exists(opts.Source);
            var hasProjectSource = File.Exists(opts.Source)
                && (Path.GetExtension(opts.Source).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(opts.Source).Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(opts.Source).Equals(".slnx", StringComparison.OrdinalIgnoreCase));
            if (!hasDirectorySource && !hasProjectSource)
            {
                Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
                return 1;
            }

            // Explicit --pass selection means "run exactly these ids"; without it
            // the descriptor defaults (convert-project chain) are used.
            IReadOnlyDictionary<string, bool>? legacyToggles = null;
            IEnumerable<string>? enablePassIds = opts.EnablePasses;
            if (opts.Passes != null && opts.Passes.Any())
            {
                legacyToggles = PassChainFactory.BuildAllDisabledToggles(PassCatalog.BuildRegistry());
                enablePassIds = opts.Passes.Concat(opts.EnablePasses ?? Array.Empty<string>()).ToList();
            }

            // Validate ids before creating any output directory.
            var validationPlan = PassPlanResolver.Resolve(
                PassCatalog.BuildRegistry(), legacyToggles, enablePassIds, opts.DisablePasses);
            if (!validationPlan.IsValid)
            {
                foreach (var planError in validationPlan.Errors)
                {
                    Console.Error.WriteLine(planError);
                }

                return 1;
            }

            // Enabled stubs fail with CS2J9001 before any directory is created.
            var enabledStub = validationPlan.Enabled
                .FirstOrDefault(pass => pass.Status == PassStatus.Stub);
            if (enabledStub != null)
            {
                Console.Error.WriteLine(ProjectPassRunner.StubMessage(enabledStub));
                return 1;
            }

            Directory.CreateDirectory(opts.Destination);

            var chain = await CliPreprocessChain.RunAsync(new CliChainRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
                VerifyBuild = opts.VerifyBuild,
                VerifyTests = opts.VerifyTests,
                Strict = opts.LocalFunctionStrict,
                TupleNamespace = opts.TupleNamespace,
                MakerOptionsFactory = () => new CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructMakerOptions
                {
                    ForceReadOnlyQualifiedNames = LoadReadOnlyForceOverrides(opts.ReadOnlyOverrides),
                },
                RecordReadOnlyOptionsFactory = () => BuildRecordReadOnlyOptions(
                    opts.RecordReadOnlyOverrides, strict: false),
                JavaKeywordOptionsFactory = () => BuildJavaKeywordOptions(
                    opts.JavaKeywordsOverrides, strict: false),
                MemberDesugarOptionsFactory = () => BuildMemberDesugarOptions(
                    opts.MemberDesugarOverrides, strict: false),
                StructValueSemanticsOptionsFactory = () => BuildStructValueSemanticsOptions(
                    opts.StructValueSemanticsOverrides),
                VarianceAuditOptionsFactory = () => BuildVarianceAuditOptions(
                    opts.VarianceStrict, opts.VarianceNoReportInfo),
                EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions
                {
                    Strict = opts.LocalFunctionStrict,
                    ReportInfo = opts.Verbose,
                },
                LexicalNormalizeOptionsFactory = () =>
                    new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions
                {
                    ReportInfo = opts.Verbose,
                    NormalizeFileScopedNamespaces = opts.LnNormalizeFileScoped,
                    ExternAliasSeverity = opts.LnExternAliasSeverityEffective,
                    NormalizeRawLiterals = !opts.LnNoRaw,
                    NormalizeUtf8Literals = !opts.LnNoU8,
                    NormalizeVerbatimLiterals = !opts.LnNoVerbatim,
                    NormalizeEscEscapes = !opts.LnNoEsc,
                },
                EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions
                {
                    Strict = opts.LocalFunctionStrict,
                },
                UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions
                {
                    Strict = opts.LocalFunctionStrict,
                },
                ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions
                {
                    CollectionExpr = new CSharpToJava.Core.ExpressionDesugar.CollectionExpr.CollectionExprOptions
                    {
                        Strict = opts.LocalFunctionStrict,
                    },
                    // C23 rule-level opt-in: default off keeps pre-C23
                    // byte-identical behavior; the family pass itself (C36)
                    // stays default-on. HandlerPolicy=Error (Q1: Error/Keep
                    // only), FormattableString preserved (0511), expression-
                    // tree canonical lowering deferred (M4/Q4).
                    Interpolations = new CSharpToJava.Core.ExpressionDesugar.Interpolations.InterpolationOptions
                    {
                        Enabled = opts.InterpolationExpand,
                        Strict = opts.LocalFunctionStrict,
                        HandlerPolicy = CSharpToJava.Core.ExpressionDesugar.Interpolations.HandlerUnsupportedPolicy.Error,
                        KeepFormattableString = true,
                        RewriteInExpressionTrees = false,
                        FoldConstants = true,
                    },
                },
                PlatformIsolationOptionsFactory = () => BuildPlatformIsolationOptions(
                    opts.PlatformReportOnly, opts.PlatformMode, opts.PlatformStrict, opts.PlatformConfig),
                LegacyToggles = legacyToggles,
                EnablePassIds = enablePassIds,
                DisablePassIds = opts.DisablePasses,
                EnableDecimalOps = opts.DecimalOpsEffective,
                ExtensionMappingConfigPath = opts.MappingConfig,
                ExtensionMethodStrict = opts.ExtensionMethodStrict,
                ExtensionOverridesPath = opts.ExtensionOverridesPath,
            });

            if (!chain.Success)
            {
                return 1;
            }

            Console.WriteLine($"[preprocess] output: {chain.FinalSourcePath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (opts.Verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }

            return 1;
        }
    }

    private static async Task<int> ArrayMethod(ArrayMethodOptions opts)
    {
        var rewriterOptions = new ArrayMethodRewriterOptions(
            Verbose: opts.Verbose,
            Strict: opts.Strict);
        var rewriter = new ArrayMethodRewriter();

        // 单文件模式
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "array-method",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true));

            var result = rewriter.Rewrite(sourceCode, compilation.GetSemanticModel(tree), rewriterOptions);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));

                if (result.Statistics.InstanceMemberRewrites > 0 || result.Statistics.StaticArrayCalls > 0
                    || result.Statistics.ResizeStatements > 0 || result.Statistics.ForeachFlattened > 0
                    || result.Statistics.RankInlined > 0 || result.Statistics.MultiDimLengthRewrites > 0)
                {
                    var outputDir = Path.GetDirectoryName(Path.GetFullPath(opts.Output)) ?? ".";
                    var extensionPath = Path.Combine(outputDir, ArrayHelperSupportFiles.SingleFileHelperFileName);
                    await File.WriteAllTextAsync(extensionPath, ArrayHelperSupportFiles.GetArrayHelperFileContent(), new System.Text.UTF8Encoding(false));
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Wrote extension file: {extensionPath}");
                    }
                }

                if (opts.Verbose)
                {
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
                }
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
            }

            return result.Diagnostics.Any(d => d.Severity == ArrayMethodSeverity.Error) ? 1 : 0;
        }

        // 项目 / 目录模式
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }

        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }

        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectArrayMethodPreprocessor.PreprocessAsync(
            new ProjectArrayMethodPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                RewriterOptions = rewriterOptions,
                FilterUnsupportedProjects = !opts.NoFilterProjects,
            });

        Console.WriteLine(
            $"array-method: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.InstanceMemberRewrites} instance member rewrites, " +
            $"{preprocessResult.Statistics.StaticArrayCalls} static array calls, " +
            $"{preprocessResult.Statistics.ResizeStatements} resize statements, " +
            $"{preprocessResult.Statistics.ForeachFlattened} foreach flattened, " +
            $"{preprocessResult.Statistics.RankInlined} rank inlined, " +
            $"{preprocessResult.Statistics.MultiDimLengthRewrites} multi-dim length rewrites");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");
        return preprocessResult.Success ? 0 : 1;
    }

    /// <summary>
    /// 独立 record-readonly verb：单文件 -i/-o（只允许自包含 record，RR090）或
    /// 项目 -s/-d（整树驱动 + 计划级回退）。
    /// </summary>
    private static async Task<int> RecordReadOnlyVerbAsync(RecordReadOnlyVerbOptions opts)
    {
        var options = BuildRecordReadOnlyOptions(
            opts.Overrides,
            strict: opts.Strict,
            extraForce: opts.Force,
            extraSkip: opts.Skip,
            convertRecordClasses: opts.ConvertRecordClasses);

        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                sourceCode,
                new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                    Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest),
                path: Path.GetFullPath(opts.Input));
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "record-readonly-file",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            var preprocessor =
                new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyPreprocessor(options);
            preprocessor.BindCompilation(compilation);

            // 单文件模式自包含闸门（§3.3）：跨文件 partial/用点/接口连带/层次均不可见，
            // 凡计划需要这些的一律排除并报 RR090。
            var nonSelfContained = preprocessor.Plans
                .Where(p => p.Outcome == CSharpToJava.Core.RecordReadOnly.PlanOutcome.Converted
                    && (p.TouchedFiles.Any(f => !string.Equals(
                            Path.GetFullPath(f), tree.FilePath, StringComparison.OrdinalIgnoreCase))
                        || p.Ripples.Count > 0
                        || p.Migrations.Count > 0))
                .Select(p => p.Id)
                .ToList();
            if (nonSelfContained.Count > 0)
            {
                preprocessor.ExcludePlans(nonSelfContained);
            }

            var statistics = new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyStatistics();
            var diagnostics = new List<CSharpToJava.Core.RecordReadOnly.RecordReadOnlyDiagnostic>();
            var result = preprocessor.RewriteFile(
                sourceCode, compilation.GetSemanticModel(tree), statistics, diagnostics);
            foreach (var id in nonSelfContained)
            {
                var plan = preprocessor.Plans.First(p => p.Id == id);
                diagnostics.Add(new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyDiagnostic(
                    CSharpToJava.Core.RecordReadOnly.RecordReadOnlySeverity.Warning,
                    CSharpToJava.Core.RecordReadOnly.RecordReadOnlyDiagnosticCode.RR090,
                    "record is not self-contained within the single input file (partial parts, " +
                    "mutation sites, interface ripple, migrated call sites, or hierarchy members " +
                    "live outside the file); skipped in single-file mode.",
                    tree.FilePath, 0, 0, plan.RecordQualifiedName));
            }

            if (opts.Output != null)
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
            else
                Console.Write(result.OutputCode);

            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine(diagnostic.ToMsBuildString());

            return options.Strict && statistics.RecordsSkipped > 0 ? 1 : 0;
        }

        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
            new ProjectRecordReadOnlyPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                Options = options,
                FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
            });

        Console.WriteLine(
            $"record-readonly: {preprocessResult.Statistics.RecordStructsConverted} struct(s), " +
            $"{preprocessResult.Statistics.RecordClassesConverted} class(es) converted, " +
            $"{preprocessResult.Statistics.RecordsSkipped} skipped, " +
            $"{preprocessResult.Statistics.PlansRolledBack} plan(s) rolled back");
        foreach (var diagnostic in preprocessResult.Diagnostics)
            Console.Error.WriteLine(diagnostic.ToMsBuildString());
        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");

        if (options.Strict && preprocessResult.Statistics.RecordsSkipped > 0) return 1;
        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> GenericPreprocess(GenericPreprocessOptions opts)
    {
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("generic-preprocess",
                new[] { tree },
                new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            var preprocessor = new CSharpToJava.Core.GenericPreprocess.GenericPreprocessor();
            var genOpts = new CSharpToJava.Core.GenericPreprocess.GenericPreprocessOptions
            {
                Strict = opts.Strict,
                ReportInfo = opts.Verbose,
            };
            var result = preprocessor.Preprocess(sourceCode, compilation.GetSemanticModel(tree), genOpts);
            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose) Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else Console.Write(result.OutputCode);

            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.Message}");
            return result.Diagnostics.Any(d => d.Severity == CSharpToJava.Core.GenericPreprocess.GenericPreprocessSeverity.Warning) && opts.Strict ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectGenericPreprocessor.PreprocessAsync(new ProjectGenericPreprocessRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = opts.Destination,
            Force = true,
            Verbose = opts.Verbose,
            Options = new CSharpToJava.Core.GenericPreprocess.GenericPreprocessOptions
            {
                Strict = opts.Strict,
                ReportInfo = opts.Verbose,
            },
            FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
            VerifyBuild = opts.VerifyBuild,
            VerifyTests = opts.VerifyTests,
        });

        Console.WriteLine(
            $"generic: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean " +
            $"({preprocessResult.Statistics.DefaultExpressionsRewritten} default(T), " +
            $"{preprocessResult.Statistics.NewTypeParamRewritten} new T(), " +
            $"{preprocessResult.Statistics.NewTypeParamArrayRewritten} new T[n])");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            if (diagnostic.Severity != CSharpToJava.Core.GenericPreprocess.GenericPreprocessSeverity.Info || opts.Verbose)
            {
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.FilePath}: {diagnostic.Message}");
            }
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");

        if (opts.Strict && preprocessResult.Diagnostics.Any(d => d.Severity == CSharpToJava.Core.GenericPreprocess.GenericPreprocessSeverity.Warning))
            return 1;
        return preprocessResult.Success ? 0 : 1;
    }

    /// <summary>
    /// Standalone entry for the tuple-to-struct stage (same work the convert pipeline
    /// performs as its second step). Useful for inspecting the intermediate C# alone.
    /// </summary>
    private static async Task<int> TupleToStructVerbAsync(TupleToStructVerbOptions opts)
    {
        var rewriterOptions = new CSharpToJava.Core.TupleToStruct.TupleToStructOptions
        {
            NamespaceName = opts.Namespace,
        };

        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "tuple-to-struct",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            var preprocessor = new CSharpToJava.Core.TupleToStruct.TupleToStructPreprocessor(rewriterOptions);
            var statistics = new CSharpToJava.Core.TupleToStruct.TupleToStructStatistics();
            var diagnostics = new List<CSharpToJava.Core.TupleToStruct.TupleToStructDiagnostic>();
            var result = preprocessor.RewriteFile(
                sourceCode, compilation.GetSemanticModel(tree), statistics, diagnostics);

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                var structContent = preprocessor.GenerateStructFile();
                if (structContent != null)
                {
                    var outputDir = Path.GetDirectoryName(Path.GetFullPath(opts.Output)) ?? ".";
                    var structPath = Path.Combine(outputDir, rewriterOptions.GeneratedFileName);
                    await File.WriteAllTextAsync(structPath, structContent, new System.Text.UTF8Encoding(false));
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Wrote tuple struct file: {structPath}");
                    }
                }

                if (opts.Verbose)
                {
                    Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
                }
            }
            else
            {
                Console.Write(result.OutputCode);
            }

            foreach (var diagnostic in diagnostics)
            {
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
            }

            return diagnostics.Any(d => d.Severity == CSharpToJava.Core.TupleToStruct.TupleToStructSeverity.Error)
                ? 1
                : 0;
        }

        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }

        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }

        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectTupleToStructPreprocessor.PreprocessAsync(
            new ProjectTupleToStructPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                RewriterOptions = rewriterOptions,
                FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
            });

        Console.WriteLine(
            $"tuple-to-struct: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.TupleTypesReplaced} types, " +
            $"{preprocessResult.Statistics.TupleLiteralsRewritten} literals, " +
            $"{preprocessResult.Statistics.DeconstructionsExpanded} deconstructions, " +
            $"{preprocessResult.Statistics.StructsGenerated} structs generated");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");

        return preprocessResult.Success ? 0 : 1;
    }

    private static async Task<int> StripDefault(StripDefaultOptions opts)
    {
        var paramsCollectionsOptions = BuildParamsCollectionsOptions(
            opts.EnableParamsCollections,
            opts.ParamsCollectionsScope,
            opts.ParamsCollectionsExpandArrays,
            opts.Strict,
            out var paramsScopeError);
        if (paramsScopeError != null)
        {
            Console.Error.WriteLine($"Error: {paramsScopeError}");
            return 1;
        }

        var rewriterOptions = new DefaultParameterStripperOptions(
            Verbose: opts.Verbose,
            Strict: opts.Strict,
            GenerateOverloads: !opts.NoOverloads,
            PreserveDefaultMetadata: !opts.NoDefaultMetadata,
            ParamsCollections: paramsCollectionsOptions);

        // A solution / project file (or a directory) passed to --input must be
        // routed to project mode instead of being parsed as a single C# file.
        if (opts.Input != null && IsProjectOrSolutionInput(opts.Input))
        {
            opts.Source ??= opts.Input;
            opts.Destination ??= opts.Output;
        }
        else if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("strip-default",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var stripper = new DefaultParameterStripper();
            // C48: the params-collections rule needs a Collect pass to build its
            // declaration plans; skipped entirely while the rule is off so the
            // single-file output stays byte-for-byte identical.
            if (rewriterOptions.ParamsCollections is { Enabled: true })
            {
                stripper.Collect(tree, compilation.GetSemanticModel(tree), rewriterOptions);
            }
            var result = stripper.Strip(sourceCode, compilation.GetSemanticModel(tree), rewriterOptions);
            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose) Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else Console.Write(result.OutputCode);
            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.Message}");
            return result.Diagnostics.Any(d => d.Severity == DefaultParameterStripSeverity.Error) ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectDefaultParameterStripper.PreprocessAsync(
            new ProjectDefaultParameterStripRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                RewriterOptions = rewriterOptions,
                FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                VerifyBuild = opts.VerifyBuild,
                VerifyTests = opts.VerifyTests,
            });

        Console.WriteLine(
            $"strip-default: {preprocessResult.Statistics.FilesTransformed} transformed, " +
            $"{preprocessResult.Statistics.FilesSkippedClean} clean, " +
            $"{preprocessResult.Statistics.DeclarationsStripped} declarations stripped, " +
            $"{preprocessResult.Statistics.OverloadsGenerated} overloads generated");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.Message}");
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");

        return preprocessResult.Success ? 0 : 1;
    }

    /// <summary>
    /// True when an --input value actually denotes a solution, a project or a
    /// directory rather than a single C# source file.
    /// </summary>
    private static bool IsProjectOrSolutionInput(string input)
    {
        if (Directory.Exists(input)) return true;
        var extension = Path.GetExtension(input);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the C48 params-collections options from the CLI flags. Returns
    /// null while the rule is disabled (zero behavior change); reports an error
    /// for an unrecognized --params-collections-scope value. The kebab-case
    /// scope spellings (all|span|mutable-collection|interface|collection-builder)
    /// and the PascalCase enum names are both accepted, case-insensitively.
    /// </summary>
    private static ParamsCollectionsOptions? BuildParamsCollectionsOptions(
        bool enabled,
        string? scope,
        bool expandArrayCallSites,
        bool strict,
        out string? error)
    {
        error = null;
        if (!enabled)
        {
            return null;
        }

        var normalized = (scope ?? "all").Replace("-", string.Empty);
        if (!Enum.TryParse<ParamsCollectionsScope>(normalized, ignoreCase: true, out var parsedScope)
            || !Enum.IsDefined(parsedScope))
        {
            error = "Invalid --params-collections-scope value '" + scope + "'. " +
                    "Expected all|span|mutable-collection|interface|collection-builder.";
            return null;
        }

        return new ParamsCollectionsOptions(
            Enabled: true,
            Strict: strict,
            Scope: parsedScope,
            ExpandArrayCallSites: expandArrayCallSites,
            Builder: CollectionBuilderPolicy.Error);
    }

    private static async Task<int> MakeReadonly(MakeReadOnlyOptions opts)
    {
        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }
            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("make-readonly",
                new[] { tree },
                new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            var maker = new CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructMaker();
            var makerOpts = new CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructMakerOptions
            {
                Strict = opts.Strict,
                EnableMethodMigration = !opts.NoMethodMigration,
                EnableDtoConversion = !opts.NoDtoConversion,
                EnablePublicFieldConversion = !opts.NoPublicFieldConversion,
                UpdateCallSites = !opts.NoUpdateCallSites,
                ForceReadOnlyQualifiedNames = LoadReadOnlyForceOverrides(opts.ReadOnlyOverrides),
            };
            var result = maker.MakeReadOnly(tree, compilation.GetSemanticModel(tree), makerOpts);
            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.OutputCode ?? sourceCode, new System.Text.UTF8Encoding(false));
                if (opts.Verbose) Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
            }
            else Console.Write(result.OutputCode ?? sourceCode);

            foreach (var d in result.Diagnostics)
                Console.Error.WriteLine($"[{d.Severity}] {d.StructName}: {d.Reason}");
            return result.Diagnostics.Any(d => d.Severity == CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructSeverity.Warning) && opts.Strict ? 1 : 0;
        }

        // Project / directory mode
        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectReadOnlyStructPreprocessor.PreprocessAsync(new ProjectReadOnlyPreprocessRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = opts.Destination,
            Force = true,
            Verbose = opts.Verbose,
            MakerOptions = new CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructMakerOptions
            {
                Strict = opts.Strict,
                EnableMethodMigration = !opts.NoMethodMigration,
                EnableDtoConversion = !opts.NoDtoConversion,
                EnablePublicFieldConversion = !opts.NoPublicFieldConversion,
                UpdateCallSites = !opts.NoUpdateCallSites,
                ForceReadOnlyQualifiedNames = LoadReadOnlyForceOverrides(opts.ReadOnlyOverrides),
            },
            FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
            VerifyBuild = opts.VerifyBuild,
            VerifyTests = opts.VerifyTests,
        });

        Console.WriteLine(
            $"make-readonly: {preprocessResult.Statistics.StructsConverted} converted, " +
            $"{preprocessResult.Statistics.StructsSkipped} skipped, " +
            $"{preprocessResult.Statistics.StructsFailed} failed (scanned {preprocessResult.Statistics.StructsScanned})");

        foreach (var diagnostic in preprocessResult.Diagnostics)
        {
            if (diagnostic.Severity != CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructSeverity.Info || opts.Verbose)
            {
                Console.Error.WriteLine($"[{diagnostic.Severity}] {diagnostic.FilePath}: {diagnostic.StructName}: {diagnostic.Reason}");
            }
        }

        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");

        if (opts.Strict && preprocessResult.Diagnostics.Any(d => d.Severity == CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructSeverity.Warning))
            return 1;
        return preprocessResult.Success ? 0 : 1;
    }
}

// 命令行选项
[Verb("eliminate-goto", HelpText = "Eliminate goto/label from C# source")]
class EliminateGotoOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unsupported-method diagnostics as fatal")]
    public bool Strict { get; set; }
}

[Verb("make-readonly", HelpText = "Convert eligible structs to readonly structs")]
class MakeReadOnlyOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unconvertible structs as fatal")]
    public bool Strict { get; set; }
    [Option("no-method-migration", Default = false, HelpText = "Disable method migration (L5)")]
    public bool NoMethodMigration { get; set; }
    [Option("no-dto-conversion", Default = false, HelpText = "Disable DTO conversion (L3)")]
    public bool NoDtoConversion { get; set; }
    [Option("no-public-field-conversion", Default = false, HelpText = "Disable public field to property conversion (L4)")]
    public bool NoPublicFieldConversion { get; set; }
    [Option("no-update-call-sites", Default = false, HelpText = "Don't update call sites for migrated methods")]
    public bool NoUpdateCallSites { get; set; }

    [Option("no-filter-projects", Default = false, HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after readonly preprocessing")]
    public bool NoVerifyBuild { get; set; }

    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after readonly preprocessing")]
    public bool NoVerifyTests { get; set; }

    [Option("readonly-overrides", Required = false,
        HelpText = "Path to JSON config listing qualified type names to force as readonly structs (default: config/ReadOnlyStructOverrides.json)")]
    public string? ReadOnlyOverrides { get; set; }

    public bool FilterUnsupportedProjects => !NoFilterProjects;
    public bool VerifyBuild => !NoVerifyBuild;
    public bool VerifyTests => !NoVerifyTests;
}

[Verb("generic-preprocess", HelpText = "Rewrite generic C# patterns (default(T), new T(), new T[n]) to translation-friendly equivalents")]
class GenericPreprocessOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unrewritten generic patterns as fatal")]
    public bool Strict { get; set; }

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after generic preprocessing")]
    public bool NoVerifyBuild { get; set; }

    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after generic preprocessing")]
    public bool NoVerifyTests { get; set; }

    public bool FilterUnsupportedProjects => !NoFilterProjects;
    public bool VerifyBuild => !NoVerifyBuild;
    public bool VerifyTests => !NoVerifyTests;
}

[Verb("tuple-to-struct", HelpText = "Rewrite C# value tuples (types, literals, deconstructions) into generated readonly structs")]
class TupleToStructVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }

    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }

    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }

    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show detailed progress")]
    public bool Verbose { get; set; }

    [Option("namespace", Default = "Cs2jTuples", HelpText = "Namespace for the generated tuple structs")]
    public string Namespace { get; set; } = "Cs2jTuples";

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    public bool FilterUnsupportedProjects => !NoFilterProjects;
}

[Verb("record-readonly", HelpText = "Convert non-readonly records (record struct and optionally record class) into readonly/immutable records")]
class RecordReadOnlyVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file (self-contained records only)")]
    public string? Input { get; set; }

    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }

    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }

    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show detailed progress")]
    public bool Verbose { get; set; }

    [Option("overrides", HelpText = "Path to RecordReadOnlyOverrides.json")]
    public string? Overrides { get; set; }

    [Option("force", Separator = ',', HelpText = "Fully-qualified record names to force-convert (comma-separated)")]
    public IEnumerable<string>? Force { get; set; }

    [Option("skip", Separator = ',', HelpText = "Fully-qualified record names to exclude (comma-separated)")]
    public IEnumerable<string>? Skip { get; set; }

    [Option("strict", Default = false, HelpText = "Return non-zero exit code when any record is skipped")]
    public bool Strict { get; set; }

    [Option("convert-record-classes", Default = false, HelpText = "Also convert record class declarations (RC rules; semantic immutability)")]
    public bool ConvertRecordClasses { get; set; }

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    public bool FilterUnsupportedProjects => !NoFilterProjects;
}

[Verb("java-keywords", HelpText = "Normalize C# identifiers that clash with Java reserved words and downgrade operator true/false")]
class JavaKeywordsVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file (self-contained only)")]
    public string? Input { get; set; }

    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }

    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }

    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show detailed progress")]
    public bool Verbose { get; set; }

    [Option("overrides", HelpText = "Path to JavaKeywordsOverrides.json")]
    public string? Overrides { get; set; }

    [Option("force", Separator = ',', HelpText = "Fully-qualified type names to force operator downgrade even in expression trees (comma-separated)")]
    public IEnumerable<string>? Force { get; set; }

    [Option("skip", Separator = ',', HelpText = "Fully-qualified type names to exclude from operator downgrade (comma-separated)")]
    public IEnumerable<string>? Skip { get; set; }

    [Option("strict", Default = false, HelpText = "Return non-zero exit code when any plan is skipped")]
    public bool Strict { get; set; }

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    public bool FilterUnsupportedProjects => !NoFilterProjects;
}

[Verb("strip-default", HelpText = "Strip default parameter values and generate C# overloads")]
class StripDefaultOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("no-strict", Default = false, HelpText = "Treat skipped declarations as non-fatal (default: fatal)")]
    public bool NoStrict { get; set; }
    [Option("no-overloads", Default = false, HelpText = "Strip defaults but don't generate overloads")]
    public bool NoOverloads { get; set; }

    [Option("no-default-metadata", Default = false,
        HelpText = "Don't emit [DefaultParameterValue] on stripped parameters (loses reflection-visible defaults)")]
    public bool NoDefaultMetadata { get; set; }

    public bool Strict => !NoStrict;

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after strip-default preprocessing")]
    public bool NoVerifyBuild { get; set; }

    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after strip-default preprocessing")]
    public bool NoVerifyTests { get; set; }

    [Option("enable-params-collections", Default = false,
        HelpText = "Enable the C48 params-collections rule (non-array params declarations are materialized and params call sites are explicitized; default off)")]
    public bool EnableParamsCollections { get; set; }

    [Option("params-collections-scope", Default = "all",
        HelpText = "C48 params-collections scope gate: all|span|mutable-collection|interface|collection-builder (default all)")]
    public string ParamsCollectionsScope { get; set; } = "all";

    [Option("params-collections-expand-arrays", Default = false,
        HelpText = "C48 params-collections sub-switch (D8): explicitize classic params T[] call sites as array creations (default off)")]
    public bool ParamsCollectionsExpandArrays { get; set; }

    public bool FilterUnsupportedProjects => !NoFilterProjects;
    public bool VerifyBuild => !NoVerifyBuild;
    public bool VerifyTests => !NoVerifyTests;
}

[Verb("unsigned-type", HelpText = "Rewrite byte/ushort/uint/ulong keywords to System.* full names")]
class UnsignedTypeOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option("no-operator-rewrite", Default = false, HelpText = "Disable operator → extension method rewrite")]
    public bool NoOperatorRewrite { get; set; }
    [Option("no-cast-rewrite", Default = false, HelpText = "Disable cast rewrite")]
    public bool NoCastRewrite { get; set; }
    [Option("no-literal-rewrite", Default = false, HelpText = "Disable numeric literal erasure-safety rewrite")]
    public bool NoLiteralRewrite { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unsupported diagnostics as fatal")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
}

[Verb("preprocess", HelpText = "Run registered preprocess passes on a C# project/solution without Java conversion; use --list-passes for pass ids")]
class PreprocessVerbOptions
{
    [Option('p', "pass", Required = false, Separator = ',',
        HelpText = "Pass id(s) to run, in locked order (repeatable or comma-separated); when omitted the convert-project default chain runs")]
    public IEnumerable<string>? Passes { get; set; }

    [Option("enable-pass", Required = false, Separator = ',',
        HelpText = "Additionally enable a pass id on top of the default chain (repeatable or comma-separated)")]
    public IEnumerable<string>? EnablePasses { get; set; }

    [Option("disable-pass", Required = false, Separator = ',',
        HelpText = "Disable a pass id (repeatable or comma-separated)")]
    public IEnumerable<string>? DisablePasses { get; set; }

    [Option("list-passes", Default = false, HelpText = "List registered preprocess passes with status and defaults, then exit")]
    public bool ListPasses { get; set; }

    [Option('s', "source", Required = false, HelpText = "Source directory, .csproj, or .sln path")]
    public string? Source { get; set; }

    [Option('d', "destination", Required = false, HelpText = "Destination directory path")]
    public string? Destination { get; set; }

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file (compat namespace prefixes)")]
    public string? MappingConfig { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show detailed progress and info diagnostics")]
    public bool Verbose { get; set; }

    [Option("tuple-namespace", Default = "Cs2jTuples", HelpText = "Namespace for the generated tuple structs")]
    public string TupleNamespace { get; set; } = "Cs2jTuples";

    [Option("no-local-function-strict", Default = false,
        HelpText = "Report residual local functions as warnings instead of failing the conversion")]
    public bool NoLocalFunctionStrict { get; set; }

    [Option("interpolation-expand", Default = false,
        HelpText = "Enable C23 interpolation expansion inside expression-desugar " +
                   "(rewrite interpolated strings to string.Concat/string.Format; default off)")]
    public bool InterpolationExpand { get; set; }

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after passes that verify by default")]
    public bool NoVerifyBuild { get; set; }

    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after passes that verify by default")]
    public bool NoVerifyTests { get; set; }

    [Option("platform-report-only", Default = false,
        HelpText = "Force the C42 platform-isolation pass into Report mode (audit only; the M1 default)")]
    public bool PlatformReportOnly { get; set; }

    [Option("platform-mode", Default = "Report",
        HelpText = "C42 platform-isolation mode: Report|Reject|Stub|Facade (Reject/Stub/Facade fail until M2/M3)")]
    public string PlatformMode { get; set; } = "Report";

    [Option("platform-strict", Default = false,
        HelpText = "C42 platform-isolation strict: Error-class sites are reported as errors and fail the run")]
    public bool PlatformStrict { get; set; }

    [Option("platform-config", Required = false,
        HelpText = "Path to Cs2jPlatformIsolation.json (default: search upward from the working directory)")]
    public string? PlatformConfig { get; set; }

    [Option("readonly-overrides", Required = false,
        HelpText = "Path to JSON config listing qualified type names to force as readonly structs (default: config/ReadOnlyStructOverrides.json)")]
    public string? ReadOnlyOverrides { get; set; }

    [Option("record-readonly-overrides", Required = false,
        HelpText = "Path to RecordReadOnlyOverrides.json (default: config/RecordReadOnlyOverrides.json)")]
    public string? RecordReadOnlyOverrides { get; set; }

    [Option("java-keywords-overrides", Required = false,
        HelpText = "Path to JavaKeywordsOverrides.json (default: config/JavaKeywordsOverrides.json)")]
    public string? JavaKeywordsOverrides { get; set; }

    [Option("member-desugar-overrides", Required = false,
        HelpText = "Path to MemberDesugarOverrides.json (default: config/MemberDesugarOverrides.json)")]
    public string? MemberDesugarOverrides { get; set; }

    [Option("struct-value-semantics-overrides", Required = false,
        HelpText = "Path to StructValueSemanticsOverrides.json (default: config/StructValueSemanticsOverrides.json)")]
    public string? StructValueSemanticsOverrides { get; set; }

    [Option("extension-methods-strict", Default = false,
        HelpText = "extension-methods pass (C19): escalate CS2J1201-1207/1210/1220 warnings to errors (default off; M4)")]
    public bool ExtensionMethodStrict { get; set; }

    [Option("extension-methods-overrides", Required = false,
        HelpText = "Path to extension-methods host overrides JSON: {\"hosts\":{\"Full.Host\":\"skip|force\"},\"additionalLinqHosts\":[...]} (C19 M4)")]
    public string? ExtensionOverridesPath { get; set; }

    [Option("no-decimal-ops", Default = false,
        HelpText = "Disable the default decimal operator normalization inside numeric-boundary (C34)")]
    public bool NoDecimalOps { get; set; }

    [Option("variance-strict", Default = false,
        HelpText = "variance-audit: escalate unsafe audit sites to errors and abort the chain (M2 gate; default off)")]
    public bool VarianceStrict { get; set; }

    [Option("variance-no-report-info", Default = false,
        HelpText = "variance-audit: suppress Info-level diagnostics in the chain stream (the report JSON always keeps every level)")]
    public bool VarianceNoReportInfo { get; set; }

    [Option("ln-normalize-file-scoped", Default = false,
        HelpText = "lexical-normalize: rewrite file-scoped namespaces into block namespaces (LN-7)")]
    public bool LnNormalizeFileScoped { get; set; }

    [Option("ln-extern-alias-severity", Default = "Error",
        HelpText = "lexical-normalize: severity for extern alias sites, one of Error|Warning|Info (LN-4)")]
    public string LnExternAliasSeverity { get; set; } = "Error";

    public CSharpToJava.Core.LexicalNormalize.LexicalNormalizeSeverity LnExternAliasSeverityEffective =>
        Enum.Parse<CSharpToJava.Core.LexicalNormalize.LexicalNormalizeSeverity>(
            LnExternAliasSeverity, ignoreCase: true);

    [Option("ln-no-raw", Default = false,
        HelpText = "lexical-normalize: disable raw string literal normalization (LL-1)")]
    public bool LnNoRaw { get; set; }

    [Option("ln-no-u8", Default = false,
        HelpText = "lexical-normalize: disable u8 literal expansion to byte[] (LL-2)")]
    public bool LnNoU8 { get; set; }

    [Option("ln-no-verbatim", Default = false,
        HelpText = "lexical-normalize: disable verbatim string normalization (LL-3)")]
    public bool LnNoVerbatim { get; set; }

    [Option("ln-no-esc", Default = false,
        HelpText = "lexical-normalize: disable \\e to \\u001B rewrite in regular strings/chars (LL-6)")]
    public bool LnNoEsc { get; set; }

    public bool LocalFunctionStrict => !NoLocalFunctionStrict;
    public bool FilterUnsupportedProjects => !NoFilterProjects;
    public bool VerifyBuild => !NoVerifyBuild;
    public bool VerifyTests => !NoVerifyTests;
    public bool DecimalOpsEffective => !NoDecimalOps;
}

[Verb("ref-out-in", HelpText = "Rewrite ref/out/in parameters to ByRef<T> proxy model (C# → C# source rewrite)")]
class RefOutInVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat residual ref/out/in as fatal")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification")]
    public bool NoVerifyBuild { get; set; }
    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification")]
    public bool NoVerifyTests { get; set; }
}

[Verb("array-method", HelpText = "Rewrite array instance members and System.Array static calls to Cs2jArray.ArrayHelper")]
class ArrayMethodOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unsupported diagnostics as fatal")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
}

[Verb("foreach-deconstruction", HelpText = "Lower C# deconstruction foreach over KeyValuePair into plain foreach + explicit locals")]
class ForeachDeconstructionVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat skipped deconstruction foreach as errors")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
}

[Verb("delegate-preprocess", HelpText = "Rewrite delegate +/- / += / -= and anonymous methods")]
class DelegatePreprocessVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat residual delegate operators as errors instead of warnings")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after delegate preprocessing")]
    public bool NoVerifyBuild { get; set; }
    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after delegate preprocessing")]
    public bool NoVerifyTests { get; set; }
}

[Verb("null-coalesce", HelpText = "Rewrite ?? and ??= operators to explicit ternary / if-assignment (single-file or project mode)")]
class NullCoalesceVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat residual ??/??= as errors instead of warnings")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after null-coalesce preprocessing")]
    public bool NoVerifyBuild { get; set; }
    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after null-coalesce preprocessing")]
    public bool NoVerifyTests { get; set; }
}

// 命令行选项
[Verb("convert", HelpText = "Convert a C# file to Java")]
class ConvertOptions
{

    [Option('t', "target", Default = "java", HelpText = "Target language: java")]
    public string Target { get; set; } = "java";
    [Option('i', "input", Required = true, HelpText = "Input C# file path")]
    public string Input { get; set; } = string.Empty;

    [Option('o', "output", Required = false, HelpText = "Output Java file path (default: stdout)")]
    public string? Output { get; set; }

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('j', "java-version", Default = "Java28", HelpText = "Target Java version (Java28)")]
    public string JavaVersion { get; set; } = "Java28";

    [Option("no-records", Default = false, HelpText = "Don't use Java records for C# records")]
    public bool NoRecords { get; set; }

    [Option("use-optional", Default = false, HelpText = "Use Optional for nullable types")]
    public bool UseOptionalForNullable { get; set; }

    [Option("no-javadoc", Default = false, HelpText = "Don't generate JavaDoc comments")]
    public bool NoJavaDoc { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show diagnostic messages")]
    public bool Verbose { get; set; }

    [Option("no-linq-rewrite", Default = false, HelpText = "Don't pre-process LINQ to procedural code")]
    public bool NoLinqRewrite { get; set; }

    [Option("prefer-stream-api", Default = false, SetName = "linq-strategy", HelpText = "Prefer Java Stream API for LINQ conversion (default for Java 28)")]
    public bool PreferStreamApiFlag { get; set; }

    [Option("prefer-procedural", Default = false, SetName = "linq-strategy", HelpText = "Prefer procedural loops for LINQ conversion")]
    public bool PreferProceduralFlag { get; set; }

    [Option("no-parallel-project-passes", Default = false, HelpText = "Disable parallel execution for safe project-level tree-local passes")]
    public bool NoParallelProjectPasses { get; set; }

    [Option("use-value-class", Default = false, HelpText = "Use Java Value Class (JEP 401) for C# readonly struct (requires Java 28+)")]
    public bool UseValueClass { get; set; }

    // 便捷属性
    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;
    public bool EnableParallelProjectPasses => !NoParallelProjectPasses;

    /// <summary>Resolve PreferStreamApi: explicit flags override, otherwise null (version-based default).</summary>
    public bool? PreferStreamApi => PreferStreamApiFlag ? true : PreferProceduralFlag ? false : null;
}

[Verb("convert-project", HelpText = "Convert a C# project to Java")]
class ConvertProjectOptions
{

    [Option('t', "target", Default = "java", HelpText = "Target language: java")]
    public string Target { get; set; } = "java";
    [Option('s', "source", Required = true, HelpText = "Source directory, .csproj, or .sln path")]
    public string Source { get; set; } = string.Empty;

    [Option('d', "destination", Required = true, HelpText = "Destination directory path")]
    public string Destination { get; set; } = string.Empty;

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('j', "java-version", Default = "Java28", HelpText = "Target Java version (Java28)")]
    public string JavaVersion { get; set; } = "Java28";

    [Option('f', "force", Default = true, HelpText = "Overwrite existing files")]
    public bool Force { get; set; }

    [Option("no-cache", Default = true, HelpText = "Disable fingerprint-based caching of previous outputs")]
    public bool NoCache { get; set; }

    [Option("no-records", Default = false, HelpText = "Don't use Java records")]
    public bool NoRecords { get; set; }

    [Option("use-optional", Default = false, HelpText = "Use Optional for nullable types")]
    public bool UseOptionalForNullable { get; set; }

    [Option("no-javadoc", Default = false, HelpText = "Don't generate JavaDoc")]
    public bool NoJavaDoc { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show detailed progress")]
    public bool Verbose { get; set; }

    [Option("no-linq-rewrite", Default = false, HelpText = "Don't pre-process LINQ to procedural code")]
    public bool NoLinqRewrite { get; set; }

    [Option("prefer-stream-api", Default = false, SetName = "linq-strategy", HelpText = "Prefer Java Stream API for LINQ conversion (default for Java 28)")]
    public bool PreferStreamApiFlag { get; set; }

    [Option("prefer-procedural", Default = false, SetName = "linq-strategy", HelpText = "Prefer procedural loops for LINQ conversion")]
    public bool PreferProceduralFlag { get; set; }

    [Option("maven-group-id", Default = "io.github.ningpp", HelpText = "Maven groupId")]
    public string MavenGroupId { get; set; } = "io.github.ningpp";

    [Option("maven-version", Default = "0.0.1-SNAPSHOT", HelpText = "Maven version")]
    public string MavenVersion { get; set; } = "0.0.1-SNAPSHOT";

    [Option("include-tests", Default = true, HelpText = "Include discovered test projects and write them to src/test/java")]
    public bool IncludeTests { get; set; } = true;

    [Option("linq-report", Default = false, HelpText = "Output LINQ preprocessing report (rewrite stats, skipped chains, uncovered operators)")]
    public bool LinqReport { get; set; }

    [Option("no-eliminate-goto", Default = false, HelpText = "Disable the default goto-elimination preprocessing step")]
    public bool NoEliminateGoto { get; set; }

    [Option("no-unsigned-type", Default = false,
        HelpText = "Disable the default unsigned-type preprocessing step")]
    public bool NoUnsignedType { get; set; }

    [Option("no-ref-out-in", Default = false,
        HelpText = "Disable the default ref/out/in preprocessing step")]
    public bool NoRefOutIn { get; set; }
    
    [Option("no-make-readonly", Default = false, HelpText = "Disable the default readonly-struct preprocessing step")]
    public bool NoMakeReadOnly { get; set; }

    [Option("generic-preprocess", Default = true, HelpText = "Run the generic-preprocessing step (default(T)/new T()/new T[n] lowering) before conversion")]
    public bool GenericPreprocess { get; set; }

    [Option("no-strip-default", Default = false,
        HelpText = "Disable the default strip-default-parameter preprocessing step")]
    public bool NoStripDefault { get; set; }

    [Option("enable-params-collections", Default = false,
        HelpText = "Enable the C48 params-collections rule inside strip-default (non-array params declarations are materialized and params call sites are explicitized; default off)")]
    public bool EnableParamsCollections { get; set; }

    [Option("params-collections-scope", Default = "all",
        HelpText = "C48 params-collections scope gate: all|span|mutable-collection|interface|collection-builder (default all)")]
    public string ParamsCollectionsScope { get; set; } = "all";

    [Option("params-collections-expand-arrays", Default = false,
        HelpText = "C48 params-collections sub-switch (D8): explicitize classic params T[] call sites as array creations (default off)")]
    public bool ParamsCollectionsExpandArrays { get; set; }

    [Option("no-tuple-to-struct", Default = false,
        HelpText = "Disable the default tuple-to-struct preprocessing step (C# value tuples to generated readonly structs)")]
    public bool NoTupleToStruct { get; set; }

    [Option("no-foreach-deconstruction", Default = false,
        HelpText = "Disable the default foreach-deconstruction preprocessing step")]
    public bool NoForeachDeconstruction { get; set; }

[Option("no-null-coalesce", Default = false,
    HelpText = "Disable the default null-coalesce (?? / ??=) preprocessing step")]
    public bool NoNullCoalesce { get; set; }

    [Option("no-delegate-preprocess", Default = false,
        HelpText = "Disable the default delegate (+/- / += / -= / anonymous method) preprocessing step")]
    public bool NoDelegatePreprocess { get; set; }

    [Option("no-strip-conditional", Default = false,
        HelpText = "Disable the default conditional-compilation (#if/#elif/#else/#endif) preprocessing step")]
    public bool NoStripConditional { get; set; }

    [Option("no-enum-preprocess", Default = false,
        HelpText = "Disable the default enum bitwise / HasFlag preprocessing step")]
    public bool NoEnumPreprocess { get; set; }

    [Option("tuple-namespace", Default = "Cs2jTuples",
        HelpText = "Namespace for the generated tuple structs")]
    public string TupleNamespace { get; set; } = "Cs2jTuples";

    [Option("local-function-lift", Default = true,
        HelpText = "Hoist C# local functions to class-level private methods before conversion (set false to disable)")]
    public bool LocalFunctionLift { get; set; }

    [Option("no-local-function-strict", Default = false,
        HelpText = "Report residual local functions as warnings instead of failing the conversion")]
    public bool NoLocalFunctionStrict { get; set; }

    [Option("interpolation-expand", Default = false,
        HelpText = "Enable C23 interpolation expansion inside expression-desugar " +
                   "(rewrite interpolated strings to string.Concat/string.Format; default off)")]
    public bool InterpolationExpand { get; set; }

    [Option("extra-deps", Required = false, HelpText = "Comma-separated Maven coordinates to add to every generated module, e.g. groupId:artifactId:version")]
    public string? ExtraDependencies { get; set; }

    [Option("use-value-class", Default = true, HelpText = "Use Java Value Class (JEP 401) for C# readonly struct (requires Java 28+)")]
    public bool UseValueClass { get; set; }

    [Option("no-filter-projects", Default = false, HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after readonly preprocessing")]
    public bool NoVerifyBuild { get; set; }

    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after readonly preprocessing")]
    public bool NoVerifyTests { get; set; }

    [Option("readonly-overrides", Required = false,
        HelpText = "Path to JSON config listing qualified type names to force as readonly structs (default: config/ReadOnlyStructOverrides.json)")]
    public string? ReadOnlyOverrides { get; set; }

    [Option("no-record-readonly", Default = false,
        HelpText = "Disable the default record-to-readonly-record preprocessing step (non-readonly records to readonly records)")]
    public bool NoRecordReadOnly { get; set; }

    [Option("record-readonly-overrides", Required = false,
        HelpText = "Path to RecordReadOnlyOverrides.json (default: config/RecordReadOnlyOverrides.json)")]
    public string? RecordReadOnlyOverrides { get; set; }

    [Option("no-java-keywords", Default = false,
        HelpText = "Disable the default Java reserved-word normalization preprocessing step (rename identifiers that clash with Java keywords, downgrade operator true/false)")]
    public bool NoJavaKeywords { get; set; }

    [Option("java-keywords-overrides", Required = false,
        HelpText = "Path to JavaKeywordsOverrides.json (default: config/JavaKeywordsOverrides.json)")]
    public string? JavaKeywordsOverrides { get; set; }

    [Option("no-member-desugar", Default = false,
        HelpText = "Disable the default member desugaring preprocessing step (properties become get_X/set_X methods plus backing fields)")]
    public bool NoMemberDesugar { get; set; }

    [Option("member-desugar-overrides", Required = false,
        HelpText = "Path to MemberDesugarOverrides.json (default: config/MemberDesugarOverrides.json)")]
    public string? MemberDesugarOverrides { get; set; }

    [Option("no-equality-concat", Default = false,
        HelpText = "Disable the default equality/concat call-site materialization step (C47)")]
    public bool NoEqualityConcat { get; set; }

    [Option("no-decimal-ops", Default = false,
        HelpText = "Disable the default decimal operator normalization inside numeric-boundary (C34): decimal operators stay as-is in the intermediate tree")]
    public bool NoDecimalOps { get; set; }

    [Option("variance-strict", Default = false,
        HelpText = "variance-audit: escalate unsafe audit sites to errors and abort the chain (M2 gate; default off)")]
    public bool VarianceStrict { get; set; }

    [Option("variance-no-report-info", Default = false,
        HelpText = "variance-audit: suppress Info-level diagnostics in the chain stream (the report JSON always keeps every level)")]
    public bool VarianceNoReportInfo { get; set; }

    [Option("struct-value-semantics", Default = true,
        HelpText = "Enable the struct-value-semantics pass (defensive Clone insertion for residual mutable structs); disable with --disable-pass struct-value-semantics")]
    public bool StructValueSemantics { get; set; } = true;

    [Option("foreach-source", Default = true,
        HelpText = "Enable the foreach-source pass (wrap foreach over string with ToCharArray() and close residual deconstruction forms); disable with --disable-pass foreach-source")]
    public bool ForeachSource { get; set; } = true;

    [Option("struct-value-semantics-overrides", Required = false,
        HelpText = "Path to StructValueSemanticsOverrides.json (default: config/StructValueSemanticsOverrides.json)")]
    public string? StructValueSemanticsOverrides { get; set; }

    [Option("extension-methods-strict", Default = false,
        HelpText = "extension-methods pass (C19): escalate CS2J1201-1207/1210/1220 warnings to errors (default off; M4)")]
    public bool ExtensionMethodStrict { get; set; }

    [Option("extension-methods-overrides", Required = false,
        HelpText = "Path to extension-methods host overrides JSON: {\"hosts\":{\"Full.Host\":\"skip|force\"},\"additionalLinqHosts\":[...]} (C19 M4)")]
    public string? ExtensionOverridesPath { get; set; }

    [Option("no-array-method", Default = false,
        HelpText = "Disable the default array-method preprocessing step")]
    public bool NoArrayMethod { get; set; }

    [Option("no-iterator-lowering", Default = false,
        HelpText = "Disable the default iterator-lowering preprocessing step (yield state machine)")]
    public bool NoIteratorLowering { get; set; }

    [Option("enable-pass", Required = false, Separator = ',',
        HelpText = "Enable a preprocess pass by id, overriding defaults and legacy flags (repeatable or comma-separated; use the 'preprocess --list-passes' verb for ids)")]
    public IEnumerable<string>? EnablePasses { get; set; }

    [Option("disable-pass", Required = false, Separator = ',',
        HelpText = "Disable a preprocess pass by id, overriding defaults and legacy flags (repeatable or comma-separated; use the 'preprocess --list-passes' verb for ids)")]
    public IEnumerable<string>? DisablePasses { get; set; }

    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;
    public bool EliminateGoto => !NoEliminateGoto;
    public bool UnsignedType => !NoUnsignedType;
    public bool RefOutIn => !NoRefOutIn;
    public bool MakeReadOnly => !NoMakeReadOnly;
    public bool StripDefault => !NoStripDefault;
    public bool TupleToStruct => !NoTupleToStruct;
    public bool ForeachDeconstruction => !NoForeachDeconstruction;
    public bool NullCoalesce => !NoNullCoalesce;
    public bool DelegatePreprocess => !NoDelegatePreprocess;
    public bool EnumPreprocess => !NoEnumPreprocess;
    public bool StripConditional => !NoStripConditional;
    public bool LocalFunctionStrict => !NoLocalFunctionStrict;
    public bool FilterUnsupportedProjects => !NoFilterProjects;
    public bool VerifyBuild => !NoVerifyBuild;
    public bool VerifyTests => !NoVerifyTests;

    /// <summary>Passes whose enable flags were flipped to default-on; legacy toggle names preserved.</summary>
    public bool RecordReadOnly => !NoRecordReadOnly;
    public bool JavaKeywords => !NoJavaKeywords;
    public bool MemberDesugar => !NoMemberDesugar;
    /// <summary>Pass switch, default ON; disable with --no-equality-concat / --disable-pass equality-concat.</summary>
    public bool EqualityConcat { get; set; } = true;

    /// <summary>Effective legacy toggle: the explicit --no-equality-concat stays authoritative.</summary>
    public bool EqualityConcatEffective => EqualityConcat && !NoEqualityConcat;

    /// <summary>C34 family switch, default ON; kill with --no-decimal-ops (or --disable-pass numeric-boundary).</summary>
    public bool DecimalOpsEffective => !NoDecimalOps;

    public bool ArrayMethod => !NoArrayMethod;
    public bool IteratorLowering => !NoIteratorLowering;

    [Option("platform-report-only", Default = false,
        HelpText = "Force the C42 platform-isolation pass into Report mode (audit only; the M1 default)")]
    public bool PlatformReportOnly { get; set; }

    [Option("platform-mode", Default = "Report",
        HelpText = "C42 platform-isolation mode: Report|Reject|Stub|Facade (Reject/Stub/Facade fail until M2/M3)")]
    public string PlatformMode { get; set; } = "Report";

    [Option("platform-strict", Default = false,
        HelpText = "C42 platform-isolation strict: Error-class sites are reported as errors and fail the run")]
    public bool PlatformStrict { get; set; }

    [Option("platform-config", Required = false,
        HelpText = "Path to Cs2jPlatformIsolation.json (default: search upward from the working directory)")]
    public string? PlatformConfig { get; set; }

    /// <summary>Resolve PreferStreamApi: explicit flags override, otherwise null (version-based default).</summary>
    public bool? PreferStreamApi => PreferStreamApiFlag ? true : PreferProceduralFlag ? false : null;

    internal IReadOnlyList<JavaDependency>? ParsedExtraDependencies { get; set; }
    internal string? FingerprintSourcePath { get; set; }
    internal string? FingerprintInputRoot { get; set; }
    internal bool UsingGotoPreprocessedSource { get; set; }

    /// <summary>
    /// Intermediate directory names of every pass enabled by the resolved chain plan.
    /// Populated by ConvertProject before the fingerprint/destination cleanup runs.
    /// </summary>
    internal IReadOnlySet<string>? EnabledPassIntermediateDirectoryNames { get; set; }
}

[Verb("local-function-lift", HelpText = "Lift local functions to member methods (single-file or project mode)")]
class LocalFunctionLiftOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("no-strict", Default = false, HelpText = "Treat unconvertible local functions as warnings instead of fatal")]
    public bool NoStrict { get; set; }
    [Option("captures-at-end", Default = false, HelpText = "Place capture parameters after original parameters")]
    public bool CapturesAtEnd { get; set; }
    [Option("allow-ref-in-async", Default = false, HelpText = "Relax gate #8 for async/iterator call sites")]
    public bool AllowRefInAsync { get; set; }
    [Option("allow-interface-host", Default = false, HelpText = "Relax gate #4 for interface host types")]
    public bool AllowInterfaceHost { get; set; }

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after local function lift")]
    public bool NoVerifyBuild { get; set; }

    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after local function lift")]
    public bool NoVerifyTests { get; set; }

    public bool Strict => !NoStrict;
    public bool FilterUnsupportedProjects => !NoFilterProjects;
    public bool VerifyBuild => !NoVerifyBuild;
    public bool VerifyTests => !NoVerifyTests;
}

[Verb("analyze", HelpText = "Analyze a C# project and generate type mapping report")]
class AnalyzeOptions
{
    [Option('s', "source", Required = true, HelpText = "Source directory path")]
    public string Source { get; set; } = string.Empty;

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('r', "report", Required = true, HelpText = "Output report file path")]
    public string Report { get; set; } = string.Empty;
}

[Verb("enum-preprocess", HelpText = "Rewrite enum bitwise operations, compound assignments, and HasFlag to explicit integer operations + (E) casts (single-file or project mode)")]
class EnumPreprocessVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat residual enum bitwise/HasFlag as errors instead of warnings")]
    public bool Strict { get; set; }
    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }
    [Option("no-verify-build", Default = false, HelpText = "Skip 'dotnet build' verification after enum preprocessing")]
    public bool NoVerifyBuild { get; set; }
    [Option("no-verify-tests", Default = false, HelpText = "Skip 'dotnet test' verification after enum preprocessing")]
    public bool NoVerifyTests { get; set; }
}
