using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces RTree.GetAllIntersecting pattern where ToArray() appears in
/// a conditional expression alongside `new T[0]`. The generated
/// TypeHelper.listToArray(...) returns Object, which is incompatible with
/// the T[] from the other branch in a ternary — the converter must wrap
/// the listToArray call in a cast.
/// </summary>
public class ListToArrayConditionalExpressionTests
{

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
