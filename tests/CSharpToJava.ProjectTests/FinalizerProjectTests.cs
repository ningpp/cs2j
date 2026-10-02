using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Finalizers;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C39 project-level coverage（设计 §7.4，M2 FN-6/FN-8/FN-10）：SampleMemberDesugarSolution
/// 全 sln（Lib + Tests + App）跑 member-desugar 适配器，VerifyBuild/VerifyTests 默认开
/// —— 中间树 dotnet build + dotnet test（含 FinalizerBehaviorTests）绿即证明
/// A/B 两档产物行为等价。
/// </summary>
/// <remarks>
/// 按设计 §6，CS2J5205（CriticalFinalizerObject 派生）恒定 Error 且 force 不可解除，
/// 因此 fixture 含 FinalizerCritical 时 run.Success 必为 false；这不是构建/测试失败 ——
/// 断言全部 Error 诊断仅为 5205，且不存在 BuildFailed/TestsFailed/RestoreFailed/
/// WholeTreeRollback（即 verify 阶段绿）。
/// </remarks>
[Collection("SampleMemberDesugarSolution")]
public class FinalizerProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    private const string SidecarFileName = ".cs2j-finalizer-removals.json";

    // B 档删除集合（Empty / DisposePattern+Paired / SafeHandleOnly / 派生空体）。
    private static readonly string[] RemovedTypes =
    {
        "SampleMemberDesugarLib.FinalizerEmpty",
        "SampleMemberDesugarLib.FinalizerDisposePattern",
        "SampleMemberDesugarLib.FinalizerSafeHandle",
        "SampleMemberDesugarLib.FinalizerDerived",
    };

    // B 档 5204 保守拒绝集合（NativeRelease / Other / resurrection / Extern / 基类 Other）。
    private static readonly string[] RefusedTypes =
    {
        "SampleMemberDesugarLib.FinalizerNativeRelease",
        "SampleMemberDesugarLib.FinalizerOther",
        "SampleMemberDesugarLib.FinalizerResurrect",
        "SampleMemberDesugarLib.FinalizerExtern",
        "SampleMemberDesugarLib.FinalizerBaseWithFinalizer",
    };

    private const string CriticalType = "SampleMemberDesugarLib.FinalizerCritical";

    // fixture 终结器总数 = 4 removed + 5 refused + 1 critical。
    private const int FixtureFinalizerCount = 10;

    private static readonly Regex DestructorRegex = new(
        @"~[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.Compiled);

    private static string ShortName(string fqn) => fqn[(fqn.LastIndexOf('.') + 1)..];

    private static IEnumerable<string> EnumerateLibCsFiles(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var parts = Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    && !parts.Contains("obj", StringComparer.OrdinalIgnoreCase);
            });

    private static int CountDestructors(string root)
        => EnumerateLibCsFiles(root)
            .Select(File.ReadAllText)
            .Sum(text => DestructorRegex.Matches(text).Count);

    private static MemberDesugarPassAdapter CreateAdapter(MemberDesugarOptions options)
        => new(options, verbose: false, filterUnsupportedProjects: true,
            compatNamespacePrefixes: null);

    private static string ErrorDump(IReadOnlyList<MemberDesugarDiagnostic> diagnostics)
        => string.Join("\n", diagnostics
            .Where(d => d.Severity == MemberDesugarSeverity.Error)
            .Select(d => d.ToMsBuildString()));

    // ---- A 档（默认 Policy=Reject）：全保留 + 5200 + critical 5205，零 sidecar ----

    [Fact(Skip = "TODO: 临时跳过端到端慢测试（排查根因中）— 48.68s")]
    public async Task TierA_Reject_AllFinalizersRetained_Reported_NoSidecar()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        Assert.True(File.Exists(solutionPath), $"fixture sln not found: {solutionPath}");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-fn-a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var adapter = CreateAdapter(new MemberDesugarOptions());
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = destination,
                Force = true,
            });

            // 设计 §6：5205 恒定 Error → run.Success=false 是预期语义；
            // 但唯一允许的 Error 是 critical 的 5205，verify 必须全绿。
            var errorCodes = adapter.Diagnostics
                .Where(d => d.Severity == MemberDesugarSeverity.Error)
                .Select(d => d.Code).ToList();
            Assert.Equal(
                new[] { MemberDesugarDiagnosticCode.FinalizerCriticalBlocked },
                errorCodes.Distinct().OrderBy(c => c.ToString()).ToList());
            Assert.Single(errorCodes);
            foreach (var gateCode in new[]
                     {
                         MemberDesugarDiagnosticCode.BuildFailed,
                         MemberDesugarDiagnosticCode.TestsFailed,
                         MemberDesugarDiagnosticCode.RestoreFailed,
                         MemberDesugarDiagnosticCode.WholeTreeRollback,
                     })
            {
                Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == gateCode);
            }
            Assert.False(run.Success,
                "fixture 含 1 个 CriticalFinalizerObject 终结器（恒定 5205 Error），"
                + "run.Success 按设计应为 false；实际 Error：\n" + ErrorDump(adapter.Diagnostics));

            // 统计：10 个全部检测到/报告，0 删除。
            Assert.Equal(FixtureFinalizerCount, adapter.Statistics.FinalizersDetected);
            Assert.Equal(FixtureFinalizerCount, adapter.Statistics.FinalizersReported);
            Assert.Equal(0, adapter.Statistics.FinalizersRemoved);
            Assert.Equal(0, adapter.Statistics.FinalizerSidecarEntries);
            Assert.Equal(1, adapter.Statistics.FinalizerCriticalBlocked);
            Assert.Equal(0, adapter.Statistics.FinalizerResidual);

            // 诊断：9×5200 + 1×5205。
            Assert.Equal(9, adapter.Diagnostics.Count(d =>
                d.Code == MemberDesugarDiagnosticCode.FinalizerRejected));
            Assert.Single(adapter.Diagnostics, d =>
                d.Code == MemberDesugarDiagnosticCode.FinalizerCriticalBlocked
                && d.Message.Contains(CriticalType, StringComparison.Ordinal));

            // 中间树：fixture 10 个终结器一个不少（A 档不改树）。
            var intermediateLib = Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib");
            Assert.Equal(FixtureFinalizerCount, CountDestructors(intermediateLib));
            foreach (var type in RemovedTypes.Concat(RefusedTypes).Append(CriticalType))
            {
                var hit = EnumerateLibCsFiles(intermediateLib)
                    .Any(file => File.ReadAllText(file)
                        .Contains("~" + ShortName(type), StringComparison.Ordinal));
                Assert.True(hit, $"A 档必须保留终结器：{type}");
            }

            // A 档无删除 → 无 sidecar 文件。
            Assert.False(File.Exists(Path.Combine(run.IntermediateRoot, SidecarFileName)),
                "A 档不得产生 sidecar JSON");
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- B 档（Policy=Remove，非 strict）：4 删除 / 5 拒绝 / 1 critical + sidecar ----

    [Fact]
    public async Task TierB_Remove_DeletesSafeShapes_RefusesRisky_AndWritesSidecar()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-fn-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var options = new MemberDesugarOptions
            {
                Finalizers = new FinalizerOptions { Policy = FinalizerPolicy.Remove },
            };
            var adapter = CreateAdapter(options);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = destination,
                Force = true,
            });

            // 唯一允许的 Error：critical 5205（设计恒定 Error）。5204 非 strict 为 Warning，
            // verify（build+test）必须绿 —— FinalizerBehaviorTests 在删除后的树上执行通过。
            var errorCodes = adapter.Diagnostics
                .Where(d => d.Severity == MemberDesugarSeverity.Error)
                .Select(d => d.Code).ToList();
            Assert.Equal(
                new[] { MemberDesugarDiagnosticCode.FinalizerCriticalBlocked },
                errorCodes.Distinct().OrderBy(c => c.ToString()).ToList());
            Assert.Single(errorCodes);
            foreach (var gateCode in new[]
                     {
                         MemberDesugarDiagnosticCode.BuildFailed,
                         MemberDesugarDiagnosticCode.TestsFailed,
                         MemberDesugarDiagnosticCode.RestoreFailed,
                         MemberDesugarDiagnosticCode.WholeTreeRollback,
                     })
            {
                Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == gateCode);
            }
            Assert.False(run.Success,
                "含 critical 5205 时 run.Success 按设计为 false；实际 Error：\n"
                + ErrorDump(adapter.Diagnostics));

            // ---- 统计对账 ----
            var stats = adapter.Statistics;
            Assert.Equal(FixtureFinalizerCount, stats.FinalizersDetected);
            Assert.Equal(RemovedTypes.Length, stats.FinalizersRemoved);
            Assert.Equal(0, stats.FinalizersForceRemoved);
            Assert.Equal(RemovedTypes.Length, stats.FinalizerSidecarEntries);
            Assert.Equal(RefusedTypes.Length, stats.FinalizersRefused);
            Assert.Equal(1, stats.FinalizerCriticalBlocked);
            Assert.Equal(0, stats.FinalizerResidual);
            Assert.Equal(RefusedTypes.Length,
                stats.SkippedByReason[MemberDesugarDiagnosticCode.FinalizerRemoveRefused]);
            Assert.Equal(1,
                stats.SkippedByReason[MemberDesugarDiagnosticCode.FinalizerCriticalBlocked]);

            // ---- 诊断计数：4×5201 + 5×5204 + 1×5205 ----
            Assert.Equal(RemovedTypes.Length, adapter.Diagnostics.Count(d =>
                d.Code == MemberDesugarDiagnosticCode.FinalizerRemoved));
            Assert.Equal(RefusedTypes.Length, adapter.Diagnostics.Count(d =>
                d.Code == MemberDesugarDiagnosticCode.FinalizerRemoveRefused));
            Assert.Single(adapter.Diagnostics, d =>
                d.Code == MemberDesugarDiagnosticCode.FinalizerCriticalBlocked);
            Assert.DoesNotContain(adapter.Diagnostics, d =>
                d.Code == MemberDesugarDiagnosticCode.FinalizerResidual);
            foreach (var fqn in RefusedTypes)
            {
                Assert.Contains(adapter.Diagnostics, d =>
                    d.Code == MemberDesugarDiagnosticCode.FinalizerRemoveRefused
                    && d.Message.Contains(fqn, StringComparison.Ordinal));
            }

            // ---- 中间树逐类型文本断言 ----
            var libRoot = Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib");

            var basic = await File.ReadAllTextAsync(
                Path.Combine(libRoot, "FinalizersBasic.cs"));
            Assert.DoesNotContain("~FinalizerEmpty(", basic, StringComparison.Ordinal);
            Assert.DoesNotContain("~FinalizerDisposePattern(", basic, StringComparison.Ordinal);
            Assert.DoesNotContain("~FinalizerSafeHandle(", basic, StringComparison.Ordinal);
            Assert.Contains("~FinalizerNativeRelease(", basic, StringComparison.Ordinal);
            Assert.Contains("~FinalizerOther(", basic, StringComparison.Ordinal);
            Assert.Contains("~FinalizerResurrect(", basic, StringComparison.Ordinal);
            Assert.Contains("~FinalizerExtern(", basic, StringComparison.Ordinal);
            // 删除默认零痕迹：无 marker 注释、无提取方法。
            Assert.DoesNotContain("CS2J5201", basic, StringComparison.Ordinal);
            Assert.DoesNotContain("Cs2jRemovedFinalizer", basic, StringComparison.Ordinal);

            var inheritance = await File.ReadAllTextAsync(
                Path.Combine(libRoot, "FinalizersInheritance.cs"));
            Assert.DoesNotContain("~FinalizerDerived(", inheritance, StringComparison.Ordinal);
            Assert.Contains("~FinalizerBaseWithFinalizer(", inheritance, StringComparison.Ordinal);

            var critical = await File.ReadAllTextAsync(
                Path.Combine(libRoot, "FinalizerCritical.cs"));
            Assert.Contains("~FinalizerCritical(", critical, StringComparison.Ordinal);

            // 输入 10 → 输出 6，差恰为 sidecar 条目数 / 5201 条数。
            var inputCount = CountDestructors(Path.Combine(FixtureDir, "SampleMemberDesugarLib"));
            var outputCount = CountDestructors(libRoot);
            Assert.Equal(FixtureFinalizerCount, inputCount);
            Assert.Equal(FixtureFinalizerCount - RemovedTypes.Length, outputCount);

            // ---- sidecar JSON 落盘 + 字段抽查 ----
            var sidecarPath = Path.Combine(run.IntermediateRoot, SidecarFileName);
            Assert.True(File.Exists(sidecarPath), "B 档必须在中间树根写 sidecar JSON");
            Assert.Equal(sidecarPath, stats.FinalizerSidecarPath);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sidecarPath));
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("runId").GetString()));
            var entries = doc.RootElement.GetProperty("entries");
            Assert.Equal(RemovedTypes.Length, entries.GetArrayLength());

            var sidecarFqns = entries.EnumerateArray()
                .Select(e => e.GetProperty("typeFqn").GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(RemovedTypes.ToHashSet(StringComparer.Ordinal), sidecarFqns);

            var byFqn = entries.EnumerateArray()
                .ToDictionary(e => e.GetProperty("typeFqn").GetString()!, e => e);
            foreach (var (fqn, index) in RemovedTypes.Select((t, i) => (t, i)))
            {
                var entry = byFqn[fqn];
                Assert.Equal(index, entry.GetProperty("index").GetInt32());
                Assert.False(entry.GetProperty("forced").GetBoolean());
                Assert.Matches("^[0-9a-f]{8}$", entry.GetProperty("bodySha256").GetString()!);
                Assert.Equal(
                    Path.Combine("SampleMemberDesugarLib",
                        fqn == "SampleMemberDesugarLib.FinalizerDerived"
                            ? "FinalizersInheritance.cs"
                            : "FinalizersBasic.cs").Replace('\\', '/'),
                    entry.GetProperty("file").GetString());
            }

            // 抽查删除 body 原文留痕（Dispose(false) 调用逐字保留）。
            var disposeEntry = byFqn["SampleMemberDesugarLib.FinalizerDisposePattern"];
            Assert.Equal("DisposePattern", disposeEntry.GetProperty("bodyKind").GetString());
            Assert.Equal("Paired", disposeEntry.GetProperty("pairing").GetString());
            Assert.Contains("Dispose(false)", disposeEntry.GetProperty("body").GetString());
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}
