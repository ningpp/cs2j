# C09 `[ThreadStatic]` 前置化预处理器设计

**日期**: 2026-09-28（2026-09-29 核订：全部代码证据按 dev-3.x@2d25700e 重定位；按 catalog C09 条目六次补全的官方语义把内联初始化器默认策略从 EveryThread 翻转为 FirstThreadFaithful；C02/C01 落地状态与 §11 并行协调同步）
**范围**: 新增注册表 pass `thread-static`（Core `ThreadStaticRewrite\` + CLI `Preprocessing/Adapters/ThreadStaticPassAdapter.cs`），把 `[ThreadStatic]` 隐式线程本地状态改写为显式 `System.Threading.ThreadLocal<T>` 包装字段 + `.Value` 访问的可编译 C#；实施 PR 不改 C#→Java 转换器
**状态**: 已核订（2026-09-29 对照 dev-3.x@2d25700e 逐条核验全部代码证据并修正；关键修正：① stub 锚点 L171-L180→L160-L169、FieldTransformer/PassCatalog/PassChainFactory/TypeMappings 等行号全量重定位；② 内联初始化器默认物化改为 FirstThreadFaithful 单线程保真形，EveryThread 降为显式选项（对齐 catalog C09 六次补全官方语义）；③ C02 generic-deepen 已落地（默认关、分析器已按 attribute 名排除 [ThreadStatic] 字段），§2/§11 依赖表述由「两 pass 均未落地」改写为已落地协调；④ 「tests 零 ThreadStatic 命中」断言更新（C02 测试已有 skip 对照）；⑤ 链序断言更新为 thread-static 插在 generic 与 generic-deepen 之间）
**Catalog**: C09（P2，分类 1.5 并发）；§3.3 中同时被挂在「泛型擦除补偿族」（[preprocessor-catalog.md L1086](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1086)）与「捕获/隐藏状态前移族」（[L1088](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1088)，C32+C09，并入闭包族 C45 统一捕获分析）
**注册表现状**: stub 已登记——[PassStubCatalog.cs L160-L169](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L160-L169)，id `thread-static`，Stage=`Declarative`，RunAfter=`generic`，目录 `.cs2j-thread-static-src`，默认关

## 1. 背景与目标

### 1.1 现状：转换器内联处理，且只改了声明，读写点改写不存在

当前 `[ThreadStatic]` 的处理全部内联在转换器 Java emit 层。全仓 src 实现文件命中三处（另 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 登记一条 stub）：本节 1、2 两处为 C09 自身的转换器代偿；第 3 处是 2026-09-29 核订时新增的——已落地的 C02 generic-deepen 在 GD-FIELD 分析器里按 attribute 名排除 `[ThreadStatic]` 字段（[GenericDeepenAnalyzer.cs L268-L273](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenAnalyzer.cs#L268-L273)，登记 Info「owned by the thread-static pass (C09)」，所有权让渡已兑现于代码）：

1. **字段声明被改写为 Java `ThreadLocal<T>`**：[FieldTransformer.cs L51-L57](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L51-L57) 检测 attribute；[L123-L147](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L123-L147) 注册字段、改声明类型为 `ThreadLocal<boxedT>`、无初始化器时发 `new ThreadLocal<>()`；[L343-L351](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L343-L351) 有初始化器时包成 `ThreadLocal.withInitial(() -> <init>)`，并 `AddImport("java.lang.ThreadLocal")`。
2. **上下文注册表是只写死状态**：[ConversionContext.cs L397-L439](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L397-L439) 提供 `RegisterThreadStaticField` / `IsThreadStaticField(...)` / `IsThreadStaticFieldDeclaration(...)`。全仓 grep 证实：`RegisterThreadStaticField` 唯一调用方是 FieldTransformer；两个 `IsThreadStaticField` 查询方法**零调用方**。

[FieldTransformer.cs L53-L56 注释](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L53-L56) 声称读写点在 `IdentifierExpressionTransformer` 与 `AssignmentTransformer` 改 `.get()/.set()`、且 `ClassTransformer` 预注册字段——两处声称均不成立：

- [IdentifierExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs) 与 [AssignmentTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs) 全文无 `ThreadStatic`/`ThreadLocal` 命中；
- [ClassTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs) 只有 `PreRegisterNestedEnums`（L95/L251），无 ThreadStatic 预扫描。

### 1.2 失败面 / 静默错值面

由于声明被换成 `ThreadLocal<T>` 而**全部读写点原样发射**，当前实际行为是：

| 使用形态 | 现状结果 |
|---|---|
| `[ThreadStatic] static int x;` 且无任何引用 | 产出可编译 Java（死声明），仅此一种幸存 |
| 任意读 `x` | Java 侧按 `int` 消费，实际类型 `ThreadLocal<Integer>` → 类型不匹配，Java 编译失败 |
| 任意写 `x = v` / 复合赋值 / `++` | 向 `ThreadLocal<T>` 赋 `T` → Java 编译失败；无诊断、无 fail-fast，错误只在 Java 半区暴露 |
| 泛型类中 `[ThreadStatic] static T x;` | 先撞 [FieldTransformer.cs L115-L119](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L115-L119) 的 `CS2J normalization required: static field referencing enclosing type parameters` fail-fast |
| 带内联初始化器 | `withInitial(() -> e)` 给**每个线程**初值，与 C#「仅触发静态构造的首线程初始化、其余线程取 default」语义不一致（代码注释自陈「closest sane Java semantics」），属静默语义偏移 |
| 实例字段上的 `[ThreadStatic]` | 检测谓词 [L434-L435](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L434-L435) 要求 static，非静态字段上的 attribute 被静默丢弃（CLR 本就忽略该形态） |
| 用户源码直接使用 `System.Threading.ThreadLocal<T>` | [TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json) 无 `System.Threading.ThreadLocal` 类型/成员映射（全文件无命中）；`.Value` 会按普通属性派生 `getValue/setValue`，而 Java `ThreadLocal` 是 `get/set`；`new ThreadLocal<T>(() => e)` 对应 Java 静态工厂 `withInitial(Supplier)` 而非实例构造——Java 半区同样不成立 |

测试侧：`tests/` 全目录 grep `ThreadStatic|ThreadLocal` 仅有 [GenericDeepenTests.cs L445 `ThreadStaticField_SkippedWithInfo`](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GenericDeepenTests.cs#L445) 一处命中（已落地的 C02 的「[ThreadStatic] 字段跳过 + Info」对照用例），C09 自身的声明/读写点改写仍**零测试覆盖**。ROADMAP #6 明确登记「[ThreadStatic]」为待前置化项（[ROADMAP.md L7](file:///d:/code/cs2j/ROADMAP.md#L7)）。

### 1.3 目标与前置化后转换器删除什么

把隐式状态显式化为**可编译、可在 .NET 半区跑源项目测试验证语义**的 C#：

```csharp
// 输入
[ThreadStatic] private static int _counter;
_counter++;
var v = _counter;

