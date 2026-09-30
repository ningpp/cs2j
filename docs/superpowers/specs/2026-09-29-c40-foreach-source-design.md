# C40 foreach 迭代源二期：枚举器适配 / ref foreach 设计

**日期**: 2026-09-29
**范围**: 在 C16 已创建的 `foreach-source` pass（Core [ForeachSource/](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachSource/)）内增量增加两个规则目录 `Rules/EnumeratorAdapter/`（FA-x）与 `Rules/RefForeach/`（FR-x）：① 模式化 foreach（duck-typed `GetEnumerator`、直接枚举器变量）lower 为编译器形态 `GetEnumerator()` + `while(MoveNext())` + `Current`（按 `IDisposable`/pattern Dispose 决定 finally）；② `foreach (ref var x in …)` / `ref readonly` lower 为索引循环 + ref 局部。**不新增 pass、不翻转开关、不改注册表结构、不碰 C#→Java 转换器**。
**状态**: 设计稿，待评审；实施硬前置 = C16 锚点分支合入（见 §3、§12）
**族属**: catalog P2「控制流去糖族」（§3.3「同一底层难点」的合并建议 [preprocessor-catalog.md:1076-1081](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1076-L1081)，控制流去糖族表行 [L1081](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1081-L1081)）。注册表 stub `foreach-source`（[PassStubCatalog.cs:90-99](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L90-L99)，`catalogItems = "C16, C40"`、`RunAfter = foreach-deconstruction`、`Stage = ControlFlow`）。**C16 是锚点**（设计 [2026-09-29-c16-foreach-complete-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c16-foreach-complete-design.md)）：锚点翻转为 Implemented 时 `catalogItems` 收窄为 `"C16"`，本任务合入时追加回 `"C16, C40"`。

> 现状说明（2026-09-29 只读复核）：当前工作区 HEAD（`5e3e796d`）中 C16 设计文档已落盘但 `ForeachSource/` 与适配器尚未创建（C16 在独立分支实施），`foreach-source` 仍为 Stub。本设计所有「C16 已有」表述均以 C16 设计文档 §4 为契约基线。

---

## 1. 背景与目标

### 1.1 转换器自认需要前置、但至今没有通道的一类源

[StatementTransformer.Loops.cs:99-103](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L99-L103) 注释自述 foreach 的契约：

> Strings, dictionaries, LINQ streams, **enumerator adapters** and deconstructions must be lowered by a preprocessor.

strings/dictionaries/LINQ/解构已分别由 C16/E04/E12/E16/E07 覆盖，唯独「enumerator adapters」无任何通道。实际行为分三种：

1. **模式化 foreach 静默产不可编译 Java**：不实现 `IEnumerable` 但有可访问 `GetEnumerator()`（实例或扩展方法）返回带 `MoveNext()`/`Current` 类型的源，经 [Loops.cs:104-155](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L104-L155) 直译为 `for (T x : expr)`；[L123-L127](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L123-L127) 只有数组/null 判断，非 Iterable 源不报错、不占位，产出无法编译的 Java。普通（非 ref）foreach over `Span<T>` 同病：`Span<T>` 不实现 `IEnumerable`，compat [Span.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Span.java) 也不实现 Iterable。
2. **ref foreach 静默错值（本族最高危）**：[Loops.cs:104-L155](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L104-L155) 全函数无 `RefKeyword` 判定。`foreach (ref var x in span) { x = …; }` 被按值 foreach 处理：Java 侧 `for (T x : span)`（经 compat `get`），循环体内对 `x` 的赋值不回写 Span 元素——**典型 catalog §4 风险 12「静默错误输出」**（[L1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)）。
3. **直接枚举器变量 foreach**：`foreach (var x in enumerator)`（源静态类型即 `IEnumerator`/`IEnumerator<T>` 或鸭子枚举器）同样直译为 enhanced-for，Java 侧 `Iterator` 接口不是 Iterable，不可编译。

旁证：转换器把 `GetEnumerator` 方法名映射为 `iterator`（[ExpressionTransformerFacade.cs:488](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L488-L488)、[MethodTransformer.cs:181](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L181-L181)），说明现网只有「集合自身 iterator()」单一路径，无枚举器驱动形态。

### 1.2 foreach 四族划分与本任务位置

| 族 | 归属 pass | 形态 |
|---|---|---|
| 同步源·数组/泛型集合/string | 转换器直连 / **C16**（string） | enhanced-for、`ToCharArray()` |
| 同步源·解构（KV/Tuple/Deconstruct） | E07/E06，残余归 **C16** | 普通 foreach + 解包 |
| 同步源·**枚举器适配 / 索引模式 / ref** | **C40（本任务）** | while+枚举器；索引循环+ref 局部 |
| 异步源 `await foreach` / `await using` | **C38**（`async-normalize` pass，见 §3.3） | 不碰 |

### 1.3 目标 lowering 形态

**① 枚举器适配（FA）**，编译器 foreach lowering 同构（[Loops.cs 注释](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L99-L103) 已点名）：

```csharp
// before
foreach (var item in src) { body }
// after（DisposeMethod 非空时；__cs2jFaSrc 保证源只求值一次）
var __cs2jFaSrc = src;
var __cs2jFaE = __cs2jFaSrc.GetEnumerator();      // 扩展方法形态语法同形
try
{
    while (__cs2jFaE.MoveNext())
    {
        var item = (TElement)__cs2jFaE.Current;  // 仅在需要转换时插强转
        body
    }
}
finally
{
    __cs2jFaE.Dispose();                          // pattern Dispose / IDisposable 才生成
}
```

枚举器不需要 `IDisposable`（鸭子枚举器常态）时**无 try/finally**；值类型枚举器（`List<T>.Enumerator`、`Span<T>.Enumerator`）用 `var` 保持具体类型，调用不装箱（§2 决策 5/6）。

**② ref / ref readonly foreach（FR）**，索引模式（数组、`Span<T>`、自定义 Length+索引器 ref struct）：

```csharp
// before
foreach (ref var item in span) { body }
// after
var __cs2jFrSrc = span;
for (int __cs2jFrI = 0; __cs2jFrI < __cs2jFrSrc.Length; __cs2jFrI++)
{
    ref TElement item = ref __cs2jFrSrc[__cs2jFrI];
    body
}
```

`ref readonly` 产 `ref readonly TElement item = ref …`。枚举器 `Current` 按引用返回（`ref T Current`）的形态走枚举器骨架 + `ref T item = ref e.Current;`（FR-3）。

### 1.4 目标

1. 非 array/非 IEnumerable 的模式化源、直接枚举器变量源在 C# 侧 100% lower 为 while 骨架，中间树可编译、运行时与 C# foreach 语义逐项等价（求值一次、null 时序、装箱口径、Dispose 时序）。
2. 消灭 ref foreach 静默按值：ref/ref readonly 一律索引/ref-Current lower，使对元素的写改在 .NET 与 Java 两半区均可见；不可静态 lower 的站点显式诊断。
3. 严格复用 C16 契约（`IForeachSourceRule`/`ForeachSourceSite`/有序规则注册/fixture/CS2J54xx），C40 只新增自有规则文件、追加注册行与码位。
4. 产物可编译可测试；不引入支撑库；residual 闸门给出「foreach 迭代源已全部归一」的可判定口径。

