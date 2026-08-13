using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A public .NET serialization constructor (SerializationInfo, StreamingContext) in a
/// derived class forwards to base(info, context). The generated Java base class never
/// declares a serialization constructor (non-public ones are omitted), so that super(...)
/// call has no target and fails to compile. The derived ctor must instead emit a body that
/// throws PlatformNotSupportedException (mirroring the C# base's behavior).
/// </summary>
public class SerializationConstructorForwardingTests
{
    [Fact]
    public void DerivedPublicSerializationCtor_DoesNotForwardToMissingBaseCtor()
    {
        var result = Convert(@"
using System;
using System.Runtime.Serialization;
class Base {
    protected Base(SerializationInfo info, StreamingContext context) => throw new PlatformNotSupportedException();
}
class Derived : Base {
    public Derived(SerializationInfo info, StreamingContext context) : base(info, context) { }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.DoesNotContain("super(info, context)", code);
        Assert.Contains("throw new PlatformNotSupportedException();", code);
        Assert.Contains("Derived(", code);
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
