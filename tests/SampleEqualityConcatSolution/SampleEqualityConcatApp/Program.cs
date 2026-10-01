// SampleEqualityConcatApp：.NET 运行时预言机（同一份断言在转换后的 Java 侧再跑一遍）。
using System;
using SampleDelegateEqualityLib;
using SampleRecordEqualityLib;
using SampleStringToolsLib;

namespace SampleEqualityConcatApp;

internal static class AssertionRunner
{
    public static int Run()
    {
        int failures = 0;

        // ---- 字符串：构造串相等 / object 逃逸 / 显式 ReferenceEquals ----
        if (!Check("constructed equals", StringTools.ConstructedEquals("abc"))) failures++;
        if (!Check("object escape", StringTools.ObjectEscape("abc"))) failures++;
        if (!Check("explicit referenceequals", StringTools.ExplicitReference("abc"))) failures++;

        // ---- 字符串：null 拼接 / += 链 / 值类型文本 / 属性计数 / 接收者单次 ----
        if (!Check("null concat", StringTools.NullConcat())) failures++;
        if (!Check("compound chain", StringTools.CompoundChain())) failures++;
        if (!Check("value text", StringTools.ValueText())) failures++;
        if (!Check("property += counts", StringTools.PropertyCompoundCounts())) failures++;
        if (!Check("unstable receiver once", StringTools.UnstableReceiverOnce())) failures++;

        // ---- 委托：调用列表相等（fixture 库源码无 +/-，多播在此构造）----
        Action a = () => { };
        Action p1 = a + a;
        Action p2 = a + a;
        if (!Check("(a+a)==(a+a)", DelegateEquality.AreEqual(p1, p2))) failures++;
        if (!Check("(a+a)!=(a+a) is false", !DelegateEquality.AreNotEqual(p1, p2))) failures++;
        if (!Check("delegate null==null", DelegateEquality.AreEqual(null, null))) failures++;
        if (!Check("delegate null!=a", !DelegateEquality.AreEqual(null, a))) failures++;
        if (!Check("delegate a!=null", !DelegateEquality.AreEqual(a, null))) failures++;
        if (!Check("delegate IsNull(null)", DelegateEquality.IsNull(null!))) failures++;
        if (!Check("delegate IsNullReverse(null)", DelegateEquality.IsNullReverse(null!))) failures++;
        if (!Check("delegate IsNotNull(a)", DelegateEquality.IsNotNull(a))) failures++;

        Action first = () => { };
        Action second = () => { };
        if (!Check("multicast order differs",
            !DelegateEquality.AreEqual(first + second, second + first))) failures++;

        // 泛型委托相等面
        if (!Check("generic delegate equals",
            DelegateEquality.AreEqualGeneric(p1, p2))) failures++;

        // ---- record：合成 == ----
        string[] tags = { "x" };
        PersonRecord r1 = new PersonRecord("Alice", 30, tags);
        PersonRecord r2 = new PersonRecord("Alice", 30, tags);
        if (!Check("record class ==", RecordEquality.SamePerson(r1, r2))) failures++;
        if (!Check("record class != false", !RecordEquality.DifferentPerson(r1, r2))) failures++;
        if (!Check("record null", RecordEquality.PersonIsNull(null))) failures++;
        if (!Check("record vs null", !RecordEquality.SamePerson(r1, null))) failures++;

        PointRecord pt1 = new PointRecord(1, 2);
        PointRecord pt2 = new PointRecord(1, 2);
        if (!Check("record value members ==", RecordEquality.SamePoint(pt1, pt2))) failures++;

        if (failures > 0)
        {
            Console.WriteLine(failures + " check(s) failed");
            return 1;
        }

        Console.WriteLine("ALL OK");
        return 0;
    }

    // 刻意不用 ref：项目链的 refoutin 降级会把 ref 局部物化为 ByRef 访问器 lambda
    // （`v -> failures = v`），该形态在 Java 侧无法作为 effectively-final 捕获编译。
    private static bool Check(string label, bool condition)
    {
        if (!condition)
        {
            Console.WriteLine("FAIL: " + label);
        }
        return condition;
    }
}

public static class Program
{
    // JVM 入口只接受 void（JDK28 允许无参 void main）；失败码经 Environment.Exit
    // （.NET 与 Java 均成立）。成功路径直接走完全部 Main，退出码 0。
    public static void Main()
    {
        int code = AssertionRunner.Run();
        if (code != 0)
        {
            Environment.Exit(code);
        }
    }
}
