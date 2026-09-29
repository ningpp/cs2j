# C11 Property Desugar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the `member-desugar` CLI preprocessor pass (C11): desugar every source C# property into a backing field plus `get_X`/`set_X` ordinary methods, rewriting all use sites via SemanticModel binding, with whole-tree recompile validation and per-plan atomic rollback — flipping the pass from stub to Implemented (default off).

**Architecture:** Three-stage (analyze whole compilation → plan per property symbol → per-file rewrite) mirroring the existing `java-keywords` / `record-readonly` preprocessor architecture. Core logic lives in `src/CSharpToJava.Core/MemberDesugar/` (shared skeleton + `Properties/` rule files); the CLI adapter `MemberDesugarPassAdapter` drives the iterative copy→rewrite→recompile→rollback loop and reuses the runner stage helpers for restore/verify. Registry flip moves the pass from `PassStubCatalog` into `PassCatalog.Implemented` between `eliminate-goto` and `java-keywords`.

**Tech Stack:** .NET 10, Roslyn (Microsoft.CodeAnalysis.CSharp 4.x), xunit. Build: `dotnet build CSharpToJavaConverter.slnx -c Release`; test: `dotnet test -c Release` on CSharpToJava.Tests / CSharpToJava.ProjectTests.

**Spec:** [2026-09-28-c11-property-desugar-design.md](../specs/2026-09-28-c11-property-desugar-design.md) — decision numbers (决策 N) and rule ids (PD-N) below refer to it.

**Execution environment note:** the design §12 prescribes a worktree `feature/preproc-c11-property-desugar`; this plan executes on the current `dev-3.x` checkout (clean tree at start). Commits happen at task boundaries. No converter (`Transformers/`) code is touched.

---

## File Structure

```
src/CSharpToJava.Core/MemberDesugar/               [Create — shared skeleton, C11 owned]
  MemberDesugarModels.cs        Options/Statistics/Diagnostic/Severity/FileResult + CS2J51xx codes
  MemberDesugarPreprocessor.cs  Entry: ctor/RewriteFile (public), BindCompilation/ExcludePlans/Plans/
                                TryAttributeDiagnostic/FormatAndValidate (internal)
  MemberDesugarAnalyzer.cs      Whole-compilation property symbol + per-tree semantic model index
  MemberDesugarPlan.cs          MemberDesugarKind/Outcome/Plan base/PlanBuilder/AnalysisContext
  BackingFieldNaming.cs         Deterministic backing-field names (wraps JavaNaming) + reservation
  AccessorsToMethods.cs         Accessor → MethodDeclaration construction helpers (modifiers/trivia)
  MemberDesugarSyntaxFacts.cs   RewriteAnnotation, expression-tree/dynamic detection, temp names
  MemberDesugarRules.cs         IMemberDesugarRule + [MemberDesugarRule] + assembly discovery
  Properties/                   [Create — C11 exclusive]
    PropertyDiagnostics.cs      Code constants/messages (CS2J5110-5129)
    PropertyFacts.cs            Classification predicates (auto/full/init/required/hiding/interface)
    PropertyRule.cs             IMemberDesugarRule (Kind=Property, Order=10): gates + plan building
    PropertyPlan.cs             PropertyPlan record (backing field name, accessor info, site classes)
    PropertyDeclarationRewriter.cs  PD-1/PD-2/PD-3/PD-4/PD-5/PD-10: declaration → field + methods
    PropertyUseSiteRewriter.cs  PD-6/PD-7/PD-8/PD-9: reads/writes/compound/++--/method group/base/
                                nameof/ctor field-write/object-initializer lifting
src/CSharpToJava.CLI/Preprocessing/
  PassCatalog.cs                [Modify] PassIds.MemberDesugar + Implemented entry (default off)
  PassStubCatalog.cs            [Modify] delete member-desugar stub entry + Ids.MemberDesugar
  PassChainFactory.cs           [Modify] MemberDesugarOptionsFactory + CreateAdapter branch + toggle
  CliPreprocessChain.cs         [Modify] CliChainRequest.MemberDesugarOptionsFactory + plumb
  Adapters/MemberDesugarPassAdapter.cs  [Create] whole-tree self-driving adapter
src/CSharpToJava.CLI/Program.cs [Modify] BuildMemberDesugarOptions + 2 chain constructions + options
tests/SampleMemberDesugarSolution/                [Create] fixture sln (Lib/App/Tests)
tests/CSharpToJava.ProjectTests/
  CSharpToJava.ProjectTests.csproj [Modify] Content-include fixture
  MemberDesugarPropertyUnitTests.cs [Create] core unit matrix (§7.1)
  MemberDesugarProjectTests.cs      [Create] fixture end-to-end (§7.2)
tests/CSharpToJava.Tests/Preprocessing/
  PassChainOrderTests.cs        [Modify] LockedChain insert; adapter-factory test
  PassStubCatalogTests.cs       [Modify] 23 → 22 stubs
  ProjectPreprocessorsConsistentTests.cs [Modify] member-desugar assertions
```

Task graph: T1→T2→T3→T4→T5 (Core); T6→T7→T8 (CLI); T9 (unit tests, after T5+T8); T10→T11 (fixture+project tests); T12 final validation.

---

### Task 1: Core models and diagnostics

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDiagnostics.cs`

- [ ] **Step 1.1: Write MemberDesugarModels.cs**

```csharp
namespace CSharpToJava.Core.MemberDesugar;

public enum MemberDesugarSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CS2J51xx 诊断码。5100-5109 共享基建；5110-5129 property（C11）；
/// 5130-5189 为 C12/C13/C14 预留。码位只追加不改号（设计 §6）。</summary>
public enum MemberDesugarDiagnosticCode
{
    // 5100-5109 shared infrastructure
    WholeTreeRollback = 5100,       // 新增错误无法归属单一 plan → 全量回退
    PlanRolledBack = 5101,          // 新增错误归属某 plan → 原子回退（也用于本地校验失败）
    RestoreFailed = 5102,
    BuildFailed = 5103,
    TestsFailed = 5104,
    SingleFileNotSelfContained = 5109,
    // 5110-5129 property rules
    PlanCreated = 5110,             // Info，仅 verbose
    BackingFieldGenerated = 5111,   // Info，仅 verbose
    ResidualProperty = 5112,        // PD-12 residual 闸门
    AttributeUnroutable = 5113,     // PD-10
    PartialProperty = 5114,
    RefReturningProperty = 5115,
    StaticAbstractInterfaceProperty = 5116,
    ExpressionTreeSite = 5117,      // force 可解除
    DynamicSite = 5118,
    NonLiftableInitializer = 5119,  // 对象初始化器/复合赋值不可提升
    InitWidened = 5120,             // Info
    RequiredDropped = 5121,         // Info
    FieldKeywordAccessor = 5122,
    PositionalRecordProperty = 5123,
    WithSite = 5124,
}

public sealed record MemberDesugarDiagnostic(
    MemberDesugarSeverity Severity,
    MemberDesugarDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? SymbolName = null)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning CS2J51xx: [Symbol] message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            MemberDesugarSeverity.Error => "error",
            MemberDesugarSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = SymbolName != null ? $"[{SymbolName}] " : string.Empty;
        return $"{loc}{level} CS2J{(int)Code}: {who}{Message}";
    }
}

/// <summary>CLI 汇总行与断言使用的计数器（设计 §5.1 摘要行）。</summary>
public sealed class MemberDesugarStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int PropertiesPlanned { get; set; }
    public int BackingFieldsGenerated { get; set; }
    public int ReadSitesRewritten { get; set; }
    public int WriteSitesRewritten { get; set; }
    public int CompoundSitesRewritten { get; set; }
    public int InitializersRewritten { get; set; }
    public int NameofLiteralized { get; set; }
    public int InitAccessorsWidened { get; set; }
    public int RequiredConstraintsDropped { get; set; }
    public int PlansSkipped { get; set; }
    public int PlansRolledBack { get; set; }

    /// <summary>按诊断码分桶的跳过数（设计 §6：按原因分组）。</summary>
    public Dictionary<MemberDesugarDiagnosticCode, int> SkippedByReason { get; } = new();
}

