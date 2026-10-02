# C48 params 集合显式化 —— M0 Roslyn 探针记录与 M1 实施计划

**任务**: C48（C# 13 params collections 预处理器规则）M0 探针执行 + 实施计划产出
**日期**: 2026-10-02
**分支**: `feature/preproc-c48-params-collections`（worktree `.worktrees/preproc-c48-params-collections`）
**base**: `10aae03f`（`Update c32 capture lift spec and add implementation plan`）
**设计文档**: [docs/superpowers/specs/2026-09-29-c48-params-collections-design.md](file:///d:/code/cs2j/.worktrees/preproc-c48-params-collections/docs/superpowers/specs/2026-09-29-c48-params-collections-design.md)（下称「设计」，引用其 §1.3/§2/§7/§9 与附录裁决 1-5）
**探针环境**: Roslyn `Microsoft.CodeAnalysis.CSharp` **4.12.0.0**（NuGet 包实测定版）、引用集 `Microsoft.NETCore.App.Ref 10.0.8 / net10.0`（167 个 ref 程序集）、`CSharpCompilation` + `CSharpParseOptions`，TFM net10.0 控制台程序。探针本体**不入仓**（原文件在 `%TEMP%\cs2j-c48-probe\Program.cs`，全文见附录；原始输出存 `%TEMP%\cs2j-c48-probe\probe-output.txt`）。
**运行状态**: 全部 13 个探针段（12 题外加分 1 段运行时 emission）**完整跑通、零崩溃、无静默吞掉的失败项**。

---

## 0. 一句话结论（TL;DR）

Roslyn 4.12 在 strip-default 适配器实际 parse options（未指定 LanguageVersion，Default 解析为 **CSharp13**）下，对八类 params 声明 + 三形态调用 **parse/bind/IOperation/runtime emission 全支持，硬前提（§8-1）成立，适配器 parse options 零改动**。三处对设计的**修正/钉死**：

1. **ArgumentKind 枚举成员名**：单实参正常式是 `ArgumentKind.Explicit`（设计 §2.2 写的「Default」在 Roslyn API 中不存在；另有 `DeclaredDefault` 供默认值补全位使用）——PC-2 判据实现时按 `Explicit`/`ParamArray`/`ParamCollection` 三分；
2. **裁决 5 空态选形（实测）**：`M()` 空 params **数组**态编译器 lower 为 **`Array.Empty<T>()` 缓存单例**（两次调用同实例、`ReferenceEquals(Array.Empty<int>()) == True`）；空 **接口**态（`params IEnumerable<int>`）lower 为 **`Array.Empty<T>()`（运行时类型 `Int32[]`，同实例）而非 `List<T>`**；空 **List** 态每次 `new List<int>()`（不同实例）。按裁决 5「见缓存则发缓存形」：K1/K2/K5 **空态发 `System.Array.Empty<T>()`**、K4 List 空态发 `new List<T>()`；逗号展开态恒为每次新建（实测 `False`）；
3. **ParamCollectionAttribute 形态**：源符号 `IParameterSymbol.GetAttributes()` **不**含 `ParamCollectionAttribute`（ParamArrayAttribute 同样不出现在源符号上），只在 **emit 后元数据**出现（`System.Runtime.CompilerServices.ParamCollectionAttribute`，无参构造）——PC-1 声明分类唯一判据必须是 `IsParamsCollection`；裁决 3 的「补发 Attribute」可行（无参 `new ParamCollectionAttribute()`，M1 落地）。

---

## 1. M0 探针记录（12 题逐条）

> 摘录均来自 `probe-output.txt` 原始输出；「对实现的影响」指向设计 §4 组件与规则编号（PC-1~PC-10）。

### Q1 八类声明 parse/bind 支持 —— ✅ 全绿

**结论**：八类合法 params 声明（`int[]`、`Span<int>`、`ReadOnlySpan<int>`、`List<int>`、`HashSet<int>`、五接口、`[CollectionBuilder]` 自定义类型 `CB`）在两档 parse options 下均 **0 错误 0 警告**，全部方法符号可解析。适配器 `new CSharpParseOptions(preprocessorSymbols: …)` 的 Default 档在 4.12 解析为 **`LanguageVersion.CSharp13`**（latestMajor），与显式 `Latest` 等价——**`WithLanguageVersion(LanguageVersion.Latest)` 不需要**（设计 §5-2 预期的「零改动」成立）。

**摘录**：

```text
ParseOptions A(adapter-aligned): LanguageVersion=CSharp13 (Default = latestMajor)
ParseOptions B(explicit Latest) : LanguageVersion=CSharp13
[OK] all 8 kinds parse+bind with zero errors        ← adapter-aligned 档
[OK] all 8 kinds parse+bind with zero errors        ← explicit Latest 档
  PArr: bound, param0 = int[]
  PSpan: bound, param0 = System.Span<int>
  ...
  PBuilder: bound, param0 = CB
```

**对实现的影响**：适配器/单测 parse options 维持现状；Core 单测直接用 `new CSharpParseOptions(preprocessorSymbols: ImmutableArray<string>.Empty)` 造夹具编译。

### Q2 符号 API：IsParams / IsParamsCollection / 元数据形态 —— ✅ 与设计预期一致 + 一个重要修正

**结论**：数组 params 的 `IsParamsCollection=False`、其余全部 `=True`，`IsParams` 恒为 `True`——与设计 §1.3 的文档核实完全吻合。**修正**：源符号 `GetAttributes()` 对所有 11 个参数均返回空——`ParamArrayAttribute`/`ParamCollectionAttribute` 都只在 emit 后元数据出现（见 Q13 附录段），不能作为分类判据。

**摘录**：

```text
  PArr: type=int[] IsParams=True IsParamsCollection=False attrs=[]
  PSpan: type=System.Span<int> IsParams=True IsParamsCollection=True attrs=[]
  PROSpan: type=System.ReadOnlySpan<int> IsParams=True IsParamsCollection=True attrs=[]
  PList: type=System.Collections.Generic.List<int> IsParams=True IsParamsCollection=True attrs=[]
  PHashSet: ... IsParamsCollection=True attrs=[]
  PIEnumerable: ... IsParamsCollection=True attrs=[]
  PIROCollection / PIROList / PICollection / PIList: ... IsParamsCollection=True attrs=[]
  PBuilder: type=CB IsParams=True IsParamsCollection=True attrs=[]
[INFO] no CollectionBuilderAttribute surfaced on param GetAttributes()
```

**对实现的影响**：
- `ParamsSignatureCollector`（PC-1）以 `IsParams`+`IsParamsCollection` + 类型符号为唯一判据，禁止读 attributes 判 params 性；
- 声明侧 `[CollectionBuilder]` 从**形参类型符号**的 `GetAttributes()` 读（见 Q7）。

### Q3 调用点 ArgumentKind —— ✅ 三形态齐备 + 枚举名修正

**结论**：`params int[]` 与 `params List<int>` 的四种调用形态（逗号 / 空 / 单实参 / 具名单实参）中：
- 逗号段与空实参 → **一个**合成 `IArgumentOperation`：数组 = `ArgumentKind.ParamArray`（Value = `IArrayCreationOperation`）；集合 = `ArgumentKind.ParamCollection`（Value = **`OperationKind.CollectionExpression`**，4.12 新增 operation 种类，元素表达式为其 children）；
- 单实参与具名单实参（`arr` / `a: arr`）→ `ArgumentKind.**Explicit**`（**设计 §2.2 所写 `Default` 需修正为 `Explicit`**；enum 另有 `DeclaredDefault`，供 E08 默认值补全位区分使用）；
- Value 的元素语法确如设计 R8 预判：合成 operation（`Syntax == null`），**元素必须取自源码 `ArgumentListSyntax`**。

**摘录**：

```text
[INFO] call PArr(1,2,3) -> F1.PArr(params int[])  (1 argument operation(s))
  arg -> param 'a' ArgumentKind=ParamArray
    ArrayCreation : int[]
      ArrayInitializer : <no-type>
        Literal : int  ×3
[INFO] call PArr() ...
  arg -> param 'a' ArgumentKind=ParamArray
    ArrayCreation : int[]       ← 空态 IOperation 形状（运行时 lowering 见 Q13：实为 Array.Empty 缓存）
[INFO] call PArr(arr) / PArr(a: arr) ...
  arg -> param 'a' ArgumentKind=Explicit
    LocalReference : int[]
[INFO] call PList(1,2,3) -> F1.PList(params List<int>)
  arg -> param 'a' ArgumentKind=ParamCollection
    CollectionExpression : System.Collections.Generic.List<int>
      Literal : int  ×3
[INFO] call PList() ...
  arg -> param 'a' ArgumentKind=ParamCollection
    CollectionExpression : System.Collections.Generic.List<int>
```

**对实现的影响**：`ParamsArgumentRewriter`（PC-2 判据、PC-6 直传）的三分判定落地为：`ParamArray` → K1 门控（D8）、`ParamCollection` → K2/K4/K5 构造、`Explicit` → 零改直传（含具名）；`DeclaredDefault` 位不触碰（归 E08）。合成 Value 不可 Lower 的设计假设（§2.2）被证实（`[synthesized:no-syntax]`）。

### Q4 空实参 lowering（裁决 5 关键）—— ✅ 缓存形，选形已钉死

**结论**（IOperation 形状 + Q13 运行时实证合并裁决）：
- `M()` 空数组态：IOperation 显示 `IArrayCreationOperation`（`new int[0]`），**但运行时实际 lower 为 `Array.Empty<T>()` 单例**——只看 IOperation 会误判为新建，**裁决必须以 emission 实测为准**（设计 R8 的又一实证）；
- 空接口态（`params IEnumerable<int>`）：运行时类型 **`Int32[]`**，与 `Array.Empty<int>()` 同一实例——**不是** `List<T>`，也**不是**独立空集合单例；
- 空 List 态：每次新建 `new List<int>()`（两次调用不同实例）；
- 逗号展开态（数组）：每次新建数组（`same-instance=False`）——设计 R4「缓存/静态复用假设会错」的实证。

**摘录**（P4 的 IOperation + P13 的运行时）：

```text
[INFO] PArr() ArgumentKind=ParamArray — full Value tree:
  ArrayCreation : int[]
    Literal : int
    ArrayInitializer : <no-type>
[INFO] PIEnumerable() ArgumentKind=ParamCollection — full Value tree:
  CollectionExpression : System.Collections.Generic.IEnumerable<int>
（P13 运行时：）
[INFO] empty params int[] : instance1-type=System.Int32[] len=0, same-instance-across-two-calls=True
[INFO] empty params IEnumerable<int>: runtime-type=System.Int32[], same-instance-across-two-calls=True
[INFO] empty params List<int>  : runtime-type=System.Collections.Generic.List`1[...Int32...], same-instance-across-two-calls=False
[INFO] empty int[] ReferenceEquals Array.Empty<int>(): True
[INFO] empty IEnumerable ReferenceEquals Array.Empty<int>(): True
[INFO] comma-expansion params int[] : len=2, same-instance-across-two-calls=False
[INFO] comma-expansion params IEnumerable<int>: runtime-type=<>z__ReadOnlyArray`1[[System.Int32,...]], count=3
```

**对实现的影响（裁决 5 拍板，修订设计 §2.1 空态列 / D2）**：
- **K1/K2（数组与 Span）空态**：发 **`System.Array.Empty<T>()`**（与编译器同形、ReferenceEquals 一致；取代设计 D2 的 `new T[]{}` 主选——设计 D2 本就预留「是否改发 Array.Empty 留 M0 探针」）；
- **K5（五接口）空态**：发 **`Array.Empty<T>()`**（编译器即发数组空集；`new List<T>()` 与编译器不同形且每次新建，违背裁决 5 标准）；
- **K4（List/可初始化器具体类，M2）空态**：发 `new C<T>()`（编译器即新建）；
- **逗号展开态**：一律每次新建（数组 `new T[]{…}` / List `new List<T>(){…}`），禁止任何缓存/静态复用；
- 附注：非空接口展开编译器运行时产物是内部类型 `<>z__ReadOnlyArray<T>`（源码不可构造）——C48 发 `new List<T>(){…}` 在源码层面**无编译器同形候选**，语义等价（内容/枚举一致）即为终态，M1 夹具以行为对照验收而非 IL 同形。

### Q5 数组→Span 隐式转换（K2）—— ✅ 可编译，v1 数组形态成立

**结论**：`M(params ReadOnlySpan<int>)` 与 `M(params Span<int>)` 下，`M(new int[]{1,2,3})` 与 `M(new int[]{})` 均**编译通过**（System.Memory 时代隐式引用转换，非 C# 14 新转换）；单实参 `ArgumentKind=Explicit`，Value 为 `IConversionOperation`（`implicit=True, identity=False`）包 `IArrayCreationOperation`——**编译器只做隐式转换，C48 发 `new T[]{…}` 即可，不发 `AsSpan`**（设计 K2 v1、裁决 2 收口）。

**摘录**：

```text
[OK] T[] -> Span/ReadOnlySpan params call sites compile (implicit reference conversion, non-C#14 path)
[INFO] PROSpan(new int[]{1,2,3}) ArgumentKind=Explicit
    Conversion : System.ReadOnlySpan<int> implicit=True identity=False
      ArrayCreation : int[] ...
[INFO] PSpan(new int[]{}) ArgumentKind=Explicit
    Conversion : System.Span<int> implicit=True identity=False
      ArrayCreation : int[]    ← 空数组同路径
```

**对实现的影响**：`CollectionConstructionEmitter`（PC-2）K2 列与 K1 空态同一发射形态（数组/`Array.Empty<T>()`），0604 Info 留痕即可；C06 收口前无需 Span 专用发射。

### Q6 ref struct 元素（K6）—— ✅ 声明级即非法，错误码 CS9244

**结论**：`params ReadOnlySpan<Span<int>>` 与 `params Span<RS>`（元素为 ref struct）在**声明级**即编译错误 **CS9244**（「Span<int> 不能是 ref 结构……用作泛型实参 T」）——**不是调用点错误**，K6 站点在可编译输入中不可达，C48 只需防御性识别（设计 K6/PC-2 的 0608 路径仅作为「防御 + 指路 C06」存在）。

**摘录**：

```text
[INFO] declaration-level errors:
  Error CS9244: 类型 "Span<int>" 不能是 ref 结构或允许 ref 结构的类型参数，以便在泛型类型或方法 "ReadOnlySpan<T>" 中将其用作参数 "T"
  Error CS9244: 类型 "RS" 不能是 ref 结构或允许 ref 结构的类型参数，以便在泛型类型或方法 "Span<T>" 中将其用作参数 "T"
```

**对实现的影响**：`ParamsTargetClassifier` 对 Span 元素类型做 `ITypeSymbol.IsRefLikeType` 检查；命中即 CS2J0608（Error/Warning），无发射路径需求。

### Q7 CollectionBuilder（K3）—— ✅ 编译通过；attribute 从类型符号读取

**结论**：`[CollectionBuilder(typeof(CBBuilder), "Create")]` 类型（实现 `IEnumerable<int>`）的 params 声明 + 逗号/空/单实参调用**全部编译通过**；调用点 `ArgumentKind=ParamCollection`、Value 为 `CollectionExpression : CB`（不暴露 Create 调用细节——IOperation 层停在合成集合表达式）。`CollectionBuilderAttribute` 可从**形参类型符号** `GetAttributes()` 读到，ctor 参数为 `[CBBuilder, "Create"]`（类型 + 工厂名字符串）——**参数符号上无此 attribute**（Q2 已证）。

**摘录**：

```text
[OK] params CB (CollectionBuilder type) + comma/empty/single calls compile
[INFO] PBuilder(1,2,3) ArgumentKind=ParamCollection
    CollectionExpression : CB
      Literal : int  ×3
[INFO] PBuilder() ArgumentKind=ParamCollection
    CollectionExpression : CB
[INFO] CB type attr: System.Runtime.CompilerServices.CollectionBuilderAttribute ctorArgs=[CBBuilder, "Create"]
```

**对实现的影响**：v1（M1）对 K3 站点只报 **CS2J0607**（消息含 builder 类型与工厂名，均从类型 attribute 读）；M2 经该 attribute 解析工厂方法符号（形参 `ReadOnlySpan<T>`）后发射 `BuilderType.Create(new T[]{…})`，与 C36 CE-10 共用绑定结论（设计 K3/裁决 4）。

### Q8 重载决议（D1 / C52 交界）—— ✅ 非 params 正常形参恒胜

**结论**：`M(1)` 在 `M(int)` vs `M(params int[])` 下选 **`F8.M(int)`**；`S("x")` 在 `S(string)` vs `S(params ReadOnlySpan<char>)` 下选 **`F8.S(string)`**——4.12/C# 13 规则下**正常形参优先于展开形参**，string→ROS 的既有隐式转换不改变 params 位劣势。这是 C# 14 隐式 Span 转换（C52）影响重载决议的**基线记录点**。

**摘录**：

```text
[INFO] M(1) resolves to: F8.M(int) (params=False)
  arg ArgumentKind=Explicit value=Literal
[INFO] S("x") resolves to: F8.S(string) (params=False)
  arg ArgumentKind=Explicit value=Literal
```

**对实现的影响**：D1 落实——C48 完全信任 `IInvocationOperation.TargetMethod`，不自行复刻决议；M1 单测把本结论钉为回归基线；C52 批次在此基线上再测 4.14 行为漂移。`ReadOnlySpan<char>` 逗号展开的 char 面维持 v1 Scope 排除（裁决 2）。

### Q9 基线非法形态（K7/K8）—— ✅ 错误码齐备

**结论**：四类基线非法声明的错误码：

| 形态 | 错误码 |
|---|---|
| `params StringBuilder`（无 Add、非 `IEnumerable<T>`） | **CS0225**（Params 参数必须具有有效的集合类型） |
| `params` 非末位 | **CS0231** |
| 多维数组 `params int[,]` | **CS0225** |
| 非泛型 `System.Collections.IEnumerable` | **CS0225** |

```text
[INFO] params StringBuilder: error codes = [CS0225]
[INFO] params non-last: error codes = [CS0231]
[INFO] params int[,]: error codes = [CS0225]
[INFO] params non-generic IEnumerable: error codes = [CS0225]
```

**对实现的影响**：K7/K8 在可编译输入不可达，`ParamsTargetClassifier` 防御分支（error type/表外类型）→ CS2J0601 即可；单测「排除/防御」项用这些码做夹具断言（设计 §7.1-14）。

### Q10 virtual 族一致性（PC-8）—— ✅ 全族旗标一致

**结论**：基类 virtual / 派生 override / 抽象声明 / 接口声明 / 接口实现的 params 集合参数，`IsParams=True IsParamsCollection=True` **全族一致**——策略 B（剥离物化）整族处理有统一判据。

```text
  B10.M: ... IsParams=True IsParamsCollection=True attrs=[]
  B10.N: ... IsParams=True IsParamsCollection=True attrs=[]
  D10.M: ... IsParams=True IsParamsCollection=True attrs=[]
  D10.N: ... IsParams=True IsParamsCollection=True attrs=[]
  I10.M: ... IsParams=True IsParamsCollection=True attrs=[]
  Impl10.M: ... IsParams=True IsParamsCollection=True attrs=[]
```

**对实现的影响**：`ParamsDeclarationMaterializer`（PC-8）沿 `OverriddenMethod` 链 + `AllInterfaces`/`ExplicitInterfaceImplementations` 归组整族物化（0612 fail-fast，裁决 3）；族内一致性断言进单测 12。

### Q11 ctor 链展开（PC-7/PC-2）—— ✅ 合法，路径已钉

**结论**：`C11(int a, params IEnumerable<int> rest)` + `C11(int a) : this(a, 1, 2, 3)` **编译通过**；IOperation 路径：`IConstructorBodyOperation.Initializer` 是 **`IExpressionStatementOperation` 包裹 `IInvocationOperation`**（不能直接当 Invocation 取！），展开实参在 `Invocation.Arguments`：固定槽 `a` = `Explicit(ParameterReference)`，params 槽 `rest` = **`ParamCollection` + `CollectionExpression`**。注意：无显式初始化器的 ctor 有隐式 `object.Object()` base 调用（同样走该路径）。

**摘录**：

```text
[OK] ctor chain with params-collection expansion compiles
[INFO] ctor C11(int a): Initializer kind=ExpressionStatement inner=Invocation
  target: C11.C11(int, params System.Collections.Generic.IEnumerable<int>)
  arg -> param 'a' (int) ArgumentKind=Explicit
      ParameterReference : int
  arg -> param 'rest' (...) ArgumentKind=ParamCollection
      CollectionExpression : System.Collections.Generic.IEnumerable<int>
        Literal : int  ×3
[INFO] ctor C11(int a,params ... rest): Initializer kind=ExpressionStatement inner=Invocation
  target: object.Object()      ← 隐式 base 调用也在此路径
```

**对实现的影响**：`ParamsArgumentRewriter` 的 ctor-init 挂点实现为：`IConstructorBodyOperation.Initializer` → 解包 `IExpressionStatementOperation` → 取内层 `IInvocationOperation` → 与普通 invocation 同一套判定；`VisitPrimaryConstructorBaseType` 同理（设计 §4 已列）。

### Q12 集合表达式组合（D4 / C36 交界）—— ✅ 全部 Explicit，直传判定成立

**结论**：`M([1,2])`、`M([..s])` 传给 params 集合形参、以及 `[1,2]`/`[..s]` 传给 `params int[]` 形参，**全部 `ArgumentKind=Explicit`**（单实参正常式，非 ParamArray/ParamCollection）——C48 一律直传不触达，D4 的「集合表达式是用户显式单实参」成立，且**与 pass 顺序无关**（枚举成员名修正：判据是 `Explicit` 而非设计稿的 `Default`）。Value 形态 = implicit `IConversionOperation` 包 `CollectionExpression`（spread 时含 `Spread` 子节点）——**`[1,2]`/`..s` 的去糖完全归 C36**。

**摘录**：

```text
[INFO] PIEnumerable([1,2]) ArgumentKind=Explicit
    Conversion : System.Collections.Generic.IEnumerable<int> implicit=True identity=False
      CollectionExpression : System.Collections.Generic.IEnumerable<int>
        Literal : int  ×2
[INFO] PIEnumerable([..s]) ArgumentKind=Explicit
    Conversion : ... implicit=True identity=False
      CollectionExpression : ...
        Spread : <no-type>
          LocalReference : int[]
[INFO] PArr([1,2]) / PArr([..s]) — ArgumentKind=Explicit（同构）
```

**对实现的影响**：PC-6「Explicit 零改」天然防双重包裹（R2），§3.3 双保险成立；0611 边缘（展开段内嵌 spread）仅当 C36 未跑时理论上可达，M1 不实现、M2 探针复测。

### Q13（加分段）运行时 emission 实证 —— 元数据属性形态（裁决 3 / Q3 开放问题）

**结论**：emit 后元数据中，`params int[]` 参数带 `[System.ParamArrayAttribute]`；params 集合参数带 **`[System.Runtime.CompilerServices.ParamCollectionAttribute]`**（无参构造）——裁决 3「为剥离后的参数补发 ParamCollectionAttribute」落地方式 = 物化时对原 params 集合参数补 `new ParamCollectionAttribute()`（M1 范围内可做；JL/反射面评估另记）。运行时空态同一性数据见 Q4。

```text
[INFO] emitted metadata attributes:
  A(params int[]): [System.ParamArrayAttribute]
  E(params IEnumerable<int>): [System.Runtime.CompilerServices.ParamCollectionAttribute]
  L(params List<int>): [System.Runtime.CompilerServices.ParamCollectionAttribute]
```

---

## 2. 对设计文档的修订建议清单（供评审，不改设计原文）

| # | 设计位置 | 原文 | M0 实测修订 |
|---|---|---|---|
| 1 | §2.2 判据表 | `Default`（正常式实参） | 枚举成员名为 **`Explicit`**（另有 `DeclaredDefault` = 默认值补全位）；PC-2/PC-6 按三分 `Explicit`/`ParamArray`/`ParamCollection` 实现 |
| 2 | §2.1 K1/K5 空态列、D2 | `M(new T[]{})` / `M(new List<T>())` | 裁决 5 依探针拍板：K1/K2/K5 空态发 **`Array.Empty<T>()`**（缓存单例，同形）；K4 List 空态发 `new List<T>()`（编译器即新建） |
| 3 | §1.3「参数 GetAttributes 带 ParamCollectionAttribute」 | 源符号可读 | 源符号 `GetAttributes()` 为空；属性只在 **emit 后元数据**（无参构造）——声明分类必须用 `IsParamsCollection`；裁决 3 补发属性可行 |
| 4 | §2.1 K6 行 | 调用点错误 | **声明级 CS9244**，可编译输入不可达；0608 纯防御 |
| 5 | §4 ParamsArgumentRewriter | ctor-init 取「上游调用 operation」 | 具体路径：`IConstructorBodyOperation.Initializer`（`IExpressionStatementOperation`）→ 内层 `IInvocationOperation` |
| 6 | §3.4/§5-2 ParseOptions | 可能需 `WithLanguageVersion(Latest)` | **零改动**（Default=latestMajor=CSharp13 实测确认） |

---

## 3. M1 实施任务分解（K1/K2/K5 + 空态 + E08 协作，规则默认关）

> 范围对齐设计 §9-M1：PC-1/2/4/5/6/7/8/10；K4（具体初始化器类 Add 绑定）与 K3（builder 发射）为 M2，**v1 对 K3 一律报 CS2J0607**。目录 `src/CSharpToJava.Core/DefaultParameterStripper/ParamsCollections/`，命名空间 `CSharpToJava.Core.DefaultParameterStripper.ParamsCollections`。每项标注依赖的探针结论（Q1-Q13）。
> 纪律：规则 `Enabled=false` 默认关；E08 既有 10 文件仅追加式接缝；E08 既有测试零改动；每任务完成即 `dotnet build` 全绿。

### 3.1 基础设施与选项

- [ ] **T1** `ParamsCollectionsOptions.cs`：`Enabled(false)/Strict(true)/Scope(All)/ExpandArrayCallSites(false)/Builder(Error)` + `ParamsCollectionsScope { All, Span, MutableCollection, Interface, CollectionBuilder }`；null/disabled = 零行为（依赖 Q1 全形态可编译；灰度面 K2/K5 先行 §8-4）
- [ ] **T2** `ParamsModels.cs`：`ParamsParamKind { Array, Span, Builder, Initializable, Interface, Unsupported }`、`ParamsSitePlan`（目标 `IMethodSymbol`、params 槽 index、形参类型符号、Kind、展开段元素语法表、形态 A/B/C、Outcome）、`ParamsDeclPlan`（策略 A/B + virtual 族符号集）（依赖 Q2 判据、Q3 形态）
- [ ] **T3** `ParamsDiagnostics.cs`：CS2J0600–0619 常量与消息模板（v1 生效码：0600/0601/0602/0603/0604/0605/0606/0607/0609/0610/0612/0613/0619；0608 防御路径）
- [ ] **T4** `ParamsStatistics.cs`：`ParamsSites`(按 Kind)、`ParamsCallSitesExpanded/Empty/Passthrough`、`ParamsDeclarationsMaterialized`、`ArrayCallSitesExpanded`、`SpanAsArraySites`、`BuilderSitesBlocked`、`SkippedByReason`、`ResidualCount`

### 3.2 采集与分类（Collect 遍）

- [ ] **T5** `ParamsTargetClassifier.cs`：类型符号分类——一维数组校验、`System.Span`/`System.ReadOnlySpan` 全名匹配、CollectionBuilder attribute 读取（**从形参类型符号 GetAttributes()，ctor=[Type,string]**，Q2/Q7）、五接口集合、初始化器模式（IEnumerable<T>+无参 ctor+可访问 Add，K4 仅分类不发射）、ref struct 元素 `IsRefLikeType`（Q6）→ Kind + 规范具体类（五接口→`List<T>`）
- [ ] **T6** `ParamsSignatureCollector.cs`：沿 E08 `DefaultParameterCatalog.Collect` 已登记符号读末位参数 `IsParams`/`IsParamsCollection`（**Q2：源符号无 attribute，禁读 attributes**）；建 `IMethodSymbol → ParamsDeclPlan`；virtual 槽位族归组（`OverriddenMethod` 链 + `AllInterfaces`/`ExplicitInterfaceImplementations`，Q10 一致性）；表外/error type → 0601（Q9 码表）

### 3.3 调用点改写（Strip 遍，R-b 挂点）

- [ ] **T7** `ParamsArgumentRewriter.cs` 核心：invocation/object-creation 取 `IInvocationOperation`/`IObjectCreationOperation`；ctor-init/primary-base 走 **Q5/§2-5 修订路径**：`IConstructorBodyOperation.Initializer` → 解包 `IExpressionStatementOperation` → 内层 `IInvocationOperation`（Q11）；逐 `IArgumentOperation` 判 `ArgumentKind`：`Explicit` → 直传零改（PC-6，Q3/Q12）；`ParamArray` → K1 门控（T9）；`ParamCollection` → K2/K4/K5 分派（T8）；槽位对齐复用 E08 slots（D5 先 E08 补全后 C48）；IOperation 与语法槽位冲突 → 0610
- [ ] **T8** `CollectionConstructionEmitter.cs`（M1 面 = 数组 + Array.Empty + List）：K1/K2 展开态 `ArrayCreationExpression`+initializer（Q5：K2 发数组、不发 AsSpan）；**空态按裁决 5：K1/K2/K5 发 `System.Array.Empty<T>()`、K4 发 `new C<T>()`**（Q4/Q13）；K5 展开态 `new List<T>(){…}`（Q4 附注：语义等价终态，IL 层无编译器同形候选）；元素语法一律取自源码 `ArgumentList`（Q3：合成 Value 无 Syntax）；零临时表达式
- [ ] **T9** K1 门控（D8）：`ExpandArrayCallSites=false` 时 ParamArray 站点零改（0603 计数）；开启后逗号/空显式化、Explicit 恒直传（Q3）
- [ ] **T10** K2 Span 分派：数组形态发射 + 0604 Info；`ReadOnlySpan<char>` 面 v1 Scope 排除（裁决 2，Q8 基线记录）；ref struct 元素 → 0608（Q6）
- [ ] **T11** K5 接口分派：规范具体类 `List<T>`（Q4；复用 C36 T5 表结论，未合入则本地记录）；五接口逐一点测重编译
- [ ] **T12** K3 阻断（v1）：命中 builder 类型（Q7 attribute 读取）→ 0607 Error（strict）/Warning，消息含 builder 类型 + 工厂名，站点不改写
- [ ] **T13** 具名/默认组合（D5/PC-7）：`M(1, b:2)`、`M(a: existing)`、E08 补全位与 `DeclaredDefault`（Q3 修正注记）不触 params 分派

### 3.4 声明物化与 E08 接缝（R-a 挂点）

- [ ] **T14** `ParamsDeclarationMaterializer.cs`（PC-8/策略 B）：非数组 params 去 `ParamsKeyword`（`WithModifiers`）；virtual 族整族一致（Q10）；部分符号不可解析 → **0612 fail-fast 整族报错**（裁决 3）；**补发 `ParamCollectionAttribute`（无参构造，Q13 实证形态）**；K1 声明不动
- [ ] **T15** `ParamsOverloadForwarding.cs`（PC-7）：替换 `DefaultParameterOverloadBuilder` 仅认 `ArrayTypeSyntax` 的空槽分支为按 Kind 分派（数组 → `Array.Empty<T>()` 按 T4 裁决 5 选形；List → `new List<T>()`；builder → 0607 阻断路径）；0603/0613 计数（设计 §3.2 R-a 顺序：先 E08 转发重载再物化 params）
- [ ] **T16** `DefaultParameterStripperOptions` 增 `ParamsCollections?` 属性、`DefaultParameterStripper` 的 Collect 后接 T6、`IsDirty` 增 params 粗扫析取支、`DefaultParameterDeclarationRewriter` 两个挂点各数行委托（全部追加式，默认关零 diff）
- [ ] **T17** `ParamsResidualVerifier.cs`（PC-10）：输出树重 parse + 语义复核——非数组 params 残留必须有 skip 配对；二次 Strip `Changed=false`；D8 开启时无未处理 ParamArray 站点；0619 闸门

### 3.5 CLI 接线

- [ ] **T18** `PassChainFactory`/`Program.cs`：`--enable-params-collections`（默认 false）+ 可选 `--params-collections-scope`/`--params-collections-expand-arrays` 透传到 T1；独立 verb `strip-default` 同名选项；无新 pass id/目录/适配器；**parse options 零改动**（Q1）
- [ ] **T19** 摘要行 + 统计合并：`BuildChainSummaryLine` 追加 params 段（规则启用时）；`MergeStatistics` 并入 T4 字段

### 3.6 测试（对齐设计 §7.1/§7.2，M1 子集）

- [ ] **T20** Core 单测 `tests/CSharpToJava.ProjectTests/ParamsCollectionsUnitTests.cs`：§7.1 矩阵 1-5/7/8/9/12/15（K1 两档、K2 Span 三形态+空态+可写、K5 五接口、形态 B 同一性、空态八类中 M1 可达类、默认/具名组合、声明物化 virtual 族、零误伤/幂等）；parse options 与适配器一致（Q1）；**空态断言文本 = `System.Array.Empty<T>()`（裁决 5 修订）**；Q9 错误码防御夹具
- [ ] **T21** 项目级 fixture（并入 `SampleStripDefaultSolution`，`Params*` 前缀）：.NET 半区行为对照——内容/顺序/Count、空态、形态 B 同一性（ReferenceEquals 半区：**空数组两次调用同实例**，Q4）、Span 读写、副作用顺序
- [ ] **T22** Release build/test 三工程全绿；E08 既有测试零改动；`--list-passes` 无新增行；residual 闸门全绿

**依赖顺序**：T1-T4 → T5-T6 → T7-T13 → T14-T17 → T18-T19 → T20-T22（T14/T15 可与 T7-T13 并行）；M1 完成线 = T22 全绿 + 幂等/residual 闸门 + 设计 §8 门槛未翻转（Enabled 仍 false）。

---

## 4. 附录：M0 探针源码全文（不入仓，原文件 `%TEMP%\cs2j-c48-probe\Program.cs`）

工程文件 `cs2j-c48-probe.csproj`：TFM `net10.0`，`<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.12.0" />`。原始完整输出见随探针留存的 `probe-output.txt`（摘要已内嵌上文各节）。

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;

// ============================================================================
// C48 M0 Roslyn 4.12 params-collections probe (NOT part of cs2j repo)
// 12 questions; one [OK]/[FAIL]/[INFO] per finding; crashes are caught, not swallowed.
// ============================================================================

Console.WriteLine("### C48 M0 probe — Roslyn params collections");
var roslynAsm = typeof(CSharpParseOptions).Assembly;
Console.WriteLine($"Roslyn: {roslynAsm.GetName().Name} v{roslynAsm.GetName().Version}");

var refs = LoadRefs();
Console.WriteLine($"Refs: {refs.Count()} ref-pack assemblies (Microsoft.NETCore.App.Ref net10.0)");

// adapter-aligned options: new CSharpParseOptions(preprocessorSymbols: ...) WITHOUT LanguageVersion
var optAdapter = new CSharpParseOptions(preprocessorSymbols: ImmutableArray<string>.Empty);
var optLatest = new CSharpParseOptions(LanguageVersion.Latest);
Console.WriteLine($"ParseOptions A(adapter-aligned): LanguageVersion={optAdapter.LanguageVersion} (Default = latestMajor)");
Console.WriteLine($"ParseOptions B(explicit Latest) : LanguageVersion={optLatest.LanguageVersion}");

Guard("P1 parse/bind of 8 legal params kinds (adapter opts + Latest)", () => P1());
Guard("P2 IParameterSymbol.IsParams / IsParamsCollection / attributes", () => P2());
Guard("P3 call-site ArgumentKind (ParamArray/ParamCollection/Default)", () => P3());
Guard("P4 empty-argument lowering shape (adjudication R5)", () => P4());
Guard("P5 array -> Span implicit conversion at call sites", () => P5());
Guard("P6 ref-struct element params (expected CS errors)", () => P6());
Guard("P7 [CollectionBuilder] params + attribute readability", () => P7());
Guard("P8 overload resolution M(int) vs M(params int[]), M(string) vs M(params ROS<char>)", () => P8());
Guard("P9 baseline-illegal declarations (error codes)", () => P9());
Guard("P10 virtual/override/interface param flag consistency", () => P10());
Guard("P11 ctor chain : this(a, 1, 2, 3) expansion", () => P11());
Guard("P12 collection expressions [1,2] / [..s] into params slots", () => P12());
Guard("P13 runtime emission: empty-call identity + metadata attributes (R5/Q3)", () => P13());

Console.WriteLine();
Console.WriteLine("### probe complete");

// ============================================================================
// helpers
// ============================================================================

static void Guard(string name, Action a)
{
    Console.WriteLine();
    Console.WriteLine("### " + name);
    try { a(); }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] probe crashed: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.ToString());
    }
}

