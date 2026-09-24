using CommandLine;
using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.CLI;

[Verb("strip-conditional", HelpText = "Evaluate and remove C# conditional compilation directives (#if/#elif/#else/#endif/#define/#undef) from C# source")]
internal sealed class StripConditionalOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }

    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }

    [Option('s', "source", SetName = "dir", HelpText = "Source directory (or .csproj/.sln: its directory is used as the root)")]
    public string? Source { get; set; }

    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }

    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }

    [Option("strict", Default = false, HelpText = "Treat warning diagnostics as fatal")]
    public bool Strict { get; set; }

    [Option("define", Required = false, HelpText = "Extra preprocessor symbol (repeatable, highest precedence)")]
    public IEnumerable<string> Define { get; set; } = Enumerable.Empty<string>();

    [Option("configuration", Default = "Release", HelpText = "Configuration used to evaluate project symbols")]
    public string Configuration { get; set; } = "Release";

    [Option("tfm", Required = false, HelpText = "Target framework override for symbol synthesis (for example net8.0)")]
    public string? Tfm { get; set; }

    [Option("remove-regions", Default = false, HelpText = "Also remove #region/#endregion directives")]
    public bool RemoveRegions { get; set; }
}

internal static class StripConditionalCommand
{
    internal static async Task<int> RunAsync(
        StripConditionalOptions options,
        TextWriter? standardOutput = null,
        TextWriter? standardError = null)
    {
        standardOutput ??= Console.Out;
        standardError ??= Console.Error;

        try
        {
            var extraDefines = options.Define
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .Select(symbol => symbol.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (options.Input != null)
            {
                return await RunSingleFileAsync(options, extraDefines, standardOutput, standardError);
            }

            return await RunDirectoryAsync(options, extraDefines, standardOutput, standardError);
        }
        catch (Exception exception)
        {
            await standardError.WriteLineAsync($"Error: {exception.Message}");
            if (options.Verbose)
            {
                await standardError.WriteLineAsync(exception.StackTrace);
            }

            return 1;
        }
    }

    private static async Task<int> RunSingleFileAsync(
        StripConditionalOptions options,
        IReadOnlyList<string> extraDefines,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (!File.Exists(options.Input))
        {
            await standardError.WriteLineAsync($"Error: Input file not found: {options.Input}");
            return 1;
        }

        var source = await SourceTextIo.ReadAsync(options.Input);
        var result = new ConditionalDirectiveStripper().Strip(
            source.Code,
            new ConditionalDirectiveStripOptions
            {
                RemoveRegions = options.RemoveRegions,
                Symbols = extraDefines,
            });

        await WriteDiagnosticsAsync(result.Diagnostics, options.Input, standardError);
        if (options.Output == null)
        {
            await standardOutput.WriteAsync(result.OutputCode);
        }
        else if (result.Changed)
        {
            await SourceTextIo.WriteAtomicallyAsync(options.Output, result.OutputCode, source.HasBom);
        }
        else
        {
            CopyFileVerbatim(options.Input, options.Output);
        }

        if (options.Verbose)
        {
            await standardError.WriteLineAsync(
                $"strip-conditional: 1 scanned, {(result.Changed ? 1 : 0)} transformed, " +
                $"{(result.Changed ? 0 : 1)} clean, " +
                $"{(result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error) ? 1 : 0)} failed, " +
                $"{result.Statistics.DirectivesRemoved} directives removed, " +
                $"{result.Statistics.DisabledBranchesRemoved} disabled blocks removed");
        }

        var hasErrors = result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
        var hasWarnings = result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Warning);
        return hasErrors || (options.Strict && hasWarnings) ? 1 : 0;
    }

