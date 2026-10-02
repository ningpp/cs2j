# C28 指令/using trivia 清扫 — Implementation Plan

> **For agentic workers:** 按任务顺序实施本计划；步骤用 checkbox（`- [ ]`）跟踪。执行环境：分支 `dev-3.x`，**禁止 worktree**（用户 2026-10-02 强制约束），直接在当前工作区提交。

**Goal:** 翻转注册表 stub `lexical-normalize` 为 Implemented，交付 LN-1…LN-7 纯语法规则（一次 parse、幂等、residual 闸门），经 M3 门槛后默认开启。

**Architecture:** Core 新目录 `LexicalNormalize/`（parse 一次 → DirectiveScan 守卫扫描 → LineDirectiveRewriter/NamespaceRewriter 内存串接 → Verifier 校验）；CLI 单适配器走标准 spine，`AfterFilesAsync` 做 global using 跨文件查重；不新增 convert-project 专属开关，opt-in 仅经 `--enable-pass lexical-normalize`。

**Tech Stack:** C# / .NET 10 / Roslyn（`Microsoft.CodeAnalysis.CSharp` syntax 层，无 Compilation/SemanticModel）/ xUnit / CommandLine parser。

**设计**：[2026-09-29-c28-directive-trivia-design.md](../specs/2026-09-29-c28-directive-trivia-design.md)（2026-10-02 二次校订）

---

## 0. 基线裁决（开工前确认）

1. 开工 HEAD：`e5e7773a`（分支 `dev-3.x`）。先跑 `git branch --show-current`，输出必须为 `dev-3.x`；否则停止。
2. 工作区存在与本任务无关的在途改动（C34 decimal-ops：PassCatalog/PassChainFactory/Program.cs 等已被修改未提交）。开工第一步与其归属确认：
   - 若已被提交：以新 HEAD 为准，重新核对 §2 的行号（以 Read 实际内容为准）；
   - 若仍未提交：本计划所有提交**逐文件 `git add <本任务文件>`**，禁止 `git add -A`/`git add .`，不裹挟其改动。
3. 不新建支撑工程；不修改 `Transformers/**` 等转换器代码；不 push、不建标签（除非用户另行授权）。
4. 提交节奏：M1（Tasks 1-10）→ M2（Tasks 11-16）→ M3（Tasks 17-19）；每个 Task 结束可提交，里程碑末必须全量 Release 验证。

## 1. 文件结构（新建/修改总览）

```
src/CSharpToJava.Core/LexicalNormalize/                         # 全部新建
  LexicalNormalizeModels.cs        # Options/Statistics/Diagnostic/Result/Severity + Codes
  GlobalUsingModel.cs              # GlobalUsingRecord + GlobalUsingKind
  MutableLexicalStats.cs           # 内部可变统计 → Build() 不可变 record
  DirectiveScan.cs                 # LN-2/3/4/5/6 守卫与审计扫描
  LineDirectiveRewriter.cs         # LN-1 #line trivia 剥离（复制 E01 RemovingRewriter 机制）
  NamespaceRewriter.cs             # LN-7 file-scoped → block（仅选项开启）
  LexicalNormalizeVerifier.cs      # 重 parse + residual + token/结构化等价
  LexicalNormalizePreprocessor.cs  # public 入口 Normalize(source, options)
src/CSharpToJava.CLI/Preprocessing/Adapters/
  LexicalNormalizePassAdapter.cs   # 新建，标准 spine
tests/CSharpToJava.Tests/
  LexicalNormalizeTests.cs         # 新建，Core 单测矩阵
tests/CSharpToJava.ProjectTests/
  SampleLexicalNormalizeSolution/  # 新建 fixture（dotnet new 脚手架）
  LexicalNormalizeProjectTests.cs  # 新建，项目级验收
  LexicalNormalizeCLITests.cs      # 新建，CLI 端到端
# 修改（行内追加，锚点见 §2）：
  src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs
  src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs
  src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs
  src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs
  src/CSharpToJava.CLI/Program.cs
  tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs
  tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs
  tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
```

## 2. 集成点行号锚点（2026-10-02 基线，随 Read 复核）

| 文件 | 位置 |
|------|------|
| PassStubCatalog.cs | lexical stub 条目 L49-59（删）；`Ids.LexicalNormalize` L22（保留） |
| PassCatalog.cs | conditional 描述符 L41-49；local-function-lift L50-60（新条目插二者之间）；stub 拼接 L383-384 |
| PassChainFactory.cs | `BuildLegacyToggles` L99-129（不动）；config 类 L37-88（加 factory，参照 L72-73）；`CreateAdapter` L142-374（conditional 臂 L147-153，新臂紧随） |
| CliPreprocessChain.cs | CliChainRequest L16-82（加 factory，参照 L47-48）；config 组装 L144-165（透传） |
| Program.cs | convert-project 请求 L185-233（加 factory）；preprocess 动词 L3648-3696（加 factory）；PreprocessVerbOptions L4603-4678（加两 `--ln-*`）；ConvertProjectOptions L4849 起（**不动**） |
| PassChainOrderTests.cs | LockedChain L19-49；EnabledByDefault L51-54；config 实例 L231-242；豁免断言 L331-349 |
| ProjectPreprocessorsConsistentTests.cs | C05 段 L130-137（新断言紧随） |

---

# M1 — 锚点基建 + LN-1/2/3

## Task 1: Models 与诊断码

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/LexicalNormalizeModels.cs`

- [ ] **Step 1: 写入 Models 文件**

```csharp
namespace CSharpToJava.Core.LexicalNormalize;

public enum LexicalNormalizeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CS2J55xx diagnostic produced by the lexical-normalize pass.</summary>
public sealed record LexicalNormalizeDiagnostic(
    LexicalNormalizeSeverity Severity,
    string Code,
    string Message,
    int? Line = null);

/// <summary>Pure-syntax options; deliberately contains no preprocessor Symbols (boundary with E01).</summary>
public sealed record LexicalNormalizeOptions
{
    /// <summary>LN-1: strip active #line directives (default on).</summary>
    public bool StripLineDirectives { get; init; } = true;

    /// <summary>LN-7: rewrite file-scoped namespaces into block namespaces (default off; double opt-in).</summary>
    public bool NormalizeFileScopedNamespaces { get; init; } = false;

    /// <summary>LN-4: severity for extern alias declarations/use sites.</summary>
    public LexicalNormalizeSeverity ExternAliasSeverity { get; init; } = LexicalNormalizeSeverity.Error;

    /// <summary>LN-5: cross-file duplicate global using audit (AfterFilesAsync).</summary>
    public bool AuditGlobalUsingDuplicates { get; init; } = true;

    /// <summary>Upgrade every Warning to Error.</summary>
    public bool Strict { get; init; } = false;

    /// <summary>Whether Info diagnostics are surfaced (set false to suppress).</summary>
    public bool ReportInfo { get; init; } = true;
}

public sealed record LexicalNormalizeStatistics
{
    public required int FilesScanned { get; init; }
    public required int FilesTransformed { get; init; }
    public required int FilesSkippedClean { get; init; }
    public required int FilesDirtyNoRewrite { get; init; }
    public required int ResidualCount { get; init; }
    public required int LineDirectivesRemoved { get; init; }
    public required int ErrorDirectivesSeen { get; init; }
    public required int ResidualConditionalDirectives { get; init; }
    public required int ExternAliasDirectives { get; init; }
    public required int ExternAliasQualifiedSites { get; init; }
    public required int GlobalUsingsPlain { get; init; }
    public required int GlobalUsingsStatic { get; init; }
    public required int GlobalUsingsAlias { get; init; }
    public required int StaticUsings { get; init; }
    public required int DuplicateGlobalUsings { get; init; }
    public required int FileScopedNamespacesNormalized { get; init; }
    public required IReadOnlyDictionary<string, int> ErrorsByCode { get; init; }
}

