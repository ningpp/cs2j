# C29 lock / Monitor 并发原语归一预处理器设计

**日期**: 2026-09-29
**范围**: 新增注册表 pass `concurrency-normalize`（Core `ConcurrencyNormalize\` + CLI `Preprocessing/Adapters/ConcurrencyNormalizePassAdapter.cs`），把 `Monitor.Enter/TryEnter/Exit/Wait/Pulse/PulseAll` 显式调用归一为**可编译、可在 .NET 半区跑并发测试验证语义**的规范 C# 形态；`lock` 语句与 `volatile` 字段的直映路径 v1 不动；实施 PR 不改 C#→Java 转换器，Java 半区桥接（TypeMappings + compat `Monitor.java` 真实化）列为后续 PR
**状态**: 设计已确认，待实施（P3 观察项，建议长期 opt-in，论证见 §8）；**2026-09-29 磁盘复核（HEAD `ef70e5a2`）：`concurrency-normalize` 仍为 stub，预处理层零实现，无「部分已实现的同名 pass」（见文首校订结论 1）**

> **校订（2026-09-29）**——本次只做证据核对与行号/事实订正，未改任何代码、注册表与 catalog：
>
> 1. **实现结论（首要）**：按当前磁盘（HEAD `ef70e5a2`）逐文件复核，`concurrency-normalize` **仍是 [PassStubCatalog.cs L247-L258](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L247-L258) 的 stub，不存在「concurrency-normalize 已 Implemented」与「lock-normalize stub」两条重叠注册**：[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) Implemented 表无该条目；`src/CSharpToJava.Core/ConcurrencyNormalize/` 目录、CLI `Adapters/ConcurrencyNormalizePassAdapter.cs`、`PassChainFactory` switch 臂、Core/fixture 测试**全部不存在**（全仓 grep 零命中，21 个适配器无该族）；全仓亦**从无 `lock-normalize` id**（[PassStubCatalog.cs L110-L119](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L110-L119) 实为 VarianceAudit/C33）。C29 在预处理层零实现，状态维持「设计定稿、待实施」；已存在的仅是转换器老直映（`lock`/`volatile`）与 compat/映射吸收（Interlocked/Thread）+ 死壳 `Monitor.java`，逐项见 §1.1。
> 2. **注册表行号重锚**：stub 块 L247-L258（原引 L269-L280），id 常量在 [L42](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L42)（原引 L44），晚锚注释 L254-L255（原引 L276-L278）；JavaKeywords 尾锚块 [PassCatalog.cs L251-L257](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L251-L257)（concurrency 锚点 [L255](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L255)，原引 L229）；stub 表现共 **21** 条。
> 3. **翻转叙述统一为 IteratorLowering 先例**：id 常量**留在 `PassStubCatalog.Ids` 不迁 `PassIds`**（先例：描述符 [PassCatalog.cs L210](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L210)、工厂 [PassChainFactory.cs L81/L227-L228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L81-L81)、链序测试 [PassChainOrderTests.cs L36](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L36) 均引用 `PassStubCatalog.Ids.IteratorLowering`）；尾锚符号翻转时**无需改**（§3.1/§3.2/M2 已同步订正，原「迁常量 + 换锚点符号」说法作废）。
> 4. **catalog 链接重锚**：C29 条目在 [L664-L679](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L664-L679)（原引 L602-L613）；「可与 C09 合并评估为并发归一单通道」现仅存于 C29 名称行 [L667](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L667)（§3.3 现表 [L1076-L1093](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1076-L1093) 已无 C29 行，原引 L910 失效）；§4 风险 12「静默错误输出」在 [L1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110)（原引 L931）；「非失败型语义缺口」定性在 [L670](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L670)（原引 L608-L611）。
> 5. **测试存在性订正**：lock 直映回归由两处减为**一处**——[ExtraConversionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/ExtraConversionTests.cs) 已随 HEAD `ef70e5a2` 测试删除批次清空（现仅空类），现存 [LanguageFeatureTests.cs L11-L16](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/LanguageFeatureTests.cs#L11-L16)（`LockStatement_ConvertsToSynchronized`）；Interlocked 现存测试 3 个（[InterlockedCompareExchangeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterlockedCompareExchangeTests.cs)、[InterlockedExchangeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterlockedExchangeTests.cs)、[InterlockedOrCompatTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterlockedOrCompatTests.cs)）；**无 `LockStatement*`/`Monitor*` 命名的测试文件**（Glob 零命中），§1.1 已补证据。
> 6. **链位补述**：member-desugar（C11/C14）已翻转，LockedChain 现为 …→eliminate-goto→**member-desugar**→java-keywords（[PassChainOrderTests.cs L18-L40](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L40)）；C29 翻转后与 member-desugar 互无 RunAfter 约束，相对次序只由注册表插入序决定，须在翻转时于 LockedChain 锁定（§3.1）。
> 7. **零散行号订正**：volatile 发射在 [JavaMemberDeclaration.cs L57](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/JavaMemberDeclaration.cs#L57)（原引 L58）；C18 锁内 await 决策 D10 在 [c18 设计 L71](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L71)（原引 L70）；PassStage 信息性注释 [PassDescriptor.cs L3-L7](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L3-L7)（原引 L8-L30）；§12 当前 HEAD 改注 `ef70e5a2`（base `953293b9` 已核仍存在，worktree 命令作为计划工件保留）。

> **开放问题（校订新增，待裁决，禁止以推测补码）**
>
> - **Q-1（同物异名/他仓落点待查）**：本次核对的输入前提称存在「`concurrency-normalize` 已 Implemented（默认关，Stage.Lexical，RunAfter 含 make-readonly/goto/ref-out-in/iterator-lowering/member-desugar/linq-channel；Core `ConcurrencyNormalize/` 7 文件 + CLI adapter）」与「`lock-normalize` stub（Stage.ControlFlow，RunAfter=foreach-deconstruction，catalogItems `C29, C46, C50 (volatile part)`）」两条——当前工作树、已注册全部 pass、全仓 grep（含 tests/）及 `.worktrees` 均无任一对应物。若该实现位于未挂载分支/下游仓，**实施前须先定位实物并裁决与本设计的关系（替代、互补或异名同物）**；裁决前注册表/catalog 一律以「单一 `concurrency-normalize` stub、catalogItems 仅 `C29`」为准。前提所提 C46（成员签名收口，catalog §3.3 属「成员去糖」族）、C50（C# 14 `field`）在现行注册表均无 volatile/C29 归属线索。
> - **Q-2**：catalog C29 名称行保留「可与 C09 合并评估为并发归一单通道」措辞（L667），但 §3.3 合并族表已无对应行；是否在 catalog 补回评估行不影响本设计 §2.9「独立 pass」结论，仅登记叙述漂移。


## 1. 背景与目标

### 1.1 现状：两条直映路径可用，Monitor 显式 API 无映射且 compat 是死壳

| 子项 | 现状（HEAD `5e3e796d` 初核；2026-09-29 校订于 HEAD `ef70e5a2` 逐项重锚，存在性以当前磁盘为准） | 判定 |
|---|---|---|
| `lock (o) {…}` | 语句分发 [StatementTransformer.cs L52](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L52) → [TransformLockStatement L286-L293](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L286-L293)，直接发 `synchronized (expr) { body }`；回归测试现存**一处**：[LanguageFeatureTests.cs L11-L16](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/LanguageFeatureTests.cs#L11-L16)（`LockStatement_ConvertsToSynchronized`）；原第二处 [ExtraConversionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/ExtraConversionTests.cs) 的 lock 用例已随 HEAD `ef70e5a2` 测试删除批次移除（该类现仅空类，校订 5） | **直映可用**（测试仅一处） |
| `volatile` 基元字段 | 修饰符直映 [FieldTransformer.cs L410](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L410) → `JavaModifiers.Volatile`（发射点 [JavaMemberDeclaration.cs L57](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/JavaMemberDeclaration.cs#L57)）；非基元字段只加一行建议注释（[FieldTransformer.cs L354-L356](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L354-L356)） | **直映可用**（注释非诊断，可见性差） |
| `Interlocked.*` | 成员映射齐（[TypeMappings.json L8131-L8161](file:///d:/code/cs2j/config/TypeMappings.json#L8131-L8161)，CompareExchange/Exchange/Increment/Decrement 四成员），compat [Interlocked.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Interlocked.java) / [InterlockedHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/InterlockedHelper.java) 吸收；但另有一条类型映射 `System.Threading.Interlocked → AtomicInteger`（[L3608-L3613](file:///d:/code/cs2j/config/TypeMappings.json#L3608-L3613)）与 [L4421-L4424](file:///d:/code/cs2j/config/TypeMappings.json#L4421-L4424) 的 compat 映射**双映射并存**，M1 盘点；现存转换侧测试 [InterlockedCompareExchangeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterlockedCompareExchangeTests.cs)、[InterlockedExchangeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterlockedExchangeTests.cs)、[InterlockedOrCompatTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/InterlockedOrCompatTests.cs)（无 `LockStatement*`/`Monitor*` 专项测试，Glob 零命中） | **已吸收，不动**（盘点双映射） |
| `Thread.MemoryBarrier/Sleep` | compat [ThreadHelper.java L55-L85](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ThreadHelper.java#L55-L85)（VarHandle 全栅栏/sleep），`System.Threading.Thread → ThreadHelper`（[TypeMappings.json L3566-L3571](file:///d:/code/cs2j/config/TypeMappings.json#L3566-L3571)） | **已吸收，不动** |
| `Monitor.Enter/Exit` 显式调用 | **全仓零特判**（src 全目录 grep `Monitor\.` 无命中；校订于 HEAD `ef70e5a2` 复核仍零命中）；`System.Threading.Monitor` 在 TypeMappings.json 无类型/成员映射（grep 零命中），全仓也无 compat 通配 import。按未映射静态调用回退路径（[InvocationExpressionTransformer.cs L216-L224](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L216-L224)）推断产物为 `Monitor.enter(...)` 形且**无 import**，实际发射文本由 M1 探针锁定 | **缺口**（疑似 Java 编译失败；待探针确认） |
| compat `Monitor.java` | [Monitor.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java) 存在但**无任何配置引用，是死壳**；且语义错误：`enter` 空实现（[L13-L19](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L13-L19)，注释自称「锁在调用方 synchronized 块获取」——手写 Enter 调用点并无该块）；`tryEnter(obj,int)` **恒返回 true**（[L51-L57](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L51-L57)，超时分支永不可达）；`wait/pulse` 直接 `obj.wait()/notify()`（[L70-L90](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L70-L90)），未持 intrinsic lock 时 Java 抛 `IllegalMonitorStateException` | **死壳 + 错误语义** |
| `Monitor.TryEnter(timeout)` | 无映射；即使被接到死壳，超时分支恒成功——等价于「永不超时」的静默错值 | **静默丢失** |
| `Monitor.Wait/Pulse/PulseAll` | 无映射；接到死壳时仅在「外层恰有 `synchronized(o)`」（即用户同时写了 `lock(o)`）才偶然成立，裸用即运行时炸；C# 侧对应异常 `SynchronizationLockException` 已有映射（[TypeMappings.json L977-L981](file:///d:/code/cs2j/config/TypeMappings.json#L977-L981)） | **缺口/偶然可用** |
| `System.Threading.Volatile.Read/Write` 静态 API | 无类型/成员映射、无 compat `Volatile.java`（grep 零命中）——catalog C29「Volatile 静态 API 由 compat 吸收」的叙述与证据不符；仅 `volatile` **关键字字段**直映 | **缺口**（M1 修正 catalog 叙述） |

### 1.2 失败面定性：非失败型语义缺口

catalog 把 C29 定性为「非失败型语义缺口」（[L670](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L670)）：主路径（`lock`/`volatile` 基元）今天可用且有测试；痛处在手写 Monitor 面——要么 Java 半区不可编译（无映射无 import），要么接上 compat 死壳后**静默错值**（超时永真、wait/notify 脱离持锁块）。这正是 §4 风险 12 要求消灭的「不报错、却产出错误运行时行为」一族。C29 的价值是**在 C# 阶段把这些构造显式化、结构化、可诊断化**，而不是新发明一套并发运行时。

### 1.3 目标

1. 把手写 `Monitor.Enter/Exit` 编译器形态**逆向归一为 `lock` 语句**（重回唯一被 Java 直映支持的结构化形态）；
2. 把 `TryEnter`/`Wait` 各种重载（TimeSpan、裸 bool 返回、lockTaken 形）**归一为统一规范形**（3 参 int 毫秒 + `lockTaken` + try/finally 守卫），超时分支与 bool 返回值显式保留——C# 树始终真跑 BCL `System.Threading.Monitor`，.NET 半区并发测试可直接验证；
3. 对 Java intrinsic lock 无法表达的子项（timed `TryEnter`、不可证明持锁的 Wait/Pulse、`exitContext:true`）**显式发码**，不静默、不占位；
4. `lock`/`volatile` 直映项保持不动（M3 才评审是否为 CsCore 输入收敛而前置，默认建议永不前置，论证见 §2.1/§2.5）；
5. 输出 Java 半区消费清单（映射 + compat 真实化），本 pass 不实施。

## 2. 关键设计决策表

| 决策点 | 结论 | 备注 |
|---|---|---|
| 独立 pass vs 并入 C09 | **独立 pass**（注册表已独立 stub，按独立写） | 边界见 §2.9、§11；不做物理合并 |
| `lock` 语句 | **v1 保留直映，不前置**；M3 提供 `FrontLoadLock` 选项（默认 false，建议永久） | §2.1 |
| 手写 `Enter/Exit` 对 | 识别编译器两种标准形，同方法内**逆向归一为 `lock`**；非规范形 CS2J2910 原样不动 | CN-2 |
| `Monitor.Enter(o, ref bool)` 模式 | C# 4+ 编译器 lock 降低形即此模式，正是 CN-2 的首要识别目标，归一回 `lock` | §2.2 |
| `TryEnter` 无超时/0ms | 规范为 `TryEnter(o, 0, ref taken)` 形；Java intrinsic 无 tryLock，站点挂 CS2J2911 闸门 | CN-3 |
| `TryEnter` 超时（int/TimeSpan） | 统一 3 参 int 毫秒规范形，超时分支显式保留；CS2J2911；**v1 不声称 Java 可运行**，Java 半区方案二选一（§5.4） | §2.3 |
| 裸 bool 返回形 `if(TryEnter(o,ms)){try{}finally Exit}` | 改写为 `lockTaken` 规范形（消除 Enter 成功后、进入 try 前异常窗口） | CN-3 |
| `Wait/Pulse/PulseAll` | 保持 BCL 调用；TimeSpan→显式 int 毫秒临时量；必须通过同对象持锁校验；映射 `wait/notify/notifyAll` | §2.4、CN-4/CN-5 |
| `Wait(exitContext:true)` | 无 Java 对应 → CS2J2921，站点原样 | CN-4 |
| Wait 超时 bool 返回 | 保留；Java `wait(long)` 是 void，compat 包装为近似 bool（★风险，§10-1） | §5.4 |
| 锁对象身份审计 | `lock(this)`/`lock(typeof(T))`/`lock(string)` → CS2J2931 Info（不改写、不合成私锁字段） | §2.1、CN-6 |
| `volatile` 字段 | **保留直映**；`VolatileRewriter` v1 只审计；M3 选项默认 false（建议永不前置） | §2.5、CN-7 |
| `Interlocked`/`Thread` 静态 API | 不动；仅 M1 盘点 Interlocked 双映射 | CN-8 |
| `Volatile` 静态类 | 不改（已是显式 C#），计数 + CS2J2960 Info；Java VarHandle 映射登记后续 | CN-9 |
| `Mutex`/`SemaphoreSlim`/事件等待句柄等 | **首期排除**，命中计数 CS2J2961，不改不阻断（清单见 §2.7） | CN-11 |
| async 内 lock/await | C# 基线 CS1996 必拒；本 pass 防御镜像 CS2J2930，不与 C18 CS2J1805 抢诊断 | §2.8、CN-10 |
| 支撑库 | `PassSupportLibrary.None`：目标形只用真 BCL Monitor，.NET 半区守门 | §5.2 |
| 默认开关 | 翻转即 Implemented 但默认关；**建议长期 opt-in** | §3.4、§8 |

### 2.1 lock 语句：保留直映，不前置 Enter/try/finally

C# 编译器把 `lock (o) { BODY; }` 降低为（C# 4+ 形）：

```csharp
bool __lockTaken = false;
try { Monitor.Enter(o, ref __lockTaken); BODY; }
finally { if (__lockTaken) Monitor.Exit(o); }
```

理论上的「前置」就是在 C# 树做这步降低。**推荐不做**，理由：

1. **Java intrinsic lock 只能由 `synchronized` 结构化获取，没有任何方法调用可以获得它**——这正是 compat `Monitor.enter` 只能写空实现的根本原因（[Monitor.java L13-L19](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L13-L19)）。前置后转换器仍须把该模式**再识别回** `synchronized`，等于给转换器增加一段脆弱的模式匹配去抵消预处理，零语义收益；
2. 直映路径已实现（8 行）且有回归测试锁定（现存一处，校订 5；fixture 再补嵌套/throw 并发行为）；前置只服务于「CsCore 输入形态收敛」的假设目标，而 lock 是语句糖、不影响任何类型/签名面，不是 CsCore 的收敛痛点；
3. 反向（把手写 Enter/Exit 归一回 lock，CN-2）才是有产出的方向。

历史问题只审计不改写（CN-6）：`lock (this)`（监视器对外可见，外部代码可致死锁，C#/Java 通病）；`lock(typeof(T))`（C# 锁的是每个闭型不同的 `RuntimeType`；Java 侧 `typeof(T)` 发 `T.class` 且剥泛型实参，[TypeOperationTransformer.cs L126-L129](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L126-L129)，泛型擦除后多闭型共享一把锁）；`lock("…")`/字符串表达式（驻留共享，两边规则不同）。合成私有锁字段会改变类型成员面与对象身份，v1 不做，留 M3 显式选项。

### 2.2 Enter/Exit 与 `Monitor.Enter(obj, ref bool)` 模式

手写 Enter/Exit 是 Java 半区最差形态（无结构化块可映）。CN-2 在**同方法内**识别两种规范形并归一回 `lock (o) { BODY }`：

- 形 A（C# 4+，含 `Enter(o, ref taken)`）：`bool t=false;` 紧邻 `Monitor.Enter(o, ref t);` + `try { BODY } finally { if (t) Monitor.Exit(o); }`，且 `t` 未在 BODY 被引用；
- 形 B（旧式）：`Monitor.Enter(o);` 紧邻 `try { BODY } finally { Monitor.Exit(o); }`。

识别条件从严：唯一 Exit 在 finally、Enter 与 try 紧邻、三处锁对象表达式经 SemanticModel 绑定为同一符号（local/parameter/field，且不含每次求值可能变值的调用/volatile 读）、`t` 为该模式独占局部量。任一不满足（跨方法配对、条件 Enter、多 Exit 路径、catch 中释放、taken 被业务代码读取）→ CS2J2910，区域原样不动（仍合法 C#），禁止半改。

### 2.3 TryEnter 超时分支

intrinsic lock 不存在 `tryLock(timeout)`，这是 C29 唯一**在 Java 半区无直接等价物**的子项。v1 的处置是「C# 侧显式规范 + Java 侧显式挂账」，而不是伪造语义：

```csharp
// 输入（裸 bool 形，超时分支隐藏在 if 外）
if (Monitor.TryEnter(_gate, 50)) { try { Work(); } finally { Monitor.Exit(_gate); } }
else { TimeoutFallback(); }

