using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class ReadOnlyStructMakerL4Tests
{
    private readonly ITestOutputHelper _output;

    public ReadOnlyStructMakerL4Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ReadOnlyStructMakerResult RunMaker(string source, ReadOnlyStructMakerOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Core.ReadOnlyStructMaker.ReadOnlyStructMaker()
            .MakeReadOnly(tree, compilation.GetSemanticModel(tree), options);
    }

    // === L4 V4: True Readonly Struct Tests ===

    [Fact]
    public void PublicFields_V4_AddsReadonlyModifier()
    {
        // V4: struct must have readonly modifier
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point();
        p.X = 5;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Point", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_ConvertsToGetOnlyProperty()
    {
        // V4: public fields become get-only properties, not private fields
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point();
        p.X = 5;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Should have get-only properties
        Assert.Contains("public double X { get; }", result.OutputCode);
        Assert.Contains("public double Y { get; }", result.OutputCode);
        // Should NOT have private fields
        Assert.DoesNotContain("private double X;", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_WithXxxUsesConstructor()
    {
        // V4: WithXxx methods use constructor, not field assignment
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point();
        p.X = 5;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // WithXxx should use constructor
        Assert.Contains("new Point(", result.OutputCode);
        Assert.Contains("WithX(", result.OutputCode);
        Assert.Contains("WithY(", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_ExternalAssignmentUpdated()
    {
        // V4: external assignment p.X = 5 becomes p = p.WithX(5)
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point();
        p.X = 5;
        p.Y = 10;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("p = p.WithX(5)", result.OutputCode);
        Assert.Contains("p = p.WithY(10)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_ReadAccess_PropertyAccess()
    {
        // V4: read accesses stay as property access, not getter methods
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    double M() {
        var p = new Point(1, 2);
        p.X = 5;
        return p.X + p.Y;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        var output = result.OutputCode!;
        // Read accesses should use property access (p.X, p.Y)
        Assert.Contains("p.X", output);
        Assert.Contains("p.Y", output);
        // Should NOT have getter methods (p.getX(), p.getY())
        Assert.DoesNotContain("p.getX()", output);
        Assert.DoesNotContain("p.getY()", output);
    }

    [Fact]
    public void PublicFields_V4_CompoundAssignment_PropertyAccess()
    {
        // V4: compound assignment uses property access for reads
        var src = @"
struct Counter {
    public int Value;
    public Counter(int v) { Value = v; }
}
class User {
    void M() {
        var c = new Counter(0);
        c.Value += 5;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Should use property access: c.Value + 5, not c.getValue() + 5
        Assert.Contains("c.Value + 5", result.OutputCode);
    }
}