public sealed record LexicalNormalizeResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<LexicalNormalizeDiagnostic> Diagnostics,
    LexicalNormalizeStatistics Statistics,
    IReadOnlyList<GlobalUsingRecord> GlobalUsings);

/// <summary>CS2J55xx code constants (design §6).</summary>
internal static class LexicalNormalizeCodes
{
    public const string BuildFailed = "CS2J5503";
    public const string TestsFailed = "CS2J5504";
    public const string LineRemovedInfo = "CS2J5510";
    public const string ErrorDirective = "CS2J5511";
    public const string ConditionalFirst = "CS2J5512";
    public const string ExternAlias = "CS2J5520";
    public const string DuplicateGlobalUsing = "CS2J5521";
    public const string StaticUsingInfo = "CS2J5522";
    public const string Residual = "CS2J5540";
    public const string FileScopedInfo = "CS2J5541";
    public const string StructuralCheck = "CS2J5542";
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`（警告可既有）。

## Task 2: GlobalUsingRecord 与 MutableLexicalStats

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/GlobalUsingModel.cs`
- Create: `src/CSharpToJava.Core/LexicalNormalize/MutableLexicalStats.cs`

- [ ] **Step 1: 写 GlobalUsingModel.cs**

```csharp
namespace CSharpToJava.Core.LexicalNormalize;

public enum GlobalUsingKind
{
    Plain,
    Static,
    Alias,
}

/// <summary>
/// One global using directive observed at syntax level. SourcePath is attached by
/// the adapter for cross-file duplicate grouping.
/// </summary>
public sealed record GlobalUsingRecord(
    GlobalUsingKind Kind,
    string NormalizedText,
    int Line,
    bool IsStatic,
    bool IsAlias,
    string? SourcePath = null);
```

- [ ] **Step 2: 写 MutableLexicalStats.cs**

```csharp
namespace CSharpToJava.Core.LexicalNormalize;

internal sealed class MutableLexicalStats
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;
    public int LineDirectivesRemoved;
    public int ErrorDirectivesSeen;
    public int ResidualConditionalDirectives;
    public int ExternAliasDirectives;
    public int ExternAliasQualifiedSites;
    public int GlobalUsingsPlain;
    public int GlobalUsingsStatic;
    public int GlobalUsingsAlias;
    public int StaticUsings;
    public int DuplicateGlobalUsings;
    public int FileScopedNamespacesNormalized;

    private readonly Dictionary<string, int> _errorsByCode = new(StringComparer.Ordinal);

    public void AddError(string code)
        => _errorsByCode[code] = _errorsByCode.TryGetValue(code, out var count) ? count + 1 : 1;

    public LexicalNormalizeStatistics Build() => new()
    {
        FilesScanned = FilesScanned,
        FilesTransformed = FilesTransformed,
        FilesSkippedClean = FilesSkippedClean,
        FilesDirtyNoRewrite = FilesDirtyNoRewrite,
        ResidualCount = ResidualCount,
        LineDirectivesRemoved = LineDirectivesRemoved,
        ErrorDirectivesSeen = ErrorDirectivesSeen,
        ResidualConditionalDirectives = ResidualConditionalDirectives,
        ExternAliasDirectives = ExternAliasDirectives,
        ExternAliasQualifiedSites = ExternAliasQualifiedSites,
        GlobalUsingsPlain = GlobalUsingsPlain,
        GlobalUsingsStatic = GlobalUsingsStatic,
        GlobalUsingsAlias = GlobalUsingsAlias,
        StaticUsings = StaticUsings,
        DuplicateGlobalUsings = DuplicateGlobalUsings,
        FileScopedNamespacesNormalized = FileScopedNamespacesNormalized,
        ErrorsByCode = new Dictionary<string, int>(_errorsByCode, StringComparer.Ordinal),
    };
}
```

- [ ] **Step 3: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 3: DirectiveScan（LN-2/3/4/5/6）

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/DirectiveScan.cs`

- [ ] **Step 1: 写 DirectiveScan.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize;

internal sealed record DirectiveScanOutcome(
    List<LexicalNormalizeDiagnostic> Diagnostics,
    List<GlobalUsingRecord> GlobalUsings,
    MutableLexicalStats Stats);

/// <summary>
/// Read-only guard/audit scan: LN-2 active #error, LN-3 residual conditional
/// directives, LN-4 extern alias declarations/use sites, LN-5 global using
/// classification and LN-6 static using inventory. Never rewrites nodes.
/// </summary>
internal static class DirectiveScan
{
    public static DirectiveScanOutcome Run(CompilationUnitSyntax root, LexicalNormalizeOptions options)
    {
        var diagnostics = new List<LexicalNormalizeDiagnostic>();
        var globalUsings = new List<GlobalUsingRecord>();
        var stats = new MutableLexicalStats();

        var activeDirectives = root.DescendantTrivia()
            .Select(trivia => trivia.GetStructure())
            .OfType<DirectiveTriviaSyntax>()
            .Where(directive => directive.IsActive)
            .ToList();

        // LN-2: active #error must never be stripped; fail loudly.
        foreach (var error in activeDirectives.OfType<ErrorDirectiveTriviaSyntax>())
        {
            stats.ErrorDirectivesSeen++;
            stats.AddError(LexicalNormalizeCodes.ErrorDirective);
            diagnostics.Add(new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.ErrorDirective,
                $"[LN-2] active #error directive must be resolved: {error.ToString().Trim()}",
                LineOf(error)));
        }

        // LN-3: conditional family must already have been removed by E01.
        foreach (var directive in activeDirectives)
        {
            if (directive is DefineDirectiveTriviaSyntax
                or UndefDirectiveTriviaSyntax
                or IfDirectiveTriviaSyntax
                or ElifDirectiveTriviaSyntax
                or ElseDirectiveTriviaSyntax
                or EndifDirectiveTriviaSyntax)
            {
                stats.ResidualConditionalDirectives++;
                stats.AddError(LexicalNormalizeCodes.ConditionalFirst);
                diagnostics.Add(new LexicalNormalizeDiagnostic(
                    LexicalNormalizeSeverity.Error,
                    LexicalNormalizeCodes.ConditionalFirst,
                    "[LN-3] residual conditional directive; the 'conditional' pass must run before lexical-normalize",
                    LineOf(directive)));
            }
        }

        foreach (var trivia in root.DescendantTrivia()
                     .Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            stats.ResidualConditionalDirectives++;
            stats.AddError(LexicalNormalizeCodes.ConditionalFirst);
            diagnostics.Add(new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.ConditionalFirst,
                "[LN-3] residual disabled text; the 'conditional' pass must run before lexical-normalize",
                LineOf(trivia)));
        }

        // LN-4a: extern alias declarations.
        foreach (var externAlias in root.DescendantNodes().OfType<ExternAliasDirectiveSyntax>())
        {
            stats.ExternAliasDirectives++;
            if (options.ExternAliasSeverity == LexicalNormalizeSeverity.Error)
            {
                stats.AddError(LexicalNormalizeCodes.ExternAlias);
            }

            diagnostics.Add(new LexicalNormalizeDiagnostic(
                options.ExternAliasSeverity,
                LexicalNormalizeCodes.ExternAlias,
                $"[LN-4] extern alias cannot be normalized by a syntax-only pass: {externAlias.ToString().Trim()}",
                LineOf(externAlias)));
        }

        // LN-4b: alias-qualified use sites (global:: is built-in and excluded).
        foreach (var aliasName in root.DescendantNodes().OfType<AliasQualifiedNameSyntax>()
                     .Where(name => name.Alias.Name.Identifier.ValueText != "global"))
        {
            stats.ExternAliasQualifiedSites++;
            if (options.ExternAliasSeverity == LexicalNormalizeSeverity.Error)
            {
                stats.AddError(LexicalNormalizeCodes.ExternAlias);
            }

            diagnostics.Add(new LexicalNormalizeDiagnostic(
                options.ExternAliasSeverity,
                LexicalNormalizeCodes.ExternAlias,
                $"[LN-4] alias-qualified name cannot be normalized by a syntax-only pass: {aliasName.ToString().Trim()}",
                LineOf(aliasName)));
        }

        // LN-5/LN-6: using inventory.
        foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var isStatic = usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword);
            var isGlobal = usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword);
            var isAlias = usingDirective.Alias != null;

            if (isStatic)
            {
                stats.StaticUsings++;
            }

            if (!isGlobal)
            {
                continue;
            }

            var kind = isAlias ? GlobalUsingKind.Alias
                : isStatic ? GlobalUsingKind.Static
                : GlobalUsingKind.Plain;

            if (kind == GlobalUsingKind.Plain)
            {
                stats.GlobalUsingsPlain++;
            }
            else if (kind == GlobalUsingKind.Static)
            {
                stats.GlobalUsingsStatic++;
            }
            else
            {
                stats.GlobalUsingsAlias++;
            }

            globalUsings.Add(new GlobalUsingRecord(
                kind,
                NormalizeUsing(usingDirective),
                LineOf(usingDirective) ?? 0,
                isStatic,
                isAlias));
        }

        return new DirectiveScanOutcome(diagnostics, globalUsings, stats);
    }

    /// <summary>Ordinal text normalization for duplicate comparison (trim + single spaces).</summary>
    internal static string NormalizeUsing(UsingDirectiveSyntax usingDirective)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(
            usingDirective.ToString(), @"\s+", " ");
        return text.Trim();
    }

    internal static int? LineOf(SyntaxNode node)
    {
        try
        {
            return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static int? LineOf(SyntaxTrivia trivia)
    {
        try
        {
            return trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 4: LineDirectiveRewriter（LN-1）

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/LineDirectiveRewriter.cs`

- [ ] **Step 1: 写 LineDirectiveRewriter.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize;

/// <summary>
/// LN-1: removes active <see cref="LineDirectiveTriviaSyntax"/> structured trivia
/// using the same filter-and-rewind mechanism as E01's RemovingRewriter
/// (ConditionalDirectiveStripper.cs L180-230). The target table is independent
/// and contains only #line; the token sequence (kind+text) must stay identical.
/// </summary>
internal sealed class LineDirectiveRewriter : CSharpSyntaxRewriter
{
    private int _lineDirectivesRemoved;

