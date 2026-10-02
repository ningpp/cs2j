# C20 partial 类型/方法合并前置 — Implementation Plan

> **For agentic workers:** 按任务顺序实施本计划；步骤用 checkbox（`- [ ]`）跟踪。执行环境：分支 `dev-3.x`，**禁止 worktree**（用户 2026-10-02 强制约束），直接在当前工作区提交。

**Goal:** 翻转注册表 stub `partial-merge` 为 Implemented，在词法链位（E01 conditional、C28 lexical-normalize 之后，一切语义改写之前）把跨文件 partial 类型组合并为「每个源生类型在树中只剩一份声明节点」的中间 C# 树（模型 B：主部件原地承载合并声明，其余部件摘除节点、文件不删），默认长期关（opt-in），经 M4（§8 门槛）后才默认化。

**Architecture:** Core 新目录 `PartialMerge/`（整树符号分析一次 → TypeGroupPlan → MergeBuilder 在主部件节点上构造合并声明 → Rewriter 逐文件确定性编辑）；CLI 单适配器 `PartialMergePassAdapter` 自定义 `ExecuteAsync`，结构镜像 `JavaKeywordsPassAdapter`：复制整树 → 基线快照 → 分析 → 逐文件改写 → 候选 recompile-diff → 组级 CS2J2001 / 整轮 CS2J2002 回滚 → 有界重放 → residual CS2J2018 → restore + build/test 双 verify。不新增 convert-project 专属开关与 legacy toggle，opt-in 仅经 `--enable-pass partial-merge`。转换器侧现有 `PartialType/` 机制不改不删（退役是 M3 之后的独立 PR）。

**Tech Stack:** C# / .NET 10 / Roslyn 5.0（`Microsoft.CodeAnalysis.CSharp`，含 `CSharpCompilation`/`SemanticModel`，parse 锁 `LanguageVersion.Latest`）/ xUnit / CommandLine parser。

**设计**：[2026-09-29-c20-partial-merge-design.md](../specs/2026-09-29-c20-partial-merge-design.md)（2026-10-02 第三次校订定稿，锚点 HEAD `0a5372ae`）

---

## 0. 基线裁决（开工前确认）

1. 开工 HEAD：**`ff63b20f`**（分支 `dev-3.x`）。先跑 `git branch --show-current`，输出必须为 `dev-3.x`；否则停止。再跑 `git fetch` 与 `git pull --ff-only`；若非 fast-forward 则停止并回报，不 rebase 不 merge。
2. 锚点口径：设计文档锚定 `0a5372ae`；当前 HEAD `ff63b20f` 相对该锚点新增两个 C28 提交——`c3250a07`（仅改 `tests/CSharpToJava.Tests/LexicalNormalizeTests.cs`，+98/-2）与 `ff63b20f`（C28 M2：`LexicalNormalizePassAdapter.cs` +2/-2、`LexicalNormalizePreprocessor.cs` +50、ProjectTests csproj +9、新增 C28 测试与 SampleLexicalNormalizeSolution fixture）。**C20 全部生产集成点（PassStubCatalog/PassCatalog/PassChainFactory/CliPreprocessChain/ProjectPassRunner/JavaKeywordsPassAdapter/TuplePassAdapter/PassChainOrderTests）零改动，§2 锚点继续有效**；唯一变动是 ProjectTests csproj 行号（见 §2，已重锚）。实施中每步仍以 Read 实际内容为准；若 `pull --ff-only` 再拉入新提交，按新 HEAD 重核 §2。
3. 开工时点工作区在途改动（均与本任务无关）：
   - ` M docs/superpowers/specs/2026-09-29-c20-partial-merge-design.md`——**本特性设计稿**，可随 M1 首个提交或单独提交，逐文件 `git add`；
   - `?? prompts/preprocessor-completion-audit-and-order.md`——非本任务，**不 add、不裹挟**。
4. 本计划所有提交必须**逐文件 `git add <本任务文件>`**，严禁 `git add -A`/`git add .`；提交前以 `git status` 复核暂存内容仅含本特性文件。
5. 不新建支撑工程；不修改 `Transformers/**`、`PartialType/**`、`PartialTypeMergeStore`、`TypeGroupResolver`（设计 D12 并存纪律）；不 push、不建标签（除非用户另行授权）。
6. 提交节奏：**M1（Tasks 1-12）→ M2（Tasks 13-21）→ M3（Tasks 22-25，可选增强）→ M4（Task 26，决策留痕）**；每个 Task 结束可提交，里程碑末必须全仓 Release 验证。

## 1. 文件结构（新建/修改总览）

```
src/CSharpToJava.Core/PartialMerge/                            # 全部新建
  PartialMergeModels.cs          # Options/Statistics/Diagnostic/Severity + MutableStats + Codes
  PartialMergePlan.cs            # TypePartInfo/TypeGroupPlan/FileEdit(Replace/Remove)/PartialMergePlan
  PartialTypePartCollector.cs    # 符号遍历 → 源内部件分组（嵌套递归、top-level 跳过、主部件排序）
  PartialTypeMergeBuilder.cs     # 合并声明：修饰符/基类/特性/约束/成员/文档，保留 partial
  PartialMemberMerge.cs          # 成员按部件序拼接（M1）；M3 配对塌缩
  PartialDocCommentMerger.cs     # M2：D6 类型/成员 XML 合并
  PartialMergeAnalyzer.cs        # Analyze(compilation, options, exclusions) → PartialMergePlan
  PartialMergeRewriter.cs        # Apply(source, edits) → 确定性文本（span 降序 TextChange）
  PartialMergePreprocessor.cs    # public 门面：Analyze + RewriteFile + Residual
src/CSharpToJava.CLI/Preprocessing/Adapters/
  PartialMergePassAdapter.cs     # 新建，自定义 ExecuteAsync（镜像 JavaKeywordsPassAdapter）
tests/CSharpToJava.ProjectTests/
  PartialMergeUnitTests.cs       # 新建，Core 单测矩阵（设计 §7.1）
  PartialMergeProjectTests.cs    # 新建，fixture 项目级验收
  PartialMergeCLITests.cs        # 新建，CLI 端到端
tests/SamplePartialSolution/     # 新建 fixture（Lib + App + Tests + .sln）
# 修改（行内追加，锚点见 §2）：
  src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs         # 删 stub 条目、留 Ids 常量
  src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs             # 插 Implemented 描述符 + local-function-lift 边
  src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs        # CreateAdapter 加臂（无 toggle/config factory）
  tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs # LockedChain/EnabledByDefault/顺序/catalog
  tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
```

## 2. 集成点行号锚点（2026-10-02 基线，随 Read 复核）

| 文件 | 位置 |
|------|------|
| PassStubCatalog.cs | partial-merge stub 条目 **L51-61（删）**；`Ids.PartialMerge` **L23（保留）** |
| PassCatalog.cs | conditional **L41-49**；lexical-normalize **L50-62**；local-function-lift **L63-73**（`runAfterIds` L70 追加 `partial-merge`）；iterator **L319-330**；eliminate-goto **L331-340**（补边先例 L338）；member-desugar **L341-352**（远端锚先例 L348）；equality-concat L353-367；java-keywords L368-386（多锚先例 L377-385）；ById L389-393；`BuildRegistry` L395-397（`Implemented.Concat(Stubs)` L397） |
| PassChainFactory.cs | `PassChainConfiguration` L38-92（**不加字段**）；`BuildLegacyToggles` L103-133（**不加键**）；`CreateAdapter` L146-384（新臂插 L334 iterator 臂之前）；default throw L382 |
| CliPreprocessChain.cs | `ExecutedIntermediateDirectoryNames` **L105**（中间目录自动登记，**不改**）；stub 拒绝 L179-184（翻转后此路径对本 id 不再触发）；`sourcePath = run.PreprocessedSourcePath` **L233**（不改） |
| ProjectPassRunner.cs | `PrepareTreeAsync` L79-132；`BuildStandardCompilation` L136-155；`RunStandardSpineAsync` L159-252；`CopyVerbatim` L254-259；`WriteRewrittenAsync` L261-270；`Restore` L274-295；`VerifyBuildAndTestsAsync` L299-；`DeleteBuildOutputs` L476 |
| ProjectPassAdapter.cs | 默认 `ExecuteAsync` → spine L268-269；`PassExecutionContext` stage 助手 L79-108；hooks 全集 L117-262 |
| TuplePassAdapter.cs | global using 合成树先例 **L59-88**；`AfterFilesAsync`/DistributeStructFiles L122-127 |
| JavaKeywordsPassAdapter.cs | `ExecuteAsync` **L48-255（镜像母本）**；maxIterations L97；回滚 L170-218；`BuildErrorSet` L333-346；`DiffErrors` L348-361；BOM 判定 L369-375 |
| PassDescriptor.cs | 构造器 L67-102（`needsSemanticModel` 默认 **true** L76）；`PassStage` 枚举 L8-30（枚举注释不动，设计 §13） |
| PassChainOrderTests.cs | `LockedChain` L19-50；`EnabledByDefault` L52-54；顺序断言 L56-74；catalog 映射 L216-234；适配器全覆盖 L236-259（config 实例 L239-251，无需新 factory） |
| CSharpToJava.ProjectTests.csproj | 新 ItemGroup 插 L257 `</Project>` 之前；Content 先例 SampleMemberDesugarSolution L185-192、C28 SampleLexicalNormalizeSolution ItemGroup L248-255 |
| CSharpToJava.Core.csproj | net10.0 / Roslyn 5.0.0 L12；InternalsVisibleTo Tests/ProjectTests/CLI L19-29 |

---

# M1 — 类型合并最小闭环（Tasks 1-12）

**出口（设计 §9）**：设计 §7.1 组 1-3、12-13 通过；fixture 骨架 .NET 半区 build 绿；禁用路径零行为差异；注册表 stub 翻转为 Implemented（默认关）。

## Task 1: Models 与诊断码

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialMergeModels.cs`

- [ ] **Step 1: 写入 Models 文件**

```csharp
namespace CSharpToJava.Core.PartialMerge;

public enum PartialMergeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CS2J20xx diagnostic produced by the partial-merge pass.</summary>
public sealed record PartialMergeDiagnostic(
    PartialMergeSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null,
    int? Column = null,
    string? TypeFullName = null)
{
    public string ToMsBuildString()
    {
        var severity = Severity == PartialMergeSeverity.Error ? "error"
            : Severity == PartialMergeSeverity.Warning ? "warning" : "info";
        var location = FilePath != null
            ? $"{FilePath}({Line ?? 0},{Column ?? 0}): "
            : "";
        return $"{location}{severity} {Code}: {Message}";
    }
}

/// <summary>
/// Options for partial-merge. Only two knobs by design: Strict and (M3) the
/// generator-part assumption. No per-file syntax toggles (design §4.6).
/// </summary>
public sealed record PartialMergeOptions
{
    /// <summary>Unhandled residual multi-part declarations / unattributable new errors fail the run.</summary>
    public bool Strict { get; init; } = true;

    /// <summary>M3 only: allow removing classic partial declarations with no in-source implementation.</summary>
    public bool AssumeNoGeneratedPartial { get; init; } = false;
}

public sealed record PartialMergeStatistics
{
    public required int FilesScanned { get; init; }
    public required int FilesTransformed { get; init; }
    public required int FilesSkippedClean { get; init; }
    public required int FilesDirtyNoRewrite { get; init; }
    public required int ResidualCount { get; init; }
    public required int TypesScanned { get; init; }
    public required int GroupsMerged { get; init; }
    public required int PartsRemoved { get; init; }
    public required int MembersCollapsed { get; init; }
    public required int GroupsRolledBack { get; init; }
    public required int Conflicts { get; init; }
    public required int SkippedBaseline { get; init; }
    public required int SkippedTopLevel { get; init; }
    public required IReadOnlyDictionary<string, int> ErrorsByCode { get; init; }
}

