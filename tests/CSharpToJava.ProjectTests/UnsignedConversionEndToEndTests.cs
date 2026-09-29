using CSharpToJava.CLI;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.ProjectTests;

[Collection("UnsignedTypePreprocessSerial")]
public class UnsignedConversionEndToEndTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleUnsignedSolution");

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };
}
