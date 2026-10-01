# C39 终结器（`~T()`）消除（member-desugar 族小规则）设计

**日期**: 2026-09-29
**范围**: 在 C11 锚点建立的 `member-desugar` 族内新增小规则目录 `Finalizers/`，对 C# 终结器（destructor / CLR `Finalize` 重写）从「转换器静默丢弃」改为「显式诊断优先、人工确认后可删除」；本特性不注册/不翻转 pass、不动 RunAfterIds、默认不改正转换器（转换器 fail-fast 安全网列为同步后续 PR）
**状态**: 设计已确认；**C11–C14 已由维护者手动合并主干 dev-3.x**（2026-09-30 确认），可直接在 member-desugar 族内新增 `Finalizers/` 规则（pass 默认关）
**族属**: catalog P3「成员去糖族」（[preprocessor-catalog.md §3.3](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1082-L1082)）；C11–C14 已合入，`member-desugar` 现为 Implemented 描述符（[PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)，catalogItems 2026-09-30 已对齐 `"C11, C12, C13, C14"`，C39 待按合入序追加）；C11 Property 是族锚点（[2026-09-28-c11-property-desugar-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md)），本设计假设其 §4.5 族契约成立

---

## 1. 背景与目标

### 1.1 现状：终结器被成员分发静默丢弃，零信号

C# 终结器 `~T() { ... }` 是成员级语法，主转换器按成员逐种分发，但分发 switch 中没有它的位置：

- [ClassTransformer.ProcessMember](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L316-L380) 的成员 `switch`（[L337-L379](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L337-L379)）覆盖 field/property/method/constructor/operator/conversion/indexer/嵌套类型/delegate/event-field/event 共 11 个分支，**既无 `DestructorDeclarationSyntax` 分支，也无 `default`**。终结器落入 switch 后直接走出方法，不产出任何 Java 成员、不抛异常、不写注释、不发诊断。
- 全 `src/CSharpToJava.Core` grep `Destructor` 仅一处命中：[LambdaTransformer.cs:306-308](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L306-L308)，只服务 lambda 可见局部名收集（把终结器体纳入名字集合），不产出成员。
- `tests/` 全目录 grep `Destructor|finalizer|~\w+\(` **零命中**：没有任何测试锁定终结器行为，静默丢弃无回归护栏。
- 对比同函数内 extern/DllImport 的处理：[ClassTransformer.cs:346-350](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L346-L350) 对 extern 方法显式 `throw new NotSupportedException("CS2J normalization required: ...")`——终结器缺少同等级别的 fail-fast。
- catalog 三次/四次补全已将本现象登记为「静默错误输出」族（[preprocessor-catalog.md:1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)，风险 12）与 C39 专条（[L810-L822](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L810-L822)）。

### 1.2 终结器语义（为什么不能静默删，也不能简单翻译）

- C# `~T()` 由编译器降为 `protected override void Finalize()`，方法体包在 try/finally 中并**自动插入 `base.Finalize()` 链调用**；每类型最多一个，无访问修饰符/参数/返回类型；struct、接口不允许；存在无体 `extern` 终结器形态。
- **非确定性回收**：由 GC 在 finalizer 线程上调度，时机、顺序、是否执行均不保证（进程终止、`WaitForPendingFinalizers`、终结器队列饱和等都影响结果）；终结器内不得抛未处理异常；对象可被 resurrection（`GC.ReRegisterForFinalize`）；`GC.SuppressFinalize(this)` 是标准 Dispose 模式中「确定性释放已发生，跳过终结器」的配对调用。
- `System.Runtime.ConstrainedExecution.CriticalFinalizerObject` 派生类型的终结器受 CER 约束保证（普通终结器之后、AppDomain 卸载/线程中断时仍尽力执行）；`SafeHandle`/`CriticalHandle` 的可靠性建立在关键终结器上。本仓 `SafeHandle` 已出现在平台隔离的阻断命名空间清单中（[UnsupportedDomainPasses.cs:246-251](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L246-L251)）。
- Java 侧：`Object.finalize()` 自 Java 9 起 `@Deprecated`（Java 18 起 `forRemoval=true`），官方推荐 `java.lang.ref.Cleaner`——但 Cleaner **不是**终结器的等价物：它要求在构造期显式注册一个不持有目标引用的 cleaning action（否则复活），注册与否、时机、异常处理全部由代码主动控制。
- 与 C07/Dispose **无直接语义等价关系**：Dispose 是确定性释放，终结器是非确定性兜底；二者唯一的工程联系是标准「`Dispose(bool disposing)` + `GC.SuppressFinalize`」模式中终结器调用 `Dispose(false)`。

### 1.3 命中率证据（sdk 只读画像，2026-09-29 复核）

对只读训练集 `D:\csharp-sdk` 全量 .cs 扫描行首形态 `~Identifier(`：**仅 2 处命中**，且都是教科书式 Dispose 模式：

1. `regular-expressions\tests\TestUtilities\System\IO\FileCleanupTestBase.cs:19`：`~FileCleanupTestBase() { Dispose(false); }`，同类有 `Dispose() { Dispose(true); GC.SuppressFinalize(this); }`；
2. `xml-tiny\src\System\Xml\Dom\XmlElementList.cs:45`：`~XmlElementList() { Dispose(false); }`。

命中率极低（P3 定级合理），但两处都承担真实资源兜底职责（临时目录删除、XML 节点列表释放），**删除即行为改变**，必须有人工信号，不可零信号丢弃。

### 1.4 目标

1. **显式信号优先**：每一处终结器在预处理期得到具名、带位置、带分类的诊断码，消灭「无诊断、无注释、无占位」的静默丢弃；
2. 提供三档策略（§2 选型）：**A 显式拒绝（推荐默认）**、B 确认后删除并留痕、C 远期 Cleaner 引导；
3. 同步推动转换器安全网：`ProcessMember` 显式识别 `DestructorDeclarationSyntax` 并 fail-fast（后续 PR，§4 FN-9），使「未启用本 pass」的路径也不再静默；
4. 规则保持小而自包含：终结器无显式调用点（C# 不允许用户调用 `Finalize`，CS0245），本规则**只改声明、不改使用点**，规模显著小于 C11-C14；
5. 中间树永远可编译、幂等、删除场景下产物零 `DestructorDeclarationSyntax` 残留（residual 闸门）。