// 输出（字段名不变；所有读写点改 .Value）
private static readonly System.Threading.ThreadLocal<int> _counter = new System.Threading.ThreadLocal<int>();
_counter.Value++;
var v = _counter.Value;
```

前置化 PR 落地并默认开启后，转换器侧下列代码成为后续 PR 的删除/改写对象（本特性 PR 不动转换器，删除以默认开启为前置门，同 C02/C08 范式）：

1. [FieldTransformer.cs L51-L57](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L51-L57) 检测分支；
2. [L123-L147](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L123-L147) 注册 + `ThreadLocal<T>` 声明/初始化器改写；
3. [L343-L351](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L343-L351) `withInitial` 包装与 `AddImport("java.lang.ThreadLocal")`；
4. [ConversionContext.cs L397-L439](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L397-L439) 整套只写注册表与过期注释；
5. 配套的**新增**工作（Java 半区消费 BCL 形态，见 §5.4）：`System.Threading.ThreadLocal<T>` 类型映射、`Value` get/set → `get()/set()`、`.ctor(Func<T>)` → `withInitial(Supplier)`、`Dispose/remove` 的映射决策。

目标形态下转换器不再需要认识 `[ThreadStatic]` attribute（中间树中 attribute 已被移除），只需要把 BCL `ThreadLocal<T>` 当普通泛型类型消费。

## 2. 关键设计决策表

| 决策点 | 结论 | 备注 |
|---|---|---|
| 目标 C# 形态 | 字段名**保持不变**：`[ThreadStatic] static T f;` → `static readonly ThreadLocal<T> f = new ThreadLocal<T>();`；所有绑定到该字段的读改写为 `f.Value`、写改为 `f.Value =` | 不生成访问器方法、不改字段名：nameof/调用面最小扰动；与 catalog C09 目标形态一致 |
| 重写判定真相源 | 全编译 SemanticModel + `Compilation.GetTypeByMetadataName("System.ThreadStaticAttribute")` 的符号身份判定；引用点一律 `GetSymbolInfo` 反查 `IFieldSymbol`，禁止文本替换 | 现状谓词按显示名字符串匹配（[ConversionContext.cs L436-L438](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L436-L438)），新 pass 不沿用 |
| 静态字段 | v1 主形态，全量支持（非泛型容器） | §4.3 TS-1~TS-5 |
| 实例字段 | CLR 忽略实例字段上的 `[ThreadStatic]`；v1 **不改写**，出 CS2J1201 Warning 并保留原样 | 不静默丢 attribute；未来可用选项显式化（见 §4.3 TS-9） |
| 无初始化器 | `new ThreadLocal<T>()`——每线程 default/null，与 C# 严格等价 | |
| 内联初始化器 | 默认 **FirstThreadFaithful 单线程保真**：holder 无参构造 `new ThreadLocal<T>()`，初始化表达式按声明序注入静态构造（无显式 cctor 则合成），出 Info TS002 留痕——与官方语义「仅执行静态初始化器的线程见到初始值、其余线程 default(T)」严格一致（catalog C09 条目六次补全 2026-09-29 官方文档核对明确要求此物化形，本设计据核订翻转原 EveryThread 默认）。选项 `InitializerPolicy.EveryThread`（`new ThreadLocal<T>(() => INIT)`，每线程独立初值）作为显式 opt-in 保留，仅适合「源码意图明确是每线程初始化」的字段，详见 §2.1 | C# 原语义是「仅首线程初始化」的陷阱；当前 Java emit 的 `withInitial` 是意图近似、偏离官方语义（§1.2），前置化不再继承该偏移 |
| 静态构造函数中的赋值 | 保留为 `f.Value = e`，不搬入工厂 | cctor 只在一个线程跑一次，与 C# 严格一致；与内联初始化器策略的差异显式记录（§4.3 TS-10） |
| `readonly` 组合 | 输出 holder 加 `readonly`；原 cctor 内赋值改 `.Value=` 合法（holder 引用只读、Value 可变） | |
| 多变量声明 | `[ThreadStatic] static int a, b;` 拆为两个独立字段声明 | holder 各自初始化；trivia/attribute 归属按变量拆分 |
| 泛型容器静态字段 | v1 对「容器是泛型类型」的命中字段一律**跳过 + CS2J1204 Warning**（无论字段类型是否引用类类型参数） | C# 按闭型各持一份、Java 擦除后共享；且引用类类型参数的静态字段本就 fail-fast。组合形 holder 与 C02 共同设计，见 §11 |
| 与 C02 holder 的关系 | **C02 已落地**（Implemented、默认关，[PassCatalog.cs L167-L178](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L167-L178)，RunAfter=`generic`），其 GD-FIELD-1 规则已声明排除 `[ThreadStatic]` 字段交 C09（C02 设计 §4.3/§11），且已兑现于代码（[GenericDeepenAnalyzer.cs L268-L273](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenAnalyzer.cs#L268-L273) 按 attribute 名过滤 + Info）。链序：C09 插在 generic 与 generic-deepen 之间（C09 先行）；C09 改写后 attribute 消失，C02 的排除过滤对这些字段自然失效（`ThreadLocal<T>` 不引用类类型参数时 GD-FIELD 本就不命中）；「泛型容器 + ThreadLocal holder」组合形留 C02 后续增补 | C02 文档 §11 原建议「C09 落地时 RunAfter 增 generic-deepen」（C09 后行），本设计 §11 维持反序主张；C02 已按「排除交 C09」落地，无论孰先孰后两 pass 均无双重改写，链序由本设计 §3.1 的插入位置定死 |
| struct 值类型 T | 读/写/复合赋值支持；`f.Value.Mut()`/`f.Value.X =` 等变异点按语句级临时变量提升 + 回写；不可提升上下文 CS2J1203 | catalog C09 注明「ThreadLocal<结构体> 变异语义需原型确认」；C09 链位早于 E13/C07，不能依赖后置 struct 通道 |
| `ref/out/&` 传递 | 属性不是变量，无法 `ref f.Value`；v1 命中即 CS2J1202 Error、整条字段计划跳过 | C01（ref-out-in）链位更早，其产物形态需实测；直接 ref-local/指针不绕过 |
| 表达式树 / dynamic | 表达式树内站点改变树节点形态 → 字段计划跳过 + CS2J1205（Warning，strict Error）；dynamic 绑定站点不可判定 → CS2J1202 | 与 JK-8/C02 共用「表达式树禁区」判定（未来共享 `ExpressionTreeFacts`） |
| nameof / 反射 | 字段名不变，`nameof(f)` 无需改；反射 `GetField("f")` 仍能取到但**字段类型改变、attribute 消失**，属文档化限制 | 同 JK §9；不做按名契约的全面排查，命中按名序列化等场景时由选项 skip 字段 |
| 用户直接使用 `ThreadLocal<T>` | 本 pass 不触碰（已是显式 C# 形态）；Java 半区映射缺口列为默认开门禁（§8 第 4 条），不在本 pass 内补 | stub 描述含「and ThreadLocal」，本设计将其范围解释为「attribute 前置化」，BCL 直用走转换器映射 |

### 2.1 `[ThreadStatic]` 配内联初始化器的 C# 语义陷阱

`[ThreadStatic] static int x = 42;` 的真实 CLR 语义（catalog C09 条目 2026-09-29 六次补全经官方文档核对确认）：静态构造在**首个触碰该类型的线程**上执行，初始化器也只在该线程把 `x` 置为 42；**其余线程的 `x` 是 0**。这是被广泛记录的坑，也是当前 Java emit 用 `withInitial` 偏离 C# 的点。两种 C# 目标形：

- **FirstThreadFaithful（默认，官方保真）**：

  ```csharp
  private static readonly ThreadLocal<int> x = new ThreadLocal<int>();
  static C() { x.Value = 42; /* 按初始化器声明序注入 */ }
  ```

  无显式 cctor 则合成静态构造；已有显式 cctor 则把注入赋值放在 cctor 体最前（C# 语义中字段初始化器先于构造体执行）；多个带初始化器字段按声明序排列，初始化器间的依赖顺序、异常缓存语义与 Roslyn 静态字段初始化一致。跨线程行为与 C# 严格逐点一致：首触线程见 42、其余线程见 0。合成 cctor 由 Roslyn 语法构造即可完成，成本可控，v1 交付。

- **EveryThread（M2，显式选项）**：`new ThreadLocal<int>(() => 42)`，每个首访线程都得 42。它**偏离**官方 `[ThreadStatic]` 语义（等价于「每线程各自执行一次初始化器」），只应在源码意图明确是每线程初始化时由 overrides/选项逐字段启用；启用时出 Info 留痕。保留该选项的动机：当前 Java emit 已选择 `withInitial` 意图近似，个别项目可能已按「每线程初值」写了 Java 侧断言，选项提供平滑迁移路径。

## 3. 注册表与链位契约

### 3.1 描述符

翻转时从 [PassStubCatalog.cs L160-L169](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L160-L169) 删除 stub，在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 的 `Implemented` 表中**紧随 `generic` 条目（L156-L166）之后、`generic-deepen` 条目（L167-L178）之前**插入（两个 pass 的 RunAfter 都只锚 `generic`，先后由表序/topo 稳定序决定；注册表拓扑顺序由顺序断言测试 [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 的 `LockedChain` 锁死，翻转时须在 `PassIds.Generic` 与 `PassIds.GenericDeepen` 之间插入 `thread-static`）：

```text
Id                          = "thread-static"
DisplayName                 = "Thread-static rewrite"
IntermediateDirectoryName   = ".cs2j-thread-static-src"
Stage                       = PassStage.Declarative
Status                      = PassStatus.Implemented
DefaultEnabled              = false（初版 opt-in；过 §8 门槛后翻 true——本 pass 具备较早默认开的条件，见 §3.4）
RunAfterIds                 = { "generic" }（generic-deepen 已落地并同锚 generic；两 pass 先后由上述插入位置锁定为 C09 先行，见 §11）
SupportLibrary              = PassSupportLibrary.None
NeedsSemanticModel          = true
VerifyBuildByDefault        = true
VerifyTestsByDefault        = true
CatalogItems                = "C09"
Description                 = "Rewrite [ThreadStatic] fields into explicit ThreadLocal<T> holders with .Value access."
```

链位语义：`… → unsigned → generic(E10) → thread-static(C09) → generic-deepen(C02) → record-readonly / array-method / make-readonly → … → java-keywords → 主转换`。不重排任何既有 pass。

### 3.2 Stub→Implemented 翻转清单

| 文件 | 改动 |
|---|---|
| [PassStubCatalog.cs L171-L180](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L171-L180) | 删除 `ThreadStatic` stub 条目；`Ids.ThreadStatic` 常量迁移到 [PassIds](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L24)（无其它 stub 引用它，可直接迁） |
| [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | 按 §3.1 插入 Implemented 描述符；`PassIds` 增 `ThreadStatic` 常量 |
| `src/CSharpToJava.CLI/Preprocessing/Adapters/ThreadStaticPassAdapter.cs` | **新增**适配器（§5.1） |
| [PassChainFactory.cs L91-L218](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L91-L218) | `CreateAdapter` switch 增 `PassIds.ThreadStatic => new ThreadStaticPassAdapter(...)`；**不进 `BuildLegacyToggles`**（无历史开关），经通用 `--enable-pass/--disable-pass` 控制 |
| `src/CSharpToJava.Core/ThreadStaticRewrite/ThreadStatic*.cs` | 新增 Core 组件（§4） |
| 测试 | 注册表顺序/`--list-passes` 状态断言（[PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)、[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs)）、Core 单测、项目级 fixture、residual 闸门、CLI 测试（§7） |

不需要改的：`Program.cs` 指纹逻辑（已按注册表全集生成 `pass:<id>=on/off` 令牌，翻转造成一次性缓存失效，符合 foundation plan 既定决策）；保留目录由已执行 pass 的目录名驱动；通用 verb `preprocess --pass thread-static` 自动可达，stub 的 CS2J9001 拒绝路径翻转后自然消失；`ProjectPassRunner` 标准骨架（整树编译 + 逐文件 RewriteFile + AfterFiles）足够，无需自定义 `ExecuteAsync`。

### 3.3 中间目录与验证

- 中间目录 `.cs2j-thread-static-src`，输入为 E10 产物树，输出交下一环；干净文件字节级复制（含 BOM 保持，同各 pass 惯例）。
- `VerifyBuild/VerifyTests` 默认开（最严档，同 E10/enum），经适配器属性与 [VerifyBuildAndTestsAsync](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L102-L107) 生效；`DeleteBuildOutputsAfterRestore = true`。
- 成本面遵守 catalog §4 风险 11：只新增「一环一棵树」，不绕开指纹、不多轮 restore（回退重放除外，与 JK/RR 同构）。
- 无支撑库：holder 是 BCL `System.Threading.ThreadLocal<T>`，不新增 `PassSupportLibrary` 枚举值、不注入支撑工程、不触发 [IsExecutedSupportLibraryProject](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L225-L235) 过滤。

### 3.4 默认关 → 可较早默认开的论证

C09 是 stub 目录中默认开性价比最高的条目之一：

1. **规则面极小**：一个 attribute、一种目标形态、字段名不变、无签名族改写、无支撑库、无生成文件分发；
2. **现状基线最差**：任何被实际引用的命中字段当前都产出不可编译 Java，且无诊断（§1.2），前置化不可能更差；
3. **.NET 半区可完整守门**：`ThreadLocal<T>` 是真 BCL 类型，fixture 可用多线程测试直接验证每线程独立副本，语义等价证据在 C# 层闭环；
4. **无跨 pass 形态依赖**（唯一外部依赖是 Java 半区 BCL 映射，属转换器后续 PR，可并行准备）。

故里程碑允许「翻转即候选默认开」，但仍以 §8 门槛为硬门，不抢跑。

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/ThreadStaticRewrite/`，命名空间 `CSharpToJava.Core.ThreadStaticRewrite`。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `ThreadStaticOptions.cs` | `record ThreadStaticOptions`：`InitializerPolicy InitializerPolicy`（默认 `EveryThread`）、`bool Strict`（残余/跳过项升级 Error）、`bool ReportInfo`、`bool RewriteGenericContainingTypes`（默认 false，预留 C02 组合形）、`ExpressionTreePolicy ExpressionTreePolicy`（Skip/Error，默认 Skip） |
| `ThreadStaticStatistics.cs` | `FilesScanned/FilesTransformed/FilesSkippedClean`、`FieldsDetected/FieldsRewritten/FieldsSkipped`、`ReadsRewritten/WritesRewritten/CompoundSitesRewritten/IncrementSitesRewritten`、`StructTempLifts`、`InitializersNormalized`、`InfeasibleSites`、`SkippedByReason`、`ResidualThreadStaticAttributes`；实现 runner 通用投影所需形状 |
| `ThreadStaticDiagnostics.cs` | `ThreadStaticSeverity`、`ThreadStaticRule`（Declaration/Read/Write/Compound/StructMutation/RefOut/ExpressionTree/Dynamic/GenericContainer/Instance/Residual）、`ThreadStaticDiagnostic(Severity, Rule, Code, Message, FilePath?, Line?)`、码常量表（§6） |
| `ThreadStaticResult.cs` | `record ThreadStaticFileResult(string OutputCode, bool Changed, IReadOnlyList<ThreadStaticDiagnostic> Diagnostics, ThreadStaticStatistics Statistics)` |
| `ThreadStaticPlan.cs` | `sealed class ThreadStaticFieldPlan`：`IFieldSymbol Field`、`Kind`（Static/Instance）、`InitializerKind`（None/Inline）、`IReadOnlyList<ReferenceSite> Sites`（跨文件，含节点文件/span/站点种类）、`Outcome(Active/Skipped)`、`FailCode/FailReason`、`TouchedFiles`、`OwnsTouch(file, span)`；字段是回退最小单位 |
| `ThreadStaticAnalyzer.cs` | **整树一次**：用 `Compilation.GetTypeByMetadataName("System.ThreadStaticAttribute")` 取属性符号；遍历所有 `FieldDeclarationSyntax`，按 `IFieldSymbol.GetAttributes()` 符号身份命中；逐语法树用 SemanticModel 枚举绑定到命中字段的全部引用站点（IdentifierName/MemberAccess 右段，含限定/`global::` 形态）并分类；partial 多部件按符号天然合并；产出确定性排序的 plan 列表（文件路径+span） |
| `ThreadStaticRewriter.cs` | `CSharpSyntaxRewriter`：按本文件 active plan 改声明点与引用点（TS-1~TS-10）；struct 临时变量提升；改动节点挂 `RewriteAnnotation = new("Cs2jThreadStaticRewrite")`；只接受语义绑定命中，禁止文本替换 |
| `ThreadStaticResidualScanner.cs` | 重写后整树扫描：输出中剩余的 `ThreadStaticAttribute` 数与 plan skip 集合对账；扫描未被改写的命中字段引用（防御性） |
| `ThreadStaticPreprocessor.cs` | 对外入口：`BindCompilation(CSharpCompilation)`/`Analyze()`/`ExcludePlans(ids)`/`Plans`/`RewriteFile(string, SemanticModel, ThreadStaticStatistics, List<ThreadStaticDiagnostic>)`/`ResidualScan(Compilation)`/`internal static FormatAndValidate(CompilationUnitSyntax)`（三候选：改动区格式化→整文件格式化→原文，与 JK/RR 同款） |

