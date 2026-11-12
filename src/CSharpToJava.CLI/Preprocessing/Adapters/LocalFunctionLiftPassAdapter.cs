using System.Text;
using System.Xml.Linq;
using CSharpToJava.Core.ConditionalDirectiveStrip;
using CSharpToJava.Core.LocalFunctionLift;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CoreLocalFunctionLiftOptions = CSharpToJava.Core.LocalFunctionLift.LocalFunctionLiftOptions;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>Adapter for the local-function lift pass (former ProjectLocalFunctionLiftPreprocessor).</summary>
internal sealed class LocalFunctionLiftPassAdapter : ProjectPassAdapter
{
    /// <summary>
    /// Symbols the standard spine has always parsed the tree with. Kept as the
    /// baseline configuration so the default pass output is unchanged.
    /// </summary>
    internal static readonly string[] DefaultPreprocessorSymbols = ["NETCOREAPP"];

    private readonly CoreLocalFunctionLiftOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly LocalFunctionLiftPreprocessor _preprocessor = new();
    private readonly List<LocalFunctionLiftDiagnostic> _diagnostics = new();
    private readonly LocalFunctionLiftStatistics _statistics = new();

    public LocalFunctionLiftPassAdapter(
        CoreLocalFunctionLiftOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.LocalFunctionLift);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public LocalFunctionLiftStatistics Statistics => _statistics;
    public IReadOnlyList<LocalFunctionLiftDiagnostic> Diagnostics => _diagnostics;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(preprocessorSymbols: DefaultPreprocessorSymbols);

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[local-fn] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        var files = EnumerateIntermediateCompileFiles(tree);
        var configs = ConditionalSymbolConfigResolver.Resolve(
            tree.Layout, DefaultPreprocessorSymbols, out var configWarnings);
        foreach (var warning in configWarnings)
        {
            _diagnostics.Add(new LocalFunctionLiftDiagnostic(
                LocalFunctionLiftSeverity.Warning, warning));
        }

        if (_verbose)
        {
            foreach (var config in configs)
            {
                Console.WriteLine(
                    $"[local-fn] symbol config '{config.Name}': " +
                    $"{config.Symbols.Count} symbol(s)");
            }
        }

        // The standard spine already rewrote every file with the default
        // configuration. A local function inside a conditionally compiled region
        // it disabled (e.g. `#if DEBUG`) is trivia there, so it survives that
        // pass; re-run the lift for every symbol configuration the projects of
        // this tree actually compile with.
        for (var index = 1; index < configs.Count; index++)
        {
            ApplySymbolConfiguration(tree, files, configs[index], index);
        }

        // residual 一律以中间树实测为准，不做任何“有意保留”扣除：
        // 任何未提升的局部函数都是改写残留，strict 下必须暴露。
        // 每个工程实际编译的符号配置都要检查，否则只在默认配置下不可见的
        // 条件编译分支里的局部函数会漏检。
        var residualLocations = new List<string>();
        var residualCount = CountResidualLocalFunctions(
            tree, files, configs, residualLocations);
        _statistics.LocalFunctionsResidual = residualCount;

        if (residualCount > 0)
        {
            var severity = _strict
                ? LocalFunctionLiftSeverity.Error
                : LocalFunctionLiftSeverity.Warning;
            // V-09: aggregate residuals must point at the surviving
            // declarations — "[Error] :0: 2 residual" gave no way to locate
            // them without grepping the intermediate tree by hand.
            var shown = residualLocations.Take(5).ToList();
            var suffix = shown.Count > 0
                ? " Locations: " + string.Join("; ", shown)
                  + (residualLocations.Count > shown.Count
                      ? $" (and {residualLocations.Count - shown.Count} more)"
                      : string.Empty)
                : string.Empty;
            _diagnostics.Add(new LocalFunctionLiftDiagnostic(
                severity,
                $"{residualCount} residual local function(s) remaining after preprocessing.{suffix}",
                null, 0, LocalFunctionSkipReason.None));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after local function lift."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after local function lift."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == LocalFunctionLiftSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"local-function-lift: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.LocalFunctionsLifted} lifted, " +
           $"{_statistics.LocalFunctionsSkipped} skipped, " +
           $"{_statistics.LocalFunctionsResidual} residual, " +
           $"holder={_statistics.CapturesByHolder}, " +
           $"rolled-back={_statistics.FilesRolledBackAtomicity}";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d => $"[{d.Severity}] {d.FilePath}:{d.Line}: {d.Message}")
            .ToList();

