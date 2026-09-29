# C19 扩展方法显式化设计：`x.M(a)` → `ExtType.M(x, a)`

**日期**: 2026-09-29
**范围**: 把注册表 stub `extension-methods`（catalog C19，P2）从 `Stub` 翻转为 `Implemented`；新增 Core 规则与 CLI 适配器，不修改 C#→Java 转换器代码（转换器侧旧资产退役另立后续 PR，见 §10/§9 M5）
**状态**: 设计定稿待评审；实施在独立 worktree（见 §12），本文不改动任何代码
**对应基线**: `953293b9`（dev-3.x HEAD，完整 SHA `953293b908e9143437c45f287fb4277e890c398e`，"docs: update doc base commit"）
**关联资料**: `prompts/preprocessor-catalog.md` C19（L483-494）/C26（L565-577）/§3.3 表达式与方法解析族（L898-915）/§4 风险 7、8、10、11、12（L918-939）；`.trae/documents/preprocessor-foundation_plan.md` L60-139；范本 [2026-09-28-c02-generic-deepen-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c02-generic-deepen-design.md)

---

## 1. 背景与目标

### 1.1 现状：扩展方法解析逻辑分散在至少五个消费点，调用点无 .NET 侧等价性闸门

| # | 消费点 | 职责 | 证据 / 缺口 |
|---|---|---|---|
| 1 | [ExtensionMethodIndex.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ExtensionMethodIndex.cs)（全文 199 行） | 主转换器**自建**扩展方法索引：`ExtensionMethodDescriptor` 按 docCommentId / receiverType（`OriginalDefinition` 规范化）/ project 三字典组织；`ScanCompilation` 语法扫 `this` 修饰 + `IsExtensionMethod` 符号确认；描述符里预算了 Java 包名/host 类名/camelCase 方法名 | 只索引**声明**，不做调用点绑定、不做 using 可见性判定、不做重载决议；构造于主转换前（[Cs2jLibrary.cs:44](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Cs2jLibrary.cs#L40-L44)、[ProjectConversionPipeline.cs:320-335](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs#L320-L335)），多项目模式靠 CLI 预填 |
| 2 | [ExtensionMethods.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/ExtensionMethods.cs) | 名字是「扩展方法」，实为 roslyn-linq-rewrite 第三方版权的**语法 helper**（`WithStatic`/`WithRef`），与扩展方法语义解析无关 | 双份逻辑盘点中的命名噪声：真正的 LINQ 扩展解析在 LinqRewriter 的 SemanticModel 调用里（见 §3.5 证据） |
| 3 | [ProjectPasses.cs:740-830](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L740-L830) `ProjectExtensionMethodCheckPass` | 主转换期校验：调用点绑定为 `MethodKind.ReducedExtension` 时，按 `ReducedFrom.GetDocumentationCommentId()` 查自建索引、按 `TypeMappings.MapMethod(hostType, name)` 查表；两者都不中则 **Warning `CS2J_EXT001`，`blockEmit:false` 非阻断** | LINQ host（`System.Linq.Enumerable`/`Queryable`）硬编码豁免（L790-793、L825-829）；只告警不改写，Java 产物继续「引用可能不可用的静态 host 类」 |
| 4 | [InvocationExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs)（成员访问分支 L135-168、L206-240） | 调用渲染主路径：`x.M(a)` 一律渲染为 receiver 实例调用 `receiver.m(a)`；是否改成静态分派完全依赖 [TryResolveMappedName](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L559-L595) 查 TypeMappings，且查表只用 `ContainingType.ToDisplayString()` + 名字 + 参数数 | **全文件无任何 `IsExtensionMethod` / `ReducedExtension` 分支**（全 Transformers 目录 grep 仅命中 `MemoryExtensions` 特判，见下）：用户自定义扩展 host 不在 TypeMappings 中时，产出的是 receiver Java 类型上根本不存在的实例方法 |
| 5 | 各 pass 私有 reduced 符号启发式 | RecordReadOnly 族 6 处 `ReducedFrom ?? raw` 归一（[MethodMigrator.cs:278,646,660](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/MethodMigrator.cs#L278)、[RecordReadOnlyAnalyzer.cs:400-438](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs#L400-L438)）；[BoxingReachabilityAnalyzer.cs:207-209](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/BoxingReachabilityAnalyzer.cs#L207-L209)（「reduced 调用 receiver 不在参数表里」）；[DefaultParameterCatalog.cs:219,263-265](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterCatalog.cs#L219-L265)；[ForeachDeconstructionRewriter.cs:166](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L166) 注释 | 每个消费点各自把 reduced 符号归一回静态符号——同一份语义事实被反复重建，正是 C19 要消灭的「双份/N 份解析」面 |
| 附 | 实验开关 `RewriteExtensionMethods` | 唯一会动扩展形态的转换器期选项，**默认 false**（[ConversionOptions.cs:75](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionOptions.cs#L75)），多项目模式被强制关闭并告警（[ProjectConversionPipeline.cs:300-308](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs#L300-L308)） | 单项目专用、作用是剥 receiver 形参（声明侧），与 C19 的「调用点显式化」不是同一形态，C19 不依赖、不复活它 |

测试也是双份且各盖一半：[tests/CSharpToJava.Tests/ExtensionMethodTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ExtensionMethodTests.cs)（115 行：索引扫描、声明保持、`RewriteExtensionMethods` 剥参）与 [tests/CSharpToJava.ConversionTests/Categories/ExtensionMethodTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/ExtensionMethodTests.cs)（49 行：声明侧 `static class` → `final class` + private ctor）。**没有任何测试断言「用户扩展调用点被降级为对 host 类的静态调用」**——调用点绑定正确性目前在 .NET 半区零闸门（§7 补）。

### 1.2 Java 侧静态分派回归：四处可观察语义差异（不止是「名字没映射上」）

catalog C19 记录「Java 侧静态方法分派与 C# 扩展接收者语义在重载/泛型处反复出回归」（L489）。代码级根因是：扩展调用的 C# 语义是**静态、非虚分派**，而现渲染路径产出**实例虚分派**，差异在四个点上可观察：

1. **null 接收者**：C# 中 `x.M(a)` 绑定为扩展方法时等价 `Host.M(x, a)`，`x == null` 本身**不抛**（是否 NRE 取决于方法体是否解引用首参）；Java 实例形态 `x.m(a)` 在进入方法体前即对 `x` 虚分派 → 必然 NPE。这是最常被真实代码依赖的语义（大量 `ThrowIfNull`/`NullGuard` 风格扩展）。
2. **非虚分派**：扩展方法按**静态类型**绑定，不随运行时类型变化；receiver Java 类型若存在同名可覆写方法，实例形态会被动态分派劫持。
3. **重载/泛型决议时机**：C# 候选集含 using（普通/别名/global using）与嵌套命名空间引入的全部扩展方法，泛型扩展经类型推断定案；现路径只做「host 显示名 + 方法名 + 参数数」查表，泛型扩展 `Id<T>` 在 erasure 后与同 arity 重载碰撞时无事实可依。逐 host 打补丁的现状有实证：[BinaryExpressionTransformer.cs:179-187](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L179-L187) 为 `MemoryExtensions.sequenceEqual` 做硬编码特判，[StatementTransformer.ExpressionAndReturn.cs:169-174](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L169-L174) 为 `MemoryExtensions.asSpan` 加 import——每来一个 host 补一处。
4. **跨项目 host 不可达**：host 在转换图外且无映射时只发 CS2J_EXT001 Warning 且 `blockEmit:false`（[ProjectPasses.cs:767-771](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L760-L772)），错误 Java 照常产出。

### 1.3 目标

在所有会向中间树**注入新代码/新绑定**的锚点 pass 之后，新增独立 pass `extension-methods`：

1. **调用点显式化**：整树一次 `CSharpCompilation`，凡 `SemanticModel.GetSymbolInfo` 绑定为 `IMethodSymbol { MethodKind == ReducedExtension }` 的调用 `r.M(a)`，按 C# 语义改写为不可降级的显式静态调用 `global::Ns.Host.M<...>(r, a)`（receiver 为首参，求值次序不变；泛型实参与 ref-kind 见 §2）。
2. **SemanticModel 是唯一真相**：using/命名空间嵌套引入、实例同名优先、重载决议、泛型推断全部直接消费 Roslyn 绑定结果，不重建候选决议、不做文本/名字猜测（catalog §3.2 横切约束 L896）。
3. **LINQ 糖保留归主**：绑定到 `System.Linq.Enumerable`/`Queryable` 等 LINQ host 的 reduced 调用**不改写**，糖继续归 E16（今天，主转换器内嵌）/ C26（未来 `linq-channel`）消费——链序与所有权的复核结论见 §3.4。
4. 产物仍是语义等价 C#，fixture 与 sdk 中间树 `dotnet build -c Release` / `dotnet test -c Release` 全绿；不可判定站点显式诊断（CS2J12xx），禁止注释/占位/原样静默透传（catalog §4 风险 12）。
5. **不做**：不改扩展方法**声明**（不剥 `this` 形参、不动静态 host 类）、不新建支撑库、不在本特性 PR 改转换器；旧资产退役（索引/校验 pass/逐 host 补丁）登记为后续 PR（§9 M5）。

---

## 2. 关键设计决策表

| 决策点 | 结论 | 备注 |
|---|---|---|
| 总体架构 | 全编译 Bind→Analyze→逐文件 Rewrite + AfterFiles residual 扫描；复刻 generic-deepen 的「整树 Analyze 一次 + 标准 spine + 回退骨架」，但**无跨文件生成产物**（无 holder/无新文件），适配器比 C02 薄 | §4 |
| 唯一真相 | 调用点分类只认 `SemanticModel.GetSymbolInfo(invocation).Symbol`：`MethodKind.ReducedExtension` 才改写，目标方法取 `ReducedFrom`（未归约静态符号）；语法形状（`MemberAccess`/名子串）永不参与判定 | EM-BIND；实例方法同名优先、using 候选遮蔽等 C# 规则由 Roslyn 天然保证，本 pass 不重新实现 |
| 候选扩展方法集（using import 层级） | **不自建候选集、不跑 LookupExtensions**：普通 using、using static（不引入扩展方法，仅事实登记）、using alias、global using（含 `<ImplicitUsings>enable</ImplicitUsings>` 合成树，runner 已有合成 global using 范式）、当前命名空间及其外层命名空间、外层类型中声明的扩展——Roslyn 绑定已全部计入；绑定失败经 `SymbolInfo.CandidateReason` 分类诊断 | EM-CAND；`Ambiguous` / `OverloadResolutionFailure` / `None` 分流到 CS2J1201/1202，不靠语法猜一个 host |
| 目标形态（限定方式） | `global::` 全限定 host 静态调用：`global::Ns.Host.M(r, a)`；**不向源文件加 using**（零命名管理、零与既有 import 冲突、零别名遮蔽）；方法名保持 **C# 原名**（PascalCase），camelCase 是 Java 侧命名映射，由链尾转换器负责 | 与 java-keywords 的关系：本 pass 在其之前，全限定名若撞 Java 关键字由 C08 兜底；全限定用 `SymbolDisplayFormat.FullyQualifiedFormat` 经 `SymbolDisplayGlobalNamespaceStyle.Included` 构造 NameSyntax，禁止手拼字符串 |
| 泛型扩展：方法类型实参 | **源码省略就继续省略**：`r.Shuffle()` → `Host.Shuffle(r)`，让 C# 编译器从「显式首参」重新做方法类型推断——推断输入与 reduced 形态完全同构（receiver 原先是隐藏首参，现在是显式首参），推断结果必然一致；源码显式给出的实参 `r.M<int>(a)` 原样保留为 `Host.M<int>(r, a)`。**不物化推断结果**——推断类型可能是不可显式书写类型（lambda、方法组、带元组名的 tuple 形） | EM-GEN；显著降低实现风险，避免「推断出的类型再渲染回语法」的整类 bug |
| 泛型扩展：嵌套泛型 host | 扩展方法的宿主静态类自身可因**嵌套在泛型类型中**而是泛型（`Outer<T>.Ext`）：从 `ReducedFrom.ContainingType` 取**已构造**宿主符号，发射 `global::Ns.Outer<TArgs>.Ext.M(r, a)`；顶层非泛型静态类是常见形，嵌套泛型是必须覆盖的非常见形，fixture 双覆盖 | EM-GEN-HOST；`ContainingType.IsGenericType == true` 时才发射类型实参表 |
| 方法链 | `a.M().N()` 由 `CSharpSyntaxRewriter` 自底向上遍历天然收敛：内层先改为 `H1.M(a)`，外层 N 若同为扩展则改为 `H2.N(H1.M(a))`；混合链（实例方法/属性夹杂）只改绑定为 reduced 的节点，其余原样 | EM-CHAIN；专项覆盖「用户扩展夹在 LINQ 链中」（§3.4） |
| null 接收者 | 静态首参形态天然保留「null receiver 不抛」语义，这是本 pass 的核心正确性收益之一；fixture 在中间树 xUnit 里对 null receiver 调用断言不抛 | §1.2-1 |
| null 条件扩展 `r?.M(a)` | **M0 探针任务**：用最小 Roslyn 编译确认当前语言版本下该形态是否合法、绑定是否仍为 ReducedExtension（资料不确定，禁止凭记忆写规则）。合法则 v1 候选形为临时变量提升 + 条件静态调用（复用表达式族「单次求值临时变量」基建口径）；v1 若不落地则该站点 **CS2J1205 显式报告并跳过**（strict Error），禁止静默透传 | EM-COND；与 C24/C35（expression-desugar）的所有权边界：本 pass 只出报码，临时提升基建落地后再收编 |
| `this ref` / `this in` / `this scoped` 接收者 | 按首参 `RefKind` 发射实参修饰：`this ref Span<T>` → `Host.M(ref r, a)`、`this in`/`ref readonly` → `in r`；`scoped` 是逃逸元数据不进调用语法；普通值接收者不加修饰 | EM-REF；`ArgumentSyntax` 复用源实参节点，仅替换 `RefOrOutKeyword`/`InKeyword` |
| 实例方法同名优先 | 不做任何特殊处理：适用实例方法存在时 Roslyn 不绑 reduced，节点自然不被选中；另出 Core 单测锁死「实例/扩展同名同适用度」两类样本 | 防止任何「按名字找扩展」的回退路径破坏该规则 |
| 扩展属性 / C# 14 扩展成员块 | **v1 仅支持经典 `static class` + 首个 `this` 形参的扩展方法**；M0 探针确认 `LanguageVersion.Latest` 下 C# 14 extension 块（含扩展属性、无 this 形参的扩展成员）的语法/符号形态；分析器一旦发现该形态即 **CS2J1206 显式报错**（strict 前也为 Error：无法等价改写的构造不得静默），是否另立注册表条目（如 `extension-members-cs14`）M5 评审 | EM-EXT14；对齐 catalog §4 风险 13「新语法防线」口径 |
| 表达式树 `Expression<Func<...>>` 内站点 | **照常改写**（与多数改名 pass 不同）：C# 编译器对扩展糖在表达式树中本就发射为静态方法的 `MethodCallExpression`（receiver 是首参），显式化前后表达式树节点同形、同 `MethodInfo`；但必须过 **EM-EXPRTREE 同一性断言**：改写后重新编译，对比前后 `GetSymbolInfo` 的 `OriginalDefinition` 与参数序列，不一致则 CS2J1210 回退该站点。lambda 体内改写不得引入新局部/语句 | catalog §4 风险 8 的共同禁区在本 pass 的特化结论；C02 提取的 `ExpressionTreeFacts` 若已落地则复用，否则本 pass 自带最小判定 |
| 方法组形态 `var f = r.M;`（非调用） | reduced 扩展方法也可经成员访问以方法组形式出现在委托转换/`nameof` 之外的位置；v1 检测「成员访问绑定为 ReducedExtension 但父节点不是调用」的站点 → **CS2J1207 显式报告**（候选降级形 `(x) => Host.M(x)` 留 M2 评审，禁止 v1 悄悄插 lambda） | EM-GROUP |
| `nameof` / 反射 | `nameof(r.M)` 取的是标识符文本且不是调用，不经过本 pass 选择器，保持原样；本 pass **不改声明、不改任何成员名**，不新增 catalog §4 风险 7 的反射/JSON/XAML 按名面（显式静态调用在 CLR 与原糖是同一个 MethodInfo） | 风险面显著小于改名族 pass |
| dynamic 接收者 | C# 本就不对 dynamic 接收器做扩展绑定；`GetSymbolInfo` 拿不到 reduced 符号 → 不选中、Info 计数；不猜测 | 与各语义 pass 同策略 |
| 外部程序集 host（BCL/NuGet 扩展） | **同样改写**为 `global::Ns.Host.M(r, a)`：静态限定形态正是转换器 TypeMappings 静态路径的输入（比现状的实例渲染 + 逐 host 补丁更直接）；host 是否「在转换图/有映射」的分类只影响**诊断级别**：图内用户 host/支撑库 host 静默改写；metadata host 无 TypeMappings 落点时 **CS2J1203 报告**（承接 CS2J_EXT001 语义，non-strict Warning、strict Error），不再产出无主 Java | EM-EXTERNAL；`ContainingType.Locations[0].IsInSource` 且在本 compilation → 图内；否则 metadata |
| 支撑库注入扩展的可见性 | 链位五锚保证 E05 prelude（经 delegate 注入）、E06 tuple、C01 ref-out-in、E09 Cs2jUnsignedMath、E12 Cs2jArray 的注入产物已在中间树（merged compilation 直接可见）；**2026-09-29 核实：四个支撑工程与 `*SupportFiles.cs` 当前均无 `this` 扩展方法（grep 零命中），今天暴露的都是普通静态 helper**——五锚是面向未来（支撑库升级/C15/C31/C34）的不变量，不是当前数据依赖 | §3.2/§11 |
| 幂等 | 已是静态形态的 `Host.M(r, a)` 绑定为普通静态方法（非 reduced），天然不重复改写；residual 扫描对每个改写点做重绑定断言 | EM-IDEAL |
| 声明侧 | 一个字不改：`this` 修饰保留（C# 合法、中间树正常编译），声明→Java 的 `final class` + private ctor 映射由现有转换器承担（ConversionTests 五例已覆盖） | 与实验选项 `RewriteExtensionMethods`（剥形参）划清界限 |
| 与转换器关系 | 本特性 PR **不改转换器**；显式静态调用经现有「静态类型限定接收者」路径消费（`TryResolveMappedName` 返回 `isStatic:true` → `RenderMappedCall` 首参路径，[InvocationExpressionTransformer.cs:505-557](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L505-L557)）；默认开启前以冒烟证明该路径吃下图内 host（§8-4） | §9 M5 退役清单 |

---

## 3. 注册表与链位契约

### 3.1 描述符（Stub→Implemented 翻转目标）

在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 的 `Implemented` 表中、**`iterator-lowering` 之后、`eliminate-goto` 之前**插入（注册顺序即拓扑序，顺序有锁定测试，见 §3.3）：

```text
Id                          = "extension-methods"
DisplayName                 = "Extension method explicitness"
IntermediateDirectoryName   = ".cs2j-extension-methods-src"
Stage                       = PassStage.Expression
Status                      = PassStatus.Implemented
DefaultEnabled              = false（初版 opt-in；过 §8 门槛后翻 true）
RunAfterIds                 = { "delegate", "tuple", "ref-out-in", "unsigned", "array-method" }   ← stub 五锚原样保留
                             翻转评审建议追加：{ "make-readonly", "iterator-lowering" }            ← EM-ORDER，见 §3.2
SupportLibrary              = PassSupportLibrary.None
NeedsSemanticModel          = true
VerifyBuildByDefault        = true
VerifyTestsByDefault        = true
CatalogItems                = "C19"
Description                 = "Explicitize extension method calls to static host calls (x.M(a) -> global::Host.M(x, a)); LINQ hosts excluded."
```

`IntermediateDirectoryName` 常量放适配器 `ExtensionMethodResolvePassAdapter.IntermediateDirectoryName`（与 generic-deepen 同先例：无历史门面类，不建 `ProjectXxxPreprocessor.cs`）。

### 3.2 链位：五锚契约与 EM-ORDER 追加建议（评审决策点）

**五锚是「可见性」不变量，不是「相邻」约束。** stub 登记的 RunAfter = delegate / tuple / ref-out-in / unsigned / array-method（[PassStubCatalog.cs:202-218](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L202-L218)），精确对应五条会注入支撑代码的已实现通道（E05 prelude 实际挂在 `delegate` 描述符的 `SupportLibrary=Cs2jPrelude` 上，见 [PassCatalog.cs:86](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L86)）。五锚必须原样保留。

但五锚只把本 pass 放在 `array-method` 之后；当前链上 `array-method` 之后还有 `make-readonly`（默认开）与 `iterator-lowering`（已实现、默认关）。两个 pass 持有**按 reduced 糖形态工作的语义启发式**：

- E13 make-readonly 的 [BoxingReachabilityAnalyzer.cs:207-209](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/BoxingReachabilityAnalyzer.cs#L207-L209) 明确依赖「reduced 调用 receiver 不在参数表」来排除装箱；C19 若先跑，receiver 变成显式首参，该启发式的输入形态改变（同文件 MethodMigrator 还会迁方法、CallSiteUpdater 改调用点）。
- E14 iterator-lowering 把迭代器体展开为状态机类，展开产物可能携带扩展调用糖；本 pass 在其后才能显式化这些新站点（方向上与「注入新代码的 pass 先行」同理）。

**结论（EM-ORDER）**：翻转时把 RunAfter 扩为「五锚 + `make-readonly` + `iterator-lowering`」，落点 = `iterator-lowering` 之后、`eliminate-goto` 之前；`java-keywords` 仍是最后一个 pass（其 RunAfter 集合不变，注册表末位锁定测试保持成立）。这样所有 reduced 糖形态的消费者都先于本 pass 运行，C19 是链上最后一个语义/表达式改写，显式化之后只剩 goto 纯语法 pass 与命名 pass。

备选（不推荐）：保持五锚，用 fixture 证明 E13 在静态形态下分析结果逐位等价后可不追加；该证明须覆盖 MethodMigrator 迁移扩展方法的场景（RecordReadOnly 族已有 `MethodMigrationKind.Extension`，[MethodMigrator.cs:139](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/MethodMigrator.cs#L139)），成本高于加锚，列为开放问题 Q1。

### 3.3 Stub→Implemented 翻转时改动的文件

| 文件 | 改动 |
|---|---|
| [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L202-L218) | 删除 `extension-methods` stub 条目（L202-218）；**删除 `Ids.ExtensionMethods` 常量前先把 `linq-channel` stub 的 RunAfter 引用（L226）改为 `PassIds.ExtensionMethods`**（同 C02 翻转 generic-deepen 的常量迁移做法） |
| [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | `PassIds` 增 `ExtensionMethods = "extension-methods"`；按 §3.1 位置插 Implemented 描述符 |
| `src/CSharpToJava.CLI/Preprocessing/Adapters/ExtensionMethodResolvePassAdapter.cs` | **新增**适配器（§5） |
| [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L93-L237) | `CreateAdapter` switch 增 `PassIds.ExtensionMethods => new ExtensionMethodResolvePassAdapter(...)`；**不进 `BuildLegacyToggles`**（无历史开关，缺省态取描述符，通用 `--enable-pass/--disable-pass` 控制） |
| `src/CSharpToJava.Core/ExtensionMethodResolve/*` | 新增 Core 组件（§4） |
| 测试 | ① [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) `LockedChain` 18 → 19 id（插在 IteratorLowering 与 EliminateGoto 之间；`EnabledByDefault` 不变——本 pass 默认关）；② [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24) stub 计数 23 → 22（不补继任 stub；C#14 扩展成员由 CS2J1206 门承接，是否另注册观察条目 M5 定）；③ [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs#L131-L132) 仿 generic-deepen 段追加 `IntermediateDirectoryName == ".cs2j-extension-methods-src"` 断言；④ Core 单测/fixture/residual/CLI（§7） |

不需要改：`Program.cs` 指纹逻辑（已按注册表全集自动生成 `pass:extension-methods=on/off` 令牌，翻转造成一次性缓存失效，符合预期）、preserve 目录（由实际执行 pass 的描述符驱动）、通用 verb（`preprocess --pass extension-methods` 自动可达；stub 期启用即 CS2J9001 的路径翻转后自然消失）、`ProjectPassRunner`（标准 spine + AfterFiles 够用）。

### 3.4 与 `linq-channel`（C26）相对次序：复核结论（对照 catalog L574）

事实对照：

- 注册表：`linq-channel` 的 RunAfter = `{ extension-methods, anonymous-type-lift }`（[PassStubCatalog.cs:219-228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L219-L228)）——注册契约是 **C19 在前、C26 在后**。
- catalog C26 行备注：「依赖 C19（扩展方法解析）若同批做需排序：**LINQ 先**（消费扩展方法糖）、C19 在后」（L574）——字面上与注册表相反。

**复核结论：维持注册次序（C19 → C26），「LINQ 先消费糖」通过所有权排除实现，而不是链序交换。** 硬证据：

1. E16 的 [LinqRewriter.cs:123-129](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L123-L129) 对 `node.Expression` 做 `as MemberAccessExpressionSyntax` 结构性 cast，链回溯 [L212-219](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L212-L219) 是 `while (c.Expression is MemberAccessExpressionSyntax)`——它**结构性依赖 reduced 糖形态**；[LinqQueryDesugarer.cs:254-260](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs#L254-L260) 查询语法去糖也发射 member-access reduced 形态。若 C19 把 LINQ 链改成 `global::System.Linq.Enumerable.Where(src, ...)` 静态形态，E16/C26 的链识别全部失配。
2. 因此本 pass 设 **EM-LINQ 排除策略**：调用绑定到 LINQ host 集（默认 `System.Linq.Enumerable`、`System.Linq.Queryable`，复用 [ProjectPasses.cs:825-829](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L821-L829) 的同一对 host，做成可配置集）时**不选中**，糖原样保留。效果上「LINQ 先拿到自己的糖」成立：今天糖流入主转换器由内嵌 E16（`EnableLinqRewrite && !EffectivePreferStreamApi`，[ProjectPasses.cs:164](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L164)）消费；C26 通道化后糖在 C26 环被消费。两条时间线下 C19 对 LINQ 站点的行为一致：零触碰。
3. catalog L574 的排序建议在「LINQ 重写器能吃静态形态」的假设下才成立；代码证据否定该假设，故以注册表契约为准，并在 C26 设计中回链本结论。C26 的 IQueryable 三路分流门（catalog L573）依赖 receiver 语义判定——reduced 形态下 `GetSymbolInfo`/receiver 类型信息完整，不受影响。
4. **混合链**（`src.Where(...).MyUserExt().Select(...)`）：C19 只改 `MyUserExt` 一个节点为静态调用，LINQ 节点保持糖；E16 链遍历遇到非 member-access 子节点的行为列为 M4 专项风险验证（§10-1），必要时 C19 对「LINQ 链上的用户扩展」整链保守跳过并出 CS2J1203 之外的 Info——策略以探针结果定，v1 默认只改孤立用户扩展节点。

### 3.5 中间目录、验证与默认值

- 中间目录 `.cs2j-extension-methods-src`；输入 = 五锚（及 EM-ORDER 追加锚）产物树，输出交 goto/命名/主转换。
- `VerifyBuild/VerifyTests` 默认**双开**（最严档，同 generic/generic-deepen）；失败映射 CS2J1230/1231；`DeleteBuildOutputsAfterRestore = true`。
- 无支撑库：不增 `PassSupportLibrary` 枚举值，不触发支撑工程过滤；本 pass 不生成文件。
- 成本模型同各环（catalog §4 风险 11）：一环一树一 restore，指纹/preserve 走注册表驱动，不加额外轮次。

---

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/ExtensionMethodResolve/`，命名空间 `CSharpToJava.Core.ExtensionMethodResolve`。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `ExtensionMethodResolveOptions.cs` | 选项 record：`Strict`（Warning→Error）、`ReportInfo`、`RunResidualScan`（默认 true）、`RewriteExternalMetadataHosts`（默认 true）、`LinqHostSet`（默认 Enumerable/Queryable 全名集，可追加）、`ExpressionTreePolicy`（RewriteWithIdentityAssert / ReportOnly，默认前者）、`OverridesPath`（按 host 全名 force/skip，默认空） |
| `ExtensionMethodResolveStatistics.cs` | `FilesScanned/FilesTransformed/FilesClean`、`CallsitesBoundReduced`、`CallsitesRewritten`、`GenericHostQualified`、`RefReceiverSites`、`LinqOwnedSkipped`、`ExternalHostSites`、`InCompilationHostSites`、`ExpressionTreeSitesChecked`、`SitesSkippedByReason`（字典）、`ResidualsByRule`；自有字段形状，runner 仅经适配器消费 |
| `ExtensionMethodResolveDiagnostics.cs` | `ExtensionSeverity`、`ExtensionRule`（Bind/Gen/Ref/Cond/Group/Ext14/Linq/External/ExprTree/Residual/Infra）、`ExtensionDiagnostic`、码表（§6） |
| `ExtensionCallBinding.cs` | `record ExtensionCallBinding(InvocationExpressionSyntax Invocation, IMethodSymbol Reduced, IMethodSymbol Unreduced, ReceiverClassification Classification, RefKind ReceiverRefKind, bool IsInExpressionTree, bool ExplicitTypeArguments)`；`ReceiverClassification { InCompilationUser, InCompilationSupport, ExternalMetadata, LinqOwned }` |
| `ExtensionCallBinder.cs` | 单文件分析：枚举 `InvocationExpressionSyntax` → `GetSymbolInfo` 分类；产出 binding 列表与不可解析站点的初判诊断；另检测「reduced 成员访问但非调用」方法组站点（EM-GROUP）。**不做候选决议**，只消费 Roslyn 结果 |
| `ExtensionHostSyntaxFactory.cs` | 从 `Unreduced.ContainingType` 构造 `global::` 限定 `NameSyntax`（含嵌套泛型宿主类型实参，EM-GEN-HOST）；用 `SymbolDisplayFormat.FullyQualifiedFormat` + `SyntaxFactory.ParseName`，禁止字符串手拼；泛型宿主实参同样走全限定格式 |
| `ExtensionCallRewriter.cs` | `CSharpSyntaxRewriter`：按 binding 改写——构造 `InvocationExpression(QualifiedMemberAccess(host, M[+显式实参表]), [ref/in] receiver, oldArgs)`；receiver 表达式节点原样复用（保副作用/trivia 策略：receiver 移入首参，原 member-access 的 leading trivia 移到新调用首 token）；链式由遍历顺序天然处理；`GenericName` 实参表仅在源码显式存在时复制（EM-GEN）；挂改写注解供 residual 配对 |
| `ExtensionExpressionTreeFacts.cs` | 最小化判定「invocation 是否位于 `Expression<>` lambda 内」：优先复用 C02 提取的共享 `Analysis/ExpressionTreeFacts`（若其已先合入），否则内置等价实现并在注释登记合并去向（不产生第二份长期副本） |
| `ExtensionMethodResidualScanner.cs` | AfterFiles 对中间树再编译：①残留 reduced 调用（按 LINQ host 分类豁免）；②残留 reduced 方法组；③改写点重绑定 `OriginalDefinition` 同一性；④表达式树节点序列同一性；⑤误删声明 `this` 修饰计数（应恒 0） |
| `ExtensionMethodResolveResult.cs` | `record ExtensionFileResult(string OutputCode, bool Changed, IReadOnlyList<ExtensionDiagnostic> Diagnostics, ExtensionStatistics Statistics)` |
| `ExtensionMethodResolvePreprocessor.cs` | 入口：`BindCompilation(CSharpCompilation)` / `Analyze(ExtensionMethodResolveOptions)`（整树一次，逐文件产出 binding 表，按文件路径+span 确定性排序）/ `RewriteFile(SemanticModel)` / `ResidualScan(CSharpCompilation)` / 格式化与校验（改动区格式化→整文件格式化→原文三级，同 JK/RR/C02） |

编译构造沿用 runner 标准（[ProjectPassRunner.BuildStandardCompilation](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L135-L154)）：`CSharpParseOptions(LanguageVersion.Latest)` + `NETCOREAPP` 符号 + 合成 global using 树 + `GetBasicReferences()` + `allowUnsafe:true` 单库编译，**整树 .cs 合并进同一 compilation**（跨项目用户代码符号即源码符号）。

### 4.2 跨项目 / 元数据边界

1. **图内用户 host（含多工程）**：merged compilation 把所有未排除工程的 .cs 合编，Lib 声明/App 调用是同编译内绑定，`ContainingType.Locations[0].IsInSource == true`、`DeclaringSyntaxReferences` 非空 → `InCompilationUser`，无条件改写。
2. **注入的支撑工程**：支撑工程源码由其注入 pass 复制进中间树且不被排除（过滤只发生在 Java 转换/指纹侧），同样是源码符号 → `InCompilationSupport`；改写为对支撑 C# 类型的全限定静态调用，中间树 build/test 直接验证，Java 侧交转换器映射到 compat 对应物。
3. **metadata host（BCL/NuGet）**：符号来自 `MetadataReference`，无源码位置 → `ExternalMetadata`；默认改写（决策 EM-EXTERNAL），是否「有 Java 落点」由适配器结合 TypeMappings 查表做**诊断分级**（Core 不依赖 CLI 的映射表——Core 只打分类标签，host→是否可映射的判定函数由适配器注入，保持 Core 无 TypeMappings 依赖）。
4. **已知边界**：runner 的 `GetBasicReferences()` 只含基础引用，不含中间树 restore 出来的 NuGet 程序集；这些程序集提供的扩展方法可能绑定失败（Info→CS2J1202）或错误绑定。**M0 在 sdk 上测量 reduced 站点的绑定成功率**；若不可绑比例可观，M3 增强适配器 `GetMetadataReferences()`：经 ProjectGraph 枚举 restore 输出目录程序集补充引用（按工程分组、构建 per-project compilation 是更重的备选，非必要不做）。见开放问题 Q2。
5. **单文件 verb 模式**：本 pass 无跨文件计划，但绑定依赖编译引用完整性；引用不全导致的非 reduced 结论与真「非扩展」无法区分时保守不改 + CS2J1290 Warning（仿 JK090/C02-1190 口径）。

### 4.3 规则全集（EM-x）

- **EM-BIND**：invocation 绑定唯一 reduced 符号 → 选中；多候选符号（`CandidateReason.Ambiguous`）→ CS2J1201，消息列出全部候选 host 全名与引入命名空间，不猜。
- **EM-CALL**：核心改写：`r.M(args)` → `global::Host.M(r, args)`；静态方法名保持 C# 原名；receiver 首参，求值次序不变；普通静态形态调用幂等不选。
- **EM-GEN**：方法类型实参省略策略（§2）；显式实参原样复制；推断失败/不可达站点由绑定层过滤。
- **EM-GEN-HOST**：嵌套泛型宿主的类型实参限定。
- **EM-REF**：`this ref/in` 接收者实参加注 `ref`/`in`；`scoped` 不发射。
- **EM-CHAIN**：自底向上链式收敛（非独立规则，由遍历保证），专测覆盖。
- **EM-LINQ**：host ∈ LinqHostSet → 计数跳过；host 集默认 Enumerable/Queryable，overrides 可扩不可删（防误关分流）。
- **EM-EXTERNAL**：metadata host 改写 + 可映射性分级（1203）。
- **EM-EXPRTREE**：表达式树内改写 + 重绑定同一性断言，失败 CS2J1210 单站回退。
- **EM-COND**：`r?.M()` 按 §2/M0 探针结论，v1 兜底 CS2J1205。
- **EM-GROUP**：reduced 方法组（非调用成员访问）→ CS2J1207。
- **EM-EXT14**：C# 14 扩展成员块 → CS2J1206 Error。
- **EM-IDEAL**：全改写点幂等/可重编译。

### 4.4 三阶段数据流与回退

1. **Analyze（整树一次）**：`CompilationReadyAsync` 时 Bind+Analyze：全树逐文件 binding 表、跳过/失败分类全部落定并排序（span 确定性）。
2. **Rewrite（逐文件）**：标准 spine；Changed=false 字节级复制（保 BOM）。
3. **Residual（AfterFiles）**：再编译中间树跑 scanner（同 generic-deepen 的 AfterFiles 范式），strict 下残余即 Error。
4. **回退**：中间树 build 相对基线新增错误时，按文件归属唯一化（本 pass 无跨文件计划，归属简单：出错文件即责任文件），排除该文件全部改写、从原始副本重放；不可归属 → CS2J1200 当次全回退（仿 JK030/RR030/C02-1100）。

### 4.5 residual 后置自检（闸门）

重写后再编译整树计数（strict Error，否则 Warning + 统计）：

1. 残留 reduced **调用**站点（LINQ host 豁免后）：**0**。
2. 残留 reduced **方法组**站点：0（或全部有 1207 活动诊断配对）。
3. 改写点重绑定同一性不为真（host OriginalDefinition/参数序列变化）：**0**。
4. 表达式树区内改写节点树形态变化：**0**。
5. 扩展方法声明上的 `this` 修饰被误改/误删：**0**。
6. 无主静态调用（host 名在新 compilation 中不可解析）：**0**（该项与中间树 build 互证）。

---

## 5. CLI 适配器与集成

### 5.1 ExtensionMethodResolvePassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/ExtensionMethodResolvePassAdapter.cs`（预计 120-180 行），形态**贴近 [GenericDeepenPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs) 但去掉生成文件分发**：

- `Descriptor => PassCatalog.ImplementedById(PassIds.ExtensionMethods)`；`VerifyBuild/VerifyTests => true`；`DeleteBuildOutputsAfterRestore => true`；构造六参仿 GenericDeepen（`options, verbose, filterUnsupportedProjects, compatNamespacePrefixes, verifyBuild, verifyTests`），另加一个可选 `Func<INamedTypeSymbol, ExternalHostMappingKind>?` 宿主可映射性判定（由 CLI 侧 TypeMappings 提供，v1 可先恒返 `Unknown`，即 1203 全量报告，见 §4.2-4/M3）。
- `CompilationReadyAsync`：`BindCompilation` + `Analyze(_options)`。
- `RewriteFileAsync`：调 `RewriteFile(model)`，合并统计/诊断（补 RelativePath），Changed=false 返回 null。
- `AfterFilesAsync`：仅跑 residual 扫描（复用 GenericDeepen 的目录枚举/bin/obj 过滤范式）。
- `OnRestoreFailed/OnBuildFailed/OnTestsFailed` → CS2J1230/1231 映射；`HasErrors`、`BuildChainSummaryLine`（改写数/跳过数/LINQ 豁免数/残余数）、`BuildChainDiagnosticLines`（Info 仅 verbose）。
- 不建门面类，测试走 runner + 通用 `preprocess` verb。

### 5.2 PassChainFactory / 选项 / 可达性

- switch 增 case，v1 直接 `new ExtensionMethodResolveOptions()`（同 C02，不加 lazy OptionsFactory）；不进 `BuildLegacyToggles`。
- `preprocess --list-passes` 翻转后显示 `extension-methods | Expression | Implemented | default off | .cs2j-extension-methods-src | C19`。
- 指纹：注册表自动覆盖，翻转一次性缓存失效；preserve 目录自动纳入。
- 单文件 verb：绑定不完整保守跳过 CS2J1290（§4.2-5）。

---

## 6. 诊断策略

码段 CS2J12xx（转换器现有扩展码 CS2J_EXT001 为 Warning 文本码、泛型码段 CS2J1001-1110 已占、pass 基建 CS2J9001/9002，不冲突；只追加不改号）。MSBuild 格式：`文件(行,列): warning/error CS2J12xx: [规则] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1200 | Error | 整树验证新增错误且无法归属文件 → 当次全回退 |
| CS2J1201 | Warning / strict Error | 扩展调用歧义（`CandidateReason.Ambiguous`）：列候选 host 全名与引入命名空间，站点不改 |
| CS2J1202 | Info→Warning / strict Error | 形似扩展糖但绑定 `None`/`OverloadResolutionFailure` 或引用不全（含单文件模式引用缺口），站点不改 |
| CS2J1203 | Warning / strict Error | metadata host（图外 BCL/NuGet）无 Java 落点映射；承接 CS2J_EXT001 语义但发生在预处理期、可阻断 |
| CS2J1204 | Warning / strict Error | `this ref/in` 接收者实参加注后语义不可证（ref kind 与实参 lvalue 不匹配等防御性场景） |
| CS2J1205 | Warning / strict Error | null 条件扩展 `r?.M()` 站点 v1 未收编（探针后若合法且 M2 落地则注销此码路径） |
| CS2J1206 | **Error（不分级）** | C# 14 扩展成员块/扩展属性等经典形之外的扩展成员 |
| CS2J1207 | Warning / strict Error | reduced 扩展方法组（非调用成员访问）v1 不改写 |
| CS2J1210 | Warning / strict Error | 表达式树同一性断言失败：该站点回退为原糖 |
| CS2J1220 | Warning / strict Error | residual 闸门发现残余（§4.5 任一计数超白名单） |
| CS2J1230 | Error | 中间树 restore/build 失败 |
| CS2J1231 | Error | 中间树 test 失败 |
| CS2J1290 | Warning | 单文件模式绑定不完整，保守跳过 |
| EM001 | Info | 逐站点明细（host 全名、分类、是否链式/泛型/ref），仅 verbose |

原则（catalog §4 风险 12）：不可判定即显式诊断，禁止 `/* TODO */`/注释/原样静默透传；`--strict` 统一升级；skip 仅经 overrides 按 host 全名显式指定且留 Info 痕迹。

---

## 7. 测试计划

### 7.1 Core 单测

新增 `tests/CSharpToJava.Tests/ExtensionMethodResolveTests.cs`（内联源码 → 全编译 → Bind/Analyze/RewriteFile → 文本断言 + 重编译可编译断言 + 重绑定断言）：

- **自定义基础**：同命名空间扩展 `s.Reverse()` → `global::Ns.StringExtensions.Reverse(s)`；多参；静态原形态幂等（二次运行零 diff）。
- **using 层级**：文件顶 using 引入；using alias（`using E = Ns.StringExtensions` 与别名命名空间两种）；global using（合成树注入）；**嵌套命名空间**（外层命名空间声明的扩展对内层文件可见）；外层类型中声明的扩展；两个命名空间同签名扩展的遮蔽/歧义 → CS2J1201。
- **泛型**：`Shuffle<T>` 推断省略（输出不带 `<...>` 且重编译推断成功）；显式 `M<int>` 保留；扩展多类型参数；扩展接收者为泛型接口 `IEnumerable<T>`；**嵌套泛型宿主** `Outer<T>.Ext`（host 带类型实参）。
- **链式/混合**：全扩展链；扩展夹实例方法/属性；扩展返回值再调实例方法。
- **实例同名优先**：receiver 类型有适用实例 M 时不改；实例 M 不适用（参数类型不符）回落到扩展并改写。
- **null 接收者**：中间树等价性主要在 fixture 运行时断言；Core 层断言形态为首参静态调用。
- **ref/in**：`this ref Span<int>` → `ref r`；`this in` → `in r`；普通值无修饰。
- **LINQ 旁路**：`src.Where().Select()`、Queryable 调用零改写（计数 `LinqOwnedSkipped`）；混合链只改用户节点。
- **表达式树**：`Expression<Func<IEnumerable<int>, IEnumerable<int>>> e = x => x.Where(...)` 形态站点改写/豁免策略与树节点同一性（EM-EXPRTREE/EM-LINQ 组合）；自定义扩展在表达式树中 → 改写后 MethodInfo 同一。
- **外部 host**：合成 metadata 引用（编译一个小程序集再引用）中的扩展 → 改写 + `ExternalMetadata` 分类。
- **方法组 / `?.` / C#14**：分别出 1207、1205（以探针结论为准）、1206。
- **声明不动**：`this` 形参修饰保留；residual 第 5 项。
- **回退**：可测 hook 注入必然编译失败的改写 → CS2J1200 全回退、产物与输入字节一致。

### 7.2 项目级 sln fixture（多工程）

新增 `tests/SampleExtensionMethodsSolution/`（`SampleExtLib` + `SampleExtApp` + `SampleExtTests` + `.sln`，net10.0，按 SampleGenericDeepenSolution/SampleStaticGenericSolution 范式以 Content 挂入 ProjectTests）：

- Lib：普通扩展、泛型扩展、嵌套命名空间扩展、`this ref` 扩展、嵌套泛型 host 扩展、与实例方法同名扩展、null-guard 扩展（方法内解引用才抛）。
- App：using 引入调用、链式调用、跨项目调用。
- Tests（在中间树 xUnit 运行）：null receiver 不抛、链式结果、泛型推断结果、ref 扩展生效、同名优先级。
- 断言：`result.Success`、统计计数、中间树无 reduced 调用残留（residual 六项 0）、`dotnet build -c Release` 退出码 0、`dotnet test -c Release` 全绿、clean 文件字节复制与 BOM 保持用例。

### 7.3 与支撑库 / 兄弟 pass 串行

- 复用 `SampleArraySolution`（E12）、`SampleUnsignedSolution`（E09）、`SampleRefOutInV2Solution`（C01）：在各自 pass 后串跑本 pass，断言中间树 build/test 全绿且本 pass 统计正常（当前支撑库无扩展方法，主要锁「五锚可见性 + 互不干扰」不变量）。
- E13/E14 交互：构造含扩展调用的 readonly struct 迁移样本与迭代器样本，在 §3.2 EM-ORDER 链位下断言 E13/E14 先消费糖、C19 后显式化；若评审选不追加锚的备选，本用例改为等价性证明门。
- E16 交互：复用 [LinqCompletionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LinqCompletionTests.cs) 等 LINQ 测试簇，本 pass 开/关两跑转换器结果比对（LINQ 站点产物零 diff）；混合链样本进 fixture。

### 7.4 Release build/test 与 sdk 基线

- fixture 与 `D:\csharp-sdk`（只读）预处理树分别 `dotnet build -c Release` / `dotnet test -c Release`；sdk 新增失败为零（foundation Validation 同口径）。
- M0 测量轮输出：reduced 调用总数、按分类（图内/metadata/LINQ/未绑定）占比、1201-1207 站点分布，作为收益与默认开启评审基线。

### 7.5 residual 闸门测试

对 fixture 与合成样本断言 §4.5 六项计数；strict 下任一 >0 退出非 0；非 strict 仅 Warning 且树仍可编译。

### 7.6 CLI / 注册表测试

- 翻转前 `--enable-pass extension-methods` → CS2J9001 退出 1（现状锁定）；翻转后 `--list-passes` 状态 Implemented、目录名正确；默认关时无目录/无耗时；启用后链路执行并把产物串给下一环；`preprocess --pass extension-methods -s … -d …` 单跑可达；单文件模式引用缺口 → CS2J1290。
- 注册表：拓扑顺序断言（19 id 锁定链中的位置）、末位仍是 java-keywords、默认启用集合不含本 pass、stub 计数 22、无重复 id/目录。
- 一致性：ProjectPreprocessorsConsistentTests 追加段；指纹令牌含 `pass:extension-methods=...`。
- 两份既有 `ExtensionMethodTests`：**全部保持通过**（声明侧行为与索引扫描本 pass 不触碰）；不重写其断言。

---

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足：

1. **中间树正确性**：fixture sln 与 sdk 预处理树 `dotnet build -c Release` 零错误、含测试工程时 `dotnet test -c Release` 全绿；sdk 相对基线新增失败为零。
2. **残余闸门**：§4.5 六项在 fixture 全 0；sdk 残余只出现在显式 CS2J12xx 报告站点，且全部站点分类有主，无「未知残余」。
3. **全仓测试不回归**：`CSharpToJavaConverter.slnx` Release build + 三个测试工程 Release test 全绿（含已登记红灯不增加）；两份既有 ExtensionMethodTests 与 LINQ 测试簇零 diff/零新增失败。
4. **转换器协同冒烟**：图内用户 host 的显式静态调用被现有静态限定路径正确消费（代表性场景：null guard 扩展、泛型扩展、链式、跨项目）；开启本 pass 后 `ProjectExtensionMethodCheckPass` 对图内站点零告警；外部 host 的 1203 报告集经 triage 与 CS2J_EXT001 历史告警集对账（不新增未知 host 类）。
5. **LINQ 零影响证据**：E16 两种模式（procedural 重写 / prefer-stream）下 LINQ 簇结果开/关本 pass 逐字节或逐语义比对一致；C26 未来 host 集无需改动（契约冻结在 EM-LINQ）。
6. **诊断可运维 + 文档**：sdk Warning/Error 清单 triage 完毕，overrides skip/force 验证；本设计与 plan（`docs/superpowers/plans/2026-09-29-c19-extension-methods.md`）定稿；探针结论（`?.`/C#14/metadata 绑定率）回填本文。

不满足时任保持 opt-in（catalog §4 风险 10）。

---

## 9. 里程碑

| 里程碑 | 内容 | 可独立提交/验证 |
|---|---|---|
| M0 | Core 骨架（options/statistics/diagnostics/入口空壳）+ 适配器 + 注册表翻转（规则全空走字节复制）+ 注册表/CLI 测试；**四个探针**：① `r?.M()` 合法性与绑定；② C#14 扩展成员在 `LanguageVersion.Latest` 的形态；③ sdk reduced 站点分类计数与 metadata 绑定率；④ LinqRewriter 对静态形态失配的最小复现（固化 EM-LINQ 必要性） | 中间树零差异；`--list-passes` 可见；测量数回填 §8 |
| M1 | EM-BIND/EM-CALL/EM-GEN/EM-GEN-HOST/EM-IDEAL + Core 单测（using 层级/泛型/嵌套 host/同名优先/幂等）+ residual 1/3/6 项 + fixture 骨架（build/test 全绿） | 不改 receiver 以外任何节点 |
| M2 | EM-REF + EM-GROUP + EM-COND（按探针）+ EM-EXPRTREE 同一性 + ref/方法组/表达式树用例；fixture 补 null receiver 运行时断言 | 单测/项目测 |
| M3 | EM-EXTERNAL + host 可映射性注入点（CLI 侧 TypeMappings 判定，先报告后分级）+ 1203 与 CS2J_EXT001 对账；metadata 引用增强按 M0 数据决定（§4.2-4）；多工程 fixture 完整版 + 支撑库串行三样本 | sdk 轮 |
| M4 | EM-LINQ 混合链策略定稿 + E16 双模式零影响比对 + E13/E14 链位用例 + 回退实战 + strict/overrides + residual 全门 + sdk 基线比对 | 候选默认开评审 |
| M5 | 过 §8 门槛后 descriptor 翻默认开；plan 收尾；登记转换器侧后续 PR 清单：①`ProjectExtensionMethodCheckPass`/CS2J_EXT001 退役或降级为纯 residual 复核；②`ExtensionMethodIndex` 与 `Cs2jLibrary.ExtensionMethodIndex` 退役评估（跨项目预填路径）；③InvocationExpressionTransformer 逐 host 补丁（MemoryExtensions 等）收口；④`RewriteExtensionMethods` 选项去留；⑤C#14 是否另注册观察 stub | 默认开 |
| M5+（不承诺） | C26 通道化时回链 EM-LINQ 契约；方法组→lambda、`?.`→临时提升随 expression-desugar 基建收编（注销 1207/1205） | 独立条目 |

---

## 10. 风险

1. **LINQ 被误显式化（最高风险）**：LinqRewriter/LinqQueryDesugarer 结构性依赖 reduced member-access 形态（§3.4 证据），一旦 C19 改写 Enumerable/Queryable 站点，E16 链识别静默失效——能编译、单测可能不直接红（过程式展开退化为普通调用翻译），属 catalog §4 风险 12 式静默事故。缓解：EM-LINQ host 集**不可经选项删空**（只可扩）、residual 双向计数（LINQ 站点被改写数必须为 0，进闸门）、M0 探针④固化失配复现、M4 双模式比对。
2. **与 E11/E13 reduced 启发式的次序耦合**：MethodMigrator/BoxingReachabilityAnalyzer/DefaultParameterCatalog 都吃 reduced 形态；EM-ORDER 追加锚（§3.2）把本 pass 放到它们之后是主缓解；若不追加，须承担等价性证明成本（开放问题 Q1）。C01 之后落地的成员去糖族（C11-C14/member-desugar）原则上在本 pass 之前（declarative 阶段），翻转其链位时须复核（§11）。
3. **外部 host 大库告警噪声**：EF Core/`Microsoft.Extensions.*` 大量扩展方法经 metadata 绑定；1203 若全部 Warning 可能淹没日志。缓解：分级（TypeMappings 命中静默、未命中才报）、sdk triage 门槛（§8-4）、必要时按 host 全名 overrides；同时这正是比现状 CS2J_EXT001 更早暴露问题的收益，不能因噪声回退为静默。
4. **metadata 引用不全导致假「非扩展」**：标准编译缺 NuGet 程序集时绑定失败，站点被漏改且 1202 可能被当作噪声忽略。缓解：M0 绑定率测量、CS2J1202 在 strict 下为 Error、M3 按数据增强引用（开放问题 Q2）。
5. **泛型/嵌套 host 显式形态的编译等价性**：省略方法类型实参依赖「receiver→显式首参推断同构」，理论等价但须用推断边界样本压测（不可显式书写类型、tuple 元素名、lambda 返回类型推断、nullable 引用类型上下文）；嵌套泛型 host 的类型实参取自构造符号，须防 `OriginalDefinition` 误用回未构造形。
6. **null 条件 / 方法组 / C#14 三个边界**：探针未完成前规则不得凭记忆实现；v1 一律显式报码（1205/1207/1206），宁可 opt-in 期不覆盖也不静默产出（开放问题 Q3/Q4）。
7. **旧双份逻辑退役节奏**：本 pass 默认开启后，`ExtensionMethodIndex`/`ProjectExtensionMethodCheckPass` 与转换器实例形渲染仍在，两套事实短期并存；必须在 M5 清单中明确退役顺序，避免长期三套口径（catalog §4 风险 10）。退役前转换器冒烟（§8-4）是并存安全的前提。
8. **中间树成本**（catalog §4 风险 11）：新增一环一 restore；严格走注册表指纹/preserve；residual 再编译只 parse 不 restore，不引额外还原。
9. **表达式树同一性**：理论同形（CLR 本就发静态 Call 节点），但自定义扩展 + 显式 host 限定在查询提供方侧是否被 ExpressionVisitor 严格比对的场景须实测；1210 单站回退保底，不影响其他站点。
10. **热文件并发冲突**：注册表三文件 + 两个注册表测试是并行 worktree 共同热点，解法见 §11。

---

## 11. 并行协调

| 并行条目 | 关系 | 协调结论 |
|---|---|---|
| 五锚（delegate/tuple/ref-out-in/unsigned/array-method，E05/E06/C01/E09/E12） | 已实现、是 RunAfter 依赖，无开发并行面 | 无阻塞；fixture 串行锁可见性不变量；当前支撑库无扩展方法（2026-09-29 grep 零命中），五锚是面向未来的不变量 |
| **C26 linq-channel** | LINQ 即扩展方法；其 stub RunAfter 本 pass | 次序复核结论见 §3.4：**不改注册次序**，C19 以 EM-LINQ 排除让出糖所有权；C26 设计文档须回链本结论并复用同一 host 集（建议提取共享常量，两处硬编码不长期并存）；C26 三路分流门在 reduced 形态上工作不受影响 |
| **E16 嵌入式 LINQ**（现状，非注册 pass） | 在主转换器内、跑在全部 preprocessor 之后（`EnableLinqRewrite && !EffectivePreferStreamApi`） | C19 不落地前/后 E16 输入均为 reduced 糖（EM-LINQ 保证），E16 零改动；两种模式（procedural / prefer-stream）各跑零影响比对（§7.3）；混合链若失配，策略选「整链保守跳过 + Info」而非抢改 |
| **C15/C31/C34**（numeric-boundary / index-range，未来支撑库） | 均可能注入带扩展方法的新支撑代码（C31 可能并入 Cs2jArray；C34 复用 compat Decimal 不新建工程） | 其 stub 翻转 PR 必须：①若新注入产物含扩展方法，把自身 id 加进 `extension-methods` 的 RunAfter（并在本 pass 设计/plan 留 checklist 项）；②支撑 host 经 merged compilation 自动进入可见集，无需本 pass 改代码；③其 fixture 与本 pass 串行跑。现 stub 锚（numeric-boundary→unsigned、index-range→array-method）不保证拓扑在本 pass 之前，**翻转时以 RunAfter 显式链接为准，不靠阶段名推断顺序** |
| **C02 generic-deepen** | 同改方法体，但规则面不相交：C02 插 `typeof(T)` 锚点语句/签名手术，C19 改调用表达式 | 二者对 reduced 符号无共同节点；receiver 为含 C02 锚点方法调用的纯表达式组合时各自独立；Core 目录不重叠；C02 若先合，复用其 `ExpressionTreeFacts` 与 residual/回退骨架 |
| C11-C14 member-desugar / C03-C37 等未落地族 | 声明/语句去糖若产生新调用糖，理论消费者次序需复核 | 协调规则同 C15：凡「注入/产生新调用表达式」的通道，翻转时显式 RunAfter 链接到本 pass 之前；本 pass 默认开启后任何新注册 pass 须在评审中回答「是否产生扩展调用糖」 |
| C08 java-keywords | 链尾命名 pass | 本 pass 全限定 host 名中的标识符若撞 Java 关键字由 C08 兜底（C08 在最后）；生成节点无 `__cs2j_` 新标识，无新增碰撞面 |
| 公共热文件 | `PassStubCatalog.cs`、`PassCatalog.cs`、`PassChainFactory.cs`、PassChainOrderTests、PassStubCatalogTests、ProjectPreprocessorsConsistentTests | 各 worktree 仅追加各自 id 的行与各自测试段；按 rebase 解冲突；禁止顺手重排既有顺序；stub 计数（23→22）与锁定链（18→19）的断言修改归本 pass 分支所有，他分支若先翻转其他 stub 需相应 rebase 调整计数 |

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c19-extension-methods`
- 目录：`.worktrees/preproc-c19-extension-methods`
- base：`953293b9`（dev-3.x HEAD；完整 SHA `953293b908e9143437c45f287fb4277e890c398e`，"docs: update doc base commit"；2026-09-29 只读核验工作区干净）
- 创建命令（**交实施环节执行，本文档不执行、不代为创建**）：

```bash
git worktree add .worktrees/preproc-c19-extension-methods -b feature/preproc-c19-extension-methods 953293b9
```

实施约束（写入实施 plan，不在本设计任务执行）：仅在该 worktree 内改码；按 §9 里程碑逐提交；默认关先行，过 §8 门槛才翻默认开；转换器代码改动留 M5 后续 PR；探针结论先回填本设计再写规则代码。

---

## 附：关键开放问题（评审输入）

1. **EM-ORDER**：翻转时 RunAfter 是否追加 `make-readonly`/`iterator-lowering`（推荐追加），还是投入等价性证明成本维持五锚（§3.2）。
2. **metadata 引用**：M0 测得 sdk 扩展站点的 metadata 绑定率后，是否需要在 M3 为 merged compilation 补 restore 程序集引用 / per-project compilation（§4.2-4）。
3. **null 条件扩展 `r?.M()`**：合法性探针结论与 v1 是否收编（临时提升）还是停在 CS2J1205（§2 EM-COND）。
4. **C#14 扩展成员块/扩展属性**：探针确认形态后，CS2J1206 长期报错是否足够，还是另注册观察 stub（§2 EM-EXT14）。
5. **方法组 reduced 站点（EM-GROUND）** 与**旧资产退役顺序**（索引/校验 pass/逐 host 补丁/实验选项，M5 清单）的排期。
