using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for explicit interface property implementations that conflict with a class's
/// own property of the same name. The explicit interface accessor must keep the standard
/// Java name to satisfy the interface, while the class property accessor is renamed and
/// all internal references are updated.
/// </summary>
public class ExplicitInterfacePropertyConflictTests
{
    /// <summary>
    /// SchemaEntity-style scenario: explicit interface property IEntity.Name returns string
    /// and accesses the class's own Name property (returning a wrapper type). The generated
    /// Java must not produce a recursive getName() call.
    /// </summary>

    /// <summary>
    /// Reverse declaration order: class property declared before the explicit interface
    /// property. The fix must still rename the class property accessor and update references.
    /// </summary>


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
