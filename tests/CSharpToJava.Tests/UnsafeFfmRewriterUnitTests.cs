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

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.UnsafeMethodModifiersStripped);
        Assert.Contains("int* p", result.OutputCode);
        Assert.Contains("*p", result.OutputCode);
        Assert.False(result.HasErrors);
        // 注意：剥除 unsafe 上下文后该文件已不能独立编译——这正是后续里程碑
        // residual 闸门（UF020 等）要接管的构造；M0 不做拒绝也不做转换。
        Assert.DoesNotContain("unsafe", result.OutputCode);
    }
}
