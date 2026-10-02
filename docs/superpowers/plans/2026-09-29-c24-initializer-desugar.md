# C24 对象/集合初始化器归一 — 实施计划（2026-10-02）

**设计**：[2026-09-29-c24-initializer-desugar-design.md](../specs/2026-09-29-c24-initializer-desugar-design.md)
**worktree**：`.worktrees/preproc-c24-initializer-desugar`，分支 `feature/preproc-c24-initializer-desugar`
**base**：`4ea50d44`（= dev-3.x `10aae03f` + C24 设计文档 base 前移提交）

## 0. 基线裁决（与设计文档差异点）

截至 base：C23 锚点未合入；C36 已按独立落地先例把 `expression-desugar` stub 翻转
（Implemented、**默认关**、catalogItems `"C36"`）并在 dev-3.x 落地最小族基建
（`src/CSharpToJava.Core/ExpressionDesugar/Shared/` + `CollectionExpr/`，
`ExpressionDesugarRuleSet` 已按固定序 `Interpolations(C23)→Initializers(C24)→NullConditional(C35)→CollectionExpr(C36)` 为 C24 预留第二追加位）。
C24 沿同一先例**只增落地**：

1. **不改 pass 注册状态**：`expression-desugar` 已 Implemented/默认关；不翻转 stub、不新增
   pass id/适配器/verb/中间目录；RunAfterIds 沿用 `null-coalesce`；中间目录沿用
   `.cs2j-expression-desugar-src`。
2. **族基建以 C36 实物为准**（设计正文中指向 C23 的前向引用全部替换为以下签名）：
   - `ExpressionDesugarRuleSet.Rules`：在第二追加位注册 `new InitializersRule()`（C36 末位不动）；
   - `ExpressionDesugarOptions`：追加 `public InitializerOptions? Initializers { get; init; }`
     （nullable；`InitializerOptions` 为 public sealed class，缺省视为 `new()`）；
   - `ExpressionDesugarStatistics`：追加 `public InitializerStatistics Initializers { get; } = new();`
     + `MergeFrom` 一行；
   - `ExpressionDesugarPreprocessor`：按 C36 同款**只增**补齐 Initializers 段计数
     （FilesSkippedClean/FilesDirtyNoRewrite/FilesTransformed/TempsLifted）；C36 行为零改动；
   - CLI `ExpressionDesugarPassAdapter`：**只增** init 摘要段 + AfterFiles residual 按语法种类
     扩展（新增 `InitializerTreeGate`，不改 C36 既有配对口径）；`PassCatalog.cs` 一行
     catalogItems 加宽 `"C36, C24"`；`PassChainOrderTests` 断言一行。
3. **复用**（不改签名）：`ExpressionDesugarTempLifter`（`PendingTemp` 语句序列 + `Apply` +
   锚点注解）、`ExpressionDesugarTempNameAllocator`（`__ed_` 文件级发号）、
   `ExpressionDesugarSyntaxFacts.IsInExpressionTree/IsInLiftableContext`、
   `ExplainedAnnotation`/`ExplainedSubtreeAnnotation`。
4. **C24 自带 `InitializerContextLowering`**（C36 的 `CollectionContextLowering` 是其最小先行版）：
   不重构、不覆盖 C36 文件；如需泛化提取留族评审（设计 §3.2-4、裁决 4）。
5. **规则实现全部落 `Initializers/`**；不触碰 `Interpolations/*`（不存在）、`CollectionExpr/*`、
   `Transformers/**`、`TypeMappings.json`、compat 库；不新建支撑工程。
6. **Roslyn 版本以仓内实物 5.0.0 为准**（`CSharpToJava.Core.csproj`）；设计按 4.12 写的 API
   外形一律以 M0 探针实测收口（同 C36 M0 纪律）。
7. **诊断码** CS2J0530–CS2J0545（设计 §6），与 C36 057x/058x 码段不交叉；级别语义与 C36
   同口径：strict Error → 整文件原子不改写；非 strict 降 Warning 且节点保留（裁决 5 的
   摘要单列）。
8. **不在本次交付面**：M4（sdk 画像/默认开证据、构造函数迁移）、Java 半区核查（设计 §8-3）、
   转换器特判下掉 PR——登记为后续证据/消费 PR；本计划交付 M0→M3。

## 1. 目标目录结构

```
src/CSharpToJava.Core/ExpressionDesugar/Initializers/
  InitializerOptions.cs           # public：Strict/Scope(All|Object|Collection|TargetTypedNew)/RewriteInExpressionTrees/UnsupportedPolicy
  InitializerModels.cs            # InitializerPlan / InitializerMemberPlan(PropertySet/FieldSet/IndexSet/NestedCreate/NestedMemberInit/CollectionAdd) / TargetTypeInfo / InitializerContextKind / Outcome
  InitializerDiagnostics.cs       # CS2J0530-CS2J0545 常量 + 消息模板 + ResolveSeverity（同 C36 口径）
  InitializerStatistics.cs        # 设计 §5-3 全字段 + AddSkip + MergeFrom
  InitializerSyntaxFacts.cs       # residual 三类节点判定、初始化器项语法形状、排除谓词（数组/匿名对象）、Explained 配对
  InitializerBindingAnalyzer.cs   # IOperation 逐项绑定分类（决策 2/7/8/10/11/12）+ 目标类型推断 + 可提升性
  ObjectInitRewriter.cs           # 对象初始化器：构造语句 + 属性/字段/索引赋值序列 + 嵌套
  CollectionInitRewriter.cs       # 集合初始化器：构造实参保留 + Add（单参/多参/扩展/fluent/params）
  TargetTypedNewRewriter.cs       # 裸 new()/带初始化器 new(){…} 显式化（可空目标剥注解）
  InitializerContextLowering.cs   # 决策 11 语句化：声明/return/throw/赋值/表达式语句/using/三元 if-else/实参交错
  InitializerTreeGate.cs          # M3 AfterFiles 全树 residual 配对（三类节点 vs 配对 skip）
  InitializersRule.cs             # IExpressionDesugarRule 实现（Id="Initializers"）
tests/CSharpToJava.Tests/InitializerDesugarUnitTests.cs          # 设计 §7.1 矩阵
tests/CSharpToJava.ProjectTests/InitializerDesugarProjectTests.cs # M3 项目级（范式：CollectionExprProjectTests）
tests/SampleExpressionDesugarSolution/                            # M2/M3 增 Initializers* 前缀夹具（不建第二套 sln）
```