static IEnumerable<MetadataReference> LoadRefs()
{
    var root = Path.Combine(@"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref");
    if (!Directory.Exists(root)) throw new InvalidOperationException("ref pack root not found: " + root);
    var packs = Directory.GetDirectories(root)
        .Select(d => (dir: d, ver: Version.Parse(Path.GetFileName(d))))
        .OrderByDescending(x => x.ver)
        .ToList();
    foreach (var (dir, ver) in packs)
    {
        var refRoot = Path.Combine(dir, "ref");
        if (!Directory.Exists(refRoot)) continue;
        var apiDir = Directory.GetDirectories(refRoot).First();
        Console.WriteLine($"[INFO] using ref pack {ver} / {Path.GetFileName(apiDir)}");
        return Directory.GetFiles(apiDir, "*.dll").Select(d => MetadataReference.CreateFromFile(d)).ToArray();
    }
    throw new InvalidOperationException("no usable ref pack found under " + root);
}

static CSharpCompilation Compile(string name, string source, CSharpParseOptions po, IEnumerable<MetadataReference> refs, out ImmutableArray<Diagnostic> diags)
{
    var tree = CSharpSyntaxTree.ParseText(source, po);
    var comp = CSharpCompilation.Create(name, new[] { tree }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    diags = comp.GetDiagnostics();
    return comp;
}

static string DiagSummary(ImmutableArray<Diagnostic> diags, DiagnosticSeverity min = DiagnosticSeverity.Warning)
{
    var sb = new StringBuilder();
    foreach (var d in diags.Where(d => d.Severity >= min))
        sb.AppendLine($"  {d.Severity} {d.Id}: {d.GetMessage()}");
    return sb.Length == 0 ? "  (no errors/warnings)" : sb.ToString();
}

static void DumpOp(IOperation op, int depth)
{
    if (op is null) { Console.WriteLine(new string(' ', depth * 2) + "<null>"); return; }
    var indent = new string(' ', depth * 2);
    var type = op.Type?.ToDisplayString() ?? "<no-type>";
    var tag = op.Syntax is null ? " [synthesized:no-syntax]" : "";
    var extra = "";
    if (op is IConversionOperation conv) extra = $" implicit={conv.Conversion.IsImplicit} identity={conv.Conversion.IsIdentity}";
    Console.WriteLine($"{indent}{op.Kind} : {type}{extra}{tag}");
    foreach (var c in op.ChildOperations) DumpOp(c, depth + 1);
}

static void PrintArgs(IInvocationOperation inv)
{
    foreach (var arg in inv.Arguments)
    {
        var pname = arg.Parameter?.Name ?? "<no-param>";
        var ptype = arg.Parameter?.Type?.ToDisplayString() ?? "<no-type>";
        Console.WriteLine($"  arg -> param '{pname}' ({ptype}) ArgumentKind={arg.ArgumentKind}");
        DumpOp(arg.Value, 3);
    }
}

static IMethodSymbol GetMethodByName(Compilation comp, string typeName, string methodName)
{
    foreach (var tree in comp.SyntaxTrees)
    {
        var model = comp.GetSemanticModel(tree);
        foreach (var t in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var sym = model.GetDeclaredSymbol(t);
            if (sym?.Name != typeName) continue;
            var m = sym.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
            if (m != null) return m;
        }
    }
    throw new InvalidOperationException($"method {typeName}.{methodName} not found");
}

static void PrintParamFlags(string label, IParameterSymbol p)
{
    var attrs = p.GetAttributes().Select(a => a.AttributeClass?.ToDisplayString()).ToList();
    Console.WriteLine($"  {label}: type={p.Type.ToDisplayString()} IsParams={p.IsParams} IsParamsCollection={p.IsParamsCollection} attrs=[{string.Join(", ", attrs)}]");
}

static (ImmutableArray<IArgumentOperation> args, IMethodSymbol target) InvOf(Compilation comp, string containingType, string callText)
{
    foreach (var tree in comp.SyntaxTrees)
    {
        var model = comp.GetSemanticModel(tree);
        foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!inv.ToString().Contains(callText)) continue;
            var encType = inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var encSym = encType is null ? null : model.GetDeclaredSymbol(encType);
            if (encSym?.Name != containingType) continue;
            var op = model.GetOperation(inv) as IInvocationOperation;
            if (op is null) continue;
            return (op.Arguments, op.TargetMethod);
        }
    }
    throw new InvalidOperationException($"call '{callText}' in {containingType} not bound");
}

