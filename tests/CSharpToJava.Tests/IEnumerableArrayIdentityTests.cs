using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# stores the array instance itself when an array is assigned to an
/// IEnumerable&lt;T&gt; target, and a later (T[]) cast returns the SAME instance.
/// Code such as StaircaseRemover relies on that identity:
/// SegWithIndex.Equals compares the backing Point[] by reference, so a
/// staircase is only removable when the RTree segments and the current points
/// share one array instance. ArrayHelper.toList copies, breaking the round
/// trip; the identity-preserving ArrayHelper.asIterable view must be used for
/// IEnumerable targets instead.
/// </summary>
public class IEnumerableArrayIdentityTests
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
