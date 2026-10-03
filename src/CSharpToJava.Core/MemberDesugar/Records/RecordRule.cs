using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
            // 组件闸门（含位置 reshape 闸门）时不发任何工厂；候选类型以 GateCode 留诊断。
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
            // 仅可行类型携带 reshape 物料（闸门类型 TryGetShape 必为 false）。
            PositionalShape? shape = null;
            if (claim is { Feasible: true }) index.TryGetShape(type, out shape);

            // 类型闸门码：认领结论不可行（5220/5221/缺复制 ctor 等）直接采用；
            // 无认领结论（位置 record/struct/非 record class 等形态排除，或本站点全部
            // 本地失败 5223/5224/…）时取首个失败站点码作整 plan 门码——否则站点会被
            // 重写期误当可行站点改写（原子性铁律：门码非 null，声明与站点全不动）。
            MemberDesugarDiagnosticCode? gateCode = null;
            string? gateReason = null;
            if (claim is { Feasible: false } bad)
            {
                gateCode = bad.FailCode;
                gateReason = bad.FailReason;
            }
            else if (claim is not { Feasible: true })
            {
                var firstBad = sites.FirstOrDefault(s => s.FailCode != null);
                if (firstBad != null)
                {
                    gateCode = firstBad.FailCode;
                    gateReason = firstBad.FailReason;
                }
                else
                {
                    gateCode = MemberDesugarDiagnosticCode.NonRecordClassWith;
                    gateReason = RecordDiagnostics.NonRecordClassWith(type.ToDisplayString(),
                        "no feasible record claim was produced for the with receiver type");
                }
            }

            // reshape 类型的工厂/reshape 注入点统一锚定带 ParameterList 的部件标识符
            // （partial 多部件中唯一），保证同遍同部件完成。
            var insertSpan = shape?.Part.Identifier.SpanStart ?? span;
            builder.Add(new RecordClonePlan
            {
                Id = RecordClonePlan.BuildId(type, insertSpan),
                Kind = MemberDesugarKind.Record,
                Symbol = type,
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = touched.ToList(),
                RecordType = type,
                IsOrphan = false,
                Reshape = shape,
                FactoryForm = factory.Form,
                FactoryName = factory.Name,
                FactoryAlreadyExists = factory.AlreadyExists,
                FactoryInsertSpanStart = insertSpan,
                FactoryRequired = factory.Name != null,
                GateCode = gateCode,
                GateReason = gateReason,
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

        // ---- 5. 纯工厂角色的闭包成员（自身无 with 站点，派生链/虚分派需要其工厂；
        // M2 起也含连带 reshape 的位置基类：注入点锚到带参数表部件的标识符）----
        foreach (var (type, factory) in factoryByType)
        {
            if (sitesByType.ContainsKey(type)) continue;
            var touched = type.Locations.Where(l => l.IsInSource)
                .Select(l => l.SourceTree!.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var span = type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
            PositionalShape? shape = null;
            if (index.TryGetTypeClaim(type, out var closureClaim)
                && closureClaim.Feasible)
            {
                index.TryGetShape(type, out shape);
            }
            var insertSpan = shape?.Part.Identifier.SpanStart ?? span;
            builder.Add(new RecordClonePlan
            {
                Id = RecordClonePlan.BuildId(type, insertSpan),
                Kind = MemberDesugarKind.Record,
                Symbol = type,
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = touched.ToList(),
                RecordType = type,
                IsOrphan = false,
                Reshape = shape,
                FactoryForm = factory.Form,
                FactoryName = factory.Name,
                FactoryAlreadyExists = factory.AlreadyExists,
                FactoryInsertSpanStart = insertSpan,
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
