# Preprocessor 泛型能力升级方案（GLM-5.3）

> 版本：2026-11-04 **v3**（GLM-5.3 分析版；v3 **换源**：json 语料更换为 .NET Runtime 10.0.12 真实 System.Text.Json 源码并修正 v1/v2 的语料时代错位，新增 W-09/W-10 缺口与对应修复项；v2 刷新基线至 52 pass / json 0 失败并吸收四份同行方案实测增量，采纳/不采纳清单见附录 D）
> 基线：`D:\code\cs2j`，分支 `dev-202611`，HEAD `6b57a210`（**52 个 Implemented pass**，工作树干净）
> 语料证据（v3 口径）：
> - **json 主语料 [v3 更换]**：`D:\runtime-10.0.12\src\libraries\System.Text.Json`（.NET Runtime 10.0.12 **真实源码**；库本体 352 .cs = src 308 + gen 20 + Common 24，另有 tests 333 .cs；本版完成**静态普查**，全链运行统计待 M0 重跑——v2 及之前的全部 json 运行数据均在旧语料上测得，见 §3.2 换源说明）
> - json 旧语料（v1/v2 用，**时代错位已确认**）：`D:\csharp-sdk\json`——实为 .NET 3.x 时代 STJ 布局（`JsonClassInfo`/`ReflectionEmitMemberAccessor`，无 `JsonTypeInfo`），341 .cs；其全链日志（`D:\cs\20261104-json.log`、`20261104-json-final.log` 626/0）**测的是这份旧语料**
> - `D:\YamlDotNet-18.0.0`（18.0.0，261 .cs，**实测**）；`D:\cs\cs-sdk-78e810c6` 历史中间树与审计报告；`D:\cs\yaml-lib-probe.log` 等 6 份 YamlDotNet 运行日志
> 同行方案：`C:\Users\15097\Desktop\V202611042101` 目录下 deepseek-flash-4.1 / kimi3.1-pro / longcat2.5 / seed-evolving 四份同题文档（v2 吸收其经本仓代码/日志核证的论断，附录 D 全量对照；v3 的换源发现为本文档独立增量，四份均基于旧语料）
> **范围硬约束：本方案只升级 C# → C# 预处理链（preprocessor），一切改写产物仍是可编译、可测试的 C#；不修改 C# → Java 转换器，不生成 Java。Java 侧事实仅用于界定"降级目标子集"。**

---

## 0. 结论摘要

当前预处理链共 **52 个 pass**（全部 Implemented；`normalization-audit` 默认关闭，由 `--profile java-ready|audit` 追加）。v1 版文档时点的两个非泛型硬域（`Utf8JsonReader` ref-only ref struct、`JsonWriterHelper.Date.cs` 的 fixed+指针算术）**已被四个新 pass 攻克**：`ref-struct-span-field-memory-lift`（只读 Span 字段 → `ReadOnlyMemory<T>` 字段 + 同名 Span 属性 + `__cs2j_ToMemory` 写穿透，使 `Utf8JsonReader` 不再是 ref struct）、`fixed-span-to-array`（span-backed `fixed` → 托管数组 `fixed`，可写 Span 加 finally 回写）、`compat-method-replacement`（按全限定签名注册表把指针密集方法体替换为托管实现，内置 `JsonWriterHelper.ToUtf8`/`NeedsEscaping`）、`pointer-diff-inline`（同元素类型指针差值小助手内联 + 零引用声明清除）。json 模块（System.Text.Json）当前基线：Java 转换 626 类型成功 / 0 失败——**[v3] 该数字测自旧语料（.NET 3.x 时代布局，§3.2.1），rt10 真源基线待 M0 重跑建立**；且 rt10 上 `ToUtf8` compat 条目已确认零匹配（W-09）。

但"json 语料 0 失败"仍不等于"任意泛型密集库可过"，且 v3 确认了一个更根本的问题——**语料时代错位**：v1/v2 的 json 语料（`D:\csharp-sdk\json`）是 .NET 3.x 时代 STJ（`JsonClassInfo` 反射栈、无 `JsonTypeInfo`、无源生成器、无 Schema 导出），而"以 .NET 10 JSON 库为目标"的本方案实际要对齐的是 `D:\runtime-10.0.12` 的现代重写（普查结论见 §3.2：泛型密度 2-7 倍放大，且 compat 注册表已出现签名漂移 W-09）。v2 基于 json 全链运行统计（**旧语料实测，rt10 待 M0 重跑**）、YamlDotNet 18.0.0 **实测语料**（v1 仅为知识画像）与四份同行方案的交叉核证，把缺口认识从 v1 的"归约覆盖面"推进到三个更深层面（v3 增补 W-09/W-10）：

**层面 V：已落地机制存在正确性缺陷（活动 bug，修复优先于扩能力）**

| # | 缺陷 | 证据 |
|---|------|------|
| V-01 | GD-FIELD **singleton 形态** getter 每次调用重新执行初始化表达式（`=> InitializerText`），`static readonly` 的单例 identity 破坏；fixture 已把该行为固化为预期 | `Core/GenericPreprocess/GenericDeepenHolderGenerator.cs:54-55`；`tests/SampleGenericDeepenSolution/.../RuntimeBehaviorTests.cs:18-27` 自述 "guarantees per-closed-type construction, not singleton identity" |
| V-02 | 同一包含类型**多个有状态静态泛型字段** → holder 内 `__cs2j_gate/__cs2j_store` 按字段重复生成，生成代码不可编译 | `GenericDeepenHolderGenerator.cs:63-65`（AppendAccessors 每字段无条件发 gate/store，分组只到 holder 类型级） |
| V-03 | `GenericDeepenParamRef` record 的 `CompareTo` 只比 (kind, ordinal) 而 Equals/GetHashCode 含 Name → SortedSet 与 List 去重语义分裂（已有 `NamedParamRefComparer` 局部补丁证明缺陷真实发作过） | `GenericDeepenAnalyzer.cs:10-18, 284-293` |
| V-04 | GD 锚点物化把 abstract 成员改 `virtual` + `NotImplementedException`、为接口成员塞默认抛错体（公开 ABI 破坏） | `GenericDeepenRewriter.cs` MaterializeStubBody（~729-765，注释自称 "E10 precedent"） |
| V-05 | `ClosedTypeKey` 把 `NullableAnnotation` 以 `?` 后缀并入键：`List<string>` 与 `List<string?>` 成为两个运行时身份（NRT 非运行时类型信息，不应入键） | `Core/ClosedMetadata/ClosedTypeKey.cs` AppendNullableMarker |
| V-06 | closed-metadata collector 递归判型参（数组元素+类型实参）vs linker 只查顶层 `ITypeParameterSymbol` → `Deserialize<List<T>>` 与 `Deserialize<T>` 判定路径不一致 | `ClosedMetadataCollector.cs:580-599` vs linker `IsTypeParameter` |
| V-07 | `x as T`（T 类型参数）**两侧都不改写**：C# 侧 GD 只登记 token 站点（rewriter 只处理 IsPattern），Java 侧 as 路径无类型参数分支 | `GenericDeepenAnalyzer.cs:1953-1960`；`Transformers/Expression/Transformers/TypeOperationTransformer.cs` ~789 |
| V-08 | `default(T)`（值类型约束 T）Java 侧 `GetDefaultValueForType` 兜底渲染 `"null"`——值类型实例化时零值变 null（拆箱 NPE/语义错误）；variance-audit 已有判词但只进报告 | `TypeOperationTransformer.cs:966-993`；`Core/VarianceAudit/ConstraintAuditor.cs:354-356`（"definite semantic error"） |

**层面 S：合成层约束债——提升/合成族 pass 在 C#→C# 合成时丢约束/丢 arity（v1 多处"已接"判断不成立）**

