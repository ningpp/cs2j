# C13 Event 去糖预处理器设计

**日期**: 2026-09-28（2026-09-29 按 C11 落地形态修订）
**范围**: 在 `member-desugar` 族 pass 下新增 **Events 规则集**（Core `MemberDesugar/Events/`），把 C# `event`（字段式 / 自定义 add/remove / 显式接口实现 / virtual/abstract）声明改写为「委托后备字段 + `add_X`/`remove_X` 访问器方法」，并把 `+=`/`-=`/`nameof` 等全部使用点统一改写为语义等价的可编译 C#；本任务**不新增 pass、不注册/翻转 stub、不改 CLI 注册表**
**状态**: 设计已按 C11 合入形态修订（C11 分支 `feature/preproc-c11-property-desugar` @ `ac9bacab` 已 merge 进本分支，含 stub→Implemented 翻转、`MemberDesugar/` 骨架、`MemberDesugarPassAdapter` 与 `SampleMemberDesugarSolution` fixture）
**Catalog**: C13（P2，分类 1.4 成员）[preprocessor-catalog.md L407-L417](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L407-L417)；族归属「成员去糖」C11+C12+C13+C14+C46（[L898-L904](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L898-L904)）；风险 7 反射/字符串契约（[L926](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926)）、风险 9 实验 Lowering 非资产（[L928](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L928)）；并发背景见 §1.5（[L71-L72](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L71-L72)）
**注册表现状**: member-desugar 已由 C11 从 Stub 翻转为 Implemented——现登记于 [PassCatalog.cs L217-L228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L217-L228)（dev-3.x 侧 stub 条目 [PassStubCatalog.cs L191-L200](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L191-L200) 已随翻转删除），Stage=`Declarative`，RunAfter=`{struct-value-semantics, closure-lift}`，目录 `.cs2j-member-desugar-src`，默认关（显式启用）。C13 只在族 pass 内追加规则，注册表零行为改动

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
| 后备字段 | 编译器生成 private delegate 字段（**名同事件名**） | **不生成**；用户自持（常见转发到一个 private field-style event 或 delegate 字段） |
| add/remove | 编译器生成；修饰符随事件 | 用户编写；形参固定名 `value`；两访问器可各带可见性 |
| 声明类型内引用 | `E` 直接绑定后备字段：可 `E?.Invoke`、`E(...)`、`E = null`、`E == null`、类内 `E += h`（**直接改字段，绕过虚访问器分派**） | 只能 `+=`/`-=`（始终走访问器，类内类外皆然）；不能 Invoke / 判空 / 赋值 |
| 类外引用 | 仅允许 `+=`/`-=`（走 add/remove 访问器，虚分派） | 同左 |
| abstract / interface | `abstract event D E;` 只有抽象 add/remove、无字段；接口事件同理 | abstract accessor 只在抽象类；接口事件无 accessor 体语法（C# 8 DIM 接口事件可带体） |
| virtual/override/sealed/new | 修饰符作用于两个访问器；field-style `override` 自带一份新后备字段 | 同左；accessor 级修饰 |
| struct | 允许（实例后备字段；访问器非虚；**从不加锁**） | 允许 |
| 静态 | 静态后备字段 + 静态访问器 | 同左 |
| partial（C# 13） | 声明/实现两部分；源内可出现 | 同左 |

**线程安全订阅语义（§10 的论证基础）**：

- C# 1.0/2.0 编译器为 field-like event 生成**带锁**访问器：实例事件等价 `lock(this)`、静态事件等价 `lock(typeof(T))`；struct 从不加锁。
- **C# 3.0 起锁被移除**（Roslyn 至今）：生成代码是无锁的 `E = (D)Delegate.Combine(E, value)` / `Delegate.Remove`，并发订阅存在丢更新窗口，语言不再保证订阅线程安全；需要保证由用户自写 custom accessor（`lock` 或 `Interlocked.CompareExchange` 循环）。
- 触发侧的标准线程安全惯用语是**捕获到局部变量**：`var h = E; h?.Invoke(...)`（一次字段读取，避免「判空后被退订」的 TOCTOU）；C# 6 的 `E?.Invoke(...)` 同样只读一次字段。
- 零订阅时字段为 `null`：`E(args)` 抛 `NullReferenceException`；`E?.Invoke(args)` 无操作。多播链是**不可变**对象：Combine/Remove 返回新链，进行中的触发读到的是旧链快照；`Delegate.Remove` 按委托相等性移除**最后**一段匹配调用表。

### 1.3 Java 目标形态与本特性的关系

Java 现状形态（[EventFieldTransformer.cs L196-L233](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L196-L233)）：

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

本特性的 C# 目标形态采用 **C# 编译器自身的规范去糖形态**（catalog L412「委托字段 + Add/Remove 访问器」的落地形）——这也是族内 C11/C12 已确立的命名原则（C# 规范访问器名天然零碰撞）：

```csharp
// 输入
public event EventHandler<EventArgs>? Click;
protected void RaiseClick(EventArgs e) => Click?.Invoke(this, e);
// 类外：c.Click += OnClick;   c.Click -= OnClick;

// 输出（默认 Simple 订阅模式）
private EventHandler<EventArgs>? Click;   // 后备字段与事件同名（编译器规范形态）
public void add_Click(EventHandler<EventArgs>? value)
    => Click = Cs2jPrelude.CsPrelude.Combine(Click, value);
public void remove_Click(EventHandler<EventArgs>? value)
    => Click = Cs2jPrelude.CsPrelude.Remove(Click, value);
protected void RaiseClick(EventArgs e) => Click?.Invoke(this, e);   // 类内触发点零改写
// 类外：c.add_Click(OnClick);  c.remove_Click(OnClick);
```

要点：

1. 后备是**真 BCL delegate 字段**（不是合成列表类型），事件的多播/空值/异常/返回值语义在 .NET 半区可直接用源项目测试验证（catalog §5.1 两半区方法论）。
2. **字段名与事件同名**是 C# 编译器对 field-like event 的真实去糖形：声明类型内对事件名的全部引用（触发、判空、重置、直接赋值、`nameof`、类内 `+=` 绑定）在去糖后自动落在同名字段上，**零改写即语义等价**；`+=`/`-=` 例外——它们必须改写为 `Cs2jPrelude.CsPrelude.Combine/Remove` 调用（见要点 3）。
3. 类内 field-like `+=`/`-=` 必须改写为 `字段 = Cs2jPrelude.CsPrelude.Combine/Remove(字段, value)`：E05 已先行执行并跳过事件站点（[DelegateSyntaxRewriter.cs L173-L176](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L173-L176)），C13 引入的裸 delegate 组合不再有后续 pass 兜底，必须就地发射 E05 的规范产物形态 `Cs2jPrelude.CsPrelude.Combine/Remove`，保持「转换器永不见裸 delegate 组合」的链契约。
4. 命名遵循族规范：`add_X/remove_X` 是 C# 编译器对事件访问器的真实成员名——源内存在事件 `X` 时用户不可能再声明同签名 `add_X`（CS0082 同槽保留）或同名成员 `X`（CS0102），去糖后槽位确定性释放，**天然零碰撞**；与 C11 `get_X/set_X`、C12 `get_Item/set_Item` 完全平行。旧 `AddXxxListener` 方案随 C12 D1 的同款论证被取代（catalog 无措辞约束）。
5. 不合成 `FireXxx`/`OnXxx` 方法：触发只在声明类型内部合法，去糖后对同名字段直接调用即可；合成虚 `OnXxx` 会平白引入一条虚分派链、改变派生类行为。
6. E05 对 `IEventSymbol` 左值的 `+=`/`-=` 跳过行是两 pass 边界契约，C13 落地后不得删除。

**硬不变式**（与 C11 族契约一致）：中间源码树预处理后永远可编译（整树重编译相对基线无新增错误）；事件计划是原子改写单位（声明 + 全部使用点全改或全不改）；禁止注释/占位/半改流入下一环节。

## 2. 关键设计决策表