**明确不做**：`await foreach`（C38）、string（C16 FS-1）、解构（E06/E07/C16 FS-2/3）、数组**值**foreach（转换器直连）、转换器任何改动、任何 C#→Java 侧 compat 新增（复用既有 [Span.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Span.java) `get/set/length`，见决策 9）。

### 1.5 硬不变式

1. 输出用户代码中：不存在「源静态类型非 array、非 string、未实现 `IEnumerable`/`IEnumerable<T>`，且语义模型给出可用 foreach 绑定」的裸普通 foreach；不存在任何带 `RefKeyword` 的裸 foreach（residual 按 SemanticModel 判定，不靠文本 grep）。
2. 不触碰任何 `AwaitKeyword` 节点（C38 面，沿用 C16 CS2J5442 计数）；不触碰 `ForEachVariableStatementSyntax`（解构面归 E07/C16）。
3. 一切分类来自 SemanticModel：`GetForEachStatementInfo` + 操作/局部符号（§4.2）；无模型、error type、dynamic 分发节点不强行改写（dynamic 走显式诊断或透传，见决策 11）。
4. Dispose 纪律：仅当绑定给出 Dispose 方法（IDisposable 或 pattern Dispose）时生成 finally；**非 IDisposable 且无 pattern Dispose 的枚举器绝不生成 Dispose 调用**。
5. 枚举器局部一律 `var`（具体类型），禁止把值类型枚举器强转/声明为 `IEnumerator` 造成提前装箱（决策 5）。
6. 源表达式严格只求值一次（统一 `__cs2jFaSrc`/`__cs2jFrSrc` 缓存）；索引循环中源被多次引用（Length + 每次索引），必须走缓存。
7. 幂等：改写点不再含 foreach 节点，二次执行零 diff；trivia/BOM/干净文件复制纪律同 C16。
8. 禁止注释/占位/原样透传流入 Java 产物（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110-L1110)）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | duck-typing 判定 API | **以 SemanticModel 的 foreach 绑定信息为唯一事实源**：主用 `SemanticModel.GetForEachStatementInfo(node)`（Roslyn 4.12，仓库已引 [Microsoft.CodeAnalysis.CSharp 4.12.0](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12-L12)；成员 `GetEnumeratorMethod/MoveNextMethod/CurrentProperty/DisposeMethod/ElementType/ElementConversion/CurrentConversion/IsAsynchronous/IsDynamicallyDispatched` 已经 XML 元数据复核），辅以 `GetOperation(node) as IForEachLoopOperation`（同 DLL 元数据含 `get_IsRef` 与全套枚举器符号）；ref/ref readonly 的权威判据用 `GetDeclaredSymbol(node) : ILocalSymbol` 的 `RefKind`（`Ref`/`RefReadOnly`/`None`），语法侧 `node.RefKeyword.IsKind(SyntaxKind.RefKeyword)` 仅作快速筛选 | 同 API 已在仓库三处消费：[ForeachDeconstructionRewriter.cs:46](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L46-L46)、[TupleToStructPreprocessor.cs:1313](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L1313-L1313)、[IteratorLoweringAnalyzer.cs:268](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringAnalyzer.cs#L268-L268)。**不自行模拟 C# 候选归约**（自己找 GetEnumerator/MoveNext 极易与编译器分歧）；`IForEachLoopOperation.IsRef` 4.12 可用性由 M1 探针钉死，`ILocalSymbol.RefKind` 为零成本兜底（开放问题 5）。4.12 **不存在** `GetForEachSymbols`/`ForEachSymbols`（已在包 DLL 字符串中复核为 -1），不得引用该高版本 API |
| 2 | 实例 vs 扩展 GetEnumerator | 不区分：绑定给出的 `GetEnumeratorMethod` 即编译器选中的符号（实例优先，扩展次之）；改写**只生成语法调用** `__cs2jFaSrc.GetEnumerator()`，不内联扩展静态名，重新绑定交给 C# 编译器 | 同文件 using/static using 全部保留，扩展归约结果不变；await foreach 的 `GetAsyncEnumerator` 扩展形态同理归 C38（Learn 迭代语句文档明确扩展成员可作 GetEnumerator/GetAsyncEnumerator）。`IMethodSymbol.IsExtensionMethod` 仅用于统计与诊断文案 |
| 3 | 何时判定「需要 FA」（不与转换器抢源） | 同时满足：普通 `ForEachStatementSyntax`、无 ref/await、非 string（C16 面）、源静态类型**非 array**、且其类型/基类/接口**不实现** `System.Collections.IEnumerable` 与 `IEnumerable<T>`；绑定给出枚举器（`GetEnumeratorMethod != null`，或直接枚举器模式 `MoveNextMethod != null && GetEnumeratorMethod == null`，决策 4） | `List<T>` 等虽以模式绑定公共 `GetEnumerator()`，但实现了 IEnumerable → 转换器 + compat Iterable 路径已覆盖，**不重写**（避免海量改动与 struct Enumerator 类型名流入 Java）。数组值 foreach、非泛型集合 cast 路径有回归锁定（[ArrayForeachNotCollectedTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ArrayForeachNotCollectedTests.cs)、[ForeachNonGenericCollectionCastTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachNonGenericCollectionCastTests.cs)），零误伤夹具必测 |
| 4 | 直接枚举器变量源 | `IEnumerator`/`IEnumerator<T>` 或任意「自身有 MoveNext+Current」类型的变量直接作 foreach 源（C# 允许集合即枚举器模式）：lower 为 `var __cs2jFaE = __cs2jFaSrc;`（一次复制；class 为引用复制、struct 为值复制，均与编译器一致），后续 MoveNext/Current/Dispose 骨架相同 | 此时 `GetEnumeratorMethod` 为 null、`MoveNextMethod/CurrentProperty` 非空；不得凭空再调一次 `GetEnumerator()`。非泛型 `IEnumerator.Current` 为 `object`，值类型元素本就有装箱——这是 C# 既有语义，保留（决策 7） |
| 5 | ★值类型枚举器不装箱 | 枚举器局部统一 `var __cs2jFaE = …`，类型 = `GetEnumeratorMethod.ReturnType`（直接枚举器模式 = 源类型）；`MoveNext()`/`Current`/pattern `Dispose()` 均在具体类型上非虚调用 | 经典陷阱：`List<T>.Enumerator` 是 struct，若写成 `IEnumerator e = src.GetEnumerator()` 会在循环前装箱，此后 MoveNext/Current 走盒子副本。FA-1 正例探针必须断言文本为 `var __cs2jFaE` 且**不含** `IEnumerator` 声明；运行时用「枚举器推进时修改内部状态」的自定义 struct 枚举器对照（装箱副本会导致死循环/错值） |
| 6 | 非 IDisposable 不 Dispose；pattern Dispose 要 Dispose | 以 `info.DisposeMethod` 是否为 null 二分：**null → 完全不生成 try/finally**（duck 枚举器不要求 IDisposable，C# 语义要点）；非 null → finally 单次调用。调用形态再二分：(a) 方法在枚举器类型上可直接访问（pattern `public void Dispose()`，含值类型，**不装箱**）→ `__cs2jFaE.Dispose()`；(b) 仅显式接口实现且枚举器为值类型 → `((IDisposable)__cs2jFaE).Dispose()`（会装箱，CS2J5455 计数，★风险 1） | Roslyn `LocalRewriter.TryGetDisposeMethod` 规则：先 pattern Dispose 后 IDisposable；ref struct 不装箱只认 pattern。内部 `ForEachEnumeratorInfo.PatternDisposeInfo/NeedsDisposal`（Roslyn main 源码核实）证明「非 IDisposable 但有可访问无参 Dispose()」也会被调用——不可简化为「只认接口」。引用类型枚举器两种形态均无装箱成本 |
| 7 | 泛型/非泛型 Current 与元素类型 | 元素局部类型取 foreach 局部符号的实际类型（`var` 时 = `info.ElementType`，显式标注时 = 标注类型）；仅当 `CurrentProperty.Type` 与元素类型不同（非泛型 `Current` 为 object、用户显式收窄等，编译器在此处插 `CurrentConversion`，可含显式转换）时生成强转 `(TElement)__cs2jFaE.Current`；类型名用 `SymbolDisplayFormat.FullyQualifiedFormat` 渲染，避免 using 缺失 | 无转换时不加强转（可读性 + 避免给值类型加无意义引用转换）；强转同时复现 user-defined conversion 选择。`var item = e.Current;` 的偷懒写法在 object Current + `foreach(int x)` 场景会让体内 `x` 类型错误，必须按本决策 |
| 8 | ref foreach → 索引循环 | `IsRef` 且为索引模式（array；或绑定无 GetEnumerator 符号、类型具 `Length`/`Count` 属性或字段 + 单参 int 索引器）：`for (int i = 0; i < src.Length; i++) { ref T x = ref src[i]; … }`；无 `Length` 则取 `Count`；ref readonly 产 `ref readonly T x = ref src[i]` | C# 对 `Span<T>`/数组的 ref foreach 即此 lowering（不经过值语义 `Span<T>.Enumerator.Current`，该属性按值返回无法支持写回）。Learn 迭代语句文档：枚举器 `Current` 为 `ref T` 引用返回时亦可声明 ref/ref readonly 迭代变量——对应 FR-3，不与本索引形态混淆 |
| 9 | 普通 Span/ReadOnlySpan foreach 也走索引形态 | 源静态类型为 `System.Span<T>`/`System.ReadOnlySpan<T>` 时，**无论是否 ref** 一律索引循环（非 ref 时元素局部不加 ref） | 类型映射已有（[TypeMappings.json:396-427](file:///d:/code/cs2j/config/TypeMappings.json#L396-L427)），compat [Span.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Span.java) 提供 `get(int)`/`set(int,T)`/`length()`/`getLength()` 但**没有** `Enumerator` 嵌套类型、也不实现 Iterable：枚举器形态的 Java 半区无落点；索引形态直译为 `for (int i=0; i<span.length(); i++){ T x = span.get(i); }`，零 compat 新增。`ref` 产物 `ref T x = ref span[i]` 交 C01（§11） |
| 10 | break/continue/标签 | break/continue **不合成标签**：骨架为 `try { while(…) { body } } finally {…}`，break/continue 最近循环即合成 while——break 出循环后 finally 执行一次，continue 回 `MoveNext()` 且不触发 finally（Java/C# 同构，转换器 [SwitchAndResource.cs:96-100](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L96-L100) 直译为 `break;`/`continue;`，[Loops.cs:12](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L12-L12) 直转 while，try/finally 直转）；body 原样搬迁进 Block，体内标签语句保留 | goto 跨进 foreach body 在 C# 受变量作用域规则限制；eliminate-goto（E14）在链尾运行，只吃我们搬迁后仍合法的标签树。fixture 覆盖 body 非块、嵌套循环、带标签 continue/break、try 内 continue（验证不 Dispose） |
| 11 | dynamic / 无法解析源 | `info.IsDynamicallyDispatched == true`（dynamic 上的运行时枚举）：无法静态 lower → CS2J5456（默认 Warning、strict Error），节点保留（动态归一总域在 C25 `dynamic-desugar`）；绑定符号残缺（error type、模型缺失）按 C16 纪律透传计数；「源非 IEnumerable 但编译器可编译的模式绑定」理论必能取到符号，取不到 = CS2J5457 | 不猜测、不文本降级；dynamic 在 C25 落地前继续按现状流动，但站点首次被显式计数，不再「无信号」 |
| 12 | 迭代变量被 lambda/匿名方法捕获 | 不做特殊提升：lower 后 `item` 是 while/for 体内的普通局部，转换器对普通局部已有单元素 holder 路径（[StatementTransformer.Declarations.cs:64-78](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L64-L78)，每轮迭代随块重建，等价 foreach 每轮新变量语义）；foreach 节点特有的 holder 分支（[Loops.cs:142-151](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L142-L151)）不再触发，功能由普通局部路径承接 | 必须有专项 fixture：体内 `Action a = () => use(item); list.Add(a);` 收集的委托各自见到本轮值。C32/closure-lift 未来统一捕获分析时以本形态为准（§11） |
| 13 | Current 抛异常 / MoveNext 抛异常语义 | 不处理、不吞：异常按调用点自然抛出（MoveNext 在条件位、Current 在每轮首语句，顺序与编译器 lowering 一致）；Current 每轮**只取一次**存入局部（禁止体内隐式二次求值 e.Current） | 自定义枚举器可在 Current 抛 `InvalidOperationException`；fixture 用探针验证「MoveNext=true 后取 Current 恰好一次、异常原样冒泡、Dispose 仍执行」 |
| 14 | ref 生命周期 | 索引循环 + ref 局部中，`__cs2jFrSrc` 为普通局部（值拷贝一个 Span/数组引用），`ref item` 指向其元素——ref 安全由 C# 编译器在中间树校验；不引入 `scoped`、不转义 ref。ref struct 集合自身不可被闭包捕获（CS8175），FR 体内若捕获 item 到 lambda，C# 编译即失败，错误由 verify build 兜底并以 CS2J5486 给出预处理侧定位 | 与 C01 的衔接（ref 局部→ByRef 代理）是本族最大外部依赖，见 §10 风险 3、§11 |

---

## 3. 链位与族契约

### 3.1 链位（不新增 pass、不改顺序）

C16 合入后链序（C16 设计 §3.2）：`…, tuple, foreach-deconstruction, foreach-source, pattern-desugar, strip-default, ref-out-in, unsigned, generic, …, eliminate-goto, java-keywords`。C40 规则作为 `foreach-source` 内部有序规则执行，**链位零变化**：

- 早于 strip-default / ref-out-in（C01）/ unsigned / generic：FR 产出的 `ref T item = ref src[i]` 由后续 C01 统一改写为 ByRef 形态——C40 **不自己消 ref**。
- 早于 iterator-lowering（C04）/ eliminate-goto（E14）/ java-keywords（C08）：合成 while/try/for 参与后续状态机、goto、改名处理。
- RunAfter 锚边、Stage、中间目录 `.cs2j-foreach-source-src`、双 verify、无支撑库——全部沿用 C16 描述符。

### 3.2 规则目录与有序注册

C16 设计 §4.1 预留的两个目录在本任务落地：

```
src\CSharpToJava.Core\ForeachSource\Rules\
  EnumeratorAdapter\
    EnumeratorAdapterRule.cs        # FA-1/2/3/4 分派（含直接枚举器模式）
    SpanIndexLoopRule.cs            # FA-5（Span/ReadOnlySpan 普通 foreach → 索引循环）
    ForeachBindingResolver.cs       # 见 §4.2（可放根层供两族共享）
    EnumeratorLoweringBuilder.cs    # 源缓存/枚举器局部/while/try-finally/强转/Current 语句合成
  RefForeach\
    RefIndexLoopRule.cs             # FR-1/FR-2（数组、Span、自定义 Length+ref 索引器源）
    RefCurrentEnumeratorRule.cs     # FR-3（枚举器 Current 引用返回：ref T/ref readonly T Current）
```

注册顺序（在 C16 既有有序列表末尾追加，见其 §4.2「EnumeratorAdapter 注册在 TupleResidual 之后，RefForeach 最后」）：

```
<StringSourceRule>, <TupleResidualRule>, <OtherResidualRule>,   // C16
<EnumeratorAdapterRule>, <SpanIndexLoopRule>,                   // C40 FA
<RefIndexLoopRule>, <RefCurrentEnumeratorRule>                  // C40 FR（最后；先内部判 RefKind）
```

规则互斥（首个命中终止）；ref/await 守卫仍在 rewriter 前置（C16 FS-4），FR 规则只在 `RefKind != None` 时被询问。

### 3.3 码段细分与 C38 边界

C16 预留 CS2J5450-5479（EnumeratorAdapter）/ CS2J5480-5499（RefForeach），细分见 §6。与 C38 的边界：

- `await foreach` 节点 C40 零接触（C16 CS2J5442 计数面不变）；C38 未来在 **`async-normalize` pass** 内实现（[C18 设计 §1/§11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md) 明确「C38 落地时在同一 pass 上扩宽回 C18, C38」）。
- **C38 独立设计文档当前不存在**（specs 目录 2026-09-29 批次只有 `c18-async-normalize-design.md`；已复核文件列表）：本设计按「同步源归 foreach-source、异步源归 async-normalize、同插入点、不共享码段」的目录划分假设立约；C38 设计启动时须双向确认 await 分支与 `GetAsyncEnumerator`/`MoveNextAsync`/`IAsyncAsyncDisposable` 不进 CS2J54xx（应在 async-normalize 自有码段）。

---

## 4. Core 组件设计

### 4.1 对 C16 契约的复用与扩展

- **复用不改行为**：`IForeachSourceRule.TryHandle(ForeachSourceSite, ForeachSourceRewriteContext)` 三态（不匹配/改写/拒绝诊断）、`ForeachSourceSite`（Node/Model/Options/ExpressionType/ElementType/IsRef/IsAwait）、有序规则 rewriter、`ForeachSourcePreprocessor.Preprocess(...)→Result` 外形、`AdhocWorkspace`+`Formatter.Format`、廉价预检、FreshName 作用域扫描、clean 文件字节复制/BOM、`AfterFilesAsync` 闸门聚合位。
- **扩根层（带 C16 回归）**：
  1. `ForeachSourceSite` 增 `RefKind RefKind`（None/Ref/RefReadOnly；`IsRef` 保留为 `RefKind != None` 便利属性）与 `ForEachBinding? Binding`（nullable，§4.2）；
  2. `ForeachSourceStatistics` 增 §5.3 计数器；
  3. FreshName 基名增 `__cs2jFaSrc/__cs2jFaE/__cs2jFrSrc/__cs2jFrI/__cs2jFrCur`（与 C16 `__cs2jFsEntry`、E07 `__entry` 区分）。
- 临时提升不新建框架：源缓存/枚举器局部的声明合成由 `EnumeratorLoweringBuilder` 直接产 `LocalDeclarationStatementSyntax`（仓库内 while/try 合成先例：[RefOutInSyntaxRewriter.cs:519-541](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L519-L541)、[StateMachineBuilder.cs:1228](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L1228-L1228)）。

### 4.2 ForeachBindingResolver

internal 纯函数，输入 `(CommonForEachStatementSyntax node, SemanticModel model)`，输出归一绑定：

```
ForeachBinding
  RefKind RefKind                       // 迭代局部符号 RefKind（权威）
  bool IsDynamic                         // info.IsDynamicallyDispatched
  ITypeSymbol ElementType               // info.ElementType
  ITypeSymbol CollectionType             // GetTypeInfo(node.Expression).Type
  // 枚举器模式
  IMethodSymbol? GetEnumeratorMethod    // null = 数组 / 直接枚举器 / 纯索引模式
  IMethodSymbol? MoveNextMethod
  IPropertySymbol? CurrentProperty
  IMethodSymbol? DisposeMethod          // pattern Dispose 或 IDisposable；null = 不释放
  ITypeSymbol? EnumeratorType           // GetEnumeratorMethod?.ReturnType ?? CollectionType（直接枚举器）
  bool DirectEnumerator                 // GetEnumeratorMethod==null 且 MoveNextMethod!=null
  // 索引模式
  bool IsIndexPattern                   // array，或 Length/Count + 单参 int 索引器
  ISymbol? LengthMember                 // Length/Count 属性或字段
  IPropertySymbol? Indexer              // this[int]；RefKind 决定 FR-1/FR-2
```

要点：`GetForEachStatementInfo` 一次取得枚举器四符号；`DisposeMethod` 直接决定 finally（决策 6）；数组判定 `CollectionType is IArrayTypeSymbol`；`IEnumerable` 判定扫 `AllInterfaces`（含定义类型自身）；索引模式成员自行用语义查找（取编译器同款成员：优先 `Length` 属性/字段，次 `Count`；索引器取参数为 int 的 `IPropertySymbol`，校验 `RefKind` 与 foreach RefKind 兼容）。操作 API（`IForEachLoopOperation`）仅用于交叉校验与 IsRef 探针，不作硬依赖。

### 4.3 规则全集（before/after）

| 规则 | 命中输入 | 输出骨架 | 要点/边界 |
|---|---|---|---|
| FA-1 实例 GetEnumerator | 普通 foreach；源非 array/string/IEnumerable；`GetEnumeratorMethod!=null` 且非扩展 | §1.3 形态：`__cs2jFaSrc` + `var __cs2jFaE = __cs2jFaSrc.GetEnumerator()` + while | 值类型枚举器 var 不装箱（决策 5）；Current 转换强转（决策 7）；Dispose 二分（决策 6） |
| FA-2 扩展 GetEnumerator | 同 FA-1 但 `GetEnumeratorMethod.IsExtensionMethod == true` | 同形，调用保留 `__cs2jFaSrc.GetEnumerator()` 扩展语法 | 不内联静态名；CS2J5451 计数；using 保留保证重绑定 |
| FA-3 直接枚举器源 | `DirectEnumerator`：源即 IEnumerator/IEnumerator\<T\>/鸭子枚举器（含非泛型 Current=object） | `var __cs2jFaE = __cs2jFaSrc;` + while(Current/MoveNext) | 不二次 GetEnumerator（决策 4）；object Current 强转保留装箱（决策 7）；CS2J5452 |
| FA-4 Dispose/异常骨架 | FA-1/2/3 的公共构造（不单列站点规则） | `DisposeMethod!=null` → try/finally；否则裸 while | pattern/接口/显式接口三形态（决策 6）；探针验证非 IDisposable 无 Dispose 调用 |
| FA-5 Span 系索引循环 | 源类型为 `System.Span<T>`/`System.ReadOnlySpan<T>`（任意 RefKind=None） | `var __cs2jFrSrc=…; for(int __cs2jFrI=0; __cs2jFrI<__cs2jFrSrc.Length; __cs2jFrI++){ var item=__cs2jFrSrc[__cs2jFrI]; body }` | 决策 9；Java 半区吃现有 `length/get/set`；与 FR-1/FR-2 共用索引构建器，仅 ref 修饰不同 |
| FR-1 数组 ref/ref readonly | RefKind≠None；源为一维数组 | 索引循环 + `ref T item = ref __cs2jFrSrc[i]`（readonly 加 readonly） | 多维数组 ref foreach 不合法（C# 仅一维可索引），verify build 兜底 + CS2J5485 |
| FR-2 Span/自定义 ref struct 索引源 | RefKind≠None；索引模式（Length/Count + 引用返回索引器） | 同 FR-1，长度成员按绑定符号取 Length 或 Count | 自定义源需 `ref T this[int]`（ref readonly foreach 接受 `ref readonly T this[int]` 或 `T this[int]`？——后者不允许 ref 绑定，编译器裁决，verify build 兜底）；产物交 C01 |
| FR-3 ref-Current 枚举器 | RefKind≠None；枚举器模式且 `CurrentProperty.RefKind` 为 Ref/RefReadOnly | FA 骨架，元素行换为 `ref T item = ref __cs2jFaE.Current;`（readonly 同形） | 即「ReadEnumerator」形态；Dispose 规则同 FA-4；CS2J5482 |
| FR-4 不可绑定 ref 源 | RefKind≠None 但既无索引模式、Current 又非引用返回、dynamic | 节点不动 + CS2J5485 Error（dynamic 降 CS2J5456 Warning） | 正常可编译代码不应落此；主要防 error-type/语言版本错配 |

### 4.4 合成细节

1. **先 Visit 子节点**（同 E07 [L76](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L76-L76)）：嵌套 foreach 先递归，再整节点替换，FreshName 按作用域避让。
2. **body 规整**：非块体包 `Block`（移植 [ForeachDeconstructionRewriter.cs:424-436](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L424-L436) 的 PrependStatements）；元素声明/解包语句前置；原节点 trivia 搬到替换语句首节点。
3. **try/finally 合成**：`SyntaxFactory.TryStatement(Block(whileStmt), default, finallyClause)`；finally 块仅一条 Dispose 语句（显式接口形态先转 `(IDisposable)e`）。引用类型枚举器优先直接 `e.Dispose()`（可访问时），避免不必要强转。
4. **源缓存**：FA/FR 统一在最前插 `var __cs2jXxxSrc = node.Expression;`，循环内全部引用缓存局部（不变式 6）；副作用计数器 fixture 断言源只求值一次。
5. **for 循环**（FR/FA-5）：`For(int i=0; i<src.Length; i++)`，条件与步长不引方法调用以外的复杂表达式，确保 C01 的条件 splice（[RefOutInSyntaxRewriter.cs:552-636](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L552-L636)）与转换器直转无歧义。
6. **ref 局部声明**：`VariableDeclaration(RefTypeSyntax(ref/readonly, TElement), VariableDeclarator(item).WithInitializer(EqualsValue(RefExpression(indexAccess/Current))))`；类型文本用语义符号渲染，不用 `node.Type` 原文（`var` 在 ref 局部声明中非法，必须实类型）。

---

## 5. CLI 集成

### 5.1 文件清单（全部追加，无结构性改动）

1. **[ForeachSourcePassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（C16 新增文件）**：规则注册列表追加两行（`EnumeratorAdapterRule`（内含 FA-5 Span 判定）与 `RefForeach` 两条规则）；摘要行追加 C40 计数段。此文件是 C40 唯一的适配器编辑点（C16 设计 §11 所有权约定）。
2. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) foreach-source 描述符：**一行** `catalogItems: "C16" → "C16, C40"`，Description 追加「enumerator adapters and ref foreach」。
   - **冲突结论**：C16 收窄在前、C40 追加在后是 C16 设计 §3.4-8/§11 预定的单行协调点；本分支在 C16 合入后追加该行，合入说明中显式声明，不做其它改回。
3. Core 新增 §3.2 六个文件 + 根层扩展（§4.1）；不新增 pass id、不动 `PassStubCatalog`（C16 已删该 stub）、不动 `PassChainFactory`（适配器仍为同一个 foreach-source 适配器）、不动 `Program.cs` 选项。
4. 统计汇入：§5.3 计数器进现有摘要/`PassFileRewrite` 通道；CS2J5402/5403/5404 spine 失败码复用 C16 接线。
5. fixture：在 C16 新建的 `tests/SampleForeachSourceSolution/` 内**追加** `Enumerator*.cs`/`Ref*.cs`（lib 源文件）与对应 xunit 断言文件，不新建 sln。

### 5.2 默认开关与入口面

- 本任务**不翻** `defaultEnabled`：随 foreach-source 开关（默认关→§8 门槛后随锚点一起翻 true）；`--foreach-source`、通用 `--enable-pass`、独立 `preprocess --pass foreach-source` 均无需新增。
- parse 选项、双 verify、`DeleteBuildOutputsAfterRestore`、指纹/中间目录纳管全部沿用锚点，零新增行为面。

### 5.3 新增统计字段

`DuckEnumeratorSitesLowered`、`ExtensionGetEnumeratorSites`、`DirectEnumeratorSites`、`StructEnumeratorSites`、`DisposePatternCallSites`、`DisposeBoxedStructSites`、`NoDisposeSites`、`SpanIndexLoopSites`、`RefIndexLoopSites`（数组/Span/自定义分桶由诊断码细分）、`RefReadonlySites`、`RefCurrentEnumeratorSites`、`DynamicForeachSites`、`UnresolvedForeachSites`、`ResidualEnumeratorSites`、`ResidualRefSites`，并入 `ErrorsByCode`。

---

## 6. 诊断策略

MSBuild 格式，沿用 C16 §6 头：`文件(行,列): warning/error CS2J54xx: [FA-x/FR-x] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5402/5403/5404 | Error | restore/build/test 失败（spine，复用 C16） |
| CS2J5450 | Info | 模式化枚举器站点已 lower（聚合计数，verbose） |
| CS2J5451 | Info | 扩展 GetEnumerator 站点（FA-2） |
| CS2J5452 | Info | 直接枚举器变量源站点（FA-3） |
| CS2J5453 | Info | 值类型枚举器以 var 具体化、无装箱（探针计数） |
| CS2J5454 | Info | pattern Dispose / IDisposable 调用已生成（含次数） |
| CS2J5455 | Info（strict Warning） | 显式接口 Dispose 迫使值类型枚举器装箱（风险 1 可观测点） |
| CS2J5456 | Warning（strict Error） | dynamic 分发 foreach 无法静态 lower（归 C25 前的显式信号） |
| CS2J5457 | Warning（strict Error） | 非 IEnumerable 源但绑定残缺/无法构造骨架（多重候选等异常态） |
| CS2J5470 | Warning（strict Error） | residual 闸门：输出树仍有「非 array/string/IEnumerable + 有效绑定」的裸普通 foreach |
| CS2J5480 | Info | ref foreach 索引循环（数组/Span/自定义索引源） |
| CS2J5481 | Info | ref readonly 站点 |
| CS2J5482 | Info | ref/ref readonly Current 枚举器形态（FR-3） |
| CS2J5485 | Error | ref foreach 源不可索引且 Current 非引用返回（正常不命中；防错配） |
| CS2J5486 | Warning（strict Error） | ref 局部跨不过的构造（闭包捕获 ref struct 等）站点定位（verify build 兜底） |
| CS2J5495 | Warning（strict Error） | residual 闸门：输出树仍有带 RefKeyword 的 foreach |

5460-5469 预留给 FA-5/Span 细分与未来自定义索引模式诊断；码段细分与 C16 §6 表双向一致（5450-5479/5480-5499）。

---

## 7. 测试计划

### 7.1 Core 单测（新增 `tests/CSharpToJava.ProjectTests/ForeachSourceC40RewriterUnitTests.cs`，内联源码→compilation→Preprocess，仿 [ForeachDeconstructionTests.cs:409-429](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs#L409-L429)）

**枚举器适配（FA）**：
1. 实例 `GetEnumerator()` 鸭子类（class，无 IDisposable）：断言 while 骨架、`var __cs2jFaE`、**无 try/finally、无 Dispose 文本**（探针：自定义枚举器带 `Dispose()` 抛异常/置位旗标，运行时验证不调用）；
2. 扩展方法 GetEnumerator（static 扩展类）：调用语法保持 `src.GetEnumerator()`、using 不丢；
3. 显式 `IEnumerator`/`IEnumerator<T>` 变量直接 foreach：无 GetEnumerator 调用、`var __cs2jFaE = __cs2jFaSrc;`；非泛型 + `foreach (int x in e)` 强转 `(int)__cs2jFaE.Current`；
4. **struct 枚举器不装箱**：`List<int>` 不重写（IEnumerable 排除），自定义 `struct E { MoveNext/Current }` + 容器鸭子类：断言声明文本为 var 具体化、无 `IEnumerator` 转换；用「MoveNext 修改状态字段」的 struct 枚举器做语义对照（装箱即错）；
5. IDisposable class 枚举器：try/finally + `e.Dispose()`；仅显式实现 IDisposable 的 struct 枚举器：`((IDisposable)e).Dispose()` 形态 + CS2J5455；有 public pattern Dispose 的非 IDisposable struct：直接 `e.Dispose()`、无 cast；
6. `Span<int>`/`ReadOnlySpan<int>` 普通 foreach（FA-5）：索引循环、无 GetEnumerator 文本、元素行为值访问；
7. 零误伤：`int[]`、`string`（已 ToCharArray 形态）、`List<T>`、`IEnumerable<T>`、`Dictionary<K,V>` 源 Changed=false 或不再被 FA 命中；
8. 守卫：await foreach 节点不动（CS2J5442 仍由 C16 计数）；ForEachVariableStatement 不进 FA/FR；
9. 幂等：lower 后二次 Preprocess 零 diff；副作用源 `GetSrc()` 仅求值一次（文本断言单缓存）；
10. Current/MoveNext 抛异常探针：异常冒泡、Dispose 仍执行一次；Current 每轮恰好取一次。

**ref（FR）**：
11. `foreach (ref var x in span)` 与显式类型 `foreach (ref int x in span)`：索引循环 + `ref int x = ref __cs2jFrSrc[__cs2jFrI]`；Length 成员工具；
12. `foreach (ref readonly var x in span)` / `ref readonly int`：`ref readonly` 形态、CS2J5481；
13. 一维数组 ref/ref readonly foreach：`x = v` 经中间树编译；多维数组源走 CS2J5485/编译错误兜底；
14. 自定义 ref struct 集合（`Count` + `ref T this[int]`）：索引循环取 Count；自定义「ReadEnumerator」（GetEnumerator 返回具 `ref T Current` 的 struct）：FR-3 骨架 + `ref T x = ref e.Current`；
15. 不兼容源：ref foreach 于无 Length/Count 且 Current 按值返回的类型 → CS2J5485；dynamic → CS2J5456。

**共处**：
16. E06→E07→foreach-source（C16+C40 规则全注册）串联：KV/Tuple/string/鸭子/ref 五类站点各归其规则，统计分桶互不串；
17. foreach-source→C01 串联（可用 C01 Preprocessor 直接串跑）：`ref int x = ref span[i]` 形态进 C01 后产物形态记录（当前 C01 元素访问分支 [RefOutInSyntaxRewriter.cs:783-789](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L783-L789) 对非数组接收器的缺口见风险 3，测试先标记 Skip/显式断言现状，缺口修复后转绿）。

### 7.2 fixture 追加（SampleForeachSourceSolution）

- Lib 追加：`EnumeratorDuck.cs`（class 无 IDisposable、class 有 IDisposable、struct 枚举器、扩展 GetEnumerator、ref-Current 枚举器）、`RefCollections.cs`（Span 填充/求和、数组原地翻倍、自定义 ref struct 集合）；
- Tests .NET 半区断言：
  - **Span 元素修改可见性**：`foreach (ref var x in span) x *= 2;` 后原 span/支撑数组逐元素为两倍（本任务核心运行时对照）；ref readonly 路径只读求和一致；
  - 数组 ref foreach 原地修改可见；自定义 ref struct 集合同口径；
  - 非 IDisposable 枚举器：Dispose 旗标始终 false；IDisposable 枚举器：正常结束/break/异常三路径 Dispose 各恰好一次；`continue` 路径 Dispose 不被中途调用；
  - 求值一次：源为带计数器方法/属性时每次 foreach 只求值一次；
  - 闭包捕获：体内收集的 Action 各自持有本轮值（决策 12）；
  - 嵌套：外层鸭子集合 + 内层 ref Span，双层计数与修改正确。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx）全绿；`dotnet test -c Release` 三工程（Tests / ProjectTests / ConversionTests）全绿；
- 既有回归不改即绿：[ForeachDeconstructionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs)、[ArrayForeachNotCollectedTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ArrayForeachNotCollectedTests.cs)、[ForeachNonGenericCollectionCastTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachNonGenericCollectionCastTests.cs)、[DictionaryEnumeratorMappingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DictionaryEnumeratorMappingTests.cs) 等枚举器映射簇（验证「实现 IEnumerable 的源一个都不重写」）。

### 7.4 Java 半区冒烟

- `Span<int>` 普通 foreach（FA-5）与 ref foreach（FR 串 C01）端到端：产物分别为 `span.get(i)` 与 ByRef 访问器代理（依赖风险 3 修复），可编译、元素修改在 Java 侧可见；
- 自定义鸭子集合/枚举器（用户类型正常参与转换）：MoveNext→moveNext、Current→getCurrent、Dispose→dispose/try-finally 直转路径冒烟；
- 数组 ref foreach 经 C01 `ByRef.create(arr, i)` 别形态冒烟；
- 证实 Loops.cs 增强 for 路径不再接收非 Iterable 源（转换器抛点/直连安全网不在本任务改）。

### 7.5 residual 闸门（C16 FS-5 聚合的 C40 分桶）

判定口径（对输出树建 compilation，语义扫描全部 foreach 站点）：

1. **枚举器桶 CS2J5470**：普通 foreach 中源非 array、非 string、未实现 IEnumerable/IEnumerable\<T\>，且 `GetForEachStatementInfo` 给出有效模式绑定（或直接枚举器绑定）——零残留；
2. **ref 桶 CS2J5495**：任何 `RefKind != None` 的 foreach——零残留；
3. 分离计数不判失败：await 桶（CS2J5442，C38）、string/解构桶（C16 5440）、dynamic（5456，按 strict 策略）；
4. strict 下 CS2J5470/5495 退出码非零；非 strict Warning。

### 7.6 CLI / 一致性

- 无新开关：`--list-passes` 仍只有 foreach-source 一条，catalogItems 显示 `C16, C40`；链序测试 [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) LockedChain **不变**（同 pass）；stub 计数测试不变；
- 新增 ProjectTests：`preprocess --pass foreach-source` 对 duck/ref 夹具的输出文本、strict 退出码、统计摘要含 C40 字段。

---

## 8. 默认开启验收门槛

C40 无独立开关，随 foreach-source 一起默认开；除锚点 C16 §8 全部门槛外，附加：

1. §7.1 全部 FA/FR 单测绿；§7.2 fixture 中间树 `dotnet build/test -c Release` 绿，**Span 元素修改可见性、非 IDisposable 不 Dispose、Dispose 次数三族运行时断言全绿**；
2. **C01 联合链路绿**：FR 产物经 ref-out-in 后中间树可编译（风险 3 关闭），Java 半区 ref Span/数组样本可编译且写改可见（§7.4）；
3. 零误伤回归：数组/string/List/Dictionary/IEnumerable 源改写计数为 0（§7.1-7 与既有映射簇）；
4. residual 两桶（5470/5495）在 fixture 与启用扫描的工程上为零；
5. csharp-sdk（只读训练集）启用扫描报表：鸭子源/直接枚举器/Span foreach/ref foreach 站点分桶计数，每条 5455/5456/5457/5485 人工定性（真不支持 vs 分析器过保守），作为开放问题 1/2 的决策输入；
6. 与 C38 的边界备注、与 C16 的码段/所有权双向核对完成；sdk 基线不退化（改写为局部语法变换，verify 成本由指纹缓存承担）。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 duck-typed 枚举器 | ForeachBindingResolver + FA-1/2/3/4（含 try/finally、强转、源缓存）+ 注册追加 + §7.1-1/2/3/8/9/10 + fixture 鸭子类与 Dispose 时序断言；IsRef API 探针（开放问题 5） | Core ~260 行 |
| M2 struct/非 Dispose 细节与收口 | struct 不装箱/显式接口装箱/pattern Dispose（FA 决策 5/6）、FA-5 Span 索引循环、闭包捕获/break-continue/嵌套/标签夹具、CS2J5455-5457 与 5470 闸门、§7.3 Release 全绿、零误伤回归 | Core ~180 行 + fixture |
| M3 ref/ref readonly | FR-1/2/3/4 + CS2J5480-5495 + 与 C01 联合链路打通（风险 3 关闭）+ Java 半区冒烟 + sdk 分桶报表 + §8 附加门槛；随锚点评审默认开 | Core ~200 行 + 联调 |

---

## 10. 风险

1. ★**值类型枚举器装箱导致性能/语义差异**：错误地以 `IEnumerator` 接收 `GetEnumerator()` 返回值，或显式接口 Dispose 强转装箱，会在循环前/退出时复制枚举器状态——多数 BCL struct 枚举器（List/Dictionary）Dispose 为空操作故仅性能差，但自定义有状态 struct 枚举器可能错值/死循环。缓解：决策 5 硬性 `var` + 探针断言文本与运行时行为；显式接口 Dispose 装箱点 CS2J5455 全量计数并在 sdk 报表中列名，必要时为已知空操作枚举器评估跳过策略（开放问题 2）。
2. **Dispose 误加/漏加**：把「非 IDisposable」误当「不 Dispose」会漏掉 pattern Dispose（Roslyn 对可访问无参 `Dispose()` 有直接调用语义）；反之给无 Dispose 枚举器加 finally 会产出不可编译 C#。缓解：唯一事实源是 `info.DisposeMethod`，三形态构造集中在 Builder 一处；非 IDisposable 旗标探针与 Dispose 次数探针双锁。
3. **ref 生命周期与 C01 一致性（最大外部依赖）**：FR 产物 `ref T x = ref span[i]` 进 C01 后，现有 ref 局部元素访问分支无条件发 `ByRef<T>.Create(span, i)`（[RefOutInSyntaxRewriter.cs:783-789](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L783-L789) + [DecomposeElementAccess:1101-1106](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L1101-L1106)），而工厂 `Create(T[] array, int index)`（[ByRef.cs:14](file:///d:/code/cs2j/src/Cs2jRefOutIn/ByRef.cs#L14-L14)）不接受 Span 接收器——中间树不可编译；C01 在**实参**站点对非数组索引器已有 copy-in/copy-out 槽（[RefOutInStatementLifter.cs:305-314](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInStatementLifter.cs#L305-L314)），但声明站点与别名语义（foreach 变量写改必须当轮回写）未打通。缓解：M3 前置联调，倾向 C01 声明站点升级为访问器代理 `ByRef<T>.Create(() => span[i], v => span[i] = v)`（C01 已有该工厂形态，Java 半区为 `ByRef.create(getter,setter)`，与 [Span.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Span.java) get/set 天然缝合）；在 C01 缺口修复前 FR 默认开门槛不达标（开放问题 1）。
4. **try/finally 与循环/状态机叠加**：合成 try-finally 位于迭代器方法（C04）/含 goto（E14）方法内时，Dispose 时序须在状态机展开后仍正确（catalog §4 风险 2 同族）。缓解：C40 只产源生 try/finally，不碰状态机；fixture 覆盖「迭代器方法内的鸭子 foreach + break」串到 C04 后的中间树 build/test。
5. **Current 转换强转误插**：对泛型协变/标识转换滥加强转造成可读性/编译问题，对 user-defined CurrentConversion 漏插则类型错误。缓解：按符号差异判定是否加强转，枚举器/元素类型组合夹具覆盖。
6. **ref readonly 经 C01 的只读形态未定**：访问器代理若带 setter 则失去 readonly 约束（C# 侧），但只读本就是源语言保证；需确认 C01 产物可编译且无额外写回（开放问题 3）。
7. **Span 外的 ref struct 源命中率未知**：自定义 `Length`+ref 索引器集合、ref-Current 枚举器在真实代码中可能极罕见；若 sdk 扫描零命中，FR-2/FR-3 维持实现但不阻塞默认开评审（以 fixture 证明正确性即可）。

---

## 11. 并行协调

- **C16（锚点，硬依赖）**：本任务在 C16 分支合入后开工（worktree base 见 §12）；只新增 §3.2 自有文件、适配器注册追加两行、catalogItems 一行追加、根层扩展带 C16 回归；不改 C16 FS-1/2/3 规则体与 SyntaxFacts 既有行为（C16 设计 §11 所有权约定）。
- **C38（异步源边界）**：同 foreach 插入点但不同 pass/码段；C40 对 AwaitKeyword 零接触；C38 设计文档尚不存在，边界假设（§3.3）待其启动时双向确认；两份文档都不得把 await 统计纳入各自闸门失败项。
- **C01 ref-out-in（产物承接，强协同）**：链上 foreach-source 先于 ref-out-in；FR 的 ref 局部与 FA-5 Span 形态以「C01 可消费」为验收条件；风险 3 的 C01 声明站点升级建议作为 C40 M3 的姊妹改动（同 worktree 或明确前置 PR，由评审定）。参考 C01 设计 Q2（RefStructByRef）与 [RefStructByRef.cs](file:///d:/code/cs2j/src/Cs2jRefOutIn/RefStructByRef.cs)。
- **C06 unsafe-ffm（Span 形态同源）**：C06 把指针/stackalloc 归约为数组/Span+索引（C06 设计 D1/D7）；FR/FA-5 消费其产物形态（Span 变量、`Length`、`this[int]`），不重复 Span 映射决策；fixed/Span 交叉 fixture 对齐。
- **E02/C32（捕获提升）**：决策 12 依赖转换器现有普通局部 holder 路径；closure-lift（C32/C45 stub）未来统一捕获分析时，lower 后的 item 是普通块级局部，按普通局部处理即可，无需 foreach 特例。
- **E06（tuple）/E07（foreach-deconstruction）**：均在本 pass 之前，ForEachVariableStatement 面不触碰；共处测试（§7.1-16）锁顺序契约。
- **C04 iterator-lowering / E14 eliminate-goto / C08 java-keywords**：消费合成 while/try/for/标签树；临时名 `__cs2jFa*/__cs2jFr*` 双下划线开头经 C08 改名仍合法；E14 纯语法 pass 对新增结构无需适配，但 goto 跨循环夹具须过。
- **热文件**：C40 全部代码在 C16 新目录内；跨任务共享文件仅 `ForeachSourcePassAdapter.cs`（追加两行）、`PassCatalog.cs`（一行字符串，C16 预留的预期冲突）、fixture sln（追加文件不改编排）；与同日其它任务零行级重叠。

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c40-foreach-source`
- 目录：`.worktrees/preproc-c40-foreach-source`
- base：`953293b9`（`docs: update doc base commit`；**实施前先合入 C16 锚点分支**——含 `ForeachSource/` 骨架、适配器、注册表翻转与 SampleForeachSourceSolution；合并完成后在 C16 合入产物上创建本 worktree）
- 创建命令（仓库根、PowerShell；**设计阶段不执行，实施阶段由执行者运行**）：

```powershell
git worktree add .worktrees/preproc-c40-foreach-source -b feature/preproc-c40-foreach-source 953293b9
```

> 注：若创建时 C16 尚未合入 `953293b9` 所在主线，应先按 C16 任务的 worktree/合入流程完成合并，再以「C16 合入后的等价基点」执行同一命令（基点哈希以合入实际结果为准并回填本文件）。
- 交付面遵循 foundation plan 交付包模板（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core 两规则目录六个文件 + 根层扩展、适配器两行注册 + catalogItems 一行追加、Core 单测 + fixture 追加 + Release build/test + residual 两桶闸门 + CLI 测试、plan 文档 `docs/superpowers/plans/2026-09-29-c40-foreach-source.md`；
- 不新建支撑工程；不触转换器；不创建 git 标签、不做 push；实施授权以任务书为准。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持 base `953293b9`，创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（按原题号）：**

1. **`ref T x = ref span[i]` 声明站点代理形态**：采纳前者——**升级 C01** [RefOutInSyntaxRewriter.cs:783-789](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L783-L789) 元素访问分支，对非数组接收器发访问器代理 `ByRef<T>.Create(() => span[i], v => span[i] = v)`，作为 **C40 M3 的硬前置 PR**（保留别名语义与体内 ref 实参传递能力，Java 半区与 Span get/set 缝合）；否决 C40 侧产出无 ref 局部展开（丢语义）。C01 该 PR 与 C40 各自独立提交、顺序合入。
2. **空 Dispose 装箱白名单**：**不做**符号白名单、不做合成空方法探针跳过；v1 一律如实生成 finally 释放并计 **CS2J5455**（分配噪声不属于语义偏差，跳过白名单会引入"按符号猜语义"的长期维护面）；白名单优化须等 sdk 命中率+基准证据另立项。
3. **ref readonly 产物**：用**无 setter 访问器代理**（`ByRef<T>.Create(getter, null)` 形态/等价只读代理），否决统一双参代理——只读约束必须在 C# 中间树编译期就可见；fixture 锁定"对代理写入无法编译"，并与 C01 设计 Q 系列回填同口径。
4. **自定义鸭子枚举器验收深度**：M3 冒烟只覆盖常规 MoveNext/Current/Dispose 转换；**非泛型枚举器 + 值类型元素的 cast/装箱对照**按冒烟实测反馈决定是否扩 fixture——发现可观察差异即扩，不预防性铺满。
5. **`IForEachLoopOperation.IsRef` 可用性**：M1 第一步用最小编译探针确认 Roslyn 4.12 下属性可取；**不可取则全程使用 `ILocalSymbol.RefKind`**（已确认可靠），设计其余部分不回退，探针结论回填本文。

---

### 原问题记录（裁决前）

1. **C01 对 `ref T x = ref span[i]` 声明站点的代理形态**：升级 [RefOutInSyntaxRewriter.cs:783-789](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInSyntaxRewriter.cs#L783-L789) 元素访问分支，对非数组接收器发访问器代理 `ByRef<T>.Create(() => span[i], v => span[i] = v)`（别名语义正确、Java 半区与 Span get/set 缝合），作为 C40 M3 前置？还是由 C40 在 C# 侧直接产出无 ref 局部的展开（会丢失体内 ref 实参传递能力，不推荐）。倾向前者。
2. **显式接口 Dispose 装箱是否需要白名单跳过**：对 `List<T>.Enumerator` 等已知 Dispose 为空操作的 struct，finally 中 `((IDisposable)e).Dispose()` 是纯分配噪声；是否按符号白名单或「struct 且 Dispose 为合成空方法」探针跳过？默认先如实生成 + CS2J5455 计数，sdk 命中率驱动决策。
3. **ref readonly 经 C01 的产物**：无 setter 访问器代理 vs 统一双参代理（仅 C# 侧约束差异）；需与 C01 设计 Q 系列对齐后定形态，fixture 先锁「不可写」在 C# 中间树编译期成立。
4. **自定义鸭子枚举器 Java 半区验收深度**：用户类型的 MoveNext/Current/Dispose 走常规转换，理论无坑，但非泛型枚举器 + 值类型元素的 cast/装箱在 Java 侧的行为对照做多深，随 M3 冒烟反馈决定是否扩 fixture。
5. **`IForEachLoopOperation.IsRef` 在 Roslyn 4.12 的运行时可用性**：包元数据已见 `get_IsRef`，但 M1 需用最小编译探针确认属性可取；不可取时全程使用 `ILocalSymbol.RefKind`（已确认可靠），不影响设计其余部分。
