using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Diagnostics.Debug.Fail 桥接回归：Debug.Fail 映射为 compat.Debug.fail
/// 时必须同步补上 io.github.ningpp.compat.Debug 导入，否则生成的 Java
/// 「找不到符号: 变量 Debug」（dotnet/text/regularexpressions/RegexNode.java）。
/// </summary>
public class DebugFailCompatImportTests
{
    private static async Task<string> ConvertAsync(string source)
    {
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[] { new SourceFile { FilePath = "Test.cs", Content = source } },
                projectName: "Demo");
        foreach (var failure in results.Where(r => !r.Success))
            throw new Xunit.Sdk.XunitException(
                string.Join("\n", failure.Diagnostics) + "\n---\n" + failure.GeneratedCode);
        return string.Join("\n", results.Select(r => r.GeneratedCode));
    }

    [Fact]
    public async Task DebugFail_WithUsingDirective_GetsCompatDebugImport()
    {
        var code = await ConvertAsync("""
            using System.Diagnostics;

            public class Test
            {
                public void Run(string name)
                {
                    if (name == null)
                    {
                        Debug.Fail("name is null");
                        return;
                    }
                }
            }
            """);

        Assert.Contains("Debug.fail(", code);
        Assert.Contains("import io.github.ningpp.compat.Debug;", code);
    }

    [Fact]
    public async Task DebugFail_FullyQualified_GetsCompatDebugImport()
    {
        var code = await ConvertAsync("""
            public class Test
            {
                public void Run()
                {
                    System.Diagnostics.Debug.Fail("unreachable");
                }
            }
            """);

        Assert.Contains("Debug.fail(", code);
        Assert.Contains("import io.github.ningpp.compat.Debug;", code);
    }

    [Fact]
    public async Task DebugAssert_IsCommentedOutWithoutCompatReference()
    {
        var code = await ConvertAsync("""
            using System.Diagnostics;

            public class Test
            {
                public void Run(int value)
                {
                    Debug.Assert(value >= 0, "value must be non-negative");
                }
            }
            """);

        // Release 语义：Debug.Assert 降为注释，不产生未绑定的 Debug 引用
        Assert.Contains("// Debug.Assert(", code);
        Assert.DoesNotContain("import io.github.ningpp.compat.Debug;", code);
    }
}
