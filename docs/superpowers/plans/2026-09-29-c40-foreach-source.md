# C40 foreach-source 实现计划（M1 / M2 / M3）

> **For agentic workers:** REQUIRED SUB-SKILL: 使用 superpowers:executing-plans 按任务逐项执行。步骤用 checkbox（`- [ ]`）跟踪；已完成项以 `- [x]` 标注并附偏差记录。

**Goal:** 新增 C#→C# 预处理器 `foreach-source`（PassId `foreach-source`，码段 CS2J54xx），把下游转换器无法消化的 foreach 源形态（鸭子枚举器、Span/ref-struct 索引、ref 枚举）降级为显式 `GetEnumerator()` + `while (MoveNext())` + `Current` 骨架或索引 while，中间树保持可编译且语义等价。

**Architecture:** 复用 C16 foreach-source 锚点的整树 compilation + 逐文件 Rewrite 脊柱。M1 新增自有目录 `src/CSharpToJava.Core/ForeachSource/Rules/EnumeratorAdapter/`（ForeachBindingResolver / EnumeratorLoweringBuilder / EnumeratorAdapterRule 三文件），在现有 ForeachSourceRewriter 上追加规则注册；根层统计 15 个 C40 字段、CLI adapter MergeStatistics/摘要行、PassCatalog 描述追加 "C16, C40"。

**Tech Stack:** C# / .NET 10、Roslyn `Microsoft.CodeAnalysis.CSharp` **5.0.0**（运行时探针钉死，非设计假设的 4.12）、xUnit；fixture 为 net10.0 + xUnit 的 SampleForeachSourceSolution（C16 已建，本期追加文件）。

> 规范来源：[2026-09-29-c40-foreach-source-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c40-foreach-source-design.md)（§9 里程碑；M1 探针结论回填见该文档「附：M1 探针结论校验节」）。

---

## M1 — duck-typed 枚举器适配（FA-1/2/3/4）

### Task 1: 基础扩展（根层模型/统计/诊断码）

- [x] **Step 1: 诊断码声明**：CS2J5450（鸭子站点）/5451（强转 Current）/5452（直接枚举器，防御性）/5453（源缓存）/5454（try/finally Dispose）/5456（扩展 GetEnumerator）；5455/5457/5470（M2）、5480-5495（M3）仅注释占位。
- [x] **Step 2: Statistics 15 个 C40 字段** + CLI adapter MergeStatistics/摘要行 + PassCatalog `"C16, C40"` + PassChainOrderTests 断言。

### Task 2: EnumeratorAdapter 三件套

**Files:**
- Create: `src/CSharpToJava.Core/ForeachSource/Rules/EnumeratorAdapter/ForeachBindingResolver.cs`
- Create: `src/CSharpToJava.Core/ForeachSource/Rules/EnumeratorAdapter/EnumeratorLoweringBuilder.cs`
- Create: `src/CSharpToJava.Core/ForeachSource/Rules/EnumeratorAdapter/EnumeratorAdapterRule.cs`

- [x] **Step 1: ForeachBindingResolver**：经 `GetForEachStatementInfo` 绑定 GetEnumerator/MoveNext/Current/DisposeMethod；枚举器类型从 GetEnumerator.ReturnType 取；元素类型以 `(model.GetDeclaredSymbol(node) as ILocalSymbol)?.Type ?? info.ElementType` 为准；IEnumerable 双重排除（类型自身 SpecialType/OriginalDefinition + AllInterfaces 扫描）；扩展 GetEnumerator（FA-2）与实例鸭子统一走 Resolver。
- [x] **Step 2: EnumeratorLoweringBuilder**：源缓存 `var __cs2jFaSrcN = <源>;` + `var __cs2jFaEN = __cs2jFaSrcN.GetEnumerator();` + `while (__cs2jFaEN.MoveNext()) { var item = __cs2jFaEN.Current; ... }`；Current 类型与迭代局部类型不一致时用 SymbolEqualityComparer 判定并插 FullyQualifiedFormat 强转；IDisposable 站点包 try/finally。
- [x] **Step 3: EnumeratorAdapterRule**：接线 ForeachSourceRewriter，IEnumerable/string/已排除形态透传，Changed/Site 统计写 5450/5451/5453/5454/5456。

### Task 3: 探针与偏差修正（2026-10-01）

