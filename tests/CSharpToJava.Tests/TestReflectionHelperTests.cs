using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Test-module reflection batch:
/// 1. Overrides of "protected internal" base methods must widen to public
///    (base "protected internal abstract" maps to public in Java).
/// 2. Type.GenericTypeArguments → TypeHelper.getGenericTypeArguments.
/// 3. proxy.MakeGenericType(args) on a variable receiver → TypeHelper.makeGenericType.
/// Regression: tests/TestUtilities + PrecompiledRegexScenarioTest.
/// </summary>
public class TestReflectionHelperTests
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
