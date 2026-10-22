using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// 类型级 [DebuggerDisplay]/[DebuggerTypeProxy] → 运行时可见注解 @DebuggerDisplay/
/// @DebuggerTypeProxy 的发射回归测试。移植的 System.Diagnostics.DebuggerAttributes
/// 测试工具经 compat TypeInfo.getCustomAttributes 反射桥断言"恰好一个"，注解缺失时
/// 抛 IllegalStateException("Expected one DebuggerDisplayAttribute on ...")。
/// </summary>
public class DebuggerMetadataAnnotationTests
{
    private const string TestSource = """
        using System.Diagnostics;

        namespace Samples
        {
            internal sealed class CollectionProxy<T>
            {
                private readonly T[] _items;
                public CollectionProxy(T[] items) => _items = items;
                [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                public T[] Items => _items;
            }

            [DebuggerDisplay("Count = {Count}")]
            [DebuggerTypeProxy(typeof(CollectionProxy<int>))]
            public class WidgetBag
            {
                public int Count => 3;
            }

            [DebuggerDisplay("Top = {_top}")]
            internal struct WidgetBuilder
            {
                private int _top;
            }
        }
        """;

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
    public async Task DebuggerDisplay_EmitsRuntimeAnnotation()
    {
        var code = await ConvertAsync(TestSource);

        Assert.Contains("@DebuggerDisplay(\"Count = {Count}\")", code);
        Assert.Contains("io.github.ningpp.compat.DebuggerDisplay", code);
    }

    [Fact]
    public async Task DebuggerTypeProxy_EmitsRawClassLiteral()
    {
        var code = await ConvertAsync(TestSource);

        // Java 注解值必须是编译期常量：typeof(CollectionProxy<int>) 落成泛型定义裸类名。
        Assert.Contains("@DebuggerTypeProxy(CollectionProxy.class)", code);
        Assert.Contains("io.github.ningpp.compat.DebuggerTypeProxy", code);
    }

    [Fact]
    public async Task Struct_CarriesDebuggerDisplayAnnotation()
    {
        var code = await ConvertAsync(TestSource);

        Assert.Contains("@DebuggerDisplay(\"Top = {_top}\")", code);
    }
}
