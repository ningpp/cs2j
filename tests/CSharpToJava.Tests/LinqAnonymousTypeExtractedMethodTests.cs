using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ queries with anonymous type select, extracted to a helper method
/// by the LINQ rewriter (because the foreach body contains control flow like continue/break),
/// correctly synthesize the record type in the method signature and foreach variable.
///
/// Root cause: When the foreach body has continue/break/return, CanRewrapForeachVisitor fails
/// and the LINQ expression is extracted to a separate yield-return method.  The rewriter
/// sets the anonymous type to null (SelectMethod handler), producing a broken return type
/// that becomes List&lt;T&gt; (with T undefined) in Java. The foreach variable degrades to Object.
/// </summary>
public class LinqAnonymousTypeExtractedMethodTests
{
    /// <summary>
    /// Reproduces the MsmtRectilinearPath.java compilation error:
    /// LINQ query with anonymous type extracted to helper method,
    /// foreach body contains 'continue', forcing method extraction.
    /// The helper method return type must use the synthesized record, not 'T'.
    /// The foreach variable must be the synthesized record, not 'Object'.
    /// </summary>

    /// <summary>
    /// Method chain variant: items.Cast().SelectMany(...).Select(x => new { ... })
    /// with foreach body containing 'continue', forcing extraction.
    /// </summary>

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions { PreferStreamApi = false },
        });
    }
}
