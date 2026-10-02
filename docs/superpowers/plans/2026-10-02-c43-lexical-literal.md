# C43 词法层字面量归一（raw / u8 / verbatim / \e）— Implementation Plan

> **For agentic workers:** 按任务顺序实施本计划；步骤用 checkbox（`- [ ]`）跟踪。执行环境：分支 `dev-3.x`，**禁止 worktree、禁止新建特性分支**（用户 2026-10-02 强制约束），直接在当前工作区逐文件提交。

**Goal:** 在已落地的 `lexical-normalize` pass（C28）内追加 LL-1/LL-2/LL-3/LL-6 四条字面量归一规则、LL-4 插值跳过登记与 LL-5 值保真校验，产出只含常规 C# 词法的可编译中间树；不新增 pass、不翻默认开关、不改转换器。

**Architecture:** Core 在 `LexicalNormalize/Literals/` 新增十文件（分类 Facts + 四个规则 rewriter + 字节工厂 + 单次 Visit 编排件 + 快照 recorder + 保真 verifier）；在 `LexicalNormalizePreprocessor` 的 `NamespaceRewriter` 之后、`ToFullString()`/`UnifyLineEndings` 之前追加一次 `LiteralRewriter.Visit`；发生字面量改写时跳过 C28 LN-1 的 token 序列全等检查（由 LL-5 多重集值守恒承担）。CLI 复用 C28 单适配器（构造不变），preprocess verb 增四个 `--ln-no-*` 细粒度开关。

**Tech Stack:** C# / .NET 10 / Roslyn（`Microsoft.CodeAnalysis.CSharp` syntax 层，无 Compilation/SemanticModel；`SyntaxFactory.Literal` 零算法重建、`Encoding.UTF8.GetBytes` 仅用于字节计算）/ xUnit / CommandLine parser。

**设计**：[2026-09-29-c43-lexical-literal-design.md](../specs/2026-09-29-c43-lexical-literal-design.md)（2026-10-02 按 C28 落地实况二次校订）

---

## 0. 基线裁决（开工前确认）

1. 开工基线：分支 **`dev-3.x`**，HEAD **`ff63b20f`**（C28 M2 fixture/CLI 验收提交）。依次执行：

```powershell
git branch --show-current
git rev-parse --short HEAD
git status --short
```

Expected：第一行输出 `dev-3.x`；第二行输出 `ff63b20f` 或其后继提交（若为后继，按 §2 重新 Read 复核行号）；第三行应无本任务之外的未提交改动——若有，**停止并询问**，不裹挟、不自行 stash/checkout。

2. **禁止 `git worktree`、禁止新建分支**；所有提交在当前工作区完成，逐文件 `git add <具体路径>`（禁止 `git add -A` / `git add .`）；不 push、不建标签（除非用户另行授权）。
3. 不修改 `src/CSharpToJava.Core/Transformers/**`、`HIR/**` 等转换器代码；不新增 pass id/stub；不翻转 `defaultEnabled`（C43 随 C28 保持默认关，M3 只生产证据不翻开关）。
4. 提交节奏：M0（Task 1，探针不入库）→ M1（Tasks 2-10）→ M2（Tasks 11-17）→ M3（Task 18）。每个代码 Task 结束可提交；里程碑末必须全量 Release 验证。
5. 本计划写于设计/计划阶段，**此时不运行构建与测试**；运行发生在实施阶段执行者按各 Task 的 Run/Expected 步骤进行。

## 1. 文件结构（新建/修改总览）

```
src/CSharpToJava.Core/LexicalNormalize/
  Literals/                                        # 【全部新建，10 文件】
    LiteralKinds.cs                               # enum LiteralKind
    LiteralSyntaxFacts.cs                         # token 分类 + 裸 \e 配对扫描
    LiteralRewriteRecorder.cs                     # 期望值/字节快照 + 计数 + Info 聚合
    RawStringLiteralRewriter.cs                   # LL-1（M1）
    EscEscapeLiteralRewriter.cs                   # LL-6（M1）
    LiteralRewriter.cs                            # 编排件：单次 Visit（M1 挂 LL-1/6，M2 补 LL-2/3）
    LiteralNormalizationVerifier.cs               # LL-5（M1 串/字符/residual，M2 补 u8 blob/verbatim）
    ByteLiteralFactory.cs                         # LL-2 字节数组发射（M2）
    Utf8SuffixLiteralRewriter.cs                  # LL-2（M2）
    VerbatimLiteralRewriter.cs                    # LL-3（M2）
# 修改（仅追加性改动，锚点见 §2）：
  LexicalNormalize/LexicalNormalizeModels.cs      # Options +4 bool；Statistics +10 字段；Codes +10 常量
  LexicalNormalize/MutableLexicalStats.cs         # +10 计数字段 + Build 映射
  LexicalNormalize/LexicalNormalizePreprocessor.cs# D11 唯一追加位 + LL-5 调用
  LexicalNormalize/LexicalNormalizeVerifier.cs    # Verify 增加 literalRewritesApplied 形参
src/CSharpToJava.CLI/
  Preprocessing/PassCatalog.cs                    # 仅 CatalogItems/Description 两行文本
  Preprocessing/Adapters/LexicalNormalizePassAdapter.cs # 仅摘要行 + 计数字段
  Program.cs                                      # preprocess verb +4 个 --ln-no-*；factory +4 透传行
tests/
  CSharpToJava.Tests/LexicalLiteralNormalizeTests.cs     # 【新建】Core 单测矩阵
  CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs # 一行断言 "C28" → "C28, C43"
  SampleLexicalNormalizeSolution/
    SampleLexicalNormalizeLib/LiteralSamples.cs   # 【新建】fixture 字面量样本
    SampleLexicalNormalizeLiteralTests/           # 【新建 xUnit 工程，sln add】
      SampleLexicalNormalizeLiteralTests.csproj
      LiteralSamplesTests.cs
  CSharpToJava.ProjectTests/LexicalNormalizeProjectTests.cs # 追加 1 个 Fact
  CSharpToJava.ProjectTests/LexicalNormalizeCLITests.cs     # 追加 3 个 Fact
```

不新建 pass、不建独立 Options 类（四子选项进既有 `LexicalNormalizeOptions`）、不新建独立适配器。

## 2. 集成点行号锚点（2026-10-02 基线 ff63b20f，实施时随 Read 复核）

| 文件 | 位置（基线行号） |
|------|------|
| LexicalNormalizeModels.cs | Options L18-37（四 bool 加在 L30 后）；Statistics L39-58（十字段加在 L56 后）；Codes L68-81（常量加在 L80 后） |
| MutableLexicalStats.cs | 字段区 L5-20（加在 L20 后）；Build() L27-46（映射加在 L44 后） |
| LexicalNormalizePreprocessor.cs | LN-7 块 L69-74（LL 追加位紧随其后）；`UnifyLineEndings` 调用 L76；LN verifier 调用 L90-102 |
| LexicalNormalizeVerifier.cs | `Verify` 签名 L17-21；token-seq 分支 L70（加跳过条件） |
| PassCatalog.cs | lexical-normalize 描述符 L50-62；Description L61、catalogItems L62 |
| LexicalNormalizePassAdapter.cs | 计数字段 L21-25；每文件累加 L60-63；摘要行 L136-139 |
| Program.cs | convert-project factory L214-218（**不动**）；preprocess factory L3682-3688（+4 行）；verb 参数 L4685-4695（+4 参数紧随 L4695 后） |
| ProjectPreprocessorsConsistentTests.cs | lexical 段 L139-146（L145 断言改值） |
| SampleLexicalNormalizeSolution.sln | 现有 Lib + App 两工程；新增第三个（xUnit） |
| ProjectTests csproj | Content glob `..\SampleLexicalNormalizeSolution\**\*.*` 已存在，新文件自动覆盖，**不改 csproj** |

---

# M0 — 孤立代理项 u8 探针（开放问题 3，不入库）

## Task 1: 最小探针锁定 `"\uD800"u8` 编译器行为

**Files:** 无（临时目录探针，用后删除；结论回填设计 D5/§6 与本计划 Task 13）

- [x] **Step 1: 在临时目录创建探针工程**

Run（PowerShell）：

```powershell
$probe = Join-Path $env:TEMP "cs2j-c43-probe"
if (Test-Path $probe) { Remove-Item $probe -Recurse -Force }
dotnet new console -n Probe -f net10.0 -o $probe
Set-Content -Path (Join-Path $probe "Program.cs") -Encoding UTF8 -Value @'
System.ReadOnlySpan<byte> lone = "\uD800"u8;
System.Console.WriteLine(string.Join(",", lone.ToArray()));

System.ReadOnlySpan<byte> paired = "\uD83D\uDE00"u8;
System.Console.WriteLine(string.Join(",", paired.ToArray()));
'@
```

here-string 全为 ASCII 字面文本，无需手工替换：C# 源码中的两个探针字面量即 `"\uD800"u8`（孤立高代理）与 `"\uD83D\uDE00"u8`（成对代理，sanity 基线，期望 F0 9F 98 80）；PowerShell 单引号 here-string 不做转义，反斜杠会原样写入 Program.cs。

- [x] **Step 2: 编译并运行**

Run：

```powershell
dotnet build (Join-Path $probe "Probe.csproj") --nologo -v q
if ($LASTEXITCODE -eq 0) { dotnet run --project (Join-Path $probe "Probe.csproj") }
```

- [x] **Step 3: 按结果二选一拍板，并把结论写进设计文档 D5 行尾注记**

> **M0 探针结论（2026-10-02 实测，分支 B 成立）**：`dotnet build` 失败，`error CS9026: 无法将输入字符串转换为等效的 UTF-8 字节表示形式。 Unable to translate Unicode character \uD800 at index 0 to specified code page.`（成对代理未及验证——孤立代理已先失败，分支判定只依赖孤立项）。裁决：编译器拒绝孤立代理 u8，生产代码**不加特判**，该输入经本 pass 既有 parse Error 路径整文件原样透传/Changed=false；CS2J5583 保持预留常量、全代码不引用；Task 13 的 `LL2_Utf8_LoneSurrogate_FollowsM0ProbeDecision` 采用**报错透传变体**（已回填至 Task 13 代码块）。设计文档 D5 行尾注记已同步。

**分支 A（构建成功）**：记录孤立代理的输出字节。
- 若为 `239,191,189`（EF BF BD）：D5 口径确认为「UTF-8 替换字节」；Task 13 的代理项测试用**替换字节变体**；CS2J5583 保持预留常量、全代码不引用。
- 若为其他字节：以实测为准，先更新设计 D5/§4.4 再继续，不杜撰。
- 成对代理必须输出 `240,159,152,128`（F0 9F 98 80），否则停止排查探针本身。

