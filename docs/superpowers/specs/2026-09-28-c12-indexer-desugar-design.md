# C12 Indexer 去糖预处理器设计：`this[...]` → `GetItem/SetItem`（member-desugar 族 Indexers 规则）

**日期**: 2026-09-28
**范围**: 向 member-desugar 族增加 Indexers 规则——Core `MemberDesugar/Indexers/`（声明归约 + 全调用点改写）；**不新增 pass、不新增适配器、不翻转 stub、不改任何 C#→Java 转换代码**
**状态**: 设计待评审；实现依赖 C11 Property 锚点任务合入
**族锚点**: C11 Property 去糖（catalog L384-394）——C11 创建 `src/CSharpToJava.Core/MemberDesugar/` 共享基建并把 `member-desugar` 从 Stub 翻转为 Implemented；本任务只在其骨架上增加规则目录
**分支基线**: `f68a783c`（`feat(preprocessing): register 23 merged-family stub passes`，已核实存在：`f68a783cd74cbfc398204bcdabe000e4f881ad30`）；实现前先 rebase/合入 C11 分支

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

- 声明：`T this[P1 p1, P2 p2] { get; set; }` → `T GetItem(P1 p1, P2 p2)` + `void SetItem(P1 p1, P2 p2, T value)`；
- 调用点：`obj[a, b]` → `obj.GetItem(a, b)`；`obj[a, b] = v` → `obj.SetItem(a, b, v)`；复合/自增/`?.[]`/集合初始化器/ref 返回等形态按 §4.5 精确归约；
- 归约后中间树整树重编译相对基线零新增错误（硬不变式，与 record-readonly/java-keywords 相同），plan 为最小原子回退单位。

### 1.3 范围

**在范围内**：

- `this[...]` 索引器声明：单参、多参（`this[int x, int y]`，编译为签名重载的同名方法）；
- `get`/`set`/`init`/表达式体索引器、访问器差异化可访问性（`public get; private set`）；
- 显式接口实现索引器（`T IFoo.this[int i]`，含同类实现两个带索引器接口）；
- `virtual`/`abstract`/`override`/`sealed` 索引器、接口索引器声明；
- `ref`/`ref readonly` 返回索引器（调用点按 ref 变量分类归约，见 D7）；
- 全部源内调用点：读、写、复合赋值、自增自减、`this[...]`/`base[...]`、null 条件 `x?[i]`、集合初始化器 `new C { [k] = v }`、跨文件/跨工程（同一预处理树内）用点。

**明确不在范围内（边界，详见 §11）**：

- `^`/`..`（System.Index/System.Range）表达式去糖——属 **C31 index-range**（catalog L627-639）；本规则只保证把索引实参**原样搬进方法实参**，不解释实参形态；
- 数组（含多维数组 `T[,]`、锯齿数组 `T[][]`）元素访问——属 **E12 array-method** 与转换器数组路径；语义上它们不绑定 `IPropertySymbol`，本规则天然不可见；
- BCL/第三方（外部程序集）类型的索引器——`List<T>`/`Dictionary<,>`/`string`/`Span<T>` 等的 `[]` 调用点**原样保留**，Java 侧 `get/put/getChecked/charAt/[]` 映射仍由现有转换器负责；
- 转换器侧 `IndexerTransformer`/`ElementAccessTransformer`/`AssignmentTransformer` 索引器分支的删除或改名——后续独立 PR（§10 R1、§9 M6），本任务不动转换器。

## 2. 关键设计决策表

