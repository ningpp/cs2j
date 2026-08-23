using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class ClassHashCodeAutoGenerationTests
{
    private static ConversionResult Convert(string src, string file = "Sample.cs")
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = file,
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void ClassWithEqualsOverrideButNoGetHashCode_GeneratesHashCode()
    {
        var result = Convert(@"
namespace Foo {
    public class Seg {
        public int[] Points;
        public int I;
        public Seg(int[] pts, int i) { Points = pts; I = i; }
        public override bool Equals(object obj) {
            var other = (Seg)obj;
            return other.Points == Points && other.I == I;
        }
    }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        // Verify equals is preserved
        Assert.Contains("public boolean equals(Object obj)", result.GeneratedCode);
        // Verify hashCode is auto-generated
        Assert.Contains("public int hashCode()", result.GeneratedCode);
        Assert.Contains("Objects.hash(Points, I)", result.GeneratedCode);
    }

    [Fact]
    public void ClassWithEqualsAndGetHashCode_DoesNotGenerateExtraHashCode()
    {
        var result = Convert(@"
namespace Foo {
    public class Seg {
        public int I;
        public override bool Equals(object obj) {
            var other = (Seg)obj;
            return other.I == I;
        }
        public override int GetHashCode() { return I.GetHashCode(); }
    }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        // Verify only one hashCode method exists (the user-defined one)
        var hashCodeCount = System.Text.RegularExpressions.Regex.Matches(result.GeneratedCode, @"int\s+hashCode\s*\(\s*\)").Count;
        Assert.Equal(1, hashCodeCount);
    }

    [Fact]
    public void ClassWithoutEqualsOverride_DoesNotGenerateHashCode()
    {
        var result = Convert(@"
namespace Foo {
    public class Plain {
        public int X;
    }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain("hashCode()", result.GeneratedCode);
    }
}
