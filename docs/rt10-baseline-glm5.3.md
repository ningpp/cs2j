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

## 8. 第二次重跑（HEAD `ffab1880`）

### 8.1 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；仓库 HEAD：`ffab1880`（本轮两个修复提交：
  `05d1f8e5` 表达式体 void 成员 `?.` 降低、`ffab1880` 可构造集合类型 T6；工作树干净）。
- 代码前置：全量单测 4705 通过 / 9 跳过 / 0 失败；ProjectTests 仅 `dotnet build` 验证。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10b`；unified 中间树 `D:\cs\rt10-baseline-w10b\.cs2j-preprocessed-src`。
- 命令（与 §1 同参数，仅换目标目录与日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10b --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- 结果：进程退出码 **1**（链中止，非超时/崩溃）。证据文件：
  `D:\cs\rt10-baseline-w10b.log`（进程输出）、`D:\cs\rt10-baseline-w10b\preprocess-report.json`
  （report `success=false`、`totalSeconds=31.873`、**10** 个 pass）。

### 8.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`）

| # | pass | 状态 | 实测 summary（原样节选） |
|---|------|------|--------------------------|
| 1 | conditional | ok | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | `0 transformed, 352 clean, 0 #line removed, ...` |
| 3 | partial-merge | ok | `10 group(s) merged, 0 partial member pair(s) collapsed` |
| 4 | local-function-lift | ok | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | **ok** | `107 transformed, 209 clean, 36 dirty-no-rewrite; coll: 35 sites (array 9/mutable 15/iface 0/span 8/builder 0/unknown 0), spread 3 (addrange 3), empty 16, nested 1, lifted 2, skipped 0, residual 0; nc: 180 member/call, 0 idx, 24 invoke, 56 void, 42 inline, 83 lifted, 32 coalesce, 0 deferred-c51, 164 temps, 0 skipped, 0 residual, assign=0` |
| 7 | enum | ok | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple-to-struct | **failed** | `16 transformed, 338 clean, 25 types, 36 literals, 10 deconstructions, 0 tuple locals -> var, 20 structs generated` |

### 8.3 与第一次（§2，expression-desugar 6 Error）的差异

- **expression-desugar 已通过**：`status=ok`，0 条 Error；`coll` 的 `unknown` 与 `skipped` 均为 **0**，
  `nc` 的 `skipped`/`residual` 均为 **0**。第一次的 6 条 Error（CS2J0558×3、CS2J0581×3）全部消失。
- 文件级差异：`107 transformed / 209 clean / 36 dirty-no-rewrite`（第一次为 `102/209/41`）；
  因不再原子回滚，多出的 5 个文件完成改写。
- 中间树核对（`D:\cs\rt10-baseline-w10b\.cs2j-preprocessed-src`）：
  - `DefaultJsonTypeInfoResolver.Helpers.cs` 的 `ClearMemberAccessorCaches` 已转为块体：
    `var __ed_s0 = s_memberAccessor; if (__ed_s0 != null) { __ed_s0.Clear(); }`；
  - `JsonPolymorphismOptions.cs` 的 `VerifyMutable` / `OnCollectionModifying` 同样转为块体（`__ed_s0` / `__ed_s2` 单次求值）；
  - `EnumConverter.cs:494` → `JsonArray enumValues = new global::System.Text.Json.Nodes.JsonArray();`；
  - `JsonSchema.cs:271` → `new global::System.Text.Json.Nodes.JsonArray()`；
  - `JsonPrimitiveConverter.cs:57` → `new global::System.Text.Json.Nodes.JsonArray(new global::System.Text.Json.Nodes.JsonNode[] { (JsonNode)"NaN", ... })`。
- **新的中止点**：第 10 个 pass `tuple-to-struct`（`id=tuple`，`status=failed`）。错误原文：
  `[Error] src\System\Text\Json\Serialization\Metadata\PolymorphicTypeResolver.cs: tuple rewrite produced invalid C# syntax; the original file was preserved.`
  另有 8 条 Warning：`gen\JsonSourceGenerator.Roslyn4.0.cs: Tuple element type could not be resolved (missing reference?); it was replaced with object.`
  该失败与本次两个修复无关（第一次运行未到达该 pass）；该文件已被原样保留，链在此退出。
- 未到达的后续 pass：`anonymous-type-lift` 及其后的全部 pass。

### 8.4 TFM 警告

- `D:\cs\rt10-baseline-w10b.log` 中 `skipped unresolvable TFM value` 计数仍为 **0**
  （`Select-String ... | Measure-Object`）；conditional pass 的 `diagnostics` 为空。
- conditional 的 `372 directives / 103 disabled blocks` 与第一次一致，net10 符号集求值未回退。

### 8.5 来源文件

- `D:\cs\rt10-baseline-w10b.log`（进程输出，0 条 TFM 警告）
- `D:\cs\rt10-baseline-w10b\preprocess-report.json`（10 个 pass、`success=false`、`totalSeconds=31.873`）
- `D:\cs\rt10-baseline-w10b\.cs2j-preprocessed-src`（改写后的中间源，含上述 3 + 3 处落点）

## 9. 第三次重跑（HEAD `36e8cfbb`，tuple-to-struct 通过）

### 9.1 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；仓库代码 HEAD：`36e8cfbb`
  （`fix(tuple): foreach 解构目标不再被当成元组字面量改写`）。
- 代码前置：全量单测 4706 通过 / 9 跳过 / 0 失败（基线 4705 + 新增 1 条 tuple 单测）。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10c`；unified 中间树
  `D:\cs\rt10-baseline-w10c\.cs2j-preprocessed-src`。
- 命令（与 §8.1 同参数，仅换目标目录与日志；**注意 `preprocess` 动词没有 `--force`
  选项**，传入会被命令行解析器拒绝并以退出码 1 静默结束，故按 §8.1 原样调用）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10c --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- 结果：进程退出码 **1**（链中止，非超时/崩溃）。证据文件：
  `D:\cs\rt10-baseline-w10c.log`（stdout）、`D:\cs\rt10-baseline-w10c.err`（stderr）、
  `D:\cs\rt10-baseline-w10c\preprocess-report.json`（`success=false`、
  `totalSeconds=31.848`、**13** 个 pass）。

### 9.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`）

| # | pass | 状态 | 实测 summary（原样节选） |
|---|------|------|--------------------------|
| 1 | conditional | ok | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | `0 transformed, 352 clean, 0 #line removed, ...` |
| 3 | partial-merge | ok | `10 group(s) merged, 0 partial member pair(s) collapsed` |
| 4 | local-function-lift | ok | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | ok | `107 transformed, 209 clean, 36 dirty-no-rewrite; ...; nc: 180 member/call, 24 invoke, 56 void, 42 inline, 83 lifted, 32 coalesce, 164 temps, 0 skipped, 0 residual` |
| 7 | enum | ok | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple-to-struct | **ok（本轮修复）** | `16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated` |
| 11 | anonymous-type-lift | ok | `0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual` |
| 12 | foreach-deconstruction | ok | `0 transformed, 358 clean, 0 lowered, 1 skipped` |
| 13 | foreach-source | **failed** | `9 transformed, 349 clean, 0 tuple residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered, 7 span index loop(s), 0 residual enumerator site(s), 1 error(s)` |

### 9.3 tuple-to-struct 通过证据

- §8 的唯一 Error（`PolymorphicTypeResolver.cs: tuple rewrite produced invalid C# syntax`）
  已消失；本 pass `status=ok`，无 Error/Warning 中的 invalid-syntax 条目。
- summary 变化：元组字面量 36 → **35**、生成 struct 20 → **19**。差额正是被错误计入
  字面量的 `(Type derivedType, object? typeDiscriminator)` 解构模式（原先注册并生成了
  `TupleTypeObject`，现已不再注册）。
- 中间树核对：`PolymorphicTypeResolver.cs` 的原始解构在 tuple pass 中原样保留，随后由
  `foreach-deconstruction` / `foreach-source` 正确降级为：

```
foreach (var __cs2jFsEntry in polymorphismOptions.DerivedTypes)
{
    __cs2jFsEntry.Deconstruct(out Type derivedType, out object? typeDiscriminator);
    ...
}
```

  即 `JsonDerivedType.Deconstruct` 语义不变，不再出现 `newglobal::Cs2jTuples.TupleTypeObject(...)`
  形式的非法 foreach 头。

### 9.4 新中止点

- 第 13 个 pass `foreach-source`（`status=failed`），错误原文（stderr）：
  `gen\JsonSourceGenerator.Emitter.cs(787,17): error CS2J5457: [FA] foreach over a non-array/non-string/non-IEnumerable source has an incomplete enumerator binding (no usable GetEnumerator/MoveNext/Current symbols); the node at this site is left unchanged.`
- 该中止点此前被 tuple 失败遮蔽（§8 在第 10 个 pass 退出），与本轮 tuple 修复无关。

### 9.5 TFM 警告

- `D:\cs\rt10-baseline-w10c.log` / `.err` 中 `skipped unresolvable TFM value` 计数为 **0**；
  conditional 的 `372 directives / 103 disabled blocks` 与 §2/§8 一致。

### 9.6 来源文件

- `D:\cs\rt10-baseline-w10c.log`（进程 stdout，13 个 pass 摘要）
- `D:\cs\rt10-baseline-w10c.err`（stderr：32 条 `warning `、1 条 error CS2J5457、61 条 `[Warning]`）
- `D:\cs\rt10-baseline-w10c\preprocess-report.json`（13 个 pass、`success=false`、`totalSeconds=31.848`）
- `D:\cs\rt10-baseline-w10c\.cs2j-preprocessed-src`（改写后的中间源）

## 10. 第四次重跑（HEAD `2af1843f`，foreach-source 通过）

### 10.1 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；仓库代码 HEAD：`2af1843f`
  （`fix(foreach-source): Nullable<T> 源按底层值类型归类，消除 CS2J5457 误报`）。
- 代码前置：全量单测 **4710 通过 / 9 跳过 / 0 失败**（基线 4706 + 新增 4 条
  nullable foreach-source 单测）。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10d`；unified 中间树
  `D:\cs\rt10-baseline-w10d\.cs2j-preprocessed-src`（运行时目录不存在，无需 `--force`）。
- 命令（与 §9.1 同参数，仅换目标目录与日志；stdout/stderr 合并进同一日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10d --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10d.log
```

- 结果：进程退出码 **1**（链在更后面的 pass 中止，非超时/崩溃）。证据：
  `D:\cs\rt10-baseline-w10d.log`（417 行）、
  `D:\cs\rt10-baseline-w10d\preprocess-report.json`（`success=false`、
  `totalSeconds=33.481`、**14** 个 pass）。

### 10.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`/`totalSeconds`）

| # | pass | 状态 | 秒 | 实测 summary（原样节选） |
|---|------|------|----|--------------------------|
| 1 | conditional | ok | 5.812 | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | 1.446 | `0 transformed, 352 clean, 0 #line removed, ...` |
| 3 | partial-merge | ok | 3.220 | `10 group(s) merged, 0 partial member pair(s) collapsed`（9 条 CS2J2001 warning，同 §9） |
| 4 | local-function-lift | ok | 4.588 | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | 0.858 | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | ok | 1.697 | `107 transformed, 209 clean, 36 dirty-no-rewrite; ...; nc: 180 member/call, 24 invoke, 56 void, 42 inline, 83 lifted, 32 coalesce, 164 temps, 0 skipped, 0 residual` |
| 7 | enum | ok | 1.193 | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | 5.493 | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | 0.691 | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple-to-struct | ok | 6.520 | `16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated` |
| 11 | anonymous-type-lift | ok | 0.894 | `0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual` |
| 12 | foreach-deconstruction | ok | 0.201 | `0 transformed, 358 clean, 0 lowered, 1 skipped` |
| 13 | foreach-source | **ok（本轮修复）** | 0.432 | `9 transformed, 349 clean, 0 tuple residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)` |
| 14 | pattern-desugar | **failed** | 0.406 | `12 transformed, 307 clean, 39 dirty-no-rewrite, 14 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 6 govs, 0 end-labels, 3 skipped, 0 residual` |

- 与 §9.2 逐条对比：第 1–12 个 pass 的 summary 逐字一致；唯一差异是第 13 个
  `foreach-source` 的 `1 unresolved site(s), 1 error(s)` → `0 unresolved site(s),
  0 error(s)`（`9 transformed / 349 clean / 2 duck / 7 span` 等计数不变）。
  第 14 个 `pattern-desugar` 在 §9 从未运行（链在第 13 个 pass 中止）。

### 10.3 foreach-source 通过：根因与证据

- 站点不变：`gen\JsonSourceGenerator.Emitter.cs:787` 在 w10c 与 w10d 两棵中间树中
  逐字相同，均为 `foreach (int i in typeGenSpec.FastPathPropertyIndices)`；
  本 pass 对该文件无改写，也无任何诊断。
- 实测根因（与 §9.4 的初步判断不同）：`ImmutableEquatableArray<T>` 的源文件
  `src/libraries/Common/src/SourceGenerators/ImmutableEquatableArray.cs` 由
  `gen/System.Text.Json.SourceGeneration.targets` 的
  `<Compile Include="$(CommonPath)\SourceGenerators\...">` 链接进来，物理位置在
  枚举根（`...\System.Text.Json`）之外，因此**不在中间树的 358 个 .cs 文件里**
  （pass 的 `9 transformed + 349 clean = 358` 与中间树 .cs 数完全相等）。
  用这 358 个文件 + 链的 TPA 引用集重建 compilation 后实测该站点：
  - 表达式类型 `ImmutableEquatableArray<int>?`，`TypeKind=Struct`、
    `OriginalDefinition=System.Nullable<T>`；
  - 底层 `ImmutableEquatableArray<int>` 的 `TypeKind=**Error**`
    （同文件有 `CS0246: SourceGenerators / SourceWriter / TypeRef` 未解析）；
  - `ForEachStatementInfo` 的 GetEnumerator/MoveNext/Current 全为 null。
  即：`Nullable<T>` 包装把“未解析类型”的 `TypeKind.Error` 藏在了 Struct 之下，
  旧的 `IsErrorType` 只看顶层 kind，于是把该站点当成“真正的非 IEnumerable 源”
  报 CS2J5457。修复后 `IsErrorType` 看穿 `Nullable<T>`，站点走既有的
  error-type 直通路径（`SitesPassedThrough`，无诊断、无改写）。
- 同类潜在缺陷一并修复：若 T 可解析且实现 IEnumerable（如 `struct E :
  IEnumerable<int>` + `E?`），旧分类同样因 `ImplementsIEnumerable(Nullable<T>)`
  为 false 而进入 lower 分支，产出
  `var __cs2jFaSrc = source; var __cs2jFaE = __cs2jFaSrc.GetEnumerator();`
  （源局部类型为 `E?`）——实测 `error CS1061: “E?”未包含“GetEnumerator”的定义`。
  现归为 converter-owned，原样保留（不做 `.Value` 改写）。
- 剩余形态：`struct T` 仅有鸭子枚举器时的 `T?` 源无法用
  GetEnumerator/while 骨架表达（`T?.GetEnumerator()` 不绑定），改为精确诊断
  **CS2J5458**；实测该形态 C# 在 null 时抛 `InvalidOperationException`
  （编译器经 `Nullable<T>.Value` 求值，不是 NullReferenceException）。rt10 中
  不存在该形态（本 pass 的 diagnostics 为空，无 CS2J5458）。

### 10.4 新中止点

- 第 14 个 pass `pattern-desugar`（`status=failed`，0.406s），3 条 Error（stderr/日志原文）：

```
[Error] src\System\Text\Json\Serialization\JsonConverterOfT.ReadCore.cs: PD010: is-pattern inside a when guard is reduced by C37 (D:\cs\rt10-baseline-w10d\.cs2j-preprocessed-src\src\System\Text\Json\Serialization\JsonConverterOfT.ReadCore.cs:74,47)
[Error] src\System\Text\Json\Serialization\JsonSerializer.Read.HandleMetadata.cs: PD010: is-pattern inside a when guard is reduced by C37 (D:\cs\rt10-baseline-w10d\.cs2j-preprocessed-src\src\System\Text\Json\Serialization\JsonSerializer.Read.HandleMetadata.cs:363,28)
[Error] src\System\Text\Json\Nodes\JsonNode.To.cs: PD010: property subpattern without an explicit member name (D:\cs\rt10-baseline-w10d\.cs2j-preprocessed-src\src\System\Text\Json\Nodes\JsonNode.To.cs:49,47)
```

- 该中止点此前被 §9 的 foreach-source 失败遮蔽（§9 在第 13 个 pass 退出）。

### 10.5 TFM 警告

- `D:\cs\rt10-baseline-w10d.log` 中 `skipped unresolvable TFM value` 计数为 **0**；
  `TFM/NETSDK/targetFramework/MSB` 关键字命中数亦为 **0**。
- conditional 的 `372 directives / 103 disabled blocks` 与 §2/§8/§9 一致。
- 日志计数：`warning ` 行 32 条（与 §9.6 相同）、`[Warning]` 前缀行 8 条
  （tuple 元素类型未解析）、`[Error]` 行 3 条（PD010）。

### 10.6 来源文件

- `D:\cs\rt10-baseline-w10d.log`（stdout+stderr 合并，417 行）
- `D:\cs\rt10-baseline-w10d\preprocess-report.json`（14 个 pass、`success=false`、`totalSeconds=33.481`）
- `D:\cs\rt10-baseline-w10d\.cs2j-preprocessed-src`（改写后的中间源）

## 11. 第五次重跑（HEAD `e7107779`，pattern-desugar 通过）

### 11.1 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；仓库代码 HEAD：`e7107779`
  （`fix(pattern-desugar): 放行 guard 中的 is null/not null，支持 C# 10 扩展属性模式`）。
- 代码前置：全量单测 **4715 通过 / 9 跳过 / 0 失败**（基线 4710 + 新增 5 条
  pattern-desugar 单测）。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10e`；unified 中间树
  `D:\cs\rt10-baseline-w10e\.cs2j-preprocessed-src`（目标目录运行时不存在，无需 `--force`）。
- 命令（与 §10.1 同参数，仅换目标目录与日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10e --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10e.log
```

- 结果：进程退出码 **1**（链在更后面的 pass 中止，非超时/崩溃）。证据：
  `D:\cs\rt10-baseline-w10e.log`（546 行）、
  `D:\cs\rt10-baseline-w10e\preprocess-report.json`（`success=false`、
  `totalSeconds=38.138`、**16** 个 pass）。
- 单 pass 复核（独立目录、无 `--force`）：同一源 + `--pass pattern-desugar`，
  目标 `D:\cs\rt10-pd-w10e1`，`success=true`、`totalSeconds=4.155`，summary：
  `pattern-desugar: 15 transformed, 298 clean, 39 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual`。

### 11.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`/`totalSeconds`）

| # | pass | 状态 | 秒 | 实测 summary（原样节选） |
|---|------|------|----|--------------------------|
| 1 | conditional | ok | 5.552 | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | 1.448 | `0 transformed, 352 clean, 0 #line removed, ...` |
| 3 | partial-merge | ok | 3.240 | `10 group(s) merged, 0 partial member pair(s) collapsed`（9 条 CS2J2001 warning，同 §9/§10） |
| 4 | local-function-lift | ok | 4.616 | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | 0.965 | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | ok | 1.846 | `107 transformed, 209 clean, 36 dirty-no-rewrite; ...; nc: 180 member/call, 24 invoke, 56 void, 42 inline, 83 lifted, 32 coalesce, 164 temps, 0 skipped, 0 residual` |
| 7 | enum | ok | 1.312 | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | 5.749 | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | 0.656 | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple | ok | 6.360 | `tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated` |
| 11 | anonymous-type-lift | ok | 0.897 | `0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual` |
| 12 | foreach-deconstruction | ok | 0.192 | `0 transformed, 358 clean, 0 lowered, 1 skipped` |
| 13 | foreach-source | ok | 0.473 | `9 transformed, 349 clean, 0 tuple residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)` |
| 14 | pattern-desugar | **ok（本轮修复）** | 0.428 | `15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual` |
| 15 | strip-default | ok | 2.807 | `97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated` |
| 16 | ref-out-in | **failed** | 1.566 | `204 transformed, 154 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members` |

