using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.JavaKeywords;

internal enum PlanOutcome { Converted, Infeasible, UserSkipped }

internal enum JavaKeywordPlanKind { KeywordRename, OperatorTrueFalse }

internal sealed class JavaKeywordPlan
{
    public required string Id { get; init; }
    public required JavaKeywordPlanKind Kind { get; init; }
    public required ISymbol Symbol { get; init; }
    public required string SymbolName { get; init; }
    public PlanOutcome Outcome { get; set; } = PlanOutcome.Converted;
    public JavaKeywordDiagnosticCode? FailCode { get; set; }
    public string? FailReason { get; set; }
    public Location? FailLocation { get; set; }
    public bool Feasible => Outcome == PlanOutcome.Converted;

    /// <summary>该 plan 改名后的新名（KeywordRename 用）。</summary>
    public string? NewName { get; set; }

    /// <summary>运算符 plan 所属类型（OperatorTrueFalse 用）。</summary>
    public INamedTypeSymbol? OperatorType { get; set; }

    /// <summary>运算符 plan 的布尔使用点清单（分析期填充）。</summary>
    public List<BooleanSiteInfo> BooleanSites { get; } = new();

    public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PlanTouch> Touches { get; } = new();

    public bool OwnsTouch(string file, string? typeName, string? memberName)
    {
        foreach (var touch in Touches)
        {
            if (!string.Equals(touch.File, file, StringComparison.OrdinalIgnoreCase))
                continue;
            if (typeName != null && !string.Equals(touch.TypeName, typeName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (memberName != null && !string.Equals(touch.MemberName, memberName, StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }
        return false;
    }
}

internal sealed record PlanTouch(string File, string? TypeName, string? MemberName);

/// <summary>
/// 编译器实际绑定 op_true/op_false 的一个使用点（JK-6）。
/// </summary>
internal sealed class BooleanSiteInfo
{
    public required Location Location { get; init; }
    public required string FilePath { get; init; }
    public required SyntaxNode Node { get; init; }
    public required BooleanSiteKind Kind { get; init; }
    /// <summary>运算符所属声明类型（接收者类型）。</summary>
    public required INamedTypeSymbol OperatorType { get; init; }
    /// <summary>是否位于表达式树 lambda 内（JK-8 闸门）。</summary>
    public bool InExpressionTree { get; set; }
    /// <summary>是否 dynamic 绑定（JK-8 闸门）。</summary>
    public bool IsDynamic { get; set; }
    /// <summary>是否需要提升临时变量但处于不可插入语句的表达式上下文（JK-8 闸门）。</summary>
    public bool NeedsHoistButCannot { get; set; }
}

internal enum BooleanSiteKind
{
    IfWhileDoFor,         // JK-6a
    Conditional,          // JK-6b
    WhenGuard,            // JK-6c
    AndAlso,              // JK-6d
    OrElse,               // JK-6e
}

internal sealed class ConversionPlan
{
    public List<JavaKeywordPlan> Plans { get; } = new();
}

internal sealed class JavaKeywordAnalysis
{
    /// <summary>全编译唯一改名表 ISymbol → newName（SymbolEqualityComparer.Default 键）。</summary>
    public Dictionary<ISymbol, string> RenameTable { get; } = new(SymbolEqualityComparer.Default);

    /// <summary>声明了 operator true/false 的类型及其布尔站点。</summary>
    public List<OperatorTypeInfo> OperatorTypes { get; } = new();
}

internal sealed class OperatorTypeInfo
{
    public required INamedTypeSymbol Type { get; init; }
    public required IMethodSymbol OpTrue { get; init; }
    public required IMethodSymbol OpFalse { get; init; }
    public List<BooleanSiteInfo> Sites { get; } = new();
    public bool Infeasible { get; set; }
    public string? InfeasibleReason { get; set; }
}

internal sealed record RewriteContext(
    SemanticModel Model,
    JavaKeywordAnalysis Analysis,
    ConversionPlan Plan,
    IReadOnlyList<JavaKeywordPlan> ActivePlans,
    IReadOnlySet<string> ExcludedPlanIds,
    JavaKeywordStatistics Statistics,
    List<JavaKeywordDiagnostic> Diagnostics,
    string FilePath);

/// <summary>
/// Phase 2：从分析结果构建 plan 列表。关键字改名 plan 永不 Infeasible；
/// 运算符 plan 按 JK-8 判定 Infeasible。
/// </summary>
internal sealed class PlanBuilder
{
    private readonly JavaKeywordOptions _options;

    public PlanBuilder(JavaKeywordOptions options)
    {
        _options = options;
    }

    public ConversionPlan Build(JavaKeywordAnalysis analysis)
    {
        var plan = new ConversionPlan();
        var idCounter = 0;

        // 关键字改名 plan
        foreach (var (symbol, newName) in analysis.RenameTable)
        {
            var p = new JavaKeywordPlan
            {
                Id = $"jk-rename-{idCounter++}",
                Kind = JavaKeywordPlanKind.KeywordRename,
                Symbol = symbol,
                SymbolName = symbol.Name,
                NewName = newName,
            };

            // TouchedFiles = 符号全部源内位置所在文件
            foreach (var loc in symbol.Locations.Where(l => l.IsInSource))
            {
                var file = loc.SourceTree?.FilePath;
                if (file != null)
                {
                    p.TouchedFiles.Add(file);
                    p.Touches.Add(new PlanTouch(file, GetTypeName(symbol), symbol.Name));
                }
            }

            // For partial methods, also include the implementation part's
            // source locations so that all partial files are covered by
            // this plan (the definition part's Locations only include the
            // defining declaration file).
            if (symbol is IMethodSymbol method
                && method.PartialImplementationPart is { } impl)
            {
                foreach (var loc in impl.Locations.Where(l => l.IsInSource))
                {
                    var file = loc.SourceTree?.FilePath;
                    if (file != null)
                    {
                        p.TouchedFiles.Add(file);
                        p.Touches.Add(new PlanTouch(file, GetTypeName(symbol), symbol.Name));
                    }
                }
            }

            plan.Plans.Add(p);
        }

        // 运算符 plan
        foreach (var opType in analysis.OperatorTypes)
        {
            var qualifiedName = opType.Type.ToDisplayString();
            if (_options.SkipQualifiedNames.Contains(qualifiedName))
            {
                var p = new JavaKeywordPlan
                {
                    Id = $"jk-operator-{idCounter++}",
                    Kind = JavaKeywordPlanKind.OperatorTrueFalse,
                    Symbol = opType.Type,
                    SymbolName = qualifiedName,
                    OperatorType = opType.Type,
                    Outcome = PlanOutcome.UserSkipped,
                    FailCode = JavaKeywordDiagnosticCode.JK010,
                };
                foreach (var loc in opType.Type.Locations.Where(l => l.IsInSource))
                {
                    var file = loc.SourceTree?.FilePath;
                    if (file != null)
                    {
                        p.TouchedFiles.Add(file);
                        p.Touches.Add(new PlanTouch(file, opType.Type.Name, null));
                    }
                }
                plan.Plans.Add(p);
                continue;
            }

            var opPlan = new JavaKeywordPlan
            {
                Id = $"jk-operator-{idCounter++}",
                Kind = JavaKeywordPlanKind.OperatorTrueFalse,
                Symbol = opType.Type,
                SymbolName = qualifiedName,
                OperatorType = opType.Type,
            };

            if (opType.Infeasible)
            {
                opPlan.Outcome = PlanOutcome.Infeasible;
                opPlan.FailCode = JavaKeywordDiagnosticCode.JK020;
                opPlan.FailReason = opType.InfeasibleReason;
            }

            foreach (var loc in opType.Type.Locations.Where(l => l.IsInSource))
            {
                var file = loc.SourceTree?.FilePath;
                if (file != null)
                {
                    opPlan.TouchedFiles.Add(file);
                    opPlan.Touches.Add(new PlanTouch(file, opType.Type.Name, null));
                }
            }

            plan.Plans.Add(opPlan);
        }

        return plan;
    }

    private static string? GetTypeName(ISymbol symbol)
    {
        return symbol.ContainingType?.Name;
    }
}
