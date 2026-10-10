using CSharpToJava.Core.AnonymousTypeLift;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Infrastructure;

/// <summary>
/// Runs the anonymous-type-lift preprocessor over a single source string
/// (mirroring the convert-project chain, where anonymous-type-lift precedes
/// every LINQ pass), inlines the synthesized shape types, and converts the
/// lifted tree through the direct single-file pipeline.
///
/// C21 M5 retired the converter's legacy <c>Map.of(...)</c> anonymous arm, so
/// direct-pipeline tests whose source projects anonymous types must lower them
/// to named types first instead of relying on the converter fallback.
/// </summary>
internal static class AnonymousTypeLiftConverter
{
    public static ConversionResult Convert(
        string sourceCode,
        string fileName = "Test.cs",
        ConversionOptions? options = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions, path: fileName);
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "anon-lift", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new AnonymousTypeLiftPreprocessor();
        var statistics = new AnonymousTypeLiftStatistics();
        var diagnostics = new List<AnonymousTypeLiftDiagnostic>();
        var lifted = preprocessor.RewriteFile(
            sourceCode, compilation.GetSemanticModel(tree), statistics, diagnostics);
        Assert.True(
            lifted.Changed,
            "anonymous-type-lift should rewrite the projected anonymous types. " +
            string.Join("; ", diagnostics.Select(d => d.Message)));

        // Shape types are emitted as a separate compilation unit; inline them so
        // the single-file converter can resolve the now-named projections.
        var generated = preprocessor.GenerateTypeFile();
        var liftedSource = string.IsNullOrEmpty(generated)
            ? lifted.OutputCode
            : lifted.OutputCode + Environment.NewLine + generated;

        return new ConversionPipeline().Convert(new ConversionRequest
        {
            SourceCode = liftedSource,
            FileName = fileName,
            Options = options ?? new ConversionOptions(),
        });
    }
}
