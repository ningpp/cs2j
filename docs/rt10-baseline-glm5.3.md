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
`D/N/B/A/Nul/NulVal = 0/0/0/0/7/0`，`safeDependentSites=10`（7 条 `T?` 类型使用 +
3 条守卫后的 `.Value`）、`judgment=lazy`、`unsafeDependentSites=0`。

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

## 25. 修复 record-readonly 的 NU1015（w10u，代码 `87eb43c0`）

基线 HEAD `9a8bff3e`，工作树干净。§24 止于 record-readonly 的 `RR020`
（`dotnet restore` exit 1，NU1015）。

### 25.1 复现与精确归因（修复前，w10t）

在 `D:\cs\rt10-baseline-w10t\.cs2j-preprocessed-src\src` 执行
`dotnet restore System.Text.Json.csproj -v minimal` → **exit 1**，错误全部为
NU1015（日志中无 MSB4025）：

| 项目 | 未指定版本的 PackageReference |
|---|---|
| `src/System.Text.Json.csproj` | `Microsoft.DotNet.Build.Tasks.TargetFramework`、`System.Buffers`、`System.Memory`、`System.Runtime.CompilerServices.Unsafe`、`System.Threading.Tasks.Extensions` |
| `gen/…Roslyn4.0.csproj`、`gen/…Roslyn4.4.csproj` | 上述 + `Microsoft.CodeAnalysis.CSharp.Workspaces` |
| `Cs2jPrelude`、`Cs2jRefOutIn`、`Cs2jUnsignedMath` | `Microsoft.DotNet.Build.Tasks.TargetFramework` |

- restore 日志中**没有** MSB4019：NuGet 求值把缺失 import 记为
  `由于文件不存在，项目“D:\cs\Directory.Build.props”不由 (15,3) 处的 … 导入`
  并继续；而普通 MSBuild 求值（`dotnet msbuild System.Text.Json.csproj
  -getProperty:…`）会报 `MSB4019: 找不到导入的项目“D:\cs\Directory.Build.props”`
  （扁平 props 的 `..\..\Directory.Build.props`）与 `…\NetCoreAppLibrary.props`。
- 原始项目求值（`dotnet msbuild <原始项目> -getItem:PackageReference`）实测：
  `System.Buffers=4.6.1`、`System.Memory=4.6.3`、
  `System.Threading.Tasks.Extensions=4.6.3`、
  `System.Runtime.CompilerServices.Unsafe=6.1.2`、
  `Microsoft.DotNet.Build.Tasks.TargetFramework=10.0.0-beta.26411.108`；
  `Microsoft.CodeAnalysis.CSharp.Workspaces` 分别为 `3.11.0` / `4.0.1` / `4.4.0`。

### 25.2 修复（代码 `87eb43c0`）

- `MsBuildCompileEvaluator.TryEvaluatePackageReferences`：运行
  `dotnet msbuild <proj> -getItem:PackageReference --nologo`，解析
  `Items.PackageReference` 的 `Identity`/`Version`；按项目路径 + 写入时间缓存，
  120s 超时，异常/非 0 退出返回 `null`；新增独立测试缝
  `PackageReferenceEvaluationOverride`（不复用 `EvaluationOverride`）。
- 复制 csproj：对 `Version="$(` 的 `PackageReference`/`PackageVersion` 用原始项目
  求值结果钉成字面值；求值失败保留原文。共享 targets 中由项目属性驱动的版本
  （`RoslynApiVersion`）不改写共享文件，而是把每个项目的该属性钉成其自身求值
  版本（3.11.0 / 4.0.1 / 4.4.0），避免三个 Roslyn 生成器互相污染。
- 扁平 `Directory.Build.props/targets`：移除目标不存在的 `<Import>`（保留其余
  属性与 SDK import），修复 MSB4019；移除 Import 后一并移除
  `IsImplicitlyDefined="true"` 的 PackageReference——它们只服务于被移除的 eng
  targets，保留会让 restore 去下载本机未缓存的 Arcade 构建任务包（实测缓存仅有
  `microsoft.dotnet.build.tasks.targetframework/8.0.0-beta.26371.5`）。
- 树准备写出的 csproj 与根部 MSBuild 目录文件不再被逐 pass 的资源拷贝覆盖
  （新增 `IsPreparedProjectOrMsBuildFile` 过滤）。
- XDocument 回写改用报告 UTF-8 的 `StringWriter`：原先声明写成
  `encoding="utf-16"` 而文件按 UTF-8 无 BOM 写入，后续 XML 读取报
  `Error: There is no Unicode byte order mark. Cannot switch to Unicode.`
  （该错误曾使 normalization-audit 直接中止）。

### 25.3 修复前后实测

- **修复前**（w10t）：29 个 pass，第 29 个 record-readonly `failed`，
  `error RR020`，根因 NU1015。
- **单 pass E2E**（新目录 `D:\cs\rt10-restore-check4`，`--pass record-readonly`）：
  record-readonly `ok`（`total: 6.64s`），随后对中间树执行
  `dotnet restore System.Text.Json.csproj` → **exit 0**，关键行：
  - `已还原 D:\cs\rt10-restore-check4\.cs2j-preprocessed-src\src\System.Text.Json.csproj (用时 143 毫秒)。`
  - `已还原 D:\cs\rt10-restore-check4\.cs2j-preprocessed-src\gen\System.Text.Json.SourceGeneration.Roslyn4.4.csproj (用时 169 毫秒)。`
  - 中间树实测钉死值：`System.Buffers 4.6.1`、`System.Memory 4.6.3`、
    `System.Threading.Tasks.Extensions 4.6.3`、`System.Runtime.CompilerServices.Unsafe 6.1.2`、
    `<RoslynApiVersion>4.4.0</RoslynApiVersion>`；`Directory.Build.targets` 中
    PackageReference 计数为 0。
  - `System.ValueTuple` 仍保留 `$(SystemValueTupleVersion)`：其条件组仅
    `.NETFramework`，当前中间树 TFM 为 netstandard2.0，不参与恢复。

### 25.4 rt10 全链复跑（w10u）

命令（`preprocess` 无 `--force`，新目录/日志）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10u --unified-preprocessed-src --profile java-ready `
  --no-verify-build --no-verify-tests *>&1 | Tee-Object D:\cs\rt10-baseline-w10u.log
```

- 29 个 pass：前 28 个 `ok`（含 `variance-audit`），record-readonly `failed`。
- **NU1015 计数 0**；新的中止点是 **NU1201**（`RR020` 计数 1，restore exit 1）：
  ```
  error NU1201: 项目 Cs2jPrelude 与 netstandard2.0 (.NETStandard,Version=v2.0) 不兼容。 项目 Cs2jPrelude 支持: net10.0 (.NETCoreApp,Version=v10.0)
  error NU1201: 项目 Cs2jRefOutIn 与 netstandard2.0 …
  error NU1201: 项目 Cs2jUnsignedMath 与 netstandard2.0 …
  ```
  （`src/System.Text.Json.csproj` 与 `gen/…Roslyn4.4.csproj` 各 3 条）
- 归因：仓库 TFM 属性（`NetCoreAppCurrent/Previous/Minimum`、
  `NetCoreAppToolCurrent`）随扁平 props 丢失，主项目与 gen 项目塌缩为
  `netstandard2.0`；注入的 `Cs2j*` 支撑项目固定 `net10.0` → 项目引用不兼容。
  这是 NU1015 之后的独立根因；本轮未改 TFM（会改变前 28 个 pass 读到的条件编译
  符号），留待后续处理。
- TFM 警告：`skipped unresolvable TFM value` 计数 **0**。
- report：`success=false`，`totalSeconds=60.14`。

### 25.5 各 pass summary（29 个，record-readonly 中止）

```
1  conditional               ok      strip-conditional: 66 transformed, 286 clean, 372 directives removed, 103 disabled blocks removed
2  lexical-normalize         ok      lexical-normalize: 0 transformed, 344 clean, 0 #line removed, 0 extern-alias site(s), 0 global using(s), 0 file-scoped normalized, 0 raw (0 multi), 0 u8 (0 bytes), 0 verbatim, 0 esc, 0 interpolated skipped (r 0/v 0/i 0)
3  partial-merge             ok      partial-merge: 11 group(s) merged, 0 partial member pair(s) collapsed
4  local-function-lift       ok      local-function-lift: 28 transformed, 316 clean, 59 lifted, 0 skipped, 0 residual
5  null-coalesce             ok      null-coalesce: 59 transformed, 285 clean, 98 ?? rewritten, 100 ??= rewritten, 98 temp vars, 57 arrow bodies expanded, 0 residual
6  expression-desugar        ok      expression-desugar: 105 transformed, 204 clean, 35 dirty-no-rewrite; coll: 35 sites (array 9/mutable 15/iface 0/span 8/builder 0/unknown 0), spread 3 (addrange 3/span-foreach 0/enum-foreach 0), empty 16, nested 1, lifted 2, skipped 0, residual 0; guard: static-abstract 0 decl/0 call, generic-attr 0 decl/0 app, scoped 129 param/21 local; interp: 218 sites, concat 162, format 4 (align 0/fmt 3/iformattable 1), fold 25, formattable-kept 0, exprtree-skip 0, handler err0|keep27, temps 63, residual 0; init: 65 obj, 1 coll (single 1/multi 0/ext 0/params 0/fluent 0), 1 dict-index, 124 target-new, 0/0 nested, 36 ctx-stmts, 142 temps, 161 skipped (2 non-strict kept), 96 residual; nc: 180 member/call, 0 idx, 24 invoke (event 0/delegate 24), 56 void, 42 inline, 83 lifted, 32 coalesce, 0 deferred-c51, 164 temps, 0 skipped, 0 residual, assign= 0 member/0 idx, 0 compound/0 event, 0 coalesce, 0 value, 0 chain, 0 rhs-nc, 0 temps, 0 residual
7  enum                      ok      enum: 82 transformed, 105 clean, 157 dirty-no-rewrite, 116 bitwise, 1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual
8  enum-declaration          ok      enum-declaration: 6 enum(s) rewritten (flags=0, non-int=5, explicit=4), 20 file(s), 0 plan(s) rolled back, 0 kept real (numeric-boundary), 0 residual
9  delegate                  ok      delegate: 1 transformed, 343 clean, 0 combine, 0 remove, 0 compound, 5 anonymous method, 0 skipped, 0 residual
10 tuple                     ok      tuple-to-struct: 16 transformed, 330 clean, 25 types, 35 literals, 10 deconstructions, 0 tuple locals -> var, 19 structs generated
11 anonymous-type-lift       ok      anonymous-type-lift: 0 transformed, 350 clean, 0 creations rewritten, 0 shapes (0 generic, 0 dynamic), 0 types generated, 0 residual
12 foreach-deconstruction    ok      foreach-deconstruction: 0 transformed, 350 clean, 0 lowered, 1 skipped
13 foreach-source            ok      foreach-source: 9 transformed, 341 clean, 0 string site(s) wrapped, 0 tuple residual(s), 0 other/KV residual(s), 1 deconstruct(s) lowered, 2 duck enumerator(s) lowered (ext 0, direct 0, struct 2, dispose 0 [boxed 0], no-dispose 2, dynamic 0), 7 span index loop(s), 0 ref index loop(s), 0 ref-readonly index loop(s), 0 ref-Current enumerator(s) [5485 0, 5486 0, residual ref 0], 0 unresolved site(s), 0 residual enumerator site(s), 0 error(s)
14 pattern-desugar           ok      pattern-desugar: 15 transformed, 299 clean, 36 dirty-no-rewrite, 17 pattern-switch, 0 non-int-switch, 1 catch-filter, 0 catch-T, 8 govs, 0 end-labels, 0 skipped, 0 residual
15 strip-default             ok      strip-default: 94 transformed, 256 clean, 195 declarations stripped, 176 overloads generated
16 ref-out-in                ok      ref-out-in: 202 transformed, 148 clean, 245 ref params, 236 out params, 702 call sites lifted, 153 locked members
17 unsafe-ref-lowering       ok      unsafe-ref-lowering: 0 transformed, 352 clean, 0 ref local(s) lowered (0 read, 0 add), 1 rejected (URL001)
18 using-resource-lift       ok      using-resource-lift: 0 transformed, 352 clean, 0 resource(s) lifted, 0 preserved (USL001)
19 fixed-span-to-array       ok      fixed-span-to-array: 1 transformed, 351 clean, 1 span fixed statement(s) lowered, 0 preserved (FSA001)
20 compat-method-replacement ok      compat-method-replacement: 2 transformed, 350 clean, 2 method(s) replaced, 0 already replaced, 2 signature mismatch(es) (CMR001), 0 drifted entr(y/ies) (CMR020), 1 unused table entry(ies)
21 pointer-diff-inline       ok      pointer-diff-inline: 0 transformed, 352 clean, 0 helper(s) inlined, 0 call site(s) rewritten, 0 unused helper(s) removed, 0 preserved (PDI001)
22 unsigned                  ok      unsigned-type: 123 transformed, 229 clean, 2206 types replaced, 195 operators rewritten, 3 literals rewritten
23 numeric-boundary          ok      numeric-boundary: 352 file(s), 9 binary/0 unary/13 conversion/0 increment site(s) explicitized, 338 unchecked expr(s)/0 block(s) stripped, 92 constant(s) materialized, 0 error(s); decimal-ops: 0 binary/0 comparison/0 unary/0 compound site(s), 0 promotion(s), 0 const downgraded, 0 temp(s), 289 deferred; increment-targets: 392 property/0 indexer/0 enum-array/1 simple site(s), 1 unsigned helper call(s), 387 postfix lifted/0 prefix inlined, 387 temp(s), 0 deferred; nullable-lift: 3 rel/134 eq/4 conv site(s), 11 temp(s)
24 generic                   ok      generic: 7 transformed, 346 clean (16 default(T) encountered, 2 rewritten, 0 nullability tests folded, 11 value defaults rewritten, 2 new T(), 1 new T[n])
25 thread-static             ok      thread-static: 1 transformed, 352 clean (1 fields rewritten, 0 skipped, 8 reads, 2 writes, 0 compound, 0 increments, 0 struct lifts, 0 initializers, 0 plan rollback(s), 0 residual)
26 generic-deepen            ok      generic-deepen: 154 transformed, 199 clean (265 anchors, 16 nested propagations, 4 patterns, 7 type tests, 0 catches, 28 static methods, 17 field accesses, 526 call sites, 0 expression-tree skips, 0 plans skipped, 0 ctor-token types, 0 ctor-token sites, 0 ctor-token call sites, 1 mtok methods, 1 mtok sites, 1 mtok call sites, 0 residuals)
27 closed-metadata           ok      closed-metadata: 382 registered (1959 enumerated, 1577 deduplicated, 3 register files, 0 consumer sites linked, 443 dynamic sites skipped, 0 residuals)
28 variance-audit            ok      variance-audit: 0 variant declaration(s), 323 use site(s) (39/18/266), 87 variance conversion(s), 13 unsafe; 56 constraints (struct/unmanaged/new/...); report=variance-audit-report.json
29 record-readonly           failed  record-readonly: 0 struct(s), 0 class(es) converted, 0 skipped, 0 plan(s) rolled back, 0 site(s), 0 method(s), 0 interface ripple(s)
```

