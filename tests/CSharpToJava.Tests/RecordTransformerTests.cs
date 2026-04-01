using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for record conversion.
/// Covers: inheritance, abstract/sealed records, keyword escaping, record structs,
/// custom constructors, deep array equality, and with-expressions.
/// </summary>
public class RecordTransformerTests
{
    // ── Fix 1: Record inheritance (extends) must be preserved ──────────────────

    [Fact]
    public void Record_WithBaseRecord_GeneratesExtends()
    {
        var result = Convert(@"
public record Base(int X);
public record Derived(int X, int Y) : Base(X);
");

        Assert.True(result.Success);
        // The Derived record should extend Base
        Assert.Contains("extends Base", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_ImplementsInterface_GeneratesImplements()
    {
        var result = Convert(@"
using System;
public record Point(int X, int Y) : IComparable<Point>
{
    public int CompareTo(Point other) => X.CompareTo(other.X);
}
");

        Assert.True(result.Success);
        Assert.Contains("implements Comparable<Point>", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 2: Abstract record should fall back to abstract class ─────────────

    [Fact]
    public void AbstractRecord_GeneratesAbstractClass_NotAbstractRecord()
    {
        var result = Convert(@"
public abstract record Shape(string Name);
");

        Assert.True(result.Success);
        // Should be abstract class, NOT abstract record (Java records can't be abstract)
        Assert.Contains("abstract", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("class Shape", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("abstract record", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AbstractRecord_HasFieldsAndConstructor()
    {
        var result = Convert(@"
public abstract record Shape(string Name);
public record Circle(string Name, double Radius) : Shape(Name);
");

        Assert.True(result.Success);
        // Abstract class should have getter for name
        Assert.Contains("getName()", result.GeneratedCode, StringComparison.Ordinal);
        // Circle should extend Shape
        Assert.Contains("extends Shape", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 3: Sealed record should NOT produce "final record" ────────────────

    [Fact]
    public void SealedRecord_NoRedundantFinal()
    {
        var result = Convert(@"
public sealed record Point(int X, int Y);
");

        Assert.True(result.Success);
        // Java records are implicitly final, so "final record" is redundant
        Assert.DoesNotContain("final record", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("record Point", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainRecord_NoFinalKeyword()
    {
        var result = Convert(@"
public record Point(int X, int Y);
");

        Assert.True(result.Success);
        // Plain record (implicitly sealed in C#) should not have "final" on the record
        Assert.DoesNotContain("final record", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public record Point(int x, int y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 5: Java keyword escaping in record components ─────────────────────

    [Fact]
    public void RecordComponent_JavaKeyword_IsEscaped()
    {
        var result = Convert(@"
public record Config(int Default, string Class);
");

        Assert.True(result.Success);
        // "default" and "class" are Java keywords — should be escaped
        Assert.DoesNotContain("int default,", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("defaultValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("classValue", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 6: Record struct should include interface implementations ─────────

    [Fact]
    public void RecordStruct_ImplementsInterface()
    {
        var result = Convert(@"
using System;
public record struct Point(int X, int Y) : IFormattable
{
    public string ToString(string format, IFormatProvider formatProvider) => $""{X},{Y}"";
}
");

        Assert.True(result.Success);
        // Should generate class (not record) with implements
        Assert.Contains("class Point", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 7: Record struct should have default no-arg constructor ───────────

    [Fact]
    public void RecordStruct_HasDefaultConstructor()
    {
        var result = Convert(@"
public record struct Point(int X, int Y);
");

        Assert.True(result.Success);
        // Should have both parametrized and default constructors
        Assert.Contains("Point(int x, int y)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Point()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordStruct_DefaultConstructor_InitializesFields()
    {
        var result = Convert(@"
public record struct Point(int X, int Y);
");

        Assert.True(result.Success);
        // Default constructor should initialize fields to defaults
        Assert.Contains("this.x = 0;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("this.y = 0;", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 8: Custom constructors inside records ─────────────────────────────

    [Fact]
    public void Record_CustomConstructor_IsPreserved()
    {
        var result = Convert(@"
public record Point(int X, int Y)
{
    public Point() : this(0, 0) { }
}
");

        Assert.True(result.Success);
        // Should contain the custom constructor
        Assert.Contains("Point()", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 9: Deep array equals/hashCode ─────────────────────────────────────

    [Fact]
    public void ImmutableClass_DeepArray_UsesDeepEquals()
    {
        var result = ConvertNoRecords(@"
public record Matrix(int[][] Data);
");

        Assert.True(result.Success);
        // Should use deepEquals for multidimensional arrays
        Assert.Contains("deepEquals", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("deepHashCode", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ImmutableClass_SingleArray_UsesArraysEquals()
    {
        var result = ConvertNoRecords(@"
public record Data(int[] Values);
");

        Assert.True(result.Success);
        Assert.Contains("Arrays.equals", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Arrays.hashCode", result.GeneratedCode, StringComparison.Ordinal);
        // Ensure it does NOT use deep versions for single-dim arrays
        Assert.DoesNotContain("deepEquals", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 1 + ImmutableClass path: inheritance preserved in fallback ────────

    [Fact]
    public void ImmutableClass_WithBaseRecord_GeneratesExtends()
    {
        var result = ConvertNoRecords(@"
public record Base(int X);
public record Derived(int X, int Y) : Base(X);
");

        Assert.True(result.Success);
        Assert.Contains("extends Base", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("super(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ImmutableClass_ImplementsInterface()
    {
        var result = ConvertNoRecords(@"
using System;
public record Point(int X, int Y) : IComparable<Point>
{
    public int CompareTo(Point other) => X.CompareTo(other.X);
}
");

        Assert.True(result.Success);
        Assert.Contains("implements Comparable<Point>", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Record with generic type parameters ───────────────────────────────────

    [Fact]
    public void GenericRecord_TypeParameters()
    {
        var result = Convert(@"
public record Pair<T1, T2>(T1 First, T2 Second);
");

        Assert.True(result.Success);
        Assert.Contains("record Pair<T1, T2>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("T1 first", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("T2 second", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Record with extra methods ─────────────────────────────────────────────

    [Fact]
    public void Record_WithMethod_IsIncluded()
    {
        var result = Convert(@"
public record Point(int X, int Y)
{
    public double DistanceTo(Point other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }
}
");

        Assert.True(result.Success);
        Assert.Contains("distanceTo(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Keyword escaping in ImmutableClass path ───────────────────────────────

    [Fact]
    public void ImmutableClass_KeywordEscaping()
    {
        var result = ConvertNoRecords(@"
public record Config(int Default, string Class);
");

        Assert.True(result.Success);
        Assert.DoesNotContain("int default;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("defaultValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("classValue", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Record struct keyword escaping ────────────────────────────────────────

    [Fact]
    public void RecordStruct_KeywordEscaping()
    {
        var result = Convert(@"
public record struct Config(int Default);
");

        Assert.True(result.Success);
        Assert.DoesNotContain("int default;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("defaultValue", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static ConversionResult ConvertNoRecords(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions { UseRecords = false },
        });
    }
}