- 与 §10.2 逐条对比：第 1–13 个 pass 的 summary 逐字一致（仅耗时数值不同）；
  第 14 个 `pattern-desugar` 由 failed 转为 ok；第 15 个 `strip-default` 在 §10 从未运行
  （链在第 14 个 pass 中止）。

### 11.3 pattern-desugar 通过证据

- §10.4 的 3 条 PD010 全部消失：本轮日志中 `PD010` 命中 **0** 次。
- summary 变化：`transformed 12 → 15`、`pattern-switch 14 → 17`、`govs 6 → 8`、
  `dirty-no-rewrite 39 → 36`、`skipped 3 → 0`、`residual 0 → 0`；`clean` 保持 307
  （3 个原被拒文件从 dirty 转入 transformed）。
- 三处实测输出（中间树原文，逐字）：
  - `src\System\Text\Json\Nodes\JsonNode.To.cs:61`
    （C# 10 扩展属性模式 `Value.ValueKind`，两段路径，中间段为值类型无 null 守卫）：
    `else if ((__cs2jGov_0 is JsonValueOfElement && (jsonElement = (JsonValueOfElement)__cs2jGov_0) != null && jsonElement.Value.ValueKind == JsonValueKind.String))`
  - `src\System\Text\Json\Serialization\JsonConverterOfT.ReadCore.cs:69`
    （guard 中的 `is null` 原样保留，未改写成 `== null`）：
    `else if (ex is JsonException && (jsonEx = (JsonException)ex) != null && (jsonEx.Path is null))`
  - `src\System\Text\Json\Serialization\JsonSerializer.Read.HandleMetadata.cs:369`
    （guard 整体原样保留；前序 pass 已把 `?.` 展开成三元，条件仍是 `is null`）：
    `else if (__cs2jGov_0 == 5 && ((resolver != null ? resolver.CustomTypeDiscriminatorPropertyNameUtf8 : null) is null && propertyName.SequenceEqual("$type"u8)))`
- 注：单 pass 复核处理的是**原始源**（该站点原文为
  `resolver?.CustomTypeDiscriminatorPropertyNameUtf8 is null`），全链中间树中该表达式
  已被前序 pass 展开为三元；两种形态均在 guard 中原样保留，`0 residual`。

### 11.4 新中止点

- 第 16 个 pass `ref-out-in`（`status=failed`，1.566s），summary：
  `ref-out-in: 204 transformed, 154 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members`。
- 该 pass 共 46 条 Error（report `diagnostics` 计数），全部为文本消息、无 CS2J 错误码：
  - **3** 条 `cannot construct a legal ByRef proxy here: unsupported ref/out argument shape`
    （`src\System\Text\Json\Serialization\JsonSerializerOptions.cs`）；
  - **8** 条 `ref/out/in rewrite produced invalid C# syntax; the original file was preserved.`
    （4 个文件各 2 条：`JsonCollectionConverter.cs`、`JsonDictionaryConverter.cs`、
    `ObjectDefaultConverter.cs`、`ObjectWithParameterizedConstructorConverter.cs`）；
  - **35** 条 `internal '…' still declares ref/out parameter 'state'` 形式的锁定成员报错。
- 示例（报告原文节选）：

```
[Error] ObjectDefaultConverter.cs: internal 'System.Text.Json.Serialization.Converters.ObjectDefaultConverter<T>.ReadAheadPropertyValue(scoped ref System.Text.Json.ReadStack, ref System.Text.Json.Utf8JsonReader, System.Text.Json.Serialization.Metadata.JsonPropertyInfo)' still declares ref/out parameter 'state'
```

- 该中止点此前被 §10 的 pattern-desugar 失败遮蔽（§10 在第 14 个 pass 退出）。

### 11.5 TFM 警告

- `D:\cs\rt10-baseline-w10e.log` 中 `skipped unresolvable TFM value` 计数为 **0**；
  `TFM|NETSDK|targetFramework|MSB` 关键字命中数亦为 **0**。
- conditional 的 `66 transformed / 286 clean / 372 directives / 103 disabled blocks`
  与 §2/§8/§9/§10 一致。
- 日志计数：`warning ` 行 32 条（与 §9/§10 相同）、`[Warning]` 前缀行 8 条、
  `[Error]` 行 46 条（全部来自 ref-out-in，无 PD010）。

### 11.6 来源文件

- `D:\cs\rt10-baseline-w10e.log`（stdout+stderr 合并，546 行）
- `D:\cs\rt10-baseline-w10e\preprocess-report.json`（16 个 pass、`success=false`、`totalSeconds=38.138`）
- `D:\cs\rt10-baseline-w10e\.cs2j-preprocessed-src`（改写后的中间源）
- `D:\cs\rt10-pd-w10e1\preprocess-report.json`（单 pass 复核：`success=true`、`totalSeconds=4.155`）

## 12. 第六次重跑（HEAD `2461172a`，ref-out-in 通过）

### 12.1 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；仓库代码 HEAD：`2461172a`
  （`docs(rt10): 落档第五次真源重跑（pattern-desugar 通过，新中止点 ref-out-in）`）。
- 代码前置：全量单测 **4720 通过 / 9 跳过 / 0 失败**（基线 4715 + 新增 5 条
  ref-out-in 单测）。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10f`；unified 中间树
  `D:\cs\rt10-baseline-w10f\.cs2j-preprocessed-src`（目标目录运行时不存在，无需 `--force`）。
- 命令（与 §11.1 同参数，仅换目标目录与日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10f --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10f.log
```

- 结果：进程退出码 **1**（链在更后面的 pass 中止，非超时/崩溃）。证据：
  `D:\cs\rt10-baseline-w10f.log`（442 行）、
  `D:\cs\rt10-baseline-w10f\preprocess-report.json`（`success=false`、
  `totalSeconds=41.356`、**20** 个 pass）。

### 12.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`/`totalSeconds`）

| # | pass | 状态 | 秒 | 实测 summary（原样节选） |
|---|------|------|----|--------------------------|
| 1 | conditional | ok | 5.639 | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | 1.401 | `0 transformed, 352 clean, 0 #line removed, ...` |
| 3 | partial-merge | ok | 3.382 | `10 group(s) merged, 0 partial member pair(s) collapsed`（9 条 CS2J2001 warning，同 §9–§11） |
| 4 | local-function-lift | ok | 4.596 | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | 0.873 | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | ok | 2.701 | `107 transformed, 209 clean, 36 dirty-no-rewrite; ...; nc: 180 member/call, 24 invoke, 56 void, 42 inline, 83 lifted, 32 coalesce, 164 temps, 0 skipped, 0 residual` |
| 7 | enum | ok | 1.489 | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | 6.131 | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | 0.603 | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple | ok | 6.171 | `tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated` |
| 11 | anonymous-type-lift | ok | 0.883 | `0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual` |
| 12 | foreach-deconstruction | ok | 0.272 | `0 transformed, 358 clean, 0 lowered, 1 skipped` |
| 13 | foreach-source | ok | 0.537 | `9 transformed, 349 clean, 0 tuple residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)` |
| 14 | pattern-desugar | ok | 0.423 | `15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual` |
| 15 | strip-default | ok | 2.779 | `97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated` |
| 16 | ref-out-in | **ok（本轮修复）** | 1.577 | `208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members` |
| 17 | unsafe-ref-lowering | ok | 1.190 | `0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)` |
| 18 | using-resource-lift | ok | 0.208 | `0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)` |
| 19 | fixed-span-to-array | ok | 0.231 | `1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)` |
| 20 | compat-method-replacement | **failed** | 0.233 | `1 transformed, 359 clean, 1 method(s) replaced, 0 already replaced, 3 signature mismatch(es) (CMR001), 2 drifted entr(y/ies) (CMR020), 0 unused table entry(ies)` |

- 与 §11.2 逐条对比：第 1–15 个 pass 的 summary 逐字一致（仅耗时数值不同）；
  第 16 个 `ref-out-in` 由 failed 转为 ok；第 17–20 个 pass 在 §11 从未运行。

### 12.3 ref-out-in 通过证据

- diagnostics **0** 条（§11.4 的 46 条全部消失：invalid C# 0、unproxyable 0、
  residual `still declares` 0）。
- summary 变化：`transformed 204 → 208`、`clean 154 → 150`；`245 ref params`、
  `241 out params`、`717 call sites lifted`、`153 locked members` 不变。
- 三个根因（前两个共同表现为语句拆分，第三个是 C# 14 节点形态）：
  1. **生成语句插值丢 trivia**：短路右侧被提升为
     `__scN = __callM is JsonConverter polymorphicConverter;` 时用字符串插值
     `ToString()` 拼语句，`__callM` 与 `is` 粘成单个标识符（CS1002）。修复为
     生成语句前对表达式片段 `NormalizeWhitespace()`（`RefOutInStatementLifter.Text`）。
  2. **`is T v` 模式变量作用域**：holder-only 的 ref 调用无需 slot 时仍被提升成
     `var __callM = …;`，模式变量被声明在生成语句的块内，随后的
     `if (__scN) { … v … }` 变 CS0103。修复为“无 hoist 即原地改写”
     （`LiftInvocation`），短路表达式整体保留；需要 hoist 的 `?.` 调用改为
     CS2J3303 精确诊断并保留源调用。
  3. **`ref field`（C# 14）**：`field` 是 `FieldExpressionSyntax` 而非
     `IdentifierNameSyntax`，落入 `unsupported ref/out argument shape`。修复为在
     `LiftArgument` 的标识符/成员链分支中接受 `FieldExpressionSyntax`，生成
     `ByRef<T>.Create(field)` + 写回 `field = __refN.Value`（ref 写回进 finally）。
- 中间树证据（逐字）：
  - `src\System\Text\Json\Serialization\Converters\Collection\JsonCollectionConverter.cs:171`
    （`is` 模式与模式变量保持在原 if 条件内）：
    `ResolvePolymorphicConverter(jsonTypeInfo, state) is JsonConverter polymorphicConverter)`
  - `src\System\Text\Json\Serialization\JsonSerializerOptions.cs:53-61`
    （`ref field` 的 copy-in/copy-out）：
    `ByRef<System.Text.Json.JsonSerializerOptions?> __ref1 = ByRef<...>.Create(field);`
    `try { __call2 = GetOrCreateSingleton(__ref1, ...); } finally { field = __ref1.Value; }`
- TFM 警告：日志中 **0** 次（`(?i)TFM` 命中 0）。warning 行共 97 条，均为
  `[Warning]` 预处理提示（interpolated-string handler、`scoped` 保留、
  tuple 元素类型回退等）与 CMR001/URL001，不阻断链。

### 12.4 新中止点

- 第 20 个 pass `compat-method-replacement`（`status=failed`，0.233s），共
  **2** 条 Error（CMR020）+ **3** 条 warning（CMR001）：
  - `error CMR020: compat method replacement entry 'System.Text.Json.JsonWriterHelper.ToUtf8(System.ReadOnlySpan<char>, System.Span<byte>, out int)' drifted: ... no declaration matched the registered signature.`
  - `error CMR020: compat method replacement entry 'System.Text.Json.JsonWriterHelper.ToUtf8(System.ReadOnlySpan<byte>, System.Span<byte>, out int, out int)' drifted: ...`
  - `warning CMR001: ... did not match declaration 'System.Text.Json.JsonWriterHelper.ToUtf8(ReadOnlySpan<char>, Span<byte>, ByRef<int>)': parameter 2 ('written') has ref kind None, not Out.`
  - 另 2 条 CMR001 为 `JsonWriterHelper.NeedsEscaping` 的注册签名与声明不匹配
    （与 ref-out-in 无关）。
- 根因方向（下一轮）：ref-out-in 已把 `out int` 改写为 `ByRef<int>`，compat 注册表
  仍按原生 `out int` 匹配；需要让注册表识别改写后的 holder 签名（或在其后按
  `ByRef<T>` 重新登记），本轮未修。

### 12.5 来源文件

- `D:\cs\rt10-baseline-w10f\preprocess-report.json`（schemaVersion 1，
  `success=false`，`totalSeconds=41.356`）。
- `D:\cs\rt10-baseline-w10f.log`（442 行）。
- 中间树：`D:\cs\rt10-baseline-w10f\.cs2j-preprocessed-src`。

## 13. 第七次重跑（HEAD `28c393ba` + 工作树修复，compat-method-replacement 通过）

### 13.1 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；基线 HEAD：`28c393ba`，叠加本次
  compat-method-replacement 修复的工作树改动（尚未提交时运行）。
- 代码前置：全量单测 **4727 通过 / 9 跳过 / 0 失败**（§12 基线 4720 + 新增
  7 条 compat-method-replacement 单测：5 条 holder 签名归一化 + 1 条
  holder 集成 + 1 条普通 `out` 回归）。
- 修复内容：`CompatMethodReplacementEntry.Matches` 在比较前把
  `Cs2jByRef.{ByRef,ScopedByRef,RefStructByRef}<T>` 归一为「`ref` 或 `out` +
  `T`」、`{ReadOnlyByRef,ReadOnlyScopedByRef}<T>` 归一为「`in` + `T`」
  （`Describe` 同步渲染回原形）；`SubstituteParameterNames` 对 holder 参数把
  模板引用改写为 `name.Value`（已有 `.Value` 不重复追加）。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10g`；unified 中间树
  `D:\cs\rt10-baseline-w10g\.cs2j-preprocessed-src`（目标目录运行时不存在，无需 `--force`）。
- 命令（与 §12.1 同参数，仅换目标目录与日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10g --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10g.log
```

- 结果：`D:\cs\rt10-baseline-w10g\preprocess-report.json` 为 `success=false`、
  `totalSeconds=44.502`、**23** 个 pass（比 §12 多跑 3 个 pass）；日志 516 行。

### 13.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`/`totalSeconds`）

| # | pass | 状态 | 秒 | 实测 summary（原样节选） |
|---|------|------|----|--------------------------|
| 1 | conditional | ok | 5.746 | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | 1.453 | `0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), ...` |
| 3 | partial-merge | ok | 3.304 | `10 group(s) merged, 0 partial member pair(s) collapsed`（9 条 CS2J2001 warning，同 §9–§12） |
| 4 | local-function-lift | ok | 4.912 | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | 0.906 | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | ok | 2.063 | `107 transformed, 209 clean, 36 dirty-no-rewrite; ...; nc: 180 member/call, 24 invoke, 56 void, 42 inline, 83 lifted, 32 coalesce, 164 temps, 0 skipped, 0 residual` |
| 7 | enum | ok | 1.462 | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | 5.896 | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | 0.6 | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple | ok | 6.03 | `tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated` |
| 11 | anonymous-type-lift | ok | 0.896 | `0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual` |
| 12 | foreach-deconstruction | ok | 0.234 | `0 transformed, 358 clean, 0 lowered, 1 skipped` |
| 13 | foreach-source | ok | 0.417 | `9 transformed, 349 clean, 0 string site(s) wrapped, 0 tuple residual(s), 0 other/KV residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)` |
| 14 | pattern-desugar | ok | 0.41 | `15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual` |
| 15 | strip-default | ok | 2.846 | `97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated` |
| 16 | ref-out-in | ok | 1.561 | `208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members` |
| 17 | unsafe-ref-lowering | ok | 1.168 | `0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)` |
| 18 | using-resource-lift | ok | 0.198 | `0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)` |
| 19 | fixed-span-to-array | ok | 0.234 | `1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)` |
| 20 | compat-method-replacement | **ok（本轮修复）** | 0.228 | `2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)` |
| 21 | pointer-diff-inline | ok | 0.278 | `0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)` |
| 22 | unsigned | ok | 0.688 | `unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten`（4 条 Cs2jUnsignedMath TFM warning） |
| 23 | numeric-boundary | **failed** | 2.931 | `numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 16 error(s); decimal-ops: ... 314 deferred; increment-targets: ...; nullable-lift: 3 rel/121 eq/2 conv site(s), 1 temp(s), 15 deferred` |

- 与 §12.2 对照：第 1–19 个 pass 的 `transformed`/`clean` 计数一致（个别
  summary 因 `28c393ba` 新增计数器而变长，如 foreach-source 的 string/ref
  计数）；第 20 个 `compat-method-replacement` 由 failed 转为 ok；第 21–23 个
  pass 在 §12 从未运行。

### 13.3 compat-method-replacement 通过证据

- diagnostics 中 **0** 条 CMR020（§12.4 的 2 条 Error 全部消失）；summary 的
  `2 drifted entr(y/ies) (CMR020)` → `0 drifted entr(y/ies) (CMR020)`。
- `2 method(s) replaced` 且 `2 transformed`：`JsonWriterHelper.ToUtf8`
  （3 参、`out int` 已被 ref-out-in 提升为 `ByRef<int>`）与原本就命中的
  `JsonWriterHelper.NeedsEscaping`。
- 中间树证据（逐字）：
  - `src\System\Text\Json\Writer\JsonWriterHelper.cs:266`：
    `// cs2j-compat-method-replacement: System.Text.Json.JsonWriterHelper.ToUtf8(System.ReadOnlySpan<char>, System.Span<byte>, out int)`
  - 同文件签名保持提升后的形态（证明是真正替换而非跳过）：
    `internal static OperationStatus ToUtf8(ReadOnlySpan<char> source, Span<global::System.Byte> destination, ByRef<int> written)`
  - 同文件 body 内的写回（模板 `written = 0;` → `written.Value = 0;`）：
    `written.Value = 0;`、`written.Value = bytesUsed;`
- 剩余 2 条 CMR001 与本修复无关（`JsonWriterHelper.Escaping.cs:286/290`）：
  rt10 的 `NeedsEscaping` 声明是 `NeedsEscaping(byte)`（1 参）与
  `NeedsEscaping(ReadOnlySpan<byte>, JavaScriptEncoder)`，注册条目是
  `ReadOnlySpan<char>` 版本；`1 unused table entry` 为 3.x 遗留的 4 参
  `ToUtf8` 兄弟条目（同 family 已命中，按 W-09 口径不计漂移）。

### 13.4 新中止点

- 第 23 个 pass `numeric-boundary`（`status=failed`，2.931s），共 **16** 条
  Error + 4 条 Warning：
  - `CS2J5339` ×1：`Utf8JsonWriter.WriteValues.StringSegment.cs(532,26)`，
    `value-consumed increment/decrement 'BytesPending++' has no safe single-execution hoist anchor ...`
  - `CS2J5355` ×13：`[NL-2] lifted Nullable<T> site '...' has an unstable operand but
    sits in an anchor-free or conditional/short-circuit/initializer context ...`
    （`JsonSerializer.Read.HandleMetadata.cs:352`、`JsonPropertyInfo.cs:624/656` 等）。
  - `CS2J5357` ×2：`[NL-14] residual built-in lifted Nullable<T> site(s) survived in
    user-scope output: _canUseFastPathSerializationLogic; strict mode fails`。
- 4 条 Warning 均为 `target framework '...' is below net10.0`（unsigned pass 注入
  `Cs2jUnsignedMath.csproj` 的提示，链推进到第 22 个 pass 后才出现）。
- 该中止点此前被 §12 的 compat-method-replacement 失败遮蔽（§12 在第 20 个 pass 退出）。

### 13.5 TFM 警告

- 日志中 `skipped unresolvable TFM value` 计数为 **0**（与 §2–§12 一致）。
- 因链首次运行到 `unsigned`/`numeric-boundary`，日志新增 8 行
  `target framework '...' is below net10.0` 提示（`gen/*.csproj` 3 个 +
  `src/System.Text.Json.csproj`，unsigned 与 numeric-boundary 各报 4 条），
  属 Cs2jUnsignedMath 注入的已知提示，不阻断本 pass 之外的环节。
- 日志计数：`warning ` 行 36 条、`[Warning]` 前缀行 69 条、`[Error]` 行 16 条
  （全部来自 numeric-boundary）。

### 13.6 来源文件

- `D:\cs\rt10-baseline-w10g\preprocess-report.json`（schemaVersion 1，
  `success=false`，`totalSeconds=44.502`，23 个 pass）。
- `D:\cs\rt10-baseline-w10g.log`（516 行）。
- 中间树：`D:\cs\rt10-baseline-w10g\.cs2j-preprocessed-src`。

## 14. 第八次重跑（HEAD `b923e1a4` + 本轮 numeric-boundary 修复）

