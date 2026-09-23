using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for explicit interface property implementations.
///
/// <para>C# explicit implementations (e.g.
/// <c>bool global::System.Collections.ICollection.IsSynchronized { get { return false; } }</c>,
/// <c>object global::System.Collections.ICollection.SyncRoot => this;</c>) are private in C#
/// and reached through interface casts, but Java classes satisfy an interface contract with
/// public methods carrying the interface's exact accessor names. StructTransformer used to
/// blanket-skip EVERY explicit interface property (assuming a public counterpart already
/// existed); when the property existed ONLY as an explicit implementation, the converted
/// struct was missing getIsSynchronized()/getSyncRoot() and failed to implement the
/// interface. The bug is independent of the <c>global::</c> qualifier — short interface
/// names were skipped just the same.</para>
/// </summary>
public class ExplicitInterfacePropertyEmissionTests
{
    private readonly ITestOutputHelper _output;

    public ExplicitInterfacePropertyEmissionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Struct_GlobalQualifiedExplicitInterfaceProperties_AreEmitted()
    {
        var result = Convert(@"
using System.Collections;
public struct MyList : global::System.Collections.ICollection
{
    public int Count => 0;
    public global::System.Collections.IEnumerator GetEnumerator() => null;

    bool global::System.Collections.ICollection.IsSynchronized
    {
        get { return false; }
    }

    object global::System.Collections.ICollection.SyncRoot => this;
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        _output.WriteLine(result.GeneratedCode);

        Assert.Contains("public boolean getIsSynchronized()", result.GeneratedCode);
        Assert.Contains("return false;", result.GeneratedCode);
        Assert.Contains("public Object getSyncRoot() { return this; }", result.GeneratedCode);
    }

    [Fact]
    public void Struct_ShortNameExplicitInterfaceProperties_AreEmitted()
    {
        var result = Convert(@"
using System.Collections;
public struct MyList : ICollection
{
    public int Count => 0;
    public IEnumerator GetEnumerator() => null;

    bool ICollection.IsSynchronized
    {
        get { return false; }
    }

    object ICollection.SyncRoot => this;
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        _output.WriteLine(result.GeneratedCode);

        Assert.Contains("getIsSynchronized", result.GeneratedCode);
        Assert.Contains("getSyncRoot", result.GeneratedCode);
    }

    [Fact]
    public void Struct_PublicAndExplicitSameReturnType_KeepsSingleAccessor()
    {
        // The original reason for skipping explicit properties: when a public property of
        // the same name/type already exists, its accessor satisfies the interface contract.
        // The redundant explicit accessor must be deduplicated, not emitted twice.
        var result = Convert(@"
using System.Collections;
public struct MyList : ICollection
{
    public int Count => 0;
    public IEnumerator GetEnumerator() => null;

    public bool IsSynchronized => false;
    bool ICollection.IsSynchronized => IsSynchronized;

    public object SyncRoot => this;
    object ICollection.SyncRoot => SyncRoot;
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        _output.WriteLine(result.GeneratedCode);

        Assert.Equal(1, CountOccurrences(result.GeneratedCode, " getIsSynchronized()"));
        Assert.Equal(1, CountOccurrences(result.GeneratedCode, " getSyncRoot()"));
    }

    [Fact]
    public void Struct_PublicAndExplicitDifferentReturnType_ResolvesNameClash()
    {
        var result = Convert(@"
using System.Collections;
public class SyncToken { }
public struct MyList : ICollection
{
    public int Count => 0;
    public IEnumerator GetEnumerator() => null;

    public SyncToken SyncRoot => new SyncToken();
    object ICollection.SyncRoot => SyncRoot;

    bool ICollection.IsSynchronized => false;
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        _output.WriteLine(result.GeneratedCode);

        // The Object-returning explicit accessor keeps the interface contract name and
        // delegates to the typed class accessor, which is renamed to getSyncRoot$Class().
        Assert.Equal(1, CountOccurrences(result.GeneratedCode, " getSyncRoot()"));
        Assert.Contains("Object getSyncRoot() { return getSyncRoot$Class(); }", result.GeneratedCode);
        Assert.Contains("SyncToken getSyncRoot$Class()", result.GeneratedCode);
        Assert.Contains("getIsSynchronized", result.GeneratedCode);
    }

    [Fact]
    public void Class_GlobalQualifiedExplicitInterfaceProperties_AreEmitted()
    {
        // Parity check: classes already emitted these; structs must now behave the same.
        var result = Convert(@"
using System.Collections;
public class MyList : global::System.Collections.ICollection
{
    public int Count => 0;
    public global::System.Collections.IEnumerator GetEnumerator() => null;

    bool global::System.Collections.ICollection.IsSynchronized
    {
        get { return false; }
    }

    object global::System.Collections.ICollection.SyncRoot => this;
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        _output.WriteLine(result.GeneratedCode);

        Assert.Contains("getIsSynchronized", result.GeneratedCode);
        Assert.Contains("getSyncRoot", result.GeneratedCode);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
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
