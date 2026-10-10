using System.Diagnostics;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C10 M3 「平台边界零干扰」证据：同一份含互操作/平台注解的最小项目，分别在
/// --disable-pass attribute-normalize 链与默认链（2026-10-06 翻转后 C10 默认开）
/// 下运行，随后对两个最终树用转换器同一批边界 analyzer
/// （CS2J3001/3101/3102/3201/3202/3203）重放，按 (码, 相对文件, 行, 消息)
/// 归一化后逐条一致；C10 开启不新增任何 CS2J13xx，AN003 Info 仅 verbose 可见
/// 且只引用主管辖码；DllImport 声明所在文件逐字节保留。
///
/// 现状基线（以实际管线为准，测试不改变现状）：C06 unsafe-ffm 在默认链中开启，
/// 对 extern/DllImport 发 UF060 Error，链在 unsafe-ffm 后中止，因此两种模式的
/// 最终树都是 .cs2j-unsafe-ffm-src；项目管线既有的两条文件级豁免
/// （NativeMethods.cs 跳过 native-interop；*.AssemblyInfo.cs 跳过 platform）
/// 通过 BoundaryAnalysisHelpers 原样复刻。
/// </summary>
public class AttributeNormalizePlatformZeroInterferenceTests
{
    private static string CliPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(
                    dir, "src", "CSharpToJava.CLI", "bin", "Debug", "net10.0",
                    "CSharpToJava.CLI.dll");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            throw new FileNotFoundException("Could not locate CSharpToJava.CLI.dll from test output directory.");
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{CliPath}\" {string.Join(' ', args.Select(a => $"\"{a}\""))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private const string ProjectFile =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>disable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    // NativeMethods.cs：同时覆盖互操作（DllImport+extern）与平台（SupportedOSPlatform），
    // 文件名命中项目管线的 P/Invoke 容器豁免。
    private const string NativeMethodsSource =
        """
        using System.Runtime.InteropServices;
        using System.Runtime.Versioning;

        [SupportedOSPlatform("windows")]
        internal static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern int GetCurrentProcess();
        }
        """;

    // 非 NativeMethods 命名：OS guard 两件套 + 平台 API 探测。
    private const string PlatformGuardsSource =
        """
        using System.Runtime.Versioning;

        internal static class PlatformGuards
        {
            [SupportedOSPlatformGuard("windows")]
            internal static bool IsWindows() => System.OperatingSystem.IsWindows();

            [UnsupportedOSPlatform("browser")]
            internal static void BrowserUnsupported()
            {
            }
        }
        """;

    // 非容器文件上的互操作 attribute（MarshalAs 无需配套 Guid 即可编译），
    // 保证 CS2J3201 在 NativeMethods.cs 豁免之外仍有发射点。
    private const string MarshalSiteSource =
        """
        using System.Runtime.InteropServices;

        internal sealed class MarshalSite
        {
            internal void Pin([MarshalAs(UnmanagedType.I4)] int code)
            {
            }
        }
        """;

    private static string CreateWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "cs2j-an-zero-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Interop.csproj"), ProjectFile);
        File.WriteAllText(Path.Combine(dir, "NativeMethods.cs"), NativeMethodsSource);
        File.WriteAllText(Path.Combine(dir, "PlatformGuards.cs"), PlatformGuardsSource);
        File.WriteAllText(Path.Combine(dir, "MarshalSite.cs"), MarshalSiteSource);
        return dir;
    }

    [SlowE2EFact(97.3)]
    public async Task DefaultChain_And_C10DisabledChain_Emit_Identical_PlatformBoundarySignals_And_Preserve_Bytes()
    {
        var sourceDir = CreateWorkspace();
        // 输出必须放在源目录之外：第二跑会把第一跑的输出树当源文件枚举。
        var runId = Guid.NewGuid().ToString("N");
        var disabledDest = Path.Combine(Path.GetTempPath(), $"cs2j-an-zero-{runId}-disabled");
        var defaultDest = Path.Combine(Path.GetTempPath(), $"cs2j-an-zero-{runId}-default");

        try
        {
            // ── 模式 1：默认链显式摘除 C10（attribute-normalize 关） ──
            var (disabledExit, _, disabledStderr) = await RunCliAsync(
                "preprocess", "--disable-pass", "attribute-normalize",
                "-s", sourceDir, "-d", disabledDest);
            Assert.True(disabledExit == 1,
                $"disabled run should abort at C06 UF060; stderr:\n{disabledStderr}");

            // ── 模式 2：默认链（C10 自 2026-10-06 起默认开，非 verbose） ──
            var (defaultExit, _, defaultStderr) = await RunCliAsync(
                "preprocess",
                "-s", sourceDir, "-d", defaultDest);
            Assert.True(defaultExit == 1,
                $"default run should abort at C06 UF060; stderr:\n{defaultStderr}");

            // C10 仅存在于默认链。
            Assert.True(Directory.Exists(Path.Combine(
                defaultDest, ".cs2j-attribute-normalize-src")));
            Assert.False(Directory.Exists(Path.Combine(
                disabledDest, ".cs2j-attribute-normalize-src")));

            // 非 verbose：C10 不发射 AN003，也不发射任何 CS2J13xx。
            Assert.DoesNotContain("AN003", defaultStderr);
            Assert.DoesNotContain("CS2J13", defaultStderr);
            Assert.DoesNotContain("CS2J13", disabledStderr);

            // 两种模式都在 C06 中止，最终树为 unsafe-ffm。
            var baselineTree = Path.Combine(disabledDest, ".cs2j-unsafe-ffm-src");
            var enabledTree = Path.Combine(defaultDest, ".cs2j-unsafe-ffm-src");
            Assert.True(Directory.Exists(baselineTree));
            Assert.True(Directory.Exists(enabledTree));

            // ── 边界诊断重放：归一化集合逐条一致 ──
            var baselineSignals = CollectBoundarySignals(baselineTree);
            var enabledSignals = CollectBoundarySignals(enabledTree);
            Assert.Equal(baselineSignals, enabledSignals);

            // 锁定实际基线下的完整集合（码+相对文件+行+消息）。
            var expected = new (string Code, string File, int Line, string Message)[]
            {
                ("CS2J3201", "MarshalSite.cs", 5,
                    "Native interop attribute 'MarshalAs' is not supported by the Java-only conversion pipeline."),
                ("CS2J3101", "NativeMethods.cs", 4,
                    "Platform boundary attribute 'SupportedOSPlatform' must be isolated before Java conversion."),
                ("CS2J3101", "PlatformGuards.cs", 5,
                    "Platform boundary attribute 'SupportedOSPlatformGuard' must be isolated before Java conversion."),
                ("CS2J3102", "PlatformGuards.cs", 6,
                    "Platform-specific API 'System.OperatingSystem.IsWindows' must be isolated before Java conversion."),
                ("CS2J3101", "PlatformGuards.cs", 8,
                    "Platform boundary attribute 'UnsupportedOSPlatform' must be isolated before Java conversion."),
            };
            Assert.Equal(expected.Select(FormatSignal).ToArray(), enabledSignals);

            // ── 字节保留：两个最终树逐文件哈希一致 ──
            var baselineHashes = HashTree(baselineTree);
            var enabledHashes = HashTree(enabledTree);
            Assert.Equal(baselineHashes.Select(kv => kv.Key).ToArray(),
                         enabledHashes.Select(kv => kv.Key).ToArray());
            Assert.Equal(baselineHashes, enabledHashes);

            // C10 的中间树相对源输入同样逐字节保留（DllImport 声明区间即整文件）。
            var c10Tree = Path.Combine(defaultDest, ".cs2j-attribute-normalize-src");
            foreach (var file in new[] { "NativeMethods.cs", "PlatformGuards.cs", "MarshalSite.cs" })
            {
                Assert.Equal(
                    await File.ReadAllTextAsync(Path.Combine(sourceDir, file)),
                    await File.ReadAllTextAsync(Path.Combine(c10Tree, file)));
            }

            // DllImport 声明区间在开启前后逐字节相同。
            var baselineDecl = ExtractDllImportRange(Path.Combine(baselineTree, "NativeMethods.cs"));
            var enabledDecl = ExtractDllImportRange(Path.Combine(enabledTree, "NativeMethods.cs"));
            Assert.Equal(baselineDecl, enabledDecl);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
            Directory.Delete(disabledDest, recursive: true);
            Directory.Delete(defaultDest, recursive: true);
        }
    }

    [Fact]
    public async Task C10Enabled_Verbose_Shows_AN003_Referencing_Only_Governing_Codes()
    {
        var sourceDir = CreateWorkspace();
        var destDir = Path.Combine(Path.GetTempPath(),
            $"cs2j-an-zero-{Guid.NewGuid().ToString("N")}-verbose");

        try
        {
            // 单跑 C10 + verbose：验证 AN003 的可见性与消息形态。
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "attribute-normalize", "--verbose",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            var an003Lines = stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("AN003"))
                .ToArray();

            // 5 个受管辖出现点：DllImport(3201+3203)、SupportedOSPlatform(3101)、
            // SupportedOSPlatformGuard(3101)、UnsupportedOSPlatform(3101)、MarshalAs(3201)。
            Assert.Equal(5, an003Lines.Length);
            Assert.Contains(an003Lines, l =>
                l.Contains("DllImport") && l.Contains("CS2J3201/CS2J3203"));
            Assert.Contains(an003Lines, l =>
                l.Contains("SupportedOSPlatformAttribute'") && l.Contains("CS2J3101"));
            Assert.Contains(an003Lines, l =>
                l.Contains("SupportedOSPlatformGuardAttribute") && l.Contains("CS2J3101"));
            Assert.Contains(an003Lines, l =>
                l.Contains("UnsupportedOSPlatformAttribute'") && l.Contains("CS2J3101"));
            Assert.Contains(an003Lines, l =>
                l.Contains("MarshalAs") && l.Contains("CS2J3201"));

            // 消息不复述拒绝内容。
            Assert.All(an003Lines, l =>
            {
                Assert.DoesNotContain("not supported", l, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("reject", l, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
            Directory.Delete(destDir, recursive: true);
        }
    }

    // ── helpers ──

    private static string[] CollectBoundarySignals(string treeRoot)
    {
        var csFiles = Directory.GetFiles(treeRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnderBuildArtifact(f))
            .ToArray();
        var trees = csFiles
            .Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(
                File.ReadAllText(f), path: f))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "boundary-probe", trees, TpaReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var signals = new List<(string Code, string File, int Line, string Message)>();
        foreach (var tree in trees)
        {
            AddSignals(signals, treeRoot, UnsupportedDomainAnalyzer.AnalyzeSyntaxTree(tree));

            // 复刻 ProjectPlatformBoundaryCheckPass：跳过 MSBuild 生成的 AssemblyInfo。
            if (!BoundaryAnalysisHelpers.IsGeneratedAssemblyInfoFile(tree.FilePath))
            {
                AddSignals(signals, treeRoot, PlatformBoundaryAnalyzer.AnalyzeSyntaxTree(
                    tree, compilation.GetSemanticModel(tree)));
            }

            // 复刻 ProjectNativeInteropCheckPass：NativeMethods.cs 容器文件整文跳过。
            if (!BoundaryAnalysisHelpers.IsPInvokeContainerFile(tree.FilePath))
            {
                AddSignals(signals, treeRoot, NativeInteropAnalyzer.AnalyzeSyntaxTree(
                    tree, compilation.GetSemanticModel(tree)));
            }
        }

        return signals
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Code, StringComparer.Ordinal)
            .Select(FormatSignal)
            .ToArray();
    }

    private static void AddSignals(
        List<(string, string, int, string)> sink,
        string treeRoot,
        IReadOnlyList<DiagnosticMessage> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var span = diagnostic.Location!.GetLineSpan();
            sink.Add((
                diagnostic.Code!,
                Path.GetRelativePath(treeRoot, span.Path),
                span.StartLinePosition.Line + 1,
                diagnostic.Message));
        }
    }

    private static string FormatSignal((string Code, string File, int Line, string Message) signal)
        => $"{signal.Code}|{signal.File}|{signal.Line}|{signal.Message}";

    private static MetadataReference[] TpaReferences()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

    private static Dictionary<string, string> HashTree(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            // obj 只删 bin 不删 obj：NuGet 的 project.assets/dgspec 含绝对路径与
            // 时间戳，属非确定性生成工件，字节证据只覆盖源码面。
            .Where(f => !IsUnderBuildArtifact(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToDictionary(
                f => Path.GetRelativePath(root, f),
                f => Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

    private static bool IsUnderBuildArtifact(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");

    /// <summary>截取 [DllImport(...)] 特性行到其 extern 声明行（含两端）。</summary>
    private static string ExtractDllImportRange(string file)
    {
        var lines = File.ReadAllLines(file);
        var start = -1;
        var end = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("[DllImport(")) start = i;
            if (start >= 0 && lines[i].Contains("extern ") && i > start)
            {
                end = i;
                break;
            }
        }

        Assert.True(start >= 0 && end >= 0, "DllImport declaration range not found.");
        return string.Join('\n', lines.Skip(start).Take(end - start + 1));
    }
}