| # | 决策点 | 结论 | 备注 |
|---|---|---|---|
| D1 | **总体目标形态** | field-style event → private delegate 后备字段（**名同事件名**）+ `add_X/remove_X` 两方法；custom event → 仅两方法（accessor 体原样搬入，形参保留名 `value`，零 body 改写）；abstract/interface 事件 → 两抽象方法签名；全部使用点按符号绑定改写 | C# 编译器规范形态；与 C11 `get_X/set_X`、C12 `get_Item/set_Item` 族命名原则一致（D2） |
| D2 | **命名派生与零碰撞** | 字段名 = 事件声明标识符；方法名 = `"add_" + 事件名` / `"remove_" + 事件名`（`Events/EventFacts.AdderName/RemoverName`）。零碰撞论证同 C12 D1/D16：源内事件 `X` 已保留 `X`（CS0102）与 `add_X/remove_X`（CS0082）槽位，去糖后确定性释放；显式接口实现槽位独立（`IFoo.add_X`） | 原 `_<decap(X)>Listeners`/`AddXxxListener` 方案（镜像 [EventListenerNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventListenerNaming.cs) Java 命名）作废——族内已确立「C# 规范成员名 + 转换器派生 Java 形」路线（C11 决策 1b：`get_/set_ → getX/setX`；Java 半区对应派生 `add_/remove_ → addX/removeX`，归 §5.4 消费 PR） |
| D3 | **类内引用零改写** | field-like 事件的后备字段名 = 事件名 → 声明类型内的触发（`E?.Invoke`/`E.Invoke`/`E(args)`）、判空（`E == null`/`!= null`）、重置/直接赋值（`E = null`/`E = expr`）、捕获局部惯用语（`var h = E; h?.Invoke()`）、`nameof(E)` 在去糖后自动绑定同名字段，**文本零改动、语义精确等价**（编译器自身的字段引用形态） | 相比原方案（改写为 `_xxxListeners`）大幅缩减改写面：只改声明与 `+=`/`-=`；下游转换器识别「委托字段 + add_X/remove_X 方法对」形态即可，字段名即事件原名 |
| D4 | **订阅线程安全默认** | **Simple**：生成访问器体 `X = Cs2jPrelude.CsPrelude.Combine(X, value)` / `Remove`——与 Roslyn（C# 3.0+）field-like event 实际代码生成严格一致 | .NET 半区测试可证语义等价；并发订阅不保证是现 C# 语义而非退化，见 §10 |
| D5 | **订阅线程安全选项** | `MemberDesugarOptions.EventSubscriptionPolicy`（Simple / InterlockedCompareExchange，默认 Simple）：CAS 时生成访问器体为 `Interlocked.CompareExchange` 重试循环（lock-free、线性化），每事件出 CS2J5151 | 对齐 Java 现状 `CopyOnWriteArrayList` 的订阅安全性强度；`lock(this)` 历史形**不复刻**（反模式，lock 归一归 C29）；CAS 只作用于**生成**的访问器体，custom 事件用户体不受策略影响 |
| D6 | **struct 事件** | 一律 Simple；选项要求 CAS 时出 CS2J5152 并降级 Simple（`this` 可能是 readonly 拷贝/装箱副本，CAS 不可证） | C# 编译器对 struct 事件也从不加锁 |
| D7 | **零订阅触发 NRE** | 保持 C# 语义：`E(args)`/`E.Invoke(args)` → 同名字段直接调用（null 抛 NRE）；`E?.Invoke(args)` → 字段条件调用（无操作）；**不**引入 isEmpty 包装 | isEmpty 语义是 Java 列表形态的下游事，由后续转换器清理映射（§5.4） |
| D8 | **类内 field-like +=/-=** | 直接字段组合：`X = Cs2jPrelude.CsPrelude.Combine(X, value)` / `Remove`——复刻「类内绕过访问器虚分派」；`+=`/`-=` 是表达式，可用 `=` + Combine 局部等价替换（**任意位置合法，无语句拼接需求**） | 符号 + 词法含入类型判定（`GetEnclosingSymbol` 的 ContainingType 与事件 ContainingType 按 `SymbolEqualityComparer` 相等；partial 跨部件天然同类型），参照 AssignmentTransformer 现状分流 |
| D9 | **类内 custom +=/-= 与类外 +=/-=** | custom 事件（类内类外皆然）与 field-like 事件类外站点 → `receiver.add_X(handler)` / `receiver.remove_X(handler)`；接收者表达式**原样保留、单次求值**（接收者不复制，无副作用问题）；显式接口实现经接口引用：`((IFoo)x).add_Click(h)` | 方法组/lambda 实参到 delegate 形参的转换是 C# 原生；custom 类内走访问器是 C# 语义本义（含虚分派，与原访问器调用一致） |
| D10 | **触发点** | 仅声明类型内可出现且仅 field-like 有：去糖后自动绑定同名字段，**零改写**（D3）；custom 事件触发在 C# 中非法，无站点 | 不合成 Fire/On 方法；非 void delegate 的返回值语义因此不丢 |
| D11 | **判空/重置/直接赋值** | 同 D3：去糖后自动绑定同名字段，零改写；下游转换器不再需要 `TryTransformEventNull` 特例 | |
| D12 | **virtual/abstract/override/sealed/new/static/readonly 修饰符** | 修饰符按语义搬到 Add/Remove 两方法（accessor 级可访问性各自覆盖，规则同 [AccessorsToMethods.BuildMethodModifiers](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/AccessorsToMethods.cs)）；abstract/interface 事件只出抽象方法签名、无字段；field-style override 出 `override` 方法 + 私有后备字段；`new` 隐藏保留 `new`；struct `readonly` 修饰符丢弃（C11/C12 同策略） | 现状转换器不传播 abstract/virtual（EventFieldTransformer 硬编码 public 实例方法），前置化同时修此缺口 |
| D13 | **显式接口实现** | `event D IFoo.E { add {...} remove {...} }` → `void IFoo.add_E(D value)` / `void IFoo.remove_E(D value)`（显式实现，无访问修饰符）；接口事件本身同步去糖为 `void add_E(D value); void remove_E(D value);` 抽象方法对 | 接口事件去糖后是普通抽象方法，实现类方法对天然实现之；同类型多接口同名事件在源内即共用一个事件成员，去糖后共用一对方法，语义不变 |
| D14 | **custom accessor 可见性** | `add`/`remove` 各自的访问修饰符独立搬运；默认取事件可见性 | field-style 无 accessor 表，两方法同可见性 |
| D15 | **字段初始化器 / 多变量 / 静态** | `event D E = Init();` → 后备字段初始化器原样保留（E05 已先行把其中 delegate `+`/`-` 规范化为 CsPrelude 调用）；多变量声明 `event D E1, E2;` 拆为两组独立 plan（字段 + 各自方法对） | |
| D16 | **attribute** | `[field:]` 归后备字段（custom 事件无字段 → 不可路由）；`[method:]` 归两方法；accessor 级特性随访问器；无 target 按 AttributeUsage 目标感知路由（全 Field → 字段，任一 Method → 两方法，仅 Event → CS2J5156 跳过） | 复用 C11 [AttributeRouting.GetValidOn](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs) 的用法位提取；路由逻辑事件侧自持（Events/EventFacts），不写共享文件 |
| D17 | **nameof** | 绑定 `IEventSymbol` 的 `nameof`（含跨类 `nameof(C.E)`、接口限定）一律替换为原名字符串字面量 `"E"`；声明类型内 `nameof(E)` 即使不改写也会绑定同名字段取得同值，统一改写以简化分类（值不变） | 与 JK-4a/C11 决策 11 同策略 |
| D18 | **反射/字符串契约** | 不改写：`GetEvent("E")`/`AddEventHandler`/XAML/JSON 按名订阅静态不可见，属文档化限制（§6、§10） | 沿用 catalog §4 风险 7 的统一限制声明；skip 走族 overrides |
| D19 | **dynamic / 表达式树** | dynamic 接收者的订阅点不可判定 → 事件计划原子跳过 + CS2J5154；表达式树内**不存在**事件订阅语法节点（`+=`/`-=` 事件左值在表达式树中编译期非法），无闸门需求；触发/判空在表达式树内绑定的是同名字段，零改写即合法 | 若遇绑定不可解析站点一律显式诊断（CS2J5155） |
| D20 | **WinRT / static abstract / partial / readonly struct / 保留名** | `IEventSymbol.IsWindowsRuntime`、接口 `static abstract`/`static virtual` 事件、C# 13 `partial` 事件、readonly struct 事件、事件名恰为 `value`（生成体形参遮蔽同名字段，语义破坏）→ CS2J5153/5157 整事件跳过 | WinRT 访问器代码生成与托管模型不同；static abstract 无具体实现体；partial 事件对齐 C11 5114/C12 5138；`value` 保留名防御（对齐 C11 BackingFieldNaming 的 value 检查） |
| D21 | **计划粒度与回退** | 每个 `IEventSymbol` 一条 `EventPlan`（多变量声明按变量拆分；接口事件与其实现者为**独立 plan**，文件集合经层次联动）；含任一不可行站点 → 声明与全部站点原子不动；整树重编译新增错误归属走族 `TryAttributeDiagnostic`/OwnsTouch 机制，无法唯一归属 → 族级整树回退（CS2J5100，保守正确）——已知限制同 C12 D17 | 命名碰撞在 D2 论证下合法输入不可达，防御性 CS2J5157 闸门；原「确定性序号消解 vs 跳过」问题随零碰撞命名消解（原 Q3 作废） |
| D22 | **转换器代码** | 本特性 PR **不动** C#→Java 转换器；§5.4 Java 半区消费清单是默认开门禁，删除旧事件特例留后续 PR | 与 C08/C09/C12 同范式 |