    public int LineDirectivesRemoved => _lineDirectivesRemoved;

    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        var visited = base.VisitToken(token);
        var leading = Filter(visited.LeadingTrivia);
        var trailing = Filter(visited.TrailingTrivia);

        return leading.Count == visited.LeadingTrivia.Count
            && trailing.Count == visited.TrailingTrivia.Count
                ? visited
                : visited.WithLeadingTrivia(leading).WithTrailingTrivia(trailing);
    }

    private SyntaxTriviaList Filter(SyntaxTriviaList triviaList)
    {
        if (triviaList.Count == 0)
        {
            return triviaList;
        }

        List<SyntaxTrivia>? result = null;
        for (var index = 0; index < triviaList.Count; index++)
        {
            var trivia = triviaList[index];
            if (!IsLineTarget(trivia))
            {
                result?.Add(trivia);
                continue;
            }

            result ??= new List<SyntaxTrivia>(triviaList.Take(index));
            _lineDirectivesRemoved++;

            // Rewind trailing whitespace left on the line (E01 mechanism).
            while (result.Count > 0 && result[^1].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                result.RemoveAt(result.Count - 1);
            }
        }

        return result == null ? triviaList : SyntaxFactory.TriviaList(result);
    }

    internal static bool IsLineTarget(SyntaxTrivia trivia)
        => trivia.GetStructure() is LineDirectiveTriviaSyntax { IsActive: true };
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 5: LexicalNormalizeVerifier

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/LexicalNormalizeVerifier.cs`

- [ ] **Step 1: 写 LexicalNormalizeVerifier.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize;

/// <summary>
/// Post-rewrite verification: reparse with no errors, enabled-rule residuals
/// cleared, and either token-sequence equality (LN-1) or structural equivalence
/// (LN-7). Failure returns a diagnostic; the preprocessor rolls the file back.
/// </summary>
internal static class LexicalNormalizeVerifier
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);

    public static bool Verify(
        string output,
        CompilationUnitSyntax originalRoot,
        LexicalNormalizeOptions options,
        out LexicalNormalizeDiagnostic? failure)
    {
        failure = null;

        var reparsed = CSharpSyntaxTree.ParseText(output, ParseOptions);
        if (reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            failure = new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.StructuralCheck,
                "[LN] rewritten output produced parse errors; file rolled back");
            return false;
        }

        var outputRoot = reparsed.GetCompilationUnitRoot();

        if (options.StripLineDirectives
            && outputRoot.DescendantTrivia()
                .Select(t => t.GetStructure())
                .OfType<LineDirectiveTriviaSyntax>()
                .Any(d => d.IsActive))
        {
            failure = new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.Residual,
                "[LN-1] residual #line directive; file rolled back");
            return false;
        }

        if (options.NormalizeFileScopedNamespaces)
        {
            if (outputRoot.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Any())
            {
                failure = new LexicalNormalizeDiagnostic(
                    LexicalNormalizeSeverity.Error,
                    LexicalNormalizeCodes.Residual,
                    "[LN-7] residual file-scoped namespace; file rolled back");
                return false;
            }

            if (!StructuralEquivalence(originalRoot, outputRoot))
            {
                failure = new LexicalNormalizeDiagnostic(
                    LexicalNormalizeSeverity.Error,
                    LexicalNormalizeCodes.StructuralCheck,
                    "[LN-7] structural equivalence of usings/members failed; file rolled back");
                return false;
            }
        }
        else if (options.StripLineDirectives && !TokenSequenceEqual(originalRoot, outputRoot))
        {
            failure = new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.StructuralCheck,
                "[LN-1] token sequence mismatch; file rolled back");
            return false;
        }

        return true;
    }

    /// <summary>Using directives (with in/out namespace marker) and declared type names.</summary>
    private static bool StructuralEquivalence(CompilationUnitSyntax left, CompilationUnitSyntax right)
        => UsingShape(left).SequenceEqual(UsingShape(right), StringComparer.Ordinal)
            && MemberShape(left).SequenceEqual(MemberShape(right), StringComparer.Ordinal);

    private static IEnumerable<string> UsingShape(CompilationUnitSyntax root)
    {
        foreach (var usingDirective in root.Usings)
        {
            yield return "out:" + DirectiveScan.NormalizeUsing(usingDirective);
        }

        foreach (var namespaceDeclaration in root.Members.OfType<BaseNamespaceDeclarationSyntax>())
        {
            foreach (var usingDirective in namespaceDeclaration.Usings)
            {
                yield return "in:" + DirectiveScan.NormalizeUsing(usingDirective);
            }
        }
    }

    private static IEnumerable<string> MemberShape(CompilationUnitSyntax root)
    {
        foreach (var member in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            yield return member.Keyword.ValueText + " " + member.Identifier.ValueText;
        }
    }

    // Copied from E01 (ConditionalDirectiveStripper.cs L154-178).
    private static bool TokenSequenceEqual(CompilationUnitSyntax left, CompilationUnitSyntax right)
    {
        using var leftTokens = left.DescendantTokens().GetEnumerator();
        using var rightTokens = right.DescendantTokens().GetEnumerator();
        while (true)
        {
            var hasLeft = leftTokens.MoveNext();
            var hasRight = rightTokens.MoveNext();
            if (hasLeft != hasRight)
            {
                return false;
            }

            if (!hasLeft)
            {
                return true;
            }

            var leftToken = leftTokens.Current;
            var rightToken = rightTokens.Current;
            if (leftToken.RawKind != rightToken.RawKind
                || !string.Equals(leftToken.Text, rightToken.Text, StringComparison.Ordinal))
            {
                return false;
            }
        }
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 6: LexicalNormalizePreprocessor 入口

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/LexicalNormalizePreprocessor.cs`

- [ ] **Step 1: 写 LexicalNormalizePreprocessor.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.LexicalNormalize;

/// <summary>
/// Pure-syntax, idempotent lexical normalization. Parses the source once; guard
/// diagnostics (LN-2/3/4) gate every rewrite, and a verification failure rolls
/// the file back. No CSharpCompilation / SemanticModel is ever created.
/// </summary>
public sealed class LexicalNormalizePreprocessor
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    public LexicalNormalizeResult Normalize(
        string sourceCode,
        LexicalNormalizeOptions? options = null)
    {
        options ??= new LexicalNormalizeOptions();

        var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions);
        var root = tree.GetCompilationUnitRoot();

