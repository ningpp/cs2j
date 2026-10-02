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

- [x] **Task 1: 共享模型追加**
  - [x] 1.1 `MemberDesugarDiagnosticCode` 追加 5190-5199（设计 §6：PlanCreated/DivergentInterfaceBodies/NameReservationConflict/ReflectionContract/ExpressionTreeOrDynamic/ValueTypeCovariant/GenericBridgeInfeasible/SlotAmbiguous/StaticAbstractInfo/Residual）
  - [x] 1.2 `MemberDesugarStatistics` 追加 C46 计数器（设计 §5.2 特有计数清单）
  - [x] 1.3 `MemberDesugarOptions` 追加 `SignatureOptions Signatures { get; init; } = new()`
- [x] **Task 2: CLI 选项透传**：`Program.cs` `MemberDesugarOverrideSets` 增 `Signatures` 子节（`Enabled/Strict/ReportInfo/SkipSignatures/AllowDivergentInterfaceBodies`），`BuildMemberDesugarOptions` 接线（缺失/非法回退默认；Enabled 默认 false）
- [x] **Task 3: `SignatureModels.cs`**：`SignatureOptions`（Enabled=false、Strict=true、ReportInfo、SkipSignatures、AllowDivergentInterfaceBodies=false/v1 强制 false）、`SignatureStatistics`、`SignatureKind`、审计载体（按文件分桶，供 RewriteFile 并入族统计）
- [x] **Task 4: `SignatureDiagnostics.cs`**：CS2J5190-5199 常量与消息模板；Warning 经 `SignatureOptions.Strict`（默认 true）升 Error
- [x] **Task 5: `SignatureSyntaxFacts.cs`**：调用点五分类（决策 4）、nameof/反射/表达式树/dynamic 判定、体等价比较（决策 2）、强转最小化辅助（M2 消费；M1 建能力）
- [x] **Task 6: 四索引分析器**：`SignatureExplicitizationAnalyzer`（整树一次，Enabled=false 时完全不执行）+ `InterfaceMappingIndex`（类型→接口→槽→实现符号；重新实现/菱形/泛型构造双轨；候选歧义清单）/`HideGroupResolver`（IsHideByName、`new virtual` 槽链成组、纯 base 委托白名单判定）/`CovariantChainAnalyzer`（override 返回类型链 + Java 映射后子类化/擦除判定；C27 `Cs2jWithClone` 固定 CR-1）
- [x] **Task 7: 审计规则与统计并入**：`SignatureAuditRule`（Kind=Signature、Order=30；Enabled 时输出三类计数 + CS2J5190 Info，不产 plan）；预处理器把审计计数按文件并入 `MemberDesugarStatistics`（镜像 `MetadataOperatorSitesByFile`）
- [x] **Task 8: 单测**：`MemberDesugarSignatureUnitTests.cs`——默认关零 diff/零诊断/零计数（含显式实现+隐藏+协变样例）；Enabled 审计计数矩阵（显式实现按四成员种类、隐藏按 new/隐式/newslot/静态、协变 Retained/Bridged/ValueType）；二次运行零 diff
- [x] **Task 9: 收口**：`PassCatalog.cs` catalogItems `"C11, C12, C13, C14, C39"` → `"C11, C12, C13, C14, C39, C46"`（勿回退 C12–C14/C39）；`dotnet build -c Release CSharpToJavaConverter.slnx`；`dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugar"`；提交

---

## M2 显式接口方法（SI-1/2/3/5 方法面，commit 2）

**产物**：方法形态显式接口实现全链路改写（去限定名/提 public、让位改名、双接口同源合并/报码、五类调用点），residual 方法面。