// 输出（规范形：3 参 int 毫秒 + lockTaken + finally 守卫；分支全部显式保留）
bool __cs2j_mt = false;
Monitor.TryEnter(_gate, 50, ref __cs2j_mt);
try
{
    if (__cs2j_mt) { Work(); }
    else { TimeoutFallback(); }
}
finally { if (__cs2j_mt) Monitor.Exit(_gate); }
```

- TimeSpan 超时 → 提升 `int __cs2j_to = (int)timeout.TotalMilliseconds;` 临时量后入 3 参形（合法 C#，与用户改调 int 重载同形；`-1`/负值、`TimeSpan.Zero` 原样保留语义）；
- 已是 3 参规范形的只做结构校验；`TryEnter(o)`/`TryEnter(o,0)` 归一时补 `0`；
- 每个规范化站点挂 CS2J2911（Warning，strict Error）：**显式声明「Java 半区兼容层未就绪前不得声称产物可在 Java 正确运行」**，杜绝接回恒真死壳；
- Java 半区两条路线（per-object `ReentrantLock` 表 vs intrinsic + 显式不支持）见 §5.4，M3 评审。

### 2.4 Wait/Pulse/PulseAll 与 Java wait/notify 的语义差异

| 维度 | C# `Monitor` | Java intrinsic | 归一处置 |
|---|---|---|---|
| 可重入 | 同线程可重复 Enter | `synchronized` 可重入 | 等价；递归 lock 仅 Info（CN-12） |
| 调用前置 | 必须持锁，否则 `SynchronizationLockException` | 必须持锁，否则 `IllegalMonitorStateException` | 持锁校验 CN 锁所有权（CS2J2920） |
| 通知面 | `Pulse`/`PulseAll` | `notify`/`notifyAll` | 机械映射；要求同对象持锁 |
| 超时签名 | `Wait(int ms)`/`Wait(TimeSpan)` 返回 **bool**（false=超时） | `wait(long ms)` **void** | C# 侧保留 bool；compat 计时包装（★近似，§10-1） |
| 无限等待 | `Wait(o)`、`Wait(o,-1)` | `wait()`、`wait(0L)` | 保留；0/-1 映射规则 Java 半区定 |
| 虚假唤醒 | 存在，文档要求谓词循环 | 存在，要求谓词循环 | 两边一致；不改循环，Info 提示 |
| Pulse 早于 Wait | 通知丢失 | notify 早于 wait 丢失 | 两边一致；fixture 覆盖 |
| 中断 | `ThreadInterruptedException`（[L990-L995](file:///d:/code/cs2j/config/TypeMappings.json#L990-L995) 已映射） | checked `InterruptedException` | checked 异常 throws 传播 M1 盘点（§5.4-5） |
| 精度 | 毫秒；TimeSpan tick→ms 截断 | 毫秒 + 纳秒重载 | int 毫秒形 |
| `exitContext` | `Wait(o,ms,exitContext)` 上下文封送 | 无对应 | `true` → CS2J2921 |

Wait/Pulse 调用**保持 BCL `System.Threading.Monitor` 原调用**（不发明 C# helper），C# 树的运行时语义 100% 真实，这是 .NET 半区并发测试守门的前提；桥接全部后置到 Java 半区映射。

### 2.5 volatile 字段：保留直映

C# `volatile`（读 acquire / 写 release；允许类型=引用类型、定宽基元、对应枚举、class 约束类型参数）与 Java `volatile`（同 fence 语义，且 long/double 也保证原子性，是超集）在允许类型集上语义一致，[FieldTransformer.cs L410](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L410) 的修饰符直映成立。前置为 `Volatile.Read/Write` 会改写全部读写点（含 ref/表达式树/复合赋值），churn 极大、零收益。结论：`VolatileRewriter` v1 仅做审计计数与非基元诊断（把 [L354-L356](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L354-L356) 的注释升级为可见 Info），重写能力 M3 挂 `FrontLoadVolatile=false`，建议永久不开。

### 2.6 Interlocked / Thread 静态 API

不动。compat + 成员映射已吸收（§1.1）。M1 仅盘点 `Interlocked` 双类型映射（`AtomicInteger` vs compat `Interlocked`）的实际命中路径，记录不改。

### 2.7 首期范围排除清单（命中只计数 CS2J2961）

`Mutex`（含命名系统互斥；现状 `Mutex → ReentrantLock` [L3580-L3585](file:///d:/code/cs2j/config/TypeMappings.json#L3580-L3585)，命名语义本不等价）、`Semaphore`/`SemaphoreSlim`、`WaitHandle` 及 `WaitAny/WaitAll/WaitOne/SignalAndWait`、`AutoResetEvent`/`ManualResetEvent`/`ManualResetEventSlim`/`EventWaitHandle`（现状映 `CountDownLatch`，[L3594-L3606](file:///d:/code/cs2j/config/TypeMappings.json#L3594-L3606)，后者不可重置，AutoReset 语义不等价）、`Barrier`/`CountdownEvent`、`SpinLock`/`SpinWait`、`ReaderWriterLock`/`ReaderWriterLockSlim`、`Thread.Join` 重载、`TaskCompletionSource` 同步用法、`System.Threading.Channels`。这些是独立 P2/P3 题，不借 C29 扩面。

### 2.8 async 内 lock

`await` 出现在 `lock` 体内 = CS1996（C# 基线必拒）；锁内持 Monitor 跨 await 本就不合法，无需设计「跨挂起持锁」表达。所有权归 C18（[C18 设计 D10](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L71)，CS2J1805）；C29 仅基线优先地防御镜像 CS2J2930（基线快照已拒则不发）。

### 2.9 独立 pass 还是与 C09 合并：结论=独立

注册表已给独立 stub，本设计按独立 pass 落，不合并：

1. **改写面不同**：C09 改字段**存储声明 + 全部读写点**（`[ThreadStatic] → ThreadLocal<T> + .Value`）；C29 改**语句结构与 Monitor 调用**，不碰字段声明；
2. **验证方法不同**：C09 验每线程副本；C29 验互斥/等待/通知/超时的并发行为（§7.2）；
3. **Java 半区不同**：C09 消费 `ThreadLocal` 元数据；C29 消费 Monitor 映射 + compat 真实化；
4. **链位不同**：C09 RunAfter=`generic`（早，[PassStubCatalog.cs L167](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L167)），C29 RunAfter=`eliminate-goto`（晚锚点，[L256](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L256)；与已翻转的 member-desugar 互无 RunAfter 约束，相对次序翻转时在 LockedChain 锁定，见 §3.1 校注）；
5. catalog 的「合并为并发归一单通道」措辞现仅存于 C29 名称行（[L667](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L667)），§3.3 合并族表已无该建议行（Q-2）——是**评估建议不是指令**；未来统一引擎若建「并发族合批」是 §5.4 统一引擎话题，届时以共享 Analyzer 合批，不提前物理合并。C09 先跑后，`lock(x.Value)` 对 C29 只是普通表达式锁对象，天然兼容（§11）。

## 3. 注册表与链位契约

### 3.1 描述符

翻转时从 [PassStubCatalog.cs L247-L258](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L247-L258) 删除 stub **条目**（常量保留，见 §3.2），在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) `Implemented` 表中**置于 `eliminate-goto` 之后、`java-keywords` 之前**（拓扑顺序由 [PassChainOrderTests.cs L18-L40](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L40) 锁死）。**校注（2026-09-29）**：member-desugar 翻转后已占 goto 之后一位（[PassCatalog.cs L230-L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)），其 RunAfter 只锚 StructValueSemantics/ClosureLift 两个 stub，与 C29 互无强约束；建议 C29 描述符**置于 member-desugar 之后**，并同步在 LockedChain 增列锁定该相对次序（拓扑并列时顺序只由注册表插入序决定）。

```text
Id                          = PassStubCatalog.Ids.ConcurrencyNormalize // "concurrency-normalize"，常量留 PassStubCatalog.Ids（IteratorLowering 先例）
DisplayName                 = "Concurrency normalization"
IntermediateDirectoryName   = ".cs2j-concurrency-normalize-src"
Stage                       = PassStage.Declarative
Status                      = PassStatus.Implemented
DefaultEnabled              = false（长期 opt-in，见 §3.4/§8）
RunAfterIds                 = { "eliminate-goto" }   // 无强功能依赖；晚锚点仅为命名 pass 保最后、--list-passes 位置确定
SupportLibrary              = PassSupportLibrary.None
NeedsSemanticModel          = true
VerifyBuildByDefault        = true
VerifyTestsByDefault        = true
CatalogItems                = "C29"
Description                 = "Normalize explicit Monitor enter/try-enter/wait/pulse calls into canonical lock-based C# forms; lock/volatile direct mapping untouched."
```

链位语义：`… → make-readonly → iterator-lowering → eliminate-goto → member-desugar(已翻转) → concurrency-normalize(C29，建议位次) → java-keywords(C08) → 主转换`（member-desugar 与 C29 的相对次序以翻转时 LockedChain 锁定为准，见上校注）。Stage 标 `Declarative` 而锚点在链尾是注册表既有事实（[PassDescriptor.cs L3-L7](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L3-L7) 注明 Stage 仅信息性，顺序只由 RunAfterIds 决定），不改。RunAfter 晚锚点无功能依赖——C29 的语法面（lock/Monitor 调用）不被 goto 消除之后的任何 pass 产生，但晚锚点保证命名 pass 恒最后，沿用 stub 注释原话（[PassStubCatalog.cs L254-L255](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L254-L255)）。

### 3.2 Stub→Implemented 翻转清单

| 文件 | 改动 |
|---|---|
| [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) | 删除 L247-L258 `ConcurrencyNormalize` stub **条目**；**`Ids.ConcurrencyNormalize` 常量（[L42](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L42)）保留不迁**——IteratorLowering 先例：翻转后描述符（[PassCatalog.cs L210](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L210)）、工厂（[PassChainFactory.cs L81](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L81)、[L227-L228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L227-L228)）、链序测试（[PassChainOrderTests.cs L36](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L36)）一律继续引用 `PassStubCatalog.Ids.*` |
| [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | 按 §3.1 插 Implemented 描述符（id 引用 `PassStubCatalog.Ids.ConcurrencyNormalize`，置于 member-desugar 之后）；**不新增 `PassIds` 常量**；[L255](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L255) JavaKeywords 尾锚**符号原样不动**（翻转前即引用 `PassStubCatalog.Ids.ConcurrencyNormalize`，无「换符号」动作；原计划「迁常量 + 换锚点符号」已按 IteratorLowering 先例作废，校订 3） |
| `src/CSharpToJava.CLI/Preprocessing/Adapters/ConcurrencyNormalizePassAdapter.cs` | **新增**适配器（§5.1）——当前磁盘不存在（21 个现存适配器无该族，校订 1） |
| [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) | `CreateAdapter` switch 增 `PassStubCatalog.Ids.ConcurrencyNormalize => new ConcurrencyNormalizePassAdapter(...)`（先例 [L227-L228](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L227-L228)）；不进 `BuildLegacyToggles`（无历史开关；IteratorLowering 先例另在 [L81](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L81) 显式列 `= false`，翻转时可照补但非必需），经通用 `--enable-pass/--disable-pass` 控制 |
| `src/CSharpToJava.Core/ConcurrencyNormalize/Concurrency*.cs` | 新增 Core 组件（§4）；目录当前磁盘不存在（校订 1） |
| 测试 | 链序/`--list-passes` 断言（[PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)、[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs)）、Core 单测、fixture、residual 闸门、CLI（§7） |

不改：`Program.cs` 指纹逻辑（全集令牌自动纳入，翻转一次性缓存失效，foundation plan 既定决策）；保留目录由已执行 pass 目录名驱动；通用 verb `preprocess --pass concurrency-normalize` 自动可达，stub CS2J9001 拒绝路径翻转后自然消失。

### 3.3 中间目录与验证

- 中间目录 `.cs2j-concurrency-normalize-src`，输入为 eliminate-goto 产物树；干净文件字节级复制（含 BOM 保持）；
- `VerifyBuild/VerifyTests` 默认开（最严档），`DeleteBuildOutputsAfterRestore = true`；
- 只增「一环一棵树」，不绕指纹、不多轮 restore（回退重放除外，与 JK/RR/C09 同构，遵守 catalog §4 风险 11）；
- 无支撑库：不新增 `PassSupportLibrary` 枚举值、不注入工程、不触发支撑工程过滤。

### 3.4 默认关，且建议长期 opt-in

与 C09「翻转即候选默认开」不同，C29 建议**长期 opt-in**：①直映主路径（lock/volatile 基元）今天正确且有测试，默认链零缺口；②本 pass 的规范化价值要等 Java 半区 compat 真实化后才闭环，而 timed TryEnter 在 Java 侧根本没有无代价等价物；③训练集手写 Monitor 命中率由 M1 测量，预期低频；④默认开等于让所有工程为低频构造多付一整环 build/test。§8 仍给出门槛全文，作为「若数据证明值得」的翻正路径。

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/ConcurrencyNormalize/`，命名空间 `CSharpToJava.Core.ConcurrencyNormalize`。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `ConcurrencyOptions.cs` | `record ConcurrencyOptions`：`bool Strict`（接 `PassChainConfiguration.Strict`，默认 true）、`bool RewriteEnterExitPairs`（M2 默认 true）、`bool CanonicalizeTryEnter`（true）、`bool CanonicalizeWait`（true）、`bool AuditLockIdentity`（true）、`bool AuditVolatile`（true）、`bool FrontLoadLock`（M3，默认 false，置 true 在 M2 抛 `NotSupportedException`，不预埋半成品）、`bool FrontLoadVolatile`（同上）、`bool ReportInfo` |
| `ConcurrencyStatistics.cs` | `FilesScanned/FilesTransformed/FilesSkippedClean`、`LockStatementsSeen/LockNestedRegions`、`EnterExitPairsDetected/Rewritten/Rejected`、`TryEnterSitesDetected/Canonicalized`（分 `Timed/ZeroTimeout/LockTaken/BareBool`）、`WaitSites`（分重载）、`PulseSites/PulseAllSites`、`OwnershipWarnings`、`IdentityAuditsByKind`、`VolatileFieldsPrimitive/NonPrimitive`、`VolatileStaticApiSites`、`OutOfScopeSitesByType`、`InfeasibleSites`、`SkippedByReason`、`ResidualMonitorCalls`；实现 runner 通用投影形状 |
| `ConcurrencyDiagnostic.cs` | `ConcurrencySeverity`（Info/Warning/Error）、`ConcurrencyRule`、`ConcurrencyDiagnostic(Severity, Rule, Code, Message, FilePath?, Line?)`、码常量表（§6）、`ToMsBuildString()` |
| `ConcurrencyModels.cs` | `enum MonitorCallKind { Enter, EnterWithTaken, Exit, TryEnter, TryEnterWithTaken, Wait, Pulse, PulseAll }`；`sealed class MonitorCallSite`（节点/span/kind/重载参数形/所在方法）；`sealed class ConcurrencyRegionPlan`（方法级原子：含方法符号、TouchedFiles、全部 sites、锁对象绑定摘要、`Outcome(Active/Skipped)`、FailCode、OwnsTouch）；`enum Outcome` |
| `MonitorRewriteAnalyzer.cs` | **整树一次**：`Compilation.GetTypeByMetadataName("System.Threading.Monitor")`/`"System.Threading.Volatile"` 取符号，调用站点一律 `GetSymbolInfo` + `IMethodSymbol` 身份判定，禁止文本匹配；逐方法建 region plan：枚举 lock 区域、Enter/Exit 配对匹配（CN-2 结构条件）、TryEnter/Wait/Pulse 站点分类、锁对象符号绑定、持锁所有权判定、锁对象身份审计、volatile 字段/静态 API/范围外类型计数；产出确定性排序 plan |
| `LockRewriter.cs` | `CSharpSyntaxRewriter`：CN-2 逆向归一、CN-3 TryEnter 规范形、CN-4 Wait 重载/TimeSpan 归一；生成 `__cs2j_mt`/`__cs2j_to` 确定性命名（碰撞加序号）；改动节点挂 `RewriteAnnotation = new("Cs2jConcurrencyNormalize")`；只接受语义绑定命中 |
| `VolatileRewriter.cs` | v1 **只审计不重写**（由 Analyzer 调其判定谓词）：volatile 字段分类（基元/非基元）、`System.Threading.Volatile` 静态站点；重写分支在 `FrontLoadVolatile=false` 下不接（M3） |
| `ConcurrencyResidualScanner.cs` | 重写后扫描输出树全部 `System.Threading.Monitor` 绑定站点（Enter/TryEnter/Exit/Wait/Pulse/PulseAll），与 active/skipped plan 集合对账；无归属残余 >0 → CS2J2970 |
| `ConcurrencyPreprocessor.cs` | 入口：`BindCompilation(CSharpCompilation)`/`Analyze()`/`ExcludePlans(ids)`/`Plans`/`RewriteFile(string, SemanticModel, …)`/`ResidualScan(Compilation)`/`internal static FormatAndValidate(CompilationUnitSyntax)`（三候选，同 JK/RR/C09） |