        var scan = DirectiveScan.Run(root, options);
        var diagnostics = scan.Diagnostics;
        var stats = scan.Stats;
        stats.FilesScanned = 1;

        // Roslyn parse errors surfaced with the compiler id (CS1xxx); #error is
        // already covered with CS2J5511 by DirectiveScan.
        foreach (var diagnostic in tree.GetDiagnostics()
                     .Where(d => d.Severity == DiagnosticSeverity.Error
                         && d.Id != "CS1029"))
        {
            stats.AddError(diagnostic.Id);
            diagnostics.Add(new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                diagnostic.Id,
                $"[LN] parse error: {diagnostic.GetMessage()}",
                diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1));
        }

        var effective = diagnostics
            .Select(d => EffectiveSeverity(d, options.Strict))
            .ToList();
        var hasError = effective.Any(s => s == LexicalNormalizeSeverity.Error);

        if (hasError)
        {
            stats.FilesDirtyNoRewrite = 1;
            return new LexicalNormalizeResult(
                sourceCode,
                Changed: false,
                Materialize(diagnostics, options),
                stats.Build(),
                scan.GlobalUsings);
        }

        var working = root;

        if (options.StripLineDirectives)
        {
            var lineRewriter = new LineDirectiveRewriter();
            working = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)lineRewriter.Visit(working);
            stats.LineDirectivesRemoved += lineRewriter.LineDirectivesRemoved;
        }

        if (options.NormalizeFileScopedNamespaces)
        {
            var namespaceRewriter = new NamespaceRewriter();
            working = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)namespaceRewriter.Visit(working);
            stats.FileScopedNamespacesNormalized += namespaceRewriter.NormalizedCount;
        }

        var output = working.ToFullString();
        var changed = !string.Equals(sourceCode, output, StringComparison.Ordinal);

        if (!changed)
        {
            stats.FilesSkippedClean = 1;
            return new LexicalNormalizeResult(
                sourceCode,
                Changed: false,
                Materialize(diagnostics, options),
                stats.Build(),
                scan.GlobalUsings);
        }

        if (!LexicalNormalizeVerifier.Verify(output, root, options, out var failure)
            && failure != null)
        {
            stats.AddError(failure.Code);
            diagnostics.Add(failure);
            stats.FilesDirtyNoRewrite = 1;
            return new LexicalNormalizeResult(
                sourceCode,
                Changed: false,
                Materialize(diagnostics, options),
                stats.Build(),
                scan.GlobalUsings);
        }

        stats.FilesTransformed = 1;
        return new LexicalNormalizeResult(
            output,
            Changed: true,
            Materialize(diagnostics, options),
            stats.Build(),
            scan.GlobalUsings);
    }

    private static LexicalNormalizeSeverity EffectiveSeverity(
        LexicalNormalizeDiagnostic diagnostic,
        bool strict)
        => strict && diagnostic.Severity == LexicalNormalizeSeverity.Warning
            ? LexicalNormalizeSeverity.Error
            : diagnostic.Severity;

    private static List<LexicalNormalizeDiagnostic> Materialize(
        List<LexicalNormalizeDiagnostic> diagnostics,
        LexicalNormalizeOptions options)
        => diagnostics
            .Where(d => options.ReportInfo || d.Severity != LexicalNormalizeSeverity.Info)
            .Select(d => d with
            {
                Severity = EffectiveSeverity(d, options.Strict),
            })
            .ToList();
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 7: NamespaceRewriter（LN-7；M1 先落文件、默认无路径触发）

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/NamespaceRewriter.cs`

- [ ] **Step 1: 写 NamespaceRewriter.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize;

/// <summary>
/// LN-7: rewrite a file-scoped namespace into a block-scoped one. Usings after
/// the file-scoped declaration belong inside the namespace (Roslyn stores them
/// on FileScopedNamespaceDeclarationSyntax.Usings); usings before it stay on the
/// compilation unit. Output is normalized through the Formatter (E07/E11 pattern).
/// </summary>
internal sealed class NamespaceRewriter : CSharpSyntaxRewriter
{
    private int _normalizedCount;

    public int NormalizedCount => _normalizedCount;

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var fileScoped = node.Members
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (fileScoped == null)
        {
            return node;
        }

        var namespaceKeyword = SyntaxFactory.Token(SyntaxKind.NamespaceKeyword)
            .WithLeadingTrivia(fileScoped.NamespaceKeyword.LeadingTrivia);

        var block = SyntaxFactory.NamespaceDeclaration(fileScoped.Name)
            .WithNamespaceKeyword(namespaceKeyword)
            .WithExterns(fileScoped.Externs)
            .WithUsings(fileScoped.Usings)
            .WithMembers(fileScoped.Members)
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                    .WithTrailingTrivia(fileScoped.SemicolonToken.TrailingTrivia));

        var newMembers = node.Members.Replace(fileScoped, block);
        var updated = (CompilationUnitSyntax)node.WithMembers(newMembers);
        _normalizedCount++;

        using var workspace = new AdhocWorkspace();
        return Formatter.Format(updated, workspace);
    }
}
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 8: Core 单测（LN-1/2/3 + 幂等/parse 错误）

**Files:**
- Create: `tests/CSharpToJava.Tests/LexicalNormalizeTests.cs`

- [ ] **Step 1: 写单测文件**

```csharp
using CSharpToJava.Core.LexicalNormalize;

namespace CSharpToJava.Tests;

/// <summary>Core matrix for M1: LN-1 #line strip, LN-2 #error, LN-3 residual
/// conditionals, idempotence and malformed input.</summary>
public sealed class LexicalNormalizeTests
{
    private static LexicalNormalizeResult Run(string source, LexicalNormalizeOptions? options = null)
        => new LexicalNormalizePreprocessor().Normalize(source, options);