### 14.1 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；基线 HEAD：`b923e1a4`，叠加本轮
  numeric-boundary（C17 increment-target / C22 nullable-lift）工作树改动
  （运行时尚在工作树，尚未提交）。
- 代码前置：全量单测 **4735 通过 / 9 跳过 / 0 失败**（§13 基线 4727 +
  本轮新增 8 条：`NullableLiftDistributionTests` 6 条 —— if/else-if 条件
  hoist 正例、短路 RHS 负例、整左操作数临时正例、短路 RHS 负例、return
  位置隐式 unwrap 正例、显式 cast 相邻负例；`IncrementLinearizationTests`
  2 条 —— `span[index++] = value` 正例、调用型 receiver 负例）。
- 修复内容（4 条 lane，均为真实降低，未改 strict / 未降级为 Warning）：
  1. **C22 B-1**：`NullableLiftSyntaxFacts.IsWhitelistedAnchor` 纳入
     `IfStatementSyntax`（含 `else if` 链；Pass2 把 `if` 包成
     `{ temps; if (...) ... }`）。循环条件（while/do/for）仍排除。
  2. **C22 B-2**：由 B-1 自动生效 —— `PrepareOperand` 对不稳定操作数本就
     整体 hoist（条件表达式作为一个 `bool?` 临时），不做内部 arm 拆分。
  3. **C17**：新增「元素访问索引后缀自增」lane
     （`FindIndexAssignmentPostfixTarget` / `IsIndexAssignmentOrderSafe`）：
     `container[index++] = value;` 线性化为
     `var o = index; index = o + 1; container[o] = value;`；要求后缀目标
     无 receiver/index 临时，且站点之前的前置效应只允许位于索引 receiver
     链内的纯成员访问（含 invocation/creation/赋值/自增的 receiver 仍报
     CS2J5339）。
  4. **C22 / NL-14**：新增 return 位置隐式 unwrap lane
     （`VisitReturnStatement`）—— null-coalesce pass 把
     `return f ??= v;` 降为 `if (f == null) { f = v; } return f;`，此 return
     无转换语法节点；新建 lane 在「返回槽非可空、表达式为 `T?`」时追加
     `.Value`，`_canUseFastPathSerializationLogic` / `_valueKind` 两条
     CS2J5357 残留因此消失。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10i`；unified 中间树
  `D:\cs\rt10-baseline-w10i\.cs2j-preprocessed-src`（目标目录运行时不存在，无需 `--force`）。
- 命令（与 §13.1 同参数，仅换目标目录与日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10i --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10i.log
```

- 结果：`D:\cs\rt10-baseline-w10i\preprocess-report.json` 为 `success=false`、
  `totalSeconds=42.202`、**23** 个 pass；日志 484 行。
- 另有一次性中间对照：仅 B-1/B-2/A 落地（C22 `VisitReturnStatement` 尚未加）时
  以同参数跑到 `D:\cs\rt10-baseline-w10h`：numeric-boundary 为
  **7** 条 Error（CS2J5355 ×5 + CS2J5357 ×2，CS2J5339 已归零）。C 修复后
  再跑 `w10i` 得下表 5 条。

### 14.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`/`totalSeconds`）

| # | pass | 状态 | 秒 | 实测 summary（原样节选） |
|---|------|------|----|--------------------------|
| 1 | conditional | ok | 5.222 | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | 1.344 | `0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), ...` |
| 3 | partial-merge | ok | 3.109 | `10 group(s) merged, 0 partial member pair(s) collapsed` |
| 4 | local-function-lift | ok | 4.340 | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | 0.878 | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | ok | 1.687 | `107 transformed, 209 clean, 36 dirty-no-rewrite; ...` |
| 7 | enum | ok | 1.431 | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | 5.716 | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | 0.605 | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple | ok | 6.037 | `tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated` |
| 11 | anonymous-type-lift | ok | 0.858 | `0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual` |
| 12 | foreach-deconstruction | ok | 0.253 | `0 transformed, 358 clean, 0 lowered, 1 skipped` |
| 13 | foreach-source | ok | 0.413 | `9 transformed, 349 clean, ..., 7 span index loop(s), 0 unresolved site(s), 0 error(s)` |
| 14 | pattern-desugar | ok | 0.387 | `15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual` |
| 15 | strip-default | ok | 2.789 | `97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated` |
| 16 | ref-out-in | ok | 1.519 | `208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members` |
| 17 | unsafe-ref-lowering | ok | 1.152 | `0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)` |
| 18 | using-resource-lift | ok | 0.205 | `0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)` |
| 19 | fixed-span-to-array | ok | 0.228 | `1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)` |
| 20 | compat-method-replacement | ok | 0.236 | `2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)` |
| 21 | pointer-diff-inline | ok | 0.253 | `0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)` |
| 22 | unsigned | ok | 0.638 | `unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten` |
| 23 | numeric-boundary | **failed** | 2.865 | `numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 5 error(s); decimal-ops: ... 313 deferred; increment-targets: 392 property/0 indexer/0 enum-array/1 simple site(s), 1 unsigned helper call(s), 387 postfix lifted/0 prefix inlined, 387 temp(s), 0 deferred; nullable-lift: 3 rel/129 eq/4 conv site(s), 9 temp(s), 5 deferred` |

- 与 §13.2 对照：第 1–22 个 pass 全部 `ok`，`transformed`/`clean` 计数与 §13
  一致（时间随机器负载浮动）；第 23 个 pass 仍为唯一失败点。

### 14.3 numeric-boundary 前后对照

| 诊断码 | §13（基线） | 本轮 | 变化 |
|--------|-------------|------|------|
| CS2J5339 | 1 | **0** | `StringSegment.cs:532` 的 `_memory.Span[BytesPending++] = Quote;` 已线性化 |
| CS2J5355 | 13 | **5** | 减少 8 条（普通 if/else-if 条件 + 整左操作数条件表达式） |
| CS2J5357 | 2 | **0** | return 位置隐式 unwrap 已补 `.Value` |
| 合计 Error | 16 | **5** | — |

- 统计面（summary 原文）：`nullable-lift: 3 rel/129 eq/4 conv site(s), 9 temp(s),
  5 deferred`（§13 为 `3 rel/121 eq/2 conv, 1 temp, 15 deferred`）；
  `increment-targets: ... 387 postfix lifted/0 prefix inlined, 387 temp(s),
  0 deferred`（§13 未运行到该 lane 的完整 postfix 计数）。

### 14.4 剩余 5 条 CS2J5355 的根因（如实保留 Error，未消音）

- 4 条为**短路 RHS**（B-3 未实现，本轮明确不做）：
  - `src\System\Text\Json\Serialization\JsonSerializer.Read.HandleMetadata.cs(352,30)`：
    `return ... || ((__ed_s2 != null ? ... ) == true);` 中 `||` 右操作数。
  - `src\System\Text\Json\Serialization\Metadata\JsonPropertyInfo.cs(624,49)`：
    `else if (NumberHandling.HasValue && NumberHandling != Strict)` 的 `&&` 右操作数。
  - `gen\JsonSourceGenerator.Parser.cs(834,53)`：
    `if (!isPolymorphic.Value && typeToGenerate.Mode == ...)` 的 `&&` 右操作数。
  - `gen\Model\PropertyGenerationSpec.cs(175,51)`：
    `if (!IsProperty && !HasJsonInclude && (__ed_s0 != null ? ...) != true)` 的
    第三个 `&&` 操作数。
- 1 条为**无语句锚点的字段初始化器**：
  - `gen\JsonSourceGenerator.Parser.cs(1065,125)`：
    `public Dictionary<...> AddedProperties = new((options != null ?
    options.PropertyNameCaseInsensitive : null) == true ? ... : ...);`
    站点位于 primary-constructor 参数驱动的字段初始化器内，没有可插入临时
    声明的语句锚点；且 `bool?` arm 无法在不重复读取属性的前提下拆入条件分支
    （`GetValueOrDefault` 展开本身也需要单次求值临时），故保持 Error。

### 14.5 TFM 警告

- 日志中 `skipped unresolvable TFM value` 计数为 **0**（与 §2–§14 一致）。
- `target framework '...' is below net10.0` 提示仍为 **8** 行（`gen/*.csproj`
  3 个 + `src/System.Text.Json.csproj`，unsigned 与 numeric-boundary 各 4 条），
  即本轮 8 条 Warning 全部来自 Cs2jUnsignedMath 注入的 netstandard2.0 项目。
- 与上述 5 条 Error **无因果关系**：这 8 条是 `unsigned` pass 注入
  `Cs2jUnsignedMath.csproj`(net10.0) 的 TFM 提示，numeric-boundary 的
  CS2J5355 是 C22 语义判定，与 TFM 无关。
- 日志计数：`warning ` 行 36 条、`[Warning]` 前缀行 69 条、`[Error]` 行 5 条
  （全部来自 numeric-boundary）。

### 14.6 单测（先红后绿证据）

- `NullableLiftDistributionTests`（9 条，含新增 6 条）：
  - 红：临时把 `IsWhitelistedAnchor` 的 `IfStatement` 分支改为 `false` →
    2 条正例失败（`UnstableNullableProperty_In_ElseIf_Condition_Is_Hoisted_Once`、
    `Conditional_Left_Operand_Is_Hoisted_Whole_And_Reads_Once`），负例仍绿；
    恢复后 9/9 通过。
  - 红：临时让 return-unwrap lane 的 `returnType` 恒为 null →
    `Implicit_Unwrap_In_Return_Position_Becomes_Value_And_Clears_Residual`
    失败，诊断正文为 `CS2J5357 ... _flag, _kind`（与 rt10 症状一致）；
    恢复后通过。
- `IncrementLinearizationTests`（16 条，含新增 2 条）：
  - 红：临时把新 lane 的 `indexTarget` 置空 →
    `Span_Element_Index_Postfix_Linearizes_Before_Statement` 失败并报
    CS2J5339，负例仍绿；恢复后 16/16 通过。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4735 通过 / 9 跳过 / 0 失败**（1 m 45 s）。

### 14.7 来源文件

- `D:\cs\rt10-baseline-w10i\preprocess-report.json`（schemaVersion 1，
  `success=false`，`totalSeconds=42.202`，23 个 pass）。
- `D:\cs\rt10-baseline-w10i.log`（484 行）。
- 中间树：`D:\cs\rt10-baseline-w10i\.cs2j-preprocessed-src`。
- 中间对照：`D:\cs\rt10-baseline-w10h\preprocess-report.json`（7 条 Error）与
  `D:\cs\rt10-baseline-w10h.log`。

## 15. 第九次重跑（HEAD `398acbd6` + 本轮 numeric-boundary 清零）

### 15.1 运行口径

- 运行日期：2026-11-06（Asia/Shanghai）；基线 HEAD：`398acbd6`，叠加本轮
  numeric-boundary（C22 nullable-lift）工作树改动（运行时尚在工作树，尚未提交）。
- 代码前置：全量单测 **4741 通过 / 9 跳过 / 0 失败**（§14 基线 4735 +
  本轮新增 6 条 `NullableConditionalArmLiftingTests`：`bool? == true` 在
  `||` 右操作数（三态 + 短路）、字段初始化器（无锚点）、嵌套条件 arm 单次
  求值、`== false` 负例、`else if (A && B)` 含 else、三段 `&&` 无 else）。
- 修复内容（3 条 lane，均为真实降低，未改 strict / 未降级为 Warning）：
  1. **S1 `bool? == true` / `!= true`**（`NullableLiftRewriter`
     `TryRewriteBooleanTrueEquality` / `ExpandBooleanTrueTest`）：`true`
     字面量是二值形态（null 与 false 同真值），普通一侧 HasValue guard
     无法 hoist 时把测试分发进源条件表达式的 arm（null arm → `false`，
     `!=` 时 → `true`）；叶 `bool?` 走 NL-6 的 HasValue/Value 展开，
     `(bool?)e`（e 为非可空 bool）直接折叠为 `e`，保持单次求值。
     `== false` / `!= false` 不适用（null 时结果翻转），仍走 hoisting。
  2. **S2 `if (A && B)` 合取线性化**（新增
     `NullableIfConjunctionLinearizer`）：在首个需要 hoist 的合取分量处
     拆分 —— 无 else 时改为嵌套 if；有 else 时改为
     `bool __cs2j_n_scN = false; if (A) { <temps>; __cs2j_n_scN = B; } if (__cs2j_n_scN) body else otherwise;`，
     A/B/body/otherwise 各自只求值一次，短路频率与求值顺序不变。
  3. **S3 `||` 右操作数**：由 S1 直接消除（站点 1 的 RHS 不再含 lifted
     站点），未新增 return 线性化。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10j`；unified 中间树
  `D:\cs\rt10-baseline-w10j\.cs2j-preprocessed-src`（目标目录运行时不存在，无需 `--force`）。
- 命令（与 §14.1 同参数，仅换目标目录与日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10j --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10j.log
```

- 结果：`D:\cs\rt10-baseline-w10j\preprocess-report.json` 为
  `success=false`、`totalSeconds=52.208`、**26** 个 pass；日志 554 行。
- 与 §14 的关键差异：numeric-boundary 首次 `status=ok`，链路因此继续执行
  `generic` / `thread-static` / `generic-deepen` 三个 pass，最终停在
  `generic-deepen`（§15.5）。

### 15.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`/`totalSeconds`）

| # | pass | 状态 | 秒 | 实测 summary（原样节选） |
|---|------|------|----|--------------------------|
| 1 | conditional | ok | 6.118 | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | 1.130 | `0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), ...` |
| 3 | partial-merge | ok | 3.321 | `10 group(s) merged, 0 partial member pair(s) collapsed` |
| 4 | local-function-lift | ok | 4.767 | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | 1.118 | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | ok | 1.927 | `107 transformed, 209 clean, 36 dirty-no-rewrite; ...` |
| 7 | enum | ok | 1.317 | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | 6.482 | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | 0.544 | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple | ok | 6.231 | `tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated` |
| 11 | anonymous-type-lift | ok | 0.936 | `0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual` |
| 12 | foreach-deconstruction | ok | 0.200 | `0 transformed, 358 clean, 0 lowered, 1 skipped` |
| 13 | foreach-source | ok | 0.641 | `9 transformed, 349 clean, ..., 7 span index loop(s), 0 unresolved site(s), 0 error(s)` |
| 14 | pattern-desugar | ok | 0.406 | `15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual` |
| 15 | strip-default | ok | 2.835 | `97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated` |
| 16 | ref-out-in | ok | 1.645 | `208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members` |
| 17 | unsafe-ref-lowering | ok | 1.211 | `0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)` |
| 18 | using-resource-lift | ok | 0.216 | `0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)` |
| 19 | fixed-span-to-array | ok | 0.245 | `1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)` |
| 20 | compat-method-replacement | ok | 0.238 | `2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)` |
| 21 | pointer-diff-inline | ok | 0.276 | `0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)` |
| 22 | unsigned | ok | 0.624 | `unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten` |
| 23 | numeric-boundary | **ok** | 3.402 | `numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 0 error(s); decimal-ops: ... 291 deferred; increment-targets: 392 property/0 indexer/0 enum-array/1 simple site(s), 1 unsigned helper call(s), 387 postfix lifted/0 prefix inlined, 387 temp(s), 0 deferred; nullable-lift: 3 rel/134 eq/4 conv site(s), 11 temp(s)` |
| 24 | generic | ok | 1.354 | `7 transformed, 354 clean (16 default(T) encountered, 16 rewritten, 0 nullability tests folded, 2 new T(), 1 new T[n])` |
| 25 | thread-static | ok | 0.653 | `1 transformed, 360 clean (1 fields rewritten, 0 skipped, 8 reads, 2 writes, 0 compound, 0 increments, 0 struct lifts, 0 initializers, 0 plan rollback(s), 0 residual)` |
| 26 | generic-deepen | **failed** | 4.326 | `147 transformed, 214 clean (254 anchors, 16 nested propagations, 4 patterns, 7 type tests, 0 catches, 23 static methods, 17 field accesses, 526 call sites, 0 expression-tree skips, 0 plans skipped, 0 ctor-token types, 0 ctor-token sites, 0 ctor-token call sites, 1 mtok methods, 1 mtok sites, 1 mtok call sites, 1 residuals)` |

- 第 1–25 个 pass 全部 `ok`；`transformed`/`clean` 计数与 §14 一致（时间随
  机器负载浮动，`numeric-boundary` 3.402s vs §14 2.865s）。

### 15.3 numeric-boundary 前后对照

| 诊断码 | §14（w10i） | 本轮（w10j） | 变化 |
|--------|-------------|--------------|------|
| CS2J5339 | 0 | **0** | — |
| CS2J5355 | 5 | **0** | 5 条站点全部消除 |
| CS2J5357 | 0 | **0** | — |
| 合计 Error | 5 | **0** | — |

- 统计面（summary 原文）：`nullable-lift: 3 rel/134 eq/4 conv site(s),
  11 temp(s)`（§14 为 `3 rel/129 eq/4 conv, 9 temp, 5 deferred`）：
  eq 桶 +5（S1 的 5 条站点计入 one-sided equality），temp +2（S2 的两条
  合取右臂各 hoist 一个枚举属性临时），deferred 5 → 0。
- `CS2J5355`/`CS2J5357` 在整份日志中均为 **0** 次命中。

### 15.4 五条站点的实际产物

- `src\System\Text\Json\Serialization\JsonSerializer.Read.HandleMetadata.cs`
  （`||` 右操作数）：`(... || ((__ed_s2 != null ? (__ed_s3 != null ?
  __ed_s3.AsSpan().SequenceEqual(propertyName) : false) : false)));`
  —— 分发到条件 arm + `(bool?)e` 折叠，`SequenceEqual` 单次求值，无临时。
- `src\System\Text\Json\Serialization\Metadata\JsonPropertyInfo.cs(624)`
  （`else if` 合取右臂）：`else if (NumberHandling.HasValue) { { var
  __cs2j_n_a1 = NumberHandling; if (!(__cs2j_n_a1.HasValue &&
  __cs2j_n_a1.Value == JsonNumberHandling.Strict)) { throw ... } } }`。
- `gen\JsonSourceGenerator.Parser.cs(834)`（`if` 合取右臂）：
  `if (!isPolymorphic.Value) { { var __cs2j_n_a2 = typeToGenerate.Mode; if
  ((__cs2j_n_a2.HasValue && __cs2j_n_a2.Value == ...Serialization)) { ... } } }`。
- `gen\JsonSourceGenerator.Parser.cs(1065)`（字段初始化器）：
  `new((options != null ? (options.PropertyNameCaseInsensitive.HasValue ?
  options.PropertyNameCaseInsensitive.Value : default(bool)) : false) ?
  StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)` —— 无锚点
  需求，属性为生成型 auto-property（纯读）。
- `gen\Model\PropertyGenerationSpec.cs(175)`（`&&` 链末 `!= true`）：
  `if (!IsProperty && !HasJsonInclude && (__ed_s0 != null ?
  (!(__ed_s0.IncludeFields.HasValue ? __ed_s0.IncludeFields.Value :
  default(bool))) : true))` —— `null != true` 为 true，与原语义一致。

### 15.5 新中止点

- numeric-boundary 通过后链路继续执行到第 26 个 pass `generic-deepen`
  （`status=failed`），新增中止点为该 pass 的静态成员/generic-deepen
  报错，report 共 **8** 条 Error：
  - `CS2J1102` ×5：`JsonPropertyInfoOfT.cs:144/149/154` 的
    `shouldSerializeIgnoreConditionNever/Always/WhenWritingDefault`
    （以 method group 形式被委托转换引用，hoist 会改变委托形状）、
    `ObjectWithParameterizedConstructorConverter.Small.cs:63` 的
    `TryRead<TArg>` 与 `JsonDictionaryConverter.cs:99` 的
    `GetConverter<T>`（方法自有类型参数与外围类参数冲突）。
  - `CS2J1106` ×2：`ByteConverter.cs:61` 的 `GetSchemaForNumericType(...)`
    与 `JsonValueOfTPrimitive.cs:25` 的 `DetermineValueKind(value)`
    （调用点未揭示封闭类型构造）。
  - `CS2J1120` ×1：`EnumConverter.cs:617` 的
    `forward-only-without-anchor` 残留 1 条。
