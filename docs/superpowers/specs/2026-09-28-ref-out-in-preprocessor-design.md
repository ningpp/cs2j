# ref/out/in 前置转换设计（C# → C#，统一 ByRef 位置模型）

日期：2026-09-28
状态：已评审（基于任务书《前置转换：ref / out / in 完全消除》压力测试后修订）
范围：`src/CSharpToJava.Core` 新增重写器、`src/Cs2jRefOutIn` 支撑库、`src/CSharpToJava.CLI` 预处理器与 `convert-project` 集成、测试、`D:\csharp-sdk` 验证。**本期不含 Java 侧改动。**

---

## 1. 背景与目标

cs2j 是 C# → Java 转译器，Java 没有 `ref` / `out` / `in` 语义。当前生产路径在 Java 代码生成阶段（[ArgumentTransformer.Ref.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Ref.cs) + [HolderTypeResolver.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/HolderTypeResolver.cs)）按需产出 `IntHolder` / `ObjectHolder<T>` 等 Java holder。该路径 lowering 深埋在字符串拼接式代码生成中，调用点提升、`out var`、丢弃、嵌套调用极易出错，且无法在 .NET 侧直接证明语义等价。

本设计新增一条 **RefOutIn 前置转换**（纯 C# → C# 源码重写），用统一的"可变位置（ByRef）"对象模型，把**方案内部定义**的 ref/out/in 全部 lowering 到 C# 源码层。产物必须合法、可编译、行为等价（`dotnet build -c Release` / `dotnet test -c Release` 零新增失败）。

[LowerRefOut.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerRefOut.cs) 仅注册在实验性 HIR 管线，不在生产路径上；本任务**不涉及、不依赖、不修改**它。

### 1.1 对任务书的关键修订（压力测试结论）

任务书要求"产物中完全不出现任何 ref/out/in 关键字"。该目标与"build 零错误"在数学上不相容，实测确认：