**分支 B（构建失败，报 CS 错误）**：记录错误码与原文。
- 结论：编译器拒绝孤立代理 u8；生产代码**不加特判**——真实输入经本 pass 的既有 parse Error 路径即整文件原样透传/Changed=false（preprocessor 现有 `tree.GetDiagnostics()` 分支）；CS2J5583 保持预留常量、全代码不引用（设计 §6 已写明该码条件性，报错口径下不需要 pass 自行报错）。
- Task 13 的代理项测试用**报错透传变体**。

- [x] **Step 4: 删除探针，确认工作区零痕迹**

Run：

```powershell
Remove-Item (Join-Path $env:TEMP "cs2j-c43-probe") -Recurse -Force
git status --short
```

Expected：`git status --short` 无探针相关文件（探针本就在 TEMP，工作区只应有随后的文档注记改动）。本任务**没有提交**（如按 Step 3 回填了设计文档，文档改动并入 Task 10 的提交）。

---

# M1 — LL-1 raw + LL-6 \e + 骨架 + 接线（LL-2/3 在 M2 补齐）

## Task 2: Models 与 MutableLexicalStats 追加

**Files:**
- Modify: `src/CSharpToJava.Core/LexicalNormalize/LexicalNormalizeModels.cs`
- Modify: `src/CSharpToJava.Core/LexicalNormalize/MutableLexicalStats.cs`

- [x] **Step 1: Options 追加四个 bool**

在 `LexicalNormalizeOptions` 的 `AuditGlobalUsingDuplicates` 属性（L30）之后插入：

```csharp
    /// <summary>LL-1: normalize non-interpolated raw string literals (default on).</summary>
    public bool NormalizeRawLiterals { get; init; } = true;

    /// <summary>LL-2: expand "x"u8 into new byte[]{ (byte)0xXX, … } (default on).</summary>
    public bool NormalizeUtf8Literals { get; init; } = true;

    /// <summary>LL-3: normalize non-interpolated verbatim @"…" strings (default on).</summary>
    public bool NormalizeVerbatimLiterals { get; init; } = true;

    /// <summary>LL-6: rewrite bare \e to \u001B in regular strings/chars (default on).</summary>
    public bool NormalizeEscEscapes { get; init; } = true;
```

- [x] **Step 2: Statistics 追加十个字段**

在 `LexicalNormalizeStatistics` 的 `FileScopedNamespacesNormalized`（L56）之后、`ErrorsByCode` 之前插入：

```csharp
    public required int RawLiteralsSingleLine { get; init; }
    public required int RawLiteralsMultiLine { get; init; }
    public required int Utf8LiteralsExpanded { get; init; }
    public required int Utf8BytesEmitted { get; init; }
    public required int VerbatimLiteralsNormalized { get; init; }
    public required int EscEscapesNormalized { get; init; }
    public required int InterpolatedRegularSkipped { get; init; }
    public required int InterpolatedVerbatimSkipped { get; init; }
    public required int InterpolatedRawSkipped { get; init; }
    public required int LiteralResidualCount { get; init; }
```

- [x] **Step 3: Codes 追加十个常量**

在 `LexicalNormalizeCodes` 的 `StructuralCheck`（L80）之后插入：

```csharp
    // C43 literal normalization (design §6): CS2J5570-5599 reservation.
    public const string RawLiteralInfo = "CS2J5570";
    public const string EscEscapeInfo = "CS2J5571";
    public const string Utf8LiteralInfo = "CS2J5572";
    public const string VerbatimLiteralInfo = "CS2J5573";
    public const string InterpolatedSkipInfo = "CS2J5574";
    public const string StringValueFidelity = "CS2J5580";
    public const string Utf8ValueFidelity = "CS2J5581";
    public const string LiteralResidualCode = "CS2J5582";
    public const string SurrogateUtf8 = "CS2J5583";
    public const string Utf8LongArrayInfo = "CS2J5590";
```

- [x] **Step 4: MutableLexicalStats 追加字段与 Build 映射**

在 `MutableLexicalStats` 的 `FileScopedNamespacesNormalized;`（L20）之后插入：

```csharp
    public int RawLiteralsSingleLine;
    public int RawLiteralsMultiLine;
    public int Utf8LiteralsExpanded;
    public int Utf8BytesEmitted;
    public int VerbatimLiteralsNormalized;
    public int EscEscapesNormalized;
    public int InterpolatedRegularSkipped;
    public int InterpolatedVerbatimSkipped;
    public int InterpolatedRawSkipped;
    public int LiteralResidualCount;
```

在 `Build()` 的 `FileScopedNamespacesNormalized = FileScopedNamespacesNormalized,`（L44）之后插入：

```csharp
        RawLiteralsSingleLine = RawLiteralsSingleLine,
        RawLiteralsMultiLine = RawLiteralsMultiLine,
        Utf8LiteralsExpanded = Utf8LiteralsExpanded,
        Utf8BytesEmitted = Utf8BytesEmitted,
        VerbatimLiteralsNormalized = VerbatimLiteralsNormalized,
        EscEscapesNormalized = EscEscapesNormalized,
        InterpolatedRegularSkipped = InterpolatedRegularSkipped,
        InterpolatedVerbatimSkipped = InterpolatedVerbatimSkipped,
        InterpolatedRawSkipped = InterpolatedRawSkipped,
        LiteralResidualCount = LiteralResidualCount,
```

- [x] **Step 5: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`（警告可既有）。

## Task 3: LiteralKinds / LiteralSyntaxFacts / LiteralRewriteRecorder

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralKinds.cs`
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralSyntaxFacts.cs`
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralRewriteRecorder.cs`

- [x] **Step 1: 写 LiteralKinds.cs**

```csharp
namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>Syntactic classification bucket for a literal expression (design §4.2).</summary>
internal enum LiteralKind
{
    Other,
    Regular,
    RawSingleLine,
    RawMultiLine,
    Utf8,
    Verbatim,
    EscString,
    EscChar,
}
```

- [x] **Step 2: 写 LiteralSyntaxFacts.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// Token-level classification for LL-1/2/3/6 (design §4.2, D13). Purely
/// syntactic: SyntaxKind, the verbatim '@' prefix and backslash pairing.
/// </summary>
internal static class LiteralSyntaxFacts
{
    public static LiteralKind Classify(LiteralExpressionSyntax node)
    {
        var token = node.Token;

        if (token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken))
        {
            return LiteralKind.RawSingleLine;
        }

        if (token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
        {
            return LiteralKind.RawMultiLine;
        }

        if (token.IsKind(SyntaxKind.Utf8StringLiteralToken))
        {
            return LiteralKind.Utf8;
        }

        if (token.IsKind(SyntaxKind.StringLiteralToken))
        {
            if (token.Text.StartsWith("@\"", StringComparison.Ordinal))
            {
                return LiteralKind.Verbatim;
            }

            return ContainsBareEsc(token.Text) ? LiteralKind.EscString : LiteralKind.Regular;
        }

        if (token.IsKind(SyntaxKind.CharacterLiteralToken)
            && ContainsBareEsc(token.Text))
        {
            return LiteralKind.EscChar;
        }

        return LiteralKind.Other;
    }

    public static bool IsInterpolatedRaw(SyntaxKind startKind)
        => startKind is SyntaxKind.InterpolatedSingleLineRawStringStartToken
            or SyntaxKind.InterpolatedMultiLineRawStringStartToken;

    public static bool IsInterpolatedVerbatim(SyntaxKind startKind)
        => startKind == SyntaxKind.InterpolatedVerbatimStringStartToken;

    /// <summary>
    /// True when <paramref name="text"/> contains a '\e' escape that is not
    /// itself backslash-escaped: the source text "\\e" (two backslashes
    /// followed by letter e) must NOT match. Scans runs of consecutive
    /// backslashes; an odd-length run immediately followed by 'e' is bare.
    /// </summary>
    public static bool ContainsBareEsc(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\')
            {
                continue;
            }

            var run = 1;
            while (i + run < text.Length && text[i + run] == '\\')
            {
                run++;
            }

            if (i + run < text.Length
                && text[i + run] == 'e'
                && (run & 1) == 1)
            {
                return true;
            }

            i += run - 1; // the loop's i++ then moves just past the run
        }

        return false;
    }
}
```

- [x] **Step 3: 写 LiteralRewriteRecorder.cs**

```csharp
namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// Pre-rewrite value/byte snapshots plus per-bucket counters. Feeds the LL-5
/// multiset subset checks, MutableLexicalStats and the per-file aggregated
/// Info diagnostics (design §4.6/§4.7).
/// </summary>
internal sealed class LiteralRewriteRecorder
{
    public int RawSingleLine;
    public int RawMultiLine;
    public int Utf8Expanded;
    public int Utf8Bytes;
    public int VerbatimNormalized;
    public int EscStrings;
    public int EscChars;
    public int InterpolatedRegular;
    public int InterpolatedVerbatim;
    public int InterpolatedRaw;
    public int ResidualCount;

    private readonly List<string> _expectedStringValues = new();
    private readonly List<char> _expectedCharValues = new();
    private readonly List<byte[]> _expectedUtf8Blobs = new();

    public IReadOnlyList<string> ExpectedStringValues => _expectedStringValues;
    public IReadOnlyList<char> ExpectedCharValues => _expectedCharValues;
    public IReadOnlyList<byte[]> ExpectedUtf8Blobs => _expectedUtf8Blobs;

    public int TotalRewrites =>
        RawSingleLine + RawMultiLine + Utf8Expanded + VerbatimNormalized + EscStrings + EscChars;

    public void RecordRaw(bool multiLine, string value)
    {
        if (multiLine)
        {
            RawMultiLine++;
        }
        else
        {
            RawSingleLine++;
        }

        _expectedStringValues.Add(value);
    }

    public void RecordVerbatim(string value)
    {
        VerbatimNormalized++;
        _expectedStringValues.Add(value);
    }

    public void RecordEscString(string value)
    {
        EscStrings++;
        _expectedStringValues.Add(value);
    }

    public void RecordEscChar(char value)
    {
        EscChars++;
        _expectedCharValues.Add(value);
    }

    public void RecordUtf8(byte[] bytes)
    {
        Utf8Expanded++;
        Utf8Bytes += bytes.Length;
        _expectedUtf8Blobs.Add(bytes);
    }

    public void CopyTo(MutableLexicalStats stats)
    {
        stats.RawLiteralsSingleLine += RawSingleLine;
        stats.RawLiteralsMultiLine += RawMultiLine;
        stats.Utf8LiteralsExpanded += Utf8Expanded;
        stats.Utf8BytesEmitted += Utf8Bytes;
        stats.VerbatimLiteralsNormalized += VerbatimNormalized;
        stats.EscEscapesNormalized += EscStrings + EscChars;
        stats.InterpolatedRegularSkipped += InterpolatedRegular;
        stats.InterpolatedVerbatimSkipped += InterpolatedVerbatim;
        stats.InterpolatedRawSkipped += InterpolatedRaw;
        stats.LiteralResidualCount += ResidualCount;
    }

