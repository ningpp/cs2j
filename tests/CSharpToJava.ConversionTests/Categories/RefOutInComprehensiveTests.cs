using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Comprehensive tests for ref/out/in parameter conversion across various types.
/// Verifies that the C#-to-Java converter generates the correct holder types and value access patterns.
/// </summary>
public class RefOutInComprehensiveTests : ConversionTestBase
{
    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefDecimal_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(ref decimal a) { a = 1m; } }");
        AssertConversion(result, "ObjectHolder<Decimal> a", "a.value = Decimal.parse");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefNullableInt_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(ref int? a) { a = 1; } }");
        AssertConversion(result, "ObjectHolder<Integer> a", "a.value = 1");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutNullableDouble_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(out double? a) { a = 1.5; } }");
        AssertConversion(result, "ObjectHolder<Double> a", "a.value = 1.5");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefEnum_ConvertsToObjectHolder()
    {
        var result = Convert("enum Color { Red, Green, Blue } class C { public void M(ref Color a) { a = Color.Red; } }");
        AssertConversion(result, "ObjectHolder<Color> a", "a.value = Color.Red");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutString_ConvertsToObjectHolderWithWriteback()
    {
        var result = Convert("class C { public void M(out string a) { a = \"x\"; } }");
        AssertConversion(result, "ObjectHolder<String> a", "a.value = \"x\"");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InPrimitive_PassesByValue()
    {
        var result = Convert("class C { public void M(in int a) { var x = a; } }");
        AssertConversion(result, "public void m(int a)");
        Assert.False(result.GeneratedCode.Contains("IntHolder"),
            "in int parameter should not generate IntHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InReferenceType_PassesByValue()
    {
        var result = Convert("class C { public void M(in string a) { var x = a; } }");
        AssertConversion(result, "public void m(String a)");
        Assert.False(result.GeneratedCode.Contains("ObjectHolder"),
            "in string parameter should not generate ObjectHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InStruct_PassesByValue()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M(in Point a) { var x = a.X; } }");
        AssertConversion(result, "public void m(Point a)");
        Assert.False(result.GeneratedCode.Contains("ObjectHolder"),
            "in struct parameter should not generate ObjectHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void MultiOutParameters_GeneratesMultipleHolders()
    {
        var result = Convert("class C { public void M(out int a, out double b) { a = 1; b = 1.5; } }");
        AssertConversion(result, "IntHolder a", "DoubleHolder b", "a.value = 1", "b.value = 1.5");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void MixedRefOutIn_AllConvertedCorrectly()
    {
        var result = Convert("class C { public void M(ref int a, out double b, in string c) { a = 1; b = 1.5; var x = c; } }");
        AssertConversion(result, "IntHolder a", "DoubleHolder b", "String c");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefOutUsedInExpression_ValueAccess()
    {
        var result = Convert("class C { public int M(ref int a) { return a + 1; } }");
        AssertConversion(result, "IntHolder a", "return a.value + 1");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutVarReadBack_AfterCall()
    {
        var result = Convert("class C { public bool Try(out int result) { result = 42; return true; } public void Call() { Try(out var r); var x = r; } }");
        AssertConversion(result, "IntHolder _rHolder1 = new IntHolder()", "tryValue(_rHolder1)", "int r = _rHolder1.value");
    }

    // ── StructTypes ──────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "StructTypes")]
    public void RefStruct_ConvertsToObjectHolder()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M(ref Point a) { a = new Point(); } }");
        AssertConversion(result, "ObjectHolder<Point> a", "a.value = new Point()");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void OutStruct_ConvertsToObjectHolderWithWriteback()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M(out Point a) { a = new Point(); } }");
        AssertConversion(result, "ObjectHolder<Point> a", "a.value = new Point()");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void RefStruct_FieldAccessThroughHolder()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M(ref Point a) { a.X = 5; a.Y = 10; } }");
        AssertConversion(result, "a.X = 5", "a.Y = 10");
        Assert.False(result.GeneratedCode.Contains("ObjectHolder<Point>"),
            "ref struct parameter with only field writes should use IsRefParamEffectivelyReadOnly optimization");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void OutStruct_MethodAssignsAllFields()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M(out Point a) { a = new Point(1, 2); } }");
        AssertConversion(result, "ObjectHolder<Point> a", "a.value = new Point(1, 2)");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void StructWithRefOutMethod_MethodSignature()
    {
        var result = Convert("struct S { public void M(ref int x) { x = 1; } }");
        AssertConversion(result, "void m(IntHolder x)", "x.value = 1");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void StructWithRefOutMethod_CallSite()
    {
        var result = Convert("struct S { public void M(ref int x) { x = 1; } } class C { public void Call() { int val = 0; var s = new S(); s.M(ref val); } }");
        AssertConversion(result, "IntHolder _valRef = new IntHolder(val)", "s.m(_valRef)", "val = _valRef.value");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void RefStructEffectivelyReadOnly_NoHolder()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public int M(ref Point a) { return a.X + a.Y; } }");
        AssertConversion(result, "public int m(Point a)", "return a.X + a.Y");
        Assert.False(result.GeneratedCode.Contains("ObjectHolder<Point>"),
            "ref struct parameter that is only read should not generate ObjectHolder");
    }

    [Fact]
    [Trait("Category", "StructTypes")]
    public void StructAsOutArg_ReadBackFieldAccess()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M(out Point p) { p = new Point(); } public void Call() { M(out var q); var x = q.X; } }");
        AssertConversion(result, "ObjectHolder<Point> _qHolder1 = new ObjectHolder<>()", "m(_qHolder1)", "Point q = _qHolder1.value");
    }

    // ── GenericTypes ──────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void RefGenericParameter_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M<T>(ref T a) { } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void OutGenericParameter_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M<T>(out T a) { a = default; } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void GenericMethod_RefStructConstraint()
    {
        var result = Convert("struct Point { public int X; } class C { public void M<T>(ref T a) where T : struct { } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void GenericClass_RefParameter()
    {
        var result = Convert("class C<T> { public void M(ref T a) { } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void GenericMethod_OutWithDefaultAssignment()
    {
        var result = Convert("class C { public void M<T>(out T a) where T : new() { a = new T(); } }");
        AssertConversion(result, "ObjectHolder<T> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void RefListOfGeneric_HolderType()
    {
        var result = Convert("using System.Collections.Generic; class C { public void M<T>(ref List<T> a) { } }");
        AssertConversion(result, "ObjectHolder<CSharpList<T>> a");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void OutGenericUsedInExpression()
    {
        var result = Convert("class C { public void Assign<T>(out T a) { a = default; } public void Test() { Assign(out var x); var s = x == null; } }");
        AssertConversion(result, "ObjectHolder", ".value");
    }

    [Fact]
    [Trait("Category", "GenericTypes")]
    public void GenericStruct_RefParameter()
    {
        var result = Convert("struct S<T> { public T Item; } class C { public void M(ref S<int> a) { a = new S<int>(); } }");
        AssertConversion(result, "ObjectHolder<S<Integer>> a");
    }

    // ── EdgeCases ──────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutDiscard_PassesScratchArray()
    {
        var result = Convert("class C { public void M(out int a) { a = 1; } public void Call() { M(out _); } }");
        AssertConversion(result, "new Object[1]");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutDiscardIdentifier_PassesScratchArray()
    {
        var result = Convert("class C { public bool Try(out int v) { v = 1; return true; } public void Call() { Try(out _); } }");
        AssertConversion(result, "new Object[1]");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void RefForwarding_PassesHolderDirectly()
    {
        var result = Convert("class C { public void Inner(ref int a) { a = 1; } public void Outer(ref int a) { Inner(ref a); } }");
        AssertConversion(result, "inner(a)");
        Assert.False(result.GeneratedCode.Contains("new IntHolder(a)"),
            "Ref forwarding should pass the holder directly without re-wrapping");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutForwarding_PassesHolderDirectly()
    {
        var result = Convert("class C { public void Inner(out int a) { a = 1; } public void Outer(out int a) { Inner(out a); } }");
        AssertConversion(result, "inner(a)");
        Assert.False(result.GeneratedCode.Contains("new IntHolder(a)"),
            "Out forwarding should pass the holder directly without re-wrapping");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void NestedOutInCondition()
    {
        var result = Convert("class C { public bool TryGet(out int val) { val = 1; return true; } public void Use(int x) { } public void Test() { if (TryGet(out var val)) { Use(val); } } }");
        AssertConversion(result, "IntHolder", "tryGet(", "val = ", ".value");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void MultipleOutInSingleCall()
    {
        var result = Convert("class C { public void M(out int a, out int b, out int c) { a = 1; b = 2; c = 3; } }");
        AssertConversion(result, "IntHolder a", "IntHolder b", "IntHolder c");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void InKeyword_RefReadonly_PassesByValue()
    {
        var result = Convert("class C { public void M(in int a) { var x = a; } }");
        AssertConversion(result, "public void m(int a)");
        Assert.False(result.GeneratedCode.Contains("IntHolder"),
            "in int parameter should not generate IntHolder");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void RefMemberAccess_WrapsInHolder()
    {
        var result = Convert("class C { public int Value; public void Mutate(ref int a) { a = a + 1; } public void Test() { Mutate(ref Value); } }");
        AssertConversion(result, "IntHolder", "Value = ", ".value");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void RefElementAccess_WrapsInHolder()
    {
        var result = Convert("class C { public void Mutate(ref int a) { a = a + 1; } public void Test() { int[] arr = new int[1]; Mutate(ref arr[0]); } }");
        AssertConversion(result, "IntHolder", "arr[0] = ", ".value");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutVarInForeach_ReadBackWorks()
    {
        var result = Convert("using System.Collections.Generic; class C { public bool TryGet(out int y) { y = 1; return true; } public void Use(int x) { } public void Test(List<int> items) { foreach (var item in items) { TryGet(out var y); Use(y); } } }");
        AssertConversion(result, "IntHolder", "tryGet(", "y = ", ".value");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void RefAfterOut_SameVariable_HolderSeeded()
    {
        var result = Convert("class C { public void Assign(out int a) { a = 1; } public void Mutate(ref int a) { a = a + 1; } public void Test() { int val; Assign(out val); Mutate(ref val); } }");
        // The converter generates: the out-call creates an IntHolder, reads back into val,
        // then the ref-call wraps val in a new IntHolder(val) seeded with the current value.
        AssertConversion(result, "IntHolder", "mutate(", "val = ");
    }

    [Fact]
    [Trait("Category", "EdgeCases")]
    public void OutVarTypeInference_ResolvesCorrectType()
    {
        var result = Convert("class C { public bool TryGet(out double x) { x = 1.5; return true; } public void Test() { TryGet(out var y); var z = y; } }");
        AssertConversion(result, "DoubleHolder", "double y = ", ".value");
    }
}