/// <summary>Internal mutable accumulator; Build() yields the immutable statistics record.</summary>
internal sealed class MutablePartialMergeStats
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;
    public int TypesScanned;
    public int GroupsMerged;
    public int PartsRemoved;
    public int MembersCollapsed;
    public int GroupsRolledBack;
    public int Conflicts;
    public int SkippedBaseline;
    public int SkippedTopLevel;

    private readonly Dictionary<string, int> _errorsByCode = new(StringComparer.Ordinal);

    public void AddError(string code)
        => _errorsByCode[code] = _errorsByCode.TryGetValue(code, out var count) ? count + 1 : 1;

    public PartialMergeStatistics Build() => new()
    {
        FilesScanned = FilesScanned,
        FilesTransformed = FilesTransformed,
        FilesSkippedClean = FilesSkippedClean,
        FilesDirtyNoRewrite = FilesDirtyNoRewrite,
        ResidualCount = ResidualCount,
        TypesScanned = TypesScanned,
        GroupsMerged = GroupsMerged,
        PartsRemoved = PartsRemoved,
        MembersCollapsed = MembersCollapsed,
        GroupsRolledBack = GroupsRolledBack,
        Conflicts = Conflicts,
        SkippedBaseline = SkippedBaseline,
        SkippedTopLevel = SkippedTopLevel,
        ErrorsByCode = new Dictionary<string, int>(_errorsByCode, StringComparer.Ordinal),
    };
}

/// <summary>CS2J20xx code constants (design §4.6 / §6).</summary>
internal static class PartialMergeCodes
{
    public const string GroupRolledBack = "CS2J2001";
    public const string WholeTreeRolledBack = "CS2J2002";
    public const string BaseTypeConflict = "CS2J2010";
    public const string TypeParameterMismatch = "CS2J2011";
    public const string ModifierConflict = "CS2J2012";
    public const string DuplicateMember = "CS2J2013";
    public const string AttributeConflict = "CS2J2014";
    public const string ExtendedPartialMissingImpl = "CS2J2015";
    public const string UnsupportedShape = "CS2J2016";
    public const string AnalyzerSeen = "CS2J2017";
    public const string Residual = "CS2J2018";
    public const string BuildFailed = "CS2J2030";
    public const string TestsFailed = "CS2J2031";
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`（警告可既有）。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMergeModels.cs
git commit -m "feat(preprocessing): C20 partial-merge Models/Options/Statistics/诊断码（M1）"
```

## Task 2: Plan 记录

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialMergePlan.cs`

- [ ] **Step 1: 写入 Plan 文件**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>One part declaration within a partial type group.</summary>
internal sealed record TypePartInfo
{
    public required TypeDeclarationSyntax Declaration { get; init; }
    public required string SourceFilePath { get; init; }
    public required string RelativePath { get; init; }
    public required int Ordinal { get; init; }
}

/// <summary>Analysis/merge state for one partial type group (plan atom, design D9).</summary>
internal sealed record TypeGroupPlan
{
    public required string TypeFullName { get; init; }
    public required INamedTypeSymbol Symbol { get; init; }
    public required IReadOnlyList<TypePartInfo> Parts { get; init; }

    /// <summary>Deterministic primary part: relative-path ordinal order, then smallest span (D1).</summary>
    public TypePartInfo Primary => Parts[0];

    /// <summary>Assigned by MergeBuilder; null when the group is skipped/rolled back.</summary>
    public TypeDeclarationSyntax? MergedDeclaration { get; set; }

    public bool SkipBaseline { get; set; }
    public bool SkipUnsupportedShape { get; set; }
    public bool RolledBack { get; set; }

    public IEnumerable<string> TouchedFiles =>
        Parts.Select(part => part.SourceFilePath).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One deterministic text edit; Span is the declaration FullSpan in the source file.</summary>
internal abstract record FileEdit
{
    public required string FilePath { get; init; }
    public required TextSpan Span { get; init; }
}

/// <summary>Primary part: replace the whole declaration (incl. leading trivia) with the merged declaration.</summary>
internal sealed record ReplaceTypeEdit : FileEdit
{
    public required string Replacement { get; init; }
}

/// <summary>Non-primary part: remove the declaration and leave the single-line provenance comment (D1).</summary>
internal sealed record RemoveTypeEdit : FileEdit
{
    public required string Comment { get; init; }
}

/// <summary>Whole-tree analysis result handed to the rewriter/adapter.</summary>
internal sealed record PartialMergePlan
{
    public required IReadOnlyList<TypeGroupPlan> Groups { get; init; }
    public required ILookup<string, FileEdit> EditsByFile { get; init; }
    public required IReadOnlyList<PartialMergeDiagnostic> Diagnostics { get; init; }

    public IEnumerable<TypeGroupPlan> ActiveGroups =>
        Groups.Where(g => !g.SkipBaseline && !g.SkipUnsupportedShape && !g.RolledBack);
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMergePlan.cs
git commit -m "feat(preprocessing): C20 partial-merge Plan/TypeGroup/FileEdit 记录（M1）"
```

## Task 3: PartialTypePartCollector（符号分组 + 主部件规则）

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialTypePartCollector.cs`

- [ ] **Step 1: 写入 Collector**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Symbol-driven collection of multi-part type declarations (design §4.1). Walks
/// GlobalNamespace recursively; only types with ≥2 source DeclaringSyntaxReferences
/// become groups. Single-part types cost nothing; top-level-statements Program is
/// skipped (design D8).
/// </summary>
internal static class PartialTypePartCollector
{
    public static (List<TypeGroupPlan> Groups, int TopLevelSkipped) Collect(
        CSharpCompilation compilation,
        string sourceRoot)
    {
        var groups = new List<TypeGroupPlan>();
        var topLevelSkipped = 0;

        VisitNamespace(compilation.GlobalNamespace);

        return (groups, topLevelSkipped);

        void VisitNamespace(INamespaceSymbol namespaceSymbol)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
            {
                VisitType(type);
            }

            foreach (var nested in namespaceSymbol.GetNamespaceMembers())
            {
                VisitNamespace(nested);
            }
        }

        void VisitType(INamedTypeSymbol type)
        {
            foreach (var nestedType in type.GetTypeMembers())
            {
                VisitType(nestedType);
            }

            if (type.IsTopLevelStatementsEntryPointType())
            {
                topLevelSkipped++;
                return;
            }

            // Only real source trees under the source root participate; the
            // synthesized global-usings tree (synthetic path) is excluded.
            var sourceReferences = type.DeclaringSyntaxReferences
                .Where(reference =>
                    reference.SyntaxTree.FilePath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)
                    && !reference.SyntaxTree.FilePath.Contains("__cs2j", StringComparison.Ordinal))
                .ToList();

            if (sourceReferences.Count < 2)
            {
                return;
            }

            // D1 two-key determinism: relative path ordinal asc, then span.Start asc.
            var orderedParts = sourceReferences
                .Select(reference =>
                {
                    var node = (TypeDeclarationSyntax)reference.GetSyntax();
                    return new
                    {
                        Node = node,
                        FilePath = reference.SyntaxTree.FilePath,
                        RelativePath = Path.GetRelativePath(sourceRoot, reference.SyntaxTree.FilePath)
                            .Replace('\\', '/'),
                    };
                })
                .OrderBy(part => part.RelativePath, StringComparer.Ordinal)
                .ThenBy(part => part.Node.SpanStart)
                .Select((part, index) => new TypePartInfo
                {
                    Declaration = part.Node,
                    SourceFilePath = part.FilePath,
                    RelativePath = part.RelativePath,
                    Ordinal = index,
                })
                .ToList();

            groups.Add(new TypeGroupPlan
            {
                TypeFullName = type.ToDisplayString(),
                Symbol = type,
                Parts = orderedParts,
            });
        }
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialTypePartCollector.cs
git commit -m "feat(preprocessing): C20 partial-merge PartCollector 符号分组与主部件规则（M1）"
```

## Task 4: PartialMemberMerge（M1：部件序拼接）

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialMemberMerge.cs`

- [ ] **Step 1: 写入 MemberMerge**

```csharp
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Member collection and (M3) partial-pair collapse (design §4.5). M1 policy:
/// concatenate members in "part order × declaration order" with NO text-key
/// dedup — the legacy MergedTypeDeclaration text-key behavior (int vs
/// System.Int32 mismatch, first-wins) is deliberately not revived. Partial
/// method/property pairs therefore survive as two members in v1.
/// </summary>
internal static class PartialMemberMerge
{
    public static IEnumerable<MemberDeclarationSyntax> CollectMembers(TypeGroupPlan group)
    {
        foreach (var part in group.Parts)
        {
            foreach (var member in part.Declaration.Members)
            {
                // Nested partial types that are themselves groups are replaced by
                // their merged node (lookup supplied by the analyzer, depth-first).
                if (NestedOverrides.TryGetValue(member.ToFullString().Trim(), out var replacement))
                {
                    yield return replacement;
                }
                else
                {
                    yield return member;
                }
            }
        }
    }

    /// <summary>
    /// Keyed (for M1) by the nested declaration's trimmed full text; analyzer fills
    /// it depth-first. If collisions appear in real fixtures, switch to an
    /// annotation-based key before M2.
    /// </summary>
    public static Dictionary<string, TypeDeclarationSyntax> NestedOverrides { get; }
        = new(StringComparer.Ordinal);
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMemberMerge.cs
git commit -m "feat(preprocessing): C20 partial-merge 成员部件序拼接（M1，零文本去重）"
```

## Task 5: PartialTypeMergeBuilder（合并声明构造）

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialTypeMergeBuilder.cs`

- [ ] **Step 1: 写入 MergeBuilder**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Builds the merged declaration on the primary part node (design §4.2):
/// kind/record parameter list stay primary's; modifier union in canonical order;
/// semantic base/interface dedup; attribute concat; constraint union; members in
/// part order; the partial modifier is KEPT (design D2).
/// </summary>
internal static class PartialTypeMergeBuilder
{
    // Canonical type-modifier order; readonly precedes partial (CS1585).
    private static readonly SyntaxKind[] ModifierOrder =
    [
        SyntaxKind.PublicKeyword,
        SyntaxKind.PrivateKeyword,
        SyntaxKind.ProtectedKeyword,
        SyntaxKind.InternalKeyword,
        SyntaxKind.ProtectedOrInternalKeyword,
        SyntaxKind.PrivateProtectedKeyword,
        SyntaxKind.NewKeyword,
        SyntaxKind.StaticKeyword,
        SyntaxKind.UnsafeKeyword,
        SyntaxKind.AbstractKeyword,
        SyntaxKind.SealedKeyword,
        SyntaxKind.ReadOnlyKeyword,
        SyntaxKind.VirtualKeyword,
        SyntaxKind.PartialKeyword,
    ];

    public static TypeDeclarationSyntax Build(CSharpCompilation compilation, TypeGroupPlan group)
    {
        var primary = group.Primary.Declaration;

        var merged = primary
            .WithMembers(SyntaxFactory.List(PartialMemberMerge.CollectMembers(group)))
            .WithModifiers(BuildMergedModifiers(group))
            .WithBaseList(BuildMergedBaseList(compilation, group))
            .WithAttributeLists(BuildMergedAttributes(group))
            .WithConstraintClauses(BuildMergedConstraints(group))
            .WithTypeParameterList(primary.TypeParameterList);

        // Deterministic provenance banner on the merged declaration.
        var banner = SyntaxFactory.Comment(
            "// cs2j-partial-merge: merged from " + group.Parts.Count + " part(s): " +
            string.Join(", ", group.Parts.Select(part => part.RelativePath)));

        var leading = primary.GetLeadingTrivia();
        return merged.WithLeadingTrivia(leading.InsertRange(0,
        [
            banner,
            SyntaxFactory.CarriageReturnLineFeed,
        ]));
    }

    private static SyntaxTokenList BuildMergedModifiers(TypeGroupPlan group)
    {
        var kinds = group.Parts
            .SelectMany(part => part.Declaration.Modifiers)
            .Select(token => token.Kind())
            .Distinct()
            .OrderBy(ModifierOrder.IndexOf)
            .Select(SyntaxFactory.Token)
            .ToList();

        return SyntaxFactory.TokenList(kinds);
    }

