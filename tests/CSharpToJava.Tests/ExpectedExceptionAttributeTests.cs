using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class ExpectedExceptionAttributeTests
{
    private static ConversionResult Convert(string src, string file = "Sample.cs")
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = file,
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void ExpectedException_WrapsBodyWithTryCatchFlag()
    {
        var result = Convert(@"
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Foo {
    [TestClass]
    public class Bar {
        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public void M() {
            throw new InvalidOperationException();
        }
    }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("catch (IllegalStateException", result.GeneratedCode);
        Assert.Contains("Assert.fail(", result.GeneratedCode);
        Assert.Contains("boolean _cs2jExpectedThrown", result.GeneratedCode);
    }
}
