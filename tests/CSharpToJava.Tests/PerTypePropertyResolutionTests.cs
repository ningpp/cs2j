using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that property access is resolved per-type via GetMembers(),
/// not via a global whitelist. Verifies the fix for property names that
/// are fields on some types but properties on others.
/// </summary>
public class PerTypePropertyResolutionTests
{
    /// <summary>
    /// Property on an interface accessed through interface-typed variable
    /// must generate a getter call. This is the most common failure pattern.
    /// </summary>
    [Fact]
    public void InterfaceProperty_GeneratesGetter()
    {
        var result = Convert(@"
interface IShape {
    Point Start { get; set; }
    Point End { get; set; }
}
class Point { public double X { get; set; } public double Y { get; set; } }
class Test {
    void M(IShape shape) {
        var s = shape.Start;
        var e = shape.End;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("shape.getStart()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("shape.getEnd()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("shape.Start", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("shape.End", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same property name that is a field on a different type must still work
    /// correctly per-type. "Nodes" is a property on Cluster but a field on
    /// some other MSAGL types.
    /// </summary>
    [Fact]
    public void SameName_PropertyOnOneType_FieldOnAnother()
    {
        var result = Convert(@"
class Container {
    public int Nodes;  // field
}
class Cluster {
    public System.Collections.Generic.List<string> Nodes { get; set; }  // property
}
class Test {
    void M(Container c, Cluster cl) {
        var n1 = c.Nodes;   // field access
        var n2 = cl.Nodes;  // property → getter
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("c.Nodes", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("cl.getNodes()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property defined in a base class must be found via base type walking.
    /// </summary>
    [Fact]
    public void BaseClassProperty_GeneratesGetter()
    {
        var result = Convert(@"
class Base {
    public string Label { get; set; }
}
class Derived : Base {
    public int Extra { get; set; }
}
class Test {
    void M(Derived d) {
        var l = d.Label;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("d.getLabel()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property defined in an implemented interface must be found via interface walking.
    /// </summary>
    [Fact]
    public void InterfaceProperty_OnImplementingClass_GeneratesGetter()
    {
        var result = Convert(@"
interface IHasId {
    int Id { get; set; }
}
class Entity : IHasId {
    public int Id { get; set; }
}
class Test {
    void M(Entity e) {
        var id = e.Id;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("e.getId()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Chained property access (a.B.C) must resolve correctly.
    /// </summary>
    [Fact]
    public void ChainedPropertyAccess_GeneratesChainedGetters()
    {
        var result = Convert(@"
class Inner {
    public int Value { get; set; }
}
class Outer {
    public Inner Child { get; set; }
}
class Test {
    void M(Outer o) {
        var v = o.Child.Value;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("o.getChild().getValue()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property names that were removed from the whitelist in commit 70bfbc5
    /// must still generate getters when they are properties on the receiver type.
    /// </summary>
    [Fact]
    public void RemovedWhitelistNames_StillGenerateGetters()
    {
        var result = Convert(@"
class Shape {
    public double Width { get; set; }
    public double Height { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Radius { get; set; }
}
class Edge {
    public Shape SourcePoint { get; set; }
    public Shape TargetPoint { get; set; }
}
class Test {
    void M(Shape s, Edge e) {
        var w = s.Width;
        var h = s.Height;
        var l = s.Left;
        var t = s.Top;
        var r = s.Radius;
        var sp = e.SourcePoint;
        var tp = e.TargetPoint;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("s.getWidth()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.getHeight()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.getLeft()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.getTop()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.getRadius()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("e.getSourcePoint()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("e.getTargetPoint()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Auto-properties that are emitted as public fields in project pipeline
    /// must NOT get getters.
    /// </summary>
    [Fact]
    public void AutoProperty_EmittedAsField_AccessedAsField()
    {
        var result = Convert(@"
class Node {
    public object AlgorithmData { get; set; }
}
class Test {
    void M(Node n) {
        var data = n.AlgorithmData;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("n.AlgorithmData", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("n.getAlgorithmData()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// System type property access (e.g. Count → size()) must still work.
    /// </summary>
    [Fact]
    public void SystemTypeProperty_CountMapsToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(List<int> items) {
        var c = items.Count;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("items.size()", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