    /// <summary>
    /// Parse the intermediate tree once per symbol configuration and lift the
    /// local functions that configuration makes visible. Runs at most two rounds
    /// so a configuration cannot keep producing work indefinitely; every round
    /// re-parses the current text (the previous round, or a previous
    /// configuration, may have rewritten it).
    /// </summary>
    private void ApplySymbolConfiguration(
        PassTreeContext tree,
        IReadOnlyList<string> files,
        ConditionalSymbolConfig config,
        int configIndex)
    {
        var parseOptions = new CSharpParseOptions(preprocessorSymbols: config.Symbols);
        for (var round = 0; round < 2; round++)
        {
            var pending = new List<(string File, string Text)>();
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                var parsed = CSharpSyntaxTree.ParseText(text, parseOptions, path: file);
                if (ContainsLocalFunction(parsed))
                {
                    pending.Add((file, text));
                }
            }

            if (_verbose)
            {
                Console.WriteLine(
                    $"[local-fn] config '{config.Name}' round {round + 1}: " +
                    $"{pending.Count} file(s) with active local functions");
            }

            if (pending.Count == 0)
            {
                return;
            }

            var syntaxTrees = new List<SyntaxTree>(files.Count + 8);
            foreach (var file in files)
            {
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(
                    File.ReadAllText(file), parseOptions, path: file));
            }

            syntaxTrees.AddRange(CreateAdditionalSyntaxTrees(tree));
            var compilation = CSharpCompilation.Create(
                $"{CompilationName}-{configIndex}-{round}",
                syntaxTrees,
                GetMetadataReferences(),
                CreateCompilationOptions());

            var changed = false;
            foreach (var (file, text) in pending)
            {
                var syntaxTree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
                    string.Equals(candidate.FilePath, file, StringComparison.OrdinalIgnoreCase));
                if (syntaxTree == null)
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(tree.IntermediateRoot, file);
                var result = _preprocessor.Preprocess(
                    text, compilation.GetSemanticModel(syntaxTree), _options);
                MergeStatistics(result.Statistics);
                foreach (var diagnostic in result.Diagnostics)
                {
                    _diagnostics.Add(diagnostic with { FilePath = relativePath });
                }

                if (_verbose)
                {
                    Console.WriteLine(
                        $"[local-fn] config '{config.Name}' " +
                        $"{(result.Changed ? "transformed" : "clean")}: {relativePath}");
                }

                if (!result.Changed)
                {
                    continue;
                }

