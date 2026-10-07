using System.IO;
using System.Text.Json;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 R8 opt-in FFM 白名单条目（设计 D8）。键为
/// <c>{含命名空间的宿主类型显示名}.{方法名}</c>（如
/// <c>MyApp.NativeMethods.Beep</c>）；<see cref="EntryPoint"/> 缺省时由
/// rewriter 回落到 C# 方法名。
/// <para>
/// v1 封送面（布局可证明子集）：形参仅允许基元（byte/sbyte/short/ushort/
/// char/int/uint/long/ulong/float/double）与 string（只读拷贝，按
/// <see cref="StringEncoding"/> 分配 UTF-8/UTF-16）；返回允许 void 与基元
/// （string 回写无证明，拒绝）。bool 因 ABI 宽度二义（Win32 BOOL=4 字节 vs
/// ValueLayout.JAVA_BOOLEAN=1 字节）v1 拒绝（UF061），请用 int/byte 显式
/// 表达宽度。ref/out/in、指针、自定义 struct、enum、泛型方法、params
/// 数组一律 UF061。
/// </para>
/// </summary>
public sealed record ExternWhitelistEntry(
    string MethodKey,
    string Library,
    string? EntryPoint,
    ExternStringEncoding StringEncoding)
{
    /// <summary>原生入口符号名；未配置时使用 C# 方法名。</summary>
    public string ResolveEntryPoint(string csharpMethodName) =>
        string.IsNullOrWhiteSpace(EntryPoint) ? csharpMethodName : EntryPoint!;
}

/// <summary>白名单字符串封送编码：UTF-8（默认）或 UTF-16（*W 入口）。</summary>
public enum ExternStringEncoding
{
    Utf8,
    Utf16,
}

/// <summary>
/// C06 R8 opt-in FFM 白名单（JSON 配置，设计 D8「配置驱动」）。文件形态：
/// <code>
/// {
///   "entries": [
///     { "method": "MyApp.NativeMethods.Beep",
///       "library": "kernel32", "entryPoint": "Beep",
///       "stringEncoding": "utf16" }
///   ]
/// }
/// </code>
/// library 为空串表示平台默认库（Linker.defaultLookup）。
/// 未加载白名单（null）时一切 extern 维持 UF060 默认拒绝——白名单是
/// opt-in 子段，永不改变默认安全姿态。
/// </summary>
public sealed class ExternWhitelist
{
    private readonly Dictionary<string, ExternWhitelistEntry> _entries;

    private ExternWhitelist(Dictionary<string, ExternWhitelistEntry> entries)
    {
        _entries = entries;
    }

    public int Count => _entries.Count;

    /// <summary>按方法符号键取条目；未命中返回 null（调用方维持 UF060）。</summary>
    public ExternWhitelistEntry? Find(string containingTypeDisplayName, string methodName)
    {
        var key = containingTypeDisplayName + "." + methodName;
        return _entries.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>
    /// 从 JSON 文件加载白名单。文件不存在、JSON 非法、schema 字段缺失或
    /// 键重复均以 <see cref="ExternWhitelistException"/> 响亮失败（静默空
    /// 白名单会让 opt-in 用户误以为白名单已生效）。
    /// </summary>
    public static ExternWhitelist LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ExternWhitelistException("FFM extern whitelist path is empty.");

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ExternWhitelistException(
                $"cannot read FFM extern whitelist '{path}': {e.Message}", e);
        }

        try
        {
            return Parse(text);
        }
        catch (JsonException e)
        {
            throw new ExternWhitelistException(
                $"malformed FFM extern whitelist '{path}': {e.Message}", e);
        }
    }

    /// <summary>从 JSON 文本解析白名单（供测试与内存配置使用）。</summary>
    public static ExternWhitelist Parse(string jsonText)
    {
        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("root must be a JSON object.");
        if (!root.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("missing required array property 'entries'.");
        }

        var map = new Dictionary<string, ExternWhitelistEntry>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in entries.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new JsonException($"entries[{index}] must be an object.");

            var method = ReadRequiredString(element, "method", index);
            var library = ReadRequiredString(element, "library", index);
            string? entryPoint = null;
            if (element.TryGetProperty("entryPoint", out var ep)
                && ep.ValueKind == JsonValueKind.String)
            {
                entryPoint = ep.GetString();
            }

            var encoding = ExternStringEncoding.Utf8;
            if (element.TryGetProperty("stringEncoding", out var enc)
                && enc.ValueKind == JsonValueKind.String)
            {
                encoding = enc.GetString() switch
                {
                    "utf8" or "utf-8" => ExternStringEncoding.Utf8,
                    "utf16" or "utf-16" => ExternStringEncoding.Utf16,
                    var bad => throw new JsonException(
                        $"entries[{index}].stringEncoding must be 'utf8' or 'utf16' (was '{bad}')."),
                };
            }

            var entry = new ExternWhitelistEntry(method, library, entryPoint, encoding);
            if (!map.TryAdd(method, entry))
                throw new JsonException($"duplicate whitelist method key '{method}'.");
            index++;
        }

        return new ExternWhitelist(map);
    }

    private static string ReadRequiredString(
        JsonElement element, string property, int index)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"entries[{index}].{property} must be a non-empty string.");
        }

        return value.GetString()!;
    }
}

/// <summary>FFM 白名单加载/解析失败（配置错误，区别于代码诊断 UF060/UF061）。</summary>
public sealed class ExternWhitelistException : Exception
{
    public ExternWhitelistException(string message) : base(message)
    {
    }

    public ExternWhitelistException(string message, Exception inner) : base(message, inner)
    {
    }
}
