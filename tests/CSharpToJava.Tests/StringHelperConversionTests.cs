using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Verifies that C# string methods mapped to StringHelper via TypeMappings.json
/// are correctly converted to Java, including proper passing of the receiver object
/// as the first argument to the static helper method.
/// </summary>
public class StringHelperConversionTests
{
    // ==================== Static methods (no receiver needed) ====================

    [Fact]
    public void String_Concat_ConvertsToStringHelperConcat()
    {
        var result = Convert(@"
class Sample
{
    public string Join(string a, string b)
    {
        return string.Concat(a, b);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.concat(a, b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Join_StringArray_ConvertsToStringHelperJoin()
    {
        var result = Convert(@"
class Sample
{
    public string JoinParts(string[] parts)
    {
        return string.Join("", "", parts);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.join(\", \", parts)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.StringHelper;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.join(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringAlias_Join_StringArray_ConvertsToStringHelperJoin()
    {
        var result = Convert(@"
using System;
class Sample
{
    public string JoinParts(string[] parts)
    {
        return String.Join("";"", parts);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.join(\";\", parts)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.join(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Join_ObjectArray_ConvertsToStringHelperJoin()
    {
        var result = Convert(@"
class Sample
{
    public string JoinValues(object[] values)
    {
        return string.Join(""|"", values);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.join(\"|\", values)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.join(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Join_GenericEnumerable_ConvertsToStringHelperJoin()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    public string JoinValues(IEnumerable<int> values)
    {
        return string.Join<int>("","", values);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.join(\",\", values)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.join(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Join_ArrayRange_ConvertsToStringHelperJoin()
    {
        var result = Convert(@"
class Sample
{
    public string JoinRange(string[] parts, int startIndex, int count)
    {
        return string.Join("","", parts, startIndex, count);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.join(\",\", parts, startIndex, count)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.join(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IsNullOrEmpty_ConvertsToStringHelperIsNullOrEmpty()
    {
        var result = Convert(@"
class Sample
{
    public bool Check(string s)
    {
        return string.IsNullOrEmpty(s);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.isNullOrEmpty(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IsNullOrWhiteSpace_ConvertsToStringHelperIsNullOrWhiteSpace()
    {
        var result = Convert(@"
class Sample
{
    public bool Check(string s)
    {
        return string.IsNullOrWhiteSpace(s);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.isNullOrWhiteSpace(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Compare_ConvertsToStringHelperCompare()
    {
        var result = Convert(@"
class Sample
{
    public int Cmp(string a, string b)
    {
        return string.Compare(a, b);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.compare(a, b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_CompareOrdinal_ConvertsToStringHelperCompareOrdinal()
    {
        var result = Convert(@"
class Sample
{
    public int Cmp(string a, string b)
    {
        return string.CompareOrdinal(a, b);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.compareOrdinal(a, b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Copy_ConvertsToStringHelperCopy()
    {
        var result = Convert(@"
class Sample
{
    public string DoCopy(string s)
    {
        return string.Copy(s);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.copy(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Intern_ConvertsToStringHelperIntern()
    {
        var result = Convert(@"
class Sample
{
    public string DoIntern(string s)
    {
        return string.Intern(s);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.intern(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IsInterned_ConvertsToStringHelperIsInterned()
    {
        var result = Convert(@"
class Sample
{
    public string Check(string s)
    {
        return string.IsInterned(s);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.isInterned(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ==================== Instance methods with StringComparison (receiver correctly preserved) ====================

    [Fact]
    public void String_Equals_WithComparison_ConvertsToStringHelperEquals()
    {
        var result = Convert(@"
using System;
class Sample
{
    public bool Eq(string s, string other)
    {
        return s.Equals(other, StringComparison.OrdinalIgnoreCase);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.equals(s, other, true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_StartsWith_WithComparison_ConvertsToStringHelperStartsWith()
    {
        var result = Convert(@"
using System;
class Sample
{
    public bool Check(string s, string prefix)
    {
        return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.startsWith(s, prefix, true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_EndsWith_WithComparison_ConvertsToStringHelperEndsWith()
    {
        var result = Convert(@"
using System;
class Sample
{
    public bool Check(string s, string suffix)
    {
        return s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.endsWith(s, suffix, true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Contains_WithComparison_ConvertsToStringHelperContains()
    {
        var result = Convert(@"
using System;
class Sample
{
    public bool Check(string s, string value)
    {
        return s.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.contains(s, value, true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IndexOf_WithComparison_ConvertsToStringHelperIndexOf()
    {
        var result = Convert(@"
using System;
class Sample
{
    public int Find(string s, string value)
    {
        return s.IndexOf(value, StringComparison.OrdinalIgnoreCase);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.indexOf(s, value, true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IndexOf_WithStartIndexAndComparison_ConvertsToStringHelperIndexOf()
    {
        var result = Convert(@"
using System;
class Sample
{
    public int Find(string s, string value, int start)
    {
        return s.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.indexOf(s, value, start, true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_LastIndexOf_WithComparison_ConvertsToStringHelperLastIndexOf()
    {
        var result = Convert(@"
using System;
class Sample
{
    public int Find(string s, string value)
    {
        return s.LastIndexOf(value, StringComparison.OrdinalIgnoreCase);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.lastIndexOf(s, value, true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_LastIndexOf_WithStartIndexAndComparison_ConvertsToStringHelperLastIndexOf()
    {
        var result = Convert(@"
using System;
class Sample
{
    public int Find(string s, string value, int start)
    {
        return s.LastIndexOf(value, start, StringComparison.OrdinalIgnoreCase);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.lastIndexOf(s, value, start, true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ==================== TypeMappings-driven instance methods (receiver preserved) ====================

    [Fact]
    public void String_Insert_ConvertsToStringHelperInsert()
    {
        var result = Convert(@"
class Sample
{
    public string DoInsert(string s, int index, string value)
    {
        return s.Insert(index, value);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.insert(s, index, value)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Remove_OneArg_ConvertsToStringHelperRemove()
    {
        var result = Convert(@"
class Sample
{
    public string DoRemove(string s, int startIndex)
    {
        return s.Remove(startIndex);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.remove(s, startIndex)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Remove_TwoArgs_ConvertsToStringHelperRemove()
    {
        var result = Convert(@"
class Sample
{
    public string DoRemove(string s, int startIndex, int count)
    {
        return s.Remove(startIndex, count);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.remove(s, startIndex, count)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_PadLeft_OneArg_ConvertsToStringHelperPadLeft()
    {
        var result = Convert(@"
class Sample
{
    public string DoPad(string s, int totalWidth)
    {
        return s.PadLeft(totalWidth);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.padLeft(s, totalWidth)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_PadLeft_TwoArgs_ConvertsToStringHelperPadLeft()
    {
        var result = Convert(@"
class Sample
{
    public string DoPad(string s, int totalWidth, char padChar)
    {
        return s.PadLeft(totalWidth, padChar);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.padLeft(s, totalWidth, padChar)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_PadRight_OneArg_ConvertsToStringHelperPadRight()
    {
        var result = Convert(@"
class Sample
{
    public string DoPad(string s, int totalWidth)
    {
        return s.PadRight(totalWidth);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.padRight(s, totalWidth)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_PadRight_TwoArgs_ConvertsToStringHelperPadRight()
    {
        var result = Convert(@"
class Sample
{
    public string DoPad(string s, int totalWidth, char padChar)
    {
        return s.PadRight(totalWidth, padChar);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.padRight(s, totalWidth, padChar)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_TrimStart_NoArgs_ConvertsToStringHelperTrimStart()
    {
        var result = Convert(@"
class Sample
{
    public string DoTrim(string s)
    {
        return s.TrimStart();
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.trimStart(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_TrimStart_WithChar_ConvertsToStringHelperTrimStart()
    {
        var result = Convert(@"
class Sample
{
    public string DoTrim(string s, char c)
    {
        return s.TrimStart(c);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.trimStart(s, c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_TrimEnd_NoArgs_ConvertsToStringHelperTrimEnd()
    {
        var result = Convert(@"
class Sample
{
    public string DoTrim(string s)
    {
        return s.TrimEnd();
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.trimEnd(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_TrimEnd_WithChar_ConvertsToStringHelperTrimEnd()
    {
        var result = Convert(@"
class Sample
{
    public string DoTrim(string s, char c)
    {
        return s.TrimEnd(c);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.trimEnd(s, c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IndexOfAny_ConvertsToStringHelperIndexOfAny()
    {
        var result = Convert(@"
class Sample
{
    private static readonly char[] s_UnsafeChars = { '\\', '/', '?', '@', '#', ':', '[', ']' };

    public bool HasUnsafeChar(string host)
    {
        return host.IndexOfAny(s_UnsafeChars) != -1;
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.indexOfAny(host, s_UnsafeChars)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IndexOfAny_WithStartIndex_ConvertsToStringHelperIndexOfAny()
    {
        var result = Convert(@"
class Sample
{
    public int Find(string s, char[] anyOf, int startIndex)
    {
        return s.IndexOfAny(anyOf, startIndex);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.indexOfAny(s, anyOf, startIndex)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IndexOfAny_WithStartIndexAndCount_ConvertsToStringHelperIndexOfAny()
    {
        var result = Convert(@"
class Sample
{
    public int Find(string s, char[] anyOf, int startIndex, int count)
    {
        return s.IndexOfAny(anyOf, startIndex, count);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.indexOfAny(s, anyOf, startIndex, count)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_LastIndexOfAny_ConvertsToStringHelperLastIndexOfAny()
    {
        var result = Convert(@"
class Sample
{
    public int Find(string s, char[] anyOf)
    {
        return s.LastIndexOfAny(anyOf);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.lastIndexOfAny(s, anyOf)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_LastIndexOfAny_WithStartIndex_ConvertsToStringHelperLastIndexOfAny()
    {
        var result = Convert(@"
class Sample
{
    public int Find(string s, char[] anyOf, int startIndex)
    {
        return s.LastIndexOfAny(anyOf, startIndex);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.lastIndexOfAny(s, anyOf, startIndex)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Clone_ConvertsToStringHelperClone()
    {
        var result = Convert(@"
class Sample
{
    public object DoClone(string s)
    {
        return s.Clone();
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.clone(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_CompareTo_ConvertsToStringHelperCompareTo()
    {
        var result = Convert(@"
class Sample
{
    public int Cmp(string s, object other)
    {
        return s.CompareTo(other);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.compareTo(s, other)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Normalize_ConvertsToStringHelperNormalize()
    {
        var result = Convert(@"
using System.Text;
class Sample
{
    public string DoNormalize(string s)
    {
        return s.Normalize();
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.normalize(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_IsNormalized_ConvertsToStringHelperIsNormalized()
    {
        var result = Convert(@"
using System.Text;
class Sample
{
    public bool Check(string s)
    {
        return s.IsNormalized();
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.isNormalized(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_ReplaceLineEndings_NoArg_ConvertsToStringHelperReplaceLineEndings()
    {
        var result = Convert(@"
class Sample
{
    public string DoReplace(string s)
    {
        return s.ReplaceLineEndings();
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.replaceLineEndings(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_ReplaceLineEndings_WithArg_ConvertsToStringHelperReplaceLineEndings()
    {
        var result = Convert(@"
class Sample
{
    public string DoReplace(string s, string replacement)
    {
        return s.ReplaceLineEndings(replacement);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.replaceLineEndings(s, replacement)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_TryCopyTo_ConvertsToStringHelperTryCopyTo()
    {
        var result = Convert(@"
class Sample
{
    public bool DoCopy(string s, char[] dest, int index)
    {
        return s.TryCopyTo(dest, index);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.tryCopyTo(s, dest, index)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_CopyTo_ConvertsToStringHelperCopyTo()
    {
        var result = Convert(@"
class Sample
{
    public void DoCopy(string s, int sourceIndex, char[] dest, int destIndex, int count)
    {
        s.CopyTo(sourceIndex, dest, destIndex, count);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.copyTo(s, sourceIndex, dest, destIndex, count)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_CopyTo_WithLiteralArguments_ConvertsCorrectly()
    {
        var result = Convert(@"
class Sample
{
    public void DoCopy()
    {
        string s = ""Hello World"";
        char[] dest = new char[5];
        s.CopyTo(0, dest, 0, 5);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.copyTo(s, 0, dest, 0, 5)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ==================== String constructor: new string(char, int) → String.valueOf(char).repeat(int) ====================

    [Fact]
    public void String_Ctor_CharInt_ConvertsToValueOfRepeat()
    {
        var result = Convert(@"
class Sample
{
    public string Repeat(char c, int count)
    {
        return new string(c, count);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("String.valueOf(c).repeat(count)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new String(c, count)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Ctor_CharLiteralInt_ConvertsToValueOfRepeat()
    {
        var result = Convert(@"
class Sample
{
    public string MakeString()
    {
        return new string('a', 5);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("String.valueOf('a').repeat(5)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new String('a', 5)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ==================== Import verification ====================

    [Fact]
    public void StringHelperMethods_GenerateImportStatement()
    {
        var result = Convert(@"
class Sample
{
    public bool Check(string s, char[] chars)
    {
        return s.IndexOfAny(chars) != -1;
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("import io.github.ningpp.compat.StringHelper;", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ==================== Helper ====================

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