    /// <summary>One aggregated Info diagnostic per non-empty bucket (design §4.7).</summary>
    public List<LexicalNormalizeDiagnostic> BuildAggregatedInfo()
    {
        var result = new List<LexicalNormalizeDiagnostic>();

        if (RawSingleLine + RawMultiLine > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.RawLiteralInfo,
                $"[LL-1] raw string literals normalized: {RawSingleLine + RawMultiLine} " +
                $"({RawMultiLine} multi-line)"));
        }

        if (EscStrings + EscChars > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.EscEscapeInfo,
                $"[LL-6] \\e escapes rewritten: {EscStrings + EscChars} " +
                $"({EscStrings} string(s), {EscChars} char(s))"));
        }

        if (Utf8Expanded > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.Utf8LiteralInfo,
                $"[LL-2] u8 literals expanded: {Utf8Expanded} ({Utf8Bytes} byte(s))"));
        }

        if (VerbatimNormalized > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.VerbatimLiteralInfo,
                $"[LL-3] verbatim string literals normalized: {VerbatimNormalized}"));
        }

        var interpolated = InterpolatedRegular + InterpolatedVerbatim + InterpolatedRaw;
        if (interpolated > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.InterpolatedSkipInfo,
                $"[LL-4] interpolated strings skipped for C23: {interpolated} " +
                $"(regular {InterpolatedRegular}, verbatim {InterpolatedVerbatim}, raw {InterpolatedRaw})"));
        }

        return result;
    }

    private static LexicalNormalizeDiagnostic Info(string code, string message)
        => new(LexicalNormalizeSeverity.Info, code, message);
}
```

- [x] **Step 4: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 4: RawStringLiteralRewriter（LL-1）与 EscEscapeLiteralRewriter（LL-6）

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/RawStringLiteralRewriter.cs`
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/EscEscapeLiteralRewriter.cs`

- [x] **Step 1: 写 RawStringLiteralRewriter.cs**

```csharp
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-1: non-interpolated raw string literal (single/multi-line) → regular
/// string literal. ValueText already carries Roslyn's delimiter stripping,
/// indentation trimming and LF normalization (design D1/D2); zero hand-written
/// decoding lives here.
/// </summary>
internal static class RawStringLiteralRewriter
{
    public static LiteralExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        var kind = LiteralSyntaxFacts.Classify(node);
        if (kind is not (LiteralKind.RawSingleLine or LiteralKind.RawMultiLine))
        {
            return null;
        }

        var value = node.Token.ValueText;
        var token = SyntaxFactory.Literal(value);
        var replacement = SyntaxFactory
            .LiteralExpression(SyntaxKind.StringLiteralExpression, token)
            .WithTriviaFrom(node);

        recorder.RecordRaw(kind == LiteralKind.RawMultiLine, value);
        return replacement;
    }
}
```

- [x] **Step 2: 写 EscEscapeLiteralRewriter.cs**

```csharp
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-6 (open question 5 adjudicated into C43): bare \e inside a *regular*
/// (non-raw, non-verbatim, non-interpolated) string/char literal → \u001B.
/// Both forms rebuild through SyntaxFactory.Literal from ValueText/Value, so
/// Roslyn owns every other escape (design D13).
/// </summary>
internal static class EscEscapeLiteralRewriter
{
    public static ExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        var kind = LiteralSyntaxFacts.Classify(node);

        if (kind == LiteralKind.EscString)
        {
            var value = node.Token.ValueText;
            var replacement = SyntaxFactory
                .LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(value))
                .WithTriviaFrom(node);

            recorder.RecordEscString(value);
            return replacement;
        }

        if (kind == LiteralKind.EscChar)
        {
            var value = (char)node.Token.Value!;
            var replacement = SyntaxFactory
                .LiteralExpression(
                    SyntaxKind.CharacterLiteralExpression,
                    SyntaxFactory.Literal(value))
                .WithTriviaFrom(node);

            recorder.RecordEscChar(value);
            return replacement;
        }

        return null;
    }
}
```

- [x] **Step 3: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 5: LiteralRewriter 编排件（单次 Visit；M1 只挂 LL-1/LL-6 + LL-4 计数）

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralRewriter.cs`

- [x] **Step 1: 写 LiteralRewriter.cs（M1 形态）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// Single-Visit orchestrator (design D11): one tree walk applies the literal
/// rules in order LL-1/LL-6 (LL-2/LL-3 are appended in M2). Interpolated
/// strings are counted by LL-4 and left intact, but base Visit still descends
/// into their holes so independent literals there get rewritten.
/// </summary>
internal sealed class LiteralRewriter : CSharpSyntaxRewriter
{
    private readonly LexicalNormalizeOptions _options;
    private readonly LiteralRewriteRecorder _recorder = new();

    public LiteralRewriter(LexicalNormalizeOptions options)
    {
        _options = options;
    }

    public LiteralRewriteRecorder Recorder => _recorder;

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        ExpressionSyntax? replacement = null;

        if (_options.NormalizeRawLiterals)
        {
            replacement = RawStringLiteralRewriter.TryRewrite(node, _recorder);
        }

        if (replacement is null && _options.NormalizeEscEscapes)
        {
            replacement = EscEscapeLiteralRewriter.TryRewrite(node, _recorder);
        }

        // M2 inserts the LL-2 and LL-3 branches immediately before this return.

        return (SyntaxNode?)replacement ?? node;
    }

    public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
    {
        var startKind = node.StringStartToken.Kind();
        if (LiteralSyntaxFacts.IsInterpolatedRaw(startKind))
        {
            _recorder.InterpolatedRaw++;
        }
        else if (LiteralSyntaxFacts.IsInterpolatedVerbatim(startKind))
        {
            _recorder.InterpolatedVerbatim++;
        }
        else
        {
            _recorder.InterpolatedRegular++;
        }

        // LL-4: the outer node and its text tokens stay untouched; descending
        // still normalizes independent literals used inside the holes.
        return base.VisitInterpolatedStringExpression(node);
    }
}
```

- [x] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 6: LiteralNormalizationVerifier（LL-5；M1 形态：串/字符多重集 + residual + 幂等）

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralNormalizationVerifier.cs`

- [x] **Step 1: 写 LiteralNormalizationVerifier.cs（M1 形态；M2 在 Task 12 局部追加 u8/verbatim）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// Result of LL-5: Fatal means the caller must roll the file back to the
/// original text; non-fatal diagnostics (residual Warning unless --strict)
/// are reported while the rewrite stands.
/// </summary>
internal sealed record LiteralVerificationOutcome(
    bool Fatal,
    IReadOnlyList<LexicalNormalizeDiagnostic> Diagnostics);

/// <summary>
/// LL-5 (design §4.6): reparse the output, then assert value fidelity as an
/// ordered-multiset subset (no span mapping, immune to u8 expansion shifts in
/// M2), residual freedom for enabled rules, and idempotence.
/// </summary>
internal static class LiteralNormalizationVerifier
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    public static LiteralVerificationOutcome Verify(
        string output,
        LexicalNormalizeOptions options,
        LiteralRewriteRecorder recorder)
    {
        var diagnostics = new List<LexicalNormalizeDiagnostic>();

        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(Fatal(
                LexicalNormalizeCodes.StringValueFidelity,
                "[LL-5] rewritten output produced parse errors; file rolled back"));
            return new LiteralVerificationOutcome(true, diagnostics);
        }

        var root = tree.GetCompilationUnitRoot();

        // M2 additionally gates this block on NormalizeVerbatimLiterals.
        if (options.NormalizeRawLiterals || options.NormalizeEscEscapes)
        {
            var actualStrings = Multiset(
                root.DescendantTokens()
                    .Where(t => t.IsKind(SyntaxKind.StringLiteralToken)
                        && !t.Text.StartsWith("@\"", StringComparison.Ordinal))
                    .Select(t => t.ValueText));

            if (!ContainsAll(actualStrings, recorder.ExpectedStringValues))
            {
                diagnostics.Add(Fatal(
                    LexicalNormalizeCodes.StringValueFidelity,
                    "[LL-1/LL-6] regular-string value multiset is missing expected value(s); file rolled back"));
            }

            var actualChars = Multiset(
                root.DescendantTokens()
                    .Where(t => t.IsKind(SyntaxKind.CharacterLiteralToken))
                    .Select(t => (char)t.Value!));

            if (!ContainsAll(actualChars, recorder.ExpectedCharValues))
            {
                diagnostics.Add(Fatal(
                    LexicalNormalizeCodes.StringValueFidelity,
                    "[LL-6] char value multiset is missing expected value(s); file rolled back"));
            }
        }

        // M2 inserts the u8 blob subset block here.

        recorder.ResidualCount = CountResidual(root, options);
        if (recorder.ResidualCount > 0)
        {
            var severity = options.Strict
                ? LexicalNormalizeSeverity.Error
                : LexicalNormalizeSeverity.Warning;
            diagnostics.Add(new LexicalNormalizeDiagnostic(
                severity,
                LexicalNormalizeCodes.LiteralResidualCode,
                $"[LL-5] residual raw/\\e lexical forms remain: {recorder.ResidualCount}"));
        }

        var secondPass = new LiteralRewriter(options);
        secondPass.Visit(root);
        if (secondPass.Recorder.TotalRewrites > 0)
        {
            diagnostics.Add(Fatal(
                LexicalNormalizeCodes.LiteralResidualCode,
                "[LL-5] idempotence violation: a second literal pass rewrote node(s); file rolled back"));
        }

        var fatal = diagnostics.Any(d => d.Severity == LexicalNormalizeSeverity.Error);
        return new LiteralVerificationOutcome(fatal, diagnostics);
    }

    /// <summary>Residual scan for the rules enabled in M1 (M2 adds u8/verbatim).</summary>
    internal static int CountResidual(CompilationUnitSyntax root, LexicalNormalizeOptions options)
    {
        var residual = 0;

        foreach (var token in root.DescendantTokens())
        {
            if (options.NormalizeRawLiterals
                && (token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                    || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)))
            {
                residual++;
                continue;
            }

            // M2 inserts the u8 and verbatim residual branches here.

            if (options.NormalizeEscEscapes
                && ((token.IsKind(SyntaxKind.StringLiteralToken)
                        && !token.Text.StartsWith("@\"", StringComparison.Ordinal)
                        && LiteralSyntaxFacts.ContainsBareEsc(token.Text))
                    || (token.IsKind(SyntaxKind.CharacterLiteralToken)
                        && LiteralSyntaxFacts.ContainsBareEsc(token.Text))))
            {
                residual++;
            }
        }

        return residual;
    }

    private static Dictionary<T, int> Multiset<T>(
        IEnumerable<T> values,
        IEqualityComparer<T>? comparer = null)
    {
        var result = new Dictionary<T, int>(comparer ?? EqualityComparer<T>.Default);
        foreach (var value in values)
        {
            result[value] = result.TryGetValue(value, out var count) ? count + 1 : 1;
        }

        return result;
    }

    /// <summary>
    /// Multiset subset with consumption: every expected value must be matched
    /// by a distinct actual occurrence; pre-existing equal literals are allowed.
    /// </summary>
    private static bool ContainsAll<T>(Dictionary<T, int> actual, IReadOnlyList<T> expected)
    {
        foreach (var value in expected)
        {
            if (!actual.TryGetValue(value, out var remaining) || remaining <= 0)
            {
                return false;
            }

            actual[value] = remaining - 1;
        }

        return true;
    }

    private static LexicalNormalizeDiagnostic Fatal(string code, string message)
        => new(LexicalNormalizeSeverity.Error, code, message);
}
```

- [x] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 7: Preprocessor 接线 + LN token-seq 跳过条件

**Files:**
- Modify: `src/CSharpToJava.Core/LexicalNormalize/LexicalNormalizePreprocessor.cs`
- Modify: `src/CSharpToJava.Core/LexicalNormalize/LexicalNormalizeVerifier.cs`

- [x] **Step 1: LexicalNormalizeVerifier 增加 literalRewritesApplied 形参**

把 `Verify` 签名（L17-21）改为：

```csharp
    public static bool Verify(
        string output,
        CompilationUnitSyntax originalRoot,
        LexicalNormalizeOptions options,
        bool literalRewritesApplied,
        out LexicalNormalizeDiagnostic? failure)
