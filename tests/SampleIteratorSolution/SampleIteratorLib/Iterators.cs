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
}
