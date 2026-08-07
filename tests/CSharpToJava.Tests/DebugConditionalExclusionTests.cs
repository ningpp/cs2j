using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that types inside #if DEBUG regions are not converted, even when the
/// workspace compilation was loaded with the DEBUG preprocessor symbol (Debug
/// configuration). The converter targets Release semantics (Debug.Assert is
/// stripped elsewhere for the same reason).
/// Reproduces RegexAssemblyCompiler (whole file under #if DEBUG) emitting
/// imports of the nonexistent System.Reflection.* Java packages.
/// </summary>
public class DebugConditionalExclusionTests
{
    [Fact]
    public async Task DebugOnlyType_NotConverted_WhenCompilationDefinesDebug()
    {
        const string source = @"
public class AlwaysPresent
{
    public int GetValue() => 42;
}

#if DEBUG
public class DebugOnlyHelper
{
    public int GetDebugValue() => 1;
}
#endif
";
        var tree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.Latest)
                .WithPreprocessorSymbols("DEBUG", "TRACE"));
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(compilation);

        Assert.NotEmpty(results);
        Assert.Contains(results, r =>
            r.FileName != null && r.FileName.Contains("AlwaysPresent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(results, r =>
            r.FileName != null && r.FileName.Contains("DebugOnlyHelper", StringComparison.OrdinalIgnoreCase));
    }
}