    private static BaseListSyntax? BuildMergedBaseList(CSharpCompilation compilation, TypeGroupPlan group)
    {
        var seenTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var bases = new List<BaseTypeSyntax>();

        foreach (var part in group.Parts)
        {
            if (part.Declaration.BaseList is null)
            {                continue;
            }

            var semanticModel = compilation.GetSemanticModel(part.Declaration.SyntaxTree);
            foreach (var baseType in part.Declaration.BaseList.Types)
            {
                if (semanticModel.GetSymbolInfo(baseType.Type).Symbol is not INamedTypeSymbol baseSymbol)
                {
                    // Unresolved in source: keep syntax verbatim; candidate diff is the backstop.
                    bases.Add(baseType);
                    continue;
                }

                // object implicit base never appears in syntax; dedup interfaces and
                // same-symbol bases by semantic identity (IEnumerable<int> == full name).
                if (!seenTypes.Add(baseSymbol))
                {
                    continue;
                }

                bases.Add(baseType);
            }
        }

        return bases.Count == 0 ? null : SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(bases));
    }

    private static SyntaxList<AttributeListSyntax> BuildMergedAttributes(TypeGroupPlan group)
        => SyntaxFactory.List(group.Parts.SelectMany(part => part.Declaration.AttributeLists));

    private static SyntaxList<TypeParameterConstraintClauseSyntax> BuildMergedConstraints(
        TypeGroupPlan group)
    {
        // M1: primary's clauses first, then clauses for params not already covered;
        // same-name constraint union is refined in M2 against symbol identities.
        var primary = group.Primary.Declaration;
        var clauses = new List<TypeParameterConstraintClauseSyntax>(primary.ConstraintClauses);
        var covered = new HashSet<string>(
            clauses.Select(clause => clause.Name.ToString()), StringComparer.Ordinal);

        foreach (var part in group.Parts.Skip(1))
        {
            foreach (var clause in part.Declaration.ConstraintClauses)
            {
                if (covered.Add(clause.Name.ToString()))
                {
                    clauses.Add(clause);
                }
            }
        }

        return SyntaxFactory.List(clauses);
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialTypeMergeBuilder.cs
git commit -m "feat(preprocessing): C20 partial-merge MergeBuilder 合并声明构造（M1）"
```

## Task 6: PartialMergeRewriter（逐文件确定性编辑）

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialMergeRewriter.cs`

- [ ] **Step 1: 写入 Rewriter**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Applies a file's edits as deterministic TextChanges (design §4.2 point 9):
/// edits are ordered by descending span so earlier offsets stay valid; removal
/// leaves exactly one LF-terminated provenance comment. No timestamps/GUIDs are
/// ever emitted.
/// </summary>
internal sealed class PartialMergeRewriter
{
    public string Apply(string source, IReadOnlyList<FileEdit> edits)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));

        var changes = edits
            .OrderByDescending(edit => edit.Span.Start)
            .Select(edit => new TextChange(
                new TextSpan(edit.Span.Start, edit.Span.Length),
                edit switch
                {
                    ReplaceTypeEdit replace => replace.Replacement,
                    RemoveTypeEdit remove => remove.Comment + "\n",
                    _ => throw new InvalidOperationException(
                        $"Unknown edit kind: {edit.GetType().Name}"),
                }))
            .ToList();

        return syntaxTree.GetText().WithChanges(changes).ToString();
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMergeRewriter.cs
git commit -m "feat(preprocessing): C20 partial-merge Rewriter 逐文件确定性编辑（M1）"
```

## Task 7: PartialMergeAnalyzer（M1 版分析）

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialMergeAnalyzer.cs`

- [ ] **Step 1: 写入 Analyzer**

```csharp
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Whole-tree analysis → PartialMergePlan (design §4.1). M1: collect groups,
/// build merged declarations depth-first (so nested merged nodes exist when the
/// containing type is built), emit one Replace edit for the primary and one
/// Remove edit per other part. Baseline snapshot/skip, CS2J2016 shape gates and
/// recompile-diff attribution arrive in M2.
/// </summary>
internal sealed class PartialMergeAnalyzer
{
    public PartialMergePlan Analyze(
        CSharpCompilation compilation,
        PartialMergeOptions options,
        string sourceRoot)
    {
        var diagnostics = new List<PartialMergeDiagnostic>();
        var (groups, topLevelSkipped) = PartialTypePartCollector.Collect(compilation, sourceRoot);

        // Build deepest-first; after a group is built, register each part's text
        // as an alias for its merged node so containing types (built afterwards)
        // replace the nested part members. Switch to annotation keys if real
        // fixtures produce text-key collisions.
        foreach (var group in groups
                     .OrderByDescending(g => g.TypeFullName.Count(c => c == '.')))
        {
            var merged = PartialTypeMergeBuilder.Build(compilation, group);
            group.MergedDeclaration = merged;

            foreach (var part in group.Parts)
            {
                PartialMemberMerge.NestedOverrides.TryAdd(
                    part.Declaration.ToFullString().Trim(), merged);
            }
        }

        var edits = new List<FileEdit>();
        foreach (var group in groups)
        {
            if (group.MergedDeclaration is null)
            {
                continue;
            }

            var primary = group.Primary;
            edits.Add(new ReplaceTypeEdit
            {
                FilePath = primary.SourceFilePath,
                Span = primary.Declaration.FullSpan,
                Replacement = group.MergedDeclaration.ToFullString(),
            });

            foreach (var otherPart in group.Parts.Skip(1))
            {
                edits.Add(new RemoveTypeEdit
                {
                    FilePath = otherPart.SourceFilePath,
                    Span = otherPart.Declaration.FullSpan,
                    Comment = "// cs2j-partial-merge: part of '" + group.TypeFullName +
                              "' merged into '" + primary.RelativePath + "'",
                });
            }
        }

        return new PartialMergePlan
        {
            Groups = groups,
            EditsByFile = edits.ToLookup(edit => edit.FilePath, StringComparer.OrdinalIgnoreCase),
            Diagnostics = diagnostics,
        };
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMergeAnalyzer.cs
git commit -m "feat(preprocessing): C20 partial-merge Analyzer 整树分析产 plan（M1）"
```

## Task 8: PartialMergePreprocessor（门面）

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialMergePreprocessor.cs`

- [ ] **Step 1: 写入门面**

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Public facade used by the CLI adapter: Analyze once, RewriteFile per file,
/// CheckResidual after the candidate tree is built (design §4.1 / §6).
/// </summary>
public sealed class PartialMergePreprocessor
{
    private readonly PartialMergeOptions _options;

    public PartialMergePreprocessor(PartialMergeOptions? options = null)
    {
        _options = options ?? new PartialMergeOptions();
    }

    public PartialMergePlan Analyze(CSharpCompilation compilation, string sourceRoot)
        => new PartialMergeAnalyzer().Analyze(compilation, _options, sourceRoot);

    public string RewriteFile(string source, IReadOnlyList<FileEdit> edits)
        => new PartialMergeRewriter().Apply(source, edits);

    /// <summary>
    /// Residual gate (design D2/§6): every multi-part source type must belong to a
    /// rolled-back/skipped group. The partial token itself is never residual.
    /// </summary>
    public IReadOnlyList<PartialMergeDiagnostic> CheckResidual(
        CSharpCompilation candidate,
        PartialMergePlan plan)
    {
        var whitelist = new HashSet<string>(
            plan.Groups
                .Where(g => g.RolledBack || g.SkipBaseline || g.SkipUnsupportedShape)
                .Select(g => g.TypeFullName),
            StringComparer.Ordinal);

        var diagnostics = new List<PartialMergeDiagnostic>();
        foreach (var type in candidate.GlobalNamespace.GetAllTypes(candidate))
        {
            if (type.DeclaringSyntaxReferences.Length < 2 || whitelist.Contains(type.ToDisplayString()))
            {
                continue;
            }

            var severity = _options.Strict ? PartialMergeSeverity.Error : PartialMergeSeverity.Warning;
            diagnostics.Add(new PartialMergeDiagnostic(
                severity,
                PartialMergeCodes.Residual,
                "residual multi-part type '" + type.ToDisplayString() +
                "' outside the rollback/skip whitelist",
                TypeFullName: type.ToDisplayString()));
        }

        return diagnostics;
    }
}

internal static class PartialMergeSymbolExtensions
{
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(
        this INamespaceSymbol root,
        Compilation compilation)
    {
        foreach (var type in root.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in GetNestedTypes(type))
            {
                yield return nested;
            }
        }

        foreach (var nestedNamespace in root.GetNamespaceMembers())
        {
            foreach (var type in nestedNamespace.GetAllTypes(compilation))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in GetNestedTypes(nested))
            {
                yield return deeper;
            }
        }
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMergePreprocessor.cs
git commit -m "feat(preprocessing): C20 partial-merge Preprocessor 门面与 residual 检查（M1）"
```

## Task 9: Core 单测（组 1-3、12-13）

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/PartialMergeUnitTests.cs`

- [ ] **Step 1: 先写失败测试**（M1 矩阵，设计 §7.1 组 1-3、12-13）

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.PartialMerge;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// M1 unit matrix (design §7.1 groups 1-3, 12-13): inline syntax trees →
/// CSharpCompilation → Analyze/RewriteFile text assertions; candidate tree must
/// compile with zero errors.
/// </summary>
public sealed class PartialMergeUnitTests
{
    private static (CSharpCompilation Compilation, PartialMergePlan Plan, string Root) BuildPlan(
        params (string Path, string Source)[] files)
    {
        var root = CrossPlatformRoot(files);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(
            f.Source, new CSharpParseOptions(LanguageVersion.Latest), path: Path.Combine(root, f.Path)));
        var compilation = CSharpCompilation.Create(
            "partial-merge-tests", trees,
            ProjectBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var plan = new PartialMergePreprocessor().Analyze(compilation, root);
        return (compilation, plan, root);
    }

    [Fact]
    public void Group1_Two_File_Class_Members_Union_And_Primary_Determinism()
    {
        var (_, plan, _) = BuildPlan(
            ("Alpha/Part1.cs", "namespace Demo; public partial class C { public void A() {} }"),
            ("Beta/Part2.cs", "namespace Demo; public partial class C { public void B() {} }"));

        var group = Assert.Single(plan.Groups);
        Assert.Equal("Demo.C", group.TypeFullName);
        Assert.Equal("Alpha/Part1.cs", group.Primary.RelativePath);
        Assert.Equal(2, group.Parts.Count);
        Assert.Contains(group.MergedDeclaration!.Members, m => m.ToString().Contains("A()"));
        Assert.Contains(group.MergedDeclaration!.Members, m => m.ToString().Contains("B()"));
    }

    [Fact]
    public void Group1_Same_File_Smallest_Span_Wins_And_Header_Usings_Preserved()
    {
        var source =
            "using System; namespace Demo { public partial class C { void A(){} } " +
            "public partial class C { void B(){} } }";
        var (_, plan, _) = BuildPlan(("Same.cs", source));

        var group = Assert.Single(plan.Groups);
        Assert.True(group.Primary.Declaration.SpanStart < group.Parts[1].Declaration.SpanStart);
        var rewritten = new PartialMergeRewriter().Apply(source,
            plan.EditsByFile[Path.Combine(BuildRootPlaceholder(), "Same.cs")].ToList());
        Assert.StartsWith("using System;", rewritten);
    }

    [Fact]
    public void Group2_Three_Parts_MultiGroup_Shared_File_And_Nested_Partials()
    {
        var (compilation, plan, root) = BuildPlan(
            ("a1.cs", "namespace Demo; public partial class A { void X(){} }"),
            ("a2.cs", "namespace Demo; public partial class A { void Y(){} public partial class N { void P(){} } }"),
            ("a3.cs", "namespace Demo; public partial class A { void Z(){} public partial class N { void Q(){} } }"));

        var group = Assert.Single(plan.Groups);
        Assert.Equal(3, group.Parts.Count);

        // Apply all file edits; candidate text must compile and contain X/Y/Z and P/Q.
        var outputs = ApplyAll(plan, root, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var allText = string.Join("\n", outputs.Select(o => o.Source));
        Assert.Contains("X()", allText);
        Assert.Contains("Y()", allText);
        Assert.Contains("Z()", allText);
        Assert.Contains("P()", allText);
        Assert.Contains("Q()", allText);
    }

    [Fact]
    public void Group3_Struct_Interface_Record_Generic_And_FileScopedNamespace()
    {
        var (compilation, plan, root) = BuildPlan(
            ("s1.cs", "namespace Demo; public ref partial struct S { int _a; }"),
            ("s2.cs", "namespace Demo; public ref partial struct S { void M(){} }"),
            ("i1.cs", "namespace Demo; public partial interface I { void One(); }"),
            ("i2.cs", "namespace Demo; public partial interface I { void Two(); }"),
            ("r1.cs", "namespace Demo; public partial record class R(int Id);"),
            ("r2.cs", "namespace Demo; public partial record class R { string? Name { get; } }"),
            ("g1.cs", "namespace Demo; public partial class G<T> { void A(T t){} }"),
            ("g2.cs", "namespace Demo; public partial class G<T> { void B(){} }"),
            ("f1.cs", "namespace Demo; public partial class F { }"),
            ("f2.cs", "namespace Demo; public partial class F { }"));

        Assert.Equal(5, plan.Groups.Count);
        var outputs = ApplyAll(plan, root, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group12_Idempotent_Second_Run_Has_No_Edits()
    {
        var (compilation, plan, root) = BuildPlan(
            ("p1.cs", "namespace Demo; public partial class C { void A(){} }"),
            ("p2.cs", "namespace Demo; public partial class C { void B(){} }"));

        var outputs = ApplyAll(plan, root, compilation);
        var secondTrees = outputs.Select(o => CSharpSyntaxTree.ParseText(
            o.Source, new CSharpParseOptions(LanguageVersion.Latest), path: o.Path));
        var secondCompilation = CSharpCompilation.Create(
            "partial-merge-idem", secondTrees, ProjectBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var secondPlan = new PartialMergePreprocessor().Analyze(secondCompilation, root);

        Assert.Empty(secondPlan.EditsByFile.SelectMany(g => g));
    }

    [Fact]
    public void Group13_Residual_Count_And_Partial_Token_Kept()
    {
        var (compilation, plan, root) = BuildPlan(
            ("p1.cs", "namespace Demo; public partial class C { void A(){} }"),
            ("p2.cs", "namespace Demo; public partial class C { void B(){} }"));

        var outputs = ApplyAll(plan, root, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(new PartialMergePreprocessor().CheckResidual(candidate, plan));
        var mergedFile = outputs.Single(o => o.Source.Contains("cs2j-partial-merge: merged"));
        Assert.Contains("partial class C", mergedFile.Source);
    }

    // ---- helpers: keep in this file; mirror ProjectTests conventions ----
    private static List<MetadataReference> ProjectBasicReferences() => /* Basic .NET refs */
        ProjectUnsignedTypePreprocessor.GetBasicReferences().ToList();

    private static string CrossPlatformRoot((string Path, string Source)[] files) =>
        BuildRootPlaceholder();

    private static string BuildRootPlaceholder() =>
        OperatingSystem.IsWindows() ? "D:\\cs2j-partial-merge-test" : "/tmp/cs2j-partial-merge-test";

    private static List<(string Path, string Source)> ApplyAll(
        PartialMergePlan plan, string root, CSharpCompilation original)
    {
        var rewriter = new PartialMergeRewriter();
        return original.SyntaxTrees
            .Where(t => t.FilePath.StartsWith(root))
            .Select(t =>
            {
                var edits = plan.EditsByFile[t.FilePath].ToList();
                var source = t.ToString();
                return (t.FilePath, edits.Count == 0 ? source : rewriter.Apply(source, edits));
            })
            .ToList();
    }

    private static CSharpCompilation Recompile(List<(string Path, string Source)> outputs)
        => CSharpCompilation.Create(
            "partial-merge-candidate",
            outputs.Select(o => CSharpSyntaxTree.ParseText(
                o.Source, new CSharpParseOptions(LanguageVersion.Latest), path: o.Path)),
            ProjectBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
}
```

注意：上面的 helper（`CrossPlatformRoot`/路径拼装）实施时允许就地调整为与测试文件真实临时根一致的写法，但**测试名称与断言意图不得删减**。

- [ ] **Step 2: 跑测试（期望全绿）**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter PartialMergeUnitTests --nologo`
Expected: `失败: 0`。

- [ ] **Step 3: 提交**

```powershell
git add tests/CSharpToJava.ProjectTests/PartialMergeUnitTests.cs
git commit -m "test(preprocessing): C20 partial-merge M1 组1-3/12-13 Core 单测"
```

## Task 10: PartialMergePassAdapter（M1：复制→分析→改写→restore/verify）

**Files:**
- Create: `src/CSharpToJava.CLI/Preprocessing/Adapters/PartialMergePassAdapter.cs`

- [ ] **Step 1: 写入适配器（M1 版）**

```csharp
using CSharpToJava.Core.PartialMerge;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for partial-merge (C20). Drives its own whole-tree execution like
/// JavaKeywordsPassAdapter (design §5): prepare/copy → whole-tree semantic
/// analysis → per-file deterministic rewrites → restore → build/test verify.
/// M1 has no rollback replay (M2 adds baseline/diff/rollback/residual).
/// </summary>
internal sealed class PartialMergePassAdapter : ProjectPassAdapter
{
    private readonly PartialMergeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly List<string> _diagnostics = new();