非目标：不重写 Dispose/IDisposable（归 C07/既有转换器）；不翻译 `GC.SuppressFinalize` 调用本身（仅清点标注）；v1 不实现 Cleaner 降级；不处理 `SafeHandle`/P/Invoke 的平台归一（C06/C42 职责）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | **★三选型** | **A 显式拒绝为推荐默认（宁假阳）**：每处终结器具名报码、不重写，要求人工改为确定性 Dispose（或显式登记例外）；B 删除+Warning+body 留痕作为逃生口；C Cleaner 引导列远期，v1 配置即报错 | 终结器体内是**非确定性资源语义**，静态分析无法判定「删掉是否安全」：可能释放非托管句柄、也可能做关键 flush。命中率仅 2 处（§1.3），假阳成本极低；静默误删代价是进程期资源泄漏/数据丢失。对应 catalog「显式拒绝而非静默删除」建议（[L813](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L813-L813)）与风险 12 第一原则（[L1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)） |
| 2 | 默认动作的交付节奏 | 选型定 A 为**产品默认**；出厂渐进：M1 级别为 **Warning**（strict 升 Error），§8 门槛达成后默认翻 **Error**；B（`Policy=Remove`）作为 `force-remove` 逃生口始终可选 | Error 默认会卡住含 2 处终结器的真实画像（§1.3），Warning→Error 渐进让先有诊断流、再以 sdk triage 数据翻转；族 `Strict` 选项与 C11/C13 同构，翻转仅改默认严重级一行 |
| 3 | 模式 B 删除时的 body 去向 | **默认不自动提取方法、不写树内注释**：删除成员，body 原文 + 符号 + span + 分类写入中间目录 sidecar `.cs2j-finalizer-removals.json`，每处必出 CS2J5201 Warning（含 sidecar 路径）；两个可选项默认关：`EmitRemovalMarkerComment`（原位留 `// CS2J5201: ...` 注释）、`ExtractRemovedBody`（提取为永不调用的 `private void Cs2jRemovedFinalizer_T()`） | 树内注释可能随转换器流入 Java 产物，违反族「禁止占位静默透传」纪律；自动提取的未调用方法在 Java 侧是死代码，且方法体捕获 `this`/字段会制造「兜底逻辑仍在」的错觉。sidecar 是日志而非产物：.json 不参与编译、不被转换器消费，杜绝 body 丢失（仍可在日志检索） |
| 4 | 终结器体分类 | 五分类：`Empty`（空体/仅基类链）、`DisposePattern`（唯一有效语句是 `Dispose(false)` 形态）、`SafeHandleOnly`（仅触碰 `SafeHandle`/`CriticalHandle` 字段）、`NativeRelease`（`Marshal.Free*`/`NativeLibrary.Free`/IntPtr 句柄释放/extern 释放）、`Other`（上述之外：flush、日志、自定义逻辑）；另有无体 `Extern` | 分类决定信号措辞与 B 档可否自动删：Empty/SafeHandleOnly 是冗余终结器（SafeHandle 自带关键终结器，用户终结器多余）；DisposePattern 是可人工确认的安全候选；NativeRelease/Other 默认拒绝自动删（CS2J5204/CS2J5205） |
| 5 | `GC.SuppressFinalize` 配对分析 | Analyzer 清点同类型 `Dispose()` 是否存在、是否含 `GC.SuppressFinalize(this)`，输出配对结论（paired/unpaired/none）作为 Info（CS2J5206）与分类证据；**不删除、不改写** `SuppressFinalize`/`ReRegisterForFinalize` 调用 | 删除终结器后 `SuppressFinalize` 成为无害空操作语义，留在树中不破坏编译；删它反而改动 Dispose 方法体（超出成员删除规则边界，且属 C07 Dispose 面）。`ReRegisterForFinalize`（resurrection）命中即在报告中单列高风险标记 |
| 6 | 继承链 | Analyzer 沿 BaseType 链查基类是否声明终结器并记录（CS2J5207）；**删除派生类终结器不影响基类终结器**：C# 源重编译后派生类型无 Finalize 槽，运行时对该实例执行基类自身的 Finalize；编译器自动插入的 `base.Finalize()` 链随派生重写一起消失，不需手工补链 | 这是「源级删除 + 重编译」相对「IL 级删方法体」的关键区别，也是 residual 后中间树 build 可证的形态；基类在另一程序集（无源码）时分析降级为 Info 标注，不阻断 |
| 7 | `CriticalFinalizerObject` | 含入类型派生自 `CriticalFinalizerObject`（含语义模型判定，跨程序集基类也算）时，**任何模式都拒绝自动删除**（CS2J5205 Error），只能经逐类型 force 显式豁免（豁免仍留 Error 级日志） | 关键终结器有 CER 保证，删除属于平台契约变更，与 C42 平台边界同族风险；SafeHandle 族类型自身不允许用户声明终结器（C# 编译器/分析器已有约束），重点拦截用户自定义关键终结器 |
| 8 | 模式 C（Cleaner 引导） | v1 **不实现**：options 选 `CleanerGuide` 即 CS2J5208 配置错误；远期形态仅做设计备忘——在构造器发射显式注册 `Cs2jCleaner.Register(holder, static state => ...)`（state 不持有 target，防复活），转换器配合映射 `java.lang.ref.Cleaner`，需 compat 新类 + 转换器 PR | Cleaner 是主动注册模型，无法从终结器语法机械推导注册点（构造器？多个构造器？反序列化路径？）；sdk 两处都是 Dispose 模式（§1.3），Cleaner 路线收益低，先积累需求 |
| 9 | 策略 options | 族 options 子节 `rules.finalizers`：`Policy = Reject/Remove/CleanerGuide`（默认 Reject）、`bool Strict`（继承族级，默认 true）、`IReadOnlySet<string> ForceRemoveTypes`（逐类型 FQN 白名单，仅解除 CS2J5204，不解除 CS2J5205）、`bool EmitRemovalMarkerComment=false`、`bool ExtractRemovedBody=false` | 逃生口与 JK/C11 overrides 同范式：显式、可审计、按类型精确；不提供「全库静默删除」总开关 |
| 10 | 规则发现与 Kind | 经 C11 锚点的 `IMemberDesugarRule` 程序集发现机制注册；需要锚点在 `MemberDesugarKind` 枚举补一个 `Finalizer` 成员（[C11 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L183-L195) 现仅 Property/Indexer/Event/Operator/Signature）——这是对锚点共享文件的一处增量，按 §11 协调 | C11 §11 已为 C39 预留 `Finalizers/` 目录与 CS2J52xx 码段建议（[L388](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L388-L388)） |
| 11 | 支撑库 | **无**：A 档不重写；B 档只删成员 + 写 sidecar（.json 不编译）；不注入 C# 支撑工程、不新建 Java compat 类 | Cleaner 路线（决策 8）才需要 compat，v1 零注入 |
| 12 | 转换器安全网 | 后续 PR（与 M1 同步推进）：`ProcessMember` 增 `case DestructorDeclarationSyntax d: throw new NotSupportedException("CS2J normalization required: finalizer ...")`（仿 extern 文案），并增 `default` 对真正未知成员种类抛异常；`IncompleteMemberSyntax` 显式列为例外（容错跳过，不吞没新种类） | 安全网保证「不启用 member-desugar」路径也响亮失败；`default` 落地前需枚举全部成员种类证明无新误伤（§10-4），该 PR 属转换器改动，不在本规则 worktree 交付内 |