- 同时该 pass 有 `CS2J1107` ×12（Anchor 警告）与 `CS2J2001` ×9（partial
  merge 冲突警告），均为 Warning，不阻断。
- 本轮未修改上述 pass，也未降级其诊断。

### 15.6 TFM 警告

- 日志中 `skipped unresolvable TFM value` 计数为 **0**（与 §2–§14 一致）。
- `target framework '...' is below net10.0` 提示与 §14 相同（`gen/*.csproj`
  3 个 + `src/System.Text.Json.csproj`，由 Cs2jUnsignedMath 注入），
  本轮**未新增** TFM 警告。
- 日志计数（控制台折行使文本行数不等于诊断数，诊断总数以 report 为准）：
  `warning ` 文本行 48 条、`[Warning]` 前缀行 69 条；Error 全部来自
  `generic-deepen`（report 8 条），numeric-boundary 为 0 条。

### 15.7 单测（先红后绿证据）

- `NullableConditionalArmLiftingTests`（新增 6 条）：
  - 红：临时 `git stash` 三处源码改动（`NullableLiftAnalyzer` /
    `NullableLiftRewriter` / `NullableLiftRule`）并移出新增
    `NullableIfConjunctionLinearizer` → 4 条正例失败
    （`BooleanTrue_In_ShortCircuit_Right_Is_TwoValued_And_ShortCircuits`、
    `BooleanTrue_In_Field_Initializer_Distributes_Without_Anchor`、
    `Conjunction_In_ElseIf_With_Else_Keeps_Order_Frequency_And_Else`、
    `Conjunction_If_Without_Else_Nests_And_Preserves_Frequency`；4 failed /
    1 passed，`BooleanFalse_Literal_Stays_On_The_Hoisting_Path` 负例两态
    均绿）；恢复后 6/6 通过。第 6 条
    `BooleanTrue_Over_Non_Nullable_Bool_Arm_Reads_Once` 覆盖 S1 内的
    `(bool?)e` 折叠（在红检查之后补入）。
- `NullableLiftDistributionTests`：原两条「仍报 CS2J5355」的结构性负例
  （`UnstableNullableProperty_In_ShortCircuit_If_Condition_...`、
  `Conditional_Left_Operand_In_ShortCircuit_Right_...`）按新能力改为正例
  断言（无 Error、临时 hoist 一次 / 无临时、三态行为一致）。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4741 通过 / 9 跳过 / 0 失败**（1 m 47 s）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`。

### 15.8 来源文件

- `D:\cs\rt10-baseline-w10j\preprocess-report.json`（schemaVersion 1，
  `success=false`，`totalSeconds=52.208`，26 个 pass）。
- `D:\cs\rt10-baseline-w10j.log`（554 行）。
- 中间树：`D:\cs\rt10-baseline-w10j\.cs2j-preprocessed-src`。
- 对照：`D:\cs\rt10-baseline-w10i\preprocess-report.json`（numeric-boundary
  5 条 CS2J5355，23 个 pass）。

## 16. 第十次重跑（HEAD `add6af02` + 本轮 generic-deepen 修复）

### 16.1 运行口径

- 运行日期：2026-11-06/07（Asia/Shanghai，跨零点）；基线 HEAD：`add6af02`，
  叠加本轮 generic-deepen（GD-STATIC / 残留门）工作树改动（运行时尚在工作树，
  尚未提交）。
- 代码前置：全量单测 **4749 通过 / 9 跳过 / 0 失败**（§15 基线 4741 +
  本轮新增 8 条，见 §16.7）。
- 修复内容（4 族，均为真实降低，未改 strict / 未降级为 Warning）：
  1. **隐式基类静态调用（CS2J1106）**：`RenderMethodTypeArguments` 在语法路径
     都给不出构造时，回退到**绑定符号的 constructed containing type**，按类型
     参数名在 containing 链上取闭型实参
     （`FindTypeArgumentText` + `ToMinimalDisplayString`），覆盖
     `ByteConverter : JsonPrimitiveConverter<byte>` 这类基列表替换。
  2. **方法组引用（CS2J1102）**：`HasMethodGroupReference` 的拒绝门改为
     `CollectMethodGroupIdentifiers` 收集站点，提升后把每个引用改写成
     **保形 lambda**（参数形状取自目标委托的 Invoke，闭型实参显式拼进调用）；
     委托形状不可保时在**精确站点**报 CS2J1102 并保留方法。
  3. **静态方法自带类型参数（CS2J1102）**：冲突诊断从候选池前移到
     fixpoint 内——只有方法**确实需要提升**（签名/体/已提升被调用的类参数实参）
     时才报；不引用外围类参数的静态泛型方法不再误报。
  4. **残留门（CS2J1120）**：`GenericDeepenResidualScanner` 对 GD-STATIC
     提升计划中的方法（按 containing 类型 + 方法名 + 参数个数匹配）豁免
     **被提升的类类型参数**的 `forward-only-without-anchor` 判定——提升本身
     就在每个调用点显式给出该参数。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10k`；unified 中间树
  `D:\cs\rt10-baseline-w10k\.cs2j-preprocessed-src`（目标目录运行时不存在，未用 `--force`）。
