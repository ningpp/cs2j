# 枚举预处理（Enum Preprocessor）设计

日期：2026-09-26

全文统一命名：C# 侧预处理器为 `EnumPreprocessor`（Core 目录
`EnumPreprocessor/`），CLI 包装为 `ProjectEnumPreprocessor`，
中间目录名 `.cs2j-enum-src`，CLI verb `enum-preprocess`。

本文严格对齐 `docs/superpowers/specs/2026-09-26-null-coalesce-preprocessor-design.md`
的架构与接线方式。

---

## 0. 本文相对初稿的修正（评审索引）

初稿指本特性的第一版草稿（`EnumPreprocessor` / `ProjectEnumPreprocessor`
命名、`.cs2j-enum-src`、verb `enum-preprocess` 均沿用）。以下 6 处经实测或
源码查证后修正：

| # | 初稿内容 | 修正 | 依据 |
|---|----------|------|------|
| 1 | 默认仅处理 `[Flags]` 枚举（`IncludeNonFlags = false`） | **改写所有枚举，统一规则**；删除 `IncludeNonFlags` 选项 | `[Flags]`/位模式枚举在转换器里已映射为 Java `int`/`long` 常量，位运算天然是 int 运算（`EnumTransformer.cs` 的 flags 分支 + `BinaryExpressionTransformer.TryTransformEnumBitwiseOperation` 对 flags 显式放行）。需要收敛的脆弱路径在非 flags 侧；统一规则无需与转换器的 flags 判定耦合，代价是 flags 枚举会多出冗余 `(int)` 强转（已确认接受） |
| 2 | `~` 用 `(U)(~(U)X)`，断言 checked 下不溢出 | **窄底层类型必须 `unchecked((U)(~(U)X))`** | 实测：`checked { b = (byte)(~(byte)x); }` 抛 `OverflowException`；常量形式直接 CS0221 编译失败。而原生 `checked { PB n = ~p; }`（byte 枚举）返回 `0xFD` 不抛异常。`unchecked` 版返回 `0xFD` 且**仍是编译期常量** |
| 3 | `E.A \| 0x04` 作为"含整数字面量"用例 | 删除该用例；**只有字面量 `0`** 可隐式转枚举 | `E.A \| 0x04` 是 CS0019（枚举与 int 之间无 `\|` 运算符、无隐式 int→枚举转换）。实测 `FB.A \| 0` 合法 |
| 4 | "C# 不允许枚举 `+` 运算符"，故不发明 `a + b` | 理由改为：`+`/`-`/`++`/`--` **是**合法枚举运算符，但属算术而非位运算，且转换器已正确处理；本阶段不动它们 | 实测 `e + 1`、`e - e2`、`e++` 均编译运行通过。规范定义 `E operator ++(E)`、`E operator +(E, U)` 等 |
| 5 | §8.4 说插入位置是"TupleToStruct 之前"，§9 列表却是另一套顺序 | 统一为**真实代码顺序**（`Program.cs` 的 `ConvertProject`）：在 `NullCoalesce` 之后、`TupleToStruct` 之前 | `Program.cs:235-271` 是 NullCoalesce 块，`:273` 起是 TupleToStruct 块 |
| 6 | 项目级"后置自检"照抄 null-coalesce 的纯语法全树扫描 | 改为**逐文件语义自检**（`ReplaceSyntaxTree` + 新 SemanticModel） | 枚举位运算与 int 位运算**语法同形**，纯语法扫描会把每个 `int a \| b` 误报为残留 |
| 7 | §3.3/§3.5 把 `x \|= R` 直接展开为 `x = (E)((U)x \| (U)R)`，并称"get/set 次数与原生一致、不需要临时变量" | **复合赋值只改写稳定目标**（局部/参数/字段/`this.f`/`Type.F`）；`obj.Prop \|= R`、`cache[k] \|= R`、`Get().Prop \|= R` 一律跳过 + Warning | 实测：原生 `Get().Prop \|= F.B` 的接收者求值 **1** 次，展开后 **2** 次；`arr[Idx()] \|= v` 同理。C# 复合赋值保证左操作数只求值一次，展开会破坏它 |

另有两处查证补充（初稿未提，但属于本特性的核心动机）：

- **`HasFlag` 在整个转换器中无任何支持**：`src/` 下无 `HasFlag` 处理（`JavaModifiers.HasFlag` 不是它），
  `config/TypeMappings.json` 无条目，`EnumHelper.java` 无 `hasFlag`，
  `EnumTransformer` 生成的 `has(int,int)` 助手从未被调用。现状是产出非法 Java。
- **独立 `~enumVar` 无支持**：`UnaryExpressionTransformer.cs:45` 把 `BitwiseNotExpression`
  直接交给通用 `"~" + operand`（`:138-178`），只有嵌套在别的位运算里才碰巧正确。

---

## 1. 背景与问题

`[Flags]` 枚举的位运算（`| & ^ ~`）、复合赋值（`|= &= ^=`）、`HasFlag`、以及
枚举与整型的显式强转，其转换逻辑分散在主转换器的多个 transformer 中，
依赖"父节点长什么样"的启发式，且存在明确缺口。

### 1.1 主转换器现状（已查证）

| 场景 | 现状 | 结论 |
|------|------|------|
| `[Flags]` / 位模式枚举 | `EnumTransformer` 生成 Java 类 + `public static final int` 常量；类型引用直接映射为 `int`/`long`（`TypeMappingService` flags 分支） | `p \| Permissions.Write` 已是原生 int 运算；`TryTransformEnumBitwiseOperation` 对 flags **显式放行**（`BinaryExpressionTransformer` 的 flags 早退分支） |
| 非 flags 枚举 | 生成 Java `enum` + `getValue()`/`fromValue()`/`_UNMAPPED` | 位运算由 `BinaryExpressionTransformer.TryTransformEnumBitwiseOperation` / `AssignmentTransformer` 的复合赋值展开 / `UnaryExpressionTransformer` 的 `++`/`--` 处理 |
| 包装结果 | `ShouldWrapEnumBitwiseResult` 按**父节点种类**决定是否包 `.fromValue(...)`（父节点白名单：返回语句/表达式体/yield/`=`/赋值/实参/强转） | `(a \| b).ToString()`、`var z = (a \| b) + x` 之类父节点不在白名单时**不被包装** → 产出错类型 |
| `HasFlag` | 无支持（见 §0） | 产出非法 Java（`x.HasFlag(f)` 原样输出） |
| 独立 `~enumVar` | 无支持（见 §0） | 非 flags 枚举产出 `~x`，非法 |

因此本预处理器的定位是：**把枚举运算符在 C# → C# 层降级为"显式底层整数
运算 + `(E)` 强转"**，使主转换器只需处理普通 `int` 运算与 `(E)` 强转
（后者它已完整支持：int → enum 走 `EnumType.fromValue((U)(...))`，见
`TypeOperationTransformer.TransformCast`），从而不再依赖父节点启发式。

### 1.2 总体约束（对齐 null-coalesce §1）

1. **仅做 C# → C# 转换**，不涉及任何 C# → Java 代码生成（本阶段产物仍是 C#）。
2. **语义绝对等价**：改写前后运行结果、副作用顺序、求值次数必须完全相同。
3. **不引入外部支撑类**：默认采用「显式底层类型强转 + 整数运算」的纯语法降级，
   **不生成也不注入** `EnumFlags` 之类的 helper 项目。改写产物是纯整数算术 +
   `(E)` 强转（必要时一个 `unchecked(...)`），下游主转换器无需任何额外运行时。
