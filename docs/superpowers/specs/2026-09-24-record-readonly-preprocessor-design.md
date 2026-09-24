# Record → readonly record 预处理器设计

**日期**: 2026-09-24
**范围**: 新增 CLI 级 C#→C# 预处理器（Core `RecordReadOnly\` + CLI `ProjectRecordReadOnlyPreprocessor`），不涉及任何 C#→Java 转换代码
**状态**: 待审核

## 1. 背景与目标

cs2j 的 convert-project 管线已有一套成熟的 CLI 级 C#→C# 预处理链（LocalFunctionLift → TupleToStruct → StripDefault → Unsigned → Generic → MakeReadOnly → Goto），每个特性一个独立预处理器，产出可 `dotnet restore`/可编译的中间源码树。现有 `ReadOnlyStructMaker` 只处理 `StructDeclarationSyntax`（普通 struct），**完全不覆盖 `RecordDeclarationSyntax`**——record struct 与 record class 不会被 readonly 化。

本设计新增一个独立预处理器，把任意非 readonly record 转换为 readonly record：

- `record struct` → `readonly record struct`（C# 语法支持的显式标记，编译器强制完全不可变）
- `record` (class) → 语义 readonly：对外契约不可变（全部可设置成员改 init、公共可变字段改 init 属性），C# 无 `readonly record class` 语法故无标记

**硬约束**：必须考虑所有情况；必须支持将任意非 readonly record 转换为 readonly record。对该约束的实现承诺是：每个 record 要么被完整转换，要么被**原子跳过**（保持原样）并输出精确诊断（位置 + 原因 + 建议），绝不产出无法编译的中间源码，绝不部分转换。

## 2. 已确认的决策

| 决策点 | 结论 | 备注 |
|---|---|---|
| 范围 | record struct + record class（对照官方 records 文档） | record class 采用语义转换（无语法标记） |
| 改写策略 | 全量改写：声明 + 所有用点 + mutating 方法迁移 | `p.X = 1` → `p = p with { X = 1 }` |
| 接口兜底 | 连带改接口：接口新增非破坏成员（With 风格），record 旧修改成员显式实现降级为抛 `NotSupportedException` 的桩，in-tree 普通 class 实现者自动补 fluent 实现 | 见 §4.5 |
| 不可行情况 | 除接口连带可解外，其余客观不可改写情况 → 诊断 + 原子跳过该 record | 保证中间树可编译 |
| 管线集成 | 独立预处理器、convert-project **默认关**（`--record-readonly` 显式开启）+ 独立 verb `record-readonly` | 插入在 Generic 之后、MakeReadOnly 之前 |
| 架构 | 方案 A：语义驱动三阶段（分析 → 计划 → 逐文件重写+验证），仿 TupleToStruct 模板 | |
| C#→Java | 完全不动（RecordTransformer/StructTransformer/Pipeline 均不改） | 用户明确边界 |

## 3. 总体架构与组件

### 3.1 组件划分

```
src\CSharpToJava.Core\RecordReadOnly\
  RecordReadOnlyPreprocessor.cs     # 对外入口：RewriteFile(source, semanticModel, statistics, diagnostics)
  RecordReadOnlyAnalyzer.cs         # Phase 1：全编译语义分析
  RecordConversionPlan.cs           # Phase 2：每个 record 一份计划 + 接口连带计划
  DeclarationRewriter.cs            # Phase 3a：声明改写
  MutationSiteRewriter.cs           # Phase 3b：用点改写
  MethodMigrator.cs                 # Phase 3c：mutating 方法通用迁移
  InterfaceRippler.cs               # Phase 3d：接口加非破坏成员 + 实现者补齐
  RecordReadOnlyStatistics.cs       # 统计
  RecordReadOnlyDiagnostics.cs      # 诊断码与描述（RR0xx 系列）

src\CSharpToJava.CLI\ProjectRecordReadOnlyPreprocessor.cs
  # 中间树复制/枚举/编译/RewriteFile/回写/restore，复用 ProjectPreprocessSource 全套管线
  # IntermediateDirectoryName = ".cs2j-record-readonly-src"

config\RecordReadOnlyOverrides.json # { "force": ["Namespace.Type"], "skip": ["Namespace.Type"] }
```

