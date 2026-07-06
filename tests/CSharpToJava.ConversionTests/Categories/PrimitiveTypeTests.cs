using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# primitive/built-in value types to their Java equivalents.
/// Method names are lowercased by the converter; type/field names are preserved.
/// </summary>
public class PrimitiveTypeTests : ConversionTestBase
{
    public static IEnumerable<object[]> FieldTypeCases()
    {
        yield return new object[] { "int", "int" };
        yield return new object[] { "long", "long" };
        yield return new object[] { "bool", "boolean" };
        yield return new object[] { "double", "double" };
        yield return new object[] { "char", "char" };
        yield return new object[] { "string", "String" };
        yield return new object[] { "byte", "int" };
        yield return new object[] { "sbyte", "byte" };
        yield return new object[] { "short", "short" };
        yield return new object[] { "ushort", "int" };
        yield return new object[] { "uint", "int" };
        yield return new object[] { "ulong", "long" };
        yield return new object[] { "float", "float" };
        yield return new object[] { "decimal", "Decimal" };
        yield return new object[] { "object", "Object" };
    }

    [Theory]
    [MemberData(nameof(FieldTypeCases))]
    public void Field_OfPrimitiveType_MapsToJavaType(string csharpType, string javaType)
    {
        var result = Convert($"class C {{ public {csharpType} Value; }}");
        AssertConversion(result, $"public {javaType}");
    }

    [Theory]
    [MemberData(nameof(FieldTypeCases))]
    public void LocalVariable_OfPrimitiveType_MapsToJavaType(string csharpType, string javaType)
    {
        var result = Convert($"class C {{ public void M() {{ {csharpType} x = default; }} }}");
        AssertConversion(result, $"{javaType} x");
    }

    [Theory]
    [MemberData(nameof(FieldTypeCases))]
    public void MethodReturn_OfPrimitiveType_MapsToJavaType(string csharpType, string javaType)
    {
        var result = Convert($"class C {{ public {csharpType} M() {{ return default; }} }}");
        AssertConversion(result, $"public {javaType} m()");
    }

    [Theory]
    [MemberData(nameof(FieldTypeCases))]
    public void MethodParameter_OfPrimitiveType_MapsToJavaType(string csharpType, string javaType)
    {
        var result = Convert($"class C {{ public void M({csharpType} x) {{ var y = x; }} }}");
        AssertConversion(result, $"m({javaType} x)");
    }

    [Fact]
    public void IntegerLiteral_FieldInitializer_MapsToJavaInt()
    {
        var result = Convert("class C { public int Count = 42; }");
        AssertConversion(result, "public int Count = 42;");
    }

    [Fact]
    public void LongLiteral_Suffix_MapsToJavaLong()
    {
        var result = Convert("class C { public long Big = 42L; }");
        AssertConversion(result, "public long Big = 42L;");
    }

    [Fact]
    public void DoubleLiteral_MapsToJavaDouble()
    {
        var result = Convert("class C { public double Pi = 3.14; }");
        AssertConversion(result, "public double Pi = 3.14;");
    }

    [Fact]
    public void FloatLiteral_Suffix_MapsToJavaFloat()
    {
        var result = Convert("class C { public float F = 1.5f; }");
        AssertConversion(result, "public float F = 1.5f;");
    }

    [Fact]
    public void CharLiteral_MapsToJavaChar()
    {
        var result = Convert("class C { public char C = 'x'; }");
        AssertConversion(result, "public char C = 'x';");
    }

    [Fact]
    public void BooleanTrueFalse_MapsToJavaBooleanLiterals()
    {
        var result = Convert("class C { public bool Flag = true; public bool Other = false; }");
        AssertConversion(result, "public boolean Flag = true;");
    }

    [Fact]
    public void BoolField_NamedToAvoidConflict_MapsCorrectly()
    {
        var result = Convert("class C { public bool Enabled; }");
        AssertConversion(result, "public boolean Enabled;");
    }

    [Fact]
    public void DecimalField_GetsDefaultInitializer()
    {
        var result = Convert("class C { public decimal Amount; }");
        AssertConversion(result, "import io.github.ningpp.compat.Decimal;", "public Decimal Amount = Decimal.ZERO;");
    }

    [Fact]
    public void UnsignedInt_MapsToSignedInt()
    {
        var result = Convert("class C { public uint Value; }");
        AssertConversion(result, "public int Value;");
    }

    [Fact]
    public void UnsignedLong_MapsToSignedLong()
    {
        var result = Convert("class C { public ulong Value; }");
        AssertConversion(result, "public long Value;");
    }