| # | 缺陷 | 证据 |
|---|------|------|
| S-01 | `generic-deepen` 分析只遍历 `MethodDeclarationSyntax`：构造函数/属性/索引器/事件访问器/运算符体内的 `typeof(T)/is/as` **不分析不锚点** | `GenericDeepenAnalyzer.cs:1417-1449`（仅 MethodDeclaration+Invocation+TypeOf 三类遍历） |
| S-02 | 嵌套闭包 token 不可达：表达式体 lambda 委托形状不可解析 → CS2J1101 只告警且 pass 残差计数器仍报 0；STJ 实测 **37 处**（NumberHandlingTests/ConstructorTests 等） | `GenericDeepenAnalyzer.cs:2061-2071`；`D:\cs\20261104-json.log:521+`（"0 residuals" 与 37 条 1101 并存） |
| S-03 | closure-lift M2/M3 约束丢失三处：display class/holder 重声明 `TypeParameterList` 无 `WithConstraintClauses`；迁移方法 `typeParameterList: null` 硬编码 | `Core/ClosureLift/Lift/HolderSyntaxGenerator.cs:70-76`、`DisplayClass/DisplayClassSyntaxEmitter.cs:120-128`、`Lift/LambdaMethodLifter.cs:502-526` |
| S-04 | tuple 生成泛型 struct **零约束发射**（`Nullable<__P0>` 字段将 CS0453） | `Core/TupleToStruct/TupleStructFileContent.cs:48-60` |
| S-05 | local-function-lift 自有+外层类型参数 **arity 错位** → CS0308，无 pass 内回滚 | `Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs:624-662, 256-291` |
| S-06 | thread-static 对**泛型容器中的 `[ThreadStatic]` 字段显式停用**（"per-closed-type sharing cannot be expressed in v1"）；存在 `RewriteGenericContainingTypes` 选项但默认关 | `Core/ThreadStaticRewrite/ThreadStaticAnalyzer.cs` InGenericContainer 分支 |
| S-07 | foreach-deconstruction 开放组件（KVP/元组元素含类型参数）整站保留，并被 foreach-source **CS2J5420 硬门**放大 | `Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs:383-417`（强制 INamedTypeSymbol） |
| S-08 | async 泛型携带的类型参数**约束传递闭包**不完整（`where T : IFoo<U>` 漏带 U → CS0246） | `Core/AsyncNormalize/AsyncNormalizeAnalyzer.cs` ResolveGenericCarry 邻域（按名匹配无闭包） |

**层面 W：接线与审计缺口（机制已有但未闭环）**

| # | 缺口 | 证据 |
|---|------|------|
| W-01 | 约束**审计码体系已存在**（VA020-027 + struct/new()/unmanaged 依赖位点计数 + 判词），但未接 java-ready 门禁；`PassChainFactory` 构造 `GenericDeepenOptions/ClosedMetadataOptions` 时不传 `config.Strict` → **java-ready ≠ strict** | `Core/VarianceAudit/VarianceAuditDiagnostic.cs:29-36`、`ConstraintAuditor.cs:354-356`；`CLI/Preprocessing/PassChainFactory.cs`（对照 `NumericBoundaryOptions` 接收 strict） |
| W-02 | 现有 10 个 NORM-\* 探针**全部非泛型域**；closed-metadata 实测 **281 residuals / 369 dynamic skipped / 0 consumer sites linked** 仍链成功（CS2J1220 Warning 放行） | `Core/Normalization/ResidualRegistry.cs:11-23`；`D:\cs\20261104-json.log:645` |
| W-03 | 诊断码撞号：**CS2J1203 一码三义**（closed-metadata "JavaTargetUndecidable" / extension-methods "ExternalHostNoMapping"+"ExternalMetadata" / thread-static "StructMutationNotLiftable"）；另 extension-methods 的 12xx 段与 closed-metadata 撞段、linq-channel 的 13xx 与 attribute-normalize 撞段 | `ClosedMetadataDiagnostics.cs:55`、`ExtensionMethodResolveDiagnostics.cs:75`、`ThreadStaticDiagnostics.cs:49` |
| W-04 | `linq-channel` 的真实引用增强路径**已实现但调用点被一行注释**：`// EnsureEnhancedReferences(tree); // NEGATIVE-CHECK (temporary)` | `CLI/Preprocessing/Adapters/LinqChannelPassAdapter.cs:105` |
| W-05 | closed-metadata 消费面**硬编码 STJ**（4 个 Serialize/Deserialize 方法名 + `GetTypeInfo` 共 5 入口），`ConsumerSurfaces` 选项未实现——YamlDotNet 序列化入口不在枚举面 | `ClosedMetadataCollector.cs:42, 279-288` |
| W-06 | **YamlDotNet 库默认链在 `null-coalesce` 中止**：`?? []`（`TypeConverterCache.cs:39` 的集合表达式）无法处理 → 链失败，而能处理 `[]` 的 `expression-desugar` 排其后 → **泛型 pass 在默认链上从未真正执行**（单 pass 复跑才见 2 `new T()` / 2 `new T[n]` / 100 anchors） | `D:\cs\yaml-lib-probe.log`（"1 residual ??/??= ... Error"）；`D:\cs\yaml-generic-only.log`、`yaml-gd-only.log` |
| W-07 | Java 侧闭型注册**已接线**（v1 文档"未接线"论断过时）：`TypeHelper.registerClosedType/resolveClosedType/closedCacheKey` + `ClosedTypeMetadataTest`（幂等/并发/null）；但 `ClosedMetadataRegistrationGenerator.cs:9-11` 注释仍写 "lands in a separate PR" 未更新 | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java:1255,1270`；`ClosedTypeMetadataTest.java` |
| W-08 | 语义 pass 默认 **TPA-only 引用集**：泛型绑定假阴性（STJ 实测 `ConvertedType=null` → 37 CS2J1101 的根因之一；variance-audit 跳过 **126 个** baseline-error 文件）；仅 extension-methods/async-normalize 自行补包引用，linq-channel 差一行注释（W-04） | `CLI/ProjectUnsignedTypePreprocessor.cs:81`；`D:\cs\20261104-json.log:652` |
| W-09 | **compat 注册表签名漂移** [v3 新增]：内置条目 `JsonWriterHelper.ToUtf8` 的键/参数表拟合的是旧语料（3.x 时代 `JsonWriterHelper.Transcoding.cs:28` 的 `ToUtf8(ReadOnlySpan<byte>, Span<byte>, out int, out int)` 4 参形态）；Runtime 10 真签名是 `JsonWriterHelper.cs:291` 的 `ToUtf8(ReadOnlySpan<char>, Span<byte>, out int written)` **3 参 char-span**——条目在真实语料上**零匹配静默空转**（`NeedsEscaping` 键则仍匹配）。注册表无"声明类型在语料中存在但零匹配"的漂移告警 | `Core/CompatMethodReplacement/CompatMethodReplacementRegistry.cs:306-330`（4 参 byte-span 键）vs `D:\runtime-10.0.12\...\Writer\JsonWriterHelper.cs:291` |
| W-10 | **csproj 门控 Compile 与外部共享源未进入预处理输入模型** [v3 新增]：Runtime 10 STJ 用**文件级** TFM 门控（`<ItemGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETCoreApp'">` 收 `JsonReaderHelper.net8.cs`，非 CoreApp 组收 `netstandard.cs`——同一 `partial class JsonReaderHelper` 的两套实现）与 **csproj 外部共享源**（`$(CoreLibSharedDir)`：FeatureSwitchDefinitionAttribute/ValueListBuilder；`$(CommonPath)`：HexConverter/ArrayBuffer/ValueStringBuilder/PooledByteBufferWriter）；gen/ 还有 `DefineConstants=ROSLYN4_x_OR_GREATER`、`.targets` import、analyzer 型 ProjectReference。目录 glob 输入必然产出**重复成员/缺源中间树**；L1 的 `MsBuildProject` Compile 求值从"可选上移"升格为 **rt10 语料硬前置**（`.slnx` 解析已支持，`ProjectPreprocessSource.cs:32,74`） | `D:\runtime-10.0.12\...\src\System.Text.Json.csproj:384,394-399,435`；gen\System.Text.Json.SourceGeneration.Roslyn4.4.csproj |

