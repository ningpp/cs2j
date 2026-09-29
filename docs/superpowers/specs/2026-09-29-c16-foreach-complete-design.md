# C16 foreach 补全（string / 残余解构）（foreach-source 锚点任务）设计

**日期**: 2026-09-29
**范围**: 新增 CLI 级 C#→C# 预处理器 `foreach-source`（Core 新目录 `ForeachSource/` + CLI 单适配器），把字符串 foreach 归一为 `ToCharArray()` 后 foreach，并收口 E07 未覆盖的 Tuple/Other 残余解构 foreach；本特性不改任何 C#→Java 转换代码
**状态**: 设计已确认，待实施
**族属**: catalog P2「控制流去糖族」（[preprocessor-catalog.md §3.3](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L903-L903)：C03 + C37 + C16 + C40 + 部分 C05）。注册表 stub `foreach-source` 的 `catalogItems = "C16, C40"`、`RunAfter = foreach-deconstruction`、`Stage = ControlFlow`（[PassStubCatalog.cs:102-111](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L102-L111)）。**C16 是该 pass 的锚点任务**：完成 stub→Implemented 翻转、`catalogItems` 先收窄为 `"C16"`；姊妹任务 C40（枚举器适配 / ref foreach）后续在同一 pass 内增量扩展。

---

## 1. 背景与目标

### 1.1 foreach 在转换器侧的契约与两类硬拒绝

转换器把 C# foreach 直接映射为 Java enhanced-for，并明确声明迭代源必须在预处理阶段归一为 Java 数组或 Iterable（[StatementTransformer.Loops.cs:99-103](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L99-L103)）：

> Strings, dictionaries, LINQ streams, enumerator adapters and deconstructions must be lowered by a preprocessor.

其中 dictionaries/LINQ/数组已有 E04/E12/E16 与转换器集合/数组路径分担，但仍有两个**命中即失败**站点：

1. **字符串 foreach 硬拒绝**（[StatementTransformer.Loops.cs:118-122](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L118-L122)）：

   ```csharp
   if (expressionType?.SpecialType == SpecialType.System_String)
   {
       throw new NotSupportedException(
           "CS2J normalization required: foreach over a string must be lowered (e.g. toCharArray()) by a preprocessor before Java conversion.");
   }
   ```

2. **解构 foreach 硬拒绝**（[StatementTransformer.Loops.cs:157-159](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L157-L159)）：`TransformForEachVariableStatement` 无条件抛 `NotSupportedException`——任何存活到转换器的 `ForEachVariableStatementSyntax` 都无法通过。

同文件 [L104-L155](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L104-L155) 对 `RefKeyword`/`AwaitKeyword` **既不检查也不归一**（ref foreach 静默按值语义处理）——该缺口登记为 **C40/C38，不在本任务范围**。

### 1.2 E07 已覆盖与未覆盖的面

E07（`foreach-deconstruction`，已实现、默认开）只 lower KeyValuePair 族（[ForeachDeconstructionRewriter.cs:59-73](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L59-L73)）：

- **KeyValuePair**：lower 为 `foreach (var __entry in …) { var k = __entry.Key; var v = __entry.Value; … }`；
- **Deconstruct（实例 Deconstruct 方法）**：发 Info「handled by converter」后原样透传（[L63-L66](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L63-L66)）；
- **Tuple**：发 Info「run TupleToStruct first」后原样透传（[L67-L70](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L67-L70)），依赖 E06 先跑；
- **Other**：默认分支原样透传（[L71-L72](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L71-L72)）。

两个已存在的顺序事实：

- E06（`tuple`，链中位于 E07 之前）对元素类型 `IsTupleType` 的解构 foreach **整形 lower** 为普通 foreach + `Item1/Item2` 解包（[TupleToStructPreprocessor.cs:1312-1361](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L1312-L1361)），仅当 `Variable` 既非括号 designation 声明、也非 TupleExpression 时放弃（[L1336-L1339](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L1336-L1339)）。因此 E07 对 Tuple 的 skip 是安全网：正常链路上 Tuple 族残余应趋近于零。
- E07 自陈其 Deconstruct 判定**只查实例方法，不查扩展方法**；扩展方法 Deconstruct 会落入 Other 并被「安全」透传（[ForeachDeconstructionRewriter.cs:166-171](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L166-L171)）——但按 §1.1 证据 2，透传终点是转换器无条件抛异常，并不安全。

### 1.3 窄范围与分工表

本任务严格锁定 catalog C16（[preprocessor-catalog.md:446-457](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L446-L457)）的窄边界：**string foreach + 非 KV/非 Deconstruct 族的残余解构 foreach**。

| foreach 形态 | 归属 pass | 处置 | 本任务关系 |
|---|---|---|---|
| 数组（含 `char[]`） | 转换器直连 | Java enhanced-for 直接迭代，[ArrayForeachNotCollectedTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ArrayForeachNotCollectedTests.cs) 锁定不被 collect | 不动 |
| 泛型/非泛型集合 | 转换器 + compat 映射 | Iterable 直接迭代；非泛型双 cast（[ForeachNonGenericCollectionCastTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachNonGenericCollectionCastTests.cs)） | 不动 |
| KeyValuePair 解构 | **E07**（已实现） | lower 为 `__entry.Key/Value` | 不抢语法面；C16 见 KV 残余只诊断 |
| 真 ValueTuple 解构 | **E06**（已实现） | 生成结构体 + `ItemN` 解包 | 消费其产物，不重写 |
| **string 迭代源** | **C16（本任务）** | `expr.ToCharArray()` 包裹后 foreach | 主体规则 FS-1 |
| **Tuple 族残余解构** | **C16（本任务）** | `__entry.ItemN` 解包 lower（防御性） | 规则 FS-2 |
| **Other 族残余**（扩展 Deconstruct 等） | **C16（本任务）** | v1 显式诊断、响亮失败，不静默透传 | 规则 FS-3 |
| 实例 Deconstruct 族 | 见开放问题 1 | E07 已发 Info；转换器现状硬抛 | **本任务不实现 lowering**，仅加定位诊断 |
| duck-typed `GetEnumerator` / 显式 `IEnumerator` / 扩展方法 GetEnumerator | **C40**（同 pass 后续） | 编译器 lowering 形态 `while(MoveNext())` | 不碰 |
| `ref` / `ref readonly` foreach | **C40** | 显式索引/Copy 形态 | 不碰 |
| `await foreach` | **C38**（async-normalize 族） | 异步迭代归一 | 不碰 |

### 1.4 目标