## 2. 集成点清单（全部只增不改）

| 文件 | 改动 |
|---|---|
| `Core/ExpressionDesugar/Shared/ExpressionDesugarRuleSet.cs` | 第二追加位 `new InitializersRule()` + 注释更新 |
| `Core/ExpressionDesugar/Shared/ExpressionDesugarOptions.cs` | 一个 nullable 属性 `Initializers` |
| `Core/ExpressionDesugar/Shared/ExpressionDesugarStatistics.cs` | 一个段属性 + `MergeFrom` 一行 |
| `Core/ExpressionDesugar/Shared/ExpressionDesugarPreprocessor.cs` | 只增 Initializers 段计数行（含 no-model 警告路径） |
| `CLI/Preprocessing/PassCatalog.cs` | `catalogItems: "C36"` → `"C36, C24"`（一行） |
| `CLI/Preprocessing/Adapters/ExpressionDesugarPassAdapter.cs` | 摘要 init 段 + AfterFiles 闸门按种类扩展 |
| `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs` | L243 断言改 `"C36, C24"` |

不加 convert-project legacy toggle、不动 `PassChainFactory.cs`/`Program.cs`（工厂已存在）、
不动 `PassStubCatalog.Stubs` 计数。

## 3. 阶段划分（每阶段：实现 → Release build/test 全绿 → 提交）

### M0 探针 + 骨架 + 注册登记（空转，默认关）

- `Initializers/` 全部文件骨架（类型/枚举/常量/字段齐备；Analyze 只产站点计数与 stats，
  **不发射任何改写、不产 Error**）；RuleSet/Options/Statistics/Preprocessor/catalogItems/
  PassChainOrderTests 集成点落地。
- **Roslyn 5.0.0 探针**（写成可运行的 xunit 事实，结果回填本文 §6）：
  1. `IObjectCreationOperation.Initializer` 成员项分类外形：属性 `IPropertyReferenceOperation`
     （SetValue）、字段写、嵌套成员初始化 `IMemberInitializerOperation`、集合元素
     `ICollectionElementInitializerOperation`（AddMethod/Arguments）、字典索引
     `ImplicitElementAccess`；
  2. Add 绑定外形：单参/多参（`ComplexElementInitializer`）/扩展 reduced/fluent/params 展开/
     IEnumerable 无 Add 的基线报错（CS1922/CS0117 同族）；
  3. init-only（`IPropertySymbol.SetMethod.IsInitOnly`）与 readonly 字段/get-only 属性基线
     CS0191/CS0200 复现；C11 放宽后的输入树形态（普通 set 方法 + 字段）对照；
  4. target-typed `new()` 各上下文 `GetTypeInfo(node).ConvertedType`：局部声明/return/实参/
     三元/字段初始化器/`: this()`；可空目标注解取底层 T；
  5. 字典索引初始化器 Operation 外形（`ImplicitElementAccessOperation` 或实际类型）；
  6. dirty 门谓词：`ObjectCreationExpressionSyntax.Initializer` / 集合初始化器元素 /
     `ImplicitObjectCreationExpressionSyntax`。
- 单测：探针事实 + clean 文件 `Changed=false` 字节保真 + 注册清单断言 + 含初始化器的 dirty
  文件 M0 空转（`Changed=false`、计数正确、输出原样）。
- 验收：`dotnet build -c Release CSharpToJavaConverter.slnx` + Tests 工程全绿。
- 提交：`feat(preprocessing): C24 initializers M0 探针+规则骨架+注册登记（默认关、空转）`

### M1 对象初始化器 + Add 主路径

- IN-1/2/3/6/9/12 主体：对象初始化器序列（属性保持属性写、字段直写，按运行时树符号；
  成员严格源码序）；构造实参保留（`new List<int>(8){…}` 不丢实参）；单参 Add 以
  `AddMethod` 绑定为准（不猜 `put`）；裸 `new()` 显式化为 `new T(…)`（含可空目标）；
  基础上下文：局部/using 声明 RHS、return、表达式语句。
- 诊断：CS2J0530/0531/0532/0533/0535/0536（strict Error → 整文件原子不动）。
- 单测：设计 §7.1 矩阵 1/2/3(部分)/5/7(基础)/9/11/12。
- 验收：Release build + Tests 全绿。
- 提交：`feat(preprocessing): C24 initializers M1 对象初始化器+Add 主路径（CS2J0530-0536）`

#### M1 落地记录（2026-10-02 回填）

**文件**（全部在允许面内）：
- 改：`src/CSharpToJava.Core/ExpressionDesugar/Initializers/{InitializerBindingAnalyzer, InitializerContextLowering, InitializerSyntaxFacts, InitializerModels, ObjectInitRewriter, CollectionInitRewriter, TargetTypedNewRewriter, InitializersRule}.cs`；
- 增：`src/CSharpToJava.Core/ExpressionDesugar/Initializers/InitializerRewriter.cs`（自底向上 `CSharpSyntaxRewriter`，M0 骨架中无对应文件）；
- 改：`tests/CSharpToJava.Tests/InitializerDesugarUnitTests.cs`（29 个 Fact：7 探针 + 1 注册 + 21 M1 行为）；
- 未改：`CollectionExpr/*`、`Shared/*`、CLI、其它测试文件（**C36 测试文件零改动**）。

