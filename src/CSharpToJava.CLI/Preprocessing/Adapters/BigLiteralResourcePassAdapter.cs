using System.Collections.Concurrent;
using System.Xml.Linq;
using CSharpToJava.Core.BigLiteralResource;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the lexical pre-pass <c>big-literal-resource</c> (second pass of
/// the chain, right after <c>conditional</c>).
/// <para>
/// Per file the work is pure — parse, find oversized literal payloads, encode
/// them, rewrite the call site — so the spine runs it in parallel. The
/// file-system side effects (payload files, <c>EmbeddedResource</c> items,
/// support-project reference) are applied serially in
/// <see cref="AfterFilesAsync"/>, in source-file order.
/// </para>
/// <para>
/// Why resources: a <c>DemoBigBytes.cs</c>/<c>DemoBigStrings.cs</c> over 1 MB
/// makes every downstream pass pay for a huge syntax tree, and the Java emitter
/// cannot express the result at all — a single <c>&lt;clinit&gt;</c> holding tens
/// of thousands of array elements exceeds the JVM 64 KB method limit, and a
/// string constant above 65535 modified-UTF-8 bytes exceeds the constant-pool
/// entry limit. Moving the payload into a resource keeps the source file (and
/// the generated Java file) small on both sides.
/// </para>
/// </summary>
internal sealed class BigLiteralResourcePassAdapter : ProjectPassAdapter
{
    private readonly BigLiteralResourceOptions _options;
    private readonly BigLiteralResourceExtractor _extractor;
    private readonly bool _verbose;

    private readonly BigLiteralResourceStatistics _statistics = new();
    private readonly List<BigLiteralResourceDiagnostic> _diagnostics = new();
    private readonly List<ExtractedPayload> _extracted = new();
    private readonly List<string> _rewrittenDestinationFiles = new();

    /// <summary>Per-directory nearest-project cache; parallel rewrite shares it.</summary>
    private readonly ConcurrentDictionary<string, string?> _projectByDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    public BigLiteralResourcePassAdapter(BigLiteralResourceOptions options, bool verbose)
    {
        _options = options;
        _extractor = new BigLiteralResourceExtractor(options);
        _verbose = verbose;
    }

    private sealed record ExtractedPayload(string DestinationFilePath, BigLiteralResourcePayload Payload);

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.BigLiteralResource);

    public override bool Verbose => _verbose;

    /// <summary>The per-file rewrite is a pure function of the file text.</summary>
    public override bool SupportsParallelFileRewrite => true;

    public override async Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Single source of truth for both spines: rewrite purely, then merge.
        var pure = await RewriteFilePureAsync(context);
        MergeFilePureResult(pure);
        return pure.Rewrite;
    }

    public override Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
    {
        var result = _extractor.RewriteFile(context.SourceCode, context.RelativePath);
        return Task.FromResult(new PassFilePureResult
        {
            Context = context,
            Rewrite = result.Changed
                ? new PassFileRewrite
                {
                    OutputCode = result.OutputCode,
                    Changed = true,
                    PreserveBom = true,
                }
                : null,
            PassState = result,
        });
    }

    public override void MergeFilePureResult(PassFilePureResult pure)
    {
        var context = pure.Context;
        var result = (BigLiteralResourceResult)pure.PassState!;

        MergeStatistics(result.Statistics);
        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (result.Changed)
        {
            _rewrittenDestinationFiles.Add(context.DestinationFilePath);
            foreach (var payload in result.Payloads)
            {
                _extracted.Add(new ExtractedPayload(context.DestinationFilePath, payload));
            }
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[{Descriptor.Id}] {(result.Changed ? "transformed" : "clean (copy)")}: " +
                context.RelativePath);
        }
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_extracted.Count == 0)
        {
            return;
        }

        // project file -> ordered, de-duplicated resource registrations
        var byProject = new Dictionary<string, List<(string IncludePath, string LogicalName)>>(
            StringComparer.OrdinalIgnoreCase);
        var writtenResourceDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _extracted)
        {
            var sourceDirectory = Path.GetDirectoryName(entry.DestinationFilePath)!;
            var resourceDirectory = Path.Combine(sourceDirectory, _options.ResourceDirectoryName);
            if (writtenResourceDirectories.Add(resourceDirectory))
            {
                Directory.CreateDirectory(resourceDirectory);
            }

            var resourceFilePath = Path.Combine(resourceDirectory, entry.Payload.FileName);
            if (!File.Exists(resourceFilePath))
            {
                await File.WriteAllBytesAsync(resourceFilePath, entry.Payload.Content);
            }

            var projectFile = FindNearestProject(resourceDirectory);
            if (projectFile == null)
            {
                _diagnostics.Add(new BigLiteralResourceDiagnostic(
                    BigLiteralResourceSeverity.Warning,
                    PreprocessDiagnosticCodes.BigLiteralResourceWiring,
                    $"no .csproj found above '{resourceDirectory}'; the payload " +
                    $"'{entry.Payload.ResourceName}' was written but not registered as a resource")
                {
                    FilePath = entry.DestinationFilePath,
                });
                continue;
            }

            if (!byProject.TryGetValue(projectFile, out var items))
            {
                items = new List<(string, string)>();
                byProject[projectFile] = items;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            var includePath = Path.GetRelativePath(projectDirectory, resourceFilePath);
            if (!items.Any(item =>
                    string.Equals(item.LogicalName, entry.Payload.ResourceName, StringComparison.Ordinal)))
            {
                items.Add((includePath, entry.Payload.ResourceName));
            }
        }

        foreach (var (projectFile, items) in byProject)
        {
            RegisterResourceItems(projectFile, items);
        }

        // The rewritten call sites reference Cs2jPrelude.Cs2jResource, so every
        // host project needs the support project injected before the verification
        // build runs (the delegate pass injects the same library later too; both
        // release/reference steps are idempotent).
        var libDirectory = Path.Combine(tree.IntermediateRoot, PreludeSupportFiles.LibDirectoryName);
        await PreludeSupportFiles.ReleaseAsync(libDirectory);
        var libProjectPath = Path.Combine(libDirectory, Cs2jPreludeProject.ProjectFileName);

        foreach (var projectFile in byProject.Keys)
        {
            SupportLibraryProjectReference.Ensure(
                projectFile,
                libProjectPath,
                PreludeSupportFiles.LibDirectoryName,
                tree.IntermediateRoot,
                message => _diagnostics.Add(new BigLiteralResourceDiagnostic(
                    BigLiteralResourceSeverity.Warning,
                    PreprocessDiagnosticCodes.BigLiteralResourceWiring,
                    message)));
        }
    }

    /// <summary>
    /// Append one <c>EmbeddedResource</c> item per payload, with the logical name
    /// the loader looks up at runtime. Idempotent: a registration that already
    /// carries the same <c>LogicalName</c> is never duplicated.
    /// </summary>
    private void RegisterResourceItems(
        string projectFilePath,
        IReadOnlyList<(string IncludePath, string LogicalName)> items)
    {
        if (!File.Exists(projectFilePath) || items.Count == 0)
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _diagnostics.Add(new BigLiteralResourceDiagnostic(
                BigLiteralResourceSeverity.Warning,
                PreprocessDiagnosticCodes.BigLiteralResourceWiring,
                $"could not read '{projectFilePath}' to register resources: {ex.Message}"));
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Add(new BigLiteralResourceDiagnostic(
                BigLiteralResourceSeverity.Warning,
                PreprocessDiagnosticCodes.BigLiteralResourceWiring,
                $"'{projectFilePath}' is not an MSBuild project; resources were written but not registered"));
            return;
        }

        var existingLogicalNames = root.Descendants()
            .Where(element => element.Name.LocalName == "EmbeddedResource")
            .SelectMany(element => element.Elements()
                .Where(child => child.Name.LocalName == "LogicalName")
                .Select(child => child.Value)
                .Concat(element.Attribute("LogicalName") is { } attribute
                    ? new[] { attribute.Value }
                    : Array.Empty<string>()))
            .ToHashSet(StringComparer.Ordinal);

        var pending = items
            .Where(item => !existingLogicalNames.Contains(item.LogicalName))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        // LogicalName is emitted as a CHILD element on purpose:
        // SolutionLoader.ScanProjectResources reads it from item.Elements(), and
        // the Java side needs the logical name as the classpath-relative path.
        var group = new XElement(
            "ItemGroup",
            pending.Select(item => new XElement(
                "EmbeddedResource",
                new XAttribute("Include", item.IncludePath),
                new XElement("LogicalName", item.LogicalName))));

        root.Add(new XText(Environment.NewLine + "  "), group, new XText(Environment.NewLine));

        try
        {
            document.Save(projectFilePath, SaveOptions.DisableFormatting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Add(new BigLiteralResourceDiagnostic(
                BigLiteralResourceSeverity.Warning,
                PreprocessDiagnosticCodes.BigLiteralResourceWiring,
                $"could not write '{projectFilePath}': {ex.Message}"));
        }
    }

    private string? FindNearestProject(string directory)
        => _projectByDirectory.GetOrAdd(directory, static path =>
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                var candidate = Directory
                    .EnumerateFiles(current, "*.csproj", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (candidate != null)
                {
                    return candidate;
                }

                var parent = Path.GetDirectoryName(current);
                if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                current = parent;
            }

            return null;
        });

    private void MergeStatistics(BigLiteralResourceStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.ArraysExtracted += source.ArraysExtracted;
        _statistics.StringsExtracted += source.StringsExtracted;
        _statistics.PayloadBytes += source.PayloadBytes;
        _statistics.SkippedNonLiteral += source.SkippedNonLiteral;
        _statistics.SkippedConst += source.SkippedConst;
        _statistics.SkippedUnsupportedText += source.SkippedUnsupportedText;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new BigLiteralResourceDiagnostic(
            BigLiteralResourceSeverity.Warning,
            PreprocessDiagnosticCodes.BigLiteralRestoreFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new BigLiteralResourceDiagnostic(
            BigLiteralResourceSeverity.Error,
            PreprocessDiagnosticCodes.BigLiteralBuildFailed,
            $"dotnet build failed with exit code {exitCode} after big-literal resource extraction."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new BigLiteralResourceDiagnostic(
            BigLiteralResourceSeverity.Error,
            PreprocessDiagnosticCodes.BigLiteralTestsFailed,
            $"dotnet test failed with exit code {exitCode} after big-literal resource extraction."));

    public override bool HasErrors
        => _diagnostics.Any(diagnostic => diagnostic.Severity == BigLiteralResourceSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"big-literal-resource: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.ArraysExtracted} array(s), " +
           $"{_statistics.StringsExtracted} string(s), " +
           $"{_statistics.PayloadBytes} payload bytes, " +
           $"{_statistics.SkippedNonLiteral + _statistics.SkippedConst + _statistics.SkippedUnsupportedText} skipped";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(diagnostic => verbose || diagnostic.Severity != BigLiteralResourceSeverity.Info)
            .Select(diagnostic =>
            {
                var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : string.Empty;
                return $"[{diagnostic.Severity}] {diagnostic.Code} {prefix}{diagnostic.Message}";
            })
            .ToList();
}
