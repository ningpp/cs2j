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
/// Project-level gate for the unsafe-ffm pass (C06, M1). The
/// SampleUnsafeFfmSolution fixture covers a fixed-size buffer field in a
/// struct (R2 → managed array field) and fixed statements unpacking a message
/// frame (R3 → array + int index). Running the pass in isolation must lower
/// both forms, and the intermediate tree must restore/build cleanly through
/// the residual gate.
/// </summary>
public class UnsafeFfmProjectTests
{
    private const string PassId = "unsafe-ffm";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleUnsafeFfmSolution");

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath, string outputRoot, bool verify = true)
    {
        var registry = PassCatalog.BuildRegistry();
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            UnsafeFfmOptionsFactory = () => new Core.UnsafeFfm.UnsafeFfmOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Fixture_LowersFixedFieldAndStatements_AndIntermediateTreeVerifies()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-unsafe-ffm");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleUnsafeFfmSolution.sln"), outputRoot);

            Assert.True(result.Success, "unsafe-ffm pass failed (see chain diagnostics above)");
            Assert.Contains(".cs2j-unsafe-ffm-src",
                result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, ".cs2j-unsafe-ffm-src");
            Assert.True(Directory.Exists(intermediate), $"missing intermediate tree: {intermediate}");

            var libFile = Path.Combine(intermediate, "SampleUnsafeFfmLib", "FrameParsing.cs");
            var text = await File.ReadAllTextAsync(libFile);

            // R2：fixed 字段 → byte[] 字段；struct 补显式无参 ctor。
            Assert.DoesNotContain("fixed byte payload", text);
            Assert.Contains("byte[] payload", text);
            Assert.Contains("public FrameHeader()", text);

            // R3：fixed 语句归一为数组局部 + int 索引，指针下标变数组下标。
            Assert.DoesNotContain("fixed (byte*", text);
            Assert.Contains("_p_array", text);
            // p[i] 在复合赋值右侧，索引归一为 (p) + (i)。
            Assert.Contains("sum += _p_array[(p) + (i)];", text);
            // App 源码不含 fixed 构造，保持可运行。
            var appProgram = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SampleUnsafeFfmApp", "Program.cs"));
            Assert.Contains("FrameHeader.Summarize", appProgram);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FileWithoutUnsafeOrFixed_IsCopiedByteIdentical()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-ffm-clean-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            const string sourceCode =
                "namespace Plain;\npublic static class Maths { public static int Add(int a, int b) => a + b; }\n";
            var sourceBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                .GetPreamble().Concat(Encoding.UTF8.GetBytes(sourceCode)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "Plain.cs"), sourceBytes);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "Plain.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
                "    <TargetFramework>net10.0</TargetFramework>\n" +
                "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                "    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");

            var result = await RunPassAsync(Path.Combine(sourceDir, "Plain.csproj"), outputDir);

            Assert.True(result.Success);
            Assert.Contains(".cs2j-unsafe-ffm-src",
                result.ExecutedIntermediateDirectoryNames);

            var copiedBytes = await File.ReadAllBytesAsync(
                Path.Combine(outputDir, ".cs2j-unsafe-ffm-src", "Plain.cs"));
            Assert.Equal(sourceBytes, copiedBytes);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
