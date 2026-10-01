using System.Linq;
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
/// Project-level gate for the enum-declaration pass (C05). The
/// SampleEnumDeclarationSolution fixture covers [Flags] byte enums, non-int
/// enums with explicit values and plain int enums; running the pass in
/// isolation must normalize the targeted enums to enum-like sealed classes,
/// leave the plain enum (and every unrelated byte) untouched, and the
/// intermediate tree must restore/build cleanly with a zero residual gate.
/// </summary>
public class EnumDeclarationProjectTests
{
    private const string PassId = "enum-declaration";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleEnumDeclarationSolution");

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
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Fixture_NormalizesProblemEnums_AndIntermediateTreeVerifies()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-enum-declaration");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleEnumDeclarationSolution.sln"), outputRoot);

            Assert.True(result.Success, "enum-declaration pass failed (see chain diagnostics above)");
            Assert.Contains(".cs2j-enum-declaration-src",
                result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, ".cs2j-enum-declaration-src");
            Assert.True(Directory.Exists(intermediate), $"missing intermediate tree: {intermediate}");

            var libFile = Path.Combine(intermediate, "SampleEnumDeclarationLib", "EnumSamples.cs");
            var text = await File.ReadAllTextAsync(libFile);

            // 目标形态：[Flags]/non-int 归一为 sealed class。
            Assert.Contains("sealed class Color", text);
            Assert.Contains("sealed class Code", text);
            Assert.Contains("private readonly byte _value", text);
            Assert.Contains("private readonly long _value", text);
            Assert.Contains("public byte GetValue()", text);
            Assert.Contains("public long GetValue()", text);
            // D5：[Flags] 特性不迁移（注释里的文字不算）。
            Assert.DoesNotContain("[Flags]\nsealed class", text);
            Assert.DoesNotContain("[Flags] sealed", text);

            // 普通 int enum 路径字节级保持。
            Assert.Contains("enum Priority", text);

            // App 跨文件使用点已改写。
            var appProgram = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SampleEnumDeclarationApp", "Program.cs"));
            Assert.DoesNotContain("Color.Red | Color.Blue", appProgram);
            Assert.Contains("FromValue", appProgram);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PlainIntEnumSolution_WhenEnabled_IsByteIdentical_WithBomPreserved()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-ed-clean-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            const string sourceCode =
                "namespace Plain;\npublic enum Priority { Low, Normal, High }\n" +
                "public static class Uses { public static bool IsNormal(Priority p) => p == Priority.Normal; }\n";
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
            Assert.Contains(".cs2j-enum-declaration-src",
                result.ExecutedIntermediateDirectoryNames);

            var copiedBytes = await File.ReadAllBytesAsync(
                Path.Combine(outputDir, ".cs2j-enum-declaration-src", "Plain.cs"));
            Assert.Equal(sourceBytes, copiedBytes);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