4. **最小侵入**：只改写出现目标语法的表达式；其余代码原样保留。
5. **保持可编译**：输出合法 C#，保留 `using`、命名空间、`var`、访问修饰符、trivia。
6. **不改写就不改字节**：语法层 dirty 是语义层的**超集**（任何 `int` 位运算都
   命中）。若一个文件虽然 dirty 但**没有任何可改写的节点**，必须原样返回输入
   字符串（不跑 `Formatter`、`Changed = false`），保持字节级保真。
7. **全部改写基于 Roslyn `SemanticModel`**：用枚举符号的底层类型
   （`INamedTypeSymbol.EnumUnderlyingType`）与操作数类型判定，禁止仅凭标识符
   名字猜测；需正确识别 `using static`、别名、非限定名、嵌套枚举。

---

## 2. 目标

- 新增预处理阶段 **enum**：把**所有**枚举（`[Flags]` 与非 `[Flags]` 一视同仁）
  的位运算、复合赋值、一元 `~`、`HasFlag` 改写为显式底层整数运算 + `(E)` 强转。
- 改写后残留 C# 中不再出现"操作数类型为枚举"的 `| & ^ ~`、`|= &= ^=`、
  `HasFlag`；主转换器无需再依赖父节点启发式。
- 补上主转换器今天完全缺失的两项：`HasFlag`、独立 `~enumVar`。
- 复用现有预处理器基础设施（`ProjectPreprocessSource`、
  `SupportedProjectFiltering`、`ProjectUnsignedTypePreprocessor.GetBasicReferences()`），
  保证中间目录选择/拷贝行为与其他阶段一致。
- clean 文件（以及 dirty 但无可改写节点者）字节级保真；改写产物重新解析无 Error。
- 完整接入 CLI（独立 verb `enum-preprocess` + `convert-project` 串联），与
  `LocalFunctionLift` / `NullCoalesce` 同款模式。

---

## 3. 核心改写规则（统一机制）

**统一机制**：对任意枚举类型 `E`，取其底层类型
`U = enumTypeSymbol.EnumUnderlyingType`（`int/uint/long/ulong/short/ushort/byte/sbyte`）。
所有枚举位运算降级为：先把枚举操作数强转为 `U`，在 `U` 上做整数运算，
再整体强转回 `E`。

### 3.1 操作数强转的省略（控制产物噪音）

- 操作数静态类型**是枚举** `E` → 需要 `(U)` 强转（去掉枚举性）。
  判定用 `GetTypeInfo(operand).Type`（**不是** `.ConvertedType`：字面量 `0` 的
  `ConvertedType` 会是枚举类型，而它本身是 `int`）。
- 操作数**是字面量 `0`** 或 `default` 字面量 → 原样保留、不加强转
  （它已是整数；`E.A | 0` 是合法 C#，见 §4.4）。
- 操作数静态类型**已经是 `U`** → 省略 `(U)`（它已经是整数了）。这条规则同时
  覆盖了**已被改写的内层子树**（如 `~` 产物，见 §3.4）。
- 外层 `(E)` 强转：**若紧邻父节点（可跳过括号）已是对 `E` 的强转**，省略，
  避免产物里出现 `(E)((E)(...))` 从而在 Java 侧产生 `fromValue(fromValue(...))`。

### 3.2 checked / unchecked（实测依据）

> **结论先写**：`| & ^` 不需要 `unchecked`；`~` 在**窄底层类型**
> （`byte/sbyte/short/ushort`）下**必须** `unchecked`。

- `(U)a`（枚举 → 其底层整数类型）合法；当 `a` 是常量时结果仍是常量。
- 整数 `| & ^` 在 `U` 上逐位计算；对窄类型，两个 `U` 值按位或/与/异或的结果
  仍是 `U` 范围内的值，故 `checked` 下也不会溢出（即使是编译期常量）。
  **实测**：`checked { byte b = (byte)((byte)1 | (byte)2); }` 正常，原生
  `checked { PB n = p | PB.B; }` 与改写形式结果一致。
- `~` 不同：C# 对窄整数做一元 `~` 时会先做整型提升，`~(byte)x` 的结果是
  **`int`**，再窄化回 `U` 在 `checked` 下会抛异常。**实测**：
  - `checked { b = (byte)(~(byte)x); }` → 抛 `OverflowException`；
  - 常量形式 `checked((byte)(~(byte)2))` → 编译错误 CS0221；
  - 原生 `checked { PB n = ~p; }`（`PB : byte`）→ 返回 `0xFD`，**不抛异常**；
  - `unchecked((byte)(~(byte)x))` → 返回 `0xFD`，与原生一致；
  - `const PB C = unchecked((PB)(byte)(~(byte)PB.A));` → **合法且为编译期常量**。
  即：枚举运算符在 C# 中按未检查上下文计算，因此改写必须显式 `unchecked`。
- 产物形状：`~X`（`X : E`）→ `unchecked((U)(~(U)X))`，其中 `unchecked` 仅在
  `U ∈ {byte, sbyte, short, ushort}` 时发出；`U ∈ {int, uint, long, ulong}`
  时窄化是恒等操作，无需 `unchecked`。
- 下游兼容性：`TypeOperationTransformer.TransformUnchecked` 直接返回内层表达式
  （丢掉包装），且 `UnsignedMathSyntax.UncheckedCast` 已经在既有预处理产物里
  生成 `unchecked(...)`，故该包装对流水线安全。

### 3.3 二元位运算

| 原语法 | 改写为（`U` = 底层类型） |
|--------|--------------------------|
| `L \| R` | `(E)(L' \| R')` |
| `L & R` | `(E)(L' & R')` |
| `L ^ R` | `(E)(L' ^ R')` |

其中 `L'` / `R'` 按 §3.1 得到（枚举操作数加 `(U)`，已是 `U` 者不加），
外层 `(E)` 按 §3.1 决定是否省略。

- 仅当**至少一个操作数**的静态类型是枚举（SemanticModel 判定）时才改写；
  两个都是普通 `int` 的 `a | b` 不动。
- 另一操作数只能是枚举或字面量 `0`（§4.4）；其他形态在合法 C# 中不可能出现。
- 嵌套/括号：`(x \| y) & ~z` 自底向上递归，内层先改写，外层再包 `(E)`。

### 3.4 一元取反（去位模式）

| 原语法 | 改写为 |
|--------|--------|
| `~X`（`X : E`，宽底层类型） | `(E)(~(U)X)` |
| `~X`（`X : E`，窄底层类型） | `(E)unchecked((U)(~(U)X))` |

"移除某个 flag" 的高频模式 `x & ~MyEnum.A` 自动变为
`(E)((U)x & ~(U)MyEnum.A)`（宽类型）或
`(E)((U)x & unchecked((U)(~(U)MyEnum.A)))`（窄类型），逐位正确。

### 3.5 复合赋值（仅限稳定目标）

| 原语法 | 改写为（`x` 为稳定引用，见 §3.9） |
|--------|-----------------------------------|
| `x \|= R;` | `x = (E)((U)x \| R');` |
| `x &= R;` | `x = (E)((U)x & R');` |
| `x ^= R;` | `x = (E)((U)x ^ R');` |

（替换整条赋值语句；`x` 的类型是 `E`，故 `(U)x` 必加；右侧按 §3.1。）

