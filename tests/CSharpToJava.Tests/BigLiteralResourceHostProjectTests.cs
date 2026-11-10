using System.Text;
using System.Xml.Linq;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.CLI.Preprocessing.SupportLibraries;
using CSharpToJava.Core.BigLiteralResource;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for the <c>big-literal-resource</c> pre-pass: a shared source file
/// compiled by more than one project must register its extracted payload in
/// EVERY compiling project. Registering only the project nearest the resource
/// directory left the linking project without the
/// <c>EmbeddedResource</c>/<c>LogicalName</c>, so the runtime loader threw
/// "cs2j resource ... was not found in any loaded assembly" (the
/// System.Private.Xml XmlReaderLib case).
/// </summary>
public sealed class BigLiteralResourceHostProjectTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cs2j-biglit-host-" + Guid.NewGuid().ToString("N"));

    private string LibDir => Path.Combine(_root, "Lib");
    private string TestsDir => Path.Combine(_root, "Tests");
    private string OtherDir => Path.Combine(_root, "Other");

    private static string BigByteArraySource(int count = 8192)
    {
        var builder = new StringBuilder();
        builder.AppendLine("public static class Demo");
        builder.AppendLine("{");
        builder.AppendLine("    public static readonly byte[] Data = new byte[]");
        builder.AppendLine("    {");
        for (var index = 0; index < count; index++)
        {
            builder.Append("        0x").Append((index % 256).ToString("X2"));
            builder.AppendLine(index == count - 1 ? string.Empty : ",");
        }

        builder.AppendLine("    };");
        builder.AppendLine("}");
        return builder.ToString();
    }

    [Fact]
    public async Task Shared_source_registers_the_resource_in_every_compiling_project()
    {
        Directory.CreateDirectory(LibDir);
        Directory.CreateDirectory(TestsDir);
        Directory.CreateDirectory(OtherDir);

        File.WriteAllText(Path.Combine(LibDir, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var sharedFile = Path.Combine(LibDir, "Shared.cs");
        File.WriteAllText(sharedFile, BigByteArraySource());

        File.WriteAllText(Path.Combine(TestsDir, "Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="..\Lib\Shared.cs" Link="Shared.cs" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(OtherDir, "Other.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var libProject = Path.Combine(LibDir, "Lib.csproj");
        var testsProject = Path.Combine(TestsDir, "Tests.csproj");
        var otherProject = Path.Combine(OtherDir, "Other.csproj");
        var layout = new ProjectPreprocessSourceLayout(
            _root, _root, new[] { libProject, testsProject, otherProject });

        var tree = new PassTreeContext
        {
            Layout = layout,
            SourceRoot = _root,
            IntermediateRoot = _root,
            DestinationRoot = _root,
            ProjectGraph = null,
            Exclusion = new SupportedProjectFiltering.ExclusionInfo(),
            SourceFiles = new[] { sharedFile },
            CsFiles = new[] { sharedFile },
            ExternalSharedCompileFiles = Array.Empty<string>(),
            InPlace = true,
            Snapshot = new InPlaceSourceSnapshot(true),
        };

        var source = File.ReadAllText(sharedFile);
        var adapter = new BigLiteralResourcePassAdapter(new BigLiteralResourceOptions(), verbose: false);
        var rewrite = Assert.IsType<PassFileRewrite>(await adapter.RewriteFileAsync(new PassFileContext
        {
            SourceCode = source,
            SourceFilePath = sharedFile,
            RelativePath = "Lib/Shared.cs",
            DestinationFilePath = sharedFile,
        }));
        await adapter.AfterFilesAsync(tree);

        var resourceDirectory = Path.Combine(LibDir, "Cs2jResources");
        Assert.True(Directory.Exists(resourceDirectory), "payload directory was not created");
        Assert.Single(Directory.GetFiles(resourceDirectory));

        var logicalName = Assert.Single(
            XDocument.Load(libProject).Descendants()
                .Where(e => e.Name.LocalName == "EmbeddedResource")
                .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "LogicalName"))
                .Select(e => e.Value));
        Assert.StartsWith("cs2j/", logicalName, StringComparison.Ordinal);
        Assert.Contains($"\"{logicalName}\"", rewrite.OutputCode, StringComparison.Ordinal);

        AssertRegistersResource(libProject, logicalName);
        AssertRegistersResource(testsProject, logicalName);
        Assert.DoesNotContain(
            XDocument.Load(otherProject).Descendants(),
            e => e.Name.LocalName == "EmbeddedResource");
    }

    /// <summary>
    /// The host set is computed from the syntax compile set, so a forward-slash
    /// recursive glob (<c>&lt;Compile Include="**/*.cs" /&gt;</c>, the form the
    /// System.Private.Xml test project uses) must expand to nested files.
    /// </summary>
    [Fact]
    public void Recursive_compile_glob_makes_a_project_a_host_for_nested_files()
    {
        var appDir = Path.Combine(_root, "GlobApp");
        var nestedDir = Path.Combine(appDir, "Nested");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(appDir, "GlobApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="**/*.cs" />
              </ItemGroup>
            </Project>
            """);
        var nestedFile = Path.Combine(nestedDir, "ReadTo.cs");
        File.WriteAllText(nestedFile, "class ReadTo { }");

        var project = Path.Combine(appDir, "GlobApp.csproj");
        var layout = new ProjectPreprocessSourceLayout(_root, _root, new[] { project });

        var hosts = Cs2jArraySupportLibrary.ComputeHostProjectDirectories(
            layout,
            new SupportedProjectFiltering.ExclusionInfo(),
            new[] { nestedFile },
            projectGraph: null,
            intermediateRoot: _root);

        Assert.Contains(appDir, hosts);
    }

    private static void AssertRegistersResource(string projectFile, string logicalName)
    {
        var document = XDocument.Load(projectFile);
        Assert.Contains(
            document.Descendants().Where(e => e.Name.LocalName == "EmbeddedResource"),
            element => element.Elements()
                .Any(child => child.Name.LocalName == "LogicalName" && child.Value == logicalName));
        Assert.Contains(
            document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"),
            element => ((string?)element.Attribute("Include"))?.EndsWith(
                "Cs2jPrelude.csproj", StringComparison.OrdinalIgnoreCase) == true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp dir best effort */ }
    }
}