- 命令（与 §15.1 同参数，仅换目标目录与日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10k --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10k.log
```

- 结果：`D:\cs\rt10-baseline-w10k\preprocess-report.json` 为
  `success=false`、`totalSeconds=55.5`、**27** 个 pass；日志 528 行；退出码 1。
- 与 §15 的关键差异：`generic-deepen` 首次 `status=ok`（8 条 Error 全部消除），
  链路因此继续执行第 27 个 pass `closed-metadata`，最终停在该 pass（§16.5）。

### 16.2 每个 pass 的实测（来源：preprocess-report.json 的 `status`/`summary`/`totalSeconds`）

| # | pass | 状态 | 秒 | 实测 summary（原样节选） |
|---|------|------|----|--------------------------|
| 1 | conditional | ok | 6.049 | `strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed` |
| 2 | lexical-normalize | ok | 1.456 | `0 transformed, 352 clean, ... 0 interpolated skipped (r 0/v 0/i 0)` |
| 3 | partial-merge | ok | 3.526 | `10 group(s) merged, 0 partial member pair(s) collapsed` |
| 4 | local-function-lift | ok | 4.735 | `28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual` |
| 5 | null-coalesce | ok | 0.849 | `59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual` |
| 6 | expression-desugar | ok | 2.127 | `107 transformed, 209 clean, 36 dirty-no-rewrite; ... residual 0`（init 段 96 residual） |
| 7 | enum | ok | 1.576 | `85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual` |
| 8 | enum-declaration | ok | 6.166 | `6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual` |
| 9 | delegate | ok | 0.663 | `1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual` |
| 10 | tuple | ok | 6.525 | `tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated` |
| 11 | anonymous-type-lift | ok | 0.947 | `0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual` |
| 12 | foreach-deconstruction | ok | 0.200 | `0 transformed, 358 clean, 0 lowered, 1 skipped` |
| 13 | foreach-source | ok | 0.430 | `9 transformed, 349 clean, ..., 7 span index loop(s), 0 unresolved site(s), 0 error(s)` |
| 14 | pattern-desugar | ok | 0.409 | `15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual` |
| 15 | strip-default | ok | 2.928 | `97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated` |
| 16 | ref-out-in | ok | 1.651 | `208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members` |
| 17 | unsafe-ref-lowering | ok | 1.210 | `0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)` |
| 18 | using-resource-lift | ok | 0.217 | `0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)` |
| 19 | fixed-span-to-array | ok | 0.238 | `1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)` |
| 20 | compat-method-replacement | ok | 0.238 | `2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)` |
| 21 | pointer-diff-inline | ok | 0.283 | `0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)` |
| 22 | unsigned | ok | 0.662 | `unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten` |
| 23 | numeric-boundary | ok | 3.296 | `numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 0 error(s); ... nullable-lift: 3 rel/134 eq/4 conv site(s), 11 temp(s)` |
| 24 | generic | ok | 1.546 | `7 transformed, 354 clean (16 default(T) encountered, 16 rewritten, 0 nullability tests folded, 2 new T(), 1 new T[n])` |
| 25 | thread-static | ok | 0.641 | `1 transformed, 360 clean (1 fields rewritten, 0 skipped, 8 reads, 2 writes, 0 compound, 0 increments, 0 struct lifts, 0 initializers, 0 plan rollback(s), 0 residual)` |
| 26 | generic-deepen | **ok** | 4.106 | `158 transformed, 203 clean (254 anchors, 16 nested propagations, 4 patterns, 7 type tests, 0 catches, 28 static methods, 17 field accesses, 542 call sites, 0 expression-tree skips, 0 plans skipped, 0 ctor-token types, 0 ctor-token sites, 0 ctor-token call sites, 1 mtok methods, 1 mtok sites, 1 mtok call sites, 0 residuals)` |
| 27 | closed-metadata | **failed** | 2.780 | `388 registered (1975 enumerated, 1587 deduplicated, 3 register files, 0 consumer sites linked, 443 dynamic sites skipped, 139 residuals)` |

- 第 1–26 个 pass 全部 `ok`；`generic-deepen` 的 transformed 由 147 升到 158、
  static methods 23 → 28、call sites 526 → 542、residuals 1 → 0（本轮新增
  提升的 5 个方法与 16 个调用点）。

### 16.3 generic-deepen 8 条 Error 的逐条状态

| 族 | 原报错 | 站点 | 本轮状态 |
|----|--------|------|----------|
| 1 | CS2J1106 ×2 | `ByteConverter.cs:61` 的 `GetSchemaForNumericType(...)`；`JsonValueOfTPrimitive.cs:25` 的 `DetermineValueKind(value)` | **消除**（0 条），见 §16.4 |
| 2 | CS2J1102 ×3 | `JsonPropertyInfoOfT.cs:144/149/154` 三个静态方法的 method group 引用 | **消除**（0 条），5 处引用改写为 lambda |
| 3 | CS2J1102 ×2 | `ObjectWithParameterizedConstructorConverter.Small.cs:63` 的 `TryRead<TArg>`；`JsonDictionaryConverter.cs:99` 的 `GetConverter<T>` | **消除**（0 条）：二者均不引用外围类参数，无需提升，不再误报 |
| 4 | CS2J1120 ×1 | `EnumConverter.cs:617` 的 `ResolveEnumFields<T>` `forward-only-without-anchor` | **消除**（0 条）：提升方法携带的提升参数由调用点显式给出，残留门豁免 |

- `generic-deepen` 的 report diagnostics 中 CS2J1102/CS2J1106/CS2J1120 命中
  均为 **0**；仍为 12 条 CS2J1107（Anchor 警告，与 §15 同集合）与 9 条
  CS2J2001（partial-merge 警告），均为 Warning，不阻断。

### 16.4 修复后的实际产物（逐站点）

- `src\System\Text\Json\Serialization\Converters\Value\ByteConverter.cs:59`：
  `GetSchemaForNumericType<byte>(JsonSchemaType.Integer, numberHandling, isIeeeFloatingPoint: false);`
- `src\System\Text\Json\Nodes\JsonValueOfTPrimitive.cs:24`：
  `_valueKind = DetermineValueKind<TValue>(value);`
- `src\System\Text\Json\Serialization\Metadata\JsonPropertyInfoOfT.cs:155/160/165`：
  三个方法提升为 `private static bool shouldSerializeIgnoreConditionNever<T>(object _, T? value)`
  （`...Always<T>` / `...WhenWritingDefault<T>` 同形）；`:438/441/446/456/460`
  五处引用改写为
  `ShouldSerialize = (arg1, arg2) => shouldSerializeIgnoreConditionNever<T>(arg1, arg2);`
  （`arg1`/`arg2` 取自 `Func<object, T?, bool>` 的 Invoke 参数名）。
- `ObjectWithParameterizedConstructorConverter.Small.cs:63` 的
  `private static bool TryRead<TArg>(...)` 与 `JsonDictionaryConverter.cs:95` 的
  `protected static JsonConverter<T> GetConverter<T>(JsonTypeInfo typeInfo)`
  保持原样（未提升、未加诊断），既有调用 `GetConverter<TKey>(...)` /
  `GetConverter<TValue>(...)` 不变。
- `src\System\Text\Json\Serialization\Converters\Value\EnumConverter.cs`：
  `getFields<T>()`（:57）、`ResolveEnumFields<T>(namingPolicy)`（:617）、
  调用点 `ResolveEnumFields<T>(namingPolicy)`（:70）、体内
  `getFields<T>()`（:624）——提升后不再触发残留；`ResolveEnumFields<T>` 体内
  无自用 token 锚点，语义与 §15 相同。

### 16.5 新中止点

- `generic-deepen` 通过后链路继续执行到第 27 个 pass `closed-metadata`
  （`status=failed`），report 共 **1** 条 Error：
  `CS2J1220: [Residual] 139 residual site(s) of rule 'bare-typeof-passthrough'
  remain after the closed-metadata rewrite (D:\cs\rt10-baseline-w10k\.cs2j-preprocessed-src\Common\ReflectionExtensions.cs:223,
  ...\gen\Helpers\KnownTypeSymbols.cs:25, ...:28); the post-rewrite gate requires zero (design §5.4).`
- 该 pass 自身为 `388 registered / 1975 enumerated / 1587 deduplicated /
  0 consumer sites linked / 443 dynamic sites skipped / 139 residuals`。
- 站点为源级开放泛型 `typeof(...)` 透传（例：`ReflectionExtensions.cs:223`
  的 `typeof(KeyValuePair<,>)`、`KnownTypeSymbols.cs:25/28` 的
  `typeof(IList<>)` / `typeof(ICollection<>)`）；`ContainsGenericConstruction`
  把 unbound generic 记为泛型构造。**该 pass 首次被执行**（§15 在第 26 个 pass
  中止），与本轮 generic-deepen 改动无交集（本轮只新增 `typeof(T)` 锚点与
  闭型非泛型 token 实参），如实记为**新暴露的既有缺口**，本轮不处理。
- 本轮未修改 closed-metadata，也未降级其诊断。

### 16.6 TFM 警告

- 日志中 `skipped unresolvable TFM value` 计数为 **0**（与 §2–§15 一致）。
- `target framework '...' is below net10.0` 提示共 **8** 行，与 §15（w10j）
  完全相同（`gen/*.csproj` 3 个 + `src/System.Text.Json.csproj`，由
  Cs2jUnsignedMath 注入），本轮**未新增**。
- 日志计数：`warning ` 文本行 48 条、`[Warning]` 前缀行 69 条；` error `
  文本行 1 条（`closed-metadata` 的 CS2J1220）。

### 16.7 单测（先红后绿证据）

- `GenericDeepenTests` 新增 8 条（每族 1 正例 + 1 相邻负例）：
  `StaticMethod_ImplicitBaseStaticCall_UsesClosedConstruction`、
  `StaticMethod_ImplicitBaseStaticCall_DoesNotDoubleApplyExplicitReceiver`、
  `StaticMethod_MethodGroupReference_IsWrappedInShapePreservingLambda`、
  `StaticMethod_MethodGroupOfNonHoistedMethod_IsLeftVerbatim`、
  `StaticMethodWithOwnTypeParams_NoClassParamReference_IsLeftInPlace`、
  `StaticMethodWithOwnTypeParams_ClassParamThroughHoistedCallee_StillReportsCS2J1102`、
  `Residual_HoistedStaticForwarder_HasNoFinding`、
  `Residual_UnhoistedStaticGenericForwarder_StillCounts`。
- 红：仅加测试、不改源码运行 `GenericDeepenTests` → **5 failed / 113 passed**
  （族 1×2、族 2 正例、族 3 正例、族 4 正例）；恢复源码后 **118/118 通过**。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4749 通过 / 9 跳过 / 0 失败**（1 m 47 s）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`。

### 16.8 来源文件

- `D:\cs\rt10-baseline-w10k\preprocess-report.json`（schemaVersion 1，
  `success=false`，`totalSeconds=55.5`，27 个 pass）。
- `D:\cs\rt10-baseline-w10k.log`（528 行）。
- 中间树：`D:\cs\rt10-baseline-w10k\.cs2j-preprocessed-src`。
- 对照：`D:\cs\rt10-baseline-w10j\preprocess-report.json`（generic-deepen
  8 条 Error，26 个 pass）。

## 17. 第十一次重跑（HEAD `f95bfc25` + 本轮 closed-metadata 规则 3 口径对齐）

### 17.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；基线 HEAD：`f95bfc25`，叠加本轮
  closed-metadata 规则 3 修复（运行时尚在工作树，尚未提交）。
- 代码前置：全量单测 **4752 通过 / 9 跳过 / 0 失败**（§16 基线 4749 +
  本轮净增 3 条，见 §17.7）。
- 修复内容（2 条，均未改 strict、未把 Error 降级为 Warning）：
  1. **未绑定构造归 CM-DYN**：`ClosedMetadataResidualScanner.ScanTypeOfNodes`
     在 `ContainsGenericConstruction` 之后跳过
     `ClosedMetadataCollector.ContainsUnboundGenericConstruction`（新增：递归
     `INamedTypeSymbol.IsUnboundGenericType`，含未绑定泛型数组元素）与
     `MentionsTypeParameter`（同一递归检测器，V-06 既有口径）。二者都是收集器
     的 CM-DYN 动态站点（CS2J1210），不可闭化、本来就不登记，规则 3 不再重复
     计数。`typeof(IList<>)` 的 `IsDefinition=false`（≠ 开放定义），仅靠
     `ContainsGenericConstruction` 无法区分，这是原 139 条的 85 条来源。
  2. **已登记闭型不算泄漏**：规则 3 与收集器改为**同一事实源**——用
     `analysis.ClosedTypes` 的 `ClosedTypeKey.SortText` 集合判定；沿
     `ClosedMetadataGuardResolver.EnumerateTypeClosure` 检查该 `typeof` 提及的
     **每一层闭型**（嵌套实参、`List<short>[]` 这类泛型元素数组本身），全部
     有记录才放行，任一缺失仍报 `bare-typeof-passthrough`。判定不依赖
     holder 可达性（见 §17.5 的观察）。
- 源不变：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-baseline-w10l`；unified 中间树
  `D:\cs\rt10-baseline-w10l\.cs2j-preprocessed-src`（目标目录运行前不存在，
  未用 `--force`）。
- 命令（与 §16.1 同参数，仅换目标目录与日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10l --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10l.log
```

- 结果：`D:\cs\rt10-baseline-w10l\preprocess-report.json` 为
  `success=false`、`totalSeconds=56.789`、**28** 个 pass；日志 995 行；退出码 1。
- 与 §16 的关键差异：`closed-metadata` 由 `failed` 变 **`ok`**（139 →
  **0** residuals），链路因此首次进入第 29 个 pass `variance-audit` 并停在那里
  （§17.6）。

### 17.2 第 27–29 个 pass 的实测（`preprocess-report.json`）

| # | pass | 状态 | 秒 | 实测 summary（原样节选） |
|---|------|------|----|--------------------------|
| 27 | generic-deepen | ok | 4.427 | `158 transformed, 203 clean (... 542 call sites, ... 0 residuals)`（与 §16 同） |
| 28 | closed-metadata | **ok** | 2.572 | `388 registered (1975 enumerated, 1587 deduplicated, 3 register files, 0 consumer sites linked, 443 dynamic sites skipped, 0 residuals)` |
| 29 | variance-audit | **failed** | 1.922 | `0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 65 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json` |

- 第 1–27 个 pass 全部 `ok`，与 §16.2 一致（本轮只改 residual 判定，不改任何
  改写产物；`closed-metadata` 的改写计数 388/1975/1587/3/0/443 与 §16 完全相同）。
- `CS2J1220` 在日志与 report 中均为 **0** 次（`Select-String CS2J1220` 计数 0）；
  `closed-metadata` 的 `residuals` 汇总为 **0**。

### 17.3 原 139 条的实测分类（修复依据）

在同一语料上以环境变量门控的临时插桩（`CMDBG|文件:行|类型|分类`，仅统计用，
已随本轮提交移除）逐条记录，记录总数 **139**，与 §16.5 的残留数逐条对齐：

| 分类 | 条数 | 判定依据 | 代表站点 |
|------|------|----------|----------|
| 未绑定构造（unbound） | **85** | `IsUnboundGenericType=true` 且 `IsDefinition=false` | `IEnumerableConverterFactory.cs` 31、`gen\Helpers\KnownTypeSymbols.cs` 29（`typeof(IList<>)`/`typeof(ICollection<>)`/`typeof(Dictionary<,>)`）、`FSharpTypeConverterFactory.cs` 5、`MemoryConverterFactory.cs` 5、`ObjectConverterFactory.cs` 3、`JsonTypeInfo.Cache.cs` 2、`IAsyncEnumerableConverterFactory.cs` 2、其余 6 个文件各 1（含 `Common\ReflectionExtensions.cs:223` 的 `typeof(KeyValuePair<,>)`） |
| 提及类型参数（CM-DYN） | **13** | `MentionsTypeParameter=true`（收集器已按 CM-DYN 报 CS2J1210，不登记） | `List<TElement>` ×3、`Dictionary<TKey,TValue>` ×2、`IEnumerable<KeyValuePair<TKey,TValue>>`、`IAsyncEnumerable<TElement>`、`HashSet<TElement>`、`IEnumerable<TElement>`、`Func<...,TCollection>` ×2、`Cs2jTuples.TupleT0T1<...>` 与 `TupleT0T1T2<...>` 各 1；文件为 3 个 Reflection*Accessor 各 2 + 7 个转换器文件各 1 |
| 已登记闭型（registered） | **41** | `analysis.ClosedTypes` 中存在对应 `ClosedTypeKey` | `Nullable<T>` 系 38（`long?` 6，`DateTime?`/`DateTimeOffset?`/`ulong?`/`sbyte?`/`bool?`/`Guid?`/`char?`/`short?`/`decimal?`/`int?`/`double?`/`uint?`/`ushort?`/`byte?`/`float?` 各 2）；`List<object?>` 2；`IDictionary<string,object>`、`Dictionary<string,object?>`、`IDictionary<string,JsonElement>` 各 1；站点分布 `JsonValueOfJsonPrimitive.cs` 16、`JsonValueOfElement.cs` 16、`JsonException.cs` 4、`JsonTypeInfo.Cache.cs` 2、`IListConverter.cs`/`IEnumerableConverter.cs`/`IDictionaryConverter.cs` 各 1 |
| 未登记闭型（unregistered） | **0** | 上二者之外的闭型 | — |

- 结论：139 = 85（CM-DYN 未绑定）+ 13（CM-DYN 类型参数）+ 41（已登记）+
  0（未登记）。前两类不是闭型（规则 3 无法也不应修），第三类有 Java 侧元数据，
  本质是**规则 3 与收集器口径不一致的误报**；真正"未登记闭型"在 rt10 为 0。
- 修复后同类实测：同一插桩的复跑（§17.5）仍记录 139 个候选，分类变为
  unbound 85 / mentions-type-param 13 / registered 41 / unregistered 0，
  residual 0——即全部 139 条按类归零，没有"整体放行"。

### 17.4 口径对齐的实现要点

- `ClosedMetadataCollector.ContainsUnboundGenericConstruction`：
  `INamedTypeSymbol.IsUnboundGenericType`，数组递归到元素类型；与
  `MentionsTypeParameter` 一起构成规则 3 的 CM-DYN 豁免面（与规则 1/4 同一
  检测器族）。
- `ClosedMetadataResidualScanner.IsRegisteredClosedConstruction`：用
  `ClosedTypeKey.Create` + `analysis.ClosedTypes` 键集合；数组层单独查
  `ClosedTypeKey.Create(array)`（`VisitSiteType` 会登记 `List<short>[]` 本身）。
  任一闭层缺记录 → 仍报 finding，消息改为
  `... is not registered (its closure would silently flow into Java erasure)`。
- 未改 `Strict` 语义：residual finding 在 strict 下仍为 Error，非 strict 仍为
  Warning；未新增 skip/force 条目，未用 overrides 消音。
- 未改 `docs/preprocess-upgrade-glm5.3.md` 的 G-04a 行（该行"未绑定 typeof →
  登记"的历史措辞与收集器现状不符，本文按 §17.3 的实测口径记录，不回改计划文档）。

### 17.5 修复后统计与一个既有观察

- `closed-metadata`：388 registered / 1975 enumerated / 1587 deduplicated /
  3 register files / **0 consumer sites linked** / 443 dynamic sites skipped /
  **0 residuals**；与修复前逐项相同（本轮只改 gate 判定）。
- **既有观察（本轮未改）**：`consumer sites linked` 恒为 0，根因在
  `ClosedMetadataPassAdapter.ResolveHolderName` 的目录缓存——它对"本目录无
  csproj"写入 `null`，后续同目录文件命中该负缓存后直接返回 `null`，不再向上
  找祖先项目；实测 `BuildHolderByRecordKey` 只得到 **33/388** 条记录的 holder
  （33 条全在 `gen/*` 源码生成器项目），即 CM-LINK 对多数文件的 guard 插入被
  跳过。规则 3 本轮改用 analysis 记录键判定，不受该缺陷影响；该缺陷与
  §16.5 的 139 条无因果关系，留待后续独立修复。

### 17.6 新中止点（第 29 个 pass）

- `variance-audit`（`status=failed`，只读审计、不重写源码）报
  `VA030: strict variance gate: 144 unsafe site(s) [VA010=44, VA012=21,
  VA015=43, VA020=24, VA021=1, VA027=11]; no source was rewritten`。
- 该 pass 的 report `diagnostics` 为 145 条字符串（144 条站点 + 1 条 VA030 汇总），
  样例：`gen\Helpers\SourceGeneratorHelpers.cs(13,48): error VA020`（`TEnum`
  `struct` 约束）、`Common\ReflectionExtensions.cs(232,13): error VA015`、
  `Common\ReflectionExtensions.cs(332/339): error VA010`（`Type[]` 协变）。
- 与本轮改动无因果关系：§16 链路在 `closed-metadata` 即中止，从未到达该 pass；
  本轮只把门往下推进了一个 pass，未改 variance-audit 的任何代码/开关。

### 17.7 单测（先红后绿证据）

- `ClosedMetadataTests` 规则 3 用例调整为与新口径一致（48 → 51 条）：
  `ResidualRule3_UnboundGenericTypeOf_HasNoFinding`（`typeof(IList<>)` /
  `typeof(Dictionary<,>)` / `typeof(KeyValuePair<,>)` / `typeof(IList<>[])`）、
  `ResidualRule3_RegisteredClosedTypeOf_HasNoFinding`（`typeof(List<int>)` 与
  `typeof(List<List<short>>)`，前者由字段 CM-ENUM-1 登记）、
  `ResidualRule3_UnregisteredClosedTypeOf_IsFinding`（负例：关闭 CM-ENUM-2 让
  该闭型确实无记录 → 仍报 1 条）、
  `ResidualRule3_InsideSourceGenContext_IsExcluded`；
  `ResidualRule3_InsideGeneratedRegistrationFile_IsExcluded` 与
  `ResidualRule3_JsonSerializablePayload_IsExcluded` 既有豁免不变。
  同步更新 `ResidualRule1_NonGenericTypeOverload_IsFinding_AdjacentGuardClearsIt`
  的规则 3 断言（1 → 0）与 `ResidualOverrides_ForceType_...`（该用例显式关闭
  CM-ENUM-2 以保留"skip 抑制 + CLO001 Info"路径）。
- 红：仅回退两个源码文件（保留测试）运行
  `dotnet test --filter FullyQualifiedName~ClosedMetadataTests`
  → **3 失败 / 48 通过**（未绑定、已登记、规则 1 用例的规则 3 断言）；
  负例与源生成/特性豁免均保持通过。恢复源码后 **51/51 通过**。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4752 通过 / 9 跳过 / 0 失败**（1 m 50 s）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`。

### 17.8 TFM 警告

- 日志中 `skipped unresolvable TFM value` 计数为 **0**（与 §2–§16 一致）。
- `target framework '...' is below net10.0` 共 **8** 行，与 §16 相同，未新增。
- 日志计数：`warning ` 文本行 48 条、`[Warning]` 前缀行 69 条；` error ` 文本行
  146 条（其中 `error VAxxx` 站点行 140 条、`error VA030` 汇总 1 条，余为消息
  文本中的 "semantic error" 字样/换行续行）。variance-audit 的 report
  `diagnostics` 为 **145** 条（144 站点 + VA030 汇总），比日志可见站点行多 4 条
  （VA010 42/44、VA015 41/43），以 report 为准。

### 17.9 来源文件

- `D:\cs\rt10-baseline-w10l\preprocess-report.json`（schemaVersion 1，
  `success=false`，`totalSeconds=56.789`，28 个 pass）。
- `D:\cs\rt10-baseline-w10l.log`（995 行）。
- 中间树：`D:\cs\rt10-baseline-w10l\.cs2j-preprocessed-src`（3 个
  `_Cs2jClosedTypes.*.cs` 注册文件）。
- 分类实测：`D:\cs\rt10-baseline-w10l-prefix4.log`（临时插桩运行，139 条
  `CMDBG` 记录；同一构建关闭插桩后即本轮正式产物）。
- 对照：`D:\cs\rt10-baseline-w10k\preprocess-report.json`（§16，139 residuals）。


## 18. R4 单 pass 实测：`variance-use-site-normalize`（HEAD `953f1a9b` + 本轮 VR 实现）

### 18.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；基线 HEAD：`953f1a9b`，叠加本轮新增
  `variance-use-site-normalize` pass（运行时尚在工作树，尚未提交）。
- 代码前置：全量单测 **4766 通过 / 9 跳过 / 0 失败**（§17 基线 4752 + 本轮
  VR 新增 14 条）。
- 全链仍会在第 29 个 pass `variance-audit` 中止（见 §17.6；本轮未改该门），
  因此本节用**单 pass** 运行新 pass，源树为未经前置 pass 改写的 rt10 原始源码。
- 源：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  目标：`D:\cs\rt10-vr-w10m`；中间树
  `D:\cs\rt10-vr-w10m\.cs2j-variance-use-site-normalize-src`。
- 命令：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-vr-w10m --pass variance-use-site-normalize --no-verify-build --no-verify-tests
```

- 结果：`preprocess-report.json` 为 `success=true`、`totalSeconds=4.735`、
  1 个 pass；该 pass `status=ok`，summary 原样为
  `variance-use-site-normalize: 0 transformed, 352 clean, 0 rewritten [-], 60 preserved [VR-04=60], 6 unsupported [VR-05=1,VR-07=5], 0 rollback`。
- pass 内阶段耗时：`total=4.72s | compile=0.08s, rewrite=1.92s, prepare=2.68s, execute=2.04s`。

### 18.2 站点分布（`variance-use-site-normalize-report.json`）

| 项 | 实测 |
|----|------|
| `filesScanned` / `filesTransformed` / `filesSkippedClean` | 352 / **0** / 352 |
| `sitesTotal` | **66** |
| `sitesRewritten` | **0** |
| `sitesPreserved` | **60**（VR-04=60） |
| `sitesDeclaredUnsupported` | **6**（VR-05=1，VR-07=5） |
| `filesRolledBack` | **0** |
| VR-01 / VR-02 / VR-03 / VR-06 / VR-08 | 0 / 0 / 0 / 0 / 0 |

- VR-04 的 60 条全部为引用类型数组边（含 23 条同型/仅可空注解差异的边，
  如 `object[] -> object[]`、`object[] -> object?[]`）；按规则保留原样，未改写。
- 6 条 `DeclaredUnsupported` 全部带 `CS2J1404` 与
  `NORM-GENERIC-VARIANCE-USE`，逐条为：
  - `Serialization\Metadata\JsonPropertyInfoOfT.cs` 408/412/418/428/433：
    `VR-07`，`<method group> -> Func<object, T?, bool>`（泛型委托方法组，需先显式 lambda）；
  - `Schema\JsonSchemaExporter.cs:445:63`：`VR-05`，`string[] -> string[]`
    落在 `ref/out/in` 实参位（写回不可排除，安全条件 3/4）。
- 本语料原始树上，共享 C33 分析没有产出 VR-01/VR-02 的具名 producer/consumer
  转换边（66 条中 60 条数组 + 5 条方法组 + 1 条 ref 数组），因此本次实测改写数为
  0；VR-01/VR-02 的改写由单测覆盖（§18.3），未用不安全改写充数。
- 无整文件回滚（`filesRolledBack=0`），即所有文件均通过逐文件解析/语义后校验或
  无需改写。

### 18.3 单测（先红后绿证据）

- 新增 `tests/CSharpToJava.Tests/VarianceUseSiteNormalizeTests.cs`（14 条）：
  - VR-01 实参/返回值、VR-02 委托实参：改写为 `(T)(object)expr`，输出再编译
    错误数为 0；
  - Both 流向本地初始化（局部随后作为实参使用）：`VR-05` +
    `CS2J1404`，源码逐字节不变；
  - VR-04：引用数组站点保留且登记 `Preserved`；同型 `object[] -> object[]`
    边（无候选表达式）同样登记为 VR-04，不得误报为 unsupported；
  - VR-06/VR-03：`ClassifyPair` 直接用例（`IEnumerable<int>` →
    `IEnumerable<object>`、`IEnumerable<List<string>>` →
    `IEnumerable<List<object>>`）；
  - VR-08：`dynamic` 实参 → `DeclaredUnsupported` + `CS2J1404` +
    `NORM-GENERIC-VARIANCE-USE`；
  - 幂等（二次运行零改动）、干净文件字节保留、报告 JSON 形状；
  - 注册表单测：描述符（Expression、default-on、RunAfter
    member-desugar/linq-channel/equality-concat、rules=C33）、锁定槽位
    `equality-concat < variance-use-site-normalize < platform-isolation`、
    `--list-passes` 行、`PassChainFactory` 适配器与验证默认值。
- 红：实现修正前该测试类实测 **7 失败 / 6 通过**（站点收集为空与 cast 未落地）；
  修正 `ReplaceNodes` 改写与未改写路径的记录返回后 **14/14 通过**。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4766 通过 / 9 跳过 / 0 失败**（1 m 48 s）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`。

### 18.4 契约与开关

- `--list-passes` 实测新增行：
  `variance-use-site-normalize       Expression   Implemented   on       member-desugar,linq-channel,equality-concat  rules=C33`
  （位于 `equality-concat` 之后、`platform-isolation` 之前）。
- 默认链包含（`default` profile 与 `java-ready` 均按 default-on 进入计划）；
  Contract 声明 `Consumes/Postconditions/MayLeave = NORM-GENERIC-VARIANCE-USE`、
  `Policy=Fail`、`Idempotent=true`、`RequiresWholeTree=true`。
- 未新增 CLI 旋钮；未改 `docs/preprocess-upgrade-glm5.3.md`。

### 18.5 来源文件

- `D:\cs\rt10-vr-w10m\preprocess-report.json`（schemaVersion 1，
  `success=true`，`totalSeconds=4.735`，1 个 pass）。
- `D:\cs\rt10-vr-w10m\.cs2j-variance-use-site-normalize-src\variance-use-site-normalize-report.json`
  （66 站点：60 preserved + 6 unsupported）。
- 运行日志：本轮工作树根目录 `rt10-vr.log`（临时产物，未入库）。


## 19. R4 VR-03/05/07 扩展单 pass 实测（HEAD `576569c2` + 本轮实现）

### 19.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；基线 HEAD：`576569c2`，叠加本轮
  VR-03/05/07 实现（运行时尚在工作树，尚未提交）。
- 代码前置：全量单测 **4775 通过 / 9 跳过 / 0 失败**（§18 基线 4766 + 本轮
  新增 9 条；VR 测试类 23/23）。
- 全链仍在 `variance-audit` 门中止，故两处均用**单 pass** 运行。
- 源 1（rt10 原始源）：
  `D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`
  → 目标 `D:\cs\rt10-vr-w10n`。
- 源 2（上一次全链中间树，更接近链末真实形态）：
  `D:\cs\rt10-baseline-w10l\.cs2j-preprocessed-src` → 目标
  `D:\cs\rt10-vr-chain-w10n`。
- 命令：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-vr-w10n --pass variance-use-site-normalize --no-verify-build --no-verify-tests
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\cs\rt10-baseline-w10l\.cs2j-preprocessed-src `
  -d D:\cs\rt10-vr-chain-w10n --pass variance-use-site-normalize --no-verify-build --no-verify-tests
```

### 19.2 rt10 原始源

- `preprocess-report.json`：`success=true`、`totalSeconds=4.738`、1 个 pass。
- summary 原样：
  `variance-use-site-normalize: 0 transformed, 352 clean, 0 rewritten [-], 60 preserved [VR-04=60], 6 unsupported [VR-05=1,VR-07=5], 0 rollback`。
- 报告 totals：`filesScanned=352`、`filesTransformed=0`、`filesSkippedClean=352`、
  `filesRolledBack=0`、`sitesTotal=66`、`sitesRewritten=0`、`sitesPreserved=60`、
  `sitesDeclaredUnsupported=6`。
- 规则分布：VR-03=0、VR-05=1（DeclaredUnsupported）、VR-07=5
  （DeclaredUnsupported）；VR-04=60（Preserved）；VR-01/02/06/08=0。
- 6 条 `DeclaredUnsupported` 均带 `CS2J1404` + `NORM-GENERIC-VARIANCE-USE`：
  - `Serialization\Metadata\JsonPropertyInfoOfT.cs` 408/412/418/428/433：VR-07，
    `<method group> -> Func<object, T?, bool>`，原因为方法组签名含未绑定类型参数
    （`T?`），显式 lambda 无法绑定，按规则 `DeclaredUnsupported`（未做不安全改写）；
  - `Schema\JsonSchemaExporter.cs:445:63`：VR-05，`string[] -> string[]` 落在
    `ref/out/in` 实参位（安全条件 3/4）。
- 改写数 0、回滚 0。

### 19.3 全链中间树

- `preprocess-report.json`：`success=true`、`totalSeconds=4.161`、1 个 pass。
- summary 原样：
  `variance-use-site-normalize: 0 transformed, 366 clean, 0 rewritten [-], 86 preserved [VR-04=86], 1 unsupported [VR-05=1], 0 rollback`。
- 报告 totals：`filesScanned=366`、`filesTransformed=0`、`filesSkippedClean=366`、
  `filesRolledBack=0`、`sitesTotal=87`、`sitesRewritten=0`、`sitesPreserved=86`、
  `sitesDeclaredUnsupported=1`。
- 规则分布：VR-03=0、VR-05=1（`Schema\JsonSchemaExporter.cs:451:100`，
  `string[] -> string[]`，ref 实参位）、VR-04=86（Preserved）；VR-01/02/06/07/08=0。
- 该树中上一轮的 5 条 VR-07 方法组站点不再出现（前置 pass 已改变其形状）；
  改写数 0、回滚 0。

### 19.4 单测（先红后绿）

- `tests/CSharpToJava.Tests/VarianceUseSiteNormalizeTests.cs` 由 14 条扩到
  **23 条**，新增 9 条：
  - VR-03：嵌套协变改写（`IEnumerable<IEnumerable<Derived>>` →
    `IEnumerable<IEnumerable<Base>>`）、逆变委托内嵌套改写、嵌套 invariant 中间层
    （`IEnumerable<List<string>>` → `IEnumerable<List<object>>`）判定为
    `DeclaredUnsupported`/VR-03；
  - VR-05：Both 流向仅签名位（实参 + 返回值、只读、无 mutable 映射）改写；
    mutable 映射（`IReadOnlyList<T>`）负例保持 `DeclaredUnsupported`；
  - VR-07：方法组 → 显式 lambda → 方差 cast 改写（输出再编译错误数 0）；
    自定义 `Fn<in T,R>` 委托负例 `DeclaredUnsupported`；
  - VR-03/VR-07 输出幂等（二次运行零改动）、嵌套 `ClassifyPair` 分类。
- 红：临时回退三条规则的实现后实测 **7 失败 / 16 通过**（VR-03/05/07 正例与幂等
  用例失败）；恢复实现后 **23/23 通过**。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4775 通过 / 9 跳过 / 0 失败**（1 m 47 s）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`。

### 19.5 来源文件

- `D:\cs\rt10-vr-w10n\preprocess-report.json`（`success=true`，
  `totalSeconds=4.738`，1 个 pass）。
- `D:\cs\rt10-vr-w10n\.cs2j-variance-use-site-normalize-src\variance-use-site-normalize-report.json`
  （66 站点：60 preserved + 6 unsupported）。
- `D:\cs\rt10-vr-chain-w10n\preprocess-report.json`（`success=true`，
  `totalSeconds=4.161`，1 个 pass）。
- `D:\cs\rt10-vr-chain-w10n\.cs2j-variance-use-site-normalize-src\variance-use-site-normalize-report.json`
  （87 站点：86 preserved + 1 unsupported）。
- 运行日志：工作树根目录 `rt10-vr-w10n.log`、`rt10-vr-chain-w10n.log`
  （临时产物，未入库）。


## 20. R4 VR-04 数组协变口径纠正实测（HEAD `1d3cb3c3` + 本轮实现）

### 20.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；基线 HEAD：`1d3cb3c3`，叠加本轮
  数组协变口径纠正（运行时尚在工作树；代码+测试随后提交 `2de46933`）。
- 背景：rt10 全链此前止于 `variance-audit`，共 145 条 strict Error
  （VA010×44、VA015×43、VA012×21、VA020×24、VA021×1、VA027×11、VA030×1），
  其中 VA010/VA015 全部为数组转换。方案 §6.4-R4/VR-04 规定引用元素数组
  `Derived[] → Base[]` 保留原样（Java 数组协变），逐站点验证元素映射无装箱
  差异；审计此前把数组站点按 VA010/VA015 拦截，与 VR pass 的 VR-04 实现冲突。
- 代码前置：全量单测 **4780 通过 / 9 跳过 / 0 失败**（基线 4775 + 本轮 5 条）。
- 命令（`preprocess` 无 `--force`，新目录/日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10o --unified-preprocessed-src --profile java-ready `
  --no-verify-build --no-verify-tests *>&1 | Tee-Object D:\cs\rt10-baseline-w10o.log
```

### 20.2 variance-audit 结果

- 结论：**仍未通过**（strict gate），但 strict Error 从 145 条降为 **57 条**，
  `VA010/VA015 归零`；中止点仍为第 28 个 pass `variance-audit`（VA030）。
- summary 原样：
  `variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 34 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json`。
- 剩余 strict Error 分布：`VA012=21、VA020=24、VA021=1、VA027=11`，另有
  `VA030=1`（聚合门）。
- 不在本轮范围、保持原样：VA012（mapped mutability）、VA020/021/027（约束）。
- 87 个数组站点全部 `Direction=Array` 且 `ArrayElementMappingSafe=true`：
  totals 实测 `varianceConversionsArray=87`、`varianceConversionsArraySafe=87`、
  `varianceConversionsCovariant=0`、`varianceConversionsContravariant=0`；
  审计不再产生 VA010/VA015，strict 下同样不产生 Error；`unsafeUseSites=34`
  只剩 VA012/约束类站点。

### 20.3 87 个数组站点的分类结果

VR 单 pass 复跑（源 = 本轮全链中间树
`D:\cs\rt10-baseline-w10o\.cs2j-preprocessed-src`）实测 87 个数组站点：
`VR-04 preserved=86`、`VR-05 unsupported=1`。唯一例外是
`src\System\Text\Json\Schema\JsonSchemaExporter.cs:451:100` 的
`string[] -> string[]` 落在 `ref/out/in` 实参位，按安全条件 3/4 拒绝改写
（与元素映射无关）；审计侧仍判 `Array` 安全、不报 Error。

| 源数组类型 | 站点数 |
|------------|--------|
| `Type[]` | 20 |
| `object[]` | 15 |
| `EnumFieldInfo[]` | 7 |
| `string[]` | 6 |
| `ParameterInfo[]` | 5 |
| `Action<JsonTypeInfo>[]` | 4 |
| `object?[]` | 3 |
| `JsonParameterInfoValues[]` | 3 |
| `WeakReference<CachingContext>?[]` | 2 |
| `ConfigurationState[]` | 2 |
| `JsonPropertyInfo[]` | 2 |
| `SourceConstructFlags[]` | 2 |
| `ConsumeNumberResult[]` | 2 |
| `MethodInfo[]` | 2 |
| `ConsumeTokenResult[]` | 2 |
| `ConverterStrategy[]` | 2 |
| `EnclosingContainerType[]` | 2 |
| `JsonNode?[]` | 1 |
| `ConstructorInfo[]` | 1 |
| `JsonParameterInfo[]` | 1 |
| `WeakReference<CachingContext>[]` | 1 |
| `JsonConverterFactory[]` | 1 |
| `JsonNode[]` | 1 |
| **合计** | **87** |

- 全部站点满足：同秩、两侧元素均为引用类型、源元素可隐式引用转换到目标元素
  （子类型关系），即元素映射无装箱差异，符合 VR-04 保留条件。
- 值类型元素（如 `int[] -> object[]`）不在上述 87 站点内：C# 本身不允许该
  隐式数组协变，实测审计不产生此类 finding。共享判定实测
  `ClassifyArrayElementMapping(int[], object[]) = BoxingDifference`，VR 侧
  仍归 VR-06 `DeclaredUnsupported`（单测覆盖），未放行。

### 20.4 各 pass summary（28 个，variance-audit 中止）

```
1  conditional                ok      strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed
2  lexical-normalize          ok      lexical-normalize: 0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), 0 global using(s), 0 file-scoped normalized, 0 raw (0 multi), 0 u8 (0 bytes), 0 verbatim, 0 esc, 0 interpolated skipped (r 0/v 0/i 0)
3  partial-merge              ok      partial-merge: 10 group(s) merged, 0 partial member pair(s) collapsed
4  local-function-lift        ok      local-function-lift: 28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual
5  null-coalesce              ok      null-coalesce: 59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual
6  expression-desugar         ok      expression-desugar: 107 transformed, 209 clean, 36 dirty-no-rewrite; coll: 35 sites (array 9/mutable 15/iface 0/span 8/builder 0/unknown 0), spread 3 (addrange 3/span-foreach 0/enum-foreach 0), empty 16, nested 1, lifted 2, skipped 0, residual 0; guard: static-abstract 0 decl/0 call, generic-attr 0 decl/0 app, scoped 129 param/24 local; interp: 218 sites, concat 162, format 4 (align 0/fmt 3/iformattable 1), fold 25, formattable-kept 0, exprtree-skip 0, handler err0|keep27, temps 63, residual 0; init: 67 obj, 1 coll (single 1/multi 0/ext 0/params 0/fluent 0), 1 dict-index, 126 target-new, 0/0 nested, 38 ctx-stmts, 144 temps, 163 skipped (2 non-strict kept), 96 residual; nc: 180 member/call, 0 idx, 24 invoke (event 0/delegate 24), 56 void, 42 inline, 83 lifted, 32 coalesce, 0 deferred-c51, 164 temps, 0 skipped, 0 residual, assign= 0 member/0 idx, 0 compound/0 event, 0 coalesce, 0 value, 0 chain, 0 rhs-nc, 0 temps, 0 residual
7  enum                       ok      enum: 85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual
8  enum-declaration           ok      enum-declaration: 6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual
9  delegate                   ok      delegate: 1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual
10 tuple                      ok      tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated
11 anonymous-type-lift        ok      anonymous-type-lift: 0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual
12 foreach-deconstruction     ok      foreach-deconstruction: 0 transformed, 358 clean, 0 lowered, 1 skipped
13 foreach-source             ok      foreach-source: 9 transformed, 349 clean, 0 string site(s) wrapped, 0 tuple residual(s), 0 other/KV residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 ref index loop(s), 0 ref-readonly index loop(s), 0 ref-Current enumerator(s) [5485 0, 5486 0, residual ref 0], 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)
14 pattern-desugar            ok      pattern-desugar: 15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual
15 strip-default              ok      strip-default: 97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated
16 ref-out-in                 ok      ref-out-in: 208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members
17 unsafe-ref-lowering        ok      unsafe-ref-lowering: 0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)
18 using-resource-lift        ok      using-resource-lift: 0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)
19 fixed-span-to-array        ok      fixed-span-to-array: 1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)
20 compat-method-replacement  ok      compat-method-replacement: 2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)
21 pointer-diff-inline        ok      pointer-diff-inline: 0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)
22 unsigned                   ok      unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten
23 numeric-boundary           ok      numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 0 error(s); decimal-ops: 0 binary/0 comparison/0 unary/0 compound site(s), 0 promotion(s), 0 const downgraded, 0 temp(s), 291 deferred; increment-targets: 392 property/0 indexer/0 enum-array/1 simple site(s), 1 unsigned helper call(s), 387 postfix lifted/0 prefix inlined, 387 temp(s), 0 deferred; nullable-lift: 3 rel/134 eq/4 conv site(s), 11 temp(s)
24 generic                    ok      generic: 7 transformed, 354 clean (16 default(T) encountered, 16 rewritten, 0 nullability tests folded, 2 new T(), 1 new T[n])
25 thread-static              ok      thread-static: 1 transformed, 360 clean (1 fields rewritten, 0 skipped, 8 reads, 2 writes, 0 compound, 0 increments, 0 struct lifts, 0 initializers, 0 plan rollback(s), 0 residual)
26 generic-deepen             ok      generic-deepen: 158 transformed, 203 clean (254 anchors, 16 nested propagations, 4 patterns, 7 type tests, 0 catches, 28 static methods, 17 field accesses, 542 call sites, 0 expression-tree skips, 0 plans skipped, 0 ctor-token types, 0 ctor-token sites, 0 ctor-token call sites, 1 mtok methods, 1 mtok sites, 1 mtok call sites, 0 residuals)
27 closed-metadata            ok      closed-metadata: 388 registered (1975 enumerated, 1587 deduplicated, 3 register files, 0 consumer sites linked, 443 dynamic sites skipped, 0 residuals)
28 variance-audit             failed  variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 34 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json
```

### 20.5 TFM 警告

- 日志中 `skipped unresolvable TFM value` 计数为 **0**（与 §2–§19 一致）。
- warning 行共 38 条，为基线既有的审计/归因提示（CS2J2001、ED020、
  CS2J1107、CMR001、URL001 等），非本轮引入。
- 另有 4 条已知 `Cs2jUnsignedMath.csproj`（netstandard2.0）`[Warning]` 提示，
  与 §13 相同，不是 TFM 解析失败。

### 20.6 单测（先红后绿）

- `tests/CSharpToJava.Tests/VarianceAuditUnitTests.cs` 新增 4 条：
  - `Derived[] → Base[]` 在 `--variance-strict` 下无 Error、无 VA010/VA015，
    统计 `VarianceConversionsArray=1`、`VarianceConversionsArraySafe=1`；
  - `string[] → object[]`、`Type[] → object[]` 同上（2/2）；
  - `ClassifyArrayElementMapping(string[], object[]) = SafeReference`，
    `int[] → object[] = BoxingDifference`（仍不安全）；
  - 非数组 `IEnumerable<string> → IEnumerable<object>` 在 strict 下仍为
    Error 级 VA010（行为不回归）。
- `tests/CSharpToJava.Tests/VarianceUseSiteNormalizeTests.cs` 新增 1 条：
  `int[] → object[]` 判定 `BoxingDifference`，VR 侧仍为 VR-06
  `DeclaredUnsupported`。
- 红：实现前 3 条新用例实测 **2 失败 / 1 通过**（两条数组正例因 strict
  VA010/VA015 失败，非数组回归通过）；实现+补断言后相关 7 条全过。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4780 通过 / 9 跳过 / 0 失败**（1 m 49 s）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`；
  ProjectTests 仅按要求未运行。

### 20.7 来源文件

- `D:\cs\rt10-baseline-w10o\preprocess-report.json`（`success=false`，
  `totalSeconds=58.278`，28 个 pass；第 28 个 `variance-audit` 为 `failed`）。
- `D:\cs\rt10-baseline-w10o\.cs2j-preprocessed-src\variance-audit-report.json`
  （totals：87 个数组转换全部 safe）。
- `D:\cs\rt10-vr-w10o\preprocess-report.json`（VR 单 pass，`success=true`）。
- `D:\cs\rt10-vr-w10o\.cs2j-variance-use-site-normalize-src\variance-use-site-normalize-report.json`
  （87 站点：86 VR-04 preserved + 1 VR-05 unsupported）。
- 运行日志：`D:\cs\rt10-baseline-w10o.log`、`D:\cs\rt10-vr-w10o.log`
  （临时产物，未入库）。

## 21. R4 VA012 异步枚举接口误报修复实测（HEAD `540b6dfd` + 本轮，代码 `b40d9290`）

### 21.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；基线 HEAD：`540b6dfd`，叠加本轮
  `TypeMutabilityTable` 种子修复（运行时在工作树；代码+测试随后提交 `b40d9290`）。
- 背景：§20 复跑后剩 57 条 strict Error，其中 VA012×21 全部是
  `IAsyncEnumerable<T?>` 的 producer 站点，消息为
  `use site 'IAsyncEnumerable<T?>' is Producer-classified and its Java mapping` +
  `(unmapped metadata variant type) exposes mutating member(s) ...`。根因：
  `TypeMutabilityTable.Seeds()` 未收录 ``IAsyncEnumerable`1``，该元数据变体接口
  经 `IsConservativelyMutable` 落入保守 `(unmapped)` 分支；而
  `config/TypeMappings.json` 已把它映射到只读 compat 接口
  `io.github.ningpp.compat.AsyncEnumerable`（仅 `getAsyncEnumerator`）。
- 代码前置：全量单测 **4783 通过 / 9 跳过 / 0 失败**（基线 4780 + 本轮 3 条）。
- 命令（`preprocess` 无 `--force`，新目录/日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10q --unified-preprocessed-src --profile java-ready `
  --no-verify-build --no-verify-tests *>&1 | Tee-Object D:\cs\rt10-baseline-w10q.log
```

- 中间实测（`D:\cs\rt10-baseline-w10p`，仅补 ``IAsyncEnumerable`1`` 种子）：VA012
  由 21 降至 **1**，剩余一条为 `IAsyncEnumerator<TElement>`
  （`src\System\Text\Json\Serialization\Converters\Collection\IAsyncEnumerableOfTConverter.cs:139`）。
  该接口同为 covariant 元数据接口且同样未收录，故一并补入
  ``IAsyncEnumerator`1`` → `io.github.ningpp.compat.AsyncEnumerator`（Java 侧仅
  `moveNextAsync`/`getCurrent`，无 mutating 成员）。下列数据取自补全后的 `w10q` 复跑。

### 21.2 variance-audit 结果

- 结论：**仍未通过**（strict gate），strict Error 57 → **36 条**，`VA012 归零`；
  中止点仍为第 28 个 pass `variance-audit`（VA030）。
- summary 原样：
  `variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json`。
- 剩余 strict Error 分布：`VA020=24、VA021=1、VA027=11`，另有 `VA030=1`（聚合门，
  原文 `strict variance gate: 36 unsafe site(s) [VA020=24, VA021=1, VA027=11]`）。
- totals 实测（`variance-audit-report.json`）：`useSitesTotal=324`
  （producer/consumer/both = 39/18/267）、`metadataVariantTypesUsed=10`、
  `unsafeUseSites=13`（§20 为 34，正好减去 21 个 VA012）、
  `varianceConversionsArray=87`、`varianceConversionsArraySafe=87`、
  `varianceConversionsCovariant=0`、`varianceConversionsContravariant=0`、
  `nestedCompositionSites=105`、`constraintsStruct=24`、`constraintsUnmanaged=1`。
- 不在本轮范围、保持原样：VA020/021/027（约束类）。

### 21.3 各 pass summary（28 个，variance-audit 中止）

```
1  conditional                ok      strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed
2  lexical-normalize          ok      lexical-normalize: 0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), 0 global using(s), 0 file-scoped normalized, 0 raw (0 multi), 0 u8 (0 bytes), 0 verbatim, 0 esc, 0 interpolated skipped (r 0/v 0/i 0)
3  partial-merge              ok      partial-merge: 10 group(s) merged, 0 partial member pair(s) collapsed
4  local-function-lift        ok      local-function-lift: 28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual
5  null-coalesce              ok      null-coalesce: 59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual
6  expression-desugar         ok      expression-desugar: 107 transformed, 209 clean, 36 dirty-no-rewrite; coll: 35 sites (array 9/mutable 15/iface 0/span 8/builder 0/unknown 0), spread 3 (addrange 3/span-foreach 0/enum-foreach 0), empty 16, nested 1, lifted 2, skipped 0, residual 0; guard: static-abstract 0 decl/0 call, generic-attr 0 decl/0 app, scoped 129 param/24 local; interp: 218 sites, concat 162, format 4 (align 0/fmt 3/iformattable 1), fold 25, formattable-kept 0, exprtree-skip 0, handler err0|keep27, temps 63, residual 0; init: 67 obj, 1 coll (single 1/multi 0/ext 0/params 0/fluent 0), 1 dict-index, 126 target-new, 0/0 nested, 38 ctx-stmts, 144 temps, 163 skipped (2 non-strict kept), 96 residual; nc: 180 member/call, 0 idx, 24 invoke (event 0/delegate 24), 56 void, 42 inline, 83 lifted, 32 coalesce, 0 deferred-c51, 164 temps, 0 skipped, 0 residual, assign= 0 member/0 idx, 0 compound/0 event, 0 coalesce, 0 value, 0 chain, 0 rhs-nc, 0 temps, 0 residual
7  enum                       ok      enum: 85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual
8  enum-declaration           ok      enum-declaration: 6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual
9  delegate                   ok      delegate: 1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual
10 tuple                      ok      tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated
11 anonymous-type-lift        ok      anonymous-type-lift: 0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual
12 foreach-deconstruction     ok      foreach-deconstruction: 0 transformed, 358 clean, 0 lowered, 1 skipped
13 foreach-source             ok      foreach-source: 9 transformed, 349 clean, 0 string site(s) wrapped, 0 tuple residual(s), 0 other/KV residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 ref index loop(s), 0 ref-readonly index loop(s), 0 ref-Current enumerator(s) [5485 0, 5486 0, residual ref 0], 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)
14 pattern-desugar            ok      pattern-desugar: 15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual
15 strip-default              ok      strip-default: 97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated
16 ref-out-in                 ok      ref-out-in: 208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members
17 unsafe-ref-lowering        ok      unsafe-ref-lowering: 0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)
18 using-resource-lift        ok      using-resource-lift: 0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)
19 fixed-span-to-array        ok      fixed-span-to-array: 1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)
20 compat-method-replacement  ok      compat-method-replacement: 2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)
21 pointer-diff-inline        ok      pointer-diff-inline: 0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)
22 unsigned                   ok      unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten
23 numeric-boundary           ok      numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 0 error(s); decimal-ops: 0 binary/0 comparison/0 unary/0 compound site(s), 0 promotion(s), 0 const downgraded, 0 temp(s), 291 deferred; increment-targets: 392 property/0 indexer/0 enum-array/1 simple site(s), 1 unsigned helper call(s), 387 postfix lifted/0 prefix inlined, 387 temp(s), 0 deferred; nullable-lift: 3 rel/134 eq/4 conv site(s), 11 temp(s)
24 generic                    ok      generic: 7 transformed, 354 clean (16 default(T) encountered, 16 rewritten, 0 nullability tests folded, 2 new T(), 1 new T[n])
25 thread-static              ok      thread-static: 1 transformed, 360 clean (1 fields rewritten, 0 skipped, 8 reads, 2 writes, 0 compound, 0 increments, 0 struct lifts, 0 initializers, 0 plan rollback(s), 0 residual)
26 generic-deepen             ok      generic-deepen: 158 transformed, 203 clean (254 anchors, 16 nested propagations, 4 patterns, 7 type tests, 0 catches, 28 static methods, 17 field accesses, 542 call sites, 0 expression-tree skips, 0 plans skipped, 0 ctor-token types, 0 ctor-token sites, 0 ctor-token call sites, 1 mtok methods, 1 mtok sites, 1 mtok call sites, 0 residuals)
27 closed-metadata            ok      closed-metadata: 388 registered (1975 enumerated, 1587 deduplicated, 3 register files, 0 consumer sites linked, 443 dynamic sites skipped, 0 residuals)
28 variance-audit             failed  variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json
```

### 21.4 TFM 警告

- 日志中 `skipped unresolvable TFM value` 计数为 **0**（与 §2–§20 一致）。
- warning 行共 48 条（含折行续行），归类仍为基线既有提示：
  ED020/CS2J1107/CS2J2001/CMR001/URL001，本轮未引入新类目。

### 21.5 单测（先红后绿）

- `tests/CSharpToJava.Tests/VarianceAuditUnitTests.cs` 新增 3 条：
  - `IAsyncEnumerable<Derived>` producer 站点在 strict 下无 VA012、无 Error；
  - `IAsyncEnumerator<Derived>` producer 站点同上（对应 21.1 的中间残留）；
  - 负例：未收录的元数据变体接口 `IQueryable<Derived>` producer 站点仍报 1 条
    VA012，消息含 `(unmapped metadata variant type)`/`(unmapped)`，保守行为不回归。
- 红：种子修复前实测 `Mapped_async_enumerable_producer_has_no_va012` 失败
  （`Expected: 0 / Actual: 1`），负例通过；补种子后相关 2 条转绿。
  `IAsyncEnumerator` 一条由 rt10 中间复跑（`w10p`，VA012=1）先行证实，补种子后转绿。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4783 通过 / 9 跳过 / 0 失败**（1 m 49 s）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`；
  ProjectTests 未运行。

### 21.6 来源文件

- `D:\cs\rt10-baseline-w10q\preprocess-report.json`（`success=false`，
  `totalSeconds=56.703`，28 个 pass；第 28 个 `variance-audit` 为 `failed`）。
- `D:\cs\rt10-baseline-w10q\.cs2j-preprocessed-src\variance-audit-report.json`
  （totals：`unsafeUseSites=13`，VA012=0）。
- `D:\cs\rt10-baseline-w10p\preprocess-report.json`（中间态：仅补
  ``IAsyncEnumerable`1`` 种子，VA012=1）。
- 运行日志：`D:\cs\rt10-baseline-w10q.log`、`D:\cs\rt10-baseline-w10p.log`
  （临时产物，未入库）。

## 22. R9/CN-01/GD-MTOK-2 值类型 default 零值构造实测（HEAD `b54f6329` + 本轮 `c8602b6e`）

### 22.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；基线 HEAD：`b54f6329`，代码+测试提交
  `c8602b6e`（本轮工作树）。
- 背景：§21 复跑后 strict Error 36 条，其中 `VA027=11` 全部是值类型约束类型参数上的
  `default`（5 处 `EnumConverter.cs`、1 处 `NullableConverter.cs`、5 处
  `FSharpValueOptionConverter.cs`），Java 侧 `GetDefaultValueForType` 对擦除类型参数
  一律渲染 `null` 并报 CS2J1403。
- 本轮口径：在 `generic` pass 中把 `where T : struct/unmanaged/Enum`（含
  `where T : U` 传递闭包）上的显式 `default(T)` 与 target-typed `default` 改写为
  `((T)global::System.Activator.CreateInstance(typeof(T)))`；随后由 generic-deepen
  的 `typeof(T)` 锚点（`__cs2j_token_T`）提供运行时类型，Activator compat 已实现
  值类型零值语义。
- 安全排除保持原节点（不新增静默）：表达式树、attribute/parameter 默认值/enum
  初始化器/const/case/goto case/常量模式、`nameof`、`ref`/`out`/`in` 实参；
  这些站点若仍依赖值语义，继续走既有 CS2J1403。
- 代码前置：全量单测 **4791 通过 / 9 跳过 / 0 失败**（基线 4783 + 本轮 8 条）。
- 命令（`preprocess` 无 `--force`，新目录/日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10r --unified-preprocessed-src --profile java-ready `
  --no-verify-build --no-verify-tests *>&1 | Tee-Object D:\cs\rt10-baseline-w10r.log
```

### 22.2 generic pass 实测

- summary 原样：
  `generic: 7 transformed, 354 clean (16 default(T) encountered, 2 rewritten, 0 nullability tests folded, 11 value defaults rewritten, 2 new T(), 1 new T[n])`。
- `ValueDefaultsRewritten=11`，与 §21 的 11 条 VA027 站点一一对应，全部是
  target-typed `default`：EnumConverter 5（`:105`/`:256`/`:313`/`:395`/`:445`）、
  NullableConverter 1（`:39`）、FSharpValueOptionConverter 5（`:39`/`:55`/`:61`/`:90`/`:119`）。
- 预处理树实测形态（`D:\cs\rt10-baseline-w10r\.cs2j-preprocessed-src`）：

```
EnumConverter.cs:105      T result = ((T)global::System.Activator.CreateInstance(typeof(T)));
EnumConverter.cs:256      return ((T)global::System.Activator.CreateInstance(typeof(T)));
NullableConverter.cs:39   T element = ((T)global::System.Activator.CreateInstance(typeof(T)));
FSharpValueOptionConverter.cs:61  if (value.Equals(((TValueOption)global::System.Activator.CreateInstance(typeof(TValueOption)))))
```

- generic-deepen 在相应实例方法内注入 `__cs2j_token_T`/`__cs2j_token_TValueOption`
  锚点（anchors 254 → **265**，+11），Java 侧 `TypeParameterToken` 将这些
  `typeof(T)` 渲染为锚点变量；`mtok` 相关计数维持 1/1/1，residual 仍 0。
- 16 处显式 `default(T)` 中仅 2 处按原逻辑改写为 `default` 字面量；其余 14 处
  均为 `default(T) is null` / `is not null` 判定（V-08 与既有跳过规则保护，
  约束无法静态判定时保持原样），不属值语义 `default` 用法。最终树代码内残留的
  14 处显式站点全部是这类 null 判定，值语义站点为 0。

### 22.3 variance-audit 结果

- 结论：**仍未通过**（strict gate），strict Error 36 → **25 条**，`VA027 归零`；
  中止点仍为第 28 个 pass `variance-audit`（VA030）。
- summary 原样：
  `variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json`。
- 剩余 strict Error 分布：`VA020=24、VA021=1`，另有 `VA030=1`（聚合门，
  原文 `strict variance gate: 25 unsafe site(s) [VA020=24, VA021=1]`）。
- totals 实测（`variance-audit-report.json`）：`useSitesTotal=324`
  （39/18/267）、`metadataVariantTypesUsed=10`、`unsafeUseSites=13`、
  `varianceConversionsArray=87`（safe=87）、`nestedCompositionSites=105`、
  `constraintsStruct=24`、`constraintsUnmanaged=1`、`constraintDependentDefaultSites=0`
  （§21 为 11）、`constraintDependentNewSites=2`、`constraintDependentBoxingSites=12`、
  `constraintDependentArraySites=1`、`constraintDependentNullableSites=9`。
- 任务 B 判定：24 条 VA020 中 **18 条全部依赖计数为 0**（占多数），但仍有 6 条
  带依赖（boxing 8/4、array 1、nullable 1/1/7）加 VA021 1 条；即使把零依赖站点
  降级，variance-audit 仍会因这 7 条 strict Error 中止，未满足"消除后可继续"的
  执行条件，故本轮不实施 B，保持 Error，留待后续口径与实现一起处理。

### 22.4 各 pass summary（28 个，variance-audit 中止）

```
1  conditional                ok      strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed
2  lexical-normalize          ok      lexical-normalize: 0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), 0 global using(s), 0 file-scoped normalized, 0 raw (0 multi), 0 u8 (0 bytes), 0 verbatim, 0 esc, 0 interpolated skipped (r 0/v 0/i 0)
3  partial-merge              ok      partial-merge: 10 group(s) merged, 0 partial member pair(s) collapsed
4  local-function-lift        ok      local-function-lift: 28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual
5  null-coalesce              ok      null-coalesce: 59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual
6  expression-desugar         ok      expression-desugar: 107 transformed, 209 clean, 36 dirty-no-rewrite; coll: 35 sites (array 9/mutable 15/iface 0/span 8/builder 0/unknown 0), spread 3 (addrange 3/span-foreach 0/enum-foreach 0), empty 16, nested 1, lifted 2, skipped 0, residual 0; guard: static-abstract 0 decl/0 call, generic-attr 0 decl/0 app, scoped 129 param/24 local; interp: 218 sites, concat 162, format 4 (align 0/fmt 3/iformattable 1), fold 25, formattable-kept 0, exprtree-skip 0, handler err0|keep27, temps 63, residual 0; init: 67 obj, 1 coll (single 1/multi 0/ext 0/params 0/fluent 0), 1 dict-index, 126 target-new, 0/0 nested, 38 ctx-stmts, 144 temps, 163 skipped (2 non-strict kept), 96 residual; nc: 180 member/call, 0 idx, 24 invoke (event 0/delegate 24), 56 void, 42 inline, 83 lifted, 32 coalesce, 0 deferred-c51, 164 temps, 0 skipped, 0 residual, assign= 0 member/0 idx, 0 compound/0 event, 0 coalesce, 0 value, 0 chain, 0 rhs-nc, 0 temps, 0 residual
7  enum                       ok      enum: 85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual
8  enum-declaration           ok      enum-declaration: 6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual
9  delegate                   ok      delegate: 1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual
10 tuple                      ok      tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated
11 anonymous-type-lift        ok      anonymous-type-lift: 0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual
12 foreach-deconstruction     ok      foreach-deconstruction: 0 transformed, 358 clean, 0 lowered, 1 skipped
13 foreach-source             ok      foreach-source: 9 transformed, 349 clean, 0 string site(s) wrapped, 0 tuple residual(s), 0 other/KV residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 ref index loop(s), 0 ref-readonly index loop(s), 0 ref-Current enumerator(s) [5485 0, 5486 0, residual ref 0], 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)
14 pattern-desugar            ok      pattern-desugar: 15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual
15 strip-default              ok      strip-default: 97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated
16 ref-out-in                 ok      ref-out-in: 208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members
17 unsafe-ref-lowering        ok      unsafe-ref-lowering: 0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)
18 using-resource-lift        ok      using-resource-lift: 0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)
19 fixed-span-to-array        ok      fixed-span-to-array: 1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)
20 compat-method-replacement  ok      compat-method-replacement: 2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)
21 pointer-diff-inline        ok      pointer-diff-inline: 0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)
22 unsigned                   ok      unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten
23 numeric-boundary           ok      numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 0 error(s); decimal-ops: 0 binary/0 comparison/0 unary/0 compound site(s), 0 promotion(s), 0 const downgraded, 0 temp(s), 291 deferred; increment-targets: 392 property/0 indexer/0 enum-array/1 simple site(s), 1 unsigned helper call(s), 387 postfix lifted/0 prefix inlined, 387 temp(s), 0 deferred; nullable-lift: 3 rel/134 eq/4 conv site(s), 11 temp(s)
24 generic                    ok      generic: 7 transformed, 354 clean (16 default(T) encountered, 2 rewritten, 0 nullability tests folded, 11 value defaults rewritten, 2 new T(), 1 new T[n])
25 thread-static              ok      thread-static: 1 transformed, 360 clean (1 fields rewritten, 0 skipped, 8 reads, 2 writes, 0 compound, 0 increments, 0 struct lifts, 0 initializers, 0 plan rollback(s), 0 residual)
26 generic-deepen             ok      generic-deepen: 158 transformed, 203 clean (265 anchors, 16 nested propagations, 4 patterns, 7 type tests, 0 catches, 28 static methods, 17 field accesses, 542 call sites, 0 expression-tree skips, 0 plans skipped, 0 ctor-token types, 0 ctor-token sites, 0 ctor-token call sites, 1 mtok methods, 1 mtok sites, 1 mtok call sites, 0 residuals)
27 closed-metadata            ok      closed-metadata: 388 registered (1975 enumerated, 1587 deduplicated, 3 register files, 0 consumer sites linked, 443 dynamic sites skipped, 0 residuals)
28 variance-audit             failed  variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json
```

### 22.5 TFM 警告

- 日志中 `skipped unresolvable TFM value` 计数为 **0**（与 §2–§21 一致）。
- warning 行共 48 条（含折行续行），与 §21 相同，仍为 ED020/CS2J1107/CS2J2001/
  CMR001/URL001 等基线既有提示；本轮未引入新类目。

### 22.6 单测（先红后绿）

- `tests/CSharpToJava.Tests/GenericPreprocessTests.cs` 新增 8 条：
  - 显式 `default(T)`（赋值/返回/`Equals` 实参）改写为 Activator 形式；
  - target-typed `default`（局部/返回）改写；
  - `Enum` 与 `unmanaged` 约束改写；
  - `where T : U where U : struct` 传递闭包改写（无语义模型路径）；
  - 负例：`where T : class` / 无约束的 target-typed `default` 不改写；
  - 表达式树内不改写；
  - 参数默认值上下文不改写。
- 红：临时回退 `GenericPreprocessor.cs` 后实测 8 条中 **6 条失败**（全部正例及
  表达式树/参数默认值保护），恢复后 8 条通过。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4791 通过 / 9 跳过 / 0 失败**（1 m 50 s）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`；
  ProjectTests 未运行。

### 22.7 来源文件

- `D:\cs\rt10-baseline-w10r\preprocess-report.json`（`success=false`，
  `totalSeconds=62.587`，28 个 pass；第 28 个 `variance-audit` 为 `failed`）。
- `D:\cs\rt10-baseline-w10r\.cs2j-preprocessed-src\variance-audit-report.json`
  （totals：`unsafeUseSites=13`、`constraintDependentDefaultSites=0`、VA027=0）。
- 运行日志：`D:\cs\rt10-baseline-w10r.log`（临时产物，未入库）。

## 23. R9：约束审计语义分类（w10s）

### 23.1 判据（符号级，非文本猜测）

- **惰性（lazy）判据**：`struct`/`unmanaged` 约束只有在存在"值语义依赖站点"时才阻断
  strict。约束只限制调用方，而所有调用方都来自已编译的 C#；当没有任何依赖站点仍依赖
  值语义时，Java 擦除不改变可观察行为，故 `VA020`/`VA021` 降为 `Info`（仍保留站点、
  计数、`judgment` 与 `rationale`）。
- **枚举 boxing/array**：按符号判定约束是否含 `System.Enum`
  （`ITypeParameterSymbol.ConstraintTypes` 中 `SpecialType == System_Enum`），不看名字或
  文本。命中时 boxing-site 与 array-site 不算不安全：C# 枚举是不可变值类型，装箱副本与
  引用观察不可区分，Java 枚举数组元素不可变。`default(T)`/`new T()`/`T?` 值操作仍按原
  规则处理；不带 `System.Enum` 的普通 `struct` 约束保留原判定（boxing/array 仍不安全）。
- **`T?` 精确判定**：仅类型使用（参数/返回/字段/局部/基类型，以及 `T?` 传参/返回）判安全
  ——Java 引用擦除下 null ⇔ 无值、非 null ⇔ 有值，struct 实例本身非 null。若对 `T?` 做
  `.HasValue`/`.Value`/`GetValueOrDefault`/`??` 等值语义操作，则检查当前中间树是否已被
  nullable-lift/C22 改写成等价 null 检查：仍是 `Nullable<T>` 值操作即未改写，保持 Error
  并在报告/诊断中给出文件与行。
- **`unmanaged`**：只有 pointer/`sizeof`/`Marshal`/`MemoryMarshal`/`Unsafe` 站点算
  unmanaged 布局依赖；零依赖时与惰性同判 `Info`。
- 报告新增 `constraintSites[].includesEnum/judgment/rationale/safeDependentSites/
  unsafeDependentSites/dependentNullableValueSites` 与顶层 `constraintDependentSites`
  （逐站点 `safe` + `rationale`）。

### 23.2 25 条逐类判定

`struct` 24 条 + `unmanaged` 1 条，实测 **24 条 lazy / 1 条 unsafe**，其中含
`System.Enum` 的 15 条。

| # | 站点（预处理树相对路径:行） | 约束 | 依赖计数 D/N/B/A/Nul/NulVal | 判定 |
|---|---|---|---|---|
| 1 | `Common/JsonHelpers.cs:21` | unmanaged TKey | 0/0/0/0/0/0（无 pointer/sizeof/marshal/Unsafe） | lazy |
| 2 | `_Cs2jGenericHolders.System.Text.Json.1.cs:12` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 3 | `_Cs2jGenericHolders.System.Text.Json.1.cs:29` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 4 | `gen/Helpers/SourceGeneratorHelpers.cs:13` | struct TEnum, Enum | 0/0/0/0/0/0 | lazy |
| 5 | `FSharp/FSharpValueOptionConverter.cs:11` | struct TValueOption | 0/0/0/0/0/0 | lazy |
| 6 | `Value/EnumConverter.cs:17` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 7 | `Value/EnumConverter.cs:57` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 8 | `Value/EnumConverter.cs:457` | struct T, Enum | 0/0/**8**/0/0/0 | lazy（枚举装箱豁免） |
| 9 | `Value/EnumConverter.cs:473` | struct T, Enum | 0/0/**4**/0/0/0 | lazy（枚举装箱豁免） |
| 10 | `Value/EnumConverter.cs:486` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 11 | `Value/EnumConverter.cs:621` | struct T, Enum | 0/0/0/**1**/0/0 | lazy（枚举数组豁免） |
| 12 | `Value/EnumConverter.cs:672` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 13 | `Value/EnumConverterFactory.cs:50` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 14 | `Value/EnumConverterFactory.cs:64` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 15 | `Value/NullableConverter.cs:8` | struct T | 0/0/0/0/**7**/**3** | **unsafe**（`T?` 类型使用 7 条安全；3 条 `value.Value` 未 lift） |
| 16 | `IReadBufferState.cs:10` | struct TReadBufferState | 0/0/0/0/0/0 | lazy |
| 17 | `JsonNumberEnumConverter.cs:16` | struct TEnum, Enum | 0/0/0/0/0/0 | lazy |
| 18 | `JsonStringEnumConverter.cs:15` | struct TEnum, Enum | 0/0/0/0/0/0 | lazy |
| 19 | `Metadata/FSharpCoreReflectionProxy.cs:34` | struct TStruct | 0/0/0/0/0/0 | lazy |
| 20 | `Metadata/FSharpCoreReflectionProxy.cs:170` | struct TFSharpValueOption | 0/0/0/0/0/0 | lazy |
| 21 | `Metadata/JsonMetadataServices.Converters.cs:659` | struct T, Enum | 0/0/0/0/0/0 | lazy |
| 22 | `Metadata/JsonMetadataServices.Converters.cs:675` | struct T | 0/0/0/0/**1**/0 | lazy（`JsonConverter<T?>` 仅类型使用） |
| 23 | `Metadata/JsonMetadataServices.Converters.cs:692` | struct T | 0/0/0/0/**1**/0 | lazy（同上） |
| 24 | `Metadata/JsonTypeInfoOfT.ReadHelper.cs:29` | struct TReadBufferState | 0/0/0/0/0/0 | lazy |
| 25 | `Metadata/JsonTypeInfoOfT.ReadHelper.cs:158` | struct TReadBufferState | 0/0/0/0/0/0 | lazy |

