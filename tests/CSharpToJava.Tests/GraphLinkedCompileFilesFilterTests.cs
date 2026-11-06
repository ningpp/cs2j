using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS2001 in the convert-project verification build: shared
/// source pulled into a test project via
/// <c>&lt;Compile Include="..\Common\…" Link="…" /&gt;</c> must be copied to the
/// intermediate tree even when the shared directory (e.g. uri\tests\Common)
/// contains no project of its own and therefore is not "under a project
/// directory" of the entry graph.
/// </summary>
public sealed class GraphLinkedCompileFilesFilterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _sourceRoot;

    public GraphLinkedCompileFilesFilterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-linked-compile-" + Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(_sourceRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private string WriteFile(params string[] segments)
    {
        var path = Path.Combine(_sourceRoot, Path.Combine(segments));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// stub");
        return Path.GetFullPath(path);
    }

    private string WriteProject(string relativeDirectory, string projectName, string csprojBody)
    {
        var directory = Path.Combine(_sourceRoot, relativeDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, projectName + ".csproj");
        File.WriteAllText(path, csprojBody);
        return Path.GetFullPath(path);
    }

    private static string SimpleProject(params string[] itemElements)
        => """
           <Project>
             <ItemGroup>
           """ + string.Join(Environment.NewLine, itemElements) + """

             </ItemGroup>
           </Project>
           """;

    private static string SdkProject(params string[] itemElements)
        => """
           <Project Sdk="Microsoft.NET.Sdk">
             <ItemGroup>
           """ + string.Join(Environment.NewLine, itemElements) + """

             </ItemGroup>
           </Project>
           """;

    private static DiscoveredProject Project(string projectFilePath)
    {
        var directory = Path.GetDirectoryName(projectFilePath)!;
        return new DiscoveredProject
        {
            Name = Path.GetFileNameWithoutExtension(projectFilePath),
            ProjectFilePath = projectFilePath,
            ProjectDirectory = directory,
            Kind = ProjectKind.Production,
            ProjectReferences = Array.Empty<string>(),
            ResourceItems = Array.Empty<ResourceItem>(),
        };
    }

    private static ProjectGraph SingleGraph(DiscoveredProject project)
        => new()
        {
            RootProjectPath = project.ProjectFilePath,
            ProjectsInTopologicalOrder = new[] { project },
        };

    [Fact]
    public void LinkedSharedSource_SiblingDirectoryWithNoProject_IsKept()
    {
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "FunctionalTests"),
            "dotnet-uri-functional-tests",
            SimpleProject(
                "<Compile Include=\"LocalTest.cs\" />",
                "<Compile Include=\"..\\Common\\System\\ThreadCultureChange.cs\" Link=\"Common\\ThreadCultureChange.cs\" />",
                "<Compile Include=\"..\\Common\\Xunit\\AssertExtensions.cs\" Link=\"Common\\AssertExtensions.cs\" />"));

        var localFile = WriteFile("uri", "tests", "FunctionalTests", "LocalTest.cs");
        var shared1 = WriteFile("uri", "tests", "Common", "System", "ThreadCultureChange.cs");
        var shared2 = WriteFile("uri", "tests", "Common", "Xunit", "AssertExtensions.cs");
        // A foreign tree under the common root that the entry graph does not
        // reference must still be dropped.
        var foreign = WriteFile("json", "src", "Utf8JsonWriter.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });
        var files = new[] { csproj, localFile, shared1, shared2, foreign };

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            files, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(shared1, result);
        Assert.Contains(shared2, result);
        Assert.Contains(localFile, result);
        Assert.DoesNotContain(foreign, result);
    }

    [Fact]
    public void LinkedSharedSource_RecursiveGlob_IsKept()
    {
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "UnitTests"),
            "dotnet-uri-unit-tests",
            SimpleProject("<Compile Include=\"..\\Common\\**\\*.cs\" />"));

        var nested = WriteFile("uri", "tests", "Common", "System", "PlatformDetection.cs");
        var foreign = WriteFile("other", "src", "NotLinked.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, nested, foreign }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(nested, result);
        Assert.DoesNotContain(foreign, result);
    }

    [Fact]
    public void LinkedSharedSource_RemovedByCompileRemove_IsDropped()
    {
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "UnitTests"),
            "dotnet-uri-unit-tests",
            SimpleProject(
                "<Compile Include=\"..\\Common\\**\\*.cs\" />",
                "<Compile Remove=\"..\\Common\\System\\Skipped.cs\" />"));

        var kept = WriteFile("uri", "tests", "Common", "System", "PlatformDetection.cs");
        var removed = WriteFile("uri", "tests", "Common", "System", "Skipped.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, kept, removed }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(kept, result);
        Assert.DoesNotContain(removed, result);
    }

    [Fact]
    public void FileCompiledByKeptProject_ButPhysicallyUnderExcludedDirectory_IsKept()
    {
        var keptProjectFile = WriteProject(
            Path.Combine("product", "src"),
            "product",
            SimpleProject("<Compile Include=\"..\\..\\tests\\Legacy\\SharedHarness.cs\" Link=\"SharedHarness.cs\" />"));
        var excludedProjectFile = WriteProject(
            Path.Combine("tests", "Legacy"),
            "legacy-tests",
            SimpleProject("<Compile Include=\"SharedHarness.cs\" />"));
        var linked = WriteFile("tests", "Legacy", "SharedHarness.cs");
        var excludedOnly = WriteFile("tests", "Legacy", "OldTest.cs");

        var graph = new ProjectGraph
        {
            RootProjectPath = keptProjectFile,
            ProjectsInTopologicalOrder = new[]
            {
                Project(keptProjectFile),
                Project(excludedProjectFile),
            },
        };
        var exclusion = new SupportedProjectFiltering.ExclusionInfo();
        exclusion.ExcludedProjectPaths.Add(excludedProjectFile);
        exclusion.ExcludedProjectDirectories.Add(Path.GetDirectoryName(excludedProjectFile)!);

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, keptProjectFile, new[] { keptProjectFile, excludedProjectFile });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { keptProjectFile, excludedProjectFile, linked, excludedOnly },
            graph, layout, exclusion);

        Assert.Contains(linked, result);
        Assert.DoesNotContain(excludedOnly, result);
        Assert.DoesNotContain(excludedProjectFile, result);
    }

    [Fact]
    public void WithoutGraph_ExcludedProjectDirectories_AreStillFiltered()
    {
        var excludedDir = Path.Combine(_sourceRoot, "tests", "Legacy");
        Directory.CreateDirectory(excludedDir);
        var excludedProjectFile = Path.Combine(excludedDir, "legacy-tests.csproj");
        File.WriteAllText(excludedProjectFile, "<Project />");
        var keptFile = WriteFile("product", "src", "Product.cs");
        var excludedFile = Path.Combine(excludedDir, "OldTest.cs");
        File.WriteAllText(excludedFile, "// stub");

        var exclusion = new SupportedProjectFiltering.ExclusionInfo();
        exclusion.ExcludedProjectPaths.Add(excludedProjectFile);
        exclusion.ExcludedProjectDirectories.Add(excludedDir);

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, _sourceRoot, Array.Empty<string>());

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { keptFile, excludedFile }, null, layout, exclusion);

        Assert.Contains(keptFile, result);
        Assert.DoesNotContain(excludedFile, result);
    }

    [Fact]
    public void SdkProject_DefaultGlob_CompileRemoveGlob_DropsExcludedPlatformSplit()
    {
        // Regression for CS0246 after partial-merge: the pass tree must match the
        // real MSBuild compile set. <Compile Remove="**\*.Unix.cs" /> keeps the
        // platform split out of the build, so it must not enter the pass file set
        // (partial-merge otherwise merges the whole partial type into the
        // excluded file and the compiled parts are stubbed away).
        var csproj = WriteProject(
            Path.Combine("xml", "src"),
            "dotnet-xml",
            SdkProject(
                "<Compile Remove=\"**\\*.Unix.cs\" />",
                "<Compile Remove=\"**\\*.OSX.cs\" />"));

        var shared = WriteFile("xml", "src", "System", "Xml", "Reader.cs");
        var unix = WriteFile("xml", "src", "System", "Xml", "Reader.Unix.cs");
        var osx = WriteFile("xml", "src", "System", "Xml", "Reader.OSX.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, shared, unix, osx }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(shared, result);
        Assert.DoesNotContain(unix, result);
        Assert.DoesNotContain(osx, result);
        // The project file itself always stays.
        Assert.Contains(csproj, result);
    }

    [Fact]
    public void FileRemovedByOneSdkProject_ButCompiledByAnotherProject_IsKept()
    {
        // A <Compile Remove> is per-project: another retained project that still
        // compiles the file (explicit link) keeps it in the pass set.
        var remover = WriteProject(
            Path.Combine("product", "src"),
            "product",
            SdkProject("<Compile Remove=\"Platform\\Impl.Unix.cs\" />"));
        var consumer = WriteProject(
            Path.Combine("product", "shim"),
            "shim",
            SdkProject("<Compile Include=\"..\\src\\Platform\\Impl.Unix.cs\" Link=\"Impl.Unix.cs\" />"));

        var ordinary = WriteFile("product", "src", "Platform", "Impl.cs");
        var removedHere = WriteFile("product", "src", "Platform", "Impl.Unix.cs");

        var graph = new ProjectGraph
        {
            RootProjectPath = remover,
            ProjectsInTopologicalOrder = new[] { Project(remover), Project(consumer) },
        };
        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, remover, new[] { remover, consumer });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { remover, consumer, ordinary, removedHere }, graph, layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(ordinary, result);
        Assert.Contains(removedHere, result);
    }

    [Fact]
    public void SdkProject_DefaultItemsDisabled_NonCompiledLooseFile_IsKeptConservatively()
    {
        // EnableDefaultCompileItems=false is non-deterministic for loose files
        // (the SDK default glob is off); the filter must not drop them on the
        // strength of a compile set that never included the directory glob.
        var csproj = WriteProject(
            Path.Combine("lib", "src"),
            "lib",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Listed.cs" />
              </ItemGroup>
            </Project>
            """);

        var listed = WriteFile("lib", "src", "Listed.cs");
        var loose = WriteFile("lib", "src", "Loose.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, listed, loose }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(listed, result);
        Assert.Contains(loose, result);
    }

    [Fact]
    public void SdkProject_RemoveAllThenIncludeOne_ReincludedFile_IsKept()
    {
        // MSBuild evaluates Compile items in document order: <Compile Remove="**\*.cs" />
        // followed by <Compile Include="CorrectnessTest.cs" /> leaves exactly the
        // re-included file in the compile set (dotnet-xml.root-tests.csproj shape).
        var csproj = WriteProject(
            Path.Combine("xml-tiny", "tests"),
            "dotnet-xml.root-tests",
            SdkProject(
                "<Compile Remove=\"**\\*.cs\" />",
                "<Compile Include=\"CorrectnessTest.cs\" />"));

        var included = WriteFile("xml-tiny", "tests", "CorrectnessTest.cs");
        var removed = WriteFile("xml-tiny", "tests", "Common", "OtherTest.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, included, removed }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(included, result);
        Assert.DoesNotContain(removed, result);

        var compiled = ProjectCompileSet.ComputeCompiledFiles(csproj, _sourceRoot);
        Assert.NotNull(compiled);
        Assert.Contains(included, compiled!);
        Assert.DoesNotContain(removed, compiled!);
    }

    [Fact]
    public void TfmSpecificCompileItem_NotInAuthoritativeCompileSet_IsExcluded()
    {
        // Regression for JsonReaderHelper.netstandard.cs leaking into the net10
        // input tree: the syntactic explicit-only set cannot evaluate the TFM
        // Condition when TargetFrameworks depends on an unresolved SDK property,
        // so it picks the raw (netstandard) branch. Once the authoritative
        // MSBuild/TFM evaluation is available, its compile set is the only
        // source of truth and the losing TFM variant must be dropped.
        var csproj = WriteProject(
            Path.Combine("json", "src"),
            "System.Text.Json",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>$(NetCoreAppCurrent);netstandard2.0</TargetFrameworks>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETCoreApp'">
                <Compile Include="V.net8.cs" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFrameworkIdentifier)' != '.NETCoreApp'">
                <Compile Include="V.netstandard.cs" />
              </ItemGroup>
            </Project>
            """);

        var net8Variant = WriteFile("json", "src", "V.net8.cs");
        var netstandardVariant = WriteFile("json", "src", "V.netstandard.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        try
        {
            MsBuildCompileEvaluator.EvaluationOverride = path =>
            {
                Assert.Equal(csproj, Path.GetFullPath(path));
                return new MsBuildCompileEvaluation
                {
                    Files = new[] { net8Variant },
                    TargetFramework = "net10.0",
                };
            };

            var result = ProjectPreprocessSource.FilterToGraphMembers(
                new[] { csproj, net8Variant, netstandardVariant },
                SingleGraph(Project(csproj)), layout,
                new SupportedProjectFiltering.ExclusionInfo());

            Assert.Contains(net8Variant, result);
            Assert.DoesNotContain(netstandardVariant, result);
        }
        finally
        {
            MsBuildCompileEvaluator.EvaluationOverride = null;
        }
    }

    [Fact]
    public void ExplicitSharedSource_OutsideProjectDirectory_IsKeptFromAuthoritativeSet()
    {
        // The authoritative compile set (not the explicit-only syntax set) must
        // still carry explicitly linked shared source outside the project dir.
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "UnitTests"),
            "dotnet-uri-unit-tests",
            SdkProject(
                "<Compile Include=\"..\\Common\\System\\ThreadCultureChange.cs\" Link=\"Common\\ThreadCultureChange.cs\" />"));

        var shared = WriteFile("uri", "tests", "Common", "System", "ThreadCultureChange.cs");
        var foreign = WriteFile("other", "src", "NotLinked.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, shared, foreign }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(shared, result);
        Assert.DoesNotContain(foreign, result);
    }

    [Fact]
    public void ExternalSharedCompileItems_OutsideSourceRoot_AreReportedForAnalysis()
    {
        // Runtime shared sources (Common/src/SourceGenerators/TypeRef.cs,
        // Common/src/System/SR.cs, …) are compiled by graph projects through
        // <Compile Include="…"> but live outside the entry's source root, so the
        // intermediate tree can never mirror them. The filter must still report
        // them so the whole-tree analysis compilation can add their
        // declarations; otherwise types like TypeRef stay unresolved. Items the
        // evaluated compile set excludes (losing TFM variant) stay out.
        var csproj = WriteProject(
            Path.Combine("json", "src"),
            "System.Text.Json",
            SdkProject(
                "<Compile Include=\"Local.cs\" />",
                "<Compile Include=\"..\\..\\shared\\TypeRef.cs\" />",
                "<Compile Include=\"..\\..\\shared\\V.netstandard.cs\" Condition=\"'1' == '2'\" />"));

        var local = WriteFile("json", "src", "Local.cs");
        var shared = WriteFile("shared", "TypeRef.cs");
        var tfmVariant = WriteFile("shared", "V.netstandard.cs");

        var layout = new ProjectPreprocessSourceLayout(
            Path.Combine(_sourceRoot, "json"), csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, local },
            SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo(),
            out var externalShared);

        Assert.Contains(local, result);
        Assert.DoesNotContain(shared, result);
        // Reported for the analysis compilation; the compile-set-excluded TFM
        // variant stays out.
        Assert.Equal(new[] { shared }, externalShared);
        Assert.DoesNotContain(tfmVariant, externalShared);
    }

    [Fact]
    public void UnparseableProject_FilesUnderProjectDirectory_AreKeptConservatively()
    {
        // fullSet == null (root element is not <Project>): no compile set can be
        // trusted, so files under the project directory stay in scope.
        var csproj = WriteProject(
            Path.Combine("broken", "src"),
            "broken",
            "<NotAProject><ItemGroup><Compile Include=\"Kept.cs\" /></ItemGroup></NotAProject>");

        var kept = WriteFile("broken", "src", "Kept.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, kept }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(kept, result);
        Assert.Contains(csproj, result);
    }

    [Fact]
    public void ComputeCompiledFiles_WithoutDefaultItems_ResolvesOnlyExplicitIncludes()
    {
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "FunctionalTests"),
            "dotnet-uri-functional-tests",
            SimpleProject(
                "<Compile Include=\"LocalTest.cs\" />",
                "<Compile Include=\"..\\Common\\System\\ThreadCultureChange.cs\" Link=\"Common\\ThreadCultureChange.cs\" />"));
        var local = WriteFile("uri", "tests", "FunctionalTests", "LocalTest.cs");
        var loose = WriteFile("uri", "tests", "FunctionalTests", "LooseSdkFile.cs");
        var linked = WriteFile("uri", "tests", "Common", "System", "ThreadCultureChange.cs");

        var compiled = ProjectCompileSet.ComputeCompiledFiles(
            csproj, _sourceRoot, includeDefaultItems: false);

        Assert.NotNull(compiled);
        Assert.Contains(local, compiled!);
        Assert.Contains(linked, compiled!);
        // EnableDefaultCompileItems glob expansion is skipped: a loose .cs in
        // the project directory that no <Compile> names is not reported.
        Assert.DoesNotContain(loose, compiled!);
    }
}
