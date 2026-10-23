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

    // 回归（xml-tiny NumberFunctions/TemplateAction/XPathScanner）：double/float 的
    // NaN/Infinity/MinValue/MaxValue/Epsilon 以原始关键字拼写（double.NaN，而非
    // System.Double 别名拼写）出现在方法返回与字段初始化器中时，同样必须落到
    // Java 包装类常量——原始关键字在 Java 里不可解引用，double.NaN 直接非法。
    // MinValue 语义差（C# 最负值 vs Java MIN_VALUE 最小正值）必须取 -MAX_VALUE。
    [Fact]
    public void PrimitiveKeywordFloatDoubleConstants_AllSiteShapes_UseWrapperConstants()
    {
        var result = Convert(@"
public class Sample
{
    private double _priority = double.NaN;
    private float _fInfinity = float.PositiveInfinity;
    public double Get() { return double.NaN; }
    public double Neg() { return double.MinValue; }
    public double Pos() { return double.MaxValue; }
    public float NegInf() { return float.NegativeInfinity; }
    public float Eps() { return float.Epsilon; }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("_priority = Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_fInfinity = Float.POSITIVE_INFINITY;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return (-Double.MAX_VALUE);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Double.MAX_VALUE;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Float.NEGATIVE_INFINITY;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Float.MIN_VALUE;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("double.NaN", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("double.MinValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("double.MaxValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("float.PositiveInfinity", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("float.NegativeInfinity", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("float.Epsilon", result.GeneratedCode, StringComparison.Ordinal);
    }

    [SlowE2EFact(5.5)]
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
