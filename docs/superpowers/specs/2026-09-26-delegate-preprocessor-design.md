# 委托预处理器（Delegate Preprocessor）设计

日期：2026-09-26
状态：设计已评审，待实现

统一命名：C# 侧预处理器为 `DelegatePreprocessor`（Core 目录 `DelegatePreprocessor/`），
CLI 包装为 `ProjectDelegatePreprocessor`，中间目录名 `.cs2j-delegate-src`，
CLI verb `delegate-preprocess`。
本文对齐 `NullCoalesce` / `Enum` / `Event` 预处理器的架构与接线方式。

---

## 0. 本文相对初稿的修正（评审索引）

初稿是一份近乎完整的规格，但有若干处经实测不成立或与前文自相矛盾。以下 6 条是本文
相对初稿的**全部**有意偏离，实现时以本文为准：

| # | 初稿位置 | 问题 | 修正 |
|---|---|---|---|
| C1 | §4 `namespace CsPrelude { class CsPrelude }` | **实测 CS0234**：命名空间 `CsPrelude` 遮蔽同名类型，调用点 `CsPrelude.Combine(...)` 无法解析。且与仓库既有约定 `Cs2jArray.ArrayHelper` / `Cs2jUnsigned.UnsignedMath` 不一致 | 命名空间改为 `Cs2jPrelude`，类名保持 `CsPrelude`，**调用点拼写保持 `CsPrelude.Combine(...)` 不变**（§4.1） |
| C2 | §3.1 vs §4 | **自相矛盾**：§3.1 称 `Combine` 返回 `System.Delegate` 故 `(Del)` 强转必须；§4 签名 `Combine<T>(T a, T b) where T : Delegate` 返回 `T`。实测泛型推断总成立，强转冗余 | 移除 `(Del)` 强转，产出裸调用 `CsPrelude.Combine(d1, d2)`。保留"返回 `T`"这一核心设计（§3.1、§6.2） |
| C3 | §4 末尾「Java 映射示例」、§9 | 把 Java 侧映射描述成"主转换器负责"的**新增工作**；实际 `io.github.ningpp.compat.DelegateHelper.combine/remove` 已存在，且主转换器对委托 `+=`/`-=` 已在用它（`AssignmentTransformer.cs:1300-1305`） | 映射落地为 `config/TypeMappings.json` 的 **3 条配置**，零新增 Java 代码（§4.3） |
| C4 | §7 流水线顺序 | 引用的 `Event`(#11) 与 `KeywordType`(#13) 阶段**尚不存在**（Enum / Event / KeywordType 三份规格均未实现） | 锚点定为 `NullCoalesce` 之后、`TupleToStruct` 之前，保持初稿"在 TupleToStruct 之前"的相对位置意图（§9.1） |
| C5 | §8「幂等：二次运行 dirty 检测为 clean」 | **不成立**：Dirty 检测含 `+`/`-` 二元表达式，语法上无法与数值加法区分，故任何含算术的文件永远 dirty | 幂等性改述为「二次运行不产生新改写（`Changed == false`，产物逐字节相同）」（§9.2） |
| C6 | §3.2 | §3.2 的匿名方法→lambda 能力**下游已具备**（`LambdaTransformer.TransformAnonymousMethod`，`LambdaTransformer.cs:600-704`） | 评审决定**保留**（理由与增量价值见 §3.2 末） |

另有一处初稿未涉及、实现时必须遵守的约束：Core 目录 `DelegatePreprocessor/` 下存在
同名类 `DelegatePreprocessor`，因此入口**必须是实例成员**而非静态方法——静态调用
`DelegatePreprocessor.Preprocess(...)` 会触发与 C1 同类的 CS0234。这与既有
`NullCoalesceRewriter`（`sealed class` + 实例 `Preprocess`）形态一致（§5.1）。

---

## 1. 背景与问题

C# 委托提供两种语法糖，主转换器（C#→Java）处理成本高：

1. **委托组合/拆解运算符** `+` / `-` 与复合赋值 `+=` / `-=`（作用于委托实例）
   编译为 `Delegate.Combine` / `Delegate.Remove`——而 **Java 没有 `System.Delegate`**，
   必须改为普通方法调用才能被下游映射。
2. **匿名方法语法** `delegate (...) { ... }`（关键字形式），可用 lambda 等价表达
   （Java 8+ 支持 lambda）。

现状核查（本次设计期实测，决定了本阶段的真实空白）：

| 语法 | 现状 | 结论 |
|---|---|---|
| `d += h` / `d -= h`（委托） | **已处理**：`AssignmentTransformer.cs:1300-1305` 产出 `DelegateHelper.combine/remove` | 本阶段只做归一化 |
| `d1 + d2` / `d1 - d2`（二元） | **完全未处理**（全仓库无二元委托运算符逻辑；`BinaryExpressionTransformer.cs` 零委托逻辑） | **本阶段的主要空白** |
| `delegate (...) {...}` | **下游已处理**：`LambdaTransformer.TransformAnonymousMethod` | 本阶段做归一化（§3.2） |

即：本阶段最有价值的产出是**二元 `+`/`-`**，其余两项是把已有能力上移，换取下游只面对
单一形态。

### 总体约束（对齐其他预处理器）

1. **仅做 C# → C# 转换**，但目的明确是为**后续 C# → Java** 做准备：产物仍是合法 C#，
   但形态（委托组合/拆解统一走 `CsPrelude` 普通方法调用）必须对 Java 转换友好。
2. **语义绝对等价**：组合/拆解结果与匿名方法行为一致。
3. **引入受控工具类 `CsPrelude`**（prelude / runtime shim）：委托组合/拆解一律降级为
   `CsPrelude.Combine` / `CsPrelude.Remove` **普通方法调用**，**禁止**输出
   `System.Delegate.Combine` / `Delegate.Remove`（Java 没有 `System.Delegate`）。
4. **基于 Roslyn `SemanticModel`**：用 `ITypeSymbol.TypeKind == TypeKind.Delegate` 判定
   操作数是否为委托类型；用委托的 `DelegateInvokeMethod` 签名生成匿名方法→lambda 的参数。
5. **最小侵入**：只改写委托 `+`/`-`/`+=`/`-=` 与 `delegate` 匿名方法；方法组转换、
   `Delegate.Combine` 显式调用等已显式形式不动。

### 为什么必须引入 `CsPrelude`（而不是直接输出 `Delegate.Combine`）

初看 `System.Delegate.Combine(d1, d2)` 更简单（无需注入任何支撑文件），但它**返回
`System.Delegate`**，要求调用点写 `(Del)System.Delegate.Combine(...)`——于是改写后的
C# 源码里出现了 `System.Delegate` 这个类型，而它在 `config/TypeMappings.json` 中**没有
任何映射条目**（本次核查确认），主转换器必须新增处理。

`CsPrelude.Combine<T>(T a, T b) where T : Delegate` **返回 `T`**，泛型实参由推断定住
（§6.2），因此改写后的源码中**永远不出现 `System.Delegate`**，只出现调用点自己的委托
类型（本来就要被映射，如 `Action`→`Runnable`）。这是 `CsPrelude` 存在的核心理由，也是
C2 移除强转后仍然成立的原因。

---

## 2. 目标

- 新增预处理阶段 **delegate**：
  - 把委托 `d = d1 + d2;` / `d -= h;` 等降级为 `CsPrelude.Combine(...)` /
    `CsPrelude.Remove(...)`（普通方法调用）。
  - 把匿名方法 `delegate (...) {...}` 降级为 lambda。
- 改写后残留 C# 中不再出现"委托类型"的 `+`/`-`/`+=`/`-=` 运算符与 `delegate` 匿名方法。
- 复用 `ProjectPreprocessSource`、`SupportedProjectFiltering`、`GetBasicReferences()`。
- clean 文件字节级保真；改写产物重新解析无 Error；接入 CLI + `convert-project`。

---

## 3. 核心改写规则

### 3.1 委托组合 / 拆解 → CsPrelude.Combine / Remove

| 原语法（Del 为委托类型） | 改写为 |
|---------------------------|--------|
| `Del d = d1 + d2;` | `Del d = CsPrelude.Combine(d1, d2);` |
| `d = d1 - d2;` | `d = CsPrelude.Remove(d1, d2);` |
| `d += h;` | `d = CsPrelude.Combine(d, h);` |
| `d -= h;` | `d = CsPrelude.Remove(d, h);` |
| `a + b + c`（多组合） | `CsPrelude.Combine(a, b, c);`（优先用 `params` 重载） |
| `(d1 + d2) - d3`（嵌套） | `CsPrelude.Remove(CsPrelude.Combine(d1, d2), d3);` |

**无强转**（C2）：`Combine<T>`/`Remove<T>` 返回 `T`，泛型推断由操作数定住（§6.2），
产物形态与下游既有已测输出 `DelegateHelper.combine(_changed, handler)` 一致。

- **判定**：`BinaryExpressionSyntax`（`AddExpression` / `SubtractExpression`）或
  `AssignmentExpressionSyntax`（`AddAssignmentExpression` / `SubtractAssignmentExpression`），
  其操作数（或赋值目标）经 `SemanticModel` 判定为委托类型。
- **多组合扁平化规则**：只在**左嵌套的 `AddExpression` 链**上扁平化（C# 中
  `a + b + c` 解析为 `(a + b) + c`），收集操作数为 `a, b, c`；操作数 ≥ 3 用
  `params` 重载，== 2 用双参重载。显式右嵌套 `a + (b + c)` 不扁平化，产出
  `Combine(a, Combine(b, c))`——语义等价（`Delegate.Combine` 本身按序展平），实现更简单。
- **`Remove` 不扁平化**：`d1 - d2 - d3` 产出 `Remove(Remove(d1, d2), d3)`，与 C# 左结合一致。
- **复合赋值**：`d += h;` 是语句时，整条 `AssignmentExpressionSyntax` 的父节点是
  `ExpressionStatementSyntax`，改写为 `d = CsPrelude.Combine(d, h);` 后仍是合法语句
  （赋值的值是委托、未被使用）。**注意左操作数只求值一次**：`d += h` 中 `d` 既是读又是写，
  产出 `d = CsPrelude.Combine(d, h)` 中 `d` 出现两次，因此左操作数的**求值次数必须为 1**。

  判定用**语法谓词**（不查符号）：左操作数是 `IdentifierNameSyntax`，或是
  `MemberAccessExpressionSyntax` 且其接收者表达式中**不含** `InvocationExpressionSyntax`
  与 `++`/`--` 一元表达式，则改写。注意 `Foo.Bar += h`（`Bar` 为委托属性）**在改写范围内**：
  C# 的 `+=` 语义本就是 getter 一次 + setter 一次，改写后 `Foo.Bar` 同样只求值一次，语义等价。

  其余左值（`arr[i++] += h`、`Get().Del += h`）→ `SkippedUnsupported` + Warning 诊断，
  原文保留。这类左值在委托上合法（数组元素是变量，`GetDel() += h` 本身是 CS0131 不合法），
  但安全改写需要引入临时变量，属本阶段非目标（§11）。
- **非委托操作数**：若操作数是数值类型（如 `1 + 2`）→ 不改写（靠 `SemanticModel` 区分）。

### 3.2 匿名方法 → lambda

| 原语法 | 改写为 |
|--------|--------|
| `delegate (int x) { return x * 2; }` | `(int x) => { return x * 2; }` |
| `delegate { ... }`（省略参数列表） | 按目标委托 `DelegateInvokeMethod` 签名生成参数：`(p0, p1, ...) => { ... }`；仅当委托本身无参时为 `() => { ... }` |
| `delegate (int a, ref int b) { ... }` | `(int a, ref int b) => { ... }` |
| `M(delegate { return 1; })` | `M((p0, ...) => { return 1; })` |
| `async delegate { await ... }` | `async (p0, ...) => { await ...; }` |

- **判定**：`AnonymousMethodExpressionSyntax`（即 `delegate` 关键字匿名方法）。
  **注意与委托声明区分**：`delegate void D(int x);` 是 `DelegateDeclarationSyntax`，
  **不改写**。
- **实现**：替换为 `ParenthesizedLambdaExpressionSyntax`，携带原 `ParameterList`
  （若有）、原 `Modifiers`（`async`）、原 `Block`，` => ` 令牌替换 `delegate` 关键字。
- **省略参数列表的匿名方法**：**不能写成 `() => { ... }`**——无参 lambda 无法转换为
  有参委托。必须用 `SemanticModel.GetTypeInfo(node).ConvertedType` 取目标委托类型，
  再取 `INamedTypeSymbol.DelegateInvokeMethod.Parameters` 按序生成形参。
  - 形参**不加类型**（`(p0, p1) => ...`），与下游既有形态一致；lambda 形参由目标类型推断。
  - 命名 `p0, p1, ...`，**须避开函数体内已出现的标识符**：先从 `Block` 子树收集全部
    `IdentifierNameSyntax` 文本构成 blocked 集合，再按序取第一个未被占用的 `p{i}`。
    下游 `SynthesizeAnonymousMethodParameterList` 已用同一手法（收集
    `bodyLocalBlockedNames`），此处独立实现，不跨层复用。
  - 目标委托若**无参**才生成 `() => { ... }`。
  - **无法解析目标类型**（`ConvertedType` 非委托，如 `Expression<...>` 上下文）→
    `SkippedUnsupported` + Warning 诊断，原文保留（不改写优于错改写）。
- **显式参数列表**：直接保留参数（含 `ref` / `out` / `params`）与 `Block` 体。
  `ref`/`out` 形参在 lambda 中合法；其下游 Java 处理属既有 `ref`/`out` 机制范围，非本阶段。
- **保留块体**：一律保留 `{ ... }` 块体（不转为表达式体 lambda），保证 `return`、
  多条语句、`out` 参数赋值等语义一致。
- **闭包/捕获**：lambda 与匿名方法对 `this`、局部变量的捕获语义等价，无需额外处理。
- **函数指针 `delegate*<...>`**：属类型语法（`FunctionPointerTypeSyntax`），**不是**
  匿名方法，天然不在改写范围；不得误改。需 `allowUnsafe: true` 的编译才能解析。

**为何保留下游已有能力（C6）**：`LambdaTransformer.TransformAnonymousMethod` 已能处理
本表全部情形，但其目标类型解析在拿不到 `SemanticModel` 时**退化为 `()`**
（`SynthesizeAnonymousMethodParameterList` 注释明言 "Falls back to () when no delegate
target can be found"）——对省略参数列表的匿名方法，这是静默产错。本预处理器持有完整
`SemanticModel`，可确定性解析；且改写发生在 C#→C# 层，用 C# 编译即可验证，比验证 Java
产物容易。归一化后 §5.6 的后置自检可断言"零 `AnonymousMethodExpressionSyntax` 残留"，
把不变式做成可检查项。

### 3.3 必须覆盖的所有情况（ALL CASES）

- 委托变量组合 `d = d1 + d2;` / 拆解 `d = d1 - d2;`
- 复合赋值 `d += h;` / `d -= h;`（委托类型局部/字段/参数/**属性**——符号为委托类型且非事件）
- 多委托组合 `a + b + c`（params 重载）
- 匿名方法（显式参数 / 省略参数 / `ref` / `out` / `params` / `async`）
- 匿名方法作为方法参数（`M(delegate {...})`）、作为返回值、作为强转目标
  `((Del)delegate {...})` → `((Del)(() => {...}))`
- 泛型委托 `Action<>` / `Func<>` / 自定义泛型委托的组合
- 嵌套：`(d1 + d2) - d3` → `CsPrelude.Remove(CsPrelude.Combine(d1, d2), d3)`
- 操作数为方法组或 lambda 的组合 `d1 + M`、`d1 + (() => {})`（推断由 `d1` 定住，§6.2）
- 多播委托调用 `d()`、null 条件 `d?.Invoke()` → 保留（不在本阶段）

### 3.4 不触碰 / 不改动（关键区分）

- **事件上的 `+=` / `-=`**（左操作数符号为 `IEventSymbol`）→ **本阶段跳过**，
  交给 Event 预处理器（两者按符号种类互斥）。用 `SemanticModel.GetSymbolInfo` 判定，
  与下游 `AssignmentTransformer.IsEventLikeHandlerAssignment` 的 `OnXxx` 命名启发式不同。
- **方法组转换** `Del d = M;` / `new Del(M)` → 保留（已是显式/下游处理）。
- **显式 `Delegate.Combine` / `Delegate.Remove` 调用** → 保留（已是等价形态）。
  初稿提到的可选 `NormalizeExplicitCombine` 开关**不在本阶段范围**（§11 非目标）。
- **`Delegate.CreateDelegate`** → 保留。
- **lambda 表达式**（非 `delegate` 匿名方法）→ 保留。
- **数值 `+`/`-`**（操作数为非委托类型）→ 保留。
- **`event` 声明 / 委托声明 `delegate void D(...);`** → 保留。

---

## 4. 辅助类契约（CsPrelude）

### 4.1 真源与命名（C1）

```csharp
// src/Cs2jPrelude/CsPrelude.cs
namespace Cs2jPrelude
{
    public static class CsPrelude
    {
        public static T Combine<T>(T a, T b) where T : Delegate
            => (T)Delegate.Combine(a, b);
        public static T Combine<T>(params T[] delegates) where T : Delegate
            => (T)Delegate.Combine(delegates);
        public static T Remove<T>(T a, T b) where T : Delegate
            => (T)Delegate.Remove(a, b);
    }
}
```

**命名空间的选取是被实测逼出来的。** 初稿的 `namespace CsPrelude { class CsPrelude }` 在
调用点不可用：

```
error CS0234: 命名空间"CsPrelude"中不存在类型或命名空间名"Combine"
```

原因是限定成员访问 `CsPrelude.Combine(...)` 中，同名命名空间优先于同名类型，编译器把
`CsPrelude` 解析为命名空间后找不到 `Combine`。把命名空间改为 `Cs2jPrelude` 即可解决，
**且调用点拼写 `CsPrelude.Combine(...)` 完全不变**（`using Cs2jPrelude;` 引入类型）。
一并拿到的好处是与仓库既有约定 `Cs2jArray.ArrayHelper` / `Cs2jUnsigned.UnsignedMath` 同构。

- 类本身**不被本预处理器处理**（`Cs2jPrelude` 目录在源枚举中被排除，见 §4.2），
  故其内部直接使用 `Delegate.Combine`/`Remove` 不会造成自改写。
- `where T : Delegate` 需 **C# 7.3+**。支撑 csproj 显式设置 `LangVersion`（§12 R4）。

### 4.2 注入机制（对齐 Cs2jArray / Cs2jUnsignedMath）

完全复用既有"嵌入资源 + 释放到中间工程 + 注入 ProjectReference"三段式，不发明新机制：

| 环节 | 参照实现 |
|---|---|
| 真源目录 | `src/Cs2jPrelude/CsPrelude.cs` + `src/Cs2jPrelude/Cs2jPrelude.csproj`（后者镜像 `src/Cs2jArray/Cs2jArray.csproj`） |
| 嵌入 CLI 程序集 | `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj` 加 `EmbeddedResource`，`LogicalName` 为 `Cs2jPrelude.CsPrelude.cs` / `Cs2jPrelude.Cs2jPrelude.csproj` |
| 资源读取/释放 | `src/CSharpToJava.CLI/PreludeSupportFiles.cs`，镜像 `ArrayHelperSupportFiles.cs`：`LibDirectoryName = "Cs2jPrelude"`、`SingleFileHelperFileName = "_Cs2jPrelude.cs"`、`ReleaseAsync(destinationDirectory)` 释放 `CsPrelude.cs` + `Cs2jPrelude.csproj`（UTF-8 无 BOM） |
| 释放位置 | `<intermediateRoot>/Cs2jPrelude/` |
| 注入引用 | `EnsureExtensionProjectReferences(...)`，只注入**实际编译了被改写源文件**的项目；csproj 内容按 `NormalizeLineEndings` 归一 |
| 源枚举排除 + 清理 | 枚举时排除 `Cs2jPrelude` 目录；`ClearDestinationForFreshConversion` 时随中间目录一并处理 |

`EnsureExtensionProjectReferences` 目前在 `ProjectArrayMethodPreprocessor.cs:325` 与
`ProjectUnsignedTypePreprocessor.cs:507` 各有一份副本。本阶段**照抄第三份**（与既有形态
一致、改动面最小）；把它与 `ProjectPreprocessSource` 合并去重是独立的重构议题，§11 列为
非目标。

### 4.3 Java 侧映射（C3）

`io.github.ningpp.compat.DelegateHelper` **已经存在**且语义匹配：

```java
// java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DelegateHelper.java
static <T> T combine(T a, T b)   // 展平两侧 invocation list，用 java.lang.reflect.Proxy 包多播
static <T> T remove(T a, T b)    // 移除最后一处匹配；空则返回 null（与 C# d - d == null 一致）
```

主转换器对委托 `+=`/`-=` 已在调用它（`AssignmentTransformer.cs:1300-1305`），因此**只需在
`config/TypeMappings.json` 增 3 条**，零新增 Java 代码：

| 键 | 值 |
|---|---|
| `Cs2jPrelude.CsPrelude`（类型映射） | `DelegateHelper` + import `io.github.ningpp.compat.DelegateHelper` |
| `Cs2jPrelude.CsPrelude.Combine`（方法映射） | `combine` |
| `Cs2jPrelude.CsPrelude.Remove`（方法映射） | `remove` |

**已知语义近似（不在本阶段修）**：`DelegateHelper` 的 `remove` 依赖元素引用相等
（`Objects.equals` + 反射 Proxy），而 C# 的 `-` 是**委托值相等**、移除最后一处相等项。
对"同一个方法组/命名方法"的常见用法等价；对 lambda 与动态生成的委托代理引用，只保证
引用匹配。这是**既有下游行为**，本阶段不改变它，仅记录（§12 R2）。

---

## 5. 架构概览与组件布局

### 5.1 目录结构

```
src/CSharpToJava.Core/DelegatePreprocessor/
    DelegatePreprocessor.cs              # 入口（实例方法 Preprocess，理由见下）
    DelegateSyntaxRewriter.cs            # CSharpSyntaxRewriter 主体
    DelegatePreprocessorOptions.cs       # record（Verbose / Strict）
    DelegatePreprocessorStatistics.cs
    DelegatePreprocessorDiagnostic.cs
    DelegatePreprocessorResult.cs

src/Cs2jPrelude/
    CsPrelude.cs                         # shim 真源
    Cs2jPrelude.csproj                   # 镜像 Cs2jArray.csproj

src/CSharpToJava.CLI/
    ProjectDelegatePreprocessor.cs       # 项目模式包装
    PreludeSupportFiles.cs               # 嵌入资源读取/释放
```

**入口必须是实例成员。** 目录 `CSharpToJava.Core.DelegatePreprocessor` 与类
`DelegatePreprocessor` 同名，`using CSharpToJava.Core.DelegatePreprocessor;` 之后的静态调用
`DelegatePreprocessor.Preprocess(...)` 会触发与 C1 同类的 CS0234（命名空间优先）。因此
`DelegatePreprocessor` 为 `sealed class` + **实例** `Preprocess`，形态与
`NullCoalesceRewriter` 一致：

```csharp
var rewriter = new DelegatePreprocessor();
var result = rewriter.Preprocess(sourceCode, semanticModel, options);
```

### 5.2 核心流程

与 `EnumPreprocessor` / `EventPreprocessor` 一致：ResolveLayout → CopyProjectFiles →
预解析 SyntaxTree → `CSharpCompilation`（`allowUnsafe:true`，`GetBasicReferences()`）→
逐文件 dirty 检测 → clean 字节级拷贝（保 BOM）/ dirty 改写 → 后置自检 →
dotnet restore → 可选 build/test → 清理 build 输出。

### 5.3 单遍改写与 SemanticModel 使用约束

**一次 `CSharpSyntaxRewriter` 遍历**，不拆多趟。理由是语义模型只能回答**原始树**节点的问题：
若第一趟改写后第二趟仍持有第一趟的 `SemanticModel`，新节点查不到类型，必须重建
compilation（每文件重编译，代价不可接受）。

约束（实现必须遵守）：**只对原始节点查询语义模型**。`CSharpSyntaxRewriter` 默认先访问子
节点，因此处理 `BinaryExpressionSyntax` 时其子树已被替换——但方法参数仍是原始节点实例，
对其调 `GetTypeInfo`/`GetSymbolInfo` 合法。**绝不对新构造的节点查询语义模型。**

### 5.4 Dirty 检测（语法快路径）

```csharp
internal static bool IsDirty(CompilationUnitSyntax root)
    => root.DescendantNodes().Any(n =>
        n is AnonymousMethodExpressionSyntax
        || (n is BinaryExpressionSyntax b &&
            (b.IsKind(SyntaxKind.AddExpression) || b.IsKind(SyntaxKind.SubtractExpression)))
        || (n is AssignmentExpressionSyntax a &&
            (a.IsKind(SyntaxKind.AddAssignmentExpression)
             || a.IsKind(SyntaxKind.SubtractAssignmentExpression))));
```

**这个检查在语法层无法收紧，是刻意接受的过近似**：判定委托 `+` 与数值 `+` 需要类型信息，
语法树上看不出区别，所以任何含算术的文件都会"脏"。代价是这些文件要走语义遍历，收益是
正确性（不会漏判）。真正的字节保真由 §5.5 的 `Changed` 语义兜住——脏但无需改写的文件
仍然逐字节拷贝，**不会**被重新格式化。

### 5.5 产物打印与字节保真

两道闸门，缺一不可：

1. **`Changed` 标志**：改写器在**真正替换了节点**时才置位。若遍历结束 `Changed == false`
   （如文件里只有 `i + 1`），`Preprocess` 直接返回**入参 `sourceCode` 原字符串**与
   `Changed = false`；调用方据此走字节级拷贝。这是"脏但无需改写"文件保持逐字节不变的关键。
2. **最小化重打印**：`Changed == true` 时，输出用 `root.ToFullString()`，**不调用
   `Formatter.Format`**。Roslyn 按 trivia 逐字保留未触碰区域，只有被替换节点处文本变化，
   差异最小。这正是本阶段与 `NullCoalesceRewriter` 的**有意分歧**——后者无条件
   `Formatter.Format`，若照搬，因为 `+` 无处不在，会把大量无关格式差异混进改写产物。
   合成的节点用 `WithTriviaFrom(原节点)` 继承前后 trivia；节点内部空白由构造时显式写入。

### 5.6 后置自检

分两层，与既有预处理器一致：

1. **改写器内（语法层）**：对产物重新 `CSharpSyntaxTree.ParseText`，断言无
   `AnonymousMethodExpressionSyntax`，并确认产物重新解析**无 Error 诊断**。此层不需要语义
   模型，由 `DelegatePreprocessor.Preprocess` 内部完成（对照
   `NullCoalesceRewriter.ContainsResidualNullCoalesce`）。
2. **项目包装内（语义层）**：`ProjectDelegatePreprocessor` 在全部文件落盘后重新建
   `CSharpCompilation`，逐文件确认不存在"操作数为委托类型"的二元 `+`/`-`/`+=`/`-=`，
   命中数累计到 `ResidualCount`，Strict 下 Error / 否则 Warning。

语义层是真正的兜底（语法层无法区分委托 `+` 与数值 `+`），语法层保证产物可解析。

---

## 6. 语义保证与边界情况

### 6.1 语义等价性保证

| 原形态 | 产物 | 等价依据 |
|---|---|---|
| `d1 + d2` | `Combine(d1, d2)` | `Delegate.Combine` 逐字等价 |
| `a + b + c` | `Combine(a, b, c)` | `Delegate.Combine` 按序展平；左结合链顺序一致 |
| `d1 - d2` | `Remove(d1, d2)` | `Delegate.Remove` 逐字等价；返回 `null` 语义一致（`DelegateHelper.remove` 空则 null） |
| `d += h` | `d = Combine(d, h)` | 左操作数求值次数为 1（§3.1 语法谓词）；不满足则跳过 |
| `delegate (int x) {...}` | `(int x) => {...}` | C# 语言规范：参数列表显式时两者可互换 |
| `delegate {...}` | `(p0, ...) => {...}` | 省略参数列表时形参不可被引用；显式命名与目标签名同序，行为一致 |

### 6.2 泛型推断为何总是成立

C# 要求 `+`/`-` 的两个操作数都能转换为**同一委托类型**（实测：两个方法组相加是
CS0019「运算符"+"无法应用于"方法组"和"方法组"类型的操作数」），因此**至少一个操作数具有
委托静态类型**。该参数把 `T` 定住，其余操作数（方法组 / lambda / 转换）目标类型化为 `T`。
推断与参数顺序无关，故操作数在左在右都可以。

实测通过的形态（本次设计期在 net8.0 上编译验证）：

```csharp
Action r1 = CsPrelude.Combine(x, M);            // 第二操作数为方法组
Action r2 = CsPrelude.Combine(x, () => {});     // 第二操作数为 lambda
Action r3 = CsPrelude.Combine<Action>(x, y);    // 显式类型实参（备选形态，本设计不产出）
Delegate r4 = CsPrelude.Combine(x, y);          // 赋值目标为 Delegate
Action r5 = CsPrelude.Combine(x, y);            // 不加强转 ← 本设计采用
Action r6 = (Action)CsPrelude.Combine(x, y);    // 冗余强转（不采用）
Action r7 = CsPrelude.Combine(a, b, c);         // params 重载
Action r8 = CsPrelude.Combine(a, b, M);         // params 重载含方法组
Action r9 = CsPrelude.Remove(CsPrelude.Combine(a, b), c);  // 嵌套
Func<int,int> r10 = CsPrelude.Combine(f1, f2);  // 泛型委托
```

**若实现期遇到推断失败的实例**，回退形态是显式类型实参 `CsPrelude.Combine<Del>(...)`
（r3 已验证可编译），而不是 `(Del)` 强转——强转加在结果上，救不了推断失败。

### 6.3 匿名方法目标类型解析

`SemanticModel.GetTypeInfo(anonymousMethod).ConvertedType` 是唯一权威来源，覆盖
"作为方法实参 / 返回值 / 强转目标 / 赋值右值"全部上下文。解析结果必须是
`TypeKind.Delegate` 且 `DelegateInvokeMethod != null`，否则计入 `SkippedUnsupported`
并保留原文。

### 6.4 与事件（Event 预处理器）的边界

事件订阅 `E += h` 的左操作数符号是 `IEventSymbol`，本阶段跳过；委托字段/局部/参数/属性的
`+=` 符号是 `IFieldSymbol` / `ILocalSymbol` / `IParameterSymbol` / `IPropertySymbol`，本阶段
改写。两者按**符号种类**互斥，不存在重叠，也不依赖名字启发式。

---

## 7. 数据结构

### 7.1 Options

```csharp
public sealed record DelegatePreprocessorOptions(bool Verbose = false, bool Strict = false);
```

### 7.2 Statistics

`FilesScanned / FilesTransformed / FilesSkippedClean / CombineRewritten / RemoveRewritten /
CompoundAssignRewritten / AnonymousMethodRewritten / SkippedUnsupported / ResidualCount`

### 7.3 Diagnostic

```csharp
public sealed record DelegatePreprocessorDiagnostic(
    DelegatePreprocessorSeverity Severity, string Message, string? FilePath, int Line);
```

### 7.4 Result

```csharp
public sealed record DelegatePreprocessorResult(
    string OutputCode, bool Changed,
    IReadOnlyList<DelegatePreprocessorDiagnostic> Diagnostics,
    DelegatePreprocessorStatistics Statistics);
```

---

## 8. CLI 包装（ProjectDelegatePreprocessor）

### 8.1 请求/结果模型

`ProjectDelegatePreprocessRequest` / `ProjectDelegatePreprocessResult`，字段对齐
`ProjectNullCoalesceRequest`：`SourcePath` / `DestinationRoot` / `Force` / `Verbose` /
`FilterUnsupportedProjects` / `CompatNamespacePrefixes` / `VerifyBuild` / `VerifyTests` /
`Strict`。结果含 `PreprocessedSourcePath` / `OriginalSourcePath` / `SourceRoot` /
`Success` / `Statistics` / `Diagnostics`。

### 8.2 PreprocessAsync 流程

```
ResolveLayout → CopyProjectFiles → 预解析 SyntaxTree
  → ClearDestinationForFreshConversion（含中间目录保留集）
  → CSharpCompilation（allowUnsafe:true, GetBasicReferences()）
  → 逐文件：dirty? → 改写 : 字节级拷贝（保 BOM）
  → PreludeSupportFiles.ReleaseAsync(<intermediateRoot>/Cs2jPrelude)
  → EnsureExtensionProjectReferences（仅注入实际编译了改写文件的项目）
  → 后置自检 → dotnet restore → 可选 build/test → 清理 build 输出
```

恒定 `IntermediateDirectoryName = ".cs2j-delegate-src"`。

### 8.3 CLI Verb 接入

1. 新增 `DelegatePreprocessVerbOptions`（verb `delegate-preprocess`），字段对齐其他 verb。
2. `Parser.ParseArguments<...>` 列表追加 `DelegatePreprocessVerbOptions`。
3. 新增 `DelegatePreprocessAsync` 调用 `ProjectDelegatePreprocessor.PreprocessAsync`
   并打印统计/诊断。

### 8.4 单文件模式契约

沿用 `_Cs2j*` 支撑文件前缀契约（`SingleFileHelperFileName = "_Cs2jPrelude.cs"`）：
单文件模式的产物旁边释放 `_Cs2jPrelude.cs`，使产物与 shim 处于同一编译单元。文件名与项目
模式的 `CsPrelude.cs` 不同名是**有意的**（同 `ArrayHelperSupportFiles` 的注释）。

---

## 9. convert-project 流水线顺序

### 9.1 阶段顺序

初稿引用的 `Event`(#11) 与 `KeywordType`(#13) 阶段**尚不存在**（C4）。本阶段锚定在
`NullCoalesce` 之后、`TupleToStruct` 之前，保持初稿"在 TupleToStruct 之前"的相对意图；
未来 Enum / Event / KeywordType 落地后自然插在本阶段之前。

`ConvertProjectOptions` 新增：

```csharp
[Option("no-delegate-preprocess")] public bool NoDelegatePreprocess { get; set; }
public bool DelegatePreprocess => !NoDelegatePreprocess;   // 默认开启
```

在 `ConvertProject` 中 `NullCoalesce` 分支之后插入 delegate 分支，串接 `opts.Source` 并
重指向 `delegateResult.PreprocessedSourcePath`，维护 `FingerprintSourcePath` /
`FingerprintInputRoot`（`??=` 语义，与相邻阶段一致）。

`GetPreservedDestinationDirectories`（`Program.cs:2099`）追加
`preserved.Add(ProjectDelegatePreprocessor.IntermediateDirectoryName);`，
与既有 10 个阶段并列。

### 9.2 幂等性

**二次运行不产生新的改写**（C5）。产物中已无委托 `+`/`-`，但若文件含数值算术，
`IsDirty` 仍为 true——此时改写器不改任何节点，`Changed == false`，调用方字节级拷贝，
产物与首次逐字节相同。因此幂等性测试断言的是**输出相同**，而非 `IsDirty == false`。

---

## 10. 测试策略

### 10.1 单元测试（`DelegatePreprocessorUnitTests`）

正向：
- 组合两委托：`d = d1 + d2;` → `CsPrelude.Combine(d1, d2);`
- 拆解：`d = d1 - d2;` → `CsPrelude.Remove(d1, d2);`
- 复合 `+=`：`d += h;` → `d = CsPrelude.Combine(d, h);`（含字段 / 参数 / 属性左值）
- 复合 `-=`：`d -= h;` → `d = CsPrelude.Remove(d, h);`
- 多组合三委托（params 重载）
- 嵌套：`(d1 + d2) - d3`
- 操作数为方法组 / lambda：`d1 + M`、`d1 + (() => {})`
- 泛型委托：`Func<int,int>`
- 匿名方法显式参数 → lambda
- 无参匿名方法 `delegate {}` → `() => {}`
- 省略参数列表 + 有参委托 → `(p0, p1) => {...}`（形参名避开函数体已用标识符）
- `ref`/`out` 参数匿名方法 → lambda 保留 `ref`/`out`
- `async delegate { await ... }` → `async (p0, ...) => {...}`
- 匿名方法作为方法参数 / 返回值 / 强转目标
- 后置自检：无残留委托运算符 / 匿名方法；输出重新解析无 Error

负向：
- 事件 `E += h;`（符号为 `IEventSymbol`）不改写
- 数值 `1 + 2` 不改写
- 委托声明 `delegate void D(int x);` 不改写
- 方法组转换 `Del d = M;` 不改写
- 显式 `Delegate.Combine(a, b)` 调用不改写
- 含副作用的复合赋值左值（`arr[i++] += h`）不改写且计入 `SkippedUnsupported`
- `delegate*<...>` 函数指针类型不改写

保真与幂等：
- 含算术但无委托运算符的文件：`Changed == false`，输出与输入逐字节相同（含 BOM）
- 二次运行：输出与首次逐字节相同

### 10.2 项目级集成测试（`ProjectDelegatePreprocessorTests`）

参考 `ReadOnlyStructPreprocessorVerifyTests.cs`：`IsDotNetAvailable` 守卫 / 临时目录 /
`.cs2j-delegate-src` 结构 / `Cs2jPrelude` 支撑项目释放与 ProjectReference 注入 /
`dotnet build -c Release` / `dotnet test -c Release` / 后置自检。

---

## 11. 非目标

- 不处理事件订阅（Event 预处理器）。
- 不把 lambda 反向转为匿名方法 / 委托。
- 不处理表达式树（`Expression<...>`）转换。
- **不做 `NormalizeExplicitCombine`**（初稿 §3.4 的可选开关）：显式 `Delegate.Combine`
  调用保持原样。YAGNI——它只影响"是否为下游统一 surface"，而下游尚未消费该 surface。
- **不为非简单左值的复合赋值引入临时变量**（§3.1）：`arr[i++] += h` 这类左值安全改写需要
  语句提升 + 临时声明（类似 `NullCoalesceStatementLifter` 的两趟结构），收益低；本阶段
  计入 `SkippedUnsupported` 并保留原文。
- **不重构** `EnsureExtensionProjectReferences` 的双份副本（§4.2），本阶段照抄第三份。
- 不引入运行时（除受控 shim `CsPrelude` 外）；不修改主转换器**代码**；
  `config/TypeMappings.json` 的 3 条映射是本阶段唯一的主转换器侧改动（C3）。
- Maven 模块 / TypeMappings 的 Java 运行时实现不在本预处理器范围。

---

## 12. 已知限制与风险

| # | 风险 | 影响 | 处置 |
|---|---|---|---|
| R1 | Dirty 检测过近似（§5.4） | 含算术的文件都要走语义遍历 | 接受；`Changed` 闸门保证不改写就不重打印 |
| R2 | `DelegateHelper.remove` 用引用相等而非 C# 委托值相等（§4.3） | 动态生成的委托代理上 `-` 语义近似 | 既有下游行为，本阶段不改；记录 |
| R3 | `o` 目录/类同名导致的实例化约束（§5.1） | 未来若有人把入口改成静态方法会 CS0234 | 规格明文约束 + 单测覆盖入口调用形态 |
| R4 | `where T : Delegate` 需 C# 7.3+（§4.1） | 极老 `LangVersion` 的用户项目编译 shim 失败 | `Cs2jPrelude.csproj` 显式设置 `LangVersion` |
| R5 | `DelegateHelper.combine` 用反射 Proxy 实现，运行时开销高于直接委托链 | 性能 | 既有下游行为，非本阶段 |

---

## 13. 迁移

1. 新增 `src/Cs2jPrelude/`（shim 真源 + csproj）与 CLI 嵌入资源。
2. 新增 Core 预处理器 + CLI 包装 + 单元/集成测试。
3. `config/TypeMappings.json` 增 3 条映射（§4.3）。
4. `ConvertProjectOptions` / `ConvertProject` / `GetPreservedDestinationDirectories` 接线（§9.1）。
5. 默认开启；`--no-delegate-preprocess` 可关。
