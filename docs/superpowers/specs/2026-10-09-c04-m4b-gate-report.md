# C04 iterator-lowering M4b：§8 默认开启五门槛评审结论

- 日期：2026-10-09
- 分支：`dev-3.x-26100507`
- 基线提交：M4a `fd39e442`
- 设计依据：[2026-09-28-c04-iterator-lowering-design.md](2026-09-28-c04-iterator-lowering-design.md) §8（默认开启验收门槛）、§9 M4 出口（「§8 五项结论成文；默认关→开或继续 opt-in 的决策记录」）

## 0. 结论

**iterator-lowering 继续 default-off（保持 opt-in：`--enable-pass iterator-lowering`）。**

§8 五门槛中：门槛 1 达成；门槛 2 达成（以本轮 Release 证据为准）；门槛 3、4、5 未达成。Descriptor 维持 `defaultEnabled: false`（[PassCatalog.cs](../../../src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)），能力已 Implemented、链位已锁（closure-lift → iterator-lowering → async-normalize → extension-methods → eliminate-goto）。

## 1. M4b 本轮代码变更（正确性补齐）

M4a 后的 sdk 真实语料画像与裸 return 语义评审发现 4 个问题，本轮全部修复：

1. **裸 `return;` 归一为 `yield break`**：迭代器块内的无值 return 是合法 C#（语义等同 yield break，含 finally 清理），旧 CS2J0402 闸门误拒。新增 `BareReturnToYieldBreakRewriter`，在模型绑定的资源归一之后、分析器之前做纯语法归一；`return <expr>;` 本就是 CS1622，不进入预处理。
2. **限定成员访问右名误改写（sdk 画像真实崩溃）**：状态机 TokenRewriter 旧逻辑把 `x.Foo` 的右名 `Foo`（与裸 `Foo` 解析到同一拥有者实例成员）也改写成 `_cs2jThis.Foo`，既改目标又破坏默认成员访问 visitor 的 `SimpleNameSyntax` 形状（`Unable to cast MemberAccessExpressionSyntax to SimpleNameSyntax`）。新增 `IsQualifiedMemberName` 守卫（`a.Foo` / `a?.Foo`），VisitIdentifierName/VisitGenericName 同时补 `nameof` 守卫。
3. **嵌套例程 yield 误归属**：方法体内嵌迭代器局部函数时，旧检测（`Body.DescendantNodes().Any(yield)`）把外层普通方法误判为迭代器——分析器误发 CS2J0401、归一器会把外层裸 return 误改为 yield break（CS1631）。抽出共享判定 `IteratorLoweringAnalyzer.MethodOwnsYield`（剪枝局部函数/λ/匿名方法），分析器、资源归一器、裸 return 归一器三处统一复用。
4. **同文件构造函数/事件访问器裸 return 误归一**：裸 return 重写器默认遍历会经过构造函数、事件 add/remove 访问器，文件一旦含 yield（脏检查通过）就会把其中裸 return 错改成 yield break。增加 `_inIteratorMethod` 遍历上下文闸门，非迭代器方法体不动。

测试：`IteratorLoweringUnitTests` 新增 5 个用例（for 内裸 return 结束枚举、try/finally 内裸 return 清理链恰好一次、限定访问右名不改写、外层非迭代器方法含迭代器局部函数、同文件 ctor/事件访问器裸 return 保留），并移除 1 条过时的「裸 return 必须拒绝」理论用例行。全类 94/94 绿。

## 2. §8 五门槛逐项结论

### 门槛 1 — 原型出口（M1+M2）：✅ 达成

- try/finally（含嵌套）、using（块/声明/多资源/pattern-based）、foreach 跨挂起、提前 Dispose 时序在专项 fixture（SampleIteratorSolution）.NET 半区全绿（M4a 3/3 项目级）。
- 生成的 goto/转发 switch 被 eliminate-goto 全量吃下；M4a 修复了「有 default 且每节以控制转移终结的内层 switch 不可正常完成、外层追加 `break;` 被 javac 判不可达」（StatementTransformer.SwitchAndResource.cs `StatementAlwaysTerminatesSwitchSection` 嵌套 switch 分支）。
- Java 半区：`IteratorJavaE2EProjectTests` 经真实 javac（JDK 28-ea）+ JVM 执行 + 27 行行为 oracle 对拍通过（M4a 1/1，3m27s；M4b 回归见 §3）。