    public PartialMergePassAdapter(
        PartialMergeOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.PartialMerge);

    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest, preprocessorSymbols: new[] { "NETCOREAPP" });

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;

        // 1. Explicit 1:1 copy of .cs inputs and non-.cs assets (mirror
        // JavaKeywords L56-85: intermediate input graph can be empty).
        CopyInputs(tree);

        // 2. Whole-tree semantic compilation and analysis.
        var compilation = context.BuildStandardCompilation();
        var preprocessor = new PartialMergePreprocessor(_options);
        var plan = preprocessor.Analyze(compilation, tree.SourceRoot);

        foreach (var diagnostic in plan.Diagnostics)
        {
            _diagnostics.Add(diagnostic.ToMsBuildString());
        }

        // 3. Per-file rewrite (edits) or verbatim copy (no edits).
        var rewriter = new PartialMergeRewriter();
        foreach (var sourceFile in tree.SourceFiles)
        {
            var relativePath = Path.GetRelativePath(tree.SourceRoot, sourceFile).Replace('\\', '/');
            var edits = plan.EditsByFile[sourceFile].ToList();
            if (edits.Count == 0)
            {
                context.CopyVerbatim(sourceFile, relativePath);
                continue;
            }

            var source = await File.ReadAllTextAsync(sourceFile);
            var output = rewriter.Apply(source, edits);
            var fileContext = new PassFileContext
            {
                SourceCode = source,
                SourceFilePath = sourceFile,
                RelativePath = relativePath,
                DestinationFilePath = Path.Combine(tree.IntermediateRoot, relativePath),
            };
            await context.WriteRewrittenAsync(fileContext, new PassFileRewrite
            {
                OutputCode = output,
                Changed = true,
                PreserveBom = true,
            });

            if (_verbose)
            {
                Console.WriteLine($"[partial-merge] transformed: {relativePath}");
            }
        }

        // M1: no candidate recompile / rollback replay — added in Task 13.

        // 4. Restore + build/test double verify (descriptor defaults both on).
        context.Restore();
        var verified = await context.VerifyBuildAndTestsAsync();
        context.Success = verified && !HasErrors;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("PartialMergePassAdapter drives the file loop itself.");

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(
            $"[Warning] dotnet restore failed with exit code {exitCode} for {restoreTarget}.");

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(
            $"[Error] {PartialMergeCodes.BuildFailed}: dotnet build failed with exit code {exitCode} after partial-merge.");

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(
            $"[Error] {PartialMergeCodes.TestsFailed}: dotnet test failed with exit code {exitCode} after partial-merge.");

    public override bool HasErrors
        => _diagnostics.Any(d => d.Contains("error CS2J", StringComparison.OrdinalIgnoreCase)
                                 || d.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase));

    public override string BuildChainSummaryLine()
        => "partial-merge: see verbose diagnostics";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => verbose ? _diagnostics.ToList() : _diagnostics;

