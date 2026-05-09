using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StaticTypeReceiverResolutionTests
{
    [Fact]
    public void ConversionPipeline_ResolvesRelativeNamespaceStaticMembers_AcrossFieldPropertyAndMethod()
    {
        var result = Convert(@"
namespace Microsoft.Msagl.Core.Geometry
{
    public static class Compass
    {
        public static readonly int South = 1;
        public static int East => 2;
        public static int West() => 3;
    }
}

namespace Microsoft.Msagl.Layout
{
    public class Sample
    {
        public int Measure()
        {
            var south = Core.Geometry.Compass.South;
            var east = Core.Geometry.Compass.East;
            return Core.Geometry.Compass.West() + south + east;
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("var south = Compass.South;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("var east = Compass.getEast();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Compass.west() + south + east;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Geometry.Compass.South", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Geometry.Compass.getEast()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Geometry.Compass.west()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_MapsRelativeNamespaceFrameworkStatics_UsingSemanticReceiverTypes()
    {
        var result = Convert(@"
namespace Microsoft.Msagl.System.IO
{
    public static class Path
    {
        public static string Combine(string left, string right) => left + right;
    }
}

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
        public string Build(string left, string right)
        {
            var joined = System.IO.Path.Combine(left, right);
            return Text.String.Format(""{0}"", joined);
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("var joined = java.nio.file.Paths.get(left, right).toString();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return String.format(\"{0}\", joined);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO.Path.Combine", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Text.String.Format", result.GeneratedCode, StringComparison.Ordinal);
    }

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
    public void ConversionPipeline_RewritesStringFormatPlaceholders_ToJavaFormatSpecifiers()
    {
        var result = Convert(@"
public class Sample
{
    public string Build(string x, int n, double d)
    {
        var s1 = string.Format(""{0} {1}"", x, n);
        var s2 = String.Format(""{0:D4}"", n);
        var s3 = string.Format(""{0:F2}"", d);
        var s4 = string.Format(""{0}% done"", n);
        return s1;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("String.format(\"%s %s\", x, n)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("String.format(\"%04d\", n)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("String.format(\"%.2f\", d)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("String.format(\"%s%% done\", n)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_RewritesStringBuilderAppendFormat_ToJavaFormatSpecifiers()
    {
        var result = Convert(@"
using System.Text;

public class Sample
{
    public void Build(string x, int n)
    {
        var sb = new StringBuilder();
        sb.AppendFormat(""{0} {1}"", x, n);
        sb.AppendFormat(""{0:D4}"", n);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("sb.append(String.format(\"%s %s\", x, n))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("sb.append(String.format(\"%04d\", n))", result.GeneratedCode, StringComparison.Ordinal);
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