    [Fact]
    public void LN1_StripsAllLineDirectiveForms_AndKeepsTokenSequence()
    {
        const string source = """
            #line 100 "x.cs"
            using System;

            #line default
            public class C
            {
                #line hidden
                public void M() { }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(3, result.Statistics.LineDirectivesRemoved);
        Assert.DoesNotContain(result.OutputCode, "#line");
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LN1_WhenDisabled_LeavesFileUnchanged()
    {
        const string source = "#line 5\npublic class C { }\n";

        var result = Run(source, new LexicalNormalizeOptions { StripLineDirectives = false });

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void LN2_ActiveErrorDirective_FailsAndDoesNotRewrite()
    {
        const string source = "#error boom\npublic class C { }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ErrorDirective);
        Assert.Equal(1, result.Statistics.ErrorDirectivesSeen);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void LN3_ResidualDefineOrConditional_FailsWithOrderingDiagnostic()
    {
        const string source = "#define X\npublic class C { }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ConditionalFirst);

        const string ifSource = "#if X\npublic class C { }\n#endif\n";
        var ifResult = Run(ifSource);
        Assert.Contains(ifResult.Diagnostics, d => d.Code == LexicalNormalizeCodes.ConditionalFirst);
    }

    [Fact]
    public void LN3_PragmaRegionNullableWarning_DoNotTrigger()
    {
        const string source = """
            #pragma warning disable CS0105
            #nullable enable
            #region R
            #warning careful
            public class C { }
            #endregion
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ConditionalFirst);
    }

    [Fact]
    public void LN4_ExternAlias_ErrorsByDefault_GlobalColonExcluded()
    {
        const string source = "extern alias X;\npublic class C { X::N.T Field; global::System.Int32 Other; }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.ExternAliasDirectives);
        Assert.Equal(1, result.Statistics.ExternAliasQualifiedSites);
        Assert.Contains(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ExternAlias);
    }

    [Fact]
    public void LN4_SeverityWarning_RemainingRulesStillRewrite()
    {
        const string source = "extern alias X;\n#line 2\npublic class C { X::N.T Field; }\n";

        var result = Run(source, new LexicalNormalizeOptions
        {
            ExternAliasSeverity = LexicalNormalizeSeverity.Warning,
        });

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.OutputCode, "#line");
        Assert.Contains(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ExternAlias
            && d.Severity == LexicalNormalizeSeverity.Warning);
    }

    [Fact]
    public void Idempotent_SecondRunProducesNoChange()
    {
        const string source = "#line 7\npublic class C { public int X; }\n";

        var first = Run(source);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void CleanFile_IsReturnedByteEqual()
    {
        const string source = "public class C { }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void MalformedInput_ReturnsOriginalWithParseError()
    {
        const string source = "public class {\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Severity == LexicalNormalizeSeverity.Error);
        Assert.Equal(source, result.OutputCode);
    }
}
```

- [ ] **Step 2: 运行测试，确认全绿**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo --filter "FullyQualifiedName~LexicalNormalizeTests"`
Expected: `已通过! - 失败: 0，通过: 10`（若数字不符，核对是否全部发现）。

- [ ] **Step 3: 提交**

```bash
git add src/CSharpToJava.Core/LexicalNormalize tests/CSharpToJava.Tests/LexicalNormalizeTests.cs
git commit -m "feat(preprocessing): C28 lexical-normalize Core LN-1/2/3/4 守卫与单测（M1）"
```

## Task 9: LexicalNormalizePassAdapter

**Files:**
- Create: `src/CSharpToJava.CLI/Preprocessing/Adapters/LexicalNormalizePassAdapter.cs`

- [ ] **Step 1: 写适配器**

```csharp
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for lexical-normalize (C28). Parse-only standard spine; each file is
/// parsed exactly once inside Core. AfterFilesAsync performs the cross-file
/// duplicate global using audit (LN-5).
/// </summary>
internal sealed class LexicalNormalizePassAdapter : ProjectPassAdapter
{
    private readonly LexicalNormalizeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly List<string> _diagnostics = new();
    private readonly List<(GlobalUsingRecord Record, string RelativePath, int Line)> _globalUsings = new();

    private int _filesTransformed;
    private int _filesClean;
    private int _lineRemoved;
    private int _fileScoped;
    private int _externSites;

    public LexicalNormalizePassAdapter(
        LexicalNormalizeOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.LexicalNormalize);

    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = new LexicalNormalizePreprocessor().Normalize(context.SourceCode, _options);

        foreach (var diagnostic in result.Diagnostics)
        {
            var severity = diagnostic.Severity == LexicalNormalizeSeverity.Error ? "Error"
                : diagnostic.Severity == LexicalNormalizeSeverity.Warning ? "Warning"
                : "Info";
            _diagnostics.Add(
                $"[{severity}] {context.RelativePath}:{diagnostic.Line ?? 0}: {diagnostic.Message}");
        }

        _lineRemoved += result.Statistics.LineDirectivesRemoved;
        _fileScoped += result.Statistics.FileScopedNamespacesNormalized;
        _externSites += result.Statistics.ExternAliasDirectives
            + result.Statistics.ExternAliasQualifiedSites;

        foreach (var globalUsing in result.GlobalUsings)
        {
            _globalUsings.Add((globalUsing, context.RelativePath, globalUsing.Line));
        }

        if (result.Changed)
        {
            _filesTransformed++;
            if (_verbose)
            {
                Console.WriteLine($"[lexical-normalize] transformed: {context.RelativePath}");
            }

            return Task.FromResult<PassFileRewrite?>(new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            });
        }

        _filesClean++;
        if (_verbose)
        {
            Console.WriteLine($"[lexical-normalize] clean (copy): {context.RelativePath}");
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        if (!_options.AuditGlobalUsingDuplicates)
        {
            return Task.CompletedTask;
        }

        foreach (var group in _globalUsings.GroupBy(entry => entry.Record.NormalizedText, StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            var locations = string.Join(", ",
                group.Select(g => $"{g.RelativePath}:{g.Line}").Distinct(StringComparer.Ordinal));
            var severity = _options.Strict ? LexicalNormalizeSeverity.Error : LexicalNormalizeSeverity.Warning;

            _diagnostics.Add(
                $"[{(severity == LexicalNormalizeSeverity.Error ? "Error" : "Warning")}] :0: " +
                $"[LN-5] duplicate global using '{group.Key}' at {locations}");
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(
            $"[Warning] dotnet restore failed with exit code {exitCode} for {restoreTarget}.");

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(
            $"[Error] dotnet build failed with exit code {exitCode} after lexical-normalize.");

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(
            $"[Error] dotnet test failed with exit code {exitCode} after lexical-normalize.");

    public override bool HasErrors
        => _diagnostics.Any(d => d.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase));

    public override string BuildChainSummaryLine()
        => $"lexical-normalize: {_filesTransformed} transformed, {_filesClean} clean, " +
           $"{_lineRemoved} #line removed, {_externSites} extern-alias site(s), " +
           $"{_globalUsings.Count} global using(s), {_fileScoped} file-scoped normalized";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => verbose
            ? _diagnostics.ToList()
            : _diagnostics.Where(d => !d.StartsWith("[Info]", StringComparison.OrdinalIgnoreCase)).ToList();
}
```

- [ ] **Step 2: 编译确认（此时 CreateAdapter 尚未接线，独立编译 CLI）**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 10: 注册表翻转与链接线

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs`
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: PassStubCatalog —— 删 stub 条目、留常量**

删除整个 lexical 条目块（当前 L49-59：从 `new(` 到 `catalogItems: "C28, C43 (observation)"),`），原位替换为注释：

```csharp
// Ids.LexicalNormalize flipped to Implemented in PassCatalog (C28);
// the id constant stays here by the iterator-lowering precedent (§3.4).
```

- [ ] **Step 2: PassCatalog —— conditional 之后插入描述符**

在 conditional 描述符块（L41-49）结束之后、local-function-lift（L50）之前插入：

```csharp
        new(
            PassStubCatalog.Ids.LexicalNormalize,
            "Lexical normalization",
            ".cs2j-lexical-normalize-src",
            PassStage.Lexical,
            PassStatus.Implemented,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.Conditional },
            needsSemanticModel: false,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Strip #line directives, audit #error/extern-alias/global-using forms and optionally normalize file-scoped namespaces; pure-syntax idempotent.",
            catalogItems: "C28"),
