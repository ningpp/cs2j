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

    [Fact]
    public void FlagsEnum_AliasAttribute_GeneratesConstantsClass()
    {
        var result = Convert(@"
using F = System.FlagsAttribute;

[F]
public enum Permissions
{
    None = 0,
    Read = 1,
    Write = 2
}

public class Sample
{
    public Permissions Get() { return Permissions.Read; }
}");

        Assert.True(result.Success);
        Assert.Contains("class Permissions", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static final int Read = 1;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("enum Permissions", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsEnum_ComplexValuesAndAutoIncrement_UseConstantValues()
    {
        var result = Convert(@"
using System;

[Flags]
public enum Permissions
{
    None = 0,
    Read = 1,
    Write = Read << 1,
    Execute = Read | Write,
    Next
}

public class Sample
{
    public int Get() { return Permissions.Next; }
}");

        Assert.True(result.Success);
        Assert.Contains("public static final int Write = 2;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static final int Execute = 3;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static final int Next = 4;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Read << 1", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Read | Write", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitValueEnum_ComplexValuesAndAutoIncrement_UseConstantValues()
    {
        var result = Convert(@"
public enum Permissions
{
    None = 0,
    Read = 1,
    Write = Read << 1,
    Execute = Read | Write,
    Next,
    Negative = -1,
    Min = int.MinValue
}

public class Sample
{
    public Permissions Get() { return Permissions.Execute; }
}");

        Assert.True(result.Success);
        Assert.Contains("Write(2)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Execute(3)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Next(4)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Negative(-1)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Min(Integer.MIN_VALUE)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Read << 1", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Read | Write", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitValueEnum_LongUnderlyingType_UsesLongValueAccessorsAndCasts()
    {
        var result = Convert(@"
public enum BigStatus : long
{
    Small = 1L,
    Huge = 5000000000L,
    Next
}

public class Sample
{
    public long ToLong(BigStatus s) { return (long)s; }
    public int ToInt(BigStatus s) { return (int)s; }
    public BigStatus FromLong(long v) { return (BigStatus)v; }
}");

        Assert.True(result.Success);
        Assert.Contains("Small(1L)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Huge(5000000000L)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Next(5000000001L)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private final long value;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public long getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static BigStatus fromValue(long v)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return s.getValue();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return (int)(s.getValue());", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return BigStatus.fromValue((long)(v));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("fromValue(int v)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsEnum_LongUnderlyingType_UsesLongConstantsAndMappedVariables()
    {
        var result = Convert(@"
using System;

[Flags]
public enum BigFlags : long
{
    None = 0L,
    High = 1L << 40,
    Next
}

public class Sample
{
    public BigFlags Get(BigFlags flags) { return flags | BigFlags.High; }
    public string Text(BigFlags flags) { return flags.ToString(); }
}");

        Assert.True(result.Success);
        Assert.Contains("public static final long None = 0L;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static final long High = 1099511627776L;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public static final long Next = 1099511627777L;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public long get(long flags)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("String.valueOf(flags)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public int get(int flags)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumArray_FillsDefaultZeroMember()
    {
        var result = Convert(@"
public class Program
{
    enum VertStatus
    {
        NotVisited,
        InStack,
        Visited,
    }

    public static void Main(string[] args)
    {
        VertStatus[] status = new VertStatus[3];
        System.Console.WriteLine(status[1]);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.fill(status, Program.VertStatus.NotVisited)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Arrays;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumArray_FlagsEnum_NoFillNeeded()
    {
        var result = Convert(@"
using System;

public class Program
{
    [Flags]
    enum Permissions
    {
        None = 0,
        Read = 1,
        Write = 2
    }

    public static void Main(string[] args)
    {
        Permissions[] perms = new Permissions[3];
        System.Console.WriteLine(perms[1]);
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.fill", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumArray_ExplicitValueEnumNoZeroMember_FillsWithFirstValue()
    {
        var result = Convert(@"
public class Program
{
    enum Status
    {
        Open = 10,
        Closed = 20
    }

    public static void Main(string[] args)
    {
        Status[] statuses = new Status[3];
        System.Console.WriteLine(statuses[1]);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.fill(statuses, Program.Status.values()[0])", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Arrays;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumArray_WithInitializerList_NoFillNeeded()
    {
        var result = Convert(@"
public class Program
{
    enum Direction { North, South, East, West }

    public static void Main(string[] args)
    {
        Direction[] dirs = new Direction[] { Direction.North, Direction.South };
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.fill", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumArray_TopLevelEnum_FillsWithZeroMember()
    {
        var result = Convert(@"
public enum Color { Red, Green, Blue }

public class Sample
{
    public static void Main(string[] args)
    {
        Color[] colors = new Color[5];
        System.Console.WriteLine(colors[0]);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.fill(colors, Color.Red)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import java.util.Arrays;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleEnum_ComparisonLessThanOrEqual_UsesOrdinal()
    {
        var result = Convert(@"
public enum LexKind
{
    Unknown,
    Or,
    And,
    Eq
}

public class Sample
{
    public bool Check(LexKind kind)
    {
        if (kind <= LexKind.And)
        {
            return true;
        }
        return false;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("kind.ordinal() <= LexKind.And.ordinal()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("kind <= LexKind.And", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleEnum_ComparisonLessThan_UsesOrdinal()
    {
        var result = Convert(@"
public enum LexKind
{
    Unknown,
    Or,
    And,
    Eq
}

public class Sample
{
    public bool Check(LexKind kind)
    {
        if (kind < LexKind.And)
        {
            return true;
        }
        return false;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("kind.ordinal() < LexKind.And.ordinal()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleEnum_ComparisonGreaterThan_UsesOrdinal()
    {
        var result = Convert(@"
public enum LexKind
{
    Unknown,
    Or,
    And,
    Eq
}

public class Sample
{
    public bool Check(LexKind kind)
    {
        if (kind > LexKind.And)
        {
            return true;
        }
        return false;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("kind.ordinal() > LexKind.And.ordinal()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleEnum_ComparisonGreaterThanOrEqual_UsesOrdinal()
    {
        var result = Convert(@"
public enum LexKind
{
    Unknown,
    Or,
    And,
    Eq
}

public class Sample
{
    public bool Check(LexKind kind)
    {
        if (kind >= LexKind.And)
        {
            return true;
        }
        return false;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("kind.ordinal() >= LexKind.And.ordinal()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitValueEnum_ComparisonLessThanOrEqual_UsesGetValue()
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
    public bool Check(Status s)
    {
        if (s <= Status.Closed)
        {
            return true;
        }
        return false;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("s.getValue() <= Status.Closed.getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("s <= Status.Closed", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleEnum_Equality_NoOrdinalNeeded()
    {
        var result = Convert(@"
public enum LexKind
{
    Unknown,
    Or,
    And,
    Eq
}

public class Sample
{
    public bool Check(LexKind kind)
    {
        if (kind == LexKind.And)
        {
            return true;
        }
        return false;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("kind == LexKind.And", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinal()", result.GeneratedCode, StringComparison.Ordinal);
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

    [Fact]
    public void NonFlagsEnum_BitwiseOrAssignment_UsesGetValueAndFromValue()
    {
        var result = Convert(@"
public class UriParser
{
    public void Check(Flags flags)
    {
        flags |= Flags.UserNotCanonical;
    }

    private enum Flags : ulong
    {
        Zero = 0x00000000,
        SchemeNotCanonical = 0x1,
        UserNotCanonical = 0x2,
        HostNotCanonical = 0x4,
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // |= on non-Flags enum should be expanded to fromValue(getValue() | getValue())
        Assert.Contains("fromValue(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getValue() |", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".getValue())", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT use raw |= on enum type
        Assert.DoesNotContain("flags |= UriParser.Flags", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFlagsEnum_BitwiseAndComparison_UsesGetValue()
    {
        var result = Convert(@"
public class UriParser
{
    public void Check(Flags flags)
    {
        if ((flags & Flags.HostNotCanonical) != 0) { }
    }

    private enum Flags : ulong
    {
        Zero = 0x00000000,
        SchemeNotCanonical = 0x1,
        UserNotCanonical = 0x2,
        HostNotCanonical = 0x4,
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // & on non-Flags enum should use getValue()
        Assert.Contains("getValue() &", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".getValue()) != 0", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT use raw & on enum type
        Assert.DoesNotContain("flags & UriParser.Flags.HostNotCanonical", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFlagsEnum_BitwiseAndAssignment_UsesGetValueAndFromValue()
    {
        var result = Convert(@"
public class Sample
{
    public void Check(MyEnum flags)
    {
        flags &= MyEnum.Read;
    }

    private enum MyEnum
    {
        None = 0,
        Read = 1,
        Write = 2,
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("fromValue(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getValue() &", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFlagsEnum_BitwiseXorAssignment_UsesGetValueAndFromValue()
    {
        var result = Convert(@"
public class Sample
{
    public void Check(MyEnum flags)
    {
        flags ^= MyEnum.Read;
    }

    private enum MyEnum
    {
        None = 0,
        Read = 1,
        Write = 2,
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("fromValue(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getValue() ^", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFlagsEnum_WithSameSimpleNameAsPreviouslyConvertedFlagsEnum_UsesGetValue()
    {
        var flagsResult = Convert(@"
using System;

public class OtherUri
{
    [Flags]
    private enum Flags : ulong
    {
        Zero = 0,
        Read = 1,
        Write = 2,
    }

    public bool Has(Flags flags)
    {
        return (flags & Flags.Read) != 0;
    }
}");

        Assert.True(flagsResult.Success, string.Join("\n", flagsResult.Diagnostics));

        var result = Convert(@"
public class UriParser
{
    public void Check(Flags flags)
    {
        flags |= Flags.UserNotCanonical;
        if ((flags & Flags.HostNotCanonical) != 0) { }
    }

    private enum Flags : ulong
    {
        Zero = 0x00000000,
        SchemeNotCanonical = 0x1,
        UserNotCanonical = 0x2,
        HostNotCanonical = 0x4,
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("getValue() |", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getValue() &", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsEnum_BitwiseOperations_RemainNativeIntOps()
    {
        // [Flags] enums are mapped to int/long, so bitwise ops should work natively
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
    public Permissions Combine(Permissions p) { return p | Permissions.Write; }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // [Flags] enum should use native int bitwise ops, not getValue()/fromValue()
        Assert.DoesNotContain("getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("fromValue(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("p | Permissions.Write", result.GeneratedCode, StringComparison.Ordinal);
    }
}
