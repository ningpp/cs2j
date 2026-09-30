# C52 版本防线升格与低频构造收编（C# 13/14 + C# 8–12 补盘）预处理器设计

**日期**: 2026-09-29
**范围**: 把 C36 设计中的「C# 11/12 新语法防线」升格为「C# 11+（含 13/14/15 预览）版本防线」伞组件：① CLI 层**版本能力探测**（项目 LangVersion vs 所用 Roslyn 包支持上限，parse 前给出可读 CS2J 诊断，消灭「parse 层即全量失败」裸异常/裸跳过）；② 语义层**子项 checker 注册表 + 残余节点扫描**（每个子项一个小 `IGuardCheck`，随承接家族合入而移除）；③ **诊断路由表**（子项→家族条目→诊断码的单一事实源）；④ 只读 **version-audit** 审计 verb 与 markdown/json 差集清单；⑤ Roslyn 升级季差集审计方法论固化。本 pass **只拒绝/扫描/路由，不物化任何子项**（物化归各承接家族），不新增 pass id/stub、不改写产品代码。
**状态**: 设计待评审；语义层组件硬前置 C36（其分支合入后落地）；CLI 层版本探测可脱离 C36 先行（「最小 Guard 先行」策略，见 §9/§12）
**Catalog**: C52（P3，伞条目，分类 1.8 版本边界）[preprocessor-catalog.md L997-L1010](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L997-L1010)；C36 五次补全升格注记 [L778](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L778-L778)；默认分支黑洞 风险 13 [L1111](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1111-L1111)；语言版本轴错配 风险 20 [L1118](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1118-L1118)
**前置设计**: [2026-09-29-c36-collection-expr-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c36-collection-expr-design.md)（CE-GUARD 防线表 §2.5、CS2J0585-0588、盘点报告模式 §5-3——C52 是其**升格而非新通道**）；[preprocessor-foundation_plan.md L60-L139](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L60-L139)（runner/adapter/stub 契约）
**注册表现状**: stub `expression-desugar`（[PassStubCatalog.cs L217-L226](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L217-L226)，catalogItems `"C23, C24, C35, C36"`）；本设计不新增 stub；合入时 catalogItems 加宽结论见 §3.3。
**base**: `9b99c60b`（`docs: C46 成员签名显式化设计`，已 `git rev-parse --verify` 校验存在，完整 SHA `9b99c60b33980c3c351b70d6b99e6b4bf57b30ae`）；语义层实现前先合入 C36 分支。

---

## 1. 背景与目标

### 1.1 「默认分支黑洞」与语言版本轴错配

- **风险 13（既有定性）**：`static abstract` 接口成员、`scoped`、泛型 attribute 等新语法无任何专项处理，落入转换器默认分支（TODO 注释/兜底/静默丢弃），C36 已承担 C# 11/12 防线职责（CE-GUARD，[c36 §2.5 L113-L124](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c36-collection-expr-design.md#L113-L124)）。
- **风险 20（五次补全）**：全部频率证据来自 .NET 8/9 时代训练集，对 C# 13/14 特性**零样本**；既定转译目标为 .NET 10（按 C# 14 编写）。先决条件＝cs2j 的 Roslyn 包能 parse C# 14，否则「.NET 10 源码在 parse 层即全量失败」。
- **C52 伞条目定位**：子项按家族拆入 C02/C06/C10/C12/C14/C17/C20/C23/C28/C29/C43/C45/C46/C48/C49/C50/C51/E01；伞条目**只保留「版本扫描 + 显式拒绝 + 路由登记」三职责**，不实现任何子项的去糖/改写。

### 1.2 ★Roslyn 版本核实结果（2026-09-29，WebSearch 1 次 + 仓库核实）

