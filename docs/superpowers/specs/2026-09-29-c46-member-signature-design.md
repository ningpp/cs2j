# C46 成员签名显式化（显式接口实现 / new 隐藏 / 协变返回）设计

**日期**: 2026-09-29
**范围**: 在 `member-desugar` 族 pass 下新增 **Signatures 规则集**（Core `MemberDesugar/Signatures/`），把三类依赖 C# 签名/分派暗语义的成员形态——① 显式接口实现（方法/属性/索引器/事件）、② `new` 隐藏成员、③ C# 9 协变返回 override——改写为不依赖该暗语义的显式可编译 C#：改名 public 槽位方法 + 按 SemanticModel 静态绑定重写全部调用点，或在 Java 不可表达处显式报码。本任务**不注册/不翻转 stub、不新增适配器、不改 CLI 注册表、不触转换器代码**
**状态**: 设计稿，待评审；实施前提为 C11 锚点合入
**族属**: catalog P3「成员去糖族」第 6 条规则集（[preprocessor-catalog.md:910-922](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L910-L922)，§3.3 合并族表 [L1082](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1082-L1082)）；锚点设计 [2026-09-28-c11-property-desugar-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md)

---

## 1. 背景与目标

### 1.1 三类签名问题的现状证据

**① 显式接口实现：成员键仅识别，方法零处理。**

