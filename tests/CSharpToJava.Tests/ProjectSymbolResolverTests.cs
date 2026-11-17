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

    [Fact]
    public void ResolvedTfm_SkipsUnresolvableProjectTfm_AndSynthesizesModernSymbols()
    {
        var file = WriteFile("App/Code.cs");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>$(NetCoreAppCurrent);netstandard2.0</TargetFrameworks></PropertyGroup>
            </Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file, resolvedTfm: "net10.0");

        Assert.Contains("NET", result.Symbols);
        Assert.Contains("NET10_0", result.Symbols);
        Assert.Contains("NET10_0_OR_GREATER", result.Symbols);
        Assert.Contains("NETCOREAPP", result.Symbols);
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.Contains("skipped unresolvable TFM value", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectoryWithMultipleProjects_UsesTheProjectThatCompilesTheFile()
    {
        const string code = """
            #if SYSTEM_PRIVATE_CORELIB
            public
            #else
            internal
            #endif
            struct Rune {}
            """;
        var file = WriteFile("Lib/src/System/Text/Rune.cs", code);
        WriteFile("Lib/dotnet-System.Globalization.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup><Compile Include="src/System/Globalization/*.cs" /></ItemGroup>
            </Project>
            """);
        WriteFile("Lib/dotnet-System.Private.CoreLib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <PropertyGroup><DefineConstants>$(DefineConstants);SYSTEM_PRIVATE_CORELIB</DefineConstants></PropertyGroup>
              <ItemGroup><Compile Include="src/System/Text/**/*.cs" /></ItemGroup>
            </Project>
            """);

        var symbols = ProjectSymbolResolver.ResolveForFile(file, "Debug");
        Assert.Contains("SYSTEM_PRIVATE_CORELIB", symbols.Symbols);

        var result = new ConditionalDirectiveStripper().Strip(
            code,
            new ConditionalDirectiveStripOptions { Symbols = symbols.Symbols });
        Assert.Contains("public", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("internal", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFile_CompiledWithDifferentSymbols_PreservesDirectives()
    {
        const string code = """
            #if PROJECT_A
            public
            #else
            internal
            #endif
            class Shared {}
            """;
        var file = WriteFile("Common/Shared.cs", code);
        WriteFile("LibA/LibA.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <DefineConstants>PROJECT_A</DefineConstants>
              </PropertyGroup>
              <ItemGroup><Compile Include="../Common/Shared.cs" /></ItemGroup>
            </Project>
            """);
        WriteFile("LibB/LibB.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <DefineConstants>PROJECT_B</DefineConstants>
              </PropertyGroup>
              <ItemGroup><Compile Include="../Common/Shared.cs" /></ItemGroup>
            </Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file, sourceRoot: _root);

        Assert.True(result.PreserveDirectives);
        Assert.Empty(result.Symbols);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("preserved conditional directives", warning, StringComparison.Ordinal);
        Assert.Contains("2 projects", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFile_CompiledWithIdenticalSymbols_UsesThatSymbolSet()
    {
        const string code = """
            #if NET
            class NetOnly {}
            #endif
            """;
        var file = WriteFile("Common/Shared.cs", code);
        foreach (var name in new[] { "LibA", "LibB" })
        {
            WriteFile($"{name}/{name}.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <DefineConstants>$(DefineConstants);SHARED</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="../Common/Shared.cs" /></ItemGroup>
                </Project>
                """);
        }

        var symbols = ProjectSymbolResolver.ResolveForFile(file, sourceRoot: _root);

        Assert.False(symbols.PreserveDirectives);
        Assert.Contains("NET", symbols.Symbols);
        Assert.Contains("SHARED", symbols.Symbols);

        var result = new ConditionalDirectiveStripper().Strip(
            code,
            new ConditionalDirectiveStripOptions { Symbols = symbols.Symbols });
        Assert.Contains("class NetOnly", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWithoutOwningProject_PreservesDirectives()
    {
        var file = WriteFile("Loose/Code.cs", "#if ANY\nclass C {}\n#endif\n");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
            </Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file, sourceRoot: _root);

        Assert.True(result.PreserveDirectives);
        Assert.Empty(result.Symbols);
    }

    [Fact]
    public void CompileInclude_ExpandsMSBuildThisFileDirectory()
    {
        var file = WriteFile("Common/Linked.cs", "#if LINKED\nclass C {}\n#endif\n");
        WriteFile("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <DefineConstants>LINKED</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)../Common/Linked.cs" />
              </ItemGroup>
            </Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file, sourceRoot: _root);

        Assert.False(result.PreserveDirectives);
        Assert.Contains("LINKED", result.Symbols);

        var stripped = new ConditionalDirectiveStripper().Strip(
            "#if LINKED\nclass C {}\n#endif\n",
            new ConditionalDirectiveStripOptions { Symbols = result.Symbols });
        Assert.Contains("class C", stripped.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFile_WithUnusedDifferingSymbols_StillStrips()
    {
        const string code = """
            #if NET
            class NetOnly {}
            #endif
            """;
        var file = WriteFile("Common/Shared.cs", code);
        foreach (var (name, symbol) in new[] { ("LibA", "PROJECT_A"), ("LibB", "PROJECT_B") })
        {
            WriteFile($"{name}/{name}.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <DefineConstants>{symbol}</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="../Common/Shared.cs" /></ItemGroup>
                </Project>
                """);
        }

        var result = ProjectSymbolResolver.ResolveForFile(
            file, sourceRoot: _root, sourceCode: code);

        Assert.False(result.PreserveDirectives);
        Assert.Contains("NET", result.Symbols);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SharedFile_OwnerOutsideConversionScope_DoesNotPreserveDirectives()
    {
        const string code = """
            #if SHARPKIT
            class SharpKitOnly {}
            #else
            class DesktopOnly {}
            #endif
            """;
        var file = WriteFile("MSAGL/Core/RBTree.cs", code);
        WriteFile("MSAGL/AutomaticGraphLayout.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        WriteFile("MsaglSharpkit/MsaglSharpKit.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <DefineConstants>SHARPKIT</DefineConstants>
              </PropertyGroup>
              <ItemGroup><Compile Include="../MSAGL/Core/RBTree.cs" /></ItemGroup>
            </Project>
            """);

        // Whole-tree resolution sees both owners and preserves the directives.
        var unscoped = ProjectSymbolResolver.ResolveForFile(
            file, sourceRoot: _root, sourceCode: code);
        Assert.True(unscoped.PreserveDirectives);

        // The conversion entry only contains AutomaticGraphLayout.csproj, so the
        // linked compile item of the sibling project must not block the strip.
        var scoped = ProjectSymbolResolver.ResolveForFile(
            file,
            sourceRoot: _root,
            sourceCode: code,
            projectScope: [Path.Combine(_root, "MSAGL", "AutomaticGraphLayout.csproj")]);

        Assert.False(scoped.PreserveDirectives);
        Assert.Empty(scoped.Warnings);

        var stripped = new ConditionalDirectiveStripper().Strip(
            code,
            new ConditionalDirectiveStripOptions { Symbols = scoped.Symbols });
        Assert.Contains("class DesktopOnly", stripped.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpKitOnly", stripped.OutputCode, StringComparison.Ordinal);
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
