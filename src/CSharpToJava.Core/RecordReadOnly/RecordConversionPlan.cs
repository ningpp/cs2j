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

internal enum MethodMigrationKind { VoidToRecord, NonVoidToOut, Extension }

internal sealed record MemberRewrite(string MemberName, MemberRewriteKind Kind, ISymbol Symbol, bool IsRequired);

internal sealed record MethodMigrationPlan(
    IMethodSymbol Method,
    MethodMigrationKind Kind,
    IReadOnlyList<ISymbol> PromotedMembers);

internal sealed record InterfaceRipplePlan(
    INamedTypeSymbol Interface,
    IReadOnlyList<ISymbol> TriggerMembers);

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
        MethodMigration: false,
        InterfaceRipple: false);}

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

        var rippleRequired = false;

        // 属性 set→init（RS-3/4/9/20；RC-2/5 在 M5 复用同一条目）。
        // positional record struct 的位置参数属性由 readonly 修饰符自动转 set→init，不算显式重写。
        var positionalNames = info.IsPositional
            ? new HashSet<string>(info.Parts
                .SelectMany(p => p.ParameterList?.Parameters ?? default)
                .Select(p => p.Identifier.Text), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in info.SettableProperties)
        {
            if (positionalNames.Contains(property.Name))
                continue; // readonly 修饰符自动处理位置参数的 set→init。
            if (PropertyImplementsInterfaceSetter(info.Symbol, property))
            {
                rippleRequired = true;
                continue; // RS-14/RC-7：显式桩 + IR 在 M4 落地。
            }
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
        }

        // 用点（MS）：逐点分类，任一不可行即整 record 失败（FB-1），错误位置在用点。
        foreach (var site in info.Sites.OrderBy(s => s.Location.SourceSpan.Start))
        {
            var failure = MutationSiteClassifier.Classify(analysis, plan, info, site);
            if (failure != null)
                return FailAt(plan, failure.Code, failure.Reason, failure.Location);
            plan.Sites.Add(new SitePlan(site, SiteActionKind.WithAssign));
            plan.TouchedFiles.Add(site.FilePath);
        }

        // 接口连带（IR）。
        if (rippleRequired && !_caps.InterfaceRipple)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                "record implicitly implements a settable interface member; interface ripple (IR) " +
                "is required to convert.");
        if (rippleRequired)
        {
            foreach (var group in info.SettableProperties
                         .Where(p => PropertyImplementsInterfaceSetter(info.Symbol, p))
                         .Select(p => InterfaceForSetter(info.Symbol, p))
                         .Where(i => i is not null)
                         .GroupBy(i => (ISymbol)i!, SymbolEqualityComparer.Default))
            {
                plan.Ripples.Add(new InterfaceRipplePlan((INamedTypeSymbol)group.Key, Array.Empty<ISymbol>()));
            }
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
    private static bool IsPrivateFieldWrittenAfterConstruction(RecordInfo info, IFieldSymbol field)
        => info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
            && w.WrittenMembers.Contains(field));

    private static bool PropertyImplementsInterfaceSetter(INamedTypeSymbol record, IPropertySymbol property)
    {
        foreach (var iface in record.AllInterfaces)
        {
            foreach (var ip in iface.GetMembers().OfType<IPropertySymbol>())
            {
                if (ip.SetMethod is not { IsInitOnly: false }) continue;
                var impl = record.FindImplementationForInterfaceMember(ip);
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, property))
                    return true;
            }
        }
        return false;
    }

    private static INamedTypeSymbol? InterfaceForSetter(INamedTypeSymbol record, IPropertySymbol property)
    {
        foreach (var iface in record.AllInterfaces)
        {
            foreach (var ip in iface.GetMembers().OfType<IPropertySymbol>())
            {
                if (ip.SetMethod is not { IsInitOnly: false }) continue;
                var impl = record.FindImplementationForInterfaceMember(ip);
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, property))
                    return iface;
            }
        }
        return null;
    }
}
