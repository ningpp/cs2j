namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// CS2J33xx (ABI/holder 段) 诊断码——spec §6。现有 ROI 旧码在同一事件上并行保留一个版本。
/// </summary>
public static class RefOutInDiagnosticCodes
{
    /// <summary>Info：成员命中锁定（外部契约），保留 ref/out/in（并行 ROI001）。</summary>
    public const string LockedMember = "CS2J3301";

    /// <summary>Warning：in 去除引发重载/调用歧义，保留 in（CD8）。</summary>
    public const string InOverloadCollision = "CS2J3302";

    /// <summary>Error：无法构造合法代理的位置（多维数组、ref 字段、do-while 条件等）。</summary>
    public const string UnproxyablePosition = "CS2J3303";

    /// <summary>Error：ref struct H 槽向外部 ref API 转发（C# 语言无解，D3 §9.4）。</summary>
    public const string RefStructSlotForwarded = "CS2J3304";

    /// <summary>Error：后置自检发现内部 ref/out 残留（C10）。</summary>
    public const string InternalResidue = "CS2J3305";

    /// <summary>Error：重写产物重解析非法（并行 ROI099）。</summary>
    public const string ReparseFailed = "CS2J3310";

    /// <summary>Error：中间树 dotnet restore 失败（并行 ROI100）。</summary>
    public const string RestoreFailed = "CS2J3311";

    /// <summary>Error：中间树 dotnet build 失败（并行 ROI101）。</summary>
    public const string BuildFailed = "CS2J3312";

    /// <summary>Error：中间树 dotnet test 失败（并行 ROI102）。</summary>
    public const string TestsFailed = "CS2J3313";

    /// <summary>Error：Java 转换侧遇到未映射 ByRef 形态（J8）。</summary>
    public const string UnmappedByRefForm = "CS2J3320";
}
