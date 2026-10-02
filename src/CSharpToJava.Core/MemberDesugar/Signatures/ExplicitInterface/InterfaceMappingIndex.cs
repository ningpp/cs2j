using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.ExplicitInterface;

/// <summary>接口映射异常清单（设计 §4.2-2）：同槽多候选/映射缺口/同名双接口体分歧。</summary>
internal enum SlotAnomalyKind
{
    /// <summary>同一（归一化）槽位映射到多个候选实现（SI-4 → CS2J5197）。</summary>
    AmbiguousCandidate,

    /// <summary>具体类未解析到槽位实现（映射缺口）。</summary>
    MappingGap,

    /// <summary>同名不同接口的显式实现体分歧（SI-3 → CS2J5191）。</summary>
    DivergentDualInterfaceBodies,
}

/// <summary>一条显式接口实现记录（实现符号 ↔ 接口槽符号），设计 §4.2-1。</summary>
internal sealed class ExplicitImplementationEntry
{
    public required ISymbol Implementation { get; init; }
    public required ISymbol Slot { get; init; }
    public required string ContainingTypeFqn { get; init; }
    public required SignatureKind Kind { get; init; }
    public required string DeclaringFile { get; init; }
}

/// <summary>一条接口槽位映射异常。</summary>
internal sealed class SlotMappingAnomaly
{
    public required string ContainingTypeFqn { get; init; }
    public required ISymbol Slot { get; init; }
    public required SlotAnomalyKind Kind { get; init; }
    public IReadOnlyList<ISymbol> Candidates { get; init; } = Array.Empty<ISymbol>();
}

/// <summary>
/// 接口映射索引（设计 §4.2-2）：类型 → 接口 → 槽位 → 实现符号（显式 + 隐式 + 继承 +
/// 重新实现重绑），泛型接口按开放定义/构造双轨（按 <see cref="ISymbol.OriginalDefinition"/>
/// 归一化分组）。<c>SymbolEqualityComparer.Default</c> 一律用于符号同一性判定，零文本匹配。
/// </summary>
internal sealed class InterfaceMappingIndex
{
    public required IReadOnlyList<ExplicitImplementationEntry> ExplicitImplementations { get; init; }
    public required IReadOnlyList<SlotMappingAnomaly> Anomalies { get; init; }

    public static InterfaceMappingIndex Build(MemberDesugarAnalysisContext context)
    {
        var explicitEntries = BuildExplicitImplementations(context);
        var anomalies = new List<SlotMappingAnomaly>();

        foreach (var type in SignatureExplicitizationAnalyzer.SourceTypes(context))
        {
            if (type.TypeKind == TypeKind.Error) continue;
            var typeFqn = type.ToDisplayString();

            // ---- 逐槽位解析实现符号（隐式/显式/继承/重新实现统一）----
            var bySlot = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);
            foreach (var iface in type.AllInterfaces)
            {
                foreach (var member in iface.GetMembers())
                {
                    if (member is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) continue;
                    var impl = type.FindImplementationForInterfaceMember(member);
                    if (impl == null)
                    {
                        // 具体类缺失实现不存在（本就不可编译），只对非错误类型留缺口清单。
                        continue;
                    }
                    var key = member.OriginalDefinition;
                    if (!bySlot.TryGetValue(key, out var list))
                        bySlot[key] = list = new List<ISymbol>();
                    if (!list.Contains(impl, SymbolEqualityComparer.Default)) list.Add(impl);
                }
            }

            foreach (var (slot, impls) in bySlot)
            {
                if (impls.Count > 1)
                {
                    anomalies.Add(new SlotMappingAnomaly
                    {
                        ContainingTypeFqn = typeFqn,
                        Slot = slot,
                        Kind = SlotAnomalyKind.AmbiguousCandidate,
                        Candidates = impls,
                    });
                }
            }

            // ---- 同名不同接口的显式实现体分歧（SI-3）----
            var forType = explicitEntries
                .Where(e => string.Equals(e.ContainingTypeFqn, typeFqn, StringComparison.Ordinal))
                .ToList();
            foreach (var group in forType.GroupBy(e => SignatureKey(e.Slot), StringComparer.Ordinal))
            {
                var distinctInterfaces = group
                    .Select(e => e.Slot.ContainingType)
                    .Distinct(SymbolEqualityComparer.Default)
                    .ToList();
                if (distinctInterfaces.Count < 2) continue;
                var first = group.First();
                if (group.Skip(1).Any(e => !SignatureSyntaxFacts.AreBodiesEquivalent(
                        first.Implementation, e.Implementation)))
                {
                    anomalies.Add(new SlotMappingAnomaly
                    {
                        ContainingTypeFqn = typeFqn,
                        Slot = first.Slot,
                        Kind = SlotAnomalyKind.DivergentDualInterfaceBodies,
                        Candidates = group.Select(e => e.Implementation).ToList(),
                    });
                }
            }
        }

