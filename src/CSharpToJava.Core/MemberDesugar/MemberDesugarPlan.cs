using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar;

internal enum MemberDesugarKind
{
    Property,
    Indexer,   // C12
    Event,     // C13
    Operator,  // C14
    Signature, // C46 预留
    Finalizer, // C39：置于最后，保证程序集发现排序中终结器规则最后运行
}

internal enum MemberDesugarOutcome
{
    Planned,
    Infeasible,   // 语言/上下文不可行 → 声明与站点原子不动 + 必留诊断
    UserSkipped,  // overrides.skip
}

/// <summary>去糖 plan 基类：一个成员符号一个 plan，plan 是原子回退单位（设计 §1.3/§4.3）。</summary>
internal abstract class MemberDesugarPlan
{
    public required string Id { get; init; }
    public required MemberDesugarKind Kind { get; init; }
    public required ISymbol Symbol { get; init; }
    public required MemberDesugarOutcome Outcome { get; init; }
    /// <summary>声明部件文件 + 全部使用点文件（含接口/实现联动文件）。</summary>
    public required IReadOnlyList<string> TouchedFiles { get; init; }
    public MemberDesugarDiagnosticCode? FailCode { get; init; }
    public string? FailReason { get; init; }

    public string SymbolName => Symbol.Name;

    /// <summary>错误归属谓词（同 JK OwnsTouch）：文件 + 类型名 + 成员名（原名或生成名）。</summary>
    public abstract bool OwnsTouch(string filePath, string? typeName, string? memberName);
}

internal sealed class MemberDesugarPlanBuilder
{
    private readonly List<MemberDesugarPlan> _plans = new();

    public void Add(MemberDesugarPlan plan) => _plans.Add(plan);

    /// <summary>确定性排序：Id（符号完全限定名 + 源 span，设计 §4.3-2）。</summary>
    public IReadOnlyList<MemberDesugarPlan> Build()
        => _plans.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
}

/// <summary>一次整树分析的结果载体，规则经此读取共享索引（设计 §4.3-1）。</summary>
internal sealed class MemberDesugarAnalysisContext
{
    public required CSharpCompilation Compilation { get; init; }
    public required MemberDesugarOptions Options { get; init; }
    public required IReadOnlyList<SemanticModel> Models { get; init; }
    /// <summary>全部源内非 indexer 属性符号（partial 多部件按符号天然去重）。</summary>
    public required IReadOnlyList<IPropertySymbol> SourceProperties { get; init; }

    /// <summary>决策 9：绑到元数据-only 用户运算符的站点计数，按文件键（重写期并入统计）。</summary>
    public Dictionary<string, int> MetadataOperatorSitesByFile { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public SemanticModel ModelFor(SyntaxTree tree) => Compilation.GetSemanticModel(tree);
}
