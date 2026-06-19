using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that object initializer extraction inside a ternary expression
/// does not move side-effectful calls outside the guarded true-branch.
///
/// Bug: C# code like
///   str != null ? new Arrowhead { TipPosition = ParsePoint(str) } : null
/// was converted to Java that calls parsePoint(str) unconditionally:
///   var _obj = new Arrowhead();
///   _obj.setTipPosition(parsePoint(str));   // runs even when str == null!
///   var arrowhead = (str != null ? _obj : null);
///
/// The correct conversion must keep parsePoint(str) inside the ternary's
/// true-branch so it is only evaluated when str != null.
/// </summary>
public class TernaryObjectInitializerNullGuardTests
{
    [Fact]
    public void Ternary_ObjectInitializer_WithNullGuard_ShouldNotHoistMethodCall()
    {
        var result = Convert("""
using System;

class Arrowhead
{
    public Point TipPosition { get; set; }
}

class Point { public double X; public double Y; }

class Edge
{
    public EdgeGeometry EdgeGeometry { get; set; }
}

class EdgeGeometry
{
    public Arrowhead SourceArrowhead { get; set; }
}

class TestClass
{
    private static Point ParsePoint(string s)
    {
        var parts = s.Split(',');
        return new Point { X = double.Parse(parts[0]), Y = double.Parse(parts[1]) };
    }

    public void M(Edge edge)
    {
        string str = null;
        var arrowhead = str != null ? new Arrowhead { TipPosition = ParsePoint(str) } : null;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;

        // The ternary condition must still be present
        Assert.Contains("str != null", code, StringComparison.Ordinal);

        // BUG: The object initializer is hoisted out of the ternary, causing
        // parsePoint(str) to be called unconditionally even when str is null.
        // The generated code looks like:
        //   var _obj1 = new Arrowhead();
        //   _obj1.setTipPosition(parsePoint(str));  <-- called regardless of null check!
        //   var arrowhead = (str != null ? _obj1 : null);
        //
        // The setter with parsePoint(str) must NOT appear as a standalone statement
        // before the ternary expression.
        var lines = code.Split('\n').Select(l => l.Trim()).ToList();
        var ternaryLineIndex = lines.FindIndex(l => l.Contains("str != null"));
        var setterLineIndex = lines.FindIndex(l => l.Contains("setTipPosition(parsePoint(str))"));

        // The setter call with parsePoint(str) must not appear before the ternary
        Assert.True(setterLineIndex == -1 || setterLineIndex > ternaryLineIndex,
            $"setTipPosition(parsePoint(str)) at line {setterLineIndex} appears BEFORE " +
            $"the ternary null check at line {ternaryLineIndex}. " +
            $"It must be inside the true-branch, not hoisted.\nGenerated code:\n{code}");
    }

    [Fact]
    public void Ternary_ObjectInitializer_WithNullGuard_ChainedAssignment()
    {
        var result = Convert("""
using System;

class Arrowhead
{
    public Point TipPosition { get; set; }
}

class Point { public double X; public double Y; }

class Edge
{
    public EdgeGeometry EdgeGeometry { get; set; }
}

class EdgeGeometry
{
    public Arrowhead SourceArrowhead { get; set; }
}

class TestClass
{
    private static Point ParsePoint(string s)
    {
        var parts = s.Split(',');
        return new Point { X = double.Parse(parts[0]), Y = double.Parse(parts[1]) };
    }

    public void M(Edge edge)
    {
        string str = null;
        edge.EdgeGeometry.SourceArrowhead = str != null ? new Arrowhead { TipPosition = ParsePoint(str) } : null;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;
        Assert.Contains("str != null", code, StringComparison.Ordinal);

        // Same bug: setter hoisted before the ternary
        var lines = code.Split('\n').Select(l => l.Trim()).ToList();
        var ternaryLineIndex = lines.FindIndex(l => l.Contains("str != null"));
        var setterLineIndex = lines.FindIndex(l => l.Contains("setTipPosition(parsePoint(str))"));

        Assert.True(setterLineIndex == -1 || setterLineIndex > ternaryLineIndex,
            $"setTipPosition(parsePoint(str)) at line {setterLineIndex} appears BEFORE " +
            $"the ternary null check at line {ternaryLineIndex}. " +
            $"It must be inside the true-branch, not hoisted.\nGenerated code:\n{code}");
    }

    [Fact]
    public void Ternary_ObjectInitializer_WithNullGuard_SideEffectMethodCall()
    {
        var result = Convert("""
using System;

class Config
{
    public string Value { get; set; }
}

class TestClass
{
    private static string Transform(string input)
    {
        return input.ToUpper();
    }

    public void M()
    {
        string str = null;
        var cfg = str != null ? new Config { Value = Transform(str) } : null;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;
        Assert.Contains("str != null", code, StringComparison.Ordinal);

        // Transform(str) must not be called unconditionally
        var lines = code.Split('\n').Select(l => l.Trim()).ToList();
        var ternaryLineIndex = lines.FindIndex(l => l.Contains("str != null"));
        var setterLineIndex = lines.FindIndex(l => l.Contains("setValue(transform(str))"));

        Assert.True(setterLineIndex == -1 || setterLineIndex > ternaryLineIndex,
            $"setValue(transform(str)) at line {setterLineIndex} appears BEFORE " +
            $"the ternary null check at line {ternaryLineIndex}. " +
            $"It must be inside the true-branch, not hoisted.\nGenerated code:\n{code}");
    }

    [Fact]
    public void Ternary_ObjectInitializer_NestedNullGuard_MultipleProperties()
    {
        var result = Convert("""
using System;

class Arrowhead
{
    public Point TipPosition { get; set; }
    public string Style { get; set; }
}

class Point { public double X; public double Y; }

class TestClass
{
    private static Point ParsePoint(string s)
    {
        var parts = s.Split(',');
        return new Point { X = double.Parse(parts[0]), Y = double.Parse(parts[1]) };
    }

    public void M()
    {
        string str = null;
        var a = str != null ? new Arrowhead { TipPosition = ParsePoint(str), Style = "solid" } : null;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;
        Assert.Contains("str != null", code, StringComparison.Ordinal);

        // Neither property setter should be hoisted out of the ternary guard
        var lines = code.Split('\n').Select(l => l.Trim()).ToList();
        var ternaryLineIndex = lines.FindIndex(l => l.Contains("str != null"));
        var setterLineIndex = lines.FindIndex(l => l.Contains("setTipPosition(parsePoint(str))"));

        Assert.True(setterLineIndex == -1 || setterLineIndex > ternaryLineIndex,
            $"setTipPosition(parsePoint(str)) at line {setterLineIndex} appears BEFORE " +
            $"the ternary null check at line {ternaryLineIndex}. " +
            $"It must be inside the true-branch, not hoisted.\nGenerated code:\n{code}");
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