- [x] **Step 1: Roslyn API 探针**（临时文件 C40TempProbe.cs，验证后删除）：`IForEachLoopOperation.IsRef` 在 5.0.0 不存在 → 用 `ILocalSymbol.RefKind`；`ForEachStatementInfo.IsDynamicallyDispatched` 不存在 → dynamic 按 `TypeKind.Dynamic` 判定。
- [x] **Step 2: Dispose 语义探针**：foreach 仅当枚举器类型真实实现 `System.IDisposable` 才 Dispose；判定改为 `EnumeratorImplementsDisposable`（SpecialType/AllInterfaces + metadata-name 兜底）；直接调用走 `TryGetDirectDisposeAccess`（接口类型直接可访问；具体类型沿 BaseType 找 public 无参 void Dispose；否则 `((System.IDisposable)e).Dispose()`，struct 时 DisposeBoxed）。删除旧 `DisposeRequiresExplicitInterfaceCast`；`binding.DisposeMethod` 降级为交叉校验注释。
- [x] **Step 3: 直接枚举器不可达确认**：`foreach(var x in enumeratorVariable)` 对 IEnumerator/IEnumerator&lt;T&gt;/鸭子枚举器一律 CS1579；`DirectEnumerator`/CS2J5452 分支防御性保留。
- [x] **Step 4: net10 接口层次确认**：非泛型 `System.Collections.IEnumerator` 不再继承 IDisposable（AllInterfaces 为空），非泛型盒子站点不生成 finally；IEnumerator&lt;T&gt; 仍继承。
- [x] **Step 5: 捕获等价性确认（决策 12）**：while 骨架每轮块级局部，lambda 捕获与原生 foreach 一致 `[1,2,3]`。

### Task 4: 测试与 fixture

- [x] **Step 1: Core 单测**：`tests/CSharpToJava.Tests/ForeachSourceC40RewriterUnitTests.cs`（FA-1/FA-2/FA-3 拆分为 CS1579 不降级 + 接口盒子两站点、FA-4 三 Dispose 形态、FA-9/FA-10 子串计数修正）；同步修正 `ForeachSourceRewriterUnitTests.cs` 的 FS1 透传计数（鸭子站点 + int 扩展站点现在被 FA 命中）。当前 41 通过 0 失败。
- [x] **Step 2: fixture 追加**：
  - `tests/SampleForeachSourceSolution/SampleForeachSourceLib/EnumeratorDuck.cs`：PlainDuck（无 Dispose）+ PatternDisposeDuck（public Dispose 无接口，旗标永不触发）+ FlaggedDuck/FlaggedEnumerator（IDisposable + Dispose 旗标）+ ExtensionEnumerable/扩展 GetEnumerator + GenericBoxedEnumerable（IEnumerator&lt;int&gt;）/NonGenericBoxedEnumerable（IEnumerator）+ PlainDuckFactory（源求值计数）+ EnumeratorDuckSamples（正常/break/异常/continue/决策 12 捕获）。
  - `tests/SampleForeachSourceSolution/SampleForeachSourceTests/EnumeratorDuckTests.cs`：非 IDisposable 旗标恒 false；IDisposable 正常/break/异常各恰好一次；continue 中途 0、末尾 1；源只求值一次；泛型/非泛型盒子枚举；lambda 每轮独立值。
- [x] **Step 3: ProjectTests**：新建 `tests/CSharpToJava.ProjectTests/ForeachSourceC40ProjectTests.cs`（PassId=`foreach-source`，中间目录 `.cs2j-foreach-source-src`，VerifyBuild+VerifyTests）：断言骨架文本、站点序号命名（FreshName 文件级计数器）、非 IDisposable 无 finally/Dispose、IDisposable try/finally、非泛型盒子无 finally 且有 `(int)__cs2jFaE4.Current`、扩展静态名不内联。
- [x] **Step 4: 偏差修复（基线编译断裂）**：`CliChainRequest.EqualityConcatOptionsFactory` 已成为 required 成员，补齐 C40 新文件外的三个既有缺口：`ForeachSourceProjectTests.cs`（两处）、`NumericBoundaryDecimalOpsProjectTests.cs`。

### Task 5: 文档与验收

