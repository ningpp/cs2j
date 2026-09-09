using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that call sites of generic methods with trailing Class&lt;?&gt;
/// parameters (added because the method body creates new T[] or similar)
/// correctly emit the Class literal argument at the call site.
/// </summary>
public class TrailingClassTokenCallSiteTests
{
    [Fact]
    public void GenericMethodCall_WithTrailingClassParam_EmitsClassLiteral()
    {
        // Repeat<T> has a trailing Class<?> parameter because its body creates
        // new T[count] (an array of T). The call site Repeat<string>("x", 2)
        // must emit repeat("x", 2, String.class).
        var result = Convert("""
class Demo {
    public T[] Repeat<T>(T value, int count)
    {
        return new T[count];
    }
}

class UseDemo {
    public string[] Run()
    {
        return new Demo().Repeat<string>("x", 2);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The call Repeat<string>("x", 2) should emit String.class as trailing arg
        Assert.Contains("String.class", code);
    }

    [Fact]
    public void GenericMethodCall_WithIntTypeArg_EmitsIntegerClassLiteral()
    {
        var result = Convert("""
class Demo {
    public T[] Repeat<T>(T value, int count)
    {
        return new T[count];
    }
}

class UseDemo {
    public int[] Run()
    {
        return new Demo().Repeat<int>(0, 2);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The call Repeat<int>(0, 2) should emit a Class literal as trailing arg
        // (int.class or Integer.class — both are valid for Class<?>)
        Assert.Matches(@"(int|Integer)\.class", code);
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