编译构造沿用标准脊柱：`CSharpParseOptions(LanguageVersion.Latest)` + runner 默认预处理符号、`GetBasicReferences()`、`allowUnsafe: true` DLL 编译；分析/残余扫描跑同一棵全编译。

### 4.2 数据流（标准骨架，不自定义 ExecuteAsync）

1. **CompilationReadyAsync（整树一次）**：runner 建完全树编译 → `BindCompilation` + 基线诊断快照（async/CS0185/CS0677 等基线已拒项不抢诊断，同 C18/C20 基线优先原则）→ `Analyze()`，plan 一次成型；
2. **Plan（方法级原子）**：每个含 Monitor 站点的方法一条 region plan；任一不可行站点（2910/2921 等）只把**该站点结构**留原样、可做的其它站点照改，但不得产生「Enter 归一了而配对 Exit 没归一」的半截结构——配对类判定为 plan 级，失败则该方法 CN-2/CN-3 整体不动；Wait/Pulse 规范化是单站点独立的，可分别改写；
3. **Rewrite（逐文件）**：Changed=false 返回 null，runner 字节级复制；诊断补 RelativePath，统计合并；
4. **AfterFilesAsync**：ResidualScan 对账 + strict 升级；
5. **回退**：中间树重编译新增错误按 TouchedFiles/OwnsTouch 归属方法 plan，ExcludePlans 后从原始副本重放；无法归属 → CS2J2900 全回退（与 JK030/TS1200 同构；规范化只动语句结构且 C# 侧类型严格，稳态预期零回退）；
6. restore/build/test 由 runner 标准骨架收尾。

