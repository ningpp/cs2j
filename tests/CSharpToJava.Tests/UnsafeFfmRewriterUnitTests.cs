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

        // M1 起 EnableFixed 默认开启会接管 fixed 构造；本用例意图验证 M0/R1
        // 面不处理普通指针形参，显式关 EnableFixed 保持 M0 语义。
        var result = Rewrite(src, new UnsafeFfmOptions { EnableFixed = false });

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
    public void StackAlloc_BarePointerTarget_Rejected_UF040_OriginalPreserved()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    int* p = stackalloc int[8];
                    System.Console.Write(p[0]);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF040);
        Assert.Equal(1, result.Statistics.Uf040Rejections);
        // 原文回滚。
        Assert.Contains("int* p = stackalloc int[8];", result.OutputCode);
    }

    [Fact]
    public void StackAlloc_VarTarget_IsPointerSite_Rejected_UF040()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    var x = stackalloc int[4];
                    System.Console.Write(x[0]);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF040);
        Assert.Equal(1, result.Statistics.Uf040Rejections);
        Assert.Contains("var x = stackalloc int[4];", result.OutputCode);
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
}