| # | 决策点 | 结论 | 理由 / 备选 |
|---|---|---|---|
| D1 | **归约目标命名** | getter `GetItem`、setter `SetItem`（PascalCase；转换器普通方法路径首字母小写后为 `getItem`/`setItem`） | catalog L401 写定 `GetItem/SetItem`；与 C# 元数据 `get_Item/set_Item` 同根但去掉 CLR 下划线约定（手写 C# 自然、与属性 `GetX/SetX` 平行）。备选 B：`Get/Set`——与今日 Java 产物字面同名、转换器零改名即可对接，但与用户既有 `Get(int)` 碰撞概率高、多参/字典语义无法从名字区分、信息量低，否决。备选 C：保留 `get_Item/set_Item`——转换器 decap 后保留下划线，Java 命名反常，否决 |
| D2 | **多参数 / 重载索引器** | 同一类型不同签名的索引器共用 `GetItem/SetItem`，按形参表形成普通 C# 重载；调用点按绑定符号生成实参，重载解析交还给 C# 编译器 | C# 元数据本身即如此（多个 `get_Item` 重载）；无需参数名拼入方法名（不采用 `GetItemByXAndY` 一类自造命名） |
| D3 | **`[IndexerName("Foo")]`** | 采用特性给定名：`GetFoo/SetFoo`；符号 `IPropertySymbol.Name` 是真相源（默认 `"Item"`）；该特性（`AttributeTargets.Property`）从声明上**移除**（挂到方法上是编译错误），发 MD102 | 全仓零处理现状（§1.5）；命名表以符号为键，声明与全部用点一致 |
| D4 | **多维索引器 vs 多维数组** | 唯一闸门 = 语义模型：`ElementAccess` 绑定到 `IPropertySymbol { IsIndexer: true }` 且符号在源内才改写；`T[,]`/`T[][]` 访问绑定不到属性符号，原样保留（E12 领地）；多参用户索引器 `m[a,b]` → `GetItem(a,b)`，绝不做 `[a][b]` 文本变换 | 消除今日「数组 vs 属性」二义（IndexerTransformer 正则、ElementAccessTransformer 两路 fallback 的根因） |
| D5 | **外部程序集索引器** | `Locations` 无源位置的索引器（List/Dictionary/string/Span/COM…）**声明与用点均不动**；用点计数 MD110（Info） | 这些类型的 Java 映射形态不同（`[]`/`get`/`put`/`getChecked`/`charAt`），C# 侧方法化反而锁死映射；与 JK-4c「外部符号不改名」同原则 |
| D6 | **显式接口实现** | 声明保留限定：`T IFoo.GetItem(P p)` / `void IFoo.SetItem(P p, T value)`；接口在源内时接口声明同步归约为同签名方法；用点按绑定符号生成——接收者静态类型不是该接口时插入最小限定强转 `((IFoo)receiver).GetItem(...)`；同类显式实现 `IFoo` 与 `IBar` 两个索引器时得到两个限定名方法，天然不碰撞 | 复刻 C# 显式实现的私有+接口分派语义；今日转换器靠字符串猜接口名（IndexerTransformer L246-L253），改由符号表终结 |
| D7 | **虚/抽象/覆写** | 修饰符整体平移到生成方法：`virtual`/`override`/`sealed override`/`abstract`（抽象与接口方法无体）；访问器级修饰符各自落到 get/set 方法；协变返回索引器不在首期处理（归 C46 收口，见 §11） | 普通 C# 实例方法的虚分派语义与索引器一致；struct 索引器的 setter 变异 `this` 语义在普通实例方法上原样成立 |
| D8 | **init-only 访问器** | 归约为普通 `SetItem`（丢失编译期 init 限定），发 MD103；策略必须与 C11 属性 init 决策**逐字一致**（锚点定调） | 中间树目标是可编译 C# 而非保留 init 契约；转换器现状本就是注释占位 |
| D9 | **ref 返回索引器** | get 方法签名保留 `ref T GetItem(...)` / `ref readonly T GetItem(...)`；ref 变量位置（`ref var x = ref obj[i]`、`obj[i] = v` 无 setter、`obj[i]++`）归约后 C# 仍合法——调用 ref 返回方法的结果在 C# 中是变量；普通（值）索引器的写/复合走 setter 展开（IX-6/7） | 不引入 ByRef holder；Java 侧 ref 缺口是 C01 的既有领地，本规则不重复建设 |
| D10 | **调用点全量改写** | 凡绑定到源内索引器符号的 `ElementAccessExpression`（含 `ThisExpression`/`BaseExpression` 接收者、嵌套、条件访问、初始化器、参数）一律改写；索引实参表达式**逐字保留**（含 `^`/`..`，留给 C31） | 改名/改签名通道必须基于全编译符号表（catalog L896 横切约束）；禁止文本替换 |
| D11 | **复合赋值/自增的求值次数** | 值索引器 `t[k] op= v` → `t.SetItem(k, t.GetItem(k) op v)`；索引实参为「简单表达式」（无副作用白名单：字面量/局部/参数/字段访问）可直接复用文本；否则在可插入语句上下文提升一次临时变量；表达式上下文无法提升 → 该 plan MD120 跳过。`++/--` 同构并保留旧值/新值语义 | 修复今日双求值（AssignmentTransformer L99-L109）；与表达式族「单次求值临时变量提升」基建（catalog L909）对齐，落地时优先复用锚点/既有提升工具 |
| D12 | **null 条件访问** | `r?[i]` → `r?.GetItem(i)`（`NullConditionalAccessExpression` 内的 member-binding element access 改写为条件调用）；`r?[i] = v` 在 C# 中本就非法，无需处理 | 方法化后条件访问语义由 C# 编译器直接承担 |
| D13 | **集合初始化器** | `new C { [k] = v }`（initializer 内 `ImplicitElementAccess`，绑定源内索引器 setter）在语句上下文展开为 `var __c = new C(); __c.SetItem(k, v);` 再替换使用点；字段/特性等不可提升上下文 → MD120 跳过 plan | 与 C13 事件/add、C24 集合表达式的初始化器提升同模式 |
| D14 | **nameof / 反射 / DefaultMember** | C# 语法上 `nameof` 无法指向索引器（无成员名访问路径），无 nameof 改写项；`[IndexerName]` 按 D3 移除；类型级 `[DefaultMember("Item")]`（VB Default 或手写）保留不动并发 MD104（反射消费者 `GetProperty("Item")` 不再成立——属 catalog §4 风险 7（[L926](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926)）的同族文档化限制，不保证运行时反射一致） | 沿用 E17/JK 的反射限制声明与 overrides 机制，不发明运行时桥 |
| D15 | **表达式树 / dynamic** | 用点位于 `Expression<TDelegate>` lambda 内：该索引器 plan 整体 Infeasible（MD120，默认跳过，force 可解除该项）；`dynamic` 接收者/绑定不可判定：同样跳过。共享 JK-8/C05 的表达式树判定工具，不自造 | catalog §4 风险 8（[L927](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927)） |
| D16 | **命名碰撞** | `GetItem/SetItem`（或 `GetFoo/SetFoo`）与类型既有成员碰撞时，按锚点共享的确定性碰撞消解追加序号（`GetItem2`、`GetItem3`…，首撞从 2 起，按符号源位置排序）；声明与全部用点同查一张符号表 | 复刻 JK-4b 模型；不做即兴 per-site 改名 |
| D17 | **plan 粒度** | 一个源内索引器符号一个 plan（接口索引器与其各实现为**独立 plan**，用点按实际绑定符号归属）；TouchedFiles = 声明文件 ∪ 全部用点文件；族级回滚驱动由 C11 骨架承担，Indexers 规则只提供 plan/OwnsTouch/归属谓词 | 属性 plan 与索引器 plan 相互独立，可分别回退 |
| D18 | **产物 Java 命名变化** | 预处理后路径上，源内索引器的 Java 产物将从 `get/set/put` 变为 `getItem/setItem`；这是有意的形态变更，其与 compat 库/既有 Java 调用方的对接由转换器侧收口 PR（M6）与 §8 门槛钉住；pass 落地即默认关 | 与 C05/JK「新通道先 opt-in、门槛满足再默认开」（catalog §4 风险 10，[L929](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L929)）一致 |