**为什么必须限制目标（实测依据）**：C# 的复合赋值保证"左操作数只求值一次"，
把它展开为 `x = x op R` 会让**接收者/索引表达式**重复求值。实测：

```csharp
Get().Prop |= F.B;              // 原生：calls = 1
Get().Prop = (F)((int)Get().Prop | (int)F.C);   // 展开：calls = 2  ← 语义不等价
arr[Idx()] |= F.B;              // 原生：calls = 1
arr[Idx()] = (F)((int)arr[Idx()] | (int)F.C);   // 展开：calls = 2  ← 语义不等价
```

因此目标 `x` 必须满足 §3.9 的"稳定引用"判定（局部变量 / 参数 / 字段 /
`this.f` / `Type.F`）；`obj.Prop |= R`、`cache[key] |= R`、`Get().Prop |= R`、
`await t` 之类**一律跳过 + Warning**（此时下游 `AssignmentTransformer` 的既有
枚举复合赋值展开仍然可用，不会回退成非法 Java）。

> 附带好处：`x` 为稳定引用时，"get 一次 + set 一次"与原生 `|=` 完全一致
> （若 `x` 是属性，getter/setter 各调用一次，也与原生相同）。

### 3.6 `HasFlag`（返回 bool）

`x.HasFlag(f)`（`x`、`f` 均为**同一**枚举类型 `E`）→

```
((x' & f') == f')
```

按 §3.1 得到 `x'` / `f'`（枚举操作数加 `(U)`），结果类型 `bool`，无外层 `(E)`。

- 语义等价**实测**（8 组用例全部 MATCH，含 `f = (FB)0` → `true`、
  `f` 含 `x` 之外的位 → `false`、多位置掩码、`x` 持有未声明成员值 `(FB)8`）：
  `Enum.HasFlag` 的实现即 `(x & f) == f`。
- 注意与既有 TS 路径的差别：`tests/CSharpToTypeScript.Tests/TsEnumEmissionTests.cs`
  断言 `HasFlag` 被内联为 `(this.s & f) !== 0` —— 对多位掩码那是**错的**
  （`!= 0` 是"任一位置位"，`HasFlag` 是"全部位置位"）。本规范采用 `== f`。
- 参数类型必须解析为与接收者**同一**枚举类型；否则跳过 + Warning（见 §4.10）。

### 3.7 枚举 ↔ 整型 / 枚举 ↔ 枚举 强转：保留不动

- `(Color)n`（int → enum）：**保留**。C# 原生已保证"值不存在也不抛异常、
  原值保留"，下游主转换器已支持（`EnumType.fromValue((U)(...))`，且
  实测 `(FB)8` 合法）。
- `(int)c`（enum → int）：保留（下游走 `getValue()`，含 `byte`/`ushort`/`uint`
  的掩码处理）。
- `(OtherEnum)c`（跨枚举强转）：保留（下游走 `fromValue(other.getValue())`
  或 `values()[...]`）。
- `default(Color)` / `default` / `(Color)0` / `Enum.ToObject`：保留。

### 3.8 比较与模式匹配：保留结构，只改写嵌套位运算

- `==` / `!=` / `<` `>` `<=` `>=`（枚举之间或枚举与同类型字面量）：保留
  （C# 原生语义稳定，下游已处理；flags 枚举走原生 int 比较）。
- `if (e is E.A)` / `is E.A or E.B` / 关系模式 / `switch` 语句与 `switch`
  表达式；`nameof(E.Member)`、`typeof(E)`：保留，只改写其中出现的枚举位运算
  **子表达式**。

### 3.9 稳定性判定 `IsStableReference`（本阶段唯一的"副作用"闸门）

一个表达式是**稳定引用**，当且仅当（先剥掉外层括号）：

1. `SemanticModel.GetSymbolInfo(expr).Symbol` 解析为
   `ILocalSymbol` / `IParameterSymbol` / `IFieldSymbol`；或
2. 形如 `this.F` / `Type.F`（或嵌套的 `Outer.Static.F`），且末段解析为
   `IFieldSymbol`（接收者链上不允许出现属性/索引器/调用）。

其余一切（属性访问、索引器、方法调用、对象创建、`await`、条件访问 `?.`、
内嵌赋值、`dynamic`）都**不稳定**。

该判定同时被两处使用，不可放宽：

- §3.5 复合赋值的**目标**（`x` 必须稳定）；
- §4.5 可空 lifted 的**可空操作数**（重复读取必须无副作用）。

---

## 4. 必须覆盖的所有语法 / 语义情况（ALL CASES）

### 4.1 位运算（核心）
`| & ^ ~`、复合赋值 `|= &= ^=`、嵌套括号、`x & ~MyEnum.A` 去位模式、
与字面量 `0` 混合（`E.A | 0`）。**`[Flags]` 与非 `[Flags]` 枚举统一处理**，
不做特性判定（见 §0 修正 1）。

### 4.2 底层类型差异（CRITICAL）
`enum E : byte/short/int/long/sbyte/ushort/uint/ulong`。`U` 一律按
`GetEnumUnderlyingType()` 取值（用 `INamedTypeSymbol.EnumUnderlyingType`，
不要用 `ToDisplayString()` 去猜）。
`byte` 枚举的 `~E.A` 必须得到 `0xFE` 而非 `0xFFFFFFFE`，靠
`(E)unchecked((byte)(~(byte)X))` 保证。
`uint`/`ulong` 枚举注意本阶段早于 unsigned 阶段（§9），产出的 `(uint)`/`(ulong)`
强转由 unsigned 阶段继续规范化。

### 4.3 有符号底层类型
`enum G : int { N = -1 }`：位运算按补码语义，强转形式天然正确；
`~` 在窄有符号类型（`sbyte`/`short`）同样需要 `unchecked`。

### 4.4 字面量 `0` 隐式转换特例
`E x = 0;` 合法（**仅**字面量 `0` 可隐式转枚举），保留；
`E.A | 0` 合法 → `(E)((U)E.A | 0)`（`0` 已是 `U` 兼容的整数字面量，按 §3.1 不加强转）；
`E x = 1;` 非法需 `(E)1`；`E.A | 0x04` 非法（CS0019），不在覆盖范围。

### 4.5 可空枚举（`E?`，lifted 运算符）
**只在"可空操作数求值两次无副作用"时改写**：每个可空操作数都必须满足 §3.9 的
`IsStableReference`（局部变量 / 参数 / 字段 / `this.f` / `Type.F`），否则跳过 +
Warning。属性、索引器、方法调用等一律跳过 —— 因为改写把它们**读两次**。

- 二元：`x | E.A`（`x : E?`，`x` 稳定）→
  `x.HasValue ? (E?)((U)x.Value | (U)E.A) : (E?)null`
- 两侧可空：`x | y`（均 `E?`，均稳定）→
  `x.HasValue && y.HasValue ? (E?)((U)x.Value | (U)y.Value) : (E?)null`
- 一元：`~x`（`x : E?`，稳定）→ `x.HasValue ? (E?)~(U)x.Value : (E?)null`
  （窄类型加 `unchecked`，写法同 §3.4，包在 `(E?)` 内层）
- 复合赋值：`x |= R`（`x : E?`，稳定引用且可赋值）→
  `x = x.HasValue ? (E?)((U)x.Value | (U)R') : (E?)null;`
- **实测**：`x.HasValue ? (E?)((byte)x.Value | (byte)FB.B) : (FB?)null` 与原生
  `x | FB.B` 在 `x = FB.A` 与 `x = null` 两条路径结果一致（3 与 null）。