/// <summary>Overrides 形态（设计决策/§5.2）：
/// skip/force 接受 "Ns.Type"（整类型）或 "Ns.Type::Prop"（单属性）。</summary>
public sealed class MemberDesugarOptions
{
    public IReadOnlySet<string> SkipQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>force：仅解除表达式树项的保守跳过（CS2J5117）。</summary>
    public IReadOnlySet<string> ForceQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>存在跳过时退出码非零（strict 把 Warning 升级 Error；链内默认 false）。</summary>
    public bool Strict { get; init; }

    public bool ReportInfo { get; init; } = true;
}

public sealed record MemberDesugarFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
```

- [ ] **Step 1.2: Write Properties/PropertyDiagnostics.cs**

```csharp
namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>CS2J5110-5129 property 面消息文案（码位常量在
/// <see cref="MemberDesugarDiagnosticCode"/>；本文件只放消息模板，便于统一措辞）。</summary>
internal static class PropertyDiagnostics
{
    public const string SkipSuffix =
        "the property declaration and all of its use sites were left unchanged";

    public static string PartialProperty(string name)
        => $"partial property '{name}' is not supported before partial-merge (C20); {SkipSuffix}.";

    public static string RefReturning(string name)
        => $"ref/ref-readonly returning property '{name}' couples with C01 ref lowering; {SkipSuffix}.";

    public static string StaticAbstractInterface(string name)
        => $"static abstract/virtual interface property '{name}' is gated behind C36; {SkipSuffix}.";

    public static string ExpressionTree(string name)
        => $"property '{name}' is accessed inside an expression-tree lambda; rewriting would " +
           $"change the tree shape; {SkipSuffix} (overrides.force lifts this).";

    public static string Dynamic(string name)
        => $"property name '{name}' is accessed through a dynamic receiver; binding is " +
           $"undecidable; {SkipSuffix}.";

    public static string NonLiftable(string name)
        => $"property '{name}' is assigned in an object initializer / compound assignment in a " +
           $"context that cannot be lifted to statements; {SkipSuffix}.";

    public static string FieldKeyword(string name)
        => $"an accessor of property '{name}' uses the C# 'field' contextual keyword; {SkipSuffix}.";

    public static string PositionalRecord(string name)
        => $"property '{name}' is a positional record parameter property (left to RecordTransformer; C27); {SkipSuffix}.";

    public static string WithSite(string name)
        => $"property '{name}' is referenced by a with-expression initializer (C27 prerequisite missing); {SkipSuffix}.";

    public static string AttributeUnroutable(string name, string attributeName)
        => $"attribute '{attributeName}' on property '{name}' is only applicable to properties " +
           $"and cannot be routed to the backing field or accessor methods; {SkipSuffix}.";
}
```

- [ ] **Step 1.3: Build Core project**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: BUILD SUCCEEDED (new files compile standalone).

- [ ] **Step 1.4: Commit**

```bash
git add src/CSharpToJava.Core/MemberDesugar/
git commit -m "feat(member-desugar): C11 models and CS2J51xx diagnostic surface"
```

---

### Task 2: Shared plan, rules, naming, analyzer, preprocessor skeleton

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPlan.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarRules.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/BackingFieldNaming.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarSyntaxFacts.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarAnalyzer.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/AccessorsToMethods.cs`

- [ ] **Step 2.1: Write MemberDesugarPlan.cs** — kind enum, outcome, abstract plan, plan builder, analysis context.

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar;

internal enum MemberDesugarKind
{
    Property,
    Indexer,   // C12
    Event,     // C13
    Operator,  // C14
    Signature, // C46 预留
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

    public SemanticModel ModelFor(SyntaxTree tree) => Compilation.GetSemanticModel(tree);
}
```

- [ ] **Step 2.2: Write MemberDesugarRules.cs** — rule interface + attribute + discovery (设计 §4.5).

```csharp
namespace CSharpToJava.Core.MemberDesugar;

internal interface IMemberDesugarRule
{
    MemberDesugarKind Kind { get; }
    int Order { get; }
    void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder);
}

/// <summary>种类 + 同 Kind 内确定性顺序。规则经程序集扫描发现，禁止注册表热文件。</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class MemberDesugarRuleAttribute(MemberDesugarKind kind, int order) : Attribute
{
    public MemberDesugarKind Kind { get; } = kind;
    public int Order { get; } = order;
}

internal static class MemberDesugarRuleDiscovery
{
    private static IReadOnlyList<IMemberDesugarRule>? _cached;

