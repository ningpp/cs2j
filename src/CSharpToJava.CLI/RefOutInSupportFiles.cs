using CSharpToJava.Core.RefOutInPreprocessor;

namespace CSharpToJava.CLI;

/// <summary>
/// Cs2jRefOutIn 支撑文件的嵌入资源读取/释放。真源是
/// src/Cs2jRefOutIn/ 下的文件（构建期嵌入 CLI 程序集），结构上
/// 不存在第二份副本可漂移。单文件模式沿用 _Cs2j* 支撑文件前缀契约。
/// Q2-A 定案后仅释放 ByRef.cs 与 RefStructByRef.cs 两个泛型源，
/// 不再按类型生成 RefStructByRef_&lt;Mangled&gt;。
/// </summary>
internal static class RefOutInSupportFiles
{
    public const string LibDirectoryName = "Cs2jRefOutIn";
    public const string SingleFileHelperFileName = "_Cs2jRefOutIn.cs";

    private const string SourceResourceName = "Cs2jRefOutIn.ByRef.cs";
    private const string RefStructSourceResourceName = "Cs2jRefOutIn.RefStructByRef.cs";
    private const string ProjectResourceName = "Cs2jRefOutIn.Cs2jRefOutIn.csproj";

    public static string GetByRefFileContent() => ReadResource(SourceResourceName);
    public static string GetRefStructByRefFileContent() => ReadResource(RefStructSourceResourceName);
    public static string GetProjectFileContent() => ReadResource(ProjectResourceName);

    /// <summary>单文件模式：两个支撑源合并写入 _Cs2jRefOutIn.cs（S3）。</summary>
    public static string GetSingleFileHelperContent() =>
        GetByRefFileContent() + Environment.NewLine + GetRefStructByRefFileContent();

    public static async Task ReleaseAsync(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "ByRef.cs"),
            GetByRefFileContent(),
            new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "RefStructByRef.cs"),
            GetRefStructByRefFileContent(),
            new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "Cs2jRefOutIn.csproj"),
            GetProjectFileContent(),
            new System.Text.UTF8Encoding(false));
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(RefOutInSupportFiles).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "The CLI project must embed src/Cs2jRefOutIn/* with explicit LogicalName.");
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
