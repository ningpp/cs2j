# C46 成员签名显式化 Implementation Plan

> **For agentic workers:** 每个里程碑一个独立提交（实现一部分、提交一部分）；所有改动在工作目录（worktree）内完成。步骤用 checkbox（`- [ ]`）跟踪。

**Goal:** 在 `member-desugar` 族新增 **Signatures 规则集**（SI/NH/CR）：显式接口实现（方法/属性/索引器/事件）、`new` 隐藏成员、C# 9 协变返回，改写为不依赖 C# 签名/分派暗语义的显式可编译 C#（设计 [2026-09-29-c46-member-signature-design.md](../specs/2026-09-29-c46-member-signature-design.md)）。

**Architecture:** C11 锚点骨架上的追加式落地：`[MemberDesugarRule(MemberDesugarKind.Signature, 40-69)]` 经程序集发现自动装配，不新增 pass/adapter/verb/开关。共享文件仅追加式修改：诊断码 5190-5199、族统计计数器、`MemberDesugarPreprocessor.RewriteFile` 签名分相、summary 追加 signatures 段、`rules.signatures` 选项子节解析（同 C39 `rules.finalizers`）。全部新代码在 `src/CSharpToJava.Core/MemberDesugar/Signatures/` 与自有测试/fixture 内；不触转换器代码、不做注册表翻转。

**Tech Stack:** .NET SDK（`CSharpToJavaConverter.slnx`）、Roslyn（`Microsoft.CodeAnalysis.CSharp` `CSharpSyntaxRewriter`）、xUnit。全部命令 `-c Release`。

**Working directory（每条命令）:** `D:\code\cs2j\.worktrees\preproc-c46-member-signature`（分支 `feature/preproc-c46-member-signature`）

**Gate（2026-10-02 设计口径更新，绑定）:** 族 pass 已 Implemented 且 defaultEnabled=true；Signatures 子集由 `SignatureOptions.Enabled`（默认 **false**）闸门承接「不翻转期零执行零 diff」——默认关时分析器/规则完全不执行；显式启用后 M1 仅审计、M2+ 才改写。§8 默认开门槛完成前不翻默认值。

**提交策略:** M1→M2→M3→M4 每里程碑 build/test 全绿后各自提交（实现一部分、提交一部分）；提交信息 `feat(preprocessing): C46 M<n> …`。

---

## M1 审计骨架（commit 1）

**产物**：Signatures 四索引 + 只读审计 + 选项闸门；**零改写**（Enabled=false 零痕迹；Enabled=true 出计数与 CS2J5190 Info）。

**Files:**
- Modify: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs`（枚举 5190-5199 + 统计计数器 + `Signatures` 选项属性）
- Modify: `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs`（审计计数并入，镜像 `MetadataOperatorSitesByFile` 模式）
- Modify: `src/CSharpToJava.CLI/Program.cs`（`rules.signatures` 子节解析）
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`（catalogItems 一行追加 C46）
- Create: `src/CSharpToJava.Core/MemberDesugar/Signatures/SignatureModels.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Signatures/SignatureDiagnostics.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Signatures/SignatureSyntaxFacts.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Signatures/SignatureExplicitizationAnalyzer.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Signatures/ExplicitInterface/InterfaceMappingIndex.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Signatures/Hiding/HideGroupResolver.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Signatures/Covariant/CovariantChainAnalyzer.cs`
- Create: `src/CSharpToJava.Core/MemberDesugar/Signatures/SignatureAuditRule.cs`
- Create: `tests/CSharpToJava.ProjectTests/MemberDesugarSignatureUnitTests.cs`