    public static IReadOnlyList<IMemberDesugarRule> Discover()
    {
        if (_cached != null) return _cached;
        _cached = typeof(MemberDesugarRuleDiscovery).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && t.GetCustomAttributes(typeof(MemberDesugarRuleAttribute), inherit: false).Length == 1
                && typeof(IMemberDesugarRule).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IMemberDesugarRule)Activator.CreateInstance(t)!)
            .OrderBy(r => r.Kind).ThenBy(r => r.Order)
            .ToList();
        return _cached;
    }
}
```

- [ ] **Step 2.3: Write BackingFieldNaming.cs** — decision 3: reuse `JavaNaming.GetAutoPropertyBackingFieldName` as the base, then disambiguate against ALL declared members (methods/events/nested types — C# CS0102 faces those too) and against already-reserved generated names of this run, appending `"Value"` until free. Deterministic per-type source order.

```csharp
using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>后备字段确定性命名 + 跨规则预留表（设计决策 3）。
/// 基名直接复用 <see cref="JavaNaming.GetAutoPropertyBackingFieldName"/>，
/// 保证「预处理前转换器隐含的 Java 字段名」==「去糖 C# 显式字段名」。</summary>
internal static class BackingFieldNaming
{
    /// <summary>为类型内（按源序）待生成的后备字段一次性算定名字。</summary>
    public static IReadOnlyDictionary<IPropertySymbol, string> Assign(
        IEnumerable<IPropertySymbol> propertiesNeedingFields)
    {
        var result = new Dictionary<IPropertySymbol, string>(SymbolEqualityComparer.Default);
        foreach (var group in propertiesNeedingFields
                     .Where(p => p.ContainingType != null)
                     .GroupBy(p => p.ContainingType!, SymbolEqualityComparer.Default)
                     .OrderBy(g => g.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            // 同类型按源 span 排序（首声明者保留裸名，后撞者加 Value，与 JavaNaming 一致）。
            foreach (var prop in g
                         .OrderBy(p => p.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
                         .ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                var name = JavaNaming.GetAutoPropertyBackingFieldName(prop);
                while (IsTaken(name, prop, g.Key) || reserved.Contains(name))
                    name += "Value";
                reserved.Add(name);
                result[prop] = name;
            }
        }
        return result;
    }

    private static bool IsTaken(string name, IPropertySymbol prop, INamedTypeSymbol type)
        => type.GetMembers(name).Any(m =>
            !SymbolEqualityComparer.Default.Equals(m, prop) && !m.IsImplicitlyDeclared);
}
```

- [ ] **Step 2.4: Write MemberDesugarSyntaxFacts.cs** — RewriteAnnotation, expression-tree containment, dynamic detection, temp-name generation, nameof helper.

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar;

internal static class MemberDesugarSyntaxFacts
{
    public static readonly SyntaxAnnotation RewriteAnnotation = new("Cs2jMemberDesugarRewrite");

    /// <summary>表达式树包含判定（设计决策 12 / 风险 8）：祖先 lambda 的转换类型是
    /// System.Linq.Expressions.Expression&lt;Delegate&gt;。</summary>
    public static bool IsInsideExpressionTree(ExpressionSyntax node, SemanticModel model)
    {
        for (var current = (SyntaxNode?)node; current != null; current = current.Parent)
        {
            if (current is not LambdaExpressionSyntax lambda) continue;
            var converted = model.GetTypeInfo(lambda).ConvertedType;
            if (converted is INamedTypeSymbol { Name: "Expression", Arity: 1 }
                && converted.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions")
                return true;
        }
        return false;
    }

    /// <summary>dynamic 接收者/表达式判定（CS2J5118；force 不解除）。</summary>
    public static bool IsDynamic(ExpressionSyntax node, SemanticModel model)
    {
        var type = model.GetTypeInfo(node).Type;
        return type is { TypeKind: TypeKind.Dynamic }
            || (node as MemberAccessExpressionSyntax)?.Expression is { } recv
            && model.GetTypeInfo(recv).Type is { TypeKind: TypeKind.Dynamic };
    }

    /// <summary>临时变量名：沿用全仓 __cs2j 前缀惯例（设计决策 7）。</summary>
    public static string TempName(ref int counter, string hint = "tmp")
        => $"__cs2j_{hint}{counter++}";
}
```

- [ ] **Step 2.5: Write MemberDesugarAnalyzer.cs** — enumerate source property symbols + per-tree models; builds the shared `MemberDesugarAnalysisContext` and invokes rule discovery + plan building.

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>整树一次的共享分析入口（设计 §4.3-1）：成员索引 + SemanticModel 缓存。
/// 站点枚举/分类由各规则在 Analyze 中按需执行（C12-C14 只读复用本类）。</summary>
internal static class MemberDesugarAnalyzer
{
    public static (MemberDesugarAnalysisContext Context, IReadOnlyList<MemberDesugarPlan> Plans)
        Analyze(CSharpCompilation compilation, MemberDesugarOptions options)
    {
        var models = new List<SemanticModel>();
        var properties = new List<IPropertySymbol>();
        var seen = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.TryGetRoot(out var root) || root is not CompilationUnitSyntax unit) continue;
            var model = compilation.GetSemanticModel(tree);
            models.Add(model);
            foreach (var decl in unit.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } prop || prop.IsIndexer) continue;
                if (!seen.Add(prop)) continue; // partial 多部件按符号去重
                properties.Add(prop);
            }
        }

        var context = new MemberDesugarAnalysisContext
        {
            Compilation = compilation,
            Options = options,
            Models = models,
            SourceProperties = properties,
        };

        var builder = new MemberDesugarPlanBuilder();
        foreach (var rule in MemberDesugarRuleDiscovery.Discover())
            rule.Analyze(context, builder);
        return (context, builder.Build());
    }
}
```

- [ ] **Step 2.6: Write AccessorsToMethods.cs** — shared construction helpers used by the property declaration rewriter (C12/C13 reuse later). Key members (full code in this task's commit):

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>访问器 → 方法声明的共享骨架（设计 §4.5）：修饰符/可访问性/体/显式接口平移。
/// 属性面规则负责归类；本类只负责语法构造。</summary>
internal static class AccessorsToMethods
{
    /// <summary>属性声明 → get 访问器方法名（C# 规范访问器名，设计决策 1）。</summary>
    public static string GetterName(string propertyName) => "get_" + propertyName;

    /// <summary>属性声明 → set 访问器方法名（init 访问器同样映射到 set_，设计决策 4）。</summary>
    public static string SetterName(string propertyName) => "set_" + propertyName;

    /// <summary>合并可访问性：访问器自带修饰符优先，否则用属性的。</summary>
    public static SyntaxTokenList MergeAccessibility(
        PropertyDeclarationSyntax property, AccessorDeclarationSyntax accessor)
    {
        var accessorMods = accessor.Modifiers.Where(m =>
            m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) || m.IsKind(SyntaxKind.InternalKeyword) ||
            m.IsKind(SyntaxKind.ProtectedInternalKeyword) || m.IsKind(SyntaxKind.PrivateProtectedKeyword))
            .ToList();
        var propertyMods = property.Modifiers.Where(m =>
            m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) || m.IsKind(SyntaxKind.InternalKeyword) ||
            m.IsKind(SyntaxKind.ProtectedInternalKeyword) || m.IsKind(SyntaxKind.PrivateProtectedKeyword))
            .ToList();
        // 接口成员不允许任何可访问性修饰符。
        var inInterface = property.Ancestors().OfType<InterfaceDeclarationSyntax>().Any();
        return SyntaxFactory.TokenList(inInterface
            ? Enumerable.Empty<SyntaxToken>()
            : accessorMods.Count > 0 ? accessorMods : propertyMods);
    }

    /// <summary>非可访问性修饰符（static/virtual/override/sealed/new/abstract/extern）平移。</summary>
    public static SyntaxTokenList NonAccessibilityModifiers(
        PropertyDeclarationSyntax property, AccessorDeclarationSyntax accessor)
    {
        var excluded = new HashSet<SyntaxKind>
        {
            SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword, SyntaxKind.ProtectedKeyword,
            SyntaxKind.InternalKeyword, SyntaxKind.ProtectedInternalKeyword,
            SyntaxKind.PrivateProtectedKeyword,
            SyntaxKind.RequiredKeyword, // required 不向方法传递（设计决策 5）
            SyntaxKind.PartialKeyword,
        };
        var mods = property.Modifiers.Where(m => !excluded.Contains(m.Kind())).ToList();
        mods.AddRange(accessor.Modifiers.Where(m =>
            m.IsKind(SyntaxKind.PublicKeyword) == false && excluded.Contains(m.Kind()) == false
            && !m.IsKind(SyntaxKind.PrivateKeyword) && !m.IsKind(SyntaxKind.ProtectedKeyword)
            && !m.IsKind(SyntaxKind.InternalKeyword)));
        return SyntaxFactory.TokenList(mods.Distinct());
    }
}
```

- [ ] **Step 2.7: Build**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: BUILD SUCCEEDED.

- [ ] **Step 2.8: Commit**

```bash
git add src/CSharpToJava.Core/MemberDesugar/
git commit -m "feat(member-desugar): shared plan/rule/naming/analyzer skeleton"
```

---

