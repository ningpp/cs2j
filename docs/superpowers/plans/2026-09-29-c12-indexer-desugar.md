# C12 Indexer Desugar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the Indexers rule to the `member-desugar` pass family: reduce every source-declared C# indexer (`this[...]`) to `get_Item/set_Item` ordinary methods and rewrite all bound use sites (`obj[i]`, writes, compounds, `++/--`, `r?[i]`, `new C { [k] = v }`, ref-return positions), with per-plan atomic rollback — no new pass, no new adapter, no converter changes.

**Architecture:** C12 rides the C11 `MemberDesugar/` skeleton (already merged into this branch): rule discovery is assembly-scan, so `[MemberDesugarRule(MemberDesugarKind.Indexer, 20)]` on `Indexers/IndexerRule.cs` is the entire registration. Three append-only edits to C11 shared files (diagnostic enum + statistics counters; `RewriteFile` indexer dispatch + two-pass rebind; adapter summary line). All other code is new under `src/CSharpToJava.Core/MemberDesugar/Indexers/` mirroring the `Properties/` file layout.

**Tech Stack:** .NET SDK, Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.x `CSharpSyntaxRewriter`), xUnit. All builds/tests run `-c Release` inside worktree `.worktrees/preproc-c12-indexer-desugar` (branch `feature/preproc-c12-indexer-desugar`, already contains merge of C11 @ `ac9bacab`).

**Working directory for every command:** `D:\code\cs2j\.worktrees\preproc-c12-indexer-desugar`

**Key naming decisions (design §2, binding):** methods are `get_Item`/`set_Item` (C# spec accessor names via `AccessorsToMethods.GetterName/SetterName(prop.MetadataName)`); custom `[IndexerName("Foo")]` → `get_Foo/set_Foo`; diagnostics are CS2J5130-5141; temp variables use the family `__cs2j_` prefix.

---

### Task 1: Shared model appends (diagnostic codes 5130-5141 + indexer statistics)

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs`

- [ ] **Step 1.1: Append indexer codes to `MemberDesugarDiagnosticCode`**

In `MemberDesugarModels.cs`, directly after the line `    WithSite = 5124,` insert:

```csharp
    // 5130-5149 indexer rules (C12, design §6); 5150-5189 reserved for C13/C14.
    IndexerPlanCreated = 5130,        // Info，仅 verbose
    IndexerNameAdopted = 5131,        // Info：[IndexerName] 自定义名采用（特性已移除）
    IndexerInitWidened = 5132,        // Info：init 归约为普通 set
    DefaultMemberRetained = 5133,     // Info：类型级 [DefaultMember] 保留
    ExternalIndexerSite = 5134,       // Info，仅 verbose：外部索引器用点未改写
    IndexerExpressionTreeSite = 5135, // Warning，force 可解除
    IndexerDynamicSite = 5136,        // Warning
    IndexerNonLiftableSite = 5137,    // Warning：复合/自增/初始化器/写值消费不可提升
    IndexerPartialDeclaration = 5138, // Warning：C# 13 partial 索引器
    IndexerUnsupportedDeclaration = 5139, // Warning：extern / static abstract 接口索引器
    IndexerNameCollision = 5140,      // Warning，防御性
    ResidualIndexer = 5141,           // Warning（strict 升级 Error）：residual 闸门
```

- [ ] **Step 1.2: Append indexer counters to `MemberDesugarStatistics`**

In the same file, after the property `public int PlansRolledBack { get; set; }` insert:

```csharp
    // C12 indexer rule counters (design §5.3).
    public int IndexersPlanned { get; set; }
    public int IndexerReadSitesRewritten { get; set; }
    public int IndexerWriteSitesRewritten { get; set; }
    public int IndexerCompoundSitesExpanded { get; set; }
    public int IndexerInitializerSitesRewritten { get; set; }
    public int ExplicitInterfaceIndexers { get; set; }
    public int ExternalIndexerSites { get; set; }
```

- [ ] **Step 1.3: Build**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj -c Release`
Expected: `Build succeeded` (0 errors).

- [ ] **Step 1.4: Commit**

```bash
git add src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs
git commit -m "feat(member-desugar): CS2J5130-5141 indexer codes + indexer statistics counters"
```

---

### Task 2: Indexer skeleton — IndexerDiagnostics, IndexerFacts, IndexerPlan, IndexerRule (analysis + gates)

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerDiagnostics.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerFacts.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerPlan.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerRule.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs`

- [ ] **Step 2.1: Write failing tests for the declaration gates (skip diagnostics observable through `RewriteFile`)**

Create `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs`:

```csharp
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.ProjectTests;

/// <summary>C12 indexer 去糖 Core 单测矩阵（设计 §7.1）：内联源码 → RewriteFile →
/// 忽略空白断言 + AssertOutputCompiles。复用 MemberDesugarPropertyUnitTests 的
/// internal Rewrite/AssertOutputCompiles 帮助方法（同程序集可见）。</summary>
public class MemberDesugarIndexerUnitTests
{
    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    private static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    private static MemberDesugarDiagnostic? Find(
        List<MemberDesugarDiagnostic> diags, MemberDesugarDiagnosticCode code)
        => diags.FirstOrDefault(d => d.Code == code);

    private const string SimpleIndexer = """
        class C
        {
            private readonly int[] _a = new int[8];
            public int this[int i] { get => _a[i]; set => _a[i] = value; }
            public int Read(int i) => this[i];
        }
        """;

    [Fact]
    public void PartialIndexer_GatedWith5138()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            partial class C
            {
                public partial int this[int i] { get; set; }
            }
            """);
        Assert.Contains(CS2J(5138), Codes(diags));
        Assert.Contains("this[int i]", outCode);
    }

    [Fact]
    public void ExternIndexer_GatedWith5139()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public extern int this[int i] { get; }
            }
            """);
        Assert.Contains(CS2J(5139), Codes(diags));
        Assert.Contains("this[int i]", outCode);
    }

    [Fact]
    public void ExpressionTreeSite_GatedWith5135_AndForceLifts()
    {
        var src = """
            using System.Linq.Expressions;
            class C
            {
                public int this[int i] => i;
                public System.Linq.Expressions.Expression<System.Func<int, int>> F()
                    => (System.Linq.Expressions.Expression<System.Func<int, int>>)(x => this[x]);
            }
            """;
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite(src);
        Assert.Contains(CS2J(5135), Codes(diags));
        Assert.Contains("this[int i]", outCode);

        var forced = new MemberDesugarOptions
        {
            ForceQualifiedNames = new HashSet<string> { "C::Item" },
        };
        var (_, _, diags2) = MemberDesugarPropertyUnitTests.Rewrite(src, forced);
        Assert.DoesNotContain(CS2J(5135), Codes(diags2));
    }

    [Fact]
    public void SkipOverrides_ExcludesPlan()
    {
        var options = new MemberDesugarOptions
        {
            SkipQualifiedNames = new HashSet<string> { "C" },
        };
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite(SimpleIndexer, options);
        Assert.Contains("this[int i]", outCode);
        Assert.Contains(CS2J(5110), Codes(diags)); // 族级 PlanCreated Info（UserSkipped 同码位复用）
    }

    private static IEnumerable<string> Codes(List<MemberDesugarDiagnostic> diags)
        => diags.Select(d => $"CS2J{(int)d.Code}");

    private static string CS2J(int code) => $"CS2J{code}";
}
```

- [ ] **Step 2.2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: FAIL/compile error — `IndexerDiagnosticsCode`/indexer rule does not exist; partial/extern plans are not produced so the 5138/5139 assertions fail.

- [ ] **Step 2.3: Implement `IndexerDiagnostics.cs`**

```csharp
namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>CS2J5130-5141 indexer 面消息文案（码位常量在
/// <see cref="MemberDesugarDiagnosticCode"/>；只放消息模板，对齐 PropertyDiagnostics）。</summary>
internal static class IndexerDiagnostics
{
    public const string SkipSuffix =
        "the indexer declaration and all of its use sites were left unchanged";

    public static string Partial(string name)
        => $"partial indexer '{name}' is not supported before partial-merge (C20); {SkipSuffix}.";

    public static string Unsupported(string name, string why)
        => $"{why} indexer '{name}' is conservatively skipped; {SkipSuffix}.";

    public static string ExpressionTree(string name)
        => $"indexer '{name}' is accessed inside an expression-tree lambda; rewriting would " +
           $"change the tree shape; {SkipSuffix} (overrides.force lifts this).";

    public static string Dynamic(string name)
        => $"indexer '{name}' may be accessed through a dynamic receiver; binding is " +
           $"undecidable; {SkipSuffix}.";

    public static string NonLiftable(string name)
        => $"indexer '{name}' is written in a compound/increment/initializer/value-consumed " +
           $"context that cannot be lifted to statements; {SkipSuffix}.";

    public static string NameCollision(string name)
        => $"generated accessor methods for indexer '{name}' collide with an existing " +
           $"same-signature member; {SkipSuffix}.";