**实现语义**：`IOperation` 逐项分类——对象项 `ISimpleAssignmentOperation` 的 `Target` 为 `IPropertyReferenceOperation`（保留属性写 `tmp.P = v`）/`IFieldReferenceOperation`（直写），严格源码序；构造实参原样保留（IN-3，`new List<int>(8){1,2}` 保 `(8)`）；集合项仅实例单参 `Add`（`Arguments.Length==1 && !IsExtensionMethod && !IsParams`，非 void fluent 同发 `Add` 丢弃返回值）；裸 `new()` 零临时显式化为 `new T(…)`（target-typed 类型用 `FullyQualifiedFormat` 渲染，可空目标天然取底层）；临时名 Analyze 预分配（`ExpressionDesugarTempNameAllocator`，post-order 内层先于外层），Rewrite 消费并经族 `TempLifter` 单次 Apply。基础上下文：局部/using 声明 RHS、return/yield、throw、赋值语句 RHS、表达式语句、表达式体成员。诊断 0530/0532/0536 Info；0531/0533/0535 strict Error（整文件原子不动）/非 strict Warning + 站点保留且兄弟站点照常改写；0531 仅在解析到 init-only/read-only/静态/不可解析成员时命中。

**测试与验证**：`dotnet build -c Release CSharpToJavaConverter.slnx` = **0 错误**；M0 阶段 `dotnet test` 报 **2548 通过 / 0 失败**、C24 12 Fact 全绿。
（注：M0 阶段用 `dotnet test --no-build` 复核过 C24/C36 定向 99 个用例。**M2 阶段发现问题**：`--no-build` 在只构建 slnx 时会跑旧测试 dll；
真实计数以 M2 记录的「含重编译」结果为准。）

**偏差与阻塞**：
1. **跨规则节点身份（族级技术债，已上报）**：族入口按规则顺序在**同一工作树**上渐进改写，而 C36 的 rewriter 以**原始 `SyntaxNode` 引用**为 plan key。**实测（探针，2026-10-02 复核）**：Roslyn 的 `CSharpSyntaxRewriter`/`ReplaceNode` 一旦改动文件中**任一**节点，**整棵树（含完全无关的兄弟语句/成员）在再遍历时都会产生新的红节点实例**，只有「零改动」才短路返回同一 root；故 C36 的原始节点引用 key **在 C24 有任何改写的文件里必然全部 miss** → C36 留下语法 → CS2J0589 → 整文件回退。**M1/M2 收口（正确性优先）**：**文件内出现任何 `CollectionExpressionSyntax` 时 C24 整文件让位**——站点只计数（`CoexistenceDeferredSites++`）、不做语义分类、不改写、不加注解、不产诊断，`InitializersRulePlan.Rewrite` 因此不触碰树，C36 照常改写；无 C36 语法的文件 C24 正常全量改写。让位文件在 C36 语法消除后的**下一轮**完成 C24 收敛（`M1_C36Coexistence_*` 与 `M1_C36Coexistence_RewriterIdentityIsNotPreserved` 锁定，后者把「身份不保持」固化为可执行事实）。**代价**：单轮内「同文件既有集合表达式又有初始化器」的站点不会当轮处理（CLI 单轮链下需再跑一轮）。**建议族级修复**（`CollectionExpr/*` + `Shared/ExpressionDesugarPreprocessor.cs`，本 worktree 允许面外，留 C36/族评审）：C36 plan 改按稳定注解/`(Span, SyntaxTree)` 取键，或族入口对每条规则在其改写前于当前树重跑 Analyze；二者均可使让位退役。
2. **空初始化器**：`new C { }` 折为 `new C()`（零临时，0530 Info）；`new T { }` 若 T 实现 `System.Collections.IEnumerable` 视为空集合初始化器而**保留**（C36 语句化临时即发射 `new List<T>(){}`，折写会破坏其二次运行字节稳定性）。
3. **诊断消息按站点计数**：0530/0532 消息使用本站点成员/Add 计数（非累计族统计）。
4. **默认关落地口径（重要修正）**：实测 `PassCatalog` 中 `expression-desugar` 为 `defaultEnabled: true`（§0-1「默认关」只对 C36 之前成立）。若 C24 永远激活，默认链上的既有工程夹具会被改写/被 0531 拒绝（实测 `SamplePartial...` 的 init-only 对象初始化器直接 Error，6 个 ProjectTests 失败）。M1 故以 **`ExpressionDesugarOptions.Initializers` 子节存在**为启用闸门：缺省时规则完全惰性、保持 M0 行为（仅计 dirty、字节不变、零诊断），与设计 §9「M0–M4 全程族 pass `DefaultEnabled=false`」一致；M4 默认开启时再评估 init-only 与 C11 链序。单测经 `C24Enabled` 显式开启。
   **遗留（M3 处理，勿忘）**：CLI 工厂（`Program.cs` 两处 `ExpressionDesugarOptionsFactory`）当前不构造 `Initializers` 子节，故 M1 的 C24 只有 Core 直调可达、经 CLI 不可达。M3「CLI 收口」须给出可用的灰度开关路径（沿用既有 options/配置面，不新增 pass/verb；设计 §5-1），并在 `--list-passes`/摘要中体现；在该路径落地前，C24 不参与任何默认链。
5. **M1 未做**（按计划留给 M2/M3）：CS2J0537/0538/0539/0540/0541/0545 与 residual 闸门；不支持的上下文与 M2 形状站点静默保留（带 explained 注解），M1 不产这几类码。

### M2 嵌套 + 字典 + 上下文语句化 + Add 变体

- IN-4/5/7/8/10/11：嵌套创建自底向上（内层临时先于外层成员赋值）；嵌套成员初始化 getter
  链单次求值临时；字典索引 `[k]=v` 索引赋值（键值各一次、源码序）；多参/扩展/fluent/params
  Add（CS2J0539 分类计数）；上下文语句化：三元 if-else 双支定指、`??`、赋值 RHS、throw、
  实参交错（前序实参提升）；CS2J0537/0538/0541/0542 跳过族。