## 3. 链位与族契约

### 3.1 归属既有 pass：`member-desugar`

stub 现状（[PassStubCatalog.cs L191-L200](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L191-L200)）：

| 字段 | 值 |
|---|---|
| Id | `member-desugar`（[PassStubCatalog.Ids.MemberDesugar](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L36)） |
| DisplayName / 目录 | "Member desugaring" / `.cs2j-member-desugar-src` |
| Stage | `PassStage.Declarative` |
| Status / DefaultEnabled | `Stub` / `false`（显式启用即 CS2J9001） |
| RunAfterIds | `{ "struct-value-semantics", "closure-lift" }` |
| CatalogItems | `C11, C12, C13, C14, C27, C39, C46` |

**本任务（C12）的链位承诺**：

1. **不新增 pass**：不注册新 id、不新增 `PassDescriptor`、不动 `RunAfterIds`；Indexers 随 member-desugar 的启用而启用、随其位置而行事。
2. **本分支不翻转 stub**：`PassStubCatalog.cs`/`PassCatalog.cs` 的 Stub→Implemented 翻转是 C11 锚点 PR 的专属交付（含适配器与注册表同批落地，不允许中间态，原则同 C05 §3.3）。C12 分支在 C11 合入点之后开发，合入时注册表中 member-desugar 已是 Implemented。
3. Stage 固定 `Declarative`（成员声明 + 跨文件用点归约），与 stub 登记一致；`NeedsSemanticModel=true`（全编译符号表是 D4/D5/D6 判定的前提）；`SupportLibrary=None`；`VerifyBuildByDefault/VerifyTestsByDefault` 取 C11 翻转时为整个 pass 设定的值（预期 true），C12 不单独改。
4. 中间目录 `.cs2j-member-desugar-src`、preserve 目录注册、指纹令牌（`pass:member-desugar=on/off`）、`--enable-pass/--disable-pass/--list-passes` 全部复用，零新增。

### 3.2 对 C11 锚点的依赖与等价文件所有权约定

首选路径：实现前把 C11 分支 rebase/merge 进本分支（§12），在 C11 已落地的 `MemberDesugar/` 骨架上直接添加 `Indexers/`。

若开发窗口内 C11 尚未合入，采用以下**等价文件所有权约定**，两分支最终可无语义冲突合并：

| 文件/目录 | 所有任务 | C12 行为 |
|---|---|---|
| `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs` | **C11 独占** | 只读引用，不编辑 |
| `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs` | **C11 独占** | 不编辑 |
| `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs`、`Adapters/MemberDesugarPassAdapter.cs` | **C11 独占** | 不新增适配器；如确需接线，仅允许在 C11 合入后追加（见 §5） |
| `src/CSharpToJava.Core/MemberDesugar/` 根下共享文件（Preprocessor/Models/Analyzer/Plan/SyntaxFacts/BackingFieldNaming 等） | **C11 所有、C12 只读复用**；共享命名/碰撞/标注/FormatAndValidate/表达式树判定如需新增，由 C11 接受 PR 或在合入后补 | C12 不在这些文件内就地写索引器特例 |
| `src/CSharpToJava.Core/MemberDesugar/Indexers/**` | **C12 独占** | 本任务全部新增代码落此目录 |
| `src/CSharpToJava.Core/MemberDesugar/Properties/**`、`Events/**`、`Operators/**` | C11 / C13 / C14 各自独占 | 不触碰 |
| 测试：`tests/CSharpToJava.ProjectTests/MemberDesugarIndexer*`、`tests/SampleMemberDesugarSolution/` 中 `Indexers*`/索引器夹具文件 | C12 | 共享 fixture 解决方案由 C11 建目录；C12 只新增带 Indexer 后缀的文件 |

接缝要求（写入 C11 设计的锚点契约，本设计假设其成立；若 C11 最终形态不同，以 C11 为准并做同名适配，列为开放问题 Q1）：族 Preprocessor 以规则集方式组合，每条规则实现统一接缝（建议名 `IMemberDesugarRule`）：贡献 plan、声明侧 rewriter、用点侧 rewriter、统计段与摘要片段、`OwnsTouch` 归属谓词；C12 只交付规则实现与一处**规则注册**（注册点放在 C12 独占文件内，如 `Indexers/IndexerRuleRegistration.cs` 由锚点的规则发现机制收集——具体发现机制（显式 `Add` 一行 vs 程序集扫描）由 C11 定，见 Q1）。

### 3.3 硬不变式

1. 中间源码树在预处理后永远可编译（整树重编译相对基线无新增 error；警告不比对）。
2. 每个 plan 的转换原子：全转或全不转；回退单位是单个索引器 plan，不影响同文件的属性/事件 plan。
3. 幂等：对已归约树再跑一次 member-desugar，Indexers 规则零改动（声明侧已无 `IndexerDeclarationSyntax`；用点侧只认属性符号绑定）。

## 4. Core 组件设计

### 4.1 新文件清单（全部在 `src/CSharpToJava.Core/MemberDesugar/Indexers/`）

命名空间 `CSharpToJava.Core.MemberDesugar.Indexers`：