    public static string Residual(string name)
        => $"indexer '{name}' has neither a plan nor a skip diagnostic (residual gate).";
}
```

- [ ] **Step 2.4: Implement `IndexerFacts.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>索引器纯判定（设计 §4.1）：声明形态、简单实参白名单（D11）、
/// 外部符号、初始化器可提升性（镜像 C11 PropertyRule 的 ComputeLiftable 逻辑，
/// 按 §3.2 所有权约定在 C12 目录内自持副本，不改 Properties/）。</summary>
internal static class IndexerFacts
{
    public static bool IsPartial(IndexerDeclarationSyntax decl)
        => decl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))
           || decl.AccessorList?.Accessors.Any(a =>
                  a.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))) == true;

    public static bool HasInitAccessor(IndexerDeclarationSyntax decl)
        => decl.AccessorList?.Accessors.Any(a =>
               a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

    public static bool IsExternal(IPropertySymbol prop)
        => !prop.Locations.Any(l => l.IsInSource);

    public static bool ReturnsByRef(IPropertySymbol prop)
        => prop.ReturnsByRef || prop.ReturnsByRefReadonly;

    /// <summary>无副作用实参白名单（D11）：字面量/标识符/this/base/成员链/括号。
    /// 方法调用、自增、赋值、条件等一律视为有副作用。</summary>
    public static bool IsSimpleArgument(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax => true,
        IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax => true,
        MemberAccessExpressionSyntax ma => IsSimpleArgument(ma.Expression),
        MemberBindingExpressionSyntax => true,
        ParenthesizedExpressionSyntax p => IsSimpleArgument(p.Expression),
        _ => false,
    };

    /// <summary>防御性碰撞（D16）：生成名与既有同签名方法冲突（合法输入理论不可达）。</summary>
    public static bool HasSignatureCollision(IPropertySymbol prop)
    {
        var type = prop.ContainingType;
        if (type == null) return false;
        foreach (var m in type.GetMembers())
        {
            if (m is not IMethodSymbol meth || m.IsImplicitlyDeclared) continue;
            if (meth.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet) continue;
            var isGetterName = meth.Name == AccessorsToMethods.GetterName(prop.MetadataName);
            var isSetterName = meth.Name == AccessorsToMethods.SetterName(prop.MetadataName);
            if (!isGetterName && !isSetterName) continue;
            var getterLike = isGetterName && prop.GetMethod != null;
            var setterLike = isSetterName && prop.SetMethod != null;
            if (!getterLike && !setterLike) continue;
            if (meth.Parameters.Length != prop.Parameters.Length) continue;
            var mismatch = false;
            for (var i = 0; i < prop.Parameters.Length; i++)
                if (!SymbolEqualityComparer.Default.Equals(
                        meth.Parameters[i].Type, prop.Parameters[i].Type))
                { mismatch = true; break; }
            if (!mismatch) return true;
        }
        return false;
    }

    /// <summary>对象创建的可提升性（镜像 C11）：语句位 / 单变量局部声明 / 块内 return，
    /// 或嵌套初始化器继承外层可提升性。</summary>
    public static bool IsLiftableCreation(ObjectCreationExpressionSyntax oc)
        => ComputeLiftable(oc, new HashSet<SyntaxNode>());

    private static bool ComputeLiftable(ObjectCreationExpressionSyntax oc, HashSet<SyntaxNode> memo)
    {
        if (!memo.Add(oc)) return false;
        bool result;
        if (IsLiftablePosition(oc))
        {
            result = true;
        }
        else if (oc.Parent is EqualsValueClauseSyntax eq
                 && eq.Parent is AssignmentExpressionSyntax a
                 && a.Parent is InitializerExpressionSyntax init
                 && init.Parent is ObjectCreationExpressionSyntax outer)
        {
            result = ComputeLiftable(outer, memo);
        }
        else if (oc.Parent is AssignmentExpressionSyntax va
                 && va.Parent is InitializerExpressionSyntax vinit
                 && vinit.Parent is ObjectCreationExpressionSyntax vouter)
        {
            result = ComputeLiftable(vouter, memo);
        }
        else if (oc.Parent is InitializerExpressionSyntax complex
                 && complex.IsKind(SyntaxKind.ComplexElementInitializerExpression)
                 && complex.Parent is EqualsValueClauseSyntax eq2
                 && eq2.Parent is AssignmentExpressionSyntax a2
                 && a2.Parent is InitializerExpressionSyntax init2
                 && init2.Parent is ObjectCreationExpressionSyntax outer2)
        {
            result = ComputeLiftable(outer2, memo);
        }
        else
        {
            result = false;
        }
        return result;
    }

    private static bool IsLiftablePosition(ObjectCreationExpressionSyntax oc)
    {
        switch (oc.Parent)
        {
            case ExpressionStatementSyntax:
                return true;
            case EqualsValueClauseSyntax eq when eq.Parent is VariableDeclaratorSyntax d
                && d.Parent is VariableDeclarationSyntax vd
                && vd.Variables.Count == 1
                && vd.Parent is LocalDeclarationStatementSyntax l
                && l.UsingKeyword.IsKind(SyntaxKind.None)
                && l.AwaitKeyword.IsKind(SyntaxKind.None)
                && !l.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)
                    || m.IsKind(SyntaxKind.StaticKeyword)):
                return true;
            case ReturnStatementSyntax r when r.Parent is BlockSyntax or SwitchSectionSyntax:
                return true;
            default:
                return false;
        }
    }
}
```

- [ ] **Step 2.5: Implement `IndexerPlan.cs`**

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

internal sealed class IndexerPlan : MemberDesugarPlan
{
    public required IPropertySymbol Indexer { get; init; }
    /// <summary>索引器元数据名（"Item" 或 [IndexerName] 自定义名；显式接口实现的
    /// Name 是 "IFoo.Item" 形态，方法名必须用 MetadataName，D3）。</summary>
    public required string MetadataName { get; init; }
    public required string GetterMethodName { get; init; }
    public required string SetterMethodName { get; init; }
    /// <summary>set/init 访问器是否存在（init 亦算，放宽为 set，D8）。</summary>
    public required bool HasSetter { get; init; }
    public required bool HasInitAccessor { get; init; }

    public bool ReturnsByRef => Indexer.ReturnsByRef || Indexer.ReturnsByRefReadonly;
    public bool IsExplicitImplementation => Indexer.ExplicitInterfaceImplementations.Length > 0;

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Indexer.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return memberName != null
               && (string.Equals(memberName, GetterMethodName, StringComparison.Ordinal)
                   || string.Equals(memberName, SetterMethodName, StringComparison.Ordinal)
                   || string.Equals(memberName, MetadataName, StringComparison.Ordinal));
    }

    public static string BuildId(IPropertySymbol prop)
    {
        var span = prop.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD:{prop.ContainingType?.ToDisplayString()}::{prop.MetadataName}@{span}";
    }
}
```

- [ ] **Step 2.6: Implement `IndexerRule.cs` (declaration collection + gates + site classification + plan production)**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>
/// C12 indexer 规则（Kind=Indexer, Order=20；经程序集扫描自动注册，零注册代码）。
/// 三步镜像 PropertyRule：1) 声明收集 + 声明级闸门（partial/extern/static abstract/碰撞）；
/// 2) 站点扫描（读/写/复合/自增/null 条件/初始化器/ref|out/表达式树/dynamic）；
/// 3) IndexerPlan 产出（D17：一个索引器符号一个 plan）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Indexer, 20)]
internal sealed class IndexerRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Indexer;
    public int Order => 20;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var options = context.Options;

        var candidates = new Dictionary<IPropertySymbol,
            (IndexerDeclarationSyntax Decl, SemanticModel Model)>(SymbolEqualityComparer.Default);
        var failures = new Dictionary<IPropertySymbol,
            (IndexerDeclarationSyntax? Decl, MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        var userSkipped = new Dictionary<IPropertySymbol, IndexerDeclarationSyntax>(
            SymbolEqualityComparer.Default);
        var allSourceIndexers = new List<IPropertySymbol>();

        // ---- 阶段 1：声明收集 + 声明级闸门 ----
        foreach (var model in context.Models)
        {
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<IndexerDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { IsIndexer: true } prop) continue;
                if (!allSourceIndexers.Contains(prop, SymbolEqualityComparer.Default))
                    allSourceIndexers.Add(prop);
                if (candidates.ContainsKey(prop) || failures.ContainsKey(prop)
                    || userSkipped.ContainsKey(prop)) continue;

                if (MatchesSet(options.SkipQualifiedNames, prop))
                {
                    userSkipped[prop] = decl;
                    continue;
                }

                var gate = DeclarationGate(decl, prop);
                if (gate != null)
                {
                    failures[prop] = (decl, gate.Value.Code, gate.Value.Reason);
                    continue;
                }
                candidates[prop] = (decl, model);
            }
        }

        // ---- 阶段 2：站点扫描 ----
        var siteFiles = new Dictionary<IPropertySymbol, HashSet<string>>(
            SymbolEqualityComparer.Default);
        var siteFailures = new Dictionary<IPropertySymbol,
            (MemberDesugarDiagnosticCode Code, string Reason)>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var filePath = model.SyntaxTree.FilePath;
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

            // dynamic 元素访问保守闸门（D15）：任何 dynamic 接收者的 [] 都可能命中
            // 任意索引器 → 全部候选按 CS2J5136 跳过（对齐 C11 属性 dynamic 处理）。
            foreach (var ea in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (model.GetTypeInfo(ea.Expression).Type is not { TypeKind: TypeKind.Dynamic })
                    continue;
                foreach (var key in candidates.Keys.ToList())
                    MarkFailure(siteFailures, key,
                        MemberDesugarDiagnosticCode.IndexerDynamicSite,
                        IndexerDiagnostics.Dynamic(key.MetadataName));
            }

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case ImplicitElementAccessSyntax iea when iea.Parent is InitializerExpressionSyntax:
                        HandleInitializerElement(iea, model, filePath,
                            candidates, siteFiles, siteFailures);
                        break;
                    case ElementAccessExpressionSyntax ea:
                        HandleElementAccess(ea, model, filePath,
                            candidates, siteFiles, siteFailures, options);
                        break;
                    case ElementBindingExpressionSyntax eb:
                        HandleElementAccess(eb, model, filePath,
                            candidates, siteFiles, siteFailures, options);
                        break;
                }
            }
        }

        // ---- 阶段 3：plan 产出 ----
        foreach (var (prop, declModel) in candidates)
        {
            var (decl, _) = declModel;
            var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in prop.Locations.Where(l => l.IsInSource))
                touched.Add(loc.SourceTree!.FilePath);
            if (siteFiles.TryGetValue(prop, out var files))
                foreach (var f in files) touched.Add(f);
            CollectHierarchyLinks(prop, allSourceIndexers, touched);

            if (siteFailures.TryGetValue(prop, out var fail))
            {
                builder.Add(CreatePlan(prop, decl, MemberDesugarOutcome.Infeasible,
                    DeclarationFiles(prop), fail.Code, fail.Reason));
                continue;
            }
            builder.Add(CreatePlan(prop, decl, MemberDesugarOutcome.Planned,
                touched.ToList(), null, null));
        }

        foreach (var (prop, fail) in failures)
            builder.Add(CreatePlan(prop, fail.Decl, MemberDesugarOutcome.Infeasible,
                DeclarationFiles(prop), fail.Code, fail.Reason));

        foreach (var (prop, decl) in userSkipped)
            builder.Add(CreatePlan(prop, decl, MemberDesugarOutcome.UserSkipped,
                DeclarationFiles(prop), null, null));
    }

    // ---- 声明级闸门（5138 → 5139 → 5140）----

    private static (MemberDesugarDiagnosticCode Code, string Reason)? DeclarationGate(
        IndexerDeclarationSyntax decl, IPropertySymbol prop)
    {
        if (IndexerFacts.IsPartial(decl))
            return (MemberDesugarDiagnosticCode.IndexerPartialDeclaration,
                IndexerDiagnostics.Partial(prop.MetadataName));
        if (prop.IsExtern)
            return (MemberDesugarDiagnosticCode.IndexerUnsupportedDeclaration,
                IndexerDiagnostics.Unsupported(prop.MetadataName, "extern"));
        if (prop.IsStatic && prop.IsAbstract)
            return (MemberDesugarDiagnosticCode.IndexerUnsupportedDeclaration,
                IndexerDiagnostics.Unsupported(prop.MetadataName, "static abstract interface"));
        if (IndexerFacts.HasSignatureCollision(prop))
            return (MemberDesugarDiagnosticCode.IndexerNameCollision,
                IndexerDiagnostics.NameCollision(prop.MetadataName));
        return null;
    }

    // ---- 站点处理 ----

    private static void HandleElementAccess(
        ExpressionSyntax node, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (IndexerDeclarationSyntax Decl, SemanticModel Model)> candidates,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options)
    {
        if (model.GetSymbolInfo(node).Symbol is not IPropertySymbol { IsIndexer: true } p) return;
        if (!p.IsDefinition) p = p.OriginalDefinition;
        if (!candidates.ContainsKey(p)) return;
        Record(siteFiles, p, filePath);

        // 表达式树（CS2J5135，force 可解除）
        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(node, model)
            && !MatchesSet(options.ForceQualifiedNames, p))
        {
            MarkFailure(siteFailures, p,
                MemberDesugarDiagnosticCode.IndexerExpressionTreeSite,
                IndexerDiagnostics.ExpressionTree(p.MetadataName));
            return;
        }

        // 左值分类
        if (node.Parent is AssignmentExpressionSyntax assign && ReferenceEquals(assign.Left, node))
        {
            if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                // 消费位写（x = (c[i] = v)）：值索引器不可提升 → 5137；ref 索引器走 getter 变量。
                if (assign.Parent is not ExpressionStatementSyntax
                    && assign.Parent is not InitializerExpressionSyntax
                    && !IndexerFacts.ReturnsByRef(p))
                    MarkFailure(siteFailures, p,
                        MemberDesugarDiagnosticCode.IndexerNonLiftableSite,
                        IndexerDiagnostics.NonLiftable(p.MetadataName));
            }
            else
            {
                // 复合：仅语句位处理（含实参提升）；表达式位 → 5137。
                if (assign.Parent is not ExpressionStatementSyntax
                    && !IndexerFacts.ReturnsByRef(p))
                    MarkFailure(siteFailures, p,
                        MemberDesugarDiagnosticCode.IndexerNonLiftableSite,
                        IndexerDiagnostics.NonLiftable(p.MetadataName));
            }
            return;
        }

        // ++/--：仅语句位处理（结果未被消费）；消费位值索引器 → 5137。
        var isIncrementTarget =
            node.Parent is PrefixUnaryExpressionSyntax pre
                && (pre.IsKind(SyntaxKind.PreIncrementExpression)
                    || pre.IsKind(SyntaxKind.PreDecrementExpression))
            || node.Parent is PostfixUnaryExpressionSyntax post
                && (post.IsKind(SyntaxKind.PostIncrementExpression)
                    || post.IsKind(SyntaxKind.PostDecrementExpression));
        if (isIncrementTarget && node.Parent!.Parent is not ExpressionStatementSyntax
            && !IndexerFacts.ReturnsByRef(p))
        {
            MarkFailure(siteFailures, p,
                MemberDesugarDiagnosticCode.IndexerNonLiftableSite,
                IndexerDiagnostics.NonLiftable(p.MetadataName));
        }
        // 其余（读 / ref|out 实参 / ref var）无需失败标记。
    }

    private static void HandleInitializerElement(
        ImplicitElementAccessSyntax iea, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (IndexerDeclarationSyntax Decl, SemanticModel Model)> candidates,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures)
    {
        if (model.GetSymbolInfo(iea).Symbol is not IPropertySymbol { IsIndexer: true } p) return;
        if (!p.IsDefinition) p = p.OriginalDefinition;
        if (!candidates.ContainsKey(p)) return;
        Record(siteFiles, p, filePath);

        var enclosing = iea.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        if (enclosing == null || !IndexerFacts.IsLiftableCreation(enclosing))
            MarkFailure(siteFailures, p,
                MemberDesugarDiagnosticCode.IndexerNonLiftableSite,
                IndexerDiagnostics.NonLiftable(p.MetadataName));
    }

    // ---- 层次联动（override 链 / 接口实现，镜像 C11 CollectHierarchyLinks）----

    private static void CollectHierarchyLinks(
        IPropertySymbol prop, IReadOnlyList<IPropertySymbol> all, SortedSet<string> files)
    {
        foreach (var other in all)
        {
            if (SymbolEqualityComparer.Default.Equals(other, prop)) continue;
            if (InOverrideChain(other, prop) || InOverrideChain(prop, other)
                || other.ExplicitInterfaceImplementations.Contains(prop, SymbolEqualityComparer.Default)
                || ImplementsImplicit(other, prop))
            {
                foreach (var loc in other.Locations.Where(l => l.IsInSource))
                    files.Add(loc.SourceTree!.FilePath);
            }
        }
    }

    private static bool InOverrideChain(IPropertySymbol derived, IPropertySymbol potentialBase)
    {
        for (var b = derived.OverriddenProperty; b != null; b = b.OverriddenProperty)
            if (SymbolEqualityComparer.Default.Equals(b, potentialBase)) return true;
        return false;
    }

    private static bool ImplementsImplicit(IPropertySymbol impl, IPropertySymbol ifaceProp)
        => ifaceProp.ContainingType is { TypeKind: TypeKind.Interface }
           && impl.ContainingType != null
           && SymbolEqualityComparer.Default.Equals(
               impl.ContainingType.FindImplementationForInterfaceMember(ifaceProp), impl);

    // ---- 小工具 ----

    private static IndexerPlan CreatePlan(
        IPropertySymbol prop, IndexerDeclarationSyntax? decl, MemberDesugarOutcome outcome,
        IReadOnlyList<string> touchedFiles, MemberDesugarDiagnosticCode? code, string? reason)
        => new()
        {
            Id = IndexerPlan.BuildId(prop),
            Kind = MemberDesugarKind.Indexer,
            Symbol = prop,
            Outcome = outcome,
            TouchedFiles = touchedFiles,
            FailCode = code,
            FailReason = reason,
            Indexer = prop,
            MetadataName = prop.MetadataName,
            GetterMethodName = AccessorsToMethods.GetterName(prop.MetadataName),
            SetterMethodName = AccessorsToMethods.SetterName(prop.MetadataName),
            HasSetter = prop.SetMethod != null,
            HasInitAccessor = decl != null && IndexerFacts.HasInitAccessor(decl),
        };

    private static void Record(
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles, IPropertySymbol prop, string filePath)
    {
        if (!siteFiles.TryGetValue(prop, out var set))
            siteFiles[prop] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(filePath);
    }

    private static void MarkFailure(
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        IPropertySymbol prop, MemberDesugarDiagnosticCode code, string reason)
        => failures.TryAdd(prop, (code, reason));

    private static bool MatchesSet(IReadOnlySet<string> names, IPropertySymbol prop)
    {
        var type = prop.ContainingType?.ToDisplayString();
        return names.Contains($"{type}::{prop.MetadataName}")
               || (type != null && names.Contains(type));
    }

    private static IReadOnlyList<string> DeclarationFiles(IPropertySymbol prop)
        => prop.Locations.Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
