using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures;

/// <summary>整树一次签名分析结果（设计 §4.2）：四索引 + 按文件审计计数 + 门控站点清点。</summary>
internal sealed class SignatureAnalysisResult
{
    public required ExplicitInterface.InterfaceMappingIndex InterfaceMapping { get; init; }
    public required Hiding.HideGroupResolver Hiding { get; init; }
    public required Covariant.CovariantChainAnalyzer Covariant { get; init; }

    /// <summary>按声明文件分桶的审计计数（重写期并入族统计，镜像 MetadataOperatorSitesByFile）。</summary>
    public required Dictionary<string, SignatureStatistics> AuditByFile { get; init; }

    /// <summary>反射/字符串契约风险站点文件集合（M1 清点，M2 发 CS2J5193）。</summary>
    public required IReadOnlySet<string> ReflectionRiskFiles { get; init; }

    /// <summary>存在表达式树内/dynamic 不可判定站点（M2 发 CS2J5194）。</summary>
    public required bool HasUndecidableSites { get; init; }
}

/// <summary>
/// C46 整树签名分析器（设计 §4.2）：BindCompilation 后一次建立四张索引并派生按文件审计计数。
/// M1 只读审计（不产 plan、不改树）；M2+ 的 SI/NH/CR 规则只读复用同一索引。
/// <c>Signatures.Enabled=false</c> 时由调用方（<see cref="SignatureAuditRule"/>）提前返回，本类不执行。
/// </summary>
internal static class SignatureExplicitizationAnalyzer
{
    public static SignatureAnalysisResult Analyze(MemberDesugarAnalysisContext context)
    {
        var options = context.Options.Signatures;
        // 性能：三张索引互不依赖且只读 context，并发建立（原串行 ~14s，并发 ~max）。 
        ExplicitInterface.InterfaceMappingIndex interfaceMapping = null!;
        Hiding.HideGroupResolver hiding = null!;
        Covariant.CovariantChainAnalyzer covariant = null!;
        System.Threading.Tasks.Parallel.Invoke(
            () => interfaceMapping = ExplicitInterface.InterfaceMappingIndex.Build(context),
            () => hiding = Hiding.HideGroupResolver.Build(context),
            () => covariant = Covariant.CovariantChainAnalyzer.Build(context));

        var byFile = new Dictionary<string, SignatureStatistics>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in interfaceMapping.ExplicitImplementations)
        {
            if (options.IsSkipped(entry.Implementation)) continue;
            var stats = For(byFile, entry.DeclaringFile);
            stats.ExplicitMembersDetected++;
            switch (entry.Kind)
            {
                case SignatureKind.Method: stats.ExplicitMethods++; break;
                case SignatureKind.Property: stats.ExplicitProperties++; break;
                case SignatureKind.Indexer: stats.ExplicitIndexers++; break;
                case SignatureKind.Event: stats.ExplicitEvents++; break;
            }
        }

        foreach (var hidden in hiding.HiddenMembers)
        {
            if (options.IsSkipped(hidden.Member)) continue;
            var stats = For(byFile, hidden.DeclaringFile);
            stats.HidingDetected++;
            switch (hidden.Flavor)
            {
                case Hiding.HideFlavor.New: stats.HidingNew++; break;
                case Hiding.HideFlavor.Implicit: stats.HidingImplicit++; break;
                case Hiding.HideFlavor.NewSlot: stats.HidingNewSlot++; break;
                case Hiding.HideFlavor.Static: stats.HidingStatic++; break;
            }
        }

        foreach (var chain in covariant.Chains)
        {
            if (options.IsSkipped(chain.Member)) continue;
            var stats = For(byFile, chain.DeclaringFile);
            stats.CovariantChains++;
            switch (chain.Verdict)
            {
                case Covariant.CovariantVerdict.Retained: stats.CovariantRetained++; break;
                case Covariant.CovariantVerdict.Bridged: stats.CovariantBridged++; break;
                case Covariant.CovariantVerdict.ValueTypeSkipped: stats.CovariantValueTypeSkipped++; break;
            }
        }

        // 性能：ScanGateSites 的结果（ReflectionRiskFiles/HasUndecidableSites）当前无任何消费者
        // （M2 才发 CS2J5193/5194），却要遍历全部模型逐节点做语义查询。此处不计算，保持
        // 结果字段为空/false——与「结果未被读取」逐字节等价。
        return new SignatureAnalysisResult
        {
            InterfaceMapping = interfaceMapping,
            Hiding = hiding,
            Covariant = covariant,
            AuditByFile = byFile,
            ReflectionRiskFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            HasUndecidableSites = false,
        };
    }

    // ---- 共享符号/类型枚举（各索引 Builder 复用）----

    /// <summary>全部源内命名的类型符号（partial 多部件按符号去重；含嵌套类型）。</summary>
    internal static IEnumerable<INamedTypeSymbol> SourceTypes(MemberDesugarAnalysisContext context)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var model in context.Models)
        {
            var root = model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type) continue;
                if (seen.Add(type)) yield return type;
            }
        }
    }

    /// <summary>符号的首个源内声明文件（跨部件取首个，避免重复计数）。</summary>
    internal static string FirstSourceFile(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath
           ?? string.Empty;

    // ---- 门控站点清点（M1 审计，M2 发码）----
    // 性能：ScanGateSites 已移除——其结果字段无消费者，逐节点语义扫描纯属浪费
    // （原扫描覆盖全部模型的所有 MemberAccess/Invocation 节点）。

    private static SignatureStatistics For(
        Dictionary<string, SignatureStatistics> byFile, string filePath)
    {
        if (!byFile.TryGetValue(filePath, out var stats))
            byFile[filePath] = stats = new SignatureStatistics();
        return stats;
    }
}