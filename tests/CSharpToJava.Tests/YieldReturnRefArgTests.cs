using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for ref argument holder declarations inside yield return statements.
///
/// Root cause: TransformYieldReturn did not drain pre/post-statements emitted
/// by ArgumentTransformer when processing ref arguments. The holder variable
/// declaration (e.g., CharHolder _previousInstructionRef = ...) leaked into
/// the wrong scope, and the post-statement (e.g., previousInstruction =
/// _previousInstructionRef.value) was also misplaced or lost.
/// </summary>
public class YieldReturnRefArgTests
{
    /// <summary>
    /// Reproduces the AGL GeometryGraphWriter bug: ref parameter in
    /// yield return inside if/else branches. Both branches call a method
    /// with ref char parameter, and the holder declarations must be
    /// scoped correctly within each branch.
    /// </summary>

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
