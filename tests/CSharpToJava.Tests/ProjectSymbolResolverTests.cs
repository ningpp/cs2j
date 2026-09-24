using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

/// <summary>
/// Symbol-resolution coverage per design §5:
/// DefineConstants scraping (Configuration conditions, append vs replace semantics,
/// Directory.Build.props chains, macro skipping) + TFM symbol synthesis.
/// </summary>
public class ProjectSymbolResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-psr-" + Guid.NewGuid().ToString("N"));

    private string WriteProject(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteFile(string relativePath, string content = "class F { }")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static bool Has(ProjectSymbols symbols, string name)
        => symbols.Symbols.Contains(name, StringComparer.Ordinal);

    // -----------------------------------------------------------------------
    // DefineConstants scraping
    // -----------------------------------------------------------------------

    [Fact]
    public void SimpleDefineConstants_Scraped()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <DefineConstants>FEATURE_A;FEATURE_B</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file, configuration: "Release");
        Assert.True(Has(symbols, "FEATURE_A"));
        Assert.True(Has(symbols, "FEATURE_B"));
        Assert.True(Has(symbols, "NET"));
        Assert.True(Has(symbols, "NET8_0"));
        Assert.False(Has(symbols, "DEBUG"));
        Assert.False(Has(symbols, "TRACE"));
    }

    [Fact]
    public void ConfigurationCondition_DebugOnlyExcludedUnderRelease()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <DefineConstants>DEBUG;TRACE</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <DefineConstants>TRACE</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var release = ProjectSymbolResolver.ResolveForFile(file, configuration: "Release");
        Assert.False(Has(release, "DEBUG"));
        Assert.True(Has(release, "TRACE"));

        var debug = ProjectSymbolResolver.ResolveForFile(file, configuration: "Debug");
        Assert.True(Has(debug, "DEBUG"));
        Assert.True(Has(debug, "TRACE"));
    }

    [Fact]
    public void AppendMacro_Semantics()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <DefineConstants>$(DefineConstants);EXTRA</DefineConstants>
              </PropertyGroup>
              <PropertyGroup>
                <DefineConstants>REPLACED</DefineConstants>
              </PropertyGroup>
              <PropertyGroup>
                <DefineConstants>$(DefineConstants);APPENDED</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file);
        Assert.True(Has(symbols, "REPLACED"));
        Assert.True(Has(symbols, "APPENDED"));
        Assert.False(Has(symbols, "EXTRA")); // append 先于 replace → 被替换清除
    }

    [Fact]
    public void DirectoryBuildProps_ChainAppliedRootToLeaf()
    {
        WriteProject("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <DefineConstants>ROOT_SYM</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        WriteProject("Sub/Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <DefineConstants>$(DefineConstants);SUB_SYM</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        WriteProject("Sub/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <DefineConstants>$(DefineConstants);PROJ_SYM</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("Sub/App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file);
        Assert.True(Has(symbols, "ROOT_SYM"));
        Assert.True(Has(symbols, "SUB_SYM"));
        Assert.True(Has(symbols, "PROJ_SYM"));
    }

    [Fact]
    public void UnknownMacros_SkippedWithWarning()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <DefineConstants>$(SomethingElse);REAL</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file);
        Assert.True(Has(symbols, "REAL"));
        Assert.DoesNotContain(symbols.Symbols, s => s.Contains("SomethingElse", StringComparison.Ordinal));
        Assert.Contains(symbols.Warnings, w => w.Contains("SomethingElse", StringComparison.Ordinal));
    }

    [Fact]
    public void NoCsproj_OnlyExtraDefines()
    {
        var file = WriteFile("Loose/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file, extraDefines: new[] { "CLI_SYM" });
        Assert.Equal(new[] { "CLI_SYM" }, symbols.Symbols);
    }

    [Fact]
    public void ExtraDefines_MergedAndCasePreserved()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file, extraDefines: new[] { "foo", "FOO" });
        Assert.Equal(2, symbols.Symbols.Count(s => s.Equals("foo", StringComparison.Ordinal) || s.Equals("FOO", StringComparison.Ordinal)));
    }

    // -----------------------------------------------------------------------
    // TFM synthesis
    // -----------------------------------------------------------------------

    [Fact]
    public void Tfm_net60_SynthesizesSdkSymbols()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net6.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file).Symbols;
        Assert.Contains("NET", symbols);
        Assert.Contains("NET6_0", symbols);
        Assert.Contains("NET6_0_OR_GREATER", symbols);
        Assert.Contains("NET5_0_OR_GREATER", symbols);
        Assert.Contains("NETCOREAPP3_1_OR_GREATER", symbols);
        Assert.Contains("NETCOREAPP1_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETSTANDARD2_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETFRAMEWORK", symbols);
        Assert.DoesNotContain("NETCOREAPP", symbols); // 不带版本的 NETCOREAPP 仅属于 netcoreapp 目标
    }

    [Fact]
    public void Tfm_netstandard20_SynthesizesNetStandardSymbols()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file).Symbols;
        Assert.Contains("NETSTANDARD", symbols);
        Assert.Contains("NETSTANDARD2_0", symbols);
        Assert.Contains("NETSTANDARD2_0_OR_GREATER", symbols);
        Assert.Contains("NETSTANDARD1_6_OR_GREATER", symbols);
        Assert.Contains("NETSTANDARD1_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NET", symbols);
        Assert.DoesNotContain("NETCOREAPP3_1_OR_GREATER", symbols);
    }

    [Fact]
    public void Tfm_net48_SynthesizesNetFrameworkSymbols()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net48</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file).Symbols;
        Assert.Contains("NETFRAMEWORK", symbols);
        Assert.Contains("NET48", symbols);
        Assert.Contains("NET48_OR_GREATER", symbols);
        Assert.Contains("NET472_OR_GREATER", symbols);
        Assert.Contains("NET40_OR_GREATER", symbols);
        Assert.DoesNotContain("NET", symbols);
        Assert.DoesNotContain("NET6_0_OR_GREATER", symbols);
    }

    [Fact]
    public void Tfm_net80_IncludesLowerOrGreaterChain()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file).Symbols;
        Assert.Contains("NET8_0", symbols);
        Assert.Contains("NET7_0_OR_GREATER", symbols);
        Assert.Contains("NET5_0_OR_GREATER", symbols);
        Assert.Contains("NETCOREAPP2_1_OR_GREATER", symbols);
    }

    [Fact]
    public void MultiTarget_FirstByDefault_TfmOverrideWins()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>netstandard2.0;net6.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");

        var first = ProjectSymbolResolver.ResolveForFile(file).Symbols;
        Assert.Contains("NETSTANDARD2_0", first);
        Assert.DoesNotContain("NET6_0", first);

        var overridden = ProjectSymbolResolver.ResolveForFile(file, tfm: "net6.0").Symbols;
        Assert.Contains("NET6_0", overridden);
        Assert.DoesNotContain("NETSTANDARD2_0", overridden);
    }

    [Fact]
    public void UnknownTfm_NoSymbolsWithWarning()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>nettoothfairy</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Empty(result.Symbols);
        Assert.Contains(result.Warnings, w => w.Contains("nettoothfairy", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetFrameworkMacro_SkippedWithWarning()
    {
        WriteProject("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>$( computedTfm )</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("App/Code.cs");
        var result = ProjectSymbolResolver.ResolveForFile(file);
        Assert.Empty(result.Symbols);
        Assert.Contains(result.Warnings, w => w.Contains("computedTfm", StringComparison.Ordinal));
    }

    [Fact]
    public void NearestCsproj_WinsOverFarther()
    {
        WriteProject("Outer/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <DefineConstants>OUTER_SYM</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        WriteProject("Outer/Inner/Inner.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <DefineConstants>INNER_SYM</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var file = WriteFile("Outer/Inner/Code.cs");
        var symbols = ProjectSymbolResolver.ResolveForFile(file);
        Assert.True(Has(symbols, "INNER_SYM"));
        Assert.False(Has(symbols, "OUTER_SYM"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
