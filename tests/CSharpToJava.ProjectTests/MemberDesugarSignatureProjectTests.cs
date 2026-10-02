using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Signatures;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C46 project-level coverage（设计 §7.2）：SampleMemberDesugarSolution 全 sln（Lib + Tests + App）
/// 以 <c>Signatures.Enabled=true</c> 跑 member-desugar 适配器，VerifyBuild/VerifyTests 默认开
/// —— 中间树 dotnet build + dotnet test（含 SignatureBehaviorTests）绿即证明三类签名改写
/// （显式实现 / new 隐藏 / 协变桥接）的 .NET 半区行为等价。
/// </summary>
/// <remarks>
/// 用 <c>Strict=false</c>：project 级验证聚焦「中间树可编译 + 行为等价」，
/// 不把既有 fixture 上可能出现的 51xx Warning（如 NH-5 residual）升级为 Error 而误判 success。
/// </remarks>
[Collection("SampleMemberDesugarSolution")]
public class MemberDesugarSignatureProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    private static void AssertContainsNormalized(string haystack, string needle)
        => Assert.True(
            new string(haystack.Where(c => !char.IsWhiteSpace(c)).ToArray())
                .Contains(new string(needle.Where(c => !char.IsWhiteSpace(c)).ToArray()),
                    StringComparison.Ordinal),
            $"Expected to contain: {needle}");

    private static void AssertDoesNotContainNormalized(string haystack, string needle)
        => Assert.False(
            new string(haystack.Where(c => !char.IsWhiteSpace(c)).ToArray())
                .Contains(new string(needle.Where(c => !char.IsWhiteSpace(c)).ToArray()),
                    StringComparison.Ordinal),
            $"Expected NOT to contain: {needle}");

    [Fact]
    public async Task Signature_scenarios_are_rewritten_and_intermediate_tree_verifies()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        Assert.True(File.Exists(solutionPath), $"fixture sln not found: {solutionPath}");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-c46-sig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var options = new MemberDesugarOptions
            {
                Signatures = new SignatureOptions
                {
                    Enabled = true,
                    Strict = false,
                },
            };
            var adapter = new MemberDesugarPassAdapter(
                options, verbose: false, filterUnsupportedProjects: true,
                compatNamespacePrefixes: null);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = destination,
                Force = true,
            });

            // verify（restore/build/test）绿：中间树仍是可编译 C# 且行为等价。
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
            // fixture 含 1 个 CriticalFinalizerObject 终结器（恒定 CS2J5205 Error，force 不可解除，
            // 与本规则无关）→ 唯一允许的 Error 是该 5205；signatures 段不得产生任何 Error。
            var errorCodes = adapter.Diagnostics
                .Where(d => d.Severity == MemberDesugarSeverity.Error)
                .Select(d => d.Code)
                .Distinct()
                .ToList();
            Assert.Equal(
                new[] { MemberDesugarDiagnosticCode.FinalizerCriticalBlocked },
                errorCodes);

            // ---- 统计对账（bridge / 改名 / 提升 / 让位）----
            var stats = adapter.Statistics;
            Assert.True(stats.SignatureCovariantChains >= 2, "至少 CR-1 + CR-2 两条协变链");
            Assert.True(stats.SignatureCovariantRetained >= 1, "CR-1 Create 直映保留");
            Assert.True(stats.SignatureCovariantBridged >= 1, "CR-2 Items 擦除冲突");
            Assert.Equal(1, stats.SignatureCovariantBridgesSynthesized);
            Assert.Equal(2, stats.SignatureCovariantRenamed);   // bridge 点 + CovMore 派生
            Assert.True(stats.SignatureHideRenamed >= 3, "Tag + Slot(newslot 链两条)");
            Assert.True(stats.SignaturePlansPromoted >= 2, "PlainShape + CollidingShape 显式实现");
            Assert.True(stats.SignatureDisplacedRenamed >= 1, "CollidingShape 类自有成员让位");

            // ---- 中间树文本断言：Lib 声明侧 ----
            var libRoot = Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib");
            var sig = await File.ReadAllTextAsync(Path.Combine(libRoot, "SignatureSamples.cs"));

            // ① 显式接口：零未归属 ExplicitInterfaceSpecifier；接口体占规范名、类成员让位。
            AssertDoesNotContainNormalized(sig, "IShape.Describe");
            AssertContainsNormalized(sig, "public string Describe()");
            AssertContainsNormalized(sig, "public string Describe_cs2jClass()");
            // ② new 隐藏：零隐藏性 new 令牌；化石隐藏与新虚槽链整组改名。
            AssertDoesNotContainNormalized(sig, "new string Tag()");
            AssertDoesNotContainNormalized(sig, "new virtual");
            AssertContainsNormalized(sig, "public string Tag_cs2jNew()");
            AssertContainsNormalized(sig, "public virtual string Slot_cs2jNew()");
            AssertContainsNormalized(sig, "public override string Slot_cs2jNew()");
            // ③ 协变：CR-1 Create 不桥接；CR-2 Items 合成基槽 bridge + 改名 sub-chain。
            AssertDoesNotContainNormalized(sig, "Create_cs2jCovariant");
            AssertContainsNormalized(sig, "public override IEnumerable<object> Items() => Items_cs2jCovariant();");
            AssertContainsNormalized(sig, "public virtual IEnumerable<string> Items_cs2jCovariant()");
            AssertContainsNormalized(sig, "public override IEnumerable<string> Items_cs2jCovariant()");

            // ---- 中间树文本断言：App 跨文件站点侧 ----
            var app = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarApp", "SignatureApp.cs"));
            AssertContainsNormalized(app, "shape.Describe_cs2jClass()");  // 类静态类型站点改名
            AssertContainsNormalized(app, "((IShape)o).Describe()");      // 强转接口分派点不动
            AssertContainsNormalized(app, "d.Tag_cs2jNew()");             // 隐藏成员派生静态类型站点
            AssertContainsNormalized(app, "d.Items_cs2jCovariant()");     // 协变派生静态类型站点
            AssertContainsNormalized(app, "b.Items()");                   // 基类静态类型站点不动

            // ---- M4c：访问器面跨文件站点（声明在 Lib/DtoSamples.cs，站点在 App/Program.cs）----
            var dto = await File.ReadAllTextAsync(Path.Combine(libRoot, "DtoSamples.cs"));
            AssertDoesNotContainNormalized(dto, "new string Name");
            AssertContainsNormalized(dto, "public string get_Name_cs2jNew()");
            AssertContainsNormalized(dto, "public void set_Name_cs2jNew(string value)");

            var program = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarApp", "Program.cs"));
            AssertContainsNormalized(program, "hd.get_Name_cs2jNew()");    // 派生静态类型站点改名（回退绑定）
            AssertContainsNormalized(program, "hb.get_Name()");            // 基类静态类型站点不动
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}