**核心判断**：v1 的"泛型归约零审计"判断仍然成立（10 探针全非泛型），但 v2 新增两个更紧迫的事实——(a) 已落地的泛型补偿机制（GD-FIELD/CM）自带正确性缺陷，json 语料恰好未触发（单字段、无 `List<string?>` 键差、泛型测试方法少）；(b) **合成层**（closure/tuple/local-function/async 的提升与生成）在 C#→C# 阶段丢约束/丢 arity，错误在中间树就爆发（CS0246/CS0308/CS0453），不再等到 Java。升级方案五层（继承 v1 框架，重排优先级——**先修正确性，再扩能力**）：

```text
L0 契约层   NORM-GENERIC-* 探针族扩容（合并四方案探针分野，~17 码）+ CS2J14xx 新诊断段 + 撞号迁移
            + PassContract 推广（9 字段已落地、10 pass 已挂载；补 VerificationProfile 字段与派生断言）
L1 编译层   真实引用集 spine 化（含打开 W-04 一行注释）+ degraded 显式化 + 引用画像报告
            + [v3] csproj Compile/TFM/外部源求值进输入模型（W-10/G-26，rt10 硬前置）
L2 验证层   四路差分验收（A 原库原测 / B 预处理库+原测 / C 双预处理 / D 原库+预处理测）
            + 残差 ratchet（只许减不许增）+ java-ready 严格化（W-01 strict 接线/不可绕过/幂等逐字节）
L3 能力层   V 层缺陷修复（GD-FIELD 规范形/CM 键修/闭包补锚）+ S 层合成债统一解析器
            + v1 的 P1-R1~R8 / P2-R1~R3 规则族 + 消费面配置化（ClosedTypeConsumerSurfaces.json）
            + [v3] compat 注册表漂移探测与条目版本锚（W-09/G-25）
L4 性能层   统一中间树默认化 + 增量缓存 + 选择性 verify（M5 未竟，泛型整树 pass 收益最大）
```

验收目标（详见 §7）：**[v3] `D:\runtime-10.0.12` 的 System.Text.Json 全模块（src + gen + Common，rt10 真源）**与 `YamlDotNet 18.0.0` 经本链 `--profile java-ready` 预处理后：

1. `preprocess-residual-report.json` 中泛型类 `NORM-GENERIC-*` 计数为 0（或全部 `DeclaredUnsupported` 且带 owner/文件/行/原因）；
2. **四路差分中 B 路**（预处理后的库 + 原测试工程）`dotnet build`/`dotnet test` 零失败——"预处理语义等价"的最强可执行代理指标；
3. （下游口径，超出本方案实施范围）Java 转换产物 `mvn compile` 通过、JUnit 全绿。

---

## 1. 文档定位与阅读路径

- 本文件是 `docs/preprocessor-upgrade-plan.md`（2026-11-01 版，下称"v1 方案"）的**泛型专题续篇**；本文档自身的 2026-11-04 首版称"v1 版"，v2 刷新版后本次版本称 **v3**。v3 相对 v2 的变更：**json 主语料更换为 `D:\runtime-10.0.12` Runtime 10 真源**（旧语料确认为 .NET 3.x 时代 STJ，v2 全部 json 运行数据降级为旧语料历史基线，§3.2.1）；静态普查按真源重测（§3.2.2，泛型密度 2-7× 放大）；新增 **W-09**（compat 注册表签名漂移，ToUtf8 条目在 rt10 零匹配）与 **W-10**（csproj 门控 Compile/外部共享源 → L1 硬前置）两项缺口及修复项；§5 增补 G-25/G-26；验收 json 口径切换至 rt10（§7.1）。v2 相对首版的变更：基线 48→52 pass、json 2 失败→0 失败、新增 V/S/W 三层缺口体系（替代单一的 G 矩阵结论）、YamlDotNet 推测画像→实测、吸收四份同行方案经核证增量（附录 D 全量对照）。
- v1 方案的 §5.3 Pass Contract v2、§5.9 性能层、M5 波次继承不变；其"42 pass"表述按 52 刷新。
- §2 现状全景；§3 需求模型+双语料实证；§4 逐 pass（52 个，含泛型交互修正）；§5 缺口矩阵（G-01~G-15 修正 + G-16~G-24 新增）；§6 升级设计；§7 验收；§8 波次与风险；附录 A 诊断码 / B 证据 / C 边界 / D 同行方案对照。
- 代码引用以 HEAD `6b57a210` 为准；`src/CSharpToJava.Core/` 省写为 `Core/`，`src/CSharpToJava.CLI/` 省写为 `CLI/`，`java/csharptojava-compat/src/main/java/io/github/ningpp/compat/` 省写为 `compat/`。

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
- **`--profile` 已提交**：`convert-project` 与 `preprocess` 共用 `TryNormalizeProfile`（`CLI/Program.cs:4046`），java-ready/audit 自动追加 normalization-audit（显式 `--disable-pass` 仍可排除，见 §7.3 严格化要求）。
- **残留报告覆盖率计数已落地**（commit `deda3b62`）：`ResidualReportSummary.ByCode/ByOwner`（coverage matrix 最小形态）。
- **每 pass 执行报告** `preprocess-report.json`（commit `f162d07b`）继续存在；两份报告尚无互链与统计明细（G-AUDIT 遗留）。

### 2.3 契约与审计现状

- `ResidualRegistry` 10 个 descriptor 全 `Fail`：`NORM-ENUM-ARITH / STACKALLOC / REF-STRUCT / REF-LOCAL / ASYNC-LAMBDA / TUPLE-LOCAL / POINTER-OP / POINTER-LOCAL / UNSAFE-BLOCK / FIXED`（`Core/Normalization/ResidualRegistry.cs:11-23`）。**泛型域 0 探针**。
- `generic-deepen` 有 pass 内残留门（五形状：static generic field、catch-type-param、type-param-pattern、裸 `typeof(T)`、`is/as T`；`GenericDeepenResidualScanner.cs` 8 条规则），但只进 `CS2J1120` 诊断；且 **java-ready 不自动 strict**（W-01）。
- `variance-audit-report.json` 有 `--variance-audit-escalate` 链内升级开关，但无代码消费该 JSON 做降级决策；**约束审计 VA020-027 已存在**（含依赖位点统计与"definite semantic error"判词）但同样未接门禁。
- 审计证据链：`.cs2j-java-keywords-audit-baseline`（170 残留/7 类）→ `.cs2j-stackalloc-lowering-audit`（97/6 类）→ **当前 json 全链 0 Java 转换失败**。

### 2.4 Java 转换器对泛型的现有承接（目标子集事实清单，v2 修正）

> 事实清单，用于界定预处理降级目标；不是本方案改动范围。v2 修正处以 **[v2]** 标注。

**硬门禁**（`Transformers/NormalizationRequired.cs:13` 统一构造点；全链 throw 点实测 **42 个** [v2 修正：v1 首版误记 45]，泛型直接相关 2 个）：

1. 静态字段引用外围类型参数：`Transformers/Member/FieldTransformer.cs:119`（GD-FIELD 先行消除；注意 **静态泛型属性有 Java 侧提升** `PropertyTransformer.cs:64/83` 调 `PromoteReferencedEnclosingTypeParameters`，deepseek 文档"静态泛型属性无载体"论断**不成立**）。
2. `catch (T e)`：`StatementTransformer.SwitchAndResource.cs:327`（GD-CATCH 先行）。
3. （构造维度间接命中）泛型枚举/值类型上的非等值二元运算、用户定义一元/增减运算符。