- 保留：`x ?? E.A`、`x.Value`、`x.HasValue`、`GetValueOrDefault()` /
  `GetValueOrDefault(E.B)`、lifted 比较 `x > y`（任一 null → false）。
- 跳过 + Warning：`x | null`（结果恒为 null，无改写价值）、
  `x?.HasFlag(f)`（null 条件调用）、接收者类型为 `System.Enum` 基类的情况、
  任一可空操作数不满足 §3.9（如 `GetFlags() | E.A`、`obj.Prop | E.A`）。

### 4.6 自增 / 自减与算术：**不在本阶段职责内**
`e++` / `e--` / `++e` / `--e`、`e + 1`、`e - 1`、`e1 - e2`（结果类型为 `U`）
在 C# 中**全部合法**（规范定义 `E operator ++(E)`、`E operator +(E, U)`、
`E operator -(E, U)`、`E operator -(E, E)` 等），且转换器已正确处理
（`fromValue(getValue() + 1)`、`values()[ordinal + 1]`，`byte` 枚举带 `& 0xFF`
掩码 —— 见 `tests/CSharpToJava.Tests/ByteEnumArithmeticWrapTests.cs`）。
它们是**算术**而**不是位运算**，本阶段不改写，也不会去"发明"新的 `a + b`。
（`~` 是唯一一个既被本阶段接管、又在转换器里缺失的运算符。）

### 4.7 常量上下文（枚举成员初始化 / const / 特性参数 / case / 默认参数）
统一用 §3 的强转形式改写，**因为改写结果整体仍是编译期常量**：
```csharp
enum E { A = 1, B = 2, AB = (E)((int)E.A | (int)E.B) }   // 常量，合法
const E C = (E)((int)E.A | (int)E.B);
[My((E)((int)E.A | (int)E.B))]
void F(E e = (E)((int)E.A | (int)E.B)) {}
switch (x) { case (E)((int)E.A | (int)E.B): ... }   // case 标签需常量，此处仍是常量
```
窄底层类型带 `~` 时用 `unchecked(...)`；**实测** `unchecked` 包裹后仍是
编译期常量，故常量上下文同样安全。
与 null-coalesce 不同，本阶段**不插入任何语句**（lifted 三元也是纯表达式），
因此**字段初始化器、LINQ 查询表达式、表达式 lambda 体内都可以改写**。

### 4.8 （原 `IncludeNonFlags` 已删除）
统一改写所有枚举，不再有"是否包含非 `[Flags]`"的开关（§0 修正 1）。

### 4.9 泛型约束与方法形参
- `T where T : struct, System.Enum`：`T` 无编译期底层类型，且 C# 本身不允许对
  `T` 类型的值使用 `| & ^ ~`（无运算符约束），故此类位运算不会出现；
  若因其他原因无法在编译期确定 `U` → **保守跳过 + Warning**。
- `out`/`ref`/`in` 参数、`params E[]`、元组 `(E, int)`：调用处位运算仍按 §3 改写；
  `out`/`ref`/`in` 参数解析为 `IParameterSymbol`，属于 §3.9 的稳定引用，
  作为 lifted 操作数或复合赋值目标时可改写（读取两次在同一表达式内不可观测）。

### 4.10 不触碰清单（必须原样保留）
- `[Flags]` / `[Description]` / `[Obsolete]` 等特性声明本身（只改其参数里的位运算）。
- `nameof(E.Member)`、`typeof(E)`。
- 非枚举类型自定义的 `|`/`&`/`^` 运算符（含自定义 `struct`；用 SemanticModel
  区分，避免误改）。
- `using static` 下的非限定枚举成员：必须靠符号解析确认所属枚举类型，
  解析成功才改写。
- `Enum.IsDefined` / `Enum.Parse` / `Enum.TryParse` / `Enum.GetName` /
  `Enum.GetNames` / `Enum.GetValues` / `Enum.GetValues<E>()` /
  `Enum.GetUnderlyingType` / `Enum.Format` / `e.ToString("G|F|D|X")`：
  全部保留（本就合法，下游已有 `EnumHelper` 映射）。
- `HasFlag` 的**接收者与实参不是同一枚举类型**（含实参为 `System.Enum` 基类、
  为普通整数、为 `object`）→ 跳过 + Warning（`HasFlag(Enum)` 允许跨类型调用，
  但 `(U)f` 会截断，语义不保证）。

### 4.11 `checked` / `unchecked` 溢出语义
改写后的窄化强转必须保持原生语义：`~` 用 `unchecked(...)`（§3.2 实测），
`| & ^` 无需（结果恒在 `U` 范围内）。改写**不改动**原有 `checked`/`unchecked`
上下文结构；若源码处于 `checked { }` 块或项目级
`<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>`，产物行为与原生一致。

### 4.12 装箱 / 拆箱 / `Enum` 基类 / `IConvertible`
`object o = E.A;`、`E e = (E)o;`、`Enum en = E.A;`、
`((IConvertible)e).ToInt32(null)`：全部保留（不在本阶段职责）。

---

## 5. 架构概览与组件布局

### 5.1 目录结构

```
src/CSharpToJava.Core/EnumPreprocessor/
    EnumPreprocessor.cs                 # 公共入口 Preprocess(sourceCode, semanticModel, options) -> result
    EnumSyntaxRewriter.cs               # CSharpSyntaxRewriter 主体（自底向上表达式改写）
    EnumPreprocessorOptions.cs          # record（Verbose / Strict）
    EnumPreprocessorStatistics.cs       # 统计模型
    EnumPreprocessorDiagnostic.cs       # 诊断模型
    EnumPreprocessorResult.cs           # 结果 record

src/CSharpToJava.CLI/ProjectEnumPreprocessor.cs   # 项目模式包装
```

命名空间 `CSharpToJava.Core.EnumPreprocessor`（对齐
`CSharpToJava.Core.NullCoalesceRewriter`）。

### 5.2 核心流程（对齐 `NullCoalesceRewriter` / `ProjectNullCoalescePreprocessor`）

```
ProjectEnumPreprocessor.PreprocessAsync
  ├── ResolveLayout / ComputeExclusions
  ├── CopyProjectFilesAsync + CopyAncestorMsBuildDirectoryFiles   (复用 ProjectPreprocessSource)
  ├── 预解析全部 .cs 为 SyntaxTree（preprocessorSymbols: NETCOREAPP）
  ├── CSharpCompilation（allowUnsafe: true）+ GetBasicReferences()
  ├── 逐文件遍历
  │    ├── 非 .cs → 原样拷贝（.sln 跳过，已在项目文件循环处理）
  │    ├── dirty 检测（语法超集）
  │    │    ├── 不 dirty → 原样字节拷贝
  │    │    └── dirty → EnumPreprocessor.Preprocess(code, semanticModel, options)
  │    │         ├── 自底向上 EnumSyntaxRewriter（按 U 强转 + 必要时 unchecked）
  │    │         ├── 无任何改写 → 原样返回输入（Changed = false，字节保真）
  │    │         ├── 有改写 → Formatter.Format → 输出 C#
  │    │         └── 后置自检（语义级，见 5.5）
  ├── dotnet restore
  └── 可选 dotnet build -c Release / dotnet test -c Release
```

### 5.3 Dirty 检测（语法超集，快速路径）