---

## 3. 链位与族契约

### 3.1 归属 member-desugar，不新增 pass、不翻转、不动 RunAfterIds

- C39 没有独立 descriptor/adapter/中间目录/CLI 开关。`member-desugar` 已由 C11 锚点翻转为 Implemented 描述符（[PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)：Stage=`Declarative`、RunAfter=`{ struct-value-semantics, closure-lift }`、catalogItems 当前为 `"C11"`）；C39 规则经程序集发现自动生效。
- **注册表结构无需改动**；C39 落地时在 PassCatalog 的 catalogItems 字符串按合入序追加 `C39`（**2026-09-30 勘误：当前真值已是 `"C11, C12, C13, C14"`——在此真值上追加为 `"C11, C12, C13, C14, C39"`，切勿按旧文回退成 `"C11"` 或 `"C11, C39"`**；一行字符串冲突，属预期协调点，同 C13 §3.1）。
- C39 的开关只存在于族 options 子节（决策 9），不新增 verb、不新增 `--enable-pass` 目标；在 C11 合入前启用 member-desugar 仍是 CS2J9001（[PreprocessDiagnosticCodes.cs:12](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L12-L12)）。
- 目录/指纹/保留目录/verify 全沿用族一份 `.cs2j-member-desugar-src`，不新增中间树环节（[catalog §4 风险 11](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1109-L1109)）。sidecar 写在该中间目录根部，天然随中间目录清理，不进指纹快照（非 .cs）。

### 3.2 与 C36「语法盘点 + 显式拒绝」防线的关系：归成员族，不归 C36

