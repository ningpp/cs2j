using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// W-10: csproj 门控的 Compile 项 / TFM Condition / 外部共享源必须进入预处理
/// 输入模型。语法求值器必须对 Condition 求值；语法欠定时才升级到真正的
/// MSBuild 求值（测试通过 <see cref="MsBuildCompileEvaluator.EvaluationOverride"/>
/// 注入，绝不启动 msbuild 进程）。
/// </summary>
[Collection(MsBuildCompileEvaluatorOverridesCollection.CollectionName)]
public sealed class ProjectCompileSetConditionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _sourceRoot;

    public ProjectCompileSetConditionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-compile-condition-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void MutuallyExclusiveTfmGroups_SelectOnlyFirstTargetFrameworkVariant()
    {
        // Red before the fix: without Condition evaluation both ItemGroups are
        // collected, yielding JsonReaderHelper.net8.cs AND
        // JsonReaderHelper.netstandard.cs (duplicate members).
        var csproj = WriteProject(
            "multi-tfm",
            "MultiTfm",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETCoreApp'">
                <Compile Include="Variant.net8.cs" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFrameworkIdentifier)' != '.NETCoreApp'">
                <Compile Include="Variant.netstandard.cs" />
              </ItemGroup>
            </Project>
            """);

        var net8Variant = WriteFile("multi-tfm", "Variant.net8.cs");
        var netstandardVariant = WriteFile("multi-tfm", "Variant.netstandard.cs");

        var set = ProjectCompileSet.ComputeCompiledSet(csproj, _sourceRoot);

        Assert.NotNull(set);
        Assert.False(set!.UnderDetermined);
        Assert.Contains(net8Variant, set.Files);
        Assert.DoesNotContain(netstandardVariant, set.Files);
    }

    [Fact]
    public void AndOrNotExistsAndPropertyConditions_AreEvaluated()
    {
        var csproj = WriteProject(
            "conditions",
            "Conditions",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <EnableFeature Condition="'$(TargetFramework)' == 'net8.0'">true</EnableFeature>
                <DisabledFeature Condition="'$(TargetFramework)' == 'net48'">true</DisabledFeature>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="And.cs" Condition="'$(TargetFrameworkIdentifier)' == '.NETCoreApp' And '$(TargetFramework)' != 'net48'" />
                <Compile Include="Or.cs" Condition="'$(TargetFramework)' == 'net48' Or '$(TargetFrameworkIdentifier)' == '.NETCoreApp'" />
                <Compile Include="Not.cs" Condition="!('$(TargetFramework)' == 'net8.0')" />
                <Compile Include="Feature.cs" Condition="'$(EnableFeature)' == 'true'" />
                <Compile Include="DisabledFeature.cs" Condition="'$(DisabledFeature)' == 'true'" />
                <Compile Include="Exists.cs" Condition="Exists('Exists.cs')" />
                <Compile Include="Missing.cs" Condition="Exists('Missing.cs')" />
                <Compile Include="BareTrue.cs" Condition="true" />
                <Compile Include="QuotedFalse.cs" Condition="'false'" />
                <Compile Include="EmptyLeft.cs" Condition="$(UnknownFeature) == 'true'" />
              </ItemGroup>
            </Project>
            """);

        var and = WriteFile("conditions", "And.cs");
        var or = WriteFile("conditions", "Or.cs");
        var not = WriteFile("conditions", "Not.cs");
        var feature = WriteFile("conditions", "Feature.cs");
        var disabled = WriteFile("conditions", "DisabledFeature.cs");
        var exists = WriteFile("conditions", "Exists.cs");
        // Missing.cs is deliberately NOT created on disk so Exists('Missing.cs')
        // evaluates to false.
        var missing = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(csproj)!, "Missing.cs"));
        var bareTrue = WriteFile("conditions", "BareTrue.cs");
        var quotedFalse = WriteFile("conditions", "QuotedFalse.cs");
        var emptyLeft = WriteFile("conditions", "EmptyLeft.cs");

        var set = ProjectCompileSet.ComputeCompiledSet(csproj, _sourceRoot);

        Assert.NotNull(set);
        Assert.False(set!.UnderDetermined);
        Assert.Contains(and, set.Files);
        Assert.Contains(or, set.Files);
        Assert.Contains(feature, set.Files);
        Assert.Contains(exists, set.Files);
        Assert.Contains(bareTrue, set.Files);
        Assert.DoesNotContain(not, set.Files);
        Assert.DoesNotContain(disabled, set.Files);
        Assert.DoesNotContain(missing, set.Files);
        Assert.DoesNotContain(quotedFalse, set.Files);
        Assert.DoesNotContain(emptyLeft, set.Files);
    }

    [Fact]
    public void ExternalPropertyInclude_TriggersMsBuildEvaluation()
    {
        var csproj = WriteProject(
            "external",
            "External",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="$(ExternalDir)Shared.cs" />
              </ItemGroup>
            </Project>
            """);

        var evaluatedFile = Path.Combine(_tempRoot, "evaluated", "Shared.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(evaluatedFile)!);
        File.WriteAllText(evaluatedFile, "// stub");

        var calls = 0;
        try
        {
            MsBuildCompileEvaluator.EvaluationOverride = path =>
            {
                calls++;
                Assert.Equal(csproj, Path.GetFullPath(path));
                return new MsBuildCompileEvaluation
                {
                    Files = new[] { evaluatedFile },
                    TargetFramework = "net8.0",
                };
            };

            var set = ProjectCompileSet.ComputeCompiledSet(
                csproj, _sourceRoot, allowMsBuildEvaluation: true);

            Assert.Equal(1, calls);
            Assert.NotNull(set);
            Assert.True(set!.DefaultItemsInEffect);
            Assert.False(set.UnderDetermined);
            Assert.Contains(Path.GetFullPath(evaluatedFile), set.Files);
        }
        finally
        {
            MsBuildCompileEvaluator.EvaluationOverride = null;
        }
    }

    [Fact]
    public void FullyResolvableProject_DoesNotInvokeMsBuildEvaluation()
    {
        var csproj = WriteProject(
            "resolvable",
            "Resolvable",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Local.cs" />
                <Compile Remove="Removed.cs" />
              </ItemGroup>
            </Project>
            """);

        var local = WriteFile("resolvable", "Local.cs");
        WriteFile("resolvable", "Removed.cs");

        var calls = 0;
        try
        {
            MsBuildCompileEvaluator.EvaluationOverride = _ =>
            {
                calls++;
                return null;
            };

            var set = ProjectCompileSet.ComputeCompiledSet(
                csproj, _sourceRoot, allowMsBuildEvaluation: true);

            Assert.Equal(0, calls);
            Assert.NotNull(set);
            Assert.False(set!.UnderDetermined);
            Assert.Contains(local, set.Files);
        }
        finally
        {
            MsBuildCompileEvaluator.EvaluationOverride = null;
        }
    }

    [Fact]
    public void MsBuildEvaluationFailure_FallsBackToSyntaxSet()
    {
        var csproj = WriteProject(
            "fallback",
            "Fallback",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="$(ExternalDir)Shared.cs" />
              </ItemGroup>
            </Project>
            """);

        var syntaxResolved = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(csproj)!, "Shared.cs"));

        try
        {
            MsBuildCompileEvaluator.EvaluationOverride = _ => null;

            var set = ProjectCompileSet.ComputeCompiledSet(
                csproj, _sourceRoot, allowMsBuildEvaluation: true);

            Assert.NotNull(set);
            Assert.True(set!.UnderDetermined);
            Assert.False(string.IsNullOrEmpty(set.UnderDeterminedReason));
            Assert.Contains(syntaxResolved, set.Files);
        }
        finally
        {
            MsBuildCompileEvaluator.EvaluationOverride = null;
        }
    }

    [Fact]
    public void ParseEvaluationJson_ResolvesIdentitiesAndDeduplicates()
    {
        var projectDirectory = Path.Combine(_tempRoot, "json-project");
        Directory.CreateDirectory(projectDirectory);

        const string json = """
            {
              "Properties": {
                "TargetFramework": "net10.0",
                "TargetFrameworks": "net10.0;netstandard2.0"
              },
              "Items": {
                "Compile": [
                  { "Identity": "System\\Text\\Json\\Reader\\JsonReaderHelper.cs" },
                  { "Identity": "D:\\runtime\\shared\\Marvin.cs" },
                  { "Identity": "System\\Text\\Json\\Reader\\JsonReaderHelper.cs" }
                ]
              }
            }
            """;

        var evaluation = MsBuildCompileEvaluator.ParseEvaluationJson(json, projectDirectory);

        Assert.NotNull(evaluation);
        Assert.Equal("net10.0", evaluation!.TargetFramework);
        Assert.Equal("net10.0;netstandard2.0", evaluation.TargetFrameworks);
        Assert.Equal(2, evaluation.Files.Count);
        Assert.Contains(
            Path.GetFullPath(Path.Combine(
                projectDirectory, "System", "Text", "Json", "Reader", "JsonReaderHelper.cs")),
            evaluation.Files);
        Assert.Contains(Path.GetFullPath("D:\\runtime\\shared\\Marvin.cs"), evaluation.Files);
    }

    [Theory]
    [InlineData("net8.0", ".NETCoreApp", "v8.0")]
    [InlineData("net10.0", ".NETCoreApp", "v10.0")]
    [InlineData("netcoreapp3.1", ".NETCoreApp", "v3.1")]
    [InlineData("netstandard2.0", ".NETStandard", "v2.0")]
    [InlineData("net48", ".NETFramework", "v4.8")]
    [InlineData("net462", ".NETFramework", "v4.6.2")]
    public void TargetFrameworkIdentifier_IsDerived(
        string targetFramework,
        string expectedIdentifier,
        string expectedVersion)
    {
        var derived = MSBuildPropertyReader.TryDeriveTargetFramework(
            targetFramework, out var identifier, out var version);

        Assert.True(derived);
        Assert.Equal(expectedIdentifier, identifier);
        Assert.Equal(expectedVersion, version);
    }
}