```

- [ ] **Step 2.7: Run the Task-2 tests**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: 4 PASS. (Skip diagnostics flow through the family-generic `EmitSkipDiagnostics`; no dispatch needed yet.)

- [ ] **Step 2.8: Run the C11 property suite to prove no regression**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: all PASS (gates never fire for properties).

- [ ] **Step 2.9: Commit**

```bash
git add src/CSharpToJava.Core/MemberDesugar/Indexers/ tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs
git commit -m "feat(member-desugar): indexer rule analysis — declaration gates, site classification, IndexerPlan"
```

---

### Task 3: Declaration reduction — `IndexerDeclarationExpander` + `IndexerUseSiteRewriter` declaration path + `RewriteFile` dispatch

**Files:**
- Create: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerDeclarationRewriter.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs`
- Modify: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs:46-78`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs` (append)

- [ ] **Step 3.1: Write failing declaration-reduction tests (append to `MemberDesugarIndexerUnitTests`)**

```csharp
    [Fact]
    public void GetSetIndexer_ReducedToBothMethods()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public int Read(int i) => this[i];
            }
            """);
        AssertContains(outCode, "public int get_Item(int i) => _a[i];");
        AssertContains(outCode, "public void set_Item(int i, int value) => _a[i] = value;");
        AssertNotContains(outCode, "this[int i]");
        Assert.Equal(1, stats.IndexersPlanned);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void MultiParam_ExpressionBodied_AutoAccessorBodies()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class M
            {
                private readonly int[,] _g = new int[4, 4];
                public int this[int r, int c]
                {
                    get { return _g[r, c]; }
                    set { _g[r, c] = value; }
                }
                public int Corner => this[0, 0];
            }
            """);
        AssertContains(outCode, "public int get_Item(int r, int c)");
        AssertContains(outCode, "public void set_Item(int r, int c, int value)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void AbstractVirtualOverrideChain_ModifiersCarry()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            abstract class A
            {
                public abstract int this[int i] { get; set; }
            }
            class B : A
            {
                public virtual int this[int i] { get => i; set { } }
            }
            sealed class C2 : B
            {
                public override sealed int this[int i] { get => base[i]; set { } }
            }
            """);
        AssertContains(outCode, "public abstract int get_Item(int i);");
        AssertContains(outCode, "public virtual int get_Item(int i)");
        AssertContains(outCode, "public override sealed int get_Item(int i)");
        AssertContains(outCode, "base.get_Item(i)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void InterfaceGetOnly_NoSetterGenerated_DimKeptBody()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            interface IFoo
            {
                int this[int i] { get; }
            }
            interface IDim
            {
                int this[string k] => k.Length;
            }
            """);
        AssertContains(outCode, "int get_Item(int i);");
        AssertNotContains(outCode, "set_Item");
        AssertContains(outCode, "int get_Item(string k) => k.Length;");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void AccessorAccessibility_PrivateSet()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int i] { get => i; private set { } }
                public void M(int i) { this[i] = i; }
            }
            """);
        AssertContains(outCode, "public int get_Item(int i) => i;");
        AssertContains(outCode, "private void set_Item(int i, int value)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void InitAccessor_WidenedWith5132()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int i] { get => i; init { } }
            }
            """);
        AssertContains(outCode, "public void set_Item(int i, int value)");
        Assert.Contains(CS2J(5132), Codes(diags));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void ReadonlyStructIndexer_ModifierDropped()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            struct S
            {
                private int _x;
                public readonly int this[int i] => _x;
            }
            """);
        AssertContains(outCode, "public int get_Item(int i) => _x;");
        AssertNotContains(outCode, "readonly");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void OverloadPair_And_DefaultParameterValue()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int i] => i;
                public string this[string s] => s;
                public int this[int i, int j = 5] => i + j;
                public int Sum() => this[1] + this[2, 3];
            }
            """);
        AssertContains(outCode, "public int get_Item(int i) => i;");
        AssertContains(outCode, "public string get_Item(string s) => s;");
        AssertContains(outCode, "public int get_Item(int i, int j = 5) => i + j;");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }
```

Note: `Rewrite` returns `(string OutputCode, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diagnostics)`; use `var (outCode, stats, _) = ...` or `var (outCode, _) = ...` (deconstruction drops trailing items only in declaration position with exactly matching arity — always bind all three and discard: `var (outCode, _, _) = ...`).

- [ ] **Step 3.2: Run to verify failure**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: new tests FAIL (`get_Item` absent — declaration not rewritten).