第 15 条保留 Error 的实证：中间树 `NullableConverter.cs:64/88/111` 仍是
`_elementConverter.*(writer, value.Value, ...)`（`value` 为 `T?`），nullable-lift/C22
只改写比较/相等/转换站点（本轮 `3 rel/134 eq/4 conv`），未把这些 `.Value` 改写为等价
null 检查，故按判据不放行。`value is null` 是安全 null 判定，不计入值语义操作。

### 23.3 rt10 结果

- 命令（`preprocess` 无 `--force`，新目录/日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10s --unified-preprocessed-src --profile java-ready `
  --no-verify-build --no-verify-tests *>&1 | Tee-Object D:\cs\rt10-baseline-w10s.log
```

- 结论：**variance-audit 仍未通过**（strict gate）。strict Error 25 → **4 条 VA020**，
  另加 1 条 VA030（聚合门）；不再有 VA021 Error。中止点仍是第 28 个 pass。
- 剩余 4 条 VA020 = 3 条依赖站点（`NullableConverter.cs:64/88/111`，`.Value` 未 lift）
  + 1 条约束站点（`NullableConverter.cs:8`）。VA030 原文：
  `strict variance gate: 4 unsafe site(s) [VA020=4]; no source was rewritten ...`。
- VA021：`Common/JsonHelpers.cs:21` 判 lazy（无 pointer/sizeof/marshal/Unsafe），Info。
- 因未通过，本轮没有后续 pass 或新的中止点；后续 29+ pass 未执行。
- variance-audit summary 原样：
  `variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json`。