### 4.3 规则全集（CN-x）

- **CN-1 lock 保留**：`LockStatementSyntax` v1 不重写，仅嵌套计数与 CN-6 审计；锁内 throw 的释放正确性由 C# 编译器降低形保证（fixture 行为验证）。
- **CN-2 Enter/Exit→lock**：形 A/形 B 识别条件如 §2.2；归一时删除独占 bool 声明，`BODY` 原样入锁块；归一出的锁对象表达式保持原文一次求值语义（`lock(o)` 与规范 Enter 形都只求值一次）；不满足条件 → CS2J2910，方法该配对整体不动。
- **CN-3 TryEnter 规范形**：2 参/3 参/裸 bool/TimeSpan 全部归一为 `TryEnter(o, <intMsExpr>, ref __cs2j_mt)` + try/finally `if (__cs2j_mt) Exit(o)` 守卫；用户 `if(!taken)/else` 分支结构保留（裸 bool 形按 §2.3 补全 else 空路径外的原有 fallback）；0ms 形补 `0`；已规范形不改；站点 CS2J2911；非规范结构（Exit 不在 finally、taken 跨方法流转、条件化 Enter）→ CS2J2910 区域不动。
- **CN-4 Wait 归一**：`Wait(o)` 原样；`Wait(o,int)` 原样；`Wait(o,TimeSpan)` 提升 int 毫秒临时量后转 int 重载；`Wait(o,int,bool exitContext)` 中常量 false 归 int 重载，常量 true/变量 → CS2J2921 不动；bool 返回值（超时分支）原样保留；持锁校验见 CN 锁所有权；`Wait(o,0)`/`-1` 不改数值。
- **CN-5 Pulse/PulseAll**：调用原样（真 BCL），只做持锁校验与计数。
- **CN-6 锁对象身份**：`this`、`typeof(…)`（含 `GetType()`/`Type` 变量保守归入审计）、字符串字面量/驻留可疑表达式、跨闭型泛型 `typeof(T<…>)` → CS2J2931 Info（消息带 kind 与后果说明）；值类型锁对象（CS0185）基线已拒不发。
- **CN-7 volatile 字段**：不重写；基元/非基元计数；非基元 → CS2J2940 Info（替代转换器注释，M3 评估升 Warning）；`volatile struct` 字段（CS0677）基线项不抢。
- **CN-8 Interlocked/Thread**：不绑定不计数进残余；M1 双映射盘点。
- **CN-9 `System.Threading.Volatile` 静态 API**：计数 + CS2J2960 Info，不改写。
- **CN-10 async**：lock 区域词法内含 `await` 表达式且基线快照未报 CS1996（防御情形）→ CS2J2930；`Task`-returning 方法内裸用 Wait/Pulse 不阻断（同步等待是合法但可疑代码，归 2920 持锁校验覆盖）。
- **CN-11 范围外原语**：§2.7 清单元类型的调用/对象创建 → 按类型 CS2J2961 Info 聚合计数，不改不阻断、不进残余。
- **CN-12 递归/异常**：同对象嵌套 lock（可重入）Info；CN-2/CN-3 归一出的 finally 守卫与原编译器异常释放语义一致，fixture 专测「锁内 throw 后他线程可再入」。

