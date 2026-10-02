namespace CSharpToJava.Core.MemberDesugar.Signatures;

/// <summary>
/// C46 SI 规则（Kind=Signature, Order=40；设计 §4.4 Order 40-49 段）：晚于
/// <see cref="SignatureAuditRule"/>（Order=30）运行，只读复用其整树索引，产出
/// <see cref="ExplicitInterface.SiPlan"/> 并交给族 <see cref="MemberDesugarPlanBuilder"/>。
/// <c>Signatures.Enabled=false</c> 时零执行（不建索引、不扫站点、零统计零诊断零 diff）。
/// 本规则的 <see cref="Analyze"/> 只产 plan、不改树；改写由预处理器签名字段分相驱动。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Signature, 40)]
internal sealed class SiRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Signature;
    public int Order => 40;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        if (!context.Options.Signatures.Enabled) return;      // 主闸门：零执行
        if (context.SignatureAnalysis == null) return;        // 审计规则先行建立（防御）
        ExplicitInterface.SiPlanBuilder.Build(context, builder);
    }
}