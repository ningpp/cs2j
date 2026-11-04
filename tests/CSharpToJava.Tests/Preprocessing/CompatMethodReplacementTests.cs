using System.Buffers;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.CompatMethodReplacement;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// compat-method-replacement：独立 Expression pass，在 fixed-span-to-array 之后、
/// unsafe-ffm 之前，用代码注册的已知方法替换表，把
/// <c>JsonWriterHelper.ToUtf8</c> 的指针算法体替换为托管
/// <c>Encoding.UTF8.GetEncoder().Convert</c> 实现；签名/属性/修饰符保留，
/// 同名重载/不同命名空间保留并报 CMR001，幂等。
/// </summary>
public sealed class CompatMethodReplacementTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static CompatMethodReplacementResult Run(string source) =>
        new CompatMethodReplacementPreprocessor()
            .RewriteFile(source, ParseWithSemantics(source));

    // ── 1. 精确签名匹配 → body 替换，无 pointer/fixed，可编译 ──

    [Fact]
    public void ExactSignature_match_replaces_the_pointer_body_with_the_managed_encoder()
    {
        const string src = """
            using System;
            using System.Buffers;

            namespace System.Text.Json
            {
                internal static class JsonWriterHelper
                {
                    public static unsafe OperationStatus ToUtf8(
                        ReadOnlySpan<byte> utf16Source,
                        Span<byte> utf8Destination,
                        out int bytesConsumed,
                        out int bytesWritten)
                    {
                        bytesConsumed = 0;
                        bytesWritten = 0;
                        fixed (byte* pSrc = utf16Source)
                        fixed (byte* pDst = utf8Destination)
                        {
                            bytesWritten = (int)(pDst - pDst);
                            return OperationStatus.Done;
                        }
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(1, result.Statistics.MethodsReplaced);
        Assert.Equal(0, result.Statistics.SignatureMismatches);
        Assert.Contains("Encoding.UTF8.GetEncoder()", result.OutputCode);
        Assert.Contains("out bool completed", result.OutputCode);
        Assert.Contains("compat-method-replacement", result.OutputCode);
        Assert.DoesNotContain("fixed", result.OutputCode);
        Assert.DoesNotContain("byte*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2. 同名方法但参数不同 / 不同命名空间 → 不替换 + CMR001 ──

    [Fact]
    public void Same_name_overloads_and_other_namespaces_are_preserved_with_CMR001()
    {
        const string src = """
            using System;
            using System.Buffers;

            namespace Other
            {
                internal static class JsonWriterHelper
                {
                    public static OperationStatus ToUtf8(ReadOnlySpan<byte> source, Span<byte> destination)
                    {
                        return OperationStatus.Done;
                    }

                    public static unsafe OperationStatus ToUtf8(
                        ReadOnlySpan<byte> utf16Source,
                        Span<byte> utf8Destination,
                        out int bytesConsumed,
                        out int bytesWritten)
                    {
                        bytesConsumed = 0;
                        bytesWritten = 0;
                        fixed (byte* pSrc = utf16Source)
                        {
                            bytesWritten = 0;
                        }

                        return OperationStatus.Done;
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.MethodsReplaced);
        Assert.Equal(2, result.Statistics.SignatureMismatches);
        Assert.Equal(2, result.Diagnostics.Count(d =>
            d.Code == CompatMethodReplacementDiagnosticCode.CMR001));
        Assert.Contains(result.Diagnostics, d =>
            d.Code == CompatMethodReplacementDiagnosticCode.CMR001
            && d.Message.Contains("namespace", StringComparison.Ordinal));
        Assert.DoesNotContain("Encoding.UTF8.GetEncoder()", result.OutputCode);
    }

    // ── 3. 幂等：第二次无变化 ──

    [Fact]
    public void Rewrite_is_idempotent()
    {
        const string src = """
            using System;
            using System.Buffers;

            namespace System.Text.Json
            {
                internal static class JsonWriterHelper
                {
                    public static unsafe OperationStatus ToUtf8(
                        ReadOnlySpan<byte> utf16Source,
                        Span<byte> utf8Destination,
                        out int bytesConsumed,
                        out int bytesWritten)
                    {
                        bytesConsumed = 0;
                        bytesWritten = 0;
                        fixed (byte* p = utf16Source)
                        {
                            return OperationStatus.Done;
                        }
                    }
                }
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);
        AssertOutputCompiles(first.OutputCode);

        var second = Run(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.MethodsReplaced);
        Assert.Equal(1, second.Statistics.MethodsAlreadyReplaced);
    }

    // ── 4. 运行时等价：ASCII / 中文，与原 Encoding.UTF8 一致 ──

    private const string RuntimeSource = """
        using System;
        using System.Buffers;

        namespace System.Text.Json
        {
            internal static class JsonWriterHelper
            {
                public static unsafe OperationStatus ToUtf8(
                    ReadOnlySpan<byte> utf16Source,
                    Span<byte> utf8Destination,
                    out int bytesConsumed,
                    out int bytesWritten)
                {
                    bytesConsumed = 0;
                    bytesWritten = 0;
                    fixed (byte* pSrc = utf16Source)
                    fixed (byte* pDst = utf8Destination)
                    {
                        return OperationStatus.Done;
                    }
                }

                public static int Run(byte[] utf16Bytes, byte[] destination, out int bytesConsumed, out int bytesWritten)
                    => (int)ToUtf8(utf16Bytes, destination, out bytesConsumed, out bytesWritten);
            }
        }
        """;

    [Fact]
    public void Replacement_produces_the_same_utf8_bytes_as_Encoding_Utf8()
    {
        var method = LoadRuntimeConverter();

        AssertTranscodes(method, "Hello, cs2j!");
        AssertTranscodes(method, "中文编码测试 — UTF-16 → UTF-8");

        // Destination too small → DestinationTooSmall and no overrun.
        var text = "中文编码测试";
        var utf16 = Encoding.Unicode.GetBytes(text);
        var expected = Encoding.UTF8.GetBytes(text);
        var small = new byte[Math.Max(1, expected.Length - 2)];
        var args = new object[] { utf16, small, 0, 0 };
        var status = (int)method.Invoke(null, args)!;
        Assert.Equal((int)OperationStatus.DestinationTooSmall, status);
        Assert.True((int)args[3] <= small.Length);
        Assert.True((int)args[2] < utf16.Length);
    }

    // ── 4b. 未配对 surrogate：返回 InvalidData，只写有效前缀 ──

    [Fact]
    public void Unpaired_surrogates_return_InvalidData_and_write_only_the_valid_prefix()
    {
        var method = LoadRuntimeConverter();

        AssertInvalidSurrogate(method, "prefix-", (char)0xD800, "-tail");
        AssertInvalidSurrogate(method, "prefix-", (char)0xDC00, "-tail");
        AssertInvalidSurrogate(method, "中文", (char)0xD800, "-tail");
    }

    [Fact]
    public void Unpaired_surrogate_with_too_small_destination_reports_DestinationTooSmall_first()
    {
        var method = LoadRuntimeConverter();

        const string prefix = "中文编码";
        var utf16 = Encoding.Unicode.GetBytes(prefix + (char)0xD800 + "tail");
        var expectedPrefix = Encoding.UTF8.GetBytes(prefix);
        var destination = new byte[Math.Max(1, expectedPrefix.Length - 1)];
        var args = new object[] { utf16, destination, 0, 0 };

        var status = (int)method.Invoke(null, args)!;

        Assert.Equal((int)OperationStatus.DestinationTooSmall, status);
        Assert.True((int)args[3] <= destination.Length);
    }

    // ── 5. 注册：fixed-span-to-array 之后、pointer-diff-inline/unsigned/unsafe-ffm 之前，默认开启 ──

    [Fact]
    public void Registry_slots_after_fixed_span_to_array_before_pointer_diff_and_unsigned_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.CompatMethodReplacement);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.Expression, descriptor.Stage);
        Assert.Equal(".cs2j-compat-method-replacement-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(new[] { PassIds.FixedSpanToArray }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var fixedSpan = ordered.IndexOf(PassIds.FixedSpanToArray);
        var compat = ordered.IndexOf(PassIds.CompatMethodReplacement);
        var pointerDiff = ordered.IndexOf(PassIds.PointerDiffInline);
        var unsigned = ordered.IndexOf(PassIds.Unsigned);
        var unsafeFfm = ordered.IndexOf(PassStubCatalog.Ids.UnsafeFfm);

        Assert.True(fixedSpan >= 0 && compat >= 0 && pointerDiff >= 0
            && unsigned >= 0 && unsafeFfm >= 0);
        Assert.True(fixedSpan < compat, "compat-method-replacement must run after fixed-span-to-array");
        Assert.True(compat < pointerDiff, "compat-method-replacement must run before pointer-diff-inline");
        Assert.True(compat < unsigned, "compat-method-replacement must run before unsigned");
        Assert.True(compat < unsafeFfm, "compat-method-replacement must run before unsafe-ffm");

        var disabled = PassPlanResolver.Resolve(
            registry, disablePassIds: new[] { PassIds.CompatMethodReplacement });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.CompatMethodReplacement);
    }

    [Fact]
    public void Default_registry_is_extensible_and_carries_the_json_writer_helper_entry()
    {
        var registry = CompatMethodReplacementRegistry.Default;
        var entry = Assert.Single(registry.Entries);
        Assert.Equal(
            "System.Text.Json.JsonWriterHelper.ToUtf8(" +
            "System.ReadOnlySpan<byte>, System.Span<byte>, out int, out int)",
            entry.Key);
        Assert.Equal("JsonWriterHelper", entry.ContainingTypeName);
        Assert.Equal(4, entry.Parameters.Count);

        var extended = registry.WithAdditionalEntries(entry with { Key = entry.Key + "#copy" });
        Assert.Equal(2, extended.Entries.Count);
    }

    // ── helpers ──

    private static MethodInfo LoadRuntimeConverter()
    {
        var result = Run(RuntimeSource);
        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        return LoadMethod(result.OutputCode, "System.Text.Json.JsonWriterHelper", "Run");
    }

    private static void AssertInvalidSurrogate(
        MethodInfo method,
        string prefix,
        char unpairedSurrogate,
        string suffix)
    {
        var utf16 = Encoding.Unicode.GetBytes(prefix + unpairedSurrogate + suffix);
        var expectedPrefix = Encoding.UTF8.GetBytes(prefix);
        var destination = new byte[expectedPrefix.Length + 16];
        var args = new object[] { utf16, destination, 0, 0 };

        var status = (int)method.Invoke(null, args)!;

        Assert.Equal((int)OperationStatus.InvalidData, status);
        Assert.Equal(prefix.Length * 2, (int)args[2]);
        Assert.Equal(expectedPrefix.Length, (int)args[3]);
        Assert.Equal(expectedPrefix, destination.AsSpan(0, (int)args[3]).ToArray());
    }

    private static void AssertTranscodes(MethodInfo method, string text)
    {
        var utf16 = Encoding.Unicode.GetBytes(text);
        var expected = Encoding.UTF8.GetBytes(text);
        var destination = new byte[expected.Length + 8];
        var args = new object[] { utf16, destination, 0, 0 };

        var status = (int)method.Invoke(null, args)!;

        Assert.Equal((int)OperationStatus.Done, status);
        Assert.Equal(utf16.Length, (int)args[2]);
        Assert.Equal(expected.Length, (int)args[3]);
        Assert.Equal(expected, destination.AsSpan(0, (int)args[3]).ToArray());
    }

    private static MethodInfo LoadMethod(string outputCode, string typeName, string methodName)
    {
        var compilation = CompileOutput(outputCode);
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        return assembly.GetType(typeName)!
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
    }

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "compat-method-replacement-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static Compilation AssertOutputCompiles(string output)
    {
        var compilation = CompileOutput(output);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten output has compile errors:\n" +
            string.Join("\n", errors) + "\n--- output ---\n" + output);
        return compilation;
    }

    private static Compilation CompileOutput(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        return CSharpCompilation.Create(
            "compat-method-replacement-output",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