- [ ] **Step 3.3: Implement `IndexerDeclarationRewriter.cs` (`IndexerDeclarationExpander`)**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>IX-1..IX-5：把一个索引器声明展开为 1~2 个 MethodDeclarationSyntax。
/// 修饰符规则镜像 AccessorsToMethods；ref 签名保留（D9）；init 放宽（D8，CS2J5132）；
/// [IndexerName] 移除（D3，CS2J5131）。使用点改写在 IndexerUseSiteRewriter。</summary>
internal sealed class IndexerDeclarationExpander
{
    private readonly SemanticModel _model;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public IndexerDeclarationExpander(
        SemanticModel model, MemberDesugarOptions options, MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics, string filePath)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    /// <summary><paramref name="bindingDecl"/> 是语义绑定（特性路由）用的原始树内声明；
    /// <paramref name="decl"/> 的访问器体已过使用点改写。</summary>
    public IReadOnlyList<MemberDeclarationSyntax> Expand(
        IndexerDeclarationSyntax decl, IndexerPlan plan, IndexerDeclarationSyntax? bindingDecl = null)
    {
        bindingDecl ??= decl;
        var prop = plan.Indexer;
        var inInterface = prop.ContainingType is { TypeKind: TypeKind.Interface };
        var isExplicit = decl.ExplicitInterfaceSpecifier != null;

        var accessors = decl.AccessorList?.Accessors;
        var getAccessor = accessors?.FirstOrDefault(a =>
            a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setAccessor = accessors?.FirstOrDefault(a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration)
            || a.IsKind(SyntaxKind.InitAccessorDeclaration));
        var expressionBody = decl.AccessorList == null ? decl.ArrowExpressionClause : null;

        if (prop.MetadataName != "Item")
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.IndexerNameAdopted,
                $"indexer name '{prop.MetadataName}' adopted from [IndexerName]; the attribute " +
                "was removed from the declaration",
                _filePath, LineOf(bindingDecl), ColOf(bindingDecl), prop.MetadataName));
        if (plan.HasInitAccessor)
        {
            _stats.InitAccessorsWidened++;
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.IndexerInitWidened,
                "init-only indexer accessor widened to an ordinary set_Item method",
                _filePath, LineOf(bindingDecl), ColOf(bindingDecl), prop.MetadataName));
        }
        if (isExplicit) _stats.ExplicitInterfaceIndexers++;
        EmitDefaultMemberNote(prop, bindingDecl);

        var results = new List<MemberDeclarationSyntax>();

        if (prop.GetMethod != null)
        {
            TypeSyntax returnType = decl.Type;
            if (prop.ReturnsByRef)
                returnType = SyntaxFactory.RefType(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword), decl.Type);
            else if (prop.ReturnsByRefReadonly)
                returnType = SyntaxFactory.RefType(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword),
                    SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword), decl.Type);

            var method = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.List(RouteDeclarationAttributes(bindingDecl, forGetter: true)
                    .Concat(getAccessor?.AttributeLists ?? default).ToList()),
                BuildIndexerModifiers(decl, getAccessor, inInterface, isExplicit),
                returnType,
                isExplicit
                    ? SyntaxFactory.ExplicitInterfaceSpecifier(decl.ExplicitInterfaceSpecifier!.Name)
                    : null,
                SyntaxFactory.Identifier(plan.GetterMethodName),
                null,
                decl.ParameterList!,
                SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                getAccessor?.Body,
                getAccessor?.ExpressionBody ?? expressionBody,
                getAccessor?.Body == null && getAccessor?.ExpressionBody == null && expressionBody == null
                    ? SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                    : default);
            results.Add(method);
        }

        if (prop.SetMethod != null)
        {
            var valueParameter = SyntaxFactory.Parameter(
                default, default, decl.Type.WithoutTrivia(),
                SyntaxFactory.Token(SyntaxKind.Identifier, "value"), null);
            var parameters = decl.ParameterList!.Parameters.Add(valueParameter);
            var method = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.List(RouteDeclarationAttributes(bindingDecl, forGetter: false)
                    .Concat(setAccessor?.AttributeLists ?? default).ToList()),
                BuildIndexerModifiers(decl, setAccessor, inInterface, isExplicit),
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                isExplicit
                    ? SyntaxFactory.ExplicitInterfaceSpecifier(decl.ExplicitInterfaceSpecifier!.Name)
                    : null,
                SyntaxFactory.Identifier(plan.SetterMethodName),
                null,
                SyntaxFactory.ParameterList(parameters),
                SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                setAccessor?.Body,
                setAccessor?.ExpressionBody,
                setAccessor?.Body == null && setAccessor?.ExpressionBody == null
                    ? SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                    : default);
            results.Add(method);
        }

        // trivia：首成员接声明 leading，末成员接 trailing。
        if (results.Count > 0)
        {
            var annotated = results
                .Select(m => m.WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation))
                .ToList();
            annotated[0] = annotated[0].WithLeadingTrivia(decl.GetLeadingTrivia());
            annotated[^1] = annotated[^1].WithTrailingTrivia(decl.GetTrailingTrivia());
            return annotated;
        }
        return results;
    }

    private void EmitDefaultMemberNote(IPropertySymbol prop, IndexerDeclarationSyntax decl)
    {
        if (prop.ContainingType?.GetAttributes().Any(a =>
                a.AttributeClass is { Name: "DefaultMemberAttribute" }
                && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "System.Reflection")
            != true) return;
        _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.DefaultMemberRetained,
            "type-level [DefaultMember] was retained; reflection lookups such as " +
            "GetProperty(\"Item\") are no longer guaranteed to find the desugared indexer",
            _filePath, LineOf(decl), ColOf(decl), prop.MetadataName));
    }

    /// <summary>声明级特性：IndexerName 移除；return: 目标 → getter；其余按
    /// AttributeRouting 路由（Unroutable 落 getter，best-effort，受整树回滚保护）。</summary>
    private IEnumerable<AttributeListSyntax> RouteDeclarationAttributes(
        IndexerDeclarationSyntax bindingDecl, bool forGetter)
    {
        foreach (var list in bindingDecl.AttributeLists)
        {
            var kept = list.Attributes.Where(a => !IsIndexerName(a)).ToList();
            if (kept.Count == 0) continue;
            var filtered = list.WithAttributes(SyntaxFactory.SeparatedList(kept));
            if (list.Target?.Identifier.ValueText == "return")
            {
                if (forGetter) yield return filtered;
                continue;
            }
            var dest = AttributeRouting.RouteList(list, _model, fieldGenerated: false);
            if (dest == AttributeRouting.Destination.Getter && !forGetter) continue;
            yield return filtered;
        }
    }

    private bool IsIndexerName(AttributeSyntax attr)
        => attr.Name.ToString() is "IndexerName" or "IndexerNameAttribute"
           || (_model.GetSymbolInfo(attr).Symbol as IMethodSymbol)?.ContainingType?.Name
               == "IndexerNameAttribute";

    /// <summary>索引器版修饰符规则（镜像 AccessorsToMethods.BuildMethodModifiers；
    /// readonly 一律丢弃（D7），required/partial 不存在于索引器面）。</summary>
    private static SyntaxTokenList BuildIndexerModifiers(
        IndexerDeclarationSyntax decl, AccessorDeclarationSyntax? accessor,
        bool inInterface, bool isExplicit)
    {
        if (isExplicit) return SyntaxFactory.TokenList();
        if (inInterface)
            return SyntaxFactory.TokenList();

        var mods = new List<SyntaxToken>();
        var accessorAccessibility =
            accessor?.Modifiers.FirstOrDefault(AccessorsToMethods.IsAccessibility) ?? default;
        var chosen = accessorAccessibility != default
            ? accessorAccessibility
            : decl.Modifiers.FirstOrDefault(AccessorsToMethods.IsAccessibility);
        if (chosen != default) mods.Add(chosen);

        foreach (var m in decl.Modifiers)
        {
            if (AccessorsToMethods.IsAccessibility(m)) continue;
            if (m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.PartialKeyword))
                continue;
            mods.Add(m);
        }
        return SyntaxFactory.TokenList(mods);
    }

    private static int LineOf(IndexerDeclarationSyntax decl)
        => decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int ColOf(IndexerDeclarationSyntax decl)
        => decl.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
}
```

Note: `SyntaxFactory.Parameter` — use the overload `Parameter(SyntaxList<AttributeListSyntax> attributeLists, SyntaxTokenList modifiers, TypeSyntax type, SyntaxToken identifier, EqualsValueClauseSyntax? @default)`; pass `default`/`default(SyntaxToken)`/`null` accordingly. If the 5-arg overload is awkward, use `SyntaxFactory.Parameter(decl.Type.WithoutTrivia(), SyntaxFactory.Identifier("value"))` (2-arg overload).

- [ ] **Step 3.4: Implement the `IndexerUseSiteRewriter` skeleton (declaration path + splice infra only)**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>IX-6..IX-12 使用点改写 + 声明展开插入（逐文件单遍，绑定一律取原始
/// SemanticModel，镜像 C11 PropertyUseSiteRewriter）。语句拼接基建与 C11 同构。</summary>
internal sealed class IndexerUseSiteRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<IPropertySymbol, IndexerPlan> _active;
    private readonly HashSet<IPropertySymbol> _knownSymbols;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;
    private readonly HashSet<string> _externalReported = new(StringComparer.Ordinal);
    private int _tempCounter;

    public IndexerUseSiteRewriter(
        SemanticModel model,
        IReadOnlyList<IndexerPlan> plannedPlans,
        IReadOnlyList<IndexerPlan> allPlans,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _active = new Dictionary<IPropertySymbol, IndexerPlan>(SymbolEqualityComparer.Default);
        foreach (var p in plannedPlans) _active[p.Indexer] = p;
        _knownSymbols = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var p in allPlans) _knownSymbols.Add(p.Indexer);
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    // ---- 声明展开 + residual 闸门（CS2J5141）----

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitTypeLike(node);

    private SyntaxNode? VisitTypeLike(TypeDeclarationSyntax node)
    {
        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            if (member is IndexerDeclarationSyntax idxDecl
                && _model.GetDeclaredSymbol(idxDecl) is { IsIndexer: true } idx)
            {
                if (_active.TryGetValue(idx, out var plan))
                {
                    // 先改写访问器体内的使用点（this[i] 递归），再展开；语义绑定走原始声明。
                    var visitedDecl = (IndexerDeclarationSyntax)Visit(idxDecl)!;
                    var expander = new IndexerDeclarationExpander(
                        _model, _options, _stats, _diagnostics, _filePath);
                    foreach (var expanded in expander.Expand(visitedDecl, plan, idxDecl))
                        newMembers.Add(expanded);
                    continue;
                }
                if (!_knownSymbols.Contains(idx))
                {
                    _diagnostics.Add(new MemberDesugarDiagnostic(
                        _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                        MemberDesugarDiagnosticCode.ResidualIndexer,
                        IndexerDiagnostics.Residual(idx.MetadataName),
                        _filePath,
                        idxDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        idxDecl.GetLocation().GetLineSpan().StartLinePosition.Character + 1,
                        idx.MetadataName));
                    _stats.PlansSkipped++;
                    _stats.SkippedByReason.TryGetValue(
                        MemberDesugarDiagnosticCode.ResidualIndexer, out var n);
                    _stats.SkippedByReason[MemberDesugarDiagnosticCode.ResidualIndexer] = n + 1;
                }
            }

            if (Visit(member) is MemberDeclarationSyntax visitedMember)
                newMembers.Add(visitedMember);
        }
        return node.WithMembers(SyntaxFactory.List(newMembers));
    }

    // ---- 语句拼接（镜像 C11）：提升块拆入外层作用域 ----

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var members = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            if (member is GlobalStatementSyntax global)
            {
                if (Visit(global.Statement) is not StatementSyntax visited) continue;
                if (visited is BlockSyntax spliced
                    && spliced.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind))
                {
                    var parts = Splice(spliced).ToList();
                    parts[0] = parts[0].WithLeadingTrivia(
                        global.GetLeadingTrivia().Concat(parts[0].GetLeadingTrivia()));
                    for (var i = 0; i < parts.Count; i++)
                        members.Add(SyntaxFactory.GlobalStatement(parts[i]));
                    continue;
                }
                members.Add(global.WithStatement(visited));
                continue;
            }
            if (Visit(member) is MemberDeclarationSyntax visitedMember)
                members.Add(visitedMember);
        }
        return node.WithMembers(SyntaxFactory.List(members));
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    private IEnumerable<StatementSyntax> SpliceStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var stmt in statements)
        {
            if (Visit(stmt) is not StatementSyntax visited) continue;
            if (visited is BlockSyntax spliced
                && spliced.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind))
            {
                foreach (var s in Splice(spliced)) yield return s;
                continue;
            }
            yield return visited;
        }
    }

    private static IEnumerable<StatementSyntax> Splice(BlockSyntax block)
    {
        var statements = block.Statements;
        if (statements.Count == 0) yield break;
        var list = statements.ToList();
        list[0] = list[0].WithLeadingTrivia(
            block.GetLeadingTrivia().Concat(list[0].GetLeadingTrivia().Where(t => t.IsDirective)));
        list[^1] = list[^1].WithTrailingTrivia(block.GetTrailingTrivia());
        foreach (var s in list) yield return s;
    }

    // ---- 使用点（后续 Task 填充）----

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        => base.VisitElementAccessExpression(node);

    // ---- 符号/构造小工具 ----

    private bool TryGetPlan(ExpressionSyntax node, out IndexerPlan plan)
    {
        plan = null!;
        if (node is not (ElementAccessExpressionSyntax or ElementBindingExpressionSyntax
            or ImplicitElementAccessSyntax)) return false;
        if (_model.GetSymbolInfo(node).Symbol is not IPropertySymbol { IsIndexer: true } p) return false;
        if (!p.IsDefinition) p = p.OriginalDefinition;
        return _active.TryGetValue(p, out plan);
    }

    private string TempName(string hint) => MemberDesugarSyntaxFacts.TempName(ref _tempCounter, hint);

    private static IdentifierNameSyntax Ident(string name) => SyntaxFactory.IdentifierName(name);

    private static InvocationExpressionSyntax Invoke(ExpressionSyntax e, ArgumentListSyntax args)
        => SyntaxFactory.InvocationExpression(e, args)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

    private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax recv, string name)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, recv,
            SyntaxFactory.Token(SyntaxKind.DotToken), SyntaxFactory.IdentifierName(name));

    private static BlockSyntax Block(IEnumerable<StatementSyntax> stmts) => SyntaxFactory.Block(stmts);

    private static ExpressionStatementSyntax ExprStmt(ExpressionSyntax e)
        => SyntaxFactory.ExpressionStatement(e);

    internal static LocalDeclarationStatementSyntax DeclVar(string name, ExpressionSyntax value)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                // "var" 与标识符之间必须显式留白：无 trivia 的相邻标识符会拼接成单个 token。
                SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(value))
                })));
}
```