```

把 token 序列全等分支（L70）：

```csharp
        else if (options.StripLineDirectives && !TokenSequenceEqual(originalRoot, outputRoot))
```

改为：

```csharp
        else if (options.StripLineDirectives
            && !literalRewritesApplied
            && !TokenSequenceEqual(originalRoot, outputRoot))
```

即：豁免**只认 recorder 实际改写计数 > 0**（由调用方传入），与 LL 选项开关无关——纯 LN-1 场景（零字面量命中）该安全网照常生效（设计风险 8）。

- [x] **Step 2: Preprocessor 顶部加 using**

在 `LexicalNormalizePreprocessor.cs` 现有 using 区（L1-2）加：

```csharp
using CSharpToJava.Core.LexicalNormalize.Literals;
```

- [x] **Step 3: 在 LN-7 块之后插入 LL 追加位**

在 `Normalize` 的 LN-7 块（L69-74）之后、`var output = UnifyLineEndings(...)`（L76）之前插入：

```csharp
        var anyLiteralRule = options.NormalizeRawLiterals
            || options.NormalizeUtf8Literals
            || options.NormalizeVerbatimLiterals
            || options.NormalizeEscEscapes;

        LiteralRewriteRecorder? literalRecorder = null;
        if (anyLiteralRule)
        {
            var literalRewriter = new LiteralRewriter(options);
            working = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)
                literalRewriter.Visit(working);
            literalRecorder = literalRewriter.Recorder;
            literalRecorder.CopyTo(stats);
            diagnostics.AddRange(literalRecorder.BuildAggregatedInfo());
        }
```

- [x] **Step 4: LN verifier 调用传入改写位，并在其后追加 LL-5**

把 LN verifier 调用（L90）：

```csharp
        if (!LexicalNormalizeVerifier.Verify(output, root, options, out var failure)
            && failure != null)
```

改为：

```csharp
        var literalRewrote = literalRecorder is not null && literalRecorder.TotalRewrites > 0;

        if (!LexicalNormalizeVerifier.Verify(
                output, root, options, literalRewrote, out var failure)
            && failure != null)
```

紧接该 if 块的回退结束大括号之后、`stats.FilesTransformed = 1;`（L104）之前插入：

```csharp
        if (literalRecorder is not null)
        {
            var literalOutcome = LiteralNormalizationVerifier.Verify(
                output, options, literalRecorder);
            diagnostics.AddRange(literalOutcome.Diagnostics);

            if (literalOutcome.Fatal)
            {
                foreach (var error in literalOutcome.Diagnostics
                             .Where(d => d.Severity == LexicalNormalizeSeverity.Error))
                {
                    stats.AddError(error.Code);
                }

                stats.FilesDirtyNoRewrite = 1;
                return new LexicalNormalizeResult(
                    sourceCode,
                    Changed: false,
                    Materialize(diagnostics, options),
                    stats.Build(),
                    scan.GlobalUsings);
            }
        }
```

- [x] **Step 5: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 8: CatalogItems/Description 与一致性断言

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`
- Modify: `tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs`

- [x] **Step 1: PassCatalog 两行文本更新**

把描述符（L61-62）：

```csharp
            description: "Strip #line directives, audit #error/extern-alias/global-using forms and optionally normalize file-scoped namespaces; pure-syntax idempotent.",
            catalogItems: "C28"),
```

改为：

```csharp
            description: "Strip #line directives, audit #error/extern-alias/global-using forms and optionally normalize file-scoped namespaces; pure-syntax idempotent; normalize raw/u8/verbatim/\\e literals to regular C# lexical forms.",
            catalogItems: "C28, C43"),
```

其余字段（Id/Stage/Status/defaultEnabled:false/RunAfter/verify 开关/目录名）一律不动。

- [x] **Step 2: ProjectPreprocessorsConsistentTests 断言改值**

把 L145：

```csharp
            Assert.Equal("C28", lexicalDescriptor.CatalogItems);
```

改为：

```csharp
            Assert.Equal("C28, C43", lexicalDescriptor.CatalogItems);
```

链序 LockedChain/EnabledByDefault（PassChainOrderTests）**不动**：pass id 未变。

