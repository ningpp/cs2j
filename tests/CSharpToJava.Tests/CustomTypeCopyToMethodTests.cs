using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// When a custom type has its own CopyTo method (not ICollection.CopyTo),
/// the converter must preserve it as a regular method call rather than
/// rewriting it to System.arraycopy(...toArray()...size()).
/// </summary>
public class CustomTypeCopyToMethodTests
{
    private readonly ITestOutputHelper _out;
    public CustomTypeCopyToMethodTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void CustomType_CopyToWithStringBuilder_NotRewrittenToArraycopy()
    {
        var r = Convert(@"
using System.Text;

class NodeData
{
    private string _value;
    private char[] _chars;
    private int _valueStartPos;
    private int _valueLength;

    internal void CopyTo(int valueOffset, StringBuilder sb)
    {
        if (_value == null)
        {
            sb.Append(_chars, _valueStartPos + valueOffset, _valueLength - valueOffset);
        }
        else
        {
            if (valueOffset <= 0)
            {
                sb.Append(_value);
            }
            else
            {
                sb.Append(_value, valueOffset, _value.Length - valueOffset);
            }
        }
    }
}

class Sample
{
    void Test()
    {
        var sb = new StringBuilder();
        var node = new NodeData();
        node.CopyTo(0, sb);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should preserve CopyTo as a regular method call, NOT System.arraycopy
        Assert.Contains("copyTo", code);
        Assert.DoesNotContain("System.arraycopy", code);
        Assert.DoesNotContain(".toArray()", code);
        Assert.DoesNotContain(".size()", code);
    }
}
