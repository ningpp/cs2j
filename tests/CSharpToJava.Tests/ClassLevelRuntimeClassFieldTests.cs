using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that typeof(T) / default(T) with a CLASS-level type parameter uses the
/// class runtime Class field instead of shadowing the class T with a
/// method-level type parameter. NullableConverter.Read previously became
/// `&lt;T&gt; T read(Class&lt;T&gt; _cs2j_T, ...)` — shadowing the class T, failing
/// the abstract override and producing "T cannot be converted to T".
/// </summary>
public class ClassLevelRuntimeClassFieldTests
{
    [Fact]
    public void TypeOfOnClassTypeParameter_UsesClassField_NotMethodTypeParam()
    {
        var result = Convert(@"
abstract class Base<T>
{
    public abstract T Read();
}
class Derived<T> : Base<T>
{
    public override T Read()
    {
        System.Type t = typeof(T);
        return default(T);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("public T read()", code);
        Assert.DoesNotContain("<T> T read(", code);
        Assert.DoesNotContain("_cs2j_T", code);
        Assert.Contains("tClass", code);
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