- [x] **Step 1: 设计回填**：spec 开放问题裁决 5 后插入【M1 探针结论 2026-10-01】摘要 + 文末「M1 探针结论校验节」8 条运行时证据。
- [x] **Step 2: plan 文档**：本文件。
- [x] **Step 3: 4 组验收命令全绿**（M1 收口；2026-10-01 实测）：
  - [x] `dotnet build CSharpToJavaConverter.slnx -c Debug` → 已成功生成，0 错误
  - [x] `dotnet test tests/CSharpToJava.Tests --filter "FullyQualifiedName~ForeachSource|FullyQualifiedName~Preprocessing"` → 77 通过 0 失败
  - [x] `dotnet test tests/CSharpToJava.ProjectTests --filter "FullyQualifiedName~ForeachSource"` → 11 通过 0 失败（含 C16 既有 10 项 + 新增 C40 项目测试，含 fixture 中间树 build+test）
  - [x] `dotnet test tests/CSharpToJava.Tests`（全量，slnx 不含测试工程）→ 2227 通过 / 5 失败；**5 个失败经 `git stash -u` 基线对照确认与本轮无关**：2 个 EnumDeclaration ulong token 断言（基线即红，疑似 dev 合并后行为漂移待 EnumDeclaration 负责人收口）、3 个 Java 环境失败（`io.github.ningpp.compat` 包未就位 ×2、Maven 不支持发行版本 28 ×1）。
- [x] **Step 4: git 提交**（不 push、不建标签）：
  - `feat(preprocessing): C40 foreach-source M1 鸭子枚举器适配（FA-1/2/3/4）+ 绑定解析/注册/诊断/测试`

### M1 偏差汇总（相对设计原案）

| 项 | 设计原案 | 实际实现 | 证据 |
|---|---|---|---|
| Roslyn 版本 | 4.12 | 5.0.0 | 编译+反射探针 |
| IsRef 信号 | IForEachLoopOperation.IsRef | ILocalSymbol.RefKind | API 不存在 |
| Dispose 判定（决策 6） | info.DisposeMethod 为事实源 | AllInterfaces 含真实 IDisposable | pattern Dispose 运行时不被调用，info 误报 |
| FA-3 直接枚举器复制源 | 可达分支 | 合法源码不可达（CS1579），防御性保留 | GetForEachStatementInfo 全空 |
| 非泛型 IEnumerator | 隐含继承 IDisposable | net10 不继承，无 finally | 符号 AllInterfaces=[] + 运行时 `as IDisposable == null` |
| ProjectTests 基线 | 不传 EqualityConcatOptionsFactory | required 成员，三个既有文件一并补齐 | CS9035 编译错误 |

---

## M2 — struct/非 Dispose 细节与收口（已完成）

- [x] **Task 1**: FA 决策 5 struct 枚举器全形态——硬 `var` 接收、显式接口 Dispose 装箱点 CS2J5455 计数与 sdk 列名；pattern Dispose 矩阵按 M2 探针修正：**ref struct** 枚举器（IsRefLikeType + 可访问无参 void pattern Dispose()）直接生成 pattern 调用 finally（不装箱）；class/普通 struct 的 pattern Dispose 仍不生成（M1 结论不变）。fixture/单测矩阵补齐（5 形态）。
- [x] **Task 2**: FA-5 Span 索引循环（`Length`/`Count` + int 单参索引器 → 索引 for），CS2J5457 unresolved 闸门 + CS2J5470 residual 闸门；ref 形态参数经共享 builder 的 RefKind 透传，规则实现留 M3。
- [x] **Task 3（部分）**: 闭包捕获/break/continue/嵌套/非块体/try 内 continue 夹具与单测扩面（D10 四例 + fixture 运行时 break/continue/异常/嵌套断言）。**偏差**：迭代器方法（C04 产物）串链 build/test 不在 M2 任务书（A-K）内，未做，留后续里程碑。
- [x] **Task 4**: CS2J5455/5457/5460/5470 统计闸门 + Release 全量 build（0 error）+ 三个测试工程实跑 + 零误伤回归四件套全绿；链摘要行补 boxed/span/unresolved/residual 分桶。

### M2 偏差汇总（相对设计原案 / M1）

