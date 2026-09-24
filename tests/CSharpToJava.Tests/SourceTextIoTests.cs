using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.Tests;

public class SourceTextIoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs2j-strio-" + Guid.NewGuid().ToString("N"));

    private string WriteBytes(byte[] bytes)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, Path.GetRandomFileName() + ".cs");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task BomFile_ReadStripsBom_AndWriteRestoresIt()
    {
        var path = WriteBytes([0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("class C { }")]);
        var text = await SourceTextIo.ReadAsync(path);
        Assert.True(text.HasBom);
        Assert.Equal("class C { }", text.Code);

        var outPath = Path.Combine(_dir, "out-bom.cs");
        await SourceTextIo.WriteAsync(outPath, text.Code, text.HasBom);
        byte[] expected = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("class C { }")];
        Assert.Equal(expected, await File.ReadAllBytesAsync(outPath));
    }

    [Fact]
    public async Task NoBomFile_RoundTripsWithoutBom()
    {
        var path = WriteBytes(System.Text.Encoding.UTF8.GetBytes("class C { }"));
        var text = await SourceTextIo.ReadAsync(path);
        Assert.False(text.HasBom);
        Assert.Equal("class C { }", text.Code);

        var outPath = Path.Combine(_dir, "out-nobom.cs");
        await SourceTextIo.WriteAsync(outPath, text.Code, text.HasBom);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("class C { }"), await File.ReadAllBytesAsync(outPath));
    }

    [Fact]
    public async Task NonAsciiContent_RoundTripsLosslessly()
    {
        var path = WriteBytes(System.Text.Encoding.UTF8.GetBytes("// 中文注释 \u4e2d\u6587\nclass C { }"));
        var text = await SourceTextIo.ReadAsync(path);
        var outPath = Path.Combine(_dir, "out-cjk.cs");
        await SourceTextIo.WriteAsync(outPath, text.Code, text.HasBom);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("// 中文注释 \u4e2d\u6587\nclass C { }"), await File.ReadAllBytesAsync(outPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
