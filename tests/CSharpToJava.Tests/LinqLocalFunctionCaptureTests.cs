using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ rewrite inside C# local functions correctly captures
/// variables from the enclosing method scope. The LinqRewriter generates
/// ProceduralLinq helper methods, and when the enclosing local function
/// is hoisted to class level, the ProceduralLinq helper must also receive
/// the captured variables as parameters.
/// </summary>
public class LinqLocalFunctionCaptureTests
{

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                PreferStreamApi = false,
            },
        });
    }
}
