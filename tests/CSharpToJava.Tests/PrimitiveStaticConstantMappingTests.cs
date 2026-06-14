using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PrimitiveStaticConstantMappingTests
{

    [Fact]
    public void StringCompareOrdinal_TwoArgs_MapsToStringHelperCompareOrdinal()
    {
        var result = Convert("""
public class Sample
{
    public int Test(string a, string b)
    {
        return string.CompareOrdinal(a, b);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("StringHelper.compareOrdinal(a, b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringCompareOrdinal_FiveArgs_MapsToStringHelperCompareOrdinal()
    {
        var result = Convert("""
public class Sample
{
    public int Test(string a, string b)
    {
        return string.CompareOrdinal(a, 0, b, 0, 3);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("StringHelper.compareOrdinal(a, 0, b, 0, 3)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringAliasCompareOrdinal_TwoArgs_MapsToStringHelperCompareOrdinal()
    {
        var result = Convert("""
public class Sample
{
    public int Test(string a, string b)
    {
        return String.CompareOrdinal(a, b);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("StringHelper.compareOrdinal(a, b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringAliasCompareOrdinal_FiveArgs_MapsToStringHelperCompareOrdinal()
    {
        var result = Convert("""
public class Sample
{
    public int Test(string a, string b)
    {
        return String.CompareOrdinal(a, 1, b, 2, 4);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("StringHelper.compareOrdinal(a, 1, b, 2, 4)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxedDoubleNaN_InStaticPropertyGetter_UsesJavaWrapperConstant()
    {
        var result = Convert(@"
public class Sample
{
    public static double NoFixedPosition
    {
        get { return Double.NaN; }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimitiveDoubleNaN_UsesJavaWrapperConstant()
    {
        var result = Convert(@"
public class Sample
{
    public double GetNaN()
    {
        return double.NaN;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UInt32MaxValue_EmitsLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const long MaxIPv4Value = UInt32.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("4294967295L", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UInt32MinValue_EmitsZero()
    {
        var result = Convert("""
public class Sample
{
    private const uint Min = UInt32.MinValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("= 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.MIN_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UintKeywordMaxValue_EmitsLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const long MaxIPv4Value = uint.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("4294967295L", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortMaxValue_EmitsLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const int MaxPort = UInt16.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("65535", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Short.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortKeywordMaxValue_EmitsLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const int MaxPort = ushort.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("65535", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Short.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ULongMaxValue_EmitsHexLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const long MaxVal = UInt64.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("0xFFFFFFFFFFFFFFFFL", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Long.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Int32MaxValue_StillUsesIntegerMAX_VALUE()
    {
        var result = Convert("""
public class Sample
{
    private const int Max = Int32.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntKeywordMaxValue_StillUsesIntegerMAX_VALUE()
    {
        var result = Convert("""
public class Sample
{
    private const int Max = int.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharToLowerInvariant_MapsToCharacterToLowerCase()
    {
        var result = Convert("""
public class Sample
{
    public char Test(char c)
    {
        return char.ToLowerInvariant(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.toLowerCase(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharToUpperInvariant_MapsToCharacterToUpperCase()
    {
        var result = Convert("""
public class Sample
{
    public char Test(char c)
    {
        return char.ToUpperInvariant(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.toUpperCase(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsLower_MapsToCharacterIsLowerCase()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsLower(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isLowerCase(c)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Character.isLower(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsUpper_MapsToCharacterIsUpperCase()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsUpper(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isUpperCase(c)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Character.isUpper(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsWhiteSpace_MapsToCharacterIsWhitespace()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsWhiteSpace(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isWhitespace(c)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Character.isWhiteSpace(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsControl_MapsToCharacterIsISOControl()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsControl(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isISOControl(c)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Character.isControl(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsDigit_MapsToCharacterIsDigit()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsDigit(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isDigit(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsLetter_MapsToCharacterIsLetter()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsLetter(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isLetter(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsLetterOrDigit_MapsToCharacterIsLetterOrDigit()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsLetterOrDigit(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isLetterOrDigit(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsPunctuation_MapsToCharacterIsPunctuation()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsPunctuation(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isPunctuation(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsSeparator_MapsToCharacterIsSeparator()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsSeparator(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isSeparator(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsSymbol_MapsToCharacterIsSymbol()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsSymbol(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isSymbol(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsSurrogate_MapsToCharacterIsSurrogate()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsSurrogate(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isSurrogate(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsHighSurrogate_MapsToCharacterIsHighSurrogate()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsHighSurrogate(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isHighSurrogate(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsLowSurrogate_MapsToCharacterIsLowSurrogate()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return char.IsLowSurrogate(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isLowSurrogate(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsSurrogatePair_MapsToCharacterIsSurrogatePair()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char high, char low)
    {
        return char.IsSurrogatePair(high, low);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isSurrogatePair(high, low)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharGetNumericValue_MapsToCharacterGetNumericValue()
    {
        var result = Convert("""
public class Sample
{
    public double Test(char c)
    {
        return char.GetNumericValue(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.getNumericValue(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharToLower_MapsToCharacterToLowerCase()
    {
        var result = Convert("""
public class Sample
{
    public char Test(char c)
    {
        return char.ToLower(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.toLowerCase(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharToUpper_MapsToCharacterToUpperCase()
    {
        var result = Convert("""
public class Sample
{
    public char Test(char c)
    {
        return char.ToUpper(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.toUpperCase(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharConvertFromUtf32_MapsToCharacterToChars()
    {
        var result = Convert("""
public class Sample
{
    public string Test(int utf32)
    {
        return char.ConvertFromUtf32(utf32);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.toChars(utf32)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharConvertToUtf32_MapsToCharacterToCodePoint()
    {
        var result = Convert("""
public class Sample
{
    public int Test(char high, char low)
    {
        return char.ConvertToUtf32(high, low);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.toCodePoint(high, low)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharMaxValue_MapsToCharacterMaxValue()
    {
        var result = Convert("""
public class Sample
{
    private const char Max = char.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharMinValue_MapsToCharacterMinValue()
    {
        var result = Convert("""
public class Sample
{
    private const char Min = char.MinValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.MIN_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharAliasIsLower_MapsToCharacterIsLowerCase()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return Char.IsLower(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isLowerCase(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharAliasIsWhiteSpace_MapsToCharacterIsWhitespace()
    {
        var result = Convert("""
public class Sample
{
    public bool Test(char c)
    {
        return Char.IsWhiteSpace(c);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Character.isWhitespace(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsEnumParameter_MapsToLongType()
    {
        var result = Convert("""
public class Uri
{
    [Flags]
    private enum Flags : ulong
    {
        Zero = 0x00000000,
        SchemeNotCanonical = 0x1,
        UserNotCanonical = 0x2,
        HostNotCanonical = 0x4,
    }

    private bool InFact(Flags flags)
    {
        return (_flags & flags) != 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private boolean inFact(long flags)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("inFact(Uri.Flags", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsEnumParameter_QualifiedName_MapsToLongType()
    {
        var result = Convert("""
public class Uri
{
    [Flags]
    private enum Flags : ulong
    {
        Zero = 0x00000000,
        SchemeNotCanonical = 0x1,
        UserNotCanonical = 0x2,
        HostNotCanonical = 0x4,
    }

    private bool InFact(Uri.Flags flags)
    {
        return (_flags & flags) != 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private boolean inFact(long flags)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("inFact(Uri.Flags", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTimeStylesStaticMember_MapsToIntegerHelper()
    {
        var result = Convert("""
using System;
using System.Globalization;

public class Sample
{
    public DateTime Parse(string text)
    {
        return DateTime.ParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("IntegerHelper.RoundtripKind", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.IntegerHelper", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Globalization.DateTimeStyles", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeSpanZero_StaticMember_MapsToCompatConstant()
    {
        var result = Convert("""
using System;

public class Sample
{
    public bool IsNonZero(TimeSpan value)
    {
        return value != TimeSpan.Zero;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("CSharpTimeSpan.ZERO", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpTimeSpan.Zero", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringSplitOptionsStaticMember_MapsToIntegerHelper()
    {
        var result = Convert("""
using System;

public class Sample
{
    private static readonly char[] Separators = new[] { ' ' };

    public string[] Split(string value)
    {
        return value.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("IntegerHelper.RemoveEmptyEntries", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.IntegerHelper", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.StringSplitOptions", result.GeneratedCode, StringComparison.Ordinal);
    }

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
