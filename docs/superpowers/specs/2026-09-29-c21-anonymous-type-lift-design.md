# C21 匿名类型提升预处理器设计（具名不可变类型 + 跨方法/跨文件形状去重）

**日期**: 2026-09-29
**范围**: 新增注册表 pass `anonymous-type-lift`（Core `AnonymousTypeLift/` + CLI `AnonymousTypeLiftPassAdapter`），只做 C#→C# 预处理：把全部 `new { … }` 匿名对象创建点改写为对生成的具名不可变类型的构造，形状按 E06 TupleToStruct 同款 shape registry 基建在跨方法/跨文件/跨编译单元间去重；本特性不新增 Java compat 代码
**状态**: 设计稿，待评审
**注册表锚点**: stub `anonymous-type-lift` 已登记于 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L249-L258)（catalogItems `C21`，Stage=`Declarative`，RunAfter=`tuple`，默认关，目录 `.cs2j-anonymous-type-lift-src`）；下游 linq-channel stub 的 RunAfter 已含本 pass（[L219-L228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L219-L228)）
**catalog 对应**: `prompts/preprocessor-catalog.md` C21（[L508-L518](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L508-L518)）、§3.3 类型声明前置族 E06+C05+C21（[L898-L915](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L898-L915)）、C26（[L566-L577](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L566-L577)）、§4 风险（[L918-L939](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L918-L939)，尤其风险 7/8/10/11）、优先级 P3（[L517](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L517)）
**同构样板**: E06 `tuple-to-struct`（已实现、默认开）——[TupleShapeRegistry.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleShapeRegistry.cs)、[TupleStructFileContent.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleStructFileContent.cs)、[TupleToStructPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs)、[TuplePassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs)
**格式范本**: [2026-09-28-c05-enum-declaration-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c05-enum-declaration-design.md)（同为「生成新声明」的整树 pass）

---

## 1. 背景与目标

### 1.1 现状：匿名类型在主转换器中的两条路径与失败面（2026-09-29 复核）

C# 匿名类型（`new { X = 1, Y = 2 }`、投影缩写 `new { e.X, e.Y }`）是编译器生成的 `internal sealed` 不可命名引用类型，主转换器无法按普通类型声明转换，现有两条处理路径：