catalog C39 条目曾写「若采用显式拒绝策略可直接并入 C36」（[L818](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L818-L818)）。复核结论：**归 member-desugar/Finalizers/**，理由：

1. §3.3 合并族表已把 C39 明确列入「成员去糖」族（[L1082](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1082-L1082)），且 C11 锚点已落地（member-desugar 为 Implemented 描述符）；
2. C36 是表达式/C# 11-12 新语法防线（集合表达式、spread、`static abstract`、`scoped`、泛型 attribute，[L764-L780](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L764-L780)），终结器是老牌成员语法，不需要「语法盘点」，需要的是**成员级语义分类与删除/拒绝**——复用 C11 的整树编译、plan、回退框架；
3. 两防线互补：未来若 C36 增加「未识别成员」通用盘点，终结器由 C39 先消费；C36 不为 `~T()` 发第二份诊断（避免双报码）。

### 3.3 硬不变式

1. 中间源码树预处理后永远可编译（整树重编译相对基线无新增 error；B 档删除成员不产生悬挂引用——终结器不可被显式调用，天然无调用点）；
2. **禁止零信号丢弃**：每个输出树中消失的终结器都必须对应一条 CS2J5201 + sidecar 记录；每个保留的终结器都必须对应 CS2J5200（Reject）或 skip/force 登记；
3. 幂等：A 档对含终结器树重跑零 diff；B 档产物已无 `DestructorDeclarationSyntax`，二次执行零改动；干净文件字节级一致（含 BOM 保持）；
4. 规则只认 SemanticModel 绑定（含入类型、基类、方法调用分类），不做文本匹配；partial 多部件按符号合并为同一 plan。

---

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/MemberDesugar/Finalizers/`，命名空间 `CSharpToJava.Core.MemberDesugar.Finalizers`。C11 建立的族共享层（Preprocessor/Models/Analyzer/Plan/SyntaxFacts/FormatAndValidate/规则发现）只读复用，不在共享文件内写终结器特例（`MemberDesugarKind.Finalizer` 枚举增量除外，§11 协调）。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `FinalizerModels.cs` | `FinalizerPolicy`（Reject/Remove/CleanerGuide）、`FinalizerBodyKind`（Empty/DisposePattern/SafeHandleOnly/NativeRelease/Other/Extern）、`SuppressFinalizePairing`（Paired/Unpaired/None）、`FinalizerOptions`（决策 9）、`FinalizerStatistics`、`FinalizerDiagnostic`、`FinalizerFileResult` |
| `FinalizerPlan.cs` | `FinalizerPlan`：含入类型 `INamedTypeSymbol`、`IMethodSymbol`（`MethodKind.Destructor`）、FQN、语法 span 与文件、body 原文、`FinalizerBodyKind`、配对结论、基类链信息、`IsCriticalFinalizer`、`Outcome`（Reported/Removed/Refused/ForceRemoved）、FailCode/FailReason、TouchedFiles、OwnsTouch |
| `FinalizerAnalyzer.cs` | **整树一次**：枚举全部 `DestructorDeclarationSyntax`（经符号 `MethodKind.Destructor` 核实，partial 合并）；分类（§4.2）；沿 BaseType 链查基类终结器；查同类型 Dispose/SuppressFinalize 配对；清点 `GC.ReRegisterForFinalize`/`WaitForPendingFinalizers` 站点；CriticalFinalizerObject 判定（含跨程序集基类）；产出确定性排序（文件+span）的 plan 列表 |
| `FinalizerBodyClassifier.cs` | 体分类纯逻辑：空体判定；`Dispose(false)`/`Dispose()` 单一有效语句形态（含 expression body 不适用——终结器只有块体）；方法体/字段访问符号流是否仅限 SafeHandle 派生字段；`Marshal.FreeHGlobal/FreeCoTaskMem/DestroyStructure`、`NativeLibrary.Free`、extern 释放调用识别；其余落 Other；无体外存终结器落 Extern |
| `FinalizerRewriter.cs` | **仅 B 档生效**：从类型成员列表移除终结器节点（保持 trivia/相邻成员布局，过族 `FormatAndValidate`）；按选项发射原位注释或提取方法（默认全关，见决策 3）；A 档不注册任何重写（Changed=false） |
| `FinalizerSidecarReport.cs` | sidecar 读写：`.cs2j-member-desugar-src/.cs2j-finalizer-removals.json`，条目含 run 标识、类型 FQN、文件、(line,col)、分类、配对、基类链、body 原文（Base64/原文 JSON 转义保 BOM 无关）、force 标记；run 开始重置、逐 plan 追加；路径进统计与摘要 |
| `FinalizerResidualScanner.cs` | AfterFiles：整树统计 `DestructorDeclarationSyntax`，与 plan 集对账：A 档残余数必须 == Reported/Refused 登记数；B 档残余数必须 == 0（force 白名单未覆盖项不得消失） |
| `FinalizerRule.cs` | `IMemberDesugarRule` 实现（规则 id `"finalizers"`，Kind=Finalizer）：贡献 plan、按 Policy 挂/不挂 rewriter、统计段与摘要片段、OwnsTouch、无支撑库声明；签名遵循 C11 锚点契约，差异 M1 对齐（同 C13 附 Q1 处理法） |
| `FinalizerRuleRegistration.cs` | 位于独占目录内的注册点，由锚点发现机制收集（显式 Add 或程序集扫描由 C11 定） |

编译构造沿用族标准（`LanguageVersion.Latest` + 全量 references + `allowUnsafe:true`，终结器体常含 unsafe/Marshal 调用；分析只查**原始** SemanticModel）。

### 4.2 规则全集（FN-x）

- **FN-1 发现与建 plan**：整树枚举终结器声明并核实 `MethodKind.Destructor`；partial 同类型合并；每终结器一 plan（终结器每类型至多一个，无重载）；跨程序集基类信息缺失时标注 `BaseInfoDegraded` 不报错。
- **FN-2 体分类**：Empty / DisposePattern / SafeHandleOnly / NativeRelease / Other / Extern 六分类（决策 4），分类依据全部来自符号绑定（调用法、字段类型），`Debug.Assert`/注释等噪声语句允许在 Empty/DisposePattern 中出现（仅 trivia/无副作用调用白名单）。
- **FN-3 Dispose 配对分析**：同类型 `IDisposable.Dispose()`/`Dispose(bool)` 存在性 + `GC.SuppressFinalize(this)` 调用点；paired + DisposePattern 是「人工确认删除」最低风险组合（仍需人确认，不自动删）；unpaired 终结器在报告中升级措辞（兜底逻辑无确定性入口，删除后无任何调用路径）。
- **FN-4 继承链分析**：记录基类终结器链（含库类型，如 sdk 的 FileCleanupTestBase 自身即基类）；派生与基类各有独立 plan；B 档仅删本类型 plan，不触碰基类文件；解释并在报告中写明「源级删除后基类终结器仍独立执行」（决策 6）。
- **FN-5 A 档显式拒绝**：不重写，每处发 CS2J5200（出厂 Warning/strict Error，门槛后默认 Error），消息含 `[Ns.Type]`、分类、配对结论、建议动作（改 Dispose 或选 Remove+force）。
- **FN-6 B 档删除**：`Policy=Remove` 时移除成员；Empty/SafeHandleOnly/DisposePattern(paired) 可直接删（CS2J5201）；Other/NativeRelease 无 force 白名单 → CS2J5204 拒绝（回退为保留+报码）；`CriticalFinalizerObject` → CS2J5205 始终拒绝；删除前写 sidecar（FN-8）；plan 原子（单成员声明，天然原子）。
- **FN-7 GC 生命周期 API 清点**：`GC.SuppressFinalize`/`GC.ReRegisterForFinalize`/`GC.WaitForPendingFinalizers`/`GC.Collect` 调用站点按含入类型汇总（CS2J5206）；resurrection 站点（ReRegister）对所在类型的 plan 标高风险，B 档默认拒绝（同 CS2J5204 路径）。
- **FN-8 body 留痕**：每次实际删除写 sidecar 一条 + CS2J5201（含 sidecar 路径与条目序号）；可选项 EmitRemovalMarkerComment / ExtractRemovedBody 按决策 3 默认关；提取方法名 `Cs2jRemovedFinalizer_<TypeName>`（ASCII 命名、private、无调用，转换器侧死代码风险文档化）。
- **FN-9 转换器 fail-fast 建议（不在本规则代码内）**：见决策 12 与 §6 末；作为转换器后续 PR 与 M1 同步交付，未交付前本规则是唯一信号源（默认关期间存在信号真空，§10-3）。
- **FN-10 residual 对账**：见 §7.4；A 档「有码必在树、无码必无树」、B 档「树中零终结器（除登记保留项）」。

### 4.3 数据流

1. Analyze（整树一次，经族执行器）：建全部 FinalizerPlan（终结器无使用点，**无使用点 rewriter 阶段**，比 C11-C14 少一阶段）；
2. 计划期定案 Outcome：Reject 全 Reported；Remove 按分类/force/Critical 分流 Removed/ForceRemoved/Refused；
3. Rewrite（逐文件，仅声明侧）：移除节点 → FormatAndValidate 三候选 → Changed 判定；
4. AfterFiles：sidecar 落盘 + residual 扫描；
5. 回退：整树重编译新增错误（理论面极小：无调用点）经族框架按 TouchedFiles 归属；无法归属走族级 CS2J5100 全回退；
6. 单文件模式：终结器声明自包含，删除可在单文件完成；跨文件基类链分析降级（CS2J5211），分类保守化（SafeHandleOnly 等依赖全编译的判定退化为 Other）。

---

## 5. CLI 集成

C39 **不改 Program.cs、不增适配器、不增开关/verb**，全部经 C11 锚点建立的族适配器承载，约定汇入方式（对齐 C13 §5）：

1. **执行**：族适配器整树标准骨架（Bind/Analyze → 逐文件 Rewrite → AfterFiles 残余 → restore/build/test verify，verify 双默认开）；C39 无使用点阶段、无支撑库，适配器无需为它新增钩子。
2. **选项透传**：族 options 增 `rules.finalizers` 子节（决策 9）；经 `preprocess --pass member-desugar` 与 convert-project 链上到达；不新增 CLI 参数；配置文件缺失/解析失败按族惯例 Warning 空集继续。
3. **统计汇入**：`FinalizerStatistics` 投影通用 5 字段（FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount）；特有计数 FinalizersDetected、ByKind（六分类分项）、Rejected、Removed、ForceRemoved、CriticalBlocked、ResurrectionSites、SuppressFinalizeSites、ChainsWithBaseFinalizer、SidecarEntries。族摘要行追加：
   `member-desugar: … finalizers: D detected (empty E / dispose-pattern P / safe-handle S / native N / other O / extern X), R reported, M removed (sidecar: <path>), B blocked, K residual`。
4. **诊断汇入**：经族统一诊断选择器映射 `PassDiagnostic`（Severity/Code/Message/FilePath/Line），MSBuild 格式；Info（CS2J5202/5203/5206/5207）仅 verbose。
5. **sidecar 生命周期**：位于中间目录根部，非 .cs 不参与编译与 Java 转换；run 开始重置；路径在摘要行与 CS2J5201 消息中给出；不进指纹快照、不需 preserve 接线（随中间目录存亡）。

### 5.1 Java 半区消费清单（转换器后续 PR 的输入，本特性不实施）

1. 决策 12 的 fail-fast PR：`DestructorDeclarationSyntax` 显式抛 + 枚举后的 `default` + `IncompleteMemberSyntax` 例外；
2. B 档产物中已无终结器，转换器无需学习 `finalize` 发射——**明确永不发射 `@Override protected void finalize()`**（Java 9+ 废弃，forRemoval）；
3. 模式 C 若未来立项：转换器需识别 `Cs2jCleaner.Register(...)` 形态映射 `java.lang.ref.Cleaner` + compat 新类，需独立设计与 Java 半区验证（M3+ 观察项）。

---

## 6. 诊断策略（每处终结器具名报码，禁止零信号丢弃）

使用 **CS2J5200–CS2J5219** 码段：C11 锚点已在其 §11 为 C39 建议 `Finalizers/` + CS2J52xx（[C11 L388](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L388-L388)）；族公共回退/restore/build/test 码沿用锚点 CS2J5100-5104，本规则不重复定义。码位只追加不改号；**最终码位表由 C11 锚点统一登记**（C13 对 MD2xx 采用同一预留惯例，附 Q2）；若锚点改按族内子段编号，M1 做纯常量改名。格式：`文件(行,列): warning/error CS2J52xx: [Ns.Type] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5200 | Warning（strict Error；§8 门槛后默认 Error） | A 档显式拒绝：终结器保留在树中，须人工迁移为确定性 Dispose 或显式选择删除；消息带分类与配对结论 |
| CS2J5201 | Warning（不静默；不因 strict 降为 Info） | B 档终结器已删除；消息带 sidecar 路径与条目序号、body 哈希前缀 |
| CS2J5202 | Info | 识别为标准 Dispose 模式（`Dispose(false)` + `GC.SuppressFinalize` 配对），仅 verbose |
| CS2J5203 | Info | 冗余终结器提示：空体或仅操作 SafeHandle 字段（SafeHandle 自带关键终结器），仅 verbose |
| CS2J5204 | Warning（strict Error） | B 档拒绝删除：body 分类 NativeRelease/Other/Extern 或类型含 resurrection 站点，且不在 ForceRemoveTypes；按 A 档保留处理 |
| CS2J5205 | Error（不可经 force 解除；仅配置显式豁免可过且仍留 Error 日志） | 类型派生自 CriticalFinalizerObject，关键终结器不允许自动删除 |
| CS2J5206 | Info | GC 生命周期 API 站点清点（SuppressFinalize/ReRegisterForFinalize/WaitForPendingFinalizers），仅 verbose |
| CS2J5207 | Info | 基类终结器链信息（基类 FQN/是否跨程序集），仅 verbose |
| CS2J5208 | Error | options 配置 `Policy=CleanerGuide`：v1 未实现（远期路线见设计 §2-8） |
| CS2J5210 | Warning（strict Error） | residual 闸门：终结器登记与输出树对账不平（§7.4） |
| CS2J5211 | Warning | 单文件模式：基类链/全编译分类降级，保守按 Other 处理 |

原则：

1. **零信号丢弃即事故**：与 [catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110) 一致——不允许「删了无码」「留着无码」两种状态；
2. A/B 模式分级分明：A 档信号在树（成员保留）、B 档信号在 sidecar + Warning（成员消失），两档都可审计；
3. Warning 经族 `Strict`（默认 true）升 Error；唯一「默认关的警告」是 M1 出厂严重级（决策 2 的过渡期），§8 门槛后翻 Error；
4. 跳过/拒绝全部按 `SkippedByReason: Dictionary<code,int>` 分桶进统计与摘要，供 sdk 基线盘点；
5. **转换器侧 fail-fast 建议（后续 PR，FN-9）**：在 [ClassTransformer.cs:337-379](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L337-L379) 的 switch 增 `case DestructorDeclarationSyntax` 显式抛 NotSupportedException（文案仿 [L348-349](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L348-L349) extern），并增 `default:` 对未知成员种类抛异常；`IncompleteMemberSyntax` 显式容错。它与本规则构成双保险：pass 未启用时转换器响亮失败，pass 启用时预处理显式诊断。该 PR 改转换器，归独立后续 PR（与 M1 同步），不在本规则 worktree 内。

---

## 7. 测试计划

### 7.1 Core 单测

新增 `tests/CSharpToJava.Tests/MemberDesugarFinalizerTests.cs`（内联源码 → `CSharpCompilation` → 族规则 Bind/Analyze/RewriteFile → 忽略空白断言 + `AssertOutputCompiles`，仿 C13 单测范式）：

1. **基本形态**：`class C { ~C() { } }` → A 档 CS2J5200 + 零改动 + 输出可编译；B 档（Empty）→ 成员消失 + CS2J5201 + sidecar 条目 + 输出可编译；
2. **继承链**：基类/派生类各带终结器 → 两个 plan、CS2J5207 链信息；B 档仅删派生时基类文件字节不变；中间树 .NET 行为用例：删除派生终结器后构造派生对象 + `GC.Collect/WaitForPendingFinalizers`，基类终结器仍被调用（决策 6 可证）；
3. **SuppressFinalize 配对**：sdk 同形 `~T(){Dispose(false);}` + `Dispose(){Dispose(true);GC.SuppressFinalize(this);}` → DisposePattern+Paired/CS2J5202；有终结器无 Dispose → Unpaired 措辞；有 `GC.ReRegisterForFinalize` → B 档 CS2J5204 拒绝；
4. **SafeHandle**：终结器体仅 `_handle.Dispose()`/`_handle.ReleaseHandle()` 触 SafeHandle 字段 → SafeHandleOnly/CS2J5203；混合访问普通字段 → Other；
5. **NativeRelease/Other**：体含 `Marshal.FreeHGlobal(ptr)` → NativeRelease，B 档无 force 拒绝（CS2J5204），force 白名单后删除且 sidecar 留全文；体含 `_writer.Flush()`/自定义逻辑 → Other 同路径；
6. **CriticalFinalizerObject**：`class C : CriticalFinalizerObject { ~C(){...} }` → 任何 Policy 下 CS2J5205 Error，成员保留；
7. **空/extern/噪声体**：纯空体、仅 `Debug.Assert`/注释、无体 `extern ~C();` → 对应分类；extern 落 Extern，B 档拒绝；
8. **partial/嵌套**：终结器在 partial 第二部件；嵌套类终结器；record（class 语义）终结器——按符号合并为同一 plan；
9. **可选项**：EmitRemovalMarkerComment 原位注释文本断言；ExtractRemovedBody 生成 `Cs2jRemovedFinalizer_C` 且无调用；默认两者关时输出不含注释/新方法；
10. **边界零误伤**：属性/事件/索引器/运算符/普通方法名为 `Finalize`/`Dispose` 的成员不被识别（终结器只认 `DestructorDeclarationSyntax` + `MethodKind.Destructor`）；`GC.SuppressFinalize` 调用树原样保留（文本零 diff）；
11. **工程性质**：幂等（A/B 两档各跑两次）；干净文件字节级一致 + BOM 保持；sidecar 重置/追加；单文件模式 CS2J5211 降级；CleanerGuide 配置 → CS2J5208；
12. **回退**：注入可归属编译错误的测试 hook，断言 plan 回退后产物与输入一致（终结器无调用点，预期稳态零回退）。

### 7.2 项目级 fixture

优先并入 C11 建立的族共享解决方案 `tests/SampleMemberDesugarSolution/`，C39 只新增 `Finalizers*` 前缀夹具文件（`FinalizersBasic.cs`/`FinalizersInheritance.cs`/`FinalizersApp.cs`），与 `Properties*`/`Events*` 文件并列；若合入时族 fixture 尚不存在，先建独立 `tests/SampleFinalizerSolution/`（仿 [SampleRecordSolution](file:///d:/code/cs2j/tests/SampleRecordSolution) 范式），后续并入：

- Lib：空终结器类；标准 Dispose 模式类（sdk FileCleanupTestBase 同形）；派生+基类双终结器层级；SafeHandle 字段类；NativeRelease 反例类；CriticalFinalizerObject 反例类；resurrection 反例类；
- App/Tests（.NET 半区守门）：B 档删除后，Dispose 确定性路径行为 xunit 断言全绿（证明人工迁移目标形态可用）；GC 收集后基类终结器仍执行的用例；
- 项目级测试 `FinalizerProjectTests.cs`：`result.Success`、统计计数、A 档中间树仍含终结器且诊断齐全、B 档中间树文本断言零 `~` 终结器声明、sidecar 条目数与删除数一致、`dotnet build -c Release` 与 `dotnet test -c Release` 退出码 0。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三个测试工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests；
- 不新增对转换器现有测试的修改（终结器测试当前零命中，无红灯基线）。

### 7.4 residual 闸门（B 档零 DestructorDeclaration）

1. **B 档硬闸门**：输出树 `DestructorDeclarationSyntax` 计数 == 0（ForceRemoveTypes 已覆盖的删除项；Refused 项保留则必须有对应 CS2J5204/5205 且该项不得计入「应删」集合——strict 下 Refused=Error 使运行失败，语义闭合）；
2. **A 档对账闸门**：输出树每个终结器必须命中一条 CS2J5200/CS2J5204/CS2J5205，且每条报码对应树中一个终结器，双向对账；
3. sidecar 条目数 == CS2J5201 条数 == 输入/输出终结器计数差；
4. sdk 基线：模式 A 全量跑 `D:\csharp-sdk`，期望恰好 2 处报码（§1.3 两文件）、零新增 build 错误（A 档不改树）；模式 B（放开 force 演练）下 2 处处置人工定性后零残留。

### 7.5 CLI / 注册表侧

- C11 合入前不新增 CLI 测试（启用 member-desugar 仍 CS2J9001）；
- C11 合入后：`--list-passes` 的 catalogItems 含 C39（追加后）；摘要含 finalizers 段；`rules.finalizers:Policy=Remove` 配置端到端；`--disable-pass member-desugar` 时无中间目录、无 sidecar、无诊断；
- 不新增任何 C39 专属注册表/链序断言（无新 pass/目录；枚举 Kind 发现的单测随 C11 规则发现测试）。

---

## 8. 默认开启验收门槛

终结器规则**无独立 pass 默认态**，随 member-desugar 族开启而生效；规则本身小（仅声明侧、无使用点），可在 C11 锚点后较早开启，但默认**动作严重级**走渐进：

1. **M1 出厂**：Policy=Reject，严重级 Warning（Strict 升 Error）；转换器 FN-9 安全网 PR 已合（pass 关时仍响亮失败）；sdk 全量恰好 2 处报码并完成逐处人工定性（sdk 两例均为 DisposePattern+Paired）；
2. **翻 Error 前（随族默认开评审）**：
   - slnx Release build + 三个测试工程 Release test 全绿；fixture A/B 两档中间树 build/test 全绿；幂等/字节复制/sidecar/residual 闸门全绿；
   - sdk 与既有全部 fixture 中 CS2J5200 清单逐条 triage，每条有「已迁移 Dispose / ForceRemove 确认 / 误报修复分析器」三类归宿之一，无未定性条目；
   - NativeRelease/Other/Critical 分类在真实样本上的误报率抽检（构造反例单测覆盖主要 Marshal/NativeLibrary 形态）；
   - 族级 §8 门槛（C11/C12-C14 各自项）同步满足；
3. 翻转动作：严重级默认 Warning → Error 一行 + 指纹一次性失效（可接受）；Policy 默认保持 Reject 不变（B 永远是显式选项，不做默认删除）；
4. 不满足时长期保持「Reject + Warning」，每评审周期复跑 sdk 清单——即使停在 Warning，也已消灭「无信号」状态（配合转换器安全网），满足本任务根本目标。

---

## 9. 里程碑

| 里程碑 | 内容 | 验证/规模 |
|---|---|---|
| M1 显式诊断 + 转换器安全网 | `Finalizers/` 骨架（Models/Plan/Analyzer/Classifier/Rule/Registration）+ FN-1~FN-5/FN-7 + A 档全链路（出厂 Warning）+ §7.1 单测主体 + sdk 测量（期望 2 处）；**同步推动转换器 PR**：`DestructorDeclarationSyntax` 显式 case fail-fast + 成员种类枚举后的 `default` + IncompleteMember 例外 | Core ~400 行；转换器 PR ~30 行 + 全量成员种类举证；sdk 2/2 报码 |
| M2 删除模式 B | FN-6/FN-8/FN-10：Rewriter 删除 + force 白名单 + CS2J5204/5205 拒绝路径 + sidecar 报告 + 两个可选项 + 统计/摘要/CLI 接线 + 族 fixture（Finalizers*）+ residual 闸门 + Release build/test 全绿 | ~300 行 + fixture；B 档 sdk 演练零残留 |
| M3 默认开证据包 | sdk triage 归档、严重级翻 Error 评审材料、与 C11-C14 族默认开联动；§5.1 转换器消费核查（确认永不发射 finalize） | 文档/数据 |
| M3+（不承诺，长期观察） | 模式 C Cleaner 引导：真实需求画像（非 Dispose 模式终结器占比）、compat `Cs2jCleaner` 设计、转换器映射 PR；无需求则永久搁置，Reject 即终态 | 独立设计 |

---

## 10. 风险

1. **资源泄漏语义误删（核心风险）**：终结器是某些非托管句柄（无 Dispose 调用路径时）的唯一释放点；B 档删除后 Java 进程期泄漏，OS 句柄未必随进程退出及时回收。缓解：A 为默认、B 需显式 Policy + 逐类型 force；NativeRelease/Other 默认拒删（CS2J5204）；Critical 永不自动删（CS2J5205）；body 全量 sidecar 留痕；§8 要求每条命中人工定性。**论证 Error 默认合理性**：命中率 2（假阳成本≈读两条 Warning 的时间），漏删代价是不可见的资源/数据语义丢失——非对称损失下「宁假阳」是唯一合理选择。
2. **终结器被依赖做关键刷新时的行为改变**：如日志/文件 `Flush()`、XmlElementList 式 Dispose(false) 兜底——GC 时刷新本就不可靠，但用户可能事实上依赖进程退出前的 GC 节奏。缓解：Other 分类拒删 + 人工评审；文档明确「迁移目标是确定性 Dispose/using，而非找另一个非确定性钩子」；.NET 半区 fixture 对 Dispose 路径行为守门。
3. **默认关期信号真空**：member-desugar 默认关且转换器安全网未合时，终结器仍被静默丢弃。缓解：M1 把转换器 fail-fast PR 列为同步交付（FN-9）；安全网先于规则默认开独立生效。
4. **转换器 `default` 的误伤面**：ProcessMembers 还承担 partial 合并（[ClassTransformer.cs:258-297](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L258-L297)），未知成员种类理论上还包括 `IncompleteMemberSyntax`（错误语法树）等；贸然 `default throw` 可能让当前靠静默吞掉容错的输入变红。缓解：转换器 PR 先枚举 Roslyn 全部 MemberDeclarationSyntax 派生种类逐一举证去向，IncompleteMember 显式容错并（可选）发 Warning；用现有全量测试工程回归守门；该 PR 独立于本规则评审。
5. **终结器语义细节误判**：异常吞没、resurrection、finalizer 线程、CER 保证等无法在 C# 源码层完整验证。缓解：分析器只做保守分类不做语义承诺；跨程序集基类信息缺失降级（CS2J5211）；ReRegister 站点直接升高拒绝等级。
6. **sidecar 可发现性**：删除记录在中间目录，用户可能不看。缓解：CS2J5201 消息内嵌路径与条目序号、摘要行汇总、strict 下 Refused/对账不平升 Error；sidecar 不随产物分发，仅是审计日志。
7. **与 C06/C42 平台面重叠**：SafeHandle/Marshal 既是终结器体分类依据也是平台隔离对象，可能双报码。缓解：C39 只报终结器成员码（CS2J52xx），平台合规归 C42 的 CS2J30xx/31xx；消息中交叉引用但不重复阻断；顺序上 C39 在 Declarative 先跑、C42 在 ABI 后跑，各自独立。
8. **族共享层演进**：新增 `MemberDesugarKind.Finalizer` 触及 C11 所有的枚举文件。缓解：纯追加枚举值 + 规则发现单测；由锚点合入后的首个接触点一次性补齐（§11）。
9. **Cleaner 路线的复活陷阱（远期备忘）**：Cleaner 的 cleaning action 若捕获 target 即构成复活，状态对象必须独立——这也是 v1 不机械降级的理由之一。

---

## 11. 并行协调

| 并行条目 | 关系 | 协调动作 |
|---|---|---|
| **C11 Property（锚点）** | 硬前置：族基建、规则发现、plan/回退/统计/适配器、stub 翻转全由 C11 建立；C11 §11 已预留 `Finalizers/` + CS2J52xx | C39 在 C11 合入后 rebase 开工；需要锚点共享层一处增量：`MemberDesugarKind` 追加 `Finalizer`（在 C11 分支补或合入后首个小 PR，附 property 回归）；规则注册点放 C39 独占目录；本设计 §4 接缝签名若与锚点实现在 M1 有差异，按锚点同名适配（同 C13 附 Q1） |
| **C12 Indexer / C13 Event / C14 Operator** | 同一 pass、同一 stub、同一共享层；均为「声明 + 使用点」规则，C39 是唯一纯声明侧小规则 | 目录各自独立（`Properties/Indexers/Events/Operators/Finalizers`）；不碰别人文件；C39 不使用 BackingFieldNaming/AccessorsToMethods（无成员生成），共享面仅 Analyzer 外壳/Plan 基类/FormatAndValidate/规则发现；码段位互不重叠（5130-5149/5150-5169/5170-5189/5200-5219；C46 建议 5190-5199）；合入顺序任意，catalogItems 一行追加冲突按序 rebase |
| **C06 unsafe-ffm / C42 platform-isolation** | SafeHandle/Marshal/非托管释放是终结器体高频内容；C42 已握 SafeHandle 阻断清单（[UnsupportedDomainPasses.cs:246-251](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L246-L251)） | 无代码依赖：分类用 SemanticModel 自判；C39 不替 C42 判定平台合规、不发 CS2J30xx 码；文档互引；C42 若先落地导致类型被整体隔离，C39 仍发成员码（报告完整），由执行结果去重展示 |
| **C36 语法防线** | 结论：不归 C36（§3.2）；两防线互补不双报 | C36 未来若加未识别成员盘点，终结器面以 C39 为唯一报码源 |
| **C07 struct-value-semantics / Dispose** | Dispose 模式是本规则的**人工迁移目标**，但语义不同（确定性 vs 非确定性）；RunAfter 锚边已间接锁定 member-desugar 晚于 struct-value-semantics | C39 不改写 Dispose 方法、不改写 `GC.SuppressFinalize`；不向 C07 提需求；仅在诊断消息中建议「迁移到 Dispose/using」 |
| **C08 java-keywords（链尾）** | B 档只删成员；可选提取方法名 `Cs2jRemovedFinalizer_T` 为 ASCII 安全标识；sidecar 不进转换 | 无命名交互；若开启 ExtractRemovedBody，方法名经正常 MethodTransformer 链路，无需特殊处理 |
| **公共热文件** | `MemberDesugar/` 根共享文件、族适配器（C11 所有）、PassCatalog catalogItems 字符串、Program.cs 选项工厂 | C39 只新建 `Finalizers/` 与自有测试/fixture 文件；枚举增量与 anchor 协调；不碰适配器、注册表描述符、Program.cs；rebase 追加行解决冲突，不重排族规则顺序 |
| **转换器安全网 PR（FN-9）** | 改 [ClassTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs)，与本规则 worktree 不同交付面 | 建议 M1 同期独立 PR（文档 + 小改动，先做成员种类枚举举证）；两 PR 无代码依赖，任先合入都消除一部分静默面 |

---

## 12. Worktree 分配

- 分支：`feature/preproc-c39-finalizer-strip`
- 目录：`.worktrees/preproc-c39-finalizer-strip`
- base：`28ae4594`（`fix(test): restore C34 unsupported site in SampleNumericBoundaryUnsupportedApp`，dev-3.x 2026-10-01 最新提交，已用 `git cat-file -t` 核实为 commit；C11–C14 锚点已合入该基线，`MemberDesugar/` 共享基建在场，直接开工，无需再 rebase；历史 base `953293b9` 与裁决时 HEAD `ef70e5a2` 已被本基线取代）
- 创建命令（仓库根、PowerShell；2026-10-01 实施时执行）：

```powershell
git worktree add .worktrees/preproc-c39-finalizer-strip -b feature/preproc-c39-finalizer-strip 28ae4594
```

实施约束（交实施 worktree）：仅在该 worktree 内改码；base 已含 `MemberDesugar/` 共享基建（C11–C14），无需 rebase；按 §9 里程碑逐提交；不注册/翻转任何 pass、不动 RunAfterIds/注册表描述符/Program.cs；转换器 FN-9 改动走独立同期 PR；族 pass 默认开启与严重级翻转以 §8 门槛为准，不抢跑；交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持 base `953293b9`，创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。**【2026-10-01 实施更新】** C11–C14 锚点已由维护者合入主干，worktree base 前移到 dev-3.x 最新提交 `28ae4594`（已含 `MemberDesugar/` 共享基建），直接从该 base 创建分支、无需 rebase；本条历史裁决原文（`953293b9`/`ef70e5a2`）保留以备追溯。

**裁决（按原题号）：**

1. **选型确认**：采纳推荐 **A（Reject）**：默认显式报码、不重写；B（Remove+sidecar）保留为显式逃生口；C（Cleaner）v1 不实现且不预留半吊子挂钩。严重级走 **M1 出厂 Warning → 门槛后 Error** 的渐进（否决直接出厂 Error：sdk 两处真实文件会被立刻卡死，Warning 画像期先取得终结器站点的真实分布与处置确认）。终结器在 Java 侧确无语义等价物（finalize 已弃用且不等价），门槛后长期保持 Error 是合法终态，不违反"全语义支持"口径——该构造的登记终态去向就是"显式拒绝+人工改造指路"。
2. **B 档 body 留痕形态**：默认 **sidecar JSON、树内零痕迹**（否决原位注释与方法提取：二者都会让死代码/注释流入 Java 产物）；sidecar 与源码同根受控、随审计产物可 diff，原位形态不提供开关。
3. **`MemberDesugarKind.Finalizer` 落点**：不阻塞 C11 锚点分支；由 **C39 在 C11 合入后以首个小 PR 追加**枚举值与交接实现，C11/C39 交接清单据此写定（锚点只保证枚举可加性，不预置空值）。
4. **FN-9 安全网范围**：只加 **`DestructorDeclarationSyntax` 显式 case**（最小、零误伤）；同期不加 `default` 抛异常（完整成员种类枚举举证成本不值当）。`IncompleteMemberSyntax` 容错策略 = **发 Warning 不静默跳过**（残缺成员可能正藏终结器，静默即漏报）。
5. **码段位**：占 **CS2J5200-5219，以 C11 锚点统一登记为准**；若锚点改用 C13 式 MD 子段编号，M1 纯常量改名、不改设计语义，号段登记结果回填本文。

---

### 原问题记录（裁决前）

1. **选型确认（推荐 A）**：默认策略 Reject（显式报码、不重写），B（Remove+sidecar）仅作显式逃生口，C（Cleaner）v1 不实现——是否同意「宁假阳」与 Warning→Error 渐进（M1 出厂 Warning、门槛后 Error）？备选意见：直接出厂 Error（最响，但会立刻卡住 sdk 两处真实文件）。
2. **B 档 body 留痕形态**：默认 sidecar JSON、树内零痕迹（本设计推荐，避免注释流入 Java 产物）；是否需要默认开启原位注释或方法提取？后两者的死代码/产物污染风险是否可接受？
3. **`MemberDesugarKind.Finalizer` 枚举增量的落点**：由 C11 锚点分支补（合入前阻塞 C39 M1），还是 C39 开工时在已合入的锚点上以首个小 PR 追加？影响 C11/C39 的交接清单。
4. **转换器安全网（FN-9）的交付时机与范围**：只加 `DestructorDeclarationSyntax` 显式 case（最小、零误伤），还是同期加全 `default` 抛异常（需完整成员种类枚举举证）？`IncompleteMemberSyntax` 的容错策略（静默跳过 vs Warning）需转换器评审定。
5. **码段位确认**：按 C11 §11 建议占 CS2J5200-5219（真实码，锚点统一登记）；若锚点最终采用 C13 式 MD 子段编号（如 MD5xx），M1 纯常量改名——以锚点登记为准。
