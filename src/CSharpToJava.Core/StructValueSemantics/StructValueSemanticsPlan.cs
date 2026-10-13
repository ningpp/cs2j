using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>一个残余 struct 的 shim 决策（设计 §2/§4.2）。</summary>
internal enum ShimKind
{
    /// <summary>合成 private 拷贝构造 + public Clone()。</summary>
    GenerateCopyCtorAndClone,

    /// <summary>用户已提供合规零参 Clone()（SV010），仅做使用点插入。</summary>
    ReuseExistingClone,

    /// <summary>无法生成/复用 shim（SV050/SV051/SV040 族），plan 不激活。</summary>
    Infeasible,
}

/// <summary>plan 终态。</summary>
internal enum ClonePlanOutcome
{
    /// <summary>激活：shim 与全部使用点同生共灭。</summary>
    Active,

    /// <summary>用户 overrides.skip 或保守不可证（SV020）。</summary>
    Skipped,

    /// <summary>语言/shim 不可行（SV040/SV041/SV042/SV043/SV050/SV051）。</summary>
    Infeasible,
}

internal sealed record PlanTouch(string File, string? TypeName, string? MemberName);

/// <summary>
/// SV040-d 消解记录：struct 实例方法中存在直接捕获 this 的逃逸匿名函数/局部函数
/// （CS1673 形态，含 ref-out-in 生成的 ByRef 访问器闭包），Phase 3 对该方法做
/// 方法级 this 持有者改写（copy-in/copy-out 值副本）。
/// </summary>
/// <param name="Method">需改写的实例方法符号。</param>
/// <param name="HasWrites">
/// 方法体（含其匿名函数/局部函数）是否经 this 路由写入实例态；true 时改写需在
/// 所有出口回写 <c>this = holder</c>（try/finally）。
/// </param>
/// <param name="Location">首个触发的捕获位置（诊断/测试用）。</param>
internal sealed record ThisCaptureRewrite(
    IMethodSymbol Method,
    bool HasWrites,
    Location Location);

/// <summary>
/// SV041 消解记录：构造函数 / 属性·索引器·事件访问器 / 含 base 访问的实例方法中的
/// 整体 <c>this = expr</c>（语句形态）无法经持有者路由（构造函数 this 起始未赋值、
/// readonly 字段只可在构造函数内赋值、base. 非虚调用不能改道持有者），Phase 3
/// 将其逐字段展开：<c>var __cs2jTaN = expr; this.f1 = __cs2jTaN.f1; …</c>。
/// </summary>
/// <param name="Member">需展开的成员符号（构造函数/访问器/普通方法）。</param>
/// <param name="Location">首个触发的赋值位置（诊断/测试用）。</param>
internal sealed record ThisAssignmentExpansion(
    IMethodSymbol Member,
    Location Location);

/// <summary>
/// 值流站点分类（设计 §4.3 VS-x 全表）。M2 先枚举 VS-1/VS-2/VS-4 原始锚点，
/// VS-3/VS-5 在 M4 补齐；新鲜值/只读实参等精化在 M3 经 ValueCopyFacts 完成。
/// </summary>
internal enum SiteKind
{
    /// <summary>VS-1a：局部/字段/for 初始化器 <c>S x = e;</c>。</summary>
    LocalInit,

    /// <summary>VS-1b：简单赋值右值（局部/字段/属性槽）。</summary>
    Assign,

    /// <summary>VS-1c：链式赋值 <c>a = b = e;</c>（语句位置可提升）。</summary>
    ChainedAssign,

    /// <summary>VS-2a：return 残余 struct。</summary>
    Return,

    /// <summary>VS-2b：残余 struct 值参实参（跨过程只读判定在 M3 精化）。</summary>
    Argument,

    /// <summary>VS-3：防御性接收者变异调用（M4）。</summary>
    DefensiveReceiver,

    /// <summary>VS-4a：struct 数组元素存储 <c>arr[i] = e;</c>。</summary>
    ArrayStore,

    /// <summary>VS-4b：struct 数组元素读取流入消费点（M3 按上下文决定）。</summary>
    ArrayLoad,

    /// <summary>VS-5a：装箱到 object/ValueType/dynamic（M4）。</summary>
    Boxing,

    /// <summary>VS-5b：接口转换（M4）。</summary>
    Interface,

    /// <summary>VS-5c：拆箱消费（M4）。</summary>
    Unbox,
}

/// <summary>
/// 单个值流站点：锚点节点 + 归属 struct plan + 文件/成员触碰面。
/// 判定全部来自 SemanticModel 绑定（硬不变式：禁止纯文本别名猜测）。
/// </summary>
internal sealed class ValueCopySite
{
    public required SiteKind Kind { get; init; }
    public required ClonePlan Plan { get; init; }

