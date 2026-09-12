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
    public void MemberData_EnumValueToNumericParameter_UsesEnumHelperAdapter()
    {
        var result = Convert(@"
using System.Collections.Generic;
using Xunit;

public enum SampleEnumByte : byte {
    A = 1
}

public class MyTests {
    public static IEnumerable<object[]> MyMethodData() {
        yield return new object[] { SampleEnumByte.A };
    }

    [Theory]
    [MemberData(nameof(MyMethodData))]
    public void TestSomething(long value) {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // JUnit cannot convert a converter-generated Java enum to a numeric
        // parameter; the adapter must unwrap it with EnumHelper.toInt64.
        Assert.Contains("MethodSource(\"myMethodData_pad", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("EnumHelper.toInt64(_row[0])", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.EnumHelper;", result.GeneratedCode, StringComparison.Ordinal);
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

    [Fact]
    public void ConditionalTheory_UnsupportedRemoteExecutor_IsDisabled()
    {
        // C# tests guarded by RemoteExecutor.IsSupported are skipped when the
        // remote executor is unavailable (compat reports false). Running them
        // in-process breaks AppDomain-default-timeout tests (RegexMatchTests
        // match_DefaultTimeout_Throws hangs forever because Regex's static
        // default timeout was already initialized).
        var result = Convert(@"
using System.Collections.Generic;
using Xunit;

public static class RemoteExecutor
{
    public static bool IsSupported => false;
}

public class MyTests {
    [ConditionalTheory(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
    [InlineData(""x"")]
    public void TestSomething(string input) {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("org.junit.jupiter.api.Disabled", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("@Disabled", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterLoop_GeneratesDisabled()
    {
        // xUnit [OuterLoop] tests are huge integration tests (e.g. writing 125 MB
        // of base64); they must be @Disabled in the JUnit suite or the surefire
        // fork times out inside Span.copyTo.
        var result = Convert(@"
using Xunit;

public class MyTests {
    [Fact]
    [OuterLoop]
    public void BigTest() {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("org.junit.jupiter.api.Disabled", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("@Disabled", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterLoop_WithNonLiteralInlineData_GeneratesDisabled()
    {
        // Regression test: a [Theory]/[ConditionalTheory] whose [InlineData] carries a
        // non-literal constant (here the `MaxInt` const) is switched to @MethodSource,
        // and that branch used to `return` before the [OuterLoop] -> @Disabled mapping
        // ran. The result was that ValueTests.VeryLongInputString — [ConditionalTheory]
        // + [OuterLoop] + [InlineData(MaxInt)] — stayed enabled and allocated/compared
        // 1 GB strings on every JUnit run, dominating the whole suite's wall time.
        var result = Convert(@"
using Xunit;

public class MyTests {
    private const int MaxInt = 715827882;
    private const int MaximumPossibleStringLength = 1073741791;

    [ConditionalTheory(nameof(IsX64))]
    [OuterLoop]
    [InlineData(MaxInt)]
    [InlineData(MaximumPossibleStringLength)]
    public void VeryLongInputString(int length) {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Still generated as a parameterized @MethodSource test — but disabled.
        Assert.Contains("org.junit.jupiter.params.ParameterizedTest", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MethodSource(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("org.junit.jupiter.api.Disabled", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("@Disabled", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignore_WithNonLiteralInlineData_GeneratesDisabled()
    {
        // Same early-return hazard as [OuterLoop]: [Ignore] must also survive the
        // @MethodSource switch.
        var result = Convert(@"
using Xunit;

public class MyTests {
    private const int Limit = 1024;

    [Theory]
    [Ignore]
    [InlineData(Limit)]
    public void BigTest(int size) {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("MethodSource(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("@Disabled", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterLoop_WithLiteralInlineData_GeneratesDisabled()
    {
        // The literal-InlineData path emits @CsvSource and never returned early, so it
        // already produced @Disabled; this guards against the hoisted check regressing it.
        var result = Convert(@"
using Xunit;

public class MyTests {
    [Theory]
    [OuterLoop]
    [InlineData(1)]
    [InlineData(2)]
    public void BigTest(int size) {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("@Disabled", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterLoop_OnConditionalFactWithInlineData_GeneratesDisabled()
    {
        // [ConditionalFact] + [OuterLoop] (JsonEncodedTextTests.InvalidLargeEncode shape)
        // must keep working — that path had no early return and is the case the
        // original fix was written for.
        var result = Convert(@"
using Xunit;

public class MyTests {
    [ConditionalFact]
    [OuterLoop]
    public void InvalidLargeEncode() {
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("@Disabled", result.GeneratedCode, StringComparison.Ordinal);
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