- totals 实测：`constraintsStruct=24`、`constraintsUnmanaged=1`、
  `constraintDependentDefaultSites=0`、`constraintDependentNewSites=2`、
  `constraintDependentBoxingSites=12`、`constraintDependentArraySites=1`、
  `constraintDependentNullableSites=9`、`constraintDependentNullableValueSites=3`、
  `constraintLazySites=24`、`constraintUnsafeSites=1`、`unsafeUseSites=13`、
  `varianceConversionsArray=87`（safe=87）。
- `constraintDependentSites` 共 12 条：9 条 `safe=true`（7 条 `NullableConverter` 的
  `T?` 类型使用 + 2 条 `GetNullableConverter`），3 条 `safe=false`（上述 `.Value` 站点）。
- TFM 警告：日志中 `skipped unresolvable TFM value` 计数 **0**。

### 23.4 各 pass summary（28 个，variance-audit 中止）

```
1  conditional                ok      strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed
2  lexical-normalize          ok      lexical-normalize: 0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), 0 global using(s), 0 file-scoped normalized, 0 raw (0 multi), 0 u8 (0 bytes), 0 verbatim, 0 esc, 0 interpolated skipped (r 0/v 0/i 0)
3  partial-merge              ok      partial-merge: 10 group(s) merged, 0 partial member pair(s) collapsed
4  local-function-lift        ok      local-function-lift: 28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual
5  null-coalesce              ok      null-coalesce: 59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual
6  expression-desugar         ok      expression-desugar: 107 transformed, 209 clean, 36 dirty-no-rewrite; coll: 35 sites (array 9/mutable 15/iface 0/span 8/builder 0/unknown 0), spread 3 (addrange 3/span-foreach 0/enum-foreach 0), empty 16, nested 1, lifted 2, skipped 0, residual 0; guard: static-abstract 0 decl/0 call, generic-attr 0 decl/0 app, scoped 129 param/24 local; interp: 218 sites, concat 162, format 4 (align 0/fmt 3/iformattable 1), fold 25, formattable-kept 0, exprtree-skip 0, handler err0|keep27, temps 63, residual 0; init: 67 obj, 1 coll (single 1/multi 0/ext 0/params 0/fluent 0), 1 dict-index, 126 target-new, 0/0 nested, 38 ctx-stmts, 144 temps, 163 skipped (2 non-strict kept), 96 residual; nc: 180 member/call, 0 idx, 24 invoke (event 0/delegate 24), 56 void, 42 inline, 83 lifted, 32 coalesce, 0 deferred-c51, 164 temps, 0 skipped, 0 residual, assign= 0 member/0 idx, 0 compound/0 event, 0 coalesce, 0 value, 0 chain, 0 rhs-nc, 0 temps, 0 residual
7  enum                       ok      enum: 85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual
8  enum-declaration           ok      enum-declaration: 6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual
9  delegate                   ok      delegate: 1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual
10 tuple                      ok      tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated
11 anonymous-type-lift        ok      anonymous-type-lift: 0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual
12 foreach-deconstruction     ok      foreach-deconstruction: 0 transformed, 358 clean, 0 lowered, 1 skipped
13 foreach-source             ok      foreach-source: 9 transformed, 349 clean, 0 string site(s) wrapped, 0 tuple residual(s), 0 other/KV residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 ref index loop(s), 0 ref-readonly index loop(s), 0 ref-Current enumerator(s) [5485 0, 5486 0, residual ref 0], 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)
14 pattern-desugar            ok      pattern-desugar: 15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual
15 strip-default              ok      strip-default: 97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated
16 ref-out-in                 ok      ref-out-in: 208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members
17 unsafe-ref-lowering        ok      unsafe-ref-lowering: 0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)
18 using-resource-lift        ok      using-resource-lift: 0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)
19 fixed-span-to-array        ok      fixed-span-to-array: 1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)
20 compat-method-replacement  ok      compat-method-replacement: 2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)
21 pointer-diff-inline        ok      pointer-diff-inline: 0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)
22 unsigned                   ok      unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten
23 numeric-boundary           ok      numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 0 error(s); decimal-ops: 0 binary/0 comparison/0 unary/0 compound site(s), 0 promotion(s), 0 const downgraded, 0 temp(s), 291 deferred; increment-targets: 392 property/0 indexer/0 enum-array/1 simple site(s), 1 unsigned helper call(s), 387 postfix lifted/0 prefix inlined, 387 temp(s), 0 deferred; nullable-lift: 3 rel/134 eq/4 conv site(s), 11 temp(s)
24 generic                    ok      generic: 7 transformed, 354 clean (16 default(T) encountered, 2 rewritten, 0 nullability tests folded, 11 value defaults rewritten, 2 new T(), 1 new T[n])
25 thread-static              ok      thread-static: 1 transformed, 360 clean (1 fields rewritten, 0 skipped, 8 reads, 2 writes, 0 compound, 0 increments, 0 struct lifts, 0 initializers, 0 plan rollback(s), 0 residual)
26 generic-deepen             ok      generic-deepen: 158 transformed, 203 clean (265 anchors, 16 nested propagations, 4 patterns, 7 type tests, 0 catches, 28 static methods, 17 field accesses, 542 call sites, 0 expression-tree skips, 0 plans skipped, 0 ctor-token types, 0 ctor-token sites, 0 ctor-token call sites, 1 mtok methods, 1 mtok sites, 1 mtok call sites, 0 residuals)
27 closed-metadata            ok      closed-metadata: 388 registered (1975 enumerated, 1587 deduplicated, 3 register files, 0 consumer sites linked, 443 dynamic sites skipped, 0 residuals)
28 variance-audit             failed  variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json
```

