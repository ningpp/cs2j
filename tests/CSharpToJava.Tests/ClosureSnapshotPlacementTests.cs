using CSharpToJava.Core.ClosureLift;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C45 M2 回归：快照锚字典按 CapturedSymbolPlan record 值相等作键，两个组的
/// 同名只读捕获 plan 字段全等——后组查到前组的锚，`__cs2jClosureN._cs2jCap_x`
/// 快照插进未声明该实例的作用域（真实链 java-keywords 阶段 CS0103，
/// ReflectionXmlSerializationReader 10 处）。锚必须按各组自己的 plan 实例
/// （引用相等）记录与查找。
/// </summary>
public class ClosureSnapshotPlacementTests
{
    private const string Source = @"using System;
class M
{
    void Generate(object[] p)
    {
        for (int i = 0; i < 2; i++)
        {
            int index = i;
            Action<object> source = (o) => p[index] = o;
            if (i > 0)
            {
                int indexJ = i;
                Action<object> check = (o) => p[indexJ] = o;
            }
        }
    }
}";

    [Fact]
    public void PerGroupSnapshots_StayInsideTheirInstanceScope()
    {
        var pre = new ClosureLiftPreprocessor();
        var result = pre.Preprocess(Source);
        Assert.True(result.Changed);
        var code = result.OutputCode;

        // 内层组的 p 快照必须随其实例声明一起出现在内层作用域（check 站点之前），
        // 不得提升到外层 source 站点（那里没有 __cs2jClosure1）。
        var innerScope = code[code.IndexOf("__cs2jClosure1 = new", StringComparison.Ordinal)..];
        var inner = innerScope[..innerScope.IndexOf("}", StringComparison.Ordinal)];
        Assert.Contains("__cs2jClosure1._cs2jCap_p = p;", inner, StringComparison.Ordinal);

        // 外层作用域不得引用 __cs2jClosure1。
        var outer = code[..code.IndexOf("__cs2jClosure1 = new", StringComparison.Ordinal)];
        Assert.DoesNotContain("__cs2jClosure1", outer, StringComparison.Ordinal);
    }
}
