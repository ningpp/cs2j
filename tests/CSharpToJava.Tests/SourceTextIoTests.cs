using System.Text;
using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

public sealed class SourceTextIoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-source-io-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BomFile_ReadsWithoutMarker_AndWritesWithMarker()
    {
        var input = WriteBytes("input.cs", [.. SourceTextIo.Utf8Bom, .. Encoding.UTF8.GetBytes("// 中文\nclass C {}")]);
        var source = await SourceTextIo.ReadAsync(input);
        Assert.True(source.HasBom);
        Assert.Equal("// 中文\nclass C {}", source.Code);

        var output = Path.Combine(_root, "output.cs");
        await SourceTextIo.WriteAsync(output, source.Code, source.HasBom);
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task NoBomUtf8_RoundTripsByteForByte()
    {
        var bytes = Encoding.UTF8.GetBytes("// 中文\r\nclass C {}\n");
        var input = WriteBytes("input.cs", bytes);
        var source = await SourceTextIo.ReadAsync(input);
        Assert.False(source.HasBom);

        var output = Path.Combine(_root, "output.cs");
        await SourceTextIo.WriteAsync(output, source.Code, source.HasBom);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task AtomicWrite_CreatesParent_ReplacesDestination_AndLeavesNoTempFile()
    {
        var output = Path.Combine(_root, "nested", "output.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "old", new UTF8Encoding(false));

        await SourceTextIo.WriteAtomicallyAsync(output, "new", hasBom: false);

        Assert.Equal("new", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(output)!, "*.tmp"));
    }

    [Fact]
    public async Task AtomicWrite_NewBomFile_HasExactlyOneBom()
    {
        var output = Path.Combine(_root, "new", "output.cs");
        await SourceTextIo.WriteAtomicallyAsync(output, "class C {}", hasBom: true);
        byte[] expected = [.. SourceTextIo.Utf8Bom, .. Encoding.UTF8.GetBytes("class C {}")];
        var actual = await File.ReadAllBytesAsync(output);
        Assert.Equal(expected, actual);
    }

    private string WriteBytes(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
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
