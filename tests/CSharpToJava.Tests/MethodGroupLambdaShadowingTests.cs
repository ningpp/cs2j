using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that lambdas synthesized from method groups rename their parameters when
/// the default names (sender/e) collide with names visible in the enclosing scope
/// (e.g. a method parameter named e). Java forbids lambda parameter shadowing.
/// </summary>
public class MethodGroupLambdaShadowingTests
{
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
