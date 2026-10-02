namespace CSharpToJava.Core.MemberDesugar.Signatures;

/// <summary>
/// C46 M1 审计规则（Kind=Signature, Order=30）：仅在 <see cref="SignatureOptions.Enabled"/>
/// 时建立四索引，并把按文件审计计数写入
/// <see cref="MemberDesugarAnalysisContext.SignatureAuditByFile"/>；不产 plan、不造改写
/// （M1 只读审计，设计 §9）。Enabled=false 时零执行（不建索引、不扫站点、零统计零诊断零 diff）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Signature, 30)]
internal sealed class SignatureAuditRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Signature;
    public int Order => 30;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        if (!context.Options.Signatures.Enabled) return; // 主闸门：零执行
        if (context.SignatureAnalysis != null) return;   // 整树一次

        var result = SignatureExplicitizationAnalyzer.Analyze(context);
        context.SignatureAnalysis = result;
        foreach (var (file, stats) in result.AuditByFile)
            context.SignatureAuditByFile[file] = stats;
    }
}