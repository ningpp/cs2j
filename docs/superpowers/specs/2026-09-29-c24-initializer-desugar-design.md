# C24 对象/集合初始化器归一（`expression-desugar` Initializers 规则）预处理器设计

**日期**: 2026-09-29
**范围**: 在 C23 锚点合入后的 `src/CSharpToJava.Core/ExpressionDesugar/` 共享族基建上新增 `Initializers/` 规则集，在 C# 层把对象初始化器（`new C { A = 1 }`，含嵌套创建/嵌套成员初始化、init 属性赋值）、集合初始化器（`new List<int> { 1, 2 }`，含多参/扩展/fluent `Add`、IEnumerable 模式）、字典索引初始化器（`new D { [k] = v }`）、target-typed `new()`（含可空目标）统一归一为「构造调用 + 逐成员 set/赋值/Add/索引赋值」的语句序列，严格保持构造实参→初始化器、成员按源码序的求值顺序与单次求值。本任务**不注册/不翻转 pass、不新增 pass id/中间目录/适配器、不改任何 C#→Java 转换器代码**
**状态**: 设计待评审；实现硬前置 C23 锚点分支合入（见 §3、§12）
**Catalog**: C24（P3，分类 1.8 表达式）[preprocessor-catalog.md L544-L553](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L544-L553)；族归属「表达式/集合形态归一」（[§3.3 L909](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L909-L909)：C23+C24+C35+C36，共用单次求值临时变量提升基建）；C36 边界 [L691-L704](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L691-L704)；横切约束（禁止静默错误输出，[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931)；表达式树共享判定，[风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927-L927)）
**注册表现状**: stub `expression-desugar`（[PassStubCatalog.cs L239-L248](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L239-L248)，catalogItems `"C23, C24, C35, C36"`，Stage=Expression，RunAfter=`null-coalesce`，目录 `.cs2j-expression-desugar-src`）。stub 翻转与族基建由 **C23 锚点**完成（翻转时收窄 catalogItems 为 `"C23"`）；C24 只在翻转后的同一 pass 内追加 `Initializers/` 规则，合入时 catalogItems 加宽为 `"C23, C24"`（该字符串属锚点侧文件，由锚点侧统一登记，见 §3）
**base**: `953293b9`（`docs: update doc base commit`，C23 设计已校验存在）；实际开工 base = C23 分支合入后的 rebase 点

---

## 1. 背景与目标

### 1.1 现状：单文件 Java 侧代偿，四处静默语义差

初始化器今天在 C#→Java 主转换器内部被就地翻译成「前置语句 + 临时变量」，唯一入口 [ObjectCreationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs)（153 行）。2026-09-29 复核，证据如下：