    private static void CopyInputs(PassTreeContext tree)
    {
        foreach (var csFile in tree.CsFiles)
        {
            var relativePath = Path.GetRelativePath(tree.SourceRoot, csFile);
            var destination = Path.Combine(tree.IntermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(csFile, destination, overwrite: true);
        }

        foreach (var asset in tree.SourceFiles)
        {
            if (asset.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || asset.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(tree.SourceRoot, asset);
            var destination = Path.Combine(tree.IntermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(asset, destination, overwrite: true);
        }
    }
}
```

- [ ] **Step 2: 编译确认（此时 CreateAdapter 尚未接线）**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.CLI/Preprocessing/Adapters/PartialMergePassAdapter.cs
git commit -m "feat(preprocessing): C20 partial-merge CLI 适配器 M1（复制分析改写双verify）"
```

## Task 11: 注册表翻转与排序边

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs`

- [ ] **Step 1: PassStubCatalog —— 删 stub 条目、留常量**

删除整个 partial-merge 条目块（当前 L51-61：从 `new(` 到 `catalogItems: "C20"),`），原位替换为注释：

```csharp
        // Ids.PartialMerge flipped to Implemented in PassCatalog (C20);
        // the id constant stays here by the iterator-lowering precedent (§3.4).
```

`Ids.PartialMerge`（L23）**保留不动**。

- [ ] **Step 2: PassCatalog —— lexical-normalize 之后插入描述符**

在 lexical-normalize 描述符块（L50-62）结束之后、local-function-lift（L63）之前插入：

```csharp
        new(
            PassStubCatalog.Ids.PartialMerge,
            "Partial type merge",
            ".cs2j-partial-merge-src",
            PassStage.Lexical,
            PassStatus.Implemented,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Conditional, PassStubCatalog.Ids.LexicalNormalize },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Merge partial type declarations across the tree before the semantic passes.",
            catalogItems: "C20"),
```

- [ ] **Step 3: PassCatalog —— local-function-lift 追加 RunAfter 边**

把 local-function-lift 的 `runAfterIds`（当前 L70）由

```csharp
            runAfterIds: new[] { PassIds.Conditional },
```

改为

```csharp
            runAfterIds: new[] { PassIds.Conditional, PassStubCatalog.Ids.PartialMerge },
```

（设计 §3.3：仿 eliminate-goto 补 iterator 边先例 L338；partial-merge 默认关时启用子集过滤消解该边，local-function-lift 不被阻塞。）

- [ ] **Step 4: PassChainFactory —— CreateAdapter 加臂（不动 config/toggles）**

在 iterator-lowering 臂（L334-341）之前插入：

```csharp
            PassStubCatalog.Ids.PartialMerge => new PartialMergePassAdapter(
                new PartialMergeOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),
```

文件顶部 using 区加：

```csharp
using CSharpToJava.Core.PartialMerge;
```

`PassChainConfiguration`（L38-92）与 `BuildLegacyToggles`（L103-133）**均不动**（无专属开关、无 options factory，构造形态照 iterator 臂 L334-335）。

- [ ] **Step 5: 构建确认**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 6: 提交（逐文件 add）**

```powershell
git add src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs
git add src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs
git add src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs
git commit -m "feat(preprocessing): C20 partial-merge 翻转 Implemented（默认关、语义双verify、排序边）"
```

## Task 12: 链序测试翻转 + M1 里程碑全量验证

**Files:**
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs`

- [ ] **Step 1: LockedChain 插入 partial-merge**

在 `LockedChain` 的 lexical-normalize（L22）之后、local-function-lift（L23）之前插入一行：

```csharp
        PassStubCatalog.Ids.PartialMerge,
```

- [ ] **Step 2: EnabledByDefault 追加排除**

把 L52-54

```csharp
    // lexical-normalize lands Implemented/default-off (M3 gate flips it on).
    private static readonly string[] EnabledByDefault =
        LockedChain.Where(id => id != PassStubCatalog.Ids.LexicalNormalize).ToArray();
```

改为

```csharp
    // lexical-normalize and partial-merge land Implemented/default-off (gated).
    private static readonly string[] EnabledByDefault =
        LockedChain.Where(id =>
            id != PassStubCatalog.Ids.LexicalNormalize
            && id != PassStubCatalog.Ids.PartialMerge).ToArray();
```

- [ ] **Step 3: 顺序断言扩展（设计 §3.3）**

把 `Registry_order_matches_the_locked_historical_chain` 中 L67-73 的断言块追加为：

```csharp
        var ordered = registry.Ordered.Where(p => p.Status == PassStatus.Implemented).ToList();
        Assert.True(
            ordered.FindIndex(p => p.Id == PassIds.Conditional)
                < ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.LexicalNormalize)
                && ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.LexicalNormalize)
                < ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.PartialMerge)
                && ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.PartialMerge)
                < ordered.FindIndex(p => p.Id == PassIds.LocalFunctionLift),
            "expected conditional < lexical-normalize < partial-merge < local-function-lift");

        Assert.True(
            ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.PartialMerge)
                < ordered.FindIndex(p => p.Id == PassIds.MemberDesugar),
            "expected partial-merge < member-desugar (catalog: C46 after C20)");
```

- [ ] **Step 4: catalog 映射断言补 C20**

在 `Implemented_catalog_passes_carry_their_catalog_item_mappings`（L216-234）末尾加：

```csharp
        Assert.Equal("C20", byId[PassStubCatalog.Ids.PartialMerge].CatalogItems);
        Assert.False(byId[PassStubCatalog.Ids.PartialMerge].DefaultEnabled);
```

（适配器全覆盖用例 L236-259 **无需改动**：新臂不依赖 config factory。）

- [ ] **Step 5: 跑链序与 stub 测试**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter PassChainOrderTests --nologo`
Expected: `失败: 0`。

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter PassStubCatalogTests --nologo`
Expected: `失败: 0`（stub 计数动态化，翻转后自动为 13 条）。

- [ ] **Step 6: 提交**

```powershell
git add tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs
git commit -m "test(preprocessing): C20 partial-merge 链序/默认关/catalog 断言翻转"
```

- [ ] **Step 7: M1 里程碑全量验证**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q`
Expected: `0 个错误`。

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo`
Expected: `失败: 0`。

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter PartialMergeUnitTests --nologo`
Expected: `失败: 0`。

Run: `dotnet src/CSharpToJava.CLI/bin/Release/net10.0/CSharpToJava.CLI.dll preprocess --list-passes`
Expected: `partial-merge  Lexical  Implemented  off  conditional, lexical-normalize  rules=C20`；CS2J9001 不再出现于该 id。

---

# M2 — 回滚/冲突/文档 + 完整闸门（Tasks 13-21）

**出口（设计 §9）**：设计 §7.1 组 6-11、14-15 全绿；CS2J2001/2002/2018 用例齐；fixture Release build+test 双绿；sdk partial 命中率清单成文。

## Task 13: 基线快照 + recompile-diff + 回滚重放（ExecuteAsync 升级）

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/PartialMergePassAdapter.cs`

- [ ] **Step 1: 用下方完整版替换 M1 的 `ExecuteAsync`**

```csharp
    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;

        // 1. Explicit 1:1 copy; baseline error set reads ORIGINAL content at dest paths.
        CopyInputs(tree);
        var baseline = BuildErrorSet(tree);

        var preprocessor = new PartialMergePreprocessor(_options);
        var excludedGroups = new HashSet<string>(StringComparer.Ordinal);
        PartialMergePlan? acceptedPlan = null;
        var success = true;
        const int maxIterations = 1024;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // 2. Every round starts from originals (replay, like JavaKeywords L101-103).
            CopyInputs(tree);

            var analysisCompilation = context.BuildStandardCompilation();
            var plan = preprocessor.Analyze(analysisCompilation, tree.SourceRoot);
            foreach (var group in plan.Groups
                         .Where(g => excludedGroups.Contains(g.TypeFullName)))
            {
                group.RolledBack = true;
            }

            // 3. Per-file rewrites; files with no active edit are already copied.
            var rewriter = new PartialMergeRewriter();
            foreach (var csFile in tree.CsFiles)
            {
                var edits = plan.EditsByFile[csFile]
                    .Where(e => plan.ActiveGroups.Any(g => g.TouchedFiles.Contains(csFile)))
                    .ToList();
                if (edits.Count == 0)
                {
                    continue;
                }

                var source = await File.ReadAllTextAsync(csFile);
                var output = rewriter.Apply(source, edits);
                var destination = Path.Combine(
                    tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, csFile));
                File.WriteAllText(destination, output,
                    FileStartsWithBom(csFile) ? new UTF8Encoding(true) : new UTF8Encoding(false));
            }

            // 4. Candidate recompile + baseline diff.
            var candidate = BuildIntermediateCompilation(tree);
            var newErrors = DiffErrors(baseline, candidate, tree.IntermediateRoot);

            if (newErrors.Count == 0)
            {
                acceptedPlan = plan;
                break;
            }

            // 5. Attribute every new error to exactly one active group (design D9).
            var groupsToRollback = new HashSet<TypeGroupPlan>();
            var attributable = true;
            foreach (var error in newErrors)
            {
                var hits = AttributeToGroups(error, plan);
                if (hits.Count != 1)
                {
                    attributable = false;
                    break;
                }

                groupsToRollback.Add(hits[0]);
            }

            if (!attributable || groupsToRollback.Count == 0)
            {
                // Zero/multi attribution → whole-tree rollback (CS2J2002).
                CopyInputs(tree);
                var first = newErrors[0];
                _diagnostics.Add(
                    $"[Error] {PartialMergeCodes.WholeTreeRolledBack}: whole-tree validation failed and " +
                    $"the new error ({first.Descriptor.Id}: {first.GetMessage()}) could not be " +
                    "attributed to a single group; all changes were rolled back.");
                success = false;
                break;
            }

            // 6. Group rollback (CS2J2001): restore every touched file to original bytes.
            foreach (var group in groupsToRollback)
            {
                group.RolledBack = true;
                excludedGroups.Add(group.TypeFullName);
                _diagnostics.Add(
                    $"[Warning] {PartialMergeCodes.GroupRolledBack}: group '{group.TypeFullName}' and all " +
                    "its touched files were rolled back; the group stays multi-part and the " +
                    "main converter will pick it up. Resolve the conflict and retry.");

                foreach (var touched in group.TouchedFiles)
                {
                    var destination = Path.Combine(
                        tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, touched));
                    File.Copy(touched, destination, overwrite: true);
                }
            }

            if (iteration == maxIterations - 1)
            {
                CopyInputs(tree);
                success = false;
            }
        }

        // 7. Residual gate: multi-part types must be whitelisted (rollback/skip).
        if (acceptedPlan != null && success)
        {
            var finalTree = BuildIntermediateCompilation(tree);
            foreach (var residual in preprocessor.CheckResidual(finalTree, acceptedPlan))
            {
                _diagnostics.Add(residual.ToMsBuildString());
            }
        }

        // 8. Restore + build/test double verify on the accepted tree.
        if (success && !HasErrors)
        {
            context.Restore();
            var verified = await context.VerifyBuildAndTestsAsync();
            context.Success = verified && !HasErrors;
        }
        else
        {
            context.Success = false;
        }
    }
```

- [ ] **Step 2: 在同一文件追加私有 helper（镜像 JavaKeywords L293-375）**

```csharp
    private static List<TypeGroupPlan> AttributeToGroups(Diagnostic error, PartialMergePlan plan)
    {
        var file = error.Location.SourceTree?.FilePath;
        if (file == null)
        {
            return [];
        }

        return plan.ActiveGroups
            .Where(group => group.TouchedFiles
                .Any(touched => string.Equals(touched, file, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private CSharpCompilation BuildIntermediateCompilation(PassTreeContext tree)
    {
        var parseOptions = CreateParseOptions(tree);
        var trees = tree.CsFiles
            .Select(file =>
            {
                var destination = Path.Combine(
                    tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, file));
                return CSharpSyntaxTree.ParseText(
                    File.ReadAllText(destination), parseOptions, path: destination);
            })
            .ToList();
        trees.AddRange(CreateAdditionalSyntaxTrees(tree));

        return CSharpCompilation.Create(
            Descriptor.Id + "-candidate", trees, GetMetadataReferences(), CreateCompilationOptions());
    }

    /// <summary>(relative intermediate path, error id, message) set over ORIGINAL sources.</summary>
    private HashSet<(string File, string Id, string Message)> BuildErrorSet(PassTreeContext tree)
        => new(BuildIntermediateCompilation(tree)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree != null)
            .Select(d => (
                NormalizePath(d.Location.SourceTree!.FilePath, tree.IntermediateRoot),
                d.Descriptor.Id,
                d.GetMessage())));

    private static List<Diagnostic> DiffErrors(
        HashSet<(string File, string Id, string Message)> baseline,
        CSharpCompilation candidate,
        string intermediateRoot)
        => candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree != null
                && !baseline.Contains((
                    NormalizePath(d.Location.SourceTree!.FilePath, intermediateRoot),
                    d.Descriptor.Id,
                    d.GetMessage())))
            .ToList();

    private static string NormalizePath(string path, string intermediateRoot)
        => Path.GetRelativePath(intermediateRoot, Path.GetFullPath(path)).Replace('\\', '/');

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }
```

文件头 using 追加 `using System.Text;`。

- [ ] **Step 3: 构建确认**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 4: 提交**

```powershell
git add src/CSharpToJava.CLI/Preprocessing/Adapters/PartialMergePassAdapter.cs
git commit -m "feat(preprocessing): C20 partial-merge 基线快照/recompile-diff/组级整轮回滚重放（M2）"
```

## Task 14: Analyzer 基线闸门与 CS2J2016/2017

**Files:**
- Modify: `src/CSharpToJava.Core/PartialMerge/PartialMergeAnalyzer.cs`

- [ ] **Step 1: Analyze 开头加基线快照与组标记**

在 `Analyze` 中 `PartialTypePartCollector.Collect(...)` 之后插入：

```csharp
        // Baseline-first (design §4.1): groups already covered by a compiler error
        // are marked skip — the pass does not compete with compiler diagnostics.
        var baselineErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                && d.Location.SourceTree != null)
            .ToList();

        foreach (var group in groups)
        {
            if (baselineErrors.Any(error =>
                    group.Parts.Any(part =>
                        string.Equals(part.Declaration.SyntaxTree.FilePath,
                            error.Location.SourceTree!.FilePath, StringComparison.OrdinalIgnoreCase)
                        && part.Declaration.FullSpan.Contains(error.Location.SourceSpan.Start))))
            {
                group.SkipBaseline = true;
            }
        }
```

实现顺序：collect → 本步标记 `SkipBaseline`/`SkipUnsupportedShape` → 仅对未标记组调用 MergeBuilder（跳过组 `MergedDeclaration` 保持 null、不产编辑），统计计 `SkippedBaseline`。

- [ ] **Step 2: CS2J2016 不受支持形态闸门**

在同一循环中追加 record 参数表检查：

```csharp
            // Primary-constructor parameter list may appear on at most one part;
            // multiple parts with a parameter list is a baseline CS error, and an
            // explicitly unsupported shape for our merge (design §4.5 point 4).
            var parameterLists = group.Parts
                .Select(part => part.Declaration)
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax>()
                .Count(record => record.ParameterList != null);
            if (parameterLists > 1)
            {
                group.SkipUnsupportedShape = true;
                diagnostics.Add(new PartialMergeDiagnostic(
                    PartialMergeSeverity.Error,
                    PartialMergeCodes.UnsupportedShape,
                    "record primary-constructor parameter list appears on " + parameterLists +
                    " parts; group left untouched",
                    TypeFullName: group.TypeFullName));
            }
```

- [ ] **Step 3: CS2J2017 analyzer/source-generator 引用探测（Info，仅 verbose）**

在 `Analyze` 返回前加：

```csharp
        foreach (var projectFile in EnumerateIntermediateProjectFiles(compilation, sourceRoot))
        {
            var text = File.ReadAllText(projectFile);
            if (text.Contains("<Analyzer Include", StringComparison.OrdinalIgnoreCase)
                || text.Contains("OutputItemType=\"Analyzer\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("SourceGenerator", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new PartialMergeDiagnostic(
                    PartialMergeSeverity.Info,
                    PartialMergeCodes.AnalyzerSeen,
                    "analyzer/source-generator reference detected; merged declaration keeps partial " +
                    "so generated parts remain valid (verify build is the semantic oracle)",
                    FilePath: projectFile));
            }
        }
```

（启发式探测，仅 verbose 可见；实施时补 `EnumerateIntermediateProjectFiles` 为对源根下 `*.csproj` 的枚举。）

- [ ] **Step 4: 构建确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 5: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMergeAnalyzer.cs
git commit -m "feat(preprocessing): C20 partial-merge 基线闸门/CS2J2016/CS2J2017 诊断（M2）"
```

## Task 15: PartialDocCommentMerger（D6 XML 合并）

**Files:**
- Create: `src/CSharpToJava.Core/PartialMerge/PartialDocCommentMerger.cs`

- [ ] **Step 1: 写入 DocCommentMerger**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Deterministic XML doc merge (design D6): the primary part's documentation is
/// kept verbatim; every other part's doc block is wrapped into a generated
/// &lt;remarks&gt;/&lt;para source="…"&gt; section appended after it. Member docs
/// follow surviving members naturally; partial-pair doc merge is an M3 concern.
/// </summary>
internal static class PartialDocCommentMerger
{
    public static SyntaxTriviaList BuildTypeLeadingTrivia(TypeGroupPlan group)
    {
        var parts = group.Parts;
        var builder = new StringBuilder();

        AppendDocBlock(builder, parts[0].Declaration, stripRemarksWrapper: false);

        foreach (var part in parts.Skip(1))
        {
            var inner = ExtractDocLines(part.Declaration);
            if (inner.Count == 0)
            {
                continue;
            }

            builder.AppendLine("/// <remarks>");
            builder.AppendLine($"/// <para source=\"{part.RelativePath}\">");
            foreach (var line in inner)
            {
                // Re-indent original XML content one level; keep its bytes otherwise.
                builder.AppendLine("/// " + line);
            }

            builder.AppendLine("/// </para>");
            builder.AppendLine("/// </remarks>");
        }

        var parsed = SyntaxFactory.ParseLeadingTrivia(builder.ToString());

        // Keep non-doc leading trivia (extern pragmas etc.) from the primary part.
        var primaryNonDoc = parts[0].Declaration.GetLeadingTrivia()
            .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        return SyntaxFactory.TriviaList(primaryNonDoc.Concat(parsed));
    }

    private static void AppendDocBlock(StringBuilder builder, TypeDeclarationSyntax declaration, bool stripRemarksWrapper)
    {
        foreach (var line in ExtractRawDocCommentLines(declaration))
        {
            builder.AppendLine(line);
        }
    }

    private static List<string> ExtractDocLines(TypeDeclarationSyntax declaration)
    {
        // Content between '///' and the raw text (XML only), trimmed; blank lines kept.
        return ExtractRawDocCommentLines(declaration)
            .Select(line => line.StartsWith("///") ? line[3..].TrimStart() : line.Trim())
            .Where(line =>
                !line.StartsWith("<summary", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("</summary", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<string> ExtractRawDocCommentLines(TypeDeclarationSyntax declaration)
    {
        var trivia = declaration.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .ToList();
        if (trivia.Count == 0)
        {
            return [];
        }

        return declaration.SyntaxTree.GetText()
            .Lines
            .Where(line => trivia.Any(t => t.Span.Contains(line.Span.Start)))
            .Select(line => line.ToString().Trim())
            .ToList();
    }
}
```

- [ ] **Step 2: MergeBuilder 接线文档合并**

把 `PartialTypeMergeBuilder.Build` 中构造 `leading` 的部分改为：

```csharp
        var docTrivia = PartialDocCommentMerger.BuildTypeLeadingTrivia(group);
        var banner = SyntaxFactory.Comment(
            "// cs2j-partial-merge: merged from " + group.Parts.Count + " part(s): " +
            string.Join(", ", group.Parts.Select(part => part.RelativePath)));
        return merged.WithLeadingTrivia(
            SyntaxFactory.TriviaList(docTrivia)
                .Add(SyntaxFactory.CarriageReturnLineFeed)
                .Add(banner)
                .Add(SyntaxFactory.CarriageReturnLineFeed));
```

（文档在前、溯源 banner 在后；顺序字节由组 9 单测锁定。）

- [ ] **Step 3: 构建确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 4: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialDocCommentMerger.cs
git add src/CSharpToJava.Core/PartialMerge/PartialTypeMergeBuilder.cs
git commit -m "feat(preprocessing): C20 partial-merge XML 文档确定性合并（M2，D6）"
```

## Task 16: global using 合成树（CreateAdditionalSyntaxTrees）

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/PartialMergePassAdapter.cs`

- [ ] **Step 1: 追加 override（代码镜像 TuplePassAdapter L59-88）**

```csharp
    public override IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
    {
        var globalUsings = new StringBuilder();
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.Layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(
                intermediateProjectFile, tree.IntermediateRoot);
            if (projectUsings != null)
            {
                globalUsings.AppendLine(projectUsings);
            }
        }

        if (globalUsings.Length == 0)
        {
            return Array.Empty<SyntaxTree>();
        }

        const string syntheticPath = "__cs2j_global_usings.g.cs";
        return new[]
        {
            CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: syntheticPath),
        };
    }
```

- [ ] **Step 2: 构建确认**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.CLI/Preprocessing/Adapters/PartialMergePassAdapter.cs
git commit -m "feat(preprocessing): C20 partial-merge global using 合成树（M2，tuple 先例）"
```

## Task 17: Core 单测补全（组 6-11、14-15）

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/PartialMergeUnitTests.cs`

- [ ] **Step 1: 追加 M2 矩阵（测试名称即需求，不得删减）**

```csharp
    // Group 6: modifier union semantics and ordering.
    [Fact]
    public void Group6_Modifiers_Union_Accessibility_Abstract_Sealed_Static_Unsafe_ReadonlyPartialOrder() { }

    // Group 7: base/interface semantic dedup and conflict baseline-skip.
    [Fact]
    public void Group7_Base_One_NonObject_Interface_Semantic_Dedup_And_Conflict_BaselineSkip() { }

    // Group 8: attribute union / AllowMultiple / non-AllowMultiple baseline.
    [Fact]
    public void Group8_Attributes_Union_AllowMultiple_And_NonAllowMultiple_BaselineSkip() { }

    // Group 9: XML doc merge ordering, source paths and byte-stable rerun.
    [Fact]
    public void Group9_XmlDoc_Merge_Order_SourcePaths_And_Rerun_ByteStable() { }

    // Group 10: global using / alias signature pairing (int vs System.Int32) and negative control.
    [Fact]
    public void Group10_GlobalUsing_Alias_Signature_Pairing_And_NoSyntheticTrees_Negative() { }

    // Group 11: rollback CS2J2001 (unique attribution) / CS2J2002 (unattributable) / Strict=false.
    [Fact]
    public void Group11_Unique_Attribution_Rolls_Group_Back_CS2J2001() { }

    [Fact]
    public void Group11_Unattributable_Error_Rolls_Whole_Tree_Back_CS2J2002() { }

    [Fact]
    public void Group11_StrictFalse_Downgrades_Residual_To_Warning() { }

    // Group 14: generator safety — an extra 'generated' part compiles; classic declaration kept.
    [Fact]
    public void Group14_Simulated_Generated_Part_Compiles_And_Classic_Declaration_Kept() { }

    // Group 15: top-level statements Program is never grouped/changed.
    [Fact]
    public void Group15_TopLevel_Program_Not_Grouped_Or_Changed() { }
```

实施要点：

1. 组 11 的「基线干净、合并后新错」注入走内部测试钩子（`PartialMergeAnalyzer` 增加 `IReadOnlyList<Action<TypeGroupPlan>>? groupMutators = null` 可选参数，仅测试程序集经 InternalsVisibleTo 使用，生产调用点一律传 null）：mutator 向 `MergedDeclaration.Members` 追加一个与既有成员同名的成员制造候选 CS0102；
2. 组 7/8 的基线冲突用真实 CS 错误源码（两个非 object 基类、非 AllowMultiple 重复特性），断言组 `SkipBaseline`、零编辑、无 CS2J20xx；
3. 组 10 需两套 compilation 对照：含/不含 `CreateAdditionalSyntaxTrees` 各跑一次；
4. 组 9 的字节稳定：同输入连跑 Analyze 两次，`MergedDeclaration.ToFullString()` 相等。

- [ ] **Step 2: 跑测试**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter PartialMergeUnitTests --nologo`
Expected: `失败: 0`（M1 + M2 全部用例）。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMergeAnalyzer.cs
git add tests/CSharpToJava.ProjectTests/PartialMergeUnitTests.cs
git commit -m "test(preprocessing): C20 partial-merge M2 组6-11/14-15 单测与注入钩子"
```

## Task 18: SamplePartialSolution fixture

**Files:**
- Create: `tests/SamplePartialSolution/`（Lib + App + Tests + sln）

- [ ] **Step 1: 脚手架（非交互）**

```powershell
dotnet new sln -n SamplePartialSolution -o tests/SamplePartialSolution
dotnet new classlib -n SamplePartialLib -o tests/SamplePartialSolution/SamplePartialLib -f net10.0
dotnet new console -n SamplePartialApp -o tests/SamplePartialSolution/SamplePartialApp -f net10.0
dotnet new xunit -n SamplePartialTests -o tests/SamplePartialSolution/SamplePartialTests -f net10.0
dotnet sln tests/SamplePartialSolution/SamplePartialSolution.sln add tests/SamplePartialSolution/SamplePartialLib/SamplePartialLib.csproj
dotnet sln tests/SamplePartialSolution/SamplePartialSolution.sln add tests/SamplePartialSolution/SamplePartialApp/SamplePartialApp.csproj
dotnet sln tests/SamplePartialSolution/SamplePartialSolution.sln add tests/SamplePartialSolution/SamplePartialTests/SamplePartialTests.csproj
dotnet add tests/SamplePartialSolution/SamplePartialApp/SamplePartialApp.csproj reference tests/SamplePartialSolution/SamplePartialLib/SamplePartialLib.csproj
dotnet add tests/SamplePartialSolution/SamplePartialTests/SamplePartialTests.csproj reference tests/SamplePartialSolution/SamplePartialLib/SamplePartialLib.csproj
```

删除脚手架自带 `Class1.cs`/模板测试文件。

- [ ] **Step 2: 写入 Lib 场景文件（设计 §7.2）**

```
SamplePartialLib/
  FolderA/OrderService.Part1.cs   # 主部件：partial class + 部分成员/XML summary
  FolderB/OrderService.Part2.cs   # 同类型第二部件（成员、特性分部件）
  Records/Customer.Part1.cs       # partial record class，参数表仅此一件
  Records/Customer.Part2.cs
  Structs/Utf8Reader.Part1.cs     # ref partial struct 跨文件夹方法
  Structs/Utf8Reader.Part2.cs
  Contracts/IRepository.Part1.cs  # partial interface
  Contracts/IRepository.Part2.cs
  Methods/ClassicPartial.cs       # 经典 partial void（无实现）
  Methods/ExtendedPartial.cs      # 扩展 partial（定义+实现两部件）
  Members/PartialEvent.cs         # C#13 partial 事件（字段式定义 + add/remove 实现）
  Members/PartialProperty.cs      # C#13 partial 属性/索引器（定义+实现）
  Members/PartialConstructor.cs   # C#13 partial 构造器（初始化器只随实现性声明）
  Nested/Outer.Part1.cs           # 嵌套 partial，内外各自合并
  Nested/Outer.Part2.cs
```

`OrderService.Part1.cs` 示例：

```csharp
namespace SamplePartialLib;

/// <summary>Order service primary part.</summary>
public partial class OrderService
{
    private readonly int _region;

    public int Region => _region;

    partial void OnCreated();

    public void Create()
    {
        OnCreated();
    }
}
```

`FolderB/OrderService.Part2.cs` 示例：

```csharp
using System.ComponentModel.DataAnnotations;

namespace SamplePartialLib;

/// <summary>Order service second part.</summary>
public partial class OrderService
{
    [Required]
    private string? _name;

    public string? Name => _name;

    partial void OnCreated()
    {
    }
}
```

其余文件按文件头注释的场景意图写齐；C#13 文件须能在 `LanguageVersion.Latest` 下编译。

- [ ] **Step 3: 写入 .NET 半区行为测试**

`SamplePartialTests/BehaviorTests.cs`（合并前后行为等价闸门，测试在中间树与原树都必须通过）：

```csharp
using SamplePartialLib;

namespace SamplePartialTests;

public sealed class BehaviorTests
{
    [Fact]
    public void OrderService_Create_Runs_Classic_Partial_Hook()
    {
        var service = new OrderService();
        service.Create();
        Assert.True(service.Region >= 0);
    }

    [Fact]
    public void Customer_Record_Keeps_Primary_Constructor_Parameters()
    {
        var customer = new Customer(1) { };
        Assert.Equal(1, customer.Id);
    }
}
```

按 fixture 实际成员补全覆盖；`SamplePartialApp/Program.cs` 调一次 Lib 确认控制台半区。

- [ ] **Step 4: fixture 本地 build/test 确认**

Run: `dotnet build tests/SamplePartialSolution/SamplePartialSolution.sln --nologo -v q`
Expected: `0 个错误`。

Run: `dotnet test tests/SamplePartialSolution/SamplePartialSolution.sln --nologo`
Expected: `失败: 0`。

- [ ] **Step 5: 提交（逐文件 add fixture 全部文件；bin/obj 不 add）**

```powershell
git add tests/SamplePartialSolution/SamplePartialSolution.sln
git add tests/SamplePartialSolution/SamplePartialLib
git add tests/SamplePartialSolution/SamplePartialApp
git add tests/SamplePartialSolution/SamplePartialTests
git commit -m "test(preprocessing): C20 SamplePartialSolution fixture（两半区，M2）"
```

## Task 19: 项目级验收 + ProjectTests csproj 接线

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`
- Create: `tests/CSharpToJava.ProjectTests/PartialMergeProjectTests.cs`

- [ ] **Step 1: csproj 追加 Content（插 L257 `</Project>` 之前，紧邻 L248-255 C28 ItemGroup 之后）**

```xml
  <ItemGroup>
    <!-- Include SamplePartialSolution as content so it's available at test runtime.
         bin/obj are excluded: the fixture may have been built locally. -->
    <Content Include="..\SamplePartialSolution\**\*.*"
              Exclude="..\SamplePartialSolution\**\bin\**;..\SamplePartialSolution\**\obj\**">
      <Link>SamplePartialSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 2: 写 PartialMergeProjectTests（形态仿 MemberDesugarProjectTests）**

```csharp
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.PartialMerge;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C20 project-level acceptance (design §7.2/§7.3): direct adapter + runner.
/// run.Success proves the intermediate tree passes dotnet build + dotnet test,
/// residual=0 and statistics carry the merged counts.
/// </summary>
[Collection("SamplePartialSolution")]
public sealed class PartialMergeProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SamplePartialSolution");

    [Fact]
    public async Task Pass_Succeeds_IntermediateTree_BuildAndTest_Residual_Zero()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c20-" + Guid.NewGuid().ToString("N"));
        try
        {
            var adapter = new PartialMergePassAdapter(
                new PartialMergeOptions(), verbose: false,
                filterUnsupportedProjects: false,
                compatNamespacePrefixes: null,
                verifyBuild: true,
                verifyTests: true);

            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = Path.Combine(FixtureDir, "SamplePartialSolution.sln"),
                DestinationRoot = outputRoot,
            });

            Assert.True(run.Success);
            var intermediate = Directory.GetFiles(
                run.IntermediateRoot, "*.cs", SearchOption.AllDirectories);

            // Exactly one declaration per source type: no file contains the
            // removal comment and a surviving declaration of the same type.
            Assert.DoesNotContain(intermediate, file =>
                File.ReadAllText(file).Contains("cs2j-partial-merge: part of", StringComparison.Ordinal)
                && File.ReadAllText(file).Contains("public partial", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Disabled_Path_Is_Byte_Identical_To_Input()
    {
        // Run the default chain (partial-merge default-off) over the fixture and
        // assert its source files are byte-equal copies (design §1.4-3).
        // Implementation mirrors MemberDesugarProjectTests byte-copy case.
        await Task.CompletedTask;
    }
}
```

`Disabled_Path_Is_Byte_Identical_To_Input` 实施时补成与 MemberDesugar 同款字节比对（空 body 仅为占位标记，不得保留空实现）。

- [ ] **Step 3: 跑项目级验收**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter PartialMergeProjectTests --nologo`
Expected: `失败: 0`。

- [ ] **Step 4: 提交**

```powershell
git add tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
git add tests/CSharpToJava.ProjectTests/PartialMergeProjectTests.cs
git commit -m "test(preprocessing): C20 partial-merge 项目级验收与 csproj 接线（M2）"
```

## Task 20: CLI 端到端测试

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/PartialMergeCLITests.cs`

- [ ] **Step 1: 写 CLI 端到端（Process 启动 CLI dll）**

```csharp
using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C20 CLI end-to-end (design §7.3): default plan excludes partial-merge;
/// --enable-pass executes it with the .cs2j-partial-merge-src directory;
/// a second convert-project run hits the fingerprint cache; the standalone
/// `preprocess --pass partial-merge` verb reaches it.
/// </summary>
public sealed class PartialMergeCLITests
{
    private static string CliDll
        => Path.Combine(AppContext.BaseDirectory, "CSharpToJava.CLI.dll");

    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SamplePartialSolution");

    private static (int ExitCode, string Output) RunCli(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(CliDll);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    [Fact]
    public void Default_Plan_Excludes_PartialMerge()
    {
        var (_, output) = RunCli("preprocess", "--list-passes");

        // The registry line exists (Implemented) but must be marked off, so the
        // default execution plan never schedules partial-merge.
        var line = Assert.Single(output.Split('\n')
            .Where(line => line.Contains("partial-merge", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("off", line);
    }

    [Fact]
    public void EnablePass_Executes_And_Creates_Intermediate_Directory()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "cs2j-c20-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exitCode, output) = RunCli(
                "convert-project", Path.Combine(FixtureDir, "SamplePartialSolution.sln"),
                "--enable-pass", "partial-merge", "--output", workRoot);
            Assert.Equal(0, exitCode);
            Assert.Contains(Directory.GetDirectories(workRoot),
                directory => Path.GetFileName(directory) == ".cs2j-partial-merge-src");
            Assert.Contains("CS2J", output) ;
        }
        finally
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    [Fact]
    public void Standalone_Preprocess_Pass_Reaches_PartialMerge()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "cs2j-c20-standalone-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exitCode, output) = RunCli(
                "preprocess", Path.Combine(FixtureDir, "SamplePartialSolution.sln"),
                "--pass", "partial-merge", "--output", workRoot);
            Assert.Equal(0, exitCode);
            Assert.Contains("partial-merge", output);
        }
        finally
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }
}
```

注意：`--dry-run`/真实动词与参数名实施时以 Program.cs 实际定义为准修正（参数意图不变：默认排除、显式启用执行、独立 verb 可达）；`CSharpToJava.CLI.dll` 的输出拷贝由 ProjectTests csproj 既有机制保证（若缺则参照其他 CLITests 的路径解析）。

- [ ] **Step 2: 跑 CLI 测试**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter PartialMergeCLITests --nologo`
Expected: `失败: 0`。

- [ ] **Step 3: 提交**

```powershell
git add tests/CSharpToJava.ProjectTests/PartialMergeCLITests.cs
git commit -m "test(preprocessing): C20 partial-merge CLI 端到端（启用/独立verb/默认排除，M2）"
```

## Task 21: M2 里程碑全量验证 + sdk partial 命中率清单

- [ ] **Step 1: 全仓 Release build**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 2: 三个测试工程全量 Release test**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo`
Expected: `失败: 0`。

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --nologo`
Expected: `失败: 0`（含 PartialMerge 全部用例）。

并存回归：禁用 partial-merge 时现有 PartialStructMergeTests / PartialInterfaceTupleMappingTests 保持全绿（已含在 Tests 全量中，另确认无 MergedTypePlaceholder 相关失败）。

- [ ] **Step 3: sdk partial 命中率统计（设计 §7.3/§8 输入）**

对 `D:\csharp-sdk` 的四子集（按 catalog §5.1 分桶）逐个以 opt-in 运行：

```powershell
dotnet src/CSharpToJava.CLI/bin/Release/net10.0/CSharpToJava.CLI.dll convert-project `
  <sdk 子集路径> --enable-pass partial-merge --output <临时输出>
```

汇总每次运行的链摘要行与 CS2J2001/2002/2018 计数，成文为 **`docs/superpowers/specs/2026-10-02-c20-sdk-partial-hitrate.md`**（M2 执行时新建，本计划阶段不预建），表格列：

| 子集 | 扫描类型数 | partial 类型组 | 命中文件数 | 经典/扩展 partial 方法 | C#13 成员 | 回滚组 | residual | 人工结论 |
|---|---|---|---|---|---|---|---|---|

- [ ] **Step 4: 提交 sdk 清单**

```powershell
git add docs/superpowers/specs/2026-10-02-c20-sdk-partial-hitrate.md
git commit -m "docs(preprocessing): C20 sdk partial 命中率与残余清单（M2）"
```

---

# M3 — partial 方法/成员塌缩（Tasks 22-25，可选增强）

**出口（设计 §9）**：设计 §7.1 组 4-5 全绿；调用点移除专项；转换器侧 PartialType 退役的前置评审（不在本里程碑删代码）。

## Task 22: PartialMemberMerge 配对塌缩

**Files:**
- Modify: `src/CSharpToJava.Core/PartialMerge/PartialMemberMerge.cs`
- Modify: `src/CSharpToJava.Core/PartialMerge/PartialTypeMergeBuilder.cs`

- [ ] **Step 1: 重写 CollectMembers 为符号配对版（仅当组进入 M3 塌缩路径）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class PartialMemberMerge
{
    /// <summary>
    /// M3 (design §4.5): when both the partial definition and its implementation
    /// exist in source, collapse to the implementation member — attributes merged
    /// (implementation first), docs merged via PartialDocCommentMerger, partial
    /// token stripped. Call sites never change (the member name is unchanged).
    /// Pairs with one side outside source keep both members (v1 behavior).
    /// </summary>
    public static IEnumerable<MemberDeclarationSyntax> CollectMembers(
        TypeGroupPlan group,
        CSharpCompilation compilation,
        MutablePartialMergeStats stats,
        bool collapseEnabled)
    {
        var emittedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var part in group.Parts)
        {
            var semanticModel = compilation.GetSemanticModel(part.Declaration.SyntaxTree);
            foreach (var member in part.Declaration.Members)
            {
                if (semanticModel.GetDeclaredSymbol(member) is not { } memberSymbol)
                {
                    yield return member;
                    continue;
                }

                if (TryGetPartialPair(memberSymbol, out var definition, out var implementation))
                {
                    if (collapseEnabled && definition != null && implementation != null
                        && BothInSource(group, definition, implementation))
                    {
                        if (emittedSymbols.Add(implementation))
                        {
                            stats.MembersCollapsed++;
                            yield return CollapsePair(definition, implementation, compilation);
                        }

                        continue;
                    }
                }

                // v1: definition + implementation both survive as two members.
                if (emittedSymbols.Add(memberSymbol))
                {
                    yield return member;
                }
            }
        }
    }

    private static bool TryGetPartialPair(
        ISymbol member,
        out ISymbol? definition,
        out ISymbol? implementation)
    {
        switch (member)
        {
            case IMethodSymbol method when method.PartialDefinitionPart != null
                || method.PartialImplementationPart != null:
                definition = method.PartialDefinitionPart ?? (method.IsPartialDefinition ? method : null);
                implementation = method.PartialImplementationPart
                    ?? (method.PartialDefinitionPart != null ? method : null);
                return true;

            case IPropertySymbol property when property.PartialDefinitionPart != null
                || property.PartialImplementationPart != null:
                definition = property.PartialDefinitionPart;
                implementation = property.PartialImplementationPart;
                return true;

            case IEventSymbol @event when @event.PartialDefinitionPart != null
                || @event.PartialImplementationPart != null:
                definition = @event.PartialDefinitionPart;
                implementation = @event.PartialImplementationPart;
                return true;

            default:
                definition = null;
                implementation = null;
                return false;
        }
    }

    private static bool BothInSource(
        TypeGroupPlan group,
        ISymbol definition,
        ISymbol implementation)
        => IsInGroupSource(definition) && IsInGroupSource(implementation);

    private static bool IsInGroupSource(ISymbol symbol)
        => symbol.Locations.Any(location => location.IsInSource);

    private static MemberDeclarationSyntax CollapsePair(
        ISymbol definition,
        ISymbol implementation,
        CSharpCompilation compilation)
    {
        var implementationNode = (MemberDeclarationSyntax)
            implementation.DeclaringSyntaxReferences[0].GetSyntax();
        var definitionNode = (MemberDeclarationSyntax)
            definition.DeclaringSyntaxReferences[0].GetSyntax();

        // Attributes: implementation lists win/first, then definition's; strip partial.
        var attributes = SyntaxFactory.List(
            implementationNode.AttributeLists.Concat(definitionNode.AttributeLists));
        var modifiers = SyntaxFactory.TokenList(
            implementationNode.GetModifiers().Where(token => !token.IsKind(SyntaxKind.PartialKeyword)));

        // Docs: implementation doc primary; definition doc folded into remarks (D6).
        var docTrivia = PartialDocCommentMerger.BuildPairTrivia(definitionNode, implementationNode);

        return implementationNode
            .WithAttributeLists(attributes)
            .WithModifiers(modifiers)
            .WithLeadingTrivia(docTrivia);
    }
}
```

（`MemberDeclarationSyntax.GetModifiers()` 用 `(node as dynamic).Modifiers` 或按节点种类分派实现；构造器初始化器自然只在实现性成员上，塌缩不改构造器初始化器。）

- [ ] **Step 2: MergeBuilder/Builder 调用点透传塌缩开关**

`PartialTypeMergeBuilder.Build` 增加参数：

```csharp
public static TypeDeclarationSyntax Build(
    CSharpCompilation compilation,
    TypeGroupPlan group,
    MutablePartialMergeStats stats,
    PartialMergeOptions options)
{
    // ...
    var collapseEnabled = true; // pair collapse itself is unconditional in M3;
                                // classic declaration removal is the separate flag.
    var members = SyntaxFactory.List(PartialMemberMerge.CollectMembers(
        group, compilation, stats, collapseEnabled));
    // ...
}
```

Analyzer/Build 调用点同步传入 stats 与 options。

- [ ] **Step 3: 构建确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 4: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMemberMerge.cs
git add src/CSharpToJava.Core/PartialMerge/PartialTypeMergeBuilder.cs
git add src/CSharpToJava.Core/PartialMerge/PartialMergeAnalyzer.cs
git commit -m "feat(preprocessing): C20 partial-merge M3 源内配对塌缩（实现优先/文档合并/摘partial）"
```

## Task 23: AssumeNoGeneratedPartial —— 经典 partial 声明与调用移除

**Files:**
- Modify: `src/CSharpToJava.Core/PartialMerge/PartialMemberMerge.cs`
- Create: `src/CSharpToJava.Core/PartialMerge/PartialCallSiteRewriter.cs`

- [ ] **Step 1: 经典声明移除条件**

在 `CollectMembers` 配对分支旁追加经典形态判定：

```csharp
                // Classic C#3 partial method: declaration-only, implementation optional,
                // generator may supply it. Removed only with the explicit flag.
                if (memberSymbol is IMethodSymbol { IsPartialDefinition: true } classic
                    && classic.PartialImplementationPart == null
                    && optionsAssumeNoGeneratedPartial(group))
                {
                    if (emittedSymbols.Add(classic))
                    {
                        stats.MembersCollapsed++;
                        continue; // declaration dropped
                    }
                }
```

`optionsAssumeNoGeneratedPartial` = 组级从 `PartialMergeOptions.AssumeNoGeneratedPartial` 读取（默认 false；非显式 opt-in 时经典声明一律保留，generator 安全红线）。

- [ ] **Step 2: 调用点移除（PartialCallSiteRewriter）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Removes invocation statements of classic partial declarations erased under
/// AssumeNoGeneratedPartial (design §4.5 M3). Conservative: the invocation must
/// be a standalone expression statement whose argument expressions provably have
/// no side effects; any uncertainty leaves the call in place (residual count),
/// matching the StatementTransformer's conservative statement policies.
/// </summary>
internal sealed class PartialCallSiteRewriter : CSharpSyntaxRewriter
{
    private readonly HashSet<IMethodSymbol> _erasedMethods;
    private readonly SemanticModel _semanticModel;