- [ ] **Step 3.5: Wire the indexer dispatch into `MemberDesugarPreprocessor.RewriteFile`**

In `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs` replace the block

```csharp
        var planned = active.OfType<Properties.PropertyPlan>()
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        if (planned.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }
        statistics.PropertiesPlanned += planned.Count(p => IsFirstDeclarationFile(p, filePath));

        var rewriter = new Properties.PropertyUseSiteRewriter(
            semanticModel, planned, active.OfType<Properties.PropertyPlan>().ToList(),
            _options, statistics, diagnostics, filePath);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
```

with

```csharp
        var plannedProps = active.OfType<Properties.PropertyPlan>()
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedIndexers = active.OfType<Indexers.IndexerPlan>()
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        if (plannedProps.Count == 0 && plannedIndexers.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }
        statistics.PropertiesPlanned += plannedProps.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));

        // 属性 pass 先行；两族都改写同一文件时，索引器 pass 必须基于重解析树重新绑定
        //（SemanticModel 只能绑定自身语法树的节点，设计 §4.2-3）。
        var current = (SyntaxNode)root;
        var currentModel = semanticModel;
        if (plannedProps.Count > 0)
        {
            var rewriter = new Properties.PropertyUseSiteRewriter(
                semanticModel, plannedProps, active.OfType<Properties.PropertyPlan>().ToList(),
                _options, statistics, diagnostics, filePath);
            var visited = (CompilationUnitSyntax)rewriter.Visit(root)!;
            if (!visited.IsEquivalentTo(root))
            {
                current = visited;
                currentModel = RebindModel(semanticModel, visited);
            }
        }
        if (plannedIndexers.Count > 0)
        {
            var indexerRewriter = new Indexers.IndexerUseSiteRewriter(
                currentModel, plannedIndexers, active.OfType<Indexers.IndexerPlan>().ToList(),
                _options, statistics, diagnostics, filePath);
            current = indexerRewriter.Visit(current)!;
        }
        var newRoot = (CompilationUnitSyntax)current;
```

and append this private static method to the class (after `FormatAndValidate`):

```csharp
    /// <summary>属性 pass 产出新树后，索引器 pass 需要绑定新节点的 SemanticModel：
    /// 重解析产出文本并替换编译内语法树（ReplaceSyntaxTree 保持其余树不变）。</summary>
    private static SemanticModel RebindModel(
        SemanticModel original, CompilationUnitSyntax rewritten)
    {
        var oldTree = original.SyntaxTree;
        var newTree = CSharpSyntaxTree.ParseText(
            rewritten.ToFullString(),
            (CSharpParseOptions)oldTree.Options,
            oldTree.FilePath);
        var newCompilation = original.Compilation.ReplaceSyntaxTree(oldTree, newTree);
        return newCompilation.GetSemanticModel(newTree);
    }
```

- [ ] **Step 3.6: Run tests**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: declaration tests PASS (read-site `this[i]`/`base[i]` sites are not yet rewritten — tests that only assert declaration text pass; `AbstractVirtualOverrideChain` asserts `base.get_Item(i)` and needs Task 4).

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarPropertyUnitTests"`
Expected: all PASS (indexer dispatch is additive).

- [ ] **Step 3.7: Commit**

```bash
git add -A
git commit -m "feat(member-desugar): indexer declaration reduction (get_Item/set_Item methods) + RewriteFile indexer dispatch with two-pass rebind"
```

---

### Task 4: Read sites — plain / `this` / `base` / null-conditional / external counting / `^`/`..` passthrough

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs` (append)

- [ ] **Step 4.1: Write failing tests**

```csharp
    [Fact]
    public void ReadSites_Rewritten_IncludingThisAndBase()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class B
            {
                public virtual int this[int i] => i;
                public int Wrap(int i) => this[i] * 2;
            }
            class D : B
            {
                public override int this[int i] => base[i] + 1;
                public int CallBase(int i) => base[i];
            }
            """);
        AssertContains(outCode, "this.get_Item(i)");
        AssertContains(outCode, "base.get_Item(i)");
        Assert.True(stats.IndexerReadSitesRewritten >= 2);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void NullConditional_ReadRewritten()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int i] => i;
                public int? M(C c) => c?[0];
            }
            """);
        AssertContains(outCode, "c?.get_Item(0)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void IndexAndRangeArguments_PreservedVerbatim()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public object this[object k] => k;
                public object Last(int a) => this[^1] + (object)this[a..^1];
            }
            """);
        AssertContains(outCode, "this.get_Item(^1)");
        AssertContains(outCode, "this.get_Item(a..^1)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void ExternalIndexerAccesses_UntouchedAndCounted()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            using System.Collections.Generic;
            class C
            {
                public int this[int i] => i;
                public int M(List<int> list, Dictionary<int, string> map, string s, int[] arr)
                    => list[0] + map[1].Length + s[2] + arr[3] + this[4];
            }
            """);
        AssertContains(outCode, "list[0]");
        AssertContains(outCode, "map[1]");
        AssertContains(outCode, "s[2]");
        AssertContains(outCode, "arr[3]");
        AssertContains(outCode, "this.get_Item(4)");
        Assert.True(stats.ExternalIndexerSites >= 3);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void MultiDimAndJaggedArrays_Untouched_BesideMultiParamIndexer()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int r, int c] => r + c;
                public int Sum(int[,] m, int[][] j)
                    => m[0, 1] + j[1][0] + this[2, 3];
            }
            """);
        AssertContains(outCode, "m[0, 1]");
        AssertContains(outCode, "j[1][0]");
        AssertContains(outCode, "get_Item(2, 3)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }
```

- [ ] **Step 4.2: Run to verify failure**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: FAIL (`get_Item(` calls absent).

- [ ] **Step 4.3: Implement read sites in `IndexerUseSiteRewriter`**

Replace the placeholder `VisitElementAccessExpression` and add the element-binding + helpers:

```csharp
    // ---- 读站点（IX-6/IX-9/IX-11）----

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        if (!TryGetPlan(node, out var plan))
        {
            CountExternalIfIndexer(node);
            return base.VisitElementAccessExpression(node);
        }
        if (IsDirectAssignmentTarget(node)) return base.VisitElementAccessExpression(node);
        _stats.IndexerReadSitesRewritten++;
        return BuildGetterInvocation(node, plan);
    }

    public override SyntaxNode? VisitElementBindingExpression(ElementBindingExpressionSyntax node)
    {
        if (!TryGetPlan(node, out var plan))
        {
            if (_model.GetSymbolInfo(node).Symbol is IPropertySymbol { IsIndexer: true } ext
                && IndexerFacts.IsExternal(ext))
                CountExternal(ext);
            return base.VisitElementBindingExpression(node);
        }
        // r?[i] → r?.get_Item(i)
        _stats.IndexerReadSitesRewritten++;
        var binding = SyntaxFactory.MemberBindingExpression(
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName(plan.GetterMethodName));
        var args = (ArgumentListSyntax)Visit(node.ArgumentList)!;
        return SyntaxFactory.InvocationExpression(binding, args)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    private static bool IsDirectAssignmentTarget(SyntaxNode node)
        => node.Parent is AssignmentExpressionSyntax a && ReferenceEquals(a.Left, node)
           || node.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax;

    private InvocationExpressionSyntax BuildGetterInvocation(
        ElementAccessExpressionSyntax node, IndexerPlan plan)
    {
        var recv = (ExpressionSyntax)Visit(node.Expression)!;
        recv = ApplyExplicitCast(recv, plan, node);
        var args = (ArgumentListSyntax)Visit(node.ArgumentList)!;
        return Invoke(
            MemberAccess(recv, plan.GetterMethodName), args);
    }

    /// <summary>显式接口实现的接收者静态类型不是该接口时插入最小限定强转（D6；
    /// 合法输入中站点总是经接口类型绑定到接口 plan，此处为防御路径）。</summary>
    private ExpressionSyntax ApplyExplicitCast(
        ExpressionSyntax recv, IndexerPlan plan, ExpressionSyntax site)
    {
        if (!plan.IsExplicitImplementation) return recv;
        var iface = plan.Indexer.ExplicitInterfaceImplementations.FirstOrDefault()
            ?.ContainingType;
        if (iface == null) return recv;
        if (_model.GetTypeInfo(site).Type is { } t
            && SymbolEqualityComparer.Default.Equals(t, iface)) return recv;
        if (recv is ParenthesizedExpressionSyntax) return recv;
        return SyntaxFactory.ParenthesizedExpression(recv);
    }

    private void CountExternalIfIndexer(ElementAccessExpressionSyntax node)
    {
        if (_model.GetSymbolInfo(node).Symbol is IPropertySymbol { IsIndexer: true } ext
            && IndexerFacts.IsExternal(ext))
            CountExternal(ext);
    }

    private void CountExternal(IPropertySymbol ext)
    {
        _stats.ExternalIndexerSites++;
        var typeName = ext.ContainingType?.ToDisplayString() ?? "?";
        if (_externalReported.Add(typeName))
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.ExternalIndexerSite,
                $"indexer access on external type '{typeName}' was left untouched (D5 gate)",
                _filePath, 0, 0, typeName));
    }
```

Note the cast expression: building `(IFoo)recv` requires `SyntaxFactory.CastExpression(typeSyntax, recv)`; the interface type syntax comes from `iface.ToMinimalDisplayString(_model, site.SpanStart)` — use:

```csharp
        var ifaceType = SyntaxFactory.ParseTypeName(
            iface.ToMinimalDisplayString(_model, site.SpanStart));
        return SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(ifaceType, recv));
```

- [ ] **Step 4.4: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: all PASS.

```bash
git add -A
git commit -m "feat(member-desugar): indexer read sites — this/base/null-conditional, external counting, ^/.. passthrough"
```

---

### Task 5: Writes — simple statement writes + ref-return positions

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs` (append)

- [ ] **Step 5.1: Write failing tests**

```csharp
    [Fact]
    public void SimpleWriteStatement_RewrittenToSetter()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public void M(C c, int v) { c[3] = v; this[1] = 2; }
            }
            """);
        AssertContains(outCode, "c.set_Item(3, v)");
        AssertContains(outCode, "this.set_Item(1, 2)");
        Assert.True(stats.IndexerWriteSitesRewritten >= 2);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void RefReturnIndexer_VariablePositionsStayLegal()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class Cells
            {
                private readonly int[] _d = new int[4];
                public ref int this[int i] => ref _d[i];
                public int M(int v)
                {
                    this[0] = v;
                    this[0] += 5;
                    this[1]++;
                    int r = this[2];
                    Take(ref _d[3]);
                    return r;
                }
                static void Take(ref int x) => x++;
            }
            """);
        AssertContains(outCode, "public ref int get_Item(int i) => ref _d[i]");
        AssertContains(outCode, "this.get_Item(0) = v");
        AssertContains(outCode, "this.get_Item(0) += 5");
        AssertContains(outCode, "this.get_Item(1)++");
        AssertContains(outCode, "int r = this.get_Item(2)");
        AssertContains(outCode, "Take(ref _d[3])");
        AssertNotContains(outCode, "set_Item");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void RefReturnIndexer_RefLocalAndRefOutArgs()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class Cells
            {
                private readonly int[] _d = new int[4];
                public ref int this[int i] => ref _d[i];
                public int M()
                {
                    ref var x = ref this[1];
                    x = 7;
                    TakeOut(out this[2]);
                    return _d[1] + _d[2];
                }
                static void TakeOut(out int y) => y = 9;
            }
            """);
        AssertContains(outCode, "ref var x = ref this.get_Item(1)");
        AssertContains(outCode, "TakeOut(out this.get_Item(2))");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }
