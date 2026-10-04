namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// C18 确定性命名服务（设计 §2 D12、§4.4）。命名契约与 C04/C45 对齐但不共享代码：
/// 全部 ASCII、避 C#/Java 关键字、禁用 Roslyn <c>&lt;&gt;</c> IL 名。
/// 碰撞手法同 IteratorLoweringNaming.ResolveUniqueName：原名字符串末尾直接追加 "2","3"…。
/// </summary>
internal static class AsyncNormalizeNaming
{
    public const string ThisField = "_cs2jThis";
    private const string LiftedMethodPrefix = "Cs2jAsync_";
    private const string WrapperSuffix = "_w";
    private const string CaptureFieldPrefix = "_cs2jCap_";
    private const string LocalFieldPrefix = "_cs2jLoc_";
    private const string TempPrefix = "_cs2jTmp_";

    /// <summary>提升方法名 <c>Cs2jAsync_&lt;宿主成员名&gt;_&lt;n&gt;</c>。</summary>
    public static string GetLiftedMethodName(string hostMemberName, int index)
        => $"{LiftedMethodPrefix}{hostMemberName}_{index}";

    /// <summary>D3(c) 同步丢弃 wrapper 名：提升方法名 + <c>_w</c>。</summary>
    public static string GetWrapperMethodName(string liftedMethodName)
        => liftedMethodName + WrapperSuffix;

    /// <summary>捕获字段名 <c>_cs2jCap_&lt;名&gt;</c>（1b 起使用）。</summary>
    public static string GetCaptureFieldName(string originalName)
        => CaptureFieldPrefix + originalName;

    /// <summary>提升局部字段名 <c>_cs2jLoc_&lt;名&gt;</c>（1b 起使用）。</summary>
    public static string GetLocalFieldName(string originalName)
        => LocalFieldPrefix + originalName;

    /// <summary>临时量名 <c>_cs2jTmp_&lt;n&gt;</c>。</summary>
    public static string GetTempName(int index) => $"{TempPrefix}{index}";

    /// <summary>
    /// 在 <paramref name="usedNames"/> 内消解碰撞：命中则在原名末尾直接追加
    /// "2","3",…（不加分隔符）；消解后把结果写回集合。
    /// </summary>
    public static string ResolveUniqueName(string baseName, HashSet<string> usedNames)
    {
        var name = baseName;
        var suffix = 2;
        while (usedNames.Contains(name))
        {
            name = baseName + suffix;
            suffix++;
        }
        usedNames.Add(name);
        return name;
    }
}
