namespace CSharpToJava.CLI;

/// <summary>
/// 按 csproj 文件名精确识别 cs2j 内部支撑库 Cs2jRefOutIn
/// （mirrors Cs2jUnsignedMathProject）。
/// </summary>
internal static class Cs2jRefOutInProject
{
    public const string ProjectFileName = "Cs2jRefOutIn.csproj";

    public static bool IsCs2jRefOutInProject(string? projectFilePath)
        => projectFilePath != null
           && Path.GetFileName(projectFilePath).Equals(ProjectFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cs2jRefOutIn uses ref fields and C# 13 <c>allows ref struct</c>, so it
    /// cannot target netstandard2.0 (verified: CS9064/CS9240/CS0246). Hosts below
    /// net10.0 cannot reference it; their injection is skipped with an explicit
    /// warning instead of producing an NU1201 restore failure.
    /// </summary>
    public static bool CanBeReferencedBy(string? hostTargetFramework)
        => ProjectUnsignedTypePreprocessor.IsTargetFrameworkAtLeastNet10(hostTargetFramework);
}
