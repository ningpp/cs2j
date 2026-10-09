using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.PlatformIsolation;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.Tests;

/// <summary>
/// C42 M2 Core apply 单测（设计 §7.1）：探针重定向/保守值、复合 guard 短路、
/// guard 擦除、平台服务类型 PNSE 桩 + 签名移植、互操作零桩、幂等、对账与
/// 输出树零编译错误。
/// </summary>
public class PlatformIsolationApplyTests
{
    private static readonly string TypeMappingPath =
        Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json");

    private static IReadOnlyCollection<string> CompatPrefixes()
        => new TypeMappingRegistry(TypeMappingPath).GetExplicitNamespaceMappings();

    private static CSharpCompilation BuildCompilation(params (string Path, string Content)[] files)
    {
        var trees = files
            .Select(file => CSharpSyntaxTree.ParseText(
                file.Content, new CSharpParseOptions(LanguageVersion.Latest), file.Path))
            .ToList();
        trees.Add(ConversionPipeline.GlobalUsingsTree);

        return CSharpCompilation.Create(
            "PlatformIsolationApplyAsm",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true)
                .WithUsings("System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Threading.Tasks"));
    }

    private static string WriteConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cs2j-pi-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static PlatformIsolationApplyResult Run(
        PlatformIsolationOptions options,
        bool isTestProject = false,
        params (string Path, string Content)[] files)
    {
        var preprocessor = new PlatformIsolationPreprocessor();
        preprocessor.BindCompilation(BuildCompilation(files), options, isTestProject, CompatPrefixes());
        return preprocessor.RewriteAll();
    }

    private static string SingleOutput(PlatformIsolationApplyResult result, string path)
        => result.Files.Single(f => f.FilePath == path).OutputCode;

    private static void AssertOutputCompiles(PlatformIsolationApplyResult result)
    {
        var trees = result.Files
            .Select(f => CSharpSyntaxTree.ParseText(f.OutputCode, path: f.FilePath))
            .ToList();
        trees.AddRange(result.GeneratedFiles.Select(g =>
            CSharpSyntaxTree.ParseText(g.Content, path: g.RelativePath)));
        trees.Add(ConversionPipeline.GlobalUsingsTree);

        var compilation = CSharpCompilation.Create(
            "PlatformIsolationOutputAsm",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true)
                .WithUsings("System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Threading.Tasks"));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void StubMode_RedirectsProbe_AndEmitsShim()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Stub, ConfigPath = config };
            var result = Run(options, false,
                ("Probe.cs", "class C { public static bool A() => System.OperatingSystem.IsWindows(); }"));

            var output = SingleOutput(result, "Probe.cs");
            Assert.Contains("Cs2j.Platform.Cs2jPlatformProbe.IsWindows()", output);
            Assert.DoesNotContain("System.OperatingSystem.IsWindows()", output);

            var shim = Assert.Single(result.GeneratedFiles);
            Assert.Equal("generated/Cs2jPlatformProbe.g.cs", shim.RelativePath);
            Assert.Contains("Cs2jPlatformProbe", shim.Content);
            Assert.Contains("IsWindows() => false", shim.Content);

            Assert.Contains(result.Diagnostics, d => d.Code == "PI011" && d.Message.Contains("false"));
            Assert.True(result.Statistics.InvariantHolds);
            Assert.False(result.HasErrors);
            AssertOutputCompiles(result);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void StubMode_RedirectsPropertyProbe_DescriptionInterpolationCompiles()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Stub, ConfigPath = config };
            var result = Run(options, false,
                ("Desc.cs", "class C { public static string A() => $\"os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}\"; }"));

            var output = SingleOutput(result, "Desc.cs");
            Assert.Contains("Cs2j.Platform.Cs2jPlatformProbe.OSDescription()", output);
            AssertOutputCompiles(result);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void StubMode_CompoundGuard_PreservesShortCircuitOrder()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Stub, ConfigPath = config };
            var result = Run(options, false, ("Guard.cs", """
                class G
                {
                    public static bool Check()
                        => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                           || System.OperatingSystem.IsLinux();
                }
                """));

            var output = SingleOutput(result, "Guard.cs");
            var first = output.IndexOf("Cs2j.Platform.Cs2jPlatformProbe.IsOSPlatform(", StringComparison.Ordinal);
            var orPos = output.IndexOf("||", StringComparison.Ordinal);
            var second = output.IndexOf("Cs2j.Platform.Cs2jPlatformProbe.IsLinux()", StringComparison.Ordinal);
            Assert.True(first >= 0 && orPos > first && second > orPos, output);
            AssertOutputCompiles(result);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void StubMode_StripsGuardAttributes_AllPositions()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Stub, ConfigPath = config };
            var result = Run(options, false, ("Guarded.cs", """
                [assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
                [System.Runtime.Versioning.UnsupportedOSPlatform("linux")]
                class Guarded
                {
                    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
                    public static void M() { }
                }
                """));

            var output = SingleOutput(result, "Guarded.cs");
            Assert.DoesNotContain("SupportedOSPlatform", output);
            Assert.DoesNotContain("UnsupportedOSPlatform", output);
            Assert.DoesNotContain("SupportedOSPlatformGuard", output);
            Assert.Contains(result.Diagnostics, d => d.Code == "PI012");
            AssertOutputCompiles(result);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void StubMode_PlatformType_ThrowStubWithSignaturePort_Compiles()
    {
        var config = WriteConfig("{\"policies\":{\"PlatformType\":\"stub\"}}");
        try
        {
            var options = new PlatformIsolationOptions
            {
                Mode = PlatformIsolationMode.Stub,
                EnableThrowStub = true,
                ConfigPath = config,
            };
            var result = Run(options, false, ("Platform.cs", """
                class PlatformWrapper
                {
                    public static void Write(Microsoft.Win32.RegistryKey key, string value)
                    {
                        System.Console.WriteLine(key);
                    }
                }
                """));

            var output = SingleOutput(result, "Platform.cs");
            Assert.Contains("object key", output);
            Assert.Contains("System.PlatformNotSupportedException", output);
            Assert.Contains("isolated by platform-isolation", output);
            Assert.Contains(result.Diagnostics, d => d.Code == "PI010" && d.Message.Contains("-> object"));
            AssertOutputCompiles(result);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void StubMode_MixedFile_OnlyHitMemberChanged()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Stub, ConfigPath = config };
            var result = Run(options, false, ("Mixed.cs", """
                class Mixed
                {
                    public static bool Check() => System.OperatingSystem.IsWindows();
                    public static int Plain(int x) => x + 1;
                }
                """));

            var output = SingleOutput(result, "Mixed.cs");
            Assert.Contains("Cs2j.Platform.Cs2jPlatformProbe.IsWindows()", output);
            Assert.Contains("public static int Plain(int x) => x + 1;", output);
            AssertOutputCompiles(result);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void RejectMode_Interop_IsNeverStubbed_AndFailsWithPI030()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Reject, ConfigPath = config };
            var result = Run(options, false, ("Interop.cs", """
                using System.Runtime.InteropServices;
                class N
                {
                    [DllImport("k.dll")] static extern bool Beep();
                    static extern int Raw();
                }
                """));

            var output = SingleOutput(result, "Interop.cs");
            Assert.DoesNotContain("PlatformNotSupportedException", output);
            Assert.DoesNotContain("Cs2jPlatformProbe", output);
            Assert.True(result.HasErrors);
            Assert.Contains(result.Diagnostics, d => d.Code == "PI030" && d.Message.Contains("CS2J3201"));
            Assert.Contains(result.Diagnostics, d => d.Code == "PI030" && d.Message.Contains("CS2J3203"));
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void StubMode_ExternIsNeverStubbed()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Stub, ConfigPath = config };
            var result = Run(options, false, ("Interop.cs", """
                using System.Runtime.InteropServices;
                class N
                {
                    static extern int Raw();
                }
                """));

            var output = SingleOutput(result, "Interop.cs");
            Assert.DoesNotContain("PlatformNotSupportedException", output);
            Assert.DoesNotContain("Cs2jPlatformProbe", output);
            Assert.True(result.HasErrors);
            Assert.Contains(result.Diagnostics, d => d.Code == "PI030");
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void StubMode_IsIdempotent()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Stub, ConfigPath = config };
            var first = Run(options, false,
                ("Probe.cs", "class C { public static bool A() => System.OperatingSystem.IsWindows(); }"));
            Assert.DoesNotContain(first.Diagnostics, d => d.Code == "PI031");

            var output = SingleOutput(first, "Probe.cs");
            var second = Run(options, false, ("Probe.cs", output));

            Assert.DoesNotContain(second.Diagnostics, d => d.Code == "PI011");
            Assert.Empty(second.GeneratedFiles);
            Assert.False(second.HasErrors);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void ReportMode_DoesNotRewriteAnything()
    {
        var config = WriteConfig("{}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Report, ConfigPath = config };
            var result = Run(options, false,
                ("Probe.cs", "class C { public static bool A() => System.OperatingSystem.IsWindows(); }"));

            var file = result.Files.Single(f => f.FilePath == "Probe.cs");
            Assert.False(file.Changed);
            Assert.Empty(result.GeneratedFiles);
            Assert.Equal(1, result.Statistics.Stubbed);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // ── C42 M3 Facade ───────────────────────────────────────────────────────

    [Fact]
    public void FacadeMode_PlatformType_EmitsPortSeamAndUserSlot_Compiles()
    {
        var config = WriteConfig("{\"policies\":{\"PlatformType\":\"facade\"}}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Facade, ConfigPath = config };
            var result = Run(options, false, ("Registry.cs", """
                class RegistryWrapper
                {
                    public void Work(Microsoft.Win32.RegistryKey key)
                    {
                        System.Console.WriteLine(key);
                    }
                }
                """));

            var output = SingleOutput(result, "Registry.cs");

            // 原类型 partial 化，被隔离成员整体委托端口（D5：RegistryKey -> object）。
            Assert.Contains("partial class RegistryWrapper", output);
            Assert.Contains("void Work(object key)", output);
            Assert.Contains("=> Cs2jRequiredPort.Work(key)", output);
            Assert.DoesNotContain("Microsoft.Win32", output);

            // 缝：可空端口字段 + 未供给即 PNSE 的属性 + 无实现 partial hook。
            Assert.Contains("IRegistryWrapperPort? _cs2jPort;", output);
            Assert.Contains("IRegistryWrapperPort Cs2jRequiredPort", output);
            Assert.Contains("throw new System.PlatformNotSupportedException", output);
            Assert.Contains("partial void ConfigureCs2jPort(ref IRegistryWrapperPort? port);", output);

            // 恰好两个生成文件：端口接口 + 用户槽位（无探针 shim）。
            Assert.Equal(2, result.GeneratedFiles.Count);
            var port = result.GeneratedFiles.Single(
                g => g.RelativePath == "generated/IRegistryWrapperPort.g.cs");
            Assert.Contains("internal interface IRegistryWrapperPort", port.Content);
            Assert.Contains("void Work(object key);", port.Content);
            Assert.DoesNotContain("Microsoft.Win32", port.Content);

            var slot = result.GeneratedFiles.Single(
                g => g.RelativePath == "generated/RegistryWrapper.PortSlot.g.cs");
            // D8/R2 红线：槽位剥离注释/指令/空行后只剩 partial 空壳，零实现。
            var slotCodeLines = slot.Content
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("//") && !line.StartsWith("#"))
                .ToList();
            Assert.Equal(
                new[] { "internal partial class RegistryWrapper", "{", "}" },
                slotCodeLines.ToArray());

            var pi020 = Assert.Single(result.Diagnostics, d => d.Code == "PI020");
            Assert.Equal(CoreDiagnosticSeverity.Info, pi020.Severity);
            Assert.Equal(1, result.Statistics.Facaded);
            Assert.False(result.HasErrors);
            AssertOutputCompiles(result);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void FacadeMode_IsIdempotent_SecondRunMakesNoChanges()
    {
        var config = WriteConfig("{\"policies\":{\"PlatformType\":\"facade\"}}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Facade, ConfigPath = config };
            var first = Run(options, false, ("Registry.cs", """
                class RegistryWrapper
                {
                    public void Work(Microsoft.Win32.RegistryKey key)
                    {
                        System.Console.WriteLine(key);
                    }
                }
                """));
            AssertOutputCompiles(first);

            var output = SingleOutput(first, "Registry.cs");
            var second = Run(options, false, ("Registry.cs", output));

            Assert.False(second.Files.Single(f => f.FilePath == "Registry.cs").Changed);
            Assert.Empty(second.GeneratedFiles);
            Assert.DoesNotContain(second.Diagnostics, d => d.Code == "PI020");
            Assert.DoesNotContain(second.Diagnostics, d => d.Code == "PI031");
            Assert.False(second.HasErrors);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void FacadeMode_NonFacadeableShape_IsRejectedOrResidual_NeverHalfRewritten()
    {
        // 泛型方法 M3 不抽取：成员保留身份，residual 闸门显式裁决（不静默跳过）。
        var config = WriteConfig("{\"policies\":{\"PlatformType\":\"facade\"}}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Facade, ConfigPath = config };
            var result = Run(options, false, ("Generic.cs", """
                class GenericWrapper
                {
                    public void Work<T>(Microsoft.Win32.RegistryKey key)
                    {
                        System.Console.WriteLine(key);
                    }
                }
                """));

            var output = SingleOutput(result, "Generic.cs");
            Assert.DoesNotContain("Cs2jRequiredPort", output);
            Assert.DoesNotContain("IGenericWrapperPort", output);
            Assert.Contains("Microsoft.Win32.RegistryKey", output);
            Assert.True(result.HasErrors);
            Assert.Contains(result.Diagnostics, d => d.Code == "PI031");
        }
        finally
        {
            File.Delete(config);
        }
    }

    // ── C42 M3 × C06 联合：已归一带标记方法的 3201/3203 抑制 ────────────────

    [Fact]
    public void FacadeMode_NormalizedC06Facade_Suppresses3201_3203_ButNonWhitelistedStaysReject()
    {
        var config = WriteConfig("{\"policies\":{\"PlatformType\":\"facade\"}}");
        try
        {
            var options = new PlatformIsolationOptions { Mode = PlatformIsolationMode.Facade, ConfigPath = config };
            var result = Run(options, false, ("NativeMethods.cs", """
                using System.Runtime.InteropServices;

                namespace Cs2j.Generation
                {
                    [System.AttributeUsage(System.AttributeTargets.Method)]
                    internal sealed class InteropFacadeAttribute : System.Attribute
                    {
                        public InteropFacadeAttribute(string library, string entryPoint) { }
                    }
                }

                internal static class NativeMethods
                {
                    // C06 白名单收编（形态同 ExternInteropRewriter 产物）：无 DllImport/
                    // extern，带 InteropFacade 标记；参数上保留的 [MarshalAs] 仍会触发
                    // 3201——经上卷方法声明的标记判定抑制。
                    [global::Cs2j.Generation.InteropFacade("kernel32", "GetTickCount")]
                    internal static uint GetTickCount()
                        => throw new System.PlatformNotSupportedException("ffm facade stub");

                    [global::Cs2j.Generation.InteropFacade("kernel32", "GetProfileString")]
                    internal static uint GetProfileString([MarshalAs(UnmanagedType.LPWStr)] string name)
                        => throw new System.PlatformNotSupportedException("ffm facade stub");

                    // 非白名单：C06 不动，C42 仍登记为互操作站点并 Reject。
                    [DllImport("kernel32.dll")]
                    internal static extern bool Beep(uint a, uint b);
                }
                """));

            // 清单里互操作站点仅 Beep 的 3201/3203；归一两例零站点。
            Assert.Equal(1, result.Statistics.ByFamily.GetValueOrDefault(BoundaryFamily.NativeAttribute));
            Assert.Equal(1, result.Statistics.ByFamily.GetValueOrDefault(BoundaryFamily.ExternMethod));
            Assert.Equal(0, result.Statistics.Facaded);

            Assert.True(result.HasErrors);
            Assert.Contains(result.Diagnostics, d => d.Code == "PI030" && d.Message.Contains("Beep"));
            Assert.DoesNotContain(
                result.Diagnostics,
                d => (d.Message?.Contains("GetTickCount") ?? false) || (d.Message?.Contains("GetProfileString") ?? false));

            // 已归一两例原文不动，C42 不对互操作族生成任何 Facade 产物。
            var output = SingleOutput(result, "NativeMethods.cs");
            Assert.Contains("[global::Cs2j.Generation.InteropFacade(\"kernel32\", \"GetTickCount\")]", output);
            Assert.DoesNotContain("IGetTickCountPort", output);
            Assert.DoesNotContain(result.GeneratedFiles, g => g.RelativePath.Contains("Port"));
        }
        finally
        {
            File.Delete(config);
        }
    }
}