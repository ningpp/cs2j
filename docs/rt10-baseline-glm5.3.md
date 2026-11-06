# rt10 真源基线重跑（W-10 运行级证据）

## 1. 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；仓库 HEAD：`78a3de4f`（W-10 代码+测试提交，工作树干净）。
- 环境：Windows；.NET SDK `10.0.300`（MSBuild `18.6.3`）。
- 代码前置：`ProjectSymbolResolver.ResolveForFile` 新增 `resolvedTfm`；新增 `ResolvedProjectTfm`；
  `ConditionalPassAdapter` 与 `StripConditionalCommand` 两入口接入。全量单测 4695 通过/9 跳过/0 失败，
  ProjectTests 仅 `dotnet build` 通过。
- 源：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10`；unified 中间树 `D:\cs\rt10-baseline-w10\.cs2j-preprocessed-src`。
- 命令（§7.0 探针口径，关闭 verify）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- 结果：进程退出码 **1**（链中止，非超时/崩溃）。证据文件：
  `D:\cs\rt10-baseline-w10.log`（进程输出）、`D:\cs\rt10-baseline-w10\preprocess-report.json`
  （report `success=false`、`totalSeconds=20.034`、6 个 pass）。

## 2. 每个 pass 的实测（来源：preprocess-report.json 的 `summary`）

| pass | 状态 | 实测 summary（原样节选） |
|------|------|--------------------------|
| conditional | ok | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| lexical-normalize | ok | `0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), 0 global using(s), 0 file-scoped normalized, 0 raw (0 multi), 0 u8 (0 bytes), 0 verbatim, 0 esc, 0 interpolated skipped (r 0/v 0/i 0)` |
| partial-merge | ok | `10 group(s) merged, 0 partial member pair(s) collapsed`；4 组冲突回滚 CS2J2001（JsonWriterHelper/Utf8JsonWriter/JsonTypeInfo\<T\>/ThrowHelper）+ 5 条 using 冲突 |
| local-function-lift | ok | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| null-coalesce | ok | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| expression-desugar | **failed** | `102 transformed, 209 clean, 41 dirty-no-rewrite; coll ... residual 0; interp ... residual 0; init: 67 obj, 1 coll, 1 dict-index, 126 target-new, 0/0 nested, 38 ctx-stmts, 137 temps, 163 skipped (2 non-strict kept), 75 residual; nc ... residual 0` |

中止点：**expression-desugar**（第 6 个 pass）。共 6 条 Error（CS2J0558×3、CS2J0581×3），前 3 条原文：

1. `[expression-desugar] [Error] src\System\Text\Json\Serialization\Metadata\DefaultJsonTypeInfoResolver.Helpers.cs: CS2J0558: Void conditional invocation has no statement anchor (e.g. expression-bodied member); add an explicit null check or convert the member to a block body.`
2. `[expression-desugar] [Error] src\System\Text\Json\Serialization\Metadata\JsonPolymorphismOptions.cs: CS2J0558: ...`（同文件 2 条）
3. `[expression-desugar] [Error] src\System\Text\Json\Serialization\Converters\Value\EnumConverter.cs: CS2J0581: No known concrete construction form for collection target 'System.Text.Json.Nodes.JsonArray' (table-out interface/abstract class/multidimensional array). Site left unchanged.`

其余：`JsonPrimitiveConverter.cs` CS2J0581、`JsonSchema.cs` CS2J0581、`JsonPolymorphismOptions.cs` 第 2 条 CS2J0558。
对应原始源位置（`D:\runtime-10.0.12\...\System.Text.Json\src\`）：
`DefaultJsonTypeInfoResolver.Helpers.cs:41`（`ClearMemberAccessorCaches() => s_memberAccessor?.Clear()`）、
`JsonPolymorphismOptions.cs:84/:98`、`EnumConverter.cs:503`（`JsonArray enumValues = [];`）、
`JsonPrimitiveConverter.cs:60`（`Enum = [ (JsonNode)"NaN", ... ]`）、`JsonSchema.cs:234`（`JsonArray anyOfArray = [];`）。
报告诊断不带行号，故行号取自源文件静态定位。

**CS2J0545 本次未触发**（report 中该码 0 次）；probe3 的 CS2J0545（init 守恒门）被本轮的 CS2J0558/CS2J0581 提前判定取代。
未使用 `--no-local-function-strict` 等逃生门。

## 3. TFM 警告归零证据

- `conditional` pass 的 `diagnostics` 数组为空；`D:\cs\rt10-baseline-w10.log` 与 report 中
  `skipped unresolvable TFM value` 出现次数均为 **0**（逐文件计数命令：`Select-String ... | Measure-Object`）。
- 对照 `D:\cs\rt10-probe3-v8fix.log`：同一计数为 **301**（该日志 372 directives / 116 disabled blocks）。
- 本轮同一语料为 372 directives / **103** disabled blocks：net10 符号集（`NET`/`NET10_0`/`NETCOREAPP` 等）已参与分支求值，
  不再把 `#if NET`/`#if !NET` 当未知分支处理。

## 4. C06 unsafe 面收敛（定量）

