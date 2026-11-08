using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// xUnit/MSTest 特性 → JUnit 5 注解的转换回归测试（迭代 5）。
/// </summary>
public class JUnitTestAnnotationConversionTests
{
    private const string TestSource = """
        using System;
        using System.Collections.Generic;
        using Xunit;

        namespace Xunit
        {
            public class FactAttribute : Attribute { public string Skip { get; set; } }
            public class TheoryAttribute : Attribute { }
            public class InlineDataAttribute : Attribute { public InlineDataAttribute(params object[] data) { } }
            public class MemberDataAttribute : Attribute
            {
                public MemberDataAttribute(string name) { }
                public Type MemberType { get; set; }
            }
            public class PlatformSpecificAttribute : Attribute { public PlatformSpecificAttribute(string platform) { } }
        }

        public static class SampleTests
        {
            public static IEnumerable<object[]> Data()
            {
                return new object[][]
                {
                    new object[] { 1, "a" },
                    new object[] { 2, null },
                };
            }

            [Fact]
            public static void Simple() { }

            [Theory]
            [InlineData(1, "a")]
            [InlineData(2, null)]
            public static void Inline(int value, string text) { }

            [Theory]
            [MemberData(nameof(Data))]
            public static void Member(int value, string text) { }

            [Theory]
            [PlatformSpecific("Linux")]
            [InlineData(1)]
            public static void LinuxOnly(int value) { }

            [Fact(Skip = "not now")]
            public static void Skipped() { }
        }
        """;

    private const string MstestSource = """
        using System;
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        namespace Microsoft.VisualStudio.TestTools.UnitTesting
        {
            public class TestMethodAttribute : Attribute { }
            public class DataTestMethodAttribute : Attribute { }
            public class DataRowAttribute : Attribute { public DataRowAttribute(params object[] data) { } }
        }

        public class MstestSampleTests
        {
            [TestMethod]
            public void Plain() { }

            [DataTestMethod]
            [DataRow(1)]
            [DataRow(2)]
            public void Single(int weight) { }

            [DataTestMethod]
            [DataRow(1.0, "a")]
            public void Pair(double weight, string text) { }
        }
        """;

    /// <summary>
    /// xUnit 允许 MemberData 行省略尾部可选参数并填声明的默认值（真实链：
    /// Utf8JsonReaderTests.InvalidJson 的 maxDepth = 64）；JUnit 不做补齐。
    /// </summary>
    private const string OptionalParameterTheorySource = """
        using System;
        using System.Collections.Generic;
        using Xunit;

        namespace Xunit
        {
            public class TheoryAttribute : Attribute { }
            public class MemberDataAttribute : Attribute
            {
                public MemberDataAttribute(string name) { }
            }
        }

        public static class OptionalTheoryTests
        {
            public static IEnumerable<object[]> Rows()
            {
                return new object[][]
                {
                    new object[] { "{}", 1, 2 },
                };
            }

            [Theory]
            [MemberData(nameof(Rows))]
            public static void Invalid(string json, int line, int position, int maxDepth = 64) { }
        }
        """;

