using System.Runtime.InteropServices;

namespace SampleUnsafeFfmExtern;

/// <summary>
/// M4/R8 最小互操作场景：非白名单 extern（不实际调用 native）。
/// C06 启用时本文件必须收到 UF060——设计 §1.2 澄清的「检查 pass 两层
/// 不一致 / NativeMethods.cs 项目管线硬失败 NotSupportedException」现状
/// 由此统一取代为显式诊断；永不发静默 stub。
/// 独立于 SampleUnsafeFfmSolution 目录：sln 运行会递归枚举源根下全部
/// .cs 文件，故互操作工程不得放在该 sln 目录内。
/// </summary>
public static class NativeMethods
{
    [DllImport("kernel32.dll")]
    public static extern int Beep(int frequency, int duration);
}