```
IndexerRule.cs                 # IMemberDesugarRule 实现：规则 id "indexers"、
                               #   贡献 IndexerPlan 列表、挂接两个 rewriter、统计段、
                               #   OwnsTouch(file, typeName, memberName) 归属谓词
IndexerAnalysis.cs             # 规则侧分析结果：源内索引器符号表、用点倒排索引、
                               #   可行性分类（§4.3）；输入是 C11 共享 MemberDesugarAnalysis
IndexerPlan.cs                 # IndexerPlan / IndexerPlanBuilder：
                               #   Id/Symbol/MetadataName/GetName/SetName/IsExplicit/
                               #   InterfaceType/RefKind/Accessors/TouchedFiles/
                               #   Outcome/FailCode/FailReason/OwnsTouch
IndexerNaming.cs               # GetName/SetName 派生：符号 Name（Item 或 IndexerName 自定义）
                               #   → GetX/SetX；碰撞消解委托 C11 共享命名服务（D16）
IndexerSyntaxFacts.cs          # 纯判定：ElementAccess 是否绑定源内索引器、
                               #   get/set/ref 用点分类、简单实参白名单（D11）、
                               #   初始化器元素访问、表达式树/dynamic 判定（转调共享工具）
IndexerDeclarationRewriter.cs  # 阶段 2a：IndexerDeclarationSyntax → 1~2 个 MethodDeclarationSyntax
IndexerUseSiteRewriter.cs      # 阶段 2b：ElementAccess / ImplicitElementAccess /
                               #   NullConditional element-access / 复合与自增 / 初始化器
                               #   的调用点改写与临时变量提升
IndexerModels.cs               # IndexerRuleOptions / IndexerStatistics（IPassStatistics
                               #   的规则段，见 §5）/ IndexerDiagnosticCode(MD1xx) /
                               #   Severity / IndexerRuleResult / SkipReason
```

对 C11 共享组件的复用（只读，命名以 C11 定稿为准，Q1）：

- `MemberDesugarAnalyzer`：整编译源符号遍历、partial 合并、源内/外部判定（`IsInSource`）、用点枚举公共设施；
- `MemberDesugarPlan`/回滚驱动：plan 基类、`ExcludePlans`、`TouchedFiles`、整树 DiffErrors 与 `TryAttributeDiagnostic` 循环（等价于 [JavaKeywordsPassAdapter.cs L81-L209](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L81-L209) 的族内版本）；
- `BackingFieldNaming` 背后的**确定性命名/碰撞服务**：索引器无后备字段（C# 不允许自动索引器），但 D16 的方法名碰撞消解复用同一服务；
- `MemberDesugarSyntaxFacts`：`RewriteAnnotation`（建议 `"Cs2jMemberDesugarRewrite"`）、`FormatAndValidate` 三候选（改动区间格式化→整文件格式化→原文，零 error 取首）、表达式树内/dynamic 共享判定（JK-8 同族）、临时变量提升与名冲突回避工具。

### 4.2 两阶段数据流（整树一次分析 → plan 化重写 → 整树验证回滚）

1. **分析（整树一次，随 member-desugar 的 BindCompilation）**：编译方式与 record-readonly/java-keywords 同款（`CSharpParseOptions(LanguageVersion.Latest)`、合成 global usings、`GetBasicReferences()`、allowUnsafe DLL 编译；单编译包含预处理树内全部受支持工程的源文件，故解决方案内跨工程用点对符号仍可见 `IsInSource`，外部 NuGet/BCL 不可见——D5 闸门成立）。`IndexerAnalysis` 遍历编译内全部源内 `IPropertySymbol { IsIndexer: true }`（含接口与显式实现），并枚举全部 `ElementAccessExpression`/`ImplicitElementAccess`/null 条件元素访问，按 `SymbolInfo`/IOperation 反查绑定符号建倒排索引。
2. **计划**：每个源内索引器符号一个 `IndexerPlan`（D17）；派生 GetName/SetName（D1-D3、D16）；TouchedFiles = 声明语法所在文件 ∪ 全部绑定用点文件；按 §4.3 分类，存在不可行站点且未 force 时 `Outcome=Infeasible, FailCode=MD120`（不动盘）。
3. **重写（逐文件，按 active plans 过滤）**：声明文件跑 `IndexerDeclarationRewriter`，全部 TouchedFiles 跑 `IndexerUseSiteRewriter`（声明文件也要跑——访问器体内 `this[i]` 递归用点必须改写）；改动节点挂共享 RewriteAnnotation，随后 `FormatAndValidate`（仅快速预筛，不作回退依据）。
4. **验证回滚（族驱动，C11 的自定义 `ExecuteAsync`）**：基线错误集 → 每轮从原始副本重放 → 整树重编译 → DiffErrors → 按 plan 的 `OwnsTouch` 唯一归属 → 回滚该 plan 的 TouchedFiles 并 ExcludePlans → 迭代收敛；无法唯一归属 → 本次运行全部回退（族级 MD030 等价码）。归属机制对索引器 plan 与属性 plan 统一生效。

### 4.3 分析期分类清单（IndexerAnalysis 产出）

每个索引器 plan 记录：

- 符号事实：`IPropertySymbol`、含类型完全限定名、`Name`（Item/自定义）、形参表（名/类型/ref  kind/可空/默认值——索引器参数允许默认值？C# 允许索引器参数带默认值，归约后随方法保留，strip-default pass 另有处理）、返回类型与 ref kind、get/set/init 访问器及各自可访问性、`IsVirtual/IsAbstract/IsOverride/IsSealed/IsExplicitInterfaceImplementation`、显式实现的接口符号；
- 用点分类：值读（getter）、写（setter 绑定 vs ref-get 变量赋值）、复合赋值（op= 种类 + 实参副作用标记）、前后缀自增、null 条件读、ref/out 实参位置、集合初始化器、可插入语句上下文 vs 不可提升（字段初始化器/特性实参/查询表达式内）；
- 不可行标记：表达式树 lambda 内用点（D15）、dynamic/绑定不可判定（D15）、不可提升上下文中的初始化器/复合（D11/D13）、`extern` 索引器（无体且 ABI 未知，保守跳过）、锚点命名服务无法消解的残余碰撞（理论上不应发生，防御性）。