**约束处理**（`ClassTransformer.ApplyTypeParameterConstraints:347`）：类型约束→Java bounds（`ICollection<T>` 专门重映射）；`class` 静默丢弃；`struct`/`new()` 丢弃+CS2J1002；`notnull/default/unmanaged` 静默丢弃。

**型变**：声明处 `out/in` 丢弃+CS2J1003（接口 `InterfaceTransformer.cs:101`/委托/类三处）；使用点协变由 `ArgumentTransformer.Coercion.cs:930` `TryBuildIterableBridge` + `IterableCoercion.cs:130`（五挂接位）+ `TypeOperationTransformer.cs:129/751`（cast/as 适配）桥接。

**静态成员**：静态方法/运算符引用外围类型参数 → 自动提升（`StaticMemberTypeParameterHelper.cs:24`，含 bound 闭包）；静态属性 getter/setter 同样提升 [v2 修正]；静态构造器 → static 块。无每实例化静态补偿（由 GD-FIELD holder 在预处理侧承担，见 V-01/02 缺陷）。**[v2]** 泛型类型上的静态成员访问已修复 raw receiver 渲染（`ReadOnlySpan<byte>.Empty` → `ReadOnlySpan.getEmpty()`，commit `5879d22e`，`IdentifierExpressionTransformer.cs` StripTrailingGenericArguments）。

**typeof/default/new**：`typeof(T)` 优先读 `__cs2j_token_T` 锚点（`TypeOperationTransformer.cs:934`），缺失退化为 bound class literal/`Object.class`；`typeof(X<>)` → 擦除宿主 class literal（`Nullable<>`→`Optional.class`）；`default(T)` 按符号分派零值——**类型参数兜底渲染 `"null"`**（`:966-993`，值类型实例化语义错误，V-08）；`new T()` 产物 → compat `Activator.createInstance()`（注意 C# 语义坑：`Activator.CreateInstance<T>()` 对带自定义无参构造器的 struct 会调构造器，与 `default(T)` 零值**不等价**——设计 default 类降级时不得经 Activator）；`new T[n]` 产物 → `(T[])(CSharpArray.tryAs(...))` + Java 后处理 `GenericArrayCreationRewriter`。

**泛型反射**：`MakeGenericType/GetGenericArguments/GetGenericTypeDefinition/MakeGenericMethodParameter/GetUnderlyingType` 有 methodMapping → compat `TypeHelper`；**`TypeHelper.registerClosedType/resolveClosedType/closedCacheKey` 已接线并有测试** [v2 修正：v1 首版"未接线"过时]；`GenericParameterAttributes` 无映射。

**映射面**（`config/TypeMappings.json` 964 条）：v1 首版结论不变；**`EqualityComparer<T>`/`Comparer<T>` 两静态类仍无映射条目** [v2 复核确认]（compat 有 `CSharpDefaultEqualityComparerGeneric/CSharpDefaultComparerGeneric` 实现类——映射面断裂，G-11 仍成立）。

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
- 泛型交互：属性参数 `typeof(X<>)` 取泛型定义裸类名（`KnownAttributeCatalog.cs:441`）。缺口 G-08：泛型 attribute 降级归 P1-R3（expression-desugar 0587 只守门不降级）。**[v2]** 审计只挂 TPA 引用（AN004 NuGet 黑洞）且盘点不落 JSON——归 L1/报告 v1.1。

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
- 泛型交互缺口（G-01）：`new T(args)` 不改写；`sizeof(T)` 无处理；**[v2 V-08 关联]** `default` 字面量落到 Java 侧对类型参数渲染 `"null"`（值类型实例化语义错误）。设计注意（CN-01 附注）：default 类降级**不得经 Activator**（带自定义无参构造器的 struct 语义坑）。

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


## 5. 缺口矩阵（G-01~G-15 修正 + G-16~G-24 新增）

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
| G-11 | `EqualityComparer<T>.Default`/`Comparer<T>.Default` | 无；映射断裂（v2 复核仍无条目） | 不变（P1-R4） | T2 |
| G-12a | 泛型 unsafe 内在函数 | unsafe-ref-lowering 3 API | 不变；补 `Unsafe.As/BitCast/Marshal.*` 家族（GI 规则族，§6.4） | T2 |
| G-12b/c | 泛型指针 `T*`/泛型 stackalloc | UF021/UF040 拒绝 | 域决策（不变；补"零单测"事实） | T3 |
| G-13 | 泛型状态机/闭包/迭代器 | 三 pass 按需泛型 | ← 修正："已系统性承接"→"**机制系统但 M2/M3/S-03/S-08 有约束债 + S-02 闭包不可达**" | T1/T2 |
| G-14 | 泛型推断差异 | 无专项 | 不变；补 41 未映射外部宿主为新观测面 | T2 |
| G-15 | 同擦除签名冲突 | Java 侧 `_erasure_N`/`_Ntp` | 基本已接；← 补：**CS2J5196"deferred to C02 review"无承接者**、RR070 泛型接口 ripple、CS2J5223 开放泛型 with 接收者三形态无 owner（record/member-desugar 泛型域，v1 矩阵未覆盖） | T3 |
| G-16 | **合成层约束债族**（S-03/04/05/08：closure/tuple/local-function/async 丢约束丢 arity） | 无（各 pass 内散落） | **[S]** 统一解析器缺失；错误在中间树爆发 | T1 |
| G-17 | **GD 分析作用域盲区**（S-01：ctor/accessor/operator 体） | generic-deepen | **[S]** Java 侧已支持这些作用域，preprocess 单方面补齐即可 | T2 |
| G-18 | **链鲁棒性**（W-06：Yaml 默认链于 null-coalesce 中止；W-04：linq 引用增强一行之差；单 pass 顺序依赖崩溃） | 无（框架级） | **[D]** 顺序性失败策略：后续 pass 能处理的构造不应阻断链 | T1 |
| G-19 | **诊断码撞号**（W-03：CS2J1203 一码三义 + 12xx/13xx 撞段） | 无 | **[D]** 影响报告/契约一致性测试的可归因性 | T4 |
| G-20 | **ED020 保真面过宽**（枚举深泛型实参可传递致整 enum 保真） | enum-declaration | **[S]** 压低转换率而非错误 | T4 |
| G-21 | **params 集合谱系**（`ReadOnlySpan<char>`/ref struct 元素不支持；`[CollectionBuilder]` 恒 0607） | strip-default C48 | **[L]** 泛型库 spread/params 交叉面 | T3 |
| G-22 | **NRT 运行时元数据保留契约**（`NullableAttribute` flags 被 Yaml 运行时反射） | 无 | **[K]** 键剥离正确（V-05 修法）但成员元数据需保留/重建 | T2 |
| G-23 | **Expression 树域**（Yaml：执行面+元数据面；三 pass 策略分散） | linq-channel 冻结 | **[v1 P2-R2 + K 元数据面]** 域决策 | T3 |
| G-24 | **java-ready 严格性**（W-01/W-02：strict 不挂钩、泛型 0 探针、CM 残留 Warning 放行、必需 pass 可被 `--disable-pass` 绕过） | 框架 | **[K]** java-ready 的语义完备性 | T2 |
| G-25 | **compat 注册表签名漂移**（W-09：ToUtf8 条目拟合 3.x 旧签名，rt10 真签名 3 参 char-span → 零匹配静默空转；无"类型存在但零匹配"漂移告警） | compat-method-replacement | **[v3]** 条目式机制的固有风险：库升级即失效；须配漂移探测 + 条目带语料版本锚 | T2 |
| G-26 | **csproj 门控 Compile/外部共享源输入模型缺口**（W-10：文件级 TFM Condition、`$(CoreLibSharedDir)`/`$(CommonPath)` 外部源、gen/ DefineConstants 与 analyzer 型 ProjectReference——目录 glob 必产出重复成员/缺源树） | 框架（L1） | **[v3]** rt10 语料硬前置：`MsBuildProject` Compile 求值必须先于语义 pass（否则泛型绑定的"树"本身就不是目标 TFM 的树） | T1 |
| G-27 | **rt10 新语法面批量首触**（`field` 关键字含 `ref field` 实参与静态属性形态；ref struct 上主构造；using 别名指向 ValueTuple 形状；公共 `params ReadOnlySpan<>` API；`readonly` 实例成员修饰） | member-desugar/tuple/strip-default 各自路径 | **[v3]** 机制大多已有（FieldKeywordBodyRewriter 含表达式体/K2 params 集合），但组合形态（field×静态单例×ref 实参、主构造×ref struct×Span 字段、别名×tuple 形状）无 fixture；S 层统一解析器与 M0 fixture 补齐 | T2 |

