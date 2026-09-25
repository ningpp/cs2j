using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.RecordReadOnly;

internal enum PlanOutcome { Converted, AlreadyReadOnly, UserSkipped, Infeasible }

internal enum SiteActionKind { WithAssign, MethodMigrateCall, InterfaceWithCall }

internal enum MemberRewriteKind
{
    SetToInit,                    // RS-3/RS-4/RS-9/RS-20、RC-2/RC-5
    FieldToInitProperty,          // RS-5、RC-3（对外可见字段）
    PrivateFieldReadonly,         // RS-6a
    PrivateFieldToInitProperty,   // RS-6b（配合 MM）
}

[Flags]
internal enum MethodMigrationKind
{
    VoidToRecord = 1,
    NonVoidToOut = 2,
    Extension = 4,
}

internal sealed record MemberRewrite(string MemberName, MemberRewriteKind Kind, ISymbol Symbol, bool IsRequired);

internal sealed record MethodMigrationPlan(
    IMethodSymbol Method,
    MethodMigrationKind Kind,
    IReadOnlyList<ISymbol> PromotedMembers);

/// <summary>
/// 接口连带中“接口新 With 成员 ↔ record 被替代成员”的一条映射。
/// 属性触发：InterfaceMember/RecordMember 均为 IPropertySymbol；
/// 方法触发（Task 21）：均为 IMethodSymbol。
/// </summary>
internal sealed record RippleMember(
    INamedTypeSymbol Interface,
    ISymbol InterfaceMember,
    ISymbol RecordMember,
    string WithName,           // 最终生成名（碰撞时含 __cs2j 后缀）
    string OriginalWithName,   // 碰撞前的原名（WithX），用于诊断与去重
    bool IsMethod,
    bool ReturnsVoid);

internal sealed record InterfaceRipplePlan(
    INamedTypeSymbol Interface,
    IReadOnlyList<RippleMember> Members,
    IReadOnlyList<InterfaceFillPlan> Fills,
    bool StubOnly = false);

/// <summary>
/// IR-3：一个非 record-owner 的 in-tree 接口实现者需要补齐的 fluent 成员。
/// Implementer 可能是 class、struct、record class，或本轮未作为 owner 转换的 record。
/// </summary>
internal sealed record InterfaceFillPlan(
    INamedTypeSymbol Implementer,
    IReadOnlyList<RippleMember> Members);

internal sealed record SitePlan(MutationSiteInfo Site, SiteActionKind Action);

internal sealed record PlanTouch(string File, string? TypeName, string? MemberName);

/// <summary>随里程碑推进逐项打开；未开能力对应的 record 计划判 Infeasible(RR020/RR070)。</summary>
internal sealed record FeatureCaps(
    bool ReadOnlyModifier,
    bool MemberRewrites,
    bool MethodMigration,
    bool InterfaceRipple)
{
    // Task 6：readonly 修饰符 + 声明成员改写（set→init、字段→init属性、私有字段 readonly）。
    // Task 11：用点分类始终执行（分类不通过即精确诊断码阻断）。
    // 后续任务把对应项改为 true。
    public static FeatureCaps Current => new(
        ReadOnlyModifier: true,
        MemberRewrites: true,
        MethodMigration: true,
        InterfaceRipple: true);}

internal sealed class RecordPlan
{
    public required string Id { get; init; }
    public required string RecordShortName { get; init; }
    public required string RecordQualifiedName { get; init; }
    public required RecordKind Kind { get; init; }
    public required INamedTypeSymbol Symbol { get; init; }
    public PlanOutcome Outcome { get; set; }
    public RecordReadOnlyDiagnosticCode? FailCode { get; set; }
    public string? FailReason { get; set; }
    public Location? FailLocation { get; set; }
    public bool Feasible => Outcome == PlanOutcome.Converted;

    public List<MemberRewrite> MemberRewrites { get; } = new();
    public List<MethodMigrationPlan> Migrations { get; } = new();
    public List<InterfaceRipplePlan> Ripples { get; } = new();
    public List<SitePlan> Sites { get; } = new();
    public HashSet<string> RequiredRecordIds { get; } = new(StringComparer.Ordinal);

    public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PlanTouch> Touches { get; } = new();

