using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing.SupportLibraries;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Regression for CS0103 "Cs2jArray does not exist" in projects that compile
/// a rewritten shared source through an explicit linked
/// <c>&lt;Compile Include="..\..\src\X.cs" Link="…" /&gt;</c> (the uri
/// ExtendedFunctionalTests linking Common/ArrayBuilder.cs). Host detection
/// used directory containment, so only the production project owning the file
/// got the Cs2jArray ProjectReference; the linked test project did not.
/// </summary>
public class Cs2jArrayHostProjectsTests
{
    private sealed class Tree : IDisposable
    {
        public string Root { get; }
        public string LibDir { get; }
        public string TestDir { get; }
        public string OtherDir { get; }

        public Tree()
        {
            Root = Path.Combine(Path.GetTempPath(), "cs2j-host-" + Guid.NewGuid().ToString("N"));
            LibDir = Path.Combine(Root, "src", "lib");
            TestDir = Path.Combine(Root, "tests", "t");
            OtherDir = Path.Combine(Root, "tests", "other");
            Directory.CreateDirectory(LibDir);
            Directory.CreateDirectory(TestDir);
            Directory.CreateDirectory(OtherDir);

            File.WriteAllText(Path.Combine(LibDir, "lib.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(LibDir, "Shared.cs"), "class Shared { }");
            File.WriteAllText(Path.Combine(LibDir, "Other.cs"), "class OtherShared { }");

            File.WriteAllText(Path.Combine(TestDir, "t.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="..\..\src\lib\Shared.cs" Link="Shared.cs" />
                    <Compile Include="Test.cs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(TestDir, "Test.cs"), "class Test { }");

            File.WriteAllText(Path.Combine(OtherDir, "other.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="OtherTest.cs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(OtherDir, "OtherTest.cs"), "class OtherTest { }");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* temp dir best effort */ }
        }
    }

    [Fact]
    public void Linked_Shared_Rewritten_File_Marks_Both_Projects_As_Hosts()
    {
        using var tree = new Tree();
        var libProject = Path.Combine(tree.LibDir, "lib.csproj");
        var testProject = Path.Combine(tree.TestDir, "t.csproj");
        var otherProject = Path.Combine(tree.OtherDir, "other.csproj");
        var layout = new ProjectPreprocessSourceLayout(
            tree.Root, libProject, new[] { libProject, testProject, otherProject });
        var rewritten = new[] { Path.Combine(tree.LibDir, "Shared.cs") };

        var hosts = Cs2jArraySupportLibrary.ComputeHostProjectDirectories(
            layout, new SupportedProjectFiltering.ExclusionInfo(),
            rewritten, projectGraph: null, intermediateRoot: tree.Root);

        Assert.Contains(tree.LibDir, hosts);
        Assert.Contains(tree.TestDir, hosts);
        Assert.DoesNotContain(tree.OtherDir, hosts);
    }

    [Fact]
    public void Unlinked_Rewritten_File_Marks_Only_Owning_Project_As_Host()
    {
        using var tree = new Tree();
        var libProject = Path.Combine(tree.LibDir, "lib.csproj");
        var testProject = Path.Combine(tree.TestDir, "t.csproj");
        var otherProject = Path.Combine(tree.OtherDir, "other.csproj");
        var layout = new ProjectPreprocessSourceLayout(
            tree.Root, libProject, new[] { libProject, testProject, otherProject });
        var rewritten = new[] { Path.Combine(tree.LibDir, "Other.cs") };

        var hosts = Cs2jArraySupportLibrary.ComputeHostProjectDirectories(
            layout, new SupportedProjectFiltering.ExclusionInfo(),
            rewritten, projectGraph: null, intermediateRoot: tree.Root);

        Assert.Contains(tree.LibDir, hosts);
        Assert.DoesNotContain(tree.TestDir, hosts);
        Assert.DoesNotContain(tree.OtherDir, hosts);
    }
}
