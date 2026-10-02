using System.Collections.Generic;
using SampleMemberDesugarLib;

namespace SampleMemberDesugarApp;

/// <summary>
/// C46 跨文件站点样本（设计 §7.2）：声明在 Lib、站点在 App，plan 的 TouchedFiles 必须覆盖两侧。
/// 仅需编译；分派行为由 .NET 半区 Tests 守门。
/// </summary>
public static class SignatureApp
{
    // 接口/类/强转/as/约束泛型五类接收者
    public static string InterfaceDispatch(IShape shape) => shape.Describe();
    public static string ClassDispatch(CollidingShape shape) => shape.Describe();
    public static string CastDispatch(object o) => ((IShape)o).Describe();
    public static string AsDispatch(object o) => (o as IShape)?.Describe() ?? "";
    public static string GenericDispatch<T>(T shape) where T : IShape => shape.Describe();

    // 隐藏成员两类静态类型调用
    public static string HiddenBase(HidingMethodBase b) => b.Tag();
    public static string HiddenDerived(HidingMethodDerived d) => d.Tag();

    // 协变两条调用路径
    public static CovAnimal CreateViaBase(CovBase b) => b.Create();
    public static CovDog CreateViaDerived(CovDerived d) => d.Create();
    public static IEnumerable<object> ItemsViaBase(CovBase b) => b.Items();
    public static IEnumerable<string> ItemsViaDerived(CovDerived d) => d.Items();
}