namespace SampleExpressionDesugarLib;

public sealed class NestedInner
{
    public int D { get; set; }
}

public sealed class NestedOuter
{
    private NestedInner _inner = new();

    public static int GetterCalls;

    public NestedInner Inner
    {
        get
        {
            GetterCalls++;
            return _inner;
        }
        set => _inner = value;
    }

    /// <summary>Reads the backing field directly (does not touch the getter).</summary>
    public int DirectInnerD() => _inner.D;
}

public sealed class ConditionalDto
{
    public static int SideEffectCount;

    public string A { get; set; } = string.Empty;

    public static string SideEffect()
    {
        SideEffectCount++;
        return "x";
    }
}

/// <summary>Nested creation / nested member init / conditional-guard observations.</summary>
public static class NestedScenarios
{
    public static string NestedCreate()
    {
        var o = new NestedOuter { Inner = new NestedInner { D = 7 } };
        return o.DirectInnerD().ToString();
    }

    public static string NestedMemberGetterCalledOnce()
    {
        NestedOuter.GetterCalls = 0;
        var o = new NestedOuter { Inner = { D = 5 } };
        return o.DirectInnerD() + ":" + NestedOuter.GetterCalls;
    }

    /// <summary>The initializer side effect only runs on the taken branch.</summary>
    public static string ConditionalBranchSideEffects(bool cond)
    {
        ConditionalDto.SideEffectCount = 0;
        var d = cond ? new ConditionalDto { A = ConditionalDto.SideEffect() } : null;
        return (d?.A ?? "null") + ":" + ConditionalDto.SideEffectCount;
    }
}
