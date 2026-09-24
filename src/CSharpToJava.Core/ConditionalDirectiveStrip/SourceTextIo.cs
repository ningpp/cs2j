using System.Text;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

/// <summary>
/// BOM 感知的源文件读写：清理器改写文件时保留原字节序标记（无 BOM 输入输出仍无 BOM），
/// 非 ASCII 内容经 UTF-8 往返无损。编码假设与仓库其它预处理器一致：输入为 UTF-8/UTF-8-BOM。
/// </summary>
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
}
