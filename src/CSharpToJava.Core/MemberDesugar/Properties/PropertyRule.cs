using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.MemberDesugar.Records;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>
/// C11 property 规则（Kind=Property, Order=10）。三步：
/// 1) 声明级闸门（partial/ref/static abstract/field 关键字/位置 record/特性路由）；
/// 2) 站点扫描（读/写/复合/++--/初始化器/with/表达式树/dynamic）+ 层次联动文件；
/// 3) 后备字段命名 + PropertyPlan 产出（设计 §4.4 PD-1..PD-12）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Property, 10)]
internal sealed class PropertyRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Property;
    public int Order => 10;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var options = context.Options;

        // ---- 阶段 1：声明收集 + 声明级闸门 ----
        var candidates = new Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)>(
            SymbolEqualityComparer.Default);
        // C50：候选属性 → FK 语义信息（仅 field-backed 命中；开关关闭时永远为空）。
        var fieldKeywordInfos = new Dictionary<IPropertySymbol, FieldKeywordInfo>(
            SymbolEqualityComparer.Default);
        var failures = new Dictionary<IPropertySymbol, (PropertyDeclarationSyntax? Decl, string DeclName,
            MemberDesugarDiagnosticCode Code, string Reason)>(SymbolEqualityComparer.Default);
        var userSkipped = new Dictionary<IPropertySymbol, PropertyDeclarationSyntax>(
            SymbolEqualityComparer.Default);
        // 语义性有意保留属性 → 保留原因文案（元数据接口实现 / 镜像合并）。这些属性不展开、
        // 使用点也不改写，但必须各产一条 Retained plan：plan 是唯一记账载体，缺失会让 PD-12
        // residual 闸门把保留属性误判为「无 plan 的残留属性」而误发 CS2J5112。
        var retained = new Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, string Reason)>(
            SymbolEqualityComparer.Default);
        // C27 M2：被可行 reshape 认领的位置 record 属性（prop → reshape 契约后备字段名）。
        // 这些属性没有 PropertyDeclarationSyntax：产 Planned PropertyPlan 仅供使用点降形，
        // 声明侧（字段/get/set/ctor/Deconstruct）完全由 Records reshape 发射。
        var positionalClaims = new Dictionary<IPropertySymbol, string>(
            SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } prop || prop.IsIndexer) continue;
                if (candidates.ContainsKey(prop) || failures.ContainsKey(prop)
                    || userSkipped.ContainsKey(prop) || retained.ContainsKey(prop)) continue;

                if (MatchesSet(options.SkipQualifiedNames, prop))
                {
                    userSkipped[prop] = decl;
                    continue;
                }

                // 实现“元数据接口”（BCL/外部程序集，或不参与本次编译的项目）属性的成员
                // 不能展开成 get_X/set_X 方法：接口本体不经过本 pass 降形，展开后类不再
                // 以属性实现该接口槽（CS0535/CS0470；显式实现更是 CS0683）。显式与隐式
                // 实现同理——迭代器降级产物用 public T Current 隐式实现
                // IEnumerator<T>.Current，曾经整类被破坏。保留原属性，使用点也不改写；
                // 源内用户接口的实现随接口一起降形。
                if (PropertyFacts.ImplementsMetadataInterfaceProperty(prop))
                {
                    retained[prop] = (decl, PropertyDiagnostics.RetainedMetadataInterface(prop.Name));
                    continue;
                }

                // 跨程序集同名类型镜像合并（CLI 整树编译把不同工程/程序集的同名类型
                // 折成同一符号，基线 CS0260）：展开 get_X/set_X 会与镜像声明的同名属性
                // 撞访问器槽（CS0082/CS0111），且两侧无论展开哪一侧都撞。真实 msbuild
                // 构建按程序集隔离不存在该合并，故此处整体保留原属性不展开。
                if (MemberDesugarSyntaxFacts.IsMergedMirrorType(prop.ContainingType))
                {
                    retained[prop] = (decl, PropertyDiagnostics.RetainedMergedMirror(prop.Name));
                    continue;
                }

                // System.Attribute 派生类上的属性会被特性命名实参按名引用
                // （[XmlAttribute("source", DataType = "anyURI")] 的 DataType），
                // 命名实参在语法上是 NameEquals、不可改写为方法调用；属性一旦展开
                // 成 get_X/set_X（如 XmlAttributeAttribute.DataType），全部命名实参
                // 悬空 → CS0246。特性用法在最终 Java 侧整体丢弃，中间链尾的 C# 编译
                // 是唯一约束，特性类属性整体保留。
                if (IsAttributeClass(prop.ContainingType))
                {
                    retained[prop] = (decl, PropertyDiagnostics.RetainedAttributeClass(prop.Name));
                    continue;
                }

                var gate = DeclarationGate(decl, prop, model, options, out var fieldKeyword);
                if (gate != null)
                {
                    failures[prop] = (decl, decl.Identifier.ValueText, gate.Value.Code, gate.Value.Reason);
                    continue;
                }
                candidates[prop] = (decl, model);
                if (fieldKeyword != null) fieldKeywordInfos[prop] = fieldKeyword;
            }

            // 位置 record 参数没有 PropertyDeclarationSyntax 节点：默认 5123；C27 M2 下
            // 被可行 reshape 认领的位置属性改入 positionalClaims（Records 发射终形，本规则
            // 仅产使用点降形 plan；开关关闭时谓词恒 false，路径与 M1 逐字节一致）。
            foreach (var rec in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                if (rec.ParameterList == null) continue;
                if (model.GetDeclaredSymbol(rec) is not INamedTypeSymbol type) continue;
                if (MemberDesugarSyntaxFacts.IsMergedMirrorType(type)) continue; // 镜像合并类型不展开
                foreach (var param in rec.ParameterList.Parameters)
                {
                    var prop = type.GetMembers(param.Identifier.ValueText)
                        .OfType<IPropertySymbol>()
                        .FirstOrDefault(p => !p.IsIndexer);
                    if (prop == null || failures.ContainsKey(prop)
                        || candidates.ContainsKey(prop) || userSkipped.ContainsKey(prop)
                        || positionalClaims.ContainsKey(prop) || retained.ContainsKey(prop)) continue;
                    if (options.Records.Enabled
                        && model.Compilation is CSharpCompilation propCompilation
                        && RecordWithClaim.IsPositionalPropertyClaimed(prop, propCompilation, options))
                    {
                        var index = RecordClaimIndex.For(propCompilation);
                        positionalClaims[prop] =
                            index.TryGetPositionalFieldName(prop, out var fieldName)
                                ? fieldName
                                : throw new InvalidOperationException(
                                    "positional record property claimed without reshape material");
                        continue;
                    }
                    failures[prop] = (null, param.Identifier.ValueText,
                        MemberDesugarDiagnosticCode.PositionalRecordProperty,
                        PropertyDiagnostics.PositionalRecord(param.Identifier.ValueText));
                }
            }
        }

        // ---- 阶段 2：站点扫描 ----
        var siteFiles = new Dictionary<IPropertySymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        var siteFailures = new Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        // 站点名快筛用声明标识符（显式接口实现的符号 Name 是 "IFoo.X" 形态）；
        // 位置认领属性无声明节点，按属性符号名并入。
        var positionalProps = positionalClaims.Keys
            .ToHashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        var candidateNames = candidates.Values.Select(v => v.Decl.Identifier.ValueText)
            .Concat(positionalClaims.Keys.Select(p => p.Name))
            .ToHashSet(StringComparer.Ordinal);

        // 逐树并行：各分区只写自己的局部 siteFiles/siteFailures（读取共享的只读候选集合），
        // 随后按树序合并——siteFiles 并集、siteFailures 首现优先，与串行逐树扫描逐字节一致。
        var siteModels = context.Models;
        var partSiteFiles = new Dictionary<IPropertySymbol, HashSet<string>>[siteModels.Count];
        var partSiteFailures =
            new Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)>[siteModels.Count];

        Parallel.For(0, siteModels.Count, index =>
        {
            var model = siteModels[index];
            var filePath = model.SyntaxTree.FilePath;
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            var liftableMemo = new Dictionary<SyntaxNode, bool>();
            var fileSiteFiles = new Dictionary<IPropertySymbol, HashSet<string>>(SymbolEqualityComparer.Default);
            var fileSiteFailures =
                new Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
                    SymbolEqualityComparer.Default);

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax assign when assign.Parent is InitializerExpressionSyntax:
                        HandleInitializerAssignment(assign, model, filePath, candidates,
                            positionalProps, fileSiteFiles, fileSiteFailures, liftableMemo, options,
                            candidateNames);
                        break;

                    case AssignmentExpressionSyntax assign:
                        HandleStatementAssignment(assign, model, filePath, candidates,
                            positionalProps, fileSiteFiles, fileSiteFailures, candidateNames);
                        break;

                    case PostfixUnaryExpressionSyntax post
                        when post.IsKind(SyntaxKind.PostIncrementExpression)
                            || post.IsKind(SyntaxKind.PostDecrementExpression):
                        HandleIncrement(post.Operand, post.Parent, model, filePath, candidates,
                            positionalProps, fileSiteFiles, fileSiteFailures, candidateNames);
                        break;

                    case PrefixUnaryExpressionSyntax pre
                        when pre.IsKind(SyntaxKind.PreIncrementExpression)
                            || pre.IsKind(SyntaxKind.PreDecrementExpression):
                        HandleIncrement(pre.Operand, pre.Parent, model, filePath, candidates,
                            positionalProps, fileSiteFiles, fileSiteFailures, candidateNames);
                        break;

                    case MemberAccessExpressionSyntax ma
                        when candidateNames.Contains(ma.Name.Identifier.ValueText):
                        HandleReadCandidate(ma, model, filePath, candidates, positionalProps,
                            fileSiteFiles, fileSiteFailures, options);
                        break;

                    case MemberBindingExpressionSyntax mb
                        when candidateNames.Contains(mb.Name.Identifier.ValueText):
                        HandleReadCandidate(mb, model, filePath, candidates, positionalProps,
                            fileSiteFiles, fileSiteFailures, options);
                        break;

                    case IdentifierNameSyntax id
                        when candidateNames.Contains(id.Identifier.ValueText):
                        HandleReadCandidate(id, model, filePath, candidates, positionalProps,
                            fileSiteFiles, fileSiteFailures, options);
                        break;

                    case MemberAccessExpressionSyntax dyn
                        when candidateNames.Contains(dyn.Name.Identifier.ValueText)
                            && MemberDesugarSyntaxFacts.IsDynamic(dyn, model):
                        // dynamic 接收者（即使未绑定符号）：同名候选全部保守跳过（CS2J5118）
                        // 性能：名字快筛前置——原顺序会对全树每个非候选成员访问都做 IsDynamic
                        // （两次 GetTypeInfo）；交换合取项后语义不变但短路了绝大多数调用。
                        foreach (var c in candidates.Keys
                                     .Concat(positionalProps)
                                     .Where(c => c.Name == dyn.Name.Identifier.ValueText).ToList())
                            MarkFailure(fileSiteFailures, c,
                                MemberDesugarDiagnosticCode.DynamicSite, PropertyDiagnostics.Dynamic(c.Name));
                        break;
                }
            }

            partSiteFiles[index] = fileSiteFiles;
            partSiteFailures[index] = fileSiteFailures;
        });

        for (var index = 0; index < siteModels.Count; index++)
        {
            foreach (var (prop, files) in partSiteFiles[index])
            {
                if (!siteFiles.TryGetValue(prop, out var set))
                    siteFiles[prop] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files) set.Add(file);
            }

            foreach (var (prop, fail) in partSiteFailures[index])
                MarkFailure(siteFailures, prop, fail.Code, fail.Reason);
        }

        // ---- 阶段 3：后备字段命名 + plan 产出 ----
        // C50：field-backed 属性（IsAutoProperty=false）也必须进命名分配，名字与
        // 「同一属性若写成 auto 属性」逐字符相同（设计决策 2）。
        var fieldNames = BackingFieldNaming.Assign(
            candidates.Where(kv => siteFailures.ContainsKey(kv.Key) == false
                                   && (PropertyFacts.NeedsBackingField(kv.Value.Decl, kv.Key)
                                       || fieldKeywordInfos.ContainsKey(kv.Key)))
                .Select(kv => (kv.Key, kv.Value.Decl.Identifier.ValueText)));

        foreach (var (prop, declModel) in candidates)
        {
            var (decl, model) = declModel;
            var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in prop.Locations.Where(l => l.IsInSource))
                touched.Add(loc.SourceTree!.FilePath);
            if (siteFiles.TryGetValue(prop, out var files))
                foreach (var f in files) touched.Add(f);
            CollectHierarchyLinks(prop, context.SourceProperties, touched);

            if (userSkipped.ContainsKey(prop)) continue; // 不可达（userSkipped 不入 candidates）

            var declName = decl.Identifier.ValueText;
            if (siteFailures.TryGetValue(prop, out var fail))
            {
                builder.Add(new PropertyPlan
                {
                    Id = PropertyPlan.BuildId(prop),
                    Kind = MemberDesugarKind.Property,
                    Symbol = prop,
                    Outcome = MemberDesugarOutcome.Infeasible,
                    TouchedFiles = DeclarationFiles(prop),
                    FailCode = fail.Code,
                    FailReason = fail.Reason,
                    Property = prop,
                    DeclName = declName,
                    GetterMethodName = AccessorsToMethods.GetterName(declName),
                    SetterMethodName = AccessorsToMethods.SetterName(declName),
                    HasSetter = prop.SetMethod != null,
                    HasInitAccessor = PropertyFacts.HasInitAccessor(decl),
                    IsRequired = PropertyFacts.IsRequired(decl),
                });
                continue;
            }

            fieldNames.TryGetValue(prop, out var fieldName);
            fieldKeywordInfos.TryGetValue(prop, out var fkInfo);
            builder.Add(new PropertyPlan
            {
                Id = PropertyPlan.BuildId(prop),
                Kind = MemberDesugarKind.Property,
                Symbol = prop,
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = touched.ToList(),
                Property = prop,
                DeclName = declName,
                BackingFieldName = fieldName,
                FieldKeyword = fkInfo,
                GetterMethodName = AccessorsToMethods.GetterName(declName),
                SetterMethodName = AccessorsToMethods.SetterName(declName),
                HasSetter = prop.SetMethod != null,
                HasInitAccessor = PropertyFacts.HasInitAccessor(decl),
                IsRequired = PropertyFacts.IsRequired(decl),
            });
        }

        // C27 M2：位置认领属性产 plan（无声明节点、无声明展开；后备字段名取自 reshape
        // 物料，与 Records 终形逐字同名；HasInitAccessor/IsRequired 恒 false，class init
        // 与 struct set 均放宽为普通 set_X）。站点失败时同样产 Infeasible 留诊断。
        foreach (var prop in positionalProps)
        {
            var declName = prop.Name;
            var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in prop.Locations.Where(l => l.IsInSource))
                touched.Add(loc.SourceTree!.FilePath);
            if (siteFiles.TryGetValue(prop, out var files))
                foreach (var f in files) touched.Add(f);

            if (siteFailures.TryGetValue(prop, out var positionalFail))
            {
                builder.Add(new PropertyPlan
                {
                    Id = PropertyPlan.BuildId(prop),
                    Kind = MemberDesugarKind.Property,
                    Symbol = prop,
                    Outcome = MemberDesugarOutcome.Infeasible,
                    TouchedFiles = DeclarationFiles(prop),
                    FailCode = positionalFail.Code,
                    FailReason = positionalFail.Reason,
                    Property = prop,
                    DeclName = declName,
                    GetterMethodName = AccessorsToMethods.GetterName(declName),
                    SetterMethodName = AccessorsToMethods.SetterName(declName),
                    HasSetter = prop.SetMethod != null,
                    HasInitAccessor = false,
                    IsRequired = false,
                    IsPositionalRecordMember = true,
                });
                continue;
            }

            builder.Add(new PropertyPlan
            {
                Id = PropertyPlan.BuildId(prop),
                Kind = MemberDesugarKind.Property,
                Symbol = prop,
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = touched.ToList(),
                Property = prop,
                DeclName = declName,
                BackingFieldName = positionalClaims[prop],
                GetterMethodName = AccessorsToMethods.GetterName(declName),
                SetterMethodName = AccessorsToMethods.SetterName(declName),
                HasSetter = prop.SetMethod != null,
                HasInitAccessor = false,
                IsRequired = false,
                IsPositionalRecordMember = true,
            });
        }

        foreach (var (prop, fail) in failures)
        {
            builder.Add(new PropertyPlan
            {
                Id = PropertyPlan.BuildId(prop),
                Kind = MemberDesugarKind.Property,
                Symbol = prop,
                Outcome = MemberDesugarOutcome.Infeasible,
                TouchedFiles = DeclarationFiles(prop),
                FailCode = fail.Code,
                FailReason = fail.Reason,
                Property = prop,
                DeclName = fail.DeclName,
                GetterMethodName = AccessorsToMethods.GetterName(fail.DeclName),
                SetterMethodName = AccessorsToMethods.SetterName(fail.DeclName),
                HasSetter = prop.SetMethod != null,
                HasInitAccessor = fail.Decl != null && PropertyFacts.HasInitAccessor(fail.Decl),
                IsRequired = fail.Decl != null && PropertyFacts.IsRequired(fail.Decl),
            });
        }

        foreach (var (prop, decl) in userSkipped)
        {
            var declName = decl.Identifier.ValueText;
            builder.Add(new PropertyPlan
            {
                Id = PropertyPlan.BuildId(prop),
                Kind = MemberDesugarKind.Property,
                Symbol = prop,
                Outcome = MemberDesugarOutcome.UserSkipped,
                TouchedFiles = DeclarationFiles(prop),
                Property = prop,
                DeclName = declName,
                GetterMethodName = AccessorsToMethods.GetterName(declName),
                SetterMethodName = AccessorsToMethods.SetterName(declName),
                HasSetter = prop.SetMethod != null,
                HasInitAccessor = false,
                IsRequired = false,
            });
        }

        // 语义性有意保留：产 Retained plan（记账 + Info 诊断），声明与使用点均不动。
        // TouchedFiles 用声明文件（同 Infeasible/UserSkipped 跳过族），residual 闸门据此
        // 在 _knownKeys 中命中该属性，不再误发 CS2J5112。
        foreach (var (prop, info) in retained)
        {
            var declName = info.Decl.Identifier.ValueText;
            builder.Add(new PropertyPlan
            {
                Id = PropertyPlan.BuildId(prop),
                Kind = MemberDesugarKind.Property,
                Symbol = prop,
                Outcome = MemberDesugarOutcome.Retained,
                TouchedFiles = DeclarationFiles(prop),
                FailCode = MemberDesugarDiagnosticCode.PropertyRetained,
                FailReason = info.Reason,
                Property = prop,
                DeclName = declName,
                GetterMethodName = AccessorsToMethods.GetterName(declName),
                SetterMethodName = AccessorsToMethods.SetterName(declName),
                HasSetter = prop.SetMethod != null,
                HasInitAccessor = PropertyFacts.HasInitAccessor(info.Decl),
                IsRequired = PropertyFacts.IsRequired(info.Decl),
            });
        }
    }

    // ---- 声明级闸门（确定性顺序：5114 → 5115 → 5116 → 5122/FK → 5123 → 5113）----

    private static (MemberDesugarDiagnosticCode Code, string Reason)? DeclarationGate(
        PropertyDeclarationSyntax decl, IPropertySymbol prop, SemanticModel model,
        MemberDesugarOptions options, out FieldKeywordInfo? fieldKeyword)
    {
        fieldKeyword = null;
        if (PropertyFacts.IsPartial(decl))
            return (MemberDesugarDiagnosticCode.PartialProperty, PropertyDiagnostics.PartialProperty(prop.Name));
        if (PropertyFacts.ReturnsByRef(prop))
            return (MemberDesugarDiagnosticCode.RefReturningProperty, PropertyDiagnostics.RefReturning(prop.Name));
        if (PropertyFacts.IsStaticAbstractInterfaceMember(prop))
            return (MemberDesugarDiagnosticCode.StaticAbstractInterfaceProperty,
                PropertyDiagnostics.StaticAbstractInterface(prop.Name));

        // C50：开关开启时，FieldExpression 形态走 FK-1 语义判定（含属性级表达式体）；
        // 开关关闭时保持 C11 v1 原样（UsesFieldKeyword 现状只扫 AccessorList）→ CS2J5122。
        if (options.FieldKeyword.Enabled
            && FieldKeywordResolver.HasFieldKeywordSyntax(decl))
        {
            var resolved = FieldKeywordResolver.Resolve(decl, prop, model, options);
            if (!resolved.Success)
                return (resolved.FailCode!.Value, resolved.FailReason!);
            fieldKeyword = resolved.Info;
        }
        else if (PropertyFacts.UsesFieldKeyword(decl))
        {
            return (MemberDesugarDiagnosticCode.FieldKeywordAccessor,
                PropertyDiagnostics.FieldKeyword(prop.Name));
        }

        if (PropertyFacts.IsPositionalRecordProperty(prop))
            return (MemberDesugarDiagnosticCode.PositionalRecordProperty,
                PropertyDiagnostics.PositionalRecord(prop.Name));

        // 特性路由闸门（PD-10 / 决策 14）。C50：FK 属性归一后必有显式字段，
        // [field:] 目标列表按 fieldGenerated=true 路由（关闭时 fieldKeyword 恒 null，
        // 表达式与 C11 现状逐字节一致）。
        var fieldGenerated =
            (PropertyFacts.NeedsBackingField(decl, prop) || fieldKeyword != null)
            && !prop.IsAbstract;
        foreach (var list in decl.AttributeLists)
        {
            if (AttributeRouting.RouteList(list, model, fieldGenerated)
                == AttributeRouting.Destination.Unroutable)
            {
                var attrName = list.Attributes.FirstOrDefault()?.Name.ToString() ?? "<attribute>";
                return (MemberDesugarDiagnosticCode.AttributeUnroutable,
                    PropertyDiagnostics.AttributeUnroutable(prop.Name, attrName));
            }
        }
        return null;
    }

    // ---- 站点处理 ----

    private static void HandleInitializerAssignment(
        AssignmentExpressionSyntax assign, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        IReadOnlySet<IPropertySymbol> positionalProps,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        Dictionary<SyntaxNode, bool> liftableMemo,
        MemberDesugarOptions options,
        IReadOnlySet<string> candidateNames)
    {
        if (!TryCandidate(assign.Left, model, candidates, out var prop, positionalProps, candidateNames))
            return;

        if (assign.Ancestors().OfType<WithExpressionSyntax>().Any())
        {
            Record(siteFiles, prop, filePath);
            // C27 5124 让位钩（开放问题 6 最小收口）：Records 子集开启且该 with 站点会被
            // Records 规则改写成克隆体 set_X 时，本属性正常进候选（声明可展开为 set_X），
            // 不再判 CS2J5124。谓词与 RecordRule 共用 RecordClaimIndex，禁两侧分歧；
            // Records 关闭或站点属跳过族/嵌套初始化器时行为逐字节不变。
            if (!RecordWithClaim.IsClaimedByRecords(assign, model, options))
                MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.WithSite,
                    PropertyDiagnostics.WithSite(prop.Name));
            return;
        }

        Record(siteFiles, prop, filePath);
        var enclosing = assign.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        var liftable = enclosing != null && ComputeLiftable(enclosing, liftableMemo);
        if (!liftable)
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.NonLiftableInitializer,
                PropertyDiagnostics.NonLiftable(prop.Name));
    }

    private static void HandleStatementAssignment(
        AssignmentExpressionSyntax assign, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        IReadOnlySet<IPropertySymbol> positionalProps,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        IReadOnlySet<string> candidateNames)
    {
        // 元组/括号解构左值中的属性目标不可提升（保守，CS2J5119）
        if (assign.Left is TupleExpressionSyntax or ParenthesizedExpressionSyntax)
        {
            foreach (var expr in assign.Left.DescendantNodes().OfType<ExpressionSyntax>())
                if (TryCandidate(expr, model, candidates, out var p, positionalProps, candidateNames))
                {
                    Record(siteFiles, p, filePath);
                    MarkFailure(siteFailures, p, MemberDesugarDiagnosticCode.NonLiftableInitializer,
                        PropertyDiagnostics.NonLiftable(p.Name));
                }
            return;
        }

        if (!TryCandidate(assign.Left, model, candidates, out var prop, positionalProps, candidateNames))
            return;
        Record(siteFiles, prop, filePath);

        if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assign.Parent is not ExpressionStatementSyntax)
        {
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.NonLiftableInitializer,
                PropertyDiagnostics.NonLiftable(prop.Name));
        }
    }

    private static void HandleIncrement(
        ExpressionSyntax operand, SyntaxNode? parent, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        IReadOnlySet<IPropertySymbol> positionalProps,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        IReadOnlySet<string> candidateNames)
    {
        if (!TryCandidate(operand, model, candidates, out var prop, positionalProps, candidateNames))
            return;
        Record(siteFiles, prop, filePath);
        if (parent is not ExpressionStatementSyntax)
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.NonLiftableInitializer,
                PropertyDiagnostics.NonLiftable(prop.Name));
    }

    private static void HandleReadCandidate(
        ExpressionSyntax expr, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        IReadOnlySet<IPropertySymbol> positionalProps,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options)
    {
        if (!TryCandidate(expr, model, candidates, out var prop, positionalProps)) return;
        Record(siteFiles, prop, filePath);

        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(expr, model)
            && !MatchesSet(options.ForceQualifiedNames, prop))
        {
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.ExpressionTreeSite,
                PropertyDiagnostics.ExpressionTree(prop.Name));
            return;
        }
        if (MemberDesugarSyntaxFacts.IsDynamic(expr, model))
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.DynamicSite,
                PropertyDiagnostics.Dynamic(prop.Name));
    }

    /// <summary>对象创建的可提升性：语句位（表达式语句/单变量局部声明/块内 return），
    /// 或作为可提升初始化器赋值的直接值继承（嵌套创建递归记忆化）。</summary>
    private static bool ComputeLiftable(ObjectCreationExpressionSyntax oc, Dictionary<SyntaxNode, bool> memo)
    {
        if (memo.TryGetValue(oc, out var cached)) return cached;
        memo[oc] = false; // 环路保护（语法树无环，保险）

        bool result;
        if (IsLiftablePosition(oc))
        {
            result = true;
        }
        else if (oc.Parent is EqualsValueClauseSyntax eq
                 && eq.Parent is AssignmentExpressionSyntax a
                 && a.Parent is InitializerExpressionSyntax init
                 && init.Parent is ObjectCreationExpressionSyntax outer)
        {
            result = ComputeLiftable(outer, memo);
        }
        else if (oc.Parent is AssignmentExpressionSyntax va
                 && va.Parent is InitializerExpressionSyntax vinit
                 && vinit.Parent is ObjectCreationExpressionSyntax vouter)
        {
            // 初始化器赋值右值的嵌套创建：S = new Sub { X = 1 }，继承外层可提升性。
            result = ComputeLiftable(vouter, memo);
        }
        else if (oc.Parent is InitializerExpressionSyntax complex
            && complex.IsKind(SyntaxKind.ComplexElementInitializerExpression)
            && complex.Parent is EqualsValueClauseSyntax eq2
            && eq2.Parent is AssignmentExpressionSyntax a2
            && a2.Parent is InitializerExpressionSyntax init2
            && init2.Parent is ObjectCreationExpressionSyntax outer2)
        {
            result = ComputeLiftable(outer2, memo);
        }
        else
        {
            result = false;
        }

        memo[oc] = result;
        return result;
    }

    private static bool IsLiftablePosition(ObjectCreationExpressionSyntax oc)
    {
        switch (oc.Parent)
        {
            case ExpressionStatementSyntax:
                return true;
            case EqualsValueClauseSyntax eq when eq.Parent is VariableDeclaratorSyntax d
                && d.Parent is VariableDeclarationSyntax vd
                && vd.Variables.Count == 1
                && vd.Parent is LocalDeclarationStatementSyntax l
                && l.UsingKeyword.IsKind(SyntaxKind.None)
                && l.AwaitKeyword.IsKind(SyntaxKind.None)
                && !l.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)
                    || m.IsKind(SyntaxKind.StaticKeyword)):
                return true;
            case ReturnStatementSyntax r when r.Parent is BlockSyntax or SwitchSectionSyntax:
                return true;
            default:
                return false;
        }
    }

    // ---- 层次联动（决策 8 + 回退闭环）：override 链 / 接口实现 ----

    private static void CollectHierarchyLinks(
        IPropertySymbol prop, IReadOnlyList<IPropertySymbol> all, SortedSet<string> files)
    {
        foreach (var other in all)
        {
            if (SymbolEqualityComparer.Default.Equals(other, prop)) continue;
            if (InOverrideChain(other, prop) || InOverrideChain(prop, other)
                || other.ExplicitInterfaceImplementations.Contains(prop, SymbolEqualityComparer.Default)
                || ImplementsImplicit(other, prop))
            {
                foreach (var loc in other.Locations.Where(l => l.IsInSource))
                    files.Add(loc.SourceTree!.FilePath);
            }
        }
    }

    private static bool InOverrideChain(IPropertySymbol derived, IPropertySymbol potentialBase)
    {
        for (var b = derived.OverriddenProperty; b != null; b = b.OverriddenProperty)
            if (SymbolEqualityComparer.Default.Equals(b, potentialBase)) return true;
        return false;
    }

    private static bool ImplementsImplicit(IPropertySymbol impl, IPropertySymbol ifaceProp)
        => ifaceProp.ContainingType is { TypeKind: TypeKind.Interface }
           && impl.ContainingType != null
           && SymbolEqualityComparer.Default.Equals(
               impl.ContainingType.FindImplementationForInterfaceMember(ifaceProp), impl);

    /// <summary>包含类型是否为 System.Attribute 派生类（沿基类链判定）。</summary>
    private static bool IsAttributeClass(ITypeSymbol? type)
    {
        for (var b = type?.BaseType; b != null; b = b.BaseType)
        {
            if (b is INamedTypeSymbol named
                && named.Name == "Attribute"
                && named.ContainingNamespace?.ToDisplayString() == "System")
            {
                return true;
            }
        }
        return false;
    }

    // ---- 小工具 ----

    internal static bool TryCandidate(
        ExpressionSyntax node, SemanticModel model,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        out IPropertySymbol prop,
        IReadOnlySet<IPropertySymbol>? positionalProps = null,
        IReadOnlySet<string>? nameFilter = null)
    {
        prop = null!;
        // 性能：名字快筛。候选 key 用【声明标识符文本】（显式接口实现的符号 Name 是
        // "IFoo.X" 形态，故不能用 Name）；站点处的成员名恒等于声明标识符（显式实现无法被
        // 简单名引用）。名字不在候选集合内者不可能命中，直接跳过 GetSymbolInfo。
        // 未知形态（非三种可判定形态）保守放行。
        if (node is IdentifierNameSyntax or MemberAccessExpressionSyntax or MemberBindingExpressionSyntax)
        {
            if (nameFilter != null)
            {
                var name = node switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                    _ => ((MemberBindingExpressionSyntax)node).Name.Identifier.ValueText,
                };
                if (!nameFilter.Contains(name)) return false;
            }
        }

        if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or MemberBindingExpressionSyntax))
            return false;
        if (model.GetSymbolInfo(node).Symbol is not IPropertySymbol p) return false;
        // 构造泛型（Box<string>.Value）要归一到定义符号（Box<T>.Value）才能命中候选。
        if (!p.IsDefinition) p = p.OriginalDefinition;
        if (candidates.ContainsKey(p) || positionalProps?.Contains(p) == true)
        {
            prop = p;
            return true;
        }
        return false;
    }

    private static void Record(
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles, IPropertySymbol prop, string filePath)
    {
        if (!siteFiles.TryGetValue(prop, out var set))
            siteFiles[prop] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(filePath);
    }

    private static void MarkFailure(
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        IPropertySymbol prop, MemberDesugarDiagnosticCode code, string reason)
        => failures.TryAdd(prop, (code, reason));

    private static bool MatchesSet(IReadOnlySet<string> names, IPropertySymbol prop)
    {
        var type = prop.ContainingType?.ToDisplayString();
        return names.Contains($"{type}::{prop.Name}")
            || (type != null && names.Contains(type));
    }

    private static IReadOnlyList<string> DeclarationFiles(IPropertySymbol prop)
        => prop.Locations.Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>PD-10 / 决策 14：按 AttributeUsage 的目标感知路由。</summary>