统计对象与命令：对目录递归 `*.cs` 逐行匹配 `\bunsafe\b` 与 `fixed (`（literal）；
源目录 `D:\runtime-10.0.12\src\libraries\System.Text.Json\src`，输出目录 `...\.cs2j-preprocessed-src`。

- 源树 net10 编译面（排除仅 netstandard 的 `JsonReaderHelper.netstandard.cs`）：**13 行匹配 `unsafe` / 13 处 `fixed (`**。
  13 行中真正的 `unsafe` 关键字行 10 行：9 行位于 `#if !NET`（`JsonHelpers.cs` 1、`JsonReaderHelper.Unescaping.cs` 6、
  `JsonWriterHelper.cs` 2），1 行是无条件站点（`JsonWriterHelper.Escaping.cs:71`）；另 3 行为注释/`Unsafe.*` 文本。
  13 处 `fixed (` 中 12 处位于 `#if !NET`，1 处无条件（`JsonWriterHelper.Escaping.cs:73`）。
- 输出树共 352 个 `.cs`：全树 `unsafe` 文本行 **21**、`fixed (` **1**；其中仅 netstandard 的
  `src\System\Text\Json\Reader\JsonReaderHelper.netstandard.cs` 占 17 行（该文件不属于 net10 编译集）。
- net10 编译面子树收敛为：文本 `unsafe` **4 行**（其中真正的 `unsafe` 关键字 **1** 处）、`fixed (` **1** 处。
  唯一残留关键字站点是**无条件**代码 `src\System\Text\Json\Writer\JsonWriterHelper.Escaping.cs:307`（`fixed` 在 `:309`），
  不属于 `#if !NET` 可剥离面。其余残留仅为注释/`Unsafe.SizeOf` 文本（`JsonMarshal.cs:9`、`JsonDocument.DbRow.cs:61`、
  `JsonWriterHelper.Escaping.cs:306`）。
- 结论：`#if !NET` 包裹的 9 处 unsafe 关键字与 12 处 `fixed (` 已随 net10 符号选择全部剥离；
  编译面仅剩 1 处无条件 unsafe + 1 处无条件 fixed（`JsonWriterHelper.Escaping.cs`）。

## 5. 与 §3.2.3 / §3.2.4 旧基线逐项对照

| 关注点（§3.2.3 列） | 本次实测 | 判定 |
|--------------------|----------|------|
| generic（default(T)/new T()） | 无该 pass 输出 | 未到达（中止点 expression-desugar） |
| generic-deepen（anchors/1101/1106） | 无该 pass 输出 | 未到达（同上） |
| closed-metadata（registered/enumerated/...） | 无该 pass 输出 | 未到达（同上） |
| variance-audit（use sites/conversions/unsafe/constraints） | 无该 pass 输出 | 未到达（同上） |
| extension-methods | 无该 pass 输出 | 未到达（同上） |
| closure-lift / iterator-lowering / async-normalize | 无该 pass 输出 | 未到达（同上） |
| compat-method-replacement（ToUtf8 匹配） | 无该 pass 输出 | 未到达（同上） |
| local-function-lift | 28/324，59 lifted，0 skipped/0 residual | 到达；与 probe3（59/0/0）一致 |
| expression-desugar | 102/209，41 dirty-no-rewrite；init 75 residual；0 CS2J0545、6 Error（0558×3/0581×3） | 到达并中止 |
| strip-conditional | 66/286，372 directives，103 disabled，0 TFM 警告 | 到达；probe3 为 372/116/301 警告 |

§3.2.4 对照要点：probe3 的 `local-function-lift` 0 residual 与 V-09 关闭结论保持；
probe3 的 `expression-desugar` CS2J0545（75/68，2 文件 unpaired）本次未出现，改为 0558/0581 的 6 条 Error；
probe3 的 TFM Warning 群本次为 0。

## 6. 剩余偏差与下一步

- 链仍中止在 **expression-desugar**：6 条 Error（CS2J0558 表达式体 void 条件调用 3 处、CS2J0581 `JsonArray` 集合表达式 3 处），
  本次未绕过。
- 因此 §3.2.3 的 generic/generic-deepen/closed-metadata/variance-audit/extension-methods/closure-lift/iterator-lowering/
  async-normalize/compat-method-replacement 关注点本轮全部**未到达**，不能给出实测值。
- C06：条件分支内 unsafe 已收敛；剩余 `JsonWriterHelper.Escaping.cs` 无条件 unsafe 1 处、netstandard-only 文件 17 行
  仍需 C06/C01 下游 pass（本轮未到达 unsafe-ffm）。
- 下一步以修复上述 6 条 expression-desugar 中止原因为前提重跑本命令；验收路径不使用 `--no-local-function-strict`。

## 7. 来源文件

- `D:\cs\rt10-baseline-w10.log`（进程输出，0 条 TFM 警告）
- `D:\cs\rt10-baseline-w10\preprocess-report.json`（pass summary / diagnostics / success=false）
- `D:\cs\rt10-probe3-v8fix.log`（旧基线：372/116、301 条 TFM 警告）
- `D:\runtime-10.0.12\src\libraries\System.Text.Json\src`（源 unsafe/fixed 统计）
- `D:\cs\rt10-baseline-w10\.cs2j-preprocessed-src`（输出 unsafe/fixed 统计）
