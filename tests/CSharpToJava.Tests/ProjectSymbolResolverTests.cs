using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

public sealed class ProjectSymbolResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-symbols-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfigurationConditions_AndOr_AreApplied()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)' == 'Debug' Or '$(Configuration)' == 'Development'">
                <DefineConstants>DEBUG_ONLY</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' != 'Debug' And '$(Configuration)' != 'Development'">
                <DefineConstants>RELEASE_ONLY</DefineConstants>
              </PropertyGroup>
            </Project>
            """);

        var debug = ProjectSymbolResolver.ResolveForFile(file, "Debug");
        Assert.Contains("DEBUG_ONLY", debug.Symbols);
        Assert.DoesNotContain("RELEASE_ONLY", debug.Symbols);

        var release = ProjectSymbolResolver.ResolveForFile(file, "Release");
        Assert.Contains("RELEASE_ONLY", release.Symbols);
        Assert.DoesNotContain("DEBUG_ONLY", release.Symbols);
    }

    [Fact]
    public void DefineConstants_AppendAndReplace_AreOrdered()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>ROOT</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("App/Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>$(DefineConstants);CHILD</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>REPLACED</DefineConstants></PropertyGroup>
              <PropertyGroup><DefineConstants>$(DefineConstants);APPENDED</DefineConstants></PropertyGroup>
            </Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.DoesNotContain("ROOT", result.Symbols);
        Assert.DoesNotContain("CHILD", result.Symbols);
        Assert.Contains("REPLACED", result.Symbols);
        Assert.Contains("APPENDED", result.Symbols);
    }

    [Fact]
    public void DirectoryBuildProps_AreAppliedParentToChild()
    {
        var file = WriteFile("Sub/App/Code.cs");
        WriteFile("Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>ROOT</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("Sub/Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>$(DefineConstants);SUB</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("Sub/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DefineConstants>$(DefineConstants);PROJECT</DefineConstants></PropertyGroup></Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Equal(["ROOT", "SUB", "PROJECT"], result.Symbols);
    }

    [Fact]
    public void UnknownMacros_AreSkippedWithWarning()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DefineConstants>$(Other);REAL</DefineConstants></PropertyGroup></Project>
            """);
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Equal(["REAL"], result.Symbols);
        Assert.Single(result.Warnings);
        Assert.Contains("$(Other)", result.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NearestProject_Wins()
    {
        WriteFile("Outer/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DefineConstants>OUTER</DefineConstants></PropertyGroup></Project>
            """);
        WriteFile("Outer/Inner/Inner.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DefineConstants>INNER</DefineConstants></PropertyGroup></Project>
            """);
        var file = WriteFile("Outer/Inner/Code.cs");
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Contains("INNER", result.Symbols);
        Assert.DoesNotContain("OUTER", result.Symbols);
    }

    [Fact]
    public void OldStyleMsBuildXmlNamespace_IsSupported()
    {
        var file = WriteFile("Legacy/Code.cs");
        WriteFile("Legacy/Legacy.csproj", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFramework>net48</TargetFramework>
                <DefineConstants>LEGACY</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Contains("LEGACY", result.Symbols);
        Assert.Contains("NET48", result.Symbols);
    }

    [Fact]
    public void Configuration_AlsoSelectsTargetFramework()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'"><TargetFramework>net6.0</TargetFramework></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'"><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var debug = ProjectSymbolResolver.ResolveForFile(file, "Debug");
        Assert.Contains("NET6_0", debug.Symbols);
        Assert.DoesNotContain("NET8_0", debug.Symbols);
    }

    [Fact]
    public void ModernNet_SynthesizesExactCurrentLowerAndCompatibilitySymbols()
    {
        var symbols = Synthesize("net6.0");
        Assert.Contains("NET", symbols);
        Assert.Contains("NET6_0", symbols);
        Assert.Contains("NET6_0_OR_GREATER", symbols);
        Assert.Contains("NET5_0_OR_GREATER", symbols);
        Assert.Contains("NETCOREAPP", symbols);
        Assert.Contains("NETCOREAPP3_1_OR_GREATER", symbols);
        Assert.DoesNotContain("NET7_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETSTANDARD2_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETFRAMEWORK", symbols);
    }

    [Fact]
    public void NetCoreApp_OrGreaterChainStopsAtTarget()
    {
        var symbols = Synthesize("netcoreapp2.0");
        Assert.Contains("NETCOREAPP", symbols);
        Assert.Contains("NETCOREAPP2_0", symbols);
        Assert.Contains("NETCOREAPP2_0_OR_GREATER", symbols);
        Assert.Contains("NETCOREAPP1_1_OR_GREATER", symbols);
        Assert.DoesNotContain("NETCOREAPP2_1_OR_GREATER", symbols);
        Assert.DoesNotContain("NETCOREAPP3_1_OR_GREATER", symbols);
    }

    [Fact]
    public void NetStandard_OrGreaterChainStopsAtTarget()
    {
        var symbols = Synthesize("netstandard2.0");
        Assert.Contains("NETSTANDARD", symbols);
        Assert.Contains("NETSTANDARD2_0", symbols);
        Assert.Contains("NETSTANDARD2_0_OR_GREATER", symbols);
        Assert.Contains("NETSTANDARD1_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETSTANDARD2_1_OR_GREATER", symbols);
    }

    [Fact]
    public void NetFramework_UsesSdkSymbolSpelling()
    {
        var symbols = Synthesize("net48");
        Assert.Contains("NETFRAMEWORK", symbols);
        Assert.Contains("NET48", symbols);
        Assert.Contains("NET48_OR_GREATER", symbols);
        Assert.Contains("NET472_OR_GREATER", symbols);
        Assert.DoesNotContain("NET481_OR_GREATER", symbols);
    }

    [Fact]
    public void PlatformTfm_SynthesizesBaseFrameworkSymbols()
    {
        var symbols = Synthesize("net8.0-windows10.0.19041.0");
        Assert.Contains("NET8_0", symbols);
        Assert.Contains("NET8_0_OR_GREATER", symbols);
    }

    [Fact]
    public void MultiTarget_DefaultsToFirst_ExplicitTfmIsAuthoritative()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>netstandard2.0;net6.0</TargetFrameworks></PropertyGroup></Project>
            """);

        var defaultResult = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Contains("NETSTANDARD2_0", defaultResult.Symbols);
        Assert.DoesNotContain("NET6_0", defaultResult.Symbols);

        var overrideResult = ProjectSymbolResolver.ResolveForFile(file, tfm: "net8.0");
        Assert.Contains("NET8_0", overrideResult.Symbols);
        Assert.DoesNotContain("NETSTANDARD2_0", overrideResult.Symbols);
        Assert.Empty(overrideResult.Warnings);
    }

    [Fact]
    public void ExplicitTfm_WorksWithoutProject()
    {
        var file = WriteFile("Loose/Code.cs");
        var result = ProjectSymbolResolver.ResolveForFile(file, tfm: "net8.0", extraDefines: ["EXTRA"]);
        Assert.Contains("NET8_0", result.Symbols);
        Assert.Contains("EXTRA", result.Symbols);
    }

    [Fact]
    public void UnknownTfm_ProducesNoFrameworkSymbolsAndOneWarning()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>unknown</TargetFramework></PropertyGroup></Project>
            """);
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Empty(result.Symbols);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("unknown", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetFrameworkMacro_IsSkippedWithWarning()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>$(ComputedTfm)</TargetFramework></PropertyGroup></Project>
            """);
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Empty(result.Symbols);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("$(ComputedTfm)", warning, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> Synthesize(string tfm)
    {
        var symbols = new List<string>();
        Assert.True(ProjectSymbolResolver.TrySynthesizeTfmSymbols(tfm, symbols));
        return symbols;
    }

    private string WriteFile(string relativePath, string content = "class F {}")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