        return new InterfaceMappingIndex
        {
            ExplicitImplementations = explicitEntries,
            Anomalies = anomalies,
        };
    }

    private static List<ExplicitImplementationEntry> BuildExplicitImplementations(
        MemberDesugarAnalysisContext context)
    {
        var entries = new List<ExplicitImplementationEntry>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = model.SyntaxTree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                var symbol = ResolveDeclaredSymbol(node, model);
                if (symbol == null || !seen.Add(symbol)) continue;
                foreach (var slot in GetExplicitSlots(symbol))
                {
                    if (!slot.Locations.Any(l => l.IsInSource)) continue; // 仅源内接口
                    entries.Add(new ExplicitImplementationEntry
                    {
                        Implementation = symbol,
                        Slot = slot,
                        ContainingTypeFqn = symbol.ContainingType?.ToDisplayString() ?? symbol.Name,
                        Kind = KindOf(symbol),
                        DeclaringFile = SignatureExplicitizationAnalyzer.FirstSourceFile(symbol),
                    });
                }
            }
        }
        return entries;
    }

    private static ISymbol? ResolveDeclaredSymbol(SyntaxNode node, SemanticModel model) => node switch
    {
        MethodDeclarationSyntax m => model.GetDeclaredSymbol(m),
        PropertyDeclarationSyntax p => model.GetDeclaredSymbol(p),
        IndexerDeclarationSyntax i => model.GetDeclaredSymbol(i),
        EventDeclarationSyntax e => model.GetDeclaredSymbol(e),
        EventFieldDeclarationSyntax ef when ef.Declaration.Variables.Count > 0
            => model.GetDeclaredSymbol(ef.Declaration.Variables[0]),
        _ => null,
    };

    private static IReadOnlyList<ISymbol> GetExplicitSlots(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.ExplicitInterfaceImplementations.Cast<ISymbol>().ToList(),
        IPropertySymbol p => p.ExplicitInterfaceImplementations.Cast<ISymbol>().ToList(),
        IEventSymbol e => e.ExplicitInterfaceImplementations.Cast<ISymbol>().ToList(),
        _ => Array.Empty<ISymbol>(),
    };

    internal static SignatureKind KindOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol => SignatureKind.Method,
        IPropertySymbol { IsIndexer: true } => SignatureKind.Indexer,
        IEventSymbol => SignatureKind.Event,
        _ => SignatureKind.Property,
    };

    private static string SignatureKey(ISymbol slot) => slot switch
    {
        IMethodSymbol m => $"{m.Name}({string.Join(",", m.Parameters.Select(p => p.Type.ToDisplayString()))})",
        IPropertySymbol { IsIndexer: true } p
            => $"{p.Name}[{string.Join(",", p.Parameters.Select(x => x.Type.ToDisplayString()))}]",
        _ => slot.Name,
    };
}