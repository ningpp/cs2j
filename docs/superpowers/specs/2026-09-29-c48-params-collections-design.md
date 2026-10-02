# C48 params 集合显式化（C# 13 params collections / params 调用点展开）预处理器设计

**日期**: 2026-09-29
**范围**: 在已实现的 E08 `strip-default` pass（`DefaultParameterStripper/`）内新增 **ParamsCollections 规则集**（零新 pass、零 stub 翻转、零新中间目录/适配器）：① 识别 C# 13 `params` 集合声明（八类合法类型），把调用点三形态（逗号列表 / 单个集合实参 / 空实参）归一为**显式集合构造实参**；② 经典 `params T[]` 与集合表达式 `M([1,2])`、`M(..span)` 组合时的面貌划界（C36 管表达式自身，C48 管 params 形参槽位）；③ 声明侧：`params T[]` **保留** params 修饰符（直映 Java varargs），其余 params 集合声明**物化为普通参数**并改写全部展开式调用点；④ 与 E08 默认参数补全在同一两遍范式内协作（`M(1, b: 2)` 具名实参）。本任务为只读调研 + 设计产出，**不实现代码、不注册/翻转任何 pass、不执行 build/test/git 写操作**。
**状态**: 设计待评审；硬前置 M0 Roslyn 探针（§9）；M1 不阻塞开工（自带最小发射；助手复用待 C24/C36 合入，见 §3.4/§11）
**Catalog**: C48（P2，分类 1.4 成员/调用点）[preprocessor-catalog.md L939-L951](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L939-L951)；E08 两遍范式 [L166-L174](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L166-L174)；C36 边界 [L764-L779](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L764-L779)；C06 Span 选型 [L348-L361](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L348-L361)；C52 隐式 Span 转换交界 [L997-L1010](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L997-L1010)
**注册表现状**: `strip-default` 已在 [PassCatalog.cs L123-L133](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L123-L133) 登记（Status=Implemented、defaultEnabled=true、Stage=Declarative、RunAfter=`[foreach-deconstruction]`、目录 `.cs2j-strip-default-src`）；[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 21 个 stub **无任何 params 条目**（已逐行核实）。C48 不新增 pass id；规则门控仅存在于 `DefaultParameterStripperOptions`，**默认关（opt-in）**
**base**: `10aae03f`（`Update c32 capture lift spec and add implementation plan`，已 `git rev-parse --verify` 校验存在，完整 SHA `10aae03f11c2dd579acbc4d8d7e331b14cfddae8`，2026-10-02 更新至 dev-3.x 最新提交）

---

## 1. 背景与目标

### 1.1 语言差：Java varargs 的末位一维数组限制

Java 可变实参（varargs）只接受方法末位、一维数组形态的 `T...`，且 javac 把 `T...` 编译为 `T[]` 形参。C# 的 `params` 在 C# 13 之前同为末位一维数组（天然可直映）；C# 13 起 params 形参类型扩展为整个「集合模式」家族（官方 params 关键字页，catalog 六次补全已核对）：

1. 一维数组 `T[]`（经典形态，多维数组非法）；
2. `System.Span<T>` / `System.ReadOnlySpan<T>`（ref struct）；
3. 带 `[CollectionBuilder]` 的类型（如 `ImmutableArray<T>`、`FrozenSet<T>`，create 方法形参为 `ReadOnlySpan<T>`）；
4. 支持集合初始化器的类型：实现 `IEnumerable<T>` 且具有可访问的无参构造与可访问实例 `Add`——`List<T>` 即经此规则合法；**`StringBuilder` 不合法**（无 `Add`，只有 `Append`，且不实现 `IEnumerable<T>`）；
5. 五个接口：`IEnumerable<T>`、`IReadOnlyCollection<T>`、`IReadOnlyList<T>`、`ICollection<T>`、`IList<T>`。

目标转译基线 .NET 10 BCL（Collections/Text/Xml）公共 API 已使用 `params ReadOnlySpan<T>`（catalog C48 证据行）；D:\csharp-sdk 的 .NET 8/9 时代源码预期低频，命中率需画像计数（§8）。

### 1.2 调用点三形态（官方语义）

| 形态 | 源码例（`void M(params int[] a)`） | 编译器行为 |
|---|---|---|
| A 逗号列表（展开式） | `M(1, 2, 3)` | 合成集合承载全部元素；对数组即合成 `new int[]{1,2,3}` |
| B 单个集合实参（正常式） | `M(existing)`、`M(new[]{1,2})` | **不展开、不复制**，同一引用直接绑定（identity 保持） |
| C 空实参 | `M()` | 合成空集合（数组形态为空数组） |

### 1.3 仓库现状证据（2026-09-29 只读核实）

**Roslyn 版本与 C# 13 支持（先决项，结论：可行）**：

- [CSharpToJava.Core.csproj L12-L14](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12-L14) 引用 `Microsoft.CodeAnalysis.CSharp` / `.CSharp.Workspaces` / `.Workspaces.MSBuild` 均为 **4.12.0**；工程 TFM `net10.0`（[L4](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L4-L4)），无 `Directory.Packages.props`、main 工程无 `LangVersion` 钉死。
- Roslyn 4.12.0 为 Visual Studio 2022 17.12 同期包（微软 Roslyn 版本对照表已核），即 **C# 13 RTM 编译器**；params collections 为 C# 13 正式特性，4.12 下 parse、bind、IOperation 全支持。C# 14 语法（extension 块等）在 4.12 需 preview，与本条无关（归 C49/C52）。
- **API 外形实证**（本机 NuGet 缓存 `microsoft.codeanalysis.common/4.12.0/lib/net7.0/Microsoft.CodeAnalysis.xml` 逐条核对）：
  - `IParameterSymbol.IsParams`：文档原文「true if declared as a parameter array **or as a parameter collection**」——数组与集合都为 true；
  - `IParameterSymbol.IsParamsCollection`：**4.12 新增**，仅 params 集合为 true（数组为 false）——声明分类的权威判据；
  - `Operations.ArgumentKind.ParamArray`：编译器为 params 数组合成的实参，「an array creation expression that encapsulates all the elements, if any」；
  - `Operations.ArgumentKind.ParamCollection`：**4.12 新增**，编译器为 params 集合合成的实参，「a collection expression that encapsulates all the elements, if any」。
- 解析选项现状：主转换器 [ProjectCompilationBuilder.cs L30](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L30-L30) 用 `LanguageVersion.Latest`；strip-default 适配器 [StripDefaultPassAdapter.cs L54-L65](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/StripDefaultPassAdapter.cs#L54-L65) 用 `new CSharpParseOptions(preprocessorSymbols: …)`（未显式指定版本，4.12 下默认 latestMajor = C# 13）。**M0 探针**：用该实际选项 parse+bind `params ReadOnlySpan<int>` 夹具确认零错误；若任何宿主路径下默认版本不足，则只增 `WithLanguageVersion(LanguageVersion.Latest)`（不实跑，列入 M0）。

**params 现状路径（Java varargs 直透，非数组声明必错）**：

| # | 站点 | 现状行为 | 后果 |
|---|---|---|---|
| P1 | [MethodTransformer.cs L148-L149](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L148-L149)、[ConstructorTransformer.cs L95-L98](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs#L95-L98)、[DelegateTransformer.cs L196](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/DelegateTransformer.cs#L196-L196) | 见 `ParamsKeyword` 即置 `JavaParameter.IsVarArgs = true`，**不看参数类型** | `params IEnumerable<T>`/`params Span<T>` 产出 Java `Iterable...`/`Span...` 式非法 varargs（非一维数组类型），javac 拒绝 |
| P2 | [ArgumentTransformer.cs L76-L78](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs#L76-L78) | 溢出位置实参全部映射到末位 params 形参逐参 coerce | `M(1,2,3)` 靠 Java varargs 逗号直透恰好工作；单集合实参/集合类型无构造逻辑；全 Core grep `IsParamsCollection/ParamCollection` **零命中**（4.12 新 API 无任何消费方） |
| P3 | [DefaultParameterSignature.cs L22,L53](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterSignature.cs#L22-L53) | E08 签名目录已录 `bool[] IsParams`，但仅用于「params 槽可省略」（[Rewriter L627,L717](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs#L717-L717)） | params 从不被物化；[OverloadBuilder.cs L363-L373](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterOverloadBuilder.cs#L363-L373) 转发 params 空槽时只认 `ArrayTypeSyntax` 发空数组，非数组形态无分支 |
| P4 | 测试盘点 | glob `**/*[Pp]arams*` 零文件；经典用例仅 [MethodParameterTests.cs L14/L21](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/MethodParameterTests.cs#L11-L23) 两个（`params int[]/string[]` 声明直映）；E08 组合仅 [DefaultParameterStripperTests.cs L231](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DefaultParameterStripperTests.cs#L231-L231)（`int level = 0, params object[] args`，params 保持省略）；全 tests grep `params (ReadOnlySpan|Span|IEnumerable|IList|ICollection|IReadOnly|List<)` **零命中** | 非数组 params 集合零护栏；声明/调用点显式化零断言 |

### 1.4 目标与非目标

**目标**：预处理后的中间树是普通、可编译、可在 .NET 上直接跑测试的 C#，且——

1. 每个非数组 `params` 声明物化为普通参数（`params` 修饰符剥离），其全部展开式调用点（形态 A/C）显式构造与形参类型同构的集合；形态 B 原样直传（保持同一引用）；
2. 经典 `params T[]` 声明**保留**（Java varargs 直映不变）；其逗号调用点是否同样显式包 `new T[]{}` 由子规则门控（v1 默认不展开数组类，§2.4 决策 D8 / 开放问题 Q1）；
3. 一切目标类型、展开与否、重载归属以 SemanticModel/IOperation 为唯一真相源，禁止按语法名与实参数目猜测；
4. 无法等价显式化即显式诊断 + 严格模式失败，禁止注释/占位/静默直透（foundation plan §横切规则）。

**非目标**：本期不改任何 C#→Java 转换器代码（[ArgumentTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs) 等热文件只登记、不改写，见 §11）；不新增支撑工程（产物仅 BCL 数组/`List<T>`/Add/CollectionBuilder 工厂调用）；`stackalloc`/Span 栈生命周期归 C06；集合表达式自身去糖归 C36；对象/集合初始化器去糖归 C24；C# 14 隐式 Span 转换的重载面归 C52。

---

## 2. 关键设计决策表

### 2.1 ★合法类型 × 三形态构造矩阵（PC-TARGET）

形参类型分类以 `IParameterSymbol.IsParamsCollection` + 类型符号判定（不认类型文本）。接口→具体类映射与 C36 §2.2 同一张规范镜像表，M0 探针逐行核实，重编译为最终裁决。

| # | 形参类型（合法八类） | 形态 A：`M(1,2,3)` | 形态 C：`M()` | 形态 B：`M(c)` |
|---|---|---|---|---|
| K1 | 一维 `T[]`（经典 params；含泛型 `T[]`） | `M(new T[]{ 1, 2, 3 })`（子规则门控，见 D8） | `M(new T[]{})`（或 `Array.Empty<T>()`，见 R5） | **直传不改**（实参为同型数组/隐式可转） |
| K2 | `Span<T>` / `ReadOnlySpan<T>` | v1：`M(new T[]{ 1, 2, 3 })`——`T[]`→Span 的**隐式引用转换自 System.Memory 起存在**（非 C#14 新转换），4.12 可编译；v2 视 C06 选型可显式 `System.MemoryExtensions.AsSpan(new T[]{…})`（默认不取，冗余且与编译器 lowering 不一致） | `M(new T[]{})`（空数组同路径隐式转换；Info 留痕 C06 收口） | 直传（实参已是 Span；数组实参由编译器隐式转，不另包一层） |
| K3 | 带 `[CollectionBuilder]` 的类型 | **v1 显式报码 CS2J0607**；M2：`M(BuilderType.Create(new T[]{ 1,2,3 }))`，工厂方法符号取 IOperation 绑定（与 C36 CE-10 共用分析结论） | M2：`M(BuilderType.Create(new T[]{}))`（或工厂绑定的空态写法，以探针为准） | 直传 |
| K4 | 可初始化器具体类型：实现 `IEnumerable<T>` + 无参 ctor + 可访问实例 `Add`（**`List<T>` 走此列**；含 `HashSet<T>`、用户集合） | `M(new C<T>(){ 1, 2, 3 })`（集合初始化器，语义 = 构造后逐 Add；Add 绑定 M2 前按可见性规则、M2 起按 IOperation，复用 C24 助手） | `M(new C<T>())` | 直传（★同一引用，不复制） |
| K5 | `IEnumerable<T>` / `IReadOnlyCollection<T>` / `IReadOnlyList<T>` / `ICollection<T>` / `IList<T>` | `M(new List<T>(){ 1,2,3 })`（规范具体类 = `List<T>`，可隐式转五接口；复用 C36 T5 表） | `M(new List<T>())` | 直传（实参任意兼容实现；**不**归一为 List） |
| K6 | ref struct 元素的 Span（如 `Span<SomeRefStruct>`） | 无法以数组承载 → **CS2J0608**（指路 C06 D9 白名单，不发明栈缓冲） | 同左报码 | 直传 |
| K7 | 表外类型 / 不满足任一合法条件 | 编译器在基线即拒绝（可编译输入不可达）；防御性 CS2J0601 + 不改写 | 同左 | 同左 |
| K8 | `StringBuilder`（**显式排除验证项**） | 基线编译错误（无 Add、非 `IEnumerable<T>`）；夹具断言规则**不设名单特判**——合法性完全由 SemanticModel 分类得出，K7 自然兜住 | —— | —— |

非泛型 `System.Collections.IEnumerable` 上的 `params`（无元素类型）不属于 C# 13 合法集（params 集合要求 `IEnumerable<T>` 模式），基线即拒，归 K7。

### 2.2 ★实参形态判定（PC-ARG，IOperation 唯一真相源）

对每个 invocation/object-creation/constructor-init 节点取 `GetOperation(node)`（`IInvocationOperation` / `IObjectCreationOperation`），以 `TargetMethod` 为编译器**已完成重载决议**的结果；逐 `IArgumentOperation` 读 `ArgumentKind`：

| ArgumentKind（4.12 实证） | 含义 | C48 处置 |
|---|---|---|
| `Default`（正常式，含单个集合实参、具名集合实参 `a: c`） | 用户显式给了一个绑定到 params 槽的实参 | **原样直传**，绝不重新包装；这是防双重包裹（R2）与保引用语义（D5）的关键 |
| `ParamArray` | 编译器为 `params T[]` 合成的展开实参（承载逗号段或空） | K1 列构造；子规则门控（D8） |
| `ParamCollection` | 编译器为 params 集合合成的展开实参（承载逗号段或空，Value 为合成 collection expression operation） | K2/K4/K5 列构造；合成 operation 给出目标类型，但**元素语法仍取自源码 ArgumentList**（合成 operation 无源码Span，不可直接 Lower） |
| 其他（`NameColon`/`DeclaredArgument` 等配套位） | 具名/声明式实参的附属标记 | 跟随所属实参的上述判定 |

展开段元素的语法来源 = 节点 `ArgumentListSyntax.Arguments` 中位置下标 ≥ 固定参数数（并越过已具名落槽实参，槽位算法复用 E08 `TryComplete` 的 slots 模型 [L691-L760](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs#L691-L760)）。IOperation 与槽位算法结论不一致时以 IOperation 为准并记 CS2J0610（防御，不改写）。

### 2.3 声明侧三策略与结论（PC-DECL）

| 策略 | 适用 | 结论 |
|---|---|---|
| A. **保留 `params T[]`** | K1 经典数组声明 | **推荐/采用**：下游 [MethodTransformer.cs L149](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L149-L149) 直映 Java 末位 `T...`，零转换器改动；显式 `new T[]{}` 实参在 Java varargs 位是「精确类型数组实参」，javac 不再二次包装，语义一致 |
| B. **剥离 params，物化为普通参数** | K2/K3/K4/K5 全部非数组 params 集合 | **采用**：删 `params` 修饰符（`ParameterSyntax.WithModifiers` 去 ParamsKeyword），形参类型不变；该声明所有展开式调用点必须同步显式化（跨文件签名目录保障，同 E08 Collect 遍）；剥离后 P1 站点不再产出非法 Java varargs |
| C. 统一剥离全部 params（含 K1） | —— | **否决**：`Console.WriteLine(string, params object[])` 等海量经典声明与 Java varargs 天然对应，全剥只会平白要求每个调用点显式数组且使 Java 侧丢失 varargs 外形；与「最小物化」原则相悖 |

一致性要求（策略 B 的连带面，M1 实现/夹具覆盖）：

- **override / 接口实现 / 显式接口实现 / partial 两半 / base 调用**：params 集合在 IL 元数据带 `[System.Runtime.CompilerServices.ParamCollectionAttribute]`，源码侧 `params` 不是签名差异但影响调用合法性。Catalog Collect 遍按符号（既有 [DefaultParameterCatalog.EnumerateLookupSymbols L244-L253](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterCatalog.cs#L244-L253) 仅沿 `OverriddenMethod` 链向上遍历；接口实现面需在实现时扩展 `AllInterfaces`/`ExplicitInterfaceImplementations`）把同一 virtual 槽位的所有声明一并物化，避免「基类剥离、派生类保留」造成中间树跨程序集调用决议漂移；
- 剥离后构造函数链（` : this(…)` / ` : base(…)`）展开式调用同样改写；4.12 资源串存在 `ERR_ParamsCollectionInfiniteChainOfConstructorCalls`/`ERR_ParamsCollectionConstructorDoesntInitializeRequiredMember`/`ERR_ParamsCollectionMissingConstructor`，提示 ctor 场景有特殊规则，M0 探针核实并入夹具；
- **元数据丢失登记**：剥离 `params` 后中间程序集不再带 `ParamCollectionAttribute`，反射可见性变化（同 E08 用 `[DefaultParameterValue]` 保元数据的思路）——v1 不补发该 Attribute（其构造签名/可访问性需探针确认），作为开放问题 Q3 与 Java 侧反射消费方联合评估；
- `params` 非末位（CS0231）、表达式树内 params 集合（4.12 有 `ERR_ParamsCollectionExpressionTree`，同 C36 CS9175 不可达族）、`ERR_ParamsCollectionExtensionAddMethod`（Add 扩展方法非法）均属基线编译错误，防御性识别 + CS2J0601 计数，不做功能支持。

### 2.4 其余决策点

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| D1 | 重载决议 `M(int x)` vs `M(params int[] x)`：`M(1)` 选前者（正常形参优先于展开形参，C# 规范） | **不自行复刻规范**：直接用 `IInvocationOperation.TargetMethod`；重写只改变「被选中重载」的实参外形，且 `new T[]{…}` 正常式绑定不会漂移到其他重载（数组类型实参不适用零元素形参） | 防 ★重载误判（R1）；符号解析失败（NuGet 缺失）时**不做**名称/元数猜测，记 CS2J0602 跳过该站点（E08 语法回退仅限具名实参场景，[L786-L802](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs#L786-L802)，不复制其猜测面到 params） |
| D2 | 空实参 `M()` 数组形态 | K1 发 `new T[]{}`；K2 同；是否改发 `Array.Empty<T>()` 留 M0 探针（现代编译器空 params 可能复用单例，同一性可观察差异，R5） | v1 统一 `new T[]{}` 与合成 ParamArray 文本同形、重编译零风险 |
| D3 | 单个集合实参的引用同一性 | 形态 B 一律零改：`M(existingList)` 直传；callee 对形参的 Add/Remove 作用于调用方同一对象——这正是 C# 语义（K4 列 ★值语义注记）；禁止防御性复制/ToSnapshot | 只有逗号/空形态由编译器（与本规则）构造新集合 |
| D4 | `M([1,2])` / `M(..span)` 集合表达式组合 | C48 视角：`[1,2]` 是用户显式给入 params 槽的**单个实参**（ArgumentKind=Default，非 ParamArray/ParamCollection）→ **直传不触达**；该实参的去糖完全归 C36（`[1,2]`→`new[]{1,2}`、spread→AddRange/foreach）。C36 产物到达 C48（若同链且 C36 在先）时仍是普通数组实参，继续直传 | 与 C36 §2.4「params 实参位」行互锁：[c36 L109](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c36-collection-expr-design.md#L109-L109)；交汇规则是 IOperation 种类判定，**与 pass 顺序无关**（双保险，见 §3.3） |
| D5 | 具名实参与默认参数组合 `M(1, b: 2)`、`M(1, b: 2, 3)` | 同 pass 单遍 rewriter 内固定顺序：**先 E08 默认补全/具名落槽，后 C48 params 包裹**（§3.2）；具名落到 params 槽的单个集合实参（`a: existing`）按 D4 直传；具名后再跟逗号位置段在基线非法（CS 命名顺序），不可达 | 复用 slots/`IndexOfName` 模型；[DefaultParameterSignature.IsParams](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterSignature.cs#L22-L22) 已让补全跳过 params 槽，行为不变 |
| D6 | `scoped`/`in`/`ref readonly` 修饰的 Span 形参 | params 集合隐含 `scoped` 值语义；`in`/`ref` 与 params 组合基线受限；C48 只发数组实参不管理 scoped token（C01 已剥离参数侧 `scoped`，C36 0586 盘点口径一致） | 可写 `Span<T>`（K2 非 ReadOnly）：逗号展开的 `new int[]{}` 是新堆数组，方法内写入作用于该数组——与 C# 编译器展开语义一致，夹具断言 |
| D7 | 泛型 params：`void G<T>(params T[] a)`、`params Span<T>`、`params IEnumerable<T>`、`IEqualityComparer<T>` 风格 builder | `new T[]{…}`/`new List<T>(){…}` 对类型形参合法；K3 泛型 builder 的工厂绑定探针核实后 M2 支持，v1 报 0607 | 泛型夹具含 `T = int/struct/引用类型` 三档半区运行对照 |
| D8 | **K1 逗号调用点子规则门控** | v1 新增独立开关 `ExpandArrayCallSites = false`：默认只物化 K2–K5（Java 不可直映族），K1 声明保留且其逗号调用点维持逗号（P2 Java varargs 直透路径继续兜底）；开关打开后 K1 逗号/空形态同样显式化 | 灰度与回滚粒度；默认开启门槛（§8）通过后再翻 true（开放问题 Q1） |
| D9 | 委托/局部函数/索引器运算符 | params 合法于 delegate、local function（同 E08 覆盖的声明种类全集）；索引器/运算符不可声明 params（与默认参数同限制）；事件/属性不适用 | Collect 声明种类沿用 [DefaultParameterCatalog.Collect L64-L90](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterCatalog.cs#L64-L90) |
| D10 | 幂等 | 产物中：非数组 params 声明数 = 0（豁免配对除外）、二次 Strip 的 `Changed=false`；K1 展开开启后输出树无 ParamArray 形态调用（语义复核，非纯文本） | residual 闸门 PC-10 |

---

## 3. 链位与归属契约

### 3.1 ★归属论证：作为 E08 已实现 pass 的规则扩展，零 stub（推荐）

| 候选 | 评估 | 结论 |
|---|---|---|
| **甲. 并入 `strip-default`，pass 内新增 ParamsCollections 规则** | 两遍范式（Collect 跨文件签名目录 → Strip 声明 + 改调用点）与 C48 完全同构；catalog L942/L947 明示「可并入同一通道共享两遍范式」；[DefaultParameterSignature](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterSignature.cs) 已携带 `IsParams` 与按符号/按名双解析目录；调用点改写已覆盖 invocation/object-creation/ctor-init/primary-base/attribute 全节点种类；适配器两遍接线（[CompilationReadyAsync L67-L82](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/StripDefaultPassAdapter.cs#L67-L82) Collect、[RewriteFileAsync L84-L116](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/StripDefaultPassAdapter.cs#L84-L116) Strip+Verify）可直接挂第三职责；**零 stub 翻转**（pass 已 Implemented），规则受 Options 门控默认关，对现有行为零影响 | **推荐** |
| 乙. 新建 stub pass（如 `params-collections`） | 需新增 descriptor/id/目录/适配器/CLI verb/指纹令牌；与 strip-default 同时改调用点会产生两遍 Collect、两遍 Format、两遍 verify build，且两条规则都要消费同一 `IInvocationOperation`；foundation plan stub 表无此条目，新增违反「注册表是活清单但 C48 明确建议并入」 | 否决 |
| 丙. 放入 expression-desugar 族（C23/C36 通道） | 该族是表达式自底向上去糖，**无跨文件签名目录阶段**；声明侧物化（策略 B）与 override 一致性需要整树 Collect；且该族当前仍是 stub（未翻转），C48 不该被其节奏绑定 | 否决 |

门控方式：规则**默认关**，挂在已默认开的 `strip-default` pass 下——pass 执行但规则短路（dirty 扫描无 params 集合时零成本），不产生独立中间目录与指纹面。

### 3.2 同 pass 内规则顺序与目录组织

两遍时序（全部在现有 pass 生命周期内，无新 pass 阶段）：

1. **Collect（CompilationReadyAsync，整 compilation 一次）**：`DefaultParameterCatalog.Collect` 扩展登记 params 槽元数据（§4 PC-1）；E08 原有默认值登记逻辑不变；
2. **Strip/Rewrite（逐文件一次 rewriter）固定规则序**：
   - R-a 声明处理：E08 默认值剥离/重载生成（现状）→ C48 声明物化（策略 B：非数组 params 去修饰符）；同一 `ProcessMethod/ProcessConstructor/ProcessLocalFunction/…` 出口后追加，**先生成 E08 转发重载再物化 params**（转发重载体内的空 params 转发 [L363-L373](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterOverloadBuilder.cs#L363-L373) 需同步识别非数组形态：空集合发 `new List<T>()`/builder 空态而非空数组，PC-7）；
   - R-b 调用点：`VisitInvocationExpression/VisitObjectCreationExpression/VisitConstructorInitializer/VisitPrimaryConstructorBaseType` 内先跑 E08 `TryComplete`（补默认、具名落槽），其结果实参表再交 `ParamsArgumentRewriter` 做一次槽位/IOperation 判定与包裹；两规则在同一 Visit 内串联、共享 `Changed`/stats，不新增 SyntaxRewriter 轮次（避免中间节点二次绑定）；
3. **Verify（pass 3）**：[DefaultParameterVerifier](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterVerifier.cs) 后追加 PC-10 residual 自检（§4）。

目录：新增代码全部落独占子目录 `src/CSharpToJava.Core/DefaultParameterStripper/ParamsCollections/`（命名空间 `CSharpToJava.Core.DefaultParameterStripper.ParamsCollections`），E08 既有 10 个文件仅做**追加式**接缝改动（§4.3 所有权表），不重写其现有逻辑。

### 3.3 与 C36 的交汇：顺序与双重包裹规避

- 链位事实：`expression-desugar`（C23/C24/C35/C36 族）锚点 RunAfter=`null-coalesce`（[PassStubCatalog.cs L224](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L224-L224)），拓扑上先于 `strip-default`（其锚点链 foreach-deconstruction 更靠后）；stub 翻转后族 pass 先跑。
- **交汇规则（顺序无关，双保险）**：
  1. C36 先跑：`M([1,2])` → `M(new int[]{1,2})`；C48 经 IOperation 见到该实参 ArgumentKind=Default（单实参正常式）→ 直传，**不包第二层**；`M(1,2,3)` 不含集合表达式，C36 不触达，C48 正常展开；
  2. 若 C48 先跑（防御性假设）：`M([1,2])` 中集合表达式仍是**用户显式单实参**（ArgumentKind=Default）→ C48 同样直传；即包裹判定只认 `ParamArray/ParamCollection`，语法上是否 `CollectionExpressionSyntax` 完全不参与——结构上杜绝双重包裹；
  3. C48 v1 对「ParamCollection 合成实参的元素中仍嵌 `CollectionExpressionSyntax/SpreadElementSyntax`」（理论上只在 C36 未跑时出现，如 `M([..s])` 整体被当成展开段的边缘写法）发 **CS2J0611 Info/Warning** 指路 C36，不自行解 spread（避免与 C36 双轨）；探针期核实该边缘是否可达。
- K4/K5 的 `new List<T>(){…}` 与 C36 产物同构（[c36 §3.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c36-collection-expr-design.md#L136-L141)），但 C48 发生在 strip-default 遍、面对的是实参列表新节点，不会回流 C24/C36（它们已执行完）。

### 3.4 RunAfter 依赖核对

- C48 不新增 pass/锚点；`strip-default` 现有位置（foreach-deconstruction 之后、ref-out-in 之前，[PassCatalog L123-L145](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L123-L145)）满足所有顺序需求：params 物化不依赖 tuple/foreach；ref-out-in 在其后，scoped 剥离不影响本规则（D6）；
- **C06（unsafe-ffm）是语义依赖不是链位依赖**：K2 v1 数组形态只依赖「`T[]`→Span 隐式转换」这一 BCL 既有事实，不要求 C06 落地；要求 C06 最终 Span 选型与本规则同形（堆数组包装），c06 设计已持此形态（C36 §11 同口径）；
- C24/C36 助手为**代码复用依赖**（Add 发射、TargetTypeResolver），两者未合入时 C48 M1 在 `ParamsCollections/` 内自带最小实现（K1/K2/K5 + 空态只需数组与 `new List<T>(){}`，不依赖语句化/TempLifter——params 实参位是零临时表达式位），C24/C36 合入后只增提取共用（开放问题 Q4）。

---

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/DefaultParameterStripper/ParamsCollections/`。

| 文件 | 职责 |
|---|---|
| `ParamsCollectionsOptions.cs` | `ParamsCollectionsOptions(bool Enabled = false, bool Strict = true, ParamsCollectionsScope Scope = All, bool ExpandArrayCallSites = false, CollectionBuilderPolicy Builder = Error)`；`Scope { All, Span, MutableCollection, Interface, CollectionBuilder }` 逐类灰度；`Builder { Error, Emit }` v1=Error；`ExpandArrayCallSites` = D8 子开关 |
| `ParamsModels.cs` | `ParamsParamKind { Array, Span, Builder, Initializable, Interface, Unsupported }`；`ParamsSitePlan`（节点、`IMethodSymbol` 目标、params 槽 index、形参类型符号、Kind、规范具体类、展开段元素语法表、形态 A/B/C、Outcome）；`ParamsDeclPlan`（声明节点、符号、策略 A/B、关联 override/interface 符号集） |
| `ParamsSignatureCollector.cs` | Collect 遍：对 E08 已登记的方法/ctor/local/delegate/primary-ctor 符号读末位参数 `IsParams`/`IsParamsCollection`（4.12 实证 API），分类 Kind 与元素类型 `T`；建立 `IMethodSymbol → ParamsDeclPlan`（含 virtual 槽位族：override/接口实现归组）；名称/元数回退仅用于 B 直传不做展开（D1） |
| `ParamsArgumentRewriter.cs` | 调用点核心：取 `IInvocationOperation`/`IObjectCreationOperation`（ctor-init 取 `IConstructorBodyOperation` 上游调用 operation）；逐 `IArgumentOperation` 判 ArgumentKind（§2.2）；槽位对齐复用 E08 slots 算法；产出 Outcome（Wrap/Passthrough/Skip+diag）；具名/默认组合按 D5 与 E08 串联；不改 Attribute（params 集合在特性非法，K1 特性也只能是常量数组，现状 E08 已处理） |
| `CollectionConstructionEmitter.cs` | 按 PC-TARGET 矩阵发射**零临时表达式**：`ArrayCreationExpression`+initializer（K1/K2/K3 数组输入）、`ObjectCreationExpression`+`CollectionInitializerExpression`（K4/K5，初始化器即逐 Add）、空态；只读 BCL 形状；K2 不发 `AsSpan`（依赖隐式转换）；Add 绑定在 C24 助手可用前用「可访问实例 Add」可见性判定兜底并计数，M2 切 IOperation 绑定（同 C24 CS2J0533 纪律：不猜 add/put） |
| `ParamsDeclarationMaterializer.cs` | 策略 B：去 `ParamsKeyword` 修饰符；virtual 槽位族一致性；与 E08 `StripDefaults`/`[DefaultParameterValue]`/`[JsonConstructor]` 改动合流到同一参数/成员输出；K1 不动 |
| `ParamsOverloadForwarding.cs` | E08 转发重载内 params 空槽的非数组形态（PC-7）：替换 [OverloadBuilder L363-L373](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterOverloadBuilder.cs#L363-L373) 仅认 `ArrayTypeSyntax` 的分支为按 Kind 分派（数组空数组 / List 空 ctor / M2 builder 空态） |
| `ParamsTargetClassifier.cs` | 类型符号分类：数组（一维校验）、Span（符号全名 `System.Span`/`System.ReadOnlySpan`）、CollectionBuilder 特性读取（`System.Runtime.CompilerServices.CollectionBuilderAttribute` ctor 参数 = builder 类型 + 工厂名；M2 经 IOperation 取实际绑定方法）、五接口、初始化器模式（IEnumerable\<T\> + 可访问无参 ctor + Add）、ref struct 元素 K6；输出 Kind + 规范具体类 |
| `ParamsResidualVerifier.cs` | PC-10：输出树重 parse + 语义复核（在 pass 既有 verify 阶段）：非数组 `params` 修饰符残留必须有 skip 诊断配对；幂等第二次 Strip 无 diff；K1 展开开启时无未处理 ParamArray 站点 |
| `ParamsDiagnostics.cs` | CS2J0600–CS2J0619 常量与消息模板（§6） |
| `ParamsStatistics.cs` | 见 §5 统计字段 |

接缝（追加式，只增不改现有行为，默认关时短路）：

- [DefaultParameterStripperOptions.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripperOptions.cs) record 增可空/默认值属性 `ParamsCollections? ParamsCollections = null`（null/disabled = 零行为）；
- [DefaultParameterStripper.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterStripper.cs)：`Collect` 后调 `ParamsSignatureCollector`；`Strip` 构造 rewriter 时注入 plan 目录；`IsDirty` 增 params 集合快扫（`ParamsKeyword` + 末位非数组类型语法粗判，语义精判留给 rewriter）；
- [DefaultParameterDeclarationRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs)：R-a/R-b 两个挂点各数行委托调用。

### 4.1 数据流

1. Collect：E08 catalog 登记 → ParamsSignatureCollector 分类建目录（含 virtual 族归组）；
2. 逐文件 dirty：无 params 关键字文件字节级复制（现状路径，零成本）；
3. Rewrite：声明成员出口（R-a）→ 调用点 Visit 内 E08 TryComplete → ParamsArgumentRewriter（R-b）；严格模式任一 Error → 整文件原子不改写（同 E08/族入口纪律）；
4. Format（沿用现有 `AdhocWorkspace`）；
5. Verify：E08 默认值 residual + PC-10 params residual；
6. 干净/改写统计汇入适配器现有 MergeStatistics 模式。

### 4.2 规则全集（PC-1 ~ PC-10）

- **PC-1 签名采集与分类**：`IsParamsCollection` 八类分 Kind；不支持/表外 → 0601；
- **PC-2 Span 逗号/空展开（K2）**：数组形态 + 隐式转换 + Info 留痕（0604）；ref struct 元素 → 0608；
- **PC-3 可初始化器集合展开（K4，含 List）**：`new C<T>(){…}`/空 ctor；Add 不可解析 → 0606；
- **PC-4 五接口展开（K5）**：规范具体类（List）选择，复用 C36 映射结论；
- **PC-5 数组类门控（K1）**：默认仅声明保留、调用点不动（D8 关）；开启后逗号/空显式化；形态 B 永远直传；
- **PC-6 单集合实参直传**：ArgumentKind.Default 零改（同一性，D3）；具名单实参同；
- **PC-7 E08 协作**：默认参数/具名/转发重载空槽按 Kind 补形态（D5、§3.2）；
- **PC-8 声明物化（策略 B）**：非数组 params 去修饰符 + virtual 族一致；元数据丢失登记（Q3）；
- **PC-9 CollectionBuilder**：v1 0607；M2 经 IOperation 发 `Builder.Create(…)`；
- **PC-10 residual/幂等**：非数组 params 残留配对、二次运行零 diff、重编译通过。

### 4.3 文件所有权表

| 文件/目录 | 所有者 | C48 行为 |
|---|---|---|
| `DefaultParameterStripper/ParamsCollections/*` | **C48 独占** | 全部新增代码 |
| E08 既有 10 文件（Catalog/Signature/Stripper/Rewriter/OverloadBuilder/Options/Verifier/Statistics/Result/Diagnostics） | E08 维护者 | 仅追加接缝（options 属性、两个委托挂点、IsDirty 一个析取支、空槽分派一个虚点）；默认关时 diff 行为零变化；既有 E08 测试不许改一例 |
| `Transformers/**`（Method/Constructor/Delegate/Argument） | 转换器维护者 | **本期一行不改**；P1 盲目标 IsVarArgs 缺陷登记为后续转换器 PR（§8-3），中间树物化后该路径对非数组声明不再触达 |
| C24 `Initializers/*`、C36 `CollectionExpr/*` | C24/C36 | 不触碰；助手未合时自带最小实现（Q4） |
| CLI 注册表（PassCatalog/PassStubCatalog/PassChainFactory 骨架） | 平台 | 不加 id/不改 RunAfter；仅 options 透传与统计/摘要接线（§5） |

---

## 5. CLI 适配器与集成

沿用 `StripDefaultPassAdapter`（[Adapters/StripDefaultPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/StripDefaultPassAdapter.cs)），无新适配器/verb/目录：

1. **选项接线**：
   - `PassChainFactory.CreateAdapter` 中 strip-default 分支（[L168-L174](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L168-L174)）的 `new DefaultParameterStripperOptions()` 增填 `ParamsCollections` 子节，值来自 `ConvertProjectOptions` 新增 `--enable-params-collections`（默认 false）与可选 `--params-collections-scope`/`--params-collections-expand-arrays`（灰度用，long option 风格对齐现有 `--no-*` 族，[Program.cs L4752-L4764](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4752-L4764)）；
   - 独立 verb `strip-default` 的 [StripDefaultOptions L4394-L4430](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4394-L4430) 增同名选项，在 [Program.cs L3999-L4003](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3999-L4003) 构造 options 处透传；
   - 通用 `--disable-pass strip-default` 仍整体关闭本规则（规则不独立于 pass 运行）；规则关闭 = pass 行为与今日逐字节一致；
2. **ParseOptions**：M0 探针结论若需要，适配器 `CreateParseOptions` 只增 `WithLanguageVersion(LanguageVersion.Latest)`（现状未指定版本，4.12 默认已支持 C#13，预期零改动）；
3. **统计字段**（`ParamsStatistics`，并入现有 `DefaultParameterStripperStatistics` 合并流，沿用 [MergeStatistics L224-L237](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/StripDefaultPassAdapter.cs#L224-L237) 模式）：`ParamsSites`（声明，按 Kind 分桶 Array/Span/Builder/Initializable/Interface）、`ParamsCallSitesExpanded`（形态 A）、`ParamsCallSitesEmpty`（C）、`ParamsCallSitesPassthrough`（B）、`ParamsDeclarationsMaterialized`（策略 B）、`ArrayCallSitesExpanded`（D8 子规则）、`SpanAsArraySites`、`BuilderSitesBlocked`、`SkippedByReason: Dictionary<code,int>`、`ResidualCount`；
4. **摘要行**：[BuildChainSummaryLine L150-L154](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/StripDefaultPassAdapter.cs#L150-L154) 追加（规则启用时才出现）：`; params: <decl> decl (span <s>/builder <b>/init <i>/iface <f>), calls expanded <A>/empty <C>/passthrough <B>, materialized <M>, skipped <Skip>, residual <R>`；
5. **诊断出口**：复用 `DefaultParameterStripDiagnostic` 通道（severity + message），消息带 CS2J06xx 码与相对路径；`OnBuildFailed/OnTestsFailed` 现有文案自动覆盖（中间树重编译/测试是本规则的硬验收）；
6. **指纹/支撑库**：无新支撑库；pass 令牌已随 strip-default 生成（foundation plan §指纹），规则开关变化在 pass 已开场景下建议进 options 指纹串（实施期确认现有指纹是否含 options——若不含，开关切换后用 force 重跑，登记为小限制）；
7. **--list-passes**：不新增行；在 strip-default 描述说明中登记 catalogItems「E08, C48」（描述符当前未设 catalogItems 参数，实施期可选追加，不为此改 descriptor 形状）。

---

## 6. 诊断策略

第一原则：非数组 params 在 Java 侧必产出不可编译代码（P1），本规则结局只有「显式物化」或「带码失败/跳过配对」；严禁静默直透。码段 **CS2J0600–CS2J0619**（已核对 docs/superpowers/specs 全目录：CS2J05xx 为 expression-desugar 族预留段（C23 0501–0599），06xx 无任何占用；MSBuild 风格）：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J0600 | Info | params 站点盘点（声明/调用，按 Kind 分桶，verbose） |
| CS2J0601 | Error（strict）/Warning | params 类型不合法/不可判定（表外、error type、StringBuilder 等基线非法形态防御命中） |
| CS2J0602 | Error/Warning | 调用点符号解析失败且无法安全归属（NuGet 缺失等）；不做名称猜测，站点保留 |
| CS2J0603 | Info | K1 数组调用点直传/（D8 开启时）展开计数 |
| CS2J0604 | Info | Span/ReadOnlySpan 以数组形态构造（栈→堆提示，C06 收口；对应 C36 0578 同语义） |
| CS2J0605 | Info | K4/K5 集合/接口展开（List 初始化器/接口具体类分桶） |
| CS2J0606 | Error/Warning | 初始化器模式不成立：无无参 ctor/Add 不可访问或不可解析（不猜 add/put，同 C24 0533 纪律） |
| CS2J0607 | Error（strict，v1）/Warning | `[CollectionBuilder]` 类型 v1 不收编；消息含 builder 类型与工厂方法名；M2 后转 0605 |
| CS2J0608 | Error/Warning | ref struct 元素 Span 等超出数组形态边界（指路 C06 D9） |
| CS2J0609 | Info | 形态 B 单个集合实参直传（保引用同一性，分桶计数） |
| CS2J0610 | Error/Warning | IOperation 与语法槽位结论冲突（防御；站点保留） |
| CS2J0611 | Info/Warning | params 展开段嵌集合表达式/spread 而 C36 未先处理（指路 C36；顺序交汇 §3.3） |
| CS2J0612 | Error/Warning | 声明物化一致性失败：virtual 槽位族中部分符号无法解析/跨文件目录缺失 |
| CS2J0613 | Info | E08 转发重载空 params 槽非数组补形态（PC-7） |
| CS2J0614–0618 | —— | 预留（属性/委托/partial 等新增声明种类登记用） |
| CS2J0619 | Error（strict）/Warning | residual 闸门：非数组 params 修饰符残留无 skip 配对，或幂等失败 |

---

## 7. 测试计划

### 7.1 Core 单测（建议 `tests/CSharpToJava.ProjectTests/ParamsCollectionsUnitTests.cs`）

内联源码 → net10.0 `CSharpCompilation`（Roslyn 4.12；仿 [IteratorLoweringUnitTests](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs) 的 Create 范式；parse options 与适配器实际选项一致）→ E08 `Collect`+`Strip`（options 开/关两档）→ 忽略空白文本断言 + **输出重编译断言** + 二次幂等断言。矩阵：

1. **K1 经典数组（D8 关，默认）**：`M(1,2,3)`/`M()`/`M(arr)`/`M(1, b:2)` 四形态下声明保留 params、调用点零改写（除 E08 既有行为）；
2. **K1 D8 开**：逗号→`new int[]{1,2,3}`、空→`new int[]{}`、单实参直传、具名单实参直传；泛型 `params T[]`；
3. **K2 Span**：`params ReadOnlySpan<int>` 三形态（产物 `new int[]{}` 重编译通过）；`params Span<int>` 可写形态（callee 内 `xs[0]=9` 作用于新数组）；`ReadOnlySpan<byte>`；ref struct 元素 → 0608；
4. **K4 List/具体集合**：`params List<int>` 逗号→`new List<int>(){1,2,3}`（重编译 + 运行时 Count/内容）、空→`new List<int>()`、单实参**同一引用**探针（callee Add 后调用方对象 Count 增长）；`HashSet<int>`、自定义 `Add` 集合；
5. **K5 五接口**：逐接口逗号展开产物为 `new List<T>(){}` 且重编译通过（形参接口类型可隐式接）；单实参传不同实现（`T[]`、自定义 `IList`）**不归一**；
6. **K3 builder**：探针定义 `[CollectionBuilder(typeof(B),"Create")]` 类型 → v1 0607 + 不改写；非 strict Warning 保留；M2 增补工厂发射矩阵（含 `ReadOnlySpan<KeyValuePair<K,V>>` 形参探针）；
7. **形态 B 同一性（D3）**：数组/List/IEnumerable/Span 各一单实参用例，断言产物文本恒等、ReferenceEquals 半区行为对照；
8. **空实参（D2）**：八类空形态；空+默认参数混用 `void M(int x = 0, params int[] a)` 的 `M()`/`M(1)`（E08 补全与 params 顺序）；
9. **默认参数/具名组合（D5）**：`M(1, b:2)`、`M(1, b:2, 3,4)`（合法写法探针）、`M(a: existing)`、E08 转发重载链（含 params 空槽走到 PC-7 非数组分支）；
10. **集合表达式组合（D4）**：`M([1,2])`、`M(..span)`（在含 C#13 语法的编译中）C48 一律直传；模拟 C36 产物（手写 `new[]{1,2}`）仍直传（防双包）；0611 边缘探针；
11. **重载决议（D1）**：`M(int x)` vs `M(params int[])`；`M(string)` vs `M(params ReadOnlySpan<char>)`（C#14 隐式转换影响面，4.12 下按 TargetMethod 实测钉行为，记录 C52 交界）；三级继承+new 隐藏；符号解析失败 → 0602 不猜；
12. **声明物化（PC-8）**：abstract/override/interface 实现/显式接口/partial 两瓣的 params 集合一致剥离；base 构造链 params；`[JsonConstructor]` 共存（E08 现有注解逻辑不回归）；
13. **声明种类（D9）**：delegate `delegate void D(params IEnumerable<int> a)`（invoke 调用点）、local function；record primary ctor 含 params 集合（基线是否合法探针）；
14. **排除/防御**：`params StringBuilder`（基线 CS 错误输入 → 0601 路径或 parse 失败分流，钉行为）、params 非末位、多维数组 params、表达式树内 lambda 调 params 集合（基线错误，防御计数）；
15. **零误伤/幂等**：规则关闭时全文件字节一致（含 E08 既有输出）；普通无 params 工程 Changed=false、BOM 保留；开启后二次运行零 diff；统计分桶/行号正确。

### 7.2 项目级 fixture（建议 `tests/SampleStripDefaultSolution/` 内新增 `Params*` 前缀源，不建第二套 sln）

- Lib 面：.NET 10 风格 API 声明组（`params ReadOnlySpan<T>` 日志/格式化器、`params IReadOnlyList<T>` 批量 API、`params List<T>` 可变批处理、自定义 CollectionBuilder 类型独立条件编译块）、override/接口实现层级、泛型服务；
- Tests（**.NET 半区行为对照**，原生 C# 与预处理后中间树各跑一次）：集合内容/顺序/Count、形态 B 同一性（callee 突变可见于 caller）、空态、Span 下标读写、副作用顺序（逗号段表达式按源码序求值一次）；
- 项目级断言：`result.Success`；中间树重编译 + `dotnet test` 退出码 0；非数组 params 修饰符零残留（豁免配对除外）；干净文件字节复制/BOM；
- 报错 sln/夹具：builder（strict）/ref struct Span/无法解析 Add → Success=false、对应诊断出现、不留半成品目录。

### 7.3 Release build/test（实施期执行；本设计任务不执行）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

### 7.4 共处回归与 residual 闸门

- **E08 回归**：[DefaultParameterStripperTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DefaultParameterStripperTests.cs)、`DefaultParameterCatalogTests`、`DefaultParameterOverloadBuilderTests`、`StripDefaultProjectTests` 全绿且零改动；L231 `params object[]` 组合用例在 C48 开关两档输出一致（K1 不动）；
- **现有 varargs 护栏**：[MethodParameterTests.cs L11-L23](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/MethodParameterTests.cs#L11-L23) 保持绿色（K1 声明保留 → Java varargs 直映；默认关时调用点逗号仍走 P2 老路径）；
- **C36 共处**：expression-desugar 翻转后联测 `M([1,2])/M(..s)` 全链（C36 去糖 → C48 直传，无双包）；两规则开关四象限；
- **ref-out-in 共处**：strip-default 产物即 ref-out-in 输入，params 物化不与 scoped/ref 剥离冲突（Span 形参无 ref 组合）；
- **residual「零非数组 params」口径**：输出树每个非数组 `params` 修饰符必须属于「已物化（0605/0604）」或「显式跳过（0601/0602/0606/0607/0608/0610/0612）」之一；strict 下 0619 非零即退出非零；
- **CLI**：`strip-default --enable-params-collections` 单文件/项目两模式；convert-project 开/关两档摘要行、目录名仍为 `.cs2j-strip-default-src`（无新目录）；`--disable-pass strip-default` 整体关闭；`--list-passes` 无新增行。

---

## 8. 默认开启验收门槛

规则默认关；满足以下全部后方可把 `Enabled` 默认翻 true（`ExpandArrayCallSites` 另行评审，Q1）：

1. **硬前提 M0 结论成立**：Roslyn 4.12 在 strip-default 适配器实际 parse options 下对八类声明/三形态调用 parse+bind+IOperation 全支持（含 `IsParamsCollection`/`ParamCollection` 实测外形）；探针报告归档；
2. §7 矩阵全绿；三个测试工程 Release build/test 全绿；幂等、字节复制、residual「零非数组 params」闸门全绿；E08 既有测试零改动全绿；
3. **.NET 半区等价**：内容/顺序/Count、空态、形态 B 同一性（含可变 List 突变）、Span 读写、副作用序列全矩阵双跑一致；
4. **分类型灰度先于总开关**：K1 保持 D8 关；K2/K5/K4 先行（依赖面最小：数组隐式转换、List 初始化器均为 BCL 老特性）；K3 builder 默认开必须等 M2 + 白名单/通用发射评审；
5. **Span 与 C06 进度解耦确认**：K2 数组形态不依赖 C06 落地，但需 c06 设计书面确认最终 Span 形态同为堆数组（避免 C06 合入后返工）；C52 隐式 Span 转换对重载决议影响有 M0 探针结论（char/string 面 v1 可先 Scope 排除 `ReadOnlySpan<char>` 逗号展开）；
6. **命中率扫描**：对目标集（.NET 10 BCL Collections/Text/Xml 基线 + D:\csharp-sdk 抽样）跑盘点（0600 计数）：非数组 params 声明数、Kind 分布、形态 A/B/C 比例、builder/0606/0608/0602 命中清单逐条人工定性；
7. **Java 半区就绪核查（只核查，转换器改动另开 PR）**：物化产物经主转换产出可编译 Java——P1 站点（非数组 params 声明）经策略 B 后 `IsVarArgs` 不再误置；`new List<T>(){…}`/`new T[]{}` 的 Java 映射齐备；D8 开启场景下显式数组实参在 varargs 位不被二次包装（javac 语义核对 + e2e）；
8. 不达标长期 opt-in，每评审周期复跑 6/7。

---

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| **M0 Roslyn 探针 + 现状盘点 + 签名目录设计（不实现）** | ① 探针夹具（不进仓或入 tools/）：八类合法类型 + 三形态在 4.12 下 parse 诊断、`IParameterSymbol.IsParams/IsParamsCollection` 值、`IInvocationOperation/IObjectCreationOperation` 的 TargetMethod 与逐参 `ArgumentKind`（Default/ParamArray/ParamCollection）实测外形；合成 ParamCollection operation 的 Value 外形与目标类型；② Span 数组隐式转换重编译（含 `Span<T>` 可写、ref struct 元素失败码）；③ `CollectionBuilderAttribute` 读取 + 工厂方法绑定形状；④ 五接口规范具体类逐行核实（对齐 C36 探针记录）；⑤ 4.12 资源串三类 params-ctor 错误/表达式树限制复现；⑥ override/interface/partial 一致性元数据（ParamCollectionAttribute）观察；⑦ 适配器 parse options 版本确认；⑧ 目标集命中率初扫 | 探针记录 + 本设计定稿 |
| **M1 K1/K2/K5 + 空态 + E08 协作（默认关）** | PC-1/2/4/5/6/7/8/10 主体：Span 数组形态、五接口 List 形态、K1 声明保留（D8 子开关两档）、形态 B 直传、空实参、声明物化（策略 B 含 virtual 族）、转发重载空槽分派、residual/幂等；Core 单测 1-5/7/8/9/12/15；fixture .NET 半区双验；Release 全绿 | Core ~700 行 |
| **M2 K4 Add 绑定 + K3 builder + 复杂面** | PC-3/9：可初始化器具体类（Add IOperation 绑定，接 C24 助手或自带实现合并）、CollectionBuilder 工厂发射（0607 退役路径转 0605）；单测 6/10/11/13/14；泛型三档；与 C36 全链共处四象限；0611 边缘结论 | ~450 行 + fixture |
| **M3 画像、灰度与默认开门禁** | 0600 全量画像（BCL/sdk 目标集）；0612 跨文件一致性大仓夹具；CLI/统计/摘要/指纹收尾；Java 半区核查（§8-7）；D8 是否默认开评审（Q1）；Release 三工程全绿；默认开启门槛逐条签出 | ~200 行 + 扫描评审 |

M0–M3 全程规则 `Enabled=false`（pass 仍默认开、规则短路）；每里程碑自身 Release build/test 全绿，不留半残中间树；不新增 pass/stub/id/目录/适配器。

---

## 10. 风险

| # | 风险 | 说明 | 缓解 |
|---|---|---|---|
| R1 | **★重载决议误判（最高）** | `M(int)` vs `M(params int[])`、Span 相关重载在 C#14 隐式转换下的优先级、构造函数重载链——按语法名/元数猜必错 | 只信 `IInvocationOperation.TargetMethod` + `ArgumentKind`；只改已解析站点；D1 失败即 0602 不猜；重编译门 + 半区行为对照；char/Span C#14 面 v1 Scope 排除 |
| R2 | **双重包裹** | `M(new[]{1,2})` 被再次包 `new T[]{ new[]{…} }` 类错误；与 C36 顺序变化时风险升高 | 判定只认 ArgumentKind 合成位（Default 永不动）；结构上与 pass 顺序无关（§3.3）；联测四象限；幂等闸门 |
| R3 | **Span 隐式转换的版本/形态依赖** | `T[]`→Span 隐式转换虽为 BCL 老特性，但 C#14 新增的隐式 Span 转换改变部分决议；显式 `AsSpan` 又是另一套生命周期语义 | v1 只发数组；M0 探针钉 4.12 行为；与 C06 选型书面对齐（堆数组）；C52 交界登记；char 面先排除 |
| R4 | **集合可变性/同一性语义差** | 形态 B 误复制会破坏「callee 改集合 caller 可见」；逗号形态每次调用构造新集合（缓存/静态复用假设会错）；空集合单例（`[]` 或 Array.Empty）与 new 的同一性差异 | 形态 B 零改铁律；逗号/空按编译器每次新建语义；D2 空数组形态经探针确认编译器做法再定 Array.Empty；ReferenceEquals 专项探针 |
| R5 | **声明物化元数据丢失** | 剥离 params 后 ParamCollectionAttribute 消失，反射/源生成器/DPI 消费者行为变化；virtual 族剥离不一致导致跨程序集决议漂移 | PC-8 整族一致物化；Q3 评估补发 Attribute；中间树重编译 + 大仓跨项目夹具（0612） |
| R6 | **E08 热文件回归** | 在已默认开 pass 内挂规则，任何接缝泄漏都会影响全量现有用户 | options 默认 null/false 短路；E08 既有测试零改动要求；IsDirty 粗扫不得改变干净文件判定；字节级 BOM 复制沿用 |
| R7 | **Add 绑定错译** | K4 自定义集合扩展 Add、多参 Add、fluent Add（同 C24 S4） | M2 前用可见性规则保守判定 + 0606；M2 起 IOperation 绑定，复用 C24 助手与结论；不猜 add/put |
| R8 | **IOperation API 外形记忆偏差** | 4.12 虽经 XML 文档实证 IsParamsCollection/ParamCollection 存在，但合成 operation 子结构（ParamCollection.Value 是否可还原元素语法）须实证 | M0 探针先行纪律（同 C23/C24/C36）；元素一律取自源码 ArgumentList，operation 只做分类/目标类型来源 |
| R9 | **builder 自定义语义** | Create 可缓存/拷贝/返回单例（ImmutableArray），字典 builder 形参为 KV span | v1 一律 0607；M2 严格按绑定方法符号；画像白名单评审 |
| R10 | **D8 默认开的冲动** | 经典 params T[] 逗号调用点全量显式化噪声大、收益依赖 Java 侧路径下掉节奏 | v1 默认关；Q1 单独评审；K1 保留声明使两档输出在 Java 半区都正确 |

---

## 11. 并行协调

| 相邻工作 | 边界 | 接口约定/合并顺序 |
|---|---|---|
| **E08 strip-default（锚点通道）** | C48 是其规则扩展：共享 Collect 目录、调用点 Visit、Verifier、适配器、统计流；默认值补全归 E08，params 形态归 C48 | 同 pass 内 R-a/R-b 固定顺序（§3.2）；接缝全部追加式、默认关闭零行为变化；E08 维护者 review 挂点 diff；既有 4 个测试文件零改动 |
| **C36 collection-expr** | C36 管 `[1,2]`/`..s` 自身的去糖与目标类型推断；C48 管 params 槽位的展开/直传；`M([1,2])` 归 C36（Default 实参直传） | 交汇规则顺序无关（§3.3）；K4/K5 发射复用 C36 `TargetTypeResolver`/规范具体类结论（internal 引用或探针记录复制，M2 合并）；码段不交叉（057x vs 06xx） |
| **C24 initializer-desugar** | K4 `new C(){…}` 发射与 Add 绑定归 C24 助手能力；初始化器语法站点本身归 C24 | M1 可先合（自带最小零临时发射：params 实参位不需要语句化）；M2 切 IOperation Add 绑定时接 C24 internal 助手（其 Q4 约定的又一消费者，同 C36 案例） |
| **C06 unsafe-ffm（Span）** | K2 数组形态是 C06 D5（stackalloc 堆化）的同构前导；ref struct 元素拒绝（0608）指 C06 | 书面对齐堆数组终态；0604 计数供 C06 画像；C06 合入后对 K2 产物幂等 |
| **C52 版本防线** | C#14 隐式 Span 转换改变重载决议（string/char → ReadOnlySpan）；params 不涉及 extension 块 | M0 探针记录 4.12 下 `M(string)`/`M(ROS<char>)` 决议；v1 Scope 可排除 char 面；C52 盘点不重复处置 params |
| **C49 extension 成员** | params 与扩展方法/extension 块无关：params 是参数修饰符，扩展成员是接收者语法；扩展方法经典 `this` 参数与 params 可共存（`IEnumerable<T>.Append` 式） | catalog 已明示无关；若 C49 物化 extension 块为普通静态方法，其末位 params 集合仍走本规则，符号目录已含 ReducedFrom 处理（[Catalog L219-L226](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterCatalog.cs#L219-L226)） |
| **热文件 ArgumentTransformer/MethodTransformer 等** | P1/P2 现状缺陷本期**只记录不改写** | 中间树物化后非数组声明不可达 P1；转换器下掉 IsVarArgs 盲目标 + L76 溢出映射的清理 PR 由 §8-7 门禁忌动，单独立项 |
| **C01 ref-out-in** | `scoped` 参数剥离已在 C01；链位在 strip-default 之后 | C48 不碰 scoped token；0604 形态产物经 C01 不回退 |
| **C08 java-keywords** | 新发射标识符仅 BCL 名（List/Array），无新临时名（零临时形态） | 无交互；D8 亦不产生新标识符 |

---

## 12. Worktree 分配（只写不执行；本设计任务未执行该命令）

- 分支：`feature/preproc-c48-params-collections`
- 目录：`.worktrees/preproc-c48-params-collections`
- base：`10aae03f`（完整 SHA `10aae03f11c2dd579acbc4d8d7e331b14cfddae8`，已 `git rev-parse --verify` 校验，2026-10-02 更新至 dev-3.x 最新提交）
- 创建命令（在仓库根、PowerShell 下执行；本设计任务只产出本文档，**不执行**）：

```powershell
git worktree add .worktrees/preproc-c48-params-collections -b feature/preproc-c48-params-collections 10aae03f
```

实施约束（交实施 worktree）：仅在该 worktree 内改码；不新增 pass/stub/id/中间目录/适配器/verb、不动 RunAfterIds、不翻转任何注册表状态；E08 既有测试零改动；转换器代码改动（§8-7）留后续 PR；交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md L108-L117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `DefaultParameterStripper/ParamsCollections/`、Core 单测 + 项目级 fixture（并入 SampleStripDefaultSolution）+ Release build/test + residual「零非数组 params」闸门 + CLI 测试、plan 文档 `docs/superpowers/plans/2026-09-29-c48-params-collections.md`；不新建支撑工程；M0 探针先于一切实现代码。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持本文 §12 登记的自有 base `9b99c60b`（不改动），创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（按原题号）：**

1. **经典 `params T[]` 逗号调用面貌**：v1 **保持逗号调用**（Java varargs 直透，K1 声明保留、Java 半区天然正确、噪声最小），不随本规则显式化；.NET 10 BCL"非数组 params 占比"画像与 Java 侧 P1/P2 清理只影响非数组形态的优先级，**不追溯改动数组形态**，此条 M3 不再翻案。
2. **K2 Span 终态与版本边界**：v1 用 **`new T[]{}` 隐式转换**（已可编译）收口；显式 `AsSpan` 等终态**等 C06 Span 支撑终态统一收口**，本 pass 不自造 Span 半区。`ReadOnlySpan<char>` 的 char 面 **v1 排除**，待 C#14 隐式 Span 转换在 C52 批次的 4.12/4.14 探针确认重载决议无歧义后再决定收编，不凭推测放行。
3. **策略 B 元数据与跨声明一致性**：为剥离后的参数**补发 `ParamCollectionAttribute`**（保反射可见性——下游 Java 反射/序列化面依赖参数符号，与"便利下游转换"口径一致），构造签名以 M0 探针为准；override/interface/partial 整族物化时**部分符号不可解析（0612）一律 fail-fast 整族报错**，不跳过、不半剥离（半剥离比不剥离更危险），误报率用大仓夹具验证。
4. **C24/C36 助手复用时机**：**M1 自带最小发射、不阻塞开工**（零临时、无语句化需求，复制面小，K1/K2/K5 不依赖语句化）；**M2 与 C24 Add 助手绑定合并**（届时消复制）；**K3 builder 节奏完全跟 C36 CE-10 对齐**，不提前另建 builder 发射面。
5. **空实参单例同一性**：以 **"与编译器 lowering 同形、`ReferenceEquals` 可观察行为一致"为唯一裁决标准**——M0 探针确认 4.12 对 `M()` 合成 ParamArray/ParamCollection 空实参是否复用缓存（`Array.Empty<T>()` vs `new T[]{}`、空接口集合是否单例），探针见缓存则发缓存形、见新建则发新建形；不以"哪个更省分配"自行选形。

---

### 原问题记录（裁决前）

1. **D8 经典 `params T[]` 逗号调用点的默认面貌**：v1 保持逗号（Java varargs 直透）还是随本规则默认全部显式化？倾向前者（K1 声明保留、Java 半区天然正确、噪声最小），但需 .NET 10 BCL 画像评估「非数组 params 占比」与 Java 侧清理 P1/P2 路径后的统一口径；M3 凭命中率与 §8-7 核查结论拍板。
2. **K2 Span 终态形态与版本边界**：v1 `new T[]{}` 隐式转换已可编译，是否需要 v2 的显式 `AsSpan`（或等 C06 的 Span 支撑终态统一收口）？C#14 隐式 Span 转换（C52）对 `ReadOnlySpan<char>` 重载决议的影响在 4.12 探针下确认后，决定 char 面是 v1 排除还是直接收编。
3. **策略 B 的元数据与跨声明一致性**：是否为剥离后的参数补发 `ParamCollectionAttribute`（保反射可见性，构造签名待探针）？override/interface/partial 整族物化若遇部分符号不可解析（0612）应 fail-fast 还是跳过整族？倾向 fail-fast（半剥离比不剥离更危险），需大仓夹具验证误报率。
4. **C24/C36 助手复用时机与可见性**：M1 自带最小发射（零临时、无语句化需求，复制面小）先行，还是等 C24/C36 合入后开工以直接复用 `TargetTypeResolver`/Add 助手？倾向 M1 不阻塞（K1/K2/K5 不依赖语句化），M2 与 C24 Add 绑定时合并；K3 builder 节奏完全跟 C36 CE-10 对齐。
5. **空实参的单例同一性**：空数组发 `new T[]{}` 还是 `Array.Empty<T>()`、空接口集合发 `new List<T>()` 还是尝试编译器空态单例——需 M0 探针确认 4.12 编译器对 `M()` 合成 ParamArray/ParamCollection 的实际 lowering（是否复用缓存），以「与编译器同形、ReferenceEquals 可观察行为一致」为裁决标准。
