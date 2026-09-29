using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// [Flags] enum types must map to their underlying int value type everywhere,
/// including property/member declarations in interfaces and classes.
/// Regression: IViewer.ModifierKeys (C#: ModifierKeys ModifierKeys { get; })
/// produced "ModifierKeys getModifierKeys()" instead of "int getModifierKeys()",
/// breaking bitwise usage like (viewer.getModifierKeys() & ModifierKeys.Control).
/// The interface was converted before the [Flags] enum, so the name-based flags
/// registry was empty when the property type was mapped.
/// </summary>
public class FlagsEnumInterfacePropertyTests
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
