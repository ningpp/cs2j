using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.Tests;

public class PrimitiveStaticConstantMappingTests
{

    [Fact]
    public void StringCompareOrdinal_TwoArgs_MapsToStringHelperCompareOrdinal()
    {
        var result = Convert("""
public class Sample
{
    public int Test(string a, string b)
    {
        return string.CompareOrdinal(a, b);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("StringHelper.compareOrdinal(a, b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringCompareOrdinal_FiveArgs_MapsToStringHelperCompareOrdinal()
    {
        var result = Convert("""
public class Sample
{
    public int Test(string a, string b)
    {
        return string.CompareOrdinal(a, 0, b, 0, 3);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("StringHelper.compareOrdinal(a, 0, b, 0, 3)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringAliasCompareOrdinal_TwoArgs_MapsToStringHelperCompareOrdinal()
    {
        var result = Convert("""
public class Sample
{
    public int Test(string a, string b)
    {
        return String.CompareOrdinal(a, b);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("StringHelper.compareOrdinal(a, b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringAliasCompareOrdinal_FiveArgs_MapsToStringHelperCompareOrdinal()
    {
        var result = Convert("""
public class Sample
{
    public int Test(string a, string b)
    {
        return String.CompareOrdinal(a, 1, b, 2, 4);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("StringHelper.compareOrdinal(a, 1, b, 2, 4)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxedDoubleNaN_InStaticPropertyGetter_UsesJavaWrapperConstant()
    {
        var result = Convert(@"
public class Sample
{
    public static double NoFixedPosition
    {
        get { return Double.NaN; }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MsbuildProjectStructGetter_BoxedDoubleNaN_UsesJavaWrapperConstant()
    {
        SolutionLoader.EnsureMSBuildRegistered();

        var projectDir = Path.Combine(Path.GetTempPath(), "cs2j-msbuild-double-nan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        try
        {
            var projectPath = Path.Combine(projectDir, "AutomaticGraphLayout.csproj");
            var borderInfoPath = Path.Combine(projectDir, "Core", "Geometry", "OverlapRemoval", "BorderInfo.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(borderInfoPath)!);
            File.WriteAllText(projectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
</Project>
""");
            File.WriteAllText(borderInfoPath, """
using System;

namespace Microsoft.Msagl.Core.Geometry
{
    public struct BorderInfo
    {
        public static double NoFixedPosition
        {
            get { return Double.NaN; }
        }
    }
}
""");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };
            using var loader = new SolutionLoader();
            var projects = await loader.OpenProjectAsync(projectPath);
            var project = Assert.Single(projects, p => p.FilePath.Equals(projectPath, StringComparison.OrdinalIgnoreCase));

            var pipeline = new ProjectConversionPipeline(options);
            var results = await pipeline.ConvertProjectAsync(
                project.Compilation,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { borderInfoPath },
                project.Name,
                project.FilePath,
                project.ProjectReferences,
                project.IsTestProject);

            var result = Assert.Single(results, r => r.FileName == "BorderInfo.java");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
            Assert.DoesNotContain("return double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(projectDir))
                Directory.Delete(projectDir, recursive: true);
        }
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