矩阵结论（v3，在 v2 三条上增补第四条）：

1. **T2 仍是主威胁**，且 v2 新增了 T1 级别的合成层缺口（G-09/G-13/G-16/G-18）——错误提前到 C# 中间树/链执行阶段爆发。
2. **"已接"需要重新定义**：v1 的"已接"= 机制存在；v2 起要求"机制存在 + 无正确性缺陷 + 残差可对账"（G-02a/G-03/G-09/G-13 四行的修正是本次最重要的认识更新）。
3. json 0 失败的成因是**语料运气**（单字段 holder、无 NRT 键差、约束全引用语义、泛型测试方法未踩 1101 行为面）；YamlDotNet 已实测踩雷（W-06 链中止）。
4. **[v3] 语料真实性是第 0 类缺口**：v2 的运气成分比想象的更大——json 语料本身是 3.x 时代布局（G-25 的签名漂移、V-05 键分裂面 63 vs 452、`new()` 约束 1 vs 18 全部因此被低估）。任何"已接"结论在语料换代（3.x→10）后必须以真源重验，本条同时是附录 C"不为单一语料写死特判"的反向纪律：**也不允许单一旧语料固化机制**（compat 条目按旧签名拟合即此病）。

---

## 6. 升级设计

### 6.1 目标不变量

v1 首版 I1-I6 全部继承（I1 每中间树可编译 / I2 可测试 / I3 无未登记泛型构造 / I4 确定性幂等逐字节 / I5 泛型语义等价可审计 / I6 推断锚点化），v2 增补：

- **I7 补偿正确性**：GD-FIELD/CM 等既有补偿机制的输出必须满足规范形（per-closed-type 恰一份、readonly 至多初始化一次、声明类型 cctor 先于首读、异常按 CLR 模型缓存重抛、生成成员不重复）——通过 §6.4-R0 的 `StaticGenericStatePlan` 与行为反例测试锁定。
- **I8 合成保真**：任何提升/合成族 pass 产出的 C# 必须携带源约束与 arity（统一解析器出口校验），CS0246/CS0308/CS0453 类中间树错误归零。

### 6.2 五层总览与优先级重排

```text
M0 止血（先于一切扩能力）：V-01..V-08 缺陷修复 + S-03/04/05 最小修复 + W-01/W-04 接线 + W-03 撞号迁移
                          + [v3] W-09 compat 漂移修复 + W-10 rt10 基线重跑与 Compile 求值最小接线
M1 契约与审计：L0 探针族 + PassContract 泛型族挂载 + 报告 v1.1（互链/统计/引用画像）
M2 编译层：L1 真实引用 spine + degraded 显式化（W-10 完备化在此收口）
M3 能力层：S 层统一解析器 + v1 P1-R1~R8 + 消费面配置化 + G-02c/R2 扩面
M4 域决策：static abstract / 运行时合成 / Expression / 型变域（DeclaredUnsupported 或立项）
M5 性能：统一树默认 + 增量 + 选择性 verify + 文档自动生成（descriptor+contract → preprocessor-matrix.md）
```

### 6.3 L0 契约层：探针族、诊断段、撞号迁移

**探针族扩容**（合并 v1 首版 9 码与 seed 方案 12 码，两套几乎互补；统一命名 `NORM-GENERIC-*`，除注明的沿用 v1）：

| Code | 扫描形状 | Owner | 初期 Policy | 来源 |
|------|---------|-------|------------|------|
| NORM-GENERIC-TOKEN-SITE | **可执行作用域**（方法/ctor/accessor/operator/lambda/局部函数）内 `typeof/is/as` 引用可见类型参数但作用域内无 `__cs2j_token_*` 锚点 | generic-deepen | Fail | S（作用域式检测，比特征名式更接近擦除本质） |
| NORM-GENERIC-CONSTRAINT | `struct/new()/unmanaged/notnull/T:U` 约束子句 + 依赖位点 | 新 pass generic-constraint（§6.4-R2） | PreserveAndWarn | v1 |
| NORM-GENERIC-STATIC-ABSTRACT | 接口 static abstract/virtual 成员 | expression-desugar（守门转登记） | Fail | v1 |
| NORM-GENERIC-ATTRIBUTE | 泛型 attribute 声明/应用 | expression-desugar 0587 | Fail（声明） | v1 |
| NORM-GENERIC-VARIANCE-DECL | `out/in` 声明（复用 VA001） | variance-audit | PreserveAndWarn | v1 |
| NORM-GENERIC-REFLECT | `MakeGeneric*/GetGeneric*/GetGenericTypeDefinition` 调用点（含 `typeof(T)`/`typeof(X<>)` 接收者链） | closed-metadata | PreserveAndWarn | v1+D |
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

配套纪律（吸收四方案）：

1. **每码正/负 fixture 成对**（触发/不触发）；`PassContractConsistencyTests` 的计数断言改为**对常量集的派生断言**（每加码不改数）。
2. **degraded 探针口径**：纯语法可判的探针不依赖绑定；需符号的探针在绑定失败时标 `degraded`、**不得计为 0 残差**（防 L1 未覆盖时假阴性）。
3. **CS2J14xx 新诊断段**（1401 token/1402 static/1403 值语义/1404 型变/1405 闭型/1406 反射/1407 内在函数/1408 桥接/1409 不支持域）；**撞号迁移**：CS2J1203 保留给 closed-metadata，extension-methods 迁 19xx、thread-static 迁 16xx，报告留 `legacyCode` 一个版本；12xx/13xx 撞段一并消歧。
4. **`SYNTH-*` 内部构造命名空间**（SYNTH-token-anchor/SYNTH-generic-holder/SYNTH-ctor-class-token）：生成物只进 Produces/跨 pass 校验，不进 NORM 审计。
5. PassContract 三步推广：M1 挂泛型族四 pass + 残差相关的 local-function-lift/ref-out-in/foreach-deconstruction/linq-channel；M2 补产物类 `Produces`（跨 pass 顺序依赖可机检，如 tuple→foreach-deconstruction 的 5420 关系变 Precondition）；M3 全量 + 注册期四校验（MayLeave owner 在链中且在其后、每 NORM 码恰一 owner、Postconditions 构造 ID 存在、Idempotent=true 必有幂等测试）+ `VerificationProfile` 字段补齐。

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
- **fixed-span-to-array 边界加固**（kimi P0-9）：`IsWritableBodySafe` 的 symbol-null 放行改保守拒绝；ReadOnlySpan 指针写/别名观察补差分测试。

**R1 合成层统一解析器 `SyntheticTypeParameterResolver`（修 G-16/S 层，seed 方案核心设计）**

新增 `Core/Analysis/SyntheticTypeParameterResolver.cs`，输入被提升成员的符号/宿主/体内+签名+约束子句节点集，输出五件套：①应携类型参数有序集（语义归属 + **约束传递闭包**，`where T : IFoo<U>` 必携 U——修 S-08）；②宿主作用域内形参不重声明；③每形参"语法+约束"或"无法安全表达"的**显式三态**（禁止默默丢弃——修 S-03）；④调用点一律符号渲染 + 默认显式实参（消灭 CS0411）；⑤同名遮蔽按 ordinal 消歧 + 稳定重命名（修 S-05）。消费方迁移顺序：closure-lift M2/M3 → async → local-function-lift → tuple（S-04 随带修：tuple 不可表达约束时保留原生 `ValueTuple` 而非产出不可编译代码）→ anonymous-type-lift。**止血序**：解析器落地前，先把算不清的站点改保守 skip + 可对账诊断。