- 单测：矩阵 2(行为探针)/3/4/6/7(全)/8/10；`SampleExpressionDesugarSolution` 增
  `Initializers*` 夹具初版（原生/预处理双跑）。
- 验收：Release build + Tests 全绿。
- 提交：`feat(preprocessing): C24 initializers M2 嵌套/字典/上下文语句化/Add 变体（CS2J0537-0542）`

#### M2 落地记录（2026-10-02 回填）

**文件**（全部在允许面内）：
- 改：`src/CSharpToJava.Core/ExpressionDesugar/Initializers/{InitializerModels,InitializerContextLowering,InitializerBindingAnalyzer,ObjectInitRewriter,CollectionInitRewriter,InitializerRewriter,InitializersRule}.cs`；
- 改：`tests/CSharpToJava.Tests/InitializerDesugarUnitTests.cs`（新增 M2 行为 17 个 Fact；改写 1 个 M1「M2 形状保留」锁定事实为 M2 改写事实；修正 1 个 M1 共处事实的错误期望 `new Point[]`→`new global::Point[]`）；
- 未改：`CollectionExpr/*`、`Shared/*`、CLI、其它测试文件。

**实现语义**：`IOperation` 逐项递归分类，`AnalyzeState` 统一持有发号器与 owner/hoist 映射；
顶层站点按文档序建 plan，嵌套 create / 集合元素 create / 嵌套成员初始化由父站点内联发射；
临时号全部在**分类成功后**统一分配（owner 结果临时 → hoist → 接收者 → 成员源序递归），
故非 strict 被跳过的站点不占号（`M1_NonStrict_...` 由 M1 的 `__ed_1` 回落为 `__ed_0` 语义修复）。
- IN-4 嵌套创建：`var __ed_0 = new O(); var __ed_1 = new I(); __ed_1.D = 5; __ed_0.Inner = __ed_1;`
  （外接收者先于内层，内层构造先于外层成员赋值；集合元素 create 同形内联）。
- IN-5 嵌套成员初始化：为成员访问链发一个 getter 临时 `var __ed_1 = __ed_0.Inner;` 后逐成员变异；
  多层 `A = { B = { C = 1 } }` 逐层发临时（CS2J0540 verbose Info）。
- IN-8 字典索引：`__ed_0[k] = v;`（多维同形；键值原样、源码序；CS2J0534 Info；非可写索引器 CS2J0531）。
- IN-7 Add 变体：统一 `__ed_0.Add(<源元素实参>)`——多参复杂元素 `Add(a, b)`（不发 `put`）、
  扩展按 reduced 语法重发（`tmp.Add(x)` 重绑定）、params 每源元素一次 `Add(x)`、fluent 表达式语句丢返回值；
  `AddSingle/AddMulti/AddExtension/AddParams/AddFluent` 分桶 + CS2J0539 Info。
- IN-11 上下文语句化：三元/`??` 合成带定指结果临时的 if/else（守卫不外提，S6）；实参位前序**有副作用**
  实参按左→右提升为临时（无副作用不提），`M(F(), new(){A=G()}, H())` → `var __ed_0 = F(); var __ed_1 = new Dto(); __ed_1.A = G(); M(__ed_0, __ed_1, H());`；
  局部/using/return/yield/throw/赋值/表达式语句/表达式体成员经族 TempLifter 语句化。
- 跳过族：CS2J0537（构造链实参、lambda/方法组推断目标）、CS2J0538（字段/属性初始化器、特性实参、
  默认参数、查询子句、三元分支内的间接站点）、CS2J0541（`dynamic` 接收者/动态 Add/动态索引）、
  CS2J0542（表达式树）齐全；strict Error 整文件原子不动，非 strict 降 Warning 且兄弟站点照常改写。

**测试与验证**：`dotnet build -c Release CSharpToJavaConverter.slnx` = **0 错误**；
`dotnet test -c Release tests/CSharpToJava.Tests`（**含测试工程重编译**）= **2585 通过 / 0 失败**
（§7 列出的 5 个基线失败未复现）。C24 单测 49 个全绿；C36 `CollectionExprDesugarUnitTests` 72/72 全绿。
**教训**：`dotnet test --no-build` 在只 build 了 slnx（未重建测试工程）时会跑旧 dll，M1 阶段的「2566/0」即受此影响；
此后验证一律让 `dotnet test` 自行重编译。

**偏差与阻塞**：
1. **共处让位在 M2 收口为整文件粒度（正确性优先，已实施）**：见 M1 记录第 1 条。M1 曾按「不相交即可同轮双改」窄化并写了绿灯台账，经
   `M1_C36Coexistence_RewriterIdentityIsNotPreserved` 探针证实该前提不成立（Roslyn 改写后整棵树红节点皆新实例），
   故 M2 改为**文件含任何集合表达式即整文件让位**：`BuildDeferredPlan` 只计数、不分类、不改写、不产诊断；
   `M1_C36Coexistence_C36SiteInFile_DefersWholeFile_ThenSecondRunCompletes` 与 `M1_C36Coexistence_NestedSiteDefers_OuterStillRewrites`
   锁定（C36 当轮照改、C24 站点原样、下一轮收敛）。
   **族级修复建议**：C36 plan 改按稳定注解/`(Span, SyntaxTree)` 取键（`CollectionExpr/*`），或族入口对每条规则在其改写前于当前树上重跑 Analyze（`Shared/ExpressionDesugarPreprocessor.cs`）——二者均在本 worktree 允许面外，按约束**未实施**，留待 C36/族评审。
2. **M1 台账的两处期望已按实物修正**：`M1_C36Coexistence_NestedSiteDefers` 期望 `new Point[]` 而 C36 对用户类型发
   `new global::Point[]`；`M1_M2ShapeSites_ArePreservedByteStable` 的 M1 断言已被 M2 改写打破，改写为
   `M2_FormerM1ShapeSites_AreNowRewritten`。