| 项 | 设计原案 | 实际实现 | 证据 |
|---|---|---|---|
| Dispose 充要条件 | 实现 IDisposable 才 finally | IDisposable **或** IsRefLikeType + 可访问无参 void pattern Dispose()（ContainingType 为枚举器自身） | Roslyn 5.0 探针：ref struct 有/无 Dispose → info.DisposeMethod 为具体 pattern 符号/null；普通 struct/class pattern Dispose → null/误报接口符号，均不采信 |
| FA-5 循环合成位置 | EnumeratorAdapter 规则目录内 | 抽到根层 `ForeachSource/ForeachIndexLoopBuilder.cs`（TryFindIndexMembers + Build(RefKind)），供 M3 FR 规则复用 ref 修饰/长度成员/索引器参数化 | 代码结构 |
| 显式接口 Dispose cast | `(IDisposable)e.Dispose()` 直排 | `((global::System.IDisposable)e).Dispose()` 外层 ParenthesizedExpression（M1 潜伏语义 bug：无括号被解析为对 Dispose() 返回值强转） | 新增 M2_FS5 显式 struct 用例暴露 |
| 嵌套 C40 站点 | 未明确 detached 节点处理 | 子 foreach 先改写后外层节点脱离原模型树；ForeachSourceSite 新增 OriginalNode，所有语义查询（GetForEachStatementInfo/GetDeclaredSymbol/表达式类型）走原节点，代码生成走 visited 节点；StringSourceRule 同步防御 | D10 外鸭子内 Span 单测 + fixture 外 Span 内鸭子运行时 |
| ProjectTests MethodBody | 硬编码 LF 行尾 | 行尾归一化（CRLF 检出兼容；该潜伏问题对 M1 测试同样存在） | autocrlf=true 工作树实跑 |
| ProjectTests fixture 隔离 | 输出目录在共享部署 FixtureDir 内 | C40 两个测试改为在 TEMP 私有 fixture 副本（排除 `.test-output*`/bin/obj）内运行与清理；规避与 C16 ForeachSourceProjectTests/CLITests 跨类并行时的树发现-清理竞态（File.Copy 命中半删除中间树） | 全量并行实跑复现，隔离后 793/793（除 C47 环境红灯外） |
| Task 3 C04 迭代器串链 | M2 内 | 不在 M2 任务书 A-K，延后 | — |

### M2 验收实跑（本机，.NET SDK 10.0.300 / Roslyn 5.0.0）

- `dotnet build CSharpToJavaConverter.slnx -c Release`：**0 error**。
- CSharpToJava.Tests：**2245 通过 / 5 失败**，5 个失败与白名单逐一点名一致（2 EnumDeclarationRewriterUnitTests unsigned token + 2 CSharpXmlCompileRegressionTests + 1 SystemPrivateXmlRuntimeResourceTests，均 Java/环境基线）。
- CSharpToJava.ProjectTests：**793 通过 / 1 失败**。唯一失败 `EqualityConcatProjectTests.Fixture_Converts_Compiles_RunsAllRuntimeAssertions`（javac：`io.github.ningpp.compat` 包不存在，Java 支持库环境族）——经 M1 HEAD（ab34272b）临时 worktree 复现为**既有基线红灯**，与 C40 无关，属白名单外发现的第 6 个环境基线（未修改 C47 代码）。
- CSharpToJava.ConversionTests：**671 通过 / 0 失败**。
- 零误伤四件套：25/25；C40 单测：29/29；C40 project 测试（含 fixture verify build/test）：2/2。

## M3 — ref / ref readonly

- [x] **Task 1**: FR-1/2/3/4 规则实现 + CS2J5480-5495 诊断族；ref 迭代变量写回语义。
- [x] **Task 2（硬前置，开放问题 1 裁决）**：C01 升级元素访问声明站点为访问器代理（数组 `ByRef.Create(arr,i)`；Span/自定义 ref struct 接收器因 CS8175/CS9108 不可闭包，落 ScopedByRef/ReadOnlyScopedByRef ref 字段形态，Java 半区仍映射既有 ByRef/ReadOnlyByRef 工厂），独立提交 53cf4304 先合；ref readonly 用无 setter 代理（开放问题 3）。
- [x] **Task 3**: foreach-source ↔ C01 联合链路打通（spec 风险 3 关闭）+ Java 半区冒烟（ByRef.create(span,i) / ReadOnlyByRef.create(span,i) / 数组 ByRef.create(arr,i) / 鸭子 moveNext-getCurrent-try/finally-dispose）。
- [ ] **Task 4**: csharp-sdk ref/ref-struct 站点分桶报表（5485 人工定性）+ §8 附加门槛 + 随锚点评审翻转默认开。**M3 保持 opt-in（`PassCatalog.cs` foreach-source `defaultEnabled: false`）不翻默认；§8 sdk 训练集扫描属评审期活动，本里程碑不做。**