```

- [ ] **Step 5.2: Run to verify failure**

Expected: FAIL.

- [ ] **Step 5.3: Implement write paths**

Add to `IndexerUseSiteRewriter`:

```csharp
    // ---- 写站点（IX-7）----

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is AssignmentExpressionSyntax
                { Kind: SyntaxKind.SimpleAssignmentExpression } assign
            && assign.Left is ElementAccessExpressionSyntax ea
            && TryGetPlan(ea, out var writePlan)
            && !writePlan.ReturnsByRef)
        {
            _stats.IndexerWriteSitesRewritten++;
            var value = (ExpressionSyntax)Visit(assign.Right)!;
            return node.WithExpression(BuildSetterInvocation(ea, writePlan, value));
        }
        if (node.Expression is ObjectCreationExpressionSyntax oc
            && LiftIndexerCreation(oc, out var stmts, out _))
            return Block(stmts)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // ref 返回索引器：写/复合/自增一律经 getter 变量（D9）。
        if (node.Left is ElementAccessExpressionSyntax left
            && TryGetPlan(left, out var plan)
            && plan.ReturnsByRef)
        {
            var newLeft = BuildGetterInvocation(left, plan);
            var right = (ExpressionSyntax)Visit(node.Right)!;
            return SyntaxFactory.AssignmentExpression(node.Kind(), newLeft, node.OperatorToken, right)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitAssignmentExpression(node);
    }

    private InvocationExpressionSyntax BuildSetterInvocation(
        ElementAccessExpressionSyntax node, IndexerPlan plan, ExpressionSyntax value)
    {
        var recv = (ExpressionSyntax)Visit(node.Expression)!;
        var args = (ArgumentListSyntax)Visit(node.ArgumentList)!;
        var withValue = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(args.Arguments.Append(SyntaxFactory.Argument(value))));
        return Invoke(MemberAccess(recv, plan.SetterMethodName), withValue);
    }
```

`LiftIndexerCreation` is added in Task 7; until then add a stub that returns false (or move the ObjectCreation branch to Task 7 — for Task 5 keep the branch out and add it in Task 7).

- [ ] **Step 5.4: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: all PASS.

```bash
git add -A
git commit -m "feat(member-desugar): indexer write sites — setter statement writes, ref-return variable positions"
```

---

### Task 6: Compound assignment & increments with single-evaluation lifts

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs` (append)

- [ ] **Step 6.1: Write failing tests**

```csharp
    [Fact]
    public void Compound_SimpleArgs_ExpandedWithoutLift()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public void M(int i, int v) { this[i] += v; this[i] -= 1; this[i] *= 2; }
            }
            """);
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) + v)");
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) - 1)");
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) * 2)");
        Assert.True(stats.IndexerCompoundSitesExpanded >= 3);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void Compound_SideEffectArgument_EvaluatedExactlyOnce()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                private int _seed;
                public int Next() => _seed++;
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public int Bump(int v)
                {
                    this[Next()] += v;
                    return _seed;
                }
            }
            """);
        AssertContains(outCode, "var __cs2j_idx0 = C.Next()");
        AssertContains(outCode, "this.set_Item(__cs2j_idx0, this.get_Item(__cs2j_idx0) + v)");
        // Next() 在产物中只出现一次（单次求值硬断言）。
        Assert.Equal(1, CountOccurrences(outCode, "Next()"));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    private static int CountOccurrences(string code, string token)
    {
        var count = 0;
        for (var idx = code.IndexOf(token, StringComparison.Ordinal);
             idx >= 0;
             idx = code.IndexOf(token, idx + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void IncrementStatements_Expanded()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public void M(int i) { this[i]++; --this[i]; }
            }
            """);
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) + 1)");
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) - 1)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void ConsumedCompoundInExpression_GatedWith5137()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public int M(int i) => F(this[i] += 1);
                static int F(int x) => x;
            }
            """);
        Assert.Contains(CS2J(5137), Codes(diags));
        Assert.Contains("this[int i]", outCode); // plan 原子跳过
    }
```

- [ ] **Step 6.2: Run to verify failure** — Expected: FAIL.

- [ ] **Step 6.3: Implement compound/increment statement expansion**

Add to `IndexerUseSiteRewriter` (plus the binary-kind helpers mirrored from C11):

```csharp
    // ---- 复合/自增（IX-8 / D11：索引实参单次求值）----

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // （Task 5 的简单写分支保留在方法最前）
        if (node.Expression is AssignmentExpressionSyntax compound
            && !compound.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && compound.Left is ElementAccessExpressionSyntax cea
            && TryGetPlan(cea, out var cplan)
            && !cplan.ReturnsByRef)
        {
            _stats.IndexerCompoundSitesExpanded++;
            return ExpandCompoundStatement(node, compound, cea, cplan);
        }

        var isIncDec = node.Expression switch
        {
            PrefixUnaryExpressionSyntax p => p.IsKind(SyntaxKind.PreIncrementExpression)
                                             || p.IsKind(SyntaxKind.PreDecrementExpression),
            PostfixUnaryExpressionSyntax p => p.IsKind(SyntaxKind.PostIncrementExpression)
                                              || p.IsKind(SyntaxKind.PostDecrementExpression),
            _ => false,
        };
        if (isIncDec)
        {
            var operand = node.Expression switch
            {
                PrefixUnaryExpressionSyntax p => p.Operand,
                PostfixUnaryExpressionSyntax p => p.Operand,
                _ => null!,
            };
            if (operand is ElementAccessExpressionSyntax iea
                && TryGetPlan(iea, out var iplan)
                && !iplan.ReturnsByRef)
            {
                _stats.IndexerCompoundSitesExpanded++;
                var increment = node.Expression.IsKind(SyntaxKind.PreIncrementExpression)
                                || node.Expression.IsKind(SyntaxKind.PostIncrementExpression);
                return ExpandCompoundStatement(
                    node,
                    SyntaxFactory.AssignmentExpression(
                        increment
                            ? SyntaxKind.AddAssignmentExpression
                            : SyntaxKind.SubtractAssignmentExpression,
                        iea,
                        SyntaxFactory.Token(increment ? SyntaxKind.PlusToken : SyntaxKind.MinusToken),
                        One()),
                    iea,
                    iplan);
            }
        }
        return base.VisitExpressionStatement(node);
    }

    /// <summary>c[k] op= v →（简单实参）c.set_Item(k, c.get_Item(k) op v)；
    /// 带副作用实参 → 提升临时变量块（语句位必有可拼接块上下文）。</summary>
    private SyntaxNode ExpandCompoundStatement(
        ExpressionStatementSyntax node, AssignmentExpressionSyntax compound,
        ElementAccessExpressionSyntax ea, IndexerPlan plan)
    {
        var right = (ExpressionSyntax)Visit(compound.Right)!;
        var allSimple = ea.ArgumentList.Arguments.All(a => IndexerFacts.IsSimpleArgument(a.Expression));

        if (allSimple)
        {
            var read = BuildGetterInvocation(ea, plan);
            var bin = Binary(compound.Kind(), compound.OperatorToken.Kind(), read, right);
            return node.WithExpression(BuildSetterInvocation(ea, plan, bin));
        }

        var stmts = new List<StatementSyntax>();
        var argIdents = new List<ExpressionSyntax>();
        foreach (var arg in ea.ArgumentList.Arguments)
        {
            if (IndexerFacts.IsSimpleArgument(arg.Expression))
            {
                argIdents.Add(arg.Expression);
                continue;
            }
            var t = TempName("idx");
            stmts.Add(DeclVar(t, (ExpressionSyntax)Visit(arg.Expression)!));
            argIdents.Add(Ident(t));
        }
        var tempArgs = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(argIdents.Select(SyntaxFactory.Argument)));
        var recv = (ExpressionSyntax)Visit(ea.Expression)!;
        var read2 = Invoke(MemberAccess(recv, plan.GetterMethodName), tempArgs);
        var bin2 = Binary(compound.Kind(), compound.OperatorToken.Kind(), read2, right);
        var setArgs = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            tempArgs.Arguments.Append(SyntaxFactory.Argument(bin2))));
        stmts.Add(ExprStmt(Invoke(MemberAccess(recv, plan.SetterMethodName), setArgs)));
        return Block(stmts)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    private static LiteralExpressionSyntax One()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));

    private static BinaryExpressionSyntax Binary(
        SyntaxKind assignmentKind, SyntaxKind operatorKind,
        ExpressionSyntax left, ExpressionSyntax right)
    {
        var binaryKind = ToBinaryKind(assignmentKind);
        return SyntaxFactory.BinaryExpression(binaryKind, left,
            SyntaxFactory.Token(ToBinaryToken(binaryKind)), right);
    }

    private static SyntaxKind ToBinaryKind(SyntaxKind assignmentKind) => assignmentKind switch
    {
        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
        SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
        SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
        SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
        SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
        SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
        SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
        SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
        SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
        SyntaxKind.UnsignedRightShiftAssignmentExpression => SyntaxKind.UnsignedRightShiftExpression,
        SyntaxKind.CoalesceAssignmentExpression => SyntaxKind.CoalesceExpression,
        _ => SyntaxKind.AddExpression,
    };

    private static SyntaxKind ToBinaryToken(SyntaxKind binaryKind) => binaryKind switch
    {
        SyntaxKind.AddExpression => SyntaxKind.PlusToken,
        SyntaxKind.SubtractExpression => SyntaxKind.MinusToken,
        SyntaxKind.MultiplyExpression => SyntaxKind.AsteriskToken,
        SyntaxKind.DivideExpression => SyntaxKind.SlashToken,
        SyntaxKind.ModuloExpression => SyntaxKind.PercentToken,
        SyntaxKind.BitwiseAndExpression => SyntaxKind.AmpersandToken,
        SyntaxKind.BitwiseOrExpression => SyntaxKind.BarToken,
        SyntaxKind.ExclusiveOrExpression => SyntaxKind.CaretToken,
        SyntaxKind.LeftShiftExpression => SyntaxKind.LessThanLessThanToken,
        SyntaxKind.RightShiftExpression => SyntaxKind.GreaterThanGreaterThanToken,
        SyntaxKind.UnsignedRightShiftExpression => SyntaxKind.GreaterThanGreaterThanGreaterThanToken,
        SyntaxKind.CoalesceExpression => SyntaxKind.QuestionQuestionToken,
        _ => SyntaxKind.PlusToken,
    };
```