### 25.6 单测与回归

- `tests/CSharpToJava.Tests/ProjectPreprocessPackageVersionTests.cs` 新增 14 条
  （全部离线，经 `PackageReferenceEvaluationOverride` 注入，不启动 msbuild）：
  属性版本钉死、求值 null 保留原文、字面版本不改写、`PackageVersion` 中央管理
  形态、共享 targets 属性联动、缺失 import 剔除（存在保留 / 缺失移除 / 未解析
  属性剔除 / 隐式包引用联动）、`-getItem` JSON 解析、树准备文件过滤。
- 红灯：临时让 `PinPackageReferenceVersions` 直接返回原文 →
  **4 失败 / 10 通过**；恢复后 14 条全绿。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` →
  **4822 通过 / 9 跳过 / 0 失败**（§24 基线 4808/9/0，净增 14）。
- `tests/CSharpToJava.ProjectTests` 仅 `dotnet build`：**0 错误**（未运行）。

### 25.7 来源文件

- `D:\cs\rt10-baseline-w10u\preprocess-report.json`（`success=false`，
  `totalSeconds=60.14`，29 个 pass；第 28 个 `variance-audit` 为 `ok`，
  第 29 个 `record-readonly` 为 `failed`）。
- `D:\cs\rt10-baseline-w10u.log`（临时产物，未入库）。
- `D:\cs\rt10-restore-check4.log`、`D:\cs\rt10-check4-restore.log`
  （单 pass + restore exit 0 证据，临时产物，未入库）。

## 26. 修复 record-readonly 的 NU1201（w10v，代码 `7900efaa`）

### 26.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；修复前 HEAD：`52d7bb72`；本轮代码提交：`7900efaa`。
- 环境：Windows；.NET SDK `10.0.300`。
- 源：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  全链目标：`D:\cs\rt10-baseline-w10v`；unified 中间树
  `D:\cs\rt10-baseline-w10v\.cs2j-preprocessed-src`。
- 全链命令：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10v --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

### 26.2 根因与修复

- 根因：中间树扁平化仓库 `Directory.Build.props` 且没有 `eng/`，`$(NetCoreAppCurrent)`
  等属性求值为空；多目标主项目塌缩为 `netstandard2.0`，无法引用注入的 net10.0 支撑项目
  `Cs2jPrelude` / `Cs2jRefOutIn` / `Cs2jUnsignedMath`，restore 报 NU1201，record-readonly
  以 RR020 中止。
- 修复 A：`ProjectPreprocessSource.CopyProjectFilesAsync` 复制 csproj 时新增
  `PinTargetFramework`，用**原始项目**的 MSBuild 求值结果把 `TargetFramework(s)` 钉成单一
  字面值（优先 `TargetFramework`，否则取 `TargetFrameworks` 首个非空项）；多目标声明改写为
  单个 `<TargetFramework>`。求值失败保留原文并打 Warning；已单目标字面值项目不改写。
- 修复 B：`Cs2jPrelude` 与 `Cs2jUnsignedMath` 改为多目标
  `<TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>`（两者 `-f netstandard2.0`
  实测 0 错误）；`Cs2jRefOutIn` 因 ref 字段/`allows ref struct` 无法降到 netstandard2.0，
  保持 net10.0 单目标，并在注入时对低于 net10.0 的宿主**跳过** ProjectReference、写入显式
  Warning（代码侧 ID `ROI103`），不再产生 NU1201 恢复失败，也不静默跳过。
- 同时把 gen 项目 TFM 一并钉死：Roslyn4.4 由塌缩的 `netstandard2.0` 变为求值结果 `net10.0`，
  Roslyn3.11/4.0 按原文保持 `netstandard2.0`。

### 26.3 中间树实测（探针树 `D:\cs\rt10-tfm-check2`）

- 主项目：`<TargetFramework>net10.0</TargetFramework>`。
- gen：Roslyn4.4 `net10.0`；Roslyn3.11/4.0 `netstandard2.0`。
- 支撑项目：`Cs2jPrelude`/`Cs2jUnsignedMath` 均为
  `<TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>`；`Cs2jRefOutIn` 为 `net10.0`。
- 引用注入：主项目与 gen Roslyn4.4 均含 Cs2jPrelude/Cs2jRefOutIn/Cs2jUnsignedMath；
  gen Roslyn3.11/4.0 只含 Cs2jPrelude/Cs2jUnsignedMath（Cs2jRefOutIn 按策略跳过）。
- 分别 restore（`D:\cs\rt10-tfm-restore.log`）：

| 项目 | exit | NU1201 | NU1015 |
|------|------|--------|--------|
| `src\System.Text.Json.csproj` | 0 | 0 | 0 |
| `gen\...Roslyn3.11.csproj` | 0 | 0 | 0 |
| `gen\...Roslyn4.0.csproj` | 0 | 0 | 0 |
| `gen\...Roslyn4.4.csproj` | 0 | 0 | 0 |

- 跳过策略的警告落到 ref-out-in 的 `diagnostics`：report 中 2 条 Warning
  （Roslyn3.11、Roslyn4.0），原文均含 “The support ProjectReference was skipped;
  a host that actually uses Cs2jByRef types will fail its build with CS0246”。
  gen Roslyn3.11 实际 build 未成功（中间树 import 形状导致 MSB4019），不存在静默假成功。

### 26.4 全链复跑

- 进程退出码 **1**；report `success=false`、`totalSeconds=72.376`、**34 个 pass**。
- **record-readonly 通过**（第 33 个 pass，`ok`：
  `0 struct(s), 0 class(es) converted, 0 skipped, 0 plan(s) rolled back, 0 site(s),
  0 method(s), 0 interface ripple(s)`），**RR020 不再出现**。
- 新中止点：**struct-value-semantics**（第 34 个 pass，`failed`）：

```
D:\cs\rt10-baseline-w10v\.cs2j-preprocessed-src\src\System\Text\Json\Document\JsonDocument.Parse.cs(744,35):
error SV030: whole-tree validation failed and the new error could not be attributed to a single
struct plan (CS8716: There is no target type for the default literal.); all changes from this run
were rolled back.
```

- 该 pass summary：`struct-value-semantics: 0 struct(s) planned, 0 shim(s) generated,
  0 site(s) rewritten, 0 skipped, 0 rolled back`（SV030 为整树校验失败，非单计划失败）。
- NU1201 / NU1015 计数均为 **0**；`is below net10` TFM 警告计数为 **0**（w10u 为 4）。

### 26.5 单测与回归

- 新增 `tests/CSharpToJava.Tests/ProjectPreprocessTargetFrameworkTests.cs` 共 10 条，
  全部离线（经 `MsBuildCompileEvaluator.EvaluationOverride` 注入假求值，不启动 msbuild）：
  宏 TFM 钉死、`TargetFrameworks` 首项回退、求值失败保留原文、已单目标不改写且不调用求值、
  字面多目标折叠、netstandard2.0 宿主跳过 Cs2jRefOutIn 并告警、net10.0 宿主正常注入、
  旧失效引用被移除、两个支撑库多目标断言、`CanBeReferencedBy` 判定。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` →
  **4832 通过 / 9 跳过 / 0 失败**（§25 基线 4822/9/0，净增 10）。
- `tests/CSharpToJava.ProjectTests` 仅 `dotnet build`：**0 错误**（未运行）。
- 支撑库构建实测：`Cs2jPrelude`、`Cs2jUnsignedMath` 的 `netstandard2.0` 目标均 0 错误；
  `Cs2jRefOutIn` 在临时副本上以 netstandard2.0 构建失败（CS9240/CS9064/CS0246），
  故保持 net10.0 单目标。

### 26.6 来源文件

- `D:\cs\rt10-baseline-w10v\preprocess-report.json`（`success=false`、
  `totalSeconds=72.376`、34 个 pass；第 33 个 `record-readonly` 为 `ok`，
  第 34 个 `struct-value-semantics` 为 `failed`）。
- `D:\cs\rt10-baseline-w10v.log`（全链输出，临时产物，未入库）。
- `D:\cs\rt10-tfm-check2\preprocess-report.json`、`D:\cs\rt10-tfm-check2.log`
  （保留中间树的探针运行，临时产物，未入库）。
- `D:\cs\rt10-tfm-restore.log`（四个项目分别 restore exit 0 证据，临时产物，未入库）。

## 27. 修复 struct-value-semantics 的 CS8716（w10w，代码 `29480986`）

### 27.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；修复前 HEAD：`57ba228b`；本轮代码提交：`29480986`。
- 环境：Windows；.NET SDK `10.0.300`。
- 源：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  全链目标：`D:\cs\rt10-baseline-w10w`；unified 中间树
  `D:\cs\rt10-baseline-w10w\.cs2j-preprocessed-src`。
- 全链命令（与 §26 相同，仅换目标目录）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10w --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

### 27.2 根因（插桩实测）

失败路径临时把候选树与新增诊断落盘到 `%TEMP%\cs2j-sv030`（仅本地，未入库），
得到两条新增错误（`errors.txt`）：

```
CS8716: There is no target type for the default literal.
  file=...\src\System\Text\Json\Document\JsonDocument.Parse.cs line=744 col=35
  node=DefaultLiteralExpression text=[default]
  line=[            MetadataDb database = default!.Clone();]
CS1061: 'CancellationToken' does not contain a definition for 'Clone' ...
  file=...\src\System\Text\Json\Serialization\Metadata\JsonTypeInfoOfT.ReadHelper.cs line=153 col=157
  line=[            T? result = await DeserializeAsync<PipeReadBufferState, PipeReader>(utf8Json,
         bufferState: new PipeReadBufferState(utf8Json), cancellationToken.Clone()).ConfigureAwait(false);]
```

- 根因 A：前序 pass 把 `MetadataDb database;` 改成了 `MetadataDb database = default!;`；
  `default!` 是 `PostfixUnaryExpressionSyntax`（`SuppressNullableWarning`），
  `ValueCopyFacts.IsFreshValue` 只剥括号未剥 `!` → 判为非新鲜值 → VS-1b 包裹 clone；
  包裹后裸 `default` 不再处于目标类型位置 → CS8716。
- 根因 B：该调用第三个实参是**位置实参**，前面是命名实参 `bufferState:`；
  `ResolveParameter`（站点枚举）与 `ResolveArgumentParameter`（适用性裁决）都用
  「无 `NameColon` 的实参个数」求形参序号，把 `cancellationToken` 映射到第 1 个形参
  `bufferState`（`PipeReadBufferState`，活跃 struct）→ 对该实参插 clone →
  `cancellationToken.Clone()` → CS1061。
- 两条错误所在文件均被多个 plan 触碰，`TryAttributeDiagnostic` 无法归属唯一 plan，
  故整树回退报 SV030（非单计划失败）。

### 27.3 修复

- A1 `ValueCopyFacts.IsFreshValue`：新增透明包装剥离（括号 + null 包容 `!`）。
  `default!` 与 `default` 同判为新鲜值（零值本身即独立值），不再插 clone；
  剥离的两种包装都不改变求值次数与值身份，既有 clone 时机不变。
- A2 `CloneInsertionRewriter`：`Rewrite` 阶段遍历原树，为每个 target-typed 的
  `default` 字面量追加标注并把原模型 `ConvertedType` 记入表；在 clone 包裹
  （`ApplyExpressionSite`，含条件分支）与临时提升（`this = …` 字段式展开、
  链式赋值 `var t = …`）之前，按标注展开为 `default(global::…全名)`。
  标注按**节点**而非 Span 定位：重写器产出的游离节点 Span 会平移
  （实测同一字面量 `[195..202)` → `[322..329)`），按 Span 匹配会全部落空。
- B `StructValueSemanticsSiteEnumerator.ResolveParameter` 与
  `ValueCopyFacts.ResolveArgumentParameter` 改为
  `argumentList.Arguments.IndexOf(argument)`（C# 要求位置实参与其形参位次一致；
  `M(a, b: x, c)` 中 `c` 是第 3 个实参而非第 2 个）。

### 27.4 全链实测

- 进程退出码 **1**；report `success=false`、`totalSeconds=75.51`、**41 个 pass**。
- **struct-value-semantics 通过**（第 34 个 pass，`ok`）：
  `struct-value-semantics: 30 struct(s) planned, 30 shim(s) generated, 55 site(s) rewritten, 0 skipped, 0 rolled back`，
  **SV030 / CS8716 不再出现**。
- 后续 pass 逐项 `ok`：

| # | pass | summary |
|---|------|---------|
| 35 | unsafe-ffm | 0 unsafe block、1 unsafe modifier stripped；115 explicit stackalloc；19 rejection（UF021=14, UF040=1, UF050=1, UF052=4, UF051=2, UF055=1）；45 file(s) |
| 36 | stackalloc-lowering | 4 transformed, 360 clean, 12 site(s) lowered (12 span) |
| 37 | ref-struct-lowering | 1 transformed, 363 clean, 1 ref struct lowered, 4 preserved (RSL001) |
| 38 | ref-struct-span-field-memory-lift | 1 transformed, 363 clean, 1 lowered, 2 span field lifted, 3 preserved (RSM001) |
| 39 | pointer-deref-index | 0 transformed, 364 clean, 0 deref rewritten, 0 preserved |
| 40 | closure-lift | 22 transformed, 342 clean, 1 holder class, 1 capture lifted, 30 display class, 61 lambda method, 61 delegate site, 5 discard parameter, 13 stmt lambda residual (13 accounted) |

- 新中止点：**iterator-lowering**（第 41 个 pass，`failed`；CS2J0401，Error）：

```
[Error] src\System\Text\Json\Serialization\JsonSerializer.Read.Pipe.cs: Cannot resolve iterator element type for 'createAsyncEnumerableFromArray'.
[Error] src\System\Text\Json\Serialization\JsonSerializer.Read.Stream.cs: Cannot resolve iterator element type for 'createAsyncEnumerableFromArray2'.
[Error] src\System\Text\Json\Serialization\Converters\Collection\IAsyncEnumerableOfTConverter.cs: Cannot resolve iterator element type for 'GetAsyncEnumerator'.
```

  该 pass summary：`iterator-lowering: 1 member(s) lowered, 1 yield-return, 0 yield-break,
  2 hoisted, 1 enumerator(s), 1 protected region(s), 0 using region(s), 0 lock(s),
  3 skipped, 0 residual`。三条均为 `async IAsyncEnumerable<T?>` 局部函数/成员的返回类型
  解析失败，与本轮 struct 值语义改动无因果关系（判据是方法返回类型，不依赖 clone 插入）。
- TFM 警告（`is below net10` / NETSDK 系列）计数 **0**。

### 27.5 单测与回归

- 新增 5 条：
  - `tests/CSharpToJava.Tests/StructValueSemanticsTargetTypedDefaultTests.cs` 4 条：
    switch 表达式实参含 `default` 臂 → 输出 `default(global::Lib.Box)` 且可编译；
    条件表达式实参含 `default` 臂（分支级 clone，回归护栏）；命名实参后接位置实参
    → 不产生 `token.Clone()`；`Box box = default!;` 视为新鲜值、不插 clone。
  - `tests/CSharpToJava.Tests/Preprocessing/StructValueSemanticsThisAssignmentTests.cs`
    新增构造函数 `this = whole ? default : other;` → `var __cs2jTa0 = whole ? default(global::Lib.Box) : other.Clone();`
    并做运行时断言（true 分支整体清零，false 分支只写 `X`）。
