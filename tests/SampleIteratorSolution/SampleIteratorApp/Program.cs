using System.Collections;
using SampleIteratorLib;

namespace SampleIteratorApp;

/// <summary>
/// 经典 Main（非 top-level statements）：Java 半区要求转换器产出可直接启动
/// 的主类；.NET 侧作为真身预言机驱动，同样的输出基线在 JVM 上逐字对拍。
/// 所有行均为纯 ASCII 且无区域性依赖（布尔用 1/0 表示）。
/// </summary>
internal static class Program
{
    private static void WriteInts(string label, IEnumerable<int> values)
    {
        Console.Write(label);
        var first = true;
        foreach (var v in values)
        {
            Console.Write(first ? ":" : ",");
            Console.Write(v);
            first = false;
        }
        Console.WriteLine();
    }

    private static void WriteObjects(string label, IEnumerable values)
    {
        Console.Write(label);
        var first = true;
        foreach (var v in values)
        {
            Console.Write(first ? ":" : ",");
            Console.Write(v);
            first = false;
        }
        Console.WriteLine();
    }

    public static void Main()
    {
        var source = new SequenceSource();
        WriteInts("range", source.Range(10, 4));
        WriteInts("evens", source.Evens(new List<int> { 1, 2, 3, 4, 5, 6 }));
        WriteInts("tens", source.Tens(new[] { 1, 2, 3 }));

        var guardedFull = new SequenceSource();
        WriteInts("guarded0", guardedFull.Guarded(0));
        Console.WriteLine("guarded0-finally:" + guardedFull.GuardFinally);

        var guardedBreak = new SequenceSource();
        WriteInts("guarded1", guardedBreak.Guarded(1));
        Console.WriteLine("guarded1-finally:" + guardedBreak.GuardFinally);

        var nestedFull = new SequenceSource();
        WriteInts("nested0", nestedFull.Nested(0));
        Console.WriteLine("nested0-final:" + nestedFull.InnerFinally + "," + nestedFull.OuterFinally);

        var nestedBreak = new SequenceSource();
        WriteInts("nested1", nestedBreak.Nested(1));
        Console.WriteLine("nested1-final:" + nestedBreak.InnerFinally + "," + nestedBreak.OuterFinally);

        var listInTry = new SequenceSource();
        WriteInts("listintry", listInTry.ListInTry(new List<int> { 7, 8, 9 }));
        Console.WriteLine("listintry-finally:" + listInTry.GuardFinally);

        // 提前 Dispose（枚举到一半）：finally 恰好一次；之后 MoveNext 立即终止；
        // 重复 Dispose 幂等。
        var early = new SequenceSource();
        var earlyEnumerator = early.Guarded(0).GetEnumerator();
        earlyEnumerator.MoveNext();
        earlyEnumerator.MoveNext();
        ((IDisposable)earlyEnumerator).Dispose();
        ((IDisposable)earlyEnumerator).Dispose();
        Console.WriteLine("early:" + early.GuardFinally + ":"
            + (earlyEnumerator.MoveNext() ? 1 : 0));

        // 两个枚举器交错推进，互不共享状态；都未结束时 finally 计数为 0。
        var repeated = new SequenceSource();
        var first = repeated.Guarded(0).GetEnumerator();
        var second = repeated.Guarded(0).GetEnumerator();
        first.MoveNext();
        second.MoveNext();
        second.MoveNext();
        first.MoveNext();
        Console.WriteLine("interleave:" + first.Current + "," + second.Current + ":"
            + repeated.GuardFinally);
        while (first.MoveNext())
        {
        }
        while (second.MoveNext())
        {
        }
        Console.WriteLine("repeat-final:" + repeated.GuardFinally);

        WriteObjects("mixed", source.Mixed());

        // C04 M3：using 块/声明/嵌套 LIFO，提前 Dispose 恰好一次。
        var usingFull = new CountingResource();
        var usingFullSource = new SequenceSource();
        WriteInts("usingfull", usingFullSource.UsingBlock(usingFull));
        Console.WriteLine("usingfull-disp:" + usingFull.DisposeCount);

        var usingEarly = new CountingResource();
        var usingEarlySource = new SequenceSource();
        var usingEarlyEnum = usingEarlySource.UsingBlock(usingEarly).GetEnumerator();
        usingEarlyEnum.MoveNext();
        ((IDisposable)usingEarlyEnum).Dispose();
        Console.WriteLine("usingearly-disp:" + usingEarly.DisposeCount);

        var usingDecl = new CountingResource();
        var usingDeclSource = new SequenceSource();
        WriteInts("usingdecl", usingDeclSource.UsingDeclaration(usingDecl));

        var lifoLog = new List<string>();
        var lifoSource = new SequenceSource();
        WriteInts("usinglifo", lifoSource.UsingLifo(lifoLog));
        Console.WriteLine("usinglifo-order:" + string.Concat(lifoLog));

        // lock 内 yield：单线程输出序列正确（持锁时序由库测试断言）。
        WriteInts("locked", new SequenceSource().Locked(new object()));

        // C04 M4：含 yield 的 switch（多 section/default、循环内空 section 贯穿）。
        WriteInts("switch1", new SequenceSource().SwitchMap(1));
        WriteInts("switch2", new SequenceSource().SwitchMap(2));
        WriteInts("switchd", new SequenceSource().SwitchMap(7));
        WriteInts("switchloop", new SequenceSource().SwitchInLoop(new[] { 0, 1, 2, 3 }));
    }
}
