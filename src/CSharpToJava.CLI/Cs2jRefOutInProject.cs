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
}
