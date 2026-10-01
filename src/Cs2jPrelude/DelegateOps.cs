// Cs2jPrelude.DelegateOps：委托相等辅助类（C47 EC-6，唯一真源）。
// 分发方式与 CsPrelude.cs 相同：既是本库项目源码，又被 CLI 以
// EmbeddedResource 嵌入；equality-concat 预处理把它释放到中间树
// Cs2jPrelude/ 下。object.Equals 保留 C# delegate == 语义（null==null 为 true）。
using System;

namespace Cs2jPrelude
{
    public static class DelegateOps
    {
        public static bool Equals<T>(T? a, T? b) where T : Delegate
            => object.Equals(a, b);
    }
}
