using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IgnoreAttributeAnnotationTests
{
    [Fact]
    public void Convert_MethodWithIgnoreAttribute_AddsDisabledAnnotation()
    {
        const string code = """
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            public class SampleTests
            {
                [TestMethod]
                [Ignore]
                public void Skipped()
                {
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import org.junit.jupiter.api.Disabled;", result.GeneratedCode);
        Assert.Contains("@Test", result.GeneratedCode);
        Assert.Contains("@Disabled", result.GeneratedCode);
    }

    [Fact]
    public void Convert_ClassWithIgnoreAttribute_AddsDisabledAnnotation()
    {
        const string code = """
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            [TestClass]
            [Ignore]
            public class SampleTests
            {
                [TestMethod]
                public void Skipped()
                {
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import org.junit.jupiter.api.Disabled;", result.GeneratedCode);
        Assert.Contains("@Disabled", result.GeneratedCode);
        Assert.Contains("public class SampleTests", result.GeneratedCode);
    }
}