### M3 偏差表（实施实测）

| 事项 | 原计划/假设 | 实际落地 | 证据 |
|---|---|---|---|
| FR-1 一维数组 ref foreach 可达性 | 假设有合法用户输入 | Roslyn 5.0 csc 对数组 ref foreach 一律 **CS1510**（ref/out 参数与局部皆然），fixture 无法放入该形态；pass 仍防御性降级，覆盖只在 Core 单测（lowered 输出可独立编译运行，Emit=20） | 探针目录 `%TEMP%\cs2j-m3-arrayprobe`（已删）；`ForeachSourceC40RewriterUnitTests.FR1_*` |
| 自定义 Count+ref 索引器源 | 仅有索引器即可 foreach | csc 要求同时具备 ref-Current `GetEnumerator()` 才合法（否则 CS1579）；pass 仍优先走索引路径（Resolver 先判 IsIndexPattern） | fixture `RefCollections.cs` CountIndexBag |
| `SyntaxFactory.RefType` 参数顺序 | （M2 代码错放）readonly token 在第一参 | 签名为 `(refKeyword, readOnlyKeyword, type)`，错放运行时 ArgumentException；ForeachIndexLoopBuilder/EnumeratorLoweringBuilder 两处修正 | 产品代码修复 |
| C01 与源码内 ref 返回成员联动 | ref 局部站点按接收器四分類即可 | C01 同 pass 会把**源码内** `ref int this[]/Current` 重写为 ByRef 返回形态；ref 局部站点对源码定义的 ref 属性/索引器必须 holder 透传 `ByRef<T> local = access;`（数组/Span 等元数据成员仍走原四形态）。`IsSourceDefinedRefReturningMember` 以符号 `Locations.IsInSource` 判定 | RefOutInSyntaxRewriter.cs；链 fixture verify build/test 35/35 |
| ProjectTests 双跑隔离 | 同一暂存根内连跑两次 RunPassAsync | spine 枚举把首跑的中间树也编入导致重复类型（5485=2 等假象）；改为单跑/链跑各用独立 `StagePrivateFixture()` 副本 | ForeachSourceC40ProjectTests.cs |
| Cs2jByRef 支持类型随源转换 | 追加 ByRef.cs 仅用于符号解析 | 提交1 新增 ScopedByRef/ReadOnlyScopedByRef（ref struct）后，支持类型声明随转换会撞 ref-struct 守卫；转换器新增「Cs2jByRef 映射类型声明跳过」（支持类型由 Java compat 库提供）；TypeMappings 补 ScopedByRef.Create(2 参)→`ByRef.create`、ReadOnlyScopedByRef.Create(2 参)→`ReadOnlyByRef.create` | CSharpToJavaVisitor.cs；TypeMappings.json；ConversionTests 675/0 |
| 提交1 peer 测试语义 | peer 树作为源码树参与编译 | holder 透传口径下，peer 模拟的是**外部/元数据**成员（其 ref 索引器不被本 pass 重写）；harness 改为先把 peer 发射为内存 DLL 元数据引用（运行期 AssemblyResolve 桥接） | RefOutInRewriterUnitTests.cs |
| PassCatalog 排序 | — | RefOutIn `runAfterIds` 显式追加 foreach-source，保证串联顺序 | PassCatalog.cs |

### M3 验收实跑（本机，.NET SDK 10 / Roslyn 5.0.0，Release）

- `dotnet build CSharpToJavaConverter.slnx -c Release`：**0 error**。
- CSharpToJava.Tests：**2256 通过 / 5 失败**（总 2261）；5 个失败与白名单逐一点名一致：2 `EnumDeclarationRewriterUnitTests`（unsigned token）、2 `CSharpXmlCompileRegressionTests`、1 `SystemPrivateXmlRuntimeResourceTests`。
- CSharpToJava.ProjectTests：**805 通过 / 1 失败**（总 806）；唯一失败 `EqualityConcatProjectTests`（javac 缺 io.github.ningpp.compat，Java 环境族白名单）。
- CSharpToJava.ConversionTests：**675 通过 / 0 失败**（M2 基线 671 + M3 新增 4 冒烟）。
- foreach/RefOut/preprocessing 过滤：Tests 352/352、ProjectTests 99/99。
- C40 Core 单测 59/59；fixture 运行时 35/35；C40 project 测试 3/3（含链 verify build+test）。