```csharp
internal static bool IsDirty(CompilationUnitSyntax root)
    => root.DescendantNodes().Any(n =>
        (n is BinaryExpressionSyntax b &&
            (b.IsKind(SyntaxKind.BitwiseOrExpression)
             || b.IsKind(SyntaxKind.BitwiseAndExpression)
             || b.IsKind(SyntaxKind.ExclusiveOrExpression)))
        || (n is AssignmentExpressionSyntax a &&
            (a.IsKind(SyntaxKind.OrAssignmentExpression)
             || a.IsKind(SyntaxKind.AndAssignmentExpression)
             || a.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression)))
        || (n is PrefixUnaryExpressionSyntax p && p.IsKind(SyntaxKind.BitwiseNotExpression))
        || (n is InvocationExpressionSyntax i && IsHasFlagCall(i)));
```

该判定**故意**是语义层的超集（任何 `int` 位运算都命中），因为纯语法无法区分
枚举与整数。命中不代表需要改写 —— 真正的过滤在 rewriter 里用 SemanticModel 做。

### 5.4 改写计数闸门（字节级保真的关键）

`EnumSyntaxRewriter` 维护两个内部计数：

- `Rewrites`：实际构造出替换节点的次数。
- `Skipped`：判定为目标（操作数确为枚举）但按规则跳过（lifted 非简单操作数、
  类型解析失败、跨类型 `HasFlag`、`?.` 等）的次数。

`EnumPreprocessor.Preprocess` 在改写后：

- `Rewrites == 0` → **原样返回入参字符串**，`Changed = false`，不执行
  `Formatter.Format`。这样"语法 dirty 但语义无可改写"的常见文件（含任意
  `int` 位运算）保持字节级不变，统计计入 `FilesDirtyNoRewrite`。
- `Rewrites > 0` → `Formatter.Format`（`AdhocWorkspace`，同
  `GotoEliminator` / `LocalFunctionLift`），`Changed = true`，
  `FilesTransformed = 1`。

### 5.5 后置自检（语义级，逐文件）

产物必须"重新用语义验证"，而不是像 null-coalesce 那样做纯语法全树扫描 ——
枚举位运算与 `int` 位运算语法同形（§0 修正 6）。做法：改写后**先不写盘**，
在内存里换树再取新的 SemanticModel：

```csharp
var newRoot = (CompilationUnitSyntax)rewriter.Visit(root);
var newTree = semanticModel.SyntaxTree.WithRootAndOptions(newRoot, semanticModel.SyntaxTree.Options);
var newCompilation = semanticModel.Compilation.ReplaceSyntaxTree(semanticModel.SyntaxTree, newTree);
var newModel = newCompilation.GetSemanticModel(newTree);
int remaining = CountResidualEnumOps(newTree, newModel);
```

`CountResidualEnumOps` 统计产物中**操作数类型仍为枚举**的 `| & ^ ~`、
`|= &= ^=`、`HasFlag` 节点数 → `Statistics.ResidualCount`。

- `ResidualCount > 0` → 诊断：`options.Strict ? Error : Warning`，
  消息 `"{n} residual enum bitwise/HasFlag node(s) after preprocessing."`
  （`n` 中已包含刻意跳过的部分；每一个刻意跳过在其发生时另有 Warning，
  见 §5.4 与 §3.6）。
- 项目级不再做全树扫描：CLI 包装只是把各文件的 `ResidualCount` 累加
  （`MergeStatistics`），不做 `CountResidualEnumBitwise(intermediateRoot)`
  那种纯语法扫描。

### 5.6 类型判定

- `semanticModel.GetTypeInfo(operand).Type`；
- 枚举：`INamedTypeSymbol { TypeKind: TypeKind.Enum }`（`EnumUnderlyingType != null`）；
- 可空枚举：`INamedTypeSymbol` 且 `ConstructedFrom.SpecialType == System_Nullable_T`，
  取 `TypeArguments[0]` 判枚举；
- `IErrorTypeSymbol` / 类型为 `null` / `dynamic` → 跳过 + Warning；
- 两个操作数都不是枚举 → 不改写。

---

## 6. 语义保证与边界情况

### 6.1 等价性保证

| 规则 | 保证方式 |
|------|----------|
| 逐位结果一致 | 在底层类型 `U` 上做整数运算，再 `(E)` 强转，与 C# 原生枚举位运算同义 |
| `checked` 安全 | `\| & ^` 结果恒在 `U` 范围内；`~` 的窄化显式 `unchecked`（实测对齐原生行为，§3.2） |
| 求值次数不变 | 只改写表达式形态，不引入临时变量、不改变求值顺序；复合赋值仅在目标满足 §3.9 时改写，故接收者/索引表达式不会重复求值（实测原生 `Get().Prop \|= v` 与 `arr[Idx()] \|= v` 均为 calls=1） |
| 嵌套自底向上展开 | 递归改写内层优先，外层后包 `(E)(...)` |
| 常量上下文可用 | 改写结果仍为编译期常量（含 `unchecked` 形式），可出现在成员初始化/const/特性/case/默认参数 |
| 无语句插入 | 全程纯表达式改写 → 字段初始化器、LINQ、表达式 lambda 均无限制 |
| 可空 lifted 语义 | 仅对无副作用操作数改写为 `HasValue ? ... : (E?)null`，null 传播与原生一致（实测） |
| `HasFlag` 语义 | `(x & f) == f`，与 `Enum.HasFlag` 实现一致（实测 8 组用例） |

### 6.2 改写示例对照

```csharp
// before
MyEnum x = MyEnum.A | MyEnum.B;
MyEnum y = x & ~MyEnum.A;
x |= MyEnum.C;
bool has = x.HasFlag(MyEnum.A);

// after（设 MyEnum 底层类型为 int）—— 宽类型无 unchecked
MyEnum x = (MyEnum)((int)MyEnum.A | (int)MyEnum.B);
MyEnum y = (MyEnum)((int)x & ~(int)MyEnum.A);
x = (MyEnum)((int)x | (int)MyEnum.C);
bool has = (((int)x & (int)MyEnum.A) == (int)MyEnum.A);
```

```csharp
// before —— byte 底层类型枚举，去位
enum Flags : byte { A = 1, B = 2 }
Flags f = Flags.A;          // 无位运算，原样保留
Flags g = f & ~Flags.B;

// after（U = byte，结果 0xFE 而非 0xFFFFFFFE）
Flags f = Flags.A;
Flags g = (Flags)((byte)f & unchecked((byte)(~(byte)Flags.B)));
```

```csharp
// before —— 可空枚举 lifted，x 为稳定引用
Flags? a = Flags.A;         // 无位运算，原样保留
Flags? b = a | Flags.B;

// after
Flags? a = Flags.A;
Flags? b = a.HasValue ? (Flags?)((byte)a.Value | (byte)Flags.B) : (Flags?)null;
```

```csharp
// before —— 父节点已是同类型强转：(E) 不重复
MyEnum e = (MyEnum)(MyEnum.A | MyEnum.B);
// after
MyEnum e = (MyEnum)((int)MyEnum.A | (int)MyEnum.B);
```

```csharp
// before —— 复合赋值目标不稳定：跳过 + Warning（保留原样）
obj.Flags |= MyEnum.B;      // 属性
cache[key] |= MyEnum.B;     // 索引器
GetFlags() |= MyEnum.B;     // 调用
```

---

## 7. 数据结构

### 7.1 Options

