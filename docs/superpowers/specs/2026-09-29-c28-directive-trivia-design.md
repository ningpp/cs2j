# C28 指令/using trivia 清扫扩展（lexical-normalize 锚点任务）设计

**日期**: 2026-09-29
**范围**: 新增 Core 目录 `src/CSharpToJava.Core/LexicalNormalize/`（纯语法、幂等、每文件只 parse 一次）+ CLI 单适配器，翻转注册表 stub `lexical-normalize`：承接 catalog C28 的 `#line`、`#error`（非恒剥场景）、`#define` 限界、extern alias 审计、global using 项目级审计/归一预备、using static 盘点、文件作用域命名空间（file-scoped namespace）可选归一块级；本任务不改任何 C#→Java 转换代码
**状态**: 设计已确认，待实施
**族属**: catalog P3「词法/指令归一族」（[preprocessor-catalog.md §3.3](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L902-L902)：E01 + C28 + **C43（观察项）**）。注册表 stub `lexical-normalize` 当前 `catalogItems = "C28, C43 (observation)"`、`RunAfter = conditional`、`Stage = Lexical`、`needsSemanticModel = false`（[PassStubCatalog.cs:50-60](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L50-L60)）。**C28 是该 pass 的锚点任务**：完成 stub→Implemented 翻转、`catalogItems` 收窄为 `"C28"`；姊妹观察项 C43（raw string / u8 / verbatim 字面量，catalog [L784-795](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L784-L795)）若实施则向同 pass 的 `Literals/` 子目录追加规则，本设计为其预留接口但不依赖。

---

## 1. 背景与目标

### 1.1 catalog C28 条目与实测频率

C28 登记为 P3 窄扩展（[preprocessor-catalog.md:590-600](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L590-L600)）：目标模式为 `#line`、`#error`（非恒剥场景）、`#define` 限界、extern alias、global using 注入/别名归一、using static、文件作用域命名空间可选归一等 trivia 层清扫；不解决 fail-fast，价值是整洁度/一致性，以及作为统一引擎「纯语法批只 parse 一次」的天然成员（catalog §5.4，[L976-1003](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L976-L1003)；Layer 0 全景 [L1009-1017](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1009-L1017)）。sdk 实测频率：file-scoped namespace **2** 处、global using/using static/extern alias **20+** 处（训练集低频，纯语法、幂等是其入选理由，而非命中率）。

### 1.2 E01 已剥/不剥的精确边界

E01（conditional，已实现、默认开、纯文本）的指令分类表在 [ConditionalDirectiveStripper.cs:75-88](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L75-L88)：

