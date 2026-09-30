# C42 平台 / 互操作边界前置隔离 设计文档

- 范围：新增注册表 pass `platform-isolation`（Core `PlatformIsolation/` + CLI `PlatformIsolationPassAdapter`），把三类平台边界（UI 平台命名空间、平台服务/OS 探针、原生互操作）在进入主转换**之前**以「审计 → 拒绝 / 桩 / 抽象端口」三档动作批量隔离；只做 C#→C# 层处理。
- 证据基准：base `953293b9`（`docs: update doc base commit`，已只读核验 `953293b908e9`；当前工作树 HEAD `55d9d1092d90`，仅只读查阅）。本文不重复 C06 已完成的 extern 实测，直接引用 [2026-09-28-c06-unsafe-ffm-design.md §1.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c06-unsafe-ffm-design.md#L57-L70)。
- 非目标：不做 P/Invoke→FFM 归一与封送布局判定（C06 R8）；不做通用属性归一（C10）；不实现 WinForms/MAUI 的 Java UI 映射；不新增 Java compat 库（M1/M2 零新支撑工程，复用既有 PNSE 映射）；不把任何 extern 静默成空实现。

---

## 1. 背景与目标

### 1.1 三类 analyzer 现状证据（2026-09-29 逐行复核）

三个 analyzer 全部位于转换器 Core 内 [UnsupportedDomainPasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs)，是**转换器管线 Check 阶段**组件，不在预处理注册表链中：

| Analyzer | 码 | 表与位置 | 消息原文（逐字复核，catalog 转述不精确） |
|---|---|---|---|
| `UnsupportedDomainAnalyzer` | CS2J3001 | UI 黑名单 6 前缀（:10-18：`System.Windows.Forms`/`System.Windows.Controls`/`System.Windows`/`Microsoft.Maui`/`Windows.UI.Xaml`/`System.Xaml`）；using/QualifiedName/MemberAccess 三面扫描（:31-73） | `Namespace '…' is not supported by the Java-only conversion pipeline.`（:44/57/70；**不是** "must be isolated"） |
| `PlatformBoundaryAnalyzer` | CS2J3101 | OS guard 属性 4 个（:146：`SupportedOSPlatform`/`UnsupportedOSPlatform` 及两个 Guard） | `Platform boundary attribute '…' must be isolated before Java conversion.`（:151） |
| 同上 | CS2J3102 | 平台**类型**前缀 9 个（:106-117：Registry/RegistryKey/EventLog/ManagementObject(Searcher)/ServiceController/DirectoryEntry/DirectorySearcher/SerialPort）；平台**成员**前缀 8 个（:119-129：`Environment.OSVersion`、`OperatingSystem.IsWindows/IsLinux/IsMacOS`、`RuntimeInformation.IsOSPlatform/OSDescription/OSArchitecture/ProcessArchitecture`）；Invocation/MemberAccess/Type/ObjectCreation 四面扫描（:156-229） | `Platform-specific API/type '…' must be isolated before Java conversion.`（:168/189/209/226） |
| `NativeInteropAnalyzer` | CS2J3201 | 互操作属性 6 个（:265：`DllImport`/`LibraryImport`/`ComImport`/`MarshalAs`/`UnmanagedCallersOnly`/`GeneratedComInterface`） | `Native interop attribute '…' is not supported …`（:270） |
| 同上 | CS2J3202 | 互操作类型 4 个（:245-251：`Marshal`/`NativeLibrary`/`HandleRef`/`SafeHandle`）；调用/成员/类型/构造四面（:289-355） | `Native interop API/type '…' is not supported …` |
| 同上 | CS2J3203 | 所有 `extern` 方法（:275-287） | `Extern method '…' is not supported …`（:285） |

语义判定统一走 `BoundaryAnalysisHelpers.TryResolveSymbolIdentity/TryResolveTypeIdentity`（:405-466，SemanticModel 失败回退文本）；去重走 `BoundaryDiagnosticCollector`（:469-494，`code|category|path|line|col|message` 键）。

两条文件级豁免（:361-381）：

1. `IsGeneratedAssemblyInfoFile`（:369-371）：`*.AssemblyInfo.cs` 跳过 CS2J3101（MSBuild 生成的 assembly 级 OS 标注），两条管线均生效；
2. `IsPInvokeContainerFile`（:379-381）：文件名为 `NativeMethods.cs` 时**整文件跳过 NativeInteropAnalyzer**——仅项目管线实际生效；单文件管线因 [ConversionPipeline.cs:148](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L148) `ParseText` 未传 FileName 而恒不命中（C06 §1.2 实测）。:373-377「ClassTransformer already emits stub bodies」注释为**陈旧假陈述**。

另有一条现网白名单：`HasCompatNamespacePrefix`（:84-101）对具备显式 TypeMappings 命名空间映射（compat 包兜底）的前缀豁免 CS2J3001；映射来自 `TypeMappingRegistry.GetExplicitNamespaceMappings()`，CLI 装载点 [Program.cs:1520-1529](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L1520-L1529)，测试默认配置 `config/TypeMappings.json`（[Phase2PassPipelineTests.cs:478-484](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Phase2PassPipelineTests.cs#L478-L484)，WinForms 放行用例 :96-108）。

测试工程探针降级：项目管线对 `IsTestProject` 传 `shouldDowngradeTestPlatformProbes`（[ProjectPasses.cs:460-474](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L460-L474)），6 个探针身份（`IsWindows/IsLinux/IsMacOS/IsOSPlatform/ProcessArchitecture/OSArchitecture`，:234-240）降为 Warning 不阻断；**注意 `OSDescription` 不在降级集**。单文件管线不降级（[SingleFilePasses.cs:166-190](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L166-L190)）。

### 1.2 注册方式：诊断在转换器内，预处理链无承接

- 单文件管线：[ConversionPipeline.cs:417-419](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L417-L419)，三个 `SingleFile*CheckPass` 位于 CompilationCheck 之后、emit 之前；Error → `BlockEmit = true`（SingleFilePasses.cs:159-162/185-188/211-214）。
- 项目管线：[ProjectConversionPipeline.cs:269-271](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs#L269-L271)，Error → `RecordBlockingDiagnostics` 阻断该文件。
- 注册表 stub：[PassStubCatalog.cs:237-246](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L237-L246)，id `platform-isolation`、目录 `.cs2j-platform-isolation-src`、`PassStage.Abi`、`PassStatus.Stub`、默认关、`RunAfter = eliminate-goto`；启用即 CS2J9001。
- 缺口 = **有诊断、无隔离动作**：现状只能整文件失败/阻断 emit，批量隔离（重定向探针、抛错桩、端口抽象）无从下手；且 extern 逃过检查后在 [ClassTransformer.cs:346-350](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L346-L350)（`IsExternLike` :390-392）硬抛 `NotSupportedException`。C06 §1.2 已实测定论：**任何路径都不产生 stub**，NativeMethods.cs 在项目管线最终 `[Error]`、无 `NativeMethods.java`。
- 转换器内已有一处**手工点状代偿**先例：[ProjectConversionPipeline.cs:152/205-254](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs#L205-L254) 按文件名（`Regex.cs`）把 `Regex.CompileToAssembly` 方法体改写成抛 `PlatformNotSupportedException`——这正是 C42 要批量通道化、从转换器内迁出的形态。PNSE 映射已存在（[TypeMappings.json:816-819](file:///d:/code/cs2j/config/TypeMappings.json#L816-L819)，`io.github.ningpp.compat.PlatformNotSupportedException`）。

### 1.3 目标

1. **M1 审计显式化**：在最终中间树上复用三 analyzer 产出整树机器可读清单（report-only，零字节改动），把「诊断散落于转换器 Check 阶段」升级为可批量消费的 inventory + 报告，保留全部 CS2J3xxx 诊断码与消息。
2. **M2/M3 批量隔离自动化**：按策略表对每处站点执行 Reject / Stub / Facade 三档动作，以中间树 `dotnet build/test -c Release` 守门；每处隔离必有对应报告条目（禁止静默）。
3. **永不静默空实现**：extern 不发桩；桩成员显式抛 PNSE 或保守返回；转换器侧检查与 ClassTransformer fail-fast 全部保留为安全网。

### 1.4 与 C06/C10/C25/C36 的分工

| 通道 | 负责面 | C42 边界 |
|---|---|---|
| C06 unsafe-ffm | extern/DllImport/LibraryImport 的归一、FFM 白名单、UF060/UF061/UF070 判定、封送布局、SafeHandle 选型 | C42 对互操作族只做**盘点/拒绝策略与批量编排**，语义判定与归一不重造（C06 §11 :302） |
| C10 attribute-normalize | P/Invoke 属性只盘点透传（AN003），CS2J3xxx 发射权在 C42/C06（[C10 §1.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c10-attribute-normalize-design.md#L62-L66)） | C42 消费 C10 inventory 作输入之一，不在同节点重复 C10 改写 |
| C25 dynamic-desugar | 审计型 pass 范式（report JSON、manifest、residual 双扫、Warning 审计默认开候选） | C42 复用同一报告/闸门形态与 `BoundaryDiagnosticCollector` 去重基建，码位独立 PI0xx |
| C36 CSharp12Desugar | 「盘点 + 显式拒绝」防线（CE-GUARD） | 共享拒绝优先/无占位原则，不共享码位 |
| C20 partial-merge | partial 类型合并（链早段） | C42 在链晚段生成 partial 桩文件，须与 C20 产物幂等（§2 D6） |

### 1.5 两管线现状差异速查（M1 保真基线）

| 维度 | 项目管线 | 单文件管线 |
|---|---|---|
| compat 映射前缀豁免（CS2J3001） | 生效（ProjectPasses.cs:431-433） | 生效（SingleFilePasses.cs:151-153） |
| `*.AssemblyInfo.cs` 豁免（CS2J3101） | 生效（:463） | 生效（:173-176） |
| `NativeMethods.cs` 豁免（3201/3202/3203） | **生效**（:517，逃检后 ClassTransformer 硬抛） | **恒不生效**（ParseText 无 FileName，C06 §1.2） |
| 测试工程 6 探针 Warning 降级 | 生效（isTestProject，:460/474；注意 OSDescription 不在降级集） | 不生效，恒 Error（:179 未传降级参） |
| Error 后果 | `RecordBlockingDiagnostics`：该文件不 emit，其余文件继续 | `BlockEmit = true`：本次无 Java 输出 |
| 诊断聚合 | PlanningTests.cs:441-472 已消费码/类别做摘要统计 | 结果 Diagnostics 直出 |
| 预处理链 | 均不经过 `platform-isolation`（stub 默认关） | 同左；独立 verb 接入后才有 C42 面 |

M1 inventory 的分类结果必须与上表逐格一致（含 Warning/Error 级别与豁免分类），这是「零行为变化」的可执行判据，而非口号。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 边界/理由 |
|---|---|---|---|
| D1 | ★隔离动作三档 | **Reject**＝显式失败（现状，PI030 在预处理期早失败并引用原 CS2J3xxx 码）；**Stub**＝生成可编译替身：平台服务成员→抛 PNSE 桩，OS 探针→保守值 compat 探针，guard 属性→擦除；**Facade**（半自动）＝生成可移植端口接口 + 原类型改 partial、成员体委托端口，用户在受控 partial 文件补实现 | 三档均可逐族/逐站点由配置降级；默认档见 D2 |
| D2 | ★逐类目标默认档 | 见 §2.1 策略表 | UI=Reject（compat 映射前缀沿用现网白名单）；OS 探针=Stub；平台服务类型=Reject（Stub/Facade opt-in）；互操作全族=Reject 交 C06 |
| D3 | 隔离粒度 | 三级：**文件级**（NativeMethods.cs/AssemblyInfo/纯平台包装类：整文件裁决）、**成员级**（签名含平台类型的方法/属性：桩或端口，签名移植化）、**语句/表达式级**（仅 OS 探针调用点重定向） | `Marshal`/Registry 等无语义替身的调用**不做**语句级替换；成员桩的签名移植规则见 D5 |
| D4 | 白名单/配置载体 | 源根 JSON（`Cs2jPlatformIsolation.json`，CWD 向上查找，沿用 ReadOnlyStruct/Record/JavaKeywords overrides 与 C25 manifest 约定）；**不走 csproj 标注** | Core 无 MSBuild item 通道，json 可被 Core 与单文件独立 verb 直接读取；配置随源码受控、不进 .gitignore |
| D5 | 桩签名移植 | 成员签名含 `RegistryKey` 等平台类型时：Facade 档换为端口接口类型；Stub 档换为可移植句柄（`string`/`System.IntPtr`/`object`）并在报告记录签名变更，调用点由编译器暴露人工修复；禁止保留平台类型参数 | 中间树必须可编译；签名变更一律 PI010 Info 列明 |
| D6 | 桩命名空间与 partial 拼接 | 探针 shim 单一文件 `Cs2jPlatformProbe.g.cs`，命名空间 `Cs2j.Platform`，**仅含 BCL + 保守常量/PNSE**（自身零平台身份）；成员桩 `<DeclaringType>.PlatformStub.g.cs`，namespace/类名与原类型一致、标 `partial`；经 `AfterFilesAsync` 按 tuple 生成文件范式分发 | C20 在链早段、C42 在链晚段：C42 产物天然作用于 C20 合并后的树；转换器现有 partial 归一（`ProjectPartialTypeNormalizationPass`、`MemberKey` 去重）承接多部件，C20 翻转后加联合幂等测试（§11） |
| D7 | 诊断码 | **检测码不加新码**：沿用 CS2J3001/3101/3102/3201/3202/3203 与现有消息原文；**新增 PI0xx 仅描述隔离动作/报告/闸门**（§6） | 既有 14 处 CS2J3xxx 测试引用（Phase2 10 / Planning 4）在 pass 默认关时零改动 |
| D8 | extern 红线 | 任何模式下 C42 **不对 extern/DllImport 生成桩或空体**；Reject 档引用 CS2J3203/UF060 指引；白名单归一完全等 C06 R8 | catalog 风险16 + C06 §1.2；`NativeMethods.cs` 文件名豁免在 C42 启用后不再是逃生通道（§10 R4） |
| D9 | 支撑库 | M1/M2 **零新 C# 支撑工程**：探针/桩只用 BCL，PNSE 走既有 compat 映射；Facade 端口是纯生成源码 | 若 M3 证明需要可移植 probe 实现包，另立注入子项（对齐 C06 D12） |
| D10 | 语义模型 | `needsSemanticModel = true`；3102/3202 的身份判定依赖 SemanticModel（现状即如此）；单文件模式跨文件 Facade 规划发 PI090 跳过（同 UF090/JK090 范式） | 3101 属性扫描纯语法可独立工作 |
| D11 | residual 闸门 | apply 模式输出树双扫（语法节点 + 三表语义身份，生成文件路径豁免），任一非登记残留 → PI031 Error，文件不产出 | 清单驱动而非文本 grep（using 别名/全限定/`var` 隐式均可抓） |
| D12 | 幂等 | 产物二次执行：Report 站点为 0 或全部命中登记；Stub/Facade 重写节点不可被自身再识别；报告字节跨运行稳定（文件+span 排序、UTF-8 无 BOM） | 对齐 C25/C06 幂等约束 |

### 2.1 逐族默认策略表

| 族 | 现状码/表 | 默认档 | 动作形态 |
|---|---|---|---|
| UI 命名空间（6 前缀） | CS2J3001 | **Reject**；compat 映射前缀沿用 `HasCompatNamespacePrefix` 现网豁免 | C42 不新开 UI Facade；已有 WinForms→compat 映射走配置面 |
| 平台服务类型（9 个） | CS2J3102 type | **Reject**；Stub（PNSE 成员桩）/Facade opt-in | 注册表/事件日志/WMI/服务/AD/串口在 Java 无中立语义 |
| OS 成员探针（8 个） | CS2J3102 member | **Stub**：调用点重定向 `Cs2jPlatformProbe`，布尔探针保守 `false`、架构/描述探针保守 `"Unknown"`/中性枚举 | guard 子句抛错会改变语义，故探针**保守返回不抛错**；测试工程 6 探针现状 Warning 降级保留 |
| OS guard 属性（4 个） | CS2J3101 | **Stub**：擦除标注（Java 本就忽略 assembly 级标注）+ PI012 | AssemblyInfo 现状豁免升级为「识别并登记」，不再纯跳过 |
| 互操作属性（6 个） | CS2J3201 | **Reject**，归一/白名单交 C06 R8；C10 盘点 | 消息附「C06 FFM 白名单或人工端口」指引 |
| extern 方法 | CS2J3203 | **Reject**（C06 UF060），**永不发桩** | D8 |
| 互操作类型/API（4 个） | CS2J3202 | **Reject**（C06 UF070）；SafeHandle/HandleRef 长期选型归 C06 | C06 D8 |
| 已归一 interop facade / C42 生成标记 | — | 共享谓词识别后不报（§4.5） | 防 3201/3203 双报（C06 §6 :215） |

---

## 3. 注册表与链位契约

### 3.1 stub 现状与翻转目标

[PassStubCatalog.cs:237-246](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L237-L246)：id `platform-isolation`、显示名 "Platform boundary isolation"、目录 `.cs2j-platform-isolation-src`、`PassStage.Abi`、`PassStatus.Stub`、`defaultEnabled: false`、`runAfterIds: { PassIds.EliminateGoto }`、catalogItems `C42`。禁用零成本；显式启用即 CS2J9001。

翻转后描述符：Status=`Implemented`（M1 审计落地即翻，审计即完整实现，同 C25 口径）；Stage 维持 `Abi`；目录/RunAfter 维持（M3 评估加边，见 §3.2）；`NeedsSemanticModel=true`；`SupportLibrary=None`（D9）；verify 双开仅对 Stub/Facade apply 生效，Report 模式 adapter 内关闭；默认关长期保持（§8）。

### 3.2 为什么锚 eliminate-goto；要不要加 RunAfter unsafe-ffm

晚链位理由（与 C25 §3.3 同族同锚）：

1. **隔离对象是最终中间树**：前置 pass 全部结构去糖后，清单才是真正流入 Java 转换器的平台面，不对将被消除的糖节点重复计数；
2. 部分 pass 的生成代码可能**引入**新调用（状态机/提升/生成文件），晚位能抓到；
3. 必须早于 java-keywords：[PassCatalog.cs:249-258](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L249-L258) 已用 RunAfter 边锁定 FinalNaming 最后；
4. 不注入支撑库、不改名，与前序 pass 无文件冲突（生成文件除外，D6）。

与 C06 的张力：三 analyzer 表是 C06 R8 的输入之一，且 Facade 档要消费「已归一 facade」谓词——但 M1（审计）/M2（OS 探针桩）**不触碰**互操作归一，无硬依赖。结论：

- **M1/M2 维持 `RunAfter = { eliminate-goto }` 不加边**；对互操作族只在报告中给出「交 C06 白名单/手工」分组，C06 未合也能独立交付；
- **M3 追加 `RunAfter = { eliminate-goto, unsafe-ffm }`**。无环性论证：unsafe-ffm → struct-value-semantics → make-readonly；eliminate-goto → make-readonly/iterator-lowering；两条路径均无任何节点回指 platform-isolation（java-keywords 在两者下游，不构成回边），加边只把 platform-isolation 锁定到 unsafe-ffm 之后，拓扑无环（注册表启动期环校验 [PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133) 兜底）；stub 禁用时该边从启用子计划过滤（eliminate-goto 锚 iterator-lowering stub 是同款先例，[PassCatalog.cs:227](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L227)）；
- 当前 stub 声明顺序 platform → concurrency → dynamic（PassStubCatalog.cs:237/247/259），翻转时在顺序断言中锁定相对位置（同 C25 §3.3 约定）。

### 3.3 Stub→Implemented 翻转清单

1. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删 `Ids.PlatformIsolation` 常量与 :237-246 条目，常量迁 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L25) `PassIds`；
2. PassCatalog.cs：java-keywords RunAfter 中 `PassStubCatalog.Ids.PlatformIsolation`（:254）改 `PassIds.PlatformIsolation`；`Implemented` 表插描述符（eliminate-goto 之后、concurrency/dynamic 相邻位，先于 java-keywords）；M3 才改 RunAfter 加 unsafe-ffm；
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) `CreateAdapter` switch 加 `PassIds.PlatformIsolation => new PlatformIsolationPassAdapter(...)` + Options 懒加载工厂；无 legacy toggle（不新增 `--no-platform-isolation`，走通用 `--enable-pass/--disable-pass`）；
4. 新增 CLI `Adapters/PlatformIsolationPassAdapter.cs`（§5）；
5. [.gitignore:89-102](file:///d:/code/cs2j/.gitignore#L89-L102) 增 `.cs2j-platform-isolation-src/`；`Cs2jPlatformIsolation.json`/`Cs2jInteropWhitelist.json` **不**忽略；
6. [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)：Stub→Implemented、默认关、目录、RunAfter 锚点与相对顺序、CS2J9001 对该 id 消失；
7. 不新增 `Project*Preprocessor` 薄包装（stub 先行通道无历史 verb，对齐 C21/C25 口径）。

### 3.4 目录与产物

- 中间树根 `.cs2j-platform-isolation-src/`（descriptor 驱动，接指纹令牌 `pass:platform-isolation=on/off`）；
- 报告：中间树根 `platform-isolation-report.json`（两种模式都写；Report 模式它是主交付物），结构＝运行元信息 + 站点数组（文件/行列/span/family/kind/绑定身份/默认档/最终动作/诊断码/配置命中），按文件+span 排序、UTF-8 无 BOM；
- 生成文件：根下 `generated/` 目录（`Cs2jPlatformProbe.g.cs`、`*.PlatformStub.g.cs`、`*Port.cs`）；
- 配置：源根 `Cs2jPlatformIsolation.json`（策略覆盖 + allowlist + suppress 清单，键含文件+span+kind+理由）。

### 3.5 启用/禁用行为对照与失败面

| 状态/模式 | 源码树 | 诊断行为 | verify | 后续转换器 |
|---|---|---|---|---|
| 禁用（现状、长期默认） | 不触碰，无中间目录 | 三 CheckPass 完全照旧（含豁免/降级） | — | 照旧；extern 逃检仍在 ClassTransformer 抛（待 C06 M0/M4） |
| 启用 Report | 字节复制，仅多报告 JSON | 每站点 PI001（Info/Warning），不阻断；`--platform-strict` 时 Error 级站点升 Error | 关 | 照常执行，3xxx 双报为预期（R7） |
| 启用 Reject | 字节复制 | Error 级站点 PI030，本环失败、链中止、退出码非 0；中间目录 preserve 供排查 | 关 | 不再流入 |
| 启用 Stub/Facade | 按计划改写 + generated/ 分发 | PI010/011/012/020 Info；无法裁决 → PI030/PI040 失败 | build/test 双开 | 流入时树已无平台身份，三 CheckPass 自然通过；fail-fast 仍作安全网 |

任何 apply 模式失败都遵循「拒绝优先、不交付部分改写」：出错文件 `Changed=false` 或整环失败，禁止「部分隔离 + 其余原样透传」的混合产物（同 C06 §4 工程约束）。

---

## 4. Core 组件设计

新建目录 `src/CSharpToJava.Core/PlatformIsolation/`，命名空间 `CSharpToJava.Core.PlatformIsolation`，组织对齐 `JavaKeywords/`、`DynamicDesugar/`：

| 文件 | 职责 |
|---|---|
| `PlatformIsolationPreprocessor.cs` | 门面：`BindCompilation(Compilation)`、`BuildInventory()`（整树一次缓存）、`RewriteFile(...)`、`CloseReport()`；M1 仅审计，apply 模式内存合批跑 Generator 链 + 末端 residual 闸门 |
| `BoundaryInventory.cs` | 把三 analyzer 输出归并为**统一站点记录** `BoundarySite`（Family：UiNamespace/PlatformType/PlatformMember/OsGuardAttribute/NativeAttribute/NativeType/ExternMethod；Location；身份串；SyntaxKind；默认档；豁免分类：AssemblyInfo/NativeMethods/CompatMapping/TestProbeWarning/Generated）；同款键去重 |
| `BoundaryTables.cs`（M2 抽出） | 6/9/8/4/6/4 张表的**单一真相**：M1 直接引用现有 analyzer 内部表（同程序集 internal，零搬迁零行为变化）；M2 把表迁到本文件，三 analyzer 与本 pass 共同消费，消息/码逐字锁定（测试守门） |
| `IsolationPlan.cs` | 站点分组（文件/成员/语句三级，D3）+ D2 策略表裁决 + 配置覆盖；输出有序 `IsolationAction`（Reject/StubProbe/StubThrow/StripAttribute/Facade/Suppress）与签名移植方案（D5） |
| `StubGenerator.cs` | 探针调用点重定向（CS2J3102 member 8 身份）、保守值 shim 发射、PNSE 成员桩、guard 属性擦除；`Cs2jPlatformProbe.g.cs` 单例发射；改写后三候选语法校验（仿 JK/E06 invalid-syntax 保护） |
| `FacadeGenerator.cs`（M3） | 端口接口抽取（`I<Name>Port`）、原类型改 partial + 成员体委托端口、用户实现槽位（partial，不生成实现体）；与 C06 interop facade 共用标记体系 |
| `PlatformIsolationOptions.cs` | `Mode`（Report/Reject/Stub/Facade，默认 Report）、`Strict`、`ConfigPath`、`EnableProbeStub`/`EnableThrowStub`/`EnableFacade`（里程碑位） |
| `PlatformIsolationStatistics.cs` | 按 family/action 计数；**对账不变式**：inventory N = rejected + stubbed + facaded + suppressed + residual（报告中输出，违例即 PI031） |
| `PlatformIsolationDiagnostic.cs` | PI0xx 码描述、严重级、FileResult 映射 |
| `PlatformIsolationReport.cs` | 报告 JSON 与配置（`Cs2jPlatformIsolation.json`）模型、加载、键匹配、陈旧条目检测（仿 C25 manifest） |
| `PlatformResidualGate.cs` | D11 双扫：语法节点（using/属性/extern 修饰符）+ 三表语义身份在输出树重扫；`generated/**` 与配置登记面豁免 |

### 4.1 与三 analyzer 的复用/迁移节奏（关键：M1 零行为变化）

- **M1 不搬表、不改码、不改豁免**：`BuildInventory()` 对每个 SyntaxTree 直接调 `UnsupportedDomainAnalyzer`/`PlatformBoundaryAnalyzer`/`NativeInteropAnalyzer`（同 Core 程序集 internal 可见），入参复刻两个 CheckPass 的现状（compat 前缀、`shouldDowngradeTestPlatformProbes`、两条文件豁免），输出包装成 `BoundarySite`。转换器管线三个 CheckPass 一行不动——pass 默认关时全链行为字节不变。
- **M2 抽表**：`BoundaryTables.cs` 成为表与身份匹配的单一真相，三 analyzer 改为薄封装（保持 public/internal 签名与消息原文），本 pass 与 CheckPass 共用；抽表 PR 单独提交、以 [Phase2PassPipelineTests.cs:95-142/234-333](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Phase2PassPipelineTests.cs#L95-L142) 与 PlanningTests.cs:441-472 全绿锁行为。
- CheckPass **永不因本通道删除**：直连单文件、预处理未跑、残余漏网时它们仍是最后安全网（同 C06 D14、C25 §6.3 范式）；默认开门并语料验证后才评估退役为独立 PR。

### 4.2 Stub 形态细节

- 探针 shim（每个工程一份）：`namespace Cs2j.Platform { internal static class Cs2jPlatformProbe { public static bool IsWindows() => false; … public static string OSDescription() => "Unknown"; } }`——布尔探针**保守 false**（Java 宿主非 Windows，guard 取 false 分支是 .NET/Java 两侧最接近的中立语义）；架构探针返回中性枚举/字符串。方法表与 8 个 member 身份一一对应，报告给出每处重定向的 PI011。
- 抛错桩：`throw new PlatformNotSupportedException("CS2J platform stub: <原身份> at <文件:行>; isolated by platform-isolation");`——PNSE 映射已存在（TypeMappings.json:816-819）；消息含原身份与出处，可追溯。
- 所有生成成员带源内头注释 + 生成标记（§4.5），报告 JSON 为权威台账；禁止无注释/无报告的桩。
- guard 属性擦除：assembly/class/method/return/param 级 `SupportedOSPlatform*` 整体移除（:146 四属性），AssemblyInfo 文件从「跳过」改为「登记+擦除」（PI012）。

### 4.3 Facade 形态（M3，半自动）

对平台服务类型（如 `SerialPort` 包装类）：生成 `I<Type>Port`（只含可移植签名的成员子集）、原类型改 `partial` 并持有可空端口字段、原成员体改为 `if (_port is null) throw new PlatformNotSupportedException(…); return _port.M(…);`；用户实现另放受控 partial 文件（本 pass 不生成、不填）。配置中逐站点授权 Facade；未授权不生成。

### 4.4 配置 schema 要点

`Cs2jPlatformIsolation.json`：`policies`（按 family 覆盖默认档）、`allowlist`（额外 compat 前缀/身份前缀，对 CS2J3001 与现网 TypeMappings 豁免取并集）、`suppress`（`{file, span, family, reason, owner}` 显式知情残留，strict 下仍 Warning）、`probeDefaults`（探针保守值覆盖，仅允许 false/中性值，禁止 true）。陈旧条目（源内已无对应站点）→ PI061。

### 4.5 ★共享谓词「已归一 facade」的落点（回答 C06 开放问题）

C06 §2 开放问题首项（白名单配置格式，[C06:108](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c06-unsafe-ffm-design.md#L108)）与 §6/§11 点名的「已归一 facade」共享谓词，本文档给结论建议（最终由 C06 拍板）：

1. **落点**：新建 `src/CSharpToJava.Core/Analysis/InteropBoundaryFacts.cs`，与已落地的 [ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs) 及 C25 规划的 `DynamicSiteFacts.cs` 并列（不属于任一 pass 目录）。建议 API：
   - `bool IsNormalizedInteropFacade(SemanticModel, SyntaxNode)`（识别 C06 R8 归一后的受控 facade 声明/调用）；
   - `bool IsPlatformIsolationGenerated(SyntaxTree/SyntaxNode)`（识别 C42 generated 标记文件/成员）；
   - `const string InteropFacadeMarker = "Cs2j.Generation.InteropFacadeAttribute"`、`const string PlatformStubMarker = "Cs2j.Generation.PlatformStubAttribute"`（两个 internal 标记类由各自 pass 在 generated/ 发射一次，常量 FQN 三处共享不拼字符串）。
2. **消费方式**：C06 R8 白名单归一节点打 InteropFacade 标记；C42 Stub/Facade 产物打 PlatformStub 标记；三 analyzer（3201/3203/3102）在「对应 pass 已启用」的编译中经谓词抑制同节点重复诊断（pass 禁用时谓词恒 false，现状不变）。
3. **白名单配置格式建议＝独立 JSON**：源根 `Cs2jInteropWhitelist.json`（C06 所有）、CWD 向上查找，与 C25 manifest/overrides 惯例一致；**不建议 csproj 标注**——Core 编译与单文件 verb 都拿不到 MSBuild item 通道，json 可被两端直读且可跨 pass 复用同一装载/陈旧检测代码。
4. **时序**：M1 即创建 `InteropBoundaryFacts.cs`（签名冻结、谓词在无标记时恒 false），C06 M4 与 C42 M3 各自消费；谁先合谁落地，无代码依赖（与 C06 §11「UF 码契约先冻结、不要求代码先合」一致）。

### 4.6 报告 JSON 结构（字节稳定契约）

```json
{
  "tool": "cs2j platform-isolation",
  "mode": "Report",
  "generatedAtUtc": "…（仅元信息，不参与稳定排序）",
  "totals": { "sites": 0, "reject": 0, "stub": 0, "facade": 0, "suppressed": 0, "residual": 0 },
  "sites": [
    {
      "file": "src/Env/OsInfo.cs",
      "line": 12, "column": 16, "span": [ 312, 344 ],
      "family": "PlatformMember",
      "code": "CS2J3102",
      "identity": "System.OperatingSystem.IsWindows",
      "syntaxKind": "InvocationExpression",
      "defaultAction": "Stub",
      "action": "ProbeRedirect",
      "replacement": "Cs2j.Platform.Cs2jPlatformProbe.IsWindows()",
      "diagnostic": "PI011",
      "configMatch": null,
      "confidence": "Symbol"
    }
  ]
}
```

约定：站点按 `(file, line, column, code)` 排序保证跨运行字节一致；`confidence` 取 `Symbol`/`SyntaxFallback`（后者必伴随 PI090）；`action` 枚举 `None/Reject/ProbeRedirect/ThrowStub/StripAttribute/Facade/Suppressed` 与统计字段一一对应（对账不变式）。配置 schema（§4.4）中 suppress 条目必须含同形 `file+span+family` 键，陈旧检测以此匹配。

### 4.7 IsolationPlan 分组算法（apply 模式）

1. **入料**：`BoundaryInventory`（整树一次）+ SemanticModel + Options/配置；
2. **文件级裁决先行**：站点覆盖整文件且文件内无其它业务成员（纯 `NativeMethods`/平台包装类）→ 文件级 Reject 或整文件 Stub；AssemblyInfo → 属性擦除；
3. **成员级聚类**：按 `(文件, 声明类型, MemberKey)` 把同成员体内站点聚类；任一站默认 Reject 且无语义替身 → 整成员 Reject；签名含平台类型 → 按 D5 出签名移植方案（Facade 优先端口类型，Stub 退可移植句柄）；
4. **语句级收编**：仅 OS 探针 8 身份的调用/成员访问做调用点重定向；同一表达式中的复合用法（`IsOSPlatform(OSPlatform.Windows) || …`）整体重写并保证短路结构不变；
5. **配置覆盖**：allowlist/suppress 在默认档裁决之后应用（只可降不可升：不允许把互操作 Reject 配成静默 Stub）；
6. **输出**：有序动作表（文件分组）交 StubGenerator/FacadeGenerator；每动作回写站点 `action/diagnostic`，未裁决站点保持 Reject（宁错杀，R1 假阳可接受、假阴不可接受）；
7. **收口**：重写树过三候选语法校验（PI040）→ residual 双扫（PI031）→ 统计对账。

---

## 5. CLI 适配器与集成

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/PlatformIsolationPassAdapter.cs`，继承 [ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L117-L229)，标准 spine（不 override `ExecuteAsync`，与 C21/C25 同型）：

- `Descriptor => PassCatalog.ImplementedById(PassIds.PlatformIsolation)`；`CreateParseOptions`：`LanguageVersion.Latest` + NETCOREAPP；`CreateCompilationOptions`：沿用 allowUnsafe 默认（extern 节点需可 parse）；`CompatNamespacePrefixes` 复刻现状 TypeMappings 装载（M1 审计与现网豁免保持一致）；
- `CompilationReadyAsync`：`BindCompilation` + `BuildInventory`（每文件诊断在 RewriteFile 阶段按本文件站点产出，保 `RelativePath` 前缀惯例）；
- **Report 模式**（启用后默认）：`RewriteFileAsync` 恒 null（字节复制）；`AfterFilesAsync` 写 `platform-isolation-report.json`；VerifyBuild/Tests 返回 false（不 restore/不 build）；`DeleteBuildOutputsAfterRestore=false`；站点默认 Warning（3xxx 原 Error 级在报告模式只报告不阻断），`HasErrors` 仅 `--platform-strict` 为真；
- **Reject 模式**：源码不改，命中 Error 级站点即 PI030（消息携带原 CS2J3xxx 码与身份）→ 本环失败、链中止（把现状转换器期失败前移为台账化早失败）；
- **Stub/Facade 模式**：`RewriteFileAsync` 调 Core（Changed=false→null 字节复制），生成文件在 `AfterFilesAsync` 分发（仿 tuple 适配器）；VerifyBuild/Tests 取描述符双开（受全局 `--no-verify-*` 控制）；`DeleteBuildOutputsAfterRestore=true`；`OnRestoreFailed/OnBuildFailed/OnTestsFailed` → PI050/051/052；
- 摘要行：`platform-isolation: N site(s) (UI a, platform b, probes c, interop d); reject=R, stub=S, facade=F, suppressed=X, residual=K; mode=…`；
- 选项（经 [PassChainFactory](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) 懒加载工厂，仅启用时构造）：`--platform-report-only`、`--platform-mode <Report|Reject|Stub|Facade>`、`--platform-strict`、`--platform-config <path>`；
- 独立入口：通用 `preprocess --pass platform-isolation` 携带相同选项，可对原始源码直接审计（语义身份在无工程编译时退化为文本匹配，相关站点标 PI090，不回假阴性——仍扫描但标注低置信）；
- 失败语义走 runner 既有机制（Error → HasErrors → 退出码非 0）；指纹随全集令牌自动包含；无支撑库过滤项。

---

## 6. 诊断策略

**检测码沿用（不加新码、不改原文）**：CS2J3001（:44 原文）、CS2J3101（:151）、CS2J3102（:168 等）、CS2J3201（:270）、CS2J3202、CS2J3203（:285）；测试工程 6 探针 Warning 降级与两条文件豁免的现状语义在 M1 完整保留。

**PI0xx 动作/报告码（只追加）**：

| 码 | Report | Apply | 含义 |
|---|---|---|---|
| PI001 | Info | Info | 站点入清单（每站点一条，含 family/身份/位置/默认档） |
| PI010 | — | Info | PNSE 抛错成员桩生成（附签名移植明细 D5） |
| PI011 | — | Info | OS 探针调用点重定向至 `Cs2jPlatformProbe`（附原值→保守值） |
| PI012 | — | Info | OS guard 属性擦除（含 AssemblyInfo 登记） |
| PI020 | — | Info | Facade 端口/partial 委托缝生成，列用户待实现槽位 |
| PI030 | — | Error | Reject 裁决执行（消息必带原 CS2J3xxx 码、身份、建议：C06 白名单/手工端口/配置登记） |
| PI031 | Info（不闸门） | Error | residual 闸门违规：输出树仍有非登记平台身份 |
| PI040 | — | Error | 生成树语法校验失败，原文保留（对齐 JK/E06 保护） |
| PI050/051/052 | — | Warning/Error | 中间树 restore/build/test 失败 |
| PI060 | Info | Info/Warning | 站点命中配置 suppress（strict 升 Warning，永不静默：仍出报告条目） |
| PI061 | Warning | Warning | 配置陈旧条目 |
| PI070 | Info | Info | 报告写出（路径、站点数、对账行） |
| PI090 | Warning | Warning | 单文件/无工程编译下跨文件 plan 跳过或身份降级（D10） |

**禁止静默（核心纪律）**：

1. 每处隔离动作 1:1 对应报告条目与 PI 码；Statistics 对账不变式（N=rejected+stubbed+facaded+suppressed+residual）随 PI070 输出，不平时 PI031；
2. 探针保守值替换必须在报告显示「原值→保守值」，不允许悄悄改 guard；
3. extern 不允许出现「空方法体/`=> default`/TODO 注释」任何一种静默形态（D8，呼应 catalog §4 风险12）；
4. 消息句式统一：`<family> site '<身份>' at <位置>: <动作与理由>; <建议动作>`；禁止只说 unsupported 不给位置与去向。

---

## 7. 测试计划

### 7.1 Core 单测（建议 `tests/CSharpToJava.Tests/PlatformIsolationUnitTests.cs`）

按家族构造 in-memory compilation 断言 inventory 分类、档位裁决与改写文本：

- **UI**：`using System.Windows.Forms;`、全限定 `Microsoft.Maui.Controls.ContentPage`、成员访问三面 → CS2J3001 站点；compat 前缀命中 → Suppress/豁免分类；
- **平台服务类型**：`RegistryKey`/`EventLog`/`SerialPort`/`ManagementObjectSearcher`/`ServiceController`/`DirectoryEntry` 的类型/构造/成员访问 → CS2J3102 type，默认 Reject、Stub 档产 PNSE 桩且签名移植（D5）可编译；
- **OS 探针**：`OperatingSystem.IsWindows/IsLinux/IsMacOS()`、`RuntimeInformation.IsOSPlatform/OSDescription/OSArchitecture/ProcessArchitecture`、`Environment.OSVersion` → Stub 重定向文本断言 + 保守值；guard 子句 `if (OperatingSystem.IsWindows())` 走 false 分支形态；
- **guard 属性**：4 个 `SupportedOSPlatform*` 在 class/method/assembly 位置的擦除；`*.AssemblyInfo.cs` 分类；
- **互操作**：6 个 CS2J3201 属性、extern（CS2J3203）、`Marshal.*`/`NativeLibrary`/`SafeHandle`/`HandleRef`（CS2J3202）→ 仅 Reject/分组指引，**断言不生成任何桩/空体**；
- **三档动作**：同站点在 Report/Reject/Stub/Facade 四模式下的产物与计数；strict 开关；配置 allowlist/suppress/陈旧条目（PI060/061）；
- **白名单谓词**：`InteropBoundaryFacts` 对两种标记的识别与「无标记恒 false」；幂等（二次执行 Changed=false、站点归零/全登记）；生成树零编译错误。

### 7.2 项目级 fixture（建议 `tests/SamplePlatformIsolationSolution/`，Lib+TestProject+sln，net10.0）

仿 SampleRefOutInSolution 布局：平台服务包装类（Registry 读键 + SerialPort 枚举）、OS 分支工具类（guard 子句驱动行为）、`NativeMethods.cs`（一个白名单 extern 签名占位，不实际调用）、带 `[assembly: SupportedOSPlatform]` 工程、`*.AssemblyInfo.cs` 生成面。Stub 模式断言中间树 build/test 绿（被隔离路径的测试按保守值断言）；Facade 模式含一个用户手写端口实现示例。

补充用例（按风险面定向）：

- 复合 guard：`if (OperatingSystem.IsWindows() || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))` 重写后短路求值顺序与分支覆盖不变；
- `Environment.OSVersion`/`OSDescription` 字符串插值场景：桩返回 "Unknown" 后字符串可编译、测试断言不依赖真实 OS；
- 成员签名含平台类型：`void Write(RegistryKey key, string value)` → Stub 签名移植后调用点编译失败必须在 verify build 当场暴露（PI051），Facade 档端口接口成员不含平台类型；
- 纯平台包装文件（类内全部成员均命中）→ 文件级裁决；混合文件（仅一方法命中）→ 仅成员级动作、其余成员字节不动；
- `global using System.IO.Ports;` 与 using 别名 `using SP = System.IO.Ports.SerialPort;`：语义身份必须照抓（不依赖 using 文本）；
- 生成桩与既有成员重名（手写类已含 `Cs2jPlatformProbe` 同形符号）：fail-fast 报错而非覆盖，沿用适配器既有名字碰撞保护；
- 二次执行幂等：Stub 产物再跑 Report 站点为 0（generated 路径豁免）、再跑 Stub 无 diff。

### 7.3 Release build/test 与全量回归（实施者执行）

- `dotnet build -c Release` + 三工程 `dotnet test -c Release` 全绿（CSharpToJavaConverter.slnx）；
- **默认关零变化**：pass 关闭时中间目录集合、诊断集（含 NativeMethods.cs/AssemblyInfo 豁免与测试工程降级）、Java 产物与翻转前一致——既有 Phase2PassPipelineTests（10 处 3xxx 断言）与 PlanningTests（4 处 3xxx 引用）不改即过；
- PassChainOrderTests：默认关计划不含本 pass；启用计划含且位于 eliminate-goto 之后、java-keywords 之前；M3 加 unsafe-ffm 边后断言更新。

### 7.4 residual 闸门与 CLI

- Stub/Facade 后全量重扫 `.cs2j-platform-isolation-src`：三表语义身份零残留（generated/ 与 suppress 登记除外）；人为留一处未裁决 → PI031、文件不产出；
- CLI：`--enable-pass platform-isolation` 启停、四模式、strict、config、`--list-passes`、preserve 目录、Reject 退出码、独立 `preprocess --pass platform-isolation` 对原始源码出报告；CS2J9001 对该 id 消失；
- **extern 矛盾不回归**（引用 C06 §1.2 实测结论，不重复实验）：默认关时三路径（单文件 Sample.cs / 单文件 NativeMethods.cs / 项目管线 NativeMethods.cs）行为与 C06 实测逐条一致；启用后 NativeMethods.cs 必须在报告中显式列 CS2J3203 站点并在 Reject 档 PI030 前置失败——**不允许复活「文件名豁免流入 emit」**，单文件/项目两管线判定一致性依赖 C06 M0 的 FileName bug 修复（[ConversionPipeline.cs:148](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L148)），在该修复落地前 C42 单文件模式对 NativeMethods.cs 发 PI090 提示。

---

## 8. 默认开启验收门槛

| 模式 | 默认开建议 | 门槛 |
|---|---|---|
| Report（审计） | 可较早评估默认开（Warning 不阻断、字节不变） | ①三工程 Release build/test 全绿、翻转前后链字节一致；②csharp-sdk 样本补扫（先跑后填，不杜撰）：三类站点密度/family 分布/测试探针 Warning 噪声量；③额外整树复制耗时实测可接受（风险11）、指纹/增量缓存通过、报告字节稳定；④compat 前缀豁免与 AssemblyInfo/NativeMethods 分类经人工抽检无事实错误 |
| Reject | 语义上等价现状（转换器本就拒绝），翻默认开＝失败位置前移 | 除上述外：同语料上 PI030 集合与转换器 CS2J3xxx 集合逐站点核对一致（含豁免/降级差异白名单化）；建议不早于 C06 M0（注释/FileName 修正）合入 |
| Stub | **长期 opt-in** | SampleSolution Stub 模式 build/test 全绿；保守探针值在真实 guard 代码上行为评估完成（R2）；PNSE 桩触发面有测试覆盖；sdk 命中数据支撑 OS 探针优先序 |
| Facade | **长期 opt-in** | M3 与 C06 白名单联动经联合 fixture；用户实现槽位有完整示例与文档；端口签名移植人工复核流程就位 |

转换器侧 CheckPass 与 ClassTransformer fail-fast 不设退役时点（安全网永久保留，退役是默认开门后的独立评审）。

---

## 9. 里程碑

| 里程碑 | 内容 | 粗估 |
|---|---|---|
| M1 报告模式（零行为变化） | `InteropBoundaryFacts.cs` 签名冻结（恒 false 实现）+ Models/Options/Statistics + `BoundaryInventory`（直接调三 analyzer，复刻 compat/豁免/测试降级语义）+ PI001/060/061/070/090 + `platform-isolation-report.json` + adapter Report 模式 + 注册表翻转（Implemented、默认关）+ 单测分类矩阵 + CLI/拓扑/一致性测试 | Core ~500 行 + CLI ~250 行 |
| M2 Stub 生成（OS 探针优先） | `BoundaryTables` 抽表（三 analyzer 改薄封装，消息/码锁定）+ IsolationPlan + 探针重定向（8 身份）与 `Cs2jPlatformProbe.g.cs` + PNSE 成员桩（平台服务类型）+ guard 属性擦除 + 签名移植 D5 + residual 闸门 PI031/PI040 + Stub/Reject 模式 verify 双开 + SamplePlatformIsolationSolution + Release build/test + 幂等/对账测试 | Core ~600 行 + CLI ~150 行 + fixture |
| M3 Facade 半自动 + C06 联动 | FacadeGenerator（端口+partial 缝+用户槽位）+ RunAfter 加 unsafe-ffm（无环，§3.2）+ 谓词与 C06 R8 facade 标记互通、3201/3203 重复报抑制 + 互操作族报告分组接 UF060/UF070 契约 + 联合 fixture（NativeMethods 白名单/非白名单各一） | 独立排期，依赖 C06 M4 冻结契约 |

---

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | ★误隔离可移植 API | `RuntimeInformation.*` 存在 OS 中立用法（框架描述/进程架构判定并非总等于平台分支）；前缀表未来扩充会放大假阳；`IsOSPlatform` 结果可能本就跨平台 | 现状只列 8 个具体成员身份（非整个类型），M1 报告先给数据；假阳可经 allowlist 抑制且全程 Warning 不阻断；保守探针仅替换**调用点**不改写业务分支结构；扩表必须走 SemanticModel 身份匹配，禁止文本前缀新增 |
| R2 | 桩给人「已移植」假象 | PNSE 桩可编译、Java 有映射、测试不覆盖即绿；保守 false 探针静默改变 guard 行为 | 桩消息含原身份+出处；报告/PI010/PI011 强制台账；生成类名 `Cs2jPlatformProbe` 与 `generated/` 目录可审计；Facade 槽位不生成实现；默认开门槛含真实 guard 行为评估 |
| R3 | extern 空实现回潮 | 历史上注释曾错误声称「已发 stub」（风险16）；批量生成最易在此破戒 | D8 红线 + 单测断言互操作族零桩 + ClassTransformer:349 fail-fast 永久保留；白名单归一归 C06 |
| R4 | 文件名豁免双轨 | NativeMethods.cs 豁免只在项目管线生效、单文件 FileName bug（C06 §1.2）；C42 若沿用文件名裁决会复制双轨 | M1 复刻现状仅为保真；启用 C42 后裁决基于**站点+配置**而非文件名；一致性依赖 C06 M0 修复，落地前 PI090 明示 |
| R5 | 桩签名移植导致调用点编译失败 | 成员含 `RegistryKey` 参数，桩改 `string`/`IntPtr` 后调用点失配 | verify build 在中间树当场报错（PI051），不留 Java 期；D5 要求 Facade 优先处理签名面；Stub 档签名变更逐条入报告供人工修调用点 |
| R6 | 配置腐烂成新静默通道 | suppress 条目复制、span 漂移 | 键含 span+family+理由+owner；陈旧 PI061；配置随源码受控；strict 下 suppress 仍 Warning；对账不变式 |
| R7 | 与转换器 3xxx 双报 | Report 模式不改树，后续转换器 Check 仍报错（双重诊断） | 同 C25 R9 口径：Report 模式双层并存是预期（PI 是「站点台账」、3xxx 是「转换器拦截」），消息分工写明；apply 模式后树已无身份，CheckPass 自然不报；谓词抑制仅用于 C06 facade |
| R8 | 晚链位丢失出处 | 前序 pass 生成代码内的平台调用（如状态机/partial 合并）在晚位才可见——这是收益也是噪声 | 站点记录 SyntaxKind/文件路径，生成文件（tuple/E06 类产物）在报告单列 generated 来源；独立 verb 可对原始源码先跑审计 |
| R9 | 抽表 M2 改动热文件 | `UnsupportedDomainPasses.cs` 同时被 C06 M0 注释修正、C10 盘点关注 | §11 热文件协调：M1 零触碰；抽表单次单 owner 提交，落在 C06 M0 之后 |
| R10 | 单文件模式语义退化 | 无完整 compilation 时身份回退文本匹配（:441-449） | PI090 标低置信；Reject/Stub 在单文件模式仅对高置信语法面（属性/extern/UI using）生效，探针类跨编译身份不自动改写 |

---

## 11. 并行协调

| 相邻工作 | 边界 | 顺序/依赖 |
|---|---|---|
| **C06 unsafe-ffm** | 两面契约：C06 改指针/fixed/调用形态与 extern 白名单，C42 管边界隔离/拒绝策略与批量编排；共享 `InteropBoundaryFacts` 与两个标记 FQN（§4.5）；UF060 消息指引 C42，C42 互操作族报告引用 UF 码 | C06 §11：C06 先合默认关；C42 M1 不依赖 C06 代码（谓词文件自建、恒 false）；M3 加 RunAfter unsafe-ffm 并消费 R8 facade；白名单配置 json 格式结论待 C06 确认 |
| **C01 ref-out-in** | ABI 锁定谓词 `IsLocked`（[RefOutInAliasAnnotator.cs:265-327](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L265-L327)）已识别 DllImport/接口实现 | Facade/桩成员签名涉 ref/out 穿 ABI 时复用同一谓词，不复制；C01 在链前段，无 RunAfter 依赖 |
| **C10 attribute-normalize** | C10 对 6 个互操作属性与 4 个 OS guard 属性只盘点（AN003/AN-P），永不发射 CS2J3xxx；C10 的 inventory 作 C42 输入 | 属性名白名单互不重写（封送语义归 C06，擦除归 C42，其余归一归 C10）；生成标记属性 `Cs2j.Generation.*` 需 C10 列入 generated 透传类；联合 fixture 防双改 |
| **C20 partial-merge** | C20 在 Lexical 早段合并用户 partial；C42 在 Abi 晚段生成 partial 桩文件 | C42 产物作用于 C20 合并后树；桩部件须在转换器既有 partial 归一下无碰撞（MemberKey :301-310）；C20 翻转后加「C20 合并 → C42 桩拼接」联合幂等测试；无需新增边（C20 天然在前） |
| **C25 dynamic-desugar / C36** | 复用审计报告/manifest/residual 范式与「显式拒绝、禁止占位」基建；码位各自独立（PI/DS/CE） | 同锚 eliminate-goto，注册表插入顺序锁定 platform → concurrency → dynamic；若抽公共边界报告组件，三方改消费不重复建设 |
| **三 analyzer（转换器热文件）** | `UnsupportedDomainPasses.cs`：C06 M0 改注释、C42 M2 抽表、C10 只读消费 | 解法＝M1 C42 零触碰（internal 直接调用）；抽表排在 C06 M0 合入后由 C42 单 owner 一次性 PR；C10 永不改该文件；CheckPass 注册点（ConversionPipeline.cs:417-419、ProjectConversionPipeline.cs:269-271）全程不动 |
| **转换器内 PNSE 代偿** | Regex.CompileToAssembly 文件名硬编码改写（ProjectConversionPipeline.cs:205-254） | M2 后登记为 C42 可表达的既有代偿样本；其退役/外迁是独立 PR，不在 M1 触碰 |

**热文件清单**：[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、[.gitignore](file:///d:/code/cs2j/.gitignore)、PassChainOrderTests.cs（各翻转通道共享，按 rebase 串行解冲突）；M2 增 UnsupportedDomainPasses.cs（单 owner）；新增 `Analysis/InteropBoundaryFacts.cs`（C06/C10 将引用，需尽早冻结签名）。

---

## 12. Worktree 分配

只写不执行：本设计全程仅做只读调研与本文档写入，**未执行任何 git 写操作**（无 commit/add/worktree/checkout/branch，未运行 dotnet build/test）；以下命令在实施 PR 中由执行者运行。

- 分支：`feature/preproc-c42-platform-isolation`
- 目录：`.worktrees/preproc-c42-platform-isolation`（`.worktrees/` 已在 .gitignore 忽略）
- base：`953293b9`（`docs: update doc base commit`，已只读核验：`953293b908e9143437c45f287fb4277e890c398e`）

```powershell
git worktree add .worktrees/preproc-c42-platform-isolation -b feature/preproc-c42-platform-isolation 953293b9
```

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持 base `953293b9`，创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（散见决策表/里程碑门的悬置项，按主题收口）：**

1. **共享事实落点**：批准 §4.5——**`InteropBoundaryFacts.cs` 落 `src/CSharpToJava.Core/Analysis/`**（与 ExpressionTreeFacts/DynamicSiteFacts 同列），两个互操作标记 FQN 与白名单判定为 C06/C10/C42 单一事实源；C42 M1 自建谓词文件（恒 false 占位）但签名须在 M1 冻结，C06 R8 facade 落地后改消费不复制。
2. **链序**：**M1/M2 RunAfter 只锚 `eliminate-goto`**（同锚三通道注册表插入顺序锁定 platform → concurrency → dynamic，写进拓扑断言）；**M3 加 RunAfter `unsafe-ffm`** 并消费其 R8 facade，不在 M1 提前依赖 C06 代码。
3. **默认严重级**：通道默认 **Report**（显式报告/审计，不静默透传也不一刀切 Error）；隔离/拒绝策略升级按画像站点逐批走门槛，OS guard 不可判定即显式诊断，禁止占位与静默丢弃。
4. **CheckPass 安全网**：三个 CheckPass 注册点（ConversionPipeline/ProjectConversionPipeline）**永不因本通道删除或绕过**——本通道只加隔离与报告，不削弱既有编译基线闸门。
5. **extern 纪律**：**extern 方法永不发桩**（不生成空壳/throw 占位冒充实现）；无 Java 终态的 ABI 面只走显式诊断 + 白名单/标记体系指路，PNSE 等既有转换器代偿的退役/外仓是独立 PR，不在 M1 触碰。
6. **extern alias 分流（与 C28 对齐）**：真双程序集同名冲突的 extern alias 场景归本通道**显式拒转**；单程序集别名（`X::` 限缩）归 C28 M3 规则转写 `global::`，两家不重叠诊断。