### 3.2 三阶段数据流

1. **分析**（全解决方案一次）：对源码树建 `CSharpCompilation`（`CSharpParseOptions(LanguageVersion.Latest)` + 合成 `__cs2j_global_usings.g.cs`，与 TupleToStruct 相同）。产出：
   - record 符号清单（partial 多部件合并、嵌套、泛型，`INamedTypeSymbol.IsRecord` + `TypeKind` 区分 struct/class）
   - 每个 record 的可变成员清单（set 访问器属性、非 readonly 实例字段、事件、ref-returning 成员等）
   - 全树 mutation 用点：对 record 类型成员的写访问（赋值 / 复合赋值 / `++`/`--` / C#14 空条件赋值 / 链式赋值 / `ref` 传递）
   - 接口实现关系图：含修改语义成员的接口 + 其全部 in-tree 实现者
   - record 方法调用图（迁移顺序依赖，仿 `CallGraphBuilder`）
2. **计划**（`RecordConversionPlan`）：每个 record 一份声明级步骤 + 方法迁移清单 + 接口连带清单；按文件分组。计划是**原子的**：record 的任一改写步骤不可行 → 整个 record 计划作废（跳过 + 诊断）。
3. **重写**（每文件）：`DeclarationRewriter`/`MutationSiteRewriter`/`MethodMigrator`/`InterfaceRippler` 的产物在同一轮 `CSharpSyntaxRewriter` 中按计划应用（`SyntaxAnnotation("Cs2jRecordReadOnlyRewrite")` 标注改动区间），随后走 TupleToStruct 同款 **`FormatAndValidate`**：仅格式化改动区间，并**重新解析 + 语义诊断**；任何文件若引入编译错误 → 回退该文件全部改动并升级为诊断。绝不产出无法编译的中间源码。

### 3.3 CLI 接线（Program.cs 四处，均按既有模式）

- `ConvertProjectOptions`：`[Option("record-readonly", Default = false)]`（默认关）。
- `ConvertProject` 执行块：`if (opts.RecordReadOnly)` 时调用 `PreprocessAsync`，打印统计，失败即中止；`opts.Source = result.PreprocessedSourcePath`。位置：**Unsigned/Generic 之后、`ProjectReadOnlyStructPreprocessor` 之前**。
- `GetPreservedDestinationDirectories`：注册 `.cs2j-record-readonly-src`。
- 独立 verb `RecordReadOnlyVerbOptions`（单文件 `-i/-o` + 项目 `-s/-d` 双模式 + `--force`），注册进 `MainImpl` 的 `ParseArguments/MapResult`。

**单文件模式限制**：只分析单文件语义模型，跨文件用点/接口实现者不可见 → 仅转换自包含于该文件的 record，其余诊断跳过（文档化行为）。

### 3.4 与既有预处理器的顺序依据

- TupleToStruct 只生成普通 struct（已验证无 `record struct` 生成），与本特性无交集。
- MakeReadOnly（ReadOnlyStructMaker）不处理任何 `RecordDeclarationSyntax`（已验证），无双重处理。
- 放在 MakeReadOnly 紧前：不可变化改写集中归组；Unsigned/Generic 的成员语法改写先完成，减少干扰。

## 4. 转换语义规则全集（核心）

规则编号：RS-x（record struct 声明）、RC-x（record class 声明）、MS-x（用点）、MM-x（方法迁移）、IR-x（接口连带）、FB-x（兜底）。

### 4.1 record struct 声明级规则（目标：编译器强制的完全不可变）

