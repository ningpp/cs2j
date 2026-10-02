using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for lexical-normalize (C28): LN-1 strips #line trivia,
/// LN-5/6 audit usings, and LN-7 (via the pass option) block-normalizes the
/// file-scoped namespace; the intermediate tree must build and test cleanly,
/// stay idempotent and preserve BOM/byte copies.
/// </summary>
public sealed class LexicalNormalizeProjectTests
{
    private const string PassId = "lexical-normalize";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleLexicalNormalizeSolution");

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath,
        string outputRoot,
        LexicalNormalizeOptions? options = null,
        bool verify = true)
    {
        var registry = PassCatalog.BuildRegistry();
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
            EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LexicalNormalizeOptionsFactory = () => options ?? new LexicalNormalizeOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Fixture_StripsLineDirectives_AndIntermediateTreeVerifies()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-lexical-normalize");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleLexicalNormalizeSolution.sln"), outputRoot);

            Assert.True(result.Success, "lexical-normalize pass failed (see chain diagnostics)");
            Assert.Contains(".cs2j-lexical-normalize-src", result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, ".cs2j-lexical-normalize-src");
            var libFile = Path.Combine(
                intermediate, "SampleLexicalNormalizeLib", "LineAndUsings.cs");
            var text = await File.ReadAllTextAsync(libFile);

            Assert.DoesNotContain("#line", text);
            Assert.Contains("LineAndUsings", text);

            // Idempotence: second run over the intermediate tree changes nothing.
            var second = await RunPassAsync(
                Path.Combine(intermediate, "SampleLexicalNormalizeSolution.sln"),
                Path.Combine(outputRoot, "second"));
            Assert.True(second.Success);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Fixture_WithFileScopedOption_BlockNormalizesNamespace_AndTestsPass()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-ln7-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleLexicalNormalizeSolution.sln"),
                outputRoot,
                new LexicalNormalizeOptions
                {
                    NormalizeFileScopedNamespaces = true,
                });

            Assert.True(result.Success, "LN-7 run failed (see chain diagnostics)");

            var intermediate = Path.Combine(outputRoot, ".cs2j-lexical-normalize-src");
            var fileScopedText = await File.ReadAllTextAsync(Path.Combine(
                intermediate, "SampleLexicalNormalizeLib", "FileScopedFile.cs"));

            Assert.DoesNotContain("namespace SampleLexicalNormalizeLib;", fileScopedText);
            Assert.Contains("namespace SampleLexicalNormalizeLib", fileScopedText);
            Assert.Contains("{", fileScopedText);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanFileWithBom_IsBytePreserved()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-ln-bom-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            const string sourceCode = "public class C { }\n";
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                .GetPreamble().Concat(Encoding.UTF8.GetBytes(sourceCode)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "C.cs"), bytes);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "Lib.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

            var result = await RunPassAsync(Path.Combine(sourceDir, "Lib.csproj"), outputDir);

            Assert.True(result.Success);
            var copied = await File.ReadAllBytesAsync(
                Path.Combine(outputDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.Equal(bytes, copied);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Fixture_LiteralSamples_Normalized_InterpolationKept_AndTreeVerifies()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c43-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleLexicalNormalizeSolution.sln"),
                outputRoot);

            Assert.True(result.Success, "C43 literal pass failed (see chain diagnostics)");

            var intermediate = Path.Combine(outputRoot, ".cs2j-lexical-normalize-src");
            var text = await File.ReadAllTextAsync(Path.Combine(
                intermediate, "SampleLexicalNormalizeLib", "LiteralSamples.cs"));

            // LL-1: single/multi-line raw gone; multiline opening quote-newline absent.
            Assert.DoesNotContain("\"\"\"hello", text);
            Assert.DoesNotContain("\"\"\"\r\n", text);
            Assert.DoesNotContain("\"\"\"\n", text);

            // LL-2: u8 suffix gone, byte array emitted.
            Assert.DoesNotContain("\"u8", text);
            Assert.Contains("(byte)0x", text);
            Assert.Contains("new byte[]", text);

            // LL-3: no non-interpolated verbatim. The bare @" substring would also
            // match the LL-4-exempt $@" interpolation, so anchor the declaration
            // shape " = @" instead (Sql is the only non-interpolated verbatim).
            Assert.DoesNotContain(" = @\"", text);

            // LL-6: no bare \e (regular \u001b escapes instead).
            Assert.DoesNotContain("\\e", text);
            Assert.Contains("\\u001b", text);

            // LL-4: interpolated forms remain verbatim for C23.
            Assert.Contains("$\"\"\"raw {x}\"\"\"", text);
            Assert.Contains("$$\"\"\"{{x}}\"\"\"", text);
            Assert.Contains("$@\"a\"\"{x}\"", text);

            // Idempotence over the intermediate tree.
            var second = await RunPassAsync(
                Path.Combine(intermediate, "SampleLexicalNormalizeSolution.sln"),
                Path.Combine(outputRoot, "second"));
            Assert.True(second.Success);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }
}