                File.WriteAllText(file, result.OutputCode, new UTF8Encoding(false));
                changed = true;
            }

            if (!changed)
            {
                return;
            }
        }
    }

    private static bool ContainsLocalFunction(SyntaxTree tree)
        => tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any();

    /// <summary>
    /// .cs files of the intermediate tree in the same set the standard spine
    /// compiled. Enumerating the whole directory would also pick up generated
    /// obj/ sources and change the analysis compilation.
    /// </summary>
    private static IReadOnlyList<string> EnumerateIntermediateCompileFiles(PassTreeContext tree)
    {
        var files = new List<string>(tree.CsFiles.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceFile in tree.CsFiles)
        {
            var file = string.Equals(
                Path.GetFullPath(tree.SourceRoot), Path.GetFullPath(tree.IntermediateRoot),
                StringComparison.OrdinalIgnoreCase)
                ? sourceFile
                : Path.Combine(
                    tree.IntermediateRoot,
                    Path.GetRelativePath(tree.SourceRoot, sourceFile));
            if (File.Exists(file) && seen.Add(file))
            {
                files.Add(file);
            }
        }

        return files;
    }

    private static int CountResidualLocalFunctions(
        PassTreeContext tree,
        IReadOnlyList<string> files,
        IReadOnlyList<ConditionalSymbolConfig> configs,
        List<string> locations)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var config in configs)
        {
            var parseOptions = config.IsDefault
                ? new CSharpParseOptions(
                    preprocessorSymbols: DefaultPreprocessorSymbols)
                : new CSharpParseOptions(preprocessorSymbols: config.Symbols);
            foreach (var csFile in files)
            {
                var content = File.ReadAllText(csFile);
                var syntaxTree = CSharpSyntaxTree.ParseText(content, parseOptions, path: csFile);
                foreach (var localFn in syntaxTree.GetRoot().DescendantNodes()
                             .OfType<LocalFunctionStatementSyntax>())
                {
                    var key = $"{csFile}|{localFn.SpanStart}|{localFn.Identifier.Text}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    count++;
                    var line = syntaxTree.GetLineSpan(localFn.Span)
                        .StartLinePosition.Line + 1;
                    locations.Add(
                        $"{Path.GetRelativePath(tree.IntermediateRoot, csFile)}" +
                        $"({line}): {localFn.Identifier.Text} [{config.Name}]");
                }
            }
        }

        locations.Sort(StringComparer.Ordinal);
        return count;
    }

    private void MergeStatistics(LocalFunctionLiftStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.LocalFunctionsLifted += source.LocalFunctionsLifted;
        _statistics.LocalFunctionsSkipped += source.LocalFunctionsSkipped;
        _statistics.CapturesByValue += source.CapturesByValue;
        _statistics.CapturesByRef += source.CapturesByRef;
        _statistics.CapturesByHolder += source.CapturesByHolder;
        _statistics.FilesRolledBackAtomicity += source.FilesRolledBackAtomicity;
        _statistics.PlansRolledBackAtomicity += source.PlansRolledBackAtomicity;
        foreach (var kvp in source.SkippedByReason)
        {
            if (!_statistics.SkippedByReason.TryAdd(kvp.Key, kvp.Value))
            {
                _statistics.SkippedByReason[kvp.Key] += kvp.Value;
            }
        }
    }
}

/// <summary>One preprocessor-symbol set the tree is parsed with.</summary>
internal sealed record ConditionalSymbolConfig(
    string Name,
    IReadOnlyList<string> Symbols,
    bool IsDefault = false);

/// <summary>
/// Derives the preprocessor-symbol configurations a project tree is actually
/// compiled with, from the <c>DefineConstants</c> literals of its
/// <c>.csproj</c>/<c>Directory.Build.props</c> files. A pass that only parses the
/// tree with one fixed set (historically <c>NETCOREAPP</c>) never sees local
/// functions inside the disabled branches of another configuration — for
/// example <c>#if DEBUG</c> members, which source-generator and test projects
/// really compile. <c>$(...)</c> references are treated as "append" markers,
/// mirroring <see cref="ProjectSymbolResolver"/>.
/// </summary>
internal static class ConditionalSymbolConfigResolver
{
    public const int MaxConfigurations = 8;

    private const string ReleaseConfiguration = "Release";

    public static IReadOnlyList<ConditionalSymbolConfig> Resolve(
        ProjectPreprocessSourceLayout layout,
        IReadOnlyList<string> baseSymbols,
        out IReadOnlyList<string> warnings)
    {
        var warningList = new List<string>();
        warnings = warningList;

        var baseList = Dedup(baseSymbols);
        var configs = new List<ConditionalSymbolConfig>
        {
            new("default", baseList, IsDefault: true),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal) { Signature(baseList) };
        var truncated = false;

        var unionLiterals = new List<string>();
        var projectLiterals = new List<(string Project, List<string> Literals)>();
        foreach (var project in EnumerateProjects(layout))
        {
            var literals = ResolveProjectLiterals(project);
            projectLiterals.Add((project, literals));
            foreach (var symbol in literals)
            {
                AddUnique(unionLiterals, symbol);
            }

            foreach (var symbol in CollectRawLiterals(project))
            {
                AddUnique(unionLiterals, symbol);
            }
        }

        Add(baseList, unionLiterals, "union");
        foreach (var (project, literals) in projectLiterals
                     .OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase))
        {
            if (literals.Count == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(layout.SourceRoot, project)
                .Replace(Path.DirectorySeparatorChar, '/');
            Add(baseList, literals, "project:" + relative);
        }

        return configs;

        void Add(
            IReadOnlyList<string> seeds,
            IReadOnlyList<string> literals,
            string name)
        {
            var symbols = Dedup(seeds.Concat(literals));
            var signature = Signature(symbols);
            if (!seen.Add(signature))
            {
                return;
            }

            if (configs.Count >= MaxConfigurations)
            {
                if (!truncated)
                {
                    truncated = true;
                    warningList.Add(
                        $"more preprocessor-symbol configurations than the limit of " +
                        $"{MaxConfigurations} were found; the remaining ones were skipped.");
                }

                return;
            }

            configs.Add(new ConditionalSymbolConfig(name, symbols));
        }
    }

