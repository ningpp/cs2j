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
| 接口兜底 | 连带改接口：接口新增 With 风格成员（对 in-tree 实现者非破坏；对外部实现者的破坏性见 IR-1 注与 §9），record 旧修改成员显式实现降级为抛 `NotSupportedException` 的桩，in-tree 普通 class 实现者自动补 fluent 实现 | 见 §4.5 |
| 不可行情况 | 除接口连带可解外，其余客观不可改写情况 → 诊断 + 原子跳过该 record | 保证中间树可编译 |
| 管线集成 | 独立预处理器、convert-project **默认关**（`--record-readonly` 显式开启）+ 独立 verb `record-readonly` | 插入在 Generic 之后、MakeReadOnly 之前 |
| 架构 | 方案 A：语义驱动三阶段（分析 → 计划 → 逐文件重写 + 整树验证/计划级回退），复用 TupleToStruct 的三阶段模板与 `FormatAndValidate` | 见 §3.2 |
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
  InterfaceRippler.cs               # Phase 3d：接口加 With 风格成员 + 实现者补齐（破坏性边界见 IR-1 注）
  RecordReadOnlyStatistics.cs       # 统计
  RecordReadOnlyDiagnostics.cs      # 诊断码与描述（RR0xx 系列）

src\CSharpToJava.CLI\ProjectRecordReadOnlyPreprocessor.cs
  # 中间树复制/枚举/编译/RewriteFile/回写/restore，复用 ProjectPreprocessSource 全套管线
  # IntermediateDirectoryName = ".cs2j-record-readonly-src"