| # | 输入形态 | 转换 |
|---|---|---|
| RS-1 | `record struct R(...)`（positional 或带体） | 在 `Modifiers` 插入 `readonly`（`AddModifiers` 模式，渲染为 `readonly record struct`）。位置参数属性由编译器从 set 自动变 init，无需语法改写 |
| RS-2 | `partial record struct`（多部件） | **每个**缺少 `readonly` 的部件都插入，保证一致性 |
| RS-3 | 体内容 `{ get; set; }` / `{ get; private set; }` 自动属性 | `set` → `init`（保持访问修饰符：`{ get; private init; }`） |
| RS-4 | 半自动属性（C# `field` 关键字）`{ get; set { field = value; } }` | `set` → `init` 保持体；`FormatAndValidate` 验证，不过则按 FB-1 兜底 |
| RS-5 | 公共/受保护/内部可变实例字段 `public int X;` | → `public int X { get; init; }`（PublicFieldToProperty 模式）；字段上的 attribute 迁移到属性 |
| RS-6 | 私有可变实例字段 | 分两种：(a) 构造后无赋值点 → 直接加 `readonly`（满足 readonly struct 的编译器约束，`with` 永不需要设置它）；(b) 构造后有赋值点（被方法/属性修改）→ 提升为 `private T F { get; init; }`（readonly 字段无法被 `with` 设置，MM 出口合成依赖 init 属性），赋值点由 MM/MS 规则改写，改不动则 FB-1 |
| RS-7 | 构造函数/字段初始化器 | 不动；readonly 字段在 ctor 内赋值合法 |
| RS-8 | `this = expr;`（方法体内） | 按 MM-3 迁移；出现在 ctor 内则合法保留 |
| RS-9 | 手写（非自动）属性带赋值 setter 体 | 若体是平凡后备字段模式 → 转 `{ get; init; }` 自动属性；否则 FB-1（readonly struct 的 init 访问器体内不能写字段） |
| RS-10 | 实例事件（字段式或 add/remove 式） | readonly struct 无法持有可变委托存储 → FB-1 |
| RS-11 | `volatile` 实例字段、`ref` 返回成员、`fixed` 缓冲区（unsafe）、`unsafe` 指针取址 | FB-1 |
| RS-12 | 静态可变成员 | 不动（readonly 仅约束实例） |
| RS-13 | 自动 `get; init;`、`get;`、手写只读逻辑属性 | 不动 |
| RS-14 | 隐式实现的接口可变成员（如 `public int Count { get; set; }` 实现 `ICounter.Count`） | init ≠ set，隐式实现失效 → 转**显式实现桩** + IR 连带（见 §4.5） |
| RS-15 | 显式接口实现（只读契约如 `IComparable`、`ITuple`） | 不动 |
| RS-16 | attribute、XML 文档注释、运算符、嵌套类型 | 语法原样保留（trivia 保持） |
| RS-17 | 显式 `StructLayout(LayoutKind.Explicit)` 且涉及 RS-5 字段→属性 | 布局语义丢失 → FB-1（保守） |
| RS-18 | 已是 `readonly record struct` | 跳过（统计为 AlreadyReadOnly） |

### 4.2 record class 声明级规则（目标：对外契约不可变）

C# 无 `readonly record class` 语法，转换目标是**语义 readonly**：对外可观察成员全部不可设置。理由：(a) 无编译器强制，"内部完全不可变"不可验证；(b) 惯用法上不可变类允许私有惰性缓存等私有状态；(c) 私有字段无法用 `with` 表达拷贝。

| # | 输入形态 | 转换 |
|---|---|---|
| RC-1 | 声明语法 | **不变**（无 readonly 语法） |
| RC-2 | public/protected/internal/protected internal 的 `{ get; set; }` / `{ get; private set; }` 属性 | set → init（含 private set → private init；private set 对派生类/同程序集仍可见，故统一转） |
| RC-3 | 公共可变实例字段 | → `{ get; init; }` 属性（同 RS-5） |
| RC-4 | private 私有字段、私有 setter 体（仅内部使用） | **保留可变**（内部实现细节；如惰性缓存 `_cache`） |
| RC-5 | 手写属性 setter 体、事件、ref-returning 成员 | 不动（record class 无编译器限制；对外可观察的 set 语义被 RC-2 移除） |
| RC-6 | virtual/abstract/override 的对外可变属性 | 同 RC-2，全继承层次（in-tree）一致转换 |
| RC-7 | 隐式实现的接口可变成员 | 同 RS-14：显式实现桩 + IR 连带 |
| RC-8 | 继承层次中基类 record 未转换（被 FB 跳过）而派生类要转换（或反之） | 层次不一致 → 整层次一起跳过 + 单条诊断（FB-2） |

