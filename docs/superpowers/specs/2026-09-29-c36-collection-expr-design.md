# C36 C# 12 集合表达式去糖（spread 展开/按 ConvertedType 可变构造；兼新版本语法防线）预处理器设计

**日期**: 2026-09-29
**范围**: 在 C23 锚点合入后的 `src/CSharpToJava.Core/ExpressionDesugar/` 共享族基建上新增 `CollectionExpr/` 规则集，在 C# 层把集合表达式 `[1,2,3]`（`CollectionExpressionSyntax`）按 **SemanticModel/IOperation 得到的 ConvertedType** 显式构造为与目标类型同构的**可变/正确**形态：数组 `T[]` → `new T[]{…}`；`List<T>`/具体集合 → 可变 `new List<T>(){…}` 或「构造 + Add/AddRange 语句序列」；`IEnumerable<T>`/`IList<T>` 等接口目标 → 编译器规范具体类（`List<T>` 等）；`Span<T>`/`ReadOnlySpan<T>` → v1 数组形态（与 C06 划界）；带 `[CollectionBuilder]` 的类型 → v1 显式报码、M2 经 IOperation 收编；spread 元素 `..e`（`SpreadElementSyntax`）→ `AddRange`/Span `foreach` 展开，**消灭现有 `OfType<ExpressionElementSyntax>()` 静默过滤丢数据 bug**；空 `[]`、嵌套 `[[1],[2]]`、副作用顺序与 spread 单次求值一并覆盖。规则集另含 **C# 11/12 新语法防线盘点器**：接口 `static abstract/virtual` 成员（显式拒绝 + 未来 companion 展望）、`scoped`（盘点，与 C01/C06 划界）、泛型 attribute（显式拒绝）。本任务**不注册/不翻转 pass、不新增 pass id/中间目录/适配器、不改任何 C#→Java 转换器代码**
**状态**: 设计待评审；实现硬前置 C23 锚点分支合入；M1 建议等待 C24 合入以复用 Add/上下文语句化助手（见 §3、§12）
**Catalog**: C36（P3 首位——spread 丢失是数据级 bug；分类 1.8 表达式）[preprocessor-catalog.md L691-L704](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L691-L704)；C24 边界 [L544-L553](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L544-L553)；C41 数值元素边界 [L758-L769](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L758-L769)；族合并表 [§3.3 L909](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L909-L909)；静默错误输出风险 12 [L931](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931)；新语法防线风险 13 [L932](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L932-L932)
**注册表现状**: stub `expression-desugar`（[PassStubCatalog.cs L239-L248](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L239-L248)，catalogItems `"C23, C24, C35, C36"`，Stage=Expression，RunAfter=`null-coalesce`，目录 `.cs2j-expression-desugar-src`）。stub 翻转与族基建由 C23 锚点完成；C36 只在同一 pass 内追加 `CollectionExpr/` 规则，合入时 catalogItems 以 rebase 基线实际值为准加宽为含 `C36`（锚点侧文件统一登记），不新增 pass id、不拆链
**base**: `953293b9`（`docs: update doc base commit`，已 `git rev-parse --verify` 校验存在，完整 SHA `953293b908e9143437c45f287fb4277e890c398e`）；实际开工 base = C23（建议含 C24）分支合入后的 rebase 点

---

## 1. 背景与目标

### 1.1 现状：一条窄规则、两类静默错误

集合表达式今天仅在 **C#→Java 主转换器**内部被一条窄 delegate 规则处理，全仓唯一处理点 [ExpressionTransformerRegistry.cs:134-150](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs#L134-L150)。2026-09-29 复核证据：

