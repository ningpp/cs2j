# P3 member-desugar 画像证据汇总（C27 M4 record-with / C50 M3 field-keyword）

- 日期：2026-10-09
- worktree/分支：`.worktrees/p3-profiles` / `feature/p3-sdk-profiles`（基线 7afe0ea5）
- 本批次提交：`f40cee3c` fix(member-desugar) CS0082 访问器槽位归属 → `013ab5e8` feat(probe) c27-c50-ip in-proc 画像 → `da59c13e` docs(c27) M4 证据节 → `aeeb8787` docs(c50) M3 证据节 → 本报告
- 细则全文：C27 见 [`specs/2026-09-29-c27-record-with-design.md`](../specs/2026-09-29-c27-record-with-design.md) §13；C50 见 [`specs/2026-09-29-c50-field-keyword-design.md`](../specs/2026-09-29-c50-field-keyword-design.md) §13。本文只做跨族汇总与索引，计数一律以 gitignored 原始 JSON 为准（无估计）。

## 1. 方法论

1. **画像工具**：`tools/PreprocessCorpusProbe`，画像 id `c27-c50-ip`（提交 `013ab5e8`）。in-proc 逐 msbuild 工程一个 session，仅调生产 public API `MemberDesugarPreprocessor.RewriteFile`（共享语义分析器，`EnsureBuilt` 首文件懒分析整编译并缓存 plan，无需 internal 入口）。
2. **树口径**：编译项取 `dotnet msbuild -getItem:Compile`（+ DefineConstants/TargetFramework/Nullable/NoWarn、`ResolveAssemblyReferences -getItem:ReferencePath`、obj/artifacts 下 GlobalUsings），**不是目录递归枚举**；无 CLI 镜像合并问题，不写语料。
3. **为什么不能用 CLI 画像**：member-desugar CLI 适配器拥有整树「改写→候选编译→diff 错误归属→接受/回滚」循环；在镜像碰撞/缺引用/元数据接口索引器场景大量回滚，族摘要行被冲成全 0（实测 YamlDotNet 单产品轮 262 文件 transformed 而摘要全 0），无法取得诚实 plan/skip 计数。in-proc 画像只分析、不跑验收回滚，计数即 plan/skip 真实值。
4. **选项**：non-strict（Error 降 Warning 全计），`Records.Enabled=true`、`FieldKeyword.Enabled=true`（两子集开关生产默认经核实仍为 **false**）；编译项按 csproj 累加，共享链接源重复计入（与 C17 §13 同口径）。
5. **复现命令**（工作区内，Release 生成的 probe dll）：

```powershell
$probe = 'tools\PreprocessCorpusProbe\bin\Release\net10.0\PreprocessCorpusProbe.dll'
dotnet $probe scan --root D:\csharp-sdk --profile c27-c50-ip --out probe-out\profiles\c27c50-sdk --corpus csharp-sdk
dotnet $probe scan --root D:\efcore-10.0.12 --samples src --profile c27-c50-ip --out probe-out\profiles\c27c50-ef --corpus efcore-10.0.12 `
  --projects "src\EFCore\EFCore.csproj,src\EFCore.Abstractions\EFCore.Abstractions.csproj,src\EFCore.Relational\EFCore.Relational.csproj,src\EFCore.SqlServer\EFCore.SqlServer.csproj,src\EFCore.Sqlite.Core\EFCore.Sqlite.Core.csproj,src\Microsoft.Data.Sqlite.Core\Microsoft.Data.Sqlite.Core.csproj"
dotnet $probe scan --root D:\YamlDotNet-18.0.0 --profile c27-c50-ip --out probe-out\profiles\c27c50-yaml --corpus YamlDotNet-18.0.0
dotnet $probe scan --root D:\runtime-10.0.12 --profile c27-c50-ip --out probe-out\profiles\c27c50-runtime --corpus runtime-10.0.12 `
  --samples "src\libraries\System.Text.Json\src,src\libraries\System.Collections.Immutable\src,src\libraries\Microsoft.Extensions.DependencyInjection\src,src\libraries\Microsoft.Extensions.Caching.Memory\src"
```

## 2. 语料规模（与 C17 §13.1 同口径可比）