- `D:\csharp-sdk` 中至少 **30 个文件、106 处**对外部元数据符号的 ref/out 调用（`int.TryParse`、`Dictionary.TryGetValue`、`Volatile.Read(ref ...)`、`Interlocked.*` 等，见 [Regex.Cache.cs](file:///D:/csharp-sdk/regular-expressions/src/System/Text/RegularExpressions/Regex.Cache.cs) 等）。
- C# 语法规定：调用外部方法时实参修饰符必须与形参 `RefKind` 匹配，`ByRef<T>` 无法传给真正的 `out int` 形参；这些调用点**不可能**去掉 `ref`/`out`。
- 任务书的"普通数组/字段整体提升为 holder 类型"策略一旦执行，被提升存储将无法再传入 BCL（如 `Volatile.Read(ref s_cacheList[i].LastAccessStamp)`），只能制造豁免并在豁免处戳破别名图。
- 任务书"不改 Java 侧"与"默认接入 convert-project、ArgumentTransformer.Ref 分支不可达"互相矛盾：`ByRef<T>` 没有 Java 映射，现有 [RefOutInProjectTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs) 断言 Java 产物含 `IntHolder`/`ObjectHolder`。

经逐项确认，本设计采用以下六条修订决策：

| # | 决策点 | 结论 |
|---|--------|------|
| D1 | 外部符号边界 | **仅内部消除**：外部方法/索引器调用点原样保留 `ref/out/in` |
| D2 | 普通存储策略 | **代理机制**：普通数组/字段/局部零提升，取地址点生成可写代理 |
| D3 | Java 衔接 | **本期 C# 闭环**；`convert-project` 默认开启；Java 产物暂不可编译为已知中间状态，Java 映射另开后续 spec |
| D4 | ref struct holder | **判别联合型 ref struct**（direct / array+index 双模式），按具体类型生成 |
| D5 | SDK 验证口径 | 全量 `csharp-sdk.sln`（32 项目）+ 动手前基线快照，验收按"新增失败为零、通过项目数不降"比较 |
| D6 | 外部契约成员 | 实现/重写外部接口或虚方法、`[DllImport]`/`[LibraryImport] extern` 成员**一律锁定不改** |

---

## 2. 边界判定（内部 / 外部 / 锁定）

重写器基于 `SemanticModel` 对每个相关符号做来源分类（跨文件、跨项目共享同一张判定表）：

- **内部（可改写）**：符号的**声明语法**存在于本次预处理编译的源树中（同解决方案内项目通过 `CSharpCompilation` 项目引用共同纳入）。
- **外部（保留）**：符号来自元数据引用（BCL、NuGet、未纳入的程序集）。
- **锁定（保留，即使声明在源码内）**：
  1. 直接或显式/隐式实现**外部接口**方法的成员（沿 `IEventSymbol.ContainingType` / 接口映射 `ExplicitOrImplicitInterfaceImplementations` 追溯，任一接口为外部则锁定）；
  2. `override` / 实现外部类型抽象/虚成员的成员（沿 `Override` 链与 `Overridden*Member` 追溯到外部则锁定）；
  3. 带 `[DllImport]` / `[LibraryImport]` 特性的 `extern` 方法（ABI 封送锁死）。
- 纯内部接口/虚方法链（接口与全部实现都在方案内）全链一致 holder 化；内外部链不相交——若一个内部接口同时被外部代码实现（源码不可见），按保守策略把该接口成员视为**锁定**。

锁定成员：签名、方法体、所有调用点（按普通 C# ref/out 规则）全部保持原样；计入统计 `LockedContractMembers`，并对每个锁定声明发 `Info` 诊断（便于审计，绝不跳过其它功能）。

`in` 重载碰撞兜底：去掉 `in` 后若与同表其它签名（含外部元数据签名）构成重复声明/调用歧义，则该参数**保留 `in`**（语义即按值，对后续 Java 转换无害），发 `Warning`，成员计入统计但不做跳过式放弃。

---

## 3. 支撑库 `src/Cs2jRefOutIn/`

与 [src/Cs2jUnsignedMath/](file:///d:/code/cs2j/src/Cs2jUnsignedMath) 同级，独立真实类库，不向内联单文件塞 helper（单文件模式除外，见 §7.2）。

### 3.1 csproj

对齐 `Cs2jUnsignedMath.csproj`：`net10.0` / `LangVersion 11` / `Nullable disable` / `ImplicitUsings disable`。

### 3.2 普通类型世界：抽象可变位置 + 工厂

命名空间 `Cs2jByRef`。

```csharp
namespace Cs2jByRef;

public abstract class ByRef<T>
{
    public abstract T Value { get; set; }

    // out 临时槽（无 seed）
    public static ByRef<T> Create() => new SlotByRef<T>();
    // ref 临时槽（带 seed）
    public static ByRef<T> Create(T initial) => new SlotByRef<T>(initial);
    // 数组元素别名：读写穿透到 array[index]
    public static ByRef<T> Create(T[] array, int index) => new ArrayElementByRef<T>(array, index);
    // 字段/静态字段/值参成员链别名：访问器闭包，读写穿透
    public static ByRef<T> Create(Func<T> getter, Action<T> setter) => new AccessorByRef<T>(getter, setter);
}
```

三个具体实现 `SlotByRef<T>` / `ArrayElementByRef<T>` / `AccessorByRef<T>` 为同文件 internal 密封类，持有槽字段 / `(T[] array, int index)` / `(Func<T>, Action<T>)`，`Value` 分别直读直写、`array[index]`、getter/setter。所有被改写签名只出现抽象类型 `ByRef<T>`，调用方一律经工厂构造——别名跨方法逃逸、`Swap(ref arr[i], ref arr[j])`、`return ref` 均天然保真。

要点：
- **异常语义保真**：访问器/数组代理在被调方每次赋值时即时写回原存储；方法中途抛异常时已发生的写入不丢失（任务书的"调用后统一写回"方案不满足此语义，本设计仅在 out-局部场景补一次写回以满足明确赋值，见 §5）。
- 方法参数、ref 局部、ref 字段、ref 返回槽位一律用 `ByRef<T>`；**普通数组保持 `T[]`、普通字段保持 `T`、普通局部保持 `T`**，外部 API 边界零损伤。

### 3.3 ref struct 世界：判别联合型 holder

ref struct 不能继承类、不能实现接口、不能被 lambda 闭包捕获，故抽象基类模型不适用。CLI 释放库项目时，按本次预处理**实际遇到**的 ref struct 类型并集，逐类型生成：

```csharp
namespace Cs2jByRef;

// 为 Span<int> 生成；mangling 取类型全名：Span<int> -> Span_Int
public ref struct RefStructByRef_Span_Int
{
    private Span<int> _slot;                 // mode 0：直持槽
    private Span<int>[] _array; private int _index; // mode 1：数组元素
    private readonly int _mode;

    public Span<int> Value
    {
        get => _mode == 0 ? _slot : _array[_index];
        set { if (_mode == 0) _slot = value; else _array[_index] = value; }
    }

    public RefStructByRef_Span_Int() { _mode = 0; _slot = default; _array = default!; _index = 0; }
    public static RefStructByRef_Span_Int Create() => new();
    public static RefStructByRef_Span_Int Create(Span<int> initial) { var h = new(); h._slot = initial; return h; }
    public static RefStructByRef_Span_Int Create(Span<int>[] array, int index)
        => new() { _mode = 1, _array = array, _index = index };
}
```

- 命名统一为 `RefStructByRef_<Mangled>`（mangling：去点、泛型 `` `n `` 与逗号/尖括号/空格替换为 `_`，数组追加 `Array`，嵌套类型用 `_` 连接），由重写器与支撑文件生成器**共用一个 mangling 工具方法**（放 Core，CLI 引用），杜绝两边不一致。
- 不依赖 `allows ref struct`（C# 13）。不生成字段链（accessor）模式（无法用闭包）；ref struct 字段链取址的边界见 §9 已知限制。
- 方法内 ref struct 局部若在方法内被取址（作 ref/out 实参、ref 局部、return ref），把该局部声明提升为对应 `RefStructByRef_*`（方法内闭包，不跨签名爆炸）；体内读写改为 `.Value`；与外部 ref/out API 互调时按 §5.4 处理。
- `ref struct` 类型作 `in` / `ref readonly` 参数：去修饰按值（合法），不生成 holder。

---

## 4. 覆盖矩阵（18 类，全部支持）

| # | 类别 | 策略 |
|---|------|------|
| 1 | 参数 `ref T a`（内部） | `ByRef<T> a`；体内 `a`→`a.Value`；调用点见 §5 |
| 2 | 参数 `out T a`（内部） | `ByRef<T> a`；体内 `a.Value=`；调用点见 §5 |
| 3 | 参数 `in T a` | 去修饰按值；碰撞兜底保留 `in`（§2） |
| 4 | 参数 `ref readonly T a` | 去修饰按值（同 in） |
| 5 | `scoped` / `scoped ref readonly` | 纯安全注解，直接剥离，其余按对应规则 |
| 6 | 显式实参 `M(ref x)` / `M(out x)` / `M(in x)` | 内部按 §5；外部调用保留修饰 |
| 7 | `out var x` / `out int x` / `out _` | §5.3 |
| 8 | 构造 / 局部函数 / lambda 参数 | 同 1–4（含 ref struct 变体） |
| 9 | 自定义委托 `delegate void D(ref T a);` | 内部委托签名→`ByRef<T>`；赋给它的具名方法/lambda 各自按同一符号表一致改写 |
| 10 | 接口/抽象/虚/重写/new 一致性 | 内部链全链一致 holder 化；外部契约链锁定不改（§2） |
| 11 | ref struct 的 `in` / `ref readonly` 参数 | 去修饰按值，无 holder |
| 12 | ref struct 的可变 `ref`/`out` 参数 | 判别联合型 `RefStructByRef_<Mangled>` |
| 13 | `ref T M()` / `ref readonly T M()` / ref 属性 | 返回 `ByRef<T>`；`return ref e`→`return <proxy(e)>`（§6.1） |
| 14 | `ref T r = ref e;` 局部 | `ByRef<T> r = <proxy(e)>;`（§6.2） |
| 15 | `ref T` 字段 | 字段类型→`ByRef<T>`，构造/初始化适配；含此字段的 ref struct 自动降级为普通 struct |
| 16 | ref 返回索引器 `public ref T this[int i]` | 同 13（数组元素经 `Create(arr,i)`） |
| 17 | 数组元素取 ref | 数组类型不变；经 `ByRef<T>.Create(arr, i)` 构造代理 |
| 18 | ref struct 作 ref/out/字段承载 | `RefStructByRef_<Mangled>`（字段场景见 §6.3、限制见 §9） |

C# 语法本身不允许 ref/out/in 的位置（运算符重载参数、索引器参数、扩展 `this`、`params`、事件访问器）无需处理。

---

## 5. 重写规则

### 5.1 三遍结构（Core：`src/CSharpToJava.Core/RefOutInPreprocessor/`）

目录结构对齐 [GotoEliminator/](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator) 与 [UnsignedTypeRewriter/](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter)：

- `RefOutInRewriterOptions.cs`：`record RefOutInRewriterOptions(bool Verbose = false, bool Strict = false)`。
- `RefOutInRewriterStatistics.cs`：`FilesScanned` / `FilesTransformed` / `FilesSkippedClean` / `RefParamsRewritten` / `OutParamsRewritten` / `InByValueParams` / `ScopedRemoved` / `DelegatesRewritten` / `RefReturnsRewritten` / `RefLocalsRewritten` / `RefFieldsRewritten` / `ArrayElementProxies` / `RefStructParamsRewritten` / `CallSitesLifted` / `HoldersInjected` / `AccessorProxies` / `LockedContractMembers` / `InOverloadKept` / `ExternalRefArgumentsKept`。
- `RefOutInRewriterDiagnostic.cs`：`enum RefOutInSeverity { Info, Warning, Error }` + `record RefOutInRewriterDiagnostic(string Id, string Message, RefOutInSeverity Severity, Location?)`。`Error` 仅用于"无法构造合法产物"的意外内部错误；任何矩阵场景不得以 Error 跳过。
- `RefOutInRewriterResult.cs`：`record RefOutInRewriterResult(string OutputCode, bool Changed, IReadOnlyList<...> Diagnostics, RefOutInRewriterStatistics Statistics, IReadOnlyCollection<string> RequiredRefStructTypes)`。
- `RefOutInMangling.cs`：类型全名 → holder 短名（CLI 与 Core 共用）。
- `RefOutInAliasAnnotator.cs`（第一遍）、`RefOutInSyntaxRewriter.cs`（第二/三遍合并的 `CSharpSyntaxRewriter`）、`RefOutInRewriter.cs`（门面：parse → dirty 检测 → 三遍 → 注入 using → `Formatter.Format`）。

第一遍产出的标注表（对整个 Compilation 共享，跨文件/跨项目）：

1. `SymbolClass`：Internal / External / Locked（§2）。
2. `HolderSymbols H`：内部 `ref`/`out` 参数、内部 ref/ref-readonly 返回成员、ref 局部、ref 字段，以及 ref 返回索引器。
3. 传递闭包（不动点）：
   - `ref T r = ref e`：`r∈H`，并记录 `e` 的代理构造方式；`return ref e` 同；
   - ref 形参向实参传播的是"位置"而非符号——运行时别名由代理对象承担，编译期只需保证每个取址表达式能构造代理；
   - 内部接口/虚链成员一入 H 全链入 H；锁定链不入 H。
4. 对 H 中每个槽记录 holder 变体：普通 `ByRef<T>` 或 `RefStructByRef_<Mangled>`。
5. 每个 ref/out 实参、`ref e` 表达式记录其**位置种类**（holder 槽直传 / 普通局部 / 字段或成员链 / 数组元素 / 外部 API 转发），供第三遍选工厂。

### 5.2 声明重写（第二遍）

| 原始 | 改写 |
|------|------|
| `ref T a` / `out T a`（内部） | `ByRef<T> a`（ref struct → `RefStructByRef_<Mangled(T)> a`） |
| `in T a` / `ref readonly T a` | `T a`（碰撞时保留 `in`） |
| `scoped …` | 剥离 |
| `ref T r = ref e;` | `ByRef<T> r = <proxy(e)>;` |
| `ref T F;` 字段 | `ByRef<T> F;`（初始化/构造处适配为 `new SlotByRef` 经工厂，或赋同一代理） |
| `ref T M()` / `ref readonly T M()` / ref 属性 / ref 索引器 | `ByRef<T> M()` 等 |
| `delegate void D(ref T a);` | `delegate void D(ByRef<T> a);` |
| 泛型 `void M<U>(ref U a)` | `void M<U>(ByRef<U> a)`，约束不动 |

体内对 H 槽符号 `s` 的全部使用：
- 读 `s` → `s.Value`；写 LHS `s = e` → `s.Value = e`；
- 成员 `s.M()` / `s.F` → `s.Value.M()` / `s.Value.F`；
- 索引 `s[i]` → `s.Value[i]`；
- 作为内部 ref/out 实参转发 → 直接传 `s`（见 5.3）；
- 作为外部 ref/out 实参 → `ref s.Value` / `out s.Value`（类字段取址合法，见 5.4）。

### 5.3 调用点重写（第三遍，语句级提升）

**内部方法调用**，按实参位置种类：

1. **holder 槽直传**（实参本身是 H 槽：ref/out 形参、ref 局部、ref 字段）：去 `ref`，直接传槽，零包装：`Other(ref a)` → `Other(a)`。
2. **普通局部作 ref 实参**：用访问器代理（保证方法执行中即时写回、异常语义保真）。工厂签名统一为 `Create(Func<T> getter, Action<T> setter)`：
   `M(ref x)` → `M(ByRef<T>.Create(() => x, v => x = v));`
3. **普通局部作 out 实参**（已声明）：无 seed 槽 + 调用后一次显式写回（满足 C# 明确赋值分析；访问器保证异常路径上的已写值也落地）：
   `M(out x)` →
   ```
   var __outN = ByRef<T>.Create(() => x, v => x = v);
   M(__outN);
   x = __outN.Value;
   ```
4. **声明式 out**：
   - `M(out var x)` → `var __outN = ByRef<T>.Create(); M(__outN); var x = __outN.Value;`
   - `M(out int x)` → 同上，`int x = __outN.Value;`
5. **丢弃**：`M(out _)` → `M(ByRef<T>.Create());`
6. **字段/静态字段/成员链作 ref/out 实参**（`M(ref this.f)`、`M(out obj.F)`）：访问器代理，无写回语句（字段无明确赋值约束，写入即时穿透）：
   `M(ref this.f)` → `M(ByRef<T>.Create(() => this.f, v => this.f = v));`
7. **数组元素作 ref/out 实参**：`M(ref arr[i])` → `M(ByRef<T>.Create(arr, i));`（多维/交错数组按实际下标参数生成；`T[]` 不提升；out 同形态）。
8. **in / ref readonly 实参（内部）**：去修饰直接传值。
9. **多实参**：每个 ref/out 实参各生成唯一命名临时（`SyntaxAnnotation` + 文件内递增计数器，命名 `__refN` / `__outN`），前置语句按**实参从左到右**顺序排列，调用在后，写回语句按实参逆序排在调用后（保持与原求值顺序一致的可观察副作用）。
10. **嵌套调用** `Foo(Bar(ref a), Baz(out b))`：先递归提升内层，每个含 ref/out 的实参生成前置语句与临时 holder，再组装外层调用。
11. **语句上下文**：表达式语句、局部声明初始化、`return`、`if/while/do/for` 条件、三元/二元/赋值右值、作为其它调用实参——在语句级 Visit 入口检测子树是否含需提升的内部 ref/out 调用，有则整条改写为 `Block`（前置语句 + 改写后的原语句）。提升结果必须保持：明确赋值、`using/foreach/switch/fixed` 受限作用域内的写回不跨 `}`（在受限语句外层可声明位置提升临时，必要时用括号块包裹）、短路表达式（`&&`/`||`/`??`/条件）的条件求值顺序（用块+临时变量保留短路，不得把右侧提升到无条件执行位置）。
12. **ref struct 实参**：槽直传同 1；普通局部 ref/out 用 `RefStructByRef_<Mangled>.Create(...)` + 写回 `.Value`；数组元素用数组模式工厂；不支持字段链访问器（§9）。

### 5.4 外部方法调用（保留原生互操作）

- 实参位置是普通存储：保持 `ref x` / `out x` / `in x` 原样。
- 实参位置是 H 槽（类 `ByRef<T>`）：重写为对其字段取址——`Ext(ref a)` → `Ext(ref a.Value)`；`Ext(out a)` → `Ext(out a.Value)`。类的实例字段取址不受栈生存期限制，合法。
- 数组代理等临时 holder 不直接出现在外部调用（提升临时即 H 槽语义，按上条）。
- **ref struct H 槽无法向外部 ref API 转发**（不能对 ref struct 的实例属性/字段安全取址跨越其生存期）：该组合发 `Error` 诊断并保留原表达式让构建暴露——这是 C# 语言层面的根本限制，属 §9 记录边界，不作为常规路径（SDK 中 ref struct 的外部互操作入参都是普通 `Span<T>` 局部，按"普通存储"原样保留即可）。

### 5.5 ref 返回 / ref 局部 / ref 字段

- `return ref e`：按 `e` 的位置种类返回代理——H 槽直接返回；字段/成员链返回 `ByRef<T>.Create(() => e, v => <e赋值>)`（赋值侧需可写；只读字段发 Error）；数组元素 `Create(arr, i)`；ref/out 形参返回槽本身。
- `ref T r = ref e;`：同代理选择；后续 `r` 的使用一律 `r.Value`；`r = ref e2`（重绑定）改为 `r = <proxy(e2)>;`。
- ref 属性 `=> ref f` → `=> f;`（f 是 H 槽时）或 `=> ByRef<T>.Create(...)`；外部使用点 `.Prop` → `.Prop.Value`。
- ref 字段：声明 `ByRef<T> F`；所有 `s.F` 读写按 5.2 槽规则处理；ref struct 含 `ByRef<T>` 类字段后编译器自动将其降级为普通 struct（任务书 H6.3，可接受）。
- `ref readonly` 返回同样返回 `ByRef<T>`（类型可写，调用方自律只读；后续 Java 侧可再收窄）。

### 5.6 其它

- `using Cs2jByRef;`：仅当文件发生改写或引用了支撑库类型时，在文件头（现有 using 之后、按字母序或现有风格）注入。
- 干净文件（无任何 ref/out/in/scoped/ref readonly 语法）原样返回 `Changed = false`，不注入 using、不要求 ProjectReference。
- 重写器是纯函数式：`Rewrite(source, SemanticModel, options)`，单文件可独立调用（CLI 文件模式提供独立编译模型，跨文件闭包不可见时仅做单文件内可见改写，外部/未知符号保守按外部处理）。

---

## 6. CLI：`ProjectRefOutInPreprocessor`

完全参照 [ProjectUnsignedTypePreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs) 与 [ProjectPreprocessSource.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectPreprocessSource.cs)：

- `ProjectRefOutInPreprocessRequest`（源、中间根、`VerifyBuild`、`VerifyTests`、`Verbose`、`Strict`）/ `ProjectRefOutInPreprocessResult`（每文件结果、合并诊断、统计、`RequiredRefStructTypes`、构建/测试日志摘要）。
- `IntermediateDirectoryName = ".cs2j-refoutin-src"`（与 `.cs2j-unsigned-src` / `.cs2j-no-goto-src` 等同级约定）。
- 流程：
  1. `ProjectPreprocessSource.ResolveLayout` + 复制源树（仅 `.cs` 会被改写，其余原样）；
  2. 构建覆盖解决方案内全部项目的 `CSharpCompilation`（`allowUnsafe: true`，复用 unsigned 预处理器的引用收集与 `GetBasicReferences`），逐文件取 `SemanticModel` 调 `RefOutInRewriter.Rewrite`；
  3. 汇总 `RequiredRefStructTypes` 并集；任一文件 `Changed` 时：释放 `Cs2jRefOutIn` 项目（见下），对**真正编译了被改写文件且用到 holder** 的 host 项目注入 `<ProjectReference>`（复用 unsigned 的去重/相对路径计算；host csproj 位于中间树根时追加 `<Compile Remove="Cs2jRefOutIn\**" />"`）；
  4. 可选 `dotnet restore` + `dotnet build -c Release` + `dotnet test -c Release`，日志保留；
  5. 后置自检（§8）。
- 支撑文件：
  - [Cs2jRefOutInProject.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Cs2jUnsignedMathProject.cs) 镜像：`ProjectFileName = "Cs2jRefOutIn.csproj"`、同名碰撞检查（用户方案已有同名项目则终止并给出明确错误）。
  - `RefOutInSupportFiles.cs`：csproj + `ByRef.cs` 作为嵌入资源（LogicalName `Cs2jRefOutIn.ByRef.cs` / `Cs2jRefOutIn.Cs2jRefOutIn.csproj`，在 [CSharpToJava.CLI.csproj](file:///d:/code/cs2j/src/CSharpToJava.CLI/CSharpToJava.CLI.csproj) 注册 `EmbeddedResource`）；`ReleaseAsync(destDir, refStructTypes)` 生成各 `RefStructByRef_<Mangled>.cs`。
  - 项目发现/规划排除：镜像 `Cs2jUnsignedMathProject`，在 [Program.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L716-L722) 工作空间过滤与 [MultiModulePlanner.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/MultiModulePlanner.cs) 中排除 `Cs2jRefOutIn`（不转 Java、不规划 Maven 模块、不进缓存指纹）。

### 6.1 Program.cs 集成

- 新增独立 verb `ref-out-in`（对齐 `unsigned-type`）：只跑前置转换。
  `dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- ref-out-in -s D:\csharp-sdk -d d:\cs-java-refoutin-sdk`
- `ConvertProjectOptions`：新增 `NoRefOutIn`，默认 **RefOutIn = ON**（D3）；同时暴露 `--no-ref-out-in` 供回退。
- `ConvertProject` 顺序：`UnsignedType → MakeReadOnly → EliminateGoto → RefOutIn → 主转换`（前置转换最靠近主转换，避免被后续预处理误判；实现时在 SDK 上验证 unsigned 产物与本 pass 的交互）。
- 在 `GetPreservedIntermediateDirectories` 与 `GetConversionFingerprintTokens` 注册 `.cs2j-refoutin-src` / `ref-out-in=True`。

### 6.2 单文件模式 fallback

`ref-out-in -i a.cs -o b.cs` 无项目上下文、无法注入 ProjectReference 时，把当前所需 holder 源（`ByRef.cs` + 按本文件类型生成的 `RefStructByRef_*.cs`）合并写到输出同目录 `_Cs2jRefOutIn.cs`，产物文件顶注入 `using Cs2jByRef;`。目录/解决方案模式一律走独立项目 + ProjectReference 主路径。

---

## 7. 后置自检（H7 修订）

预处理后遍历产物，用语义模型（非文本）断言：

1. 内部方法/构造/lambda/局部函数/委托/索引器（返回）/属性（返回）的声明上不再有可改写的 `ref`/`out` 修饰；`in` 仅允许出现在"碰撞保留"成员并有对应 Warning 记录。
2. 不再有内部 `ref` 返回体、`ref` 局部、`ref` 字段、`ref arr[i]` 取址（全部已代理化）。
3. 残留的 `ref`/`out`/`in` 关键字仅允许两类位置：解析为**外部符号**的调用实参，或**锁定成员**及其调用链。自检输出残留分类计数（`ExternalRefArgumentsKept` 等）作为 Info。
4. 被改写文件含 `using Cs2jByRef;`；每个用到 holder 的 host csproj 含指向 `Cs2jRefOutIn.csproj` 的 ProjectReference；中间树根存在 `Cs2jRefOutIn/` 且 host 根项目 `<Compile Remove>` 生效（无重复编译）。

---

## 8. 测试设计（红→绿）

### 8.1 Core 单测：`tests/CSharpToJava.Tests/RefOutInRewriterUnitTests.cs`

构造 SemanticModel 方式对齐 [UnsignedTypeRewriterUnitTests](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs#L9-L17)（`CSharpCompilation.Create` + `allowUnsafe: true`）；多文件/跨类型场景传多个 SyntaxTree。覆盖：

- #1–#6：`ref`/`out` 参数改写、体内 `.Value`、`in`/`ref readonly` 去修饰、`scoped` 剥离、实参转发（槽直传）；
- #7：`out var` / `out int x` / `out _` 三套提升形态与写回；
- 调用点：普通局部 ref（访问器工厂）、字段链、数组元素（数组不提升）、多实参顺序、嵌套调用、`if` 条件、`return` 右值、`&&` 短路右侧的提升顺序；
- 泛型 `ref T`；
- #9 自定义委托签名 + 赋给它的具名方法与 lambda；
- #10 内部接口+实现+override 一致改写；外部接口实现/外部基类 override/**锁定不改**（用多编译单元+元数据引用模拟）；`[DllImport] extern` 锁定；
- 外部调用保留：`int.TryParse(s, out v)` 文本不变；H 槽转发外部 API → `Ext(ref a.Value)`；
- #11/#12 ref struct：`in Span<int>` 按值；`ref Span<int>` 形参→`RefStructByRef_Span_Int`，调用点双模式工厂；mangling 表驱动用例（`ReadOnlySpan<byte>`、嵌套泛型、数组）；
- #13–#16 ref 返回（字段/数组元素/形参）、ref 局部重绑定、ref 属性、ref 索引器；
- #17 `ref int r = ref arr[0];` 断言数组仍为 `int[]`、代理为 `ByRef<int>.Create(arr, 0)`；
- #15 ref 字段改写与 `.Value` 使用；
- `in` 碰撞保留 + Warning；
- 干净文件 `Changed=false`、不注入 using；文件头 using 注入幂等；
- 统计字段在各用例上的计数断言。

### 8.2 项目级测试：`tests/CSharpToJava.ProjectTests/RefOutInPreprocessorTests.cs`

**新增独立 fixture 解决方案** `tests/SampleRefOutInV2Solution/`（`SampleRefOutInV2Lib` + `SampleRefOutInV2App`，内容拷贝到 bin 的 Content 项需在 [ProjectTests.csproj](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj) 注册），覆盖：Swap、TryParse、`out var`、泛型 holder、ref 返回属性/方法、ref 局部、数组元素 ref、ref 字段、`Span<int>` ref/in 重载、外部 BCL 调用混用（`int.TryParse`、`Volatile.Read`）。

> 不扩充既有 [SampleRefOutInSolution](file:///d:/code/cs2j/tests/SampleRefOutInSolution)：其 `SlnProject_FullConversion_AllFilesSucceed` 直接对原始 fixture 跑 Java 转换并断言每个文件 Success，加入复杂 ref 场景会冲击该既有用例；旧 fixture 保持为 Java 路径基线不动。

断言：
- 中间树根含 `Cs2jRefOutIn/`（csproj + `ByRef.cs` + 按需的 `RefStructByRef_*.cs`）；不存在内联 `_Cs2jRefOutIn.cs`；
- host csproj 含 `<ProjectReference … Cs2jRefOutIn.csproj />`；被改写文件含 `using Cs2jByRef;`；
- App 产物含 `ByRef<int>.Create` 与 `.Value` 写回/访问器；外部调用点文本保留 `out`/`ref`；
- 后置自检结果零违规；
- 在 `IsDotNetAvailable` 守卫 + `CreateTempDirectory` 模式下对中间树 `dotnet build -c Release` 退出码 0（有测试工程时 `dotnet test -c Release` 全绿）。

### 8.3 集成与回归

- [RefOutInProjectTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs) 不改动（直接构造 `ConversionPipeline`，CLI 预处理器不介入，`IntHolder`/`ObjectHolder` 断言持续成立）。
- 新增一个 CLI 层最小集成用例：`ConvertProject`（或其预处理器串联段）在默认参数下先生成 `.cs2j-refoutin-src`；`--no-ref-out-in` 时不生成。
- 全量 `dotnet test -c Release`（仓库全部测试工程）无新增失败。

---

## 9. 已知边界与限制

1. **外部/锁定成员的 ref 仍出现在产物中**（D1/D6 的必然结果）：H7 已改为分类自检；Java 转换阶段对这些残留仍走 `ArgumentTransformer.Ref.cs`，故本任务**不删除、不弱化**该分支。
2. **Java 产物中间态不可编译**（D3）：`ByRef<T>` / `RefStructByRef_*` 暂无 Java 映射、`Cs2jRefOutIn` 项目不参与 Java 转换；默认开启后经 CLI 产出的 Java 工程在 Java 侧映射落地前不可构建。回退手段 `--no-ref-out-in`。AGL 转换工作流为手动触发（workflow_dispatch），不影响 `ci.yml`。后续 spec 内容预告：java/csharptojava-compat 新增同构多态位置层次（抽象 Ref + Slot/ArrayElement/Accessor 实现，ref struct 位置在 Java 侧退化为直接对象）、TypeMappings 注册、默认切换、更新 `RefOutInProjectTests` 预期、收敛 ArgumentTransformer.Ref。
3. **ref struct 字段链取址**：判别联合只含 direct/array 两模式；对 ref struct 实例的**字段**取 ref 地址且需跨签名传递的场景（如 `ref struct S { Span<int> s; }` 中 `M(ref x.s)` 且 `M` 形参为 `RefStructByRef_*`）无法构造代理，发 `Error` 诊断。`in Span<T>` 只读场景按值不受影响。
4. **ref struct H 槽转发外部 ref API**：见 §5.4，语言层面无解，发 `Error`；常规 SDK 代码路径不触发（外部互操作的实参都是普通 Span 局部）。
5. **含 ref 字段的 ref struct 降级**：`ref struct S { public ByRef<T> F; }` 因含类字段自动变为普通 struct；若原代码把它传给要求 ref struct 的 API 会编译失败——如实报错不跳过；span-like 只读路径走 in 按值，不触发。
6. **`in` 按值拷贝**：值类型多一次拷贝，仅性能差异，语义等价；碰撞时保留 `in`（§2）。
7. **async 方法**：C# 本就禁止 ref/out 参数，无需处理。
8. **访问器代理的运行时成本**：每次 ref 实参分配代理对象 + 闭包（仅普通局部/字段链场景）；这是转译中间产物，不面向最终运行性能，后续 Java lowering 可把简单局部折叠回直接槽。
9. **单文件模式保守化**：无完整编译时跨文件符号不可见，未知符号一律按外部处理；项目模式为主路径。

---

## 10. 实施顺序与验证循环

1. **基线快照（先于任何实现）**：对原始 `D:\csharp-sdk` 两个 sln 跑 `dotnet build -c Release` / `dotnet test -c Release`，存档项目级通过/失败清单与日志（只读，不改 `D:\csharp-sdk`）。
2. Red：按 §8.1 先写失败单测（最小 `ref int a` → `ByRef<int> a` + `a.Value`），确认失败。
3. Green：Core 重写器（三遍）→ 支撑库项目 → CLI 预处理器 → 独立 verb；每批用例红→绿。
4. 项目级测试 + fixture（§8.2）。
5. Program.cs 集成（默认 ON、`--no-ref-out-in`、指纹/保留目录注册、项目排除）。
6. SDK 验证：
   ```
   dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- ref-out-in -s D:\csharp-sdk -d d:\cs-java-refoutin-sdk
   cd d:\cs-java-refoutin-sdk\.cs2j-refoutin-src
   dotnet build -c Release > sdk-refoutin-build.log 2>&1
   dotnet test  -c Release > sdk-refoutin-test.log  2>&1
   ```
   与基线逐项比对：**新增失败为零、通过项目数不下降**；两个 sln 均验证，以全量 `csharp-sdk.sln` 为准；失败回到第 3 步。
7. 全仓 `dotnet test -c Release` 无回归。
8. 提交：仅暂存本任务相关文件（spec、Core/CLI/支撑库/测试/fixture/csproj/解决方案注册），**禁止 `git add -A`**（工作区存在无关既有改动）。

### 成功标准

1. §4 矩阵 18 类全部有单测/项目测试覆盖且通过；锁定/外部边界有专项测试。
2. SDK 预处理产物相对基线**零新增构建错误、零新增测试失败、通过项目数不降**，日志归档。
3. 仓库全量 `dotnet test -c Release` 无回归。
4. `ref-out-in` 独立 verb 可用；`convert-project` 默认经此前置转换（可 `--no-ref-out-in` 回退）；自检证明内部声明零残留，残留 ref/out/in 全部归类为外部调用或锁定成员。
5. Java 侧映射为明确的后续任务（§9.2），本期不实现、不伪装已完成。
