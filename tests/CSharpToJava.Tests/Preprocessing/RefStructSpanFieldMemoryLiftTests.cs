using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.RefStructSpanFieldMemoryLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// ref-struct-span-field-memory-lift：ref struct 中只读 ReadOnlySpan 字段
/// （以及自动属性后备字段）改造成 ReadOnlyMemory 存储 + 同名 span 属性，
/// 并去掉 `ref` 修饰符；可变 Span/ref/指针/初始化器一律保留并报 RSM001。
/// </summary>
public sealed class RefStructSpanFieldMemoryLiftTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static RefStructSpanFieldMemoryLiftResult Run(string source) =>
        new RefStructSpanFieldMemoryLiftPreprocessor()
            .RewriteFile(source, ParseWithSemantics(source));

    // ── 1. byte 字段 → memory + 属性，输出可编译且运行时 Len 正确 ──

    [Fact]
    public void ReadOnlySpanByteField_IsMemoryLifted_AndRuntimeLengthIsCorrect()
    {
        const string src = """
            using System;
            public ref struct R
            {
                private ReadOnlySpan<byte> _buffer;
                public R(ReadOnlySpan<byte> b) { _buffer = b; }
                public int Len => _buffer.Length;
                public static R Create(byte[] b) => new R(b);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("ref struct", result.OutputCode);
        Assert.Contains("public struct R", result.OutputCode);
        Assert.Contains(
            "global::System.ReadOnlyMemory<byte> __cs2j_buffer_memory;",
            result.OutputCode);
        Assert.Contains(
            "global::System.ReadOnlySpan<byte> _buffer => " +
            "__cs2j_buffer_memory.Span;",
            result.OutputCode);
        Assert.Contains(
            "__cs2j_buffer_memory = __cs2j_ToMemory(b);", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsLowered);
        Assert.Equal(1, result.Statistics.SpanFieldsMemoryLifted);
        Assert.Equal(0, result.Statistics.RefStructsPreserved);

        AssertOutputCompiles(result.OutputCode);
        AssertNoRefStructResidual(result.OutputCode);

        // Runtime: construct through the static factory and read Len.
        var assembly = LoadAssembly(result.OutputCode);
        var type = assembly.GetType("R", throwOnError: true)!;
        var create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
        var instance = create.Invoke(null, new object?[] { new byte[] { 1, 2, 3, 4 } })!;
        var len = type.GetProperty("Len")!.GetValue(instance);

        Assert.Equal(4, len);
    }

    // ── 2. char 变体 ──

    [Fact]
    public void ReadOnlySpanCharField_IsMemoryLifted_AndCompiles()
    {
        const string src = """
            using System;
            ref struct R
            {
                private ReadOnlySpan<char> _chars;
                public R(ReadOnlySpan<char> chars) { _chars = chars; }
                public int Len => _chars.Length;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains(
            "global::System.ReadOnlyMemory<char> __cs2j_chars_memory;",
            result.OutputCode);
        Assert.Contains(
            "global::System.ReadOnlySpan<char> _chars => " +
            "__cs2j_chars_memory.Span;",
            result.OutputCode);
        Assert.Equal(1, result.Statistics.SpanFieldsMemoryLifted);

        AssertOutputCompiles(result.OutputCode);
    }

    // ── 3. 可变 Span<T> 字段 → 保留 ref + RSM001 ──

    [Fact]
    public void WritableSpanField_IsPreserved_WithRSM001()
    {
        const string src = """
            using System;
            ref struct R { private Span<byte> _span; }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ref struct R", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsPreserved);
        Assert.Equal(0, result.Statistics.RefStructsLowered);
        Assert.Equal(0, result.Statistics.SpanFieldsMemoryLifted);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RefStructSpanFieldMemoryLiftDiagnosticCode.RSM001, diagnostic.Code);
        Assert.Equal(RefStructSpanFieldMemoryLiftSeverity.Warning, diagnostic.Severity);
    }

    // ── 4. ref 字段 / 指针字段 → 保留 ──

    [Fact]
    public void RefField_IsPreserved_WithRSM001()
    {
        const string src = """
            ref struct R { private ref int _value; }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ref struct R", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsPreserved);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RefStructSpanFieldMemoryLiftDiagnosticCode.RSM001, diagnostic.Code);
    }

    [Fact]
    public void PointerField_IsPreserved_WithRSM001()
    {
        const string src = """
            unsafe ref struct R { private int* _pointer; private ReadOnlySpan<byte> _buffer; }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ref struct R", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsPreserved);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RefStructSpanFieldMemoryLiftDiagnosticCode.RSM001, diagnostic.Code);
    }

    // ── 5. 字段 initializer → 保留 ──

    [Fact]
    public void FieldWithInitializer_IsPreserved_WithRSM001()
    {
        const string src = """
            using System;
            ref struct R
            {
                private ReadOnlySpan<byte> _buffer = ReadOnlySpan<byte>.Empty;
                public R() { }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ref struct R", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsPreserved);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RefStructSpanFieldMemoryLiftDiagnosticCode.RSM001, diagnostic.Code);
        Assert.Contains("initializer", diagnostic.Message, StringComparison.Ordinal);
    }

    // ── 6. 多个 ReadOnlySpan 字段 → 各自 memory 化，helper 只注入一次 ──

    [Fact]
    public void MultipleReadOnlySpanFields_AreLifted_WithSingleHelper()
    {
        const string src = """
            using System;
            ref struct R
            {
                private ReadOnlySpan<byte> _first;
                private ReadOnlySpan<char> _second;
                public R(ReadOnlySpan<byte> first, ReadOnlySpan<char> second)
                {
                    _first = first;
                    _second = second;
                }
                public int Len => _first.Length + _second.Length;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("__cs2j_first_memory;", result.OutputCode);
        Assert.Contains("__cs2j_second_memory;", result.OutputCode);
        Assert.Contains("__cs2j_first_memory = __cs2j_ToMemory(first);", result.OutputCode);
        Assert.Contains("__cs2j_second_memory = __cs2j_ToMemory(second);", result.OutputCode);
        Assert.Equal(2, result.Statistics.SpanFieldsMemoryLifted);
        Assert.Equal(
            1,
            CountOccurrences(
                result.OutputCode,
                "private static global::System.ReadOnlyMemory<TCs2jValue> " +
                "__cs2j_ToMemory<TCs2jValue>"));

        AssertOutputCompiles(result.OutputCode);
    }

    // ── 7. 幂等 / 注册顺序 / 默认开启 ──

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        const string src = """
            using System;
            ref struct R
            {
                private ReadOnlySpan<byte> _buffer;
                public R(ReadOnlySpan<byte> b) { _buffer = b; }
                public int Len => _buffer.Length;
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.RefStructsLowered);
        Assert.Equal(0, second.Statistics.SpanFieldsMemoryLifted);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void Registry_slots_after_ref_struct_lowering_before_pointer_deref_index_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.RefStructSpanFieldMemoryLift);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.Declarative, descriptor.Stage);
        Assert.Equal(new[] { PassIds.RefStructLowering }, descriptor.RunAfterIds);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.Equal(
            ".cs2j-ref-struct-span-field-memory-lift-src",
            descriptor.IntermediateDirectoryName);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var refStruct = ordered.IndexOf(PassIds.RefStructLowering);
        var lift = ordered.IndexOf(PassIds.RefStructSpanFieldMemoryLift);
        var pointerDeref = ordered.IndexOf(PassIds.PointerDerefIndex);
        var closure = ordered.IndexOf(PassStubCatalog.Ids.ClosureLift);

        Assert.True(refStruct >= 0 && lift >= 0 && pointerDeref >= 0 && closure >= 0);
        Assert.True(refStruct < lift, "span-field-memory-lift must run after ref-struct-lowering");
        Assert.True(lift < pointerDeref,
            "span-field-memory-lift must run before pointer-deref-index");
        Assert.True(lift < closure, "span-field-memory-lift must run before closure-lift");

        var disabled = PassPlanResolver.Resolve(
            registry, disablePassIds: new[] { PassIds.RefStructSpanFieldMemoryLift });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(
            disabled.Enabled, pass => pass.Id == PassIds.RefStructSpanFieldMemoryLift);
    }

    // ── 8. 读取点（属性/索引/Slice）不改写但可编译 ──

    [Fact]
    public void ReadSites_AreNotRewritten_AndCompile()
    {
        const string src = """
            using System;
            ref struct R
            {
                private ReadOnlySpan<byte> _buffer;
                public R(ReadOnlySpan<byte> b) { _buffer = b; }
                public int Len => _buffer.Length;
                public byte First => _buffer[0];
                public ReadOnlySpan<byte> Tail => _buffer.Slice(1);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("_buffer.Length", result.OutputCode);
        Assert.Contains("_buffer[0]", result.OutputCode);
        Assert.Contains("_buffer.Slice(1)", result.OutputCode);

        AssertOutputCompiles(result.OutputCode);
    }

    // ── 9. 自动属性后备字段同样内存化（E2E: Utf8JsonReader.ValueSpan）──

    [Fact]
    public void ReadOnlySpanAutoProperty_IsMemoryLifted_AndCompiles()
    {
        const string src = """
            using System;
            public ref struct R
            {
                private ReadOnlySpan<byte> _buffer;
                public ReadOnlySpan<byte> Value { get; private set; }
                public R(ReadOnlySpan<byte> b) { _buffer = b; Value = b; }
                public int Len => _buffer.Length + Value.Length;
                public static R Create(byte[] b) => new R(b);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("ref struct", result.OutputCode);
        Assert.Contains(
            "global::System.ReadOnlyMemory<byte> __cs2j_Value_memory;",
            result.OutputCode);
        Assert.Contains("get => __cs2j_Value_memory.Span;", result.OutputCode);
        Assert.Contains("private set => __cs2j_Value_memory = __cs2j_ToMemory(value);", result.OutputCode);
        Assert.Equal(2, result.Statistics.SpanFieldsMemoryLifted);

        AssertOutputCompiles(result.OutputCode);

        var assembly = LoadAssembly(result.OutputCode);
        var type = assembly.GetType("R", throwOnError: true)!;
        var create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
        var instance = create.Invoke(null, new object?[] { new byte[] { 1, 2 } })!;
        Assert.Equal(4, type.GetProperty("Len")!.GetValue(instance));
    }

    // ── 9b. helper 保留 span default 语义（Utf8JsonReader.ValueSpan == default）──

    [Fact]
    public void ToMemoryHelper_PreservesDefaultSpanSemantics()
    {
        const string src = """
            using System;
            public ref struct R
            {
                public ReadOnlySpan<byte> Value { get; private set; }
                public R(ReadOnlySpan<byte> value) { Value = value; }
                public bool ValueIsDefault() => Value == default;
                public static R MakeDefault() => new R(default);
                public static R MakeFromBytes(byte[] bytes) => new R(bytes);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("span == default ? default :", result.OutputCode);

        AssertOutputCompiles(result.OutputCode);

        var assembly = LoadAssembly(result.OutputCode);
        var type = assembly.GetType("R", throwOnError: true)!;
        var isDefault = type.GetMethod("ValueIsDefault", BindingFlags.Public | BindingFlags.Instance)!;
        var makeDefault = type.GetMethod("MakeDefault", BindingFlags.Public | BindingFlags.Static)!;
        var makeFromBytes = type.GetMethod("MakeFromBytes", BindingFlags.Public | BindingFlags.Static)!;

        var defaultInstance = makeDefault.Invoke(null, null)!;
        var bytesInstance = makeFromBytes.Invoke(null, new object?[] { new byte[] { 1, 2 } })!;

        Assert.True((bool)isDefault.Invoke(defaultInstance, null)!);
        Assert.False((bool)isDefault.Invoke(bytesInstance, null)!);
    }

    [Fact]
    public void UntypedDefaultAssignment_IsRewrittenWithoutHelper_AndCompiles()
    {
        const string src = """
            using System;
            ref struct R
            {
                private ReadOnlySpan<byte> _buffer;
                public void Reset() { _buffer = default; }
                public int Len => _buffer.Length;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("__cs2j_buffer_memory = default;", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 10. 无 ref struct 的文件字节保真；ref/out 取址引用 → 保留 ──

    [Fact]
    public void FileWithoutRefStruct_IsReturnedByteForByte()
    {
        const string src = "struct S { public int X; }\nclass C { }\n";

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.RefStructsLowered);
        Assert.Equal(0, result.Statistics.RefStructsPreserved);
    }

    [Fact]
    public void FieldReferencedByRefArgument_IsPreserved_WithRSM001()
    {
        const string src = """
            using System;
            ref struct R
            {
                private ReadOnlySpan<byte> _buffer;
                public void Reset(ReadOnlySpan<byte> value) { Assign(ref _buffer, value); }
                private static void Assign(ref ReadOnlySpan<byte> target, ReadOnlySpan<byte> value)
                {
                    target = value;
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ref struct R", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsPreserved);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RefStructSpanFieldMemoryLiftDiagnosticCode.RSM001, diagnostic.Code);
        Assert.Contains("ref", diagnostic.Message, StringComparison.Ordinal);
    }

    // ── Helpers ──

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "ref-struct-span-field-memory-lift-test",
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
            "ref-struct-span-field-memory-lift-output-" + Guid.NewGuid().ToString("N"),
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

    private static Assembly LoadAssembly(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "rsm-runtime-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);
        Assert.True(
            emitResult.Success,
            "runtime output failed to emit:\n" +
            string.Join(
                "\n",
                emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private static void AssertNoRefStructResidual(string output)
    {
        var hits = ResidualRegistry.CreateDefault()
            .Scan(ParseWithSemantics(output), "src/Sample.cs")
            .Where(hit => hit.Code == "NORM-REF-STRUCT")
            .ToList();

        Assert.Empty(hits);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