| # | 站点 | 现状行为 | 后果 |
|---|---|---|---|
| S1 | [L41-L55](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L41-L55) 对象初始化器分支 | 只认 `AssignmentExpressionSyntax` 且左值**必须是 `IdentifierNameSyntax`**（[L48](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L48-L48) `if (e.Left is not IdentifierNameSyntax id) continue;`）；属性/字段仅靠 `GetSymbolInfo` 名字符串二分 | 字典索引初始化器 `new D { [k] = v }`（`ImplicitElementAccess`）被**静默跳过**，赋值整体丢失；嵌套成员初始化 `Inner = { X = 1 }` 靠下游 facade 特判补偿，语义面分散 |
| S2 | 同上 | 不判定 init/readonly/可访问性：init-only 属性、get-only 属性、readonly 字段一律发 `setX(...)`/字段直写 | 对只读成员产出**不可编译 Java** 或绕过 init 约束，无诊断 |
| S3 | [L56-L65](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L56-L65) 集合初始化器分支 | **硬编码无参构造** `new {name}()`，`ObjectCreationExpressionSyntax.ArgumentList` 整体丢弃 | `new List<int>(8) { 1, 2 }` 容量实参丢失；无无参构造函数的类型直接产出不可编译 Java；custom collection 的构造实参（依赖注入、比较器）全丢 |
| S4 | [L60-L64](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L60-L64) | 纯文本启发式：嵌套两元素即 `put(k,v)`，其余即 `add(e)`；**零 SemanticModel Add 绑定** | 多参 `Add(k,v)`（非字典类型）被错发 `put`；扩展方法 `Add`、`Add` 返回非 void 的 fluent 集合、IEnumerable 模式全部未按 C# 绑定语义处理 |
| S5 | [L26-L27](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L26-L27) target-typed new | `ImplicitObjectCreationExpressionSyntax` 取 `GetTypeInfo(io).Type`，为 null 时 `name="Object"`；无显式类型发射策略 | `new() { ... }` 在类型推断缺失处产 `new Object()`，静默错译；无对应测试 |
| S6 | 求值顺序事故史 | [ObjectInitializerMethodChainTests.cs L7-L21](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ObjectInitializerMethodChainTests.cs#L7-L21) 记录 `new App { Args = args }.Run()` 曾因前置语句队列重复 transform 而**构造与 set 各发射两次**；[TernaryObjectInitializerNullGuardTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/TernaryObjectInitializerNullGuardTests.cs) 4 个用例钉住另一类缺陷：初始化器被无条件提出三元真分支，`setTipPosition(parsePoint(str))` 在 `str == null` 时仍执行 | 现路径靠 facade 特判与前置语句队列维持单次/条件求值；这正是 catalog §3.3 要求「表达式糖→显式语句 + 单次求值临时提升」的根因 |
| S7 | 无 residual 闸门 | 初始化器分支不产任何诊断；跳过即静默 | 缺成员/缺 Add/不可提升上下文全部「能转完但结果错」，属风险 12 静默错误输出族 |

另有一条 Java 半区专用护栏：[ObjectInitializerArrayToIListTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ObjectInitializerArrayToIListTests.cs)（`T[]` 赋 `IList<T>` 属性时 Java 侧包 `ArrayHelper.toList`）。该包裹是 Java 半区关注（C# 中 `T[]` 本就实现 `IList<T>`），C24 的 C# 产物保持普通赋值即可，本测试在 pass 默认关期间一行不改。

### 1.2 目标语句序列形态

预处理后的中间树满足（产物是普通、可编译、可直接在 .NET 上跑测试的 C#）：

```csharp
// 1) 对象初始化器：构造实参先行，成员严格源码序
var p = new Point(1, 2) { X = F(), Y = G() };
// →
var __ed_0 = new Point(1, 2);
__ed_0.X = F();
__ed_0.Y = G();
var p = __ed_0;

// 2) 集合初始化器：保留构造实参，Add 绑定以 SemanticModel 为准
var xs = new List<int>(8) { Next(), 2 };
// →
var __ed_0 = new List<int>(8);
__ed_0.Add(Next());
__ed_0.Add(2);
var xs = __ed_0;

new Pairs { { 1, 2 } }                       // 多参 Add → __ed_0.Add(1, 2);
new D { [K1()] = V1(), [K2()] = V2() }       // 字典索引初始化器 → __ed_0[K1()] = V1(); …（键值各求值一次、源码序）

// 3) 嵌套创建：内层随外层成员序提升
new Outer { Inner = new Inner { Deep = 5 } }
// →
var __ed_0 = new Outer();
var __ed_1 = new Inner();
__ed_1.Deep = 5;
__ed_0.Inner = __ed_1;

// 4) 嵌套成员初始化（无 new）：既有成员实例 getter 链只求值一次
new Outer { Inner = { Deep = 5 } }
// →
var __ed_0 = new Outer();
var __ed_1 = __ed_0.Inner;
__ed_1.Deep = 5;

// 5) target-typed new（含可空目标）
List<int> xs = new() { 1, 2 };               // → 同 2)，构造显式 new List<int>(…)
C? M() => new() { X = 1 };                   // → 语句序列 + return __ed_n;（目标去 NRT 注解取 C）

// 6) 条件分支内的初始化器：语句化进真分支，禁止提出守卫（S6）
var a = cond ? new C { A = F() } : null;
// →
C __ed_0;
if (cond) { var __ed_1 = new C(); __ed_1.A = F(); __ed_0 = __ed_1; }
else { __ed_0 = null; }
C a = __ed_0;
```

### 1.3 硬不变式

1. **求值顺序**：构造实参先于全部初始化器、只求值一次；成员赋值/`Add`/索引赋值严格按源码序；任何子表达式（接收者、成员访问链、字典键值、Add 多参）在产物中**单次出现**；
2. **条件位置不外提**：三元/`??`/lambda 分支等条件上下文中的初始化副作用必须留在被守卫的分支内（S6），禁止产出「能编译、条件求值错」的程序；
3. 中间源码树恒可编译（整树重编译相对基线无新增 error）；无法等价显式化即**显式诊断 + 整文件原子不改写**，禁止注释/占位/半成品透传；
4. 判定一律来自全编译 SemanticModel/IOperation（`Add` 符号、属性 init 标记、索引器 set、target-typed 目标类型），禁止按文本猜 `add`/`put`；
5. residual 口径（§4 IN-13）：输出树零未配对的对象/集合/复杂元素初始化器与零裸 `new()`；幂等（二次运行零 diff）；干净文件字节级复制（含 BOM）。

### 1.4 非目标（边界）

- **record `with` 表达式**：归 C27（[c27-design §2 决策 9/12](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c27-record-with-design.md#L87-L90)）；with 初始化器在 Declarative 阶段先归一，C24 对其产物幂等即可；
- **集合表达式 `[1,2,3]`/spread `..e`**：归 C36（[catalog L691-L704](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L691-L704)）；`CollectionExpressionSyntax` 节点 C24 不触达；
- **`stackalloc`**：归 C06；[ObjectCreationTransformer L146-L148](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L146-L148) 已是显式 throw，本规则不消费；
- **数组初始化器** `new int[] { 1, 2 }` / `new[] { 1 }`（`ArrayInitializerExpression`）：Java 有同形数组字面量，主转换器 [L85-L145](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L85-L145) 直映成立，不进本规则 residual 口径；
- **匿名对象** `new { A = 1 }`（`AnonymousObjectCreationExpressionSyntax`）：归 C21 提升；
- 不改 Java 侧转换器、compat 库、`TypeMappings.json`；不新增支撑工程（产物全是 BCL/C# 语言形态）。

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | 总体架构 | 语义驱动**逐文件**规则，挂 C23 族 spine：dirty 检查（含 `ObjectCreationExpressionSyntax.Initializer`/`ImplicitObjectCreationExpressionSyntax` 触发）→ Analyze 产 plan → 自底向上 Rewrite + 向共享 `TempLifter` 登记 → 上下文语句化（§4.5）→ 族统一 `TempLifter.Apply` 一次 → 格式化 → residual 闸门 | 无改名/签名变化、无跨文件生成、无支撑库；不覆写 `ExecuteAsync`；与 IE 规则同形 |
| 2 | 判定真相源 | 以 `SemanticModel.GetOperation(node)` 的 `IObjectCreationOperation.Initializer`（`IObjectOrCollectionInitializerOperation`）为准：成员项区分 `IPropertyReferenceOperation`（带 `SetValue`）、字段写、`IMemberInitializerOperation`（嵌套成员初始化）、集合元素 Add（C# `ICollectionElementInitializerOperation.AddMethod`/参数）、索引器属性写（带 Arguments 与 SetValue）；语法树仅用于定位与重发 | M0 探针先行（§9）：Roslyn 4.12（[Core.csproj L12](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12-L12)）下 Add/索引/嵌套成员三类 Operation 的确切外形以探针为准，禁止凭记忆写规则；S1/S4 根因即不看绑定 |
| 3 | 成员赋值序 | 构造调用语句 → 初始化器成员项**严格源码序**；属性（未放宽时保持属性写 `tmp.Prop = v`）、可写字段直写、索引器 `tmp[k] = v`、集合 `Add` 统一进入同一有序语句表，不按种类重排 | C# 编译器 lowering 即此序；成员 RHS 间若有观察依赖（共享状态/副作用）必须可见 |
| 4 | 单次求值与临时策略 | 构造实参与各成员值表达式**零复制**内联进唯一一条语句；嵌套 getter 链（决策 6）、复杂上下文（决策 11）经 `TempLifter` 提升；临时名复用族前缀 `__ed_`，由文件上下文统一发号 | 与 C23 §4.5 同一序号空间，保证未来同文件多规则协同时顺序确定 |
| 5 | 嵌套创建初始化 `P = new T {…}` | 自底向上：内层先产完整「构造+赋值」语句组并得临时，外层成员赋值引用该临时；内层语句组插在外层构造语句之后、外层该成员赋值之前（即源码序位置） | RHS 只求值一次；不内联展开（内联无法表达内层成员语句） |
| 6 | 嵌套成员初始化 `P = { … }` | 语义是「取既有成员实例并就地变异」：为成员访问链发**一个** getter 临时 `var __ed_m = __ed_0.P;`（链只求值一次），随后对 `__ed_m` 逐成员赋值；不重新给 `P` 赋值 | 对应 [ObjectInitializerMethodChainTests L116/L150](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ObjectInitializerMethodChainTests.cs#L116-L150) 现有 Java 侧 getter 链断言；C# 侧语义比现 Java 侧「内联重复 getter」更严格（getter 可有副作用） |
| 7 | **init / readonly 可写性与 C11/C27 边界** | 逐成员用符号判定：① 普通 set 属性/可写字段 → 正常赋值；② init-only 属性（`IPropertySymbol.SetMethod.IsInitOnly`）且源树仍是 init 访问器 → 构造后语句赋值在 C# 中非法（CS8852 同族约束），**CS2J0531 显式报错**，消息指明启用 member-desugar（C11 PD-3 放宽 init、PD-8 已先归一其对象初始化器，[c11 L173/L269](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L173-L269)）；③ readonly 字段/get-only 属性出现在初始化器——M0 探针确认基线即 CS0191/CS0200，防御性归 CS2J0531 并附「基线非法」说明；④ C11 已放宽/已归一的成员在 C24 输入树中不再是 init/属性，按实际符号发射（方法调用或字段写），对 C11/C27 产物**幂等** | member-desugar 属 Declarative 阶段，链序天然先于 Expression（[c27 决策 12](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c27-record-with-design.md#L90-L90)）；C24 不引入任何 required/record/init 感知，只按「运行时树实际符号」绑定；required 检查是编译期约束，C11 移除后 C24 不补校验 |
| 8 | **Add 重载 / 多参 / 扩展 / fluent / IEnumerable** | 一律取 Operation 的 `AddMethod` 绑定：① 单参 → `tmp.Add(e)`；② 复杂元素 `{ a, b }`（`ComplexElementInitializerExpression`）→ 按实参个数发 `tmp.Add(a, b)`（**不**按参数个数猜 `put`）；③ 扩展方法 Add → 保留 reduced 调用语法 `tmp.Add(a,b)` 重编译（using 仍在作用域，绑定自动复现），不手拼静态调用；④ `Add` 返回非 void（fluent）→ 表达式语句自然丢弃返回值（C# 规范允许）；⑤ 重载决议完全复用编译器结果，发射参数零改写；⑥ **AddMethod 不可解析的 IEnumerable 形态**：M0 探针钉死 Roslyn 实际允许的源码子集与 Operation 外形后按探针发射，探针不成立即 CS2J0533，不发明 helper | 消灭 S4；扩展 Add、fluent Add 在训练集真实存在（如 builder 风格集合）；dynamic 接收器归决策 13 |
| 9 | **字典索引初始化器归属** | `new D { [k] = v }`（初始化器内 `ImplicitElementAccessSyntax`）**归 C24**：发射索引赋值语句 `tmp[k] = v;`（多维索引器 `tmp[a,b]=v` 同形），键、值表达式各一次、源码序；绑定必须是可写索引器属性，否则 CS2J0531。**与 C12 划界**：C12 负责索引器**声明**去糖（`this[]`→`GetItem/SetItem`）及其使用点（含其 D13 已认领的源内索引器初始化器，[c12 D13](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c12-indexer-desugar-design.md#L68-L68)）；C24 在 Expression 阶段处理 C12 之后**残余**的索引初始化站点（BCL `Dictionary`、元数据类型、C12 未认领的源内索引器），按 C24 时树的实际绑定发射，两规则以「声明侧认领 + 阶段先后」天然互斥，不编辑 C12 代码 | 索引实参表达式节点原样保留（含 `^`/`..`，交 C31，[c12 D10](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c12-indexer-desugar-design.md#L65-L65)）；消灭 S1 的静默跳过 |
| 10 | **target-typed new 类型推断矩阵** | `ImplicitObjectCreationExpressionSyntax` 取 `GetTypeInfo(node).ConvertedType`（error type/缺失 → CS2J0535；可空注解取底层 `T`），重发为显式 `new T(args)` 再接初始化器序列。上下文：局部/using 声明 RHS（强目标，支持）；`return`/`yield return`/`throw`（返回类型为目标，支持）；实参位（形参类型为目标，支持，配合决策 11 实参交错规则）；三元/`??` 分支（另一支或整体转换类型为目标）；字段/属性初始化器（有目标类型，**但表达式位不可提升**，见决策 12）；`base(…)`/`: this(…)` 实参（构造链，v1 CS2J0537）；lambda/方法组推断目标（`Func<T>` 等，v1 CS2J0537） | 无初始化器的裸 `new()` 也显式化（residual 口径含 `ImplicitObjectCreationExpressionSyntax`）；构造实参同样零复制 |
| 11 | **表达式位语句化（上下文 lowering）** | 初始化器是「一语句序列 + 一值」，表达式位置由 `InitializerContextLowering` 把**最近外围语句**改写为块：变量声明 RHS→「临时声明+序列+原声明赋临时」；表达式语句→序列+保留尾部调用/赋值；return/throw→序列+return 临时；赋值 RHS→序列+赋值；using 声明/语句→序列+using 临时；三元分支→合成 if/else 双支定指赋值临时（两支都明确赋值，保证 C# 明确赋值分析通过，见 §1.2-6）；实参位→仅当前面实参含副作用时把前序实参一并提升为临时以保持左→右求值序（`M(F(), new(){A=G()}, H())` → `var a=F(); var t0=new T(); t0.A=G(); M(a,t0,H());`），前序全为无副作用表达式时不提升 | S6 两族回归的正面收口；语句化只改最近外围语句的形状，不改变其外层语义；标记语句（带 annotation）供 `TempLifter` 作锚点，**不改 TempLifter 签名**（§3.2） |
| 12 | **不可提升上下文** | 不存在「最近外围可写语句」且无法零临时表达的位置：字段/属性初始化器（含静态；构造函数迁移不进 v1）、特性实参、默认参数值、`const` 初始值、查询表达式子句、表达式树 lambda（受共享 `IsInExpressionTree` 策略控制，默认跳过留诊断）、`nameof`/特性等常量上下文、`lock` 语句表达式参数（基线类型约束，防御）→ CS2J0538；决策 11 中 v1 未实现的语句化形态（构造链实参、lambda 推断目标等）→ CS2J0537 | 与 C11 CS2J5119、C27 CS2J5223、C31 IR130 同口径；宁假阳显式报错，不做半成品外提；字段位高频形态（只读字典静态表）命中率由 M0 测量，构造函数迁移列 M4+ 开放问题 Q1 |
| 13 | dynamic / 绑定不可判定 | `dynamic` 接收者的初始化器/Add 无法静态绑定 → CS2J0541 显式报错（指路 C25）；error type、跨编译元数据缺失 → CS2J0533（Add）/CS2J0535（目标类型）/CS2J0531（成员）分类报错 | 不猜绑定、不静默透传 |
| 14 | default / 字面量 / 常量 | `default`/`default(T)`、常量字面量不产生创建节点，零触达；裸 `new()` 出现在默认参数/特性等常量上下文基线即 CS1736/CS0182，防御性归 CS2J0538；全常量对象创建（如 `new DateTime(...)` 常量折叠不存在于 C#）不做折叠 | 防误伤；与 C22（nullable）无语法面交集 |
| 15 | 幂等 / residual 口径 | residual 只统计三类：`ObjectInitializerExpression`、`CollectionInitializerExpression`（含 `ComplexElementInitializerExpression` 元素）、`ImplicitObjectCreationExpressionSyntax`；**不**统计 `ArrayInitializerExpression`（决策见 §1.4）；每处残余必须与显式 skip 诊断（0538/0541/0542）配对，否则 CS2J0545。产物二次运行：无触发节点则 clean 字节复制 | 按语法种类计数不按文本；输出语句序列中不保留任何初始化器语法 |

## 3. 链位与族契约

### 3.1 归属 `expression-desugar`，不新增 pass、不翻转 stub

- C24 没有独立 descriptor/adapter/中间目录/CLI 开关；pass 翻转由 C23 完成（[c23-design §3.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L174-L187)）。C24 实施时该 pass 已 Implemented、默认关。
- Stage=Expression、RunAfterIds=`[ "null-coalesce" ]`、目录 `.cs2j-expression-desugar-src` 全部沿用；规则开关只存在于族 options 子节 `InitializerOptions`。
- **catalogItems 由锚点侧统一登记**：C24 合入时由实施 PR 在 rebase 后把 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 该描述符的 catalogItems 从 `"C23"` 加宽为 `"C23, C24"`（[c23 §3.4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L189-L193) 既定约定）；不新增 pass id、不拆链、不新增中间目录。
- **本设计阶段不碰任何注册表文件**；C24 分支在 C23 合入后从含 `ExpressionDesugar/` 骨架的基线切出（§12）。

### 3.2 对 C23 锚点的接缝请求（只增不改）

1. 独占目录 `ExpressionDesugar/Initializers/`（C23 已预留占位，[c23 §4 目录树 L221](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L221-L221)）；
2. 复用：`ExpressionDesugarPreprocessor` 入口、`IExpressionDesugarRule`、`ExpressionDesugarFileContext`、共享 `ExpressionDesugarAnalyzer`（SemanticModel 包装、`IsInExpressionTree`、常量上下文、锚点搜索）、`TempLifter`（`PendingTemp`/`Collect`/`Apply`/`__ed_` 发号与碰撞避让）、族 Options/Statistics/Diagnostic/Result；
3. **唯一共享文件追加**：`ExpressionDesugarRuleSet.cs` 规则清单加一行（Initializers 在 Interpolations 之后、NullConditional/CollectionExpr 之前）；`ExpressionDesugarOptions.cs` 追加一个 nullable 子选项属性；码段常量与 partial 统计落本规则目录，不改共享文件既有内容；
4. 锚点扩展说明：C24 的语句锚点可以是 `InitializerContextLowering` 合成块内的**标记语句**（Apply 在重写后的树上搜索锚点，签名不变）；若 M1 证实需要锚点层级能力（如分支内多锚点排序），以只增 API 方式提差异给族评审，不擅自改 `PendingTemp` 形状（[c23 §4.5 冻结点](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L287-L296)）。

### 3.3 与 C36/C11 的链序建议

- **族内规则序**：Interpolations（C23）→ Initializers（C24）→ NullConditional（C35）→ CollectionExpr（C36）。C36 处理 `[1,2,3]` 时对 `List<T>` 目标**直接发射最终形态**（构造 + Add 语句序列），建议复用 C24 的 plan/emitter 助手，不先发 `new List<T>{…}` 再回流（单轮自底向上无第二轮）；两规则语法面不相交（`CollectionExpressionSyntax` vs `InitializerExpressionSyntax`）。
- **与 C11/C12（member-desugar）**：Declarative 阶段先于 Expression 阶段，无 RunAfter 边需求；C24 的 M1 验收必须同时在「原始树（C11 关）」与「C11 产物树（C11 开）」两类输入上跑（§7.4）。
- **与 C31（index-range）**：C24 原样保留索引实参中的 `^`/`..` 节点；C31 物理位在 array-method 之后，建议保持其先于/独立于 expression-desugar，互不改写对方节点。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/ExpressionDesugar/Initializers/`，命名空间 `CSharpToJava.Core.ExpressionDesugar.Initializers`。

| 文件 | 职责 |
|---|---|
| `InitializerOptions.cs` | `InitializerOptions(bool Strict = true, InitializerScope Scope = All, bool RewriteInExpressionTrees = false, InitializerUnsupportedPolicy Unsupported = Error)`；`InitializerScope { All, Object, Collection, TargetTypedNew }`（逐类开关，便于灰度）；非 strict 时 Error 类诊断降 Warning 且节点保留 |
| `InitializerModels.cs` | `InitializerPlan`（站点节点、构造类型/语法、构造实参表、`IReadOnlyList<InitializerMemberPlan>`、目标上下文分类、Outcome）；`InitializerMemberPlan` 抽象 + `PropertySetPlan`/`FieldSetPlan`/`IndexSetPlan`/`NestedCreatePlan`/`NestedMemberInitPlan`/`CollectionAddPlan`（AddMethod 符号、参数表、Add 类别）；`TargetTypeInfo`（ConvertedType、是否可空、来源上下文）；`InitializerContextKind` 枚举 |
| `InitializerBindingAnalyzer.cs` | 对每个 `ObjectCreationExpressionSyntax`/`ImplicitObjectCreationExpressionSyntax`：取 `IObjectCreationOperation` 与其 `IObjectOrCollectionInitializerOperation`，逐项分类绑定（决策 2）；target-typed 目标类型推断（决策 10）；init/readonly 可写性（决策 7）；Add 解析与 Add 类别（决策 8）；上下文分类与可提升性（决策 11/12）；产出 plan + 诊断，分析只读 |
| `ObjectInitRewriter.cs` | 对象初始化器：构造语句 + 属性/字段/索引赋值序列；嵌套创建与嵌套成员初始化（决策 5/6）；成员值零复制复用原节点 |
| `CollectionInitRewriter.cs` | 集合初始化器：保留构造实参；按 `AddMethod` 单发 Add/多参 Add/扩展 Add/fluent Add；复杂元素与嵌套集合初始化器；字典索引项也可由此文件按 plan 分发（与 ObjectInitRewriter 共享发射工具） |
| `TargetTypedNewRewriter.cs` | 裸 `new()`/带初始化器 `new() {…}`：按 `TargetTypeInfo` 重发显式 `ObjectCreationExpressionSyntax`（含类型实参、可空目标剥注解），再交同一 plan 流水线；无法推断 → CS2J0535 |
| `InitializerContextLowering.cs` | 决策 11 的语句化：识别最近外围语句形态、合成块/if-else/using/实参临时；插入带 annotation 的标记语句供 TempLifter 锚定；不可语句化位置判 CS2J0537/0538 |
| `InitializerSyntaxFacts.cs` | 语法事实：三类 residual 种类判定、初始化器项语法形状（`ImplicitElementAccess`/复杂元素）、简单名/this 白名单（零临时内联判定）、与数组初始化器/匿名对象的排除谓词 |
| `InitializerStatistics.cs` | partial 计数（§5） |
| `InitializerDiagnostics.cs` | CS2J0530–CS2J0549 常量与消息模板（§6） |
| `InitializerRule.cs` | `IExpressionDesugarRule` 实现：Id=`Initializers`，TriggerSyntax 注册 `ObjectCreationExpression`/`ImplicitObjectCreationExpression`；Analyze + CreateRewriter；贡献统计段/摘要片段 |

### 4.1 数据流

1. **Analyze（每文件一次）**：枚举触发节点 → `InitializerBindingAnalyzer` 产 plan（含成员级绑定与上下文判定）；任一 Error 诊断 → 文件 `Changed=false`（族入口语义，整文件原样）；
2. **Rewrite（自底向上一次）**：内层初始化器先改写得临时；外层按 plan 发射构造/赋值/Add 语句并向 `TempLifter` 登记；`InitializerContextLowering` 同步完成外围语句语句化（同一次树访问内，先标记后由 Apply 插入）；
3. **Apply**：族 `TempLifter.Apply(root)` 统一一次（C23 与 C24 临时合并、序确定）；
4. **格式化 + residual 扫描**：入口统一 `AdhocWorkspace` 格式化；IN-13 自检；
5. 干净文件（无触发节点）字节级复制。

### 4.2 规则全集（IN-1 ~ IN-13，含 before/after 形态）

- **IN-1 站点分类与绑定**：每个创建节点经 IOperation 分类为 对象/集合/混合 + target-typed 与否，逐项输出成员 plan；绑定不可判定即对应 Error 码，plan 不落盘。
- **IN-2 对象初始化器（基础）**：
  `var p = new C(a) { P = 1, F = 2 };` → `var __ed_0 = new C(a); __ed_0.P = 1; __ed_0.F = 2; var p = __ed_0;`（属性保持属性写、字段直写，按运行时树符号）。
- **IN-3 构造实参保留**：`new List<int>(8) { 1 }` → `var __ed_0 = new List<int>(8); __ed_0.Add(1); …`（消灭 S3；无初始化器的普通 `new C(a)` 不触达）。
- **IN-4 嵌套创建**：`new O { I = new I { D = 5 } }` → 内层构造+赋值先于外层成员赋值（决策 5 before/after）。
- **IN-5 嵌套成员初始化**：`new O { I = { D = 5 } }` → getter 临时一次 + 变异（决策 6 before/after）；多层 `A = { B = { C = 1 } }` 逐层发临时、源码序。
- **IN-6 集合 Add 序列（单参）**：`new L { e1, e2 }` → `__ed_0.Add(e1); __ed_0.Add(e2);`；元素为集合初始化表达式（嵌套集合）按 IOperation 递归。
- **IN-7 多参/扩展/fluent Add**：`new P { { 1, 2 } }` → `__ed_0.Add(1, 2);`（绑定为准，不发 `put`）；扩展 Add 保持 reduced 语法；非 void 返回作表达式语句丢弃；IEnumerable 无 Add 形态按 M0 探针子集发射或 CS2J0533。
- **IN-8 字典索引初始化器**：`new D { [k] = v, [a,b] = w }` → `__ed_0[k] = v; __ed_0[a, b] = w;`；键值各一次；C12 已认领站点不在输入树中（决策 9）。
- **IN-9 target-typed new（无初始化器）**：`T x = new();` → `T x = new T();`；`C? f() => new();` → `return new C();`；可空目标取底层类型；构造实参保留。
- **IN-10 target-typed new（带初始化器）**：IN-9 + IN-2/IN-6 序列；各上下文（局部/using/返回/实参/分支）按决策 10/11 语句化。
- **IN-11 上下文语句化**：声明 RHS / return / throw / 赋值 / using / 表达式语句 / 三元（if-else 双支定指）/ 实参交错（前序实参提升）；before/after 见 §1.2-6 与决策 11。
- **IN-12 不可提升/不可判定**：CS2J0537（v1 不支持的语句化位置）、CS2J0538（字段/特性/默认参数/常量/查询/表达式树策略外）、CS2J0531（成员不可写/init 未放宽/基线非法只读成员）、CS2J0541（dynamic）；整文件原子不动。
- **IN-13 residual 自检**：输出树三类节点（决策 15）与 skip 诊断配对；未配对 → CS2J0545；幂等断言（二次运行 Changed=false）。

### 4.3 文件所有权表

| 文件/目录 | 所有者 | C24 行为 |
|---|---|---|
| `ExpressionDesugarPreprocessor.cs` / `Shared/*` / `ExpressionDesugarRuleSet.cs` | C23 锚点 | 只读复用；RuleSet 仅追加 Initializers 一行，不改既有规则行与执行流程 |
| `ExpressionDesugarOptions.cs` / `ExpressionDesugarStatistics.cs` / `ExpressionDesugarDiagnostic.cs` | C23 锚点 | Options 追加一个 nullable 属性；统计/诊断以 partial 文件落 `Initializers/`，共享文件零改动 |
| `Interpolations/*` | C23 | 不触碰；同文件协作时只通过共享 `TempLifter` 与文件上下文间接交互 |
| `Initializers/*` | **C24 独占** | 全部新增代码（含规则注册、发射助手、上下文语句化、码常量、partial 统计）落此目录；供 C36 复用的 Add/构造发射助手以 internal 可见，不上提 Shared（开放问题 Q4） |
| `NullConditional/*` / `CollectionExpr/*` | C35 / C36 | 不触碰；C36 对 Add 发射助手的复用经族评审以 internal 引用或只增提取实现 |
| CLI `ExpressionDesugarPassAdapter.cs` | C23 锚点 | 不改骨架；统计段/options 子节经规则接缝自然汇入；catalogItems 字符串加宽属锚点侧登记面（§3.1） |
| `Transformers/**`（Java 半区） | 转换器维护者 | 本期只读核查（§8-3），一行不改；初始化器特判下掉留默认开后独立 PR |

## 5. CLI 集成

C24 **零注册表结构改动、不新增适配器/verb/开关**，全部 CLI 面由 C23 建立的 [ExpressionDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ExpressionDesugarPassAdapter.cs)（C23 新增）承载：

1. **选项透传**：族 options 增 `Initializers` 子节，经通用 `preprocess --pass expression-desugar` 与 convert-project 链到达规则；不新增 CLI 参数（需要灰度时只走 options/配置面）；
2. **统计汇入**：`InitializerStatistics` 以 partial 累加进族 `ExpressionDesugarStatistics` 与族级 `TempsLifted`；摘要行追加 initializers 段，建议形如：
   `… init: <ObjectSites> obj, <CollectionSites> coll (<AddSingle>/<AddMulti>/<AddExtension>/<AddFluent>/<EnumerableFallback>), <IndexSites> dict-index, <TargetTypedSites> target-new, <NestedCreate>/<NestedMember> nested, <ContextLowered> ctx-stmts, <Temps> temps, <Skipped> skipped, <Residual> residual`；
3. **特有计数**：ObjectInitializerSites、CollectionInitializerSites、DictionaryIndexInitializerSites、TargetTypedNewSites（裸 new 与带初始化器分计）、MemberAssignments（property/field/indexer 分计）、AddCalls（AddSingle/AddMulti/AddExtension/AddFluent/EnumerableFallback）、NestedCreateSites、NestedMemberInitSites、ContextLowered（按上下文名分桶）、SkippedByReason（按码分桶）、ResidualInitializerCount；
4. **诊断汇入**：复用族适配器选择器与 `[expression-desugar] [severity] path: message` 格式；restore/build/test 失败沿用族公共 CS2J0520，不重复定义；
5. **支撑库：无**。`SupportLibrary.None`，无注入/过滤/指纹豁免；产物仅 BCL 构造/赋值/Add/索引；
6. **指纹/目录**：沿用 pass 既有 `pass:expression-desugar=on/off` 自动令牌与 `.cs2j-expression-desugar-src`，非启用零痕迹；
7. **fixture/指纹复用**：与 C23 共用族 fixture（§7.2）与 AfterFiles 全树复扫机制（residual 配对逻辑按语法种类扩展，不改适配器骨架）。

## 6. 诊断策略

**第一原则**：无法在 C# 侧等价显式化即显式报错，禁止 S1–S5 式静默跳过/丢弃。码段 `CS2J0530–CS2J0549`（C23 §6 预留段，[c23 L346](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L346-L346)；只追加、不交叉占用；MSBuild 风格输出）：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J0530 | Info | 对象初始化器站点已展开（成员数/属性/字段分计） |
| CS2J0531 | Error（strict 默认，非 strict 降 Warning 保留） | 成员不可写：init-only 属性未经 member-desugar 放宽（消息指明启用 C11/C27 链序）、readonly 字段/get-only 属性（区分标注「基线 C# 本应 CS0191/CS0200」）、成员绑定不可解析；消息含成员名与类型全名 |
| CS2J0532 | Info | 集合初始化器站点已展开（Add 分类子计） |
| CS2J0533 | Error/Warning | 无适用实例/扩展 `Add`，IEnumerable fallback 探针形态不成立，或 Add 绑定不可解析（error type/元数据缺失）；不猜 `add`/`put` |
| CS2J0534 | Info | 字典索引初始化器 `[k]=v` 已展开为索引赋值语句 |
| CS2J0535 | Error/Warning | target-typed `new()` 目标类型不可推断（error type/无转换类型上下文） |
| CS2J0536 | Info | target-typed `new()` 已显式化为 `new T(…)`（含可空目标计数） |
| CS2J0537 | Error/Warning | 初始化器位于 v1 不支持的语句化位置（构造链实参、lambda/方法组推断目标等），整站点不改写；消息列已支持的上下文清单 |
| CS2J0538 | Error/Warning | 不可提升且无法零临时表达：字段/属性初始化器、特性实参、默认参数、常量上下文、查询子句、表达式树（按策略） |
| CS2J0539 | Info | Add 绑定分类计数（扩展/多参/fluent/非 void 返回丢弃/IEnumerable fallback 命中），verbose |
| CS2J0540 | Info | 嵌套成员初始化 getter 临时已发（链单次求值），verbose |
| CS2J0541 | Error/Warning | `dynamic` 接收器/动态 Add 不可静态判定（指路 C25，force 不解除） |
| CS2J0542 | Info | 表达式树 lambda 内初始化器按 `RewriteInExpressionTrees=false` 跳过（与 C23 CS2J0515 同策略、同共享判定） |
| CS2J0545 | Error（strict）/Warning | residual 闸门：输出残留无 skip 配对的对象/集合/复杂元素初始化器或裸 `new()` |
| CS2J0546–0549 | —— | 预留（不提前占用）；族公共 CS2J0520/0529 不重复定义 |

## 7. 测试计划

### 7.1 Core 单测矩阵

新增 `tests/CSharpToJava.ProjectTests/InitializerDesugarUnitTests.cs`（内联源码 → net10.0 `CSharpCompilation` → 族 `Preprocess` → 忽略空白文本断言 + 输出可编译 + 二次幂等；夹具含自定义多参/扩展/fluent Add 集合、init 属性、索引器类型、嵌套 DTO）：

1. **对象初始化器**：无参/带构造实参；属性/字段混排源码序；空初始化器 `new C { }`；构造实参表达式只求值一次（探针计数）；
2. **成员赋值序副作用探针（行为级，非仅文本）**：`new C { A = Next(), B = Next() }`、构造实参与成员 RHS 交叉副作用——断言调用序列与原生 C# 完全一致、每探针各 1 次；
3. **嵌套创建**：两/三层；内层构造先于外层成员赋值；内层 RHS 副作用计数；与集合初始化器互套；
4. **嵌套成员初始化**：`I = { D = 5 }` getter 只调一次（getter 内计数探针）；多层链式；getter 返回 null 时行为与原生 C# 一致（NRE 同位抛出）；
5. **集合初始化器**：`new List<int>(8){…}` 容量实参保留；单参 Add；多参 Add（非字典类型，断言**不**出现 `put`/`Add` 之外发射）；扩展方法 Add（using 静态/普通扩展两类）；fluent `T Add(...)` 返回非 void（断言返回值丢弃且程序行为一致）；无参集合元素与复杂元素混排；
6. **字典索引初始化器**：`Dictionary<int,string>` 单/多键、多维索引器、键值副作用顺序、自定义索引器；与 Add 形式混排；
7. **target-typed new**：局部声明、`C?` 可空目标、字段声明 RHS（无初始化器的裸 `new()` 可零临时→支持；带初始化器→CS2J0538）、return/yield/throw、实参位（前序实参有/无副作用两态）、using 声明、三元分支、`: this()` 实参→CS2J0537、推断失败→CS2J0535；
8. **条件守卫（S6 正面收口）**：三元真/假两支初始化、`??`、lambda 分支、嵌套三元；.NET 探针断言副作用只在命中分支发生（对应 [TernaryObjectInitializerNullGuardTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/TernaryObjectInitializerNullGuardTests) 四个场景的 C# 半区版本）；
9. **init/readonly 边界**：未启用 member-desugar 的 init 属性 → CS2J0531 且文件不改写；readonly 字段/get-only → CS2J0531；模拟 C11 产物树（手写等价输入：普通 set 方法 + 字段）→ 正常发射；
10. **不可提升/跳过族**：静态字段初始化器、实例字段初始化器、特性实参、默认参数、查询子句、表达式树（0542 跳过配对）、dynamic（0541）——每种断言原子不动与诊断码；
11. **数组/匿名零误伤**：`new int[]{1,2}`、`new[]{1}`、`new { A = 1 }`、裸数组元素 `{1,2}` 不触达（residual 口径不含数组初始化器）；
12. **工程性质**：无触发文件 Changed=false + BOM；二次运行零 diff；partial/多文件类型绑定；统计分桶正确；行号/相对路径附加正确。

### 7.2 项目级 fixture

优先并入 C23 建立的族共享解决方案（建议 `tests/SampleExpressionDesugarSolution/`，Lib+Tests+sln，Content Include 进 ProjectTests，net10.0；若 C23 最终只建了 `SampleInterpolationSolution`，则在其中增 `Initializers*` 前缀夹具，由 M0 与锚点侧确认二选一，不建第二套同构 sln）：

- Lib：DTO 群（属性/字段/init/嵌套只读成员）、自定义集合（多参 Add、扩展 Add、fluent Add、`IEnumerable` 模式探针）、字典/多维索引器类型、target-typed new 各上下文用例；
- Tests（.NET 半区行为守门）：同一断言在「原生 C#」与「预处理后中间树」上各跑一次——构造实参值、成员终值、集合内容与顺序、字典内容、嵌套 getter 计数、条件分支副作用计数、fluent 返回值丢弃行为逐项相等；
- 项目级断言：`result.Success`；中间树**不含**对象/集合初始化器与裸 `new()` 语法节点（豁免区除外）；`dotnet build -c Release` 与 `dotnet test -c Release` 退出码 0；干净工程字节复制/BOM。

### 7.3 Release build/test（实施期执行；本设计任务不执行 build/test/git 写操作）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

### 7.4 共处回归与 .NET 半区对照

- **C11/C27 产物共处**：以 member-desugar（property/records 规则）输出树为输入再跑本规则：init 已放宽、PD-8 已归一站点零重复处理、统计不重计；反向（member 关）对象/集合规则独立成立（init 站点 CS2J0531 除外）；
- **C36/C23 共处**：同一文件插值 + 初始化器混排，临时序号/锚点不冲突；C36 的 `[1,2]` 节点不被本规则触达；
- **现有 Java 侧护栏默认关不改**：[ObjectInitializerMethodChainTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ObjectInitializerMethodChainTests.cs)（单次构造、嵌套 getter 链）、[ObjectInitializerArrayToIListTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ObjectInitializerArrayToIListTests.cs)、[TernaryObjectInitializerNullGuardTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/TernaryObjectInitializerNullGuardTests.cs) 继续走直连单文件管线保持绿色；为每用例建 C# 半区正确对照，作为 Java 消费 PR 核对底稿。

### 7.5 residual 闸门与 CLI

- 扫描型测试：输出树每个三类节点属于「已展开（0530/0532/0534/0536）」或「显式跳过（0531/0533/0535/0537/0538/0541/0542）」；strict 下 CS2J0545 非零即退出非零；
- CLI：C23 合入后 `preprocess --pass expression-desugar` 时 Initializers 规则随族执行、摘要含 init 段、`--list-passes` catalogItems 显示 `C23, C24`；禁用子规则/禁用 pass 时无中间目录、无耗时；strict 报错不留半成品目录。

## 8. 默认开启验收门槛

本规则无独立 pass 默认态，随 expression-desugar 族门槛（[c23 §8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L408-L418)）开启；评审中本规则附加项：

1. §7.1 矩阵全绿；fixture 与三个测试工程 Release build/test 全绿；幂等、字节复制、residual 零初始化器闸门全绿；
2. .NET 半区与原生 C# 行为等价：求值顺序/单次求值、条件分支副作用、构造实参保留、多参/扩展/fluent Add、字典索引、嵌套 getter 全矩阵通过；
3. **Java 半区就绪核查（只核查，转换器改动另开 PR）**：去糖后语句序列经主转换产出可编译 Java——重点核查现 [ObjectCreationTransformer L41-L65](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L41-L65) 特判在输入无初始化器后不可达/不误伤；语句序列中 `Add`/索引赋值/普通属性赋值的 Java 映射齐备；S1–S5 站点在预处理输入上确认不可达；
4. csharp-sdk 画像（只读训练集）试跑：初始化器站点总量与分类分布、Add 类别比例、target-typed new 上下文分布、CS2J0531/0533/0535/0537/0538 命中清单逐条人工定性（真不可行 vs 过保守），过保守项进明确里程碑；
5. 与 C11/C27/C23/C36 启用组合下各 fixture 与 sdk 抽样工程中间树 build/test 不退化；上游 pass 统计零变化；convert-project 耗时增量可接受（clean 文件零成本、verify 可关）；
6. 不达标长期随族 opt-in，每评审周期复跑第 4/5 项。

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M0 探针 + 规则骨架（不翻转、不碰注册表） | ① Roslyn 4.12 探针：`IObjectOrCollectionInitializerOperation` 成员项分类（属性 set/字段/`IMemberInitializerOperation`/Add/索引）外形；扩展 Add/fluent Add/多参 Add 的 Operation 与重编译绑定；IEnumerable 无 Add 合法子集；init 属性 `IsInitOnly` 形状；target-typed new 各上下文 ConvertedType；② `Initializers/` 全部文件骨架 + RuleSet 一行（C23 分支合入后）+ 骨架单测 | Core ~350 行 + 探针记录 |
| M1 对象初始化器 + Add 主路径 | IN-1/2/3/6/9/12 主体：对象序列、构造实参保留、单参 Add、裸 `new()` 显式化、成员可写性（CS2J0531）、Add 不可解析（CS2J0533）、目标类型不可推断（CS2J0535）、基础上下文（声明/return/表达式语句）；Core 单测 1/2/9/11/12 | ~600 行 |
| M2 嵌套 + 字典 + 上下文语句化 | IN-4/5/8/10/11：嵌套创建/成员 getter 临时、字典索引、多参/扩展/fluent Add（CS2J0539）、三元 if-else 守卫、using/实参交错、CS2J0537/0538/0541/0542；副作用探针与条件分支行为单测；fixture 双验 | ~700 行 + fixture |
| M3 共处回归 + residual 收口 | IN-7/IN-13 打磨（IEnumerable fallback 按 M0 探针结论）；C11/C27/C23/C36 共处矩阵（§7.4）；AfterFiles residual 全树复扫；CLI/统计/幂等/BOM；Release 三工程全绿；catalogItems 加宽随合入登记 | ~300 行 |
| M4 画像评审 + 默认开证据 | sdk 全量 triage（CS2J0531/0537/0538 命中率，重点：字段初始化器静态字典表占比）；Java 半区核查（§8-3）；开放问题 Q1/Q2 拍板；随族评审默认开 | 扫描/评审为主 |
| M4+（不承诺） | 字段/属性初始化器位置的构造函数迁移（退役 CS2J0538 主类）；构造链实参/lambda 推断目标语句化（退役 CS2J0537）；Java 侧初始化器特判下掉 PR | 独立后续工作 |

M0–M4 全程族 pass `DefaultEnabled=false`；每里程碑自身 Release build/test 全绿，不留半残中间树。

## 10. 风险

| # | 风险 | 说明 | 缓解 |
|---|---|---|---|
| R1 | **求值顺序/条件外提（最高）** | 语句化位置错误会产出「能编译、行为错」；S6 已两度出事（重复发射、提出三元守卫） | 决策 11 上下文 lowering + 临时单发；§7.1-2/8 行为探针（非文本断言）；.NET 半区双跑；不可语句化即 CS2J0537/0538，宁跳过 |
| R2 | **Add 绑定错译** | 文本启发式把多参 Add 发 `put`、扩展 Add 漏 using、fluent 返回值误处理，都会静默改语义（S4） | 一切以 IOperation `AddMethod` 为准；M0 探针钉外形；多参/扩展/fluent/枚举类型专项单测；不可解析 CS2J0533 |
| R3 | **init 语句后赋值非法** | 未启用 C11 时 `tmp.set_P(v)`/`tmp.P=v` 对 init 属性在构造后不可编译 | CS2J0531 显式指路 member-desugar；M3 两类输入树共处回归；不发明反射/helper 绕过 |
| R4 | **构造实参丢失回归面** | S3 现路径丢实参但部分依赖类型恰好有无参 ctor 而「碰巧能跑」 | IN-3 统一保留实参；fixture 覆盖带参构造集合；画像比对构造签名 |
| R5 | **字段初始化器高频被拒** | 静态只读字典 `static readonly D = new() {…}` 在 sdk 高频，v1 CS2J0538 可能命中率高 | M0/M4 测命中率；报错信息给出手动改写范式（移到静态构造/构造函数）；构造函数迁移列 M4+（Q1），不因高频而偷做半成品 |
| R6 | **嵌套 getter 语义** | 现 Java 侧内联重复 getter（测试只锁文本），C# 语义要求一次；getter 有副作用/返回 null 时两路径不同 | IN-5 getter 临时；null 行为同位 NRE 探针；与现有 Java 侧文本断言的差异登记为 Java 消费 PR 输入 |
| R7 | **与 C11/C12 的阶段耦合** | member-desugar 若未把某类属性/索引器使用点改净，C24 面对混合形态 | 只按运行时 SemanticModel 绑定发射，不按语法来源；共处回归（§7.4）；站点认领靠阶段先后天然互斥；C11 跳过码（CS2J5119/MD120）与 C24 诊断各自留痕，不合并码 |
| R8 | **表达式树/dynamic** | 树内初始化器改写改变树形状；dynamic Add 无静态绑定 | 共享 `IsInExpressionTree` 跳过（0542，与 C23/C26 同口径）；dynamic CS2J0541；不猜绑定 |
| R9 | **Roslyn API 外形记忆偏差** | `ICollectionElementInitializerOperation` 在 Roslyn 4.12 的属性、IEnumerable fallback、init 标记外形可能与假设不符 | M0 探针先行（同 C23 M0 纪律），规则代码以探针结论为准 |
| R10 | **默认关双轨期分叉** | pass 开/关两套初始化器路径，画像与 Java 断言只覆盖一条 | 默认关不改既有测试；新行为全走 opt-in；§8 门槛控制默认化；Java 特判下掉放最后 |

## 11. 并行协调

| 相邻工作 | 边界 | 接口约定/合并顺序 |
|---|---|---|
| **C23 interpolation-expand（锚点，硬依赖）** | 族入口、`Shared/*`、Options/Statistics/Diagnostic、`TempLifter`、RuleSet、适配器全部 C23 所有；C24 只加 `Initializers/` 目录 + 一行规则 + 一个 options 属性 + catalogItems 加宽（锚点侧文件，统一登记） | 等 C23 分支合入后 rebase 开工（§12）；共享接口需要演进时只增不改、提族评审；热文件冲突全部追加式，落地者 rebase 取并集（[c23 §11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L451-L456)） |
| **C36 collection-expr（同一 pass，目标同构）** | `[1,2,3]`/spread 归 C36；初始化器语法归 C24；C36 的 `List<T>` 目标最终形态（构造+Add）与 IN-6 同构 | 规则序 C24 先于 C36；建议 C36 直接复用 C24 emitter/plan 助手（位于 `Initializers/` 的发射工具设为族内可见），不产生回流第二轮；目录并列、码段不交叉（053x vs 057x） |
| **C35 null-conditional（同一 pass）** | `?.`/`?[]` 归 C35；初始化器内的条件访问表达式作为普通子表达式保留 | 同享 TempLifter 序号空间；`new C { A = x?.Y }` 两规则协同时副作用序由统一发号保证；落地先后按各自节奏 |
| **C11 property / C27 records（member-desugar）** | init/required/with/属性初始化器在 Declarative 阶段先归一；C24 在 Expression 阶段只按运行时树符号处理残余一般形态，对其产物幂等，不引入 record/required 感知 | 链序天然（[c27 决策 12](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c27-record-with-design.md#L90-L90) 已把本条登记为给 C24 的前置契约）；CS2J0531 消息显式指路；M3 共处回归为硬项 |
| **C12 indexer（member-desugar）** | C12 D13 认领被去糖索引器声明的初始化站点（语句上下文）；C24 处理残余（BCL/元数据/未认领索引器）的 `[k]=v`；声明侧归 C12，使用点残余归 C24 | 阶段先后互斥；C24 原样保留索引实参（`^`/`..` 交 C31）；不编辑 `Indexers/` 代码 |
| **C31 index-range** | 索引实参内 `^`/`..` 节点保留 | 无 RunAfter 硬依赖；建议两规则各自只改自己节点种类，residual 口径互不计数 |
| **C06 unsafe-ffm / C21 anonymous-type-lift** | `stackalloc` 归 C06；匿名对象创建归 C21 | 节点种类不相交；residual 只数三类初始化器，零误伤 |
| **C08 java-keywords（链尾）** | `__ed_*` 临时名非 Java 保留字 | 撞名由 C08 改名链统一覆盖，C24 不内联逃逸 |
| 公共热文件 | `ExpressionDesugarRuleSet.cs`（一行）、Options（一个属性）、catalogItems（一个字符串）、族统计聚合 | 合并门禁 = rebase 最新主干 + 注册表校验/链序断言全绿；C24 不改任何既有规则行；本设计阶段不触碰其中任何文件 |

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c24-initializer-desugar`
- 目录：`.worktrees/preproc-c24-initializer-desugar`
- base：`953293b9`（`docs: update doc base commit`）；**实现前先合入 C23 锚点分支**，实际开工 base = C23 合入后含 `ExpressionDesugar/` 共享骨架的 rebase 点
- 创建命令（在仓库根、PowerShell 下执行；本设计任务只产出本文档，**不执行**）：

```powershell
git worktree add .worktrees/preproc-c24-initializer-desugar -b feature/preproc-c24-initializer-desugar 953293b9
```

实施约束（交实施 worktree）：仅在上述 worktree 内改码；等 C23 合入后 rebase 到含族基建的基线再开工；按 §9 里程碑逐提交；不注册/翻转任何 stub、不动 RunAfterIds、不新增 pass/适配器/中间目录/verb；转换器代码改动（§8-3）留后续 PR；交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md L108-L117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `ExpressionDesugar/Initializers/`、Core 单测 + 族共享 fixture + Release build/test + residual 零初始化器闸门 + CLI 测试、本设计与 plan 文档（`docs/superpowers/plans/2026-09-29-c24-initializer-desugar.md`）；不新建支撑工程。

---

## 附：关键开放问题（供评审拍板）

1. **字段/属性初始化器位置的构造函数迁移**：静态只读字典表等高频形态 v1 只能 CS2J0538；是否在 M4+ 做「初始化器提升进构造函数（实例/静态构造，含多 ctor 链接与 partial）」，其复杂度与 C11/C07 的构造体改写可能耦合，需 sdk 命中率数据决定排期。
2. **IEnumerable 无 Add fallback 的支持边界**：依赖 M0 探针结论——若 Roslyn 对「实现 IEnumerable 但无 Add」的集合初始化存在合法降级形态，其发射形式与 fluent 返回值的交互需在 M2 前定案；不成立则永久 CS2J0533。
3. **裸 `new()` 零临时显式化是否默认开启**：无初始化器的 target-typed new 可零风险内联 `new T()`，但会扩大改写面与画像 diff；是否与带初始化器站点同批默认开启，还是先只开初始化器相关站点，由 M4 画像数据评审。
4. **C36 复用 Initializers emitter 的可见性边界**：发射助手放在 `Initializers/` 内 internal 还是上提到 `Shared/`；若 C35 也需要语句化能力（分支锚点），`InitializerContextLowering` 是否泛化为族共享 `StatementContextLowering`，建议 M1 后按 C35/C36 实际需求只增提取。
5. **非 strict 保留策略的残留语义**：CS2J0531/0533/0535/0537/0538/0541 降 Warning 保留原节点时，下游 Java 转换仍走现有（错误）路径——保留是否应同时附「下游产物不保证可编译」标记并在摘要中单列，避免双轨期误判为已支持。
