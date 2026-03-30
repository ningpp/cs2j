using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumTransformerTests
{
    [Fact]
    public void ExplicitValueEnum_GeneratesGetValueAndFromValue()
    {
        var result = Convert(@"
public enum Status
{
    Open = 10,
    Closed = 20,
    Pending = 30
}

public class Sample
{
    public Status Get() { return Status.Open; }
}");

        Assert.True(result.Success);
        Assert.Contains("Open(10)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Closed(20)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Pending(30)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private final int value;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public int getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static Status fromValue(int v)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("if (e.value == v) return e;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitValueEnum_AutoIncrements_AfterExplicitValue()
    {
        var result = Convert(@"
public enum Priority
{
    Low = 1,
    Medium,
    High
}

public class Sample
{
    public Priority Get() { return Priority.Low; }
}");

        Assert.True(result.Success);
        Assert.Contains("Low(1)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Medium(2)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("High(3)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitValueEnum_HexValues_ParsedCorrectly()
    {
        var result = Convert(@"
public enum Color
{
    Red = 0x01,
    Green = 0x02,
    Blue = 0x04,
    Alpha
}

public class Sample
{
    public Color Get() { return Color.Red; }
}");

        Assert.True(result.Success);
        Assert.Contains("Red(0x01)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Green(0x02)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Blue(0x04)", result.GeneratedCode, StringComparison.Ordinal);
        // Alpha should auto-increment from 4 (0x04) to 5
        Assert.Contains("Alpha(5)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitValueEnum_CastToInt_UsesGetValue()
    {
        var result = Convert(@"
public enum Status
{
    Open = 10,
    Closed = 20
}

public class Sample
{
    public int GetValue(Status s) { return (int)s; }
}");

        Assert.True(result.Success);
        Assert.Contains("s.getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinal()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitValueEnum_CastFromInt_UsesFromValue()
    {
        var result = Convert(@"
public enum Status
{
    Open = 10,
    Closed = 20
}

public class Sample
{
    public Status FromInt(int v) { return (Status)v; }
}");

        Assert.True(result.Success);
        Assert.Contains("Status.fromValue(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".values()[", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleEnum_CastToInt_UsesOrdinal()
    {
        var result = Convert(@"
public enum Direction { North, South, East, West }

public class Sample
{
    public int GetIndex(Direction d) { return (int)d; }
}");

        Assert.True(result.Success);
        Assert.Contains("d.ordinal()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleEnum_CastFromInt_UsesValues()
    {
        var result = Convert(@"
public enum Direction { North, South, East, West }

public class Sample
{
    public Direction FromInt(int v) { return (Direction)v; }
}");

        Assert.True(result.Success);
        Assert.Contains("Direction.values()[(int)(v)]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedFlagsEnum_NotSilentlyDropped()
    {
        var result = Convert(@"
using System;

public class Container
{
    [Flags]
    public enum Options
    {
        None = 0,
        Fast = 1,
        Safe = 2
    }

    public int GetOptions() { return Options.Fast; }
}");

        Assert.True(result.Success);
        // [Flags] enum should generate a static class with int constants, not be dropped
        Assert.Contains("static class Options", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static final int Fast = 1;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static final int Safe = 2;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsEnum_HasFlag_ConvertedToBitwiseCheck()
    {
        var result = Convert(@"
using System;

[Flags]
public enum Permissions
{
    Read = 1,
    Write = 2,
    Execute = 4
}

public class Sample
{
    public bool CanRead(Permissions perms) { return perms.HasFlag(Permissions.Read); }
}");

        Assert.True(result.Success);
        // HasFlag should be converted to bitwise AND check
        Assert.Contains("(perms & Permissions.Read) != 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("hasFlag", result.GeneratedCode, StringComparison.OrdinalIgnoreCase);
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
