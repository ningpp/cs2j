using System.IO;
using CSharpToJava.Core.IteratorLowering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.IteratorLowering;

/// <summary>
/// 归属回归：async 迭代器（async IAsyncEnumerable&lt;T&gt;/
/// IAsyncEnumerator&lt;T&gt;）属于后续 async-normalize（C18/C38），
/// iterator-lowering 必须原样跳过，不得因无法解析同步元素类型而报
/// CS2J0401 阻断整条链。rt10 画像：System.Text.Json 的
/// createAsyncEnumerableFromArray / GetAsyncEnumerator。
/// </summary>
public sealed class AsyncIteratorOwnershipTests
{
    [Fact]
    public void AsyncEnumerableIterator_IsNotReportedAndNotRewritten()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace Lib
            {
                public class C
                {
                    public static async IAsyncEnumerable<T?> CreateAsyncEnumerableFromArray<T>(T[] values)
                    {
                        await Task.Yield();
                        foreach (T item in values)
                        {
                            yield return item;
                        }
                    }
                }
            }
            """;

        var result = Preprocess(source, verifyInputCompiles: true);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == IteratorLoweringSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == IteratorLoweringDiagnosticCode.CS2J0401);
    }

    [Fact]
    public void AsyncEnumeratorIterator_IsNotReportedAndNotRewritten()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Lib
            {
                public class Converter<TElement>
                {
                    public async IAsyncEnumerator<TElement> GetAsyncEnumerator(CancellationToken _)
                    {
                        await Task.Yield();
                        yield return default!;
                    }
                }
            }
            """;

        var result = Preprocess(source, verifyInputCompiles: true);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == IteratorLoweringSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == IteratorLoweringDiagnosticCode.CS2J0401);
    }

    [Fact]
    public void SyncEnumerableIterator_IsStillLowered()
    {
        const string source = """
            using System.Collections.Generic;

            namespace Lib
            {
                public class C
                {
                    public IEnumerable<int> Numbers()
                    {
                        yield return 1;
                        yield return 2;
                    }
                }
            }
            """;

        var result = Preprocess(source, verifyInputCompiles: true);

        Assert.True(result.Changed);
        Assert.DoesNotContain("yield return", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == IteratorLoweringSeverity.Error);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var verify = CSharpCompilation.Create(
            "il-async-sync-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(result.OutputCode, parseOptions) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = verify.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + result.OutputCode);
    }

    [Fact]
    public void NonAsyncAsyncEnumerableShape_IsSkippedWithoutCs2j0401()
    {
        // 非法 C#（非 async 方法带 yield 却返回 IAsyncEnumerable<int>，CS1624），
        // 但它是 async 流形态：按返回类型识别后同样跳过，不产生 CS2J0401。
        const string source = """
            using System.Collections.Generic;

            namespace Lib
            {
                public class C
                {
                    public IAsyncEnumerable<int> Numbers()
                    {
                        yield return 1;
                    }
                }
            }
            """;

        var result = Preprocess(source, verifyInputCompiles: false);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == IteratorLoweringSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == IteratorLoweringDiagnosticCode.CS2J0401);
    }

    private static IteratorLoweringFileResult Preprocess(string source, bool verifyInputCompiles)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "il-async-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        if (verifyInputCompiles)
        {
            var inputErrors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            Assert.True(inputErrors.Count == 0,
                "test input failed to compile:\n" + string.Join("\n", inputErrors));
        }

        var preprocessor = new IteratorLoweringPreprocessor();
        return preprocessor.Preprocess(
            source, compilation.GetSemanticModel(tree), new IteratorLoweringOptions());
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
}