**R2 v1 首版 P1-R1~R8 全部保留**（修订三处）：

- P1-R1 `generic-constraint`：新增第四条规则——**`Enum.Parse<T>/GetValues<T>/GetNames<T>/TryParse<T>` → 非泛型重载**（CN-09）与 **`Delegate.Combine<T>/Remove<T>` → CsPrelude 非泛型**（CN-10）；并补设计注意（CN-01/CN-04 附注）：default 类降级**不得经 Activator**（struct 自定义构造器语义坑）；`T?` 显式化必须连带 `.Value`/构造改写（TypeMappings 的 Nullable→Optional 与解包路径混用风险）。
- P1-R5 `make-generic-static`：升级为 **RG-0~4 反射分级**框架的 RG-0 层（吸收 kimi L4）：RG-0 静态闭合→闭型工厂（P1-R5 原案）；RG-1 token 可传播→tokenized invoker；RG-2 运行时开放→统一注册工厂（CM-DYN 收口）；RG-3 不可判定→readiness 失败或人工 provider；RG-4 表达式/延迟→移到实际执行边界。配套 `RuntimeTypeQueryPlan`（IsAssignableFrom/接口映射/查询顺序）与 `MemberMetadataPlan`（BindingFlags/发现顺序/Invoke 差分）。
- P1-R6 pattern-desugar：补 C37 组件注册与 list pattern reducer（v1 已有）；relational-CompareTo 物化规则不变。

**R3 消费面配置化（修 W-05/G-04d，吸收 seed/deepseek）**

`config/ClosedTypeConsumerSurfaces.json`：条目 `{assembly, type, methods[], genericArgPolicy, requiresGuard}`；Core 只实现"按符号匹配配置条目"，**库名只出现在 JSON**（M3 验收含 grep 守门测试：`Core/` 中不得出现 `YamlDotNet`/`JsonSerializer` 字样——后者从 `ClosedMetadataCollector.cs:42` 的硬编码常量迁出）。内置两组描述（STJ/YamlDotNet）+ `GenericReflectionProfile`（MakeGeneric 实参枚举，CM-ENUM-5；GetGenericArguments 只登记读取面不注册防无界膨胀，CM-ENUM-6）。

**R4 v1 首版 P2-R1~R3 保留**（variance use-site 锚点 opt-in + 安全五条件与嵌套 VR-03 递归规则、Expression 域（增补 Yaml 元数据面维度）、extension 推断实参物化）。

**R5 晚期链重排（决策点，kimi L6 提案——本方案列为决策而非直接采纳）**

现状：closure-lift/iterator/async/member-desugar/linq 在 generic-deepen **之后**新生成的泛型代码不会再进泛型分析（token/holder 需求可能遗漏）。kimi 提案把泛型分析移到最终形状（`...结构改写... → generic-contract-normalize → generic-deepen(最终 token 流) → generic-reflection-normalize → closed-metadata → variance-audit(最终复核) → java-keywords → normalization-audit`）。**权衡**：重排解决"晚生成泛型代码"类遗漏，但动 StateMachine/Abi 边界风险大（37×1101 的另一解法是把 closure-lift 提前，见 deepseek Q5）。本方案 stance：M3 先以统一解析器 + 依赖方向标注（Contract `Produces`）缓解；重排作为 M4 决策点，需先量化"晚生成泛型代码"的实际残差规模。

**R6 CLI 旋钮补齐**（修 G-24 相关）：`--generic-deepen-overrides/--generic-deepen-strict/--closed-metadata-strict` 暴露既有 options；`RunAfterIds` 增"启用依赖"语义（`--pass generic-deepen` 自动补跑 generic，或至少硬报错替代静默空跑——closed-metadata 已有 CS2J1205 先例）。

### 6.5 顺序与契约调整

新 pass / 规则插入点（v1 首版不变 + 增量）：

```text
unsigned → generic → generic-constraint (新, R2)
                       ↓
            thread-static → generic-deepen → closed-metadata (+R3 消费面配置化)
                                              ↓
                                   variance-audit (+P2-R1 opt-in 二轮)
...（既有 52 pass 顺序不变；M0 的缺陷修复全部原地，不动顺序）
java-keywords → normalization-audit (+§6.3 十八探针族)
```

### 6.6 L1/L2/L4 层

**L1 编译层**：真实引用集 spine 化——一次 restore 全 pass 共享；对现存三个 `GetMetadataReferences` override（extension/async/linq）收敛为 spine 追加项而非平行实现；`degraded` 显式化（java-ready 拒绝 degraded 编译）；报告记**引用画像**（每工程 resolved 程序集数/degraded 原因/未绑定符号 Top-N）。**[v3 升格]** `tools/PreprocessCorpusProbe/MsBuildProject.cs` 的逐项目 MSBuild 求值能力（DefineConstants/TFM/Compile/ResolveAssemblyReference/Nullable/global usings 已实现）从"可选上移"升为 **rt10 语料硬前置**（W-10/G-26）：文件输入必须改为 **csproj Compile 项驱动**——rt10 的文件级 TFM Condition（`JsonReaderHelper.net8.cs` vs `.netstandard.cs` 同类双实现）、`$(CoreLibSharedDir)`/`$(CommonPath)` 外部共享源、gen/ 的 `ROSLYN4_x` DefineConstants 均在 csproj 里，目录 glob 输入必产出重复成员/缺源树。`.slnx` 顶层解析已支持（`ProjectPreprocessSource.cs:32,74`），缺的是 Compile/Condition 求值进 spine。注意三处现状不一致需一并收敛：conditional 默认取**首个 TFM**（rt10 = net10.0 → `NET` 符号集，恰为正确选择，但须显式记录进报告而非默认巧合）、链默认 Debug 符号、verify 固定 Release。

**L2 验证层**：四路差分（A 原库原测 / **B 预处理库+原测**（不可省——只跑 C 会让两侧同步改错）/ C 双预处理 / D 原库+预处理测）；残差 ratchet（`preprocess-residual-report.json` 快照入库、"只许减不许增"、更新需评审）；java-ready 严格化（必需 pass 不可 `--disable-pass` 绕过、baseline-error=0 且非 degraded、目标域内 DeclaredUnsupported 也须 0、skip/force override 带 owner/理由/到期复审、二次全链输出逐字节一致、报告记 configuration/TFM/引用 hash）；探针"盲区巡逻"一致性测试（凡有 Java 硬门禁而无 NORM 探针的构造 → 测试失败，反向枚举驱动登记）。

**L4 性能层**：v1 首版不变（统一树默认化 + 增量缓存键含 solutionVariant/referenceSetHash/generatorHash + 选择性 verify + pass 级并行需 Contract 数据依赖判定）；增补：残差 gate 不得用另一套引用和 parse options 重编译出"第二种事实"；新增全树 pass 不得使链总时长劣化超 10%。

---

## 7. 验收标准

### 7.1 预处理侧验收（本方案实施范围）