### 4.4 声明改写规则（IX-1 ~ IX-5）

- **IX-1 基本归约**：`T this[P1 p1, …, Pn pn] { get { … } set { … } }` → `T GetItem(P1 p1, …, Pn pn) { … }` 与 `void SetItem(P1 p1, …, Pn pn, T value) { … }`；块体/表达式体原样平移；get-only/set-only 只生成存在侧；表达式体索引器（`T this[int i] => …;`）生成表达式体 GetItem。
- **IX-2 修饰符与访问器**：索引器修饰符 + 访问器修饰符合并为各方法修饰符（复刻 C# 访问器可访问性规则，不再走今日 JavaModifiers 位映射）；`abstract`/接口声明生成无体方法；`virtual/override/sealed` 平移；struct 索引器生成普通可变实例方法（C07 之后 struct 形态已定）。
- **IX-3 显式接口**：按 D6 生成 `T IFoo.GetItem(…)` / `void IFoo.SetItem(…, T value)`，方法体平移；移除 `ExplicitInterfaceSpecifier` 语法形态；接口侧声明在源内时同步归约。
- **IX-4 特性与名契约**：访问器级特性随对应方法；声明级中 `AttributeTargets.Method` 合法的特性平移；`IndexerNameAttribute` 移除（MD102）；`[DefaultMember]` 是类型级特性，不属本语法节点，保留（MD104 盘点）。
- **IX-5 init/ref**：init 访问器 → 普通 `SetItem`（MD103，与 C11 同策略）；ref 返回 → get 签名保留 `ref`/`ref readonly`（D9）。

### 4.5 调用点改写规则（IX-6 ~ IX-12）

- **IX-6 读**：绑定源内索引器 get 的 `ElementAccessExpression` → `receiver.GetName(args)`；接收者：`this`→`this`、`base`→`base`、显式实现且接收者静态类型不匹配 → 最小接口限定强转（D6，用 `ToMinimalDisplayString`）；实参逐字保留。
- **IX-7 写**：绑定 setter 的赋值 → `receiver.SetName(args, value)`（表达式语句）；赋值结果被消费（`x = (c[i] = v)`）时在语句上下文提升（语义值等于右值），不可提升 → MD120；绑定 ref-get 的写 → `receiver.GetName(args) = value`（C# 合法变量，D9）。
- **IX-8 复合赋值/自增**：按 D11 展开并保证索引实参一次求值；ref-get 索引器直接保留 `GetName(...) op= v` / `++GetName(...)` 形态；值索引器的 `++/--` 按旧值/新值语义展开为 get→算→set 序列。
- **IX-9 null 条件**：`r?[i]` → `r?.GetName(i)`；链式 `a?[i][j]` 按语法嵌套逐层处理。
- **IX-10 集合初始化器**：按 D13 提升临时变量改写。
- **IX-11 边界透传**：实参为 `^n`/`a..b`（IndexExpression/RangeExpression）原样进入方法实参；数组/外部类型元素访问不动（D4/D5）；指针元素访问（C06 领地）不绑定索引器，天然不动。
- **IX-12 特殊位置**：`M(ref c[i])` 仅在 ref 返回时改写为 `M(ref c.GetName(i))`（值索引器该写法在 C# 中本就非法，属基线错误）；查询表达式/字段初始化器内遇到必须提升的复合 → MD120。

## 5. CLI 集成（无新适配器）

1. **零新增适配器**：C11 交付的 `MemberDesugarPassAdapter`（族级整树迭代，自定义 `ExecuteAsync`，形态参照 [JavaKeywordsPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs)）通过规则集自动包含 Indexers 规则；C12 不新增文件到 `Preprocessing/Adapters/`。
2. **PassChainFactory/PassCatalog 零改动为目标**：规则随族 options 默认启停；仅当 C11 的 options 工厂需要显式开关（如 `MemberDesugarOptions.EnableIndexers`，默认随 pass）时，在 C11 合入后允许一行接线——该字段在 C12 独占的 `IndexerModels.cs` 定义，工厂改动如不可避免则在 §3.2 所有权表中追加备注并与 C11 共同评审。不新增 legacy `--no-xxx` 开关；启停走描述符默认值与通用 `--enable-pass/--disable-pass`。
3. **统计汇入**：`IndexerStatistics` 是族 `MemberDesugarStatistics` 的一个规则段（C11 提供规则段聚合接口）。计数：`IndexersDesugared`、`IndexerGetSitesRewritten`、`IndexerSetSitesRewritten`、`IndexerCompoundSitesExpanded`、`IndexerIncrementSitesExpanded`、`NullConditionalSitesRewritten`、`InitializerSitesExpanded`、`ExplicitInterfaceIndexers`、`AbstractVirtualIndexers`、`RefReturnIndexers`、`ExternalIndexerSitesUntouched`、`IndexerPlansSkipped`、`IndexerPlansRolledBack`、`ResidualIndexerDeclarations`。族适配器的 `BuildChainSummaryLine()` 通过规则段自渲染（建议每规则暴露 `SummaryFragment`）输出索引器分项，C12 不编辑适配器文件。
4. **overrides**：复用族 overrides JSON（由 C11 定义，建议 `config/MemberDesugarOverrides.json`）：`skip`（按类型完全限定名排除索引器 plan）、`force`（解除表达式树项的保守跳过，语义同 JK force；不解除 dynamic/碰撞/不可提升项）。JSON 解析失败 → Warning 按空集继续。
5. **中间树/指纹**：目录、preserve、restore、verify、bin 清理全部走成员去糖 pass 既有设置（catalog §4 风险 11 的指纹要求由族 pass 统一满足，规则不新增中间产物）；无支撑库注入。
6. **单文件模式**：沿用族 verb 的自包含闸门——plan 的 TouchedFiles 含输入文件之外路径时排除并补族级跨文件诊断（等价 JK090/RR090），索引器规则不另建码。