```

- [ ] **Step 3: PassChainFactory —— config 加 factory**

在 `PassChainConfiguration` 中 EnumDeclarationOptionsFactory（L72-73）之后加：

```csharp
    /// <summary>Invoked only when lexical-normalize is enabled.</summary>
    public required Func<LexicalNormalizeOptions> LexicalNormalizeOptionsFactory { get; init; }
```

文件顶部 using 区加：

```csharp
using CSharpToJava.Core.LexicalNormalize;
```

- [ ] **Step 4: PassChainFactory —— CreateAdapter 加臂**

在 conditional 臂（L147-153）之后插入：

```csharp
            PassStubCatalog.Ids.LexicalNormalize => new LexicalNormalizePassAdapter(
                config.LexicalNormalizeOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),
```

`BuildLegacyToggles` **不动**。

- [ ] **Step 5: CliPreprocessChain —— request 加 factory 并透传**

在 CliChainRequest 的 EnumDeclarationOptionsFactory（L47-48）之后加：

```csharp
    /// <summary>Invoked only when lexical-normalize is enabled.</summary>
    public required Func<LexicalNormalizeOptions> LexicalNormalizeOptionsFactory { get; init; }
```

文件顶部 using 区加：

```csharp
using CSharpToJava.Core.LexicalNormalize;
```

config 组装块（L159 EnumDeclarationOptionsFactory 透传行附近）加：

```csharp
            LexicalNormalizeOptionsFactory = request.LexicalNormalizeOptionsFactory,
```

- [ ] **Step 6: Program.cs —— 两处请求补 factory**

convert-project 链请求（L209-213 EnumDeclarationOptionsFactory 之后）加：

```csharp
                LexicalNormalizeOptionsFactory = () => new LexicalNormalizeOptions
                {
                    ReportInfo = opts.Verbose,
                },
```

文件顶部确保 `using CSharpToJava.Core.LexicalNormalize;`（若不存在则在使用处全限定）。

preprocess 动词请求（L3672-3676 之后）加：

```csharp
                LexicalNormalizeOptionsFactory = () => new LexicalNormalizeOptions
                {
                    ReportInfo = opts.Verbose,
                    NormalizeFileScopedNamespaces = opts.LnNormalizeFileScoped,
                    ExternAliasSeverity = opts.LnExternAliasSeverityEffective,
                },
```

- [ ] **Step 7: Program.cs —— PreprocessVerbOptions 加两个专属参数**

在 PreprocessVerbOptions 中 `NoDecimalOps` 属性（L4669-4671）之后、`LocalFunctionStrict` 计算属性之前加：

```csharp
    [Option("ln-normalize-file-scoped", Default = false,
        HelpText = "lexical-normalize: rewrite file-scoped namespaces into block namespaces (LN-7)")]
    public bool LnNormalizeFileScoped { get; set; }

    [Option("ln-extern-alias-severity", Default = "Error",
        HelpText = "lexical-normalize: severity for extern alias sites, one of Error|Warning|Info (LN-4)")]
    public string LnExternAliasSeverity { get; set; } = "Error";

    public LexicalNormalizeSeverity LnExternAliasSeverityEffective =>
        Enum.Parse<LexicalNormalizeSeverity>(LnExternAliasSeverity, ignoreCase: true);
```

- [ ] **Step 8: 构建确认**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 11: 链序/一致性测试翻转

**Files:**
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs`
- Modify: `tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs`

- [ ] **Step 1: LockedChain 插入本 id**

在 LockedChain 数组中 `PassIds.Conditional`（L21）之后插入：

```csharp
        PassStubCatalog.Ids.LexicalNormalize,
```

- [ ] **Step 2: EnabledByDefault 排除本 id**

把 L54：

```csharp
    private static readonly string[] EnabledByDefault = LockedChain.ToArray();
```

改为：

```csharp
    // lexical-normalize lands Implemented/default-off (M3 gate flips it on).
    private static readonly string[] EnabledByDefault =
        LockedChain.Where(id => id != PassStubCatalog.Ids.LexicalNormalize).ToArray();
```

- [ ] **Step 3: ChainFactory config 补 factory**

在 `ChainFactory_creates_an_adapter_for_every_implemented_pass` 的 config 初始化（L238 EnumDeclarationOptionsFactory 之后）加：

```csharp
            LexicalNormalizeOptionsFactory = () => new LexicalNormalizeOptions(),
```

- [ ] **Step 4: 豁免名单加本 id**

`BuildLegacyToggles_covers_every_registered_pass` 的排除链（L343-346）加一行：

```csharp
                && id != PassStubCatalog.Ids.LexicalNormalize
```

- [ ] **Step 5: 补顺序断言**

在 `Registry_order_matches_the_locked_historical_chain` 内 Assert.Equal 之后加：

```csharp
        var ordered = registry.Ordered.Where(p => p.Status == PassStatus.Implemented).ToList();
        Assert.True(
            ordered.FindIndex(p => p.Id == PassIds.Conditional)
                < ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.LexicalNormalize)
                && ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.LexicalNormalize)
                < ordered.FindIndex(p => p.Id == PassIds.LocalFunctionLift),
            "expected conditional < lexical-normalize < local-function-lift");
```

- [ ] **Step 6: ProjectPreprocessorsConsistentTests 追加断言**

在 C05 段（L130-137）之后加：

```csharp
            // lexical-normalize (C28): no facade; descriptor carries the directory;
            // default-off, enabled via --enable-pass lexical-normalize.
            var lexicalDescriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.LexicalNormalize);
            Assert.Equal(".cs2j-lexical-normalize-src",
                lexicalDescriptor.IntermediateDirectoryName);
            Assert.False(lexicalDescriptor.DefaultEnabled);
            Assert.Equal("C28", lexicalDescriptor.CatalogItems);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.LexicalNormalizePassAdapter));
```

- [ ] **Step 7: 运行链序测试**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo --filter "FullyQualifiedName~PassChainOrderTests|FullyQualifiedName~ProjectPreprocessorsConsistentTests"`
Expected: `已通过! - 失败: 0`。

- [ ] **Step 8: 提交**

```bash
git add src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs src/CSharpToJava.CLI/Program.cs tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs src/CSharpToJava.CLI/Preprocessing/Adapters/LexicalNormalizePassAdapter.cs
git commit -m "feat(preprocessing): C28 lexical-normalize 翻转 Implemented（默认关、双 verify、无专属开关）"
```

## Task 12: M1 里程碑全量验证

- [ ] **Step 1: Release build**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 2: 三个测试工程 Release test**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo`
Expected: `失败: 0`。
（ProjectTests 在 Task 16 后跑全量；M1 阶段可先只跑 Tests。）

- [ ] **Step 3: --list-passes 目视确认**

Run: `dotnet src/CSharpToJava.CLI/bin/Release/net10.0/CSharpToJava.CLI.dll preprocess --list-passes`
Expected: `lexical-normalize  Lexical  Implemented  off  conditional  rules=C28`；CS2J9001 不再出现于该 id。

---

# M2 — LN-4/5/6/7 全规则 + fixture + CLI

## Task 13: LN-5/6 单测补全（global using 分类）

LN-4/5/6 规则代码在 M1 的 DirectiveScan 中已落地，本任务补齐审计面测试。

**Files:**
- Modify: `tests/CSharpToJava.Tests/LexicalNormalizeTests.cs`

- [ ] **Step 1: 追加测试**

在文件末尾、最后一个 `}` 之前加：

