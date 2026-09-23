using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A nested struct that uses the enclosing type's type parameters must declare them itself.
/// <para>
/// C# nested types implicitly capture the enclosing type's type parameters, but Java makes
/// nested types <c>static</c>, and a static nested type may not reference the enclosing
/// class's type variables ("cannot reference a non-static type variable T from a static
/// context"). The nested class path already added the captured parameters; the nested
/// <c>struct</c> path did not.
/// </para>
/// Reproduces HashSet.cs:1514 (<c>public struct Enumerator</c> inside <c>HashSet&lt;T&gt;</c>)
/// and List.cs (<c>Entry</c> / <c>Enumerator</c>).
/// </summary>
public class NestedStructEnclosingTypeParameterTests
{
    [Fact]
    public void NestedStructUsingOuterTypeParameter_DeclaresOwnTypeParameter()
    {
        var code = Convert(@"
class Box<T>
{
    public struct Enumerator
    {
        private T _current;
        public T Current => _current;
        public bool MoveNext() => true;
    }
}");

        Assert.Contains("class Enumerator<T>", code);
        Assert.DoesNotContain("class Enumerator implements", code);
        Assert.DoesNotContain("class Enumerator {", code);
    }

    [Fact]
    public void NestedStructWithOwnTypeParameter_KeepsOnlyOwnParameter()
    {
        var code = Convert(@"
class Box<T>
{
    public struct Wrapper<U>
    {
        public U Value;
    }
}");

        Assert.Contains("class Wrapper<U>", code);
        Assert.DoesNotContain("Wrapper<T, U>", code);
    }

    [Fact]
    public void NestedClassUsingOuterTypeParameter_StillDeclaresOwnTypeParameter()
    {
        var code = Convert(@"
class Box<T>
{
    private sealed class Node
    {
        public T Item;
        public Node Left;
    }
}");

        Assert.Contains("class Node<T>", code);
    }

    private static string Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        return result.GeneratedCode ?? "";
    }
}
