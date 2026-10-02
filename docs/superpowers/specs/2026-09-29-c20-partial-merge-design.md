# C20 partial 类型/方法合并前置 设计文档

> **第三次校订（2026-10-02，定稿）**：dev-3.x 已从 `2d25700e` 经 `e5e7773a`（全部已实现 pass 默认开启收口）、`b919fbf4`（C34 decimal-ops 默认开启）前进至 **`0a5372ae`**（C28 lexical-normalize 翻转 Implemented 完成）。按 HEAD 重新核对全部证据，改动枚举：
> 1. 全部 `file:///` 链接行号按 HEAD 重算（关键变更：TypeGroupResolver L209/L212、MergedTypeDeclaration L63/L75-83/L131-132/L143-179、ClassTransformer L62、ConversionPipeline L294、PartialTypeGroup L34-46、GenericDeepen L191-197、InterfaceRippler L491-496、DeclarationRewriter L136-150、RecordReadOnlyAnalyzer L332/L354、MethodMigrator L109-112/L202-204、ProjectPassAdapter L268-269、ProjectPassRunner L136-155/L159-253、TuplePassAdapter L59-88/L122-127、CliPreprocessChain L101/L228、JavaKeywordsPassAdapter L48/L97/L179/L200、PassCatalog L319-330/L332-340/L341-352/L368-386/L395-396、PassChainFactory L149/L334/L382）。
> 2. catalog 锚点更新：C20 条目实际在 [preprocessor-catalog.md#L549-L561](file:///D:/code/cs2j/prompts/preprocessor-catalog.md#L549-L561)（原 L544-L556）；链位建议 L1068（原 L1063）；C46「须晚于 C20」在 L923（原 L918）；路线图第 12 项在 L1035（原 L1030）；partial 属性交叉登记在 L1007（原 L1002）；catalog L561 的「实现可选」括注歧义**至今未订正**（见 §13）。
> 3. **stub 真值（定稿口径）**：Stubs 列表实有 **14** 条（partial-merge 在 [PassStubCatalog.cs#L51-L61](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L51-L61)）；lexical-normalize 已由 C28 翻转为 Implemented（[PassCatalog.cs#L50-L62](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L50-L62)，默认关）。[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) 的硬编码计数断言（22）**已被删除**，改为 `BuildRegistry().WhereStatus(Stub)` 动态查询——原 §13 开放问题 1 以此方式自动消解；C20 翻转后 stubs 预期 **13** 条。
> 4. **执行纪律变更（用户 2026-10-02 强制约束）**：在 `dev-3.x` 分支**原地执行、禁止 worktree**。§12 已重写；§11 的在飞 worktree 段落按实况更新（现存 11 个 worktree 全部属于已合入通道的残留，无 C20 协调冲突）。
> 5. **工作区在途改动提示**：本文行号锚定 HEAD `0a5372ae`。定稿时点工作区仅有一处非本任务改动——`tests/CSharpToJava.Tests/LexicalNormalizeTests.cs`（C28 后续修改）与未跟踪的 `prompts/preprocessor-completion-audit-and-order.md`；实施时逐文件 `git add`，二者不纳入本特性提交（归属规则见实施计划 §0）。
> 6. PartialMethodMerger 死代码结论经 HEAD 复核不变：`ProcessPartialMethods` 全仓仍零调用点（方法定义 L27）。
>
> **校订（2026-09-29）**：本稿完成后 dev-3.x 已从 `953293b9` 前进至 `2d25700e`（C14 member/operator-desugar 合入），本次按 HEAD 重新核对全部证据，改动枚举：
> 1. 全部 `file:///` 链接行号按当前源码重算修正（PartialTypeMerger、TypeGroupResolver、ProjectPassRunner、PassStubCatalog、PassCatalog、CliPreprocessChain、Program.cs、TuplePassAdapter、JavaKeywordsPassAdapter、PassDescriptor、ConversionPipeline、GenericDeepenAnalyzer、DeclarationRewriter、PartialTypeGroup 等）。
> 2. catalog 引用全部重新锚定：C20 条目实际在 [preprocessor-catalog.md#L544-L556](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L544-L556)（原误引 L496-L506）；链位建议在 L1063（原写 885）；§4 风险 11 在 L1109（原写 930）；C46「须晚于 C20」在 L918、路线图第 12 项在 L1030（原写 904）。
> 3. stub 真值更正：partial-merge 条目在 [PassStubCatalog.cs#L59-L69](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L59-L69)（原写 L61-L71）；当前 Stubs 列表实有 **21** 条，而 [PassStubCatalogTests.cs#L24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24) 仍断言 **22**（26ea367c 翻转 member-desugar 后计数断言未同步，静态证据不一致；本次禁跑测试，红绿无法证实）；C20 翻转后 Stubs 应为 **20** 条。
> 4. §4.5/D4 补入 catalog 六次补全规则：经典 vs C#9 扩展 partial 方法双轨、partial 构造器（初始化器限制/主构造器互斥）、partial 事件（字段式定义 vs add/remove 实现）、partial 属性/索引器（C#13）。
> 5. 显式讨论 `PassStage.Lexical` 枚举注释自称「pure syntax, no semantic model」与本设计语义合并的表面矛盾（见 D3、§3.2，结论：注释是意图非强制，`NeedsSemanticModel` 必须翻 true）。
> 6. 更正主部件选择规则证据：[InterfaceRippler.cs#L491-L496](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/InterfaceRippler.cs#L491-L496) 实为「完整路径 OrdinalIgnoreCase 序 → span.Start 最小」**两键**，不存在「span 长度最短」第三键（D1 原表述无源码依据，已删改）。
> 7. PartialMethodMerger 经复核**不区分两代 partial 方法形态**：主流程仅按有无方法体分组、无实现即丢弃（[PartialMethodMerger.cs#L56-L100](file:///d:/code/cs2j/src/CSharpToJava.Core/PartialType/PartialMethodMerger.cs#L56-L100)）；`IsPartialDefinition` 辅助方法存在但 `ProcessPartialMethods` 未调用（L142-L157）；全仓零调用点结论不变。
> 8. §12 base/worktree 现状更新；新增 §13 校订遗留开放问题。

- **范围**：把跨文件的 `partial class/struct/interface/record` 多部件与 partial 方法（含 C#13 扩展 partial 成员）在词法链位（E01 之后、一切语义改写之前）合并为「每个类型在源码树中只剩一份声明」的中间 C# 树；新增 Core `PartialMerge/` + CLI 单适配器，翻转注册表既有 stub `partial-merge`。**本设计不删除转换器侧现有 PartialType 机制**（退役是 M3/M4 的独立动作）。
- **状态**：设计稿（2026-10-02 第三次校订定稿，锚点 HEAD `0a5372ae`）；stub 已在注册表（C20，P3，架构型候选）；实施原地在 `dev-3.x` 进行，禁止 worktree。
- **族属**：[preprocessor-catalog.md](file:///D:/code/cs2j/prompts/preprocessor-catalog.md#L549-L561) C20（分类 1.7）；链位建议见同文件 §3.2「与现有链的依赖/顺序总览」（L1068：`E01 → [C43+C28] → [C20 若做，放这里] → E02`）。

---

## 1. 背景与目标

### 1.1 现状证据：合并发生在主转换期（原稿 2026-09-29 复核于 base `953293b9`；二次校订按 `2d25700e`；**本节 2026-10-02 第三次校订按 HEAD `e5e7773a` 重锚**）

- 合并器是**纯符号驱动**的：[PartialTypeMerger.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/PartialType/PartialTypeMerger.cs) 从 `CSharpCompilation.GlobalNamespace`（L32）遍历符号，按 `INamedTypeSymbol.DeclaringSyntaxReferences.Length > 1` 识别多部件（L150）、按符号全显示名分组（L241-244）、递归嵌套类型（L97-137），只处理 `Locations.IsInSource` 的符号（L53；嵌套 L108）。
- 管线入口有两处：转换阶段的 `ProjectPartialTypeNormalizationPass`（[ProjectPasses.cs:545-555](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L545-L555)）与前置 pass 状态构建（[ProjectPasses.cs:282-283](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L282-L283)）；真正合成合并节点在 [TypeGroupResolver.cs:209-258](file:///D:/code/cs2j/src/CSharpToJava.Core/Pipeline/TypeGroupResolver.cs#L209-L258)：`MergedTypeDeclaration.FromPartialTypeGroup`（L209）→ `context.RegisterMergedPartialType`（L212）→ 按 `TypeKind`（class/record/struct/interface）分派四个 `TransformMerged`（L224/228/234/253/258）。
- 合并产物是**内存中的合成语法节点，不落 C# 中间树**：[MergedTypeDeclaration.cs:63-128](file:///d:/code/cs2j/src/CSharpToJava.Core/PartialType/MergedTypeDeclaration.cs#L63-L128) 拼接各部件成员（L75-83）、按**语法文本键去重（首个胜出）**、只在合成节点上摘掉 `partial` 修饰符（L131-132）；[PartialTypeMergeStore.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/PartialTypeMergeStore.cs#L10) 以「文件路径+span」登记已合并节点；普通转换入口对已合并节点返回 `MergedTypePlaceholder`（[ClassTransformer.cs:62](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L62)），占位符在发射前被过滤（[TypeGroupResolver.cs:790-795](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/TypeGroupResolver.cs#L790-L795)）。
- 下游语义 pass 各自重新面对多部件语法：`GenericDeepenAnalyzer` 对无实现体的 partial 方法放弃 token 锚点（[GenericDeepenAnalyzer.cs:191-197](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenAnalyzer.cs#L191-L197)）；RecordReadOnly 族多处特判（`PartialImplementationPart/PartialDefinitionPart`，[RecordReadOnlyAnalyzer.cs:332-354](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs#L332-L354)、[MethodMigrator.cs:109-112](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/MethodMigrator.cs#L109-L112)、[L202-204](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/MethodMigrator.cs#L202-L204)），`InterfaceRippler` 还自定了「完整文件路径 OrdinalIgnoreCase 序、再 span.Start 最小」的主部件选择规则（[InterfaceRippler.cs:491-496](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/InterfaceRippler.cs#L491-L496)）。
- 测试资产真实存在：[PartialStructMergeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PartialStructMergeTests.cs)（双文件 partial struct 成员曾被静默丢弃的回归；走内存 `ProjectConversionPipeline.ConvertProjectAsync`）、[PartialInterfaceTupleMappingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PartialInterfaceTupleMappingTests.cs)（双文件 partial interface 的 Tuple→`Map.Entry` 映射，L51 **直连** `ConversionPipeline.ConvertProjectWithPartialMergeAsync`，方法签名 [ConversionPipeline.cs:294](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L294)）。
- **关键考古事实**：[PartialMethodMerger.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/PartialType/PartialMethodMerger.cs) 全仓**零调用点**（2026-10-02 HEAD 复核：仅命中自身的方法定义 L27），是死代码；且其实现**不区分两代 partial 方法**——`ProcessPartialMethods`（L27）只按签名文本/符号键分组后查「有无方法体」（yield L51/81/99），无实现一律丢弃（L87-L95），类内虽有 `IsPartialDefinition`（L144）/`PartialImplementationPart`（L152-L156）静态辅助但主流程从不调用。当前 partial 方法的实际行为 = 成员文本键去重的首个胜出 + RecordReadOnly 的符号配对特判。新 `PartialMerge/MethodMerger` 是重新设计，不是移植该文件。

### 1.2 前置的收益

1. 全部下游 C#→C# pass 与主转换器只面对「一个类型一份声明」：可退役 `PartialType/`、`PartialTypeMergeStore`、placeholder 过滤及各 pass 的多部件特判，删掉一整类架构分叉。
2. 修掉现状隐患：成员去重靠语法文本键（`int` 与 `System.Int32` 不等价、首个胜出可能选中无实现体的 partial 定义）、基类/特性/修饰符只取自主节点（转换器靠符号二次兜底）。
3. partial 方法两代形态（含 C#13 partial 构造器/属性/索引器/事件）第一次有显式、可测、带诊断的合并语义，而不是死代码 + 隐式去重。
4. 为 catalog §5.4 统一引擎（「一次 parse 合批」）提供单声明树前提。

### 1.3 ★最大设计矛盾：不可逆的合并产物 vs 逐文件/整树双范式

preprocessor-foundation 计划锁定的现状脊柱（[preprocessor-foundation_plan.md:60-139](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L60-L139)）有一组硬不变量：

- **一文件一产物**：runner 先把整树 1:1 复制进中间目录，再按 `SourceFiles` 逐文件改写（[ProjectPassRunner.cs:79-132](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L79-L132)、L159-245）；现有通道只会「改文件」或 tuple 式「加文件」（`AfterFilesAsync` 里 `DistributeStructFiles` 生成新文件，[TuplePassAdapter.cs:122-127](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs#L122-L127)），**没有任何通道删除输入文件**。
- **整树 plan 回滚 = 按文件字节恢复**：JavaKeywords 每轮从原始副本重放（`maxIterations = 1024`，JavaKeywordsPassAdapter.cs:97-99），失败 plan 只回滚其 `TouchedFiles`（JK031），无法归属则整轮回滚（JK030，[JavaKeywordsPassAdapter.cs:172-221](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L172-L221)）。回滚的原子单位天然是「plan × 文件集」。
- **指纹/保留目录按 pass 串联**：链上以上一 pass 中间目录为输入（每环收尾 `sourcePath = run.PreprocessedSourcePath`，[CliPreprocessChain.cs:228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L228)），指纹根默认指向原始输入（Program.cs:152-153、L246-247），中间目录名经描述符自动登记；快照建立在「文件集合稳定、内容哈希变化」之上（catalog §4 风险 11，[L1114](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1114)）。

partial 合并若做成 **N 个输入部件 → 1 个物理输出文件**：文件边界消失，①文件数变化使 1:1 脊柱、删除/新建归属、空文件定义全部失配；②指纹的文件集合身份改变；③回滚必须「恢复被删文件 + 删除新建文件」，且一个文件可含多个类型、部件间是多对多关系，无法以单文件为原子；④成员的源码出处（调试可追溯性）被抹掉；⑤source generator 对 partial 部件的增补可能被直接破坏（CS0260）。

**本设计的核心结论（详见 D1）**：采用**模型 B——主部件承载合并声明、其余部件仅摘除该类型节点（文件全部保留）**。它把「N 部件 → 1 份声明」的语义收益与「文件集合不变」的范式不变量同时保住；「N 文件 → 1 文件」的物理合并（模型 A）否决，仅列为未来独立显式 opt-in。

### 1.4 目标

1. 启用后中间树满足不变量：**任一源生类型在树中恰好有一份类型声明节点**（`INamedTypeSymbol` 的源内 `DeclaringSyntaxReferences` 数为 1；合并声明**保留 `partial` 关键字**，理由见 D7）。
2. 合并在整树语义编译上完成，冲突显式诊断、失败按类型组原子回滚，不产生任何注释/占位静默透传（catalog §4 风险 12）。
3. 默认关、可独立 opt-in；禁用时行为与今天**字节级一致**；启用时转换器侧既有合并路径自然休眠（单部件 partial 走普通路径）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据 / 备注 |
|---|---|---|---|
| D1 | ★合并产物**文件所有权模型** | **模型 B：主部件文件原地替换为合并声明，其余部件文件只摘除该类型节点；文件一个不删**。确定性主部件 = 相对路径（相对源根）序数升序 → 同文件按节点 span.Start 最小（两键已唯一确定，不再设第三键）。该规则复用 [InterfaceRippler.cs:491-496](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/InterfaceRippler.cs#L491-L496) 已验证形态，仅有两处有意偏差：先例用**完整**路径 + `OrdinalIgnoreCase`，本 pass 改用相对路径 + 序数比较以保证跨根/跨机确定性（大小写口径见 §13 开放问题）。被摘除位置留单行溯源注释 `// cs2j-partial-merge: part of '<全名>' merged into '<相对路径>'`；文件头版权 trivia、using、同文件其他类型原样保留；空 namespace 合法 | 否决**模型 A（物理单文件输出）**：无删除先例、多类型文件/嵌套类型导致多对多归属、指纹文件集失配、回滚要重建已删文件、generator 增补被破坏。否决**模型 C（保留部件边界仅补全）**：下游仍面对多部件，不达成目标。模型 B 下文件集合恒定，标准文件枚举、指纹、按文件字节回滚全部沿用；模型 A 列为未来 `--physical-consolidation` 独立 opt-in，不在本特性 |
| D2 | 合并产物是否保留 `partial` 修饰符 | **保留**（与现状 `MergedTypeDeclaration` 在合成节点上摘关键字的做法相反）。合并声明上 `partial` 是有意保留的 C# 语义，不是残余 | source generator 在 `dotnet build` 中间树验证时仍会向该类型追加生成部件（WPF/WinForms、System.Text.Json、MVVM Toolkit 等高频场景）；摘掉即 CS0260。residual 闸门的定义因此是「**多部件声明数 = 0**」，不是「partial 令牌 = 0」；摘令牌永远是 C#→Java 发射期的事 |
| D3 | ★`needsSemanticModel` 复核结论 | **stub 当前的 `false` 翻转为 `true`**；`PassStage.Lexical` 保留不变 | 位置由 RunAfter 决定、Stage 仅信息标签，见 [PassDescriptor.cs:3-7](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L3-L7)（「execution order today is determined solely by RunAfterIds」）与 Stage 属性 L113-114；runner 是否建编译只看 `NeedsSemanticModel`（[ProjectPassRunner.cs:164-170](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L164-L170)）。**表面矛盾已核实并给结论**：`PassStage.Lexical` 的枚举注释自称「pure syntax, no semantic model」（PassDescriptor.cs:10），stub 描述也写 "before the semantic passes"——这是**意图标签不是强制约束**：描述符构造器默认 `needsSemanticModel: true`（L76），Stage 与该字段互不校验，枚举注释无需随本特性修改（注释语义在统一引擎落地时再统一收口，见 §13）。现状合并器 100% 符号化即证据：跨文件签名同一性（using alias、global using、extern alias、元组元素名、可空注解、ref 种类、泛型 arity）无法靠文本键安全配对——现状 `GetMemberKey` 的 `int/System.Int32` 文本错配就是反例（[MergedTypeDeclaration.cs:143-179](file:///d:/code/cs2j/src/CSharpToJava.Core/PartialType/MergedTypeDeclaration.cs#L143-L179)）。纯语法可行点与必须语义点见 §4.4 |
| D4 | partial 方法两代形态与 C#13 partial 成员策略 | **①经典 declaration-only（C#3 形态：无访问修饰符、隐式 private、`void`、无 out、无 virtual/override/sealed/new/extern）**：实现可选，无实现时编译器整体抹除调用与声明，且可被 generator 实现——**v1 原样保留**（仍是无体 `partial` 声明）；**②扩展 partial（C#9+：有访问修饰符/非 void/out/其他修饰符，实现为强制，缺实现即基线编译错误）**：v1 把定义与实现**作为两个成员保留在同一合并声明内**；**③C#13 partial 构造器/属性/索引器/事件**：同样按「定义性声明 + 实现性声明」双份保留，边界规则（初始化器限制、主构造器互斥、事件 add/remove）见 §4.5。M3 才做配对塌缩（见 §4.5） | 符号判据 `IMethodSymbol.IsPartialDefinition/PartialImplementationPart/PartialDefinitionPart` 已在现网使用（RecordReadOnly 族：[RecordReadOnlyAnalyzer.cs:332-354](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs#L332-L354)、MethodMigrator L202-204）。v1 不删除任何「在手写编译中看不见实现」的成员——这是 generator 安全红线；现状死代码 `PartialMethodMerger` 不分代、「无实现即丢弃」（L87-L95）的逻辑不沿用。catalog L561 把「实现**可选**」括在 C#9 扩展形态后，按官方语义实现可选的是①经典形态、②扩展形态实现强制；该括注有歧义，catalog 不在本次可改范围，记 §13 |
| D5 | 修饰符/基类/特性冲突校验 | **修饰符合并**：各部件 token 并集去重 + 规范化顺序（访问性至多一处；abstract/sealed/static/unsafe/new 按 C# 规则并存；`readonly` 必须排在 `partial` 之前，CS1585，见 [DeclarationRewriter.cs:136-150](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs#L136-L150)）；**基类合并**：至多一个非 object 基类（跨部件不一致即 CS0261 类基线错误），接口集合并并按符号去重（`IEnumerable<int>` 与全名写法规一）；**特性合并**：各部件 AttributeList 全部保留拼接（C# 本就跨部件合并特性）；**类型参数约束**跨部件并集；冲突由「基线诊断快照 + 合并后 recompile-diff」捕获，无法归属即整组不合并 | 合法 C# 代码里这些冲突本就是编译错误（CS0261/CS0262/CS0263/CS0264/CS0579…），pass 不与编译器抢诊断（同 C04 D11 基线优先）；CS2J20xx 只在「基线干净、合并后新错」时发，属防御层 |
| D6 | XML 文档注释合并 | 类型级 `<summary>` 取主部件；其余部件的文档块按部件序拼入生成的 `<remarks>` 段，每段带来源相对路径；成员级文档随存活成员走；partial 方法配对文档 v1 两份随成员保留，M3 塌缩时实现文档优先、定义文档并入 `<remarks>`。拼接字节确定、重跑稳定 | 不能静默丢弃任一部件文档；`GenerateJavaDoc` 下游消费文档（[Program.cs:256](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L256)），丢弃即用户可见回归 |
| D7 | source generator 产出的不可见部件 | 手写 compilation 不跑 generator（[ProjectPassRunner.cs:136-155](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L136-L155)），看不到生成部件——**接受该盲区并以三条规则兜底**：①合并声明保留 `partial`（D2）；②不删除「手写树无实现」的经典 partial 方法声明（D4①）；③中间树 `dotnet build` 双验证默认开，generator 在验证时真实运行，任何增补破坏在 Java 转换前硬失败。csproj analyzer 引用仅用于 verbose 信息统计（ProjectCompileSet 可复用），不作为行为判据 | 运行 `CSharpGeneratorDriver` 成本高且需 MSBuild 引用解析，不进 v1；verify build 是现成且更强的语义 oracle |
| D8 | record/struct/interface 特例 | 五种声明节点统一按 `TypeDeclarationSyntax` 处理（`RecordDeclarationSyntax` 在当前 Roslyn 即其派生类，`WithMembers/WithModifiers` 可用）；`record class`/`record struct`/`ref partial struct` 位置 record 参数表只允许出现在一个部件（基线 CS 错误兜底）；嵌套 partial 类型递归合并；interface 成员（含 DIM）按部件序拼接；top-level statements 合成的 Program（`CompilationUnitSyntax` 部件，见 [PartialTypeGroup.cs:34-46](file:///d:/code/cs2j/src/CSharpToJava.Core/PartialType/PartialTypeGroup.cs#L34-L46)）跳过不处理 | 转换器分派已按 `TypeKind.IsRecord` 覆盖（[TypeGroupResolver.cs:216-262](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/TypeGroupResolver.cs#L216-L262)），本 pass 只产出标准 C#，不引入分派变化 |
| D9 | plan 形态与回滚粒度 | **plan 原子单位 = 一个类型组**（触达 1 个主文件 + N-1 个摘除文件）；自定义 `ExecuteAsync`（仿 JavaKeywords）：复制整树 → 基线错误快照 → 整树分析 → 按文件改写 → 候选编译 recompile-diff → 新错唯一归属某组则回滚该组全部触达文件（CS2J2001，Warning）并重放，无法唯一归属则整轮回滚（CS2J2002，Error）；带最大轮次上限 | 标准 spine 不提供「改完再编译、跨多文件原子回滚」钩子（[ProjectPassAdapter.cs:268-269](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L268-L269)），tuple 的 AfterFiles 只能加文件不能回滚；回滚后该组保持多部件 → 转换器现有路径承接（并存期合法） |
| D10 | 指纹与保留目录 | 模型 B 文件集合恒定 → **指纹模型零改动**：不重指指纹根（`RepointsFingerprintToPassInput=false`）；启用只产生正常内容哈希变化与 `pass:partial-merge=on/off` 令牌一次性失效；中间目录 `.cs2j-partial-merge-src` 经 `ExecutedIntermediateDirectoryNames` 自动登记（[CliPreprocessChain.cs:101](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L101)）。骨架/溯源注释字节必须确定，禁止在文件里写时间戳/GUID | catalog §4 风险 11 的接入要求由注册表机制天然满足，同 C04 §5 结论 |
| D11 | 默认开关 | 翻转 Implemented 后**长期默认关（opt-in）**；默认开启的唯一合理时机是统一引擎（catalog §5.4）以单声明树为前提落地时，并以 §8 门槛全过为条件 | P3 架构简化项、现路径成熟；默认开会让全部项目白付一次整树编译并整体失效缓存，收益却要等退役/统一引擎才兑现 |
| D12 | 与转换器现有 PartialType 的并存/退役 | **并存不互斥**：启用且组成功合并 → 转换器看到的每个类型 `DeclaringSyntaxReferences` 数为 1，`PartialTypeMerger/Store/placeholder` 路径休眠但不删；回滚组、跳过组仍走旧路径。转换器侧退役单列 M3 之后的独立 PR（以 sdk 残余多部件 = 0 为前提），直连入口 `ConvertProjectWithPartialMergeAsync` 与两个 partial 测试在退役前保留 | 同一 PR 又前置又退役会让风险半径翻倍；并存期用 residual 统计与测试锁死「不重复合并」 |

---

## 3. 注册表与链位契约

### 3.1 现状（stub，已核实）

[PassStubCatalog.cs:51-61](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L51-L61)（逐字段：id L52、DisplayName "Partial type merge" L53、目录 `.cs2j-partial-merge-src` L54、`PassStage.Lexical` L55、`PassStatus.Stub` L56、`defaultEnabled: false` L57、`runAfterIds: ["conditional"]` L58、**`needsSemanticModel: false`** L59、description L60、catalogItems `C20` L61）。禁用时不进计划、无目录无耗时（[PassPlanResolver.cs:67-90](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassPlanResolver.cs#L67-L90) 按全量 `registry.Ordered` 做启用子集过滤）；显式启用在触碰文件系统前报 CS2J9001（[CliPreprocessChain.cs:179-184](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L179-L184)，路径有测试锁定）。

**与 lexical-normalize 的相对顺序（2026-10-02 复核，状态已变）**：lexical-normalize **已由 C28 翻转为 Implemented**（[PassCatalog.cs:50-62](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L50-L62)，`defaultEnabled: false` L56、RunAfter `{conditional}` L57、`needsSemanticModel: false` L58）；partial-merge 仍是 stub（PassStubCatalog L51-61），只锚 `conditional`。全量注册表 = `Implemented.Concat(Stubs)`（[PassCatalog.cs:395-396](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L395-L396)），拓扑排序按声明序兜底——lexical-normalize 声明在 Implemented 表前部、partial-merge 在 Stubs 表首位，二者之间已无歧义地 lexical-normalize 在前。翻转时 partial-merge 在 Implemented 表插入 lexical-normalize 条目之后，并把锚改为 `{conditional, lexical-normalize}` 把先后显式锁死；两 pass 同为默认关，启用序经「全量排序→按启用过滤」得出（PassPlanResolver L67-90），不裁剪 RunAfter 边。

### 3.2 翻转契约

| 字段 | 翻转后值 | 说明 |
|---|---|---|
| Status | Implemented | — |
| DefaultEnabled | **false（长期）** | 见 §8 |
| Stage | Lexical（不变） | 位置标签：E01 之后、一切语义 pass 之前；枚举注释的「pure syntax」措辞与语义实现的矛盾处置见 D3 |
| NeedsSemanticModel | **true（翻转）** | D3 结论；runner 仅当此字段为 true 才建整树编译（[ProjectPassRunner.cs:164-170](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L164-L170)），翻转是语义合并的**必要条件**而非可选项 |
| RunAfterIds | `{ "conditional", "lexical-normalize" }` | catalog L1068 建议顺序：E01 → C43/C28 词法批 → **C20** → E02；`lexical-normalize` 已 Implemented（C28）但默认关，参与全量拓扑但禁用时不执行。先例有二：java-keywords 锚多个 stub（[PassCatalog.cs:377-385](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L377-L385)）、member-desugar 锚 `struct-value-semantics`+`closure-lift`（[PassCatalog.cs:348](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L348)）——RunAfter 指向未启用 stub 合法（PassRegistry 只校验 id 已注册） |
| VerifyBuildByDefault / VerifyTestsByDefault | true / true | 最严档（generic/refout 先例）；generator 语义靠 build 验证（D7） |
| SupportLibrary | None | — |
| IntermediateDirectoryName | `.cs2j-partial-merge-src`（不变） | — |

### 3.3 必须补的排序边

注册表拓扑按「声明顺序兜底」（[PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133)，扫描到首个依赖满足的候选即发射，L108-L117）。stub 表在 Implemented 表之后拼接（[PassCatalog.cs:395-396](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L395-L396)），只锚 `conditional` 时，翻转后的 partial-merge 会因 Implemented 表声明顺序排到 local-function-lift 之后。翻转时必须：

1. Implemented 表中把 partial-merge 描述符置于 `conditional` 之后、`local-function-lift` 之前；
2. 给 `local-function-lift` 的 RunAfterIds **追加 `partial-merge`**（仿 C04 给 eliminate-goto 补 iterator 边的先例，[PassCatalog.cs:338](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L338)）；partial-merge 禁用时启用子计划经「全量排序→子集过滤」自然不含它，local-function-lift 不被阻塞（RunAfter 指向默认关/未启用节点经子集过滤消解，同 lexical-normalize 现状）；
3. 排序回归测试显式断言：全序中 `index(conditional) < index(partial-merge) < index(local-function-lift)`；启用子计划中 partial-merge 紧随 conditional；
4. catalog 要求「C46 须晚于 C20」（[catalog L923](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L923)、路线图第 12 项 [L1035](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1035)）由拓扑传递保证（member-desugar 实际锚 `struct-value-semantics`+`closure-lift`，[PassCatalog.cs:348](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L348)，二者远在链后），无需向 member-desugar 加边，但在同一测试中追加 `index(partial-merge) < index(member-desugar)` 断言锁死。

### 3.4 Stub→Implemented 翻转文件清单

| 动作 | 文件 | 内容 |
|---|---|---|
| 新建 | `src/CSharpToJava.Core/PartialMerge/*` | 见 §4 |
| 新建 | `Adapters/PartialMergePassAdapter.cs` | 见 §5 |
| 改 | [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) | 从 `Stubs` 删除 partial-merge 条目；**保留 `Ids.PartialMerge` 常量**（描述符与排序边复用，同 C04 处理） |
| 改 | [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | Implemented 表插入新描述符（位置见 §3.3）；给 local-function-lift 追加 RunAfter 边 |
| 改 | [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) | `CreateAdapter` switch（L149-L382）增加 `PassStubCatalog.Ids.PartialMerge => new PartialMergePassAdapter(...)`（照 iterator-lowering 臂 L334-335 形态）；无 legacy toggle（`BuildLegacyToggles` L103 不加键）、无支撑库。注意 [PassChainOrderTests.cs:237-259](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L237-L259) 会遍历全部 Implemented 描述符要求 switch 可建适配器，漏臂即红 |
| 不改 | [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) | **stub 计数早已动态化**：`Stubs` 经 `PassCatalog.BuildRegistry().WhereStatus(PassStatus.Stub)` 现取（L16-17），无硬编码计数断言；partial-merge 翻转移出 stub 表后该文件自动适配，无需改动。CS2J9001 用例（`Running_an_enabled_stub...`，L71-110；请求体 L82-97，用 closure-lift L96）不使用 partial-merge |
| 改 | [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) | `LockedChain`（L19-50）在 lexical-normalize 后、local-function-lift 前插入 partial-merge；`EnabledByDefault`（L53-54）的排除条件须**追加 partial-merge**（默认关；现状仅排除 lexical-normalize）；新增 §3.3 的两条相对顺序断言 |
| 不改 | Program.cs / 指纹 / preserve / 通用 verb | `--enable-pass partial-merge` 与 `preprocess --pass partial-merge` 自动可达；无新专属 verb |

---

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/PartialMerge/`（**新建命名空间，不复用 `PartialType/`**：后者是转换器期内存合并且含已证伪的文本去重，复用会把两个生命周期耦合）：

```
PartialMergeOptions.cs          # Strict（默认 true）、AssumeNoGeneratedPartial（M3 opt-in，见 §4.5）
PartialMergeStatistics.cs       # 通用 5 字段 + TypesScanned/GroupsMerged/PartsRemoved/
                                #   MembersCollapsed/GroupsRolledBack/Conflicts/SkippedTopLevel
PartialMergeDiagnostic.cs       # Severity/Code/Message/FilePath/Line + ToMsBuildString()（CS2J20xx）
PartialMergePlan.cs             # 整树分析结果：类型组计划、每文件编辑表、触达文件集
PartialTypePartCollector.cs     # 符号遍历 → 源内部件分组（含嵌套递归、top-level 跳过）
PartialTypeMergeBuilder.cs      # 组合并声明构造（五种类声明节点、修饰符/基类/特性/约束/文档）
PartialMemberMerge.cs           # 成员拼接、partial 方法/属性配对分类（v1 保留、M3 塌缩）
PartialDocCommentMerger.cs      # §4.3/D6
PartialMergeAnalyzer.cs         # Analyze(compilation, options) → PartialMergePlan + 基线快照判定
PartialMergeRewriter.cs         # ApplyFile(path, plan) → FileResult（CSharpSyntaxRewriter 驱动的确定性编辑）
PartialMergePreprocessor.cs     # 对外门面：Analyze 一次 + RewriteFile 逐文件 + Residual 断言
```

### 4.1 PartCollector（整树分析）

- 输入：runner 标准编译（`CreateParseOptions` override 为 `LanguageVersion.Latest` + 保留 `NETCOREAPP` 预处理符号；`CreateAdditionalSyntaxTrees` 仿 [TuplePassAdapter.cs:59-88](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs#L59-L88) 合成 implicit global usings，否则 global using 下的类型解析会退化为 object）。
- 符号遍历沿用现状形态（global namespace + 嵌套递归 + 仅 `IsInSource`），但**分组结果落 plan 而非直接合成节点**；组键 = 符号显示名（含 arity 与容器链），部件按 D1 确定性排序，主部件随之确定。
- 每组记录：各部件 `TypeDeclarationSyntax`、所在文件/span、成员列表、修饰符/基类/特性/类型参数约束语法、文档 trivia、符号句柄（`INamedTypeSymbol` 与配对成员符号）。
- 单部件类型不进 plan（零成本）；top-level statements 合成类型跳过并计 SkippedTopLevel。
- **基线优先**：分析开始取一次编译错误快照。基线已报错的组（CS0261/CS0262/CS0264/CS0579 等）标记 skip，不发 CS2J20xx、不改动，避免与编译器抢诊断。

### 4.2 MergeBuilder（组合并声明）

在主部件节点上原地构造（替换主文件中的节点）：

1. 节点种类保持主部件种类；record 两种形态按主节点 `RecordDeclarationSyntax` 原样保留参数表（参数表只可能在一个部件）。
2. 修饰符：部件并集去重 → 规范化排序（accessibility → new/static/unsafe/abstract/sealed/readonly → **保留 partial**）。
3. 基列表：基类取唯一非 object 显式基类（语义解析后同一性判定）；接口列表按符号去重并集，语法文本取规范写法；顺序按主部件优先、其余部件序。
4. 类型参数与约束：参数名/方差按主部件；where 约束跨部件并集（同名参数以符号归一）。
5. 特性：各部件 AttributeList 按部件序拼接（不重排特性内部参数）。
6. 成员：按「部件序 × 部件内声明序」拼接，**不做现状那种文本键去重**；嵌套 partial 类型递归产出其合并节点；partial 方法/属性对按 §4.5 处理；普通重复成员若基线干净而合并后引致 CS0102/CS0111，由 recompile-diff 捕获并回滚整组（D9）。
7. 文档：PartialDocCommentMerger 产出合并 leading trivia（D6）；声明顶部带 `// cs2j-partial-merge: merged from N part(s): <relpath>, ...` 溯源注释（确定性，非语义占位）。
8. 非主部件文件：用 rewriter 摘除对应类型节点，摘除位置放 D1 单行溯源注释；文件头 trivia、using、namespace、其他成员一律保留；不产生空文件删除（空 namespace/空 compilation unit 均合法）。
9. 同一文件含多个类型组时，编辑表按 span 降序应用或用 annotation 重写，保证不互相干扰；输出经 `NormalizeWhitespace` 的范围仅限新增 trivia，成员原文尽量保格式。

### 4.3 文档与 trivia 归属

- 文件头（版权/license）属于**文件**，节点摘除不影响 compilation unit leading trivia；
- 类型/成员文档属于**声明**，随 D6 规则并入合并节点；
- 行内 `#region`、`#pragma` 等指令：E01 已先行处理条件指令；其余 directive trivia 随最近成员保留，无法归属的留在主文件原位置；
- 严禁合并过程引入时间戳、绝对路径（溯源注释只用相对路径）、GUID。

### 4.4 纯语法可行性 vs 必须使用 SemanticModel 的点（D3 论证清单）

纯语法可判定（不依赖编译）：是否带 `partial` 令牌；类型名/arity/类型参数名表；容器类型名链与 namespace 段文本（含 file-scoped namespace）；节点 span/文件路径（主部件选择）；方法有无体（Body/ExpressionBody）；经典 partial 方法的语法外形（无访问修饰符 + void + 无 out + 无 virtual/override/sealed/new/unsafe/extern）；文档/特性/修饰符 token 的物理拼接。

必须语义（否则存在错误配对/错误丢弃面）：

1. 跨文件类型同一性：using alias、global using、extern alias、嵌套泛型容器、同名不同命名空间；
2. 方法签名同一性：参数类型等价（别名/全限定/`int`↔`System.Int32`）、可空注解、元组元素名、ref/in/out/ref readonly、`in` 自定义修饰符、`params`/可选参数/dynamic、返回类型（C# 重载不含返回类型，但 partial 配对需要）；
3. partial 方法/属性配对：`PartialDefinitionPart/PartialImplementationPart`、`IsPartialDefinition`；
4. 规范修饰符/基类/接口的真值：`DeclaredAccessibility`、`IsAbstract/IsSealed/IsStatic`、接口同一性去重；
5. source 位置 vs 元数据位置（`Locations.IsInSource`）与合成树过滤；
6. C#13 扩展 partial 成员（构造器、属性/索引器/事件）的配对、初始化器归属与实现存在性。

结论：纯语法合并只能覆盖「无 alias、无 global using、签名文本恰好一致」的子集，一旦配对失败只能选择放弃合并（残余）或危险猜测；现状转换器已用符号路径证明正确性需求，故 descriptor 翻 `needsSemanticModel: true`。「Lexical」描述的是**链位**（E01 之后、任何语义改写之前），不是实现手段。

### 4.5 PartialMemberMerge：两代形态规则与分阶段语义

**合并规则（catalog 六次补全，[preprocessor-catalog.md:561](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L561)；partial 属性交叉登记于 C36 行 [L1007](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1007)「partial 属性/事件（C20，Layer 1 已登记）」）：**

1. **经典 partial 方法（C#3）双轨之 A 轨**：语法外形 = 无访问修饰符（隐式 private）+ `void` 返回 + 无 `out` 参数 + 不得带 `virtual/override/sealed/new/extern`。**实现可选**：无实现部件时调用与声明由编译器整体抹除，也可由 source generator 事后补实现。配对/去留一律以符号 `IsPartialDefinition/PartialImplementationPart` 判定，不以修饰符文本猜测。
2. **扩展 partial 方法（C#9）双轨之 B 轨**：允许访问修饰符、非 void 返回、`out` 及其他合法修饰符；**实现为强制**——源内缺实现部件本身就是编译错误（由基线快照/候选 diff 捕获，pass 不自行发错；错误码以实测为准，见 §13）。v1 不得像现状 `PartialMethodMerger` 那样把无体声明一律丢弃。
3. **partial 构造器（C#13）**：一个类型至多一个**定义性声明**（无构造器初始化器、无体，以分号结束）与一个**实现性声明**（带构造器体）；**仅实现性声明允许带构造器初始化器**（`: this(...)`/`: base(...)`），定义性声明带初始化器即编译错误。合并时构造器初始化器只随实现性声明保留，v1 两份声明并存。
4. **partial 构造器与主构造器互斥边界**：类的主构造器参数表（`class C(...)`）至多出现在**一个 partial 部分声明**上；多部件各带参数表是基线编译错误，按 skip 组处理（不与编译器抢诊断）。
5. **partial 事件（C#13）**：**定义性声明 = 字段式事件**（`partial event E Ev;` 外形，无访问器）；**实现性声明必须显式带 `add`/`remove` 两个访问器**。v1 两份并存；M3 塌缩时以 add/remove 实现为存活成员。
6. **partial 属性 / 索引器（C#13）**：定义性声明无访问器体（以分号结束），实现性声明带 `get`/`set`（索引器含参数表）；配对与事件同构，v1 两份并存、M3 塌缩。
7. **通用边界**：以上全部形态要求 `LanguageVersion.Latest` 解析（§4.1）；任一形态在候选树出现「定义无实现且编译器要求实现」等错误，走 §6 基线/diff 防线与 D9 组回滚；Roslyn 实际符号/语法 API 与本设计假设（属性/事件/构造器的 partial 配对判据名称）若有出入，M2 前按 net10 探针实测修订本条，解析不了的形态发 CS2J2016 整组跳过（风险 8），不猜。

**分阶段语义：**

- **v1（M1-M2，零删除策略）**：经典 declaration-only 原样保留；扩展 partial 的定义+实现两份成员都进入合并声明（同一声明内允许定义与实现并存，等价 C# 语义）；规则 3-6 的 C#13 构造器/属性/索引器/事件同理双份保留。成员级 residual 不报错（设计如此）。
- **M3（配对塌缩，默认仍保守）**：当符号显示定义与实现**均在源内**时，塌缩为单一成员——保留实现体、合并特性（实现部件特性优先，冲突交基线/diff）、文档按 D6 拼接、摘掉 `partial` 令牌；调用点零改动（方法组/名字不变）。仅当 `AssumeNoGeneratedPartial` 显式 opt-in 时，才允许移除「源内无实现的经典 partial 方法」声明与其调用（调用点移除属语句改写，必须与 StatementTransformer 现状行为对齐并专项测试；不满足该 flag 时保留）。
- 每个塌缩动作计入 MembersCollapsed 并带溯源；塌缩导致的新编译错误走 D9 组回滚。

### 4.6 Options / Statistics / Diagnostic

- Options：`Strict`（残余多部件/无法归属新错即失败，默认 true）、`AssumeNoGeneratedPartial`（M3，默认 false）。
- Statistics：通用 `FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount` + `TypesScanned/GroupsMerged/PartsRemoved/MembersCollapsed/GroupsRolledBack/Conflicts/SkippedBaseline/SkippedTopLevel`。
- 诊断码：CS2J2001（组回滚，Warning）、CS2J2002（无法归属/整轮回滚，Error）、CS2J2010（基类冲突，防御）、CS2J2011（类型参数表不一致，防御）、CS2J2012（修饰符/访问性冲突，防御）、CS2J2013（合并非配对重复成员致新错）、CS2J2014（特性 AllowMultiple 冲突，防御）、CS2J2015（扩展 partial 成员缺实现，防御）、CS2J2016（record 参数表等不受支持形态，组跳过）、CS2J2017（检测到 analyzer/源生成器引用，Info，仅 verbose）、CS2J2018（residual 闸门：成功路径残余多部件，Strict 下 Error）、CS2J2030/2031（restore/build/test 失败映射，同各适配器惯例）。

---

## 5. CLI 适配器与集成

### 5.1 形态选型：整树自定义 ExecuteAsync（§D9）

新建 [PartialMergePassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/PartialMergePassAdapter.cs)，`override ExecuteAsync`，结构镜像 [JavaKeywordsPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs)：

1. `PrepareTreeAsync` 复用 runner（整树 1:1 复制、排除计算、MSBuild 目录文件）；
2. 全量 parse（Latest + NETCOREAPP + global using 合成树）→ 建标准编译；
3. `PartialMergePreprocessor.Analyze` 产 plan（含基线快照判定）；
4. 按 `tree.SourceFiles` 逐文件：plan 无该文件编辑 → 字节复制（含 BOM 语义，复用 CopyVerbatim/WriteRewrittenAsync）；有编辑 → 应用 RewriteFile 写出；
5. 对写出的中间树重新 parse+compile 得候选诊断，与基线做 recompile-diff（复用 JavaKeywords 的 `(File, Id, Message)` 三元组思路，路径相对中间根归一）；
6. 新错归属：诊断位置文件 → 求交「触达该文件的未回滚类型组」；恰好一组 → 恢复该组全部触达文件为原始字节、记 CS2J2001、加入排除集；零组或多组 → 全部文件恢复原始字节、CS2J2002、失败退出（同 JK030）；
7. 有回滚则带界重放（上限轮次，同 JavaKeywords 的 1024 防御），直至无新错；
8. residual 断言（候选树上源内多部件声明集合 ⊎ skip/rollback 白名单，违例 CS2J2018）；
9. restore + VerifyBuild/Tests（descriptor 默认双开，透传 chain config 与 `--no-verify-*`）→ `DeleteBuildOutputs`。

不走标准 spine 的唯一原因：标准 spine 在逐文件写出后没有「候选再编译 + 跨文件组原子回滚 + 重放」阶段（[ProjectPassRunner.cs:159-253](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L159-L253)）；树准备/复制/restore/verify/清理全部复用 stage helper，不重写。

### 5.2 verify、指纹、文件数特例

- **verify**：build+test 默认双开。build 是 generator 安全的最终 oracle（D7）；test 保证合并不改 .NET 半区行为（两半区原则）。
- **指纹**：无特例（D10）。文件数恒定；骨架文件与溯源注释字节确定；`RepointsFingerprintToPassInput=false`；`pass:partial-merge=on/off` 令牌随注册表全集自动生成，翻转造成一次主动缓存失效（符合 foundation 既定接受口径）。
- **文件数变化**：模型 B 下不发生；唯一「文件内容变空」的场景由确定性骨架注释保证非零字节且跨平台一致（`\n` 行尾策略跟主输出约定，写入前在单测锁定）。
- **支撑库/过滤**：None；compat 前缀与项目排除沿用适配器默认；不新写 csproj（不新增文件）。
- 摘要行建议：`partial-merge: G group(s) merged over F file(s), P part declaration(s) folded, R group(s) rolled back, C conflict(s)`；诊断行 MSBuild 格式，Info 仅 verbose。

---

## 6. 诊断策略（冲突显式报错）

原则：**每个多部件类型只有三个归宿——被成功合并、带着显式诊断整组回滚/跳过、或属于基线既有编译错误（不抢诊断）**。禁止注释掉成员、禁止首个胜出式静默去重、禁止把无法判定的签名当作普通成员拼接后透传（catalog §4 风险 12）。

三层防线：

1. **Analyzer 静态闸门**：基线快照先行；基线脏的组只 skip 计数；record 位置参数表多部件等不受支持形态发 CS2J2016 并整组不动；
2. **候选编译 recompile-diff**：合并引入的 CS0102/CS0111/CS0261/CS0579/CS1585 等一切新错，按 D9 唯一归属到组则组回滚（CS2J2001 列出组全名、触达文件、首条编译器诊断），否则整轮回滚（CS2J2002）；
3. **residual 闸门**：候选树上仍存在非白名单多部件声明 → Strict 下 CS2J2018 Error；partial 令牌本身**不**计残余（D2）。

所有诊断带文件/行/列、类型全名、处置建议（「该组保持原 partial 形态，将由主转换器承接；请人工解决冲突后重试」）；`--list-passes` 在翻转后展示 Implemented 状态与默认关。

---

## 7. 测试计划

### 7.1 Core 单测（建议 `tests/CSharpToJava.ProjectTests/PartialMergeUnitTests.cs`，内联多 SyntaxTree → CSharpCompilation → Analyze/ApplyFile → 文本断言 + 候选树零错误断言，仿 JavaKeywordRewriterUnitTests 形态）

1. **多文件 class**：双部件成员并集；主部件确定性（路径序、span 序各一例）；非主文件节点摘除、溯源注释存在；同文件其他类型/using/版权头保留。
2. **多部件（3+）**：三文件组；一个文件含多个 partial 类型；两个不相关类型组共享同一文件；嵌套 partial 类型跨部件（内外各自合并）。
3. **struct/interface/record**：`ref partial struct`（Utf8JsonReader 式方法散布）；双文件 partial interface（移植 [PartialInterfaceTupleMappingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PartialInterfaceTupleMappingTests.cs) 的 Tuple→Map.Entry 断言到合并后树再转译）；`partial record class`/`partial record struct`（参数表只在主部件）；泛型 partial `<T>` 两部件 arity 一致；file-scoped namespace 跨文件。
4. **partial 方法两代形态**：①`partial void M();` 无实现 → v1 保留、候选树可编译（验证不重蹈 PartialMethodMerger 无实现即丢弃）；②扩展 partial（`public partial int M();` + 实现）→ v1 两份并存可编译；扩展形态缺实现部件 → 基线编译错误、组 skip 不抢诊断；M3 塌缩后单成员、调用点不变。
5. **C#13**：partial 属性/索引器/事件定义+实现（Latest 解析）双部件——事件实现性声明显式 `add`/`remove`、定义性为字段式；partial 构造器定义+实现双部件，**初始化器只出现在实现性声明**（反例：定义性带 `: base()` 须基线报错 skip）；主构造器参数表出现在两个部件 → 基线错误 skip；探针若发现 SDK 行为差异，按实测调整并在 §4.5 记录。
6. **修饰符合并**：accessibility 只在一个部件、abstract/sealed 分部件、static class、unsafe；`readonly partial struct` 修饰符顺序（CS1585 不复发）。
7. **基类/接口**：基类只在一个部件；接口分两部件列出且不同写法等价（`IEnumerable<int>` vs 全名）去重；冲突基类基线脏 → skip；基线干净构造冲突（测试钩子）→ CS2J2010/组回滚。
8. **特性**：普通特性并集；`AllowMultiple` 多实例保留；非 AllowMultiple 重复（CS0579 基线）skip。
9. **XML 注释**：类型多部件 summary/remarks 拼接顺序与来源路径；成员文档随成员；重跑字节一致。
10. **global using/alias**：两文件分别用 `int` 与 `System.Int32`、alias 签名配对成功；未合成 global using 的反面对照（确认额外语法树钩子必需）。
11. **回滚**：构造合并后产生唯一归属新错的组 → CS2J2001、该组触达文件全部字节恢复、其余组合并保留；无法归属 → CS2J2002 且全部文件恢复；Strict=false 降级行为。
12. **幂等**：以产物为输入再跑一次：零编辑（Changed=false）、统计归零；partial 单声明（带令牌）不被二次处理。
13. **residual**：成功路径候选树源内多部件 = 0、partial 令牌计数 = 合并组数；回滚/skip 组在白名单内不触发 CS2J2018。
14. **generator 安全模拟**：合并产物保留 partial 后，再向树中加入一个「生成的」追加部件文件一起编译须通过（模拟 analyzer 增补面）；`AssumeNoGeneratedPartial=false` 时经典无实现 partial 声明不被移除。
15. **top-level statements**：合成 Program 不进组、不改动。

### 7.2 专项 fixture sln（建议 `tests/SamplePartialSolution/`，Lib + TestProject + `.sln`，Content Include，TFM net10.0；布局仿现存 `tests/SampleMemberDesugarSolution/`（C14 最近先例）或 `SampleEqualityConcatSolution/`。校订注（2026-10-02）：iterator-lowering 已由 C04 落地为 Implemented（PassCatalog L319-330），但 `SampleIteratorSolution` fixture 至今未在仓内创建，仍不能作为现存先例引用）

- Lib：跨文件夹多部件 class/struct/interface/record、partial 方法两代形态、C#13 partial 属性/索引器/事件/构造器（含初始化器归属与主构造器单部件边界）、特性与文档分部件、嵌套 partial；
- .NET 半区：行为测试锁定合并前后语义；项目级断言 pass Success、统计正确、residual=0、中间树 `dotnet build -c Release` 与 `dotnet test -c Release` 退出码 0；
- Java 半区：合并后中间树走主转换，同一套行为测试转译后通过；移植 PartialStructMerge/PartialInterfaceTupleMapping 两个真实回归场景。

### 7.3 闸门与回归

- 全仓 `CSharpToJavaConverter.slnx` Release build/test 全工程全绿（本设计阶段只写文档不执行；实施在 `dev-3.x` 原地验证，**禁止 worktree**）；
- 排序/注册表：§3.4 清单全部测试更新；stub 计数已动态化（`Stubs` 经 `BuildRegistry().WhereStatus(Stub)` 现取，无硬编码断言）——2026-10-02 实有 **14 条** stub，C20 翻转后 13 条；CS2J9001 路径对剩余 stub 仍成立；
- CLI：`--enable-pass partial-merge` 端到端（默认计划不含、显式启用执行、`.cs2j-partial-merge-src` 出现、convert-project 再跑命中指纹缓存）；`preprocess --pass partial-merge` 独立 verb 可达；
- 并存回归：pass 禁用时现有 [PartialStructMergeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PartialStructMergeTests.cs) 等转换器期测试保持全绿；启用后这些场景在中间树已合并、转换器单部件路径同样全绿（不重复合并、无 MergedTypePlaceholder 泄漏到发射结果）；
- sdk 画像（D:\csharp-sdk）：统计 partial 命中率（类型组/文件/方法两类/C#13 成员），列为 §8 门槛输入。

---

## 8. 默认开启验收门槛（建议长期默认关）

**建议**：翻转 Implemented 后保持 `defaultEnabled: false`，且不承诺按普通通道节奏默认化；它是**架构统一的前置项**，不是缺陷修复项。

1. 现状路径成熟：转换器期合并有真实多部件回归测试与多处符号兜底，默认关不损失任何现有正确性；
2. 成本不对称：默认开 = 每个项目多一次整树编译（catalog §4 风险 11）+ 全量指纹一次性失效，而简化收益要等转换器 PartialType 退役或统一引擎落地才兑现；
3. generator 盲区（D7）需要足够多真实工程（WPF/WinForms/MVVM/System.Text.Json/RegexGenerator）在 opt-in 下验证后才有默认化信心。

翻 `true` 的必要条件（全部满足）：

1. §7 全绿；fixture 两半区与全仓 Release build/test 全绿；
2. sdk 四子集 + EF Core 画像（catalog §5.1 前置行动）以 `--enable-pass partial-merge` 跑通：中间树 build/test 不退化、残余/回滚组为 0 或全部有明确人工结论、partial 命中率与方法/C#13 分布成文；
3. 含 analyzer/source generator 的工程专项验证（至少 MVVM Toolkit、System.Text.Json 各一例）通过；
4. 转换器侧 `PartialType/`+Store+placeholder 退役 PR 已合入（或统一引擎已声明单声明树前提），默认开之后无双轨长期共存；
5. 跨至少一次主版本回归窗口的 opt-in 观察期（沿用 record-readonly/array-method/iterator-lowering 先例）。

---

## 9. 里程碑

| 里程碑 | 内容 | 出口 |
|---|---|---|
| **M1 类型合并最小闭环** | PartialMerge 骨架（Options/Statistics/Diagnostic/Plan）、PartCollector 符号分组与主部件规则、class/struct/interface/record 合并节点构造（成员拼接、修饰符/基类/特性/约束、保留 partial）、非主节点摘除与溯源注释、自定义 ExecuteAsync（复制→分析→改写→restore/verify，先不做回滚重放）、Stub→Implemented 翻转（默认关） | §7.1 组 1-3、12-13 通过；fixture 骨架 .NET 半区 build 绿；禁用路径零行为差异 |
| **M2 回滚/冲突/文档 + 完整闸门** | 基线快照、recompile-diff、组级/整轮回滚重放、CS2J20xx 全量诊断、XML 合并、global using/Latest 解析、residual 闸门、fixture 两半区 | §7.1 组 6-11、14-15 全绿；CS2J2001/2002/2018 用例齐；fixture Release build+test 双绿；sdk partial 命中率清单成文 |
| **M3 partial 方法/成员塌缩（可选增强）** | PartialMemberMerge 源内定义+实现配对塌缩、`AssumeNoGeneratedPartial` opt-in 下经典声明与调用移除、C#13 成员覆盖复核 | §7.1 组 4-5 全绿；调用点移除专项；转换器侧 PartialType 退役的前置评审（不在本里程碑删代码） |
| **M4 默认化决策** | §8 五项门槛逐条结论 | 决策记录：继续 opt-in / 随统一引擎默认开，二选一留痕 |

---

## 10. 风险

1. **（最高，§1.3）不可逆合并 × 文件范式冲突**：模型 A 会同时打破 1:1 脊柱、指纹文件集与回滚原子性。缓解：D1 锁定模型 B，文件数恒定；物理合并永远是独立 opt-in。
2. **source generator 盲区**：手写编译看不见生成部件，错误删除/摘 partial 会在不可预期工程（MVVM、WPF）爆炸。缓解：保留 partial 令牌 + 不删手写无实现的经典 partial 声明 + verify build 双开（D7）；M3 删除动作必须显式 flag。
3. **编译环境与主转换器不一致**：parse 语言版本、global using、额外引用、合成树路径过滤差异可致符号解析退化。缓解：固定 Latest + global using 合成（tuple 先例）+ `IsSyntheticFilePath` 同款过滤 + 基线快照兜底。
4. **成员拼接正确性**：放弃文本去重后，合法但奇异的成员形态（显式接口实现、同名不同种类成员、嵌套类型与字段同名）依赖编译器 diff 兜底；归属失败会扩大为整轮回滚。缓解：§7.1 组 11 的注入式回滚用例 + 归组求交规则保守（宁缺毋滥）。
5. **并存期双路径**：启用后转换器若仍把单部件 partial 当多部件处理（或反之），会产生 placeholder 泄漏或重复发射。缓解：D12 休眠不改删 + 7.3 双向回归（禁用/启用各跑一遍现有 partial 测试）。
6. **链成本与指纹**：多一次整树编译；启用即一次性缓存失效。缓解：默认关；令牌机制已就位；sdk 上 M2 起度量增量耗时。
7. **骨架文件/trivia 回归**：节点摘除破坏版权头、空 namespace、BOM/行尾。缓解：文件头 trivia 归属规则（§4.3）+ 字节确定性单测 + BOM 沿用 WriteRewrittenAsync。
8. **C#13 语法面漂移**：partial 属性/索引器/事件的最终 Roslyn 语义若与设计假设不同。缓解：M2 前 net10/latest 探针实测并把结果写入 §4.5（同 C04 §4.6 做法）；解析不了就 skip + CS2J2016，不猜。
9. **排序边回归**：新增 local-function-lift → partial-merge 边后，未来翻转 lexical-normalize 或插入新链首 pass 可能改变相对顺序。缓解：§3.3 排序断言锁死，不靠声明顺序约定。
10. **直连入口与退役节奏**：`ConvertProjectWithPartialMergeAsync` 与两个既有测试在退役前必须持续可用，否则 M3 评审失去对照基线。缓解：退役独立 PR、残余数据先行。

---

## 11. 并行协调

1. **链位与交互面**：链最前、E01（conditional，`.cs2j-conditional-src`）之后；条件指令已先剔除失活 `#if` 区域——C20 因此只会合并**实际参与编译的部件**，这是它必须晚于 E01 的硬理由。合并树是其后**所有** pass 的共同输入契约：「源内每类型一份声明、`partial` 令牌保留、文件集合不变」。下游 pass 不需要为本特性做任何改动，但该契约一旦翻转不得悄悄改变（residual 统计是对外信号）。
2. **与 C11/C46 顺序**：catalog 中 C46 明确「须晚于 C20（partial 合并后签名才完整）」（[L923](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L923)），路线图第 12 项成员去糖族 C11→C12→C13→C14（+C46）在 [L1035](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1035)；代码侧实际 RunAfter 锚点是 member-desugar 锚 `struct-value-semantics`+`closure-lift`（[PassCatalog.cs:348](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L348)），拓扑上必然晚于 C20；C46 的签名收口在成员塌缩之后做反而更简单（单声明内配对）。本特性不向 member-desugar 加边，仅以排序测试断言 `partial-merge < member-desugar`；C11 已合入、其 worktree 已回收——任何下游通道翻转 stub 时若 partial-merge 已翻 Implemented，双方无代码语义冲突，仅注册表三个热文件的预期文本冲突。
3. **与其他在飞 worktree**：2026-10-02 `git worktree list` 实有 11 个残留——c05-enum-declaration、c06-unsafe-ffm、c07-struct-value-semantics、c15-checked-unchecked、c16-foreach-complete、c17-increment-target、c34-decimal-ops、c36-collection-expr、c39-finalizer-strip、c40-foreach-source、c47-equality-concat（对应通道多已合入 `dev-3.x`，worktree 尚未回收；无 c20 目录冲突）。共同热文件 = [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) 加两个 Preprocessing 测试文件。解决规则同 C04 §11：后合者保留全部 descriptor 与各自 RunAfter 边，不存在语义冲突；合并后必跑 PassChainOrderTests 全序断言。**本特性自身禁止新建 worktree**（用户强制约束：`dev-3.x` 原地执行，见 §12）。
4. **与转换器现有 PartialType 的并存期纪律**：本特性不编辑 `PartialType/`、`PartialTypeMergeStore`、四个 `TransformMerged`、TypeGroupResolver；转换器侧特判（GenericDeepen、RecordReadOnly 族）在 M3 退役 PR 前保持原样。退役 PR 的准入证据 = M2 产出的 sdk 残余统计（成功路径多部件 = 0）。
5. **幂等承诺**：产物再跑本 pass 零编辑（带溯源注释的输入被识别为已合并/单部件直接不进组），为未来统一引擎 fixpoint 合批（catalog §5.4）预留可重入性。

---

## 12. 执行纪律：dev-3.x 原地执行（禁止 worktree）

> **用户强制约束（2026-10-02）**：本特性在 `dev-3.x` 分支的主工作区（`D:\code\cs2j`）原地实施，**禁止使用 `git worktree`**（不创建 `.worktrees/preproc-c20-*`，也不复用现存 11 个残留 worktree）。本节取代此前各版「Worktree 分配」的全部命令与 base 选择讨论。

- 分支：开工前确认主工作区在 `dev-3.x` 且为最新（`git branch --show-current`、`git fetch` 后 `git pull --ff-only`）；不新建 feature 分支，提交直接落在 `dev-3.x`（提交节奏由实施计划 §0 规定）。
- **在途改动隔离**：工作区可能存在其他通道（C28/C34 等）的未提交改动——每次提交必须**逐文件 `git add <path>`**，严禁 `git add -A`/`git add .`，提交前以 `git status` 复核暂存内容仅含本特性文件。
- 实施纪律：`dotnet build`/`dotnet test` 在主工作区原地执行，按 §7 验证；翻转与退役分属不同 PR/提交序列（M1-M2 翻转，转换器侧旧机制退役在 M3 之后独立进行）。
- 历史上的 `git worktree add .worktrees/preproc-c20-partial-merge ...` 命令与 base `953293b9`/rebase 讨论**全部作废**，不得执行；§13 开放问题 2 据此失效。

---

## 13. 开放问题裁决（2026-09-29 拍板；2026-10-02 再注）

> **2026-09-29 拍板**（原文保留于下）；**2026-10-02 状态再注**：① 问题 1（stub 计数）已自动消解——计数测试改为动态查询，当前真值 14 条，翻转后 13 条，无需开工首步取数；② 问题 2（base/worktree）**整体失效**——用户强制 `dev-3.x` 原地执行、禁止 worktree（见 §12），上方拍板块⑧中 worktree/base 口径一并作废；③ 问题 3（catalog L561 括注歧义）**仍挂起**，catalog 不在本次可改范围。

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持 base `953293b9`，创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（按原题号）：**

1. **stub 计数测试**：开工首步在 worktree 内实跑 `dotnet test --filter PassStubCatalogTests` 取真值并接续（翻转 partial-merge 后预期 20 条），以运行结果为准，不依赖写作时点算。【2026-10-02：**已消解**——断言动态化（PassStubCatalogTests L16-17），当前 14 条，翻转后 13 条；"worktree 内"措辞随 §12 失效】
2. **base 选择**：维持 `953293b9` 创建，创建后 rebase 到当时 dev-3.x HEAD（裁决时 `ef70e5a2`，已含 member-desugar/pattern-desugar 翻转与更新的 LockedChain）；注册表热文件冲突在 rebase 中按锚点条目实际形态解，不另选 base。【2026-10-02：**整条失效**——禁止 worktree、原地 dev-3.x 实施（§12）】
3. **catalog L561 括注歧义**：按 C# 官方语义——经典 void partial 方法实现可选、扩展 partial 方法实现强制；本设计 §4.5/D4 口径正确，catalog 该行随本 pass 翻转 PR 一并订正。
4. **主部件路径口径**：采用相对源根路径 + 序数比较；Windows 下仅大小写差异的并存目录判为畸形工程并发显式诊断，不沿用 OrdinalIgnoreCase。
5. **C# 13 配对符号 API**：M2 前以 net10/Latest 探针锁定 partial 构造器/事件/属性/索引器的定义性/实现性配对判据；探针证实无法判别的形态发**显式诊断**（CS2J2016 改为响亮诊断，不静默跳过）。
6. **`PassStage.Lexical` 枚举注释**：本特性不动 Core 枚举文案，留统一引擎（catalog §5.4）批次细化。

### 13.1 原问题记录（裁决前）

1. **stub 计数测试红绿无法证实**：[PassStubCatalogTests.cs:24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24) 断言 22，而 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) Stubs 静态只有 21 条（26ea367c 翻转 member-desugar 后计数未同步）。本任务被禁止运行构建/测试，该断言当前是红是已被他处补偿（如另一注册源）无法证实；实施第一步须 `dotnet test --filter PassStubCatalogTests` 核实并接续真值（翻转 partial-merge 后预期 20 条）。【2026-10-02：**已解决**——L24 硬编码断言已删除，改为动态查询（L16-17）；本条仅作历史记录】
2. **base 选择**：worktree 从 `953293b9` 还是当前 HEAD `2d25700e` 切出？后者已含 member-desugar/pattern-desugar 翻转与更新的 LockedChain，翻转补丁更小；前者与本稿写作时证据一致但注册表热文件已冲突。倾向 rebase 到新 HEAD，留实施者定。【2026-10-02：**失效**——禁止 worktree（§12）】
3. **catalog L561 括注歧义**：六次补全行把「实现**可选**」挂在「C#9 扩展 partial 方法（非 void/out/修饰符）」后；按 C# 官方语义，实现可选的是经典 void 形态，扩展形态实现强制。本稿 §4.5/D4 按官方语义写；catalog 文件不在本次可改范围，需 catalog 维护者另行订正该括注。
4. **主部件路径口径**：InterfaceRippler 先例用完整路径 + `OrdinalIgnoreCase`（[L491-496](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/InterfaceRippler.cs#L491-L496)），本设计拟用相对源根路径 + 序数比较。Windows 下仅大小写差异的目录名并存属于畸形工程，是否完全沿用 OrdinalIgnoreCase 口径在 M1 单测定稿。
5. **C#13 配对符号 API**：partial 构造器/事件/属性/索引器在当前 Roslyn 版本上的定义性/实现性配对判据（方法有 `PartialDefinitionPart/PartialImplementationPart`，其他成员种类的等价 API 名称）未在本仓找到既有用法；§4.5 规则 3-6 的实现判据以 M2 前 net10/Latest 探针实测为准，不可解析形态 CS2J2016 跳过。
6. **`PassStage.Lexical` 枚举注释**（"pure syntax, no semantic model"）与本 pass 语义实现长期共存是否应改注释/细化枚举，留给统一引擎（catalog §5.4）批次处理，本特性不动 Core 枚举文案。
