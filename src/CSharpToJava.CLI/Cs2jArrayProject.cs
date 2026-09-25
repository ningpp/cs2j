namespace CSharpToJava.CLI;

/// <summary>
/// 按 csproj 文件名精确识别 cs2j 内部支撑库 Cs2jArray
/// （与 Cs2jUnsignedMathProject 同款保留项目名契约）。
/// </summary>
internal static class Cs2jArrayProject
{
    public const string ProjectFileName = "Cs2jArray.csproj";

    public static bool IsCs2jArrayProject(string? projectFilePath)
        => projectFilePath != null
           && Path.GetFileName(projectFilePath).Equals(ProjectFileName, StringComparison.OrdinalIgnoreCase);
}
