using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# string operations to Java String API equivalents.
/// </summary>
public class StringTests : ConversionTestBase
{
    [Fact]
    public void StringEmpty_ConvertsToEmptyLiteral()
    {
        var result = Convert("class C { public string M() { return string.Empty; } }");
        AssertConversion(result, "return \"\";");
        AssertJavaDoesNotContain(result, "String.Empty");
    }

    [Fact]
    public void StringConcat_ConvertsToPlusOperator()
    {
        var result = Convert("class C { public string M(string a, string b) { return a + b; } }");
        AssertConversion(result, "return a + b;");
    }

    [Fact]
    public void StringInterpolation_ConvertsToConcatenation()
    {
        var result = Convert("class C { public string M(int x) { return $\"val={x}\"; } }");
        AssertConversion(result, "return \"val=\" + x;");
        AssertJavaDoesNotContain(result, "$\"");
    }

    [Fact]
    public void StringFormat_ConvertsToJavaFormat()
    {
        var result = Convert("class C { public string M(int x) { return string.Format(\"{0}-{1}\", x, x); } }");
        AssertConversion(result, "String.format(\"%1$s-%2$s\", x, x)");
    }

    [Fact]
    public void StringLength_ConvertsToLengthMethod()
    {
        var result = Convert("class C { public int M(string s) { return s.Length; } }");
        AssertConversion(result, "return s.length();");
    }

    [Fact]
    public void StringIndexOf_ConvertsToIndexOfMethod()
    {
        var result = Convert("class C { public int M(string s) { return s.IndexOf('a'); } }");
        AssertConversion(result, "return s.indexOf('a');");
    }

    [Fact]
    public void StringSubstring_ConvertsToSubstringMethod()
    {
        var result = Convert("class C { public string M(string s) { return s.Substring(1); } }");
        AssertConversion(result, "return s.substring(1);");
    }

    [Fact]
    public void StringToUpper_ConvertsToUpperCase()
    {
        var result = Convert("class C { public string M(string s) { return s.ToUpper(); } }");
        AssertConversion(result, "return s.toUpperCase();");
    }

    [Fact]
    public void StringToLower_ConvertsToLowerCase()
    {
        var result = Convert("class C { public string M(string s) { return s.ToLower(); } }");
        AssertConversion(result, "return s.toLowerCase();");
    }

    [Fact]
    public void StringReplace_ConvertsToReplace()
    {
        var result = Convert("class C { public string M(string s) { return s.Replace(\"a\", \"b\"); } }");
        AssertConversion(result, "return s.replace(\"a\", \"b\");");
    }

    [Fact]
    public void StringTrim_ConvertsToTrim()
    {
        var result = Convert("class C { public string M(string s) { return s.Trim(); } }");
        AssertConversion(result, "return s.trim();");
    }

    [Fact]
    public void StringStartsWith_ConvertsToStartsWith()
    {
        var result = Convert("class C { public bool M(string s) { return s.StartsWith(\"x\"); } }");
        AssertConversion(result, "return s.startsWith(\"x\");");
    }

    [Fact]
    public void StringEndsWith_ConvertsToEndsWith()
    {
        var result = Convert("class C { public bool M(string s) { return s.EndsWith(\"x\"); } }");
        AssertConversion(result, "return s.endsWith(\"x\");");
    }

    [Fact]
    public void StringContains_ConvertsToContains()
    {
        var result = Convert("class C { public bool M(string s) { return s.Contains(\"x\"); } }");
        AssertConversion(result, "return s.contains(\"x\");");
    }

    [Fact]
    public void StringEquals_ConvertsToEquals()
    {
        var result = Convert("class C { public bool M(string s) { return s.Equals(\"x\"); } }");
        AssertConversion(result, "return Objects.equals(s, \"x\");");
    }

    [Fact]
    public void StringConcatStatic_ConvertsToConcat()
    {
        var result = Convert("class C { public string M(string a, string b) { return string.Concat(a, b); } }");
        AssertConversion(result, "StringHelper.concat(a, b)");
    }

    [Fact]
    public void StringCompareStatic_ConvertsToCompareTo()
    {
        var result = Convert("class C { public int M(string a, string b) { return string.Compare(a, b); } }");
        AssertConversion(result, "StringHelper.compare(a, b)");
    }

    [Fact]
    public void StringIsNullOrEmpty_ConvertsToHelper()
    {
        var result = Convert("class C { public bool M(string s) { return string.IsNullOrEmpty(s); } }");
        AssertConversion(result, "StringHelper.isNullOrEmpty(s)");
    }