    /// <summary>改写锚点（VS-1a/1b/2/4：被复制的表达式；1c：外层赋值表达式；3：调用表达式）。</summary>
    public required SyntaxNode Anchor { get; init; }

    /// <summary>站点所在文件（= Anchor.SyntaxTree.FilePath，冗余缓存便于归集）。</summary>
    public required string FilePath { get; init; }

    /// <summary>错误归属用的最近类型/成员名。</summary>
    public required PlanTouch Touch { get; init; }

    /// <summary>链式赋值/防御性接收者是否处于语句位置（表达式位置 → SV043，M3/M4 裁决）。</summary>
    public bool IsStatementPosition { get; init; } = true;
}

/// <summary>
/// 一个残余可变 struct 的原子计划：声明 shim 与其全部使用点改写同生共灭；
/// plan 回退 = TouchedFiles 全部恢复原状（硬不变式 §1.4-2）。
/// </summary>
internal sealed class ClonePlan
{
    public required string Id { get; init; }
    public required string StructShortName { get; init; }
    public required string StructQualifiedName { get; init; }
    public required INamedTypeSymbol Symbol { get; init; }

    public ShimKind ShimKind { get; set; } = ShimKind.GenerateCopyCtorAndClone;
    public ClonePlanOutcome Outcome { get; set; } = ClonePlanOutcome.Active;
    public StructValueSemanticsDiagnosticCode? FailCode { get; set; }
    public string? FailReason { get; set; }
    public Location? FailLocation { get; set; }

    /// <summary>overrides.force 命中（仅解除保守插入跳过，不解除 SV040 族）。</summary>
    public bool IsForced { get; set; }

    /// <summary>该 plan 是否参与重写（激活且未被排除）。</summary>
    public bool IsActive => Outcome == ClonePlanOutcome.Active;

    /// <summary>声明部件文件 + 全部站点文件（归一为绝对/树路径，比较时 OrdinalIgnoreCase）。</summary>
    public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>错误归属用的细粒度触碰面（文件 + 最近类型/成员）。</summary>
    public List<PlanTouch> Touches { get; } = new();

    /// <summary>变异实例方法集（StructAnalyzer.IsMutating + 传递闭包；VS-3/VS-5b 用）。</summary>
    public HashSet<IMethodSymbol> MutatingMethods { get; }
        = new(SymbolEqualityComparer.Default);

    /// <summary>变异实例属性集（带非 init setter；接口变异成员判定用）。</summary>
    public HashSet<IPropertySymbol> MutableProperties { get; }
        = new(SymbolEqualityComparer.Default);

    /// <summary>全部值流站点（M2：VS-1/2/4 原始锚点；M4 补 VS-3/5）。</summary>
    public List<ValueCopySite> Sites { get; } = new();

    /// <summary>SV040-d：需要做方法级 this 持有者改写的实例方法（分析期定型）。</summary>
    public List<ThisCaptureRewrite> ThisCaptureRewrites { get; } = new();

    /// <summary>SV041：需要做 this 整体赋值字段式展开的成员（分析期定型）。</summary>
    public List<ThisAssignmentExpansion> ThisAssignmentExpansions { get; } = new();

    /// <summary>统计站点数（SV001 Info 措辞用）。</summary>
    public int SiteCount => Sites.Count;

    /// <summary>shim 注入的主声明部件文件（partial 时取首个源声明）。</summary>
    public string? PrimaryDeclarationFile { get; set; }

    /// <summary>合成的拷贝构造（已存在合规拷贝构造时为 null）。</summary>
    public ConstructorDeclarationSyntax? GeneratedCopyConstructor { get; set; }

    /// <summary>合成的 public Clone()（复用既有 Clone 时为 null）。</summary>
    public MethodDeclarationSyntax? GeneratedCloneMethod { get; set; }

    public bool OwnsTouch(string file, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(file)) return false;
        if (typeName == null) return true;
        if (typeName == StructShortName) return true;
        return memberName != null
            && Touches.Any(t => string.Equals(t.MemberName, memberName, StringComparison.Ordinal));
    }

    public (string? File, int Line, int Column) Locate()
    {
        var loc = Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var span = loc?.GetLineSpan().StartLinePosition;
        return (loc?.SourceTree?.FilePath, (span?.Line ?? 0) + 1, (span?.Character ?? 0) + 1);
    }
}

internal sealed class StructValueSemanticsPlanSet
{
    public List<ClonePlan> Plans { get; } = new();
}
