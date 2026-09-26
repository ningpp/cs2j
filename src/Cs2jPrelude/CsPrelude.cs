// Cs2jPrelude.CsPrelude：委托组合/拆解辅助类（唯一真源）。
// 本文件既是本库项目的源码，又被 CLI 以 EmbeddedResource 嵌入分发：
// delegate 预处理把它释放到中间树 Cs2jPrelude/ 下，主转换器
// 跳过该项目的转换，调用点经 TypeMappings 映射到 compat 手写类
// io.github.ningpp.compat.DelegateHelper。
using System;

namespace Cs2jPrelude
{
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