1. **审计清零**（java-ready profile）：**[v3] json 语料口径切换为 `D:\runtime-10.0.12` 真源** + YamlDotNet 库项目两语料，退出码 0；泛型探针 `Fail` 策略码为 0；`PreserveAndWarn` 观测码有计数且带 owner/后果（一个波次观测期后逐个翻转 Fail 并清零）；`NORM-PROJECT-DEGRADED` = 0（无 TPA 降级）。
2. **四路差分**：B 路（预处理库 + 原测）`dotnet build`/`dotnet test` 零失败——两语料分别执行；**比较转换前后测试数量**，防止被跳过/未生成的测试误记为通过。
3. **行为反例测试**（吸收 kimi 21 类中的核心 12 类，每类一个 xUnit fixture）：per-closed-type 恰一份静态、readonly 至多初始化一次、静态 cctor 恰一次、先写后读副作用、abstract/接口分派不变、`Outer<T>.Inner<U>` 与 `Outer<T>.Inner<T>` 双形态、CRTP 收敛、public 泛型成员经 MethodInfo/Activator 调用、NRT 键同轨（`List<string>` 与 `List<string?>` 同一注册）、约束传递闭包（`IFoo<U>` 携 U）、合成物幂等（二次运行逐字节一致）、多 TFM 独立通过。
4. **回归**：三测试工程全绿；**[v3] json 基线：rt10 真源全链 Java 转换 0 失败（M0 重跑建立新基线；旧语料 626/0 仅作历史对照，不再作为回归门）**；`Core/` 无库名特判（grep 守门：`YamlDotNet`、`System.Text.Json.JsonSerializer` 零命中——后者指消费面常量迁出后）。
5. **每类缺口的最小验收样例**（v1 首版 §7.2 表基础上 v2 修正、v3 增补 rt10 行）：

| 缺口 | 最小输入 | 期望最终树 |
|------|---------|-----------|
| V-01 | `class C<T> { static readonly C<T> Empty = new(); }` | holder 缓存式 getter，两次访问同引用 |
| V-02 | 同型两个 `static T s_a; static List<T> s_b;` | holder 单份 gate/store，可编译 |
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

### 7.2 下游验收衔接（超出本方案实施范围，口径说明）

"转换后可正确编译、全部单元测试跑通"的完整验收 = §7.1 + 下游两步（**[v3] `convert-project` 两语料 0 失败——json 口径为 rt10 真源**、Maven 工程 `mvn compile` + JUnit 全绿）。涉及 Java 侧的协调项收窄为三条（v2 更新）：`Cs2jPrelude.EqualityOps` methodMapping 一条；`EqualityComparer<T>/Comparer<T>` 类型映射（R4 落地后不再必需）；`ClosedMetadataRegistrationGenerator.cs:9-11` 过时注释清理（机制已闭环）。

---

## 8. 实施波次与风险

### 8.1 波次（v2 重排：先止血、再契约、后能力；每波带进入/退出门）

| 波次 | 内容 | 退出标准 | 风险 |
|------|------|---------|------|
| M0 止血 | §6.4-R0 全部（V/S/W 修复）+ fixed-span 边界加固 + **[v3] W-09 compat 漂移修复 + W-10 的 rt10 基线重跑与 Compile 求值最小接线** | 8 个 V 缺陷各有一条红→绿 fixture；37×1101→0（json 旧语料口径）；Yaml 库默认链跑通（W-06 的 null-coalesce 保守化）；撞号迁移完成且 `legacyCode` 兼容；**[v3] rt10 真源全链可跑完并产出新基线日志（重跑关注点逐项落档 §3.2.3）；ToUtf8 漂移探测红→绿** | 中（V-01 修正是行为变更，需同步修 fixture 预期并声明"singleton identity 不再作为兼容行为保留"；[v3] rt10 重跑可能暴露旧语料未见的失败——这正是换源目的，新失败清单进 M1-M3 排程而非回滚换源） |
| M1 契约与审计 | §6.3 十八探针 + CS2J14xx + 泛型族挂契约 + 报告 v1.1 + 残差快照入库 + 探针盲区巡逻测试 | 两语料 `--profile audit` 产出全量泛型残差守恒式；快照 ratchet 生效 | 低 |
| M2 编译层 | L1 spine + degraded + 引用画像 + W-04 打开（W-10 的 Compile/TFM/外部源求值在此完备） | 两语料四个语义 pass degraded=0；variance-audit 跳过文件数旧语料口径 126→0、**[v3] rt10 口径重测归零** | 中（编译成本，按工程分组+缓存对冲） |
| M3 能力层 | R1 统一解析器 + R2（P1-R1~R8 修订版）+ R3 消费面配置化 + G-02c/R2 GD 扩面 + R6 CLI 旋钮 | §7.2 样例表全绿；grep 守门通过；B 路差分两语料通过 | 中-高（R1 迁移面大；先止血序） |
| M4 域决策 | static abstract（G-07）/ 运行时合成（G-04c RG-2 级）/ Expression 域（G-23，含 Yaml 元数据面）/ 型变域（G-05）/ 晚期链重排（R5 决策） | 每域 DeclaredUnsupported 登记（owner/原因/fail-fast）或立项文档 | 高（产品决策） |
| M5 性能与文档 | 统一树默认 + 增量 + 选择性 verify + `preprocessor-matrix.md` 自动生成 + 覆盖矩阵 JSON | 整树复制 52→1；链总时长劣化 ≤10%；文档由 descriptor+contract 生成 | 中 |

### 8.2 风险与开放问题（v2 修订）

v1 首版风险 1-7 继续有效（T? 显式化波及面/探针翻转节奏/GD 死角/make-generic-static 程序集边界/双重桥接/Yaml Expression 域/映射协调项——第 7 条已收窄见 §7.2），v2 增补：

8. **V-01 修复是行为变更**：从"每访问一次构造一次"改为"per-closed-type 单例"会改变依赖旧行为的（错误）代码路径；需在 M0 中声明"singleton identity 不作为兼容行为保留"并全量跑 B 路差分。
9. **R1 统一解析器的迁移半径**：五个消费 pass 逐一迁移期间新旧并存，需 per-pass feature flag + 双跑 diff（on/off 各一次，证明变化只在目标形状——参照 C09/C10 翻转惯例）。
10. **晚期链重排 vs 原地扩展**（R5）：两案各有盲区（重排动 StateMachine/Abi 边界；原地扩展漏"晚生成泛型代码"）；M3 之前先量化后者规模再决策。
11. **公开静态泛型字段是 ABI×erasure 交叉硬点**（kimi 决策点）：删字段破坏反射/API，保留可能不可表达；逐字段四选一（保留+holder/降级/DeclaredUnsupported/重构），不得默认删除。
12. **多 TFM 输出语义**：Yaml 5 TFM——"输出一棵树"要求 csproj 同步特化或按 variant 分目录，不得混用（kimi 决策点 11.5）。
13. **测试工程 vs 生产工程残留策略**：测试工程是行为预言机不是 Java 产物输入；泛型探针在测试工程上的 policy 应显式配置（生产 Fail / 测试默认 PreserveAndWarn 的双 policy），而非散落 adapter。
14. **种子演进纪律**（吸收 seed）：每波绑定语料与量化门；残差只许减不许增；反例 fixture 与规则同生；红→绿留痕（PR 必带残差证据/红绿测名/种子前后统计）。
15. **[v3] 换源冲击管理**：rt10 真源重跑预计产生旧语料没有的失败清单（NRT 泛型实参 452 处对 V-05、`new()` 约束 18 处对 R3、IAsyncEnumerable 13 文件对 async-normalize、csproj 门控对 L1）。纪律：新失败**只登记排程，不回滚换源、不在 M0 内顺手扩能力**；旧语料 626/0 只作历史对照。若 rt10 重跑在 M0 内不可达（如 restore 依赖内部源），先落 `tools/PreprocessCorpusProbe` 级别的静态 Compile 求值报告，链重跑顺延至 M0.5 并在 §3.2.3 落档缺口。

---

## 附录 A：诊断码与 NORM 码索引（v2 修订）

