using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for struct conversion improvements.
/// Covers: expression-bodied properties, default literal, generic clone,
/// indexer handling, and static field object initializer.
/// </summary>
public class StructTransformerTests
{
    // ── Fix 1: Expression-bodied property should NOT create a backing field ─────

    [Fact]
    public void ExpressionBodiedProperty_NoBackingField()
    {
        var result = Convert(@"
using System;
public struct Vector2
{
    public float X;
    public float Y;
    public float Length => (float)Math.Sqrt(X * X + Y * Y);
}");

        Assert.True(result.Success);
        // Should have getter method with the expression
        Assert.Contains("getLength()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Math.sqrt", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT create a backing field named 'length'
        Assert.DoesNotContain("private float length;", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 2: default literal for struct types ─────────────────────────────────

    [Fact]
    public void DefaultLiteral_StructType_GeneratesNewInstance()
    {
        var result = Convert(@"
public struct Point { public int X, Y; }
class Sample
{
    void M()
    {
        Point p = default;
    }
}");

        Assert.True(result.Success);
        // 'default' for struct should generate new Point()
        Assert.Contains("new Point()", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain TODO comment
        Assert.DoesNotContain("TODO", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultLiteral_PrimitiveType_GeneratesZeroValue()
    {
        var result = Convert(@"
class Sample
{
    void M()
    {
        int x = default;
        bool b = default;
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("TODO", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultExpression_StructType_StillWorks()
    {
        var result = Convert(@"
public struct Point { public int X, Y; }
class Sample
{
    void M()
    {
        Point p = default(Point);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("new Point()", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 3: Generic struct clone includes type parameters ────────────────────

    // ── Fix 4: Indexer handling in struct ────────────────────────────────────────

    [Fact]
    public void Struct_IndexerGeneratesGetSetMethods()
    {
        var result = Convert(@"
using System;
public struct FixedBuffer
{
    private int v0, v1;

    public int this[int index]
    {
        get => index == 0 ? v0 : v1;
        set
        {
            if (index == 0) v0 = value;
            else v1 = value;
        }
    }
}");

        Assert.True(result.Success);
        // Should generate get and set methods for indexer
        Assert.Contains("public int get(int index)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public int set(int index, int value)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 5: Static field with object initializer ─────────────────────────────

    [Fact]
    public void StaticFieldWithObjectInitializer_UsesStaticBlock()
    {
        var result = Convert(@"
public struct Matrix
{
    public float M11, M12, M21, M22;
    public static readonly Matrix Identity = new Matrix { M11 = 1, M22 = 1 };
}");

        Assert.True(result.Success);
        // Should use static initializer block instead of inline initialization
        Assert.Contains("static {", result.GeneratedCode, StringComparison.Ordinal);
        // The static block should contain the field assignment
        Assert.Contains("Identity =", result.GeneratedCode, StringComparison.Ordinal);
        // The field declaration should NOT have an inline initializer (no `= _obj1` on field line)
        Assert.DoesNotContain("static final Matrix Identity = _obj", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Fix 5b: Static constructor in struct (bonus fix) ────────────────────────

    [Fact]
    public void Struct_StaticConstructor_GeneratesStaticInitializerBlock()
    {
        var result = Convert(@"
public struct Config
{
    public static int MaxRetries;

    static Config()
    {
        MaxRetries = 3;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("static {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MaxRetries = 3", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Additional struct tests ─────────────────────────────────────────────────

    // ── Nested type declarations inside struct ──────────────────────────────────

    [Fact]
    public void Struct_NestedEnum_GeneratedAsStaticEnum()
    {
        var result = Convert(@"
public struct Direction
{
    public enum Axis { X, Y, Z }
    public Axis CurrentAxis;
}");

        Assert.True(result.Success);
        Assert.Contains("static enum Axis", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_NestedClass_GeneratedAsStaticClass()
    {
        var result = Convert(@"
public struct Container
{
    public class Builder
    {
        public int BuildCount;
    }
    public int Value;
}");

        Assert.True(result.Success);
        Assert.Contains("public static class Builder", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Operator overloading ────────────────────────────────────────────────────

    [Fact]
    public void Struct_OperatorOverloads_GenerateStaticMethods()
    {
        var result = Convert(@"
public struct Vec2
{
    public float X, Y;
    public Vec2(float x, float y) { X = x; Y = y; }
    public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 v, float s) => new Vec2(v.X * s, v.Y * s);
}");

        Assert.True(result.Success);
        Assert.Contains("public static Vec2 add(Vec2 a, Vec2 b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static Vec2 subtract(Vec2 a, Vec2 b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static Vec2 multiply(Vec2 v, float s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_UnaryOperator_GeneratesStaticMethod()
    {
        var result = Convert(@"
public struct Vec2
{
    public float X, Y;
    public Vec2(float x, float y) { X = x; Y = y; }
    public static Vec2 operator -(Vec2 v) => new Vec2(-v.X, -v.Y);
}");

        Assert.True(result.Success);
        Assert.Contains("public static Vec2 negate(Vec2 v)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_EqualityOperator_GeneratesValueEquals()
    {
        var result = Convert(@"
public struct Point
{
    public int X, Y;
    public static bool operator ==(Point a, Point b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Point a, Point b) => !(a == b);
}");

        Assert.True(result.Success);
        Assert.Contains("public static boolean valueEquals(Point a, Point b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static boolean notEquals(Point a, Point b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_NullableComparisonAgainstNull_StaysReferenceCheck()
    {
        var result = Convert(@"
using System;
using System.Collections.Generic;
public struct Point
{
    public int X, Y;
    public static bool operator ==(Point a, Point b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Point a, Point b) => !(a == b);
}
class User
{
    double Measure(IEnumerable<Point> ps)
    {
        double l = 0;
        Point? q = null;
        foreach (Point p in ps)
        {
            if (q != null)
                l += 1;
            q = p;
        }
        return l;
    }
}");

        Assert.True(result.Success);
        // A nullable struct compared against a null literal is a HasValue check:
        // it must stay a plain reference comparison instead of calling the value
        // operator, which would dereference null and throw.
        Assert.Contains("q != null", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("notEquals(q, null)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("valueEquals(q, null)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_ConversionOperators_GenerateStaticMethods()
    {
        var result = Convert(@"
public struct Temperature
{
    public double Celsius;
    public Temperature(double c) { Celsius = c; }
    public static implicit operator double(Temperature t) => t.Celsius;
    public static explicit operator Temperature(double d) => new Temperature(d);
}");

        Assert.True(result.Success);
        Assert.Contains("public static double toDouble(Temperature t)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static Temperature toTemperature(double d)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── IEquatable<T> removal ───────────────────────────────────────────────────

    // ── Auto-generated equals/toString ──────────────────────────────────────────

    [Fact]
    public void Struct_ExplicitToString_NotOverridden()
    {
        var result = Convert(@"
public struct Point
{
    public int X, Y;
    public override string ToString() => $""({X}, {Y})"";
}");

        Assert.True(result.Success);
        // The explicit toString should be present
        Assert.Contains("toString()", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain the auto-generated pattern "Point{"
        Assert.DoesNotContain("Point{", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── this = expr expansion ───────────────────────────────────────────────────

    // ── Struct field initialization in default ctor ─────────────────────────────

    [Fact]
    public void Struct_WithStructField_DefaultCtorInitializesField()
    {
        var result = Convert(@"
public struct Inner { public int Value; }
public struct Outer
{
    public Inner Data;
    public int Count;
}");

        Assert.True(result.Success);
        // Struct-typed field should be initialized at declaration (C# value type semantics)
        Assert.Contains("public Inner Data = new Inner()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_NestedStructFields_AllInitializedInDefaultCtor()
    {
        var result = Convert(@"
public struct Vec2 { public float X, Y; }
public struct Transform
{
    public Vec2 Position;
    public Vec2 Scale;
    public float Rotation;
}");

        Assert.True(result.Success);
        // Both struct-typed fields should be initialized at declaration (C# value type semantics)
        Assert.Contains("public Vec2 Position = new Vec2()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Vec2 Scale = new Vec2()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_WithoutStructFields_NoExplicitDefaultCtor()
    {
        var result = Convert(@"
public struct Simple
{
    public int X, Y;
}");

        Assert.True(result.Success);
        // No need for explicit default ctor when there are no struct fields
        // (Java's default ctor will zero-initialize primitive fields)
        Assert.DoesNotContain("public Simple()", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Struct field initialization in non-struct classes (FieldTransformer) ──────

    [Fact]
    public void Class_WithStructFieldNoInitializer_FieldInitializedAtDeclaration()
    {
        var result = Convert(@"
public struct Inner { public int Value; }
public class Wrapper
{
    public Inner Data;
    public int Count;
}");

        Assert.True(result.Success);
        // Struct-typed field in a class should be initialized to prevent null reference in Java
        Assert.Contains("public Inner Data = new Inner()", result.GeneratedCode, StringComparison.Ordinal);
        // Primitive fields should NOT get a default initializer
        Assert.DoesNotContain("public int Count = ", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Class_WithStructFieldWithInitializer_PreservesOriginalInitializer()
    {
        var result = Convert(@"
public struct Vec2 { public float X, Y; }
public class Entity
{
    public Vec2 Position = new Vec2 { X = 10, Y = 20 };
}");

        Assert.True(result.Success);
        // Should preserve the explicit initializer, not replace it with new Vec2()
        Assert.Contains("X = 10", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Y = 20", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Class_WithMultipleStructFields_AllInitialized()
    {
        var result = Convert(@"
public struct Vec2 { public float X, Y; }
public struct Rect { public Vec2 Min, Max; }
public class Layout
{
    public Vec2 Origin;
    public Rect Bounds;
    public int Count;
}");

        Assert.True(result.Success);
        Assert.Contains("public Vec2 Origin = new Vec2()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Rect Bounds = new Rect()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Count = ", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Class_WithoutStructFields_NoUnnecessaryInitializers()
    {
        var result = Convert(@"
public class Simple
{
    public int X;
    public string Name;
    public double Value;
}");

        Assert.True(result.Success);
        // Non-struct fields should never get auto-generated initializers
        Assert.DoesNotContain("public int X = ", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public double Value = ", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Generic type parameter struct binding tests ──────────────────────────

    [Fact]
    public void GenericTypeParam_AlwaysSameStruct_FieldGetsInitializer()
    {
        var result = Convert(@"
public struct ValueType { public int Value; }
public abstract class Base<TValue>
{
    public TValue Field;
}
public class Derived : Base<ValueType> { }
");

        Assert.True(result.Success);
        // Field of type parameter that is always bound to the same struct should get initializer
        // The field type stays as the type parameter name (TValue), but the initializer
        // uses the concrete struct type (ValueType) so Java can instantiate it.
        Assert.Contains("new ValueType()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericTypeParam_AlwaysReference_KeepsNullDefault()
    {
        var result = Convert(@"
public class RefType { public int Value; }
public abstract class Base<TValue>
{
    public TValue Field;
}
public class Derived : Base<RefType> { }
");

        Assert.True(result.Success);
        // Field of type parameter always bound to reference type should NOT get struct initializer
        Assert.DoesNotContain("new RefType()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericTypeParam_NoSubclassInfo_FallsBackToNull()
    {
        var result = Convert(@"
public struct ValueType { public int Value; }
public abstract class Base<TValue>
{
    public TValue Field;
}
// No subclass — analyzer cannot determine binding
");

        Assert.True(result.Success);
        // When no subclass info is available, fall back to current behavior (no initializer)
        // The field should be declared without a struct initializer
        Assert.DoesNotContain("Field = new", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructBoundSubclass_NoOverrideInAbstractSubclass()
    {
        // Reproduces the real-world ScanBase.java / Parser.java bug:
        // Abstract subclass extends base with AlwaysSameStruct binding →
        // no factory method in base → no @Override in subclass.
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(new[]
            {
                new SourceFile
                {
                    FilePath = "RealWorld.cs",
                    Content = """
public struct ValueType { public string sVal; }

public abstract class AbstractScanner<TValue>
{
    public TValue yylval;
}

public abstract class ShiftReduceParser<TValue>
{
    protected TValue CurrentSemanticValue;
    protected void Reset()
    {
        CurrentSemanticValue = default(TValue);
    }
}

public abstract class ScanBase : AbstractScanner<ValueType> { }

public sealed class Parser : ShiftReduceParser<ValueType>
{
    public void UseDefault()
    {
        Reset();
    }
}
"""
                }
            });

        Assert.All(results, r =>
            Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var code = string.Join("\n", results.Select(r => r.GeneratedCode));

        // Both base classes have AlwaysSameStruct → emit new ValueType().
        Assert.Contains("new ValueType()", code, StringComparison.Ordinal);
        // Neither subclass should have a spurious @Override for a non-existent factory method.
        Assert.DoesNotContain("_cs2jDefault_TValue", code, StringComparison.Ordinal);
    }

    // ── Chained struct assignment expansion (LowerStruct) ──────────────────────

    [Fact]
    public void Struct_AssignmentFromNew_NoClone()
    {
        var result = Convert(@"
public struct Point { public int X, Y; }
public class Program
{
    public Point center;
    public void M()
    {
        center = new Point();
    }
}");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("center = new Point()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Point().clone()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonStruct_ChainedAssignment_NoExpansion()
    {
        var result = Convert(@"
public class Widget { public int Value; }
public class Program
{
    public Widget center;
    public Widget previousCenter;
    public void M(Widget w)
    {
        center = previousCenter = w;
    }
}");

        Assert.True(result.Success);
        var code = result.GeneratedCode;
        Assert.DoesNotContain("_structCopy", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".clone()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_AutoPropertyGetter_ReturnsClone()
    {
        var result = Convert(@"
public struct Point { public int X, Y; }
public class Box
{
    public Point Value { get; set; }
}");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode;
        Assert.Contains("return value.clone();", code, StringComparison.Ordinal);
        Assert.Contains("this.value = value.clone();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_PropertySetterFromGetter_UsesCopySemantics()
    {
        var result = Convert(@"
public struct Point { public int X, Y; }
public class Box
{
    public Point Value { get; set; }
}
public class Program
{
    public void M(Box target, Box source)
    {
        target.Value = source.Value;
    }
}");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode;
        Assert.Contains("return value.clone();", code, StringComparison.Ordinal);
        Assert.Contains("this.value = value.clone();", code, StringComparison.Ordinal);
        Assert.Contains("target.setValue(source.getValue())", code, StringComparison.Ordinal);
    }

    // ── User-defined conversion operator call-sites ────────────────────────────

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