**CN 锁所有权判定（v1 词法-保守，不做完整 dataflow）**：Wait/Pulse/PulseAll 站点必须词法位于某 `lock(o)`（含 CN-2 归一出的）语句体内，且锁表达式与实参表达式绑定同一对象符号：同一 local/parameter，或同一 `IFieldSymbol`（含 `this.f`/限定形）；锁位于条件/循环而站点在其外、或锁表达式含方法调用/volatile 读/属性访问（每次求值可能不同对象）→ 判不可证明，CS2J2920。宁假阳不漏放（假阳只留 Warning 且不改代码）。

## 5. CLI 适配器与集成

### 5.1 ConcurrencyNormalizePassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/ConcurrencyNormalizePassAdapter.cs`（预计 120-170 行），形态最近 [EnumPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs)（语义逐文件 + AfterFiles 对账），在 `CompilationReadyAsync` 做一次性 Bind/Analyze（同 C09 适配器）：

- `Descriptor => PassCatalog.ImplementedById(PassStubCatalog.Ids.ConcurrencyNormalize)`（IteratorLowering 先例，常量不在 `PassIds`）；双 verify=true；`DeleteBuildOutputsAfterRestore => true`；
- 持有单个 `ConcurrencyPreprocessor` 与累计统计/诊断；options 从 `PassChainConfiguration`（Strict/开关）构造；
- `RewriteFileAsync` 调 `RewriteFile`，Changed=false 返回 null；
- `AfterFilesAsync`：ResidualScan + strict 升级；restore/build/test 失败映射 CS2J2902/2903/2904；
- `HasErrors`/`BuildChainSummaryLine`（lock/Enter/TryEnter/Wait/Pulse 计数、跳过、残余）/`BuildChainDiagnosticLines`（Info 仅 verbose）；
- 不新增门面类；测试走 runner 与通用 `preprocess` verb，不建独立 verb、不提供单文件 `-i/-o`（语句级改写单文件虽可工作，统一走项目 verb；CS2J2909 保留位稳态不触发）。