**record class 已知语义变化（特性固有，用户已接受）**：`a.X = 1` 改写为 `a = a with { X = 1 }` 后，通过别名观察修改的行为改变（原可变语义下 `b = a; a.X = 1; b.X` 可见 1；转换后不可见）。这是"强制不可变"特性的本质，写入文档与诊断提示。

### 4.3 用点（mutation site）改写规则

适用对象：对**已转换 record** 的成员写访问（语义模型判定接收者静态类型）。`with` 表达式、模式匹配、解构、构造调用均非用点，不动。

| # | 输入形态 | 转换 | 条件/失败 |
|---|---|---|---|
| MS-1 | `recv.M = expr;` | `recv = recv with { M = expr };` | recv 须为可写左值：局部变量、非 readonly/in 形参、字段、数组元素、`List` 等带 set 的索引器元素、span 元素、元组元素。不可写（readonly 局部、`in` 形参、foreach 迭代变量、get-only 属性/方法返回值）→ FB-1 |
| MS-2 | 相邻多条同接收者赋值 `p.X = a; p.Y = b;` | 合并为 `p = p with { X = a, Y = b };`（仅相邻语句间无其他语句时；可读性优化，语义等价） | — |
| MS-3 | 复合赋值 `recv.M += v;`（及 `-=`、`??=` 等） | `recv = recv with { M = recv.M + v };` | 假设 record 成员 getter 纯函数（文档化假设）；其余同 MS-1 |
| MS-4 | `recv.M++;` / `--`（语句位置） | `recv = recv with { M = recv.M + 1 };` | 表达式位置消费值（如 `x = p.M++`）→ FB-1 |
| MS-5 | 空条件赋值（C#14）`recv?.M = expr;`（仅 record class） | `recv = recv == null ? null : recv with { M = expr };` | 同 MS-1 |
| MS-6 | 链式赋值 `a.M = b.M = v;`（语句位置） | 拆分：`b = b with { M = v }; a = a with { M = v };` | 表达式位置（实参、条件内等）→ FB-1 |
| MS-7 | 数组/集合元素 `arr[i].M = v;` | `arr[i] = arr[i] with { M = v };` | 元素可写（数组、`List<T>` 索引器有 set）；只读集合（`IReadOnlyList`、`ImmutableArray`）→ FB-1 |
| MS-8 | lambda/本地函数体内上述形态 | 同 MS-1~MS-7（捕获局部变量经闭包可写） | `in` 捕获 → FB-1 |
| MS-9 | in-tree 方法内 `ref R p` 形参上的成员赋值 `p.M = v;` | `p = p with { M = v };`（ref 形参可写，穿透有效） | — |
| MS-10 | `ref`/`out` 传参实参本身是 record（`M(ref p)`） | in-tree 的 M：不改签名，M 体内按 MS-9 处理；**外部（BCL/三方）方法的 `ref` record 实参：无法证明其不修改 → 保守 FB-1**（诊断建议改为值传递或手工处理）。`out` 形参成员赋值本身非法 C#，不存在 |
| MS-11 | 对 record 字段取 `ref` 传给外部修改函数（`Interlocked.Increment(ref r.Count)`） | 转换后字段为 init 属性/readonly → FB-1 | — |
| MS-12 | `with` 表达式 | 不动（本就是非破坏性） | — |
| MS-13 | 迭代变量成员赋值 `foreach (var p in list) p.X = 1;`（record class，今日可编译） | foreach 迭代变量只读，无法回写 → FB-1 | 原语义也不影响集合元素，跳过该 record |
| MS-14 | 动态（`dynamic`）或反射（`SetValue`）修改 | 静态不可见 → 分析期扫描到 `dynamic` 接收者/`SetValue` 用法则 FB-1，否则文档化为运行时已知限制 | — |
| MS-15 | 方法组/委托绑定到被 MM 改签名的方法（`new Func<P,T>(p.M)`） | FB-1（签名已变） | `nameof(M)` 不受影响 |
| MS-16 | 顶层语句/字段初始化器/特性实参等不可提升语句的上下文内用点 | FB-1（无法插入前置语句） | — |

### 4.4 mutating 方法通用迁移（MethodMigrator）

**核心变换（自拷贝思想的结构体安全版）**：readonly struct 变量自身不可再改写成员，因此不能简单 `var self = this`。通用做法是**字段提升 + 出口合成**：