    /// <summary>
    /// Symbols declared by one project after applying the historical
    /// <c>DefineConstants</c> semantics: ancestor props outermost-first, then
    /// the project itself; an element mentioning <c>$(DefineConstants)</c>
    /// appends, otherwise it replaces the inherited value.
    /// </summary>
    private static List<string> ResolveProjectLiterals(string projectPath)
    {
        var symbols = new List<string>();
        foreach (var props in GetAncestorDirectoryBuildProps(projectPath).Reverse())
        {
            ApplyDefineConstants(props, symbols);
        }

        ApplyDefineConstants(projectPath, symbols);
        return Dedup(symbols);
    }

    /// <summary>Every literal symbol any DefineConstants element of the project declares.</summary>
    private static List<string> CollectRawLiterals(string projectPath)
    {
        var symbols = new List<string>();
        var document = LoadDocument(projectPath);
        if (document?.Root == null)
        {
            return symbols;
        }

        foreach (var element in document.Root.Descendants().Where(element =>
                     element.Name.LocalName.Equals(
                         "DefineConstants", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var literal in LiteralParts(element.Value))
            {
                AddUnique(symbols, literal);
            }
        }

        return symbols;
    }

    private static void ApplyDefineConstants(string file, List<string> symbols)
    {
        var document = LoadDocument(file);
        if (document?.Root == null)
        {
            return;
        }

        foreach (var group in document.Root.Elements().Where(element =>
                     element.Name.LocalName.Equals(
                         "PropertyGroup", StringComparison.OrdinalIgnoreCase)))
        {
            if (!ProjectSymbolResolver.EvaluateCondition(
                    AttributeNamed(group, "Condition"), ReleaseConfiguration))
            {
                continue;
            }

            foreach (var element in group.Elements().Where(element =>
                         element.Name.LocalName.Equals(
                             "DefineConstants", StringComparison.OrdinalIgnoreCase)))
            {
                if (!ProjectSymbolResolver.EvaluateCondition(
                        AttributeNamed(element, "Condition"), ReleaseConfiguration))
                {
                    continue;
                }

                var value = element.Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var appends = false;
                var parts = new List<string>();
                foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var part = raw.Trim();
                    if (part.Length == 0)
                    {
                        continue;
                    }

                    if (part.Equals("$(DefineConstants)", StringComparison.OrdinalIgnoreCase))
                    {
                        appends = true;
                        continue;
                    }

                    if (part.Contains("$(", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    parts.Add(part);
                }

                if (!appends)
                {
                    symbols.Clear();
                }

                symbols.AddRange(parts);
            }
        }
    }

    private static IEnumerable<string> LiteralParts(string value)
    {
        foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0
                || part.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            yield return part;
        }
    }

    private static IEnumerable<string> EnumerateProjects(ProjectPreprocessSourceLayout layout)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in layout.ProjectFiles)
        {
            if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(project);
            }
            catch (Exception)
            {
                continue;
            }

            if (File.Exists(fullPath) && seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static IEnumerable<string> GetAncestorDirectoryBuildProps(string projectPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        while (!string.IsNullOrEmpty(directory))
        {
            var props = Path.Combine(directory, "Directory.Build.props");
            if (File.Exists(props))
            {
                yield return props;
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent == null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            directory = parent;
        }
    }

    private static XDocument? LoadDocument(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? AttributeNamed(XElement element, string localName)
        => element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static void AddUnique(List<string> symbols, string symbol)
    {
        if (!symbols.Contains(symbol, StringComparer.Ordinal))
        {
            symbols.Add(symbol);
        }
    }

    private static List<string> Dedup(IEnumerable<string> symbols)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var symbol in symbols)
        {
            var trimmed = symbol.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static string Signature(IReadOnlyList<string> symbols)
        => string.Join('\u0001', symbols.OrderBy(symbol => symbol, StringComparer.Ordinal));
}

