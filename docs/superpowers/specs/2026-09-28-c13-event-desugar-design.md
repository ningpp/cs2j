# C13 Event 去糖预处理器设计

**日期**: 2026-09-28
**范围**: 在 `member-desugar` 族 pass 下新增 **Events 规则集**（Core `MemberDesugar/Events/`），把 C# `event`（字段式 / 自定义 add/remove / 显式接口实现 / virtual/abstract）声明改写为「委托后备字段 + Add/Remove 方法」，并把 `+=`/`-=`/触发/空判定/`nameof` 等全部使用点统一改写为语义等价的可编译 C#；本任务**不新增 pass、不注册/翻转 stub、不改 CLI 注册表**
**状态**: 设计待评审；实现依赖 C11 Property 锚点任务合入
**Catalog**: C13（P2，分类 1.4 成员）[preprocessor-catalog.md L407-L417](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L407-L417)；族归属「成员去糖」C11+C12+C13+C14+C46（[L898-L904](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L898-L904)）；风险 7 反射/字符串契约（[L926](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926)）、风险 9 实验 Lowering 非资产（[L928](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L928)）；并发背景见 §1.5（[L71-L72](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L71-L72)）
**注册表现状**: `member-desugar` 是已登记 stub——[PassStubCatalog.cs L191-L200](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L191-L200)，Stage=`Declarative`，RunAfter=`{struct-value-semantics, closure-lift}`，目录 `.cs2j-member-desugar-src`，`catalogItems="C11, C12, C13, C14, C27, C39, C46"`，默认关。stub 的**翻转由锚点任务 C11 完成**；C13 只在翻转后的族 pass 内追加规则

## 1. 背景与目标

### 1.1 现状：event 的全部处理内联在 C#→Java 转换器，且分落在五个文件

Java 语言没有 event 概念，当前转换器直接把 C# event 语法在 Java emit 期映射为「监听器列表 + add/remove/fire 方法」：

