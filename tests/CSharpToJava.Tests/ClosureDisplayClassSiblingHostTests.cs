using CSharpToJava.Core.ClosureLift;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C45 M2 回归：同一文件里<b>外层宿主</b>与<b>嵌套宿主</b>的方法各自产生
/// display class 时（真实链 XmlTextReaderImpl：17 个顶层宿主 display class
/// + NodeData.TrimSpacesInValue 的嵌套宿主 display class），嵌套宿主的类声明
/// 丢失——member-spine 挂载先对外层宿主 WithMembers(AddRange(...))，递归随即
/// 走重建后的成员表；Roslyn 重建使未改动的嵌套类型成员拿到全新 red 节点实例，
/// 按节点身份配对的 newClassesByHost/modelHostByReceivedHost 查找静默落空，
/// 嵌套宿主（NodeData）的 display class 声明被丢弃，而站点/实例声明照常发射
/// （`new Cs2jDisplayClass_TrimSpacesInValue_0(this)` 无类声明 → 下游
/// java-keywords CS0246）。挂载递归必须始终走原始成员表。
/// </summary>
public class ClosureDisplayClassSiblingHostTests
{
    private const string Source = """
        using System;
        class ByRef<T>
        {
            public static ByRef<T> Create<T2>(Func<T2> getter, Action<T2> setter) => null;
        }
        class Outer
        {
            private int _x;

            private class NodeData
            {
                private int _valueLength;

                internal void TrimSpacesInValue()
                {
                    var r = ByRef<int>.Create<int>(() => _valueLength, v => _valueLength = v);
                    _ = r;
                }
            }

            internal Func<int> Top()
            {
                return () => _x;
            }
        }
        """;

    [Fact]
    public void DisplayClasses_AreDeclared_ForBothTopLevelAndNestedHosts()
    {
        var pre = new ClosureLiftPreprocessor();
        var result = pre.Preprocess(Source);
        Assert.True(result.Changed);
        var code = result.OutputCode;

        // 两个宿主的站点都重写为 display class 实例。
        Assert.Contains("new Cs2jDisplayClass_TrimSpacesInValue_0(", code, StringComparison.Ordinal);
        Assert.Contains("new Cs2jDisplayClass_Top_0(", code, StringComparison.Ordinal);

        // 两个类声明都必须存在：外层宿主（先挂载、触发成员表重建）之后，
        // 嵌套宿主的声明不得因 red 节点身份失配而丢失。
        Assert.Contains("class Cs2jDisplayClass_TrimSpacesInValue_0", code, StringComparison.Ordinal);
        Assert.Contains("class Cs2jDisplayClass_Top_0", code, StringComparison.Ordinal);
    }
}