**Files:**
- Create: `Signatures/ExplicitInterface/SiPlan.cs`、`SiPlanBuilder.cs`、`ExplicitInterfaceRewriter.cs`（声明侧 SI-1/2/3 + 调用点侧 SI-5 合并单遍，替代原拟 `ExplicitInterfaceDeclarationRewriter.cs`+`ExplicitInterfaceCallSiteRewriter.cs` 两文件）、`Signatures/SiRule.cs`（`[MemberDesugarRule(Signature, 40)]`）
- Modify: `Signatures/SignatureSyntaxFacts.cs`（追加 `BoundSymbol`：nameof 方法组实参候选符号兜底）
- Modify: `MemberDesugarPreprocessor.cs`（签名字段分相：晚于索引器阶段；树变更→格式化+重解析换新鲜模型；EmitSkipDiagnostics 签名分支）
- Modify: `MemberDesugarModels.cs`（M2 改写计数器）、`MemberDesugarPassAdapter.cs`（summary 追加 signatures 段）
- Extend: `tests/CSharpToJava.ProjectTests/MemberDesugarSignatureUnitTests.cs`

- [x] **Task 1: SiPlan/SiPlanBuilder**：「实现符号 × 接口槽」粒度（同名多接口体等价合组）；TouchedFiles = 声明部件 + 绑定让位成员的全部调用点文件；确定性排序；不可行项 Infeasible + 码；跨 Compilation `SiteKey` 稳定键
- [x] **Task 2: 声明侧重写（SI-1/2/3）**：SI-1 去 `ExplicitInterfaceSpecifier` + 提 public + 规范槽位名；SI-2 类自有同槽成员让位改名 `{Name}_cs2jClass`（按类型预留表防撞，二撞 `_2`；同槽多候选/无法确定让位者 CS2J5192）；SI-3 同名双接口体等价合并（删除冗余显式声明）/分歧 CS2J5191 整组跳过（决策 2/3）
- [x] **Task 3: 调用点重写（SI-5）**：决策 4 五分类（接口静态类型/泛型约束/强转-is-as/绑定让位成员/`base.`）；方法组/委托绑定改名；表达式树/dynamic CS2J5194 整 plan 原子不动；反射字符串 CS2J5193 告警留站点；`nameof` 字面量化；静态抽象 CS2J5198 Info（决策 13/15）
- [x] **Task 4: 预处理器分相与 residual（SI-7）**：签名阶段晚于索引器；输出树残余无归属 `ExplicitInterfaceSpecifier` 对账（CS2J5199）；幂等谓词防回环；调用点-only 文件不 clean 跳过
- [x] **Task 5: 单测（设计 §7.1-1 方法子集）**：无碰撞提升（含无修饰符）；显式+类自有同名两体各归各位；同名双接口体等价合并/分歧 5191 字节一致；泛型接口 `IFoo<int>.Bar`；多接口一次实现；五类接收者 + 方法组 + `base.`；跨文件三文件联动；表达式树 5194 声明不动；nameof 字面量化 + 反射 5193 站点原样；`AssertOutputCompiles` + 二次执行零 diff
- [x] **Task 6: 收口**：summary 追加 signatures 段（adapter，追加式）；全量 `--filter MemberDesugar` 绿；提交

---

## M3 属性/索引器/事件显式实现 + 泛型/重实现（SI 全量 + NH-3/4 访问器面，commit 3）

> **口径调整（实施时，2026-10-02）**：T5 的 fixture/项目级场景文件（SampleMemberDesugarSolution 追加、项目级 `MemberDesugarSignatureProjectTests`）移交 **M4 T6** 一并交付；M3 只交付 Core 实现 + Core 单测（`MemberDesugarSignatureUnitTests.cs` M3 矩阵）。理由：访问器面与 NH 的项目级 fixture 与 M4 的 newslot/协变双端分派矩阵同属「.NET 预言机 + 双端对照」，合并到 M4 一次成型避免 fixture 二次返工。