### Task 3: Property rule — facts, plan, declaration rewriter

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/Properties/PropertyFacts.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Properties/PropertyPlan.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Properties/PropertyDeclarationRewriter.cs`

- [ ] **Step 3.1: Write PropertyFacts.cs** — classification predicates (PD-1/PD-2/PD-3/PD-11 declaration-level gates):

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>属性分类谓词（设计 §4.4 PD-1/PD-2/PD-3/PD-11）。</summary>
internal static class PropertyFacts
{
    /// <summary>自动属性：get/set 访问器体均为分号（语法判），符号非 abstract/extern。</summary>
    public static bool IsAutoProperty(PropertyDeclarationSyntax decl, IPropertySymbol prop)
        => decl.AccessorList != null
           && decl.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null)
           && !prop.IsAbstract && !prop.IsExtern;

    /// <summary>需要后备字段：自动属性，或带初始化器的 get-only 属性（编译器同款降级）。</summary>
    public static bool NeedsBackingField(PropertyDeclarationSyntax decl, IPropertySymbol prop)
        => IsAutoProperty(decl, prop)
           || (prop.GetMethod != null && prop.SetMethod == null && decl.Initializer != null && !prop.IsAbstract);

    /// <summary>初始化器可平移到字段（自动属性 / get-only 带初始化器）。</summary>
    public static bool HasMovableInitializer(PropertyDeclarationSyntax decl)
        => decl.Initializer != null;

    /// <summary>init 访问器（放宽为普通 set，设计决策 4）。</summary>
    public static bool HasInitAccessor(PropertyDeclarationSyntax decl)
        => decl.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

    /// <summary>required 修饰符（编译期约束解除，设计决策 5）。</summary>
    public static bool IsRequired(PropertyDeclarationSyntax decl)
        => decl.Modifiers.Any(SyntaxKind.RequiredKeyword);

    /// <summary>ref/ref-readonly 返回（CS2J5115）。</summary>
    public static bool ReturnsByRef(IPropertySymbol prop)
        => prop.RefKind != RefKind.None;

    /// <summary>partial 属性（CS2J5114；C#13 语法）。</summary>
    public static bool IsPartial(PropertyDeclarationSyntax decl)
        => decl.Modifiers.Any(SyntaxKind.PartialKeyword);

    /// <summary>接口 static abstract/virtual（CS2J5116）。</summary>
    public static bool IsStaticAbstractInterfaceMember(IPropertySymbol prop)
        => prop.IsStatic && (prop.IsAbstract || prop.IsVirtual)
           && prop.ContainingType is { TypeKind: TypeKind.Interface };

    /// <summary>访问器体使用 C#13 field 关键字（CS2J5122）。</summary>
    public static bool UsesFieldKeyword(PropertyDeclarationSyntax decl)
        => decl.AccessorList?.Accessors.Any(a => a.Body != null
               && a.Body.DescendantNodes().OfType<IdentifierNameSyntax>()
                   .Any(i => i.Identifier.ValueText == "field"))
           == true
           || decl.AccessorList?.Accessors.Any(a => a.ExpressionBody != null
               && a.ExpressionBody.DescendantNodes().OfType<IdentifierNameSyntax>()
                   .Any(i => i.Identifier.ValueText == "field"))
           == true;

    /// <summary>位置 record 参数属性（CS2J5123）：编译器合成。</summary>
    public static bool IsPositionalRecordProperty(IPropertySymbol prop)
        => prop.IsImplicitlyDeclared;

    /// <summary>显式接口实现。</summary>
    public static bool IsExplicitInterfaceImplementation(IPropertySymbol prop)
        => prop.ExplicitInterfaceImplementations.Length > 0;

    /// <summary>接口内声明（含 DIM）。</summary>
    public static bool IsInterfaceMember(IPropertySymbol prop)
        => prop.ContainingType is { TypeKind: TypeKind.Interface };
}
```

- [ ] **Step 3.2: Write PropertyPlan.cs**

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Properties;

internal sealed class PropertyPlan : MemberDesugarPlan
{
    public required IPropertySymbol Property { get; init; }
    /// <summary>后备字段名；计算属性为 null。</summary>
    public string? BackingFieldName { get; init; }
    public required string GetterMethodName { get; init; }
    public string? SetterMethodName { get; init; }
    /// <summary>set/init 访问器是否存在（init 亦算，放宽为 set）。</summary>
    public required bool HasSetter { get; init; }
    public required bool HasInitAccessor { get; init; }
    public required bool IsRequired { get; init; }

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Property.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return string.Equals(memberName, Property.Name, StringComparison.Ordinal)
            || (memberName != null && BackingFieldName != null
                && string.Equals(memberName, BackingFieldName, StringComparison.Ordinal))
            || string.Equals(memberName, GetterMethodName, StringComparison.Ordinal)
            || (SetterMethodName != null
                && string.Equals(memberName, SetterMethodName, StringComparison.Ordinal));
    }

    public static string BuildId(IPropertySymbol prop)
    {
        var span = prop.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD:{prop.ContainingType?.ToDisplayString()}::{prop.Name}@{span}";
    }
}
```

- [ ] **Step 3.3: Write PropertyRule.cs** — `[MemberDesugarRule(MemberDesugarKind.Property, 10)]`. Analyze algorithm (complete code in commit):

  1. Collect candidate `PropertyDeclarationSyntax` per tree with declared symbol (skip indexers — `IsIndexer`, decision 9).
  2. **Overrides**: `Ns.Type` or `Ns.Type::Prop` matched against `prop.ContainingType.ToDisplayString()` / `+ "::" + prop.Name` → `UserSkipped` (TouchedFiles = declaration files only).
  3. **Declaration gates** (→ `Infeasible` with code; TouchedFiles = declaration part files):
     - `IsPartial` → 5114; `ReturnsByRef` → 5115; `IsStaticAbstractInterfaceMember` → 5116; `UsesFieldKeyword` → 5122; `IsPositionalRecordProperty` → 5123.
  4. **Attribute gate**: compute routing for every attribute on the declaration (see PropertyDeclarationRewriter.RouteAttributes spec below); any unroutable → 5113.
  5. **Site scan** (per tree, walking all expressions once):
     - reads: `MemberAccess`/`IdentifierName`/`MemberBinding` (conditional access) bound to candidate symbol → record site file;
     - writes: `AssignmentExpression.Left` bound → record; compound additionally checks liftable (`Parent is ExpressionStatementSyntax` → liftable; else 5119);
     - `++`/`--` on property access: `Parent is ExpressionStatementSyntax` → liftable else 5119;
     - object initializers: `ObjectCreationExpression.Initializer` assignments whose `Left` binds to candidate → liftable only when the creation is (a) `ExpressionStatement.Expression`, (b) single-declarator `LocalDeclarationStatement` initializer, or (c) `ReturnStatement.Expression` with block parent (nested creations inside lifted initializers recurse and stay liftable); otherwise 5119;
     - `WithExpressionSyntax` initializer assignments bound to candidate → 5124;
     - any site with `IsInsideExpressionTree` → 5117 (unless force contains the qualified name);
     - any site with `IsDynamic` or receiver dynamic → 5118;
     - `nameof` sites: parent `NameOfExpressionSyntax` → nameof site (rewritten to literal, PD-9).
  6. **Interface/implementation linkage** (design decision 8): for `IsInterfaceMember` plans add files of all source members returned by `ContainingType.FindImplementationForInterfaceMember(prop)` over all source types (implementing property declaration files).
  7. **Backing fields**: `BackingFieldNaming.Assign` over all Planned properties needing fields (`PropertyFacts.NeedsBackingField`).
  8. Emit `PropertyPlan` per property: `Outcome = Planned/Infeasible/UserSkipped`, `TouchedFiles` = declaration files + site files (+ implementation files).

- [ ] **Step 3.4: Write PropertyDeclarationRewriter.cs** — PD-1/PD-2/PD-3/PD-4/PD-5/PD-10. It is a `CSharpSyntaxRewriter` co-driven with the use-site rewriter (single pass over the original tree with the original semantic model). Core behaviors:

  **`ExpandProperty(PropertyDeclarationSyntax decl, PropertyPlan plan)` → `IReadOnlyList<MemberDeclarationSyntax>`**
  - `NeedsBackingField`:
    - field modifiers: `private` + (`static` if property static) + (`readonly` when get-only **without** init/initializer-settable, i.e. `SetMethod == null`; init → NOT readonly since widened set may run outside ctor);
    - field type = property `Type`; initializer `= expr` moved verbatim (`WithEqualsValue`);
    - field name = `plan.BackingFieldName`.
  - getter method: `RetType get_X()`; auto → `=> fieldName;` (expression body `=> x;`? — use block `{ return x; }` for stability); full → accessor body/expression body moved verbatim (`value` is now a real parameter).
  - setter method (when HasSetter): `void set_X(T value)`; auto → `{ fieldName = value; }`; full → body moved.
  - modifiers: `AccessorsToMethods.MergeAccessibility` per accessor + `NonAccessibilityModifiers` (`virtual/override/sealed/abstract/static/new`); `abstract` → no body, no field (design PD-4).
  - explicit interface implementation: `ExplicitInterfaceSpecifier` moved to each method (`RetType IFoo.get_X()`), no accessibility modifiers.
  - interface member: methods without accessibility; DIM keeps accessor bodies (C# default interface methods).
  - attribute routing (PD-10 / decision 14), per attribute list on the declaration and on accessors:
    - `[field: X]` → field (when generated; else if `AttributeTargets.Method` allowed → methods; else unroutable → 5113 at analysis);
    - accessor attribute list → that accessor's method (keep any `[return:]` target on getter);
    - property-level/default: look up `AttributeUsageAttribute` of the attribute class (`attributeClass.GetAttributes()` → constructor arg 0 as `(AttributeTargets)`; absent ⇒ `AttributeTargets.All`): `Field` allowed && field generated → field; else `Method` allowed → methods; else unroutable (5113).
  - trivia: getter receives the property's leading trivia (docs follow the getter — current converter convention); field/setter get elastic leading space; every replaced node annotated `RewriteAnnotation` so `FormatAndValidate` formats the changed spans; members inserted field→getter→setter at the property's position.
  - counters: `BackingFieldsGenerated`, `InitAccessorsWidened` (+ Info CS2J5120), `RequiredConstraintsDropped` (+ Info CS2J5121), Info CS2J5110 plan created / CS2J5111 field generated (verbose only).

  **Insertion mechanism:** `PropertyUseSiteRewriter` overrides `VisitClassDeclaration/VisitStructDeclaration/VisitInterfaceDeclaration`, walks `members`, and replaces each planned property node with the expanded list (`SyntaxFactory.List`), preserving surrounding member order.

- [ ] **Step 3.5: Build**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: BUILD SUCCEEDED.

- [ ] **Step 3.6: Commit**

```bash
git add src/CSharpToJava.Core/MemberDesugar/Properties/
git commit -m "feat(member-desugar): property rule, plan and declaration rewriter"
```

---

### Task 4: Use-site rewriter (PD-6..PD-9)

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/Properties/PropertyUseSiteRewriter.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs`

