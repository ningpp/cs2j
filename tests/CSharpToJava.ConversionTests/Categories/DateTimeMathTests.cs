using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# DateTime, TimeSpan, Math, Random, Guid and decimal types to the
/// compat library / Java equivalents.
/// </summary>
public class DateTimeMathTests : ConversionTestBase
{
    [Fact]
    public void DateTimeConstructor_ConvertsToCSharpDateTime()
    {
        var result = Convert("class C { public System.DateTime M() { return new System.DateTime(2020, 1, 1); } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.CSharpDateTime;",
            "public CSharpDateTime m() {",
            "return new CSharpDateTime(2020, 1, 1);");
    }

    [Fact]
    public void DateTimeProperties_ConvertsToGetters()
    {
        var result = Convert("class C { public int M(System.DateTime d) { return d.Year + d.Month + d.Day; } }");
        AssertConversion(result, "d.getYear() + d.getMonth() + d.getDay()");
    }

    [Fact]
    public void DateTimeNow_ConvertsToGetNow()
    {
        var result = Convert("class C { public System.DateTime M() { return System.DateTime.Now; } }");
        AssertConversion(result, "return CSharpDateTime.getNow();");
    }

    [Fact]
    public void TimeSpan_ConvertsToCSharpTimeSpan()
    {
        var result = Convert("class C { public double M() { var t = new System.TimeSpan(1, 0, 0); return t.TotalHours; } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.CSharpTimeSpan;",
            "var t = new CSharpTimeSpan(1, 0, 0);",
            "return t.getTotalHours();");
    }

    [Fact]
    public void MathOps_ConvertsToStaticMath()
    {
        var result = Convert("class C { public double M() { return System.Math.Max(1, 2) + System.Math.Abs(-3) + System.Math.Sqrt(4); } }");
        AssertConversion(result, "Math.max(1, 2) + Math.abs(-3) + Math.sqrt(4.0)");
    }

    [Fact]
    public void MathRound_ConvertsToRound()
    {
        var result = Convert("class C { public int M() { return (int)System.Math.Round(1.5); } }");
        AssertConversion(result, "return (int)((double)Math.round(1.5));");
    }

    [Fact]
    public void RandomNext_ConvertsToCSharpRandom()
    {
        var result = Convert("class C { public int M() { var r = new System.Random(); return r.Next(); } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.CSharpRandom;",
            "var r = new CSharpRandom();",
            "return r.nextInt(0, Integer.MAX_VALUE);");
    }

    [Fact]
    public void GuidNew_ConvertsToUUID()
    {
        var result = Convert("class C { public System.Guid M() { return System.Guid.NewGuid(); } }");
        AssertConversion(result, "import java.util.UUID;", "public UUID m() {", "return UUID.randomUUID();");
    }

    [Fact]
    public void DecimalLiteral_ConvertsToDecimalParse()
    {
        var result = Convert("class C { public decimal M() { decimal d = 1.5m; return d; } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.Decimal;",
            "Decimal d = Decimal.parse(\"1.5\");",
            "return d;");
    }

    [Fact]
    public void DecimalOperation_ConvertsToAdd()
    {
        var result = Convert("class C { public decimal M(decimal a, decimal b) { return a + b; } }");
        AssertConversion(result, "public Decimal m(Decimal a, Decimal b) {", "return a.add(b);");
    }

    [Fact]
    public void MathMin_ConvertsToStaticMath()
    {
        var result = Convert("class C { public int M() { return System.Math.Min(1, 2); } }");
        AssertConversion(result, "return Math.min(1, 2);");
    }
}
