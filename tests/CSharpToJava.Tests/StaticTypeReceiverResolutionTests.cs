using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StaticTypeReceiverResolutionTests
{

    [Fact]
    public void ConversionPipeline_ResolvesRelativeNamespaceStaticMembers_ThroughTypeAlias()
    {
        var result = Convert(@"
namespace Microsoft.Msagl.Core.Geometry
{
    public static class Compass
    {
        public static readonly int South = 1;
        public static int West() => 3;
    }
}

namespace Microsoft.Msagl.Layout
{
    using CompassAlias = Core.Geometry.Compass;

    public class Sample
    {
        public int Measure()
        {
            var south = CompassAlias.South;
            return CompassAlias.West() + south;
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("var south = Compass.South;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Compass.west() + south;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CompassAlias.South", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CompassAlias.west()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_ResolvesRelativeNamespaceStaticMembers_OnNestedTypes()
    {
        var result = Convert(@"
namespace Microsoft.Msagl.Core.Geometry
{
    public class Outer
    {
        public static class Compass
        {
            public static readonly int South = 1;
            public static int East => 2;
            public static int West() => 3;
        }
    }
}

namespace Microsoft.Msagl.Layout
{
    public class Sample
    {
        public int Measure()
        {
            var south = Core.Geometry.Outer.Compass.South;
            var east = Core.Geometry.Outer.Compass.East;
            return Core.Geometry.Outer.Compass.West() + south + east;
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("var south = Outer.Compass.South;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("var east = Outer.Compass.getEast();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Outer.Compass.west() + south + east;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Geometry.Outer.Compass.South", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Geometry.Outer.Compass.getEast()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Geometry.Outer.Compass.west()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_ResolvesRelativeNamespaceStaticMethodGroups_ToJavaMethodReferences()
    {
        var result = Convert(@"
namespace Microsoft.Msagl.Core.Geometry
{
    public static class Compass
    {
        public static int West() => 3;
    }
}

namespace Microsoft.Msagl.Layout
{
    public class Sample
    {
        public System.Func<int> BuildGetter()
        {
            return Core.Geometry.Compass.West;
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("return Compass::west;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return Core.Geometry.Compass::west;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_DoesNotRewriteFormatString_ForCustomStringTypes()
    {
        var result = Convert(@"
namespace Microsoft.Msagl.Text
{
    public static class String
    {
        public static string Format(string pattern, object value) => pattern + value;
    }
}

namespace Microsoft.Msagl.Layout
{
    public class Sample
    {
        public string Build(string joined)
        {
            return Text.String.Format(""{0}"", joined);
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("String.format(\"{0}\", joined)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.format(\"%s\", joined)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