| 语料 | 路径/版本 | csproj（不可用） | 编译项 | 改写文件 |
|---|---|---:|---:|---:|
| csharp-sdk | `D:\csharp-sdk`（json/uri/xml-tiny/regular-expressions） | 21（0） | 1457 | 792（干净 665） |
| EF Core | `D:\efcore-10.0.12`，代表子集 6 csproj | 6（2） | 2141 | 1458（干净 682，driver-error 跳过 1） |
| YamlDotNet | `D:\YamlDotNet-18.0.0` | 8（5） | 17 | 10（干净 7） |
| runtime | `D:\runtime-10.0.12`，4 产品工程采样 | 4（4） | 0 | 数据未取得（§6） |

不可用原因均为环境项：EFCore.Sqlite.Core **NETSDK1004**、Microsoft.Data.Sqlite.Core/YamlDotNet 主库等/runtime 4 工程 **MSB4057**（未 restore 或无 ResolveAssemblyReferences 目标）。

## 3. 逐族关键计数

### 3.1 C27 record-with（本批主角）

| 桶 | sdk | EF | Yaml |
|---|---:|---:|---:|
| RecordPlans（RecordClassPlans） | 0 | 9（4） | 0 |
| PositionalRecordsReshaped / CloneFactoriesSynthesized | 0 / 0 | 0 / 0 | 0 / 0 |
| WithSitesTotal（Class/Struct/Empty） | 0 | 0 | 0 |
| PropertyAssignments / FieldAssignments / TemporariesPromoted / ResidualWithCount | 0 | 0 | 0 |
| CS2J5123 位置属性让位（C11↔C27 挂点） | 4 | 41 | 0 |

with 跳过逐条人工定性（仅 EF 有命中，共 105 条，全部设计既定闸门、零过保守）：

| 码 | 计数 | 性质 |
|---|---:|---|
| CS2J5222 元数据 record 接收者 | 40（Relational 39/SqlServer 1） | 树外声明无法补克隆工厂；终态需跨程序集克隆策略 |
| CS2J5228 元数据 record struct 接收者 | 25（21/4） | 树外声明且无 set_X |
| CS2J5223 不可提升上下文 | 22（全 EFCore） | ExpressionTreeFuncletizer.PathNode 字段/属性初始化器位 |
| CS2J5220 readonly struct 非空 with | 11（7/4） | CS8340/CS8341 语言事实，克隆局部只读 |
| CS2J5124 with 引用未去糖属性 | 7（4/3） | 上述三类的配套原子挂点 |
| 5221/5224/5225/5226/5227/5230 | 0 | residual 零 with 闸门满足（ResidualWithCount=0） |

勘误：实现码位 C39 终结器占 5200-5211，C27 实际占 5212-5230（设计 §6 提案段整体错位 12，以 `MemberDesugarModels.cs` 为准）；sdk 报表中的 5200×2/5202×2/5206×2 是 C39 命中，与 C27 无关。

### 3.2 C50 field keyword（本批主角）

| 桶 | sdk | EF | Yaml |
|---|---:|---:|---:|
| FieldKeywordProperties（CS2J5125 Info） | 0 | **60** | 0 |
| AutoAccessorsExpanded / FieldReferencesRewritten / TargetAttributesMigrated | 0 / 0 / 0 | 5 / 86 / 98 | 0 / 0 / 0 |
| FieldInitializersMoved / FieldKeywordResidualTokens | 0 / 0 | 0 / 0 | 0 / 0 |
| CS2J5122 / 5126 / 5127 / 5128 | 全 0 | 全 0 | 全 0 |

EF 分布：EFCore 54 属性/5 auto/80 refs/90 attrs；EFCore.Relational 3/0/3/4；EFCore.SqlServer 3/0/3/4。60 个真实 C#14 field-backed 属性全部归一成功，零安全网、零拒收、零特性失败、零残余。sdk/Yaml 零命中经同轮 EF 命中交叉验证为「语料不含该语法」而非漏检。

### 3.3 同车全族上下文计数（member-desugar 全规则同驱，非本两族结论仅作背景）

