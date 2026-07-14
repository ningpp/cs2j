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
    public void ArrayLength_ConvertsToLengthField()
    {
        var result = Convert("class C { public int M(int[] a) { return a.Length; } }");
        AssertConversion(result, "return a.length;");
    }

    [Fact]
    public void ArrayInitializer()
    {
        var result = Convert("class C { public int M() { int[] a = new int[] { 1, 2, 3 }; return a.Length; } }");
        AssertConversion(result, "int[] a = new int[] { 1, 2, 3 };");
    }

    [Fact]
    public void ArrayInitializerShortForm()
    {
        var result = Convert("class C { public int[] M() { return new int[] { 4, 5 }; } }");
        AssertConversion(result, "return new int[] { 4, 5 };");
    }

    [Fact]
    public void JaggedArrayDeclaration()
    {
        var result = Convert("class C { public int M() { int[][] a = new int[2][]; a[0] = new int[3]; return a[0].Length; } }");
        AssertConversion(result, "int[][] a = new int[2][];", "a[0] = new int[3];", "return a[0].length;");
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
    public void ArrayAsMethodParameter()
    {
        var result = Convert("class C { public void M(int[] data) { var n = data.Length; } }");
        AssertConversion(result, "m(int[] data)", "data.length");
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
    public void ArraySortStatic_ConvertsToArraysSort()
    {
        var result = Convert("class C { public void M(int[] a) { System.Array.Sort(a); } }");
        AssertConversion(result, "Arrays.sort(a)");
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
    public void ArrayCopyStatic_ConvertsToArrayCopy()
    {
        var result = Convert("class C { public void M(int[] src, int[] dst) { System.Array.Copy(src, dst, 3); } }");
        AssertConversion(result, "System.arraycopy(src, 0, dst, 0, 3)");
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
    public void ArrayInForeachOverGenericList()
    {
        var result = Convert("class C { public int M(System.Collections.Generic.List<int> l) { int s = 0; foreach (var x in l) { s += x; } return s; } }");
        AssertConversion(result, "import io.github.ningpp.compat.CSharpList;", "for (int x : l)");
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
    public void ArrayLiteralInField()
    {
        var result = Convert("class C { public int[] Default = new int[] { 9, 8, 7 }; }");
        AssertConversion(result, "public int[] Default = new int[] { 9, 8, 7 };");
    }

    [Fact]
    public void ArrayLengthInLoopCondition()
    {
        var result = Convert("class C { public int M(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) { s += a[i]; } return s; } }");
        AssertConversion(result, "i < a.length;", "s += a[i];");
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