```csharp
    [Fact]
    public void LN5_GlobalUsingClassification_PlainStaticAlias_CountsAndRecords()
    {
        const string source = """
            global using System;
            global using static System.Math;
            global using A = System.Collections;
            using System.Linq;
            """;

        var result = Run(source, new LexicalNormalizeOptions { StripLineDirectives = false });

        Assert.Equal(1, result.Statistics.GlobalUsingsPlain);
        Assert.Equal(1, result.Statistics.GlobalUsingsStatic);
        Assert.Equal(1, result.Statistics.GlobalUsingsAlias);
        Assert.Equal(3, result.GlobalUsings.Count);
        Assert.Equal(1, result.Statistics.StaticUsings);
    }

    [Fact]
    public void LN5_GlobalUsingGlobalColon_AndPlainForm_AreDistinctTexts()
    {
        const string source = "global using global::System;\nglobal using System;\n";

        var result = Run(source, new LexicalNormalizeOptions { StripLineDirectives = false });

        Assert.Equal(2, result.GlobalUsings.Select(g => g.NormalizedText).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LN6_StaticUsing_IsCountedAndNotRewritten()
    {
        const string source = "using static System.Math;\npublic class C { public int X => Abs(-1); }\n";

        var result = Run(source);

        Assert.Equal(1, result.Statistics.StaticUsings);
        Assert.Contains(result.OutputCode, "using static System.Math;");
    }
```

- [ ] **Step 2: 运行确认**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo --filter "FullyQualifiedName~LexicalNormalizeTests"`
Expected: `失败: 0`（总数 13）。

## Task 14: LN-7 单测

**Files:**
- Modify: `tests/CSharpToJava.Tests/LexicalNormalizeTests.cs`

- [ ] **Step 1: 追加 LN-7 测试**

```csharp
    [Fact]
    public void LN7_FileScopedNamespace_BecomesBlock_UsingsPlacedInside()
    {
        const string source = """
            namespace A.B;

            using System;

            public class C { public int X; }
            """;

        var result = Run(source, new LexicalNormalizeOptions
        {
            NormalizeFileScopedNamespaces = true,
        });

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.OutputCode, "namespace A.B;");
        Assert.Contains("namespace A.B", result.OutputCode);
        Assert.Contains("{", result.OutputCode);
        Assert.Equal(1, result.Statistics.FileScopedNamespacesNormalized);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LN7_UsingsBeforeDeclaration_StayOutsideBlock()
    {
        const string source = """
            using System;
            namespace A.B;
            public class C { }
            """;

        var result = Run(source, new LexicalNormalizeOptions
        {
            NormalizeFileScopedNamespaces = true,
        });

        Assert.True(result.Changed);
        var outsideUsing = result.OutputCode.IndexOf("using System;", StringComparison.Ordinal);
        var namespaceIndex = result.OutputCode.IndexOf("namespace A.B", StringComparison.Ordinal);
        Assert.True(outsideUsing >= 0 && outsideUsing < namespaceIndex);
    }

    [Fact]
    public void LN7_Idempotent_SecondRunClean()
    {
        const string source = "namespace A.B;\npublic class C { }\n";

        var first = Run(source, new LexicalNormalizeOptions { NormalizeFileScopedNamespaces = true });
        Assert.True(first.Changed);

        var second = Run(first.OutputCode, new LexicalNormalizeOptions { NormalizeFileScopedNamespaces = true });
        Assert.False(second.Changed);
    }
```

- [ ] **Step 2: 运行确认**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo --filter "FullyQualifiedName~LexicalNormalizeTests"`
Expected: `失败: 0`（总数 16）。

- [ ] **Step 3: 提交**

```bash
git add tests/CSharpToJava.Tests/LexicalNormalizeTests.cs
git commit -m "test(preprocessing): C28 LN-5/6/7 审计与 namespace 归一补测（M2）"
```

## Task 15: SampleLexicalNormalizeSolution fixture

**Files:**
- Create: `tests/SampleLexicalNormalizeSolution/`（脚手架 + 源码）

- [ ] **Step 1: 用 dotnet CLI 脚手架生成**

Run（PowerShell，仓库根）：

```powershell
dotnet new sln -n SampleLexicalNormalizeSolution -o tests/SampleLexicalNormalizeSolution
dotnet new classlib -n SampleLexicalNormalizeLib -f net10.0 -o tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLib
dotnet new console -n SampleLexicalNormalizeApp -f net10.0 -o tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeApp
dotnet sln tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeSolution.sln add tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLib/SampleLexicalNormalizeLib.csproj tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeApp/SampleLexicalNormalizeApp.csproj
dotnet add tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeApp/SampleLexicalNormalizeApp.csproj reference tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLib/SampleLexicalNormalizeLib.csproj
```

Expected: 每条命令成功退出；`Class1.cs` 生成后在 Step 3 删除/覆盖。

- [ ] **Step 2: 调整两个 csproj**

覆盖 `SampleLexicalNormalizeLib.csproj` 为：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

`SampleLexicalNormalizeApp.csproj` 同样含 `ImplicitUsings`/`Nullable`（dotnet new console 默认已含，确认即可）。

- [ ] **Step 3: 写 Lib 源文件**

删除脚手架的 `Class1.cs`。新建 `LineAndUsings.cs`：

```csharp
#line 10 "generated.cs"
namespace SampleLexicalNormalizeLib;

public static class LineAndUsings
{
    public static string Describe(int value) => $"value={value}";
}
```

新建 `GlobalUsings.cs`：

```csharp
global using System;
global using static System.StringComparer;
```

新建 `FileScopedFile.cs`：

```csharp
namespace SampleLexicalNormalizeLib;

public sealed class FileScopedFile
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}
```

- [ ] **Step 4: 写 App 源文件**

覆盖 `Program.cs`：

```csharp
using SampleLexicalNormalizeLib;

var item = new FileScopedFile { Id = 3, Name = "x" };
Console.WriteLine(LineAndUsings.Describe(item.Id));
Console.WriteLine(item.Name);
```

- [ ] **Step 5: fixture 基线 build/run**

Run:

```powershell
dotnet run --project tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeApp/SampleLexicalNormalizeApp.csproj
```

Expected 输出：

```text
value=3
x
```

## Task 16: 项目级验收 + ProjectTests 接线

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`
- Create: `tests/CSharpToJava.ProjectTests/LexicalNormalizeProjectTests.cs`

- [ ] **Step 1: csproj 加 Content**

在 ProjectTests csproj 末尾 `</Project>` 之前加：

```xml
  <ItemGroup>
    <!-- C28 lexical-normalize fixture (bin/obj excluded). -->
    <Content Include="..\SampleLexicalNormalizeSolution\**\*.*"
              Exclude="..\SampleLexicalNormalizeSolution\**\bin\**;..\SampleLexicalNormalizeSolution\**\obj\**">
      <Link>SampleLexicalNormalizeSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 2: 写项目级测试**

