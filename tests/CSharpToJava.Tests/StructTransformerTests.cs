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

    [Fact]
    public void ExpressionBodiedProperty_CloneDoesNotCopyPhantomField()
    {
        var result = Convert(@"
using System;
public struct Vector2
{
    public float X;
    public float Y;
    public float LengthSquared => X * X + Y * Y;
}");

        Assert.True(result.Success);
        // Clone should only copy X and Y, not a phantom 'lengthSquared' field
        Assert.Contains("copy.X = this.X;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("copy.Y = this.Y;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("copy.lengthSquared", result.GeneratedCode, StringComparison.Ordinal);
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

    [Fact]
    public void GenericStruct_CloneIncludesTypeParameters()
    {
        var result = Convert(@"
public struct Pair<T>
{
    public T First;
    public T Second;
}");

        Assert.True(result.Success);
        // Clone should use generic type in declaration and diamond in new expression
        Assert.Contains("Pair<T> copy = new Pair<>()", result.GeneratedCode, StringComparison.Ordinal);
        // Return type should include type parameter
        Assert.Contains("Pair<T> clone()", result.GeneratedCode, StringComparison.Ordinal);
    }

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

    [Fact]
    public void ReadonlyStruct_FieldsAreFinal()
    {
        var result = Convert(@"
public readonly struct ReadOnlyPoint
{
    public readonly int X;
    public readonly int Y;

    public ReadOnlyPoint(int x, int y) { X = x; Y = y; }
}");

        Assert.True(result.Success);
        Assert.Contains("public final int X;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public final int Y;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_ImplementsCloneable()
    {
        var result = Convert(@"
public struct Point { public int X, Y; }");

        Assert.True(result.Success);
        Assert.Contains("implements Cloneable", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("clone()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_DefaultConstructorGenerated()
    {
        var result = Convert(@"
public struct Point
{
    public int X, Y;
    public Point(int x, int y) { X = x; Y = y; }
}");

        Assert.True(result.Success);
        // Should have both no-arg and parameterized constructors
        Assert.Contains("public Point()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Point(int x, int y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_NestedStructField_CloneCallsClone()
    {
        var result = Convert(@"
public struct Inner { public int Value; }
public struct Outer
{
    public Inner Data;
}");

        Assert.True(result.Success);
        // Clone should deep-copy struct-typed fields.
        // Null check is appropriate because C# structs become Java classes (reference types),
        // and the field could be null at the Java level (e.g. before initialization).
        Assert.Contains("this.Data != null ? this.Data.clone() : null", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Nested type declarations inside struct ──────────────────────────────────

    [Fact]
    public void Struct_NestedStruct_GeneratedAsStaticClass()
    {
        var result = Convert(@"
public struct Outer
{
    public struct Inner
    {
        public int Value;
    }
    public Inner Data;
}");

        Assert.True(result.Success);
        // Nested struct should be generated as a static class inside the outer class
        Assert.Contains("public static class Inner implements Cloneable", result.GeneratedCode, StringComparison.Ordinal);
        // Nested struct should have its own clone method
        Assert.Contains("Inner clone()", result.GeneratedCode, StringComparison.Ordinal);
    }

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

    [Fact]
    public void Struct_DeeplyNestedStruct_ThreeLevels()
    {
        var result = Convert(@"
public struct Level1
{
    public struct Level2
    {
        public struct Level3
        {
            public int Value;
        }
        public Level3 Inner;
    }
    public Level2 Middle;
}");

        Assert.True(result.Success);
        // All three levels should be present
        Assert.Contains("class Level1", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("static class Level2", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("static class Level3", result.GeneratedCode, StringComparison.Ordinal);
        // Deep nesting should have clone support
        Assert.Contains("Level3 clone()", result.GeneratedCode, StringComparison.Ordinal);
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
    public void Struct_OperatorUsage_GeneratesStaticMethodCalls()
    {
        var result = Convert(@"
public struct Vec2
{
    public float X, Y;
    public Vec2(float x, float y) { X = x; Y = y; }
    public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 v) => new Vec2(-v.X, -v.Y);
}
class User
{
    void Test()
    {
        var a = new Vec2(1, 2);
        var b = new Vec2(3, 4);
        var c = a + b;
        var d = -a;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Vec2.add(a, b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Vec2.negate(a)", result.GeneratedCode, StringComparison.Ordinal);
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

    [Fact]
    public void Struct_IEquatable_NotInImplementsList()
    {
        var result = Convert(@"
using System;
public struct Point : IEquatable<Point>
{
    public int X, Y;
    public bool Equals(Point other) => X == other.X && Y == other.Y;
    public override bool Equals(object obj) => obj is Point p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
}");

        Assert.True(result.Success);
        // IEquatable<T> doesn't exist in Java — should not appear in implements
        Assert.DoesNotContain("IEquatable", result.GeneratedCode, StringComparison.Ordinal);
        // But the Equals(Point) method should still be present as a regular method
        Assert.Contains("boolean equals(Point other)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Auto-generated equals/toString ──────────────────────────────────────────

    [Fact]
    public void Struct_ValueEquals_GeneratesEqualsObjectOverride()
    {
        var result = Convert(@"
public struct Point
{
    public int X, Y;
    public static bool operator ==(Point a, Point b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Point a, Point b) => !(a == b);
}");

        Assert.True(result.Success);
        // Should auto-generate equals(Object) that delegates to valueEquals
        Assert.Contains("@Override", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("equals(Object o)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("valueEquals(this, other)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_ToString_AutoGenerated()
    {
        var result = Convert(@"
public struct Point
{
    public int X, Y;
}");

        Assert.True(result.Success);
        Assert.Contains("toString()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Point{", result.GeneratedCode, StringComparison.Ordinal);
    }

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
