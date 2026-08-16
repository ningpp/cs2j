using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Verifies that explicit C# test timeouts (MSTest [Timeout] and xUnit
/// [Fact(Timeout = ...)]/[Theory(Timeout = ...)]) are preserved as JUnit
/// @Timeout annotations, and that no hard-coded timeout is invented.
/// </summary>
public class TestTimeoutAttributeMappingTests
{
    [Fact]
    public void MSTest_TestMethod_without_timeout_emits_no_junit_timeout()
    {
        var result = Convert("""
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            class MyTests
            {
                [TestMethod]
                public void TestWithoutTimeout()
                {
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("@Test", result.GeneratedCode);
        Assert.DoesNotContain("@Timeout", result.GeneratedCode);
        Assert.DoesNotContain("org.junit.jupiter.api.Timeout", result.GeneratedCode);
    }

    [Fact]
    public void MSTest_TimeoutAttribute_maps_to_junit_timeout_in_milliseconds()
    {
        var result = Convert("""
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            class MyTests
            {
                [TestMethod]
                [Timeout(500)]
                public void TestWithTimeout()
                {
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("@Timeout(value=500, unit=TimeUnit.MILLISECONDS)", result.GeneratedCode);
        Assert.Contains("import org.junit.jupiter.api.Timeout;", result.GeneratedCode);
        Assert.Contains("import java.util.concurrent.TimeUnit;", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Fact_Timeout_named_argument_maps_to_junit_timeout()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact(Timeout = 500)]
                public void TestWithTimeout()
                {
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("@Timeout(value=500, unit=TimeUnit.MILLISECONDS)", result.GeneratedCode);
        Assert.Contains("import org.junit.jupiter.api.Timeout;", result.GeneratedCode);
        Assert.Contains("import java.util.concurrent.TimeUnit;", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Theory_Timeout_named_argument_maps_to_junit_timeout()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Theory(Timeout = 500)]
                [InlineData(1)]
                public void TestWithTimeout(int value)
                {
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("@Timeout(value=500, unit=TimeUnit.MILLISECONDS)", result.GeneratedCode);
        Assert.Contains("import org.junit.jupiter.api.Timeout;", result.GeneratedCode);
        Assert.Contains("import java.util.concurrent.TimeUnit;", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Fact_Timeout_zero_emits_no_timeout()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact(Timeout = 0)]
                public void TestWithoutTimeout()
                {
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("@Test", result.GeneratedCode);
        Assert.DoesNotContain("@Timeout", result.GeneratedCode);
        Assert.DoesNotContain("org.junit.jupiter.api.Timeout", result.GeneratedCode);
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
