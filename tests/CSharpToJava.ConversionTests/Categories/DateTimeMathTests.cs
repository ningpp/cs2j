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
            "return r.next();");
    }

    [Fact]
    public void RandomNext_WithMaxValue_UsesNextMethod()
    {
        // random.Next(8) should map to random.next(8), NOT nextInt(8) or guarded expression
        var result = Convert("class C { public int M(System.Random random) { return random.Next(8); } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.CSharpRandom;",
            "return random.next(8);");
        AssertJavaDoesNotContain(result, "nextInt",
            "Random.Next(maxValue) should use CSharpRandom.next(), not java.util.Random.nextInt()");
        AssertJavaDoesNotContain(result, "<= 0",
            "Constant positive maxValue should not generate edge-case guard");
    }

    [Fact]
    public void RandomNext_WithMinMax_UsesNextMethod()
    {
        var result = Convert("class C { public int M(System.Random random) { return random.Next(1, 10); } }");
        AssertConversion(result,
            "return random.next(1, 10);");
        AssertJavaDoesNotContain(result, "nextInt");
    }

    [Fact]
    public void RandomNext_WithVariableMax_UsesNextMethod()
    {
        // Even with variable maxValue, should delegate to CSharpRandom.next() which handles edge cases
        var result = Convert("class C { public int M(System.Random random, int max) { return random.Next(max); } }");
        AssertConversion(result,
            "return random.next(max);");
        AssertJavaDoesNotContain(result, "nextInt");
        AssertJavaDoesNotContain(result, "<= 0");
    }

    [Fact]
    public void RandomNextDouble_ConvertsToNextDouble()
    {
        var result = Convert("class C { public double M(System.Random random) { return random.NextDouble(); } }");
        AssertConversion(result, "return random.nextDouble();");
    }

    [Fact]
    public void RandomNextBytes_ConvertsToNextBytes()
    {
        var result = Convert("class C { public void M(System.Random random) { byte[] buf = new byte[10]; random.NextBytes(buf); } }");
        AssertConversion(result, "random.nextBytes(buf);");
    }

    [Fact]
    public void RandomNextInt64_ConvertsToNextInt64()
    {
        var result = Convert("class C { public long M(System.Random random) { return random.NextInt64(); } }");
        AssertConversion(result, "return random.nextInt64();");
    }

    [Fact]
    public void RandomNewWithSeed_ConvertsConstructor()
    {
        var result = Convert("class C { public int M() { var r = new System.Random(42); return r.Next(100); } }");
        AssertConversion(result,
            "var r = new CSharpRandom(42);",
            "return r.next(100);");
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