```csharp
namespace CSharpToJava.Core.EnumPreprocessor;

/// <param name="Verbose">If true, emit info-level diagnostics.</param>
/// <param name="Strict">If true, residual nodes produce Error instead of Warning.</param>
public sealed record EnumPreprocessorOptions(
    bool Verbose = false,
    bool Strict = false);
```

### 7.2 Statistics

```csharp
public sealed class EnumPreprocessorStatistics
{
    public int FilesScanned;
    public int FilesTransformed;         // Rewrites > 0
    public int FilesSkippedClean;        // 语法不 dirty，字节拷贝
    public int FilesDirtyNoRewrite;      // 语法 dirty 但无可改写节点，字节拷贝（§5.4）
    public int BitwiseOrRewritten;
    public int BitwiseAndRewritten;
    public int BitwiseXorRewritten;
    public int BitwiseNotRewritten;
    public int CompoundAssignRewritten;
    public int HasFlagRewritten;
    public int NullableLiftedRewritten;
    public int SkippedUnsupported;       // 判定为目标但按规则跳过
    public int ResidualCount;            // 后置自检（语义级）残留数
}
```

### 7.3 Diagnostic

```csharp
public enum EnumPreprocessorSeverity { Warning, Error }

public sealed record EnumPreprocessorDiagnostic(
    EnumPreprocessorSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0);
```

### 7.4 Result

```csharp
public sealed record EnumPreprocessorResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<EnumPreprocessorDiagnostic> Diagnostics,
    EnumPreprocessorStatistics Statistics);
```

---

## 8. CLI 包装（ProjectEnumPreprocessor）

### 8.1 请求/结果模型（mirror `ProjectNullCoalescePreprocessor`）

```csharp
internal sealed class ProjectEnumPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public EnumPreprocessorOptions Options { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectEnumPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required EnumPreprocessorStatistics Statistics { get; init; }
    public required IReadOnlyList<EnumPreprocessorDiagnostic> Diagnostics { get; init; }
}
```

常量：`public const string IntermediateDirectoryName = ".cs2j-enum-src";`

### 8.2 PreprocessAsync 流程

**完全 mirror `ProjectNullCoalescePreprocessor.PreprocessAsync`**：

1. 参数守卫；`ResolveLayout`；中间目录已存在且 `!Force` → 抛错，否则删除重建。
2. `TryLoadProjectGraph` + `SupportedProjectFiltering.ComputeExclusions`
   （受 `FilterUnsupportedProjects` 控制）。
3. `CopyProjectFilesAsync` + `CopyAncestorMsBuildDirectoryFiles`。
4. `EnumerateSourceFiles` + `IsUnderExcludedProject` 过滤。
5. 预解析所有 `.cs` 为 SyntaxTree：
   `CSharpParseOptions(preprocessorSymbols: new[] { "NETCOREAPP" })`。
6. `CSharpCompilation.Create("enum-preprocess", trees, ProjectUnsignedTypePreprocessor.GetBasicReferences(),
   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))`。
7. 逐文件：`.sln` → continue；非 `.cs` → `File.Copy`；`.cs` → `EnumPreprocessor.Preprocess`；
   `Changed` → `File.WriteAllTextAsync(file, result.OutputCode, new UTF8Encoding(false))`；
   否则 `File.Copy`（字节保真）；verbose 打印 `[enum] transformed|clean (copy): {relativePath}`。
8. 诊断回填 `FilePath = relativePath`；`MergeStatistics`。
9. `ResidualCount > 0` → `request.Strict ? Error : Warning` 诊断。
10. `dotnet restore`（失败 → Warning 诊断，同 null-coalesce）。
11. `VerifyBuildAndTestsAsync`（`VerifyBuild` 默认 true；`VerifyTests` 仅在存在受支持
    测试工程时执行）→ `DeleteBuildOutputDirectories`。
12. `Success = !diagnostics.Any(d => d.Severity == Error)`。

私有助手（`CountResidual*` 不需要，`RunDotNetCommandAsync` / `ForwardOutputAsync` /
`VerifyBuildAndTestsAsync` / `HasSupportedTestProjects` / `DeleteBuildOutputDirectories` /
`MergeStatistics`）按 `ProjectNullCoalescePreprocessor` 同款实现。

### 8.3 CLI Verb 接入（`Program.cs`）

1. 新增 `EnumPreprocessVerbOptions`（verb 名 `enum-preprocess`），字段对齐
   `NullCoalesceVerbOptions`：`-i/--input`、`-o/--output`（file 模式）、
   `-s/--source`、`-d/--destination`（dir 模式）、`-v/--verbose`、
   `--strict`、`--no-filter-projects`、`--no-verify-build`、`--no-verify-tests`。
   （**无** `--include-non-flags`，§0 修正 1。）
2. `Parser.ParseArguments<..., NullCoalesceVerbOptions, EnumPreprocessVerbOptions>` 追加类型。
3. dispatch：`(EnumPreprocessVerbOptions opts) => EnumPreprocessAsync(opts),`。
4. 新增 `EnumPreprocessAsync`：
   - 单文件模式：`CSharpSyntaxTree.ParseText` + `CSharpCompilation.Create(
     "enum-preprocess-standalone", ...)` + `GetBasicReferences()` →
     `EnumPreprocessor.Preprocess`；有 `--output` 写文件（`UTF8Encoding(false)`），
     否则 `Console.Write`；诊断 `Console.Error.WriteLine($"[{severity}] {message}")`；
     有 Error 返回 1。
   - 项目模式：构造 `ProjectEnumPreprocessRequest`（`Force = true`，
     `Options = new EnumPreprocessorOptions(Verbose, Strict)`，
     `FilterUnsupportedProjects = !NoFilterProjects`，
     `VerifyBuild = !NoVerifyBuild`，`VerifyTests = !NoVerifyTests`）；
     打印 statistics（格式对齐 `NullCoalesceAsync`）与诊断（带 `FilePath` 前缀）；
     最后 `Console.WriteLine($"Preprocessed source: {...PreprocessedSourcePath}")`。

### 8.4 `convert-project` 流水线集成（`Program.cs`）

1. `ConvertProjectOptions` 新增开关（对齐 `NoNullCoalesce` 的取反模式）：
   ```csharp
   [Option("no-enum-preprocess", Default = false,
       HelpText = "Disable the default enum bitwise / HasFlag preprocessing step")]
   public bool NoEnumPreprocess { get; set; }

   public bool EnumPreprocess => !NoEnumPreprocess;
   ```
   默认开启（与其他预处理器阶段一致）。
2. `GetProjectConversionOptionTokens` 增加增量指纹 token：
   `$"enum-preprocess={opts.EnumPreprocess}",`。
3. `GetPreservedDestinationDirectories`：当 `opts.EnumPreprocess` 时
   `preserved.Add(ProjectEnumPreprocessor.IntermediateDirectoryName)`。