| 桶 | sdk | EF | Yaml |
|---|---:|---:|---:|
| PropertiesPlanned / BackingFieldsGenerated | 13756 / 1919 | 9656 / 2461 | 122 / 61 |
| 读/写/复合站点重写 | 24448 / 3158 / 68 | 24573 / 2403 / 14 | 223 / 19 / 0 |
| IndexersPlanned（读/写重写） | 177（607/119） | 160（136/16） | 0 |
| EventsPlanned（后备字段） | 34（0） | 66（17） | 0 |
| OperatorsPlanned | 28 | 10 | 0（MetadataOperator 不动站点 1） |
| SkippedByReason 主要键 | AttributeUnroutable 209、NonLiftableInitializer 200、PositionalRecordProperty 4、ConversionMirrorMiss 4 | AttributeUnroutable 17、NonLiftableInitializer 44、PositionalRecordProperty 41、MetadataRecordReceiver 40、NonRecordClassWith 25、NonLiftableWithContext 22、PositionalReshapeGate 11、WithSite 7、IndexerNonLiftableSite 4、UnbuildableLift 4、ExpressionTreeSite 2、RefReturningProperty 1 | 无 |

## 4. 拍板事项

1. **C27（M4）不翻默认**：§8 门槛 4（语料 triage）在可测语料闭环（105 命中全定性、residual=0、过保守=0），但门槛 3 的两个 Java 硬门禁经只读核查均未实施——[RecordTransformer.cs:223-246](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L223-L246) 无复制构造发射；[ControlFlowTransformer.cs:54/633](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L54) 旧 with 臂仍可达。`rules.records` 维持默认 false。
2. **C50（M3）不翻默认**：技术就绪（60 真实命中零失败），但附裁决 2 的频率前提「Collections/Text 高频库实测密集」直接证据面（runtime）不可得；可测面 sdk 全零、EF 单产品集中。`rules.fieldKeyword` 维持默认 false，随 C11 转换器消费 PR + runtime 复跑后的族级评审再议。
3. CatalogItems 两族均已在册（早前提交）：`"C11, C12, C13, C14, C39, C46, C50, C27"`（PassCatalog.cs:549），本批未改 CLI 热文件。
4. **不改 CLI 树准备口径、不改转换器、不翻任何默认开关**（纪律项遵守）。

## 5. 代码修复/工具变更清单（本批 2 个代码提交）

| 提交 | 文件 | 内容 |
|---|---|---|
| `f40cee3c` | `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs` | `TryAttributeDiagnosticBlockedByExcluded` 候选成员名由单值改列表，补 `BasePropertyDeclarationSyntax` 声明名（CS0082 报点落 get/set 槽位时 `GetNameString()` 为 null 的收编）；`OwnsTouch` 严格匹配语义不变。既有 xUnit `Phantom_Mirror_Types_Outside_CompileItems_Do_Not_Trigger_WholeTree_Rollback` 覆盖，未新增测试（禁 dotnet test；Release 编译 0 错误） |
| `013ab5e8` | `tools/PreprocessCorpusProbe/{Profiles,Program,ProbeReport}.cs` | 新增 `c27-c50-ip` in-proc 画像与 `IProjectSession` 每工程会话；反射展平统计桶 + 显式展平 SkippedByReason 枚举键 + CS2J 诊断入账；既有画像路径零影响 |

调试噪音纪律：MD 临时 trace 全部移除（`src/` 零 `CS2J_MD_TRACE`，grep 已核），适配器未提交 trace 改动已整体 `git checkout` 还原；测试与正常路径不吐 trace，无半残代码。带历史 trace 的旧日志仅留在 gitignored `probe-out/` 作证据。

## 6. 未取得数据声明

1. **runtime-10.0.12 全仓不可得**：`artifacts\obj` 下 project.assets.json 数量为 **0**；System.Text.Json、System.Collections.Immutable、Microsoft.Extensions.DependencyInjection、Microsoft.Extensions.Caching.Memory 4 个产品工程全部 MSB4057。restore 会写入只读语料（纪律禁止），故 record/with 与 field 在 runtime Collections/Text 高频库的真实频率**本轮无法测量**——它恰是 C50 附裁决 2 默认开频率前提的直接证据面。需在允许写入的环境 restore 后用 §1 同命令复跑。
2. YamlDotNet 主库（YamlDotNet.csproj）等 5 工程未 restore/无目标，有效编译项仅 17（analyzer 13 + AoT 2+2）；零 record/零 field 结论不外推整库。
3. CLI 整树口径（接受/回滚后摘要）结构性取不到诚实计数，本批以 in-proc 分析值替代；CLI Compile 项口径修复后应复跑 CLI 全树画像做交叉验证。

