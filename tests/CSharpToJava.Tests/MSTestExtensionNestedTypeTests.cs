using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class MSTestExtensionNestedTypeTests
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
    public void TestContextProperty_WithNestedClasses_StillEmitsExtendWith()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Foo {
    [TestClass]
    public class Outer {
        public TestContext TestContext { get; set; }
        [TestMethod]
        public void M() {
            TestContext.WriteLine(""hi"");
        }
        sealed class HelperA {
            public int X { get; set; }
        }
        sealed class HelperB {
            public string Y { get; set; }
        }
    }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("ExtendWith", result.GeneratedCode);
        Assert.Contains("MSTestExtension.class", result.GeneratedCode);
    }
}
