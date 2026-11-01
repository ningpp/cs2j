using CSharpToJava.Core.LocalFunctionLift;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C05 局部函数提升：同一宿主类型内重名局部函数 + 调用点符号绑定失败时，
/// 不能按名字回退（会把不同签名的局部函数张冠李戴）。真实 SDK：json 的
/// JsonSerializerOptions 在两个方法里都声明局部函数 Add（Dictionary /
/// ConcurrentDictionary 捕获）；整树编译把 regular-expressions 工程的
/// global using System.ComponentModel 泄漏进 json 文件，使
/// new BooleanConverter() 二义（CS0104），Add(...) 重载解析失败，18 个
/// Dictionary 调用被改写为 ConcurrentDictionary 版本 → build CS1503。
/// 规则：整组保守跳过，保留局部函数与调用点原样。
/// </summary>
public class LocalFunctionLiftAmbiguousNameTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private const string MainSource = """
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using Demo.Converters;

        namespace Demo
        {
            public class Options
            {
                public static Dictionary<System.Type, object> M()
                {
                    var simple = new Dictionary<System.Type, object>();
                    void Add(object converter) => simple.Add(converter.GetType(), converter);

                    Add(new BooleanConverter());
                    return simple;
                }

                public static ConcurrentDictionary<System.Type, object> N()
                {
                    var concurrent = new ConcurrentDictionary<System.Type, object>();
                    void Add(object converter) => concurrent[converter.GetType()] = converter;

                    Add(new BooleanConverter());
                    return concurrent;
                }
            }
        }

        namespace Demo.Converters
        {
            public class BooleanConverter { }
        }
        """;

    private static LocalFunctionLiftResult Lift(string? leakSource)
    {
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(MainSource, ParseOptions, path: "main.cs"),
        };
        if (leakSource != null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(leakSource, ParseOptions, path: "other.cs"));
        }

        var compilation = CSharpCompilation.Create(
            "local-fn-ambiguous",
            trees,
            CSharpToJava.CLI.ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var mainTree = trees[0];

        return new LocalFunctionLiftPreprocessor().Preprocess(
            MainSource, compilation.GetSemanticModel(mainTree));
    }

    [Fact]
    public void Bound_Call_Sites_Lift_Distinct_Duplicate_Names()
    {
        var result = Lift(null);

        Assert.True(result.Changed);
        Assert.Contains("add(simple, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        Assert.Contains("add2(concurrent, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unbound_Duplicate_Name_Call_Sites_Skip_Whole_Group()
    {
        // 另一个工程的 global using 泄漏进同一整树编译 → BooleanConverter 二义。
        var result = Lift("global using System.ComponentModel;");

        Assert.False(result.Changed);
        Assert.Equal(MainSource, result.OutputCode);

        var ambiguous = result.Diagnostics
            .Where(d => d.Reason == LocalFunctionSkipReason.AmbiguousName)
            .ToList();
        Assert.Equal(2, ambiguous.Count);
        Assert.Equal(2, result.Statistics.LocalFunctionsSkipped);
    }
}