1. **声明侧**：[EventFieldTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs)（catalog 记 447 行，当前工作区实测 492 行）处理字段式与显式事件，产出 `CopyOnWriteArrayList<ListenerType>` 后备字段（[L196-L205](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L196-L205)）、`addXxxListener/removeXxxListener`（[L207-L223](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L207-L223)）、`fireXxx` 遍历发射（[L225-L233](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L225-L233)）；后备存储判定（[L15-L61](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L15-L61)）、接口限定名（[L69-L97](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L69-L97)）、`EventHandler`/`EventHandler<T>`/自定义 delegate 的监听器签名推导（[L300-L405](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L300-L405)）。
2. **命名约定**：[EventListenerNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventListenerNaming.cs)——`_xxxListeners` / `addXxxListener` / `removeXxxListener` / `fireXxx`；接口事件或隐式实现接口事件用接口名限定（`addIViewerMouseDownListener`），避免 Java 同擦除重载碰撞（[L22-L72](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventListenerNaming.cs#L22-L72)）。
3. **订阅点**：[AssignmentTransformer.cs L58-L155](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L58-L155)——`+=`/`-=` 按 `IEventSymbol` 分流：外部对象走 `add/removeXxxListener`，声明类型内部直接操作后备列表。
4. **触发点**：[StatementTransformer.ExpressionAndReturn.cs L323-L333](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L323-L333)——`E?.Invoke(...)` → `if (!_xxxListeners.isEmpty()) { fireXxx(...); }`；空判定 `E == null` → `_xxxListeners.isEmpty()`（[BinaryExpressionTransformer.cs L196-L207](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L196-L207)）。
5. **类型壳**：[ClassTransformer.cs L374-L379](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L374-L379)、[StructTransformer.cs L298-L306](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs#L298-L306)、[InterfaceTransformer.cs L213-L224](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs#L213-L224)（接口只发抽象 add/remove 签名）。

另有 [EventHandlerLambdaRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/EventHandlerLambdaRewriter.cs) 在 Java IR 层修补 `Consumer<T>` 误判（应为 `BiConsumer<Object,T>`）——同类问题在声明侧与订阅侧两处各修一遍，是语法糖未前置的典型重复成本。

**已锁定的行为测试**：[EventDelegateInvokeMethodTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/EventDelegateInvokeMethodTests.cs)（SAM 名 run/accept/apply；自定义事件转发 delegate 字段走 `DelegateHelper.combine/remove`）、[InterfaceEventListenerNamingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterfaceEventListenerNamingTests.cs)（接口限定名、后备存储跳过重集）、[EventHandlerEventArgsMappingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/EventHandlerEventArgsMappingTests.cs)、[EventAndStaticPropertyRegressionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/EventAndStaticPropertyRegressionTests.cs)。

### 1.2 field-style event 与 custom event 的 C# 语义

| 维度 | field-style `event D E;` | custom `event D E { add; remove; }` |
|---|---|---|
| 后备字段 | 编译器生成 private delegate 字段（名同事件名） | **不生成**；用户自持（常见转发到一个 private field-style event 或 delegate 字段） |
| add/remove | 编译器生成；修饰符随事件 | 用户编写；形参固定名 `value`；两访问器可各带可见性 |
| 声明类型内引用 | `E` 直接绑定后备字段：可 `E?.Invoke`、`E(...)`、`E = null`、`E == null`、类内 `E += h`（**直接改字段，绕过虚访问器分派**） | 只能 `+=`/`-=`（始终走访问器，类内类外皆然）；不能 Invoke |
| 类外引用 | 仅允许 `+=`/`-=`（走 add/remove 访问器，虚分派） | 同左 |
| abstract / interface | `abstract event D E;` 只有抽象 add/remove、无字段；接口事件同理 | abstract accessor 只在抽象类；接口事件无 accessor 体语法 |
| virtual/override/sealed/new | 修饰符作用于两个访问器；field-style `override` 自带一份新后备字段 | 同左；accessor 级修饰 |
| struct | 允许（实例后备字段；访问器非虚；**从不加锁**） | 允许 |
| 静态 | 静态后备字段 + 静态访问器 | 同左 |

**线程安全订阅语义（§10 的论证基础）**：

- C# 1.0/2.0 编译器为 field-like event 生成**带锁**访问器：实例事件等价 `lock(this)`、静态事件等价 `lock(typeof(T))`；struct 从不加锁。
- **C# 3.0 起锁被移除**（Roslyn 至今）：生成代码是无锁的 `E = (D)Delegate.Combine(E, value)` / `Delegate.Remove`，并发订阅存在丢更新窗口，语言不再保证订阅线程安全；需要保证由用户自写 custom accessor（`lock` 或 `Interlocked.CompareExchange` 循环）。
- 触发侧的标准线程安全惯用语是**捕获到局部变量**：`var h = E; h?.Invoke(...)`（一次字段读取，避免「判空后被退订」的 TOCTOU）；C# 6 的 `E?.Invoke(...)` 同样只读一次字段。
- 零订阅时字段为 `null`：`E(args)` 抛 `NullReferenceException`；`E?.Invoke(args)` 无操作。多播链是**不可变**对象：Combine/Remove 返回新链，进行中的触发读到的是旧链快照；`Delegate.Remove` 按委托相等性移除**最后**一段匹配调用表。

### 1.3 Java 目标形态与本特性的关系

Java 现状形态（[EventFieldTransformer.cs L196-L233](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L19-L233)）：

```java
private final java.util.concurrent.CopyOnWriteArrayList<BiConsumer<Object, EventArgs>> _clickListeners =
    new java.util.concurrent.CopyOnWriteArrayList<>();
public void addClickListener(BiConsumer<Object, EventArgs> handler) { _clickListeners.add(handler); }
public void removeClickListener(BiConsumer<Object, EventArgs> handler) { _clickListeners.remove(handler); }
protected void fireClick(Object sender, EventArgs args) {
    for (var _handler : _clickListeners) _handler.accept(sender, args);
}
```

实验 IR [LowerEvent.cs L35-L47](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerEvent.cs#L35-L47) 是同形降级（`add/remove/...Listeners.forEach`），但按 catalog §4 风险 9（[L928](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L928)）**仅借形态、不共用代码**（Lowering 只被 Java2 对比测试引用，生产不可达）。

本特性的 C# 目标形态（catalog L412「委托字段 + AddX/RemoveX/raise 方法」的落地形）采用**与上述 Java 命名一一对齐**的去糖，使后续转换器清理 PR 只需「按命名约定识别去糖成员」而非再认 event 语法：

```csharp
// 输入
public event EventHandler<EventArgs>? Click;
protected void RaiseClick(EventArgs e) => Click?.Invoke(this, e);
// 类外：c.Click += OnClick;   c.Click -= OnClick;

// 输出（默认 Simple 订阅模式）
private EventHandler<EventArgs>? _clickListeners;
public void AddClickListener(EventHandler<EventArgs>? value)
    => _clickListeners = Cs2jPrelude.CsPrelude.Combine(_clickListeners, value);
public void RemoveClickListener(EventHandler<EventArgs>? value)
    => _clickListeners = Cs2jPrelude.CsPrelude.Remove(_clickListeners, value);
protected void RaiseClick(EventArgs e) => _clickListeners?.Invoke(this, e);
// 类外：c.AddClickListener(OnClick);  c.RemoveClickListener(OnClick);
```

要点：

1. 后备是**真 BCL delegate 字段**（不是合成列表类型），事件的多播/空值/异常/返回值语义在 .NET 半区可直接用源项目测试验证（catalog §5.1 两半区方法论）。
2. 命名直接复刻 [EventListenerNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventListenerNaming.cs) 约定（`_xxxListeners` / `AddXxxListener` / `RemoveXxxListener`，接口限定同步），下游零改名。
3. Combine/Remove 复用 E05 delegate pass 的规范产物 [Cs2jPrelude.CsPrelude](file:///d:/code/cs2j/src/Cs2jPrelude/CsPrelude.cs)（Java 半区已映射 compat [DelegateHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java)），**不自造第二套委托组合机制**。
4. 不合成 `FireXxx`/`OnXxx` 方法：触发只在声明类型内部合法，去糖后对 delegate 字段直接调用即可；合成虚 `OnXxx` 会平白引入一条虚分派链、改变派生类行为。
5. E05 delegate pass 在 [DelegateSyntaxRewriter.cs L173-L176](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L173-L176) **显式跳过左值为 `IEventSymbol` 的 `+=`/`-=`**——事件订阅点是 C13 的明确预留入口，两 pass 边界无重叠。

**硬不变式**（与 C11 族契约一致）：中间源码树预处理后永远可编译（整树重编译相对基线无新增错误）；事件计划是原子改写单位（声明 + 全部使用点全改或全不改）；禁止注释/占位/半改流入下一环节。

## 2. 关键设计决策表

| 决策点 | 结论 | 备注 |
|---|---|---|
| 总体目标形态 | field-style event → private delegate 后备字段 `_xxxListeners` + `AddXxxListener/RemoveXxxListener` 两方法；custom event → 仅两方法（accessor 体原样搬入，形参保留名 `value`，零 body 改写）；全部使用点按符号绑定改写 | 命名复刻 EventListenerNaming；后备字段用 BCL delegate 类型，不用合成列表/支撑类型 |
| 命名派生 | 字段 `_<decap(Name)>Listeners`；方法 `Add<Name>Listener`/`Remove<Name>Listener`；接口限定形与 [EventListenerNaming.cs L22-L72](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventListenerNaming.cs#L22-L72) 完全同规则（`_<decap(Iface)><Name>Listeners` / `Add<Iface><Name>Listener`），接口侧与实现侧**同步限定**保持可实现关系 | 限定逻辑移植到 C# 侧 `Events/EventNaming.cs`，符号判定真相源 |
| 订阅线程安全默认 | **Simple**：`field = CsPrelude.Combine/Remove(field, value)`——与 Roslyn（C# 3.0+）field-like event 实际代码生成严格一致 | .NET 半区测试可证语义等价；并发订阅不保证是现 C# 语义而非退化，见 §10 |
| 订阅线程安全选项 | `SubscriptionThreadSafety.InterlockedCompareExchange`：add/remove 发射 `Interlocked.CompareExchange` 重试循环（lock-free、线性化），每事件出 MD202 Info | 对齐 Java 现状 `CopyOnWriteArrayList` 的订阅安全性强度；`lock(this)` 历史形**不复刻**（反模式，lock 归一归 C29） |
| struct 事件 | 一律 Simple；选项要求 CAS 时出 MD213 并降级 Simple（`this` 可能是 readonly 拷贝/装箱副本，CAS 不可证） | C# 编译器对 struct 事件也从不加锁 |
| 零订阅触发 NRE | 保持 C# 语义：`E(args)`/`E.Invoke(args)` → 字段直接调用（null 抛 NRE）；`E?.Invoke(args)` → 字段条件调用（无操作）；**不**引入 isEmpty 包装 | isEmpty 语义是 Java 列表形态的下游事，由后续转换器清理映射 |
| 类内 +=/-= | field-like 事件：直接改后备字段（CsPrelude.Combine/Remove），**复刻「类内绕过访问器虚分派」**；custom 事件：仍调本类型 Add/Remove 方法 | 符号 + 含入类型相等判定（partial 跨文件同类型按 `SymbolEqualityComparer` 合并），参照 AssignmentTransformer 现状分流 |
| 类外 +=/-= | `target.AddXxxListener(h)` / `target.RemoveXxxListener(h)`；接收者/方法组/lambda 原样保留（方法组到 delegate 形参的转换是 C# 原生） | 显式接口实现经接口引用：`((IFoo)x).AddFooListener(h)` |
| 触发点 | 仅声明类型内可出现；事件名引用整体替换为后备字段名，其余文本不动（`_f?.Invoke(...)` / `_f(...)`） | 不合成 Fire/On 方法；非 void delegate 的返回值语义因此不丢 |
| 空判定/重置 | `E == null`/`!= null`/`= null`/`= expr`（仅类内合法）替换为后备字段上的同形表达式 | 下游转换器不再需要 `TryTransformEventNull` 特例 |
| virtual/abstract/override/sealed/new/static | 修饰符按语义搬到 Add/Remove 两方法；abstract/interface 事件只出抽象方法签名、无字段；field-style override 出 `override` 方法 + 私有后备字段；`new` 隐藏保留 `new` | 现状转换器不传播 abstract/virtual（EventFieldTransformer 硬编码 public 实例方法），前置化同时修此缺口 |
| 显式接口实现 | `event D IFoo.E {...}` → `void IFoo.AddFooListener(D value)` / `void IFoo.RemoveFooListener(D value)`（显式实现，无访问修饰符） | 接口事件本身同步改限定名方法对 |
| custom accessor 可见性 | `add`/`remove` 各自的访问修饰符独立搬运；默认取事件可见性 | field-style 无 accessor 表，两方法同可见性 |
| 字段初始化器 | `event D E = Init();` → 后备字段初始化器原样保留；多变量声明 `event D E1, E2;` 拆为两组成员 | |
| attribute | 无 target 的特性默认属事件（方法对）；`[field:]` 归后备字段；`[method:]` 归两方法 | 不支持的 target 组合出 MD210 跳过，不静默丢特性 |
| 与 E05 delegate pass 衔接 | 事件字段/形参的 delegate 类型语法原样保留（delegate 类型声明由主转换器消费）；字段组合调用统一用 `Cs2jPrelude.CsPrelude.Combine/Remove`；EventRule 经规则接缝声明需要 Cs2jPrelude，由族适配器保证在场（见 §3.2、§5、附 Q1） | E05 对 `IEventSymbol` 的跳过行是边界契约，C13 落地后不得删除 |
| `nameof(E)` | 绑定 `IEventSymbol` 的 nameof（含跨类 `nameof(C.E)`）替换为原名字符串字面量 `"E"`，与 JK-4a 同策略 | 去糖后类外不再有名为 E 的成员，不字面量化会直接编译失败 |
| 反射/字符串契约 | 不改写：`GetEvent("E")`/`AddEventHandler`/XAML/JSON 按名订阅静态不可见，属文档化限制（§6、§10） | 沿用 catalog §4 风险 7 的统一限制声明；提供 skip 名单 |
| dynamic / 表达式树 | dynamic 接收者的订阅点不可判定 → 事件计划原子跳过 + MD211；表达式树内不允许事件订阅语法（C# 无此节点），残余扫描兜底 | 若遇绑定不可解析站点一律显式诊断 |
| WinRT 事件 / static abstract 接口事件 | `IEventSymbol.IsWindowsRuntime` 与接口 `static abstract`/`static virtual` 事件 v1 不支持 → MD210 整事件跳过 | WinRT 访问器代码生成与托管模型不同；static abstract 无具体实现体 |
| 计划粒度与回退 | 每个 `IEventSymbol` 一条 `EventPlan`（partial 多部件按符号合并）；含任一不可行站点 → 声明与全部站点原子不动；整树重编译新增错误归属回退（机制随 C11 锚点） | 命名碰撞（Add/Remove/字段派生名被既有成员占用）v1 即 MD210 跳过，不做即兴改名；与 C11/C12 的确定性序号消解策略是否拉齐列附 Q3 |
| 转换器代码 | 本特性 PR **不动** C#→Java 转换器；§5.4 Java 半区消费清单是默认开门禁，删除旧事件特例留后续 PR | 与 C08/C09 同范式 |

## 3. 链位与族契约

### 3.1 归属 `member-desugar`，不新增 pass、不翻转 stub、不动 RunAfterIds

- C13 没有自己的 descriptor/adapter/中间目录/CLI 开关。`member-desugar` stub 由 C11 锚点在 [PassStubCatalog.cs L191-L200](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L191-L200) 删除并在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 注册为 Implemented；C13 合入时该 pass 已存在，事件规则随族执行器一并生效。
- **Stage = Declarative**（沿用 stub 现登记值，不改）；`RunAfterIds = { struct-value-semantics, closure-lift }` **不动**（与 C12 链位承诺一致）；`SupportLibrary=None` 沿用，Cs2jPrelude 在场保证走规则接缝（§3.2 第 2 点、附 Q1）。
- **目录/指纹/保留目录/verify** 全部沿用族 pass 的一份：`.cs2j-member-desugar-src`，由 C11 锚点建立；C13 不新增中间树环节（catalog §4 风险 11 的成本约束）。
- stub 当前 `catalogItems` 已含 `C13`，无需改注册表元数据。
- C13 规则开关只存在于族 options 内部：事件子节（Policy/Strict/SkipEvents/ReportInfo），族 pass 启用时事件规则默认 active；`--strict`、skip 名单等经族 options 透传。在 C11 翻转前，启用 `member-desugar` 仍是 CS2J9001——C13 的端到端测试依赖 C11 先合（§11）。

### 3.2 对 C11 锚点的依赖与文件所有权约定

实施前先把 C11 分支 rebase/merge 进 C13 分支（§12），在 C11 已落地的 `MemberDesugar/` 骨架上添加 `Events/`。与 C12 §3.2 同款所有权约定：

| 文件/目录 | 所有任务 | C13 行为 |
|---|---|---|
| [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) / [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) / [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) | **C11 独占** | 只读引用，不编辑 |
| `Adapters/MemberDesugarPassAdapter.cs` | **C11 独占** | 不新增适配器；事件规则的统计/诊断/支撑库需求经规则接缝暴露，由 C11 适配器消费 |
| `src/CSharpToJava.Core/MemberDesugar/` 根下共享文件（Preprocessor/Models/Analyzer/Plan/SyntaxFacts/Naming/FormatAndValidate 等） | **C11 所有，C13 只读复用** | 不在共享文件内写事件特例；共享命名/碰撞/标注能力不足时由 C11 接受 PR 或合入后补 |
| `src/CSharpToJava.Core/MemberDesugar/Events/**` | **C13 独占** | 本任务全部新增代码落此目录 |
| `Properties/**`、`Indexers/**`、`Operators/**` | C11 / C12 / C14 各自独占 | 不触碰 |
| 测试：`tests/.../MemberDesugarEvent*`、共享 fixture 中 `Events*` 文件 | C13 | 共享 fixture 解决方案由 C11 建目录，C13 只新增事件夹具文件（§7.2） |

接缝要求（写入 C11 设计的锚点契约，本设计假设其成立；形态若不同以 C11 为准做同名适配，列附 Q1）：族 Preprocessor 以规则集组合，每条规则实现统一接缝（C12 建议名 `IMemberDesugarRule`）：贡献 plan、声明侧 rewriter、使用点侧 rewriter、统计段与摘要片段、`OwnsTouch` 归属谓词、（C13 新增请求）**支撑库需求声明**。C13 只交付规则实现与一个位于独占目录内的注册点（`Events/EventRuleRegistration.cs`，发现机制由 C11 定）。

两点功能依赖：

1. **共享基建**：全编译符号表、成员命名/碰撞工具、`MemberPlan` 基类（TouchedFiles/OwnsTouch/Outcome）、`FormatAndValidate` 三候选、族 RewriteAnnotation、整树 Analyze→Plan→逐文件 Rewrite→回退框架均由 C11 建立。
2. **Cs2jPrelude 在场**：类内组合点（EV-11）发射 `Cs2jPrelude.CsPrelude.Combine/Remove`（[CsPrelude.cs](file:///d:/code/cs2j/src/Cs2jPrelude/CsPrelude.cs)，Java 半区已映射 compat [DelegateHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java)）。默认链中 `delegate` 早于 member-desugar 执行，支撑工程已由 E05 适配器释放（[DelegatePassAdapter.cs L84-L109](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/DelegatePassAdapter.cs#L84-L109)）；C13 **不自行改 RunAfterIds**，而是经接缝声明规则需要 Prelude，由族适配器保证在场（用户同时禁用 delegate 等边角的处理方式由 C11 锚点定，见附 Q1）。

### 3.3 硬不变式

1. 中间源码树预处理后永远可编译（整树重编译相对基线无新增 error；警告不比对）。
2. 每个事件 plan 的改写原子：声明 + 全部使用点全改或全不改；回退单位是单个事件 plan，不影响同文件的属性/索引器 plan。
3. 幂等：对已去糖树再跑一次 member-desugar，Events 规则零改动（已无 `EventFieldDeclarationSyntax`/`EventDeclarationSyntax`；订阅点只认 `IEventSymbol` 绑定，去糖后绑定的是方法/字段符号）。

### 3.4 中间树与支撑库

- 事件改写产生的 `Cs2jPrelude.CsPrelude.Combine/Remove` 调用与 E05 产物同形：支撑工程已被主转换过滤（[Program.cs L794-L798](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L794-L798) 既有机制），指纹快照不含支撑文件——C13 不引入新的 `PassSupportLibrary` 枚举值。
- 干净文件字节级复制（含 BOM 保持）；事件规则不改写不含 event 语法/事件绑定站点的文件。

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/MemberDesugar/Events/`，命名空间 `CSharpToJava.Core.MemberDesugar.Events`。C11 建立的族共享层位于 `MemberDesugar/`（计划/格式化/符号工具），C13 只消费不复制。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `EventDesugarOptions.cs` | `SubscriptionThreadSafety Policy`（Simple/InterlockedCompareExchange，默认 Simple）、`bool Strict`、`bool ReportInfo`、`IReadOnlySet<string>? SkipEvents`（完全限定名）、事件级单文件模式开关 |
| `EventModels.cs` | `EventKind`（FieldLike/Custom/Abstract/Interface/ExplicitInterface）、`EventSiteKind`（ExternalSubscribe/ExternalUnsubscribe/InClassCombine/InClassRemove/RaiseGuarded/RaiseDirect/NullTest/Reset/NameOf）、`EventDiagnostic(MD2xx)`、`EventStatistics`（通用 5 字段投影 + 事件特有计数）、`EventFileResult` |
| `EventNaming.cs` | 移植 [EventListenerNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventListenerNaming.cs) 的 C# 侧命名：接口限定谓词（含 error type 显示串兜底）、字段名/方法名派生、首字符下划线与碰撞边界处理 |
| `EventPlan.cs` | `EventPlan`：`IEventSymbol Event`、`EventKind Kind`、限定名、派生字段名/方法名、`IReadOnlyList<EventSite> Sites`（跨文件，节点文件+span+种类）、`Outcome`（Active/Skipped）、FailCode/FailReason、TouchedFiles、OwnsTouch；**事件是回退最小单位** |
| `EventAnalyzer.cs` | **整树一次**：枚举编译内全部源声明 `IEventSymbol`（EventField/EventDeclaration 两种语法，partial 按符号合并）；分类 Kind 与修饰符（static/virtual/abstract/override/sealed/new/显式接口/接口成员/WinRT）；逐语法树枚举绑定到事件符号的全部站点并分类；产出确定性排序（文件+span）的 plan 列表 |
| `EventDeclarationRewriter.cs` | **阶段一（声明侧）**：EventFieldDeclaration → 字段 + 两方法；EventDeclaration → 两方法（体搬入）；abstract/interface → 抽象签名；显式接口 → 显式实现；修饰符/特性/初始化器/多变量/静态/属性可空标注保持；改动挂族 RewriteAnnotation |
| `EventInvocationRewriter.cs` | **阶段二（使用点）**：`+=`/`-=` 外部订阅/类内组合分流；触发点/空判定/重置替换字段名；nameof 字面量化；只接受语义模型反查命中的节点，禁止文本替换；两阶段在同文件内顺序固定（先声明后站点） |
| `EventResidualScanner.cs` | 重写后整树扫描：残余 `event` 声明数、左值绑定 `IEventSymbol` 的 `+=`/`-=` 数，与 skip plan 集合对账 |
| `EventRule.cs` | `IMemberDesugarRule` 实现（规则 id `"events"`）：族执行器入口，贡献 EventPlan 列表、挂接两个 rewriter、统计段与摘要片段、`OwnsTouch(file, typeName, memberName)` 归属谓词、声明需要 Cs2jPrelude（§3.2、附 Q1）；类型签名遵循 C11 锚点定义的族规则契约（C11 落地前按锚点草案实现，M1 对齐） |
| `EventRuleRegistration.cs` | 规则注册点（位于 C13 独占目录内）：由 C11 的规则发现机制收集——显式 `Add` 一行还是程序集扫描由 C11 定（附 Q1） |

编译构造沿用 runner 标准（`CSharpParseOptions(LanguageVersion.Latest)` + `NETCOREAPP` 符号、`ProjectUnsignedTypePreprocessor.GetBasicReferences()`、`allowUnsafe:true` 的 DLL 编译）；分析与站点分类只查询**原始** SemanticModel。

### 4.2 两阶段数据流

1. **Analyze（整树一次，经族执行器）**：建全部事件 plan。跨文件引用天然闭合：A 文件声明、B 文件 `new A().E += h` 经同一 `CSharpCompilation` 的符号反查进同一 plan（参照 [ProjectPassRunner.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs) 标准骨架提供的每文件 SemanticModel）。
2. **可行性判定（计划期）**：碰撞探测（派生字段名/方法名在同声明空间被占用）、WinRT、static abstract、dynamic 站点、skip 名单——任一命中置 `Skipped + MD2xx`，该事件声明与全部站点不动。
3. **Rewrite（逐文件，每文件两小阶段）**：`EventDeclarationRewriter` → `EventInvocationRewriter`，随后族 `FormatAndValidate`。Changed=false 字节级复制。
4. **ResidualScan（AfterFiles）**：残余 >0 且无对应 skip 诊断 → MD220（strict 升级 Error）。
5. **回退**：中间树重编译相对基线新增错误时，按 TouchedFiles/OwnsTouch 归属唯一事件 plan，族执行器 ExcludePlans 后从原始副本重放；无法归属走族级全回退（码段与流程随 C11 锚点）。稳态预期零回退：声明改写后符号关系由整树编译强保证。
6. **单文件模式**：plan 的 TouchedFiles 超出输入文件 → 排除 + MD290（与 JK090/CS2J1290 同构）。

### 4.3 规则全集（EV-x）

**声明侧**

- **EV-1 field-style 事件（class/record，默认 Simple）**：
  ```csharp
  public event EventHandler<Args>? Click;
  ↓
  private EventHandler<Args>? _clickListeners;
  public void AddClickListener(EventHandler<Args>? value)
      => _clickListeners = Cs2jPrelude.CsPrelude.Combine(_clickListeners, value);
  public void RemoveClickListener(EventHandler<Args>? value)
      => _clickListeners = Cs2jPrelude.CsPrelude.Remove(_clickListeners, value);
  ```
  XML doc/无 target 特性挂两方法；字段 private（+ static 随原修饰）；`new` 隐藏关键字同步。
- **EV-2 InterlockedCompareExchange 模式（选项）**：两方法体替换为 CAS 重试循环（add 示例）：
  ```csharp
  public void AddClickListener(EventHandler<Args>? value)
  {
      var current = _clickListeners;
      while (true)
      {
          var combined = Cs2jPrelude.CsPrelude.Combine(current, value);
          var original = System.Threading.Interlocked.CompareExchange(
              ref _clickListeners, combined, current);
          if (ReferenceEquals(original, current)) break;
          current = original;
      }
  }
  ```
  remove 同构（Combine→Remove）；`Interlocked` 是真 BCL API，.NET 半区可跑竞争测试；每事件 MD202。
- **EV-3 custom event**：不生成字段；两方法签名按 accessor 各自可见性，**形参名保留 `value`**（普通 C# 方法形参可叫 value，accessor 体因此原样保留）；表达式体/块体、trivia、特性保持：
  ```csharp
  public event EventHandler? Changed {
      add { _changedListeners = Cs2jPrelude.CsPrelude.Combine(_changedListeners, value); }
      remove { _changedListeners = Cs2jPrelude.CsPrelude.Remove(_changedListeners, value); }
  }
  ↓ （外层两个方法声明；体内标识符由 EV-7 站点规则改写到后备字段）
  ```
- **EV-4 abstract / interface 事件**：无字段，两抽象方法签名；接口内无修饰符；abstract class 内带 `abstract`：
  ```csharp
  interface IViewer { event EventHandler MouseDown; }
  ↓
  interface IViewer {
      void AddIViewerMouseDownListener(EventHandler value);   // 接口侧同步限定
      void RemoveIViewerMouseDownListener(EventHandler value);
  }
  ```
- **EV-5 virtual / override / sealed**：`virtual event D E;` → 字段 + 两 `virtual` 方法；field-style `override` → 新私有字段 + 两 `override` 方法；`sealed override` → `sealed override`。abstract 基事件的声明类型内不允许触发（C# 源约束），无触发站点要处理。
- **EV-6 显式接口实现**：
  ```csharp
  event EventHandler IViewer.MouseDown { add { BODY_ADD; } remove { BODY_REMOVE; } }
  ↓
  void IViewer.AddIViewerMouseDownListener(EventHandler value) { BODY_ADD; }
  void IViewer.RemoveIViewerMouseDownListener(EventHandler value) { BODY_REMOVE; }
  ```
- **EV-7 字段初始化器 / 多变量 / 静态 / 特性**：初始化器原样保留；`event D E1, E2;` 拆两组；静态事件字段与方法均 static；`[field:]X` 归字段、`[method:]Y` 归两方法、无 target 默认归两方法；不支持的 target 组合 MD210。
- **EV-8 struct 事件**：字段 + 非虚方法，强制 Simple（诊断 MD213）；方法体直接赋 `this` 字段，不引入 CAS/锁。
- **EV-9 不支持种类**：`IsWindowsRuntime` 事件、接口 `static abstract/static virtual` 事件 → MD210 整事件跳过。

**使用点侧**

- **EV-10 外部订阅/退订**：左值绑定 `IEventSymbol` 的 `+=`/`-=` 且站点不在事件含入类型内（含经基类访问派生类、显式接口转型）→ `receiver.AddXxxListener(handler)` / `RemoveXxxListener(handler)`；接收者含副作用时先求值一次（成员访问接收者原样；复杂接收者按共享单次求值基建提升临时量——复用族基建，不自行发明）。
- **EV-11 类内 field-style 组合**：站点含入类型 == 事件含入类型（partial 跨文件算同类型）→ 直接字段组合，复刻绕过虚访问器语义：
  ```csharp
  _clickListeners = Cs2jPrelude.CsPrelude.Combine(_clickListeners, value);   // +=
  _clickListeners = Cs2jPrelude.CsPrelude.Remove(_clickListeners, value);    // -=
  ```
  custom 事件无后备字段，类内 `+=`/`-=` 仍走 EV-10 方法形（自定义访问器递归由用户自负，C# 语义本就如此）。
- **EV-12 触发点（仅声明类型内）**：
  - `E?.Invoke(args)` / `E?.Invoke` 经任何条件访问形态 → `<field>?.Invoke(args)`（一次字段读取语义保持，不插局部变量以外的语句；`?.` 本身即单次读取）；
  - `E.Invoke(args)` / `E(args)` → `<field>.Invoke(args)` / `<field>(args)`，null 时 NRE 语义不变；
  - 捕获局部惯用语 `var h = E; h?.Invoke(...)` 中两处引用一致改写即保持。
- **EV-13 空判定 / 重置 / 类内直接赋值**：`E == null`/`E != null` → 字段比较（**不**改 isEmpty）；`E = null`/`E = expr` → 字段赋值。
- **EV-14 nameof**：`nameof(E)`/`nameof(C.E)`/`nameof(IFoo.E)` 等任一段绑定被去糖事件 → 替换为事件原名常量字符串 `"E"`（取最右段，与 JK-4a 同值规则）。
- **EV-15 不可判定站点**：dynamic 接收者订阅、SemanticModel 反查为空且无语法兜底（接口限定/字段名派生依赖符号）→ 事件 plan 原子跳过 + MD211；不猜测、不局部改写。
- **EV-16 转发/后备存储模式**：private field-style 事件仅被同类型 custom 事件 accessor 引用（[EventFieldTransformer.cs L15-L61](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L15-L61) 现按语法跳过它）时，C# 侧**仍完整去糖**（字段 + private 两方法 + 触发点全保留）——C# 元数据里它本来就是独立事件；下游 Java 清理若要复刻「跳过重集」去重，按命名约定识别即可，C# 中间树不替转换器做裁剪。
- **EV-17 非 void delegate 事件**：声明/订阅照常；触发保持字段 Invoke（多播返回最后一个处理者返回值的 C# 语义不丢）；文档化下游列表 forEach 模型无法表达该语义，Java 清理 PR 须对非 void 后备字段保留 delegate 调用形（§5.4）。

## 5. CLI 集成

C13 **不改 Program.cs、不增适配器、不增开关/verb**。所有 CLI 面由 C11 锚点建立的 `member-desugar` 族适配器承载，C13 只约定事件规则如何汇入：

1. **执行**：族适配器标准骨架（[ProjectPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs) / [ProjectPassRunner.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs)）——整树编译一次、Bind/Analyze、逐文件两阶段 Rewrite、AfterFiles 残余扫描、restore/build/test verify。
2. **选项透传**：族 options 增事件子节（Policy/Strict/SkipEvents/ReportInfo）；通用 `preprocess --pass member-desugar` 与 convert-project 链上执行均经族 options 到达 EventRule，不新增 CLI 参数（确需调试参数时用族 options 的配置文件通道，由 C11 定型）。
3. **统计汇入**：`EventStatistics` 投影通用 5 字段（FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount）进族统计；族摘要行追加事件段，建议形如：
   `member-desugar: … events: <EventsRewritten> rewritten (<field-like/custom/abstract/interface/explicit 分项>), <SubscribeSites> +=, <UnsubscribeSites> -=, <RaiseSites> raise, <PlansSkipped> skipped, <ResidualEventCount> residual`。
   事件特有计数：EventsDetected、FieldLikeRewritten、CustomRewritten、AbstractOrInterfaceSignatures、ExplicitInterfaceRewritten、VirtualOverrideRewritten、StaticEvents、SubscribeSites/UnsubscribeSites、InClassCombineSites、RaiseSitesGuarded/RaiseSitesDirect、NullTestSites、ResetSites、NameofLiteralized、CasAccessors、PlansSkipped（按原因分桶）、ResidualEventCount。
4. **诊断汇入**：`EventDiagnostic` 经族适配器的统一诊断选择器映射为通用 `PassDiagnostic`（Severity/Code/Message/FilePath/Line）；Info（MD201/MD202/MD213）仅 verbose 输出，与既有适配器一致。
5. **Cs2jPrelude 在场保证**：EventRule 经规则接缝声明「有事件改写时需要 Cs2jPrelude」，由 C11 族适配器统一保证支撑工程在场（复用 E05 已验证的 PreludeSupportFiles 释放 + ProjectReference 接线机制，具体接缝形态见附 Q1）；C13 不内联第二份释放逻辑、不自行编辑注册表。默认链 `delegate` 先执行已释放支撑工程，此保证主要覆盖适配器组装与边角配置；在场性断言失败即按 restore/build verify 响亮报错。

### 5.4 Java 半区消费清单（转换器后续 PR 的输入，本特性不实施）

默认开启前必须逐项核实/补齐（缺则补转换器，不凭空假设；对齐 C09 §5.4 纪律）：

1. 识别去糖成员约定：delegate 型字段 `_xListeners` + 同类型单参 `AddXxxListener/RemoveXxxListener`（含接口限定形）→ 产出 CopyOnWriteArrayList + add/remove（即把 [EventFieldTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs) 的触发条件从 event 语法换成去糖成员模式）。
2. 类内后备字段上的 `Cs2jPrelude.Combine/Remove` 在「被识别为事件后备字段」时映射为列表 `add/remove`（而非 DelegateHelper 链）；非事件 delegate 字段仍走 DelegateHelper（[EventDelegateInvokeMethodTests.cs L46-L64](file:///d:/code/cs2j/tests/CSharpToJava.Tests/EventDelegateInvokeMethodTests.cs#L46-L64) 断言不能回归）。
3. 后备字段 `_f?.Invoke(...)`/`_f(...)`/`_f == null` → `fireXxx` + isEmpty 守卫（吸收 [StatementTransformer.ExpressionAndReturn.cs L323-L333](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L323-L333) 与 [BinaryExpressionTransformer.cs L196-L207](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L196-L207)）；**非 void delegate 后备字段保留 delegate 调用形**（EV-17）。
4. CAS 模式访问器（`Interlocked.CompareExchange(ref _xListeners, …)`）在列表形态下直接归一为 CopyOnWriteArrayList add/remove（列表自身线程安全）；Simple 模式列表化后订阅安全性等价或强于源 C#（§10 论证）。
5. 删除旧事件特例：EventFieldTransformer、AssignmentTransformer.Event、TryTransformEventNull、条件访问事件分支、Class/Struct/Interface 三个 event case、EventHandlerLambdaRewriter 的事件补丁；§1.1 四个既有事件测试改写为「去糖 C# → Java」断言，名字/形态断言（addIViewer…Listeners、BiConsumer、fire、isEmpty）全部继续成立。

## 6. 诊断策略

事件规则级诊断使用 **MD2xx** 码（MemberDesugar 族内分配给 Events 的子段：0xx 归 C11 Property、1xx 归 C12 Indexers、2xx 归 C13 Events、3xx 预留 C14 Operator——与 C12 §6 的预登记一致，最终码位表由 C11 锚点统一登记确认，见附 Q2；码位只追加不改号），经族适配器统一打印（MSBuild 格式 `文件(行,列): warning/error MD2xx: [事件限定名] 描述`）；族级回退/restore/build/test 失败等公共码由 C11 锚点定义，事件规则只贡献严重级别与归属信息。

| 码 | 级别 | 含义 |
|---|---|---|
| MD201 | Info | 事件已去糖（限定名、Kind、字段名、站点计数），仅 verbose |
| MD202 | Info | 订阅访问器按 InterlockedCompareExchange 模式归一（附与 Simple 的语义差异说明），仅 verbose |
| MD210 | Warning（strict Error） | 事件计划整事件跳过：Add/Remove/字段派生名碰撞、WinRT 事件、接口 static abstract/virtual 事件、不支持的 attribute target 组合、skip 名单命中 |
| MD211 | Warning（strict Error） | 不可行站点：dynamic 接收者订阅、绑定不可解析且无语法兜底；事件计划原子跳过 |
| MD213 | Info（要求 CAS 时升 Warning） | struct 字段式事件不适用 CAS，已降级 Simple |
| MD220 | Warning（strict Error） | residual 闸门：输出残留无对应 skip 诊断的 `event` 声明或事件绑定 `+=`/`-=` |
| MD290 | Warning | 单文件模式：事件计划非自包含（声明或站点跨文件），排除 |

原则（catalog §4 风险 12）：不可判定即显式诊断、整事件不动；**禁止只改声明漏站点、禁止 event 语法原样透传流入下游**；Warning 可经族 `--strict` 升级 Error；每次跳过必须留痕并可与 residual 对账。

**已知限制（文档化，不阻断）**：反射（`GetEvent("E")`/`EventInfo.AddEventHandler`）、XAML/序列化/表达式树外的按名订阅不保证；`nameof` 已按原名保留字符串值，其余字符串契约由用户负责（catalog §4 风险 7）。不保证二进制兼容。

## 7. 测试计划

### 7.1 Core 单测

新增 `tests/CSharpToJava.Tests/MemberDesugarEventTests.cs`（内联源码 → `CSharpCompilation` → 族规则 Bind/Analyze/RewriteFile → 忽略空白文本断言 + `AssertOutputCompiles`，仿 [EnumPreprocessorUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/EnumPreprocessorUnitTests.cs)）：

- **field-style**：无初始化器/带初始化器/多变量拆分；static；`new` 隐藏；字段名/方法名/接口限定名派生逐项断言；
- **custom add/remove**：accessor 体原样（含 `value` 零改写）、两访问器不同可见性、转发到 private field-style 事件（后备存储模式 EV-16，两套成员都在）；
- **显式接口实现**：`void IFoo.AddFooListener` 形态与无修饰符；隐式实现事件与接口事件双向限定名一致、输出可编译（接口方法被实现）；
- **abstract/virtual/override/sealed**：抽象类抽象事件只有签名；派生类 field-style override 带新字段 + override 方法；virtual 事件外部订阅编译为虚方法调用；
- **调用点**：跨文件 `+=`/`-=`（外部 Add/Remove）；类内 `+=`/`-=`（字段 Combine/Remove，断言**不**出现 Add 方法自调用）；`E?.Invoke`/`E.Invoke`/`E(args)` 三形；`E == null`/`E != null`/`E = null`；捕获局部触发惯用语；
- **EventHandler 泛型/非泛型/自定义 delegate**：类型语法（含 `EventHandler<MyArgs>`、自定义多参 delegate、Action）逐字保留；
- **多订阅者语义**（.NET 半区）：`Combine` 后触发顺序、重复订阅/退订后调用表、退订到 null 后 `?.Invoke` 无操作、直接 Invoke 在 null 时抛 NRE；
- **CAS 模式**：输出含 `Interlocked.CompareExchange` 循环且可编译；
- **struct 事件**：Simple 输出 + MD213；
- **nameof**：类内/类外/接口限定三形 → `"E"` 字面量；不涉事件的 nameof 不动；
- **反例/诊断**：派生名碰撞 → MD210 原子不动（声明与站点都不改）；dynamic 站点 → MD211；WinRT 符号构造 → MD210；skip 名单；strict 升级；
- **跨文件**：声明/订阅分两个编译单元，一次 Bind 两边都改；单文件模式越界 → MD290；
- **干净文件**字节级保留、BOM 保持；residual 扫描计数与 skip 对账；回退通道（可测 hook 注入必然编译错误）断言产物与输入一致。

### 7.2 项目级 sln fixture

项目级 fixture 与 C12 同约定：**优先并入 C11 建立的族共享解决方案**（预期名 `tests/SampleMemberDesugarSolution/`，sln+Lib+App，Content 挂入 CSharpToJava.ProjectTests.csproj，net10.0），C13 只新增 `Events*` 前缀夹具文件（如 `EventsBasic.cs`/`EventsInterfaces.cs`/`EventsApp.cs`），与 `Indexers*`/`Properties*` 文件并列。若 C11 合入时族 fixture 尚不存在，M2 先建独立 `tests/SampleEventSolution/`（`SampleEventLib`+`SampleEventApp`+`SampleEventSolution.sln`，按 [SampleRecordSolution](file:///d:/code/cs2j/tests/SampleRecordSolution) 范式），C11/C12 fixture 就绪后评估并入，保证并行开发不阻塞：

- Lib：field-style 事件（EventHandler 非泛型/`EventHandler<T>`/自定义 delegate 各一）、custom add/remove（含转发后备事件）、接口事件 + 隐式实现 + 显式实现（限定名碰撞场景）、abstract/virtual/override 事件层级、static 事件、struct 事件、带初始化器事件、一个碰撞对照事件（断言 MD210 跳过且中间树可编译）；
- App：跨类/跨文件订阅与退订、多订阅者触发顺序、零订阅触发不抛（`?.Invoke`）与直接触发抛 NRE 两组断言、退订后不再被调用；
- **线程竞争用例**：多线程并发对同一 CAS 事件订阅/退订，最终订阅集合与订阅次数断言无丢失；同测试对 Simple 模式只断言功能正确（不断言竞争安全性），锁语义差异在 §10 文档化；
- 断言：`result.Success`、统计计数、中间树**不含** ` event ` 声明与事件绑定 `+=`/`-=`（skip 对照项除外）、含 `_...Listeners`/`Add...Listener`/`CsPrelude.Combine`；`dotnet build -c Release` 退出码 0、`dotnet test -c Release` 全绿；干净树字节级复制与 BOM 用例。

### 7.3 Release build/test 与 sdk 基线

- fixture 与 `D:\csharp-sdk`（只读输入）预处理树分别 `dotnet build -c Release` / `dotnet test -c Release`；sdk 基线 = pre-existing 失败集快照，新增失败为零。
- M0 测量轮：sdk 中 field-style/custom/接口/抽象事件数量比、`+=`/`-=` 站点数、跨文件订阅占比、碰撞/WinRT/dynamic 命中数、CAS 模式需求调研（grep `Interlocked`/`lock` 自定义事件 accessor），作为默认开评审数据。

### 7.4 residual 闸门

对 fixture 与合成样本断言：输出中 `event` 声明数 + 事件绑定 `+=`/`-=` 数 = 有 MD210/MD211 skip 诊断登记的事件站点数；strict 模式 MD220 非零即退出非零。**闸门零裸 event 语法（除登记 skip 项）**。

### 7.5 CLI / 注册表侧

- C11 合入前：不新增 CLI 测试（`--enable-pass member-desugar` 仍 CS2J9001，由既有 stub 测试锁定）；
- C11 合入后（随 C13 M2）：经 `preprocess --pass member-desugar` 验证事件规则默认随族执行、`--list-passes` 中 member-desugar 的 catalogItems 含 C13、摘要行含 events 段、MD290/MD220 在 `--strict` 下的退出码语义、`--disable-pass member-desugar` 时无中间目录；
- 不新增任何 C13 专属注册表断言（无新 pass/目录）。

## 8. 默认开启验收门槛

事件规则**没有独立开关默认态**——随 `member-desugar` 族 pass 默认开启而开启（族 pass 何时由 C11 翻默认开是族级决策）。事件规则在族默认开评审中须额外提供：

1. **中间树正确性**：事件 fixture（族共享解决方案 `Events*` 文件或 SampleEventSolution）与 sdk 预处理树 `dotnet build -c Release` 零错误、`dotnet test -c Release` 全绿（含多订阅者/NRE/竞争用例）；sdk 相对基线新增失败为零。
2. **残余闸门**：sdk 中除显式 MD210/MD211 登记 skip 事件外零裸 event 语法；全部 skip 分类有主，strict 可过。
3. **全仓测试不回归**：`CSharpToJavaConverter.slnx` Release build + 三个测试工程 Release test 全绿（既有红灯数量不增加）。
4. **Java 半区就绪**：§5.4 五项逐项核实/补齐；代表性事件样本（field-style/custom/接口/抽象/虚/EventHandler 泛型非泛型/自定义 delegate）经完整 C#→Java 链路产出可编译 Java，且 §1.1 四个既有事件测试的名字与形态断言改写后继续成立；旧事件分支在去糖产物上不可达（删除仍留后续 PR，但默认开前证明触发不到）。
5. **诊断可运维**：sdk 全量 MD210/MD211 清单经人工 triage，skip 比例有 M0 数据支撑。
6. **文档**：本设计 + 对应 plan（`docs/superpowers/plans/2026-09-28-c13-event-desugar.md`）定稿；转换器删除清单（§5.4）登记后续 PR。

不满足时任随族保持 opt-in（catalog §4 风险 10）。

## 9. 里程碑

C13 在 C11 合入后开工，建议三段提交：

| 里程碑 | 内容 | 验证 |
|---|---|---|
| M1 | `Events/` 骨架（options/models/naming/plan/入口，对齐 C11 族契约）+ EventAnalyzer 全分类 + EV-1/EV-10~EV-14（class field-style + 全部使用点两阶段）+ Core 单测主体 + M0 sdk 测量轮 | 单测全绿；基本样本中间树 build 通过 |
| M2 | EV-3~EV-9 全集（custom/接口/显式接口/abstract/virtual/override/static/struct/初始化器/特性）+ EV-15~EV-17 跳过与边界 + 族适配器统计/诊断/残余接线 + 事件项目级 fixture（含多订阅者/NRE/跨文件，优先并入 C11 族共享解决方案）+ residual 闸门与回退测试 | fixture Release build/test 全绿；sdk 新增失败为零 |
| M3 | EV-2 CAS 模式 + 线程竞争用例 + MD202/MD213 打磨 + sdk 基线 triage + §5.4 Java 半区核查原型（只核查不改正转换器，或转换器清理另开 PR）+ 默认开证据包 | 候选随族默认开评审 |
| M3+（不承诺） | 转换器侧旧事件特例删除与去糖成员识别（§5.4）；与 C46 成员签名显式化联动的事件 new/协变边角；WinRT 事件支持 | 独立后续 PR |

## 10. 风险

### 10.1 锁语义等价性（重点论证）

**事实面**：C# 1.0/2.0 field-like event 的编译器访问器带 `lock(this)`/`lock(typeof(T))`；**C# 3.0 起（Roslyn 至今）无锁**，`Delegate.Combine/Remove` 直接赋字段，并发 `+=`/`-=` 有丢更新窗口；struct 事件从不加锁（§1.2）。

**形态对比**：

| 形态 | 订阅竞争 | 死锁风险 | 与源语义关系 | Java 落地 |
|---|---|---|---|---|
| Simple（默认） | 不安全（同 Roslyn） | 无 | 与现 C# 源码严格等价，.NET 半区可证 | 列表化后为 CopyOnWriteArrayList（**强于**源，单方向加强）或 DelegateHelper 字段（同强度） |
| InterlockedCompareExchange（选项） | lock-free 线性化，无丢更新 | 无 | 强于源 C# 语义（语义归一，MD202 留痕，同 C09 EveryThread 范式） | 列表化后强度一致 |
| 复刻 lock(this)/lock(typeof) | 安全 | 有（公开对象锁、类型锁死锁） | 复刻已被废弃的历史编译器行为 | lock 归一属 C29，本特性不发射 lock |

**等价性结论**：

1. 默认 Simple 是对「今天的 C# 源码」的忠实降级——源语言本就不保证并发订阅安全，不存在语义退化；只有用户**依赖** C# 1.x 历史加锁行为的代码会观察到差异，而该行为 2008 年（C# 3.0）起已不存在。
2. Java 现状 `CopyOnWriteArrayList` 的订阅安全性**强于**源 C#；Simple 经 §5.4 列表化后保持这一强度（CopyOnWriteArrayList.add/remove 自身线程安全），属于良性单方向加强，与当前转换器行为一致，不构成回归；需要在 Java 侧精确复刻「非线程安全」没有业务理由。
3. **触发/多播快照等价**：Combine 产新不可变链，进行中的触发持有旧链引用；CopyOnWriteArrayList 的迭代持数组快照——「触发期间新订阅不被调用、退订仍可能被调用」两者一致；遍历遇首个异常即中止两者一致。
4. **退订身份差异（已知残余）**：`Delegate.Remove` 移除最后一段匹配表；`List.remove` 移除首个 equals 匹配。重复同处理者订阅/退订（`+=h; +=h; -=h;`）两模型都剩一个 h；差异只出现在「多播处理者作为整体退订」等边角，列入 Java 半区核查项，必要时在 compat 侧补尾部移除语义。
5. **null/NRE**：C# null 字段直接触发抛 NRE、`?.Invoke` 无操作；列表模型用 isEmpty 表达同一可观察行为（零订阅无操作）；直接调用形（无 `?.`）经 §5.4 映射时必须保留 NRE 等价（非空守卫路径才插 isEmpty），fixture 双断言锁定。
6. CAS 选项只发射 BCL `Interlocked.CompareExchange`（compat 已有 Interlocked 吸收，catalog §1.5 [L72](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L72)），不引入 lock 语句，故与 C29 无顺序耦合。

### 10.2 其他风险

1. **类内/类外分流正确性**：field-like 事件类内 `+=` 绕过虚访问器、custom 事件类内仍走访问器；partial 跨文件、嵌套类型、派生类访问基类事件都依赖符号含入类型判定。缓解：以 `IEventSymbol` + `SymbolEqualityComparer` 判定，不按语法/文本；原子计划 + 整树编译兜底。
2. **接口限定名双侧同步**：接口方法与实现方法必须同时限定，否则中间树编译失败（方法未实现接口）。缓解：EventNaming 整树分析，接口事件 plan 与实现者 plan 关联，单侧命中即双侧改；整树编译必捕获漏网。
3. **非 void delegate 事件**：Java 列表 forEach 丢弃返回值。缓解：C# 侧始终字段 Invoke 保语义（EV-17）；Java 清理对非 void 后备字段禁用列表 fire 形，列为 §5.4 硬核查项。
4. **反射/字符串契约**（catalog §4 风险 7）：去糖后 `GetEvent`/按名订阅失效；nameof 字面量化保编译与常量契约，其余文档化 + skip 名单。
5. **Cs2jPrelude 在场**：默认链 `delegate` 先执行已释放支撑工程；若用户同时禁用 `delegate` 又启用 member-desugar，Combine 调用可能无支撑工程 → verify build 必失败。缓解：EventRule 经规则接缝声明支撑库需求、族适配器统一保证在场（§3.2、附 Q1），缺失即响亮失败不静默；文档明示该配置组合不受支持。
6. **自定义 accessor 体内任意逻辑**：锁、转发多事件、日志、条件订阅等——策略是零 body 改写（形参保留 `value`），体内事件引用按普通站点规则改写；体内不透明语义不解释，.NET 半区测试直接验证。
7. **static abstract 接口事件 / WinRT**：sdk 若有命中走 MD210 显式跳过，不产出半成品。
8. **实验 Lowering 误导**（catalog §4 风险 9）：[LowerEvent.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerEvent.cs) 的 `Listeners.forEach` 仅借形态命名，不共用代码、不作为 Java fire 语义依据（非 void 等差异以本设计为准）。
9. **热文件冲突**：族共享文件被 C11/C12/C14 并行修改，见 §11。

## 11. 并行协调

| 并行条目 | 关系 | 协调动作 |
|---|---|---|
| **C11 Property（锚点）** | 硬前置：`MemberDesugar/` 共享基建、族执行器、stub 翻转、族 options/统计/回退全由 C11 建立 | C13 在 C11 分支**合入后** rebase 开工；本设计 §4 文件清单按 C11 契约对齐，差异在 M1 开始时一次对齐；Cs2jPrelude 在场保证走规则接缝协商（附 Q1），C13 不自行编辑注册表/RunAfterIds；规则注册点位于各规则独占目录，发现机制（显式注册 vs 扫描）由 C11 定，注册顺序按 C11/C12/C13/C14 固定 |
| **C12 Indexer / C14 Operator** | 同一族 pass、同一 stub、同一共享层；C12 已先行确立文件所有权、码位预登记、共享 fixture 约定，本设计逐项对齐 | Core 目录各自独立（`Properties/Indexers/Events/Operators`——目录名随 C11）；冲突面仅族规则发现/聚合点与族 options/统计聚合：约定各规则自带 options 子节与统计段、注册点放在自己独占目录内，禁止跨规则改别人文件；合并顺序任意，rebase 解追加行冲突；**任何一个都不得单独翻转/注册 stub** |
| **E05 DelegatePreprocessor（已实现）** | 最强相邻边界：E05 已处理 delegate `+=` 组合并预留事件入口（[DelegateSyntaxRewriter.cs L173-L176](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L173-L176)） | 该 `IEventSymbol` 跳过行是契约：C13 落地后不得改 E05；C13 统一复用 CsPrelude Combine/Remove 与 Prelude 支撑工程，不引第二种组合 helper；支撑库在场由族适配器按规则接缝保证（默认链 delegate 先执行）。delegate 类型声明（自定义 delegate）两 pass 都不改写，主转换器消费 |
| **C29 concurrency-normalize（观察项 stub）** | CAS 用 BCL `Interlocked.CompareExchange`；不发射 lock/Monitor | 无实现依赖：Interlocked 由 compat 吸收（catalog §1.5）；lock(this) 历史形不复刻，事件规则不产生 C29 职责内构造。C29 若先落地，对事件产物无新改写需求（Interlocked 直映可用） |
| **C35/C36 expression-desugar（stub）** | `E?.Invoke` 条件访问属表达式糖面 | member-desugar 在 Declarative 阶段先跑；事件触发改写成**普通 delegate 字段**的 `?.Invoke` 后，C35 看到的是标准 null 条件调用，无需识别事件；两 pass 不共享语法面 |
| **C08 JavaKeywords（已实现，链尾默认关）** | 去糖引入 `AddYieldListener` 等派生方法名 | 事件原名随 event 语法消失，产出名均为 `Add/Remove/Listener/下划线` 组合，Java 保留字风险只在事件名中部（如事件名 `yield`）；派生名经转换器 decap 后是否撞保留字列为 §5.4 核查项，若命中由 C08 链尾统一逃逸，C13 不内联关键字逻辑 |
| **C46 成员签名显式化（同 catalogItems）** | new 隐藏/显式接口事件与 C46 的签名显式化同面 | EV 规则自保留 `new`/显式实现语法；C46 落地时只消费去糖后的普通方法，不再认识 event；族内顺序约定 C46 在 C11-C14 之后（同一 pass 内规则顺序，族 pass 翻默认开前定死） |
| 公共热文件 | `MemberDesugar/` 族共享文件、族适配器（C11 新建） | C13 只新建 `Events/` 目录文件（含本规则注册点）；适配器统计/选项聚合采用「各规则子节并列」结构，禁止 C13 改动 property/indexer 代码；冲突以 rebase 追加行解决，禁止顺手重排族规则顺序 |

## 12. Worktree 分配

- 分支：`feature/preproc-c13-event-desugar`
- 目录：`.worktrees/preproc-c13-event-desugar`
- base：`67477551`（`feat(preprocessing): register 23 merged-family stub passes`，已核实存在；**实施前先合入 C11 锚点分支**，即实际开工 base = C11 合入后的主干/rebase 点）
- 创建命令（原样）：

```powershell
git worktree add .worktrees/preproc-c13-event-desugar -b feature/preproc-c13-event-desugar 67477551
```

实施约束（交实施 worktree，不在本文档执行；本任务只产出本文档、不改代码、不建 worktree）：仅在上述 worktree 内改码；等 C11 合入后 rebase 到含 `MemberDesugar/` 共享基建的基线上；按 §9 里程碑逐提交；不注册/翻转任何 stub、不动 CLI 注册表；转换器代码改动（§5.4 消费清单与旧特例删除）留后续 PR；族 pass 默认开启以 §8 门槛为准，不抢跑。

## 附：开放问题（实现前需锚点/评审确认）

- **Q1 规则接缝的支撑库协商机制**：EventRule 需要 Cs2jPrelude 在场（类内组合点发射 `CsPrelude.Combine/Remove`），但 C13 承诺不动注册表/RunAfterIds。候选：(a) C11 锚点的 `IMemberDesugarRule` 接缝提供「支撑库需求」声明（如 `RequiresCs2jPrelude`），族适配器在 AfterFiles 复用 DelegatePassAdapter 的 PreludeSupportFiles 释放 + ProjectReference 接线；(b) 锚点统一在族描述符 RunAfter 增补 `delegate`（由 C11 一处改）；(c) 用户 `--disable-pass delegate` 与启用 member-desugar 的组合直接配置期报错。本设计倾向 (a)+(c)，待 C11 锚点评审定。
- **Q2 族诊断码位最终表**：本文按 C12 §6 的预登记占 MD2xx（0xx=C11、1xx=C12、2xx=C13、3xx=C14），最终码位表由 C11 锚点统一登记；若锚点调整段位，M1 做纯常量改名。
- **Q3 命名碰撞策略族内一致性**：C11/C12 对派生成员名碰撞采用确定性追加序号消解（`GetItem2`…），C13 v1 对 Add/Remove/后备字段派生名碰撞选 MD210 整事件跳过——理由是事件名承载下游 Java 命名契约（§5.4 按约定识别）与接口双侧限定同步，序号改名会把契约复杂化。是否要求族内统一为序号消解（转换器识别序号名）由锚点评审结合 M0 碰撞实测数据定。
- **Q4 非 void delegate 事件的 Java 半区形态**：C# 侧已保字段 Invoke 语义（EV-17），但 Java 列表 forEach 模型无法表达返回值（多播返回最后处理者结果）；§5.4 要求转换器清理 PR 对非 void 后备字段保留 delegate 调用形，但最终落地形态（DelegateHelper 字段 vs 合成 fire 返回最后值）尚需转换器 PR 设计。
- **Q5 订阅线程安全默认（Simple）是否符合产品预期**：默认严格复刻 Roslyn C# 3.0+ 的无锁 field-like event 生成，CAS 仅作选项；即**不复刻** C# 1.x 的 `lock(this)`/`lock(typeof)` 历史行为，Java 现状 CopyOnWriteArrayList 的订阅安全强度在列表化后表现为单方向加强（§10）。默认开评审以 M0 数据（自定义 `lock`/`Interlocked` accessor 占比）确认是否需要把 CAS 升为默认或按类型注解自动选择。