编译构造沿用 runner 标准：`CSharpParseOptions(LanguageVersion.Latest)`（+ runner 默认 `NETCOREAPP` 预处理符号）、`ProjectUnsignedTypePreprocessor.GetBasicReferences()`、`allowUnsafe: true` 的 DLL 编译；分析与残余扫描跑在同一棵全编译上。

### 4.2 数据流（标准骨架，不自定义 ExecuteAsync）

1. **CompilationReadyAsync（整树一次）**：runner [RunStandardSpineAsync L164-L170](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L159-L170) 建完全树编译后调 `BindCompilation` → `Analyze()`，plan 一次成型；overrides/选项在适配器构造时应用。跨文件引用无需特殊编排：每文件的 SemanticModel 来自同一棵 `CSharpCompilation`（[L193-L205](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L193-L205)），A 文件声明、B 文件引用经符号反查同一张字段表。
2. **Plan（字段级原子）**：每个字段一条 plan；含任一不可行站点（ref/out、表达式树、dynamic、不可提升 struct 变异、泛型容器 v1）即置 `Skipped + FailCode`，该字段声明与全部站点都不动；绝不只改一半。
3. **Rewrite（逐文件）**：`RewriteFileAsync` 调 Core `RewriteFile`；Changed=false 返回 null，runner 字节级复制；诊断补 RelativePath，统计合并。
4. **AfterFilesAsync**：`ResidualScan` 对账，残余 >0 时按 strict 出 Warning/Error（仿 [EnumPassAdapter.AfterFilesAsync L79-L92](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs#L79-L92)）。
5. **回退**：中间树重编译相对基线新增错误时，按 TouchedFiles/OwnsTouch 归属唯一字段 plan，ExcludePlans 后从原始副本重放收敛；无法归属 → CS2J1200 当次全回退（与 JK030/JK031 同构；声明改写与 `.Value` 插入在 C# 侧类型严格，稳态预期零回退）。
6. restore/build/test 由 runner 标准骨架收尾。

### 4.3 规则全集（TS-x）

- **TS-1 声明改写**：移除该变量上的 `ThreadStaticAttribute`（同声明上的其它 attribute、XML doc、trivia 保留）；字段类型改 `global::System.Threading.ThreadLocal<T>`（T 取原声明类型，保持可空标注）；补 `readonly`（已有则不重复）；初始化：
  - 无初始化器 → `new global::System.Threading.ThreadLocal<T>()`；
  - 内联初始化器 `= e`（默认 EveryThread）→ `new global::System.Threading.ThreadLocal<T>(() => e)`；`e` 原文搬入 lambda，不重放（工厂内首访才求值，副作用次数语义与初始化器「每线程一次」一致）；
  - 多变量声明拆分（§2）。
- **TS-2 读站点**：绑定到 plan 字段的 `IdentifierNameSyntax`/`MemberAccessExpression.Name`/`QualifiedName` 右段（含 `Type.f`、`global::Ns.Type.f`）→ 包一层 `.Value`。`nameof(f)` 不动（绑定的是参数串，不产生变量访问，且字段名不变）。
- **TS-3 写站点**：简单赋值目标 `f = e` → `f.Value = e`。
- **TS-4 复合赋值 / `++`/`--`**：基本类型/引用类型直接 `f.Value += e`、`f.Value++`（C# 允许对属性复合赋值，get/set 由编译器成对发射）；值类型 T 同形可编译，但按 TS-5 核对变异语义。
- **TS-5 struct T 变异**：
  - 语句级 `f.valueField = e` / `f.Mut()` / 复合赋值 / `++`：提升为 `var __cs2j_ts_f = f.Value; <在临时量上操作>; f.Value = __cs2j_ts_f;`（方法调用后无条件回写——无法静态判定方法是否变异，冗余回写无害且保语义）；临时名确定性、碰撞加序号后缀；
  - 不可提升上下文（字段初始化器、特性实参、查询子句、lambda 参数内嵌表达式等）→ CS2J1203，字段 plan 跳过；
  - `readonly struct` T：无变异，读站点直接 `.Value`，无需回写。
- **TS-6 ref/out/指针**：`ref f`/`out f`/`&f`/`fixed`/把字段当变量传址 → CS2J1202（Error），plan 跳过。注意 C01 链位更早，其 holder 产物先经实测确认（C01 产物若已把传址改成 holder 显式调用，则按普通站点处理）。
- **TS-7 dynamic**：dynamic 绑定的字段访问不可判定 → CS2J1202，plan 跳过，不猜测。
- **TS-8 表达式树**：`Expression<...>` lambda 内对该字段的读写会改变表达式树节点形态 → 按策略 CS2J1205（默认 Warning），plan 跳过；谓词未来与 JavaKeywords/C02 共享 `ExpressionTreeFacts`，本 pass 先内联最小判定。
- **TS-9 实例字段**：非 static 命中 → CS2J1201 Warning（CLR 忽略该 attribute），不动；不计数进残余。
- **TS-10 cctor / 初始化顺序**：显式静态构造内的 `f = e` 按 TS-3 改 `f.Value = e`，不搬工厂；同一字段同时有内联初始化器与 cctor 赋值时，初始化器先于 cctor 体（C# 顺序），工厂首访时序与 cctor 写的先后在跨线程下本就微妙，出 Info 提示；FirstThreadFaithful 策略下内联初始化器改 cctor 注入（M2）。
- **TS-11 泛型容器**：`fieldSymbol.ContainingType.IsGenericType` 且 `RewriteGenericContainingTypes=false` → CS2J1204 Warning，plan 跳过（含非类型参数字段，见 §2 闭型共享理由）。
- **TS-12 const/volatile 组合**：`const` + `[ThreadStatic]` 无意义（const 无每线程存储）→ CS2J1201 类 Warning 跳过；`volatile` 修饰的是 holder 引用而非每线程值，语义可疑 → 同样跳过并提示人工处理。
- **TS-13 lambda/局部函数/async/iterator 内引用**：按普通站点改 `.Value`——holder 是静态字段，捕获无障碍，执行线程上的取值语义与原 `[ThreadStatic]` 一致；这也是与 §11 闭包族的天然边界（C32/C45 不需要为本 pass 特判）。
- **TS-14 `[ThreadStatic]` 以外的 ThreadLocal 状态**：用户直接声明的 `System.Threading.ThreadLocal<T>` 不分析、不改写；残余闸门只扫 attribute，不扫 BCL 类型。

## 5. CLI 适配器与集成

### 5.1 ThreadStaticPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/ThreadStaticPassAdapter.cs`（预计 100-150 行），形态最接近 [EnumPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs)（语义逐文件 + AfterFiles 残余对账），但在 `CompilationReadyAsync` 增加一次性 `BindCompilation/Analyze`：

- `Descriptor => PassCatalog.ImplementedById(PassIds.ThreadStatic)`；`VerifyBuild/VerifyTests => true`；`DeleteBuildOutputsAfterRestore => true`。
- 持有单个 `ThreadStaticPreprocessor` 与累计 `ThreadStaticStatistics`/诊断列表。
- `CompilationReadyAsync`：Bind + Analyze（plan 定型）。
- `RewriteFileAsync`：调 `RewriteFile(context.SourceCode, context.SemanticModel!, stats, diags)`；Changed=false 返回 null；verbose 打印 transformed/clean/dirty-no-rewrite。
- `AfterFilesAsync`：`ResidualScan` + strict 升级；restore/build/test 失败映射 CS2J1230/1231。
- `HasErrors`/`BuildChainSummaryLine`（字段命中/改写/跳过、读写站点、残余计数）/`BuildChainDiagnosticLines`（Info 仅 verbose）。
- 不新增门面类（foundation 交付包模板允许「一个适配器文件」）；测试走 runner 与通用 `preprocess` verb。

### 5.2 无支撑库 vs compat ThreadLocalHelper

结论：**不用 C# 支撑库，也不新建 Java compat 类**。

- 不用 C# 支撑库：.NET 半区守门要求目标形态就是真 BCL `ThreadLocal<T>`——合成的自定义 holder 在 .NET 上验证不了 CLR 线程本地语义，违背 catalog §5.1「两半区」方法论；也省掉支撑工程注入/过滤成本。
- 不新建 compat `ThreadLocalHelper`：Java 侧 `java.lang.ThreadLocal` 元数据已齐备（[config/java/java.base/java/lang/ThreadLocal.json](file:///d:/code/cs2j/config/java/java.base/java/lang/ThreadLocal.json)，含 `withInitial(java.util.function.Supplier)` L56-L58），`System.Func<TResult>` 已映射 `java.util.function.Supplier`（[TypeMappings.json L3218-L3223](file:///d:/code/cs2j/config/TypeMappings.json#L3218-L3223)）。Java 半区需要的是**配置/转换器映射**而非新运行时类（§5.4）。compat 内既有 ThreadLocal 用法（[TypeHelper.java L173-L193](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java#L173-L193)、[TestContext.java L20](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/Microsoft/VisualStudio/TestTools/UnitTesting/TestContext.java#L20)）均为手写 Java 直用，不构成 helper 资产。

### 5.3 verify 默认、指纹、保留目录

- verify：descriptor 双默认开（最严档）。
- 指纹：翻转自动纳入全集令牌（`pass:thread-static=false→true` 一次性失效，接受）；无支撑文件豁免问题。
- 保留目录：`.cs2j-thread-static-src` 由实际执行集合自动 preserve。

### 5.4 Java 半区消费清单（转换器后续 PR 的输入，本 pass 不实施）

预处理产物含 `System.Threading.ThreadLocal<T>`，转换器默认开启前必须证实下列映射成立（实施时逐项核实，缺则补 TypeMappings/转换器，不凭空假设，对齐 C02 风险 11 的核查纪律）：

1. 类型映射 `System.Threading.ThreadLocal<T>` → `java.lang.ThreadLocal`（TypeMappings 现无条目）；
2. 属性 `Value` 的 get/set → `get()`/`set(T)`（默认属性派生 `getValue/setValue` 不匹配，需方法/属性映射或特判）；
3. `.ctor(Func<T>)` → 静态 `withInitial(Supplier)`（对象创建表达式→静态工厂调用，是转换器里少见的 kind 变换，需原型确认）；
4. `Dispose()`/`IDisposable` using 形态 → `remove()` 或 no-op 的决策；`IsValueCreated` 无 Java 对应 → 显式不支持诊断；
5. 删除 §1.3 列出的内联特例与死注册表。

## 6. 诊断策略

新增 CS2J12xx 码段（转换器泛型段 CS2J10xx、C02 设计占用 CS2J11xx、pass 基建 CS2J90xx，本段不冲突；码位只追加不改号）。MSBuild 格式：`文件(行,列): warning/error CS2J12xx: [规则/字段限定名] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1200 | Error | 整树重编译新增错误且无法归属单一字段 plan → 本次运行全部回退 |
| CS2J1201 | Warning | 无意义/不支持的 attribute 位置：实例字段、const、volatile 组合；plan 跳过 |
| CS2J1202 | Error（plan 跳过） | 不可行站点：ref/out/传址/指针、dynamic 绑定不可判定 |
| CS2J1203 | Warning（strict Error） | struct T 变异位于不可临时提升上下文 |
| CS2J1204 | Warning（strict Error） | 泛型容器静态字段，v1 不支持（等待 C02 holder 组合形） |
| CS2J1205 | Warning（strict Error） | 字段在表达式树 lambda 内被读写，plan 跳过 |
| CS2J1220 | Warning / strict Error | residual 闸门：输出中存在无对应 skip 诊断的 `ThreadStaticAttribute` 残余 |
| CS2J1230 | Error | 中间树 `dotnet restore/build` 失败 |
| CS2J1231 | Error | 中间树 `dotnet test` 失败 |
| CS2J1290 | Warning | 单文件模式：plan 非自包含（跨文件声明/引用），排除 |
| TS001 | Info | 字段已改写（声明/读写站点计数与限定名），仅 verbose |
| TS002 | Info | 内联初始化器按 EveryThread 归一为首访工厂，附首线程语义差异说明，仅 verbose |

原则（catalog §4 风险 12）：不可判定即显式诊断并让该字段 plan 整体不动，**禁止只改声明不改站点、禁止注释/占位流入下一环节**；Warning 可经 `--strict` 升级 Error；跳过必须留诊断痕迹。

## 7. 测试计划

### 7.1 Core 单测

新增 `tests/CSharpToJava.Tests/ThreadStaticRewriteTests.cs`（内联源码 → `CSharpCompilation` → Bind/Analyze/RewriteFile → 忽略空白文本断言 + `AssertOutputCompiles`，仿 [EnumPreprocessorUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/EnumPreprocessorUnitTests.cs) 与 GenericPreprocessTests 的编译 helper）：

- 静态无初始化器：声明形 + 同文件读/写/复合/`++`/`--` 站点；限定名 `C.f`/`global::Ns.C.f`；
- 静态带内联初始化器：EveryThread 工厂形、初始化表达式原样入 lambda、Info TS002；
- 静态构造内赋值 → `.Value =`，不搬工厂；
- 多变量声明拆分；`readonly`/多 attribute/XML doc/trivia 保留；
- struct T：读、写、复合、方法变异、字段变异的临时提升 + 回写；`readonly struct` 不回写；不可提升上下文 → CS2J1203；
- 泛型类静态字段（含类型参数与不含两种）→ CS2J1204 跳过、产物与输入一致；
- 实例字段 → CS2J1201；const/volatile 组合 → CS2J1201；
- ref/out/`&` → CS2J1202 且整条字段所有站点不动（原子性断言）；
- 表达式树内读写 → CS2J1205 跳过；dynamic → CS2J1202；
- `nameof(f)` 原样；lambda/局部函数/async 方法内站点正常改写；
- **跨文件**：字段声明与引用分两个编译单元，全编译一次 Bind 后两边都改；单文件模式（无全编译）越界 plan → CS2J1290；
- 干净文件（无 attribute）字节级保留；BOM 保持；
- residual 扫描：构造残余样本断言计数与 strict 升级；回退通道（可测 hook 注入必然编译错误）断言 CS2J1200 全回退、产物与输入字节一致。

### 7.2 项目级 sln fixture

新增 `tests/SampleThreadStaticSolution/`（`SampleThreadStaticLib` + `SampleThreadStaticApp` + `.sln`，按 [SampleRecordSolution](file:///d:/code/cs2j/tests/SampleRecordSolution) 范式以 Content 挂入 CSharpToJava.ProjectTests.csproj，net10.0）：

- Lib：静态计数器（无初始化器）、带内联初始化器字段、struct T 变异、跨类/跨文件读写点、被 lambda 与普通方法共享的字段、一个泛型类 ThreadStatic 对照（断言 CS2J1204 跳过、中间树仍可编译）；
- App：**并发线程行为测试**——起多线程各自递增/读写，断言每线程独立副本、互不可见、聚合结果符合预期；初始化器线程首访行为断言（EveryThread 形）；
- 断言：`result.Success`、统计计数、中间树**不含**任何 `ThreadStaticAttribute`（skip 对照项除外）、含 `ThreadLocal<` 与 `.Value`；`dotnet build -c Release` 退出码 0、`dotnet test -c Release` 全绿；干净树字节级复制与 BOM 用例。

### 7.3 Release build/test 与 sdk 基线

- fixture 与 `D:\csharp-sdk`（只读输入）预处理树分别 `dotnet build -c Release` / `dotnet test -c Release`；sdk 基线 = pre-existing 失败集快照，要求新增失败为零。
- M0 先跑测量轮：sdk 中 `ThreadStaticAttribute` 命中字段数、其中被引用（当前必失败）数、泛型容器占比、初始化器占比，作为默认开评审数据。

### 7.4 residual 闸门测试

对 fixture 与合成样本断言：输出中 `ThreadStaticAttribute` 计数 = 有 skip 诊断的字段计数（§4.3 TS-9/TS-11/TS-12）；strict 模式下 CS2J1220 升级即非零退出。**闸门零 ThreadStatic Attribute（除登记 skip 项）**。

### 7.5 CLI / 注册表测试

- 翻转前：`--enable-pass thread-static` → CS2J9001 退出 1（现状回归锁定，经 [PreprocessVerbCLITests](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/PreprocessVerbCLITests.cs) 同类用例）；翻转后：`--list-passes` 状态 Implemented、目录名 `.cs2j-thread-static-src`、默认关时无目录无耗时；`--enable-pass` 后链路执行并把产物串给下一环；默认开之后 `--disable-pass thread-static` 可关；单文件 verb 跨文件 → CS2J1290；
- 注册表：拓扑顺序断言（紧随 generic 之后）、`catalogItems=="C09"`、无重复 id/目录；[PassStubCatalogTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) 中 stub 断言同步移除；
- 指纹令牌含 `pass:thread-static=...`。

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足：

1. **中间树正确性**：fixture sln 与 `D:\csharp-sdk` 预处理树 `dotnet build -c Release` 零错误、`dotnet test -c Release` 全绿；sdk 相对基线新增失败为零；并发线程行为测试（§7.2）全绿。
2. **残余闸门**：sdk 中除显式 CS2J1201/1204/1205 登记 skip 站点外零 attribute 残余；全部 skip 站点分类有主，无「未知残余」；strict 模式可过。
3. **全仓测试不回归**：`CSharpToJavaConverter.slnx` `dotnet build -c Release` + 三个测试工程 `dotnet test -c Release` 全绿（既有红灯数量不增加）。
4. **Java 半区就绪**：§5.4 五项映射逐项核实/补齐，代表性 `[ThreadStatic]` 样本（基本类型、引用类型、struct T、初始化器）经完整 C#→Java 链路产出可编译 Java；FieldTransformer 旧分支在预处理产物上不可达（其删除仍留后续 PR，但默认开启前证明触发不到）。
5. **诊断可运维**：sdk 全量 Warning/Error 清单经人工 triage；sdk 命中字段的 skip 比例可接受（M0 测量数据支撑）。
6. **文档**：本设计 + 对应 plan（`docs/superpowers/plans/2026-09-28-c09-thread-static.md`）定稿；转换器删除清单（§1.3/§5.4）登记为后续 PR。

不满足时任保持 opt-in（catalog §4 风险 10）。

## 9. 里程碑

规则少，允许一到两段提交：

| 里程碑 | 内容 | 验证 |
|---|---|---|
| M1（可单段提交） | Core 骨架（options/statistics/diagnostics/result/plan/入口空壳 + FormatAndValidate）+ Analyzer + TS-1~TS-4（非泛型容器、非 struct 变异）+ 适配器 + 注册表翻转（Implemented、默认关）+ Core 单测主体 + 注册表/CLI 测试 + sdk 测量轮 | `--list-passes` 可见；干净树零差异；fixture 基本形态 build/test 全绿 |
| M2（第二段） | TS-5 struct 临时提升、TS-6~TS-12 跳过/诊断全集、InitializerPolicy（含 FirstThreadFaithful 选项）、residual 闸门与回退通道、SampleThreadStaticSolution 并发测试、sdk 基线比对；过 §8 后 descriptor 翻默认开；转换器后续 PR 清单（Java 半区映射 + 旧分支删除）登记 | 候选默认开评审 |
| M2+（不在本 pass 承诺） | 与 C02 联动的泛型容器组合形 holder（`RewriteGenericContainingTypes=true`）；FirstThreadFaithful 的 cctor 合成原型；表达式树共享谓词提取 | 独立条目/后续 PR |

## 10. 风险

1. **初始化器语义归一**：EveryThread 与 C# 首线程陷阱不一致（§2.1）。缓解：默认形匹配既有 Java emit 已选意图、Info 留痕、提供 Faithful 选项；fixture 同时锁两种期望。
2. **struct T 变异**：`f.Value.Mut()` 不回写即静默丢更新（C# 直写字段会持久化）。缓解：TS-5 语句级无条件 temp+回写；不可提升处显式 CS2J1203，禁止半改；与 E13/C07 的先后以「本 pass 自包含正确」为准，不依赖后置通道。
3. **泛型容器闭型语义**：C# 每闭型一份、Java 擦除共享；即使字段类型不引用类类型参数，Java 半区仍共享。缓解：v1 显式跳过 CS2J1204，不产出看似正确实则错值的代码；组合形交 C02。
4. **Java 半区映射缺口**：`ThreadLocal` 类型/`Value`/`withInitial` 三处任一缺失都会让默认开后的 Java 产物不可编译。缓解：列为 §8 硬门禁第 4 条，先核查后翻默认；现状本就必失败，不构成回归。
5. **ref/out/传址不可表达**：属性无法传址；C01 更早链位的产物形态需在 M1 用实测样本确认，遇无法闭合形态坚持 Error + 字段整体跳过。
6. **表达式树/dynamic 共同禁区**（catalog §4 风险 8）：v1 内联判定，未来必须与 JK/C02 收敛到共享 `ExpressionTreeFacts`，禁止三份漂移。
7. **反射/字符串契约**（catalog §4 风险 7）：字段仍同名但类型改变、attribute 消失，`GetField`/`GetCustomAttributes`/序列化按名场景静态不可见；沿用 JK §9 限制声明与 overrides skip。
8. **线程池复用/生命周期**：`ThreadLocal` 默认不 trackAllValues，线程退出槽位释放，与 `[ThreadStatic]` 一致；不注入 `Dispose` 调用（用户原代码也不 dispose 静态槽）；`using`/Dispose 形态在 §5.4 决策。
9. **中间树成本**（catalog §4 风险 11）：新增一环一次复制/reparse/restore；注册表驱动的指纹/preserve 已覆盖，回退重放是仅有的额外轮次且与既有 pass 同构。
10. **热文件冲突**：注册表三文件被多个并行 pass 同时修改，见 §11。

## 11. 并行协调

| 并行条目 | 关系 | 合并顺序与冲突点 |
|---|---|---|
| **C02** generic-deepen（静态泛型字段 holder） | 最强耦合：`[ThreadStatic] static T x` 同时撞两条规则；C02 GD-FIELD-1 已声明排除带 ThreadStaticAttribute 的字段交 C09（[C02 设计 §4.3/§11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c02-generic-deepen-design.md)） | **顺序建议修正为 C09 先于 generic-deepen**：C09 把字段变成 `static readonly ThreadLocal<T> x`（attribute 消失、类型引用类 TPs），C02 的静态字段 holder 规则随后统一 holder 化——holder 按闭型存 `ThreadLocal` 对象、访问器外裹 `.Value`，一份机制同时复刻「每闭型 + 每线程」双重隔离；若按 C02 文档原建议（C09 RunAfter 增 generic-deepen），C02 先跑跳过该字段、C09 后跑留下引用类 TPs 的静态字段，FieldTransformer fail-fast 仍不可达解除。落地动作：本 pass 翻转时 `RunAfterIds={generic}`；C02 落地时其描述符 RunAfter 增 `thread-static`，且 GD-FIELD 分析器增加对「`ThreadLocal<T>` 型静态字段」的识别（不再只按 attribute 排除）。若 C02 先合，C09 rebase 时按本节把顺序约定反过来改，不得两套字段存储并存。v1 对泛型容器一律 CS2J1204 跳过，使两 pass 各自合入期间都无错值输出 |
| **C32/C45** closure-lift（捕获/隐藏状态前移族） | 同属 catalog §3.3「把运行时隐式状态显式化为 C# 源码」族；但 C09 改的是静态字段存储形态，lambda 内站点经符号绑定改 `.Value` 即可（TS-13），不进 display class | 无实现冲突；未来统一捕获分析若建立「隐藏状态前移」共享分析层，ThreadStatic 字段表作为一类状态源接入即可，不提前抽象。链位 closure-lift 在 StateMachine 阶段、远晚于本 pass，单向消费 C09 产物 |
| **C07** struct-value-semantics / E13 make-readonly | ThreadLocal&lt;struct&gt; 变异语义相邻 | C09 链位更早，TS-5 自包含做 temp 提升；C07/E13 看到的已是普通局部临时量 + 属性读写，无需识别 ThreadStatic。热文件不重叠 |
| **C01** ref-out-in | C01 产物可能已把 `M(ref x)` 改成 holder 显式调用 | M1 必须用 C01 后样本实测站点形态（§4.3 TS-6）；Core 目录不重叠 |
| **C08/E17** JavaKeywords | 链尾兜底；本 pass 字段名不变，仅新增 `__cs2j_ts_*` 临时变量（下划线标识符 Java 合法） | 无冲突；若字段原名是 Java 保留字由 C08 统一逃逸 |
| 公共热文件 | [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、注册表测试 | 各 worktree 仅追加各自 id 的行；合并顺序任意，按 rebase 解冲突；禁止顺手重排既有 pass 顺序 |

## 12. Worktree 分配

- 分支：`feature/preproc-c09-thread-static`
- 目录：`.worktrees/preproc-c09-thread-static`
- base：`ba7a0897`（`feat(c49): default-on extension-block materialization`，dev-3.x-26100507 最新提交，2026-10-05 rebase）
- 创建命令：

```powershell
git worktree add .worktrees/preproc-c09-thread-static -b feature/preproc-c09-thread-static ba7a0897
```

实施约束（交实施 worktree，不在本文档执行）：仅在上述 worktree 内改码；按 §9 里程碑逐提交；默认关先行，过 §8 门槛才翻默认开；转换器代码改动（§1.3 删除清单、§5.4 Java 半区映射）留后续 PR。

## 13. 实施校注（2026-10-06，M2 收尾 / Java 半区 / 默认翻转）

本节为实施完成后的事实回填，与正文冲突处以本节为准。M1+M2 预处理 core 在本校注前已落地（`ThreadStaticRewriteTests` 42 例、`SampleThreadStaticSolution` fixture、`ThreadStaticProjectTests`/`ThreadStaticCLITests`）。2026-10-06 补齐 Java 半区并通过 §8 六门后，描述符翻为 `defaultEnabled: true`（第一批提交 `0a17d7e9`：Java 半区实现；行尾归一 `8244778d`；翻转与门证据见本批后续提交）。

### 13.1 §5.4 Java 半区逐项核实结果（对原设想的偏差）

原 §5.4 设想映射到 `java.lang.ThreadLocal` + `withInitial(Supplier)` 静态工厂；实施时改为 compat 承载类型，理由是实例构造 + bean 访问器与既有转换器管道同构，且需要承载 `IsValueCreated`/`Dispose` 语义，规避罕见的「对象创建表达式 → 静态工厂调用」kind 变换：

1. **类型映射**：`System.Threading.ThreadLocal<T>` → `io.github.ningpp.compat.Cs2jThreadLocal<T>`（[TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json) 新增条目），非 `java.lang.ThreadLocal`。[Cs2jThreadLocal](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Cs2jThreadLocal.java) `extends ThreadLocal<T>`，JUnit5 5/5 全绿。
2. **Value 属性**：不登记方法映射，走转换器默认 bean 派生 `getValue()`/`setValue()`。为此在 [JavaNaming.IsBeanAccessorStyleName](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs) 增加门控：仅 bean 风格（get/set/is + 大写）的方法映射在属性读写点消费；字段型映射（如 `Array.Length→length`、`Count→size`）仍留给既有 lowering 管道（无差别消费曾导致 ArrayStaticInvocationTests 回归）。
3. **构造形态（替代 withInitial）**：值类型无参 holder 的默认值合成放在 [ObjectCreationTransformer](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs)——`new ThreadLocal<T>()` 且 T 为非 Nullable 值类型时产出 `new Cs2jThreadLocal<T>(() -> default(T))`（int→0、struct→`new Vec()`）；引用类型保持无参（null 即正确默认）。放转换器而非预处理器的原因：预处理器产物保持规范 C#（`default(T)` 在 C# 两边恒等），转换器一处同时覆盖预处理产物与用户直写的 `new ThreadLocal<值类型>()`（曾在预处理器加默认工厂导致 5 个 golden 失败，已回退）。
4. **Dispose / IsValueCreated / Values**：`Dispose()→dispose()`（委托 `remove()`）；`IsValueCreated→isValueCreated()`（per-thread `ThreadLocal<Boolean>` 跟踪槽，get/set/remove 覆写）；`Values` 与 `trackAllValues=true` 构造显式抛 `UnsupportedOperationException`（Java 无对应语义，显式不支持而非静默错值）。
5. **旧内联臂删除登记**：[FieldTransformer](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L51-L147) 的 `[ThreadStatic]` 内联特例（L57、L126-147）经结构证明在 C09 改写产物上**不可达**——谓词 [ConversionContext.IsThreadStaticFieldDeclaration](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L434-L443) 仅在「static + 仍带 ThreadStaticAttribute」时为真：改写后 holder 已无 attribute，CS2J1201 skip 点为实例字段（非 static）。唯一残余可达面是 **CS2J1204 泛型容器登记 skip 点**（attribute 保留、v1 不支持；strict 下为 Error 阻断转换，非 strict 下落旧臂是已知 M2+ 组合形范畴，交 C02）。旧臂按 §1.3 删除清单留后续 PR（与完成审计台账「双轨债」行对齐）。

附带转换器修复（属性 ++/-- 与表达式体方法是 holder 站点的必要消费支撑）：

- [UnaryExpressionTransformer](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs)：属性上的 `++`/`--` 对内置有符号数值类型（int/long/float/double 及 sbyte/short/char 带窄化 cast）经临时量安全降级为 `getXxx()`/`setXxx(tmp±1)`；枚举/decimal/unsigned/Nullable/自定义运算符/字段风格映射保持 fail-fast。
- [MethodTransformer.BuildBody](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs)：表达式体方法若产生 pending pre-statements，排空展开为块体（此前被静默丢弃，曾导致 javac 找不到降级临时量）。

### 13.2 §8 六门证据（2026-10-06，Windows / net10.0.300 / JDK 28-ea）

1. **中间树正确性（fixture）**：`preprocess --pass thread-static` 对 `tests/SampleThreadStaticSolution` 的中间树 `dotnet build -c Release` 零错误；`dotnet test -c Release` 8/8（含 7 个多线程行为文件）；ProjectTests 内 `ThreadStaticProjectTests` 4 Facts、`ThreadStaticCLITests` 5 Facts 全绿（默认链现在产出中间树，`--disable-pass thread-static` 可关）。
2. **残余闸门**：fixture 输出仅 `SkipControls.cs` 保留 2 个 attribute，恰为登记 skip（CS2J1201 实例字段 + CS2J1204 泛型容器），其余文件零残余（`ThreadStaticProjectTests.Statistics_ReconcileGroundTruth_ResidualGateHasOnlyOwnedSkips_Zero1220` 锁定）；sdk 零残余（见下）。
3. **全仓不回归**：`CSharpToJavaConverter.slnx` Release build **0 错误**；三个测试工程 Release 全量结果——CSharpToJava.Tests **3415 通过 / 0 失败 / 3 跳过**；CSharpToJava.ConversionTests **676 通过 / 4 失败 / 0 跳过**；CSharpToJava.ProjectTests **1843 通过 / 0 失败 / 20 跳过（25m15s）**。ConversionTests 的 4 个红灯全部位于 `AsyncStreamsConversionTests`（AsyncIteratorProducer_PreprocessedThenConverted…、ExpandedAwaitForeach_*、AwaitForeachSource_*、ExpandedAwaitUsing_*），断言缺 `.moveNextAsync().join()` / `.resource.disposeAsync().join()`，属 P0 既有红账（C18/C38）：在 P0 基线 commit `f0c0305a` 的临时 worktree（`.worktrees/_p0-baseline-check`）跑同 4 测试得到同样 4 红；且 `git diff f0c0305a --stat -- src config tests/CSharpToJava.ConversionTests` 显示 C09 改动落在 src/config 9 个文件、ConversionTests 零改动，门规"既有红灯数量不增加"满足。
4. **Java 半区就绪**：compat `mvn test` 5/5；[ThreadStaticJavaHalfTests](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/ThreadStaticJavaHalfTests.cs) 经真实预处理器 + 完整转换链后用 javac+java 跑多线程 harness，覆盖四种 holder 形态（int 无参 → `() -> 0`、用户直写工厂 42、String 引用类型无参、struct Vec → `() -> new Vec()`），断言主线程/工作线程副本隔离与 dispose 后工厂重跑，全绿。
5. **诊断可运维（sdk M0 测量 + triage）**：`D:\csharp-sdk` 全仓 `ThreadStatic` 命中字段 **1**（[xml-tiny StringBuilderCache.t_cachedInstance](file:///D:/csharp-sdk/xml-tiny/src/System/IO/StringBuilderCache.cs#L9-L10)：非泛型静态类、`StringBuilder?` 引用类型、无初始化器，读写点 1 读 2 写；被引用率 100%、泛型容器占比 0、初始化器占比 0、skip 率 0%）。全仓预处理结果：`1 transformed, 1295 clean, 1 fields rewritten, 0 skipped, 0 residual`，零 Warning/Error，无待 triage 项；受影响工程处理树 `dotnet build -c Release` 与原始基线**完全一致**（均 2 警告/0 错误）。全 sdk Release build/test 因与本 pass 无关的既有 TPA-only 基线损坏（见 C26 M0 记录）不具可比性，按「受影响工程零新增失败」记账。
6. **文档**：本 §13 校注定稿（plan 文档不再单立，M1/M2 实施记录并入此节）；FieldTransformer 旧臂删除与 CS2J1204 组合形（C09×C02 M2+）已登记后续 PR。

### 13.3 默认翻转

[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L287-L309) thread-static 描述符 `defaultEnabled: true`；[PassChainOrderTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 锁序数组槽位不变（generic → thread-static → generic-deepen），仅移出默认关排除集；CLI/注册表测试同步翻转。无 convert-project 旧版开关（`BuildLegacyToggles` 不含本 pass），退出路径统一为 `--disable-pass thread-static`。
