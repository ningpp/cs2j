using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Compat 类型导入/映射缺失的回归测试：既有的 compat 运行时类
/// (io.github.ningpp.compat.JavaScriptEncoder/SequencePosition/
/// ConcurrentStack/ConcurrentQueue/ResourceManager) 必须经 TypeMappings 登记，
/// 否则生成文件只剩裸简名（javac: 找不到符号 类 X），或退化为不存在的
/// dotnet.system.* 包引用。
/// </summary>
public class CompatTypeImportTests
{
    private readonly ITestOutputHelper _out;

    public CompatTypeImportTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task JavaScriptEncoderField_EmitsCompatImport()
    {
        var result = await ConvertAsync("""
            using System.Text.Encodings.Web;

            namespace Demo
            {
                public class JsonWriterOptions
                {
                    public JavaScriptEncoder Encoder;
                }
            }
            """);

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("io.github.ningpp.compat.JavaScriptEncoder", code);
        Assert.Contains("public JavaScriptEncoder Encoder;", code);
    }

    [Fact]
    public async Task SequencePosition_UsesCompatType()
    {
        var result = await ConvertAsync("""
            using System;

            namespace Demo
            {
                public class ReaderState
                {
                    public SequencePosition Position;

                    public void Reset()
                    {
                        SequencePosition p = default;
                    }
                }
            }
            """);

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.DoesNotContain("dotnet.system.SequencePosition", code);
        Assert.Contains("io.github.ningpp.compat.SequencePosition", code);
        Assert.Contains("public SequencePosition Position;", code);
        Assert.Contains("SequencePosition p", code);
    }

    [Fact]
    public async Task ConcurrentQueue_UsesCompatTypeAndMethodNames()
    {
        var result = await ConvertAsync("""
            using System.Collections.Concurrent;

            namespace Demo
            {
                public class Test
                {
                    public void Run()
                    {
                        ConcurrentQueue<int> q = new ConcurrentQueue<int>();
                        q.Enqueue(1);
                    }
                }
            }
            """);

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("io.github.ningpp.compat.ConcurrentQueue", code);
        Assert.Contains("ConcurrentQueue<Integer> q", code);
        Assert.Contains("q.enqueue(1)", code);
    }

    [Fact]
    public async Task ConcurrentStack_UsesCompatType()
    {
        var result = await ConvertAsync("""
            using System.Collections.Concurrent;

            namespace Demo
            {
                public class Test
                {
                    public void Run()
                    {
                        ConcurrentStack<int> s = new ConcurrentStack<int>();
                        s.Push(1);
                    }
                }
            }
            """);

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("io.github.ningpp.compat.ConcurrentStack", code);
        Assert.Contains("ConcurrentStack<Integer> s", code);
        Assert.Contains("s.push(1)", code);
    }

    [Fact]
    public async Task ResourceManager_UsesCompatType()
    {
        var result = await ConvertAsync("""
            using System.Resources;

            namespace Demo
            {
                public class Holder
                {
                    public ResourceManager Manager;
                }
            }
            """);

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("io.github.ningpp.compat.ResourceManager", code);
        Assert.Contains("public ResourceManager Manager;", code);
    }

    [Fact]
    public async Task DynamicallyAccessedMemberTypes_MapsToIntConstants()
    {
        var result = await ConvertAsync("""
            using System.Diagnostics.CodeAnalysis;

            namespace Demo
            {
                public class JsonSerializer
                {
                    private const DynamicallyAccessedMemberTypes MembersAccessedOnRead =
                        DynamicallyAccessedMemberTypes.PublicConstructors
                        | DynamicallyAccessedMemberTypes.PublicProperties
                        | DynamicallyAccessedMemberTypes.PublicFields;
                }
            }
            """);

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.DoesNotContain("DynamicallyAccessedMemberTypes.values()", code);
        Assert.DoesNotContain("DynamicallyAccessedMemberTypes", code);
        Assert.Contains("int MembersAccessedOnRead = 547", code);
    }

    private static async Task<ConversionResult> ConvertAsync(string source)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(
            new[] { new SourceFile { FilePath = "Test.cs", Content = source } },
            projectName: "Demo");
        return Assert.Single(results);
    }
}
