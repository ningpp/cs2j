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
