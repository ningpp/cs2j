# C50 C# 14 `field` 关键字去糖（半自动属性）设计

**日期**: 2026-09-29
**范围**: 在 C11 `member-desugar` 族 pass 的 `Properties/` 规则目录内新增 **FieldKeyword 前置微改写规则**：把访问器体内引用编译器合成后备字段的 `field` 上下文关键字（half-implemented / field-backed properties，C# 14 GA）归一为「显式私有后备字段 + 普通访问器体」，产物与 C11 属性去糖的输入形态完全同形；本规则零 stub 翻转、无调用点改写、不新建 pass、不触转换器
**状态**: 设计待评审；**C11–C14 已由维护者手动合并主干 dev-3.x**（2026-09-30 确认），可直接在 `MemberDesugar/Properties/` 内实施 FieldKeyword 微改写规则
**族属**: catalog P2「成员去糖族」C11 的 C# 14 子规则（[preprocessor-catalog.md:968-980](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L968-L980)）；catalog 明定「并入 C11，产物与 C11 完全同形故 Java 侧零新增」（[五次补全 L430](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L430-L430)）

---

## 1. 背景与目标

### 1.1 `field` 是对合成字段的语义引用，不是普通标识符

C# 14（.NET 10）为属性引入上下文关键字 `field`：在 get/set/init 访问器体内，主表达式 `field` 绑定到编译器为该属性合成的 private 后备字段；同时允许 auto 访问器（无体 `get;`/`set;`/`init;`）与 full 访问器（有体）混搭。典型形态：

```csharp
int P { get => field + 1; set => field = value; }          // 两侧 full
string Name { get; private set => field = value?.Trim(); } // auto get + full set
string Lazy => field ??= Compute();                         // get-only 表达式体，field 仍合成
```

现有转换器对访问器体内表达式没有任何 `field` 特判（已全仓 grep 确认零命中）：块体经 `StatementTransformer.TransformBlock`/`TransformBlockToStructuredBody`、表达式体经 `ExpressionTransformerFacade` 按**普通标识符**处理（[PropertyTransformer.cs:34-36](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L34-L36)、[L57](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L57-L57)、[L71-72](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L71-L72)），Java 产物中出现未定义符号 `field`；后备字段发射路径只覆盖 `IsAutoProperty` 与带初始化器属性（[L23-30](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L23-L30)），field-backed 属性既不被识别为 auto，标识符也无法解析，属确定性错误输出。

### 1.2 目标

在 C#→C# 预处理层把 field-backed 属性**在 C11 属性收集之前**归一为 C11 的常规输入：

1. 合成显式 private 后备字段（命名/类型/static/readonly 全部走 C11 既有契约，§2 决策 2）；
2. 访问器体内所有绑定到合成字段的 `field` 节点替换为该字段名；无体 auto 访问器展开为读写字段的普通访问器体；
3. 属性初始化器 `= e` 平移到合成字段（官方语义：直接初始化 backing field、**不调 setter**）；
4. `[field: X]` 特性迁移到合成字段；
5. 归一完成后该属性在语法上与「手写后备字段的普通 full 属性」不可区分，由 C11 PropertyRule 同路径完成 `get_X`/`set_X` 归约与全部调用点改写——**C50 自身纯声明侧、零调用点**。

覆盖：get/set 双 full、仅 get（含属性级表达式体）、仅 set、auto/full 混搭（含 `private set` 可访问性）、`init` 组合、块体与表达式体、属性初始化器、static、virtual/override/sealed/new、`??=`/复合赋值/`ref field` 实参、`[field:]` 特性。明确边界：ref 返回属性、partial 属性（C20 前）、接口属性（M0 钉死后定）、事件与索引器（语言即无 field）、abstract（无体不命中）。

### 1.3 Roslyn 版本先决核实结果（M0 前的静态结论）

