# C12 Indexer 去糖预处理器设计：`this[...]` → `get_Item/set_Item`（member-desugar 族 Indexers 规则）

**日期**: 2026-09-28（2026-09-29 按 C11 落地形态修订）
**范围**: 向 member-desugar 族增加 Indexers 规则——Core `MemberDesugar/Indexers/`（声明归约 + 全调用点改写）；**不新增 pass、不新增适配器、不新增 CLI 开关、不改任何 C#→Java 转换代码**
**状态**: 设计已按 C11 合入形态修订（C11 分支 `feature/preproc-c11-property-desugar` @ `ac9bacab` 已 merge 进本分支，含 stub→Implemented 翻转、`MemberDesugar/` 骨架、`MemberDesugarPassAdapter` 与 `SampleMemberDesugarSolution` fixture）
**族锚点**: C11 Property 去糖（catalog L384-394）——C11 创建 `src/CSharpToJava.Core/MemberDesugar/` 共享基建（规则发现/plan/回滚/命名/统计/诊断模型）并翻转 member-desugar 为 Implemented；本任务在其骨架上增加 Indexers 规则目录
**分支基线**: dev-3.x `5e3e796d`（含 `67477551` 注册基线与本文档），merge C11 `ac9bacab`（merge-base 恰为 `67477551`，无冲突）

## 1. 背景与目标

### 1.1 现状失败面

索引器是 C# 的「带参数的属性」，在转换器侧同时踩中成员转换与表达式转换两条最密集的分支链：

1. **声明侧** [IndexerTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs)（约 341 行）：
   - Java 方法名靠**类型名字符串猜测**：`get` 恒定；setter 在类型名含 `Dictionary/SortedList/Map` 或实现 `Map` 时发 `put`，否则 `set`（[L46-57](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L46-L57)、[L259-L279](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L259-L279)）；
   - 显式接口实现靠 `ExplicitInterfaceSpecifier != null` + 名字匹配接口的符号兜底，partial 合并树里语法节点自身无语义信息时还要从 enclosing 类型反查（[L25-L44](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L25-L44)、[L217-L253](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L217-L253)），回归簇 `InterfaceIndexerReturnTypeTests`、`ExplicitInterface*` 即源于此；
   - 多参数索引器在**已生成的 Java 文本**上跑正则 `[a, b] → [a][b]`（[L286-L291](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L286-L291)），把「多参索引器」与「多维数组」混为一谈；
   - init-only 访问器发注释 `// C# init-only ...` 占位（[L172-L175](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L172-L175)）；`put` 契约返回旧值无法跟踪，发 `return null;` 占位（[L184-L185](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L184-L185)）；setter 为链式复合赋值统一「返回 value」（[L161-L165](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L161-L165)）；
   - 返回类型/参数类型在语义缺失时回落 `"Object"`（[L39-L44](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L39-L44)、[L70-L81](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L70-L81)）；接口 get-only 索引器不得发 setter 的特判（[L149-L154](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L149-L154)）。