    public PartialCallSiteRewriter(
        IEnumerable<IMethodSymbol> erasedMethods,
        SemanticModel semanticModel)
    {
        _erasedMethods = new HashSet<IMethodSymbol>(erasedMethods, SymbolEqualityComparer.Default);
        _semanticModel = semanticModel;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is InvocationExpressionSyntax invocation
            && _semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
            && _erasedMethods.Contains(method)
            && invocation.ArgumentList.Arguments.All(argument => ExpressionHasNoSideEffects(argument.Expression)))
        {
            // Compiler erases the whole statement; keep a provenance comment.
            return SyntaxFactory.Comment(
                "// cs2j-partial-merge: erased call to classic partial '" + method.Name + "'");
        }

        return base.VisitExpressionStatement(node);
    }

    private static bool ExpressionHasNoSideEffects(ExpressionSyntax expression)
        => !expression.DescendantNodesAndSelf().Any(node =>
            node is InvocationExpressionSyntax
                or ObjectCreationExpressionSyntax
                or AssignmentExpressionSyntax
                or PostfixUnaryExpressionSyntax
                or PrefixUnaryExpressionSyntax);
}
```

调用点改写由适配器在文件写出前按 `SemanticModel` 驱动；带副作用参数、方法组、lambda 内调用等场景保留并计 residual。

- [ ] **Step 3: 构建确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 4: 提交**

```powershell
git add src/CSharpToJava.Core/PartialMerge/PartialMemberMerge.cs
git add src/CSharpToJava.Core/PartialMerge/PartialCallSiteRewriter.cs
git commit -m "feat(preprocessing): C20 partial-merge M3 经典声明/无副作用调用移除（显式flag）"
```

## Task 24: M3 单测（组 4-5）+ 转换器退役前置评审

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/PartialMergeUnitTests.cs`

