using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# generics: type parameter constraints, generic methods, and generic classes.
/// </summary>
public class GenericsTests : ConversionTestBase
{
    [Fact]
    public void ConstraintClass_Dropped()
    {
        var result = Convert("class C<T> where T : class { }");
        AssertConversion(result, "public class C<T> {");
        AssertJavaDoesNotContain(result, "extends", "C# 'where T : class' must be dropped in Java");
    }

    [Fact]
    public void ConstraintStruct_Dropped()
    {
        var result = Convert("class C<T> where T : struct { }");
        AssertConversion(result, "public class C<T> {");
        AssertJavaDoesNotContain(result, "extends");
    }

    [Fact]
    public void ConstraintNew_Dropped()
    {
        var result = Convert("class C<T> where T : new() { }");
        AssertConversion(result, "public class C<T> {");
        AssertJavaDoesNotContain(result, "extends");
    }

    [Fact]
    public void MultiConstraintClassNew_Dropped()
    {
        var result = Convert("class C<T> where T : class, new() { }");
        AssertConversion(result, "public class C<T> {");
        AssertJavaDoesNotContain(result, "extends");
    }

    [Fact]
    public void ConstraintInterface_ConvertsToExtends()
    {
        var result = Convert("class C<T> where T : System.IComparable { }");
        AssertConversion(result, "public class C<T extends Comparable> {");
    }

    [Fact]
    public void ConstraintBaseClass_ConvertsToExtends()
    {
        var result = Convert("class C<T> where T : B { } class B { }");
        AssertConversion(result, "public class C<T extends B> {", "public class B {");
    }

    [Fact]
    public void ConstraintMultiple_ConvertsToExtendsAnd()
    {
        var result = Convert("class C<T> where T : B, System.IComparable { } class B { }");
        AssertConversion(result, "public class C<T extends B & Comparable> {");
    }

    [Fact]
    public void GenericMethod_ConvertsToJavaGenericMethod()
    {
        var result = Convert("class C { public T M<T>(T x) => x; }");
        AssertConversion(result, "public <T> T m(T x) { return x; }");
    }

    [Fact]
    public void GenericMethodWithConstraint_Converts()
    {
        var result = Convert("class C { public T M<T>(T x) where T : class => x; }");
        AssertConversion(result, "public <T> T m(T x) { return x; }");
        AssertJavaDoesNotContain(result, "where", "C# 'where' constraint must be dropped");
    }

    [Fact]
    public void GenericClass_ConvertsToJavaGenericClass()
    {
        var result = Convert("class C<T> { public T V; }");
        AssertConversion(result, "public class C<T> {", "public T V;");
    }

    [Fact]
    public void GenericClassWithInterface_ConvertsToExtends()
    {
        var result = Convert("class C<T> where T : System.IComparable { public T V; }");
        AssertConversion(result, "public class C<T extends Comparable> {", "public T V;");
    }

    [Fact]
    public void BoundedGenericList_ConvertsToParameterizedType()
    {
        var result = Convert("class C { public void M(System.Collections.Generic.List<int> l) { } }");
        AssertConversion(result, "public void m(CSharpList<Integer> l) {");
    }

    [Fact]
    public void GenericDictionary_ConvertsToCSharpDictionary()
    {
        var result = Convert("class C { public void M(System.Collections.Generic.Dictionary<string, int> d) { } }");
        AssertConversion(result, "public void m(CSharpDictionary<String, Integer> d) {");
    }
}
