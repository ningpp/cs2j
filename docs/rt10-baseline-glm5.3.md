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