- [ ] **Step 1: 追加组 4-5 用例（测试名称即需求）**

```csharp
    // Group 4: two-generation partial methods.
    [Fact]
    public void Group4_Classic_PartialVoid_NoImpl_Kept_And_Compiles() { }

    [Fact]
    public void Group4_Extended_Partial_Definition_And_Impl_Collapse_To_Single_Member_M3() { }

    [Fact]
    public void Group4_Extended_Missing_Impl_Is_Baseline_Skip_Not_CS2J2015() { }

    [Fact]
    public void Group4_AssumeFlag_Removes_Declaration_And_SideEffectFree_Call_Statement() { }

    [Fact]
    public void Group4_Call_With_SideEffecting_Argument_Is_Kept() { }

    // Group 5: C#13 partial property/indexer/event/constructor.
    [Fact]
    public void Group5_Partial_Property_And_Indexer_Definition_Impl_Collapse() { }

    [Fact]
    public void Group5_Partial_Event_FieldStyle_Definition_AddRemove_Impl_Collapse() { }

    [Fact]
    public void Group5_Partial_Constructor_Initializer_Only_On_Implementation() { }

    [Fact]
    public void Group5_PrimaryConstructor_On_Two_Parts_Is_Baseline_Skip() { }
```

组 5 实施前若发现 Roslyn 实际配对 API 与假设（IPropertySymbol/IEventSymbol 的 Partial*Part 名称、构造器 partial 配对判据）有出入，先按设计 §4.5 点 7 做 net10/Latest 探针，把实测结果回写设计 §4.5，再落测试；解析不了的形态 CS2J2016 显式跳过。

