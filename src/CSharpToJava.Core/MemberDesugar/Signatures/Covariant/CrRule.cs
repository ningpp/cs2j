namespace CSharpToJava.Core.MemberDesugar.Signatures.Covariant;

/// <summary>
/// C46 CR 规则（Kind=Signature, Order=60；设计 §4.3-1/§4.4 Order 60-69 段）：签名分相中
/// 晚于 <see cref="NhRule"/>（Order=50）运行（SI → NH → CR），只读复用 M1
/// <see cref="CovariantChainAnalyzer"/> 的协变链结论，产出 <see cref="CrPlan"/>。
/// <c>Signatures.Enabled=false</c> 时零执行（不建索引、不扫站点、零统计零诊断零 diff）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Signature, 60)]
internal sealed class CrRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Signature;
    public int Order => 60;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        if (!context.Options.Signatures.Enabled) return;      // 主闸门：零执行
        if (context.SignatureAnalysis == null) return;        // 审计规则先行建立（防御）
        CrPlanBuilder.Build(context, builder);
    }
}