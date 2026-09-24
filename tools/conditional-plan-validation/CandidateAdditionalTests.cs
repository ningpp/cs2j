using System.Text;
using CSharpToJava.CLI;
using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

public sealed class CandidateAdditionalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-plan-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("#warning hidden")]
    [InlineData("#error hidden")]
    [InlineData("#pragma warning disable CS0168")]
    [InlineData("#pragma checksum \"x.cs\" \"{406EA660-64CF-4C82-B6F0-42D48172A799}\" \"001122\"")]
    [InlineData("#line hidden")]
    [InlineData("#nullable enable")]
    [InlineData("#region R\n#endregion")]
    public void InactiveNonConditionalDirective_IsRemoved(string directive)
    {
        var source = $"#if A\n{directive}\n#endif\n";
        var result = new ConditionalDirectiveStripper().Strip(source);
        Assert.True(result.Changed);
        Assert.Equal(string.Empty, result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
    }

    [Fact]
    public void TrulyInvalidExpression_IsBlocked()
    {
        const string source = "#if A + B\nX();\n#endif\n";
        var result = new ConditionalDirectiveStripper().Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
    }

    [Fact]
    public void CrLineEndings_ArePreserved()
    {
        var result = new ConditionalDirectiveStripper().Strip(
            "#if A\rX();\r#endif\r",
            new ConditionalDirectiveStripOptions { Symbols = ["A"] });
        Assert.Equal("X();\r", result.OutputCode);
    }

    [Fact]
    public void ConfigurationControlsTargetFramework()
    {
        var file = WriteFile("App/Code.cs", "class C { }");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <TargetFramework>net6.0</TargetFramework>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var debug = ProjectSymbolResolver.ResolveForFile(file, "Debug");
        Assert.Contains("NET6_0", debug.Symbols);
        Assert.DoesNotContain("NET8_0", debug.Symbols);
    }

    [Fact]
    public void ExplicitTfm_IsAuthoritative()
    {
        var file = WriteFile("App/Code.cs", "class C { }");
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var result = ProjectSymbolResolver.ResolveForFile(file, tfm: "net8.0");
        Assert.Contains("NET8_0", result.Symbols);
        Assert.DoesNotContain("NET6_0", result.Symbols);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LooseFile_ExplicitTfm_IsSynthesized()
    {
        var file = WriteFile("Loose/Code.cs", "class C { }");
        var result = ProjectSymbolResolver.ResolveForFile(file, tfm: "net8.0");
        Assert.Contains("NET8_0", result.Symbols);
    }

    [Fact]
    public void NetCoreAppOrGreater_IsBoundedByTarget()
    {
        var symbols = new List<string>();
        Assert.True(ProjectSymbolResolver.TrySynthesizeTfmSymbols("netcoreapp2.0", symbols));
        Assert.Contains("NETCOREAPP2_0_OR_GREATER", symbols);
        Assert.DoesNotContain("NETCOREAPP2_1_OR_GREATER", symbols);
        Assert.DoesNotContain("NETCOREAPP3_1_OR_GREATER", symbols);
    }

    [Fact]
    public void PlatformTfm_UsesBaseFrameworkSymbols()
    {
        var symbols = new List<string>();
        Assert.True(ProjectSymbolResolver.TrySynthesizeTfmSymbols("net8.0-windows10.0.19041.0", symbols));
        Assert.Contains("NET8_0", symbols);
        Assert.Contains("NETCOREAPP", symbols);
    }

    [Fact]
    public void OldStyleXmlNamespace_IsSupported()
    {
        var file = WriteFile("App/Code.cs", "class C { }");
        WriteFile("App/App.csproj", """
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
    public async Task AtomicWrite_ReplacesAndLeavesNoTemporaryFile()
    {
        var output = WriteFile("atomic/out.cs", "old");
        await SourceTextIo.WriteAtomicallyAsync(output, "new", hasBom: false);
        Assert.Equal("new", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(output)!, "*.tmp"));
    }

    [Fact]
    public async Task Cli_RepeatedDefines_AreAccepted()
    {
        var input = WriteFile("single/in.cs", "#if A && B\nclass C { }\n#endif\n");
        var output = Path.Combine(_root, "single", "out.cs");
        var exitCode = await Program.MainImpl([
            "strip-conditional", "-i", input, "-o", output,
            "--define", "A", "--define", "B",
        ]);
        Assert.Equal(0, exitCode);
        Assert.Equal("class C { }\n", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Cli_SingleFile_DoesNotReadAncestorProjectSymbols()
    {
        WriteFile("single-project/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>PROJECT_ONLY</DefineConstants></PropertyGroup>
            </Project>
            """);
        var input = WriteFile("single-project/in.cs", "#if PROJECT_ONLY\nclass C { }\n#endif\n");
        var output = Path.Combine(_root, "single-project", "out.cs");
        var exitCode = await Program.MainImpl(["strip-conditional", "-i", input, "-o", output]);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Cli_ProjectPathInPlace_TransformsNestedFiles()
    {
        var project = WriteFile("project/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>A</DefineConstants></PropertyGroup>
            </Project>
            """);
        var nested = WriteFile("project/Sub/Code.cs", "#if A\nclass C { }\n#endif\n");
        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = project,
            Destination = Path.GetDirectoryName(project),
        }, TextWriter.Null, TextWriter.Null);
        Assert.Equal(0, exitCode);
        Assert.Equal("class C { }\n", await File.ReadAllTextAsync(nested));
    }

    [Fact]
    public async Task Cli_NestedDestination_IsSkipped_AndNonCsBytesAreCopied()
    {
        var source = Path.Combine(_root, "tree");
        var destination = Path.Combine(source, "out");
        WriteFile("tree/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>A</DefineConstants></PropertyGroup>
            </Project>
            """);
        WriteFile("tree/Code.cs", "#if A\nclass C { }\n#else\nclass D { }\n#endif\n");
        var bytes = new byte[] { 0, 1, 2, 255 };
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "asset.bin"), bytes);

        var stdout = new StringWriter();
        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = source,
            Destination = destination,
        }, stdout, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Equal("class C { }\n", await File.ReadAllTextAsync(Path.Combine(destination, "Code.cs")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(destination, "asset.bin")));
        Assert.False(Directory.Exists(Path.Combine(destination, "out")));
        Assert.Equal(
            "strip-conditional: 1 scanned, 1 transformed, 0 clean, 0 failed, 3 directives removed, 1 disabled blocks removed" + Environment.NewLine,
            stdout.ToString());
    }

    [Fact]
    public async Task Cli_StrictTreatsResolverWarningAsFailure()
    {
        var source = Path.Combine(_root, "strict");
        var destination = Path.Combine(_root, "strict-out");
        WriteFile("strict/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>unknown</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        WriteFile("strict/Code.cs", "class C { }");
        var stderr = new StringWriter();
        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = source,
            Destination = destination,
            Strict = true,
        }, TextWriter.Null, stderr);
        Assert.Equal(1, exitCode);
        Assert.Contains("[Warning]", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("unrecognized TFM 'unknown'", stderr.ToString(), StringComparison.Ordinal);
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
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