### 门槛 2 — 全仓 Release build/test 三工程全绿；启用范围内 residual=0：✅ 达成（本轮证据）

- `dotnet build CSharpToJavaConverter.slnx -c Release`：**0 错误**（105 既有警告，本轮代码变更后重跑）。
- 三个测试工程 Release：CSharpToJava.Tests 3479/0；CSharpToJava.ProjectTests 1917 通过（唯一失败为并发执行造成的文件锁，单跑复绿，§3）；CSharpToJava.ConversionTests 674 通过/6 失败——6 个失败在基线 fd39e442 上逐一对齐、均为既有红测（§3 注），**本轮零新增失败**。
- 启用范围内 residual：单文件预处理有严格 residual 闸门（残留 yield 即 CS2J0402 Error 且无部分改写）；fixture 项目级用例 residual=0；本轮 ProjectTests Debug 全量 1918/0、Release 无代码相关失败。

### 门槛 3 — csharp-sdk 四子集基线：❌ 未达成

以 MSBuildWorkspace 直连探针（加载 `D:\csharp-sdk\csharp-sdk.sln` 全部 20 工程 1387 文档，对每个 SyntaxTree 单独跑 `IteratorLoweringPreprocessor`，按路径前缀分桶，单文档 try/catch 容错）画像，三轮（M4b 修复前/剪枝修复后/最终代码）数字逐轮稳定：

| 子集 | 文档 | yield 文件 | yield return | yield break | 降级成员 | 状态机 | residual | crashes | 改变文件 |
|---|---|---|---|---|---|---|---|---|---|
| json | 341 | 17 | 679 | 4 | 39 | 39 | 12 | 0 | 12 |
| regular-expressions | 83 | 10 | 2028 | 0 | 18 | 18 | 230 | 0 | 9 |
| uri | 64 | 3 | 59 | 0 | 2 | 2 | 0 | 0 | 1 |
| xml-tiny | 887 | 7 | 117 | 0 | 7 | 7 | 0 | 0 | 6 |
| **合计** | 1387 | 37 | **2883** | **4** | **66** | 66 | **242** | **0** | 28 |

- **2887 个 yield 站点与 catalog 基线逐字吻合**；画像由「有崩溃」收敛到 **0 crashes**（门槛外的真实崩溃已在本轮修复，见 §1-2）。
- **被拒成员共 6 个、涉及 244 个 yield 站点，全部有显式、可分类的诊断，无「静默漏网」**：
  - **CS2J0404 ×2 成员 / 217 站点**：迭代器体内残留局部函数（regex `Match_Basic_TestData` 214、json `TypesForInvalidJsonForCollectionTests` 3）——依赖 closure-lift 后续能力。
  - **CS2J0405 ×2 成员 / 18 站点**：体内残留 λ/匿名方法（regex `Replace_MatchEvaluator_TestData` 16、xml-tiny `ctestcase.TestCases` 2）——跨 yield 捕获未验证，随 closure-lift 推进。
  - **属性/索引器 get 访问器迭代器 ×2 文件 / 9 站点**（json `JsonDocumentTests` 5、`CollectionTests.Generic.Read` 4）：分析器当前只盘点 `MethodDeclarationSyntax`，无成员级诊断；严格 residual 闸门仍以 CS2J0402 可见并阻断部分改写。
- 口径说明：表中 residual=242 是重写后 residual 闸门计数；`ctestcase.cs` 是该文件唯一迭代器成员且被 CS2J0405 拒绝，文件 plans=0 时流水线在 residual 计数之前早退，故其 2 站点不进 residual 桶、只体现在 CS2J0405 诊断上。实际未降级站点 = 242 + 2 = **244**。补齐访问器 lowering 是明确挂账项。
- **门槛 3 未达成的直接原因**：`convert-project --enable-pass iterator-lowering` 全链在到达本 pass 之前就被**快照中与 C04 无关的 5 处既有破损**截断，无法取得「中间树 build/test 不退化」证据：
  1. json TrimmingTests 散件 `ICollection.cs` 含非法 `typeof(ICollection>)`（csproj `EnableDefaultCompileItems=false` 不编译它，目录发现吞入）；
  2. uri enum-declaration 残留 11 处 bitwise（verify build CS0266/CS0019）；
  3. `ArrayBuilder.cs` 1 处 null-coalesce residual；
  4. regex「IfStatementSyntax→ExpressionSyntax」转换崩溃；
  5. uri 后续「Syntax node is not within syntax tree」。