// ============================================================================
// fixtures
// ============================================================================

const string FixtureDecls = """
public static class F1 {
  public static void PArr(params int[] a) {}
  public static void PSpan(params System.Span<int> a) {}
  public static void PROSpan(params System.ReadOnlySpan<int> a) {}
  public static void PList(params System.Collections.Generic.List<int> a) {}
  public static void PHashSet(params System.Collections.Generic.HashSet<int> a) {}
  public static void PIEnumerable(params System.Collections.Generic.IEnumerable<int> a) {}
  public static void PIROCollection(params System.Collections.Generic.IReadOnlyCollection<int> a) {}
  public static void PIROList(params System.Collections.Generic.IReadOnlyList<int> a) {}
  public static void PICollection(params System.Collections.Generic.ICollection<int> a) {}
  public static void PIList(params System.Collections.Generic.IList<int> a) {}
  public static void PBuilder(params CB a) {}
}
""";

const string FixtureCbTypes = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
[CollectionBuilder(typeof(CBBuilder), "Create")]
public class CB : IEnumerable<int> {
  private readonly int[] _items;
  public CB(int[] items) { _items = items; }
  public IEnumerator<int> GetEnumerator() { foreach (var i in _items) yield return i; }
  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
public static class CBBuilder {
  public static CB Create(ReadOnlySpan<int> items) => new CB(items.ToArray());
}
""";

// usings must precede all type declarations (CS1529) -> CB types first
const string FixtureBase = FixtureCbTypes + FixtureDecls;

const string FixtureP3 = FixtureBase + """

public static class Driver3 {
  public static void Run() {
    F1.PArr(1,2,3);
    F1.PArr();
    var arr = new int[]{9};
    F1.PArr(arr);
    F1.PArr(a: arr);
    F1.PList(1,2,3);
    F1.PList();
    var lst = new System.Collections.Generic.List<int>{7};
    F1.PList(lst);
    F1.PList(a: lst);
  }
}
""";

const string FixtureP4 = FixtureBase + """

public static class Driver4 {
  public static void Run() {
    F1.PArr();
    F1.PIEnumerable();
    F1.PROSpan();
    F1.PList();
  }
}
""";

const string FixtureP5 = FixtureBase + """

public static class Driver5 {
  public static void Run() {
    F1.PROSpan(new int[]{1,2,3});
    F1.PROSpan(new int[]{});
    F1.PSpan(new int[]{1,2});
    F1.PSpan(new int[]{});
  }
}
""";

const string FixtureP6Decl = """
public ref struct RS { }
public static class F6 {
  public static void M(params System.ReadOnlySpan<System.Span<int>> s) {}
  public static void N(params System.Span<RS> s) {}
}
""";

const string FixtureP6Call = """
public ref struct RS { }
public static class F6 {
  public static void M(params System.ReadOnlySpan<System.Span<int>> s) {}
  public static void N(params System.Span<RS> s) {}
}
public static class Driver6 {
  public static void Run() {
    System.Span<int> s1 = stackalloc int[1];
    System.Span<int> s2 = stackalloc int[1];
    M(s1, s2);
    RS rs1 = default;
    RS rs2 = default;
    N(rs1, rs2);
  }
}
""";

const string FixtureP7 = FixtureCbTypes + FixtureDecls + """

public static class Driver7 {
  public static void Run() {
    F1.PBuilder(1,2,3);
    F1.PBuilder();
    var cb = new CB(new int[]{5});
    F1.PBuilder(cb);
  }
}
""";

const string FixtureP8 = """
public static class F8 {
  public static void M(int x) {}
  public static void M(params int[] a) {}
  public static void S(string x) {}
  public static void S(params System.ReadOnlySpan<char> x) {}
}
public static class Driver8 {
  public static void Run() { F8.M(1); F8.S("x"); }
}
""";

const string FixtureP10 = """
using System.Collections.Generic;
public abstract class B10 {
  public virtual void M(params IEnumerable<int> a) {}
  public abstract void N(params IEnumerable<int> a);
}
public class D10 : B10 {
  public override void M(params IEnumerable<int> a) {}
  public override void N(params IEnumerable<int> a) {}
}
public interface I10 { void M(params IEnumerable<int> a); }
public class Impl10 : I10 { public void M(params IEnumerable<int> a) {} }
""";

const string FixtureP11 = FixtureBase + """

public class C11 {
  public C11(int a, params System.Collections.Generic.IEnumerable<int> rest) {}
  public C11(int a) : this(a, 1, 2, 3) {}
  public C11() : this(0) {}
}
public static class Driver11 {
  public static void Run() { var c = new C11(); }
}
""";

const string FixtureP12 = FixtureBase + """

public static class Driver12 {
  public static void Run() {
    F1.PIEnumerable([1,2]);
    int[] s = [3,4];
    F1.PIEnumerable([..s]);
    F1.PArr([1,2]);
    F1.PArr([..s]);
  }
}
""";

const string FixtureP13 = """
using System.Collections.Generic;
public static class Rt {
  public static int[] CapArr;
  public static IEnumerable<int> CapEnum;
  public static List<int> CapList;
  public static void A(params int[] a) { CapArr = a; }
  public static void E(params IEnumerable<int> a) { CapEnum = a; }
  public static void L(params List<int> a) { CapList = a; }
}
public static class RtDriver {
  public static void RunEmpty() { Rt.A(); Rt.E(); Rt.L(); }
  public static void RunExpanded() { Rt.A(1,2); }
  public static void RunExpandedIEnum() { Rt.E(1,2,3); }
}
""";

// ============================================================================
// probes
// ============================================================================

void P1()
{
    foreach (var (label, po) in new[] { ("adapter-aligned", optAdapter), ("explicit Latest", optLatest) })
    {
        var src = FixtureBase;
        var comp = Compile("P1_" + label, src, po, refs, out var diags);
        var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"[INFO] {label}: {errors.Count} errors, {diags.Length - errors.Count} other diagnostics");
        Console.WriteLine(errors.Count == 0 ? "[OK] all 8 kinds parse+bind with zero errors" : "[FAIL] errors:");
        Console.WriteLine(DiagSummary(diags, DiagnosticSeverity.Warning));
        // also assert each declared method actually resolves
        foreach (var m in new[] { "PArr", "PSpan", "PROSpan", "PList", "PHashSet", "PIEnumerable", "PIROCollection", "PIROList", "PICollection", "PIList", "PBuilder" })
        {
            var sym = GetMethodByName(comp, "F1", m);
            Console.WriteLine($"  {m}: bound, param0 = {sym.Parameters[0].Type.ToDisplayString()}");
        }
    }
}

void P2()
{
    var comp = Compile("P2", FixtureBase, optAdapter, refs, out var diags);
    Console.WriteLine("[INFO] compile: " + (diags.Any(d => d.Severity == DiagnosticSeverity.Error) ? DiagSummary(diags) : "0 errors"));
    foreach (var m in new[] { "PArr", "PSpan", "PROSpan", "PList", "PHashSet", "PIEnumerable", "PIROCollection", "PIROList", "PICollection", "PIList", "PBuilder" })
    {
        var sym = GetMethodByName(comp, "F1", m);
        PrintParamFlags(m, sym.Parameters[0]);
    }
    var attr = GetMethodByName(comp, "F1", "PBuilder").Parameters[0]
        .GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CollectionBuilderAttribute");
    if (attr != null)
    {
        var builderType = (INamedTypeSymbol?)attr.ConstructorArguments[0].Value;
        var factoryName = (string?)attr.ConstructorArguments[1].Value;
        Console.WriteLine($"[OK] CollectionBuilderAttribute on PBuilder param ctor-args: type={builderType?.ToDisplayString()}, factory=\"{factoryName}\"");
    }
    else
    {
        Console.WriteLine("[INFO] no CollectionBuilderAttribute surfaced on param GetAttributes()");
    }
}

void P3()
{
    var comp = Compile("P3", FixtureP3, optAdapter, refs, out var diags);
    Console.WriteLine("[INFO] compile: " + (diags.Any(d => d.Severity == DiagnosticSeverity.Error) ? DiagSummary(diags) : "0 errors"));
    foreach (var call in new[] { "PArr(1,2,3)", "PArr()", "PArr(arr)", "PArr(a: arr)", "PList(1,2,3)", "PList()", "PList(lst)", "PList(a: lst)" })
    {
        try
        {
            var (args, target) = InvOf(comp, "Driver3", call);
            Console.WriteLine($"[INFO] call {call} -> {target.ToDisplayString()}  ({args.Length} argument operation(s))");
            foreach (var arg in args)
            {
                var pname = arg.Parameter?.Name ?? "<no-param>";
                Console.WriteLine($"  arg -> param '{pname}' ArgumentKind={arg.ArgumentKind}");
                DumpOp(arg.Value, 2);
            }
        }
        catch (InvalidOperationException ex) { Console.WriteLine($"[FAIL] {ex.Message}"); }
    }
}

void P4()
{
    var comp = Compile("P4", FixtureP4, optAdapter, refs, out var diags);
    Console.WriteLine("[INFO] compile: " + (diags.Any(d => d.Severity == DiagnosticSeverity.Error) ? DiagSummary(diags) : "0 errors"));
    foreach (var call in new[] { "PArr()", "PIEnumerable()", "PROSpan()", "PList()" })
    {
        try
        {
            var (args, target) = InvOf(comp, "Driver4", call);
            var a = args.Single();
            Console.WriteLine($"[INFO] {call} ArgumentKind={a.ArgumentKind} — full Value tree:");
            DumpOp(a.Value, 1);
        }
        catch (InvalidOperationException ex) { Console.WriteLine($"[FAIL] {ex.Message}"); }
    }
    Console.WriteLine("[INFO] (runtime identity of these empty shapes is measured in P13)");
}

void P5()
{
    var comp = Compile("P5", FixtureP5, optAdapter, refs, out var diags);
    var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Console.WriteLine(errors.Count == 0
        ? "[OK] T[] -> Span/ReadOnlySpan params call sites compile (implicit reference conversion, non-C#14 path)"
        : "[FAIL] compile errors:\n" + DiagSummary(diags));
    foreach (var call in new[] { "PROSpan(new int[]{1,2,3})", "PROSpan(new int[]{})", "PSpan(new int[]{1,2})", "PSpan(new int[]{})" })
    {
        try
        {
            var (args, target) = InvOf(comp, "Driver5", call);
            var a = args.Single();
            Console.WriteLine($"[INFO] {call} ArgumentKind={a.ArgumentKind}");
            DumpOp(a.Value, 2);
        }
        catch (InvalidOperationException ex) { Console.WriteLine($"[FAIL] {ex.Message}"); }
    }
}

void P6()
{
    var compDecl = Compile("P6decl", FixtureP6Decl, optAdapter, refs, out var dDecl);
    var errDecl = dDecl.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Console.WriteLine(errDecl.Count == 0
        ? "[OK] declarations params ReadOnlySpan<Span<int>> / params Span<RS> bind with zero errors"
        : "[INFO] declaration-level errors:\n" + DiagSummary(dDecl, DiagnosticSeverity.Error));

    var comp = Compile("P6call", FixtureP6Call, optAdapter, refs, out var diags);
    var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Console.WriteLine(errors.Count == 0
        ? "[UNEXPECTED] comma-expansion of ref-struct elements compiled with zero errors"
        : "[OK] ref-struct-element comma-expansion rejected, codes: " + string.Join(", ", errors.Select(e => e.Id).Distinct()));
    Console.WriteLine(DiagSummary(diags, DiagnosticSeverity.Warning));
}

void P7()
{
    var comp = Compile("P7", FixtureP7, optAdapter, refs, out var diags);
    var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Console.WriteLine(errors.Count == 0
        ? "[OK] params CB (CollectionBuilder type) + comma/empty/single calls compile"
        : "[FAIL] compile errors:\n" + DiagSummary(diags));
    Console.WriteLine(DiagSummary(diags, DiagnosticSeverity.Warning));

    foreach (var call in new[] { "PBuilder(1,2,3)", "PBuilder()", "PBuilder(cb)" })
    {
        try
        {
            var (args, target) = InvOf(comp, "Driver7", call);
            var a = args.Single();
            Console.WriteLine($"[INFO] {call} ArgumentKind={a.ArgumentKind}");
            DumpOp(a.Value, 2);
        }
        catch (InvalidOperationException ex) { Console.WriteLine($"[FAIL] {ex.Message}"); }
    }

    var p = GetMethodByName(comp, "F1", "PBuilder").Parameters[0];
    foreach (var attr in p.GetAttributes())
        Console.WriteLine($"[INFO] param attr: {attr.AttributeClass?.ToDisplayString()} ctorArgs=[{string.Join(", ", attr.ConstructorArguments.Select(tc => tc.Kind == TypedConstantKind.Type ? ((INamedTypeSymbol?)tc.Value)?.ToDisplayString() ?? "<type>" : $"\"{tc.Value}\""))}]");
    var cbType = GetMethodByName(comp, "F1", "PBuilder").Parameters[0].Type;
    foreach (var attr in cbType.GetAttributes())
        Console.WriteLine($"[INFO] CB type attr: {attr.AttributeClass?.ToDisplayString()} ctorArgs=[{string.Join(", ", attr.ConstructorArguments.Select(tc => tc.Kind == TypedConstantKind.Type ? ((INamedTypeSymbol?)tc.Value)?.ToDisplayString() ?? "<type>" : $"\"{tc.Value}\""))}]");
}

void P8()
{
    var comp = Compile("P8", FixtureP8, optAdapter, refs, out var diags);
    Console.WriteLine("[INFO] compile: " + (diags.Any(d => d.Severity == DiagnosticSeverity.Error) ? DiagSummary(diags) : "0 errors"));
    foreach (var call in new[] { "M(1)", "S(\"x\")" })
    {
        try
        {
            var (args, target) = InvOf(comp, "Driver8", call);
            Console.WriteLine($"[INFO] {call} resolves to: {target.ToDisplayString()} (params={target.Parameters.LastOrDefault(p => p.IsParams) != null})");
            foreach (var a in args) Console.WriteLine($"  arg ArgumentKind={a.ArgumentKind} value={a.Value.Kind}");
        }
        catch (InvalidOperationException ex) { Console.WriteLine($"[FAIL] {ex.Message}"); }
    }
}

void P9()
{
    foreach (var (label, src) in new[]
    {
        ("params StringBuilder", "public static class X { public static void A(params System.Text.StringBuilder sb) {} }"),
        ("params non-last", "public static class X { public static void B(params int[] p, int x) {} }"),
        ("params int[,]", "public static class X { public static void C(params int[,] m) {} }"),
        ("params non-generic IEnumerable", "public static class X { public static void D(params System.Collections.IEnumerable e) {} }"),
    })
    {
        var comp = Compile("P9_" + label, src, optAdapter, refs, out var diags);
        var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"[INFO] {label}: error codes = [{string.Join(", ", errors.Select(e => e.Id).Distinct())}]");
        Console.WriteLine(DiagSummary(diags, DiagnosticSeverity.Error));
    }
}

void P10()
{
    var comp = Compile("P10", FixtureP10, optAdapter, refs, out var diags);
    Console.WriteLine("[INFO] compile: " + (diags.Any(d => d.Severity == DiagnosticSeverity.Error) ? DiagSummary(diags) : "0 errors"));
    foreach (var (tn, mn) in new[] { ("B10", "M"), ("B10", "N"), ("D10", "M"), ("D10", "N"), ("I10", "M"), ("Impl10", "M") })
    {
        var m = GetMethodByName(comp, tn, mn);
        PrintParamFlags($"{tn}.{mn}", m.Parameters[0]);
    }
}

void P11()
{
    var comp = Compile("P11", FixtureP11, optAdapter, refs, out var diags);
    var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Console.WriteLine(errors.Count == 0
        ? "[OK] ctor chain with params-collection expansion compiles"
        : "[FAIL] compile errors:\n" + DiagSummary(diags));

    var tree = comp.SyntaxTrees.First();
    var model = comp.GetSemanticModel(tree);
    foreach (var ctor in tree.GetRoot().DescendantNodes().OfType<ConstructorDeclarationSyntax>())
    {
        var body = model.GetOperation(ctor) as IConstructorBodyOperation;
        if (body is null) { Console.WriteLine($"[FAIL] no IConstructorBodyOperation for {ctor.Identifier}({string.Join(",", ctor.ParameterList.Parameters)})"); continue; }
        var initOp = body.Initializer;
        var inner = initOp;
        if (initOp is IExpressionStatementOperation es) inner = es.Operation; // ctor initializers are modeled as expression statements
        Console.WriteLine($"[INFO] ctor {ctor.Identifier}({string.Join(",", ctor.ParameterList.Parameters)}): Initializer kind={initOp.Kind} inner={inner.Kind}");
        if (inner is IInvocationOperation inv)
        {
            Console.WriteLine($"  target: {inv.TargetMethod.ToDisplayString()}");
            PrintArgs(inv);
        }
    }
    foreach (var oc in tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
    {
        var op = model.GetOperation(oc) as IObjectCreationOperation;
        if (op is null) continue;
        Console.WriteLine($"[INFO] object-creation '{oc}': constructor={op.Constructor.ToDisplayString()}");
        foreach (var a in op.Arguments) Console.WriteLine($"  arg -> param '{a.Parameter?.Name}' ArgumentKind={a.ArgumentKind}");
    }
}

void P12()
{
    var comp = Compile("P12", FixtureP12, optAdapter, refs, out var diags);
    var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Console.WriteLine(errors.Count == 0 ? "[OK] collection-expression args into params slots compile" : "[FAIL] compile errors:\n" + DiagSummary(diags));
    foreach (var call in new[] { "PIEnumerable([1,2])", "PIEnumerable([..s])", "PArr([1,2])", "PArr([..s])" })
    {
        try
        {
            var (args, target) = InvOf(comp, "Driver12", call);
            var a = args.Single();
            Console.WriteLine($"[INFO] {call} ArgumentKind={a.ArgumentKind}");
            DumpOp(a.Value, 2);
        }
        catch (InvalidOperationException ex) { Console.WriteLine($"[FAIL] {ex.Message}"); }
    }
}

void P13()
{
    var comp = Compile("P13", FixtureP13, optAdapter, refs, out var diags);
    if (diags.Any(d => d.Severity == DiagnosticSeverity.Error)) { Console.WriteLine("[FAIL] fixture errors:\n" + DiagSummary(diags)); return; }

    using var ms = new MemoryStream();
    var result = comp.Emit(ms);
    if (!result.Success)
    {
        Console.WriteLine("[FAIL] emit failed:\n" + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return;
    }
    var asm = Assembly.Load(ms.ToArray());
    var t = asm.GetType("Rt")!;
    var mA = t.GetMethod("A")!; var mE = t.GetMethod("E")!; var mL = t.GetMethod("L")!;
    var fArr = t.GetField("CapArr")!; var fEnum = t.GetField("CapEnum")!; var fList = t.GetField("CapList")!;
    var driver = asm.GetType("RtDriver")!;

    // empty params call sites: compiler-generated lowering, invoked via a driver method, once per round
    driver.GetMethod("RunEmpty")!.Invoke(null, null);
    var a1 = (int[])fArr.GetValue(null)!; var e1 = fEnum.GetValue(null)!; var l1 = (List<int>)fList.GetValue(null)!;
    driver.GetMethod("RunEmpty")!.Invoke(null, null);
    var a2 = (int[])fArr.GetValue(null)!; var e2 = fEnum.GetValue(null)!; var l2 = (List<int>)fList.GetValue(null)!;
    Console.WriteLine($"[INFO] empty params int[] : instance1-type={a1.GetType().FullName} len={a1.Length}, same-instance-across-two-calls={ReferenceEquals(a1, a2)}");
    Console.WriteLine($"[INFO] empty params IEnumerable<int>: runtime-type={e1.GetType().FullName}, same-instance-across-two-calls={ReferenceEquals(e1, e2)}");
    Console.WriteLine($"[INFO] empty params List<int>  : runtime-type={l1.GetType().FullName}, same-instance-across-two-calls={ReferenceEquals(l1, l2)}");
    Console.WriteLine($"[INFO] empty int[] ReferenceEquals Array.Empty<int>(): {ReferenceEquals(a1, Array.Empty<int>())}");
    Console.WriteLine($"[INFO] empty IEnumerable ReferenceEquals Array.Empty<int>(): {ReferenceEquals(e1, Array.Empty<int>())}");

    // comma expansion identity: compiler must build a fresh collection per call
    driver.GetMethod("RunExpanded")!.Invoke(null, null);
    var a3 = (int[])fArr.GetValue(null)!;
    driver.GetMethod("RunExpanded")!.Invoke(null, null);
    var a4 = (int[])fArr.GetValue(null)!;
    Console.WriteLine($"[INFO] comma-expansion params int[] : len={a3.Length}, same-instance-across-two-calls={ReferenceEquals(a3, a4)} (expected False: fresh array per call)");

    // non-empty params IEnumerable<int>: what concrete shape does the compiler pick?
    driver.GetMethod("RunExpandedIEnum")!.Invoke(null, null);
    var e3 = fEnum.GetValue(null)!;
    Console.WriteLine($"[INFO] comma-expansion params IEnumerable<int>: runtime-type={e3.GetType().FullName}, count={((System.Collections.Generic.IEnumerable<int>)e3).Count()}");

    Console.WriteLine("[INFO] emitted metadata attributes:");
    foreach (var (label, m) in new[] { ("A(params int[])", mA), ("E(params IEnumerable<int>)", mE), ("L(params List<int>)", mL) })
    {
        var pi = m.GetParameters().Last();
        var names = pi.GetCustomAttributesData().Select(d => d.AttributeType.FullName).ToArray();
        Console.WriteLine($"  {label}: [{string.Join(", ", names)}]");
    }
    Console.WriteLine("[INFO] (note) ParamArrayAttribute expected on A; ParamCollectionAttribute expected on E/L");
}
```