Note: when Task 6 lands, merge the Task-5 `VisitExpressionStatement` simple-write branch and this one into a single method (write branch first, then compound, then increment, then ObjectCreation lift from Task 7, then `base`).

- [ ] **Step 6.4: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: all PASS.

```bash
git add -A
git commit -m "feat(member-desugar): indexer compound/increment expansion with single-evaluation argument lifts"
```

---

### Task 7: Collection initializer lifts — `new C { [k] = v }`

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs` (append)

- [ ] **Step 7.1: Write failing tests**

```csharp
    [Fact]
    public void IndexInitializer_StatementPosition_Lifted()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int k] { get => k; set { } }
                public void M()
                {
                    var c = new C { [1] = 10, [2] = 20 };
                    c[3] = 30;
                }
            }
            """);
        AssertContains(outCode, "var __cs2j_init0 = new C()");
        AssertContains(outCode, "__cs2j_init0.set_Item(1, 10)");
        AssertContains(outCode, "__cs2j_init0.set_Item(2, 20)");
        AssertContains(outCode, "c.set_Item(3, 30)");
        Assert.True(stats.IndexerInitializerSitesRewritten >= 1);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void IndexInitializer_NonLiftableField_GatedWith5137()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int k] { get => k; set { } }
            }
            class Host
            {
                private readonly C _c = new C { [1] = 10 };
            }
            """);
        Assert.Contains(CS2J(5137), Codes(diags));
        Assert.Contains("this[int k]", outCode);
    }
```

- [ ] **Step 7.2: Run to verify failure** — Expected: FAIL (initializer untouched; `new C { [1] = ... }` survives and the plan is NOT gated because the lift machinery is missing → the whole run leaves the tree unchanged only via rollback, which the unit harness does not exercise — the test asserts the gate, which fires once `LiftIndexerCreation` exists? No: the GATE is analysis-side (`IsLiftableCreation`) and fires already; the failure here is `Assert.Contains(CS2J(5137))` may already pass while the statement-lift test fails. Both must pass after implementation.)

- [ ] **Step 7.3: Implement `LiftIndexerCreation` + statement-position hooks**

Add to `IndexerUseSiteRewriter` (the `ObjectCreation` branch in `VisitExpressionStatement` from Task 5 activates now):

```csharp
    // ---- 集合初始化器提升（IX-10 / D13，镜像 C11 LiftCreation）----

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (CanLiftLocalDeclaration(node)
            && node.Declaration.Variables[0].Initializer?.Value is ObjectCreationExpressionSyntax oc
            && LiftIndexerCreation(oc, out var stmts, out var result))
        {
            var declarator = node.Declaration.Variables[0]
                .WithInitializer(SyntaxFactory.EqualsValueClause(result));
            var final = node.WithDeclaration(node.Declaration
                .WithVariables(SyntaxFactory.SeparatedList(new[] { declarator })));
            return Block(stmts.Append(final))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitLocalDeclarationStatement(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression is ObjectCreationExpressionSyntax oc
            && node.Parent is BlockSyntax or SwitchSectionSyntax
            && LiftIndexerCreation(oc, out var stmts, out var result))
        {
            var @return = SyntaxFactory.ReturnStatement(result.WithLeadingTrivia(SyntaxFactory.Space));
            return Block(stmts.Append(@return))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitReturnStatement(node);
    }

    private bool LiftIndexerCreation(
        ObjectCreationExpressionSyntax oc, out List<StatementSyntax> stmts, out ExpressionSyntax result)
    {
        stmts = new List<StatementSyntax>();
        result = oc;
        if (oc.Initializer is not { } init || !init.IsKind(SyntaxKind.ObjectInitializerExpression))
            return false;
        var assigns = init.Expressions.OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is ImplicitElementAccessSyntax iea && TryGetPlan(iea, out _))
            .ToList();
        if (assigns.Count == 0) return false;

        var temp = TempName("init");
        var kept = init.Expressions
            .Where(e => e is not AssignmentExpressionSyntax a
                || a.Left is not ImplicitElementAccessSyntax i
                || !TryGetPlan(i, out _))
            .ToList();
        var keptCreation = oc.WithInitializer(kept.Count > 0
            ? SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression, SyntaxFactory.SeparatedList(kept))
            : null);
        // `new C { .. }` 去掉初始化器后必须补空参数表，否则 `new C` 不是合法表达式。
        if (kept.Count == 0 && keptCreation.ArgumentList == null)
            keptCreation = keptCreation.WithArgumentList(SyntaxFactory.ArgumentList());
        stmts.Add(DeclVar(temp, (ExpressionSyntax)Visit(keptCreation)!));

        var tempIdent = Ident(temp);
        foreach (var a in assigns)
        {
            var iea = (ImplicitElementAccessSyntax)a.Left;
            TryGetPlan(iea, out var plan);
            var args = (ArgumentListSyntax)Visit(iea.ArgumentList)!;
            var withValue = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                args.Arguments.Append(SyntaxFactory.Argument((ExpressionSyntax)Visit(a.Right)!))));
            stmts.Add(ExprStmt(Invoke(
                MemberAccess(tempIdent, plan.SetterMethodName), withValue)));
        }
        result = tempIdent;
        _stats.IndexerInitializerSitesRewritten++;
        return true;
    }

    private static bool CanLiftLocalDeclaration(LocalDeclarationStatementSyntax node)
        => node.Declaration.Variables.Count == 1
           && node.UsingKeyword.IsKind(SyntaxKind.None)
           && node.AwaitKeyword.IsKind(SyntaxKind.None)
           && !node.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)
               || m.IsKind(SyntaxKind.StaticKeyword));
```

`TryGetPlan` already accepts `ImplicitElementAccessSyntax` (Task 3 skeleton).

- [ ] **Step 7.4: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerUnitTests"`
Expected: all PASS.

```bash
git add -A
git commit -m "feat(member-desugar): indexer collection-initializer lifts (new C { [k] = v } -> temp + set_Item)"
```

---

### Task 8: Explicit interface implementations + `[IndexerName]` + mixed property/indexer file

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs` (nothing new unless tests demand)
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs` (append)

- [ ] **Step 8.1: Write failing tests**

```csharp
    [Fact]
    public void ExplicitInterface_QualifiedMethodsGenerated()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            interface IFoo
            {
                int this[int i] { get; set; }
            }
            class C : IFoo
            {
                int IFoo.this[int i] { get => i; set { } }
                public int ViaIface(IFoo f) => f[0];
            }
            """);
        AssertContains(outCode, "int IFoo.get_Item(int i) => i;");
        AssertContains(outCode, "void IFoo.set_Item(int i, int value)");
        AssertContains(outCode, "f.get_Item(0)");
        Assert.True(stats.ExplicitInterfaceIndexers >= 1);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void TwoExplicitInterfaces_NoCollision()
    {
        var (outCode, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            interface IA { int this[int i] { get; } }
            interface IB { int this[int i] { get; } }
            class C : IA, IB
            {
                int IA.this[int i] => i;
                int IB.this[int i] => i + 1;
            }
            """);
        AssertContains(outCode, "int IA.get_Item(int i) => i;");
        AssertContains(outCode, "int IB.get_Item(int i) => i + 1;");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void IndexerNameAttribute_CustomNameUsed_AttributeRemoved()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            using System.Runtime.CompilerServices;
            class C
            {
                [IndexerName("Slot")]
                public string this[int i] => i.ToString();
                public string Get(int i) => this[i];
            }
            """);
        AssertContains(outCode, "public string get_Slot(int i) => i.ToString();");
        AssertContains(outCode, "this.get_Slot(i)");
        AssertNotContains(outCode, "IndexerName");
        Assert.Contains(CS2J(5131), Codes(diags));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void DefaultMemberAttribute_RetainedWith5133()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            using System.Reflection;
            [DefaultMember("Item")]
            class C
            {
                public int this[int i] => i;
            }
            """);
        AssertContains(outCode, "[DefaultMember(\"Item\")]");
        Assert.Contains(CS2J(5133), Codes(diags));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void MixedPropertyAndIndexerInOneFile_BothDesugared()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int Count { get; set; }
                public int this[int i] { get => i; set { } }
                public int M(int i) { Count = this[i]; return Count + this[i]; }
            }
            """);
        AssertContains(outCode, "set_Count(this.get_Item(i))");
        AssertContains(outCode, "get_Count() + this.get_Item(i)");
        Assert.True(stats.PropertiesPlanned >= 1 && stats.IndexersPlanned >= 1);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void Idempotent_SecondRun_NoChange()
    {
        var (first, _, _) = MemberDesugarPropertyUnitTests.Rewrite(SimpleIndexer);
        var (second, _, _) = MemberDesugarPropertyUnitTests.Rewrite(first);
        Assert.Equal(Normalize(first), Normalize(second));
    }
```

- [ ] **Step 8.2: Run to verify failure** — explicit-interface decl tests fail until the expander handles `ExplicitInterfaceSpecifier` (already implemented in Task 3; the failure mode here is likely the `f.get_Item(0)` site or the two-interface case); fix whatever surfaces. The mixed-file test exercises the Task-3 rebind path.

- [ ] **Step 8.3: Fix surfaced issues (expected small)**

Likely adjustments (verify against actual failures):
- In `IndexerDeclarationExpander`, if the explicit-interface method needs the interface's parameter types rather than the declaration's (they are identical in valid code — verbatim copy is correct).
- If `Expand` emits the `IndexerName` Info diagnostic twice (once per file pass on multi-file trees), guard it with a first-declaration-file check passed from the rewriter.

- [ ] **Step 8.4: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugar"`
Expected: property + indexer suites all PASS.

```bash
git add -A
git commit -m "test(member-desugar): explicit-interface indexers, IndexerName/DefaultMember, mixed property+indexer file, idempotency"
```

---

### Task 9: Adapter summary line + residual gate coverage

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/MemberDesugarPassAdapter.cs:258-269`
- Test: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs` (append)

- [ ] **Step 9.1: Write the failing residual test (uses InternalsVisibleTo)**

```csharp
    [Fact]
    public void ExcludedPlan_IndexerDeclarationBecomesResidual()
    {
        var src = """
            class C
            {
                public int this[int i] => i;
                public int M() => this[0];
            }
            """;
        var model = MemberDesugarPropertyUnitTests.ParseWithSemanticsPublic(src);
        var pp = new MemberDesugarPreprocessor();
        pp.BindCompilation(model.Compilation);
        var planId = pp.Plans.Single(p => p is Indexers.IndexerPlan).Id;
        pp.ExcludePlans(new[] { planId });
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        Assert.Contains(CS2J(5141), Codes(diags));
        Assert.Contains("this[int i]", result.OutputCode); // 排除 plan → 声明保持
    }
```

This requires a small public shim in `MemberDesugarPropertyUnitTests` (the existing `ParseWithSemantics` is private):

