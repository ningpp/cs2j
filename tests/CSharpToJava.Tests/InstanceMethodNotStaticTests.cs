using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Instance method calls on fields must keep the field name as receiver,
/// not be replaced with the type name (which would be a static call).
/// </summary>
public class InstanceMethodNotStaticTests
{
    private readonly ITestOutputHelper _out;
    public InstanceMethodNotStaticTests(ITestOutputHelper output) { _out = output; }

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
    public void FieldClose_NotTreatedAsStaticCall()
    {
        var r = Convert(@"
using System;
using System.IO;
class Sample : IDisposable {
    readonly StreamReader streamReader;
    Sample(Stream s) { streamReader = new StreamReader(s); }
    protected virtual void Dispose(bool disposing) {
        if (disposing)
            streamReader.Close();
    }
    public void Dispose() { Dispose(true); }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Must be instance call on field, not static call on type
        Assert.Contains("streamReader.close()", code);
        Assert.DoesNotContain("StreamReader.close()", code);
    }
}