- **已剥**：非 active 指令、`DisabledTextTrivia`（禁用分支）；active 指令中 `#define/#undef/#if/#elif/#else/#endif` 落入 `_ => true` 分支被剥（分支选择在 parse 期已固化，剥后输出不再依赖该定义，见 [conditional-directive-strip-design.md:31](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-24-conditional-directive-strip-design.md#L31-L31)）；`#region` 仅 `RemoveRegions` 开时剥；`#pragma warning/checksum`、`#nullable` 恒剥。
- **不剥（显式 false）**：`#line`（含 `default`/`hidden`，仅影响行号映射，[L78](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L78-L78)）、`#error`（[L82](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L82-L82)）、`#warning`、`#reference`、`#load`、`#!`。
- **激活态 `#error`** 产生 Roslyn 解析错误 CS1029 → E01 收集 Error 诊断后**文件原样透传不改写**（[L25-36](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L25-L36)、[conditional-design:37](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-24-conditional-directive-strip-design.md#L37-L37)）；激活态 `#warning` 产 CS1030 Warning 但保留（[L38-45](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L38-L45)）。

C28 严格在 E01 **之后**运行（stub 已锚 `RunAfter=conditional`），只补 E01 显式不碰的面，不重新求值任何条件符号。

### 1.3 转换器侧现状证据

- **using 别名**：转换器有逐文件语义注册表 [UsingAliasRegistry.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/UsingAliasRegistry.cs)，每文件 `Clear()`（[L91-95](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/UsingAliasRegistry.cs#L91-L95)），经 SemanticModel `GetSymbolInfo` 解析 `using X = N.T;`（[CSharpToJavaVisitor.cs:184-229](file:///d:/code/cs2j/src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs#L184-L229)）；测试 [UsingAliasResolutionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/UsingAliasResolutionTests.cs)、[GlobalAliasQualifiedNameTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GlobalAliasQualifiedNameTests.cs)。
- **using static**：已直映 Java static import（[CSharpToJavaVisitor.cs:161-169](file:///d:/code/cs2j/src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs#L161-L169)；HIR 路径 [HIRImportResolver.cs:14](file:///d:/code/cs2j/src/CSharpToJava.Core/HIR/HIRImportResolver.cs#L14-L14)）——无缺口。
- **global using**：编译侧已等价补齐——单文件管线有合成树 [ConversionPipeline.cs:91-97](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L91-L97)，预处理一次性编译有 [ImplicitGlobalUsings.BuildForProject](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectCompileSet.cs#L286-L327)（读 `ImplicitUsings=enable` 后注入默认命名空间）。
- **file-scoped namespace**：转换器与 HIR 均消费共同基类 `BaseNamespaceDeclarationSyntax`（[CSharpToJavaVisitor.cs:43](file:///d:/code/cs2j/src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs#L43-L43)、[L303](file:///d:/code/cs2j/src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs#L303-L303)；[CSharpToJavaHIRGenerator.cs:28-60](file:///d:/code/cs2j/src/CSharpToJava.Core/HIR/CSharpToJavaHIRGenerator.cs#L28-L60)），JavaKeywords 的 analyzer/rewriter 也两种形态都处理（[JavaKeywordAnalyzer.cs:99-107](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs#L99-L107)）——无缺口，归一仅为中间树一致性。
- **extern alias**：全仓 grep 无任何 `extern alias` 解析/别名引用（`MetadataReferenceProperties.Aliases`、csproj `<Aliases>`）基建：[ProjectCompilationBuilder.GetMetadataReferences](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L84-L148) 全部用无别名的 `MetadataReference.CreateFromFile`；转换器仅处理内建 `global::` 形态（[ExpressionTransformerRegistry.cs:153-169](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs#L153-L169)）。Java classpath 亦无「同一全限定名两套程序集」对应物。

### 1.4 统一引擎 Layer 0 定位与本任务边界

catalog §5.4 把 E01+C28+C43 列为「一次 parse 内存合批」首批试点（[L1003](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1003-L1003)、§4 风险 19 [L938](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L938-L938)）；但 foundation plan 已把跨 pass 合批/fixpoint/内存串联明确划为 out-of-scope（[preprocessor-foundation_plan.md:25](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L25-L25)③、[L161](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L161-L161)），当前 runner 仍是每 pass 复制整树（[ProjectPassRunner.cs:159-252](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L159-L252)）。本任务的合批承诺分两层：**pass 内**每文件只 parse 一次、三个 rewriter 在同一棵 `CompilationUnitSyntax` 上内存串接（本任务实现）；**pass 间** E01/C28/C43 共享一次 parse 留给未来统一引擎，本任务只保证各 rewriter 是无文件 IO、tree-in/tree-out 的可组合件（接缝，不实现）。

### 1.5 子项范围与分工

| 子项 | v1 处置 | 规则 |
|---|---|---|
| `#line`（含 default/hidden） | 选项开启时剥除（trivia-only） | LN-1 |
| 激活态 `#error` | **永不剥**；显式 Error 诊断、文件不动 | LN-2 |
| `#define/#undef`/残余条件指令 | 不重求值；检测到即 Error（E01 顺序守卫，即「限界」） | LN-3 |
| `extern alias X;` 与 `X::` 使用点 | ★纯语法不可归一：**审计/报错**，不改写 | LN-4 |
| global using（普通/static/别名） | **保持项目级不展开**；纯语法跨文件查重、分类计数、建模型 | LN-5 |
| using static | 不改写（转换器已直映）；盘点计数 | LN-6 |
| file-scoped namespace | 双层 opt-in 归一块级命名空间 | LN-7 |
| `#warning`、`#pragma`、`#nullable`、`#region` | 不碰（E01 职责面） | — |
| C43 raw/u8/verbatim 字面量 | 不实现；预留 `Literals/` 目录与 CS2J5570-5599 码段 | — |

### 1.6 目标

1. 在纯语法、无 `CSharpCompilation`/`SemanticModel` 的前提下交付 LN-1/LN-5/LN-6/LN-7 四条归一/审计规则与 LN-2/LN-3/LN-4 三条响亮失败守卫；
2. 翻转 `lexical-normalize` stub（Implemented、默认关、verify build/test 双开），完成注册表/链工厂/CLI 开关/链序测试全套接线；
3. 产物为可编译 C#，幂等（二次跑零 diff），trivia/BOM 保真；
4. 为 C43 与未来跨 pass 词法合批预留规则接口、目录所有权与诊断码段。

明确不做：extern alias 的语义归一（★结论为报错）、global using 逐文件展开、using static 改写、任何符号绑定/改名、任何转换器代码改动、跨 pass 合批编排。

### 1.7 硬不变式

1. `NeedsSemanticModel = false` 不被任何子项翻转；需要语义/外部元数据才能做的子项一律拆为「审计/报错或独立规则」，不拉高整 pass（决策 D10）；
2. 永不剥除激活态 `#error`、永不自行求值 `#if`/`#define`（不越过 E01 边界）；
3. LN-1 是 trivia-only 改写：输出与输入的 token 序列（kind+text）必须逐枚相等，复用 E01 已验证的校验法（[ConditionalDirectiveStripper.cs:154-178](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L154-L178)）；
4. LN-7 开启时：输出树零 `FileScopedNamespaceDeclarationSyntax`，成员/using 集合不增删不重排语义次序；
5. 幂等：任何规则产物二次 `Normalize` 零 diff；干净文件字节级复制、BOM 保持；
6. Error 级诊断存在时该文件 Changed=false（不产生半改写文件），由诊断决定 pass 成败；
7. 禁止注释/占位/静默透传流入 Java 产物（catalog §4 风险 12）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| D1 | `#line` 剥离/保留映射 | **默认剥除**（`StripLineDirectives=true`）：LN-1 按 E01 的 trivia 过滤法移除 active `LineDirectiveTrivia`（含 `default`/`hidden`），token 序列不变；选项可关 | `#line` 只改 PDB/诊断行号映射，无编译语义（[conditional-design:34](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-24-conditional-directive-strip-design.md#L34-L34)）；中间树是重写后新文本，旧映射反而是错的（verify build 诊断会指向剥离前位置）。需要回溯原始行号的场景由各 pass 的中间目录保留链承担，不靠 `#line` |
| D2 | 激活态 `#error` | **不剥，显式失败**：LN-2 检测 active `ErrorDirectiveTrivia` → CS2J5511 Error、文件原样 | 剥除等于把「编译不过」改成「编译得过」，是语义篡改；E01 当前让文件原样透传由下游 build 报错，C28 把失败前移并带上指令文本。能正常 build 的项目经 E01 后不应再有 active `#error`（都在禁用分支里被剥），命中即符号集/顺序异常信号 |
| D3 | `#define` 限界 | **不重新求值、不补剥**；LN-3 发现残余 `#define/#undef` 或任一 `#if/#elif/#else/#endif` → CS2J5512 Error「conditional must run first」 | E01 已在 parse 期固化分支后剥除 define/undef（[L75-88](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L75-L88)）；脱离分支选择去剥 `#define` 必然出错。此守卫服务独立 `preprocess --pass lexical-normalize` 用法，链上正常路径不触发 |
| D4 | ★ extern alias | **v1 只审计/报错、不改写，维持纯语法**：LN-4 对 `ExternAliasDirectiveSyntax` 与别名标识符非 `global` 的 `AliasQualifiedNameSyntax` 发 CS2J5520（默认 Error，选项可降 Warning）；`global::` 明确不属此面 | 归一需三项纯语法拿不到的输入：① csproj `<Aliases>` 指定的别名→程序集映射（源外元数据）；② 带别名 `MetadataReference` 的全编译符号（现状基建零支持，[ProjectCompilationBuilder.cs:84-148](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L84-L148)）；③ extern alias 的存在意义就是消解双程序集同全名冲突，而 Java classpath 无法表达该冲突——即便语义 pass 也只能处理无冲突子集。故结论：**宁拆规则不改 flag**；未来语义规则（独立 pass）只做无冲突子集的 `X::N.T → global::N.T` 改写，冲突子集仍报错 |
| D5 | global using 展开模型 | **v1 保持项目级，不展开到每文件**：LN-5 逐文件纯语法分类（plain/static/alias）建 `GlobalUsingRecord`，适配器 `AfterFilesAsync` 做跨文件同文本查重（CS2J5521 Warning）；不移动、不复制、不解析目标 | 展开到每文件的否决理由：① global using 可出现在任意文件，逐文件 spine 下不知全集，强制两阶段 parse 或自定义执行，违背「只 parse 一次」；② `global using A = N.T;` 别名展开会与文件内同名别名的优先级冲突；③ 下游编译已等价补齐（[ConversionPipeline.cs:91-97](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L91-L97)、[ProjectCompileSet.cs:286-327](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectCompileSet.cs#L286-L327)），语义路径天然按项目级生效；④ 全文件 diff/指纹抖动零收益；⑤ 下游 pass 自己会注入 using（[RefOutInRewriter.cs:82](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInRewriter.cs#L82-L82)、[IteratorStateMachineRewriter.cs:420](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorStateMachineRewriter.cs#L420-L420)），展开再注入不可幂等。`ExpandPerFile` 作为已评估备选记入开放问题 2 |
| D6 | using static | **不改写，仅盘点**：LN-6 计数（CS2J5522 Info，verbose） | 转换器已 1:1 直映 Java static import（[CSharpToJavaVisitor.cs:161-169](file:///d:/code/cs2j/src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs#L161-L169)）；任何重排只是 diff 噪声；目标类型是否可解析需 SemanticModel，不在纯语法面 |
| D7 | file-scoped namespace 归一开关 | **双层 opt-in**：pass 默认关；pass 启用后 `NormalizeFileScopedNamespaces` 仍默认 **false**，显式打开才执行 LN-7：`namespace A.B;` + 后续 usings/成员 → `namespace A.B { … }` 块级 | 转换器/HIR 两种形态都吃（§1.3），非正确性项；整文件缩进级 trivia 改动面大，默认关控制风险。语义要点：file-scoped 中声明**之后**的 using 在命名空间内，声明之前的 using 在命名空间外——搬移时按相对声明的位置分置块内外。格式化走 `AdhocWorkspace`+`Formatter.Format`（E07/E11 既有范式），等价性靠 verify build/test 守门 |
| D8 | 幂等性 | LN-1/2/3/4 只删 trivia 或只诊断 → 二次跑无目标即 Changed=false；LN-7 产物无 FileScoped 节点 → 不再命中；LN-5 记录随文本稳定 | 每文件以 ordinal 文本相等判 Changed；输出统一再过 verifier（重 parse 无 Error + 已启用规则 residual 清零 + LN-1 的 token 序列相等） |
| D9 | trivia 保真 | 指令剥离复用 E01 的「过滤后回卷 + 顺带摘除行尾空白」机制（[L222-225](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L222-L225)）；指令附带的普通注释保留在后继 token 的 leading trivia；LN-7 的注释随所属节点搬迁；BOM 由适配器 `PreserveBom=true` 保持 | 不发明新 whitespace 策略；E01 的机制已有全套单测（[ConditionalDirectiveStripTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ConditionalDirectiveStripTests.cs)） |
| D10 | needsSemanticModel 复核 | **维持 false**。会逼迫翻 true 的子项：extern alias 真归一（D4）、using/global using 别名目标解析与使用点改写、按语义的 global using 查重——三者 v1 全部不做（报错/只做文本查重/留给转换器既有注册表） | flag 翻 true 会让 runner 对整树建 compilation（[ProjectPassRunner.cs:164-170](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L164-L170)），丢失 parse-only 快路径，且与 stub 登记及 Layer 0 定位矛盾；按 foundation plan「宁拆规则不升档」处理 |
| D11 | parse 次数与合批接缝 | **pass 内一次 parse**：`CSharpSyntaxTree.ParseText(Latest)` 一次取 root，DirectiveRewriter→UsingAliasRewriter→NamespaceRewriter 在内存串接，`ToFullString` 一次；不加预处理符号（E01 后分支已固化）。**pass 间合批不实现**，rewriter 一律无 IO、`(CompilationUnitSyntax, options, bag)` 形状，供未来 `LexicalBatchRunner` 与 E01 的 `RemovingRewriter`、C43 literal rewriter 链式组合 | foundation plan 已把合批列为 out-of-scope（[L25](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L25-L25)③、[L161](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L161-L161)）；catalog §5.4 风险 19 要求先试点「三不变」再推广，C28 只把自己做成合格的合批零件 |
| D12 | 默认开关 | 翻转即 Implemented 但 `defaultEnabled: false`；§8 门槛全过后单行翻 true | 同 C02/C16/C20 范式；P3 整洁度项，opt-in 长期化风险低于静默改树 |

---

## 3. 注册表与链位契约

### 3.1 现状（stub）

[PassStubCatalog.cs:50-60](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L50-L60)：Id `lexical-normalize`（常量 `Ids.LexicalNormalize`，[L22](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L22-L22)）、目录 `.cs2j-lexical-normalize-src`、`PassStage.Lexical`、Stub、`defaultEnabled: false`、`runAfterIds: { PassIds.Conditional }`、**`needsSemanticModel: false`**、`catalogItems: "C28, C43 (observation)"`。已全仓 grep：`LexicalNormalize`/`lexical-normalize` 标识符除该文件外仅出现在 docs（C20/C23 设计的协调引用），删除常量无代码改引负担。

### 3.2 链位与顺序锁

- **RunAfter 锚边保持 `{ conditional }`**。拓扑排序以声明序为决胜（[PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133)）；stub 表拼在 Implemented 表之后（[PassCatalog.cs:242-243](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L242-L243)），故翻转描述符必须在 `Implemented` 表中**物理紧随 conditional 条目插入**（位于 local-function-lift 之前，[PassCatalog.cs:36-55](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L36-L55)）。启用子计划拓扑结果：

  ```
  conditional, lexical-normalize, local-function-lift, null-coalesce, …
  ```

  无需给 local-function-lift 补边（C16 在 foreach-deconstruction/strip-default 间插位的同构先例：物理序足够）。
- **与 C20 的次序约定**：catalog 建议序为 E01 → [C43+C28] → [C20] → E02（[catalog:885](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L885-L885)）；C20 设计已承诺其翻转时锚 `{ conditional, lexical-normalize }` 并给 local-function-lift 补边（[c20-design §3.2-3.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c20-partial-merge-design.md#L80-L92)）。本 pass 禁用时该锚边在启用子计划中被过滤，不影响 C20 未来翻转（goto→iterator 已验证此模式）。
- Stage 保持 `Lexical`（信息面；执行序只看 RunAfter，[PassDescriptor.cs:5-7](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L5-L7)）。
- 默认关期间不进执行计划：无目录、无耗时、无链行为变化。

### 3.3 翻转后的描述符字段

```
Id                       = "lexical-normalize"
Stage                    = PassStage.Lexical
Status                   = PassStatus.Implemented
DefaultEnabled           = false                     # 锚点翻转即 Implemented，默认关；§8 门槛后单行翻 true
RunAfterIds              = { PassIds.Conditional }
NeedsSemanticModel       = false                     # 不翻（D10）
SupportLibrary           = PassSupportLibrary.None
VerifyBuildByDefault     = true                      # 与 E01 不同：LN-7 有结构性改写，见 §5.2
VerifyTestsByDefault     = true
IntermediateDirectoryName= ".cs2j-lexical-normalize-src"
CatalogItems             = "C28"                     # 收窄；C43 合入时改 "C28, C43"
Description              = "Strip #line directives, audit #error/extern-alias/global-using forms and optionally normalize file-scoped namespaces; pure-syntax idempotent."
```

无支撑库：Program.cs 库过滤三处零改动。

### 3.4 翻转文件清单

1. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：`PassIds` 增 `public const string LexicalNormalize = "lexical-normalize";`（紧随 [L8](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L8-L8)）；`Implemented` 表在 conditional 条目（L36-44）之后、local-function-lift 之前插入 §3.3 描述符（needsSemanticModel=false、双 verify 开、无 supportLibrary）。
2. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删除 lexical-normalize stub 条目（L50-60）与 `Ids.LexicalNormalize` 常量（L22；已核实无外部代码引用）。
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)：`CreateAdapter` 增 `PassIds.LexicalNormalize => new LexicalNormalizePassAdapter(…)`（紧随 conditional 分支，[L98-104](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L98-L104)）；`BuildLegacyToggles` 增 `[PassIds.LexicalNormalize] = opts.LexicalNormalize`（[L60-80](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L60-L80)；[PassChainOrderTests.cs:169-182](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L169-L182) 要求 LockedChain 中每个 implemented pass 都有 toggle）。
4. [Program.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs) `ConvertProjectOptions`：在 [L4724-4726](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4724-L4726) 附近增正向 opt-in 属性 `[Option("lexical-normalize", Default = false, HelpText = "…")] public bool LexicalNormalize { get; set; }`（同 C16 方向，不用 `--no-xxx` 反写）；preserved 目录（[Program.cs:925-935](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L925-L935)）与指纹令牌由描述符全集驱动，无需手写接线；`--list-passes` 自动显示。
5. 新增 [Adapters/LexicalNormalizePassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（§5）。
6. **三个测试文件**（本任务指定）：
   - [PassChainOrderTests.cs:18-38](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L38)：`LockedChain` 在 `Conditional` 与 `LocalFunctionLift` 之间插入 `PassIds.LexicalNormalize`；`EnabledByDefault`（L40-55）**不变**；补全序断言 `index(conditional) < index(lexical-normalize) < index(local-function-lift)`；
   - [PassStubCatalogTests.cs:24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24-L24)：stub 总数 `23 → 22`；
   - [ProjectPreprocessorsConsistentTests.cs:129-132](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs#L129-L132)：追加 `PassCatalog.ImplementedById(PassIds.LexicalNormalize).IntermediateDirectoryName == ".cs2j-lexical-normalize-src"`（无门面类，仿 generic-deepen 段）。
7. §8 门槛达成后单行翻 `defaultEnabled: true`（指纹令牌一次性失效，可接受，catalog §4 风险 11）。
8. `CatalogItems` 后续只追加：C43 合入时 `"C28" → "C28, C43"`。

---

## 4. Core 组件设计

### 4.1 目录与文件所有权

```
src\CSharpToJava.Core\LexicalNormalize\
  LexicalNormalizePreprocessor.cs   # 对外入口：Normalize(sourceCode, options) → LexicalNormalizeResult
  LexicalNormalizeModels.cs         # Options / Statistics / Diagnostic / Result（同 E01 单文件模型范式）
  DirectiveRewriter.cs              # LN-1 #line 剥离；LN-2 #error、LN-3 残余条件指令的检测与限界
  UsingAliasRewriter.cs             # LN-5 global using 分类记录/同文件同文本查重；LN-6 using static 盘点；
                                    #   LN-4 extern alias 指令与 X:: 使用点检测；v1 不修改 using 节点
  NamespaceRewriter.cs              # LN-7 file-scoped → block-scoped（仅选项开启）
  LexicalNormalizeVerifier.cs       # 重 parse 校验 + 分规则 residual + LN-1 token 序列相等
  GlobalUsingModel.cs               # GlobalUsingRecord(Kind, Text, Line, IsGlobal, IsStatic, IsAlias)
  Literals\                         # 【C43 预留，本任务不创建文件/目录】：raw/u8/verbatim 规则所有权
```

所有权硬约束（为 C43 立）：C43 落地时只新增 `Literals/` 自有文件与自有测试、在 preprocessor 的规则序列追加一步、码段用 CS2J5570-5599、适配器构造处加选项透传；不修改 LN-1…LN-7 既有行为。共享件需放宽时先扩根层并补 C28 回归。**本任务不创建空的 `Literals/` 目录**（空目录无交付意义，仅在本文档登记所有权）。

### 4.2 对外契约（与 E01 同形，纯语法逐文件）

```csharp
public sealed class LexicalNormalizePreprocessor
{
    public LexicalNormalizeResult Normalize(
        string sourceCode, LexicalNormalizeOptions? options = null);
}
public sealed record LexicalNormalizeResult(
    string OutputCode, bool Changed,
    IReadOnlyList<LexicalNormalizeDiagnostic> Diagnostics,
    LexicalNormalizeStatistics Statistics,
    IReadOnlyList<GlobalUsingRecord> GlobalUsings);   // 供适配器跨文件聚合
```

- 只依赖 `Microsoft.CodeAnalysis.CSharp` 的 syntax 层：`CSharpSyntaxTree.ParseText` + `CSharpSyntaxRewriter`；**不创建 `CSharpCompilation`、不接收 `SemanticModel`**——与 E01 同档（[ConditionalDirectiveStripper.cs:19-23](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L19-L23)）。
- parse 选项：`CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest)`，**不设预处理符号**（条件分支归 E01；此处若再给符号会制造两套求值真相）。
- parse Error（CS1xxx、CS1029 等）：收集诊断、原样返回（同 E01 L25-36）。
- 廉价预检：遍历 root，若无任何已启用规则的目标节点，直接返回原文（`FilesSkippedClean=1`）。

### 4.3 pass 内执行流（一次 parse、内存串接）

1. parse 一次取 `CompilationUnitSyntax root`；parse Error → 返回原文 + Error；
2. LN-3 限界扫描：发现 `DefineDirectiveTrivia/UndefDirectiveTrivia` 或条件族指令残留 → CS2J5512 Error；
3. LN-2 扫描 active `ErrorDirectiveTrivia` → CS2J5511 Error（带指令文本）；LN-4 扫描 `ExternAliasDirectiveSyntax` 与左别名非 `global` 的 `AliasQualifiedNameSyntax` → CS2J5520（级别随选项）；
4. 上述任一 **Error** 存在 → 文件不改（不变式 6），仅返回诊断/统计；
5. `DirectiveRewriter`（LN-1，trivia 过滤，结构同 E01 `RemovingRewriter`，但目标表独立维护、只含 `LineDirectiveTrivia`）→ root1；
6. `UsingAliasRewriter`（LN-5/6 v1 只读遍历，产出 `GlobalUsingRecord` 列表与同文件查重诊断，不改节点）→ root2（==root1）；
7. `NamespaceRewriter`（LN-7 仅选项开）：把 `FileScopedNamespaceDeclarationSyntax` 替换为 `NamespaceDeclarationSyntax`——声明前 using 留块外、声明后 using 与成员入块，补 `{`/`}`，经 `Formatter.Format` 规范化 → root3；
8. `LexicalNormalizeVerifier`：输出重 parse 无 Error；已启用规则 residual 清零；LN-1 执行时 token 序列逐枚相等（移植 E01 `TokenSequenceEqual`，[L154-178](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L154-L178)）；LN-7 执行时改用「结构化等价」校验（见 4.5）；
9. 以 ordinal 文本比较判 `Changed`，返回结果。

### 4.4 规则全集（LN-x）

| 规则 | 输入 | 输出 | 要点/边界 |
|---|---|---|---|
| LN-1 `#line` 剥离 | active `LineDirectiveTrivia`（含 `#line default`/`#line hidden`/带文件与行号形式） | 删除该 structured trivia，摘除同行残余行尾空白 | trivia-only；token 序列必须不变（不变式 3）；`#line` 与其他指令共享 trivia 列表时只摘目标 |
| LN-2 `#error` 守卫 | active `ErrorDirectiveTrivia` | 不改写；CS2J5511 Error | 消息附指令文本与行号；不剥除（D2） |
| LN-3 条件指令限界 | 残余 define/undef/if/elif/else/endif/`DisabledTextTrivia` | 不改写；CS2J5512 Error | E01 顺序守卫（D3）；`#warning/#pragma/#nullable/#region` 不在检测表 |
| LN-4a extern alias 指令 | `ExternAliasDirectiveSyntax` | 不改写；CS2J5520（默认 Error） | D4；选项 `ExternAliasSeverity` 可降 Warning |
| LN-4b 别名限定使用点 | `AliasQualifiedNameSyntax` 且 `Alias.Identifier.Text != "global"` | 不改写；CS2J5520 站点诊断（文件:行） | `global::` 不命中（内建别名，转换器已支持，[GlobalAliasQualifiedNameTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GlobalAliasQualifiedNameTests.cs)） |
| LN-5 global using 审计 | `UsingDirectiveSyntax.GlobalKeyword != default` | 不改写；出 `GlobalUsingRecord`；同文件同文本重复 → CS2J5521 Warning | 跨文件查重在适配器 `AfterFilesAsync`（§5.1）；不解析 Name（纯文本规范化后比较：trim + 单空格压缩） |
| LN-6 using static 盘点 | `UsingDirectiveSyntax.StaticKeyword != default`（含/不含 global） | 不改写；CS2J5522 Info 计数 | D6；global static 同时计入 LN-5 模型 |
| LN-7 namespace 归一 | `FileScopedNamespaceDeclarationSyntax`（选项开启） | `NamespaceDeclarationSyntax` 块级；using 分置块内外 | D7；`extern alias` 指令只能在文件首（不进块）；多个成员/嵌套命名空间全量保留；分号与大括号 trivia 显式安置 |

### 4.5 LN-7 的结构化等价校验（不能用 token 相等）

LN-7 新增 `{`/`}` 且移动分号与 using 位置，token 序列必然不同，verifier 改为四件套：① 输出重 parse 零 Error；② 输出树零 FileScoped 节点（residual）；③ 收集两侧「全部 using 指令文本（带 in-block/in-namespace 标记）+ 全部类型/委托/枚举声明的元数据名」集合相等且成员相对序不变；④ 真编译等价由适配器 verify build/test 兜底（§5.2）。校验不过 → 回退原文 + CS2J5542 Error（内部校验失败，同 E01 post-verification 纪律，[L56-62](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L56-L62)）。

### 4.6 Options / Statistics / Diagnostics

- `LexicalNormalizeOptions`：`StripLineDirectives = true`、`NormalizeFileScopedNamespaces = false`、`ExternAliasSeverity = Error`（Info/Warning/Error 三档）、`AuditGlobalUsingDuplicates = true`、`Strict = false`（Warning→Error 升级位）、`ReportInfo = true`。**不含** `Symbols`（刻意与 E01 分界）。
- `LexicalNormalizeStatistics`：通用五字段 `FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount` + `LineDirectivesRemoved`、`ErrorDirectivesSeen`、`ResidualConditionalDirectives`、`ExternAliasDirectives`、`ExternAliasQualifiedSites`、`GlobalUsingsPlain/GlobalUsingsStatic/GlobalUsingsAlias`、`StaticUsings`、`DuplicateGlobalUsings`、`FileScopedNamespacesNormalized`、`ErrorsByCode`。
- `LexicalNormalizeDiagnostic(Severity, Code, Message, Line?)`：Info/Warning/Error 三档；Code 为 LN-x 规则号，外层打印 CS2J55xx（§6）。

---

## 5. CLI 适配器与集成

### 5.1 LexicalNormalizePassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/LexicalNormalizePassAdapter.cs`（预计 120-170 行），**走标准 spine**（不重写 `ExecuteAsync`），形态参照 [ConditionalPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ConditionalPassAdapter.cs)：

- `Descriptor => PassCatalog.ImplementedById(PassIds.LexicalNormalize)`；构造参数 `(LexicalNormalizeOptions, verbose, filterUnsupportedProjects, compatNamespacePrefixes, strict)`；
- **parse-only 快路径**：descriptor `NeedsSemanticModel=false` → runner 跳过 `BuildStandardCompilation`（[ProjectPassRunner.cs:164-174](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L164-L174)），`PassFileContext.SemanticModel` 恒为 null（[ProjectPassAdapter.cs:52-53](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L52-L53)）；每文件 Core 内部自行 parse 唯一一次；
- `RewriteFileAsync`：调 `Normalize(context.SourceCode, _options)`，诊断按 MSBuild 格式 `相对路径(行): … CS2J55xx` 收集；changed 时 `PassFileRewrite { OutputCode, Changed = true, PreserveBom = true }`；verbose 打印 transformed/clean；缓存 `GlobalUsingRecord`（带相对路径）；
- `AfterFilesAsync`：LN-5 跨文件聚合——按规范化文本对所有 `IsGlobal` 记录分组，跨文件或同文件重复即 CS2J5521 Warning（strict 升 Error）；单文件 verb 下聚合自然退化为单文件；
- `OnRestoreFailed`→CS2J5502、`OnBuildFailed`→CS2J5503、`OnTestsFailed`→CS2J5504；
- `DeleteBuildOutputsAfterRestore => true`；`HasErrors` 同 conditional 范式；
- 摘要行：`lexical-normalize: T transformed, C clean, L #line removed, E extern-alias site(s), G global using(s), F file-scoped normalized, D error(s)`；
- 无支撑库钩子、无 `OnTreePreparedAsync` 动作。

### 5.2 verify build 可行性与默认值

**可以做 build，且双 verify 默认开**。restore/verify 阶段由 spine 无条件执行（有 restore target 时），只被适配器 `VerifyBuild/VerifyTests` 门控，与 `NeedsSemanticModel` 无关（[ProjectPassRunner.cs:220-251](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L220-L251)）——E01 只是历史上选择双关（其 descriptor 未置 verify 位，[PassCatalog.cs:36-44](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L36-L44)）。C28 因 LN-7 有结构性改写、LN-1 动全文件 trivia，取最严档（双开），失败即 CS2J5503/5504，不做逐文件回退（同 enum/C16 纪律）。

### 5.3 指纹、入口面与单文件模式

- 指纹随描述符全集令牌 `pass:lexical-normalize=on/off` 自动纳管；`.cs2j-lexical-normalize-src` 由实际执行集合自动 preserve（[Program.cs:931-935](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L931-L935)）；禁用零痕迹；
- 入口：convert-project `--lexical-normalize`（默认 false）+ 通用 `--enable-pass lexical-normalize` / `--disable-pass`；独立运行走通用 `preprocess --pass lexical-normalize -s … -d …`，**不新建独立 verb**（同 C15/C16 方向）；
- LN-7 的 opt-in 经选项暴露：链上 v1 仅占位 options 默认值（不新增 convert-project 开关，避免选项膨胀）；通用 verb 增加 `--ln-normalize-file-scoped`、`--ln-extern-alias-severity` 两个 pass 专属参数（通用 verb 的 pass 参数透传机制已在，实施时按既有 verb 选项类接线）。

---

## 6. 诊断策略

MSBuild 格式：`文件(行,列): warning/error CS2J55xx: [LN-x] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5502 | Warning/Error | 中间树 restore 失败（沿用适配器历史级别） |
| CS2J5503 | Error | 中间树 `dotnet build` 失败 |
| CS2J5504 | Error | 中间树 `dotnet test` 失败 |
| CS2J5510 | Info | `#line` 已剥除计数（仅 verbose） |
| CS2J5511 | Error | 激活态 `#error` 指令残留（LN-2，文件不改） |
| CS2J5512 | Error | 残余条件指令/`#define`——conditional 必须先于本 pass（LN-3） |
| CS2J5520 | Error（选项可降 Warning） | extern alias 声明或 `X::` 使用点，纯语法 pass 无法归一（LN-4，D4） |
| CS2J5521 | Warning（strict 即 Error） | global using 跨文件/同文件重复（LN-5） |
| CS2J5522 | Info | using static / global using 分类盘点计数（LN-5/6，verbose） |
| CS2J5540 | Warning（strict 即 Error） | residual 闸门：输出树仍有应剥 `#line` 或 FileScoped 节点 |
| CS2J5541 | Info | file-scoped namespace 已归一计数 |
| CS2J5542 | Error | LN-7 内部结构化等价校验失败，文件已回退 |
| CS2J5570-5599 | — | **C43（Literals/）预留码段**，本任务不使用 |

原则（catalog §4 风险 12）：无法纯语法归一的站点（extern alias、激活 `#error`、顺序错位）显式报码，不静默透传也不注释占位。转换器无对应抛点需要收紧（本 pass 不解决 fail-fast）；`global::` 路径的既有测试不受影响。

---

## 7. 测试计划

### 7.1 Core 单测矩阵

新增 `tests/CSharpToJava.Tests/LexicalNormalizeTests.cs`（仿 [ConditionalDirectiveStripTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ConditionalDirectiveStripTests.cs) 内联源码→`Normalize` 形态）：

1. **LN-1**：`#line 100 "x.cs"`、`#line default`、`#line hidden` 三种形态 → 指令消失、token 序列逐枚相等、`LineDirectivesRemoved` 计数；与代码同行/独立行/前后带空白的 trivia 夹具；选项关闭时 Changed=false；
2. **LN-2**：含激活 `#error boom` 的源码 → CS2J5511、Changed=false、原文返回；
3. **LN-3**：含 `#define X`、`#undef Y`、`#if X` 残留源码 → CS2J5512；`#region/#pragma/#nullable/#warning` 不触发；
4. **LN-4**：`extern alias X;` → CS2J5520（Error 时不改文件）；`X::N.T` 使用点带行号诊断；`global::System.Console` **不**命中；severity 降 Warning 时其余规则照常改写；
5. **LN-5**：多文件等价通过 `GlobalUsingRecord` 在外层聚合模拟——普通/static/alias 三类计数正确；同文本重复 Warning；`global using global::System;` 与 `global using System;` 视为不同文本（不做语义归并，只做文本查重）；
6. **LN-6**：using static 不改写、Info 计数；
7. **LN-7**：`namespace A.B;` 后接 using 与类 → 块级、using 在块内；声明前 using 在块外；extern alias 留文件首；多成员/嵌套命名空间/带 XML 注释成员；产物零 FileScoped 节点且可编译；选项默认关时不改；
8. **幂等二次跑**：每条改写规则产物再跑一次零 diff；干净文件字节级一致；
9. **trivia/BOM**：指令上方注释保留、行尾空白摘除、LN-7 文件头版权注释位置正确；
10. **parse 错误输入**：畸形源码 → Error + 原文，不半改写。

### 7.2 项目级 fixture

新增 `tests/SampleLexicalNormalizeSolution/`（Lib + Tests + sln，net10.0；`Content Include` 进 ProjectTests csproj，仿 SampleForeachSourceSolution/SampleUnsignedSolution 接线）：

- Lib：含 `#line`、global using（分布在两个文件）、using static、一个 file-scoped namespace 文件（LN-7 场景）；
- Tests：普通 .NET 运行时断言，证明归一前后行为一致（namespace 内外 using 可见性、static using 成员可用性）；
- 项目级测试 `LexicalNormalizeProjectTests.cs`：`result.Success`、统计字段、中间树文本断言、**中间树 `dotnet build` 与 `dotnet test -c Release` 退出码 0**、二次运行幂等、BOM 保持、extern alias 夹具（手工 csproj `<Aliases>`）产出 CS2J5520 且失败路径无半改写文件。

### 7.3 Release build/test 与既有回归

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；三个测试工程 `dotnet test -c Release` 全绿；
- E01 回归零改动即全绿：[ConditionalDirectiveStripTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ConditionalDirectiveStripTests.cs)、[StripConditionalCliTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StripConditionalCliTests.cs)（本任务不碰 E01）；
- 转换器别名回归零改动：[UsingAliasResolutionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/UsingAliasResolutionTests.cs)、[GlobalAliasQualifiedNameTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GlobalAliasQualifiedNameTests.cs)。

### 7.4 residual 闸门

1. 扫描型测试：对输出中间树所有用户 .cs 重 parse，断言零 `LineDirectiveTrivia`（LN-1 启用时）、零 `FileScopedNamespaceDeclarationSyntax`（LN-7 启用时）；违例 CS2J5540，strict 退出非零；
2. 豁免分离：`#warning/#pragma/#region`、`global::` 使用点不计入闸门；extern alias 计数单独列出；
3. 上游共处：对经 E01 产物启用本 pass，E01 统计零变化、中间树 build/test 全绿。

### 7.5 CLI / 一致性 / 链序

- 默认关：convert-project 无 `.cs2j-lexical-normalize-src`、无耗时、链序不变；`--enable-pass lexical-normalize` 与 `--lexical-normalize` 等价；`--list-passes` 显示 Implemented/默认关/目录/`C28`；CS2J9001 不再对本 id 触发；
- 新增 `LexicalNormalizeCLITests.cs`（ProjectTests，仿 PreprocessVerbCLITests）：通用 `preprocess --pass lexical-normalize` 单文件/目录模式、LN-3 独立运行报错、strict 退出码、诊断格式、LN-7 专属参数；
- §3.4-6 三个测试文件一次改到位。

---

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足：

1. slnx Release build 全绿；三个测试工程 Release test 全绿（含 §7 全部新增项）；
2. fixture sln 中间树 build+test 全绿；幂等、字节复制、BOM、residual 闸门全绿；
3. **下游共处证据**：启用后经完整链路转换代表性工程，Java 产物与未启用时语义等价（using static/global using/file-scoped 三类各有对照样本），转换器别名测试零回归；
4. csharp-sdk（只读训练集）启用扫描：按子项分桶报表——`#line` 文件数、extern alias 命中清单（预期 20+ 中占比）、global/static using 分布、file-scoped 2 处的 LN-7 结果、CS2J5511/5512/5520/5521 逐条人工定性（真阻断 vs 分析器过保守），作为开放问题 1/2 的决策输入；
5. sdk 基线不退化：启用后抽样工程中间树 build/test 不劣于未启用；E01 及其余 pass 统计零变化；增量耗时可接受（纯语法一次 parse + 双 verify 串行成本纳入指纹缓存评估）；
6. `--list-passes`/README/ROADMAP、本设计与 plan 文档齐备；与 C43 文档的目录/码段/规则接缝完成双向核对。

未达门槛前长期默认关，每个评审周期复跑第 4/5 项。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 锚点基建 + LN-1/2/3 | `LexicalNormalize/` 骨架（Models/Preprocessor/Verifier）+ DirectiveRewriter + 适配器 + 注册表翻转（默认关、双 verify）+ CLI 开关/目录/指纹 + 三个链序测试；§7.1-1/2/3/8/9/10 | Core ~280 行，CLI ~170 行 |
| M2 using 审计 + LN-7 + 闸门 | UsingAliasRewriter（LN-4/5/6）、NamespaceRewriter（LN-7）+ 结构化等价校验 + `AfterFilesAsync` 跨文件查重 + fixture sln + §7.1-4/5/6/7、7.2/7.4/7.5 + Release build/test 全绿 | Core ~300 行 + fixture |
| M3 默认开评审 | sdk 分桶报表（§8-4）、Java 半区等价对照、与 C43 文档的 `Literals/`/码段/接缝对齐、本设计定稿 + plan 文档；门槛通过后单行翻默认开；开放问题据数据拍板 | 文档/数据 |

---

## 10. 风险

1. **LN-7 的 trivia/语义搬移回归**：file-scoped 后 using 的命名空间内外归属、分号/大括号 trivia、整文件重排缩进是本 pass 最大机械风险。缓解：默认关（D7）+ Formatter 统一 whitespace + §4.5 结构化校验 + 双 verify + XML 注释/版权头夹具；校验不过自动回退原文（CS2J5542）。
2. **extern alias 误报噪音**：sdk 20+ 命中里若存在「仅声明未使用」或自定义编译已能解析的情形，Error 默认级别可能阻断 opt-in 用户。缓解：`ExternAliasSeverity` 可降级；诊断消息明确说明 Java 侧无对应物与两条出路（移除别名引用改全限定/人工隔离）；§8-4 扫描定性后再定默认级别是否调整为 Warning。
3. **与 E01 的职责漂移**：未来有人顺手在 LN-3 补剥 `#define` 或在本 pass 加符号求值，会制造双求值真相。缓解：Options 刻意不含 `Symbols`；LN-3 只报错不动 trivia；D3 与不变式 2 写入代码注释与单测。
4. **global using 文本查重误报**：`global using global::System;` 与 `global using System;` 语义相同但文本不同（不报），反过来带不同空白的同一声明可能被规范化后误判重复——但二者本就编译冲突（CS0105 仅警告），报警与编译器方向一致，可接受；跨文件 partial 场景下重复 global using 合法且仅警告，本 pass 同级别（Warning），strict 才失败。
5. **独立 verb 双 parse 观感**：标准 spine 下文件 IO 读一次、Core parse 一次；未来若开 `ExpandPerFile` 需要两阶段模型（开放问题 2），可能破坏「只 parse 一次」承诺。缓解：v1 不实现；若实施必须走自定义 `ExecuteAsync`（一次 parse 缓存 root、聚合后改写），并补 parse 次数回归。
6. **链位热文件冲突**：与 C20（partial-merge）同处 Lexical、共用 conditional 锚。缓解：§11 次序约定 + 排序断言；本 pass 物理插位在 local-function-lift 前，不给下游加边，C20 翻转时自加其边。
7. **验证成本**：纯语法 pass 开双 verify 引入 restore/build/test 串行耗时（E01 历史双关）。缓解：指纹缓存；默认关期间零成本；M3 用实测耗时复核（§8-5）。

---

## 11. 并行协调

- **E01（conditional，已实现，锚点）**：RunAfter 锁死 `conditional → lexical-normalize`；不修改 [ConditionalDirectiveStrip/](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip) 任何文件；LN-1 的指令分类表独立维护（只含 `LineDirectiveTrivia`），不共享 E01 internal 类，避免三 pass（含 C43）共改热文件；trivia 过滤与 token 校验采用复制同源实现（同 C16 对 E07 的处置），未来合批时再提取共享。
- **C43（同 pass 观察项，姊妹文档）**：所有权槽位 = `LexicalNormalize/Literals/` 目录、CS2J5570-5599 码段、preprocessor 规则序列的追加一步、适配器选项透传；`catalogItems "C28" → "C28, C43"` 与 Description 更新由 C43 提交完成；rewriter 形状（无 IO、tree-in/tree-out）在 §4.3 已定基线；C43 的 raw **插值**字符串与 C23 的边界沿用 C23 设计已达成的约定（[c23-design:457](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L457-L457)），本 pass 不介入。
- **C20（partial-merge，同 Stage 后续）**：catalog 顺序 E01 → C28/C43 → C20 → E02；C20 设计已登记其翻转时锚 `{conditional, lexical-normalize}` 并自加 local-function-lift 边（[c20 §3.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c20-partial-merge-design.md#L85-L92)），本任务不改其 stub、不加边；两家排序断言分别在各自测试文件，行级不重叠。
- **转换器 UsingAliasRegistry 退役话题**：本任务**不启动**退役。[UsingAliasRegistry.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/UsingAliasRegistry.cs) 的语义别名解析在 LN-5 保持项目级模型后仍是别名生效的实际机制；待统一引擎合批 + 项目级 using 模型真正前置后，由独立任务评估退役，届时 [UsingAliasResolutionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/UsingAliasResolutionTests.cs)/[GlobalAliasQualifiedNameTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GlobalAliasQualifiedNameTests.cs) 是硬回归面。
- **热文件冲突解法**（同期 C16/C20 等锚点翻转）：
  - `PassStubCatalog.cs`：各任务只删自己的 stub 与常量；[PassStubCatalogTests.cs:24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24-L24) 总数断言连锁时（多家各写 23→22），**后合入方负责更新为实际余数**，不留红灯；
  - `PassCatalog.cs`/`PassChainFactory.cs`/`Program.cs`：只行级追加（描述符块、switch 分支、toggle 行、Option 属性），不改他人条目；C28 插位（conditional 与 local-function-lift 之间）与 C16 插位（foreach-deconstruction 与 strip-default 之间）行级不重叠；
  - `PassChainOrderTests.cs`：LockedChain 插点互不相同；EnabledByDefault 各家都不改；
  - 本任务 Core 产出全部位于新目录 `LexicalNormalize/`、新适配器、新测试文件，与任何在飞 pass 零文件重叠。

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c28-directive-trivia`
- 目录：`.worktrees/preproc-c28-directive-trivia`
- base：`953293b9`（已用只读 `git rev-parse --verify` 核实存在：`953293b908e9143437c45f287fb4277e890c398e`；注意当前工作区 HEAD 已前进至 `5e3e796d`，以任务指定 base 为准）
- 创建命令（仓库根、PowerShell；**本设计阶段不执行，实施阶段由执行者运行**）：

```powershell
git worktree add .worktrees/preproc-c28-directive-trivia -b feature/preproc-c28-directive-trivia 953293b9
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `LexicalNormalize/`（Models/Preprocessor/三个 Rewriter/Verifier/Model，不含 `Literals/`）、CLI 单适配器 + 注册表翻转（§3.4 清单）、本设计文档与后续 plan 文档（`docs/superpowers/plans/2026-09-29-c28-directive-trivia.md`）、Core 单测 + fixture sln + Release build/test + residual 闸门 + CLI/链序测试；
- 不新建支撑工程；不触转换器代码；不创建标签、不 push（实施阶段仍以任务授权为准）。

---

## 附：关键开放问题（供评审拍板）

1. **extern alias 的默认级别与最终出路**：CS2J5520 v1 默认 Error（D4），但 sdk 有 20+ 命中且 Java 侧完全无对应物；需 §8-4 扫描区分「真双程序集冲突（Java 不可表达，应隔离/拒转，归 C42 平台边界族）」与「单程序集别名（未来语义规则可改写 `X::`→`global::`）」。M3 据数据在「保持 Error」「降 Warning」「拆出独立语义规则」间定稿。
2. **global using 是否需要 `ExpandPerFile` 模式**：v1 保持项目级（D5）。若未来统一引擎要求逐文件自包含的中间树（内存串联、单文件可转换），需实现两阶段单 parse 自定义执行（OnTreePrepared 聚合 → 逐文件注入），并解决文件内别名优先级冲突与下游注入 using 的幂等共存；是否做、何时做由统一引擎落地节奏决定。
3. **LN-1 是否顺带处理 `#line` 的源文件映射保留物**：默认剥除后若有用户依赖中间树报错回溯原始行号，需要评估「剥除但生成 sidecar 映射」还是「选项保留」；v1 仅提供 `StripLineDirectives=false` 全保留档，无中间形态。
4. **LN-7 是否长期保持双层 opt-in**：file-scoped 在 sdk 仅 2 处且转换器原生支持；若 M3 前后均无下游 pass 提出块级唯一形态的硬需求（C20 partial-merge 的输入是 BaseNamespaceDeclarationSyntax，两种形态都吃），LN-7 可永久停留 opt-in，不进默认开门槛的必选项。
5. **pass 间词法合批试点排期**：C28 已把 rewriter 做成可组合件，但 E01 是纯文本 pass（自管 parse 与符号解析），C43 未来入同 pass；「一次 parse 合批 E01+C28+C43」涉及 E01 入口改造，属 catalog §5.4 风险 19 的统一引擎动作，需独立计划验证指纹/回滚/落盘调试三不变，不在本 pass 里程碑内。
