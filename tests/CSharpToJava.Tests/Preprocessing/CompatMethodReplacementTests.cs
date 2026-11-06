using System.Buffers;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Encodings.Web;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.CompatMethodReplacement;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    // ── 2b. NeedsEscaping：精确签名 → WillEncode 扫描，无 fixed/pointer ──

    [Fact]
    public void NeedsEscaping_exact_signature_replaces_the_fixed_pointer_body_with_the_managed_scan()
    {
        const string src = """
            using System;
            using System.Text.Encodings.Web;

            namespace System.Text.Json
            {
                internal static class JsonWriterHelper
                {
                    public static unsafe int NeedsEscaping(ReadOnlySpan<char> value, JavaScriptEncoder? encoder)
                    {
                        if (value.IsEmpty)
                        {
                            return -1;
                        }

                        fixed (char* ptr = value)
                        {
                            var ___t2 = encoder;
                            return ((!object.ReferenceEquals(___t2, null) ? ___t2 : JavaScriptEncoder.Default))
                                .FindFirstCharacterToEncode(ptr, value.Length);
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
        Assert.Contains("WillEncode", result.OutputCode);
        Assert.Contains("compat-method-replacement", result.OutputCode);
        Assert.DoesNotContain("fixed", result.OutputCode);
        Assert.DoesNotContain("FindFirstCharacterToEncode", result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void NeedsEscaping_parameter_names_are_substituted_from_the_declaration()
    {
        const string src = """
            using System;
            using System.Text.Encodings.Web;

            namespace System.Text.Json
            {
                internal static class JsonWriterHelper
                {
                    public static unsafe int NeedsEscaping(ReadOnlySpan<char> chars, JavaScriptEncoder? enc)
                    {
                        if (chars.IsEmpty)
                        {
                            return -1;
                        }

                        fixed (char* ptr = chars)
                        {
                            var ___t2 = enc;
                            return ((!object.ReferenceEquals(___t2, null) ? ___t2 : JavaScriptEncoder.Default))
                                .FindFirstCharacterToEncode(ptr, chars.Length);
                        }
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.MethodsReplaced);
        Assert.Contains("chars.IsEmpty", result.OutputCode);
        Assert.Contains("object.ReferenceEquals(enc, null)", result.OutputCode);
        Assert.DoesNotContain("value.IsEmpty", result.OutputCode);
        Assert.DoesNotContain("ReferenceEquals(encoder, null)", result.OutputCode);
        Assert.DoesNotContain("fixed", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void NeedsEscaping_signature_mismatch_is_preserved_with_CMR001()
    {
        const string src = """
            using System;
            using System.Text.Encodings.Web;

            namespace System.Text.Json
            {
                internal static class JsonWriterHelper
                {
                    public static int NeedsEscaping(ReadOnlySpan<byte> value, JavaScriptEncoder? encoder)
                    {
                        return -1;
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.MethodsReplaced);
        Assert.Equal(1, result.Statistics.SignatureMismatches);
        var mismatch = Assert.Single(result.Diagnostics);
        Assert.Equal(CompatMethodReplacementDiagnosticCode.CMR001, mismatch.Code);
        Assert.Contains("ReadOnlySpan<char>", mismatch.Message);
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

    [Fact]
    public void NeedsEscaping_rewrite_is_idempotent()
    {
        const string src = """
            using System;
            using System.Text.Encodings.Web;

            namespace System.Text.Json
            {
                internal static class JsonWriterHelper
                {
                    public static unsafe int NeedsEscaping(ReadOnlySpan<char> value, JavaScriptEncoder? encoder)
                    {
                        if (value.IsEmpty)
                        {
                            return -1;
                        }

                        fixed (char* ptr = value)
                        {
                            var ___t2 = encoder;
                            return ((!object.ReferenceEquals(___t2, null) ? ___t2 : JavaScriptEncoder.Default))
                                .FindFirstCharacterToEncode(ptr, value.Length);
                        }
                    }
                }
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);
        Assert.Equal(1, first.Statistics.MethodsReplaced);
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
        var utf16 = GetUtf16Bytes(prefix + (char)0xD800 + "tail");
        var expectedPrefix = Encoding.UTF8.GetBytes(prefix);
        var destination = new byte[Math.Max(1, expectedPrefix.Length - 1)];
        var args = new object[] { utf16, destination, 0, 0 };

        var status = (int)method.Invoke(null, args)!;

        Assert.Equal((int)OperationStatus.DestinationTooSmall, status);
        Assert.True((int)args[3] <= destination.Length);
    }

    // ── 4c. NeedsEscaping：替换体与原 FindFirstCharacterToEncode 逐字符结果一致 ──

    private const string NeedsEscapingRuntimeSource = """
        using System;
        using System.Text.Encodings.Web;

        namespace System.Text.Json
        {
            internal static class JsonWriterHelper
            {
                public static unsafe int NeedsEscaping(ReadOnlySpan<char> value, JavaScriptEncoder? encoder)
                {
                    if (value.IsEmpty)
                    {
                        return -1;
                    }

                    fixed (char* ptr = value)
                    {
                        var ___t2 = encoder;
                        return ((!object.ReferenceEquals(___t2, null) ? ___t2 : JavaScriptEncoder.Default))
                            .FindFirstCharacterToEncode(ptr, value.Length);
                    }
                }

                public static int RunReplacement(string text, JavaScriptEncoder? encoder)
                    => NeedsEscaping(text.AsSpan(), encoder);
            }
        }

        namespace CompatRuntime
        {
            internal static class OriginalNeedsEscaping
            {
                public static unsafe int Run(string text, JavaScriptEncoder? encoder)
                {
                    if (text.Length == 0)
                    {
                        return -1;
                    }

                    fixed (char* ptr = text)
                    {
                        var ___t2 = encoder;
                        return ((!object.ReferenceEquals(___t2, null) ? ___t2 : JavaScriptEncoder.Default))
                            .FindFirstCharacterToEncode(ptr, text.Length);
                    }
                }
            }
        }
        """;

    private static readonly string[] NeedsEscapingTexts =
    {
        "plain ascii",
        "Hello, cs2j!",
        "中文编码测试",
        "中文 mixed ASCII",
        "\u0000abc",
        "abc\u0001def",
        "\t\r\n",
        "\"quoted\\slash\"",
        "<script>&'",
        "中文\u0001tail",
        "a\u007Fb",
        "a\uD801b",
        "a\uDC37b",
        "a\uD801\uDC37b",
        "x\U0001F600y",
        "\uD801\uDC00\uDE6D a",
    };

    [Fact]
    public void NeedsEscaping_replacement_matches_the_original_encoder_scan()
    {
        var result = Run(NeedsEscapingRuntimeSource);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.MethodsReplaced);
        AssertOutputCompiles(result.OutputCode);

        var assembly = LoadAssembly(result.OutputCode);
        var replacement = assembly.GetType("System.Text.Json.JsonWriterHelper")!
            .GetMethod("RunReplacement", BindingFlags.Public | BindingFlags.Static)!;
        var original = assembly.GetType("CompatRuntime.OriginalNeedsEscaping")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        foreach (var encoder in new JavaScriptEncoder?[]
                 {
                     JavaScriptEncoder.Default,
                     JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                     null,
                 })
        {
            foreach (var text in NeedsEscapingTexts)
            {
                var expected = (int)original.Invoke(null, new object?[] { text, encoder })!;
                var actual = (int)replacement.Invoke(null, new object?[] { text, encoder })!;
                Assert.Equal(expected, actual);
            }

            Assert.Equal(
                -1,
                (int)replacement.Invoke(null, new object?[] { string.Empty, encoder })!);
        }
    }

    [Fact]
    public void NeedsEscaping_empty_span_returns_minus_one_and_null_encoder_uses_the_default()
    {
        var result = Run(NeedsEscapingRuntimeSource);
        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);

        var assembly = LoadAssembly(result.OutputCode);
        var replacement = assembly.GetType("System.Text.Json.JsonWriterHelper")!
            .GetMethod("RunReplacement", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal(
            -1,
            (int)replacement.Invoke(null, new object?[] { string.Empty, JavaScriptEncoder.Default })!);
        Assert.Equal(
            -1,
            (int)replacement.Invoke(null, new object?[] { string.Empty, null })!);

        // 无 encoder → JavaScriptEncoder.Default：控制字符/中文需要转义，纯 ASCII 不需要。
        Assert.Equal(0, (int)replacement.Invoke(null, new object?[] { "\u0001abc", null })!);
        Assert.Equal(0, (int)replacement.Invoke(null, new object?[] { "中文abc", null })!);
        Assert.Equal(-1, (int)replacement.Invoke(null, new object?[] { "abc", null })!);
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
        var unsafeFfm = ordered.IndexOf(PassIds.UnsafeFfm);

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
    public void Default_registry_is_extensible_and_carries_the_json_writer_helper_entries()
    {
        var registry = CompatMethodReplacementRegistry.Default;
        Assert.Equal(3, registry.Entries.Count);

        // W-09: the ToUtf8 family carries BOTH the Runtime 10 3-param
        // char-span signature (first, current corpus generation) and the
        // legacy 3.x 4-param byte-span form (family sibling).
        var toUtf8Rt10 = registry.Entries[0];
        Assert.Equal(
            "System.Text.Json.JsonWriterHelper.ToUtf8(" +
            "System.ReadOnlySpan<char>, System.Span<byte>, out int)",
            toUtf8Rt10.Key);
        Assert.Equal("JsonWriterHelper", toUtf8Rt10.ContainingTypeName);
        Assert.Equal(3, toUtf8Rt10.Parameters.Count);
        Assert.Equal("ReadOnlySpan<char>", toUtf8Rt10.Parameters[0].TypeName);
        Assert.Equal(CompatParameterRefKind.Out, toUtf8Rt10.Parameters[2].RefKind);

        var toUtf8Legacy = registry.Entries[1];
        Assert.Equal(
            "System.Text.Json.JsonWriterHelper.ToUtf8(" +
            "System.ReadOnlySpan<byte>, System.Span<byte>, out int, out int)",
            toUtf8Legacy.Key);
        Assert.Equal("JsonWriterHelper", toUtf8Legacy.ContainingTypeName);
        Assert.Equal(4, toUtf8Legacy.Parameters.Count);

        // Family invariants: alternate signatures of the same known method
        // share the family/type keys the drift gate groups by.
        Assert.Equal(toUtf8Rt10.FamilyKey, toUtf8Legacy.FamilyKey);
        Assert.Equal(toUtf8Rt10.DeclaredTypeKey, toUtf8Legacy.DeclaredTypeKey);
        Assert.Equal("System.Text.Json|JsonWriterHelper", toUtf8Rt10.DeclaredTypeKey);

        var needsEscaping = registry.Entries[2];
        Assert.Equal(
            "System.Text.Json.JsonWriterHelper.NeedsEscaping(" +
            "System.ReadOnlySpan<char>, System.Text.Encodings.Web.JavaScriptEncoder)",
            needsEscaping.Key);
        Assert.Equal("JsonWriterHelper", needsEscaping.ContainingTypeName);
        Assert.Equal("NeedsEscaping", needsEscaping.MethodName);
        Assert.Equal("int", needsEscaping.ReturnTypeName);
        Assert.Equal(2, needsEscaping.Parameters.Count);
        Assert.Equal("ReadOnlySpan<char>", needsEscaping.Parameters[0].TypeName);
        Assert.Equal("JavaScriptEncoder", needsEscaping.Parameters[1].TypeName);

        var extended = registry.WithAdditionalEntries(toUtf8Rt10 with { Key = toUtf8Rt10.Key + "#copy" });
        Assert.Equal(4, extended.Entries.Count);
    }

    // ── 6. W-09（升级方案 §6.4-R0）：rt10 3 参 char-span 签名 + 漂移探测 ──

    private const string Rt10RuntimeSource = """
        using System;
        using System.Buffers;

        namespace System.Text.Json
        {
            internal static class JsonWriterHelper
            {
                public static unsafe OperationStatus ToUtf8(
                    ReadOnlySpan<char> source,
                    Span<byte> destination,
                    out int written)
                {
                    written = 0;
                    fixed (char* pSrc = source)
                    {
                        return OperationStatus.Done;
                    }
                }

                public static int Run(string text, byte[] destination, out int written)
                    => (int)ToUtf8(text, destination, out written);
            }
        }
        """;

    [Fact]
    public void Rt10_char_span_signature_is_replaced_with_the_managed_encoder()
    {
        // W-09 红→绿核心样本：Runtime 10 的
        // ToUtf8(ReadOnlySpan<char>, Span<byte>, out int)（JsonWriterHelper.cs:291）
        // 在旧注册表下零匹配静默空转；新条目必须命中并置换。
        var result = Run(Rt10RuntimeSource);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(1, result.Statistics.MethodsReplaced);
        Assert.Equal(0, result.Statistics.SignatureMismatches);
        Assert.Contains("Encoding.UTF8.GetEncoder()", result.OutputCode);
        Assert.Contains("out bool completed", result.OutputCode);
        Assert.Contains("cs2j-compat-method-replacement", result.OutputCode);
        Assert.DoesNotContain("fixed", result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Rt10_replacement_transcodes_and_validates_surrogates()
    {
        var method = LoadMethod(
            Rt10RuntimeSourceRun(), "System.Text.Json.JsonWriterHelper", "Run");

        // ASCII / 中文：与 Encoding.UTF8 逐字节一致。
        AssertRt10Transcodes(method, "Hello, cs2j!");
        AssertRt10Transcodes(method, "中文编码测试 — UTF-16 → UTF-8");

        // 目的缓冲过小 → DestinationTooSmall，不越界。
        var text = "中文编码测试";
        var expected = Encoding.UTF8.GetBytes(text);
        var small = new byte[Math.Max(1, expected.Length - 2)];
        var args = new object?[] { text, small, 0 };
        var status = (int)method.Invoke(null, args)!;
        Assert.Equal((int)OperationStatus.DestinationTooSmall, status);
        Assert.True((int)args[2]! <= small.Length);

        // 未配对代理 → InvalidData，合法前缀照常写出。
        var prefix = "ab";
        var invalidArgs = new object?[] { prefix + "\uD800", new byte[16], 0 };
        Assert.Equal(
            (int)OperationStatus.InvalidData,
            (int)method.Invoke(null, invalidArgs)!);
        Assert.Equal(Encoding.UTF8.GetBytes(prefix).Length, (int)invalidArgs[2]!);
    }

    private static string Rt10RuntimeSourceRun()
    {
        var result = Run(Rt10RuntimeSource);
        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        return result.OutputCode;
    }

    private static void AssertRt10Transcodes(MethodInfo method, string text)
    {
        var expected = Encoding.UTF8.GetBytes(text);
        var destination = new byte[expected.Length + 8];
        var args = new object?[] { text, destination, 0 };

        var status = (int)method.Invoke(null, args)!;

        Assert.Equal((int)OperationStatus.Done, status);
        Assert.Equal(expected.Length, (int)args[2]!);
        Assert.Equal(expected, destination.AsSpan(0, (int)args[2]!).ToArray());
    }

    [Fact]
    public void Drift_detector_flags_families_whose_type_exists_but_nothing_matched()
    {
        // W-09 漂移红测：JsonWriterHelper 存在于语料，但 ToUtf8 是一个表外
        // 签名（2 参）——修复前该文件静默通过（零匹配 + 近失警告），修复后
        // 整树门必须把整个家族标记为漂移（Error 级 CMR020 的输入）。
        const string src = """
            using System;
            using System.Buffers;

            namespace System.Text.Json
            {
                internal static class JsonWriterHelper
                {
                    public static OperationStatus ToUtf8(ReadOnlySpan<char> source, Span<byte> destination)
                        => OperationStatus.Done;
                }
            }
            """;
        var result = Run(src);

        // 文件内视角：身份命中但签名不符 → 近失警告（原有行为）。
        Assert.False(result.Changed);
        Assert.Contains(
            result.Diagnostics, d => d.Code == CompatMethodReplacementDiagnosticCode.CMR001);
        Assert.Contains(
            result.Statistics.DeclaredTypeKeys,
            key => key == "System.Text.Json|JsonWriterHelper");
        Assert.Empty(result.Statistics.MatchedEntryKeys);

        // 整树视角：类型在语料中存在 + 家族零匹配 → 漂移（含 NeedsEscaping
        // 家族——同声明类型存在而方法缺失，按 §6.4-R0 的类型级口径同样计漂移）。
        var drifted = CompatMethodReplacementDriftDetector.DetectDriftedEntries(
            CompatMethodReplacementRegistry.Default,
            result.Statistics.MatchedEntryKeys,
            result.Statistics.DeclaredTypeKeys);
        Assert.Contains(drifted, entry => entry.MethodName == "ToUtf8");
        Assert.Contains(drifted, entry => entry.MethodName == "NeedsEscaping");
    }

    [Fact]
    public void Drift_detector_ignores_families_with_a_matched_sibling_signature()
    {
        // rt10 形态：3 参条目命中后，保留的 3.x 4 参兄弟条目不算漂移。
        var result = Run(Rt10RuntimeSource);

        var drifted = CompatMethodReplacementDriftDetector.DetectDriftedEntries(
            CompatMethodReplacementRegistry.Default,
            result.Statistics.MatchedEntryKeys,
            result.Statistics.DeclaredTypeKeys);
        Assert.DoesNotContain(drifted, entry => entry.MethodName == "ToUtf8");
    }

    [Fact]
    public void Drift_detector_ignores_entries_when_the_declaring_type_is_absent()
    {
        // YamlDotNet 形态：语料完全没有 JsonWriterHelper → 不漂移（沿用
        // CMR001 unused 警告路径），类型键记录的是语料自身类型。
        var result = Run("""
            namespace YamlDotNet.Serialization
            {
                internal static class TypeConverterBuilding
                {
                    public static int M() => 1;
                }
            }
            """);

        var drifted = CompatMethodReplacementDriftDetector.DetectDriftedEntries(
            CompatMethodReplacementRegistry.Default,
            result.Statistics.MatchedEntryKeys,
            result.Statistics.DeclaredTypeKeys);
        Assert.Empty(drifted);
        Assert.Contains(
            result.Statistics.DeclaredTypeKeys,
            key => key == "YamlDotNet.Serialization|TypeConverterBuilding");
    }

    [Fact]
    public void Drift_diagnostic_renders_as_error_CMR020()
    {
        var diagnostic = new CompatMethodReplacementDiagnostic(
            CompatMethodReplacementSeverity.Error,
            CompatMethodReplacementDiagnosticCode.CMR020,
            "entry drifted");

        Assert.StartsWith("error CMR020:", diagnostic.ToMsBuildString(), StringComparison.Ordinal);
    }

    // ── 7. ref-out-in 提升后的 holder 签名归一化 + 模板 .Value 适配 ──

    private static CompatMethodReplacementEntry Rt10ToUtf8Entry
        => CompatMethodReplacementRegistry.Default.Entries[0];

    /// <summary>
    /// 语义模型里的 <c>JsonWriterHelper.ToUtf8</c> 声明：参数文本由调用方给出，
    /// 以便逐项比较 <see cref="CompatMethodReplacementEntry.Matches"/> 对
    /// <c>Cs2jByRef</c> holder 的归一化行为。
    /// </summary>
    private static IMethodSymbol ToUtf8Symbol(string parameters)
    {
        var src = $$"""
            using System;
            using System.Buffers;
            using Cs2jByRef;

            namespace System.Text.Json
            {
                internal static class JsonWriterHelper
                {
                    public static OperationStatus ToUtf8({{parameters}}) => OperationStatus.Done;
                }
            }

            namespace Cs2jByRef
            {
                public sealed class ByRef<T> { }
                public sealed class ReadOnlyByRef<T> { }
                public sealed class ScopedByRef<T> { }
                public sealed class ReadOnlyScopedByRef<T> { }
                public sealed class RefStructByRef<T> { }
            }
            """;
        var model = ParseWithSemantics(src);
        var declaration = model.SyntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ToUtf8");
        return (IMethodSymbol)model.GetDeclaredSymbol(declaration)!;
    }

    private static CompatMethodReplacementEntry WithRefKind(CompatParameterRefKind refKind)
        => Rt10ToUtf8Entry with
        {
            Parameters = new[]
            {
                Rt10ToUtf8Entry.Parameters[0],
                Rt10ToUtf8Entry.Parameters[1],
                new CompatMethodParameter("written", "int", refKind),
            },
        };

    [Fact]
    public void Matches_normalizes_a_writable_byref_holder_to_the_registered_out_parameter()
    {
        var symbol = ToUtf8Symbol(
            "ReadOnlySpan<char> source, Span<byte> destination, ByRef<int> written");

        Assert.True(Rt10ToUtf8Entry.Matches(symbol, out var reason), reason);
        Assert.Null(reason);
    }

    [Fact]
    public void Matches_normalizes_a_writable_byref_holder_to_the_registered_ref_parameter()
    {
        var symbol = ToUtf8Symbol(
            "ReadOnlySpan<char> source, Span<byte> destination, ByRef<int> written");

        Assert.True(
            WithRefKind(CompatParameterRefKind.Ref).Matches(symbol, out var reason),
            reason);
    }

    [Fact]
    public void Matches_normalizes_a_readonly_byref_holder_to_the_registered_in_parameter()
    {
        var symbol = ToUtf8Symbol(
            "ReadOnlySpan<char> source, Span<byte> destination, ReadOnlyByRef<int> written");

        Assert.True(
            WithRefKind(CompatParameterRefKind.In).Matches(symbol, out var reason),
            reason);
    }

    [Fact]
    public void Matches_rejects_a_holder_whose_carried_type_differs()
    {
        var symbol = ToUtf8Symbol(
            "ReadOnlySpan<char> source, Span<byte> destination, ByRef<string> written");

        Assert.False(Rt10ToUtf8Entry.Matches(symbol, out var reason));
        Assert.Contains("string", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_rejects_a_holder_signature_with_a_different_parameter_count()
    {
        var symbol = ToUtf8Symbol("ReadOnlySpan<char> source, Span<byte> destination");

        Assert.False(Rt10ToUtf8Entry.Matches(symbol, out var reason));
        Assert.Contains("2 parameter(s), not 3", reason, StringComparison.Ordinal);
    }

    private const string LiftedRt10Source = """
        using System;
        using System.Buffers;
        using Cs2jByRef;

        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
            }
        }

        namespace System.Text.Json
        {
            internal static class JsonWriterHelper
            {
                public static unsafe OperationStatus ToUtf8(
                    ReadOnlySpan<char> source,
                    Span<byte> destination,
                    ByRef<int> written)
                {
                    written.Value = 0;
                    fixed (char* pSrc = source)
                    {
                        return OperationStatus.Done;
                    }
                }
            }
        }
        """;

    [Fact]
    public void Lifted_out_parameter_holder_matches_and_the_template_writes_through_Value()
    {
        // rt10 实测形态：ref-out-in 先把 `out int written` 提升成
        // `ByRef<int> written`，compat-method-replacement 必须仍能命中，
        // 且模板里的 `written = 0;` 要改写成 `written.Value = 0;`。
        var result = Run(LiftedRt10Source);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(1, result.Statistics.MethodsReplaced);
        Assert.Equal(0, result.Statistics.SignatureMismatches);
        Assert.Contains("written.Value = 0;", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("written.Value = bytesUsed;", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("written.Value.Value", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("fixed", result.OutputCode, StringComparison.Ordinal);
        AssertOutputCompiles(result.OutputCode);

        var drifted = CompatMethodReplacementDriftDetector.DetectDriftedEntries(
            CompatMethodReplacementRegistry.Default,
            result.Statistics.MatchedEntryKeys,
            result.Statistics.DeclaredTypeKeys);
        Assert.DoesNotContain(drifted, entry => entry.MethodName == "ToUtf8");
    }

    [Fact]
    public void Plain_out_parameter_keeps_the_direct_assignment_form()
    {
        var result = Run(Rt10RuntimeSource);

        Assert.Contains("written = 0;", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("written = bytesUsed;", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("written.Value", result.OutputCode, StringComparison.Ordinal);
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
        var utf16 = GetUtf16Bytes(prefix + unpairedSurrogate + suffix);
        var expectedPrefix = Encoding.UTF8.GetBytes(prefix);
        var destination = new byte[expectedPrefix.Length + 16];
        var args = new object[] { utf16, destination, 0, 0 };

        var status = (int)method.Invoke(null, args)!;

        Assert.Equal((int)OperationStatus.InvalidData, status);
        Assert.Equal(prefix.Length * 2, (int)args[2]);
        Assert.Equal(expectedPrefix.Length, (int)args[3]);
        Assert.Equal(expectedPrefix, destination.AsSpan(0, (int)args[3]).ToArray());
    }

    /// <summary>
    /// UTF-16LE bytes without encoder fallback. <c>Encoding.Unicode.GetBytes</c>
    /// replaces an unpaired surrogate with U+FFFD, so it cannot feed the invalid
    /// surrogate cases this class exercises.
    /// </summary>
    private static byte[] GetUtf16Bytes(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i * 2] = (byte)(text[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(text[i] >> 8);
        }

        return bytes;
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
        => LoadAssembly(outputCode).GetType(typeName)!
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;

    private static int _assemblyNameSuffix;

    private static Assembly LoadAssembly(string outputCode)
    {
        // Each emitted assembly gets a unique identity: the default load context
        // rejects a second assembly with the same simple name.
        var compilation = CompileOutput(
            outputCode,
            "compat-method-replacement-output-" +
            System.Threading.Interlocked.Increment(ref _assemblyNameSuffix));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
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

    private static Compilation CompileOutput(
        string output,
        string assemblyName = "compat-method-replacement-output")
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        return CSharpCompilation.Create(
            assemblyName,
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