    [Fact]
    public void CharToString_ConvertsToValueOf()
    {
        var result = Convert("class C { public string M(char c) { return c.ToString(); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "String.valueOf(c)");
    }

    [Fact]
    public void IntToString_ConvertsToValueOf()
    {
        var result = Convert("class C { public string M(int x) { return x.ToString(); } }");
        AssertConversion(result, "String.valueOf(x)");
    }

    [Fact]
    public void StringSplit_ConvertsToSplit()
    {
        var result = Convert("class C { public string[] M(string s) { return s.Split(','); } }");
        AssertConversion(result, "return s.split(\",\");");
    }

    [Fact]
    public void StringJoinStatic_ConvertsToJoin()
    {
        var result = Convert("class C { public string M(string[] parts) { return string.Join(\"-\", parts); } }");
        AssertConversion(result, "StringHelper.join(\"-\", parts)");
    }

    [Fact]
    public void StringPadLeft_ConvertsToPadStart()
    {
        var result = Convert("class C { public string M(string s) { return s.PadLeft(4); } }");
        AssertConversion(result, "StringHelper.padLeft(s, 4)");
    }

    [Fact]
    public void StringRemove_ConvertsToDelete()
    {
        var result = Convert("class C { public string M(string s) { return s.Remove(1); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "StringHelper.remove");
    }

    [Fact]
    public void VerbatimString_PassesThroughAsJavaString()
    {
        var result = Convert("class C { public string M() { return @\"line1\\nline2\"; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "@\"");
    }

    [Fact]
    public void StringFieldInitializer_Empty()
    {
        var result = Convert("class C { public string Name = string.Empty; }");
        AssertConversion(result, "public String Name = \"\";");
    }

    [Fact]
    public void StringLocalDeclaration()
    {
        var result = Convert("class C { public void M() { string s = \"hello\"; var len = s.Length; } }");
        AssertConversion(result, "String s = \"hello\";", "s.length()");
    }

    [Fact]
    public void StringInTernary()
    {
        var result = Convert("class C { public string M(string s) { return s != null ? s : string.Empty; } }");
        AssertConversion(result, "return (s != null ? s : \"\");");
    }

    [Fact]
    public void CharLiteralField()
    {
        var result = Convert("class C { public char Sep = ','; }");
        AssertConversion(result, "public char Sep = ',';");
    }

    [Fact]
    public void StringEqualityOperator_ConvertsToEquals()
    {
        var result = Convert("class C { public bool M(string a, string b) { return a == b; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "Objects.equals(a, b)");
    }

    [Fact]
    public void StringConcatWithInt_ConvertsToStringConcat()
    {
        var result = Convert("class C { public string M(int n) { return \"n=\" + n; } }");
        AssertConversion(result, "return \"n=\" + n;");
    }

    [Fact]
    public void StringFormatWithMultipleArgs()
    {
        var result = Convert("class C { public string M(int a, int b, int c) { return string.Format(\"{0}{1}{2}\", a, b, c); } }");
        AssertConversion(result, "String.format(\"%1$s%2$s%3$s\", a, b, c)");
    }

    [Fact]
    public void StringInterpolationWithMultipleVars()
    {
        var result = Convert("class C { public string M(string n, int a) { return $\"{n} is {a}\"; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "$\"");
    }

    [Fact]
    public void StringToCharArray_ConvertsToToCharArray()
    {
        var result = Convert("class C { public char[] M(string s) { return s.ToCharArray(); } }");
        AssertConversion(result, "return s.toCharArray();");
    }

    [Fact]
    public void StringForeach_UsesToCharArrayEnhancedFor()
    {
        var result = Convert("class C { public int M(string s) { int n = 0; foreach (var c in s) { if (c == 'x') n++; } return n; } }");
        AssertConversion(result, "for (char c : s.toCharArray())");
        AssertJavaDoesNotContain(result, "(Iterable<");
    }

    [Fact]
    public void StringIndexOfString_ConvertsToIndexOf()
    {
        var result = Convert("class C { public int M(string s) { return s.IndexOf(\"abc\"); } }");
        AssertConversion(result, "return s.indexOf(\"abc\");");
    }

    [Fact]
    public void StringLastIndexOf_ConvertsToLastIndexOf()
    {
        var result = Convert("class C { public int M(string s) { return s.LastIndexOf('x'); } }");
        AssertConversion(result, "return s.lastIndexOf('x');");
    }

    [Fact]
    public void EmptyStringLiteral_PassesThrough()
    {
        var result = Convert("class C { public string M() { return \"\"; } }");
        AssertConversion(result, "return \"\";");
    }

    [Fact]
    public void StringSubstringWithLength_ConvertsToSubstring()
    {
        var result = Convert("class C { public string M(string s) { return s.Substring(1, 2); } }");
        AssertConversion(result, "StringHelper.substring(s, 1, 2)");
    }

    [Fact]
    public void StringInsert_ConvertsToHelper()
    {
        var result = Convert("class C { public string M(string s) { return s.Insert(0, \"x\"); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "StringHelper.insert");
    }

    [Fact]
    public void CharIsDigit_ConvertsToHelper()
    {
        var result = Convert("class C { public bool M(char c) { return char.IsDigit(c); } }");
        AssertConversion(result, "Character.isDigit(c)");
    }

    [Fact]
    public void CharToUpper_ConvertsToHelper()
    {
        var result = Convert("class C { public char M(char c) { return char.ToUpper(c); } }");
        AssertConversion(result, "Character.toUpperCase(c)");
    }

    [Fact]
    public void StringTrimWithArgs_ConvertsToTrim()
    {
        var result = Convert("class C { public string M(string s) { return s.Trim('x'); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "trim");
    }

    [Fact]
    public void StringFormatWithDouble()
    {
        var result = Convert("class C { public string M(double d) { return string.Format(\"{0:F2}\", d); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "String.format");
    }

    [Fact]
    public void StringConcatChain_ConvertsToPlus()
    {
        var result = Convert("class C { public string M(string a, string b, string c) { return a + b + c; } }");
        AssertConversion(result, "return a + b + c;");
    }

    [Fact]
    public void StringPropertyOnLocal()
    {
        var result = Convert("class C { public int M() { string s = \"abc\"; return s.Length; } }");
        AssertConversion(result, "String s = \"abc\";", "return s.length();");
    }

    [Fact]
    public void StringComparisonInIf()
    {
        var result = Convert("class C { public bool M(string s) { if (s == \"x\") return true; return false; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "Objects.equals(s, \"x\")");
    }

    [Fact]
    public void StringEmptyCheckViaLength()
    {
        var result = Convert("class C { public bool M(string s) { return s.Length == 0; } }");
        AssertConversion(result, "return s.length() == 0;");
    }
}