    /// <summary>ReportSkippedRecords 每文件只计一次。</summary>
    public bool Reported { get; set; }

    public bool OwnsTouch(string file, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(file)) return false;
        if (typeName == null) return true;
        if (typeName == RecordShortName) return true;
        return memberName != null
            && Touches.Any(t => string.Equals(t.MemberName, memberName, StringComparison.Ordinal));
    }

    public RecordReadOnlyDiagnostic InfoDiagnostic(
        RecordReadOnlyDiagnosticCode code, string message)
    {
        var (file, line, col) = Locate();
        return new RecordReadOnlyDiagnostic(
            RecordReadOnlySeverity.Info, code, message, file, line, col, RecordQualifiedName);
    }

    public RecordReadOnlyDiagnostic FailDiagnostic()
    {
        var loc = FailLocation;
        var file = loc?.SourceTree?.FilePath;
        var span = loc?.GetLineSpan().StartLinePosition;
        return new RecordReadOnlyDiagnostic(
            RecordReadOnlySeverity.Warning, FailCode!.Value, FailReason ?? "record cannot be converted.",
            file, (span?.Line ?? 0) + 1, (span?.Character ?? 0) + 1, RecordQualifiedName);
    }

    private (string? File, int Line, int Column) Locate()
    {
        var loc = Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var span = loc?.GetLineSpan().StartLinePosition;
        return (loc?.SourceTree?.FilePath, (span?.Line ?? 0) + 1, (span?.Character ?? 0) + 1);
    }
}

internal sealed class ConversionPlan
{
    public List<RecordPlan> Plans { get; } = new();
}

internal sealed class PlanBuilder
{
    private readonly RecordReadOnlyOptions _options;
    private readonly FeatureCaps _caps;

    public PlanBuilder(RecordReadOnlyOptions options, FeatureCaps? caps = null)
    {
        _options = options;
        _caps = caps ?? FeatureCaps.Current;
    }

    public ConversionPlan Build(RecordAnalysis analysis)
    {
        var plan = new ConversionPlan();
        foreach (var info in analysis.Records.OrderBy(r => r.Symbol.ToDisplayString(), StringComparer.Ordinal))
        {
            plan.Plans.Add(BuildRecordPlan(info, analysis));
        }
        EnforceHierarchyConsistency(plan);
        return plan;
    }

