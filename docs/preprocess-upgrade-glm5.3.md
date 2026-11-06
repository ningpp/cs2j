# Preprocessor Java 友好规范子集与泛型能力升级方案（GLM-5.3）

> 版本：2026-11-06 **v8**（**rt10 实测基线立账 + VS-0 实施蓝图 + JM 底账扩容版**——v3 换源以来**第一次在 rt10 真源上运行全链**并落档实测：默认链中止于 `local-function-lift`（2 residual——**新缺陷 V-09**：switch section 内局部函数 lift 半改写，类级方法已生成、调用点已改写、原声明未删除）；`--no-local-function-strict` 逃生后中止于 `expression-desugar` CS2J0545 **整树守恒门**（96 init 残差 vs 88 per-file 记账不守恒——G-41 的现存先例在 rt10 上发作）；YamlDotNet 默认链 W-06 在当前 HEAD **精确复现**（`TypeConverterCache.cs` `?? []` 1 residual → Error）。新增 **W-17**（rt10 双中止的链鲁棒性实证）、W-10 的运行级证据（`$(NetCoreAppCurrent)` 属性 TFM 值不可解析）；V-09/W-17 修复与 CS2J0545 归因列入 M0 入口任务序。同时补两块可执行性：**附录 I VS-0 实施蓝图**（R12/R13/R14 的文件级落点、接口签名、测试清单）与**附录 H.3 JM 底账扩容**（6 个种子 → VS-1~VS-4 全量指认，全部按当前 HEAD 实测行号）。v8 不修改任何产品代码；HEAD `98b4c539` 与 v7 事实基线 `a1cb6633` 之间零代码提交，v7 引用行号经抽查全部仍成立）
> 2026-11-06 **v8.1**（**V-09 修复落地与实测闭环**——按本方案 §6.4-R0 设计实施：`LocalFunctionLiftRewriter` 新增 `VisitSwitchSection` 移除通道 + `UnremovablePlans` 原子性防护（不可移除容器 → `LocalFunctionLiftPreprocessor` 整文件回滚），聚合 residual 诊断嵌入位置清单；4 个新 xUnit 用例（rt10 `JsonElement.DeepEquals` 形态）4/4 绿、全量 **4552 测试 0 失败**、探针 3 复跑（`D:\cs\rt10-probe3-v8fix.log`）**实测 W-17①关闭**：`local-function-lift: 59 lifted, 0 residual`，链越过第 4/5 pass 止于 CS2J0545——与方案预测逐字一致；顺带将 CS2J0545 归因范围收窄为 2 文件（`JsonPrimitiveConverter.cs` 的 1 个 unpaired 随半改写消除，剩余 `JsonSchemaExporter.cs` 28/22 + `PropertyRefCacheBuilder.cs` 1/0））
> 2026-11-06 **v7**（**Java 友好规范子集与下游退休闭环版**——把 v6 的“Java 减负规则清单”升级为可执行的两阶段架构：定义 **JCS-1（Java-friendly C# Subset v1）**、新增版本化 `preprocess-manifest.json` 能力握手、最终树语义闭包、`inventory → shadow → enforce → retire → ratchet` 补偿分支退休协议与总复杂度账本；新增缺口 **G-40~G-44**、设计 **R12~R15**、试点 **JE-08~JE-10**，探针 **23→26 码**（+CALL-SHAPE/+VALUE-COPY/+SEMANTIC-CLOSURE）；修订 I10，避免“每条规则都必须删 Java 代码”的教条；将 JE-04 扩到 byref/out 条件求值顺序，将 struct 值拷贝与调用形状列为高价值垂直切片；明确 `--no-preprocess` 在 java-ready 下不得再以 Warning 绕过能力校验。v7 不重跑语料、不刷新 v6 的运行统计，新增代码事实均按当前 HEAD 复核）
> 2026-11-06 **v6**（**面向 Java 侧净简化的回灌版**——基线刷新至 HEAD `8268251a`（V/S/W 逐条复核时点为 `a60afc75`；其后 `8268251a` 仅为 Java codegen 枚举器擦除碰撞修复，恰为 J-02 再添实证，不影响复核行号），V/S/W 共 32 条（V8+S8+W16）逐条复核**全部仍成立**（S-05 细化为"机制半在、过滤/闭包/调用点三残留"，V-04/V-08/W-01/W-03 等行号漂移订正、CS2J1203 实测为**一码四义**、HolderSyntaxGenerator 路径订正，详见各条与附录 B）；v5 后 15 个提交（2 预处理 + 13 Java/compat；编辑过程中新增的 `8268251a` 枚举器 Current 擦除碰撞修复已并入 J-02 证据）成为新证据源，据此新增**层面 J：Java 承接复杂度回灌**（J-01~J-07：渲染期文本重建闭型 / 集合擦除一致性 Java 后处理 / 约 1666 行强制适配网 / 条件副作用 Java 线性化 / BCL 重载逐案补丁 / fail-fast 口径分裂 / 生成物反向依赖 Java 逐名特判）、缺口 **G-33~G-39** 与 **W-15/W-16**、不变量 **I10**（能力层新规则必须指认被消除的 Java 侧机制）、**R11 JE-01~07 吸收规则族**（含两个候选新 pass `collection-shape-normalize`/`condition-effect-linearize`）、探针 **21→23 码**（+COLLECTION-SHAPE/+CONDITION-EFFECT）、§7.1-8 **Java 侧净简化对账门**与附录 H **Java 复杂度普查底册**；v6 仍**不修改 Java 转换器**——JE 规则以"对应 Java 特例在真实语料上成为死代码（可删）"为验收，删除动作归 §7.2 下游协调项）。
> 2026-11-05 **v5**（GLM-5.3 分析版；v5 **吸收 kimi 3.1 pro 版设计层**——v2 仅吸收其经核证的论断（P0 缺陷清单/语料语义链/四路差分），本版进一步吸收其框架缺口与结构化设计并逐项核证：两入口 profile×disable 语义分裂（W-11）、源生成器输出物化（R9/CM-05）、消费面执行时序模型 execution kind（R3 扩/CM-03/CM-LINK-3）、逐项目 compilation 工作区（L1 扩/W-14）、ApiManifest 与 ABI diff（R10/GD-06）、GD-MTOK 可见性分级、TypeParameterId 声明 owner 身份、GD-FIELD holder 并发与双检锁证明、探针 19→21 码（+SOURCE-GENERATOR/PUBLIC-ABI）、行为反例 12→16 类（+并发/NRT 反射行为/类型关系查询/成员发现）、per-TFM 验收纪律，采纳/不采纳清单见附录 D；v4 **吸收 deepseek 1017 版设计层**——v2 只吸收其经核证的论断，本版进一步吸收其结构化设计：GNC 泛型构造目录（附录 E GC-01~GC-35）、CN-01~12/VR-01~08/GI-01~11/EB-01~06 完整规则表与 GD-MTOK-2 触发集、消费面三 profile 细则、三规则族 pass 化决策、插入顺序兼容性细则、验收命令/指标矩阵/一致性测试、报告 schema 与一页纸清单（§7.0/§7.1-6/7、附录 F/G）；v3 **换源**：json 语料更换为 .NET Runtime 10.0.12 真实 System.Text.Json 源码并修正 v1/v2 的语料时代错位，新增 W-09/W-10 缺口与对应修复项；v2 刷新基线至 52 pass / json 0 失败并吸收四份同行方案实测增量，采纳/不采纳清单见附录 D）
> 基线：`D:\code\cs2j`，分支 `dev-202611`，HEAD `98b4c539`（v8 文档起点＝v7 文档提交；`a1cb6633`→`98b4c539` 间**零代码提交**，v7 的全部代码事实仍有效——v8 对 `Program.cs:239-246`/`PassContract.cs:18-27`/`LinqChannelPassAdapter.cs:105`/`StructValueSemanticsAnalyzer.cs:77`/`ProjectPassRunner.cs:282-341`/`StatementTransformer.Conditionals.cs:373-433`/`InvocationExpressionTransformer.cs:790-827` 七处关键断言逐一抽查复核成立；**仍 52 个 Implemented pass**，`PassCatalog.cs:56-1030` 实测 52 条 descriptor；**[v8.1] 工作树现含本文档 + V-09 修复三文件 + 新测试**（`LocalFunctionLiftRewriter.cs`/`LocalFunctionLiftPreprocessor.cs`/`LocalFunctionLiftPassAdapter.cs`/`LocalFunctionLiftTests.cs`），均经 4552 全量测试 0 失败与探针 3 复跑验证、未提交；**v8 运行实测**：rt10 探针日志 `D:\cs\rt10-probe-v8.log`（默认链）与 `D:\cs\rt10-probe2-v8.log`（+`--no-local-function-strict`）与 `D:\cs\rt10-probe3-v8fix.log`（**[v8.1] 修复后复跑**），Yaml 探针 `D:\cs\yaml-probe-v8.log`，探针输出树 `D:\cs\rt10-probe-v8\.cs2j-preprocessed-src\` 保留半改写现场；代码事实基线仍为 `8268251a`，v6 复核时点 `a60afc75`，v5 编辑时点 HEAD `7e575512`，v4 时点 `fab1c483`，更早基线 `6b57a210`）
> 语料证据（v3 口径）：
> - **json 主语料 [v3 更换]**：`D:\runtime-10.0.12\src\libraries\System.Text.Json`（.NET Runtime 10.0.12 **真实源码**；库本体 352 .cs = src 308 + gen 20 + Common 24，另有 tests 333 .cs；v8 完成全链**首次实测**——默认链中止于第 4 pass（V-09）、逃生后中止于第 8 pass（CS2J0545），见 §3.2.4；完整逐 pass 运行统计仍待 V-09/CS2J0545 修复后的 M0 重跑建立——v2 及之前的全部 json 运行数据均在旧语料上测得，见 §3.2 换源说明）
> - json 旧语料（v1/v2 用，**时代错位已确认**）：`D:\csharp-sdk\json`——实为 .NET 3.x 时代 STJ 布局（`JsonClassInfo`/`ReflectionEmitMemberAccessor`，无 `JsonTypeInfo`），341 .cs；其全链日志（`D:\cs\20261104-json.log`、`20261104-json-final.log` 626/0）**测的是这份旧语料**
> - `D:\YamlDotNet-18.0.0`（18.0.0，261 .cs，**实测**）；`D:\cs\cs-sdk-78e810c6` 历史中间树与审计报告；`D:\cs\yaml-lib-probe.log` 等 6 份 YamlDotNet 运行日志
> 同行方案：`C:\Users\15097\Desktop\V202611042101` 目录下 deepseek-flash-4.1 / kimi3.1-pro / longcat2.5 / seed-evolving 四份同题文档（v2 吸收其经本仓代码/日志核证的论断、v4 另吸收 deepseek 版设计层，附录 D 全量对照；v3 的换源发现为本文档独立增量，四份均基于旧语料）
> **范围与验收边界 [v7 修订]：本方案主体只升级 C# → C# 预处理链，预处理产物仍是可编译、可测试的 C#，不是 Java，也不在预处理 PR 中修改 Java 转换器。但“下游瘦身”不再是可无限推迟的超范围事项：每个 JE/JCS 垂直切片由“预处理 PR + 配套 Java 删除/收缩 PR + 同一组行为验收”共同闭环，允许分 PR，不允许只完成前半段就宣告规则完成。Java 侧仅承担目标语法、映射与 compat 运行时职责；不得再在渲染后补做已声明由 JCS-1 保证的 C# 语义决策。**

---

## 0. 结论摘要

当前预处理链共 **52 个 pass**（全部 Implemented；`normalization-audit` 默认关闭，由 `--profile java-ready|audit` 追加）。v1 版文档时点的两个非泛型硬域（`Utf8JsonReader` ref-only ref struct、`JsonWriterHelper.Date.cs` 的 fixed+指针算术）**已被四个新 pass 攻克**：`ref-struct-span-field-memory-lift`（只读 Span 字段 → `ReadOnlyMemory<T>` 字段 + 同名 Span 属性 + `__cs2j_ToMemory` 写穿透，使 `Utf8JsonReader` 不再是 ref struct）、`fixed-span-to-array`（span-backed `fixed` → 托管数组 `fixed`，可写 Span 加 finally 回写）、`compat-method-replacement`（按全限定签名注册表把指针密集方法体替换为托管实现，内置 `JsonWriterHelper.ToUtf8`/`NeedsEscaping`）、`pointer-diff-inline`（同元素类型指针差值小助手内联 + 零引用声明清除）。json 模块（System.Text.Json）当前基线：Java 转换 626 类型成功 / 0 失败——**[v3] 该数字测自旧语料（.NET 3.x 时代布局，§3.2.1）；[v8] rt10 真源首次实测（§3.2.4）证伪其在 rt10 上的适用性：默认链在 `local-function-lift` 即中止（V-09 半改写 → 2 residual → 默认 strict Error），`--no-local-function-strict` 逃生后止于 `expression-desugar` CS2J0545 整树守恒门（W-17）——"json 0 失败"从"语料运气"（v2 结论 2）升格为"语料时代错位下的历史数字"**；且 rt10 上 `ToUtf8` compat 条目已确认零匹配（W-09）。

但"json 语料 0 失败"仍不等于"任意泛型密集库可过"，且 v3 确认了一个更根本的问题——**语料时代错位**：v1/v2 的 json 语料（`D:\csharp-sdk\json`）是 .NET 3.x 时代 STJ（`JsonClassInfo` 反射栈、无 `JsonTypeInfo`、无源生成器、无 Schema 导出），而"以 .NET 10 JSON 库为目标"的本方案实际要对齐的是 `D:\runtime-10.0.12` 的现代重写（普查结论见 §3.2：泛型密度 2-7 倍放大，且 compat 注册表已出现签名漂移 W-09）。v2 基于 json 全链运行统计（**旧语料实测，rt10 待 M0 重跑**）、YamlDotNet 18.0.0 **实测语料**（v1 仅为知识画像）与四份同行方案的交叉核证，把缺口认识从 v1 的"归约覆盖面"推进到三个更深层面（v3 增补 W-09/W-10）：

**层面 V：已落地机制存在正确性缺陷（活动 bug，修复优先于扩能力）**

| # | 缺陷 | 证据 |
|---|------|------|
| V-01 | GD-FIELD **singleton 形态** getter 每次调用重新执行初始化表达式（`=> InitializerText`），`static readonly` 的单例 identity 破坏；fixture 已把该行为固化为预期 | `Core/GenericPreprocess/GenericDeepenHolderGenerator.cs:54-55`；`tests/SampleGenericDeepenSolution/.../RuntimeBehaviorTests.cs:18-27` 自述 "guarantees per-closed-type construction, not singleton identity" |
| V-02 | 同一包含类型**多个有状态静态泛型字段** → holder 内 `__cs2j_gate/__cs2j_store` 按字段重复生成，生成代码不可编译 | `GenericDeepenHolderGenerator.cs:63-65`（AppendAccessors 每字段无条件发 gate/store，分组只到 holder 类型级） |
| V-03 | `GenericDeepenParamRef` record 的 `CompareTo` 只比 (kind, ordinal) 而 Equals/GetHashCode 含 Name → SortedSet 与 List 去重语义分裂（已有 `NamedParamRefComparer` 局部补丁证明缺陷真实发作过） | `GenericDeepenAnalyzer.cs:10-18, 284-293` |
| V-04 | GD 锚点物化把 abstract 成员改 `virtual` + `NotImplementedException`、为接口成员塞默认抛错体（公开 ABI 破坏）（**[v6] 行号漂移至 `:734-745`，仍在**） | `GenericDeepenRewriter.cs` MaterializeStubBody（`:734` 定义、`:745` 抛错，注释自称 "E10 precedent"） |
| V-05 | `ClosedTypeKey` 把 `NullableAnnotation` 以 `?` 后缀并入键：`List<string>` 与 `List<string?>` 成为两个运行时身份（NRT 非运行时类型信息，不应入键） | `Core/ClosedMetadata/ClosedTypeKey.cs` AppendNullableMarker |
| V-06 | closed-metadata collector 递归判型参（数组元素+类型实参）vs linker 只查顶层 `ITypeParameterSymbol` → `Deserialize<List<T>>` 与 `Deserialize<T>` 判定路径不一致 | `ClosedMetadataCollector.cs:580-599` vs linker `IsTypeParameter` |
| V-07 | `x as T`（T 类型参数）**两侧都不改写**：C# 侧 GD 只登记 token 站点（rewriter 只处理 IsPattern），Java 侧 as 路径无类型参数分支（**[v6] 核证：`RuntimeClassName` 对 ITypeParameterSymbol 显式返回 null（`:69-70`），三特判分支（泛型 Iterable/carrier/数组）均不命中类型参数，落 `:800` 兜底 `(e instanceof T ? …)`——Java 非法 instanceof 类型变量**） | `GenericDeepenAnalyzer.cs:1953-1961`；`TypeOperationTransformer.cs:69-70,778-800` |
| V-08 | `default(T)`（值类型约束 T）Java 侧 `GetDefaultValueForType` 兜底渲染 `"null"`——值类型实例化时零值变 null（拆箱 NPE/语义错误）；variance-audit 已有判词但只进报告（**[v6] 行号漂移至 `:978-1002`，兜底 `return "null"` 在 `:1002`**） | `TypeOperationTransformer.cs:978-1002`；`Core/VarianceAudit/ConstraintAuditor.cs:354-356`（"definite semantic error"） |
| V-09 | **[v8 rt10 实测] local-function-lift 半改写**：switch section（语句容器为 `SwitchSectionSyntax.Statements`，非 `BlockSyntax`）内的局部函数，lift 生成了类级 camelCase 方法并改写全部调用点，但**原声明不删除**——rewriter 的移除逻辑只处理 BlockSyntax 直接语句列表，`VisitLocalFunctionStatement` 对块外声明保守原样返回 → 产物同时含新方法与旧声明（I1 中间树质量破坏），residual 计数捕获后默认 strict Error 中止链；聚合诊断不带文件/行（`[Error] :0: 2 residual`），定位需人工 grep。旧语料零局部函数残留（Yaml v8 复测 0 residual）、rt10 一跑即中（`JsonElement.DeepEquals` switch-default 内双 static 局部函数）。**[v8.1] 已修复并验证**：新增 `VisitSwitchSection` 移除通道 + `UnremovablePlans` 原子性防护（不可移除容器 → 整文件回滚）+ 诊断带位置清单；4 新用例绿、全量 4552 测试 0 失败、探针 3 复跑 0 residual（§3.2.4） | 修复前：`LocalFunctionLiftRewriter.cs:146-153`（仅 `node.Statements[i] is LocalFunctionStatementSyntax` 移除）+ `:861-864`（保守返回）；实测产物 `D:\cs\rt10-probe-v8\.cs2j-preprocessed-src\...\JsonElement.ArrayEnumerator.cs:375,411`（lift 生成 `unorderedObjectDeepEquals`/`nameEquals`）vs `:1742,1745`（调用点已小写化）vs `:1759,1793`（原声明残留）；`LocalFunctionLiftPassAdapter.cs:87-99`（计数豁免口径 + 无位置聚合诊断）；源形态 `D:\runtime-10.0.12\...\Document\JsonElement.cs:1358,1404` |

**层面 S：合成层约束债——提升/合成族 pass 在 C#→C# 合成时丢约束/丢 arity（v1 多处"已接"判断不成立）**

| # | 缺陷 | 证据 |
|---|------|------|
| S-01 | `generic-deepen` 分析只遍历 `MethodDeclarationSyntax`：构造函数/属性/索引器/事件访问器/运算符体内的 `typeof(T)/is/as` **不分析不锚点** | `GenericDeepenAnalyzer.cs:1417-1449`（仅 MethodDeclaration+Invocation+TypeOf 三类遍历） |
| S-02 | 嵌套闭包 token 不可达：表达式体 lambda 委托形状不可解析 → CS2J1101 只告警且 pass 残差计数器仍报 0；STJ 实测 **37 处**（NumberHandlingTests/ConstructorTests 等） | `GenericDeepenAnalyzer.cs:2061-2071`；`D:\cs\20261104-json.log:521+`（"0 residuals" 与 37 条 1101 并存） |
| S-03 | closure-lift M2/M3 约束丢失三处：display class/holder 重声明 `TypeParameterList` 无 `WithConstraintClauses`；迁移方法 `typeParameterList: null` 硬编码（**[v6] 路径复核：HolderSyntaxGenerator 实为 `Core/ClosureLift/HolderSyntaxGenerator.cs:70-76`（非 `Lift/` 子目录）；a60afc75 三处仍在**） | `Core/ClosureLift/HolderSyntaxGenerator.cs:70-76`、`DisplayClass/DisplayClassSyntaxEmitter.cs:120-128`、`Lift/LambdaMethodLifter.cs:516` |
| S-04 | tuple 生成泛型 struct **零约束发射**（`Nullable<__P0>` 字段将 CS0453） | `Core/TupleToStruct/TupleStructFileContent.cs:48-60` |
| S-05 | local-function-lift 自有+外层类型参数 **arity 错位** → CS0308，无 pass 内回滚。**[v6] 状态细化（非全修）**：约束复制机制 `BuildConstraintClauses` **自初版即存在且已接线**（`:535` 调用、`:760-779` 定义），但残留三点——①**不过滤**：外层方法的全部约束子句都复制，含未被携带的形参（悬挂 where 子句风险）；②外层形参仍按名匹配携带（`:642-655`），无约束传递闭包（同 S-08）；③调用点补参（`:234-289`）与新 arity 的一致性缺红→绿 fixture，CS0308 风险未被测试锁定 | `Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs:535,624-662,760-779,234-289` |
| S-06 | thread-static 对**泛型容器中的 `[ThreadStatic]` 字段显式停用**（"per-closed-type sharing cannot be expressed in v1"）；存在 `RewriteGenericContainingTypes` 选项但默认关 | `Core/ThreadStaticRewrite/ThreadStaticAnalyzer.cs` InGenericContainer 分支 |
| S-07 | foreach-deconstruction 开放组件（KVP/元组元素含类型参数）整站保留，并被 foreach-source **CS2J5420 硬门**放大 | `Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs:383-417`（强制 INamedTypeSymbol） |
| S-08 | async 泛型携带的类型参数**约束传递闭包**不完整（`where T : IFoo<U>` 漏带 U → CS0246） | `Core/AsyncNormalize/AsyncNormalizeAnalyzer.cs` ResolveGenericCarry 邻域（按名匹配无闭包） |

**层面 W：接线与审计缺口（机制已有但未闭环）**

| # | 缺口 | 证据 |
|---|------|------|
| W-01 | 约束**审计码体系已存在**（VA020-027 + struct/new()/unmanaged 依赖位点计数 + 判词），但未接 java-ready 门禁；`PassChainFactory` 构造 `GenericDeepenOptions/ClosedMetadataOptions` 时不传 `config.Strict` → **java-ready ≠ strict** | `Core/VarianceAudit/VarianceAuditDiagnostic.cs:29-36`、`ConstraintAuditor.cs:354-356`；`CLI/Preprocessing/PassChainFactory.cs`（对照 `NumericBoundaryOptions` 接收 strict） |
| W-02 | 现有 10 个 NORM-\* 探针**全部非泛型域**；closed-metadata 实测 **281 residuals / 369 dynamic skipped / 0 consumer sites linked** 仍链成功（CS2J1220 Warning 放行） | `Core/Normalization/ResidualRegistry.cs:11-23`；`D:\cs\20261104-json.log:645` |
| W-03 | 诊断码撞号：**CS2J1203 一码四义 [v6 复核订正]**（closed-metadata "JavaTargetUndecidable" `ClosedMetadataDiagnostics.cs:55` / extension-methods "ExternalHostNoMapping" `ExtensionMethodResolveDiagnostics.cs:75` / extension-block "ExternalMetadata" `ExtensionBlockDiagnostics.cs:57` / thread-static "StructMutationNotLiftable" `ThreadStaticDiagnostics.cs:49`）；另 extension-methods 的 12xx 段与 closed-metadata 撞段、linq-channel 的 13xx 与 attribute-normalize 撞段 | 四常量位置如左述（a60afc75 实测） |
| W-04 | `linq-channel` 的真实引用增强路径**已实现但调用点被一行注释**：`// EnsureEnhancedReferences(tree); // NEGATIVE-CHECK (temporary)` | `CLI/Preprocessing/Adapters/LinqChannelPassAdapter.cs:105` |
| W-05 | closed-metadata 消费面**硬编码 STJ**（4 个 Serialize/Deserialize 方法名 + `GetTypeInfo` 共 5 入口），`ConsumerSurfaces` 选项未实现——YamlDotNet 序列化入口不在枚举面 | `ClosedMetadataCollector.cs:42, 279-288` |
| W-06 | **YamlDotNet 库默认链在 `null-coalesce` 中止**：`?? []`（`TypeConverterCache.cs:39` 的集合表达式）无法处理 → 链失败，而能处理 `[]` 的 `expression-desugar` 排其后 → **泛型 pass 在默认链上从未真正执行**（单 pass 复跑才见 2 `new T()` / 2 `new T[n]` / 100 anchors）。**[v8] 当前 HEAD 复测精确复现**：`null-coalesce: 63 transformed, 149 ?? + 5 ??= rewritten, 1 residual → Error`，中止行级一致（`D:\cs\yaml-probe-v8.log`）；同跑证明 Yaml 无 V-09 形态（local-function-lift 4 lifted / 0 residual），rt10 与 Yaml 的链鲁棒性失败**互为不同形态**（G-18 的两个独立实证） | `D:\cs\yaml-lib-probe.log`（"1 residual ??/??= ... Error"）；`D:\cs\yaml-generic-only.log`、`yaml-gd-only.log`；**[v8]** `D:\cs\yaml-probe-v8.log` |
| W-07 | Java 侧闭型注册**已接线**（v1 文档"未接线"论断过时）：`TypeHelper.registerClosedType/resolveClosedType/closedCacheKey` + `ClosedTypeMetadataTest`（幂等/并发/null）；但 `ClosedMetadataRegistrationGenerator.cs:9-11` 注释仍写 "lands in a separate PR" 未更新 | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java:1255,1270`；`ClosedTypeMetadataTest.java` |
| W-08 | 语义 pass 默认 **TPA-only 引用集**：泛型绑定假阴性（STJ 实测 `ConvertedType=null` → 37 CS2J1101 的根因之一；variance-audit 跳过 **126 个** baseline-error 文件）；仅 extension-methods/async-normalize 自行补包引用，linq-channel 差一行注释（W-04） | `CLI/ProjectUnsignedTypePreprocessor.cs:81`；`D:\cs\20261104-json.log:652` |
| W-09 | **compat 注册表签名漂移** [v3 新增]：内置条目 `JsonWriterHelper.ToUtf8` 的键/参数表拟合的是旧语料（3.x 时代 `JsonWriterHelper.Transcoding.cs:28` 的 `ToUtf8(ReadOnlySpan<byte>, Span<byte>, out int, out int)` 4 参形态）；Runtime 10 真签名是 `JsonWriterHelper.cs:291` 的 `ToUtf8(ReadOnlySpan<char>, Span<byte>, out int written)` **3 参 char-span**——条目在真实语料上**零匹配静默空转**（`NeedsEscaping` 键则仍匹配）。注册表无"声明类型在语料中存在但零匹配"的漂移告警 | `Core/CompatMethodReplacement/CompatMethodReplacementRegistry.cs:306-330`（4 参 byte-span 键）vs `D:\runtime-10.0.12\...\Writer\JsonWriterHelper.cs:291` |
| W-10 | **csproj 门控 Compile 与外部共享源未进入预处理输入模型** [v3 新增]：Runtime 10 STJ 用**文件级** TFM 门控（`<ItemGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETCoreApp'">` 收 `JsonReaderHelper.net8.cs`，非 CoreApp 组收 `netstandard.cs`——同一 `partial class JsonReaderHelper` 的两套实现）与 **csproj 外部共享源**（`$(CoreLibSharedDir)`：FeatureSwitchDefinitionAttribute/ValueListBuilder；`$(CommonPath)`：HexConverter/ArrayBuffer/ValueStringBuilder/PooledByteBufferWriter）；gen/ 还有 `DefineConstants=ROSLYN4_x_OR_GREATER`、`.targets` import、analyzer 型 ProjectReference。目录 glob 输入必然产出**重复成员/缺源中间树**；L1 的 `MsBuildProject` Compile 求值从"可选上移"升格为 **rt10 语料硬前置**（`.slnx` 解析已支持，`ProjectPreprocessSource.cs:32,74`）。**[v8] 运行级证据**：conditional pass 对 `$(NetCoreAppCurrent);$(NetCoreAppPrevious);$(NetCoreAppMinimum);netstandard2.0;$(NetFrameworkMinimum)` 等 **MSBuild 属性 TFM 值报 "skipped unresolvable TFM value" Warning 后仍收文件**（`Typeforwards.netcoreapp.cs`/`ReflectionExtensions.cs`/`ThrowHelper.cs` 等，externals.csproj 的 Common 文件同病）——门控机制有意识但属性求值缺失，是"求值不完整"而非"无求值" | `D:\runtime-10.0.12\...\src\System.Text.Json.csproj:384,394-399,435`；gen\System.Text.Json.SourceGeneration.Roslyn4.4.csproj；**[v8]** `D:\cs\rt10-probe-v8.log`（TFM Warning 群） |
| W-11 | **两入口 profile×disable 语义分裂** [v5 新增，kimi 论断经本仓 HEAD 核证成立]：`convert-project` 路径 java-ready/audit 追加审计时**显式 `--disable-pass` 赢**（disable 列表含 normalization-audit 则不追加，注释自述 "an explicit --disable-pass still wins"）；`preprocess` 路径**无条件追加**（M0-b 注释 "append ... including an explicit --pass list"），与显式 disable 交叠后 `PassPlanResolver` 报 "cannot be both enabled and disabled" → 退出 1。同一 CLI 同一 profile，两条入口一个静默放行一个配置失败——"审计必在"与"用户可显式排除"两种语义并存 | `CLI/Program.cs:272-287`（convert 分支）vs `CLI/Program.cs:4183-4189`（preprocess 分支）+ `CLI/Preprocessing/PassPlanResolver.cs:60-65`（冲突检测） |
| W-12 | **源生成器输出未物化** [v5 新增]：预处理额外树只合成 global using，不执行/物化 source generator 输出（kimi §2.2 差异 4，本仓现状一致）。CM 侧只把 `JsonSerializerContext` 派生与 `[JsonSerializable]` 作排除+警告（CS2J1204，CM-SRCGEN）。后果：rt10 tests（333 .cs）中依赖 STJ source-gen context 的测试、YamlDotNet `StaticGenerator` 测试 helper，在 B/C 路差分（预处理树跑原测试/预处理测试）上**无法编译**——四路差分验收的硬前置缺失；反射路径与源生成路径还可能互相误注册 | `Core/ClosedMetadata/ClosedMetadataCollector.cs:19,39-40,129,145-150`（仅排除）；`CLI/Preprocessing/ProjectPassAdapter.cs`（额外树合成无生成器执行） |
| W-13 | **消费面 guard 无执行时序模型** [v5 新增]：closed-metadata 的 bootstrap guard 插在序列化调用所属 statement 的**词法前方**，residual 只校验"guard 相邻/未误入表达式树"，不证明注册与消费处于同一执行路径。延迟 IEnumerable/IQueryable、返回 delegate 稍后调用、async iterator 真正枚举时执行、工厂方法内部稍后创建、表达式树之后编译/执行——这些 deferred 场景下 guard 可能在消费**之后**才执行或根本不在执行路径上（kimi CM-03） | `Core/ClosedMetadata/ClosedMetadataConsumptionLinker.cs:52-95`（语句前插入）；`ClosedMetadataResidualScanner.cs:306-377`（仅相邻性检查） |
| W-14 | **合并 compilation 项目边界丢失 + 无 ABI 变更清单** [v5 新增]：(a) 全树单 compilation 使项目边界消失——internal 可见性、`InternalsVisibleTo`、跨项目重复类型名、测试/生产工程分离语义不再等同于真实构建（kimi §2.2 差异 1；解决方案级路径 YamlDotNet.sln 的 CS2J2018 中止与 csharp-sdk 多项目全链均受影响；`_Cs2jClosedTypes.<Project>.cs` 虽按工程分文件，但绑定分析仍共享一个 compilation）。(b) GD-MTOK 尾参/GD-STATIC 提升/V-04 stub/EB 桥接/java-keywords 改名等**公开签名改写无登记清单**——java-ready 无 ABI diff 门禁（kimi GD-06/6.5 ApiManifest 设计） | (a) `CLI/Preprocessing/ProjectPassRunner.cs:282-312`（单 compilation）；(b) 无对应产物（缺 `api-manifest.json`） |
| W-15 | **两阶段协同无回归门（v5 后 15 提交实测）** [v6 新增]：v5 文档之后的 15 个修复中，2 个预处理修复（2aa7aa8f/51e505cd）与 13 个 Java/compat 修复（含定稿时 8268251a）**同批耦合**收敛同一真实链（MSAGL）的 javac 错误（75→24→12）——"断言回调 lambda void 语义/丢弃赋值/别名导入"这类形状同时惊动 expression-desugar、ref-out-in、LambdaTransformer、StatementTransformer、compat 五处，且没有任何门证明"预处理改写后 Java 侧特例是否仍被需要"。预处理扩能力缺少与 Java 侧的**负 diff 对账**，简化无法度量、回归靠人肉双改 | 2aa7aa8f/51e505cd 及 13 个 fix(java)/fix(compat) 提交；无对应报告字段/测试门 |
| W-16 | **fail-fast 口径分裂 + 盲区巡逻底册漂移** [v6 新增]：统一构造点 `NormalizationRequired.At(...)` 实测仅 **15 处调用（2 个文件）**，另有 **54 处裸 `throw new NotSupportedException`（18 个 Transformers 文件）**绕过统一构造点（消息/定位不一致，§7.1-7"42 个 throw 点"映射底册已不可复现）；其中多少属"预处理应消解"、多少属"IR 本身不支持"无分类清单，L0 盲区巡逻测试缺可靠输入 | `Transformers/NormalizationRequired.cs:13`（统一点）；15 点＝UnaryExpressionTransformer 14 + BinaryExpressionTransformer 1；54 点分布见附录 H |
| W-17 | **[v8 新增] rt10 双语料链中止实证——G-18 从"Yaml 单语料"升格为"两语料三形态"**：①默认链止于 `local-function-lift`（V-09 半改写 → 2 residual → 默认 strict Error，`--no-local-function-strict` 是唯一逃生门）——**[v8.1] V-09 修复后实测关闭**（探针 3 复跑 `0 residual`，链越过第 4 pass）；②止于 `expression-desugar` CS2J0545 **整树守恒门**（修复前 96 init 残差 vs 88 per-file 记账；修复后 75/68，unpaired 收窄为 `JsonSchemaExporter.cs` 28/22 + `PropertyRefCacheBuilder.cs` 1/0——per-file 记账与整树扫描口径分裂，**G-41"残差守恒"的现存先例**在 rt10 上发作；该门无条件 Error、无旋钮，归因待 M0）；③Yaml 复现 W-06 原形。**"json 0 失败"在 rt10 上实测失效**，rt10 的真实失败清单（CS2J0545 归因后重跑逐 pass 落档）是 M0 的入口任务而非结果 | `D:\cs\rt10-probe-v8.log`（修复前默认链，止于第 4 pass）；`D:\cs\rt10-probe2-v8.log`（+`--no-local-function-strict`，止于第 8 pass，CS2J0545 96/88）；**[v8.1]** `D:\cs\rt10-probe3-v8fix.log`（修复后默认链：越过第 4/5 pass，止于 CS2J0545 75/68）；`ExpressionDesugarPassAdapter.cs:359-377`（C24 IN-13 守恒门，无条件 Error）；`D:\cs\yaml-probe-v8.log` |

**层面 J：Java 承接复杂度回灌——预处理该吸未吸的 C# 形状（v6 新增，a60afc75 实测；8268251a 增补 J-02 证据）**

> 方法论反转：v1-v5 的视角是"预处理把 Java 不支持的构造消解掉"（以 Java 硬门禁为靶子）；v6 增加反向视角——**盘点 Java 转换器为承接未预处理形状而内建的复杂机制，凡能在 C#→C# 阶段（符号可得、可编译验证、可 B 路差分）确定性消解的，回灌为预处理规则**。目标不是"0 转换失败"（已在旧语料达到），而是**Java 转换器的复杂度净下降**。以下 7 项全部有 v5 后新增代码为证（§3.5/附录 H）。

| # | Java 侧承接机制（规模/证据） | 根因（预处理缺口） |
|---|------------------------------|--------------------|
| J-01 | **渲染期文本重建闭型**：`JsonDeserializeRewrite.cs`（293 行，v5 后新增）对**已渲染的 Java 文本**做前缀匹配（`JsonConvert.deserializeObject(`），按目标槽位（局部/返回/赋值）改路由到 typed compat 入口；类型参数/非 List 集合返回 null 退回擦除+强转——擦除在渲染期销毁的闭型信息用字符串手术补回，脆弱且与 CM 注册面重复 | closed-metadata 消费面只硬编码 STJ 5 入口（W-05）；Newtonsoft `JsonConvert.DeserializeObject<T>` 与同类"序列化/工厂"入口无注册、无 C# 侧 token 物化；类型参数站点仍是 GD 盲区（G-02c） |
| J-02 | **集合擦除一致性 Java 后处理**：`CompatCollectionConformance.cs`（732 行）对生成集合类做事后签名手术——`Add(T)`→`add(Object)`+体内强转、`GetEnumerator()`→`iterator()`、`Remove` 改名 `remove_2`+补 boolean 桥、SyncRoot 补齐；注释自证跨工程调用点只能靠符号再推名（"新 Context、空注册表……必须能自行推导出与声明端一致的名字"），b5adaaea 再补 IList<T>/IList 双实现碰撞、**8268251a 再补泛型/非泛型 Current 并存碰撞（v5 后第 6 个同族修复）** | EB 桥接目前只覆盖 C# 侧同类型擦除冲突（R8 EB-01~06）；"用户集合实现 → compat 映射接口形状"的一致性在 C# 侧无人 owner，拖到 Java 渲染后用文本后处理补救 |
| J-03 | **强制适配网约 1666 行**：`ArgumentTransformer.Coercion.cs` 1098 + `IListCoercion.cs` 266 + `IterableCoercion.cs` 217 + `SpanCoercion.cs` 85；v5 后 5 次 compat/codegen 修复全在补这张网（OfType 过滤语义 69b4db22、IGrouping 迭代 Map.Entry CCE 0a183e9a、IList<T>/IList 擦除碰撞 b5adaaea、数组 IList 适配 f271d4ad、CopyTo 参数校验 bd3966a2） | 数组/IList/IEnumerable/Span/IGrouping 跨形状隐式转换在 C# 侧保留隐式形态，Java 渲染期按接收器类型逐站点猜测适配；foreach-source/array-method 已开此范式但未覆盖实参/返回/赋值槽位 |
| J-04 | **条件副作用在 Java 渲染期做控制流线性化**：`StatementTransformer.Loops.cs`（a2d3a6f0）把 while 条件里属性 `++/--` 的读改写预语句从循环前移到循环体顶（`while(true){ 读改写; if(!C) break; body }`）——此前放循环前只执行一次造成**真实死循环**（XsdDateTime `while (++start.Value < _length)`）；同类 getter pre-statement 泄漏另有专门回归测试（GetterPreStatementLeakTests） | 条件位置（while/for/do/if/`?:`）的副作用求值顺序线性化是语句级改写，continue/break 交互需要控制流推理；预处理无此 pass，Java 侧在文本发射层做且已出语义 bug |
| J-05 | **BCL 重载/值语义逐案补丁**：`string.Concat(IEnumerable<char\|string>)` 展平（a60afc75）、checked IO 异常剥离（f271d4ad）、Decimal double 转换尾零（e8e69c94）、保留属性 bean getter 发射（48571b2f）——每个 C# 重载决议/值语义差异都是一个 Java 侧特判 commit | C# 重载决议与 BCL 语义差异在调用点未显式化；compat-method-replacement 已证明"条目化方法体置换"范式可行，但注册表只覆盖 unsafe 域 2 条 |
| J-06 | **fail-fast 口径分裂**（W-16）：统一构造点 15 处 + 裸 `NotSupportedException` 54 处；旧底册 42 不可复现 | 预处理探针的反向枚举（"有 Java 门无 NORM 探针"）缺准确底册；部分裸 throw 是 IR 不支持而非预处理责任，两者未分类 |
| J-07 | **预处理生成物反向依赖 Java 逐名特判**：tuple/anonymous/holder 生成的 C# 发射 `EqualityComparer<T>.Default.Equals/GetHashCode`，Java 侧在 `InvocationExpressionTransformer.cs:790-815` 硬编码降为 `Objects.equals/hashCode`；`EqualityComparer<T>` 静态类本身在 TypeMappings 仍无映射（G-11 半修） | 生成物与 Java 侧之间是**口头契约**：哪些生成标识符有 Java 特判承接无清单、无机检、不进 api-manifest；生成物改形状即破 Java 侧（v5 后已发生一次：2aa7aa8f 修的别名导入即生成形状漂移） |

**核心判断（v6 续）**：v1-v5 的五层缺口（V 正确性 / S 合成债 / W 接线 / G 泛型规则 / 框架层）全部继续成立且经 a60afc75 复核未变；v6 新增第六个事实——**"Java 0 失败"之后，复杂度的主要存量已从"转换不过"转移到"Java 转换器内的 C# 语义模拟器"**（J-01~J-03 可计部分 2691 行＝293+732+1666，另加 J-04 分散在 Statement 层的线性化位点；且 v5 后的修复密度证明它们仍在高频长肉）。这部分复杂度天然属于预处理：决策点有符号、产物可被 `dotnet build`/B 路差分验证、规则可条目化。升级方向由此增加一条硬原则（I10）：**JE/JCS 减负规则必须指认将被退休的 Java JM 分支，并以最终树 guaranteed + 配套负 diff 为验收**；[v7] M0 正确性、通用语法、框架/审计/性能工作不强制寻找可删代码，但不得新增下游语义补偿。

**核心判断（v8 增）**：v3 换源时预判的"换源暴露新失败"（风险 15）**已经兑现为实测**——rt10 首跑在链的两个不同位置中止（V-09 半改写 / CS2J0545 守恒门），两个失败形态都指向同一结构性教训：**"0 失败"从未被门禁证明过，只是语料没有踩到**。V-09 证明半改写（生成侧成功、删除侧失败）这类**非原子改写**能穿透现有 per-file 统计（28 transformed / 0 skipped 全绿）只在整树 residual 计数上现形；CS2J0545 证明守恒门本身已在链上存在（C24 IN-13），但 per-file 记账面与整树扫描面**口径不一致时失败的是链**而不是记账。这两点分别强化了 R13（最终树闭包）与 G-41（守恒不变量）的优先级：**守恒不能只做门禁，还要做记账口径的单一实现**；也再次验证 v7 把 VS-0（契约）排在一切能力扩张之前的正确性——没有最终树证据链，"0 失败"连过程指标都算不上。v8 据此把 rt10 基线建立的次序修正为"先修 V-09、归因 CS2J0545，再谈重跑落档"（§8.1 M0 行）。

**核心判断**：v1 的"泛型归约零审计"判断仍然成立（10 探针全非泛型），但 v2 新增两个更紧迫的事实——(a) 已落地的泛型补偿机制（GD-FIELD/CM）自带正确性缺陷，json 语料恰好未触发（单字段、无 `List<string?>` 键差、泛型测试方法少）；(b) **合成层**（closure/tuple/local-function/async 的提升与生成）在 C#→C# 阶段丢约束/丢 arity，错误在中间树就爆发（CS0246/CS0308/CS0453），不再等到 Java。[v5] v3/v4 之后再补第三个事实——**框架层**（而非泛型规则层）缺口：同名 profile 两条 CLI 入口语义分裂（W-11）、源生成器输出不物化使 B/C 路测试工程无法编译（W-12）、消费面 guard 只保证词法相邻而非执行时序（W-13）、合并 compilation 丢失项目边界且签名改写无 ABI 清单（W-14）——"java-ready"的可信度当前还依赖入口选择与树组织方式，而非仅依赖泛型规则完备性。升级方案为**目标层 + 五层执行**（继承 v1 框架，v7 重排——先修正确性，再以垂直切片兑现下游简化）：

```text
目标层      JCS-1 最终树后条件 + preprocess-manifest 能力握手 + JM 退休账本 [v7]
L0 契约层   NORM 探针族扩容（26 码 [v4 VARIANCE-USE；v5 SOURCE-GENERATOR/PUBLIC-ABI；
            v6 COLLECTION-SHAPE/CONDITION-EFFECT；v7 CALL-SHAPE/VALUE-COPY/SEMANTIC-CLOSURE]）
            + CS2J14xx 新诊断段 + 撞号迁移（CS2J1203 实测一码四义，v6 订正）
            + PassContract 推广（9 字段已落地、10 pass 已挂载；补 VerificationProfile 与派生断言；
              v7 增 CapabilityIds/Phase/FinalizerFor，支撑最终闭包）
L1 编译层   真实引用集 spine 化（含打开 W-04 一行注释）+ degraded 显式化 + 引用画像报告
            + [v3] csproj Compile/TFM/外部源求值进输入模型（W-10/G-26，rt10 硬前置）
            + [v5] 源生成器输出物化（R9/W-12）+ 逐项目 compilation 工作区终态（W-14a，M3 决策点）
L2 验证层   四路差分验收（A 原库原测 / B 预处理库+原测 / C 双预处理 / D 原库+预处理测）
            + 残差 ratchet（只许减不许增）+ java-ready 严格化（W-01 strict 接线/不可绕过/幂等逐字节）
            + [v5] ABI diff 门禁（api-manifest.json 无未批准项，R10）+ 两入口 profile 语义统一（W-11）
            + [v6] Java 侧净简化对账门（JE 规则须附 transformer/compat 负 diff，W-15；fail-fast 底册归一，W-16）
            + [v7] manifest 正负握手 + 最终树语义闭包 + JM enforce/retire/ratchet + 总复杂度账本
L3 能力层   V 层缺陷修复（GD-FIELD 规范形/CM 键修/闭包补锚）+ S 层合成债统一解析器
            + v1 的 P1-R1~R8 / P2-R1~R3 规则族 + 消费面配置化（ClosedTypeConsumerSurfaces.json）
            + [v3] compat 注册表漂移探测与条目版本锚（W-09/G-25）
            + [v4] 三规则族 pass 化：generic-intrinsic-lower（GI）/variance-use-site-normalize（VR）
              /generic-reflection-audit（RG 只读面）——规则表 §6.4、插入序 §6.5、构造目录附录 E
            + [v5] 消费面 execution kind 执行时序模型（CM-LINK-3/W-13）+ ApiManifest 登记（R10/W-14b）
            + [v6] **Java 减负吸收族 R11（JE-01~07）**：闭型消费扩面/集合一致性 C# 侧桥接
              /collection-shape-normalize/condition-effect-linearize/BCL 重载条目化
              /fail-fast 归一/生成物 Java-中立——每条指认被删除的 Java 机制（附录 H）
            + [v7] R12~R15：JCS manifest/最终闭包/退休协议 + JE-08 byref-out 条件
              /JE-09 调用形状/JE-10 struct 值拷贝；按 VS-1~VS-5 独立退场
L4 性能层   统一中间树默认化 + 增量缓存 + 选择性 verify（M5 未竟，泛型整树 pass 收益最大）
```

验收目标（详见 §7）：**[v3] `D:\runtime-10.0.12` 的 System.Text.Json 全模块（src + gen + Common，rt10 真源）**与 `YamlDotNet 18.0.0` 经本链 `--profile java-ready` 预处理后：

1. `preprocess-residual-report.json` 中泛型类 `NORM-GENERIC-*` 与本切片 JCS 探针计数为 0（未立项观测族可 PreserveAndWarn，但必须带 owner/文件/行/原因；必要 capability 不得 partial/unsupported）；
2. **四路差分中 B 路**（预处理后的库 + 原测试工程）`dotnet build`/`dotnet test` 零失败——"预处理语义等价"的最强可执行代理指标；
3. Java 转换产物 `mvn compile` 通过、JUnit 全绿；**[v7] 已被本切片指认的 Java 补偿分支完成删除/收缩，而不是仅证明“可以删除”。**

### 0.1 v7 总纲：从规则堆叠升级为两阶段能力契约

v6 的 V/S/W/J 证据与 R0~R11 规则继续作为实施底册，但主路径作三项改变：

1. **目标从“pass 跑完”改为“最终树满足 JCS-1”**。pass 数、改写数和 0 Java 失败都只是过程指标；只有最终 C# 树上可机检的后条件，才能授权下游删除补偿逻辑。
2. **交付单位从“单条预处理规则”改为“垂直切片”**。一个切片必须同时包含 C# 改写、最终树审计、版本化能力声明、Java 负 diff、C#/Java 双侧行为等价与回滚开关。
3. **完成定义从“预处理合入”改为“Java 补偿退休”**。允许分 PR 与短暂双形态窗口，但不允许无限期冻结；冻结必须有 owner、原因、复审波次和覆盖缺口。

#### 0.1.1 JCS-1：Java 友好 C# 规范子集

JCS-1 **不是另一套 IR，也不是把所有 C# 语法都展开**；它仍是普通 C# 源码。它按构造族规定“转换器可以假设什么”，未声明保证的语法仍可由 Java 通用路径承接。每个能力只有 `guaranteed / partial / unsupported` 三态：只有最终树 `guaranteed` 且残差为 0 的能力可以触发 Java 分支退休；`partial` 必须保留下游兜底并列出残留，`unsupported` 在 java-ready 下失败。

| 能力 ID | 最终 C# 树后条件 | Java 侧保留职责 |
|---------|------------------|-----------------|
| `JCS.WORKSPACE` | 输入由 project × TFM × configuration 求值；Compile、引用、条件符号、外部共享源与源生成输出完整且去重；无 degraded | 不重建项目语义，不猜测缺源/缺引用 |
| `JCS.SYMBOL` | 语义分析基于健康 compilation；公开 ABI 变化与全部生成符号登记；声明端/调用端使用同一符号身份 | 名称映射、import、Java 可见性与目标语法发射 |
| `JCS.CALL` | 委托调用、方法组、扩展调用、命名/可选/params 实参及需要显式化的泛型实参已按 C# 重载决议定型；无语法级“猜 callee” | **BCL/compat 成员映射仍归 Java**；只渲染已定型调用，不再重复 C# 重载决议 |
| `JCS.EVAL` | 条件、短路链、实参、ref/out 读回与属性/索引器读改写的求值顺序已显式化；无跨语句悬挂的 pre/post-statement 决策 | 只发射语句与控制流，不重排 C# 副作用 |
| `JCS.VALUE` | 用户 struct 在赋值、实参、返回、字段/数组元素按值读取等值拷贝槽位已插入受控 copy，或明确登记免拷原因/不支持；copy 方法与调用点同计划生成 | 将 copy 调用映射到 Java 方法；不再自行判断何处需要 C# 值拷贝 |
| `JCS.GENERIC` | 类型 token、闭型元数据、约束携带、静态泛型状态与反射消费面符合 R0~R10 后条件 | 擦除后的 Java 类型渲染、compat TypeHelper 运行时 |
| `JCS.CONVERSION` | 已被 JE-02/JE-03 接受的数组/集合/Span 形状转换在源树显式存在；声明端与调用端桥接一致 | compat 适配器运行时与未覆盖开放世界兜底；已退休分支不得再命中 |
| `JCS.CLOSURE` | 晚期 closure/async/iterator/member/tuple 等生成物经过**最终树**语义闭包扫描；没有早先 pass 已消除、后续 pass 又生成的无 owner 构造 | 消费稳定生成形状，不按私有命名逆向恢复语义 |

**能力声明不得靠 pass 名称推断。** 例如执行过 `struct-value-semantics` 不等于 `JCS.VALUE=guaranteed`；只有最终树扫描证明目标槽位无残差、计划无未登记 skip，才可声明保证。

#### 0.1.2 职责分界：消除重复决策，而不是搬运代码

| 层 |  owns | 明确不做什么 |
|----|-------|--------------|
| preprocess | C# 语义决策、求值顺序、源级显式转换、生成物计划、最终树残差与能力证明 | 不生成 Java 字符串；不复制 TypeMappings；不承接纯 Java 命名/导入 |
| Java converter | Java 语法与类型渲染、BCL→compat 映射、命名、import、目标版本适配 | 对已 `guaranteed` 的构造不再做 C# 语义推断、文本手术或声明/调用点二次协商 |
| compat runtime | 运行时集合、反射、文本、数值、异步等 .NET 行为 | 不承担本可在编译期消除的源码形状推断 |
| `Core/Analysis` 共享事实 | 类型参数身份、值拷贝、调用形状、求值顺序等跨阶段事实的**单一实现** | 不由 preprocess 反向调用 `Transformers/*Helper`，也不再“移植一份”后各自漂移 |

现有 `StructValueSemantics/ValueCopyFacts.cs` 自述“口径移植 `StructCloneHelper`”，同时 `StructValueSemanticsAnalyzer.cs` 又直接调用 `Transformers.StructCloneHelper`（当前 `:77`），说明职责方向已经倒置。v7 要求先把共享事实上移到 `Core/Analysis`，再谈删除 Java 分支；否则只是把同一套判断复制到两个阶段。

#### 0.1.3 两阶段握手与防绕过

```text
原 C# / csproj / sln(x)
  → 求值工作区 → JCS pass 链 → 最终树语义闭包
  → preprocessed C# + preprocess-manifest.json + api-manifest.json + residual report
  → 转换器启动时校验 subsetVersion / capabilities / inputHash / contractHash
  → 简化后的 Java renderer + compat runtime

每个已接受能力：
inventory → shadow → enforce → retire → ratchet
基线立账    双跑对比   禁回退    删除兜底   永久防复发
```

- `preprocess-manifest.json` 是**能力与身份握手**，不是承载隐藏类型信息的旁路 IR；Java 发射所需的语义必须已经体现在 C# 源码或可编译生成物中。
- 正常 `convert-project` 链内直接传递同一握手对象并落盘；`--no-preprocess` 消费已有树时必须重新读取并校验 manifest。java-ready 下缺失、过期、hash 不匹配、必要能力非 guaranteed 均为配置错误，取代当前 `Program.cs:239-246` 的“Warning 后忽略 profile”。default profile 仍允许原始树转换，但报告 `subsetStatus=unverified`；**尚未 retire 的 JM 兜底保持启用，遇到已 retire 构造则以 CS2J1410 明确要求先 preprocess**，不承诺无预处理的原始树永远走旧补偿路径。
- Java 侧为补偿分支建立稳定 `JM-*` ID（不再只用易漂移的 `文件:行号`）；每个 ID 映射到所需 capability、退休状态和覆盖语料。enforce 阶段该分支命中即失败，retire 阶段删除，ratchet 阶段禁止同义分支回流。

#### 0.1.4 首批垂直切片与优先级

通用泛型债不必全部完成才允许 Java 瘦身。除真实依赖外，以下切片可独立交付、独立退休，避免 v6 把所有 JE 都堵在一个巨型 M3 末尾：

| 切片 | 内容 | 首选理由 | 退场条件 |
|------|------|----------|----------|
| VS-0 契约 | JCS-1、manifest、JM 底账、总复杂度账本、最终树闭包 | 所有后续删除的共同前置 | 缺/旧/假 manifest 在 java-ready 下不可绕过 |
| VS-1 求值顺序 | JE-04 + JE-08：循环/条件副作用、byref/out 读回与 out-var 作用域 | 已有死循环与条件读回实证；C# 编译器和 B 路可直接验证 | Loops 与 Conditionals 被指认 JM 分支 retire，红绿行为夹具全绿 |
| VS-2 struct 值拷贝 | JE-10：共享 ValueCopyFacts、补齐槽位、声明/调用点同计划 | 已有 preprocess 机制，迁移成本低于新造 pass；可消除双实现 | Java 不再决定拷贝站点，仅渲染 copy 调用；alias/ref/override 反例全绿 |
| VS-3 调用形状 | JE-09：delegate Invoke/方法组/可选命名参数/params/泛型推断显式化；JE-01 闭型消费按依赖并行 | 直接收缩巨型 Invocation 枢纽中的 C# 语义分支，且不触碰纯 BCL 映射 | 被指认 JM 分支 retire；BCL 映射行数不作为“瘦身”伪造收益 |
| VS-4 集合与转换 | JE-02 先行；JE-03 按 array/list、IEnumerable、Span、IGrouping 分族验收 | 732 行后处理与适配网收益大，但开放世界风险也最大 | 每个形状族独立 guaranteed→retire；不得一次声明整个 coercion 网可删 |
| VS-5 泛型纵深 | R0~R10 未完成项与其余 JE | 继续作为任意泛型密集库基础能力 | 按原 V/S/W/G 门与四路差分退场 |

`condition-effect-linearize` 与 `collection-shape-normalize` 不再是“一个大开关覆盖全部形状”的候选 mega-pass：前者复用统一 `EvaluationOrderFacts` 按循环、if/短路、byref/out 分规则开启；后者按转换族分别登记 capability 与残差。未证明的形状继续走原路径，不阻断已证明切片退休。

---

## 1. 文档定位与阅读路径

- 本文件是 `docs/preprocessor-upgrade-plan.md`（2026-11-01 版，下称"v1 方案"）的**预处理能力升级主方案**；首版聚焦泛型，本次 **v8** 扩展为“JCS-1 规范子集 + 两阶段能力握手 + Java 补偿退休 + **实测基线驱动**”。v8 相对 v7 的核心增量：①**§3.2.4 rt10 实测首跑**——v3 换源以来第一次全链运行，默认链止于第 4 pass（**新缺陷 V-09**：switch section 内局部函数 lift 半改写）、`--no-local-function-strict` 逃生后止于第 8 pass（CS2J0545 整树守恒门 96/88），Yaml W-06 行级复现（**W-17** 两语料三形态实证）；②R0 新增 **V-09 修复与 CS2J0545 记账归因**两项 M0 最前置任务（rt10 基线重跑的阻断项）；③**附录 H.3 JM 底账 6→12**（VS-1~VS-4 全量指认，实测行号）；④**附录 I VS-0 实施蓝图**（R12/R13/R14 的文件级落点、接口签名、测试清单与实施顺序）；⑤v7 七处关键断言抽查复核全部成立（`a1cb6633`→`98b4c539` 零代码提交）。v7 相对 v6 的核心增量：①新增 §0.1 总纲与 JCS.WORKSPACE/SYMBOL/CALL/EVAL/VALUE/GENERIC/CONVERSION/CLOSURE 八族后条件；②新增 R12 manifest、R13 最终树语义闭包与共享事实、R14 五态退休协议、R15 JE-08~JE-10；③修订 I10 与实施边界，按垂直切片而非巨型 M3 交付；④探针扩至 26 码、报告升版并新增复杂度/覆盖/hit 对账；⑤将 `--no-preprocess` 的 java-ready 绕过列为硬修复项。更早历史保留如下：v6 相对 v5 的变更（**主题：面向 Java 侧净简化的回灌**）：①基线刷新至 HEAD `8268251a`（52 pass 不变；v5 后 15 提交＝2 预处理+13 Java/compat，GenericPreprocess/ClosedMetadata/Preprocessing 零提交；V/S/W 复核时点 a60afc75，定稿时新增的 8268251a 为 J-02 再添实证），V-01..V-08/S-01..S-08/W-01..W-16 共 32 条**逐条代码复核仍成立**，S-05 细化（`BuildConstraintClauses` 初版即在且已接线，但不滤未携带形参/仍按名匹配/无约束闭包，`LocalFunctionLiftRewriter.cs:535,760-779`）、V-04（`GenericDeepenRewriter.cs:734-745`）/V-08（`:978-1002`，类型参数兜底 `null` 在 `:1002`）/V-07（Java `RuntimeClassName` 对 ITypeParameterSymbol 返回 null，`:69-70`；as 兜底 `:800`）/W-01（`PassChainFactory.cs:485,515-519`）行号订正、W-03 订正为**一码四义**（+`ExtensionBlockDiagnostics.cs:57`）、S-03 的 HolderSyntaxGenerator 路径订正（实为 `Core/ClosureLift/HolderSyntaxGenerator.cs:70-76`，非 `Lift/` 子目录）；②新增 **§3.5 Java 承接复杂度普查**与**层面 J（J-01~J-07）**、缺口 **G-33~G-39/W-15/W-16**（§5）、不变量 **I10**（§6.1）；③新增 **R11 JE-01~07 吸收规则族**（§6.4）与两个候选新 pass（插入序 §6.5）、探针 21→23 码（§6.3）；④L2 增 **Java 侧净简化对账门**（§6.6）、§7.1-8 对账指标与样例行、M0/M3 波次与风险 18/Q14/Q15 联动、附录 B 补证、新增**附录 H Java 复杂度普查底册**。v5 相对 v4 的变更：**吸收 kimi 3.1 pro 版的设计层与论断增量**（v2 仅吸收其论断层，逐项核证见附录 D）——§0/§5 新增 **W-11~W-14/G-28~G-32** 五项框架缺口（两入口 profile×disable 分裂、源生成器输出未物化、消费面 guard 无执行时序、项目边界丢失、ABI 变更无清单）；§6.4 R0 增 GD-MTOK 可见性分级/TypeParameterId 声明 owner/holder 并发证明/计数器与事实源修正；R3 增 execution kind 与 CM-LINK-3；新增 **R9（源生成器物化）/R10（ApiManifest 与 ABI diff）**；§6.3 探针 19→21 码；§6.1 增 I9；§6.6 L1/L2 增逐项目 compilation 终态与 ABI 门禁；§7.1-3 行为反例 12→16 类、§7.1-5/6 样例与指标增行、§7.0 增 per-TFM 纪律；§8.1/§8.2 波次与风险/Q13 对应更新。v4 相对 v3 的变更：**吸收 deepseek 1017 版的设计层**（v2 仅吸收其论断层）——§6.4-R2 收录 CN-01~12 全表与 GD-MTOK-2 触发集、R3 补消费面三 profile 与 CM-ENUM-5/6、CM-LINK-2、CM-STRICT 及单 pass bind 修复、R4/R7/R8 收录 VR/GI/EB 与状态机残余规则表并落三规则族 pass 化（generic-intrinsic-lower / variance-use-site-normalize / generic-reflection-audit）、§6.5 扩为完整插入序与兼容性细则、§6.3 增 VARIANCE-USE 探针（18→19 码）与测试工程双 policy/报告 schema 条目、§7 增验收命令（§7.0）与指标矩阵/一致性测试（§7.1-6/7）、新增附录 E（GC-01~35 构造登记目录）/F（报告 schema）/G（一页纸执行清单）。v3 相对 v2 的变更：**json 主语料更换为 `D:\runtime-10.0.12` Runtime 10 真源**（旧语料确认为 .NET 3.x 时代 STJ，v2 全部 json 运行数据降级为旧语料历史基线，§3.2.1）；静态普查按真源重测（§3.2.2，泛型密度 2-7× 放大）；新增 **W-09**（compat 注册表签名漂移，ToUtf8 条目在 rt10 零匹配）与 **W-10**（csproj 门控 Compile/外部共享源 → L1 硬前置）两项缺口及修复项；§5 增补 G-25/G-26；验收 json 口径切换至 rt10（§7.1）。v2 相对首版的变更：基线 48→52 pass、json 2 失败→0 失败、新增 V/S/W 三层缺口体系（替代单一的 G 矩阵结论）、YamlDotNet 推测画像→实测、吸收四份同行方案经核证增量（附录 D 全量对照）。
- v1 方案的 §5.3 Pass Contract v2、§5.9 性能层、M5 波次继承不变；其"42 pass"表述按 52 刷新。
- **先读 §0.1 的 v7 主方案与 §3.2.4 的 v8 实测基线**；§2 现状全景；§3 需求模型+双语料实证与 Java 复杂度普查；§4 逐 pass；§5 缺口矩阵（至 **G-44**）；§6 升级设计（R0~R15，**R12~R15 是 v7 主路径**）；§7 验收；§8 垂直切片波次与风险；附录 A 诊断码 / B 证据 / C 边界 / D 历史方案对照 / E 泛型构造目录 / F 报告与 manifest schema / G 执行清单 / H Java 复杂度底册（H.3 JM 底账 v8 扩容）/ **I VS-0 实施蓝图（v8）**。
- v6 的 V/S/W 代码引用保留其复核时点 `a60afc75`/`8268251a`；v7 新增事实按 HEAD `a1cb6633` 复核；**v8 新增事实按 HEAD `98b4c539` 复核**（含七处 v7 断言抽查、V-09 机制链 `LocalFunctionLiftRewriter.cs:146-153,861-864`/`LocalFunctionLiftPassAdapter.cs:87-148`/`Program.cs:5397-5399`/`PassChainFactory.cs:306-313`、CS2J0545 门 `ExpressionDesugarPassAdapter.cs:359-377` 与三份探针日志）。历史行号只作定位证据，退休对账改用稳定 JM-ID；`src/CSharpToJava.Core/` 省写为 `Core/`，`src/CSharpToJava.CLI/` 省写为 `CLI/`，`java/csharptojava-compat/src/main/java/io/github/ningpp/compat/` 省写为 `compat/`。

---

## 2. 现状全景

### 2.1 链结构（52 pass，`preprocess --list-passes` 实测）

```text
# id                              stage        status        default  run-after
conditional                       Lexical      Implemented   on       -
lexical-normalize                 Lexical      Implemented   on       conditional  rules=C28, C43
partial-merge                     Lexical      Implemented   on       conditional,lexical-normalize  rules=C20
local-function-lift               Declarative  Implemented   on       conditional,partial-merge
null-coalesce                     Expression   Implemented   on       local-function-lift
expression-desugar                Expression   Implemented   on       null-coalesce  rules=C23, C24, C35, C36, C51
enum                              Expression   Implemented   on       null-coalesce
enum-declaration                  Declarative  Implemented   on       enum  rules=C05
delegate                          Declarative  Implemented   on       enum
tuple                             Declarative  Implemented   on       delegate
anonymous-type-lift               Declarative  Implemented   on       tuple  rules=C21
foreach-deconstruction            Expression   Implemented   on       tuple
foreach-source                    ControlFlow  Implemented   on       foreach-deconstruction  rules=C16, C40
pattern-desugar                   ControlFlow  Implemented   on       null-coalesce,tuple,foreach-deconstruction  rules=C03, C37
strip-default                     Declarative  Implemented   on       foreach-deconstruction
ref-out-in                        Abi          Implemented   on       strip-default,foreach-source  rules=C01
unsafe-ref-lowering               Abi          Implemented   on       ref-out-in
using-resource-lift               ControlFlow  Implemented   on       ref-out-in
fixed-span-to-array               Abi          Implemented   on       ref-out-in,using-resource-lift  rules=C06
compat-method-replacement         Expression   Implemented   on       fixed-span-to-array  rules=C06
pointer-diff-inline               Expression   Implemented   on       ref-out-in  rules=C06
unsigned                          Abi          Implemented   on       ref-out-in
numeric-boundary                  Expression   Implemented   on       unsigned  rules=C15, C34, C17, C22
generic                           Abi          Implemented   on       unsigned
thread-static                     Declarative  Implemented   on       generic  rules=C09
generic-deepen                    Abi          Implemented   on       generic  rules=C02
closed-metadata                   Abi          Implemented   on       generic-deepen  rules=C30
variance-audit                    Declarative  Implemented   on       generic  rules=C33
record-readonly                   Declarative  Implemented   on       generic
array-method                      Expression   Implemented   on       generic,record-readonly
index-range                       Expression   Implemented   on       array-method  rules=C31
make-readonly                     Declarative  Implemented   on       array-method
attribute-normalize               Declarative  Implemented   on       generic-deepen,make-readonly  rules=C10
struct-value-semantics            Declarative  Implemented   on       make-readonly  rules=C07
unsafe-ffm                        Abi          Implemented   on       struct-value-semantics  rules=C06
stackalloc-lowering               Abi          Implemented   on       unsafe-ffm  rules=C06
ref-struct-lowering               Declarative  Implemented   on       stackalloc-lowering  rules=C06
ref-struct-span-field-memory-lift Declarative  Implemented   on       ref-struct-lowering  rules=C06
pointer-deref-index               Expression   Implemented   on       unsafe-ffm  rules=C06
closure-lift                      StateMachine Implemented   on       ref-out-in  rules=C32, C45
iterator-lowering                 StateMachine Implemented   on       make-readonly,closure-lift  rules=C04
async-normalize                   StateMachine Implemented   on       make-readonly,closure-lift  rules=C18, C38 (C44 subtopic)
extension-methods                 Expression   Implemented   on       delegate,tuple,ref-out-in,unsigned,array-method,make-readonly,iterator-lowering  rules=C19, C49
eliminate-goto                    ControlFlow  Implemented   on       make-readonly,iterator-lowering
member-desugar                    Declarative  Implemented   on       struct-value-semantics,closure-lift  rules=C11, C12, C13, C14, C39, C46, C50, C27
linq-channel                      ControlFlow  Implemented   on       extension-methods,anonymous-type-lift,eliminate-goto  rules=C26
concurrency-normalize             Declarative  Implemented   on       eliminate-goto  rules=C29
equality-concat                   Expression   Implemented   on       tuple  rules=C47
platform-isolation                Abi          Implemented   on       eliminate-goto,unsafe-ffm  rules=C42
dynamic-desugar                   Abi          Implemented   on       eliminate-goto  rules=C25
java-keywords                     FinalNaming  Implemented   on       eliminate-goto,platform-isolation,concurrency-normalize,dynamic-desugar,extension-methods,equality-concat
normalization-audit               FinalNaming  Implemented   off      java-keywords
```

相对 v1 首版的 4 个新 pass（均 C06/C02 域攻坚产物，`unsafe-ffm` 家族的前置/伴生）：

| pass | 作用 |
|------|------|
| `fixed-span-to-array` | span-backed `fixed` → 托管数组 `fixed`（只读形态直接 `ToArray()`；可写 `Span<T>` 目标保留临时 span 并在 `finally` 回写）；`&MemoryMarshal.GetReference(span)` 初始化器解包；多变量/await/yield 体等不可证形态保留 |
| `compat-method-replacement` | 按全限定签名在 `CompatMethodReplacementRegistry` 匹配方法声明，**只替换方法体**为托管实现（签名/属性/修饰符保留）；内置 `ToUtf8`（指针转码 → `Encoding.UTF8.GetEncoder().Convert`）与 `NeedsEscaping`（`fixed(char*)` 扫描 → 托管逐字符 `WillEncode`），使 unsafe-ffm 不再拒绝这些方法。**[v3] W-09：`ToUtf8` 条目的键/参数表拟合旧语料（3.x 的 `JsonWriterHelper.Transcoding.cs:28` 4 参 byte-span 签名），Runtime 10 真签名（`JsonWriterHelper.cs:291`）为 3 参 `(ReadOnlySpan<char>, Span<byte>, out int)`——rt10 语料上该条目零匹配** |
| `pointer-diff-inline` | 内联"同元素类型指针差值取整"文件内小助手（`PtrDiff(x,y)` → `(int)(x - y)`，char\* 形态含 `(Byte*)` 重解释与 `>>> 1`），声明在无任何引用（调用/方法组/nameof/反射字符串/跨文件标识符）后删除 |
| `ref-struct-span-field-memory-lift` | 只读 Span 字段/自动属性 → `ReadOnlyMemory<T>` 存储 + 同名 Span 属性 + `__cs2j_ToMemory` 写穿透（default span 保持 default 语义）；可写 Span/ref/指针/ref-like 字段、非私有、被 `ref/out/&/nameof` 引用等保守保留（RSM001）；partial 声明按符号级统一处理 |

单一事实源 `CLI/Preprocessing/PassCatalog.cs`；拓扑排序 `Core/Preprocessing/PassRegistry.cs:95`；`PassStage` 只影响展示。

### 2.2 执行框架（相对 v1 首版的增量）

继承 v1 方案 §2 全部结论（统一引擎 `ProjectPassRunner`、文件级契约 `PassFileRewrite`、默认每 pass 一份树拷贝、开关优先级、`--pass X` 语义）。增量变化：

- **PassContract v2 已落地**（commit `58257b25`）：`Core/Preprocessing/PassContract.cs` 定义 9 字段（Consumes/Produces/Preconditions/Postconditions/MayLeave/Owners/Policy/Idempotent/RequiresWholeTree），`PassConstructIds.cs` 为 NORM 码单一事实源（现存 10 码全非泛型），`PassDescriptor.Contract` 已接线，`PassContractConsistencyTests` 做静态一致性（ID 唯一/code 可解析/owner 提及）。当前 **10 个 pass 已挂契约**（unsafe-ref-lowering/using-resource-lift/fixed-span-to-array/compat-method-replacement/pointer-diff-inline/stackalloc-lowering/ref-struct-lowering/ref-struct-span-field-memory-lift/pointer-deref-index/normalization-audit），**泛型族（generic/generic-deepen/closed-metadata/variance-audit）未挂**。
- **`--profile` 已提交但两入口语义分裂 [v5 修正事实]**：`convert-project` 与 `preprocess` 共用 `TryNormalizeProfile`（`CLI/Program.cs:4115`，调用点 `:170`/`:4162`），但 java-ready/audit 追加 normalization-audit 的交互**两条入口不同**（W-11）：convert-project 分支（`:272-287`）在 disable 列表含 normalization-audit 时**不追加**（显式 disable 赢）；preprocess 分支（`:4183-4189`）**无条件追加**，与显式 disable 交叠后经 `PassPlanResolver`（`:60-65` 的 enable/disable 冲突检测）报 "cannot be both enabled and disabled" 退出 1。v4 文档"显式 `--disable-pass` 仍可排除"的表述仅对 convert-project 成立，特此修正；统一方案见 §6.4-R0/W-11 修复项与 §7.1-5 G-28 样例。
- **残留报告覆盖率计数已落地**（commit `deda3b62`）：`ResidualReportSummary.ByCode/ByOwner`（coverage matrix 最小形态）。
- **每 pass 执行报告** `preprocess-report.json`（commit `f162d07b`）继续存在；两份报告尚无互链与统计明细（G-AUDIT 遗留）。
- **[v5] pass id 事实源分裂**：`PassIds`（`CLI/Preprocessing/PassCatalog.cs:7`）与 `PassStubCatalog.Ids`（`CLI/Preprocessing/PassStubCatalog.cs:17`）两套常量并存，`PassCatalog.cs` 内部即混用二者（如 `:68` 用 `PassStubCatalog.Ids.LexicalNormalize`、`:89` 同一行混用 `PassIds.Conditional` 与 `PassStubCatalog.Ids.LexicalNormalize`）——新 pass 注册引用哪个事实源是随机的，属可维护性债（kimi P2 论断核证成立；修复归 M5 文档/事实源清理）。
- **[v5] `generic` 计数器语义漂移**：`_stats.DefaultExpressionsRewritten` 在 `GenericPreprocessor.cs:115-116` 递增，注释自证 "Count the occurrence regardless of whether we rewrite"——**在两处跳过判定（推断实参上下文/相等比较上下文）之前递增**，名为 Rewritten 实为 Encountered（kimi 5.1 论断核证成立；修复归 §6.4-R0 v5 项，报告口径随之拆分；**[v6] a60afc75 复核仍在原位**）。
- **[v6] v5 后预处理侧两修复（2aa7aa8f/51e505cd）**：断言回调 lambda（`ThrowsException/Throws/DoesNotThrow` 的表达式体回调）统一经新 `Core/Preprocessing/VoidCallbackAssertionFacts.cs`（93 行）判 void 语义，expression-desugar 强制包块体、ref-out-in 的 `out _` 回调不再生成 return、`_ =` 丢弃赋值有承接；同批还修了 `using Tuple = System.Tuple` 别名导入（复用 `BuildImportName` 守卫）与 `CloneInsertionRewriter`（struct-value-semantics，+24 行）。**要点**：两修复与 12 个 Java/compat 修复同批耦合，收敛的是同一真实链（MSAGL）javac 错误 75→24→12——新形状"void 回调 lambda"同时惊动两阶段五个落点（W-15），但 GD/CM/Preprocessing 框架目录在 v5 后零提交，V/S/W 清单不受影响。

### 2.3 契约与审计现状

- `ResidualRegistry` 10 个 descriptor 全 `Fail`：`NORM-ENUM-ARITH / STACKALLOC / REF-STRUCT / REF-LOCAL / ASYNC-LAMBDA / TUPLE-LOCAL / POINTER-OP / POINTER-LOCAL / UNSAFE-BLOCK / FIXED`（`Core/Normalization/ResidualRegistry.cs:11-23`）。**泛型域 0 探针**。
- `generic-deepen` 有 pass 内残留门（五形状：static generic field、catch-type-param、type-param-pattern、裸 `typeof(T)`、`is/as T`；`GenericDeepenResidualScanner.cs` 8 条规则），但只进 `CS2J1120` 诊断；且 **java-ready 不自动 strict**（W-01）。
- `variance-audit-report.json` 有 `--variance-audit-escalate` 链内升级开关，但无代码消费该 JSON 做降级决策；**约束审计 VA020-027 已存在**（含依赖位点统计与"definite semantic error"判词）但同样未接门禁。
- 审计证据链：`.cs2j-java-keywords-audit-baseline`（170 残留/7 类）→ `.cs2j-stackalloc-lowering-audit`（97/6 类）→ **当前 json 全链 0 Java 转换失败**。

### 2.4 Java 转换器对泛型的现有承接（目标子集事实清单，v2 修正）

> 事实清单，用于界定预处理降级目标；不是本方案改动范围。v2 修正处以 **[v2]** 标注。

**硬门禁**（`Transformers/NormalizationRequired.cs:13` 统一构造点；**[v6] a60afc75 重测：经统一构造点的调用实测仅 15 处**＝UnaryExpressionTransformer 14 + BinaryExpressionTransformer 1，**另有 54 处裸 `throw new NotSupportedException` 散在 18 个 Transformers 文件绕过统一点**——v2"全链 42 点"的口径已不可复现，重建立册归 JE-06/W-16/附录 H；泛型直接相关 2 个）：

1. 静态字段引用外围类型参数：`Transformers/Member/FieldTransformer.cs:119`（GD-FIELD 先行消除；注意 **静态泛型属性有 Java 侧提升** `PropertyTransformer.cs:64/83` 调 `PromoteReferencedEnclosingTypeParameters`，deepseek 文档"静态泛型属性无载体"论断**不成立**）。
2. `catch (T e)`：`StatementTransformer.SwitchAndResource.cs:327`（GD-CATCH 先行）。
3. （构造维度间接命中）泛型枚举/值类型上的非等值二元运算、用户定义一元/增减运算符。

**约束处理**（`ClassTransformer.ApplyTypeParameterConstraints:347`）：类型约束→Java bounds（`ICollection<T>` 专门重映射）；`class` 静默丢弃；`struct`/`new()` 丢弃+CS2J1002；`notnull/default/unmanaged` 静默丢弃。

**型变**：声明处 `out/in` 丢弃+CS2J1003（接口 `InterfaceTransformer.cs:101`/委托/类三处）；使用点协变由 `ArgumentTransformer.Coercion.cs:930` `TryBuildIterableBridge` + `IterableCoercion.cs:130`（五挂接位）+ `TypeOperationTransformer.cs:129/751`（cast/as 适配）桥接。

**静态成员**：静态方法/运算符引用外围类型参数 → 自动提升（`StaticMemberTypeParameterHelper.cs:24`，含 bound 闭包）；静态属性 getter/setter 同样提升 [v2 修正]；静态构造器 → static 块。无每实例化静态补偿（由 GD-FIELD holder 在预处理侧承担，见 V-01/02 缺陷）。**[v2]** 泛型类型上的静态成员访问已修复 raw receiver 渲染（`ReadOnlySpan<byte>.Empty` → `ReadOnlySpan.getEmpty()`，commit `5879d22e`，`IdentifierExpressionTransformer.cs` StripTrailingGenericArguments）。

**typeof/default/new**：`typeof(T)` 优先读 `__cs2j_token_T` 锚点（`TypeOperationTransformer.cs:934`），缺失退化为 bound class literal/`Object.class`；`typeof(X<>)` → 擦除宿主 class literal（`Nullable<>`→`Optional.class`）；`default(T)` 按符号分派零值——**类型参数兜底渲染 `"null"`**（[v6] 行号漂移至 `:978-1002`，兜底 return 在 `:1002`，值类型实例化语义错误，V-08）；`new T()` 产物 → compat `Activator.createInstance()`（注意 C# 语义坑：`Activator.CreateInstance<T>()` 对带自定义无参构造器的 struct 会调构造器，与 `default(T)` 零值**不等价**——设计 default 类降级时不得经 Activator）；`new T[n]` 产物 → `(T[])(CSharpArray.tryAs(...))` + Java 后处理 `GenericArrayCreationRewriter`。

**泛型反射**：`MakeGenericType/GetGenericArguments/GetGenericTypeDefinition/MakeGenericMethodParameter/GetUnderlyingType` 有 methodMapping → compat `TypeHelper`；**`TypeHelper.registerClosedType/resolveClosedType/closedCacheKey` 已接线并有测试** [v2 修正：v1 首版"未接线"过时]；`GenericParameterAttributes` 无映射。

**映射面**（`config/TypeMappings.json` 964 条）：v1 首版结论不变；**[v6 复核 G-11 半修]** `Comparer<T>`（`System.Collections.Generic.Comparer`1`）已有映射（TypeMappings:380 → compat `Comparer.getDefault()`，`Comparer.java:12`）；但 **`EqualityComparer<T>` 静态类本身仍无映射条目**（只有 `IEqualityComparer`1` 接口映射 :470 与 compat 抽象基类 `EqualityComparer.java`）——用户代码 `EqualityComparer<T>.Default` 仍断裂；tuple/anonymous **预处理生成物**发射的 `EqualityComparer<T>.Default.Equals/GetHashCode` 靠 Java 侧硬编码承接（`InvocationExpressionTransformer.cs:790-815` → `Objects.equals/hashCode`，J-07）。

**擦除冲突**：`Context/JavaNaming.cs:211` 模型完整（同擦除 `_erasure_N`、跨 arity `_{n}tp`、声明/调用/构造器/跨继承四处一致、显式接口实现豁免）。

**DIM/static abstract**：实例 DIM → `default` 方法；static abstract/virtual 转换器不识别不报错，由上游 expression-desugar CS2J0585 / member-desugar CS2J5175/5116 守门。泛型 attribute：CS2J0587 守门。

**运算符/显式实现/状态机产物**：v1 首版结论不变（泛型类运算符静态方法化+类型参数提升；显式实现 public+接口形状；`Cs2j*` 命名生成物无专门 transformer、普通类流经）。

**已承接子集固件**：`TypeOfGenericTests`、`TypeOfUnboundGenericTests`、`SystemTextJsonDeserializeTypeReferenceTests`、`DeserializeGenericClassLiteralTests`、`GenericMethodExplicitTypeArgTests`、`NestedGenericMappingTests`、`StaticGenericFieldTests`（ConversionTests）。

---

## 3. 泛型密集库的需求模型与语料实证

### 3.1 特性分类学（v1 四层 + 运行时信息正交维度）

v1 首版的四层分类（A 声明层/B 使用层/C 语义层/D 组合层，G-01~G-15 的展开）继续成立。v2 补充一个**正交维度**（采纳自 seed 方案、经核证有价值）：按"擦除后必须显式补出的**运行时信息**"分类，可直接映射到"补什么机制"：

| 类 | 运行时信息 | 现有机制 | 成熟度 [v2 修正] |
|----|-----------|---------|-----------------|
| R1 | 方法体内 `typeof(T)` | GD `__cs2j_token_T` 锚点 + 族/闭包/前向传播 | 高（方法作用域）；**中**（S-01 作用域盲区/S-02 闭包不可达） |
| R2 | `is T`/`as T`/`catch(T)`/pattern T 运行时类型测试 | GD-ISAS-B/GD-CATCH；Java is→isInstance | 中（**as T 两侧均不改写**，V-07） |
| R3 | `new T()`/`default(T)` | E10→Activator / default 字面量 | 高（两类上下文例外 + V-08 null 兜底） |
| R4 | `new T[n]` 等具化数组 | E10 一维 + GD-CTORARR `__cs2j_class_T` 构造器尾参 | 中（多维/jagged/初始化器/CtorArr 拒嵌套与静态上下文） |
| R5 | 泛型静态字段 per-closed-type 存储 | GD-FIELD holder 键槽 | 中（V-01/02 缺陷 + 多声明符/复合赋值/接口默认方法体排除） |
| R6 | 泛型静态方法/属性引用类形参 | GD-STATIC 提升（属性经 Java 侧提升兜底） | 中（方法组形状/调用点闭型不可见 CS2J1106） |
| R7 | 泛型虚成员/接口实现族按封闭类型分派 | GD 族锚点对齐 + Java 桥 | 高（方法族） |
| R8 | 闭型全局可见性（序列化入口） | C30 注册 + compat 注册/解析闭环 [v2] | 中（消费面硬编码 STJ，W-05） |
| R9 | 约束/型变携带（new()/struct/unmanaged/in/out） | C33 只读审计（VA020-027） | **低——最大语义债**：门禁未接（W-01）+ 合成层丢约束（S 层）+ Java 侧丢弃 |

**[v3] rt10 换源对 R 表的量级影响**：R1 需求面 7.6×（typeof(T) 76）、R3 18×（`new()` 约束 18 处）、R8 第二消费面新增（Schema 导出链 + `JsonSerializerOptions.Strict/Web` 单例）、R9 需求面 3×（where 子句 103）。**V-05（R8 键分裂）的触发面 7.2×（NRT 泛型实参 452）——从"边缘场景"升为"主流量级"，修复优先级随换源上调。**

**[v5] R8 的注册时序维度补强**（kimi CM-03，归 W-13/R3 扩）：R8 的"注册"当前指词法相邻的 bootstrap guard（消费语句前方插入），未区分消费的**执行时序**——Immediate（立即执行）/Deferred（延迟枚举/委托稍后调用）/ExpressionTree（树编译时）/Factory（工厂内部稍后创建）/AsyncStream（真正枚举时）五类的注册位置要求不同；deferred 场景下 guard 可能在消费之后执行或不在执行路径上。rt10 的 Schema 导出回调（`TransformSchemaNode` 委托属性）与 Yaml 的 builder 延迟构建均落 Deferred/Factory 类，升级时 `ConsumerDescriptor` 须携带 execution kind 字段（§6.4-R3）。

### 3.2 System.Text.Json 语料实证（v3 换源：`D:\runtime-10.0.12\src\libraries\System.Text.Json`）

#### 3.2.1 换源与语料时代错位说明 [v3 新增，先于一切数据]

- **旧语料身份确认**：`D:\csharp-sdk\json`（341 .cs）不是现代 STJ——它保留 `JsonClassInfo(.Cache).cs`/`JsonPropertyInfo(.OfT).cs`/`JsonParameterInfo.cs`/`ReflectionEmitMemberAccessor.cs`/`MemberAccessor.cs`/`JsonWriterHelper.Transcoding.cs` 等 .NET 3.x 时代布局，**无** `JsonTypeInfo`/`IJsonTypeInfoResolver`/源生成器/Schema 导出。v1 的静态普查与 v2 的全链运行统计（626/0、202 anchors、37×1101、588 注册等）全部在这份**旧时代语料**上测得。
- **新语料**：Runtime 10.0.12 真源。库本体 **352 .cs**（src 308 + gen 20 + Common 24）+ tests 333 .cs，合计 685（旧 341）。结构新增：`Schema/`（JsonSchema/JsonSchemaExporter 等 5 文件）、`Converters/FSharp/`（6 文件）、`Memory/ReadOnlyMemory` 转换器族、`IAsyncEnumerable` 转换器、`JsonReaderHelper.net8.cs`/`.netstandard.cs` TFM 分文件、`gen/` 源生成器（Roslyn 3.11/4.0/4.4 三 csproj 变体）、`Typeforwards`、`JsonMarshal.cs` 互操作。TFM 5 个（net10.0/net9.0/net8.0/netstandard2.0/net472），首选 TFM=net10.0 → `NET` 符号集（W-10 关联）。
- **后果**：v2 的"json 0 失败"结论对 rt10 语料**不构成证据**；rt10 全链运行是 M0 的入口任务（§8.1）。v2 运行统计表降级为"旧语料历史基线"保留于 3.2.3，供重跑对照。

#### 3.2.2 静态普查（v3，rt10 库本体 = src+gen+Common，对照旧语料 src、剔除 obj 产物；grep 口径同式双测）

| 指标 | rt10 库本体 | 旧语料(3.x) src | 倍率/备注 |
|------|-----------:|----------------:|-----------|
| `where` 约束子句 | **103** | 34 | 3.0× |
| `typeof(T)`（单类型参数） | **76** | 10 | 7.6×（GD 锚点面同倍放大，S-01/S-02 盲区的暴露概率陡增） |
| 未绑定 `typeof(X<>)` | **81** | 42 | 1.9× |
| `typeof(` 合计 | 408 | 164 | 全语料（含 tests）6734 vs 951 |
| 泛型声明（class/struct/interface/delegate/record `<`） | 72 | 39 | 含 tests 全语料 242 vs 110 |
| `new()` 约束 | **18** | 1 | 18×（R3/`new T()` 与 default 语义区分面显著放大） |
| `notnull` 约束 | 23 | 10 | |
| `struct, Enum` 约束 | 6 | 1 | G-20 枚举域 |
| `MakeGenericType/GetGenericArguments/GetGenericTypeDefinition/IsGenericType` | 23/27/20/23 | 11/23/11/10 | 反射分级 RG-0~4 面扩大 |
| `MakeGenericMethod` | 5 | 2 | |
| **NRT 泛型实参（`<…?>` 形态）** | **452** | 63 | **7.2×——V-05 键分裂（`List<string>` vs `List<string?>`）的触发面从边缘变主流量级**；§3.3 链 1 的 NRT 元数据保留契约（G-22）同步放大 |
| `RequiresUnreferencedCode` | **138** | 0 | trimming 注解面全新（attribute-normalize/平台审计需盘点，`DynamicallyAccessedMembers` 15 vs 18） |
| `JsonConverter<` 出现文件 | 43 | 57 | rt10 src 因分部/拆文件文件数降但行数升（152 vs 88 行） |
| `in` 参数 | 59 | 54 | 基本持平 |
| ref struct 声明 | **4 型/5 声明点** | 3 | Utf8JsonReader（×2 partial 文件）、`PropertyNameSet`（Document）、`GenerationState`（Schema，主构造）、`PropertyHierarchyResolutionState`（JsonTypeInfo.cs:1050，主构造）；**`JsonReaderState` 是 `readonly struct` 非 ref**（易被 doc 注释误计）、`ValueQueue<T>` 为普通 struct |
| `unsafe` 行 / `fixed (` | 13 / 13 | 21 / 8 | rt10 指针代码大量进 `#if !NET`（13 处）——net10 符号集下剥离，C06 域实际工作量待 M0 定量 |
| `#if` 分布 | NET×80、!NET×13、NET9_0_OR_GREATER×5+!×5、NET9_0×4、DEBUG×19 | — | conditional pass 首选 TFM 正确性成为 W-10 一部分 |
| `IAsyncEnumerable` 文件 | **13** | 0 | async-normalize + 泛型状态机新面（IAsyncEnumerableOfTConverter） |
| 泛型显式调用近似（`Name<…>(`） | ~7375（含 tests） | ~3887 | 1.9× |
| `partial` 声明 | 130 | 76 | partial-merge 面 |

**rt10 实测出现、旧语料为零的现代特性面**（逐条映射到 pass）：

| 特性 | rt10 实测 | 关联 pass/缺口 |
|------|-----------|---------------|
| **C# 14 `field` 关键字** | 4 处：`JsonSerializerOptions.cs:42/57/72` 三个**静态单例属性** `get => field ?? GetOrCreateSingleton(ref field, …)`（`ref field` 作实参！）+ `JsonDocumentOptions.cs:86` `set => field = !value` | member-desugar 已有 `FieldKeywordBodyRewriter`（含表达式体分支，`:42`）；`ref field` 实参与静态属性形态需 M0 fixture 锁定 |
| **主构造函数** | 8 处，**含 ref struct 上的主构造函数**（`JsonTypeInfo.cs:1050` `internal ref struct PropertyHierarchyResolutionState(…)`、`JsonSchemaExporter.cs:411` `GenerationState`、`PropertyNameSet`） | member-desugar/局部声明域；ref struct + 主构造 + Span 字段与 ref-struct-span-field-memory-lift 组合为新组合面 |
| **`params ReadOnlySpan<>`** | 4 处**公共 API**：`JsonTypeInfoResolver.Combine(params ReadOnlySpan<IJsonTypeInfoResolver?>)`、`JsonArray(params ReadOnlySpan<JsonNode?>)`×2、`ThrowHelper…(params ReadOnlySpan<string>)` | G-21 K2 已支持声明物化+调用点展开——rt10 首次有真实公共 API 触发面 |
| **C# 12 using 别名指向元组形状** | 3 处：`using FoundProperties = System.ValueTuple<JsonPropertyInfo, JsonReaderState, long, byte[]?, string?>`（`ArgumentState.cs:7-8`、`ObjectWithParameterizedConstructorConverter.cs:11`） | tuple pass 需经别名解析元组形状（新交互，无既有 owner——归 S 层/统一解析器观测） |
| 集合表达式 | 42 处 | expression-desugar C36；G-18 `?? []` 中止教训直接适用 |
| `with` 表达式 | 11 处 | record-readonly（CS2J5223 开放泛型接收者形态） |
| `scoped` / `required` / `init` | 147 / 133 / 147 | ref-out-in / member-desugar 已有路径，量级面放大 |
| `readonly` 实例成员 | Utf8JsonReader 方法族大量 `public readonly` | ref-struct-lowering/struct-value-semantics |
| `readonly struct` | 15 | struct-value-semantics |
| Schema 导出回调 | `JsonSchemaExporterOptions.TransformSchemaNode : Func<JsonSchemaExporterContext, JsonNode, JsonNode>? { get; init; }` | 泛型委托 + init；closed-metadata 第二消费面候选（与 Yaml 双 builder 同类） |
| **确认零出现**（维持域决策不迫近） | static abstract **0**、泛型 attribute **0**、extension block **0**、`allows ref struct` **0**、`>>>` **0**、checked 块 **0**、`Expression<` 树 **0**、record struct **0**、dynamic **1** | G-07/G-08/G-23（STJ 侧）继续"语料不触发"；G-12b/c 域决策不变 |

#### 3.2.3 全链运行统计（**旧语料实测，v2 数据整体降级为历史基线**，rt10 重跑为 M0 入口任务）

v2 在旧语料上的实测（`D:\cs\20261104-json.log`，供 rt10 重跑逐项对照——预期 anchors/1101/注册数全面放大，理由见 3.2.2 倍率列）：

| pass | 运行结果（旧语料） | 关键残差信号 | rt10 重跑关注点 [v3] |
|------|---------|-------------|-----------------|
| `generic` | 6 `default(T)` / 7 `new T()` / 4 `new T[n]` 改写 | — | `new T()` 密度（`new()` 约束 18×） |
| `generic-deepen` | 80 transformed / 287 clean：202 anchors、13 static methods、9 field accesses、614 call sites；计数器报 0 residuals | 37 条 CS2J1101（S-02）+ 3 条 CS2J1106——计数器与诊断矛盾 | typeof(T) 7.6× → anchors 量级与 S-01/S-02 暴露率 |
| `closed-metadata` | 588 registered（2436 enumerated，1848 deduplicated） | 0 linked、369 dynamic skipped、281 residuals（W-02） | 消费面入口（W-05）在新 API 面上的枚举：`JsonSerializer.Serialize/Deserialize/SerializeToUtf8Bytes/SerializeAsync` + `GetTypeInfo` + 新增 `JsonSerializerOptions.Strict/Web` 单例与 Schema 导出链 |
| `variance-audit` | 704 use sites、221 conversions、53 unsafe、25 constraints | 126 baseline-error 文件跳过（W-08） | rt10 NRT 泛型实参 452 → V-05 键分裂首次有主力触发面 |
| `extension-methods` | 762 rewritten（612 external hosts） | 41 hosts 无映射 | MemoryExtensions/Linq 宿主面随 Span 化上升 |
| `closure-lift` / `iterator-lowering` / `async-normalize` | 52 holder / 55 iterators / 21 lifted | — | IAsyncEnumerable 13 文件新面 |
| `compat-method-replacement` | ToUtf8/NeedsEscaping 命中（旧签名） | — | **W-09：ToUtf8 在 rt10 零匹配**（§4.3） |

v2 两条实证结论的 v3 修订：

1. **"0 Java 转换失败"与"归约完备"是两回事**——且该 0 失败本身测自旧语料；同一轮链上 37×1101 + 3×1106 + 281 CM 残留 + 41 未映射宿主全部 Warning 放行。泛型密集库的"可过性"由**语料运气**而非**契约保证**支撑，rt10 重跑前连"json 语料运气"都要重新确认。
2. 反射密度结论不变且放大（`MakeGeneric*`/`GetGeneric*` 全面上行）：转换正确性的本质是运行时反射语义的 compat 承接面；预处理该做的是把静态可解部分物化（P1-R5/RG-0~4）、不可解部分登记（NORM-GENERIC-REFLECT）。

#### 3.2.4 rt10 实测首跑（v8 新增，2026-11-06，HEAD `98b4c539`；§7.0 命令，`--no-verify-build --no-verify-tests` 探针口径）

v3 换源以来**第一次在 rt10 真源上运行全链**（此前全部运行数据测自旧语料）。两次探针 + Yaml 复测的实测结论：

**探针 1（默认链，`--profile java-ready`）**：链在**第 4 个 pass `local-function-lift` 中止**，`[Error] 2 residual local function(s)`（该 pass 在 preprocess 路径默认 strict，`--no-local-function-strict` 是唯一逃生门，`Program.cs:5397-5399`——与 W-01"泛型族不传 strict"恰成对照：一个想严严不了，一个默认严却漏改写）。残留函数为 `JsonElement.DeepEquals` 的 switch-default 块内两个 static 局部函数（`UnorderedObjectDeepEquals`/`NameEquals`，源 `JsonElement.cs:1358,1404`），V-09 半改写实锤：中间树同名文件 `:375/:411` 已有 lift 生成的类级 camelCase 方法、`:1742/:1745` 调用点已改写为小写、`:1759/:1793` 原声明未删除。

**探针 2（+`--no-local-function-strict`）**：链前进至**第 8 个 pass `expression-desugar` 中止**，CS2J0545 整树守恒门（无条件 Error，无旋钮，`ExpressionDesugarPassAdapter.cs:359-377`）：96 个 object/collection/bare-`new()` init 残差节点 vs 88 个 per-file 记账，8 个 unpaired（`JsonSchemaExporter.cs` 28/22、`JsonPrimitiveConverter.cs` 5/4、`PropertyRefCacheBuilder.cs` 1/0）——per-file 记账与整树扫描口径分裂，G-41 的现存先例。

**沿途 pass 统计（rt10 实测，对照 §3.2.3 旧语料基线列）**：

| pass | rt10 实测（v8） | 关键信号 |
|------|----------------|---------|
| strip-conditional | 66 transformed / 286 clean，372 directives removed，116 disabled blocks | **TFM Warning 群**：`$(NetCoreAppCurrent);$(NetCoreAppPrevious);…` 属性值 "skipped unresolvable" 后仍收文件（W-10 运行级证据） |
| lexical-normalize | 0 transformed / 352 clean | 库本体 352 .cs 口径复核一致 |
| partial-merge | 9 组 merged，**5 组冲突回滚**（CS2J2001）：`JsonWriterHelper`/`Utf8JsonWriter`/`JsonTypeInfo<T>`/`ThrowHelper` 及 5 处 using 指令携带冲突 | **旧语料没有的回滚行为**：rt10 partial 族大文件（合并产物 `JsonElement.ArrayEnumerator.cs` 达 1809 行）成为 V-09 的放大器（合并后文件名漂移也使诊断定位更难） |
| local-function-lift | 28 transformed / 324 clean，**59 lifted，2 residual**（V-09） | 旧语料 json 该 pass 无 residual 记录 |
| null-coalesce | 58 transformed，97 ?? + 96 ??= rewritten，**0 residual** | Yaml 的 `?? []` 形态 rt10 不触发（W-06 为 Yaml 特有形态，rt10 的链中止另有其因） |
| expression-desugar | 106 transformed / 211 clean；coll 35 sites（array 9/mutable 15/iface 0/**span 8**/unknown 3）、spread 3、empty 16；interp 218 sites（concat 162/format 4/fold 25）、handler keep 27；init 67 obj + 125 target-new，**96 residual → CS2J0545 中止** | C24 守恒门在 rt10 的新形状（Schema 导出/基元转换器/属性缓存构建三文件）上口径分裂；CS2J0510 自定义 interpolation handler Warning 27 处（Debug.Assert/StringBuilder handler 保留，正确保守） |

**Yaml 复测（默认链）**：中止点与 v2 实测**行级一致**——`null-coalesce: 63 transformed, 149 ?? + 5 ??= rewritten, 1 residual → Error`（`TypeConverterCache.cs` `?? []`，W-06 复现）；沿路 local-function-lift 4 lifted / 0 residual（Yaml 无 V-09 形态）。

**三条实测结论**：

1. **"json 0 失败"是旧语料历史数字**：rt10 默认链连第 4 个 pass 都过不去，v7 §0 的预判（"换源暴露旧语料未见的失败"）兑现。在 V-09 修复 + CS2J0545 归因之前，**rt10 全链基线无法建立**——§3.2.2 静态普查的"重跑关注点"列（anchors 量级/CM 消费面/V-05 键分裂触发面）全部处于"未到达"状态。
2. **两个中止点互为独立形态，同指一类结构性缺口**：V-09 是**改写非原子**（生成/改调/删除三步只有前两步成功，per-file 统计全绿）；CS2J0545 是**记账口径分裂**（per-file accounting 与整树扫描是两套实现）。两者共同强化 R13/G-41：守恒需要"单一记账实现 + 最终树扫描"而非两面对账。
3. **探针的可复现性已锁定**：三份日志（`D:\cs\rt10-probe-v8.log`、`rt10-probe2-v8.log`、`yaml-probe-v8.log`）+ 输出树（`D:\cs\rt10-probe-v8\.cs2j-preprocessed-src\`，半改写现场保留）入库为 M0 重跑对照基线；`--no-local-function-strict` 只能作为**归因工具**（探针 2 的用途），不得作为 M0 之后的验收路径——修 V-09 而非绕过它。

**[v8.1] V-09 修复落地与探针 3 复跑（同日，HEAD 待提交）**：R0-V09 修复按上表设计实施完毕——①`LocalFunctionLiftRewriter` 新增 `VisitSwitchSection` 移除通道（镜像 `VisitBlock` 的索引/SpanStart 双路径）；②原子性防护：主 rewriter 的 `VisitLocalFunctionStatement` 对"非 skipped 且容器非 BlockSyntax/SwitchSectionSyntax"的 plan 记入 `UnremovablePlans`，`LocalFunctionLiftPreprocessor` 见非空即**整文件回滚**并逐 plan 出带行号的 Warning（杜绝半改写落地）；③聚合 residual 诊断嵌入位置清单（最多 5 处 + 计数）；④新增 4 个 xUnit 用例（switch-default 单函数/case 标签/多 section/嵌套 Block 回归保护，rt10 `DeepEquals` 形态），4/4 绿；全量回归 **4552 测试 0 失败**（4543 通过/9 跳过）。

**探针 3（修复后默认链复跑，`D:\cs\rt10-probe3-v8fix.log`）**：`local-function-lift: 28 transformed, 324 clean, 59 lifted, 0 skipped, 0 residual`——**W-17①实测关闭**；链继续通过 `null-coalesce`（58 transformed/0 residual），止于第 8 pass `expression-desugar` CS2J0545——**与 W-17②的预测逐字一致**。修复还带来一条归因新线索：CS2J0545 的 unpaired 面从 3 文件收窄为 2 文件（`JsonPrimitiveConverter.cs` 的 1 个 unpaired 随半改写消除；修复前 96/88 → 修复后 75/68，剩余 unpaired = `JsonSchemaExporter.cs` 28/22 + `PropertyRefCacheBuilder.cs` 1/0）——**M0 的 CS2J0545 归因范围据此收窄到后两个文件**，且证明 per-file 记账面会受上游 pass 的改写形态影响，进一步支持"整树扫描为唯一事实源"的修复方向。

### 3.3 YamlDotNet 语料实证（`D:\YamlDotNet-18.0.0`，18.0.0，261 .cs）[v2 全节替换 v1 画像推测]

**语料与运行事实**：

- 库项目 5 TFM（net10.0/net8.0/netstandard2.0/netstandard2.1/net47）；解决方案级在 `partial-merge` 处 CS2J2018 中止（`Program` 多部件不在白名单）；**库项目级默认链在 `null-coalesce` 处因 1 处 `?? []` 残留中止**（`Serialization/Utilities/TypeConverterCache.cs:39`，`expression-desugar` 在其后永无机会运行，W-06）。
- 单 pass 复跑：`generic` = 3 transformed（**2 `new T()` / 2 `new T[n]`**，0 `default(T)`）；`generic-deepen` = 27 transformed（**100 anchors、1 nested、1 pattern、1 static method、2 call sites、0 residuals**）；`closed-metadata` 单跑暴露 `CompilationReadyAsync` 早退不 BindCompilation 的空引用崩溃（单 pass 顺序依赖，Yaml 上 20 次 `_compilation` null）。
- **约束画像（推翻 v1 首版画像的两处推断）**：`where` 共 97 处——`class`×7、`notnull`×5、**`struct`×0、`new()`×0、`Enum`×0**。v1 首版"`WithTypeConverter<T>() where T : IYamlTypeConverter, new()` 是 `new()` 约束典型受害者"的推断**不成立**（实测约束全为引用语义）；`SerializerState.cs:32-50` 的 `where T : class, new()` + `new T()` 复合场景是唯一邻近形态。
- 泛型 delegate×2、record×0、`Unsafe/MemoryMarshal/sizeof(T)`×0、型变声明×0、`default(T)` 文本×1。
- 反射 API 实测：`MakeGeneric*`×7 / `GetGeneric*`×13，命中文件为 `EnumerableNodeDeserializer`/`DictionaryNodeDeserializer`/`CollectionNodeDeserializer`/`DefaultObjectFactory`/`ObjectFactoryBase` 等（deepseek 文档所称 `GenericCollectionTypeResolver` 文件**不存在**，属其编造；其 7/13 计数正确）。

**三条独家语义链**（kimi 方案实测发现，已核证）：

1. **NRT 是可反射的运行时元数据**：`ReflectionExtensions.cs:308-337` 运行时读 `NullableContextAttribute/NullableAttribute` 计算 `AcceptsNull` → `WritablePropertiesTypeInspector.cs:83-88` 映射 `AllowNulls` → `ObjectNodeDeserializer.cs:177-185` 决定空值异常行为。**运行时类型键剥离 NRT 是对的（V-05 修法确认），但这两个编译器生成属性及其 flags 必须在成员元数据中保留或等价重建**——任何"只看语法"的普查都发现不了的语义链。
2. **双 builder 路径并存**：反射型 `SerializerBuilder/DeserializerBuilder` 与 `StaticSerializerBuilder/StaticDeserializerBuilder` + `StaticContext/StaticObjectFactory` + `YamlDotNet.Analyzers.StaticGenerator`（源生成器）；静态生成路径是 closed-metadata 消费面配置化的天然第二消费者。
3. **表达式树承载成员元数据**：`BuilderSkeleton.cs:129-140` 的 `WithAttributeOverride` 收 `Expression<Func<TClass,object>>`，`ExpressionExtensions.cs:38-79` 从表达式树还原 `PropertyInfo`——Expression 域不只是"执行面"还有**元数据面**；`BuilderSkeleton.cs:35-38` 的 CRTP（`where TBuilder : BuilderSkeleton<TBuilder>`）是 GD-05 参数身份缺陷（V-03）的触发形态。

**语料定性**：YamlDotNet = "反射驱动 + 运行期类型流"型（`IObjectDescriptor(Type,object)`/`ObjectFactory.Create(Type)`/`IYamlTypeConverter.Accepts(Type)`），与 STJ 的"编译期闭型 + 性能内在函数"型互补；前者验证通用性、后者验证深度。

### 3.4 通用现代库补集（"任意"二字的覆盖口径）

v1 首版结论不变（static abstract/自声明型变/泛型 attribute/`allows ref struct`/泛型+`required|init|record struct`/高阶泛型），v2 增补：

- **params 集合全谱系**（v1 矩阵缺行）：`params Span<T>/ReadOnlySpan<T>` 声明物化+调用点展开已支持（K2，CS2J0604）；`ReadOnlySpan<char>` 字符面（CS2J0601）与 ref struct 元素（CS2J0608）不支持；`[CollectionBuilder]` 一律 CS2J0607（`Core/DefaultParameterStripper/ParamsCollections/`）。
- **泛型成员发现契约**：`BindingFlags/DeclaredOnly/DeclaringType/ReflectedType`/new-hide-override 的反射成员发现语义（STJ `JsonClassInfo.cs:109-171` 沿 BaseType+DeclaredOnly 枚举、`ReflectionMemberAccessor.cs:112-169` 直接 `MethodInfo.Invoke`——行为面需要 compat 差分验证）。
- **闭世界 vs 开放世界二分**（kimi 方案）：应用闭世界（可枚举全部 DTO → 闭型工厂优化+完整差分）vs 库开放世界（保留运行时 Type 注册/开放泛型 invoker，闭型注册仅作优化）；`Type.GetType(字符串)`/动态程序集/emit/插件 DTO 不可静态枚举——closed-metadata 的收集边界由此界定，超界即 readiness 失败而非猜测。
- **源生成器依赖的物化契约** [v5 新增，kimi CM-05]：现代库的**测试工程**普遍依赖 source generator（rt10 tests 的 `JsonSerializerContext` 派生 context、YamlDotNet.Test 的 `YamlDotNet.Analyzers.StaticGenerator` 生成 helper）。B/C 路差分要求预处理后的测试工程可编译——生成输出必须在工作区阶段真实执行并物化为普通源码（带 `(generator, project, tfm, inputHash)` 身份，防真实 build 再生成一份造成重复）；CM 侧现状仅排除+警告（CS2J1204）。反射路径与源生成路径的闭型注册还须防互相误注册/重复初始化。归 W-12/R9。

### 3.5 Java 承接复杂度普查（v6 新增，HEAD a60afc75 实测；8268251a 增补）

> 目的：为层面 J/R11 提供量化底册。口径：只统计 **Transformers/ 内"C# 形状→Java 可行形状"的主动补偿机制**（不含直接渲染），行数为当前文件物理行数；提交证据取 v5 文档提交（9ee2fc26）之后。完整底册见附录 H。

**A. 规模热点（行数 Top 机制）**

| 机制簇 | 关键文件（行数） | 规模 |
|--------|-----------------|------|
| 调用渲染总枢纽 | `InvocationExpressionTransformer.cs`（4785；v5 后 +394） | 单点巨型 |
| 强制适配网（J-03） | `ArgumentTransformer.Coercion.cs`（1098）+`IListCoercion.cs`（266）+`IterableCoercion.cs`（217）+`SpanCoercion.cs`（85） | ≈1666 |
| 集合擦除一致性后处理（J-02） | `Utilities/CompatCollectionConformance.cs`（732，v5 后 +170） | 732 |
| 渲染期文本重建闭型（J-01） | `JsonDeserializeRewrite.cs`（293，**v5 后新增**） | 293 |
| struct 值语义/clone（Java 侧） | `StructCloneHelper.cs`（450）+ Assignment/Struct/ClassTransformer 分散位点 | 450+ |
| 擦除命名模型 | `Context/JavaNaming.cs`（498）+ `ConversionContext.cs`（v5 后 +162） | 498+ |
| 静态成员提升 | `Utilities/StaticMemberTypeParameterHelper.cs`（148）+ Property/MethodTransformer 调用点 | 148+ |
| 条件副作用线性化（J-04） | `StatementTransformer.Loops.cs`（while(true) 重写）等 4 个 Statement partial | 分散 |
| fail-fast（J-06） | 统一构造点 15 调用（2 文件）+ 裸 `NotSupportedException` 54（18 文件） | 69 点 |

**B. v5 后 13 个 Java/compat 修复的性质分类**（每个都是"未预处理形状"的真实发作证据；8268251a 为文档定稿时新增）：

- 闭型/擦除信息在渲染期补回：`JsonDeserializeRewrite` 新增（0dd5258a）、raw/泛型实参/方法组渲染（f271d4ad）；
- 集合/迭代适配网修补：OfType 过滤（69b4db22）、IGrouping 迭代 CCE（0a183e9a）、IList<T>/IList 双实现碰撞（b5adaaea）、数组 IList 适配（f271d4ad）、List.CopyTo 参数校验（bd3966a2）、**泛型/非泛型 `IEnumerator.Current` 并存擦除碰撞→`getCurrentNonGeneric` 改名（8268251a，定稿时新增，J-02 同族）**；
- 语句/求值顺序：while 条件属性自增死循环（a2d3a6f0）、getter pre-statement 泄漏（107f0747 同批测试）；
- BCL 值语义/重载：string.Concat char/string 展平（a60afc75）、Decimal 尾零（e8e69c94）、checked IO（f271d4ad）；
- 测试元数据：MSTest DataRow 与 InlineData 同收集（eeafaa17）、`MSTestClassMetadataEmitter.cs`（130 行新增）。

**C. 三条定性结论**：

1. **补丁位置正在向"渲染后"漂移**：JsonDeserializeRewrite 对 `rendered` Java 字符串做前缀匹配与括号配对——这是全仓第一次在 Java 文本成形后反向手术重建类型信息，标志着"渲染期信息不足"已越过可维护边界（同类信息在 C# 语义模型里本可零成本获得）。
2. **同一形状族反复发作为不同 bug**：集合适配网在 13 个提交中占 6 个（含定稿时的 8268251a），跨 codegen/compat 两层；CompatCollectionConformance 的注释自证跨工程场景下声明端改名与调用点推导靠人工保持一致——正是 C# 侧符号级一次成型可消灭的问题类。
3. **控制流线性化在文本层做会出语义 bug**：a2d3a6f0 的死循环是"预语句放置位置"错误；continue/break 交互在 Java 发射层无法被 C# 编译器验证。这类改写放预处理侧可获得 I1（每中间树可编译）+ B 路差分双保险。

---

## 4. 逐 pass 深度分析（52 pass × 泛型交互）

> 按 stage 分组；真实执行顺序见 §2.1。每个 pass：输入形状 → 输出形状 → 诊断/失败策略 → **泛型交互与缺口**（引用 §5 编号）。v2 修正以 **[v2]** 标注。

### 4.1 Lexical（3 pass）

**conditional**（`Core/ConditionalDirectiveStrip/`）
- 输入：`#if/#elif/#else/#endif`（按项目符号集）、可选 `#region`。输出：只保留存活分支的文本。纯文本无语义模型；parse 错误或后校验失败 → 原文件不变。
- 泛型交互：无。**[v2]** 符号集取**首个 TFM**（`PickTfm = tfms.FirstOrDefault()`）——多 TFM 库（YamlDotNet 5 TFM）可能取错符号集，归 L1 编译层三不一致之一。

**lexical-normalize**（`Core/LexicalNormalize/`）
- 输入：`#line`、`#error`、extern alias、global using、file-scoped namespace、raw/u8/verbatim/`\e` 字面量（opt-in）。输出：规范化文本。纯语法、幂等、guard 诊断 + 回滚。
- 泛型交互：无直接交互；`u8` 字面量归 C43 规则。

**partial-merge**（`Core/PartialMerge/` + `Core/PartialType/`）
- 输入：跨文件 `partial` 类型。输出：合并至单一声明（primary 的 `TypeParameterList` + 全部 part 的约束子句合并，`PartialTypeMergeBuilder.cs:55, 138-150`）；类型参数不一致 → `CS2J2011`；解决方案级对非白名单多部件类型报 `CS2J2018` Error（YamlDotNet 实测中止点之一）。
- 泛型交互：已接——`Foo<T> where T : class`（part1）+ `Foo<T> where T : IEquatable<T>`（part2）合并为双约束声明。约束语义使用仍依赖 G-06 下游处理；partial 方法上的类型参数由 generic-deepen CS2J1102 处理。

### 4.2 Declarative（15 pass；variance-audit/ref-struct-lowering/ref-struct-span-field-memory-lift 因泛型/域关联划入 §4.6/§4.7）

**local-function-lift**（`Core/LocalFunctionLift/`）
- 输入：局部函数（值捕获/引用捕获/静态/嵌套）。输出：类级 `private static` 方法；捕获降级为参数。
- 泛型交互：泛型局部函数类型参数提升时保留；与外围方法类型参数同名冲突 → skip（`EnclosingTypeParameterConflict`，`LocalFunctionLiftAnalyzer.cs:236-244`）。**[v2] S-05**：自有+外层类型参数 arity 错位（提升签名 `<U,T>` 但调用点补参少一个 → CS0308）且无 pass 内回滚（`LocalFunctionLiftRewriter.cs:256-291, 624-662`）——修 R1 统一解析器。

**enum-declaration**（`Core/EnumDeclaration/`）
- 输入：`[Flags]`、非 int 底层、显式值 enum 与全树使用点。输出：enum-like sealed class + `GetValue/FromValue`；不可表示 → `ED020` 保真 enum。v1 后已接管 enum 算术（`NORM-ENUM-ARITH`）。
- 泛型交互：`where TEnum : struct, Enum` 泛型工厂中 TEnum 类型参数上的算术/位运算/HasFlag **无路径**（分析器按具体枚举符号分派）；**[v2] G-20**：ED020 保真面过宽——枚举作任意深度泛型实参/经数组/`Nullable<>`/嵌套泛型实参可传递、`struct,Enum` 约束、表达式树、反射可见成员都触发整 enum 保真，压低转换率（`EnumDeclarationAnalyzer.cs:1041-1088`）。

**delegate**（`Core/DelegatePreprocessor/`）
- 输入：委托 `+ - += -=`、匿名方法。输出：`CsPrelude.Combine/Remove`、lambda。类型无关（运算符分派），泛型委托同样改写。
- 泛型交互：已接；`DelegateOps.Equals` 刻意省略泛型实参交 C# 推断。**[v2]** `Delegate.Combine<T>/Remove<T>` 泛型入口无处理（CN-10 候选，归 R2/P1-R1 扩展）。

**tuple**（`Core/TupleToStruct/`）
- 输入：元组类型/字面量/命名元素/解构/`out var`/foreach 解构/`ValueTuple` 工厂。输出：生成的 `Cs2jTuples.TupleXxx` struct（开放形状为泛型 struct，形状键含类型参数槽位，`TupleShapeRegistry.cs:8-94`）。
- 泛型交互：**[v2 修正 S-04/G-09]** 槽位机制存在但**生成体零约束发射**（`TupleStructFileContent.cs:48-60`：泛型参数 `<__P0,...>` 裸发，不复制源约束；`Nullable<__P0>` 字段 CS0453）——从 v1"已接"降级为"机制在、约束债"。修 R1。

**anonymous-type-lift**（`Core/AnonymousTypeLift/`）
- 输入：`new { ... }`、投影缩写。输出：`internal sealed` 不可变类。
- 泛型交互：已接——`AnonymousShapeRegistry` 有类型参数槽 + 约束携带（`:15-33, 79`），泛型方法内投影生成泛型匿名类。与 linq 泄漏参数擦 object 的交互是保守点（见 §4.4 linq-channel）。

**strip-default**（`Core/DefaultParameterStripper/`）
- 输入：默认参数、`params` 集合（C48）。输出：生成重载（重挂 `TypeParameterList`，`DefaultParameterOverloadBuilder.cs:60/271/307`）；调用点改具名实参。
- 泛型交互：泛型方法默认参数已接（`Serialize<T>(T, options = null)` 正确展开）。**[v2] G-21**：params 集合谱系——`params Span<T>/ReadOnlySpan<T>` 声明物化+调用点展开已支持（K2）；`ReadOnlySpan<char>` 字符面（CS2J0601）与 ref struct 元素（CS2J0608）不支持；`[CollectionBuilder]` 一律 CS2J0607（`ParamsCollections/`）。

**record-readonly**（`Core/RecordReadOnly/`）／**make-readonly**（`Core/ReadOnlyStructMaker/`）／**struct-value-semantics**（`Core/StructValueSemantics/`）
- 输入/输出见 v1 方案 §3.2（可变 record/struct → readonly 化 + 变异点改写 + 防御性 Clone 插入）。
- 泛型交互：基本已接——泛型 struct 的 readonly 化与防御拷贝不依赖元素具体类型；`BoxingReachabilityAnalyzer` 显式处理 `ITypeParameterSymbol`。**[v2]** 泛型 record 域补三条无 owner 形态（G-15 增量）：RR070 泛型接口方法 ripple 阻断、CS2J5223 开放泛型 `with` 接收者跳过、CS2J5196 "deferred to C02 review" 无承接者。

**attribute-normalize**（`Core/AttributeNormalize/`）
- 输入：`Caller*` 调用点、JSON/序列化/PInvoke 属性盘点。输出：注入 `Caller*` 实参、属性清单。
- 泛型交互：属性参数 `typeof(X<>)` 取泛型定义裸类名（`KnownAttributeCatalog.cs:441`）。缺口 G-08：泛型 attribute 降级归 P1-R3（expression-desugar 0587 只守门不降级）。**[v2]** 审计只挂 TPA 引用（AN004 NuGet 黑洞）且盘点不落 JSON——归 L1/报告 v2。

**member-desugar**（`Core/MemberDesugar/`）
- 输入：属性/索引器/事件/运算符/finalizer/签名/`field` 关键字/record `with/init/required`。输出：backing field + 显式方法/成员。
- 泛型交互：大部分已接——显式接口实现（含泛型接口）展开；泛型类运算符展开；static abstract 接口成员全部拒绝（事件 unsupported、属性 5116、算子 CS2J5175、签名 5198）。**[v2]** 泛型擦除协变/桥接三形态（5195/5196/5223）无 owner → `NORM-ERASURE-COVARIANCE`（Domain 策略首批使用方）；TPA-only 误判已有实证（`PropertyFacts.cs:269-282` 为 MSTest 不参与绑定加启发式）。

**thread-static**（`Core/ThreadStaticRewrite/`）
- 输入：`[ThreadStatic]` 字段。输出：`ThreadLocal<T>` holder + `.Value`（字段类型原样渲染，`ThreadStaticRewriter.cs:520-540`）。
- 泛型交互：**[v2 修正 S-06/G-03 关联]** 泛型容器中的 `[ThreadStatic]` 字段被 pass **显式停用**（`ThreadStaticAnalyzer.cs` InGenericContainer 分支："per-closed-type sharing cannot be expressed in v1"）；`RewriteGenericContainingTypes` 选项默认关。正确形态：`ConcurrentDictionary<string, ThreadLocal<object>>` + 类型化访问器（GD-FIELD holder 的 ThreadLocal 变体），登记 `NORM-GENERIC-THREAD-STATIC`（Domain）。

**concurrency-normalize**（`Core/ConcurrencyNormalize/`）
- 输入：`Monitor.*`、`Volatile`、`System.Threading.Lock`。输出：规范 `lock` / 显式 enter-try-finally / compat 桥。
- 泛型交互：CN-8/CN-9 静态 API 不动——`Interlocked.CompareExchange<T>`（`where T : class`）原样透传；`Volatile.Read/Write<T>` 只计数 CS2J2960。归 G-06 约束语义清单。

### 4.3 Expression（11 pass，含新增 compat-method-replacement）

**null-coalesce**（`Core/NullCoalesceRewriter/`）
- 输入：`??`/`??=`。输出：三元/if 赋值 + 临时；复杂接收者物化。无锚点语句（字段初始化器等）→ Warning 残留。
- 泛型交互：`Nullable<T>` 左操作数识别读 `.Value`；临时一律 `var`。**[v2] G-18 关键事实**：残余 `??` 在 strict 下 Error **中止整链**——YamlDotNet 实测 `?? []`（`TypeConverterCache.cs:39`，集合表达式超出本 pass 能力而其后 的 expression-desugar 才能处理）触发链中止，泛型 pass 因此从未在默认链上执行。修法：pass 对"后续 pass 有 owner 的构造"保守跳过并登记，而非 Error（顺序性失败策略）。

**expression-desugar**（`Core/ExpressionDesugar/`）
- 规则序：Interpolation(C23) → Initializers(C24, off) → NullConditional(C35) → CollectionExpr(C36)；strict 下任一 Error 整文件回滚。
- 泛型交互（C36 为主）：接口目标仅 5 个单类型参数接口 → `List<T>`；`TryFindPatternAdd` 排除泛型 `Add<T>/AddRange<T>`；`[CollectionBuilder]` 开放泛型 builder 拒绝 0580；新语法守门（static abstract 0585 / 泛型 attribute 0587 / scoped 0586）；目标类型 `new()` + 集合表达式组合不分派。缺口归 P1-R8。

**enum**（`Core/EnumPreprocessor/`）
- 输入：enum 位运算/`HasFlag`/可空 enum。输出：底层整数运算 + cast。泛型交互：无（类型参数枚举归 enum-declaration/G-20）。

**compat-method-replacement**（`Core/CompatMethodReplacement/`，**[v2 新增]**）
- 输入：`CompatMethodReplacementRegistry` 中的全限定签名方法声明。输出：**只替换方法体**为托管实现（签名/属性/修饰符保留）；内置 `JsonWriterHelper.ToUtf8`（指针转码 → `Encoding.UTF8.GetEncoder().Convert`）与 `NeedsEscaping`（`fixed(char*)` 扫描 → 托管 `WillEncode` 循环），使 unsafe-ffm 不再拒绝。
- 泛型交互：无（机制为签名级方法体置换）；**作为硬域攻坚范式**——为不可降级热点提供配置化逃生门，与 R3 消费面配置化同构（条目化而非代码特判）。
- **[v3] W-09 签名漂移实锤**：`ToUtf8` 条目的键与参数表（`Registry.cs:306-330`：`ReadOnlySpan<byte> utf16Source, Span<byte> utf8Destination, out bytesConsumed, out bytesWritten` 4 参）拟合的是旧语料 3.x 的 `JsonWriterHelper.Transcoding.cs:28`；Runtime 10 把指针转码迁到 `JsonWriterHelper.cs:291` 并改签名为 `ToUtf8(ReadOnlySpan<char> source, Span<byte> destination, out int written)` **3 参 char-span**（且 `#if NET` 分支直接走 `Utf8.FromUtf16`、指针体在 `#else`——net10 符号集下甚至无需置换）。条目在 rt10 上零匹配且**无漂移告警**（`MatchedEntryKeys` 只记命中，不报"声明类型存在但零匹配"）。修法归 §6.4-R0：键按 rt10 重写 + 注册表漂移探测（M0）。

**foreach-deconstruction**（`Core/ForeachDeconstruction/`）
- 输入：`foreach (var (k,v) in ...)`。输出：普通 foreach + `var` 局部解包。`KeyValuePair<K,V>` 按双实参判定。
- 泛型交互：**[v2 修正 S-07]** K/V 为**类型参数**（开放组件）时 `ForeachDeconstructionRewriter.cs:383-417` 强制 INamedTypeSymbol → 整站保留，并被 foreach-source **CS2J5420 硬门**放大（G-13 关联）。

**numeric-boundary**（`Core/NumericBoundary/`）
- 规则序：C17 → C22 → C15 → C34；NB-12 任一 Error 整文件回滚。
- 泛型交互（缺口）：泛型算术零覆盖（`TryGetArithmeticDomain` 只认 4 整型 SpecialType + TypeKind.Structure，`NumericBoundarySyntaxFacts.cs:70-93`——T 上的 checked 静默不改写）；`T?` 不判 lifted；用户定义 checked 算子 Deferred 给 member-desugar；C22 lifted equality 已接。

**array-method**（`Core/ArrayMethodRewriter/`）
- 输入：数组实例/静态方法、多维数组。输出：`Cs2jArray.ArrayHelper.*`；**显式泛型实参保留**（丢 `<T>` 会落非泛型重载，注释 `ArrayMethodSyntaxRewriter.cs:125-127`）。
- 泛型交互：`T[]` 任意元素类型已接；`Array.Empty<T>()` 不在名字表（观测项）。

**index-range**（`Core/IndexRange/`）
- 输入：`^`/`..`。输出：基于长度 int 运算/`Slice`。ReceiverKind 显式覆盖 `Span<T>/Memory<T>/ImmutableArray<T>/ArraySegment<T>`。
- 泛型交互：无缺口。

**equality-concat**（`Core/EqualityConcat/`）
- 输入：`string ==/!=`、字符串 `+`、`(object)` 逃逸、委托相等、record `==`。输出：`string.Equals/Concat`、`ReferenceEquals`、`DelegateOps.Equals`、`object.Equals`。
- 泛型交互：`T` 上 `==/!=` 归 PredefinedOther 透传（装箱载体语义恰好对齐，自定义 `IEquatable<T>` 值类型实例化时行为分裂）；**`EqualityComparer<T>.Default` 零处理 + TypeMappings 无该静态类映射**（G-11，v2 复核仍成立）→ P1-R4 `EqualityOps`；泛型委托相等省略类型实参；lifted 相等移交 C22。

**extension-methods**（`Core/ExtensionMethodResolve/`）
- 输入：扩展方法调用、C#14 extension block。输出：`global::Host.M(args)`（EM-GEN 方法名 verbatim：显式实参保留、推断实参保持省略；EM-GEN-HOST 嵌套泛型宿主带实参渲染）。LINQ 宿主保护。
- 泛型交互：G-14 核心（推断实参不物化）；**[v2 实测]** json 612 external hosts 中 **41 无映射**（CS2J1203 撞号码）——外部宿主映射面独立缺口；回滚按编译错误 diff 文件/整树。

**pointer-diff-inline**（`Core/PointerDiffInline/`，**[v2 新增]**）
- 输入：文件内"同元素类型指针差值取整"私有静态小助手。输出：调用点内联 `(int)(x - y)`（char\* 形态含重解释与 `>>> 1`）；零引用声明删除。
- 泛型交互：无。

### 4.4 ControlFlow（4 pass）

**foreach-source**（`Core/ForeachSource/`）
- 规则链：C16 → C40 FA（duck-type 枚举器适配）→ M3 FR（ref foreach 索引循环/ref-Current 骨架）。`IEnumerable<T>` 按 `OriginalDefinition` 判定；`Span<T>` 按元数据名。
- 泛型交互：泛型枚举器按成员签名解析已接；**[v2]** CS2J5420 解构残余硬门（放大 S-07）。

**pattern-desugar**（`Core/PatternDesugar/`）
- 组件注册：仅 SwitchStatement + CatchClause——**C37 组件（is/switch 表达式/positional/list/slice）未注册**，list pattern 全落 PD010 不可归约。
- 泛型交互：`x is T t`（T 参数）经 `(T)(object)x` 中转 + 自比较绑定已接；类型参数 governing switch 降级 if 链；relational pattern 只发裸比较运算符（不生成 `IComparable<T>.CompareTo`——接口/类型参数主体形态无 owner）；类型参数 catch 与 GD-CATCH 协同。缺口归 P1-R6。

**eliminate-goto**（`Core/GotoEliminator/`）
- 纯语法状态机；方法级失败。out 参数默认值 `default(T)` 对类型参数天然成立。无泛型缺口。

**linq-channel**（`Core/LinqChannel/` + `Core/LinqRewrite/`）
- 三路路由：Enumerable（可展开）/ Queryable+ExpressionArgument（逐节点冻结）/ 链级保守（一个保留节点保留整链）。
- 泛型交互：LINQ 方法定义泄漏的类型参数在合成集合时擦为 object（`LinqRewriter.cs:750-774, 1097-1101`）；**[v2]** `Enumerable.GroupBy` 任何重载都冻结（M4 保守，`LinqChannelRewriter.cs:169-177`）；Expression 三 pass 策略分散无 owner（CS2J1110 Skip / LC0010-0012 Preserve / CS2J1210 RewriteWithIdentityAssert）——G-23；**[v2] W-04**：`EnsureEnhancedReferences` 已实现但调用点被一行注释（`LinqChannelPassAdapter.cs:105`）。

### 4.5 StateMachine（3 pass）

**closure-lift**（`Core/ClosureLift/`）
- 四阶段：HolderLift → NamedMethodLift → DisplayClassConsolidation → DelegateSiteRewrite；任一 Error 文件回滚。
- 泛型交互：M1（`LiftedTypeParameterResolver`）系统——提升方法重声明实际引用的外层类型参数（含 where 闭包）、仅 where 参数的委托站点补显式实参；display class 按需泛型化；泛型 this 捕获渲染 `C<T,U>`；隐式 this 泛型调用有专门 VisitGenericName；递归泛型 lambda 同一证明路径；泛型推断位 accounted skip（G-14 观测点）。**[v2 修正 S-03/G-13]**：M2/M3 约束债三处实锤（`HolderSyntaxGenerator.cs:70-76`、`DisplayClassSyntaxEmitter.cs:120-128` 重声明无 `WithConstraintClauses`；`LambdaMethodLifter.cs:502-526` 迁移方法 `typeParameterList: null`）——修 R1。

**iterator-lowering**（`Core/IteratorLowering/`）
- 输入：方法/getter 体自身持有 yield 且返回类型仅 `IEnumerable<T>/IEnumerator<T>/IEnumerable/IEnumerator`。
- 泛型交互：状态机按需泛型（方法 `TypeParameterList + ConstraintClauses` 复制进状态机类、工厂 `new StateMachine<T...>`、`_current` 强类型）——**json 实测 55 iterators / 679 yield / 0 residual**。已接（其约束复制的正确性正是 S 层其他 pass 的对照样板）。

**async-normalize**（`Core/AsyncNormalize/`）
- 输入：task-like await、`ConfigureAwait`、async void、async lambda/局部函数、await foreach/using、async 迭代器。
- 泛型交互：异步迭代器状态机按需泛型 + `_current` 强类型；泛型 async lambda 委托 Invoke 须四型 task-like；类型参数前置（`ResolveGenericCarry`）；值类型捕获 defer（json 实测 21 lifted / 4 residual 全为 async void）。**[v2 修正 S-08]**：类型参数**约束传递闭包**不完整（`where T : IFoo<U>` 漏带 U → CS0246）——修 R1。

### 4.6 Abi（14 条目：Abi stage 11 个 + 因泛型关联划入的 variance-audit / using-resource-lift / ref-struct-lowering，泛型主战场）

**ref-out-in**（`Core/RefOutInPreprocessor/`）
- 输入：`ref/out/in/scoped` 参数、ref 局部/返回/属性/索引器、委托 ref 参数、ref struct holder、数组元素代理。输出：`ByRef<T>` holder / `ScopedByRef` / accessor 闭包 / copy-back。
- 泛型交互：`ref T/in T/out T` 直接走 `ByRef<T>`；Java 擦除下泛型 out 回读 NPE 已由 `ReadbackXxx` 修复（仅 12 基元）。边界（Locked 保留）：外部元数据/extern/接口虚实现 ref 契约。**[v2]** `where T : allows ref struct` 的参数误产 `ByRef<T>` → CS0306（`RefOutInAliasAnnotator.cs:242-269`）；`ref struct` 参数走 `ref RefStructByRef<T> where T : allows ref struct` 槽（C# 13 面）。

**unsafe-ref-lowering**（`Core/UnsafeRefLowering/`）
- 输入（窄）：`ref T x = ref MemoryMarshal.GetReference(span)` 局部 + 周边 `Unsafe.ReadUnaligned<U>/Add`。输出：`MemoryMarshal.Read<U>(span)` / `span.Slice(k)` / `span[k]`。
- 泛型交互（G-12a）：`<U>` 限定标量集；span 元素为类型参数拒绝 URL001；**`Unsafe.As<A,B>`/`MemoryMarshal.Cast<A,B>`/`WriteUnaligned<T>`/用户直写 `SizeOf<T>()` 零处理路径**——P1-R7 + GI 规则族。

**using-resource-lift**（`Core/UsingResourceLift/`）
- 输入：非 await using 且资源含 ref 读回（语法 `ByRef<...>` 名匹配）。输出：资源提升为局部声明。泛型交互无额外缺口。

**fixed-span-to-array**（`Core/FixedSpanToArray/`，**[v2 新增]**）
- 输入：span-backed `fixed`（`ReadOnlySpan<T>/Span<T>`）。输出：`T[] __cs2j_fixed_N = span.ToArray(); fixed (T* p = __cs2j_fixed_N)`；可写 Span 保留临时 + `finally` 回写（`MemoryExtensions.AsSpan(...).CopyTo(...)`）；`&MemoryMarshal.GetReference(span)` 解包；数组/字符串/stackalloc/指针等其它 fixed 源保留给 unsafe-ffm R3。
- 泛型交互：span 元素为类型参数不特判；多变量/体含 await/yield 等不可证形态 FSA001 保留。**[v2]** 边界加固项：`IsWritableBodySafe` 的 symbol-null 放行改保守拒绝；ReadOnlySpan 指针写/别名观察补差分（M0）。

**unsigned**（`Core/UnsignedTypeRewriter/`）
- 输入：无符号关键字/字面量/cast/运算。输出：`System.*` 全名 + `UnsignedMath`。泛型实参中的关键字同样替换（`VisitTypeArgumentList`）。已接。

**numeric-boundary 之外的 Abi 序**——见 §4.3 numeric-boundary 条目（其 stage 为 Expression）。

**generic**（`Core/GenericPreprocess/GenericPreprocessor.cs`）
- 仅 3 条规则：`default(T)`→`default`（两个安全跳过上下文：`==/!=` 操作数、无显式实参泛型调用实参）；`new T()`（无参无初始化器）→`Activator.CreateInstance<T>()`；`new T[n]`（一维）→`(T[])Array.CreateInstance(typeof(T), n)`。`typeof(T)/is T/as T` 仅统计。
- 泛型交互缺口（G-01）：`new T(args)` 不改写；`sizeof(T)` 无处理；**[v2 V-08 关联]** `default` 字面量落到 Java 侧对类型参数渲染 `"null"`（值类型实例化语义错误）。设计注意（CN-01 附注）：default 类降级**不得经 Activator**（带自定义无参构造器的 struct 语义坑）。**[v5]** 统计口径漂移：`DefaultExpressionsRewritten` 在跳过判定前递增（`:115-116`，"Count ... regardless of whether we rewrite"）——名 Rewritten 实 Encountered，报告统计与实际改写数不可对账（修 §6.4-R0 v5 项）。

**thread-static**：见 §4.2（Declarative）。

**generic-deepen**（`Core/GenericPreprocess/GenericDeepen*.cs`，C02）
- 规则族：GD-ANCHOR（方法级 `Type __cs2j_token_T` 锚点/抽象成员物化 stub）、GD-NEST（闭包链锚点）、GD-MTOK（泛型方法尾插 token 参数 + 调用点传闭型 token；条件：全部调用点显式实参、非虚/非族成员）、GD-STATIC（静态方法提升 + 调用点同步）、GD-FIELD（静态泛型字段 → 非泛型 holder + AssemblyQualifiedName 键分槽 + 双检锁）、GD-CTORARR（`new T[]` 后 typeof 站点 → 构造器尾参 `__cs2j_class_T` 注入，保留原签名 + companion 重载；拒嵌套类型/字段初始化器/静态上下文）、GD-ISAS-B（`if (x is T v)` 单求值 if）、GD-CATCH（`catch(T)` → Exception 过滤，带 `when` 不支持）；表达式树内绝不插锚点（CS2J1110）。
- 泛型交互（v2 修正五点）：
  1. **命名二分更正**：`__cs2j_token_*` 是 GD（C02）锚点前缀；`__cs2j_typeof_*` 是 generic（E10）哨兵前缀（`GenericDeepenSyntaxFacts.cs:9-10`）——v1 首版张冠李戴，更正。
  2. **S-01 作用域盲区**：分析只遍历 `MethodDeclarationSyntax`（`GenericDeepenAnalyzer.cs:1417-1449`）——ctor/accessor/operator 体内 typeof(T)/is/as 不分析不锚点（Java 侧已支持这些作用域，preprocess 单方面补齐即可）。
  3. **V-01/02/03 GD-FIELD 三缺陷**（holder singleton 每访问重建 / 同型多字段 gate+store 重复成员不可编译 / ParamRef 比较器分裂）；**CS2J1106 调用点闭型不可见**（json 3 实测：同泛型类实例方法内调静态方法，`this` 闭型语法不可见 → RenderMethodTypeArguments null）。
  4. **S-02 嵌套闭包 token 不可达**：表达式体 lambda 委托形状不可解析 → CS2J1101 只告警且残差计数器报 0（json 37 实测）；修 GD-NEST-2（包块体）。
  5. **V-04 stub ABI**：abstract→virtual+NotImplementedException、接口成员默认抛错体。
- 残留门五形状（static generic field/catch-type-param/type-param-pattern/裸 typeof(T)/is-as T）+ overrides JSON 豁免；**[v2]** 无 CLI 旋钮（`PassChainFactory` 硬编码 `new GenericDeepenOptions()`，Overrides/strict 不可达，G-24 关联）；java-ready 不自动 strict（W-01）。

**closed-metadata**（`Core/ClosedMetadata/`，C30）
- CM-ENUM-1（签名闭型）/-2（显式 typeof 闭型）/-3（序列化入口实参）/-4（基类型/接口构造型，覆盖 `JsonConverter<T>` 家族）；AOT 源生成面排除（CS2J1204）；开放类型参数 → CS2J1210 只计数。
- 输出：每工程 `_Cs2jClosedTypes.<Project>.cs`（纯 C# 登记文件）+ `_Cs2jGenericHolders.*.cs`；为 ImplicitUsings 工程合成全局 using 树；注册文件不发射 Java。
- 泛型交互（v2 修正）：**V-05** 键并入 NRT 注解（`List<string>` vs `List<string?>` 分裂）；**V-06** collector 递归 vs linker 顶层判定不一致；**W-05** 消费面硬编码 STJ 5 入口（4 方法 + `GetTypeInfo`，`ClosedMetadataCollector.cs:42, 279-288`），`ConsumerSurfaces` 未实现；**W-07** Java 侧注册-解析-缓存已闭环（`compat/TypeHelper.java:1255/1270` + `ClosedTypeMetadataTest`；`ClosedMetadataRegistrationGenerator.cs:9-11` 过时注释待清理）；**json 实测 588 registered / 2436 enumerated / 281 residuals / 369 dynamic skipped / 0 links**（CS2J1220 Warning 放行）；单 pass 运行时 `CompilationReadyAsync` 早退不 BindCompilation 会空引用崩溃（顺序依赖）。P1-R5/R3/RG 反射分级（§6.4）为升级路径。

**variance-audit**（`Core/VarianceAudit/`，C33）
- **只读 pass**：声明清单（VA001）+ 使用点分类（Safe/NeedsWildcard/Unsafe）+ 约束审计；`--variance-audit-escalate` 链内升级。从不改写。
- 泛型交互（G-05）：json 实测 0 声明 / 704 位点（163/66/475）/ 221 转换 / 53 unsafe / 25 约束 / **126 baseline-error 文件跳过**（W-08 假阴性）；VA010 消息明文 "await the use-site rewrite pass" 但该 pass 不存在（`UseSiteSafetyClassifier.cs:1132-1157`）。**[v2 修正 G-06 性质]**：约束审计 VA020-027 **已存在**（含 struct/new()/unmanaged 依赖位点计数与 "definite semantic error" 判词，`ConstraintAuditor.cs:354-356`）——缺口从"无清单"改写为"门禁未接"（W-01）。

**unsafe-ffm**（`Core/UnsafeFfm/`，C06，R1-R9）
- R1 剥 unsafe（`sizeof(具名 struct)` → 生成 `Unsafe.SizeOf<T>()`，类型参数不改写）；R5 指针签名归一（泛型指针 `T*` UF021 拒绝）；R6 指针表达式索引化（托管重载展开跳过泛型候选）；R4 stackalloc 堆化（`stackalloc T[n]` UF040 整文件拒绝，**零单测**）；R7 ref struct 声明（Span/ReadOnlySpan 白名单硬编码；ref 字段 UF050；纯数据 UF052）；R8 extern（泛型 interop UF061）；R9 ref struct using 剥除（非白名单 UF055）。
- 泛型交互（G-12）：泛型指针/stackalloc/extern 域决策；**[v2]** R7 与 ref-struct-lowering 对 `AllowsRefLikeType` 识别口径分叉（可致 CS8345）。

**stackalloc-lowering**（`Core/StackAllocLowering/`）：unsafe-ffm R4 残余 post-pass（Span 目标堆数组/裸指针 Span 构造/条件表达式 AsSpan 回退/空 unsafe 块剥离）。自身无类型参数独立防护（依赖 R4 前置拒绝）。

**ref-struct-lowering**（`Core/RefStructLowering/`）：纯数据型去 `ref` token；保留条件 RSL001（ref 字段/ref 返回/指针/ref-like 字段含 `Span<T>/ReadOnlySpan<T>/Memory<T>` 无论 T、`allows ref struct` 反约束字段、嵌套）。与 R7 双强度处置不一致（RSL=Warning 保留，R7=Error 回滚）。

**pointer-deref-index**（`Core/PointerDerefIndex/`）：`*p`→`p[0]` 族；判定只看 `IPointerTypeSymbol` 不查 pointee；`&` 永不触碰（NORM-POINTER-OP 残留即 `&`）。

**platform-isolation**（`Core/PlatformIsolation/`）：边界表四模式，无泛型交互。

**dynamic-desugar**（`Core/DynamicDesugar/`）：M1 Audit / M2 可证 P1 子集；**P1 具体化拒绝一切开放泛型**（`FlowTypeProver.cs:795-847`）；**[v2]** DS017/DS019 涉泛型反序列化（Newtonsoft 族）为 Yaml 类库关联观测面。

### 4.7 域攻坚后置（1 pass，[v2 新增]）

**ref-struct-span-field-memory-lift**（`Core/RefStructSpanFieldMemoryLift/`）
- 输入：用户 ref struct 的 `ReadOnlySpan<T>` 字段/自动属性。输出：`ReadOnlyMemory<T>` 存储 + 同名 Span 属性 + `__cs2j_ToMemory(...)` 写穿透（default span 保持 default 语义）+ 去 `ref` 修饰符——**`Utf8JsonReader` 摆脱 ref struct 的关键**。保守保留 RSM001：可写 `Span<T>`/ref/指针/ref-like 字段、带初始化器、非私有、被 `ref/out/&/nameof`/匹配字符串引用、非简单赋值；嵌套 ref struct 保外层 `ref`；partial 按符号级统一（helper 只注入一次）。
- 泛型交互：`ReadOnlySpan<T>` 的 T 为类型参数时照常提升（字段符号级）；与 R7/lowering 的 `AllowsRefLikeType` 口径分叉见 §4.6。
- **[v3] rt10 真形核证**：Runtime 10 的 `Utf8JsonReader` 仍是 `public ref partial struct`（3 文件 partial）；字段面 = `private ReadOnlySpan<byte> _buffer`（ctor 简单赋值）+ **公共自动属性 `ValueSpan { get; private set; }`**（带私有 set）+ `ReadOnlySequence<byte> _sequence`（非 Span、判据正确跳过）。机制面吻合：候选过滤只认 `ReadOnlySpan` 存储（`RefStructSpanFieldMemoryLiftPreprocessor.cs:158-186`），且 `:775` 有 SetAccessorDeclaration 分支（set 访问器有处理路径）；旧语料的同形 reader 已实证可去 ref。rt10 增量暴露面：**方法族大量 `readonly` 实例成员修饰**；ref struct 全家清单为 4 型——reader（公共 API）、`PropertyNameSet`、`GenerationState`、`PropertyHierarchyResolutionState`（后三者为内部 helper 且带主构造，`JsonReaderState`/`ValueQueue<T>` 均非 ref）。"reader 摆脱 ref struct"的完整判据 = reader 本体可去 ref 且三个内部 ref struct helper 不阻断编译，归 M0 fixture（§7.1-5）。

### 4.8 FinalNaming（2 pass）

**java-keywords**（`Core/JavaKeywords/`）：Java 保留字改名 + `operator true/false` → `IsTrue/IsFalse` + `nameof` 字面量化；整树编译驱动 + 错误归属原子回退（JK030/JK031）。类型参数名撞保留字在重命名面；全引用传播未逐项验证（一致性测试项）。

**normalization-audit**（`Core/Normalization/`）：只读探针链（10 个 NORM-\*，全 Fail）+ `preprocess-residual-report.json`（schema v1 含 byCode/byOwner 覆盖计数）+ 支撑库豁免 + java-ready 强制路径。**泛型零探针——L0 层主扩展点（§6.3）**。

### 4.9 共享产物与支持库（v2 增量）

v1 表继续有效，增补：`_Cs2jGenericHolders.<Project>.cs`（GD-FIELD holder，json 实测 3 文件）、`CompatMethodReplacementRegistry`（代码级注册表）、`_Cs2jTupleTypes.cs`/`_Cs2jAnonymousTypes.cs`、override 面（`ReadOnlyStructOverrides.json`/`JavaKeywordsOverrides.json`/`CsPlatformIsolation.json`/`Cs2jDynamicAudit.json`）。结构债：产物拼写前缀已集中于 `GenericDeepenSyntaxFacts.cs`（`__cs2j_token_`/`__cs2j_class_`/`__cs2j_get_`），但 C# 侧注册键与 compat `closedCacheKey` 的**同构契约**未文档化（双边评审制，R0/产物契约冻结）。

---


## 5. 缺口矩阵（G-01~G-15 修正 + G-16~G-24 新增 + G-25~G-27 v3 + G-28~G-32 v5 + G-33~G-39 v6 + G-40~G-44 v7）

> 威胁形态：T1=Java 编译失败、T2=单元测试行为错误（隐性最危险）、T3=能力空白需域决策、T4=低危观测。v2 对 v1 行的**状态修正**以 ← 标注；新行 G-16 起为 v2 新增（来源标注 D/K/L/S = deepseek/kimi/longcat/seed 方案）。

| # | 特性 | owner（现状） | 缺口与 v2 修正 | 威胁 |
|---|------|--------------|----------------|------|
| G-01a | `default(T)`/`new T()`/`new T[n]` 三式 | `generic`（3 规则） | 已接；← **V-08**：default 于值类型约束 T 在 Java 兜底 null | T2 |
| G-01b | `new T(args)` 带参构造 | 无 | 穿透（不变） | T2 |
| G-01c | `sizeof(T)`/`Unsafe.SizeOf<T>()`（参数 T） | 无 | 穿透（不变）；补 `Marshal.SizeOf<T>` 同域 | T4 |
| G-02a | `typeof(T)`/`is T`（pattern 形式）/`catch(T)` | GD 族 | 方法体作用域已接；← **S-01 作用域盲区 + S-02 闭包不可达（37 实测）** | T2 |
| G-02b | 泛型反射链 `typeof(T).GetGenericArguments()` 等 | 无 | 行为面未审计（不变；TypeHelper 映射存在） | T2 |
| G-02c | `x as T`（二元式）与 `x is T`（二元式） | **双侧均不改写** ← 修正 v1"已接" | V-07：C# 只登记、Java 无类型参数分支 | T1/T2 |
| G-03 | 泛型类 static 字段 per-instantiation | GD-FIELD holder | ← 机制在但 **V-01/02/03 三缺陷**；静态属性 Java 侧有提升（deepseek"无载体"论断不成立）；CS2J1106 调用点闭型不可见（3 实测） | T2 |
| G-04a | 未绑定 `typeof(X<>)` | closed-metadata 登记 | 已接（不变） | — |
| G-04b | `MakeGenericType(闭型)` 静态可解 | 无 | 未物化（不变；P1-R5） | T2 |
| G-04c | 运行时合成（args 含运行时 Type） | CM 不登记（CM-DYN 只计数，369 实测） | 域决策（不变）；补 **RG-0~4 反射分级**收口思路（§6.4） | T3 |
| G-04d | 消费面硬编码 STJ | closed-metadata | **[W-05]** Yaml/其他库入口不枚举；`ConsumerSurfaces` 未实现 | T3 |
| G-05a | 自声明 `out/in` | variance-audit 只读 | 无降级无审计（不变；json/Yaml 实测均 0 声明——两语料都不触发，恰证明"任意库"口径的必要性） | T2 |
| G-05b | 使用点协变转换（接口族） | Java 侧三桥 | 桥覆盖面无审计（不变；json 264 全为数组协变） | T2 |
| G-06a/b/c | 约束 `struct`/`new()`/`notnull,unmanaged,T:U` | variance-audit **审计已有** ← 修正 v1"无清单" | **W-01：门禁未接 + strict 未挂钩**；V-08/S 层是其行为面 | T2 |
| G-07 | static abstract/virtual 接口成员 | expression-desugar/member-desugar 守门 | 拒绝式非 lowering（不变；json/Yaml 均 0） | T3 |
| G-08 | 泛型 attribute | expression-desugar 0587 守门 | 拒绝式非 lowering（不变） | T3 |
| G-09 | 开放泛型元组 | tuple 槽位机制 | ← 修正："机制在但 **S-04 零约束发射**" | T1 |
| G-10 | `Nullable<T>` 泛型包装/未绑定 `Nullable<>` | CM + Java 解包 | 边缘告警（不变）；← 补 **V-05 键分裂**与 NRT 元数据保留契约（§3.3 链 1） | T4 |
| G-11 | `EqualityComparer<T>.Default`/`Comparer<T>.Default` | **[v6] 半修**：`Comparer<T>` 已映射（TypeMappings:380→compat `Comparer.getDefault()`）；`EqualityComparer<T>` 静态类仍无映射，tuple 生成物靠 Java 硬编码（J-07，`InvocationExpressionTransformer.cs:790-815`） | 残余：用户态 EqualityComparer 静态类映射 + 生成物改发中立 helper（JE-07） | T2 |
| G-12a | 泛型 unsafe 内在函数 | unsafe-ref-lowering 3 API | 不变；补 `Unsafe.As/BitCast/Marshal.*` 家族（GI 规则族，§6.4） | T2 |
| G-12b/c | 泛型指针 `T*`/泛型 stackalloc | UF021/UF040 拒绝 | 域决策（不变；补"零单测"事实） | T3 |
| G-13 | 泛型状态机/闭包/迭代器 | 三 pass 按需泛型 | ← 修正："已系统性承接"→"**机制系统但 M2/M3/S-03/S-08 有约束债 + S-02 闭包不可达**" | T1/T2 |
| G-14 | 泛型推断差异 | 无专项 | 不变；补 41 未映射外部宿主为新观测面 | T2 |
| G-15 | 同擦除签名冲突 | Java 侧 `_erasure_N`/`_Ntp` | 基本已接；← 补：**CS2J5196"deferred to C02 review"无承接者**、RR070 泛型接口 ripple、CS2J5223 开放泛型 with 接收者三形态无 owner（record/member-desugar 泛型域，v1 矩阵未覆盖） | T3 |
| G-16 | **合成层约束债族**（S-03/04/05/08：closure/tuple/local-function/async 丢约束丢 arity） | 无（各 pass 内散落） | **[S]** 统一解析器缺失；错误在中间树爆发 | T1 |
| G-17 | **GD 分析作用域盲区**（S-01：ctor/accessor/operator 体） | generic-deepen | **[S]** Java 侧已支持这些作用域，preprocess 单方面补齐即可 | T2 |
| G-18 | **链鲁棒性**（W-06：Yaml 默认链于 null-coalesce 中止；W-04：linq 引用增强一行之差；单 pass 顺序依赖崩溃；**[v8] W-17：rt10 默认链止于第 4 pass（V-09）、逃生后止于第 8 pass（CS2J0545 守恒门）——两语料三形态实证**） | 无（框架级） | **[D]** 顺序性失败策略：后续 pass 能处理的构造不应阻断链 | T1 |
| G-19 | **诊断码撞号**（W-03：CS2J1203 一码三义 + 12xx/13xx 撞段） | 无 | **[D]** 影响报告/契约一致性测试的可归因性 | T4 |
| G-20 | **ED020 保真面过宽**（枚举深泛型实参可传递致整 enum 保真） | enum-declaration | **[S]** 压低转换率而非错误 | T4 |
| G-21 | **params 集合谱系**（`ReadOnlySpan<char>`/ref struct 元素不支持；`[CollectionBuilder]` 恒 0607） | strip-default C48 | **[L]** 泛型库 spread/params 交叉面 | T3 |
| G-22 | **NRT 运行时元数据保留契约**（`NullableAttribute` flags 被 Yaml 运行时反射） | 无 | **[K]** 键剥离正确（V-05 修法）但成员元数据需保留/重建 | T2 |
| G-23 | **Expression 树域**（Yaml：执行面+元数据面；三 pass 策略分散） | linq-channel 冻结 | **[v1 P2-R2 + K 元数据面]** 域决策 | T3 |
| G-24 | **java-ready 严格性**（W-01/W-02：strict 不挂钩、泛型 0 探针、CM 残留 Warning 放行、必需 pass 可被 `--disable-pass` 绕过） | 框架 | **[K]** java-ready 的语义完备性 | T2 |
| G-25 | **compat 注册表签名漂移**（W-09：ToUtf8 条目拟合 3.x 旧签名，rt10 真签名 3 参 char-span → 零匹配静默空转；无"类型存在但零匹配"漂移告警） | compat-method-replacement | **[v3]** 条目式机制的固有风险：库升级即失效；须配漂移探测 + 条目带语料版本锚 | T2 |
| G-26 | **csproj 门控 Compile/外部共享源输入模型缺口**（W-10：文件级 TFM Condition、`$(CoreLibSharedDir)`/`$(CommonPath)` 外部源、gen/ DefineConstants 与 analyzer 型 ProjectReference——目录 glob 必产出重复成员/缺源树） | 框架（L1） | **[v3]** rt10 语料硬前置：`MsBuildProject` Compile 求值必须先于语义 pass（否则泛型绑定的"树"本身就不是目标 TFM 的树） | T1 |
| G-27 | **rt10 新语法面批量首触**（`field` 关键字含 `ref field` 实参与静态属性形态；ref struct 上主构造；using 别名指向 ValueTuple 形状；公共 `params ReadOnlySpan<>` API；`readonly` 实例成员修饰） | member-desugar/tuple/strip-default 各自路径 | **[v3]** 机制大多已有（FieldKeywordBodyRewriter 含表达式体/K2 params 集合），但组合形态（field×静态单例×ref 实参、主构造×ref struct×Span 字段、别名×tuple 形状）无 fixture；S 层统一解析器与 M0 fixture 补齐 | T2 |
| G-28 | **两入口 profile×disable 语义分裂**（W-11：convert-project 显式 disable 赢 vs preprocess 无条件追加→冲突退出 1） | 框架 | **[v5/K]** 同一 profile 两条 CLI 入口两种语义；"审计必在"与"用户可显式排除"并存使 java-ready 结果依赖入口选择 | T2 |
| G-29 | **源生成器输出未物化**（W-12：额外树只合成 global using；CM 仅 CS2J1204 排除+警告） | 框架（L1） | **[v5/K]** B/C 路差分上 rt10 tests（JsonSerializerContext）与 YamlDotNet.Test（StaticGenerator helper）无法编译——四路差分验收硬前置缺失；反射/源生成路径闭型可能互相误注册 | T1 |
| G-30 | **消费面 guard 无执行时序模型**（W-13：guard 词法相邻≠执行前注册；residual 仅查相邻性） | closed-metadata | **[v5/K]** Deferred/ExpressionTree/Factory/AsyncStream 场景注册可能晚于消费或不在执行路径；rt10 Schema 回调与 Yaml builder 均落 Deferred/Factory 类 | T2 |
| G-31 | **合并 compilation 项目边界丢失**（W-14a：internal 可见性/InternalsVisibleTo/重复类型名/测试生产混编语义失真） | 框架（L1） | **[v5/K]** 解决方案级路径（YamlDotNet.sln/csharp-sdk/json tests）绑定语义不等于真实构建；单项目主路径（rt10 csproj/Yaml 库 csproj）不受影响——列 M3 决策点而非全量前置 | T1/T2 |
| G-32 | **公开签名改写无 ABI 变更清单**（W-14b：GD-MTOK 尾参/GD-STATIC 提升/V-04 stub/EB 桥接/改名均无原符号→生成符号登记） | 框架（报告/审计） | **[v5/K]** java-ready 无 ABI diff 门禁；外部调用面（反射/源码外调用）兼容性不可对账——R10 ApiManifest 补 | T2 |
| G-33 | **渲染期文本重建闭型**（J-01：`JsonDeserializeRewrite` 293 行对 rendered Java 文本前缀匹配+括号配对，按槽位改路由；类型参数 null 退回擦除强转） | 无（Java 渲染后补救） | **[v6]** 预处理吸收：消费面扩 Newtonsoft/通用槽位闭型物化（JE-01，R3 扩）；类型参数站点接 GD token；删除 Java 文本手术 | T1/T2 |
| G-34 | **集合实现擦除一致性 Java 后处理**（J-02：`CompatCollectionConformance` 732 行签名手术+remove_2 改名+补桥，跨工程一致性靠人工推导） | 无（Java 后处理） | **[v6]** JE-02：member-desugar EB 族扩 owner 到"用户集合→compat 接口形状"，C# 侧符号级显式实现+桥 | T1/T2 |
| G-35 | **跨集合形状隐式转换适配网**（J-03：≈1666 行 coercion + v5 后 5 次同类修复） | Java 渲染期逐站点猜测 | **[v6]** JE-03：`collection-shape-normalize` 候选 pass 在实参/返回/赋值槽位插显式适配；coercion 网收缩为透传 | T2 |
| G-36 | **条件位置副作用的控制流线性化**（J-04：while 条件 ++/-- 死循环真实发作 a2d3a6f0；getter pre-statement 泄漏同类） | Java 文本发射层（已出语义 bug） | **[v6]** JE-04：`condition-effect-linearize` 候选 pass，C# 侧规范形（while(true)+break）+ continue/break 等价夹具 | T2 |
| G-37 | **BCL 重载决议/值语义差异逐案 Java 补丁**（J-05：string.Concat 展平/checked IO/Decimal 尾零/bean getter，一案一 commit） | Java 侧特判 + compat | **[v6]** JE-05：高频族条目化进 compat-method-replacement 式注册表（保守，M4 决策），不做通用重载重写 | T2/T4 |
| G-38 | **fail-fast 口径分裂、探针底册漂移**（J-06/W-16：15 统一门 + 54 裸 throw；42 旧口径不可复现） | 框架（L0） | **[v6]** JE-06：裸 throw 分类归一（预处理责任 vs IR 不支持），盲区巡逻按新底册（附录 H）重测 | T3/T4 |
| G-39 | **预处理生成物反向依赖 Java 逐名特判**（J-07：tuple `EqualityComparer<T>.Default.*`→Objects.* 硬编码；生成形状漂移曾破 Java（2aa7aa8f）） | Java 调用渲染特判 | **[v6]** JE-07：生成物改发 Cs2jPrelude 中立 helper，剩余隐含契约登记进 R10 api-manifest，加生成物×特判一致性测试 | T2 |
| G-40 | **预处理结果无版本化能力握手，已有树可绕过 profile**（`Program.cs:239-246`：`--no-preprocess` 遇 java-ready/audit 仅 Warning 并忽略 profile；报告只记录 pass，不记录最终树 capability/inputHash，转换器无法知道哪个兜底可删） | 框架 + 转换入口 | **[v7]** R12：JCS-1 + `preprocess-manifest.json` + JM-ID；java-ready 下缺/旧/假 manifest 硬失败，default 原始树标 unverified | T2 |
| G-41 | **早期消除不等于最终树消除**（52 pass 串行生成新形状，R5 只把晚期重排列为开放决策；最终 normalization-audit 与具体能力声明之间无守恒关系） | 框架/链编排 | **[v7]** R13：最终树语义闭包、Produces→owner 守恒、按能力 finalizer；能力只按最终树授予 | T2 |
| G-42 | **共享语义事实双实现与反向依赖**（`StructValueSemantics/ValueCopyFacts.cs:11` 自述移植 `StructCloneHelper`；`StructValueSemanticsAnalyzer.cs:77` 又直接调用 Transformers 侧 helper） | preprocess ↔ Transformers 双向耦合 | **[v7]** R13/JE-10：事实唯一实现上移 `Core/Analysis`；先消灭重复决策，再删除 Java 拷贝站点判断 | T2/T4 |
| G-43 | **byref/out 条件的求值顺序与 out-var 作用域仍在 Java 发射层修复**（`StatementTransformer.Conditionals.cs:373-433`：短路逐操作数 temp/read-back、悬挂 post-statement、if 前读回；超出 v6 JE-04 的属性 ++/-- 范围） | Java StatementTransformer | **[v7]** JE-08：纳入 EvaluationOrderFacts 与 JE-04 同一切片；C# 侧规范形 + 短路/异常/作用域反例 | T2 |
| G-44 | **调用形状决策集中在巨型 Java 调用枢纽**（`InvocationExpressionTransformer.cs:790-924` 等同时处理生成物 comparer、方法组、隐式 delegate Invoke；另有大量纯 BCL 映射，不能一概前移） | Java Invocation transformer + 既有 delegate/strip-default/extension pass 未形成统一后条件 | **[v7]** JE-09：只前移 C# 调用决议与形状，BCL/compat 映射保留 Java；按 JM-ID 分支退休，不以整文件行数作目标 | T1/T2 |

矩阵结论（v7，在 v2 三条上 v3/v5/v6/v7 各增补一条）：

1. **T2 仍是主威胁**，且 v2 新增了 T1 级别的合成层缺口（G-09/G-13/G-16/G-18）——错误提前到 C# 中间树/链执行阶段爆发。
2. **"已接"需要重新定义**：v1 的"已接"= 机制存在；v2 起要求"机制存在 + 无正确性缺陷 + 残差可对账"（G-02a/G-03/G-09/G-13 四行的修正是本次最重要的认识更新）。
3. json 0 失败的成因是**语料运气**（单字段 holder、无 NRT 键差、约束全引用语义、泛型测试方法未踩 1101 行为面）；YamlDotNet 已实测踩雷（W-06 链中止）。
4. **[v3] 语料真实性是第 0 类缺口**：v2 的运气成分比想象的更大——json 语料本身是 3.x 时代布局（G-25 的签名漂移、V-05 键分裂面 63 vs 452、`new()` 约束 1 vs 18 全部因此被低估）。任何"已接"结论在语料换代（3.x→10）后必须以真源重验，本条同时是附录 C"不为单一语料写死特判"的反向纪律：**也不允许单一旧语料固化机制**（compat 条目按旧签名拟合即此病）。
5. **[v5] 框架层缺口与泛型规则缺口同等威胁 java-ready 语义**：G-28~G-32 表明"profile 成功"当前还依赖 CLI 入口选择（W-11）、树是否包含生成源码（W-12）、消费是否延迟执行（W-13）、项目边界是否被合并抹平（W-14a）、签名改写是否有账可查（W-14b）。kimi 的"profile 名称本身不是完整证明"论断经核证成立——这些缺口不修，泛型规则层的清零结论仍可被框架层的静默放行抵消。
6. **[v6] "0 失败"之后，主战场是 Java 转换器内的 C# 语义模拟器**（G-33~G-39/J 层）：v5 后 14 个提交证明该模拟器仍以每周数个补丁的速度增长，其中最脆弱的部分（渲染后文本手术、文本层控制流线性化）恰恰是预处理侧有符号、可编译验证、可差分却缺位的形状。能力层立项标准由此增加一条反向证据（I10）：**新规则必须同时给出"消除哪个 Java 机制"的指认与负 diff 验收**——否则扩能力只增加中间树复杂度，不兑现"简化转译"的总目标。
7. **[v7] 没有能力握手与退休协议，负 diff 仍不可执行**（G-40~G-44）：`--no-preprocess` 可绕过 profile，早期 pass 无法证明最终树，值拷贝事实已出现双实现，Conditionals/Invocation 仍在 Java 侧做 C# 语义决策。v7 因此把“已接”再升级为：**最终树 guaranteed + 转换器校验握手 + 指认 JM 分支 retire + 总复杂度无同构搬迁**。行数只作辅助，不再单独代表简化。

---

## 6. 升级设计

### 6.1 目标不变量

v1 首版 I1-I6 全部继承（I1 每中间树可编译 / I2 可测试 / I3 无未登记泛型构造 / I4 确定性幂等逐字节 / I5 泛型语义等价可审计 / I6 推断锚点化），v2 增补：

- **I7 补偿正确性**：GD-FIELD/CM 等既有补偿机制的输出必须满足规范形（per-closed-type 恰一份、readonly 至多初始化一次、声明类型 cctor 先于首读、异常按 CLR 模型缓存重抛、生成成员不重复）——通过 §6.4-R0 的 `StaticGenericStatePlan` 与行为反例测试锁定。
- **I8 合成保真**：任何提升/合成族 pass 产出的 C# 必须携带源约束与 arity（统一解析器出口校验），CS0246/CS0308/CS0453 类中间树错误归零。
- **I9 框架一致性 [v5]**：同名 profile/开关在所有 CLI 入口语义一致（W-11）；预处理输入树包含 csproj 求值所得的全部编译输入（含源生成输出，W-12/R9）；对公开签名的任何改写都在 `api-manifest.json` 中可对账（W-14b/R10）——java-ready 的成立不依赖入口选择、树组织方式或"无账可查的签名改写"。
- **I10 Java 侧净简化导向 [v6，v7 修订]**：**JE/JCS 减负规则**在立项时必须指认稳定 JM-ID、所需 capability 与退休验收；落地时完成对应 Java 分支删除/收缩，或登记有到期波次的覆盖冻结。M0 正确性修复、通用 C# 语法覆盖、框架/审计/性能工作**不强制寻找可删 Java 代码**，但不得新增下游语义补偿；不得仅因“没有行数可删”否决必要正确性工作。每波次同时报告 Java、preprocess、compat、共享 Analysis 的复杂度变化，防止把同一决策原样搬家。
- **I11 JCS 能力可信 [v7]**：能力只由最终 C# 树后条件授予，不由“某 pass 执行过”推断；manifest 的 subsetVersion、capability、inputHash、contractHash 必须与实际输入一致。java-ready 的所有入口（含 `--no-preprocess`）都执行同一校验，无法校验即失败。
- **I12 单一语义 owner [v7]**：每类 C# 语义决策只有一个实现和一个结果 owner。preprocess 不得引用 `Transformers` 获取事实，Java 不得对已 guaranteed 构造重新推断；共享事实只放 `Core/Analysis`。BCL→compat 映射、Java 命名与发射始终归 Java，不为追求删行数而倒置职责。
- **I13 最终树语义闭包 [v7]**：`Consumes/Produces/MayLeave/Owners` 必须形成守恒；任何晚期生成构造要么被后续 finalizer 接管，要么成为残差。能力审计在最后命名与所有结构生成之后运行，禁止用早期 0 残差掩盖晚期再生。
- **I14 可逆退休 [v7]**：每个 Java 补偿分支按 inventory/shadow/enforce/retire/ratchet 迁移；enforce 前保留可回滚路径，enforce 后命中旧分支即失败，retire 后以测试禁止同义实现回流。冻结只覆盖明确残差，不得作为默认终态。
- **I15 总复杂度与行为双重不回归 [v7]**：同时度量目标 Java 分支数/命中数、preprocess 与 compat 增量、共享事实重复数、残差率、构建/测试行为。Java 行数下降但决策表复制到 preprocess、compat 变厚或测试数下降，均不算完成。

### 6.2 目标层 + 五层执行（v7 重排）

```text
目标层 JCS-1：§0.1.1 八族最终树后条件 + §0.1.2 职责分界
            输出 preprocessed C# + preprocess-manifest + api-manifest + residual/complexity ledger
            只有 guaranteed capability 能授权 Java JM 分支退休

M0 止血（先于扩能力）：V-01..V-08 + S-03/04/05 + W-01/W-04 + W-03 撞号迁移
                          + W-09/W-10/rt10 基线 + W-11 两入口统一 + R0 正确性项
M1 契约与审计：L0 探针族（26 码）+ PassContract 泛型族挂载 + 报告 v2（互链/统计/引用画像）
                          + JE-06 fail-fast 底册 + [v7] R12 manifest/JM 底账 + R14 退休协议
M2 编译与共享事实：L1 真实引用 spine + degraded 显式化 + R9 源生成物化
                          + [v7] R13 最终树语义闭包 + Core/Analysis 单一事实实现
M3 垂直切片能力层：[v7] VS-1 求值顺序（JE-04/08）→ VS-2 struct 值拷贝（JE-10）
                          → VS-3 调用形状（JE-09，JE-01 按依赖并行）→ VS-4 集合分族（JE-02/03）
                          每片独立 shadow/enforce/retire，不等待整个泛型 backlog
          泛型纵深轨道：R1~R10 + JE-05/06/07 继续按依赖交付，作为 VS-5；只阻塞真实依赖切片
M4 域决策：static abstract / 运行时合成 / Expression / 型变域（DeclaredUnsupported 或立项）
                          + JE-05 BCL 条目化（Q14）+ 剩余形状族与晚期顺序裁决（Q15）
M5 性能：统一树默认 + 增量 + 选择性 verify + 文档自动生成（descriptor+contract → preprocessor-matrix.md）
```

**v7 排程原则**：M0/M1/M2 只交付对应切片的必要前置，不要求“所有泛型工作完成”才启动 VS-1；M3 的退场单位是 VS 而非整个规则包。某个形状族被卡住时保持 `partial` 并冻结相应 JM 分支，其他已 guaranteed 形状仍可退休。

### 6.3 L0 契约层：探针族、诊断段、撞号迁移

**探针族扩容**（合并 v1 首版 9 码与 seed 方案 12 码，两套几乎互补；统一命名 `NORM-GENERIC-*`，非泛型 JCS 构造使用 `NORM-*`；v5 增至 21、v6 增至 23，**v7 再增 3 码至 26**：CALL-SHAPE/VALUE-COPY/SEMANTIC-CLOSURE；CONDITION-EFFECT 同步扩到 JE-08 的 byref/out 条件序列）：

| Code | 扫描形状 | Owner | 初期 Policy | 来源 |
|------|---------|-------|------------|------|
| NORM-GENERIC-TOKEN-SITE | **可执行作用域**（方法/ctor/accessor/operator/lambda/局部函数）内 `typeof/is/as` 引用可见类型参数但作用域内无 `__cs2j_token_*` 锚点 | generic-deepen | Fail | S（作用域式检测，比特征名式更接近擦除本质） |
| NORM-GENERIC-CONSTRAINT | `struct/new()/unmanaged/notnull/T:U` 约束子句 + 依赖位点 | 新 pass generic-constraint（§6.4-R2） | PreserveAndWarn | v1 |
| NORM-GENERIC-STATIC-ABSTRACT | 接口 static abstract/virtual 成员 | expression-desugar（守门转登记） | Fail | v1 |
| NORM-GENERIC-ATTRIBUTE | 泛型 attribute 声明/应用 | expression-desugar 0587 | Fail（声明） | v1 |
| NORM-GENERIC-VARIANCE-DECL | `out/in` 声明（复用 VA001） | variance-audit | PreserveAndWarn | v1 |
| NORM-GENERIC-VARIANCE-USE | 型变使用点未分类/未改写/未登记（VR pass 残差；v4 自 VARIANCE-DECL 拆出） | variance-use-site-normalize | Fail | D |
| NORM-GENERIC-REFLECT | `MakeGeneric*/GetGeneric*/GetGenericTypeDefinition` 调用点（含 `typeof(T)`/`typeof(X<>)` 接收者链）；**[v5 扩域]** 类型关系查询（IsAssignableFrom/BaseType/接口遍历/接口映射）与成员发现访问面（BindingFlags/DeclaredOnly/DeclaringType/ReflectedType/GetValue/SetValue/Invoke）未证明等价处（kimi REFLECTION-QUERY/MEMBER-METADATA 并入本码，owner 同为 generic-reflection-audit） | closed-metadata → generic-reflection-audit | PreserveAndWarn | v1+D+K |
| NORM-GENERIC-COMPARATOR | `EqualityComparer<T>.Default`/`Comparer<T>.Default` | equality-concat R4 | PreserveAndWarn→Fail | v1 |
| NORM-GENERIC-UNSAFE-INTRINSIC | `Unsafe.As<,>/BitCast<,>/Cast<,>/WriteUnaligned<>/SizeOf<>()` 实参含类型参数 | unsafe-ref-lowering | PreserveAndWarn | v1+D |
| NORM-GENERIC-NEW-ARGS | `new T(args)`（args>0） | generic | PreserveAndWarn | v1 |
| NORM-GENERIC-ARRAY | `new T[...]` 多维/jagged/带初始化器残留 | generic | Warn→Fail | S |
| NORM-GENERIC-ENUM-OP | 类型参数操作数的枚举运算/HasFlag | enum | Warn | S |
| NORM-GENERIC-DECONSTRUCT | 解构组件含类型参数未被接管 | tuple | Warn→Fail | S |
| NORM-GENERIC-CATCH | `catch(T)` 残留 | generic-deepen | Fail | S |
| NORM-GENERIC-STATIC-FIELD | 静态字段类型引用包容类型形参（GD 门外复检） | generic-deepen | Warn→Fail | S |
| NORM-GENERIC-THREAD-STATIC | 泛型容器 `[ThreadStatic]` | thread-static | Domain（DeclaredUnsupported 首批使用方） | S |
| NORM-ERASURE-COVARIANCE | CS2J5195/5196/5223/RR070 形状 | member-desugar | Domain | S |
| NORM-EXPRESSION-TREE | `Expression<...>` 位点与 `Compile()` | linq-channel | PreserveAndWarn | v1 |
| NORM-PROJECT-DEGRADED | 工作区降级（缺引用/错 TFM/baseline error/缺生成源码） | 框架 | Fail（java-ready 下） | K |
| NORM-SOURCE-GENERATOR | 生成源码缺失/重复/过期/归属不明（generator 未执行、inputHash 不一致、生成器本体混入分析树） | 框架（L1/R9） | Fail | K |
| NORM-PUBLIC-ABI | manifest 外的公开签名变化（未登记的 tail-param/promote/stub/bridge/rename）；manifest 内 `pending` 未批准条目 | 框架（R10） | Fail（java-ready 下） | K |
| NORM-COLLECTION-SHAPE [v6] | 跨形状隐式转换站点（数组/IList/IEnumerable/Span/IGrouping 互转，实参/返回/赋值槽位）未被 `collection-shape-normalize` 显式化（JE-03 残留；与 IListCoercion/IterableCoercion 分支一一对照建底册） | collection-shape-normalize（M3 候选） | PreserveAndWarn→Fail | J-03 |
| NORM-CONDITION-EFFECT [v6/v7] | 条件位置（while/for/do/if/`?:`/短路链）含副作用表达式、ref/out 实参或悬挂读回未线性化；含 out-var 跨 if 作用域（JE-04/JE-08 残留） | condition-effect-linearize + ref-out-in（共享 EvaluationOrderFacts） | PreserveAndWarn→Fail | J-04/G-43 |
| NORM-CALL-SHAPE [v7] | 已选 JCS.CALL 形状仍依赖 Java 猜 callee/调用形状：隐式 delegate Invoke、未定型方法组、命名/可选/params 实参、需显式化的泛型实参；纯 BCL 映射不计残差 | delegate/strip-default/extension-methods + 最终 call-shape 审计 | PreserveAndWarn→Fail | G-44 |
| NORM-VALUE-COPY [v7] | 用户 struct 值拷贝槽位无 copy、无免拷证明且未 DeclaredUnsupported；copy shim 与调用点计划不一致；preprocess 仍调用 Transformers 侧事实即契约测试失败 | struct-value-semantics + Core/Analysis ValueCopyFacts | PreserveAndWarn→Fail | G-42 |
| NORM-SEMANTIC-CLOSURE [v7] | 最终树出现由晚期 pass 再生成、但无后续 owner/finalizer 的已登记构造；Produces 与最终残差不守恒 | 框架（最终语义闭包） | Fail（对应 capability 请求 guaranteed 时） | G-41 |

配套纪律（吸收四方案，v7 增补能力授予）：

1. **每码正/负 fixture 成对**（触发/不触发）；`PassContractConsistencyTests` 的计数断言改为**对常量集的派生断言**（每加码不改数）。
2. **degraded 探针口径**：纯语法可判的探针不依赖绑定；需符号的探针在绑定失败时标 `degraded`、**不得计为 0 残差**（防 L1 未覆盖时假阴性）。
3. **CS2J14xx 新诊断段**（1401 token/1402 static/1403 值语义/1404 型变/1405 闭型/1406 反射/1407 内在函数/1408 桥接/1409 不支持域；**[v7]** 1410 manifest/入口握手失败、1411 call-shape、1412 value-copy、1413 semantic-closure）；**撞号迁移**：CS2J1203 保留给 closed-metadata，extension-methods 迁 19xx、thread-static 迁 16xx，报告留 `legacyCode` 一个版本；12xx/13xx 撞段一并消歧。
4. **`SYNTH-*` 内部构造命名空间**（SYNTH-token-anchor/SYNTH-generic-holder/SYNTH-ctor-class-token）：生成物只进 Produces/跨 pass 校验，不进 NORM 审计。
5. PassContract 三步推广：M1 挂泛型族四 pass + 残差相关的 local-function-lift/ref-out-in/foreach-deconstruction/linq-channel；M2 补产物类 `Produces`（跨 pass 顺序依赖可机检，如 tuple→foreach-deconstruction 的 5420 关系变 Precondition）；M3 全量 + 注册期四校验（MayLeave owner 在链中且在其后、每 NORM 码恰一 owner、Postconditions 构造 ID 存在、Idempotent=true 必有幂等测试）+ `VerificationProfile` 字段补齐。**[v7]** 增加 `CapabilityIds` 与 `Phase/FinalizerFor` 元数据（可独立小步扩展，不另建平行 contract）；`PassCatalog`/`PassStubCatalog` 先统一 ID 事实源，contractHash 由规范化后的 contract 计算。
6. **测试工程双 policy 显式化**（[v4] 吸收 deepseek §6.5）：泛型探针在测试工程（`ProjectKind.Test`）默认 `PreserveAndWarn`、`--strict-tests` 时与生产一致（生产恒 Fail）——json 37×1101 集中在测试工程、Yaml 行为预言机依赖测试工程可跑，双 policy 是"行为预言机"与"Java 产物门禁"两类目标的显式分离（风险 13 的设计化承接，沿用 unsafe-ffm UF050 测试工程降级先例）。
7. **报告 schema v2 定稿 [v7 升版]**（附录 F）：保留 per-pass constructs 与六字段 residual，新增 JCS capabilities、manifest 引用、JM 退休状态、fallback hits、preprocess/compat/Analysis 复杂度增量；`preprocess-manifest.json` 独立承载握手。所有报告无时间戳、字段排序确定；`guaranteed` 必须由最终树 residual=0、degraded=0、必需 finalizer 已执行三项派生，禁止手写。

### 6.4 L3 能力层（规则族）

**R0 既有机制缺陷修复（M0，先于扩能力）**

- **GD-FIELD 规范形 `StaticGenericStatePlan`**（吸收 kimi）：静态字段一等计划对象——每闭型恰一份、readonly 至多初始化一次、首次读写前完成声明类型初始化（`RuntimeHelpers.RunClassConstructor` 语义或等价显式触发）、多字段声明顺序、异常按 CLR 模型缓存重抛、`__cs2j_gate/__cs2j_store` **holder 级单份**（修 V-02）、singleton 形态改缓存式 getter（修 V-01，同步修正 fixture 把缺陷固化为预期的注释）、`ParamRef` 比较器统一（修 V-03）。
- **CM 键修复**：`ClosedTypeKey` 剥离 `NullableAnnotation`（修 V-05；与 G-22 的"成员元数据保留 NRT flags"并行不悖——键不要、元数据要）；collector/linker 统一递归判定（修 V-06）。
- **GD-NEST-2**（修 S-02/37×1101）：表达式体 lambda 先包块体（`x => x+1` → `x => { return x+1; }`，语义等价不改委托形状）再走块体锚点路径；ConvertedType 不可解析时经 L1 引用集重试，仍败则 CS2J1401 登记。
- **GD 扩作用域**（修 S-01）：`WalkTree` 遍历全部可执行成员（ctor/accessor/operator）——Java 侧已支持，preprocess 单方面补齐。
- **GD-STATIC-3**（修 CS2J1106/3 处）：调用点在同声明类内部时允许类型参数作显式类型实参渲染 `GetElementConverter<TElement>(...)`（C#/Java 均合法）；跨实例给提升方法加 token 尾参从 `this.__cs2j_class_T` 读。
- **GD stub ABI 修复**（修 V-04）：abstract/接口成员的锚点物化不再改 `virtual`/塞抛错体——改为伴生 `protected static` 锚点方法族或 `DeclaredUnsupported` 登记。
- **W-04/W-01 接线**：打开 `EnsureEnhancedReferences` 注释（先在 json/Yaml 双语料 on/off diff 验证）；`PassChainFactory` 把 `config.Strict`/profile 传入 `GenericDeepenOptions/ClosedMetadataOptions`；VA020-027 接入 java-ready 门禁。
- **compat 注册表漂移修复**（修 W-09/G-25）：`ToUtf8` 条目键/参数表按 Runtime 10 真签名重写（`ToUtf8(ReadOnlySpan<char>, Span<byte>, out int written)` 3 参，BodyTemplate 同步改名 `source/destination/written` 并核对 surrogate 语义与 `#if NET` 分支的 `Utf8.FromUtf16` 等价）；保留 3.x 形态条目作 `legacyKey` 一个版本；新增**漂移探测**——`MatchedEntryKeys` 之外，凡条目声明类型在语料中存在（符号级）但零签名匹配 → Error 级诊断（CMR0xx 新码），防"库升级条目静默失效"。
- **rt10 真源基线重跑**（W-10/G-26 的入口）：对 `D:\runtime-10.0.12\src\libraries\System.Text.Json`（`System.Text.Json.slnx` 或库 csproj，`.slnx` 解析已支持）执行与 `20261104-json.log` 同参数全链，逐项对照 §3.2.3 的"rt10 重跑关注点"列；记录 C06 域在 net10 符号集下的实际残差（`#if !NET` 剥离后 unsafe 面是否收敛）。
- **rt10 组合形态 fixture**（修 G-27）：①`get => field ?? GetOrCreateSingleton(ref field, …)` 静态单例属性（member-desugar FieldKeywordBodyRewriter × ref 实参）；②ref struct 主构造 + Span 字段（`PropertyHierarchyResolutionState` 形）；③`using FoundProperties = System.ValueTuple<…?>` 别名与 tuple 形状解析；④`JsonTypeInfoResolver.Combine(params ReadOnlySpan<IJsonTypeInfoResolver?>)` 公共 params-span API；⑤Utf8JsonReader 去 ref 判据（含 3 个内部 ref struct helper 不阻断）。
- **fixed-span-to-array 边界加固**（kimi P0-9）：`IsWritableBodySafe` 的 symbol-null 放行改保守拒绝；ReadOnlySpan 指针写/别名观察补差分测试；**[v5 细目补全]** 差分覆盖四形态——块内经原数组/另一 span/另一指针别名观察立即写入、`ReadOnlySpan` 视图下的 unsafe 指针写、span 来自 `AsSpan()`/字段返回/方法返回的底层别名、异常/重入/跨线程/finally 前观察；不可证即保留并令 readiness 失败，不得只验证方法返回后的最终数组内容。
- **两入口 profile×disable 语义统一**（修 W-11/G-28，[v5] kimi §2.3 论断核证）：preprocess 分支对齐 convert-project 的"显式 disable 赢"或（推荐，与 G-24 严格化一致）两入口统一为——java-ready 下 disable 必需 pass（normalization-audit 及 §6.6 L2 清单）一律报配置错误；default profile 下显式 disable 放行但报告记 `profileDegraded: true`。附带修正：`PassPlanResolver` 的 enable/disable 冲突当前复用 `UnknownPass` 码（`PassPlanResolver.cs:60-65`），迁移 §6.3-3 的 CS2J14xx 段时一并给冲突独立码。
- **GD-MTOK 可见性分级**（修 kimi GD-06 缺口，[v5]）：尾参 token 注入的触发条件在现有"全部调用点显式实参、非虚/非族成员"之上**加可见性与调用面维度**——仅 `private` 且非虚、无方法组转换、无反射可能（全树无 `nameof`/`typeof(...)` 指向/`MethodInfo` 获取链）的成员允许签名级改写；`public/protected/interface/override/extern` 成员保留原签名，token 需求走 companion invoker/metadata registry（GD-CTORARR 已有的 companion 模式推广到方法）。
- **TypeParameterId 声明 owner 身份**（强化 V-03 修复，[v5] kimi GD-05）：参数身份从 (kind, ordinal) 升格为 `(containingSymbol 完整嵌套链, parameterKind, ordinal)`，其 `IEqualityComparer` 与 `IComparable`（及 record 默认 equality）**必须同一身份语义**——修 SortedSet/List 去重分裂的根因而非只统一比较器；override/interface family 的槽位映射另用 `FamilySlot` 键，禁止与参数身份混用。
- **GD-FIELD holder 并发与初始化协议证明**（补 kimi GD-03 未竟项，[v5]）：`StaticGenericStatePlan` 增两项硬要求——①holder 内 `Dictionary` 的"先无锁读、再加锁写"双检需**独立线程安全证明**，不能照搬双重检查锁模式（不可证即换 `Lazy<T>`/锁协议并加并发压测：多线程读写/初始化同一闭型仅一次初始化、无损坏/丢失/重复发布）；②初始化异常按 CLR 静态构造模型缓存并重抛（含递归进入策略），先写后读场景保证声明类型 cctor 与字段初始化器先于首次写执行。
- **GD 计划原子提交强化**（补 kimi GD-07，[v5]）：CS2J1190（plan 非自包含）从"事后诊断"升级为**事前协议**——先生成并验证完整 artifact plan（holder/调用点/项目归属/名称解析全部就绪）再统一提交变更；任一要素缺失即整计划失败回滚，源树保持不变（杜绝"字段声明已删除但 holder 未生成"的中间态落地）。
- **`generic` 计数器口径拆分**（修 §4.3 v5 事实）：`DefaultExpressionsRewritten` 拆为 `Encountered`/`Rewritten` 双计数，报告 v2 同步（M0 顺手，报告消费者零破坏——旧字段保留一个版本并标注 deprecated）。
- **[v8] V-09 修复（local-function-lift 半改写，M0 最先做——它是 rt10 基线的阻断项）**：①rewriter 移除逻辑从"仅 BlockSyntax 直接语句列表"扩到 **switch section 语句列表**（`SwitchSectionSyntax.Statements` 同款索引移除，`LocalFunctionLiftRewriter.cs:146-153` 的移除通道参数化，`:861-864` 的保守返回仅保留给匿名真正不可移除容器）；②**改写原子性**：任一 lift 计划的"生成类级方法/改写调用点/删除原声明"三步任一失败 → 该文件整回滚（杜绝半改写产物落地，对齐 kimi GD-07 的计划原子提交协议——该协议在 v5 已为 GD 规定，本项证明它对**所有**提升/合成族 pass 都必要）；③residual 聚合诊断携带文件/行清单（`LocalFunctionLiftPassAdapter.cs:87-99`，`CountResidualLocalFunctions` 已逐文件枚举，顺手把位置带上——`[Error] :0: 2 residual` 这类无定位聚合诊断禁止再新增）；④红测原型＝rt10 `JsonElement.DeepEquals` 双局部函数（正：switch-default 内 lift 后树无残留声明且 `dotnet build` 通过；负：任一容器形态半改写即测试失败）；fixture 入 `SampleLocalFunctionLiftSolution`。**[v8.1] 本项已实施并验证**（`LocalFunctionLiftRewriter` 增 `VisitSwitchSection`+`UnremovablePlans` 原子性防护、`LocalFunctionLiftPreprocessor` 文件级回滚、adapter 诊断带位置、4 个新 xUnit 用例 4/4 绿 + 全量 4552 测试 0 失败 + 探针 3 复跑链越过第 4 pass，见 §3.2.4）。
- **[v8] CS2J0545 归因与记账口径统一（W-17②，M0 与 V-09 同批）**：对 rt10 unpaired 文件逐文件归因——是 C24 记账未覆盖的新形状（如 `with` 表达式初始化器/嵌套 target-new/Schema 导出的复杂元素形态）则补记账；是 per-file accounting 漏报（跨文件/跨 pass 生成节点被二次计数或漏计）则修口径。**[v8.1] V-09 修复后归因范围已收窄**：探针 3（§3.2.4）显示 `JsonPrimitiveConverter.cs` 的 1 个 unpaired 随半改写消除，剩余 unpaired = `JsonSchemaExporter.cs` 28/22 + `PropertyRefCacheBuilder.cs` 1/0（总 75/68）；且证明 per-file 记账面受上游 pass 改写形态影响。**修复方向与 R13 同构**：per-file 记账与整树扫描不得是两套实现——把"整树扫描"作唯一事实源，per-file accounting 改为扫描结果的分组视图（G-41 的 M0 最小兑现，先在 C24 域落地，R13 再推广全链）。

**R1 合成层统一解析器 `SyntheticTypeParameterResolver`（修 G-16/S 层，seed 方案核心设计）**

新增 `Core/Analysis/SyntheticTypeParameterResolver.cs`，输入被提升成员的符号/宿主/体内+签名+约束子句节点集，输出五件套：①应携类型参数有序集（语义归属 + **约束传递闭包**，`where T : IFoo<U>` 必携 U——修 S-08）；②宿主作用域内形参不重声明；③每形参"语法+约束"或"无法安全表达"的**显式三态**（禁止默默丢弃——修 S-03）；④调用点一律符号渲染 + 默认显式实参（消灭 CS0411）；⑤同名遮蔽按 ordinal 消歧 + 稳定重命名（修 S-05）。消费方迁移顺序：closure-lift M2/M3 → async → local-function-lift → tuple（S-04 随带修：tuple 不可表达约束时保留原生 `ValueTuple` 而非产出不可编译代码）→ anonymous-type-lift。**止血序**：解析器落地前，先把算不清的站点改保守 skip + 可对账诊断。

**R2 v1 首版 P1-R1~R8 全部保留**（修订三处）：

- P1-R1 `generic-constraint`（新 pass，插 `generic` 之后 `thread-static` 之前，§6.5）——**CN 规则全表 [v4 自 deepseek CN-01~12 吸收 + v1 附注合并]**：

| 规则 | 输入 | 输出 | 诊断 |
|------|------|------|------|
| CN-01 | `default(T)` 且 T 有 `struct/unmanaged/Enum` 约束 | 零值语义表达式（**不得经 Activator**，附注 1） | CS2J1403 |
| CN-02 | `default(T)` 且 T 仅引用约束/无约束 | `default` 字面量保留；`==/!=` 或推断实参上下文保留原形 | — |
| CN-03 | `new T()` 且 T 有 `new()` 约束 | `Activator.CreateInstance<T>(typeof(T))`（token 由 GD-MTOK-2 注入） | CS2J1403 |
| CN-04 | `T?` 且 T 有 `struct` 约束 | `Nullable<T>` 显式化 + **连带 `.Value`/构造/`null` 比较改写**（附注 2） | CS2J1403 |
| CN-05 | `T?` 引用/无约束 | 保留（Java null 语义承接） | — |
| CN-06 | 值语义 T 装箱（`(object)t`/`IComparable` 调用） | 显式 `(object)t` 保留；需包装类型时 `Cs2jGenerics.box<T>`（不新增库则 `(object)`） | CS2J1403 |
| CN-07 | `new T[n]`（一维，type param 元素） | `(T[])Array.CreateInstance(typeof(T), n)` + token | CS2J1403 |
| CN-08 | `new T[,]` 多维/jagged | 暂不支持 → CS2J1409 / `DeclaredUnsupported` | CS2J1409 |
| CN-09 | `Enum.Parse<T>/GetValues<T>/GetNames<T>/TryParse<T>` | `Enum.Parse(typeof(T), …)` 等非泛型重载 + token | CS2J1403 |
| CN-10 | `Delegate.Combine<T>/Remove<T>` | `CsPrelude.Combine/Remove`（非泛型） | — |
| CN-11 | `where T : Enum` + enum-like class 使用点 | 交 `enum-declaration` Infeasible 保留真 enum | CS2J1409 |
| CN-12 | 无依赖站点的不可表达约束 | 约束文本保留（Java 侧 CS2J1002 告警），G-06/G-18 PreserveAndWarn | CS2J1403 |

  附注 1（CN-01，v2 已核证）：`Activator.CreateInstance<T>()` 对带自定义无参构造器的 struct 会调构造器，与 `default(T)` 零值**不等价**——零值语义改用受控 helper（`Cs2jGenericDefaults.DefaultOf<T>(typeof(T))`，需单独 Java 映射验收）或 `(T)Array.CreateInstance(typeof(T), 1).GetValue(0)`；普通 struct 可经 Activator。附注 2（CN-04，v2 已核证）：TypeMappings 把 `Nullable<T>` 映射 `java.util.Optional` 而 Java 侧 `GetDefaultValueForType` 对 Nullable 返回 `null`——只写 `Nullable<T>` 不改 `.Value`/构造会产出 Optional 与 null 混用代码（V-08 关联），改不动的站点登记 `DeclaredUnsupported`。幂等：CN 产物不再匹配原形状（`Activator.CreateInstance<T>(typeof(T))` 非 `new T()`、`Nullable<T>` 非 `T?`）；单文件任一 CS2J1403 Error → 整文件回退（strict 文件级回退沿用）。
- P1-R1b **GD-MTOK-2**（generic-deepen 扩展，[v4] 吸收）：GD-MTOK 尾参 token 注入的触发集从 `typeof(T)` 站点扩展到 CN 产物值语义站点——`Activator.CreateInstance<T>(typeof(T))`（CN-01/03）、`Nullable<T>` 构造/`HasValue`/`Value`（CN-04）、`Enum.Parse(typeof(T), …)`（CN-09）、`sizeof(T)`/`Marshal.SizeOf<T>()`/`Unsafe.SizeOf<T>()`（G-12a/GI 域）。算法复用 `MethodTokenPlan` 工作表：方法自有 type param → 追加尾参 `Type __cs2j_token_<T>` + 全调用点补 `typeof(具体实参)`；类级 → 复用 GD-CTORARR 构造器 token 字段（无构造函数/静态上下文 → CS2J1401）；闭包 → 先 GD-NEST-2；调用点不可见（外部程序集/方法组/反射）→ CS2J1406 或 `DeclaredUnsupported`。
- P1-R5 `make-generic-static`：升级为 **RG-0~4 反射分级**框架的 RG-0 层（吸收 kimi L4）：RG-0 静态闭合→闭型工厂（P1-R5 原案）；RG-1 token 可传播→tokenized invoker；RG-2 运行时开放→统一注册工厂（CM-DYN 收口）；RG-3 不可判定→readiness 失败或人工 provider；RG-4 表达式/延迟→移到实际执行边界。配套 `RuntimeTypeQueryPlan`（IsAssignableFrom/接口映射/查询顺序）与 `MemberMetadataPlan`（BindingFlags/发现顺序/Invoke 差分）。
- P1-R6 pattern-desugar：补 C37 组件注册与 list pattern reducer（v1 已有）；relational-CompareTo 物化规则不变。

**R3 消费面配置化（修 W-05/G-04d，吸收 seed/deepseek）**

`config/ClosedTypeConsumerSurfaces.json`：条目 `{assembly, type, methods[], genericArgPolicy, requiresGuard}`；Core 只实现"按符号匹配配置条目"，**库名只出现在 JSON**（M3 验收含 grep 守门测试：`Core/` 中不得出现 `YamlDotNet`/`JsonSerializer` 字样——后者从 `ClosedMetadataCollector.cs:42` 的硬编码常量迁出）。内置两组描述（STJ/YamlDotNet）+ `GenericReflectionProfile`（MakeGeneric 实参枚举，CM-ENUM-5；GetGenericArguments 只登记读取面不注册防无界膨胀，CM-ENUM-6）。

[v4 细则吸收] 消费面匹配按 deepseek `IClosedTypeConsumerProfile` 三件套落地（Core 内接口 + JSON 供数据）：①`StjProfile`——现 5 入口迁出为首批条目；②`YamlDotNetProfile`——入口取**实测存在**的反射消费面（deepseek 原稿的 `GenericCollectionTypeResolver` 文件不存在，按 §3.3 核证替换）：`IYamlTypeConverter.Accepts(Type)` 实现点、`TypeConverterCache` 的 `MakeGenericType/GetGenericArguments`、`DefaultObjectFactory/ObjectFactoryBase` 的 `Activator.CreateInstance(Type)`、`EnumerableNodeDeserializer/DictionaryNodeDeserializer/CollectionNodeDeserializer` 的 `MakeGeneric*` 调用点、静态 builder 路径（`StaticSerializerBuilder/StaticDeserializerBuilder`，第二消费者，§3.3 链 2）；③`GenericReflectionProfile`——`System.Type` 反射 API 保守白名单。配套 CM 规则：**CM-ENUM-5**（`MakeGenericType` receiver+实参枚举闭型，不可证 → CM-DYN）、**CM-ENUM-6**（`GetGenericArguments` 只登记读取面不注册，防无界膨胀）、**CM-LINK-2**（Yaml 入口插 bootstrap guard，形态同 `__cs2j_ensure_closed_types`）、**CM-STRICT**（java-ready 下 `CS2J1220` 转 Error、`CS2J1210` 动态闭型 → `DeclaredUnsupported` 带文件/行/原因）。**单 pass 修复**（W-06 关联的顺序依赖崩溃，Yaml 20 次 `_compilation` null）：`CompilationReadyAsync` 先 `BindCompilation` 再检查链前置（缺 generic-deepen → 干净报 CS2J1205 而非空引用崩溃；`RewriteFile` 对未 bind 树返回 null 不改写）。

**[v5] execution kind 执行时序模型**（修 W-13/G-30，吸收 kimi CM-03 + ConsumerDescriptor 设计）：消费面条目增 `executionKind` 字段——`Immediate | Deferred | ExpressionTree | Factory | AsyncStream` 五类。语义：**Immediate** 才允许现行的"消费语句词法前方插 guard"；**Deferred/AsyncStream**（延迟枚举、返回 delegate 稍后调用、async iterator 真正枚举时执行）把注册移动到**实际执行入口**（枚举起点/委托调用点/状态机 `MoveNextAsync` 首入口）；**ExpressionTree** 保持表达式节点不动，注册移到树的实际 `Compile()`/解释执行边界（与 CS2J1110 禁区协同）；**Factory**（工厂方法内部稍后创建对象）在工厂产物首次使用点或工厂入口注册。配套 **CM-LINK-3** 规则：residual 校验从"guard 词法相邻"升级为"注册与消费处于同一执行路径"（linker 按 executionKind 选择插入位；无法判定时序的消费面 → CS2J1210 登记 `DeclaredUnsupported`，java-ready 下 CM-STRICT 转 Error）。rt10 的 Schema 导出回调（`TransformSchemaNode` 委托属性）与 Yaml 双 builder 均为首批 Deferred/Factory 判定样例。

**R4 v1 首版 P2-R1~R3 保留并 pass 化 [v4]**：variance use-site 从 opt-in 锚点升级为链末 pass `variance-use-site-normalize`（Expression，`member-desugar/linq-channel/equality-concat` 之后、`java-keywords` 之前，§6.5）——直接复用 `VarianceAuditAnalysis.UseSites` + `UseSiteSafetyClassifier` 分类结果（**禁止两套分析漂移**；163/66/475 为旧语料类型引用站位分类，非全部需改写；实际改写集 = VA010/011/015 转换 + 221 数组 + 53 unsafe）。**VR 规则全表**：

| 规则 | 输入 | 输出 |
|------|------|------|
| VR-01 | 隐式 `IEnumerable<Derived>` → `IEnumerable<Base>`（producer） | `(IEnumerable<Base>)(object)expr` |
| VR-02 | 隐式 `Func<Base,R>` → `Func<Derived,R>`（consumer） | `(Func<Derived,R>)(object)expr` |
| VR-03 | 嵌套 `IEnumerable<List<Derived>>` → `IEnumerable<List<Base>>` | 递归 cast；中间层 invariant → `DeclaredUnsupported` |
| VR-04 | 数组 `Derived[]` → `Base[]` | 保留原样（Java 数组协变）；逐站点验证元素映射无装箱差异 |
| VR-05 | `Both` 流向（旧语料 475 位点类；rt10 待重跑） | 仅当 type argument 两侧只出现在方法签名且无 mutable 成员才改写；否则 Unsafe 登记 |
| VR-06 | 值类型实参（`int` → `object`） | `DeclaredUnsupported`（装箱/擦除不等价） |
| VR-07 | 泛型委托方法组转换 | 先显式 lambda（与 delegate/closure-lift 协同）再 cast |
| VR-08 | 动态/未绑定类型 | `DeclaredUnsupported` + `NORM-GENERIC-VARIANCE-USE` |

安全五条件不变（引用类型实参/raw 同型或 Java 映射子类型（`ConversionContext.MapType` 验证）/读取传参位不写回/不跨 `ref|out|in` 与 Span/Memory ref-like/改写后仍可编译（per-file 后校验））；Java 侧看到的是 unchecked cast，只读流向下运行时语义与 C# 协变一致。Expression 域（增补 Yaml 元数据面维度）、extension 推断实参物化（P2-R2/R3）保留不变。诊断：`CS2J1404`（无法分类/改写）+ 报告记 VR-xx/源目标/方向/安全理由。

**R5 晚期链重排（决策点，kimi L6 提案——本方案列为决策而非直接采纳）**

现状：closure-lift/iterator/async/member-desugar/linq 在 generic-deepen **之后**新生成的泛型代码不会再进泛型分析（token/holder 需求可能遗漏）。kimi 提案把泛型分析移到最终形状（`...结构改写... → generic-contract-normalize → generic-deepen(最终 token 流) → generic-reflection-normalize → closed-metadata → variance-audit(最终复核) → java-keywords → normalization-audit`）。**权衡**：重排解决"晚生成泛型代码"类遗漏，但动 StateMachine/Abi 边界风险大（37×1101 的另一解法是把 closure-lift 提前，见 deepseek Q5）。本方案 stance：M3 先以统一解析器 + 依赖方向标注（Contract `Produces`）缓解；重排作为 M4 决策点，需先量化"晚生成泛型代码"的实际残差规模。

**R6 CLI 旋钮补齐**（修 G-24 相关）：`--generic-deepen-overrides/--generic-deepen-strict/--closed-metadata-strict` 暴露既有 options；`RunAfterIds` 增"启用依赖"语义（`--pass generic-deepen` 自动补跑 generic，或至少硬报错替代静默空跑——closed-metadata 已有 CS2J1205 先例）。

**R7 泛型内在函数 pass 化 `generic-intrinsic-lower` [v4 吸收 deepseek GI-01~11]**

新 Abi pass，插 `pointer-deref-index` 之后、`closure-lift` 之前（§6.5）。与 `unsafe-ref-lowering` 分工：后者保留现有窄形（`ref T x = ref MemoryMarshal.GetReference(span)` + `Unsafe.ReadUnaligned/Add`，具体值类型 U/byte span）且**先行**；新 pass 只处理 residual scanner 记录的未改写站点（不双写）。失败站点 `URL001` 迁移 `NORM-GENERIC-UNSAFE-INTRINSIC`，java-ready 下 Fail。

| 规则 | 输入 | 输出 | 条件 |
|------|------|------|------|
| GI-01 | `Unsafe.As<TFrom,TTo>(ref x)`，双实参具体值类型 | compat `Unsafe.As` 调用（TypeMappings 已有 Unsafe 映射） | 布局兼容可证 |
| GI-02 | `Unsafe.As<TFrom,TTo>(ref x)` 含 type param | token + `Unsafe.As(ref x, typeof(TFrom), typeof(TTo))` 或 `DeclaredUnsupported` | token 可达 |
| GI-03 | `Unsafe.BitCast<TFrom,TTo>(x)` | 显式 `(TTo)(object)x`（同宽值类型）或 compat | 可证同宽/无引用 |
| GI-04 | `Unsafe.SizeOf<T>()` | `sizeof(具体类型)` 或 compat `SizeOf(typeof(T))` | 值类型可证 |
| GI-05 | `MemoryMarshal.Read<T>(span)` | compat 调用（已有映射） | T 具体值类型 |
| GI-06 | `MemoryMarshal.GetReference<T>(span)` | `unsafe-ref-lowering` 优先；未命中 → compat | 元素类型可证 |
| GI-07 | `MemoryMarshal.Cast<TFrom,TTo>(span)` | compat 调用 | 同宽值类型 |
| GI-08 | `MemoryMarshal.GetArrayDataReference<T>(array)` | compat 或 `array[0]` 引用适配 | 元素可证 |
| GI-09 | `sizeof(T)` | `sizeof(具体类型)` 或 token helper（接 GD-MTOK-2） | T 具体值类型 |
| GI-10 | `Marshal.SizeOf<T>()`/`PtrToStructure<T>` | compat 映射或 `DeclaredUnsupported` | 平台/布局域 |
| GI-11 | `Unsafe.Add<T>(ref x, n)` | 现有规则优先；其余 `x[n]`/`span[n]` | 元素类型可证 |

开放约束（Q4）：允许近似的类型族（int/long/float/double/byte/char 同宽族）需与 compat 维护者对齐后写入条件列；未对齐前一律 `DeclaredUnsupported`。Yaml 实测 0 命中（该语料不触发），json 旧语料 26 MemoryMarshal/2 Unsafe/1 sizeof(T) 为主要验收面，rt10 重跑复核。

**R8 擦除桥接与状态机/record/attribute 残余规则 [v4 吸收 deepseek EB/AS/IT/RR/RW/AT/SA]**

member-desugar C46 泛型族从"保守跳过 + NORM-ERASURE-COVARIANCE 登记"升级为确定性规则（无法证明仍走登记）：

| 规则 | Owner | 输入 → 输出 |
|------|-------|------------|
| EB-01 | member-desugar | 同类型内擦除同名同签名（`M<T>(T)` vs `M(int)`）→ 重命名 + 生成桥接转发，保留公共 API 形状 |
| EB-02 | member-desugar | 泛型接口槽位歧义（CS2J5197）→ `OriginalDefinition` + 构造实参确定性选槽；不可选 → `DeclaredUnsupported` |
| EB-03 | member-desugar | 协变返回 × 擦除（CS2J5196）→ 桥接方法转发目标擦除类型（"deferred to C02 review"自此有承接者） |
| EB-04 | member-desugar | 显式泛型接口实现 → public bridge + private implementation |
| EB-05 | record-readonly | 泛型接口方法 ripple（RR070）→ 扩展 ripple 计划；不可证 → `DeclaredUnsupported` |
| EB-06 | member-desugar | 泛型 `with` 接收者（CS2J5223）→ 用站点闭合类型生成 clone；开放泛型 → `DeclaredUnsupported` |
| AS-01 | async-normalize | 泛型 async 局部函数：提升后重声明 method type params + constraints；CS2J1844 从"无 owner"→ Fail/DeclaredUnsupported |
| AS-02 | async-normalize | 泛型 `Task<T>`/`ValueTask<T>` token；`IAsyncEnumerable<T>` 擦除登记（rt10 13 文件新面） |
| IT-01 | iterator-lowering | 泛型状态机 `where` 子句重声明核对；`IEnumerator<T>` 擦除 |
| AT-01 | attribute-normalize | 泛型 attribute 扫描 → `DeclaredUnsupported`（Java annotation 不支持泛型） |
| SA-01 | member-desugar/generic-deepen | 接口 static abstract/generic math → CS2J1409 登记（json/Yaml 实测 0 命中，可判定性兜底） |

**R9 源生成器输出物化 [v5 吸收 kimi CM-05/§6.1 规则 5，修 W-12/G-29]**

在 L1 spine（csproj Compile 求值，W-10）之上增加生成器物化层——**不是新 pass**，落点 `ProjectPassRunner`/adapter 的 generated Compile items（与 W-10 同层）：

1. **执行与物化**：按 solution variant（project × TFM）真实执行 source generator（rt10 tests 的 STJ context、YamlDotNet.Test 的 StaticGenerator），生成文件物化为普通 .cs 进入 compilation 与输出中间树，带 `(generator, project, tfm, inputHash)` 四元身份。
2. **去重**：真实 build（B 路 `dotnet build`）会再执行一遍生成器——物化文件与 build 产物同 hash 只留一份；`inputHash` 不一致（生成器版本/输入变化）→ `NORM-SOURCE-GENERATOR` Error，防中间树与真实 build 各持一份生成代码。
3. **CM 协同**：物化后的生成源码进入普通泛型/闭型分析（`JsonSerializerContext` 派生不再仅 CS2J1204 排除——排除区收窄为"生成器本体项目"）；反射注册与源生成注册防互相误注册/重复初始化（同闭型双注册 → 残差登记）。
4. **失败语义**：无法执行生成器（analyzer 型 ProjectReference 不可载入/内部依赖）→ 该工程标 degraded + `NORM-SOURCE-GENERATOR`，java-ready 下 Fail 而非静默跳过测试工程。
5. **验收锚点**：B 路（预处理库+原测）中 rt10 tests 与 YamlDotNet.Test 的 `dotnet build` 通过即为本项硬证据（§7.1-6 指标行）。

**R10 ApiManifest 与 ABI diff [v5 吸收 kimi GD-06/§6.5 公开 ABI 规则，修 W-14b/G-32]**

凡 pass 改动**公开可见性**的签名/成员形状，登记统一清单（产物 `api-manifest.json`，随报告出库，schema 见附录 F）：

| 登记项 | 内容 |
|--------|------|
| 原符号 | assembly + declaring type + member 签名（改写前） |
| 生成符号 | 改写/生成产物（companion 重载/提升方法/holder/桥接方法/改名结果） |
| 变更类型 | tail-param-inject / promote / stub-body / bridge / rename / holder-lift … |
| 调用面 | 源码内调用点数 / 反射可达性 / 源码外可见性 |
| 批准状态 | approved（带批准记录） / pending（java-ready 下计为未批准） |

规则：①`public/protected/interface/override/extern` 成员的改写必须有 manifest 条目，`pending` 状态在 java-ready 下等同失败（与 R0 的 GD-MTOK 可见性分级联动——分级允许的 private 改写免登记）；②V-04 stub 修复（abstract/接口成员不再改 `virtual`/塞抛错体）落地后，此类条目应归零——manifest 同时是"ABI 禁改清单"的**回归探针**（`NORM-PUBLIC-ABI` 扫描 manifest 外的未登记签名变化）；③与 java-keywords 改名联动：原名称→生成名称映射入 manifest（kimi §3.1 java-keywords 行的元数据映射要求）。java-ready 严格化（§6.6 L2）增补条件：**ABI diff 无未批准项**。

**R11 Java 减负吸收规则族 JE-01~07 [v6 新增；修 J-01~J-07/G-33~G-39/W-15/W-16]**

> 立项纪律（I10，v7 修订）：每条规则的“被消除 Java 机制”列必须进一步分配稳定 JM-ID 与 capability。垂直切片按 §7.1-8 六件套验收；shadow 阶段允许 Java 零改动用于双跑，但不能据此退场，最终必须 retire 或对精确 partial 形状冻结并注明 reviewWave。JE-03/JE-04 仍是仅有的两个候选新 pass（§6.5），其余扩展既有 pass/配置；JE-08~JE-10 同样复用既有 pass + finalizer。

| 规则 | owner（落点） | 输入 → C# 侧输出 | 被消除的 Java 机制（I10 指认） | 诊断/残留 |
|------|--------------|------------------|-------------------------------|-----------|
| JE-01 | closed-metadata 消费面（R3 扩，**不新增 pass**） | `JsonConvert.DeserializeObject<T>(json[,settings])`（Newtonsoft，实测 MSAGL/测试语料高频）与配置化"序列化/工厂"入口：①入口进 `ClosedTypeConsumerSurfaces.json` 新 profile（`NewtonsoftJsonProfile`，库名只在 JSON）；②**目标槽位闭型在 C# 侧物化**——局部/返回/赋值槽位的闭型 T 改写为带显式 token 的 compat 形态（复用 CM-ENUM-3/CM-ENUM-5 的符号级槽位推断，不做文本匹配）；③T 为类型参数时接 GD-MTOK-2 token，不接得到则 `DeclaredUnsupported` | `JsonDeserializeRewrite.cs` 全文件（293 行，渲染后字符串前缀匹配/括号配对/槽位猜测三机制整体删除）；其"类型参数返回 null"兜底由 GD token 取代 | CS2J1210 扩；NORM-GENERIC-REFLECT 关联 |
| JE-02 | member-desugar EB 族扩展（R8 续，**不新增 pass**） | 用户类实现"将映射为 compat 集合接口（CSharpIList/CSharpCollection/CSharpGenericIterable）"的 C# 接口时：在 C# 侧显式发射**擦除形状成员**（`int Add(object)` 桥调 `Add(T)`、`bool Contains(object)`、`Iterator iterator()` 桥调 `GetEnumerator()`、返回类型对齐、`remove_2` 改名与 boolean 桥一次成型），符号级判定跨工程一致 | `Utilities/CompatCollectionConformance.cs`（732 行后处理）删除；"跨工程空注册表再推名"脆弱注释场景消失；b5adaaea 类 IList<T>/IList 双实现碰撞由声明侧桥接预防 | NORM-ERASURE-COVARIANCE 扩域（或新 CS2J1408 子码） |
| JE-03 | **候选新 pass `collection-shape-normalize`**（Expression；M3 立项、Q15 排序） | 实参/返回/赋值槽位上的跨形状隐式转换（T[]→IList/IEnumerable、IEnumerable→List、Span/ReadOnlySpan、IGrouping 迭代、集合工厂捕获）在 C# 侧插**显式适配调用**（复用 array-method/index-range 的 helper 注册表与符号事实；只处理符号可证且 Java 有唯一映射的形状） | `ArgumentTransformer.Coercion.cs`+`IListCoercion.cs`+`IterableCoercion.cs`（合计 1581 行）按 JE-03 覆盖率收缩为透传；compat 侧 OfType/fromAny/数组适配器 5 处补丁的对应特判分支删除（保留 compat 运行时本体） | NORM-COLLECTION-SHAPE；不可证形状 PreserveAndWarn |
| JE-04 | **候选新 pass `condition-effect-linearize`**（ControlFlow；M3 立项、Q15 排序） | 条件位置（while/for/do/if/`?:` 条件及 `&&`/`\|\|`/`??` 链）中的副作用子表达式（属性/索引器/字段 `++/--`、复合赋值、丢弃赋值、带 out/ref 实参调用）按 C# 求值顺序线性化为预语句规范形；循环统一为 `while(true){ <读改写>; if(!(线性化条件)) break; BODY }`（continue/break 等价证明入夹具，红测原型＝a2d3a6f0 的 `while (++start.Value < _length)`） | `StatementTransformer.Loops.cs` while(true) 线性化删除；getter pre-statement 泄漏类补丁（GetterPreStatementLeakTests 对应分支）收缩；AddPreStatement 在条件位的放置策略整体简化 | NORM-CONDITION-EFFECT；不可锚点（表达式树/迭代器体复杂形）保守保留 |
| JE-05 | compat-method-replacement 式**条目注册表扩展**（M4 决策，Q14） | 仅收录有实测提交证据的高频 BCL 族：`string.Concat(IEnumerable<char\|string>)` 序列展平、checked IO 异常边界、Decimal↔double 转换尾零——条目内容为"唯一可见签名/托管实现"，规则引擎复用现有注册表（含 W-09 漂移探测）；**不做通用重载决议重写** | a60afc75/f271d4ad/e8e69c94 三个 commit 引入的 Java 逐案特判收编为数据条目；条目零匹配时漂移告警（同 CMR0xx 纪律） | CMR0xx 扩；无实测条目的族不预注册 |
| JE-06 | 框架/L0（**不改写**，M1 先行） | 54 处裸 `throw new NotSupportedException` 逐个分类：①预处理应消解 → 接入 `NormalizationRequired.At` 并在盲区巡逻底册映射到 NORM 探针；②IR 本身不支持 → 迁移到独立 `IrNotSupportedException`（消息可区分），明确不属预处理责任；③误报防御 → 保留并加注释。底册＝附录 H 的 69 点清单 | 盲区巡逻底册从不可复现的"42"刷新为实测 69 点分类表；探针映射测试有准确输入；W-16 关闭 | 无新探针；一致性测试扩断言 |
| JE-07 | tuple/anonymous/holder 生成物 + R10 manifest（M3 随 R10） | ①生成物中的 `EqualityComparer<T>.Default.Equals/GetHashCode` 改发 `Cs2jPrelude`（或既有 Cs2jTuples 助手）的**Java 中立 helper 形状**（compat 提供唯一入口），不再依赖 Java 按名识别；②枚举全部 `__cs2j_*`/`Cs2j*` 生成标识符与 Java 侧逐名特判（InvocationExpressionTransformer:790-815 等），不能中立化的登记 api-manifest 的 `generatedContract` 类条目；③一致性测试：生成物标识符集合 × Java 特判表必须可对账（生成物形状变更如 2aa7aa8f 类事故在编译期暴露） | `InvocationExpressionTransformer.cs:790-815` 的 EqualityComparer→Objects 特判删除；G-11 残余（用户态 EqualityComparer 静态类）由 TypeMappings 一条映射关闭（归 §7.2 协调项） | NORM-PUBLIC-ABI 扩子域；生成物契约机检 |

**JE 与既有规则族的关系**：JE-01 是 R3 消费面配置化的**数据+槽位**延伸（机制不新，扩 profile 与闭型物化时机）；JE-02 是 R8 EB 规则的**接口实现一致性**延伸；JE-03/JE-04 是仅有的两个候选新 pass；JE-05 复用 compat 注册表；JE-06 是 L0 底册工作；JE-07 是 R10 manifest 的生成物子域。R11 与 R5（晚期链重排）正交：JE-03/JE-04 若立项，其在链上的位置同样面临"晚生成代码"问题，插入序按 §6.5 锚定并在 M3 量化后决定是否随 R5 重排。**[v7] 是否退休不再只由插入序和 on/off diff 决定，必须经 R12~R14 的能力握手与状态机。**

**R12 JCS-1 manifest 与两阶段握手 [v7，修 G-40]**

1. **产物与最小字段**：最终树根部写 `preprocess-manifest.json`，包含 `schemaVersion`、`subsetVersion="JCS-1"`、`profile`、`solutionVariant{configuration,projects[],tfms[]}`、`passPlan[{id,contractHash}]`、聚合 `contractHash`、`inputHash`、`capabilities[]`、`artifacts{apiManifest,residualReport,complexityLedger}`。完整示例见附录 F。
2. **hash 口径**：`inputHash` 是**最终预处理树的消费者 hash**，覆盖最终 Compile 源文件内容、项目图/TFM/条件符号、解析后引用集、物化生成文件与 support source；聚合 `contractHash` 由排序后的 pass ID、启用配置与规范化 PassContract 派生。`--no-preprocess` 因此只依赖已有最终树及其项目资产即可重算，不需要原始源码。原始源码 fingerprint 继续归现有增量缓存，不与消费者 inputHash 混用。任何最终源文件或 contract 变化都使握手失效；时间戳和机器相关绝对路径不得入 hash。
3. **能力派生**：每项 `{id,status,constructs[],finalizers[],residuals[],evidenceHash}`。`guaranteed` 只由最终树扫描派生，要求目标构造 residual=0、degraded=0、声明的 finalizer 全部成功；`partial` 必须列出残余 construct/file/owner，`unsupported` 必须给原因。禁止 adapter 自己硬编码 `"guaranteed"`。
4. **消费者需求目录**：Java 侧以静态 `JavaConversionRequirements`（代码或受审 JSON，单一事实源）声明每个 profile 所需 JCS capability、每个 JM 分支的 `requiredCapability` 与退休状态。启动转换时先校验，再构造 ConversionContext；不得在转换中途才发现握手不符。
5. **入口一致性**：正常链内直接传握手对象；`--no-preprocess` 从已有树读取并重算 inputHash。java-ready 下缺失、版本不兼容、hash 不符、finalizer 缺失或必要能力非 guaranteed → CS2J1410 且退出 1；default 原始树可继续转换但 `subsetStatus=unverified`：未退休 JM 兜底保留，命中已退休 JM 构造则提示先 preprocess。`--preprocess-only` 同样落 manifest，不能只提升源码而丢报告。
6. **兼容与边界**：JCS major version 必须精确匹配；新增可选 capability 向后兼容，改变既有后条件则升 major。manifest 不携带“某表达式应渲染成什么 Java”的指令，不复制 TypeMappings/SemanticModel，避免形成难以验证的旁路 IR。

**R13 最终树语义闭包与共享事实 [v7，修 G-41/G-42，替代 R5 的纯顺序争论]**

- **闭包算法**：①在所有结构生成与命名 pass 后重新枚举最终树构造；②将每个构造映射到 `Consumes/MayLeave/Owners` 与 capability；③对声明了 `FinalizerFor` 的 pass 按拓扑序**各有界执行一次**，不做全链不动点循环；④重建 project/TFM compilation 并执行 build/residual audit；⑤仍有未接管构造则 `NORM-SEMANTIC-CLOSURE`，能力只能 partial/unsupported。finalizer 自身产生的新构造必须登记到本轮后续 owner，否则失败。
- **与现有执行器衔接**：当前 `ProjectPassRunner.BuildStandardCompilation`（`:282-312`）按单 compilation 建树、`NeedsSemanticModel` 才绑定（`:321-341`），所以“早前 pass 已绑定”不能代表最终树。R13 复用同一 parse/compile spine 和引用画像，不另写一套审计编译器；逐项目 compilation 是解决方案级正确性的必要演进，单项目切片不被其阻塞。
- **R5 的处理**：不再把“整体晚期重排”作为前置豪赌。先由闭包报告量化晚生成构造及 owner 缺失；能靠现有 finalizer 接管的保持顺序，确需移动 generic-deepen 的构造族做 dual-order 影子实验，对比 C#/Java 产物与行为后按 capability 小步迁移。
- **共享事实唯一化**：`TypeParameterBindingAnalyzer` 已有可复用先例；新增/迁移 `ValueCopyFacts`、`EvaluationOrderFacts`、`CallShapeFacts` 到 `Core/Analysis`，输入只依赖 Roslyn abstractions。preprocess 与 Java 都可调用同一事实层，但**调用站点决策由 JCS owner 作出**；Java 对 guaranteed 构造只消费结果。依赖方向测试禁止 `Core/*Preprocess*` → `Core/Transformers`。
- **原子性**：finalizer 以计划为单位，所有生成文件、调用点和 manifest 更新一起提交；编译/审计失败回滚该计划，不能留下“声明已改、调用点未改、manifest 却仍 guaranteed”的半成品。

**R14 Java 补偿分支退休协议与总复杂度账本 [v7，落实 I14/I15]**

| 状态 | 必做动作 | 退场证据 | 回滚/兼容 |
|------|----------|----------|-----------|
| `inventory` | 给目标 Java 分支/文本手术/语法 fallback 分配 JM-ID；记录静态决策点、语料 hits、owner、所需 capability | 附录 H 可由代码派生，行号漂移不影响 ID；基线测试与 hits 落账 | 旧行为不变 |
| `shadow` | preprocess opt-in 改写；旧 Java 路径仍启用；做 C# B 路、Java 产物 diff、JUnit/测试数对比 | 目标形状覆盖率、未覆盖残差、行为等价与性能记录齐全；能力至多 partial | 单 capability kill switch 回到旧路径 |
| `enforce` | 最终树能力升 guaranteed；java-ready 默认走新形状；旧 JM 分支加“命中即 CS2J1410”断言 | 双语料及 fixture 旧分支 0 hits；`--no-preprocess` 正确/错误 manifest 正负测试通过 | default+unverified 仍允许旧路径；严格模式不可绕过 |
| `retire` | 配套 Java PR 删除/收缩 JM 分支，移除不再需要的 import/helper/文本修复 | Java 负 diff、preprocess/compat/Analysis 增量、转换产物与 JUnit 等价同时合入 | 通过配对 revert 恢复上一状态，不留半删声明/调用点 |
| `ratchet` | 删除临时命中断言，保留契约/残差/防回流测试 | 新 JM 分支无 capability/owner 不得合入；目标 residual/hits 只许减 | 长期门，不再保留双实现 |

`frozen` 不是第六个退休阶段，而是 `partial` 能力的覆盖注记：必须有 JM-ID、未覆盖构造、owner、原因与 reviewWave；一旦对应语料/规则补齐就回到 shadow/enforce。不得用 frozen 将整个文件永久排除在对账外。

**总复杂度账本**至少包含：`targetedJmCount/retiredCount`、`javaSemanticDecisionBranches`、`renderedTextRewriteSites`、`syntaxFallbackSites`、`fallbackHitsBefore/After`、`preprocessRulesAdded`、`compatEntryPointsAdded`、`sharedFactImplementations`、各层物理行数 delta、残差率与测试数。完成判据为：目标 JM 退休率 100%、enforce 后旧路径 hits=0、共享事实重复实现=0、测试数与行为不回归；**不要求每个正确性切片总行数都为负，但要求没有同构决策表跨层复制，且被指认 Java 机制真实净下降。**

**R15 v7 新增垂直切片 JE-08~JE-10 [修 G-42~G-44]**

| 规则 | owner/输出形状 | 退休的 Java 机制 | 必过反例与边界 |
|------|----------------|------------------|----------------|
| JE-08 byref/out 条件序列 | `ref-out-in` + JE-04 共享 `EvaluationOrderFacts`：hoist out-var 声明到 C# 词法作用域；按短路 AST 用 bool temp/嵌套 if 保持懒求值；holder 声明、调用、读回放在同一已执行分支；循环 continue 前先恢复下一轮条件求值 | `StatementTransformer.Conditionals.cs:373-433` 的逐操作数 BuildShortCircuitCondition、悬挂 post 收口、if 前 temp/read-back 分支；与 JE-04 的 Loops 分支同片退休 | `!Try(out x) \|\| x.P`、and/or 混合、异常、out-var 在 if 后使用、do/while/for、continue/break；表达式树不重写，不可证形状 residual |
| JE-09 调用形状规范化 | 复用 delegate/strip-default/extension-methods/params 能力，补统一 `CallShapeFacts` 与最终审计：显式 `.Invoke()`、方法组→已定型 lambda/静态调用、扩展→静态调用、命名/可选参数→按决议位置实参、params→显式数组/集合、必要泛型实参显式化；需要保持实参求值顺序时先提取 temp | `InvocationExpressionTransformer.cs:790-924` 中方法组/隐式 delegate Invoke 等 C# 形状分支及其他登记 JM 分支；**纯 BCL/String/compat 映射不移交也不计入删除目标** | 可选+命名+params 组合、扩展泛型推断、方法组型变、委托空值/条件访问、ref/out 与实参副作用顺序、public ABI 不变；不能证明则保留原调用形状 |
| JE-10 struct 值拷贝收口 | `Core/Analysis/ValueCopyFacts` 唯一实现 + 既有 struct-value-semantics 计划：赋值、实参、返回、字段/数组元素、属性 getter、操作数等槽位显式 copy；copy ctor/shim/调用点原子生成；免拷必须带 `FreshValue/Immutable/NoAlias` 等机器可读理由 | `StructCloneHelper` 在 Argument/Assignment/Return/Statement 中的**站点判断**退休，helper 仅保留 Java copy 调用渲染所需的窄工具；不是机械删除全部 450 行 | alias 写入、数组元素、属性返回、用户 Clone 与字段级 value-copy 区分（`StructCloneHelper.cs:390-445`）、override family ref 参数一致（`:100-180`）、this 赋值、构造器/递归 struct、异常路径 |

JE-03/JE-04 与 R15 的实施粒度统一为“共享 facts + 小规则 + 独立 capability”：不要求为 JE-09 再建默认 mega-pass；最终 call-shape/value-copy/closure 审计是只读 finalizer。任何切片不得借“Java 友好”之名把 BCL 映射表、Java 擦除命名或 compat 运行时搬入 preprocess。

### 6.5 顺序与契约调整 [v4 扩充：完整插入序 + 兼容性细则；v7 增能力闭包]

新 pass / 规则插入点（**粗体为新增**；其余 52 pass 相对位置不变，M0 缺陷修复全部原地不动顺序）：

```text
unsigned → generic → **generic-constraint**（R2/CN）
            → thread-static → generic-deepen（+GD-MTOK-2/NEST-2/STATIC-3/扩作用域）
            → closed-metadata（+R3 消费面配置化/CM-STRICT）
            → variance-audit（只读基线，保持原位）
...（record-readonly … unsafe-ffm → stackalloc-lowering → ref-struct-lowering
   → ref-struct-span-field-memory-lift → pointer-deref-index 相对位置均不变）
pattern-desugar → **condition-effect-linearize**（[v6] JE-04 候选，RunAfter: pattern-desugar, expression-desugar, null-coalesce；条件副作用线性化先于一切循环/资源重写）
pointer-deref-index → **generic-intrinsic-lower**（R7/GI，RunAfter: pointer-deref-index）
closure-lift → iterator-lowering → async-normalize（+AS-01/02）→ extension-methods
   → eliminate-goto → member-desugar（+R8/EB +[v6] JE-02 集合一致性桥接）→ linq-channel → concurrency-normalize
   → equality-concat
equality-concat → **collection-shape-normalize**（[v6] JE-03 候选，RunAfter: linq-channel, extension-methods, array-method, equality-concat；跨形状适配在扩展方法/LINQ 定型后）
   → **variance-use-site-normalize**（R4/VR，RunAfter: member-desugar, linq-channel, equality-concat）
   → platform-isolation → dynamic-desugar
   → **semantic-finalizers**（[v7] R13：按 capability 有界执行 JE-04/08/09/10 等 finalizer；不是新 mega-pass）
   → java-keywords → **generic-reflection-audit**（RG 只读面，RunAfter: java-keywords）
   → normalization-audit（+§6.3 二十六探针族 + 最终树能力审计）
   → **manifest emission**（[v7] R12 框架产物，不是 pass；只在审计成功后写 guaranteed）
```

兼容性细则（吸收 deepseek §8.9，52 pass 版改写）：

- `RunAfterIds` **只增不减**：旧锚点全部保留，既有拓扑断言与 `--list-passes` 快照仅因新增条目更新（快照测试随波次提交）。
- `generic-deepen` 的 RunAfter 追加 `generic-constraint`；`thread-static` 仍锚 `generic`——Kahn tie-break 按声明序保持 generic → generic-constraint → thread-static → generic-deepen。
- `closure-lift` 追加锚 `generic-intrinsic-lower`（内在函数先降、闭包后提升）；`java-keywords` 追加锚 `variance-use-site-normalize`（型变改写先于命名改写）；`normalization-audit` 追加锚 `generic-reflection-audit`。
- 新 pass 默认开启（all-preprocessors-default-on 策略），`--disable-pass` 可关；`generic-reflection-audit` 只读不改写、可先行合入。
- 单 pass 依赖显式化（R6）：`--pass closed-metadata` 自动补跑 generic-deepen 或硬报 CS2J1205；同规则适用于新 pass 对 generic-constraint/generic-deepen 的依赖。
- **[v6/v7] 两候选 pass 的立项闸**：`condition-effect-linearize`/`collection-shape-normalize` 先 opt-in，不直接进默认链；v7 进一步要求**按形状族/capability 独立翻转**，而不是整个 pass 一次默认开启。每族完成正/负/幂等/回退 fixture、真实语料覆盖率、R14 五态退休后才把对应残翻转 Fail；未覆盖族保持 PreserveAndWarn + Java 兜底。两者标注 `RequiresWholeTree`，在 M5 统一树默认前接受整树成本。
- **[v7] finalizer 顺序**：JE-08/09/10 复用既有 pass，不改变其原顺序；最终只读审计/有界 finalizer 必须 RunAfter 所有相关 Produces owner，并位于 java-keywords 与最终 normalization-audit 之前。finalizer 生成的公开形状仍须进入 java-keywords/ABI/闭包审计；manifest emission 不得作为可被 `--enable-pass` 选择的伪 pass。

### 6.6 L1/L2/L4 层

**L1 编译层**：真实引用集 spine 化——一次 restore 全 pass 共享；对现存三个 `GetMetadataReferences` override（extension/async/linq）收敛为 spine 追加项而非平行实现；`degraded` 显式化（java-ready 拒绝 degraded 编译）；报告记**引用画像**（每工程 resolved 程序集数/degraded 原因/未绑定符号 Top-N）。**[v3 升格]** `tools/PreprocessCorpusProbe/MsBuildProject.cs` 的逐项目 MSBuild 求值能力（DefineConstants/TFM/Compile/ResolveAssemblyReference/Nullable/global usings 已实现）从"可选上移"升为 **rt10 语料硬前置**（W-10/G-26）：文件输入必须改为 **csproj Compile 项驱动**——rt10 的文件级 TFM Condition（`JsonReaderHelper.net8.cs` vs `.netstandard.cs` 同类双实现）、`$(CoreLibSharedDir)`/`$(CommonPath)` 外部共享源、gen/ 的 `ROSLYN4_x` DefineConstants 均在 csproj 里，目录 glob 输入必产出重复成员/缺源树。`.slnx` 顶层解析已支持（`ProjectPreprocessSource.cs:32,74`），缺的是 Compile/Condition 求值进 spine。注意三处现状不一致需一并收敛：conditional 默认取**首个 TFM**（rt10 = net10.0 → `NET` 符号集，恰为正确选择，但须显式记录进报告而非默认巧合）、链默认 Debug 符号、verify 固定 Release。**[v5 增量]** ①源生成器输出物化挂接 spine 之上（R9/W-12）——generated Compile items 与 csproj Compile 项同一输入模型；②**逐项目 compilation 终态**（W-14a/G-31，kimi `PreprocessWorkspace` 设计的裁剪采纳）：终态为 solution variant（configuration + 每项目显式 TFM）下每项目独立 `CSharpCompilation`，跨项目引用用 `CompilationReference`/真实元数据引用——恢复 internal 可见性、`InternalsVisibleTo`、重复类型名与测试/生产工程分离的真实语义（`_Cs2jClosedTypes.<Project>.cs` 的按工程分文件已具备输出侧基础，缺的是绑定侧）。**落地分级**（与 kimi 列为 L0 硬规则不同）：单项目主路径（rt10 csproj/Yaml 库 csproj）不受影响、无需等待；解决方案级路径（YamlDotNet.sln/csharp-sdk/json tests）为受益面，列 **M3 决策点 Q13**——需先量化"合并 compilation 假阴性/假阳性绑定"在解决方案级语料上的实际规模（variance-audit 的 126 baseline-error 跳过文件是现成样本）。

**L2 验证层**：四路差分（A 原库原测 / **B 预处理库+原测**（不可省——只跑 C 会让两侧同步改错）/ C 双预处理 / D 原库+预处理测）；残差 ratchet（`preprocess-residual-report.json` 快照入库、"只许减不许增"、更新需评审）；java-ready 严格化（必需 pass 不可 `--disable-pass` 绕过、baseline-error=0 且非 degraded、目标域内 DeclaredUnsupported 也须 0、skip/force override 带 owner/理由/到期复审、二次全链输出逐字节一致、报告记 configuration/TFM/引用 hash）；探针"盲区巡逻"一致性测试（凡有 Java 硬门禁而无 NORM 探针的构造 → 测试失败，反向枚举驱动登记）。**[v5 增量]** java-ready 严格化追加三条件：①两入口 profile 语义统一后必需 pass 排除一律配置错误（W-11/R0）；②ABI diff 无未批准项（R10 `api-manifest.json`）；③B/C 路涉及的测试工程源生成输出已物化（R9）否则该工程计 degraded。
**[v6 增量] ④Java 侧净简化对账门（W-15/I10）**：每个 JE 规则波次产出对账记录（入报告附录 F 的 `javaSimplification` 段）——被指认机制（文件:行号区间/分支标识）、预处理 on/off 两跑的 convert-project 产物 diff、该机制删除/收缩的行数与 JUnit 结果对比；规则合入但对应 Java 机制**无正当理由未删除**则波次不退场（正当理由仅限：真实语料未覆盖到的形状保留兜底，须登记到期波次）。⑤**fail-fast 底册归一**（W-16/JE-06）：M1 起 69 个 throw 点全部分类（预处理责任/IR 不支持/防御），盲区巡逻测试对底册做派生断言——新增裸 throw 而不归类即 CI 失败。
**[v7 增量] ⑥握手的正/负门**：正常链、`preprocess-only` 与 `--no-preprocess` 对同一树得到同一 JCS 结论；缺失/篡改/过期 manifest、错误 TFM、contractHash 不符、必要 capability=partial 在 java-ready 下全部 CS2J1410；default 原始树明确 unverified 且保留兜底。⑦**最终树闭包门**：对“早期 pass 消除、晚期 pass 再生成”的构造有专测；最终树 residual 与 Produces/Owners 守恒，finalizer 失败时计划原子回滚。⑧**退休与总复杂度门**：以 JM-ID 取代单纯行号对账，逐切片检查 enforce hits=0、retire 负 diff、ratchet 防回流，并同时报告 preprocess/compat/Analysis 增量与共享事实重复数；只报 Java 删行、不报新增决策实现不得退场。

**L4 性能层**：v1 首版不变（统一树默认化 + 增量缓存键含 solutionVariant/referenceSetHash/generatorHash + 选择性 verify + pass 级并行需 Contract 数据依赖判定）；增补：残差 gate 不得用另一套引用和 parse options 重编译出"第二种事实"；新增全树 pass 不得使链总时长劣化超 10%。

---

## 7. 验收标准

### 7.0 验收与复现命令 [v4 新增]

```powershell
# pass 链（顺序即执行序；52 条 + 新增条目随波次更新快照）
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess --list-passes

# json 主语料（rt10 真源；M0 基线重跑 + 各波次回归统一入口）
# [v8 实测状态] 默认链止于 local-function-lift（V-09），修复合入前用 --no-local-function-strict 作归因探针（非验收路径）；
#   归因日志基线：D:\cs\rt10-probe-v8.log（默认链）、rt10-probe2-v8.log（+--no-local-function-strict）
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\runtime-10.0.12\src\libraries\System.Text.Json\src\System.Text.Json.csproj `
  -d D:\cs\rt10-java-ready --unified-preprocessed-src --profile java-ready

# YamlDotNet 库项目（链鲁棒性回归：W-06 修复后必须跑完）
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\YamlDotNet-18.0.0\YamlDotNet\YamlDotNet.csproj `
  -d D:\cs\yaml-lib-java-ready --unified-preprocessed-src --profile java-ready

# YamlDotNet 解决方案（partial-merge 白名单/降级验证）
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\YamlDotNet-18.0.0\YamlDotNet.sln `
  -d D:\cs\yaml-sln-java-ready --unified-preprocessed-src --profile java-ready

# 单 pass 复跑（泛型能力定位；closed-metadata 单 pass 修复后的崩溃回归）
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s D:\YamlDotNet-18.0.0\YamlDotNet\YamlDotNet.csproj -d D:\cs\yaml-gd-only `
  --pass generic-deepen --no-verify-build --no-verify-tests

# 下游 Java 验收（与所在垂直切片配对，最终目标门）
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s <rt10-java-ready 的 .sln> -d <java 输出> --no-verify-build --no-verify-tests
# 之后对生成 Maven 工程执行 mvn compile + JUnit

# [v7/R12] 已有树握手：java-ready 不再忽略 profile；启动时校验 preprocess-manifest.json
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s D:\cs\rt10-java-ready -d <java 输出> --no-preprocess --profile java-ready `
  --no-verify-build --no-verify-tests
# 负例由 manifest contract tests 覆盖：缺失/篡改/过期/hash 不符/partial 必需能力 => CS2J1410 + 退出 1
```

> rt10 全链初始基线（各 pass 统计/残差分布）由 M0 建立；上表即 M0 与各波次的统一入口，报告落 `-d` 目录（schema 见附录 F）。

**[v5] per-TFM 验收纪律**（吸收 kimi §9.4）：B 路差分的测试执行必须按工程的目标框架逐 TFM 运行，且**换 TFM 必须重新 preprocess**——中间树源码已按选定 variant 特化（conditional 剥离/Compile 项选择），不得复用 net10 特化源码跑 net8 测试：

```powershell
# YamlDotNet 测试工程：每个选定 variant 独立执行
dotnet test <最终中间树>\YamlDotNet.Test\YamlDotNet.Test.csproj -c Release -f net10.0
# rt10 tests（若纳入 B 路语料面）
dotnet test <最终中间树>\tests\dotnet.text.json-tests.csproj -c Release
```

### 7.1 JCS 垂直切片验收（预处理 + 配套下游）

1. **审计与能力清零**（java-ready profile）：**[v3] json 语料口径切换为 `D:\runtime-10.0.12` 真源** + YamlDotNet 库项目两语料，退出码 0；泛型探针与本切片 JCS 探针的 `Fail` 策略码为 0；`PreserveAndWarn` 观测码有计数且带 owner/后果；`NORM-PROJECT-DEGRADED` = 0。**[v7]** 本切片必需 capability 全为 guaranteed，最终树无 SEMANTIC-CLOSURE 残差；partial/unsupported 不得被报告成 0 残差。
2. **四路差分**：B 路（预处理库 + 原测）`dotnet build`/`dotnet test` 零失败——两语料分别执行；**比较转换前后测试数量**，防止被跳过/未生成的测试误记为通过。
3. **行为反例测试**（吸收 kimi 21 类中的核心 16 类——v5 增补并发/NRT 反射行为/类型关系查询/成员发现四类，**v6 再增条件线性化/生成物契约两类共 18 类，v7 再增 byref-out 条件/调用形状/struct alias/manifest 与最终闭包四类共 22 类**，每类一个 xUnit fixture）：per-closed-type 恰一份静态、readonly 至多初始化一次、静态 cctor 恰一次、先写后读副作用、abstract/接口分派不变、`Outer<T>.Inner<U>` 与 `Outer<T>.Inner<T>` 双形态、CRTP 收敛、public 泛型成员经 MethodInfo/Activator 调用、NRT 键同轨（`List<string>` 与 `List<string?>` 同一注册）、约束传递闭包（`IFoo<U>` 携 U）、合成物幂等（二次运行逐字节一致）、多 TFM 独立通过；**[v5 增四类]** ①并发（多线程读写/初始化同一闭型：恰一次初始化、无损坏/丢失/重复发布——R0 holder 并发证明的行为面）、②NRT 反射元数据行为（`NullableAttribute/NullableContextAttribute` flags 保留或等价重建，Yaml `AcceptsNull→AllowNulls→空值异常` 链行为一致——G-22 的运行时证明）、③类型关系查询（泛型基类/接口的 `IsAssignableFrom`/接口映射/unknown `Type` 的查询结果与遍历顺序一致）、④成员发现与访问（new/override 成员集合与顺序、`DeclaredOnly`/`BindingFlags` 过滤、`MethodInfo.Invoke` 参数绑定/`TargetInvocationException` 包装差分）；**[v6 增两类]** ⑤条件副作用线性化等价（`while`/for/do/`?:`/`&&` 链中属性与索引器读改写，continue/break/异常路径与原 C# 逐次求值一致——红测原型 a2d3a6f0 的 XsdDateTime `while (++start.Value < _length)` 死循环）、⑥生成物 Java-中立契约（tuple/holder 生成物不依赖 Java 逐名特判：改发 prelude helper 后双跑转换产物一致；生成物标识符×Java 特判对账表无机检外项）；**[v7 增四类]** ⑦byref/out 条件序列（`!Try(out x)||x.P`、短路嵌套、if 后 out-var、异常/continue，JE-08）、⑧调用形状（delegate null/条件访问、方法组型变、可选+命名+params、实参副作用顺序，JE-09）、⑨struct alias 与 copy 计划（数组/字段/属性/override family/用户 Clone 与字段 copy 不混用，JE-10）、⑩manifest 与最终闭包（已有树正/负握手、晚生成构造再接管、finalizer 原子回滚，R12/R13）。
4. **回归**：三测试工程全绿；**[v3] json 基线：rt10 真源全链 Java 转换 0 失败（M0 重跑建立新基线；旧语料 626/0 仅作历史对照，不再作为回归门）**；`Core/` 无库名特判（grep 守门：`YamlDotNet`、`System.Text.Json.JsonSerializer` 零命中——后者指消费面常量迁出后）。**[v7]** 增加依赖方向守门：preprocess 命名空间不得引用 `Core.Transformers` 获取语义事实；ValueCopyFacts/EvaluationOrderFacts/CallShapeFacts 每类恰一个共享实现。
5. **每类缺口的最小验收样例**（v1 首版 §7.2 表基础上 v2 修正、v3 增补 rt10 行）：

| 缺口 | 最小输入 | 期望最终树 |
|------|---------|-----------|
| V-01 | `class C<T> { static readonly C<T> Empty = new(); }` | holder 缓存式 getter，两次访问同引用 |
| V-02 | 同型两个 `static T s_a; static List<T> s_b;` | holder 单份 gate/store，可编译 |
| V-09 [v8] | switch-default case 块内 static 局部函数（rt10 `JsonElement.DeepEquals` 双函数形态） | lift 后中间树**零残留声明**（类级方法存在、调用点指向新方法、原声明删除）、`dotnet build` 通过、链不中止；聚合 residual 诊断带文件/行 |
| S-02 | 泛型方法内表达式体 lambda 中 `typeof(T)` | 包块体后锚点可达，无 CS2J1101 |
| S-03/04 | 泛型方法 lambda 捕获 + `where T : struct` 元组形状 | 合成物携带约束，无 CS0453/CS0246 |
| G-02c | `object x; var y = x as T;`（T 参数） | `typeof(T).IsInstanceOfType(x) ? (T)(object)x : default` 单求值形态 |
| G-06 | `static T? F<T>(T v) where T : struct` | `Nullable<T>` 显式 + VA 门禁计数为 0 |
| G-04b/d | `typeof(Dictionary<,>).MakeGenericType(typeof(int), typeof(string))`；Yaml `serializer.Serialize(x)` 入口 | 前者物化闭型 typeof；后者进消费面配置枚举 |
| G-11 | `EqualityComparer<T>.Default.Equals(a, b)` | `Cs2jPrelude.EqualityOps.Equals<T>(a, b)` |
| G-16 | （统一解析器）closure 提升 + `where U : IEquatable<U>` | 重声明携带约束 |
| G-18 | `IEnumerable<int>? x = y ?? [];` | 链不再中止（null-coalesce 对 `[]` 保守跳过而非 Error，交 expression-desugar） |
| G-24 | `--profile java-ready --disable-pass generic-deepen` | 配置错误报错而非静默绕过 |
| G-25 [v3] | rt10 真源上 `JsonWriterHelper.ToUtf8`（3 参 char-span） | compat 条目命中并置换（或 net10 符号集下 `#if NET` 分支存活、无指针体可置换时**显式跳过而非报缺失**）；漂移探测对"类型存在但零匹配"给 Error |
| G-26 [v3] | rt10 `System.Text.Json.csproj`（文件级 TFM Condition + 外部共享源） | 预处理输入树 = csproj Compile 求值结果：net10 变体恰好一份 `JsonReaderHelper` 实现、共享源（HexConverter/ValueStringBuilder 等）在树内、无重复成员 |
| G-27 [v3] | `get => field ?? GetOrCreateSingleton(ref field, X)`；`ref struct S(Options o){ ReadOnlySpan<byte> _b; }`；`using A = System.ValueTuple<int, string?>;`；`Combine(params ReadOnlySpan<IJsonTypeInfoResolver?>)` | 依次：backing 字段物化且 `ref` 实参改写正确；主构造参数物化 + Span 字段可提升；别名解析出元组形状进 tuple 槽位；params-span 声明物化 + 调用点展开（K2 既有路径的真源盖章） |
| G-28 [v5] | `--profile java-ready --disable-pass normalization-audit` 分别走 `preprocess` 与 `convert-project` | 两入口同报配置错误（或统一显式放行 + 报告 `profileDegraded`，按 R0 推荐取前者）——不再一静默一失败 |
| G-29 [v5] | rt10 tests 中 `JsonSerializerContext` 派生 context 的测试工程；YamlDotNet.Test 的 StaticGenerator helper | 生成输出在中间树物化恰一份（`(generator, project, tfm, inputHash)` 身份），B 路 `dotnet build` 通过；`NORM-SOURCE-GENERATOR`=0 |
| G-30 [v5] | `var f = serializer.Build(); var r = f(x);`（工厂延迟消费）/ `await foreach` 异步流中闭型消费 | guard 位于实际执行入口（工厂产物首次使用点/枚举起点），deferred 消费不再依赖词法相邻 |
| G-31 [v5] | 解决方案级两项目含同名 `internal` 类型 + `InternalsVisibleTo` | 各项目独立 compilation 绑定正确，无跨程序集符号污染（M3/Q13 决策后落地） |
| G-32 [v5] | GD-MTOK 对 `public` 泛型方法注入尾参的站点 | 原签名保留 + companion invoker；`api-manifest.json` 有对应条目；java-ready 下 `pending` 条目 = 0 |
| G-33 [v6] | `var d = JsonConvert.DeserializeObject<Order>(json); string F<T>(){ return JsonConvert.DeserializeObject<T>(json); }` | 前者 C# 侧槽位闭型物化（显式 token 实参），后者带 GD token；转换产物不再出现 JsonDeserializeRewrite 的 rendered 文本分支 |
| G-34 [v6] | `class MyList<T> : IList<T>`（含 `void Add(T)`/`GetEnumerator`）经映射后实现 CSharpIList | C# 侧已显式发射 `int Add(object)`/`iterator()`/`remove_2` 桥；CompatCollectionConformance 对应分支零命中 |
| G-35 [v6] | `void F(IEnumerable<int> e){ g(e); }`（g 形参为映射后 List 形态）；`foreach` over IGrouping | 调用点 C# 侧显式适配调用；NORM-COLLECTION-SHAPE 残留 0；IListCoercion/IterableCoercion 相关分支零命中 |
| G-36 [v6] | `while (++start.Value < _length)`；`if (a[i++] == x) …`；`?:` 中属性复合赋值 | 规范形预语句 + `while(true){…if(!C)break;…}`；continue/break/异常夹具全绿；Java Loops 线性化分支零命中 |
| G-37 [v6] | `string.Concat(chars)`（chars 为 IEnumerable<char>）、checked IO 调用 | 经条目注册表解析为唯一签名；a60afc75/f271d4ad 对应 Java 特判分支零命中或登记冻结 |
| G-38 [v6] | 69 个 throw 点底册 | 每个点有分类标签（预处理责任/IR/防御）；盲区巡逻测试对底册派生断言通过 |
| G-39 [v6] | tuple struct 的 `EqualityComparer<__P0>.Default.Equals` 生成物 | 改发 Cs2jPrelude/助手中立形状；Java `:790-815` 特判删除；生成物×特判对账表无表外项 |
| G-40 [v7] | 对同一已有树分别走正常链与 `--no-preprocess --profile java-ready`；再删除/篡改 manifest 或改一个源文件 | 正例两入口 capability/inputHash 结论一致；负例均 CS2J1410，不再 Warning 忽略 profile；default 原始树标 unverified |
| G-41 [v7] | 早期 generic-deepen 后，由晚期 closure/tuple/member pass 再生成一个 `typeof(T)`/待 copy 构造 | 最终闭包发现并交给有界 finalizer；无 owner 时 NORM-SEMANTIC-CLOSURE，capability 不得 guaranteed；失败计划逐字节回滚 |
| G-42 [v7] | struct 含用户 `Clone()`、数组元素与 override family ref 参数；扫描依赖方向 | 字段级 value-copy 与用户 Clone 不混用；所有槽位 copy/免拷理由可审计；preprocess 不再引用 Transformers.StructCloneHelper |
| G-43 [v7] | `if (!TryGetTarget(out var repl) \|\| !repl.Pattern.HasValue) { … }`；if 后继续读 repl | out-var 作用域正确，右侧仅在前侧求值成功后执行；holder read-back 不提前、不悬挂；Conditionals 指认 JM 分支 0 hits 后 retire |
| G-44 [v7] | `Action a = null; a?.Invoke()`；`M(x: 1, values: new[]{2,3})`；泛型扩展方法组 | C# 调用形状显式且求值顺序不变；BCL 映射仍由 Java 负责；仅登记的 call-shape JM 分支退休 |

6. **指标矩阵（升级前 → 升级后目标）[v4 新增]**——"前"列除注明外均为**旧语料历史基线**（§3.2.3），rt10 重跑后逐项替换：

| 指标 | 升级前 | 升级后目标 |
|------|--------|-----------|
| 默认链可完成（Yaml 库 / Yaml 解决方案） | 否（`?? []`）/ 否（CS2J2018） | 双语料均跑完（W-06 保守化 + partial-merge 白名单/降级） |
| json `CS2J1101` / `CS2J1106`（旧语料） | 37 / 3 | 0 或全部 `DeclaredUnsupported`（GD-NEST-2/STATIC-3 + L1 引用集） |
| json closed-metadata 残留 / dynamic 未登记（旧语料） | 281 / 369 | 0（CM-STRICT + profile 化） |
| json variance-audit 未处置位点（旧语料） | 704 位点 / 221 数组 / 53 unsafe | 全部分类：已改写 / 安全免改 / `DeclaredUnsupported`（VR） |
| json 约束依赖位点（旧语料） | 3 default / 8 new / 5 nullable | 0 未处置（CN + GD-MTOK-2） |
| Yaml `generic`/`generic-deepen` 链内真实执行 | 否（链中止；单 pass 才见 2 `new T()` / 100 anchors） | 默认链内执行且全部 token 化 |
| `--pass closed-metadata` 单跑（Yaml） | `_compilation` null ×20 崩溃 | 干净报 CS2J1205 或跑完（R3 单 pass 修复） |
| C# 中间树 build/test（两语料） | 局部可过 | 全链 0 error / 0 failure（B 路差分） |
| java-ready 泛型 Fail 残留 | 0 探针（无门） | 0（`DeclaredUnsupported` 除外，带 owner/文件/行/原因） |
| [v3] rt10 全链基线 | 未建立 | M0 建立（§3.2.3"rt10 重跑关注点"逐项落档） |
| [v5] 源生成测试工程 B 路可编译（rt10 tests / YamlDotNet.Test） | 不可（生成输出缺失） | 可（R9 物化恰一份，`NORM-SOURCE-GENERATOR`=0） |
| [v5] ABI manifest 未批准/未登记条目 | 无清单（无门） | 0（R10 + `NORM-PUBLIC-ABI`=0，java-ready 下 pending=0） |
| [v6] `JsonDeserializeRewrite` 渲染后文本手术 | 293 行（v5 后新增，类型参数 null 兜底） | JE-01 后整文件删除；Newtonsoft 闭型槽位经 C# 侧物化，0 文本匹配 |
| [v6] 集合擦除一致性 Java 后处理 | `CompatCollectionConformance` 732 行 | JE-02 后文件删除（或仅余语料未覆盖兜底，行数登记冻结到期波次） |
| [v6] 强制适配网规模 | ≈1666 行（Coercion 1098+266+217+85） | JE-03 覆盖率内分支零命中；总行数只许降（每波次对账，I10） |
| [v6] 条件位 Java 线性化 | 渲染期 while(true) 重写（已出死循环 bug） | JE-04 后对应分支删除；XsdDateTime 形 B 路/C 路等价 |
| [v6] fail-fast 口径 | 统一门 15 + 裸 throw 54（旧底册 42 不可复现） | 69 点全分类；新增裸 throw 不归 CI 失败；映射到 NORM 探针的点 100% |
| [v6] 生成物×Java 特判对账 | 口头契约（tuple EqualityComparer 等 ≥1 处硬编码） | JE-07 后表外特判 0；改形状类事故（2aa7aa8f 型）编译期暴露 |
| [v6] JE 规则的 Java 负 diff 兑现率 | 无此指标（W-15） | 每条已落地 JE 规则有对账记录；"合入但未瘦身"规则数 = 0（冻结项除外，带到期波次） |
| [v7] manifest 入口一致性 | `--no-preprocess` + java-ready 仅 Warning 并忽略 profile | 正常/已有树入口同结论；缺/旧/假 manifest 与必要 partial 100% 阻断；unverified 仅存在 default |
| [v7] 最终树闭包 | 只有早期 pass 残差；晚生成构造无守恒 | 目标 capability 的 Produces→owner 覆盖 100%；SEMANTIC-CLOSURE=0；finalizer 原子回滚测试全绿 |
| [v7] JE-08 Conditionals 序列化 | Java `:373-433` 负责短路/read-back/out-var 收口 | 指认 JM 分支 enforce hits=0 并 retire；短路/异常/作用域行为等价 |
| [v7] JE-09 call-shape | C# 调用形状分支混在 4785 行 Invocation 枢纽 | 登记 JM 分支退休率 100%；纯 BCL 映射不计入目标；CallShapeFacts 单实现 |
| [v7] JE-10 value-copy | 事实“移植一份”且 preprocess 调 Transformers helper | 共享事实实现数 2→1；目标槽位残差 0；Java 不再决定 copy 站点，alias 测试无回归 |
| [v7] 总复杂度与退休 | 只有 Java 行数，无 JM hits/跨层成本 | 每个 VS targeted JM 退休率 100%、旧路径 hits=0；preprocess/compat/Analysis 增量全落账，无同构决策表复制 |

7. **一致性/回归与性能门 [v4 新增，v7 扩契约]**：每条新规则带正/负 fixture + 幂等测试 + 回退测试；`--list-passes` 快照随波次更新；`PassContractConsistencyTests` 扩展断言——每个 GC-ID 至少一个 owner、每个 MayLeave 有 Owners、Deferred 承接者拓扑可达、69 个 fail-fast 点映射 NORM 或 IR 分类。**[v7]** 新增派生断言：每个 guaranteed capability 有最终树证据、每个 JM 有 requiredCapability/状态/owner、manifest hash 可重算、finalizer 有界且拓扑可达、preprocess→Transformers 事实依赖为 0、退休 JM 不得复活。性能门沿用：Yaml 全链耗时 ≤ 现状 1.5×，增量缓存命中率 ≥ 80%（二次运行）；缓存键增加 subsetVersion/inputHash/contractHash。
8. **Java 侧净简化对账 [v6/v7]**：每条 JE/JCS 切片出示六件套——①最终树红→绿 fixture 与 guaranteed capability；②C# B 路差分；③preprocess on/off 的 Java 产物 diff；④指认 JM 分支 enforce 0 hits + 配套删除/收缩 PR；⑤JUnit 数量与结果等价；⑥`javaSimplification` 与总复杂度账本（含 preprocess/compat/Analysis 增量）。**冻结只允许精确到 partial 形状，不能豁免整个文件或整个切片。**每个 VS 独立退场；其 targeted JM 退休率必须 100%，其他未立项 JM 不阻塞。J-01~J-04 既有底册继续跟踪净下降，JE-08~JE-10 按新增行单独验收。
9. **握手与已有树验收 [v7]**：同一预处理后树经正常链和 `--no-preprocess` 转换时，Java 产物与测试结果一致；缺 manifest、错误 subsetVersion、修改任一输入文件/TFM/pass 配置、伪造 guaranteed、缺 finalizer、必需 capability=partial 都有独立负例并以 CS2J1410 失败。default 原始树转换仍需通过兼容测试：未退休兜底没有被误删，已退休构造给出明确的“请先 preprocess”诊断而非 javac 错误。

### 7.2 下游配套验收（垂直切片完成条件，v7 修订）

完整验收 = §7.1 + `convert-project` 两语料 0 失败 + Maven `mvn compile`/JUnit 全绿 + 所在切片的 JM 分支退休。既有协调项继续有效：`Cs2jPrelude.EqualityOps` methodMapping、剩余 `EqualityComparer<T>` 映射、`ClosedMetadataRegistrationGenerator.cs:9-11` 过时注释清理。**[v7]** Java 侧改动分为两类：①纯 Java 职责保留并简化（BCL/compat 映射、命名、import、渲染）；②已 guaranteed 构造的补偿删除/收缩（JsonDeserializeRewrite、CompatCollectionConformance、coercion、Loops/Conditionals、call-shape、value-copy 站点判断、生成物特判）。预处理 PR 不直接承担这些 Java 修改，但**配套 Java PR 与 R14 retire/ratchet 证据是垂直切片的一部分，不再称为“超出方案”**。

---

## 8. 实施波次与风险

### 8.1 波次与垂直切片（v7：先契约，再逐片退休；每片独立进入/退场）

| 波次 | 内容 | 退出标准 | 风险 |
|------|------|---------|------|
| M0 止血 | §6.4-R0 全部（V/S/W 修复）+ fixed-span 边界加固 + **[v3] W-09 compat 漂移修复 + W-10 的 rt10 基线重跑与 Compile 求值最小接线** + **[v5] W-11 两入口统一（含冲突独立诊断码）+ 计数器口径拆分 + GD-MTOK 可见性分级/TypeParameterId owner 身份/holder 并发压测/计划原子提交（R0 v5 项）** + **[v6] S-05 按"过滤/闭包/调用点"三残留细化修；V/S/W 复核订正随修复落盘（CS2J1203 四义迁移、HolderSyntaxGenerator 路径、V-04/V-07/V-08 行号）** + **[v8] V-09 半改写修复（[v8.1] 已实施并验证：探针 3 复跑链越过第 4 pass）与 CS2J0545 记账口径归因**（后者是 rt10 基线重跑的现存阻断项，**先于本表其余项**执行） | 9 个 V 缺陷各有一条红→绿 fixture（V-09 红测原型＝rt10 `JsonElement.DeepEquals`，**v8.1 已绿**：4 用例 + 全量 4552 测试 0 失败）；37×1101→0（json 旧语料口径）；Yaml 库默认链跑通（W-06 的 null-coalesce 保守化）；撞号迁移完成且 `legacyCode` 兼容；**[v3] rt10 真源全链可跑完并产出新基线日志（重跑关注点逐项落档 §3.2.3）——[v8.1] V-09 已关闭第 4 pass 阻断，CS2J0545 归因后越过第 8 pass，§3.2.4 三份探针日志为重跑对照基线**；ToUtf8 漂移探测红→绿；**[v5] W-11 两条命令同报配置错误；并发/先写后读反例红→绿；GD 计划缺失要素时源树逐字节不变** | 中（V-01 修正是行为变更，需同步修 fixture 预期并声明"singleton identity 不再作为兼容行为保留"；[v3] rt10 重跑可能暴露旧语料未见的失败——这正是换源目的，新失败清单进 M1-M3 排程而非回滚换源；**[v8] 该预判已兑现首批：V-09（已修）与 CS2J0545（归因中），后续 pass 的暴露面在越过第 8 pass 后才可见**） |
| M1 / VS-0 契约与审计 | §6.3 **二十六探针** + CS2J1401~1413 + 泛型族挂契约 + **报告 v2** + 残差快照 + JE-06 fail-fast 分类 + **R12 JCS-1/manifest/JavaConversionRequirements + R14 JM 底账与退休协议**（**[v8] 文件级落点与实施顺序见附录 I；与 M0 并行启动**） | 两语料 audit 产出残差守恒式；manifest 正负测试与三入口一致性通过；缺/旧/假握手在 java-ready 下 CS2J1410；每个目标 JM 有 requiredCapability/owner/hits 基线；default 原始树兼容测试全绿 | 中（hash 口径过大导致频繁失效或过小导致假 guaranteed；先用 contract tests 锁定） |
| M2 编译与共享事实 | L1 spine + degraded + 引用画像 + W-04 + Compile/TFM/外部源求值 + R9 源生成物化 + **R13 最终树闭包 + ValueCopy/EvaluationOrder/CallShape 共享事实上移** | 目标语义 pass degraded=0；生成源码恰一份；晚生成构造红→绿；finalizer 失败原子回滚；preprocess→Transformers 事实依赖为 0；每类共享事实恰一实现 | 中-高（编译成本与依赖迁移半径；按 capability 小步迁移，禁止一次改全链顺序） |
| M3 / VS-1 求值顺序 | JE-04 + JE-08；共享 EvaluationOrderFacts；Loops 与 Conditionals 指认 JM 按 R14 迁移 | §7.1 G-36/G-43 全绿；短路/out-var/异常/continue 行为等价；目标 capability guaranteed；指认 JM enforce 0 hits、retire 负 diff、ratchet 防回流 | 高（求值顺序坑密度高；按循环、if/短路、byref/out 分规则开启，失败只回滚对应 capability） |
| M3 / VS-2 struct 值拷贝 | JE-10；ValueCopyFacts 单实现；copy shim 与槽位计划原子生成；StructCloneHelper 站点判断逐步退休 | G-42 与 alias/override/数组/属性/用户 Clone 反例全绿；共享事实实现数 2→1；Java 仅渲染 copy，不再决定站点 | 高（alias 与公共 ABI；不可证计划 DeclaredUnsupported，禁止乐观免拷） |
| M3 / VS-3 调用形状与闭型 | JE-09 + JE-01（按 R3 依赖并行）；JE-07 生成物中立化随 R10 收尾 | G-33/G-39/G-44 全绿；delegate/方法组/可选命名 params 行为等价；指认 JM 退休；**BCL 映射不计入删除收益** | 中-高（调用形状组合多；只前移 C# 决议，避免复制 TypeMappings） |
| M3 / VS-4 集合与转换 | JE-02 先行；JE-03 按 array/list、IEnumerable、Span、IGrouping 分族；每族独立 capability | 每族完成 shadow/enforce/retire 后独立退场；目标 JM hits=0；未覆盖族保持 partial + 冻结原因，不拖住其他族 | 高（开放世界与擦除冲突；禁止一次删除整个 coercion 兜底） |
| M3 / VS-5 泛型纵深 | R1 统一解析器 + R2~R10 未完成项 + JE-05/06 及剩余泛型 G 缺口；Q13 按解决方案证据裁决 | 对应 §7.1 样例/指标逐项绿；ABI pending/unregistered=0；B 路差分通过；**不作为 VS-1~VS-4 的整体前置** | 中-高（迁移面大，按构造族独立交付与退场） |
| M4 域决策 | static abstract / 运行时合成 / Expression / 型变 Unsafe 子集 / 需要整体晚期重排的剩余构造（R13 证据驱动）/ 开放问题 Q1-Q18 / JE-05 BCL 条目化（Q14） | 每域 DeclaredUnsupported（owner/原因/fail-fast）或立项文档；不再把整体重排作为默认答案 | 高（产品决策） |
| M5 性能与文档 | 统一树默认 + 增量 + 选择性 verify + `preprocessor-matrix.md` 自动生成 + 覆盖矩阵 JSON | 整树复制 52→1；链总时长劣化 ≤10%；文档由 descriptor+contract 生成 | 中 |

### 8.2 风险与开放问题（v2~v7 修订）

v1 首版风险 1-7 继续有效（T? 显式化波及面/探针翻转节奏/GD 死角/make-generic-static 程序集边界/双重桥接/Yaml Expression 域/映射协调项——第 7 条已收窄见 §7.2），v2 增补：

8. **V-01 修复是行为变更**：从"每访问一次构造一次"改为"per-closed-type 单例"会改变依赖旧行为的（错误）代码路径；需在 M0 中声明"singleton identity 不作为兼容行为保留"并全量跑 B 路差分。
9. **R1 统一解析器的迁移半径**：五个消费 pass 逐一迁移期间新旧并存，需 per-pass feature flag + 双跑 diff（on/off 各一次，证明变化只在目标形状——参照 C09/C10 翻转惯例）。
10. **晚期链重排 vs 最终闭包 [v7 修订]**：整体重排动 StateMachine/Abi 边界，原地扩展可能漏晚生成代码；先由 R13 最终闭包量化并有界接管，只对无法接管的构造族做 dual-order 实验，不再默认二选一。
11. **公开静态泛型字段是 ABI×erasure 交叉硬点**（kimi 决策点）：删字段破坏反射/API，保留可能不可表达；逐字段四选一（保留+holder/降级/DeclaredUnsupported/重构），不得默认删除。
12. **多 TFM 输出语义**：Yaml 5 TFM——"输出一棵树"要求 csproj 同步特化或按 variant 分目录，不得混用（kimi 决策点 11.5）。
13. **测试工程 vs 生产工程残留策略**：测试工程是行为预言机不是 Java 产物输入；泛型探针在测试工程上的 policy 应显式配置（生产 Fail / 测试默认 PreserveAndWarn 的双 policy），而非散落 adapter。
14. **种子演进纪律**（吸收 seed）：每波绑定语料与量化门；残差只许减不许增；反例 fixture 与规则同生；红→绿留痕（PR 必带残差证据/红绿测名/种子前后统计）。
15. **[v3] 换源冲击管理**：rt10 真源重跑预计产生旧语料没有的失败清单（NRT 泛型实参 452 处对 V-05、`new()` 约束 18 处对 R3、IAsyncEnumerable 13 文件对 async-normalize、csproj 门控对 L1）。**[v8] 该预判已实测兑现首批两项**：V-09（链第 4 pass 中止）与 CS2J0545（第 8 pass 守恒门），归因日志见 §3.2.4；泛型域的暴露面（V-05/R3/async-normalize）在 V-09 修复前**尚未到达**。纪律：新失败**只登记排程，不回滚换源、不在 M0 内顺手扩能力**；旧语料 626/0 只作历史对照。若 rt10 重跑在 M0 内不可达（如 restore 依赖内部源），先落 `tools/PreprocessCorpusProbe` 级别的静态 Compile 求值报告，链重跑顺延至 M0.5 并在 §3.2.3 落档缺口。
16. **[v4] pass 化扩面风险**：四个新条目（generic-constraint/generic-intrinsic-lower/variance-use-site-normalize/generic-reflection-audit）改变 `--list-passes` 快照与拓扑 tie-break。缓解沿用 §6.5 细则（RunAfterIds 只增不减、快照随波次提交、每 pass 正/负/幂等 fixture）；`generic-reflection-audit` 只读可先行合入，其余三 pass 按依赖序（generic-constraint → GI → VR）逐个进入。
17. **[v5] R9/R10 的双生成与登记成本**：①源生成物化与真实 build 各执行一遍生成器，中间树物化文件可能与 build 产物重复/竞争——去重键 `(generator, project, tfm, inputHash)` 必须先于物化合入定型，且 `--no-verify-build` 路径下无 build 对账（物化正确性只靠 inputHash，需独立 fixture）；②R10 ApiManifest 依赖各 pass 改写点主动登记——漏登记即静默（探针 `NORM-PUBLIC-ABI` 的"manifest 外变化"检测需要改写前后符号 diff，成本高于普通探针），首版先覆盖 GD-MTOK/GD-STATIC/V-04/EB/java-keywords 五类高价值改写点，其余渐进。
18. **[v6] JE 族的特有风险**：①**双形态窗口**——预处理改写先行、Java 特例删除在后，窗口期内新旧两条路径并存可能产出二义渲染；纪律：JE-01/02/07 类改写先以 opt-in/影子模式跑双份产物 diff，Java 删除 PR 与翻转同批；②**负 diff 不可教条**——部分 coercion 分支防御的是真实语料未出现的形状（开放世界），I10 允许"冻结保留+到期波次"，禁止为凑行数指标删除防御分支；③**条件线性化的语义坑密度高**（短路运算符/条件中的 ref 赋值/迭代器与异步上下文），JE-04 必须先有求值顺序等价的形式化判据（夹具覆盖 continue/break/异常/嵌套）再动默认链，重蹈 a2d3a6f0 式死循环即回滚；④W-15 表明两阶段联调目前靠人肉——JE 波次若没有 §7.1-8 六件套硬性退场，极可能复制 2aa7aa8f/51e505cd 式的多落点同批救火。
19. **[v7] manifest 假保证与 hash 漂移**：漏把源生成物/support source/pass 配置纳入 inputHash，会让旧树被误认为兼容；把无关时间戳/绝对路径纳入又会造成持续失效。必须用字段级 hash fixture、篡改负例和确定性序列化锁定；guaranteed 只能由最终树派生，manifest 不得成为隐藏语义旁路。
20. **[v7] finalizer 再入与半提交**：finalizer 可能再生成同类构造或与前序 pass 相互触发。R13 限定每 finalizer 每轮一次、声明 Produces/Owners、拓扑执行、计划原子提交；出现无 owner 新构造即 SEMANTIC-CLOSURE 失败，不做无限不动点循环。
21. **[v7] 调用/值拷贝切片语义半径**：JE-09 容易误碰 BCL 映射，JE-10 容易把 alias 当 fresh value。按 JM-ID 精确划界，BCL 映射继续 Java 负责；不可证调用/拷贝保持原形状或 DeclaredUnsupported，不能为退休率强行改写。
22. **[v7] retire 的兼容窗口**：删除旧分支后，default 原始树或 partial 输入会失去该形状的兜底。退休仅限 guaranteed 路径；enforce 前提供单 capability kill switch，default+unverified 在退休前保留旧路径，退休后命中已退休构造必须清晰提示先 preprocess（其他形状照常转换）；配套 revert 必须能同时恢复声明端和调用端。

**开放问题清单 [v4 编号化，源自 deepseek Q1-Q12 经本仓语境改写]**：

| Q | 问题 | 本方案缺省立场 |
|---|------|--------------|
| Q1 | 值语义补偿是否接受 Activator/反射成本（vs 闭型特化 vs DeclaredUnsupported） | 先 token + compat（CN 表），热点语料收集后再闭型特化，C 兜底 |
| Q2 | `Both` 流向是否生成只读 wrapper（新 support 类型）而非 cast | 不生成；VR-05 仅可证安全才改写，其余登记（产品决策留 M4） |
| Q3 | YamlDotNet 专用 profile vs 通用白名单 | JSON 配置 + 通用符号匹配器，Yaml 条目只是首批数据（R3） |
| Q4 | 内在函数允许近似的类型族（int/long/float/double/byte/char？） | 与 compat 维护者对齐后写进 GI 条件列；未对齐前全 `DeclaredUnsupported` |
| Q5 | 是否把 closure-lift 提前到 generic-deepen 之前（37×1101 另一解法） | 不默认提前；先 GD-NEST-2 + R13 最终闭包，只有闭包证明无法接管的构造族才做 dual-order 迁移 |
| Q6 | 是否持久化 compilation 缓存 | 不持久化（Roslyn 不可序列化）；引用集 hash 缓存即可 |
| Q7 | 测试工程泛型残留是否排除出 java-ready | 双 policy：测试默认 PreserveAndWarn、`--strict-tests` 对齐生产（§6.3-6） |
| Q8 | 撞号迁移 `legacyCode` 兼容窗口 | 一个版本（随 M0） |
| Q9 | Java 门禁消息是否改从共享 catalog 读取 | 是，纳入 R12/R14 配套 Java PR：JM-ID、requiredCapability、诊断与退休状态来自同一 JavaConversionRequirements，不再散落字符串 |
| Q10 | java-ready 严格化是否翻转为默认 | CI/验收 profile 先行，稳定后决策 |
| Q11 | static abstract/generic math 是否纳入"任意库"承诺 | 不纳入；AT/SA 规则只保证可判定登记 |
| Q12 | 本方案是否同时交付 Java compat 缺失映射清单 | 先输出 inventory；所在 VS 必需的映射/compat 改动随配套 Java PR 交付，非必要项不借机扩张 |
| Q13 [v5] | 是否落地逐项目 compilation 工作区（kimi 列为 L0 硬规则，本方案列 M3 决策点） | 单项目主路径不受影响；M3 前先量化合并 compilation 在解决方案级语料上的绑定假阴/假阳性规模（variance-audit 126 跳过文件为样本），量化收益小于迁移成本则维持合并 compilation + 文档记录边界 |
| Q14 [v6] | JE-05 是否把 BCL 重载/值语义差异条目化进 compat-method-replacement 注册表 | 保守立项：仅收录有提交证据的高频族（string.Concat 序列展平/checked IO/Decimal 转换），每条带漂移探测；不做通用重载决议重写；M4 按语料增益裁决 |
| Q15 [v6/v7] | JE-03/JE-04 两候选 pass 的排序与默认翻转时机 | JE-04/JE-08 所在 VS-1 先行；JE-03 在 JE-02 后按转换族推进。每族独立完成 R14 后翻 guaranteed，不做整个 pass 一次性默认翻转 |
| Q16 [v7] | manifest 是否对所有转换强制 | java-ready/audit 强制；default 允许原始树并标 unverified，保留未退休兜底；命中已退休构造则要求先 preprocess。已有树只有 inputHash/contractHash 均可重算才可复用 |
| Q17 [v7] | 是否设统一“Java 行数下降百分比”硬指标 | 不设拍脑袋百分比；每个 VS 立项时冻结 targeted JM 集合，退场要求该集合退休率 100%、旧路径 hits=0、无同构事实复制，同时公开各层行数/分支增量 |
| Q18 [v7] | JE-08~JE-10 是否必须等 R1~R10 全部完成 | 不必；只满足各自真实依赖。默认 VS-1→VS-2→VS-3，JE-01 与 JE-07 可按闭型/manifest 依赖并行，VS-5 泛型纵深独立推进 |

---

## 附录 A：诊断码与 NORM 码索引（v2~v7 增补）

**既有泛型相关码**：CS2J11xx（generic-deepen：1100 整树回滚/1101 不可判站点 [v2：含嵌套闭包 token 不可达，json 37 实测]/1102 partial 族不可闭合 [v2：含静态泛型属性/事件停用]/1103 静态字段不可改写/1104 pattern 不可提升/1105 catch 前置失败/1106 调用点在编译外 [v2：调用点闭型不可见 3 实测]/1110 表达式树站点/1120 残留/1130-1131 verify 失败/1190 plan 非自包含）；CS2J1001/1002/1003（Java 侧）；CS2J0585/0586/0587（expression-desugar 新语法守门）；CS2J5175/5116/5119/5198（member-desugar generic math/签名）；UF021/UF040/UF061（泛型指针/stackalloc/extern 拒绝）；URL001；CS2J3301-3305/3320；**[v2 撞号警示，v6 订正为一码四义]** CS2J1203（closed-metadata `ClosedMetadataDiagnostics.cs:55`/extension-methods `ExtensionMethodResolveDiagnostics.cs:75`/extension-block `ExtensionBlockDiagnostics.cs:57`/thread-static `ThreadStaticDiagnostics.cs:49`）与 12xx/13xx 撞段；CS2J5196（"deferred to C02 review" 无承接者）、CS2J5223（开放泛型 with 接收者）、RR070（泛型接口 ripple）；CS2J5420（foreach-source 解构硬门，放大 S-07）；CS2J0601/0604/0607/0608（params 集合谱系）。

**本方案新增**：§6.3 **二十六个 NORM 码**（v6 增 COLLECTION-SHAPE/CONDITION-EFFECT；**v7 增 CALL-SHAPE/VALUE-COPY/SEMANTIC-CLOSURE**，CONDITION-EFFECT 扩 byref/out）+ CS2J14xx 诊断段（**1401~1413**；1410 manifest/握手、1411 call-shape、1412 value-copy、1413 semantic-closure）+ `generic-constraint` 专属 GC01xx + `SYNTH-*` 内部构造命名空间 + CMR0xx compat 漂移码。GI→1407、VR→1404、reflection→1406、JE-02→1408；profile×disable 冲突迁入独立码；JE-06 不新增诊断码。两候选 pass 名保持 `condition-effect-linearize`/`collection-shape-normalize`，JE-08~JE-10 复用既有 pass + 最终 finalizer，不新增默认 mega-pass。

## 附录 B：证据索引（v2~v8 增补）

| 证据 | 位置 |
|------|------|
| **[v8] rt10 实测首跑（两探针 + Yaml 复测）** | 日志：`D:\cs\rt10-probe-v8.log`（默认链，止于 local-function-lift：`28 transformed, 59 lifted, 0 skipped, 2 residual` → `[Error] 2 residual local function(s)`）、`D:\cs\rt10-probe2-v8.log`（+`--no-local-function-strict`，止于 expression-desugar CS2J0545：96/88 守恒分裂）、`D:\cs\yaml-probe-v8.log`（W-06 复现：`1 residual ??/??=` → Error）；报告：`D:\cs\rt10-probe-v8\preprocess-report.json`（schema v1，success=false，totalSeconds 9.395）；半改写现场：`D:\cs\rt10-probe-v8\.cs2j-preprocessed-src\src\System\Text\Json\Document\JsonElement.ArrayEnumerator.cs:375,411,1742,1745,1759,1793`（源形态 `D:\runtime-10.0.12\...\Document\JsonElement.cs:1358,1404`） |
| **[v8.1] V-09 修复与探针 3 复跑** | 修复落点：`Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs`（`VisitSwitchSection` 移除通道 + `UnremovablePlans`/`FindRemovableStatementContainer` 原子性防护）、`LocalFunctionLiftPreprocessor.cs`（`UnremovablePlans` 非空 → 整文件回滚 + 带行号 Warning）、`CLI/Preprocessing/Adapters/LocalFunctionLiftPassAdapter.cs`（聚合诊断嵌入位置清单，最多 5 处）；测试：`tests/CSharpToJava.Tests/LocalFunctionLiftTests.cs` 4 个 `SwitchSectionLocalFunction*` 用例（4/4 绿）+ 全量 4552 测试 0 失败（4543 通过/9 跳过，1m53s）；复跑：`D:\cs\rt10-probe3-v8fix.log`（默认链 `local-function-lift: 59 lifted, 0 residual` → 越过 null-coalesce → 止于 CS2J0545 75/68，unpaired 收窄为 `JsonSchemaExporter.cs` 28/22 + `PropertyRefCacheBuilder.cs` 1/0） |
| **[v8] V-09 机制证据** | `Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs:146-153`（移除仅扫 BlockSyntax 直接语句列表）+ `:861-864`（VisitLocalFunctionStatement 保守原样返回）；`Core/LocalFunctionLift/LocalFunctionLiftNaming.cs:31`（camelCase 化——lift 生成名 `nameEquals`）；`CLI/Preprocessing/Adapters/LocalFunctionLiftPassAdapter.cs:87-99`（CountResidualLocalFunctions 逐文件计数但聚合诊断不带位置）、`:135-148`（树级扫描实现）；`CLI/Program.cs:5397-5399`（`--no-local-function-strict` 默认 false＝默认 strict）；`CLI/Preprocessing/PassChainFactory.cs:306-313`（local-function-lift adapter 接收 config.Strict——与泛型族不传 strict 成对照） |
| **[v8] CS2J0545 守恒门** | `CLI/Preprocessing/Adapters/ExpressionDesugarPassAdapter.cs:359-377`（C24 IN-13 additive whole-tree gate，`Balanced: false` → 无条件 Error，无旋钮；姊妹门 CS2J0516 同构）；unpaired 明细见 W-17 |
| **[v8] v7 断言抽查复核** | `Program.cs:239-246`（--no-preprocess Warning 绕过，仍原样）、`PassContract.cs:18-27`（九字段 record）、`LinqChannelPassAdapter.cs:105`（NEGATIVE-CHECK 注释）、`StructValueSemanticsAnalyzer.cs:77`（调用 Transformers.StructCloneHelper）、`ProjectPassRunner.cs:282-312/321-341`（单 compilation 按需绑定）、`StatementTransformer.Conditionals.cs:373-433`（byref 短路/out-var 悬挂收口）、`InvocationExpressionTransformer.cs:790-815/827+`（EqualityComparer 特判/方法组渲染）、`PreprocessReport.cs:10-30`（schema v1 无 capability/hash）——`a1cb6633`→`98b4c539` 零代码提交，全部成立 |
| **[v7] 握手绕过与新增复杂度证据** | `CLI/Program.cs:239-246`（`--no-preprocess` 对 java-ready/audit 仅 Warning 并忽略 profile）；`PreprocessReport.cs:10-30`（现报告只有 run/pass/timing/summary/diagnostics，无 capability/hash）；`Core/Preprocessing/PassContract.cs:18-27`（现有九字段，无 CapabilityIds/Phase/FinalizerFor）；`ProjectPassRunner.cs:282-341`（按需单 compilation 绑定） |
| **[v7] JE-08~JE-10 代码事实** | `StatementTransformer.Conditionals.cs:373-433`（短路/out-var/read-back）；`InvocationExpressionTransformer.cs:790-924`（comparer/方法组/隐式 delegate Invoke）；`StructValueSemantics/ValueCopyFacts.cs:11`（移植自述）与 `StructValueSemanticsAnalyzer.cs:77`（调用 Transformers.StructCloneHelper）；`StructCloneHelper.cs:100-180,390-445`（override family 与用户 Clone/value-copy 区分） |
| **[v6] v5 后提交基线** | `git log --oneline 9ee2fc26..8268251a`（共 15：2 预处理 2aa7aa8f/51e505cd；13 Java/compat：8268251a/a60afc75/bd3966a2/b5adaaea/69b4db22/0dd5258a/0a183e9a/eeafaa17/f271d4ad/b8e29847/48571b2f/e8e69c94/a2d3a6f0/107f0747——复核时点 a60afc75 为前 14 个，8268251a 定稿时新增）；GenericPreprocess/ClosedMetadata/Preprocessing 目录区间内零提交；8268251a＝泛型/非泛型 IEnumerator.Current 擦除碰撞（`getCurrentNonGeneric` 改名 + compat/TypeMappings 桥接） |
| **[v6] V/S/W 逐条复核记录** | V-01 `GenericDeepenHolderGenerator.cs:52-56`（fixture `SampleGenericDeepenTests/RuntimeBehaviorTests.cs:18-27`）；V-02 同文件 `:63-65`；V-03 `GenericDeepenAnalyzer.cs:10-18,284-292`；V-04 `GenericDeepenRewriter.cs:734-745`；V-05 `ClosedTypeKey.cs:70-73`；V-06 collector `:593-599` vs linker `ClosedMetadataConsumptionLinker.cs:129`；V-07 analyzer `:1953-1961` + Java `TypeOperationTransformer.cs:69-70,778-800`；V-08 `:978-1002`（兜底 `:1002`）；S-01 `:1418-1432`；S-02 `:2058-2067`；S-03 `ClosureLift/HolderSyntaxGenerator.cs:70-76`（**非 Lift/ 子目录**）/`DisplayClassSyntaxEmitter.cs:122-128`/`LambdaMethodLifter.cs:516`；S-04 `TupleStructFileContent.cs:44-55`；S-05 `LocalFunctionLiftRewriter.cs:234-289,535,624-662,760-779`；S-06 `ThreadStaticAnalyzer.cs:560-563`；S-07 `ForeachDeconstructionRewriter.cs:383-417`；S-08 `AsyncNormalizeAnalyzer.cs:1044-1121`；W-01 `PassChainFactory.cs:485,515-519`；W-02 `ResidualRegistry.cs:12-24`（10 条）；W-04 `LinqChannelPassAdapter.cs:105`；W-11 `Program.cs:279-287 vs 4183-4189`；W-12 `ClosedMetadataCollector.cs:19-20,120-149`；W-13 linker `:62-95`；W-14 `ProjectPassRunner.cs:282-312`；计数器 `GenericPreprocessor.cs:115-116`；52 pass＝`PassCatalog.cs:56-1030` descriptor 计数 |
| **[v6] J 层机制规模** | `JsonDeserializeRewrite.cs`（293 行，0dd5258a 新增）；`Utilities/CompatCollectionConformance.cs`（732）；`ArgumentTransformer.Coercion.cs`（1098）/`IListCoercion.cs`（266）/`IterableCoercion.cs`（217）/`SpanCoercion.cs`（85）；`InvocationExpressionTransformer.cs`（4785）；`StructCloneHelper.cs`（450）；`Context/JavaNaming.cs`（498）；`StaticMemberTypeParameterHelper.cs`（148）；条件线性化 `StatementTransformer.Loops.cs`（a2d3a6f0，红测 XsdDateTime `while (++start.Value < _length)`）；tuple 特判 `InvocationExpressionTransformer.cs:790-815`；fail-fast 15＝`UnaryExpressionTransformer.cs`(14)+`BinaryExpressionTransformer.cs`(1)，裸 throw 54 散 18 文件（附录 H 底册）；G-11 半修：TypeMappings:380（Comparer）+ `Comparer.java:12`，EqualityComparer 静态类无条目 |
| **[v3] rt10 真源语料与普查** | `D:\runtime-10.0.12\src\libraries\System.Text.Json`（库本体 352 .cs：src 308 + gen 20 + Common 24；tests 333）；§3.2.2 全部计数为双语料同式 grep（rt10 src+gen+Common vs 旧语料 src 去 obj）实测，2026-11-04 |
| **[v3] 旧语料时代错位证据** | `D:\csharp-sdk\json\src` 存在 `Serialization\JsonClassInfo(.Cache).cs`/`ReflectionEmitMemberAccessor.cs`/`Writer\JsonWriterHelper.Transcoding.cs`（3.x 布局）、无 `JsonTypeInfo`；rt10 对应物为 `Serialization\Metadata\JsonTypeInfo.cs` 等现代布局 |
| **[v3] W-09 签名漂移** | 旧签名 `D:\csharp-sdk\json\...\JsonWriterHelper.Transcoding.cs:28`（4 参 byte-span，与 `CompatMethodReplacementRegistry.cs:306-330` 键一致）vs rt10 `JsonWriterHelper.cs:291`（3 参 `(ReadOnlySpan<char>, Span<byte>, out int)`） |
| **[v3] W-10 csproj 门控与外部源** | `D:\runtime-10.0.12\...\src\System.Text.Json.csproj:384`（`Condition="'$(TargetFrameworkIdentifier)' == '.NETCoreApp'"` 收 net8 变体）、`:399`（netstandard 变体）、`:378`（CoreLibSharedDir）、Common 引用组、`:435`（analyzer 型 ProjectReference）；gen csproj 的 `ROSLYN4_x` DefineConstants；`.slnx` 支持已在 `CLI/ProjectPreprocessSource.cs:32,74` |
| **[v3] Utf8JsonReader 真形** | rt10 `Reader\Utf8JsonReader.cs:21`（`public ref partial struct`）、`:23`（`private ReadOnlySpan<byte> _buffer`）、`:71`（`ValueSpan { get; private set; }`）；ref struct 全家 = reader/PropertyNameSet/GenerationState/PropertyHierarchyResolutionState；`JsonReaderState.cs:14` 为 `readonly struct` |
| **[v3] C# 12/14 新特性面** | `JsonSerializerOptions.cs:42/57/72`（`field` × `ref field` 实参 × 静态单例）、`JsonDocumentOptions.cs:86`、`JsonTypeInfo.cs:1050` 与 `JsonSchemaExporter.cs:411`（ref struct 主构造）、`ArgumentState.cs:7-8`（using 别名→ValueTuple）、`JsonTypeInfoResolver.cs:47`（params ReadOnlySpan 公共 API） |
| 52 pass 锁定顺序 | `preprocess --list-passes` 输出（§2.1 全文） |
| json 0 失败终态（**旧语料**，v3 起历史基线） | `D:\cs\20261104-json-final.log` 尾部（626 succeeded / 0 failed） |
| json 全链运行统计（202 anchors/37×1101/3×1106/588 注册 281 残留/704 位点/41 未映射宿主；**旧语料实测**） | `D:\cs\20261104-json.log:520-652, 4564` |
| YamlDotNet 语料与 6 份运行日志（链中止/单 pass 统计） | `D:\YamlDotNet-18.0.0`；`D:\cs\yaml-lib-probe.log`、`yaml-generic-only.log`、`yaml-gd-only.log`、`yaml-closed-only.log`、`yaml-generic-probe.log` |
| V-01/V-02 | `Core/GenericPreprocess/GenericDeepenHolderGenerator.cs:54-65`；`tests/SampleGenericDeepenSolution/.../RuntimeBehaviorTests.cs:18-27` |
| V-03 | `Core/GenericPreprocess/GenericDeepenAnalyzer.cs:10-18, 284-293` |
| V-04 | `Core/GenericPreprocess/GenericDeepenRewriter.cs:729-765` |
| V-05/V-06 | `Core/ClosedMetadata/ClosedTypeKey.cs`；`ClosedMetadataCollector.cs:580-599` |
| V-07 | `Core/GenericPreprocess/GenericDeepenAnalyzer.cs:1953-1960`；`Transformers/Expression/Transformers/TypeOperationTransformer.cs:789` |
| V-08 | `Transformers/Expression/Transformers/TypeOperationTransformer.cs:966-993`；`Core/VarianceAudit/ConstraintAuditor.cs:354-356` |
| S-01~S-08 | `GenericDeepenAnalyzer.cs:1417-1449, 2061-2071`；`Core/ClosureLift/Lift/HolderSyntaxGenerator.cs:70-76`、`DisplayClass/DisplayClassSyntaxEmitter.cs:120-128`、`Lift/LambdaMethodLifter.cs:502-526`；`Core/TupleToStruct/TupleStructFileContent.cs:48-60`；`Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs:256-291, 624-662`；`Core/ThreadStaticRewrite/ThreadStaticAnalyzer.cs`；`Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs:383-417` |
| W-01~W-08 | `CLI/Preprocessing/PassChainFactory.cs`；`Core/Normalization/ResidualRegistry.cs:11-23`；`ClosedMetadataDiagnostics.cs:55`+`ExtensionMethodResolveDiagnostics.cs:75`+`ThreadStaticDiagnostics.cs:49`；`CLI/Preprocessing/Adapters/LinqChannelPassAdapter.cs:105`；`ClosedMetadataCollector.cs:42, 279-288`；`D:\cs\yaml-lib-probe.log`；`compat/TypeHelper.java:1255,1270`+`ClosedTypeMetadataTest.java`+`ClosedMetadataRegistrationGenerator.cs:9-11`；`CLI/ProjectUnsignedTypePreprocessor.cs:81` |
| **[v5] W-11 两入口分裂** | `CLI/Program.cs:272-287`（convert-project：显式 disable 赢）vs `CLI/Program.cs:4183-4189`（preprocess：无条件追加）+ `CLI/Preprocessing/PassPlanResolver.cs:60-65`（enable/disable 冲突 → "cannot be both enabled and disabled"）；kimi 论断（其文档引 `Program.cs:274-286/4180-4207`）经本仓 HEAD `7e575512` 核证成立 |
| **[v5] W-12 源生成未物化** | `Core/ClosedMetadata/ClosedMetadataCollector.cs:19,39-40,129,145-150`（CM-SRCGEN 仅排除+警告）；额外树合成无生成器执行（`CLI/Preprocessing/ProjectPassAdapter.cs`）；rt10 tests 333 .cs 含 JsonSerializerContext 派生、YamlDotNet.Test 引 StaticGenerator |
| **[v5] W-13 guard 词法相邻** | `Core/ClosedMetadata/ClosedMetadataConsumptionLinker.cs:52-95`（消费语句前插入）；`ClosedMetadataResidualScanner.cs:306-377`（仅相邻性/表达式树检查，无执行路径证明） |
| **[v5] W-14 合并 compilation + 无 ABI 清单** | `CLI/Preprocessing/ProjectPassRunner.cs:282-312`（全树单 compilation）；无 `api-manifest.json` 产物（GD-MTOK/GD-STATIC/EB/java-keywords 改写无登记） |
| **[v5] 计数器/事实源** | `Core/GenericPreprocess/GenericPreprocessor.cs:115-116`（"Count ... regardless of whether we rewrite"）；`CLI/Preprocessing/PassCatalog.cs:7`（PassIds）vs `CLI/Preprocessing/PassStubCatalog.cs:17`（PassStubCatalog.Ids），`:68/:89` 混用实例 |
| PassContract 落地 | commit `58257b25`；`Core/Preprocessing/PassContract.cs`、`PassConstructIds.cs`、`PassContractConsistencyTests.cs`；挂载 10 pass（§2.2） |
| 新四 pass | `Core/FixedSpanToArray/`、`Core/CompatMethodReplacement/`、`Core/PointerDiffInline/`、`Core/RefStructSpanFieldMemoryLift/`；commits `da132d15/08b288c1+08b288c1/a1a7bc10/37b69722/9ef6b143` |
| Java 侧 v2 修正 | raw receiver：commit `5879d22e`；静态泛型属性提升：`Transformers/Member/PropertyTransformer.cs:64,83`；42 throw 点（grep `CS2J normalization required`） |
| EqualityComparer 映射断裂（v2 复核仍成立） | `config/TypeMappings.json` 前缀检索 0 命中；compat `CSharpDefaultEqualityComparerGeneric` 存在 |
| json 静态普查 | §3.2（v1 首版数据，grep 计数） |
| 基线审计链 | `.cs2j-java-keywords-audit-baseline`（170/7）→ `.cs2j-stackalloc-lowering-audit`（97/6）→ 当前 0 失败 |
| 泛型转换器固件 | ConversionTests 7 文件（`prompts/generic-preprocess-prompt.md:52`） |
| GD 规则族/holder/残留门 | `Core/GenericPreprocess/`（v1 首版 §4.6 引用集合有效，命名修正见 §4.2） |
| 同行方案四份 | `C:\Users\15097\Desktop\V202611042101\preprocess-upgrade-{deepseek-flash-4.1-1017, kimi3.1-pro, longcat2.5, seed-evolving}.md` |

## 附录 C：本方案不做的事（v2/v7 修订）

- 不改 52 个既有 pass 的外部行为与 CLI 开关语义（M0 缺陷修复除外——V-01 行为变更显式声明；新增规则全部增量或 opt-in）。
- 不把**纯 Java 职责**搬进 preprocess：BCL/compat 映射、Java 擦除命名、import、目标语法发射与 compat 运行时仍归 Java；只有 C# 语义决策与源形状规范化前移。
- 不为单一语料写死特判：M3 后 `Core/` 无库名（grep 守门）；消费面/compat 条目全部走配置。**[v3] 反向纪律：机制不得按旧语料形态固化**——compat 条目/消费面常量/守门规则必须锚定语料版本并可随版本换代（G-25 教训：ToUtf8 条目按 3.x 签名拟合即失效）。
- 不在本方案内承诺 static abstract（G-07）、运行时合成 R2+（G-04c）、Expression 域（G-23）、晚期链重排（R5）的落地——以 NORM 登记 + 域决策文档交付。
- 预处理 PR 不直接修改 Java 侧代码/映射；但每个 JE/JCS 垂直切片必须包含配套 Java 删除/收缩 PR 与退休证据（§7.2/R14），不得把“跨阶段未完成”包装成范围外。
- 不把"C# 中间树可编译"等同"Java 可转换"，也不把"转换成功"等同"测试全绿"——三层各有独立门（I1-I3 / §7.1 / §7.2）。
- **[v6/v7] 不为扩能力而扩能力，也不为删行数而删防御**：JE/JCS 减负规则必须指认 JM-ID 并退休；正确性、通用语法、框架/审计/性能工作不强制寻找 Java 负 diff。开放世界防御分支可对精确 partial 形状冻结保留并到期复审，禁止整文件永久豁免。Java 行数下降但同构决策表复制到 preprocess/compat，仍不算完成。
- **[v7] 不引入旁路 IR 或渲染后修复**：manifest 只承载身份、能力和证据，不承载 Java 渲染指令/SemanticModel/TypeMappings；JCS 所需语义必须体现在可编译 C# 中。禁止新增 rendered-Java 文本手术，禁止用无限不动点或 mega-pass 掩盖 pass owner 不清。

## 附录 D：同行方案采纳对照（v2 新增，v4 增补 deepseek 设计层，v5 增补 kimi 设计层）

> 四份同行方案（`C:\Users\15097\Desktop\V202611042101`）与本方案（v1 首版）同题。v2 的取舍原则：**论断先核证、增量再吸收、冲突以本仓代码为准**。

**已采纳（经核证）**

- deepseek：YamlDotNet 语料实测与"默认链在泛型 pass 前中止"方法论发现（W-06）；STJ 运行统计分母（§3.2）；GAP-01（编译环境 P0）/GAP-05（37×1101）/GAP-15（契约）/GAP-16（链鲁棒性）→ G-17/G-18/G-24；CS2J14xx 诊断段与撞号迁移；CN-09/10 规则与 CN-01/04 设计坑；GD-NEST-2/GD-STATIC-3；RG-0~4 反射分级（经 kimi 版本）。**[v4 增补设计层]**：GNC 构造目录（附录 E GC-01~35，GC-06 静态属性行按本仓核证改写）；CN-01~12 全表与 GD-MTOK-2 触发集（§6.4-R2）；`IClosedTypeConsumerProfile` 三件套与 CM-ENUM-5/6、CM-LINK-2、CM-STRICT、单 pass bind 修复（§6.4-R3，Yaml profile 文件名按实测替换）；VR-01~08/GI-01~11/EB-01~06 与 AS/IT/RR/RW/AT/SA 规则表（§6.4-R4/R7/R8）；插入顺序兼容性细则（§6.5）；三规则族 pass 化决策；指标矩阵/一致性测试明细/报告 schema（§7.0/§7.1-6/7、附录 F）；`--strict-tests` 双 policy（§6.3-6）；开放问题 Q1-Q12（§8.2）；一页纸执行清单（附录 G）。
- kimi：P0 缺陷清单 9 条（V-01/02/04/05/06/08 与 GD-03/CM-03 归并进 V 层）；YamlDotNet NRT 运行时反射链/双 builder/Expression 元数据面（§3.3）；四路差分与 21 类行为反例（§7，v5 增至 16 类核心）；java-ready 严格化 10 条（§6.6 L2，v5 补 ABI diff/两入口统一/源生成物化三条件）；`StaticGenericStatePlan` 规范形；晚期链重排（作为 R5 决策点）；`MsBuildProject` probe 上移与 TFM/符号配置三不一致。**[v5 增补设计层]**（逐项核证后吸收）：两入口 profile×disable 语义分裂（W-11——其论断经本仓 HEAD 核证成立，v4 文档"仍可排除"表述已修正）；源生成器输出物化与 `(generator, project, tfm, inputHash)` 身份/去重（R9/CM-05，NORM-SOURCE-GENERATOR 探针）；消费面 execution kind 五分类与 CM-LINK-3 执行路径校验（R3 扩/CM-03，W-13）；逐项目 compilation 工作区与 `InternalsVisibleTo`/测试生产分离语义（L1 终态/W-14a——但其 L0 硬规则定位降级为 M3 决策点 Q13）；ApiManifest 原符号→生成符号→批准状态登记与 ABI diff 门禁（R10/GD-06，NORM-PUBLIC-ABI 探针）；GD-MTOK 可见性分级（public/protected/interface/override/extern 保留原签名 + companion invoker，GD-06）；TypeParameterId 含 declaration owner 且比较器/record equality 同一语义、FamilySlot 分离（GD-05，强化 V-03 修复）；holder Dictionary 双检独立线程安全证明与并发/异常缓存反例（GD-03 未竟项，R0 v5 项）；GD 计划原子提交事前协议（GD-07/CS2J1190 升级）；`generic` 计数器语义漂移（5.1 论断核证成立）；`PassIds`/`PassStubCatalog.Ids` 事实源分裂（P2 论断核证成立，M5 清理）；fixed-span 四形态差分细目（P0-9/§3.4）；kimi REFLECTION-QUERY/MEMBER-METADATA 探针语义并入 REFLECT 扩域；per-TFM 验收与"换 TFM 必须重新 preprocess"纪律（§9.4 → §7.0）；"profile 名称本身不是完整证明"框架层论断（§5 矩阵结论 5）。
- longcat：G-02c 修正（is/as 二元式不改写，V-07）；closed-metadata D2 已接线（W-07，修正 v1 论断）；VA020-027 审计码已有（W-01 修正 G-06 缺口性质）；GD-ISAS-EXPR 降级形态（§7.2 样例）；params 谱系（G-21）；Expression 三策略分散与 GroupBy 恒冻结（§4.2）；enum 算术 owner/cast 顺序契约（§8.2.14 种子纪律关联）；报告 schema v2 要点。
- seed：R1-R9 运行时信息分类学（§3.1）；合成层约束债 GEN-GAP-05/06/09/10（S 层，含三处实锤）；`SyntheticTypeParameterResolver`（R1）；探针族 12 码与 TOKEN-SITE 作用域式检测、degraded 口径、SYNTH-\* 命名空间（§6.3）；PassContract 三步推广；种子演进方法学（波次门/ratchet/反例 fixture/盲区巡逻/grep 守门，§6.6/§8）；`ClosedTypeConsumerSurfaces.json`（R3）；产物契约冻结（前缀常量集中+键同构）。

**不采纳（经核证证伪或过时）**

- deepseek：`GenericCollectionTypeResolver` 文件名（不存在，编造）；§8.7 示例行号-API 错配；"静态泛型属性无载体"（Java 侧 PropertyTransformer 有提升）；`NormalizationAuditPassAdapter.cs:161-162` 行号错配。
- longcat：开放泛型 tuple"未覆盖"（槽位机制已实现）；"PassContract 建议新增"（`58257b25` 已落地，其提案仅 `VerificationProfile` 字段与 catalog 三消费点仍有增量价值）。
- kimi（v5 降级/部分采纳而非全盘）：`PreprocessWorkspace` 六条硬性规则作 L0 全量前置——本方案按验收路径现实裁剪：单项目主路径（rt10 csproj/Yaml 库 csproj）不依赖逐项目 compilation，解决方案级路径受益但当前本就中止（CS2J2018），故降级为 M3 决策点 Q13 并要求先出量化报告；其 `PassRunResult`/`Atomicity` 契约字段全量设计——本方案以 M3 注册期四校验 + R0 原子提交协议渐进覆盖，不新增契约字段面；其 `RuntimeTypeExpr` 统一类型表达式模型——以 V-03/V-05 修复 + RG 分级 + CM-ENUM 规则吸收语义，不引入平行 IR；其 `generic-reflection-normalize` 改写 pass 立项——本方案维持 `generic-reflection-audit` 只读面 + RG-0~4 分级（v4 已定，避免与 audit-only 边界冲突的改写 pass 提前承诺）。
- kimi/deepseek 基线漂移：两者分别基于 `361c8214`/`060e17ff`/`4bc8ea9e`，pass 数 49/48/48 均已过时（当前 52）；本方案以 HEAD `6b57a210` 重对全部行号，v5 新增核证以 HEAD `7e575512` 复核（W-11/W-12/计数器/事实源四项论断行号均有效）。

**双方互补确认**：v1 首版独有的 P1-R3（泛型 attribute 降级）/P1-R4（EqualityOps）/P1-R5（make-generic-static 闭型物化）/P1-R6（pattern CompareTo+C37）/P1-R8（collection-expr 泛型 Add）/P2-R3（推断实参物化）与 TypeMappings 映射面分析，四份同行方案均无等价具体规则，全部保留。

## 附录 E：泛型构造登记目录（GC-01~GC-35）[v4 新增，吸收 deepseek GNC 目录并经本仓核证改写]

> 编号沿用 deepseek GNC G-01~G-35 加 **GC-** 前缀（一一对应，便于两文档互查），与本文 §5 缺口矩阵 G-xx **无关**。"当前 owner"按本仓 HEAD 现状核证（GC-06 静态属性行依 V 层核证修正 deepseek 的"无载体"论断；GC-22/23 反射行并入 RG-0~4 分级）。本目录是 PassContract v2 挂载（Consumes/MayLeave 用 GC-ID）与 `normalization-audit` 探针登记的机器可读底册；"目标 policy"即 `ResidualPolicy` 三值。**探针微调项**：GC-04/06 需将 `NORM-GENERIC-STATIC-FIELD` 扩域为 `STATIC-MEMBER`（或并列 `STATIC-METHOD`）——随 M1 落地，扩域前后码集快照同步。**[v5 框架域构造不占 GC 编号]**：源生成器依赖（W-12/R9）与公开签名变更（W-14b/R10）是 v5 新增的**框架层**构造域（非单条 C# 泛型语法），探针直接用 `NORM-SOURCE-GENERATOR`/`NORM-PUBLIC-ABI`，不扩 GC-36+，保持与 deepseek 目录的一一对应；其 fixture 分别锚定 rt10 tests 的 JsonSerializerContext 派生与 GD-MTOK public 站点（§7.1-5 G-29/G-32 行）。

| GC-ID | 构造 | Java 门禁/后果 | 当前 owner（现状） | 目标 owner → policy | NORM/诊断 |
|-------|------|---------------|-------------------|--------------------|-----------|
| GC-01 | 泛型类型/方法/局部函数 type parameter 声明 | bounds 映射，可表达 | 各改写 pass | 保留 | — |
| GC-02 | 方法体内 `typeof(T)` | `Class<T>` token | generic-deepen GD-ANCHOR | Fail | TOKEN-SITE |
| GC-03 | 嵌套闭包内 `typeof(T)`/`is T` | token 不可达 | GD-NEST → GD-NEST-2 修 | Fail | TOKEN-SITE |
| GC-04 | 引用类 type param 的静态方法 | 需提升 + 自有 type param | GD-STATIC（GD-STATIC-3 修调用点闭型） | Fail | STATIC-MEMBER/CS2J1106 |
| GC-05 | 引用类 type param 的静态字段 | `FieldTransformer` 硬失败 | GD-FIELD（V-01/02/03 修复后达规范形） | Fail | STATIC-FIELD |
| GC-06 | 静态泛型属性 | **Java 侧 `PropertyTransformer` 已有提升**（deepseek"无载体"不成立）；C# 侧 GD 停用 CS2J1102 | generic-deepen + member-desugar | Fail / PreserveAndWarn | STATIC-MEMBER/CS2J1102 |
| GC-07 | 泛型类型内 `new T[]` | 构造器 token | GD-CTORARR（-2 扩多站点/初始化器） | Fail | CS2J1120 |
| GC-08 | `catch (T)` | `StatementTransformer` 硬失败 | GD-CATCH | Fail | CATCH |
| GC-09 | `is T v` 声明模式（if 上下文） | 仅具体类型 | GD-ISAS-B | Fail | TOKEN-SITE |
| GC-10 | `is T`/`as T` 二元式 | 需 `Class<T>` token | **V-07 两侧均不改写** → G-02c/R2 扩面 | Fail | TOKEN-SITE |
| GC-11 | `default(T)`（值语义 T） | Java null 兜底（V-08） | generic（部分）→ generic-constraint CN-01/02 | Fail | CONSTRAINT 关联/CS2J1403 |
| GC-12 | `new T()` | Activator 无 token 退化 | generic + CN-03 + GD-MTOK-2 | Fail | CS2J1403 |
| GC-13 | `T?`/`Nullable<T>` | Optional 映射混用 | CN-04/05（连带 `.Value`/构造改写） | Fail（struct 约束）/保留（引用） | CONSTRAINT |
| GC-14 | `new T[n]`/`new T[,]` | `Array.CreateInstance` + token | generic（一维）+ CN-07；多维 CN-08 | Fail / DeclaredUnsupported | ARRAY |
| GC-15 | 值语义 T 装箱/拆箱 | 包装类型差异 | CN-06 | Fail / DeclaredUnsupported | CONSTRAINT |
| GC-16 | `where T : struct/unmanaged/Enum/Delegate` | CS2J1002 丢弃 | variance-audit 审计 + CN 改写 | Fail（有依赖站点）/ PreserveAndWarn | CONSTRAINT |
| GC-17 | `where T : new()` | 无构造约束 | variance-audit + CN-03 | 同上 | CONSTRAINT |
| GC-18 | `where T : notnull/class/default` | 编译期可丢弃 | CN-12 | PreserveAndWarn | CONSTRAINT |
| GC-19 | `out/in` 声明型变 | CS2J1003 丢弃 | variance-audit | DeclaredUnsupported（C# 无通配符语法，靠使用点改写） | VARIANCE-DECL |
| GC-20 | 构造泛型引用转换使用点 | Java invariant 编译失败 | 无 → variance-use-site-normalize（VR） | Fail | VARIANCE-USE |
| GC-21 | 数组型变 `T[]` → `U[]` | Java 数组协变多数可编译 | variance-audit 逐站点验证 | PreserveAndWarn | VARIANCE-USE |
| GC-22 | `MakeGeneric*`/`GetGeneric*` 反射 | 擦除不可重建实参 | closed-metadata + RG-0~4 + generic-reflection-audit | Fail / DeclaredUnsupported | REFLECT |
| GC-23 | 闭型注册/消费者 link | `closedCacheKey` | closed-metadata（profile 化 + CM-LINK-2） | Fail | REFLECT/CS2J1220 |
| GC-24 | `Unsafe.*`/`MemoryMarshal.*`/`sizeof(T)` | 无内存重解释/布局保证 | unsafe-ref-lowering（窄）→ generic-intrinsic-lower（GI） | Fail / DeclaredUnsupported | UNSAFE-INTRINSIC |
| GC-25 | 擦除同名同签名冲突 | Java 同擦除 | JavaNaming `_erasure_N` 已接 + EB-01/03/04 | Fail（bridge 或登记） | ERASURE-COVARIANCE |
| GC-26 | 泛型接口槽位歧义 | CS2J5197 跳过 | member-desugar EB-02 | Fail / DeclaredUnsupported | ERASURE-COVARIANCE |
| GC-27 | 泛型 record `with`/clone | CS2J5223 跳过 | record-readonly/member-desugar EB-05/06 | Fail / DeclaredUnsupported | ERASURE-COVARIANCE |
| GC-28 | 泛型 async/iterator 状态机 | type params/约束重声明 | async-normalize/iterator-lowering（S-08/IT-01） | Fail | CS2J1844 → owner 化 |
| GC-29 | 泛型 attribute | Java annotation 不支持 | attribute-normalize AT-01 | DeclaredUnsupported | ATTRIBUTE |
| GC-30 | 接口 static abstract/generic math | Java 无 static abstract | member-desugar SA-01 | DeclaredUnsupported | STATIC-ABSTRACT |
| GC-31 | 泛型 delegate/匿名方法 | functional interface（丢型变） | delegate/closure-lift + VR-07 | PreserveAndWarn + 使用点改写 | VARIANCE-USE |
| GC-32 | 泛型匿名类型 | 生成泛型类 + 约束 | anonymous-type-lift（已携带约束） | Fail | — |
| GC-33 | 开放泛型 tuple | 生成泛型 struct | tuple（S-04 约束债修复） | Fail | DECONSTRUCT 关联 |
| GC-34 | 泛型 enum 约束/类型实参 | enum-like 不满足 `Enum` 约束 | enum-declaration Infeasible | PreserveAndWarn（保留真 enum） | ENUM-OP 关联 |
| GC-35 | type param 名撞 Java 关键字 | 编译失败 | java-keywords | Fail | — |

一致性断言素材（§7.1-7）：每个 GC-ID 至少一个 owner；GC-ID × NORM 码对应表入库 `PassConstructIds`；**[v6] Java 侧 fail-fast 点按附录 H 实测 69 点底册（15 经统一构造点 + 54 裸 throw）逐个映射到 GC-ID 或标注 IR-不支持（JE-06 分类）**（人工映射表 + 测试防漂移）。

## 附录 F：报告与握手 schema（v7 / report v2 / manifest v1）

`preprocess-report.json` 升版为 **schemaVersion=2**，保留 per-pass 构造计数并引用 JCS manifest、JM 退休与复杂度账本：

```json
{
  "schemaVersion": 2,
  "profile": "java-ready",
  "manifest": "preprocess-manifest.json",
  "subsetStatus": "verified",
  "chain": ["conditional", "...", "normalization-audit"],
  "summary": { "files": 261, "residuals": 0, "errors": 0, "warnings": 0 },
  "passes": [
    {
      "id": "generic-deepen",
      "status": "ok",
      "filesScanned": 261,
      "filesChanged": 27,
      "constructs": {
        "GC-02": { "rewritten": 100, "residual": 0 },
        "GC-04": { "rewritten": 1, "residual": 0 }
      },
      "diagnostics": { "CS2J1401": 0, "CS2J1402": 0 }
    }
  ],
  "generic": {
    "constructs": {
      "GC-11": { "fail": 0, "preserve": 0, "unsupported": 0 },
      "GC-20": { "fail": 0, "preserve": 0, "unsupported": 2 }
    },
    "owners": { "generic-deepen": { "filesChanged": 27, "residuals": 0 } }
  }
}
```

**[v7] `preprocess-manifest.json`（R12 握手产物；示例只展示必需形状，hash 为占位）**：

```json
{
  "schemaVersion": 1,
  "subsetVersion": "JCS-1",
  "profile": "java-ready",
  "solutionVariant": {
    "configuration": "Release",
    "projects": [{ "path": "YamlDotNet/YamlDotNet.csproj", "tfm": "net10.0" }]
  },
  "passPlan": [{ "id": "struct-value-semantics", "contractHash": "sha256:…" }],
  "contractHash": "sha256:…",
  "inputHash": "sha256:…",
  "capabilities": [
    {
      "id": "JCS.VALUE",
      "status": "guaranteed",
      "constructs": ["struct-value-copy-site"],
      "finalizers": ["struct-value-semantics/finalize"],
      "residuals": [],
      "evidenceHash": "sha256:…"
    },
    {
      "id": "JCS.CONVERSION",
      "status": "partial",
      "constructs": ["span-to-enumerable"],
      "finalizers": [],
      "residuals": [{ "file": "…", "owner": "collection-shape-normalize", "reason": "open-world" }],
      "evidenceHash": "sha256:…"
    }
  ],
  "artifacts": {
    "apiManifest": "api-manifest.json",
    "residualReport": "preprocess-residual-report.json",
    "complexityLedger": "preprocess-report.json#javaSimplification"
  }
}
```

> `guaranteed` 由最终树证据派生；示例中的 partial capability 若被 JavaConversionRequirements 标记为当前 profile 必需，则整个 java-ready 转换必须以 CS2J1410 拒绝，而不是自动降级为 Warning。

`preprocess-residual-report.json` 的 `residuals[]` 条目六字段（code/file/line/owner/policy/reason）：

```json
{
  "code": "NORM-GENERIC-VARIANCE-USE",
  "file": "YamlDotNet/Serialization/NodeDeserializers/CollectionNodeDeserializer.cs",
  "line": 0,
  "owner": "variance-use-site-normalize",
  "policy": "DeclaredUnsupported",
  "reason": "value-type type argument"
}
```

`generic-reflection-audit` 独立产物 `generic-reflection-report.json`（行号为占位，以实测报告为准——deepseek 原稿示例把 `MakeGenericType` 错配到 `TypeConverterCache.cs:39`，该行实为 `?? []` 构造）：

```json
{
  "schemaVersion": 1,
  "sites": [
    {
      "file": "YamlDotNet/Serialization/Utilities/TypeConverterCache.cs",
      "line": 0,
      "api": "MakeGenericType",
      "receiverType": "System.Collections.Generic.List`1",
      "typeArguments": ["T"],
      "closed": false,
      "policy": "DeclaredUnsupported",
      "reason": "runtime-bound type parameter T"
    }
  ]
}
```

> 报告无时间戳、字段排序确定（§7.1-1 幂等验收依赖）；`configuration`/TFM/引用集 hash 记入报告头（L2 严格化要求）；两份报告互链（generic 段 ↔ residuals 的 owner/filesChanged 对账）。

**[v5] `api-manifest.json`（R10 产物，随报告出库）与报告头增补字段**：

```json
{
  "schemaVersion": 1,
  "entries": [
    {
      "original": "YamlDotNet.Serialization.TypeConverterBuilding…/Serialize<T>",
      "generated": "…/Serialize<T>(T, JsonSerializerOptions, System.Type)",
      "changeType": "tail-param-inject",
      "visibility": "public",
      "callSites": { "source": 12, "reflectionReachable": false },
      "approval": "pending"
    }
  ],
  "summary": { "total": 0, "pending": 0, "unregistered": 0 }
}
```

`preprocess-report.json` 头部增 `sourceGenerators` 段（R9：每工程生成器名/inputHash/物化文件数/去重状态）与 `profileEntryPoints` 段（W-11：两入口统一后的 profile 解析记录）；`abiChanges` 统计引用 `api-manifest.json` 的 summary——java-ready 下 `pending`/`unregistered` 非 0 即失败（§6.6 L2）。

**[v7] `javaSimplification` 与总复杂度账本（R14；JM-ID 取代单纯行号对账）**：

```json
{
  "javaSimplification": {
    "schemaVersion": 2,
    "rules": [
      {
        "rule": "JE-08",
        "slice": "VS-1",
        "capability": "JCS.EVAL",
        "targetMechanismIds": ["JM-EVAL-IF-BYREF"],
        "targetMechanism": "Transformers/Statement/StatementTransformer.Conditionals.cs:373-433",
        "retirementState": "retire",
        "javaLinesDelta": -60,
        "preprocessLinesDelta": 180,
        "compatLinesDelta": 0,
        "analysisLinesDelta": 95,
        "javaDiffCommit": "<配套下游 PR commit>",
        "fallbackHits": { "before": 17, "duringEnforce": 0 },
        "sharedFactImplementations": { "before": 2, "after": 1 },
        "productDiff": { "corpus": "agl", "changedFiles": 12, "expectedShapeOnly": true },
        "junit": { "testsBefore": 4102, "testsAfter": 4102, "failedBefore": 0, "failedAfter": 0 },
        "freeze": null
      }
    ],
    "summary": {
      "targetedJmCount": 1,
      "retiredJmCount": 1,
      "rulesWithDebt": 0,
      "totalJavaLinesDelta": -60,
      "duplicateSemanticFactImplementations": 0
    }
  }
}
```

> `retirementState` 使用 R14 的 `inventory/shadow/enforce/retire/ratchet`；`frozen` 只作为 partial 能力的注记，`freeze` 必须含 JM-ID、uncoveredConstruct、reason、owner、reviewWave。行数只是账本一维，不能替代 targeted JM 退休率、fallback hits、共享事实重复数和行为等价。`rulesWithDebt>0`、目标 JM 未退休或 shared fact 仍重复时，所在 VS 不得退场。示例数字仅展示 schema，不是新增实测结果。

## 附录 G：一页纸执行清单（v7；v8 增补）

1. **M0 正确性止血**：完成当前切片依赖的 V/S/W 修复与 rt10 基线；不把全部泛型 backlog 当成 Java 瘦身的总前置。**[v8] 最先做 V-09 修复 + CS2J0545 归因**（rt10 基线的两个阻断项，§3.2.4；红测原型 `JsonElement.DeepEquals`）；探针日志 `rt10-probe-v8.log`/`rt10-probe2-v8.log` 为重跑对照基线。
2. **VS-0 先立契约**：冻结 JCS-1 后条件与职责分界；落地二十六探针、CS2J1401~1413、report v2、`preprocess-manifest.json`、JavaConversionRequirements、JM-ID 与 R14 状态机；`--no-preprocess` 正负握手先通过。**[v8] 按附录 I 五步实施（ID 事实源 → contract 增量字段 → manifest/hash/入口校验 → 闭包扫描 → JM 对账），与 M0 并行。**
3. **M2 建可信工作区**：真实 Compile/TFM/引用/源生成输入 + degraded=0；R13 最终闭包有界执行、原子回滚；ValueCopy/EvaluationOrder/CallShape 事实唯一化，禁止 preprocess→Transformers 依赖。
4. **VS-1 求值顺序**：JE-04/JE-08 分规则 shadow；短路、out-var、异常、continue/break 全绿后 enforce；Loops/Conditionals 指认 JM 0 hits 再 retire，最后 ratchet。
5. **VS-2/VS-3 值拷贝与调用**：JE-10 先消除双事实再收槽位；JE-09 只规范化 C# 调用形状，JE-01/JE-07 按依赖并行；BCL 映射/Java 命名不搬入 preprocess。
6. **VS-4/VS-5 分族推进**：JE-02 先行，JE-03 按 array/list、IEnumerable、Span、IGrouping 独立 guaranteed→retire；泛型 R1~R10 与其余 JE 作为 VS-5 按真实依赖推进，未覆盖形状保持 partial。
7. **M4/M5 收尾**：域决策只产出立项或 DeclaredUnsupported；整体链重排须由 R13 证据触发；统一树/增量缓存键包含 subsetVersion/inputHash/contractHash，文档由 descriptor+contract 派生。
8. **每片同一退出门**：正/负/幂等/回退 fixture + 双语料 B 路 + Java 产物 diff + JUnit 数量/结果等价 + guaranteed manifest + 指认 JM retire + 总复杂度账本 + 防回流测试。每次只交付一个可回滚 VS；冻结精确到形状且有 owner/reviewWave；**“预处理扩了、Java 没瘦”或“Java 瘦了、决策复制到别的层”都不得退场**。

## 附录 H：Java 侧复杂度普查与 JM 退休底册（v7 扩展）

> I10/JE 规则的指认底册与 JE-06 分类工作单。行数为 v6 在 2026-11-06 的物理行数快照，v7 新增行只作代码定位、未重测全仓行数；退休主键改为稳定 JM-ID。fail-fast 的 P/I/D 分类继续有效：P=预处理责任、I=IR 不支持、D=防御保留。

**H.1 机制级底册（J 层）**

| 机制 | 位置（行） | 行数 | 近期发作证据 | JE 规则 | 目标 |
|------|-----------|-----:|--------------|---------|------|
| 渲染后文本重建闭型 | `Transformers/JsonDeserializeRewrite.cs` | 293 | 0dd5258a 新增；f271d4ad | JE-01 | 整文件删除 |
| 集合擦除一致性后处理 | `Transformers/Utilities/CompatCollectionConformance.cs` | 732 | b5adaaea/+170；8268251a（Current 碰撞） | JE-02 | 整文件删除 |
| 实参强制/适配 | `Transformers/Expression/Transformers/ArgumentTransformer.Coercion.cs` | 1098 | f271d4ad 等 | JE-03 | 分支收缩 |
| IList 适配 | `Transformers/Expression/Utilities/IListCoercion.cs` | 266 | b5adaaea/f271d4ad | JE-03 | 分支收缩 |
| Iterable 适配 | `Transformers/Expression/Utilities/IterableCoercion.cs` | 217 | 0a183e9a/69b4db22 | JE-03 | 分支收缩 |
| Span 适配 | `Transformers/Expression/Utilities/SpanCoercion.cs` | 85 | — | JE-03 | 分支收缩 |
| 条件副作用线性化 | `Transformers/Statement/StatementTransformer.Loops.cs`（while 段） | 分散 | a2d3a6f0（死循环） | JE-04 | 删除重写 |
| [v7] byref/out 条件序列与 out-var 收口 | `Transformers/Statement/StatementTransformer.Conditionals.cs:373-433` | 分散 | 当前代码实存短路/read-back/悬挂 post 处理 | JE-08 | 指认 JM 分支退休 |
| BCL 重载/值语义特判 | Invocation/TypeOperation/Assignment/StructCloneHelper 等分散位点 | 分散 | a60afc75/e8e69c94/f271d4ad | JE-05 | 条目收编 |
| 生成物逐名特判 | `InvocationExpressionTransformer.cs:790-815`（EqualityComparer→Objects） | 26 | 2aa7aa8f（生成形状漂移） | JE-07 | 删除+manifest |
| fail-fast 统一点 | `UnaryExpressionTransformer.cs`×14、`BinaryExpressionTransformer.cs`×1 | 15 点 | — | — | 已归一 |
| fail-fast 裸 throw | 18 文件 54 点（Lambda 6/ElementAccess 4/ObjectCreation 3/Unary 3/ControlFlow 2/Assignment 1/Binary 1/Invocation 1/Literal 1/Query 1/String 1/TypeOperation 1；Statement.SwitchAndResource 12/Declarations 7/Loops 5；Class 2/Struct 1/Field 2） | 54 点 | — | JE-06 | P/I/D 全分类 |
| 调用渲染枢纽 | `InvocationExpressionTransformer.cs` | 4785 | v5 后 +394；方法组/隐式 delegate Invoke 实存 | JE-01/03/07 +[v7] JE-09 | 只退休 C# 形状 JM；纯 BCL 映射保留，不以整文件行数作目标 |
| struct clone/值语义 | `Transformers/StructCloneHelper.cs` + preprocess `ValueCopyFacts` 双实现 | 450+ | `ValueCopyFacts.cs:11` 移植自述；`StructValueSemanticsAnalyzer.cs:77` 反向调用 | [v7] JE-10 | 共享事实 2→1；copy 站点判断退休，窄渲染工具可保留 |
| 擦除命名模型 | `Context/JavaNaming.cs` | 498 | 持续 | 不吸收（Java 侧本质职责；EB 规则减少其触发面） | 观测 |
| 静态成员提升 | `Transformers/Utilities/StaticMemberTypeParameterHelper.cs` | 148 | — | 不吸收（GD-STATIC 完全覆盖后可评估收缩） | 观测 |

**H.2 使用纪律 [v7]**：①每个 VS 立项时冻结 targeted JM 子集，退场只要求该子集退休率 100%，不以整文件/全仓百分比代替；②每波重测行数、语义分支数与 hits，连同 preprocess/compat/Analysis 增量写入 `javaSimplification`；③“观测”行不是承诺，纯 Java 命名/映射/渲染不得为凑指标前移；④新进入 Transformers 的 C# 形状补偿必须分配 JM-ID、requiredCapability 与 owner，或显式声明纯 Java 职责，否则 CI 失败。

**H.3 JM-ID 对照（v7 种子 + v8 扩容至 VS-1~VS-4 全量；实施时入 JavaConversionRequirements；行号仅初始定位）**

| JM-ID | 初始位置 | capability / 规则 | 退休验收 |
|---------|----------|-------------------|----------|
| `JM-EVAL-WHILE-EFFECT` | `StatementTransformer.Loops.cs` while 条件线性化（`:28-37` while(true) 重写，规范形注释自证） | JCS.EVAL / JE-04 | 目标循环形状 enforce 0 hits 后删除 |
| `JM-EVAL-GETTER-PRE-STATEMENT` | `StatementTransformer.Loops.cs:19-21,106`（循环头读改写预语句排布）+ `StatementTransformer.ExpressionAndReturn.cs` getter pre-statement 泄漏位（回归测试 `GetterPreStatementLeakTests` 对应分支） | JCS.EVAL / JE-04 | 条件位副作用在 C# 侧线性化后，预语句排布分支删除 |
| `JM-EVAL-IF-BYREF` | `StatementTransformer.Conditionals.cs:373-433` | JCS.EVAL / JE-08 | 短路/out-var/read-back 反例全绿，指认分支删除 |
| `JM-CALL-METHOD-GROUP` | `InvocationExpressionTransformer.cs:827+`（`TryRenderMethodGroupReference`：bare method-group 实参 → Java 方法引用，注释自证 "post-preprocessing leaves the symbol unresolved"） | JCS.CALL / JE-09 | 已选方法组形状在 C# 定型后删除对应分支 |
| `JM-CALL-DELEGATE-INVOKE` | `InvocationExpressionTransformer.cs:924+` | JCS.CALL / JE-09 | 显式 Invoke 后不再由 Java 猜隐式调用 |
| `JM-VALUE-COPY-SITE` | `StructCloneHelper.cs`（方法族：`IsUserDefinedStruct:20`/`IsCloneableTemporary:52`/`IsPropertyAccess:76`/`IsRefParamEffectivelyReadOnly:100,111`/`IsValueParamEffectivelyReadOnly:194`/`ValueCopyCall:402`/`BuildCloneExpression:421`/`IsThisInsideOwnCloneMethod:456`/`CloneStructValueIfNeeded:470`）及 Argument/Assignment/Return/Statement 调用点 | JCS.VALUE / JE-10 | Java 站点判断删除；共享 ValueCopyFacts 单实现 |
| `JM-GENERATED-EQUALITY-COMPARER` | `InvocationExpressionTransformer.cs:790-815`（`TryRenderEqualityComparerCall`：按 `EqualityComparer<T>` OriginalDefinition 名逐名识别 tuple 生成物调用 → `Objects.equals/hashCode`） | JCS.SYMBOL / JE-07 | 生成物中立 helper 后删除逐名特判 |
| `JM-JSON-DESERIALIZE-TEXT-REWRITE` [v8] | `JsonDeserializeRewrite.cs` 全方法族（`:36` 前缀常量 `JsonConvert.deserializeObject(`/`TryRewriteTypedJsonDeserialize:30`/`RetypeAssignmentValue:71`/`IsUncastableTarget:108`/`JsonDeserializeListElement:118`/`JsonDeserializeClassTarget:142`/`TryWrapGenericJsonDeserializeCall:182`/`MethodBodyDeserializesTypeParameter:230`/`FindMatchingParenthesis:258`/`FirstTopLevelArgument:288`） | JCS.GENERIC（闭型）/ JE-01 | C# 侧槽位闭型物化 + GD token 后整文件删除（293 行渲染后文本手术） |
| `JM-COLLECTION-CONFORMANCE` [v8] | `Utilities/CompatCollectionConformance.cs` 手术方法族（`ErasureKey:201`/`NormalizeAdd:217`/`NormalizeObjectPredicate:244`/`NormalizeRemoveAt:259`/`UniqueMethodName:376`/`RenameAdapterDelegation:388`/`NormalizeIterator:417`/`EnsureSyncMembers:435`/`EnsureRemoveBridge:463`/`EraseParameter:512`/`WrapIteratorReturn:767`） | JCS.CONVERSION / JE-02 | C# 侧声明端桥接一次成型后整文件删除（732 行签名后处理） |
| `JM-COERCION-COLLECTION-BRIDGE` [v8] | `ArgumentTransformer.Coercion.cs` 方法族（`CoerceIterableSlotType:196`/`CoerceArgumentType:367`/`CoerceNullArgument:634`/`CoerceLambdaArgument:722`/`CoerceIntegralToEnum:790`/`CoerceArrayToCollection:801`/`TryCoerceMultiEnumerableDictionary:895`/`TryBuildIterableBridge:931`/`CoerceEnumerableStreamFallback:1212`）+ `IListCoercion.cs`（266 行）+ `IterableCoercion.cs`（217 行）+ `SpanCoercion.cs`（85 行） | JCS.CONVERSION / JE-03 | 按形状族（array/list、IEnumerable、Span、IGrouping）分族 retired；已 guaranteed 族分支零命中 |
| `JM-CALL-RAW-RECEIVER-RENDER` [v8] | `InvocationExpressionTransformer.cs` 泛型接收者 raw 渲染残余（`5879d22e` 引入的 StripTrailingGenericArguments 类特判——**已部分修复，登记用于验证不再回流**） | JCS.CALL / JE-09（观测） | C# 侧静态泛型访问显式化后，特判降为纯渲染 |

> JM-ID 一旦发布不得随重构改名；位置可更新，语义变化则新建 ID 并保留退休历史。v7 首批 6 个种子，**v8 扩容至 12 个**：VS-1~VS-4 立项所需的目标机制已全部登记（全部按当前 HEAD 实测行号）；`JM-CALL-RAW-RECEIVER-RENDER` 为"已修复防回流"型登记（enforce 阶段直接进入 ratchet 巡逻）。其余新进入 Transformers 的补偿照 H.2-④ 纪律随时补号。

## 附录 I：VS-0 实施蓝图（v8 新增——R12/R13/R14 的文件级落点）

> 目的：v7 定义了协议（R12 握手/R13 闭包/R14 退休），本附录回答"开工改哪些文件、什么接口、什么测试"。VS-0 是 M1 波次的交付物，可与 M0 并行启动（I.6 边界）。

### I.1 新增文件与关键接口

**`Core/Analysis/`（共享事实，R13/I12）**——迁移/新建三类事实，输入只依赖 Roslyn abstractions，preprocess 与转换器共同消费；依赖方向守门测试同步建立（preprocess 命名空间 → `Core/Transformers` 引用为 0）：

```csharp
// 由 StructValueSemantics/ValueCopyFacts.cs 上移唯一实现（G-42：先消灭双实现再删 Java 分支）
public static class ValueCopyFacts {
    public static bool NeedsValueCopy(ITypeSymbol? type, CopySlot slot);   // slot: Assignee/Argument/Return/FieldRead/ArrayElement/Operand
    public static string? NoCopyReason(ITypeSymbol? type, CopySlot slot);  // FreshValue/Immutable/NoAlias——免拷必须带机器可读理由
}
public static class EvaluationOrderFacts {
    public static bool HasConditionalSideEffect(ExpressionSyntax expr, SemanticModel model); // JE-04/08 共用：属性/索引器读改写、byref/out 实参、悬挂读回
    public static IReadOnlyList<StatementSyntax> Linearize(ExpressionSyntax condition, SemanticModel model); // 规范形预语句 + while(true)/嵌套 if 保持懒求值
}
public static class CallShapeFacts {
    public static CallShape Resolve(InvocationExpressionSyntax invocation, SemanticModel model); // ExplicitInvoke/MethodGroup/ExtensionStatic/NamedOptional/ParamsExpanded/InferredGeneric
}
```

**`Core/Preprocessing/Jcs/`（新增目录）**：

| 文件 | 职责 | 关键点 |
|------|------|--------|
| `JcsCapability.cs` | `enum JcsCapabilityId { Workspace, Symbol, Call, Eval, Value, Generic, Conversion, Closure }` + `enum JcsCapabilityStatus { Guaranteed, Partial, Unsupported }` + `record JcsCapability(id, status, constructs[], finalizers[], residuals[], evidenceHash)` | 三态语义按 §0.1.1；`Guaranteed` 只能由扫描器派生（构造函数私有，`JcsCapabilityScanner` 是唯一工厂——禁止 adapter 手写） |
| `PreprocessManifest.cs` | `record PreprocessManifest(schemaVersion, subsetVersion, profile, solutionVariant, passPlan[], contractHash, inputHash, capabilities[], artifacts)` | 与附录 F manifest v1 逐字段对齐；确定性序列化（字段排序固定、无时间戳） |
| `ManifestInputHash.cs` | 消费者 hash：最终 Compile 源文件内容 + 项目图/TFM/条件符号 + 解析后引用集 + 物化生成文件 + support source；**排除时间戳与机器相关绝对路径** | `--no-preprocess` 只依赖已有树 + 项目资产即可重算；与 R9 的 `(generator, project, tfm, inputHash)` 同一 hash 基元 |
| `PassContractHash.cs` | 排序 pass ID + 启用配置 + 规范化 `PassContract` → hash | 先统一 `PassCatalog`/`PassStubCatalog` ID 事实源再启用 |
| `SemanticClosureScanner.cs` | 最终树构造枚举 → GC-ID/能力映射 → `FinalizerFor` pass 按拓扑序各有界执行一次 → 未接管构造计 `NORM-SEMANTIC-CLOSURE` | 复用 `ProjectPassRunner.BuildStandardCompilation` 的 parse/compile spine，不另写审计编译器；finalizer 新生成构造必须登记后续 owner 否则失败 |
| `JmRegistry.cs` | `record JmEntry(jmId, initialLocation, requiredCapability, rule, retirementState, hits)`；`JavaConversionRequirements` 的 preprocess 侧镜像与对账输入 | 数据源＝附录 H.3 的 12 行 + 后续补号；行号可漂移，ID 稳定 |

**`CLI/Preprocessing/`（接线）**：

| 落点 | 改动 |
|------|------|
| `PreprocessManifestEmitter.cs`（新增） | 挂在 `NormalizationAuditPassAdapter` 之后（链尾、java-keywords 之后）：审计成功才写 `guaranteed`；`--preprocess-only` 同样落盘；写失败或树扫描失败 → 整链失败 |
| `Program.cs:239-246`（替换） | `--no-preprocess` 分支：java-ready/audit 下**读取并校验** manifest（缺失/`subsetVersion` 不符/`inputHash` 重算不一致/必需 capability 非 guaranteed → CS2J1410 退出 1）；default 原始树 → `subsetStatus=unverified` 继续转换；已退休 JM 构造命中 → CS2J1410 提示先 preprocess |
| `Program.cs:1441` 附近（preprocess 收尾） | 链成功后调用 Emitter；报告 schema v2 增 `manifest`/`subsetStatus` 头字段 |

### I.2 `PassContract` 增量字段（向后兼容小步）

```csharp
// PassContract.cs 追加三个可选字段（默认空/无——既有 10 个挂载 pass 零改动通过编译）
IReadOnlyList<string> CapabilityIds = [];   // 本 pass 声明支撑的 JCS 能力
PassPhase Phase = PassPhase.Rewrite;         // Rewrite | Finalize
string? FinalizerFor = null;                 // 声明"我是某能力/构造族的有界 finalizer"（RunAfter 全部 Produces owner）
```

`PassContractConsistencyTests` 同步三条派生断言：`Phase=Finalize` 必须有 `FinalizerFor` 且其 owner 链在自身之前；每 NORM 码恰一 owner；`CapabilityIds` 里的能力必须在 `JcsCapabilityId` 枚举内。

### I.3 转换器侧 `JavaConversionRequirements`（Q9 落点）

静态单一事实源（代码或受审 JSON），三张表：①profile → 所需 JCS 能力集（java-ready：八族全查；default：无硬性）；②JM-ID → requiredCapability + retirement state（数据与 `JmRegistry` 对账，漂移即 CI 失败）；③诊断码 CS2J1410/1411/1412/1413 的消息模板（从共享 catalog 读取，不再散落字符串）。启动转换时**先校验握手、再构造 ConversionContext**——不得在转换中途才发现握手不符（R12-4）。

### I.4 测试清单（§7.1-9 的 fixture 化）

| 测试 | 正/负例 |
|------|--------|
| Manifest contract tests | 正：同一树正常链/`--preprocess-only`/`--no-preprocess` 三入口同结论、同 `inputHash`；负：删除 manifest、篡改 capability、改动任一源文件/TFM/pass 配置、伪造 guaranteed、缺 finalizer、必需能力=partial → CS2J1410 + 退出 1 |
| 闭包与 finalizer | "早期 pass 消除、晚期 pass 再生成"构造（G-41 样例）被有界 finalizer 接管；finalizer 失败 → 树与 manifest 逐字节回滚（无"声明已改、调用点未改、manifest 仍 guaranteed"中间态） |
| Hash 口径 | 字段级 fixture：源生成物/support source/pass 配置纳入 hash；时间戳/绝对路径不入 hash；改一个字节必失效 |
| JM 对账 | `JmRegistry` × `JavaConversionRequirements` 双向 diff 为空；新 JM 无 owner/无 capability 即失败；已 retire JM 复活即失败 |
| 依赖方向 | `Core/*Preprocess*` → `Core/Transformers` 引用为 0（架构测试，grep 守门可后补） |

### I.5 实施顺序（VS-0 内部五步，每步独立可合入）

1. **ID 事实源统一**：`PassCatalog`/`PassStubCatalog` 双源并一（§2.2 v5 债，顺手清）；
2. **PassContract 增量字段** + 一致性测试扩断言（不接线任何运行时行为）；
3. **manifest + hash + 入口校验**：先 default=unverified 全放行、java-ready 硬校验同批灰度（负例先行）；
4. **SemanticClosureScanner + finalizer 框架**：首版只做"枚举→映射→登记 SEMANTIC-CLOSURE 残差"，finalizer 执行框架空转（为 VS-1/VS-2 的 `condition-effect`/`value-copy` finalizer 预留）；
5. **JmRegistry + 对账门**：H.3 的 12 行数据入库，双向 diff 进 CI。

### I.6 与 M0 的关系及边界

- **并行启动**：步骤 1-3、5 不依赖 M0 修复；步骤 4 的最终树扫描在 rt10 上的**真实语料验证**依赖 V-09/CS2J0545 修复（否则扫描在半改写树上运行，结论无效）——先以 Yaml（链止于 null-coalesce，但可用 `--pass` 窄链样例）与 rt10 探针树做开发夹具。
- **边界**：manifest 不承载任何"某表达式应渲染成什么 Java"的指令（附录 C/R12-6）；不改 52 pass 外部行为；`--no-preprocess` 在 default profile 下的兼容行为保持（未退休 JM 兜底启用），只收紧 java-ready。
- **验收锚点**：§7.1-9 全部 fixture 绿 + 三入口一致性 + H.3 对账门进 CI。