1. 收集方法体（含其内本地函数/lambda）**直接或经调用图传递**写到的全部实例字段/属性 F1..Fn（被调方法先迁移，调用图自底向上——仿 `CallGraphBuilder`）。
2. 方法入口提升：`var f1 = this.F1; ... var fn = this.Fn;`（`this.F` 若是转换后 init 属性则读，若是私有字段在 record struct 中已 readonly——提升读取皆合法）。
3. 方法体内对 `this.Fi` / `Fi` 的**读写**全部替换为局部 `fi`；`this = expr;` → `f1 = expr.F1; ... fn = expr.Fn;`；对其他实例成员（只读属性、方法）的访问保留 `this.`。
4. 每个出口（含隐式落尾）合成：`return this with { F1 = f1, ..., Fn = fn };`（`this with` 可设置本类型内可访问的 init 成员；私有 init 属性在本类型内可访问）。
5. 签名变化：
   - **void 方法** `void M()` → `R M()`；in-tree 语句调用点 `recv.M();` → `recv = recv.M();`（recv 可写，否则 FB-1）。ctor 内 `this.M();` → `this = this.M();`（struct ctor 内 this 赋值合法）。
   - **非 void 方法** `T M()` → `R M(out T result)`；体内 `return E;` → `{ result = E; return this with {...}; }`。调用点：丢弃 `p.M();` → `p = p.M(out _);`；消费 `var r = p.M();` → `p = p.M(out var r);`；嵌套表达式 `f(p.M())` → 提升前置语句 `p = p.M(out var r); f(r);`（仅在可插入语句的上下文——方法体/顶层语句，否则 FB-1/MS-16）。
   - 表达式体方法 → 展开为块体再变换。
6. **约束与失败**（FB-1）：
   - 迭代器方法（`yield`）/ `async` 方法：返回类型被协议占据，无法改 → FB-1。
   - 方法为 override/接口实现：signature 变化破坏契约 → 走 §4.5 路径（显式桩 + 接口加 With 成员）而非直接改签名；virtual 层次内 in-tree 全部 override 需一致迁移。
   - **record class**：私有状态方法不迁移（RC-4 保留私有可变性）。仅当对外 init 属性在自身方法内被赋值时应用同一字段提升变换（提升该 init 属性，出口 `return this with {...}`）。注意：迁移后通过别名观察原地修改的行为消失——这是特性固有语义变化（§4.2 已文档化）。
   - 迁移后 `FormatAndValidate` 失败 → FB-1。

### 4.5 接口连带改写（InterfaceRippler）

**触发**：已转换 record 实现接口 I（in-tree），且 I 的成员 m 满足任一：(a) m 是带 set 的属性；(b) m 是被 record 以迁移后签名实现的方法（MM 改了签名）。

**机制（用户指定：接口多添加一个非破坏成员）**：

- **IR-1 · 接口 I 新增成员**（不改任何既有成员，外部实现者源码兼容性不破坏契约面）：
  - `T P { get; set; }` → 追加 `I WithP(T value);`
  - `void M()` / `T M()` → 追加 `I WithM();`（语义：返回修改后的等价实例）
- **IR-2 · record 侧**：
  - 旧成员改为**显式实现桩**：`T I.P { get => P; set => throw new NotSupportedException(...); }`（编译通过、行为降级）；方法同理 `void I.M() => throw ...`。
  - 实现 I 的新成员（显式）：`I I.WithP(T v) => this with { P = v };`
  - 另生成公共强类型版本：`public R WithP(T v) => this with { P = v };`（避免 struct 装箱、返回精确类型；跨接口同名冲突时省略公共版、显式实现各自接口，调用点经接口走）。
- **IR-3 · in-tree 其他实现者**（非 record 的普通 class/struct）自动补齐：fluent 实现 `public I I.WithP(T v) { P = v; return this; }`（原地修改后返回自身——对该实现者保持原修改语义；多态调用点行为一致）。
- **IR-4 · 调用点改写**：
  - 静态类型为 record：`r.P = v;` / `r.M();` → `r = r.WithP(v);` / `r = r.WithM();`（公共强类型版）
  - 静态类型为接口 I：`i.P = v;` / `i.M();` → `i = i.WithP(v);` / `i = i.WithM();`（i 可写，否则 FB-1）
