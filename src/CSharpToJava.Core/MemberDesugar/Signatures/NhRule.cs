namespace CSharpToJava.Core.MemberDesugar.Signatures;

/// <summary>
/// C46 NH 规则（Kind=Signature, Order=50；设计 §4.4 Order 50-59 段）：晚于 <see cref="SiRule"/>
/// （Order=40）运行，只读复用 M1 的 <see cref="Hiding.HideGroupResolver"/> 索引，产出
/// <see cref="Hiding.NhPlan"/>。M3 只覆盖 NH-3（纯 base 委托白名单桥接）与 NH-4（<c>new</c> 字段/静态方法改名）；
/// NH-1/NH-2 归 M4。<c>Signatures.Enabled=false</c> 时零执行（零统计零诊断零 diff）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Signature, 50)]
internal sealed class NhRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Signature;
    public int Order => 50;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        if (!context.Options.Signatures.Enabled) return;      // 主闸门：零执行
        if (context.SignatureAnalysis == null) return;        // 审计规则先行建立（防御）
        Hiding.NhPlanBuilder.Build(context, builder);
    }
}