- 红灯（回退新代码后确认失败）：
  - 回退 `src/` 全部改动 → switch、命名实参、构造函数 3 条失败（`Not found: "default(global::Lib.Box)"`）；
  - 仅回退 `ValueCopyFacts.cs` → `default!` 那条失败（输出变成
    `Box box = default(global::Lib.Box)!.Clone();`，说明 A2 会兜底但 A1 才是避免无谓 clone 的修复）；
  - 条件表达式那条两侧均绿，属既有行为护栏。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` →
  **4837 通过 / 9 跳过 / 0 失败**（§26 基线 4832/9/0，净增 5）。
- 中间出现过一次 `SourceTextIoTests.AtomicWrite_CreatesParent_ReplacesDestination_AndLeavesNoTempFile`
  失败（`System.IO.IOException : 无法删除要被替换的文件。`，`File.Replace` 抖动），
  单跑该用例通过、复跑全量全绿；与本改动无关。
- `tests/CSharpToJava.ProjectTests`、`tests/CSharpToJava.ConversionTests` 未运行。

### 27.6 来源文件

- `D:\cs\rt10-baseline-w10w\preprocess-report.json`（`success=false`、
  `totalSeconds=75.51`、41 个 pass；第 34 个 `struct-value-semantics` 为 `ok`，
  第 41 个 `iterator-lowering` 为 `failed`）。
- `D:\cs\rt10-baseline-w10w.log`（全链输出，临时产物，未入库）。
- `%TEMP%\cs2j-sv030\errors.txt` 与同目录候选文件（SV030 插桩证据，临时产物，未入库）。

## 28. 修复 iterator-lowering 的 async 迭代器 CS2J0401 误报（w10x，代码 `dc6279c5`）

### 28.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；基线 HEAD：`fabe1b96`，本轮代码+测试
  提交：`dc6279c5`。
- 环境：Windows；.NET SDK `10.0.300`。
- 源：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  全链目标：`D:\cs\rt10-baseline-w10x`；unified 中间树
  `D:\cs\rt10-baseline-w10x\.cs2j-preprocessed-src`。
- 全链命令（`preprocess` 无 `--force`，与 §27 相同，仅换目标目录）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10x --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

### 28.2 修复

- 归属：async 迭代器返回 `IAsyncEnumerable<T>`/`IAsyncEnumerator<T>`（通常带
  `async` 修饰符），由 C18/C38 `async-normalize` 处理；`iterator-lowering`（C04）
  运行在其之前，只能低同步迭代器。
- `IteratorLoweringAnalyzer` 在 method 循环中新增 `IsAsyncIterator` 判定：语法上带
  `async` 修饰符，或语义返回类型为 `IAsyncEnumerable<T>`/`IAsyncEnumerator<T>`，
  命中即跳过（不解析同步元素类型、不产生 CS2J0401、不改写）；property/indexer
  getter 路径同样加该防御。
- 同步迭代器（`IEnumerable<T>`/`IEnumerator<T>`/非泛型两接口）路径不变。

### 28.3 全链实测

- 进程退出码 **1**；report `success=false`、`totalSeconds=77.85`、**42 个 pass**
  （41 个 `ok`，第 42 个 `async-normalize` 为 `failed`）。
- **iterator-lowering 通过**（第 41 个 pass，`ok`，无诊断）：
  `iterator-lowering: 1 member(s) lowered, 1 yield-return, 0 yield-break, 2 hoisted, 1 enumerator(s), 1 protected region(s), 0 using region(s), 0 lock(s), 0 skipped, 0 residual`。
  三条 CS2J0401 不再出现。
- 后续 pass：

| # | pass | status | summary |
|---|------|--------|---------|
| 41 | iterator-lowering | ok | `iterator-lowering: 1 member(s) lowered, 1 yield-return, 0 yield-break, 2 hoisted, 1 enumerator(s), 1 protected region(s), 0 using region(s), 0 lock(s), 0 skipped, 0 residual` |
| 42 | async-normalize | failed | `async-normalize: 0 lambda(s) lifted (0 captured), 0 wrapper(s), 19 ConfigureAwait stripped, 25 await-site(s) kept, 1 skipped, 0 await-foreach, 0 await-using, 102 async-type sites, 0 async-iterator rejected, 3 async-iterator lowered, 5 residual` |

- 第 1–40 个 pass 均 `ok`；与 §27 相比 `transformed`/`clean` 计数一致，仅
  closure-lift 的 summary 计数在两次运行间有抖动（w10w report：29 display class /
  58 lambda method / 16 stmt residual；w10x：30 / 59 / 15），与本轮改动无因果关系。
- unified 中间树中三个异步迭代器均由 `async-normalize` 处理：`createAsyncEnumerableFromArray`
  → `Cs2jAsyncEnumerable_JsonSerializer_0<T>`，`GetAsyncEnumerator` →
  `Cs2jAsyncIterator_BufferedAsyncEnumerable_0`（`iterator-lowering` 未改写）。
- 新中止点：**async-normalize**（第 42 个 pass，`failed`），2 条 Error：

```
[Error] src\System\Text\Json\Serialization\Metadata\JsonTypeInfoOfT.WriteHelpers.cs: ConfigureAwait(ConfigureAwaitOptions) cannot be normalized: the options form (e.g. SuppressThrowing) has no Java-side equivalent.
[Error] src\System\Text\Json\Serialization\Metadata\JsonTypeInfoOfT.WriteHelpers.cs: 5 residual async construct(s) remain after async normalization (un-annotated async lambda/local function, ConfigureAwait call(s), or un-exempted async void member(s)).
```

  对应诊断码：前者 `CS2J1808`（`ConfigureAwait(ConfigureAwaitOptions)` 形态），
  后者为 strict 残留闸门 `CS2J1810`。
- TFM 警告（`skipped unresolvable TFM value` / `is below net10` / NETSDK 系列）
  计数 **0**。

### 28.4 单测与回归

- 新增 4 条（`tests/CSharpToJava.Tests/IteratorLowering/AsyncIteratorOwnershipTests.cs`）：
  - `static async IAsyncEnumerable<T?> M(...)` + `yield return` → 无 CS2J0401、
    `Changed=false`、输出与输入逐字相同；
  - `public async IAsyncEnumerator<TElement> GetAsyncEnumerator(...)` → 同上；
  - 同步 `IEnumerable<int>` 迭代器照常 lowering（无残留 `yield return`，输出可编译）；
  - 非 async 但返回 `IAsyncEnumerable<int>`（CS1624 非法形态）→ 同样跳过、不阻断。
- 先红后绿：回退 `IteratorLoweringAnalyzer.cs` 后 4 条中 **3 条失败、1 条通过**
  （同步迭代器回归用例通过）；恢复后 4/4 通过。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` →
  **4841 通过 / 9 跳过 / 0 失败**（§27 基线 4837/9/0，净增 4）。
- `tests/CSharpToJava.ProjectTests`、`tests/CSharpToJava.ConversionTests` 未运行。

### 28.5 来源文件

- `D:\cs\rt10-baseline-w10x\preprocess-report.json`（`success=false`、
  `totalSeconds=77.85`、42 个 pass；第 41 个 `iterator-lowering` 为 `ok`，
  第 42 个 `async-normalize` 为 `failed`）。
- `D:\cs\rt10-baseline-w10x.log`（全链输出，临时产物，未入库）。

## 29. 修复 async-normalize 的 ConfigureAwaitOptions CS2J1808/CS2J1810（w10y，代码 `44473e76`）

### 29.1 运行口径

- 运行日期：2026-11-07（Asia/Shanghai）；基线 HEAD：`4506e403`，本轮代码+测试
  提交：`44473e76`。
- 环境：Windows；.NET SDK `10.0.300`。
- 源：`D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj`；
  全链目标：`D:\cs\rt10-baseline-w10y`；unified 中间树
  `D:\cs\rt10-baseline-w10y\.cs2j-preprocessed-src`。
- 全链命令（`preprocess` 无 `--force`，与 §28 相同，仅换目标目录）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10y --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

### 29.2 修复

- 可归一化判定（`AsyncShapeClassifier.IsNormalizableOptionsAwait`）：调用必须经
  SemanticModel 符号绑定为 `System.Threading.Tasks.Task`/`ValueTask(T)` 的
  `ConfigureAwait(ConfigureAwaitOptions)`，且
  ① 调用是 `AwaitExpression` 的操作数；② 该 await 整体是
  `ExpressionStatementSyntax`（独立 `await ...;`，结果未使用）；
  ③ options 实参为编译期常量（可证明是否含 `SuppressThrowing` 位 `2`）。
- 归一化（`ConfigureAwaitStripper.TryRewriteOptionsStatement`，由
  `AwaitShapeRewriter` 与 `AsyncLambdaLifter` 共用）：
  - options 含 `SuppressThrowing` → `try { await <task>; } catch { }`（等待并吞掉
    任务异常与取消，与 options 形态语义一致）；
  - options 不含 `SuppressThrowing`（`None`/`ContinueOnCapturedContext`/
    `ForceYielding`）→ `await <task>`（等价于剥离 `ConfigureAwait(false)`，
    Java 无 SynchronizationContext）。
- 不满足独立 await 语句（结果被使用、嵌套在表达式中）、options 非常量、
  自定义同名扩展（receiver 非 Task/ValueTask）→ 保持原诊断
  `CS2J1808` Error，不改写。
- Analyzer 仅对可归一化站点放行；`JsonTypeInfoOfT.WriteHelpers.cs` 的
  `await state.PendingTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);`
  归一化后不再进入 residual 闸门，`CS2J1810` 随之消失。

### 29.3 全链实测（w10y）

- 进程退出码 **1**；report `success=false`、`totalSeconds=104.748`、**53 个 pass**
  （第 1–52 个 `ok`，第 53 个 `normalization-audit` 为 `failed`）。
- **async-normalize 通过**（第 42 个 pass，`ok`、无诊断）：

```
async-normalize: 0 lambda(s) lifted (0 captured), 0 wrapper(s), 23 ConfigureAwait stripped, 25 await-site(s) kept, 0 skipped, 0 await-foreach, 0 await-using, 62 async-type sites, 0 async-iterator rejected, 0 async-iterator lowered, 0 residual
```

  `CS2J1808`、`CS2J1810` 均不再出现，residual 归零。
- 后续 pass：

| # | pass | status | summary |
|---|------|--------|---------|
| 42 | async-normalize | ok | `async-normalize: 0 lambda(s) lifted (0 captured), 0 wrapper(s), 23 ConfigureAwait stripped, 25 await-site(s) kept, 0 skipped, 0 await-foreach, 0 await-using, 62 async-type sites, 0 async-iterator rejected, 0 async-iterator lowered, 0 residual` |
| 43 | extension-methods | ok | `extension-methods: 455 rewritten, 280 clean (463 reduced bound, 7 LINQ-owned skipped, 187 user + 0 support in-compilation, 267 external hosts [211 mapped/56 unmapped], 68/68 ref/in receivers, 0 null-conditional lifted, 1 groups rewritten, 0 in expression trees, 0 ET rollbacks, 0 single-file deferred, 2 mixed-LINQ chain sites conservatively skipped, 0/0 host override skip/force, 0 files rolled back, 15 skipped, 2 residuals)` |
| 44 | eliminate-goto | ok | `eliminate-goto: 11 transformed, 353 clean, 155 goto(s) eliminated` |
| 45 | member-desugar | ok | `member-desugar: 848 propert(y/ies) planned, 159 field(s) generated, 2235 read/153 write/1 compound site(s) rewritten, 5 initializer(s), 6 indexer(s) planned, 0 index read/1 write/0 compound site(s) rewritten, 0 index initializer(s), 0 event(s) planned (0 field(s), 0+/0- subscribe, 0 in-class combine, 0 nameof), 101 skipped, 0 rolled back, 4 residual, 0 indexer residual, 0 event residual \| operators: 20 planned, 0 binary/0 unary/0 compound/0 conversion site(s), 0 lifted, 0 gate-retained, 0 deferred(++/--), 236 metadata-only, 0 operator-residual \| finalizers: 0 detected (empty 0/dispose-pattern 0/dispose-owned-member 0/safe-handle 0/native 0/other 0/extern 0), 0 reported, 0 removed (0 forced; sidecar: none), 0 blocked, 0 residual, 0 resurrection-site(s) \| signatures: 4 explicit promoted, 0 displaced renamed, 0 dual-interface merged, 0 call-site(s) rewritten, 0 hide renamed, 0 hide bridged, 0 covariant bridge(s), 0 covariant renamed, 0 reflection-warned, 0 residual \| field-keyword: 1 properties normalized, 0 auto accessor(s) expanded, 2 reference(s), 0 field-attribute(s), 0 initializer(s), 0 residual \| records: 0 plan(s) (0 record class), 0 clone factor(ies), 0 with-site(s) (0 class/0 empty), 0 property/0 field assignment(s), 0 temp(s), 0 residual` |
| 46 | linq-channel | ok | `linq-channel: 339 chains expanded (0 methods), 0 queryable chains preserved, 0 expression-arg sites, 0 groupby chains preserved, 51 ambiguous, 1 skipped, 0 queries desugared` |
| 47 | concurrency-normalize | ok | `concurrency-normalize: 0 transformed, 364 clean, 25 lock, 0 enter-exit, 0 try-enter, 0 wait, 0 pulse, 0 skipped, 0 residual` |
| 48 | equality-concat | ok | `equality-concat: 140 transformed, 142 clean, 118 string-eq, 662 object-ref, 42 concat chains, 10 compound, 62 delegate, 10 record, 0 temps, 0 residual` |
| 49 | variance-use-site-normalize | ok | `variance-use-site-normalize: 0 transformed, 364 clean, 0 rewritten [-], 88 preserved [VR-04=88], 1 unsupported [VR-05=1], 0 rollback` |
| 50 | platform-isolation | ok | `platform-isolation: 0 site(s) (UI 0, platform 0, probes 0, interop 0); reject=0, stub=0, facade=0, suppressed=0, residual=0; mode=Report` |
| 51 | dynamic-desugar | ok | `dynamic-desugar: 0 site(s) (0 proven, 0 unprovable, 0 manifest-suppressed), 0 declaration(s) concretized, 0 residual; mode=Audit` |
| 52 | java-keywords | ok | `java-keywords: 0 symbol(s) renamed, 0 operator type(s) downgraded, 0 boolean site(s) rewritten, 0 nameof(s) literalized, 0 plan(s) skipped, 0 plan(s) rolled back` |
| 53 | normalization-audit | failed | `normalization-audit: 31 residual(s) across 12 file(s) (NORM-ENUM-ARITH=30, NORM-REF-LOCAL=1), 3 support-library file(s) skipped` |

- 新中止点：**normalization-audit**（第 53 个 pass，`failed`），31 条 residual
  （`NORM-ENUM-ARITH`=30、`NORM-REF-LOCAL`=1），前 20 条 Error 原文：