- 去重键把 `ExplicitInterfaceSpecifier` 拼入 C# 侧 MemberKey，仅保证 partial 去重时不被误并：[ClassTransformer.cs:303-308](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L303-L308)（方法/属性/事件三键带限定名，索引器走 `other`）。
- **方法没有任何显式接口专门改写**：[MethodTransformer.cs:154-176](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L154-L176) 不读 `ExplicitInterfaceSpecifier`，方法名经 [JavaName:178-187](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L178-L187) 首字母小写派生——`void I.Foo()` 与 `public void Foo()` 派生出**同一个** Java 名 `foo`。
- 同 Java 名 + 同擦除参数签名的方法被**静默丢弃**：[AddMethodIfNotDuplicateInternal:540-545](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L540-L545)。即 `void IA.F()`/`void IB.F()` 两个不同实现体、或显式实现与类自有同名方法并存时，后入者无声消失（接口未实现/绑错槽位），无诊断。
- 调用点侧零符号特判：`src/CSharpToJava.Core/Transformers/Expression` 全目录 grep `ExplicitInterfaceImplementations|ExplicitInterfaceSpecifier` **零命中**。接口引用调用 `((IFoo)x).Foo()` 仅靠「Java 实现方法恰好也叫 foo」才能编过——catalog 所述「无碰撞时恰好能编译，碰撞/泛型重实现场景静默出错」即此。
- 属性/索引器/事件各有 1–4 处零散补丁：
  - 属性：[PropertyTransformer.cs:12-13](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L12-L13) 用语法原名派生访问器；碰撞靠测试锁定的 `getName$Class` 续命（[ExplicitInterfacePropertyConflictTests.cs:53-60](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ExplicitInterfacePropertyConflictTests.cs#L53-L60)）。
  - 索引器：[IndexerTransformer.cs:28-37](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L28-L37) 与 [:228-253](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L228-L253) 的「按接口简单名文本匹配 AllInterfaces」兜底（命名空间/别名同名易错），回归 [InterfaceIndexerReturnTypeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterfaceIndexerReturnTypeTests.cs)。
  - 事件：[EventFieldTransformer.cs:104-113](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs#L104-L113) 取限定名末段拼 `addIViewerMouseDownListener`，回归 [InterfaceEventListenerNamingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterfaceEventListenerNamingTests.cs)。
- 访问级别也错：C# 显式实现是私有访问形态，Java 接口方法必须 public；MethodTransformer 对无修饰符方法不补 public。

**② `new` 隐藏：按静态类型编译期决议的 C# 语义在 Java 侧无对应。**

- 方法：[MethodTransformer.cs:166](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L166-L166) 把 `OverrideKeyword or NewKeyword` **统一映射为 Java `@Override`**——`new` 恰是「不覆写、另开槽位」，映射在返回类型不协变时直接产出错误/非法 Java。
- 属性：现转换器的处理是「**干脆不生成派生类 getter**」丢成员（[PropertyHidingTests.cs:29-34](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertyHidingTests.cs#L29-L34) 锁定该行为），仅纯 base 委托（扩可见性）场景保留（[:72-95](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertyHidingTests.cs#L72-L95)）；返回类型不兼容隐藏另有 `_newSlot` 后缀 + `@CompatPropertyName` 注解的特例（[JavaNaming.IsIncompatibleTypeHidingProperty:39-60](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L39-L60)、[PropertyTransformer.cs:103](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L103-L103)）。三套互不一致的补丁。

**③ 协变返回：多数可直映，擦除冲突无系统处理。**

- Java 5+ 支持引用类型协变返回，普通「override 返回更派生引用类型」可保留；但泛型返回（如 `IEnumerable<string> override IEnumerable<object>`）擦除后非子类型、以及值类型卷入的组合，转换器无分析、无桥接合成，collision 时落到 `AddMethodIfNotDuplicateInternal` 静默丢方法或产出不可编译 Java。全仓 grep `covariant|Covariant` 仅见委托/接口声明侧型变（[ClassTransformer.cs:40](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L40-L40)），与 override 返回类型无关。

### 1.2 目标

在 C#→C# 层（C11–C14 方法化之后的树上）产出：

1. **显式接口实现 → public 隐式槽位方法**：移除 `ExplicitInterfaceSpecifier`、提升 public、保留接口槽位规范名；碰撞时让位的类成员确定性改名；**所有调用点按 SemanticModel 绑定符号分类重写**（接口分派点不动、绑定到让位成员的点改新名）。
2. **`new` 隐藏 → 改名成员 + 静态类型调用点重写**：把「按静态类型编译期选槽」物化为「两个不同名字的方法」，使 C# 的编译期决议在 Java 侧退化为普通名字解析；纯 base 委托扩可见性形态保留为同名桥接。
3. **协变返回 → 可直映则保留，擦除冲突则显式桥接**：基签名虚方法（返回基类型）+ 改名 newslot 派生方法（返回派生类型，原体），调用点按绑定到的 override 符号改写。
4. 产物恒为可编译 C#，由中间树 `dotnet build -c Release` + `dotnet test -c Release` 证明 .NET 半区等价（[catalog §5.1](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1127-L1134) 两半区方法论）。

**明确不做**：接口 `static abstract`/`static virtual` 成员——归 C36 防线（CS2J0585，见 [c36 设计 §6](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c36-collection-expr-design.md)），本规则只跳过并交叉引用；Java 侧转换器补丁的删除（属各默认开门禁的后续消费 PR）；终结器（C39，占 CS2J5200-5219）。

### 1.3 与族内条目的分工

| 条目 | 产物形态（C46 的输入） | C46 负责 |
|---|---|---|
| C11 property | 属性 → 后备字段 + `get_X/set_X`；显式实现保留为 `T IFoo.get_X()`（C11 决策 8）；`new` 平移为 `new` 方法（C11 决策 6，不造 Java 注解） | 消限定名/public、隐藏改名、协变桥接 |
| C12 indexer | `T IFoo.GetItem(…)`/`SetItem` 显式方法对（IX-3）；协变索引器明确划给 C46（C12 D7/R4） | 同上 |
| C13 event | `void IFoo.AddXxxListener/RemoveXxxListener` 显式方法对（EV-6） | 同上 |
| C14 operator | `op_*` → 命名方法；运算符不参与显式接口（C# 语法不允许显式实现运算符，静态抽象运算符除外→C36） | 无交集 |
| C27 record with | 合成 `virtual Base Cs2jWithClone()` + `override Derived Cs2jWithClone()`（RC-1/RC-2）——天然协变返回对 | CR 规则识别并归类为「直映保留」，不重复桥接 |
| C39 finalizer | 占 CS2J5200-5219 | 码位避让，无共享逻辑 |
| C36 防线 | CS2J0585 静态抽象盘点/拒绝 | 见 §2 决策 12，只跳过引用 |
| C20 partial merge | 合并后单一类型声明 | C46 须晚于 C20（结论见 §3.3） |

### 1.4 硬不变式

1. **虚分派语义不变**：改写后经接口引用、基类引用的观察行为与原程序逐点一致（.NET xunit 守门）；禁止用「适配类实现某接口」模拟双接口分歧实现——那会改变 `is/as`/强转与对象身份。
2. **接口身份不变**：类型仍声明实现同一接口集合；`is IFoo`、接口强转、泛型约束 `T : IFoo` 可用性不变。
3. **不可表达即显式报码**：同名不同接口且实现体分歧等 Java 单分派不可表达形态，plan 原子不动 + CS2J51xx，禁止静默选一个实现体（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)）。
4. **反射面变化显式化**：nameof 能字面量化则量化；`GetMethod("IFoo.Foo")`、`GetInterfaceMap`、按名序列化只警告不承诺（风险 7，[L1105](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1105-L1105)）。
5. 中间树恒可编译、plan 原子（声明与全部站点同生同灭）、整树重编译归属回退、幂等、干净文件字节复制（含 BOM）——与 C11 完全同纪律。

### 1.5 目标形态示例（说明性，三种问题各一）

**① 显式实现与类自有成员碰撞（SI-1 + SI-2 + SI-5）**

```csharp
// 输入
public class C : IFoo {
    public void Foo() { /* 类槽体 A */ }
    void IFoo.Foo() { /* 接口槽体 B */ }
    void Use(IFoo i, C c) { i.Foo(); c.Foo(); }   // 静态绑定：分别命中 B、A
}
// 中间树（C# 可编译；Java 侧一名一槽，无静默丢方法）
public class C : IFoo {
    public void Foo() { /* 接口槽体 B：占规范名，public，满足 IFoo */ }
    public void Foo_cs2jClass() { /* 类槽体 A：让位改名 */ }
    void Use(IFoo i, C c) { i.Foo(); c.Foo_cs2jClass(); }
}
```

无碰撞的纯显式实现仅去限定名 + 提 public，接口引用调用点零改动；同名双接口且体等价时合并为一个 public 方法，体分歧则整 plan 不动并报 CS2J5191。

**② `new` 隐藏按静态类型决议（NH-1）**

```csharp
// 输入
class Base { public string Name() => "base"; }
class Derived : Base { public new string Name() => "derived"; }
// 中间树
class Base { public string Name() => "base"; }
class Derived : Base {
    public string Name_cs2jNew() => "derived";   // 新槽改名，new 令牌移除
    // 继承的 Base.Name() 原样保留为另一槽
}
// Base b = new Derived(); b.Name() → "base"（不动）；Derived d; d.Name_cs2jNew() → "derived"（重写）
```

`new virtual` 场景下 newslot 及其派生 override **整组**改同名；纯 base 委托扩可见性（NH-3）保留同名 public 桥，体为 `base.Name()`/`base.setName(value)`。

**③ 协变返回擦除冲突（CR-2 桥接）**

```csharp
// 输入：Java 擦除后 IList<string> 不是 IList<object> 子类，直映不可编译
class Base { public virtual IList<object> Items() => ...; }
class Derived : Base { public override IList<string> Items() => ...; }
// 中间树：编译器 bridge method 的显式 C# 版
class Derived : Base {
    public override IList<object> Items() => Items_cs2jCovariant();  // 基槽签名
    public IList<string> Items_cs2jCovariant() => ...;                // 原体，独立名
}
// Base b; b.Items() 不动（虚分派进 bridge）；Derived d; d.Items_cs2jCovariant() 按原 override 绑定重写
```

引用类型普通协变（`override Derived Clone()`）CR-1 只审计不改写；值类型卷入报 CS2J5195。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | ★显式实现的命名规则 | **接口槽位方法保留规范名、让位者改名**：`void IFoo.Foo()` → `public void Foo()`；属性/索引器/事件经 C11–C14 已为 `get_X/set_X`、`GetItem/SetItem`、`AddXxxListener/RemoveXxxListener`，消限定名后**沿用不动**。仅当类自身存在同名同槽成员（或同槽被占）时，**让位的类成员**改名 `{Name}_cs2jClass`，接口实现体占规范名 | 否决 `IFoo_Foo` 前缀式：Java 接口要求实现方法恰为槽位名，前缀名不满足接口、还需再发委托壳，两层名字；让接口实现占规范名则调用点改动最小，且与现转换器补丁方向一致（接口 getName 标准名、类成员 `getName$Class`）。`$Class` 中的 `$` 替换为 `_cs2jClass`：合法 C# 标识符、族内 `__cs2j` 惯例同构、进共享命名预留表防撞；终命名由 JK-1 链尾兜底 |
| 2 | 同名不同接口 `IA.F()/IB.F()` | ① 两实现体**同源/等价**（同一隐式实现、或二者皆纯转发同一成员）→ 合并为一个 public `F()` 满足双接口；② **实现体分歧 → CS2J5191，plan 原子跳过**（v1 不做适配类模拟）；③ 仅一个显式实现而另一接口本就由同一槽隐式满足 → 常规提升即可 | Java 一个类只能有一个 `f()` 槽位，无法对 IA/IB 分派两个方法体；分歧实现是真实语义差（C# 接口映射按引用静态类型选体），只能人工重构。同源判定用符号绑定 + 体归一化比较，保守分歧（拿不准）按②处理 |
| 3 | 访问级别 | 显式实现消限定名时**一律提升 public**：无修饰符/原 private 形态 → public；访问器跟随 C11–C12 已定的方法可访问性；`internal` 维持现状映射（internal→public 既有先例） | Java 接口方法必须 public；C# 显式实现本就只能经接口引用访问，提升 public 不放大任何「经类型名的新调用能力」之外的语义，改名/接口检查由调用点重写保住 |
| 4 | ★调用点重写范围（SI） | 以 `SemanticModel.GetSymbolInfo/GetOperation` 绑定的符号为准分五类：**(a)** 接收者静态类型为接口（含接口变量、参数、字段）→ 绑接口槽位符号，**不动**；**(b)** 泛型约束 `T : IFoo` 的 `t.Foo()` → 接口分派，不动；**(c)** `((IFoo)x).Foo()`/`(x as IFoo)?.Foo()`/is 模式后调用 → 绑接口槽，不动（仅清理因形态需要的多余强转）；**(d)** 绑定到**让位类成员符号**的调用（接收者静态类型为实现类/派生类）→ 改 `{Name}_cs2jClass`；**(e)** `base.Foo()`/this 内直连 → 按绑定符号归 (d) 或不动 | 禁止按接收者语法文本/名字猜分派；泛型接口用 `ExplicitInterfaceImplementations[0].ContainingType` 的符号同一性判定，**不沿用** [IndexerTransformer.GetExplicitInterfaceType:246-253](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs#L246-L253) 的简单名文本匹配。方法组/委托绑定同 (d) 规则改写 |
| 5 | 泛型接口显式实现 `IFoo<T>.Bar` | 槽位名仍为 `Bar`（不带类型实参）；接口映射按构造符号 `IFoo<int>.Bar` 与开放定义分别登记；同一类显式实现两个不同构造的 `IFoo<T>.Bar` 且擦除后参数签名相同 → 按决策 2 同源合并或 CS2J5191 | C# 元数据槽位名不含 arity；类型实参只进映射表不进方法名。构造间签名擦除分歧属 C02 泛型深化的兄弟场景，C46 只判定、不改名擦除（擦除改名归 C02/JK 既有 `_erasure_N` 机制） |
| 6 | 接口重新实现（reimplementation） | `class D : B, IFoo`（B 已实现 IFoo）由整树接口映射索引按类型逐槽重算：D 声明的同槽成员（隐式/显式）经本规则提升/改名后，D 的 IFoo 槽自然映射到 D 的 public override/新方法；映射候选不唯一（泛型构造、多接口菱形）→ CS2J5197 跳过 | C# 重新实现是按槽重绑；B 的显式实现在 B 的输出树上已 public，D 用普通 override 即可在 Java 侧同效。索引复用 `SymbolEqualityComparer.Default`，不做语法推测 |
| 7 | ★`new` 隐藏主策略 | **改名 + 全调用点按静态绑定重写**：隐藏成员（含无 `new` 令牌的隐式隐藏，warning CS0108 形态）改名 `{Name}_cs2jNew`，**移除 `new` 修饰符**；调用点绑定到派生槽符号 → 改新名，绑定基槽 → 不动。属性/索引器/事件对 C11–C13 产物的方法对**配对改名**（`get_X/set_X` 同步） | 否决 `NewX` 前缀：Pascal 前缀可与用户既有 `NewX` 碰撞、破坏 `get_/set_` 配对惯例；后缀 + 预留表确定性防撞。改名后两方法在 C#/Java 都按名字解析，编译期决议被物化为语法事实。方法组/委托/反射字符串同 SI 纪律 |
| 8 | 纯 base 委托（桥接例外） | 隐藏成员访问器体仅 `base.X`/`base.X = value` 且目的为扩可见性（protected→public，现 [PropertyHidingTests:72-116](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertyHidingTests.cs#L72-L116) 场景）→ **保留同名 public 桥接方法**（体即 super/base 转发），不改名、不改站点，plan 记 Bridged | 该形态在 Java 侧虽变为虚覆写，但体纯转发基槽，观察等价；扩大的可访问性正是其意图。除此外一律改名，桥接不作为常规策略 |
| 9 | override/hiding/newslot 组合矩阵 | 见下表；`new virtual` 引入的**新虚槽链整组改名**（newslot 方法及其全部派生 override 同步换名），基槽链不动 | C# 允许「另开虚槽」，Java 无法对同名方法开第二虚槽；整组改名后两条虚链在两侧都独立 |
| 10 | ★协变返回保留条件 | **同时满足即直映保留**（CR-1，仅审计计数）：基返回类型 Rb 与派生返回 Rd 均为引用类型；Rd→Rb 在 Java 映射后仍是合法子类化（含非泛型类/接口、普通数组）；无 C02 哨兵签名冲突。C# 9 协变以引用同一性转换为前提 | Java 5+ 原生协变返回；C27 的 `Cs2jWithClone` 克隆对即此形态（RC-2），CR 分析器须按方法名/标记识别 C27 合成方法，避免重复加工 |
| 11 | ★擦除冲突桥接合成（CR-2） | 不满足决策 10 但可表达时合成编译器同款 bridge：基槽 `public override Rb M(){ return M_cs2jCovariant(); }`（返回基类型、虚签名）+ 改名普通方法 `public Rd M_cs2jCovariant(){ …原 override 体… }`；接收者静态类型为派生类型、原绑定到协变 override 符号的调用点改写新名；经基类型/接口的调用不动。v1 仅支持**引用类型实参的泛型/接口协变** | 即 javac 为协变/泛型自动生成的 bridge method 的 C# 显式版；.NET 测试同时验证两条调用路径返回类型与多态。派生方法不再带 override（是独立 newslot 名），sealed override 场景 bridge 标 sealed |
| 12 | 值类型/不可识别协变 | 返回类型涉值类型（含可空值类型、枚举、struct）或分析器无法证明 Java 可表达 → **CS2J5195，plan 跳过，不猜装箱桥** | C# 协变返回本身不接受装箱转换（不存在值类型协变程序），命中多为泛型实例化/分析边界；宁报不误。泛型擦除冲突中夹带值类型实参的同样报码，等 C02 token 方案联评 |
| 13 | 静态抽象/虚接口成员 | SI/NH/CR 分析器遇 `static abstract`/`static virtual` 接口成员（含静态抽象运算符/属性）→ plan 不生成，聚合 **CS2J5198 Info** 并注明「归 C36/CS2J0585」，不重复报错 | 一条防线一个码；C36 未翻转时该成员原样留树（后续转换器行为不变），翻转后由 0585 拦截 |
| 14 | 构造函数/字段/静态隐藏 | 构造函数 `new` 不涉（构造函数非按名分派）；`new static` 方法/字段按 NH-1 静态版改名（静态本就编译期决议，改名平凡安全）；`new` 字段隐藏并入同一改名规则 | 字段隐藏改名机械、零虚分派风险，M4 顺带覆盖；M2 只做实例方法 |
| 15 | 反射/nameof/接口映射 | `nameof(IFoo.Foo)` 等绑定到槽位成员的 nameof → 字面量化为原名常量（与 C11 PD-9/JK-4a 同法）；`GetMethod("…Foo")`、`GetInterfaceMap(typeof(IFoo))`、`Delegate.CreateDelegate` 字符串名 → **CS2J5193 Warning 留站点不动**；表达式树内调用点 → plan 跳过 CS2J5194（force 不解除） | 改名改的是源内绑定；反射目标是运行时字符串契约，无法静态保证。沿用 E17「反射不保证」+ overrides 机制（风险 7） |
| 16 | 规则执行相位 | Signatures 规则在族执行器中**晚于 Property/Indexer/Event/Operator 四类规则**（消费其方法化产物）；方法形态规则（SI-1/NH-1/CR-* 方法面）不依赖 C12/C13，可在只有 C11 的树上工作——分析器对「仍为访问器语法」的显式实现只登记不产 plan（Info） | 需与 C11 锚点确认规则排序契约（Kind 相位 vs 全局 Order，开放问题 4）；M2 交付即按方法形态验证，不阻塞于 C12/C13 |
| 17 | 支撑库 | **无**：不注入支撑工程、不新建 Java compat 类 | 全部为 BCL 合法 C# 形态 |

**决策 9 组合矩阵（方法面；属性/索引器/事件在 C11–C13 方法化后同构）**

| 声明形态 | C# 槽位语义 | C46 产物 | 调用点 |
|---|---|---|---|
| `virtual`/`abstract`/普通 override | 单虚槽 | 不动 | 不动 |
| `new`（fossil） | 编译期按静态类型选槽 | 派生成员改名、去 new（NH-1） | 静态绑定派生槽→新名 |
| `new virtual` + 链上 override | 两条独立虚槽 | newslot 链整组改名（NH-2） | 按绑定槽分写两名 |
| `new abstract` | 派生新抽象槽 | 随 NH-2 整组改名 | 同上 |
| `new static`/静态成员隐藏 | 静态决议 | 静态改名（NH-1 静态版） | 静态绑定点改名 |
| 纯 base 委托 `new` | 扩可见性 | 同名桥接保留（NH-3，Bridged） | 不动 |
| override + 引用协变返回 | 单虚槽、派生返回类型 | 保留（CR-1） | 不动 |
| override + 擦除冲突协变 | 单虚槽 | bridge + 改名派生方法（CR-2） | 派生静态类型点→新名 |
| override + 值类型/不可判协变 | —— | CS2J5195 跳过（CR-3） | 不动 |
| 显式接口实现（无碰撞） | 接口槽私有实现 | public 槽位名、去限定名（SI-1） | 接口分派点不动 |
| 显式实现 + 类自有同槽成员 | 两体两入口 | 接口体占槽位名，类成员 `_cs2jClass`（SI-2） | 绑类成员点改名 |
| 同名双接口、体分歧 | 两槽两体 | CS2J5191 跳过（SI-3） | 不动 |
| 接口重新实现 | 槽位重绑 | 映射重算 + 常规规则（SI-4） | 按映射符号分类 |

---

## 3. 链位与族契约

### 3.1 现状（已核实）

[PassStubCatalog.cs:192-201](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L192-L201)：id `member-desugar`、Stage=`Declarative`、Status=`Stub`、默认关、RunAfter=`{struct-value-semantics, closure-lift}`、`catalogItems="C11, C12, C13, C14, C27, C39, C46"`——**C46 已在字符串中**，本任务不编辑该文件。C11 翻转时该 stub 条目删除、Implemented 描述符 CatalogItems 收窄为 `"C11"`（C11 §3.3），后续条目按合入序追加；C46 合入时追加 `"C46"`（一行字符串，预期协调点）。

### 3.2 本任务的注册姿态

- **不注册/不翻转**：Signatures 是族内规则集，经 C11 建立的 `[MemberDesugarRule]` 程序集发现自动装配，不新增 pass、不新增适配器、不改 [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) switch、不增 CLI 开关；选项走族 options 子节 `rules.signatures`，经通用 `preprocess --pass member-desugar` 与 convert-project 链透传（同 C27 §5）。
- **代码落位**：`src/CSharpToJava.Core/MemberDesugar/Signatures/`（C11 §4.1 已预留 `MemberDesugarKind.Signature`，[c11 设计:184](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c11-property-desugar-design.md#L184-L184)）。
- **码位确认**：占 **CS2J5190-5199**（C11 §11 预留建议、C27 §11 明确「5190-5199 建议归 C46」、C39 占 5200-5219、C27 占 52xx）。本文档确认该段位，不挤占他条。
- **实施前提**：base 须在 C11 分支合入之后（§12）；M2 方法面以 C11 已合为最低条件，属性/索引器/事件面（M3）分别等待 C12/C13。

### 3.3 与 C20（partial 合并）的顺序结论

catalog 要求「C46 须晚于 C20（partial 合并后签名才完整）」。结论：**不给 member-desugar 追加 partial-merge RunAfter 边**。

1. C20 设计 §3.3-4 已裁定：拓扑传递保证——member-desugar 锚 `struct-value-semantics`+`closure-lift`（均在链后），全序必有 `index(partial-merge) < index(member-desugar)`，并在 C20 翻转时于 [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 追加该断言锁死。
2. C20 未实施期间无影响：分析器基于 `CSharpCompilation` 符号（partial 多部件天然按符号合并），主转换器既有 PartialType 合并路径继续工作；C46 的 plan/TouchedFiles 以符号为单位，跨部件声明与站点照常闭合（同 C11 §4.3）。
3. C20 翻转后本规则零改动：输入树从「多部件语法」变为「单部件语法」对符号索引透明；C46 单测含 partial 两部件各显式实现一个接口成员的用例，两种输入形态都守门。

### 3.4 不翻转期间的行为

族 pass 默认关 → 本规则完全不执行（无目录、无耗时、链行为零变化）；族 pass 启用但本规则全部 skip 时输出与仅 C11–C14 一致（零 diff 有计数）。转换器侧 §1.1 三个补丁在消费 PR 前原样保留，是默认关期的安全网。

---

## 4. Core 组件设计

### 4.1 目录与文件所有权

```
src\CSharpToJava.Core\MemberDesugar\
  Signatures\                         # 【C46 独占】
    SignatureModels.cs                # SignatureOptions/Statistics/Diagnostic/Severity/FileResult/SignatureKind
    SignatureDiagnostics.cs           # CS2J5190-5199 常量与消息
    SignatureSyntaxFacts.cs           # 调用点五分类(决策4)、nameof/反射/表达式树/dynamic 判定、
                                      #   体等价比较（SI-3 同源判定）、强转最小化
    SignatureExplicitizationAnalyzer.cs  # 整树签名索引（见 4.2），实现族 Analyze 契约
    ExplicitInterface\
      InterfaceMappingIndex.cs        # 类型→接口→槽位→实现符号映射；重新实现/菱形/泛型构造
      SiPlan.cs / SiPlanBuilder.cs    # 每「实现符号 × 接口槽」一 plan
      ExplicitInterfaceDeclarationRewriter.cs  # SI-1..SI-4 声明侧
      ExplicitInterfaceCallSiteRewriter.cs     # SI-5 调用点（五分类）
    Hiding\
      HideGroupResolver.cs            # IsHideByName 语义判定 + new virtual 槽链成组
      NhPlan.cs / HidingRewriter.cs   # NH-1..NH-4（声明改名/去 new、站点重写、纯委托桥接）
    Covariant\
      CovariantChainAnalyzer.cs       # OverriddenMethod 链 + Java 映射后子类化/擦除判定
      CrPlan.cs / CovariantBridgeRewriter.cs   # CR-1 审计 / CR-2 桥接合成 / CR-3 报码
```

共享根目录 9 文件**只读引用**（C11 所有权表）：复用 `MemberDesugarAnalyzer` 的编译绑定与站点枚举入口、`MemberDesugarPlan` 基类（Id/Kind/Symbol/Outcome/TouchedFiles/OwnsTouch/Fail*）、`BackingFieldNaming` 跨规则命名预留表（登记 `_cs2jClass/_cs2jNew/_cs2jCovariant` 新名防撞）、`AccessorsToMethods`（修饰符/public 提升骨架）、`MemberDesugarSyntaxFacts`（RewriteAnnotation、表达式树包含、dynamic、临时提升、FormatAndValidate）、族回退迭代器。确需放宽 internal 可见性只改可见性，由 C11–C13 既有单测守门。

### 4.2 SignatureExplicitizationAnalyzer（整树一次）

族 BindCompilation 后产出四张索引：

1. **显式实现索引**：全编译源内成员中 `ExplicitInterfaceImplementations.Length > 0` 的方法/属性/索引器/事件符号（C11–C14 后主要为方法符号），记录实现符号 ↔ 接口槽符号（`IMethodSymbol/IPropertySymbol/IEventSymbol`）、含入类型、声明部件、可访问性、访问器对（get/set、add/remove）。
2. **接口映射索引**（InterfaceMappingIndex）：每个含入类型对其 AllInterfaces 逐槽解析实现符号（显式 + 隐式 + 基类继承 + **重新实现重绑**），泛型接口按开放定义/构造双轨；输出「同槽多候选」「同名双接口体分歧」「映射缺口」三类异常清单。
3. **隐藏组索引**（HideGroupResolver）：对每个非 override、非 static（static 另计）成员符号，沿基类型链查同名同参数槽：`IsHideByName` 语义（Roslyn `IMethodSymbol.IsHideByIdx` 不可直接用时用「基存在同槽且本成员非 override」判定，并核对 `new` 令牌有无——两者都管，覆盖隐式隐藏 CS0108）；`new virtual` 成员向下收集全部 override 其 newslot 的派生方法成组；纯 base 委托体走 [PropertyTransformer.IsPureBaseDelegation:114-128](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L114-L128) 同款判定并泛化到方法/索引器/事件。
4. **协变链索引**（CovariantChainAnalyzer）：每个 `IsOverride && !ReturnsVoid` 成员比较 `ReturnType` 与 `OverriddenMethod.ReturnType`：同一性→无关；引用转换存在→CR-1 候选，再过 Java 映射子类化判定（复用 [JavaNaming](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs) 擦除签名工具 + C02 映射事实）判 CR-1 直映/CR-2 可桥接；值类型卷入或不可判→CR-3。识别 C27 合成的 `Cs2jWithClone`（按族 RewriteAnnotation/命名约定）固定归 CR-1。

站点枚举：遍历全编译所有语法树的 invocation/member access/element access（C12 后已为方法调用）/`+=`/`-=`/方法组/nameof/反射 API/`base.`/对象初始化器，经 GetSymbolInfo 绑定到上述四索引中的符号并分类；表达式树内与 dynamic 站点用共享判定工具标记。

### 4.3 三阶段数据流（与 C11 同构）

1. **Analyze（整树一次）**：建四张索引 + 全站点分类；规则按相位排序（访问器四类 → Signature；Signature 内 SI → NH → CR，因 CR 桥接产物的新名可能被 NH 组扫描二次见到——靠 RewriteAnnotation 与「已生成符号不再认领」幂等谓词防回环）。
2. **Plan（每符号/每组成组一 plan）**：SiPlan 以「实现符号×槽位」为粒度（同类多接口同槽可合组）；NhPlan 以隐藏组（含 newslot 链）为粒度；CrPlan 以 override 符号为粒度。TouchedFiles = 全部声明部件文件 + 全部分类站点文件（含接口声明文件联动）；确定性排序（类型全名 + 成员名 + span）。不可行项计划期定案 Infeasible + 码，不落盘。
3. **Rewrite（逐文件）**：原件重放模型——每轮取命中本文件的 active plans，先声明侧重写（SI 去限定名/提 public、NH 改名去 new、CR 插 bridge），挂 RewriteAnnotation，再站点重写（跨文件按符号绑定），FormatAndValidate 三候选快筛；CLI 驱动族迭代器：重建编译 → DiffErrors → TryAttributeDiagnostic 按 TouchedFiles/OwnsTouch 归属 → 单 plan 回退（族 CS2J5101）/不可归属全量回退（族 CS2J5100），上限 1024 轮；交付以整树重编译 + .NET xunit 为准。跨文件一致性由「plan 持全部站点文件 + 整文件重放回滚」保证（C11 §4.3 已验证范式），不允许「只改声明漏站点」的中间态出盘。

### 4.4 规则全集

**SI（显式接口，Order 40-49）**

| 规则 | 内容 |
|---|---|
| SI-1 提升 | 无碰撞显式实现：去 `ExplicitInterfaceSpecifier`、补 public、规范槽位名（决策 1/3）；属性/索引器/事件方法对整体提升；DIM 与显式实现并存时以实现符号为准 |
| SI-2 让位改名 | 类自有同槽成员 → `{Name}_cs2jClass`（预留表登记防撞，二撞追加 `_2`），接口实现体占槽位名；SI-5 站点联动 |
| SI-3 双接口 | 同名多接口：体等价合并一方法；分歧 CS2J5191 整组不动（决策 2） |
| SI-4 泛型/重实现 | 泛型构造映射（决策 5）、重新实现槽位重算（决策 6）；候选歧义 CS2J5197 |
| SI-5 调用点 | 决策 4 五分类改写；方法组/委托/强转最小化；接口声明在树内时同步核对 |
| SI-6 闸门 | 表达式树内站点 CS2J5194；dynamic CS2J5194（force 不解除）；反射/接口映射 CS2J5193；静态抽象 CS2J5198（决策 13/15） |
| SI-7 残余对账 | 输出树每个 `ExplicitInterfaceSpecifierSyntax` 必须属于已重写或带码跳过；strict 下残余 CS2J5199 |

**NH（new 隐藏，Order 50-59）**

| 规则 | 内容 |
|---|---|
| NH-1 化石隐藏改名 | `new`/隐式隐藏实例与静态成员 → `{Name}_cs2jNew`、去 `new` 令牌；全站点按静态绑定符号改写（决策 7/14）；访问器方法对配对改名 |
| NH-2 新虚槽组 | `new virtual/abstract` 与其派生 override 链整组改名；基槽链与两链调用点分别绑定两名（决策 9） |
| NH-3 委托桥接 | 纯 base 委托扩可见性 → 同名 public 桥接保留，记 Bridged（决策 8） |
| NH-4 字段/静态 | `new` 字段、`new static` 方法/字段改名（决策 14）；事件隐藏在 C13 方法对上做 |
| NH-5 闸门/对账 | 不可判定绑定（dynamic）CS2J5194 整 plan 不动；输出树零「无登记的隐藏性 new」——残余 CS2J5199 |

**CR（协变返回，Order 60-69）**

| 规则 | 内容 |
|---|---|
| CR-1 直映审计 | 满足决策 10：不改写，plan 记 Retained 与依据（引用转换链 + Java 映射子类化证据）；C27 工厂对固定命中本规则 |
| CR-2 擦除桥接 | 决策 11：bridge（基返回类型虚签名）+ `M_cs2jCovariant` 改名方法；派生静态类型绑定点改写；sealed 形态保留 |
| CR-3 报码 | 值类型/不可判/夹带值类型实参 → CS2J5195 整 plan 不动（决策 12） |
| CR-4 对账 | 输出树每个协变 override 必须有 Retained/Bridged/Skipped 归宿；未归类 → CS2J5199 |

---

## 5. CLI 集成（不新增适配器）

1. **执行/选项**：族适配器整树自驱动骨架不变；`SignatureOptions`（`bool Strict` 默认 true、`bool ReportInfo`、`IReadOnlySet<string>? SkipSignatures`（类型/`类型::成员`）、`bool AllowDivergentInterfaceBodies` 预留 v1 强制 false）挂在族 options 的 `rules.signatures` 子节；不新增 verb/开关/描述符。
2. **统计汇入**：SignatureStatistics 投影通用 5 字段进族统计；族摘要行追加 signatures 段：
   `member-desugar: … signatures: <SiPlans> explicit promoted, <DisplacedRenamed> displaced renamed, <DualMerged> dual-interface merged, <HideRenamed>/<HideBridged> hiding, <CovRetained>/<CovBridged> covariant, <CallSites> call-site(s) rewritten, <Skipped> skipped, <Residual> residual`。
   特有计数：ExplicitMembersDetected（按方法/属性/索引器/事件分桶）、Promoted、DisplacedRenamed、DualInterfaceMerged、ReimplementationRemapped、GenericExplicit、HidingDetected（new/隐式/newslot/静态分桶）、HideRenamed、HideBridged、CovariantChains（Retained/Bridged/ValueTypeSkipped）、CallSitesRewritten（接口分派点计数仅审计不改）、ReflectionWarnSites、PlansSkipped（按码分桶）、ResidualExplicitSpecifiers/ResidualHidingNew/ResidualCovariant。
3. **诊断汇入**：经族适配器统一选择器映射 PassDiagnostic，MSBuild 格式 `文件(行,列): warning/error CS2J519x: [Ns.Type::Member] 描述`；Info 仅 verbose；restore/build/test 失败沿用族 CS2J5102/5103/5104。
4. **指纹/目录**：沿用族一份 `.cs2j-member-desugar-src`；非启用零痕迹；CatalogItems 追加 C46 后 `--list-passes` 自动展示。
5. **单文件模式**：签名 plan 几乎必然跨文件（接口声明 + 实现 + 调用点），单文件入口沿用族 CS2J5109 闸门（非自包含 plan 排除），不开特例。

---

## 6. 诊断策略（CS2J5190-5199）

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5190 | Info | 签名 plan 建立（实现符号/槽位/形态/TouchedFiles 计数），仅 verbose |
| CS2J5191 | Warning（strict Error） | 同名不同接口实现体分歧（SI-3），Java 单分派不可表达，plan 原子跳过；消息列两接口名与建议（人工合并为单实现或改不同名调用面） |
| CS2J5192 | Warning（strict Error） | 命名预留表防撞失败/同槽多候选且无法确定让位者（含跨类型成员名死锁），plan 跳过 |
| CS2J5193 | Warning | 反射/字符串契约风险站点：`GetMethod`/`GetInterfaceMap`/`Delegate.CreateDelegate` 等字符串名命中被改成员，站点保留不改；不阻断 |
| CS2J5194 | Warning（strict Error） | 表达式树内（force 不解除）或 dynamic 绑定调用点不可判定，plan 跳过 |
| CS2J5195 | Warning（strict Error） | 值类型卷入/不可证明 Java 可表达的协变返回（CR-3），plan 跳过，不做装箱猜测 |
| CS2J5196 | Warning（strict Error） | 泛型擦除协变桥接 v1 不可行（如多重泛型约束下 bridge 签名与既有重载撞车），plan 跳过，留待 C02 联评 |
| CS2J5197 | Warning（strict Error） | 接口重新实现/菱形/泛型构造下槽位映射候选不唯一（SI-4），plan 跳过 |
| CS2J5198 | Info | 命中 `static abstract`/`static virtual` 接口成员，本规则不领，归 C36/CS2J0585 防线 |
| CS2J5199 | Warning（strict Error） | residual 闸门：输出树残留无对应 skip 诊断的 ExplicitInterfaceSpecifier / 隐藏性 new / 未归类协变链（SI-7、NH-5、CR-4 汇总） |

原则与 C11 §6 一致：不可判定即显式诊断 + 声明与站点同生同灭；Warning 经 Strict（默认 true）升 Error；SkippedByReason 按码分桶进统计与 sdk 盘点；**禁止静默选实现体、禁止丢成员、禁止注释占位**。回退复用族 CS2J5100/5101，不新造。

---

## 7. 测试计划

### 7.1 Core 单测（`tests/CSharpToJava.ProjectTests/MemberDesugarSignatureUnitTests.cs`）

内联源码 → CSharpCompilation → Bind/RewriteFile → 忽略空白断言 + `AssertOutputCompiles` + 二次执行零 diff：

1. **显式接口方法**：无碰撞 `void IFoo.Foo()` → `public void Foo()`，无修饰符→public；接口引用调用保持、类引用调用经接口形态验证；显式 + 类自有同名两体（接口体占槽位名、类成员 `Foo_cs2jClass`、两类静态类型调用点各归各位）；同名双接口 `IA.F()/IB.F()` 体等价合并、体分歧 CS2J5191；泛型接口 `IFoo<int>.Bar`/`IFoo<string>.Bar`；多接口一次实现（单 public 方法满足双接口，调用两接口引用均通）；接口重新实现（B 显式实现、D 重列 IFoo 并提供新版）；`((IFoo)x).`/`as`/`is`/约束 `T:IFoo` 五类接收者；方法组 `Action a = c.Foo` 绑定分类；跨文件接口+实现+调用三文件联动。
2. **属性/索引器/事件显式实现**：消费 C11–C13 产物（`IFoo.get_X`、`IFoo.GetItem`、`IFoo.AddXxxListener`）做 SI-1/2/5；显式事件后备存储不重复发射（对齐 [InterfaceEventListenerNamingTests:74-93](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterfaceEventListenerNamingTests.cs#L74-L93)）；与 C12 多维索引器共处；未启用对应访问器规则时仅登记 Info 不产 plan（决策 16）。
3. **new 隐藏**：方法 `new`（有/无令牌两种）基类引用调基槽、派生引用调新名（.NET 行为断言）；`new virtual` 三层链两虚槽各组调用点；`new static`；纯 base 委托扩可见性 → 同名桥接 + `base.getX()` 体；属性/索引器访问器配对改名；字段隐藏；改名后 `new` 令牌已移除（无 CS0109）。
4. **协变返回**：引用类型直映（`override Derived Clone()`）保留；泛型擦除冲突（`IEnumerable<string>` vs `IEnumerable<object>`）合成 bridge + `M_cs2jCovariant`，基/派生两静态类型调用点分别断言返回运行时类型；sealed override；C27 `Cs2jWithClone` 对固定 Retained 不重复桥；值类型卷入 CS2J5195；不可桥重载撞车 CS2J5196。
5. **闸门/边界**：表达式树内显式接口调用 CS2J5194 整 plan 不动；dynamic CS2J5194；`GetInterfaceMap`/`GetMethod("IFoo.Foo")` CS2J5193 站点保留；`nameof(IFoo.Foo)` 字面量化；静态抽象 CS2J5198；构造函数 `new` 不误伤；普通 virtual/override/运算符零误伤；干净文件字节一致 + BOM；幂等（bridge/改名二次运行零 diff）。

### 7.2 项目级 fixture

在 C11 的 `tests/SampleMemberDesugarSolution/` 内新增签名场景文件（复用同一 sln/工程，不另建）：

- Lib：显式实现方法库（无碰撞/碰撞/双接口同源/泛型接口/重新实现各一型）、`new` 隐藏层次（含 new virtual 两槽链、纯委托扩可见性）、协变返回层次（引用直映 + 泛型桥接）。
- App：跨文件接口分派、强转/as 分派、约束泛型分派、隐藏成员两类静态类型调用。
- Tests（.NET 半区预言机）：接口引用与类引用分别调用的结果断言、双接口同源一致性、隐藏按静态类型决议、协变两路径返回类型与多态——全部在改写后 C# 上以 xunit 断言，分派错误在 .NET 侧直接红。
- 项目级测试 `MemberDesugarSignatureProjectTests.cs`：Success、统计计数、中间树文本断言（零 ExplicitInterfaceSpecifier、零隐藏性 new、含/不含 bridge 的精确断言）、`dotnet build/test -c Release` 退出码 0。

### 7.3 Release build/test 与双端分派对照

- `dotnet build -c Release`（CSharpToJavaConverter.slnx）；`dotnet test -c Release` 三工程（Tests/ProjectTests/ConversionTests）。
- **虚方法表语义预言机**：7.1-3/7.1-4 每个分派用例同时记录「接收者静态类型 × 运行时类型 → 命中体」矩阵，作为后续 Java 半区消费 PR 的对照底稿（.NET 与 Java 双端同矩阵）。
- **红灯对照**：不改 [PropertyHidingTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertyHidingTests.cs)/[ExplicitInterfacePropertyConflictTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ExplicitInterfacePropertyConflictTests.cs)/[InterfaceIndexerReturnTypeTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterfaceIndexerReturnTypeTests.cs)/[InterfaceEventListenerNamingTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterfaceEventListenerNamingTests.cs) 一行（直连单文件管线安全网）；为每用例建 C# 半区对应断言，形成「Java 侧补丁行为 ↔ C# 侧显式形态」对照表，作为转换器消费 PR 的核对底稿（同 C11 §7.6）。

### 7.4 residual 闸门

1. 输出树全量扫描：`ExplicitInterfaceSpecifierSyntax` 计数 = 0（或每条对应 CS2J5191/5194/5197/5198 skip）；隐藏性 `new` 令牌计数 = 0（移除或带码登记）；协变 override 全部有 Retained/Bridged/Skipped 归宿。strict 下 CS2J5199 非零即退出码非零。
2. 双向对账：每条 5191/5195/5196/5197 报码能反查到树中一个具体成员/站点；回退演练（CS2J5100 全量、CS2J5101 单 plan）后树无半成品、无残留中间目录。
3. 既有 fixture（SampleProject、SampleRecordSolution、SampleJavaKeywordSolution 等）启用后 build/test 全绿；上游各 pass 统计零变化。

### 7.5 CLI / 一致性

族 pass 默认关时零痕迹；启用后 `--list-passes` 的 CatalogItems 含 C46；摘要 signatures 段与 SkippedByReason 分桶正确；通用 `--enable-pass`/`--disable-pass` 与 options 子节透传生效；不新增开关的断言。

---

## 8. 默认开启验收门槛

本规则不单独翻默认开（随族 pass）。族默认开（C11 §8）之外，Signatures 子集还须满足：

1. §7 全部单测/fixture/Release 三工程测试全绿；residual 闸门与回退演练通过；幂等/BOM 零失败。
2. csharp-sdk（只读训练集）启用扫描：三类签名计数总量与分布（显式实现按四成员种类、隐藏按 new/隐式/newslot、协变按 Retained/桥/报码）；**CS2J5191/5195/5196/5197 每命中人工定性**（真不可表达 vs 分析器过保守），过保守项修到清零或进入明确后续。
3. **Java 半区消费清单完成**（后续转换器 PR，本特性不实施）：MethodTransformer 消去 new→Override 错映、显式实现 public 与命名派生可消费去糖树；Property/Indexer/Event 三文件零散补丁在预处理输入上证实不可达（删除留独立 PR）；AddMethodIfNotDuplicateInternal 静默丢方法路径对预处理输入零命中并有测试证明；协变 bridge 形态经完整链路产出可编译 Java 且双端分派矩阵（§7.3）全绿。
4. 反射风险抽样（§7.1-5）结论评审：CS2J5193 命中清单 triage，接受「反射不保证」（同 E17）或补 overrides 逃生配置。
5. sdk 基线不退化：启用后各 fixture build/test 结果不劣于未启用；上游 pass 统计零变化；耗时增量纳入指纹缓存评估。

---

## 9. 里程碑

| 里程碑 | 内容 | 规模 |
|---|---|---|
| **M1 审计报告** | SignatureExplicitizationAnalyzer 四索引 + 只读审计模式（不重写）：三类签名计数、碰撞/双接口分歧/泛型重实现/协变擦除冲突/反射风险站点清单；在 sdk 与既有 fixture 出画像；`Signatures/` 骨架（Models/Diagnostics/Facts/规则发现空集）注册但规则不启用 | Core ~350 行；M0 数据 |
| **M2 显式接口方法（SI-1/2/3/5 方法面 + NH-1 方法面可选）** | 当前**零处理、收益最大**的形态：方法显式实现提升/让位改名/双接口同源合并与报码/五类调用点；§7.1-1（方法子集）单测 + fixture；residual 显式方法面 | ~600 行 |
| **M3 属性/索引器/事件显式实现收口（SI 全量 + NH-3/4 访问器面）** | 消费 C11–C13 产物，替换三处零散分支的输入面；索引器/事件方法对联动、纯委托桥接、接口重新实现 SI-4、泛型 SI-4；红灯对照表归档；转换器三补丁标记为预处理路径不可达的核查（消费 PR 输入） | ~500 行 |
| **M4 new 隐藏全量与协变桥接（NH-2/4、CR-1/2/3）** | newslot 槽链组、静态/字段隐藏、协变直映审计与擦除 bridge、值类型报码；双端分派矩阵；sdk 全量 triage；§8 门槛评审 | ~550 行 |

---

## 10. 风险

1. **★虚分派/接口身份误改**：调用点分类错（把接口分派点改成类成员新名或反之）会产出「能编译、错分派」的程序，单测难抓。缓解：一切以 GetSymbolInfo 绑定符号为准、零文本推测；.NET xunit 分派矩阵守门；plan 原子 + 整树重编译；拿不准的站点归 CS2J5194/5197 跳过；严禁适配类模拟（不变式 1/2）。
2. **改名撞反射/序列化名**（风险 7）：`GetMethod`、XAML、按名 JSON、`GetInterfaceMap` 对新名/槽位变化敏感。缓解：nameof 字面量化；CS2J5193 显式警告与清单；沿用 E17 限制声明 + overrides；C27/C11 的序列化特性路由结论不被本规则破坏（只改方法名不动特性归属）。
3. **跨文件调用点漏改**：接口/实现/调用分处多文件，漏改即编译错误或错绑。缓解：plan TouchedFiles 由整树站点枚举算尽，声明与站点同生同灭；整树重编译 DiffErrors 归属回退；residual CS2J5199 对账；单文件模式闸门不放宽。
4. **Java bridge 与 C# newslot 差异**：CR-2 bridge 依 javac 同款机制，但 C# 侧 bridge 是真实可调用方法，可能被源内/反射直接命中而与「编译器合成不可见」预期不同；NH-2 新虚槽链改名改变了 `GetMethod` 名字。缓解：bridge 只在有擦除冲突证据时合成（CR-1 优先）；新名预留表 + 命名固定；.NET 行为测试覆盖两路径；反射限制声明。
5. **双接口同源判定过保守/过乐观**：体等价比较误判会把分歧实现错误合并（语义错）或把同源实现误报（可用性差）。缓解：默认保守——仅「同一实现符号」或「二者皆纯转发同一目标」判同源，其余 CS2J5191；sdk triage 校准。
6. **与 C02 泛型深化的哨兵/擦除交互**：CR-2 bridge 新增 override 方法，C02 的 override 兄弟 `__cs2j_typeof_T` 哨兵同步（[c02 设计决策 5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c02-generic-deepen-design.md)）须把两个槽位都视作 override 族；擦除改名归 C02/JK 既有 `_erasure_N`，C46 不重复发明。缓解：链位上 generic-deepen 远早于 member-desugar，C46 消费其产物；bridge 形态单测显式含泛型返回 + token 场景。
7. **规则相位/回环**：SI 改名后 NH 再扫到新名成员、CR bridge 新方法被 SI 认领。缓解：相位固定 SI→NH→CR + RewriteAnnotation/已生成符号不认领谓词；幂等零 diff 硬测。
8. **默认关期补丁漂移**：转换器三处零散分支继续演进会与本设计目标分叉。缓解：不改不删但以红灯对照表锁定行为预期；消费 PR 是 §8 硬门禁，不靠「碰巧能编译」长期并存（风险 10 多套事实标准）。
9. **C11–C13 合入节奏依赖**：M3 被 C12/C13 阻塞；其规则输出形态（`IFoo.GetItem` 等）若调整，SI 输入契约变。缓解：M2 只做方法面先收口最大缺口；输入契约以 C11–C13 文档引用名为准，规则对「仍是访问器语法」形态只登记不产 plan。

---

## 11. 并行协调

- **C11 锚点（强依赖）**：复用共享符号表、plan/回退、预留表、规则发现、码段 5190-5199；规则相位契约（决策 16）需锚点确认；分支 base 在 C11 合入之后。
- **C12/C13/C14（同族 worktree）**：本任务只读其设计与产物契约（IX-3/EV-6 显式方法对形态）；M3 前与 C12/C13 协调联调，但不修改其目录文件；C14 无交集（运算符不能显式实现）。
- **C20 顺序**：§3.3 结论，不加边、靠 C20 翻转时的排序断言；partial 两部件显式实现用例进 §7.1。
- **C27 clone 协变**：`Cs2jWithClone` virtual/override 对固定归 CR-1；C27 占 52xx 码位互不重叠；两规则同经族执行器，C27 先于 CR 相位（Records 规则产工厂、CR 只审计）。
- **C36 static abstract**：CS2J0585 是唯一错误码，C46 只发 CS2J5198 Info；若 C36 未翻转，静态抽象成员原样留树（行为同今天）。
- **C39 终结器**：5200-5219 码位已占，无逻辑交集。
- **JK 改名表（C08 终环）**：`_cs2jClass/_cs2jNew/_cs2jCovariant` 新名若撞 Java 保留字由 JK-1 统一逃逸（JK-1 改名种类含普通方法与显式接口实现，[jk 设计 §4.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md#L137-L157)）；C46 不自做终命名；JK-4c 多候选方法组反查规则同样适用于本规则的改名站点。
- **C02 泛型 override**：见风险 6；泛型构造映射（决策 5）的擦除结论以 C02 输出树为事实源，C46 只做接口槽同一性判定。
- **热文件**：本任务**不碰** [ClassTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs)（MemberKey:303-308 与去重:540-545 留待消费 PR 在预处理输入不可达后简化）、不碰注册表/链序文件（CatalogItems 追加 C46 是合入时唯一一行协调，与 C12/C13/C27/C39 同列）；全部新代码在 `MemberDesugar/Signatures/` 与自有测试/fixture 文件内。
- **转换器消费 PR（默认开门禁，非本特性）**：MethodTransformer new→Override 错映（:166）、显式实现 public/命名、Property/Indexer/Event 三补丁退役、`getName$Class`/`_newSlot` 特例下线——统一登记为 §8-3 后续工作单，与 C11 §5.3/C27 §5.4 消费清单合并管理。

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c46-member-signature`
- 目录：`.worktrees/preproc-c46-member-signature`
- base：`953293b9`（`docs: update doc base commit`，已核实存在；**实现前先合入 C11 分支**，M3 前再依次 rebase C12/C13 合入点）
- 创建命令（仓库根、PowerShell；本设计阶段不执行）：

```powershell
git worktree add .worktrees/preproc-c46-member-signature -b feature/preproc-c46-member-signature 953293b9
```

- 交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `MemberDesugar/Signatures/`（SI/NH/CR 三组规则 + 整树分析器）、**零 CLI 新增**（族规则自动发现 + `rules.signatures` 选项透传）、本设计文档与后续 plan（`docs/superpowers/plans/2026-09-29-c46-member-signature.md`）、Core 单测 + fixture 场景文件 + Release build/test + residual 闸门；不新建支撑工程；不触转换器代码；不做任何注册表翻转。

---

## 附：关键开放问题（供评审拍板）

1. **命名规则**：推荐「接口槽位保留规范名 + 让位/隐藏成员后缀改名」（`Foo_cs2jClass`/`Foo_cs2jNew`/`M_cs2jCovariant`，预留表防撞）；备选 `NewX` 前缀或 `IFoo_Foo` 前缀式（前者破坏 get_/set_ 配对且可撞用户名，后者迫使接口实现再发委托壳）。后缀词汇（`_cs2jClass` vs 族内 `__cs2j` 双下划线风格）需与 C11/C27 命名惯例统一。
2. **同名双接口分歧实现的终局**：v1 推荐 CS2J5191 硬报码（保身份不变式）；是否接受未来以「显式适配对象 + 调用点包装」作为 force 选项（改变 `is IA` 结果，须用户知情）。
3. **规则排序契约**：Signatures 晚于访问器四类的相位，是在族执行器按 Kind 分相，还是靠全局 Order 区间表达？需 C11 锚点拍板；影响 M2 能否在 C12/C13 未合时独立交付方法面。
4. **CR-2 泛型桥接的默认范围**：M4 是仅做「引用类型实参泛型协变」桥接，还是同步覆盖与 C02 哨兵/token 交互的复杂形态（CS2J5196 降级为 Warning 而非跳过）？取决于 sdk 画像中擦除冲突协变的命中率（M1 数据）。
5. **纯委托桥接（NH-3）的等价性边界**：同名 public 桥在 Java 侧变虚覆写，若基槽未来被第三方派生且覆写，行为矩阵是否仍观察等价；是否需要把 NH-3 限制为「基类在编译内且 sealed 槽或无其他覆写」白名单。