    [Fact]
    public void Byte_MapsToInt()
    {
        var result = Convert("class C { public byte Value; }");
        AssertConversion(result, "public int Value;");
    }

    [Fact]
    public void Object_MapsToJavaObject()
    {
        var result = Convert("class C { public object Data; }");
        AssertConversion(result, "public Object Data;");
    }

    [Fact]
    public void StringArray_Field_MapsToStringArray()
    {
        var result = Convert("class C { public string[] Names; }");
        AssertConversion(result, "public String[] Names;");
    }

    [Fact]
    public void PrimitiveArithmetic_CompilesToJava()
    {
        var result = Convert("class C { public int Add(int a, int b) { return a + b; } public int Mul(int a, int b) { return a * b; } }");
        AssertConversion(result, "public int add(int a, int b)", "public int mul(int a, int b)");
    }

    [Fact]
    public void IncrementDecrement_MapsToJava()
    {
        var result = Convert("class C { public int M(int x) { x++; x--; return x; } }");
        AssertConversion(result, "x++", "x--");
    }

    [Fact]
    public void CompoundAssignment_MapsToJava()
    {
        var result = Convert("class C { public int M(int x) { x += 5; x *= 2; return x; } }");
        AssertConversion(result, "x += 5;", "x *= 2;");
    }

    [Fact]
    public void ModuloOperator_MapsToJava()
    {
        var result = Convert("class C { public int M(int a, int b) { return a % b; } }");
        AssertConversion(result, "return a % b;");
    }

    [Fact]
    public void ComparisonOperators_MapsToJava()
    {
        var result = Convert("class C { public bool M(int a, int b) { return a < b && a > b && a == b; } }");
        AssertConversion(result, "return a < b && a > b && a == b;");
    }

    [Fact]
    public void LogicalNot_MapsToJava()
    {
        var result = Convert("class C { public bool M(bool b) { return !b; } }");
        AssertConversion(result, "return !b;");
    }

    [Fact]
    public void BitwiseOperators_MapsToJava()
    {
        var result = Convert("class C { public int M(int a, int b) { return a & b | a ^ b; } }");
        AssertConversion(result, "return a & b | a ^ b;");
    }

    [Fact]
    public void TernaryOperator_MapsToJava()
    {
        var result = Convert("class C { public int M(int x) { return x > 0 ? x : -x; } }");
        AssertConversion(result, "return (x > 0 ? x : -x);");
    }

    [Fact]
    public void CastExpression_MapsToJava()
    {
        var result = Convert("class C { public int M(double d) { return (int)d; } }");
        AssertConversion(result, "return (int)(d);");
    }

    [Fact]
    public void ConstField_MapsToJavaFinal()
    {
        var result = Convert("class C { public const int Max = 100; }");
        AssertConversion(result, "public static final int Max = 100;");
    }

    [Fact]
    public void ReadonlyField_MapsToJavaFinal()
    {
        var result = Convert("class C { public readonly int Id = 1; }");
        AssertConversion(result, "public int Id = 1;");
    }

    [Fact]
    public void StaticField_MapsToJavaStatic()
    {
        var result = Convert("class C { public static int Counter; }");
        AssertConversion(result, "public static int Counter;");
    }

    [Fact]
    public void InternalModifier_MapsToPackagePrivate()
    {
        var result = Convert("class C { internal int Secret; }");
        AssertConversion(result, "int Secret;");
    }

    [Fact]
    public void ProtectedModifier_Preserved()
    {
        var result = Convert("class C { protected int Secret; }");
        AssertConversion(result, "protected int Secret;");
    }

    [Fact]
    public void PrivateModifier_Preserved()
    {
        var result = Convert("class C { private int secret; }");
        AssertConversion(result, "private int secret;");
    }

    [Fact]
    public void VoidReturn_MapsToJavaVoid()
    {
        var result = Convert("class C { public void DoWork() { } }");
        AssertConversion(result, "public void doWork() {");
    }

    [Fact]
    public void NestedGenericPrimitive_Field_MapsCorrectly()
    {
        var result = Convert("class C { public System.Collections.Generic.List<int> Items; }");
        AssertConversion(result, "import io.github.ningpp.compat.CSharpList;", "public CSharpList<Integer> Items;");
    }

    [Fact]
    public void BoxedPrimitive_NullableField_MapsToJavaWrapper()
    {
        var result = Convert("class C { public int? Maybe; }");
        AssertConversion(result, "public Integer Maybe;");
    }
}