- [x] T1 消费 C11–C13 产物（`IFoo.get_X`/`get_Item`/`add_Xxx` 方法对）做 SI-1/2/5；索引器/事件方法对联动
- [x] T2 SI-4 泛型构造映射（决策 5）+ 重新实现槽位重算（决策 6）；候选歧义 CS2J5197
- [x] T3 NH-3 纯 base 委托白名单桥接（决策 8 + 裁决 5）；NH-4 字段/静态隐藏改名
- [x] T4 事件后备存储不重复发射（对齐 InterfaceEventListenerNamingTests）；未启用访问器规则时仅登记不产 plan
- [x] T5 单测（§7.1-2/3 子集）——**fixture 场景文件移交 M4 T6**（见上方口径调整）
- [x] T6 收口验证 + 提交

---

## M4a new 隐藏收口（NH-1 + NH-2 + NH-5，commit 4a）

> **计划勘误（2026-10-02）**：设计 §9 里程碑表 M4 仅列 `NH-2/4、CR-1/2/3`，**遗漏 NH-1**。经 M4a 审视，NH-1（实例化石/隐式隐藏改名）与 NH-2 同属 new 隐藏面，且 M3 的 NH-4 只覆盖字段/静态方法，实例 new 方法在 M2/M3 一直是「不产 plan」的缺口。故把 **M4 拆分为 M4a（NH 隐藏补偿：NH-1 + NH-2 + NH-5）与 M4b（CR 协变 + fixture/项目测试）**；本提交为 M4a。NH 相位序维持 SI → NH → CR（设计 §4.3-1）。

- [x] T1 NH-1 实例化石/隐式（CS0108）隐藏改名 `{Name}_cs2jNew`、去 `new` 令牌、全站点按静态绑定改写；属性/索引器/事件经 C11–C13 方法化后按 `get_/set_`、`add_/remove_` 配对改名（预留表防撞，二撞 `_2`）
- [x] T2 NH-2 `new virtual/abstract` 及隐式虚隐藏（CS0114）newslot 链**整组**改名：沿 `OverriddenMethod/OverriddenProperty/OverriddenEvent` 收集 newslot 声明 + 全部编译内派生 override；基槽链不动，两链调用点各绑两名
- [x] T3 NH-5 residual/闸门：无 plan 认领的隐藏 `new` → CS2J5199（strict→Error）并计入 `SignatureResidual`；隐藏 plan 触及文件的表达式树/dynamic 站点 → CS2J5194 原子跳过；反射字符串契约 → CS2J5193 非阻断告警站点保留
- [x] T4 幂等/不重复认领：`ImplementsInterfaceMember` 排除接口实现槽（SI-4 重新实现产物二次运行不被 NH 误改名）；符号键匹配天然幂等；二次运行零 diff
- [x] T5 单测（`MemberDesugarSignatureUnitTests.cs`）：NH-1 方法（显式/隐式）+ 属性/索引器访问器配对；NH-2 三层链/`new abstract`/隐式虚隐藏；NH-5 residual 5199 + 表达式树/dynamic 5194 + 反射 5193；翻转 M3 的 `new virtual`「留 M4」测试
- [x] T6 收口验证：`dotnet build -c Release CSharpToJavaConverter.slnx` + `--filter MemberDesugar` 全绿；提交

---

## M4b 协变返回 + fixture（CR-1/2/3，commit 4b）

- [x] T1 CR-1 直映审计（决策 10）+ C27 `Cs2jWithClone` 不重复桥（复用 M1 `CovariantChainAnalyzer` 命名约定判定 → Retained，零改写）
- [x] T2 CR-2 擦除冲突桥接合成（决策 11）：bridge 基槽 + `M_cs2jCovariant` 改名方法；派生静态类型绑定点改写；sealed 形态；编译内更深派生 override 整组改名（顶层 `virtual`、派生 `override`），bridge 委托虚分派进入最深改名体
- [x] T3 CR-3 值类型/不可判 CS2J5195；CS2J5196 保守桥守卫（裁决 4：派生 override 自身仍非 Retained 时桥接不可证 → 5196；合法 C# 输入下不可达，作防御保留）
- [x] T4 CR-4 residual 闸门（协变链归宿对账 CS2J5199）；幂等/BOM（二次运行零 diff）
- [x] T5 fixture 双端分派矩阵（.NET 预言机 `SignatureBehaviorTests`）+ 项目级 `MemberDesugarSignatureProjectTests`（含 M3 T5 移交的访问器面/NH fixture）——**M4b 遗留口径**（已由 M4c 修复）：既有 fixture 的 `new` 属性隐藏（`HidingDerived::Name`，C11 访问器面）一度以 `SkipSignatures` 排除，因 NH 属性面跨文件访问器站点在方法化后无法绑定
- [x] T6 收口验证：`dotnet build -c Release CSharpToJavaConverter.slnx`（0 错误）+ `--filter MemberDesugar`（233 通过）+ fixture `dotnet build/test -c Release`（40 通过）；提交

