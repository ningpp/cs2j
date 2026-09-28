using CSharpToJava.Core.RefOutInPreprocessor;

namespace CSharpToJava.CLI;

/// <summary>
/// Cs2jRefOutIn 支撑文件的嵌入资源读取/释放。真源是
/// src/Cs2jRefOutIn/ 下的文件（构建期嵌入 CLI 程序集），结构上
/// 不存在第二份副本可漂移。单文件模式沿用 _Cs2j* 支撑文件前缀契约。
/// </summary>
internal static class RefOutInSupportFiles
{
    public const string LibDirectoryName = "Cs2jRefOutIn";
    public const string SingleFileHelperFileName = "_Cs2jRefOutIn.cs";

    private const string SourceResourceName = "Cs2jRefOutIn.ByRef.cs";
    private const string ProjectResourceName = "Cs2jRefOutIn.Cs2jRefOutIn.csproj";

    public static string GetByRefFileContent() => ReadResource(SourceResourceName);
    public static string GetProjectFileContent() => ReadResource(ProjectResourceName);

    public static async Task ReleaseAsync(
        string destinationDirectory,
        IReadOnlyCollection<string> refStructTypes)
    {
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "ByRef.cs"),
            GetByRefFileContent(),
            new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "Cs2jRefOutIn.csproj"),
            GetProjectFileContent(),
            new System.Text.UTF8Encoding(false));

        foreach (var holderName in refStructTypes)
        {
            var content = GenerateRefStructHolder(holderName);
            await File.WriteAllTextAsync(
                Path.Combine(destinationDirectory, holderName + ".cs"),
                content,
                new System.Text.UTF8Encoding(false));
        }
    }

    /// <summary>
    /// Generate a ref struct holder file. The actual type is embedded in the
    /// file name; we emit a ref struct with direct + array-element modes.
    /// </summary>
    internal static string GenerateRefStructHolder(string holderName)
    {
        return $@"using System;

namespace Cs2jByRef;

public ref struct {holderName}
{{
    private object _slot;
    private object[] _array;
    private int _index;
    private readonly int _mode;

    public object Value
    {{
        get => _mode == 0 ? _slot : _array[_index];
        set {{ if (_mode == 0) _slot = value; else _array[_index] = value; }}
    }}

    public {holderName}() {{ _mode = 0; _slot = default; _array = default!; _index = 0; }}
}}
";
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
