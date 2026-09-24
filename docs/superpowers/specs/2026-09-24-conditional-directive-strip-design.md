# 条件编译预处理指令清理器（Conditional Directive Stripper）设计

日期：2026-09-24
状态：已定稿（自主模式下依据现有仓库模式与约束决策）

## 1. 背景与目标

cs2j 仓库中已有一族 **C#→C# 预处理器**（`eliminate-goto`、`tuple-to-struct`、`unsigned-type`、`local-function-lift`、`make-readonly`、`strip-default` 等），它们把 C# 源码规范化为更利于后续转换的形式。当前管线的痛点：源码中的条件编译指令（`#if/#elif/#else/#endif/#define/#undef`）在解析期虽然会按符号集求值，但**指令本身始终留在源文本里**，下游每个预处理器都要各自处理符号可见性问题（如 `ProjectDefaultParameterStripper.CollectPreprocessorSymbols`），且人工检视中间产物时被大量指令干扰。

本设计新增一个独立的 C#→C# 预处理器：**按给定符号集求值并彻底移除条件编译指令**，输出"自包含"的 C# 源码——不含任何条件编译指令，在任意符号配置下编译行为与原文件在选定符号集下的行为一致。

**强制约束：必须考虑所有情况**——见 §3（指令种类全覆盖策略矩阵）与 §4（语法位置全覆盖矩阵），两矩阵驱动测试计划 §9。

**明确范围**：这是 C#→C# 转换，**不涉及 C#→Java 转换**，不改动 convert 管线（`ProjectConversionPipeline` 等）。产出为独立 CLI verb，与 `eliminate-goto` 同级。

## 2. 核心思路：基于 Roslyn 求值，而不是手写词法器

手工逐行扫描实现 C# 规范（ECMA-334 §6.5 预处理）的隐患极多：块注释跨分支、逐字字符串跨行包含 `#`、禁用文本中嵌套指令需按配对词法化、指令必须位于行首非空白处、`#define` 必须先于首个 token、`#if` 表达式文法（`&& || ! == != ( ) true false`）……这些全部由 Roslyn 词法器在 `CSharpSyntaxTree.ParseText` + `WithPreprocessorSymbols(symbols)` 时按规范执行：

- 非激活分支被表示为 `DisabledTextTrivia`（禁用文本，内容任意、无需是合法 C#）；
- 非激活分支中的指令参与配对但不产生结构；
- 指令求值错误（未配对 `#endif`、`#define` 出现在 token 之后、指令不在行首、表达式非法等）产生**解析错误**。

因此清理器算法为：**用 Roslyn 解析（完成求值）→ 删除 trivia 层中的条件指令与禁用文本 → 原样回吐**。token 序列完全不变（只删 trivia），从构造上保证输出可解析且语义等价。

## 3. 指令种类全覆盖策略矩阵（"所有情况"之一）

| 指令 | 处理策略 |
|---|---|
| `#if` / `#elif` / `#else` / `#endif` | 按 Roslyn 求值结果保留激活分支；指令 trivia 全部删除；禁用文本（`DisabledTextTrivia`）全部删除 |
| `#define SYM` / `#undef SYM` | 解析期已对后续 `#if` 生效；随后删除指令行。注意：删除后输出不再依赖该定义——分支选择已经固化 |
| `#region` / `#endregion` | 默认**保留**（非条件编译指令）；`RemoveRegions` 选项开启时成对删除 |
| `#pragma warning …` / `#pragma checksum …` | 保留（仅影响警告/调试信息） |
| `#line …`（含 `default`/`hidden`） | 保留（仅影响行号映射） |
| `#nullable …` | 保留（仅影响可空警告上下文，不影响运行时语义） |
| `#warning …`（激活态） | 保留指令，产生 Warning 诊断输出到 stderr |
| `#error …`（激活态） | Roslyn 解析错误 → 文件失败（Error 诊断，原样透传不改动） |
| `#r` / `#load` / `#checksum` / shebang `#!` | 保留（在普通 .cs 中本身就是解析错误 → 文件失败） |
| 非法指令（`#foo` 等，`BadDirectiveTrivia`） | Roslyn 解析错误 → 文件失败 |
| `ThenDirectiveTrivia`（Roslyn 内部合成） | 一并纳入删除集合（防御式；后置校验兜底） |

策略理由：需求限定"条件编译预处理指令"；`#region/#line/#pragma/#nullable` 无编译语义，保留最保守，`RemoveRegions` 提供可选清理。

## 4. 语法位置全覆盖矩阵（"所有情况"之二）

以下每种情况都有对应测试（§9）：