- [ ] **Step 4.1: Write PropertyUseSiteRewriter.cs** — single `CSharpSyntaxRewriter` that also drives declaration expansion (Task 3 insertion hooks). Binding rules (all against the ORIGINAL semantic model; `_active` = `Dictionary<IPropertySymbol, PropertyPlan>` with `SymbolEqualityComparer.Default`):

  | Node | Condition | Rewrite |
  |---|---|---|
  | `IdentifierName` X | binds to active P, not assign-Left, not nameof-arg, not initializer-Left | method group ctx¹ → `get_X` else `get_X()` |
  | `MemberAccess` `recv.X` | same | `recv.get_X()` (`?.X` → `?.get_X()` via `MemberBinding`); `base.X` → `base.get_X()` |
  | `MemberBinding` X (inside conditional access) | binds to active P | wrap `WhenNotNull` into conditional invocation `recv?.get_X()` |
  | `AssignmentExpression` simple, Left binds P | parent not `InitializerExpression` | `set_X(v)` / `recv.set_X(v)` / `base.set_X(v)` |
  | Assignment compound/`??=` | parent `ExpressionStatement`, simple receiver² | `recv.set_X(recv.get_X() op v)`; complex receiver → `{ var __cs2j_tmp = recv; __cs2j_tmp.set_X(__cs2j_tmp.get_X() op v); }` |
  | `++`/`--` postfix/prefix | parent `ExpressionStatement` | `recv.set_X(recv.get_X() ± 1)` (temp-lift for complex receivers) |
  | ctor write, P has no setter, receiver is `this`/implicit | enclosing `IMethodSymbol.MethodKind == Constructor` of declaring type | direct backing-field write `x = v` / `this.x = v` (PD-7) |
  | `NameOfExpression` arg binds P | — | string literal `"X"` (PD-9) |
  | `ExpressionStatement/LocalDeclarationStatement/ReturnStatement` whose expression is a liftable object creation touching active P | ≥1 initializer assignment to active P | temp + set sequence (below) |

  ¹ method group context: `GetTypeInfo(node).ConvertedType` is delegate && `.Type` is not delegate (e.g. `Func<int> f = c.X`), or argument-to-delegate-parameter — emit identifier/method access **without** invocation parens.
  ² simple receiver: `IdentifierName`/`ThisExpression`/`BaseExpression`/`PredefinedType` chains of `MemberAccess` only (side-effect free); anything else (invocation/index/conditional) is complex.

  **Object initializer lifting (PD-8 / decision 7)** for `new T(args) { a1, a2, … }`:
  ```
  var __cs2j_tmp0 = new T(args) { <assignments whose Left is NOT active> };
  <per lifted assignment, source order>:
      A = expr            → __cs2j_tmp0.set_A(<expr>);
      A = new Sub { … }   → recurse lift → var __cs2j_tmp1 = new Sub(){kept}; …; __cs2j_tmp0.set_A(__cs2j_tmp1);
      A = { x = 1, … }    → var __cs2j_b0 = __cs2j_tmp0.get_A(); __cs2j_b0.set_x(1); …   (member initializer)
      A = { 1, 2 }        → var __cs2j_l0 = __cs2j_tmp0.get_A(); __cs2j_l0.Add(1); …     (collection)
      A = { [k] = v }     → var __cs2j_l0 = __cs2j_tmp0.get_A(); __cs2j_l0[k] = v; …      (indexer kept, C12)
  <final>: expression statement → nothing; local decl `var c = …` → `var c = __cs2j_tmp0;`; return → `return __cs2j_tmp0;`
  ```
  Mixed field assignments stay inside the kept object-creation initializer. Liftable statement contexts are exactly the three listed; everything else was gated 5119 at analysis so the rewriter never sees it.

  **Counters:** `ReadSitesRewritten` / `WriteSitesRewritten` / `CompoundSitesRewritten` / `InitializersRewritten` / `NameofLiteralized`.

- [ ] **Step 4.2: Write MemberDesugarPreprocessor.cs** — mirrors `JavaReservedWordPreprocessor` exactly (same member set/visibility):

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>member-desugar 预处理器入口（C11 锚点）：三阶段分析→计划→重写，
/// 计划级排除支持 CLI 回退循环（设计 §4.2/§4.3-3）。</summary>
public sealed partial class MemberDesugarPreprocessor
{
    private readonly MemberDesugarOptions _options;
    private IReadOnlyList<MemberDesugarPlan>? _plans;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);

    public MemberDesugarPreprocessor(MemberDesugarOptions? options = null)
        => _options = options ?? new MemberDesugarOptions();

    public MemberDesugarFileResult RewriteFile(
        string sourceCode, SemanticModel semanticModel,
        MemberDesugarStatistics statistics, List<MemberDesugarDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        EnsureBuilt((CSharpCompilation)semanticModel.Compilation);

        var active = _plans!
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.TouchedFiles.Contains(filePath))
            .ToList();

        foreach (var p in active.Where(p => p.Outcome != MemberDesugarOutcome.Planned))
            EmitSkipDiagnostics(p, filePath, statistics, diagnostics);

        var planned = active.OfType<PropertyPlan>()
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        if (planned.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }

        var rewriter = new PropertyUseSiteRewriter(
            semanticModel, planned.ToDictionary(p => (IPropertySymbol)p.Property, p => p,
                SymbolEqualityComparer.Default),
            _options, statistics, diagnostics, filePath);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        if (newRoot.IsEquivalentTo(root))
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                "member-desugar rewrite failed local parse validation; the original file was " +
                "preserved pending whole-tree validation.", filePath, 0, 0, null));
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;
        return new MemberDesugarFileResult { OutputCode = outputCode, Changed = true };
    }

    internal void BindCompilation(Compilation compilation)
        => EnsureBuilt((CSharpCompilation)compilation);

    internal IReadOnlyList<MemberDesugarPlan> Plans
        => _plans ?? throw new InvalidOperationException(
            "Preprocessor has not analyzed a compilation yet.");

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    internal bool TryAttributeDiagnostic(
        Diagnostic diagnostic,
        IReadOnlyDictionary<string, string> rewrittenByFile,
        out string planId)
    {
        // 与 JK 同款：位置 → (类型名, 成员名) → 唯一 OwnsTouch plan。
        planId = string.Empty;
        if (_plans == null || diagnostic.Location.SourceTree is not { } tree) return false;
        if (!rewrittenByFile.TryGetValue(tree.FilePath, out _)) return false;
        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        var typeName = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.Text;
        var memberName = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault()?.GetNameString();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _plans)
        {
            if (_excludedPlanIds.Contains(plan.Id)) continue;
            if (!plan.TouchedFiles.Contains(tree.FilePath)) continue;
            if (plan.OwnsTouch(tree.FilePath, typeName, memberName)) matched.Add(plan.Id);
        }
        if (matched.Count == 1) { planId = matched.First(); return true; }
        return false;
    }

    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        // 三候选：改动区间格式化 → 整文件格式化 → 原文；重解析零错误取首个（RR/JK 同款）。
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = root.GetAnnotatedNodes(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind)))
                    .Select(n => n.Span).Distinct().OrderBy(s => s.Start).ToList();
                if (spans.Count > 0)
                    candidates.Add(Formatter.Format(root, spans, workspace).ToFullString());
            }
            catch { }
            try { candidates.Add(Formatter.Format(root, workspace).ToFullString()); }
            catch { }
        }
        candidates.Add(root.ToFullString());
        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch { return false; }
        });
    }

    private void EnsureBuilt(CSharpCompilation compilation)
    {
        if (_plans != null) return;
        var (_, plans) = MemberDesugarAnalyzer.Analyze(compilation, _options);
        _plans = plans;
    }

    private void EmitSkipDiagnostics(MemberDesugarPlan p, string filePath,
        MemberDesugarStatistics statistics, List<MemberDesugarDiagnostic> diagnostics)
    {
        if (p.Outcome == MemberDesugarOutcome.UserSkipped)
        {
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.PlanCreated,
                "property plan skipped by overrides.skip configuration",
                filePath, 0, 0, p.SymbolName));
            return;
        }
        var severity = _options.Strict
            ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning;
        var loc = p.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var line = 0; var col = 0;
        if (loc != null)
        {
            var span = loc.GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            col = span.StartLinePosition.Character + 1;
        }
        statistics.PlansSkipped++;
        var code = p.FailCode ?? MemberDesugarDiagnosticCode.ResidualProperty;
        statistics.SkippedByReason.TryGetValue(code, out var count);
        statistics.SkippedByReason[code] = count + 1;
        diagnostics.Add(new MemberDesugarDiagnostic(
            severity, code, p.FailReason ?? "property plan infeasible",
            filePath, line, col, p.SymbolName));
    }
}

