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
                await ReplaceWithRetryAsync(tempPath, fullPath);
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

    /// <summary>
    /// Atomically replaces <paramref name="fullPath"/> with the already written
    /// <paramref name="tempPath"/>. On Windows a concurrent reader or scanner
    /// can hold the destination briefly, so sharing violations are retried with
    /// a short backoff; if the destination stays locked, fall back to a
    /// non-atomic overwrite rather than failing the write.
    /// </summary>
    private static async Task ReplaceWithRetryAsync(string tempPath, string fullPath)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(tempPath, fullPath, destinationBackupFileName: null);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts)
                {
                    File.Move(tempPath, fullPath, overwrite: true);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt));
            }
        }
    }
}
