using System.Text;
using CSharpToJava.CLI;
using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

public sealed class StripConditionalCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-strip-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Parser_AcceptsRepeatedDefineOptions()
    {
        var input = WriteText("repeat/input.cs", "#if A && B\nclass C {}\n#endif\n");
        var output = Path.Combine(_root, "repeat", "output.cs");

        var exitCode = await Program.MainImpl([
            "strip-conditional", "-i", input, "-o", output,
            "--define", "A", "--define", "B",
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal("class C {}\n", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task SingleFile_UsesOnlyCliAndInFileSymbols()
    {
        WriteText("single/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>PROJECT_ONLY</DefineConstants></PropertyGroup>
            </Project>
            """);
        var input = WriteText("single/input.cs", "#if PROJECT_ONLY\nclass C {}\n#endif\n");
        var output = Path.Combine(_root, "single", "output.cs");

        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Input = input,
            Output = output,
        }, TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Strict_ChangesWarningExitCodeOnly()
    {
        var input = WriteText("warning/input.cs", "#warning fixme\nclass C {}\n");
        var normalOutput = Path.Combine(_root, "warning", "normal.cs");
        var strictOutput = Path.Combine(_root, "warning", "strict.cs");

        var normalExit = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Input = input,
            Output = normalOutput,
        }, TextWriter.Null, TextWriter.Null);
        var strictExit = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Input = input,
            Output = strictOutput,
            Strict = true,
        }, TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, normalExit);
        Assert.Equal(1, strictExit);
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(normalOutput));
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(strictOutput));
    }

    [Fact]
    public async Task DirectoryMode_TransformsCopiesPreservesBomSkipsNestedDestinationAndReportsExactTotals()
    {
        var source = Path.Combine(_root, "tree");
        var destination = Path.Combine(source, "out");
        WriteText("tree/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>A</DefineConstants></PropertyGroup>
            </Project>
            """);
        WriteBytes("tree/Dirty.cs", [.. SourceTextIo.Utf8Bom, .. Encoding.UTF8.GetBytes("#if A\nclass C {}\n#else\nclass D {}\n#endif\n")]);
        var cleanBytes = Encoding.UTF8.GetBytes("class Clean {}\r\n");
        WriteBytes("tree/Clean.cs", cleanBytes);
        var assetBytes = new byte[] { 0, 1, 2, 255 };
        WriteBytes("tree/asset.bin", assetBytes);

        var stdout = new StringWriter();
        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = source,
            Destination = destination,
        }, stdout, TextWriter.Null);

        Assert.Equal(0, exitCode);
        byte[] expectedDirty = [.. SourceTextIo.Utf8Bom, .. Encoding.UTF8.GetBytes("class C {}\n")];
        var actualDirty = await File.ReadAllBytesAsync(Path.Combine(destination, "Dirty.cs"));
        Assert.Equal(expectedDirty, actualDirty);
        Assert.Equal(cleanBytes, await File.ReadAllBytesAsync(Path.Combine(destination, "Clean.cs")));
        Assert.Equal(assetBytes, await File.ReadAllBytesAsync(Path.Combine(destination, "asset.bin")));
        Assert.False(Directory.Exists(Path.Combine(destination, "out")));
        Assert.Equal(
            "strip-conditional: 2 scanned, 1 transformed, 1 clean, 0 failed, 3 directives removed, 1 disabled blocks removed" + Environment.NewLine,
            stdout.ToString());
    }

    [Fact]
    public async Task ProjectPathInPlace_TransformsNestedFilesWithoutSelfCopy()
    {
        var project = WriteText("in-place/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>A</DefineConstants></PropertyGroup>
            </Project>
            """);
        var nested = WriteText("in-place/Sub/Code.cs", "#if A\nclass C {}\n#endif\n");

        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = project,
            Destination = Path.GetDirectoryName(project),
        }, TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Equal("class C {}\n", await File.ReadAllTextAsync(nested));
    }

    [Fact]
    public async Task ResolverWarning_IsWrittenOnce_AndStrictReturnsOne()
    {
        var source = Path.Combine(_root, "resolver-warning");
        var destination = Path.Combine(_root, "resolver-warning-output");
        WriteText("resolver-warning/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>unknown</TargetFramework></PropertyGroup>
            </Project>
            """);
        WriteText("resolver-warning/A.cs", "class A {}");
        WriteText("resolver-warning/B.cs", "class B {}");
        var stderr = new StringWriter();

        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = source,
            Destination = destination,
            Strict = true,
        }, TextWriter.Null, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, stderr.ToString().Split("unrecognized TFM", StringSplitOptions.None).Length - 1);
        Assert.Contains("[Warning]", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedFile_IsCopiedUnchanged_AndReturnsOne()
    {
        const string malformed = "#if A + B\nclass C {}\n#endif\n";
        var source = Path.Combine(_root, "malformed");
        var destination = Path.Combine(_root, "malformed-output");
        var input = WriteText("malformed/Bad.cs", malformed);
        var stderr = new StringWriter();

        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions
        {
            Source = source,
            Destination = destination,
        }, TextWriter.Null, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(Path.Combine(destination, "Bad.cs")));
        Assert.Contains("[Error] Bad.cs:1: CS1025", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingModeArguments_ReturnOne()
    {
        var stderr = new StringWriter();
        var exitCode = await StripConditionalCommand.RunAsync(new StripConditionalOptions(), TextWriter.Null, stderr);
        Assert.Equal(1, exitCode);
        Assert.Contains("provide (-i [-o]) or (-s <source> -d <destination>)", stderr.ToString(), StringComparison.Ordinal);
    }

    private string WriteText(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string WriteBytes(string relativePath, byte[] content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
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
