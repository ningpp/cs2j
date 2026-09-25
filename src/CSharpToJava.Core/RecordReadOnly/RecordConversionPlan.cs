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
    string WithName,
    bool IsMethod,
    bool ReturnsVoid);

internal sealed record InterfaceRipplePlan(
    INamedTypeSymbol Interface,
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
        return plan;
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

        // mutating 方法（写实例成员的非 ctor 方法）需要 MM。set 访问器转 init 后享有
        // 与 ctor 同等的 this 写豁免（RS-9），不计迁移；getter 写成员由 M3 判 RR063。
        if (info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
                && (w.WrittenMembers.Count > 0 || w.WritesThis)))
        {
            if (!_caps.MethodMigration)
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                    "record has instance method(s) that mutate state; method migration (MM) is " +
                    "required to convert.");

            var (units, migrationFailure) = MethodMigrationPlanner.BuildUnits(info);
            if (migrationFailure != null)
                return FailAt(plan, migrationFailure.Code, migrationFailure.Reason,
                    migrationFailure.Location);

            var groupFailure = MethodGroupBindingScanner.Scan(analysis.Compilation!, units);
            if (groupFailure != null)
                return FailAt(plan, groupFailure.Code, groupFailure.Reason,
                    groupFailure.Location);

            foreach (var unit in units.Values.OrderBy(u =>
                         u.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0))
            {
                plan.Migrations.Add(new MethodMigrationPlan(
                    unit.Method, unit.Kind, unit.PromotedMembers));
            }

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

        // 接口连带（IR-1/IR-2/IR-4/IR-7；属性触发；方法触发 Task 21）。
        // 先于用点分类：RippleForSite 需要 plan.Ripples 判定 InterfaceWithCall 动作。
        // cap 门保留：能力被外部关闭时给 RR070（单测/未来 overrides 可构造该形态）。
        if (rippleProperties.Count > 0 && !_caps.InterfaceRipple)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                "record implicitly implements a settable interface member; interface ripple (IR) " +
                "is required to convert.");

        foreach (var group in rippleProperties
                     .GroupBy(t => t.Interface, SymbolEqualityComparer.Default))
        {
            var iface = (INamedTypeSymbol)group.Key!;

            // IR-7：外部接口（源不在树内）无法追加成员 → FB-1（force 接受桩降级在 Task 22）。
            if (!RecordSyntaxFacts.IsInTree(iface))
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                    $"interface '{iface.ToDisplayString()}' declares settable member(s) implemented " +
                    "by this record but the interface is not part of the source tree; the required " +
                    "'With' member cannot be added to it (IR-7/FB-3).");

            var members = new List<RippleMember>();
            foreach (var (recordProperty, _, interfaceProperty) in group)
            {
                var withName = "With" + interfaceProperty.Name;

                // IR-7：签名碰撞先 FB-1；改名 WithX__cs2j 在 Task 22。
                if (iface.GetMembers().Any(m => m.Name == withName))
                    return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                        $"interface '{iface.ToDisplayString()}' already declares a member named " +
                        $"'{withName}'; adding the ripple member would collide (IR-7).",
                        interfaceProperty.Locations.FirstOrDefault(l => l.IsInSource)
                            ?? info.Symbol.Locations.First(l => l.IsInSource));
                if (info.Symbol.GetMembers().Any(m => m.Name == withName))
                    return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                        $"record '{info.Symbol.Name}' already declares a member named " +
                        $"'{withName}'; the generated strongly-typed ripple member would collide " +
                        "(IR-7).",
                        info.Symbol.Locations.First(l => l.IsInSource));

                members.Add(new RippleMember(
                    iface, interfaceProperty, recordProperty, withName,
                    IsMethod: false, ReturnsVoid: true));
            }

            plan.Ripples.Add(new InterfaceRipplePlan(iface, members));

            // IR-1 改的是接口文件：登记触碰面供 activePlans 准入与 FB-5 计划级回退归属。
            foreach (var loc in iface.Locations.Where(l => l.IsInSource))
            {
                plan.TouchedFiles.Add(loc.SourceTree!.FilePath);
                plan.Touches.Add(new PlanTouch(loc.SourceTree!.FilePath, iface.Name, null));
            }
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
}
