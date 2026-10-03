using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>
/// C27 record `with` 去糖规则（Kind=Record, Order=5；设计 §4.2/§4.3，M1 范围
/// RC-1/RC-2 + WR-1/2/3/6/7）。分析期只产出 <see cref="RecordClonePlan"/> 列表
/// （声明闭包 + with 站点），重写期（后续提交接线）由
/// RecordDeclarationRewriter/WithExpressionRewriter 消费。注意规则发现序中 Record
/// 排在 Property 之后；「Records 先于 property」靠重写期显式阶段序 + PropertyRule
/// 经共享 <see cref="RecordClaimIndex"/> 提前读到认领结论共同实现。
/// 开关 <see cref="RecordOptions.Enabled"/>=false 时零枚举零 plan（默认关零行为）。
/// 全部判定经 <see cref="RecordClaimIndex"/> 走 SemanticModel，与 PropertyRule 5124
/// 让位钩共用同一结论（禁文本替换、禁两侧分歧）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Record, 5)]
internal sealed class RecordRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Record;
    public int Order => 5;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        if (!context.Options.Records.Enabled) return;

        var compilation = context.Compilation;
        var index = RecordClaimIndex.For(compilation);
        if (index.IsEmpty) return;

        // ---- 1. 源内有接收者类型的站点按类型分组（含 struct/位置/非 record 跳过族）----
        var sitesByType = new Dictionary<INamedTypeSymbol, List<WithSite>>(
            SymbolEqualityComparer.Default);
        var orphanSitesByFile = new SortedDictionary<string, List<WithSite>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var site in index.Sites
                     .Select(s => s.ToWithSite())
                     .OrderBy(s => s.FilePath, StringComparer.Ordinal)
                     .ThenBy(s => s.SpanStart))
        {
            if (site.ReceiverInSource && site.ReceiverType != null)
            {
                // 构造泛型归一 OriginalDefinition（Box<int>/Box<string> 同一声明一条 plan）。
                var key = site.ReceiverType.OriginalDefinition;
                if (!sitesByType.TryGetValue(key, out var list))
                    sitesByType[key] = list = new List<WithSite>();
                list.Add(site);
            }
            else if (!orphanSitesByFile.TryGetValue(site.FilePath, out var list))
            {
                orphanSitesByFile[site.FilePath] = list = new List<WithSite>();
                list.Add(site);
            }
            else
            {
                list.Add(site);
            }
        }

        // ---- 2. 继承链组件 → 统一工厂定名（决策 2）----
        var factoryByType = new Dictionary<INamedTypeSymbol, FactoryDecision>(
            SymbolEqualityComparer.Default);
        foreach (var component in index.Components)
        {
            // 组件闸门（M1：含位置 record）时不发任何工厂；候选类型以 GateCode 留诊断。
            if (component.GateCode != null) continue;

            var members = component.Members
                .Where(CloneCtorResolver.IsRecordClass)
                .ToList();
            var (name, alreadyExists) = CloneCtorResolver.ResolveFactoryName(members);
            foreach (var type in members)
            {
                factoryByType[type] = new FactoryDecision
                {
                    Name = name,
                    Form = CloneCtorResolver.ResolveFactoryForm(type),
                    AlreadyExists = alreadyExists.Contains(type.ToDisplayString()),
                };
            }
        }

        // ---- 3. 每个源内接收者类型一条 plan ----
        foreach (var (type, sites) in sitesByType)
        {
            var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in type.Locations.Where(l => l.IsInSource))
                touched.Add(loc.SourceTree!.FilePath);
            foreach (var s in sites) touched.Add(s.FilePath);

            var span = type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start
                       ?? sites[0].SpanStart;

            index.TryGetTypeClaim(type, out var claim);
            factoryByType.TryGetValue(type, out var factory);

            builder.Add(new RecordClonePlan
            {
                Id = RecordClonePlan.BuildId(type, span),
                Kind = MemberDesugarKind.Record,
                Symbol = type,
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = touched.ToList(),
                RecordType = type,
                IsOrphan = false,
                IsPositional = CloneCtorResolver.IsPositional(type),
                FactoryForm = factory.Form,
                FactoryName = factory.Name,
                FactoryAlreadyExists = factory.AlreadyExists,
                FactoryRequired = factory.Name != null,
                GateCode = claim is { Feasible: false } ? claim.FailCode : null,
                GateReason = claim is { Feasible: false } ? claim.FailReason : null,
                Sites = sites,
            });
        }

        // ---- 4. 无源码接收者站点：逐文件载体 plan（5222/5225/5226/5227/5220 元组等）----
        foreach (var (filePath, sites) in orphanSitesByFile)
        {
            builder.Add(new RecordClonePlan
            {
                Id = RecordClonePlan.BuildOrphanId(filePath, sites.Select(s => s.SpanStart)),
                Kind = MemberDesugarKind.Record,
                Symbol = compilation.Assembly,
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = new[] { filePath },
                RecordType = null,
                IsOrphan = true,
                FactoryRequired = false,
                Sites = sites,
            });
        }

        // ---- 5. 纯工厂角色的闭包成员（自身无 with 站点，派生链/虚分派需要其工厂）----
        foreach (var (type, factory) in factoryByType)
        {
            if (sitesByType.ContainsKey(type)) continue;
            var touched = type.Locations.Where(l => l.IsInSource)
                .Select(l => l.SourceTree!.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var span = type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
            builder.Add(new RecordClonePlan
            {
                Id = RecordClonePlan.BuildId(type, span),
                Kind = MemberDesugarKind.Record,
                Symbol = type,
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = touched.ToList(),
                RecordType = type,
                IsOrphan = false,
                IsPositional = false,
                FactoryForm = factory.Form,
                FactoryName = factory.Name,
                FactoryAlreadyExists = factory.AlreadyExists,
                FactoryRequired = true,
                Sites = Array.Empty<WithSite>(),
            });
        }
    }

    private struct FactoryDecision
    {
        public string Name;
        public RecordFactoryForm? Form;
        public bool AlreadyExists;
    }
}
