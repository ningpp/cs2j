using System;

namespace SampleMemberDesugarLib;

// C50 field 上下文关键字样本（C# 14）：member-desugar 在 fieldKeyword.enabled=true 时
// 归一为显式后备字段 + get_X/set_X；关闭时原样保留（CS2J5122 声明级跳过）。
// 行为闸门 SampleMemberDesugarTests/FieldKeywordBehaviorTests 在两棵树上同样通过。

/// <summary>INPC：块体守卫 setter，值变化才通知；属性初始化器移至后备字段。</summary>
public class FieldKeywordInpc
{
    public event Action<string>? Changed;

    public string Name
    {
        get => field;
        set
        {
            if (field == value) return;
            field = value;
            Changed?.Invoke(nameof(Name));
        }
    } = "init";
}

/// <summary>lazy ??=：get-only field 属性在 getter 内首次赋值后备字段。</summary>
public class FieldKeywordLazy
{
    private int _loads;
    public int LoadCount => _loads;

    public string Expensive
    {
        get => field ??= Load();
    }

    private string Load()
    {
        _loads++;
        return "loaded-" + _loads;
    }
}

/// <summary>校验型块体 setter：非法值抛异常，后备字段不变。</summary>
public class FieldKeywordValidating
{
    public int Age
    {
        get => field;
        set
        {
            if (value < 0 || value > 150)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    }
}

/// <summary>get-only（含初始化器与 getter 计算）与 set-only（副作用可观测）。</summary>
public class FieldKeywordAccessKinds
{
    public int Doubled
    {
        get => field * 2;
    }

    public FieldKeywordAccessKinds() => Doubled = 21;

    public event Action<int>? WriteObserved;

    public int WriteOnly
    {
        set
        {
            field = value;
            WriteObserved?.Invoke(value);
        }
    }
}

/// <summary>静态 field 属性 + 同类型使用点。</summary>
public static class FieldKeywordStaticHolder
{
    public static int Counter
    {
        get => field;
        set => field = value;
    }

    public static void Bump()
    {
        Counter++;
    }
}

/// <summary>virtual/override field 属性链（各有独立合成字段）。</summary>
public class FieldKeywordVirtualBase
{
    public virtual int Kind
    {
        get => field;
        set => field = value;
    }

    protected FieldKeywordVirtualBase(int kind) => Kind = kind;
    public FieldKeywordVirtualBase() : this(1) { }
}

public class FieldKeywordOverrideSample : FieldKeywordVirtualBase
{
    public override int Kind
    {
        get => field;
        set => field = value;
    }

    public FieldKeywordOverrideSample() => Kind = 2;
}

/// <summary>readonly struct 内 get-only field 属性，构造函数经属性赋值。</summary>
public readonly struct FieldKeywordReadonlyPoint
{
    public int Doubled
    {
        get => field * 2;
    }

    public FieldKeywordReadonlyPoint(int value) => Doubled = value;
}

/// <summary>[field:] 目标特性随归一迁到合成字段；属性初始化器移字段。</summary>
[Serializable]
public class FieldKeywordSerializationDto
{
    public int Id
    {
        get => field;
        set => field = value;
    }

    [field: NonSerialized]
    public string Secret
    {
        get => field;
        set => field = value;
    } = "shh";
}

/// <summary>初始化器直达后备字段（绕过虚 setter 副作用），setter 自身计数。</summary>
public class FieldKeywordInitializerSideEffect
{
    public int SetterHits { get; private set; }

    public virtual int Weight
    {
        get => field;
        set
        {
            SetterHits++;
            field = value;
        }
    } = 10;
}