```
[Error] src\System\Text\Json\Serialization\Metadata\DefaultJsonTypeInfoResolver.Helpers.cs(410,17): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] src\System\Text\Json\Serialization\Metadata\DefaultJsonTypeInfoResolver.Helpers.cs(416,30): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] src\System\Text\Json\Serialization\Metadata\JsonPropertyInfo.cs(914,42): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] src\System\Text\Json\Serialization\Metadata\JsonTypeInfo.Cache.cs(1342,25): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] src\System\Text\Json\Reader\JsonReaderHelper.netstandard.cs(23,13): NORM-REF-LOCAL ref local must be lowered by a preprocessor before Java conversion
[Error] src\System\Text\Json\Nodes\JsonValue.cs(233,21): NORM-ENUM-ARITH enum operand in binary operator '=='
[Error] src\System\Text\Json\Nodes\JsonValue.cs(237,27): NORM-ENUM-ARITH enum operand in binary operator '=='
[Error] src\System\Text\Json\Nodes\JsonValue.cs(237,66): NORM-ENUM-ARITH enum operand in binary operator '=='
[Error] gen\JsonSourceGenerator.Emitter.cs(641,25): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Emitter.cs(973,25): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Emitter.cs(999,25): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Parser.cs(547,30): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Parser.cs(1280,33): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Parser.cs(1299,33): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Parser.cs(1324,29): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Parser.cs(1562,21): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Parser.cs(1567,21): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\JsonSourceGenerator.Parser.cs(1625,173): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\Model\PropertyGenerationSpec.cs(186,17): NORM-ENUM-ARITH enum operand in binary operator 'is'
[Error] gen\Model\SourceGenerationOptionsSpec.cs(74,55): NORM-ENUM-ARITH enum operand in binary operator 'is'
... (11 more)
```

- TFM 警告（`skipped unresolvable TFM value` / `is below net10` / NETSDK 系列）
  计数 **0**。
- 连续复跑对照（同命令、目标 `D:\cs\rt10-baseline-w10z`，`totalSeconds=105.834`）：
  第 42 个 `async-normalize` 为 `ok`，summary 为
  `0 lambda(s) lifted (0 captured), 0 wrapper(s), 25 ConfigureAwait stripped, 25 await-site(s) kept, 0 skipped, 0 await-foreach, 0 await-using, 102 async-type sites, 0 async-iterator rejected, 3 async-iterator lowered, 0 residual`
  （相对 §28 的 19 stripped/102 sites/3 lowered，恰好新增本文件 6 个站点）；
  第 53 个 `normalization-audit` 仍为 `failed`，31 条 residual 与 w10y 完全相同。
  w10y 中第 42 个 pass 的 `23 stripped/62 sites/0 lowered` 在相同输入上未复现，
  表现为两次运行间的绑定面抖动；两次运行的 `async-normalize` 均为 `ok`、
  residual 均为 0，且新中止点一致。

### 29.4 单测与回归

- 新增 4 条（`tests/CSharpToJava.Tests/Preprocessing/AsyncNormalizeConfigureAwaitOptionsTests.cs`）：
  - 独立 `await t.ConfigureAwait(SuppressThrowing);` → `try { await t; } catch { }`，
    无 `CS2J1808`、`residual=0`、`ConfigureAwaitStripped=1`，输出可解析且可编译；
  - `int value = await t.ConfigureAwait(SuppressThrowing);`（结果被使用）→ 仍
    `CS2J1808` Error、输入逐字保留；
  - 独立 `await t.ConfigureAwait(ContinueOnCapturedContext);` → 剥离为
    `await t;`、无 try/catch、无 `CS2J1808`；
  - 自定义同名 `ConfigureAwait(this Foo, ConfigureAwaitOptions)` 扩展 → 不改写、
    无 `CS2J1808`、`ConfigureAwaitStripped=0`。
- 先红后绿：实现前 4 条中 **2 条失败、2 条通过**（两个负例通过）；实现后 4/4 通过。
- 同步更新 `tests/CSharpToJava.ProjectTests/AsyncNormalizeUnitTests.cs` 中两条
  依赖旧行为（options 形态必报错）的断言为归一化后断言。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` →
  **4845 通过 / 9 跳过 / 0 失败**（§28 基线 4841/9/0，净增 4）。
- `tests/CSharpToJava.ProjectTests` 仅执行 `dotnet build`（成功）；
  `tests/CSharpToJava.ProjectTests`、`tests/CSharpToJava.ConversionTests` 未运行。

### 29.5 来源文件

- `D:\cs\rt10-baseline-w10y\preprocess-report.json`（`success=false`、
  `totalSeconds=104.748`、53 个 pass；第 42 个 `async-normalize` 为 `ok`，
  第 53 个 `normalization-audit` 为 `failed`）。
- `D:\cs\rt10-baseline-w10y.log`（全链输出，临时产物，未入库）。
- `D:\cs\rt10-baseline-w10z\preprocess-report.json` / `.log`（同命令复跑对照，
  第 42 个 `async-normalize` 为 `ok`，`25 stripped/102 sites/3 lowered/0 residual`；
  第 53 个 `normalization-audit` 同上）。

## 30. 输入树 TFM 过滤 + 枚举 `is` 模式归一化（w10z6 复跑）

### 30.1 代码前置

- 提交 A `135cb324`（`fix(preprocess): 输入树以权威编译集排除 TFM 专用文件`）：
  `ProjectPreprocessSource.FilterToGraphMembers` 在权威编译集
  （`allowMsBuildEvaluation: true`）可用时不再合并语法 explicit-only 集合，
  仅在项目完全无法解析（`fullSet == null`）时回退。未选中 TFM 的
  `JsonReaderHelper.netstandard.cs` 因此不会进入 net10 输入树。
- 提交 B `bf9a2877`（`fix(enum): is/is not 枚举常量模式归一化为 ==/!=`）：
  `EnumSyntaxRewriter` 的 `IsExpression` / `IsPatternExpression` 常量模式不再
  要求 enum 源内声明；左侧类型为该枚举（非可空）且右侧绑定到同枚举成员常量时
  改写为 `==` / `!=`。`object`、可空左值与跨类型常量保持原样。
- 单测净增 5 条：
  - `EnumArithmeticLoweringTests`：元数据 enum（`DayOfWeek`）的 `is` / `is not`
    改写；`object` 类型测试与 `DayOfWeek?` 常量模式不改写（负例）。
  - `GraphLinkedCompileFilesFilterTests`：多 TFM 条件项仅保留权威编译集命中的
    `V.net8.cs`；项目目录外显式共享源仍保留；不可解析项目按目录保守保留。
- 先红后绿：暂存 A/B 实现后两条正例失败
  （`TfmSpecificCompileItem_NotInAuthoritativeCompileSet_IsExcluded`、
  `Is_Metadata_Enum_Member_Is_Lowered_To_Equality`），恢复后相关 26/26 通过。
- 全量：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` →
  **4850 通过 / 9 跳过 / 0 失败**（§29 基线 4845/9/0，净增 5）。
- `tests/CSharpToJava.ProjectTests` 仅执行 `dotnet build`（成功）；
  ProjectTests、ConversionTests 未运行。

### 30.2 复跑口径与结果

- 运行时 HEAD `dd8b23f1`，工作树包含提交 A/B 的改动（随后分别提交为
  `135cb324`、`bf9a2877`）。
- 命令（与 §29 相同，无 `--force`）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10z6 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10z6.log
```

- 进程退出码 **1**；report `success=false`、`totalSeconds=100.184`、
  **53 个 pass**（52 个 `ok`，第 53 个 `normalization-audit` 为 `failed`）；
  墙钟耗时 101.63s。
- pass 顺序与状态：
  `conditional`、`lexical-normalize`、`partial-merge`、`local-function-lift`、
  `null-coalesce`、`expression-desugar`、`enum`、`enum-declaration`、`delegate`、
  `tuple`、`anonymous-type-lift`、`foreach-deconstruction`、`foreach-source`、
  `pattern-desugar`、`strip-default`、`ref-out-in`、`unsafe-ref-lowering`、
  `using-resource-lift`、`fixed-span-to-array`、`compat-method-replacement`、
  `pointer-diff-inline`、`unsigned`、`numeric-boundary`、`generic`、
  `thread-static`、`generic-deepen`、`closed-metadata`、`variance-audit`、
  `record-readonly`、`array-method`、`index-range`、`make-readonly`、
  `attribute-normalize`、`struct-value-semantics`、`unsafe-ffm`、
  `stackalloc-lowering`、`ref-struct-lowering`、
  `ref-struct-span-field-memory-lift`、`pointer-deref-index`、`closure-lift`、
  `iterator-lowering`、`async-normalize`、`extension-methods`、
  `eliminate-goto`、`member-desugar`、`linq-channel`、
  `concurrency-normalize`、`equality-concat`、
  `variance-use-site-normalize`、`platform-isolation`、`dynamic-desugar`、
  `java-keywords` 全部 `ok`，`normalization-audit` `failed`。
- 关键 summary：
  - `enum`: `87 transformed, 107 clean, 156 dirty-no-rewrite, 125 bitwise,
    1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual`。
  - `member-desugar`: `848 propert(y/ies) planned, 159 field(s) generated,
    2233 read/153 write/1 compound site(s) rewritten, ..., 4 residual`。
  - `normalization-audit`: `17 residual(s) across 9 file(s)
    (NORM-ENUM-ARITH=17), 3 support-library file(s) skipped`。
- TFM 警告（`skipped unresolvable TFM value` / `is below net10` / `NETSDK`）
  计数 **0**。

### 30.3 残留变化与归因

- **NORM-REF-LOCAL：1 → 0**。`JsonReaderHelper.netstandard.cs` 已不在输入树，
  提交 A 生效。
- **NORM-ENUM-ARITH：30 → 17**，已消除 13 条（均为元数据 enum 的
  `x is E.M` 且语义绑定完整）：
  - `Common\JsonSeparatorNamingPolicy.cs:151`（`UnicodeCategory`）；
  - `gen\Helpers\RoslynExtensions.cs` 的 `NullableAnnotation`（2 处）、
    `TypeKind.Interface`（3 处）、`SpecialType.System_Nullable_T`（1 处）；
  - `gen\JsonSourceGenerator.Parser.cs` 的 `TypeKind.Enum`、
    `SpecialType.System_Boolean`、`SpecialType.System_Char`；
  - `src\...\DefaultJsonTypeInfoResolver.Helpers.cs` 的 `MemberTypes`（2 处）。
- 剩余 17 条按根因分类：
  | 类别 | 数量 | 站点/原因 |
  |------|------|-----------|
  | 可空 enum 左值 | 7 | `JsonIgnoreCondition?`（Emitter 641、PropertyGenerationSpec 186、JsonPropertyInfo 914）、`JsonPrimitiveTypeKind?`（Emitter 973/999）、`JsonSerializerDefaults?`（SourceGenerationOptionsSpec 74）、`JsonUnmappedMemberHandling?`（JsonTypeInfo.Cache 1342）。按本 pass 口径（可空左值保持原样）不改写；改写为 `==` 仍会被 residual 探针按非 enum 左值命中。 |
  | `Accessibility` 符号不完整 | 5 | RoslynExtensions 355、Parser 1280/1299/1324/1625。pass 编译集中该元数据 enum 的 `TypeKind` 为 `Class` 且 `Accessibility.Public` 无绑定符号（重复 Roslyn 引用导致），左值不满足 enum 判定；改写为 `==` 后探针仍按左值非 enum 命中，故保持原样。 |
  | 左值类型缺失 | 2 | TypeGenerationSpec 122/137。`property.get_PropertyType()` 的 `TypeRef` 类型不在输入树（未进入编译集），左值在 pass 编译集中为 error 类型，无法确认 enum 身份。 |
  | 成员属性改写失败 | 3 | `JsonValue.cs` 233/237 的 `==`。该文件 member-desugar 本地解析校验失败（CS2J5101）被整体保留，`element.ValueKind` 未改写为 `get_ValueKind()`，左值在审计编译集中不可绑定。 |
- 因此本次**未建立“首次 rt10 全链”基线**：第 53 个 pass 仍 `failed`，
  最后残留为上述 17 条 `NORM-ENUM-ARITH`。

### 30.4 来源文件

- `D:\cs\rt10-baseline-w10z6\preprocess-report.json`（`success=false`、
  `totalSeconds=100.184`、53 个 pass、52 ok / 1 failed）。
- `D:\cs\rt10-baseline-w10z6\preprocess-residual-report.json`（17 条
  `NORM-ENUM-ARITH`）。
- `D:\cs\rt10-baseline-w10z6.log`（全链输出，临时产物，未入库）。
- `D:\cs\cs2j-fulltest-w10z5.log`（全量单测 4850/9/0，临时产物，未入库）。

## 31. 清零剩余 17 条 `NORM-ENUM-ARITH`：首次 rt10 全链基线建立（w10z7 复跑）

### 31.1 代码前置

- 提交 A `3f216981`（`fix(enum): 可空 enum 常量模式归一化 + 影子命名空间成员解析`）：
  - **可空左值（7 条）**：`x is E.M` / `x is not E.M`（`x: E?`）改写为
    `object.Equals(x, E.M)` / `!object.Equals(x, E.M)`。单次求值、null 安全
    （null → false / true，与常量模式一致），Java 侧渲染为
    `Objects.equals(...)`。**不能**用提升 `==`：numeric-boundary 的 enum-domain
    提升相等会对不稳定操作数（属性读）要求可提升的临时变量，初始化器/短路
    上下文直接 CS2J5355（首轮实测 `SourceGenerationOptionsSpec.cs:74`
    `Defaults == JsonSerializerDefaults.Web` 即失败）。
  - **`Accessibility` 5 条（原归因“重复 Roslyn 引用”不成立）**：pass 编译集里
    `Microsoft.CodeAnalysis.Accessibility` 的 TypeKind 为 Enum、左值绑定完整，
    但右侧常量无绑定符号。真实原因是 parser 把 `x is E.M` 的右侧保留在**类型
    位置**，而 `GetBasicReferences` 注入的 WindowsDesktop 载体带来顶层
    `Accessibility` 命名空间（Accessibility.dll），在类型名位置遮蔽了枚举类型
    （CS0234）。`GetSpeculativeSymbolInfo` 在同一位置仍按类型绑定、同样失败，
    故新增按左值枚举类型限定的解析回退：绑定符号 → 推测绑定 → 名称匹配
    （限定符文本须指向该枚举且成员唯一），名称回退路径输出
    `global::Microsoft.CodeAnalysis.Accessibility.Public` 全限定成员引用，
    保证中间树自身仍可绑定。
  - NORM-ENUM-ARITH 探针：可空 enum 与同类 enum 的 `==` / `!=`（enum pass
    归一化产物）与可空 enum 对 `null` 的判空不再计为残留；不同枚举之间的
    比较仍报残留（收紧了原先“两侧都是 enum 即放行”的口径）。
- 提交 B `892eb08e`（`fix(member-desugar): 属性模式路径不重写 + 分析编译纳入目录外共享源`）：
  - **`JsonValue.cs` 3 条（原归因 member-desugar 整文件保留，成立）**：根因是
    扩展属性模式路径 `o is { A.B: true }` 中的 `A.B` 被当作普通属性读改写成
    `get_A().B`，产出非法模式语法（CS1003），本地解析校验失败后整文件被保留；
    其他文件里该属性声明已去糖，留下悬空的 `element.ValueKind` 读站点，
    枚举 switch 临时变量因此在审计编译集中变成 error 类型。
    `PropertyUseSiteRewriter` 现跳过表达式冒号内的模式成员路径，两个文件
    （`JsonValue.cs`、`JsonSchemaExporter.cs`）不再回滚，`__cs2jGov_0` 正常绑定为
    `JsonValueKind`（两侧同类 enum 的 `==`，探针放行）。
  - **`TypeRef` 2 条（原归因“被 `135cb324` 过滤误删”不成立）**：`TypeRef.cs` 等
    运行时共享源物理位置在 sourceRoot 之外，枚举/审计编译从未包含其声明（更早
    的 w10z3/4/5 基线同样没有）。现于树准备阶段收集图项目的权威编译集成员中
    “位于 sourceRoot 之外”的 `.cs`（按全路径去重），由 `enum` 与
    `normalization-audit` 以 analysis-only 语法树加入整树分析编译；
    该能力按 pass 选择开启（`IncludesExternalSharedCompileSources`），避免
    `ValueStringBuilder` 等 ref struct 类型突然解析改变 ref-out-in 等 pass 判定。
  - unified 链的第 2+ 个 pass 的 `SourcePath` 已指向中间树，扁平化后的 MSBuild
    文件无法解析 `$(CommonPath)`（实测解析成 `D:\code\cs2j\Common\...`）。新增
    `ProjectPassRunRequest.OriginalSourcePath`（由 `CliPreprocessChain` 恒定传入
    用户入口），按原始入口求值一次并在进程内缓存复用。
- 单测净增 9 条：
  - `EnumArithmeticLoweringTests`：可空源内/元数据 enum 的 `is` / `is not` →
    `object.Equals` 正例、`object` 左值与跨枚举常量负例、影子命名空间常量
    改写为全限定引用（+4）。
  - `ResidualRegistryTests`：可空 enum 同类 `==`/`!=` 与判空不再计残留（+3）。
  - `GraphLinkedCompileFilesFilterTests`：sourceRoot 之外的显式编译项被报告为
    分析源、被编译集排除的 TFM 变体不报告（+1）。
  - `MemberDesugarPropertyPatternUseSiteTests`：扩展属性模式路径不重写、
    产物可解析（+1）。

### 31.2 复跑口径与结果

- 运行时 HEAD `892eb08e`（工作树干净）。
- 命令（与 §30 相同，无 `--force`，目标目录先清空）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10z7 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10z7.log
```

