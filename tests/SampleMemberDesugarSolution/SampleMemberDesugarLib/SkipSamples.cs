using System.Linq.Expressions;

namespace SampleMemberDesugarLib;

/// <summary>
/// 保守跳过样本（隔离类型）：表达式树引用的属性（CS2J5117）与
/// 静态字段初始化器中的属性赋值（CS2J5119，不可提升）。
/// 两者声明都保留原样，文件不去糖。
/// </summary>
public class ExpressionTreeSample
{
    public int Erp { get; set; }

    public Expression<Func<int>> Selector => () => Erp * 2;
}

public class StaticInitializerSample
{
    private static readonly StaticInitializerSample shared = new() { Label = "shared" };

    public string Label { get; set; } = "";

    public static StaticInitializerSample Shared => shared;
}
