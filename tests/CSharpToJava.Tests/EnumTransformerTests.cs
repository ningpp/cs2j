using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumTransformerTests
{

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
        Assert.DoesNotContain("kind.getValue()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// A const enum field initialized with an explicit integer cast must use
    /// EnumType.fromValue(...) so that the static final field is type-compatible
    /// in Java. A bare integer literal is not assignable to a Java enum type.
    /// </summary>

    /// <summary>
    /// Flags enums are emitted as primitive int constants in Java. A const flags field
    /// initialized with an explicit cast must stay a bare integer literal, not wrapped
    /// with a non-existent int.fromValue(...) call.
    /// </summary>

    /// <summary>
    /// Non-flags enum used in a bitwise OR expression for a property return must have
    /// the result wrapped with EnumType.fromValue(...) so the return type matches.
    /// </summary>
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

    /// <summary>
    /// Bitwise AND between an int-to-enum cast (from a method call returning int)
    /// and an enum-to-enum cast must convert both operands to integer values,
    /// perform the AND, and convert back exactly once with fromValue().
    /// </summary>
}