- 仓库引用 **Microsoft.CodeAnalysis.CSharp / .Workspaces / Workspaces.MSBuild 均为 4.12.0**（[CSharpToJava.Core.csproj:12-14](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12-L14)），Core TFM 为 net10.0；无 `Directory.Packages.props`、无 `global.json`，包版本在各 csproj 自管。
- Roslyn 4.12 对应 VS 17.12 / .NET 9 周期，`LanguageVersion.Latest == C# 13`。全仓所有语义 pass（含 JK/RR/tuple 适配器与 [ProjectCompilationBuilder.cs:30](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs#L30-L30)）统一用 `CSharpParseOptions(LanguageVersion.Latest)`，无一处 `Preview`。
- 官方时间线（[What's new / field 提案](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-14.0/field-keyword)）：`field` 在 **C# 13 为 preview**（须 `<LangVersion>preview</LangVersion>`），**C# 14 GA**（Roslyn 4.14，VS 17.14 / .NET 10）。
- **结论**：4.12 + Latest 下 .NET 10 源码中的 `field` 不享受上下文关键字绑定，SemanticModel 拿不到 `IsImplicitlyDeclared` 的 backing field（确切表现——普通标识符 + CS0103、还是 preview 版本门诊断——列入 M0 探针 FK-P0，不靠记忆写死诊断号）。**C50 的硬前提是把 Roslyn 升级到 ≥4.14.x**（与 C49/C51 同属 catalog 标注的「Roslyn 版本先决」批，建议由 C52 版本防线统一升级一次，§11）；4.12 + `LanguageVersion.Preview` 仅作 M0 评估的过渡退路，不推荐（preview 语义可能与 GA 漂移）。
- 在版本前提满足前，field-backed 源文件进基线错误集，pass 不会产生半截改写（族迭代器以基线错误集为 Diff 基准，C11 §4.3）。

### 1.4 硬不变式

1. 整树可编译：归一后中间树相对基线无新增错误；field 属性以所属 PropertyPlan 为原子单位，全转或全不转（沿用 C11 plan 回退迭代器）；
2. **判定纯语义化**：只有 `SemanticModel.GetSymbolInfo` 返回 `IFieldSymbol.IsImplicitlyDeclared == true` 且 `AssociatedSymbol` 为该属性的 `field` 节点才允许改写；禁止按标识符文本替换（§2 决策 1 的反例集）；
3. 产物与 C11 同形、幂等：二次执行分析器不再发现 field-backed 属性，零 diff；
4. 不丢特性、不丢初始化器、不丢访问器；不支持形态显式诊断并让整个 PropertyPlan 保持原样。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | `field` 绑定判定 | **只认 SemanticModel**：访问器体内 `IdentifierNameSyntax`，`Identifier.Text == "field"` 且 token 非 `@` 转义，且 `GetSymbolInfo(node).Symbol` 为 `IFieldSymbol { IsImplicitlyDeclared=true }`、`AssociatedSymbol`（或等价归属链）等于所在 `IPropertySymbol` | `field` 是上下文关键字而非保留字。反例：① `@field`（转义标识符，合法，绑用户字段/局部，不动）；② C#14 起访问器内声明 `string field` 局部变量本身即编译错误（[.NET 10 breaking changes 第 9 条](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/breaking-changes/compiler%20breaking%20changes%20-%20dotnet%2010)，可编译基线中不存在）；③ 访问器外（普通方法/构造器）的同名标识符本就不绑合成字段，符号判定天然排除；④ 同类用户字段/参数同名时，C#14 主表达式 `field` 仍绑 backing field（编译器另发「旧版本会绑别的符号」warning，M0 钉 id），语义判定跟随编译器不误伤 |
| 2 | 后备字段命名/类型/只读性 | 命名复用 [JavaNaming.GetAutoPropertyBackingFieldName](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L99-L127)（camelCase + Java 关键字逃逸 + 首声明裸名/后撞 `Value` 后缀，碰撞检查已排除 `IsImplicitlyDeclared` 字段），并在 C11 共享 BackingFieldNaming 预留表登记；类型 = 属性类型（含 nullable 注解按符号原样）；`static`/`readonly` 修饰符**直接读合成字段符号的 `IsStatic`/`IsReadOnly`**，不自行推断 | get-only `{ get => field+1; }` 合成字段 readonly；`=> field ??= Compute()` 字段可写；set-only 可写；init-only 可写性等同 auto 属性。符号事实唯一可靠，规则不复制编译器的 null-resilience/写站点分析（[提案 Nullability 节](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-14.0/field-keyword)） |
| 3 | 字段发射位置与可见性 | `private`（显式接口实现属性同为 private 字段）、字段声明插在属性原位置之前，trivia/XML doc 保留归属规则同 C11 PD-1 | 合成字段与用户手写 backing field 在后续 C11 眼中无差别；字段名走预留表防撞（用户已有同名字段时算法加 `Value`） |
| 4 | 访问器体改写 | 仅替换决策 1 命中的 IdentifierName 节点为合成字段标识符（限定形态按 C11 既有纪律：静态成员/必要处加容器名，默认裸名与现有 PropertyTransformer 直写风格一致）；块体、表达式体、`field = value`、`field +=`、`field ??=`、`Set(ref field, value)` 全部是同一标识符替换的自然覆盖 | 官方示例 `set => Set(ref field, value)` 证明 field 是普通变量表达式，ref 实参合法；不新增表达式形态改写 |
| 5 | auto/full 混搭展开 | 无体 auto 访问器展开为引用合成字段的普通体：`get;`→`get => <f>;`、`set;`→`set => <f> = value;`、`init;`→`init => <f> = value;`（块体/表达式体风格随对侧或统一表达式体，由 C11 格式化收口）；访问器自身可访问性修饰符保留 | 混搭本身也是 C#14 新语法，归一产物必须两侧皆 full 才能在降级后的语言形态上自洽；展开语义 = 编译器对 auto 访问器的隐式读写 |
| 6 | 半自动单访问器 | 仅 get（`P => field…` 或 `{ get => field… }`）：合成字段、无 set；仅 set（`{ set { …field… } }`）：合成字段、无 get，属性在源码层即只读只写，规则不补发访问器 | 官方提案「Set-only properties may also use field」明示合法；无 set 时 ctor 赋值直写字段的现有语义由 C11 PD-7 承接，与 C50 无关 |
| 7 | 属性初始化器 | `int P { get; init; } = 1` 与 field 组合时，初始化器**移到合成字段声明**（`private int p = 1;`），访问器体不含初始化调用 | 官方明示 backing field 直接初始化、不调 setter（virtual 属性场景可观察差异）；与 C11 PD-1 对 auto 属性初始化器的处理同形，保证语义不漂移 |
| 8 | 特性迁移 | 属性上 `AttributeArgumentList` 目标为 `field:` 的 AttributeListSyntax **整张迁移**到合成字段声明（保留多特性合并列表形态）；`[get:]`/`[set:]`/`[init:]` 留各自访问器；无目标限定符与 `[property:]` 的特性不动，交 C11 PD-10 AttributeUsage 路由 | `[field: NonSerialized]`、`[field: ThreadStatic]` 等影响序列化/GC/运行时语义，丢失即行为错误（§10 风险 2）。官方：field 目标特性仅当访问器确用 backing field 时合法，纯计算属性上即 C# 编译错误，可编译基线不会出现 |
| 9 | init/required | `init => field = value;` 与 set 同改；随后由 C11 PD-3 放宽为普通 `set_X`；`required` 与 field 无交互（required 是属性级约束，仍由 C11 处理） | 与 C27 同批验证组合形态（catalog C50「与 C27 组合同批验证」） |
| 10 | static / virtual / override / sealed / new / abstract | 命中属性的修饰符全部随 C11 平移；**C50 只额外加一个字段**：override field-backed 属性的合成字段归派生类型自身（与 override auto-property 同构，M0 探针 FK-P2 反射验证）；abstract 无体不命中、不产字段 | 官方 spec 未禁止 override 使用 field；`new`/virtual/sealed 不影响字段归属；字段从不 virtual/override |
| 11 | 接口属性 | 抽象接口属性无体不命中；DIM 带体属性中的 `field`：实例字段在接口中非法（预期 C# 编译错误，进基线错误集，规则不触发）；**接口静态属性 field 是否合法（接口可含静态字段）列 M0 探针 FK-P3**，v1 无论结果先按 CS2J5127 显式跳过 | 不猜测语言边界；探针定案后再决定收编 |
| 12 | ref 返回属性 | `ref T P`/`ref readonly T P` 上的 field 用法：官方明示 **field 不可用于 ref-returning properties**；可编译基线中该形态不存在；规则对 ref 属性整体不命中（沿用 C11 CS2J5115 跳过闸门） | `ref get => ref field` 非法；`Set(ref field, value)` 是普通 ref 实参，合法且属决策 4 |
| 13 | partial 属性（C#13） | C20 partial-merge 落地前：含 field 的 partial 属性按 C11 CS2J5114 整体跳过；C20 落地后：官方允许 implementing 声明的访问器为 field-backed（[partial-member 文档](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/partial-member)），合并后的完整声明按常规命中 | 不提前实现 partial 合并，顺序由链位保证（partial-merge 在 Lexical，member-desugar 在 Declarative） |
| 14 | readonly struct / readonly 实例属性 | 字段 readonly 性读符号（决策 2）；访问器上的 `readonly` 实例修饰符随 C11 平移到方法；readonly 访问器内不可能出现写 field（编译器 CS1604 类错误，基线拦截），规则不造特例 | 与 E13/C07 的交互全部沉淀在 C11 产物层；member-desugar RunAfter 锚点已保证 struct 通道先行 |
| 15 | 与用户手写后备字段属性的关系 | 不命中：手写 `_x` 的 full 属性内没有绑合成字段的 `field`，C50 零行为；即便访问器里出现标识符 `field`，决策 1 的符号判定不通过即不动（安全网 CS2J5126） | 文本匹配方案会误伤的正是此类，语义判定是本规则的立身之本 |
| 16 | 执行相位 | **C11 PropertyRule 的声明侧前置微改写**，不独立成 plan、不新增 MemberDesugarKind、不改调用点；FK 归一后的属性带批注进入同一 PropertyPlan，字段已在预留表中，PropertyRule 按 full 属性处理且不重复发字段（§4.4 接缝） | 产物在 RewriteFile 同一轮内成形；FK 引发的重编译错误经 PropertyPlan.TouchedFiles 归属回退，不另造回退通道 |
| 17 | nullable 注解 | 合成显式字段的 nullable 注解以 backing field **符号自身**的注解为准，不取属性声明的注解文本 | 官方 null-resilience 分析：非注解引用类型属性的 backing field 可能被编译器判为注解态（`string Lazy => field ??= Get()` 的空安全弹性）；直接读 `IFieldSymbol` 注解可白嫖编译器结论，中间树 nullable 诊断零新增 |
| 18 | 访问器体内的表达式树/dynamic | 访问器体内 lambda/表达式树中出现的 `field`：符号判定照常命中，替换后树仍可编译，但表达式树字面引用的字段名从编译器合成名变为契约字段名（反射形状变化）——v1 命中时随 PropertyPlan 按 CS2J5117 同族保守跳过（M2 探针确认真实频率后再议）；dynamic 体内 `field` 绑定仍为编译期字段符号（field 不参与动态分派），正常改写 | C11 PD-11 表达式树闸门作用在属性使用站点；FK 补的是**访问器体内嵌**这一声明侧位置，共用 MemberDesugarSyntaxFacts 的树包含判定 |
| 19 | nameof/反射 | 访问器内 `nameof(field)` 合法且等于合成字段名（`"<P>k__BackingField"`）；归一后若保留为 `nameof(<契约字段名>)` 字面量行为变化（字符串值不同），统一在 FK-3 把绑定 backing field 的 `nameof(field)` 直接字面量化为原编译器字段名字符串会固化实现细节——v1 选择随树替换为 `nameof(<字段名>)` 并计入 CS2J5125，M0 确认是否有真实命中 | 反射访问合成 backing field 本就依赖编译器内部名，属不受支持契约；与 C11 决策 11 的「反射不保证」声明一致 |

---

## 3. 链位与族契约

### 3.1 归属：`member-desugar` / `Properties/`，零 stub 翻转

- C50 没有自己的 descriptor / adapter / 中间目录 / CLI 开关 / RunAfter 边；member-desugar 的 stub 删除与 PassCatalog 注册已由 C11 锚点完成——现注册于 [PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)（[c11-design §3.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L94-L108)）。
- Stage=Declarative、RunAfterIds=`{struct-value-semantics, closure-lift}`、目录 `.cs2j-member-desugar-src`、verify 双开、无支撑库，全部沿用 C11。
- 代码全部落在 `src/CSharpToJava.Core/MemberDesugar/Properties/`（C11 独占目录，经锚点合入后在其上追加文件）；不碰 `MemberDesugar/` 根目录共享文件（若接缝能力不足，按 C11 §4.5 约定由锚点扩共享层，不在 Properties 复制）。

### 3.2 catalogItems 文本

C11 锚点已翻转 stub 并注册于 PassCatalog，catalogItems 现状为 `"C11"`（[PassCatalog.cs:241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L241-L241)）。catalog C50 条目明定「并入 C11」，**建议锚点翻转时直接登记为 `"C11, C50"`**（C50 与 C11 同批交付，见 §8/§9）；锚点翻转已收窄为 `"C11"`，C50 合入时按 C11 §3.3-10「只追加不改写」惯例改为 `"C11, C50"`，一行字符串预期冲突。**2026-09-30 勘误**：维护者已合并 C12–C14，catalogItems 真值现为 `"C11, C12, C13, C14"`；C50 合入时在此真值上追加为 `"C11, C12, C13, C14, C50"`，勿回退 C12–C14。CatalogItems 不含 C50 期间 `--list-passes` 不宣称覆盖 C50。

### 3.3 规则相位：声明收集的前置微改写

同一 RewriteFile 轮次内，声明侧顺序固定为：

1. **FieldKeywordNormalizer（C50）**：枚举 field 命中 → 合成字段 + 改访问器体/展开 auto 侧/移初始化器/迁特性，节点挂 `RewriteAnnotation` 与内部 `FieldKeywordSynthesizedAnnotation`（属性符号 ↔ 合成字段名）；
2. **PropertyDeclarationRewriter（C11 PD-1/PD-2）**：对带批注属性按 full 属性走（不再判 auto、不再发字段），字段名从批注/预留表读取；
3. **PropertyUseSiteRewriter（C11 PD-6 等）**：调用点改写与 C50 无关，照常按 `IPropertySymbol` 绑定。

分析侧（整树一次）FieldKeywordResolver 作为 MemberDesugarAnalyzer 的补充索引：为每个 field-backed `IPropertySymbol` 记录命中节点集合与合成字段事实，挂在该属性的 PropertyPlan 上（新增 `FieldKeywordInfo` 子结构，由 C50 文件定义、C11 PropertyPlan 预留一个可空挂载点——属向锚点申请的接缝，§4.4）。

### 3.4 与 C27（init/required/with）的共处顺序

- 同 pass 同文件无节点争抢：C27 的 Records reshape 直接产终形，其产物属性若出现 field（位置 record reshape 不会产生 field 语法，只产 auto/full），不命中 C50；
- init+field 组合（`{ get; init => field = …; } = 1`）由 C50 先归一、C11 PD-3 再放宽 init，两层计数分别留痕；
- `with` 站点不涉 field 标识符，C27 WR 规则与 FK 无交集；同批 fixture 组合验证即可（§7）。

---

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/MemberDesugar/Properties/`（C11 合入后追加），命名空间沿用 C11 Properties 规则目录。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `FieldKeywordResolver.cs` | **语义识别**：给定 SemanticModel + PropertyDeclarationSyntax，判定 field-backed（决策 1）；枚举访问器体内全部命中节点（块体/表达式体/属性级 ExpressionBody）；读取合成 `IFieldSymbol` 的 Type/IsStatic/IsReadOnly/NullableAnnotation；产出 `FieldKeywordInfo`（属性符号、命中节点 span 列表、字段事实、`[field:]` 特性列表、初始化器迁移需求）；`@field`/访问器外同名标识符零误判，附判定理由供诊断 |
| `BackingFieldSynthesizer` 接缝 | **不新写合成器**：复用 C11 BackingFieldNaming 预留表 + [JavaNaming.GetAutoPropertyBackingFieldName](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L99-L127)；本规则只负责「为 IPropertySymbol 预留并锁定一个字段名」与构造 FieldDeclarationSyntax（private/static/readonly/初始化器/迁入的特性）。若 C11 实现未暴露预留 API，向锚点申请 internal 接缝（一行可见性/方法扩展），禁止复制命名算法 |
| `FieldKeywordBodyRewriter.cs` | `CSharpSyntaxRewriter`：决策 4 的标识符替换 + 决策 5 的 auto 访问器展开（`get;`→`get => f;` 等）+ 决策 7 初始化器摘除（属性上 Initializer 移除，值交字段声明）；只改 Resolver 命中的节点，未命中节点原样透传；保持 leading/trailing trivia |
| `FieldKeywordAttributeMigration.cs` | 决策 8：拆 AttributeListSyntax 的 target specifier，`field:` 列表合并迁移到 FieldDeclarationSyntax，其余原位保留；迁移后属性上不得残留 field 目标列表（residual 断言项） |
| `FieldKeywordNormalizer.cs` | **编排入口**：对单个 PropertyPlan 执行 Resolver→（需要时）字段合成→BodyRewriter→特性迁移的声明侧小阶段；产出归一后的属性节点与字段节点、FieldKeywordSynthesizedAnnotation、本规则计数；无法满足前提（符号缺失/容器不支持）时不产出任何改动并返回跳过码 |
| `FieldKeywordDiagnostics.cs` | CS2J5125-5128 码位常量与消息模板（§6，最终码位由 C11 锚点统一登记） |
| `FieldKeywordRuleExtensions.cs` | 向 PropertyRule/PropertyPlan/PropertyPlanBuilder 挂载 `FieldKeywordInfo?` 的扩展与批注查询；不新建 `IMemberDesugarRule` 实现（决策 16；若锚点最终偏好独立子规则形态，退化为 `Kind=Property, Order=1` 的内部规则，仍在本目录，M1 由锚点定） |

编译构造、语义模型、FormatAndValidate 三候选、BOM 保持全部复用族基建，本规则不自持 parse 选项（Roslyn 版本前提由整 pass 的 parse options 承担，§1.3）。

### 4.2 数据流（嵌入 C11 三阶段）

1. **Analyze（整树一次）**：MemberDesugarAnalyzer 枚举属性符号时，FieldKeywordResolver 对每个候选 `PropertyDeclarationSyntax`（含 partial 合并后符号的各部件）做一次判定，结果缓存进 PropertyPlan.FieldKeywordInfo；
2. **Plan**：field 命中不改变 plan 边界（TouchedFiles 仍是属性声明 + 全部使用点文件）；跳过情形（ref/partial/接口探针项/符号缺失）在计划期定案；
3. **Rewrite（逐文件）**：FieldKeywordNormalizer 在 PropertyDeclarationRewriter 之前跑（§3.3）；归一结果过族 FormatAndValidate；字段插入与属性改写在同一语法树事务内，要么都落要么都不落；
4. **回退**：归一后整树重编译新增错误由族迭代器按 PropertyPlan.TouchedFiles 归属（CS2J5101 单 plan 回退 / CS2J5100 全量回退），C50 不另建回退逻辑；
5. **Residual**：AfterFiles 阶段对输出树执行 FK-9 对账（§4.3）。

### 4.3 规则全集（FK-x）

| 规则 | 内容 | 边界/要点 |
|---|---|---|
| FK-1 语义命中 | 访问器体（含属性级表达式体）内 `field` 主表达式，符号为关联属性的 IsImplicitlyDeclared backing field | 不命中：`@field`、访问器外、手写 backing field 属性、事件/索引器（无此语法）、abstract（无体） |
| FK-2 后备字段合成 | private 字段，名走 JavaNaming/预留表，static/readonly 读符号，类型与 nullable 注解同属性 | 字段名与「同一属性若写成 auto 属性」的 C11 字段名**必然相同**，Java 半区零漂移 |
| FK-3 访问器体标识符改写 | 命中 IdentifierName → 合成字段标识符；块体/表达式体/复合/`??=`/`++`/ref 实参全覆盖 | 只改 FK-1 节点；嵌套 lambda/局部函数内的同名标识符按各自绑定重新判定（lambda 参数 shadow 场景由符号判定兜底） |
| FK-4 auto 访问器展开 | 同属性内无体一侧展开为字段读写体；get-only/set-only 不补发另一侧 | init 展开为 init 体（后续 C11 放宽）；访问器可访问性修饰符随体保留 |
| FK-5 属性级表达式体 | `T P => field …` 视为 get-only full 属性：合成字段 + 改写体，形态 `T P { get => f …; }` 或保留表达式体（二者对 C11 等价，选保 trivia 最小改形：仅替换标识符 + 加字段） | 属性级表达式体无混搭问题，不产 set |
| FK-6 初始化器迁移 | 属性 `= e` 移到合成字段初始化器 | 语义=不调 setter；含副作用表达式原样平移；与 C11 auto 属性初始化器路径同形 |
| FK-7 `[field:]` 特性迁移 | field 目标特性整张落字段；`[get:]/[set:]/[init:]` 留访问器；默认/property 目标不拦截 | 反射断言 `[NonSerialized]` 等落位（§7.4）；与 C10 归一标记的幂等消费由 C11 PD-10 负责，FK 只认语法目标符 |
| FK-8 容器闸门 | ref 返回属性 → CS2J5115（沿用 C11）；partial → CS2J5114；接口 field → M0 后 CS2J5127 或基线错误；override/static/virtual/sealed/new 正常命中 | 闸门=PropertyPlan 整体不动且必留诊断；不允许只归一不跳过 |
| FK-9 residual 对账 | 输出树访问器体内不存在未转义、且绑定 backing field 的 `field` token；语法扫描（`Identifier.Text=="field"` 且 token 非 `@`）发现残余时必须能对应一条 skip 诊断，否则 CS2J5122 residual（该码由 C11 v1「field 跳过」重定义/退役，§6） | 语法扫描配合语义标记双保险；幂等性（二次执行零命中）由此闸门证明 |

归一产物示例（FK 阶段输出，随后由 C11 降为 get_X/set_X；字段名示意取 JavaNaming 契约值）：

```csharp
// 输入（C# 14）
[field: NonSerialized]
public virtual int Count
{
    get => field + 1;
    init => field = value < 0 ? throw new ArgumentOutOfRangeException() : value;
} = 1;

// FK 归一后（C# 13 可编译形态，交由 C11 PropertyRule）
[NonSerialized]
private int count = 1;
public virtual int Count
{
    get => count + 1;
    init => count = value < 0 ? throw new ArgumentOutOfRangeException() : value;
}
```

要点：初始化器从属性移到字段（决策 7，不调 init 访问器）、`[field:]` 目标剥除后特性落字段（决策 8）、`virtual` 留属性（字段不带 virtual）、字段名与该属性作为 auto 属性时 C11 将发射的名字逐字符相同（决策 2）。

### 4.4 向 C11 锚点申请的接缝

1. PropertyPlan/PropertyPlanBuilder 增可空挂载点 `FieldKeywordInfo?`（或等价的 plan 级 annotation bag）；
2. PropertyRule 声明重写支持「字段已由前置步骤提供」：按批注跳过 auto 判定与字段发射，字段名以预留表为准；
3. BackingFieldNaming 暴露「为 IPropertySymbol 预留字段名」的 internal 入口；
4. 声明侧重写链允许在 PropertyDeclarationRewriter 前插入同 plan 的 normalizer 小阶段（若锚点实现为固定管线，则在管线中挂一个 `IPropertyDeclarationNormalizer` 钩子）。
以上均为只增不改行为的接缝；锚点落地形态若不同，C50 做同名适配，不改族契约。

---

## 5. CLI 集成（零结构改动）

- 不新增适配器/开关/verb/Option/目录；全部经 C11 建立的 `MemberDesugarPassAdapter` 承载（整树自驱动、verify 双开、无支撑库）。
- 选项：不新增独立子节（field 归一不可关闭——它是 C11 属性正确性的前提而非可选风味）；沿用族 overrides 的 skip/force 语义：skip 某属性 = 整个 PropertyPlan 跳过（含 field），force 不解除 FK-8 语言闸门。
- 统计汇入族 Statistics，建议字段：`FieldKeywordProperties`（命中属性数）、`FieldKeywordAutoAccessorsExpanded`（展开的无体访问器数）、`FieldReferencesRewritten`（标识符节点数）、`FieldTargetAttributesMigrated`（迁特性数）、`FieldInitializersMoved`、`FieldKeywordSkipped`（按码分桶）、`ResidualFieldTokens`。
- 摘要行在 C11 property 段后追加片段：`field-keyword: <N> propert(y/ies), <A> auto accessor(s) expanded, <R> reference(s), <T> field-attribute(s), <I> initializer(s), <S> skipped`。
- 诊断码位向 C11 锚点申请 property 段 CS2J51xx 内空号（现占用至 CS2J5124，建议 **CS2J5125-5128**，文内编号均为建议值，由锚点统一登记后回填，码位只追加不改号）；CS2J5122（C11 v1「field 关键字访问器跳过」）的退役/重定义由锚点一并处理（§6）。
- 指纹/保留目录/`--list-passes`/CatalogItems 展示零新增工作。

---

## 6. 诊断策略

| 码（建议） | 级别 | 含义 |
|---|---|---|
| CS2J5125 | Info | field-backed 属性归一留痕（属性名/字段名/readonly/static/展开访问器数），仅 verbose |
| CS2J5126 | Warning（strict Error） | **语义判定安全网**：访问器体内存在非转义 `field` 主表达式但 GetSymbolInfo 拿不到关联的 IsImplicitlyDeclared backing field（Roslyn 版本不足、编译错误树、预览语义漂移等），PropertyPlan 原子跳过；**严禁退化为文本替换** |
| CS2J5127 | Warning（strict Error） | field 出现在规则不收的容器（M0 定案后的接口形态等），plan 跳过 |
| CS2J5128 | Warning（strict Error） | `[field:]` 特性迁移无法完成（字段事实缺失等异常态；正常可编译基线不应出现），plan 跳过 |
| CS2J5122（重定义申请） | Warning（strict Error） | C11 v1 语义为「field 访问器 v1 不收编，plan 跳过」；C50 落地后该跳过理由消失，申请**重定义为 residual 闸门码**（FK-9：输出树残留未归一 field token 且无对应 skip 诊断）或退役，由锚点登记拍板 |
| CS2J5114/5115 | 沿用 | partial 属性 / ref 返回属性的既有跳过闸门，field 命中同类容器时直接复用，不新造码 |

原则沿用 C11 §6：不可判定即显式诊断 + plan 原子不动；Warning 在 `Strict=true`（默认）下升 Error；跳过按 SkippedByReason 分桶进统计，供 .NET 10 目标扫描盘点。Roslyn 版本不足不属本 pass 诊断职责（基线编译即失败，C52 版本防线统一报告），但 CS2J5126 必须在该情形下被观测到（验证安全网生效），不得静默产出含 `field` 的中间树。

---

## 7. 测试计划

### 7.1 Core 单测矩阵（`tests/CSharpToJava.ProjectTests/MemberDesugarFieldKeywordUnitTests.cs`，仿 C11 单测形态）

1. **双访问器 full**：`int P { get => field + 1; set => field = value; }` → 显式字段 + 同名 full 体；字段名等于 JavaNaming 契约值；二次 Rewrite 零 diff。
2. **仅 get 半自动**：`int P { get => field + 1; }` 与属性级表达式体 `int P => field`；断言字段 `readonly`（读符号事实）、无 set 方法；`string Lazy => field ??= Compute()` 断言字段**非** readonly、`??=` 保留。
3. **仅 set**：`{ set { if (field != value) field = value; } }` 字段合成、无 get。
4. **混搭**：`{ get; set => field = value; }`、`{ get => field; private set; }`、`{ get; init => field = value; }`：无体侧展开为字段读写体、可访问性/init 保留。
5. **初始化器**：`{ get; init; } = 1` 与 `{ get; set => field=value; } = Exp()`：初始化器落字段且**不在 setter 调用路径**（fixture 用 virtual setter 副作用断言初始化期不触发 setter，复刻官方示例）。
6. **`[field:]` 特性**：`[field: NonSerialized] int P { get; set => field=value; }` 字段上反射断言 `IsNotSerialized`；多特性/混合 `[get:]` 同列；默认目标特性留属性交 C11 PD-10（断言 C50 阶段不动）。
7. **复合/ref 实参**：`field += 1`、`field ??= x`、`field++`、`Set(ref field, value)` 节点级断言。
8. **static / virtual / override / sealed / new**：override 用例反射断言派生类型拥有独立合成字段且基类字段槽位独立（FK-P2 定案后固化）；static 字段静态性。
9. **readonly struct / readonly 访问器**：readonly struct 内 field 属性（与 E13 产物共处一树）；readonly get 内只读 field，字段 readonly；struct 默认赋值/构造器直写经 C11 PD-7 路径。
10. **消歧安全网**：① 用户手写 `_field` + 访问器内引用 `_field`（不动）；② 访问器内 `@field` 绑用户字段（不动）；③ 普通方法内标识符 `field`（不动）；④ 同类存在用户字段/属性名 shadow 场景（C#14 主表达式仍绑 backing field，正确改写）；⑤ 人为制造符号缺失（损坏 compilation）→ CS2J5126 且 plan 全不动。
11. **闸门族**：ref 返回属性 → CS2J5115；partial 属性 → CS2J5114；接口 DIM field（FK-P3 结论填入：基线错误或 CS2J5127）；事件/索引器体零误伤。
12. **工程性质**：幂等、干净文件字节级一致 + BOM、partial 类型多部件（非 partial-property）上字段插入位置确定性。

### 7.2 项目级 fixture

在 C11 的 `tests/SampleMemberDesugarSolution/` 中加 `FieldKeyword*` 前缀文件（不建第二套 sln，同 C27 约定）：view-model（INotifyPropertyChanged 式 set）、lazy `??=`、校验型 set、get-only/set-only、static、override、readonly struct、`[field: NonSerialized]` DTO、初始化器 + virtual setter 副作用观测类；App/Tests 侧在去糖后的 C# 上做行为 xunit（读写、校验、lazy 单次求值、初始化期不触发 virtual setter）。

### 7.3 Release build/test（本设计阶段不执行，仅登记验收命令）

- `dotnet build -c Release`（CSharpToJavaConverter.slnx）；
- `dotnet test -c Release`：CSharpToJava.Tests / ProjectTests / ConversionTests 全绿。

### 7.4 反射/序列化验证

- 合成字段绑定事实：`typeof(C).GetField(<契约字段名>, BindingFlags.NonPublic|Instance|Static)` 非空，且属性的 getter/setter 方法体行为经该字段；
- 特性落位：`[field: NonSerialized]` → `FieldInfo.IsNotSerialized == true`；`[field: ThreadStatic]` 等按可用 BCL 特性抽查；
- 字段 IsStatic/IsInitOnly 与 FK-2 决策一致（IL 层 `initonly`）。

### 7.5 residual 闸门

输出树扫描：每个访问器体内非转义 `field` token 必须有对应 skip 诊断（CS2J5126/5127/5114/5115 之一）或不存在；strict 下残余 → CS2J5122（重定义后）Error。启用 member-desugar 的既有 fixture（SampleProject/SampleRecordSolution/SampleJavaKeywordSolution 等）中间树 build/test 不退化，上游各 pass 统计零变化。

### 7.6 CLI / 红灯对照

- C50 不带独立 CLI 面；经 `--enable-pass member-desugar` 验证摘要片段、CatalogItems（含 C50 时 `--list-passes` 可见）、verbose Info；
- 与 [PropertyTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs) 直连单文件路径的红灯对照：不修改 PropertyTransformer 及其测试一行；field 形态在直连路径仍属未支持（转换器安全网），C50 只保证预处理路径正确——与 C11 §7.6 同纪律。

---

## 8. 默认开启验收门槛

C50 无独立开关，其「默认开」= 随 C11 一起 `defaultEnabled: true`，在 C11 §8 五项门槛之上追加：

1. **Roslyn 版本前提达成**：仓库 Microsoft.CodeAnalysis 升级到支持 C# 14 GA 的 ≥4.14.x（版本号 M0 钉死），全仓 parse options 下 field 源文件无基线诊断；或评审明确接受 Preview 退路（不推荐）；
2. FK-P0~P3 探针全部定案，限制集（接口/override/partial/符号形状）写入本文档并体现在单测；
3. §7 全部单测/fixture/反射断言/residual 闸门全绿；field 归一相关零回退或全部回退有诊断归属；
4. **.NET 10 目标源码扫描计数**（catalog P2→等价 P1 的条件）：输出 field-backed 属性总量、命中/跳过分布；Collections/Text 等高频库若实际使用密集，C50 作为 C11 同批默认开的必带项；低频则可在 C11 默认开后随下一评审周期开启（不单独翻转 pass，只影响规则是否随版本前提启用的评审结论——实现上规则恒在，门由版本与门槛控制）；
5. C11 §5.3 Java 半区消费清单对 field 产物（显式字段 + get_X/set_X）无新增项：字段名既走 JavaNaming 契约即与 auto 属性完全同形，评审确认无额外消费缺口。

---

## 9. 里程碑

| 里程碑 | 内容 | 规模 |
|---|---|---|
| M0 Roslyn 探针与限制集钉死 | 完成下表 FK-P0~P6 全部探针，结论回填本设计 §1.3/§2/§6/§8，零产品代码（临时探针工程不进仓库） | 探针工程 + 文档 |
| M1 基本归一 | FK-1/2/3/4/5/9 + FK-6：Resolver、字段合成、体改写、混搭展开、初始化器、幂等/residual；§7.1-1/2/3/4/5/7/12 单测；CS2J5125/5126 接线 | ~350 行 |
| M2 特性与边角 | FK-7 特性迁移（反射断言）、FK-8 闸门（ref/partial/接口结论）、static/override/sealed/new、readonly struct 与 E13 共处、消歧安全网全集；fixture FieldKeyword* 文件 + 项目级测试；Release build/test 全绿 | ~250 行 + fixture |
| M3 随 C11 同批收口 | .NET 10 目标扫描计数与 SkippedByReason 报表、C11 §5.3 消费确认、CS2J5122 退役/重定义随锚点登记、CatalogItems 含 C50、门槛 §8 评审；随 C11 默认开（不独立翻转） | 数据/文档 |

M0 探针清单（每条以「可编译性 + SemanticModel 符号形状 + 反射/IL」三件证据钉死，禁止只凭文档推断）：

| 探针 | 问题 | 预期产出 |
|---|---|---|
| FK-P0 | 仓库当前 4.12.0 + `LanguageVersion.Latest` 解析含 field 源文件时的实际表现：语法节点是否仍为 IdentifierName、确切诊断 id（CS0103/CS8773 类版本门/preview warning）、GetSymbolInfo 返回；4.12+Preview 是否可用且语义与 GA 一致；NuGet 上 ≥4.14 的确切版本与升级后 Workspaces.MSBuild/MSBuild.Locator 兼容性 | 版本先决决策（升级 or Preview 退路）+ §6 安全网用例 |
| FK-P1 | 4.14 GA 下各形态 backing field 的 `IFieldSymbol` 事实：`IsImplicitlyDeclared`/`AssociatedSymbol`/`IsStatic`/`IsReadOnly`/NullableAnnotation，覆盖 `{ get => field; }`（readonly？）、`=> field ??= X()`（可写？）、`{ get; set => … }`、`{ get => …; init; }`、set-only、static | FK-2 实现依据单测基线 |
| FK-P2 | `public override int P { get => field; set => field = value; }` 是否可编译；反射确认派生类型自有独立字段、基类字段槽位独立；new/sealed override 同测 | 决策 10 定稿 |
| FK-P3 | 接口内：实例 DIM 属性 field（预期编译错误及诊断 id）、接口 `static` 属性 field（接口允许静态字段，是否合法？）、静态 abstract/virtual（无体不涉） | 决策 11 与 CS2J5127 边界 |
| FK-P4 | 访问器体内 `string field;` 局部声明的错误 id；`@field` 绑定用户字段的符号形状；同类存在名为 field 的实例字段/静态字段时主表达式 `field` 的绑定目标与「旧版本符号差异」warning id | 决策 1 反例集 + 基线错误集行为 |
| FK-P5 | partial 属性 implementing 声明中 field-backed 访问器的语法/符号形态（合并前后）；`nameof(field)` 编译期值；访问器内嵌 `Expression<Func<T>>` 引用 field 的表达式树合法性与树节点形状 | 决策 13/18/19 定稿 |
| FK-P6 | `[field: X]` 在 field-backed / 纯计算 / auto 三类属性上的诊断与落位；readonly 实例访问器（C#8）内写 field 的诊断 id；struct 无参 ctor 确定赋值与合成字段的交互（CS9020 静默警告是否影响整树 warning 对账） | FK-7/决策 14 边界 |

---

## 10. 风险

1. **★语义误判同名标识符**：文本匹配会把 `@field`、手写 backing field 场景、访问器外同名标识符改错，产出「编译能过、绑错符号」的程序。缓解：决策 1 纯语义判定 + CS2J5126 安全网（拿不到隐式字段宁跳过不硬改）+ §7.1-10 反例集；Roslyn 版本不足时正是靠该安全网失败得响亮。
2. **`[field:]` 特性丢失**：序列化（NonSerialized/Json*）/运行时语义特性随字段消失，行为静默改变。缓解：FK-7 语法目标符强制迁移 + 反射断言落位；与 C10 attribute-normalize 的分工在单测锁定（两套输入幂等）。
3. **override / 接口 / partial 语义误读**：语言边界若凭记忆实现会在边角产出非法 C#（ref 返回、DIM、静态接口成员）。缓解：M0 探针全部以可编译性 + 反射钉死，未证实形态一律 plan 跳过（CS2J5127/既有闸门），不假设有语义。
4. **预览语法/编译器变动**：C#13 preview → C#14 GA 间绑定形状若有差异，4.12+Preview 退路可能与 GA 不一致。缓解：只接受 ≥4.14 GA 作为默认路径；版本升级与 C49/C51/C52 同批，一次验证全套 C#14 特性。
5. **初始化器语义漂移**：误把 `= e` 经 setter 物化会改变 virtual/可观察行为。缓解：FK-6 直移字段，fixture 用官方 virtual setter 观测用例守门。
6. **与 C11 接缝演进耦合**：C11 尚未合入，PropertyPlan/预留表/声明管线为设计形态。缓解：接缝只增不改、C50 实现前先合 C11（§12），以锚点实际 API 做同名适配；字段命名硬绑 JavaNaming 既有算法，锚点不改该算法即零漂移。
7. **规则恒在但版本未达时的噪音**：旧 Roslyn 下大量 CS2J5126。缓解：版本前提列为 pass 执行前置检查项（与 C52 协调），前提不满足时 pass 整体早失败而非逐属性刷诊断。

---

## 11. 并行协调

- **C11（锚点，硬依赖）**：分支先合 C11 再实施；申请 §4.4 四个接缝与 §5 码位/CatalogItems 登记；CS2J5122 的退役或重定义由锚点统一拍板；C50 不改 `MemberDesugar/` 根目录共享文件与适配器。
- **C27（init/required/with）**：同批组合验证 init+field+初始化器；文件目录互不重叠（Records/ vs Properties/）；无 plan 归属争议（field 节点不可能出现在 with 站点）。
- **C52（版本防线）**：Roslyn 4.12→4.14 升级建议归 C52 统一执行（C49/C50/C51 共享先决）；`[field:]` 目标限定符归 C50，`field` 作为上下文关键字的词法防线登记与 C52 互见；M0 结论同步 C52 限制集表。
- **E13 make-readonly / C07 struct-value-semantics**：RunAfter 锚点已保证先于 member-desugar；readonly 访问器/struct 字段确定性由字段符号事实保住，M2 以 E13 输出树做共处回归。
- **JK/E17（java-keywords 终命名）**：`field` 本身不是 Java 关键字，但合成字段名必须走 JavaNaming 逃逸 + JK 最后闸门（与 C11 后备字段同等待遇）；C50 不自行做终命名。
- **C10 attribute-normalize**：链位先于 member-desugar；FK-7 对原始 `[field:]` 语法先成立，C10 落地后消费归一标记的两套输入单测由 M2 补齐。
- **C20 partial-merge**：partial property field 场景在 C20 前恒走 CS2J5114；C20 合入后解锁 FK-8 对应单测，无代码耦合（声明合并后自然命中）。
- **热文件**：PropertyTransformer.cs 只读记录（本任务零改动）；PassCatalog/PassStubCatalog/PassChainFactory 等 CLI 热文件零改动（CatalogItems 一行字符串由 C11 锚点翻转者按 §3.2 登记）。

---

## 12. Worktree 分配（2026-10-03 更新 base，与 C27 共用同一 worktree 进入实施）

- 分支：`feature/preproc-c27-c50-record-with-field-keyword`（**C27 record-with 与本特性 C50 共用同一分支/worktree**：两 preprocessor 在同一 worktree 内同批实施、按各自里程碑分别提交；建议先落 C50（建立 PropertyRule 前置 normalizer 钩子），再落 C27）
- 目录：`.worktrees/preproc-c27-c50-record-with-field-keyword`
- base：**`840010e2`**（`docs: C46 设计文档 worktree base 前移至 dev-3.x HEAD 4ea50d44`，dev-3.x HEAD @ 2026-10-03，完整 SHA `840010e2c1d6311424c89e8c597f5147abec2fbc`，已 `git rev-parse --verify` 校验存在；worktree 切出后基线 `dotnet build CSharpToJavaConverter.slnx` 退出码 0）。~~原 base `9b99c60b`~~（`docs: C46 成员签名显式化设计`，2026-10-03 前移；原校订注记「实现前先把 C11 锚点分支合入本分支」已随 C11–C14 合入收口，无需再合）。
- 创建命令（在仓库根、PowerShell 下执行；2026-10-03 实施时已实际执行）：

```powershell
git worktree add .worktrees/preproc-c27-c50-record-with-field-keyword -b feature/preproc-c27-c50-record-with-field-keyword dev-3.x
```

- **前置现状（2026-10-03 基线核实，硬前提变化）**：
  1. **Roslyn 版本先决已满足**：`Microsoft.CodeAnalysis.CSharp`/`.CSharp.Workspaces`/`Workspaces.MSBuild` 已由 4.12.0 升级到 **5.0.0**（`src/CSharpToJava.Core/CSharpToJava.Core.csproj:12-14`），Core TFM `net10.0`、SDK 10.0.300、全仓 parse options 仍统一 `LanguageVersion.Latest`（`src/CSharpToJava.Core/Pipeline/ProjectCompilationBuilder.cs:30`）——C#14 `field` 上下文关键字 GA 绑定可用。§1.3「4.12 拿不到 backing field、须等 C52 升到 ≥4.14」的阻塞消解，M0 **FK-P0 版本探针由本基线复核替代**（不再有升级动作）；FK-P1~P6 的符号形状/反射/语言边界探针仍保留为实现期单测基线。
  2. **族 pass 已默认开**：member-desugar 为 Implemented 且 **defaultEnabled=true**（`src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs:355-365`，Stage=Declarative，RunAfter=`{struct-value-semantics, closure-lift}`，目录 `.cs2j-member-desugar-src`），catalogItems 真值 **`"C11, C12, C13, C14, C39"`**（C39 已合入；§3.2 的 `"C11, C12, C13, C14"` 勘误再次过期）。本特性合入时在该真值上追加 `C50`；与同分支 C27 一并落地后的终值为 `"C11, C12, C13, C14, C39, C50, C27"`（最终顺序以合入时 PassCatalog 真值为准，**勿回退 C12–C14/C39**）。
  3. **默认开口径变化（不抢跑）**：§5/§8/附裁决 2 撰写于「族 pass 默认关、field 规则恒在无独立开关、只受版本门+门槛门控制」的前提；现族 pass 已默认开，field 归一在 §8 门槛（§7 全部单测/fixture/反射断言/FK-9 residual/.NET 10 扫描）全绿前不得以默认行为改写输出。实施时仿 C39 `rules.finalizers`（`Program.cs:3321`）与 C46 `rules.signatures.enabled` 的子集闸门口子，为 field 归一加一个默认 **false** 的子集开关（经 Program.cs 配置透传到 `MemberDesugarOptions`，**不新增 pass/适配器/verb/描述符**）；关闭时 field 规则零执行零 diff，显式启用才参与分析/改写，门槛通过后随 C11 翻默认值。
  4. `MemberDesugar/Properties/` 六文件在盘（PropertyDeclarationRewriter/PropertyFacts/PropertyPlan/PropertyRule/PropertyUseSiteRewriter/PropertyDiagnostics），全树无任何 FieldKeyword* 文件；C50 全部新增代码落 `MemberDesugar/Properties/`，§4.4 四个接缝（PropertyPlan 挂载点、PropertyRule「字段已提供」钩子、BackingFieldNaming 预留入口、声明管线 normalizer 相位）向现有形状适配，只增不改既有行为。
- worktree 内交付面：M0 FK-P1~P6 探针结论回填本文档；Core 仅在 `MemberDesugar/Properties/` 增加 FieldKeyword* 文件（含 PropertyRule 前置 normalizer 钩子与默认关子集开关）；Core 单测（`tests/CSharpToJava.ProjectTests/MemberDesugarFieldKeywordUnitTests.cs`）+ fixture `FieldKeyword*` 前缀文件（并入既有 `tests/SampleMemberDesugarSolution`，不建第二套 sln）+ Release build/test + FK-9 residual 闸门；零 pass/适配器/注册表结构改动（仅 catalogItems 一行 + 子集开关透传）；不触转换器代码；不新建支撑工程。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持本文 §12 登记的自有 base `9b99c60b`（不改动），创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。【2026-10-03 实施更新：base 直接前移至 dev-3.x HEAD `840010e2`，并与 C27 共用同一 worktree/分支（名见 §12），命令见 §12 且已执行；Roslyn 已 5.0.0（版本先决满足，FK-P0 取消）；member-desugar 已 Implemented 且 **defaultEnabled=true**，catalogItems 真值 `"C11, C12, C13, C14, C39"`，本特性合入追加 `C50`；族 pass 既已默认开，field 归一在 §8 门槛全绿前以默认 **false** 的子集开关承接「零执行零 diff」，不抢跑，详见 §12 前置现状】

**裁决（按原题号）：**

1. **Roslyn 升级路径**：4.12→≥4.14 **统一归 C52 升级**（C50/C49/C51 同批节奏由 C52 锚定，否决各 pass 分支自升——多分支自升会造成 TFM/包版本事实源分裂）；**不接受 4.12+Preview 作为过渡**（Preview API 形状不稳，规则不得建在漂移面上）；M0 FK-P0 探针数据作为 C52 升级评审输入，C50 规则代码在升级落地后开工。
2. **C50 与 C11 默认开批次**：**规则恒在、无独立开关**——即便 field 命中低频，也允许在 C11 默认开时随版本门保持"规则在、无命中"状态；门只有**版本门 + 门槛门**两个，不为低频规则单造开关。catalog **P2 扫描阈值等同 P1**（同一门槛，不设更松的第二档），阈值数字随 C11 门槛表登记。
3. **接缝形态**：采用 **PropertyRule 内前置 normalizer 钩子**落地（否决 `Kind=Property, Order=1` 内部子规则——不新增规则发现面）；钩子由 C11 锚点实现时按其管线形状定具体接口，C50 只提供 normalizer 逻辑，规则自动发现数量不增加。
4. **接口静态 field-backed 属性**：若 M0 FK-P3 探针**证实 C#14 允许接口静态 field-backed 属性**（接口可含静态字段），则 **M2 直接收编，不长期保留 CS2J5127 跳过**（与"全语义支持"一致：合法构造有明确 Java 终态——companion/holder 静态字段承载）；探针证伪（编译器不允许）则维持 CS2J5127 显式诊断并注明编译器版本依据。
5. **CS2J5122 处置与码位**：**重定义为 residual 闸门码**（不退役——保留对未归一 field 站点的末端拦截计数）；CS2J5125-5128 建议号**以 C11 锚点统一登记为准**，登记结果回填本文，若号段位移 M1 纯常量改名。

---

### 原问题记录（裁决前）

1. **Roslyn 升级路径**：4.12→≥4.14 由 C52 统一升级还是 C50 分支自升（影响 C49/C51 同批节奏）？4.12+Preview 是否接受为过渡（倾向不接受）？M0 FK-P0 数据为决策输入。
2. **C50 与 C11 的默认开批次**：若 .NET 10 目标扫描 field 命中低频，C50 规则是否允许在 C11 默认开时随版本门保持「规则恒在但无命中」状态（实现倾向：是，规则无独立开关，门只有版本与门槛）；catalog P2 等价 P1 的扫描阈值需评审定。
3. **接缝形态**：FK 归一以 PropertyRule 内前置 normalizer 钩子落地，还是以 `Kind=Property, Order=1` 的内部子规则落地？倾向前者（不新增规则发现面），由 C11 锚点实现时按其管线形状定。
4. **接口静态属性 field**：若 M0 FK-P3 证实 C#14 允许接口静态 field-backed 属性（接口可含静态字段），v1 是否收编？倾向先 CS2J5127 跳过、按扫描命中再议。
5. **CS2J5122 处置与码位**：退役还是重定义为 residual 闸门码、CS2J5125-5128 建议号是否被锚点采纳，以 C11 统一登记为准并回填本设计。

---

## 13. M3 画像实施记录（2026-10-09）

> 工具与口径与 C27 §13 完全一致（`tools/PreprocessCorpusProbe` 画像 id `c27-c50-ip`，提交 `013ab5e8`；in-proc public API、msbuild `-getItem:Compile` 干净编译项、non-strict、`Records.Enabled=true`+`FieldKeyword.Enabled=true`；子集开关生产默认核实仍为 false）。同一次扫描同时服务两族计数，因此语料规模表、CLI 5100 残余与 driver-error 定性直接**沿用 C27 §13.1/§13.5/§13.6 作为证据**，本节只落 C50 需要的数据与裁决。原始报表（gitignored）：`probe-out/profiles/c27c50-{sdk,ef,yaml,runtime}/c27-c50-ip.{json,md}`。

### 13.1 field 关键字命中矩阵（全部实测）

| 桶 | csharp-sdk | EF Core（6 工程子集，2 不可用） | YamlDotNet |
|---|---:|---:|---:|
| csproj（不可用）/编译项 | 21（0）/1457 | 6（2）/2141 | 8（5）/17 |
| FieldKeywordProperties（CS2J5125 Info） | 0 | **60** | 0 |
| FieldKeywordAutoAccessorsExpanded | 0 | 5 | 0 |
| FieldReferencesRewritten | 0 | 86 | 0 |
| FieldTargetAttributesMigrated | 0 | 98 | 0 |
| FieldInitializersMoved | 0 | 0 | 0 |
| FieldKeywordResidualTokens（FK-9） | 0 | 0 | 0 |
| CS2J5126 安全网 / 5127 不收编容器 / 5128 特性失败 | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |
| CS2J5122（声明跳过/residual 双角色，见 13.4） | 0 | 0 | 0 |
| SkippedByReason field 专属键 | 无 | 无 | 无 |

EF 分项目实测分布（属性/自动访问器展开/field 引用重写/`[field:]` 特性迁移）：

| 工程（可用 4 个中的 3 个有命中） | props | auto | refs | attrs |
|---|---:|---:|---:|---:|
| src/EFCore/EFCore.csproj（1119 编译项） | 54 | 5 | 80 | 90 |
| src/EFCore.Relational/EFCore.Relational.csproj（793 项） | 3 | 0 | 3 | 4 |
| src/EFCore.SqlServer/EFCore.SqlServer.csproj（206 项） | 3 | 0 | 3 | 4 |
| EFCore.Abstractions（23 项） | 0 | 0 | 0 | 0 |

60 个 field-backed 属性产出 98 个迁移到合成字段的目标符特性、86 处访问器体内 `field` 引用重写、5 个自动访问器对（`{ get; set; }`→`{ get => field; set => field = value; }` 形态），**零 residual token、零安全网跳过、零容器拒收、零特性迁移失败、零初始化器迁移**（FieldInitializersMoved=0，命中集内无带初始化器的半实现属性）。

### 13.2 零命中语料定性（§8 门槛 4 输入）

- **csharp-sdk 四库（1457 项）与 YamlDotNet 有效 17 项全部 0 命中**：这两个语料本身不含 C# 14 `field` 上下文关键字的半实现属性（代码风格/语言版本事实），**不是规则漏检**——同一二进制、同一选项的 EF 轮拿到 60 命中，证明 FK-1~FK-9 管线在盘有效。
- **runtime Collections/Text 数据未取得**：`D:\runtime-10.0.12` 全仓 `artifacts\obj` project.assets.json 数为 0，4 个采样产品工程（System.Text.Json、System.Collections.Immutable、Microsoft.Extensions.DependencyInjection、Microsoft.Extensions.Caching.Memory）全部 MSB4057；restore 会写入只读语料而未执行。附裁决 2「Collections/Text 高频库使用密集则同批默认开」的**直接证据面本轮无法测量**（C27 §13.6 已完整声明）。
- YamlDotNet 主库等 5 工程未 restore/无目标，零结论不外推到整库。

### 13.3 C11 §5.3 Java 半区消费确认（§8 门槛 5）

field 归一只发生在声明侧：产物为**普通显式私有字段**（命名走与 auto 属性后备字段相同的 BackingFieldNaming/JavaNaming 契约）+ **未变的 get_X/set_X 访问器** + FK-7 迁移后的**普通字段目标特性**。对照 [C11 §5.3](file:///d:/code/cs2j/.worktrees/p3-profiles/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L237-L245) 五项：

1. `get_/set_` 剥前缀旧特例与派生 `getX/setX`：field 产物与 auto 属性去糖产物**逐字同形**，无新形态；
2. PropertyTransformer 对去糖树不可达：同 C11，field 归一不改变该结论；
3. 序列化/数据契约注解路由：Java 侧注解桥面对的就是「后备字段 + 字段目标特性」，FK-7 已把 98 个 `[field:]` 目标符特性在 C# 侧落位（零 CS2J5128），**无新增 Java 消费面**；
4. 显式接口/new/DIM 三类形态：field 在这些容器内不收编（CS2J5127，本轮零命中），不产生需 Java 侧处理的新形态；
5. 布尔 `isX`：field 不引入新的布尔命名契约。

结论：**C11 §5.3 消费清单对 field 产物零新增项**，门槛 5 的评审确认面成立（消费义务不增；C11 转换器消费 PR 仍是共同前置）。

### 13.4 CS2J5122 锚点登记与 CatalogItems

- 附裁决 5「重定义为 residual 闸门码、不退役」已按此落地并核实：C50 子集开启时 5122 为 FK-9 residual 诊断（[PropertyUseSiteRewriter.cs:215](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyUseSiteRewriter.cs#L215)，配 `FieldKeywordResidualTokens` 统计，strict 升 Error）；子集关闭时仍承担 C11 v1 声明级跳过（[PropertyRule.cs:377](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L377)，零执行零 diff）。三语料开启轮 **5122=0、residual=0**。
- CatalogItems 已含 C50：[PassCatalog.cs:549](file:///d:/code/cs2j/.worktrees/p3-profiles/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L549) 现值 `"C11, C12, C13, C14, C39, C46, C50, C27"`（早前提交追加，本轮未改 CLI 热文件）。
- 实现占用码位与本设计一致：5122（双角色）+ 5125 Normalized / 5126 SafetyNet / 5127 UnsupportedContainer / 5128 AttributeFailed；本轮仅 5125 有计数（60）。

### 13.5 §8 门槛对照与拍板（M3）

| 门槛 | 状态 |
|---|---|
| 1 Roslyn ≥4.14（C#14 GA 绑定） | 已满足：5.0.0（§12 前置现状），EF 60 真实命中即 GA 绑定可用的实测证据 |
| 2 FK-P0~P3 限制集定案、单测/fixture | M0-M2 已落（既有提交），本轮语料 5126/5127/5128 全 0 与限制集结论一致 |
| 3 零回退/回滚有归属、residual 闸门 | 三语料 residual=0；field 面无回退；族级 CLI 回滚两条 5100 与本规则无关（C27 §13.5） |
| 4 .NET 10 目标扫描计数 | 可测面：sdk 0 / EF 60（54 集中 EFCore 主工程）/ Yaml 0；**runtime 高频库未取得**（13.2） |
| 5 C11 §5.3 消费确认 | 零新增项（13.3） |

**拍板（M3）**：技术就绪面成立（真实 C#14 产品代码 60 属性 / 98 特性迁移零失败、安全网零误伤、residual 为 0），但附裁决 2 的默认开频率前提——「Collections/Text 高频库实测密集」——其直接证据面（runtime）本轮无法取得，可测语料呈「sdk 全零、EF 仅单一产品集中」形态。**`rules.fieldKeyword` 维持默认 false，M3 不翻默认**；翻默认动作进入族级评审，与 C11 §5.3 转换器消费 PR、runtime restore 后复跑（同一 `c27-c50-ip` 命令）绑定，本设计不承诺时间表。

### 13.6 族级残余指针（不重复定性）

CLI 两条 CS2J5100（json TrimmingTests 镜像目录枚举合并；YamlDotNet OrderedDictionary 元数据接口索引器 + 重载签名不辨）与 1 个 C11 使用点 driver-error（EF InversePropertyAttributeConvention.cs，PropertyUseSiteRewriter.cs:409 InvalidCastException）定性、计数与复现证据见 C27 §13.5 及汇总报告；三者均与 field 归一无关——EF 轮 60 个 field 属性自身全部成功归一，driver-error 文件不属于 field 命中集。
