using CSharpToJava.Core.Workspace;

namespace CSharpToJava.CLI;

internal sealed record ProjectPreprocessSourceLayout(
    string SourceRoot,
    string EntryPath,
    IReadOnlyList<string> ProjectFiles);

/// <summary>
/// Shared source-tree preparation used by the unsigned-type, readonly-struct and
/// goto preprocessors. Keeping one implementation guarantees every intermediate
/// directory (".cs2j-unsigned-src", ".cs2j-readonly-src", ".cs2j-no-goto-src")
/// selects and copies exactly the same supported projects.
/// </summary>
internal static class ProjectPreprocessSource
{
    internal static readonly string[] MsBuildDirectoryFileNames =
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "nuget.config",
    };

    internal static ProjectPreprocessSourceLayout ResolveLayout(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);

        if (File.Exists(fullSourcePath)
            && (Path.GetExtension(fullSourcePath).Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(fullSourcePath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(
                graph.ProjectsInTopologicalOrder
                    .Select(p => p.ProjectDirectory)
                    .Append(Path.GetDirectoryName(fullSourcePath)!));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .Append(fullSourcePath)
                .ToList();
            return new ProjectPreprocessSourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(
                graph.ProjectsInTopologicalOrder
                    .Select(p => p.ProjectDirectory)
                    .Append(Path.GetDirectoryName(fullSourcePath)!));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .ToList();
            return new ProjectPreprocessSourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        if (Directory.Exists(fullSourcePath))
        {
            if (ProjectDiscovery.TryResolveProjectEntry(fullSourcePath, out var entryProjectPath))
            {
                var graph = ProjectDiscovery.LoadProjectGraph(entryProjectPath);
                var sourceRoot = GetCommonRoot(
                    graph.ProjectsInTopologicalOrder
                        .Select(p => p.ProjectDirectory)
                        .Append(fullSourcePath));
                var projectFiles = graph.ProjectsInTopologicalOrder
                    .Select(p => p.ProjectFilePath)
                    .ToList();

                var entryExtension = Path.GetExtension(entryProjectPath);
                if (entryExtension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    || entryExtension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    projectFiles.Add(entryProjectPath);
                }
                else
                {
                    var slnDirs = new[] { sourceRoot, fullSourcePath };
                    foreach (var searchDir in slnDirs)
                    {
                        if (!Directory.Exists(searchDir)) continue;
                        var slnInDir = Directory.GetFiles(searchDir, "*.sln", SearchOption.TopDirectoryOnly)
                            .Concat(Directory.GetFiles(searchDir, "*.slnx", SearchOption.TopDirectoryOnly))
                            .FirstOrDefault();
                        if (slnInDir != null)
                        {
                            projectFiles.Add(slnInDir);
                            break;
                        }
                    }
                }

                return new ProjectPreprocessSourceLayout(sourceRoot, fullSourcePath, projectFiles);
            }

            return new ProjectPreprocessSourceLayout(fullSourcePath, fullSourcePath, Array.Empty<string>());
        }

        throw new FileNotFoundException("Source path not found.", fullSourcePath);
    }

    /// <summary>
    /// Scopes an enumerated whole-tree file set down to the files that belong
    /// to the loaded solution/project graph:
    /// <list type="bullet">
    /// <item>files inside a non-excluded project directory,</item>
    /// <item>the layout entry files themselves (the .sln / .csproj), and</item>
    /// <item>files pulled into a graph project by explicit
    /// <c>&lt;Compile Include="…"&gt;</c> items even when they live OUTSIDE the
    /// project directory (shared source linked via <c>..\Common\</c> whose
    /// directory contains no project of its own). Such files must be copied to
    /// the intermediate tree, otherwise the verification build fails with
    /// CS2001 (source file not found).</item>
    /// </list>
    /// Unrelated trees that merely sit below the common source root
    /// (foreign repositories, sibling solutions not referenced by the entry)
    /// are dropped, so they never reach the per-file compilers — compiling
    /// such files with the pass' own reference set fails type resolution and
    /// spurious residual errors abort the run. A file explicitly compiled by a
    /// non-excluded graph project is kept even if it physically resides under
    /// an excluded project directory.
    /// With no graph (plain directory conversion) the whole tree stays in
    /// scope apart from excluded-project directories.
    /// </summary>
    internal static List<string> FilterToGraphMembers(
        IEnumerable<string> files,
        ProjectGraph? projectGraph,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        var fullFiles = files.Select(Path.GetFullPath).ToList();

        if (projectGraph == null)
        {
            return fullFiles
                .Where(full => !SupportedProjectFiltering.IsUnderExcludedProject(full, exclusionInfo))
                .ToList();
        }

        var includedProjects = projectGraph.ProjectsInTopologicalOrder
            .Where(p => !SupportedProjectFiltering.IsUnderExcludedProject(p.ProjectFilePath, exclusionInfo))
            .ToList();
        var includedDirectories = includedProjects
            .Select(p => p.ProjectDirectory)
            .ToList();
        var layoutFiles = layout.ProjectFiles
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Explicit <Compile Include> items of every retained project, resolved
        // against the source tree. Only explicit items are requested: SDK
        // default globs are inside the project directory and are already
        // covered by includedDirectories.
        var explicitlyCompiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Full per-project compile sets (SDK default glob MINUS <Compile Remove>
        // globs), used to exclude files that physically sit under a project
        // directory but are not part of ANY real compilation — e.g. platform
        // split files (*.Unix.cs) dropped by <Compile Remove="**\*.Unix.cs" />.
        // Merging/rewriting such files corrupts the real build: a partial-merge
        // target inside an excluded file disappears from the actual compile.
        var compiledSets = new List<(string ProjectDirectory, ProjectCompileSet.ProjectCompiledSet? Set)>();
        var compiledByAny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in includedProjects)
        {
            var explicitOnly = ProjectCompileSet.ComputeCompiledFiles(
                project.ProjectFilePath, layout.SourceRoot, includeDefaultItems: false);
            if (explicitOnly != null)
            {
                foreach (var compiledFile in explicitOnly)
                {
                    explicitlyCompiled.Add(Path.GetFullPath(compiledFile));
                }
            }

            var fullSet = ProjectCompileSet.ComputeCompiledSet(
                project.ProjectFilePath, layout.SourceRoot,
                includeDefaultItems: true, allowMsBuildEvaluation: true);
            compiledSets.Add((project.ProjectDirectory, fullSet));
            if (fullSet != null)
            {
                foreach (var compiledFile in fullSet.Files)
                {
                    compiledByAny.Add(Path.GetFullPath(compiledFile));
                }
            }
        }

        return fullFiles
            .Where(full =>
            {
                if (explicitlyCompiled.Contains(full) || compiledByAny.Contains(full))
                {
                    return true;
                }

                if (SupportedProjectFiltering.IsUnderExcludedProject(full, exclusionInfo))
                {
                    return false;
                }

                if (layoutFiles.Contains(full))
                {
                    return true;
                }

                // Non-source assets keep the historical directory-containment
                // rule; only .cs files can be excluded by <Compile Remove>.
                if (!full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    return SupportedProjectFiltering.IsUnderAnyDirectory(full, includedDirectories);
                }

                var responsible = compiledSets
                    .Where(entry => SupportedProjectFiltering.IsUnderAnyDirectory(
                        full, new[] { entry.ProjectDirectory }))
                    .ToList();
                if (responsible.Count == 0)
                {
                    return false;
                }

                // Reliable exclusion only when every project covering the file
                // directory was analyzed AND relies on the SDK default glob:
                // then "in none of the sets" means the file is removed from the
                // real build. An unparseable project or EnableDefaultCompileItems
                // =false keeps the conservative keep-it behavior.
                return responsible.Any(entry =>
                    entry.Set is null || !entry.Set.DefaultItemsInEffect);
            })
            .ToList();
    }

    internal static IEnumerable<string> EnumerateSourceFiles(
        string sourceRoot,
        string destinationRoot,
        string? outputRoot = null)
    {
        var fullOutputRoot = outputRoot == null ? null : Path.GetFullPath(outputRoot);
        var fullSourceRoot = Path.GetFullPath(sourceRoot);
        var fullDestinationRoot = Path.GetFullPath(destinationRoot);
        var skipOutputRoot = fullOutputRoot != null
                             && IsSameOrDescendantPath(fullOutputRoot, fullSourceRoot)
                             && !string.Equals(fullOutputRoot, fullSourceRoot, StringComparison.OrdinalIgnoreCase);
        // The intermediate tree is excluded from enumeration only when it lives
        // strictly INSIDE the source tree (first pass copying into a destination
        // below the source). In unified-directory chains later passes enumerate
        // the intermediate tree itself in place (source root == destination root),
        // so it must not be skipped.
        var skipDestinationRoot = IsSameOrDescendantPath(fullDestinationRoot, fullSourceRoot)
                                  && !string.Equals(fullDestinationRoot, fullSourceRoot, StringComparison.OrdinalIgnoreCase);

        var pending = new Stack<string>();
        pending.Push(fullSourceRoot);

        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            if ((skipDestinationRoot && IsSameOrDescendantPath(currentDirectory, fullDestinationRoot))
                || (skipOutputRoot && fullOutputRoot != null && IsSameOrDescendantPath(currentDirectory, fullOutputRoot)))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (SupportedProjectFiltering.IgnoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                {
                    continue;
                }

                if ((skipDestinationRoot && IsSameOrDescendantPath(subDirectory, fullDestinationRoot))
                    || (skipOutputRoot && fullOutputRoot != null && IsSameOrDescendantPath(subDirectory, fullOutputRoot)))
                {
                    continue;
                }

                pending.Push(Path.GetFullPath(subDirectory));
            }
        }
    }

    internal static async Task CopyProjectFilesAsync(
        ProjectPreprocessSourceLayout layout,
        string sourceRoot,
        string intermediateRoot,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        foreach (var projectFile in layout.ProjectFiles)
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, projectFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (exclusionInfo.HasExclusions &&
                projectFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var solutionContent = await File.ReadAllTextAsync(projectFile);
                var rewrittenSolution = SupportedProjectFiltering.RewriteSolutionContent(
                    solutionContent,
                    Path.GetDirectoryName(projectFile) ?? sourceRoot,
                    exclusionInfo);
                await File.WriteAllTextAsync(destinationFile, rewrittenSolution, new System.Text.UTF8Encoding(false));
            }
            else
            {
                try
                {
                    if (projectFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        // Fix NU1605 package-downgrade errors in intermediate copies by
                        // aligning older Newtonsoft.Json references to 13.0.1 (the
                        // version Microsoft.NET.Test.Sdk requires). Without this,
                        // `dotnet restore` on the intermediate solution fails and the
                        // semantic model cannot resolve those external types.
                        var csprojContent = await File.ReadAllTextAsync(projectFile);
                        var fixedContent = FixPackageDowngrades(csprojContent);
                        // The intermediate tree flattens the repository's MSBuild
                        // directory files and has no eng/ directory, so repo version
                        // properties ($(SystemBuffersVersion) …) evaluate to empty
                        // and restore fails with NU1015. Pin every property-based
                        // PackageReference/PackageVersion version to the literal
                        // resolved by MSBuild on the ORIGINAL project.
                        var versions = ResolveProjectPackageVersions(projectFile, csprojContent);
                        fixedContent = PinPackageReferenceVersionProperties(
                            fixedContent, versions, Path.GetDirectoryName(projectFile)!);
                        fixedContent = PinPackageReferenceVersions(fixedContent, versions);
                        await File.WriteAllTextAsync(destinationFile, fixedContent, new System.Text.UTF8Encoding(false));
                    }
                    else
                    {
                        File.Copy(projectFile, destinationFile, overwrite: true);
                    }
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"CopyProjectFilesAsync failed. EntryPath={layout.EntryPath}, SourceRoot={sourceRoot}, IntermediateRoot={intermediateRoot}, " +
                        $"Source={projectFile}, Destination={destinationFile}. {ex.Message}",
                        ex);
                }
            }
        }
    }

    /// <summary>
    /// Aligns known package-downgrade references (e.g. Newtonsoft.Json 12.0.3 →
    /// 13.0.1) so that intermediate-project dotnet restore does not fail with
    /// NU1605. Only touches references with versions strictly below the minimum
    /// required by the rest of the solution.
    /// </summary>
    internal static string FixPackageDowngrades(string csprojContent)
    {
        var needsSave = false;
        var doc = System.Xml.Linq.XDocument.Load(new System.IO.StringReader(csprojContent));
        if (doc.Root == null) return csprojContent;

        foreach (var pkgRef in doc.Root.Descendants("PackageReference"))
        {
            var include = pkgRef.Attribute("Version")?.Value;
            if (string.IsNullOrEmpty(include))
                continue;

            // Newtonsoft.Json < 13.0.1 triggers NU1605 when Microsoft.NET.Test.Sdk
            // is also present. Pin to 13.0.1 to match the transitive requirement.
            var includeLower = include.Trim();
            if (includeLower.StartsWith("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase)
                && IsOlderThan(include, "13.0.1"))
            {
                pkgRef.SetAttributeValue("Version", "13.0.1");
                needsSave = true;
            }
        }

        if (!needsSave) return csprojContent;
        using var sw = new Utf8StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }

    /// <summary>
    /// A <see cref="System.IO.StringWriter"/> that reports UTF-8 so XDocument
    /// emits <c>encoding="utf-8"</c> in the XML declaration. A plain StringWriter
    /// reports UTF-16, and the resulting string written back as UTF-8 (no BOM)
    /// makes later XML loads fail with "There is no Unicode byte order mark."
    /// </summary>
    private sealed class Utf8StringWriter : System.IO.StringWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }

    private static bool IsOlderThan(string versionAttr, string targetVersion)
    {
        try
        {
            if (!System.Version.TryParse(System.Text.RegularExpressions.Regex.Match(versionAttr, @"\d+(\.\d+)*").Value, out var v1))
                return false;
            if (!System.Version.TryParse(targetVersion, out var v2))
                return false;
            return v1 < v2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the MSBuild-evaluated PackageReference versions for an original
    /// project only when the project (or a sibling props/targets file that
    /// versions packages) actually needs them. Evaluation failure/null leaves
    /// the original markup untouched.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? ResolveProjectPackageVersions(
        string projectFile,
        string csprojContent)
    {
        if (!NeedsPackageVersionResolution(csprojContent))
        {
            var projectDirectory = Path.GetDirectoryName(projectFile);
            if (projectDirectory == null
                || !DirectoryMsBuildFilesNeedPackageVersionResolution(projectDirectory))
            {
                return null;
            }
        }

        return MsBuildCompileEvaluator.TryEvaluatePackageReferences(projectFile);
    }

    /// <summary>True when a file contains a property-based package version.</summary>
    internal static bool NeedsPackageVersionResolution(string content)
        => content.Contains("PackageReference", StringComparison.OrdinalIgnoreCase)
           && content.Contains("Version=\"$(", StringComparison.Ordinal);

    private static bool DirectoryMsBuildFilesNeedPackageVersionResolution(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.props")
                         .Concat(Directory.EnumerateFiles(directory, "*.targets")))
            {
                if (FileContentNeedsPackageVersionResolution(file))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory we cannot enumerate simply needs no pinning.
        }

        return false;
    }

    private static bool FileContentNeedsPackageVersionResolution(string file)
    {
        try
        {
            return NeedsPackageVersionResolution(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces property definitions used as PackageReference versions
    /// (e.g. <c>&lt;RoslynApiVersion&gt;$(MicrosoftCodeAnalysisVersion_4_4)&lt;/RoslynApiVersion&gt;</c>)
    /// with the evaluated literal for the owning project. Shared targets files
    /// such as System.Text.Json.SourceGeneration.targets set their version from a
    /// per-project property, so pinning the property (not the shared file) keeps
    /// each generator on its own Roslyn version.
    /// </summary>
    internal static string PinPackageReferenceVersionProperties(
        string projectContent,
        IReadOnlyDictionary<string, string>? versions,
        string projectDirectory)
    {
        if (versions == null || versions.Count == 0)
        {
            return projectContent;
        }

        var propertyPackages = CollectVersionPropertyPackageMap(projectDirectory);
        if (propertyPackages.Count == 0)
        {
            return projectContent;
        }

        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Parse(
                projectContent, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return projectContent;
        }

        var changed = false;
        foreach (var propertyGroup in doc.Descendants("PropertyGroup"))
        {
            foreach (var property in propertyGroup.Elements())
            {
                if (!propertyPackages.TryGetValue(property.Name.LocalName, out var packageIdentity)
                    || !property.Value.Contains("$(", StringComparison.Ordinal)
                    || !versions.TryGetValue(packageIdentity, out var version)
                    || string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                property.Value = version;
                changed = true;
            }
        }

        if (!changed)
        {
            return projectContent;
        }

        using var writer = new Utf8StringWriter();
        doc.Save(writer, System.Xml.Linq.SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    /// <summary>
    /// Maps <c>$(PropertyName)</c> → package Identity for every PackageReference
    /// in the props/targets files of one project directory.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> CollectVersionPropertyPackageMap(string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory)
                .Where(file => file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                               || file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return map;
        }

        foreach (var file in files)
        {
            System.Xml.Linq.XDocument doc;
            try
            {
                doc = System.Xml.Linq.XDocument.Load(file);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var packageReference in doc.Descendants("PackageReference"))
            {
                var identity = packageReference.Attribute("Include")?.Value?.Trim();
                var version = packageReference.Attribute("Version")?.Value?.Trim();
                if (string.IsNullOrEmpty(identity)
                    || string.IsNullOrEmpty(version)
                    || !version.StartsWith("$(", StringComparison.Ordinal)
                    || !version.EndsWith(')'))
                {
                    continue;
                }

                var propertyName = version[2..^1].Trim();
                if (propertyName.Length == 0
                    || propertyName.Contains('$')
                    || propertyName.Contains('('))
                {
                    continue;
                }

                map.TryAdd(propertyName, identity);
            }
        }

        return map;
    }

    /// <summary>
    /// Pins every <c>PackageReference</c>/<c>PackageVersion</c> whose Version is a
    /// property expression to the evaluated literal. Literal versions and
    /// identities without a resolved version are left untouched.
    /// </summary>
    internal static string PinPackageReferenceVersions(
        string content,
        IReadOnlyDictionary<string, string>? versions)
    {
        if (versions == null
            || versions.Count == 0
            || !content.Contains("$(", StringComparison.Ordinal))
        {
            return content;
        }

        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Parse(content, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return content;
        }

        var changed = false;
        foreach (var element in doc.Descendants())
        {
            if (element.Name.LocalName is not ("PackageReference" or "PackageVersion"))
            {
                continue;
            }

            var identity = element.Attribute("Include")?.Value?.Trim();
            var versionAttribute = element.Attribute("Version");
            if (string.IsNullOrEmpty(identity)
                || versionAttribute == null
                || !versionAttribute.Value.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            if (!versions.TryGetValue(identity, out var literal)
                || string.IsNullOrWhiteSpace(literal))
            {
                continue;
            }

            versionAttribute.Value = literal;
            changed = true;
        }

        if (!changed)
        {
            return content;
        }

        using var writer = new Utf8StringWriter();
        doc.Save(writer, System.Xml.Linq.SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    /// <summary>
    /// Drops <c>&lt;Import&gt;</c> elements whose target does not exist relative to
    /// the intermediate directory (the flattened copy has no eng/ tree and no
    /// repository-root Directory.Build.props, so those imports would fail with
    /// MSB4019). SDK-style imports and imports that resolve are kept.
    /// When imports were removed, implicitly-defined PackageReferences in the
    /// same file are dropped as well: they exist only to feed build tasks from
    /// those eng imports, so keeping them makes restore demand uncached Arcade
    /// task packages for tasks that no longer run.
    /// </summary>
    internal static string RemoveMissingImports(string content, string baseDirectory)
    {
        if (content.IndexOf("<Import", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return content;
        }

        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Parse(content, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return content;
        }

        var properties = CollectLiteralProperties(doc, baseDirectory);
        var removed = false;
        foreach (var import in doc.Descendants("Import").ToList())
        {
            if (import.Attribute("Sdk") != null)
            {
                continue;
            }

            var projectAttribute = import.Attribute("Project");
            if (projectAttribute == null)
            {
                continue;
            }

            if (ImportTargetExists(projectAttribute.Value, baseDirectory, properties))
            {
                continue;
            }

            import.Remove();
            removed = true;
        }

        if (!removed)
        {
            return content;
        }

        foreach (var packageReference in doc.Descendants("PackageReference").ToList())
        {
            if (string.Equals(
                    packageReference.Attribute("IsImplicitlyDefined")?.Value?.Trim(),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                packageReference.Remove();
            }
        }

        using var writer = new Utf8StringWriter();
        doc.Save(writer, System.Xml.Linq.SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    private static Dictionary<string, string> CollectLiteralProperties(
        System.Xml.Linq.XDocument doc,
        string baseDirectory)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyGroup in doc.Descendants("PropertyGroup"))
        {
            foreach (var property in propertyGroup.Elements())
            {
                var value = property.Value.Trim();
                if (value.Length > 0)
                {
                    properties[property.Name.LocalName] = value;
                }
            }
        }

        // Two expansion passes resolve sibling property references and
        // $(MSBuildThisFileDirectory) (which points at the flattened copy).
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var name in properties.Keys.ToList())
            {
                var value = properties[name].Replace(
                    "$(MSBuildThisFileDirectory)",
                    EnsureTrailingSeparator(baseDirectory),
                    StringComparison.OrdinalIgnoreCase);
                foreach (var pair in properties)
                {
                    value = value.Replace(
                        "$(" + pair.Key + ")", pair.Value, StringComparison.OrdinalIgnoreCase);
                }

                properties[name] = value;
            }
        }

        return properties;
    }

    private static bool ImportTargetExists(
        string rawPath,
        string baseDirectory,
        IReadOnlyDictionary<string, string> properties)
    {
        var expanded = rawPath.Replace(
            "$(MSBuildThisFileDirectory)",
            EnsureTrailingSeparator(baseDirectory),
            StringComparison.OrdinalIgnoreCase);
        foreach (var pair in properties)
        {
            expanded = expanded.Replace(
                "$(" + pair.Key + ")", pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        // An unresolved property means the target cannot be located in the
        // intermediate tree — treat it as missing rather than leaving a latent
        // MSB4019 behind.
        if (expanded.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(
                baseDirectory, expanded.Replace('\\', Path.DirectorySeparatorChar)));
            return File.Exists(fullPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string EnsureTrailingSeparator(string directory)
        => directory.EndsWith(Path.DirectorySeparatorChar)
           || directory.EndsWith(Path.AltDirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

    /// <summary>
    /// True for files tree preparation writes itself: project files and the
    /// MSBuild directory files living directly in the source root (flattened to
    /// the intermediate root). The per-pass asset copy must skip these so it
    /// does not overwrite the package-version pinning with pristine bytes.
    /// </summary>
    internal static bool IsPreparedProjectOrMsBuildFile(string file, string sourceRoot)
    {
        if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!MsBuildDirectoryFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(file));
        return directory != null
               && string.Equals(
                   directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   Path.GetFullPath(sourceRoot).TrimEnd(
                       Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static void CopyAncestorMsBuildDirectoryFiles(
        string sourceRoot,
        string intermediateRoot,
        string logPrefix,
        bool verbose,
        string? evaluationProjectPath = null)
    {
        var copies = new List<(string Source, string Destination)>();
        foreach (var fileName in MsBuildDirectoryFileNames)
        {
            var current = Path.GetFullPath(sourceRoot);
            string? found = null;
            while (!string.IsNullOrEmpty(current))
            {
                var candidate = Path.Combine(current, fileName);
                if (File.Exists(candidate))
                {
                    found = candidate;
                    break;
                }

                var parent = Path.GetDirectoryName(current);
                if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            if (found == null)
            {
                continue;
            }

            copies.Add((found, Path.Combine(intermediateRoot, fileName)));
        }

        // Only pay for the MSBuild evaluation when one of the flattened files
        // actually versions a package through a repository property.
        IReadOnlyDictionary<string, string>? packageVersions = null;
        if (evaluationProjectPath != null
            && copies.Any(copy => FileContentNeedsPackageVersionResolution(copy.Source)))
        {
            packageVersions = MsBuildCompileEvaluator.TryEvaluatePackageReferences(evaluationProjectPath);
        }

        foreach (var (source, destination) in copies)
        {
            CopyMsBuildDirectoryFile(
                source, destination, packageVersions, intermediateRoot, logPrefix, verbose);
            CopyMsBuildThisFileDirectoryReferences(source, intermediateRoot, logPrefix, verbose);
        }

        EnsurePreserializedResourceSupport(intermediateRoot, logPrefix, verbose);
    }

    private static void CopyMsBuildDirectoryFile(
        string sourceFile,
        string destinationFile,
        IReadOnlyDictionary<string, string>? packageVersions,
        string intermediateRoot,
        string logPrefix,
        bool verbose)
    {
        string content;
        try
        {
            content = File.ReadAllText(sourceFile);
        }
        catch (IOException)
        {
            File.Copy(sourceFile, destinationFile, overwrite: true);
            return;
        }

        var rewritten = PinPackageReferenceVersions(content, packageVersions);
        rewritten = RemoveMissingImports(rewritten, intermediateRoot);

        if (string.Equals(rewritten, content, StringComparison.Ordinal))
        {
            // Byte-exact copy keeps nuget.config and untouched props/targets intact.
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
        else
        {
            File.WriteAllText(destinationFile, rewritten, new System.Text.UTF8Encoding(false));
        }

        if (verbose)
        {
            Console.WriteLine($"{logPrefix} Copied MSBuild directory file: {sourceFile} -> {destinationFile}");
        }
    }

    internal static string MapToIntermediate(ProjectPreprocessSourceLayout layout, string intermediateRoot)
    {
        if (layout.ProjectFiles.Count > 0 && Directory.Exists(layout.EntryPath))
        {
            return intermediateRoot;
        }

        if (File.Exists(layout.EntryPath))
        {
            // Project files are copied with their path relative to the source root
            // (see CopyProjectFilesAsync), so an entry solution that lives below the
            // root — e.g. <root>\json\System.Text.Json.sln — lands at the same
            // relative location inside the intermediate tree.
            var relativePath = Path.GetRelativePath(layout.SourceRoot, layout.EntryPath);
            if (!relativePath.StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relativePath))
            {
                return Path.Combine(intermediateRoot, relativePath);
            }

            return Path.Combine(intermediateRoot, Path.GetFileName(layout.EntryPath));
        }

        if (string.Equals(layout.SourceRoot, layout.EntryPath, StringComparison.OrdinalIgnoreCase))
        {
            return intermediateRoot;
        }

        return Path.Combine(intermediateRoot, Path.GetRelativePath(layout.SourceRoot, layout.EntryPath));
    }

    internal static string? ResolveRestoreTarget(
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
    {
        var preprocessedPath = MapToIntermediate(layout, intermediateRoot);
        if (File.Exists(preprocessedPath) &&
            (preprocessedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
             preprocessedPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
             preprocessedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return preprocessedPath;
        }

        if (Directory.Exists(preprocessedPath))
        {
            var sln = Directory.GetFiles(preprocessedPath, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(preprocessedPath, "*.slnx", SearchOption.TopDirectoryOnly))
                .FirstOrDefault();
            if (sln != null) return sln;

            var csproj = Directory.GetFiles(preprocessedPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj != null) return csproj;

            // Preprocessed trees keep the entry solution at its original
            // repository-relative location instead of the stage root; restoring it is
            // what gives MSBuildWorkspace a usable metadata reference set.
            if (SolutionLoader.TryResolveNestedSolutionEntry(preprocessedPath, out var nestedSolution))
            {
                return nestedSolution;
            }
        }

        return null;
    }

    internal static int RunDotNetRestore(string targetPath, string logPrefix)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{targetPath}\" --ignore-failed-sources -p:NoWarn=NU1605",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine($"{logPrefix} Failed to start dotnet restore process.");
                return -1;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            System.Threading.Tasks.Task.WaitAll(outputTask, errorTask);

            if (process.ExitCode != 0)
            {
                var output = outputTask.Result;
                var error = errorTask.Result;
                Console.Error.WriteLine($"{logPrefix} dotnet restore exited with code {process.ExitCode} for {targetPath}");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Console.Error.WriteLine($"stdout: {output}");
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    Console.Error.WriteLine($"stderr: {error}");
                }
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{logPrefix} dotnet restore failed: {ex.Message}");
            return -1;
        }
    }

    internal static ProjectGraph? TryLoadProjectGraph(ProjectPreprocessSourceLayout layout, bool verbose)
    {
        if (layout.ProjectFiles.Count == 0)
        {
            return null;
        }

        try
        {
            if (ProjectDiscovery.TryResolveProjectEntry(layout.EntryPath, out var entryPath))
            {
                return ProjectDiscovery.LoadProjectGraph(entryPath);
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"Warning: could not load the project graph for support filtering: {ex.Message}");
            }
        }

        return null;
    }

    internal static string GetCommonRoot(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return Path.GetFullPath(".");
        }

        var commonRoot = normalized[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (normalized.Any(path => !IsSameOrDescendantPath(path, commonRoot)))
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

    internal static bool IsSameOrDescendantPath(string path, string candidateAncestor)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidateAncestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.Equals(fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullCandidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullCandidate + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyMsBuildThisFileDirectoryReferences(
        string sourceFile,
        string intermediateRoot,
        string logPrefix,
        bool verbose)
    {
        string content;
        try
        {
            content = File.ReadAllText(sourceFile);
        }
        catch (IOException)
        {
            return;
        }

        var sourceDirectory = Path.GetDirectoryName(sourceFile)!;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     content,
                     @"\$\(MSBuildThisFileDirectory\)\s*\\?([^""<>|?*\r\n]+)"))
        {
            var relativeReference = match.Groups[1].Value.Trim().TrimEnd('\\', '/');
            if (relativeReference.Length == 0) continue;

            var referencedPath = Path.GetFullPath(Path.Combine(sourceDirectory, relativeReference));
            if (!File.Exists(referencedPath)) continue;

            var destinationPath = Path.Combine(intermediateRoot, Path.GetFileName(referencedPath));
            if (File.Exists(destinationPath)) continue;

            File.Copy(referencedPath, destinationPath, overwrite: false);
            if (verbose)
            {
                Console.WriteLine($"{logPrefix} Copied MSBuild sibling reference: {referencedPath} -> {destinationPath}");
            }
        }
    }

    /// <summary>
    /// .NET SDK 10 fails net4x builds that contain non-string resources with
    /// MSB3823/MSB3822 unless <c>GenerateResourceUsePreserializedResources</c> is
    /// enabled and the <c>System.Resources.Extensions</c> assembly is available to
    /// the GenerateResource task. Every preprocessor applies the same targets file
    /// so the intermediate trees stay structurally identical.
    /// </summary>
    private static void EnsurePreserializedResourceSupport(
        string intermediateRoot,
        string logPrefix,
        bool verbose)
    {
        var assemblyPath = ResolveSystemResourcesExtensionsAssembly();
        if (assemblyPath == null)
        {
            if (verbose)
            {
                Console.WriteLine(
                    $"{logPrefix} System.Resources.Extensions not found in NuGet cache; " +
                    "preserialized-resource fallback will not be injected.");
            }

            return;
        }

        var targetsPath = Path.Combine(intermediateRoot, "Directory.Build.targets");
        var escapedPath = System.Security.SecurityElement.Escape(assemblyPath);
        var snippet =
            "  <ItemGroup Condition=\"'$(GenerateResourceUsePreserializedResources)' == 'true'" +
            " and '$(TargetFrameworkIdentifier)' == '.NETFramework'\">\n" +
            $"    <ReferencePathWithRefAssemblies Include=\"{escapedPath}\" />\n" +
            "  </ItemGroup>\n";

        if (File.Exists(targetsPath))
        {
            var content = File.ReadAllText(targetsPath);
            var closeTag = "</Project>";
            var closeIndex = content.LastIndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
            if (closeIndex >= 0)
            {
                content = content.Insert(closeIndex, "\n" + snippet);
            }
            else
            {
                content += "\n" + snippet;
            }

            File.WriteAllText(targetsPath, content);
        }
        else
        {
            var fullProject =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Project>\n" + snippet + "</Project>\n";
            File.WriteAllText(targetsPath, fullProject);
        }

        if (verbose)
        {
            Console.WriteLine(
                $"{logPrefix} Injected System.Resources.Extensions reference ({assemblyPath}) " +
                "for preserialized resource verification builds.");
        }
    }

    private static string? ResolveSystemResourcesExtensionsAssembly()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile))
        {
            return null;
        }

        var packageRoot = Path.Combine(userProfile, ".nuget", "packages", "system.resources.extensions");
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        var bestVersion = Directory.EnumerateDirectories(packageRoot)
            .Select(Path.GetFileName)
            .Where(v => !string.IsNullOrEmpty(v) && Version.TryParse(v, out _))
            .OrderByDescending(v => Version.Parse(v!))
            .FirstOrDefault();
        if (bestVersion == null)
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(packageRoot, bestVersion, "lib", "netstandard2.0", "System.Resources.Extensions.dll"),
            Path.Combine(packageRoot, bestVersion, "lib", "net462", "System.Resources.Extensions.dll"),
            Path.Combine(packageRoot, bestVersion, "lib", "net461", "System.Resources.Extensions.dll"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