- [ ] **Task 1: 共享模型追加**
  - [ ] 1.1 `MemberDesugarDiagnosticCode` 追加 5190-5199（设计 §6：PlanCreated/DivergentInterfaceBodies/NameReservationConflict/ReflectionContract/ExpressionTreeOrDynamic/ValueTypeCovariant/GenericBridgeInfeasible/SlotAmbiguous/StaticAbstractInfo/Residual）
  - [ ] 1.2 `MemberDesugarStatistics` 追加 C46 计数器（设计 §5.2 特有计数清单）
  - [ ] 1.3 `MemberDesugarOptions` 追加 `SignatureOptions Signatures { get; init; } = new()`
- [ ] **Task 2: CLI 选项透传**：`Program.cs` `MemberDesugarOverrideSets` 增 `Signatures` 子节（`Enabled/Strict/ReportInfo/SkipSignatures/AllowDivergentInterfaceBodies`），`BuildMemberDesugarOptions` 接线（缺失/非法回退默认；Enabled 默认 false）
- [ ] **Task 3: `SignatureModels.cs`**：`SignatureOptions`（Enabled=false、Strict=true、ReportInfo、SkipSignatures、AllowDivergentInterfaceBodies=false/v1 强制 false）、`SignatureStatistics`、`SignatureKind`、审计载体（按文件分桶，供 RewriteFile 并入族统计）
- [ ] **Task 4: `SignatureDiagnostics.cs`**：CS2J5190-5199 常量与消息模板；Warning 经 `SignatureOptions.Strict`（默认 true）升 Error
- [ ] **Task 5: `SignatureSyntaxFacts.cs`**：调用点五分类（决策 4）、nameof/反射/表达式树/dynamic 判定、体等价比较（决策 2）、强转最小化辅助（M2 消费；M1 建能力）
- [ ] **Task 6: 四索引分析器**：`SignatureExplicitizationAnalyzer`（整树一次，Enabled=false 时完全不执行）+ `InterfaceMappingIndex`（类型→接口→槽→实现符号；重新实现/菱形/泛型构造双轨；候选歧义清单）/`HideGroupResolver`（IsHideByName、`new virtual` 槽链成组、纯 base 委托白名单判定）/`CovariantChainAnalyzer`（override 返回类型链 + Java 映射后子类化/擦除判定；C27 `Cs2jWithClone` 固定 CR-1）
- [ ] **Task 7: 审计规则与统计并入**：`SignatureAuditRule`（Kind=Signature、Order=30；Enabled 时输出三类计数 + CS2J5190 Info，不产 plan）；预处理器把审计计数按文件并入 `MemberDesugarStatistics`（镜像 `MetadataOperatorSitesByFile`）
- [ ] **Task 8: 单测**：`MemberDesugarSignatureUnitTests.cs`——默认关零 diff/零诊断/零计数（含显式实现+隐藏+协变样例）；Enabled 审计计数矩阵（显式实现按四成员种类、隐藏按 new/隐式/newslot/静态、协变 Retained/Bridged/ValueType）；二次运行零 diff
- [ ] **Task 9: 收口**：`PassCatalog.cs` catalogItems `"C11, C12, C13, C14, C39"` → `"C11, C12, C13, C14, C39, C46"`（勿回退 C12–C14/C39）；`dotnet build -c Release CSharpToJavaConverter.slnx`；`dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugar"`；提交

---

## M2 显式接口方法（SI-1/2/3/5 方法面，commit 2）

**产物**：方法形态显式接口实现全链路改写（去限定名/提 public、让位改名、双接口同源合并/报码、五类调用点），residual 方法面。

**Files:**
- Create: `Signatures/ExplicitInterface/SiPlan.cs`、`SiPlanBuilder.cs`、`ExplicitInterfaceDeclarationRewriter.cs`、`ExplicitInterfaceCallSiteRewriter.cs`
- Modify: `Signatures/SignatureExplicitizationAnalyzer.cs`（SI 规则接入：`[MemberDesugarRule(Signature, 40)]`）
- Modify: `MemberDesugarPreprocessor.cs`（签名字段分相：晚于索引器阶段；树变更→格式化+重解析换新鲜模型，同 operator/indexer 阶段形态）
- Modify: `Signatures/SignatureModels.cs`（SI 统计并入）
- Extend: `tests/CSharpToJava.ProjectTests/MemberDesugarSignatureUnitTests.cs`