- [x] **Step 3: 运行一致性测试**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo --filter "FullyQualifiedName~ProjectPreprocessorsConsistentTests"`
Expected: `失败: 0`。

## Task 9: Core 单测（M1 切片：矩阵 1/2/3/4/10/12/13/14 部分 + C28 双向回归）

**Files:**
- Create: `tests/CSharpToJava.Tests/LexicalLiteralNormalizeTests.cs`

- [x] **Step 1: 写 M1 测试文件**

```csharp
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.LexicalNormalize.Literals;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// C43 Core matrix (M1 slice): LL-1 raw single/multi-line/indentation/newline
/// contract, LL-6 \e scope, LL-5 fidelity rollback, trivia, option gates and
/// the two-direction C28 token-sequence safety-net regression (design §7.1).
/// </summary>
public sealed class LexicalLiteralNormalizeTests
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    private static LexicalNormalizeResult Run(string source, LexicalNormalizeOptions? options = null)
        => new LexicalNormalizePreprocessor().Normalize(source, options);

    private static LiteralExpressionSyntax ParseLiteralExpression(string literalText)
    {
        var root = CSharpSyntaxTree.ParseText(
            "public class C { object x = " + literalText + "; }", ParseOptions)
            .GetCompilationUnitRoot();
        return root.DescendantNodes().OfType<LiteralExpressionSyntax>().First();
    }

    [Fact]
    public void LL1_RawSingleLine_QuotesBackslashesAndLongerDelimiters_AreRegularStrings()
    {
        // 外层定界符必须是 5 引号：Q 的源码里出现连续 4 引号，4 引号定界会被提前终结。
        const string source = """""
            public class C
            {
                public string A = """hello""";
                public string B = """a"b""";
                public string P = """C:\temp\a""";
                public string Q = """"a"""b"""";
            }
            """"";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(4, result.Statistics.RawLiteralsSingleLine);
        Assert.DoesNotContain("\"\"\"", result.OutputCode);
        Assert.Contains("\"hello\"", result.OutputCode);
        Assert.Contains("\"a\\\"b\"", result.OutputCode);
        Assert.Contains("\"C:\\\\temp\\\\a\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_ZeroColumnClosing_TrimsNothing()
    {
        var source = "public class C\n{\n    public string Z = \"\"\"\na\n\"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"a\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_TwoColumnClosing_DeepLineKeepsExtraIndent()
    {
        var source = "public class C\n{\n    public string T = \"\"\"\n  x\n    y\n  \"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"x\\n  y\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_FourColumnClosing_TrimsCommonPrefix()
    {
        var source = "public class C\n{\n    public string F = \"\"\"\n    p\n    \"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"p\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_TabColumnClosing_TrimsTabPrefix()
    {
        var source = "public class C\n{\n\tpublic string T = \"\"\"\n\tt\n\t\"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"t\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_BlankLine_IsExemptFromIndentationPrefix()
    {
        var source = "public class C\n{\n    public string B = \"\"\"\n    a\n\n    b\n    \"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"a\\n\\nb\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void LL1_RawMultiLine_AllSourceNewlines_NormalizeToValueLf(string newline)
    {
        var source = "public class C { public string X = "
            + "\"\"\"" + newline + "  a" + newline + "  " + "\"\"\"" + "; }";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"a\"", result.OutputCode);
        Assert.DoesNotContain("a\\r", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawIndentationViolation_IsParseError_FilePassesThroughUnchanged()
    {
        // Closing quotes at column 4 while the content line only has 2 spaces.
        var source = "public class C { public string X = "
            + "\"\"\"\n  a\n    \"\"\"" + "; }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Severity == LexicalNormalizeSeverity.Error);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void LL6_BareEsc_InStringAndChar_BecomesUnicodeEscape_OtherFormsUntouched()
    {
        const string source = """"
            public class C
            {
                public string A = "a\eb";
                public char B = '\e';
                public string C = "\\e";
                public string D = @"\e";
                public string E = """\e""";
                public string F(int y) => $"x\e{y}";
            }
            """";

        // 只开 LL-6：raw/verbatim 显式关掉（verbatim 在 M2 才落地，提前关掉可保
        // C/D/E/F「未被本规则触碰」的断言在 M1 与 M2 两个里程碑下都成立）。
        var result = Run(source, new LexicalNormalizeOptions
        {
            NormalizeRawLiterals = false,
            NormalizeUtf8Literals = false,
            NormalizeVerbatimLiterals = false,
        });

        Assert.True(result.Changed);
        Assert.Contains("\"a\\u001bb\"", result.OutputCode);
        Assert.Contains("'\\u001b'", result.OutputCode);
        Assert.Equal(2, result.Statistics.EscEscapesNormalized);
        Assert.Contains("\"\\\\e\"", result.OutputCode);
        Assert.Contains("@\"\\e\"", result.OutputCode);
        Assert.Contains("\"\"\"\\e\"\"\"", result.OutputCode);
        Assert.Contains("$\"x\\e{y}\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL5_MissingExpectedStringValue_FailsFidelity_With5580()
    {
        var recorder = new LiteralRewriteRecorder();
        recorder.RecordRaw(multiLine: false, "missing-value");

        var outcome = LiteralNormalizationVerifier.Verify(
            "public class C { }",
            new LexicalNormalizeOptions(),
            recorder);

        Assert.True(outcome.Fatal);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "CS2J5580");
    }

    [Fact]
    public void LL_RewrittenLiteral_PreservesLeadingAndTrailingTrivia()
    {
        const string source = """"
            // leading comment
            public class C
            { // inner
                public string A = """x"""; // tail
            }
            """";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Contains("// leading comment", result.OutputCode);
        Assert.Contains("// inner", result.OutputCode);
        Assert.Contains("// tail", result.OutputCode);
        Assert.Contains("public string A = \"x\";", result.OutputCode);
    }

    [Fact]
    public void LL1_LL6_DisableSwitches_LeaveFormsUntouched()
    {
        const string source = "public class C { public string A = \"\"\"x\"\"\"; public string B = \"a\\eb\"; }\n";

        var off = Run(source, new LexicalNormalizeOptions
        {
            NormalizeRawLiterals = false,
            NormalizeEscEscapes = false,
        });
        Assert.False(off.Changed);
        Assert.Equal(source, off.OutputCode);

        var rawOnly = Run(source, new LexicalNormalizeOptions { NormalizeEscEscapes = false });
        Assert.True(rawOnly.Changed);
        Assert.Equal(1, rawOnly.Statistics.RawLiteralsSingleLine);
        Assert.Equal(0, rawOnly.Statistics.EscEscapesNormalized);
        Assert.Contains("\"x\"", rawOnly.OutputCode);
        Assert.Contains("\"a\\eb\"", rawOnly.OutputCode);

        var escOnly = Run(source, new LexicalNormalizeOptions { NormalizeRawLiterals = false });
        Assert.True(escOnly.Changed);
        Assert.Equal(0, escOnly.Statistics.RawLiteralsSingleLine);
        Assert.Equal(1, escOnly.Statistics.EscEscapesNormalized);
        Assert.Contains("\"\"\"x\"\"\"", escOnly.OutputCode);
        Assert.Contains("\"a\\u001bb\"", escOnly.OutputCode);
    }

    [Fact]
    public void LN1_TokenSequenceSafetyNet_StillRuns_WhenNoLiteralRewriteOccurred()
    {
        var original = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(
            "public class C { int a; }", ParseOptions).GetRoot();
        var output = "public class C { int b; }";

        var (passes, failure) =
            LexicalNormalizeVerifier_HasTokenSequenceCoverage(output, original);

        Assert.False(passes);
        Assert.NotNull(failure);
        Assert.Equal(LexicalNormalizeCodes.StructuralCheck, failure!.Code);
    }

    [Fact]
    public void LN1_TokenSequenceSafetyNet_IsRelaxed_WhenLiteralRewriteOccurred()
    {
        var original = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(
            "public class C { int a; }", ParseOptions).GetRoot();
        var output = "public class C { int b; }";

        var (passes, failure) =
            LexicalNormalizeVerifier_HasTokenSequenceCoverage(output, original, literalRewrote: true);

        Assert.True(passes);
        Assert.Null(failure);
    }

    private static (bool Passes, LexicalNormalizeDiagnostic? Failure)
        LexicalNormalizeVerifier_HasTokenSequenceCoverage(
            string output,
            CompilationUnitSyntax original,
            bool literalRewrote = false)
    {
        var passes = LexicalNormalizeVerifier.Verify(
            output,
            original,
            new LexicalNormalizeOptions { NormalizeFileScopedNamespaces = false },
            literalRewrote,
            out var failure);

        return (passes, failure);
    }
}
```

说明（实施者注意，不是占位符）：
- 五个多行 raw 用例全部用字符串拼接（`"\n"`、空格、`"\t"`）确定性构造源码，规避「把多行 raw 嵌进外层 raw」的定界符歧义；闭合引号列分别为 0/2/4/tab，另加一条全空白行豁免用例，每条只含 1 个多行 raw、各断言 `RawLiteralsMultiLine == 1`。
- `LL1_LL6_DisableSwitches` 验证两个单向闸门：`rawOnly`（只关 `\e`）断言 raw 已归一成 `"x"`、`"a\eb"` 原样保留；`escOnly`（只关 raw）断言 `"""x"""` 原样保留、`\e` 已归一成 `"a\u001bb"`。
- **2026-10-02 M1 实测修正**（Roslyn 5.0.0 探针）：① 多行 raw 闭定界符前的最后换行**不进值**（`"""\na\n"""` 的 ValueText 为 `a`），6 个多行 raw 用例期望值不含尾 `\n`（设计 D2④ 已同步勘误）；② `SyntaxFactory.Literal` 对控制字符发射**小写** hex 转义（ESC → `\u001b`），LL-6 断言用小写 b。

- [x] **Step 2: 运行新测试，确认全绿**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo --filter "FullyQualifiedName~LexicalLiteralNormalizeTests"`
Expected: `失败: 0`（14 个测试方法；含 1 个 Theory 的 3 行 InlineData 展开，共 16 个用例）。

- [x] **Step 3: C28 既有 16 个单测零改动回归**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo --filter "FullyQualifiedName~LexicalNormalizeTests"`
Expected: `失败: 0`（C28 既有 16 个全绿；追加式集成的硬要求）。

- [x] **Step 4: 提交**

```bash
git add src/CSharpToJava.Core/LexicalNormalize tests/CSharpToJava.Tests/LexicalLiteralNormalizeTests.cs src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs
git commit -m "feat(preprocessing): C43 M1 LL-1 raw/LL-6 \\e 归一骨架、接线与 CatalogItems 更新（默认关）"
```

## Task 10: M1 里程碑验证

- [x] **Step 1: Debug 全量 build + Tests**

Run:

```powershell
dotnet build CSharpToJavaConverter.slnx --nologo -v q
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo
```

Expected: build `0 个错误`；Tests `失败: 0`。

- [x] **Step 2: `--list-passes` 文本确认**

Run: `dotnet src/CSharpToJava.CLI/bin/Debug/net10.0/CSharpToJava.CLI.dll preprocess --list-passes`
Expected: lexical-normalize 行含 `rules=C28, C43`，状态仍为 `off`。

---

# M2 — LL-2 u8 + LL-3 verbatim + CLI 开关 + fixture 验收

## Task 11: ByteLiteralFactory 与 Utf8SuffixLiteralRewriter（LL-2）

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/ByteLiteralFactory.cs`
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/Utf8SuffixLiteralRewriter.cs`
- Modify: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralRewriter.cs`
- Modify: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralNormalizationVerifier.cs`

- [ ] **Step 1: 写 ByteLiteralFactory.cs**

```csharp
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-2 emission: byte blob → new byte[]{ (byte)0xXX, … } (empty blob →
/// new byte[]{}). Hex two-digit uppercase, explicit cast, no Encoding symbol
/// in the emitted tree (design D4). UTF-8 computation happens at the caller.
/// </summary>
internal static class ByteLiteralFactory
{
    public static ArrayCreationExpressionSyntax Create(IReadOnlyList<byte> bytes)
    {
        var rankSpecifier = SyntaxFactory.ArrayRankSpecifier(
            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                SyntaxFactory.OmittedArraySizeExpression()));
        var arrayType = SyntaxFactory.ArrayType(PredefinedByte())
            .WithRankSpecifiers(SyntaxFactory.SingletonList(rankSpecifier));

        var elements = new List<ExpressionSyntax>(bytes.Count);
        foreach (var value in bytes)
        {
            var literalToken = SyntaxFactory.Literal("0x" + value.ToString("X2"), (int)value);
            var numeric = SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression, literalToken);
            elements.Add(SyntaxFactory.CastExpression(PredefinedByte(), numeric));
        }

        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.ArrayInitializerExpression,
            SyntaxFactory.SeparatedList(elements));

        return SyntaxFactory.ArrayCreationExpression(arrayType, initializer);
    }

    private static PredefinedTypeSyntax PredefinedByte()
        => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ByteKeyword));
}
```

- [ ] **Step 2: 写 Utf8SuffixLiteralRewriter.cs**

```csharp
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-2: "x"u8 → byte[] array carrying the compile-time UTF-8 blob (design
/// D4/D5). Byte computation uses Encoding.UTF8 (Core-side only); the M0 probe
/// (Task 1) locks the lone-surrogate behavior. C# 11 implicit span conversion
/// covers ReadOnlySpan&lt;byte&gt; use sites; verify build gates them.
/// </summary>
internal static class Utf8SuffixLiteralRewriter
{
    public static ArrayCreationExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        if (LiteralSyntaxFacts.Classify(node) != LiteralKind.Utf8)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(node.Token.ValueText);
        var replacement = ByteLiteralFactory.Create(bytes).WithTriviaFrom(node);
        recorder.RecordUtf8(bytes);
        return replacement;
    }
}
```

- [ ] **Step 3: 编排件挂 LL-2**

在 `LiteralRewriter.VisitLiteralExpression` 中，把：

```csharp
        // M2 inserts the LL-2 and LL-3 branches immediately before this return.

        return (SyntaxNode?)replacement ?? node;
```

替换为：

```csharp
        if (replacement is null && _options.NormalizeUtf8Literals)
        {
            replacement = Utf8SuffixLiteralRewriter.TryRewrite(node, _recorder);
        }

        // Task 12 inserts the LL-3 branch immediately before this return.

        return (SyntaxNode?)replacement ?? node;
```

- [ ] **Step 4: Verifier 增加 u8 blob 多重集校验**

在 `LiteralNormalizationVerifier.Verify` 中，把占位注释 `// M2 inserts the u8 blob subset block here.` 替换为：

```csharp
        if (options.NormalizeUtf8Literals && recorder.ExpectedUtf8Blobs.Count > 0)
        {
            var actualBlobs = Multiset(
                root.DescendantNodes()
                    .OfType<ArrayCreationExpressionSyntax>()
                    .Select(TryExtractByteBlob)
                    .Where(blob => blob is not null)
                    .Select(blob => BlobKey(blob!)),
                StringComparer.Ordinal);

            var expectedBlobs = recorder.ExpectedUtf8Blobs.Select(BlobKey).ToList();
            if (!ContainsAll(actualBlobs, expectedBlobs))
            {
                diagnostics.Add(Fatal(
                    LexicalNormalizeCodes.Utf8ValueFidelity,
                    "[LL-2] UTF-8 byte blob multiset is missing expected sequence(s); file rolled back"));
            }
        }
```

在 `CountResidual` 的 `// M2 inserts the u8 and verbatim residual branches here.` 处先插入 u8 分支：

```csharp
            if (options.NormalizeUtf8Literals
                && token.IsKind(SyntaxKind.Utf8StringLiteralToken))
            {
                residual++;
                continue;
            }
```

并在类中 `BlobKey`/`TryExtractByteBlob` 两个私有方法（加在 `Fatal` 之前）：

```csharp
    private static string BlobKey(IReadOnlyList<byte> bytes)
        => string.Concat(bytes.Select(b => b.ToString("X2")));

    /// <summary>
    /// Recognizes only arrays shaped exactly like ByteLiteralFactory output:
    /// byte[] with omitted rank size and an initializer whose elements are all
    /// (byte) numeric constants. Anything else is a pre-existing array and is
    /// ignored (multiset subset allows extra same-shaped arrays).
    /// </summary>
    private static byte[]? TryExtractByteBlob(ArrayCreationExpressionSyntax array)
    {
        if (array.Type.ElementType is not PredefinedTypeSyntax elementType
            || !elementType.Keyword.IsKind(SyntaxKind.ByteKeyword))
        {
            return null;
        }

        if (array.Type.RankSpecifiers.Count != 1
            || array.Type.RankSpecifiers[0].Sizes.Count != 1
            || array.Type.RankSpecifiers[0].Sizes[0] is not OmittedArraySizeExpressionSyntax
            || array.Initializer is null)
        {
            return null;
        }

        var bytes = new List<byte>();
        foreach (var expression in array.Initializer.Expressions)
        {
            if (expression is not CastExpressionSyntax cast
                || cast.Type is not PredefinedTypeSyntax castType
                || !castType.Keyword.IsKind(SyntaxKind.ByteKeyword)
                || cast.Expression is not LiteralExpressionSyntax numeric
                || !numeric.IsKind(SyntaxKind.NumericLiteralExpression)
                || numeric.Token.Value is not int value
                || value < 0
                || value > 255)
            {
                return null;
            }

            bytes.Add((byte)value);
        }

        return bytes.ToArray();
    }
```

- [ ] **Step 5: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 12: VerbatimLiteralRewriter（LL-3）与 Verifier 收口

**Files:**
- Create: `src/CSharpToJava.Core/LexicalNormalize/Literals/VerbatimLiteralRewriter.cs`
- Modify: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralRewriter.cs`
- Modify: `src/CSharpToJava.Core/LexicalNormalize/Literals/LiteralNormalizationVerifier.cs`

- [ ] **Step 1: 写 VerbatimLiteralRewriter.cs**

```csharp
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-3: non-interpolated verbatim string (@"…") → regular string literal.
/// ValueText keeps source newlines verbatim (no LF normalization — design D6,
/// deliberately different from raw D2⑤); SyntaxFactory.Literal emits them as
/// \r\n escapes. @identifiers are a different lexical family and never reach
/// this classifier.
/// </summary>
internal static class VerbatimLiteralRewriter
{
    public static LiteralExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        if (LiteralSyntaxFacts.Classify(node) != LiteralKind.Verbatim)
        {
            return null;
        }

        var value = node.Token.ValueText;
        var replacement = SyntaxFactory
            .LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(value))
            .WithTriviaFrom(node);

        recorder.RecordVerbatim(value);
        return replacement;
    }
}
```

- [ ] **Step 2: 编排件挂 LL-3**

在 `LiteralRewriter.VisitLiteralExpression` 中，把：

```csharp
        // Task 12 inserts the LL-3 branch immediately before this return.

        return (SyntaxNode?)replacement ?? node;
```

替换为：

```csharp
        if (replacement is null && _options.NormalizeVerbatimLiterals)
        {
            replacement = VerbatimLiteralRewriter.TryRewrite(node, _recorder);
        }

        return (SyntaxNode?)replacement ?? node;
```

- [ ] **Step 3: Verifier 打开 verbatim 口径**

字符串子集门（M1 注释为 `// M2 additionally gates this block on NormalizeVerbatimLiterals.` 的那行）：

```csharp
        if (options.NormalizeRawLiterals || options.NormalizeEscEscapes)
```

改为：

```csharp
        if (options.NormalizeRawLiterals || options.NormalizeEscEscapes
            || options.NormalizeVerbatimLiterals)
```

在 `CountResidual` 的 u8 分支之后、esc 分支之前插入：

```csharp
            if (options.NormalizeVerbatimLiterals
                && token.IsKind(SyntaxKind.StringLiteralToken)
                && token.Text.StartsWith("@\"", StringComparison.Ordinal))
            {
                residual++;
                continue;
            }
```

注意：esc 分支已显式排除 `@"` 前缀，两分支互斥。

- [ ] **Step 4: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 13: Core 单测 M2 切片（矩阵 5/6/7/8/9/11/14）

**Files:**
- Modify: `tests/CSharpToJava.Tests/LexicalLiteralNormalizeTests.cs`

- [ ] **Step 1: 在文件末尾最后一个 `}` 之前追加 M2 测试**

```csharp
    [Fact]
    public void LL2_Utf8_AsciiAndEmpty_EmitHexCasts_WithExactByteCounts()
    {
        const string source = """
            public class C
            {
                public void M()
                {
                    System.ReadOnlySpan<byte> a = "abc"u8;
                    System.ReadOnlySpan<byte> e = ""u8;
                    System.Console.WriteLine(a.Length + e.Length);
                }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.Utf8LiteralsExpanded);
        Assert.Equal(3, result.Statistics.Utf8BytesEmitted);
        Assert.Contains("(byte)0x61", result.OutputCode);
        Assert.Contains("(byte)0x62", result.OutputCode);
        Assert.Contains("(byte)0x63", result.OutputCode);
        Assert.Contains("new byte[]", result.OutputCode);
        Assert.DoesNotContain("\"u8", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL2_ByteLiteralFactory_EmitsExpectedCastSyntax()
    {
        var array = ByteLiteralFactory.Create(new byte[] { 0x61, 0xFF });

        Assert.Equal(2, array.Initializer!.Expressions.Count);
        Assert.Equal("(byte)0x61", array.Initializer.Expressions[0].ToString());
        Assert.Equal("(byte)0xFF", array.Initializer.Expressions[1].ToString());
        Assert.Empty(ByteLiteralFactory.Create(System.Array.Empty<byte>()).Initializer!.Expressions);
    }

    [Fact]
    public void LL2_Utf8_ChineseAndEmoji_MatchEncoding_AndEmitNoBom()
    {
        var chinese = ParseLiteralExpression("\"中\"u8");
        var emoji = ParseLiteralExpression("\"😀\"u8");
        var recorder = new LiteralRewriteRecorder();

        var chineseArray = Utf8SuffixLiteralRewriter.TryRewrite(chinese, recorder)!;
        var emojiArray = Utf8SuffixLiteralRewriter.TryRewrite(emoji, recorder)!;

        Assert.Equal(new byte[] { 0xE4, 0xB8, 0xAD }, recorder.ExpectedUtf8Blobs[0]);
        Assert.Equal(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, recorder.ExpectedUtf8Blobs[1]);
        Assert.Equal(2, recorder.Utf8Expanded);
        Assert.Equal(7, recorder.Utf8Bytes);
        Assert.DoesNotContain("0xEF", chineseArray.ToString());
        Assert.DoesNotContain("0xEF", emojiArray.ToString());
    }

    [Fact]
    public void LL2_Utf8_HalfRangeBytes_7F_80_FF_ArePreserved()
    {
        var node = ParseLiteralExpression("\"\u007F\u0080\u00FF\"u8");
        var recorder = new LiteralRewriteRecorder();

        Utf8SuffixLiteralRewriter.TryRewrite(node, recorder);

        Assert.Equal(new byte[] { 0x7F, 0xC2, 0x80, 0xC3, 0xBF }, recorder.ExpectedUtf8Blobs[0]);
    }

    [Fact]
    public void LL2_Utf8_LoneSurrogate_FollowsM0ProbeDecision()
    {
        var source = "public class C { object x = \"\uD800\"u8; }\n";

        // M0 probe (Task 1) decided branch B: the compiler rejects lone
        // surrogates in u8 literals (CS9026), surfaced by the pass's
        // existing parse-error path — the file rolls back unchanged.
        var errorResult = Run(source);
        Assert.False(errorResult.Changed);
        Assert.Equal(source, errorResult.OutputCode);
        Assert.Contains(errorResult.Diagnostics,
            d => d.Severity == LexicalNormalizeSeverity.Error);
    }

    [Fact]
    public void LL5_MissingExpectedUtf8Blob_FailsFidelity_With5581()
    {
        var recorder = new LiteralRewriteRecorder();
        recorder.RecordUtf8(new byte[] { 0x01, 0x02 });

        var outcome = LiteralNormalizationVerifier.Verify(
            "public class C { }",
            new LexicalNormalizeOptions(),
            recorder);

        Assert.True(outcome.Fatal);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "CS2J5581");
    }

    [Fact]
    public void LL3_Verbatim_DoubledQuotes_Crlf_AndBackslashes_AreRegularStrings()
    {
        var source = "public class C {\n"
            + "public string A = @\"a\"\"b\";\n"
            + "public string B = @\"line1\r\nline2\";\n"
            + "public string P = @\"C:\\path\";\n}";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(3, result.Statistics.VerbatimLiteralsNormalized);
        Assert.Contains("\"a\\\"b\"", result.OutputCode);
        Assert.Contains("\"line1\\r\\nline2\"", result.OutputCode);
        Assert.Contains("\"C:\\\\path\"", result.OutputCode);
        Assert.DoesNotContain("@\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL4_InterpolatedForms_SkippedAndBucketed_HoleLiteralsStillRewritten()
    {
        const string source = """"
            public class C
            {
                public string A(int x) => $"x{x}";
                public string B(int x) => $@"a""{x}";
                public string C(int x) => @$"{x}";
                public string D(int x) => $"""raw {x}""";
                public string E(int x) => $$"""{{x}}""";
                public string F() => $"{("""inner""")}";
            }
            """";

        var result = Run(source);

        Assert.True(result.Changed);
        // A `$"x{x}"` 与 F `$"{(...)}"` 的外层各一个常规插值串；F 洞内的 raw 仍被改写。
        Assert.Equal(2, result.Statistics.InterpolatedRegularSkipped);
        Assert.Equal(2, result.Statistics.InterpolatedVerbatimSkipped);
        Assert.Equal(2, result.Statistics.InterpolatedRawSkipped);
        Assert.Equal(1, result.Statistics.RawLiteralsSingleLine);
        Assert.Contains("$\"x{x}\"", result.OutputCode);
        Assert.Contains("$@\"a\"\"{x}\"", result.OutputCode);
        Assert.Contains("@$\"{x}\"", result.OutputCode);
        Assert.Contains("$\"\"\"raw {x}\"\"\"", result.OutputCode);
        Assert.Contains("$$\"\"\"{{x}}\"\"\"", result.OutputCode);
        Assert.Contains("\"inner\"", result.OutputCode);
        Assert.DoesNotContain("(\"\"\"inner\"\"\")", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL_AllRules_SecondRunIsClean_AndByteIdempotent()
    {
        var source = "public class C { public void M() {\n"
            + "string r = \"\"\"\nx\n\"\"\";\n"
            + "System.ReadOnlySpan<byte> u = \"ab\"u8;\n"
            + "string v = @\"a\r\nb\";\n"
            + "string e = \"\\e\";\n} }\n";

        var first = Run(source);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void LL2_LL3_DisableSwitches_OnlyTargetedRulesAreSuppressed()
    {
        var source = "public class C { public void M() { "
            + "System.ReadOnlySpan<byte> u = \"a\"u8; string v = @\"b\"; } }\n";

        var u8Off = Run(source, new LexicalNormalizeOptions { NormalizeUtf8Literals = false });
        Assert.True(u8Off.Changed);
        Assert.Equal(0, u8Off.Statistics.Utf8LiteralsExpanded);
        Assert.Equal(1, u8Off.Statistics.VerbatimLiteralsNormalized);
        Assert.Contains("\"a\"u8", u8Off.OutputCode);

        var verbatimOff = Run(source, new LexicalNormalizeOptions { NormalizeVerbatimLiterals = false });
        Assert.True(verbatimOff.Changed);
        Assert.Equal(1, verbatimOff.Statistics.Utf8LiteralsExpanded);
        Assert.Equal(0, verbatimOff.Statistics.VerbatimLiteralsNormalized);
        Assert.Contains("@\"b\"", verbatimOff.OutputCode);
    }
```

- [ ] **Step 2: 运行全部 C43 Core 单测**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo --filter "FullyQualifiedName~LexicalLiteralNormalizeTests"`
Expected: `失败: 0`（M1 16 个用例 + M2 新增 10 个 Fact，本类共 26 个用例；代理项测试按 M0 只保留一个分支）。

- [ ] **Step 3: 提交**

```bash
git add src/CSharpToJava.Core/LexicalNormalize/Literals tests/CSharpToJava.Tests/LexicalLiteralNormalizeTests.cs
git commit -m "feat(preprocessing): C43 M2 LL-2 u8 展开与 LL-3 verbatim 归一并补全 Core 矩阵"
```

## Task 14: CLI 适配器摘要行与四个 --ln-no-* 开关

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/LexicalNormalizePassAdapter.cs`
- Modify: `src/CSharpToJava.CLI/Program.cs`

- [ ] **Step 1: 适配器增加累加字段**

在 `LexicalNormalizePassAdapter` 的字段区（`_externSites;` L25 之后）加：

```csharp
    private int _raw;
    private int _rawMulti;
    private int _u8;
    private int _u8Bytes;
    private int _verbatim;
    private int _esc;
    private int _interpolated;
    private int _interpolatedVerbatim;
    private int _interpolatedRaw;
```

在 `RewriteFileAsync` 的每文件累加区（L60-63 的 `_externSites += ...;` 之后）加：

```csharp
        var literalStats = result.Statistics;
        _raw += literalStats.RawLiteralsSingleLine + literalStats.RawLiteralsMultiLine;
        _rawMulti += literalStats.RawLiteralsMultiLine;
        _u8 += literalStats.Utf8LiteralsExpanded;
        _u8Bytes += literalStats.Utf8BytesEmitted;
        _verbatim += literalStats.VerbatimLiteralsNormalized;
        _esc += literalStats.EscEscapesNormalized;
        _interpolated += literalStats.InterpolatedRegularSkipped
            + literalStats.InterpolatedVerbatimSkipped
            + literalStats.InterpolatedRawSkipped;
        _interpolatedVerbatim += literalStats.InterpolatedVerbatimSkipped;
        _interpolatedRaw += literalStats.InterpolatedRawSkipped;
```

- [ ] **Step 2: 摘要行追加字面量段**

把 `BuildChainSummaryLine`（L136-139）整体替换为：

```csharp
    public override string BuildChainSummaryLine()
        => $"lexical-normalize: {_filesTransformed} transformed, {_filesClean} clean, " +
           $"{_lineRemoved} #line removed, {_externSites} extern-alias site(s), " +
           $"{_globalUsings.Count} global using(s), {_fileScoped} file-scoped normalized, " +
           $"{_raw} raw ({_rawMulti} multi), {_u8} u8 ({_u8Bytes} bytes), " +
           $"{_verbatim} verbatim, {_esc} esc, {_interpolated} interpolated skipped " +
           $"(r {_interpolatedRaw}/v {_interpolatedVerbatim}/i " +
           $"{_interpolated - _interpolatedRaw - _interpolatedVerbatim})";
```

适配器构造函数**不变**（四子选项随 `LexicalNormalizeOptions` 整体传入）。

- [ ] **Step 3: Program.cs preprocess factory 透传四开关**

把 preprocess verb 的 options factory（L3682-3688）：

```csharp
                LexicalNormalizeOptionsFactory = () =>
                    new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions
                {
                    ReportInfo = opts.Verbose,
                    NormalizeFileScopedNamespaces = opts.LnNormalizeFileScoped,
                    ExternAliasSeverity = opts.LnExternAliasSeverityEffective,
                },
```

改为：

```csharp
                LexicalNormalizeOptionsFactory = () =>
                    new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions
                {
                    ReportInfo = opts.Verbose,
                    NormalizeFileScopedNamespaces = opts.LnNormalizeFileScoped,
                    ExternAliasSeverity = opts.LnExternAliasSeverityEffective,
                    NormalizeRawLiterals = !opts.LnNoRaw,
                    NormalizeUtf8Literals = !opts.LnNoU8,
                    NormalizeVerbatimLiterals = !opts.LnNoVerbatim,
                    NormalizeEscEscapes = !opts.LnNoEsc,
                },
```

convert-project factory（L214-218）**不动**：不新增 convert-project 开关，子规则默认值 true 随 pass opt-in 生效。

- [ ] **Step 4: Program.cs 增加四个 verb 参数**

在 `LnExternAliasSeverityEffective` 计算属性（L4693-4695）之后插入：

```csharp
    [Option("ln-no-raw", Default = false,
        HelpText = "lexical-normalize: disable raw string literal normalization (LL-1)")]
    public bool LnNoRaw { get; set; }

    [Option("ln-no-u8", Default = false,
        HelpText = "lexical-normalize: disable u8 literal expansion to byte[] (LL-2)")]
    public bool LnNoU8 { get; set; }

    [Option("ln-no-verbatim", Default = false,
        HelpText = "lexical-normalize: disable verbatim string normalization (LL-3)")]
    public bool LnNoVerbatim { get; set; }

    [Option("ln-no-esc", Default = false,
        HelpText = "lexical-normalize: disable \\e to \\u001B rewrite in regular strings/chars (LL-6)")]
    public bool LnNoEsc { get; set; }
```

- [ ] **Step 5: 编译确认**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

## Task 15: fixture 扩展（LiteralSamples.cs + 新 xUnit 工程）

**Files:**
- Create: `tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLib/LiteralSamples.cs`
- Create: `tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLiteralTests/`（脚手架后覆盖）

- [ ] **Step 1: Lib 新增 LiteralSamples.cs**

```csharp
namespace SampleLexicalNormalizeLib;

/// <summary>
/// C43 fixture samples: every modern lexical form the pass normalizes or
/// skips. Runtime values are asserted by SampleLexicalNormalizeLiteralTests
/// both before and after intermediate-tree normalization.
/// </summary>
public static class LiteralSamples
{
    public const string Single = """hello""";

    public const string Multi = """
        line1
        line2
        """;

    public static ReadOnlySpan<byte> Greeting => "hello中"u8;

    public static byte[] GreetingBytes() => Greeting.ToArray();

    public const string Sql = @"
SELECT *
FROM t
WHERE id = 1";

    public const string EscString = "a\eb";
    public const char EscChar = '\e';

    public static string InterpolatedRaw(int x) => $"""raw {x}""";
    public static string InterpolatedDoubleDollar(int x) => $$"""{{x}}""";
    public static string InterpolatedVerbatim(int x) => $@"a""{x}";
}
```

（`ReadOnlySpan<>` 经现有 `GlobalUsings.cs` 的 `global using System;` 解析。）

- [ ] **Step 2: 脚手架生成新 xUnit 工程并入 sln**

Run（PowerShell，仓库根）：

```powershell
dotnet new xunit -n SampleLexicalNormalizeLiteralTests -f net10.0 -o tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLiteralTests
dotnet add tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLiteralTests/SampleLexicalNormalizeLiteralTests.csproj reference tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLib/SampleLexicalNormalizeLib.csproj
dotnet sln tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeSolution.sln add tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLiteralTests/SampleLexicalNormalizeLiteralTests.csproj
```

Expected: 三条命令成功退出；生成的 `UnitTest1.cs` 在 Step 4 删除。

- [ ] **Step 3: 覆盖 csproj（对齐 SampleForeachSourceTests 版本）**

覆盖 `SampleLexicalNormalizeLiteralTests.csproj` 为：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SampleLexicalNormalizeLib\SampleLexicalNormalizeLib.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: 删除脚手架测试，写 LiteralSamplesTests.cs**

删除 `UnitTest1.cs`，新建 `LiteralSamplesTests.cs`：

```csharp
using System.Text;
using SampleLexicalNormalizeLib;

namespace SampleLexicalNormalizeLiteralTests;

/// <summary>
/// Runtime (half-side) value assertions. They run against the original
/// fixture AND the normalized intermediate tree, so they are the .NET-side
/// proof that raw indentation, u8 bytes, verbatim content and \e survive the
/// lexical pass unchanged in value (design §7.2).
/// </summary>
public sealed class LiteralSamplesTests
{
    [Fact]
    public void RawSingleLine_HasExpectedValue()
        => Assert.Equal("hello", LiteralSamples.Single);

    [Fact]
    public void RawMultiLine_StripsIndentation_ExcludesClosingNewline()
        => Assert.Equal("line1\nline2", LiteralSamples.Multi);

    [Fact]
    public void Utf8_MatchesUtf8EncodingOfValue()
        => Assert.Equal(Encoding.UTF8.GetBytes("hello中"), LiteralSamples.GreetingBytes());

    [Fact]
    public void Verbatim_KeepsSqlContent()
    {
        // verbatim 原样保留源换行：fixture 若以 CRLF 落盘，值里就是 \r\n，先归一再断言。
        var sql = LiteralSamples.Sql.Replace("\r\n", "\n");
        Assert.StartsWith("\nSELECT *\n", sql);
        Assert.EndsWith("WHERE id = 1", sql);
    }

    [Fact]
    public void EscEscapes_DecodeToEscapeCharacter()
    {
        Assert.Equal("a\u001Bb", LiteralSamples.EscString);
        Assert.Equal('\u001B', LiteralSamples.EscChar);
    }

    [Fact]
    public void InterpolatedForms_KeepRuntimeValues()
    {
        Assert.Equal("raw 7", LiteralSamples.InterpolatedRaw(7));
        Assert.Equal("7", LiteralSamples.InterpolatedDoubleDollar(7));
        Assert.Equal("a\"7", LiteralSamples.InterpolatedVerbatim(7));
    }
}
```

- [ ] **Step 5: fixture 基线 build/test（归一前必须先绿）**

Run: `dotnet test tests/SampleLexicalNormalizeSolution/SampleLexicalNormalizeLiteralTests/SampleLexicalNormalizeLiteralTests.csproj --nologo`
Expected: `失败: 0，通过: 6`。

## Task 16: 项目级与 CLI 验收扩展

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/LexicalNormalizeProjectTests.cs`
- Modify: `tests/CSharpToJava.ProjectTests/LexicalNormalizeCLITests.cs`

- [ ] **Step 1: LexicalNormalizeProjectTests 追加 fixture 字面量验收 Fact**

在文件末尾最后一个 `}` 之前追加：

```csharp
    [Fact]
    public async Task Fixture_LiteralSamples_Normalized_InterpolationKept_AndTreeVerifies()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c43-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleLexicalNormalizeSolution.sln"),
                outputRoot);

            Assert.True(result.Success, "C43 literal pass failed (see chain diagnostics)");

            var intermediate = Path.Combine(outputRoot, ".cs2j-lexical-normalize-src");
            var text = await File.ReadAllTextAsync(Path.Combine(
                intermediate, "SampleLexicalNormalizeLib", "LiteralSamples.cs"));

            // LL-1: single/multi-line raw gone; multiline opening quote-newline absent.
            Assert.DoesNotContain("\"\"\"hello", text);
            Assert.DoesNotContain("\"\"\"\r\n", text);
            Assert.DoesNotContain("\"\"\"\n", text);

            // LL-2: u8 suffix gone, byte array emitted.
            Assert.DoesNotContain("\"u8", text);
            Assert.Contains("(byte)0x", text);
            Assert.Contains("new byte[]", text);

            // LL-3: no non-interpolated verbatim.
            Assert.DoesNotContain("@\"", text);

            // LL-6: no bare \e (regular \u001b escapes instead).
            Assert.DoesNotContain("\\e", text);
            Assert.Contains("\\u001b", text);

            // LL-4: interpolated forms remain verbatim for C23.
            Assert.Contains("$\"\"\"raw {x}\"\"\"", text);
            Assert.Contains("$$\"\"\"{{x}}\"\"\"", text);
            Assert.Contains("$@\"a\"\"{x}\"", text);

            // Idempotence over the intermediate tree.
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
```

- [ ] **Step 2: LexicalNormalizeCLITests 追加三个 Fact**

在文件末尾最后一个 `}` 之前追加：

```csharp
    [Fact]
    public async Task LiteralRules_DefaultWithinPass_RawAndVerbatimNormalized_ExitZero()
    {
        var sourceDir = CreateProject(
            "public class C { public string A => \"\"\"x\"\"\"; public string B => @\"y\"; }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, _) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.DoesNotContain("\"\"\"", rewritten);
            Assert.DoesNotContain("@\"", rewritten);
            Assert.Contains("\"x\"", rewritten);
            Assert.Contains("\"y\"", rewritten);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task LnNoRawSwitch_LeavesRaw_ButStillNormalizesVerbatim()
    {
        var sourceDir = CreateProject(
            "public class C { public string A => \"\"\"x\"\"\"; public string B => @\"y\"; }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, _) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize", "--ln-no-raw",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.Contains("\"\"\"x\"\"\"", rewritten);
            Assert.DoesNotContain("@\"", rewritten);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task Utf8_AsSpanArgument_BuildsAfterExpansion()
    {
        var sourceDir = CreateProject(
            "public class C { public void M() { "
            + "System.ReadOnlySpan<byte> s = \"abc\"u8; "
            + "System.Console.WriteLine(s.Length); } }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.DoesNotContain("\"u8", rewritten);
            Assert.Contains("(byte)0x61", rewritten);
            Assert.DoesNotContain("CS2J", stderr);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
```

（`--list-passes` 中 `C28, C43` 的断言已由 Task 10 Step 2 人工目视；如需自动化，复用本文件 `RunCliAsync("preprocess", "--list-passes")` 自行补一条，不阻断 M2。）

- [ ] **Step 3: build Debug 并运行项目级测试**

Run:

```powershell
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --nologo --filter "FullyQualifiedName~LexicalNormalizeProjectTests|FullyQualifiedName~LexicalNormalizeCLITests"
```

Expected: build `0 个错误`；test `失败: 0`（C28 既有 6 个 + C43 新增 4 个；verify build/tests 会真实还原并执行新 xUnit 工程，耗时较长）。

- [ ] **Step 4: 提交**

```bash
git add src/CSharpToJava.CLI/Preprocessing/Adapters/LexicalNormalizePassAdapter.cs src/CSharpToJava.CLI/Program.cs tests/SampleLexicalNormalizeSolution tests/CSharpToJava.ProjectTests/LexicalNormalizeProjectTests.cs tests/CSharpToJava.ProjectTests/LexicalNormalizeCLITests.cs
git commit -m "feat(preprocessing): C43 M2 CLI 四开关、fixture xUnit 工程与项目级/CLI 验收"
```

## Task 17: M2 里程碑全量 Release 验证

- [ ] **Step 1: Release 全量 build**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 2: 三个测试工程 Release test**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --nologo
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj -c Release --nologo
```

Expected: 三个工程全部 `失败: 0`。转换器零改动，ConversionTests 全绿即证明无 Java 侧行为回归。

- [ ] **Step 3: 默认关零痕迹复核**

Run:

```powershell
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess --list-passes
```

Expected: lexical-normalize 仍为 `off`，规则行 `rules=C28, C43`；不带 `--enable-pass lexical-normalize` 的任何运行不产生 `.cs2j-lexical-normalize-src` 目录。

---

# M3 — 默认开评审证据（随 C28 §8；本任务不翻开关）

## Task 18: §8 门槛证据生产（只做数据/文档，不改代码默认值）

**Files:** 无代码改动；证据结论写入任务记录/评审文档（不新建 md，除非评审要求）。

- [ ] **Step 1: sdk 只读画像分桶**

若 `D:\csharp-sdk` 存在，对其跑仅启用本 pass 的预处理，按桶汇总（设计 §8-3）：

| 桶 | 需要的数字 |
|----|-----------|
| raw | 命中文件数；单行/多行/插值占比；闭合引号列分布；CRLF/LF/CR 占比 |
| u8 | 命中数、总字节数、最长 blob、多字节站点；M0 代理项站点搜索结果 |
| verbatim | 命中数、含换行站点数 |
| `\e` | 命中数（串/字符分列） |
| 回退 | CS2J5580/5581/5583 命中必须为 0；5582 residual 逐条归因（预期全部落在插值节点豁免桶） |

若 `D:\csharp-sdk` 不存在：本步标注「证据缺失」，M3 不得继续，pass 维持默认关。

- [ ] **Step 2: Java 半区等价对照（设计 §8-4）**

选代表性工程，分别在 pass 启用/禁用下走完整 `convert-project`，按 `TargetJavaVersion` 14 与 17 两档比对 Java 产物中字符串字面量的**运行时值**（重点：raw 高版本 text block 路径在归一后改走 `EscapeJavaString` 普通串路径的等价性）；E01/C28 及其余 pass 统计零变化。

- [ ] **Step 3: 驱动成立性复核（设计 §8-1/§1.2）**

书面确认「统一引擎一次 parse 合批」或「版本无关中间树实际消费者」至少一个成立并有消费方背书；两者皆无则 M3 结束、维持默认关。

- [ ] **Step 4: 逐条核对设计 §8 六项门槛 + C23 边界**

§8-2（本计划 Task 17 证据）、§8-5（基线不退化/增量耗时）、§8-6（与 C23 文档互引、LL-4 跳过清单与 C23 M3 矩阵无重叠缺口）逐条打勾。

- [ ] **Step 5: 明确不做的动作**

本任务**不修改** `PassCatalog.cs` 的 `defaultEnabled: false`，**不删除** PassChainOrderTests 的 Where 排除。默认开翻转属于 **C28 M3 联合评审**（C28/C43 同一总闸），在该评审任务中凭本任务证据一次性完成；E01+C28+C43 共享一次 parse 的合批试点另随统一引擎立项，不在本计划内。

---

## 附：Self-Review 记录

- **Spec coverage**：LL-1 → Tasks 4/9（设计 §4.3/§7.1-1…4）；LL-2 → Tasks 11/13/15/16（§4.4/§7.1-5…7）；LL-3 → Tasks 12/13/15/16（§4.5/§7.1-8）；LL-4 → Tasks 5/13/15（§4.5/§7.1-9）；LL-5 → Tasks 6/7/9/11/12（§4.6/§7.1-12/§7.4）；LL-6 → Tasks 4/9/15/16（D13/§7.1-10）；M0 探针 → Task 1（开放问题 3/D5）；接线 D11（含 token-seq 豁免与双向回归）→ Task 7/9；描述符两行 → Task 8；CLI 四开关 → Task 14；fixture xUnit 工程 → Tasks 15/16；§8 门槛 → Task 18。无缺口。
- **占位符**：唯一二选一内容是 Task 13 的代理项测试，分支由 Task 1 探针结果决定，两分支代码均已完整写出；其余无 TBD/TODO。
- **类型名一致性**：十文件 `LiteralKinds`/`LiteralSyntaxFacts`/`LiteralRewriteRecorder`/`RawStringLiteralRewriter`/`EscEscapeLiteralRewriter`/`Utf8SuffixLiteralRewriter`/`VerbatimLiteralRewriter`/`ByteLiteralFactory`/`LiteralRewriter`/`LiteralNormalizationVerifier` 在全部任务与命名空间 `CSharpToJava.Core.LexicalNormalize.Literals` 下统一；统计字段名（Models/Mutable/适配器三处）逐字一致；诊断码集中于 `LexicalNormalizeCodes`。
- **纪律**：全程 dev-3.x 工作区、禁 worktree、逐文件 `git add`、不 push；默认关不翻转；转换器/HIR 零改动。
- **2026-10-02 二次自检修订**：① `LL1_RawSingleLine` 外层定界符 4→5 引号（Q 含连续 4 引号）；② 损坏的多行缩进单测重写为 5 个确定性 Fact（0/2/4/tab 列 + 全空白行豁免，各 1 个多行 raw），M1 计数更正为 14 方法/16 用例，Task 13 汇总更正为 M1 16 + M2 10 = 26；③ LL4 常规插值桶 1→2（A 与 F 外层）；④ `LL1_LL6_DisableSwitches` 补 escOnly 并纠正 rawOnly 反向断言；⑤ 两个 LN1 token-seq 回归断言正名（不改写→False+CS2J5542；改写→True+null），helper 透传 failure；⑥ LL6 用例显式只开本规则（raw/u8/verbatim 关），消除 E(raw) 在 M1 即被 LL-1 改写、D(verbatim) 在 M2 被 LL-3 改写导致的断言跨里程碑失败；⑦ Task 15 verbatim 值断言先 `Replace("\r\n","\n")`，防 fixture CRLF 落盘；⑧ M0 探针改为纯 ASCII here-string（原文误入 U+0000/U+0001 控制字符），文件重写为合法 UTF-8；⑨ 修正 `ContainsBareEsc` 注释对 `"\\e"`（两个反斜杠）的错误描述。
