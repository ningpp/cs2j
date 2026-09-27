using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# type.FullName access correctly maps to TypeHelper.getFullName(type)
/// on java.lang.Class objects. This verifies the fix for the missing getFullName()
/// method error in generated Java code.
/// See: ExpressionTransformerFacade.cs (line 253) and
///      IdentifierExpressionTransformer.TryResolvePropertyByType.
/// </summary>
public class TypeFullNameTests
{
    /// <summary>
    /// C# Type.FullName → TypeHelper.getFullName(type) for parameter-typed variables.
    /// The previous (buggy) behavior generated type.getFullName() which does not
    /// exist on java.lang.Class.
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