```csharp
using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for lexical-normalize (C28): LN-1 strips #line trivia,
/// LN-5/6 audit usings, and LN-7 (via the pass option) block-normalizes the
/// file-scoped namespace; the intermediate tree must build and test cleanly,
/// stay idempotent and preserve BOM/byte copies.
/// </summary>
public sealed class LexicalNormalizeProjectTests
{
    private const string PassId = "lexical-normalize";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleLexicalNormalizeSolution");

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath,
        string outputRoot,
        LexicalNormalizeOptions? options = null,
        bool verify = true)
    {
        var registry = PassCatalog.BuildRegistry();
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
            EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LexicalNormalizeOptionsFactory = () => options ?? new LexicalNormalizeOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Fixture_StripsLineDirectives_AndIntermediateTreeVerifies()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-lexical-normalize");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleLexicalNormalizeSolution.sln"), outputRoot);

            Assert.True(result.Success, "lexical-normalize pass failed (see chain diagnostics)");
            Assert.Contains(".cs2j-lexical-normalize-src", result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, ".cs2j-lexical-normalize-src");
            var libFile = Path.Combine(
                intermediate, "SampleLexicalNormalizeLib", "LineAndUsings.cs");
            var text = await File.ReadAllTextAsync(libFile);

            Assert.DoesNotContain("#line", text);
            Assert.Contains("LineAndUsings", text);

            // Idempotence: second run over the intermediate tree changes nothing.
            var second = await RunPassAsync(
                Path.Combine(intermediate, "SampleLexicalNormalizeSolution.sln"),
                Path.Combine(outputRoot, "second"));
            Assert.True(second.Success);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Fixture_WithFileScopedOption_BlockNormalizesNamespace_AndTestsPass()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-ln7-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleLexicalNormalizeSolution.sln"),
                outputRoot,
                new LexicalNormalizeOptions
                {
                    NormalizeFileScopedNamespaces = true,
                });

            Assert.True(result.Success, "LN-7 run failed (see chain diagnostics)");

            var intermediate = Path.Combine(outputRoot, ".cs2j-lexical-normalize-src");
            var fileScopedText = await File.ReadAllTextAsync(Path.Combine(
                intermediate, "SampleLexicalNormalizeLib", "FileScopedFile.cs"));

            Assert.DoesNotContain("namespace SampleLexicalNormalizeLib;", fileScopedText);
            Assert.Contains("namespace SampleLexicalNormalizeLib", fileScopedText);
            Assert.Contains("{", fileScopedText);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanFileWithBom_IsBytePreserved()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-ln-bom-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            const string sourceCode = "public class C { }\n";
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                .GetPreamble().Concat(Encoding.UTF8.GetBytes(sourceCode)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "C.cs"), bytes);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "Lib.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

            var result = await RunPassAsync(Path.Combine(sourceDir, "Lib.csproj"), outputDir);

            Assert.True(result.Success);
            var copied = await File.ReadAllBytesAsync(
                Path.Combine(outputDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.Equal(bytes, copied);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
```

- [ ] **Step 3: 运行项目级测试**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --nologo --filter "FullyQualifiedName~LexicalNormalizeProjectTests"`
Expected: `已通过! - 失败: 0，通过: 3`（含中间树真实 build/test；耗时较长）。

## Task 17: CLI 端到端测试

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/LexicalNormalizeCLITests.cs`

- [ ] **Step 1: 写 CLI 测试（复用 PreprocessVerbCLITests 的进程调用模式）**

```csharp
using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for lexical-normalize: isolated --pass selection,
/// LN-3 ordering failure when run on a source with residual #define, LN-7 via
/// --ln-normalize-file-scoped and extern alias severity override.
/// </summary>
public sealed class LexicalNormalizeCLITests
{
    private static string CliPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(
                    dir, "src", "CSharpToJava.CLI", "bin", "Debug", "net10.0",
                    "CSharpToJava.CLI.dll");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            throw new FileNotFoundException("Could not locate CSharpToJava.CLI.dll.");
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{CliPath}\" {string.Join(' ', args.Select(a => $"\"{a}\""))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string CreateProject(string source)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-ln-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(tempDir, "C.cs"), source);
        return tempDir;
    }

    [Fact]
    public async Task IsolatedPass_StripsLineDirectives_ExitZero()
    {
        var sourceDir = CreateProject("#line 9\npublic class C { public int X; }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.DoesNotContain("#line", rewritten);
            Assert.DoesNotContain("CS2J9001", stderr);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResidualDefine_FailsWith_CS2J5512_ExitOne()
    {
        var sourceDir = CreateProject("#define X\npublic class C { }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("CS2J5512", stderr);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task FileScopedOption_BlockNormalizes_FromVerb()
    {
        var sourceDir = CreateProject("namespace A.B;\npublic class C { }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, _) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize",
                "--ln-normalize-file-scoped",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.Contains("namespace A.B\n", rewritten);
            Assert.Contains("{", rewritten);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: 先 build Debug，再跑 CLI 测试**

Run:

```powershell
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --nologo --filter "FullyQualifiedName~LexicalNormalizeCLITests"
```

Expected: build `0 个错误`；test `失败: 0，通过: 3`。

- [ ] **Step 3: 提交**

```bash
git add tests/SampleLexicalNormalizeSolution tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj tests/CSharpToJava.ProjectTests/LexicalNormalizeProjectTests.cs tests/CSharpToJava.ProjectTests/LexicalNormalizeCLITests.cs
git commit -m "feat(preprocessing): C28 M2 fixture＋项目级/CLI 验收（LN-5/7 全链 verify）"
```

## Task 18: M2 里程碑全量验证

- [ ] **Step 1: Release 全量 build/test**

Run:

```powershell
dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --nologo
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj -c Release --nologo
```

Expected: build `0 个错误`；三个 test 工程全部 `失败: 0`。

---

# M3 — 默认开启评审

## Task 19: sdk 分桶报表与默认开翻转

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`（单行翻转）

- [ ] **Step 1: csharp-sdk 启用扫描**

对 `D:\csharp-sdk` 跑只含本 pass 的转换，按子项产出报表（手工汇总，结论写入本 Task 记录）：

| 桶 | 需要的数字/清单 |
|----|----------------|
| `#line` | 命中文件数与总条数 |
| extern alias | 声明数、使用点数、逐条归因：双程序集冲突（→C42）vs 单程序集（→M3 后语义规则 `X::→global::`） |
| global using | plain/static/alias 分布；重复命中数（CS2J5521） |
| file-scoped | 命中 2 处的 LN-7 试跑结果 |
| CS2J5511/5512 | 若命中，定性：真异常还是输入顺序问题 |

- [ ] **Step 2: 下游共处对照**

选一个代表性工程（fixture 或 sdk 子项目），分别在本 pass 启用/禁用下走完整 convert-project，确认 Java 产物语义等价；E01 及其他 pass 统计零变化。

- [ ] **Step 3: 核对设计 §8 全部六条门槛**

逐条打勾（Release build/test、fixture、下游共处、sdk 报表、基线不退化、文档齐备）；任一未满足则保持默认关，本任务停止。

- [ ] **Step 4: 单行翻转**

PassCatalog 中 lexical-normalize 描述符的 `defaultEnabled: false` 改为 `defaultEnabled: true`；EnabledByDefault 在 PassChainOrderTests 中恢复为 `LockedChain.ToArray()`（删除 Where 排除）。

- [ ] **Step 5: 全量验证**

Run:

```powershell
dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --nologo
```

Expected: 全部 `失败: 0`。

- [ ] **Step 6: 提交**

```bash
git add src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs
git commit -m "feat(preprocessing): C28 lexical-normalize 默认开启（M3 门槛通过）"
```

---

## 附：Self-Review 记录

- **Spec coverage**：设计 §1.5 九个子项 → LN-1 Task 4、LN-2/3 Task 3、LN-4 Task 3、LN-5/6 Task 3+13、LN-7 Task 7+14；接线（设计 §3.4）→ Tasks 10-11；fixture/测试（设计 §7）→ Tasks 8/15-17；门槛（设计 §8）→ Task 19。无缺口。
- **一致性**：类型名在全部任务中统一（`LexicalNormalizeOptions/Result/Diagnostic/Statistics`、`GlobalUsingRecord`、`MutableLexicalStats`、`DirectiveScan`、`LineDirectiveRewriter`、`NamespaceRewriter`、`LexicalNormalizeVerifier`）；诊断码集中于 `LexicalNormalizeCodes`。
- **无占位符**：所有代码步骤含完整代码；命令含预期输出。