3. **CS2J0541 触发面**：Roslyn 5.0 无 `IDynamicIndexAccessOperation`，且 `new` 的接收者恒为静态类型，
   可编译输入里动态 Add 站点极难构造；实现按 `IDynamicInvocation/MemberReference/ObjectCreationOperation`
   与 `OperationKind` 名以 `Dynamic` 前缀兜底识别，M2 未新增动态负例单测（探针面已登记）。
4. **临时号分配时机修正**：M2 把临时号改为「分类成功后统一分配」（owner 结果临时 → hoist → 接收者 → 成员源序递归），
   非 strict 被跳过的站点不再占号（M1 的 `__ed_1` 回落为 `__ed_0`）。

### M3 residual 收口 + 共处回归 + fixture/CLI 交付

- IN-13：residual 三类节点闸门（对象/集合初始化器 + 裸 `new()`）CS2J0545：文件级自检 +
  AfterFiles 全树复扫（`InitializerTreeGate`，与 C36 闸门并列只增）；幂等断言（二次运行
  `Changed=false`）；BOM/字节复制。
- 共处矩阵：C11/C27 产物树输入（手写等价形态）、C36/C23 混排（临时序号/锚点不冲突）、
  数组/匿名对象零误伤。
- CLI 摘要 init 段 + `--list-passes` catalogItems `C36, C24`；项目级 fixture 双跑扩展 +
  ProjectTests + CLI 测试。
- 验收：全量 build + 三测试工程（除 §7 已知基线失败外）全绿 + 高价值 grep 三禁。
- 提交：`feat(preprocessing): C24 initializers M3 residual 全树闸门+共处回归+fixture/CLI 收口（默认关）`

#### M3 落地记录（2026-10-02 回填）

**文件**（全部在允许面内）：
- 增（Core）：`src/CSharpToJava.Core/ExpressionDesugar/Initializers/InitializerTreeGate.cs`
  （`InitializerTreeGateFile/Result` + `CountPairedNodes`/`Reconcile`，镜像 C36 `CollectionExprTreeGate`）；
- 改（Core `Initializers/`）：`InitializerStatistics.cs`（`PreservedResidualNodes`/`DeferredResidualNodes`/
  `PairedResidualNodes` + `MergeFrom`）、`InitializerSyntaxFacts.cs`（`CountResidualNodes`；
  `IsExplained` 兼认 `ExplainedSubtreeAnnotation`）、`InitializerBindingAnalyzer.cs`（配对计数 +
  `IsLoweredOutcome`）、`InitializersRule.cs`（`CheckResiduals` 落地）、`InitializerDiagnostics.cs`
  （新增 `CS2J0546` Info + `TreeResidualMessage`/`InventorySummaryMessage`）;
- 改（CLI）：`ExpressionDesugarPassAdapter.cs`（摘要 `init:` 段 + 只增的 C24 AfterFiles 全树闸门
  `RunInitializerGate` + verbose init 行；C36 闸门/计数/摘要段零改动）；
- 改（Tests）：`InitializerDesugarUnitTests.cs` 新增 M3 行为 8 个 Fact；
- 增（ProjectTests）：`InitializerDesugarProjectTests.cs`（2 Fact，Core 直调双跑）；
- 增（fixture）：`SampleExpressionDesugarLib/{InitializersDto,InitializersCollections,InitializersNested,
  InitializersTargetTyped}.cs` + `SampleExpressionDesugarTests/InitializerSemanticsTests.cs`（4 Fact）。
- 未改：`CollectionExpr/*`、`Shared/*`、`PassCatalog.cs`、`PassChainFactory.cs`、`Program.cs`、
  `Transformers/**`、既有 fixture 文件、其它测试文件（**C36 测试零改动**）。

**residual 闸门设计（IN-13）**：
- **per-file（`InitializersRulePlan.CheckResiduals`）**：`Options.Initializers is null` → 直接返回 0
  （规则惰性，且避免误伤 C36 默认链产出的 `new List<T>(){…}`）；文件含任一 `DeferredForCoexistence`
  站点 → 返回 0（整文件让位，C24 未改写，剩余节点只能是「让位站点」或「C36 发射的子节点」，均由
  让位计数/C36 自身 CS2J0589 配对）；否则统计三类 residual 中**未带 explained 注解**的节点数
  （数组初始化器/匿名对象由 `IsResidualNode` 直接排除），>0 → CS2J0545（strict Error / 非 strict Warning），
  写入 `ResidualInitializerCount`。
- **配对口径（计数式，供 AfterFiles 复扫）**：Analyze 时对每个**非嵌套顶层站点**按结果记账——
  被保留（`SkippedDiagnostic`/`UnsupportedContext`/空枚举集合形态）的站点把其子树 residual 节点数记入
  `PreservedResidualNodes`；整文件让位站点记入 `DeferredResidualNodes`；被 lower 的站点
  （`ObjectSequence`/`CollectionSequence`/`TargetTypedNewExplicit`/`ZeroTempExplicit`）不产初始化器语法，
  贡献 0。`PairedResidualNodes = Preserved + Deferred`。`IsExplained` 走 explained 注解（含 subtree 变体），
  故 M2 形态/诊断跳过站点天然配对；**只含让位站点的文件不报 0545**。
- **AfterFiles 全树闸门（CLI，只增）**：仅当 `Options.Initializers` 存在时运行（CLI 工厂当前不构造该子节，
  故默认链完全惰性、C36 行为/计数不变）；逐中间 `.cs` 重解析、按三类节点计数，与每文件
  `PairedResidualNodes` 对账，失衡 → CS2J0545 Error 使链失败，平衡 + verbose → CS2J0546 Info。
- **幂等/BOM**：M3 单测锁定 dirty 文件二次运行 `Changed=false` 字节稳定、含 BOM 干净文件字节保真、
  含 BOM dirty 文件二次运行字节稳定；`M2_*` 既有幂等事实保持。