## 3. 链位与族契约

### 3.1 归属既有 pass：`member-desugar`

C11 已完成 stub→Implemented 翻转，现登记于 [PassCatalog.cs L217-L228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L217-L228)：

| 字段 | 值 |
|---|---|
| Id | `member-desugar`（`PassIds.MemberDesugar`） |
| DisplayName / 目录 | "Member desugaring" / `.cs2j-member-desugar-src` |
| Stage | `PassStage.Declarative` |
| Status / DefaultEnabled | `Implemented`（C11 已翻转）/ `false`（显式启用） |
| RunAfterIds | `{ "struct-value-semantics", "closure-lift" }` |
| VerifyBuild/VerifyTestsByDefault | `true` / `true` |
| CatalogItems | `"C11"`（C13 合入后可扩为 `"C11, C13"`，属描述性元数据，可选一行） |

**本任务（C13）的链位承诺**：

1. **不新增 pass**：不注册新 id、不新增 `PassDescriptor`、不动 `RunAfterIds`；Events 随 member-desugar 的启用而启用、随其位置而行事。
2. **不动 stub/翻转**：翻转已由 C11 完成；C13 对 `PassCatalog.cs` 的唯一可选改动是 `CatalogItems`/`Description` 描述性更新（一行，非行为）。
3. Stage 固定 `Declarative`，与登记一致；`NeedsSemanticModel=true`；`SupportLibrary=None`（Cs2jPrelude 在场经族适配器保证，见 §3.2 第 3 点，不改枚举）；`VerifyBuildByDefault/VerifyTestsByDefault` 沿用 C11 设定的 `true`。
4. 中间目录 `.cs2j-member-desugar-src`、preserve 目录注册、指纹令牌（`pass:member-desugar=on/off`）、`--enable-pass/--disable-pass/--list-passes` 全部复用，零新增。

### 3.2 对 C11 锚点的依赖、规则接缝与文件所有权

**首选路径已成立**：C11 分支（`feature/preproc-c11-property-desugar` @ `ac9bacab`）已 merge 进本分支，C13 在已落地的 `MemberDesugar/` 骨架上直接添加 `Events/`。

规则接缝（Q1 已按 C11 落地形态定案）：

- 规则实现 `IMemberDesugarRule { MemberDesugarKind Kind; int Order; void Analyze(MemberDesugarAnalysisContext, MemberDesugarPlanBuilder); }`；
- **发现机制 = 程序集扫描**（`MemberDesugarRuleDiscovery.Discover()` 收集带 `[MemberDesugarRule(kind, order)]` 特性、有无参构造的具体类）——C13 **零注册代码**：`Events/EventRule.cs` 标注 `[MemberDesugarRule(MemberDesugarKind.Event, 30)]` 即被自动收集（`MemberDesugarKind.Event` 槽位 C11 已预留；Order 10=Property、20=Indexer、**30=Event**、40=Operator）；
- 共享基建（C11 已交付）：`MemberDesugarAnalyzer`（整编译一次分析 + 语义模型缓存；声明/站点枚举由各规则在 `Analyze` 内自扫）、`MemberDesugarPlan` 基类 + `MemberDesugarPlanBuilder`（确定性排序）、`MemberDesugarPreprocessor`（`RewriteFile`/`BindCompilation`/`ExcludePlans`/`TryAttributeDiagnostic`/`FormatAndValidate`）、`MemberDesugarSyntaxFacts`（RewriteAnnotation/表达式树/dynamic 判定/TempName）、`BackingFieldNaming`（本规则不需要——D2 同名形态无撞名面）、`MemberDesugarPassAdapter`（族级整树迭代回滚 + restore/verify/清理尾段）。

**post-merge 追加式共享文件编辑清单**（C13 合入 C11 后允许的全部共享文件改动，全部为「追加」不改既有行为；C11 的码位注释本身即预留「5130-5189 为 C12/C13/C14」，C12 已取 5130-5141）：

| 共享文件（C11 所有） | C13 追加内容 |
|---|---|
| `src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs` | 诊断枚举追加 5150-5158 事件码位（§6）；`MemberDesugarStatistics` 追加事件计数器（§5.3）；`MemberDesugarOptions` 追加 `EventSubscriptionPolicy`（D5，默认 Simple——纯追加，属性规则行为不变） |
| `src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs` | `RewriteFile` 内追加 EventPlan 派发：**先事件后属性**链式执行（事件改写器在原始树上运行保证全部语义查询命中原始节点；属性改写器随后在事件改写产物上运行，其符号查询按候选名 + `IPropertySymbol` 绑定门控，事件产物节点不含属性候选绑定）；「该文件无任何 planned plan」判定改为两族联合 |
| `src/CSharpToJava.CLI/Preprocessing/Adapters/MemberDesugarPassAdapter.cs` | `BuildChainSummaryLine()` 追加事件分项（纯展示）；新增**条件性 Cs2jPrelude 在场保证**：`EventsPlanned > 0` 时经 `PreludeSupportFiles.ReleaseAsync` 释放支撑工程并向含事件触碰文件的项目注入 ProjectReference（§3.2 第 3 点） |
| `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs` | （可选）`CatalogItems`/`Description` 描述性更新 |

**C13 独占新增**（全部落在 `src/CSharpToJava.Core/MemberDesugar/Events/` 与带 Event 前缀的测试/fixture 文件，见 §4.1/§7）：不在 `MemberDesugar/` 根共享文件与 `Properties/`/`Indexers/` 内写事件特例。

1. **共享基建**（C11 已建立）：全编译符号表、`MemberDesugarPlan` 基类（TouchedFiles/OwnsTouch/Outcome）、`FormatAndValidate` 三候选、族 RewriteAnnotation、整树 Analyze→Plan→逐文件 Rewrite→回退框架均直接复用。
2. **每文件改写顺序**：RewriteFile 内先 Events 后 Properties（§3.2 追加清单）；同一文件内事件声明展开与站点改写在单遍 `EventUseSiteRewriter` 内完成（先访问器体后展开，同 C11 模式）。
3. **Cs2jPrelude 在场保证（原 Q1 定案）**：事件改写产物引用 `Cs2jPrelude.CsPrelude.Combine/Remove`。默认转换链中 delegate pass 先于 member-desugar 执行，支撑工程已在场；**族适配器兜底**：当本次运行发生了事件改写时，`MemberDesugarPassAdapter` 在 verify 尾段前释放 `PreludeSupportFiles`（复用 E05 的嵌入资源释放 + ProjectReference 注入机制，注入范围 = 含事件 plan TouchedFiles 的项目）。用户禁用 delegate 又启用 member-desugar 的组合因此可编译（适配器兜底释放），`OnBuildFailed`（CS2J5103）仍是响亮失败的最后防线。C13 不新增 `PassSupportLibrary` 枚举值、不自行改 RunAfterIds。

### 3.3 硬不变式

1. 中间源码树在预处理后永远可编译（整树重编译相对基线无新增 error；警告不比对）。
2. 每个 plan 的转换原子：全转或全不转；回退单位是单个事件 plan，不影响同文件的属性/索引器 plan。
3. 幂等：对已去糖树再跑一次 member-desugar，Events 规则零改动（声明侧已无 `EventFieldDeclarationSyntax`/`EventDeclarationSyntax` 承载 plan 符号；订阅点只认 `IEventSymbol` 绑定，去糖后绑定的是方法/字段符号）。