1. **string foreach 显式化（FS-1）**：对表达式静态类型为 `System.String` 的普通 foreach，把迭代表达式包裹为 `ToCharArray()` 调用，产物在中间树可编译、经转换器产出 `for (char c : <e>.toCharArray())`；求值次数、null 行为、字符/文化语义与 C# 完全一致（§2 决策 1-3）。
2. **残余解构收口（FS-2/FS-3）**：对经 E06/E07 后仍存活的 `ForEachVariableStatementSyntax`，Tuple 族做 `ItemN` 解包 lower；Other 族给稳定码位的显式诊断（strict 即 Error），禁止其静默流入 [Loops.cs:157-159](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L157-L159) 的抛点。
3. **锚点翻转**：把 `foreach-source` 从 stub 翻转为 Implemented（默认关），完成注册表/链工厂/CLI 开关/链序测试全套接线，为 C40 预留规则接口、文件所有权与诊断码段。
4. **响亮失败与 residual 闸门**：无法在 v1 归一的站点（Other 残余、异常残留）给 Error/Warning 诊断；输出树重扫保证 string foreach 零裸残留（FS-5）。
5. 产物仍是可编译 C#，由中间树 `dotnet build -c Release` + `dotnet test -c Release`（含字符序列、求值次数、代理项运行时断言）证明 .NET 半区等价。

明确不做：模式化 GetEnumerator / 显式 IEnumerator 适配（C40）、ref/ref readonly foreach（C40）、await foreach（C38）、实例 Deconstruct 族 lowering（开放问题 1）、任何 C#→Java 转换代码改动。

### 1.5 硬不变式

