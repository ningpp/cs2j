using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// C# allows out-of-range enum values such as <c>(DtdProcessing)(-1)</c>, a common
/// "unset" sentinel. Java enums have no such member, so a literal translation
/// (<c>DtdProcessing.values()[-1]</c>) throws ArrayIndexOutOfBoundsException at
/// class-initialization time. The sentinel is modeled as a null reference, and
/// reading it back as an integer must yield -1 like C# (via
/// <c>EnumHelper.ordinalOrMinusOne</c>).
/// </summary>
public class EnumSentinelCastTests : ConversionTestBase
{
    [Fact]
    public void OutOfRangeOrdinalEnumCast_EmitsNullSentinel()
    {
        var result = Convert("""
public enum DtdProcessing { Prohibit, Ignore, Parse }

class Host
{
    public void Configure()
    {
        DtdProcessing dtdProc = (DtdProcessing)(-1);
        System.Console.WriteLine(dtdProc);
    }
}
""");

        AssertSuccess(result);
        AssertJavaContains(result, "DtdProcessing dtdProc = (DtdProcessing) null;");
        AssertJavaDoesNotContain(result, "values()[-1]");
        AssertJavaDoesNotContain(result, "values()[(-1)]");
    }

    [Fact]
    public void InRangeOrdinalEnumCast_StillUsesValuesIndex()
    {
        var result = Convert("""
public enum DtdProcessing { Prohibit, Ignore, Parse }

class Host
{
    public DtdProcessing Pick()
    {
        return (DtdProcessing)1;
    }
}
""");

        AssertSuccess(result);
        AssertJavaContains(result, "DtdProcessing.values()[1]");
        AssertJavaDoesNotContain(result, "(DtdProcessing) null");
    }

    [Fact]
    public void OrdinalEnumToIntCast_UsesNullTolerantHelper()
    {
        var result = Convert("""
public enum DtdProcessing { Prohibit, Ignore, Parse }

class Host
{
    public bool IsSet(DtdProcessing dtdProcessing)
    {
        return (int)dtdProcessing != -1;
    }
}
""");

        AssertSuccess(result);
        AssertJavaContains(result, "EnumHelper.ordinalOrMinusOne(dtdProcessing) != -1");
        AssertJavaContains(result, "import io.github.ningpp.compat.EnumHelper;");
        AssertJavaDoesNotContain(result, "dtdProcessing.ordinal()");
    }

    [Fact]
    public void ValueEnumCast_OutOfRangeSentinel_StillUsesFromValue()
    {
        // Value enums (explicit/irregular member values) keep their fromValue
        // representation, which already accepts any numeric value including -1.
        var result = Convert("""
public enum Mode { None = 0, Alpha = 1, Beta = 3 }

class Host
{
    public Mode M()
    {
        return (Mode)(-1);
    }
}
""");

        AssertSuccess(result);
        AssertJavaContains(result, "Mode.fromValue((int)(-1))");
        AssertJavaDoesNotContain(result, "(Mode) null");
    }

    [Fact]
    public void NumericLocalInitializer_OutOfRangeEnumCast_EmitsNull()
    {
        var result = Convert("""
public enum DtdProcessing { Prohibit, Ignore, Parse }

class Host
{
    public void Configure()
    {
        DtdProcessing e = (DtdProcessing)(-1);
    }
}
""");

        AssertSuccess(result);
        AssertJavaContains(result, "DtdProcessing e = (DtdProcessing) null;");
        AssertJavaDoesNotContain(result, "values()[-1]");
        AssertJavaDoesNotContain(result, "values()[(-1)]");
    }

    [Fact]
    public void XmlReaderSettingsShape_BecomesNullSafe()
    {
        // Mirrors the real AGL shape: a field holding the -1 sentinel, a guard
        // reading it back as int, a null-safe comparison with real constants,
        // and the value passed to another class that stores and compares it.
        var result = Convert("""
public enum DtdProcessing { Prohibit, Ignore, Parse }

public class XmlCharCheckingReader
{
    private readonly DtdProcessing _dtdProcessing;

    public XmlCharCheckingReader(int unused, DtdProcessing dtdProcessing)
    {
        _dtdProcessing = dtdProcessing;
    }

    public bool Check()
    {
        return _dtdProcessing == DtdProcessing.Prohibit;
    }
}

public class XmlReaderSettings
{
    internal DtdProcessing dtdProcessing = (DtdProcessing)(-1);

    public void Setup()
    {
        if ((int)dtdProcessing != -1)
        {
            var reader = new XmlCharCheckingReader(0, dtdProcessing);
            reader.Check();
        }
    }
}
""");

        AssertSuccess(result);
        AssertJavaContains(result, "DtdProcessing dtdProcessing = (DtdProcessing) null;");
        AssertJavaContains(result, "if (EnumHelper.ordinalOrMinusOne(dtdProcessing) != -1)");
        AssertJavaContains(result, "== DtdProcessing.Prohibit");
        AssertJavaContains(result, "new XmlCharCheckingReader(0, dtdProcessing)");
        AssertJavaContains(result, "import io.github.ningpp.compat.EnumHelper;");
        AssertJavaDoesNotContain(result, "values()[-1]");
        AssertJavaDoesNotContain(result, "values()[(-1)]");
    }
}
