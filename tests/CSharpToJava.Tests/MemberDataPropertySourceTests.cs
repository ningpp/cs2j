using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# [MemberData(nameof(XxxData))] referencing a property
/// generates @MethodSource with the correct getter method name (get + PascalCase),
/// matching the PropertyTransformer's getter naming convention.
/// </summary>
public class MemberDataPropertySourceTests
{

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
