using System.IO;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C06 unsafe-ffm 预处理器 M0 / R1 单元测试（设计 §7.1）：
/// unsafe 块（含嵌套）剥除、class/struct/interface/record/method/field/
/// constructor/local-function 等声明 unsafe 修饰符剥除、统计计数与幂等。
/// M0 不激活全量 residual 闸门：pointer 等后续里程碑构造不在 R1 拒绝面。
/// </summary>
public class UnsafeFfmRewriterUnitTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static UnsafeFfmResult Rewrite(string src, UnsafeFfmOptions? options = null)
    {
        var model = ParseWithSemantics(src);
        return new UnsafeFfmPreprocessor(options ?? new UnsafeFfmOptions())
            .RewriteFile(src, model);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create("uffm-verify",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private const string BlockSource = """
        class C
        {
            static void M()
            {
                unsafe
                {
                    int x = 42;
                    System.Console.WriteLine(x);
                }
            }
        }
        """;

    [Fact]
    public void UnsafeBlock_IsStripped_InnerStatementsKept()
    {
        var result = Rewrite(BlockSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        Assert.Contains("int x = 42;", result.OutputCode);
        Assert.Equal(1, result.Statistics.UnsafeBlocksStripped);
        Assert.Equal(0, result.Statistics.UnsafeModifiersStripped);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.False(result.HasErrors);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void NestedUnsafeBlocks_AllStripped_NestedBlocksRemain()
    {
        var src = """
            class C
            {
                static void M()
                {
                    unsafe
                    {
                        int a = 1;
                        unsafe
                        {
                            int b = 2;
                            System.Console.Write(a + b);
                        }
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.UnsafeBlocksStripped);
        Assert.Equal(0, result.Statistics.UnsafeModifiersStripped);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        // 两层 Block 结构仍在（语句逐条保留）。
        Assert.Contains("int a = 1;", result.OutputCode);
        Assert.Contains("int b = 2;", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeBlock_ContainingUnsafeLocalFunction_BothStripped()
    {
        var src = """
            class C
            {
                static void M()
                {
                    unsafe
                    {
                        unsafe int Local() => 1;
                        System.Console.Write(Local());
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeBlocksStripped);
        Assert.Equal(1, result.Statistics.UnsafeLocalFunctionModifiersStripped);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeClass_ModifierStripped()
    {
        var src = "unsafe class C { static void M() { System.Console.Write(1); } }";

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeTypeModifiersStripped);
        Assert.Contains("class C", result.OutputCode);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeStructAndInterface_ModifiersStripped()
    {
        var src = """
            unsafe struct S { public int X; }
            unsafe interface I { void M(); }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.UnsafeTypeModifiersStripped);
        Assert.Contains("struct S", result.OutputCode);
        Assert.Contains("interface I", result.OutputCode);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeRecord_ModifierStripped()
    {
        var src = "unsafe record R(int X);";

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeTypeModifiersStripped);
        Assert.Contains("record R", result.OutputCode);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeMethod_ModifierStripped()
    {
        var src = """
            class C
            {
                unsafe void M()
                {
                    int x = 1;
                    System.Console.Write(x);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeMethodModifiersStripped);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeField_ModifierStripped()
    {
        var src = """
            class C
            {
                unsafe int x = 1;
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeFieldModifiersStripped);
        Assert.Contains("int x = 1;", result.OutputCode);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeEventField_ModifierStripped()
    {
        var src = "class C { unsafe event System.Action E; }";

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeFieldModifiersStripped);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeConstructor_ModifierStripped()
    {
        var src = """
            class C
            {
                unsafe C()
                {
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeConstructorModifiersStripped);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafeLocalFunction_ModifierStripped()
    {
        var src = """
            class C
            {
                static void M()
                {
                    unsafe int Local() => 1;
                    System.Console.Write(Local());
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeLocalFunctionModifiersStripped);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsafePropertyIndexerEventFinalizerOperator_ModifiersStripped()
    {
        var src = """
            class C
            {
                unsafe int P { get; set; }
                unsafe int this[int i] => i;
                unsafe event System.Action E { add { } remove { } }
                unsafe ~C() { }
                unsafe public static C operator -(C a) => a;
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(5, result.Statistics.UnsafeOtherModifiersStripped);
        Assert.Equal(0, result.Statistics.UnsafeTypeModifiersStripped);
        Assert.DoesNotContain("unsafe", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    private const string CombinedSource = """
        unsafe class A
        {
            unsafe int field;

            unsafe A() { }

            unsafe void M()
            {
                unsafe
                {
                    int x = 1;
                    System.Console.Write(x);
                }
            }
        }

        unsafe struct S { }
        unsafe interface I { }
        unsafe record R(int X);

        class B
        {
            unsafe void Local()
            {
                unsafe int Lf() => 1;
                System.Console.Write(Lf());
            }
        }
        """;

    [Fact]
    public void Combined_AllCountersRecorded()
    {
        var result = Rewrite(CombinedSource);

        Assert.True(result.Changed);
        var s = result.Statistics;
        Assert.Equal(1, s.UnsafeBlocksStripped);
        Assert.Equal(4, s.UnsafeTypeModifiersStripped);
        Assert.Equal(2, s.UnsafeMethodModifiersStripped);
        Assert.Equal(1, s.UnsafeFieldModifiersStripped);
        Assert.Equal(1, s.UnsafeConstructorModifiersStripped);
        Assert.Equal(1, s.UnsafeLocalFunctionModifiersStripped);
        Assert.Equal(9, s.UnsafeModifiersStripped);
        Assert.Equal(1, s.FilesTransformed);
        Assert.False(result.HasErrors);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Rewrite_IsIdempotent_SecondPassMakesNoChangesOrDiagnostics()
    {
        var first = Rewrite(CombinedSource);
        Assert.True(first.Changed);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(0, second.Statistics.UnsafeBlocksStripped);
        Assert.Equal(0, second.Statistics.UnsafeModifiersStripped);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
        AssertOutputCompiles(second.OutputCode);
    }

    [Fact]
    public void FileWithoutUnsafe_IsUnchanged()
    {
        var src = "class C { static void M() { System.Console.Write(1); } }";

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void PointerConstructs_Remain_M0DoesNotActivateFullResidualGate()
    {
        var src = """
            class C
            {
                unsafe void M(int* p)
                {
                    int x = *p;
                    System.Console.Write(x);
                }
            }
            """;

        // M1 起 EnableFixed 默认开启会接管 fixed 构造，M3a 起 EnablePointer
        // 默认开启会归一指针形参；本用例意图验证 M0/R1 面不处理普通指针形参，
        // 显式关 EnableFixed/EnablePointer 保持 M0 语义。
        var result = Rewrite(src, new UnsafeFfmOptions
        {
            EnableFixed = false,
            EnablePointer = false,
        });

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeMethodModifiersStripped);
        Assert.Contains("int* p", result.OutputCode);
        Assert.Contains("*p", result.OutputCode);
        Assert.False(result.HasErrors);
        // 注意：剥除 unsafe 上下文后该文件已不能独立编译——这正是后续里程碑
        // residual 闸门（UF020 等）要接管的构造；M0 不做拒绝也不做转换。
        Assert.DoesNotContain("unsafe", result.OutputCode);
    }

    // ==================================================================
    // M1 / R2：fixed 字段缓冲区 → 数组字段
    // ==================================================================

    [Fact]
    public void FixedBufferFields_ByteCharIntLong_NormalizedToArrays()
    {
        var src = """
            unsafe struct Frame
            {
                fixed byte a[16];
                fixed char b[8];
                fixed int c[4], d[2];
                fixed long e[1];
                public int Id;
                public byte First(int i) => a[i];
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte[] a = new byte[16];", result.OutputCode);
        Assert.Contains("char[] b = new char[8];", result.OutputCode);
        // 同一字段声明多变量保持共享声明形态。
        Assert.Contains("int[] c = new int[4], d = new int[2];", result.OutputCode);
        Assert.Contains("long[] e = new long[1];", result.OutputCode);
        Assert.DoesNotContain("fixed", result.OutputCode);
        // 数组建于 struct 中 → 补显式无参 ctor 规避 CS8983。
        Assert.Contains("public Frame()", result.OutputCode);
        // 原有索引用法保持不变且语义随数组成立。
        Assert.Contains("=> a[i];", result.OutputCode);
        Assert.Equal(5, result.Statistics.FixedBufferFieldsRewritten);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedBufferField_InClass_NoCtorSynthesized()
    {
        var src = """
            unsafe class Buffer
            {
                fixed byte data[32];
                public byte Get(int i) => data[i];
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte[] data = new byte[32];", result.OutputCode);
        Assert.DoesNotContain("public Buffer(", result.OutputCode);
        Assert.Equal(1, result.Statistics.FixedBufferFieldsRewritten);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StructLayoutWithExtern_Rejected_UF031_OriginalPreserved()
    {
        var src = """
            using System.Runtime.InteropServices;
            [StructLayout(LayoutKind.Sequential)]
            unsafe struct Frame
            {
                fixed byte buf[64];
                [DllImport("x")] public static extern int Beep(int a);
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF031);
        Assert.Equal(1, result.Statistics.Uf031Rejections);
        // 原文回滚：fixed 字段仍在。
        Assert.Contains("fixed byte buf[64];", result.OutputCode);
    }

    [Fact]
    public void SequentialLayoutWithoutExtern_IsRewrittenNormally()
    {
        var src = """
            using System.Runtime.InteropServices;
            [StructLayout(LayoutKind.Sequential)]
            unsafe struct Frame
            {
                fixed byte buf[64];
                public int X;
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte[] buf = new byte[64];", result.OutputCode);
        Assert.Equal(0, result.Statistics.Uf031Rejections);
        AssertOutputCompiles(result.OutputCode);
    }

    // ==================================================================
    // M1 / R3：fixed 语句五类初始式归一
    // ==================================================================

    [Fact]
    public void FixedStatement_Array_DereferenceAndIndexNormalized()
    {
        var src = """
            unsafe class C
            {
                static void M(int[] arr)
                {
                    fixed (int* p = arr)
                    {
                        int x = *p;
                        int y = p[2];
                        p++;
                        System.Console.Write(x + y);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] _p_array = arr;", result.OutputCode);
        Assert.Contains("int p = 0;", result.OutputCode);
        Assert.Contains("int x = _p_array[p];", result.OutputCode);
        Assert.Contains("p++;", result.OutputCode);
        Assert.DoesNotContain("fixed", result.OutputCode);
        Assert.Equal(1, result.Statistics.FixedStatementsCount);
        Assert.Equal(1, result.Statistics.FixedVariablesArray);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedStatement_MultipleVariables_AliasAndCompare()
    {
        var src = """
            unsafe class C
            {
                static void M(int[] arr)
                {
                    fixed (int* p = arr, q = arr)
                    {
                        int* r = p + 1;
                        bool b = p < q || p == q || p != q;
                        long d = r - p;
                        *r = 5;
                        System.Console.Write(b);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("_p_array", result.OutputCode);
        Assert.Contains("_q_array", result.OutputCode);
        Assert.Contains("int r = p + 1;", result.OutputCode);
        // r 别名 p+1，与 p 同数组；写 r 即写 _p_array。
        Assert.Contains("_p_array[r] = 5;", result.OutputCode);
        // 比较/差值原文即 int 运算，保持不动。
        Assert.Contains("p < q", result.OutputCode);
        Assert.Equal(2, result.Statistics.FixedVariablesArray);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedStatement_ArrayOffset_IndexStartsAtOffset()
    {
        var src = """
            unsafe class C
            {
                static void M(int[] arr)
                {
                    fixed (int* p = &arr[5])
                    {
                        int x = *p;
                        int y = p[1];
                        System.Console.Write(x + y);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int p = 5;", result.OutputCode);
        Assert.Contains("int x = _p_array[p];", result.OutputCode);
        Assert.Equal(1, result.Statistics.FixedVariablesOffset);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedStatement_Null_ArrayLocalSuppressed()
    {
        var src = """
            unsafe class C
            {
                static void M(int[] arr)
                {
                    fixed (int* p = null)
                    {
                        System.Console.Write(p);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] _p_array = null!;", result.OutputCode);
        Assert.Equal(1, result.Statistics.FixedVariablesNull);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedStatement_String_ReadNormalizedToCharArray()
    {
        var src = """
            unsafe class C
            {
                static void M(string s)
                {
                    fixed (char* p = s)
                    {
                        char c = *p;
                        char c2 = p[1];
                        System.Console.Write(c);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("s.ToCharArray()", result.OutputCode);
        Assert.Contains("char c = _p_array[p];", result.OutputCode);
        Assert.Equal(1, result.Statistics.FixedVariablesString);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedStatement_StringWrite_Rejected_UF030()
    {
        var src = """
            unsafe class C
            {
                static void M(string s)
                {
                    fixed (char* p = s)
                    {
                        *p = 'x';
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF030);
        Assert.Equal(1, result.Statistics.Uf030Rejections);
        Assert.Equal(1, result.Statistics.FixedVariablesString);
    }

    [Fact]
    public void FixedStatement_ScalarSlot_WritebackAtBlockEnd()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    int x = 41;
                    fixed (int* p = &x)
                    {
                        *p += 1;
                    }
                    System.Console.Write(x);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] _p_slot = new int[] { x };", result.OutputCode);
        Assert.Contains("_p_slot[p] += 1;", result.OutputCode);
        Assert.Contains("x = _p_slot[0];", result.OutputCode);
        Assert.Equal(1, result.Statistics.FixedVariablesScalar);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedStatement_Nested_BothLowered_InnerSeesOuterArray()
    {
        var src = """
            unsafe class C
            {
                static void M(int[] a, int[] b)
                {
                    fixed (int* p = a)
                    {
                        fixed (int* q = b)
                        {
                            *q = *p;
                        }
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("_q_array[q] = _p_array[p];", result.OutputCode);
        Assert.Equal(2, result.Statistics.FixedStatementsCount);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedStatement_LambdaCapture_Rejected_UF020()
    {
        var src = """
            unsafe class C
            {
                static void M(int[] arr)
                {
                    fixed (int* p = arr)
                    {
                        System.Action a = () => System.Console.Write(*p);
                        a();
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
        Assert.Equal(1, result.Statistics.Uf020Rejections);
    }

    [Fact]
    public void FixedStatement_OutputIsIdempotent()
    {
        var src = """
            unsafe class C
            {
                static void M(int[] arr)
                {
                    fixed (int* p = &arr[2])
                    {
                        int x = p[1];
                        System.Console.Write(x);
                    }
                }
            }
            """;

        var first = Rewrite(src);
        Assert.True(first.Changed);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(0, second.Statistics.FixedStatementsCount);
        Assert.Equal(0, second.Statistics.FixedVariablesTotal);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void EnableFixedFalse_FixedConstructsUntouched_OnlyR1Runs()
    {
        var src = """
            unsafe struct Frame
            {
                fixed byte buf[8];
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("fixed byte buf[8];", result.OutputCode);
        Assert.Equal(0, result.Statistics.FixedBufferFieldsRewritten);
        Assert.Equal(0, result.Statistics.FixedStatementsCount);
    }

    // ==================================================================
    // M2 / R4：stackalloc 统一堆化
    // ==================================================================

    [Fact]
    public void StackAlloc_ExplicitNoInitializer_HeapArrayLocal()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    Span<int> x = stackalloc int[8];
                    System.Console.Write(x[0]);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] x = new int[8];", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.Equal(1, result.Statistics.ExplicitStackAllocCollected);
        Assert.Equal(0, result.Statistics.ImplicitStackAllocCollected);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_ExplicitWithInitializer_HeapArrayKeepsElements()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    Span<int> x = stackalloc int[] { 1, 2, 3 };
                    System.Console.Write(x[2]);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] x = new int[]", result.OutputCode);
        Assert.Contains("1, 2, 3", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.Equal(1, result.Statistics.ExplicitStackAllocCollected);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_ReadOnlySpanTarget_ArrayLocal()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    ReadOnlySpan<byte> x = stackalloc byte[3];
                    System.Console.Write(x[1]);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte[] x = new byte[3];", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_ImplicitElementForm_HeapArrayWithInferredType()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    Span<int> x = stackalloc[] { 1, 2 };
                    System.Console.Write(x[1]);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] x = new int[]", result.OutputCode);
        Assert.Contains("1, 2", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.Equal(1, result.Statistics.ImplicitStackAllocCollected);
        Assert.Equal(0, result.Statistics.ExplicitStackAllocCollected);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_DimensionOmittedInExplicitForm_LengthInferred()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    Span<long> x = stackalloc long[] { 4L, 5L, 6L };
                    System.Console.Write(x.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("long[] x = new long[]", result.OutputCode);
        Assert.Contains("4L, 5L, 6L", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_InConditional_BranchesReplaced_ConvertsToSpan()
    {
        var src = """
            using System;
            unsafe class C
            {
                static void M(bool flag)
                {
                    Span<int> x = flag ? stackalloc int[1] : stackalloc int[2];
                    Console.Write(x.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("flag ? new int[1] : new int[2]", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.Equal(2, result.Statistics.ExplicitStackAllocCollected);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_AsArgument_ReplacedByHeapArray()
    {
        var src = """
            using System;
            unsafe class C
            {
                static void Take(Span<int> s) => Console.Write(s.Length);

                static void M()
                {
                    Take(stackalloc int[2]);
                    Take(stackalloc[] { 1, 2 });
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("Take(new int[2]);", result.OutputCode);
        Assert.Contains("new int[]", result.OutputCode);
        Assert.Equal(1, result.Statistics.ExplicitStackAllocCollected);
        Assert.Equal(1, result.Statistics.ImplicitStackAllocCollected);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_AssignmentToSpan_HeapArrayAssignment()
    {
        var src = """
            using System;
            unsafe class C
            {
                static void M()
                {
                    Span<int> x;
                    x = stackalloc int[2];
                    Console.Write(x[0]);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("x = new int[2];", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_ConstantLength_LoweredToHeapArrayAndIndex()
    {
        var src = """
            unsafe class C
            {
                static int M()
                {
                    int* p = stackalloc int[8];
                    p[0] = 11;
                    p[3] = p[0] + 1;
                    return p[3];
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
        Assert.Equal(1, result.Statistics.ExplicitStackAllocCollected);
        Assert.Contains("int[] _p_array = new int[8];", result.OutputCode);
        Assert.Contains("int p = 0;", result.OutputCode);
        // 常量下标语义保持为「索引局部 + 常量」（R6 既有元素访问形态）。
        Assert.Contains("_p_array[(p) + (0)] = 11;", result.OutputCode);
        Assert.Contains("_p_array[(p) + (3)] = _p_array[(p) + (0)] + 1;", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_VarPointerTarget_LoweredToHeapArrayAndIndex()
    {
        var src = """
            unsafe class C
            {
                static int M()
                {
                    var x = stackalloc int[4];
                    x[2] = 9;
                    return x[2];
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
        Assert.Contains("int[] _x_array = new int[4];", result.OutputCode);
        Assert.Contains("int x = 0;", result.OutputCode);
        Assert.Contains("_x_array[(x) + (2)] = 9;", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_NonConstantLength_LoweredPreservingOverflowBehavior()
    {
        var src = """
            unsafe class C
            {
                static int M(int n)
                {
                    byte* p = stackalloc byte[n];
                    p[0] = 7;
                    return p[0];
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
        // 负长度：new byte[n] 与 stackalloc byte[n] 同样抛 OverflowException；
        // 同名索引局部一律为 int。
        Assert.Contains("byte[] _p_array = new byte[n];", result.OutputCode);
        Assert.Contains("int p = 0;", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_DereferenceReadWrite_Lowered()
    {
        var src = """
            unsafe class C
            {
                static int M()
                {
                    int* p = stackalloc int[2];
                    *p = 4;
                    return *p;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("_p_array[p] = 4;", result.OutputCode);
        Assert.Contains("return _p_array[p];", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_PointerIncrement_LoweredToIndexIncrement()
    {
        var src = """
            unsafe class C
            {
                static int M()
                {
                    int* p = stackalloc int[4];
                    p[0] = 1;
                    p[1] = 2;
                    p++;
                    int v = *p;
                    p += 2;
                    return v + p[0];
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] _p_array = new int[4];", result.OutputCode);
        Assert.Contains("p++", result.OutputCode);
        Assert.Contains("p += 2", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_UnsignedElement_Lowered()
    {
        var src = """
            unsafe class C
            {
                static uint M()
                {
                    uint* p = stackalloc uint[4];
                    p[0] = 0xFFFFFFFFu;
                    p[1]++;
                    return p[0] + p[1];
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("uint[] _p_array = new uint[4];", result.OutputCode);
        Assert.Contains("int p = 0;", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_PairedSourceCallee_ExpandedToArrayAndZeroIndex()
    {
        var src = """
            unsafe class C
            {
                static int M(string s)
                {
                    byte* numbers = stackalloc byte[4];
                    numbers[0] = 0;
                    return Parse(s, numbers, 0, 1);
                }

                private static unsafe int Parse(string name, byte* numbers, int start, int end)
                {
                    fixed (char* ipString = name)
                    {
                        return numbers[start] + ipString[end - start] + (int)*numbers;
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
        // 调用点实参展开为「伴生数组 + 0 起点索引局部」。
        Assert.Contains("Parse(s, _numbers_array, numbers, 0, 1)", result.OutputCode);
        // 被调签名按 paired-index 改写。
        Assert.Contains("byte[] numbers, int numbersIndex", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_NestedFixedOffsetFrame_Lowered()
    {
        var src = """
            unsafe class C
            {
                static int M()
                {
                    int* p = stackalloc int[8];
                    for (int i = 0; i < 8; i++) p[i] = i;
                    fixed (int* q = &p[2])
                    {
                        q[0] = 42;
                        return q[1];
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_Returned_StillRejected_UF040()
    {
        var src = """
            unsafe class C
            {
                static int* M()
                {
                    int* p = stackalloc int[4];
                    return p;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF040);
        Assert.Equal(1, result.Statistics.Uf040Rejections);
        Assert.Contains("int* p = stackalloc int[4];", result.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_LambdaCapture_StillRejected_UF040()
    {
        var src = """
            using System;
            unsafe class C
            {
                static int M()
                {
                    int* p = stackalloc int[4];
                    Func<int> f = () => p[0];
                    return f();
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF040);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_RefArgument_StillRejected_UF040()
    {
        var src = """
            unsafe class C
            {
                static void Take(ref int* q) { q[0] = 1; }
                static int M()
                {
                    int* p = stackalloc int[4];
                    Take(ref p);
                    return p[0];
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF040);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_NonWhitelistPointerCallee_StillRejected_UF040()
    {
        var src = """
            unsafe struct Box { public int A; public int B; }
            unsafe class C
            {
                static void Consume(Box* b) { b->A = 1; }
                static int M()
                {
                    Box* p = stackalloc Box[2];
                    Consume(p);
                    return p[0].A;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF040);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_LoweringIsIdempotent()
    {
        var src = """
            unsafe class C
            {
                static int M()
                {
                    int* p = stackalloc int[4];
                    p[0] = 5;
                    return p[0];
                }
            }
            """;

        var first = Rewrite(src);
        Assert.True(first.Changed);
        Assert.False(first.HasErrors);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.False(second.HasErrors);
        Assert.Empty(second.Diagnostics);
        Assert.DoesNotContain("stackalloc", second.OutputCode);
        AssertOutputCompiles(second.OutputCode);
    }

    [Fact]
    public void StackAlloc_BarePointerTarget_MetadataEncodingGetBytes_RewrittenToArrayOverload()
    {
        var src = """
            using System.Text;
            unsafe class C
            {
                static int M(string input, int i, int count)
                {
                    byte* bytes = stackalloc byte[160];
                    fixed (char* pStr = input)
                    {
                        return Encoding.UTF8.GetBytes(pStr + i, count, bytes, 160);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
        Assert.Equal(0, result.Statistics.Uf020Rejections);
        // 指针重载 → 托管数组重载：两个指针实参各展开为数组+索引；bytes 索引
        // 全程恒 0，当运行库只提供 5 参形态时容量实参按零不变后备消去
        // （bytes.Length-0 恰为原容量 160，语义一致）。两种形态均接受。
        Assert.True(
            result.OutputCode.Contains(
                "Encoding.UTF8.GetBytes(_pStr_array, pStr + i, count, _bytes_array, bytes, 160)")
            || result.OutputCode.Contains(
                "Encoding.UTF8.GetBytes(_pStr_array, pStr + i, count, _bytes_array, bytes)"),
            result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_GenericElement_Rejected_UF040()
    {
        var src = """
            unsafe class C
            {
                static void G<T>() where T : unmanaged
                {
                    Span<T> x = stackalloc T[4];
                    System.Console.Write(x.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF040);
        Assert.Equal(1, result.Statistics.Uf040Rejections);
    }

    [Fact]
    public void StackAlloc_NonBlittableStruct_Rejected_UF040()
    {
        var src = """
            unsafe class C
            {
                struct Flag { public bool B; }

                static void M()
                {
                    Span<Flag> x = stackalloc Flag[2];
                    System.Console.Write(x.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF040);
        Assert.Equal(1, result.Statistics.Uf040Rejections);
    }

    [Fact]
    public void StackAlloc_BlittableStruct_RewrittenNormally()
    {
        var src = """
            unsafe class C
            {
                struct Point { public int X; public int Y; }

                static void M()
                {
                    Span<Point> x = stackalloc Point[2];
                    System.Console.Write(x.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("Point[] x = new Point[2];", result.OutputCode);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StackAlloc_OutputIsIdempotent_SecondPassCleanNoDiagnostics()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    Span<int> x = stackalloc int[8];
                    Span<int> y = stackalloc[] { 1, 2 };
                    System.Console.Write(x.Length + y.Length);
                }
            }
            """;

        var first = Rewrite(src);
        Assert.True(first.Changed);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(0, second.Statistics.ExplicitStackAllocCollected);
        Assert.Equal(0, second.Statistics.ImplicitStackAllocCollected);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void StackAlloc_MixedCounters_AllRecorded()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    Span<int> a = stackalloc int[2];
                    Span<int> b = stackalloc[] { 1 };
                    Span<byte> c = stackalloc byte[] { 2, 3 };
                    System.Console.Write(a.Length + b.Length + c.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.ExplicitStackAllocCollected);
        Assert.Equal(1, result.Statistics.ImplicitStackAllocCollected);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void EnableStackAllocFalse_StackAllocUntouched_OnlyEarlierRulesRun()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    Span<int> x = stackalloc int[8];
                    System.Console.Write(x.Length);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableStackAlloc = false });

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("Span<int> x = stackalloc int[8];", result.OutputCode);
        Assert.Equal(0, result.Statistics.ExplicitStackAllocCollected);
        Assert.Equal(0, result.Statistics.ImplicitStackAllocCollected);
        Assert.Equal(0, result.Statistics.Uf040Rejections);
    }

    // ==================================================================
    // M3a / R5：指针签名白名单 → 数组
    // ==================================================================

    [Theory]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("char")]
    [InlineData("float")]
    [InlineData("double")]
    [InlineData("bool")]
    public void M3a_PointerParameter_WhitelistElement_NormalizedToArray(string element)
    {
        var src = $$"""
            unsafe class C
            {
                static void M({{element}}* p)
                {
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains($"{element}[] p", result.OutputCode);
        Assert.DoesNotContain($"{element}*", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerParametersRewritten);
        Assert.Equal(1, result.Statistics.PointerSignaturesRewritten);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_MultiplePointerParameters_AllNormalized()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* a, int* b, char* c)
                {
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte[] a", result.OutputCode);
        Assert.Contains("int[] b", result.OutputCode);
        Assert.Contains("char[] c", result.OutputCode);
        Assert.Equal(3, result.Statistics.PointerParametersRewritten);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_PointerParameter_MixedWithOutRef_OnlyPointerNormalized()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p, out int x, ref long y)
                {
                    x = 0;
                    System.Console.Write(y);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte[] p", result.OutputCode);
        Assert.Contains("out int x", result.OutputCode);
        Assert.Contains("ref long y", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerParametersRewritten);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_PointerReturn_NormalizedToArray()
    {
        var src = """
            unsafe class C
            {
                static byte* M() => null;
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("static byte[] M()", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerReturnsRewritten);
        Assert.Equal(0, result.Statistics.PointerParametersRewritten);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_PointerPlusLengthAdjacentParameters_LengthPatternCounted()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p, int len)
                {
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte[] p", result.OutputCode);
        Assert.Contains("int len", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerParametersRewritten);
        Assert.Equal(1, result.Statistics.PointerLengthPatterns);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_PointerLocalFromRewrittenParameter_NormalizedAndFlowInherited()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p)
                {
                    byte* q = p;
                    System.Console.Write(*q);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte[] q = p;", result.OutputCode);
        // q 的 flow 继承 p（ParamArray），*q 在参数源上索引化为 p[0]。
        Assert.Contains("p[0]", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerParametersRewritten);
        Assert.Equal(1, result.Statistics.PointerLocalsRewritten);
        Assert.Equal(1, result.Statistics.PointerExprsIndexized);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_FixedOnRewrittenParameter_ArrayPathNoPointerResidual()
    {
        // probe 实证（CS8385）：R5 必须先把 p 改为 byte[]，R3 的 fixed 才能走数组路径。
        var src = """
            unsafe class C
            {
                static void M(byte* p)
                {
                    fixed (byte* q = p)
                    {
                        System.Console.Write(*q);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("fixed", result.OutputCode);
        Assert.DoesNotContain("byte*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_FixedOnRewrittenParameterWithOffset_OffsetPath()
    {
        // 配对索引模型（信号④：fixed 块内指针算术根植形参）：方法整体配对为
        // byte[] p + int pIndex，fixed 的偏移 p+2 表达为「同名 int 索引 =
        // pIndex + 2」（paired-index 统一规则，取代旧的合成 offset 计数路径）。
        var src = """
            unsafe class C
            {
                static void M(byte* p)
                {
                    fixed (byte* q = p + 2)
                    {
                        System.Console.Write(*q);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("fixed", result.OutputCode);
        Assert.Contains("static void M(byte[] p, int pIndex)", result.OutputCode);
        Assert.Contains("int q = pIndex + 2;", result.OutputCode);
        Assert.Contains("_q_array[q]", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ==================================================================
    // M3a / R6：指针表达式索引化（基础族）
    // ==================================================================

    [Fact]
    public void M3a_Dereference_IndexizedOnParameterArray()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p)
                {
                    byte a = *p;
                    System.Console.Write(a);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte a = p[0];", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerExprsIndexized);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_PointerSubscript_AlreadyArrayShape_KeptNotCounted()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p, int i)
                {
                    byte a = *p;
                    byte b = p[i];
                    System.Console.Write(a + b);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("p[0]", result.OutputCode);
        Assert.Contains("p[i]", result.OutputCode);
        // *p 计 1；p[i] 偏移 0、数组名即接收者，同构不计。
        Assert.Equal(1, result.Statistics.PointerExprsIndexized);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3a_PointerMemberAccess_Rejected_UF020()
    {
        var src = """
            unsafe class C
            {
                struct S { public int M; }

                static void F()
                {
                    S s = default;
                    fixed (S* p = &s)
                    {
                        System.Console.Write(p->M);
                    }
                }
            }
            """;

        // R6 的 p->m 分支只在 R3 未运行时成为首发诊断（默认链 R3 先拦截）。
        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void M3a_VoidPointerParameter_Rejected_UF021()
    {
        var src = """
            unsafe class C
            {
                static void M(void* p)
                {
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
        Assert.Equal(1, result.Statistics.Uf021Rejections);
    }

    [Fact]
    public void M3a_FunctionPointerParameter_Rejected_UF021()
    {
        var src = """
            unsafe class C
            {
                static void M(delegate*<int, int> p)
                {
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
        Assert.Equal(1, result.Statistics.Uf021Rejections);
    }

    [Fact]
    public void M3a_GenericPointerParameter_Rejected_UF021()
    {
        var src = """
            unsafe class C<T>
            {
                void M(T* p)
                {
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
        Assert.Equal(1, result.Statistics.Uf021Rejections);
    }

    [Fact]
    public void M3a_PointerToIntegerCast_Rejected_UF021()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p)
                {
                    int x = (int)p;
                    System.Console.Write(x);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
        Assert.Equal(1, result.Statistics.Uf021Rejections);
    }

    [Fact]
    public void M3a_IntegerToPointerCast_Rejected_UF021()
    {
        var src = """
            unsafe class C
            {
                static void M(int i)
                {
                    byte* p = (byte*)i;
                    System.Console.Write(1);
                }
            }
            """;

        // 本地指针声明归 R3（默认链发 UF020）；关 EnableFixed 后由 R6 发 UF021。
        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
    }

    [Fact]
    public void M3a_PointerArithmetic_Rejected_UF022()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p)
                {
                    p + 1;
                }
            }
            """;

        // R3 域在 fixed 块内；块外指针算术经 R6（关 EnableFixed 直达）UF022。
        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF022);
        Assert.Equal(1, result.Statistics.Uf022Rejections);
    }

    [Fact]
    public void M3b_PointerComparison_DifferentParams_Rejected_UF020()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* a, byte* b)
                {
                    bool x = a < b;
                    System.Console.Write(x);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        // M3b：两参数 flow 各自数组表达式不同源 → UF020（非 M3a 的 UF022）。
        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void M3b_PointerIncrement_Statement_NormalizesToIndexLocal()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p)
                {
                    p++;
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        // M3b：参数可承载索引局部，块外自增归一成功。
        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int _p_index = 0;", result.OutputCode);
        Assert.Contains("_p_index++;", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerIncrements);
    }

    [Fact]
    public void M3b_PointerDifference_DifferentParams_Rejected_UF020()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* a, byte* b)
                {
                    a - b;
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        // M3b：异参数差值不可证同源 → UF020（语句丢弃 UF022 不再是首要原因）。
        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void M3a_OutputIsIdempotent_SecondPassCleanNoDiagnostics()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p, int i)
                {
                    byte a = *p;
                    byte b = p[i];
                    System.Console.Write(a + b);
                }
            }
            """;

        var first = Rewrite(src);
        Assert.True(first.Changed);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(0, second.Statistics.PointerParametersRewritten);
        Assert.Equal(0, second.Statistics.PointerExprsIndexized);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void M3a_EnablePointerFalse_PointerSignaturesUntouched()
    {
        var src = """
            unsafe class C
            {
                static void M(byte* p)
                {
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnablePointer = false });

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("byte* p", result.OutputCode);
        Assert.Equal(0, result.Statistics.PointerParametersRewritten);
        Assert.Equal(0, result.Statistics.PointerExprsIndexized);
    }

    // ==================================================================
    // M3b：指针算术偏移传播（同源、元素单位、使用点物化）
    // ==================================================================

    [Fact]
    public void M3b_PointerPlusInt_MaterializesAtUse_AsArrayIndex()
    {
        var src = """
            unsafe class C
            {
                static byte M(byte* p)
                {
                    return *(p + 2);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("return p[2];", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerArithmeticPropagated);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3b_IntPlusPointer_AlsoNormalizes()
    {
        var src = """
            unsafe class C
            {
                static byte M(byte* p)
                {
                    return *(2 + p);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.Contains("return p[2];", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerArithmeticPropagated);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3b_PointerMinusInt_Normalizes()
    {
        var src = """
            unsafe class C
            {
                static byte M(byte* p)
                {
                    return *(p - 1);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.Contains("p[-1]", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerArithmeticPropagated);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3b_ChainedArithmetic_CombinesOffsets()
    {
        var src = """
            unsafe class C
            {
                static byte M(byte* p)
                {
                    return *(p + 1 + 2);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.Contains("p[3]", result.OutputCode);
        Assert.Equal(2, result.Statistics.PointerArithmeticPropagated);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3b_CompoundAssignment_NormalizesToIndexLocal()
    {
        var src = """
            unsafe class C
            {
                static byte M(byte* p)
                {
                    p += 3;
                    return *p;
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.Contains("_p_index += 3;", result.OutputCode);
        Assert.Contains("p[_p_index]", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerArithmeticPropagated);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3b_Comparison_SameSource_NormalizesToIndexComparison()
    {
        var src = """
            unsafe class C
            {
                static bool M(byte* p)
                {
                    byte* q = p;
                    return q == p;
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("0 == 0", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerComparisons);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3b_Difference_CastInt_CountsDifference()
    {
        var src = """
            unsafe class C
            {
                static int M(byte* p)
                {
                    return (int)(p - p);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.Contains("return 0;", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerDifferences);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M3b_LoweredIndexArithmetic_IsIdempotent_SecondPassClean()
    {
        var src = """
            unsafe class C
            {
                static int M(byte* p)
                {
                    byte x = *(p + 1);
                    return x;
                }
            }
            """;

        var first = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });
        Assert.True(first.Changed);
        AssertOutputCompiles(first.OutputCode);

        // lowered 的 int 索引参与算术按 int 处理：二次跑零变化零诊断。
        var second = Rewrite(first.OutputCode, new UnsafeFfmOptions { EnableFixed = false });
        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void M3b_ArithmeticStoredToField_Escapes_Rejected_UF022()
    {
        var src = """
            unsafe class C
            {
                byte[] f;
                static void M(byte* p, C c)
                {
                    c.f = p + 1;
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF022);
    }

    [Fact]
    public void M3b_IncrementInExpressionBody_NoBlock_Rejected_UF022()
    {
        var src = """
            unsafe class C
            {
                static int M(byte* p) => p++;
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF022);
    }

    // ==================================================================
    // M4 / R7：用户自定义 ref struct 声明 → UF052 降级 / UF050 拒绝
    // ==================================================================

    /// <summary>
    /// 双文件编译辅助：被处理文件可引用兄弟文件中声明的 ref struct
    /// （单文件站点规则测试需要，避免 R7 先对同文件声明作 ref struct 处置
    /// ——纯数据型降级/ref-only 拒绝）。
    /// </summary>
    private static UnsafeFfmResult RewriteWithSibling(
        string mainSrc, string siblingSrc, UnsafeFfmOptions? options = null)
    {
        var mainTree = CSharpSyntaxTree.ParseText(mainSrc, ParseOptions);
        var siblingTree = CSharpSyntaxTree.ParseText(siblingSrc, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-test",
            new[] { mainTree, siblingTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(mainTree);
        return new UnsafeFfmPreprocessor(options ?? new UnsafeFfmOptions())
            .RewriteFile(mainSrc, model);
    }

    [Fact]
    public void M4_UserRefStructDecl_PureData_Downgraded_UF052()
    {
        var src = """
            namespace P;
            public ref struct MyBuf { public int X; }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("ref struct", result.OutputCode);
        Assert.Contains("public struct MyBuf", result.OutputCode);
        Assert.False(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF052);
        Assert.Equal(UnsafeFfmSeverity.Warning, diag.Severity);
        Assert.Contains("stack-only", diag.Message);
        Assert.Contains("plain struct", diag.Message);
        Assert.Equal(1, result.Statistics.Uf052Downgrades);
        Assert.Equal(0, result.Statistics.Uf050Rejections);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_ReadOnlyRefStructDecl_PureData_Downgraded_UF052()
    {
        var src = """
            namespace P;
            public readonly ref struct RoBuf { public int X { get; } }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("ref struct", result.OutputCode);
        Assert.Contains("public readonly struct RoBuf", result.OutputCode);
        Assert.False(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF052);
        Assert.Equal(1, result.Statistics.Uf052Downgrades);
        Assert.Equal(0, result.Statistics.Uf050Rejections);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_RefOnlyRefStructDecl_RefLikeField_Rejected_UF050()
    {
        // ref-like 实例字段（Span<T>）去 ref 后成非法普通 struct：维持拒绝。
        var src = """
            namespace P;
            public ref struct MyBuf { public System.Span<byte> B; }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Equal(src, result.OutputCode);
        var diag = Assert.Single(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF050);
        Assert.Equal(UnsafeFfmSeverity.Error, diag.Severity);
        Assert.Contains("stack-only", diag.Message);
        Assert.Equal(1, result.Statistics.Uf050Rejections);
        Assert.Equal(0, result.Statistics.Uf052Downgrades);
    }

    [Fact]
    public void M4_RefOnlyRefStructDecl_RefField_Rejected_UF050()
    {
        // ref 字段去 ref 后成非法普通 struct（CS9059）：维持拒绝。
        var src = """
            namespace P;
            public ref struct MyBuf { private ref int _r; }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF050);
        Assert.Equal(1, result.Statistics.Uf050Rejections);
        Assert.Equal(0, result.Statistics.Uf052Downgrades);
    }

    [Fact]
    public void M4_SpanUseSite_IsUnchanged_NoUF050()
    {
        var src = """
            class C
            {
                static int M(System.Span<byte> data) => data[0];
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF050);
    }

    // ==================================================================
    // M4 / InlineArray → UF051
    // ==================================================================

    [Fact]
    public void M4_InlineArray_Rejected_UF051()
    {
        var src = """
            using System.Runtime.CompilerServices;
            namespace P;
            [InlineArray(16)]
            struct Block16 { private byte _e0; }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF051);
        Assert.Equal(1, result.Statistics.Uf051Rejections);
    }

    // ==================================================================
    // M4 / R9：ref struct using 站点（Span 空操作 / UF055）
    // ==================================================================

    [Fact]
    public void M4_UsingDeclaration_Span_NoOpNormalized()
    {
        var src = """
            class C
            {
                static void M(byte[] data)
                {
                    using System.Span<byte> s = data;
                    System.Console.WriteLine(s.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("System.Span<byte> s = data;", result.OutputCode);
        Assert.DoesNotContain("using", result.OutputCode);
        Assert.Equal(1, result.Statistics.SpanUsingNoOps);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_UsingDeclaration_ReadOnlySpan_NoOpNormalized()
    {
        var src = """
            class C
            {
                static void M(System.ReadOnlySpan<char> text)
                {
                    using System.ReadOnlySpan<char> s = text;
                    System.Console.WriteLine(s.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("System.ReadOnlySpan<char> s = text;", result.OutputCode);
        Assert.DoesNotContain("using", result.OutputCode);
        Assert.Equal(1, result.Statistics.SpanUsingNoOps);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_UsingBlock_Span_NoOpNormalized()
    {
        var src = """
            class C
            {
                static void M(byte[] data)
                {
                    using (System.Span<byte> s = data)
                    {
                        System.Console.WriteLine(s.Length);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("System.Span<byte> s = data;", result.OutputCode);
        Assert.Contains("System.Console.WriteLine(s.Length);", result.OutputCode);
        Assert.DoesNotContain("using", result.OutputCode);
        Assert.Equal(1, result.Statistics.SpanUsingNoOps);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_UsingDeclaration_NonWhitelistRefStruct_UF055()
    {
        var sibling = """
            namespace P;
            public ref struct MyBuf { public int X; }
            """;
        var main = """
            using P;
            class C
            {
                static void M(MyBuf b)
                {
                    using MyBuf x = b;
                    System.Console.Write(x.X);
                }
            }
            """;

        var result = RewriteWithSibling(main, sibling);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF055);
        Assert.Equal(1, result.Statistics.Uf055Rejections);
    }

    [Fact]
    public void M4_UsingBlock_NonWhitelistRefStruct_UF055()
    {
        var sibling = """
            namespace P;
            public ref struct MyBuf { public int X; }
            """;
        var main = """
            using P;
            class C
            {
                static void M(MyBuf b)
                {
                    using (MyBuf x = b)
                    {
                        System.Console.Write(x.X);
                    }
                }
            }
            """;

        var result = RewriteWithSibling(main, sibling);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF055);
        Assert.Equal(1, result.Statistics.Uf055Rejections);
    }

    [Fact]
    public void M4_AwaitUsing_IsNotTouched_NoDiagnostic()
    {
        var src = """
            using System;
            using System.Threading.Tasks;
            sealed class Cx : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => default;
            }
            class C
            {
                static async Task M()
                {
                    await using var x = new Cx();
                    System.Console.Write(1);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF055);
        Assert.Contains("await using", result.OutputCode);
    }

    [Fact]
    public void M4_RegularDisposableUsing_IsNotTouched_NoUF055()
    {
        var src = """
            using System.IO;
            class C
            {
                static void M()
                {
                    using var ms = new MemoryStream();
                    System.Console.Write(ms.Length);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF055);
        Assert.Contains("using var ms", result.OutputCode);
    }

    // ==================================================================
    // M4 / R8：extern-like → UF060
    // ==================================================================

    [Fact]
    public void M4_ExternMethod_Rejected_UF060_MessageGuidesC42()
    {
        var src = """
            class C
            {
                public static extern int Plain();
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        var diag = Assert.Single(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF060);
        Assert.Contains("平台隔离通道（C42）", diag.Message);
        Assert.Equal(1, result.Statistics.Uf060Rejections);
    }

    [Fact]
    public void M4_DllImportMethod_Rejected_UF060()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("x")]
                public static extern int M();
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF060);
        Assert.Equal(1, result.Statistics.Uf060Rejections);
    }

    [Fact]
    public void M4_LibraryImportMethod_Rejected_UF060()
    {
        // LibraryImport 为 partial 定义（源生成器在真实工程提供实现部分）；
        // 本测试无生成器、编译有 CS8795，但符号绑定完整、UF060 仍应发出。
        var src = """
            using System.Runtime.InteropServices;
            partial class C
            {
                [LibraryImport("x")]
                public static partial int M();
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF060);
        Assert.Equal(1, result.Statistics.Uf060Rejections);
    }

    // ==================================================================
    // M4 / R8：Marshal/NativeLibrary/SafeHandle/HandleRef → UF070
    // ==================================================================

    [Fact]
    public void M4_MarshalCall_Rejected_UF070()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                static void M()
                {
                    System.Console.WriteLine(Marshal.SizeOf<int>());
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF070);
        Assert.Equal(1, result.Statistics.Uf070Rejections);
    }

    [Fact]
    public void M4_NativeLibraryCall_Rejected_UF070()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                static void M()
                {
                    System.Console.WriteLine(NativeLibrary.Load("x"));
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF070);
        Assert.Equal(1, result.Statistics.Uf070Rejections);
    }

    [Fact]
    public void M4_SafeHandleTypeUse_Rejected_UF070()
    {
        var src = """
            class C
            {
                System.Runtime.InteropServices.SafeHandle? handle;
                static void M(C c)
                {
                    System.Console.Write(c.handle != null);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF070);
        Assert.Equal(1, result.Statistics.Uf070Rejections);
    }

    [Fact]
    public void M4_HandleRefTypeUse_Rejected_UF070()
    {
        var src = """
            class C
            {
                static void M()
                {
                    System.Runtime.InteropServices.HandleRef hr = default;
                    System.Console.Write(hr);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF070);
        Assert.Equal(1, result.Statistics.Uf070Rejections);
    }

    [Fact]
    public void M4_SafeHandleDerivationConstruction_Rejected_UF070()
    {
        var src = """
            using System;
            using System.Runtime.InteropServices;
            sealed class MyHandle : SafeHandle
            {
                public MyHandle() : base(IntPtr.Zero, ownsHandle: true) { }
                public override bool IsInvalid => false;
                protected override bool ReleaseHandle() => true;
            }
            class C
            {
                static void M()
                {
                    var h = new MyHandle();
                    System.Console.Write(h);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF070);
    }

    [Fact]
    public void M4_EnableExternFalse_ExternUntouched_NoRejection()
    {
        var src = """
            class C
            {
                public static extern int Plain();
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableExtern = false });

        Assert.False(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Equal(0, result.Statistics.Uf060Rejections);
        Assert.Equal(0, result.Statistics.Uf070Rejections);
    }

    // ==================================================================
    // M4：UF061 布局骨架单测（pass 不触发，仅冻结骨架）
    // ==================================================================

    private static IMethodSymbol GetMethodSymbol(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-layout", new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return EnumerateTypes(compilation.GlobalNamespace)
            .SelectMany(t => t.GetMembers(methodName))
            .OfType<IMethodSymbol>()
            .Single();
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateTypes(type))
            {
                yield return nested;
            }
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in EnumerateTypes(child))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateTypes(nested))
            {
                yield return deeper;
            }
        }
    }

    [Fact]
    public void M4_LayoutSkeleton_RefParameter_Blocker()
    {
        var method = GetMethodSymbol(
            "class C { static void M(ref int x) { x = 1; } }", "M");

        Assert.True(MarshalLayoutSkeleton.TryGetLayoutBlocker(method, out var blocker));
        Assert.Contains("ref", blocker);
    }

    [SlowE2EFact(8.5)]
    public void M4_LayoutSkeleton_CleanMethod_NoBlocker()
    {
        var method = GetMethodSymbol(
            "class C { static int M(int x) => x; }", "M");

        Assert.False(MarshalLayoutSkeleton.TryGetLayoutBlocker(method, out var blocker));
        Assert.Equal(string.Empty, blocker);
    }

    [Fact]
    public void M4_LayoutSkeleton_ExplicitLayoutUnion_Blocker()
    {
        var src = """
            using System.Runtime.InteropServices;
            [StructLayout(LayoutKind.Explicit)]
            struct U
            {
                [FieldOffset(0)] public int A;
                [FieldOffset(0)] public int B;
            }
            class C
            {
                static void M(U u) { System.Console.Write(u.A); }
            }
            """;
        var method = GetMethodSymbol(src, "M");

        Assert.True(MarshalLayoutSkeleton.TryGetLayoutBlocker(method, out var blocker));
        Assert.Contains("Explicit", blocker);
    }

    [Fact]
    public void M4_LayoutSkeleton_FrozenWidthConstants()
    {
        Assert.Equal(1, MarshalLayoutSkeleton.BoolWidthBytes);
        Assert.Equal(2, MarshalLayoutSkeleton.CharWidthBytes);
    }

    // ==================================================================
    // M4：幂等 / 统计
    // ==================================================================

    [Fact]
    public void M4_SpanUsingNormalized_Idempotent_SecondPassClean()
    {
        var src = """
            class C
            {
                static void M(byte[] data)
                {
                    using System.Span<byte> s = data;
                    System.Console.WriteLine(s.Length);
                }
            }
            """;

        var first = Rewrite(src);
        Assert.True(first.Changed);
        AssertOutputCompiles(first.OutputCode);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(0, second.Statistics.SpanUsingNoOps);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void M4_Stats_MultipleSites_CountersAddUp()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                SafeHandle? a;
                static void M()
                {
                    System.Console.WriteLine(Marshal.SizeOf<long>());
                    System.Console.WriteLine(NativeLibrary.Load("y"));
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Equal(3, result.Statistics.Uf070Rejections);
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == UnsafeFfmDiagnosticCode.UF070));
    }

    // ==================================================================
    // M4 / R8 opt-in 子段：FFM 白名单 facade 归一
    // ==================================================================

    private const string BeepWhitelistJson = """
        {
          "entries": [
            { "method": "C.Beep", "library": "kernel32", "entryPoint": "Beep" },
            { "method": "C.FormatMsg", "library": "kernel32", "entryPoint": "FormatMessageW", "stringEncoding": "utf16" },
            { "method": "C.DefaultName", "library": "kernel32" }
          ]
        }
        """;

    private static UnsafeFfmOptions WhitelistOptions(string json) => new()
    {
        Whitelist = ExternWhitelist.Parse(json),
    };

    [Fact]
    public void M4_WhitelistedDllImport_NormalizedToInteropFacade_AndCompiles()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("kernel32")]
                public static extern int Beep(int frequency, int duration);
            }
            """;

        var result = Rewrite(src, WhitelistOptions(BeepWhitelistJson));

        Assert.True(result.Changed);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == UnsafeFfmSeverity.Error));
        Assert.DoesNotContain("extern", result.OutputCode);
        Assert.DoesNotContain("DllImport", result.OutputCode);
        Assert.Contains("global::Cs2j.Generation.InteropFacade(\"kernel32\", \"Beep\")",
            result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("PlatformNotSupportedException", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("namespace Cs2j.Generation", result.OutputCode, StringComparison.Ordinal);
        Assert.Equal(1, result.Statistics.FfmFacadesEmitted);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_WhitelistedLibraryImport_PartialStripped_FacadeEmitted()
    {
        var src = """
            using System.Runtime.InteropServices;
            partial class C
            {
                [LibraryImport("kernel32", EntryPoint = "Beep")]
                public static partial int Beep(int a, int b);
            }
            """;

        var result = Rewrite(src, WhitelistOptions(BeepWhitelistJson));

        Assert.True(result.Changed);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == UnsafeFfmSeverity.Error));
        Assert.DoesNotContain("partial int Beep", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("InteropFacade(\"kernel32\", \"Beep\")", result.OutputCode, StringComparison.Ordinal);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_WhitelistLoaded_ButMethodNotListed_StillUF060()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("x")]
                public static extern int Other(int a);
            }
            """;

        var result = Rewrite(src, WhitelistOptions(BeepWhitelistJson));

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF060);
        Assert.Equal(0, result.Statistics.FfmFacadesEmitted);
    }

    [Fact]
    public void M4_Whitelisted_StringParameter_Utf16NamedArgument()
    {
        var src = """
            using System;
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("kernel32")]
                public static extern int FormatMsg(int flags, string text);
            }
            """;

        var result = Rewrite(src, WhitelistOptions(BeepWhitelistJson));

        Assert.True(result.Changed);
        Assert.Contains("FormatMessageW", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("StringEncoding = \"utf16\"", result.OutputCode, StringComparison.Ordinal);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_Whitelisted_MissingEntryPoint_DefaultsToCSharpMethodName()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("kernel32")]
                public static extern int DefaultName(int a);
            }
            """;

        var result = Rewrite(src, WhitelistOptions(BeepWhitelistJson));

        Assert.True(result.Changed);
        Assert.Contains("InteropFacade(\"kernel32\", \"DefaultName\")",
            result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void M4_Whitelisted_BoolParameter_UF061_WidthAmbiguity()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("x")]
                public static extern int Beep(int a, bool b);
            }
            """;

        var result = Rewrite(src, WhitelistOptions("""
            { "entries": [ { "method": "C.Beep", "library": "x" } ] }
            """));

        Assert.False(result.Changed);
        var diag = Assert.Single(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF061);
        Assert.Contains("bool", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.Statistics.Uf061Rejections);
    }

    [Fact]
    public void M4_Whitelisted_RefParameter_UF061()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("x")]
                public static extern int M(ref int a);
            }
            """;

        var result = Rewrite(src, WhitelistOptions("""
            { "entries": [ { "method": "C.M", "library": "x" } ] }
            """));

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF061);
    }

    [Fact]
    public void M4_Whitelisted_StructParameterAndStringReturn_UF061()
    {
        var src = """
            using System;
            using System.Runtime.InteropServices;
            struct Point { public int X; public int Y; }
            class C
            {
                [DllImport("x")]
                public static extern void Take(Point p);
                [DllImport("y")]
                public static extern string Ret(int a);
            }
            """;

        var result = Rewrite(src, WhitelistOptions("""
            { "entries": [
                { "method": "C.Take", "library": "x" },
                { "method": "C.Ret", "library": "y" }
            ] }
            """));

        Assert.False(result.Changed);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == UnsafeFfmDiagnosticCode.UF061));
    }

    [Fact]
    public void M4_WhitelistFacade_Idempotent_SecondRunClean()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("kernel32")]
                public static extern int Beep(int a, int b);
            }
            """;
        var options = WhitelistOptions(BeepWhitelistJson);

        var first = Rewrite(src, options);
        Assert.True(first.Changed);

        // 二次跑：facade 不再 extern-like，支撑类型已存在 → Changed=false、无诊断。
        var second = Rewrite(first.OutputCode, options);
        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void M4_TwoFacadesInOneUnit_SupportTypeEmittedOnce()
    {
        var src = """
            using System.Runtime.InteropServices;
            class C
            {
                [DllImport("kernel32")]
                public static extern int Beep(int a, int b);
                [DllImport("kernel32")]
                public static extern void DefaultName(int a);
            }
            """;

        var result = Rewrite(src, WhitelistOptions(BeepWhitelistJson));

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.FfmFacadesEmitted);
        Assert.Equal(1, CountOccurrences(result.OutputCode, "class InteropFacadeAttribute"));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M4_FacadeAcrossFiles_SameAssembly_SupportTypeEmittedOnce()
    {
        // 模拟项目管线：共享 compilation、共享 preprocessor 实例，逐文件改写。
        var src1 = """
            using System.Runtime.InteropServices;
            class C1
            {
                [DllImport("kernel32")]
                public static extern int Beep(int a, int b);
            }
            """;
        var src2 = """
            using System.Runtime.InteropServices;
            class C2
            {
                [DllImport("kernel32")]
                public static extern void DefaultName(int a);
            }
            """;
        var json = """
            { "entries": [
                { "method": "C1.Beep", "library": "kernel32" },
                { "method": "C2.DefaultName", "library": "kernel32" }
            ] }
            """;

        var tree1 = CSharpSyntaxTree.ParseText(src1, ParseOptions);
        var tree2 = CSharpSyntaxTree.ParseText(src2, ParseOptions);
        var compilation = CSharpCompilation.Create("uffm-multi",
            new[] { tree1, tree2 }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new UnsafeFfmPreprocessor(WhitelistOptions(json));

        var r1 = preprocessor.RewriteFile(src1, compilation.GetSemanticModel(tree1));
        var r2 = preprocessor.RewriteFile(src2, compilation.GetSemanticModel(tree2));

        Assert.True(r1.Changed);
        Assert.True(r2.Changed);
        Assert.Equal(1, CountOccurrences(r1.OutputCode, "namespace Cs2j.Generation"));
        Assert.DoesNotContain("namespace Cs2j.Generation", r2.OutputCode, StringComparison.Ordinal);

        // 两文件合在一起可编译（支撑类型只存在于文件 1）。
        var combined = CSharpSyntaxTree.ParseText(r1.OutputCode, ParseOptions);
        var combined2 = CSharpSyntaxTree.ParseText(r2.OutputCode, ParseOptions);
        var verify = CSharpCompilation.Create("uffm-multi-verify",
            new[] { combined, combined2 }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = verify.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void M4_FacadeAcrossFiles_FirstFileRollsBack_SecondStillGetsSupport()
    {
        // 文件1：一个可归一 facade + 一个 UF070 → 整文回滚，支撑占位必须释放；
        // 文件2：正常 facade → 仍应注入支撑类型。
        var src1 = """
            using System.Runtime.InteropServices;
            class C1
            {
                [DllImport("kernel32")]
                public static extern int Beep(int a, int b);
                static void Bad()
                {
                    System.Console.WriteLine(Marshal.SizeOf<long>());
                }
            }
            """;
        var src2 = """
            using System.Runtime.InteropServices;
            class C2
            {
                [DllImport("kernel32")]
                public static extern void DefaultName(int a);
            }
            """;
        var json = """
            { "entries": [
                { "method": "C1.Beep", "library": "kernel32" },
                { "method": "C2.DefaultName", "library": "kernel32" }
            ] }
            """;

        var tree1 = CSharpSyntaxTree.ParseText(src1, ParseOptions);
        var tree2 = CSharpSyntaxTree.ParseText(src2, ParseOptions);
        var compilation = CSharpCompilation.Create("uffm-rollback",
            new[] { tree1, tree2 }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new UnsafeFfmPreprocessor(WhitelistOptions(json));

        var r1 = preprocessor.RewriteFile(src1, compilation.GetSemanticModel(tree1));
        var r2 = preprocessor.RewriteFile(src2, compilation.GetSemanticModel(tree2));

        Assert.False(r1.Changed);
        Assert.Contains(r1.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF070);
        Assert.Equal(0, r1.Statistics.FfmFacadesEmitted);

        Assert.True(r2.Changed);
        Assert.Equal(1, r2.Statistics.FfmFacadesEmitted);
        Assert.Contains("namespace Cs2j.Generation", r2.OutputCode, StringComparison.Ordinal);
        AssertOutputCompiles(r2.OutputCode);
    }

    [Fact]
    public void M4_WhitelistJson_DuplicateKey_Throws()
    {
        const string json = """
            { "entries": [
                { "method": "C.M", "library": "a" },
                { "method": "C.M", "library": "b" }
            ] }
            """;

        Assert.Throws<System.Text.Json.JsonException>(() => ExternWhitelist.Parse(json));
    }

    [Fact]
    public void M4_WhitelistJson_MissingLibrary_Throws()
    {
        const string json = """{ "entries": [ { "method": "C.M" } ] }""";

        Assert.Throws<System.Text.Json.JsonException>(() => ExternWhitelist.Parse(json));
    }

    [Fact]
    public void M4_FileScopedNamespace_FlipsToBlockScoped_AndCompiles()
    {
        // 文件级 namespace 与注入的块级支撑 namespace 不能混用（CS8955）：
        // 注入必须把宿主单元机械翻转为块级 namespace。
        var src = """
            using System.Runtime.InteropServices;
            namespace My.App;

            internal static class Native
            {
                [DllImport("kernel32")]
                public static extern int Beep(int a, int b);

                [DllImport("kernel32", EntryPoint = "FormatMessageW", CharSet = CharSet.Unicode)]
                public static extern int FormatMsg(int flags, string text);
            }
            """;

        var result = Rewrite(src, WhitelistOptions("""
            { "entries": [
                { "method": "My.App.Native.Beep", "library": "kernel32", "entryPoint": "Beep" },
                { "method": "My.App.Native.FormatMsg", "library": "kernel32", "entryPoint": "FormatMessageW", "stringEncoding": "utf16" }
            ] }
            """));

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.FfmFacadesEmitted);
        // 文件级声明必须消失；宿主与支撑都以块级 namespace 存在。
        Assert.DoesNotContain("namespace My.App;", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("namespace My.App", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("namespace Cs2j.Generation", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("StringEncoding = \"utf16\"", result.OutputCode, StringComparison.Ordinal);
        AssertOutputCompiles(result.OutputCode);
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

    // ==================================================================
    // UF020 第二子族：同源证明（数组/string/pointer+length/末端指针）、
    // 裸指针局部继承与 *(p-k)、只读资源指针字段、资源流物化、
    // 标量位重解释、byte*→char* UTF-16 重解释、保守拒绝与幂等。
    // ==================================================================

    [Fact]
    public void Uf020b_FixedArray_EndPointer_ComparisonAndDifference_Normalized()
    {
        var src = """
            unsafe class C
            {
                static int M(byte[] data)
                {
                    fixed (byte* p = data)
                    {
                        byte* end = p + data.Length;
                        int n = 0;
                        while (p + n != end)
                        {
                            n++;
                        }
                        return n + (int)(end - p);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.PointerComparisons);
        Assert.Equal(1, result.Statistics.PointerDifferences);
        Assert.DoesNotContain("byte*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_FixedString_EndPointer_Comparison_Normalized()
    {
        var src = """
            unsafe class C
            {
                static int M(string s)
                {
                    fixed (char* p = s)
                    {
                        char* end = p + s.Length;
                        int n = 0;
                        while (p + n < end)
                        {
                            n++;
                        }
                        return n;
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.PointerComparisons);
        Assert.Contains("s.ToCharArray()", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_PointerPlusLengthParameter_EndPointer_ComparisonDifference_Normalized()
    {
        var src = """
            unsafe class C
            {
                static int M(byte* p, int length)
                {
                    byte* end = p + length;
                    int n = 0;
                    while (p < end)
                    {
                        p++;
                        n++;
                    }
                    return n + (int)(end - p);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.PointerComparisons);
        Assert.Equal(1, result.Statistics.PointerDifferences);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_FixedArray_NegativeOffsetDereference_Normalized()
    {
        var src = """
            unsafe class C
            {
                static byte M(byte[] data)
                {
                    fixed (byte* p = &data[2])
                    {
                        return *(p - 1);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        // p 以 2 为初值的 int 索引局部，*(p-1) 即相对该索引再前移一位。
        Assert.Contains("int p = 2;", result.OutputCode);
        Assert.Contains("return _p_array[p - 1];", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_FixedArray_BareLocalInheritsSource_NegativeDereference_Normalized()
    {
        var src = """
            unsafe class C
            {
                static byte M(byte[] data)
                {
                    fixed (byte* p = data)
                    {
                        byte* q = p;
                        return *(q - 1);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains("int q = p;", result.OutputCode);
        Assert.Contains("_p_array[q - 1]", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_DifferentArrays_PointerComparison_Rejected_UF020()
    {
        var src = """
            unsafe class C
            {
                static void M(byte[] a, byte[] b)
                {
                    fixed (byte* p = a)
                    fixed (byte* q = b)
                    {
                        System.Console.Write(p < q);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void Uf020b_FixedCursor_EscapesToPointerField_Rejected_UF020()
    {
        var src = """
            unsafe class C
            {
                private static byte* f;

                static void M(byte[] a)
                {
                    fixed (byte* p = a)
                    {
                        f = p;
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    private const string ResourceFieldsSource = """

        using System.IO;
        using System.Reflection;

        unsafe class ResourceTable
        {
            private static volatile byte* s_Props;
            internal byte* props;

            private static void InitInstance()
            {
                if (s_Props != null)
                {
                    return;
                }

                UnmanagedMemoryStream memStream =
                    (UnmanagedMemoryStream)typeof(ResourceTable).Assembly
                        .GetManifestResourceStream("ResourceTable.bin");
                byte* p = memStream.PositionPointer;
                s_Props = p;
            }

            private ResourceTable(byte* props)
            {
                this.props = props;
            }

            public static ResourceTable Instance
            {
                get
                {
                    if (s_Props == null)
                    {
                        InitInstance();
                    }
                    return new ResourceTable(s_Props);
                }
            }

            public byte Get(char ch)
            {
                return props[ch];
            }
        }
        """;

    [Fact]
    public void Uf020b_ReadOnlyResourcePointerFields_StaticAndInstance_DowngradedToArrays()
    {
        var result = Rewrite(ResourceFieldsSource);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains("private static volatile byte[] s_Props;", result.OutputCode);
        Assert.Contains("internal byte[] props;", result.OutputCode);
        Assert.Contains("new byte[(int)memStream.Length]", result.OutputCode);
        Assert.Contains("memStream.Read(_p_array, 0, _p_array.Length);", result.OutputCode);
        Assert.Contains("s_Props = _p_array;", result.OutputCode);
        Assert.DoesNotContain("byte*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_ReadOnlyResourcePointerField_StructCopyCtor_CrossInstanceCopy_Downgraded()
    {
        var src = """

            using System.IO;
            using System.Reflection;

            unsafe struct ResourceTable
            {
                private static volatile byte* s_Props;
                internal byte* props;

                private static void Init()
                {
                    UnmanagedMemoryStream memStream =
                        (UnmanagedMemoryStream)typeof(ResourceTable).Assembly
                            .GetManifestResourceStream("ResourceTable.bin");
                    byte* p = memStream.PositionPointer;
                    s_Props = p;
                }

                private ResourceTable(byte* props)
                {
                    this.props = props;
                }

                private ResourceTable(ResourceTable other)
                {
                    props = other.props;
                }

                public ResourceTable Clone() => new ResourceTable(this);

                public byte Get(char ch) => props[ch];
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains("internal byte[] props;", result.OutputCode);
        // 跨实例拷贝降级为托管数组引用拷贝（只读资源共享不变量）。
        Assert.Contains("props = other.props;", result.OutputCode);
        Assert.Contains("public ResourceTable Clone() => new ResourceTable(this);",
            result.OutputCode);
        Assert.DoesNotContain("byte*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_ResourcePointerField_SubscriptWrite_Rejected_UF020()
    {
        var src = """

            using System.IO;
            using System.Reflection;

            unsafe class C
            {
                private static byte* s_Props;

                private static void Init()
                {
                    UnmanagedMemoryStream memStream =
                        (UnmanagedMemoryStream)typeof(C).Assembly
                            .GetManifestResourceStream("C.bin");
                    s_Props = memStream.PositionPointer;
                }

                static void Mutate(byte b)
                {
                    s_Props[0] = b;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void Uf020b_ResourcePointerField_ForeignParameterWrite_Rejected_UF020()
    {
        var src = """

            using System.IO;
            using System.Reflection;

            unsafe class C
            {
                private static byte* s_Props;

                public static void Store(byte* p)
                {
                    s_Props = p;
                }

                public static byte Get(int i) => s_Props[i];
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        // 非常造器单次 copy-in 形参：来源不可证，整文回滚。
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void Uf020b_ResourceStreamPositionPointer_LocalMaterializedToArray()
    {
        var src = """

            using System.IO;
            using System.Reflection;

            unsafe class C
            {
                static byte Get()
                {
                    UnmanagedMemoryStream memStream =
                        (UnmanagedMemoryStream)typeof(C).Assembly
                            .GetManifestResourceStream("C.bin");
                    byte* p = memStream.PositionPointer;
                    return p[1];
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains("byte[] _p_array = new byte[(int)memStream.Length];", result.OutputCode);
        Assert.Contains("memStream.Read(_p_array, 0, _p_array.Length);", result.OutputCode);
        Assert.Contains("return _p_array[(p) + (1)];", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_ResourceStreamPointer_SeekBeforeUse_Rejected_UF020()
    {
        var src = """

            using System.IO;
            using System.Reflection;

            unsafe class C
            {
                static byte Get()
                {
                    UnmanagedMemoryStream memStream =
                        (UnmanagedMemoryStream)typeof(C).Assembly
                            .GetManifestResourceStream("C.bin");
                    memStream.Seek(0, SeekOrigin.Begin);
                    byte* p = memStream.PositionPointer;
                    return p[1];
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Theory]
    // 强转指针元素即目标类型：源位模式按同尺寸目标解释。
    [InlineData("double value", "long", "long", "DoubleToInt64Bits(value)")]
    [InlineData("int value", "float", "float", "Int32BitsToSingle(value)")]
    [InlineData("uint value", "float", "float", "Int32BitsToSingle(unchecked((int)value))")]
    [InlineData("long value", "double", "double", "Int64BitsToDouble(value)")]
    [InlineData("ulong value", "double", "double", "Int64BitsToDouble(unchecked((long)value))")]
    public void Uf020b_ScalarBitReinterpret_FivePairs_NormalizedToBitConverter(
        string parameter, string ret, string pointerElement, string expectedCall)
    {
        var src = $$"""

            unsafe class C
            {
                static {{ret}} M({{parameter}})
                {
                    return *(({{pointerElement}}*)&value);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains("global::System.BitConverter." + expectedCall, result.OutputCode);
        Assert.DoesNotContain("*(", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_ScalarBitReinterpret_WrongSizePair_Rejected_UF020()
    {
        var src = """

            unsafe class C
            {
                static int M(double value)
                {
                    return *((int*)&value);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void Uf020b_ScalarBitReinterpret_IsIdempotent_SecondPassClean()
    {
        var src = """

            unsafe class C
            {
                static long M(double value)
                {
                    return *((long*)&value);
                }
            }
            """;

        var first = Rewrite(src);
        Assert.True(first.Changed);
        AssertOutputCompiles(first.OutputCode);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void Uf020b_Utf16Reinterpret_NewString_NormalizedToEncodingUnicodeGetString()
    {
        var src = """

            using System;

            unsafe class C
            {
                private string GetStringAligned(byte[] data, int offset, int cch)
                {
                    fixed (byte* pb = data)
                    {
                        char* p = (char*)(pb + offset);
                        string withOffset = new String(p + 1, 0, cch);
                        return new String(p, 0, cch);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains(
            "global::System.Text.Encoding.Unicode.GetString(_pb_array, (pb + offset), checked((cch) * 2))",
            result.OutputCode);
        Assert.Contains(
            "global::System.Text.Encoding.Unicode.GetString(_pb_array, (pb + offset) + (1) * 2, checked((cch) * 2))",
            result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_Utf16Reinterpret_NonStringCtorUse_Rejected_UF020()
    {
        var src = """

            unsafe class C
            {
                private static char M(byte[] data, int offset)
                {
                    fixed (byte* pb = data)
                    {
                        char* p = (char*)(pb + offset);
                        return p[0];
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void Uf020b_Utf16Reinterpret_IsIdempotent_SecondPassClean()
    {
        var src = """

            using System;

            unsafe class C
            {
                private static string M(byte[] data, int cch)
                {
                    fixed (byte* pb = data)
                    {
                        char* p = (char*)pb;
                        return new String(p, 0, cch);
                    }
                }
            }
            """;

        var first = Rewrite(src);
        Assert.True(first.Changed);
        Assert.False(first.HasErrors,
            string.Join("\n", first.Diagnostics));
        AssertOutputCompiles(first.OutputCode);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void Uf020b_PairedMethod_FixedCursorsAndEndPointers_CombinedNormalization()
    {
        var src = """

            unsafe class C
            {
                private static int Consume(char* p, char* pEnd, byte* pb, byte* pbEnd)
                {
                    char* q = p;
                    int n = 0;
                    while (q < pEnd)
                    {
                        q++;
                        n++;
                    }
                    return n + (int)(pbEnd - pb);
                }

                private static int M(char[] chars, int charLen, byte[] bytes, int byteLen)
                {
                    fixed (char* p = chars)
                    fixed (byte* pb = bytes)
                    {
                        return Consume(p, p + charLen, pb, pb + byteLen);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        // Consume 四个白名单指针形参全部按配对约定降级为数组+索引。
        Assert.Equal(4, result.Statistics.PointerParametersRewritten);
        Assert.Contains("char[] p, int pIndex", result.OutputCode);
        Assert.Contains("byte[] pb, int pbIndex", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerComparisons);
        Assert.Equal(1, result.Statistics.PointerDifferences);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_ResourceFields_NormalizedOutput_IsIdempotent_SecondPassClean()
    {
        var first = Rewrite(ResourceFieldsSource);
        Assert.True(first.Changed);
        Assert.False(first.HasErrors,
            string.Join("\n", first.Diagnostics));
        AssertOutputCompiles(first.OutputCode);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
    }

    // ==================================================================
    // UF020 第二子族补遗：游标命名族（Begin/End/EndPos）、裸别名闭包、
    // 形参索引↔同方法 fixed 同族索引、形参 ±n 外发实参（extern/元数据）。
    // ==================================================================

    [Fact]
    public void Uf020b_BeginEndNamedParams_BareAliasClosure_ComparisonDifferenceNormalized()
    {
        var src = """
            unsafe class C
            {
                private char[] bufChars = new char[64];
                private int bufPos;

                unsafe void WriteRaw(char* pSrcBegin, char* pSrcEnd)
                {
                    fixed (char* pDstBegin = bufChars)
                    {
                        char* pSrc = pSrcBegin;
                        char* pDst = pDstBegin + bufPos;
                        char* pDstEnd = pDst + (pSrcEnd - pSrc);
                        bool done = pSrc <= pSrcEnd && pSrc >= pSrcEnd;
                        bufPos = (int)(pDst - pDstBegin);
                        System.Console.Write(done);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains(
            "void WriteRaw(char[] pSrcBegin, int pSrcBeginIndex, char[] pSrcEnd, int pSrcEndIndex)",
            result.OutputCode);
        Assert.Contains("int pSrc = pSrcBeginIndex;", result.OutputCode);
        Assert.Contains("pSrcEndIndex - pSrc", result.OutputCode);
        Assert.Contains("pSrc <= pSrcEndIndex", result.OutputCode);
        Assert.Contains("pSrc >= pSrcEndIndex", result.OutputCode);
        Assert.Contains("pDst - pDstBegin", result.OutputCode);
        Assert.Equal(2, result.Statistics.PointerComparisons);
        Assert.Equal(2, result.Statistics.PointerDifferences);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_ParamAndSameFamilyFixedCursor_DifferenceAndReturnNormalized()
    {
        var src = """
            unsafe class C
            {
                private char[] bufChars = new char[64];
                private int bufPos;

                protected unsafe char* WriteNewLine(char* pDst)
                {
                    fixed (char* pDstBegin = bufChars)
                    {
                        bufPos = (int)(pDst - pDstBegin);
                        return pDstBegin + bufPos;
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains("int WriteNewLine(char[] pDst, int pDstIndex)", result.OutputCode);
        Assert.Contains("bufPos = pDstIndex - pDstBegin;", result.OutputCode);
        Assert.Contains("return pDstBegin + bufPos;", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerDifferences);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var second = Rewrite(result.OutputCode);
        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void Uf020b_SameFamilyFixedCursor_ForwardsToPairedCallee_ExpandedWithArrayAndIndex()
    {
        var src = """
            unsafe class C
            {
                private char[] bufChars = new char[64];
                private int bufPos;

                protected unsafe char* WriteNewLine(char* pDst)
                {
                    fixed (char* pDstBegin = bufChars)
                    {
                        bufPos = (int)(pDst - pDstBegin);
                        return pDstBegin + bufPos;
                    }
                }

                private void Append()
                {
                    fixed (char* pDstBegin = bufChars)
                    {
                        char* pDst = pDstBegin + bufPos;
                        pDst = WriteNewLine(pDst);
                        bufPos = (int)(pDst - pDstBegin);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        // fixed 游标外发给配对被调方：展开为「伴生数组 + 当前 int 索引」。
        // 同类两个 fixed pDstBegin 帧的伴生数组按声明顺序消歧（_2 后缀确定）。
        Assert.Contains("pDst = WriteNewLine(_pDstBegin_array_2, pDst);", result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_DifferentFamilyParams_BeginEndNames_ComparisonRejected_UF020()
    {
        var src = """
            unsafe class C
            {
                static void M(char* pSrcBegin, char* pDstEnd)
                {
                    System.Console.Write(pSrcBegin < pDstEnd);
                }
            }
            """;

        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void Uf020b_ParamAndCrossFamilyFixedCursor_DifferenceRejected_UF020()
    {
        var src = """
            unsafe class C
            {
                private static char[] bufChars = new char[8];

                static void M(char* pSrc)
                {
                    fixed (char* pDstBegin = bufChars)
                    {
                        int distance = (int)(pSrc - pDstBegin);
                        System.Console.Write(distance);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
    }

    [Fact]
    public void Uf020b_ParamOffsetArgument_ForwardedToExtern_NormalizedToIndexArgument()
    {
        var src = """
            unsafe class C
            {
                static int EncodeAt(char* pInput, int next)
                {
                    byte[] encodedBytes = new byte[8];
                    fixed (byte* pEncodedBytes = &encodedBytes[0])
                    {
                        return System.Text.Encoding.UTF8.GetBytes(
                            pInput + next, 1, pEncodedBytes, encodedBytes.Length);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains("int EncodeAt(char[] pInput, int pInputIndex, int next)", result.OutputCode);
        Assert.Contains("System.Text.Encoding.UTF8.GetBytes(", result.OutputCode);
        Assert.Contains("pInputIndex + next, 1, pEncodedBytes, encodedBytes.Length",
            result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);

        var second = Rewrite(result.OutputCode);
        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void Uf020b_BareAliasOffsetArgument_ClosureReachesParam_NormalizedToIndexArgument()
    {
        var src = """
            unsafe class C
            {
                static int EncodeAt(char* pInput)
                {
                    char* cursor = pInput;
                    byte[] encodedBytes = new byte[8];
                    fixed (byte* pEncodedBytes = &encodedBytes[0])
                    {
                        return System.Text.Encoding.UTF8.GetBytes(
                            cursor + 1, 1, pEncodedBytes, encodedBytes.Length);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        Assert.Contains("int EncodeAt(char[] pInput, int pInputIndex)", result.OutputCode);
        Assert.Contains("int cursor = pInputIndex;", result.OutputCode);
        Assert.Contains("cursor + 1, 1, pEncodedBytes, encodedBytes.Length",
            result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
    }

    [Fact]
    public void Uf020b_BareParamArgument_NotPaired_ArrayOnlyShapeKept()
    {
        var src = """
            unsafe class C
            {
                static int Count(char* p)
                {
                    return System.Text.Encoding.UTF8.GetByteCount(p, 3);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics));
        // 裸形参外发可整组数组传递：array-only 即够，不插入配对 int 索引。
        Assert.Contains("static int Count(char[] p)", result.OutputCode);
        Assert.DoesNotContain("pIndex", result.OutputCode);
        Assert.Contains("GetByteCount(p, 3)", result.OutputCode);
    }

    [Fact]
    public void StackAlloc_SpanOnlyMembers_SliceFillClear_RetainsSpanTarget()
    {
        var src = """
            class C
            {
                static int M(int n)
                {
                    System.Span<char> buf = stackalloc char[n];
                    buf.Fill('x');
                    System.Span<char> tail = buf.Slice(1);
                    tail.Clear();
                    return buf.Length;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        // Span 专有成员（Fill/Slice/Clear）要求目标保留 Span<T>；堆化只换分配源。
        Assert.Contains("System.Span<char> buf = new char[n]", result.OutputCode);
        Assert.Contains(".Fill('x')", result.OutputCode);
        Assert.Contains(".Slice(1)", result.OutputCode);
        Assert.Contains(".Clear()", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_ReinterpretStorage_KnownSchemesChain_PackedReadAndCallExpanded()
    {
        // Uri.cs CheckKnownSchemes 链最小复现：调用方 char* 游标经 (long*) 重解释
        // 强转传给被调，被调 long* 形参实存 char[]，体内只读前缀解引用链。
        var src = """
            unsafe class C
            {
                static bool Known(long* lptr, ushort n)
                {
                    const long m0 = 'h' | (('t' << 0));
                    if (*lptr == m0) return true;
                    if (*(char*)(lptr + 1) == 's') return true;
                    if (*(int*)(lptr + 1) == 2) return true;
                    if (*(lptr + 1) == 2) return false;
                    return false;
                }

                static ushort Parse(char* uriString, ushort length)
                {
                    ushort idx = 1;
                    ushort end = 5;
                    if (Known((long*)(uriString + idx), (ushort)(end - idx)))
                        return end;
                    char* schemePtr = stackalloc char[end - idx];
                    schemePtr[0] = uriString[idx];
                    return 0;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        // 被调形参按重解释存储元素 char 钉定：long* → char[] + int 索引。
        Assert.Contains("static bool Known(char[] lptr, int lptrIndex, ushort n)", result.OutputCode);
        // *lptr 小端打包读（char[] 4 元拼 long）。
        Assert.Contains("(long)((ulong)lptr[lptrIndex]", result.OutputCode);
        // *(char*)(lptr+1)：存储单位偏移 4（long = 4×char）的窄读。
        Assert.Contains("lptr[lptrIndex + 4]", result.OutputCode);
        // *(int*)(lptr+1)：两存储单位拼 int。
        Assert.Contains("lptr[lptrIndex + 5] << 16", result.OutputCode);
        // 调用点剥除重解释强转，按「数组 + 存储单位索引」展开。
        Assert.Contains("Known(uriString, uriStringIndex + idx, (ushort)(end - idx))", result.OutputCode);
        // 非常量长度 stackalloc → 伴生堆数组 + 同名 int 索引（UF040 降级）。
        Assert.Contains("char[] _schemePtr_array = new char[end - idx]", result.OutputCode);
        Assert.Contains("int schemePtr = 0", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020b_ReinterpretStorage_AcrossFiles_SessionCatalogClosesBothSides()
    {
        // 声明点（文件2）与调用点（文件1）分处两文件：共享 compilation + 共享
        // preprocessor 序列改写，会话级 catalog 闭包必须给出两侧一致的配对形态。
        var caller = """
            unsafe class Caller
            {
                public static ushort Parse(char* uriString, ushort length)
                {
                    ushort idx = 1;
                    ushort end = 5;
                    if (Callee.Known((long*)(uriString + idx), (ushort)(end - idx)))
                        return end;
                    char* schemePtr = stackalloc char[end - idx];
                    schemePtr[0] = uriString[idx];
                    return 0;
                }
            }
            """;
        var callee = """
            unsafe class Callee
            {
                public static bool Known(long* lptr, ushort n)
                {
                    if (*lptr == 0L) return true;
                    if (*(char*)(lptr + 1) == 's') return true;
                    if (*(int*)(lptr + 1) == 2) return false;
                    return false;
                }
            }
            """;

        var tree1 = CSharpSyntaxTree.ParseText(caller, ParseOptions);
        var tree2 = CSharpSyntaxTree.ParseText(callee, ParseOptions);
        var compilation = CSharpCompilation.Create("uffm-reinterpret-multi",
            new[] { tree1, tree2 }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new UnsafeFfmPreprocessor();

        var r1 = preprocessor.RewriteFile(caller, compilation.GetSemanticModel(tree1));
        var r2 = preprocessor.RewriteFile(callee, compilation.GetSemanticModel(tree2));

        Assert.False(r1.HasErrors, string.Join("\n", r1.Diagnostics));
        Assert.False(r2.HasErrors, string.Join("\n", r2.Diagnostics));
        Assert.Contains("static bool Known(char[] lptr, int lptrIndex, ushort n)", r2.OutputCode);
        Assert.Contains("lptr[lptrIndex + 4]", r2.OutputCode);
        Assert.Contains("Callee.Known(uriString, uriStringIndex + idx, (ushort)(end - idx))", r1.OutputCode);
        Assert.Contains("char[] _schemePtr_array = new char[end - idx]", r1.OutputCode);

        // 两文件合在一起可编译。
        var verify = CSharpCompilation.Create("uffm-reinterpret-multi-verify",
            new[]
            {
                CSharpSyntaxTree.ParseText(r1.OutputCode, ParseOptions),
                CSharpSyntaxTree.ParseText(r2.OutputCode, ParseOptions),
            },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = verify.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    // ---- UF020 第三子族：string 实参传配对指针形参方法的伴生数组来源 ----
    // C# 的 string 实参在受限/退化重载解析下可能落到配对（char[] + int）指针形参
    // 位（string 重载内部 fixed 转发、或候选符号回退）。string 本身即不可变
    // char 序列，与 R3 fixed(string) 的既有物化约定一致：物化为
    // `expr.ToCharArray()` + 元素索引 0，语义逐位等价（只读使用）。

    [Fact]
    public void Uf020c_StringArgument_ToPairedPointerMethod_MaterializedToCharArray()
    {
        var src = """
            unsafe class C
            {
                private string _string = "";

                static char[] Unescape(char* pStr, int start, char[] dest)
                {
                    char* q = pStr + start;
                    dest[0] = q[0];
                    return dest;
                }

                unsafe void M(char[] dest)
                {
                    Unescape(_string, 0, dest);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(0, result.Statistics.Uf020Rejections);
        Assert.Contains("char[] Unescape(char[] pStr, int pStrIndex, int start, char[] dest)",
            result.OutputCode);
        // string 实参物化为伴生 char[] + 零索引。
        Assert.Contains("Unescape(_string.ToCharArray(), 0, 0, dest)", result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var second = Rewrite(result.OutputCode);
        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void Uf020c_FieldLocalAndParameterStrings_AllMaterializedPerSlot()
    {
        var src = """
            unsafe class C
            {
                private string _field = "";

                static char[] Join(char* a, char* b, char[] dest)
                {
                    char* q = a + 1;
                    dest[0] = q[0];
                    dest[1] = b[0];
                    return dest;
                }

                unsafe void M(string paramStr, char[] dest)
                {
                    string localStr = "";
                    Join(_field, localStr, dest);
                    Join(paramStr, localStr, dest);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(0, result.Statistics.Uf020Rejections);
        Assert.Contains("char[] Join(char[] a, int aIndex, char[] b, int bIndex, char[] dest)",
            result.OutputCode);
        // 字段/局部/形参三类 string 来源各自物化；多个配对形参同现逐位展开。
        Assert.Contains("Join(_field.ToCharArray(), 0, localStr.ToCharArray(), 0, dest)",
            result.OutputCode);
        Assert.Contains("Join(paramStr.ToCharArray(), 0, localStr.ToCharArray(), 0, dest)",
            result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020c_StringMemberAccess_ToPairedPointerMethod_Materialized()
    {
        var src = """
            unsafe class C
            {
                private string _string = "";

                static char[] Read(char* pStr, char[] dest)
                {
                    char* q = pStr + 1;
                    dest[0] = q[0];
                    return dest;
                }

                unsafe void M(char[] dest)
                {
                    Read(this._string, dest);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Contains("Read(this._string.ToCharArray(), 0, dest)", result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uf020c_UnprovablePointerArgument_StillRejected_UF020()
    {
        // 整数位重解释指针 (char*)x 无可证数组来源：保持 UF020 判决，文件回滚。
        var src = """
            unsafe class C
            {
                static char[] Read(char* p, char[] dest)
                {
                    char* q = p + 1;
                    dest[0] = q[0];
                    return dest;
                }

                unsafe void M(int x, char[] dest)
                {
                    Read((char*)x, dest);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
        Assert.Equal(1, result.Statistics.Uf020Rejections);
    }
}