### 3.4 中间树与支撑库

- 事件改写产生的 `Cs2jPrelude.CsPrelude.Combine/Remove` 调用与 E05 产物同形（`CreateCombineCall/CreateRemoveCall` 的 `Cs2jPrelude.CsPrelude` 限定名，[DelegateSyntaxRewriter.cs L115-L135](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L115-L135)）：支撑工程由族适配器按 §3.2 第 3 点释放，指纹快照机制沿用——C13 不引入新的 `PassSupportLibrary` 枚举值。
- 干净文件字节级复制（含 BOM 保持）；事件规则不改写不含 event 语法/事件绑定站点的文件。

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/MemberDesugar/Events/`，命名空间 `CSharpToJava.Core.MemberDesugar.Events`。C11 建立的族共享层位于 `MemberDesugar/`（计划/格式化/符号工具），C13 只消费不复制；镜像 C11 `Properties/` 的文件职责划分。

### 4.1 文件清单

```
EventRule.cs                   # [MemberDesugarRule(MemberDesugarKind.Event, 30)] IMemberDesugarRule 实现：
                               #   声明收集（EventFieldDeclaration 变量级 + EventDeclaration）+ 声明级闸门
                               #   （partial/WinRT/static abstract 接口事件/readonly struct/保留名 value/
                               #   特性路由 → 5153/5156/5157）、站点扫描分类（+=/-= 外部 vs 类内、
                               #   dynamic、nameof、层次联动文件）、EventPlan 产出
EventPlan.cs                   # EventPlan : MemberDesugarPlan：
                               #   Event(IEventSymbol)/DeclName(声明标识符)/AdderName/RemoverName/
                               #   HasField(是否生成后备字段)/UseCas(生效策略)/Kind 统计分类/
                               #   TouchedFiles/Outcome/FailCode/FailReason/OwnsTouch/BuildId
EventFacts.cs                  # 纯判定：AdderName/RemoverName 派生、修饰符平移（镜像 AccessorsToMethods
                               #   规则的事件版）、特性目标路由（复用 AttributeRouting.GetValidOn）、
                               #   词法含入类型判定、dynamic/表达式树判定（转调共享 SyntaxFacts）
EventDiagnostics.cs            # 5150-5158 消息文案（镜像 PropertyDiagnostics）
EventDeclarationExpander.cs    # EventFieldDeclarationSyntax → 字段 + 两方法；
                               #   EventDeclarationSyntax → 两方法（体搬入）；abstract/interface →
                               #   抽象签名；显式接口 → 显式实现；修饰符/特性/初始化器/多变量/静态保持
EventUseSiteRewriter.cs        # EventUseSiteRewriter : CSharpSyntaxRewriter（逐文件单遍）：
                               #   声明展开插入（VisitTypeLike 编排 + residual 闸门 5158）+
                               #   +=/-= 站点改写（类内字段组合 / 类外 add/remove 调用）+ nameof 字面量化
