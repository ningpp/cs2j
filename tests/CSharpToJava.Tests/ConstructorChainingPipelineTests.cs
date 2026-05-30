using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ConstructorChainingPipelineTests
{
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

    [Fact]
    public void ThisInitializer_WithArgs_GeneratesThisCall()
    {
        var result = Convert(@"
public class ClusterDef
{
    public ClusterDef() : this(0.0, 0.0) { }
    public ClusterDef(double minSizeX, double minSizeY) { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("this(0.0, 0.0);", result.GeneratedCode);
    }

    [Fact]
    public void ThisInitializer_ZeroArgs_GeneratesThisCall()
    {
        var result = Convert(@"
public class Foo
{
    public Foo(int x) : this() { }
    public Foo() { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("this();", result.GeneratedCode);
    }

    [Fact]
    public void ThisInitializer_ZeroArgs_WithBody_BothEmitted()
    {
        var result = Convert(@"
public class Foo
{
    private int field;
    public Foo(int x) : this()
    {
        field = x;
    }
    public Foo() { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("this();", result.GeneratedCode);
        Assert.Contains("field = x;", result.GeneratedCode);
    }

    [Fact]
    public void BaseInitializer_WithArgs_GeneratesSuperCall()
    {
        var result = Convert(@"
public class Base
{
    public Base(int x) { }
}
public class Derived : Base
{
    public Derived() : base(42) { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("super(42);", result.GeneratedCode);
    }

    [Fact]
    public void BaseInitializer_ZeroArgs_NotEmitted()
    {
        var result = Convert(@"
public class Base
{
    public Base() { }
}
public class Derived : Base
{
    public Derived() : base() { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.DoesNotContain("super();", result.GeneratedCode);
    }

    [Fact]
    public void ClusterDef_FullScenario_AllChainsPreserved()
    {
        var result = Convert(@"
public class BorderInfo
{
    public BorderInfo(double v) { }
}
public class ClusterDef
{
    private static int nextClusterId = 0;

    internal ClusterDef()
        : this(0.0, 0.0)
    {
    }

    internal ClusterDef(double minSizeX, double minSizeY)
    {
    }

    internal ClusterDef(BorderInfo lbi, BorderInfo rbi,
                        BorderInfo tbi, BorderInfo bbi)
        : this(0.0, 0.0, lbi, rbi, tbi, bbi)
    {
    }

    internal ClusterDef(double minSizeX, double minSizeY,
                        BorderInfo lbi, BorderInfo rbi,
                        BorderInfo tbi, BorderInfo bbi)
        : this()
    {
    }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("this(0.0, 0.0);", result.GeneratedCode);
        Assert.Contains("this(0.0, 0.0, lbi, rbi, tbi, bbi);", result.GeneratedCode);
        Assert.Contains("this();", result.GeneratedCode);
    }

    [Fact]
    public void ThisInitializer_MultipleLevels_AllPreserved()
    {
        var result = Convert(@"
public class Multi
{
    public Multi() : this(1) { }
    public Multi(int a) : this(a, 2) { }
    public Multi(int a, int b) { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("this(1);", result.GeneratedCode);
        Assert.Contains("this(a, 2);", result.GeneratedCode);
    }

    [Fact]
    public void ThisInitializer_WithVariableArgs_GeneratesThisCall()
    {
        var result = Convert(@"
public class Rect
{
    public Rect() : this(10, 20) { }
    public Rect(int w, int h) { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("this(10, 20);", result.GeneratedCode);
    }

    [Fact]
    public void Constructor_NoInitializer_NoThisOrSuper()
    {
        var result = Convert(@"
public class Simple
{
    public Simple(int x) { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.DoesNotMatch(@"this\(\)", result.GeneratedCode);
    }

    [Fact]
    public void ThisInitializer_BackReference_ChainPreserved()
    {
        var result = Convert(@"
public class Config
{
    public Config(string name, int value) : this()
    {
    }
    public Config() { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("this();", result.GeneratedCode);
    }

    [Fact]
    public void ThisInitializer_WithMixedArgsAndNoArgs_BothPreserved()
    {
        var result = Convert(@"
public class Widget
{
    public Widget() : this(100) { }
    public Widget(int size) : this(size, ""default"") { }
    public Widget(int size, string label) { }
}
");

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains("this(100);", result.GeneratedCode);
        Assert.Contains("this(size, \"default\");", result.GeneratedCode);
    }
}
