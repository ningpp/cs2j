using System.Text;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for the foreach-source pass (C16, M1). Runs the pass
/// in isolation over SampleForeachSourceSolution (Lib+App+Tests): FS-1 must
/// wrap every string foreach site with ToCharArray() (ruling-6 temp loops
/// for explicit non-char elements), leave non-string sites byte-identical,
/// and the rewritten tree must restore/build/test cleanly — the fixture's
/// own xUnit tests assert runtime equivalence (single evaluation, UTF-16
/// code units, nesting, NRE timing, empty strings) on the lowered output.
/// </summary>
public class ForeachSourceProjectTests
{
    private const string PassId = "foreach-source";
    private const string IntermediateDir = ".cs2j-foreach-source-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleForeachSourceSolution");

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath, string outputRoot, bool verify = true, bool strict = false)
    {
        var registry = PassCatalog.BuildRegistry();
        var toggles = PassChainFactory.BuildAllDisabledToggles(registry);
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () =>
                new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            LegacyToggles = toggles,
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
            Strict = strict,
        });
    }

    [Fact]
    public async Task Fixture_WrapsStringSites_AndIntermediateTreeBuildsAndTestsClean()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-foreach-source");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleForeachSourceSolution.sln"), outputRoot);

            Assert.True(result.Success,
                "foreach-source pass failed (see chain diagnostics above)");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateDir);
            Assert.True(Directory.Exists(intermediate),
                $"missing intermediate tree: {intermediate}");

            var libFile = Path.Combine(
                intermediate, "SampleForeachSourceLib", "ForeachStringSamples.cs");
            var text = await File.ReadAllTextAsync(libFile);

            // FS-1: string sources wrapped.
            Assert.Contains("foreach (var c in s.ToCharArray())", text);
            Assert.Contains("foreach (var c in GetTextWithCount().ToCharArray())", text);
            Assert.Contains("(\"a\" + param).ToCharArray()", text);
            Assert.Contains("foreach (var c in GetNullText().ToCharArray())", text);
            // ruling 6: explicit int/object elements become char temp loops.
            // The fresh-name counter is file-scoped: int site appears first
            // and takes the base name, the object site gets suffix 1.
            Assert.Contains("foreach (char __cs2jFsCh in s.ToCharArray())", text);
            Assert.Contains("int c = (int)__cs2jFsCh;", text);
            Assert.Contains("foreach (char __cs2jFsCh1 in s.ToCharArray())", text);
            Assert.Contains("object c = (object)__cs2jFsCh1;", text);
            // char/var declarations are untouched.
            Assert.Contains("foreach (char c in s.ToCharArray())", text);

            // Zero collateral damage: non-string sources keep their shape.
            Assert.Contains("foreach (var c in list)", text);
            Assert.Contains("foreach (var c in array)", text);
            // Outer string[] site is not a string source.
            Assert.Contains("foreach (var row in rows)", text);

            // App source was transformed too.
            var appProgram = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SampleForeachSourceApp", "Program.cs"));
            Assert.Contains(".ToCharArray()", appProgram);
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
    public async Task Transformed_File_PreservesUtf8Bom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-fs-bom-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            const string sourceCode =
                "namespace Bom;\n" +
                "public static class Uses {\n" +
                "    public static System.Collections.Generic.List<char> Run(string s) {\n" +
                "        var r = new System.Collections.Generic.List<char>();\n" +
                "        foreach (var c in s) { r.Add(c); }\n" +
                "        return r;\n" +
                "    }\n" +
                "}\n";
            var sourceBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                .GetPreamble().Concat(Encoding.UTF8.GetBytes(sourceCode)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "Uses.cs"), sourceBytes);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "Bom.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
                "    <TargetFramework>net10.0</TargetFramework>\n" +
                "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                "    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");

            var result = await RunPassAsync(Path.Combine(sourceDir, "Bom.csproj"), outputDir);

            Assert.True(result.Success);
            var rewrittenBytes = await File.ReadAllBytesAsync(
                Path.Combine(outputDir, IntermediateDir, "Uses.cs"));
            Assert.True(rewrittenBytes.Length >= 3);
            Assert.Equal((byte)0xEF, rewrittenBytes[0]);
            Assert.Equal((byte)0xBB, rewrittenBytes[1]);
            Assert.Equal((byte)0xBF, rewrittenBytes[2]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Pass_Is_Absent_WhenAllLegacyTogglesExplicitlyDisabled()
    {
        // foreach-source is default-on now; this test pins the universal kill
        // switch: an all-disabled legacy toggle map with no explicit enablement
        // must keep the pass (and every other one) out of the plan.
        var outputRoot = Path.Combine(FixtureDir, ".test-output-foreach-source-off");
        try
        {
            var registry = PassCatalog.BuildRegistry();
            var result = await CliPreprocessChain.RunAsync(new CliChainRequest
            {
                SourcePath = Path.Combine(FixtureDir, "SampleForeachSourceSolution.sln"),
                DestinationRoot = outputRoot,
                MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
                RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
                MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
                EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
                LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
                StructValueSemanticsOptionsFactory = () =>
                    new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
                JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () =>
                new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
                LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
                EnablePassIds = Array.Empty<string>(),
                VerifyBuild = false,
                VerifyTests = false,
            });

            Assert.DoesNotContain(IntermediateDir, result.ExecutedIntermediateDirectoryNames);
            Assert.False(
                Directory.Exists(Path.Combine(outputRoot, IntermediateDir)),
                "foreach-source intermediate directory must not be created when the pass is off");
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
