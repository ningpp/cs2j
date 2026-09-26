namespace CSharpToJava.CLI;

/// <summary>
/// 按 csproj 文件名精确识别 cs2j 内部支撑库 Cs2jPrelude
/// （与 Cs2jArrayProject 同款保留项目名契约）。
/// </summary>
internal static class Cs2jPreludeProject
{
    public const string ProjectFileName = "Cs2jPrelude.csproj";

    public static bool IsCs2jPreludeProject(string? projectFilePath)
        => projectFilePath != null
           && Path.GetFileName(projectFilePath).Equals(ProjectFileName, StringComparison.OrdinalIgnoreCase);
}