```

编译构造沿用 runner 标准（族适配器已建：`CSharpParseOptions(LanguageVersion.Latest)`、合成 global usings、`ProjectUnsignedTypePreprocessor.GetBasicReferences()`、`allowUnsafe:true` 的 DLL 编译）；分析与站点分类只查询**原始** SemanticModel。

### 4.2 两阶段数据流（整树一次分析 → plan 化重写 → 整树验证回滚）

1. **分析（整树一次，随 member-desugar 的 BindCompilation）**：`EventRule.Analyze` 遍历全部 `EventFieldDeclarationSyntax`（逐变量取 `GetDeclaredSymbol`）与 `EventDeclarationSyntax`，按 `SymbolEqualityComparer` 去重（partial 多部件按符号合并——C# 13 partial 事件整体闸门跳过，不产生双 plan）；逐语法树枚举 `+=`/`-=` 左值绑定到候选事件的站点并分类；层次联动（接口事件 ↔ 实现者事件互加文件，模式同 C11 `CollectHierarchyLinks`）。
2. **可行性判定（计划期）**：声明级闸门与站点级闸门（D19/D20）任一命中 → `Outcome=Infeasible, FailCode=CS2J515x`；族 overrides `skip` 命中 → `UserSkipped`。
3. **重写（逐文件，按 active plans 过滤）**：RewriteFile 派发先 Events 后 Properties（§3.2）。`EventUseSiteRewriter` 单遍完成声明展开与站点改写；改动节点挂共享 RewriteAnnotation，随后族 `FormatAndValidate`。Changed=false 字节级复制。
4. **ResidualScan（改写器内）**：残余 `event` 声明（无 plan 且无 skip 诊断）计数 → CS2J5158（strict 升级 Error）。
5. **回退**：中间树重编译相对基线新增错误时，按 TouchedFiles/OwnsTouch 归属唯一事件 plan，族执行器 ExcludePlans 后从原始副本重放；无法归属走族级全回退（CS2J5100）。稳态预期零回退：声明改写后符号关系由整树编译强保证。
6. **单文件模式**：plan 的 TouchedFiles 超出输入文件 → 排除 + CS2J5109（族级既有码）。

### 4.3 规则全集（EV-x）

**声明侧**

- **EV-1 field-style 事件（class/record/struct，默认 Simple）**：
  ```csharp
  public event EventHandler<Args>? Click;
  ↓
  private EventHandler<Args>? Click;
  public void add_Click(EventHandler<Args>? value)
      => Click = Cs2jPrelude.CsPrelude.Combine(Click, value);
  public void remove_Click(EventHandler<Args>? value)
      => Click = Cs2jPrelude.CsPrelude.Remove(Click, value);
  ```
  XML doc/无 target 特性按 D16 路由；字段 private（+ static 随原修饰）；`new` 隐藏关键字同步。
- **EV-2 InterlockedCompareExchange 模式（选项）**：两方法体替换为 CAS 重试循环（add 示例）：
  ```csharp
  public void add_Click(EventHandler<Args>? value)
  {
      var current = Click;
      while (true)
      {
          var combined = Cs2jPrelude.CsPrelude.Combine(current, value);
          var original = System.Threading.Interlocked.CompareExchange(
              ref Click, combined, current);
          if (ReferenceEquals(original, current)) break;
          current = original;
      }
  }
  ```
  remove 同构（Combine→Remove）；`Interlocked` 是真 BCL API，.NET 半区可跑竞争测试；每事件 CS2J5151。仅作用于生成的访问器体（field-like）；struct/接口/抽象事件不适用（D6）。
- **EV-3 custom event**：不生成字段；两方法签名按 accessor 各自可见性，**形参名保留 `value`**（普通 C# 方法形参可叫 value，accessor 体因此原样保留）；表达式体/块体、trivia、特性保持：
  ```csharp
  public event EventHandler? Changed {
      add { _changedListeners = Cs2jPrelude.CsPrelude.Combine(_changedListeners, value); }
      remove { _changedListeners = Cs2jPrelude.CsPrelude.Remove(_changedListeners, value); }
  }
  ↓ （外层两个方法声明；体内对其它事件的引用由 EV-10/EV-11 站点规则照常改写）
  ```
- **EV-4 abstract / interface 事件**：无字段，两抽象方法签名；接口内无修饰符；abstract class 内带 `abstract`：
  ```csharp
  interface IViewer { event EventHandler MouseDown; }
  ↓
  interface IViewer {
      void add_MouseDown(EventHandler value);   // 接口侧同步去糖
      void remove_MouseDown(EventHandler value);
  }
  ```
  接口事件与其实现类事件为独立 plan，双侧都 planned 时方法对自然构成实现；单侧被回退由整树编译暴露并按 plan 回退收敛（极端情形整树回退，保守正确）。
- **EV-5 virtual / override / sealed**：`virtual event D E;` → 字段 + 两 `virtual` 方法；field-style `override` → 新私有字段 + 两 `override` 方法；`sealed override` → `sealed override`。abstract 基事件的声明类型内不允许触发（C# 源约束），无触发站点要处理。
- **EV-6 显式接口实现**：
  ```csharp
  event EventHandler IViewer.MouseDown { add { BODY_ADD; } remove { BODY_REMOVE; } }
  ↓
  void IViewer.add_MouseDown(EventHandler value) { BODY_ADD; }
  void IViewer.remove_MouseDown(EventHandler value) { BODY_REMOVE; }
  ```
- **EV-7 字段初始化器 / 多变量 / 静态 / 特性**：初始化器原样保留；`event D E1, E2;` 拆两组；静态事件字段与方法均 static；特性路由按 D16；不可路由组合 CS2J5156。
- **EV-8 struct 事件**：字段 + 非虚方法，强制 Simple（诊断 CS2J5152）；方法体直接赋 `this` 字段，不引入 CAS/锁；readonly struct 事件跳过（D20）。
- **EV-9 不支持种类**：`IsWindowsRuntime` 事件、接口 `static abstract/static virtual` 事件、C# 13 `partial` 事件 → CS2J5153 整事件跳过。

**使用点侧**

- **EV-10 类外订阅/退订（field-like 与 custom 通用）**：左值绑定 `IEventSymbol` 且站点词法含入类型 ≠ 事件含入类型 → `receiver.add_X(handler)` / `receiver.remove_X(handler)`；接收者表达式原样保留、单次求值（`c.Click += h` → `c.add_Click(h)`；`((IFoo)x).E += h` → `((IFoo)x).add_E(h)`；`GetThing().E += h` → `GetThing().add_E(h)`——接收者不复制，无副作用面）。
- **EV-11 类内 field-like 组合**：站点词法含入类型 == 事件含入类型（partial 跨文件算同类型）→ 直接字段组合，复刻绕过虚访问器语义：
  ```csharp
  Click = Cs2jPrelude.CsPrelude.Combine(Click, value);   // +=（任意表达式位置合法）
  Click = Cs2jPrelude.CsPrelude.Remove(Click, value);    // -=
  ```
  custom 事件无后备字段，类内 `+=`/`-=` 走 EV-10 方法形（自定义访问器递归由用户自负，C# 语义本就如此）。
- **EV-12 触发点（仅声明类型内、仅 field-like）**：去糖后事件名自动绑定同名字段——`E?.Invoke(args)`、`E.Invoke(args)`、`E(args)`、捕获局部惯用语**全部零改写**（D3/D7）；null 时 NRE 语义不变。
- **EV-13 判空 / 重置 / 类内直接赋值**：`E == null`/`E != null`/`E = null`/`E = expr` 同 EV-12 零改写（同名字段绑定）。
- **EV-14 nameof**：任一段绑定被去糖事件的 `nameof` → 替换为事件原名常量字符串 `"E"`（取声明标识符；声明类型内不改写亦得同值，统一改写简化分类）。
- **EV-15 不可判定站点**：dynamic 接收者订阅、绑定不可解析且无语法兜底 → 事件 plan 原子跳过 + CS2J5154/5155；不猜测、不局部改写。
- **EV-16 转发/后备存储模式**：private field-style 事件仅被同类型 custom 事件 accessor 引用（[EventFieldTransformer.cs L15-L61](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L15-L61) 现按语法跳过它）时，C# 侧**仍完整去糖**（字段 + private 两方法 + 触发点全保留）——C# 元数据里它本来就是独立事件；下游 Java 清理若要复刻「跳过重集」去重，按命名约定识别即可，C# 中间树不替转换器做裁剪。
- **EV-17 非 void delegate 事件**：声明/订阅照常；触发保持同名字段 Invoke（多播返回最后一个处理者返回值的 C# 语义不丢）；文档化下游列表 forEach 模型无法表达该语义，Java 清理 PR 须对非 void 后备字段保留 delegate 调用形（§5.4）。

## 5. CLI 集成

C13 **不改 Program.cs、不增适配器、不增开关/verb**。所有 CLI 面由 C11 交付的 `MemberDesugarPassAdapter` 承载，C13 只按 §3.2 追加清单扩展：

1. **执行**：族适配器标准骨架（整树编译一次、Bind/Analyze、逐文件 Rewrite、restore/build/test verify）零改动；事件规则经程序集扫描自动汇入。
2. **选项透传**：通用 `preprocess --pass member-desugar` 与 convert-project 链上执行均经族 options 到达 EventRule，不新增 CLI 参数；`EventSubscriptionPolicy` 是 `MemberDesugarOptions` 的追加属性（默认 Simple），v1 无 CLI 开关（编程式/后续 overrides 通道）。
3. **统计汇入**：向 C11 平铺 `MemberDesugarStatistics` **追加**事件计数器：`EventsPlanned`、`EventBackingFieldsGenerated`、`EventSubscribeSitesRewritten`（类外 `+=`）、`EventUnsubscribeSitesRewritten`（类外 `-=`）、`EventInClassCombineSitesRewritten`（类内 `+=`/`-=`）、`EventNameofLiteralized`；跳过/回退复用既有 `PlansSkipped`/`PlansRolledBack`/`SkippedByReason`。族适配器 `BuildChainSummaryLine()` 追加事件分项（§3.2 清单内的展示性编辑），建议形如：
   `member-desugar: … , <EventsPlanned> event(s) planned (<N> backing field(s), <S>+/<U>- subscribe, <C> in-class combine, <K> nameof), <skipped> skipped, <rolledback> rolled back, <residual> residual`。
4. **诊断汇入**：`MemberDesugarDiagnostic` 经族适配器既有统一通道打印（MSBuild 格式 `文件(行,列): warning CS2J51xx: [事件名] 描述`）；Info（5150/5151/5152）仅 verbose 输出。
5. **Cs2jPrelude 在场保证**：见 §3.2 第 3 点——适配器在 `EventsPlanned > 0` 时释放支撑工程并注入 ProjectReference；不内联第二份常驻释放逻辑、不新增 `PassSupportLibrary` 枚举值。

### 5.4 Java 半区消费清单（转换器后续 PR 的输入，本特性不实施）

默认开启前必须逐项核实/补齐（缺则补转换器，不凭空假设；对齐 C09/C12 §5.4 纪律）：

1. 识别去糖成员约定：delegate 型字段（名同事件原名）+ 同类型单参 `add_X/remove_X` 方法对 → 产出 CopyOnWriteArrayList + add/remove（即把 [EventFieldTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs) 的触发条件从 event 语法换成去糖成员模式）；Java 方法名按 C11 决策 1b 的派生改造路径（`add_/remove_` 前缀 → `addX/removeX`），字段名走转换器既有 decap。
2. 类内同名字段上的 `Cs2jPrelude.Combine/Remove` 在「被识别为事件后备字段」时映射为列表 `add/remove`（而非 DelegateHelper 链）；非事件 delegate 字段仍走 DelegateHelper（[EventDelegateInvokeMethodTests.cs L46-L64](file:///d:/code/cs2j/tests/CSharpToJava.Tests/EventDelegateInvokeMethodTests.cs#L46-L64) 断言不能回归）。
3. 后备字段 `_f?.Invoke(...)`/`_f(...)`/`_f == null` → `fireXxx` + isEmpty 守卫（吸收 [StatementTransformer.ExpressionAndReturn.cs L323-L333](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L323-L333) 与 [BinaryExpressionTransformer.cs L196-L207](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs#L196-L207)）；**非 void delegate 后备字段保留 delegate 调用形**（EV-17）。
4. CAS 模式访问器（`Interlocked.CompareExchange(ref X, …)`）在列表形态下直接归一为 CopyOnWriteArrayList add/remove（列表自身线程安全）；Simple 模式列表化后订阅安全性等价或强于源 C#（§10 论证）。
5. 删除旧事件特例：EventFieldTransformer、AssignmentTransformer.Event、TryTransformEventNull、条件访问事件分支、Class/Struct/Interface 三个 event case、EventHandlerLambdaRewriter 的事件补丁；§1.1 四个既有事件测试改写为「去糖 C# → Java」断言，名字/形态断言（addXxx…、BiConsumer、fire、isEmpty）全部继续成立（Java 侧监听器命名随 5.4-1 派生规则对齐）。

## 6. 诊断策略

诊断码沿用族统一 **CS2J51xx**（C11 `MemberDesugarDiagnosticCode` 枚举，码位只追加不改号）。C11 已占 5100-5129（共享 5100-5109 / 属性 5110-5129），C12 取 5130-5141；**C13 取 5150-5158**，5159-5179 事件后续追加预留、5180-5189 留 C14：

| 码 | 枚举名 | 级别 | 含义 |
|---|---|---|---|
| CS2J5150 | EventPlanCreated | Info（仅 verbose） | 事件已去糖：`[含类型] 事件 → 字段 + add_X/remove_X`（含 Kind/站点计数标记） |
| CS2J5151 | EventCasPolicyApplied | Info（仅 verbose） | 生成访问器按 InterlockedCompareExchange 模式归一（附与 Simple 的语义差异说明） |
| CS2J5152 | EventStructCasDegraded | Info | struct 字段式事件不适用 CAS，已降级 Simple（若策略显式要求 CAS 则升 Warning） |
| CS2J5153 | EventUnsupportedDeclaration | Warning（strict Error） | 事件计划整事件跳过：WinRT 事件、接口 static abstract/virtual 事件、C# 13 partial 事件、readonly struct 事件 |
| CS2J5154 | EventDynamicSite | Warning（strict Error） | dynamic 接收者订阅不可判定 → 计划原子跳过 |
| CS2J5155 | EventUnbindableSite | Warning（strict Error） | 绑定不可解析且无语法兜底的站点 → 计划原子跳过 |
| CS2J5156 | EventAttributeUnroutable | Warning（strict Error） | 特性目标不可路由（`[field:]` 于 custom 事件、仅 Event 合法的无 target 组合等）→ 计划跳过 |
| CS2J5157 | EventNameCollision | Warning（strict Error） | 防御性：生成名/保留名碰撞（事件名 `value` 等，D2 论证下合法输入理论不可达）→ 计划跳过 |
| CS2J5158 | ResidualEvent | Warning（strict Error） | residual 闸门：处理后仍存在无 plan 且无跳过诊断的 `event` 声明（§7.4） |

族级诊断（C11 已落地，Events 只参与不自造）：CS2J5100 整树回退、CS2J5101 plan 回滚、CS2J5102 restore 失败、CS2J5103/5104 build/test 失败、CS2J5109 单文件跨文件。

原则（catalog §4 风险 12）：不可判定即显式诊断、整事件不动；**禁止只改声明漏站点、禁止 event 语法原样透传流入下游**（skip 登记项除外）；Warning 可经族 `--strict` 升级 Error；每次跳过必须留痕并可与 residual 对账。

**已知限制（文档化，不阻断）**：反射（`GetEvent("E")`/`EventInfo.AddEventHandler`）、XAML/序列化/表达式树外的按名订阅不保证；`nameof` 已按原名保留字符串值，其余字符串契约由用户负责（catalog §4 风险 7）。不保证二进制兼容。

## 7. 测试计划

全部以 `-c Release` 执行；先写单测矩阵再实现。

### 7.1 Core 单测 `tests/CSharpToJava.ProjectTests/MemberDesugarEventUnitTests.cs`

内联源码 → `CSharpCompilation` → `MemberDesugarPreprocessor`（规则经程序集扫描自动生效）→ `RewriteFile` → 忽略空白文本断言 + `AssertOutputCompiles`。测试基建直接复用 `MemberDesugarPropertyUnitTests` 的 `internal static Rewrite/AssertOutputCompiles` 帮助方法（同程序集可见）；事件产物含 `Cs2jPrelude.CsPrelude` 调用，`CSharpToJava.ProjectTests.csproj` 追加对 `src/Cs2jPrelude/Cs2jPrelude.csproj` 的 ProjectReference，使验证编译经 TPA 可解析。

- **field-style**：无初始化器/带初始化器/多变量拆分；static；`new` 隐藏；字段名/方法名派生逐项断言（`private D Click;` / `add_Click` / `remove_Click`）；
- **类内零改写**：`Click?.Invoke`、`Click.Invoke`、`Click(args)`、`Click == null`、`Click = null`、捕获局部惯用语——输出逐字保留（D3 断言不出现 `fire`/`isEmpty`/中间变量）；
- **类内组合**：`Click += h` → `Click = Cs2jPrelude.CsPrelude.Combine(Click, h)`；`-=` → Remove；表达式位置（`var y = (Click += h)`）；
- **类外订阅/退订**：`c.Click += h` → `c.add_Click(h)`；带副作用接收者单次求值（`GetThing().Click += h` → `GetThing().add_Click(h)`，接收者仅出现一次）；显式接口经接口引用；
- **custom add/remove**：accessor 体原样（含 `value` 零改写）、两访问器不同可见性、转发到 private field-style 事件（后备存储模式 EV-16，两套成员都在）、类内 `+=` → `add_X(h)`；
- **显式接口实现**：`void IFoo.add_Click` 形态与无修饰符；接口事件去糖为抽象方法对、隐式实现事件与接口事件双向一致、输出可编译（接口方法被实现）；同类实现两接口同名事件；
- **abstract/virtual/override/sealed**：抽象类抽象事件只有签名；派生类 field-style override 带新字段 + override 方法；virtual 事件外部订阅编译为方法调用；
- **EventHandler 泛型/非泛型/自定义 delegate**：类型语法（含 `EventHandler<MyArgs>`、自定义多参 delegate、Action）逐字保留；
- **多订阅者语义**（.NET 半区）：`Combine` 后触发顺序、重复订阅/退订后调用表、退订到 null 后 `?.Invoke` 无操作、直接 Invoke 在 null 时抛 NRE；
- **CAS 模式**：选项开启后输出含 `Interlocked.CompareExchange` 循环且可编译；struct 事件降级 Simple + CS2J5152；
- **nameof**：类内/类外/接口限定三形 → `"Click"` 字面量；不涉事件的 nameof 不动；
- **反例/诊断**：partial 事件 / readonly struct 事件 / 事件名 `value` → CS2J5153/5157 原子不动（声明与站点都不改）；dynamic 站点 → CS2J5154；skip overrides；strict 升级；`[field:]` 于 custom 事件 → CS2J5156；
- **跨文件**：声明/订阅分两个编译单元，一次 Bind 两边都改；单文件模式越界 → CS2J5109；
- **干净文件**字节级保留、BOM 保持；residual 扫描计数与 skip 对账；二次运行零改动（幂等）；回退通道（OwnsTouch/CS2J5100 族机制对事件 plan 生效）。

### 7.2 项目级 fixture（并入 C11 族共享解决方案）

在 C11 已落地的 `tests/SampleMemberDesugarSolution/` 中新增 C13 独占文件：`SampleMemberDesugarLib/EventSamples.cs`（field-style：EventHandler 非泛型/`EventHandler<T>`/自定义 delegate 各一、custom add/remove 含转发后备事件、接口事件 + 隐式实现 + 显式实现、abstract/virtual/override 事件层级、static 事件、struct 事件、带初始化器事件、一个 skip 对照事件——partial 事件或保留名，断言 CS2J5153/5157 跳过且中间树可编译）、`SampleMemberDesugarApp/Program.cs` 与 `SampleMemberDesugarTests/BehaviorTests.cs` 增补跨「工程」订阅/退订、多订阅者触发顺序、零订阅触发不抛（`?.Invoke`）与直接触发抛 NRE 两组断言、退订后不再被调用。

断言（`MemberDesugarEventProjectTests.cs`）：`run.Success`（VerifyBuild/VerifyTests 默认开，即证明中间树 build+test 通过——同时实证适配器 Cs2jPrelude 释放与 ProjectReference 注入）、`EventsPlanned`/站点计数、中间树**不含** ` event ` 声明与事件绑定 `+=`/`-=`（skip 对照项除外）、含 `add_Click`/`remove_Click`/`CsPrelude.Combine`；clean 工程字节级复制与 BOM 用例沿用族级行为。

### 7.3 转换器既有回归的处置

`EventDelegateInvokeMethodTests`、`InterfaceEventListenerNamingTests`、`EventHandlerEventArgsMappingTests`、`EventAndStaticPropertyRegressionTests`（均在 [tests/CSharpToJava.Tests](file:///d:/code/cs2j/tests/CSharpToJava.Tests)）：member-desugar 默认关期间必须**持续全绿、零改动**（这些测试直连 ConversionPipeline，不经过预处理器）；默认开评审时再按 §5.4 消费 PR 统一处置预期，不在本 pass 内迁就旧断言改测试。

### 7.4 residual 闸门与一致性/CLI 测试

- residual：新增闸门测试——构造含无 plan 事件（如防御性注入）的样例，断言 ResidualEvent（CS2J5158）计数与 `--strict` 退出码语义；干净样例 residual=0；
- 一致性：`ProjectPreprocessorsConsistentTests` 不断言新 pass（无新 pass）；C11 已为其增加 member-desugar 段，C13 仅在既有段语义不变的前提下无需改断言；
- CLI：在 C11 的 member-desugar CLI 路径上追加事件场景（`--enable-pass member-desugar` 跑事件夹具、`--list-passes` 不出现新 id、族摘要含事件分项、CS2J5153/CS2J5158 输出与退出码）；不新增独立 verb。

### 7.5 验证命令

```powershell
dotnet build CSharpToJavaConverter.slnx -c Release
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj -c Release
```

## 8. 默认开启验收门槛

随 member-desugar 族统一门槛（C11 已定义：中间树 build/test 全绿、csharp-sdk 基线不退化、指纹/回滚/落盘三不变），Events 规则附加项：

1. 7.1 单测矩阵全绿；7.2 fixture 的 Release `build` **与** `test` 退出码 0（含多订阅者/NRE/竞争用例）；
2. residual 闸门：训练集/夹具中**源内**事件 plan 覆盖率 100%（跳过项必须全部有 CS2J5153-5157 显式原因，无「静默未处理」）；
3. 等价性专项：副作用接收者单次求值（行为测试，非文本断言）；显式接口分派走通接口调用；抽象/虚链中间树测试钉住；多订阅者顺序/退订身份差异在 §10 文档化；
4. 幂等：二次运行零 diff；
5. **Java 半区就绪**：§5.4 五项逐项核实/补齐；代表性事件样本（field-style/custom/接口/抽象/虚/EventHandler 泛型非泛型/自定义 delegate）经完整 C#→Java 链路产出可编译 Java，且 §1.1 四个既有事件测试的名字与形态断言改写后继续成立；旧事件分支在去糖产物上不可达（删除仍留后续 PR，但默认开前证明触发不到）；
6. **文档**：本设计 + 对应 plan（`docs/superpowers/plans/2026-09-29-c13-event-desugar.md`）定稿；转换器删除清单（§5.4）登记后续 PR。

不满足时任随族保持 opt-in（catalog §4 风险 10）。

## 9. 里程碑

| 里程碑 | 内容 | 验证 |
|---|---|---|
| M0 | C11 锚点合入/对齐（已完成：merge `ac9bacab`）；规则接缝/统计/码位按落地形态定案 | 文档/协调 |
| M1 | 共享模型追加（5150-5158 码位、事件计数器、`EventSubscriptionPolicy`）+ EventRule 声明收集/闸门/站点分类 + EventPlan/BuildId/OwnsTouch + EventFacts/EventDiagnostics；单测覆盖分析面（不重写） | Core 单测全绿 |
| M2 | EventDeclarationExpander（EV-1~EV-9 声明面）+ `RewriteFile` 事件派发（先事件后属性）+ EventUseSiteRewriter 站点改写（EV-10~EV-15）+ 单测主体 | 单测全绿；基本样本中间树 build 通过 |
| M3 | fixture 增补（EventSamples/Program/BehaviorTests）+ 适配器摘要分项 + Cs2jPrelude 在场保证 + residual 闸门 + 幂等/回退测试；Release build/test 全绿 | fixture Release build/test 全绿 |
| M4 | EV-2 CAS 模式 + 竞争用例 + CS2J5151/5152 打磨 + sdk 事件形态扫描（field-style/custom/接口占比、`+=`/`-=` 站点数、partial/WinRT/保留名命中数）+ §5.4 Java 半区核查原型（只核查不改正转换器）+ 默认开证据包 | 候选随族默认开评审 |
| M4+（不承诺） | 转换器侧旧事件特例删除与去糖成员识别（§5.4）；与 C46 成员签名显式化联动的事件 new/协变边角；WinRT 事件支持 | 独立后续 PR |

## 10. 风险

### 10.1 锁语义等价性（重点论证）

**事实面**：C# 1.0/2.0 field-like event 的编译器访问器带 `lock(this)`/`lock(typeof(T))`；**C# 3.0 起（Roslyn 至今）无锁**，`Delegate.Combine/Remove` 直接赋字段，并发 `+=`/`-=` 有丢更新窗口；struct 事件从不加锁（§1.2）。

**形态对比**：

| 形态 | 订阅竞争 | 死锁风险 | 与源语义关系 | Java 落地 |
|---|---|---|---|---|
| Simple（默认） | 不安全（同 Roslyn） | 无 | 与现 C# 源码严格等价，.NET 半区可证 | 列表化后为 CopyOnWriteArrayList（**强于**源，单方向加强）或 DelegateHelper 字段（同强度） |
| InterlockedCompareExchange（选项） | lock-free 线性化，无丢更新 | 无 | 强于源 C# 语义（语义归一，CS2J5151 留痕，同 C09 EveryThread 范式） | 列表化后强度一致 |
| 复刻 lock(this)/lock(typeof) | 安全 | 有（公开对象锁、类型锁死锁） | 复刻已被废弃的历史编译器行为 | lock 归一属 C29，本特性不发射 lock |

**等价性结论**：

1. 默认 Simple 是对「今天的 C# 源码」的忠实降级——源语言本就不保证并发订阅安全，不存在语义退化；只有用户**依赖** C# 1.x 历史加锁行为的代码会观察到差异，而该行为 2008 年（C# 3.0）起已不存在。
2. Java 现状 `CopyOnWriteArrayList` 的订阅安全性**强于**源 C#；Simple 经 §5.4 列表化后保持这一强度（CopyOnWriteArrayList.add/remove 自身线程安全），属于良性单方向加强，与当前转换器行为一致，不构成回归；需要在 Java 侧精确复刻「非线程安全」没有业务理由。
3. **触发/多播快照等价**：Combine 产新不可变链，进行中的触发持有旧链引用；CopyOnWriteArrayList 的迭代持数组快照——「触发期间新订阅不被调用、退订仍可能被调用」两者一致；遍历遇首个异常即中止两者一致。
4. **退订身份差异（已知残余）**：`Delegate.Remove` 移除最后一段匹配表；`List.remove` 移除首个 equals 匹配。重复同处理者订阅/退订（`+=h; +=h; -=h;`）两模型都剩一个 h；差异只出现在「多播处理者作为整体退订」等边角，列入 Java 半区核查项，必要时在 compat 侧补尾部移除语义。
5. **null/NRE**：C# null 字段直接触发抛 NRE、`?.Invoke` 无操作；列表模型用 isEmpty 表达同一可观察行为（零订阅无操作）；直接调用形（无 `?.`）经 §5.4 映射时必须保留 NRE 等价（非空守卫路径才插 isEmpty），fixture 双断言锁定。
6. CAS 选项只发射 BCL `Interlocked.CompareExchange`（compat 已有 Interlocked 吸收，catalog §1.5 [L72](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L72)），不引入 lock 语句，故与 C29 无顺序耦合。

### 10.2 其他风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **Java 产物形态变化**：事件从转换器内联映射（`_xListeners` 列表）变为去糖显式形态（字段 + add_X/remove_X → Java `addX/removeX`），与 compat 库及既已生成 Java 的手工调用方可能失配 | D2/§5.4；EventListenerNaming 契约 | 默认关 + §8 第 5/6 条；消费 PR 独立收口；C11 决策 1b 已确立同型派生改造先例 |
| R2 | **类内/类外分流正确性**：field-like 事件类内 `+=` 绕过虚访问器、custom 事件类内仍走访问器；partial 跨文件、嵌套类型都依赖词法含入类型判定 | §1.2 语义表 | 以 `IEventSymbol` + `GetEnclosingSymbol` 的 `SymbolEqualityComparer` 判定，不按语法/文本；原子计划 + 整树编译兜底 |
| R3 | **接口双侧同步**：接口事件与实现类事件必须同时去糖，否则中间树编译失败（接口未实现） | EV-4 | 独立 plan + 层次联动文件；漏网由整树编译捕获并按 plan 回退收敛（单侧回退仍不编译时逐 plan 归属，极端情形 CS2J5100 整树回退，保守正确） |
| R4 | **非 void delegate 事件**：Java 列表 forEach 丢弃返回值 | EV-17 | C# 侧同名字段 Invoke 保语义（零改写）；Java 清理对非 void 后备字段禁用列表 fire 形，列为 §5.4 硬核查项 |
| R5 | **反射/字符串契约**（catalog §4 风险 7）：去糖后 `GetEvent`/按名订阅失效 | D18 | nameof 字面量化保编译与常量契约，其余文档化 + 族 overrides skip |
| R6 | **Cs2jPrelude 在场**：事件产物引用 Combine/Remove，支撑工程缺席即 verify build 失败 | §3.2 第 3 点 | 族适配器条件释放 + ProjectReference 注入（本任务交付）；默认链 delegate 先执行已在场；CS2J5103 响亮失败兜底，不静默 |
| R7 | **自定义 accessor 体内任意逻辑**：锁、转发多事件、日志、条件订阅等 | EV-3/EV-16 | 策略是零 body 改写（形参保留 `value`），体内事件引用按普通站点规则改写；体内不透明语义不解释，.NET 半区测试直接验证 |
| R8 | **static abstract 接口事件 / WinRT / partial**：sdk 若有命中 | EV-9/D20 | CS2J5153 显式跳过，不产出半成品 |
| R9 | **实验 Lowering 误导**（catalog §4 风险 9）：[LowerEvent.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerEvent.cs) 的 `Listeners.forEach` 仅借形态命名 | §1.1-4 | 不共用代码、不作为 Java fire 语义依据（非 void 等差异以本设计为准） |
| R10 | **共享文件追加冲突**：C12/C14 并行改同一批共享文件 | §11 | C13 全部共享改动为「枚举成员/计数器/选项属性/派发分支/摘要行」式追加；合并顺序任意，rebase 解追加行冲突；属性单测守门（C11 单测必须持续绿） |
| R11 | **整树多轮重编译拉长链耗时** | catalog §4 风险 11 | 无事件工程零触碰（clean 字节复制）；Analyzer 一次建表；verify 可关；指纹缓存复用 |

## 11. 并行协调

| 相邻工作 | 边界 | 合并顺序/依赖 |
|---|---|---|
| **C11 Property（族锚点）** | C11 拥有 member-desugar pass、共享基建与 stub 翻转；C13 只加 `Events/` 规则 | **已合入**（merge `ac9bacab`）；§3.2 追加清单；命名/回滚/统计接缝以 C11 为准 |
| **C12 Indexer / C14 Operator（同族）** | 各自规则目录 `Indexers/`、`Operators/` 与本规则并列；共享 Analyzer/回滚/命名服务，互不编辑他族目录；C12 码位 5130-5141，C13 取 5150-5158，5180+ 留 C14；Order 10/20/30/40 | 并行无语法面争抢；注册发现 = 程序集扫描零热文件；`RewriteFile` 派发分支按「追加」扩展，合并顺序任意（C12 先合则属性→索引器→事件链式追加） |
| **E05 DelegatePreprocessor（已实现）** | 最强相邻边界：E05 已处理 delegate `+=` 组合并预留事件入口（[DelegateSyntaxRewriter.cs L173-L176](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L173-L176)） | 该 `IEventSymbol` 跳过行是契约：C13 落地后不得改 E05；C13 统一复用 CsPrelude Combine/Remove 与 Prelude 支撑工程，不引第二种组合 helper；支撑库在场由族适配器条件保证（§3.2 第 3 点）。delegate 类型声明（自定义 delegate）两 pass 都不改写，主转换器消费 |
| **C29 concurrency-normalize（观察项 stub）** | CAS 用 BCL `Interlocked.CompareExchange`；不发射 lock/Monitor | 无实现依赖：Interlocked 由 compat 吸收（catalog §1.5）；lock(this) 历史形不复刻，事件规则不产生 C29 职责内构造 |
| **C35/C36 expression-desugar（stub）** | `E?.Invoke` 条件访问属表达式糖面 | member-desugar 在 Declarative 阶段先跑；事件触发改写后是**同名字段**的条件调用（零改写），C35 看到的是普通字段 null 条件调用，无需识别事件；两 pass 不共享语法面 |
| **C08 JavaKeywords（已实现，链尾默认关）** | 去糖产物成员名 `add_X/remove_X`/字段 `X` | `add_`/`remove_` 前缀名非 Java 保留字；派生 Java 名（addX/removeX）由转换器派生规则保证，若事件名中部撞保留字由 C08 链尾统一逃逸，C13 不内联关键字逻辑 |
| **C46 成员签名显式化（同 catalogItems）** | new 隐藏/显式接口事件与 C46 的签名显式化同面 | EV 规则自保留 `new`/显式实现语法；C46 落地时只消费去糖后的普通方法，不再认识 event；族内顺序约定 C46 在 C11-C14 之后（同一 pass 内规则顺序，族 pass 翻默认开前定死） |
| 公共热文件 | `MemberDesugar/` 族共享文件、族适配器（C11 新建） | C13 只新建 `Events/` 目录文件；共享文件改动穷尽于 §3.2 追加清单；适配器统计/选项聚合采用「各规则并列追加」结构，禁止 C13 改动 property/indexer 代码；冲突以 rebase 追加行解决，禁止顺手重排族规则顺序 |

## 12. Worktree 分配

- 分支：`feature/preproc-c13-event-desugar`
- 目录：`.worktrees/preproc-c13-event-desugar`
- base：dev-3.x `5e3e796d`（含本文档自身与 `67477551` 注册基线；base 必须包含本文档，故取 dev-3.x HEAD 而非 `67477551`）
- **实现前置动作（已完成）**：worktree 创建后 merge C11 分支 `feature/preproc-c11-property-desugar`（`ac9bacab`），确认 `src/CSharpToJava.Core/MemberDesugar/` 共享骨架、`MemberDesugarPassAdapter` 与 member-desugar Implemented 翻转已在树中，再开始 M1。

```powershell
git worktree add .worktrees/preproc-c13-event-desugar -b feature/preproc-c13-event-desugar dev-3.x
git -C .worktrees/preproc-c13-event-desugar merge --no-edit feature/preproc-c11-property-desugar
```

实施约束：仅在上述 worktree 内改码；按 §9 里程碑逐提交；不注册/翻转任何 stub、不动 CLI 注册表行为；转换器代码改动（§5.4 消费清单与旧特例删除）留后续 PR；族 pass 默认开启以 §8 门槛为准，不抢跑。

---

## 附：开放问题（已全部按 C11 落地形态定案/收窄）

- **Q1 规则接缝与支撑库协商机制（已定）**：`IMemberDesugarRule { Kind, Order, Analyze(context, builder) }`；发现 = 程序集扫描（`MemberDesugarRuleAttribute` + 无参构造）；C13 = `[MemberDesugarRule(MemberDesugarKind.Event, 30)]`，零注册代码。Cs2jPrelude 在场不走规则接缝声明，改由族适配器条件释放（§3.2 第 3 点）：EventsPlanned>0 → `PreludeSupportFiles.ReleaseAsync` + 含事件触碰文件的项目注入 ProjectReference；默认链 delegate 先执行已在场，CS2J5103 兜底。
- **Q2 族诊断码位（已定）**：族统一 CS2J51xx；C11=5100-5129、C12=5130-5141、**C13=5150-5158**、5159-5179 事件预留、5180-5189 留 C14（§6）。
- **Q3 命名碰撞策略（已消解）**：族规范命名（`add_X/remove_X` + 同名字段，D2）使派生名碰撞在合法输入中不可能发生（CS0082/CS0102 槽位保留），原「序号消解 vs 整事件跳过」问题不存在；防御性 CS2J5157 闸门覆盖保留名 `value` 等残余情形。
- **Q4 非 void delegate 事件的 Java 半区形态（保留）**：C# 侧已保同名字段 Invoke 语义（EV-17，零改写），Java 列表 forEach 模型无法表达返回值；§5.4 要求转换器清理 PR 对非 void 后备字段保留 delegate 调用形，最终落地形态（DelegateHelper 字段 vs 合成 fire 返回最后值）尚需转换器 PR 设计。
- **Q5 订阅线程安全默认（维持 Simple）**：默认严格复刻 Roslyn C# 3.0+ 的无锁 field-like event 生成，CAS 仅作 `EventSubscriptionPolicy` 选项（D5）；即**不复刻** C# 1.x 的 `lock(this)`/`lock(typeof)` 历史行为，Java 现状 CopyOnWriteArrayList 的订阅安全强度在列表化后表现为单方向加强（§10）。默认开评审以 M4 数据（自定义 `lock`/`Interlocked` accessor 占比）确认是否需要把 CAS 升为默认或按类型注解自动选择。
