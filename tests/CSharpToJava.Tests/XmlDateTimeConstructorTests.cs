using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class XmlDateTimeConstructorTests
{
    /// <summary>
    /// C# DateTime(year, month, day, hour, minute, second, DateTimeKind) 7-param constructor
    /// must be converted to CSharpDateTime(year, month, day, hour, minute, second, 0, DateTimeKind) 8-param constructor
    /// because CSharpDateTime has no 7-param constructor with DateTimeKind.
    /// </summary>
    [Fact]
    public void DateTimeConstructor_WithDateTimeKind_InsertsMillisecondZero()
    {
        var result = Convert("""
using System;

class Sample
{
    DateTime GetUtc()
    {
        return new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.CSharpDateTime;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("new CSharpDateTime(2024, 6, 15, 12, 30, 0, 0, DateTimeKind.Utc)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTimeConstructor_WithDateTimeKindLocal_InsertsMillisecondZero()
    {
        var result = Convert("""
using System;

class Sample
{
    DateTime GetLocal()
    {
        return new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Local);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("new CSharpDateTime(2024, 6, 15, 12, 30, 0, 0, DateTimeKind.Local)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTimeConstructor_SixParams_NoKind_NoChange()
    {
        var result = Convert("""
using System;

class Sample
{
    DateTime Get()
    {
        return new DateTime(2024, 6, 15, 12, 30, 0);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("new CSharpDateTime(2024, 6, 15, 12, 30, 0)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeKind", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// typeof(Decimal) should produce Decimal.class, not dotnet.system.Decimal.class.
    /// This tests the TypeOf expression transformer's handling of mapped types.
    /// </summary>
    [Fact]
    public void TypeOfDecimal_UsesCompatDecimal()
    {
        var result = Convert("""
using System;

class Sample
{
    object GetDecimalType()
    {
        return typeof(Decimal);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Decimal", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// When a class has a member named Decimal, System.Decimal should still map to
    /// io.github.ningpp.compat.Decimal, not dotnet.system.Decimal.
    /// </summary>
    [Fact]
    public void SystemDecimal_InClassWithDecimalMember_UsesCompatDecimal()
    {
        var result = Convert("""
using System;

class DecimalTests
{
    decimal GetValue()
    {
        decimal value = 1.23m;
        return value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.Decimal;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Decimal", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// DateTimeOffset.AddTicks should be converted to CSharpDateTimeOffset.addTicks.
    /// This test verifies the method mapping exists (the actual addTicks method must
    /// also exist in the compat library).
    /// </summary>
    [Fact]
    public void DateTimeOffsetAddTicks_ConvertsToAddTicks()
    {
        var result = Convert("""
using System;

class Sample
{
    DateTimeOffset AddTicks(DateTimeOffset dt, long ticks)
    {
        return dt.AddTicks(ticks);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("addTicks(ticks)", result.GeneratedCode, StringComparison.Ordinal);
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