internal static class MemberDesugarDeclarationNaming
{
    public static string? GetNameString(this MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };
}
```

  **Residual gate (PD-12)**: `PropertyUseSiteRewriter`, after visiting, scans the OUTPUT for `PropertyDeclarationSyntax` whose declared symbol has no active plan at all (neither planned nor skipped with diagnostics) — any hit emits CS2J5112. By construction every source property owns a plan, so this is a safety net.

- [ ] **Step 4.3: Build**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: BUILD SUCCEEDED.

- [ ] **Step 4.4: Commit**

```bash
git add src/CSharpToJava.Core/MemberDesugar/
git commit -m "feat(member-desugar): use-site rewriter and preprocessor entry"
```

---

### Task 5: Registry flip — stub → Implemented

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs`

- [ ] **Step 5.1: PassIds + Implemented entry.** In `PassCatalog.cs` add to `PassIds`: `public const string MemberDesugar = "member-desugar";` (after `EliminateGoto`). Insert a descriptor in `Implemented` **between the eliminate-goto and java-keywords entries** (design §3.3-1):

```csharp
new(
    PassIds.MemberDesugar,
    "Member desugaring",
    ".cs2j-member-desugar-src",
    PassStage.Declarative,
    PassStatus.Implemented,
    defaultEnabled: false,
    runAfterIds: new[] { PassStubCatalog.Ids.StructValueSemantics, PassStubCatalog.Ids.ClosureLift },
    verifyBuildByDefault: true,
    verifyTestsByDefault: true,
    description: "Desugar properties into get_X/set_X methods and backing fields.",
    catalogItems: "C11"),
```

- [ ] **Step 5.2: Delete the stub.** In `PassStubCatalog.cs` remove the member-desugar entry (the `new( Ids.MemberDesugar, … catalogItems: "C11, C12, C13, C14, C27, C39, C46"),` block) and the `Ids.MemberDesugar` constant (verified: no other source references).

- [ ] **Step 5.3: Build + run existing tests to see expected failures**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release && dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~PassChainOrder|FullyQualifiedName~PassStubCatalog"`
Expected: BUILD SUCCEEDED; `PassChainOrderTests` LockedChain test FAILS (member-desugar now in implemented order), `PassStubCatalogTests` count test FAILS (23 vs 22). These are fixed in Task 7.

- [ ] **Step 5.4: Commit**

```bash
git add src/CSharpToJava.CLI/Preprocessing/
git commit -m "feat(member-desugar): flip member-desugar stub to Implemented (default off)"
```

---

### Task 6: Chain factory, CLI request plumbing, Program wiring

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs`
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 6.1: PassChainConfiguration** — add next to `JavaKeywordOptionsFactory`:

```csharp
/// <summary>Invoked only when member-desugar is enabled (keeps override loading lazy).</summary>
public required Func<CSharpToJava.Core.MemberDesugar.MemberDesugarOptions> MemberDesugarOptionsFactory { get; init; }
```

- [ ] **Step 6.2: CreateAdapter branch** (before `PassIds.JavaKeywords`):

```csharp
PassIds.MemberDesugar => new MemberDesugarPassAdapter(
    config.MemberDesugarOptionsFactory(),
    config.Verbose,
    config.FilterUnsupportedProjects,
    config.CompatNamespacePrefixes),
```

- [ ] **Step 6.3: BuildLegacyToggles** — add `[PassIds.MemberDesugar] = opts.MemberDesugar,`.

- [ ] **Step 6.4: CliChainRequest** — add `public required Func<CSharpToJava.Core.MemberDesugar.MemberDesugarOptions> MemberDesugarOptionsFactory { get; init; }` and plumb `MemberDesugarOptionsFactory = request.MemberDesugarOptionsFactory,` into the `PassChainConfiguration` in `RunAsync`.

- [ ] **Step 6.5: Program.cs** — three edits:
  1. Both `new CliChainRequest { … }` sites (convert-project L~185, preprocess verb L~3469): add
     `MemberDesugarOptionsFactory = () => BuildMemberDesugarOptions(opts.MemberDesugarOverrides, strict: false),`
  2. Add next to `BuildJavaKeywordOptions` (same shape: explicit path → CWD `config/MemberDesugarOverrides.json` → upward walk from `AppContext.BaseDirectory`; parse failure → Warning + empty sets):

```csharp
private sealed class MemberDesugarOverrideSets
{
    public string[]? Force { get; set; }
    public string[]? Skip { get; set; }
}

private static CSharpToJava.Core.MemberDesugar.MemberDesugarOptions BuildMemberDesugarOptions(
    string? explicitPath,
    bool strict,
    IEnumerable<string>? extraForce = null,
    IEnumerable<string>? extraSkip = null)
{
    var path = explicitPath;
    if (string.IsNullOrWhiteSpace(path))
    {
        path = Path.Combine(Directory.GetCurrentDirectory(), "config", "MemberDesugarOverrides.json");
        if (!File.Exists(path))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "config", "MemberDesugarOverrides.json");
                if (File.Exists(candidate)) { path = candidate; break; }
                dir = dir.Parent;
            }
        }
    }

    var force = new HashSet<string>(StringComparer.Ordinal);
    var skip = new HashSet<string>(StringComparer.Ordinal);
    if (path != null && File.Exists(path))
    {
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<MemberDesugarOverrideSets>(
                File.ReadAllText(path)) ?? new MemberDesugarOverrideSets();
            if (parsed.Force != null) force.UnionWith(parsed.Force);
            if (parsed.Skip != null) skip.UnionWith(parsed.Skip);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[Warning] Failed to load member-desugar overrides from {path}: {ex.Message}");
        }
    }
    if (extraForce != null) force.UnionWith(extraForce);
    if (extraSkip != null) skip.UnionWith(extraSkip);

    return new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions
    {
        ForceQualifiedNames = force,
        SkipQualifiedNames = skip,
        Strict = strict,
    };
}
```

  3. `ConvertProjectOptions` — add:

```csharp
[Option("member-desugar", Default = false,
    HelpText = "Enable the property desugaring preprocessing step (properties become get_X/set_X methods plus backing fields)")]
