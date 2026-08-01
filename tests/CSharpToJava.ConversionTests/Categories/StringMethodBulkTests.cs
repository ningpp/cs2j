using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// High-volume verification of C# string instance methods mapping to Java String API equivalents,
/// reusing validated method-name transformations with varied inputs.
/// </summary>
public class StringMethodBulkTests : ConversionTestBase
{
    [Fact] public void Length_ConvertsToLength() => AssertConversion(Convert("class C { public int M(string s) { return s.Length; } }"), "return s.length();");
    [Fact] public void IndexOfChar_ConvertsToIndexOf() => AssertConversion(Convert("class C { public int M(string s) { return s.IndexOf('b'); } }"), "return s.indexOf('b');");
    [Fact] public void IndexOfString_ConvertsToIndexOf() => AssertConversion(Convert("class C { public int M(string s) { return s.IndexOf(\"xy\"); } }"), "return s.indexOf(\"xy\");");
    [Fact] public void Substring_ConvertsToSubstring() => AssertConversion(Convert("class C { public string M(string s) { return s.Substring(2); } }"), "return s.substring(2);");
    [Fact] public void ToUpper_ConvertsToToUpperCase() => AssertConversion(Convert("class C { public string M(string s) { return s.ToUpper(); } }"), "return s.toUpperCase();");
    [Fact] public void ToLower_ConvertsToToLowerCase() => AssertConversion(Convert("class C { public string M(string s) { return s.ToLower(); } }"), "return s.toLowerCase();");
    [Fact] public void Replace_ConvertsToReplace() => AssertConversion(Convert("class C { public string M(string s) { return s.Replace(\"x\", \"y\"); } }"), "return s.replace(\"x\", \"y\");");
    [Fact] public void Trim_ConvertsToTrim() => AssertConversion(Convert("class C { public string M(string s) { return s.Trim(); } }"), "return s.trim();");
    [Fact] public void StartsWith_ConvertsToStartsWith() => AssertConversion(Convert("class C { public bool M(string s) { return s.StartsWith(\"y\"); } }"), "return s.startsWith(\"y\");");
    [Fact] public void EndsWith_ConvertsToEndsWith() => AssertConversion(Convert("class C { public bool M(string s) { return s.EndsWith(\"y\"); } }"), "return s.endsWith(\"y\");");
    [Fact] public void Contains_ConvertsToContains() => AssertConversion(Convert("class C { public bool M(string s) { return s.Contains(\"y\"); } }"), "return s.contains(\"y\");");
    [Fact] public void CharAt_ConvertsToCharAt() => AssertConversion(Convert("class C { public char M(string s) { return s[0]; } }"), "return s.charAt(0);");
    [Fact] public void IsNullOrEmpty_ConvertsToHelper() => AssertConversion(Convert("class C { public bool M(string s) { return string.IsNullOrEmpty(s); } }"), "return StringHelper.isNullOrEmpty(s);");
    [Fact] public void Concat_ConvertsToHelper() => AssertConversion(Convert("class C { public string M(string a, string b) { return string.Concat(a, b); } }"), "return StringHelper.concat(a, b);");
    [Fact] public void Compare_ConvertsToHelper() => AssertConversion(Convert("class C { public int M(string a, string b) { return string.Compare(a, b); } }"), "return StringHelper.compare(a, b);");
    [Fact] public void ValueOf_ConvertsToValueOf() => AssertConversion(Convert("class C { public string M(int x) { return x.ToString(); } }"), "return String.valueOf(x);");
    [Fact] public void Split_ConvertsToSplit() => AssertConversion(Convert("class C { public string[] M(string s) { return s.Split(','); } }"), "return s.split(\",\");");
    [Fact] public void Join_ConvertsToHelper() => AssertConversion(Convert("class C { public string M(string[] parts) { return string.Join(\"-\", parts); } }"), "return StringHelper.join(\"-\", parts);");
    [Fact] public void PadLeft_ConvertsToHelper() => AssertConversion(Convert("class C { public string M(string s) { return s.PadLeft(4); } }"), "return StringHelper.padLeft(s, 4);");
    [Fact] public void EqualsStatic_ConvertsToObjectsEquals() => AssertConversion(Convert("class C { public bool M(string s) { return string.Equals(s, \"y\"); } }"), "return Objects.equals(s, \"y\");");
    [Fact] public void ConcatOperator_ConvertsToPlus() => AssertConversion(Convert("class C { public string M(string a, string b) { return a + b + \"z\"; } }"), "return a + b + \"z\";");
    [Fact] public void Format_ConvertsToStringFormat() => AssertConversion(Convert("class C { public string M(int a, int b) { return string.Format(\"{0}+{1}\", a, b); } }"), "return String.format(\"%1$s+%2$s\", a, b);");
    [Fact] public void Empty_ConvertsToLiteral() => AssertConversion(Convert("class C { public string M() { return string.Empty; } }"), "return \"\";");
    [Fact] public void Interpolation_ConvertsToConcat() => AssertConversion(Convert("class C { public string M(int x) { return $\"n={x}\"; } }"), "return \"n=\" + x;");
    [Fact] public void NullCoalesceString_ConvertsToTernary() => AssertConversion(Convert("class C { public string M(string s) { return s ?? \"def\"; } }"), "return s != null ? s : \"def\";");
    [Fact] public void ToStringInt_ConvertsToValueOf() => AssertConversion(Convert("class C { public string M(int x) { return x.ToString(); } }"), "return String.valueOf(x);");
    [Fact] public void SubstringWithLength_ConvertsToStringHelper() => AssertConversion(Convert("class C { public string M(string s) { return s.Substring(1, 2); } }"), "return StringHelper.substring(s, 1, 2);");
    [Fact] public void ReplaceChar_ConvertsToReplace() => AssertConversion(Convert("class C { public string M(string s) { return s.Replace('a', 'b'); } }"), "return s.replace('a', 'b');");
    [Fact] public void TrimStart_ConvertsToTrim() => AssertConversion(Convert("class C { public string M(string s) { return s.Trim(); } }"), "return s.trim();");
    [Fact] public void LengthUsedInCondition_ConvertsToLength() => AssertConversion(Convert("class C { public bool M(string s) { return s.Length > 0; } }"), "return s.length() > 0;");
    [Fact] public void StringMethodNoCSharpResidue()
    {
        var result = Convert("class C { public string M(string s) { return s.ToUpper().Substring(0); } }");
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void ToStringUShortStructField_ConvertsToValueOf()
    {
        var result = Convert(@"
struct Offset { public ushort PortValue; }
struct Info { public Offset Offset; }
class C {
    Info _info;
    string M() { return _info.Offset.PortValue.ToString(); }
}");
        AssertConversion(result, "String.valueOf(");
        AssertJavaDoesNotContain(result, ".toString()", "ushort struct field .ToString() must not produce .toString() on a Java primitive");
    }

    [Fact]
    public void ToStringUShortNestedStructField_WithFormatProvider_ConvertsToValueOf()
    {
        // Mimics the Uri.cs pattern: nested struct in partial class with CultureInfo arg
        var result = Convert(@"
using System.Globalization;
class Uri {
    private class UriInfo { public Offset Offset; }
    private struct Offset { public ushort PortValue; }
    private UriInfo _info;
    string GetPort() { return _info.Offset.PortValue.ToString(CultureInfo.InvariantCulture); }
}");
        AssertConversion(result, "String.valueOf(");
        AssertJavaDoesNotContain(result, ".toString()", "ushort nested struct field .ToString(culture) must not produce .toString() on a Java primitive");
    }
}
