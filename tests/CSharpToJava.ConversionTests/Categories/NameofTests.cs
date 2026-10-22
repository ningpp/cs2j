using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies nameof(...) literals: a nameof bound to a method symbol must emit the
/// Java-side (renamed) method name so runtime consumers that resolve the literal
/// by reflection (e.g. xUnit MemberData forwarding helpers) keep working.
/// </summary>
public class NameofTests : ConversionTestBase
{
    [Fact]
    public void NameOfMethod_EmitsRenamedJavaMethodName()
    {
        var result = Convert(@"
using System.Collections.Generic;
using Xunit;
namespace T {
    public class SomeTests {
        public static IEnumerable<object[]> Data_TestData() { return new List<object[]> { new object[] { 1 } }; }

        [Theory]
        [MemberData(nameof(Helper.Transform), nameof(Data_TestData), 0, MemberType = typeof(Helper))]
        public void Test(int x) { }

        [Theory]
        [InlineData(1)]
        public void Test2(int x) {
            string n = nameof(Data_TestData);
        }
    }
    public static class Helper {
        public static IEnumerable<object[]> Transform(string m, int i) { return new List<object[]> { new object[] { 1 } }; }
    }
}");
        AssertConversion(result);
        AssertJavaContains(result, "Helper.transform(\"data_TestData\", 0)",
            "MemberData forwarded nameof literal must use the renamed method name.");
        AssertJavaContains(result, "String n = \"data_TestData\";",
            "Statement-level nameof(method) must use the renamed method name.");
    }

    [Fact]
    public void NameOfLocalParameter_KeepsOriginalName()
    {
        var result = Convert("class C { public void M(int value) { var n = nameof(value); } }");
        AssertConversion(result, "var n = \"value\";");
    }
}
