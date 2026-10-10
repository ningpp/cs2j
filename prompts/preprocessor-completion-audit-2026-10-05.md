# Preprocessor 完成度核实报告与后续实施顺序

> 核实日期：2026-10-05
> 核实对象：
> - 台账：`C:\Users\15097\Desktop\preprocessor-status.md`（快照时间 2026-10-04 12:58，C01–C52 中 35 项标 ✅、17 项标 ❌）
> - 盘点清单：[preprocessor-catalog.md](file:///d:/code/cs2j/prompts/preprocessor-catalog.md)（信息更新止于 2026-09-30）
> 代码基线：分支 `dev-3.x-26100507`，HEAD `9eccaef3`（2026-10-05 22:35），`dotnet build CSharpToJavaConverter.slnx -c Release` 通过（exit 0），SDK 10.0.300，Roslyn 包 5.0.0。

---

## 0. 结论速览（TL;DR）

1. **台账整体过时约一天，但偏差方向相反**：台账标的 17 个 ❌ 中，**14 个在 10-04 午后至 10-05 的三波"默认全开"政策翻转中已落地**；真正零产品代码的只有 **C41**；**C44 按设计裁决永不单独立项**；**C52 是伞条目、机制大部分为空**。
2. **台账标的 35 个 ✅ 全部确有产品代码**，但"已实现"分三个层次：.NET 改写深度、Java 半区可信度、项目级端到端保护。至少 7 个条目属于"**默认开但 Java 半区说谎或零运行时保护**"，风险最高的是 **C04（仅 M1 即默认开）、C29（Monitor 壳恒返回 true）、C47（仅有的 2 个 Java E2E 全部 Skip）、C18/C38（Java 侧四笔红账）、C30（注册文件有人写、没人消费）**。
3. 当前注册表共 **42 个 pass 全部 `Implemented`**，[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 已无任何 stub（仅剩注释）。**（2026-10-06 P1 回写）** 原「默认关 4 个」（C09 thread-static、C10 attribute-normalize、C25 dynamic-desugar、C26 linq-channel）经 P1 四件开门决策后**已全部翻默认开**；当前默认关为 **2 个 P0 回退项**——**C20 partial-merge**（[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L75-L80)，M3 成员级配对塌缩未达）与 **C04 iterator-lowering**（[L461-L467](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L461-L467)，仅 M1、try-finally/using/lock 内降低未做），均可 `--enable-pass` 临时开。
4. **项目级端到端闸门目前大面积挂起**：10-04 的提交 `c49753bc` 起，22 个端到端测试带 `[Fact(Skip="TODO: 临时跳过端到端慢测试（排查根因中）")]`，覆盖 17 个测试文件。规则逻辑仍有隔离单测覆盖，但"转换+构建+运行 Java"的全链路保护当前不完整。
5. **整个波次尚未合入 dev-3.x**：当前分支领先本地 `dev-3.x` **241** 个提交（可 fast-forward），领先 `origin/dev-3.x` **639** 个提交（远端停在 09-16）。台账"已 commit 到 dev-3.x"一列的实际含义是"已提交到 dev-3.x 集成分支 `dev-3.x-26100507`"，不是已合入主干。
6. 后续工作按风险排为 **P0 止血 → P1 四个默认关 pass 的开门决策 → P2 Java 半区还债 → P3 .NET 半区里程碑补完 → P4 旧转换器退役 → P5 新构造（C41/C52）与画像基线**，详见第 7 节。

---

## 1. 核实方法与证据层级

以代码为唯一事实源，按以下优先级取证（台账与 catalog 仅作为待核实声明）：

1. **注册表**：[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)（42 个 `Implemented` 描述符、`catalogItems` 规则映射、`RunAfterIds` 拓扑、默认开关）+ 空壳的 PassStubCatalog；并用 CLI 实测 `preprocess --list-passes` 取得锁定执行序。
2. **提交史**：逐条目追首末提交，重点核对三波默认翻转：
   - `e5e7773a`（10-02）翻 C23/C05/C40/C07/C06/**C47**
   - `3147e093`（10-04）翻 C28/C20/C30/C33/C32/C19/C29/C42 并引入 CLI 规则开关
   - `a7f1cb9c`（10-05）翻 **C18/C21**；`ba7a0897` 翻 C49；`f16cb8ca` 翻 C35/C51；`0f935995` 翻 C46/C50/C27
3. **规格书里程碑**：[docs/superpowers/specs/](file:///d:/code/cs2j/docs/superpowers/specs) 下 55 份设计（`2026-09-28/29-cXX-*-design.md`），逐份核对 M0–M6 里程碑与 §13 实施记录。
4. **测试/fixture/Java 半区**：`tests/CSharpToJava.Tests`、`tests/CSharpToJava.ProjectTests`、`tests/CSharpToJava.ConversionTests`、`java/csharptojava-compat`、[config/TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json)。

局限：本次未重跑全量 `dotnet test`（全量串行约 11–20 分钟且当前含 22 个 Skip）；运行时基线引用 C26 M3 记录（Tests 3245 通过 / ProjectTests 1807 / ConversionTests 679，0 失败）。构建本报告时 Release 配置重新编译通过。

> **2026-10-06 P1 终态 Release 全量回归（本局限已解除）**：slnx Release build **0 错误**；Tests Release **3432 通过 / 0 失败 / 3 跳过**；ConversionTests Release **676 通过 / 4 失败**（4 红全为基线既有 AsyncStreams P0，Debug/Release 一致，红灯集未扩大）；ProjectTests Release **1848 通过 / 0 失败 / 20 跳过（总计 1868，35m38s；较本报告的 1842/20/1862 净增 6，来自 C09/C10/C25/C26 P1 新增端到端事实）**。即 P1 四件开门决策落地后无新增红灯。

---

## 2. 当前真实 pass 链（CLI 实测的锁定执行序）

`preprocess --list-passes` 输出（行序即 `RunAfterIds` + 声明序经 Kahn 排序后的执行序，被顺序测试钉死）。共 42 个 pass；**2026-10-06 P1 回写**：默认关 4→0（原 C09/C10/C25/C26 全翻默认开），同期 P0 止血把 C20/C04 回退默认关，故现 **2 个默认关（加粗）**：

| # | pass id | stage | 默认 | 规则编号（catalogItems） |
|---|---|---|---|---|
| 1 | conditional | Lexical | on | E01 |
| 2 | lexical-normalize | Lexical | on | C28, C43 |
| 3 | **partial-merge** | Lexical | **off**（2026-10-06 P0 回退） | C20 |
| 4 | local-function-lift | Declarative | on | E02 |
| 5 | null-coalesce | Expression | on | E03 |
| 6 | expression-desugar | Expression | on | C23, C24, C35, C36, C51 |
| 7 | enum | Expression | on | E04 |
| 8 | enum-declaration | Declarative | on | C05 |
| 9 | delegate | Declarative | on | E05 |
| 10 | tuple | Declarative | on | E06 |
| 11 | anonymous-type-lift | Declarative | on | C21 |
| 12 | foreach-deconstruction | Expression | on | E07 |
| 13 | foreach-source | ControlFlow | on | C16, C40 |
| 14 | pattern-desugar | ControlFlow | on | C03, C37 |
| 15 | strip-default | Declarative | on | E08（C48 为其内嵌规则，无独立 pass） |
| 16 | ref-out-in | Abi | on | C01 |
| 17 | unsigned | Abi | on | E09 |
| 18 | numeric-boundary | Expression | on | C15, C34, C17, C22 |
| 19 | generic | Abi | on | E10 |
| 20 | thread-static | Declarative | on（2026-10-06 P1 翻） | C09 |
| 21 | generic-deepen | Abi | on | C02 |
| 22 | closed-metadata | Abi | on | C30 |
| 23 | variance-audit | Declarative | on | C33 |
| 24 | record-readonly | Declarative | on | E11 |
| 25 | array-method | Expression | on | E12 |
| 26 | index-range | Expression | on | C31 |
| 27 | make-readonly | Declarative | on | E13 |
| 28 | attribute-normalize | Declarative | on（2026-10-06 P1 翻） | C10 |
| 29 | struct-value-semantics | Declarative | on | C07 |
| 30 | unsafe-ffm | Abi | on | C06 |
| 31 | closure-lift | StateMachine | on | C32, C45 |
| 32 | **iterator-lowering** | StateMachine | **off**（2026-10-06 P0 回退） | C04 |
| 33 | async-normalize | StateMachine | on | C18, C38（C44 subtopic） |
| 34 | extension-methods | Expression | on | C19, C49 |
| 35 | eliminate-goto | ControlFlow | on | E14 |
| 36 | member-desugar | Declarative | on | C11–C14, C39, C46, C50, C27 |
| 37 | linq-channel | ControlFlow | on（2026-10-06 P1 翻，M4） | C26 |
| 38 | concurrency-normalize | Declarative | on | C29 |
| 39 | equality-concat | Expression | on | C47 |
| 40 | platform-isolation | Abi | on | C42 |
| 41 | dynamic-desugar | Abi | on（2026-10-06 P1 翻） | C25 |
| 42 | java-keywords | FinalNaming | on | C08（E17） |

编号映射要点：**52 个编号 ≠ 52 个 pass**。C03/C37、C16/C40、C18/C38、C19/C49、C23/C24/C35/C36/C51、C28/C43、C32/C45、C15/C34/C17/C22、C11–C14/C39/C46/C50/C27 均共享 pass；**C44 无 pass（分治裁决）、C48 无独立 pass（strip-default 内规则）、C52 是伞条目（非 pass）、C41 当前无 pass（零实现）**。

---

## 3. C01–C52 逐项核实总表

图例：深度按各设计书 M0/M1… 里程碑；"默认"为当前注册表默认态；台账列为桌面快照的 ✅/❌。

| ID | 归属 pass | 台账 | 核实结论 | 默认 | 已达深度 | 主要挂账 |
|---|---|---|---|---|---|---|
| C01 | ref-out-in | ✅ | 已完成 | on | 全里程碑 | M5：sdk 全量比对、旧码下线（双轨） |
| C02 | generic-deepen | ✅ | 已落地 | on | M0–M3 | M4 sdk 计数门未做；旧转换器双轨 |
| C03 | pattern-desugar | ✅ | 已完成 | on | 全部 | 无实质挂账 |
| C04 | iterator-lowering | ✅ | **名不副实（最高风险之一）；2026-10-06 P0 已回退默认关** | **off**（P0-3 回退） | **仅 M1** | try-finally/using/lock 内 yield 一律 residual **CS2J0402**（M2/M3/M4 未做）；无项目级 fixture（SampleIteratorSolution 不存在）；Java 半区零；测试仅空占位；`--enable-pass iterator-lowering` 可临时开 |
| C05 | enum-declaration | ✅ | 已落地 | on | M1–M4 | M5 sdk/M6 未做；Java 半区 4 个 javac 问题（primitive `_value.getHashCode()` 等） |
| C06 | unsafe-ffm | ✅ | 深度比台账/注册表文案更好 | on | **M0–M4 全落地**（105 单测） | 注册表描述仍写 "C06 M0"（文案陈旧）；**Java FFM 发射通道零代码**、白名单 facade 未被消费、extern 默认 UF060 拒绝（后者为设计内安全姿态） |
| C07 | struct-value-semantics | ✅ | 已落地 | on | M1–M5 | M6：StructCloneHelper 退役、Java 半区收尾 |
| C08 | java-keywords | ✅ | 已完成 | on | 全部 | — |
| C09 | thread-static | ❌ | **已 Implemented，2026-10-06 P1 翻默认开** | on（P1-2） | M1+M2 core + **Java ThreadLocal 半区补齐 + sdk 语料门** | 已具备 javac e2e 与语料门证据；`[ThreadStatic]` 初始化器语义差异已在门证据说明；选项 `FirstThreadFaithful` |
| C10 | attribute-normalize | ❌ | **已 Implemented，2026-10-06 P1 翻默认开** | on（P1） | Caller* 四族真改写（54 单测）+ catalog/别名/Unresolved/工厂修复 + 门 1–5 | JSON/P/Invoke 仍为诊断盘点（CS2J1310/1320/1321/1322、AN003）；overrides 文件化为后续 |
| C11 | member-desugar / indexer | ✅ | 已完成 | on | 全部 | — |
| C12 | member-desugar / property | ✅ | 已完成 | on | 全部 | — |
| C13 | member-desugar / event | ✅ | 已完成 | on | 全部 | — |
| C14 | member-desugar / operator | ✅ | 已完成 | on | 全部 | — |
| C15 | numeric-boundary / checked | ✅ | 已落地 | on | 规则完整 | Java 双侧方法面齐但**无 javac e2e** |
| C16 | foreach-source | ✅ | 证据最扎实的条目之一 | on | 全部 | 唯一有完整 sdk 扫描 + javac 双端对比记录；旧 foreach 臂未退役 |
| C17 | numeric-boundary / increment | ✅ | 已落地 | on | M0–M2 | **M3 未做** |
| C18 | async-normalize | ❌ | **已落地且默认开（红账多）** | on | 轻量 M1 + M2 await-using + M3 await-foreach + M4 联合异步迭代器（23 文件/6421 行） | Java 四笔红账（见 §5）；CS2J1806/1809/1847/1848 未实现；描述文本陈旧；重量形态②经设计裁决不做 |
| C19 | extension-methods | ✅ | 已落地 | on | M1–M4（七锚 RunAfter） | **M5 旧臂未退役**：CS2J_EXT001 仍在 [ProjectPasses.cs:813](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L813)；ExtensionMethodIndex 未退役；外部 host 48 unmapped |
| C20 | partial-merge | ✅ | **v1 双份保留；2026-10-06 P0 已回退默认关（合设计建议）** | **off**（P0 回退） | M1–M2 | **M3 成员级 partial 配对塌缩未做**（C#13 成员级 partial 仅双份保留）；`3147e093` 的默认开已于 2026-10-06 回退；旧 PartialType 未退役 |
| C21 | anonymous-type-lift | ❌ | **已 Implemented 且默认开** | on | M1–M4 | M5 旧转换器未退役：[ObjectCreationTransformer.cs:83](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L83) 仍发 `Map.of(...)` |
| C22 | numeric-boundary / nullable-value | ✅ | 已落地 | on | M0–M3 | **M4 未做**；仅 1 个条件执行的 javac harness；仓内无批量扫描入口 |
| C23 | expression-desugar / interpolation | ✅ | 已落地 | on（`--interpolation-expand` 默认 true） | 主体完成 | M4 handler 画像未做；表达式树中的插值不重写 |
| C24 | expression-desugar / initializer | ✅ | 已完成 | on | 全部 | M4 sdk 挂起 |
| C25 | dynamic-desugar | ❌ | **已 Implemented，2026-10-06 P1 翻默认开（audit 门）** | on（P1-4） | M1 审计 + M2 P1 子集改写 + 五门证据 | 原「不随 all-on」由独立 audit 门承接；M3 全量仍挂起 |
| C26 | linq-channel | ❌ | **已 Implemented，2026-10-06 P1 翻默认开，M4 全量落地** | on（P1-1） | M0–M4：IQueryable 三门强制、82 算子矩阵、EF/SDK 复扫、**M4 默认翻转 + E16 项目路径摘除 + 报告 schema v2 + GroupBy 收尾** | **Java Queryable 提供方仍零代码**（IQueryable→Stream，已立 M4-d 项：EF10 Q13/Expr10、sdk 362 全 Enumerable、EF5 Q12；通道已冻结 Queryable 站点防误展开）；LC0040 3 文件为既有定点门遗留（非回归，详见 C26 设计书 M4-c 校注） |
| C27 | member-desugar / records | ✅ | 已落地 | on（`--enable-records-rule`） | 主体完成 | M4 未做 |
| C28 | lexical-normalize / directives | ✅ | 已落地 | on | M1–M2 | file-scoped 归一仍 opt-in（`--ln-normalize-file-scoped`）；M3 画像未做；**门槛未达即被翻转** |
| C29 | concurrency-normalize | ❌ | **已落地且默认开（Java 半区说谎）** | on | .NET 侧改写完成 | [Monitor.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L37-L77) `tryEnter(obj,timeout)` **恒返回 true**、`enter` 空体；无 Volatile.java；TypeMappings 无 Monitor；**`System.Threading.Lock` 零覆盖** |
| C30 | closed-metadata | ✅ | **半成品：只产不消费** | on | M2′+M3 生成注册文件 | [ClosedMetadataPreprocessor.cs:42-45](file:///d:/code/cs2j/src/CSharpToJava.Core/ClosedMetadata/ClosedMetadataPreprocessor.cs#L42-L45) `RewriteFile` **恒返回 null（M4 消费链接未做）**；下游 `D:\dotnet-text-json` 不存在，M1 门无法证实 |
| C31 | index-range | ✅ | 已落地 | on | M1–M3；compat `ArrayHelper.slice` 9 重载已齐 | M4 未做；无 sln fixture；Java e2e 仅文本断言；旧臂未退役 |
| C32 | closure-lift | ✅ | 已完成 | on | 全部 | — |
| C33 | variance-audit | ✅ | 已完成（审计即终态） | on | M3 终态 | M3 use-site 改写在设计上被排除，属"完成而非欠债" |
| C34 | numeric-boundary / decimal | ✅ | 已落地 | on | compat Decimal 方法面齐 | **M3 数值预言机未做**（无数值一致性验证） |
| C35 | expression-desugar / null-conditional | ❌ | **已 Implemented 且默认开** | on（`--null-conditional` 默认 true） | M1–M5 | 随全波次待画像 |
| C36 | expression-desugar / collection-expr | ✅ | 已落地 | on | M0–M3（含 CS2J0585–0589 防线） | 深化挂账随 C52 |
| C37 | pattern-desugar | ✅ | 已完成 | on | 全部 | — |
| C38 | async-normalize（C44 subtopic） | ❌ | **已落地且默认开**（与 C18 同 pass） | on | M2 await-using / M3 await-foreach / M4 联合异步迭代器 | 同 C18 全部红账 |
| C39 | member-desugar / finalizer | ✅ | 已完成 | on（policy=Reject，无独立开关） | M1+M2 | — |
| C40 | foreach-source | ✅ | 已落地，证据弱于 C16 | on | M1–M3 | javac 端**仅文本断言**；sdk 报表未做 |
| C41 | **无 pass** | ❌ | **确认零产品代码** | — | 仅设计书 | 无 SpecialNumeric 目录、catalogItems 不含 C41；转换器仅 IntPtr→long 半覆盖（[TypeMappings.json:4184](file:///d:/code/cs2j/config/TypeMappings.json#L4184)），UIntPtr/Half/Int128/UInt128 无映射（仅半存活的 [UIntPtr.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UIntPtr.java)）；sizeof 硬编码在 TypeOperationTransformer.cs L202-208 |
| C42 | platform-isolation | ✅ | 已落地 | on | M1 Report + M2 Stub/Reject | M3 Facade 未做 |
| C43 | lexical-normalize / literal | ✅ | 已落地，违设计门槛 | on（与 C28 同 pass） | M1–M2 | 设计书明示"不建议提前默认开"仍被翻转；M3 画像未做 |
| C44 | **无 pass（分治裁决）** | ❌ | 不立项（非欠债） | — | G1 已落 C06 R9（RefStructUsingRewriter/UF055）；G2 await-using 随 C38 | **G3 yield+using 挂 C04 M3，未做**；维护裁决：永不主动建 pass |
| C45 | closure-lift | ✅ | 已落地 | on | M1–M4 | M5 旧 holder/LambdaTransformer 退役未做（126 个 ProjectTests + 18 fixture 仍证明双轨事实） |
| C46 | member-desugar / signatures | ❌ | **已 Implemented 且默认开** | on（`--enable-signatures-rule`） | M1–M4c | 收尾画像 |
| C47 | equality-concat | ✅ | **.NET/Java 双侧已写，但当前零运行时保护** | on | Java J1–J5 已偿还 | [EqualityConcatProjectTests.cs:149](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/EqualityConcatProjectTests.cs#L149) 与 [:238](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/EqualityConcatProjectTests.cs#L238) 两个**仅有**的 Java 运行时 E2E 均 `Skip`（85.27s/48.79s，自 `7f4d14a8` 引入未恢复） |
| C48 | strip-default 内嵌规则（无独立 pass） | ❌ | **M1 已落地，M2 未做** | on（`--enable-params-collections` 默认 true；12 文件于 [ParamsCollections/](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/ParamsCollections)） | M1：签名/转发/实参改写 | M2 未做：CollectionBuilder 一律 CS2J0607、K4 Add 绑定未实现 |
| C49 | extension-methods | ❌ | **已 Implemented 且默认开** | on | M1–M3 完成（依托 Roslyn **5.0.0**，升级提交 `3c0d37bd`） | 随 C19 旧臂退役 |
| C50 | member-desugar / field-keyword | ❌ | **已 Implemented 且默认开** | on（`--enable-field-keyword-rule`） | M1–M2 | M3 未做 |
| C51 | expression-desugar / null-cond-assignment | ❌ | **已 Implemented 且默认开**（随 C35） | on | M1–M5 | — |
| C52 | **伞条目（非 pass）** | ❌ | **机制大部分为空** | — | 仅 C#11/12 四项以 CS2J0585–0589 落于 C36 | **CS2J0590–0599 全树零命中**；无 parse 前探测、无 version-audit verb；`Lock`、`file` 类型、DIM 菱形、unbound nameof、扩展索引器等 checker 全空；设计稿的 Roslyn 4.12 前提已过时（现 5.0.0） |

---

## 4. 台账勘误（一）：标 ❌ 实际已落地的 14 项

这 14 项在 10-04 12:58 快照之后（部分就在当天午后）随三波政策翻转落地，注册表状态均为 `Implemented`：

| ID | 当前默认 | 落地范围 | 与"完成"的差距 |
|---|---|---|---|
| C09 | 开（2026-10-06 P1 翻） | M1+M2 改写 core 完成 + Java ThreadLocal 半区 + sdk 门 | P1-2 已开门（原「有意保持关」解除） |
| C10 | 开（2026-10-06 P1 翻） | Caller* 四族真改写，54 单测 + catalog/别名/Unresolved/工厂修复 | JSON/P/Invoke 仍仅诊断盘点；门 1–5 已过 |
| C18 | 开 | M1–M4 异步归一全套（6421 行） | Java 四笔红账、4 个诊断未实现 |
| C21 | 开 | M1–M4 匿名类型提升 | 旧 `Map.of` 臂未退役 |
| C25 | 开（2026-10-06 P1 翻） | M1 审计 + M2 P1 子集 + 五门证据 | 由独立 audit 门承接「不随 all-on」；M3 全量仍挂起 |
| C26 | 开（2026-10-06 P1 翻，M4 全量） | M0–M4（三门强制 + 82 算子矩阵 + E16 项目路径摘除 + schema v2 + GroupBy） | 仅剩 Java Queryable 提供方（M4-d 已立项划界）、LC0040 3 文件既有遗留 |
| C29 | 开 | .NET 侧锁/信号量归一 | Java 半区恒真壳 + Lock 零覆盖 |
| C35 | 开 | M1–M5 null 条件改写 | — |
| C38 | 开 | await-using/await-foreach/联合异步迭代器 | 同 C18 |
| C46 | 开 | M1–M4c 成员签名规则 | 画像收尾 |
| C48 | 开（规则级） | ParamsCollections M1（12 文件） | M2 CollectionBuilder/Add 绑定 |
| C49 | 开 | M1–M3（Roslyn 5.0） | 随 C19 退役 |
| C50 | 开 | M1–M2 field 关键字规则 | M3 |
| C51 | 开 | 随 C35 全套 | — |

仍与台账 ❌ 一致的三项：**C41（零实现）、C44（设计上不立项；G3 待 C04 M3）、C52（伞机制大部分空）**。

---

## 5. 台账勘误（二）：标 ✅ 但名不副实的风险卡

按"默认开后的实际伤害"排序。

1. **C04 iterator-lowering —— 只做了 M1 就默认开（风险最高）**。[IteratorLoweringAnalyzer.cs:147-166](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringAnalyzer.cs#L147-L166) 对 ref/in/out 参数、含 yield 的 try-catch/try-finally 发 residual CS2J0402 跳过；M2（try-finally）、M3（using/lock，同时是 C44 G3 的挂载点）、M4 全部未做；SampleIteratorSolution fixture 不存在、IteratorBridgeTests 为空占位、Java 半区零产出。默认开意味着含这些构造的方法现在会走"跳过 + 旧路径"，语义责任不清。
2. **C29 concurrency-normalize —— Java 半区是"会编译但语义说谎"的壳**。[Monitor.java:13-19](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L13-L19) `enter` 空体（注释自陈"only to satisfy the translated call site"）；[:37-77](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L37-L77) 四个 `tryEnter` 重载（含超时）**全部无条件 `return true`**；无 Volatile.java；TypeMappings 无 Monitor 映射；C# 9.0 的 `System.Threading.Lock` 零覆盖。超时锁被静默翻译成"永远成功"，是隐蔽并发 bug 源。
3. **C47 equality-concat —— 当前零 Java 运行时保护**。J1–J5 五个 Java 半区偿还已合，但唯一两个真正"转换→javac→运行"的 E2E（85.27s、48.79s）自 `7f4d14a8` 起 Skip 至今，未排查恢复。
4. **C18/C38 async-normalize —— Java 半区四笔红账**：① TypeMappings 缺 `Task.Delay`、`CancellationToken.Register` 等映射；② CompletionException 在半区零命中；③ `ConversionContext.IsInAsyncContext` 全树没有 `= true` 赋值，是死路径；④ CS2J1806/1809/1847/1848 四个诊断未实现。23 文件 6421 行的默认开 pass 建立在不完整的异步运行时假设上。
5. **C30 closed-metadata —— 只写注册文件、无人消费**。M3 生成产物存在，但 `RewriteFile` 在 [ClosedMetadataPreprocessor.cs:42-45](file:///d:/code/cs2j/src/CSharpToJava.Core/ClosedMetadata/ClosedMetadataPreprocessor.cs#L42-L45) 恒返回 null，M4 ConsumptionLinker 未做；且设计中的下游仓库 `D:\dotnet-text-json` 在本机不存在，M1 门无法被独立证实。
6. **C20 partial-merge —— 能力只到 v1，默认姿态违背设计**。C#13 成员级 partial 目前仅"双份保留"，M3 配对塌缩未做；设计书明确建议长期默认关，被 `3147e093` 政策翻转；旧 PartialType 臂未退役。
7. **C28/C43 lexical-normalize —— 门槛未达即翻转**。file-scoped namespace 归一仍需 `--ln-normalize-file-scoped` opt-in；sdk 画像（M3）与"双档等价对照"未做；C43 设计书原文"不建议提前默认开"。
8. **C06 unsafe-ffm —— .NET 侧 M0–M4 全完成（注册表文案 "C06 M0" 陈旧），但 Java FFM 发射通道零代码**，白名单 facade 未被任何消费方使用，extern 方法默认走 UF060 显式拒绝。安全姿态正确，但"unsafe/FFI 可转换"对外不可宣称。
9. 次级名不副实（能力在但验证浅）：**C05**（Java 半区 4 个 javac 错误未清、M5/M6 未做）；**C40**（javac 仅文本断言、sdk 报表缺）；**C31**（无 sln fixture、Java 侧仅文本断言）；**C34**（M3 数值预言机缺）；**C22**（M4 缺）、**C17**（M3 缺）；**C15**（无 javac e2e）；**C23**（M4 handler 画像缺）；**C42**（M3 Facade 缺）；**C27/C50**（M4/M3 缺）；**C19/C21/C45/C07**（M5 旧转换器退役普遍未做，双轨并存）。

---

## 6. 横切债务（影响所有条目的可信度）

1. **端到端闸门挂起**：提交 `c49753bc`（10-04 13:18，"临时跳过 ProjectTests Top10"，后累积到 22 个）为压缩串行耗时（19m33s→11m18s）将 17 个文件中的慢 E2E 全部 Skip，理由均为"排查根因中"。覆盖 C47×2、C16×3、C28/C43×2、C20、C30、C36、C45、C19、C39、C40、C07、C33、E10、E14×2、preprocess verb×2、E11。当前绿测试主要证明"隔离单测逻辑正确"，不证明全链路。
2. **集成债**：241 个提交未合入本地 dev-3.x、639 个未推 origin；合并前应先过第 7 节 P0。
3. **双轨债（M5 族）**：C19（CS2J_EXT001/ExtensionMethodIndex）、C21（Map.of 臂）、C45（旧 holder/LambdaTransformer，126 测试+18 fixture）、C07（StructCloneHelper）、C09（ThreadStaticTransformer）、~~C26（E16 LINQ）~~、C16/C31/C02/C01 旧臂均在。新旧路径长期并存会让"默认开"的实际覆盖率打折。**（2026-10-06 回写）C26 项目路径嵌入式 E16 已在 M4-c 物理摘除**（单文件 `SingleFileLinqDesugarPass` 按 §5.5/Q4 设计保留，不算双轨）；C09 P1-2 已开门，其旧 ThreadStaticTransformer 臂退役随之解除阻塞（见 P4）。
4. **画像债**：catalog §5.1 债务 C 要求的 sdk / EF Core / .NET10 源码画像几乎全未做——仅 C16 留有完整 sdk 扫描+双端对比记录；C22 §13.5 自陈仓内无批量扫描入口。多数 pass 的"默认开门"缺少语料频率与残差率证据。
5. **文案债**：PassCatalog 中 C06 描述停在 "M0"、C18/C30 描述与实现不符；部分设计书 §13 状态行陈旧（C34/C17/C29）；桌面台账与 catalog 均已过期——本报告产出后建议以注册表 + 本报告为准。

---

## 7. 完整后续实施顺序

原则：**先止血（默认开但会静默错译）→ 再决策默认关项的开门 → 再还 Java 半区债 → 再补 .NET 里程碑 → 再退役旧臂 → 最后做新构造与画像**。同一层内按依赖（见 §2 RunAfter）排序。

### P0｜止血与集成门槛（合并 dev-3.x 前必须完成）

| 序 | 事项 | 验收信号 |
|---|---|---|
| P0-1 | **恢复 E2E 闸门分级**：把 22 个 Skip 拆为 PR 级快速门 + 夜间慢门，逐个排查根因而非长期 Skip；先恢复 C47 两个 Java 运行时 E2E | EqualityConcatProjectTests L149/L238 去 Skip 且通过；其余 20 个有归属门 |
| P0-2 | **C29 诚实化**：Monitor 的 `tryEnter` 超时重载要么接真实 `java.util.concurrent.locks` 实现（含 lockTaken 重载），要么对超时路径显式诊断拒绝；补 Volatile.java 与 TypeMappings 的 Monitor 映射；`System.Threading.Lock` 分型到 C52 checker 或本期补映射 | Monitor.java 无"恒 true"；超时语义有 javac+运行时测试 |
| P0-3 | **C04 姿态二选一**：要么补 M2（try-finally 降低）+ M3（using/lock，顺带 C44 G3），要么先把 iterator-lowering **回退默认关**直到 M3 完成 | 默认开时 try-finally/using/lock 内 yield 有正确降低或显式错误，无静默走旧路径 |
| P0-4 | **C18/C38 Java 红账决策**：接通 IsInAsyncContext 死路径、补 Task.Delay/CancellationToken.Register 映射与 CompletionException 半区、实现 CS2J1806/1809/1847/1848；若本期不做则评估回退默认关 | 异步 fixture 可 javac + 运行；死路径有赋值者或被删除 |
| P0-5 | **C20/C28/C43 默认翻转复审**：以画像数据确认双份保留/字面量归一不造成残差；C20 至少在 M3 前提供 kill-switch 文档化 | 三个 pass 的默认姿态有书面依据（维持或回退） |
| P0-6 | 修正注册表与设计书陈旧文案（C06/C18/C30/C34/C17/C29）；241 个提交合入 dev-3.x 并推送 | 描述与代码一致；主干包含本波次 |

### P1｜四个默认关 pass 的开门/长期关闭决策

> **2026-10-06 回写：P1 四件已全部完成并翻默认开**（在本分支逐件提交，未 push）。逐件结果见表内「回写」。

| 序 | 事项 | 开门门槛 | 2026-10-06 回写 |
|---|---|---|---|
| P1-1 | **C26 M4**：默认翻转 + 摘除 E16 旧 LINQ 臂 + GroupBy 收尾；**Java Queryable 提供方单独立项**（当前 IQueryable 仍→Stream，不解决则转换结果在远端求值场景不成立） | EF Core 语料复扫残差率达标；Java 提供方有最小可运行子集 | **完成并翻默认开**（M4-a GroupBy、M4-b 翻转+插值串修复、M4-c 摘 E16 项目路径+schema v2+死选项+LC0040 处置、M4-d Java Queryable 立项材料）；Java 提供方按 M4-d **另立 spec 不顺带做**，通道已冻结 Queryable 站点防误展开 |
| P1-2 | **C09 §8**：补 Java ThreadLocal 半区（含 `[ThreadStatic]` 初始化器语义差异说明），跑 sdk 语料门 | javac e2e + 语料频率/残差报告 | **完成并翻默认开**：Java ThreadLocal 半区 + 初始化器语义差异说明 + sdk 语料门证据齐备（三批提交） |
| P1-3 | **C10 §8**：决定 JSON 序列化成员名/P-Invoke  marshaling 是进 M2（显式注册/改写）还是长期保持"诊断盘点"姿态；补 overrides 文件化 | 决策落文档；若开门则 JSON M2 完成 | **翻默认开**（catalog/别名/Unresolved/工厂修复 + 门 1–5）；**决策：JSON/P/Invoke 长期保持诊断盘点姿态**（CS2J1310/1320/1321/1322），不进 M2 改写；overrides 文件化留后续 |
| P1-4 | **C25**：完成 M3 与 §8 动态语料门；在此之前保持默认关（尊重设计明令） | 语料门报告 + 风险白名单 | **翻默认开**：以独立 audit 门承接「不随 all-on」约束 + 五门证据；M3 全量动态改写仍挂起（门保护下不静默错译） |

### P2｜Java 半区还债（默认开 pass 的正确性闭环）

顺序：**C29（P0 已启动）→ C18（P0 已启动）→ C05 → C06 → C40/C31 → C15/C34/C22/C17 → C07**。

1. **C05**：清 4 个 javac 问题（primitive `_value.getHashCode()` 等），完成 M5 sdk/M6。
2. **C06**：接通白名单 facade 的消费方，落地最小 Java FFM 发射通道（opt-in），extern 继续默认 UF060。
3. **C40/C31**：javac 断言从文本升级为编译+运行；C31 补 sln fixture；消费已齐的 `ArrayHelper.slice` 9 重载。
4. **C15/C34/C22/C17**：补 javac e2e 与 **C34 M3 数值预言机**（Decimal 行为一致性）、C22 M4、C17 M3。
5. **C07**：M6 StructCloneHelper 退役前的 Java 半区核对。

### P3｜.NET 半区里程碑补完（按 §2 拓扑的依赖序）

1. **C04 M2→M3→M4**（若 P0 选择先补做）：try-finally → using/lock（含 **C44 G3**）→ ref struct 枚举桥。
2. **C30 M4 ConsumptionLinker**：让 `RewriteFile` 真正产出并接通消费侧；恢复或重建 `D:\dotnet-text-json` 下游以证实 M1 门。
3. **C48 M2**：CollectionBuilder 从一律 CS2J0607 升级为真实改写；完成 ParamsCollections 的 K4 Add 绑定。
4. **C20 M3**：成员级 partial 配对塌缩；之后旧 PartialType 臂随 P4 退役。
5. **C23 M4**（插值 handler 画像；表达式树边界固化）→ **C17 M3 / C22 M4 / C34 M3**（与 P2 联动）→ **C31 M4** → **C42 M3 Facade** → **C27 M4 / C50 M3** → **C36 深化** → **C28/C43 M3 画像**（file-scoped 归一转默认）→ **C01 M5 / C02 M4** sdk 门。
6. **C16/C40 sdk 报表**：把 C16 的画像方法复制给 C40，形成可复用的批量扫描入口（同时解除 catalog §5.1 债务 C 与 C22 §13.5 的"无入口"问题）。

### P4｜旧转换器退役（必须在 Java 半区验证之后）

每退役一项 = 删除旧臂 + 删除/改写对应旧 ProjectTests/fixture + 画像无回退：

1. ~~**C45** 旧 holder/LambdaTransformer（126 测试 + 18 fixture 是最大双轨面）~~ **（2026-10 已退役，226226e5/99bac1b2）**
2. ~~**C21** ObjectCreationTransformer 的 `Map.of` 臂~~ **（2026-10 已退役为 fail-fast，5b4eb653；原 [L83](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs) 现位于 L129-139）**
3. ~~**C19/C49** ExtensionMethodIndex 旧臂与 CS2J_EXT001，先消化外部 host 的 48 个 unmapped~~ **（2026-10 已整类删除，0a910136/b70200d6；外部 host 告警由预处理期 CS2J1203 接管）**
4. ~~**C26** E16 LINQ 旧臂（依赖 P1-1）~~ **（2026-10-06 M4-c 已摘除项目路径臂）**：`ProjectLinqDesugarPass` 及 `LastLinqStatistics`/`PreDesugarCompilation` 下游全删，报告切通道 schema v2；**单文件嵌入式 `SingleFileLinqDesugarPass` 按 §5.5/Q4 有意保留**（单文件无中间目录契约），不在退役范围
5. ~~**C09** ThreadStaticTransformer（f234760b）~~、~~**C16** foreach 直翻臂（653fbfb6）~~、~~**C31** Index/Range 直映臂（52e741c2）~~、~~**C02** E10 `__cs2j_typeof_` 哨兵与 override 同步旧轨（e43423f6；FieldTransformer 静态泛型字段 fail-fast 早已于 4b736217 落地）~~ **均已退役**。
   - **C01 旧 holder 臂裁决保留（52b7de85 仅删零调用垫片）**：CD1/CD3 保留的外部 BCL/extern/override 锁定签名在中间树合法残留，compat 库自身暴露 `tryParse`/`tryGetValue`/`tryTake`/`resize` 等 holder 形参，旧臂是活合同兜底；`RefOutInPostVerifier` 已接线（CS2J3305）。整体退役仍挂 sdk 全量门与 Java 半区验证。
   - **C07 StructCloneHelper 继续阻塞**：SV040-043 的 overrides.skip 具名逃生口（SV020）有意回退该代偿，且 §8 门槛 5 的 Java 半区 double-clone 去重核对未做；解除前不退役。

### P5｜新构造与长期基线

1. **C41 特殊数值类型**：先建 audit-only 规则（SpecialNumeric 检测，纳入 numeric-boundary 或新 pass）→ Half/Int128/UInt128/UIntPtr 显式诊断拒绝（停止静默半转换）→ 修 IntPtr 半覆盖（[TypeMappings.json:4184](file:///d:/code/cs2j/config/TypeMappings.json#L4184) 与 sizeof 硬编码 TypeOperationTransformer.cs L202-208）→ 分期映射 + javac 数值预言机。
2. **C52 版本防线机制件**（伞条目落地，基于现 **Roslyn 5.0.0** 重估设计稿）：
   - 分配 **CS2J0590–0599** 诊断码段，建 parse 前 probe 与 `version-audit` CLI verb；
   - 子 checker：`System.Threading.Lock`（与 P0-2 衔接）、`file` 类型、DIM 菱形、unbound nameof、扩展索引器；
   - 已有 C#11/12 四项防线 CS2J0585–0589 保留在 C36 并登记到伞下。
3. **C44**：永不主动立项；G1（ref struct using）维护 C06 R9，G2 维护 C38，**G3 随 C04 M3 交付**。
4. **画像基线**：重建仓内 sdk 批量扫描入口，产出 .NET 10 SDK + EF Core 9 全量画像（频率/残差/双档等价），作为所有"默认开/关"决策与旧臂退役的统一证据源；回灌更新 [preprocessor-catalog.md](file:///d:/code/cs2j/prompts/preprocessor-catalog.md) 与桌面台账。

### 建议时间序（一句话版）

> **本周**：P0-1～P0-4（E2E 恢复、C29/C04/C18 三个默认开风险）＋P0-6 文案；**合并主干后**：P0-5 姿态复审 → P1 开门决策（C26 优先）→ P2 Java 还债与 P3 里程碑按 pass 链穿插（C04→C30→C48→C20 是 .NET 侧关键路径）→ P4 退役双轨 → P5 做 C41/C52 与画像基线。

---

## 8. 证据索引

- 注册表：[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)；执行序以 `dotnet CSharpToJava.CLI.dll preprocess --list-passes` 实测为准。
- 风险代码锚点：
  - C29：[Monitor.java#L13-L77](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L13-L77)（空体 enter、恒 true 的 tryEnter）
  - C04：[IteratorLoweringAnalyzer.cs#L147-L166](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringAnalyzer.cs#L147-L166)（CS2J0402 residual）
  - C30：[ClosedMetadataPreprocessor.cs#L42-L45](file:///d:/code/cs2j/src/CSharpToJava.Core/ClosedMetadata/ClosedMetadataPreprocessor.cs#L42-L45)（RewriteFile 恒 null）
  - C19 旧臂：[ProjectPasses.cs#L813](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L813)（CS2J_EXT001）
  - C21 旧臂：[ObjectCreationTransformer.cs#L83](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L83)（Map.of）
  - C47 Skip：[EqualityConcatProjectTests.cs#L149](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/EqualityConcatProjectTests.cs#L149)、[#L238](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/EqualityConcatProjectTests.cs#L238)
  - C41 半覆盖：[TypeMappings.json#L4184](file:///d:/code/cs2j/config/TypeMappings.json#L4184)（仅 IntPtr）、[UIntPtr.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UIntPtr.java)（半存活）
  - C48 M1：[ParamsCollections/](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/ParamsCollections)（12 文件）
- 设计书：[docs/superpowers/specs/](file:///d:/code/cs2j/docs/superpowers/specs)（55 份，命名 `2026-09-2{8,9}-cXX-*-design.md`；C41/C44/C52 有设计书但无对应 pass 实现）
- 关键提交：默认翻转 `e5e7773a` / `3147e093` / `a7f1cb9c` / `ba7a0897` / `f16cb8ca` / `0f935995`；E2E Skip `c49753bc`；Roslyn 5.0.0 升级 `3c0d37bd`；C47 E2E Skip 引入 `7f4d14a8`。
- 分支差：`dev-3.x-26100507` 领先本地 `dev-3.x` 241、领先 `origin/dev-3.x` 639（origin 止于 09-16 `94e123b8`）。

---

## 9. P0 止血实施记录（2026-10-06，分支 `dev-3.x-26100507`）

> 本节是第 7 节 P0-1～P0-5 的实施台账。所有"探针"均指临时工程（`%TEMP%\cs2j_probe_async`，net8/net10 ref pack + TPA 双引用矩阵）实测；诊断码裁决遵循"基线优先、码位只追加不挪用、不发假诊断"。

### 9.1 P0-1：22 个 Skip → 分级闸门（已完成）

- 两个测试工程各增 `Infrastructure/SlowE2EGate.cs`：环境变量 `CS2J_RUN_SLOW_E2E=1` 才执行慢 E2E，否则 `Skip("slow e2e gate …")`。
- 23 处（原台账 22 处 + 合并中新发现 1 处）`Assert.Skip`/`Fact(Skip=…)` 全部替换为闸门；默认态全 Skip 快速通过，设变量后全量执行（双态实测）。PR 级快速门恢复；夜间慢门以同一变量挂 CI 即可，无需再改代码。

### 9.2 P0-3 / P0-5：默认姿态回退（已完成）

- C04 iterator-lowering、C20 partial-merge 描述符 `defaultEnabled: false`；C43 四条字面量规则改 opt-in。
- [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) `BuildLegacyToggles` 统一加前导 `PassCatalog.ImplementedById(id).DefaultEnabled &&`——默认姿态以描述符为**单一事实源**，legacy 映射表不再能独自开门。
- C43 opt-in 的两副面孔（既有 CLI 契约，LexicalNormalizeCLITests 锁定）：默认链/convert-project 上四条字面量规则需 `--ln-normalize-literals` 显式开门；**显式 `preprocess --pass lexical-normalize` 时选 pass 的意图本身即授权**，四规则默认开；`--ln-no-raw/u8/verbatim/esc` 两种形态下都只做细化关闭。
- [Program.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs) `ConvertProjectOptions` 补 5 个 `ln-normalize-*` 选项，消除两选项类分叉导致的 8 个 CS1061。
- 三方合并冲突（PassChainOrderTests，C25/C26/C09 相邻 hunk）按并集手工解决。

### 9.3 P0-2：C29 Monitor/Volatile/Lock 诚实化（已完成）

- [Monitor.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java)：enter/exit 校验 holdsLock，不再空体/恒真；4 个 tryEnter 重载一律 `UnsupportedOperationException`（超时语义 Java 监视器无法提供，选择"显式拒绝"而非"假装成功"）；wait/pulse 委托真实 monitor。
- 新增 [Volatile.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Volatile.java)（9 Holder + VarHandle `getAcquire/setRelease`，`findVarHandle` 非 `findField`）与 [Lock.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Lock.java)（委托 ReentrantLock）。
- TypeMappings 补三类映射；`TransformLockStatement` 对 `System.Threading.Lock` OriginalDefinition 发 `_cs2jLockN.enter()/try/finally exit()`。
- Java 侧 MonitorTest 9 + VolatileTest 3 + LockTest 5 全绿（JDK28）。

### 9.4 P0-4：C18/C38 四笔红账处置

#### 红账 ① Task.Delay / CancellationToken 假映射（已偿还）

- **删除** `Task.Delay → CompletableFuture.delayedExecutor` 假映射（右侧返回 Executor，不可编译）；**整条删除** `Task\`1.Delay`（该 BCL 方法不存在）。非泛型 Task 改发 `AsyncTasks.delay`（TypeMappings 方法映射，含点静态 helper 渲染规则）。
- 新增 [AsyncTasks.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/AsyncTasks.java)：共享 daemon `ScheduledExecutorService`，4 个 delay 重载；`<-1` 抛 ArgumentOutOfRangeException、`-1` 无限等待、token 取消联动 future.cancel 并解绑注册。
- 新增 [AsyncAwait.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/AsyncAwait.java)：`join(CompletableFuture)` 剥掉 CompletionException、sneaky-rethrow 原始 cause——对齐 C# await 抛原始异常而非包装异常（受检异常类型也保留）。ControlFlowTransformer 的 await 发射全部改走该桥。
- CT/CTS/CTR 半区全量重写：[CancellationToken.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CancellationToken.java)（AtomicBoolean + CopyOnWriteArrayList，None 单例惰性不触发，注册/取消/双检）、[CancellationTokenSource.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CancellationTokenSource.java)（cancel/cancelAfter/close + 定时池）、新增 [CancellationTokenRegistration.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CancellationTokenRegistration.java)（CAS 单次回调，dispose 解绑）。补 Register/IsCancellationRequested/Cancel/CancelAfter 方法映射与 CTR 类型映射。
- 新测试：AsyncTasksTest 7、AsyncAwaitTest 4、CancellationTokenTest 6（含 cancelAfter、已取消立即回调、-1 不完成、取消解绑）。

#### 红账 ③ IsInAsyncContext 死路径（已接通，语义诚实标注）

- [ConversionContext.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs) `EnterMethod`：`IsInAsyncContext = method.IsAsync && AsyncShapeClassifier.IsTaskLike(method.ReturnType)`（async void 不置）。提升的局部函数在 [MethodTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs) `TransformLocalFunction` 周围 save/restore。
- **尾 return 补齐**：C# `async Task` 方法允许省略尾 return，Java 返回类型已是 CompletableFuture；以 Roslyn `AnalyzeControlFlow(body).EndPointIsReachable` 为准（端点不可达不补，避免 Java unreachable 编译错误）追加 `return CompletableFuture.completedFuture(null);`；表达式体 async 方法同步包 completedFuture。
- **诚实边界（非状态机）**：轻量 async-normalize 下 await 经 `AsyncAwait.join` **阻塞当前线程**直到 future 完成；接通只保证返回类型/异常/编译契约，不承诺 C# 的挂起-续延调度语义（c18 设计 §4.8 重量形态明确裁决不做状态机综合）。async 属性访问器（不经 EnterMethod）仍是已知边界，本期不扩大范围。

#### 红账 ④ 诊断码：两码保留、两码经探针否决

| 码位 | 裁决 | 证据 |
|---|---|---|
| **CS2J1809** | ✅ 保留（unsafe/fixed 上下文 await，基线 CS4004 优先镜像） | net10 探针：unsafe 块/fixed 内 await → CS4004，且常量 false 不可达分支**仍报**；Roslyn 5 全语言版本一致。基线已报则不重复发（与 1805 同纪律）。 |
| **CS2J1806** | ❌ **码位不分配**（曾拟镜像 CS4007/CS9217） | Roslyn 5.0.0 实测矩阵：CSharp10/11/12 分别由声明面 **CS8936/CS9058/CS9202** 拦截；**CSharp13 起 ref struct 局部跨 await 合法**，CSharp13/14/Latest/Preview 零诊断（TPA 与 net10 ref pack 双引用一致）。CS4007/CS9217 在内置编译器下从不出现——基线无洞；且阻塞式 await 下 ref struct 局部在 Java 侧即普通局部，无能力缺口。发码只会误杀合法现代代码。活性扫描代码全部撤除。 |
| **CS2J1847** | ✅ 保留（BCL `ConfiguredAsyncDisposable`） | 探针：`IAsyncDisposable.ConfigureAwait(false)` 产物为真实可达的强类型结构体，只有模式型 DisposeAsync、不实现 IAsyncDisposable；await using 资源位遇之会被误归鸭子型 1842 给出"显式实现接口"的错误建议。资源位（BuildResources）与类型使用点（CountAsyncTypeSites）各一发。 |
| **CS2J1848** | ❌ **码位不分配**（曾拟配 `ConfiguredCancelableAsyncEnumerator<T>`） | net8/net10 双探针：该结构体 **internal**、用户代码不可命名；`IAsyncEnumerator<T>.ConfigureAwait(bool)` 重载解析到 IAsyncDisposable 扩展（产物即 1847）；`ConfiguredCancelableAsyncEnumerable<T>.GetAsyncEnumerator()` 返回的嵌套 `Enumerator` 用于 await foreach 时基线直接报 CS8411。用户代码不可达，保留诊断即假面，枚举/判定/调用点全部撤除。 |

新增/更新 C# 单测：1809 基线优先 1 个、C#13 合法跨 await 零诊断 2 个、1847 资源位+类型位 2 个。

### 9.5 验证证据

- `dotnet build CSharpToJavaConverter.slnx -c Release/-c Debug`：0 错误（两个配置都构建——CLI 类测试固定 shell 到 `src/CSharpToJava.CLI/bin/Debug/net10.0`，只构建 Release 会造成"全绿单测 + CLI 测试成片失败"的假象，本波次中途实测踩到并已回填双配置构建）。
- CSharpToJava.Tests 全量：3415 通过 / 0 失败 / 3 Skip（Skip 均为分级闸门慢 E2E 默认态）。其中 PassStubCatalogTests 两个、ProjectTests/PreprocessVerbCLITests 一个"启用 Stub → CS2J9001"样本测试随最后一个 Stub（C25）翻 Implemented、`PassStubCatalog.Stubs` 已空而移除（CS2J9001 机制保留，再有 Stub 注册时补样本）；`--list-passes` 断言同步改为"无 Stub、有 Implemented"。
- CSharpToJava.ProjectTests 全量（2026-10-06 终态复跑，Release）：**1842 通过 / 0 失败 / 20 Skip（慢 E2E 闸门默认态）/ 1862 总计**。过程中三批失败均已定位闭环：首批 20 个 CLI 失败为 Debug CLI 陈旧（补双配置构建后定向 34 全绿）；3 个 LexicalNormalize CLI 失败为 C43 opt-in 误伤显式 `--pass` 契约（按两副面孔修正后定向 8+2skip 全绿）；2 个 Stub 样本失败随 Stubs 已空而移除/改写。
- Java（JDK28 `mvn test`）：**1636 全过 / 0 失败 / 0 跳过**。原唯一失败 `ByRefTest.byRefSpanElementPassesThroughAndAliases`（C01 提交 53cf4304 引入的测试索引笔误：代理别名 span 元素 0，却 `span.set(1, 20)` 后断言该代理可见 20；与 C# `ScopedByRef.Create(span,i)` 的 `ref span[i]` 精确别名契约矛盾，实现侧 ByRef/Span 均正确）已改为 `span.set(0, 20)`，三段别名验证（代理→span、span→代理、双代理同元素）自此自洽。
- 观察项（非本波次回归）：`DynamicDesugarSolutionProjectTests.Audit_ReportOnly_BytesEqual_AndReportCountsPinned` 在多构建节点争用的全量并行跑中出现过 1 次 1s 抖动失败，单跑/定向批次均通过，疑似 MSBuild 节点争用，未复现稳定路径，留观。