- 进程退出码 **0**；report `success=true`、`totalSeconds=98.810`、
  **53 个 pass 全部 `ok`**；residual report `residuals=0`（`byCode`/`byOwner`
  为空）。
- 关键 summary：
  - `enum`：`90 transformed, 107 clean, 153 dirty-no-rewrite, 125 bitwise,
    1 ~, 6 compound, 1 HasFlag, 0 lifted, 4 enum-arith, 0 residual`。
  - `member-desugar`：`848 planned, 159 field(s) generated, 2232 read/153 write/
    1 compound, ..., 101 skipped, 0 rolled back, 4 residual`（与 §30 的
    `4 residual` 持平；本任务不涉及该 residual 类别）。
  - `normalization-audit`：`0 residual(s) across 0 file(s) (), 3 support-library
    file(s) skipped`。
- TFM 警告（`skipped unresolvable TFM value` / `is below net10` / `NETSDK` /
  `target-framework evaluation failed`）计数 **0**。

### 31.3 清零后的关键形态（中间树实测）

- `gen\Model\SourceGenerationOptionsSpec.cs:74`：
  `Defaults is JsonSerializerDefaults.Web` →
  `(object.Equals(get_Defaults(), JsonSerializerDefaults.Web) ? ...)`。
- `gen\Helpers\RoslynExtensions.cs:355` 与 `gen\JsonSourceGenerator.Parser.cs`
  的 5 处：
  `ctor.DeclaredAccessibility is Accessibility.Public` →
  `ctor.DeclaredAccessibility == global::Microsoft.CodeAnalysis.Accessibility.Public`
  （全限定成员引用，影子命名空间不再影响绑定）。
- `gen\Model\TypeGenerationSpec.cs:122/134/137/138`（`TypeRef` 解析后）：
  `...SpecialType is SpecialType.System_Object` → `==`，
  `is not SpecialType.System_Object` → `!=`。
- `src\System\Text\Json\Nodes\JsonValue.cs:232-237`：
  `var __cs2jGov_0 = element.get_ValueKind();` + `__cs2jGov_0 == JsonValueKind.Null`
  / `== JsonValueKind.Object` / `== JsonValueKind.Array`，两侧同类 enum。

### 31.4 首次 rt10 全链基线建立

**首次 rt10 全链基线建立**：`System.Text.Json`（net10.0，src + 3 个 source
generator 项目 + 注入支撑库）经 53 个预处理 pass 全链处理，
`success=true`、53/53 `ok`、residual **0**、TFM 警告 **0**、墙钟 ≈98.8s，
代码 HEAD `892eb08e`。§30 的 17 条 `NORM-ENUM-ARITH` 全部按根因在 owner
（enum pass / member-desugar + 分析编译引用面）修复，未放宽审计口径。

### 31.5 回归

- `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` →
  **4859 通过 / 9 跳过 / 0 失败**（§30 基线 4850/9/0，净增 9）。
- `tests/CSharpToJava.ProjectTests` 仅执行 `dotnet build`（成功，0 错误）；
  ProjectTests、ConversionTests 未运行。

### 31.6 来源文件

- `D:\cs\rt10-baseline-w10z7\preprocess-report.json`（`success=true`、
  `totalSeconds=98.810`、53 个 pass 全 `ok`）。
- `D:\cs\rt10-baseline-w10z7\preprocess-residual-report.json`（0 条残留）。
- `D:\cs\rt10-baseline-w10z7.log`（全链输出，临时产物，未入库）。
- `D:\cs\full-test-w10z7.log`（全量单测 4859/9/0，临时产物，未入库）。

## 32. R3 消费面配置化：closed-metadata 库名迁出 Core（w10r3 复跑）

### 32.1 代码前置

- 代码提交 `6f4c0601`（父提交 `f6f91492`）。
- 新增 `config/ClosedTypeConsumerSurfaces.json`：`systemTextJson`（2 条，
  `Serialize/SerializeAsync/Deserialize/DeserializeAsync` 与
  `GetTypeInfo`）、`yaml`（10 条，`IYamlTypeConverter.Accepts`、
  `TypeConverterCache` 的 `MakeGenericType/GetGenericArguments`、
  `DefaultObjectFactory/ObjectFactoryBase`、三个
  `*NodeDeserializer`、`StaticSerializerBuilder/StaticDeserializerBuilder`）、
  `genericReflection`（`System.Type` 的 5 个保守白名单方法）；顶层
  `contextBaseTypes` 与 `sourceGenAttributes`。每条含
  `assembly/type/methods/genericArgPolicy/requiresGuard/executionKind`。
- 新增 `Core/ClosedMetadata/ConsumerSurfaces/`：`IClosedTypeConsumerProfile`
  三件套（接口 + JSON 数据模型 + 符号匹配实现）、
  `ClosedTypeConsumerSurfaces.Load/LoadDefault`（缺失、不可读、JSON 损坏、
  缺 profile 均抛 `ClosedTypeConsumerSurfaceConfigurationException`）。
- `ClosedMetadataCollector` 删除 STJ 三个全名常量与
  `SerializerMethodNames`，`IsSerializerEntry/IsSourceGenContext/
  IsJsonSerializableAttributeName` 改为 profile 匹配；
  `ClosedMetadataResidualScanner`、`ClosedMetadataConsumptionLinker` 与
  `ClosedMetadataPreprocessor` 改为注入同一 profile。
- CLI 两入口新增 `--closed-metadata-consumer-surfaces`（默认
  `config/ClosedTypeConsumerSurfaces.json`）；CLI 与三个测试项目 csproj
  均复制该配置。

### 32.2 grep 守门（Core 库名字面量）

- 新增 `ClosedMetadataConsumerSurfaceGuardTests`：扫描
  `src/CSharpToJava.Core/**/*.cs`（排除 `bin/obj`），`YamlDotNet` 命中
  **0**；`JsonSerializer` 在 `ClosedMetadata/` 子树命中 **0**。
- 子系统外 24 个文件为既有、与本任务无关的 STJ 运行时/报告序列化代码
  （如 `JavaWorkspacePlanJsonSerializer`、`TypeMappingRegistry`、各
  `*Report`），在守门测试中以**逐文件显式白名单 + 理由 + 失效校验**
  （`SerializerNameWhitelist_HasNoStaleEntries`）保留；未使用目录级静默
  豁免，`ClosedMetadata/` 内任何命中仍直接失败。

### 32.3 回归

- `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` →
  **4868 通过 / 9 跳过 / 0 失败**（§31 基线 4859/9/0，净增 9 个 R3 用例）。
- `tests/CSharpToJava.ProjectTests` 仅执行 `dotnet build`（成功，0 错误）；
  ProjectTests、ConversionTests 未运行。

### 32.4 rt10 全链复跑（行为保持证据）

命令与 §31.2 相同，目标目录 `D:\cs\rt10-baseline-w10r3`：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10r3 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10r3.log
```

- 进程退出码 0；report `success=true`、`totalSeconds=99.085`、
  **53 个 pass 全部 `ok`**；residual report `residuals=0`、`errors=0`、
  `warnings=0`（`byCode`/`byOwner` 为空）。
- TFM 警告（`skipped unresolvable TFM value` / `is below net10` /
  `NETSDK` / `target-framework evaluation failed`）计数 **0**。
- `closed-metadata` summary 与 w10z7 基线**逐项相同**：
  `387 registered (1973 enumerated, 1586 deduplicated, 3 register files,
  0 consumer sites linked, 438 dynamic sites skipped, 0 residuals)`，
  `status=ok`，`totalSeconds=2.612`（基线 2.801）。
- 其余关键 summary 同样与 §31.2 一致，例如 `member-desugar` 848 planned、
  159 field(s) generated、2230 read/153 write/1 compound、101 skipped、
  0 rolled back、4 residual；`normalization-audit` 0 residual。

### 32.5 来源文件

- `D:\cs\rt10-baseline-w10r3\preprocess-report.json`（`success=true`、
  `totalSeconds=99.085`、53 个 pass 全 `ok`）。
- `D:\cs\rt10-baseline-w10r3\preprocess-residual-report.json`（0 条残留）。
- `D:\cs\rt10-baseline-w10r3.log`（全链输出，临时产物，未入库）。
- `D:\cs\full-test-w10r3.log`（全量单测 4868/9/0，临时产物，未入库）。

## 33. R6 CLI 旋钮补齐 + RunAfterIds 启用依赖（w10r6 复跑）

### 33.1 代码前置

- 代码提交 `8057e972`（父提交 `904afb7a`）。
- 两个 CLI 入口（`preprocess` / `convert-project`）新增三个选项，经
  `CliChainRequest` → `PassChainConfiguration` 透传到既有 options：
  `--generic-deepen-overrides <path>` → `GenericDeepenOptions.OverridesPath`；
  `--generic-deepen-strict` → `GenericDeepenOptions.Strict`；
  `--closed-metadata-strict` → `ClosedMetadataOptions.Strict`。
- strict 语义：`Strict = JavaReady || <显式开关>`，即 java-ready/audit profile
  已隐含 strict，显式开关只能开启、不能关闭；默认 profile 下不传开关保持
  非 strict。
- 显式 `--pass` 选择新增 `RunAfterIds` 启用依赖闭包（`PassDependencyClosure`）：
  - **所有模式**（无论是否开启 `--unified-preprocessed-src`）都把缺失的前置 pass
    按 `PassRegistry.Ordered` 的锁定拓扑序自动加入启用集合（去重），并在 stderr
    输出 `auto-enabled dependency 'Q' for 'P' (RunAfterIds)`；
  - 补跑依据：`CliPreprocessChain.cs:504` 在每个 pass 结束后执行
    `sourcePath = run.PreprocessedSourcePath`，即显式多 pass 是**链式**执行；
    即使不开统一目录，后序 pass 读的也是前序 pass 的输出中间树，因此补跑传递
    依赖是安全的（此前"无统一目录硬报错退出 1"的语义已删除，不再拒绝既有
    ProjectTests 的 `--pass generic-deepen` 等 CLI 用法）；
  - `--disable-pass` 与自动补跑项的冲突仍由 `PassPlanResolver` 的 CS2J9002 规则
    判定；未知 pass id 仍保留在启用集合中交由 resolver 报 CS2J9002；未知
    `RunAfter` 与 RunAfter 环由 `PassRegistry` 构造期校验；
  - `--profile java-ready|audit` 追加 `normalization-audit` 的逻辑不变（在闭包
    扩展之后追加）。

### 33.2 单测（先红后绿）

- `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs` 共 7 个 R6
  用例：两入口选项解析、strict 开关接线与 profile 不可弱化、传递闭包自动补跑
  且拓扑序正确、**无统一目录显式 `--pass generic-deepen` 自动补跑 `generic`
  且顺序为 generic → generic-deepen**、已含依赖不重复添加、`--disable-pass`
  冲突走既有 resolver 规则、未知 id 仍报 CS2J9002。
- `tests/CSharpToJava.Tests/UnifiedPreprocessedSrcTests.cs` 新增 1 个 CLI
  用例：`preprocess --pass generic-deepen`（不带 `--unified-preprocessed-src`）
  退出码 0，且 `.cs2j-generic-src` 与 `.cs2j-generic-deepen-src` 两个中间树都存在。
- 先红：临时关闭闭包自动补跑分支后，上述 8 个用例 **4 失败 / 4 通过**（含 CLI
  用例被旧语义拒绝）；恢复实现后 **8 通过 / 0 失败**。
- 全量回归：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4876 通过 / 9 跳过 / 0 失败**（§33 基线 4875/9/0，净增 1 个 CLI 用例）。
- `tests/CSharpToJava.ProjectTests` 仅执行 `dotnet build`（成功，0 错误）；
  ProjectTests、ConversionTests 未运行。

### 33.3 CLI 行为实测（小样本探针）

- 非统一目录：`preprocess -s <probe>\src -d <probe>\out-plain --pass generic-deepen
  --no-verify-build --no-verify-tests` → 退出码 **0**，stderr 输出 **14** 条
  `auto-enabled dependency '…'`（首两条为 `generic for generic-deepen`、
  `unsigned for generic`），`generic` 与 `generic-deepen` 均实际执行，输出树为
  `out-plain\.cs2j-generic-deepen-src`。
- 统一目录：同输入加 `--unified-preprocessed-src` → 退出码 **0**，同样 **14** 条
  `auto-enabled dependency '…'`，输出树为 `out-unified\.cs2j-preprocessed-src`
  （行为与 §33.3 原记录一致）。

### 33.4 rt10 全链复跑（行为保持证据）

命令与 §32.4 相同，目标目录 `D:\cs\rt10-baseline-w10r6`（RunAfterIds 语义修正
后的复跑用新目录 `D:\cs\rt10-baseline-w10r6b`）：

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w10r6b --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests `
  *>&1 | Tee-Object D:\cs\rt10-baseline-w10r6b.log
