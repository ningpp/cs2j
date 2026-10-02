namespace CSharpToJava.Core.MemberDesugar.Signatures;

/// <summary>
/// CS2J5190-5199 消息文案与级别策略（设计 §6，风格对齐 Finalizers/FinalizerDiagnostics）。
/// 5190/5198 恒 Info，其余默认 Warning 且经 <see cref="SignatureOptions.Strict"/>（默认 true）
/// 升 Error；5193（反射/字符串契约）为 Warning 非阻断，不因 strict 升 Error。
/// </summary>
internal static class SignatureDiagnostics
{
    /// <summary>CS2J5190：一次一文件的审计汇总（Info，仅 verbose）。</summary>
    public static string AuditSummary(SignatureStatistics s)
        => $"signature audit established: explicit-interface x{s.ExplicitMembersDetected} "
           + $"(method {s.ExplicitMethods}/property {s.ExplicitProperties}/indexer {s.ExplicitIndexers}/event {s.ExplicitEvents}); "
           + $"hiding x{s.HidingDetected} (new {s.HidingNew}/implicit {s.HidingImplicit}/newslot {s.HidingNewSlot}/static {s.HidingStatic}); "
           + $"covariant x{s.CovariantChains} (retained {s.CovariantRetained}/bridged {s.CovariantBridged}/valueType {s.CovariantValueTypeSkipped}).";

    /// <summary>CS2J5191：同名不同接口实现体分歧（SI-3）。</summary>
    public static string DivergentInterfaceBodies(string typeFqn, string member, string ifaceA, string ifaceB)
        => $"explicit implementations of '{member}' on [{typeFqn}] for interfaces [{ifaceA}] and [{ifaceB}] have "
           + "divergent bodies; Java single dispatch cannot express two bodies for one slot. Merge them into one "
           + "implementation or expose them under distinct member names.";

    /// <summary>CS2J5192：命名预留冲突/让位者不可确定。</summary>
    public static string NameReservationConflict(string typeFqn, string member)
        => $"cannot determine the displaced member or a collision-free name for '{member}' on [{typeFqn}]; "
           + "the plan was skipped rather than renamed nondeterministically.";

    /// <summary>CS2J5193：反射/字符串契约风险站点（Warning，非阻断）。</summary>
    public static string ReflectionContract(string member, string api)
        => $"the renamed member '{member}' is referenced through the string/reflection API {api}; the site is "
           + "retained unchanged because a runtime name contract cannot be guaranteed statically (reflection is "
           + "not promised).";

    /// <summary>CS2J5194：表达式树内（force 不解除）或 dynamic 绑定站点不可判定。</summary>
    public static string ExpressionTreeOrDynamic(string member, bool dynamic)
        => dynamic
            ? $"the call site bound to '{member}' has a dynamic receiver; the binding is undecidable and the plan "
              + "was skipped (force does not lift this gate)."
            : $"the call site bound to '{member}' is inside an expression tree; the plan was skipped (expression-tree "
              + "sites are never rewritten; force does not lift this gate).";

    /// <summary>CS2J5195：值类型卷入/不可证 Java 可表达的协变返回（CR-3）。</summary>
    public static string ValueTypeCovariant(string typeFqn, string member, string baseType, string derivedType)
        => $"covariant return of '{member}' on [{typeFqn}] ('{derivedType}' over '{baseType}') involves a value type "
           + "or cannot be proven expressible in Java; no boxing bridge is synthesized.";

    /// <summary>CS2J5196：泛型擦除协变桥接 v1 不可行。</summary>
    public static string GenericBridgeInfeasible(string typeFqn, string member)
        => $"a covariant erasure bridge for '{member}' on [{typeFqn}] is infeasible in this version (the bridge "
           + "signature would collide with an existing overload); deferred to the C02 generic-deepen review.";

    /// <summary>CS2J5197：接口槽位映射候选不唯一（SI-4 泛型构造/菱形/重新实现）。</summary>
    public static string SlotAmbiguous(string typeFqn, string member, int candidateCount)
        => $"interface slot '{member}' on [{typeFqn}] maps to {candidateCount} candidate implementations "
           + "(generic construction/diamond/reimplementation); the plan was skipped pending manual resolution.";

    /// <summary>CS2J5198：命中 static abstract/virtual 接口成员，归 C36/CS2J0585。</summary>
    public static string StaticAbstractInfo(string member)
        => $"'{member}' is a static abstract/virtual interface member; it is not owned by this rule set and is "
           + "delegated to the C36 guard (CS2J0585).";

    /// <summary>CS2J5199：residual 闸门（SI-7/NH-5/CR-4 汇总）。</summary>
    public static string Residual(string filePath, int residualCount)
        => $"residual gate: {residualCount} signature construct(s) in '{filePath}' have no owning rewrite or skip "
           + "diagnostic after the pass.";

    /// <summary>Warning 经 Strict（默认 true）升 Error；Info/Error 不变。</summary>
    public static MemberDesugarSeverity Escalate(MemberDesugarSeverity severity, SignatureOptions options)
        => severity == MemberDesugarSeverity.Warning && options.Strict
            ? MemberDesugarSeverity.Error
            : severity;

    /// <summary>按码位返回默认级别（设计 §6：5190/5198 Info，5193 Warning 非阻断，其余 Warning）。</summary>
    public static MemberDesugarSeverity DefaultSeverity(MemberDesugarDiagnosticCode code)
        => code is MemberDesugarDiagnosticCode.SignaturePlanCreated
                or MemberDesugarDiagnosticCode.SignatureStaticAbstractInfo
            ? MemberDesugarSeverity.Info
            : MemberDesugarSeverity.Warning;

    /// <summary>构造签名诊断（默认级别 + strict 升级）。5193 非阻断：不升 Error。</summary>
    public static MemberDesugarDiagnostic Create(
        MemberDesugarDiagnosticCode code, string message, SignatureOptions options,
        string? symbolName = null, string? filePath = null, int line = 0, int column = 0)
    {
        var severity = DefaultSeverity(code);
        if (code != MemberDesugarDiagnosticCode.SignatureReflectionContract)
            severity = Escalate(severity, options);
        return new MemberDesugarDiagnostic(severity, code, message, filePath, line, column, symbolName);
    }
}