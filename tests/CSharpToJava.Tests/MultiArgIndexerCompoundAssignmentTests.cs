using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Compound assignment (op=) on a multi-argument indexer must expand to a
/// set(args, get(args) op rhs) call, because Java forbids compound assignment
/// on a method-call result (e.g. PlaneTransformation[0, 2] += x).
/// </summary>
public class MultiArgIndexerCompoundAssignmentTests
{

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Matrix.cs",
            Options = new ConversionOptions(),
        });
    }
}
