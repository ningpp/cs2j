using System;

namespace SampleStringToolsLib;

/// <summary>
/// 字符串相等/拼接工具库。所有相等串均为运行时构造（new string(char[])、Substring），
/// 避开编译期驻留侥幸（设计 R1）。条件链刻意用顺序赋值而非 &amp;&amp;
/// （equality-concat 对非候选二元符 &amp;&amp; 的分类会报 CS2J0470）。
/// </summary>
public static class StringTools
{
    /// <summary>构造串的 string == / != （EC-1）。</summary>
    public static bool ConstructedEquals(string input)
    {
        string s1 = new string(input.ToCharArray());
        string s2 = input.Substring(0);
        bool ok = s1 == input;
        if (ok)
        {
            ok = s1 == s2;
        }
        if (ok)
        {
            ok = !(s1 != s2);
        }
        return ok;
    }

    /// <summary>(object) 逃逸：object 静态类型 == 必须保持引用同一性（EC-2）。</summary>
    public static bool ObjectEscape(string input)
    {
        string s1 = new string(input.ToCharArray());
        string s2 = input.Substring(0);
        if ((object)s1 == s2)
        {
            return false;
        }
        if (!((object)s1 == s1))
        {
            return false;
        }
        object o = s1;
        if (o == input)
        {
            return false;
        }
        return true;
    }

    /// <summary>显式 object.ReferenceEquals 调用（EC-10，形态保持，映射由 Java 半区保证）。</summary>
    public static bool ExplicitReference(string input)
    {
        string s1 = new string(input.ToCharArray());
        string s2 = input.Substring(0);
        bool ok = ReferenceEquals(s1, s1);
        if (ok)
        {
            ok = !ReferenceEquals(s1, s2);
        }
        if (ok)
        {
            ok = object.ReferenceEquals(s1, s1);
        }
        if (ok)
        {
            ok = !object.ReferenceEquals(s1, s2);
        }
        return ok;
    }

    /// <summary>null 拼接：null 操作数替换为空串，绝不出现 "null"。</summary>
    public static bool NullConcat()
    {
        bool ok = null + "a" == "a";
        if (ok)
        {
            ok = "a" + null == "a";
        }
        if (ok)
        {
            ok = (string?)null + (string?)null == "";
        }
        if (ok)
        {
            ok = (string?)null + 'c' == "c";
        }
        if (ok)
        {
            ok = 1 + (string?)null == "1";
        }
        return ok;
    }

    /// <summary>多段 += 链（含 null 段）。</summary>
    public static bool CompoundChain()
    {
        string acc = null!;
        acc += "a";
        acc += null;
        acc += "b";
        acc += "c";
        return acc == "abc";
    }

    /// <summary>混合拼接的值类型文本：Boolean→True/False，Double/Float 去尾 .0。</summary>
    public static bool ValueText()
    {
        bool ok = ("" + true) == "True";
        if (ok)
        {
            ok = (true + "") == "True";
        }
        if (ok)
        {
            ok = (1.0 + "") == "1";
        }
        if (ok)
        {
            ok = ("x" + 1.0) == "x1";
        }
        if (ok)
        {
            ok = (1.0f + "") == "1";
        }
        if (ok)
        {
            ok = ("" + false) == "False";
        }
        return ok;
    }

    /// <summary>属性 += ：setter 恰好一次；getter 在 += 时一次（随后的断言读取是第二次）。</summary>
    public static bool PropertyCompoundCounts()
    {
        var holder = new CountHolder();
        holder.Text += "x";
        string text = holder.Text;
        bool ok = text == "x";
        if (ok)
        {
            ok = holder.GetCount == 2;
        }
        if (ok)
        {
            ok = holder.SetCount == 1;
        }
        return ok;
    }

    /// <summary>不稳定接收者 <c>Get().P += "x"</c>：接收者只求值一次（临时提升）。</summary>
    public static bool UnstableReceiverOnce()
    {
        CountHolder.ReceiverEvaluations = 0;
        GetHolder().Text += "x";
        return CountHolder.ReceiverEvaluations == 1;
    }

    private static CountHolder GetHolder()
    {
        CountHolder.ReceiverEvaluations++;
        return new CountHolder();
    }
}

/// <summary>属性 += 计数用接收者。</summary>
public sealed class CountHolder
{
    public static int ReceiverEvaluations;

    private string _text = "";
    private int _getCount;
    private int _setCount;

    public string Text
    {
        get { _getCount++; return _text; }
        set { _setCount++; _text = value; }
    }

    // 块体属性：member-desugar 对表达式体属性（accessor null）有 NRE，故避开。
    public int GetCount
    {
        get { return _getCount; }
    }

    public int SetCount
    {
        get { return _setCount; }
    }
}
