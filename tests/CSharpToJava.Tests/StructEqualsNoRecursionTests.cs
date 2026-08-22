using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class StructEqualsNoRecursionTests
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
    public void OperatorEqualsDelegatingToStructEquals_DoesNotRecurse()
    {
        var result = Convert(@"
namespace Foo {
    public struct Box {
        public double Left;
        public double Right;
        public static bool operator ==(Box a, Box b) { return a.Equals(b); }
        public static bool operator !=(Box a, Box b) { return !a.Equals(b); }
    }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("Objects.equals(Left, other.Left)", result.GeneratedCode);
        Assert.DoesNotContain("Box.valueEquals(this, other)", result.GeneratedCode);
    }

    [Fact]
    public void ExplicitEqualsOverride_StillDelegatesToValueEquals()
    {
        var result = Convert(@"
namespace Foo {
    public struct Box {
        public double Left;
        public override bool Equals(object obj) {
            if (obj is Box other) return Left == other.Left;
            return false;
        }
        public static bool operator ==(Box a, Box b) { return a.Equals(b); }
        public static bool operator !=(Box a, Box b) { return !a.Equals(b); }
    }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("return Left == other.Left;", result.GeneratedCode);
        Assert.DoesNotContain("Box.valueEquals(this, other)", result.GeneratedCode);
    }
}
