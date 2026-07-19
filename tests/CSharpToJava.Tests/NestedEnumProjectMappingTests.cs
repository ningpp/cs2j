using System;
using System.IO;
using System.Linq;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Project-pipeline test: nested enums referenced from another type in the same
/// namespace must be emitted with their enclosing class qualifier (or imported)
/// so the generated Java compiles.
/// </summary>
public class NestedEnumProjectMappingTests
{
    private static ConversionOptions CreateOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };
    }

    [Fact]
    public async Task CrossFile_NestedEnumInClass_InterfaceMethod_UsesQualifiedName()
    {
        var processorFile = @"
namespace dotnet.xml.Xsl.XsltOld
{
    internal class Processor
    {
        internal enum OutputResult
        {
            Continue,
            Interrupt,
            Overflow,
            Error,
            Ignore
        }
    }
}";

        var recordOutputFile = @"
namespace dotnet.xml.Xsl.XsltOld
{
    internal interface RecordOutput
    {
        Processor.OutputResult RecordDone(RecordBuilder record);
        void TheEnd();
    }

    internal class RecordBuilder { }
}";

        var options = CreateOptions();
        var pipeline = new ProjectConversionPipeline(options);
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile { FilePath = "System/Xml/Xsl/XsltOld/Processor.cs", Content = processorFile },
            new SourceFile { FilePath = "System/Xml/Xsl/XsltOld/RecordOutput.cs", Content = recordOutputFile },
        });

        Assert.True(results.Count >= 2, $"Expected at least 2 results but got {results.Count}");
        var recordResult = Assert.Single(results, r => r.FileName == "RecordOutput.java");
        Assert.True(recordResult.Success, string.Join("\n", recordResult.Diagnostics));

        var code = recordResult.GeneratedCode!;
        Console.WriteLine(code);

        // Must reference the nested enum via its enclosing class so Java compiles.
        Assert.Contains("Processor.OutputResult", code, StringComparison.Ordinal);
    }
}