    private static async Task<int> RunDirectoryAsync(
        StripConditionalOptions options,
        IReadOnlyList<string> extraDefines,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (options.Source == null || options.Destination == null)
        {
            await standardError.WriteLineAsync("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }

        var sourceIsDirectory = Directory.Exists(options.Source);
        var sourceIsProject = File.Exists(options.Source) &&
            (Path.GetExtension(options.Source).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(options.Source).Equals(".sln", StringComparison.OrdinalIgnoreCase));
        if (!sourceIsDirectory && !sourceIsProject)
        {
            await standardError.WriteLineAsync($"Error: Source directory/project/solution not found: {options.Source}");
            return 1;
        }

        var sourceRoot = sourceIsProject
            ? Path.GetDirectoryName(Path.GetFullPath(options.Source))!
            : Path.GetFullPath(options.Source);
        var destinationRoot = Path.GetFullPath(options.Destination);
        var inPlace = PathsEqual(sourceRoot, destinationRoot);
        Directory.CreateDirectory(destinationRoot);

        var symbolCache = new Dictionary<string, ProjectSymbols>(StringComparer.OrdinalIgnoreCase);
        var emittedWarningKeys = new HashSet<string>(StringComparer.Ordinal);
        var hadWarnings = false;

        async Task<ProjectSymbols> SymbolsForFileAsync(string file, string relativePath)
        {
            var projectPath = FindNearestProjectPath(Path.GetDirectoryName(file)!);
            var cacheKey = projectPath ?? "<no-project>";
            if (!symbolCache.TryGetValue(cacheKey, out var symbols))
            {
                symbols = ProjectSymbolResolver.ResolveForFile(
                    file,
                    options.Configuration,
                    options.Tfm,
                    extraDefines);
                symbolCache.Add(cacheKey, symbols);
            }

            foreach (var warning in symbols.Warnings)
            {
                if (emittedWarningKeys.Add(cacheKey + "\0" + warning))
                {
                    hadWarnings = true;
                    await standardError.WriteLineAsync($"[Warning] {relativePath}:0: {warning}");
                }
            }

            return symbols;
        }

        var scanned = 0;
        var transformed = 0;
        var clean = 0;
        var failed = 0;
        var directivesRemoved = 0;
        var disabledBlocksRemoved = 0;

        foreach (var file in EnumerateFilesSkippingRoot(sourceRoot, inPlace ? null : destinationRoot)
                     .Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            scanned++;
            var relativePath = Path.GetRelativePath(sourceRoot, file);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            var source = await SourceTextIo.ReadAsync(file);
            var symbols = await SymbolsForFileAsync(file, relativePath);
            var result = new ConditionalDirectiveStripper().Strip(
                source.Code,
                new ConditionalDirectiveStripOptions
                {
                    RemoveRegions = options.RemoveRegions,
                    Symbols = symbols.Symbols,
                });

            await WriteDiagnosticsAsync(result.Diagnostics, relativePath, standardError);
            var hasErrors = result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
            var hasWarnings = result.Diagnostics.Any(diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Warning);
            hadWarnings |= hasWarnings;
            directivesRemoved += result.Statistics.DirectivesRemoved;
            disabledBlocksRemoved += result.Statistics.DisabledBranchesRemoved;

            if (hasErrors)
            {
                failed++;
                if (!inPlace)
                {
                    CopyFileVerbatim(file, destinationPath);
                }

                continue;
            }

            if (result.Changed)
            {
                await SourceTextIo.WriteAtomicallyAsync(destinationPath, result.OutputCode, source.HasBom);
                transformed++;
                if (options.Verbose)
                {
                    await standardOutput.WriteLineAsync($"transformed: {relativePath}");
                }
            }
            else
            {
                if (!inPlace)
                {
                    CopyFileVerbatim(file, destinationPath);
                }

                clean++;
                if (options.Verbose)
                {
                    await standardOutput.WriteLineAsync($"clean (copy): {relativePath}");
                }
            }
        }

        if (!inPlace)
        {
            foreach (var file in EnumerateFilesSkippingRoot(sourceRoot, destinationRoot)
                         .Where(path => !Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, file);
                CopyFileVerbatim(file, Path.Combine(destinationRoot, relativePath));
            }
        }

        await standardOutput.WriteLineAsync(
            $"strip-conditional: {scanned} scanned, {transformed} transformed, {clean} clean, {failed} failed, " +
            $"{directivesRemoved} directives removed, {disabledBlocksRemoved} disabled blocks removed");
        return failed > 0 || (options.Strict && hadWarnings) ? 1 : 0;
    }

    private static async Task WriteDiagnosticsAsync(
        IEnumerable<ConditionalDirectiveDiagnostic> diagnostics,
        string filePath,
        TextWriter standardError)
    {
        foreach (var diagnostic in diagnostics)
        {
            await standardError.WriteLineAsync(
                $"[{diagnostic.Severity}] {filePath}:{diagnostic.Line ?? 0}: {diagnostic.Message}");
        }
    }

    private static void CopyFileVerbatim(string sourcePath, string destinationPath)
    {
        if (PathsEqual(sourcePath, destinationPath))
        {
            return;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(parent);
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static string? FindNearestProjectPath(string startDirectory)
    {
        var directory = Path.GetFullPath(startDirectory);
        while (true)
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (project != null)
            {
                return Path.GetFullPath(project);
            }

            var parent = Directory.GetParent(directory)?.FullName;
            if (parent == null)
            {
                return null;
            }

            directory = parent;
        }
    }

    private static IEnumerable<string> EnumerateFilesSkippingRoot(string sourceRoot, string? skipRoot)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(sourceRoot));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var directory in Directory.EnumerateDirectories(current)
                         .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(directory);
                if (skipRoot != null && IsSameOrBelow(fullPath, skipRoot))
                {
                    continue;
                }

                pending.Push(fullPath);
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrBelow(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