1. 输出用户代码中不存在「表达式静态类型为 `string` 的普通 foreach，且迭代表达式不是 `ToCharArray()` 调用」——residual 闸门按**语义模型**判定，不靠文本 grep；
2. 本 pass 不触碰任何带 `RefKeyword` 或 `AwaitKeyword` 的 foreach 节点（C40/C38 语法面）；闸门对这两类只计数、不判失败；
3. 本 pass 不 lower KV 族与实例 Deconstruct 族（E07 职责面/开放问题 1）；KV 残余只发顺序异常诊断；
4. 一切分类来自 SemanticModel（`GetTypeInfo`/`GetForEachStatementInfo`）；无模型、error type、dynamic 绑定节点一律原样透传；
5. 幂等：产物二次执行零 diff（已包裹节点的表达式类型变为 `char[]`，FS-1 不再命中）；干净文件字节级复制、BOM 保持；
6. 禁止注释/占位/原样透传流入 Java 产物（[catalog §4 风险 12 第一原则](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931)）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | string foreach 目标形态 | **`ToCharArray()` 直接包裹**：`foreach (char c in s)` → `foreach (char c in s.ToCharArray())`，只替换 `node.Expression`，不引临时变量 | catalog C16 明文规定（[L451](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L451-L451)）；转换器抛点自述「e.g. toCharArray()」（[Loops.cs:121](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L121-L121)）；Java `String.toCharArray()` 返回 `char[]`，enhanced-for 直接支持，且数组路径已被锁定不会误入 stream collect（[ArrayForeachNotCollectedTests.cs:49-50](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ArrayForeachNotCollectedTests.cs#L49-L50)）。索引循环（`for(int i…){char c=s[i];}`）为已评估备选：零数组分配，但须改造语句形态、插入循环变量、处理体为非块等复杂情况，且 Java 侧要改走 `charAt`，收益不抵风险（风险 1），v1 否决 |
| 2 | 求值次数 / 副作用 / null | 直接包裹即保证：`<expr>.ToCharArray()` 中 `<expr>` 作为单次方法调用的接收方**严格只求值一次**，与 foreach 规范「迭代表达式只求值一次、GetEnumerator 一次」同构；`null` 时两端均在首次解引用抛 NRE（`GetEnumerator`/`MoveNext` vs `ToCharArray`），时序等价 | 不生成 `var __tmp = expr` 临时变量：临时变量只在需要多次引用子表达式时才必要，此处一次调用已足够；fixture 以计数器方法断言求值一次（§7.2） |
| 3 | 字符与文化语义 | 不做任何文化处理：C# `string` 的 foreach 枚举的是 `char`（UTF-16 码元），`CharEnumerator` **文化不敏感**；Java `String.toCharArray()` 同样逐 UTF-16 码元复制。代理对（surrogate pair）两端都呈现为两个独立 `char`；`StringInfo` 文本元素枚举是另一 API，与 foreach 无关 | `System.String`→`java.lang.String`（[TypeMappings.json:4](file:///d:/code/cs2j/config/TypeMappings.json#L4-L4)）、`char`→`char`（[L65](file:///d:/code/cs2j/config/TypeMappings.json#L65-L65)）映射已在；`ToCharArray` 经转换器常规方法名 camelCase 派生为 `toCharArray()`，M5 端到端抽样逐字核对 |
| 4 | string 判定谓词 | 以**迭代表达式类型** `GetTypeInfo(expr).Type?.SpecialType == System_String` 为唯一判据（与转换器 [Loops.cs:118](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L118-L118) 完全同判据）；**不**以「元素类型为 char」判定 | 否则会误伤 `IEnumerable<char>`/`ICollection<char>` 等非 string 源（这些由 Iterable 路径正常处理，包裹反而破坏类型）。`string?` 可空引用不影响判据；dynamic 绑定（静态类型非 string）不命中 |
| 5 | 残余解构分类 | C16 自带最小分类器，复刻 E07 的四族 `KeyValuePair / Tuple / Deconstruct / Other`（移植 [ForeachDeconstructionRewriter.cs:112-172](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L112-L172) 判据），分类结果只驱动「lower / 诊断 / 透传」三分支；不为复用而改 E07 任何 internal 文件 | E07 的类是 internal 且被其适配器与 52 处测试间接锁定；复制约 60-80 行纯函数判据比跨 pass 共享 internal 更安全（决策 10）。两 pass 同判据 `GetForEachStatementInfo(node).ElementType`，一致性由共处单测钉死（风险 3） |
| 6 | Tuple 族残余目标形态（FS-2） | lower 为普通 foreach + 显式 `ItemN` 解包：`foreach (var __cs2jFsEntry in expr) { var x = __cs2jFsEntry.Item1; …body }`；designation 声明、TupleExpression 带类型、discard `_`、嵌套元组（中间变量 + `ItemN` 递归）整套移植 E07 已验证的解包机制（[L178-357](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L178-L357)，其中嵌套元组路径在 L275-L293） | E06 正常覆盖后该族残余理论趋近于零（§1.2），FS-2 是**防御性收口**；`ValueTuple.ItemN` 是公共字段，中间树必然可编译。若 M3 sdk 扫描证明残余会把未分发的 ValueTuple 使用点引入下游，则把 FS-2 从 lower 切换为显式 CS2J5420 Error（开放问题 2），切换只动一个规则类 |
| 7 | Other 族残余（FS-3） | v1 **显式诊断** CS2J5421（默认 Warning、`Strict` 下 Error），节点原样保留但本次预处理在 strict 下失败；消息携带元素类型显示名与「扩展方法 Deconstruct 不支持」提示 | 该族现实主源是扩展方法 Deconstruct（E07 自陈漏判，[L166-L171](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L166-L171)）；编译器式扩展方法归约需要 compilation 级候选 reduction，不属于「工作量极小」范围。显式诊断把 [Loops.cs:157-159](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L157-L159) 的迟到硬抛前移到 pass 站点；M2 是否实现归约由 sdk 命中率决定（开放问题 4） |
| 8 | 实例 Deconstruct 族边界 | **严格按任务范围不实现 lowering、不改写节点**；C16 仅对其追加 CS2J5423 Info/Warning 计数定位（默认 Info，strict Warning，不升级 Error） | E07 的 Info 声称「handled by converter」（[L64](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L64-L64)），但 [Loops.cs:157-159](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L157-L159) 无条件硬抛——两处证据矛盾，本任务不擅自扩界，登记开放问题 1 由评审拍板（候选 lower 形态仅一行 `__entry.Deconstruct(out T x, …)`，成本极低） |
| 9 | ref / await / dynamic / 无模型守卫 | 节点含 `RefKeyword` 或 `AwaitKeyword`：不访问、不诊断为失败，仅 Info 计数（CS2J5441/5442），归 C40/C38；语义模型缺失、元素类型为 error type、dynamic 绑定：原样透传 | 与 [catalog §3.3 控制流族](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L903-L903) 分工一致；await foreach 的源不可能静态为 string，但守卫仍统一前置，避免对 C38 语法面做任何假设 |
| 10 | Core 目录归属 | **新建 [src/CSharpToJava.Core/ForeachSource/](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachSource/)**，不扩展现有 `ForeachDeconstruction/` | ① pass 身份独立：注册表 id/中间目录/适配器/facade 均独立，C15 已立「新 pass→新目录」范式（`NumericBoundary/` 对 `UnsignedTypeRewriter/`）；② E07 是默认开稳定面，扩展其目录会让 C16/C40 三个任务共改热文件；③ C40 需要规则子目录所有权槽位（§4.1）；④ catalog「名称」字段虽写「扩展 ForeachDeconstruction」，但注册表已据 §3.3 把它登记为独立 stub，实现应跟随注册表。代价：分类/解包工具复制一份，C40 落地时再评估提取共享（风险 3） |
| 11 | 执行形态与解析选项 | **逐文件语义 pass + 标准 spine**（同 enum/E07），不重写 `ExecuteAsync`；无支撑库（`PassSupportLibrary.None`）；verify build/tests 双默认开；`DeleteBuildOutputsAfterRestore = true`（同 enum）；parse 选项用 `new CSharpParseOptions()`（**不**加 NETCOREAPP 符号） | 改写局部、确定，无跨文件改名；双 verify 是最严 .NET 半区守门。parse 选项刻意与 [ForeachDeconstructionPassAdapter.cs:40-41](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ForeachDeconstructionPassAdapter.cs#L40-L41) 对齐：两 pass 同链先后处理同一棵树，预处理符号假设必须一致 |
| 12 | 临时命名 | `__cs2jFsEntry` / `__cs2jFsInner`，FreshName 移植 E07 的方法/块作用域扫描（[L438-L474](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L438-L474)），计数器后缀避让重名 | 与 E07 的 `__entry` 区分，便于在链式中间树里辨认改写来源；作用域扫描保证不捕获用户已有标识符 |
| 13 | 规则接口与 C40 接缝 | M1 即引入 internal `IForeachSourceRule`（按站点分类 → Rewrite/诊断），rewriter 持有**有序**规则列表；C16 注册 StringSource / TupleResidual / OtherResidual 三条；C40 后续只新增 EnumeratorAdapter/RefForeach 规则文件并在适配器注册，不改 C16 规则体 | 见 §4.2 接口形状；规则注册集中在适配器一处，是 C40 唯一预期编辑点（与 C40 的同 pass 所有权约定，§11） |
| 14 | 诊断码段 | 新增 **CS2J54xx**：5400-5409 族共享、5410-5419 string、5420-5439 残余解构、5440-5449 闸门/ref/await 计数、**5450-5499 预留 C40** | 已核实占用：CS2J5001-5003 转换器校验、CS2J51xx member-desugar（[C11 §6](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L250-L250)）、52xx 非正式预留 C39、CS2J53xx numeric-boundary（[C15 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md#L239-L239)）；54xx 经 specs 全目录 grep 无占用。5450+ 与同日 C40 文档双向对齐（开放问题 3） |
| 15 | 默认关→开门槛 | 翻转即 Implemented 但 `defaultEnabled: false`；§8 门槛全过后单行翻 true | 同 C15/C02 范式；避免 [catalog §4 风险 10](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L929-L929) 的 opt-in 长期固化 |

---

## 3. 注册表与链位契约

### 3.1 现状（stub）

[PassStubCatalog.cs:102-111](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L102-L111) 已登记：

| 字段 | 值 |
|---|---|
| Id | `foreach-source`（常量 `PassStubCatalog.Ids.ForeachSource`，[L27](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L27-L27)） |
| DisplayName | `Foreach source rewrite` |
| IntermediateDirectoryName | `.cs2j-foreach-source-src` |
| Stage | `PassStage.ControlFlow` |
| Status | `Stub`（显式启用 → CS2J9001） |
| DefaultEnabled | `false` |
| RunAfterIds | `{ "foreach-deconstruction" }` |
| CatalogItems | `"C16, C40"` |
| Description | `Normalize foreach iteration sources and ref/ref-struct enumerators.` |

已全仓 grep 核实：`ForeachSource` / `foreach-source` 标识符除该文件外**零引用**，删除常量无下游改引负担。

### 3.2 链位与顺序锁

- **RunAfter 锚边保持 `{ foreach-deconstruction }` 不变**。拓扑意义：C16 必须晚于 E07（消费其 KV lower 产物、只收口残余），并经由 E07 的锚边 `{ tuple }`（[PassCatalog.cs:107](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L107-L107)）传递性晚于 E06——Tuple 残余判据依赖 E06 已先跑（[ForeachDeconstructionRewriter.cs:67-68](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L67-L68) 的同一顺序契约）。
- [PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133) 的拓扑排序以**声明序为决胜**（Kahn 风格、依赖满足即取声明最前者）。翻转描述符在 `Implemented` 表中**物理紧随 foreach-deconstruction 条目插入**（位于 strip-default 之前，[PassCatalog.cs:100-119](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L100-L119)），拓扑结果即：

  ```
  …, tuple, foreach-deconstruction, foreach-source, strip-default, ref-out-in,
     unsigned, generic, generic-deepen, record-readonly, array-method,
     make-readonly, iterator-lowering, eliminate-goto, java-keywords
  ```

  与 C15（numeric-boundary 插在 unsigned/generic 之间）行级不重叠（§11）。
- Stage 字段保持 `ControlFlow`（仅 `--list-passes` 信息面，执行序只看 RunAfter，[PassDescriptor.cs:5-7](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L5-L7)）。
- 默认关期间不进执行计划：无目录、无耗时、无链行为变化（[PassStubCatalogTests.cs:50-57](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L50-L57) 模式）。

### 3.3 翻转后的描述符字段

```
Id                       = "foreach-source"
Stage                    = PassStage.ControlFlow
Status                   = PassStatus.Implemented
DefaultEnabled           = false                     # 锚点翻转即 Implemented，但默认关；§8 门槛后单行翻 true
RunAfterIds              = { PassIds.ForeachDeconstruction }
NeedsSemanticModel       = true
SupportLibrary           = PassSupportLibrary.None
VerifyBuildByDefault     = true
VerifyTestsByDefault     = true
IntermediateDirectoryName= ".cs2j-foreach-source-src"
CatalogItems             = "C16"                     # 收窄：锚点只宣称本规则；C40 合入时改 "C16, C40"
Description              = "Lower foreach over string (ToCharArray) and residual deconstruction forms."
```

无支撑库：不经 `PassSupportLibrary` 注入/过滤/Maven 排除任何链路，Program.cs 库过滤三处零改动。

### 3.4 翻转文件清单

1. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：`PassIds` 增 `public const string ForeachSource = "foreach-source";`（紧随 [L14](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L14-L14)）；`Implemented` 表在 foreach-deconstruction 条目（L100-L108）之后插入 §3.3 描述符（双 verify 开、无 supportLibrary）。
2. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删除 foreach-source stub 条目（L102-L111）与 `Ids.ForeachSource` 常量（L27；已核实无外部引用）。
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)：
   - `CreateAdapter` 增 `PassIds.ForeachSource => new ForeachSourcePassAdapter(new ForeachSourceOptions(), config.Verbose, config.FilterUnsupportedProjects, config.CompatNamespacePrefixes, config.Strict)`（插在 foreach-deconstruction 分支之后，[L148-L152](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L148-L152)）；
   - `BuildLegacyToggles` 增 `[PassIds.ForeachSource] = opts.ForeachSource`（[L60-L80](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L60-L80)；[PassChainOrderTests.cs:169-182](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L169-L182) 要求 LockedChain 中除 generic-deepen 外每个 pass 都有 toggle）。
4. [Program.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs) `ConvertProjectOptions`（[L4632](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4632-L4632) 起）：增独立 get/set 属性 `[Option("foreach-source", Default = false, HelpText = "…")] public bool ForeachSource { get; set; }`（不使用 `!NoXxx` 反写形态，同 C15 方向）；preserved 目录与指纹令牌由描述符全集驱动，无需手写接线；`--list-passes` 自动显示。
5. 新增 [Adapters/ForeachSourcePassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（§5）。
6. 链序/注册表测试更新：
   - [PassChainOrderTests.cs:18-38](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L38) `LockedChain` 在 `ForeachDeconstruction` 与 `StripDefault` 之间插入 `PassIds.ForeachSource`；`EnabledByDefault`（L40-L55）**不变**（默认关）；`ChainFactory_creates_an_adapter_for_every_implemented_pass` 自动覆盖；
   - [PassStubCatalogTests.cs:24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24-L24) stub 总数断言 `23 → 22`；
   - [ProjectPreprocessorsConsistentTests.cs:129-132](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs#L129-L132) 追加 `PassCatalog.ImplementedById(PassIds.ForeachSource).IntermediateDirectoryName == ".cs2j-foreach-source-src"`（仿 generic-deepen 无门面类段）。
7. §8 门槛达成后单行翻 `defaultEnabled: true`（指纹令牌 `pass:foreach-source=false→true` 一次性失效，可接受，[catalog §4 风险 11](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L930-L930)）。
8. **CatalogItems 后续只追加不改写**：C40 合入时 `"C16" → "C16, C40"`，一行字符串差异属预期协调点（§11）。

---

## 4. Core 组件设计

### 4.1 目录与文件所有权总览

```
src\CSharpToJava.Core\ForeachSource\
  ForeachSourcePreprocessor.cs    # 对外入口：Preprocess(sourceCode, semanticModel, options) → ForeachSourceResult
  ForeachSourceModels.cs          # ForeachSourceOptions / Statistics / Diagnostic(Severity,Code,…) / Result
  ForeachSourceAnalyzer.cs        # 站点分类：string 源判定（决策 4）、解构四族分类（决策 5）、
                                  #   ref/await/dynamic/error-type/no-model 守卫、residual 重扫
  ForeachSourceRewriter.cs        # CSharpSyntaxRewriter：持有有序 IForeachSourceRule 列表，
                                  #   遍历 ForEachStatement / ForEachVariableStatement，聚合诊断与统计
  ForeachSourceSyntaxFacts.cs     # ToCharArray 调用构造、ItemN 成员访问、__cs2jFs* FreshName、
                                  #   designation/tuple-expr 解包（移植自 E07）、trivia 保持
  Rules\
    IForeachSourceRule.cs         # 规则接口 + ForeachSourceSite 上下文（§4.2）
    StringSourceRule.cs           # 【C16】FS-1
    TupleResidualRule.cs          # 【C16】FS-2
    OtherResidualRule.cs          # 【C16】FS-3（Other 诊断；Deconstruct/KV 计数诊断亦在此或同文件内分部）
    EnumeratorAdapter\            # 【C40 预留，本任务不创建文件】
    RefForeach\                   # 【C40 预留，本任务不创建文件】
```

所有权硬约束（为 C40 立）：C40 落地时只新增 `Rules/EnumeratorAdapter/`、`Rules/RefForeach/` 自有文件与自有测试，在适配器规则注册处追加两行；不修改 C16 三个规则类与 `ForeachSourceSyntaxFacts.cs` 的既有公开行为。共享件需要放宽时先扩根层并补 C16 回归（同 C11/C15 纪律）。**本任务不创建两个 C40 预留目录**（空目录无交付意义，仅在本文档登记所有权）。

### 4.2 规则接口形状（为 C40 预留）

```csharp
internal enum ForeachSourceKind { Plain, Variable }

internal sealed class ForeachSourceSite
{
    public required CommonForEachStatementSyntax Node { get; init; }   // 两种 foreach 共同基类
    public required SemanticModel Model { get; init; }
    public required ForeachSourceOptions Options { get; init; }
    public ITypeSymbol? ExpressionType { get; init; }                  // 迭代表达式类型
    public INamedTypeSymbol? ElementType { get; init; }               // GetForEachStatementInfo(...).ElementType
    public DeconstructionFamily? Family { get; init; }                 // KeyValuePair/Tuple/Deconstruct/Other/null
    public bool IsRef { get; init; }                                   // RefKeyword（含 ref readonly）
    public bool IsAwait { get; init; }                                 // AwaitKeyword
}

internal interface IForeachSourceRule
{
    /// <summary>null/true/false 三态：不匹配 / 命中并改写 / 命中但拒绝改写（仅诊断）。</summary>
    RuleDecision? TryHandle(ForeachSourceSite site, ForeachSourceRewriteContext ctx);
}
```

有序注册顺序（M1）：ref/await 守卫（内置于 rewriter 前置，不占规则位）→ `StringSourceRule`（仅 Plain + string 表达式）→ `TupleResidualRule`（仅 Variable + Tuple 族）→ `OtherResidualRule`（Variable 下 KV/Deconstruct/Other 的诊断分支）。C40 的 EnumeratorAdapterRule 注册在 TupleResidual 之后、RefForeachRule 最后；规则间互斥（首个命中终止）。

### 4.3 对外契约（与 E07 同形，逐文件语义）

- `ForeachSourcePreprocessor(…)`，`Preprocess(string sourceCode, SemanticModel? model, ForeachSourceOptions? options = null) → ForeachSourceResult { OutputCode, Changed, Diagnostics, Statistics }`；
- 廉价预检：语法树无任何 `CommonForEachStatementSyntax` 即返回原文（`FilesSkippedClean = 1`），与 [ForeachDeconstructionPreprocessor.cs:31-37](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionPreprocessor.cs#L31-L37) 同形；
- 改写后经 `AdhocWorkspace` + `Formatter.Format` 规范化生成节点 whitespace（同 E07 [L50-L53](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionPreprocessor.cs#L50-L53)）；
- 每文件独立遍历，不缓存跨编译状态，可直接被适配器逐文件调用。

### 4.4 重写规则全集（FS-x）

| 规则 | 输入 | 输出 | 要点/边界 |
|---|---|---|---|
| FS-1 string 源包裹 | `ForEachStatementSyntax`，无 ref/await，表达式静态类型 `System.String` | 同一 foreach，仅 `Expression` 换为 `InvocationExpression(MemberAccess(expr, "ToCharArray"), ArgumentList())` | 保留全部 trivia/循环变量声明/类型（`char` 或 `var` 均不动）；求值一次（决策 2）；`expr` 本身已是 `ToCharArray()` 调用时表达式类型为 `char[]`，结构性不重命中 → 幂等 |
| FS-2a Tuple 残余·designation | `foreach (var (x, y) in src)`，Tuple 族 | `foreach (var __cs2jFsEntry in src) { var x = __cs2jFsEntry.Item1; var y = __cs2jFsEntry.Item2; …body }` | 组件类型/后缀取 `INamedTypeSymbol.TupleElements`（移植 E07 `GetTupleComponents`，[L382-L393](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L382-L393)） |
| FS-2b Tuple 残余·TupleExpression | `foreach ((int x, var y) in src)`，Tuple 族 | 同上，声明类型用源码标注（`int x = …Item1`），`var` 标注用 `var` | 移植 `UnpackTupleArgument`（[L315-L357](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L315-L357)） |
| FS-2c 嵌套与 discard | 嵌套括号 designation、`_` 丢弃 | 嵌套层引入 `__cs2jFsInner` 中间变量后递归；`_` 不产生声明 | 移植 [L254-L310](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L254-L310)；嵌套 KV 子节点用 Key/Value（与 E07 同构）；嵌套 Deconstruct/Other 整体绑定 + Info |
| FS-3a Other 族残余 | Variable foreach，Other 族（扩展 Deconstruct/不可分类） | 节点不动；**CS2J5421**（默认 Warning，strict Error） | 消息含元素类型显示名；失败时不产生半改写文件（该文件 Changed=false 仍复制原文，诊断决定 pass 成败，同 enum residual 纪律） |
| FS-3b KV/Deconstruct 残余计数 | Variable foreach，KV 或实例 Deconstruct 族 | 节点不动；KV→CS2J5422 Warning（顺序异常：E07 应已 lower）；Deconstruct→CS2J5423 Info（strict Warning） | 不改写（不变式 3）；仅把迟到信息前移到本 pass 输出 |
| FS-4 守卫 | ref/await 关键字；无模型；error/dynamic 元素 | 节点不动 | ref→CS2J5441 Info、await→CS2J5442 Info（仅 verbose，归 C40/C38）；无模型/error/dynamic 静默透传并计数 `SitesPassedThrough` |
| FS-5 residual 闸门 | 输出树重扫（适配器 `AfterFilesAsync` 聚合，跨文件） | 仍有裸 string foreach→CS2J5440；仍有 Tuple/Other 残余解构→CS2J5440 分桶计数 | 默认 Warning、strict Error（同 enum `ResidualCount` 范式，[EnumPassAdapter.cs:79-92](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs#L79-L92)）；ref/await/KV/Deconstruct 计数不计入闸门失败 |

### 4.5 Options / Statistics / Diagnostics 模型

- `ForeachSourceOptions`：`Strict = false`（Warning→Error 升级位，语义同 E07 `ForeachDeconstructionOptions.Strict`，[ForeachDeconstructionOptions.cs:8-9](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionOptions.cs#L8-L9)）、`ReportInfo = true`；v1 无 overrides JSON。
- `ForeachSourceStatistics`：通用五字段（`FilesScanned/FilesTransformed/FilesSkippedClean` + 本 pass 字段）+ `StringForeachWrapped`、`TupleResidualLowered`、`OtherResidualDiagnosed`、`KvResidualSeen`、`DeconstructResidualSeen`、`RefForeachSeen`、`AwaitForeachSeen`、`SitesPassedThrough`、`ResidualStringSites`、`ResidualDeconstructionSites`、`ErrorsByCode: Dictionary<string,int>`。
- `ForeachSourceDiagnostic(Severity, Code, Message, FilePath?, Line?)`：severity 三档 Info/Warning/Error；Code 为 `FS-1/FS-2/…` 规则号，外层打印 CS2J54xx 码（§6）。

---

## 5. CLI 适配器与集成

### 5.1 ForeachSourcePassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/ForeachSourcePassAdapter.cs`（预计 130-180 行），**走标准 spine**（不重写 `ExecuteAsync`），形态参照 [ForeachDeconstructionPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ForeachDeconstructionPassAdapter.cs) + [EnumPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs)：

- `Descriptor => PassCatalog.ImplementedById(PassIds.ForeachSource)`；构造参数 `(ForeachSourceOptions, verbose, filterUnsupportedProjects, compatNamespacePrefixes, strict)`；
- `CreateParseOptions` 覆写为 `new CSharpParseOptions()`（决策 11，与 E07 一致）；
- `VerifyBuild/VerifyTests` 取 descriptor 双开（不在构造器透传，同 enum 最简形态）；`DeleteBuildOutputsAfterRestore => true`；
- `RewriteFileAsync`：调 `_preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options)`，合并统计；诊断消息前缀相对路径（同 E07 适配器 [L52-L55](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ForeachDeconstructionPassAdapter.cs#L52-L55)）；changed 时返回 `PassFileRewrite { OutputCode, Changed = true, PreserveBom = true }`；verbose 打印 transformed/clean；
- `AfterFilesAsync`：FS-5 跨文件 residual 聚合——本适配器在逐文件回调中缓存各输出树的残留计数，结束后按 strict 决定 CS2J5440 级别（不重新 parse 磁盘：以 rewriter 输出的 residual 统计为准，避免二次编译；语义正确性已由 spine 的 verify build 兜底）；
- `OnRestoreFailed`→CS2J5402、`OnBuildFailed`→CS2J5403、`OnTestsFailed`→CS2J5404（spine 在 [ProjectPassRunner.cs:231-350](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L231-L350) 自动调用）；
- `HasErrors`：Error 级诊断（含 strict 升级）即真；
- 摘要行：`foreach-source: T transformed, C clean, S string site(s) wrapped, R tuple residual(s) lowered, D residual diagnostic(s), E error(s)`；
- 无 `OnTreePreparedAsync`/`AfterFilesAsync` 支撑库动作（`PassSupportLibrary.None`）。

### 5.2 verify、指纹与入口面

- verify：双默认开（最严档），verify 后清 bin；
- 指纹：随描述符全集令牌自动纳管；`.cs2j-foreach-source-src` 由实际执行集合自动 preserve；禁用时零痕迹（无目录、无耗时）；
- 入口面：convert-project `--foreach-source`（默认 false）+ 通用 `--enable-pass foreach-source` / `--disable-pass`；独立运行走通用 `preprocess --pass foreach-source -s … -d …`，**不新建独立 verb**（E07 有历史 verb `ForeachDeconstructionVerbOptions`，新 pass 无历史包袱，同 C15/C02 方向）；
- 单文件模式：FS-1/FS-2 均为自包含改写（不引支撑库），通用 verb 单文件编译即可处理；`ValueTuple` 所需引用在基础引用集内（[ForeachDeconstructionTests.cs:412-421](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs#L412-L421) 已含 ITuple/ValueTuple 程序集），不设 CS2J5409 闸门。

---

## 6. 诊断策略

MSBuild 格式：`文件(行,列): warning/error CS2J54xx: [FS-x] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5402 | Error | 中间树 `dotnet restore` 失败 |
| CS2J5403 | Error | 中间树 `dotnet build` 失败（含改写引入的编译错误——标准 spine 下以 verify build 兜底，不做逐站点回退） |
| CS2J5404 | Error | 中间树 `dotnet test` 失败 |
| CS2J5410 | Info | string foreach 已包裹 `ToCharArray()`（聚合计数，仅 verbose） |
| CS2J5420 | Error | （预留/条件启用）Tuple 残余 lower 被判定不安全时的显式失败，开放问题 2 决定 M2 是否启用 |
| CS2J5421 | Warning（strict 即 Error） | Other 族解构残余无法归一（扩展方法 Deconstruct 等） |
| CS2J5422 | Warning | KV 族解构残余（顺序异常：foreach-deconstruction 应已处理） |
| CS2J5423 | Info（strict 即 Warning） | 实例 Deconstruct 族残余（现状由转换器拒绝；见开放问题 1） |
| CS2J5440 | Warning（strict 即 Error） | residual 闸门：输出树仍有裸 string foreach 或 Tuple/Other 解构残余（FS-5） |
| CS2J5441 | Info | `ref`/`ref readonly` foreach 未处理站点计数（归 C40，仅 verbose） |
| CS2J5442 | Info | `await foreach` 未处理站点计数（归 C38，仅 verbose） |
| CS2J5450-5479 | — | **C40 EnumeratorAdapter 预留** |
| CS2J5480-5499 | — | **C40 RefForeach 预留** |

原则（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931)）：无法在 C# 侧等价表达时显式报错，禁止注释/占位流入 Java。转换器侧两个抛点（[Loops.cs:118-122](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L118-L122)、[L157-L159](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L157-L159)）在本 pass 默认开后对「经预处理的项目」成为不可达路径；其删除/收紧为后续 PR（同 C15 §6 模式），直连 ConversionPipeline 的单文件入口保留它们作安全网。

---

## 7. 测试计划

### 7.1 Core 单测矩阵

新增 `tests/CSharpToJava.ProjectTests/ForeachSourceRewriterUnitTests.cs`（仿 [ForeachDeconstructionTests.cs:409-429](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs#L409-L429) 的内联源码→`CSharpCompilation`→Preprocess 形态，并按 C15 范式补输出可编译断言）：

1. **FS-1 正例**：`foreach (char c in s)`（参数/字段/属性/方法返回值/字符串字面量/拼接表达式）→ 文本含 `in s.ToCharArray()`、循环变量与类型零变化；`foreach (var c in GetText())` 同样包裹；
2. **FS-1 零误伤**：`IEnumerable<char>`/`char[]`/`List<char>`/`string[]` 源的 foreach → Changed=false；非泛型集合路径不变；
3. **幂等**：包裹后产物二次 Preprocess 零 diff；干净文件字节级一致；
4. **守卫**：`await foreach`、`foreach (ref var x in …)`（以语法夹具构造）→ 节点不动、计数 CS2J5442/5441；无模型（Preprocess 传 null）→ 不抛异常、原样返回；
5. **FS-2**：直接构造元素为 ValueTuple 的 `ForEachVariableStatement`（绕过 E06 的夹具源码）：`var (x, y)`、`(int x, string y)`、discard `(int x, _)`、嵌套 `var (a, (b, c))` → 断言 `__cs2jFsEntry.Item1/Item2`、`__cs2jFsInner`、声明类型与作用域；输出无语法错误且可编译；
6. **FS-3**：带扩展方法 `Deconstruct` 的元素类型 → CS2J5421（非 strict 为 Warning、节点仍在）；strict 下诊断升级 Error；KV 残余夹具 → CS2J5422；实例 Deconstruct 元素 → CS2J5423 且不改写；
7. **链式共处**：同一 compilation 上先跑 E07 再跑 C16（或直接串两个 Preprocessor）：KV 已被 E07 lower、C16 统计 `KvResidualSeen=0`；E06+E07+C16 三 pass 串联的正常 tuple foreach 在 C16 阶段无 ForEachVariable 可处理。

### 7.2 项目级 fixture 与运行时行为测试

新增 `tests/SampleForeachSourceSolution/`（`SampleForeachSourceLib` + `SampleForeachSourceApp` + `SampleForeachSourceTests` + `.sln`，net10.0；`Content Include="..\SampleForeachSourceSolution\**\*.*"` 进 [CSharpToJava.ProjectTests.csproj](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj)，仿 SampleUnsignedSolution 接线）：

- Lib：string 参数/字段/方法链/字面量上的 foreach；循环体内拼接结果字符串；嵌套 foreach（外层集合、内层 string）；代理对字符串 `"a😊b"` 的逐码元计数；
- Tests（**.NET 半区守门**）：xunit 断言——各 foreach 收集到的字符序列与 `s.ToCharArray()` 逐元素一致；一个带副作用计数器的 `GetText()` 在单次 foreach 中**只被调用一次**（决策 2）；`"a😊b"` 枚举得到 4 个 UTF-16 码元（决策 3）；嵌套场景内外层计数正确；
- 项目级测试 `ForeachSourceProjectTests.cs`：`result.Success`、统计字段、中间树文本断言（foreach 处含 `.ToCharArray()`）、**中间树 `dotnet build` 与 `dotnet test -c Release` 退出码 0**、干净工程字节复制与 BOM 保持、二次运行幂等。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三个工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests；
- 既有回归不修改即保持全绿：[ForeachDeconstructionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs)、[ProjectForeachDeconstructionPreprocessorTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectForeachDeconstructionPreprocessorTests.cs)、[ArrayForeachNotCollectedTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ArrayForeachNotCollectedTests.cs)、[ForeachNonGenericCollectionCastTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachNonGenericCollectionCastTests.cs)（本任务不改 E07 与转换器）。

### 7.4 Java 半区冒烟（默认开门禁之一，非阻塞锚点合入）

- string foreach 代表性样本经完整 C#→Java 链路（先跑 `foreach-source` 再转换）：断言产物含 `for (char c : <e>.toCharArray())`、可编译；代理对样本双端字符序列一致；
- 证实 [Loops.cs:118-122](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L118-L122) 抛点在预处理路径上不可达（删除留后续 PR）；
- 数组/集合 foreach 既有行为零变化（复用 §7.3 回归）。

### 7.5 residual 闸门

1. 语义扫描型测试：对输出中间树所有用户 .cs 文件建 compilation，断言零「string 表达式的裸 foreach」与零 Tuple/Other 族 `ForEachVariableStatementSyntax`；违例即 CS2J5440，strict 退出码非零（FS-5）；
2. 豁免/分离断言：ref/await 计数存在时闸门仍通过（C40/C38 面）；KV/Deconstruct 计数单独列出且不触发失败；
3. 上游产物回归：对含 E06 tuple 解构、E07 KV 解构的既有样例启用本 pass，中间树 build/test 全绿，E06/E07 统计零变化（C16 只处理残余）。

### 7.6 CLI / 一致性 / 链序

- 默认关：convert-project 不产生 `.cs2j-foreach-source-src`、无耗时、链序不变；`--enable-pass foreach-source` 与 `--foreach-source` 等价；`--list-passes` 显示 Implemented/默认关/目录/说明/`C16` catalog 项；CS2J9001 不再对本 id 触发；preserved 目录注册；
- 新增 `ForeachSourceCLITests.cs`（ProjectTests，仿 PreprocessVerbCLITests/JavaKeywordCLITests）：通用 `preprocess --pass foreach-source` 单文件/目录模式、strict 退出码、诊断落盘格式；
- [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 插位 + [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) stub 数 23→22 + [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 目录名断言，按 §3.4-6 一次改到位。

---

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足：

1. slnx `dotnet build -c Release` 全绿；三个测试工程 `dotnet test -c Release` 全绿（含 §7 全部新增单测/fixture/CLI/链序/residual）。
2. fixture sln 中间树 build + test 全绿；幂等、字节复制、BOM、residual 闸门全绿；字符序列/求值次数/代理对运行时断言（§7.2）全绿。
3. **Java 半区就绪**：§7.4 冒烟通过——string 样本经完整链路产出可编译 Java 且含 `toCharArray()`；转换器 string 抛点在预处理路径证实不可达。
4. csharp-sdk（只读训练集）启用扫描：string foreach 总量与包裹率、ForEachVariable 残余按四族分桶（KV/Tuple/Deconstruct/Other）、CS2J5421-5423/5440 命中清单；**每条 Warning/Error 人工定性**（真不支持 vs 分析器过保守），作为开放问题 1/2/4 的决策输入。
5. sdk 基线不退化：启用后既有 fixture 与 sdk 抽样工程中间树 build/test 不劣于未启用；E06/E07 统计零变化；convert-project 总耗时增量可接受（string 站点改写为常数级语法替换，verify 串行成本纳入指纹缓存评估，[catalog §4 风险 11](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L930-L930)）。
6. `--list-passes` 描述、README/ROADMAP 条目、本设计与 plan 文档齐备；与 C40 文档的码段/文件所有权约定完成双向核对。

未达门槛前长期保持默认关，每个评审周期复跑第 4/5 项。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 锚点基建 + FS-1 string | `ForeachSource/` 骨架（Models/Analyzer/SyntaxFacts/Preprocessor）+ `Rules/IForeachSourceRule` + StringSourceRule + 适配器 + 注册表翻转（默认关、verify 双开）+ CLI 开关/目录/指纹接线 + 链序三件套测试；string foreach 端到端跑通（含 §7.1-1/2/3/4 与 fixture string 部分） | Core ~300 行，CLI ~180 行 |
| M2 残余解构收口 + 闸门 | TupleResidualRule（FS-2a/b/c）、OtherResidualRule（FS-3a/b）、FS-4 守卫诊断、FS-5 residual 闸门（含 strict 升级）+ fixture 代理对/求值次数断言补齐 + §7.1-5/6/7 单测 + Release build/test 全绿 | Core ~250 行 + fixture |
| M3 默认开评审 | sdk 扫描与分桶报表（§8-4）、Java 半区冒烟（§7.4）、转换器消费核对（§6 后续 PR 底稿）、与 C40 文档的接口/码段对齐、本设计定稿 + plan 文档；门槛通过后单行翻默认开；开放问题 1/2/4 据扫描数据拍板 | 文档/数据 |

---

## 10. 风险

1. **ToCharArray 的分配开销**：每次进入循环分配一个 `char[]`（C# 侧 `ToCharArray` 与 Java 侧 `toCharArray` 均如此）；C# 原生 CharEnumerator 也有枚举器分配，数量级相近，但紧热点上数组拷贝略重。缓解：string 不可变，语义上无任何差异；fixture 不设微基准阈值；若 sdk 扫描显示热循环高命中，M3 可经 options 增加「索引循环」形态（决策 1 已留备选），不影响默认形态。
2. **Tuple 残余的下游可转换性**：FS-2 产出的 `__entry.Item1` 若对应未被 E06 分发的 ValueTuple 使用点，中间树 build 可过但转换器侧可能缺生成结构体映射。缓解：该残余在正常链路上理论趋近于零（E06 [L1312-L1361](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L1312-L1361) + E07 双重收口）；verify build 双开守门；§8-4 的 sdk 分桶给出真实命中，必要时 M2 启用 CS2J5420 改显式失败（开放问题 2）。
3. **两份分类器漂移**：C16 与 E07 各自持有 KV/Tuple/Deconstruct 判据副本，未来一侧修改后可能分歧。缓解：判据严格同源（同 Roslyn API、同字符串/接口判定），§7.1-7 共处单测锁定；C40 落地时优先把分类器提取为 pass 间共享 internal 工具（届时补两侧回归）。
4. **E07 info 消息与转换器行为矛盾**：[ForeachDeconstructionRewriter.cs:64](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L64-L64) 称 Deconstruct「handled by converter」，[Loops.cs:157-159](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L157-L159) 实则硬抛。缓解：本任务不掩盖矛盾（CS2J5423 把站点显式化），以开放问题 1 推动评审，默认关期间不改变任何现状成败。
5. **格式化/trivia 回归**：`ToCharArray()` 包裹与解包语句生成依赖 `Formatter.Format` 与 trivia 手工搬迁（E07 同类机制）。缓解：幂等测试 + BOM 保持断言 + 嵌套/非块循环体夹具（体为单语句时 PrependStatements 包块，移植 [L424-L436](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L424-L436)）。
6. **扩展方法 Deconstruct 漏判的语义错觉**：FS-3 只诊断不 lower，strict 关闭时产物仍会在转换器侧失败——不是回归（现状同样失败），但用户体验是「Warning 后仍失败」。缓解：消息明确指引启用 strict 或等待归约支持；文档与 CLI help 写清；M2 依命中率决定是否实现（开放问题 4）。

---

## 11. 并行协调

- **C40（同 pass 扩展，同日并行设计）**：
  - **所有权约定**：C40 只新增 `Rules/EnumeratorAdapter/`、`Rules/RefForeach/` 规则文件、自有测试/fixture 片段与 CS2J5450-5499 码位；在 `ForeachSourcePassAdapter` 的规则注册列表追加两行是其唯一预期适配器编辑点；`catalogItems "C16" → "C16, C40"`、Description 更新由 C40 提交完成；`IForeachSourceRule`/`ForeachSourceSite` 形状以本文档 §4.2 为基线，C40 文档若需加字段走「扩根层 + 补 C16 回归」；
  - **插入点共识**：EnumeratorAdapter 规则注册在 TupleResidual 之后，RefForeach 最后；string/数组/集合已由 C16 与转换器覆盖，C40 不再判定这些源；
  - 双向待对齐项：诊断码段 5450-5499 的细分（开放问题 3）、规则接口是否需要 `IEnumeratorSymbol` 便利属性。
- **E06（tuple）/ E07（foreach-deconstruction，已实现）**：RunAfter 锚点锁死传递顺序（tuple → foreach-deconstruction → foreach-source）；C16 不修改这两个 pass 任何文件；E07 产物中的 `__entry` 与 C16 的 `__cs2jFsEntry` 在同一中间树可共存（FreshName 作用域扫描各自避让）；§7.1-7 共处单测为硬回归项。
- **C38（await foreach，async-normalize 族）**：C16 对 AwaitKeyword 节点零接触（FS-4）；C38 与 C40 共享 foreach 插入点（catalog C40 [L750](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L750-L750)），C16 的 residual 闸门把 await 残余单列为 CS2J5442 计数，不替 C38 宣告完成。
- **转换器侧**：本任务不改 [StatementTransformer.Loops.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs)；默认开并稳定后，由后续 PR 删除/收紧两个抛点并把直连单文件路径改为明确的「未预处理」错误（同 C15 §6 模式）。
- **热文件冲突解法**（同期还有 C15 等锚点翻转任务）：
  - `PassStubCatalog.cs`：各任务只删自己那一条 stub 与常量；若合并序导致 [PassStubCatalogTests.cs:24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24-L24) 的 stub 总数断言连锁（C15 与 C16 各自写 23→22），**后合入方负责更新为 21**，不留红灯；
  - `PassCatalog.cs`/`PassChainFactory.cs`/`Program.cs`：只做行级追加（描述符块、switch 分支、toggle 行、Option 属性），不修改他人条目；C16 插位（foreach-deconstruction 与 strip-default 之间）与 C15 插位（unsigned 与 generic 之间）行级不重叠；
  - `PassChainOrderTests.cs`：LockedChain 插点互不相同；EnabledByDefault 两家都不改；
  - `catalogItems` 字符串：C40 合入时一行追加，属预期冲突；
  - 本任务全部产出位于新目录 `ForeachSource/`、新适配器文件、新测试文件，与 C15 的 `NumericBoundary/` 零文件重叠。

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c16-foreach-complete`
- 目录：`.worktrees/preproc-c16-foreach-complete`
- base：`953293b9`（`docs: update doc base commit`，已用只读 `git rev-parse` 核实存在，且为当前工作区 HEAD）
- 创建命令（在仓库根、PowerShell 下执行；**本设计阶段不执行，实施阶段由执行者运行**）：

```powershell
git worktree add .worktrees/preproc-c16-foreach-complete -b feature/preproc-c16-foreach-complete 953293b9
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `ForeachSource/`（含 Rules 接口与三条 C16 规则）、CLI 单适配器 + 注册表翻转（§3.4 清单）、本设计文档与后续 plan 文档（`docs/superpowers/plans/2026-09-29-c16-foreach-complete.md`）、Core 单测 + fixture sln + Release build/test + residual 闸门 + CLI/链序测试；
- 不新建支撑工程；不触转换器代码；不创建 git 标签、不做任何 push（实施阶段仍以任务授权为准）。

---

## 附：关键开放问题（供评审拍板）

1. **实例 Deconstruct 族的矛盾处置**：E07 Info 称「handled by converter」（[ForeachDeconstructionRewriter.cs:64](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L64-L64)），但 [Loops.cs:157-159](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L157-L159) 对所有解构 foreach 无条件硬抛。C16 按授权范围不实现其 lowering（仅 CS2J5423 定位）。是否在 M2 顺手收编——目标形态极简：`foreach (var __e in src) { __e.Deconstruct(out T1 x, out T2 y); … }`（out 变量声明、重载/嵌套委托给 C# 编译器语义）——还是坚持归 C40/转换器侧 PR？
2. **Tuple 残余（FS-2）应 lower 还是显式失败**：E06 已覆盖全部 `IsTupleType` + 两种合法变量形态，正常链路残余趋近于零；防御性 `ItemN` lower 可能引入未经 E06 分发的 ValueTuple 使用点（风险 2）。建议 M1/M2 先以诊断计数观察 sdk 真实命中，M3 据数据在「FS-2 lower（现状设计）」与「CS2J5420 显式失败」之间定稿。
3. **CS2J5450-5499 与 C40 码段细分**：本设计按 EnumeratorAdapter（5450-5479）/ RefForeach（5480-5499）预留，需与同日 C40 设计文档双向确认；若 C40 需要更多码位，调整只影响本文件 §6/§11 与 C40 文档，不动实现。
4. **扩展方法 Deconstruct 的归约深度**：v1 仅 CS2J5421 显式诊断；若 §8-4 sdk 扫描显示高命中，M2 是否实现编译级候选 reduction（按 C# 规约在 using/静态 using 范围内查找 `Deconstruct` 扩展并 lower 为静态调用）？该实现复杂度与收益需实测支撑。
5. **是否需要索引循环形态选项**：FS-1 统一 `ToCharArray()` 有一次数组分配；若热点扫描命中显著，是否增加 `ForeachSourceOptions.StringForm = ToCharArray | IndexLoop`（后者产 `for (int __i = 0; __i < s.Length; __i++) { char c = s[__i]; … }`，Java 侧走 `length()/charAt()`）？默认形态不变。