**测试与验证（编排侧复核，2026-10-02）**：`dotnet build -c Release CSharpToJavaConverter.slnx` = **0 错误**；
C24+C36+注册表定向（`InitializerDesugarUnitTests`+`CollectionExprDesugarUnitTests`+`PassChainOrderTests`）= **144/144 通过**；
C24 项目级 `InitializerDesugarProjectTests` = **2/2 通过**。
`dotnet test tests/CSharpToJava.Tests`（含重编译）= **2592 通过 / 0 断言失败**，但运行末尾被
**既有环境缺陷**中止：`ConvertProjectGotoIntegrationTests`（`ConvertProjectGotoIntegrationTests.cs:249` 故意传入畸形
Maven 坐标 `org.example::1.0.0`）触发**测试宿主进程崩溃**（`Unsupported Maven coordinate ... Expected non-empty groupId`），
该类隔离复跑可确定性复现（1 通过后宿主中止）——**与 C24 无关**（C24 不涉及 Maven/依赖解析），子代理另一轮曾完整跑完 2593/0，
属环境/宿主不稳。§7 的 5 个基线失败本轮未复现。

**测试与验证（子代理记录）**：`dotnet build -c Release CSharpToJavaConverter.slnx` = **0 错误**；
`dotnet test -c Release tests/CSharpToJava.Tests`（含测试工程重编译）= **2593 通过 / 0 失败**
（§7 列出的 5 个基线失败未复现）；C24 单测 **57** 个全绿（49 + 8）。
ProjectTests 全量 = **959 通过 / 7 失败 / 10 跳过（976）**：其中 3 例 `LexicalNormalizeProjectTests`
为**已知 CRLF/LF 环境失败**（隔离复跑稳定复现 `Expected "line1\nline2" / Actual "line1\r\nline2"`），
另 4 例（`PatternDesugarProjectTests`、`PartialMergeProjectTests`、`FinalizerProjectTests`、
`ForeachSourceC40ProjectTests` 各一并发/临时目录资源争用的 e2e）**隔离复跑全部通过**；7 例均与 C24 无关
（C24 CLI 路径未启用，适配器改动在无 `Initializers` 子节时完全惰性）。fixture 原生 `dotnet test`
（SampleExpressionDesugarSolution）= **10 通过 / 0 失败**；C24 ProjectTests 2/2 通过（原生 vs 预处理
行为逐项相等、预处理产物 residual 为零、二次幂等）。grep 三禁复核：`Initializers/` 无 `List.of`、
无按语法种类 `OfType` 静默过滤初始化器项（既有 `OfType` 仅用于深度计数与统计分桶）、产物无 `System.Linq`。

**偏差与阻塞**：
1. **residual 计数按节点、摘要按站点**：`CoexistenceDeferredSites` 保持站点计数（M1/M2 事实依赖），
   新增 `DeferredResidualNodes`/`PreservedResidualNodes` 为**节点计数**供全树对账（一个保留站点可含
   多个嵌套 residual 节点）。
2. **per-file 闸门对「C36 发射集合初始化器语法」的依赖**：非让位文件不会出现 C36 发射（有 C36 语法即整文件
   让位），故 per-file 只需 explained + 让位计数即闭合；AfterFiles 闸门在「C36 发射 collection-initializer
   且文件同时有 C24 让位站点」这一组合下依赖让位计数已覆盖，未新增 C36 侧标记（见缺口 b）。
3. **测试口径修正**：`M3_C11ProductTreeInput_*` 以「普通 `set_X` + 公开字段」手写模拟 C11 产物树，
   覆盖 `F = 1` 字段写正常发射（无 CS2J0531）；`M3_Coexistence_*` 显式断言 C36 当轮改写、C24 留位、
   无 0545、二轮收敛、三轮字节稳定。

#### 已知缺口/后续（M3 收口后仍开放）

1. **CLI 启用路径未接线**：`Initializers` 子节仅由 Core 直调可达；CLI 工厂
   （`Program.cs` 两处 `ExpressionDesugarOptionsFactory`）尚未构造该子节，故默认链上 C24 仍惰性。
   本里程碑按约束**未新增 CLI 开关/pass**；M4 默认开前须补一条灰度 options 路径（沿用既有 options/配置面）
   并在 `--list-passes`/摘要中体现，届时 `RunInitializerGate` 才会在 CLI 生效。
2. **族级 C36 plan 取键修复（退役整文件让位）**：C36 仍按原始 `SyntaxNode` 引用取 plan key，
   先行规则一旦改写即整树红节点皆新实例（M2 §6.7 实证），故 C24 对「同文件含集合表达式」整文件让位。
   建议族评审在 `CollectionExpr/*` 改按稳定注解/`(Span, SyntaxTree)` 取键，或族入口对每条规则
   在改写前于当前树重跑 Analyze；二者均在本 worktree 允许面外，**未实施**。
3. **M4（sdk 画像/默认开/构造函数迁移）不在本次交付面**：CS2J0531/0537/0538 的 sdk 命中率 triage、
   字段/属性初始化器位置的构造函数迁移、Java 半区初始化器特判下掉 PR 均登记为后续证据/消费 PR。

## 4. 跨阶段硬纪律

1. 判定一律走全编译 SemanticModel/IOperation（Add 符号、init 标记、索引器 set、target-typed
   目标类型）；**禁止按文本猜 `add`/`put`**（设计硬不变式 #4）。
2. 求值顺序：构造实参先于初始化器；成员/Add/索引赋值严格源码序；任何子表达式单次出现；
   条件位置（三元/`??`/lambda）不外提（S6 两族回归的正面收口）。
3. 禁止静默：无法等价显式化即显式诊断 + 整文件原子不改写；禁止注释/占位/半成品透传。
4. 产物是普通合法 C#：每轮 `AdhocWorkspace` 格式化；干净文件字节级复制（含 BOM）；
   二次运行零 diff。