- **IR-5 · 接口继承**：成员加到声明它的基接口（符号模型定位），派生接口/实现链自然继承。
- **IR-6 · 泛型接口** `I<T>`：按符号构造实例化生成，语法生成时携带正确 type argument。
- **IR-7 · 失败**（FB-1/FB-3）：
   - I 为**外部接口**（BCL/三方，源不在树内）：无法加成员 → record 旧成员显式桩降级 + 经 I 类型接收者的修改点无法改写 → **FB-1 跳过该 record**（除非 overrides.force）。
   - I 含默认实现（DIM）成员被触发：新增成员一律无默认体（抽象），实现者按 IR-3 补齐。
   - 接口新增成员与既有成员冲突（签名碰撞）：重命名 `WithX__cs2j` 或 FB-1。

### 4.6 兜底规则（FB）

| # | 情形 | 行为 |
|---|---|---|
| FB-1 | 单 record 存在任何不可改写点 | **原子跳过该 record**（不转换、原样保留，包括已计划的部分改写全部撤销）+ 诊断（文件:行列、record 名、原因、建议） |
| FB-2 | record 继承层次（class）部分不可转换 | 整层次跳过 + 单条诊断 |
| FB-3 | 外部接口修改语义触发且无法连带 | FB-1；`overrides.force` 可强制（接受桩降级语义） |
| FB-4 | `config\RecordReadOnlyOverrides.json` | `force`：强制转换（不可改写点降级为显式桩/文档化语义变化）；`skip`：显式排除 |
| FB-5 | 中间树最终验证失败（FormatAndValidate 兜底） | 回退该文件全部改动 + 诊断 |

**不变式**：中间源码树在预处理后永远可编译（与 TupleToStruct 相同契约）；每个 record 的转换是原子的（全转或全不转）。

## 5. 边界情况清单（完整性核对表）

| 情况 | 处理 | 规则 |
|---|---|---|
| `readonly record struct` 已存在 | 跳过 | RS-18 |
| positional record struct / 带体 / 混合 | 加 readonly，体按 RS 表 | RS-1 |
| `record class` 无体 / positional | 无声明变化；仅当有可变对外成员才改 | RC-2/3 |
| partial 多部件 | 全部件加 readonly；计划跨部件合并 | RS-2 |
| 嵌套 record（含泛型容器内） | 独立按符号处理 | §3.2 |
| 泛型 record `record struct P<T>(T X)` | 正常（type argument 语法不动） | — |
| abstract/sealed record class | 正常；abstract 可变成员按 RC-6 层次一致 | RC-6 |
| record class 继承层次 | record 只能继承 record；层次内任一成员不可转换 → 整层次跳过 | FB-2 |
| 显式接口实现（只读契约） | 不动 | RS-15 |
| 事件、volatile、ref-return、fixed/unsafe、`StructLayout.Explicit`+字段属性化 | FB-1 | RS-10/11/17 |
| 迭代器/async mutating 方法 | FB-1 | MM-6 |
| `this = expr`（方法体/ctor） | 迁移/保留 | MM-3, RS-8 |
| ctor 字段初始化、`base(...)`/`this(...)` 链 | 不动 | RS-7 |
| 表达式树内赋值 | C# 本身非法，不存在 | — |
| `foreach` 迭代变量赋值 | FB-1 | MS-13 |
| 装箱防御拷贝行为差异 | 文档化（readonly struct 方法不再防御拷贝），非阻断 | — |
| `Interlocked`/`Volatile`/`Marshal` 对 record 字段 | FB-1 或文档化运行时限制 | MS-11/14 |
| 值相等/`GetHashCode`/`ToString`/`Deconstruct`/`==` 合成成员 | 编译器再合成，不受影响 | — |
| `with` 空初始化器 / 嵌套初始化 | 不动 | MS-12 |
| 元组元素 `t.Item1.M = v` | `t.Item1 = t.Item1 with { M = v };`（可写元组字段） | MS-1 |
| span/stackalloc 元素 | 同 MS-7 | MS-7 |
| 空条件赋值（C#14 `?.` 赋值） | 三元展开 | MS-5 |
| C# `field` 关键字半自动属性 | set→init 保体；验证不过则 FB-1 | RS-4 |
| 单文件 verb 模式跨文件用点 | 诊断跳过 | §3.3 |
| 同名 With 成员冲突 | 显式实现消歧或改名 | IR-7 |
| 方法组绑定被迁移签名 | FB-1 | MS-15 |
| 静态可变成员 | 不动 | RS-12 |

