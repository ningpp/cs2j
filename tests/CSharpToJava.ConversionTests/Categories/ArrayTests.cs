using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# arrays (single-dimensional, jagged, multi-dimensional)
/// to Java arrays.
/// </summary>
public class ArrayTests : ConversionTestBase
{
    [Fact]
    public void SingleDimArrayDeclaration()
    {
        var result = Convert("class C { public int[] Make() { int[] a = new int[3]; return a; } }");
        AssertConversion(result, "int[] a = new int[3];");
    }

    [Fact]
    public void SingleDimArrayIndexing()
    {
        var result = Convert("class C { public int M() { int[] a = new int[3]; a[0] = 1; return a[0]; } }");
        AssertConversion(result, "a[0] = 1;", "return a[0];");
    }

    [Fact]
    public void JaggedArrayElementAccess()
    {
        var result = Convert("class C { public int M() { int[][] a = new int[2][]; a[0] = new int[3]; a[0][1] = 5; return a[0][1]; } }");
        AssertConversion(result, "a[0][1] = 5;", "return a[0][1];");
    }

    [Fact]
    public void ArrayForeach_ConvertsToEnhancedFor()
    {
        var result = Convert("class C { public int M(int[] a) { int s = 0; foreach (var x in a) { s += x; } return s; } }");
        AssertConversion(result, "for (int x : a)");
    }

    [Fact]
    public void ArrayForeachWithExplicitType()
    {
        var result = Convert("class C { public void M(string[] a) { foreach (string s in a) { var x = s; } } }");
        AssertConversion(result, "for (String s : a)");
    }

    [Fact]
    public void StringArrayField()
    {
        var result = Convert("class C { public string[] Names; }");
        AssertConversion(result, "public String[] Names;");
    }

    [Fact]
    public void IntArrayField()
    {
        var result = Convert("class C { public int[] Values; }");
        AssertConversion(result, "public int[] Values;");
    }

    [Fact]
    public void ArrayElementMutation()
    {
        var result = Convert("class C { public void M(int[] a) { a[2] = a[2] + 1; } }");
        AssertConversion(result, "a[2] = a[2] + 1;");
    }

