using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class UnsignedMathSupportFilesTests
{
    [Fact]
    public void GetUnsignedMathFileContent_ReturnsPublicTrueSource()
    {
        var content = UnsignedMathSupportFiles.GetUnsignedMathFileContent();
        Assert.Contains("namespace Cs2jUnsigned", content);
        Assert.Contains("public static class UnsignedMath", content);
        Assert.DoesNotContain("internal static class UnsignedMath", content);
        Assert.Contains("public static int AddUInt32(int a, int b)", content);
    }

    [Fact]
    public void ToUInt64CheckedInt32_MatchesEmitterExpectation()
    {
        // The numeric-boundary C15 conversion matrix emits this helper for
        // checked int -> ulong conversions (source domain Int32).
        Assert.Equal(0UL, Cs2jUnsigned.UnsignedMath.ToUInt64CheckedInt32(0));
        Assert.Equal((ulong)int.MaxValue, Cs2jUnsigned.UnsignedMath.ToUInt64CheckedInt32(int.MaxValue));
        Assert.Throws<OverflowException>(() => Cs2jUnsigned.UnsignedMath.ToUInt64CheckedInt32(-1));
    }

    [Fact]
    public async Task ReleaseAsync_WritesProjectAndSourceFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2j-unsigned-support-" + Guid.NewGuid().ToString("N"));
        try
        {
            await UnsignedMathSupportFiles.ReleaseAsync(dir);
            Assert.True(File.Exists(Path.Combine(dir, "Cs2jUnsignedMath.csproj")));
            var csproj = await File.ReadAllTextAsync(Path.Combine(dir, "Cs2jUnsignedMath.csproj"));
            var source = await File.ReadAllTextAsync(Path.Combine(dir, "UnsignedMath.cs"));
            Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csproj);
            Assert.Contains("<LangVersion>11</LangVersion>", csproj);
            Assert.Contains("public static class UnsignedMath", source);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
