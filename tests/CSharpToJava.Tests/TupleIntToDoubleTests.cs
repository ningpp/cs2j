using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# tuple expressions with int literals in positions that
/// require double (e.g. (int, int, int, int, bool, double)) properly
/// convert the int literal to a double in the generated Java Tuple.of() call.
///
/// In C#, int→double is an implicit conversion. In Java, Tuple.of() infers
/// the type argument from the literal, so an int literal produces Integer
/// instead of Double, causing "incompatible bounds" type inference errors.
/// </summary>
public class TupleIntToDoubleTests
{
    private readonly ITestOutputHelper _out;

    public TupleIntToDoubleTests(ITestOutputHelper output) => _out = output;

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
