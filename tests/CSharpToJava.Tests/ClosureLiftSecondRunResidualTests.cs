using System.Text.RegularExpressions;
using CSharpToJava.Core.ClosureLift;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// 二次 closure-lift 回归（真实链：linq-channel 的 LiftGeneratedClosures 会对含
/// `yield` 的文件整树重跑 closure-lift）。首轮已处理文件里残留的捕获 lambda
/// 在二次运行中会重新规划，必须满足两条：① 访问既有 `__cs2jClosureN` 字段的
/// lambda 不再是提升候选（否则新类里没有这些字段 → javac 找不到符号）；
/// ② 新组不得复用输入里已存在的 display class 名（否则出现两个同名嵌套类，
/// 下游合并后构造器多出 `_cs2jThis` 实参 → 旧构造点失配）。
/// </summary>
public class ClosureLiftSecondRunResidualTests
{
    [Fact]
    public void LambdaReferencingExistingDisplayClass_StaysInline()
    {
        var source = """
            using System;

            class C
            {
                private int Serializer => 1;

                void M()
                {
                    var __cs2jClosure0 = new Cs2jDisplayClass_M_0();
                    __cs2jClosure0._cs2jCap_json = "1";
                    Run(() => Console.WriteLine(Serializer + __cs2jClosure0._cs2jCap_json));
                }

                void Run(Action a) { }

                private sealed class Cs2jDisplayClass_M_0
                {
                    public string _cs2jCap_json;
                }
            }
            """;

        var result = new ClosureLiftPreprocessor().Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(1, Regex.Matches(result.OutputCode, @"class Cs2jDisplayClass_M_0\b").Count);
        Assert.DoesNotContain("Lambda_", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NewGroupInAlreadyLiftedFile_GetsFreshClassName()
    {
        var source = """
            using System;

            class C
            {
                private int Serializer => 1;

                void M()
                {
                    var __cs2jClosure0 = new Cs2jDisplayClass_M_0();
                    __cs2jClosure0._cs2jCap_json = "1";
                    string invalidJson = "{}";
                    Run(() => Console.WriteLine(Serializer + invalidJson));
                }

                void Run(Action a) { }

                private sealed class Cs2jDisplayClass_M_0
                {
                    public string _cs2jCap_json;
                }
            }
            """;

        var code = new ClosureLiftPreprocessor().Preprocess(source).OutputCode;

        var names = Regex.Matches(code, @"class (Cs2jDisplayClass_\w+)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("Cs2jDisplayClass_M_0", names);
        Assert.Contains("Cs2jDisplayClass_M_1", names);
    }

    [Fact]
    public void FirstRun_CapturingLambda_StillLifted()
    {
        var source = """
            using System;

            class C
            {
                void M()
                {
                    string json = "1";
                    Run(() => { json = "2"; });
                    Run(() => Console.WriteLine(json));
                }

                void Run(Action a) { }
            }
            """;

        var result = new ClosureLiftPreprocessor().Preprocess(source);

        Assert.True(result.Changed);
        Assert.Contains("class Cs2jDisplayClass_M_0", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Lambda_", result.OutputCode, StringComparison.Ordinal);
    }
}