- [ ] **Step 2: 跑测试**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter PartialMergeUnitTests --nologo`
Expected: `失败: 0`。

- [ ] **Step 3: 转换器 PartialType 退役前置评审（只评审、不删代码）**

评审输入：M2 sdk 清单残余统计、`PartialType/` 目录与 `PartialTypeMergeStore`、placeholder 过滤点、两个直连测试（PartialStructMergeTests/PartialInterfaceTupleMappingTests）。产出：在设计文档末尾追加「退役前置评审结论」小节，列退役 PR 的准入条件（sdk 成功路径多部件=0、直连入口保留至退役 PR 同批处理、回滚组白名单为 0）。

- [ ] **Step 4: 提交**

```powershell
git add tests/CSharpToJava.ProjectTests/PartialMergeUnitTests.cs
git add docs/superpowers/specs/2026-09-29-c20-partial-merge-design.md
git commit -m "test(preprocessing): C20 partial-merge M3 组4-5 单测与转换器退役前置评审"
```

## Task 25: M3 全量验证

- [ ] **Step 1: 全仓 Release build**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 2: 两个测试工程全量 Release test**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo`
Expected: `失败: 0`。

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --nologo`
Expected: `失败: 0`。

---

# M4 — 默认化决策（Task 26）

**出口（设计 §9）**：决策记录二选一留痕——继续 opt-in / 随统一引擎默认开。

## Task 26: §8 五项门槛逐条结论

**Files:**
- Modify: `docs/superpowers/specs/2026-09-29-c20-partial-merge-design.md`（不新建文件）

- [ ] **Step 1: 逐条核对设计 §8 门槛**

1. §7 全绿；fixture 两半区与全仓 Release build/test 全绿；
2. sdk 四子集 + EF Core 画像（M2 清单）以 `--enable-pass partial-merge` 跑通：残余/回滚组为 0 或全部有明确人工结论；
3. 含 analyzer/source generator 工程专项验证（至少 MVVM Toolkit、System.Text.Json 各一例）通过；
4. 转换器侧 `PartialType/`+Store+placeholder 退役 PR 已合入（或统一引擎已声明单声明树前提）；
5. 跨至少一次主版本回归窗口的 opt-in 观察期。

- [ ] **Step 2: 在设计文档末尾追加「M4 决策」小节并提交**

小节内容：五条门槛各一行结论（满足/不满足 + 证据链接），末尾给出决策：

- 五条件全满足 → 把 `defaultEnabled` 翻 `true`（逐文件 add PassCatalog.cs 及受影响测试，提交 `feat(preprocessing): C20 partial-merge 默认开启（M4 门槛全过）`）；
- 任一不满足 → 决策「继续 opt-in，随统一引擎默认开」，`defaultEnabled: false` 不变（仅文档提交）。

---