config\RecordReadOnlyOverrides.json # { "force": ["Namespace.Type"], "skip": ["Namespace.Type"] }
```

### 3.2 三阶段数据流

1. **分析**（全解决方案一次）：对源码树建 `CSharpCompilation`（`CSharpParseOptions(LanguageVersion.Latest)` + 合成 `__cs2j_global_usings.g.cs`，与 TupleToStruct 相同）。注：`LanguageVersion.Latest` 要求 Roslyn 版本支持 C# 13/14（`field` 关键字、空条件赋值），实现时以项目已引用的 Microsoft.CodeAnalysis 版本为准，解析不到的语法按语法错误走 FB 兜底，不做降级猜测。产出：
   - record 符号清单（partial 多部件合并、嵌套、泛型，`INamedTypeSymbol.IsRecord` + `TypeKind` 区分 struct/class）
   - 每个 record 的可变成员清单（set 访问器属性、非 readonly 实例字段、带 set 的索引器、事件、ref-returning 成员等）
   - 全树 mutation 用点：对 record 类型成员的写访问（赋值 / 复合赋值 / `++`/`--` / C#14 空条件赋值 / 链式赋值 / 嵌套成员链 / 索引器赋值 / `ref`/`out` 传递）
   - 接口实现关系图：含修改语义成员的接口 + 其全部 in-tree 实现者
   - record 方法调用图（迁移顺序依赖，复用既有 `ReadOnlyStructMaker\CallGraphBuilder` 的构建思路：以语义符号为键、自底向上拓扑排序）
2. **计划**（`RecordConversionPlan`）：每个 record 一份声明级步骤 + 方法迁移清单 + 接口连带清单；按文件分组。计划是**原子的**：record 的任一改写步骤不可行 → 整个 record 计划作废（跳过 + 诊断）。
3. **重写**（每文件）：`DeclarationRewriter`/`MutationSiteRewriter`/`MethodMigrator`/`InterfaceRippler` 的产物在同一轮 `CSharpSyntaxRewriter` 中按计划应用（`SyntaxAnnotation("Cs2jRecordReadOnlyRewrite")` 标注改动区间），随后走 TupleToStruct 同款 **`FormatAndValidate`**：仅格式化改动区间，并**重新解析 + 语义诊断**。注（修订，原子性修正）：验证与回退的单位是 **record 计划**而不是单个文件——record 的声明与其用点常分布在不同文件，逐文件回退会留下"声明已转、用点未转"的不可编译中间树。因此：全部文件改写完成后对**整树**重建 `CSharpCompilation` 验证；任何新增错误定位到其所属 record 计划，回退该计划涉及的**所有文件**的改动后重新验证，迭代至收敛；无法归属到单一 record 的错误 → 回退本次运行全部改动并升级为诊断。单文件 `FormatAndValidate` 仅作为快速预筛，不作为回退粒度依据。

### 3.3 CLI 接线（Program.cs 四处，均按既有模式）

- `ConvertProjectOptions`：`[Option("record-readonly", Default = false)]`（默认关）。
- `ConvertProject` 执行块：`if (opts.RecordReadOnly)` 时调用 `PreprocessAsync`，打印统计，失败即中止；`opts.Source = result.PreprocessedSourcePath`。位置：**Unsigned/Generic 之后、`ProjectReadOnlyStructPreprocessor` 之前**。
- `GetPreservedDestinationDirectories`：注册 `.cs2j-record-readonly-src`。
- 独立 verb `RecordReadOnlyVerbOptions`（单文件 `-i/-o` + 项目 `-s/-d` 双模式 + `--force`），注册进 `MainImpl` 的 `ParseArguments/MapResult`。

**单文件模式限制**：只分析单文件语义模型，跨文件用点/接口实现者不可见 → 仅转换自包含于该文件的 record，其余诊断跳过。判断标准（可测试）：record 的声明全部 partial 部件、全部 mutation 用点、全部 mutating 方法调用点、其接口连带的全部 in-tree 实现者、以及继承层次（class）的全部成员均在同一文件内，缺一不可。

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
| RS-5 | 公共/受保护/内部可变实例字段 `public int X;` | → `public int X { get; init; }`（仿 ReadOnlyStructMaker 既有 `ApplyPublicFieldToProperty` 变换）；字段上的 attribute 迁移到属性；字段初始化器随之迁移为属性初始化器（`public int X = 5;` → `public int X { get; init; } = 5;`） |
| RS-6 | 私有可变实例字段 | 分两种：(a) 构造后无赋值点 → 直接加 `readonly`（满足 readonly struct 的编译器约束，`with` 永不需要设置它）；(b) 构造后有赋值点（被方法/属性修改）→ 提升为 `private T F { get; init; }`（readonly 字段无法被 `with` 设置，MM 出口合成依赖 init 属性），赋值点由 MM/MS 规则改写，改不动则 FB-1 |
| RS-7 | 构造函数/字段初始化器 | 不动；readonly 字段在 ctor 内赋值合法 |
| RS-8 | `this = expr;`（方法体内） | 按 MM-3 迁移；出现在 ctor 内则合法保留 |
| RS-9 | 手写（非自动）属性带赋值 setter 体 | `set` → `init` 保体（注：语言规范上 init 访问器与构造函数一样豁免 readonly struct 的 `this` 只读约束，体内写字段合法，故无需仅限平凡后备字段模式）；`FormatAndValidate` 验证不过则 FB-1 |
| RS-10 | 实例事件（字段式或 add/remove 式） | readonly struct 无法持有可变委托存储 → FB-1 |
| RS-11 | `volatile` 实例字段、`ref` 返回成员、`fixed` 缓冲区（unsafe）、`unsafe` 指针取址 | FB-1 |
| RS-12 | 静态可变成员 | 不动（readonly 仅约束实例） |
| RS-13 | 自动 `get; init;`、`get;`、手写只读逻辑属性 | 不动 |
| RS-14 | 隐式实现的接口可变成员（如 `public int Count { get; set; }` 实现 `ICounter.Count`） | init ≠ set，隐式实现失效 → 转**显式实现桩** + IR 连带（见 §4.5） |
| RS-15 | 显式接口实现（只读契约如 `IComparable`、`ITuple`） | 不动 |
| RS-16 | attribute、XML 文档注释、运算符、嵌套类型 | 语法原样保留（trivia 保持） |
| RS-17 | 显式 `StructLayout(LayoutKind.Explicit)` 且涉及 RS-5 字段→属性 | 布局语义丢失 → FB-1（保守） |
| RS-18 | 已是 `readonly record struct` | 跳过（统计为 AlreadyReadOnly） |
| RS-19 | 带 `set` 访问器的索引器 `public int this[int i] { get; set; }` | FB-1（`with` 表达式语法上无法表达索引器赋值，索引器 set 语义无法用 init 迁移） |
| RS-20 | `required` 成员（C#11，属性/字段） | 与 RS-3/RS-5 同处理；`required` 修饰符保留（`required init` 合法），不改语义 |

### 4.2 record class 声明级规则（目标：对外契约不可变）

C# 无 `readonly record class` 语法，转换目标是**语义 readonly**：对外可观察成员全部不可设置。理由：(a) 无编译器强制，"内部完全不可变"不可验证；(b) 惯用法上不可变类允许私有惰性缓存等私有状态；(c) 私有字段无法用 `with` 表达拷贝。

| # | 输入形态 | 转换 |
|---|---|---|
| RC-1 | 声明语法 | **不变**（无 readonly 语法） |
| RC-2 | public/protected/internal/protected internal 的 `{ get; set; }` / `{ get; private set; }` 属性 | set → init（含 private set → private init）。注（修订）：private set 仅本类型内可见，对外契约本不含它；此处统一转是保守策略（减少"对外 init、对内仍 set"的特例分支），代价是本类型方法内的赋值点须按 §4.4 迁移。与 RC-4 不冲突：RC-4 指**整体私有**的成员，本条指对外可见属性上的私有访问器 |
| RC-3 | public/protected/internal 可变实例字段 | → `{ get; init; }` 属性（同 RS-5，含初始化器迁移） |
| RC-4 | 整体私有的成员（private 字段、private 属性及其 setter 体，仅内部使用） | **保留可变**（内部实现细节；如惰性缓存 `_cache`） |
| RC-5 | 手写属性 setter 体（对外可见）、事件、ref-returning 成员 | 手写 set 体 → `init` 保体（class 中 init 访问器写字段无限制；否则"对外契约不可变"目标不成立）；事件**保留**——注：add/remove 订阅不视为成员赋值用点，属于已知语义保留点，如需严格禁止订阅变异请用 overrides.skip；对外可见的 ref-returning 成员会泄漏内部可变引用 → FB-1（保守），私有 ref-returning 保留 |
| RC-6 | virtual/abstract/override 的对外可变属性 | 同 RC-2，全继承层次（in-tree）一致转换 |
| RC-7 | 隐式实现的接口可变成员 | 同 RS-14：显式实现桩 + IR 连带 |
| RC-8 | 继承层次中基类 record 未转换（被 FB 跳过）而派生类要转换（或反之） | 层次不一致 → 整层次一起跳过 + 单条诊断（FB-2） |
| RC-9 | 带 `set` 访问器的（对外可见）索引器 | FB-1（同 RS-19：`with` 无法表达索引器赋值） |
| RC-10 | 私有/不可访问的拷贝构造函数（record class 特有：`with` 依赖可访问的拷贝 ctor） | 若 `with` 在改写点不可访问 → FB-1；若仅类型外不可访问而改写点皆在类型内，可转换但需在诊断中提示该限制 |

**record class 已知语义变化（特性固有，用户已接受）**：`a.X = 1` 改写为 `a = a with { X = 1 }` 后，通过别名观察修改的行为改变（原可变语义下 `b = a; a.X = 1; b.X` 可见 1；转换后不可见）。这是"强制不可变"特性的本质，写入文档与诊断提示。

### 4.3 用点（mutation site）改写规则

适用对象：对**已转换 record** 的成员写访问（语义模型判定接收者静态类型）。`with` 表达式、模式匹配、解构、构造调用均非用点，不动。

| # | 输入形态 | 转换 | 条件/失败 |
|---|---|---|---|
| MS-1 | `recv.M = expr;` | `recv = recv with { M = expr };` | recv 须为可写左值：局部变量、非 readonly/in 形参、字段、数组元素、`List` 等带 set 的索引器元素、span 元素、元组元素。不可写（readonly 局部、`in` 形参、foreach 迭代变量、get-only 属性/方法返回值）→ FB-1。**限制（修订）**：`with` 改写使 recv 表达式被求值两次（左值与右值），故 recv 表达式必须无副作用（标识符/简单成员访问/纯索引器）；含调用或复合索引（`arr[F()]`）→ FB-1 |
| MS-2 | 相邻多条同接收者赋值 `p.X = a; p.Y = b;` | 合并为 `p = p with { X = a, Y = b };`（仅相邻语句间无其他语句时；可读性优化） | **语义前提（修订）**：后续语句的右值不得读取 recv 上本轮已改写的成员（否则合并后读到旧值）。示例违例：`p.X = a; p.Y = p.X;`（原读新值，合并后读旧值）→ 不作为合并处理，逐条改写或 FB-1 |
| MS-3 | 复合赋值 `recv.M += v;`（及 `-=`、`??=` 等） | `recv = recv with { M = recv.M + v };` | **限制（修订）**：该改写读取 `recv.M` 一次、写一次，仅当 M 的 getter 为自动属性或无副作用平凡访问器（读一次、无外部可观察副作用）时等价；无法证明纯净（含方法调用/惰性副作用）→ FB-1（保守兜底，不再仅作"文档化假设"）。其余同 MS-1 |
| MS-4 | `recv.M++;` / `--`（语句位置） | `recv = recv with { M = recv.M + 1 };` | 表达式位置消费值（如 `x = p.M++`）→ FB-1；getter 纯净前提同 MS-3 |
| MS-5 | 空条件赋值（C#14）`recv?.M = expr;`（仅 record class；同族 `?.M += v`、`?.M++` 一并覆盖） | `recv = recv == null ? null : recv with { M = expr };` | 同 MS-1；recv 表达式须无副作用（否则三元改写会多次求值） |
| MS-6 | 链式赋值 `a.M = b.M = v;`（语句位置） | 拆分：`b = b with { M = v }; a = a with { M = v };` | 表达式位置（实参、条件内等）→ FB-1 |
| MS-7 | 数组/集合元素 `arr[i].M = v;` | `arr[i] = arr[i] with { M = v };` | 元素可写（数组、`List<T>` 索引器有 set）；只读集合（`IReadOnlyList`、`ImmutableArray`）→ FB-1 |
| MS-8 | lambda/本地函数体内上述形态 | 同 MS-1~MS-7（捕获局部变量经闭包可写） | `in` 捕获 → FB-1 |
| MS-9 | in-tree 方法内 `ref R p` 形参上的成员赋值 `p.M = v;` | `p = p with { M = v };`（ref 形参可写，穿透有效） | — |
| MS-10 | `ref`/`out` 传参实参本身是 record（`M(ref p)`） | in-tree 的 M：不改签名，M 体内按 MS-9 处理；**外部（BCL/三方）方法的 `ref` record 实参：无法证明其不修改 → 保守 FB-1**（诊断建议改为值传递或手工处理）。注（修订）：`out` 形参在方法体内被**先赋值后**再写成员是合法 C#（如 `void M(out R r) { r = ...; r.X = 1; }`），须按 MS-9 同法处理，而非"不存在" |
| MS-11 | 对 record 字段取 `ref`/`out` 传给外部修改函数（`Interlocked.Increment(ref r.Count)`、`int.TryParse(s, out r.N)`） | 转换后字段为 init 属性/readonly → FB-1 | — |
| MS-12 | `with` 表达式 | 不动（本就是非破坏性） | — |
| MS-13 | 迭代变量成员赋值 `foreach (var p in list) p.X = 1;` | foreach 迭代变量只读，无法回写 → FB-1（跳过该 record）。注（修订）：对 record class 原语义**会**修改集合元素（引用别名），行为有实际意义；对 record struct 原本就是拷贝、无效果。两种情况均 FB-1，理由不同 |
| MS-14 | 动态（`dynamic`）或反射（`SetValue`）修改 | 静态不可见 → 分析期扫描到 `dynamic` 接收者/`SetValue` 用法则 FB-1，否则标注为运行时已知限制（见 §9） | — |
| MS-15 | 方法组/委托绑定到被 MM 改签名的方法（`new Func<P,T>(p.M)`） | FB-1（签名已变） | `nameof(M)` 不受影响 |
| MS-16 | 字段初始化器/特性实参/表达式体成员/查询表达式等**无法插入前置语句**的表达式上下文内用点 | FB-1 | 顶层语句与方法体均为语句上下文，**不在此列**（可正常插入前置语句，按 MS-1~MS-9 处理） |
| MS-17 | 嵌套成员链 `a.B.C = v;`（中间成员 `B` 本身是已转换 record 且经 get/init 属性或字段暴露） | 递归 with 重建：`a = a with { B = a.B with { C = v } };` | 链条上每一层的接收者都须可写、每层容器成员须可经 `with` 设置（init 可访问）；任一层不满足（get-only 容器成员、非 record 中间类型等）→ FB-1 |
| MS-18 | 索引器赋值 `r[i] = v;`（record 自身声明的索引器） | FB-1（`with` 无法表达索引器赋值，对应 RS-19/RC-9 声明级已跳过该 record） | — |
| MS-19 | in-tree 扩展方法修改 record（`static void Bump(this R r)`，调用点 `r.Bump();`） | 视同 mutating 方法按 MM 迁移：`this R r` 形参参与字段提升，签名 `void Bump(this R r)` → `R Bump(this R r)`，调用点 `r = r.Bump();`；外部（BCL/三方）扩展方法的修改静态不可见，无需处理 | — |

### 4.4 mutating 方法通用迁移（MethodMigrator）

**核心变换（自拷贝思想的结构体安全版）**：readonly struct 变量自身不可再改写成员，因此不能简单 `var self = this`。通用做法是**字段提升 + 出口合成**。规则编号 MM-1~MM-7（注：§4.1/§5 对 MM-3、MM-6 的引用指向以下编号）。

- **MM-1 · 影响集收集**：收集方法体（含其内本地函数/lambda）**直接或经调用图传递**写到的全部实例字段/属性 F1..Fn；被调方法先迁移，拓扑顺序自底向上（复用既有 `ReadOnlyStructMaker\CallGraphBuilder` 的符号级调用图构建与排序，不自造第二套；若调用图含环 → 无法定序 → FB-1）。
- **MM-2 · 入口提升与替换**：入口生成 `var f1 = this.F1; ... var fn = this.Fn;`（`this.F` 若为转换后 init 属性则读；若为 record struct 中已 readonly 的私有字段，提升读取亦合法）；方法体内对 `this.Fi` / `Fi` 的**读写**全部替换为局部 `fi`；对其他实例成员（只读属性、只读方法）的访问保留 `this.`。
- **MM-3 · `this = expr;` 处理**：`this = expr;` → `f1 = expr.F1; ... fn = expr.Fn;`。RS-8 引用本规则。注：出现在 ctor 内时本就可合法保留（ctor 可赋值 `this`），仅在普通方法体内需迁移。
- **MM-4 · 出口合成**：每个出口（含隐式落尾）合成 `return this with { F1 = f1, ..., Fn = fn };`（`this with` 可设置本类型内可访问的 init 成员；私有 init 属性在本类型内可访问；record class 走其拷贝构造语义）。
- **MM-5 · 签名变化与调用点改写**：
  - **void 方法** `void M()` → `R M()`；in-tree 语句调用点 `recv.M();` → `recv = recv.M();`（recv 可写，否则 FB-1）。ctor 内 `this.M();` → `this = this.M();`（struct ctor 内 `this` 赋值合法）。
  - **非 void 方法** `T M()` → `R M(out T result)`；体内 `return E;` → `{ result = E; return this with {...}; }`。调用点：丢弃 `p.M();` → `p = p.M(out _);`；消费 `var r = p.M();` → `p = p.M(out var r);`；嵌套表达式 `f(p.M())` → 提升前置语句 `p = p.M(out var r); f(r);`（仅在可插入语句的上下文——方法体/顶层语句，否则 FB-1/MS-16）。
  - 表达式体方法 → 展开为块体再变换。
- **MM-6 · 约束与失败**（一律 FB-1）：
  - 迭代器方法（`yield`）/ `async` 方法：返回类型被协议占据，无法改 → FB-1。（§5 中"迭代器/async mutating 方法"引用本编号）
  - 方法为 override/接口实现：签名变化破坏契约 → 走 §4.5 路径（显式桩 + 接口加 With 成员）而非直接改签名；virtual 层次内 in-tree 全部 override 需一致迁移。
  - **partial 方法**：签名由双方声明绑定，改签名破坏绑定 → FB-1（保守）。
  - **ctor 内调用迁移方法**：`this = this.M();` 需 `this` 已完全明确赋值，否则读到未赋值 `this` 编译失败 → 由 `FormatAndValidate` 兜底 FB-1。
  - **属性访问器/getter 体内修改其他成员**（getter 在 readonly struct 下 `this` 只读）→ 无法迁移 → FB-1。
  - **record class**：私有状态方法不迁移（RC-4 保留私有可变性）。仅当对外 init 属性在自身方法内被赋值时应用同一字段提升变换（提升该 init 属性，出口 `return this with {...}`）。注意：迁移后通过别名观察原地修改的行为消失——这是特性固有语义变化（§4.2 已文档化）。
  - 迁移后 `FormatAndValidate` 失败 → FB-1。
- **MM-7 · 扩展方法**：in-tree 扩展方法修改 record（形参 `this R r`）按 MM-1~MM-6 同法迁移（签名 `void Bump(this R r)` → `R Bump(this R r)`，调用点 `r = r.Bump();`），见 MS-19。

### 4.5 接口连带改写（InterfaceRippler）

**触发**：已转换 record 实现接口 I（in-tree），且 I 的成员 m 满足任一：(a) m 是带 set 的属性；(b) m 是被 record 以迁移后签名实现的方法（MM 改了签名）。

**机制（用户指定：接口多添加一个成员，对 in-tree 实现者非破坏）**：

- **IR-1 · 接口 I 新增成员**（不改任何既有成员）：
  - `T P { get; set; }` → 追加 `I WithP(T value);`
  - `void M(...)` / `T M(...)` → 追加 `I WithM(...);`（参数原样携带；原返回非 void 的方法迁移后为 `R M(out T result)` 形态，对应 `I WithM(out T result);`；语义：返回修改后的等价实例）
  - 注（修订）：新增抽象接口成员对 **in-tree** 实现者非破坏（由 IR-3 自动补齐），但对**外部实现者**构成源码/运行时破坏（未实现该成员的第三方实现类将编译失败或运行期 `TypeLoadException`/`MissingMethodException`）——这是接口加成员的固有代价，列入 §9 已知限制；若需缓解可给新成员加默认实现体（DIM），代价是实现者静默走默认体，默认关闭、由 overrides 控制。
- **IR-2 · record 侧**：
  - 旧成员改为**显式实现桩**：`T I.P { get => P; set => throw new NotSupportedException(...); }`（编译通过、行为降级）；方法同理 `void I.M() => throw ...`。
  - 实现 I 的新成员（显式）：`I I.WithP(T v) => this with { P = v };`
  - 另生成公共强类型版本：`public R WithP(T v) => this with { P = v };`（避免 struct 装箱、返回精确类型；跨接口同名冲突时省略公共版、显式实现各自接口，调用点经接口走）。
- **IR-3 · in-tree 其他实现者**（非 record 的普通 class/struct）自动补齐：fluent 显式实现 `I I.WithP(T v) { P = v; return this; }`（注：显式接口实现不允许访问修饰符，无 `public`；原地修改后返回自身——对该实现者保持原修改语义；多态调用点行为一致）。
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
| FB-5 | 中间树最终验证失败（FormatAndValidate 兜底） | **按 record 计划回退**：错误定位到所属 record 计划 → 回退该计划涉及的所有文件改动 + 诊断，重新验证迭代收敛；无法归属到单一 record → 回退本次运行全部改动 + 诊断（修订：逐文件回退粒度不足以维持不变式，见 §3.2 第 3 步） |

**不变式**：中间源码树在预处理后永远可编译（与 TupleToStruct 相同契约）；每个 record 的转换是原子的（全转或全不转）。该不变式在以下路径均成立：(a) FB-1/FB-2/FB-3 在计划阶段作废整个 record 计划，改动尚未落盘；(b) FB-5 以 record 计划为单位回退，且最终整树重编译通过才交付；(c) 单文件模式（§3.3）只允许自包含 record，计划天然不跨文件；(d) `FormatAndValidate` 仅作为预筛，其通过不构成交付依据，交付以整树重编译为准。

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
| 索引器（自声明、带 set） | FB-1（声明级即跳过） | RS-19/RC-9；用点 `r[i]=v` 见 MS-18 |
| 嵌套成员链 `a.B.C = v`（B 为 record） | 递归 with 重建；任一层不可写/不可 init → FB-1 | MS-17 |
| in-tree 扩展方法修改 record | 视同 mutating 方法迁移 | MS-19, MM-7 |
| `required` 成员 | 与普通成员同处理，修饰符保留 | RS-20 |
| record class 私有/不可访问拷贝 ctor | 改写点处 `with` 不可用则 FB-1 | RC-10 |
| `out` 形参赋值后再写成员 | 同 ref 形参处理（非"不存在"） | MS-10 |
| 对外可见的手写 setter 体（record class） | set→init 保体 | RC-5 |
| 事件订阅/取消订阅（record class） | 保留（不视为成员赋值用点，已知语义保留点） | RC-5 |
| 接口新增抽象成员的外部实现者 | 源码/运行时破坏（已知限制，可用 DIM 缓解） | IR-1, §9 |
| 复合赋值但 getter 不纯（含调用/副作用） | FB-1（保守） | MS-3 |
| 接收者表达式有副作用（`arr[F()].M = v`） | FB-1（`with` 改写导致多次求值） | MS-1/MS-5 |
| 只读集合/只读索引器元素 `ro[i].M = v` | FB-1 | MS-7 |
| `record class` 显式继承非 record 基类 | C# 本身非法，不存在 | — |
| `record struct` 实现带 set 属性的 in-tree 接口 | 显式实现桩 + IR 连带 | RS-14 |
| `dynamic` 接收者 / `SetValue` 反射修改 | 扫描到即 FB-1，否则运行时已知限制 | MS-14, §9 |

## 6. 验证与错误处理

- **逐文件**：`FormatAndValidate`——改动区间格式化 + 重解析 + 语义诊断零错误（仅作快速预筛；不作为回退粒度，见 §3.2/§4.6）。
- **整树**：全部文件重写后重建 `CSharpCompilation` 比对基线错误集，不得新增错误；新增错误按 record 计划粒度回退并迭代（FB-5）；项目级测试额外对中间树执行 `dotnet build`（TupleToStructProjectTests 模式）。
- **确定性**：文件处理顺序稳定（按路径排序）、生成成员命名确定，无时间戳依赖。
- **诊断**：RR0xx 码 + MSBuild 格式 `文件(行,列): warning/error RR0xx: 描述`；统计含：转换的 record struct/class 数、成员改写数、用点改写数、方法迁移数、接口连带数、跳过数（按原因分组）。
- **退出码**：`--strict`（后续可加）时存在跳过即非零；默认成功（跳过仅警告）。

## 7. 测试策略（测试先行，Agent.md 规则）

1. **单元测试** `tests\CSharpToJava.ProjectTests\RecordReadOnlyRewriterUnitTests.cs`（TupleToStructRewriterUnitTests 模式：内联源码 → `CSharpCompilation` → `RewriteFile` → 断言输出文本/归一化 + `AssertOutputCompiles` + 无残留可变成员断言）：
   - 每个 RS/RC/MS/MM/IR 规则至少 1 正例；每个 FB 至少 1 反例（跳过 + 诊断码断言）
   - 重点：partial、嵌套、泛型、非 void 方法 out 迁移调用点、ref/out 形参体内改写（含 MS-10 的 out 先赋值后写成员）、接口连带（属性/带参方法/非 void 方法 out 形态/多实现者/泛型接口/外部接口跳过）、record class 契约转换、ctor 内 this 赋值、已 readonly 跳过
   - 新增覆盖（修订）：索引器 FB-1（RS-19/RC-9/MS-18）、嵌套成员链正/反例（MS-17）、扩展方法迁移（MS-19/MM-7）、`required` 保留（RS-20）、私有拷贝 ctor（RC-10）、MS-2 读后写违例不合并、MS-3 不纯 getter → FB-1、接收者副作用 → FB-1（MS-1）、MM-6 partial 方法/调用图环/getter 内修改成员、RC-8/FB-2 层次回退、计划级回退（FB-5：声明与用点跨文件时用点失败回退声明）
2. **项目级测试** `RecordReadOnlyProjectTests.cs` + 新 fixture `tests\SampleRecordSolution\`（Lib + App + sln，覆盖全部规则类别），`Content Include` 进 `CSharpToJava.ProjectTests.csproj`；断言中间树文件内容 + `dotnet build` 中间树成功。
3. **一致性测试**：基于 `ProjectPreprocessSource` 构建，`ProjectPreprocessorsConsistentTests` 自动保持（如需扩展断言则同步更新）。
4. **CLI 测试**：`--record-readonly` 开关接线、verb 双模式、`.cs2j-record-readonly-src` 保留目录、统计输出。

## 8. 工作量与实施里程碑（供 writing-plans 展开）

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 | 骨架 + Analyzer + **RS 声明规则全集（RS-1~RS-20）** + FormatAndValidate（含计划级回退框架）+ CLI 四处接线 + 基础单测 | Core ~800 行, CLI ~700 行 |
| M2 | MutationSiteRewriter（**MS-1~MS-19**，含索引器/嵌套链/扩展方法调用点）+ 单测 | ~700 行 |
| M3 | MethodMigrator（**MM-1~MM-7** 全部含扩展方法迁移 + 调用图复用 `CallGraphBuilder`）+ 单测 | ~600 行 |
| M4 | InterfaceRippler（IR 全部 + 桩 + 实现者补齐）+ 单测 | ~500 行 |
| M5 | **record class 契约转换（RC-1~RC-10）** + 层次一致性（RC-8/FB-2）+ 单测 | ~300 行 |
| M6 | overrides 配置、诊断打磨、SampleRecordSolution、项目级测试（计划级回退/整树重编译）、文档 | ~500 行 + fixture |

总量与 tuple-to-struct 特性（~5300 行含测试 fixture）相当。

## 9. 明确不做的事

- 不改 RecordTransformer / StructTransformer / TypeGroupResolver / Pipeline / 任何 C#→Java 代码（后续若需让 `readonly record struct` 在 Java 侧产出 value record，另行立项）。
- 不处理 record 之外的类型（普通 struct 由 MakeReadOnly 负责）。
- 不保证外部程序集对被改签名方法/接口的二进制兼容（源码级转换边界）。
- 不追踪反射/动态的运行时修改（静态不可见，文档化）。
- 不规避接口新增抽象成员对**外部实现者**的源码/运行时破坏（IR-1 注；可用 DIM 默认体缓解，默认关闭）。
- 不保证依赖公共 set 访问器的序列化/反序列化框架（旧式 JSON/XML 反序列化器等）行为不变（init 与现代序列化器兼容；旧框架可能写不回——已知限制）。
- 不改变 record class 的别名观察语义变化（§4.2 已文档化，特性固有）。
