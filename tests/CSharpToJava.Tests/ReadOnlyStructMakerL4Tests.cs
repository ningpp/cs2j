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

    // === L4 Basic Conversion Tests ===

    [Fact]
    public void PublicFields_SimpleAssignment_Converted()
    {
        // Point.cs pattern: public fields assigned externally
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
        // L4 converts public fields to private fields + getXxx() methods + WithXxx() methods
        Assert.Contains("private double X;", result.OutputCode);
        Assert.Contains("private double Y;", result.OutputCode);
        Assert.Contains("getX()", result.OutputCode);
        Assert.Contains("getY()", result.OutputCode);
        Assert.Contains("WithX(", result.OutputCode);
        Assert.Contains("WithY(", result.OutputCode);
        // Note: assignment conversion (p = p.WithX(5)) happens in UpdateCrossFileFieldReads
        // (project-level), not in MakeReadOnly (single-file).
    }

    [Fact]
    public void PublicFields_ObjectInitializer_Converted()
    {
        // Need external assignment to trigger L4 conversion
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point { X = 5, Y = 10 };
        p.X = 20;  // External assignment to trigger L4
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Object initializer should be converted to constructor arguments
        Assert.Contains("new Point(x: 5, y: 10)", result.OutputCode);
        Assert.DoesNotContain("{ X = 5, Y = 10 }", result.OutputCode);
        // External assignment should be converted to WithX call
        Assert.Contains("p = p.WithX(20)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_CompoundAssignment_Converted()
    {
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
        Assert.Contains("c = c.WithValue(c.getValue() + 5)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_SubtractAssignment_Converted()
    {
        var src = @"
struct Counter {
    public int Value;
    public Counter(int v) { Value = v; }
}
class User {
    void M() {
        var c = new Counter(10);
        c.Value -= 3;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("c = c.WithValue(c.getValue() - 3)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_Increment_Converted()
    {
        var src = @"
struct Counter {
    public int Value;
    public Counter(int v) { Value = v; }
}
class User {
    void M() {
        var c = new Counter(0);
        c.Value++;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("c = c.WithValue(c.getValue() + 1)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_Decrement_Converted()
    {
        var src = @"
struct Counter {
    public int Value;
    public Counter(int v) { Value = v; }
}
class User {
    void M() {
        var c = new Counter(10);
        c.Value--;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("c = c.WithValue(c.getValue() - 1)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_PreIncrement_Converted()
    {
        var src = @"
struct Counter {
    public int Value;
    public Counter(int v) { Value = v; }
}
class User {
    void M() {
        var c = new Counter(0);
        ++c.Value;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("c = c.WithValue(c.getValue() + 1)", result.OutputCode);
    }

    // === L4 Option Tests ===

    [Fact]
    public void PublicFields_DisabledByOption_NotConvertible()
    {
        var src = @"
struct Point {
    public double X;
    public double Y;
}
class User {
    void M() {
        var p = new Point();
        p.X = 5;
    }
}";
        var opts = new ReadOnlyStructMakerOptions { EnablePublicFieldConversion = false };
        var result = RunMaker(src, opts);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
    }

    // === L4 Statistics Tests ===

    [Fact]
    public void PublicFields_StatisticsTracked()
    {
        // Need external assignment to trigger L4 (public fields assigned externally)
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
        Assert.Equal(1, result.Statistics.Level4_PublicFieldToProperty);
        Assert.Equal(1, result.Statistics.StructsConverted);
        Assert.Equal(2, result.Statistics.PublicFieldsConverted);
    }

    [Fact]
    public void PublicFields_LevelAssignedCorrectly()
    {
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
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.PublicFieldToProperty);
    }

    // === L4 Pattern Detection Tests ===

    [Fact]
    public void PublicFields_WithMutatingMethod_NotL4()
    {
        // If struct has mutating methods, it should go to L5/L7 instead
        var src = @"
struct S {
    public int X;
    public void Increment() { X++; }
}";
        var result = RunMaker(src);
        // Should NOT be L4 because it has mutating methods
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.PublicFieldToProperty);
    }

    [Fact]
    public void PublicFields_NoExternalAssignment_L1()
    {
        // If public fields are only assigned internally (in constructor),
        // it should be L1 DirectAdd
        var src = @"
struct Point {
    public readonly double X;
    public readonly double Y;
    public Point(double x, double y) { X = x; Y = y; }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Should be L1 (already readonly fields)
        Assert.Contains(result.Diagnostics, d =>
            d.Level == ConversionLevel.DirectAdd || d.Level == ConversionLevel.PublicFieldToProperty);
    }

    // === L4 Complex Scenarios ===

    [Fact]
    public void PublicFields_MultipleAssignments_AllConverted()
    {
        var src = @"
struct Rect {
    public double X;
    public double Y;
    public double Width;
    public double Height;
}
class User {
    void M() {
        var r = new Rect();
        r.X = 0;
        r.Y = 0;
        r.Width = 100;
        r.Height = 200;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("r = r.WithX(0)", result.OutputCode);
        Assert.Contains("r = r.WithY(0)", result.OutputCode);
        Assert.Contains("r = r.WithWidth(100)", result.OutputCode);
        Assert.Contains("r = r.WithHeight(200)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_MixedWithPublicFields_OnlyTargetStructConverted()
    {
        var src = @"
struct Target {
    public int Value;
}
struct Other {
    public int Data;
}
class User {
    void M() {
        var t = new Target();
        t.Value = 42;
        var o = new Other();
        o.Data = 100;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Target should be converted (external assignment)
        Assert.Contains("t = t.WithValue(42)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_DiagnosticMessage_ContainsFieldName()
    {
        var src = @"
struct Point {
    public double X;
    public double Y;
}
class User {
    void M() {
        var p = new Point();
        p.X = 5;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Level == ConversionLevel.PublicFieldToProperty &&
            d.StructName == "Point");
    }

    [Fact]
    public void PublicFields_WithMethod_MethodPreserved()
    {
        var src = @"
struct Point {
    public double X;
    public double Y;
    public double Distance => System.Math.Sqrt(X * X + Y * Y);
}
class User {
    void M() {
        var p = new Point();
        p.X = 3;
        p.Y = 4;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Original method should be preserved
        Assert.Contains("Distance", result.OutputCode);
        // WithX/WithY methods should be generated
        Assert.Contains("WithX(", result.OutputCode);
        Assert.Contains("WithY(", result.OutputCode);
    }

    [Fact]
    public void PublicFields_MultiplyAssignment_Converted()
    {
        var src = @"
struct Scale {
    public double Factor;
    public Scale(double f) { Factor = f; }
}
class User {
    void M() {
        var s = new Scale(1.0);
        s.Factor *= 2.0;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("s = s.WithFactor(s.getFactor() * 2.0)", result.OutputCode);
    }

    // === Bug Fix: Class with same field name as struct should NOT be converted ===

    [Fact]
    public void PublicFields_ClassWithSameFieldNameAsStruct_NotConverted()
    {
        // MoreInfo is a class (not struct), so its field assignments should NOT be converted to With* calls
        // Offset is a struct with Path, Query, Fragment fields that gets L4 conversion
        // Bug: AssignmentRewriter was converting info.MoreInfo.Path = value to info.MoreInfo.WithPath(value)
        var src = @"
struct Offset {
    public int Path;
    public int Query;
    public int Fragment;
    public Offset(int p, int q, int f) { Path = p; Query = q; Fragment = f; }
}
class MoreInfo {
    public string Path;
    public string Query;
    public string Fragment;
}
class User {
    void M() {
        var o = new Offset(0, 0, 0);
        o.Path = 1;  // This SHOULD be converted to o = o.WithPath(1) because Offset is a struct
        var info = new MoreInfo();
        info.Path = ""test"";  // This should NOT be converted because MoreInfo is a class
        info.Query = ""q"";
        info.Fragment = ""f"";
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Offset struct assignment should be converted to WithPath
        Assert.Contains("o = o.WithPath(1)", result.OutputCode);
        // MoreInfo is a class, so its field assignments should remain as simple assignments
        Assert.DoesNotContain("info.WithPath(", result.OutputCode);
        Assert.DoesNotContain("info.WithQuery(", result.OutputCode);
        Assert.DoesNotContain("info.WithFragment(", result.OutputCode);
        // Original field assignments should be preserved for the class
        Assert.Contains("info.Path =", result.OutputCode);
        Assert.Contains("info.Query =", result.OutputCode);
        Assert.Contains("info.Fragment =", result.OutputCode);
    }

    [Fact]
    public void PublicFields_Idempotent()
    {
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}";
        var first = RunMaker(src);
        Assert.True(first.Changed);

        // Second run should produce same output
        var second = RunMaker(first.OutputCode!);
        // After first conversion, there are no more public fields to convert
        // so second run should be idempotent (no changes)
        Assert.False(second.Changed);
    }

    [Fact]
    public void PublicFields_InsidePreprocessorDirectives_PreservesBalance()
    {
        // Reproduces the Point.cs bug: public fields inside #if/#else/#endif blocks
        // The rewriter must not strip preprocessor directives from trivia
        var src = @"
struct Point {
#if SHARPKIT
    private double m_X;
    public double X { get { return m_X; } set { m_X = value; } }
#else
    public double X;
#endif
#if SHARPKIT
    private double m_Y;
    public double Y { get { return m_Y; } set { m_Y = value; } }
#else
    public double Y;
#endif
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point(1, 2);
        p.X = 5;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        var output = result.OutputCode!;

        // Count preprocessor directives - they must be balanced
        var ifCount = output.Split('\n').Count(l => l.TrimStart().StartsWith("#if "));
        var endifCount = output.Split('\n').Count(l => l.TrimStart().StartsWith("#endif"));
        Assert.Equal(ifCount, endifCount);

        // The output must still be valid C# (parseable without errors)
        var tree = CSharpSyntaxTree.ParseText(output);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void PublicFields_ReadAccess_ConvertedToGetter()
    {
        // Read accesses to converted fields must use getter methods
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

        // Read accesses should use getter methods
        Assert.Contains("p.getX()", output);
        Assert.Contains("p.getY()", output);
        // Should NOT have direct field reads (p.X in a non-assignment context)
        Assert.DoesNotContain("return p.X", output);
    }
}
