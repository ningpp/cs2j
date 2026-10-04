namespace SampleAnonLib;

/// <summary>
/// C21 项目级 fixture 的 Lib 侧投影工具。所有方法都以 object/IEnumerable&lt;object&gt;
/// 签名暴露匿名投影结果：跨工程只走 object 身份，各工程分别获得生成类的 internal
/// 副本（设计 §7.2）。
/// </summary>
public static class Projections
{
    /// <summary>
    /// Name:String + Length:Int32 形状。必须与 SampleAnonApp/Program.cs 中独立创建
    /// 的形状同形 —— 两个工程各自得到同名 internal 类的独立副本。
    /// </summary>
    public static IEnumerable<object> ProjectAsObjects(IEnumerable<string> names)
        => names.Select(n => new { Name = n, Length = n.Length }).Cast<object>();

    /// <summary>Value:Int32 + Doubled:Int32 形状，取首项后以 object 跨工程返回。</summary>
    public static object FirstProjection(IEnumerable<int> xs)
        => xs.Select(x => new { Value = x, Doubled = x * 2 }).First();

    /// <summary>
    /// 泛型方法内投影：Item:T(槽位0) + Key:Int32 生成泛型形状，T 无约束
    /// （设计 D12 路径覆盖）。
    /// </summary>
    public static object GenericProjection<T>(IEnumerable<T> xs, Func<T, int> keyOf)
        => xs.Select(x => new { Item = x, Key = keyOf(x) }).First();

    /// <summary>嵌套投影：Items 是内层 V:Int32+Even:Boolean 形状的 List。</summary>
    public static object NestedProjection(IEnumerable<int> xs)
        => new
        {
            Items = xs.Select(x => new { V = x, Even = x % 2 == 0 }).ToList(),
            Count = xs.Count(),
        };

    /// <summary>dynamic 属性形状（AL030 Info；C# 侧保真，不影响 Success）。</summary>
    public static object DynamicShape(int v) => new { D = (dynamic)v, Tag = "d" };
}