2. **调用点侧** [ElementAccessTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs)（约 375 行）：array / string / StringBuilder / List / Map / 指针 / 未知类型七路分支（[L38-L118](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L38-L118)、[L120-L292](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L120-L292)）；Dictionary 读要改发 `getChecked`（[L369-L374](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L369-L374)，回归 `DictionaryIndexerReadCheckedTests`）；多参数时数组发 `expr[a][b]`、其它一律 `expr.get(a, b)`（[L283-L291](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L283-L291)），无法区分锯齿数组、多维数组与多参索引器。
3. **赋值/复合赋值侧** [AssignmentTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L60-L109)：非数组 ElementAccess 左值再做一次 Dictionary 猜测决定 `put/set`（[L62-L67](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L62-L67)）；复合赋值展开为 `set(args, get(args) op v)` 时**索引实参原样输出两次**（[L99-L109](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L99-L109)），`m[Next()] += x` 类带副作用索引会两次求值（语义错误），回归 `MultiArgIndexerCompoundAssignmentTests` 只钉住了文本形状。
4. **实验 Lowering 只证明形态、不是资产**：[LowerIndexer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerIndexer.cs#L37-L62) 在 Java2 IR 上把 `IrCSharpIndexerAccessExpression` 降为 `get/set` 调用，但按 catalog §4 风险 9（[L928](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L928)）该文件仅被 Java2 对比测试引用，本通道只借形态、不共用代码。
5. **元数据契约零处理**：全仓 Core grep 无 `get_Item`/`set_Item`/`DefaultMember`/`IndexerName` 任何命中——`[IndexerName("Foo")]` 自定义索引器名与 `[DefaultMember]` 反射契约目前完全不可见。

### 1.2 目标

在主转换之前，由 member-desugar pass 的 Indexers 规则把**源内声明的索引器**归约为普通 C# 方法，并基于 SemanticModel 全编译符号表全量改写调用点，使进入转换器的 C# 中不再存在 in-scope 的 `IndexerDeclarationSyntax` 与绑定到源内索引器的 `ElementAccessExpression`：

- 声明：`T this[P1 p1, P2 p2] { get; set; }` → `T get_Item(P1 p1, P2 p2)` + `void set_Item(P1 p1, P2 p2, T value)`；
- 调用点：`obj[a, b]` → `obj.get_Item(a, b)`；`obj[a, b] = v` → `obj.set_Item(a, b, v)`；复合/自增/`?.[]`/集合初始化器/ref 返回等形态按 §4.5 精确归约；
- 归约后中间树整树重编译相对基线零新增错误（硬不变式，与 record-readonly/java-keywords/member-desugar 属性规则相同），plan 为最小原子回退单位。

### 1.3 范围

**在范围内**：

- `this[...]` 索引器声明：单参、多参（`this[int x, int y]`，编译为签名重载的同名方法）；
- `get`/`set`/`init`/表达式体索引器、访问器差异化可访问性（`public get; private set`）、访问器表达式体；
- 显式接口实现索引器（`T IFoo.this[int i]`，含同类实现两个带索引器接口）；
- `virtual`/`abstract`/`override`/`sealed` 索引器、接口索引器声明（含 C# 8 默认接口实现 DIM 带体索引器）；
- `ref`/`ref readonly` 返回索引器（调用点按 ref 变量分类归约，见 D9）；
- 全部源内调用点：读、写、复合赋值、自增自减、`this[...]`/`base[...]`、null 条件 `x?[i]`、集合初始化器 `new C { [k] = v }`、`ref/out` 实参位置、跨文件/跨工程（同一预处理树内）用点。

**明确不在范围内（边界，详见 §11）**：

- `^`/`..`（System.Index/System.Range）表达式去糖——属 **C31 index-range**（catalog L627-639）；本规则只保证把索引实参**原样搬进方法实参**，不解释实参形态；
- 数组（含多维数组 `T[,]`、锯齿数组 `T[][]`）元素访问——属 **E12 array-method** 与转换器数组路径；语义上它们不绑定 `IPropertySymbol`，本规则天然不可见；
- BCL/第三方（外部程序集）类型的索引器——`List<T>`/`Dictionary<,>`/`string`/`Span<T>` 等的 `[]` 调用点**原样保留**，Java 侧 `get/put/getChecked/charAt/[]` 映射仍由现有转换器负责；
- partial 索引器（C# 13 `partial` 访问器）→ CS2J5138 保守跳过（对齐 C11 属性 5114）；
- `extern` 索引器与 `static abstract` 接口索引器 → CS2J5139 保守跳过（对齐 C11 5116）；
- 转换器侧 `IndexerTransformer`/`ElementAccessTransformer`/`AssignmentTransformer` 索引器分支的删除或改名——后续独立 PR（§10 R1、§9 M6），本任务不动转换器。

## 2. 关键设计决策表

| # | 决策点 | 结论 | 理由 / 备选 |
|---|---|---|---|
| D1 | **归约目标命名** | getter `get_Item`、setter `set_Item`（小写前缀 + 下划线 + 索引器元数据名），直接复用 C11 共享 [AccessorsToMethods](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/AccessorsToMethods.cs) 的 `GetterName/SetterName`；Java 半区经 C11 决策 1b 的消费改造后派生 `getItem/setItem` | **族一致性 + 天然零碰撞**：C# 编译器对索引器访问器的真实成员名即 `get_Item/set_Item`，源内存在索引器时用户不可能再声明同签名 `get_Item`（CS0082 同槽保留），去糖后该槽位确定性释放——与 C11 属性 `get_X/set_X`（已落地）完全平行，且 C11 设计「共享骨架被 C12（get_Item/set_Item）复用」条款已预留此命名。原方案 `GetItem/SetItem`（catalog L401 措辞）与族已落地命名冲突且撞 C11 决策 1 论证的 Pascal 名碰撞面（`GetType→getClass` 特例表等），**修订为 get_Item/set_Item**，catalog L401 措辞按 C11 锚点决策被取代 |
| D2 | **多参数 / 重载索引器** | 同一类型不同签名的索引器共用 `get_Item/set_Item`，按形参表形成普通 C# 重载；调用点按绑定符号生成实参，重载解析交还给 C# 编译器 | C# 元数据本身即如此（多个 `get_Item` 重载）；无需参数名拼入方法名（不采用 `GetItemByXAndY` 一类自造命名） |
| D3 | **`[IndexerName("Foo")]`** | 采用特性给定名：`get_Foo/set_Foo`；**符号 `IPropertySymbol.MetadataName` 是真相源**（默认 `"Item"`，`[IndexerName]` 反映进 MetadataName；显式接口实现的 `Name` 是 `"IFoo.Item"` 形态、不能用于命名）；该特性（`AttributeTargets.Property`）从声明上**移除**（挂到方法上是编译错误），发 CS2J5131 | 全仓零处理现状（§1.1-5）；命名表以符号为键，声明与全部用点一致 |
| D4 | **多维索引器 vs 多维数组** | 唯一闸门 = 语义模型：`ElementAccess`/`ElementBinding` 绑定到 `IPropertySymbol { IsIndexer: true }` 且符号在源内才改写；`T[,]`/`T[][]` 访问绑定 `IArrayTypeSymbol`，原样保留（E12 领地）；多参用户索引器 `m[a,b]` → `get_Item(a,b)`，绝不做 `[a][b]` 文本变换 | 消除今日「数组 vs 属性」二义（IndexerTransformer 正则、ElementAccessTransformer 两路 fallback 的根因） |
| D5 | **外部程序集索引器** | `Locations` 无源位置的索引器（List/Dictionary/string/Span/COM…）**声明与用点均不动**；用点计数 `ExternalIndexerSites`（Info，仅 verbose） | 这些类型的 Java 映射形态不同（`[]`/`get`/`put`/`getChecked`/`charAt`），C# 侧方法化反而锁死映射；与 JK-4c「外部符号不改名」同原则 |
| D6 | **显式接口实现** | 声明保留限定：`T IFoo.get_Item(P p)` / `void IFoo.set_Item(P p, T value)`；接口在源内时接口声明同步归约为同签名方法；用点按绑定符号生成——接收者静态类型不是该接口时插入最小限定强转 `((IFoo)receiver).get_Item(...)`；同类显式实现 `IFoo` 与 `IBar` 两个索引器时得到两个限定名方法，天然不碰撞 | 复刻 C# 显式实现的私有+接口分派语义；今日转换器靠字符串猜接口名（IndexerTransformer L246-L253），改由符号表终结；与 C11 决策 8（`T IFoo.get_X()`）同构 |
| D7 | **虚/抽象/覆写** | 修饰符整体平移到生成方法：`virtual`/`override`/`sealed override`/`abstract`（抽象与接口方法无体）；访问器级修饰符各自落到 get/set 方法（复用 C11 `AccessorsToMethods.BuildMethodModifiers`）；struct `readonly` 索引器的 `readonly` 修饰符**丢弃**（纯编译期不变异保证，丢弃只放宽不改变行为，C11 同策略）；协变返回索引器在 C# 中**不存在**（C# 9 协变返回仅限方法，索引器覆写必须精确匹配返回类型），无需处理 | 普通 C# 实例方法的虚分派语义与索引器一致；struct 索引器的 setter 变异 `this` 语义在普通实例方法上原样成立；C# 无 static 索引器，无该分支 |
| D8 | **init-only 访问器** | 归约为普通 `set_Item`（丢失编译期 init 限定），发 CS2J5132；与 C11 属性 init 决策**逐字一致**（C11 5120 InitWidened 同构） | 中间树目标是可编译 C# 而非保留 init 契约；转换器现状本就是注释占位 |
| D9 | **ref 返回索引器** | get 方法签名保留 `ref T get_Item(...)` / `ref readonly T get_Item(...)`；ref 变量位置（`ref var x = ref obj[i]`、`obj[i] = v`、`obj[i]++`、`M(ref/out obj[i])`）归约后 C# 仍合法——调用 ref 返回方法的结果在 C# 中是变量；**ref 返回索引器的简单赋值/复合/自增一律改写为对 getter 变量的直接操作**（`obj.get_Item(i) = v` / `obj.get_Item(i) += v` / `obj.get_Item(i)++`，C# 绑定器本就经 getter 变量分派而不走 setter）；普通（值）索引器的写/复合走 setter 展开（IX-7/8） | 不引入 ByRef holder；Java 侧 ref 缺口是 C01 的既有领地，本规则不重复建设；与 C11 属性「ref 返回整体跳过」（5115）不同——索引器必须保留 ref 才能改写 `ref var x = ref c[i]` 类用点，且 ref 索引器远比 ref 属性常见（Span/Slice 型 API） |
| D10 | **调用点全量改写** | 凡绑定到源内索引器符号的 `ElementAccessExpression`/`ElementBindingExpressionSyntax`（null 条件访问内的元素绑定；含 `ThisExpression`/`BaseExpression` 接收者、嵌套、条件访问、初始化器、参数）一律改写；索引实参表达式**逐字保留**（含 `^`/`..`，留给 C31） | 改名/改签名通道必须基于全编译符号表（catalog L896 横切约束）；禁止文本替换 |
| D11 | **复合赋值/自增的求值次数** | 值索引器 `t[k] op= v` → `t.set_Item(k, t.get_Item(k) op v)`；索引实参为「简单表达式」（无副作用白名单：字面量/局部/参数/字段访问）可直接复用文本；否则在可插入语句上下文提升一次临时变量（`__cs2j_` 前缀，复用 C11 `MemberDesugarSyntaxFacts.TempName`）；表达式上下文无法提升 → CS2J5137 跳过该 plan。`++/--` 同构并保留旧值/新值语义（结果被消费时提升旧值/新值临时变量） | 修复今日双求值（AssignmentTransformer L99-L109）；与表达式族「单次求值临时变量提升」基建（catalog L909、C11 属性复合展开）对齐 |
| D12 | **null 条件访问** | `r?[i]` → `r?.get_Item(i)`（`ConditionalAccessExpression.WhenNotNull` 内的 `ElementBindingExpressionSyntax` 改写为 `MemberBindingExpressionSyntax` 条件调用）；`r?[i] = v` 在 C# 中本就非法，无需处理；链式 `a?[i][j]` 按语法嵌套逐层处理（内层普通 `ElementAccess`，接收者为条件访问结果） | 方法化后条件访问语义由 C# 编译器直接承担 |
| D13 | **集合初始化器** | `new C { [k] = v }`（initializer 内 `ImplicitElementAccess`，绑定源内索引器 setter）在语句上下文展开为 `var __cs2j_tmp0 = new C(); __cs2j_tmp0.set_Item(k, v);` 再替换使用点（复用 C11 对象初始化器提升与语句拼接基建）；字段/特性等不可提升上下文 → CS2J5137 跳过 plan | 与 C11 属性初始化器提升（5121/NonLiftable 同构）同模式 |
| D14 | **nameof / 反射 / DefaultMember** | C# 语法上 `nameof` 无法指向索引器（无成员名访问路径），无 nameof 改写项；`[IndexerName]` 按 D3 移除；类型级 `[DefaultMember("Item")]`（VB Default 或手写）保留不动并发 CS2J5133（反射消费者 `GetProperty("Item")` 不再成立——属 catalog §4 风险 7（[L926](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926)）的同族文档化限制，不保证运行时反射一致） | 沿用 E17/JK 的反射限制声明与 overrides 机制，不发明运行时桥 |
| D15 | **表达式树 / dynamic** | 用点位于 `Expression<TDelegate>` lambda 内：该索引器 plan 整体 Infeasible（CS2J5135，默认跳过，overrides.force 可解除该项）；`dynamic` 接收者/绑定不可判定：CS2J5136 跳过（force 不解除）。**直接复用 C11 已落地的** `MemberDesugarSyntaxFacts.IsInsideExpressionTree/IsDynamic`，不自造 | catalog §4 风险 8（[L927](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927)）；判定工具经 C11 单测守门 |
| D16 | **命名碰撞** | 采用 C# 规范访问器名后**同签名碰撞在合法输入中不可能发生**（源内索引器存在时同签名 `get_Item` 触发 CS0082；不同签名用户 `get_Item` 与生成方法构成合法重载；显式接口限定名独立槽位）；残余防御：生成名与既有成员意外同签名 → CS2J5140 跳过 plan（理论上不可达） | C11 决策 1 的「天然零碰撞」论证直接继承；不做即兴 per-site 改名、不追加序号（原 `GetItem2` 方案随 D1 修订作废——它以 Pascal 名高碰撞率为前提） |
| D17 | **plan 粒度** | 一个源内索引器符号一个 plan（接口索引器与其各实现为**独立 plan**，用点按实际绑定符号归属）；TouchedFiles = 声明文件 ∪ 全部用点文件；族级回滚驱动由 C11 `MemberDesugarPreprocessor.TryAttributeDiagnostic` + `MemberDesugarPassAdapter` 整树循环承担，Indexers 规则只提供 plan/OwnsTouch。已知限制：同类型同名重载索引器（`this[int]`+`this[string]`）的生成方法名相同，生成体内新增错误时按 (文件,类型,成员名) 归属会命中两个 plan → 不可唯一归属 → 族级整树回退（CS2J5100，保守但正确），v1 接受、签名级归属列为后续改进 | 属性 plan 与索引器 plan 相互独立，可分别回退；整树回退输出仍是合法原文，无半残树 |
| D18 | **产物 Java 命名变化** | 预处理后路径上，源内索引器的 Java 产物将从 `get/set/put` 变为 `getItem/setItem`（`get_Item` 经 C11 决策 1b 的 `get_/set_` → `getX/setX` 派生改造，与属性同路径）；这是有意的形态变更，其与 compat 库/既有 Java 调用方的对接由转换器侧收口 PR（M6）与 §8 门槛钉住；pass 落地即默认关 | 与 C05/JK/C11「新通道先 opt-in、门槛满足再默认开」（catalog §4 风险 10，[L929](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L929)）一致 |

## 3. 链位与族契约

### 3.1 归属既有 pass：`member-desugar`

C11 已在本分支内完成 stub→Implemented 翻转，现登记于 [PassCatalog.cs L217-L228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L217-L228)（dev-3.x 侧 stub 条目 [PassStubCatalog.cs L191-L200](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L191-L200) 已随翻转删除）：

| 字段 | 值 |
|---|---|
| Id | `member-desugar`（`PassIds.MemberDesugar`） |
| DisplayName / 目录 | "Member desugaring" / `.cs2j-member-desugar-src` |
| Stage | `PassStage.Declarative` |
| Status / DefaultEnabled | `Implemented`（C11 已翻转）/ `false`（显式启用） |
| RunAfterIds | `{ "struct-value-semantics", "closure-lift" }` |
| VerifyBuild/VerifyTestsByDefault | `true` / `true` |
| CatalogItems | `"C11"`（C12 合入后可扩为 `"C11, C12"`，属描述性元数据） |

**本任务（C12）的链位承诺**：

1. **不新增 pass**：不注册新 id、不新增 `PassDescriptor`、不动 `RunAfterIds`；Indexers 随 member-desugar 的启用而启用、随其位置而行事。
2. **不动 stub/翻转**：翻转已由 C11 完成；C12 对 `PassStubCatalog.cs`/`PassCatalog.cs` 的唯一可选改动是 `CatalogItems`/`Description` 描述性更新（一行，非行为）。
3. Stage 固定 `Declarative`（成员声明 + 跨文件用点归约），与登记一致；`NeedsSemanticModel=true`（全编译符号表是 D4/D5/D6 判定的前提）；`SupportLibrary=None`；`VerifyBuildByDefault/VerifyTestsByDefault` 沿用 C11 设定的 `true`。
4. 中间目录 `.cs2j-member-desugar-src`、preserve 目录注册、指纹令牌（`pass:member-desugar=on/off`）、`--enable-pass/--disable-pass/--list-passes` 全部复用，零新增。

### 3.2 对 C11 锚点的依赖与文件所有权

**首选路径已成立**：C11 分支（`feature/preproc-c11-property-desugar` @ `ac9bacab`）已 merge 进本分支，C12 在已落地的 `MemberDesugar/` 骨架上直接添加 `Indexers/`。

规则接缝（Q1 已按 C11 落地形态定案）：

- 规则实现 `IMemberDesugarRule { MemberDesugarKind Kind; int Order; void Analyze(MemberDesugarAnalysisContext, MemberDesugarPlanBuilder); }`；
- **发现机制 = 程序集扫描**（`MemberDesugarRuleDiscovery.Discover()` 收集带 `[MemberDesugarRule(kind, order)]` 特性、有无参构造的具体类）——C12 **零注册代码**：`Indexers/IndexerRule.cs` 标注 `[MemberDesugarRule(MemberDesugarKind.Indexer, 20)]` 即被自动收集（`MemberDesugarKind.Indexer` 槽位 C11 已预留，Order 10 已被 Property 占用，Indexers 取 20，C13/C14 取 30/40）；
- 共享基建（C11 已交付）：`MemberDesugarAnalyzer`（整编译一次分析 + 语义模型缓存；站点枚举由各规则自扫）、`MemberDesugarPlan` 基类 + `MemberDesugarPlanBuilder`（确定性排序）、`MemberDesugarPreprocessor`（`RewriteFile`/`BindCompilation`/`ExcludePlans`/`TryAttributeDiagnostic`/`FormatAndValidate`）、`MemberDesugarSyntaxFacts`（RewriteAnnotation/表达式树/dynamic 判定/TempName）、`AccessorsToMethods`（访问器命名 + 修饰符平移）、`BackingFieldNaming`（确定性命名防撞服务）、`MemberDesugarPassAdapter`（族级整树迭代回滚 + restore/verify/清理尾段）。

**post-merge 追加式共享文件编辑清单**（C12 合入 C11 后允许的全部共享文件改动，全部为「追加」不改既有行为；C11 的码位注释本身即预留「5130-5189 为 C12/C13/C14」）：

| 共享文件（C11 所有） | C12 追加内容 |
|---|---|
| `src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs` | 诊断枚举追加 5130-5141 索引器码位（§6）；`MemberDesugarStatistics` 追加索引器计数器（§5.3） |
| `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs` | `RewriteFile` 内追加 IndexerPlan 派发：与属性 rewriter 链式先后执行、「该文件无任何 planned plan」判定改为两族联合 |
| `src/CSharpToJava.CLI/Preprocessing/Adapters/MemberDesugarPassAdapter.cs` | `BuildChainSummaryLine()` 追加索引器分项与 ResidualIndexer 计数（纯展示） |
| `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs` | （可选）`CatalogItems`/`Description` 描述性更新 |

**C12 独占新增**（全部落在 `src/CSharpToJava.Core/MemberDesugar/Indexers/` 与带 Indexer 后缀的测试/fixture 文件，见 §4.1/§7）：不在 `MemberDesugar/` 根共享文件与 `Properties/` 内写索引器特例。

### 3.3 硬不变式

1. 中间源码树在预处理后永远可编译（整树重编译相对基线无新增 error；警告不比对）。
2. 每个 plan 的转换原子：全转或全不转；回退单位是单个索引器 plan，不影响同文件的属性/事件 plan。
3. 幂等：对已归约树再跑一次 member-desugar，Indexers 规则零改动（声明侧已无 `IndexerDeclarationSyntax`；用点侧只认属性符号绑定）。

## 4. Core 组件设计

### 4.1 新文件清单（全部在 `src/CSharpToJava.Core/MemberDesugar/Indexers/`）

命名空间 `CSharpToJava.Core.MemberDesugar.Indexers`，镜像 C11 `Properties/` 的文件职责划分：

```
IndexerRule.cs                 # [MemberDesugarRule(MemberDesugarKind.Indexer, 20)] IMemberDesugarRule 实现：
                               #   声明收集+声明级闸门（partial/extern/static abstract → 5138/5139）、
                               #   站点扫描分类（读/写/复合/自增/null 条件/初始化器/ref|out/表达式树/dynamic）、
                               #   IndexerPlan 产出
IndexerPlan.cs                 # IndexerPlan : MemberDesugarPlan：
                               #   Indexer(IPropertySymbol)/MetadataName/GetterMethodName/SetterMethodName/
                               #   IsExplicitImplementation/InterfaceSymbol/RefKind/HasSetter/HasInitAccessor/
                               #   TouchedFiles/Outcome/FailCode/FailReason/OwnsTouch/BuildId
IndexerFacts.cs                # 纯判定：IndexerName 特性提取、init/表达式体访问器、
                               #   简单实参白名单（D11）、外部符号判定、ref 返回判定
IndexerDiagnostics.cs          # 5130-5141 消息文案（镜像 PropertyDiagnostics）
IndexerDeclarationRewriter.cs  # IndexerDeclarationExpander：IndexerDeclarationSyntax → 1~2 个
                               #   MethodDeclarationSyntax（修饰符经 AccessorsToMethods、特性路由、
                               #   显式接口限定、ref 签名、表达式体）
IndexerUseSiteRewriter.cs      # IndexerUseSiteRewriter : CSharpSyntaxRewriter（逐文件单遍）：
                               #   声明展开插入 + ElementAccess/ElementBinding/ImplicitElementAccess/
                               #   复合与自增/初始化器 的调用点改写与临时变量提升 + 语句拼接
                               #   （镜像 C11 PropertyUseSiteRewriter 的 Splice 基建）
```

对 C11 共享组件的复用（只读 + §3.2 追加清单）：

- `MemberDesugarAnalyzer.Analyze`：整编译一次分析入口，`MemberDesugarAnalysisContext` 提供 `Compilation/Options/Models`；索引器声明与站点枚举由 IndexerRule 在 `Analyze` 内自扫（C11 注释明示 C12-C14 如此复用）；
- `MemberDesugarPlan`/`MemberDesugarPlanBuilder`：plan 基类与确定性排序；
- `MemberDesugarPreprocessor`：`RewriteFile` 驱动、`ExcludePlans`、`TryAttributeDiagnostic`（位置 → (类型名,成员名) → 唯一 `OwnsTouch` plan）、`FormatAndValidate` 三候选（改动区间格式化→整文件格式化→原文，零 error 取首）；
- `MemberDesugarSyntaxFacts`：`RewriteAnnotation`（`"Cs2jMemberDesugarRewrite"`）、`IsInsideExpressionTree`、`IsDynamic`、`TempName`（`__cs2j_` 前缀）；
- `AccessorsToMethods`：`GetterName/SetterName`（`"get_" + name`）与 `BuildMethodModifiers`（访问器可访问性/接口/显式实现修饰符规则，签名按属性声明收窄为「索引器声明 + 访问器」等价信息）。

### 4.2 两阶段数据流（整树一次分析 → plan 化重写 → 整树验证回滚）

1. **分析（整树一次，随 member-desugar 的 BindCompilation）**：编译方式与 C11 适配器一致（`CSharpParseOptions(LanguageVersion.Latest)`、合成 global usings、`GetBasicReferences()`、allowUnsafe DLL 编译；单编译包含预处理树内全部受支持工程的源文件，故解决方案内跨工程用点对符号仍可见 `IsInSource`，外部 NuGet/BCL 不可见——D5 闸门成立，Q4 已由 C11 `SampleMemberDesugarSolution`（App 引用 Lib）实证）。`IndexerRule.Analyze` 遍历全部 `IndexerDeclarationSyntax` 收集源内 `IPropertySymbol { IsIndexer: true }`（partial 无索引器、多部件按符号去重），并枚举全部 `ElementAccessExpression`/`ImplicitElementAccess`/`ElementBindingExpression`，按 `SymbolInfo` 反查绑定符号建倒排索引。
2. **计划**：每个源内索引器符号一个 `IndexerPlan`（D17）；派生 GetterMethodName/SetterMethodName（D1-D3）；TouchedFiles = 声明语法所在文件 ∪ 全部绑定用点文件；按 §4.3 分类，存在不可行站点且未 force 时 `Outcome=Infeasible, FailCode=CS2J5135..5140`（不动盘）。
3. **重写（逐文件，按 active plans 过滤）**：全部 TouchedFiles 跑 `IndexerUseSiteRewriter`（声明文件也跑——访问器体内 `this[i]` 递归用点必须改写，且声明展开与用点改写在同一遍内先访问器体后展开，同 C11）；改动节点挂共享 RewriteAnnotation，随后 `FormatAndValidate`（仅快速预筛，不作回退依据）。与属性 rewriter 的顺序：同一文件先属性后索引器（Order 10→20），两者改写不交叉（属性规则不触碰 `ElementAccess`，索引器规则不触碰 `PropertyDeclarationSyntax`）。
4. **验证回滚（族驱动，C11 `MemberDesugarPassAdapter.ExecuteAsync`）**：基线错误集 → 每轮从原始副本重放 → 整树重编译 → DiffErrors → 按 plan 的 `OwnsTouch` 唯一归属 → 回滚该 plan 的 TouchedFiles 并 ExcludePlans → 迭代收敛；无法唯一归属 → 本次运行全部回退（CS2J5100）。归属机制对索引器 plan 与属性 plan 统一生效。

### 4.3 分析期分类清单（IndexerRule.Analyze 产出）

每个索引器 plan 记录：

- 符号事实：`IPropertySymbol`、含类型完全限定名、`MetadataName`（Item/自定义）、形参表（名/类型/refkind/可空/默认值——索引器参数允许默认值，归约后随方法保留，strip-default pass 另有处理）、返回类型与 RefKind、get/set/init 访问器及各自可访问性、`IsVirtual/IsAbstract/IsOverride/IsSealed/IsExplicitInterfaceImplementation`、显式实现的接口符号；
- 用点分类：值读（getter）、写（setter 绑定 vs ref-get 变量赋值）、复合赋值（op= 种类 + 实参副作用标记）、前后缀自增、null 条件读、`ref/out` 实参位置、集合初始化器（可插入语句上下文 vs 不可提升——字段初始化器/特性实参/查询表达式内）、外部索引器用点（计数）；
- 不可行标记：表达式树 lambda 内用点（D15，CS2J5135，force 可解除）、dynamic/绑定不可判定（CS2J5136）、不可提升上下文中的初始化器/复合/写值消费（CS2J5137）、partial 声明（CS2J5138）、extern / static abstract 接口声明（CS2J5139）、残余碰撞（CS2J5140，防御性）。

### 4.4 声明改写规则（IX-1 ~ IX-5）

- **IX-1 基本归约**：`T this[P1 p1, …, Pn pn] { get { … } set { … } }` → `T get_Item(P1 p1, …, Pn pn) { … }` 与 `void set_Item(P1 p1, …, Pn pn, T value) { … }`；块体/表达式体（含访问器级表达式体）原样平移；get-only/set-only 只生成存在侧；表达式体索引器（`T this[int i] => …;`）生成表达式体 `get_Item`。
- **IX-2 修饰符与访问器**：索引器修饰符 + 访问器修饰符合并为各方法修饰符（复用 C11 `AccessorsToMethods` 规则，不复刻今日 JavaModifiers 位映射）；`abstract`/接口声明生成无体方法；`virtual/override/sealed` 平移；struct `readonly` 修饰符丢弃（D7）；struct 索引器生成普通可变实例方法（C07 之后 struct 形态已定）。
- **IX-3 显式接口**：按 D6 生成 `T IFoo.get_Item(…)` / `void IFoo.set_Item(…, T value)`，方法体平移；移除 `ExplicitInterfaceSpecifier` 语法形态；接口侧声明在源内时同步归约（同 plan TouchedFiles 联动，接口 plan 与实现 plan 独立但文件集合联动，同 C11 CollectHierarchyLinks 模式）。
- **IX-4 特性与名契约**：访问器级特性随对应方法；声明级中 `AttributeTargets.Method` 合法的特性平移（复用 C11 `AttributeRouting` 的目标感知路由）；`IndexerNameAttribute` 移除（CS2J5131）；`[DefaultMember]` 是类型级特性，不属本语法节点，保留（CS2J5133 盘点）。
- **IX-5 init/ref**：init 访问器 → 普通 `set_Item`（CS2J5132，与 C11 同策略）；ref 返回 → get 签名保留 `ref`/`ref readonly`（D9）。

### 4.5 调用点改写规则（IX-6 ~ IX-12）

- **IX-6 读**：绑定源内索引器 get 的 `ElementAccessExpression` → `receiver.get_Item(args)`；接收者：`this`→`this`、`base`→`base`、显式实现且接收者静态类型不匹配 → 最小接口限定强转（D6，用 `ToMinimalDisplayString`）；实参逐字保留。
- **IX-7 写**：绑定 setter 的赋值 → `receiver.set_Item(args, value)`（表达式语句）；赋值结果被消费（`x = (c[i] = v)`）时在语句上下文提升（`var __cs2j_t = v; c.set_Item(i, __cs2j_t); x = __cs2j_t;`），不可提升 → CS2J5137；ref-get 索引器的写 → `receiver.get_Item(args) = value`（C# 合法变量，D9）。
- **IX-8 复合赋值/自增**：按 D11 展开并保证索引实参一次求值；ref-get 索引器直接保留 `get_Item(...) op= v` / `++get_Item(...)` 形态；值索引器的 `++/--` 按旧值/新值语义展开为 get→算→set 序列（结果被消费时提升结果临时变量）。
- **IX-9 null 条件**：`r?[i]` → `r?.get_Item(i)`（`ElementBindingExpressionSyntax` → `MemberBindingExpressionSyntax`）；链式 `a?[i][j]` 按语法嵌套逐层处理。
- **IX-10 集合初始化器**：按 D13 提升临时变量改写（`ImplicitElementAccess` 站点）。
- **IX-11 边界透传**：实参为 `^n`/`a..b`（IndexExpression/RangeExpression）原样进入方法实参；数组/外部类型元素访问不动（D4/D5）；指针元素访问（C06 领地）不绑定索引器，天然不动。
- **IX-12 特殊位置**：`M(ref c[i])` / `M(out c[i])` 仅在 ref 返回索引器时改写为 `M(ref c.get_Item(i))` / `M(out c.get_Item(i))`（值索引器该写法在 C# 中本就非法 CS0206，属基线错误）；查询表达式/字段初始化器内遇到必须提升的复合 → CS2J5137。

## 5. CLI 集成（无新适配器、无新开关）

1. **零新增适配器**：C11 交付的 `MemberDesugarPassAdapter`（族级整树迭代，自定义 `ExecuteAsync`）通过规则集自动包含 Indexers 规则；C12 不新增 `Preprocessing/Adapters/` 文件（仅 §3.2 清单内的 summary 行追加）。
2. **PassChainFactory/Program 零改动**：C11 已接线 `MemberDesugarOptionsFactory`（`--member-desugar-overrides` 与 `config/MemberDesugarOverrides.json` 回退查找）；Indexers 规则无独立开关，随 pass 启停（不设 `EnableIndexers`——规则缺省全随 pass，对齐 C11「一个 pass 四条规则」的族模型）。不新增 legacy `--no-xxx` 开关。
3. **统计汇入**：向 C11 平铺 `MemberDesugarStatistics` **追加**索引器计数器：`IndexersPlanned`、`IndexerReadSitesRewritten`、`IndexerWriteSitesRewritten`、`IndexerCompoundSitesExpanded`（复合+自增）、`IndexerInitializerSitesRewritten`、`ExplicitInterfaceIndexers`、`ExternalIndexerSites`；跳过/回退复用既有 `PlansSkipped`/`PlansRolledBack`/`SkippedByReason`。族适配器 `BuildChainSummaryLine()` 追加索引器分项（§3.2 清单内的展示性编辑）。
4. **overrides**：复用 C11 已落地的 `config/MemberDesugarOverrides.json`（`{"force": [...], "skip": [...]}`，键为 `"Ns.Type"` 或 `"Ns.Type::Item"`（MetadataName）；force 仅解除 CS2J5135 表达式树项，不解除 dynamic/碰撞/不可提升；JSON 解析失败 → Warning 按空集继续——C11 已实现）。
5. **中间树/指纹**：目录、preserve、restore、verify、bin 清理全部走 member-desugar pass 既有设置（catalog §4 风险 11 的指纹要求由族 pass 统一满足，规则不新增中间产物）；无支撑库注入。
6. **单文件模式**：沿用族 verb 的自包含闸门——plan 的 TouchedFiles 含输入文件之外路径时排除并补族级跨文件诊断（CS2J5109），索引器规则不另建码。

## 6. 诊断策略

诊断码沿用族统一 **CS2J51xx**（MSBuild 格式 `file(line,col): warning CS2J51xx: [Symbol] message`，C11 `MemberDesugarDiagnosticCode` 枚举，码位只追加不改号）。C11 已占 5100-5129（共享 5100-5109 / 属性 5110-5129）并预留 5130-5189 给 C12/C13/C14；**C12 取 5130-5141**，5142-5189 留给 C13/C14 与 C12 后续追加：

| 码 | 枚举名 | 级别 | 含义 |
|---|---|---|---|
| CS2J5130 | IndexerPlanCreated | Info（仅 verbose） | 索引器已归约：`[含类型] 签名 → get_X/set_X`（含显式接口/ref/抽象标记） |
| CS2J5131 | IndexerNameAdopted | Info | 采用 `[IndexerName]` 自定义名（同时告知特性已移除） |
| CS2J5132 | IndexerInitWidened | Info | init-only 访问器归约为普通 set 方法（编译期 init 限定不再强制） |
| CS2J5133 | DefaultMemberRetained | Info | 类型级 `[DefaultMember]` 保留：反射按名查找（`GetProperty("Item")` 等）不保证 |
| CS2J5134 | ExternalIndexerSite | Info（仅 verbose） | 外部程序集索引器用点未改写（按接收者类型聚合，供抽样核对 D5 闸门） |
| CS2J5135 | IndexerExpressionTreeSite | Warning | 索引器用点位于表达式树 lambda 内 → plan 原子跳过（overrides.force 可解除） |
| CS2J5136 | IndexerDynamicSite | Warning | dynamic 接收者/绑定不可判定 → plan 原子跳过（force 不解除） |
| CS2J5137 | IndexerNonLiftableSite | Warning | 不可提升上下文的复合/自增/初始化器/写值消费 → plan 原子跳过 |
| CS2J5138 | IndexerPartialDeclaration | Warning | partial 索引器（C# 13）→ plan 原子跳过 |
| CS2J5139 | IndexerUnsupportedDeclaration | Warning | extern / static abstract 接口索引器 → plan 原子跳过 |
| CS2J5140 | IndexerNameCollision | Warning | 生成方法名与既有成员同签名碰撞（防御性，合法输入理论不可达） |
| CS2J5141 | ResidualIndexer | Warning（`--strict` 下 Error） | residual 闸门：处理后仍存在无 plan 且无跳过诊断的索引器声明/绑定用点（§7.4） |

族级诊断（C11 已落地，Indexers 只参与不自造）：CS2J5100 整树回退、CS2J5101 plan 回滚、CS2J5102 restore 失败、CS2J5103/5104 build/test 失败、CS2J5109 单文件跨文件。

## 7. 测试计划

全部以 `-c Release` 执行；先写单测矩阵再实现。

### 7.1 Core 单测 `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerUnitTests.cs`

内联源码 → `CSharpCompilation` → `MemberDesugarPreprocessor`（规则经程序集扫描自动生效）→ `RewriteFile` → 文本断言（忽略空白）+ `AssertOutputCompiles`。测试基建直接复用 `MemberDesugarPropertyUnitTests` 的 `internal static Rewrite/AssertOutputCompiles` 帮助方法（同程序集可见）。

- **单参**：get/set、get-only、表达式体、`public get; private set`、`this[i]` 体内递归用点、`base[i]`；
- **多参/重载**：`this[int x, int y]` → `get_Item(x, y)`；同类型两个签名的索引器生成重载；索引用点带默认参数；
- **多维边界**：`T[,]`/`T[][]` 的 `arr[i, j]`/`arr[i][j]` 原样不动；同一文件内多维数组与多参索引器并存互不误伤；
- **显式接口**：`T IFoo.this[int i]` → `T IFoo.get_Item(int i)`；同类显式实现 `IFoo`/`IBar` 两索引器；接口+隐式实现同编译；跨文件接口；用点强转限定最小化；
- **虚/抽象**：abstract class 抽象索引器（无体方法）、`virtual`/`override`/`sealed override`、接口索引器 get-only 不生成 set_Item、DIM 带体接口索引器；
- **ref 返回**：`ref T this[int i]` 的读/`ref var`/赋值/自增/`M(ref ..)`/`M(out ..)` 归约后可编译；
- **复合/自增**：值索引器 `+=`/`-=`/`++`/`--` 展开正确；**带副作用索引实参**（`m[Next()] += x`）提升临时变量且 `Next()` 只出现一次；不可提升上下文中判 CS2J5137；
- **null 条件 / 初始化器**：`d?[0]` → `?.get_Item(0)`；`new C { [1] = 2 }` 提升；
- **外部门**：`List<int>`/`Dictionary<int,string>`/`string`/`int[]` 的 `[]` 全部原样；BCL 用点计数 ExternalIndexerSites；
- **C31 交接**：`c[^1]`/`c[a..b]` 归约为 `get_Item(^1)`/`get_Item(a..b)`，实参不被吞改；
- **nameof/特性**：`[IndexerName("Foo")]` → `get_Foo` 且特性消失（CS2J5131）；`[DefaultMember]` 保留 + CS2J5133；
- **反例**：表达式树 lambda 内用点 → plan CS2J5135 全不动（同类型属性 plan 不受影响）；`force` 解除；dynamic 跳过（CS2J5136）；单文件模式跨文件 plan 排除（CS2J5109）；`skip` 配置排除；二次运行零改动（幂等）。

### 7.2 项目级 fixture

在 C11 已落地的 `tests/SampleMemberDesugarSolution/` 中新增 C12 独占文件：`SampleMemberDesugarLib/IndexerSamples.cs`（单参/多参/显式双接口/抽象虚链/ref 返回/`[IndexerName]`/初始化器与复合副作用用例各一）、`SampleMemberDesugarApp/Program.cs` 与 `SampleMemberDesugarTests/BehaviorTests.cs` 增补跨「工程」用点（App 经项目引用消费 Lib 的索引器，验证 §4.2 单编译内跨工程源符号可见——C11 fixture 已证明该模型成立）。断言：`result.Success`、统计计数、中间树不含源内 `this[` 声明与绑定用点、含 `get_Item/set_Item/IFoo.get_Item`；对中间树 `dotnet build`/`dotnet test -c Release` 退出码 0（`MemberDesugarProjectTests` 的 VerifyBuild/VerifyTests 默认开即覆盖）；clean 工程字节级复制（含 BOM）用例沿用族级行为。新增 `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerProjectTests.cs` 承载索引器分项断言。

### 7.3 转换器既有回归的处置

`MyDictIndexerTests`、`DictionaryLikeIndexerSetterTests`、`MultiArgIndexerCompoundAssignmentTests`、`DictionaryIndexerReadCheckedTests`、`InterfaceIndexerReturnTypeTests`、`ArrayIndexerAccessTests`（均在 [tests/CSharpToJava.Tests](file:///d:/code/cs2j/tests/CSharpToJava.Tests)）：member-desugar 默认关期间必须**持续全绿、零改动**（这些测试直连 ConversionPipeline，不经过预处理器）；默认开评审时再按 M6 统一处置预期，不在本 pass 内迁就旧断言改测试。

### 7.4 residual 闸门与一致性/CLI 测试

- residual：新增闸门测试——构造含未覆盖索引器形态（如 extern）的样例，断言 ResidualIndexer（CS2J5141）计数与 `--strict` 退出码语义；干净样例 residual=0；
- 一致性：`ProjectPreprocessorsConsistentTests` 不断言新 pass（无新 pass）；C11 已为其增加 member-desugar 段，C12 仅在既有段语义不变的前提下无需改断言；
- CLI：在 C11 的 member-desugar CLI 路径上追加索引器场景（`--enable-pass member-desugar` 跑索引器夹具、`--list-passes` 不出现新 id、族摘要含索引器分项、CS2J5135/CS2J5141 输出与退出码）；不新增独立 verb。

### 7.5 验证命令

```powershell
dotnet build CSharpToJavaConverter.slnx -c Release
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj -c Release
```

## 8. 默认开启验收门槛

随 member-desugar 族统一门槛（C11 已定义：中间树 build/test 全绿、csharp-sdk 基线不退化、指纹/回滚/落盘三不变），Indexers 规则附加项：

1. 7.1 单测矩阵全绿；7.2 fixture 的 Release `build` **与** `test` 退出码 0；
2. residual 闸门：训练集/夹具中**源内**索引器 plan 覆盖率 100%（跳过项必须全部有 CS2J5135-5140 显式原因，无「静默未处理」）；外部索引器用点零改写经 ExternalIndexerSites 抽样核对；
3. 等价性专项：副作用索引实参单次求值（行为测试，非文本断言）；显式双接口分派走通接口调用；抽象/虚链中间树测试钉住；
4. 幂等：二次运行零 diff；
5. **转换器对接前置确认**：M6 影响面（get/set/put → getItem/setItem 的 Java 形态变化、C11 决策 1b 的 `get_/set_` 派生改造、compat 库既有调用方）经评审并有跟进 PR 排期，或族已确定 member-desugar 长期默认关策略——二者满足其一前，Indexers 随族保持默认关（D18）。

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M0 | C11 锚点合入/对齐（已完成：merge `ac9bacab`）；规则接缝/统计/overrides/诊断码位按落地形态定案 | 文档/协调 |
| M1 | 共享模型追加（5130-5141 码位、索引器计数器）+ IndexerRule 声明收集/闸门/站点分类 + IndexerPlan/BuildId/OwnsTouch + IndexerFacts/IndexerDiagnostics；单测覆盖分析面（不重写） | Core ~350 行 |
| M2 | IndexerDeclarationExpander（IX-1~IX-5：单参/多参/显式接口/虚抽象/init/ref/readonly 丢弃/特性路由）+ `RewriteFile` 索引器派发 + 单测 | ~450 行 |
| M3 | IndexerUseSiteRewriter 用点全改写（IX-6~IX-12：读/写/复合自增单次求值/null 条件/初始化器/ref|out/强转限定/语句拼接）+ 单测主体 | ~650 行 |
| M4 | fixture 增补、跨工程用点、residual 闸门、幂等、适配器摘要分项、CLI 场景；Release build/test 全绿 | ~250 行 + fixture |
| M5 | sdk 索引器形态补扫（命中率/外部类型分布/`[IndexerName]`/DefaultMember 盘点）、文档交付与 §8 门槛评审 | 扫描/文档 |
| M6 | （独立排期，默认开前置）转换器侧收口：评估 `IndexerTransformer`/`ElementAccessTransformer`/`AssignmentTransformer` 索引器分支在预处理路径上的冗余下掉或重定向，完成 C11 决策 1b 的 `get_/set_` 派生改造对接（get_Item → getItem），确认 compat 库对接；处置 §7.3 旧回归预期 | 独立 PR |

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **Java 产物形态变化**：源内索引器从 `get/set/put` 变为 `getItem/setItem`，可能与 compat 库及既已生成 Java 的手工调用方失配；字典 `put` 返回旧值语义无 C# 对应 | D18；IndexerTransformer L52-57/L184-185 | 默认关 + §8 第 5 条；M6 独立收口（含 C11 决策 1b 派生改造）；不试图在 C# 侧保留 put 语义 |
| R2 | 副作用索引实参的双求值：临时提升改变语句结构，漏提即静默语义错 | 今日转换器双求值（AssignmentTransformer L99-109） | D11 白名单 + 行为测试（非文本）；不可提升即 CS2J5137，禁止凑合 |
| R3 | 外部/源内误判：项目引用经 DLL 元数据参与编译时 `IsInSource` 可能为假，跨工程用点漏改 | 单编译近似模型与 JK/RR 同源 | §7.2 跨工程 fixture 实证（C11 同模型已实证属性面）；漏改由整树编译/中间树测试暴露（方法不存在即编译错并触发回滚/跳过诊断），不产出半残树 |
| R4 | 显式接口/多接口组合复杂：强转限定插错位置改变分派 | 今日字符串匹配兜底反复回归（InterfaceIndexerReturnTypeTests 等） | 一切以绑定符号为准；协变返回（C# 无协变索引器，D7 已澄清不存在的情形）；同类型同名重载索引器生成体内新增错误归属不唯一 → 族级整树回退（D17 已知限制，保守正确） |
| R5 | 表达式树/dynamic：改写改变表达式树节点 | catalog §4 风险 8 | D15 复用 C11 判定 + plan 级跳过 + force 语义 |
| R6 | 反射/字符串契约：`[DefaultMember]`、`GetProperty("Item")`、XAML/JSON 按名绑定、COM `[IndexerName]` | catalog §4 风险 7；全仓零处理现状 | D14 文档化限制 + overrides；nameof 无此形态；不承诺运行时一致 |
| R7 | 与 C31 的顺序未在注册表强约束：若 C12 先把 `[^1]` 搬入 `get_Item(^1)`，C31 的用点识别必须覆盖方法实参位置 | catalog L632-635；两 pass RunAfter 无直接边 | D10 实参透传使两种先后顺序都可编译；联合 fixture 钉死；两族都落地时补一条 RunAfter 边（倾向 index-range 先于 member-desugar，见 §11） |
| R8 | C11 接缝形态变化导致返工 | 本任务含三处共享文件追加（§3.2 清单） | C11 已合入且 fixture e2e 绿；追加均为「枚举成员/计数器/派发分支/摘要行」式扩展，不改既有属性行为；属性单测守门（C11 单测必须持续绿） |
| R9 | 整树多轮重编译拉长链耗时 | catalog §4 风险 11 | 无索引器工程零触碰（clean 字节复制）；Analyzer 一次建表；verify 可关；指纹缓存复用 |
| R10 | init-only 语义弱化：set_Item 可在任意时点调用 | D8 | 与 C11 同策略同诊断；中间树目标是 Java 可译而非完整保留 C# 访问期约束 |

## 11. 并行协调

| 相邻工作 | 边界 | 合并顺序/依赖 |
|---|---|---|
| **C11 Property（族锚点）** | C11 拥有 member-desugar pass、共享基建与 stub 翻转；C12 只加 `Indexers/` 规则 | **已合入**（merge `ac9bacab`）；§3.2 追加清单；命名/回滚/统计接缝以 C11 为准 |
| **C13 Event / C14 Operator（同族）** | 各自规则目录 `Events/`、`Operators/` 与本规则并列；共享 Analyzer/回滚/命名服务，互不编辑他族目录；C13/C14 取码位 5150+、Order 30/40 | 并行无语法面争抢；注册发现 = 程序集扫描零热文件 |
| **C31 index-range（`index-range`，RunAfter array-method）** | C31 归约 `^`/`..` 表达式使用点（含数组/string/自定义 Index 索引器/切片）；C12 归约索引器**声明与成员形态**，实参原样透传（IX-11） | 无直接 RunAfter 边；两种落地顺序都产出可编译树；联合测试 `c[^n]`；两族都在链时倾向补边 `index-range` → `member-desugar`（先消表达式糖再方法化，减少他族特判），最终由注册表评审定 |
| **E12 array-method** | E12 处理多维数组/数组静态方法并注入 Cs2jArray；数组元素访问不绑定 `IPropertySymbol`，C12 闸门天然不命中（D4） | member-desugar RunAfter struct/closure（晚于 array-method 现状位）；无交叠；保留一条「数组 vs 多参索引器并存」联合用例 |
| **C17 复合赋值/自增（numeric-boundary 族，更晚）** | C17 做通用复合目标读改写归约；catalog L404 注明复合/自增依赖 indexer 先方法化 | C12 先把索引器复合点展开为方法调用序列（D11 已保证单次求值）；C17 落地后把索引器分支视同普通方法调用，不再需要索引器特例 |
| **C01 ref-out-in** | ref 返回索引器的 Java holder 映射走 C01 既有通道；C12 在 C# 中间树保留 `ref T get_Item` | member-desugar RunAfter 含 closure-lift，与 ref-out-in 先后按注册表现状（ref-out-in 已在链中）；联合用例 `ref var x = ref c[i]` |
| **C46 成员收口（同族未来项）** | 显式接口/new 隐藏的最终 Java 分派收口 | C12 不处理 Java 分派（`Kind.Signature` 槽位预留）；协变返回索引器不存在（D7） |
| **C08/E17 java-keywords（链尾）** | `get_Item/set_Item` 非 Java 保留字；生成方法名/形参名若撞保留字由 C08 全符号改名表统一覆盖（含 `value` 等形参） | C12 在前、C08 在后；索引器无 nameof 项，与 JK-4a 无冲突 |
| **C20 partial-merge** | 索引器符号经 SemanticModel 天然跨 partial 部件合并，不依赖语法合并；C# 13 partial 索引器本身被 CS2J5138 排除 | 无硬依赖；partial-merge 落地后仍以符号为键 |
| **注册表/工厂** | `PassChainFactory`/`PassCatalog`/`PassStubCatalog` 目标零行为改动（§3.1、§3.2） | 全部注册表行为变更归 C11 PR（已落地）；C12 仅可选描述性更新 |

## 12. Worktree 分配

- 分支：`feature/preproc-c12-indexer-desugar`
- 目录：`.worktrees/preproc-c12-indexer-desugar`
- base：dev-3.x `5e3e796d`（含 `67477551` 注册基线 + 本设计文档与 C31/C37 设计文档——base 必须包含本文档自身，故取 dev-3.x HEAD 而非 `67477551`；`67477551` 与 C11 的 merge-base 一致，无冲突面）
- **实现前置动作（已完成）**：worktree 创建后 merge C11 分支 `feature/preproc-c11-property-desugar`（`ac9bacab`），确认 `src/CSharpToJava.Core/MemberDesugar/` 共享骨架、`MemberDesugarPassAdapter` 与 member-desugar Implemented 翻转已在树中，再开始 M1。

```powershell
git worktree add .worktrees/preproc-c12-indexer-desugar -b feature/preproc-c12-indexer-desugar dev-3.x
git -C .worktrees/preproc-c12-indexer-desugar merge --no-edit feature/preproc-c11-property-desugar
```

---

## 附：开放问题（已全部按 C11 落地形态定案）

- **Q1 规则接缝形态（已定）**：`IMemberDesugarRule { Kind, Order, Analyze(context, builder) }`；发现 = 程序集扫描（`MemberDesugarRuleAttribute` + 无参构造）；C12 = `[MemberDesugarRule(MemberDesugarKind.Indexer, 20)]`，零注册代码。共享 Analyzer/Plan/命名/统计/回滚组件以 `MemberDesugar*.cs` 落地形态为准（§3.2 追加清单穷尽了共享文件改动）。
- **Q2 诊断码位与 overrides 文件名（已定）**：族统一 CS2J51xx；C12 取 5130-5141（§6），C13/C14 取 5150+；overrides = C11 已落地的 `config/MemberDesugarOverrides.json`（`force`/`skip`），键含 `"Ns.Type::Item"`（MetadataName）。
- **Q3 getter/setter 最终命名与 Java 对接路线（已定）**：`get_Item/set_Item`（C11 设计共享骨架条款预留 + `AccessorsToMethods` 落地）；Java 半区经 C11 决策 1b 的 `get_/set_ → getX/setX` 派生改造得 `getItem/setItem`，归 M6 消费 PR。
- **Q4 跨工程用点编译模型（已定）**：C11 适配器的单编译模型（全部受支持工程源文件进同一 `CSharpCompilation`）下跨工程源符号可见；C11 `SampleMemberDesugarSolution`（App→Lib 项目引用）已实证；C12 fixture 复用并加索引器用点。
- **Q5 协变返回索引器（已定：不存在）**：C# 协变返回（C# 9）仅适用于方法覆写，索引器覆写必须精确匹配返回类型——合法输入中不存在协变索引器，无需分支（D7/R4 已澄清）。