4. 在 `ConvertProject` 的 `if (opts.NullCoalesce) { ... }` 块（`Program.cs:235-271`）
   之后、`if (opts.TupleToStruct)` 块（`:273`）之前插入：
   ```csharp
   if (opts.EnumPreprocess)
   {
       var enumResult = await ProjectEnumPreprocessor.PreprocessAsync(
           new ProjectEnumPreprocessRequest
           {
               SourcePath = opts.Source,
               DestinationRoot = opts.Destination,
               Force = opts.Force,
               Verbose = opts.Verbose,
               FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
               CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
               VerifyBuild = opts.VerifyBuild,
               VerifyTests = opts.VerifyTests,
               Strict = opts.LocalFunctionStrict,   // 与 null-coalesce 同款借用
           });

       Console.WriteLine(
           $"enum: {enumResult.Statistics.FilesTransformed} transformed, " +
           $"{enumResult.Statistics.FilesSkippedClean} clean, " +
           $"{enumResult.Statistics.FilesDirtyNoRewrite} dirty-no-rewrite, " +
           $"{enumResult.Statistics.BitwiseOrRewritten + enumResult.Statistics.BitwiseAndRewritten + enumResult.Statistics.BitwiseXorRewritten} bitwise, " +
           $"{enumResult.Statistics.BitwiseNotRewritten} ~, " +
           $"{enumResult.Statistics.CompoundAssignRewritten} compound, " +
           $"{enumResult.Statistics.HasFlagRewritten} HasFlag, " +
           $"{enumResult.Statistics.NullableLiftedRewritten} lifted, " +
           $"{enumResult.Statistics.ResidualCount} residual");

       foreach (var diagnostic in enumResult.Diagnostics)
       {
           var prefix = diagnostic.FilePath != null ? $"{diagnostic.FilePath}: " : "";
           Console.Error.WriteLine($"[{diagnostic.Severity}] {prefix}{diagnostic.Message}");
       }

       if (!enumResult.Success)
           return 1;

       opts.Source = enumResult.PreprocessedSourcePath;
       opts.FingerprintSourcePath ??= enumResult.OriginalSourcePath;
       opts.FingerprintInputRoot ??= enumResult.SourceRoot;
   }
   ```
5. `.gitignore` 与 `.zcodeignore` 增加 `.cs2j-enum-src/`（同时补齐历史遗漏的
   `.cs2j-record-readonly-src/`、`.cs2j-array-src/`、`.cs2j-foreach-deconstruction-src/`、
   `.cs2j-null-coalesce-src/`）。

### 8.5 SemanticModel 构建

与 `ProjectNullCoalescePreprocessor` 同款：预解析全部 `.cs` 为 SyntaxTree
（`CSharpParseOptions(preprocessorSymbols: new[] { "NETCOREAPP" })`），
`CSharpCompilation.Create(..., allowUnsafe: true)`，基础引用集来自
`ProjectUnsignedTypePreprocessor.GetBasicReferences()`，逐文件
`compilation.GetSemanticModel(tree)`。

---

## 9. convert-project 流水线顺序

### 9.1 真实代码顺序与插入点

`Program.cs` 的 `ConvertProject` 中，各阶段的 `if (opts.X)` 块实际顺序为：

```
 1. LocalFunctionLift        (Program.cs:201)   ← 其他阶段
 2. NullCoalesce             (Program.cs:235)
 3. Enum                     ← 新增，插在 2 与 4 之间（Program.cs:271 之后、:273 之前）
 4. TupleToStruct            (Program.cs:273)
 5. ForeachDeconstruction    (:318)
 6. StripDefault             (:355)
 7. UnsignedType             (:391)
 8. GenericPreprocess        (:426)
 9. RecordReadOnly           (:465)
10. ArrayMethod              (:507)
11. MakeReadOnly             (:548)
12. EliminateGoto            (:591)
→ 转译
```

（`StripConditional` 只作为独立 verb 存在：`Program.cs:43` 的 dispatch
`(StripConditionalOptions opts) => StripConditionalCommand.RunAsync(opts)`，
`ConvertProject` 内没有对应块，故不出现在上表中。）选择"`NullCoalesce` 之后、
`TupleToStruct` 之前"的理由：

- 枚举位运算降级是纯表达式降级，不依赖其他预处理器产物；
- 紧跟同为表达式级改写的 null-coalesce，二者共享"只改表达式、不插语句"的性质；
- 在 `UnsignedType` 之前：`enum E : uint/ulong` 产出的 `(uint)`/`(ulong)`
  强转随后被 unsigned 阶段统一规范化（顺序与既有 `UnsignedEnum` 样例一致）；
- 在 `StripDefault` 之前：常量上下文（默认参数、`case` 标签）的改写结果
  仍是常量，可安全穿过后续阶段；
- 在 record / array 预处理之前，避免与它们的改写产生交互。

### 9.2 幂等性

重复运行幂等：改写产物中位运算/复合赋值/`HasFlag` 的操作数都是整数类型
（`(U)` 强转后的子树、`unchecked(...)` 形式），dirty 检测虽可能命中语法层，
但 `Rewrites == 0` → 原样输出（§5.4），后端自检残留为 0，统计无改写。

---

## 10. 测试策略

### 10.1 单元测试（`EnumPreprocessorUnitTests`）
放在 `tests/CSharpToJava.Tests/`，参考 `GotoEliminatorTests.cs` / `GenericPreprocessTests.cs` 风格：

**位运算改写组**：基础 OR/AND/XOR；去位 `x & ~A`；多 flag `a | b | c`；
嵌套括号 `(x | y) & ~z`；与字面量 `0` 混合；复合赋值 `|= &= ^=`（目标为局部/参数/字段）；
`HasFlag` → 掩码判等；父节点已是 `(E)` 时不重复包 `(E)`；
§3.1 的强转省略（已被改写的内层子树不再重复加 `(U)`）。

**底层类型组**：`byte`/`sbyte`/`short`/`ushort` 的 `~` 必须生成 `unchecked(...)`；
`int`/`uint`/`long`/`ulong` 的 `~` **不**生成 `unchecked`；有符号负值枚举位运算；
`enum E : uint` 产物含 `(uint)`。

**稳定性闸门组（§3.9）**：`obj.Prop |= A`、`cache[key] |= A`、`Get().Prop |= A`
→ 复合赋值**不改写 + Warning**；`GetFlags() | E.A`、`obj.Prop | E.A` 作为
可空 lifted 操作数 → **不改写 + Warning**；`this.f |= A`、`Type.F |= A`、
参数 `p |= A` → 正常改写。

**可空枚举组**：稳定引用的 lifted 二元/一元/复合赋值改写为三元；`x | null`
→ 不改写 + Warning；`x?.HasFlag(f)` → 不改写 + Warning；
`GetValueOrDefault` / `??` / lifted 比较保留。

**常量上下文组**：枚举成员初始化 `AB = A | B` 改写后仍可编译；
`const E C = E.A | E.B;`；特性参数 `[My(E.A | E.B)]`；`case (E)(A | B):`；
窄类型常量 `~` 在 `unchecked` 下仍是常量。

**负向测试**：clean 文件字节级保真（含 BOM 保留）；**语法 dirty 但无语义可改写
节点（如 `int a | b`、`~intVar`）→ 字节级保真且 `Changed == false`**；
普通 `struct` 自定义 `|` → 不改写；`Enum` 基类接收者的 `HasFlag` → 不改写；
跨枚举类型 `HasFlag` → 不改写；`IErrorTypeSymbol` → 跳过 + Warning。

**后置自检**：改写产物重新解析无 Error；产物中无"操作数类型为枚举"的
`| & ^ ~` / 复合赋值 / `HasFlag`（语义级计数为 0）。

### 10.2 语义等价测试（`EnumPreprocessorSemanticsTests`）
同一组夹具源码分别以"原始"与"改写后"编译运行，逐字节比对 stdout：

- 底层类型矩阵 `byte/sbyte/short/ushort/int/uint/long/ulong` 下的 `| & ^ ~`
  与去位 `x & ~flag`（窄类型必须体现 `0xFD`/`-3` 这类**已窄化**结果，
  不能是 `0xFFFFFFFD`）；