- 处置：5 处破损单列挂账（不属于 C04），修复后须以全链（含 verify build/test）重跑本画像，门槛 3 才能复核。

### 门槛 4 — EF Core 画像：❌ 无法评估

catalog §5.1 前置行动（EF Core 训练快照拉取）在本机不可用，无法取得 λ 迭代器、泛型嵌套、lock 等形态的命中率/skip 率。既有 sdk 画像中 CS2J0405 已占 2 个拒绝成员（16 站点），按门槛原文「若 CS2J0405 占比高则默认开启推迟到 closure-lift 之后」，即使 EF Core 画像缺位，维持 opt-in 也是与该条款一致的保守决策。

### 门槛 5 — opt-in 观察期跨一次主版本回归窗口：❌ 未达成

Iterator-lowering 在 M4a（同一开发周期）才由 Stub 转 Implemented，尚未跨过任何一次主版本回归窗口；且 M4b 就在 sdk 画像中发现并修复了真实崩溃（§1-2）——这正说明观察期的必要性。维持 opt-in 至少至下一次主版本回归窗口。

## 3. 本轮验证证据汇总

| 项 | 命令/范围 | 结果 |
|---|---|---|
| 迭代器单测 | `dotnet test --filter IteratorLoweringUnitTests` | 94/94 通过 |
| Core 全量（Debug） | CSharpToJava.Tests | 3479 通过 / 0 失败（3 跳过，与基线一致） |
| Core 全量（Release） | CSharpToJava.Tests -c Release | 3479 通过 / 0 失败 |
| ProjectTests 全量（Debug） | CSharpToJava.ProjectTests | 1918 通过 / 0 失败 / 25 跳过 |
| ProjectTests 全量（Release） | CSharpToJava.ProjectTests -c Release（36m48s） | 1917 通过 / 1 失败 / 25 跳过；唯一失败为并发跑 ConversionTests 导致的 `CrossFileDynamic.cs` 文件锁 IOException，单跑该类 5/5 绿，非代码缺陷 |
| ConversionTests（Release） | CSharpToJava.ConversionTests -c Release | 674 通过 / 6 失败，**6 个均为基线既有红测**（见下注） |
| Java 慢门 | JAVA_HOME=jdk-28-ea、CS2J_RUN_SLOW_E2E=1，IteratorJavaE2E | 1/1 通过（3m35s，真实 javac+JVM，oracle 全对拍） |
| Release build | `dotnet build CSharpToJavaConverter.slnx -c Release` | 0 错误 |
| sdk 画像 | MSBuildWorkspace 探针，20 工程 1387 文档 | 0 crashes，三轮数字稳定，242 residual + 2 早退口径 = 244 未降级站点全分类 |

> **ConversionTests 6 个既有红测（与 C04 无关）**：4 个 AsyncStreamsConversionTests（期望 `.moveNextAsync().join()`/`disposeAsync().join()`，实际发 `AsyncAwait.join(...)`，属 C38/C18 async-streams 形态漂移）在基线 fd39e442 上实证同样失败；另 2 个（TopLevelStatements 期望无参 `main()`、duck enumerator Dispose 形态）Release 单跑即红、Debug 单跑即绿，属既有 Release/Debug 行为差。本轮生产改动全部位于 `IteratorLowering` 命名空间（+ PassCatalog 一行注释），这些测试不启用 iterator-lowering、代码路径不可达。

## 4. 挂账项（默认开启的前置条件）

1. 属性/索引器 get 访问器迭代器 lowering（分析器成员盘点 + 状态机发射）。
2. CS2J0404/CS2J0405 形态随 closure-lift 能力推进后复核（235 站点）。
3. csharp-sdk 快照 5 处与 C04 无关的前置 pass 破损修复后，全链重跑画像，补门槛 3 的「build/test 不退化」证据。
4. EF Core 快照（catalog §5.1 前置行动）到位后补门槛 4。
5. 跨过一次主版本回归观察窗口（门槛 5）。

以上 1-5 全部闭环后，才把 descriptor `defaultEnabled` 翻 true；翻转时同步更新本报告与 PassCatalog 注释。
