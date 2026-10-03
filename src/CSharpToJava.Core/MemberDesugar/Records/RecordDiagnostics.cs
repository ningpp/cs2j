namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>
/// CS2J5212-5230/5290 record `with` 面消息文案（码位常量在共享枚举
/// <see cref="MemberDesugarDiagnosticCode"/>；本文件只放消息模板，风格对齐
/// Properties/Finalizers 的 Diagnostics 静态类）。
/// </summary>
internal static class RecordDiagnostics
{
    public const string AtomicSuffix =
        "the record clone plan and all of its with sites were left unchanged";

    /// <summary>CS2J5220：位置 record reshape 闸门（决策 8c，原子不改；reason 给出具体检测结果）。</summary>
    public static string PositionalReshapeBlocked(string typeFqn, string reason)
        => $"positional record '{typeFqn}' cannot be reshaped into explicit members because "
         + $"{reason}; the declaration and all of its with sites were left unchanged.";

    /// <summary>CS2J5220：readonly 值类型非空 with——克隆局部成员仍只读，set_X/字段直写
    /// 无合法发射面（空 with 的纯值拷贝不受限，照常改写）。</summary>
    public static string ReadonlyValueCopyBlocked(string receiverText)
        => $"'with' on readonly value type '{receiverText}' mutates members, but every instance "
         + "member of a readonly struct is itself readonly; the value-copy clone cannot be "
         + "assigned into; the with site was left unchanged (empty with initializers are still "
         + "rewritten as a pure value copy).";

    /// <summary>CS2J5221：非密封 record 存在树外派生 record，虚克隆无法保证运行时类型。</summary>
    public static string MetadataDerivedRisk(string typeFqn, IReadOnlyCollection<string> derived)
    {
        var who = derived.Count > 0
            ? $" out-of-tree derived record(s) found: {string.Join(", ", derived)}"
            : " the derivation closure cannot be proven within this compilation";
        return $"record '{typeFqn}' is unsealed and{who}; the synthesized virtual clone factory "
             + "could silently fall back to the base factory for an out-of-tree derived runtime "
             + "type and change the result type (decision 4); " + AtomicSuffix + ".";
    }

    /// <summary>CS2J5222：接收者是元数据 record（声明不在编译树内），无法补发工厂。</summary>
    public static string MetadataRecordReceiver(string typeFqn)
        => $"'with' receiver '{typeFqn}' is a record declared in referenced metadata (its "
         + "declaration is not part of this compilation); no clone factory can be synthesized "
         + "for it; the with site was left unchanged.";

    /// <summary>CS2J5223：with 位于不可临时提升的表达式上下文。</summary>
    public static string NonLiftableContext(string? typeFqn)
        => $"'with' expression{(typeFqn != null ? $" on '{typeFqn}'" : null)} sits in a context "
         + "that cannot be lifted to statements (field/property initializer, expression-bodied "
         + "member, attribute argument, query clause, non-statement expression position); the "
         + "single-evaluation clone sequence cannot be emitted there; the with site was left "
         + "unchanged.";

    /// <summary>CS2J5224：M1 不收编的嵌套形态（嵌套 with 是 WR-5/M2；嵌套对象/集合初始化器 M2）。</summary>
    public static string UnsupportedNestedInitializer(string? typeFqn, string what)
        => $"'with' initializer{(typeFqn != null ? $" on '{typeFqn}'" : null)} contains {what}, "
         + "which is scheduled for M2 (nested with post-order lifting / nested object or "
         + "collection initializers); the with site was left unchanged.";

    /// <summary>CS2J5225：匿名类型 with。</summary>
    public static string AnonymousTypeWith()
        => "'with' on an anonymous type is not desugared in this version (anonymous-type "
         + "lifting to a named record is scheduled for C21); the with site was left unchanged.";

    /// <summary>CS2J5226：表达式树内 with（防御性，合法 C# 本不允许）。</summary>
    public static string ExpressionTreeWith()
        => "'with' inside an expression-tree lambda cannot be rewritten without changing the "
         + "expression tree shape; the with site was left unchanged.";

    /// <summary>CS2J5227：dynamic 接收者。</summary>
    public static string DynamicWith()
        => "'with' receiver is dynamic; clone semantics cannot be bound statically; the with "
         + "site was left unchanged.";

    /// <summary>CS2J5228：非 record class 接收者（CS8858 残余兜底）/成员左值不可解析。</summary>
    public static string NonRecordClassWith(string? typeFqn, string detail)
        => $"'with' receiver '{typeFqn ?? "<unbound>"}' is not a desugarable record class "
         + $"({detail}); the with site was left unchanged.";

    /// <summary>record class 缺复制构造函数（合法 C# 不应出现，防御性兜底）。</summary>
    public static string CopyConstructorMissing(string typeFqn)
        => $"record '{typeFqn}' has no accessible single-parameter copy constructor of its own "
         + "type; the clone factory cannot be emitted; " + AtomicSuffix + ".";

    // ---- Info（5212/5213，仅 verbose）----

    public static string WithSiteRewritten(string typeFqn, bool isEmpty, int memberCount, string? factory)
        => factory != null
            ? isEmpty
                ? $"with site on '{typeFqn}' rewritten to inline clone factory call '{factory}()' (WR-3)."
                : $"with site on '{typeFqn}' rewritten: clone via '{factory}()' with {memberCount} "
                  + "member assignment(s) (WR-1/2/6)."
            : isEmpty
                ? $"with site on '{typeFqn}' rewritten as a local value copy (WR-4)."
                : $"with site on '{typeFqn}' rewritten as a local value copy with {memberCount} "
                  + "member assignment(s) (WR-4).";

    public static string CloneFactorySynthesized(string typeFqn, RecordFactoryForm form, string factory)
        => $"clone factory '{factory}' synthesized on '{typeFqn}' "
         + $"(form: {form.ToString().ToLowerInvariant()}, RC-1/RC-2).";

    /// <summary>CS2J5214：位置 record 已 reshape（决策 8a/8b/8c/6）。</summary>
    public static string PositionalReshaped(string typeFqn, int memberCount, bool isStruct)
        => $"positional record '{typeFqn}' reshaped: parameter list removed; {memberCount} "
         + $"positional member(s) materialized as fields with get_X/set_X methods "
         + $"({(isStruct ? "RC-4 record struct" : "RC-3 record class")} final form).";

    /// <summary>CS2J5230：residual 零 with 闸门。</summary>
    public static string ResidualWith(int line, int column)
        => "a with-expression remains in the output without a registered rewrite or skip "
         + $"(residual gate WR-8) at ({line},{column}).";
}
