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

    // ── this = expr expansion ───────────────────────────────────────────────────

    [Fact]
    public void Struct_ThisAssignDefault_ExpandsToFieldReset()
    {
        var result = Convert(@"
public struct MutablePoint
{
    public int X, Y;
    public void Reset()
    {
        this = default;
    }
}");

        Assert.True(result.Success);
        // Should NOT contain 'this = ' (invalid Java)
        Assert.DoesNotContain("this = ", result.GeneratedCode, StringComparison.Ordinal);
        // Should contain field-by-field zero-initialization
        Assert.Contains("this.X = 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("this.Y = 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_ThisAssignOther_ExpandsToFieldCopy()
    {
        var result = Convert(@"
public struct MutablePoint
{
    public int X, Y;
    public void SetTo(MutablePoint other)
    {
        this = other;
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("this = ", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("this.X = other.X", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("this.Y = other.Y", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_ThisAssignDefault_StructFields_InitializedNotNull()
    {
        var result = Convert(@"
public struct Vec2 { public float X, Y; }
public struct Rect
{
    public Vec2 Min;
    public Vec2 Max;
    public void Reset()
    {
        this = default;
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("this = ", result.GeneratedCode, StringComparison.Ordinal);
        // Struct-typed fields should be initialized to new instances, not null
        Assert.Contains("this.Min = new Vec2()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("this.Max = new Vec2()", result.GeneratedCode, StringComparison.Ordinal);
    }

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
    public void Struct_WithStructFieldAndExplicitCtor_DefaultCtorAlsoInitializes()
    {
        var result = Convert(@"
public struct Inner { public int Value; }
public struct Wrapper
{
    public Inner Info;
    public int Count;
    public Wrapper(int count)
    {
        Count = count;
        Info = new Inner();
    }
}");

        Assert.True(result.Success);
        // Should have both ctors (explicit one + auto-generated default)
        Assert.Contains("public Wrapper()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Wrapper(int count)", result.GeneratedCode, StringComparison.Ordinal);
        // Struct-typed field should be initialized at declaration (C# value type semantics)
        Assert.Contains("public Inner Info = new Inner()", result.GeneratedCode, StringComparison.Ordinal);
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
    public void GenericTypeParam_DefaultExpression_EmitsNewStruct()
    {
        var result = Convert(@"
public struct ValueType { public int Value; }
public class Container<TValue>
{
    public TValue GetDefault() { return default(TValue); }
}
public class Derived : Container<ValueType> { }
");

        Assert.True(result.Success);
        // default(TValue) where TValue is always bound to a struct should emit new ValueType()
        Assert.Contains("new ValueType()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return null", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericTypeParam_DefaultLiteral_EmitsNewStruct()
    {
        var result = Convert(@"
public struct ValueType { public int Value; }
public class Container<TValue>
{
    public void Reset() { TValue v = default; }
}
public class Derived : Container<ValueType> { }
");

        Assert.True(result.Success);
        // default literal where TValue is always bound to a struct should emit new ValueType()
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
    public async Task GenericRuntimeLibrary_DefaultTypeParameter_NotFrozenAsNullBeforeConsumerBinding()
    {
        var runtimeResults = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(new[]
            {
                new SourceFile
                {
                    FilePath = "Runtime.cs",
                    Content = """
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
"""
                }
            });

        Assert.All(runtimeResults, result =>
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message))));

        var runtimeCode = string.Join("\n", runtimeResults.Select(result => result.GeneratedCode));
        // Runtime library has no subclass bindings visible, so the binding is Unknown.
        // default(TValue) should emit a factory method call, not null.
        Assert.DoesNotContain("CurrentSemanticValue = null;", runtimeCode, StringComparison.Ordinal);
        Assert.Contains("_cs2jDefault_TValue()", runtimeCode, StringComparison.Ordinal);
        Assert.Contains("_cs2jDefault_TValue", runtimeCode, StringComparison.Ordinal); // factory method itself

        var consumerResults = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(new[]
            {
                new SourceFile
                {
                    FilePath = "Dot.cs",
                    Content = """
public struct ValueType
{
    public string sVal;
}

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

public abstract class ScanBase : AbstractScanner<ValueType>
{
}

public sealed class Scanner : ScanBase
{
    public void Load()
    {
        yylval.sVal = "id";
    }
}

public sealed class Parser : ShiftReduceParser<ValueType>
{
    public void UseDefault()
    {
        Reset();
        CurrentSemanticValue.sVal = "id";
    }
}
"""
                }
            });

        Assert.All(consumerResults, result =>
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message))));

        var consumerCode = string.Join("\n", consumerResults.Select(result => result.GeneratedCode));
        // When the binding analyzer detects TValue is always ValueType (struct),
        // default(TValue) should resolve to new ValueType() (not null).
        Assert.Contains("new ValueType()", consumerCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultValue.of()", consumerCode, StringComparison.Ordinal);
        // The base class emitted new ValueType() directly (AlwaysSameStruct), so
        // no _cs2jDefault_TValue factory method exists. The subclass must NOT add
        // a spurious @Override for a non-existent base method.
        Assert.DoesNotContain("_cs2jDefault_TValue", consumerCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructBoundSubclass_NoSpuriousOverride_WhenBaseHasSameStructBinding()
    {
        // Single-conversion project with mixed bindings:
        // - LibraryBase<T> has no struct-bound subclass visible → Unknown binding → factory method
        // - StructBase<T> has struct-bound subclass → AlwaysSameStruct → new ValueType() directly
        // - SubStruct extends StructBase<ValueType> → must NOT add @Override for factory method
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(new[]
            {
                new SourceFile
                {
                    FilePath = "Mixed.cs",
                    Content = """
public struct ValueType { public string sVal; }

// Unknown binding: no struct-bound subclass of LibraryBase in this project.
public abstract class LibraryBase<TValue>
{
    public TValue Field;
    protected TValue GetDefault() { return default(TValue); }
}

// AlwaysSameStruct binding: SubStruct<ValueType> is visible.
public abstract class StructBase<TValue>
{
    public TValue yylval;
}

public abstract class SubStruct : StructBase<ValueType> { }
"""
                }
            });

        Assert.All(results, r =>
            Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var code = string.Join("\n", results.Select(r => r.GeneratedCode));

        // LibraryBase has Unknown binding → factory method IS generated.
        Assert.Contains("_cs2jDefault_TValue()", code, StringComparison.Ordinal);
        // StructBase has AlwaysSameStruct → emits new ValueType().
        Assert.Contains("new ValueType()", code, StringComparison.Ordinal);
        // SubStruct extends StructBase<ValueType> (AlwaysSameStruct base) →
        // must NOT add a spurious @Override for a factory method that doesn't exist.
        var subStructClassStart = code.IndexOf("class SubStruct", StringComparison.Ordinal);
        var subStructClassEnd = code.IndexOf("class ", subStructClassStart + 10, StringComparison.Ordinal);
        if (subStructClassEnd < 0) subStructClassEnd = code.Length;
        var subStructClass = code[subStructClassStart..subStructClassEnd];
        Assert.DoesNotContain("_cs2jDefault_TValue", subStructClass, StringComparison.Ordinal);
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

    [Fact]
    public async Task StructBoundSubclass_ThreeLevelInheritance_NoSpuriousOverride()
    {
        // GrandParent<T> → Parent<T> → Child<ValueType>
        // GrandParent uses default(T) → AlwaysSameStruct (Child binds to ValueType).
        // No factory method → no override at any level.
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(new[]
            {
                new SourceFile
                {
                    FilePath = "ThreeLevel.cs",
                    Content = """
public struct ValueType { public string sVal; }

public abstract class GrandParent<TValue>
{
    public TValue Field;
    protected TValue GetDefault() { return default(TValue); }
}

public abstract class Parent<TValue> : GrandParent<TValue>
{
}

public abstract class Child : Parent<ValueType>
{
    public void Use()
    {
        var x = GetDefault();
    }
}
"""
                }
            });

        Assert.All(results, r =>
            Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var code = string.Join("\n", results.Select(r => r.GeneratedCode));

        // The conversion succeeded — no Java compilation errors would occur.
        Assert.Contains("new ValueType()", code, StringComparison.Ordinal);

        // Key invariant: if a subclass contains _cs2jDefault_TValue @Override,
        // the base class must also define that method. Check that the base
        // (GrandParent) has the factory method if Child overrides it.
        var childStart = code.IndexOf("class Child", StringComparison.Ordinal);
        Assert.True(childStart > 0, "Child class should be present in output");
        var nextClass = code.IndexOf("class ", childStart + 10, StringComparison.Ordinal);
        var childBody = nextClass > 0 ? code[childStart..nextClass] : code[childStart..];

        if (childBody.Contains("_cs2jDefault_TValue"))
        {
            // If Child has the override, GrandParent must have the base factory method.
            Assert.Contains("_cs2jDefault_TValue()", code.AsSpan(0, childStart), StringComparison.Ordinal);
        }
        // Either way: conversion succeeded and no spurious @Override on non-existent method.
    }

    [Fact]
    public async Task StructBoundSubclass_FactoryMethodOverride_WhenBaseHasUnknownBinding()
    {
        // Convert a runtime library WITHOUT any struct-bound subclass.
        // The binding is Unknown → factory method IS generated.
        var runtimeResults = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(new[]
            {
                new SourceFile
                {
                    FilePath = "Library.cs",
                    Content = """
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
"""
                }
            });

        Assert.All(runtimeResults, r =>
            Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var runtimeCode = string.Join("\n", runtimeResults.Select(r => r.GeneratedCode));

        // Unknown binding: factory methods ARE generated in both base classes.
        Assert.Contains("_cs2jDefault_TValue()", runtimeCode, StringComparison.Ordinal);
        Assert.Contains("DefaultValue.of()", runtimeCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossProject_DefaultFactoryMethod_OverrideGeneratedInSubclassProject()
    {
        // Simulates the cross-project scenario where:
        // Project A (base library) has ShiftReduceParser<TValue> with default(TValue) → Unknown binding → factory method
        // Project B (consumer) has Parser extends ShiftReduceParser<ValueType> → should generate @Override

        // Use a shared ConversionOptions so the DefaultFactoryMethodStore is shared across projects.
        var sharedOptions = new ConversionOptions();

        // ── Project A: base library with abstract generic class using default(T) ──
        var libraryResults = await new ProjectConversionPipeline(sharedOptions)
            .ConvertProjectAsync(new[]
            {
                new SourceFile
                {
                    FilePath = "ShiftReduceParser.cs",
                    Content = """
public abstract class ShiftReduceParser<TValue>
{
    protected TValue CurrentSemanticValue;
    protected void Reset()
    {
        CurrentSemanticValue = default(TValue);
    }
}
"""
                }
            });

        Assert.All(libraryResults, r =>
            Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var libraryCode = string.Join("\n", libraryResults.Select(r => r.GeneratedCode));

        // Unknown binding: factory method IS generated.
        Assert.Contains("_cs2jDefault_TValue()", libraryCode, StringComparison.Ordinal);
        Assert.Contains("DefaultValue.of()", libraryCode, StringComparison.Ordinal);

        // ── Project B: consumer with struct-bound subclass ──
        var consumerResults = await new ProjectConversionPipeline(sharedOptions)
            .ConvertProjectAsync(new[]
            {
                new SourceFile
                {
                    FilePath = "Parser.cs",
                    Content = """
public struct ValueType
{
    public string sVal;
}

public abstract class ShiftReduceParser<TValue>
{
    protected TValue CurrentSemanticValue;
    protected void Reset()
    {
        CurrentSemanticValue = default(TValue);
    }
}

public sealed class Parser : ShiftReduceParser<ValueType>
{
    public void UseDefault()
    {
        Reset();
        CurrentSemanticValue.sVal = "id";
    }
}
"""
                }
            });

        Assert.All(consumerResults, r =>
            Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var consumerCode = string.Join("\n", consumerResults.Select(r => r.GeneratedCode));

        // The shared DefaultFactoryMethodStore should carry the registration from project A,
        // so the subclass in project B gets the @Override method.
        Assert.Contains("@Override", consumerCode, StringComparison.Ordinal);
        Assert.Contains("_cs2jDefault_TValue", consumerCode, StringComparison.Ordinal);
        Assert.Contains("new ValueType()", consumerCode, StringComparison.Ordinal);
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