## 7. 族级残余与后续项（均不在本批修）

1. **CLI 镜像合并 5100**（json TrimmingTests）：`ProjectPreprocessSource` 目录递归枚举把 `EnableDefaultCompileItems=false`、本应每文件独立成程序的镜像 `ClassWithDay`（EnumConverterTest.cs:21 与 JsonConverterAttributeTest.cs:23）合入同一编译 → CS0082 `get_Day` + 缺引用级联（newErrors 1688→1243，5101 计 220），最终 1×CS2J5100（JsonConverterAttributeTest.cs(26,32)）。`f40cee3c` 已收编族内可收部分；根因（CLI 应对齐 msbuild Compile 项）登记后续。证据：`probe-out/profiles/cli/repro-json2/{stdout,stderr}.log`。
2. **YamlDotNet OrderedDictionary 真实 pass 缺口**（单产品工程，与树合并无关）：公开索引器 `this[TKey]` 实现元数据 `IDictionary<TKey>.this[]`，去糖成普通 `get_Item` 后 CS0470（+CS0535）；且 `this[TKey]`/`this[int]` 重载同产 `get_Item`、`IndexerPlan.OwnsTouch` 不辨签名致归属命中 2 plan。1×CS2J5100 + 41×CS2J5101 + 1×CS2J5103。后续需：显式接口实现发射 + 签名敏感 plan 归属。证据：`probe-out/profiles/cli/single-yaml/{stdout,stderr}.log`。
3. **C11 使用点分析器崩溃 1 例（driver-error）**：`src/EFCore/Metadata/Conventions/InversePropertyAttributeConvention.cs`；`InvalidCastException`：InvocationExpressionSyntax 不能强转 SimpleNameSyntax，`PropertyUseSiteRewriter.VisitPostfixUnaryExpression`（PropertyUseSiteRewriter.cs:409），经 null 条件绑定（VisitConditionalAccessExpression:480）进入。probe 按文件记 driverErrors=1 并跳过该文件改写；C27/C50 计数不受影响（崩溃在 C11 重写期）。复现：§1 EF 命令加 `--projects "src\EFCore\EFCore.csproj" --out probe-out\profiles\c27c50-ef-dbg` 并设 `$env:PROBE_DEBUG='1'`（完整堆栈已存该目录任务日志）。
4. EF 轮另有 C11/C12/C14 面 Warning（5112×99、5119×44、5137×4、5176×4、5117×2、5115×1 等），属各族既有残余口径，本批不 triage，索引于 JSON 报表供后续族复跑使用。

## 8. 报表与证据路径（均 gitignored）

- `probe-out/profiles/c27c50-sdk/c27-c50-ip.{json,md}`（21 工程/1457 文件/9448 诊断）
- `probe-out/profiles/c27c50-ef/c27-c50-ip.{json,md}`（6 工程/2141 文件/7993 诊断；含 1 driver-error）
- `probe-out/profiles/c27c50-yaml/c27-c50-ip.{json,md}`（8 工程/17 文件/122 诊断）
- `probe-out/profiles/c27c50-runtime/c27-c50-ip.{json,md}`（4 工程全部不可用）
- CLI 取证：`probe-out/profiles/cli/repro-json2/`、`probe-out/profiles/cli/single-yaml/`（含 `.cs2j-member-desugar-src/` 中间树与 stdout/stderr）
- driver-error 堆栈：`probe-out/profiles/c27c50-ef-dbg/` 对应后台任务输出（PROBE_DEBUG 单工程轮）

## 9. 验证

- `dotnet build CSharpToJavaConverter.slnx -c Release`：**退出码 0，0 个错误，113 个警告**（2026-10-09 本批提交后复跑，警告为既有基线，无新增）。
- 纪律核对：未执行 dotnet test；仅 add 具体文件；未 push；只读语料四库零改动；未改 PassCatalog/PassStubCatalog/PassChainFactory、CLI Program.cs、转换器生产代码、任何默认开关。
