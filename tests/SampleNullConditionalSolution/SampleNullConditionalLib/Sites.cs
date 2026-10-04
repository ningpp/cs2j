using System;
using System.Collections.Generic;

namespace SampleNullConditionalLib;

public static class NullConditionalSites
{
    public static int MemberFallback(Simple? s) => s?.B ?? -1;

    public static int ValueCall(CallTarget? c)
        => c?.M(c.Next(), c.Next()) ?? -100;

    public static int ArrayIndex(int[]? a, int i) => a?[i] ?? -1;

    public static int ListIndex(List<int>? l, int i) => l?[i] ?? -2;

    public static int? MultiValue(TreeNode? n)
    {
        var x = n?.Leaf?.Value;
        return x;
    }

    public static void MultiVoid(TreeNode? n, int seed)
    {
        n?.Leaf?.Fire(seed);
    }

    public static int MixedCall(TreeNode? n)
    {
        return n?.Jump(1)?.Leaf?.Value ?? -3;
    }

    public static int? Jagged(int[][]? j, int i)
    {
        var x = j?[i]?.Length;
        return x;
    }
}

public sealed class InvokeSites
{
    public int HandlerCalls;

    public void Handler(int x) => HandlerCalls += x;

    public Action<int>? Pub;
    public event Action<int>? Ev;
    public Func<int, int>? Fn;

    public void VoidPub(int x)
    {
        Pub?.Invoke(x);
    }

    public void VoidEventNull()
    {
        Ev?.Invoke(7);
    }

    public void VoidEventSubscribed(int x)
    {
        Ev += Handler;
        Ev?.Invoke(x);
    }

    public int ValueFunc(int x) => Fn?.Invoke(x) ?? -9;
}