- **`checked` 上下文**：同一夹具在 `checked { }` 内、以及
   `<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>` 项目下
  运行结果一致且不抛 `OverflowException`（这条直接钉住 §3.2 的结论）；
- `HasFlag` 表驱动（含 `f = 0` → true、多位置掩码、未声明成员值）；
- lifted 在 null / 非 null 两条路径；
- **求值次数**：复合赋值目标为属性时 getter/setter 各一次；
  被跳过的 `Get().Prop |= v` / `arr[Idx()] |= v` 在原始与改写后行为
  完全相同（`calls == 1`）—— 这条直接钉住 §3.5 的限制；
- 常量上下文（成员初始化 / `const` / 特性参数）编译通过且值一致。

### 10.3 项目级集成测试（`ProjectEnumPreprocessorTests`）
参考 `ProjectGotoPreprocessorTests.cs` / `ReadOnlyStructPreprocessorVerifyTests.cs` /
`ProjectNullCoalescePreprocessor` 的项目测试：

- `IsDotNetAvailable` 守卫 / `CreateTempDirectory`；
- 验证中间目录 `.cs2j-enum-src` 结构正确（含 `ProjectPreprocessorsConsistentTests`
  补一条 `IntermediateDirectoryName` 断言，对齐现有 record-readonly 片段）；
- `dotnet build -c Release` 通过；`dotnet test -c Release` 通过（含测试工程时）；
- 夹具需包含各类底层类型的枚举、`[Flags]` 与普通枚举、常量上下文、
  `x & ~flag`、`HasFlag`、`checked` 项目；
- 后置自检：中间目录内各文件 `ResidualCount` 为 0，且产物无编译错误。

### 10.4 Java 侧端到端钉桩

由于 flags 枚举统一改写后会产生冗余 `(int)` 强转（已接受），需要在
`tests/DualTargetCorpus/Enums.cs`（既有语料）中补充 `byte` 底层枚举的
`~`/去位/`HasFlag` 用例，使"生成的 Java 逐位结果正确"被端到端钉住
（尤其 `~` 的窄化在 Java 侧必须落到 `& 0xFF` 这类既有掩码机制上）。

---

## 11. 非目标

- 不引入 `EnumFlags` 等外部 helper 项目 / 不注入支撑库（产物是纯整数运算 +
  强转 + 一个可选的 `unchecked`）。
- 不处理 `??` / `??=`（NullCoalesce 的职责）。
- 不重写枚举 ↔ 整型显式强转 `(Color)n` / `(int)c` / 跨枚举强转（下游已支持）。
- 不重写 `Enum.Parse`/`IsDefined`/`GetValues`/`GetNames`/`Format`/`ToString("F")`
  等反射 API 与 `nameof`/`typeof`。
- 不处理 `[Flags]` 特性声明本身（只处理其参数里的位运算）。
- 不改写枚举的 `++`/`--`/`+`/`-` 算术（§4.6：合法但属算术，且下游已支持）。
- 不处理 `System.Enum` 基类接收者、`x?.HasFlag(f)`、跨枚举类型 `HasFlag`
  （一律跳过 + Warning）。
- 不改写目标不稳定的复合赋值（`obj.Prop |= R`、`cache[k] |= R`、`Get().Prop |= R`）
  —— 展开会让接收者/索引重复求值（§3.5 实测）。这类代码继续走下游
  `AssignmentTransformer` 的既有展开，语义不变、也不回退成非法 Java。
- 不修改主转换器的任何行为（纯 C# → C# 预处理）。既有 transformer 中针对
  枚举的位运算分支**不删除**（未被本阶段改写的代码仍需它们）。
- 不规划 Java 侧支撑类或 compat 方法 / 不规划 Maven 模块或 TypeMappings 条目。
- 不处理 TypeScript 路径（TS 转换不经过本预处理，见 `ConvertProject` 的 TS 分支）。

---

## 12. 已知限制与风险

| 限制 | 说明 | 缓解 |
|------|------|------|
| flags 枚举产物冗余强转 | 统一规则下 `[Flags]` 枚举（Java 侧本就是 `int`）也会被包上 `(int)` 强转，如 `(int)(a \| b)`；`EnumTransformer` 生成的 `and/or/complement/has` 助手彻底变成死代码 | 已确认接受；产物仍合法且语义等价；后续若噪音不可接受，可加"flags 判定"开关（§0 修正 1 的备选方案） |
| 既有 flags 相关测试受影响面 | 单文件 `Convert` 测试不经过预处理器，故 `EnumTransformerTests` 中"flags 位运算保持原生"的断言不受影响；但**项目级**测试若断言 flags 产物文本需同步更新 | 项目级测试夹具需重跑确认；本阶段只新增测试，不修改既有断言（除必要处） |
| 类型推导失败 | 操作数类型无法解析（`IErrorTypeSymbol`） | 跳过 + Warning |
| 普通 struct 自定义 `\|` 误改 | 同运算符不同语义 | SemanticModel 判定操作数确为枚举类型才改 |
| `using static` 非限定名 | 仅凭名字解析不到枚举 | 全部基于符号解析（§1.2 约束 7） |
| 可空 lifted 覆盖面 | 只支持稳定引用操作数（局部/参数/字段） | 其余跳过 + Warning；如需全覆盖需引入临时变量提升（null-coalesce 的 `NullCoalesceStatementLifter` 模式），可作为后续迭代 |
| 复合赋值覆盖面 | 只改写稳定目标（§3.9）；`obj.Prop`/`cache[k]`/`Get().Prop` 目标跳过 + Warning | 展开这类目标会让接收者/索引重复求值（实测 calls 1→2），故必须跳过；下游既有展开仍在，语义不变 |
| 跳过的构造仍需下游兜底 | 被跳过的 `HasFlag`（跨类型 / `System.Enum` 接收者 / `?.`）下游**无兜底**，仍会产出非法 Java | 每个跳过都有 Warning；`--strict` 可让这类输入直接失败而不是产出坏 Java |
| `Strict` 下刻意跳过也会报 Error | 与 null-coalesce 的 `--strict` 语义一致（"输入只用了受支持构造"的断言） | `convert-project` 默认借用 `opts.LocalFunctionStrict`（默认关）；需要严格模式的用户需知晓该含义 |
| `unchecked(...)` 的下游兼容 | 依赖 `TransformUnchecked` 丢掉包装 | 已实测支持；unsigned 阶段本身已在生成 `unchecked(...)`（`UnsignedMathSyntax.UncheckedCast`） |
| 非 flags 枚举的 `_UNMAPPED` 单例 | 未声明成员值由共享可变单例承载（既有行为） | 改写不增加暴露：`(E)((U)a \| (U)b)` 保证恰好一次 `fromValue` 调用（§3.1 的父强转省略规则） |
| 可读性下降 | `(E)((U)a \| (U)b)` 较冗长 | 仅对真正出现的位运算改写；`Rewrites == 0` 的文件字节不变；可用 `--no-enum-preprocess` 关闭 |

---

## 13. 迁移

- 无 Java 侧变更：不涉及 compat 类、TypeMappings、Maven 模块。
- `convert-project` 集成为新增阶段，**默认开启**（`--no-enum-preprocess` 可关）。
- 新增独立 verb `enum-preprocess`，可单文件/项目模式单独运行。
- 旧产物需重跑 cs2j 全流水线再生效。
- 对已预处理输入的重复运行幂等（改写统计为 0，见 §9.2）。