internal static class AttributeRouting
{
    public enum Destination
    {
        Field,
        Methods,
        Getter,
        Unroutable,
    }

    public static Destination RouteList(AttributeListSyntax list, SemanticModel model, bool fieldGenerated)
    {
        var target = list.Target?.Identifier.ValueText;
        return target switch
        {
            "field" => fieldGenerated ? Destination.Field : RouteByUsage(list, model, allowField: false),
            "method" => Destination.Methods,
            "return" => Destination.Getter,
            _ => RouteByUsage(list, model, allowField: fieldGenerated),
        };
    }

    private static Destination RouteByUsage(AttributeListSyntax list, SemanticModel model, bool allowField)
    {
        var allField = list.Attributes.Count > 0 && allowField;
        var anyMethod = false;
        foreach (var attr in list.Attributes)
        {
            var usage = GetValidOn(model, attr);
            if (!usage.HasFlag(AttributeTargets.Field)) allField = false;
            if (usage.HasFlag(AttributeTargets.Method)) anyMethod = true;
        }
        if (allField) return Destination.Field;
        if (anyMethod) return Destination.Methods;
        return Destination.Unroutable;
    }

    public static AttributeTargets GetValidOn(SemanticModel model, AttributeSyntax attr)
    {
        var attrClass = (model.GetSymbolInfo(attr).Symbol as IMethodSymbol)?.ContainingType;
        if (attrClass == null) return AttributeTargets.All;
        var usage = attrClass.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass is { Name: "AttributeUsageAttribute" }
            && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "System");
        if (usage != null && usage.ConstructorArguments.Length > 0
            && usage.ConstructorArguments[0].Value is int v)
            return (AttributeTargets)v;
        return AttributeTargets.All;
    }
}
