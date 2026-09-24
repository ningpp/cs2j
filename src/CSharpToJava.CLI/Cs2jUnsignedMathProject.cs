namespace CSharpToJava.CLI;

/// <summary>
/// 按 csproj 文件名精确识别 cs2j 内部支撑库 Cs2jUnsignedMath
/// （2026-08-15 设计 §4.3 的保留项目名契约，项目级落地）。
/// </summary>
internal static class Cs2jUnsignedMathProject
{
    public const string ProjectFileName = "Cs2jUnsignedMath.csproj";

    public static bool IsCs2jUnsignedMathProject(string? projectFilePath)
        => projectFilePath != null
           && Path.GetFileName(projectFilePath).Equals(ProjectFileName, StringComparison.OrdinalIgnoreCase);
}