```

- report `success=true`、`totalSeconds=144.913`、**53 个 pass 全部 `ok`**；
  residual report `residuals=0`、`errors=0`、`warnings=0`（filesScanned=362）。
- TFM 警告（`skipped unresolvable TFM value` / `is below net10` / `NETSDK` /
  `target-framework evaluation failed`）计数 **0**。
- 关键 summary 与 §32.4 / §33.4 基线逐项一致：`generic` 7 transformed/349 clean、
  `generic-deepen` 158 transformed/198 clean（0 residuals）、
  `closed-metadata` 387 registered（1973 enumerated、1586 deduplicated、
  3 register files、0 consumer sites linked、438 dynamic sites skipped、
  0 residuals）、`normalization-audit` 0 residual。

### 33.5 来源文件

- `D:\cs\rt10-baseline-w10r6\preprocess-report.json`（`success=true`、
  `totalSeconds=98.425`、53 个 pass 全 `ok`）。
- `D:\cs\rt10-baseline-w10r6b\preprocess-report.json`（`success=true`、
  `totalSeconds=144.913`、53 个 pass 全 `ok`）。
- `D:\cs\rt10-baseline-w10r6b\preprocess-residual-report.json`（0 条残留）。
- `D:\cs\rt10-baseline-w10r6b.log`、`D:\cs\rt10-baseline-w10r6.log`（全链输出，
  临时产物，未入库）。
- `D:\cs\r6b-probe\plain.log`、`D:\cs\r6b-probe\unified.log`（RunAfterIds 语义
  修正后的 CLI 行为探针，临时产物，未入库）。

## 34. R12 JCS-1 preprocess-manifest 两阶段握手（w12 复跑）

### 34.1 落地范围

- Core 新增 `PreprocessManifest`（`schemaVersion=1`、`subsetVersion="JCS-1"`、
  camelCase、LF、无时间戳/绝对路径、null artifacts 省略）、`PassContractHash`
  （契约规范化 SHA-256 + 按 pass id 排序的聚合）、`ManifestInputHash`（源文件 +
  项目资产 + 引用集 + solutionVariant，CRLF→LF，相对路径，跳过
  `bin`/`obj`/`.git`/`.cs2j-*`）、`JcsCapabilityScanner`（只从最终树证据派生，
  Guaranteed 仅经既有 Core-internal `GuaranteedForScanner`）。
- CLI 新增 `PreprocessManifestWriter`/`Emitter`/`Validator`/
  `JavaConversionRequirements`；`CliPreprocessChain` 成功后向
  `finalIntermediateRoot` 写 manifest，`--preprocess-only` 随
  `PromotePreprocessedTree` 移动到目的根。
- `--no-preprocess`：java-ready/audit 的缺失/版本不符/inputHash 漂移/必需能力
  非 guaranteed 统一以 `CS2J1410` 退出 1；default 原始树保留 Warning 并输出
  `subsetStatus=unverified`。必需能力单一事实源为
  `JCS.WORKSPACE`、`JCS.SYMBOL`、`JCS.CONVERSION`。
- `PreprocessReport` 增加可空 `manifest`/`subsetStatus` 字段
  （`WhenWritingNull`，既有 JSON 断言不变）。

### 34.2 单测（先红后绿）

- 新增 30 个用例：
  `PreprocessManifestTests`（3：字段顺序/camelCase/无时间戳/无绝对路径、
  null artifacts、确定性往返）、`ManifestInputHashTests`（6：跨绝对根稳定、
  源内容敏感、TFM/引用敏感、时间戳不敏感、中间目录跳过、CRLF→LF）、
  `PassContractHashTests`（4：稳定、字段敏感、聚合与顺序无关、聚合敏感）、
  `JcsCapabilityScannerTests`（7：枚举顺序与 `$(...)` 原始 TFM、无项目
  partial、collection expression 正负例、EVAL `i++` partial、Warning 文本
  含 error 不影响 SYMBOL、VALUE 无声明 unsupported、VALUE 有声明 partial
  且 owner/reason 正确）、`PreprocessManifestHandshakeTests`（10：正例、
  缺 manifest/错版本/源漂移/能力 partial 的 CS2J1410、default 原始树
  unverified、default 匹配 manifest verified、向上查找、CLI java-ready
  缺 manifest 退出 1、`--preprocess-only` 目的根落 manifest）。
- 先红：实现前 28 个用例编译/断言失败；实现后全绿；补 SYMBOL 误判与 CLI
  缺 manifest 用例后最终 30 个全过。
- 全量回归：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4906 通过 / 9 跳过 / 0 失败**（§33 基线 4876/9/0，净增 30）。
  `CSharpToJava.ProjectTests`/`ConversionTests` 未运行。

### 34.3 rt10 全链复跑

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w12 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- `success=true`、`totalSeconds=106.019`、**53/53 pass `ok`**、
  residual report `residuals=0 / errors=0 / warnings=0`、TFM 警告 **0**。
- `D:\cs\rt10-baseline-w12\.cs2j-preprocessed-src\preprocess-manifest.json`：
  `schemaVersion=1`、`subsetVersion=JCS-1`、`profile=java-ready`、
  `solutionVariant.configuration=default`、`projects=10`（树内相对路径 + `/`，
  TFM 原始文本拆分/去重/排序）、`passPlan=53`、`capabilities=8`。
- 能力状态：`JCS.WORKSPACE=guaranteed`、`JCS.SYMBOL=guaranteed`、
  `JCS.CONVERSION=guaranteed`；`JCS.CALL=partial`（`.Invoke(` 残点）、
  `JCS.EVAL=partial`（条件内 out/ref 残点）；
  `JCS.VALUE/GENERIC/CLOSURE=unsupported`（无 pass 声明，语义构造扫描留待
  R13，residual 带 reason，不伪造 guaranteed）。
- `inputHash=sha256:0d1c6a6cc36932afb12dc73220420a46c9292cf4322dfe9ad9ed70f68e412eda`；
  `contractHash=sha256:617042830e2f980129d3e68074e29fbf40d21be041958a7b91b0943305d00957`；
  `manifest=`（报告字段）`preprocess-manifest.json`、`subsetStatus=verified`。
- artifacts：`apiManifest` 省略（最终树无该文件）、
  `residualReport=preprocess-residual-report.json`、
  `complexityLedger=preprocess-report.json`。

### 34.4 确定性复跑

- 同一输入复跑到 `D:\cs\rt10-baseline-w12b`：`success=true`、
  `totalSeconds=107.41`、53/53 `ok`、residual 0、TFM 警告 0。
- 两份 manifest **逐字节一致**（无根路径字段需剔除）：
  SHA-256 均为
  `349C61B23155E0EDE1DD10E2E59C5C35990249FE32CECF953AB9608C6E659E93`。
- hash 口径：`*.cs` + `*.csproj/*.props/*.targets/*.sln/*.slnx` 内容
  （CRLF→LF）+ `solutionVariant`（configuration + 相对 path/TFM）+
  引用集（`PackageReference Include/Version`、`ProjectReference` 树内相对
  路径、`Reference` 程序集简单名）；不纳入时间戳、绝对根路径、manifest/
  report/residual/api-manifest 产物、`bin/obj/.git/.cs2j-*` 嵌套目录。

### 34.5 与 §33 对照

- §33 的 53/53、`success=true`、residual=0、TFM 警告=0 全部保持；新增的
  manifest 发射在链成功收尾后执行，不改变中间树内容。
- `preprocess-report.json` 仅新增可空 `manifest`/`subsetStatus`，既有字段
  与断言不变（`WhenWritingNull` 缺省不写）。
- 全量测试从 4876 通过增至 4906（+30），无回归。

## 35. R2 新 pass `generic-constraint` 首版实测（w13 复跑）

### 35.1 落地范围

- 新 Core 目录 `src/CSharpToJava.Core/GenericConstraint/`：
  `GenericConstraintAnalyzer`（整树 CN-12 依赖站点事实）、
  `GenericConstraintRewriter`（CN-02/04/05/06/08/09/10/11/12）、
  `GenericConstraintPreprocessor`（绑定整树 + 逐文件改写 + 任一 Error 整文件回退 +
  解析增量校验）、`GenericConstraintOptions`/`Diagnostics`/`Statistics`。
- 新 CLI adapter `GenericConstraintPassAdapter`；`PassIds.GenericConstraint`、
  `PassIds` 链序插入 `generic → generic-constraint → thread-static →
  generic-deepen`；`thread-static` 与 `generic-deepen` 的 `RunAfterIds`
  追加 `generic-constraint`。
- 契约：`Consumes/Postconditions/MayLeave = NORM-GENERIC-CONSTRAINT`、
  `Policy=PreserveAndWarn`、`Idempotent=true`、`RequiresWholeTree=true`、
  `CapabilityIds=["JCS.GENERIC"]`；pass 默认开启，`--disable-pass
  generic-constraint` 可关。
- 诊断常量归口 `PreprocessDiagnosticCodes`：`CS2J1403`（约束不可表达/
  改写不可证明，Warning）与 `CS2J1409`（DeclaredUnsupported，CN-08/CN-11）。
- CN 口径：CN-01/03/07 由上游 `generic` 负责，本 pass 只做派生断言
  （对 generic 输出二次运行 0 改写）；CN-02/05 保留不诊断；CN-04 显式化
  `Nullable<T>` 并连带 `HasValue` 级联；CN-06 保留显式装箱；CN-08/11
  DeclaredUnsupported；CN-09 改写 Parse/GetValues/GetNames（TryParse 因
  无非泛型 `out T` 等价重载保守保留 + Warning）；CN-10 Combine/Remove
  非泛型化 + `(T)`；CN-12 无依赖站点约束保留 + Warning。

### 35.2 单测（先红后绿）

- 新增 23 个用例：`GenericConstraintPassTests`（18：CN-04 显式化/比较/
  模式级联、CN-05/02/06 保留、CN-08 多维/jagged、CN-09 Parse/GetValues/
  GetNames/TryParse、CN-10、CN-11、CN-12 正负例、generic 输出幂等、
  二次运行 fixed-point、summary 分规则计数）、
  `GenericConstraintRegistryTests`（5：descriptor/契约/链序/默认计划与
  disable/adapter 工厂）。
- 兼容性断言更新：`PassChainOrderTests` 锁定链与 legacy toggle 排除集、
  `GenericDeepenRegistryTests`/`ThreadStaticRegistryTests` 邻接槽位、
  `PassContractConsistencyTests` NORM 构造 id 计数 11→12。
- 全量回归：`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4929 通过 / 9 跳过 / 0 失败**（§34 基线 4906/9/0，净增 23）。
  `CSharpToJava.ProjectTests`/`CSharpToJava.ConversionTests` 未运行。

### 35.3 rt10 全链复跑

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w13 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- `success=true`、`totalSeconds=106.672`、**54/54 pass `ok`**、
  residual report `residuals=0 / errors=0 / warnings=0`、TFM 警告 **0**。
- `D:\cs\rt10-baseline-w13\preprocess-manifest.json`：`passPlan=54`、
  `profile=java-ready`、`capabilities=8`；`JCS.GENERIC=partial`（新 pass 声明
  capability，最终树语义构造扫描仍留 R13），其余与 §34 一致
  （`JCS.WORKSPACE/SYMBOL/CONVERSION=guaranteed`，`CALL/EVAL=partial`，
  `VALUE/CLOSURE=unsupported`）。
- `generic-constraint` summary（`preprocess-report.json`）：
  `3 transformed, 353 clean (encountered 310, rewritten 14, preserved 289,
  unsupported 7; CN01 0/0/0/0, CN02 14/0/14/0, CN03 0/0/0/0, CN04 12/12/0/0,
  CN05 238/0/238/0, CN06 12/0/12/0, CN07 0/0/0/0, CN08 0/0/0/0,
  CN09 2/2/0/0, CN10 0/0/0/0, CN11 7/0/0/7, CN12 25/0/25/0,
  Infrastructure 0/0/0/0)`。
- CN-04 在 `NullableConverter.cs` 上把 `T?` 显式化为
  `global::System.Nullable<T>` 并把 `is null` 级联为 `!value.HasValue`；
  variance-audit VA020 的支配守卫分析按符号识别该形态，4 个原 VA020
  站点全部消解，未触发 VA030 strict gate。

### 35.4 与 §34 对照

- pass 数 53→54（新增 `generic-constraint`，`success=true`、residual 0/0/0、
  TFM 警告 0 均保持）。
- 中间树新增 3 个文件改写（`NullableConverter.cs` 等），其余 pass 的输出
  统计与 §34 同量级；manifest `passPlan` 由 53 增至 54，`JCS.GENERIC`
  由 `unsupported` 转为 `partial`。
- 全量单测由 4906 通过增至 4929（+23），无回归。

## 36. R13 VS-0 第 4 步首版：SemanticClosureScanner + finalizer 框架（w14 复跑）

### 36.1 落地范围与首版边界

- Core 新增 `src/CSharpToJava.Core/Preprocessing/Jcs/SemanticClosureScanner.cs`：
  最终树构造枚举 → capability/候选 owner 映射 → 未接管构造登记
  `NORM-SEMANTIC-CLOSURE`；`Phase=Finalize` 契约按启用链顺序做声明校验并
  “各有界执行一次”（无实现时显式记录空转）。
- Core 新增 `JcsConditionFacts.cs`：R12 `JcsCapabilityScanner` 的 EVAL 条件
  副作用分类与 R13 `condition-effect` 构造扫描共用同一事实源（抽取后 R12
  既有测试全绿，行为不变）。
- id 归口：`PassConstructIds.SemanticClosure = "NORM-SEMANTIC-CLOSURE"`、
  `PreprocessDiagnosticCodes.SemanticClosure = "CS2J1413"`（仓库内无撞号）。
- CLI 新增 `Preprocessing/PreprocessClosureReportWriter.cs`；`CliPreprocessChain`
  在 manifest 发射成功后扫描并写 `<最终树根>/preprocess-closure-report.json`
  （camelCase、缩进、UTF-8 无 BOM、LF、无时间戳、相对路径 `/`）；
  IO/扫描异常打印 CS2J1413 并失败，存在 unowned 只登记不改链结果。
  `preprocess-report.json` 新增可空 `semanticClosure` 头字段。
- **首版严格边界**：只读扫描，不改写任何源码；不新增 pass、不改 54 pass 的
  链序与行为；不修改 R12 manifest 八字段 schema（`artifacts` 仍三字段）；
  不实现真实 finalizer；`JCS.CLOSURE` 仍为 `unsupported`（不得由本切片提升）。

### 36.2 构造族与 owner 映射

| 构造族 | capability | 候选 owner | rt10 构造数 | 未接管 |
|--------|------------|------------|-------------|--------|
| `generic-typeof-type-parameter` | JCS.GENERIC | `generic-deepen` | 496 | 0 |
| `generic-default-type-parameter` | JCS.GENERIC | `generic` | 15 | 0 |
| `generic-new-type-parameter` | JCS.GENERIC | `generic` | 0 | 0 |
| `collection-expression` | JCS.CONVERSION | `expression-desugar` | 0 | 0 |
| `explicit-invoke` | JCS.CALL | 无（JE-09 未实现） | 34 | 34 |
| `condition-effect` | JCS.EVAL | 无（JE-04/08 未实现） | 143 | 143 |

- 映射口径：候选 owner 必须同时“在启用列表内 + pass `ok`”才算已接管；
  否则登记 `NORM-SEMANTIC-CLOSURE`（file/line/构造 id/候选 owner/原因）。
- rt10 最终树已无 collection expression（`expression-desugar` 已消解），
  也无 `new T()` 构造；`typeof(T)`/`default(T)` 全部由 `generic-deepen`/
  `generic` 接管。

### 36.3 unowned 统计

- `unownedCount=177`（`explicit-invoke` 34 + `condition-effect` 143），
  原因均为 “no candidate owner pass is implemented for this construct family”。
- 样本：`gen/JsonSourceGenerator.Roslyn3.11.cs:84`（`.Invoke`）、
  `Common/JsonHelpers.cs:75`（条件副作用）。
- 结论 `isClosed=false` 属首版预期登记，不影响 `success=true`。

### 36.4 finalizer 框架空转证据

- rt10 `finalizers=[]`：54 pass 中当前没有 `Phase=Finalize` 契约，故无
  声明需要执行（finalizerCount=0）。
- 单测用测试内 fake `ISemanticFinalizer` 覆盖：`Phase=Finalize` 契约按链序
  各执行一次；无实现时记录 `Executed=false` 的有界空转且不阻断闭包；
  `FinalizerFor` 非法能力、能力无启用声明 owner 均登记 `NORM-SEMANTIC-CLOSURE`
  并置 `isClosed=false`。**未新增任何真实 finalizer 实现**。

### 36.5 单测

- 新增 16 个用例：`SemanticClosureScannerTests`（13：类型参数三类正/负例、
  collection/`.Invoke`/条件副作用、纯条件负例、owner 成功/缺失/失败/禁用、
  finalizer 链序各一次、无实现空转、未知能力/无声明 owner 残差、稳定排序、
  空树 `IsClosed=true`）+ `PreprocessClosureReportWriterTests`（3：schema/
  字段/summary、确定性、CLI 链落 closure 报告与报告头字段）。
- 先红（缺类型编译失败）后绿；`PassContractConsistencyTests` 构造 id 计数
  12→13（仅新增 NORM id，不涉 manifest schema）。
- 全量 `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4945 通过 / 9 跳过 / 0 失败**（§35 基线 4929+16）。
  `CSharpToJava.ProjectTests`/`CSharpToJava.ConversionTests` 未运行。

### 36.6 rt10 全链复跑

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w14 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- `success=true`、`totalSeconds=143.126`、**54/54 pass `ok`**、residual report
  `residuals=0 / errors=0 / warnings=0`、TFM 警告 **0**。
- `preprocess-manifest.json`：`passPlan=54`、`profile=java-ready`、
  `subsetStatus=verified`、`capabilities=8`，状态与 §35 完全一致
  （`WORKSPACE/SYMBOL/CONVERSION=guaranteed`，`CALL/EVAL/GENERIC=partial`，
  `VALUE/CLOSURE=unsupported`）；`artifacts` 仍三字段。
- 最终树根 `.cs2j-preprocessed-src/preprocess-closure-report.json`：
  `schemaVersion=1`、`isClosed=false`、
  `summary{constructCount:688, unownedCount:177, finalizerCount:0}`。
- `preprocess-report.json`：`semanticClosure="preprocess-closure-report.json"`。

### 36.7 与 §35 对照

- 54 pass、`success=true`、residual 0/0/0、TFM 警告 0、manifest 八字段与
  能力三态全部保持；新增的是链尾只读闭包报告与报告头字段，不进入中间树内容。
- 闭包扫描如实量化了晚生成/未实现 owner 的构造：688 个构造中 511 个已被
  现有 owner 接管，177 个（`explicit-invoke`/`condition-effect`）登记为
  `NORM-SEMANTIC-CLOSURE`，为 VS-1/VS-2 的 finalizer 与 JE-04/JE-09 提供
  可达基线。
- 全量单测 4929 → 4945（+16），无回归。

## 37. R14 VS-0 第 5 步首版：JmRegistry 数据入库 + 双向文件级对账门（w15 复跑）

### 37.1 范围与边界

- Core 新增 `src/CSharpToJava.Core/Preprocessing/Jcs/JmRegistry.cs`：
  `JmRetirementState` 六态（`Inventory/Shadow/Enforce/Retire/Ratchet/Frozen`，
  `Frozen` 只是 partial 覆盖注记，不是第六阶段）、`JmEntry`、按 JmId ordinal
  排序的 `JmRegistry.Default`（11 条）、`CoverageFiles`、`RetiredStates`
  （`Retire`/`Ratchet`）与 `IsRetired`、只读 `Validate()`。
- CLI `JavaConversionRequirements` 增加 `RetiredJmIds`（从注册表过滤
  `Retire`/`Ratchet`，ordinal 排序）、`IsJmRetired`、`RequiredCapabilityForJm`
  （未知 JM-ID → null）。**未接入任何运行时门禁**；`--no-preprocess` 的已退休
  命中门禁（CS2J1410）留待后续波次。
- 本轮只做数据与只读对账：不改任何 pass 行为、不改 54 pass 链序、不改
  manifest 八字段 schema、不改 R12/R13 产物。`Hits` 首版均为 0（尚未实测
  语料命中，待后续波次回填）。

### 37.2 11 条 JM 数据

> H.3 表格注记“扩容至 12 个”，但表格实际只有 11 行；本轮以表格 11 行为准，
> 该差异已由单测与本节固定。

| JM-ID | LocationFile | capability / 规则 | 初始退休态 |
|-------|--------------|-------------------|------------|
| `JM-CALL-DELEGATE-INVOKE` | `InvocationExpressionTransformer.cs` | JCS.CALL / JE-09 | Inventory |
| `JM-CALL-METHOD-GROUP` | `InvocationExpressionTransformer.cs` | JCS.CALL / JE-09 | Inventory |
| `JM-CALL-RAW-RECEIVER-RENDER` | `IdentifierExpressionTransformer.cs` | JCS.CALL / JE-09 | Ratchet |
| `JM-COERCION-COLLECTION-BRIDGE` | `ArgumentTransformer.Coercion.cs` | JCS.CONVERSION / JE-03 | Inventory |
| `JM-COLLECTION-CONFORMANCE` | `CompatCollectionConformance.cs` | JCS.CONVERSION / JE-02 | Inventory |
| `JM-EVAL-GETTER-PRE-STATEMENT` | `StatementTransformer.Loops.cs` | JCS.EVAL / JE-04 | Inventory |
| `JM-EVAL-IF-BYREF` | `StatementTransformer.Conditionals.cs` | JCS.EVAL / JE-08 | Inventory |
| `JM-EVAL-WHILE-EFFECT` | `StatementTransformer.Loops.cs` | JCS.EVAL / JE-04 | Inventory |
| `JM-GENERATED-EQUALITY-COMPARER` | `InvocationExpressionTransformer.cs` | JCS.SYMBOL / JE-07 | Inventory |
| `JM-JSON-DESERIALIZE-TEXT-REWRITE` | `JsonDeserializeRewrite.cs` | JCS.GENERIC / JE-01 | Inventory |
| `JM-VALUE-COPY-SITE` | `StructCloneHelper.cs` | JCS.VALUE / JE-10 | Inventory |

- 每条含稳定 `Anchors`（方法/成员名 token），用于文件级对账；行号漂移不影响
  JM-ID。多文件方法族（getter 预语句、collection/iterable/span 桥接）以
  `InitialLocation` 记录次要文件，`LocationFile` 取主落点。
- **路径修正（H.3 漂移）**：`JM-CALL-RAW-RECEIVER-RENDER` 的实际实现
  `StripTrailingGenericArguments` 已位于
  `Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs:273,498-502`，
  不再在 `InvocationExpressionTransformer.cs`；本轮按真实路径登记。
  `InvocationExpressionTransformer.cs` 因此仍覆盖 3 条 JM（method-group /
  delegate-invoke / generated-equality）。

### 37.3 双向文件级对账门

- `CoverageFiles` 为 11 条涉及的 distinct `LocationFile` 集合，共 **8 个文件**
  （`InvocationExpressionTransformer.cs` 覆盖 3 条，`Loops.cs` 覆盖 2 条，其余
  6 条各占一个文件）。
- `Validate()` 返回错误列表（不抛异常）：ID 唯一、`RequiredCapability` 可
  `JcsCapabilityIds.TryParse`、`Rule` 匹配 `^JE-\d{2}$`、`LocationFile` 非空、
  `Anchors` 非空、文件在仓库中存在、文件内至少一个 Anchor 文本存在，
  以及 `CoverageFiles` 与条目文件集合双向一致（无未登记文件、无悬空条目）。
- 任一新增未登记文件或删除/移动已登记文件都会让 `Validate()` 非空；单测覆盖
  双向不一致、非法能力、空 anchors、不存在文件、重复 ID 与坏 Rule。

### 37.4 退休态现状

- 10 条 `Inventory` + 1 条 `Ratchet`（`JM-CALL-RAW-RECEIVER-RENDER`）；
  `Retire` 为 **0** 条——本轮没有任何 Java 补偿分支被真正删除。
- `Ratchet` 是长期防回流门，不是已删除实现；因此 `RetiredJmIds` 当前恰为
  `["JM-CALL-RAW-RECEIVER-RENDER"]`，且没有任何运行时门禁消费它。
  （任务描述中的“当前为空”与 H.3 将该条登记为 ratchet 巡逻冲突；本轮以
  H.3/验证步骤“仅 Ratchet 1 条”为准，并在单测注明。）

### 37.5 单测

- 新增 `tests/CSharpToJava.Tests/Preprocessing/JmRegistryTests.cs`，12 个用例：
  11 条精确集合/唯一/ordinal 排序、能力与 Rule 格式、退休态合法与 Hits=0、
  文件/锚点存在、CoverageFiles 双向一致、默认 `Validate()` 空错误、
  内存非法条目（非法能力/空 anchors/不存在文件）、重复 ID/坏 Rule、
  CoverageFiles 双向不一致、`RetiredJmIds` 与注册表一致、`RequiredCapabilityForJm`
  正/负例。
- 先红（缺 `JmRegistry`/`JmRetirementState` 编译失败）后绿；R12/R13 既有断言未改。
- 全量 `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4957 通过 / 9 跳过 / 0 失败**（§36 基线 4945 + 12）。
  首轮全量曾出现 1 次与本次改动无关的并行静态 override 竞争
  （`ProjectPreprocessTargetFrameworkTests`），单独复跑该测试类 10/10 通过，
  再次全量复跑 0 失败。
  `CSharpToJava.ProjectTests`/`CSharpToJava.ConversionTests` 未运行。

### 37.6 rt10 全链复跑

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w15 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- `success=true`、`totalSeconds=143.079`、**54/54 pass `ok`**、residual report
  `residuals=0 / errors=0 / warnings=0`、`passPlan=54`、`subsetStatus=verified`。
- `capabilities=8`，能力三态与 §36 完全一致；manifest 八字段未变。
- 闭包报告仍为 `constructCount=688 / unownedCount=177 / finalizerCount=0`、
  `isClosed=false`，与 §36 逐项一致。

### 37.7 与 §36 对照

- 产物数字零变化：54 pass、`success=true`、residual 0/0/0、manifest 八字段、
  闭包报告 688/177/0 全部保持。本轮新增的是源码侧注册表数据与只读校验门，
  不进入 preprocess 链，也不写任何新 artifact。
- 全量单测 4945 → 4957（+12），无回归；`RetiredJmIds` 当前只有 1 条 Ratchet
  防回流登记，`Retire` 为 0，VS-1~VS-4 的退休动作尚未发生。

## 38. R1a：统一合成类型参数解析器 + closure-lift M2/M3 迁移（w16 复跑）

### 38.1 范围与边界

- 按 `docs/preprocess-upgrade-glm5.3.md` 行 913-915（R1 五件套）实现 **R1a 切片**：
  新增共享解析器，并只把 closure-lift M1/M2/M3 迁到它。async、local-function-lift、
  tuple、anonymous-type-lift 的迁移留到 R1b（本轮未改其实现）。
- 不新增 pass、不改 54 pass 链序、不改 manifest/closure report schema、
  不实现 R7/R13-full/R14-runtime。
- 新解析器只依赖 Roslyn abstractions，位于 `Core/Analysis`，不引用 ClosureLift 命名空间。

### 38.2 `SyntheticTypeParameterResolver` 五件套

新增 `src/CSharpToJava.Core/Analysis/SyntheticTypeParameterResolver.cs`：

- 输入：`SemanticModel`、`member`（lambda / local function / method 等）、可选
  own `TypeParameterListSyntax` + own `ConstraintClauses`（缺省从 member 派生）、
  可选 `body`、可选 `signatureTypes`（lambda 缺省取 ConvertedType 的委托
  Invoke 参数/返回类型）。
- 输出 `SyntheticTypeParameterPlan`：
  1. `Parameters`：按宿主声明顺序（own 在前，外层方法按外→内）的有序携带集，
     每项含 `Name`、原始 `Constraint`、`Origin`（`Own/Signature/Body/ConstraintClosure`）、
     三态 `State`（`Expressible/Unconstrained/NotExpressible`）与 `Reason`；
  2. `TypeArguments`：签名可见时为空（推断），任一带入参数不在签名中则全列；
  3. `Diagnostics`：`STP001` 不可表达、`STP002` 遮蔽重命名、`STP003` degraded 引用，
     `NotExpressible` 必带 reason，禁止静默丢弃（S-03）；
  4. 类级类型形参不属于候选（嵌套成员可见，不重复声明）；
  5. 同名候选按声明序消歧，后出现者给确定性 `StableName`（`<name>_<ordinal>`，
     避开宿主可见名与其余候选名，修 S-05）。
- 约束闭包：`where T : IFoo<U>` 递归携带 U 直到不动点，按宿主声明顺序输出（S-08）。
- 语义判定全部用 `SymbolEqualityComparer.Default` 比较参数符号，不按名字猜测；
  绑定失败/约束类型为 error 时返回 `NotExpressible` + 诊断，不抛异常。

### 38.3 closure-lift 迁移

- `LiftedTypeParameterResolver.Resolve`（M1 静态 lambda）改为委托统一解析器，
  保留 `LiftedTypeParameter`/`LiftedTypeParameterSet` 记录形状，调用点不变。
- 新增 `LiftedTypeParameterResolver.ResolveForRoutine`：M2 holder 与 M3 嵌套
  display class 共用同一 `LiftedTypeParameterSet`。
  `HolderPlanner`/`DisplayClassPlanner` 删除按名字收集的
  `CollectMethodTypeParameters`/`CollectMethodConstraintClauses`，改用
  `ResolveForRoutine`；`MethodTypeParameters`/`MethodConstraintClauses`/
  `ClassTypeParameters` 的发射形状保持不变。
- 行为差异：display class 仍携带 routine 的全部 own 形参（own 必携），
  因此既有输出与 S-03 约束子句行为不变；统一解析器额外提供三态与闭包诊断
  （closure-lift 当前没有消费诊断的通道，仅保留在 plan 内，R1b 接入）。

### 38.4 单测

新增 `tests/CSharpToJava.Tests/Analysis/SyntheticTypeParameterResolverTests.cs`，8 个用例：

1. `Func<T,int>` 签名 + body 引用 T → 携带 T、`TypeArguments` 为空；
2. `where T : IFoo<U>` → 同时携带 U（`ConstraintClosure`），`TypeArguments=[T,U]`；
3. 类级 `T` 不重复声明；
4. 无约束 T → `Unconstrained`；
5. 未解析约束 → `NotExpressible` + reason + `STP001`，不抛异常；
6. 同名遮蔽 → 确定性 `StableName` 与 `STP002`；
7. closure-lift 表达式体 lambda fixture 输出可编译；
8. closure-lift 约束闭包 fixture 输出 `<T, U>` + `where T : IFoo<U>` 且可编译。

全量 `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
→ **4965 通过 / 9 跳过 / 0 失败**（§37 基线 4957 + 8）。
既有 closure-lift 相关 60 个用例全绿。

### 38.5 rt10 全链复跑（w16）

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w16 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- `success=true`、`totalSeconds=147.386`、**54/54 pass `ok`**、manifest
  `passPlan=54`、`subsetVersion=JCS-1`、`subsetStatus=verified`、`capabilities=8`；
  residual report `residuals=0 / errors=0 / warnings=0`。
- closure-lift pass：`22 transformed, 340 clean, 1 holder class(es), 1 capture(s) lifted,
  0 blocked, 0 residual, 30 display class(es), 61 lambda method(s) (31 static, 9 this-only),
  61 delegate site(s), 5 discard parameter(s) normalized, 0 quoted skipped,
  13 stmt lambda(s) residual (13 accounted)`。
- 闭包报告仍为 `constructCount=688 / unownedCount=177 / finalizerCount=0`、
  `isClosed=false`，与 §37 逐项一致（本轮无 S-xx 形状被消除，故无变化）；
  manifest 八字段未变。

### 38.6 与 §37 对照与后续

- 产物数字零变化：54 pass、residual 0/0/0、closure 688/177/0、manifest 八字段；
  本轮为纯语义解析器收敛与消费方迁移，不产生新 artifact、不改链序。
- 全量单测 4957 → 4965（+8），无回归；ProjectTests/ConversionTests 未运行
  （ProjectTests 仅 `dotnet build`）。
- **R1b 待迁移消费者**：`AsyncNormalize/AsyncNormalizeAnalyzer.ResolveGenericCarry`
  （:1051）、`LocalFunctionLift/LocalFunctionLiftRewriter.BuildConstraintClauses`
  （:892）、`TupleToStruct/TupleStructFileContent`、`AnonymousTypeLift/AnonymousTypeFileContent.RenderConstraintClauses`
  （:231）。迁移时需把 `SyntheticTypeParameterPlan.Diagnostics` 接入各自诊断通道，
  并处理 `NotExpressible` 的保守 skip/登记。

## 39. R1b：async-normalize / local-function-lift 迁移到统一解析器（w17 复跑）

### 39.1 范围与边界

- 按 `docs/preprocess-upgrade-glm5.3.md` 行 913-915（R1 五件套）实现 **R1b 切片**：
  只把 `async-normalize` 与 `local-function-lift` 的类型参数携带逻辑迁移到
  `Core/Analysis/SyntheticTypeParameterResolver`。
- **R1c 待迁移**：`TupleToStruct/TupleStructFileContent` 与
  `AnonymousTypeLift/AnonymousTypeFileContent.RenderConstraintClauses`（tuple /
  anonymous-type-lift）；本轮未改其实现。
- 不新增 pass、不改 54 pass 链序、不改 manifest/closure report schema、
  不实现 R7/R13-full/R14-runtime。

### 39.2 async-normalize 迁移

- 删除 `AsyncNormalizeAnalyzer.ResolveGenericCarry` 的名称扫描实现，lambda 与
  async local function 两个调用点统一改为 `SyntheticTypeParameterResolver.Resolve`：
  - lambda：零捕获时由解析器从 `ConvertedType` 取委托签名；安全捕获时把委托
    形参/返回类型与捕获符号类型一并作为 `signatureTypes`（捕获形参的类型同样
    可能引用外层方法类型参数）。
  - async local function：传入 own `TypeParameterList`/`ConstraintClauses`、
    形参符号类型与返回类型；宿主类级形参不是候选，因此不会重复声明。
- 保持 `LambdaLiftPlan.MethodTypeParameters`（声明）与 `ConstraintClauses`
  （where）的发射形状；新增 `CallSiteTypeArguments` 承载解析器 ④ 的输出：
  签名可见时为空（由目标委托/实参推断），任一带入参数不可推断时列出完整
  有序集，避免 CS0411。
- `NotExpressible`（STP001，含 degraded 绑定）映射为既有 async 诊断并保守回退：
  lambda → `CS2J1807`、async local function → `CS2J1804`，节点原样保留，
  绝不产出缺约束的提升方法（S-03）。
- 约束闭包（`where T : IFoo<U>` 必携 U）完全由解析器不动点保证（S-08）。

### 39.3 local-function-lift 迁移

- `LocalFunctionLiftAnalyzer` 删除按名字收集 + Gate #9 名称冲突跳过，改为一次
  `SyntheticTypeParameterResolver.Resolve`：own 形参在前，外层方法/局部函数按
  宿主声明顺序追加，约束引用递归闭包；`LiftPlan.EnclosingTypeParams` 取非 own
  的 `RenderName`，调用点显式类型实参的既有扩展行为不变。
- `LocalFunctionLiftRewriter.BuildTypeParameterList` /
  `BuildConstraintClauses` / `DeclaredTypeParameterNames` 改为消费
  `LiftPlan.TypeParameterPlan`：
  - 只发射 `Parameters` 对应的约束，同名遮蔽使用 `StableName`（按 ordinal 消歧）；
  - `NotExpressible` 在 analyzer 阶段即 `Skip`（`EnclosingTypeParameterConflict`
    + 可对账 Warning），rewriter 侧再防御性跳过，不产出悬挂 `where` 子句（S-03）；
  - 既有 `LiftPlan` 字段（`EnclosingTypeParams`/`NeedsExplicitTypeArguments`）
    保持兼容，仅新增 `TypeParameterPlan`。
- 宿主类级形参不进入候选（④）；同名遮蔽不再整函数跳过，而是由解析器去重后
  正常提升（S-05）。

### 39.4 单测

新增 9 个用例（红→绿）：

- `tests/CSharpToJava.Tests/Preprocessing/AsyncNormalizeResolverMigrationTests.cs`（5）：
  async lambda 约束闭包 `<T,U>` + `where T : IFoo<U>`；async local function
  own+外层闭包且类级形参不重声明；lambda/async local function 的
  `NotExpressible` 保守回退（CS2J1807/CS2J1804）；解析器 ④ 契约
  （签名可见 `TypeArguments` 为空、约束-only 全列）。
- `tests/CSharpToJava.Tests/LocalFunctionLiftResolverMigrationTests.cs`（4）：
  外层约束闭包、类级形参不重声明、同名遮蔽仅声明一次且可提升、
  `NotExpressible` 跳过且无悬挂 where。
- 端到端 fixture 均用 Roslyn 重编译断言 0 Error；既有 async /
  local-function-lift 58 个用例全绿。
- 全量 `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4974 通过 / 9 跳过 / 0 失败**（§38 基线 4965 + 9）。

### 39.5 rt10 全链复跑（w17）

```
dotnet build -c Release src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-baseline-w17 --unified-preprocessed-src --profile java-ready --no-verify-build --no-verify-tests
```

- `success=true`、`totalSeconds=117.503`、**54/54 pass `ok`**、manifest
  `passPlan=54`、`subsetVersion=JCS-1`、`subsetStatus=verified`、`capabilities=8`；
  residual report `residuals=0 / errors=0 / warnings=0`。
- local-function-lift：`28 transformed, 322 clean, 59 lifted, 0 skipped, 0 residual`
  （与 w16 产物逐项一致；§38 未列该 pass 明细）。
- async-normalize：`0 lambda(s) lifted (0 captured), 0 wrapper(s),
  23 ConfigureAwait stripped, 25 await-site(s) kept, 0 skipped, 0 residual`，
  与 §38 一致（rt10 无被提升的 async lambda/local function，故 R1b 不改写产物）。
- closure-lift：本轮两次运行分别为 `30 display class(es), 61 lambda method(s),
  61 delegate site(s), 0 quoted skipped, 13 stmt residual`（w17b）与
  `29/60/60/1/14`（w17）；两者唯一树差异为 `gen/JsonSourceGenerator.Parser.cs`
  中一个方法组是否物化 display class，语义等价。该差异在**同一份代码**的两次
  运行间可复现（w17 vs w17b），属 §28 已记录的绑定面抖动，与 R1b 迁移无关；
  w17b 与 §38 逐项一致。
- 闭包报告仍为 `constructCount=688 / unownedCount=177 / finalizerCount=0`、
  `isClosed=false`，与 §38 逐项一致；manifest 八字段未变。

### 39.6 与 §38 对照与后续

- 产物数字零变化：54 pass、residual 0/0/0、closure 688/177/0、manifest 八字段；
  local-function-lift / async-normalize summary 与 w16 一致，无 S-xx 形状消除。
- 单测 4965 → 4974（+9），无回归；ProjectTests/ConversionTests 未运行
  （ProjectTests 仅 `dotnet build`）。
- **R1c 待迁移**：tuple（`TupleStructFileContent`）与 anonymous-type-lift
  （`RenderConstraintClauses`）；迁移后需同样接入 `NotExpressible` 保守回退。

## 40. tuple 运行时身份观测项目作用域化（w19 复跑）

### 40.1 根因与作用域规则

- 根因：`typeof(ValueTuple<...>)` 的运行时身份保护 `_runtimeObservedArities` 是
  **整编译单元全局**的。合并编译包含 json 测试
  `Serialization/CustomConverterTests/CustomConverterTests.NullableTypes.cs:512`
  的 `typeof(ValueTuple<,>)`，导致 regex 项目的
  `(string CharClass, bool CaseInsensitive)` 也被保留为原生 ValueTuple；Java 侧把
  它映射成 `io.vavr.Tuple2` 后，`CharClass` / `CaseInsensitive` 等具名元素访问
  找不到符号（`RegexCode.java` / `RegexCompiler.java`）。
- 修复：`TuplePassAdapter.OnTreePreparedAsync` 保存项目树；
  `CompilationReadyAsync` 逐语法树扫描观测后按**项目**归属：
  - 项目内所有文件的观测取并集（arity 级一致，避免 struct / ValueTuple 混用 CS0029）；
  - 文件只被一个项目编译 → 该项目集合；被多个项目共享（链接源码）→ 取并集
    （改写文本只有一份，必须同时满足所有编译它的项目）；
  - 无法从 Compile 集合归属 → 最近的 csproj 目录前缀（近似）；仍无法归属 →
    整树并集（保守）。
  - Compile 集合对**源树**求值：编译准备阶段中间树尚未复制 `.cs`（逐文件改写时
    才落盘），对中间树求值会得到空集合；统一目录链中源树即中间树。
- `TupleToStructPreprocessor.RewriteFile` / 两个 `GenerateStructFile` 重载新增
  可选的 per-file / per-project 观测集合；shape 注册表仍全局共享。跨项目不再
  互相污染，同一项目内仍保持 arity 级一致。

### 40.2 Java 侧最小修复（验证中发现，w18 基线同文件已存在）

- `ArgumentTransformer.WrapMemberHolder`：`out/ref` 的成员属性写回改走
  `AssignmentTransformer.PropertySetterCall`。此前把读取侧 getter 当赋值目标，
  生成 `props.getValue() = _outArgHolder.value;`（dotnet-xml
  `QueryBuilder.java:243`，javac"意外的类型"）。
- `AssignmentTransformer.TryRenderUserDefinedCompound`：框架数值类型
  （`System.IntPtr` → Java `long`）不再发静态运算符调用。此前生成
  `long.subtract(nLength, 8)`（dotnet-text-json
  `JsonReaderHelper.java:449`，javac 把 `long` 当类）；退回普通复合赋值
  `nLength -= 8`，语义等价。

### 40.3 单测

- 新增 4 个用例：`Tupleprocessor/TupleRuntimeIdentityScopeTests.cs`（3：两项目
  观测不泄漏、同项目跨文件 arity 一致、文件/项目 arity 映射含共享与目录近似）；
  `TryGetValueComplexOutArgTests.OutArgument_ByRefHolderValue_WritesBackThroughSetter`
  （1）。`IntPtrMappingTests` 追加 2 条断言（`nLength -= 8`、不得出现
  `long.subtract`）。
- 全量 `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
  → **4978 通过 / 9 跳过 / 0 失败**（§39 基线 4974 + 4）。

### 40.4 w19 转换与 Maven 现状

```
dotnet run -c Release --no-build --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project \
  -s D:\csharp-sdk\csharp-sdk.sln -d D:\cs\cs2j-sdk-w19 --no-verify-build --no-verify-tests
```

- `Conversion complete (MSBuild): 1984 succeeded, 0 failed, 247 resources copied, modules=21`
  （§39 为 1976；+8）。
- `mvn clean package -e`：parent / `dotnet-uri` / `dotnet-text-regularexpressions` /
  `dotnet-xml` **SUCCESS**；regex 的 `Tuple2` 编译错误已消除，xml 亦已通过编译。
- **阻塞（未达全绿）**：`dotnet-text-json` FAILURE，494 个唯一编译错误（日志
  988 行含重复）。抽样 6 个报错文件与 w18 基线逐字节比对，5 个完全相同，属既有
  转换债（w18 时 regex 先失败，json/xml 未真正编译，故未被发现）。主要簇：
  `ReadOnlySpan<Integer> → Span<Integer>` 101、`int[] → byte[]` 69、
  `Utf8JsonReader → ObjectHolder<Utf8JsonReader>` 59、`找不到符号` 54、
  `byte[]/char[] → Object[]` 71。
- `mvn clean package -e -fae` 补充：`dotnet-text-regularexpressions-tests`
  （15.2s）与三个 uri 测试模块 SUCCESS；`dotnet-xml-read-content-as-tests`
  （667 tests / 2 errors）与 `dotnet-xml-xpath*` 三个模块（各 1513 tests / 9 errors）
  FAILURE，均为 `String → Double/Number` ClassCastException，同属既有转换债。
- 结论：本轮**未**达到"Maven 全绿"。后续波次首要门 = 清零 `dotnet-text-json`
  编译错误 + 上述 xml 测试错误，再重跑同一命令；不得跳过/删减测试过关。
- ProjectTests / ConversionTests 未运行（ProjectTests 仅 `dotnet build`）。