### 23.5 单测（先红后绿）

- `tests/CSharpToJava.Tests/VarianceAuditUnitTests.cs` 新增 8 条：
  - 零依赖 `struct` → `lazy`/`Info`，无 strict Error；
  - `struct, System.Enum` 的 boxing/array → 豁免，无 strict Error；
  - 负例：不带 `System.Enum` 的 `struct` boxing → 仍 unsafe；
  - `T?` 仅类型使用 → 安全、无 Error；
  - 负例：`value.HasValue` 未 lift → VA020 Error，报告逐站点 `rationale`；
  - 负例：`??` 被识别为值操作；
  - `unmanaged` 无依赖 → `lazy`/`Info`；
  - 负例：`sizeof(T)` → VA021 Error。
- 红：临时把 `lazy` 分支回退为 `false`，4 条正例（零依赖 struct、枚举
  boxing/array、`T?` 仅类型使用、unmanaged 无依赖）失败；恢复后全部通过。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4799 通过 / 9 跳过 / 0 失败**（基线 4791 + 本轮 8 条）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`。

### 23.6 来源文件

- `D:\cs\rt10-baseline-w10s\preprocess-report.json`（`success=false`，
  `totalSeconds=57.423`，28 个 pass；第 28 个 `variance-audit` 为 `failed`）。
- `D:\cs\rt10-baseline-w10s\.cs2j-preprocessed-src\variance-audit-report.json`
  （`constraintLazySites=24`、`constraintUnsafeSites=1`、
  `constraintDependentNullableValueSites=3`、`constraintDependentSites=12`）。
- 运行日志：`D:\cs\rt10-baseline-w10s.log`（临时产物，未入库）。

## 24. R9 补丁：`T?` 守卫判定放行（w10t）

基线 HEAD `d04070cc`，工作树仅含本轮 `variance-audit` 守卫判定修改。§23 把
`NullableConverter.cs:64/88/111` 三处 `.Value` 一律判 Error；本轮改为"有支配性
守卫且 Java 映射等价"即判安全，**无守卫的 `.Value` 仍为 Error**。

### 24.1 判据（语法 + 符号级）

- `T?` 仅类型使用：安全（§23 规则不变）。
- `.HasValue`：安全——`IdentifierExpressionTransformer.cs:987-989` 映射为
  `(receiver != null)`，null/无值等价。
- `.Value`：仅当存在**同一 receiver 符号**的支配性非空守卫时安全。守卫形态：
  - 早退卫语句之后（`if (value is null) { ...; return/throw; }`，其后代码）；
  - `if (x.HasValue) { ... }` / `if (x != null) { ... }` / `if (x is not null) { ... }`
    的真分支；
  - `if (!x.HasValue) ... else { ... }` / `if (x is null) ... else { ... }` 的 else 分支；
  - 条件表达式对应 arm（`x.HasValue ? x.Value : ...` 的真 arm，及取反后的假 arm）。
  - 守卫与被守卫 receiver 用 `SymbolEqualityComparer.Default` 判符号相等；仅"文件任意
    位置出现同名字符串 HasValue"不算支配。守卫与站点之间的语句若写入该符号（赋值、
    `++/--`、`ref/out` 实参）则守卫失效。
  - 映射依据：`IdentifierExpressionTransformer.cs:990-1021`——基元底层类型走
    `io.github.ningpp.compat.Nullable.value(receiver)`（null 抛
    `IllegalStateException`，对齐 C# `InvalidOperationException`），enum/struct/类型参数
    等引用映射底层类型直接返回 receiver；有守卫时两条路径都与 C# 等值。
- `??`：安全——`BinaryExpressionTransformer.cs:286-312` 映射为
  `receiver != null ? receiver : fallback`，与 `HasValue ? Value : fallback` 等值。
- `GetValueOrDefault()`：零参且底层为类型参数时安全——
  `InvocationExpressionTransformer.cs:514-522`（及 `:1399-1405`）映射为 receiver 本身，
  擦除下 null 即 `default(T)`；带默认参数或其他未证明形态保持 Error。
- `T?` 模式匹配：审计桶只收 `.HasValue/.Value/GetValueOrDefault/??`，模式匹配不产生
  值操作站点（本轮不改变该口径，也未默认放行任何新增形态）。

### 24.2 逐站点结果（中间树）

`NullableConverter.cs` 的 3 个 `.Value` 站点全部由 `value is null` 守卫（中间树原文）：

| 站点 | 形态 | 判定 | rationale |
|---|---|---|---|
| `NullableConverter.cs:64` | `OnTryWrite` 中 `if (value is null) { ...; return true; }` 之后 | safe | `dominated after a same-receiver 'is null' early-exit guard` |
| `NullableConverter.cs:88` | `Write` 中 `if (value is null) ... else { value.Value }` | safe | `dominated by the else branch of a same-receiver 'is null' check` |
| `NullableConverter.cs:111` | `WriteNumberWithCustomHandling` 同形 else 分支 | safe | 同上 |

约束站点 `NullableConverter.cs:8`（`struct T`）：依赖计数
`D/N/B/A/Nul/NulVal = 0/0/0/0/9/0`，`judgment=lazy`、`unsafeDependentSites=0`。

### 24.3 rt10 结果

命令（`preprocess` 无 `--force`，新目录/日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10t --unified-preprocessed-src --profile java-ready `
  --no-verify-build --no-verify-tests *>&1 | Tee-Object D:\cs\rt10-baseline-w10t.log
```

- **variance-audit 通过**（第 28 个 pass，`status=ok`）；日志中 `VA020`/`VA030`
  计数 **0**（§23 的 4 条 strict Error 与 VA030 聚合门全部消失）。
- 链首次进入第 29 个 pass `record-readonly`，并在此中止：`status=failed`，
  **`error RR020`：`dotnet restore` exited with code 1**。根因是中间树生成的
  辅助项目（`Cs2jPrelude`/`Cs2jRefOutIn`/`Cs2jUnsignedMath` 及
  `src/System.Text.Json.csproj`）出现 **NU1015：PackageReference 未指定版本**
  （`Microsoft.DotNet.Build.Tasks.TargetFramework`、`System.Buffers`、`System.Memory`、
  `System.Runtime.CompilerServices.Unsafe`、`System.Threading.Tasks.Extensions`、
  `Microsoft.CodeAnalysis.CSharp.Workspaces`）；`record-readonly` 自身统计仍为
  `0 struct(s), 0 class(es) converted, 0 skipped`。后续 pass 未执行。
- variance-audit summary 原样：
  `variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json`。
- totals 实测：`constraintsStruct=24`、`constraintsUnmanaged=1`、
  `constraintDependentNullableSites=9`、**`constraintDependentNullableValueSites=0`**、
  **`constraintLazySites=25`、`constraintUnsafeSites=0`**、`unsafeUseSites=13`、
  `varianceConversionsArray=87`（safe=87）。
- `constraintDependentSites` 中 3 条 `.Value` 均为 `safe=true`（逐站点 rationale 见
  §24.2），不再有 `safe=false` 的 nullable 值操作站点。
- TFM 警告：日志中 `skipped unresolvable TFM value` 计数 **0**。

### 24.4 各 pass summary（29 个，record-readonly 中止）

```
1  conditional                ok      strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed
2  lexical-normalize          ok      lexical-normalize: 0 transformed, 352 clean, 0 #line removed, 0 extern-alias site(s), 0 global using(s), 0 file-scoped normalized, 0 raw (0 multi), 0 u8 (0 bytes), 0 verbatim, 0 esc, 0 interpolated skipped (r 0/v 0/i 0)
3  partial-merge              ok      partial-merge: 10 group(s) merged, 0 partial member pair(s) collapsed
4  local-function-lift        ok      local-function-lift: 28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual
5  null-coalesce              ok      null-coalesce: 59 transformed, 293 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual
6  expression-desugar         ok      expression-desugar: 107 transformed, 209 clean, 36 dirty-no-rewrite; coll: 35 sites (array 9/mutable 15/iface 0/span 8/builder 0/unknown 0), spread 3 (addrange 3/span-foreach 0/enum-foreach 0), empty 16, nested 1, lifted 2, skipped 0, residual 0; guard: static-abstract 0 decl/0 call, generic-attr 0 decl/0 app, scoped 129 param/24 local; interp: 218 sites, concat 162, format 4 (align 0/fmt 3/iformattable 1), fold 25, formattable-kept 0, exprtree-skip 0, handler err0|keep27, temps 63, residual 0; init: 67 obj, 1 coll (single 1/multi 0/ext 0/params 0/fluent 0), 1 dict-index, 126 target-new, 0/0 nested, 38 ctx-stmts, 144 temps, 163 skipped (2 non-strict kept), 96 residual; nc: 180 member/call, 0 idx, 24 invoke (event 0/delegate 24), 56 void, 42 inline, 83 lifted, 32 coalesce, 0 deferred-c51, 164 temps, 0 skipped, 0 residual, assign= 0 member/0 idx, 0 compound/0 event, 0 coalesce, 0 value, 0 chain, 0 rhs-nc, 0 temps, 0 residual
7  enum                       ok      enum: 85 transformed, 107 clean, 160 dirty-no-rewrite, 125 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual
8  enum-declaration           ok      enum-declaration: 6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual
9  delegate                   ok      delegate: 1 transformed, 351 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual
10 tuple                      ok      tuple-to-struct: 16 transformed, 338 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated
11 anonymous-type-lift        ok      anonymous-type-lift: 0 transformed, 358 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual
12 foreach-deconstruction     ok      foreach-deconstruction: 0 transformed, 358 clean, 0 lowered, 1 skipped
13 foreach-source             ok      foreach-source: 9 transformed, 349 clean, 0 string site(s) wrapped, 0 tuple residual(s), 0 other/KV residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 ref index loop(s), 0 ref-readonly index loop(s), 0 ref-Current enumerator(s) [5485 0, 5486 0, residual ref 0], 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)
14 pattern-desugar            ok      pattern-desugar: 15 transformed, 307 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual
15 strip-default              ok      strip-default: 97 transformed, 261 clean, 195 declarations stripped, 176 overloads generated
16 ref-out-in                 ok      ref-out-in: 208 transformed, 150 clean, 245 ref params, 241 out params, 717 call sites lifted, 153 locked members
17 unsafe-ref-lowering        ok      unsafe-ref-lowering: 0 transformed, 360 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)
18 using-resource-lift        ok      using-resource-lift: 0 transformed, 360 clean, 0 resource(s) lifted, 0 preserved (USL001)
19 fixed-span-to-array        ok      fixed-span-to-array: 1 transformed, 359 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)
20 compat-method-replacement  ok      compat-method-replacement: 2 transformed, 358 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)
21 pointer-diff-inline        ok      pointer-diff-inline: 0 transformed, 360 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)
22 unsigned                   ok      unsigned-type: 129 transformed, 231 clean, 2269 types replaced, 195 operators rewritten, 3 literals rewritten
23 numeric-boundary           ok      numeric-boundary: 360 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 0 error(s); decimal-ops: 0 binary/0 comparison/0 unary/0 compound site(s), 0 promotion(s), 0 const downgraded, 0 temp(s), 291 deferred; increment-targets: 392 property/0 indexer/0 enum-array/1 simple site(s), 1 unsigned helper call(s), 387 postfix lifted/0 prefix inlined, 387 temp(s), 0 deferred; nullable-lift: 3 rel/134 eq/4 conv site(s), 11 temp(s)
24 generic                    ok      generic: 7 transformed, 354 clean (16 default(T) encountered, 2 rewritten, 0 nullability tests folded, 11 value defaults rewritten, 2 new T(), 1 new T[n])
25 thread-static              ok      thread-static: 1 transformed, 360 clean (1 fields rewritten, 0 skipped, 8 reads, 2 writes, 0 compound, 0 increments, 0 struct lifts, 0 initializers, 0 plan rollback(s), 0 residual)
26 generic-deepen             ok      generic-deepen: 158 transformed, 203 clean (265 anchors, 16 nested propagations, 4 patterns, 7 type tests, 0 catches, 28 static methods, 17 field accesses, 542 call sites, 0 expression-tree skips, 0 plans skipped, 0 ctor-token types, 0 ctor-token sites, 0 ctor-token call sites, 1 mtok methods, 1 mtok sites, 1 mtok call sites, 0 residuals)
27 closed-metadata            ok      closed-metadata: 388 registered (1975 enumerated, 1587 deduplicated, 3 register files, 0 consumer sites linked, 443 dynamic sites skipped, 0 residuals)
28 variance-audit             ok      variance-audit: 0 variant declaration(s), 324 use site(s) (39/18/267), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json
29 record-readonly            failed  record-readonly: 0 struct(s), 0 class(es) converted, 0 skipped, 0 plan(s) rolled back, 0 site(s), 0 method(s), 0 interface ripple(s)
```

### 24.5 单测（先红后绿）

- `tests/CSharpToJava.Tests/VarianceAuditUnitTests.cs`：新增 9 条，改写 2 条既有
  用例（原"HasValue 未 lift → Error"改为"无守卫 `.Value` → Error"；原"`??` 计为
  值操作"改为"`??` 安全"）。
  - 正例：早退 `is null` 卫语句后的 `.Value`、`if (x.HasValue)` 真分支、
    `if (!x.HasValue) ... else` 的 else 分支、条件表达式 arm、`??`、零参
    `GetValueOrDefault()`、`.HasValue` 本身；
  - 负例：无守卫 `.Value`、不同 receiver 的 HasValue 守卫、守卫后被重新赋值、
    带默认参数的 `GetValueOrDefault(d)`。
  - 正例断言同时核对逐站点 `rationale`（含 `dominated ...` 证据）。
- 红：临时让守卫判定恒返回 `false`，4 条守卫正例失败（54 通过 / 4 失败）；
  恢复后 58 条 variance 单测全绿。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4808 通过 / 9 跳过 / 0 失败**（基线 4799 + 本轮净增 9 条）。
- 未执行 `CSharpToJava.ProjectTests` 与 `CSharpToJava.ConversionTests`。

### 24.6 来源文件

- `D:\cs\rt10-baseline-w10t\preprocess-report.json`（`success=false`，
  `totalSeconds=59.188`，29 个 pass；第 28 个 `variance-audit` 为 `ok`，
  第 29 个 `record-readonly` 为 `failed`）。
- `D:\cs\rt10-baseline-w10t\.cs2j-preprocessed-src\variance-audit-report.json`
  （`constraintLazySites=25`、`constraintUnsafeSites=0`、
  `constraintDependentNullableValueSites=0`）。
- 运行日志：`D:\cs\rt10-baseline-w10t.log`（临时产物，未入库）。

