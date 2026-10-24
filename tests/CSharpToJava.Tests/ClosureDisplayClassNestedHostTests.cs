using CSharpToJava.Core.ClosureLift;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C45 M2 回归：宿主类型为<b>嵌套类型</b>（如 XmlTextReaderImpl.NodeData）时，
/// 捕获 this 的 lambda 迁入的 display class 实例声明与站点重写已发射
/// （`new Cs2jDisplayClass_M_0(this)`），但类声明未挂到嵌套宿主的成员表尾
/// ——真实链 java-keywords 阶段 CS0246（XmlTextReaderImpl.TrimSpacesInValue
/// 的 ByRef&lt;int&gt;.Create getter/setter lambda）。类声明必须与站点重写
/// 同批落地到其嵌套宿主类型。
/// </summary>
public class ClosureDisplayClassNestedHostTests
{
    private const string Source = """
        using System;
        class ByRef<T>
        {
            public static ByRef<T> Create<T2>(Func<T2> getter, Action<T2> setter) => null;
        }
        class Outer
        {
            private class NodeData : IComparable
            {
                private char[] _chars = new char[8];
                private int _valueStartPos;
                private int _valueLength;
                private string _value;

                internal bool ValueBuffered => _value == null;

                internal void TrimSpacesInValue()
                {
                    if (ValueBuffered)
                    {
                        Outer.StripSpaces(_chars, _valueStartPos, ByRef<int>.Create<int>(() => _valueLength, v => _valueLength = v));
                    }
                    else
                    {
                        _value = Outer.StripSpaces(_value);
                    }
                }

                public int CompareTo(object other) => 0;
            }

            static ByRef<int> StripSpaces(char[] chars, int start, ByRef<int> len) => len;
            static string StripSpaces(string value) => value;
        }
        """;

    [Fact]
    public void DisplayClass_IsDeclared_WhenHostTypeIsNested()
    {
        var pre = new ClosureLiftPreprocessor();
        var result = pre.Preprocess(Source);
        Assert.True(result.Changed);
        var code = result.OutputCode;

        // 站点已重写为 display class 实例 + Lambda 方法引用。
        Assert.Contains("new Cs2jDisplayClass_TrimSpacesInValue_0(", code, StringComparison.Ordinal);

        // 类声明必须存在于输出中（嵌套宿主 NodeData 内）。
        Assert.Contains("class Cs2jDisplayClass_TrimSpacesInValue_0", code, StringComparison.Ordinal);
    }
}
