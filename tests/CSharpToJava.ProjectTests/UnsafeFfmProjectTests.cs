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
        string sourcePath, string outputRoot, bool verify = true,
        IReadOnlyList<string>? passIds = null)
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
            ExpressionDesugarOptionsFactory = () =>
                new Core.ExpressionDesugar.ExpressionDesugarOptions(),
            EqualityConcatOptionsFactory = () => new Core.EqualityConcat.EqualityConcatOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = passIds ?? new[] { PassId },
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

            // M2/R4：FrameText 的全部 stackalloc 站点归一为 BCL 堆数组（D12：
            // 零新支撑库），且中间树整体 build 通过（本链 verify 默认开）。
            Assert.False(HasStackAllocSyntax(text));
            Assert.Contains("int[] digits = new int[4];", text);
            Assert.Contains("int[] offsets = new int[8];", text);
            Assert.Contains("int[] widened = new int[4];", text);
            Assert.Contains("int[] chunk = new int[8];", text);

            // M4/R9：using declaration on Span<int>（文本解析暂存槽）→
            // 空操作归一：using 剥除、局部保留（无 Dispose）；中间树 build 绿。
            Assert.DoesNotContain("using Span<int>", text);
            Assert.Contains("Span<int> digitSlots = new int[4];", text);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task M2_StackAlloc_InteroperatesWithGotoEliminator_FinalTreeCompiles()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-unsafe-ffm-goto");
        try
        {
            // R4 × eliminate-goto（不启 iterator-lowering：此时 ScanFieldLengths
            // 仍是普通迭代器方法、不产生 resume 标签，文件内唯一 goto/label
            // 来自 GotoScanTotal）。
            var (result, chainErrors) = await RunPassCapturingErrors(
                Path.Combine(FixtureDir, "SampleUnsafeFfmSolution.sln"),
                outputRoot,
                passIds: new[]
                {
                    PassId,
                    PassIds.EliminateGoto,
                });

            Assert.True(result.Success,
                "M2 × eliminate-goto chain failed. Chain diagnostics:\n" + chainErrors);
            Assert.Contains(".cs2j-unsafe-ffm-src", result.ExecutedIntermediateDirectoryNames);
            Assert.Contains(".cs2j-no-goto-src", result.ExecutedIntermediateDirectoryNames);

            var finalLib = Path.Combine(outputRoot, ".cs2j-no-goto-src",
                "SampleUnsafeFfmLib", "FrameParsing.cs");
            var text = await File.ReadAllTextAsync(finalLib);

            // goto/label 已完全消除（语法级，忽略注释）；stackalloc 已堆化。
            // goto 状态机会把局部提升为「T x = default;」+ 状态内赋值，故堆化
            // 形态为 chunk = new int[8];。
            Assert.False(HasGotoOrLabelSyntax(text));
            Assert.False(HasStackAllocSyntax(text));
            Assert.Contains("chunk = new int[8];", text);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task M4_NativeMethodsExtern_Rejected_UF060_NoHardCrash()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-test-output-extern-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 独立最小互操作工程（未纳入 sln，且位于 sln 目录之外，不影响
            // 成功 fixture）：非白名单 [DllImport] extern 必须显式收 UF060
            // ——取代设计 §1.2 的两层不一致/项目管线 NotSupportedException。
            var externDir = Path.Combine(AppContext.BaseDirectory, "SampleUnsafeFfmExtern");
            var interopProject = Path.Combine(externDir, "SampleUnsafeFfmExtern.csproj");

            var (result, chainErrors) = await RunPassCapturingErrors(
                interopProject, outputRoot, new[] { PassId });

            Assert.False(result.Success, "extern project unexpectedly passed the pass");
            Assert.Contains("UF060", chainErrors);
            Assert.Contains("Beep", chainErrors);
            Assert.Contains("平台隔离通道（C42）", chainErrors);
            Assert.DoesNotContain("NotSupportedException", chainErrors);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    /// <summary>
    /// 运行链并把链 stderr（诊断/失败横幅）捕获为字符串，避免 xUnit 并行
    /// 运行时控制台输出不可见。
    /// </summary>
    private static async Task<(CliChainResult Result, string Errors)> RunPassCapturingErrors(
        string sourcePath, string outputRoot, IReadOnlyList<string> passIds)
    {
        var debugLog = Path.Combine(Path.GetTempPath(), "cs2j-chain-debug-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var oldError = Console.Error;
            var oldOut = Console.Out;
            CliChainResult result;
            using (var debugWriter = new StreamWriter(debugLog, append: false))
            {
                Console.SetError(debugWriter);
                Console.SetOut(debugWriter);
                try
                {
                    result = await RunPassAsync(
                        sourcePath, outputRoot, verify: true, passIds: passIds);
                }
                finally
                {
                    Console.SetError(oldError);
                    Console.SetOut(oldOut);
                }
            }

            var errors = await File.ReadAllTextAsync(debugLog);
            return (result, errors);
        }
        finally
        {
            if (File.Exists(debugLog)) File.Delete(debugLog);
        }
    }

    private static bool HasGotoOrLabelSyntax(string code)
    {
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes()
            .Any(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.GotoStatementSyntax
                          or Microsoft.CodeAnalysis.CSharp.Syntax.LabeledStatementSyntax);
    }

    private static bool HasStackAllocSyntax(string code)
    {
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Any(n =>
            n is Microsoft.CodeAnalysis.CSharp.Syntax.StackAllocArrayCreationExpressionSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.ImplicitStackAllocArrayCreationExpressionSyntax);
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
