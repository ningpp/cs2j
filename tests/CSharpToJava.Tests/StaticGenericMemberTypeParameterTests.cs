using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# lets a static member of a generic type use the type parameters of its
/// enclosing type; Java forbids static members from referencing class type
/// parameters. The converter must promote the referenced enclosing type
/// parameters to method-level type parameters (and only the referenced ones).
/// </summary>
public class StaticGenericMemberTypeParameterTests
{
    [Fact]
    public void StaticMethodUsingClassTypeParameter_DeclaresMethodTypeParameter()
    {
        var result = Convert("""
            public class Box<T>
            {
                public T Value;

                public static Box<T> Combine(Box<T> a, Box<T> b)
                {
                    return a;
                }
            }
            """);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;
        Assert.Contains("public static <T> Box<T> combine(Box<T> a, Box<T> b)", code);
        Assert.DoesNotContain("public static Box<T> combine(", code);
    }

    [Fact]
    public void StaticMethodUsingClassTypeParameterOnlyInBody_DeclaresMethodTypeParameter()
    {
        var result = Convert("""
            public class Box<T>
            {
                public T Value;

                public static T GetDefault()
                {
                    T local = default(T);
                    T[] array = new T[0];
                    return local;
                }
            }
            """);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;
        Assert.Contains("public static <T> T getDefault()", code);
        Assert.Contains("T local = null;", code);
    }

    [Fact]
    public void StaticMethod_OnlyDeclaresReferencedClassTypeParameters()
    {
        var result = Convert("""
            public class Pair<T, U>
            {
                public T First;
                public U Second;

                public static T Pick(Pair<T, U> pair)
                {
                    return pair.First;
                }

                public static int Count()
                {
                    return 0;
                }
            }
            """);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;
        Assert.Contains("public static <T, U> T pick(Pair<T, U> pair)", code);
        Assert.DoesNotContain("U pick(", code);
        Assert.Contains("public static int count()", code);
        Assert.DoesNotContain("static <T> int count(", code);
    }

    [Fact]
    public void StaticPropertyUsingClassTypeParameter_DeclaresMethodTypeParameter()
    {
        var result = Convert("""
            public class Box<T>
            {
                public static T Default => default(T);
            }
            """);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;
        Assert.Contains("public static <T> T getDefault()", code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
