using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>C25 residual 双扫闸门结果（设计 §6.2/D13；仿 C42 ResidualGateResult）。</summary>
public sealed class DynamicResidualGateResult
{
    /// <summary>未登记残余站点数（命中 manifest 的不计；>0 即 HasErrors）。</summary>
    public required int ResidualCount { get; init; }

    /// <summary>命中 manifest 条目的残余站点数（DS060）。</summary>
    public required int SuppressedCount { get; init; }

    /// <summary>陈旧条目数（DS061）。</summary>
    public required int StaleEntries { get; init; }

    /// <summary>闸门诊断（DS030 Error / DS060 / DS061；已按 options.Strict 分级）。</summary>
    public required IReadOnlyList<DynamicDesugarDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// C25 M2 residual 闸门（设计 §6.2，仅 Rewrite 模式收口；仿
/// <see cref="PlatformIsolation.PlatformResidualGate"/> 的 C42 范本）：
/// 输入为逐文件改写后整树重建的编译 + 审计 manifest，双扫输出树——
/// ①语法扫非注释/非字符串的 dynamic 关键字 token；②语义扫（对输出编译重跑
/// <see cref="DynamicSiteInventoryBuilder"/>）TypeKind.Dynamic / LateBound /
/// object+[Dynamic] 使用点。每个残余站点必须命中 manifest 条目：命中→DS060
/// （默认 Warning，--strict 升 Error）；未命中→DS030 Error；未匹配任何站点的
/// 条目→DS061 Warning。合法终态只有两个：dynamic 站点为 0，或全部残余均已
/// 审计登记（D13）。
/// </summary>
internal static class DynamicResidualGate
{
    public static DynamicResidualGateResult Scan(
        Compilation outputCompilation,
        DynamicAuditManifest? manifest,
        DynamicDesugarOptions options)
    {
        ArgumentNullException.ThrowIfNull(outputCompilation);
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<DynamicDesugarDiagnostic>();
        var residual = 0;
        var suppressed = 0;

        // 语义扫：输出残余站点即输出编译上的 inventory 站点。DS002 P2 cast
        // 所在动态子树仍是 inventory 记录（DynamicConversion/ExplicitCastP2），
        // 故同样按残余计——显式转换只证明使用点静态绑定，源头 dynamic 面未消。
        var inventory = DynamicSiteInventoryBuilder.Build(outputCompilation);
        var coverageSpans = BuildCoverageSpans(outputCompilation, inventory);
        var matchedEntries = new HashSet<int>();
        var manifestBaseDirectory = ManifestBaseDirectory(manifest);

        foreach (var site in inventory.Sites)
        {
            var fileCandidates = SiteFileCandidates(site.FilePath, manifestBaseDirectory);
            var match = FindMatch(manifest, site, fileCandidates);
            var location = $"{DisplayFile(site.FilePath, manifestBaseDirectory)}"
                           + $"({site.Line + 1},{site.Column + 1})";

            if (match != null)
            {
                suppressed++;
                matchedEntries.Add(match.Index);
                diagnostics.Add(new DynamicDesugarDiagnostic(
                    DynamicDesugarDiagnosticCodes.ManifestSuppressed,
                    options.Strict
                        ? DynamicDesugarSeverity.Error
                        : DynamicDesugarSeverity.Warning,
                    $"Residual dynamic site '{site.Kind}' at {location} is registered in "
                    + $"Cs2jDynamicAudit.json (auditor: {match.Entry.Auditor}; reason: "
                    + $"{match.Entry.Reason}); accepted as an explicit known residual.",
                    site.FilePath,
                    site.Line + 1,
                    site.Column + 1));
            }
            else
            {
                residual++;
                diagnostics.Add(new DynamicDesugarDiagnostic(
                    DynamicDesugarDiagnosticCodes.ResidualGateViolation,
                    DynamicDesugarSeverity.Error,
                    $"Residual dynamic site '{site.Kind}' at {location} survived dynamic-desugar "
                    + "without an audit registration; concretize it (change the declaration/"
                    + "receiver to a concrete type) or register the site in Cs2jDynamicAudit.json "
                    + $"at the source root. Site text: {site.ExpressionText}.",
                    site.FilePath,
                    site.Line + 1,
                    site.Column + 1));
            }
        }

        // 语法扫：dynamic 关键字 token 必须落在某残余站点（含声明类型位）覆盖
        // 区间内；落在覆盖外的关键字代表语义扫漏判形态，fail-closed 直接 DS030。
        foreach (var tree in outputCompilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            var filePath = tree.FilePath ?? string.Empty;
            var fileCandidates = SiteFileCandidates(filePath, manifestBaseDirectory);
            var fileSpans = coverageSpans
                .Where(span => span.FilePath == filePath)
                .Select(span => span.Span)
                .ToList();

            foreach (var token in root.DescendantTokens())
            {
                if (!IsDynamicKeywordToken(token) || fileSpans.Any(span => span.Contains(token.Span)))
                {
                    continue;
                }

                // 语义扫完全没有站点、却出现 dynamic 关键字：无法按 kind 登记，
                // 始终 DS030（宁假阳；注释/字符串天然不产生 token，见谓词注释）。
                residual++;
                var position = tree.GetLineSpan(token.Span).StartLinePosition;
                var location = $"{DisplayFile(filePath, manifestBaseDirectory)}"
                               + $"({position.Line + 1},{position.Character + 1})";
                diagnostics.Add(new DynamicDesugarDiagnostic(
                    DynamicDesugarDiagnosticCodes.ResidualGateViolation,
                    DynamicDesugarSeverity.Error,
                    $"Residual 'dynamic' keyword at {location} is not covered by any classified "
                    + "dynamic site after rewrite; concretize the declaration or register the site "
                    + "in Cs2jDynamicAudit.json at the source root.",
                    filePath,
                    position.Line + 1,
                    position.Character + 1));
            }
        }

        // 陈旧检测：条目未命中任何输出残余站点 → DS061 每条一条（R7 防腐烂）。
        var stale = 0;
        if (manifest != null)
        {
            for (var i = 0; i < manifest.Entries.Count; i++)
            {
                if (matchedEntries.Contains(i))
                {
                    continue;
                }

                stale++;
                var entry = manifest.Entries[i];
                diagnostics.Add(new DynamicDesugarDiagnostic(
                    DynamicDesugarDiagnosticCodes.StaleManifestEntry,
                    options.Strict
                        ? DynamicDesugarSeverity.Error
                        : DynamicDesugarSeverity.Warning,
                    $"Stale dynamic audit entry[{i}] for '{entry.File}' {entry.Kind} span "
                    + $"[{entry.SpanStart},{entry.SpanEnd}) (auditor: {entry.Auditor}) matches no "
                    + "residual site in the rewritten tree; remove it or re-audit the site."));
            }
        }

        return new DynamicResidualGateResult
        {
            ResidualCount = residual,
            SuppressedCount = suppressed,
            StaleEntries = stale,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// 语法扫谓词（设计 §6.2 ①「非注释/非字符串的 dynamic 关键字 token」）。
    /// 实况：dynamic 是上下文关键字，Roslyn 词法产出的 token 种类恒为
    /// IdentifierToken（本工程 Roslyn 版本不存在 SyntaxKind.DynamicKeyword，
    /// 与 M1 DynamicSiteRewriter.IsDynamicDirty 同口径），故按
    /// IdentifierToken + 文本 "dynamic" 识别。字符串字面量是单个
    /// StringLiteralToken、注释是 trivia（DescendantTokens 不遍历非结构化
    /// trivia），二者天然不计。
    /// </summary>
    private static bool IsDynamicKeywordToken(SyntaxToken token)
        => token.IsKind(SyntaxKind.IdentifierToken) && token.Text == "dynamic";

    /// <summary>
    /// 在逐文件改写之前计算输入清单中的 manifest 命中站点（引用相等集合）：
    /// 登记站点都在不被具体化的字节复制文件内（D13 span 稳定前提），故输入
    /// 命中即输出残余命中；这些站点的原始 DS01x 诊断不再发射，由闸门对输出
    /// 统一发 DS060（D13「由 Error 降为 DS060」）。
    /// </summary>
    public static HashSet<DynamicSiteRecord> FindManifestHitSites(
        DynamicSiteInventory inventory,
        DynamicAuditManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var hits = new HashSet<DynamicSiteRecord>(ReferenceEqualityComparer.Instance);
        if (manifest == null)
        {
            return hits;
        }

        var baseDirectory = ManifestBaseDirectory(manifest);
        foreach (var site in inventory.Sites)
        {
            var candidates = SiteFileCandidates(site.FilePath, baseDirectory);
            foreach (var entry in manifest.Entries)
            {
                if (DynamicAuditManifest.MatchesSite(entry, site, candidates))
                {
                    hits.Add(site);
                    break;
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// 声明类站点的记录节点不一定覆盖类型位上的 dynamic 关键字：
    /// 局部/字段记录挂在 VariableDeclarator（不含共享 VariableDeclaration 类型
    /// 语法），解构分量挂在 SingleVariableDesignation。把这些站点的覆盖区间
    /// 拓宽到含类型位的最近声明节点，避免语法扫对同一残余重复计数。
    /// </summary>
    private static List<(string FilePath, TextSpan Span)> BuildCoverageSpans(
        Compilation compilation,
        DynamicSiteInventory inventory)
    {
        var rootsByPath = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        var result = new List<(string FilePath, TextSpan Span)>();

        foreach (var site in inventory.Sites)
        {
            if (!rootsByPath.TryGetValue(site.FilePath, out var root))
            {
                var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                    (t.FilePath ?? string.Empty) == site.FilePath);
                if (tree == null)
                {
                    continue;
                }

                root = tree.GetRoot();
                rootsByPath[site.FilePath] = root;
            }

            var node = root.FindNode(
                new TextSpan(site.SpanStart, site.SpanEnd - site.SpanStart),
                getInnermostNodeForTie: true);

            var coverageNode = WidenDeclarationNode(node);
            result.Add((site.FilePath, coverageNode.Span));
        }

        return result;
    }

    private static SyntaxNode WidenDeclarationNode(SyntaxNode node)
    {
        // 局部/字段/事件字段声明符：拓宽到共享 VariableDeclaration（含类型位）。
        if (node is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration })
        {
            return declaration;
        }

        // 解构/out var 分量：拓宽到外层 DeclarationExpression（含显式 dynamic 类型位）。
        if (node is SingleVariableDesignationSyntax)
        {
            var declarationExpression = node.Ancestors()
                .OfType<DeclarationExpressionSyntax>()
                .FirstOrDefault();
            if (declarationExpression != null)
            {
                return declarationExpression;
            }
        }

        return node;
    }

    private static ManifestMatch? FindMatch(
        DynamicAuditManifest? manifest,
        DynamicSiteRecord site,
        IReadOnlyCollection<string> fileCandidates)
    {
        if (manifest == null)
        {
            return null;
        }

        for (var i = 0; i < manifest.Entries.Count; i++)
        {
            if (DynamicAuditManifest.MatchesSite(manifest.Entries[i], site, fileCandidates))
            {
                return new ManifestMatch(i, manifest.Entries[i]);
            }
        }

        return null;
    }

    private sealed record ManifestMatch(int Index, DynamicAuditManifestEntry Entry);

    /// <summary>manifest 所在目录即源根（D13：清单落源根、随源码受控）。</summary>
    private static string? ManifestBaseDirectory(DynamicAuditManifest? manifest)
    {
        var path = manifest?.LoadedPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 站点文件的可比候选：原值（单元测试形态 "Test.cs" 直接相等）与相对
    /// manifest 目录的源根相对路径（CLI 形态：树路径为绝对路径）。
    /// </summary>
    private static IReadOnlyCollection<string> SiteFileCandidates(
        string filePath,
        string? manifestBaseDirectory)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return [];
        }

        var candidates = new List<string> { filePath };
        if (!string.IsNullOrEmpty(manifestBaseDirectory)
            && Path.IsPathRooted(filePath))
        {
            try
            {
                var relative = Path.GetRelativePath(manifestBaseDirectory, filePath);
                if (!relative.StartsWith("..", StringComparison.Ordinal))
                {
                    candidates.Add(relative);
                }
            }
            catch (ArgumentException)
            {
                // 跨盘符等异常相对化：只用原值候选。
            }
        }

        return candidates;
    }

    private static string DisplayFile(string filePath, string? manifestBaseDirectory)
    {
        var relative = SiteFileCandidates(filePath, manifestBaseDirectory)
            .Skip(1)
            .FirstOrDefault();
        return relative ?? filePath;
    }
}