    /// <summary>
    /// RC-8/FB-2：record class 继承层次（基类 record ↔ 派生 record）必须整层一致。
    /// 层次中任一 record 不可转换 → 层次内所有 Converted 计划降级为 Infeasible(RR021)。
    /// 按 BaseType 链做连通分量分组（允许中间夹非 record 类）。
    /// </summary>
    private static void EnforceHierarchyConsistency(ConversionPlan plan)
    {
        var classPlans = plan.Plans.Where(p => p.Kind == RecordKind.Class).ToList();
        if (classPlans.Count == 0) return;
        var bySymbol = classPlans.ToDictionary(
            p => p.Symbol, p => p, SymbolEqualityComparer.Default);
        var visited = new HashSet<RecordPlan>();

        foreach (var root in classPlans)
        {
            if (visited.Contains(root)) continue;
            var component = new HashSet<RecordPlan>();
            var queue = new Queue<RecordPlan>();
            queue.Enqueue(root);
            visited.Add(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);

                // 基类方向：沿 BaseType 链向上找同链 record class。
                for (var t = current.Symbol.BaseType; t != null; t = t.BaseType)
                {
                    if (!t.IsRecord || t.TypeKind != TypeKind.Class) continue;
                    if (bySymbol.TryGetValue(t, out var basePlan)
                        && visited.Add(basePlan))
                        queue.Enqueue(basePlan);
                }
                // 派生方向：直接基类是本 record 的 record class。
                foreach (var derived in classPlans)
                {
                    if (visited.Contains(derived)) continue;
                    if (SymbolEqualityComparer.Default.Equals(
                            derived.Symbol.BaseType, current.Symbol))
                        queue.Enqueue(derived);
                }
            }

            var anyBlocked = component.Any(p =>
                p.Outcome is PlanOutcome.Infeasible or PlanOutcome.UserSkipped);
            if (!anyBlocked) continue;
            foreach (var p in component.Where(p => p.Outcome == PlanOutcome.Converted))
            {
                p.Outcome = PlanOutcome.Infeasible;
                p.FailCode = RecordReadOnlyDiagnosticCode.RR021;
                p.FailReason = "record class hierarchy is inconsistent: a base or derived " +
                    "record in the same hierarchy was skipped; the entire hierarchy must be " +
                    "converted together (RC-8/FB-2).";
                p.FailLocation = p.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
            }
        }
    }

    private RecordPlan BuildRecordPlan(RecordInfo info, RecordAnalysis analysis)
    {
        var qn = info.Symbol.ToDisplayString();
        var plan = new RecordPlan
        {
            Id = "RR:" + qn,
            RecordShortName = info.Symbol.Name,
            RecordQualifiedName = qn,
            Kind = info.Kind,
            Symbol = info.Symbol,
        };
        foreach (var part in info.Parts)
        {
            var file = part.SyntaxTree.FilePath;
            plan.TouchedFiles.Add(file);
            plan.Touches.Add(new PlanTouch(file, info.Symbol.Name, null));
        }
        // Fallback: if Parts was empty (e.g. DeclaringSyntaxReferences didn't yield
        // RecordDeclarationSyntax), use symbol.Locations to populate TouchedFiles.
        if (plan.TouchedFiles.Count == 0)
        {
            foreach (var loc in info.Symbol.Locations.Where(l => l.IsInSource))
            {
                var f = loc.SourceTree?.FilePath ?? "";
                plan.TouchedFiles.Add(f);
                plan.Touches.Add(new PlanTouch(f, info.Symbol.Name, null));
            }
        }

        if (_options.SkipQualifiedNames.Contains(qn))
        {
            plan.Outcome = PlanOutcome.UserSkipped;
            return plan;
        }
        if (info.AlreadyReadonly)
        {
            plan.Outcome = PlanOutcome.AlreadyReadOnly;
            return plan;
        }

        var forced = _options.ForceQualifiedNames.Contains(qn);

        // IR-6：泛型接口方法（方法形态）保守 RR070——With 名上的类型实参、泛型 fill、
        // 显式泛型实现的形状未解。无论方法是否 mutating 都要阻断，否则转换后旧契约丢失。
        foreach (var method in info.InTreeMethods)
        {
            if (method.MethodKind is not (MethodKind.Ordinary
                or MethodKind.ExplicitInterfaceImplementation))
                continue;
            foreach (var (iface, interfaceMethod) in
                     InterfaceMethodImplementations(info.Symbol, method))
            {
                if (interfaceMethod.TypeParameters.Length == 0) continue;
                var memberLocation = interfaceMethod.Locations.FirstOrDefault(l => l.IsInSource)
                    ?? info.Symbol.Locations.First(l => l.IsInSource);
                return FailAt(plan, RecordReadOnlyDiagnosticCode.RR070,
                    $"interface method '{iface.ToDisplayString()}.{interfaceMethod.Name}' " +
                    "declares type parameters; generic interface-member ripple is not " +
                    "supported yet (IR-6, deferred to a later task).",
                    memberLocation);
            }
        }

        // 属性 set→init（RS-3/RS-4/RS-9/RS-20；RC-2/RC-5 在 M5 复用同一条目）。
        // 命中接口 set（RS-14）的公共属性照常 set→init；旧接口契约改由 IR-2 显式桩满足，
        // 触发信息收集到 rippleProperties，本方法末统一规划 IR-1/IR-2/IR-4。
        // positional record struct 的位置参数属性由 readonly 修饰符自动转 set→init，不算显式重写。
        var positionalNames = info.IsPositional
            ? new HashSet<string>(info.Parts
                .SelectMany(p => p.ParameterList?.Parameters ?? default)
                .Select(p => p.Identifier.Text), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var rippleProperties =
            new List<(IPropertySymbol RecordProperty, INamedTypeSymbol Interface,
                IPropertySymbol InterfaceProperty)>();
        // 属性触发与方法触发的接口连带成员统一收集，方法块结束后一次性物化（IR-3 fills
        // 需要看到合并后的完整成员集）。
        var rippleMembersByInterface =
            new Dictionary<INamedTypeSymbol, List<RippleMember>>(SymbolEqualityComparer.Default);
        // FB-3 force 桩降级的接口：物化时生成 StubOnly ripple（record 侧只保留旧契约桩）。
        var forcedStubInterfaces =
            new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var property in info.SettableProperties)
        {
            var implementations = InterfaceSetterImplementations(info.Symbol, property);
            foreach (var (iface, interfaceProperty) in implementations)
            {
                rippleProperties.Add((property, iface, interfaceProperty));
            }
            // readonly 修饰符自动处理位置参数的 set→init；但命中接口 set 的位置属性
            // 仍需 MemberRewrite（显式桩/IR-2 路径依赖该条目）。
            if (positionalNames.Contains(property.Name) && implementations.Count == 0)
                continue;
            plan.MemberRewrites.Add(new MemberRewrite(
                property.Name, MemberRewriteKind.SetToInit, property, property.IsRequired));
        }

        // 字段分类（RS-5/RS-6）。
        foreach (var field in info.MutableFields.Where(f => !f.IsImplicitlyDeclared))
        {
            if (RecordSyntaxFacts.IsVisibleOutsideType(field))
            {
                if (info.HasExplicitLayout)
                    return Fail(plan, RecordReadOnlyDiagnosticCode.RR043,
                        "StructLayout(LayoutKind.Explicit) with a field promoted to property loses " +
                        "layout semantics (RS-17).");
                plan.MemberRewrites.Add(new MemberRewrite(
                    field.Name, MemberRewriteKind.FieldToInitProperty, field, field.IsRequired));
            }
            else if (info.Kind == RecordKind.Class)
            {
                // RC-4：record class 整体私有字段保留可变（惰性缓存等内部状态），
                // 不加 readonly、不提升属性，方法体内的写入也无需迁移。
                continue;
            }
            else if (IsPrivateFieldWrittenAfterConstruction(info, field))
            {
                if (!_caps.MethodMigration)
                    return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                        $"private field '{field.Name}' is assigned after construction; method " +
                        "migration is required to convert it (RS-6b/MM).");
                plan.MemberRewrites.Add(new MemberRewrite(
                    field.Name, MemberRewriteKind.PrivateFieldToInitProperty, field, false));
            }
            else
            {
                plan.MemberRewrites.Add(new MemberRewrite(
                    field.Name, MemberRewriteKind.PrivateFieldReadonly, field, false));
            }
        }

        // 声明级硬阻断（force 也不绕过：这些形态无法生成可编译结果）。
        // 在收集 MemberRewrites 之后检查，让测试能验证成员分类事实。
        if (info.HasInstanceEvent)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR040,
                "instance events cannot exist on a readonly record (RS-10).");
        if (info.HasFixedSizeBuffer || info.HasVisibleRefReturningMember
            || info.MutableFields.Any(f => f.IsVolatile))
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR041,
                "volatile instance field, ref-returning visible member, or fixed-size buffer " +
                "cannot be converted (RS-11/RC-5).");
        if (info.HasSettableIndexer)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR042,
                "indexer with a set accessor cannot be expressed via 'with' (RS-19/RC-9/MS-18).");

        // mutating 方法 → MM 迁移规划（影响集 / MM-6 否决 / 调用点）。
        // RC-4：record class 写整体私有字段（保留可变的内部状态）不算 mutating。
        if (info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
                && (w.WritesThis || w.WrittenMembers.Any(m => RequiresMigrationForMember(info, m)))))
        {
            if (!_caps.MethodMigration)
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                    "record has instance method(s) that mutate state; method migration (MM) is " +
                    "required to convert.");

            var (units, migrationFailure) = MethodMigrationPlanner.BuildUnits(info);
            if (migrationFailure != null)
                return FailAt(plan, migrationFailure.Code, migrationFailure.Reason,
                    migrationFailure.Location);

            var orderedUnits = units.Values.OrderBy(u =>
                    u.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0)
                .ToList();

            // IR 方法触发：先于 Migrations/RR058/调用点扫描做映射与阻断门（原子跳过）。
            foreach (var unit in orderedUnits)
            {
                foreach (var (iface, interfaceMethod) in
                         InterfaceMethodImplementations(info.Symbol, unit.Method))
                {
                    var memberLocation = interfaceMethod.Locations.FirstOrDefault(l => l.IsInSource)
                        ?? info.Symbol.Locations.First(l => l.IsInSource);

                    // IR-7/FB-3：外部接口无法追加成员；force 接受桩降级。
                    if (!RecordSyntaxFacts.IsInTree(iface))
                    {
                        if (!forced)
                            return FailAt(plan, RecordReadOnlyDiagnosticCode.RR070,
                                $"interface '{iface.ToDisplayString()}' declares method " +
                                $"'{interfaceMethod.Name}' implemented by this record but the " +
                                "interface is not part of the source tree; the required 'With' " +
                                "member cannot be added to it (IR-7/FB-3). Use overrides.force " +
                                "to accept stub degradation.",
                                memberLocation);
                        forcedStubInterfaces.Add(iface);
                    }

                    var originalWithName = "With" + interfaceMethod.Name;
                    var withName = originalWithName;
                    var collisionOnInterface = iface.GetMembers().Any(m => m.Name == withName);
                    var collisionOnRecord = info.Symbol.GetMembers().Any(m => m.Name == withName);
                    if (collisionOnInterface || collisionOnRecord)
                        withName = withName + "__cs2j";
                    // 改名后仍需检测二次碰撞（极罕见）→ 此时才 RR071。
                    if (iface.GetMembers().Any(m => m.Name == withName)
                        || info.Symbol.GetMembers().Any(m => m.Name == withName))
                        return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                            $"even the suffixed name '{withName}' collides with an existing " +
                            $"member on interface '{iface.ToDisplayString()}' or record " +
                            $"'{info.Symbol.Name}'; cannot generate a ripple member (IR-7).",
                            memberLocation);

                    // IR-5：With 成员加到声明该接口成员的原始基接口，派生接口自然继承。
                    var declaringIface = RecordSyntaxFacts.DeclaringInterfaceFor(iface, interfaceMethod);
                    AddRippleMembers(rippleMembersByInterface, declaringIface, new[]
                    {
                        new RippleMember(
                            declaringIface, interfaceMethod, unit.Method, withName,
                            originalWithName,
                            IsMethod: true, ReturnsVoid: interfaceMethod.ReturnsVoid),
                    });
                }
            }

            foreach (var unit in orderedUnits)
            {
                plan.Migrations.Add(new MethodMigrationPlan(
                    unit.Method, unit.Kind, unit.PromotedMembers));
            }

            var groupFailure = MethodGroupBindingScanner.Scan(analysis.Compilation!, units);
            if (groupFailure != null)
                return FailAt(plan, groupFailure.Code, groupFailure.Reason,
                    groupFailure.Location);

            var callSites = MethodCallSiteScanner.Scan(
                analysis.Compilation!, info, units, out var callFailure);
            if (callFailure != null)
                return FailAt(plan, callFailure.Code, callFailure.Reason, callFailure.Location);
            foreach (var (callSite, _) in callSites)
            {
                plan.Sites.Add(callSite);
                plan.TouchedFiles.Add(callSite.Site.FilePath);
            }
        }

        // 接口连带（IR-1/IR-2/IR-4/IR-7；属性触发）。
        // 先于用点分类：RippleForSite 需要 plan.Ripples 判定 InterfaceWithCall 动作。
        foreach (var group in rippleProperties
                     .GroupBy(t => t.Interface, SymbolEqualityComparer.Default))
        {
            var iface = (INamedTypeSymbol)group.Key!;

            // IR-7/FB-3：外部接口（源不在树内）无法追加成员 → FB-1；
            // force 接受桩降级（record 侧仍生成旧契约显式桩，接口不加 With 成员）。
            if (!RecordSyntaxFacts.IsInTree(iface))
            {
                if (!forced)
                    return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                        $"interface '{iface.ToDisplayString()}' declares settable member(s) " +
                        "implemented by this record but the interface is not part of the source " +
                        "tree; the required 'With' member cannot be added to it (IR-7/FB-3). " +
                        "Use overrides.force to accept stub degradation.");
                forcedStubInterfaces.Add(iface);
            }

            var members = new List<RippleMember>();
            foreach (var (recordProperty, _, interfaceProperty) in group)
            {
                var originalWithName = "With" + interfaceProperty.Name;
                var withName = originalWithName;
                var collisionOnInterface = iface.GetMembers().Any(m => m.Name == withName);
                var collisionOnRecord = info.Symbol.GetMembers().Any(m => m.Name == withName);
                if (collisionOnInterface || collisionOnRecord)
                    withName = withName + "__cs2j";
                // 改名后仍需检测二次碰撞（极罕见：接口已有 WithX__cs2j）→ 此时才 RR071。
                if (iface.GetMembers().Any(m => m.Name == withName)
                    || info.Symbol.GetMembers().Any(m => m.Name == withName))
                    return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                        $"even the suffixed name '{withName}' collides with an existing " +
                        $"member on interface '{iface.ToDisplayString()}' or record " +
                        $"'{info.Symbol.Name}'; cannot generate a ripple member (IR-7).",
                        interfaceProperty.Locations.FirstOrDefault(l => l.IsInSource)
                            ?? info.Symbol.Locations.First(l => l.IsInSource));

                // IR-5：With 成员加到声明该接口成员的原始基接口，派生接口自然继承。
                var declaringIface = RecordSyntaxFacts.DeclaringInterfaceFor(iface, interfaceProperty);
                members.Add(new RippleMember(
                    declaringIface, interfaceProperty, recordProperty, withName,
                    originalWithName,
                    IsMethod: false, ReturnsVoid: true));
            }

            AddRippleMembers(rippleMembersByInterface, iface, members);
        }

        // 属性 + 方法触发的接口连带在此合并物化：cap 门、IR-3 fills、触碰面一次算清。
        if (rippleMembersByInterface.Count > 0 && !_caps.InterfaceRipple)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                "record implements an interface member that requires a generated With-style " +
                "contract; interface ripple (IR) is required to convert.");

        foreach (var pair in rippleMembersByInterface
                     .OrderBy(p => p.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            var iface = pair.Key;
            var members = pair.Value;
            var fills = BuildInterfaceFills(
                analysis, info.Symbol, iface, members, out var fillBlock);
            if (fillBlock != null)
                return FailAt(plan, fillBlock.Code, fillBlock.Reason, fillBlock.Location);

            plan.Ripples.Add(new InterfaceRipplePlan(iface, members, fills,
                StubOnly: forcedStubInterfaces.Contains(iface)));
            AddInterfaceTouches(plan, iface, fills);
        }

        // 用点（MS）：逐点分类，任一不可行即整 record 失败（FB-1），错误位置在用点。
        foreach (var site in info.Sites.OrderBy(s => s.Location.SourceSpan.Start))
        {
            var failure = MutationSiteClassifier.Classify(analysis, plan, info, site);
            if (failure != null)
                return FailAt(plan, failure.Code, failure.Reason, failure.Location);
            // IR-4 仅接管简单赋值；复合/自增等形态在 record 接收者上继续走 with 管线，
            // 在接口接收者上由各自 Classify* 辅助的 MemberIsInitSettable 门判 RR020。
            var action = site.Kind == SiteKind.SimpleAssign
                && MutationSiteClassifier.RippleForSite(plan, site.Member) != null
                ? SiteActionKind.InterfaceWithCall
                : SiteActionKind.WithAssign;
            plan.Sites.Add(new SitePlan(site, action));
            plan.TouchedFiles.Add(site.FilePath);
        }

        if (analysis.HasDynamicOrReflectionMutation)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR057,
                "dynamic receiver or reflection SetValue usage cannot be rewritten statically (MS-14).");

        var needMemberRewrites = plan.MemberRewrites.Any(m =>
            m.Kind is MemberRewriteKind.SetToInit or MemberRewriteKind.FieldToInitProperty
                or MemberRewriteKind.PrivateFieldToInitProperty);
        if (needMemberRewrites && !_caps.MemberRewrites)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                "record has mutable properties/fields; declaration member rewriting (RS-3..RS-6) " +
                "is required to convert.");

        if (info.Kind == RecordKind.Struct && !_caps.ReadOnlyModifier)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020, "readonly modifier stage disabled.");

        _ = forced; // RR012 信息在 CLI 报告阶段补；force 不绕过上面的硬阻断。
        plan.Outcome = PlanOutcome.Converted;

        // 属性/字段改写触碰点（错误归属用）。
        foreach (var rewrite in plan.MemberRewrites)
        {
            foreach (var loc in rewrite.Symbol.Locations.Where(l => l.IsInSource))
                plan.Touches.Add(new PlanTouch(loc.SourceTree.FilePath, info.Symbol.Name, rewrite.MemberName));
            plan.TouchedFiles.UnionWith(rewrite.Symbol.Locations
                .Where(l => l.IsInSource).Select(l => l.SourceTree.FilePath));
        }
        return plan;
    }

    private static RecordPlan Fail(RecordPlan plan, RecordReadOnlyDiagnosticCode code, string reason)
    {
        plan.Outcome = PlanOutcome.Infeasible;
        plan.FailCode = code;
        plan.FailReason = reason;
        plan.FailLocation = plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return plan;
    }

    private static RecordPlan FailAt(RecordPlan plan, RecordReadOnlyDiagnosticCode code,
        string reason, Location location)
    {
        plan.Outcome = PlanOutcome.Infeasible;
        plan.FailCode = code;
        plan.FailReason = reason;
        plan.FailLocation = location;
        return plan;
    }

    // set 访问器会被转成 init（RS-9），init 访问器与 ctor 同样可写 readonly 字段，
    // 故仅由 ctor/set 访问器写的私有字段归入 RS-6a（readonly），无需提升。
    // this = expr（MM-3）会逐成员拷贝私有字段，字段必须转私有 init 属性才能被出口 with 设置。
    private static bool IsPrivateFieldWrittenAfterConstruction(RecordInfo info, IFieldSymbol field)
        => info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
            && (w.WritesThis || w.WrittenMembers.Contains(field)));

    /// <summary>
    /// RC-4：record class 整体私有字段保留可变，方法体内的写入无需迁移；
    /// 其余成员（属性、可见字段）转换后 init-only，写入需要 MM。
    /// record struct 全部成员转 init，一律需要迁移。
    /// </summary>
    private static bool RequiresMigrationForMember(RecordInfo info, ISymbol member)
    {
        if (info.Kind == RecordKind.Struct) return true;
        return member is not IFieldSymbol field
            || RecordSyntaxFacts.IsVisibleOutsideType(field);
    }

    /// <summary>
    /// 返回 record 属性对“带可写（非 init）set 的接口属性”的全部实现映射。
    /// 一个公共属性可同时隐式实现多个接口成员（IR-2 每接口各生成一份显式桩）。
    /// 外部接口同样枚举——IR-7 的 in-tree 裁决在规划段做，这里不预先过滤。
    /// </summary>
    private static IReadOnlyList<(INamedTypeSymbol Interface, IPropertySymbol InterfaceProperty)>
        InterfaceSetterImplementations(INamedTypeSymbol record, IPropertySymbol property)
    {
        var result = new List<(INamedTypeSymbol, IPropertySymbol)>();
        foreach (var iface in record.AllInterfaces)
        {
            foreach (var interfaceProperty in iface.GetMembers().OfType<IPropertySymbol>())
            {
                if (interfaceProperty.SetMethod is not { IsInitOnly: false }) continue;
                var impl = record.FindImplementationForInterfaceMember(interfaceProperty);
                if (impl != null
                    && SymbolEqualityComparer.Default.Equals(impl, property))
                {
                    result.Add((iface, interfaceProperty));
                }
            }
        }
        return result;
    }

    private sealed record InterfaceFillBlock(
        RecordReadOnlyDiagnosticCode Code, string Reason, Location Location);

    private static void AddRippleMembers(
        Dictionary<INamedTypeSymbol, List<RippleMember>> byInterface,
        INamedTypeSymbol iface,
        IEnumerable<RippleMember> members)
    {
        if (!byInterface.TryGetValue(iface, out var list))
            byInterface[iface] = list = new();
        foreach (var member in members)
        {
            if (list.Any(existing =>
                    existing.IsMethod == member.IsMethod
                    && SymbolEqualityComparer.Default.Equals(
                        existing.InterfaceMember, member.InterfaceMember)))
            {
                continue;
            }
            list.Add(member);
        }
    }

    /// <summary>
    /// 返回 record 迁移方法所实现的全部 in-tree/接口符号映射。一个方法可同时隐式实现
    /// 多个接口；每个接口各生成旧契约桩与新 With 契约。
    /// </summary>
    private static IReadOnlyList<(INamedTypeSymbol Interface, IMethodSymbol InterfaceMethod)>
        InterfaceMethodImplementations(INamedTypeSymbol record, IMethodSymbol method)
    {
        var result = new List<(INamedTypeSymbol, IMethodSymbol)>();
        foreach (var iface in record.AllInterfaces)
        {
            foreach (var interfaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (interfaceMethod.MethodKind != MethodKind.Ordinary)
                    continue;
                var implementation = record.FindImplementationForInterfaceMember(interfaceMethod);
                if (implementation is not IMethodSymbol implemented)
                    continue;
                if (SymbolEqualityComparer.Default.Equals(implemented, method)
                    || SymbolEqualityComparer.Default.Equals(
                        implemented.PartialImplementationPart, method)
                    || SymbolEqualityComparer.Default.Equals(
                        implemented.PartialDefinitionPart, method))
                {
                    result.Add((iface, interfaceMethod));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// IR-3：枚举同一 in-tree 接口的其他实现者。转换中的 owner record 由 IR-2 负责，
    /// 因此这里跳过 owner；其他“本轮也转换”的 record 在 Rewriter 索引中再按计划状态去重。
    /// 抽象类型只有确实声明/继承了旧成员实现时才补成员；接口类型本身永不补实现。
    /// </summary>
    private static List<InterfaceFillPlan> BuildInterfaceFills(
        RecordAnalysis analysis,
        INamedTypeSymbol owner,
        INamedTypeSymbol iface,
        IReadOnlyList<RippleMember> rippleMembers,
        out InterfaceFillBlock? block)
    {
        block = null;
        var result = new List<InterfaceFillPlan>();
        if (!analysis.Interfaces.TryGetValue(iface, out var fact))
            return result;

        foreach (var implementer in fact.InTreeImplementers
                     .OrderBy(t => t.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath
                         ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0))
        {
            if (SymbolEqualityComparer.Default.Equals(implementer, owner))
                continue;
            if (implementer.TypeKind == TypeKind.Interface)
                continue;

            var supported = new List<RippleMember>();
            foreach (var member in rippleMembers)
            {
                var implementation = implementer.FindImplementationForInterfaceMember(
                    member.InterfaceMember);
                if (implementation == null)
                    continue;

                // class/record class 经接口引用调用旧显式实现是同一引用；struct 会装箱成副本，
                // 无法保持“原地修改再返回自身”，保守阻断 owner record 的接口连带。
                if (implementer.IsReferenceType == false
                    && RecordSyntaxFacts.IsExplicitInterfaceImplementation(
                        implementation, member.InterfaceMember))
                {
                    block = new InterfaceFillBlock(
                        RecordReadOnlyDiagnosticCode.RR070,
                        $"in-tree value type '{implementer.ToDisplayString()}' explicitly implements " +
                        $"'{iface.ToDisplayString()}.{member.InterfaceMember.Name}'; a fluent interface " +
                        "fill would mutate a boxed copy instead of the original value (IR-3/IR-7).",
                        implementation.Locations.FirstOrDefault(l => l.IsInSource)
                            ?? implementer.Locations.First(l => l.IsInSource));
                    return new();
                }

                supported.Add(member);
            }

            if (supported.Count > 0)
                result.Add(new InterfaceFillPlan(implementer, supported));
        }

        return result;
    }

    private static void AddInterfaceTouches(
        RecordPlan plan,
        INamedTypeSymbol iface,
        IReadOnlyList<InterfaceFillPlan> fills)
    {
        foreach (var loc in iface.Locations.Where(l => l.IsInSource))
        {
            plan.TouchedFiles.Add(loc.SourceTree!.FilePath);
            plan.Touches.Add(new PlanTouch(loc.SourceTree!.FilePath, iface.Name, null));
        }

        foreach (var fill in fills)
        {
            foreach (var loc in fill.Implementer.Locations.Where(l => l.IsInSource))
            {
                plan.TouchedFiles.Add(loc.SourceTree!.FilePath);
                foreach (var member in fill.Members)
                {
                    plan.Touches.Add(new PlanTouch(
                        loc.SourceTree!.FilePath, fill.Implementer.Name, member.WithName));
                }
            }
        }
    }
}