    private static async Task<string> ConvertAsync(string source)
    {
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[] { new SourceFile { FilePath = "Test.cs", Content = source } },
                projectName: "Demo");
        foreach (var failure in results.Where(r => !r.Success))
            throw new Xunit.Sdk.XunitException(
                string.Join("\n", failure.Diagnostics) + "\n---\n" + failure.GeneratedCode);
        return string.Join("\n", results.Select(r => r.GeneratedCode));
    }

    [Fact]
    public async Task Fact_BecomesInstanceJUnitTest()
    {
        var code = await ConvertAsync(TestSource);

        Assert.Contains("@Test", code);
        Assert.Contains("public void simple()", code);
        Assert.DoesNotContain("public static void simple()", code);
    }

    [Fact]
    public async Task StaticTestClass_GetsAccessibleConstructor()
    {
        var code = await ConvertAsync(TestSource);

        Assert.DoesNotContain("private SampleTests()", code);
    }

    [Fact]
    public async Task Theory_InlineData_EmitsMethodSourceProvider()
    {
        var code = await ConvertAsync(TestSource);

        Assert.Contains("@ParameterizedTest", code);
        Assert.Contains("@MethodSource(\"__cs2jData_inline\")", code);
        Assert.Contains("private static java.util.stream.Stream<Arguments> __cs2jData_inline()", code);
        Assert.Contains("Arguments.of(1, \"a\")", code);
        Assert.Contains("Arguments.of(2, (Object) null)", code);
    }

    /// <summary>
    /// xUnit converts theory-row values to the declared parameter type (long→int,
    /// enum constant→long); JUnit only widens. Rows with numeric/enum parameters
    /// must go through XunitArguments.coerce.
    /// </summary>
    [Fact]
    public async Task Theory_NumericParameters_CoerceRowValues()
    {
        var code = await ConvertAsync("""
            using System;
            using System.Collections.Generic;
            using Xunit;

            namespace Xunit
            {
                public class TheoryAttribute : Attribute { }
                public class MemberDataAttribute : Attribute
                {
                    public MemberDataAttribute(string name) { }
                }
            }

            public static class NumericTheoryTests
            {
                public static IEnumerable<object[]> Rows()
                {
                    return new object[][] { new object[] { 5L } };
                }

                [Theory]
                [MemberData(nameof(Rows))]
                public static void TakesInt(int value) { }
            }
            """);

        Assert.Contains("XunitArguments.coerce(", code, StringComparison.Ordinal);
        Assert.Contains("new Class[]{int.class}", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Theory_MemberData_ShortRow_PadsTrailingOptionalParameter()
    {
        var code = await ConvertAsync(OptionalParameterTheorySource);

        Assert.Contains("XunitArguments.pad(__row, 3, 64)", code, StringComparison.Ordinal);
        Assert.Contains("import csharp.xunit.XunitArguments;", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Theory_MemberData_EnumeratesCompatIterable()
    {
        var code = await ConvertAsync(TestSource);

        Assert.Contains("@MethodSource(\"__cs2jData_member\")", code);
        Assert.Contains("private static java.util.stream.Stream<Arguments> __cs2jData_member()", code);
        Assert.Contains(".iterator()", code);
        Assert.Contains("moveNext()", code);
    }

    [Fact]
    public async Task PlatformSpecific_NonWindows_IsDisabled()
    {
        var code = await ConvertAsync(TestSource);

        var linuxMethod = code[Math.Max(0, code.IndexOf("linuxOnly(", StringComparison.Ordinal) - 120)..];
        Assert.Contains("@Disabled", linuxMethod);
        Assert.Contains("@ParameterizedTest", linuxMethod);
    }

    [Fact]
    public async Task DataTestMethod_DataRows_EmitMethodSourceProvider()
    {
        var code = await ConvertAsync(MstestSource);

        Assert.Contains("@ParameterizedTest", code);
        Assert.Contains("@MethodSource(\"__cs2jData_single\")", code);
        Assert.Contains("private static java.util.stream.Stream<Arguments> __cs2jData_single()", code);
        Assert.Contains("Arguments.of(1)", code);
        Assert.Contains("Arguments.of(2)", code);
        Assert.Contains("@MethodSource(\"__cs2jData_pair\")", code);
        Assert.Contains("private static java.util.stream.Stream<Arguments> __cs2jData_pair()", code);
        Assert.Contains("Arguments.of(1.0, \"a\")", code);
    }

    [Fact]
    public async Task TestMethod_StaysPlainJUnitTest()
    {
        var code = await ConvertAsync(MstestSource);

        var plainIndex = code.IndexOf("plain(", StringComparison.Ordinal);
        var plainMethod = code[Math.Max(0, plainIndex - 120)..Math.Min(code.Length, plainIndex + 30)];
        Assert.Contains("@Test", plainMethod);
        Assert.DoesNotContain("@ParameterizedTest", plainMethod);
    }

    [Fact]
    public async Task FactSkip_IsDisabled()
    {
        var code = await ConvertAsync(TestSource);

        var skippedMethod = code[Math.Max(0, code.IndexOf("skipped(", StringComparison.Ordinal) - 80)..];
        Assert.Contains("@Disabled", skippedMethod);
    }
}
