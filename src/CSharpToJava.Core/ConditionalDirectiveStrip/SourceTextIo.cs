using System.Text;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public static class SourceTextIo
{
    public static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public sealed record SourceText(string Code, bool HasBom);

    public static async Task<SourceText> ReadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var code = Encoding.UTF8.GetString(hasBom ? bytes[3..] : bytes);
        return new SourceText(code, hasBom);
    }

    public static async Task WriteAsync(string path, string code, bool hasBom)
    {
        var payload = Encoding.UTF8.GetBytes(code);
        if (hasBom)
        {
            payload = [.. Utf8Bom, .. payload];
        }

        await File.WriteAllBytesAsync(path, payload);
    }

    public static async Task WriteAtomicallyAsync(string path, string code, bool hasBom)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Output path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await WriteAsync(tempPath, code, hasBom);
            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