5. 规则代码不按语法种类 `OfType` 静默过滤；每类初始化器项必须有处置或显式诊断。
6. 不改 `CollectionExpr/*`、`Transformers/**`、E09、C01/C11/C12 已落地代码；共享文件只增不改。
7. 每阶段提交前必须本地跑过 Release build + 相关测试；提交信息带阶段与码段。
8. 临时名统一 `__ed_` 族发号（复用 `ExpressionDesugarTempNameAllocator`），禁止自造前缀。

## 5. 验证命令

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

## 6. M0 探针记录（2026-10-02 回填）

环境：.NET 10 / `Microsoft.CodeAnalysis.CSharp` **5.0.0**（`CSharpToJava.Core.csproj`）；
探针固定在 `tests/CSharpToJava.Tests/InitializerDesugarUnitTests.cs`（7 个探针 Fact + 5 个 M0
行为 Fact，共 12 个，全绿）。设计正文按 4.12 记录的 API 外形有一处**实质偏差**（见 6.2），
以本节实测为准。

### 6.1 对象初始化器成员项分类（`IObjectCreationOperation.Initializer`）

- `ObjectCreationOperation.Initializer` 的运行时具体类型是
  **`Microsoft.CodeAnalysis.Operations.ObjectOrCollectionInitializerOperation`**
  （`Kind=ObjectOrCollectionInitializer`，实现 `IObjectOrCollectionInitializerOperation`）；
  对象/集合/字典索引初始化器**共用同一类型**。成员项集合是 **`IEnumerable<IOperation>`**
  （5.0.0 实测；用 `foreach`/`Count()`，**不是** `ImmutableArray` 的 `.Length`）。
- 逐项外形（`new Dto { Prop = 1, Field = 2, Inner = { Prop = 3 }, Items = { 4, 5 } }`）：

  | 源码项 | Item 具体类型（FullName） | 关键成员 |
  | --- | --- | --- |
  | 属性写 `Prop = 1` | `SimpleAssignmentOperation`（`ISimpleAssignmentOperation`） | `.Target` = `PropertyReferenceOperation`（`IPropertyReferenceOperation`），`.Property.Name="Prop"`、`.Property.SetMethod.Name="set_Prop"`、`IsInitOnly=false`、`.Arguments` 空 |
  | 字段写 `Field = 2` | `SimpleAssignmentOperation` | `.Target` = `FieldReferenceOperation`（`IFieldReferenceOperation`），`.Field.Name="Field"`、`.Field.IsReadOnly=false` |
  | 嵌套成员初始化 `Inner = { … }` | **`MemberInitializerOperation`**（`IMemberInitializerOperation`） | `.InitializedMember` = `PropertyReferenceOperation`；`.Initializer.Initializers` = 内层项 |
  | 集合成员初始化 `Items = { 4, 5 }` | `MemberInitializerOperation` | `.Initializer.Initializers` 为 2 个 Add 项（见 6.2） |
  | 嵌套创建 RHS `P = new I { … }` | `SimpleAssignmentOperation`，`.Value` = `ObjectCreationOperation`（自带 `.Initializer`） | — |

- **字典索引初始化器** `new D { [k] = v }`：item 也是 `SimpleAssignmentOperation`，`.Target`
  = `PropertyReferenceOperation`，`Property.IsIndexer=true`、`Property.Name="this[]"`、
  `SetMethod.Name="set_Item"`、`.Arguments` 含键实参（多维同形）。**不存在**
  `ImplicitElementAccessOperation` 类型的成员项；消费端按 `IPropertyReferenceOperation.IsIndexer`
  识别（设计 §4 决策 9 的"ImplicitElementAccessOperation 或实际类型"以实测收口为属性引用）。

### 6.2 Add 绑定外形（重大 API 变化：`ICollectionElementInitializerOperation` 已废弃）

- **`ICollectionElementInitializerOperation` 在 Roslyn 5.0.0 已标记 `[Obsolete]`（使用即
  CS0619 编译错误）**，替代物为 **`IInvocationOperation` / `IDynamicInvocationOperation`**。
  设计 §2 决策 8/§4 表格中"`AddMethod`/`Arguments`"的引用对象**改为
  `IInvocationOperation.TargetMethod` / `.Arguments`**（`IArgumentOperation`）。
- 集合初始化器元素项实测（含顶层集合初始化器与成员集合初始化器，形状一致）：

  | 形态 | Item 具体类型 | `TargetMethod` 观测 |
  | --- | --- | --- |
  | 单参 `new List<int> { 1, 2 }` | `InvocationOperation`（`Kind=Invocation`） | `List<int>.Add(int)`，`ReturnsVoid=true`，`ReducedFrom=null`，`Arguments.Length=1`，`ArgumentKind=Explicit` |
  | 多参 `new Pairs { { 1, 2 } }` | `InvocationOperation` | `Pairs.Add(int, int)`，`Arguments.Length=2`（**不**发射 `put`） |
  | 扩展 Add（泛型集合 + 扩展方法） | `InvocationOperation` | `GenExt.Add(GenBag, int)`，`IsExtensionMethod=true`、**`ReducedFrom=null`**（**非** reduced 形式），`Arguments[0].Value.Type=GenBag` 为接收器；重发 reduced 语法 `tmp.Add(x)` 即可复现绑定 |
  | fluent（非 void 返回） | `InvocationOperation` | `FluentBag.Add(int)`，`ReturnsVoid=false`；表达式语句丢弃返回值 |
  | params Add `Add(params int[])` 配 `{1,2,3}` | **3 个** `InvocationOperation`（每个源码元素一次调用） | 每次 `Arguments.Length=1`，`Parameter.IsParams=true`、`ArgumentKind=ParamArray`、实参值类型 `int[]`（**展开为单元素数组调用**，非一次 `Add(a,b,c)`）——设计 §2 决策 8⑦ 的"展开形式"以实测为准 |
  | IEnumerable 无 Add `new NoAdd { 1 }` | **`InvalidOperation`**（`IInvalidOperation`） | 基线报错 **CS1929**（"不包含 Add 的定义"，CS1922/CS0117 族）；M2 映射 CS2J0533 |

  （注：扩展 Add 的最小合法夹具需集合实现 `IEnumerable<T>` 的空洞实现；非泛型空 `IEnumerable`
  夹具会因无绑定 Add 报 CS1950，不构成扩展 Add 探针。）

