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
    [Fact]
    public void FlagsEnum_InterfaceProperty_SingleFile_MapsToInt()
    {
        var result = Convert(@"
[System.Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4
}

public interface IViewer
{
    ModifierKeys ModifierKeys { get; }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int getModifierKeys();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ModifierKeys getModifierKeys()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsEnum_InterfaceProperty_MultiFileProject_EnumDeclaredAfterInterface_MapsToInt()
    {
        // The interface source file comes first; the [Flags] enum is in a later file.
        // The project pipeline converts type groups in compilation order, so the
        // interface would previously be converted before the enum's flags registry
        // entry existed, producing "ModifierKeys getModifierKeys()".
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = pipeline.ConvertProjectAsync(
            new[]
            {
                new SourceFile
                {
                    FilePath = "IViewer.cs",
                    Content = @"
public interface IViewer
{
    ModifierKeys ModifierKeys { get; }
}",
                },
                new SourceFile
                {
                    FilePath = "ModifierKeys.cs",
                    Content = @"
[System.Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4
}",
                },
            }).Result;

        var viewer = results.FirstOrDefault(r => r.GeneratedCode?.Contains("getModifierKeys") == true)
            ?? results.FirstOrDefault(r => r.FileName != null);
        Assert.NotNull(viewer);
        Assert.True(viewer.Success, string.Join("\n", viewer.Diagnostics.Select(d => d.Message)));
        Assert.Contains("int getModifierKeys();", viewer.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ModifierKeys getModifierKeys()", viewer.GeneratedCode, StringComparison.Ordinal);
    }

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
