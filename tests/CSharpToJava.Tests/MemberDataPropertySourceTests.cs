using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# [MemberData(nameof(XxxData))] referencing a property
/// generates @MethodSource with the correct getter method name (get + PascalCase),
/// matching the PropertyTransformer's getter naming convention.
/// </summary>
public class MemberDataPropertySourceTests
{
    [Fact]
    public void MemberData_ReferencingProperty_UsesGetterNameInMethodSource()
    {
        var result = Convert(@"
using System.Collections.Generic;
using Xunit;

public class MyTests {
    public static IEnumerable<object[]> MyTestData {
        get {
            yield return new object[] { ""hello"" };
        }
    }

    [Theory]
    [MemberData(nameof(MyTestData))]
    public void TestSomething(string input) {
        Assert.NotNull(input);
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // PropertyTransformer generates getter as "get" + PascalCase(propName)
        // So @MethodSource must reference "getMyTestData", not "myTestData"
        Assert.Contains("getMyTestData", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MethodSource(\"getMyTestData\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberData_ReferencingMethod_UsesCamelCaseInMethodSource()
    {
        var result = Convert(@"
using System.Collections.Generic;
using Xunit;

public class MyTests {
    public static IEnumerable<object[]> MyMethodData() {
        yield return new object[] { ""hello"" };
    }

    [Theory]
    [MemberData(nameof(MyMethodData))]
    public void TestSomething(string input) {
        Assert.NotNull(input);
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // For methods, @MethodSource should use camelCase (no "get" prefix)
        Assert.Contains("MethodSource(\"myMethodData\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberData_WithMemberType_UsesFullyQualifiedMethodSource()
    {
        var result = Convert(@"
using System.Collections.Generic;
using Xunit;

namespace MyApp.Tests
{
    public class DataSource
    {
        public static IEnumerable<object[]> InvalidValues()
        {
            yield return new object[] { ""x"" };
        }
    }

    public class MyTests
    {
        [Theory]
        [MemberData(nameof(DataSource.InvalidValues), MemberType = typeof(DataSource))]
        public void TestSomething(string input)
        {
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // JUnit requires "fully.qualified.Class#method" when the data method is
        // declared on a different type than the test class.
        Assert.Contains("DataSource#invalidValues", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MethodSource(\"invalidValues\")", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberData_WithOptionalParameter_PadsRowsWithDefault()
    {
        var result = Convert(@"
using System.Collections.Generic;
using Xunit;

public class MyTests {
    public static IEnumerable<object[]> MyMethodData() {
        yield return new object[] { ""hello"", 1 };
    }

    [Theory]
    [MemberData(nameof(MyMethodData))]
    public void TestSomething(string input, int maxDepth = 64) {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // JUnit cannot fill C# optional parameters from shorter MemberData rows;
        // the converter must pad rows through a synthetic adapter data method.
        Assert.Contains("MethodSource(\"myMethodData_pad", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Object[] _defaults = new Object[] { 64 };", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalTheory_GeneratesParameterizedTest()
    {
        // xUnit [ConditionalTheory] must be treated as [Theory]: otherwise the
        // converted test keeps @MethodSource but loses @ParameterizedTest and is
        // silently dropped (e.g. RegexMatchTests' timeout cases never ran).
        var result = Convert(@"
using System.Collections.Generic;
using Xunit;

public class MyTests {
    public static IEnumerable<object[]> MyMethodData() {
        yield return new object[] { ""hello"" };
    }

    [ConditionalTheory]
    [MemberData(nameof(MyMethodData))]
    public void TestSomething(string input) {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("org.junit.jupiter.params.ParameterizedTest", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MethodSource(\"myMethodData\"", result.GeneratedCode, StringComparison.Ordinal);
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
