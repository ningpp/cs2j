using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that object initializer extraction inside a ternary expression
/// does not move side-effectful calls outside the guarded true-branch.
///
/// Bug: C# code like
///   str != null ? new Arrowhead { TipPosition = ParsePoint(str) } : null
/// was converted to Java that calls parsePoint(str) unconditionally:
///   var _obj = new Arrowhead();
///   _obj.setTipPosition(parsePoint(str));   // runs even when str == null!
///   var arrowhead = (str != null ? _obj : null);
///
/// The correct conversion must keep parsePoint(str) inside the ternary's
/// true-branch so it is only evaluated when str != null.
/// </summary>
public class TernaryObjectInitializerNullGuardTests
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