### 5.2 无支撑库决策

不用 C# 支撑库、不新建 Java compat 类于本 pass：目标形态是**真 BCL `System.Threading.Monitor`**，合成 helper 在 .NET 半区验证不到真实等待/通知语义，违背 catalog §5.1 两半区方法论；也省注入/过滤成本。Java 半区需要的 compat 改造与映射见 §5.4，属转换器/兼容层后续 PR。

### 5.3 verify、指纹、保留目录

descriptor 双 verify 默认开；翻转自动纳入全集指纹令牌（`pass:concurrency-normalize=false→true` 一次性失效，接受）；目录 `.cs2j-concurrency-normalize-src` 自动 preserve。

### 5.4 Java 半区消费清单（后续 PR，本 pass 不实施）

1. **类型/成员映射**：TypeMappings 增 `System.Threading.Monitor → compat Monitor`（import `io.github.ningpp.compat.Monitor`）+ 成员映射 `Enter/Exit/Wait/Pulse/PulseAll/TryEnter`（ref `lockTaken` 经现有 holder 体系（BoolHolder）传参，需与 ref 实参转换联测）；
2. **compat Monitor.java 真实化，二选一（M3 评审开放问题）**：
   - 路线 (a) **intrinsic 桥**（v1 推荐）：依赖 CN-2 已消灭结构化 Enter/Exit——`enter/exit` 仅留 null 检查空体（锁由 `synchronized` 持有）；`wait/pulse/pulseAll` 委托 `obj.wait/notify/notifyAll`（CN 持锁校验保证调用点在 synchronized 内，否则 Java 抛 IllegalMonitorStateException 对齐 C# SynchronizationLockException）；timed wait 用 compat 内计时包装返回近似 bool（★§10-1）；**`tryEnter` 所有超时/0ms 重载显式抛 `UnsupportedOperationException`**——禁止保留恒真实现（[Monitor.java L51-L57](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java#L51-L57)）；
   - 路线 (b) **per-object `ReentrantLock` 表**（identity WeakHashMap 同步表或 Striped）：支持 `tryLock(ms)`，但 `lock(o)` 直映的 intrinsic 与该表是两把锁——同一对象混用即互斥失效；只有同时接受「TryEnter 对象禁用 lock」用法约束、或 M3 反转 §2.1 前置 lock 才成立，成本高；