1. 位置：文件首行；成员之间；`using`/attribute 列表中间；修饰符之间（`public` ␤ `#if A` ␤ `static`）；类型实参之间（`List<` ␤ `#if A` ␤ `int` …）；语句中间；实参列表中间（`foo(` ␤ `#if A` ␤ `1,` …）；对象/集合初始化器、switch 表达式臂、lambda 参数表内部；文件末尾（EOF token 的 leading trivia，含**末行无尾随换行**的 `#endif`）。
2. 分支组合：单 `#if/#endif`；`#else`；多级 `#elif` 链；嵌套任意深度（内外分支真假的 4 种组合）。
3. 表达式：`SYMBOL`、`!S`、`&&`、`||`、括号嵌套、`== true`、`!= true`、`true`/`false` 字面量、多空白（`#if  A  ||  B`）、`#  endif`（`#` 与名称间空白）。
4. 符号语义：符号**大小写敏感**（`FOO`≠`foo`）；文件内 `#define` 只对其**之后**的 `#if` 生效；`#undef` 可移除外部符号（文件内中途翻转分支）；`#define` 与外部符号重名（幂等）。
5. 禁用文本：含非法语法垃圾；含嵌套指令（含嵌套的非法/未配对内容，只要整体配对）；含 `#region`（随禁用文本一起删除）。
6. 非指令文本：普通字符串、逐字字符串（跨行）内的 `#if` 文本、块注释/行注释/doc 注释（`///`、`/** */`）中的 `#` 文本——**不是指令，原样保留**。
7. 空分支：`#if A` 直接 `#endif`；整个文件处于禁用分支 → 输出为空/仅空白；文件只有 `#define` → 输出空。
8. 换行与编码：CRLF/LF/CR 及混合换行**逐字保留**（只删 trivia 不重排版）；输入带 BOM → 输出带 BOM；无 BOM → 无 BOM；不变更文件字节级复制保真。
9. 畸形输入（全部 → Error 诊断 + 文件原样透传，绝不半清理）：多余 `#endif`；缺 `#endif`；`#elif` 在 `#else` 之后；`#define` 出现在首个 token 之后（CS1032）；指令不在行首（CS1040）；表达式非法（如 `A == B`，spec 仅允许与 `true/false` 比较）；激活的 `#error`。
10. 注释保真：保留分支内的注释（含 doc 注释）保留；禁用分支内注释随之删除。
11. 后置不变量：输出重新解析——零语法错误、零条件指令、零禁用文本，且 **token 流（kind+text 序列）与输入解析树完全一致**。

## 5. 符号来源（分层解析）

| 层 | 来源 | 说明 |
|---|---|---|
| 1（最高） | CLI `--define SYM`（可重复） | 追加到所有文件 |
| 2 | 文件内 `#define`/`#undef` | Roslyn 解析期按 spec 处理 |
| 3 | 所属项目 `<DefineConstants>` | 目录模式下：从 .cs 文件**向上找最近祖先 .csproj**，沿途收集 `Directory.Build.props`（父→子顺序应用：值含 `$(DefineConstants)` → 追加，否则 → 替换；`;` 分隔；跳过含其它 MSBuild 宏的项并发 Warning） |
| 4 | TFM 合成符号 | 由 csproj `<TargetFramework(s)>`（`--tfm` 可覆盖，多目标默认取第一个）合成 SDK 注入的框架符号（§5.1） |

配置条件求值：`'$(Configuration)' == 'Debug'` / `!=` 等 `$(Configuration)` 等式/不等式（含 `And`/`Or` 组合）；不涉及 `$(Configuration)` 的条件视为包含（保守）。`--configuration` 默认 **Release**（与 convert 管线 StripDebugPreprocessorSymbol 的 Release 语义一致）。

单文件模式（`-i`）：只用 `--define` + 文件内 `#define`（无项目上下文，文档明示）。

不做完整 MSBuild 属性求值（设计取舍：免 restore、可离线；复杂 props 链用 `--define` 兜底；与 `ProjectDefaultParameterStripper` 现有轻量方案一致）。

### 5.1 TFM 符号合成表

- `netstandard{X}.{Y}` → `NETSTANDARD`、`NETSTANDARD{X}_{Y}`，及所有更低版本的 `NETSTANDARD{…}_OR_GREATER`
- `netcoreapp{M}.{m}` → `NETCOREAPP`、`NETCOREAPP{M}_{m}`，及更低 `NETCOREAPP{…}_OR_GREATER`
- `net{N}.*`（N≥5）→ `NET`、`NET{N}_{minor}`，及更低 `NET{5..}_OR_GREATER` + 全部 `NETCOREAPP{…}_OR_GREATER`（SDK 实际行为；不含 NETSTANDARD*，不含 NETFRAMEWORK*）
- `net{4xx}` → `NETFRAMEWORK`、`NET{4xx}`，及更低 `NETFRAMEWORK{…}_OR_GREATER`
- 无法识别的 TFM → 空集 + Warning