    [Fact]
    public void MultiDimArrayDeclaration()
    {
        var result = Convert("class C { public int M() { int[,] a = new int[2, 3]; a[0, 0] = 1; return a[0, 0]; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "[,]", "C# multidimensional array syntax must be removed");
    }

    [Fact]
    public void ArrayIndexOfStatic_ConvertsToHelper()
    {
        var result = Convert("class C { public int M(int[] a) { return System.Array.IndexOf(a, 5); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "indexOf");
    }

    [Fact]
    public void ArrayReverseStatic_ConvertsToArraysReverse()
    {
        var result = Convert("class C { public void M(int[] a) { System.Array.Reverse(a); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "reverse");
    }

    [Fact]
    public void ArrayClearStatic()
    {
        var result = Convert("class C { public void M(int[] a) { System.Array.Clear(a, 0, a.Length); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void ArrayResizeStatic()
    {
        var result = Convert("class C { public void M(ref int[] a) { System.Array.Resize(ref a, 10); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void EmptyArrayAllocation()
    {
        var result = Convert("class C { public int[] M() { return new int[0]; } }");
        AssertConversion(result, "return new int[0];");
    }

    [Fact]
    public void ArrayEmpty_PrimitiveElementType()
    {
        var result = Convert("class C { public int[] M() { return System.Array.Empty<int>(); } }");
        AssertConversion(result, "return new int[0];");
    }

    [Fact]
    public void ArrayEmpty_ReferenceElementType_UsesTypedEmpty()
    {
        // no-arg CSharpArray.empty() 返回共享 Object[0]，赋给具体数组类型的字段/局部
        // 时调用点 checkcast 会抛 ClassCastException，必须带元素类型 Class token。
        var result = Convert("class C { public string[] M() { return System.Array.Empty<string>(); } }");
        AssertConversion(result, "return CSharpArray.empty(String.class);");
    }

    [Fact]
    public void ArrayEmpty_GenericElementType_StripsTypeArguments()
    {
        var result = Convert("class C { public System.Collections.Generic.List<int>[] M() { return System.Array.Empty<System.Collections.Generic.List<int>>(); } }");
        AssertConversion(result, "return CSharpArray.empty(CSharpList.class);");
    }

    [Fact]
    public void ArrayEmpty_TypeParameterElementType_KeepsErasureSafeEmpty()
    {
        // 类型参数无法取 .class；其 T[] 目标擦除为 Object[]，no-arg empty() 行为正确。
        var result = Convert("class C { public T[] M<T>() { return System.Array.Empty<T>(); } }");
        AssertConversion(result, "return CSharpArray.empty();");
    }

    [Fact]
    public void CharArrayField()
    {
        var result = Convert("class C { public char[] Chars; }");
        AssertConversion(result, "public char[] Chars;");
    }

    [Fact]
    public void BoolArrayDeclaration()
    {
        var result = Convert("class C { public bool[] M() { return new bool[4]; } }");
        AssertConversion(result, "return new boolean[4];");
    }

    [Fact]
    public void DoubleArrayDeclaration()
    {
        var result = Convert("class C { public double[] M() { return new double[2]; } }");
        AssertConversion(result, "return new double[2];");
    }

    [Fact]
    public void ArrayOfCustomType()
    {
        var result = Convert("class Item { } class C { public Item[] M() { return new Item[3]; } }");
        AssertConversion(result, "Item[] m()", "return new Item[3];");
    }

    [Fact]
    public void ArrayAssignmentBetweenVars()
    {
        var result = Convert("class C { public int[] M(int[] src) { int[] dst = src; return dst; } }");
        AssertConversion(result, "int[] dst = src;", "return dst;");
    }

    [Fact]
    public void NestedArrayIndexInExpression()
    {
        var result = Convert("class C { public int M(int[][] a) { return a[1][2] + 1; } }");
        AssertConversion(result, "return a[1][2] + 1;");
    }

    [Fact]
    public void ArrayExistsMethod()
    {
        var result = Convert("class C { public bool M(int[] a) { return System.Array.Exists(a, x => x > 0); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void ArrayFindMethod()
    {
        var result = Convert("class C { public int M(int[] a) { return System.Array.Find(a, x => x > 0); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void ArrayTrueForAllMethod()
    {
        var result = Convert("class C { public bool M(int[] a) { return System.Array.TrueForAll(a, x => x > 0); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void ArrayConvertAllMethod()
    {
        var result = Convert("class C { public string[] M(int[] a) { return System.Array.ConvertAll(a, x => x.ToString()); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void JaggedArrayForeach()
    {
        var result = Convert("class C { public int M(int[][] a) { int s = 0; foreach (var row in a) { foreach (var v in row) { s += v; } } return s; } }");
        AssertConversion(result, "for (int[] row : a)", "for (int v : row)");
    }

    [Fact]
    public void ArrayAsReturnTypeOfGeneric()
    {
        var result = Convert("class C { public int[] M(System.Collections.Generic.List<int> l) { return l.ToArray(); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "toArray");
    }

    [Fact]
    public void CollectionExpressionToArrayField_GeneratesArrayInitializer()
    {
        var result = Convert("class C { public int[] Values = [1, 2, 3]; }");
        AssertConversion(result, "new int[]{ 1, 2, 3 }");
        AssertJavaDoesNotContain(result, "List.of", "Array-typed collection expression must generate array initializer, not List.of");
    }

    [Fact]
    public void CollectionExpressionToStringArrayField_GeneratesArrayInitializer()
    {
        var result = Convert("class C { public string[] Names = [\"a\", \"b\"]; }");
        AssertConversion(result, "new String[]{ \"a\", \"b\" }");
        AssertJavaDoesNotContain(result, "List.of", "String[] collection expression must generate array initializer");
    }
}
