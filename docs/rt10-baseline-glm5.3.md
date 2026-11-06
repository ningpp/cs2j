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