```csharp
    internal static SemanticModel ParseWithSemanticsPublic(string src)
        => ParseWithSemantics(src);
```

- [ ] **Step 9.2: Run to verify failure** — Expected: FAIL (residual diagnostics not emitted because the excluded plan leaves the declaration to the rewriter's `_knownSymbols` path — verify this path actually emits; it should, since excluded plans are absent from `active`).

- [ ] **Step 9.3: Extend `BuildChainSummaryLine` in `MemberDesugarPassAdapter`**

Replace the method body with:

```csharp
    public override string BuildChainSummaryLine()
    {
        _statistics.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualProperty, out var residual);
        _statistics.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualIndexer, out var indexerResidual);
        return $"member-desugar: {_statistics.PropertiesPlanned} propert(y/ies) planned, " +
               $"{_statistics.BackingFieldsGenerated} field(s) generated, " +
               $"{_statistics.ReadSitesRewritten} read/{_statistics.WriteSitesRewritten} write/" +
               $"{_statistics.CompoundSitesRewritten} compound site(s) rewritten, " +
               $"{_statistics.InitializersRewritten} initializer(s), " +
               $"{_statistics.IndexersPlanned} indexer(s) planned, " +
               $"{_statistics.IndexerReadSitesRewritten} index read/" +
               $"{_statistics.IndexerWriteSitesRewritten} write/" +
               $"{_statistics.IndexerCompoundSitesExpanded} compound site(s) rewritten, " +
               $"{_statistics.IndexerInitializerSitesRewritten} index initializer(s), " +
               $"{_statistics.PlansSkipped} skipped, " +
               $"{_statistics.PlansRolledBack} rolled back, " +
               $"{residual} residual, {indexerResidual} indexer residual";
    }
```

- [ ] **Step 9.4: Run tests, then commit**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release`
Expected: whole project suite PASS (including CLI/consistency tests from C11).

```bash
git add -A
git commit -m "feat(member-desugar): adapter summary indexer segment + residual gate coverage for indexers"
```

---

### Task 10: Fixture — `IndexerSamples` in the sample solution + project-level test

**Files:**
- Create: `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/IndexerSamples.cs`
- Modify: `tests/SampleMemberDesugarSolution/SampleMemberDesugarApp/Program.cs` (append usage)
- Modify: `tests/SampleMemberDesugarSolution/SampleMemberDesugarTests/BehaviorTests.cs` (append tests)
- Create: `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerProjectTests.cs`

- [ ] **Step 10.1: Add the fixture library file**

Create `tests/SampleMemberDesugarSolution/SampleMemberDesugarLib/IndexerSamples.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace SampleMemberDesugarLib;

public interface IRow
{
    int this[int index] { get; }
}

public interface IGrid
{
    int this[int row, int col] { get; set; }
}

public abstract class MatrixBase
{
    public abstract int this[int row, int col] { get; set; }
}

public sealed class Grid : MatrixBase
{
    private readonly int[] _cells = new int[16];

    public override int this[int row, int col]
    {
        get => _cells[row * 4 + col];
        set => _cells[row * 4 + col] = value;
    }

    public int Sum() => this[0, 0] + this[1, 1] + this[2, 2];
}

public sealed class NamedIndexer
{
    private readonly string[] _vals = new string[8];

    [IndexerName("Slot")]
    public string this[int i]
    {
        get => _vals[i];
        set => _vals[i] = value;
    }
}

public sealed class ExplicitGrid : IRow, IGrid
{
    private readonly int[] _cells = new int[16];

    int IRow.this[int index] => _cells[index];

    int IGrid.this[int row, int col]
    {
        get => _cells[row * 4 + col];
        set => _cells[row * 4 + col] = value;
    }

    public int Total(IRow row, IGrid grid)
    {
        grid[0, 1] = 5;
        return row[0] + grid[0, 1];
    }
}

public sealed class RefCells
{
    private readonly int[] _data = new int[4];

    public ref int this[int i] => ref _data[i];

    public int Bump()
    {
        this[1] = 7;
        this[1] += 5;
        this[2]++;
        return _data[1] + _data[2];
    }
}

public sealed class SideEffectIndexer
{
    private readonly int[] _vals = new int[8];
    private int _seed;

    public int Next() => _seed++;

    public int this[int i]
    {
        get => _vals[i];
        set => _vals[i] = value;
    }

    public int Bump()
    {
        this[Next()] += 10;
        return _vals[1] + _seed;
    }
}
```

- [ ] **Step 10.2: Append cross-project usage in `SampleMemberDesugarApp/Program.cs`**

Append inside the existing `Program` class (keep existing members intact):

```csharp
    public static string UseIndexers()
    {
        var grid = new SampleMemberDesugarLib.Grid();
        grid[1, 2] = 9;
        var named = new SampleMemberDesugarLib.NamedIndexer();
        named[3] = "abc";
        var cells = new SampleMemberDesugarLib.RefCells();
        var total = cells.Bump();
        var se = new SampleMemberDesugarLib.SideEffectIndexer();
        var seed = se.Bump();
        return $"{grid[1, 2]}|{named[3]}|{total}|{seed}";
    }
```

- [ ] **Step 10.3: Append behavior tests in `SampleMemberDesugarTests/BehaviorTests.cs`**

```csharp
    [Fact]
    public void Indexers_BehaveIdentically()
    {
        var grid = new Grid();
        grid[1, 2] = 9;
        Assert.Equal(9, grid[1, 2]);
        Assert.Equal(9, grid.Sum() - grid[0, 0] - grid[1, 1] - grid[2, 2]);

        var named = new NamedIndexer();
        named[3] = "abc";
        Assert.Equal("abc", named[3]);

        var eg = new ExplicitGrid();
        IRow row = eg;
        IGrid g2 = eg;
        g2[0, 1] = 5;
        Assert.Equal(5, row[1]);

        Assert.Equal(13, new RefCells().Bump());

        var se = new SideEffectIndexer();
        se.Bump();
        Assert.Equal(1, typeof(SideEffectIndexer).GetField("_seed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(se));
    }

    [Fact]
    public void Indexers_SingleEvaluationOfSideEffectingArgument()
    {
        var se = new SideEffectIndexer();
        se.Bump(); // this[Next()] += 10 —— Next() 必须只调用一次
        Assert.Equal(1, typeof(SideEffectIndexer).GetField("_seed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(se));
    }
```

(Place them in the existing test class/namespace; adjust to the file's existing usings.)

- [ ] **Step 10.4: Create `MemberDesugarIndexerProjectTests.cs`**

```csharp
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;

namespace CSharpToJava.ProjectTests;

/// <summary>C12 indexer project-level coverage（设计 §7.2）：SampleMemberDesugarSolution
/// 跑 member-desugar 适配器，断言索引器分项统计与中间树文本；VerifyBuild/VerifyTests
/// 默认开 → run.Success 已含中间树 dotnet build + dotnet test（含跨工程 App→Lib 用点）。</summary>
public class MemberDesugarIndexerProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    [Fact]
    public async Task Solution_IndexersDesugared_AndIntermediateBuildsAndTests()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-md-idx");
        try
        {
            var adapter = new MemberDesugarPassAdapter(
                new MemberDesugarOptions(), verbose: false,
                filterUnsupportedProjects: true, compatNamespacePrefixes: null);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Where(d => d.Severity == MemberDesugarSeverity.Error)
                    .Select(d => d.ToMsBuildString())));
            Assert.True(adapter.Statistics.IndexersPlanned >= 6,
                "expected indexer plans from IndexerSamples");
            Assert.True(adapter.Statistics.IndexerReadSitesRewritten > 0);
            Assert.True(adapter.Statistics.IndexerWriteSitesRewritten > 0);
            Assert.True(adapter.Statistics.IndexerCompoundSitesExpanded > 0);
            Assert.True(adapter.Statistics.IndexerInitializerSitesRewritten == 0
                        || adapter.Statistics.IndexerInitializerSitesRewritten > 0); // fixture 无初始化器用点亦可
            Assert.True(adapter.Statistics.ExplicitInterfaceIndexers >= 2);

            var samples = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib", "IndexerSamples.cs"));
            Assert.DoesNotContain("this[", samples);
            Assert.Contains("get_Item(", samples);
            Assert.Contains("set_Item(", samples);
            Assert.Contains("IRow.get_Item(", samples);
            Assert.Contains("get_Slot(", samples);
            Assert.Contains("ref int get_Item(", samples);

            var program = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarApp", "Program.cs"));
            Assert.Contains("grid.set_Item(1, 2, 9)", program);
            Assert.Contains("grid.get_Item(1, 2)", program);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }
}
```

- [ ] **Step 10.5: Run the fixture pre-desugar behavior gate (must stay green)**

Run: `dotnet test tests/SampleMemberDesugarSolution/SampleMemberDesugarSolution.sln -c Release`
Expected: all behavior tests PASS (the fixture compiles and behaves before any desugaring — C11's 12 existing + the new indexer tests).

- [ ] **Step 10.6: Run the project test (desugared tree build+test gate)**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugarIndexerProjectTests"`
Expected: PASS (this drives the adapter: baseline → rewrite → recompile → diff → restore → dotnet build + dotnet test on the intermediate tree). If it fails, the adapter diagnostics list contains the attributed plan / whole-tree rollback reason — fix the rewriter accordingly.

- [ ] **Step 10.7: Commit**

```bash
git add -A
git commit -m "test(member-desugar): SampleMemberDesugarSolution indexer fixtures + project-level indexer gate"
```

---

### Task 11: Full verification — all suites green

- [ ] **Step 11.1: Full Release build**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release`
Expected: `Build succeeded`, 0 errors, no new warnings vs. baseline.

- [ ] **Step 11.2: ProjectTests suite (member-desugar + preprocessing chains)**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release`
Expected: all PASS — including `MemberDesugarPropertyUnitTests`, `MemberDesugarIndexerUnitTests`, `MemberDesugarProjectTests`, `MemberDesugarIndexerProjectTests`, `ProjectPreprocessorsConsistentTests`, `PassChainOrderTests`, `PassStubCatalogTests`.

- [ ] **Step 11.3: Converter regression suites (design §7.3 — must be untouched and green)**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release`
Expected: all PASS — including `MyDictIndexerTests`, `DictionaryLikeIndexerSetterTests`, `MultiArgIndexerCompoundAssignmentTests`, `DictionaryIndexerReadCheckedTests`, `InterfaceIndexerReturnTypeTests`, `ArrayIndexerAccessTests` (member-desugar stays default-off; these suites bypass preprocessing).

Run: `dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj -c Release`
Expected: all PASS.

- [ ] **Step 11.4: Final commit (if any leftovers) and summary**

```bash
git status --short
git add -A && git commit -m "chore(member-desugar): C12 indexer rule full-suite verification" || true
```

---

## Self-Review Notes (checked)

- **Spec coverage**: D1-D18 map to Tasks 1-9 (naming→T1/T3; gates→T2; declaration IX-1..5→T3; read IX-6/9/11→T4; write IX-7→T5; compound IX-8→T6; initializer IX-10→T7; explicit/attributes→T3/T8; residual/summary→T9; fixture §7.2→T10). M5 (sdk scan) and M6 (converter follow-up) are explicitly out of scope for implementation (§9 of the design marks them separate).
- **Type consistency**: `IndexerPlan` fields (`Indexer/MetadataName/GetterMethodName/SetterMethodName/HasSetter/HasInitAccessor`) used consistently; stats counter names match Task 1; diagnostic names match the enum appended in Task 1.
- **No placeholders**: every step has complete code or exact commands.
