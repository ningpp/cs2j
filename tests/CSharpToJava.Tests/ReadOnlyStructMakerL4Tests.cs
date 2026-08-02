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

    [Fact]
    public void PublicFields_V4_ObjectInitializer_Converted()
    {
        // V4: object initializer converted to constructor
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point { X = 5, Y = 10 };
        p.X = 20;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Object initializer should be converted to constructor arguments
        Assert.Contains("new Point(", result.OutputCode);
        Assert.DoesNotContain("{ X = 5", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_NoConstructor_GeneratesCtor()
    {
        // V4: when no constructor exists, generate one
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
        var output = result.OutputCode!;
        // Should generate constructor
        Assert.Contains("public Point(", output);
        // Should still have WithXxx methods
        Assert.Contains("WithX(", output);
        Assert.Contains("WithY(", output);
    }

    [Fact]
    public void PublicFields_V4_NonStandardCtorParams()
    {
        // V4: Point.cs style with non-standard parameter names (xCoordinate, yCoordinate)
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double xCoordinate, double yCoordinate) { X = xCoordinate; Y = yCoordinate; }
}
class User {
    void M() {
        var p = new Point();
        p.X = 5;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        var output = result.OutputCode!;
        
        // Debug: output the actual generated code
        _output.WriteLine("=== Generated Output ===");
        _output.WriteLine(output);
        _output.WriteLine("=== End Output ===");
        
        // WithXxx should use the original constructor parameter names
        Assert.Contains("WithX(double x)", output);
        // Constructor call should use xCoordinate parameter name
        Assert.Contains("xCoordinate:", output);
    }

    // === L4 V4: Statistics Tests ===

    [Fact]
    public void PublicFields_V4_StatisticsTracked()
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
        Assert.Equal(1, result.Statistics.Level4_PublicFieldToProperty);
        Assert.Equal(1, result.Statistics.StructsConverted);
        Assert.Equal(2, result.Statistics.PublicFieldsConverted);
    }

    [Fact]
    public void PublicFields_V4_LevelAssignedCorrectly()
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

    // === L4 V4: Option Tests ===

    [Fact]
    public void PublicFields_V4_DisabledByOption_NotConvertible()
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

    // === L4 V4: Pattern Detection Tests ===

    [Fact]
    public void PublicFields_V4_WithMutatingMethod_NotL4()
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
    public void PublicFields_V4_NoExternalAssignment_L1()
    {
        // If public fields are only assigned internally (constructor), should be L1
        var src = @"
struct Point {
    public readonly double X;
    public readonly double Y;
    public Point(double x, double y) { X = x; Y = y; }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Level == ConversionLevel.DirectAdd || d.Level == ConversionLevel.PublicFieldToProperty);
    }

    // === L4 V4: Complex Scenarios ===

    [Fact]
    public void PublicFields_V4_MultipleAssignments_AllConverted()
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
    public void PublicFields_V4_MixedWithPublicFields_OnlyTargetStructConverted()
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
    public void PublicFields_V4_DiagnosticMessage_ContainsFieldName()
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
    public void PublicFields_V4_WithMethod_MethodPreserved()
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

    // === L4 V4: Bug Fix Tests ===

    [Fact]
    public void PublicFields_V4_ClassWithSameFieldNameAsStruct_NotConverted()
    {
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
        o.Path = 1;
        var info = new MoreInfo();
        info.Path = ""test"";
        info.Query = ""q"";
        info.Fragment = ""f"";
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("o = o.WithPath(1)", result.OutputCode);
        // MoreInfo is a class, so its field assignments should remain
        Assert.DoesNotContain("info.WithPath(", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_Idempotent()
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
        Assert.False(second.Changed);
    }

    [Fact]
    public void PublicFields_V4_InsidePreprocessorDirectives_PreservesBalance()
    {
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
    public void PublicFields_V4_WithXxxBody_NotRewrittenByAssignmentRewriter()
    {
        // After L4 conversion, WithXxx methods use constructor.
        // Re-applying AssignmentRewriter should not break them.
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point(1, 2);
        p.X = 5;
    }
}";
        var firstResult = RunMaker(src);
        Assert.True(firstResult.Changed);
        var output = firstResult.OutputCode!;

        // Sanity: WithX should use constructor at this stage
        Assert.Contains("new Point(", output);

        // Re-apply AssignmentRewriter
        var outputTree = CSharpSyntaxTree.ParseText(output);
        var outputCompilation = CSharpCompilation.Create("field-read-update",
            new[] { outputTree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var outputModel = outputCompilation.GetSemanticModel(outputTree);

        var publicFieldStructs = new Dictionary<string, HashSet<string>>
        {
            ["Point"] = new HashSet<string> { "X", "Y" }
        };
        var propertyAccessStructs = new HashSet<string> { "Point" }; // L4 uses property access
        var rewriter = new AssignmentRewriter(publicFieldStructs, outputModel, propertyAccessStructs);
        rewriter.BuildVariableTypeMap(outputTree.GetRoot());
        var newRoot = rewriter.Visit(outputTree.GetRoot());
        var finalOutput = newRoot!.ToFullString();

        // WithX body should NOT be broken
        Assert.Contains("new Point(", finalOutput);
        Assert.DoesNotContain("result.WithX(", finalOutput);
    }

    // === L4 V4: Subtract/Increment/Decrement Tests ===

    [Fact]
    public void PublicFields_V4_SubtractAssignment_Converted()
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
        Assert.Contains("c = c.WithValue(c.Value - 3)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_Increment_Converted()
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
        Assert.Contains("c = c.WithValue(c.Value + 1)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_Decrement_Converted()
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
        Assert.Contains("c = c.WithValue(c.Value - 1)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_PreIncrement_Converted()
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
        Assert.Contains("c = c.WithValue(c.Value + 1)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_MultiplyAssignment_Converted()
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
        Assert.Contains("s = s.WithFactor(s.Factor * 2.0)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_V4_DivideAssignment_BinaryRightSide_Parenthesized()
    {
        var src = @"
struct Point {
    public double X;
    public Point(double x) { X = x; }
}
class User {
    void M(double ma, double mb) {
        var c = new Point(0);
        c.X /= 2.0 * (mb - ma);
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        var output = result.OutputCode!;
        // The right-hand side binary expression must be parenthesized
        Assert.Contains("c.X / (2.0 * (mb - ma))", output);
        Assert.DoesNotContain("c.X / 2.0 * (mb - ma)", output);
    }
}