public bool MemberDesugar { get; set; }

[Option("member-desugar-overrides", Required = false,
    HelpText = "Path to MemberDesugarOverrides.json (default: config/MemberDesugarOverrides.json)")]
public string? MemberDesugarOverrides { get; set; }
```

- [ ] **Step 6.6: Fix remaining CliChainRequest construction in tests** — `tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs` CS2J9001 test gets `MemberDesugarOptionsFactory = () => throw new InvalidOperationException("stub must run first"),`.

- [ ] **Step 6.7: Build** (adapter from Task 7 must exist first if compiling CLI — order Tasks 6/7 in one commit if needed; adapter file created in Task 7; build here will fail until then → combine steps so that Task 6 and Task 7 land together, or create the adapter first.)

Run: `dotnet build CSharpToJavaConverter.slnx -c Release`
Expected: BUILD SUCCEEDED after Task 7 adapter exists.

- [ ] **Step 6.8: Commit** (together with Task 7)

---

### Task 7: CLI adapter

**Files:**
- Create: `src/CSharpToJava.CLI/Preprocessing/Adapters/MemberDesugarPassAdapter.cs`

- [ ] **Step 7.1: Write the adapter** — whole-tree self-driving loop copied structurally from `JavaKeywordsPassAdapter` (copy files → baseline error set → iterate { replay originals → BuildCompilation → BindCompilation → ExcludePlans → per-file RewriteFile with BOM-preserving writes → rebuild → DiffErrors → TryAttributeDiagnostic → per-plan rollback; unattributable → whole-run rollback CS2J5100; max 1024 iterations), then the `MakeReadonlyPassAdapter` tail (design §5.1 revised):

```csharp
var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(tree.Layout, intermediateRoot);
if (restoreTarget != null)
{
    context.Restore();                          // failure → OnRestoreFailed (CS2J5102)
    await context.VerifyBuildAndTestsAsync();   // failure → OnBuildFailed/OnTestsFailed (CS2J5103/5104)
    ProjectPassRunner.DeleteBuildOutputs(intermediateRoot);
}
context.Success = success;
```

  Public surface: ctor `(MemberDesugarOptions options, bool verbose, bool filterUnsupportedProjects, IReadOnlyCollection<string>? compatNamespacePrefixes)`; `Descriptor => PassCatalog.ImplementedById(PassIds.MemberDesugar)`; `CreateParseOptions => LanguageVersion.Latest`; `RewriteFileAsync => throw NotSupportedException(...)`; `DeleteBuildOutputsAfterRestore => true`; `Statistics`/`Diagnostics` properties; `HasErrors` = any Error diagnostic; `BuildChainSummaryLine`:

```csharp
public override string BuildChainSummaryLine()
    => $"member-desugar: {_statistics.PropertiesPlanned} propert(y/ies) planned, " +
       $"{_statistics.BackingFieldsGenerated} field(s) generated, " +
       $"{_statistics.ReadSitesRewritten} read/{_statistics.WriteSitesRewritten} write/" +
       $"{_statistics.CompoundSitesRewritten} compound site(s) rewritten, " +
       $"{_statistics.InitializersRewritten} initializer(s), " +
       $"{_statistics.PlansSkipped} skipped, " +
       $"{_statistics.PlansRolledBack} rolled back, " +
       "{residual count} residual";
```

  Diagnostics: `OnRestoreFailed` → CS2J5102 Error; `OnBuildFailed` → CS2J5103 Error; `OnTestsFailed` → CS2J5104 Error. The private `BuildCompilation` mirrors JK (LanguageVersion.Latest + synthetic `__cs2j_global_usings.g.cs` from `ImplicitGlobalUsings.BuildForProject` + `ProjectUnsignedTypePreprocessor.GetBasicReferences()`, `allowUnsafe: true`).

- [ ] **Step 7.2: Build + commit Tasks 6+7**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release`
Expected: BUILD SUCCEEDED.

```bash
git add src/CSharpToJava.CLI/ tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs
git commit -m "feat(member-desugar): CLI adapter, chain factory and options wiring"
```

---

### Task 8: Chain/registry test updates

**Files:**
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs`
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs`
- Modify: `tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs`

- [ ] **Step 8.1: LockedChain** — insert `PassIds.MemberDesugar,` between `PassIds.EliminateGoto,` and `PassIds.JavaKeywords,`. `EnabledByDefault` unchanged (default off). **Verify with the actual topological order** (design §3.2: 实测为准) — the assertion test itself will confirm; if the real order differs, adjust the descriptor's position in `Implemented` (not RunAfter edges).
- [ ] **Step 8.2: adapter-factory test** — add `MemberDesugarOptionsFactory = () => new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions(),` to `ChainFactory_creates_an_adapter_for_every_implemented_pass`'s config.
- [ ] **Step 8.3: PassStubCatalogTests** — `Assert.Equal(23, ids.Length)` → `Assert.Equal(22, ids.Length)`.
- [ ] **Step 8.4: ProjectPreprocessorsConsistentTests** — append (mirroring the generic-deepen block):

```csharp
// member-desugar (C11)：目录常量挂在注册表描述符上；默认关，
// 经 --enable-pass member-desugar / --member-desugar / --pass member-desugar 开启。
Assert.Equal(".cs2j-member-desugar-src",
    PassCatalog.ImplementedById(PassIds.MemberDesugar).IntermediateDirectoryName);
Assert.NotNull(typeof(Adapters.MemberDesugarPassAdapter));
```

- [ ] **Step 8.5: Run**

Run: `dotnet test tests/CSharpToJava.Tests -c Release --filter "FullyQualifiedName~Preprocessing"`
Expected: ALL PASS.

- [ ] **Step 8.6: Commit**

```bash
git add tests/CSharpToJava.Tests/
git commit -m "test(member-desugar): lock chain order and registry assertions"
```

---