## 6. 组件与数据流

```
CLI verb: strip-conditional
  ├─ 单文件: ConditionalDirectiveStripper.Strip(source, symbols) → Result
  └─ 目录/项目: 遍历 *.cs（非 .cs 字节复制；in-place 安全；.tmp 原子写）
        每文件: ProjectSymbolResolver.ResolveForFile(path, config, tfm, extraDefines) → symbols
                ConditionalDirectiveStripper.Strip(...)
Core（新目录 src/CSharpToJava.Core/ConditionalDirectiveStrip/）
  ├─ ConditionalDirectiveStripOptions     // Verbose/Strict/Symbols/RemoveRegions
  ├─ ConditionalDirectiveStripModels      // Severity/Diagnostic/Statistics/Result
  ├─ ConditionalDirectiveStripper         // 核心：解析→trivia 过滤→后置校验
  └─ ProjectSymbolResolver                // csproj/Directory.Build.props 抓取 + TFM 合成
```

- `Strip` 返回 `Result { OutputCode, Changed, Diagnostics, Statistics }`（对齐 `GotoEliminatorResult` 形状）。
- 统计：`FilesScanned/FilesTransformed/FilesSkippedClean/FilesFailed`（目录级）+ `DirectivesRemoved/DisabledBranchesRemoved`（单文件级）。
- 退出码：任一文件 Error 或参数错误 → 1；`--strict` 时 Warning 也计 1（对齐 eliminate-goto）。

## 7. 核心算法细节（trivia 过滤）

对每个 token（含 `EndOfFileToken`）的 leading/trailing trivia 列表：

1. 删除 `DisabledTextTrivia`。
2. 删除条件族 `DirectiveTriviaSyntax`（If/Elif/Else/EndIf/Then/Define/Undef；`RemoveRegions` 时加 Region/EndRegion）。
3. 行首缩进清理：被删指令**紧邻前方**的纯空白 trivia，若其前方是 EOL/列表首/另一被删 trivia，则一并删除（该空白是指令行的缩进，避免残留"纯空白行"；指令 trivia 自带行尾 EOL，删之即整行消失）。
4. 列表未变化则不替换（避免无谓 churn）；`root.ToFullString()` 回吐，保持原格式与换行。

实现载体：`CSharpSyntaxRewriter.VisitToken`。

**后置校验**（每文件，防实现缺陷）：重解析输出 → 断言零语法错误、零条件指令/禁用文本、token 流与输入一致；任一不满足 → 内部 Error，文件透传。

## 8. CLI 规格

```
strip-conditional -i <file.cs> [-o <out.cs|stdout>]
strip-conditional -s <dir|csproj|sln> -d <dir> [-v] [--strict]
    [--define SYM]... [--configuration Release|Debug|...] [--tfm net6.0]
    [--remove-regions]
```

`-s` 为 .csproj/.sln 时以其目录为源根（不做 MSBuild 图加载；符号靠最近祖先 .csproj + `--define`）。

## 9. 测试计划（tests/CSharpToJava.Tests，TDD：先失败后实现）

- `ConditionalDirectiveStripTests`：§4 矩阵逐条 ~40 用例（保真断言：精确输出文本 / Contains / 语法零错 / token 等价 / 换行与 BOM 字节级断言）。
- `ProjectSymbolResolverTests`：DefineConstants 抓取（条件 Configuration、append/replace、Directory.Build.props 链、宏跳过 Warning）、TFM 合成表（net6.0/netstandard2.0/net48/net8.0/多目标/--tfm）、大小写、`--define` 合并。
- 完成门：`dotnet build` 零错 + `dotnet test`（全量）通过。

## 10. 非目标与已知局限

- 不集成进 convert 管线（本任务明确 C#→C#；集成属后续独立决策）。
- 不做完整 MSBuild 属性求值（`--define` 兜底）；`<DefineConstants Remove=…>` 忽略。
- 编码假定为 UTF-8/UTF-8-BOM（与仓库其它预处理器一致）；其它编码可能产生替换字符（不变更文件走字节复制不受影响）。
- 输出保留原分支缩进（不重排版）——纯格式层面，编译语义无关。