| 核实项 | 结论 | 证据 |
|---|---|---|
| 仓库 Roslyn 包 | **Microsoft.CodeAnalysis.CSharp / .CSharp.Workspaces / .Workspaces.MSBuild 均为 4.12.0** | [CSharpToJava.Core.csproj L12-L14](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12-L14)；[conditional-plan-validation.csproj L10](file:///d:/code/cs2j/tools/conditional-plan-validation/conditional-plan-validation.csproj#L10-L10) |
| 4.12.0 语言上限 | **LanguageVersion 枚举最高项 = `CSharp13` (1300)**，特性清单含 Escape character（`\e`）、Method group natural type improvements、`Lock` object、params collections、`allows ref struct`、Partial properties | Microsoft Learn `LanguageVersion` 枚举 **roslyn-dotnet-4.12.0** 视图（已核）；即 **4.12 ≡ C# 13 世代，不识别 C# 14 GA 语法** |
| C# 14 所需版本 | Roslyn **4.14.0** 世代（同 API 浏览器列出 4.12.2 / 4.13.0 / **4.14.0**；C# 14 随 .NET 10 于 2025-11 GA，特性＝extension members、null-conditional assignment、unbound generic nameof、implicit Span conversions、simple lambda parameter modifiers、`field`、partial events/constructors、user-defined compound assignment） | Microsoft Learn「What's new / version history」（已核） |
| parse 选项现状 | 主链与多个适配器固定 `LanguageVersion.Latest`（4.12 下即 C# 13）：[ProjectCompilationBuilder.cs L30](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L30-L30)、[Program.cs L3367-L3368/L3706-L3707](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3367-L3368)、[ConditionalDirectiveStripper.cs L19-L20](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L19-L20)、JavaKeywords/RecordReadOnly/Tuple 适配器；部分适配器用 `new CSharpParseOptions()`（默认 Default=最新主版本） | grep `ParseOptions|LanguageVersion`（35 命中，16 文件） |
| parse 失败现状（★风险实证） | parse 错误文件被**逐条原始消息 Warning 后整体跳过**；全部文件失败 → 返回 null（项目静默不转换）；无任何 CS2J 码包装 | [ProjectCompilationBuilder.cs L34-L56](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L34-L56)「Skipping parse-invalid file: {Roslyn 原始消息}」；另见 [ConversionPipeline.cs L149-L151](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L149-L151) |
| 工程 LangVersion 分布 | 全仓仅 7 个工程显式声明：`latest`×3（Cs2jArray/Cs2jPrelude/WpfReflection）、**`13`**×1（Cs2jRefOutIn）、`11`×2（Cs2jUnsigned/Cs2jUnsignedMath）、`10`×1（WpfCompatBaseline）；**主转换工程（Core/CLI/Tests）未显式声明**，随 TFM 默认 | grep `LangVersion` 全仓（7 命中）；无 `Directory.Packages.props`/`Directory.Build.props` 集中版本 |
| 关键含义 | ① C# 14 语法（extension 块、`field`、`a?.b = c`、`nameof(List<>)`、partial 构造器、`operator +=`、`#:package`）在 4.12 下 parse 即报错 → 当前行为是**整文件 Warning 跳过/整项目 null**；② 一旦升级 4.14，`Latest` 静默翻为 C# 14，parse 通过但语义层新构造全部裸奔——必须先有版本防线再升包。 |

### 1.2.1 工程 LangVersion 明细与「无集中版本」事实

| 工程 | LangVersion | 与 C52 的关系 |
|---|---|---|
| [Cs2jArray.csproj](file:///d:/code/cs2j/src/Cs2jArray/Cs2jArray.csproj) | `latest` | 支撑库源码自身可用新语法；probe 须覆盖非主转换工程 |
| [Cs2jPrelude.csproj](file:///d:/code/cs2j/src/Cs2jPrelude/Cs2jPrelude.csproj) | `latest` | 同上 |
| [Cs2jRefOutIn.csproj](file:///d:/code/cs2j/src/Cs2jRefOutIn/Cs2jRefOutIn.csproj) | **`13`** | 已实际使用 V-01 `allows ref struct`（[RefStructByRef.cs L11](file:///d:/code/cs2j/src/Cs2jRefOutIn/RefStructByRef.cs#L11-L11)）——仓内首个 C# 13 特性实锤 |
| [Cs2jUnsigned.csproj](file:///d:/code/cs2j/src/Cs2jUnsigned/Cs2jUnsigned.csproj) | `11` | `>>>`（V-18）的最低语言版本；锁 11 保证支撑库可在旧编译器构建 |
| [Cs2jUnsignedMath.csproj](file:///d:/code/cs2j/src/Cs2jUnsignedMath/Cs2jUnsignedMath.csproj) | `11` | 同上（注释明示「无符号右移一律写 C# 11 的 >>>」） |
| [WpfCompatBaseline.csproj](file:///d:/code/cs2j/tools/WpfCompatBaseline/WpfCompatBaseline.csproj) | `10` | 工具工程低版本样本，probe 差集测试可用 |
| [WpfReflection.csproj](file:///d:/code/cs2j/tools/WpfReflection/WpfReflection.csproj) | `latest` | 工具工程 |
| Core/CLI/Tests 等主工程 | **未显式声明** | 随 TFM 默认；probe 必须解析「有效 LangVersion」而非只读 csproj 字面值；仓内无 Directory.Build.props 集中管控是既有事实，probe 不假设其存在 |

### 1.2.2 C52 与 C36 的职责切分（升格而非新通道）

| 面 | C36 现状（设计契约） | C52 升格后 |
|---|---|---|
| 防线范围 | C# 11/12 三项：static abstract（0585）、scoped（0586）、泛型 attribute（0587）+ 0588 防御 | C# 11+（13/14/15 预览）26 子项；C36 三项**迁入**统一注册表，码不变 |
| 组件位置 | `ExpressionDesugar/CollectionExpr/NewSyntaxGuardAnalyzer.cs`（规则集内） | 族级 `ExpressionDesugar/Shared/VersionGuard/`；CollectionExpr 只保留集合表达式职责 |
| parse 层 | 不覆盖（假设 parse 已成功） | **新增 CLI 层 parse 前探测 + parse 诊断治理**（风险 20 正面收口） |
| 家族路由 | 三项各自文字指路（companion/C06/C10） | `DiagnosticRouter` 单一事实源，14 个家族条目具名路由 + 三态码 |
| 产物 | 集合表达式改写（CE-1..CE-10/12）+ 防线盘点（CE-11） | **零改写**；审计报告 md/json、checker 随家族摘除 |
| 码段 | CS2J0570-0589 | CS2J0590-0599（顺延，不交叉占用） |

### 1.3 目标

1. **parse 前**：项目级探测「工程声明 LangVersion / 实际 parse 选项 / Roslyn 包支持上限」三者差集，输出可读 CS2J 诊断（含特性名、所属语言版本、承接家族），不允许裸 Roslyn 异常/原始 CS 消息直接流出。
2. **语义层**：对 parse 成功但转换器不能正确处理的子项做**残余节点扫描**，按四档处置显式报码或登记，消灭默认分支黑洞。
3. **单一事实源**：每个子项一条注册记录（语言版本/识别谓词/处置/承接家族/码/严重级），家族合入即摘除 checker，路由表同步翻转。
4. **纯只读**：Guard 永不改写语法树；审计默认开、重写无；可独立 `version-audit` 运行，不阻断现网（误报率为零门槛，§8）。

### 1.4 非目标

- 不物化任何子项（不做 Lock 改写、DIM 重写、file 类型改名、词法剥离实现等——全部归承接家族）。
- 不升级 Roslyn 包版本（升级是独立动作，C52 只提供升级季审计工具与 checklist）。
- 不新增 pass id / stub / 中间目录；不翻转任何既有 pass 默认态；不碰 UnsupportedDomain（CS2J3xxx）码段语义。
- C49/C50/C51 三子项**只做路由引用**，识别谓词与处置以其家族设计为准，本文件不重复展开。

## 2. 关键设计决策表

### 2.1 四档处置定义

| 档位 | 定义 | 默认严重级 | 移除条件 |
|---|---|---|---|
| **RejectNow** | 无承接家族或预览特性/直放必然错码，Guard 显式报 Error（非 strict 可降 Warning 保留） | Error（strict） | 特性被家族认领并实施后改 RouteFamily 或撤销 |
| **RouteFamily** | 有明确承接家族条目；家族未实施前 Guard 先报「已路由家族未就绪」（CS2J0592），正确性高危子项升 Error（CS2J0590） | Warning / 高危 Error | 家族 PR 同 PR 摘除该 `IGuardCheck` 并在路由表标记 Claimed |
| **DirectMap** | 与 Java 1:1 直映且转换器现状已正确处理，仅登记计数（Info），不报错 | Info | 永留为升级季回归项（行为漂移监控） |
| **LexicalStrip** | 归 C28/C43 词法批的声明层/词法子项；词法批未实施前发 Info/Warning 盘点，不阻断 | Info/Warning | 词法批认领后摘除 |

### 2.2 ★总处置矩阵（V-01 ~ V-26；识别方式均以 Roslyn 4.12 API 为基准，M0 探针复核）

| # | 子项 | 版本 | 语法 / SemanticModel 识别方式 | 处置 | 承接家族（文档锚点） | 码（§6） | 优先级 | 现状仓库证据（2026-09-29 grep） |
|---|---|---|---|---|---|---|---|---|
| V-01 | `allows ref struct` 反约束 | C# 13 | `TypeParameterConstraintSyntax` 节点 `IsKind(SyntaxKind.AllowsRefStructConstraint)`（4.12 可 parse） | RouteFamily（v1 Reject） | **C06**（ref struct 面；次邻 C07/C33 约束审计）[c06 D9 L101](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c06-unsafe-ffm-design.md#L101-L101) | 0592/高危 0590 | P2 | 转换器零命中；仅支撑库自用 [RefStructByRef.cs L9-L11](file:///d:/code/cs2j/src/Cs2jRefOutIn/RefStructByRef.cs#L9-L11)（工程 `LangVersion=13`）——证明语法可达 |
| V-02 | `System.Threading.Lock` 作为 lock 目标（`EnterScope()` lowering） | C# 13 | `LockStatementSyntax`：`SemanticModel.GetTypeInfo(stmt.Expression).Type` 全名 == `System.Threading.Lock`；对比普通对象 Monitor lowering | RouteFamily（v1 **Error**：直放必错码） | **C29** 五次/六次补全追加行 [catalog L676-L678](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L676-L678) | 0590 | **P1** | [StatementTransformer.SwitchAndResource.cs L286-L292](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L286-L292) 不区分目标类型直发 `synchronized (expr)`——Lock 目标直放＝错误 Monitor 语义；.NET 9 BCL 高频风险 |
| V-03 | `\e` ESC 字符转义 | C# 13 | 词法：`CharacterEscapeSequenceSyntax`/token `ValueText == "\x1b"`（M0 探针定具体 API；4.12 已支持，LanguageVersion 枚举特性清单在列） | LexicalStrip | **C43** LL-6 候选（设计已预留码段、开放问题 5 点名 C52 审计时归并）[c43 L36/L203/L323](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c43-lexical-literal-design.md#L323-L323) | 0593（移交后 55xx） | P3 | src 零命中（grep `EscapeCharacter` 无）；不阻断（转换器对字面量透明） |
| V-04 | method group 自然类型改进 | C# 13 | 语义：无目标类型的方法组表达式站点（`SemanticModel.GetTypeInfo`/`GetSymbolInfo` 候选集变化）；盘点 IdentifierName 实参位且 SymbolInfo 为方法组者 | RouteFamily（盘点 Warning） | **C45** D8 委托绑定（[c45 L65](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c45-closure-lift-design.md#L65-L65)）；次邻 E05 | 0592 | P3 | E05 [DelegateSyntaxRewriter.cs L115-L140](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L115-L140) 按旧规则处理，无自然类型判定；新规则主要影响**推断结果**，盘点为主 |
| V-05 | `[OverloadResolutionPriority]` | C# 13 | `AttributeSyntax` 符号名解析为 `System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute`（须 SemanticModel，防同名误判） | RouteFamily（v1 Warning：忽略特性＝潜在重载选错） | **C10** attribute-normalize（catalog 五次补全点名）[c10](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c10-attribute-normalize-design.md) | 0592 | P2 | src 零命中（grep `OverloadResolutionPriority` 无） |
| V-06 | partial 属性 / 事件（定义+实现两部件） | C# 13 | `PropertyDeclarationSyntax`/`EventDeclarationSyntax` 的 `Modifiers` 含 `PartialKeyword`；4.12 可 parse | RouteFamily（家族未实施前 Error：C11 已对 partial 属性 skip） | **C20** partial-merge（[c20 L192/L193/L260](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c20-partial-merge-design.md#L192-L193)）；C11 现状 CS2J5114 [c11 L266](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L266-L266) | 0592 | P2 | 转换器仅对 method/ctor/local 查 PartialKeyword（[DefaultParameterVerifier.cs L35-L56](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterVerifier.cs#L35-L56)），属性/事件 partial 无合并；语义层可见节点 |
| V-07 | 隐式 Span 转换（改变重载决议） | C# 14 | **语义差集**：站点 `GetTypeInfo(node).ConvertedType` 为 `Span<T>`/`ReadOnlySpan<T>` 而源表达式类型为数组/string/`T` 等，且 `ImplicitConversion` 存在（4.12 无 C# 14 新转换规则——parse 虽过，绑定结果与 C# 14 编译器不同；M0 用 4.14 探针建立形态基线） | RouteFamily（v1 **Error/Warning 分级**：重载候选差异站点 Error，纯赋值 Warning） | **C06**（Span 选型）+ **C48**（params/调用点）[catalog L1279](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1279-L1279)、[C48 L939-L951](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L939-L951) | 0590/0592 | **P1** | src 零命中；属「编译通过但选错重载」静默类，且依赖 4.14 绑定才能精确识别——v1 先做 Span 转换站点盘点（4.12 语义下可测的隐式 Span 形态） |
| V-08 | unbound generic `nameof(List<>)` | C# 14 | `InvocationExpressionSyntax` 形态 nameof 调用，实参为含 `OmittedTypeArgumentSyntax` 的 `GenericNameSyntax`（4.12 下 parse 报特性版本错 → 走 V-PARSE；4.14 后走节点扫描） | RejectNow（无家族；转换器 nameof 为内联路径） | 无家族（转换器内联 nameof 路径；登记待立项）；C02 typeof 归一覆盖的是 `typeof(List<>)` 非本项（catalog 六次补全⑤） | 0590 | P2 | 现 nameof 按普通调用识别 [InvocationExpressionTransformer.cs L37-L76](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L37-L76)，grep `OmittedTypeArgument` 零命中；C# 14 前 parse 层即拦 |
| V-09 | partial 构造器 | C# 14 | `ConstructorDeclarationSyntax.Modifiers` 含 `PartialKeyword`（4.14 起可 parse；4.12 parse 失败 → V-PARSE） | RouteFamily | **C20**（partial 成员合并；catalog C52①点名） | 0592 | P2 | [DefaultParameterVerifier.cs L46](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterVerifier.cs#L46-L46) 已能见到 partial ctor 修饰符（说明旧语法面有碰撞点）；无合并逻辑 |
| V-10 | 简单 lambda 参数修饰符（`(out r) => …`） | C# 14 | `ParameterSyntax`（父为 `SimpleLambdaExpressionSyntax`/`ParenthesizedLambdaExpressionSyntax`）无类型但带 `RefKindKeyword`/`ScopedKeyword`（4.14 起合法） | RouteFamily（v1 Error：ref/out lambda 形参无 Java 形） | **C45** D6②（已设计 **CS2J4501** Error 守门）[c45 L63](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c45-closure-lift-design.md#L63-L63)、[catalog L908](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L908-L908) | 0592（家族码 4501 就绪后让位） | P2 | src 零专门识别；家族设计已先行，checker 与 CS2J4501 做**码段避让**（Guard 只在 C45 未实施时报） |
| V-11 | 用户定义复合赋值运算符（`operator +=` 等） | C# 14 | `AssignmentExpressionSyntax`（复合 kinds）站点取 `SemanticModel.GetOperation` 为 `ICompoundAssignmentOperation`，其 `OperatorMethod` **非 null**（显式复合运算符）；区别于空（拆解为 `op_Addition`+赋值） | RouteFamily（v1 **Error**：拆解式改写对显式复合运算符错语义） | **C14**（运算符声明物化）+ **C17**（复合目标）+ **C51**（复合空条件形态）[catalog L470](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L470-L470)、[c17](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md) | 0590 | P2 | src 无显式复合运算符判定（grep `CompoundAssignment` 仅 E09 方法表/自写赋值检测）；4.12 下声明站点 parse 即拦、调用站点 4.14 起可查 |
| V-12 | 文件级应用指令 `#:package` / `#:sdk` | C# 14 | trivia：结构为 directive trivia（4.14 下的具体 `SyntaxKind` 由 M0 探针确定；候选 Load/Shebang/新 kind）；4.12 下形态待测 | RouteFamily（v1 Warning：指令被保留/忽略，包依赖不生效） | **E01** conditional-directive-strip（[2026-09-24 设计](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-24-conditional-directive-strip-design.md)） | 0592 | P2 | [ConditionalDirectiveStripper.cs L84-L86](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L84-L86) 明确把 `LoadDirectiveTrivia`/`ShebangDirectiveTrivia` 列入**保留**（=>false）——**不识别 `#:package`** |
| V-13 | extension 块（扩展属性/运算符/静态扩展） | C# 14 | `ExtensionDeclarationSyntax`（4.14 节点类型；4.12 无此类型→parse 失败 V-PARSE） | RouteFamily（**引用不重复**：识别/处置细节归 C49；Guard 仅在 C49 未实施且包已升 4.14 时残余报码） | **C49**（catalog L953-L966，**spec 尚未写**，仅 catalog 条目） | 0592 | P1（随 .NET 10 基线） | grep `ExtensionDeclaration` 零命中；风险 20 首要项；本文件不展开处置矩阵 |
| V-14 | `field` 上下文关键字（半自动属性） | C# 14 | 访问器体内 IdentifierName 绑定为合成 backing field（`SemanticModel.GetSymbolInfo` → field kind `IsImplicitlyDeclared`；4.14 起） | RouteFamily（引用不重复） | **C50**（catalog L968-L980；随 C11 批；C11 已有 CS2J5122 码位预留迹象） | 0592 | P2 | grep `FieldKeyword/FieldExpression` 零命中；细节归 C50 |
| V-15 | 简单空条件赋值 `a?.b = c`（含复合形态） | C# 14 | `AssignmentExpressionSyntax` 左侧为 `ConditionalAccessExpression`/`ConditionalAccessExpression` 路径（4.14 起合法） | RouteFamily（引用不重复） | **C51**（catalog L982-L995；C35/E03 家族追加规则） | 0592 | P3 | C35 只覆盖读取链；细节归 C51 |
| V-16 | 扩展索引器（extension block 内 indexer） | **C# 15 预览** | 同 V-13 节点族内索引器成员 + 预览特性诊断（Roslyn 预览标记/`FeatureInPreview`）；4.12/4.14 GA parse 均不接受 | **RejectNow**（显式拒绝，禁止误解析/静默跳过） | 无（未来归 C49 扩展面；在 GA 前保持拒绝） | 0590 | P2 | 不可达语法，但必须在 parse 错误治理中给出**具名**诊断而非「Skipping parse-invalid file」（catalog L966/L1010 已核） |
| V-17 | DIM 完整面（非 public 默认实现、默认属性/事件、菱形最具体重实现 `I.M()`） | C# 8+ | ① 接口成员声明带 body 且修饰符含 private/protected/internal（`BaseMethodDeclarationSyntax` + 含访问修饰符）；② 接口内 property/event 带实现；③ 类中方法带 `ExplicitInterfaceSpecifier` 且为重新实现（配合 C46 InterfaceMappingIndex 歧义判定）；Java default 方法仅覆盖 **public 实例方法**子集 | RouteFamily（v1：private/protected/internal 默认实现与菱形重实现 **Error**；public 实例方法体不重复报） | **C46**（成员签名收口，SI-4 菱形 CS2J5197）[c46 L132/L205/L242/L294](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c46-member-signature-design.md#L294-L294) | 0590（歧义归 5197 不抢码） | P2 | [InterfaceTransformer.cs L159-L165](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs#L159-L165) 仅按「有 body 且非 static」加 Java `default`——**不判访问修饰符/菱形/默认属性事件**，非 public 直放产物非法 |
| V-18 | `>>>` 无符号右移（含 `>>>=`） | C# 11 | `SyntaxKind.UnsignedRightShiftExpression` / `UnsignedRightShiftAssignmentExpression` | **DirectMap**（内置操作数已 1:1；仅登记升级季回归） | 无（C41/E09 族已吸收；用户自定义 `op_UnsignedRightShift` 归 C14，checker 排除显式绑定站点） | 0593 Info | P3 | **已核实 1:1**：[BinaryExpressionSupport.cs L46](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Utilities/BinaryExpressionSupport.cs#L46-L46) 直映 `">>>"`、[ExpressionWriter.cs L38](file:///d:/code/cs2j/src/CSharpToJava.Core/Java2/CodeGen/ExpressionWriter.cs#L38-L38)、`>>>=` 拆解 [MutationSiteRewriter.cs L957/L974](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs#L957-L974)、自定义运算符映射 [OperatorTransformer.cs L48/L85](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs#L48-L85) |
| V-19 | NRT 引用类型 `T?` 注解声明层剥离 | C# 8 | 声明层 `NullableTypeSyntax` 且元素类型经 SemanticModel 判定为**引用类型**（区别于值类型 `int?`）；与 `#nullable` 指令（E01 现行白名单保留、未剥）和 `x!`（已透明剥离）划界 | LexicalStrip（盘点 Warning；勿误剥值类型 `?`） | **C28/C43 词法批**（catalog L1199 点名）[c28](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c28-directive-trivia-design.md) | 0593/0592 | P3 | `#nullable` 在 [ConditionalDirectiveStripper.cs L75-L88](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L75-L88) 指令白名单中保留（`IsRemovedDirective` =>false，未剥）；`x!` [UnaryExpressionTransformer.cs L54](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L54-L54)；`NullableTypeSyntax` 仅零散 unwrap（LinqRewriter/GenericDeepen 等 7 文件），**无声明层统一剥离** |
| V-20 | `file` 局部类型（C# 11 `file` 修饰符） | C# 11 | 类型声明 `Modifiers` 含 `SyntaxKind.FileKeyword`（**注意与 `FileScopedNamespaceDeclarationSyntax` 是两个特性**，识别不得混用） | RouteFamily（v1 Error：Java 无文件局部可见性，需配合改名，直放泄露符号） | **C28**（词法/可见性盘点）+ **C08**（E17 改名链承接最终改名） | 0590 | P2 | grep `FileKeyword|file class` 零命中；仅 file-scoped namespace 处理（[KeywordRenamingRewriter.cs L239-L241](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/KeywordRenamingRewriter.cs#L239-L241)）——反向证据证明易混淆 |
| V-21 | 特性目标限定符 `[field:]`/`[return:]`/`[assembly:]` 等 | 全部（老特性，补盘） | `AttributeListSyntax.Target`（`AttributeTargetSpecifierSyntax.Identifier`）；分类 field/return/assembly/module/event/param/property | RouteFamily（v1 Warning：限定符影响特性落点，现转换器静默丢特性） | **C10**（attribute 归一）；`[field:]` 与 **C50** 半自动属性交互（点名） | 0592 | P3 | grep `AttributeTargetSpecifier` 零命中（转换器无识别）；assembly 级特性另有 AssemblyInfo 跳过惯例（[UnsupportedDomainPasses.cs L369-L371](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L369-L371)），checker 须排除该生成文件 |
| V-22 | 别名限定符 `global::` / `X::`（extern alias 表达式形态） | 全部（补盘） | `AliasQualifiedNameSyntax`，`Alias.Identifier.Text == "global"` vs 其它（extern alias）；配合 `ExternAliasDirectiveSyntax` | 分档：`global::` **DirectMap**（已支持，Info）；非 global 的 `X::` RouteFamily（C28 已设计 Error） | **C28** LN-4a/4b（[c28 L205-L206/L262/L283](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c28-directive-trivia-design.md#L205-L206)，CS2J5520） | 0593 / 0592（让位 5520） | P3 | `global::` 已直映并有测试（ExpressionTransformerRegistry.cs L156-L159、GlobalAliasQualifiedNameTests）；extern alias 表达式面与 LN-4b 同一检查，Guard 不抢码 |
| V-23 | `delegate*` 函数指针（含 `unmanaged[Cdecl]`、`&M`） | C# 9 | `FunctionPointerTypeSyntax`；符号 `IFunctionPointerTypeSymbol`；`&` 取方法地址 + 间接调用 | RouteFamily（v1 Error：指针类型无 Java 形；C06 已定 UF021 拒绝） | **C06** R5/D6（五次补全子项）[c06 L98/L163/L200](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c06-unsafe-ffm-design.md#L200-L200)；delegate preprocessor 已声明不改写（[2026-09-26-delegate L164/L562](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-26-delegate-preprocessor-design.md#L164-L164)） | 0592（让位 UF021） | P2 | 语法/符号面仅 3 处**防御性分类**：[TupleShapeRegistry.cs L318/L373](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleShapeRegistry.cs#L318-L373)、[StateMachineBuilder.cs L238](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L238-L238)、[LocalFunctionLiftRewriter.cs L607](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs#L607-L607)——无转换路径 |
| V-24 | const 内插字符串（`const string s = $"a{1}"`） | C# 10 | `InterpolatedStringExpressionSyntax` 位于 const 声明初始值/默认参数/特性实参/常量 case，且 `SemanticModel.GetConstantValue(node).HasValue` | RouteFamily（家族设计**已覆盖**：IE-FOLD 折叠；checker 仅在 C23 未实施时盘点） | **C23** IE-FOLD（[c23 L52/L78/L103/L136](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L78-L78)，CS2J0503） | 0593（让位 0503） | P3 | 主转换器无专门路径；C23 落地即摘除 checker——**首个「设计已就绪」路由样例** |
| V-25 | lambda 参数默认值 | C# 12 | `ParameterSyntax`（lambda 形参）带 `Default`；父为 lambda/匿名方法 | RouteFamily（v1 Warning：默认值在委托调用点不生效） | **C12** indexer/参数族（member-desugar 通道；罕见）；与 E08 默认值剥离协同 | 0592 | P3 | src 零命中（grep lambda 默认值无）；[DefaultParameterDeclarationRewriter.cs L1348](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs#L1348-L1348) 常量上下文枚举均为声明成员，不含 lambda |
| V-26 | 泛型静态字段逐封闭类型实例化语义（`C<int>.x` 与 `C<string>.x` 独立） | 全部（补盘） | 泛型类型中 `static` 字段声明（经 SemanticModel 取 `ContainingType.IsGenericType` 与字段类型是否引用类型参数分桶）；静默错值族 | RouteFamily（v1 Error：Java 擦除后静态共享＝计数器/缓存静默错值；转换器已有 fail-fast 即锚点） | **C02** 静态字段规则（[c02 行 8 L26](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c02-generic-deepen-design.md#L26-L26)；[catalog L192-L195/L294](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L294-L294)） | 0590（与既有 fail-fast 互认不重复） | P2 | 转换器已 fail-fast：[FieldTransformer.cs L112-L118](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L112-L118)「normalization required: static field referencing enclosing type parameters」；Guard 做**链上提前/统一码**盘点 |
| V-PARSE | （机制行）版本能力/parse 层失败治理 | 机制 | 项目 LangVersion 探测 + parse 诊断中「feature not available in language version」类（CS8022–CS8059/CS9xxx 特性版本族）与残余 parse error 归类 | 机制（非子项） | 本组件自承（§4 `VersionCapabilityProbe`） | **0591/0596** | **P1** | [ProjectCompilationBuilder.cs L34-L56](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L34-L56) 裸跳过；特性 CS 码→CS2J 码的精确映射表 M0 探针建立 |

**矩阵统计**：子项行 26（V-01–V-26）+ 机制行 1（V-PARSE）＝**27 行**。分档：RejectNow **2**（V-08、V-16）；RouteFamily **21**（其中 v1 即 Error 的高危 5：V-02、V-07、V-11、V-17、V-20；V-26 与既有 fail-fast 互认）；DirectMap **2 项次**（V-18、V-22 的 `global::` 分支；按行计 1 行）；LexicalStrip **2**（V-03、V-19）。引用不重复行 3（V-13/V-14/V-15，均 RouteFamily 占位）。

### 2.3 版本能力检测策略：三结合

| 层 | 时机 | 组件 | 能发现 | 不能发现 |
|---|---|---|---|---|
| A. parse 前探测 | CLI 层、项目级、**整批 parse 之前** | `VersionCapabilityProbe` | 工程声明 LangVersion（MSBuild 模型/csproj）、parse 选项有效值、Roslyn 包支持上限（4.12→CSharp13）、低版本工程使用高版本特性的 CS 特性诊断、**完全 parse 不动的 C# 14/15 语法** | 语义差异（隐式 Span 重载变化） |
| B. parse 错误治理 | parse 之后、入 compilation 之前 | 诊断包装器（A 的子步骤） | 把「feature not available」类诊断翻译为 CS2J0591（含特性名/版本/家族）；把残余 parse error 包成 CS2J0596；**消灭裸 Warning 跳过** | —— |
| C. 语义节点扫描 | pass 内、与 C36 同阶段（有 SemanticModel） | `LanguageVersionGuard`（catalog 名称 `CSharp13PlusGuard`）+ `IGuardCheck` 注册表 | parse 通过但错译/漏译/静默错值的全部子项（矩阵 V-01–V-26） | 包都 parse 不了的语法（由 A/B 兜底） |

要点：**A 是 B 的前置，C 是 A/B 之后的兜底**；三层共用同一份 `DiagnosticRouter` 路由表，禁止各自硬编码码与家族映射。

### 2.4 preview 特性滚动策略（升级季方法论固化）

1. **单一事实源**：`DiagnosticRouter`（代码内注册表）为子项事实源；机器可读基线文件 `docs/superpowers/version-guard/csharp-feature-inventory.json`（字段：id/名称/引入版本/GA 或预览/识别 kinds/处置/家族/码/状态）由单测与注册表**双向一致断言**。
2. **升级季流程**（每次 Roslyn 包升级或目标 TFM 升级必跑）：① 升包前先跑 `version-audit` 存基线报告；② 升包后用新 `ParseOptions(Latest)` 重跑全部 V-xx snippet 与 .NET 10 抽样工程；③ 对照官方「What's new in C# n」特性全集做**差集**（新特性=新 checker 行 RejectNow 起步；GA 转正的预览项翻转状态）；④ 产出 `docs/superpowers/version-guard/audits/YYYY-MM-roslyn-X.Y.Z.md` 评审；⑤ 注册表/inventory/审计快照三者一致才允许合入升包 PR。
3. **未知兜底**：任何匹配「新版语法可疑形态」但不在注册表的节点 → CS2J0594（Warning，非阻断；strict Error），消息固定为「疑似新版本语言构造，请升级 cs2j 或登记 C52 差集」，附节点 kind 与源码片段——**宁可多一条 Warning，不允许静默**。

### 2.5 识别共性与误判规避细则（checker 编写纪律）

| 细则 | 内容 | 反例/规避 |
|---|---|---|
| SY-1 语义优先 | 凡同名特性/自定义类型可伪造的子项（V-02 Lock、V-05 OverloadResolutionPriority、V-07 Span、V-11 显式复合运算符、V-14 field、V-26）必须 SemanticModel 全名/IOperation 判定，禁止文本/kind 单条件报 Error | 用户自定义 `class Lock`、自定义同名 attribute 不得命中 |
| SY-2 符号缺失降级 | 单文件模式/引用不全导致 `SymbolInfo` 为 None 时，Error 类 checker 一律降 Warning（消息注明「符号未解析」），Info 类正常计数 | 不得因工程引用缺失产生假 Error（R1） |
| SY-3 kind 版本守卫 | 4.12 不存在的节点类型（ExtensionDeclaration 等）以 SyntaxKind 原始值/反射访问，禁止直接引用 4.14 类型导致编译失败；谓词在旧包上恒 false 且发 0595 留痕 | check 程序集必须在 4.12 编译通过 |
| SY-4 形态同族去重 | 同一节点可能被多 checker 命中（如 `a?.b += c` 同时属 V-11/V-15；`[field: NonSerialized]` 属 V-21 且与 V-14 同属性）：按「最具体子项优先」归一个 check 报码，其余以 Info 关联，不在同位置发多条 Error | 路由表声明 `PrimaryCheck`/`SeeAlso` 字段 |
| SY-5 生成文件排除 | obj 生成 AssemblyInfo（V-21 assembly 目标）、设计器生成代码沿用既有 `IsGeneratedAssemblyInfoFile` 排除惯例；排除仍计数（Info）不静默 | [UnsupportedDomainPasses.cs L369-L381](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L369-L381) |
| SY-6 表达式树/动态 | checker 在 `Expression<>` 树内、dynamic 绑定站点的行为逐子项声明（多数版本特性基线即在表达式树内非法）；共享 C23/C24 的 `IsInExpressionTree` 判定，不自造 | catalog 风险 8 |
| SY-7 parse 层不重复 | 凡在当前包 parse 层必然失败的子项（4.12 下的 V-08/V-09/V-13/V-16 等），节点 checker 注册但标注 `RequiresMinVersion=4.14`，当前版本不挂载，只由 A/B 层负责；升包后自动生效 | 避免「parse 都没过却扫不到」的困惑 |
| SY-8 支撑库豁免 | src/Cs2j* 支撑库工程是 cs2j 自身产物（含 V-01 自用、V-18 大量 `>>>`），审计输入默认为**被转换目标工程**；扫支撑库仅在显式 `--include-support-libs` 时进行 | 防止自扫产生噪声 Error |

### 2.6 Error 白名单（默认链可发 Error 的完整集合；§8-3 裁决对象）

| check | 入白理由 | 降级条件 |
|---|---|---|
| V-02 Lock 目标 lock | 直放 `synchronized` 必错（Monitor 与 EnterScope 不可混用），且无法在 Java 侧侥幸工作 | 符号未解析（SY-2）、非 strict |
| V-16 C# 15 预览扩展索引器 | GA 前无确定语义，误解析风险最高 | 始终 Error（strict）；非 strict Warning |
| V-17 非 public DIM / 菱形重实现 | Java 语法层非法或语义歧义，产物必不可编译/错分派 | public 实例默认方法不属白名单（V-17 子集放行） |
| V-20 `file` 局部类型 | Java 无可见性对应，直放泄露符号并引发跨类型碰撞 | 符号判定为 file-scoped namespace 时绝不命中 |
| V-11 显式复合运算符站点 | 拆解式翻译调用错误方法族（错值） | OperatorMethod 解析缺失时降 Warning |
| V-07 Span 重载差集高危站点 | 静默选错重载；但 4.12 无法精确判定，**M2 仅对可证形态入白**，其余随 4.14 复核（R6） | —— |
| V-26 泛型静态字段（引用类型参数） | 静默错值；转换器已有 fail-fast 锚点，Guard 提前统一码 | 不引用类型参数的静态字段不命中 |

白名单外全部子项首版仅 Warning/Info；白名单每项配一条「现网零命中」核实记录后方可随 M2 默认开。

## 3. 注册表与链位契约

### 3.1 归属与目录

- 语义层：升格 C36 的防线组件——新建族级目录 `src/CSharpToJava.Core/ExpressionDesugar/Shared/VersionGuard/`（C36 的 `NewSyntaxGuardAnalyzer`（static abstract 0585/scoped 0586/generic attribute 0587）在 C36 合入时作为**首批迁入的 `IGuardCheck`**，码保持 CS2J0585-0587 不变；迁移＝只增移动，不改码语义）。
- CLI 层：`src/CSharpToJava.CLI/Preprocessing/VersionGuard/`（probe、parse 诊断包装、version-audit verb 处理），**不依赖 expression-desugar pass 翻转**，可独立先行。
- **零新 stub、零新 pass id、零新中间目录**；Guard 不产文件。

### 3.2 Guard 执行时机（两层）

1. **第一层（CLI，最早）**：`VersionCapabilityProbe` 在 runner 整批 parse 前运行（插入 [ProjectPassRunner.cs L139-L145](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L139-L145) `BuildStandardCompilation` 之前的可选钩子 + convert-project 主链 [ProjectCompilationBuilder.cs L26-L56](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L26-L56) 治理点），项目级一次；纯只读，失败只发诊断不改后续行为（除非 strict）。
2. **第二层（pass 内，语义阶段）**：`LanguageVersionGuard` 随 expression-desugar 族 `Analyze` 阶段每文件一次运行（与 C36 防线同遍，复用族 SemanticModel 包装），触发条件＝注册表任一 checker 的 dirty 语法 kind 命中。

链位示意（★＝C52 新增/升格点，其余为既有链）：

```text
convert-project / version-audit
  │
  ├─★ VersionCapabilityProbe（CLI，项目级，parse 前；0591/0595/0596）
  │      └─ 整批 ParseText（既有 ProjectPassRunner/ProjectCompilationBuilder）
  │
  ├─ E01 strip-conditional → … 既有链 … → expression-desugar（C23/C24/C35/C36）
  │                                              │
  │                                              └─★ Analyze 阶段：
  │                                                   CollectionExpr（CE-GUARD 迁入）
  │                                                   + LanguageVersionGuard（V-01..V-26 残余扫描；0590/0592/0593/0594）
  │
  └─★ AuditReport（md/json；只读，无 Rewriter、无中间目录）
```

### 3.3 catalogItems 结论

- **建议加宽但不新增 pass**：C52 合入时把 `expression-desugar` 描述符 catalogItems 由 `"C23, C24, C35, C36"`（rebase 基线实际值取并集）加宽为 **`"C23, C24, C35, C36, C52"`**。理由：C52 交付真实 Guard 组件与诊断码（非纯文档伞条目），`--list-passes` 可见归属；不加宽的备选（保持「…,C36」伞下）也可接受，若评审认为伞条目不应出现在实施描述符中，则只在 C36 catalogItems 描述文本内注明「含 C52 升格」。**倾向前者**，与 C36 加宽先例一致。
- 本设计阶段不触碰 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)。

### 3.4 与家族 pass 的关系：兜底不重复报码

1. **判定顺序＝家族在前、Guard 在后扫残余**：checker 只扫描树中**仍存在**的目标节点；家族合入后目标语法已消失（partial 合并、去糖、词法剥离）或判定谓词主动让位（见 3）。
2. **checker 随家族合入移除（主机制）**：家族 PR 必须同 PR 删除对应 `IGuardCheck`、把路由行状态翻为 Claimed，并更新 inventory.json；注册表单测断言「Claimed 家族已 Implemented 但 checker 仍存在」即失败——**用机制保证不重复报**，不靠人工记忆。
3. **已处理标记/谓词（辅助机制）**：对码段已被家族设计占用的子项（V-10→CS2J4501、V-22→CS2J5520、V-23→UF021、V-24→CS2J0503），checker 内置 `SuppressedByFamilyCode`：家族 pass 在链上已启用时（descriptor Implemented + 本次执行计划含该 pass）Guard 不发码，仅 Info 计数；未启用时才发 0592。
4. **链位差容忍**：承接家族晚于 expression-desugar（如 C06/C20 实际链位）时，Guard 扫描点节点仍在——此类子项默认只发 CS2J0592 Warning（已路由未就绪），只有 V-02/V-07/V-11/V-17/V-20 等「直放必错」清单经 strict 升级 Error；避免在链中段阻断。
5. Guard 自身**永不进 residual 闸门的改写配对**（无改写即无残余），但输出「节点数 vs 诊断数」自洽断言（每类命中必有一条诊断/计数）。

## 4. Core 组件设计

语义层目录 `src/CSharpToJava.Core/ExpressionDesugar/Shared/VersionGuard/`，命名空间 `CSharpToJava.Core.ExpressionDesugar.Shared.VersionGuard`（族 `Shared` 由 C23 锚点所有，本组件只增新子目录与注册接缝）。

| 文件 | 职责 |
|---|---|
| `VersionGuardModels.cs` | `VersionGuardCheckId`（VG-01..V-26 常量）、`FeatureIntroducedVersion`（CSharp11..CSharp15Preview 枚举）、`GuardDisposition`（RejectNow/RouteFamily/DirectMap/LexicalStrip）、`GuardFinding`（节点位置、check id、处置、家族条目号、建议码、严重级、特性名）、`GuardRouteState`（Unsupported/UpgradeRequired/RoutedPending/RegisteredClaimed 三态+1） |
| `IGuardCheck.cs` | `string Id`；`ImmutableArray<SyntaxKind> TriggerKinds`（cheap dirty 扫描）；`LanguageVersion MinVersion`；`GuardDisposition Disposition`；`string FamilyItem`（"C29" 等）；`string SuppressedByFamilyCode`（可空）；`IEnumerable<GuardFinding> Detect(SyntaxNode root, SemanticModel model, GuardCheckContext ctx)`；要求每个 checker ≤ 80 行、纯读 |
| `GuardCheckContext.cs` | SemanticModel 包装、parse 选项/有效语言版本、本次执行计划含哪些家族 pass（供 §3.4-3 抑制判定）、strict 标志、去重 `HashSet<(check,location)>` |
| `LanguageVersionGuard.cs` | 节点扫描器：dirty kind 并集过滤 → 注册表 checker 顺序执行 → 汇总 findings → 经 `DiagnosticRouter` 翻译成诊断；贡献族统计 partial；**不持有 rewriter、不产出 SyntaxNode 变更** |
| `Checks/AllowsRefStructCheck.cs` …（26 个一文件一子项，引用行 V-13/14/15 用薄占位 checker，仅 kind 命中+路由） | 矩阵 §2.2 的逐子项实现；C36 迁入的 StaticAbstractCheck/ScopedCheck/GenericAttributeCheck 落同目录（码 0585-0587） |
| `DiagnosticRouter.cs` | **单一事实源**：check id →（默认码、strict 码、家族、家族码、help 链接锚点）；`Map(GuardFinding, options) → PassDiagnostic`；Claimed 状态查询；与 inventory.json 的一致性由单测锁定 |
| `VersionGuardStatistics.cs` | `FindingsByCheck`（26 桶）、`ByDisposition`、`Errors/Warnings/Infos`、`SuppressedByFamily`、`ParseLayerFeatureDiagnostics`、`ProjectLangVersion`、`RoslynLanguageVersionCeiling` |
| `VersionGuardDiagnostics.cs` | CS2J0590–0599 常量与消息模板（§6） |

CLI 层目录 `src/CSharpToJava.CLI/Preprocessing/VersionGuard/`：

| 文件 | 职责 |
|---|---|
| `VersionCapabilityProbe.cs` | 读项目有效 LangVersion（MSBuild 工程模型/ParseOptions）；探测当前包 `LanguageVersion` 枚举上限（反射 `Enum.GetValues<CSharp.LanguageVersion>()` 最大值，硬编码兜底表）；对每文件做**一次廉价 parse**（复用传入 parseOptions，不额外造 compilation）收集语法诊断并分类 |
| `FeatureDiagnosticClassifier.cs` | 把 Roslyn 诊断分三类：① 特性版本类（CS8022 系列/CS9xxx 特性门，M0 建立精确 id 清单）→ CS2J0591 + 从消息/位置反查特征名映射到 V-xx；② 其他 parse error → CS2J0596（包装，保留原 CS 码与位置）；③ 非 error → 忽略 |
| `VersionAuditReport.cs` | markdown（人读：项目/版本/分桶/路由/家族就绪态/升级建议）+ json（机读：与 inventory.json 同 schema 扩展 findings 段）双产物；默认输出 stdout，`--report-out` 落盘 |
| `VersionAuditCommand.cs` | 独立 verb `version-audit` 接线（只扫描不改写、不建中间目录、不 restore） |

### 4.1 执行序列（语义层，每文件一次）

1. **dirty 快扫**：对 root 做 `DescendantNodes` 惰性枚举，按注册表全部 checker 的 `TriggerKinds` 并集 + trivia 白名单（V-12/V-16）判定；无命中即 clean 返回（零分配 findings，族 Changed=false 语义不受影响——Guard 本来就不改树）。
2. **上下文构造**：`GuardCheckContext`（SemanticModel、有效 LanguageVersion、执行计划家族集合、strict、SY-5 排除谓词、SY-8 支撑库标志）。
3. **checker 顺序执行**：按注册表固定序（V-01..V-26，C36 迁入三项置于尾部保留原 0585-0587 语义序）；SY-7 版本守卫不满足的 checker 整体跳过并计数 `SkippedByVersion`。
4. **SY-4 去重归并**：同位置多命中按 PrimaryCheck/SeeAlso 归并为一条主诊断 + 关联 Info。
5. **路由翻译**：`DiagnosticRouter.Map` 按 §3.4 抑制规则与严重级策略产出 `PassDiagnostic`；同 key 去重。
6. **统计与报告**：partial 累加 `VersionGuardStatistics`；AfterFiles 阶段汇总 markdown/json；**不注册任何 Rewriter，不返回替换节点**（VG-8 断言引用相等）。

CLI 层序列：工程模型加载 → 有效 LangVersion 解析 → 包上限反射 → 一次 parse 取诊断 → `FeatureDiagnosticClassifier` 三分类 → 0591/0595/0596 诊断流 + 项目级摘要；`version-audit` 额外触发语义层独立 host（轻量 compilation，不经过 pass 链）以便无 expression-desugar 时也能出全量报告。

### 4.2 规则全集（VG-1 ~ VG-10）

- **VG-1 能力探测**：项目 LangVersion / parse 选项 / 包上限三值采集与差集（CS2J0595 Info 汇总）。
- **VG-2 parse 诊断治理**：特性版本诊断→0591；未知 parse error→0596；禁止裸 Roslyn 消息直接作为最终输出。
- **VG-3 子项残余扫描**：26 checker 注册、dirty kind 过滤、逐文件一次。
- **VG-4 路由三态判定**：Unsupported（0590）/ UpgradeRequired（0591）/ RoutedPending（0592）；DirectMap/LexicalStrip 走 0593。
- **VG-5 家族去重**：§3.4 移除机制 + SuppressedByFamilyCode + 注册表/家族状态一致性断言。
- **VG-6 未知 preview 兜底**：可疑新语法形态（无法识别的顶级声明/成员/运算符 token/指令 trivia）→0594。
- **VG-7 审计报告**：md/json 差集清单，含「已认领不计缺口」排除清单（同 C36 盘点报告模式：`x!`、`#nullable`、primary constructor、raw/u8 等逐项列来源）。
- **VG-8 只读不变式**：Guard 无 Rewriter、输出树与输入树引用相等（断言），Changed 恒 false。
- **VG-9 升级季差集**：inventory.json ↔ 注册表 ↔ 官方特性全集三方对账（§2.4）。
- **VG-10 直映回归监控**：V-18/V-22(global::) 等 DirectMap 项在每次升级季重跑 snippet，行为漂移即升级为 RouteFamily。

### 4.3 M0 探针清单（M1 必须实证，禁止凭记忆写 checker）

| # | 探针内容 | 载体 | 结论落点 |
|---|---|---|---|
| P1 | 4.12 `LanguageVersion` 枚举实测最大值＝CSharp13；各 parse 入口（Latest/Default/new()）有效值 | 反射单测 | 已 WebSearch 核实，单测锁死防升包静默翻转 |
| P2 | 逐 V-xx snippet 在 4.12 下 parse：可 parse 的记录 SyntaxKind/节点类型；失败的记录**完整 CS 诊断 id + descriptor**（特性版本族 vs 普通 parse error） | 探针控制台 | FeatureDiagnosticClassifier 映射表；V-PARSE 行 |
| P3 | `#:package`/`#:sdk` 在 4.12 下的 trivia 结构（Load/Shebang/DisabledText/未知 directive 之何者）与 4.14 预期 kind 差异 | 探针 + 官方语言参考 | V-12 识别谓词 |
| P4 | `\e` 的 token/节点外形与 ValueText（`\u001b`） | 4.12 单测（特性已在枚举清单） | V-03 |
| P5 | `lock` 语句对 `System.Threading.Lock` 目标在 SemanticModel 上的可判定信息（表达式类型符号全名即可，无需绑定 body） | 4.12 单测 + .NET 9 引用 | V-02 |
| P6 | 复合赋值 `ICompoundAssignmentOperation.OperatorMethod` 在普通 `+=`（null）与自定义 `operator +=`（非 null，需 4.14 语法或手工构造 operation 比对）的差异 | 4.12 可测 null 路径；非 null 路径标注 4.14 复核 | V-11/R6 |
| P7 | 4.12 既有隐式 Span 转换站点（数组→ReadOnlySpan 形参）的 ConvertedType/Conversion 外形，作为 V-07 粗扫基线；新转换（string→Span<byte> 等）列 4.14 差集 | 4.12 单测 | V-07 |
| P8 | DIM 三形态符号面：非 public 带体接口方法、接口属性/事件带实现、显式重实现 `I.M()` 的语法+符号特征；与 C46 InterfaceMappingIndex 设计的复用面 | 4.12 单测（DIM 自 C# 8 可 parse） | V-17 |
| P9 | `file` 修饰符的 SyntaxKind（FileKeyword，C# 11 已支持）与 file-scoped namespace 同文件反例 | 4.12 单测 | V-20 |
| P10 | 特性目标限定符七种 target 的 AttributeTargetSpecifier 外形；AssemblyInfo 生成文件样本 | 4.12 单测 | V-21/SY-5 |
| P11 | const 内插 `GetConstantValue().HasValue` 在 const 声明/默认参数/特性实参/常量 case 四上下文（与 C23 IE-FOLD 探针结论对齐，引用不重做） | 复用 C23 探针 | V-24 |
| P12 | 泛型静态字段：`ContainingType.TypeParameters` 与字段类型引用关系的判定 API，复现 FieldTransformer 既有 fail-fast 输入 | 4.12 单测 | V-26 |
| P13 | 现有 parse 失败端到端行为录像：构造超版本工程跑 convert-project，记录当前 Warning 文案/退出码/null compilation 表现状 | CLI 手工 + 测试固化 | §1.2 证据行 + 7.1-4 |

## 5. CLI 适配器与集成

1. **随族承载（语义层）**：不新增适配器；C36 合入后由 C23 的 `ExpressionDesugarPassAdapter` 承载，族 options 增 `VersionGuardOptions`（`bool EnableGuard = true`、`bool Strict = false`、各 checker 开关字典）；摘要行追加 `guard: <checks-hit> findings (error E/warn W/info I, suppressed S, parse-layer P)`。
2. **独立 verb `version-audit`（CLI 层，建议 M1 即交付）**：`cs2j version-audit --project <sln|csproj> [--langversion-override N] [--report-out dir] [--strict]`；只读；可在默认链最早期手工/CI 跑；退出码＝0（无 Error）/非 0（strict 下 Error 或 parse 层失败）。
3. **convert-project 默认链接入（仅探测层）**：在工程 parse 前调用 probe；非 strict 下只增诊断行与摘要，**不改变既有跳过/转换决策**（零行为变更）；strict 下能力不足（项目需要 C# 14 而包上限 13）直接 CS2J0591 失败退出，避免「全量跳过产出空结果」。
4. **parse 失败可读诊断包装**：替换 [ProjectCompilationBuilder.cs L41-L43](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L41-L43) 裸 Warning 文案的最终呈现——保留原 CS 码位置，外包一层 CS2J 码与「语言版本/特性/承接家族/升级建议」上下文（Core 侧只提供 classifier API，CLI 侧决定打印；不改变 L47-L56 的跳过控制流，strict 升级另走选项）。
5. **与 UnsupportedDomain（CS2J3xxx）的码段区分**：[UnsupportedDomainPasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs) 占用 CS2J3001/3101-3102/3201-3203、C01 占 3301-3320，语义是「领域/平台/互操作不可转」；C52 语义是「语言版本/构造防线」，**采用 CS2J0590–0599 新段**（C36 段 0570-0589 紧邻顺延），在锚点登记表（C23 §6 码段表 + PassStubCatalog 注释）登记；category 固定 `version-guard`，与 `unsupported-domain`/`platform-boundary`/`native-interop` 并列，不过滤、不合流。
6. 指纹：探测只读不产目录，不生成指纹令牌；version-audit 不接入 fingerprint/preserve。

### 5.1 选项面（VersionGuardOptions）

| 选项 | 默认 | 作用 |
|---|---|---|
| `EnableGuard` | true（随族；族 pass 本身仍默认关） | 语义层扫描总开关；关＝零节点访问、零耗时 |
| `Strict` | false | true：白名单（§2.6）Error 即文件/项目失败；false：Error 降 Warning 保留 |
| `EnabledChecks` / `DisabledChecks` | 全开 | 逐 V-xx 灰度（如画像期单独开 V-02） |
| `ErrorChecks` | 白名单集合 | 可收紧不可放宽（配置项只能比白名单更保守，防止私自放宽误报门） |
| `ReportFormats` | Markdown | Markdown/Json 枚举；`--report-out` 给目录 |
| CLI `--langversion-override` | 无 | version-audit 专用：模拟工程 LangVersion（升级演练/低版本工程探测） |
| CLI `--include-support-libs` | false | SY-8 支撑库豁免开关 |
| `EnableProbeInConvertProject` | true（仅 Info/Warning 输出） | 控制默认链是否跑探测层；strict 升级失败另由 `--strict-version-capability` 显式开启，**默认不阻断** |

## 6. 诊断策略（CS2J0590–CS2J0599；MSBuild 风格）

**第一原则**：每一种结局必须具名——显式报码、可读升级提示、或 Info 登记；**禁止裸 Roslyn parse 异常/诊断流出**；未知新构造必须兜底发声而非静默。

| 码 | 级别 | 含义 / 三态归属 |
|---|---|---|
| CS2J0590 | Error（strict）/Warning | **Unsupported**：构造已能识别但转换器不支持且直放必然错译/非法（V-02 Lock、V-07 高危站点、V-08、V-11、V-16、V-17 非 public/菱形、V-20、V-26 等）；消息含构造名、语言版本、承接家族条目、用户改写范式 |
| CS2J0591 | Error（strict）/Warning | **UpgradeRequired**：parse 层语言能力不足（工程 LangVersion/语法 > Roslyn 包上限），由特性版本 CS 诊断翻译；消息＝特性名 + 所需 C# 版本 + 当前 cs2j/Roslyn 世代 + 升级 cs2j 建议 |
| CS2J0592 | Warning（默认） | **RoutedPending**：构造已登记路由到家族条目但家族未实施；消息点名承接家族（如「routed to C29, family not yet implemented」）；不阻断 |
| CS2J0593 | Info | **Registered**：DirectMap 已确认（V-18/V-22 global::）、LexicalStrip 盘点（V-03/V-19）、家族码抑制计数（V-10/V-22/V-23/V-24 让位）、const 插值等「已认领不计缺口」 |
| CS2J0594 | Warning（strict Error） | **Unknown preview construct**：疑似新版本构造兜底；固定文案「疑似更高版本 C# 语言构造，请升级 cs2j 或在 C52 差集登记」，附 SyntaxKind/trivia kind 与源码片段 |
| CS2J0595 | Info | 版本能力探测汇总：项目 LangVersion、有效 parse 版本、Roslyn 包语言上限、差集特性计数 |
| CS2J0596 | Error/Warning | parse 失败包装码（非特性版本类的残余 parse error）；内嵌原 CS 码与位置，禁止再出现无码「Skipping parse-invalid file」终态 |
| CS2J0597–0599 | — | 预留（升级季/表达式树防御等） |

消息属性（json 报告字段）：`checkId`（VG-xx）、`feature`、`languageVersion`、`family`、`familyDiagnosticCode`、`disposition`、`location`。同位置同 check 去重（仿 [BoundaryDiagnosticCollector L469-L494](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L469-L494) 的 key 去重范式）。

## 7. 测试计划

### 7.1 Core/CLI 单测（矩阵逐行 snippet）

`tests/CSharpToJava.ProjectTests/VersionGuardUnitTests.cs`（语义层；net10.0 CSharpCompilation，4.12 包；仿 C36 夹具范式）+ `VersionCapabilityProbeTests.cs`（CLI 层）：

1. **逐子项报码断言（26 snippet，一文件一最小例）**：`where T : allows ref struct`（V-01）；`lock (new System.Threading.Lock()) {}`（V-02，SemanticModel 造 Lock 符号桩或引用 .NET 9 引用程序集）；`"\e"`（V-03）；无目标方法组（V-04）；`[OverloadResolutionPriority(1)]`（V-05，探针造特性符号）；partial property/event 两部件（V-06）；Span 隐式转换站点（V-07，M0 后按 4.14 基线补断言）；`nameof(List<>)`（V-08，4.14 ParseOptions 模拟）；partial ctor（V-09）；`(out int r) => …` 简单形参修饰符（V-10）；显式 `operator +=` 声明 + `a += b` 站点 ICompoundAssignmentOperation.OperatorMethod 非空（V-11）；`#:package …` trivia（V-12，kind 以探针为准）；extension 块/`field`/`a?.b = c`（V-13/14/15 占位路由码与家族认领后不报）；extension 块内 indexer（V-16）；接口 private 默认方法/默认属性/`void I.M()` 重实现（V-17 三例）；`x >>> 1`、`x >>>= 1`（V-18 仅 Info）；`string? s` 声明且不误伤 `int?`（V-19）；`file class C {}`（V-20，与 file-scoped namespace 同文件反例）；`[field: X]`/`[return: X]`/`[assembly: X]`（V-21，含 AssemblyInfo 生成文件排除）；`global::System.Console`（Info）与 `extern alias X; X::N.C`（V-22）；`delegate*<int,void> f = &M; f(1);`（V-23）；`const string s = $"a{1}";`（V-24）；`Func<int,int> f = (int x = 1) => x;`（V-25）；泛型类静态字段两种（引用类型参数 vs 不引用，V-26 只报前者）。
2. **家族已合入不报断言**：用 mock `GuardCheckContext`（执行计划含家族 pass）验证 SuppressedByFamilyCode（V-10/V-22/V-23/V-24）；Claimed checker 移除场景用注册表断言（§3.4-2）。
3. **版本探测**：构造低 LangVersion 工程（`new CSharpParseOptions(LanguageVersion.CSharp10)` + C# 13 语法文件）→ CS2J0591；高版本语法在 4.12 下的特性诊断分类（CS 码→VG 映射表逐行，M0 清单）；包上限反射值＝CSharp13。
4. **parse 失败可读诊断**：损坏/超版本输入 → CS2J0596/0591，断言输出不含裸「Skipping parse-invalid file」终态文案（CLI 层断言），位置/原 CS 码保留。
5. **未知兜底**：手工构造注册表外可疑 token 串（随升级季更新）→ CS2J0594。
6. **只读/幂等**：任意输入二次运行 findings 完全一致；输出树与输入根引用相等；Changed 恒 false。
7. **审计快照**：固定 fixture 工程的 md/json 报告快照比对（含已认领排除清单段落）。

#### 7.1.1 snippet 索引表（26 行最小例；与 §2.2 矩阵一一对应）

| check | 最小 snippet（节选） | 期望（非 strict / strict） |
|---|---|---|
| V-01 | `class C<T> where T : allows ref struct { }` | 0592 W / W（C06 未就绪） |
| V-02 | `var l = new System.Threading.Lock(); lock (l) { }` | 0590 W / **Error** |
| V-03 | `var s = "\e[1m";` | 0593 Info（移交 C43 后 55xx） |
| V-04 | `static int F(int x) => x; Func<int,int> g = F;`（无目标方法组改进站点探针构造） | 0592 Info/W（盘点） |
| V-05 | `[OverloadResolutionPriority(1)] void M() { }` | 0592 W |
| V-06 | `public partial int P { get; }` + `public partial int P { get => 1; }` | 0592 W（C11 5114 已先行时让位） |
| V-07 | `void M(ReadOnlySpan<char> s){} M("abc");`（4.14 新隐式转换形态探针） | 可证高危 0590；其余 0592 |
| V-08 | `_ = nameof(System.Collections.Generic.List<>);` | 4.12：0591；4.14：0590 W/Error |
| V-09 | `partial class C { partial C() { } partial C() { } }` | 4.12：0591；4.14：0592 |
| V-10 | `TryParse<int> p = (out int r) => int.TryParse("", out r);` | 0592（C45 CS2J4501 就绪后抑制） |
| V-11 | `class A { public static A operator +(A a, int b)=>a; public static A operator +=(A a, int b)=>a; }` + `a += 1;` | 0590 W/Error（OperatorMethod 非空） |
| V-12 | `#:package System.Text.Json@9.0.0`（文件首 trivia） | 0592 W（kind 以 P3 探针为准） |
| V-13 | `extension(string s) { public int Length => s.Length; }`（占位路由） | 4.12：0591；4.14：0592 |
| V-14 | `int P { get => field + 1; }`（占位路由） | 4.12：0591；4.14：0592 |
| V-15 | `a?.b = c;` / `a?[i] = v;` / `a?.b += c;`（占位路由） | 4.12：0591；4.14：0592 |
| V-16 | extension 块内 `public int this[int i] { get => …; }`（C# 15 预览） | 0590 W / **Error** |
| V-17a/b/c | 接口 `private void M(){}`；接口属性带默认实现；类中重新实现 `void I.M(){}` 菱形 | 0590 W/Error（public 实例 body 反例不报） |
| V-18 | `int x = a >>> 2; a >>>= 3;` | 0593 Info（逐节点计数） |
| V-19 | `string? s = null;`（反例 `int? n = null;` 不报） | 0593/0592 |
| V-20 | `file internal class C { }`（反例 `namespace A;` 不报） | 0590 W/Error |
| V-21 | `[field: NonSerialized] int x;` / `[return: X()] int M()=>1;` / `[assembly: X]`（AssemblyInfo 反例排除） | 0592 W |
| V-22 | `global::System.Console.WriteLine(1);`（Info）；`extern alias X; X::N.C.M();`（W） | 0593 / 0592（C28 5520 就绪抑制） |
| V-23 | `delegate*<int, void> f = &M; f(1);` | 0592（C06 UF021 就绪抑制） |
| V-24 | `const string s = $"a{1}";`（四上下文各一例） | 0593（C23 0503 就绪摘除） |
| V-25 | `Func<int,int> f = (int x = 1) => x;` | 0592 W |
| V-26 | `class C<T> { static int count; }`（引用 T vs 不引用 T 两例） | 前者 0590 W/Error；后者不报 |

### 7.2 项目级 fixture（C# 13/14 混合工程）

建议 `tests/SampleVersionGuardSolution/`（独立小 sln，不与 C36 fixture 耦合）：多工程声明不同 LangVersion（11/13 + 手工 14 语法样本文件条件编译隔离）；Lib 包含全部可在 4.12 parse 的 V-xx 形态；断言 `version-audit` 退出码、报告分桶数、strict/非 strict 两模式；**报错路径不落任何中间目录**。

### 7.3 Release build/test（实施期执行；本设计任务不执行 build/test/git 写操作）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

### 7.4 CLI 与升级演练

- `version-audit` 在现有仓 `/ D:\csharp-sdk` 抽样工程上跑通，报告无 Error（基线回归）；`--list-passes` 在 catalogItems 加宽后显示含 C52。
- **升级演练（不实际升包）**：测试中以反射/测试钩子把 `LanguageVersion` 上限与 ParseOptions 替换为模拟 4.14（构造 4.14 支持语法的 snippet 字符串，按探针记录的诊断形态模拟），验证差集流程、新 checker 登记、快照刷新；产出一次演练审计报告样例归档。

## 8. 默认开启验收门槛

1. **纯加诊断属性**：审计/拒绝模式（probe + Guard 扫描 + 报告）可较早默认开——不改写、不改变跳过控制流（非 strict），对现网零行为变更。
2. **硬依赖**：语义层默认开依赖 C36 Guard 组件落地（族 pass Implemented）；CLI 探测层可随 M1 独立默认开（仅 Info/Warning）。
3. **误报率为零门槛（★）**：默认链中 **Error 仅对「确认不支持且直放必然错」的构造发出**（0590 白名单＝V-02/V-07 高危/V-11/V-16/V-17 非 public·菱形/V-20/V-26，逐条评审）；其余一律 Warning/Info；任何 Error 在 D:\csharp-sdk 与现有三个测试工程样本上命中数必须为 0（或全部核实为真实错误），否则降 Warning。
4. 差集 checklist（inventory.json）评审通过并与注册表双向断言锁定；码段 CS2J0590-0599 锚点登记完成。
5. version-audit 在仓内 + sdk 抽样工程零崩溃、报告与人工 triage 一致；strict 模式失败路径不留半成品、无中间目录。
6. 不达标长期 opt-in；每评审周期按升级季流程复跑。

## 9. 里程碑

| 里程碑 | 内容 | 依赖结论 |
|---|---|---|
| **M1 Roslyn 核实 + checklist + 版本探测 CLI（最小 Guard 先行）** | ① M0 探针：4.12 下全部 V-xx 的 parse kind/诊断 id/IOperation 外形记录（重点：特性版本 CS 码清单、`#:package` trivia kind、V-07 Span 绑定、V-11 ICompoundAssignmentOperation.OperatorMethod）；② `VersionCapabilityProbe` + FeatureDiagnosticClassifier + CS2J0591/0595/0596 + `version-audit` verb + 报告；③ inventory.json 初版（26 行 + 引用 3 行 + 机制行） | **可独立于 C36 提前交付**：CLI 层不碰 ExpressionDesugar 目录、不需要 C23 锚点；分支策略＝从 9b99c60b 切「最小 Guard 先行」分支（§12），只动 CLI/文档；探针记录归 `docs/superpowers/version-guard/audits/`。Core ~400 行 |
| **M2 语义层 Guard 与全部 checker** | C36 分支合入后：`Shared/VersionGuard/` 全套、26 checker（含 C36 三项迁入）、DiagnosticRouter、CS2J0590/0592/0593/0594、族 options/统计/摘要、§7.1 全矩阵单测、fixture、Release 全绿；catalogItems 加宽 | 硬前置 C36（建议含 C23 锚点）；~700 行 |
| **M3 随家族合入的摘除机制 + 升级季流程** | 注册表↔家族状态一致性单测；与首批合入家族（预期 C23 const 插值 V-24、C28 V-22/V-03、C20 V-06/V-09）联合 PR 演练 checker 摘除；升级季 SOP 文档化；Roslyn 4.14 升级预演报告（不升包） | 跟随各家族节奏；~200 行 + 流程 |

全程不新增 stub/pass；M1 默认仅 Info/Warning；M2 strict 关；Error 白名单（§8-3）随 M2 评审定稿。

## 10. 风险

| # | 风险 | 说明 | 缓解 |
|---|---|---|---|
| R1 | **误报阻断真实代码（最高）** | Lock 在 .NET 9 BCL 可能高频；V-02 一旦误判（普通自定义 Lock 类、符号缺失的单文件模式）会 Error 阻断 | 类型判定必须 SemanticModel 全名匹配，符号缺失/单文件模式降 Warning；Error 白名单制 + §8-3 零误报门槛；非 strict 永不阻断；先 Warning 画像一个评审周期再升 Error |
| R2 | **Roslyn preview API 变动** | C# 14/15 节点类型（ExtensionDeclaration 等）、`#:package` trivia kind、ICompoundAssignmentOperation 外形在 4.12 不可实证，靠记忆/文档易偏差 | 所有识别谓词以 M0 探针记录为准（4.12 能测的先测，4.14 项标注「升包后复核」）；checker 对反射缺失的节点类型做 kind 编号防御，包版本不匹配时不抛异常、降级为 0594/Info |
| R3 | **parse 层失败无节点可扫** | 4.12 下 C# 14 语法根本不产生目标节点，语义层扫描全部失效 | A/B 层（parse 前探测 + 诊断治理）是硬前置而非可选；M1 独立先交；严格保证超版本文件有 CS2J0591 而非裸跳过 |
| R4 | **家族与 Guard 重复报码** | checker 未随家族摘除/家族码与 059x 双报 | §3.4 三机制：同 PR 摘除 + 注册表一致性单测 + SuppressedByFamilyCode；联合 fixture 覆盖 |
| R5 | **`>>>` 1:1 假设** | 内置操作数 1:1 已有证据（§2.2 V-18），但用户自定义 `op_UnsignedRightShift`、移位掩码（`n & 31/63`）与类型提升边界可能有差 | V-18 仅登记内置操作数（排除有 OperatorMethod 的站点→C14）；升级季回归（VG-10）；掩码语义差异归 E09/C15，不在本防线 |
| R6 | **隐式 Span 检测依赖高版本绑定** | V-07 的精确识别需要 4.14 重载决议；4.12 下只能粗扫，粗扫可能漏/误 | M2 仅对可证形态报 Error，其余 Warning 并标注「需 4.14 复核」；精确 checker 随升包完成，不提前承诺 |
| R7 | **probe 双倍 parse 成本** | version-audit/探测层额外 parse 全树 | 复用 runner 已建 parse（钩子取同一 options/树）；独立 verb 才全量 parse；convert-project 内 probe 只读工程模型 + 复用诊断，不二次解析 |
| R8 | **码段/锚点漂移** | 0590-0599 与未来 C23 扩展段、CS2J3xxx 边界混淆 | §5-5 锚点登记；码段表单一事实源在 C23 §6 维护，C52 PR 同步登记 |
| R9 | **NRT/value-type `?` 误剥** | V-19 词法剥离若误把 `int?` 当引用注解将改变语义 | checker 只做识别盘点（SemanticModel 判引用/值类型），剥离动作归词法批且必须双跑；单测反例固定 |
| R10 | **catalog/spec 口径不一致** | `\e` 归属在 catalog L778/L1199 与 C43 开放问题 5 有口径差；C49/C50/C51 尚无 spec | 矩阵给结论（V-03 归 C43 LL-6）并在合入时请 catalog 维护者更新引用；V-13/14/15 只引用不展开，spec 产出后回填链接 |

## 11. 并行协调

| 相邻工作 | 边界/点名 | 接口约定 |
|---|---|---|
| **C36（同目录，锚点硬依赖）** | CE-GUARD 三检查（0585-0587）迁入 Guard 注册表；目录 `CollectionExpr/` vs `Shared/VersionGuard/` 划清；C36 文档 §2.5 追加「升格 C52」注记 | C36 先合；迁入只移码不改语义；热文件 `ExpressionDesugarRuleSet.cs`/Options 仍只增 |
| **C23 锚点** | `Shared/` 所有权、族 options/统计/适配器接缝 | VersionGuard 子目录只增；不改 TempLifter 等既有形状 |
| **C49/C50/C51（spec 待写，catalog 已备）** | V-13/V-14/V-15 占位路由；[catalog L953-L995](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L953-L995) | spec 产出后回填路由锚点；实施 PR 同 PR 摘 checker；C50 与 V-21 `[field:]` 联动 |
| **C48（params 族）/ C06（unsafe-ffm）** | V-07 Span 重载、V-23 delegate*（UF021）、V-01 allows ref struct | 家族码优先（UF021/5520/4501 等），Guard 让位；Span 绑定形态与 C06 D5 对齐 |
| **C29** | V-02 Lock 追加行（[catalog L676-L678](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L676-L678)） | C29 入口 SemanticModel 类型分流（object→synchronized／Lock→compat 或拒绝）；落地摘 V-02 |
| **C02** | V-26 静态字段逐封闭实例化（c02 行 8） | Guard 提前盘点与 FieldTransformer 既有 fail-fast 互认（同位置不双报） |
| **C46** | V-17 DIM 完整面（CS2J5197 菱形歧义码已占位） | 非 public/默认属性事件的 Guard 识别谓词与 C46 InterfaceMappingIndex 共用符号判定 |
| **C43/C28（词法批）** | V-03 `\e`（LL-6/预留码 5571/5583-5589/5591-5599）、V-19 NRT、V-20 file 类型、V-22 extern alias（CS2J5520） | 词法批翻转后摘 4 个 checker；C43 开放问题 5 随本设计闭环 |
| **C10/C12/C14/C17/C45/C20** | V-05、V-25、V-11、V-10、V-06/V-09/V-21 | 全部「家族码就绪→Guard 让位」；C20 partial 成员与 V-06/V-09 同摘；C45 CS2J4501 已设计先行 |
| **E01** | V-12 `#:package`/`#:sdk`（[ConditionalDirectiveStripper.cs L75-L88](file:///d:/code/cs2j/src/CSharpToJava.Core/ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs#L75-L88) 指令白名单） | E01 认领后扩 IsRemovedDirective 或新规则；Guard 仅在其未认领时报 |
| **Roslyn 升级季 owner** | 包 4.12→4.14 升级节奏、inventory 差集评审 | 升包 PR 必须含 VG-9 对账与审计快照；未跑差集不得升包 |
| **热文件** | `ExpressionDesugarRuleSet.cs`、族 Options、[PassStubCatalog.cs L217-L226](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L217-L226) catalogItems、[ProjectCompilationBuilder.cs L26-L56](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L26-L56)、`ProjectPassRunner.cs`/`ProjectPassAdapter.cs`（parse 选项钩子）、Program.cs verb 注册 | 全部追加式；probe 以钩子/独立 verb 接入，不改 runner 主流程控制；本设计阶段不触碰任何一个 |

## 12. Worktree 分配（只写不执行；本设计任务未执行任何 git 写操作）

- 分支：`feature/preproc-c52-version-guard`
- 目录：`.worktrees/preproc-c52-version-guard`
- base：`9b99c60b`（已校验存在）；**语义层（M2）实现前先合入 C36 分支**（C36 又以 C23 锚点为硬前置）。
- 创建命令（仓库根、PowerShell；本设计只产出本文档，**不执行**）：

```powershell
git worktree add .worktrees/preproc-c52-version-guard -b feature/preproc-c52-version-guard 9b99c60b
```

- **「最小 Guard 先行」分支策略（M1 独立交付）**：M1（probe + classifier + version-audit + inventory，CLI/文档层，不依赖 C23/C36）直接从 `9b99c60b` 切上述分支开发并可独立合入主干；M2 在 C36 合入后 rebase 到含 `ExpressionDesugar/Shared` 的基线，新增语义层提交。若评审要求 M1/M2 物理分栈，则 M1 用分支 `feature/preproc-c52-capability-probe`（同 base）先合，M2 分支 rebase 其上；二选一在 M1 开工评审拍板，**倾向单分支两阶段**（probe 与路由表共用 inventory/码段，拆栈会产生临时重复事实源）。
- 实施约束：仅在 worktree 内改码；不执行任何 git 写操作由本设计任务开始保持；不新增 pass/stub/中间目录/适配器骨架（M1 的 verb 处理除外，属新增 CLI 命令而非 pass）；转换器代码一行不改；交付面遵循 foundation plan 交付包模板（Core 规则目录 + CLI 命令 + 单测/fixture + Release build/test + 设计与 plan 文档）。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持本文 §12 登记的自有 base `9b99c60b`（不改动），创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（按原题号）：**

1. **catalogItems 加 C52**：**加宽为 `"C23, C24, C35, C36, C52"`**（否决仅描述文本注明——能力门控是事实承接关系，必须可机读）；随 M1 交付 PR 直接登记，不再外挂提请 catalog 维护者；同时在 C52 审计批次回填 catalog L778/L1199 的 `\e` 归属（见 C43 裁决 5）。
2. **Error 白名单终稿**：采纳推荐分级——**Lock/V-16/V-20 首发即 Error**（锁语义与关键特性版本无可靠降级，假阴代价最高）；**其余 V-02/V-07/V-11/V-17/V-26 首版 Warning，画像一个评审周期后按命中与误报数据转 Error**；转 Error 是默认方向、须逐码给出继续保留 Warning 的证据，不允许长期 Warning 挂起。
3. **CS 诊断码→V-xx 映射完整性**：**M0 探针实证** CS8022–CS8059/CS9xxx 中 4.12 实际发出的描述符全集与消息机读反查特性名的可行性；探针确认到的逐码入表，**0591 允许泛化但消息必须含实际特性名**（从诊断参数/源码位点提取，提取不到才退纯泛化并计探针缺口），不杜撰描述符。
4. **M1 分支策略**：采 **M1 单分支两阶段**（probe/classifier/version-audit/inventory 先行独立可合，M2 语义层同分支追加提交）；否决 probe 独立分栈——probe 与路由表共用 inventory/码段事实源，拆栈必然产生临时重复事实源。
5. **V-08 unbound nameof 归属**：**撤销 RejectNow 临时结论**，正式**登记为 C02 typeof 归一家族 backlog 条目**（unbound `nameof` 与 `typeof` 归一并入 C02/C19 扩展方法/nameof 内联路径改造时顺带收编）；在该家族承接前 V-08 保持显式诊断并在诊断文案指路 backlog，不静默放行。
6. **version-audit 进 CI 门禁**：**M3 前**仅人工/升级季运行、**不阻断主干 PR**（Warning 不阻断）；**M3 后**转**非阻塞周报**模式（定时跑出画像差异通报，仍不阻断 PR）；任何阶段都不做强制阻断门禁，除非 R7 耗时数据证明增量开销可忽略且画像流程提出阻断需求，届时另行评审。

---

### 原问题记录（裁决前）

1. **catalogItems 是否加 C52**：建议加宽为 `"C23, C24, C35, C36, C52"`（§3.3），备选仅在描述文本注明；需 catalog/注册表维护者确认。
2. **Error 白名单终稿**：V-02/V-07/V-11/V-17/V-20/V-26 是否全部首版即 Error，还是先全 Warning 画像一个评审周期（倾向 Lock/V-16/V-20 首发 Error，其余先 Warning；R1）。
3. **特性版本 CS 诊断码→V-xx 映射表的完整性**：CS8022–CS8059/CS9xxx 中哪些描述符在 4.12 实际发出、消息能否机读反查特性名，需 M0 探针实证；映射不全时 0591 退化为泛化消息的可接受边界。
4. **M1 是否单分支两阶段**（§12）：单分支 vs probe 独立分栈；影响 inventory.json 事实源放置。
5. **V-08（unbound nameof）是否随 C02 typeof 归一立项家族**：现无承接家族，RejectNow 是临时结论；长期可在 C02/C19 扩展方法/nameof 内联路径改造时顺带收编，需家族 owner 表态。
6. **version-audit 是否进入 CI 门禁**：M3 前建议仅人工/升级季运行；是否对主干 PR 强制跑（Warning 是否阻断 PR）需结合耗时（R7）与 sdk 画像流程定。
