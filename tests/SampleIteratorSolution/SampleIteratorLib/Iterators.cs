using System.Collections;
using System.Collections.Generic;

namespace SampleIteratorLib;

/// <summary>
/// C04 M2 迭代器样例集：覆盖 for/while 风格循环、List&lt;T&gt;（值类型枚举器）、
/// 数组 foreach、try/finally 保护区域、嵌套 finally、提前结束迭代、
/// foreach-in-try 与非泛型 IEnumerable。计数器字段供 App/Tests 观察
/// finally 执行次数契约。
/// </summary>
public sealed class SequenceSource
{
    public int GuardFinally;
    public int OuterFinally;
    public int InnerFinally;

    public IEnumerable<int> Range(int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return start + i;
        }
    }

    public IEnumerable<int> Evens(List<int> values)
    {
        foreach (var v in values)
        {
            if (v % 2 == 0)
            {
                yield return v;
            }
        }
    }

    public IEnumerable<int> Tens(int[] values)
    {
        foreach (var v in values)
        {
            yield return v * 10;
        }
    }

    public IEnumerable<int> Guarded(int stop)
    {
        try
        {
            yield return 1;
            yield return 2;
            if (stop == 1)
            {
                yield break;
            }
            yield return 3;
        }
        finally
        {
            GuardFinally++;
        }
    }

    public IEnumerable<int> Nested(int stop)
    {
        try
        {
            yield return 1;
            try
            {
                yield return 2;
                if (stop == 1)
                {
                    yield break;
                }
                yield return 3;
            }
            finally
            {
                InnerFinally++;
            }
            yield return 4;
        }
        finally
        {
            OuterFinally++;
        }
    }

    public IEnumerable<int> ListInTry(List<int> values)
    {
        try
        {
            foreach (var v in values)
            {
                yield return v;
            }
        }
        finally
        {
            GuardFinally++;
        }
    }

    public IEnumerable Mixed()
    {
        yield return 1;
        yield return "two";
    }

    // ── C04 M3：using 语句/声明 + lock 跨 yield ──

    public IEnumerable<int> UsingBlock(CountingResource resource)
    {
        using (resource)
        {
            yield return 11;
            yield return 22;
        }
    }

    public IEnumerable<int> UsingDeclaration(CountingResource resource)
    {
        using var held = resource;
        yield return 33;
        yield return 44;
    }

    public IEnumerable<int> UsingLifo(List<string> log)
    {
        using (new OrderResource("a", log))
        using (new OrderResource("b", log))
        {
            yield return 55;
        }
    }

    public IEnumerable<int> Locked(object gate)
    {
        lock (gate)
        {
            yield return 71;
            yield return 72;
        }
    }
}

/// <summary>M3：记录 Dispose 次数的引用类型资源（null 守卫路径）。</summary>
public sealed class CountingResource : IDisposable
{
    public int DisposeCount;

    public void Dispose()
    {
        DisposeCount++;
    }
}

/// <summary>M3：记录释放顺序的资源，用于断言嵌套 using 的 LIFO 时序。</summary>
public sealed class OrderResource : IDisposable
{
    private readonly string _name;
    private readonly List<string> _log;

    public OrderResource(string name, List<string> log)
    {
        _name = name;
        _log = log;
    }

    public void Dispose()
    {
        _log.Add(_name);
    }
}
