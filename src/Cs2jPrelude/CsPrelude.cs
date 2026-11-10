// Cs2jPrelude：预处理注入的支撑类型唯一真源（委托组合/拆解 + C# 类型形态标记）。
// 本文件既是本库项目的源码，又被 CLI 以 EmbeddedResource 嵌入分发：
// 预处理把它释放到中间树 Cs2jPrelude/ 下，主转换器跳过该项目的转换，
// 调用点经 TypeMappings 映射到 compat 手写类（DelegateHelper / TypeHelper /
// Cs2jTypeShape）。
using System;
using System.Reflection;

namespace Cs2jPrelude
{
    /// <summary>
    /// 所有 C# 类型形态空标记接口的父接口。C# 里有而 Java 没有的类型修饰符
    /// （struct / record / readonly / ref struct）统一由实现空接口的方式带到
    /// Java 侧，避免依赖注解、命名约定或运行时猜测。父接口本身不带成员，
    /// 只用于一次性判断「该类型带有 C# 形态标记」。
    /// </summary>
    public interface Cs2jTypeShapeMarker { }

    /// <summary>C# struct 家族（struct / record struct / 降级的 ref struct）。Java 无值类型。</summary>
    public interface Cs2jStructMarker : Cs2jTypeShapeMarker { }

    /// <summary>C# record / record struct。Java record 的继承、可变成员与 with 语义均不同。</summary>
    public interface Cs2jRecordMarker : Cs2jTypeShapeMarker { }

    /// <summary>C# readonly struct / readonly record struct。Java 无编译期只读实例约束。</summary>
    public interface Cs2jReadOnlyMarker : Cs2jTypeShapeMarker { }

    /// <summary>
    /// 标记：该类型在原始 C# 源码中是 ref struct，已由 ref-struct-lowering 去 ref 降级。
    /// C# 侧 Cs2jByRefLikeHelper 与 Java compat TypeHelper.getIsByRefLike 都通过它
    /// 复现 Type.IsByRefLike 语义；Java 侧经 TypeMappings 映射到 compat 同名接口。
    /// </summary>
    public interface Cs2jByRefLikeMarker : Cs2jTypeShapeMarker { }

    /// <summary>
    /// 替代 System.Type.IsByRefLike 读取的桥接 Helper。降级类型靠标记接口识别；
    /// 仍带 ref 修饰符（本 pass 未降级）的类型靠 CLR 属性识别，避免行为回退。
    /// netstandard2.0 目标没有 Type.IsByRefLike，故用反射读取并缓存 PropertyInfo。
    /// </summary>
    public static class Cs2jByRefLikeHelper
    {
        private static readonly PropertyInfo IsByRefLikeProperty =
            typeof(Type).GetProperty("IsByRefLike");

        public static bool IsByRefLike(Type type)
        {
            if (type == null) return false;
            if (typeof(Cs2jByRefLikeMarker).IsAssignableFrom(type)) return true;
            return IsByRefLikeProperty != null
                && IsByRefLikeProperty.GetValue(type) is bool value
                && value;
        }
    }

    public static class CsPrelude
    {
        public static T Combine<T>(T a, T b) where T : Delegate
            => (T)Delegate.Combine(a, b);

        public static T Combine<T>(params T[] delegates) where T : Delegate
            => (T)Delegate.Combine(delegates);

        public static T Remove<T>(T a, T b) where T : Delegate
            => (T)Delegate.Remove(a, b);
    }
}
