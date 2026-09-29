using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that var declarations with lambda initializers correctly resolve
/// to explicit functional interface types instead of leaving Java's var
/// (which cannot infer lambda target types).
/// </summary>
public class VarLambdaTypeInferenceTests
{
    private readonly ITestOutputHelper _out;

    public VarLambdaTypeInferenceTests(ITestOutputHelper output) => _out = output;

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
