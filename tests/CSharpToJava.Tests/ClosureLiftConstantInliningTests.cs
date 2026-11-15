using CSharpToJava.Core.ClosureLift;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C45 回归：外层 <c>const</c> 局部是编译期常量，C# 闭包不捕获它
/// （CaptureClassification 有意跳过 HasConstantValue）。lambda 体迁入 display
/// class 的 <c>Lambda_k</c> 方法或宿主级 static 命名方法后，常量名字不再在
/// 作用域内——真实链 p43 json 测试 CS0103（CacheTests.TestClassCount、
/// JsonDocumentTests.ThrowsAnyway）。迁移时必须按值内联成字面量。
/// </summary>
public class ClosureLiftConstantInliningTests
{
    private const string InstanceSource = """
        using System;
        using System.Threading.Tasks;
        class M
        {
            void Run(object[] items)
            {
                const int N = 2;
                const string S = "s";
                Task.Run(() => Use(items, N));
                Task.Run(() => Use(items, S));
                Task.Run(() => Use(items, N - 1));
            }

            static void Use(object[] items, int value) { }
            static void Use(object[] items, string value) { }
        }
        """;

    [Fact]
    public void DisplayClassMethods_InlineOuterConstLocals()
    {
        var pre = new ClosureLiftPreprocessor();
        var result = pre.Preprocess(InstanceSource);
        Assert.True(result.Changed);
        var code = result.OutputCode;

        Assert.Contains("class Cs2jDisplayClass_Run_0", code, StringComparison.Ordinal);

        // 迁入 display class 的方法里出现字面量，不再出现裸常量标识符。
        Assert.Contains("internal void Lambda_0() => Use(_cs2jCap_items, 2);", code, StringComparison.Ordinal);
        Assert.Contains("internal void Lambda_1() => Use(_cs2jCap_items, \"s\");", code, StringComparison.Ordinal);
        Assert.Contains("internal void Lambda_2() => Use(_cs2jCap_items, 2 - 1);", code, StringComparison.Ordinal);
        Assert.DoesNotContain(", N)", code, StringComparison.Ordinal);
        Assert.DoesNotContain(", S)", code, StringComparison.Ordinal);
    }

    private const string StaticSource = """
        using System;
        enum Mode
        {
            A = 0,
            B = 1,
        }
        class M
        {
            void Run()
            {
                const Mode Selected = Mode.B;
                Action a = () => Use(Selected);
                a();
            }

            static void Use(Mode value) { }
        }
        """;

    [Fact]
    public void LiftedStaticMethod_InlinesEnumConstLocalAsCastLiteral()
    {
        var pre = new ClosureLiftPreprocessor();
        var result = pre.Preprocess(StaticSource);
        Assert.True(result.Changed);
        var code = result.OutputCode;

        Assert.Contains("Cs2jLambda_Run_0", code, StringComparison.Ordinal);
        // 枚举常量渲染为 (EnumType)(underlying)，裸整数字面量对枚举不成立。
        Assert.Contains("Use((global::Mode)(1))", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Use(Selected)", code, StringComparison.Ordinal);
    }
}