3. **ThreadHelper 不扩 monitor 面**：catalog C29 目标形态提「ThreadHelper/Object.wait/notify」，但既有 monitor 资产与语义归属都在 compat `Monitor.java`，[ThreadHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ThreadHelper.java) 只承载 Thread 实例/静态面（sleep/memoryBarrier/culture）；建议 monitor 族全部留 Monitor compat，ThreadHelper 不新增 wait/notify 包装（开放问题，评审可推翻）；
4. **`System.Threading.Volatile` 静态 API** → VarHandle 形态（`Volatile.Read(ref f)` 的 ref 字段/变量在 Java 侧的 VarHandle 定位）独立映射题，M3+ 另立；
5. **checked exception**：`wait` 抛 checked `InterruptedException`，转换器是否传播 throws 未知（M1 探针盘点）；若不传播，compat 包装为非检查异常并复位 interrupt 标志（仿 [ThreadHelper.sleep L60-L76](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ThreadHelper.java#L60-L76) 模式）；
6. 转换器侧不删任何代码：lock 直映（[L286-L293](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L286-L293)）保留；FieldTransformer 非基元注释待 CS2J2940 诊断上线后再评估退役。

## 6. 诊断策略

新增 **CS2J29xx** 码段（已占段：05xx/11xx/12xx/13xx/18xx/20xx、50-54xx、30-33xx、40xx、90xx；本段不冲突；码位只追加不改号）。MSBuild 格式：`文件(行,列): warning/error CS2J29xx: [CN-x/方法限定名] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J2900 | Error | 整树重编译新增错误且无法归属单一 region plan → 本次运行全部回退 |
| CS2J2901 | Warning | 新增错误归属某方法 plan → 该 plan 原子回退（仿 TS/JK 031） |
| CS2J2902 / 2903 / 2904 | Error | 中间树 restore / build / test 失败 |
| CS2J2909 | Warning | 单文件模式 plan 非自包含（保留位；语句级改写稳态不触发） |
| CS2J2910 | Warning（strict Error） | 手写 Enter/Exit 或 TryEnter 结构非规范（跨方法配对、多出口、条件 Enter、Exit 不在 finally），intrinsic 锁无法表达；区域原样不动 |
| CS2J2911 | Warning（strict Error） | TryEnter（0ms/超时/lockTaken 重载）已规范为显式形，但 Java 半区 intrinsic 无 tryLock、compat 未就绪；**显式语义挂账，禁止接恒真实现** |
| CS2J2920 | Warning（strict Error） | Wait/Pulse/PulseAll 不能词法-保守证明处于同对象持锁区（Java 侧必抛 IllegalMonitorStateException） |
| CS2J2921 | Warning（strict Error） | `Wait(exitContext: true)` 无 Java 对应；站点原样 |
| CS2J2930 | Warning | lock 区域内 await 的防御镜像（基线 CS1996 优先；主诊断归 C18 CS2J1805） |
| CS2J2931 | Info | 锁对象身份审计：this / typeof(T)（泛型擦除身份）/ string（驻留），消息带 kind 与后果 |
| CS2J2940 | Info（M3 可评 Warning） | volatile 非基元字段（转换器注释的诊断化） |
| CS2J2960 | Info | `System.Threading.Volatile` 静态 API 命中（Java VarHandle 缺口登记） |
| CS2J2961 | Info | 范围外同步原语命中（Mutex/SemaphoreSlim/EventWaitHandle/…），只盘点 |
| CS2J2970 | Warning / strict Error | residual 闸门：输出树存在无 plan 归属、无 skip 诊断的 Monitor 绑定站点 |
| CN001 | Info | 区域已改写（Enter/Exit→lock、TryEnter 规范形、Wait 重载归一计数与方法限定名），仅 verbose |
| CN002 | Info | Wait 谓词循环/虚假唤醒与超时 bool 近似说明（逐站点一条），仅 verbose |

原则（catalog §4 风险 12）：无法在 Java 侧等价承接的子项**显式发码**，禁止恒真返回/空体/注释占位流入 Java 产物；Warning 可经 strict 升 Error；所有跳过必留诊断痕迹；残余零未知。

## 7. 测试计划

### 7.1 Core 单测

新增 `tests/CSharpToJava.Tests/ConcurrencyNormalizeTests.cs`（内联源码 → `CSharpCompilation` → Bind/Analyze/RewriteFile → 忽略空白文本断言 + 输出编译 helper，仿 Enum/Generic 预处理单测）：

- **lock**：单层/嵌套 lock、`lock(this)/typeof/string`（2931 分 kind）、锁内 throw、递归同对象——树字节级不动（CN-1），仅审计计数；干净文件/BOM 保持；
- **CN-2**：形 A（Enter+ref taken）、形 B（旧式 Enter）→ `lock` 文本断言 + 临时声明确认删除；taken 在 BODY 被引用、Exit 不在 finally、catch 中释放、跨方法（Enter/Exit 分两方法）、条件 Enter、锁表达式为方法调用 → CS2J2910 且方法配对原子不动；
- **CN-3 TryEnter**：`TryEnter(o)`、`(o,0)`、`(o,50)` 裸 bool 形、`(o,TimeSpan)`、`(o,ref t)` 2 参、`(o,ms,ref t)` 3 参——规范形文本/临时量/else 超时分支保留/-1 与负值；每站点 2911；非规范 finally → 2910 原子不动；
- **CN-4 Wait**：四种重载归一文本；TimeSpan 临时量；`exitContext:false` 常量折叠、`true` 与变量 → 2921；bool 返回值参与三元/赋值；持锁正反例：同 local、同字段（含 this.f）、锁在 if 外（2920）、不同对象（2920）、属性/方法调用锁表达式（2920）；
- **CN-5**：Pulse/PulseAll 持锁/不持锁；
- **CN-7/CN-9**：volatile int（不动）、volatile 引用类型字段（2940，读写点不动）、`Volatile.Read/Write`（2960 计数不改）；
- **CN-10**：含 await 的 lock 样本在基线快照下不抢诊断（CS1996 优先），防御路径 2930 用合成快照构造；
- **CN-11**：SemaphoreSlim/Mutex/ManualResetEventSlim 命中 → 2961 计数、树不动、不进残余；
- **跨文件**：锁对象字段声明与使用分两编译单元，全编译一次 Bind 正常判定；
- residual：构造未归属样本断言 CS2J2970 与 strict 升级；回退通道（可测 hook 注入编译错误）断言 2900 全回退、产物与输入字节一致；幂等（输出再过一遍零差异）。

### 7.2 项目级 sln fixture（双半区并发行为）

新增 `tests/SampleConcurrencySolution/`（Lib + Tests + `.sln`，按 [SampleRecordSolution](file:///d:/code/cs2j/tests/SampleRecordSolution) 范式以 Content 挂入 CSharpToJava.ProjectTests，net10.0）：

- **Lib**：`Counter`（嵌套 lock、递归 lock）、旧式 Enter/Exit 形（CN-2 目标）、`BoundedBuffer<T>`（Wait/Pulse/PulseAll 生产者-消费者 + 超时 Wait bool 分支）、`TimedGate`（TryEnter 超时分支日志器，覆盖 0ms/int/TimeSpan/lockTaken 形）、volatile 停机标志循环、锁内 throw 释放用例、范围外对照（SemaphoreSlim/EventWaitHandle，注释标明仅审计）；
- **Tests（真实多线程，.NET 半区）**：
  - N 生产者 × M 消费者：全部元素恰好消费一次、顺序约束、看门狗超时防死锁；
  - TryEnter 超时分支**实际可达**：一线程持锁 sleep，二线程断言走 fallback 分支；
  - 锁内 throw 后另一线程在限定时间内重新进入成功（释放语义）；
  - volatile 停机标志：自旋工作线程在置位后限定时间内退出；
  - 递归锁计数正确（同线程重入不死锁）；
- **断言**：`result.Success`、统计对账；中间树不含手写 Enter/Exit 对（除 2910 对照）、TryEnter 全为 3 参规范形；`dotnet build -c Release` 退出码 0、`dotnet test -c Release` 全绿；
- **双半区并发行为对照**：M2 先闭 .NET 半区环；Java 半区（转换器 + §5.4 路线 (a)）就绪后，同一 fixture 转换产物跑同一组并发用例，对照项固定为五项：producer-consumer、timeout 分支（路线 (a) 下为显式 UnsupportedOperationException 的*预期*行为，用例据此分流）、异常释放、递归重入、volatile 停机。

### 7.3 Release build/test 与 sdk 基线

fixture 与 `D:\csharp-sdk`（只读输入）预处理树分别 `dotnet build -c Release` / `dotnet test -c Release`；sdk 基线 = pre-existing 失败集快照，新增失败为零。M1 先跑测量轮（见 §9）。

### 7.4 residual 闸门

fixture 与合成样本断言：输出树 Monitor 绑定站点数 = active 改写站点 + 有诊断 skip 站点；2910/2921/2930 站点计入白名单；strict 下 CS2J2970 非零即非零退出。**闸门零未知 Monitor 站点**。

### 7.5 CLI / 注册表测试

- 翻转前：`--enable-pass concurrency-normalize` → CS2J9001 退出 1（经 [PreprocessVerbCLITests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/PreprocessVerbCLITests.cs) 同类用例锁现状）；翻转后：`--list-passes` Implemented、目录 `.cs2j-concurrency-normalize-src`、默认关时无目录无耗时；启用后产物串给 java-keywords 与主转换；`--disable-pass` 可关；
- 注册表：拓扑顺序断言（eliminate-goto/member-desugar 之后、java-keywords 之前，在 [PassChainOrderTests.cs L18-L40](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L40) LockedChain 增列；[PassCatalog.cs L255](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L255) 尾锚符号**不随翻转更新**，保持 `PassStubCatalog.Ids.ConcurrencyNormalize`）、`catalogItems=="C29"`、无重复 id/目录；[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) stub 断言同步移除（该校验动态取 stub 集，无硬编码计数）；
- 指纹令牌含 `pass:concurrency-normalize=...`。

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足（门槛全文保留，但本 pass 建议长期不翻，见下）：

1. **中间树正确性**：fixture sln 与 sdk 预处理树 `dotnet build -c Release` 零错误、`dotnet test -c Release` 全绿；§7.2 五项真实多线程行为用例全绿；sdk 新增失败为零；
2. **残余闸门**：除显式 2910/2921/2930 登记站点外零未知 Monitor 站点，skip 分类全部有主，strict 可过；
3. **全仓测试不回归**：`CSharpToJavaConverter.slnx` Release build + 全部测试工程 Release test 既有红灯不增加；
4. **Java 半区就绪**：§5.4 映射与 compat 路线 (a) 落地，代表性样本（lock 嵌套、Wait/Pulse 缓冲区、锁内 throw、递归）经完整链路产出可编译且并发测试全绿的 Java；timed TryEnter 有明确行为契约（显式不支持或路线 (b)）；
5. **频率证据**：M1/M2 sdk 测量显示手写 Monitor 命中率高到「默认链付出一环」有正收益；
6. **文档**：本设计 + plan（`docs/superpowers/plans/2026-09-29-c29-concurrency-normalize.md`）定稿，审计报告回填。

**长期 opt-in 论证**：第 4、5 条在可见未来不成立——timed TryEnter 在 Java 无无代价等价物（§10-2），lock/volatile 主路径零缺口，手写 Monitor 低频；观察项的使命是「让静默缺口可被显式发现」，opt-in + 诊断面已经达成。默认开会制造 catalog §4 风险 10 所警的「多套事实标准」而无对应收益。需要时用户显式 `--enable-pass` 即可，不翻默认。

## 9. 里程碑

| 里程碑 | 内容 | 验证 |
|---|---|---|
| **M1 审计盘点**（不改产品码） | ① sdk/`D:\csharp-sdk` 频率扫描：lock/volatile 关键字、Monitor 全重载（Enter 两形、TryEnter 四形、Wait 五形、Pulse/All）、Volatile 静态 API、§2.7 范围外原语分布；②探针锁定无映射下 `Monitor.*` 的**实际 Java 发射文本与 import 行为**（验证 §1.1 推断）；③确认 compat `Monitor.java` 死壳无引用；④checked exception/throws 传播盘点；⑤Interlocked 双映射实际命中路径；⑥审计报告回填本设计，修正 catalog「Volatile 已吸收」叙述 | 审计文档 + M2 范围确认；不接线 |
| **M2 显式化 + 翻转（默认关）** | Core 骨架（options/statistics/diagnostics/models/入口）+ Analyzer + CN-1~CN-12（FrontLoad 两选项不接）+ LockRewriter/VolatileRewriter(审计) + ResidualScanner + 适配器 + stub→Implemented 翻转（**常量留 `PassStubCatalog.Ids`、L255 尾锚符号不换**，校订 3；LockedChain 增列锁 member-desugar 相对次序）+ Core 单测 + SampleConcurrencySolution（.NET 半区五项并发行为全绿）+ 注册表/CLI 测试 + sdk 基线比对 | `--list-passes` 可见；fixture Release build/test 双绿；干净树零差异；2910/2911/2920 分类齐 |
| **M3 Java 半区与评审项（不承诺排期）** | §5.4 路线 (a)（Monitor 类型/成员映射 + compat 真实化 + timed TryEnter 显式不支持）；双半区并发行为对照；路线 (b) 与 FrontLoadLock/FrontLoadVolatile 评审（默认维持 false）；`Volatile` 静态 API → VarHandle 立项；按 §8 数据决定是否评默认开（建议维持 opt-in） | 双半区对照报告；默认开评审（预期不翻） |

## 10. 风险

1. **★ wait/notify 等价性**：①timed Wait 的 bool 返回在 Java `wait(long)`（void）侧只能 compat 计时近似（notify 与超时边界竞态可能误判 bool）；②虚假唤醒两边都有，依赖用户谓词循环——CN 不改循环，若用户原代码无谓词循环，转换后同样脆弱（CN002 提示但不修复）；③`notify` 唤醒选择由 JVM 实现定义，与 CLR 调度差异可能暴露调度依赖；④中断模型不同（checked 异常/复位语义）。缓解：只做结构归一不包装自定义语义、持锁校验保证 wait 调用前置、fixture 真实多线程守门、compat 方法 Javadoc 明确近似边界。
2. **★ TryEnter 无 intrinsic 解**：路线 (b) 的 per-object 锁表与 synchronized 是两套 monitor，同对象混用互斥失效且静态不可全查。缓解：v1 仅 C# 侧显式化 + CS2J2911 挂账，Java 侧路线 (a) 显式 `UnsupportedOperationException`，**禁止恒真死壳回流**；路线 (b) 只在用法约束可强制时才启。
3. **锁对象身份**：`lock(this)` 外部可争用、`lock(typeof(T))` 经 [TypeOf L126-L129](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L126-L129) 发 `T.class` 在泛型擦除后闭型共享、string 驻留边界跨语言不同。缓解：2931 只审计不自动改（自动改要生成字段、动成员面，超出问题半径）。
4. **CN-2 误判手写模式**：把依赖特殊异常/中止语义的手写 Enter/Exit 误当编译器形会错放锁。缓解：识别条件从严（§2.2），不匹配即 2910 原样；fixture 异常路径专测；plan 级原子。
5. **持锁判定假阳**：保守词法分析会把实际持锁站点报 2920（如锁经辅助方法获取）。缓解：非 strict 仅 Warning 且不改代码，不破坏构建；分析精度随 sdk triage 迭代，宁假阳不漏放。
6. **volatile 内存模型差异**：允许类型集上 fence 语义一致，Java 对 long/double 更强不产生错值；引用类型赋值两边原子。缓解：v1 不重写，仅审计；M3 不轻易前置。
7. **exitContext/上下文封送**：ContextBoundObject/Remoting 场景无 Java 对应。缓解：2921 显式。
8. **中间树成本**（catalog §4 风险 11）：新增一环一次复制/reparse/restore；默认关 = 零成本；启用时指纹/preserve 已覆盖。
9. **opt-in 长期化**（风险 10）：见 §8 论证；直映主路径不动，默认链事实标准唯一。
10. **热文件冲突**：注册表四文件/测试多 pass 共改，见 §11。

## 11. 并行协调

| 并行条目 | 关系与边界 | 协调动作 |
|---|---|---|
| **C09** thread-static | 同族不同面：C09 改字段存储声明+读写点，C29 改语句/调用；C09 链位更早（RunAfter generic），C29 看到的已是 `ThreadLocal<T>.Value` 形态——`lock(x.Value)` 对 CN 只是普通锁表达式（值为 object 即可），无特判 | 不合并（§2.9）；Core 目录不重叠；若未来建并发族共享 Analyzer，以「字段状态源 + 区域源」两类接入，不提前抽象 |
| **C18** async-normalize | `await in lock` 主诊断归 C18 CS2J1805（[D10](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L71)）；C18 链位在 C29 之前，C29 消费其产物 | C29 CS2J2930 仅基线优先的防御镜像，不重复报错；async 方法内 Monitor 用法由 CN 持锁校验照常工作 |
| **C04** iterator-lowering | 既有规则：yield 在 lock 内 → CS2J0402 skip（[IteratorLoweringAnalyzer.cs L196-L205](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringAnalyzer.cs#L196-L205)） | C29 不抢该诊断；C04 在链更早，C29 看到的 lock 体已不含被 skip 迭代器的歧义形态；热文件不重叠 |
| **C02** generic-deepen | `lock(typeof(T<…>))` 身份审计（2931）与泛型擦除主题相邻 | 无代码耦合；2931 文案引用擦除事实即可 |
| **C08/E17** JavaKeywords | 链尾，C29 之后；合成名 `__cs2j_mt/__cs2j_to` 为合法 Java 标识符 | 原名撞 Java 保留字由 C08 统一逃逸；无冲突 |
| **compat 桥接面归属** | [Monitor.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java) 真实化、TypeMappings Monitor 条目、ThreadHelper 是否扩面（建议否）、Volatile→VarHandle | 全部归「C29 Java 半区后续 PR」，与预处理 PR 分离；M3 评审路线 (a)/(b)；不动 Interlocked 既有面 |
| 公共热文件 | [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)（L42 常量、L247-L258 stub 条目）、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)（**L251-L257 尾锚块，L255 锚点翻转不改符号**；Implemented 表插描述符）、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、[PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)、[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) | 各 worktree 仅追加/替换各自 id 的行；合并顺序任意，按 rebase 解冲突；禁止顺手重排既有 pass 顺序 |

## 12. Worktree 分配

- 分支：`feature/preproc-c29-concurrency-normalize`
- 目录：`.worktrees/preproc-c29-concurrency-normalize`
- base：`dcda0151`（dev-3.x-26100311 HEAD，2026-10-03 更新；worktree 已按此 base 直接创建，无需建树后 rebase，附录⑧原「base `953293b9` + 建树后 rebase」流程由本次直接建树替代）
- 创建命令（已执行）：

```powershell
git worktree add .worktrees/preproc-c29-concurrency-normalize -b feature/preproc-c29-concurrency-normalize dcda0151
```

实施约束：仅在上述 worktree 内改码；按 §9 里程碑逐提交；M2 翻转即 Implemented + 默认关；FrontLoadLock/FrontLoadVolatile 不接实现；转换器与 compat 改动（§5.4）留后续 PR；默认开评审前必须先有 M1/M3 频率与 Java 半区证据。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令原定 base `953293b9` + 建树后 rebase；2026-10-03 已改为直接以 dev-3.x-26100311 HEAD `dcda0151` 建树（§12），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（散见决策表/里程碑门的悬置项，按主题收口）：**

1. **Java 半区 monitor 路线**：定 **路线 (a) intrinsic 桥**——`Monitor.Enter/Exit` 桥的 enter/exit 空体由 Java `synchronized` 块持锁保证（桥方法只做签名承接），`Wait/Pulse/PulseAll` 委托到 `Object.wait/notify/notifyAll`；否决路线 (b) 全量 ReentrantLock 表锁（v1 分配/侵入面过大）。
2. **timed `TryEnter` 终态（拍死，不接恒真壳）**：v1 **维持 CS2J2911 显式门**，禁止生成"恒返回 true"的桩、禁止伪造超时语义；**M3 方案 = 对使用 TryEnter 的锁对象整体改 ReentrantLock 代码生成**——CN 按类型/数据流识别同一锁对象上的 `lock`/`TryEnter` 混用：仅 TryEnter 出现的对象走表锁代码生成；**同一对象上 intrinsic（lock）与表锁（TryEnter）混用无法统一的，发 CS2J2911 硬错并指路**（要求用户手工拆分锁对象），不做静默二选一。
3. **ThreadHelper 边界（确认）**：**ThreadHelper 不扩 monitor 面**——monitor 语义全部落 [Monitor.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Monitor.java) compat 桥与 TypeMappings 条目，ThreadHelper 维持线程构造面单一职责。
4. **Volatile 静态 API**：`Volatile.Read/Write` 等静态 API → Java `VarHandle` 的 compat 桥**排 M3+ 立项**（明确排期、登记终态去向，不无限挂起）；M3 前对应站点显式诊断计数，画像零命中可在两个画像周期后书面降级。
5. **前置事实**：M2 翻转即 Implemented+默认关；默认开评审前必须先有 M1/M3 频率画像与 Java 半区桥证据，顺序不抢跑；FrontLoadLock/FrontLoadVolatile 永不接实现（只计数）。