---

## M4c 访问器面跨文件站点按接收者静态类型回退绑定（commit 4c）

> **缺口**：SI-2 让位 / NH-1 隐藏的**访问器面**（property/indexer/event）使用点若在**另一文件**，签名分相时该文件已被 C11–C13 方法化（`d.Name → d.get_Name()`）并重解析进编译，而编译中**声明文件仍是原始形态**（属性 `Name` 而非 `get_Name` 方法），故 `d.get_Name()` 无法绑定符号（`GetSymbolInfo` 返回 `Symbol=null`、带错误恢复候选）→ 稳定键匹配落空、站点漏改（M4b 项目测试以 `SkipSignatures` 绕行）。

- [x] T1 回退机制（`SignatureSyntaxFacts`）：`IsAccessorMethodName`（`get_/set_/add_/remove_` 前缀）+ `TryParseSiteKey`（解析 plan 稳定键 `{类型}::{元数据名}({参数类型,…})` 为三元组）+ `AddAccessorRename`/`TryLookupAccessorRename`（按接收者静态类型沿基链 most-derived-first 查表，镜像 C# 成员查找）；**仅解析 plan 自带结构化键，不做源码文本推测**
- [x] T2 SI 重写器（`ExplicitInterfaceRewriter`）：绑定优先（真实符号 → 让位表；错误恢复候选 → 候选命中）；`Symbol=null` 时回退按 (接收者静态类型, 访问器原始名, 参数个数) 查 `_displacedAccessor`；接口/基类型接收者不在表内 → 不动
- [x] T3 NH 重写器（`HidingRewriter`）：同构回退查 `_accessorRename`（由 `NhPlan.RenameByKey` 派生，覆盖 get_/set_/add_/remove_ 与 indexer 参数个数）
- [x] T4 单测（跨文件多源 harness）：NH 属性隐藏（读+写双向、基类型不动）、SI 属性让位（接口分派不动/类静态类型改名）、NH 事件隐藏（`+=`/`-=` → `add_E_cs2jNew`/`remove_E_cs2jNew`）；均 `AssertOutputCompiles` + 二次运行零 diff
- [x] T5 fixture 清理：移除 `MemberDesugarSignatureProjectTests` 的 `SkipSignatures` 绕行，新增 `DtoSamples.cs`/`Program.cs` 中间树断言；`.NET` 行为预言机 `BehaviorTests.Hiding_properties_dispatch_on_static_type` 原样守门（跨工程引用）
- [x] T6 收口验证：`dotnet build -c Release CSharpToJavaConverter.slnx`（0 错误）+ `--filter MemberDesugar`（236 通过）+ fixture `dotnet build/test -c Release`（40 通过）；提交

> **残留限制（已记录）**：回退仅在**符号绑定失败**时启用，且要求站点已呈 C11–C13 方法化调用形（`recv.get_/set_/add_/remove_X(...)`）。若某使用点文件在本轮未被 C11–C13 方法化（例如该文件的声明未产生访问器 plan），站点仍是属性/事件语法 `d.Name`/`d.E += h`，回退不适用——该情形本 pass 架构下不产生跨文件访问器站点（C11–C13 的 plan TouchedFiles 与本规则站点闭合一致），故当前无未覆盖用例。

---

## 验证命令（每里程碑一致）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~MemberDesugar"
```