**既有泛型相关码**：CS2J11xx（generic-deepen：1100 整树回滚/1101 不可判站点 [v2：含嵌套闭包 token 不可达，json 37 实测]/1102 partial 族不可闭合 [v2：含静态泛型属性/事件停用]/1103 静态字段不可改写/1104 pattern 不可提升/1105 catch 前置失败/1106 调用点在编译外 [v2：调用点闭型不可见 3 实测]/1110 表达式树站点/1120 残留/1130-1131 verify 失败/1190 plan 非自包含）；CS2J1001/1002/1003（Java 侧）；CS2J0585/0586/0587（expression-desugar 新语法守门）；CS2J5175/5116/5119/5198（member-desugar generic math/签名）；UF021/UF040/UF061（泛型指针/stackalloc/extern 拒绝）；URL001；CS2J3301-3305/3320；**[v2 撞号警示]** CS2J1203 三义（closed-metadata/extension-methods×2/thread-static）与 12xx/13xx 撞段；CS2J5196（"deferred to C02 review" 无承接者）、CS2J5223（开放泛型 with 接收者）、RR070（泛型接口 ripple）；CS2J5420（foreach-source 解构硬门，放大 S-07）；CS2J0601/0604/0607/0608（params 集合谱系）。

**本方案新增**：§6.3 十八个 NORM 码 + CS2J14xx 诊断段（1401-1409）+ `generic-constraint` pass 专属码（GC01xx 段）+ `SYNTH-*` 内部构造命名空间 + **[v3] compat 漂移探测码（CMR0xx 段新码："声明类型在语料中存在但条目零匹配"）**。

## 附录 B：证据索引（v2 修订）

| 证据 | 位置 |
|------|------|
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
| PassContract 落地 | commit `58257b25`；`Core/Preprocessing/PassContract.cs`、`PassConstructIds.cs`、`PassContractConsistencyTests.cs`；挂载 10 pass（§2.2） |
| 新四 pass | `Core/FixedSpanToArray/`、`Core/CompatMethodReplacement/`、`Core/PointerDiffInline/`、`Core/RefStructSpanFieldMemoryLift/`；commits `da132d15/08b288c1+08b288c1/a1a7bc10/37b69722/9ef6b143` |
| Java 侧 v2 修正 | raw receiver：commit `5879d22e`；静态泛型属性提升：`Transformers/Member/PropertyTransformer.cs:64,83`；42 throw 点（grep `CS2J normalization required`） |
| EqualityComparer 映射断裂（v2 复核仍成立） | `config/TypeMappings.json` 前缀检索 0 命中；compat `CSharpDefaultEqualityComparerGeneric` 存在 |
| json 静态普查 | §3.2（v1 首版数据，grep 计数） |
| 基线审计链 | `.cs2j-java-keywords-audit-baseline`（170/7）→ `.cs2j-stackalloc-lowering-audit`（97/6）→ 当前 0 失败 |
| 泛型转换器固件 | ConversionTests 7 文件（`prompts/generic-preprocess-prompt.md:52`） |
| GD 规则族/holder/残留门 | `Core/GenericPreprocess/`（v1 首版 §4.6 引用集合有效，命名修正见 §4.2） |
| 同行方案四份 | `C:\Users\15097\Desktop\V202611042101\preprocess-upgrade-{deepseek-flash-4.1-1017, kimi3.1-pro, longcat2.5, seed-evolving}.md` |

## 附录 C：本方案不做的事（v2 修订）

- 不改 52 个既有 pass 的外部行为与 CLI 开关语义（M0 缺陷修复除外——V-01 行为变更显式声明；新增规则全部增量或 opt-in）。
- 不把 Java 转换器规则搬进 preprocess；Java 侧事实只用于界定降级目标子集与一致性测试。
- 不为单一语料写死特判：M3 后 `Core/` 无库名（grep 守门）；消费面/compat 条目全部走配置。**[v3] 反向纪律：机制不得按旧语料形态固化**——compat 条目/消费面常量/守门规则必须锚定语料版本并可随版本换代（G-25 教训：ToUtf8 条目按 3.x 签名拟合即失效）。
- 不在本方案内承诺 static abstract（G-07）、运行时合成 R2+（G-04c）、Expression 域（G-23）、晚期链重排（R5）的落地——以 NORM 登记 + 域决策文档交付。
- 不修改 Java 侧代码/映射（协调项清单收窄为 §7.2 三条）。
- 不把"C# 中间树可编译"等同"Java 可转换"，也不把"转换成功"等同"测试全绿"——三层各有独立门（I1-I3 / §7.1 / §7.2）。

## 附录 D：同行方案采纳对照（v2 新增）

> 四份同行方案（`C:\Users\15097\Desktop\V202611042101`）与本方案（v1 首版）同题。v2 的取舍原则：**论断先核证、增量再吸收、冲突以本仓代码为准**。

**已采纳（经核证）**

- deepseek：YamlDotNet 语料实测与"默认链在泛型 pass 前中止"方法论发现（W-06）；STJ 运行统计分母（§3.2）；GAP-01（编译环境 P0）/GAP-05（37×1101）/GAP-15（契约）/GAP-16（链鲁棒性）→ G-17/G-18/G-24；CS2J14xx 诊断段与撞号迁移；CN-09/10 规则与 CN-01/04 设计坑；GD-NEST-2/GD-STATIC-3；RG-0~4 反射分级（经 kimi 版本）。
- kimi：P0 缺陷清单 9 条（V-01/02/04/05/06/08 与 GD-03/CM-03 归并进 V 层）；YamlDotNet NRT 运行时反射链/双 builder/Expression 元数据面（§3.3）；四路差分与 21 类行为反例（§7）；java-ready 严格化 10 条（§6.6 L2）；`StaticGenericStatePlan` 规范形；晚期链重排（作为 R5 决策点）；`MsBuildProject` probe 上移与 TFM/符号配置三不一致。
- longcat：G-02c 修正（is/as 二元式不改写，V-07）；closed-metadata D2 已接线（W-07，修正 v1 论断）；VA020-027 审计码已有（W-01 修正 G-06 缺口性质）；GD-ISAS-EXPR 降级形态（§7.2 样例）；params 谱系（G-21）；Expression 三策略分散与 GroupBy 恒冻结（§4.2）；enum 算术 owner/cast 顺序契约（§8.2.14 种子纪律关联）；报告 schema v2 要点。
- seed：R1-R9 运行时信息分类学（§3.1）；合成层约束债 GEN-GAP-05/06/09/10（S 层，含三处实锤）；`SyntheticTypeParameterResolver`（R1）；探针族 12 码与 TOKEN-SITE 作用域式检测、degraded 口径、SYNTH-\* 命名空间（§6.3）；PassContract 三步推广；种子演进方法学（波次门/ratchet/反例 fixture/盲区巡逻/grep 守门，§6.6/§8）；`ClosedTypeConsumerSurfaces.json`（R3）；产物契约冻结（前缀常量集中+键同构）。

**不采纳（经核证证伪或过时）**

- deepseek：`GenericCollectionTypeResolver` 文件名（不存在，编造）；§8.7 示例行号-API 错配；"静态泛型属性无载体"（Java 侧 PropertyTransformer 有提升）；`NormalizationAuditPassAdapter.cs:161-162` 行号错配。
- longcat：开放泛型 tuple"未覆盖"（槽位机制已实现）；"PassContract 建议新增"（`58257b25` 已落地，其提案仅 `VerificationProfile` 字段与 catalog 三消费点仍有增量价值）。
- kimi/deepseek 基线漂移：两者分别基于 `361c8214`/`060e17ff`/`4bc8ea9e`，pass 数 49/48/48 均已过时（当前 52）；本方案以 HEAD `6b57a210` 重对全部行号。

**双方互补确认**：v1 首版独有的 P1-R3（泛型 attribute 降级）/P1-R4（EqualityOps）/P1-R5（make-generic-static 闭型物化）/P1-R6（pattern CompareTo+C37）/P1-R8（collection-expr 泛型 Add）/P2-R3（推断实参物化）与 TypeMappings 映射面分析，四份同行方案均无等价具体规则，全部保留。
