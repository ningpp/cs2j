using System.Runtime.InteropServices;

namespace SamplePlatformIsolationInteropLib;

/// <summary>
/// C42 fixture: the conventional P/Invoke container. The M2 inventory registers
/// this file (no longer a silent filename escape), so CS2J3203/3201 must appear
/// in the report and Reject mode must fail fast (design §7.4).
/// </summary>
internal static class NativeMethods
{
    // 非白名单形态（bool 返回不属 C06 v1 ABI 子集，UF061 拒）：任何档下都由
    // C42 登记 CS2J3201/3203 并 Reject。
    [DllImport("kernel32.dll")]
    internal static extern bool Beep(uint frequency, uint duration);

    // M3 联合 fixture：C06 unsafe-ffm 白名单可收编形态（基元返回、无形参；
    // kernel32!GetTickCount）。加载含该条目的 --extern-whitelist 后，C06 将其
    // 归一为带 Cs2j.Generation.InteropFacade 标记的受控 facade，随后的 C42 对
    // 其不再重复报 CS2J3201/3203（共享谓词抑制，设计 §4.5）；无白名单时它与
    // Beep 一样保持 extern 形态并被 C42 Reject。
    [DllImport("kernel32.dll")]
    internal static extern uint GetTickCount();
}