- [ ] **Task 1: SiPlan/SiPlanBuilder**：「实现符号 × 接口槽」粒度；TouchedFiles = 声明部件 + 全部分类站点文件（含接口声明联动）；确定性排序；不可行项 Infeasible + 码
- [ ] **Task 2: 声明侧重写（SI-1/2/3）**：SI-1 去 `ExplicitInterfaceSpecifier` + 提 public + 规范槽位名；SI-2 类自有同槽成员让位改名 `{Name}_cs2jClass`（预留表防撞，二撞 `_2`）；SI-3 同名双接口体等价合并 / 分歧 CS2J5191 整组跳过（决策 2/3）
- [ ] **Task 3: 调用点重写（SI-5）**：决策 4 五分类（接口静态类型/泛型约束/强转-is-as/绑定让位成员/`base.`）；方法组/委托绑定；强转最小化；表达式树 CS2J5194、dynamic CS2J5194、反射字符串 CS2J5193、`nameof` 字面量化、静态抽象 CS2J5198（决策 13/15）
- [ ] **Task 4: 预处理器分相与 residual（SI-7）**：签名阶段晚于索引器；输出树残余 `ExplicitInterfaceSpecifier` 对账（strict 下 CS2J5199）；幂等谓词防回环
- [ ] **Task 5: 单测（设计 §7.1-1 方法子集）**：无碰撞提升；显式+类自有同名两体各归各位；同名双接口体等价合并/分歧 5191；泛型接口 `IFoo<int>.Bar`；多接口一次实现；五类接收者；方法组；跨文件三文件联动；`AssertOutputCompiles` + 二次执行零 diff
- [ ] **Task 6: 收口**：summary 追加 signatures 段（adapter，追加式）；全量 `--filter MemberDesugar` 绿；提交

---

## M3 属性/索引器/事件显式实现 + 泛型/重实现（SI 全量 + NH-3/4 访问器面，commit 3）

- [ ] T1 消费 C11–C13 产物（`IFoo.get_X`/`get_Item`/`add_Xxx` 方法对）做 SI-1/2/5；索引器/事件方法对联动
- [ ] T2 SI-4 泛型构造映射（决策 5）+ 重新实现槽位重算（决策 6）；候选歧义 CS2J5197
- [ ] T3 NH-3 纯 base 委托白名单桥接（决策 8 + 裁决 5）；NH-4 字段/静态隐藏改名
- [ ] T4 事件后备存储不重复发射（对齐 InterfaceEventListenerNamingTests）；未启用访问器规则时仅登记不产 plan
- [ ] T5 单测（§7.1-2/3 子集）+ fixture 场景文件（SampleMemberDesugarSolution 追加）
- [ ] T6 收口验证 + 提交

---

## M4 new 隐藏全量与协变桥接（NH-2/4、CR-1/2/3，commit 4）

- [ ] T1 NH-2 `new virtual/abstract` newslot 链整组改名（决策 9 矩阵）
- [ ] T2 CR-1 直映审计（决策 10）+ C27 `Cs2jWithClone` 不重复桥
- [ ] T3 CR-2 擦除冲突桥接合成（决策 11）：bridge 基槽 + `M_cs2jCovariant` 改名方法；派生静态类型绑定点改写；sealed 形态
- [ ] T4 CR-3 值类型/不可判 CS2J5195；CS2J5196 保守桥（裁决 4）
- [ ] T5 residual 闸门三面（SI-7/NH-5/CR-4）与回退演练；幂等/BOM
- [ ] T6 fixture 双端分派矩阵（.NET 预言机）+ sdk 画像/§8 门槛评审材料
- [ ] T7 收口验证 + 提交

---

## 验证命令（每里程碑一致）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugar"
```