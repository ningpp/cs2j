namespace CSharpToJava.CLI;

/// <summary>
/// Cs2jUnsignedMath 支撑文件的嵌入资源读取/释放。真源是
/// src/Cs2jUnsignedMath/ 下的两个文件（构建期嵌入 CLI 程序集），结构上
/// 不存在第二份副本可漂移。单文件模式（unsigned-type -i/-o）沿用
/// _Cs2j* 支撑文件前缀契约，文件名与项目模式的 UnsignedMath.cs 不同名是有意的。
/// </summary>
internal static class UnsignedMathSupportFiles
{
    public const string LibDirectoryName = "Cs2jUnsignedMath";
    public const string SingleFileHelperFileName = "_Cs2jUnsignedMath.cs";

    private const string SourceResourceName = "Cs2jUnsignedMath.UnsignedMath.cs";
    private const string ProjectResourceName = "Cs2jUnsignedMath.Cs2jUnsignedMath.csproj";

    public static string GetUnsignedMathFileContent() => ReadResource(SourceResourceName);

    public static string GetProjectFileContent() => ReadResource(ProjectResourceName);

    /// <summary>把库项目（csproj + 源码）释放到目标目录（UTF-8 无 BOM）。</summary>
    public static async Task ReleaseAsync(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "UnsignedMath.cs"),
            GetUnsignedMathFileContent(),
            new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "Cs2jUnsignedMath.csproj"),
            GetProjectFileContent(),
            new System.Text.UTF8Encoding(false));
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(UnsignedMathSupportFiles).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "The CLI project must embed src/Cs2jUnsignedMath/* with explicit LogicalName.");
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
