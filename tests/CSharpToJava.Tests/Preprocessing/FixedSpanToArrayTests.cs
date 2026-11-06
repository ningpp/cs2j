using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.FixedSpanToArray;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// <c>fixed-span-to-array</c>：把 <c>fixed (T* p = Span/ReadOnlySpan)</c> 与
/// <c>fixed (T* p = &amp;MemoryMarshal.GetReference(span))</c> 物化为数组固定，
/// 让后续 unsafe-ffm R3 的数组 fixed 路径接管指针降级；可写 Span 通过
/// try/finally 写回，无法证明的形态保留并报 FSA001。
/// </summary>
public sealed class FixedSpanToArrayTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static FixedSpanToArrayResult Run(string source) =>
        new FixedSpanToArrayPreprocessor().RewriteFile(source, ParseWithSemantics(source));

    // ── 1. ReadOnlySpan fixed → span.ToArray() + fixed over array ──

    [Fact]
    public void ReadOnlySpan_fixed_is_materialized_to_array_fixed()
    {
        const string src = """
            using System;
            class C
            {
                static unsafe int M(ReadOnlySpan<char> value)
                {
                    fixed (char* p = value)
                    {
                        return p[0];
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(1, result.Statistics.SpanFixedStatementsLowered);
        Assert.Contains("char[] __cs2j_fixed_0 = value.ToArray();", result.OutputCode);
        Assert.Contains("fixed (char* p = __cs2j_fixed_0)", result.OutputCode);
        Assert.DoesNotContain("fixed (char* p = value)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2. writable Span fixed → temp span + try/finally CopyTo write-back ──

    [Fact]
    public void WritableSpan_fixed_uses_try_finally_write_back_and_persists_mutation()
    {
        const string src = """
            using System;
            public static class Api
            {
                public static unsafe void Mutate(Span<int> span)
                {
                    fixed (int* p = span)
                    {
                        p[2] = 42;
                    }
                }
            }
            public static class Use
            {
                public static int Run()
                {
                    int[] data = new int[4];
                    Api.Mutate(data.AsSpan());
                    return data[2];
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(1, result.Statistics.SpanFixedStatementsLowered);
        Assert.Contains("var __cs2j_span_0 = span;", result.OutputCode);
        Assert.Contains("int[] __cs2j_fixed_0 = __cs2j_span_0.ToArray();", result.OutputCode);
        Assert.Contains("try", result.OutputCode);
        Assert.Contains("finally", result.OutputCode);
        Assert.Contains("CopyTo(__cs2j_span_0)", result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));

        Assert.Equal(42, (int)assembly.GetType("Use")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!);
    }

    // ── 3. &MemoryMarshal.GetReference(span) → 提取 span 后数组化 ──

    [Fact]
    public void GetReference_fixed_unwraps_the_span_argument()
    {
        const string src = """
            using System;
            using System.Runtime.InteropServices;
            class C
            {
                static unsafe int M(ReadOnlySpan<byte> value)
                {
                    fixed (byte* p = &MemoryMarshal.GetReference(value))
                    {
                        return p[0];
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(1, result.Statistics.SpanFixedStatementsLowered);
        Assert.Contains("byte[] __cs2j_fixed_0 = value.ToArray();", result.OutputCode);
        Assert.Contains("fixed (byte* p = __cs2j_fixed_0)", result.OutputCode);
        Assert.DoesNotContain("GetReference(value)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 4. array / string / stackalloc fixed 原样保留 ──

    [Fact]
    public void Array_string_and_stackalloc_fixed_sources_are_preserved()
    {
        const string src = """
            using System;
            class C
            {
                static unsafe int M(char[] arr, string str)
                {
                    int sum = 0;
                    fixed (char* p = arr) { sum += p[0]; }
                    fixed (char* q = str) { sum += q[0]; }
                    fixed (int* r = stackalloc int[4]) { r[0] = 1; }
                    return sum;
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.SpanFixedStatementsLowered);
        Assert.Equal(0, result.Statistics.SpanFixedStatementsPreserved);
        Assert.Empty(result.Diagnostics);
    }

    // ── 5. 多变量 / 非指针 / await / yield → FSA001 保留 ──

    [Fact]
    public void Unprovable_span_fixed_shapes_are_preserved_with_FSA001()
    {
        const string src = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            class C
            {
                static unsafe int Multi(ReadOnlySpan<char> value)
                {
                    fixed (char* a = value, char* b = value)
                    {
                        return a[0] + b[0];
                    }
                }

                static unsafe int NonPointer(ReadOnlySpan<char> value)
                {
                    fixed (Span<char> s = value)
                    {
                        return s.Length;
                    }
                }

                static unsafe async Task<int> AsyncBody(ReadOnlySpan<char> value)
                {
                    fixed (char* p = value)
                    {
                        await Task.Yield();
                        return p[0];
                    }
                }

                static unsafe IEnumerable<int> IteratorBody(ReadOnlySpan<char> value)
                {
                    fixed (char* p = value)
                    {
                        yield return p[0];
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.SpanFixedStatementsLowered);
        Assert.Equal(4, result.Statistics.SpanFixedStatementsPreserved);
        Assert.Equal(4, result.Diagnostics.Count(d =>
            d.Code == FixedSpanToArrayDiagnosticCode.FSA001));
        Assert.False(result.HasErrors);
    }

    // ── 6. 幂等 + 注册顺序 + 默认开启 ──

    [Fact]
    public void Rewrite_is_idempotent()
    {
        const string src = """
            using System;
            class C
            {
                static unsafe int M(ReadOnlySpan<char> value)
                {
                    fixed (char* p = value)
                    {
                        return p[0];
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
        Assert.Equal(0, second.Statistics.SpanFixedStatementsLowered);
    }

    [Fact]
    public void Registry_slots_between_using_resource_lift_and_unsigned_before_unsafe_ffm_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.FixedSpanToArray);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.Equal(".cs2j-fixed-span-to-array-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(
            new[] { PassIds.RefOutIn, PassIds.UsingResourceLift },
            descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var usingLift = ordered.IndexOf(PassIds.UsingResourceLift);
        var fixedSpan = ordered.IndexOf(PassIds.FixedSpanToArray);
        var unsigned = ordered.IndexOf(PassIds.Unsigned);
        var unsafeFfm = ordered.IndexOf(PassIds.UnsafeFfm);

        Assert.True(usingLift >= 0 && fixedSpan >= 0 && unsigned >= 0 && unsafeFfm >= 0);
        Assert.True(usingLift < fixedSpan,
            "fixed-span-to-array must run after using-resource-lift");
        Assert.True(fixedSpan < unsigned,
            "fixed-span-to-array must run before unsigned");
        Assert.True(fixedSpan < unsafeFfm,
            "fixed-span-to-array must run before unsafe-ffm");
    }

    // ── Helpers ──

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "fixed-span-to-array-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static Compilation AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "fixed-span-to-array-output",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten output has compile errors:\n" +
            string.Join("\n", errors) + "\n--- output ---\n" + output);
        return compilation;
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
