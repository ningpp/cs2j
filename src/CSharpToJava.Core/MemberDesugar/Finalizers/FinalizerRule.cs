using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Finalizers;

/// <summary>
/// C39 finalizer 规则（Kind=Finalizer, Order=50，FN-1~FN-5/FN-7，设计 §4.2）。三阶段：
/// 1) 遍历 context.Models 枚举全部 DestructorDeclarationSyntax，经符号
///    MethodKind.Destructor 核实，按含入类型去重（每类型至多一个终结器）；
/// 2) 体分类（<see cref="FinalizerBodyClassifier"/>）+ Dispose/SuppressFinalize 配对（FN-3）+
///    基类终结器链（FN-4，含元数据基类，缺信息降级不报错）+ GC 生命周期 API 清点（FN-7）+
///    CriticalFinalizerObject 判定（含跨程序集元数据）；单文件模式保守降级；
/// 3) 每个终结器产出一个 <see cref="FinalizerPlan"/>，TouchedFiles=声明文件集合（无使用点）。
/// 确定性排序由 <see cref="MemberDesugarPlanBuilder.Build"/> 按 Id 完成。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Finalizer, 50)]
internal sealed class FinalizerRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Finalizer;
    public int Order => 50;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var compilation = context.Compilation;
        var singleFile = compilation.SyntaxTrees.Count() == 1;

        // ---- 阶段 1：声明枚举 + 符号核实 + 按含入类型去重 ----
        var destructors = new List<(IMethodSymbol Method, DestructorDeclarationSyntax Syntax, SemanticModel Model)>();
        var seenTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = model.SyntaxTree.GetRoot();
            foreach (var node in root.DescendantNodes().OfType<DestructorDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(node) is not { } method) continue;
                if (method.MethodKind != MethodKind.Destructor) continue;
                var containingType = method.ContainingType;
                if (containingType == null) continue;
                if (!seenTypes.Add(containingType)) continue; // 每类型至多一个 plan
                destructors.Add((method, node, model));
            }
        }

        if (destructors.Count == 0) return;

        // ---- 阶段 2a：GC 生命周期 API 清点（全树一次，按含入类型 FQN 汇总，FN-7）----
        var inventory = BuildGcInventory(context);
        var criticalFinalizerObject =
            compilation.GetTypeByMetadataName("System.Runtime.ConstrainedExecution.CriticalFinalizerObject");

        // ---- 阶段 2b/3：逐终结器分析 + plan 产出 ----
        foreach (var (method, syntax, model) in destructors)
        {
            var type = method.ContainingType!;
            var typeFqn = type.ToDisplayString();

            var baseInfoDegraded = singleFile;
            var hasBaseFinalizer = TryFindBaseFinalizer(type, ref baseInfoDegraded, out var baseFqn);
            var isCritical = IsCriticalFinalizer(type, criticalFinalizerObject);

            var bodyKind = FinalizerBodyClassifier.Classify(syntax, model, method, singleFile);
            var pairing = ComputePairing(type, inventory, typeFqn, out var suppressThis);
            inventory.TryGetValue(typeFqn, out var sites);

            var touched = method.Locations.Where(l => l.IsInSource)
                .Select(l => l.SourceTree!.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var span = method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;

            builder.Add(new FinalizerPlan
            {
                Id = FinalizerPlan.BuildId(method, typeFqn, span),
                Kind = MemberDesugarKind.Finalizer,
                Symbol = method,
                // M1 统一 Planned：C39 全部诊断由 FinalizerRewriter 自行发射，
                // 避免框架 EmitSkipDiagnostics 双发。
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = touched,
                Destructor = method,
                TypeFqn = typeFqn,
                BodyKind = bodyKind,
                Pairing = pairing,
                HasBaseFinalizer = hasBaseFinalizer,
                BaseFinalizerFqn = baseFqn,
                BaseInfoDegraded = baseInfoDegraded,
                IsCriticalFinalizer = isCritical,
                HasResurrectionSite = sites.ReRegister > 0,
                SuppressFinalizeSiteCount = sites.Suppress,
                ReRegisterSiteCount = sites.ReRegister,
                WaitForPendingFinalizersSiteCount = sites.WaitForPending,
                BodyText = syntax.Body?.ToFullString(),
            });
        }
    }

    // ---- FN-3：Dispose/SuppressFinalize 配对 ----

    private static SuppressFinalizePairing ComputePairing(
        INamedTypeSymbol type,
        IReadOnlyDictionary<string, GcSites> inventory,
        string typeFqn,
        out int suppressThisSites)
    {
        suppressThisSites = 0;
        var declaresDispose = type.GetMembers().OfType<IMethodSymbol>()
            .Any(m => m.Name == "Dispose" && m.Parameters.Length is 0 or 1);
        if (inventory.TryGetValue(typeFqn, out var sites))
            suppressThisSites = sites.SuppressThis;
        return declaresDispose && suppressThisSites > 0
            ? SuppressFinalizePairing.Paired
            : SuppressFinalizePairing.Unpaired;
    }

    // ---- FN-4：基类终结器链（含元数据基类；缺信息降级不报错）----

    private static bool TryFindBaseFinalizer(
        INamedTypeSymbol type, ref bool degraded, out string? baseFqn)
    {
        baseFqn = null;
        for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            // System.Object.Finalize 是所有类型的隐式根（Roslyn 亦标 MethodKind.Destructor），
            // 不构成有工程意义的「基类终结器链」，跳过（否则每个 plan 都会误报 5207）。
            if (baseType.SpecialType == SpecialType.System_Object)
                continue;
            if (baseType.TypeKind == TypeKind.Error)
            {
                degraded = true;
                continue;
            }
            if (baseType.GetMembers().OfType<IMethodSymbol>()
                .Any(m => m.MethodKind == MethodKind.Destructor))
            {
                baseFqn = baseType.ToDisplayString();
                return true;
            }
        }
        return false;
    }

    private static bool IsCriticalFinalizer(
        INamedTypeSymbol type, INamedTypeSymbol? criticalFinalizerObject)
    {
        if (criticalFinalizerObject == null) return false;
        for (var current = type; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(
                    current.OriginalDefinition, criticalFinalizerObject))
                return true;
        }
        return false;
    }

    // ---- FN-7：GC.SuppressFinalize / ReRegisterForFinalize / WaitForPendingFinalizers 清点 ----

    private static Dictionary<string, GcSites> BuildGcInventory(MemberDesugarAnalysisContext context)
    {
        var result = new Dictionary<string, GcSites>(StringComparer.Ordinal);

        foreach (var model in context.Models)
        {
            var root = model.SyntaxTree.GetRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) continue;
                if (method is not
                    {
                        Name: "SuppressFinalize" or "ReRegisterForFinalize" or "WaitForPendingFinalizers",
                        ContainingType.Name: "GC",
                    })
                    continue;
                if (method.ContainingType.ToDisplayString() != "System.GC") continue;

                var enclosing = invocation.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                if (enclosing == null) continue;
                if (model.GetDeclaredSymbol(enclosing) is not INamedTypeSymbol enclosingType) continue;

                var fqn = enclosingType.ToDisplayString();
                var sites = result.TryGetValue(fqn, out var existing) ? existing : default;
                var isThisArg = method.Name == "SuppressFinalize"
                    && invocation.ArgumentList.Arguments.Count == 1
                    && invocation.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.ThisExpression);
                switch (method.Name)
                {
                    case "SuppressFinalize":
                        sites.Suppress++;
                        if (isThisArg) sites.SuppressThis++;
                        break;
                    case "ReRegisterForFinalize":
                        sites.ReRegister++;
                        break;
                    case "WaitForPendingFinalizers":
                        sites.WaitForPending++;
                        break;
                }
                result[fqn] = sites;
            }
        }

        return result;
    }

    /// <summary>按含入类型汇总的 GC 生命周期站点计数（FN-7）。</summary>
    private struct GcSites
    {
        public int Suppress;
        public int SuppressThis;
        public int ReRegister;
        public int WaitForPending;
    }
}
