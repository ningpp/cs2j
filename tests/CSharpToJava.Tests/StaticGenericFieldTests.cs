using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class StaticGenericFieldTests
{
    [Fact]
    public void StaticFieldOfGenericTypeInGenericClass_IsConvertedToGenericStaticMethod()
    {
        var result = Convert("""
public class GenericHolder<T>
{
    public static readonly GenericHolder<T> Empty = new GenericHolder<T>();
    private T[] _items;

    public GenericHolder()
    {
        _items = new T[10];
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;
        System.Console.WriteLine("=== GENERATED CODE ===");
        System.Console.WriteLine(code);
        System.Console.WriteLine("=== END GENERATED CODE ===");

        // The static field must become a generic static method that takes the runtime Class<?> token,
        // because Java does not allow static members to reference the enclosing class's type parameters.
        Assert.Contains("public static <T> GenericHolder<T> Empty(Class<?> tClass)", code);
        Assert.Contains("return new GenericHolder<T>(tClass);", code);
        Assert.DoesNotContain("public static final GenericHolder<T> Empty", code);
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
