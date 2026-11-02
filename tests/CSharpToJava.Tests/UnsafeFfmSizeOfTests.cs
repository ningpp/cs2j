using System.IO;
using System.Linq;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C06 unsafe-ffm R1：剥除 unsafe 修饰符后，源码中仍依赖 unsafe 编译
/// 许可的 <c>sizeof(非预定义尺寸 struct)</c> 会触发 CS0233。R1 必须把
/// 这类 sizeof 归一为安全上下文可用的
/// <c>System.Runtime.CompilerServices.Unsafe.SizeOf&lt;T&gt;()</c>；
/// 基元/枚举等预定义尺寸类型保持 <c>sizeof</c> 原样。
/// </summary>
public class UnsafeFfmSizeOfTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-sizeof-test",
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

    /// <summary>
    /// 输出代码必须在禁用 unsafe 的编译选项下零错误：这正是 R1 剥除
    /// unsafe 后目标工程应满足的安全上下文。
    /// </summary>
    private static void AssertOutputCompilesWithoutUnsafe(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-sizeof-verify",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile without unsafe:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private const string StructSizeOfSource = """
        class C
        {
            readonly struct Row
            {
                private readonly int A;
                private readonly int B;
                private readonly int C;

                static unsafe Row()
                {
                    System.Diagnostics.Debug.Assert(sizeof(Row) == 12);
                }
            }
        }
        """;

    [Fact]
    public void StructSizeOf_InUnsafeStaticConstructor_RewrittenToSafeSizeOf()
    {
        var result = Rewrite(StructSizeOfSource);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("unsafe", result.OutputCode);
        Assert.Contains(
            "global::System.Runtime.CompilerServices.Unsafe.SizeOf<Row>()",
            result.OutputCode);
        Assert.DoesNotContain("sizeof(Row)", result.OutputCode);
        Assert.Equal(1, result.Statistics.UnsafeSizeOfExpressionsRewritten);
        AssertOutputCompilesWithoutUnsafe(result.OutputCode);
    }

    [Fact]
    public void PredefinedSizeOf_RemainsUnchanged()
    {
        var src = """
            class C
            {
                static unsafe int M() => sizeof(int) * 2;
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("unsafe", result.OutputCode);
        Assert.Contains("sizeof(int)", result.OutputCode);
        Assert.DoesNotContain("Unsafe.SizeOf", result.OutputCode);
        Assert.Equal(0, result.Statistics.UnsafeSizeOfExpressionsRewritten);
        AssertOutputCompilesWithoutUnsafe(result.OutputCode);
    }
}
