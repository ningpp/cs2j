using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Finalizers;

/// <summary>
/// C39 终结器声明侧 rewriter（M1 仅 A 档：只发诊断、不改树，设计 §9 M1）。
/// 在属性阶段之前基于「原始 root + 原始 semanticModel」运行（M2 将在此删成员，顺序已占好）。
/// 每个终结器仅在其首个声明文件发射：主报码 CS2J5200（strict 升 Error）/CS2J5205/CS2J5208，
/// 以及 Info 码 CS2J5202/5203/5206/5207（始终入 diagnostics，adapter 端按 verbose 过滤显示）；
/// 单文件降级额外发 CS2J5211。Visit 不替换任何节点 → 输出树与原树等价（Changed=false，
/// A 档零 diff/幂等/字节一致）。
/// </summary>
internal sealed class FinalizerRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly IReadOnlyList<FinalizerPlan> _plans;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public FinalizerRewriter(
        SemanticModel model,
        IReadOnlyList<FinalizerPlan> plansForFile,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _plans = plansForFile;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node)
    {
        if (_model.GetDeclaredSymbol(node) is { } method
            && method.MethodKind == MethodKind.Destructor
            && method.ContainingType != null)
        {
            var typeFqn = method.ContainingType.ToDisplayString();
            var plan = _plans.FirstOrDefault(p =>
                string.Equals(p.TypeFqn, typeFqn, StringComparison.Ordinal));
            if (plan != null && IsFirstDeclarationFile(plan))
                EmitForPlan(node, plan);
        }
        return base.VisitDestructorDeclaration(node);
    }

    private void EmitForPlan(DestructorDeclarationSyntax node, FinalizerPlan plan)
    {
        var (line, column) = LineColumn(node);

        // ---- 统计（仅首个声明文件，仿 PropertiesPlanned 口径）----
        _stats.FinalizersDetected++;
        switch (plan.BodyKind)
        {
            case FinalizerBodyKind.Empty: _stats.FinalizerEmpty++; break;
            case FinalizerBodyKind.DisposePattern: _stats.FinalizerDisposePattern++; break;
            case FinalizerBodyKind.SafeHandleOnly: _stats.FinalizerSafeHandleOnly++; break;
            case FinalizerBodyKind.NativeRelease: _stats.FinalizerNativeRelease++; break;
            case FinalizerBodyKind.Extern: _stats.FinalizerExtern++; break;
            default: _stats.FinalizerOther++; break;
        }
        if (plan.HasResurrectionSite) _stats.FinalizerResurrectionSites += plan.ReRegisterSiteCount;
        _stats.FinalizerSuppressFinalizeSites += plan.SuppressFinalizeSiteCount;
        if (plan.HasBaseFinalizer) _stats.FinalizerChainsWithBaseFinalizer++;

        // ---- 主报码（每个终结器恰好一条）----
        if (plan.IsCriticalFinalizer)
        {
            // CS2J5205：任何 Policy 下不可解除；成员保留；不再发 5200。
            Emit(MemberDesugarSeverity.Error, MemberDesugarDiagnosticCode.FinalizerCriticalBlocked,
                FinalizerDiagnostics.CriticalBlocked(plan.TypeFqn), line, column);
            _stats.FinalizerCriticalBlocked++;
        }
        else if (_options.Finalizers.Policy == FinalizerPolicy.CleanerGuide)
        {
            // CS2J5208：v1 未实现（设计 §2 决策 8）。
            Emit(MemberDesugarSeverity.Error, MemberDesugarDiagnosticCode.FinalizerCleanerGuideUnsupported,
                FinalizerDiagnostics.CleanerGuideUnsupported(plan.TypeFqn), line, column);
        }
        else
        {
            // CS2J5200：出厂 Warning，strict 升 Error（M1 不翻 Error，设计 §2 决策 2）。
            Emit(_options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                MemberDesugarDiagnosticCode.FinalizerRejected,
                FinalizerDiagnostics.Rejected(plan.TypeFqn, plan.BodyKind, plan.Pairing),
                line, column);
        }
        _stats.FinalizersReported++;

        // ---- 单文件降级（CS2J5211，附加 Warning）----
        if (plan.BaseInfoDegraded)
            Emit(MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.FinalizerSingleFileDegraded,
                FinalizerDiagnostics.SingleFileDegraded(plan.TypeFqn), line, column);

        // ---- Info 码（始终入列表，adapter 端按 verbose 过滤）----
        if (plan.BodyKind == FinalizerBodyKind.DisposePattern
            && plan.Pairing == SuppressFinalizePairing.Paired)
        {
            Emit(MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.FinalizerDisposePattern,
                FinalizerDiagnostics.DisposePattern(plan.TypeFqn), line, column);
        }
        if (plan.BodyKind is FinalizerBodyKind.Empty or FinalizerBodyKind.SafeHandleOnly)
        {
            Emit(MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.FinalizerRedundant,
                FinalizerDiagnostics.Redundant(plan.TypeFqn, plan.BodyKind), line, column);
        }
        if (plan.SuppressFinalizeSiteCount > 0 || plan.ReRegisterSiteCount > 0
            || plan.WaitForPendingFinalizersSiteCount > 0)
        {
            Emit(MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.FinalizerGcLifecycleSite,
                FinalizerDiagnostics.GcLifecycleSites(
                    plan.TypeFqn, plan.SuppressFinalizeSiteCount, plan.ReRegisterSiteCount,
                    plan.WaitForPendingFinalizersSiteCount),
                line, column);
        }
        if (plan.HasBaseFinalizer)
        {
            Emit(MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.FinalizerBaseChain,
                FinalizerDiagnostics.BaseChain(plan.TypeFqn, plan.BaseFinalizerFqn, plan.BaseInfoDegraded),
                line, column);
        }
    }

    private void Emit(
        MemberDesugarSeverity severity, MemberDesugarDiagnosticCode code,
        string message, int line, int column)
        => _diagnostics.Add(new MemberDesugarDiagnostic(
            severity, code, message, _filePath, line, column, null));

    private (int Line, int Column) LineColumn(DestructorDeclarationSyntax node)
    {
        var span = node.GetLocation().GetLineSpan();
        return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private bool IsFirstDeclarationFile(FinalizerPlan plan)
    {
        var first = plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return first != null
            && string.Equals(first.SourceTree?.FilePath, _filePath, StringComparison.OrdinalIgnoreCase);
    }
}
