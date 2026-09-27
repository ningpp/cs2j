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

    /// <summary>
    /// Real-world regression from converting the dotnet/runtime collections
    /// (System.Collections project): the project lives in namespace
    /// <c>dotnet.System.Collections.Generic</c> AND defines its own
    /// <c>dotnet.System.Collections.ICollection</c>. After the pipeline strips
    /// <c>global::</c>, the specifier <c>System.Collections.ICollection</c> resolves
    /// relatively to the PROJECT-LOCAL interface; the resulting symbol mismatch
    /// leaves ExplicitInterfaceImplementations empty while Roslyn reports the FULLY
    /// QUALIFIED name ("dotnet.System.Collections.ICollection.IsSynchronized") as
    /// IPropertySymbol.Name. The emitted accessor must still be getIsSynchronized() —
    /// a name containing dots is illegal in Java and failed javac.
    /// </summary>
    [Fact]
    public void Class_LocalShadowNamespace_BindingDrift_StillUsesSyntaxAccessorName()
    {
        var result = Convert(@"
namespace dotnet.System.Collections
{
    public interface ICollection
    {
        bool IsSynchronized { get; }
        object SyncRoot { get; }
    }
}

namespace dotnet.System.Collections.Generic
{
    public class MyStack
    {
        bool global::System.Collections.ICollection.IsSynchronized
        {
            get { return false; }
        }

        object global::System.Collections.ICollection.SyncRoot => this;
    }
}
");
        _output.WriteLine(result.GeneratedCode);

        Assert.Contains("boolean getIsSynchronized()", result.GeneratedCode);
        Assert.Contains("Object getSyncRoot()", result.GeneratedCode);
        Assert.DoesNotContain("getDotnet.", result.GeneratedCode);
        Assert.DoesNotContain("ICollection.IsSynchronized()", result.GeneratedCode);
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