1. **Map.of 降级路径（实际唯一生效的创建点路径）**：[ObjectCreationTransformer.NewAnonymous](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L74-L84) 把每个匿名对象创建直接降为 `java.util.Map.of("X", …, "Y", …)`。类型信息在此丢失——后续成员访问 `pair.sourceV`、`foreach` 变量、LINQ 投影链全部失去静态类型；[TypeMappingService](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L443-L464) 对匿名类型符号只能尝试匹配「已合成 record」，匹配不到即映射为 `Object`。
2. **合成 Java record 路径（基建存在但当前为死代码）**：[AnonymousTypeRecordSynthesizer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/AnonymousTypeRecordSynthesizer.cs) 提供按结构键去重的 Java record 合成（[SynthesizedRecordStore](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/SynthesizedRecordStore.cs)、[ConversionContext](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L343-L349) 注册/查询、[CSharpToJavaVisitor](file:///d:/code/cs2j/src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs#L86-L117) 与 [TypeGroupResolver](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/TypeGroupResolver.cs#L264-L281) 把合成 record 发为嵌套类型），但全仓 grep 证实 **`SynthesizeForAnonymousType` 无任何生产调用点**（仅类内递归自调）；该路径只在 Java 28 + `UseRecords` 条件下由 LINQ 侧门控（[LinqRewriter.CanUseRecordsForAnonymousTypes](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L49-L56)），且 LINQ 过程式化遇到匿名类型时一律把显示名消毒为 `object`（[SanitizeAnonymousTypeDisplay](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L637-L665)），或直接以 `AnonymousTypeRequiresRecords` 跳过整条链（[LinqRewriter.cs#L410-L436](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L410-L436)、[LinqRewriteObservability.cs#L15](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriteObservability.cs#L15)）；提取方法返回类型置空则产出未定义 `T`（[LinqRewriter.Rules.cs#L2162](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L2162)）。

高频失败面被既有测试直接钉住：

- [ForeachAnonymousTypeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachAnonymousTypeTests.cs)：foreach 变量类型在集合表达式转换前解析，record 尚未合成 → `Object pair`；
- [LinqAnonymousTypeExtractedMethodTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LinqAnonymousTypeExtractedMethodTests.cs)：foreach 体内含 continue/break 导致 LINQ 提取方法时，helper 返回类型/迭代变量退化为 `Object`/未定义 `T`（源自 MsmtRectilinearPath.java 真实编译错误）；
- [LinqChainRefactoringTests.cs#L97](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LinqChainRefactoringTests.cs#L97) 及同文件 record 字段 Java 关键字转义断言：不同形状须得不同名、`Int/Class` 属性须转义——这些是**转换器侧临时启发式**（[SingularizePascalCase](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/AnonymousTypeRecordSynthesizer.cs#L229-L267)、[EnsureUniqueRecordName](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/AnonymousTypeRecordSynthesizer.cs#L198-L222)），命名不确定、作用域仅限单文件、与 JUnit 注解名 `Test` 碰撞需要特判。

### 1.2 目标：在 C# 侧把匿名类型变成具名不可变类型

新增整树预处理 pass，产物仍是可编译 C#，形如：

```csharp
// before
var p = new { X = 1, Y = 2 };
var q = sources.Select(s => new { Name = s.Name, Nested = new { K = s.K } });

// after（示意；生成类型集中在 _Cs2jAnonymousTypes.*.cs）
var p = new global::Cs2jAnonymous.Anon_X_Int32_Y_Int32(1, 2);
var q = sources.Select(s => new global::Cs2jAnonymous.Anon_Name_String_Nested_Anon_K_Int32(
    s.Name, new global::Cs2jAnonymous.Anon_K_Int32(s.K)));
```

目标具体为：

1. **形状去重三件套**：跨方法、跨文件、跨编译单元（多工程）按「属性名序列 + 属性类型结构」规范化去重，复刻 E06 的 shape registry / 确定性命名 / 生成文件分发管线；foreach 变量、LINQ 链、提取方法签名在重编译后自然绑定具名类型，`Object`/`Map.of`/未定义 `T` 三族失败面从根上消失。
2. **语义保持**：生成类型保持匿名类型的全部可观察语义——引用类型身份、只读属性、`Equals`/`GetHashCode` 按字段值相等、`ToString` 的 `{ X = 1, Y = 2 }` 格式、`==`/`!=` 的**引用相等**（匿名类型不重载 `==`）、投影缩写与嵌套投影、`nameof`/动态绑定/按名序列化的既有用法（D1–D8）。
3. **链位前置于 LINQ**：在 E16/C26 LINQ 过程式化之前把匿名形状全部显式化，使 LINQ 重写不再需要 `object` 消毒与 record 门控；linq-channel stub 的 RunAfter 已预先登记本 pass。
4. Stub→Implemented 翻转，默认关起步，按 §8 门槛验收后默认开；默认开后退役 §11 列出的转换器侧合成 record/Map.of 代码。

**非目标**：

- 不做 C# 查询表达式的 transparent identifier（`from … let …` 编译器合成透明标识符匿名类型）改写——它们在源码中**没有创建语法节点**，由 LINQ 过程式化（E16/C26）在自己的 lowering 中消化（D9）。
- 不做 `dynamic` 降级（C25）；不做表达式树内改写（沿用 JK-8 共享判定）；不做 LINQ 重写本身（E16 现状/C26）。
- 不修改转换器与 Java compat 作为本 pass 的交付内容；转换器退役与 Java 侧映射验证是默认开门的**验收条件**与后续 PR（§8、§11）。
- 不追求跨程序集的类型身份合并（见 D3：默认每编译单元一份 internal 副本，忠实复刻 C# 编译器行为）。

### 1.3 与 E06/C05 的族关系

| 维度 | E06 `tuple`（已实现） | C05 `enum-declaration`（设计） | C21 `anonymous-type-lift`（本设计） |
|---|---|---|---|
| 输入 | 元组语法（类型/字面量/解构/ValueTuple） | 问题三族 enum 声明 + 用点 | 匿名对象创建 `new { … }` |
| 生成形态 | `readonly struct` + 全部成员 | enum-like `readonly struct` + 外显成员 | **`internal sealed class`** + 只读属性/构造器/Equals/GetHashCode/ToString（D1） |
| 执行模型 | 逐文件 rewriter + 共享 registry + AfterFiles 分发文件 | 整树 Analyze→Plan→回滚（自定义 ExecuteAsync） | **逐文件 rewriter + 共享 registry + AfterFiles 分发**（与 E06 同型，标准 spine 即可，D10） |
| 泛型 | SlotAssigner 槽位规范化 | 不涉及 | 需要（泛型方法/类型中的投影，D2） |
| 跨工程 | public 形状单 owner + internal 形状各一份 | 不创建文件 | **默认 internal 每编译单元一份**（D3，简化 E06 算法） |
| 链位 | delegate 之后 | enum 之后 | **tuple 之后**；linq-channel 已锚本 pass |

---

## 2. 关键设计决策表

> 标注「**待 M1 原型**」的条目须在 M1（§9）以最小原型 + 中间树编译/主转换/javac 证据锁死。

| # | 决策点 | 结论 | 依据与备注 |
|---|---|---|---|
| D1 | 目标表示形态 | **`internal sealed class`：只读自动属性 `{ get; }` + 全参构造器 + override `Equals(object)`/`GetHashCode()`/`ToString()`；不实现 `IEquatable<T>`、不声明 `==`/`!=`、不用 record、不用 struct** | 见 §2.1 语义对照表。关键：匿名类型 `==` 是引用相等而 C# record 是值相等——用 record 会静默改变可观察行为。M1 需验证主转换器对「合成 sealed class + 只读属性 + `string.Concat(params object[])`」的转换质量 |
| D2 | 形状规范化与确定性命名 | 形状键 = 属性**按声明顺序**的 `(名称, 类型结构键)` 序列；类型键走 SemanticModel 符号结构（复用 E06 `BuildDisplayTemplate`/`MangleType`/`SlotAssigner` 思路），**区分 dynamic/object**（二者在 C# 中是不同形状）；名称 = `Anon` + 属性名 mangle + 类型 mangle，480 字符截断、`_2/_3` 碰撞后缀；全部引用以 `global::Cs2jAnonymous.` 限定 | E06 [MakeUniqueName](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleShapeRegistry.cs#L508-L526) 已验证确定性命名；形状不排序进键（顺序不同即不同类型，与 C# 及[现有合成器注释](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/AnonymousTypeRecordSynthesizer.cs#L143-L147)一致），但生成文件前按键排序保证文件内容跨运行稳定（指纹） |
| D3 | 跨方法/跨文件/跨项目去重 | ①方法/文件：整树一个 registry，同形同类型实参即同形；②项目：**默认每个编译单元各生成一份 internal 类型**，忠实复刻「匿名类型 internal、跨程序集同形不同类型」；不引入 E06 的 public owner 图算法 | 匿名类型不可在公共签名中命名，跨程序集流只经 object/dynamic，per-assembly 副本必然可编译且语义最忠实（跨程序集 `Equals` 本就为 false）。InternalsVisibleTo 不改变该结论。**待 M1**：确认是否存在「经前序 pass 物化为具名跨工程签名」的反例（§2.4），若有则对该子集启用 E06 式 public owner |
| D4 | 可变性边界 | 只读属性（get-only，构造期赋值），**不做 init/record/with**；但嵌套成员的可变性原样保留（`new { list = new List<int>() }` 后 `x.list.Add(1)` 合法） | 匿名类型只保证顶层属性只读；class 形态下引用类型成员的原地修改天然保持；struct 形态会引入复制语义，排除 |
| D5 | Equals 语义 | `public override bool Equals(object obj)`：`obj is TName other && EqualityComparer<P1>.Default.Equals(P1, other.P1) && …`，逐字段、按声明顺序；不同形状永不等（精确运行时类型检查由 `is TName` 保证）；null receiver 不适用（实例方法） | 与 Roslyn 生成匿名类型的 Equals 同构；直接复用 E06 [EqualExpr](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleStructFileContent.cs#L206-L207) 的 EqualityComparer 写法。**不声明 IEquatable**（匿名类型没有），避免给下游引入额外接口面 |
| D6 | GetHashCode 语义 | 固定种子 17、逐字段 `hash = hash * 31 + EqualityComparer<P>.Default.GetHashCode(P)`（与 E06 生成 struct 同一算法） | 仅要求：相等实例哈希相等、字典键可用；不要求复刻编译器内部哈希组合常数（BCL 字符串哈希本就带进程随机种子，无法也无需逐位复刻） |
| D7 | ToString 格式 | 生成 `return global::System.String.Concat("{ P1 = ", P1, ", P2 = ", P2, " }");`（object 参数重载：null 实参渲染为空串；区域性/布尔/嵌套全部由各值自己的 `ToString()` 承担，嵌套形状递归自然命中其 ToString）。零属性形状返回 `"{ }"` | C# 匿名 ToString 实测格式即 `{ X = 1, Y = 2 }`、null 渲染为 `""`。`string.Concat(params object?[])` 的 null→Empty 语义精确匹配；比手写三元判断简洁且对值类型/引用类型统一。转换器对该重载的映射质量列 M1 验证项，不达标则退化为「逐字段显式 `(object)P ?? ""` 条件拼接」 |
| D8 | nameof / 反射 / 序列化 | `nameof(x.X)` 零改写（属性同名保留）；生成类挂 `[CompilerGenerated]`（保留「编译器生成」发现面）；基于属性名的 JSON 序列化（System.Text.Json/Newtonsoft）线格式保持（名称与声明顺序保持）；`GetType().Name/FullName`、`IsAnonymousType` 反射判定、手写反序列化为具体匿名类型等**身份反射**无法等价 → AL040 Warning 点名列点，不阻断 | 风险 7（[L926](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926)）同族「反射/字符串契约」；匿名类型名本就是不可言说的 `<>f__AnonymousType…`，依赖其类型身份的代码本身不可移植，先显式盘点 |
| D9 | 与 dynamic 的边界 | 属性类型为 `dynamic` 的形状：生成类中属性**保留 `dynamic` 声明**（C# 合法），形状键区分 dynamic/object（D2）；`dynamic d = new { X = 1 }; d.X` 在中间树照常动态绑定（class 上有真实属性）。该形状打 AL030 Info（Java 侧无 dynamic，属 C25 未覆盖面），不跳过、不降级 | C25 dynamic-desugar 锚 eliminate-goto（远晚）；本 pass 只保真不降级。**待 M1**：若证明转换器对 dynamic 属性必然产出坏 Java，则对「含 dynamic 字段的形状」整体不提升（创建点保留）+ AL030 Warning，走今天的失败路径，不静默产出半截类型 |
| D10 | 执行模型 | **标准 spine**（无需 E11/C05 式 plan/回滚）：全树编译一次 → 逐文件 dirty 快检 → rewriter 边登记形状边改写 → `AfterFilesAsync` 按编译单元闭包分发生成文件 + Compile Include/Link 挂接 → restore → verify build/tests。与 E06 完全同构 | 匿名类型在 C# 中**不可被任何签名命名**，创建点改写后重编译自动重新绑定所有 var/lambda/泛型推断，不存在「声明改了用点没改」的半改写不可编译窗口（§2.4） |
| D11 | 创建点改写面 | 只改写 `AnonymousObjectCreationExpressionSyntax`：语义取 `TypeInfo.Type/ConvertedType`（须 `IsAnonymousType`），登记形状后替换为带 `global::` 限定的 `ObjectCreationExpressionSyntax`，参数按属性声明顺序取各 initializer 的表达式（投影缩写 `new { e.X }` 即 `X = e.X`）；其余语法（成员访问、var、lambda、隐式数组、初始化器）**一律不动**，靠重编译重新绑定 | 与 E06 VisitTupleExpression 同构（[L523-L552](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L523-L552)）；语义解析失败（IErrorTypeSymbol/缺引用）→ AL010 Warning + 原样保留，不硬造类型 |
| D12 | 泛型形状 | 泛型方法/泛型类型内的投影生成泛型类：复用 E06 [SlotAssigner](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleShapeRegistry.cs#L12-L30) 按结构首次出现稠密编号；嵌套形状的类型形参按位置绑定回外层槽位；生成类的形参约束从源码形参约束拷贝（class/struct/unmanaged/new()/基类/接口的非 ref-struct 子集；匿名属性不允许 ref struct 字段，allows-ref-struct 约束不会出现） | `M<T>(T x) { var a = new { Item = x }; }` 的编译器匿名类型本就是 `<>f__AnonymousType<T>`；不拷约束可能在 `where T : new()` 等场景使生成类无法以原 T 实例化 |
| D13 | 支撑库 | **不注入支撑库**（descriptor `SupportLibrary = None`）：Equals/GetHashCode 用 BCL `EqualityComparer<T>.Default`，ToString 用 BCL `String.Concat`；无共享工程、无 csproj 运行时引用、无过滤清单 | 与 C05 D13 同原则；Compile Include/Link 只挂**生成源码**，与 E06 挂生成 struct 文件同型（不是支撑库工程） |
| D14 | 诊断与 residual | 创建点残余在 strict 下为 Error（AL060）；residual 闸门 = 语义扫描「绑定到匿名类型的 `AnonymousObjectCreationExpressionSyntax` 数量为 0」；查询 transparent identifier 的匿名符号残余只计 Info（D9/非目标），不计失败 | 匿名创建节点语法唯一，不存在 E04 那种语法同形误报；transparent identifier 无创建节点，必须显式排除以免闸门误红 |
| D15 | 幂等 | 输出再跑：dirty 快检无 `AnonymousObjectCreationExpressionSyntax` → 整文件字节复制；形状集为空 → 不生成任何文件 | 与 E06 IsTupleDirty/Clean 复制同构 |
| D16 | 标识符与碰撞 | 属性名按 C# 规则本就是合法标识符，生成类内成员（构造参数小写化、类型形参 `__P{i}`）做声明空间探测，碰撞确定性加序号；生成类名只含字母数字/下划线；属性名撞 Java 保留字（`Int/Class/new`…）**不在本 pass 处理**，由链尾 C08 java-keywords 统一改名 | 现有合成器的 `EscapeJavaKeyword`/`intValue`/`classValue` 启发式（[L132-L134](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/AnonymousTypeRecordSynthesizer.cs#L132-L134)）随之退役，消除单文件启发式 |

### 2.1 D1 详述：为什么是 sealed class 而不是 record / struct / Map

| 语义点 | C# 匿名类型（真相） | sealed class（选） | C# `record class` | readonly struct |
|---|---|---|---|---|
| 引用/null/默认 | 引用类型，可 null | **保持** | 保持 | 破坏（默认值非 null、装箱） |
| `==`/`!=` | **引用相等**（不重载运算符） | **保持** | **值相等（静默改变）** | 无默认 `==`，编译断裂 |
| `Equals(obj)` | 按字段值、精确同形 | **保持**（D5） | 值相等（近似，但跨 record 名不同即不等，行为接近） | 值相等 |
| `GetHashCode` | 按字段 | **保持**（D6） | 保持 | 保持 |
| `ToString` | `{ X = 1, Y = 2 }` | **保持**（D7） | PrintMembers 输出同形（近似但非承诺一致） | 需自写 |
| `with` 表达式 | 不支持 | 不支持（保持） | **新增能力（可观察差异）** | 不支持 |
| `IEquatable<T>`/Deconstruct | **无** | 不生成（保持） | 自动具备（新增面） | 可自选 |
| 顶层只读/嵌套可变 | 属性只读、嵌套引用可原地改 | **保持**（D4） | 属性 init-only，同 | 嵌套修改作用于副本（破坏） |
| 字典键/集合 Distinct/GroupBy | 值相等 | **保持** | 保持 | 保持 |
| 泛型推断为引用类型 | 是 | 是 | 是 | 否（约束面变化） |

结论：record 与 struct 都会引入可观察差异，sealed class 是唯一无需任何使用点补偿就能保持全部语义的形态。M1 唯一要证伪的形态子问题是**主转换器对合成 sealed class（只读自动属性、全参构造器、override 三方法、`string.Concat(params object[])`）的 Java 转换质量**；若缺口过深，备选不是 record/struct，而是在 class 形态内调整成员写法（如 ToString 退化条件拼接、属性改显式 backing field + getter），诊断/registry/分发模型不变。

### 2.2 形状规范化示例（D2/D12）

```
new { X = 1, Y = "a" }
  → key "(X:global::System.Int32;Y:global::System.String)"
  → name Anon_X_Int32_Y_String
new { Y = "a", X = 1 }        → 不同形状（顺序不同）→ Anon_Y_String_X_Int32
new { X = 1, Y = (object)"a" } → 不同形状（object vs String）
new { X = (dynamic)1 }         → 不同形状（dynamic vs object），属性保留 dynamic（D9）
M<T>(T v) => new { V = v }    → Anon_V_T0，生成 internal sealed class Anon_V_T0<__P0>（约束拷贝）
new { Outer = new { K = 1 } } → 内层 Anon_K_Int32 先注册；外层 Anon_Outer_Anon_K_Int32
```

### 2.3 生成类型形状（产物示意）

```csharp
// <auto-generated>
//   Generated by the cs2j anonymous-type-lift preprocessor.
//   One internal sealed immutable class per distinct anonymous shape.
// </auto-generated>
#nullable disable
namespace Cs2jAnonymous
{
    [global::System.CodeDom.Compiler.GeneratedCode("cs2j-anonymous-type-lift", "1.0")]
    [global::System.CompilerGenerated]
    internal sealed class Anon_X_Int32_Y_String
    {
        public int X { get; }
        public string Y { get; }
        public Anon_X_Int32_Y_String(int x, string y) { X = x; Y = y; }

        public override bool Equals(object obj)
        {
            return obj is Anon_X_Int32_Y_String other
                && global::System.Collections.Generic.EqualityComparer<int>.Default.Equals(X, other.X)
                && global::System.Collections.Generic.EqualityComparer<string>.Default.Equals(Y, other.Y);
        }

        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 31 + global::System.Collections.Generic.EqualityComparer<int>.Default.GetHashCode(X);
            hash = hash * 31 + global::System.Collections.Generic.EqualityComparer<string>.Default.GetHashCode(Y);
            return hash;
        }

        public override string ToString()
            => global::System.String.Concat("{ X = ", X, ", Y = ", Y, " }");
    }
}
```

合成器语言子集自律（对齐 C05 §2.3）：只发射自动属性、构造器、`is` 模式、`EqualityComparer`、`String.Concat`、整数字面量等转换器已稳定支持的构造；禁止在生成成员内使用 LINQ、插值字符串 handler、switch 表达式等高危形态。

### 2.4 为什么标准 spine 足够（无 plan/回滚）

C# 源码中匿名类型**永远不能被具名书写**（无显式类型语法、不可作方法/局部函数签名——局部函数返回类型必须显式，已核实 [LocalFunctionLiftRewriter.cs#L530-L531](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs#L530-L531) 只会搬运显式语法），因此：

- 本 pass 之前的 E01/E02/E03/E04/E05/E06 都不可能在产物文本中物化匿名类型名（lambda 保持推断、提升方法均为 private 且无法以匿名类型签名）；
- 创建点一旦替换为具名构造，var 声明、lambda 形参/返回、泛型实参推断、隐式数组元素类型、foreach 变量、三元合并全部由同一轮重编译**自动**绑定到生成类型；
- 不存在「声明/用点必须原子改写否则不可编译」的耦合——生成文件经 Compile Include/Link 与改写源码在同一次 restore/build 中共同编译，失败由 verify 直接暴露，失败面归属到文件即够，无需符号级 plan 回滚。

M1 须加一条**扫描断言**固化该前提：对 C21 输入树做全编译，凡 `INamedTypeSymbol.IsAnonymousType` 的类型只允许出现在匿名创建节点、var/lambda 推断流与查询 transparent identifier 中，不出现在任何显式 `TypeSyntax` 文本位置；命中即说明前序 pass 出现新物化点，升级为 AL020 并重新评估链位。

---

## 3. 注册表与链位契约

### 3.1 现状与目标描述符

stub 现状（[PassStubCatalog.cs#L249-L258](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L249-L258)）：id `anonymous-type-lift`，显示名 "Anonymous type lift"，目录 `.cs2j-anonymous-type-lift-src`，`PassStage.Declarative`，`PassStatus.Stub`，`defaultEnabled: false`，`runAfterIds: { PassIds.Tuple }`，`needsSemanticModel` 默认 true，catalogItems `C21`。linq-channel stub 的 `runAfterIds: { extension-methods, anonymous-type-lift }`（[L226](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L226)）。

翻转后目标描述符：

| 字段 | 值 | 理由 |
|---|---|---|
| Id | `anonymous-type-lift` | 不变 |
| Stage | `Declarative` | 生成新类型声明，与 stub 登记及 E06 同族 |
| Status | `Implemented` | Stub→Implemented 翻转 |
| DefaultEnabled | **false（落地起步）→ 满足 §8 后翻 true** | 对齐 record-readonly/array-method/generic-deepen/java-keywords 策略与 catalog 风险 10（[L929](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L929)） |
| RunAfterIds | `{ "tuple" }`（维持） | 复用 E06 基建且形状上无前后依赖，锚 tuple 即落在声明前置族（E06→C21）；linq-channel 已反向锚本 pass |
| IntermediateDirectoryName | `.cs2j-anonymous-type-lift-src`（维持） | |
| NeedsSemanticModel | true | 形状/类型键/泛型槽位全靠符号模型 |
| SupportLibrary | `None` | D13 |
| VerifyBuildByDefault | **true** | 生成文件 + 创建点改写跨文件，逐环整树编译验证（比 E06 历史默认更严，对齐 C05 新通道标准） |
| VerifyTestsByDefault | **true** | Equals/GetHashCode/ToString/字典键/LINQ 行为由中间树测试钉住 |

### 3.2 链位拓扑后果与翻转顺序校验

拓扑序中本 pass 位于 `… → delegate → tuple → anonymous-type-lift → foreach-deconstruction → strip-default → ref-out-in → unsigned → generic → … → linq-channel(stub) …`。要点：

1. **必须早于 E16/C26 LINQ**：嵌入式 E16 在主转换期运行（[ProjectPasses.cs#L151-L351](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L151-L351)），消费的是预处理后的树——本 pass 落链后 E16 见到的投影已是具名类型；C26 linq-channel 落地时其 RunAfter 边已存在，无需再改。
2. 与 `foreach-deconstruction` 都只声明 RunAfter=tuple，彼此无强依赖（foreach-deconstruction 处理元组解构，不碰匿名形状）；翻转时须在注册表顺序断言中显式锁定「anonymous-type-lift 紧随 tuple、且在 foreach-deconstruction 之前（或断言其确定性相对位置）」，避免拓扑并列时插入顺序漂移。
3. 晚于本 pass 的 generic/generic-deepen 正常处理生成类的泛型实例化；C08 java-keywords 在链尾统一覆盖生成类成员改名（D16）。
4. 生成类型不依赖 E06 的 `Cs2jTuples` 命名空间/支撑工程；两 pass 的生成文件各自独立、命名空间不同（`Cs2jTuples` vs `Cs2jAnonymous`）。

### 3.3 Stub→Implemented 翻转清单

1. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删除 `Ids.AnonymousTypeLift` 常量与 Stubs 表条目（常量迁至 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L25) 的 `PassIds`）；同步更新 linq-channel stub RunAfter 对该常量的引用（改为 `PassIds.AnonymousTypeLift`）。
2. `PassCatalog.Implemented`：按 §3.1 增加描述符（`runAfterIds: new[] { PassIds.Tuple }`，verifyBuild/Tests=true，defaultEnabled=false，SupportLibrary=None）。
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L93-L237)：`CreateAdapter` switch 增加 `PassIds.AnonymousTypeLift => new AnonymousTypeLiftPassAdapter(...)`；`PassChainConfiguration` 增加 `Func<AnonymousTypeLiftOptions>` 懒加载工厂（仿 [JavaKeywordOptionsFactory](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L47-L48)）。无 legacy toggle（不新增 `--no-anonymous-type-lift` 老开关），启停只走描述符默认值 + 通用 `--enable-pass/--disable-pass`。
4. 新增 CLI `Adapters/AnonymousTypeLiftPassAdapter.cs`（§5）。
5. 注册表顺序断言/`--list-passes` 测试更新（Stub→Implemented、默认值、链位可见；CS2J9001 行为对该 id 消失）。
6. [.gitignore](file:///d:/code/cs2j/.gitignore#L89-L102)：增加 `.cs2j-anonymous-type-lift-src/`（与既有 `.cs2j-tuple-src/` 同处登记）。

stub 显式启用即 [CS2J9001](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L12) 的行为在翻转后自然消失；翻转 PR 必须与适配器同批交付，不允许中间态（foundation plan L138）。

### 3.4 生成文件目录约定

- 中间树根：`.cs2j-anonymous-type-lift-src/`（descriptor 驱动，runner 自动建/清/保活/preserve）。
- 生成文件名：单工程 `_Cs2jAnonymousTypes.cs`；多工程 `_Cs2jAnonymousTypes.{ProjectName}.cs`（与 E06 [DistributeStructFiles](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs#L292-L326) 命名同型）。
- 落盘位置：中间树根目录；经 csproj `Compile Include + Link` 挂到每个实际编译了被改写源文件的项目（复用 E06 [EnsureCompileItem](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs#L421-L473)），SDK 默认 glob 兜底。

---

## 4. Core 组件设计

### 4.1 新文件清单（`src/CSharpToJava.Core/AnonymousTypeLift/`）

命名空间 `CSharpToJava.Core.AnonymousTypeLift`，组织方式对齐 `TupleToStruct/`（4 文件）：

```
AnonymousTypeLiftPreprocessor.cs   # 公共入口：
                                   #   AnonymousTypeLiftPreprocessor(options)
                                   #   AnonymousTypeLiftFileResult RewriteFile(
                                   #       string sourceCode, SemanticModel model,
                                   #       AnonymousTypeLiftStatistics stats,
                                   #       List<AnonymousTypeLiftDiagnostic> diags)
                                   #   string? GenerateTypeFile() / GenerateTypeFile(shapeKeys)
                                   #   IReadOnlyDictionary<string,IReadOnlyList<string>> ShapesByFile
                                   #   IReadOnlyList<string> CloseShapeKeys(shapeKeys)
                                   #   IReadOnlyCollection<string> TypeNames
                                   #   internal static bool IsAnonymousDirty(CompilationUnitSyntax)
                                   #   FormatAndValidate（三候选，复刻 E06）
AnonymousShapeRegistry.cs         # SlotAssigner（槽位规范化，直接照搬 E06 思路）
                                   # AnonymousPropertySpec（Key/MangledName/DisplayTemplate/
                                   #   NestedShape/NestedArgSlots/IsDynamic/IsPubliclyAccessible）
                                   # AnonymousShape（Key/Name/Properties/GenericArity/Constraints/
                                   #   IsPubliclyAccessible/HasDynamic）
                                   # AnonymousShapeRegistry：GetOrCreate(INamedTypeSymbol)、
                                   #   FindByKey、形状键/类型 mangle/约束收集/确定性命名/排序
AnonymousTypeFileContent.cs       # Generate(shapes, namespaceName)：
                                   #   internal sealed class + 自动属性 + ctor +
                                   #   Equals/GetHashCode/ToString + [CompilerGenerated]
AnonymousTypeLiftModels.cs        # AnonymousTypeLiftOptions（NamespaceName="Cs2jAnonymous"、
                                   #   GeneratedFileName="_Cs2jAnonymousTypes.cs"）
                                   # AnonymousTypeLiftStatistics / AnonymousTypeLiftDiagnostic /
                                   #   AnonymousTypeLiftSeverity / AnonymousTypeLiftFileResult
```

### 4.2 数据流（标准 spine，与 E06 同构）

1. **准备/编译**：runner 复制工程树 → parse 全树 + 建 `CSharpCompilation`（`LanguageVersion.Latest`、`NETCOREAPP`、allowUnsafe、`GetBasicReferences()`；仿 E06 [CreateAdditionalSyntaxTrees](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs#L59-L88) 合成 ImplicitUsings 工程的 global usings，否则属性类型解析会退化）。
2. **逐文件改写**：dirty 快检（无 `AnonymousObjectCreationExpressionSyntax` 直接字节复制）→ `AnonymousSiteRewriter` 在原语义模型上工作：创建点经语义模型取匿名类型符号 → `registry.GetOrCreate(symbol)`（嵌套形状递归先注册）→ TrackShape 记入 `ShapesByFile[file]`（含嵌套闭包）→ 替换为限定构造表达式；其余节点不动。
3. **格式化/校验**：复刻 E06 [FormatAndValidate](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L213-L256)：改动区间格式化 → 整文件格式化 → 原文，三候选重新 parse，取零 error 之首；全失败则保留原文 + AL001 Error（不产出语法损坏的中间 C#）。
4. **生成文件分发（AfterFiles）**：adapter 按「文件 → 编译单元（ProjectCompileSet.ComputeCompiledFiles）」聚合每个项目所需形状闭包，生成 internal 类型集合文件，写中间树根并 `EnsureCompileItem` 挂接（D3：每项目一份，无 owner 图、无跨项目复用判定）。
5. **restore + verify**：`dotnet build`/`dotnet test -c Release`（descriptor 默认双开，受全局 `--no-verify-*` 控制）。

### 4.3 形状注册表（AnonymousShapeRegistry）要点

- **键**：`(` + 属性序列 `name:typeKey` 以 `;` 连接 + `)`；属性名大小写敏感；类型键递归：标量取 `global::` 限定显示（IErrorTypeSymbol/pointer/function-pointer → AL010 + `global::System.Object`，与 E06 同口径），数组 `[…]`，泛型 `Name<args>`，dynamic 记 `dynamic` 键并在 shape 上标 HasDynamic，嵌套匿名替换为 `ANON<嵌套键>[外层槽位…]`。
- **槽位**：照搬 E06 SlotAssigner——形状私有稠密编号，嵌套形状形参经 argSlots 位置绑定回外层；`CollectParameterOrder` 结构遍历顺序与编号顺序严格一致。
- **约束**：收集形状属性类型引用到的全部源类型形参，记录其约束子句（D12）；生成文件渲染为形参约束。
- **可访问性**：沿用 E06 [IsPubliclyAccessible](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleShapeRegistry.cs#L386-L424) 计算（仅用于诊断信息；D3 下生成类恒 internal）。
- **命名**：`Anon` + 每属性 `SanitizeIdentifier(属性名)` + `MangleType(属性类型)` 顺序拼接；泛型后缀由 GenericArity 在渲染时加 `<__P0,…>`；截断 480、`_2` 后缀去碰撞；命名空间固定 `Cs2jAnonymous`（可经 Options 覆盖）。
- **稳定性**：注册表内部按首次登记有序（嵌套先于外层），`GenerateTypeFile` 输出前按 Key 排序，保证多文件/多运行的生成内容字节级稳定（指纹缓存正确性，catalog 风险 11）。

### 4.4 站点改写器（AnonymousSiteRewriter）规则全集

1. **匿名对象创建**（唯一改写节点）：`VisitAnonymousObjectCreationExpression`：
   - `TypeInfo(node).Type`（回退 `ConvertedType`）须为 `INamedTypeSymbol { IsAnonymousType: true }`，否则 AL010 + 原样；
   - 先 `Visit` 各 initializer 表达式（保证嵌套创建先登记、参数表达式也被改写）；
   - `GetOrCreate` 形状后构造 `new global::Cs2jAnonymous.Name<源类型实参>(arg0, arg1, …)`；泛型实参按源码符号显示（仿 E06 [ShapeDisplayInScope](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L1663-L1680)，槽位→源形参名）；
   - 挂统一注解 `Cs2jAnonymousTypeLiftRewrite`（供区间格式化与后续 pass 幂等识别，不落盘进产物文本）。
2. **查询语法内投影**：`select new { … }`、`let`/`join … into` 子句中的匿名创建同为该节点，照常改写；查询本身保持查询语法（C26/E16 之后处理）。
3. **不动的节点**：成员访问（`.X` 重新绑定到生成属性）、`var`/`var p = …`、lambda、隐式数组 `new[] { new{…}, … }`（元素类型重编译推断为生成类型）、对象/集合初始化器、三元/`is` 属性模式、`nameof`、`(object)`/`(dynamic)` 强转。
4. **跳过条件**：节点位于表达式树 lambda 内（共享 JK-8 判定工具，catalog 风险 8）→ AL020 Warning 原样保留；语义模型解析出 IErrorTypeSymbol → AL010 原样保留。
5. **残余统计**：每文件改写后 parse 计数残余 `AnonymousObjectCreationExpressionSyntax`（语义复核仍绑定匿名类型），计入 Statistics.ResidualCreations；>0 出 Warning（strict 下 AL060 Error）。

### 4.5 统计与模型（AnonymousTypeLiftModels.cs）

`AnonymousTypeLiftStatistics`：`FilesScanned / FilesTransformed / FilesSkippedClean / ShapesRegistered / GenericShapes / DynamicShapes / NestedShapes / CreationsRewritten / TypesGenerated / CrossCompilationCopies / ResidualCreations`。Options：`NamespaceName="Cs2jAnonymous"`、`GeneratedFileName="_Cs2jAnonymousTypes.cs"`。诊断模型与 E06 同型（Severity/Message，文件路径由 adapter 前缀拼接）。

---

## 5. CLI 适配器与集成

### 5.1 AnonymousTypeLiftPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/AnonymousTypeLiftPassAdapter.cs`，继承 [ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs)，形态对齐 [TuplePassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs)（标准 spine，不 override ExecuteAsync）：

- `Descriptor => PassCatalog.ImplementedById(PassIds.AnonymousTypeLift)`；`LogPrefix "[anonymous-type-lift]"`；
- `CreateParseOptions` 用 `LanguageVersion.Latest`；`CompilationName "anonymous-type-lift-preprocess"`；
- `CreateAdditionalSyntaxTrees`：**照搬 E06 的 ImplicitGlobalUsings 合成**（[TuplePassAdapter.cs#L59-L88](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs#L59-L88)）；
- `RewriteFileAsync`：调 Core `RewriteFile`，Changed=false 返回 null（runner 字节复制），Changed=true 返回 `PassFileRewrite { PreserveBom = true }`；诊断加 `RelativePath` 前缀；
- `AfterFilesAsync`：`DistributeTypeFiles(tree)`——E06 分发算法的简化版：
  - `ProjectCompileSet.ComputeCompiledFiles` 计算「项目→编译的改写文件→所需形状闭包」；
  - **恒 internal 生成、每项目一份**（D3）：无 public owner 图、无 `IsReachable`/引用图计算；多目标/共享源码天然各得一份；
  - 文件名 `_Cs2jAnonymousTypes[.{project}].cs`，写中间树根（UTF-8 无 BOM，`File.Exists` 去重），`EnsureCompileItem` 同款 Include+Link 挂 csproj；
  - verbose 打印每项目形状数；
- `VerifyBuild/VerifyTests` 取构造入参（convert-project 全局 verify 开关，默认 true）；`DeleteBuildOutputsAfterRestore = true`（verify 型 pass，对齐 enum/C05）；
- `OnBuildFailed/OnTestsFailed/OnRestoreFailed` 记 AL050 系列 Error/Warning；
- `BuildChainSummaryLine` 形如 `anonymous-type-lift: N transformed, M clean, K creations rewritten, S shapes (G generic, D dynamic), T types generated, R residual`；诊断行经 `BuildChainDiagnosticLines` 输出。

### 5.2 开关 / 指纹 / verb

- 开关：翻转后默认关，经 `--enable-pass anonymous-type-lift` 开启；§8 达成后翻 descriptor 默认值；无 legacy toggle，`BuildLegacyToggles` 不改。
- 指纹：注册表全集令牌自动产生 `pass:anonymous-type-lift=on/off`（foundation plan L72）；生成文件位于本 pass 中间目录内，随 `opts.Source` 串联自然成为下一环输入；支撑库过滤不涉及本 pass（SupportLibrary.None，`IsExecutedSupportLibraryProject` 无需改）。
- 独立入口：仅通用 `preprocess --pass anonymous-type-lift` 与 convert-project 链；**不新增** `Project*Preprocessor` 薄包装 facade（stub 是注册表先建的，无历史 verb，对齐 C05 §5.1）。
- `--list-passes` 显示 Implemented/默认关/目录/RunAfter=tuple。

### 5.3 verify 与失败语义

verify 失败（build/test 退出码非 0）按 runner 既有约定置 adapter HasErrors、链失败退出；生成文件导致的编译错误（形状/约束/可访问性缺口）在中间树当场暴露，不留到 Java 产物——满足 catalog 风险 12「禁止静默错误输出」第一原则。

---

## 6. 诊断策略

码位 AL0xx（MSBuild 风格，只追加）：

| 码 | 级别 | 含义 |
|---|---|---|
| AL001 | Info | 创建点已提升（消息含文件、形状名/键），汇总计数 |
| AL002 | Warning | 改写后输出无法通过语法校验，原文保留（对齐 E06 invalid-syntax 保护） |
| AL010 | Warning | 匿名创建点符号信息不全（IErrorTypeSymbol/缺引用），该节点原样保留 |
| AL020 | Warning | 表达式树内匿名创建点（JK-8 共享判定）/§2.4 显式类型位置出现匿名符号，跳过 |
| AL030 | Info（strict 下 Warning） | 形状含 `dynamic` 属性（D9）：C# 保真提升，Java 侧属 C25 未覆盖面 |
| AL040 | Warning | 反射/身份契约站点盘点：`GetType().Name/FullName`、`IsAnonymousType`、按类型身份的反射/序列化调用，逐处列出（D8） |
| AL050 | Warning/Error | restore 失败（Warning，沿用各 pass 惯例）/ build/test 失败（Error） |
| AL060 | Warning（strict 下 Error） | 残余匿名创建节点数 >0（residual 闸门，D14） |
| AL070 | Info | 编译单元生成文件分发结果（项目名、形状数、文件名） |

原则：无法等价的站点（表达式树、身份反射）显式诊断 + 保留原状，**禁止** TODO/占位/把半截类型透传到 Java；transparent identifier 残余不诊断为问题（非目标）；`--strict` 下 AL020/AL060 升级 Error，供「输入只用受支持构造」的 CI 断言。

---

## 7. 测试计划

### 7.1 Core 单测矩阵（`tests/CSharpToJava.Tests/AnonymousTypeLiftUnitTests.cs`，仿 TupleToStruct 单测：内联源码→整树编译→RewriteFile→忽略空白断言 + 输出可编译断言）

**去重/命名（确定性）**：
- 同形状跨方法、跨类、跨文件 → 同一形状名、同一生成类型；
- 属性顺序不同 → 不同形状；属性名大小写不同 → 不同形状；`string` vs `object` 属性 → 不同形状；`dynamic` vs `object` → 不同形状；
- 跨多次运行/文件输入顺序打乱 → 形状名与生成文件内容字节级稳定；
- 命名碰撞（极端长属性名截断后加 `_2`）。

**嵌套/集合/数组**：
- 嵌套投影 `new { Outer = new { K = 1 } }`：内层形状先注册、外层字段类型为内层实例化；
- 隐式数组 `new[] { new { X = 1 }, new { X = 2 } }` → 推断为生成类型[]；
- List/集合初始化器、三元同形合并、`??`/默认流中的创建点。

**LINQ / foreach / 查询语法**：
- 方法链 `Select(x => new { … })`、查询式 `select new { … }`、`GroupBy/Join/Distinct` 投影；
- foreach 变量绑定具名类型（对应 [ForeachAnonymousTypeTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachAnonymousTypeTests.cs) 场景的 C# 侧版本）；
- 提取方法形态（continue/break 导致 helper）在中间树的返回类型为具名类型（对应 LinqAnonymousTypeExtractedMethodTests 场景）。

**语义（中间树可执行断言）**：
- `Equals`：同值 true、不同值 false、不同形状 false、null false；引用类型字段 null 对 null；
- `==`：两个同值实例 **false**（引用相等，D1 核心回归）；同一引用 true；
- `GetHashCode`：相等实例同哈希；Dictionary 以投影为键增查、GroupBy 分组正确；
- `ToString`：`{ X = 1, Y = a }`；null 字段渲染空串；嵌套投影递归；布尔/DateTime 走值自身 ToString（区域性）；
- `nameof(p.X)` 编译并求值为 `"X"`；
- 顶层只读：`p.X = 1` 不可编译（用负例断言）；嵌套可变：`new { L = list }.L.Add(1)` 可编译。

**泛型（D12）**：泛型方法/泛型类型中投影 → 生成泛型类；多槽位/嵌套泛型；源约束（class/struct/new()/接口）拷贝后实例化可编译。

**诊断/保护**：表达式树内创建（AL020）；缺引用 IErrorTypeSymbol（AL010）；dynamic 形状（AL030）计数；残余计数与 strict；FormatAndValidate 三候选；BOM 保持；clean 文件字节级复制；**幂等**（输出再跑零改写、零生成文件）。

### 7.2 项目级 fixture（`tests/SampleAnonymousTypeLiftSolution/`，仿 SampleTuple/JavaKeyword fixture：Lib+App+Tests+sln，纳入 ProjectTests Content）

- **Lib**：LINQ 投影工具方法（返回 object 签名，内部创建匿名类型）、泛型方法投影、嵌套投影、含 `dynamic` 属性形状一处；
- **App**：与 Lib **同形的独立创建**（断言两工程各得 internal 副本、互不依赖）、foreach over 投影、查询式 + 方法链混合、`Distinct/GroupBy/Join/ToDictionary` 键、隐式数组、`nameof`；
- **Tests 工程**：行为等价 xUnit——Equals/GetHashCode/==（引用）/ToString 格式/null 渲染/字典键分组；
- 断言：`Success`；统计/诊断码；中间树 `.cs2j-anonymous-type-lift-src` 中零 `AnonymousObjectCreationExpressionSyntax` 绑定匿名类型；生成文件经 Link 挂入各项目；`dotnet build -c Release` 退出码 0；`dotnet test -c Release` 通过；
- 关闭 pass 时链输出与今天字节一致（clean 工程零触碰）。

### 7.3 Release build/test 与全量回归

- `dotnet build -c Release`（CSharpToJavaConverter.slnx）；Tests/ProjectTests/ConversionTests 三工程 `dotnet test -c Release` 全绿或红灯集不扩大；
- 链序组合专项：tuple→anonymous-type-lift 双 pass 同树（元组作匿名属性、匿名投影作元组元素交叉用例）；与 foreach-deconstruction 相邻顺序用例；
- 单文件直转路径（ConversionPipeline，不经预处理）行为不变——现有转换器路径在默认关期间必须零回归。

### 7.4 residual 闸门

语义级断言（D14）：启用 pass 的中间树中，绑定到匿名类型符号的 `AnonymousObjectCreationExpressionSyntax` 为 0；同时断言查询 transparent identifier 引发的匿名符号残余**只计 Info 不报错**（用含 `let/into` 的查询式用例钉住该豁免，避免闸门误红）。

### 7.5 CLI 与一致性

- `--enable-pass anonymous-type-lift` 启停、`--list-passes` Implemented/默认关/链位、preserve 目录、verify 失败退出码；
- `ProjectPreprocessorsConsistentTests` 追加 adapter/描述符/目录断言；注册表拓扑顺序断言锁定 §3.2 位置；
- 现有转换器侧测试（ForeachAnonymousTypeTests、LinqAnonymousTypeExtractedMethodTests、LinqChainRefactoringTests 中 record 名/关键字断言）：默认关期间保持现状不动；转换器退役 PR（§11 M5）中按项目级路径重写期望，不在本 pass 内为迁就旧断言扭曲设计；
- [DualTargetCorpus](file:///d:/code/cs2j/tests/DualTargetCorpus) 增补 LINQ 投影/嵌套/Equals/ToString 双目标用例。

---

## 8. 默认开启验收门槛

默认关落地；同时满足以下条件才翻 descriptor `defaultEnabled: true`（对齐 catalog 风险 10 与 foundation 交付包模板第 5 条）：

1. 三工程 `dotnet build/test -c Release` 全绿，存量红灯集不扩大；单文件直转路径零回归（pass 关闭时字节一致）。
2. SampleAnonymousTypeLiftSolution 在启用下 build/test 通过（含跨工程 internal 副本、LINQ、字典键、ToString/Equals/== 全套行为断言）。
3. residual 闸门启用时为 0（strict 通过）；AL010/AL020/AL040 在 sdk 样本中的命中类型完成分类与人工处置文档，无静默透传。
4. **sdk 基线补扫**（catalog C21 条目不带 sdk 计数，不杜撰数字）：统计匿名创建点密度、形状数/创建点比、泛型形状与 dynamic 形状占比、嵌套形状占比、反射/身份契约站点数；启用后基线转换成功率/产物可编译率不降低。
5. 链序组合（tuple、foreach-deconstruction、generic）与指纹/增量缓存（preserve 目录、pass 令牌、生成文件 Link 挂接）在基线样本验证通过。
6. **M1 形态项锁死**：主转换器对「合成 sealed class + 只读自动属性 + `string.Concat(params object[])` + `is T other` 模式」的 Java 转换质量经端到端验证（javac 通过 + Equals/ToString 行为用例通过）；不达标项已按 §2.1 备选写法收口。
7. §11 转换器退役方案经评审（退役 PR 可独立于翻默认开，但默认开前须确认 Map.of/合成 record 双轨不再被可达路径需要）。

---

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M1 | **原型定形态**：最小 ShapeRegistry（非泛型形状）+ 单个 `new { X=1, Y=2 }` 的端到端（中间树 build + 主转换 + javac），锁定 D1 sealed class 形态与 D7 ToString 写法；§2.4 显式类型位置扫描断言；E06 事实函数（mangle/sanitize/EqualityComparer 渲染）提取清单 | Core ~350 行 + 可丢弃原型 |
| M2 | Core 骨架（Models/Preprocessor/FormatAndValidate/dirty 快检）+ ShapeRegistry 全量：泛型槽位、约束拷贝、dynamic/object 区分、嵌套闭包、确定性排序 | ~500 行 |
| M3 | AnonymousTypeFileContent 全成员（属性/ctor/Equals/GetHashCode/ToString/泛型/特性）+ SiteRewriter 全集（查询内投影/表达式树跳过/残余统计）+ 单测矩阵主体 | ~500 行 |
| M4 | CLI adapter（含简化版分发：编译单元映射、Include+Link、ImplicitUsings 合成）+ 注册表翻转（默认关）+ SampleAnonymousTypeLiftSolution + residual 闸门 + 幂等 + CLI/一致性测试 | ~350 行 + fixture |
| M5 | sdk 补扫与数据评审；§11 转换器退役 PR（删 synthesizer/Map.of/object 消毒/record 门控，重写相关测试期望）；双目标语料扩充 | 独立 PR |
| M6 | 按 §8 门槛评审翻默认开；C26 linq-channel 落地时对齐「具名类型输入」契约（移除匿名分支） | 独立排期 |

---

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | 主转换器对合成 sealed class 的转换质量未知（只读自动属性、全参 ctor、`is T other`、`String.Concat(params object[])`、`EqualityComparer<T>` 泛型静态） | catalog 风险 14「映射存在≠语义可用」；现有匿名路径从不产出此类 Java（走 Map.of/Object） | M1 端到端原型先行；合成器语言子集自律；不达标只调整成员写法不换形态；默认关+门槛 6 |
| R2 | **`==` 语义陷阱**：若实现误用 record，值 `==` 静默替换引用 `==`；若用 struct，null/复制/字典全面漂移 | §2.1 对照表；C# 匿名类型不重载 `==` 是硬语义 | D1 锁定 sealed class；单测显式钉「同值实例 == 为 false」；评审检查生成模板不含运算符 |
| R3 | 跨程序集形状身份：internal 副本最忠实，但若存在经前序 pass 物化的跨工程具名签名，per-assembly 副本会编译失败 | D3/§2.4；前序 pass 理论上不可能物化匿名名，但无扫描证据 | M1 扫描断言（AL020）；确有反例时仅对该子集启用 E06 public owner 算法，不全局改策略 |
| R4 | 泛型形状：约束遗漏导致生成类无法以原 T 实例化；ref struct/allows-ref-struct 边界 | D12；匿名属性禁止 ref struct 字段，但形参约束样式多样 | 约束全量拷贝；泛型单测矩阵；IErrorTypeSymbol 按 E06 口径降级 object + 告警 |
| R5 | 反射/序列化身份契约：`GetType().Name`、`IsAnonymousType`、`[CompilerGenerated]` 发现、按类型的序列化/模型绑定行为变化 | catalog 风险 7；匿名名本不可言说，但确有反射代码判定 IsAnonymousType | D8：保留 CompilerGenerated、属性名/序保持、AL040 显式盘点；JSON 按名往返入 fixture |
| R6 | dynamic 属性：保真声明后 Java 侧无 dynamic 支持（C25 未做），可能产坏 Java | LinqRewriter 对 dynamic 有消毒先例；JK-8/C25 长期禁区 | D9：C# 侧保真 + AL030 计数；M1 验证转换器行为，必要时含 dynamic 形状整体不提升（保留今天失败路径，不静默） |
| R7 | 查询 transparent identifier 无创建语法，residual/改写边界易误伤 | C# 查询绑定合成匿名类型只存在于语义模型；E16  lowering 自行处理 | 明确非目标；residual 只数创建节点；含 let/into 查询式 fixture 钉住豁免；C26 RunAfter 已保证顺序 |
| R8 | 生成文件 + 每环 verify 的耗时成本（catalog 风险 11） | E06 历史默认不 verify；本 pass 默认双 verify | verify 可关（`--no-verify-*`）；clean 工程零触碰；形状级指纹稳定可缓存；无命中项目不挂 Compile 项 |
| R9 | 默认关双轨期：转换器 Map.of/合成 record 路径与预处理路径行为分叉，测试期望维护两套 | §1 两条路径；ForeachAnonymousTypeTests 等面向转换器启发式 | 默认关期间转换器零改动；退役集中在 M5 单 PR；项目级 fixture 只验证 C# 侧新路径 |
| R10 | 可空引用类型（NRT）：生成文件 `#nullable disable` 与源工程 NRT 上下文混用产生告警/告警即错误差异；属性类型含 nullable 标注 | E06 生成文件同样 `#nullable disable` 但 struct 值类型受影响小 | 复用 E06 做法并在 WarningsAsErrors 工程 fixture 验证；属性类型显示不带 NRT 标注；必要时生成文件挂 `(void)` 级 pragma（仅在验证要求时） |
| R11 | 多目标/共享源码同一文件编入多个程序集 | E06 已有同问题与解法 | 复用 ProjectCompileSet 映射 + 按项目闭包分发；fixture 覆盖 |

---

## 11. 并行协调

| 相邻工作 | 边界 | 顺序/依赖 |
|---|---|---|
| **E06 `tuple`（已实现）** | 提供同构基建（shape registry/确定性命名/文件分发/Include+Link/ImplicitUsings 合成/FormatAndValidate）；元组与匿名可互为属性/元素 | RunAfter=tuple 已登记；只**复制/提取**事实函数，不改 E06 公共契约与其测试；交叉形状（元组含匿名、匿名属性含元组）在 M3 双 pass 组合用例验证：tuple 先把元组变 struct，C21 见到的属性类型已是 `Cs2jTuples.*` |
| **C26 `linq-channel`（stub）/ E16 嵌入式 LINQ** | C21 只改创建点；LINQ 过程式化消费具名投影。C26 的 RunAfter 已含本 pass；落地时删除 [SanitizeAnonymousTypeDisplay](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L642-L665)、`AnonymousTypeRequiresRecords` 门控（[L410-L436](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L410-L436)）、Rules 中匿名置空返回类型分支 | C21 必须先落地默认开；C26 三路分流门与本 pass 独立。E16 在主转换期运行（[ProjectPasses.cs#L164](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L164)、[L286-L351](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L286-L351)），默认关期间双轨并存（R9） |
| **C25 `dynamic-desugar`（stub）** | C21 保真 dynamic 属性但不降级；C25 未来把动态站点显式化 | 无 RunAfter 依赖；AL030 诊断类型与 C25 共享分类；C25 落地时对 `Cs2jAnonymous` 生成类上的 dynamic 属性按普通 dynamic 站点处理 |
| **转换器 synthesizer 退役（M5）** | 默认开后以下转换器代码成为死路径，单 PR 退役：①[AnonymousTypeRecordSynthesizer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/AnonymousTypeRecordSynthesizer.cs)（当前已零生产调用）；②[SynthesizedRecordStore](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/SynthesizedRecordStore.cs) 及 [ConversionContext](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L343-L349) 注册面与 [L579-L586](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L579-L586) 两个 resolver；③[TypeMappingService](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L443-L464) 匿名分支；④[ObjectCreationTransformer.NewAnonymous](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L74-L84) 的 Map.of 降级；⑤[CSharpToJavaVisitor](file:///d:/code/cs2j/src/CSharpToJava.Core/Visitors/CSharpToJavaVisitor.cs#L86-L117)/[TypeGroupResolver](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/TypeGroupResolver.cs#L264-L281) 合成 record 发射；⑥LinqRewriter 匿名门控/消毒（与 C26 协调，可延后到 C26 PR）；同步重写 ForeachAnonymousTypeTests/LinqAnonymousTypeExtractedMethodTests/LinqChainRefactoringTests 期望 | M5 在默认开 PR 之后；退役前保留默认关回退能力；⑥与 C26 同批或更早，二者评审对齐 |
| **E02 local-function-lift / E05 delegate（已实现，在本 pass 之前）** | 两者均不可能在产物中物化匿名类型名（局部函数必须显式返回类型；lambda 保持推断），无语法面争抢 | §2.4 扫描断言固化前提；不修改两 pass |
| **E10 generic / generic-deepen（已实现/opt-in）** | 生成泛型匿名类的泛型实例化、数组元素由后续 generic 通道常规处理 | 链位天然在其后（拓扑上 generic 晚于 tuple 链段）；无特殊接线 |
| **C08/E17 java-keywords（链尾）** | 生成类属性名若撞 Java 保留字（`new/class/int`…）由 C08 全符号改名表统一处理 | C21 不做 Java 关键字转义（D16）；退役现有 `intValue/classValue` 启发式后该职责唯一归 C08 |
| **C20 partial-merge（观察项）** | 生成文件非 partial；输入 partial 类型中的匿名创建按普通创建点处理 | 无交集 |

**热文件清单**（落地时预期冲突面，合并需协调）：
[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、[.gitignore](file:///d:/code/cs2j/.gitignore)、[LinqRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs)（C26/M5）、[ObjectCreationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs)、[TypeMappingService.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs)、注册表顺序断言测试。

---

## 12. Worktree 分配

只写不执行（本设计不执行任何 git 写操作；以下命令在实施 PR 中由执行者运行）：

- 分支：`feature/preproc-c21-anonymous-type`
- 目录：`.worktrees/preproc-c21-anonymous-type`（`.worktrees/` 已在 [.gitignore](file:///d:/code/cs2j/.gitignore#L77) 忽略）
- base：`953293b9`（`docs: update doc base commit`，已核实存在：`953293b908e9143437c45f287fb4277e890c398e`）

```powershell
git worktree add .worktrees/preproc-c21-anonymous-type -b feature/preproc-c21-anonymous-type 953293b9
```