### Task 9: Core unit tests (§7.1 matrix)

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/MemberDesugarPropertyUnitTests.cs`

- [ ] **Step 9.1: Write the harness** (mirror `JavaKeywordRewriterUnitTests`: `ParseWithSemantics` with `LanguageVersion.Preview`, `Rewrite(src, options, extraTrees)`, `AssertOutputCompiles`).

- [ ] **Step 9.2: Write the matrix** — each test = inline source → rewrite → whitespace-insensitive `Assert.Contains` on the output + `AssertOutputCompiles` + (for skips) diagnostic-code assertions:

  1. **AutoProperty** `{ get; set; }` → field + `get_X`/`set_X`; get-only → `readonly` field + getter only; `private set`; static; initializer `= 42` moved; `Age`/`age`/explicit `age` field triple-collision → `age`/`ageValue` disambiguation.
  2. **Full/computed**: block bodies; expression bodies; setter expression body delegating to another property (no `_chainVal` artifacts); computed getter-only without field.
  3. **Use sites**: read/write/`+=`/`++`/`--`/`??=`; `Get().X += 1` receiver evaluated once (temp lift); method group `Func<int> f = c.X`; `base.X`; `c?.X`; cross-file (extra tree).
  4. **Ctor field-write**: get-only property assigned in ctor → backing-field write; init property in ctor/initializer → `set_X`.
  5. **Modifiers**: virtual/abstract (no body/field)/override/sealed override/`new` hiding (implicit + explicit).
  6. **Interfaces**: interface property → method decls; DIM with body; explicit implementation `int IFoo.get_X()`; explicit impl + same-name class property coexist and compile.
  7. **init/required**: init → `set_X`; required + object-initializer lifting (nested, multi-property, field mix); non-liftable (field initializer context) → CS2J5119 skip.
  8. **nameof**: `nameof(X)`/`nameof(C.X)` → `"X"`; unrelated nameof untouched.
  9. **Attributes**: `[field: NonSerialized]`-style → field; property-level `Property|Field` usage → field; accessor attributes → method.
  10. **Skip family** (assert `Outcome=Infeasible` diagnostic + output unchanged): expression tree CS2J5117 (+force lifts), dynamic CS2J5118, ref-return CS2J5115, partial CS2J5114, static abstract interface CS2J5116, `field` keyword CS2J5122, positional record CS2J5123, `with` CS2J5124.
  11. **Boundary zero-touch**: indexer declaration + `obj[i]` site unchanged; event `+=`; `operator +`; delegate field unchanged.
  12. **Idempotency**: rewrite(rewrite(src)) == rewrite(src) byte-identical; clean file unchanged.

- [ ] **Step 9.3: Run**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Release --filter "FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: ALL PASS (iterate on implementation until green).

- [ ] **Step 9.4: Commit**

```bash
git add tests/CSharpToJava.ProjectTests/MemberDesugarPropertyUnitTests.cs src/CSharpToJava.Core/MemberDesugar/
git commit -m "test(member-desugar): property unit matrix (design §7.1)"
```

---

### Task 10: Fixture solution

**Files:**
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarSolution.sln`
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/SampleMemberDesugarLib.csproj` (net10.0, xunit-free lib)
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/DtoSamples.cs` — auto/get-only/init/required/static/computed/hiding/explicit-interface/interface+DIM/generic/`Age`+`age` collision/initializer properties
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/InterfaceSamples.cs` — `IPerson` + DIM + explicit impl
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/SkipSamples.cs` — expression-tree-referenced type + non-liftable initializer (isolated type)
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarApp/Program.cs` — cross-file read/write/compound/side-effect receiver/object initializer/interface dispatch
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarTests/SampleMemberDesugarTests.csproj` (net10.0 + xunit reference to Lib)
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarTests/BehaviorTests.cs` — .NET-half behavior gate: value read/write semantics, hiding non-virtual dispatch, explicit interface dispatch, initializer results, method-group delegates, `GetProperty("X") == null && GetMethod("get_X") != null` new-contract pin, `nameof == "X"` literal
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` — Content-include `..\SampleMemberDesugarSolution\**\*.*` (Exclude bin/obj)

- [ ] **Step 10.1: Create the fixture files** (contents as listed; mirror SampleJavaKeywordSolution csproj shapes).
- [ ] **Step 10.2: Fixture builds standalone**

Run: `dotnet build tests/SampleMemberDesugarSolution/SampleMemberDesugarSolution.sln -c Release && dotnet test tests/SampleMemberDesugarSolution/SampleMemberDesugarSolution.sln -c Release`
Expected: build + tests green BEFORE desugaring (baseline behavior).

- [ ] **Step 10.3: Commit**

```bash
git add tests/SampleMemberDesugarSolution/ tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
git commit -m "test(member-desugar): SampleMemberDesugarSolution fixture"
```

---

### Task 11: Project-level tests + CLI surface

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/MemberDesugarProjectTests.cs`

- [ ] **Step 11.1: Write project tests** — construct `MemberDesugarPassAdapter` directly + `ProjectPassRunner.RunAsync` (no facade by design §5.2):

```csharp
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;

namespace CSharpToJava.ProjectTests;

public class MemberDesugarProjectTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    [Fact]
    public async Task Solution_PropertiesDesugared_AndIntermediateBuildsAndTests()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-md-sln");
        try
        {
            var adapter = new MemberDesugarPassAdapter(
                new MemberDesugarOptions(), verbose: false,
                filterUnsupportedProjects: true, compatNamespacePrefixes: null);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            { SourcePath = solutionPath, DestinationRoot = outputDir, Force = true });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Where(d => d.Severity != MemberDesugarSeverity.Info)
                    .Select(d => d.ToMsBuildString())));
            Assert.True(adapter.Statistics.PropertiesPlanned > 0);

            var dtos = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib", "DtoSamples.cs"));
            Assert.Contains("get_Total()", dtos);
            Assert.Contains("set_Total(", dtos);

            // VerifyBuild/VerifyTests default on → run.Success already proves the
            // intermediate tree built AND passed dotnet test (design §7.2).
        }
        finally { if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true); }
    }
}
```

  Plus: `Skipped_ExpressionTree_Property_Keeps_Declaration_And_Still_Succeeds` (asserts a CS2J5117 diagnostic exists and the skip-samples type still contains its property in the intermediate tree); `Clean_Tree_Is_Byte_Copied` (project with no properties → all files byte-identical, BOM preserved on a crafted BOM file).

- [ ] **Step 11.2: Run**

Run: `dotnet test tests/CSharpToJava.ProjectTests -c Release --filter "FullyQualifiedName~MemberDesugar"`
Expected: ALL PASS (this exercises the full adapter: rollback loop, restore, build+test verify).

- [ ] **Step 11.3: CLI smoke** — `--list-passes` shows member-desugar Implemented/default-off; `preprocess --pass member-desugar -s <fixture sln> -d <tmp>` exits 0; convert-project without the flag creates no `.cs2j-member-desugar-src`.

Run: `dotnet run --project src/CSharpToJava.CLI -c Release -- --list-passes | grep member-desugar`
Expected: line listing member-desugar as implemented, default off, directory `.cs2j-member-desugar-src`.

- [ ] **Step 11.4: Commit**

```bash
git add tests/CSharpToJava.ProjectTests/
git commit -m "test(member-desugar): project-level fixture tests and CLI surface"
```

---

### Task 12: Final validation (§7.3/§7.5)

- [ ] **Step 12.1: Full solution build**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release`
Expected: 0 errors/warnings delta.

- [ ] **Step 12.2: All three test projects**

Run: `dotnet test tests/CSharpToJava.Tests -c Release && dotnet test tests/CSharpToJava.ProjectTests -c Release && dotnet test tests/CSharpToJava.ConversionTests -c Release`
Expected: ALL PASS (existing PropertyTransformerTests etc. untouched and green — they guard the direct single-file converter path, design §7.6).

- [ ] **Step 12.3: Residual gate spot-check** — unit test asserting no `PropertyDeclarationSyntax` remains in desugared output except skip-diagnosed ones (already in Task 9-12; confirm green).

- [ ] **Step 12.4: Final commit**

```bash
git add -A
git commit -m "feat(member-desugar): C11 property desugar pass complete (M1-M4)"
```

---

## Self-Review

- **Spec coverage**: decisions 1-15 map to Tasks 1-4 (naming decision 1 → `AccessorsToMethods`+rewriter; 3 → BackingFieldNaming; 4/5 → PD-3 rewriter+counters; 6 → modifier transfer; 7 → initializer lifting; 8 → interface linkage in PropertyRule; 9/10 → analyzer excludes indexers/events/operators; 11 → nameof; 12 → gates 5117/5118; 13 → no support library anywhere; 14 → attribute routing; 15 → adapter). PD-1..PD-12 covered in Tasks 3-4; registry §3.3 items 1-8 in Tasks 5-8 (item 9 default-on flip intentionally NOT done — gated by §8); tests §7.1-§7.6 in Tasks 9-12; §5.3/§8 are follow-up PRs by design.
- **Placeholders**: all registry/CLI/test code blocks are complete; the two largest rewriters (Tasks 3-4) specify exact behavior tables + insertion/rewrite algorithms — their commit steps deliver full code.
- **Type consistency**: `MemberDesugarOptions/Statistics/Diagnostic/FileResult` (Task 1) used verbatim in Tasks 4/7/9/11; `PropertyPlan.Property/BackingFieldName/GetterMethodName` consistent across Tasks 3/4; adapter ctor signature matches Task 6.2/11.1.