## 6. 诊断策略

规则诊断码 **MD1xx**（MemberDesugar 族内分配给 Indexers 的子段；0xx 留给 C11 属性，2xx/3xx 预留给 C13/C14——最终码位表由 C11 锚点统一登记，Q2）。MSBuild 格式，码位只追加不改号：

| 码 | 级别 | 含义 |
|---|---|---|
| MD101 | Info | 索引器已归约：`[含类型] 签名 → GetName/SetName`（含显式接口/ref/抽象标记） |
| MD102 | Info | 采用 `[IndexerName]` 自定义名（同时告知特性已移除） |
| MD103 | Info | init-only 访问器归约为普通 set 方法（编译期 init 限定不再强制） |
| MD104 | Info | 类型级 `[DefaultMember]` 保留：反射按名查找（`GetProperty("Item")` 等）不保证 |
| MD110 | Info（仅 verbose） | 外部程序集索引器用点未改写（列出类型名，供抽样核对 D5 闸门） |
| MD120 | Warning | 索引器 plan 原子跳过：表达式树用点（force 可解除）/dynamic/不可提升上下文的复合或初始化器/extern/碰撞不可消解；消息含分类、文件:行列、建议 |
| MD150 | Warning（`--strict` 下 Error） | residual 闸门：处理后仍存在绑定到源内索引器的残余声明/用点（§7.4） |

族级诊断（整树回退 MD030/MD031 等价码、restore MD040、单文件跨文件 MD090）由 C11 骨架统一定义与打印；Indexers 规则只通过 plan 归属参与，不自定义重复码。

## 7. 测试计划

全部以 `-c Release` 执行；先写单测矩阵再实现。

### 7.1 Core 单测 `tests/CSharpToJava.ProjectTests/MemberDesugarIndexerRewriterUnitTests.cs`

内联源码 → `CSharpCompilation` → 族 Preprocessor（启用 indexers 规则）→ `RewriteFile` → 文本断言（忽略空白辅助方法同款）+ `AssertOutputCompiles`。

- **单参**：get/set、get-only、表达式体、`public get; private set`、`this[i]` 体内递归用点、`base[i]`；
- **多参/重载**：`this[int x, int y]` → `GetItem(x, y)`；同类型两个签名的索引器生成重载；索引用点带默认参数；
- **多维边界**：`T[,]`/`T[][]` 的 `arr[i, j]`/`arr[i][j]` 原样不动；同一文件内多维数组与多参索引器并存互不误伤；
- **显式接口**：`T IFoo.this[int i]`；同类显式实现 `IFoo`/`IBar` 两索引器；接口+隐式实现同编译；跨文件接口；用点强转限定最小化；
- **虚/抽象**：abstract class 抽象索引器（无体方法）、`virtual`/`override`/`sealed override`、接口索引器 get-only 不生成 SetItem；
- **ref 返回**：`ref T this[int i]` 的读/`ref var`/赋值/自增归约后可编译；
- **复合/自增**：值索引器 `+=`/`-=`/`++`/`--` 展开正确；**带副作用索引实参**（`m[Next()] += x`）提升临时变量且 `Next()` 只出现一次；不可提升上下文中判 MD120；
- **null 条件 / 初始化器**：`d?[0]` → `?.GetItem(0)`；`new C { [1] = 2 }` 提升；
- **外部门**：`List<int>`/`Dictionary<int,string>`/`string`/`int[]` 的 `[]` 全部原样；BCL 用点计数 MD110；
- **C31 交接**：`c[^1]`/`c[a..b]` 归约为 `GetItem(^1)`/`GetItem(a..b)`，实参不被吞改；
- **nameof/特性**：`[IndexerName("Foo")]` → `GetFoo` 且特性消失；`[DefaultMember]` 保留 + MD104；
- **碰撞**：类型既有 `GetItem(int)` → `GetItem2`（声明与跨文件用点一致）；
- **反例**：表达式树 lambda 内用点 → plan MD120 全不动（同类型属性 plan 不受影响）；`force` 解除；dynamic 跳过；单文件模式跨文件 plan 排除；`skip` 配置排除；二次运行零改动（幂等）。

### 7.2 项目级 fixture

在 C11 建立的 `tests/SampleMemberDesugarSolution/`（命名随锚点）中新增 C12 独占文件：`IndexersLib`（单参/多参/显式双接口/抽象虚链/ref 返回/`[IndexerName]`/初始化器与复合副作用用例各一）、`IndexersApp`（跨文件、跨「工程」用点：App 经项目引用消费 Lib 的索引器，验证 §4.2 单编译内跨工程源符号可见）。断言：`result.Success`、统计计数、中间树不含源内 `this[` 声明与绑定用点、含 `GetItem/SetItem/IFoo.GetItem`；对中间树 `dotnet build`/`dotnet test -c Release` 退出码 0；clean 工程字节级复制（含 BOM）用例。

### 7.3 转换器既有回归的处置