## 6. 验证与错误处理

- **逐文件**：`FormatAndValidate`——改动区间格式化 + 重解析 + 语义诊断零错误，失败回退（FB-5）。
- **整树**：全部文件重写后重建 `CSharpCompilation` 比对基线错误集，不得新增错误；项目级测试额外对中间树执行 `dotnet build`（TupleToStructProjectTests 模式）。
- **确定性**：文件处理顺序稳定（按路径排序）、生成成员命名确定，无时间戳依赖。
- **诊断**：RR0xx 码 + MSBuild 格式 `文件(行,列): warning/error RR0xx: 描述`；统计含：转换的 record struct/class 数、成员改写数、用点改写数、方法迁移数、接口连带数、跳过数（按原因分组）。
- **退出码**：`--strict`（后续可加）时存在跳过即非零；默认成功（跳过仅警告）。

## 7. 测试策略（测试先行，Agent.md 规则）

1. **单元测试** `tests\CSharpToJava.ProjectTests\RecordReadOnlyRewriterUnitTests.cs`（TupleToStructRewriterUnitTests 模式：内联源码 → `CSharpCompilation` → `RewriteFile` → 断言输出文本/归一化 + `AssertOutputCompiles` + 无残留可变成员断言）：
   - 每个 RS/RC/MS/MM/IR 规则至少 1 正例；每个 FB 至少 1 反例（跳过 + 诊断码断言）
   - 重点：partial、嵌套、泛型、非 void 方法 out 迁移调用点、ref 形参体内改写、接口连带（属性/方法/多实现者/泛型接口/外部接口跳过）、record class 契约转换、ctor 内 this 赋值、已 readonly 跳过
2. **项目级测试** `RecordReadOnlyProjectTests.cs` + 新 fixture `tests\SampleRecordSolution\`（Lib + App + sln，覆盖全部规则类别），`Content Include` 进 `CSharpToJava.ProjectTests.csproj`；断言中间树文件内容 + `dotnet build` 中间树成功。
3. **一致性测试**：基于 `ProjectPreprocessSource` 构建，`ProjectPreprocessorsConsistentTests` 自动保持（如需扩展断言则同步更新）。
4. **CLI 测试**：`--record-readonly` 开关接线、verb 双模式、`.cs2j-record-readonly-src` 保留目录、统计输出。

## 8. 工作量与实施里程碑（供 writing-plans 展开）

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 | 骨架 + Analyzer + RS-1/2/3/5 声明改写 + FormatAndValidate + CLI 四处接线 + 基础单测 | Core ~800 行, CLI ~700 行 |
| M2 | MutationSiteRewriter（MS-1~MS-9, MS-11~13）+ 单测 | ~700 行 |
| M3 | MethodMigrator（MM 全部 + 调用图）+ 单测 | ~600 行 |
| M4 | InterfaceRippler（IR 全部 + 桩 + 实现者补齐）+ 单测 | ~500 行 |
| M5 | record class 契约转换（RC）+ 层次一致性 + 单测 | ~300 行 |
| M6 | overrides 配置、诊断打磨、SampleRecordSolution、项目级测试、文档 | ~500 行 + fixture |

总量与 tuple-to-struct 特性（~5300 行含测试 fixture）相当。

## 9. 明确不做的事

- 不改 RecordTransformer / StructTransformer / TypeGroupResolver / Pipeline / 任何 C#→Java 代码（后续若需让 `readonly record struct` 在 Java 侧产出 value record，另行立项）。
- 不处理 record 之外的类型（普通 struct 由 MakeReadOnly 负责）。
- 不保证外部程序集对被改签名方法/接口的二进制兼容（源码级转换边界）。
- 不追踪反射/动态的运行时修改（静态不可见，文档化）。
