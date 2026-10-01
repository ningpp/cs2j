using System;

namespace SampleDelegateEqualityLib;

/// <summary>
/// 委托相等库：源码刻意只含 <c>==</c>/<c>!=</c> 与 null 比较，不含任何 +/- 委托运算。
/// equality-concat pass 应仅因 <c>==</c> 即把调用点物化为
/// <c>Cs2jPrelude.DelegateOps.Equals(...)</c>；适配器也必须仅因 <c>==</c> 就释放
/// Cs2jPrelude 支撑库（不依赖 E05 是否改写过 +/-）。
/// </summary>
public static class DelegateEquality
{
    public static bool AreEqual(Action? a, Action? b) => a == b;

    public static bool AreNotEqual(Action? a, Action? b) => a != b;

    public static bool IsNull(Action? a) => a == null;

    public static bool IsNullReverse(Action? a) => null == a;

    public static bool IsNotNull(Action? a) => a != null;

    // 泛型形参上的 == 不被 preproc 改写（候选解析不跨泛型形参）。
    // 走 object.Equals：.NET 落到 virtual Delegate.Equals（多播调用列表语义），
    // Java 映射 Objects.equals → 代理的 equals（同为多播列表语义，J4 接线）。
    // 刻意不加 where T : Delegate：Java 侧泛型约束不会为 Delegate 自动补 import。
    public static bool AreEqualGeneric<T>(T? a, T? b) => object.Equals(a, b);
}