`MyDictIndexerTests`、`DictionaryLikeIndexerSetterTests`、`MultiArgIndexerCompoundAssignmentTests`、`DictionaryIndexerReadCheckedTests`、`InterfaceIndexerReturnTypeTests`、`ArrayIndexerAccessTests`（均在 [tests/CSharpToJava.Tests](file:///d:/code/cs2j/tests/CSharpToJava.Tests)）：member-desugar 默认关期间必须**持续全绿、零改动**（这些测试直连 ConversionPipeline，不经过预处理器）；默认开评审时再按 M6 统一处置预期，不在本 pass 内迁就旧断言改测试。

### 7.4 residual 闸门与一致性/CLI 测试

- residual：新增闸门测试——构造含未覆盖索引器形态（如 extern）的样例，断言 MD150 计数与 `--strict` 退出码语义；干净样例 residual=0；
- 一致性：`ProjectPreprocessorsConsistentTests` 不断言新 pass（无新 pass）；若 C11 已加 member-desugar 段，仅在其统计断言内补索引器分项；
- CLI：在 C11 的 member-desugar CLI 测试类中追加索引器场景（`--enable-pass member-desugar` 跑索引器夹具、`--list-passes` 不出现新 id、族摘要含索引器行、MD120/MD150 输出与退出码）；不新增独立 verb。

### 7.5 验证命令

```powershell
dotnet build CSharpToJavaConverter.slnx -c Release
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj -c Release
```

## 8. 默认开启验收门槛

随 member-desugar 族统一门槛（由 C11 定义：中间树 build/test 全绿、csharp-sdk 基线不退化、指纹/回滚/落盘三不变），Indexers 规则附加项：

1. 7.1 单测矩阵全绿；7.2 fixture 的 Release `build` **与** `test` 退出码 0；
2. residual 闸门：训练集/夹具中**源内**索引器 plan 覆盖率 100%（跳过项必须全部有 MD120 显式原因，无「静默未处理」）；外部索引器用点零改写经 MD110 抽样核对；
3. 等价性专项：副作用索引实参单次求值（行为测试，非文本断言）；显式双接口分派走通接口调用；抽象/虚链中间树测试钉住；
4. 幂等：二次运行零 diff；
5. **转换器对接前置确认**：M6 影响面（get/set/put → getItem/setItem 的 Java 形态变化、compat 库既有调用方）经评审并有跟进 PR 排期，或 C11 已确定 member-desugar 长期默认关策略——二者满足其一前，Indexers 随族保持默认关（D18）。

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M0 | C11 锚点合入/对齐：确认规则接缝、族 options/统计段/overrides/诊断码段（§3.2、Q1/Q2）；C12 分支 rebase | 文档/协调 |
| M1 | IndexerAnalysis + IndexerPlan + IndexerNaming：源内索引器盘点、用点绑定分类、命名与碰撞、TouchedFiles；单测覆盖分析表（不重写） | Core ~400 行 |
| M2 | IX-1~IX-5 声明归约（单参/多参/显式接口/虚抽象/init/ref/特性）+ 单测 | ~450 行 |
| M3 | IX-6~IX-12 用点全改写（读/写/复合自增单次求值/null 条件/初始化器/强转限定）+ MD1xx 诊断 + 单测主体 | ~700 行 |
| M4 | 族回滚接线验证（OwnsTouch/MD120/force/skip）、fixture、跨工程用点、residual 闸门、幂等、CLI 增补；Release build/test 全绿 | ~300 行 + fixture |
| M5 | sdk 索引器形态补扫（命中率/外部类型分布/`[IndexerName]`/DefaultMember 盘点）、文档交付与 §8 门槛评审 | 扫描/文档 |
| M6 | （独立排期，默认开前置）转换器侧收口：评估 `IndexerTransformer`/`ElementAccessTransformer`/`AssignmentTransformer` 索引器分支在预处理路径上的冗余下掉或重定向，确认 getItem/setItem 与 compat 库对接；处置 §7.3 旧回归预期 | 独立 PR |

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **Java 产物形态变化**：源内索引器从 `get/set/put` 变为 `getItem/setItem`，可能与 compat 库及既已生成 Java 的手工调用方失配；字典 `put` 返回旧值语义无 C# 对应 | D18；IndexerTransformer L52-57/L184-185 | 默认关 + §8 第 5 条；M6 独立收口；不试图在 C# 侧保留 put 语义 |
| R2 | 副作用索引实参的双求值：临时提升改变语句结构，漏提即静默语义错 | 今日转换器双求值（AssignmentTransformer L99-109） | D11 白名单 + 行为测试（非文本）；不可提升即 MD120，禁止凑合 |
| R3 | 外部/源内误判：项目引用经 DLL 元数据参与编译时 `IsInSource` 可能为假，跨工程用点漏改 | 单编译近似模型与 JK/RR 同源 | §7.2 跨工程 fixture 实证；漏改由整树编译/中间树测试暴露（方法不存在即编译错并触发回滚/MD120），不产出半残树 |
| R4 | 显式接口/协变/多接口组合复杂：强转限定插错位置改变分派；协变返回在 Java 侧虽合法但需验证 | 今日字符串匹配兜底反复回归（InterfaceIndexerReturnTypeTests 等） | 一切以绑定符号为准；协变显式划归 C46，首期 MD120 或保持原签名策略（评审定） |
| R5 | 表达式树/dynamic：改写改变表达式树节点 | catalog §4 风险 8 | D15 共享判定 + plan 级跳过 + force 语义 |
| R6 | 反射/字符串契约：`[DefaultMember]`、`GetProperty("Item")`、XAML/JSON 按名绑定、COM `[IndexerName]` | catalog §4 风险 7；全仓零处理现状 | D14 文档化限制 + overrides；nameof 无此形态；不承诺运行时一致 |
| R7 | 与 C31 的顺序未在注册表强约束：若 C12 先把 `[^1]` 搬入 `GetItem(^1)`，C31 的用点识别必须覆盖方法实参位置 | catalog L632-635；两 pass RunAfter 无直接边 | D10 实参透传使两种先后顺序都可编译；联合 fixture 钉死；两族都落地时补一条 RunAfter 边（倾向 index-range 先于 member-desugar，见 §11） |
| R8 | C11 锚点延期或接缝形态变化导致返工 | 本任务零共享文件编辑权（§3.2） | M0 先对齐；规则接口保持最小（plan + 两 rewriter + 统计段）；接缝假设集中在 Q1，易适配 |
| R9 | 整树多轮重编译拉长链耗时 | catalog §4 风险 11 | 无索引器工程零触碰（clean 字节复制）；Analyzer 一次建表；verify 可关；指纹缓存复用 |
| R10 | init-only 语义弱化：SetItem 可在任意时点调用 | D8 | 与 C11 同策略同诊断；中间树目标是 Java 可译而非完整保留 C# 访问期约束 |

## 11. 并行协调

| 相邻工作 | 边界 | 合并顺序/依赖 |
|---|---|---|
| **C11 Property（族锚点）** | C11 拥有 member-desugar pass、共享基建与 stub 翻转；C12 只加 `Indexers/` 规则 | **硬依赖，先合入**（M0）；§3.2 文件所有权；命名/回滚/统计接缝以 C11 为准 |
| **C13 Event / C14 Operator（同族）** | 各自规则目录 `Events/`、`Operators/` 与本规则并列；共享 Analyzer/回滚/命名服务，互不编辑他族目录 | 并行无语法面争抢；注册发现机制由 C11 统一；码位段 1xx/2xx/3xx 预先登记（Q2） |
| **C31 index-range（`index-range`，RunAfter array-method）** | C31 归约 `^`/`..` 表达式使用点（含数组/string/自定义 Index 索引器/切片）；C12 归约索引器**声明与成员形态**，实参原样透传（IX-11） | 无直接 RunAfter 边；两种落地顺序都产出可编译树；联合测试 `c[^n]`；两族都在链时倾向补边 `index-range` → `member-desugar`（先消表达式糖再方法化，减少他族特判），最终由注册表评审定 |
| **E12 array-method** | E12 处理多维数组/数组静态方法并注入 Cs2jArray；数组元素访问不绑定 `IPropertySymbol`，C12 闸门天然不命中（D4） | member-desugar RunAfter struct/closure（晚于 array-method 现状位）；无交叠；保留一条「数组 vs 多参索引器并存」联合用例 |
| **C17 复合赋值/自增（numeric-boundary 族，更晚）** | C17 做通用复合目标读改写归约；catalog L404 注明复合/自增依赖 indexer 先方法化 | C12 先把索引器复合点展开为方法调用序列（D11 已保证单次求值）；C17 落地后把索引器分支视同普通方法调用，不再需要索引器特例 |
| **C01 ref-out-in** | ref 返回索引器的 Java holder 映射走 C01 既有通道；C12 在 C# 中间树保留 `ref T GetItem` | member-desugar RunAfter 含 closure-lift，与 ref-out-in 先后按注册表现状（ref-out-in 已在链中）；联合用例 `ref var x = ref c[i]` |
| **C46 成员收口（同族未来项）** | 显式接口/new 隐藏/协变返回的最终收口 | C12 首期不处理协变索引器（R4）；留出 C46 规则位 |
| **C08/E17 java-keywords（链尾）** | `GetItem/SetItem` 非 Java 保留字；生成方法名/形参名若撞保留字由 C08 全符号改名表统一覆盖（含 `value` 等形参） | C12 在前、C08 在后；索引器无 nameof 项，与 JK-4a 无冲突 |
| **C20 partial-merge** | 索引器符号经 SemanticModel 天然跨 partial 部件合并，不依赖语法合并 | 无硬依赖；partial-merge 落地后仍以符号为键 |
| **注册表/工厂** | `PassChainFactory`/`PassCatalog`/`PassStubCatalog` 目标零改动（§3.1、§5.2） | 全部注册表变更归 C11 PR；本分支不翻转 stub |

## 12. Worktree 分配

- 分支：`feature/preproc-c12-indexer-desugar`
- 目录：`.worktrees/preproc-c12-indexer-desugar`
- base：`f68a783c`（`feat(preprocessing): register 23 merged-family stub passes`）
- **实现前置动作**：worktree 创建后，先把 C11 Property 分支（`feature/preproc-c11-property-desugar`，名称以 C11 实际分支为准）merge/rebase 进来，确认 `src/CSharpToJava.Core/MemberDesugar/` 共享骨架与 member-desugar Implemented 翻转已在树中，再开始 M1；C11 未就绪期间只允许产出/评审本文档，不提交实现代码。

```powershell
git worktree add .worktrees/preproc-c12-indexer-desugar -b feature/preproc-c12-indexer-desugar f68a783c
```

---

## 附：开放问题（实现前需锚点/评审确认）

- **Q1 规则接缝形态**：C11 的规则组合机制（`IMemberDesugarRule` 显式注册 vs 程序集发现）与共享 Analyzer/Plan/命名服务的最终 public 形状；本设计全部组件名按假设命名，以 C11 设计文档定稿为准对齐。
- **Q2 诊断码位与 overrides 文件名**：MD1xx 子段与 `config/MemberDesugarOverrides.json`（或等价物）需 C11 在族级统一登记，避免与 C13/C14 撞码。
- **Q3 getter/setter 最终命名与 Java 对接路线**：`GetItem/SetItem`（→ `getItem/setItem`，D1 推荐）是否作为族定稿；若 M6 评审认为必须保今日 `get/set/put` 字面形态，需回退评估备选 B 及其碰撞代价——该决策影响 §8 第 5 条与 M6 排期。
- **Q4 跨工程用点编译模型**：runner 单编译近似下，项目引用以元数据参与时源内符号可见性边界，需在 7.2 fixture 与真实多工程解决方案上各验证一次（R3）。
- **Q5 协变返回索引器**：首期保签名归约（依赖 C# 编译器判定覆盖合法性）还是 MD120 跳过；与 C46 的收口分工。
