using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// The read-only ref-struct optimization (pass the struct by value, skipping the
/// ObjectHolder wrapper) decides per method BODY, which breaks virtual dispatch:
/// if one override mutates the ref parameter (holder shape) while a sibling
/// override is read-only (raw shape), the raw version becomes a Java OVERLOAD, not
/// an override — calls dispatch to the base and throw. Regression: all
/// JsonConverter&lt;T&gt;.ReadWithQuotes overrides except CharConverter degraded to
/// overloads, so dictionary-key reads (76 errors) hit the base's
/// InvalidOperationException. Every member of an override family must take the
/// ObjectHolder shape when any member mutates the parameter.
/// </summary>
public class RefStructVirtualFamilyHolderTests
{
    [Fact]
    public void ReadOnlyOverride_TakesHolderShape_WhenSiblingMutates()
    {
        var result = Convert(@"
struct Reader { }

class Base
{
    public virtual int Read(ref Reader reader) { throw new System.InvalidOperationException(); }
}

class Mutating : Base
{
    public override int Read(ref Reader reader) { reader = new Reader(); return 1; }
}

class ReadOnly : Base
{
    public override int Read(ref Reader reader) { return 2; }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The mutating override requires the holder; the read-only sibling must match.
        Assert.Contains("ObjectHolder<Reader>", code, StringComparison.Ordinal);
        // The raw (by-value) shape on an override would be a Java overload — virtual
        // dispatch would silently fall to the base and throw.
        Assert.DoesNotContain("read(Reader reader)", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
