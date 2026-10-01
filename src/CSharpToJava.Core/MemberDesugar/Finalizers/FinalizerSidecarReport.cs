using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.Core.MemberDesugar.Finalizers;

/// <summary>
/// FN-8 body 留痕报告（设计 §4.1/§5.3-5）：固定名
/// <c>.cs2j-finalizer-removals.json</c>，位于中间树根（文件路径中名为
/// <c>.cs2j-member-desugar-src</c> 的目录段根部，向上/本级定位）。每次 run 使用全新的
/// <see cref="MemberDesugarPreprocessor"/>，首次实际删除时懒初始化并清空旧文件，随后逐删除
/// 追加并落盘。定位不到中间树段（单测裸解析场景）时只保留内存条目不写盘。
/// sidecar 是审计日志：.json 不参与编译、不被转换器消费。
/// </summary>
internal sealed class FinalizerSidecarReport
{
    public const string FileName = ".cs2j-finalizer-removals.json";
    public const string IntermediateSegment = ".cs2j-member-desugar-src";
    public const string MemoryOnlyDisplayPath = "<in-memory sidecar>";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<FinalizerSidecarEntry> _entries = new();
    private readonly string _runId;
    private bool _resetDone;

    private FinalizerSidecarReport(string? path, string runId)
    {
        Path = path;
        _runId = runId;
    }

    /// <summary>sidecar 文件绝对路径；null 表示定位不到中间树根（内存模式）。</summary>
    public string? Path { get; }

    /// <summary>诊断/摘要中展示的路径（内存模式给出占位串）。</summary>
    public string DisplayPath => Path ?? MemoryOnlyDisplayPath;

    /// <summary>本 run 累计条目（先落盘后入列，保证条目数 == CS2J5201 条数）。</summary>
    public IReadOnlyList<FinalizerSidecarEntry> Entries => _entries;

    /// <summary>按被改写文件路径定位中间树根；找不到 <c>.cs2j-member-desugar-src</c> 段时
    /// 返回内存模式报告（树目录无需真实存在，路径段按字符串判定）。</summary>
    public static FinalizerSidecarReport CreateFor(string filePath)
    {
        var root = LocateIntermediateRoot(filePath);
        var path = root != null ? System.IO.Path.Combine(root, FileName) : null;
        return new FinalizerSidecarReport(path, Guid.NewGuid().ToString("N"));
    }

    private static string? LocateIntermediateRoot(string filePath)
    {
        // 裸解析（SyntaxTree 无 path）等场景：filePath 为空时直接内存模式，
        // 避免 Path.GetFullPath("") 抛 ArgumentException。
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath));
        while (!string.IsNullOrEmpty(dir))
        {
            if (string.Equals(
                    new DirectoryInfo(dir).Name, IntermediateSegment, StringComparison.Ordinal))
                return dir;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>记录一次实际删除：分配 index、追加并立即落盘（内存模式只入列）。
    /// 返回的条目即为 JSON 序列化源（body 原文经 JSON 转义，BOM 无关）。</summary>
    public FinalizerSidecarEntry Add(
        string typeFqn, string sourceFilePath, int line, int column,
        FinalizerBodyKind bodyKind, SuppressFinalizePairing pairing,
        bool hasBaseFinalizer, string? baseFinalizerFqn, bool forced, string? bodyText)
    {
        EnsureReset();

        var body = bodyText ?? string.Empty;
        var entry = new FinalizerSidecarEntry
        {
            Index = _entries.Count,
            TypeFqn = typeFqn,
            File = RelativeFile(sourceFilePath),
            Line = line,
            Column = column,
            BodyKind = FinalizerDiagnostics.KindLabel(bodyKind),
            Pairing = PairingJson(pairing),
            HasBaseFinalizer = hasBaseFinalizer,
            BaseFinalizerFqn = baseFinalizerFqn,
            Forced = forced,
            Body = body,
            BodySha256 = Sha8(body),
        };
        _entries.Add(entry);
        Flush();
        return entry;
    }

    /// <summary>run 开始重置：清空旧文件并写入空报告（适配器每轮新建 preprocessor）。</summary>
    private void EnsureReset()
    {
        if (_resetDone) return;
        _resetDone = true;
        _entries.Clear();
        Flush();
    }

    private void Flush()
    {
        if (Path == null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var document = new SidecarDocument { RunId = _runId, Entries = _entries.ToList() };
        File.WriteAllText(Path, JsonSerializer.Serialize(document, JsonOptions),
            new UTF8Encoding(false));
    }

    private string RelativeFile(string sourceFilePath)
    {
        var root = System.IO.Path.GetDirectoryName(Path);
        if (root == null) return sourceFilePath.Replace('\\', '/');
        try
        {
            return System.IO.Path.GetRelativePath(root, System.IO.Path.GetFullPath(sourceFilePath))
                .Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            // 路径位于不同根等异常情况：退化为正斜杠绝对路径。
            return sourceFilePath.Replace('\\', '/');
        }
    }

    private static string PairingJson(SuppressFinalizePairing pairing) => pairing switch
    {
        SuppressFinalizePairing.Paired => "Paired",
        SuppressFinalizePairing.Unpaired => "Unpaired",
        _ => "None",
    };

    private static string Sha8(string body)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..8]
            .ToLowerInvariant();

    private sealed class SidecarDocument
    {
        [JsonPropertyName("runId")]
        public string RunId { get; set; } = string.Empty;

        [JsonPropertyName("entries")]
        public List<FinalizerSidecarEntry> Entries { get; set; } = new();
    }
}

/// <summary>sidecar 单条删除记录（设计 §4.1 FN-8，字段名即 JSON 键，camelCase 显式标注）。</summary>
internal sealed class FinalizerSidecarEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("typeFqn")]
    public string TypeFqn { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("bodyKind")]
    public string BodyKind { get; set; } = string.Empty;

    [JsonPropertyName("pairing")]
    public string Pairing { get; set; } = string.Empty;

    [JsonPropertyName("hasBaseFinalizer")]
    public bool HasBaseFinalizer { get; set; }

    [JsonPropertyName("baseFinalizerFqn")]
    public string? BaseFinalizerFqn { get; set; }

    [JsonPropertyName("forced")]
    public bool Forced { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("bodySha256")]
    public string BodySha256 { get; set; } = string.Empty;
}