| # | 站点 | 现状行为 | 后果 |
|---|---|---|---|
| S1 | [L142-L149](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs#L142-L149) | 仅当 `GetTypeInfo(node).ConvertedType is IArrayTypeSymbol` 发 `new T[]{…}`；**其余全部兜底 `java.util.List.of(…)`** | `List<int> l = [1,2]; l.Add(3);` 在 C# 合法且可变，Java 侧 `List.of` **不可变** → 运行时 `UnsupportedOperationException`。能编译、单测难抓，静默语义差（catalog P3 首位依据） |
| S2 | [L138-L139](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs#L138-L139) | 元素取 `coll.Elements.OfType<ExpressionElementSyntax>()`；`SpreadElementSyntax`（`..e`）**不是** `ExpressionElementSyntax`，被 OfType **静默过滤** | `[..a, x]` 中 spread 数据整体丢失，无异常、无诊断、无 TODO——**数据级 bug**；全 `src` grep `SpreadElement` 零命中，确认无任何补偿处理 |
| S3 | 全 `src` grep `CollectionExpression\|SpreadElement\|CollectionBuilder\|ExpressionElement` | 仅 2 个文件 6 行命中：窄规则本身（L135/138/139）与 E09 的 byte 判定（[UnsignedTypeSyntaxRewriter.cs:439-L449](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L439-L449)）；`CollectionBuilder` 零命中 | 无目标类型分派、无接口具体类选择、无 .NET 8 builder 模式、无 spread、无嵌套专项；E09 只在 byte 常量收窄处「认得」该语法，反证各 preprocessor 未把它当一等语法 |
| S4 | 测试面 | 仅 [ArrayTests.cs:257-270](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/ArrayTests.cs#L257-L270) 两个数组目标用例（钉「数组目标不得出 List.of」）；[LinqRewriterVariableCaptureTests.cs:11](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LinqRewriterVariableCaptureTests.cs#L11) 仅方法名含 CollectionExpression，用例本体是 LINQ 捕获，**无 spread/List 可变性/接口/嵌套/spread 任何断言** | 现有护栏只锁数组直映这一恰好正确的分支；S1/S2 完全无测试 |
| S5 | 新版本语法 | 接口 `static abstract/virtual` 成员、泛型 attribute 在 `src` **零命中**；`scoped` 仅 C01 RefOutIn 在参数重写路径剥离（[RefOutInSyntaxRewriter.cs:239-243,259-263](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L239-L263)，`ScopedRemoved` 计数；dirty 判定 [RefOutInRewriter.cs:72-77](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInRewriter.cs#L72-L77)） | catalog 四次补全 [L704](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L704-L704)：sdk json/json-codegen **已使用**接口 static abstract 成员——「本仓不用」≠「输入不用」；命中即落入转换器默认分支，无信号 |

### 1.2 目标形态

预处理后的中间树是普通、可编译、可直接在 .NET 上跑测试的 C#，且集合的**可变性、元素、顺序、求值次数**与 C# 语义一致：

```csharp
int[] arr = [1, 2, 3];                       // → int[] arr = new int[]{ 1, 2, 3 };            （零临时）
List<int> l = [1, 2]; l.Add(3);              // → List<int> l = new List<int>(){ 1, 2 }; …     （可变，Add 合法）
IEnumerable<int> e = [1, 2];                 // → IEnumerable<int> e = new List<int>(){ 1, 2 };（编译器规范具体类）
int[] c = [.. a, x];                         // → var __ed_0 = new List<int>(); __ed_0.AddRange(a);
                                             //   __ed_0.Add(x); int[] c = __ed_0.ToArray();   （spread 单次求值、保序）
List<int> n = [[1],[2]][0];                  // 嵌套：内层按自身 ConvertedType 先展开
Span<byte> s = [1, 2];                       // → Span<byte> s = new byte[]{ 1, 2 };            （v1 数组形态，CS2J0578）
int[] empty = [];                            // → int[] empty = new int[]{};
```

硬不变式：

1. **目标类型真相源**：一律 `SemanticModel.GetTypeInfo(node).ConvertedType` + `GetOperation(node)`（`ICollectionExpressionOperation`，Roslyn 4.12 已提供，包版本 [Core.csproj:12](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12-L12)），禁止按赋值左侧文本猜类型；
2. **可变性同构**：目标是可变集合（`List<T>`/`HashSet<T>`/接口规范具体类）时产物必须可变，消灭 `List.of` 不可变静默差；
3. **spread 零丢失**：每个 `SpreadElementSyntax` 必须有对应展开与诊断计数，residual 闸门兜底（CS2J0589），OfType 式过滤在新代码中明令禁止；
4. **求值顺序/单次求值**：元素与 spread 严格源码序；任何子表达式（含 spread 源）在产物中单次出现；含 spread 的语句化走 C23 `TempLifter`（`__ed_` 前缀、同一发号空间）；
5. 无法等价显式化（未知目标、非可枚举 spread、CollectionBuilder 自定义类型 v1、ref struct 元素等）即**显式诊断 + 整文件原子不改写**，禁止注释/占位/静默透传（catalog §4 风险 12）。

### 1.3 非目标（边界）

- **不改 Java 侧任何代码**：[ExpressionTransformerRegistry.cs:134-150](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs#L134-L150) 窄规则本期保留为直连单文件路径安全网，默认开后下掉排期另立 PR；
- **对象/集合初始化器、target-typed `new()`** 归 C24；本规则只消费 `CollectionExpressionSyntax`/`SpreadElementSyntax`，但复用 C24 的 Add 发射与上下文语句化助手；
- **`stackalloc`/Span 栈生命周期/ref struct 声明**归 C06（D5 已认领显式/隐式 stackalloc 统一堆化，[c06 设计](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c06-unsafe-ffm-design.md)）；本规则 Span 目标只发数组形态，不模拟栈分配；
- **`nint`/`nuint`/`Half`/`Int128` 元素**的类型归一归 C41/E09；本规则按符号发射元素表达式，不做数值降级；
- 已核实**不在本通道**：空宽容 `x!` 已透明剥离（[UnaryExpressionTransformer.cs:54](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L54-L54)）；`#nullable/#pragma` 归 E01；primary constructor 归 E08；终结器归 C39；
- 不新增支撑工程：产物仅含 BCL 数组/`List<T>`/`AddRange`/`ToArray` 与（M2）builder 调用。

## 2. 关键设计决策表

### 2.1 ★目标类型分派表（CE-TARGET）

`TargetTypeResolver` 对每个集合表达式取 ConvertedType（error type/缺失 → CE-ERR），按下表短路。接口→具体类映射是 C# 规范「better conversion target」表的镜像，**以重编译通过为最终裁决**，M0 探针逐行核实（§9），不在代码里发明表外类型。

| # | ConvertedType | v1/M2 产物形态 | 临时需求 | 诊断 |
|---|---|---|---|---|
| T1 | `T[]`/多维数组（v1 仅一维；多维 CS2J0581） | 无 spread：`new T[]{ e0, e1 }`（零临时）；含 spread：`new List<T>(){…}` 同序 Add/AddRange 后 `.ToArray()` 语句序列 | 无 / 有 | 0572 |
| T2 | `System.Span<T>` / `System.ReadOnlySpan<T>`（符号全名判定，不认文本） | v1 统一按 T1 发数组（`T[]` 对 Span 有隐式转换）；含 spread 同 T1 语句序列后赋给 Span 变量 | 同 T1 | 0578（Info，注明栈分配→堆数组，C06 收口） |
| T3 | `List<T>` 及其他**无 CollectionBuilder、有无参构造 + 可访问 Add 的具体类/struct**（`HashSet<T>`、`Collection<T>`、用户集合等） | 无 spread：集合初始化器 `new C<T>(){ e0, e1 }`（语义即构造后逐 Add，**结果可变**，零临时）；含 spread：`var __ed_n = new C<T>(); __ed_n.Add…/AddRange…;` 语句序列，引用点用 `__ed_n` | 无 / 有 | 0573 |
| T4 | 接口目标 → 规范具体类（T5 表） | 选具体类后按 T3 发射（零临时形态 `new List<T>(){…}` 本身可隐式转接口） | 同 T3 | 0574 |
| T5 | 带 `[CollectionBuilder]` 的类型（`ImmutableArray<T>`、`FrozenSet<T>`、用户 builder 类型） | **v1：CS2J0580 Error**（非 strict 降 Warning 保留）。M2：经 IOperation 取编译器绑定的 builder 工厂方法，发 `BuilderType.CreateMethod(new T[]{…})`（无 spread 零临时；含 spread 先 List 构建再 `ToArray()` 传参，数组→`ReadOnlySpan<T>` 隐式转换由编译器完成） | 无 / 有 | 0580（v1）/0573（M2） |
| T6 | 表外抽象类/接口/无法构造类型、error type、`var x = []`（基线 CS9176，防御） | 不改写 | —— | 0571/0581 |

### 2.2 接口目标 → 具体类型选择表（T4 明细，规范镜像）

| 接口目标（泛型） | 具体类型 |
|---|---|
| `IEnumerable<T>`、`ICollection<T>`、`IList<T>`、`IReadOnlyCollection<T>`、`IReadOnlyList<T>` | `List<T>` |
| `IDictionary<TKey,TValue>`、`IReadOnlyDictionary<TKey,TValue>`、对 `KeyValuePair<TKey,TValue>` 的 `IEnumerable<>`/`ICollection<>`/`IList<>`/`IReadOnly*` | `Dictionary<TKey,TValue>`（元素为键值对，Add 绑定由 IOperation/符号决议） |
| 非泛型 `System.Collections.IEnumerable`/`ICollection`/`IList` | `List<object?>`（与规范一致） |

注：选择必须与 Roslyn 对该站点的转换/绑定一致（M0 探针读取 `ICollectionExpressionOperation` 子操作核实）；表外新增接口（如未来 BCL 接口）默认 CS2J0581，不静默落到 `List.of`。

### 2.3 spread 展开决策（CE-SPREAD）

`SpreadExpander` 对每个 `..e` 先用 SemanticModel 取 spread 源的**静态类型与元素类型**（IOperation 子操作/`ConvertedType` 推断），按目标集合种类与源种类两维处置：

| 目标 | 源是数组/`IEnumerable<T>`/string（char 元素） | 源是 `Span<T>`/`ReadOnlySpan<T>`（ref struct，不实现 IEnumerable） | 源不可枚举/error type |
|---|---|---|---|
| 数组 T1（含 spread 必走 List 临时路径） | `__ed_n.AddRange(e)`（e 单次出现，枚举一次） | `foreach (var __ed_e in e) __ed_n.Add(__ed_e);`（Span 原生可 foreach；循环变量走发号器） | CS2J0576 |
| 具体集合 T3/接口 T4 | 同上 `AddRange(e)`（AddRange 绑定以符号为准；自定义集合无 AddRange 但实现 IEnumerable 时退化为同样的 `foreach` Add 循环） | 同上 foreach 展开 | CS2J0576 |
| Span T2 | 同数组路径（最终 `ToArray()`） | 双 Span 源：v1 foreach Add 到 List（不发 `MemoryMarshal`，保持 BCL-only） | CS2J0576 |
| builder T5（M2） | 并入 List 构建后一次 `ToArray()` 传 `Create` | foreach 并入同一 List | CS2J0576/0580 |

展开保序铁律：`[A(), ..B(), C()]` 的可观察顺序必须是 `A()` → `B()` 求值并枚举 → `C()`；List 构建天然满足，禁止把 spread 改写为 `Concat`/数组字面量拼接等改变求值时机或引入 LINQ 依赖的形态。spread 源为方法调用/属性访问时**内联一次即满足单次求值**，不额外提临时；仅当源表达式本身含无法内联的上下文（与 C24 实参交错同规则）才经 TempLifter 提前求值。

### 2.4 其余决策点

| 决策点 | 结论 | 依据/备注 |
|---|---|---|
| 空集合 `[]` | 数组 → `new T[]{}`；Span → 数组空态（`new T[]{}` 隐式转 Span）；具体集合/接口 → `new List<T>()`（零临时，可变）；builder 类型 v1 仍 0580，M2 发 `Create(Array.Empty<T>())`/`new T[]{}`（以探针绑定为准） | CS2J0577；空集合无求值顺序问题，字段初始化器位置也可零临时 |
| 嵌套 `[[1],[2]]` | 自底向上：内层集合表达式按**自身节点的 ConvertedType**独立分派（`int[][]` 外层数组目标下，内层 ConvertedType 为 `int[]` → T1；`List<List<int>>` 下内层为 `List<int>` → T3）；内层语句序列的锚点先于外层，顺序由统一发号保证 | CS2J0579；混维/锯齿数组、集合套数组、数组套集合各列单测 |
| 常量折叠 | **不做**：数组/集合不是常量值，全常量元素也必须是运行时新集合（`[1,2]` 每次求值产生新数组，折叠成静态单例会改变同一性与可变性） | 与 C23 IE-FOLD 的本质区别；元素自身常量性保留 |
| 元素副作用顺序 | 无 spread：数组初始化器/集合初始化器按源码序求值，与 C# 一致；含 spread：§2.3 保序铁律；嵌套：深度优先（内层先于其在外层的位置点） | 探针计数断言，非文本断言 |
| spread 单次求值 | 源表达式零复制；枚举恰好一次；不发 `foreach` 两遍或 Count+索引两遍 | 自定义可枚举探针（MoveNext/Current 计数） |
| 可写上下文 | 零临时形态（数组初始化器、`new List<T>(){…}`、接口零临时）在局部/字段/属性初始化器/实参/return/特性外的任意表达式位均可嵌入；**含 spread 的语句形态**复用 C24 `InitializerContextLowering`（声明 RHS/return/赋值/表达式语句/三元分支/实参交错），不可提升位置（字段/属性初始化器、特性、默认参数、查询子句）含 spread → CS2J0583 | 与 C24 决策 11/12 同口径；零临时形态不受限 |
| ref struct span 元素 | 元素类型是 ref struct 时只有 Span 目标合法，而 v1 数组形态无法容纳 ref struct 数组元素 → CS2J0582（指路 C06，不发明栈缓冲 helper）；普通基元/struct/引用类型 Span 目标正常走 T2 | 与 C06 D9 白名单一致 |
| 表达式树 | 基线编译器 **CS9175「An expression tree may not contain a collection expression」**（[MS 表达式树限制表](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/expression-tree-restrictions)），可编译输入中不可达；不设跳过策略，仅在共享 `IsInExpressionTree` 判定命中时防御性发 CS2J0588 + 不改写（理论不可达） | 与 C23 0515/C24 0542 的「跳过」策略不同：本语法基线即非法，无需保留语义 |
| 无目标推断 | `var x = []` 基线 CS9176；`new()`/形参无类型等推断不出 ConvertedType 的形态同样非法 | 防御性 CS2J0571，消息注明「基线 C# 本应 CS9176」 |
| `params` 实参位 | `M([1,2])`（形参 `params int[]`/`params Collection<T>`）ConvertedType 明确，走对应 T1/T3 零临时形态，正常支持 | 不单列规则 |
| 自定义 `Add`/多参 Add 的具体集合 | T3 的逐 Add 发射以 IOperation Add 绑定为准（复用 C24 绑定结论）；集合表达式只允许单参元素，字典目标元素为 `KeyValuePair`，不出现 C24 的多参复杂元素 | 不按文本猜 add/put |
| 幂等 | 产物零 `CollectionExpressionSyntax`/`SpreadElementSyntax`（豁免除外），二次运行零 diff、Changed=false | residual 按语法种类计数 |

### 2.5 ★新版本语法防线表（CE-GUARD，M3 盘点/显式拒绝）

防线规则与集合表达式同属 `CollectionExpr/` 规则集（M3 前以独立 Analyzer 提供盘点计数），**只盘点/拒绝，不在本通道实现收编**：

| 语法 | 现状证据 | 处置 | 码 |
|---|---|---|---|
| 接口 `static abstract`/`static virtual` 成员（方法/属性/事件/索引器/运算符，含 sdk json-codegen 已用形态） | `src` 零命中；Java 无对应特性（接口不能有静态抽象成员，也无静态接口多态分派） | **声明站点：strict Error 显式拒绝**，消息含成员签名与「Java 无此特性；未来 companion 模式 = 伴随类静态方法 + 单态化分派，属独立专题」；通过接口类型对 static abstract 成员的**调用站点**：M3 探针可识别则 Warning 盘点（generic math/JSON 序列化器高频），不静默 | CS2J0585 |
| `scoped` 关键字（C# 11 `scoped Span<T>` 局部/参数） | C01 RefOutIn 已在参数重写路径剥离（5 处命中，带 `ScopedRemoved` 计数）；**局部 `scoped` 等非 C01 重写形态无覆盖**（catalog [L1084](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1084-L1084) 注「scoped 无处理」） | 盘点不重写：参数形态发 Info（下游 C01 剥离，顺序上 C01 在 expression-desugar 之后，盘点时仍可见 token，仅计数不报错）；局部/其余形态 Warning 指路 C06/C01 补全；不与 C01 重复剥离 | CS2J0586 |
| 泛型 attribute（`class FooAttribute<T> : Attribute`，C# 11） | `src` 零命中；Java 注解不能带类型形参 | 声明站点 strict Error；实例化应用站点 Warning 盘点；归 C10 attribute 归一远期收编 | CS2J0587 |
| 集合表达式入表达式树（防御） | 基线 CS9175 | 理论不可达；命中即 Info + 不改写，供残损输入诊断 | CS2J0588 |

已核实**排除**：`x!`（[UnaryExpressionTransformer.cs:54](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L54-L54) 透明剥离）、`#nullable/#pragma`（E01）、primary constructor（E08，`DefaultParameterDeclarationRewriter.cs:165,178-204`）、raw string/u8（C43 词法族）、终结器（C39）——防线报告把这些列为「已认领，不计缺口」并给出来源，避免重复盘点。

## 3. 链位与族契约

### 3.1 归属 `expression-desugar`，不新增 pass、不翻转 stub

- C36 没有独立 descriptor/adapter/中间目录/CLI 开关；pass 翻转由 C23 完成，C36 实施时该 pass 已 Implemented、默认关。
- Stage=Expression、RunAfterIds=`[ "null-coalesce" ]`、目录 `.cs2j-expression-desugar-src` 全部沿用；规则开关只存在于族 options 子节 `CollectionExprOptions`。
- **catalogItems 加宽**：合入 PR 在 rebase 基线上把该描述符 catalogItems 从当时实际值加宽为含 `C36`（C24/C35 可能已先合，取并集；锚点侧 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 文件统一登记）；不新增 pass id、不拆链、不新增中间目录。
- **本设计阶段不碰任何注册表文件**。

### 3.2 族内固定规则顺序

`Interpolations`（C23）→ `Initializers`（C24）→ `NullConditional`（C35）→ `CollectionExpr`（C36）。该顺序在 C23 §4 目录树与 C24 §3.3 已登记，C36 落地时在 `ExpressionDesugarRuleSet.cs` 追加末行，不改既有规则行。互补而非回流：

- C24 只匹配 `ObjectCreationExpressionSyntax`/`ImplicitObjectCreationExpressionSyntax`，C36 只匹配 `CollectionExpressionSyntax`/`SpreadElementSyntax`，语法面不相交；
- C36 对 `List<T>` 目标**直接发最终形态**（零临时集合初始化器，或 Add 语句序列），不先发 `new List<T>{…}` 再让 C24 第二轮处理——单轮管线规则序在前的 C24 不会回访 C36 新发射的初始化器节点；
- 零临时形态刻意选择「C# 集合初始化器」而非 Add 语句序列：它本身就是 C# 可变构造、可嵌入字段等不可提升位置，且与 C24 输出同构，下游 Java 侧对 `new List<int>(){1,2}` 恰好走无参 ctor + 单参 Add 的正确窄路径。

### 3.3 对 C23/C24 的接缝请求（只增不改）

1. 独占目录 `ExpressionDesugar/CollectionExpr/`（C23 已预留占位）；
2. 复用：族入口、`IExpressionDesugarRule`、`ExpressionDesugarFileContext`、共享 `ExpressionDesugarAnalyzer`（SemanticModel 包装、`IsInExpressionTree`、锚点搜索）、`TempLifter`（`PendingTemp`/`Collect`/`Apply`/`__ed_` 发号与碰撞避让）、族 Options/Statistics/Diagnostic/Result；
3. 复用 C24（M1 硬建议先合）：Add 绑定/发射助手与 `InitializerContextLowering`——按 C24 开放问题 Q4，助手留在 `Initializers/` 内以 **internal 可见**（同一 Core 程序集，直接引用；不上提 `Shared/`），C36 是该约定的首个跨目录消费者；
4. 共享文件追加面：`ExpressionDesugarRuleSet.cs` 末行 + Options 一个 nullable 子节属性 + 码段常量/partial 统计落本规则目录；若 C24 未合，C36 M1 自带最小语句化实现并登记技术债，C24 合入时只增提取合并，不改 TempLifter 形状。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/ExpressionDesugar/CollectionExpr/`，命名空间 `CSharpToJava.Core.ExpressionDesugar.CollectionExpr`。

| 文件 | 职责 |
|---|---|
| `CollectionExprOptions.cs` | `CollectionExprOptions(bool Strict = true, CollectionExprScope Scope = All, CollectionBuilderPolicy Builder = Error, bool RewriteSpanAsArray = true, bool EnableGuard = true)`；`CollectionExprScope { All, Array, MutableCollection, Interface, Spread, Guard }`（逐类灰度）；非 strict 时 Error 类降 Warning 且节点保留 |
| `CollectionExprModels.cs` | `CollectionExprPlan`（站点节点、`TargetKind`、ConvertedType 符号、选定具体类型、元素计划表、语句化需求、Outcome）；`ElementPlan`（普通元素 / `SpreadPlan`（源表达式、源种类、元素类型符号、展开方式））；`TargetKind { Array, Span, MutableConcrete, Interface, CollectionBuilder, Unknown }`；`SpreadSourceKind { Enumerable, Array, StringChars, Span, Unknown }` |
| `TargetTypeResolver.cs` | 取 `GetTypeInfo(node).ConvertedType` 与 `GetOperation(node)`（`ICollectionExpressionOperation`）；按 §2.1/§2.2 分类：数组、Span（符号全名）、CollectionBuilder 特性检测（类型符号上的 `System.Runtime.CompilerServices.CollectionBuilderAttribute` 构造参数 = builder 类型 + 工厂方法名）、接口规范具体类、可 Add 具体类（无参 ctor + Add 可访问性，借 IOperation 绑定）、未知；嵌套内层独立解析 |
| `CollectionBuilderAnalyzer.cs` | 读 builder 特性与 IOperation 中编译器实际绑定的工厂方法符号（M2）：方法名、接收类型、形参（`ReadOnlySpan<T>`/`ReadOnlySpan<KeyValuePair<…>>`）、元素类型；v1 只产 0580 诊断与画像计数，不发射 |
| `SpreadExpander.cs` | spread 源分类（§2.3）；发射 `AddRange` 表达式语句或 `foreach (var __ed_e in src) tmp.Add(__ed_e);` 循环；源单次出现、保序；Span/string/数组/自定义可枚举探针夹具驱动；不可枚举 → 0576 |
| `CollectionExprEmitter.cs` | 零临时发射：`ArrayCreationExpression`（含初始化器）、`ObjectCreationExpression` + `CollectionInitializerExpression`（可变具体类/接口具体类）、空集合；语句化发射：List 临时声明 + Add/AddRange/foreach 序列 + 数组目标的 `ToArray()` 收尾；向 TempLifter 登记；**禁止 OfType 过滤**：元素遍历枚举 `CollectionElementSyntax` 全种类并要求每类有处置 |
| `CollectionExprRewriter.cs` | `CSharpSyntaxRewriter` 自底向上：内层先展开得表达式/临时，外层按 plan 替换；与 `InitializerContextLowering`（复用 C24 internal）协作完成表达式位语句化 |
| `NewSyntaxGuardAnalyzer.cs` | §2.5 防线：接口成员 static abstract/virtual 声明与调用站点、泛型 attribute 声明/应用、`scoped` token（区分参数形态与局部形态）盘点；产 plan 外的独立诊断流与统计；TriggerSyntax=null（每文件参与），自身有廉价 dirty 扫描 |
| `CollectionExprSyntaxFacts.cs` | 语法事实：residual 种类判定、元素语法种类穷举（`ExpressionElementSyntax`/`SpreadElementSyntax`，未来种类预留开关）、可提升位置谓词、byte 常量元素标记（与 E09 衔接，§11） |
| `CollectionExprStatistics.cs` | partial 计数（§5/§6） |
| `CollectionExprDiagnostics.cs` | CS2J0570–CS2J0589 常量与消息模板（§6） |
| `CollectionExprRule.cs` | `IExpressionDesugarRule` 实现：Id=`CollectionExpr`，TriggerSyntax 注册 `CollectionExpression`；Analyze + CreateRewriter；防线盘点由规则 Analyze 阶段同文件一次完成；贡献统计段/摘要片段 |

### 4.1 数据流

1. **Analyze（每文件一次）**：dirty 检查（含 `CollectionExpressionSyntax` 或防线语法才进入，否则 clean）→ 逐站点 `TargetTypeResolver` + `CollectionBuilderAnalyzer` + spread 源分类，产 plan；`NewSyntaxGuardAnalyzer` 同遍盘点；任一 Error → 文件 `Changed=false`，整文件原样（族入口语义）；
2. **Rewrite（自底向上一次）**：内层集合先展开；零临时形态直接替换；语句化形态登记 PendingTemp 并由上下文 lowering 改外围语句；spread foreach 循环作为语句序列一部分插入锚点；
3. **Apply**：族 `TempLifter.Apply(root)` 一次（C23/C24/C35/C36 临时合并、序确定）；
4. **格式化 + residual**：入口统一 `AdhocWorkspace` 格式化；CE-12 自检；
5. 干净文件字节级复制（含 BOM）。

### 4.2 规则全集（CE-1 ~ CE-12）

- **CE-1 站点与目标分类**：§2.1 六类 TargetKind + §2.2 接口具体类；未知 → 0571/0581；
- **CE-2 数组零临时**：无 spread 的 `T[]`/`params` 数组 → `new T[]{…}`；空 → `new T[]{}`；
- **CE-3 可变具体集合零临时**：`List<T>` 等 → `new C<T>(){…}`（保可变；空 → `new C<T>()`）；
- **CE-4 接口目标具体类**：§2.2 选择后同 CE-3；
- **CE-5 spread 展开（AddRange）**：数组/IEnumerable/string 源 → AddRange 或 foreach Add；源单次求值、保序（§2.3）；
- **CE-6 spread 展开（Span 源）**：foreach Add 循环；
- **CE-7 语句化收尾**：数组含 spread 的 List 构建 + `ToArray()`；不可提升位置 → 0583；
- **CE-8 嵌套**：内层按自身 ConvertedType 分派，锚点先于外层；
- **CE-9 Span 目标 v1**：数组形态 + 0578；ref struct 元素 → 0582；
- **CE-10 CollectionBuilder**：v1 0580；M2 IOperation 工厂方法发射；
- **CE-11 新语法防线**：0585/0586/0587/0588 盘点与拒绝；
- **CE-12 residual 自检**：输出树 `CollectionExpressionSyntax`/`SpreadElementSyntax` 与 skip 诊断配对，未配对 → 0589；幂等断言。

### 4.3 文件所有权表

| 文件/目录 | 所有者 | C36 行为 |
|---|---|---|
| `ExpressionDesugarPreprocessor.cs` / `Shared/*` / `ExpressionDesugarRuleSet.cs` | C23 锚点 | 只读复用；RuleSet 仅追加末行 |
| `Options`/`Statistics`/`Diagnostic` 共享文件 | C23 锚点 | Options 追加一个 nullable 属性；统计/诊断以 partial 落 `CollectionExpr/` |
| `Initializers/*`（Add 助手、上下文 lowering） | C24 | internal 复用，不改其文件；C24 未合时的最小自带实现登记为债（§3.3） |
| `Interpolations/*` / `NullConditional/*` | C23 / C35 | 不触碰 |
| `CollectionExpr/*` | **C36 独占** | 全部新增代码落此目录 |
| CLI 适配器 | C23 锚点 | 不改骨架；统计/防线报告经接缝汇入 |
| `Transformers/**`、E09、C01/C06 代码 | 各维护者 | 本期一行不改；衔接复核见 §11 |

## 5. CLI 集成

零注册表结构改动、不新增适配器/verb/开关，全部由 C23 建立的 `ExpressionDesugarPassAdapter` 承载：

1. **选项透传**：族 options 增 `CollectionExpr` 子节，经通用 `preprocess --pass expression-desugar` 与 convert-project 链到达；灰度只走 options；
2. **统计汇入**：partial 累加进族统计与 `TempsLifted`；摘要行追加 collection 段，建议形如：
   `… coll: <Sites> sites (array <A>/mutable <M>/iface <I>/span <S>/builder <B>/unknown <U>), spread <SpreadElt> (addrange <R>/span-foreach <F>), empty <E>, nested <N>, temps <T>, guard: static-abstract <SA>/generic-attr <GA>/scoped <SC>, skipped <Skip>, residual <Residual>`；
3. **防线盘点报告**：AfterFiles 汇总 CE-GUARD 计数，输出独立「new-syntax inventory」小节（static abstract 声明/调用、泛型 attribute、scoped 参数/局部分桶、已认领语法排除清单），无论 strict 是否拒绝都先给全量画像，支撑 catalog 四次补全要求的「EF Core 画像扫描后复核优先级」；
4. **诊断**：复用族适配器选择器与 `[expression-desugar] [severity] path: message` 格式；restore/build/test 失败沿用族公共 CS2J0520；
5. **支撑库：无**。无注入/过滤/指纹豁免；指纹沿用自动 `pass:expression-desugar=on/off` 令牌；
6. AfterFiles 全树 residual 复扫按语法种类扩展（增 `CollectionExpressionSyntax`/`SpreadElementSyntax` 两类配对），不改适配器骨架；
7. `--list-passes` catalogItems 随合入显示含 `C36`；禁用 pass/子规则时无目录无耗时；strict 报错不留半成品目录。

## 6. 诊断策略

**第一原则**：spread 丢失（S2）是数据级静默，`List.of` 不可变（S1）是语义级静默——本规则把「正确展开」或「带码失败」作为仅有的两种结局。码段 `CS2J0570–CS2J0589`（C23 §6 预留 C36 段，只追加、不交叉占用；MSBuild 风格输出）：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J0570 | Info | 集合表达式站点总数（各子类分计） |
| CS2J0571 | Error（strict）/Warning | ConvertedType 缺失/error type/无目标推断（防御镜像 CS9176） |
| CS2J0572 | Info | 数组目标展开（含/不含 spread 分计） |
| CS2J0573 | Info | 可变具体集合目标展开（`List<T>`/自定义集合；零临时/语句化分计） |
| CS2J0574 | Info | 接口目标按规范具体类展开（含字典接口分计） |
| CS2J0575 | Info | spread 元素已展开（AddRange/span-foreach/自定义枚举分计） |
| CS2J0576 | Error/Warning | spread 源静态类型不可枚举/不可判定（非 Span、非 IEnumerable、error type） |
| CS2J0577 | Info | 空集合 `[]` 展开（按目标种类分计） |
| CS2J0578 | Info | Span/ReadOnlySpan 目标以数组形态构造（栈→堆语义差提示，C06 收口） |
| CS2J0579 | Info | 嵌套集合表达式展开（深度分计） |
| CS2J0580 | Error（strict，v1）/Warning | 目标类型带 `[CollectionBuilder]`，v1 不支持；消息含 builder 类型/工厂方法名；M2 收编后转 0573 |
| CS2J0581 | Error/Warning | 表外接口/抽象类/多维数组等无已知具体构造形态 |
| CS2J0582 | Error/Warning | ref struct 元素/Span 栈形态超出 v1 数组边界（指路 C06） |
| CS2J0583 | Error/Warning | 含 spread 的集合位于不可语句化位置（字段/特性/默认参数/查询子句） |
| CS2J0584 | Info | 防线盘点汇总（verbose，与 §5 报告对应） |
| CS2J0585 | Error（声明站点，strict）/Warning（调用站点） | 接口 `static abstract`/`static virtual` 成员；消息说明 Java 无此特性与 companion 展望 |
| CS2J0586 | Info（参数形态）/Warning（局部等残余形态） | `scoped` 盘点：C01 已认领参数剥离；残余形态指路 C06/C01 |
| CS2J0587 | Error（声明）/Warning（应用站点） | 泛型 attribute（Java 注解不可泛型） |
| CS2J0588 | Info | 表达式树内集合表达式（基线 CS9175 非法，理论不可达）防御计数 |
| CS2J0589 | Error（strict）/Warning | residual 闸门：输出残留无 skip 配对的集合表达式或 spread 元素 |

统计（`CollectionExprStatistics.cs` partial）：`CollectionSites`、`ByTargetKind`（六类分桶）、`SpreadElements`（AddRange/SpanForeach/EnumerableForeach 分桶）、`EmptySites`、`NestedSites`、`SpanArraySites`、`MutableConstructSites`、`BuilderSites`、`StatementLiftedSites`、`SkippedByReason: Dictionary<code,int>`、`GuardStaticAbstractSites`（声明/调用分计）、`GuardGenericAttributeSites`、`GuardScopedSites`（参数/局部分计）、`ResidualCollectionCount`，加通用五字段与族 `TempsLifted`。

## 7. 测试计划

### 7.1 Core 单测 `tests/CSharpToJava.ProjectTests/CollectionExprDesugarUnitTests.cs`

内联源码 → net10.0 `CSharpCompilation`（Roslyn 4.12；夹具仿 [IteratorLoweringUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs) 的 `CSharpCompilation.Create` 范式）→ 族 `Preprocess` → 忽略空白文本断言 + 输出可编译断言 + 二次幂等断言。矩阵：

1. **数组**：一维 `int[]`/`string[]`/自定义 struct 元素；`params` 实参位；多维 → 0581；锯齿内层分类；字段初始化器零临时；
2. **List 可变性回归（P3 首位 bug）**：`List<int> l = [1,2]; l.Add(3);` 预处理后 .NET 运行探针断言 Count==3 且末元素 3；产物**不含** `List.of`（C# 侧断言文本无 java 包）、是可变 List；`HashSet<int>`、自定义 `Add` 集合同类；
3. **spread 多集合**：`[..a]`、`[..a, x]`、`[x, ..a]`、`[..a, ..b]`、三源交错；数组源、`List<T>`` 源、`IEnumerable<T>` 源（yield 方法）、string 源（char 元素）；
4. **spread Span 源**：`Span<int>`/`ReadOnlySpan<int>` spread 进数组/List 目标 → foreach Add 展开，运行时内容一致；
5. **spread 非可枚举**：探针造无可枚举性的类型 → CS2J0576 且整文件不改写；
6. **空集合**：数组/List/接口/Span 各目标的 `[]`；空 + spread 混合（`[..empty]`）；
7. **嵌套**：`int[][] j = [[1],[2]]`；`List<List<int>>`；`List<int[]> l = [[1,2]]`；三层；嵌套层各含 spread，语句锚点顺序与运行时内容对照；
8. **接口目标**：`IEnumerable<T>`/`ICollection<T>`/`IList<T>`/`IReadOnlyList<T>` 全部产 `List<T>` 且可变；字典接口 + `KeyValuePair` 元素 → `Dictionary<K,V>`；非泛型 `IEnumerable` → `List<object?>`；
9. **Span 目标**：`Span<byte>`/`ReadOnlySpan<byte>` → 数组形态 + 0578，运行时索引一致；ref struct 元素 → 0582；
10. **副作用顺序/单次求值（探针计数，行为级）**：`[Next(), ..Source(), Next()]` 断言调用序列、MoveNext 次数、spread 源只求值一次；嵌套深度优先；三元/赋值/lambda 内元素；
11. **上下文语句化**：局部声明/return/yield return/throw/赋值 RHS/表达式语句/三元分支/实参交错；含 spread 的字段初始化器 → 0583；无 spread 字段初始化器零临时通过；
12. **CollectionBuilder**：自定义 `[CollectionBuilder(typeof(B),"Create")]` 类型（探针定义）→ v1 0580 且不改写；非 strict Warning 保留；（M2 增补工厂方法发射与 spread 传参矩阵）
13. **无目标/防御**：`var x = []`（基线本就报错，构造 error-type 输入）→ 0571；表达式树包裹 → 0588；
14. **防线**：接口 `static abstract`/`static virtual`（方法/属性/运算符）声明 → 0585 Error；接口类型 static 抽象成员调用探针 → Warning 盘点；泛型 attribute 声明/应用 → 0587；`scoped Span<byte>` 参数 → 0586 Info、局部 → Warning；确认 `x!`/`#nullable`/primary constructor 不计缺口；
15. **零误伤/幂等**：普通数组初始化器 `new int[]{1}`、C24 初始化器、插值字符串同文件混排互不触达；无触发文件 Changed=false + BOM；二次运行零 diff；统计分桶与行号/相对路径正确。

### 7.2 项目级 fixture

并入 C23/C24 族共享解决方案（建议 `tests/SampleExpressionDesugarSolution/`；若锚点只建了 Interpolation 夹具则增 `CollectionExpr*` 前缀，不建第二套同构 sln）：

- Lib：可变集合包装器（暴露 `List<T> Append(…)` 风格 API）、自定义集合（单参 Add）、自定义 CollectionBuilder 类型（独立报错 sln 或条件编译，避免 strict 拖垮整树）、Span 源工具、嵌套集合 DTO、接口目标 API 面；
- Tests（.NET 半区行为守门）：同一断言在「原生 C#」与「预处理后中间树」各跑一次——集合**运行时可变性**（Add/Remove 不抛）、内容与顺序、Count、spread 多源拼接结果、嵌套内容、副作用探针计数逐项相等；
- 项目级断言：`result.Success`；中间树零 `CollectionExpressionSyntax`/`SpreadElementSyntax`（豁免除外）；`dotnet build -c Release` 与 `dotnet test -c Release` 退出码 0；干净工程字节复制/BOM；
- **报错 sln**：builder 类型/非可枚举 spread/static abstract 声明 strict 下 `Success=false`、对应诊断出现、退出非零且不留半成品目录。

### 7.3 Release build/test（实施期执行；本设计任务不执行 build/test/git 写操作）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

### 7.4 共处回归与运行时对照

- **运行时可变性与内容对照（S1 的正面收口）**：`List<int> l = [1,2]; l.Add(3);` 类用例必须在 .NET 半区双跑（原生/预处理）一致；Java 半区 e2e 增同形用例，钉「产物可变、Add 成功」（默认开门禁项）；
- **四象限扩展**：expression-desugar × unsigned 两 pass on/off，专项复核 **byte/ushort 常量元素**：C36 产物中 `new byte[]{128}` 仍落在 E09 [IsByteArrayInitializerElement](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L444-L449) 的 `InitializerExpressionSyntax` 父模式（cast 自动接上）；但语句化的 `tmp.Add(128)` 与 `ToArray()` 元素父节点不是初始化器，需确认 E09 是否需只增识别 Add 实参（§11），unsigned 关闭时 C# 半区仍编译通过（常量隐式转换）；
- 与 C24 共处：集合表达式 + 对象初始化器同文件，临时序号/锚点不冲突；C36 发射的集合初始化器不被 C24 回访；
- 与 LINQ/fixture：`new[] { … }.Select(…)` 式链、[LinqRewriterVariableCaptureTests.cs:11](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LinqRewriterVariableCaptureTests.cs#L11) 继续绿；集合表达式在查询实参/返回位的组合；
- **现有直连护栏默认关不改**：[ArrayTests.cs:257-270](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/ArrayTests.cs#L257-L270) 两个数组用例保持绿色（pass 默认关，输入仍含集合表达式，走窄规则）。

### 7.5 residual「零集合表达式」口径与 CLI

- 闸门：输出树每个 `CollectionExpressionSyntax`/`SpreadElementSyntax` 必须属于「已展开（0572/0573/0574/0575/0577/0578/0579）」或「显式跳过（0571/0576/0580/0581/0582/0583/0588）」之一；strict 下 0589 非零即退出非零；
- CLI：C23 合入后 `preprocess --pass expression-desugar` 时本规则随族执行、摘要含 coll 段与防线 inventory、`--list-passes` catalogItems 含 `C36`；禁用子规则/pass 时无目录无耗时；strict 报错不留半成品目录。

## 8. 默认开启验收门槛

本规则无独立 pass 默认态，随 expression-desugar 族门槛（[c23 §8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L408-L418)）开启；本规则附加项：

1. §7.1 矩阵全绿；fixture 与三个测试工程 Release build/test 全绿；幂等、字节复制、residual 双零闸门（零集合表达式/零 spread）全绿；
2. .NET 半区等价：可变性、内容/顺序、spread 多源与单次求值、副作用序列、嵌套、接口具体类选择全矩阵双跑一致；
3. **Java 半区就绪核查（只核查，转换器改动另开 PR）**：去糖产物经主转换产出可编译且运行时正确的 Java——S1 用例（List 目标 Add 不抛）e2e 全绿；窄规则 [L134-L150](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs#L134-L150) 在预处理输入上确认不可达；Add/AddRange/foreach/ToArray 的 Java 映射齐备；
4. E09 byte 常量衔接（§7.4 四象限）有结论：要么现有父模式覆盖全部新产物，要么 E09 只增识别项已合入；
5. csharp-sdk 画像试跑：集合表达式/spread 站点总量、目标种类分布、0580（CollectionBuilder）/0585（static abstract）/0583（不可提升）命中清单逐条人工定性；防线 inventory 对 json/json-codegen 等已知 static abstract 使用库给出量化报告（呼应 catalog 四次补全的 EF Core 复核要求）；
6. 与 C23/C24/C35 及 E09/C01 启用组合下 fixture 与 sdk 抽样工程 build/test 不退化；耗时增量可接受；
7. 不达标长期随族 opt-in，每评审周期复跑第 5/6 项。

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M0 探针 + 规则骨架（不翻转、不碰注册表） | ① Roslyn 4.12 探针：`ICollectionExpressionOperation` 在数组/List/接口/Span/builder 各目标下的子操作外形与 ConvertedType；CollectionBuilder 特性读取与工厂方法绑定形状；spread 源 IOperation 与元素类型；接口规范具体类逐行核实 §2.2；CS9175/CS9176 防御复现；② `CollectionExpr/` 全部文件骨架 + RuleSet 末行（C23 分支合入后）+ 骨架单测 | Core ~350 行 + 探针记录 |
| M1 数组 + List + spread（P3 首位 bug 收口） | CE-1/2/3/5/6/7/12 主体：数组零临时、可变集合初始化器、AddRange/span-foreach、语句化与 ToArray 收尾、空集合、0571/0576/0583；Core 单测 1-3/10/11/15；可变性回归 + 四象限 byte 复核；fixture 双验；Release 全绿 | ~700 行 |
| M2 接口目标 + 嵌套 + Span + builder | CE-4/8/9/10：接口具体类表、嵌套锚点、Span 数组形态（0578/0582）、CollectionBuilder 工厂方法发射（0580 退役路径转 0573）；字典接口；Core 单测 4/6/7/9/12/13；共处回归 | ~500 行 + fixture |
| M3 新语法防线 + 画像与收口 | CE-11：static abstract（0585 声明/调用）、泛型 attribute（0587）、scoped 盘点（0586）、0584/0588；AfterFiles residual 全树复扫与 inventory 报告；CLI/统计/幂等/BOM；sdk 画像 triage；Java 半区核查（§8-3）；Release 三工程全绿；catalogItems 加宽随合入登记 | ~300 行 + 扫描评审 |

M0–M3 全程族 pass `DefaultEnabled=false`；每里程碑自身 Release build/test 全绿，不留半残中间树；M0 不翻转 stub。

## 10. 风险

| # | 风险 | 说明 | 缓解 |
|---|---|---|---|
| R1 | **目标类型误判 → 可变性/类型错误（最高）** | 误把 List 目标当数组或误选不可变具体类，会复刻 S1；接口具体类选错则编译失败或运行时类型差 | 一切以 ConvertedType/IOperation 为准；规范表逐行 M0 探针；重编译门 + .NET 运行时可变性/内容双跑；未知即 0571/0581，禁止兜底 List.of |
| R2 | **spread 展开的求值/枚举偏差** | 多源交错、Span 源、自定义枚举器下，AddRange/foreach 选择或循环变量提升出错会改顺序/次数；语句化外提可能提出条件分支（同 C24 S6） | §2.3 保序铁律；复用 C24 上下文 lowering 与条件守卫结论；MoveNext/Current 计数探针；源单次出现；不可提升即 0583 |
| R3 | **CollectionBuilder 自定义语义** | builder `Create(ReadOnlySpan<T>)` 可缓存/拷贝/返回单例（如 `ImmutableArray`），仿造 List+ToArray 再调 Create 一般合语义但泛型 arity/字典 builder/自定义形参形态多 | v1 一律 0580 不猜；M2 严格按 IOperation 绑定的方法符号发射，画像白名单评审；不支持形态留 0580 |
| R4 | **Span 栈分配/生命周期语义差** | 数组形态把栈数据堆化；ref struct 元素无法数组化；下游 C06 对 stackalloc 另有统一计划 | v1 明确只发数组 + 0578 留痕；ref struct 元素 0582 指路 C06；与 C06 D5 目标形态对齐（同一堆数组形态，避免返工） |
| R5 | **E09 衔接点失效** | [UnsignedTypeSyntaxRewriter.cs:447](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L447-L448) 现显式识别集合表达式父节点；C36 后该父节点消失，Add 实参/ToArray 元素可能漏 byte cast | 四象限专项；必要时向 E09 只增「Add 实参/集合构造元素」识别，C36 不内联 UnsignedMath；unsigned 关闭时 C# 半区必绿 |
| R6 | **接口具体类表与编译器演进漂移** | 未来 BCL 接口/可空注解（`List<object?>`）变化会使镜像表过时 | 表只作初判，重编译为裁决；表外即 0581 显式报码；M0 探针记录 Roslyn 版本行为 |
| R7 | **static abstract 防线的真实命中率** | 该语法在 generic math/json-codegen 是结构性用法，严格 Error 可能使整库无法过预处理（但静默放过去产物必然不可编译/语义错） | 声明 Error/调用盘点分级；非 strict Warning 保留为显式逃生门；inventory 先行量化，companion 模式独立立项，不因高频而静默 |
| R8 | **Roslyn API 外形记忆偏差** | `ICollectionExpressionOperation` 在 4.12 为标记式接口（无专属属性，子操作承载元素），builder/spread 的确切 operation 外形须实证 | M0 探针先行（同 C23/C24 纪律），规则代码以探针结论为准 |
| R9 | **默认关双轨期分叉** | pass 开/关两套集合路径，画像与 Java 断言只覆盖窄规则 | 默认关不改既有测试；新行为全走 opt-in；Java 窄规则下掉放最后；S1/S2 在直连路径仍存在期间于文档/门禁显式登记 |
| R10 | **字段位高频 spread 被拒** | 静态只读表 `static readonly List<int> = [..Seed()];` 可能高频命中 0583 | M0/M3 测命中率；报错消息给手动改写范式（移到构造器/内建 ToArray 表达式零临时写法）；构造函数迁移同 C24 Q1，不偷做半成品 |

## 11. 并行协调

| 相邻工作 | 边界 | 接口约定/合并顺序 |
|---|---|---|
| **C23 interpolation-expand（锚点，硬依赖）** | 族入口、`Shared/*`、Options/Statistics/Diagnostic、TempLifter、RuleSet、适配器归 C23；C36 只加 `CollectionExpr/` + RuleSet 末行 + 一个 options 属性 | 等 C23 合入后 rebase 开工；共享接口只增不改；热文件冲突全部追加式，落地者 rebase 取并集 |
| **C24 initializer-desugar（M1 建议先合）** | 初始化器语法归 C24，集合表达式归 C36；C36 List 目标最终形态（构造+Add）与 IN-6 同构 | 规则序 C24 先于 C36 固定；C36 internal 复用 C24 Add 发射助手与 `InitializerContextLowering`（C24 Q4 约定的首个消费者）；不产生回流第二轮；C24 若晚合，M1 自带最小 lowering 并在合入时只增合并 |
| **C35 null-conditional（同一 pass）** | `?.` 归 C35；集合元素内的条件访问作为普通子表达式原样保留 | 共享 TempLifter 发号空间；`[..x?.Y]` 类协同时副作用序由统一发号保证 |
| **C06 unsafe-ffm（Span/stackalloc/ref struct）** | Span 目标 v1 数组形态、ref struct 元素拒绝（0582）均为 C06 D5/D9 的前导形态；C36 不发 stackalloc/指针/栈缓冲 | 两 pass 目标形态一致（堆数组 + compat Span 映射）；0578 计数供 C06 画像；C06 合入后对 C36 Span 产物幂等 |
| **E09 unsigned（Unsigned 跳过点复核）** | E09 仅在 byte 常量收窄识别集合表达式父节点（[:444-449](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L444-L449)）；C36 在链上早于 unsigned | 四象限 byte 元素 fixture 为硬项；如需 E09 只增 Add 实参识别，由 E09 侧 PR 承担，C36 不引用支撑库；无符号类型元素本身（uint[] 目标）按符号直传，不做数值改写 |
| **C41 special numeric（nint 元素）** | `nint[]`/`Span<nint>` 等元素的类型降级归 C41；C36 只保证集合构造形态 | 节点种类不相交；C41 画像中若 nint 集合高频，联合 fixture 补 `nint[] x = [..]` |
| **C01 RefOutIn（scoped 参数）** | C01 在参数重写路径剥离 `scoped`（链位在 expression-desugar 之后）；C36 防线只盘点不剥离 | 0586 参数形态 Info 不报错，避免与 C01 重复处置；局部 scoped 残余由 C06/C01 补全，防线保留 Warning |
| **C10 attribute-normalize / C26 linq-channel** | 泛型 attribute 的远期收编归 C10；集合表达式在普通 IEnumerable LINQ 中正常展开，IQueryable 表达式树基线 CS9175 不可达 | 0587 消息指路 C10；共享 `IsInExpressionTree` 口径与 C23/C24/C26 一致 |
| **C08 java-keywords（链尾）** | `__ed_*`/foreach 临时名非 Java 保留字 | 撞名由 C08 改名链覆盖 |
| **公共热文件** | `ExpressionDesugarRuleSet.cs`（末行）、Options（一个属性）、catalogItems（一个字符串）、族统计聚合 | 合并门禁 = rebase 最新主干 + 注册表校验/链序断言全绿；本设计阶段不触碰其中任何文件 |

## 12. Worktree 分配（只写不执行；本设计任务未执行该命令）

- 分支：`feature/preproc-c36-collection-expr`
- 目录：`.worktrees/preproc-c36-collection-expr`
- base：`953293b9`（`docs: update doc base commit`，已校验存在）；**实现前先合入 C23 锚点分支**（M1 开工建议同时等待 C24 合入，以兑现 Add 助手/上下文 lowering 复用；C24 晚合时按 §3.3-4 自带最小实现）
- 创建命令（在仓库根、PowerShell 下执行；本设计任务只产出本文档，**不执行**）：

```powershell
git worktree add .worktrees/preproc-c36-collection-expr -b feature/preproc-c36-collection-expr 953293b9
```

实施约束（交实施 worktree）：仅在该 worktree 内改码；C23 合入后 rebase 到含族基建的基线开工；按 §9 里程碑逐提交；不注册/翻转任何 stub、不动 RunAfterIds、不新增 pass/适配器/中间目录/verb；转换器代码改动（§8-3）留后续 PR；交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md L108-L117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `ExpressionDesugar/CollectionExpr/`、Core 单测 + 族共享 fixture + Release build/test + residual 零集合表达式/零 spread 闸门 + CLI 测试、本设计与 plan 文档（`docs/superpowers/plans/2026-09-29-c36-collection-expr.md`）；不新建支撑工程。

---

## 附：关键开放问题（供评审拍板）

1. **CollectionBuilder 类型的收编范围与白名单**：M2 按 IOperation 通用发射 builder 工厂调用，还是先做画像驱动白名单（`ImmutableArray<T>` 等 BCL 高频类型）？通用路径对字典 builder（`ReadOnlySpan<KeyValuePair<K,V>>`）与自定义形参形态的覆盖度需 M0 探针数据；v1 0580 拒绝期间用 sdk 命中率定优先级。
2. **spread 语句化对字段/属性初始化器的策略**：长期是否同 C24 Q1 做构造函数迁移（退役 0583 主类），还是提供「`[..a, x]` → `a.Append(x).ToArray()` 式零临时 BCL 改写面」（依赖 LINQ/扩展方法，可能引入与 C19 的交互）；倾向前者，需命中率支撑。
3. **static abstract 的 companion 终态**：显式拒绝是 v1 结论，但 sdk json-codegen 已结构性使用——companion 模式（伴随类 + 单态化/泛型特化分派）的可行性、与 C02 generic-deepen 的分工、是否作为独立 catalog 条目立项，需 EF Core/sdk 画像量化后单独立项，不在 C36 内承诺。
4. **C24 Add 助手复用的可见性演进**：C36 是 `Initializers/` internal 助手的首个外部消费者；若 C35 也需要语句化，是否按 C24 Q4 把公共发射/上下文 lowering 只增提取到 `Shared/`，建议 M1 后按三家实际需求评审，不提前抽象。
5. **数组目标含 spread 的构造形态**：List+`ToArray()` 会多一次 List 分配；是否对「全 spread 且源全为数组」等可证形态发 `a.Concat(b).ToArray()`（引 System.Linq）或手写 `Array.Resize`/`CopyTo` 拼接以减少分配——倾向统一 List 形态优先（分配密度不在等价性范围、且避免 LINQ 依赖与 C19 顺序耦合），画像有性能证据再议。