### 6.3 init / readonly 基线

- init-only 属性：初始化器内合法；`IPropertyReferenceOperation.Property.SetMethod.IsInitOnly
  == true`（`ISimpleAssignmentOperation.Target`）。C11 放宽后输入树为普通 `set`（`IsInitOnly=false`）
  + 字段，探针以对照输入确认。
- readonly 字段 / get-only 属性出现在初始化器：基线 **CS0191**（只读字段）/**CS0200**（只读属性），
  且该初始化器项的左侧 `GetSymbolInfo(...).Symbol == null`（绑定失败）——M1 无法依赖符号做
  防御性 CS2J0531 分类，只能按语法 + 基线错误位置归因。

### 6.4 target-typed `new()` 的 ConvertedType

- 全部上下文（字段初始化器 / 属性初始化器 / `: this(...)` 实参 / 局部声明 / `return` /
  实参位 / 三元双支 / 可空 `return`）实测 `GetTypeInfo(node).ConvertedType` 均为
  `System.Collections.Generic.List<int>`（`NullableAnnotation=NotAnnotated`，可空目标亦取底层 T，
  **无** `List<int>?` 注解串）；`GetOperation` 统一为 `ObjectCreationOperation`。
- 因此决策 10 的"可空目标剥注解"在 ConvertedType 层已天然成立（UnderlyingType == ConvertedType）。

### 6.5 dirty 门 / residual 谓词

- trigger：`ObjectCreationExpressionSyntax{Initializer:not null}` 或
  `ImplicitObjectCreationExpressionSyntax`（裸 `new()` 与带初始化器均算）。
- **零误伤**：`new int[]{…}`、`new[]{…}`（`ArrayInitializerExpression`）、裸 `new C()`、
  匿名对象 `new { A = 1 }` 均不触发。
- residual 三类节点：`ObjectInitializerExpression`、`CollectionInitializerExpression`
  （含 `ComplexElementInitializerExpression` 元素）、`ImplicitObjectCreationExpressionSyntax`；
  `ArrayInitializerExpression` 排除；配对用共享 `ExpressionDesugarSyntaxFacts.ExplainedAnnotation`
  （祖先链查询）。
- 模板：`new Dto { A = 1 }` 源码的 trigger 计数=4（a/b/`new(){…}`/`new()`）；
  `new { A = 1 }` 若计入会与匿名对象冲突，实测排除。

### 6.6 M0 行为锁定（同测试文件）

clean 文件 `Changed=false` + `FilesSkippedClean=1` 且字节不变；仅含初始化器（无 C36 语法）的
dirty 文件 `Changed=false` + `FilesDirtyNoRewrite=1`、输出原样、**零诊断**（Initializers M0 不产
任何码；CheckResiduals 恒 0）；初始化器与 C36 混排文件 C36 站点照常改写（`new int[]{…}`、计数不变）
且二次运行对 C24 站点字节稳定；`SemanticModel=null` 的初始化器 dirty 文件不崩、`FilesDirtyNoRewrite=1`。

### 6.7 跨规则节点身份实测（M2 新增，2026-10-02）

M1 记录 §3 偏差 1 的「Roslyn 复用未变子树实例（兄弟/无关站点不受影响）」**不成立**，实测（临时探针，
已删除）结论：
- 同一个 `CSharpSyntaxRewriter` 在**无任何改动**时返回同一 root 实例（`Update` 全链短路；仅覆写
  `Visit` 的对照改写器保持引用相等）；
- 一旦发生**任一处**改写（`VisitXxx` 返回新节点，或 `SyntaxNode.ReplaceNode`），
  再遍历结果树时**整棵树**（含完全无关的兄弟类、跨方法、跨顶层类型）都得到**新的红节点实例**
  （`ReferenceEquals` 全为 false，文本相等）；
- 结论：以原始 `SyntaxNode` 引用为 key 的跨规则 plan（C36 现状）在**任何先行之规则改写后必然 miss**；
  C24 的「仅推迟 `CollectionExpressionSyntax` 内的站点」只能覆盖「本文件 C24 完全无改写」的情形。

## 7. 已知基线失败（开工前既有，与 C24 无关）

- Tests 工程 5 个：SystemPrivateXmlRuntimeResource ×1、CSharpXmlCompileRegression ×2、
  EnumDeclaration UnsignedNumericToken ×2；
- ProjectTests 1 个：EqualityConcat e2e（缺 compat jar）；
- ConversionTests 0。

（依据 C36 计划 §7.4 终验记录；每阶段提交前以实际失败清单复核，只增不掩盖。）

**M0 实际复跑（2026-10-02，Release）**：`dotnet build -c Release CSharpToJavaConverter.slnx`
= **0 错误**；`dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
= **2548 通过 / 0 失败**（§7 列出的 5 个基线失败在本基线未复现，未新增任何失败）；
`CollectionExprProjectTests` 4/4 通过、`ListPasses_IncludesExpressionDesugar_WithC36CatalogItem`
（断言已更新为 `rules=C36, C24`）通过。
ProjectTests 全量复跑：959 通过 / 5 失败，均为**与 C24 无关的既有/环境失败**——
3 例 `LexicalNormalizeProjectTests` 系 `SampleLexicalNormalizeLiteralTests` 的 CRLF/LF
行尾断言（`Expected "line1\nline2" / Actual "line1\r\nline2"`，Windows checkout 环境面，隔离复跑
稳定复现），2 例（StripDefault `Rerun_DoesNotLeaveOldArtifacts`、ForeachSource
`Fixture_WrapsStringSites`）在隔离复跑通过（全量并发下的构建/临时目录资源争用）。C24 不触碰
lexical-normalize/foreach-source/strip-default 任一 pass。
