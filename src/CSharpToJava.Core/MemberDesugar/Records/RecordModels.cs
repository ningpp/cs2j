using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>C27 克隆工厂形态（RC-1/RC-2，设计 §4.3）。</summary>
internal enum RecordFactoryForm
{
    /// <summary>抽象 record：<c>public abstract T Cs2jWithClone();</c></summary>
    Abstract,
    /// <summary>无 record 基类的非密封具体 record：<c>public virtual T Cs2jWithClone() =&gt; new T(this);</c></summary>
    Virtual,
    /// <summary>派生 record（含密封/抽象派生）：<c>public override D Cs2jWithClone() =&gt; new D(this);</c></summary>
    Override,
    /// <summary>无 record 基类的密封 record：非虚普通方法。</summary>
    Sealed,
}

/// <summary>with 初始化器内一个直接成员覆写（左值经 SemanticModel 绑定，禁文本判定）。</summary>
internal sealed class WithAssignment
{
    /// <summary>绑定成员符号（IPropertySymbol / IFieldSymbol；构造泛型已归一 OriginalDefinition）。</summary>
    public required ISymbol Member { get; init; }

    /// <summary>成员源名（set_X/字段直写发射用；显式接口形态不会出现在 with 初始化器）。</summary>
    public required string MemberName { get; init; }

    public bool IsProperty { get; init; }

    /// <summary>原始赋值节点（RHS 在重写期访问；重写发生在任何树重建之前，节点在树）。</summary>
    public required AssignmentExpressionSyntax Syntax { get; init; }
}

/// <summary>一个 with 站点的分析快照。<see cref="FailCode"/> 非 null 即 M1 不收编
/// （5220/5223/5224/5225/5226/5227/5228），站点原子不动并留诊断。</summary>
internal sealed class WithSite
{
    public required WithExpressionSyntax Syntax { get; init; }
    public required string FilePath { get; init; }
    public required int SpanStart { get; init; }

    /// <summary>接收者静态类型（dynamic/绑定失败为 null；元数据/匿名/元组为非源内命名类型）。</summary>
    public INamedTypeSymbol? ReceiverType { get; init; }

    /// <summary>接收者类型声明是否在本编译源树内（false→orphan 载体 plan）。</summary>
    public required bool ReceiverInSource { get; init; }

    /// <summary>WR-3：空初始化器（纯工厂内联，无需提升）。</summary>
    public required bool IsEmpty { get; init; }

    /// <summary>直接成员覆写（空初始化器为空；嵌套初始化器在闸门命中时可能不完整）。</summary>
    public required IReadOnlyList<WithAssignment> Assignments { get; init; }

    public MemberDesugarDiagnosticCode? FailCode { get; init; }
    public string? FailReason { get; init; }

    public bool IsRewritable => FailCode == null;
}

/// <summary>
/// 每个源内 record 命名类型一条（设计决策 15）；另以 <see cref="IsOrphan"/> 载体 plan
/// 承载接收者类型无源码声明（元数据 record/元组/dynamic/匿名/非 record 元数据类型）的站点。
/// M1 与 FinalizerPlan 同形：Outcome 恒为 Planned，跳过诊断由 Records 重写阶段按
/// 站点/声明位置自行发射（框架 EmitSkipDiagnostics 只能锚定 Symbol 首个源位置，无法表达
/// 5223-5228 的逐站点位置与无源码接收者）。工厂合成与站点跳过相互独立：派生链需要时
/// 即使本类型站点被闸门跳过，工厂仍可合成（WR-2 虚分派链完整性）。
/// </summary>
internal sealed class RecordClonePlan : MemberDesugarPlan
{
    /// <summary>接收者 record 类型；orphan 载体为 null（Symbol 锚定编译程序集符号）。</summary>
    public INamedTypeSymbol? RecordType { get; init; }

    /// <summary>true=逐文件 orphan 站点载体（无声明侧动作）。</summary>
    public required bool IsOrphan { get; init; }

    /// <summary>本 plan 是否为位置 record（M1 整 plan 5220）。</summary>
    public bool IsPositional { get; init; }

    /// <summary>工厂形态；null=不发工厂（orphan/struct/位置 record/非 record）。</summary>
    public RecordFactoryForm? FactoryForm { get; init; }

    /// <summary>继承链统一工厂定名（plan 期算定，禁运行时拼名）。</summary>
    public string? FactoryName { get; init; }

    /// <summary>幂等：同签名工厂已存在（首轮产物或用户手写），不重复注入。</summary>
    public bool FactoryAlreadyExists { get; init; }

    /// <summary>派生链闭包要求本类型必须带工厂（即使自身无 with 站点）。</summary>
    public required bool FactoryRequired { get; init; }

    /// <summary>类型级闸门码（5220 位置/struct、5221 树外派生洞等）：命中后本类型
    /// 全部站点不动；工厂仍按 <see cref="FactoryRequired"/> 合成。</summary>
    public MemberDesugarDiagnosticCode? GateCode { get; init; }
    public string? GateReason { get; init; }

    /// <summary>静态接收者为本类型的全部 with 站点（含被闸门站点）。</summary>
    public required IReadOnlyList<WithSite> Sites { get; init; }

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (IsOrphan)
        {
            // orphan 站点的新增错误只能归属到文件；同文件多 plan 时回退族级 5100。
            return true;
        }
        if (!string.Equals(typeName, RecordType?.Name, StringComparison.Ordinal)) return false;
        if (memberName == null) return true;
        if (FactoryName != null && string.Equals(memberName, FactoryName, StringComparison.Ordinal))
            return true;
        if (Sites.Any(s => s.Assignments.Any(a =>
                string.Equals(a.MemberName, memberName, StringComparison.Ordinal))))
            return true;
        return false;
    }

    public static string BuildId(INamedTypeSymbol type, int spanStart)
        => $"RC:{type.ToDisplayString()}@{spanStart}";

    /// <summary>orphan 载体 Id：RW:{文件}@{站点 span 序列}。</summary>
    public static string BuildOrphanId(string filePath, IEnumerable<int> spans)
        => $"RW:{filePath}@{string.Join(",", spans.OrderBy(s => s))}";
}
