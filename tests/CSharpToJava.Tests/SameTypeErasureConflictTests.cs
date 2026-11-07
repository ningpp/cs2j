using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Transformers.Member;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests;

public class SameTypeErasureConflictTests
{

    [Fact]
    public async Task SameNameNestedDelegates_DifferentArity_BothSurviveWithRename()
    {
        // JsonClassInfo declares two delegates named ParameterizedConstructorDelegate
        // with different type-parameter counts. Java forbids two nested types with
        // the same simple name, so the later arity must be renamed (Name_arity) at
        // both the declaration and all reference sites.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-delegate-arity-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Sample.cs"), @"
class Sample
{
    public delegate T Del<T>(object[] args);
    public delegate T Del<T, TArg0, TArg1>(TArg0 arg0, TArg1 arg1);

    public Del<T> Create<T>() => null;
    public Del<T, TArg0, TArg1> Create2<T, TArg0, TArg1>() => null;
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);
            var sample = Assert.Single(results);
            Assert.True(sample.Success, string.Join("\n", sample.Diagnostics));

            var code = sample.GeneratedCode;

            // Both delegate interfaces must be emitted with distinct names.
            Assert.Contains("interface Del<T>", code);
            Assert.Contains("interface Del_3<T, TArg0, TArg1>", code);

            // The renamed delegate must be referenced under its Java name.
            Assert.Contains("Del_3<T, TArg0, TArg1> create2", code);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// 调用点可能属于另一个单独转换的工程（新 ConversionContext、空改名注册表），
    /// 此时只能靠符号推导后缀。后缀必须按（Java 名，擦除签名）分组各自从 2 计起：
    /// 另一组签名占用 <c>M_2</c> 不能把本组推到 <c>M_3</c>，否则声明（M_2）与
    /// 调用点（M_3）不一致。
    /// </summary>
    [Fact]
    public void PredictionPerGroup_ParallelSignaturesBothGetSuffixTwo()
    {
        const string source = """
            using System.Collections.Generic;

            class Sample
            {
                void M(List<string> a) { }
                void M(Dictionary<string, string> a) { }
                void M(Dictionary<int, int> a) { }
                void M(List<int> a) { }
            }
            """;

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var method = GetDeclaredMethod(source, options, "M", "List<int>");
        Assert.NotNull(method);

        var context = CreateContext(options);
        Assert.False(context.TryGetErasureConflictMethodName(method!, out _));
        Assert.True(MethodTransformer.TryPredictErasureConflictName(method!, context, out var predicted));
        Assert.Equal("m_2", predicted);
    }

    /// <summary>
    /// 显式类型实参的泛型调用绑定到「构造后」的符号（Serialize&lt;object&gt;），
    /// 其擦除签名仍必须按 Object 归类，调用点才会改名为声明侧的 serialize_2；
    /// 否则序列化入口会递归回未改名的同名 Object 重载（真实链 JsonSerializer）。
    /// </summary>
    [Fact]
    public void ConstructedGenericCall_BindsToRenamedErasureSibling()
    {
        const string source = """
            using System;

            class Sample
            {
                public static string Serialize(object value, Type inputType, string options = null)
                {
                    return Serialize<object>(value, inputType, options);
                }

                private static string Serialize<T>(in T value, Type inputType, string options)
                {
                    return "";
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = source,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        Assert.Contains("String serialize_2(", code, StringComparison.Ordinal);
        Assert.Contains("return serialize_2(value, inputType, options);", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同名的多个擦除组（3 参 Object 重载 + 4 参 writer 重载）都要在调用点绑定到
    /// 各自的 _2 改名：预扫描（PrepareErasureConflictRenames）与声明侧的签名必须
    /// 都把泛型类型参数擦成 Object，否则某一组漏注册，调用点改错名（真实链
    /// JsonSerializer.Serialize(Utf8JsonWriter, object, Type, options)）。
    /// </summary>
    [Fact]
    public void MultipleErasureGroups_BindCallSitesToTheirRenamedSibling()
    {
        const string source = """
            using System;

            class Utf8JsonWriter { }

            class Sample
            {
                public static string Serialize(object value, Type inputType, string options = null)
                {
                    return Serialize<object>(value, inputType, options);
                }

                public static void Serialize(Utf8JsonWriter writer, object value, Type inputType, string options = null)
                {
                    Serialize<object>(writer, value, inputType, options);
                }

                private static string Serialize<T>(in T value, Type inputType, string options)
                {
                    return "";
                }

                private static void Serialize<T>(Utf8JsonWriter writer, in T value, Type inputType, string options)
                {
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = source,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        Assert.Contains("return serialize_2(value, inputType, options);", code, StringComparison.Ordinal);
        Assert.Contains("serialize_2(writer, value, inputType, options);", code, StringComparison.Ordinal);
    }

    private static ConversionContext CreateContext(ConversionOptions options)
        => new(options, new TypeMappingRegistry(options.TypeMappingConfigPath));

    private static IMethodSymbol? GetDeclaredMethod(
        string source, ConversionOptions options, string methodName, string parameterList)
    {
        var context = CreateContext(options);
        var compilation = ProjectCompilationBuilder.BuildCompilation(
            new[] { new SourceFile { FilePath = "Probe.cs", Content = source } }, context);
        Assert.NotNull(compilation);

        var tree = compilation!.SyntaxTrees.Single(t => t.FilePath == "Probe.cs");
        var model = compilation.GetSemanticModel(tree);
        var declaration = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == methodName
                         && m.ParameterList.ToString() == $"({parameterList} a)");
        return model.GetDeclaredSymbol(declaration) as IMethodSymbol;
    }
}
