using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.UsingResourceLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// <c>using-resource-lift</c>：把 <c>using</c> 资源表达式中带 ref/out 读回的
/// 求值提升为普通局部声明，确保 Java <c>TransformUsingStatement</c> 门禁不再
/// 触发 "ref/out arguments producing read-backs in a using resource expression"。
/// </summary>
public sealed class UsingResourceLiftTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static UsingResourceLiftResult Run(string source) =>
        new UsingResourceLiftPreprocessor().RewriteFile(source, ParseWithSemantics(source));

    // ── 1. 声明形态：using (T x = F(ref r)) → T x = F(ref r); using (x) ──

    [Fact]
    public void Declared_resource_with_ref_argument_lifts_to_local_plus_identifier_using()
    {
        const string src = """
            using System;
            class Resource : IDisposable
            {
                public void Dispose() { }
            }
            static class Factory
            {
                public static Resource Make(ref int r) { return new Resource(); }
            }
            class C
            {
                static void M()
                {
                    int r = 0;
                    using (Resource x = Factory.Make(ref r))
                    {
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("Resource x = Factory.Make(ref r);", result.OutputCode);
        Assert.Contains("using (x)", result.OutputCode);
        Assert.Equal(1, result.Statistics.UsingResourcesLifted);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2. 表达式形态：using (F(ref r)) → var __cs2j_using_0 = F(ref r); using (__cs2j_using_0) ──

    [Fact]
    public void Expression_resource_with_ref_argument_lifts_to_var_temp_with_deterministic_name()
    {
        const string src = """
            using System;
            class Resource : IDisposable
            {
                public void Dispose() { }
            }
            static class Factory
            {
                public static Resource Make(ref int r) { return new Resource(); }
            }
            class C
            {
                static void M()
                {
                    int r = 0;
                    using (Factory.Make(ref r))
                    {
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("var __cs2j_using_0 = Factory.Make(ref r);", result.OutputCode);
        Assert.Contains("using (__cs2j_using_0)", result.OutputCode);
        Assert.Equal(1, result.Statistics.UsingResourcesLifted);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 3. 负向：普通 using 资源不含 ref/out → 原样保留 ──

    [Fact]
    public void Plain_resource_without_read_back_is_left_untouched()
    {
        const string src = """
            using System.IO;
            class C
            {
                static void M()
                {
                    using (var s = new MemoryStream())
                    {
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.UsingResourcesLifted);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    // ── 4. 嵌套 using：外层含 ref 提升，内层不含 ref 原样保留 ──

    [Fact]
    public void Nested_using_lifts_only_the_outer_resource_with_read_back()
    {
        const string src = """
            using System;
            using System.IO;
            class Resource : IDisposable
            {
                public void Dispose() { }
            }
            static class Factory
            {
                public static Resource Make(ref int r) { return new Resource(); }
            }
            class C
            {
                static void M()
                {
                    int r = 0;
                    using (Resource outer = Factory.Make(ref r))
                    {
                        using (var s = new MemoryStream())
                        {
                        }
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("Resource outer = Factory.Make(ref r);", result.OutputCode);
        Assert.Contains("using (outer)", result.OutputCode);
        Assert.Contains("using (var s = new MemoryStream())", result.OutputCode);
        Assert.Equal(1, result.Statistics.UsingResourcesLifted);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 5. await using 跳过 ──

    [Fact]
    public void Await_using_is_preserved_verbatim()
    {
        const string src = """
            using System;
            using System.Threading.Tasks;
            class AsyncResource : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => default;
            }
            static class AsyncFactory
            {
                public static AsyncResource Make(ref int r) => new AsyncResource();
            }
            class C
            {
                static async Task M()
                {
                    int r = 0;
                    await using (AsyncResource x = AsyncFactory.Make(ref r))
                    {
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.UsingResourcesLifted);
        Assert.Empty(result.Diagnostics);
    }

    // ── 6. 幂等：对输出再跑不再变化 ──

    [Fact]
    public void Rewrite_is_idempotent()
    {
        const string src = """
            using System;
            class Resource : IDisposable
            {
                public void Dispose() { }
            }
            static class Factory
            {
                public static Resource Make(ref int r) { return new Resource(); }
            }
            class C
            {
                static void M()
                {
                    int r = 0;
                    using (Resource x = Factory.Make(ref r))
                    {
                    }
                    using (Factory.Make(ref r))
                    {
                    }
                }
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);
        Assert.Equal(2, first.Statistics.UsingResourcesLifted);
        AssertOutputCompiles(first.OutputCode);

        var second = Run(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.UsingResourcesLifted);
    }

    // ── 7. 注册/顺序：unsafe-ref-lowering 之后、unsigned 之前，默认开启 ──

    [Fact]
    public void Registry_slots_after_unsafe_ref_lowering_before_unsigned_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.UsingResourceLift);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.ControlFlow, descriptor.Stage);
        Assert.Equal(new[] { PassIds.RefOutIn }, descriptor.RunAfterIds);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.Equal(".cs2j-using-resource-lift-src", descriptor.IntermediateDirectoryName);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var refOutIn = ordered.IndexOf(PassIds.RefOutIn);
        var unsafeRefLowering = ordered.IndexOf(PassIds.UnsafeRefLowering);
        var usingLift = ordered.IndexOf(PassIds.UsingResourceLift);
        var unsigned = ordered.IndexOf(PassIds.Unsigned);

        Assert.True(refOutIn >= 0 && unsafeRefLowering >= 0 && usingLift >= 0 && unsigned >= 0);
        Assert.True(refOutIn < usingLift, "using-resource-lift must run after ref-out-in");
        Assert.True(unsafeRefLowering < usingLift,
            "using-resource-lift must run after unsafe-ref-lowering");
        Assert.True(usingLift < unsigned, "using-resource-lift must run before unsigned");
        Assert.Contains(PassIds.UsingResourceLift, ordered);
    }

    // ── Helpers ──

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "using-resource-lift-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "using-resource-lift-output",
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
