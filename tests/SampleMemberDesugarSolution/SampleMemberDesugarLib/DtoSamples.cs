using System;

namespace SampleMemberDesugarLib;

/// <summary>
/// C11 属性矩阵样本：自动 / 计算表达式体 / init / required / 静态 / get-only 带初始化器 /
/// 隐藏（new）/ 泛型 / 后备字段命名冲突。基线（去糖前）与去糖后行为必须一致。
/// </summary>
public class OrderLine
{
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Total => Quantity * Price;
    public string Note { get; init; } = "none";
}

public class RequiredDto
{
    public required int Id { get; set; }
    public required string Name { get; init; }
}

public class StaticHolder
{
    public static int Counter { get; private set; }

    public static int Bump()
    {
        Counter++;
        return Counter;
    }
}

public class GetOnlyWithInitializer
{
    public DateTime Created { get; } = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
    public IReadOnlyList<int> Bits { get; } = new[] { 1, 2, 3 };
}

public class CollisionSample
{
    // 与 Age 的默认后备字段名冲突（camelCase + Value 后缀），迫使再追加后缀。
    private int ageValue;

    public int Age { get; set; }
    public int Count { get; set; }
    public int Sum => Age + Count;
}

public class Box<T>
{
    public T Value { get; set; }
}

public class DelegateHolder
{
    public Func<int, int> Op { get; set; } = x => x + 1;
}

public class HidingBase
{
    public string Name { get; set; } = "base";
}

public class HidingDerived : HidingBase
{
    public new string Name { get; set; } = "derived";
}
