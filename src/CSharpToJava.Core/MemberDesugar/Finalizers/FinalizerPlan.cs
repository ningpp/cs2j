using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Finalizers;

/// <summary>
/// 一个源内终结器符号一个 plan（FN-1，每类型至多一个终结器，partial 多部件按符号合并）。
/// C39 无使用点（C# 不允许显式调用 Finalize），<see cref="MemberDesugarPlan.TouchedFiles"/>
/// 只含声明文件集合。M1 全部 plan 的 Outcome 统一为 <see cref="MemberDesugarOutcome.Planned"/>
/// （A 档不改树），C39 全部诊断由自有 rewriter 发射，避免框架 EmitSkipDiagnostics 双发。
/// </summary>
internal sealed class FinalizerPlan : MemberDesugarPlan
{
    /// <summary>终结器方法符号（IMethodSymbol.MethodKind == Destructor）。</summary>
    public required IMethodSymbol Destructor { get; init; }

    /// <summary>含入类型完全限定显示串。</summary>
    public required string TypeFqn { get; init; }

    /// <summary>体分类（FN-2）。</summary>
    public required FinalizerBodyKind BodyKind { get; init; }

    /// <summary>Dispose/SuppressFinalize 配对结论（FN-3）。</summary>
    public required SuppressFinalizePairing Pairing { get; init; }

    /// <summary>基类链上是否存在声明终结器的基类（含元数据基类，FN-4）。</summary>
    public required bool HasBaseFinalizer { get; init; }

    /// <summary>最近一个声明终结器的基类 FQN（无则 null），供 CS2J5207 消息使用。</summary>
    public string? BaseFinalizerFqn { get; init; }

    /// <summary>基类链信息降级（跨程序集缺信息/错误类型符号，或单文件模式），不报错（FN-4）。</summary>
    public required bool BaseInfoDegraded { get; init; }

    /// <summary>含入类型派生自 System.Runtime.ConstrainedExecution.CriticalFinalizerObject（FN-5/CS2J5205）。</summary>
    public required bool IsCriticalFinalizer { get; init; }

    /// <summary>同类型存在 GC.ReRegisterForFinalize 站点（resurrection 高风险，FN-7）。</summary>
    public required bool HasResurrectionSite { get; init; }

    /// <summary>同类型 GC.SuppressFinalize 站点数（FN-7 清点）。</summary>
    public required int SuppressFinalizeSiteCount { get; init; }

    /// <summary>同类型 GC.ReRegisterForFinalize 站点数（FN-7 清点）。</summary>
    public required int ReRegisterSiteCount { get; init; }

    /// <summary>同类型 GC.WaitForPendingFinalizers 站点数（FN-7 清点）。</summary>
    public required int WaitForPendingFinalizersSiteCount { get; init; }

    /// <summary>终结器体原文（M2 sidecar 留痕使用；M1 仅承载）。</summary>
    public string? BodyText { get; init; }

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Destructor.ContainingType?.Name, StringComparison.Ordinal))
            return false;
        // 终结器经编译器降为 protected override void Finalize()，成员名两处都认。
        return string.Equals(memberName, "Finalize", StringComparison.Ordinal)
            || string.Equals(memberName, Destructor.ContainingType?.Name, StringComparison.Ordinal);
    }

    /// <summary>plan Id：FN:{含类型 FQN}@{终结器声明 span 起点}（仿 EventPlan.BuildId）。</summary>
    public static string BuildId(IMethodSymbol destructor, string typeFqn, int spanStart)
        => $"FN:{typeFqn}@{spanStart}";
}
