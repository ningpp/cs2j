# C03 控制流去糖（pattern switch / 非整型 switch / catch-when / catch T）预处理器设计

**日期**: 2026-09-28（2026-09-29 评审修订：catch 单子句保形归约与裸 `else throw;` 形态、governing 简单标识符不提升、SwitchCaseWhenClause/CatchFilterRethrow 断言口径按现渲染器核实修正、常量/关系模式编译性细则、residual 与 PD010/PD011 去重）
**范围**: 把注册表 stub `pattern-desugar` 落地为**语句侧** C#→C# 预处理器（Core `PatternDesugar/` + CLI 适配器），并把该 stub 翻转为 `Implemented`；不修改任何 C#→Java 转换代码
**状态**: **已实现并合入 dev-3.x 主干**（与 C37 同批，2026-09-30 核实，提交链见「再基线」段；pass 默认关）
**再基线（2026-09-30）**: 本设计（含姊妹 C37）**已在主干实现完成**，无需重新实施。实现提交链（均为 dev-3.x 祖先、75958d7a 已含）：**aa9fc2df**（PatternDesugar 核心组件：reducer/plans/rewriter/preprocessor）→**700bfa7a**（hoist-and-assign 绑定 + 41 项核心测试）→**559dcce4**（尾部 break 消解 + D16 fixture 全绿）→**1b67b823**（D17 翻转 pattern-desugar stub 为 Implemented，catalogItems `C03, C37`）→**d06064ef**（CLI 可达性）→**1e1fa112**（SamplePatternSolution fixture + 项目级守门）→**15d534a1**（PD010 白名单双态适配器级锁定）。现状：PassCatalog.cs L111-122，`pattern-desugar` Implemented、**默认关**、Stage=ControlFlow、RunAfter=`{ null-coalesce, tuple, foreach-deconstruction }`、catalogItems `"C03, C37"`；Core 实现 `src/CSharpToJava.Core/PatternDesugar/`（9 文件，含表达式侧 is-pattern/switch-expression 落点）；测试 PatternDesugarCoreTests/PatternMatchingTests + SamplePatternSolution 端到端，随 2026-09-30 Release 基线全绿（Tests 2102/ProjectTests 592/ConversionTests 671）。2026-09-30 复核：Roslyn 已升级 5.0.0；被删红测为维护者有意删除。
**注册表**: `pattern-desugar`（[PassCatalog.cs L111-L122](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L111-L122)，catalogItems `C03, C37`，RunAfter=`{ null-coalesce, tuple, foreach-deconstruction }`）
**Catalog 条目**: `prompts/preprocessor-catalog.md` C03（P0，L282-295）、C37（P1，L706-718）、§3.3 控制流去糖族（L898-915）、§4 风险 15（L934）/风险 8（L927）
**基线提交**: 实现已合入 dev-3.x（止于 15d534a1，75958d7a 已含）；worktree `.worktrees/preproc-c03-pattern-desugar`（分支 feature/preproc-c03-pattern-desugar）2026-09-30 快进到 75958d7a 留档，分支无独有提交、使命已完成，不再用于实施

---

## 1. 背景与目标

### 1.1 每命中一处就是一次转换失败（语句侧现状证据）

主转换器对 C03 目标构造全部 fail-fast，异常文案均为 "CS2J normalization required: … must be lowered by a preprocessor"：

| 构造 | 失败点 | 行为 |
|---|---|---|
| 带模式标签（含 `when`）的 switch 语句 | [StatementTransformer.SwitchAndResource.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L22-L26) L22-26 | `NotSupportedException` |
| bool/long/ulong/float/double/decimal governing 的 switch | 同文件 L29-42 | `NotSupportedException` |
| 未知 switch label 种类 | 同文件 L58-61 | `NotSupportedException` |
| `catch (T) when (…)` catch filter | 同文件 L141-145 | `NotSupportedException` |
| 对类型参数的 catch（`catch (T)`） | 同文件 L152-156 | `NotSupportedException` |

注：常量标签带 `when`（`case 1 when F:`）在 Roslyn 语法面也是 `CasePatternSwitchLabelSyntax`，因此同样在 L22 失败——没有「常量 switch 但带守卫」的旁路。

表达式侧（C37 范围）则是另一类问题——**静默错误输出**：`is` 模式仅 3 条分支、其余兜底为恒真 `true`（[TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L107-L117) L115 `/* TODO: unsupported pattern */ true`）；switch *表达式*臂未知模式同样兜底（[ControlFlowTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L474-L507) L505 `/* TODO: pattern … */ true`，位置模式无分支，递归模式只桥接属性子模式 L521-561）。

另：字符串 foreach 在 [StatementTransformer.Loops.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L118-L122) L118-122 fail-fast。catalog C03 把它列为「可并入本通道或 C16」的第 ⑤ 项；**本设计明确不纳入 C03 锚点范围**，归 `foreach-source`（C16/C40），见 §11。

### 1.2 PatternMatchingTests 红灯清单（2026-09-28 `-c Release` 实测复核，2026-09-29 复跑确认 6 失败/10 通过与下述分布一致）

`tests/CSharpToJava.ConversionTests/Categories/PatternMatchingTests.cs`（11 例，直连单文件 `ConversionPipeline`）：**8 失败 / 3 通过**，与 catalog §4.15 记载一致。

失败 8 例：

| 用例 | 实际产物 / 失败原因 | 归属 |
|---|---|---|
| `IsConstantPattern_ConvertsToEquality` | `if (/* TODO: unsupported pattern */ true)` | C37 |
| `RelationalPattern_ConvertsToComparison` | 同上恒真兜底 | C37 |
| `LogicalPattern_ConvertsToAnd` | 同上 | C37 |
| `OrPattern_ConvertsToOr` | 同上 | C37 |
| `NegatedPattern_ConvertsToNotEqualsNull` | 同上 | C37 |
| `NotTypePattern_ConvertsToNegatedInstanceof` | 同上 | C37 |
| `PropertyPattern_ConvertsWithTodoMarker` | 测试期望的 `/* TODO: recursive pattern */ p` 文本在 src 全仓已不存在；实际产 `unsupported pattern` 恒真——**实现与断言双向脱节** | C37（C03 负责同步修订该断言，见 §2 末行） |
| `IsDeclarationUsed_ConvertsToInstanceofWithBody` | 模式本身成功（`o instanceof String s`），方法体产 `s.getLength()` 而断言要 `s.length()`——非模式问题，是 String 成员映射口径 | 转换器既有问题，记录为独立项，不阻塞 C03 |

通过 3 例：`IsTypePattern_ConvertsToInstanceof`、`IsDeclarationPattern_ConvertsToInstanceof`、`PatternMatchingNoCSharpResidue`（后者在恒真兜底下**空洞通过**——`is` 被整体替换为 `true`，自然无 C# 残留）。

`tests/CSharpToJava.Tests/PatternMatchingTests.cs`（10 例）：**3 失败 / 7 通过**。失败全部是语句侧，错误信息即 §1.1 的 fail-fast 文案（实测由 `SingleFileJavaEmitPass` 报出）：

- `SwitchStmt_PatternCase_DeclarationType_ConvertsToIfElse`
- `SwitchStmt_PatternCase_WhenClause`
- `SwitchStmt_PatternCase_ConstantNull`

通过的 7 例覆盖 `is` 表达式两条窄分支与 switch *表达式*既有臂编译（含 Not/Type/Relational/Declaration/RecursiveProperty）。

另有两组相关红灯（2026-09-29 实测：PatternMatching + CatchFilter + SwitchCaseWhen 三组合计 16 例，6 失败/10 通过，与 3+1+2 分布一致）：

- [CatchFilterRethrowTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/CatchFilterRethrowTests.cs)：1 例失败（catch filter fail-fast）；该测试断言 rethrow 目标形态 `else { throw ex; }`。注意：现渲染器主路径对 else 统一包多行块（[StatementTransformer.Conditionals.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Conditionals.cs#L382-L388) L382-388；单行 `else { … }` 仅 TryGetValue 特例路径可达），逐字符断言不可再现——本 pass 落地时断言放宽为语义形态（`throw ex;` 存在 + `else` 存在 + 无 AssertionError，见 D16②）。
- [SwitchCaseWhenClausePrecedenceTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/SwitchCaseWhenClausePrecedenceTests.cs)：`CaseWhen_WithOrClause_IsParenthesized`、`CaseWhen_WithAndClause_KeepsSingleParens` 2 例失败（`case 1 when A || B` 走 L22 fail-fast）。该测试还锁死了一个**优先级不变式**：守卫必须整体加括号——`type == 1 && (A || B)`（旧断言文本 `Objects.equals(type, 1) && (A || B)` 来自一个已不存在的旧实现，随 D16③ 更新比较形态），C03 的 C# 侧归约必须同构地把 when 条件整体括起，防止 Java 侧 `&&`/`||` 优先级错位回归（RegexNode.canBeMadeAtomic 事故）。

### 1.3 目标形态

机械归约，两类目标产物，均为可编译 C#（.NET 半区以原项目测试守门）：

1. **pattern switch / 非整型 governing switch → governing 为非简单标识符时先单例化临时变量，再归约为 if-else 链**；模式归约为布尔表达式，模式变量为分支块内局部变量；`default` → `else`，无 default 则无 else；section 内 `break` 先以「goto 链尾标签」表达，交后续 `eliminate-goto` 消除。
2. **catch-when / catch T → 单子句保形归约（body 包 if/else throw）或多子句合并为 `catch (Exception __cs2jEx_n)` + 按子句顺序的类型测试与守卫 if-else 链，末端裸 `else throw;`**；finally 原样保留。

归约出的布尔表达式只含普通 C# 语法（`==`/`!=`/`is T v` 声明模式/`&&`/`||`/属性或字段访问），其中声明模式 `ex is T v` 是主转换器已接受的生产形态（由 [JavaGenericPatternRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Rewriters/JavaGenericPatternRewriter.cs#L110-L129) 在 Java 路径做泛型提升，类型参数形态在 [TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L90-L92) L90-92 已映射为 class token `isInstance`）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 备注 |
|---|---|---|---|
| D1 | 总体形态 | 语义驱动逐文件 pass（整树 `CSharpCompilation`，Analyze 一次、逐文件 Rewrite），复刻 EnumPreprocessor 范式：dirty check → rewriter → Formatter → 语义 residual 复检 | [EnumPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessor.cs)；干净文件字节级保留 |
| D2 | pattern switch | governing 为**简单标识符（局部变量/参数）时不提升**，直接内联进条件（最小扰动：`switch (type)` 的条件仍引用 `type`）；其余表达式（方法调用/属性/字段访问/复杂表达式，凡可能带副作用或双重求值）→ `var __cs2jGov_n = <governing>;` 单例化一次。每个 section 的标签析取 + when 合取归约为一个 `if` 条件，section 体进分支块；多 section 串成 else-if；`default:` → `else` | 求值次数语义：C# switch 对 governing 恰好求值一次，局部/参数重复读无副作用可等价；方法调用/属性/字段访问必须只求值一次 |
| D3 | 同 section 多标签 | `case A: case B: Body` → 单标签不加括号、多标签 `(condA || condB)`，守卫整体合取且**整体加括号** `… && (<when>)` | 锁死 SwitchCaseWhenClausePrecedenceTests 优先级不变式 |
| D4 | 非整型 governing | 归约集 = Java switch statement **不支持**的全部 governing 类型：① `bool/long/ulong/float/double/decimal`（以 SemanticModel `SpecialType` 判定，与 L31-41 清单一致）；② **`object`/`dynamic`/类型参数**（2026-09-29 实测补充：Java switch 只接受基本类型、string 与 enum，`switch (object)` 直转会产不可编译 Java；`case 1:` 在 object governing 上须走 D5 的 `is` 形态）。常量标签 → `__gov == c`（直接保留 C# `==` 语义：浮点精确匹配、decimal 等值、NaN 自然不匹配） | decimal 等值在 Java 半区由 numeric-boundary（C15/C34）后续负责；string/enum **不动**（Java 支持 string/enum switch），C05 enum-as-class 落地后再扩集（§11） |
| D5 | 模式→布尔表达式 | 常量→`gov == c`（null 常量→`gov == null`；governing 静态类型为 `object`/`dynamic`/类型参数时改用 `gov is CT v && v == c`，CT 为常量静态类型——`object == 1` 会按引用比较装箱值、静默恒假且对非可空值类型不编译，必须走 is 形态）；type/declaration→`gov is T` / `gov is T v`；discard→`true`、var 绑定→`gov is var v`（恒真且完成绑定）；relational→`gov > c` 等（可空 governing 前置 `gov != null &&` 并经 `.Value` 比较——提升比较产 `bool?` 不能直接作 if 条件）；and/or→`&&`/`||`（复合结果整体括起）；not→`!(…)`；parenthesized 透传；property/recursive→类型省略时用 governing 静态类型直接成员访问、带类型时 `gov is T v && <v.成员递归归约>`，引用类型中间成员逐层补 `!= null` 守卫，绑定名用用户 designation、缺省合成 `__cs2jPat_n`；公共字段与属性都支持（`P.X` 用例是字段） | 全部由 PatternReducer 输出，**C03 与 C37 共用同一归约器**（catalog L709/L903）；null 常量出现在非可空值类型 governing 上属不可达模式，PD010 显式报错 |
| D6 | 位置/列表/Slice 模式 | C03 不实现：语句标签中出现 `PositionalPatternClause`/`ListPatternSyntax`/SlicePattern → **PD010 显式报错**（文件:行列 + 模式种类），**该 switch 节点整体保持原样不归约**，绝不产出占位/恒真；对应 reducer 由 C37 注册后自然消错 | 这是默认开启不产生静默回退的关键闸门 |
| D7 | when 子句 | 守卫表达式**原样保留**为 C# 表达式并整体括入 `(… && (<when>))`，本 pass 不重写守卫内部；但 Analyzer 必须扫描守卫内的 `IsPatternExpressionSyntax`——**凡出现即 PD010**（C37 才归约表达式侧模式；C03 原样透传会把未知模式送进 TypeOperationTransformer L115 恒真兜底，静默错误），命中后该 switch 整体不归约；C37 合入后放开 | 防止 C03 单独默认开启后，守卫把恒真兜底送进 Java 产物 |
| D8 | catch-when | **两种策略，按子句形态选择**：① **单子句带 filter（具体类型、非类型参数）→ 保形归约**：catch 子句声明与变量名原样保留（`throw ex` 等体内引用零扰动），仅把 body 包进 `if (<filter>) { <body> } else throw;`——**else 用裸语句 `else throw;` 不加块**（Roslyn Formatter 不给裸 else 加括号；Java 侧对非块 else 走 `{\n … \n}` 包裹，rethrow 的是原变量 `ex`，与 CatchFilterRethrowTests 的语义断言一致，见 D16②）；② **多子句（任一带 filter 或类型参数）→ 合并链**：全部 catch 子句合并为一个 `catch (Exception __cs2jEx_n)`，按原子句序生成 `if (__cs2jEx_n is T1 <原变量名> && (<f1>)) { body1 } else if … else throw;`——每个分支的 `is T v` 绑定用**原 catch 变量名**，filter/body 内对原变量的引用由此解析为派生类型变量，无需改名；无 filter 的具体类型子句折叠为 `__cs2jEx_n is Tn <原变量名>` 链节点；无类型 `catch { }` 视为 `Exception`、条件省略（末位 `else { body }`）；`finally` 不动 | 只做「单 catch + if/else throw」不完整——同类型后续子句（`catch(Foo)when F` 后接 `catch(Foo)`）要求 filter 假时继续匹配后子句；多子句必须合并链精确保留 C# 按序匹配语义。合并后裸 `throw;` 的 Java 映射为 `throw __cs2jEx_n;`（同一异常对象，无测试锁该文本） |
| D9 | catch T（类型参数） | 单个子句（带/不带 filter）也走合并链形态（D8 策略②）：`catch (System.Exception __cs2jEx_n) { T <原变量名>; if (__cs2jEx_n is T && ((<原变量名> = (T)(object)__cs2jEx_n) == <原变量名>) && (<filter>?)) { body } else throw; }`——类型参数不能留在 catch 声明上（Java 无法表达），必须经绑定恢复派生形态。**实测修订**：`catch (T)` 无约束是 **CS0155**（"The type caught or thrown must be derived from System.Exception"），输入本身不合法——合法输入必带 `where T : System.Exception`（或其派生），故 D9 只有「有约束」一条路径；cast 一律经 `(T)(object)…`（直接 `(T)ex` 在无约束/非派生约束下是 CS0030，`(T)(object)` 对有约束 T 同样合法且无需分支）。Java 半区由 generic 既有路径把 `is T` 映射为 class token `isInstance`（L90-92 已验证形态） | 禁止新写特判；依赖 generic pass 在链上位于本 pass 之后的既有顺序 |
| D10 | 裸 rethrow | 归约产物中末端与未匹配分支一律发**裸语句** `else throw;`（保留 C# 堆栈语义；Formatter 不给裸 else 加块）；转换器 L88-94 把 catch 内裸 throw 映射为 `throw <catch声明名>;`——策略①下即原变量名 `ex`，策略②下为 `throw __cs2jEx_n;` | catch 外裸 throw 仍是既有 TODO，与本特性无关 |
| D11 | 模式变量提升形态（2026-09-29 实测修订） | **hoist-and-assign**：绑定变量在归约块顶部以普通声明提升一次（`T v;`），在条件内赋值完成绑定——`gov is T && ((v = (T)gov) != null)`（引用类型）/ `gov is T && ((v = (T)gov) == v)`（值类型，`!= null` 对值类型不编译）/ `gov is T && ((v = (T)(object)gov) == v)`（类型参数，`(T)gov` 是 CS0030，须经 object 中转）；带类型之外的 designation（`{ X: 1 } p`、`var x`、无类型递归模式）改为块顶 `var p = gov;` 急切赋值（无需类型，也不产生 cast）。governing 仍按 D2：非简单标识符提升 `__cs2jGov_<n>` | **否决修订前的「各自分支块内声明 `is T v`」**：实测 C# 模式变量作用域是**整个封闭块**——`if (o is string s) {} else if (o is string s) {}` 是 CS0136（不能在封闭范围内重复声明），且 `{ if (o is int i) {} }` 在参数名为 `i` 时也是 CS0136（不能遮蔽外层局部），即题面的 switch 本身（同 section 两个 `case int i when …`）之所以合法，是因为各 case 标签作用域彼此独立，而 if/else-if 链不成立。hoist-and-assign 也顺带解决泛型绑定恢复（cast 保留完整泛型实参）与确定赋值（提升声明于块顶，所有分支可见） |
| D12 | section 终止语义（2026-09-29 实测修订：尾部 break 消解） | C# section 不允许穿透。**section 末尾的 `break;` 直接删除**——if/else-if 链是互斥的，分支体执行完即已离开该 switch，与 break 语义逐字等价，因此绝大多数真实 switch（每个 section 都以 break 收尾）**不产生任何 goto/label**，产物可直接进主转换器而无需依赖 E14。仅当 break 位于 section 中部（`if (x) { break; } 后续语句;`）时，才发 `goto __cs2jSwitchEnd_n;` 并在链尾生成同名标签（labeled statement，空语句承载；避免 CS0164 未引用标签告警）；**break 改写只作用于以本 switch 为最近 enclosing switch/循环的 break**——嵌套 switch/循环体内的 `break` 原样保留（否则语义被劫持）；`return/throw/continue` 原样保留（continue 仍指向外层循环）；跳出到外层标签的普通 `goto label;` 原样保留（非 goto case，交 E14）；无终止语句的块按 C# 规则本就不可编译（基线错误，不处理） | 残余 goto 由链上更靠后的 `eliminate-goto` 消除（pattern-desugar 的锚点在 goto 之前，顺序天然正确）；尾部消解显著降低对 E14 的运行期依赖：默认链关闭 E14 时绝大多数输入仍可直转 |
| D13 | `goto case`/`goto default` | 被归约 switch 的 section 体内若含 goto case/default → PD011 显式报错（罕见组合；goto case 只对常量标签合法），不做静默近似；**命中 PD011 的 switch 节点整体保持原样不归约**（该文件其余可归约目标照常处理），文件标记后 residual 复检跳过（§4.1） | 与 E14 的精确分工：E14 处理普通 switch 上的残余 goto case，本 pass 只保证不制造无法解析的新形态 |
| D14 | 与 JavaGenericPatternRewriter 切割 | 本 pass **不接管**泛型声明模式提升，输出中保留 `gov is CSharpList<T> v`；Java 路径的 `ProjectJavaPatternRewritePass`（[ProjectPasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L559-L597) L559-597）继续负责「类型模式 + 段首强转声明」。切割线：**C03 管语句/子句结构归约，rewriter 管泛型绑定变量的类型恢复**。注意 rewriter 的条件提升只匹配**整体条件恰为 IsPatternExpressionSyntax** 的形态（[JavaGenericPatternRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Rewriters/JavaGenericPatternRewriter.cs#L110-L129) TryHoistCondition）：无 when 的泛型声明模式条件经提升后正常；带 when 的合取条件（`gov is List<T> v && (F)`）不匹配提升、按既有 `&&` 条件路径走裸类型 instanceof（与今天 `if (x is List<T> v && y)` 的既有行为同类，泛型恢复缺口预先存在且 fail-loud，非本 pass 引入）。case 标签经 D2 后整体消失，rewriter 的 VisitSwitchStatement 在归约产物上无活可干 | catalog §4.15 要求的「争抢同一语法面」由此顺序解：先归约结构，再做泛型提升 |
| D15 | string / enum governing | 保持 switch 原样（含 string 常量 case 与 enum case），本 pass dirty check 不命中 | Java 7+ string switch、enum switch 均为 1:1；C05 enum-as-class 落地时由 enum-declaration 侧或本 pass 扩集（§11） |
| D16 | 测试期望同步修订 | ① Tests 工程 3 个语句侧用例改为「先跑 PatternDesugarPreprocessor 再进 ConversionPipeline」的组合夹具，Java 侧断言（if/instanceof/无 `switch (o)`）保持；② CatchFilterRethrowTests 同夹具，**断言放宽为语义形态**：现渲染器主路径对 else 统一包多行块（[StatementTransformer.Conditionals.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Conditionals.cs#L382-L388) L382-388，单行 `else { … }` 仅 TryGetValue 特例可达），`else { throw ex; }` 逐字符断言不可再现——改为断言 `throw ex;` 存在、`else` 存在、无 `AssertionError`；策略①/D10 保证 rethrow 的是原 catch 变量 `ex`；③ SwitchCaseWhenClause 2 例同夹具，**断言文本更新**：旧期望 `Objects.equals(type, 1) && (…)` 来自一个已不存在的旧版 if 链实现（对 int==int 无任何现路径会产 Objects.equals，BinaryExpressionTransformer 仅 string==string 走 Objects.equals）；新期望为 `type == 1 && (Objects.equals(str, "A") || Objects.equals(str, "B"))` 与 `type == 1 && (m > 0 && ch != 5)`——**被锁死的不变式保持不变**：when 条件在 `&&` 之后作为整体括起（优先级回归防线仍在，只是常量比较形态从 Objects.equals 变为 `==`）；④ ConversionTests 的 `PropertyPattern_ConvertsWithTodoMarker` 删除过时 TODO 文本断言、改为 `Skip="C37 positional/property expression-side"` 并登记到 C37 设计；⑤ `IsDeclarationUsed` 的 `getLength/length` 差异登记独立 issue，不改本 pass 范围 | 「先定死目标形态、再同步修订测试期望」（§4.15）；恒真占位文本断言不得保留 |
| D17 | 翻转方式 | C03 合入时：descriptor 从 `PassStubCatalog.Stubs` 移入 `PassCatalog.Implemented`，新增 `PassIds.PatternDesugar`，加适配器与 `PassChainFactory` 分支；`Status=Implemented`，`defaultEnabled` 见 §8（建议过门槛即默认开） | 注册表测试 23→22、链序测试同步改（§7） |

---

## 3. 注册表与链位契约

沿用 [PassDescriptor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs) 既有契约，翻转后描述符字段：

| 字段 | 值 | 说明 |
|---|---|---|
| Id | `pattern-desugar` | `PassIds.PatternDesugar` 常量；即 `--enable-pass pattern-desugar` 名 |
| DisplayName | `Pattern desugaring` | 沿用 stub 文案 |
| IntermediateDirectoryName | `.cs2j-pattern-desugar-src` | 沿用 stub 注册，不改（指纹一次性失效可接受，foundation plan L137） |
| Stage | `PassStage.ControlFlow` | 信息性分类 |
| Status | `Stub` → **`Implemented`** | 本锚点任务的翻转职责 |
| DefaultEnabled | 翻转时 `false`；满足 §8 后同特性改 `true` | 两步走，与 record-readonly/array-method 的 opt-in 先例一致；不新增 legacy 开关，只走通用 `--enable-pass/--disable-pass`（[PassPlanResolver.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassPlanResolver.cs) 对无 legacy toggle 的 implemented id 直接生效） |
| RunAfterIds | 翻转时建议由 `{ null-coalesce }` 扩为 **`{ null-coalesce, tuple, foreach-deconstruction }`** | C37 的位置模式依赖 E06 元组字段/E07 解构命名契约（catalog L714）；同一 pass id 一次把锚点放足，避免 C37 再动链。仍严格早于 `eliminate-goto`（goto 现锚 make-readonly，拓扑序天然在后；D12 依赖此后序） |
| SupportLibrary | `None` | 不注入支撑库，无需过滤清单 |
| NeedsSemanticModel | `true`（默认） | governing 类型、catch 类型符号、模式成员符号都依赖 SemanticModel |
| VerifyBuildByDefault / VerifyTestsByDefault | 建议均 `true`（枚举/goto 先例） | 中间树 Release build/test 是默认开启的硬证据 |
| CatalogItems | `C03, C37` | 不变：一个 pass id 覆盖两条 catalog，C03 先翻转、C37 在同 pass 内扩展 |

链序影响：[PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L53) 的 `LockedChain`（implemented 子序列）与 `EnabledByDefault` 是显式数组——翻转 PR 必须把 `pattern-desugar` 插入到 `foreach-deconstruction` 之后、`strip-default` 之前（或实际拓扑等价位），默认开启变更时再动第二个数组。[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24) L24 的 stub 总数断言 `23 → 22` 同步修改。`BuildLegacyToggles_covers_every_registered_pass`（L169-182）的「无 legacy toggle 排除集」需补入 `pattern-desugar`（与 generic-deepen 同列）。

独立动词：通用 `preprocess --pass pattern-desugar` 经 `CliPreprocessChain` 天然可达，不新增独立 verb（与 java-keywords 保留自有 verb 的历史包袱不同，新 pass 一律走通用面）。

锚点职责边界（本任务 vs C37）：

- **C03（本设计）**：创建 `PatternDesugar/` 全部脚手架与语句侧组件（switch 语句、非整型 governing、catch filter、catch T）、PatternReducer 基干与语句侧模式种类、CLI 适配器、翻转 stub、默认开评估材料。
- **C37（独立设计/独立 worktree）**：在**同一 pass、同一目录、同一 PatternReducer** 上增加表达式侧组件（`is` 表达式、switch 表达式臂、位置/列表模式），不新增 pass id，不新增中间目录。

---

## 4. Core 组件设计

新目录 `src/CSharpToJava.Core/PatternDesugar/`（命名空间 `CSharpToJava.Core.PatternDesugar`），九个文件，对齐 foundation plan 的标准交付面与 EnumPreprocessor 现行外形（八文件 + 组件文件承载 §4.4 的两个语句侧组件实现）：

```
src/CSharpToJava.Core/PatternDesugar/
  PatternDesugarOptions.cs      # 选项记录：Strict(bool, 默认 true)、Verbose、ResidualWhitelist（相对路径子串白名单）
  PatternDesugarStatistics.cs   # 计数 POCO（不引新接口；跟现网各 pass 同款）
  PatternDesugarDiagnostic.cs   # PatternDesugarDiagnostic(Code,Severity,Message,FilePath) + Severity/Code 常量（PD001/PD010/PD011/PD020/PD030）
  PatternDesugarResult.cs       # PatternDesugarResult(OutputCode, Changed, Diagnostics, Statistics)
  PatternDesugarPreprocessor.cs # 公共入口：Preprocess(source, SemanticModel, options) + 组件注册表
  PatternDesugarAnalyzer.cs     # 整树语义分类：dirty check + residual 计数（计划构建在组件 Analyze 内，见 §4.4）
  PatternDesugarRewriter.cs     # CSharpSyntaxRewriter：top-down 计划改写 + break→goto 栈式改写 + 命名作用域
  PatternReducer.cs             # 模式→布尔表达式归约器（C03/C37 共用核心）+ 扩展点接口
  PatternDesugarComponents.cs   # IPatternDesugarComponent + PatternDesugarPlanBuilder + SwitchStatementComponent/CatchClauseComponent（含计划模型）
```

### 4.1 入口 `PatternDesugarPreprocessor`

签名与 [EnumPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessor.cs#L14-L70) 同形：

```csharp
public sealed class PatternDesugarPreprocessor
{
    public PatternDesugarResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        PatternDesugarOptions? options = null);
}
```

流程：语法 dirty check（文件内无 `CasePatternSwitchLabelSyntax`、无 catch filter/类型参数 catch、无 switch/catch 目标节点即字节级返回 clean；dirty check 取超集，Analyzer 负责精确判定）→ 各组件 Analyze 产出计划 + PD010/PD011 → 无任何计划则 dirty-no-rewrite（诊断仍随结果返回）→ Rewriter 执行 → `AdhocWorkspace` Formatter（与 enum 一致）→ 重解析 + 重建 compilation 做语义 residual 复检（仿 `CountResidualEnumOps`）：输出树中目标节点计数必须为 0，否则按 Strict 记 PD020 Error/Warning。**已报 PD010/PD011 的文件跳过 residual 复检**（显式失败已定性，重复计数是噪声）。无 SemanticModel（单文件裸调）时仅做语法级归约与语法 residual（模式标签 + catch filter），D4 非整型 governing 判定与 D6 语义判定不可用——无法语义确认的归约不做（保守：pattern 标签驱动归约仍可用，常量标签在 object 型 governing 上的 is 形态选择需模型，缺模型时直发 `==` 并在文档注明该局限）。

### 4.2 Analyzer（语义）

计划构建由组件的 `Analyze` 完成（组件自治，C37 组件同构扩展）：

- `SwitchLoweringPlan`（SwitchStatementComponent）：governing 表达式与类型符号（`SemanticModel.GetTypeInfo`）、是否需要提升临时变量（D2：仅非简单标识符）、是否需要归约（模式标签 ∪ D4 governing 类型）、按 section 序的标签/守卫/语句清单、是否含 `goto case`（D13，命中则整个 switch 不归约并报 PD011）、是否有需改写的 break（决定链尾标签）、`__cs2jGov_n`/`__cs2jSwitchEnd_n` 命名。
- `CatchLoweringPlan`（CatchClauseComponent）：try 节点、全部 catch 子句（类型符号、是否 `ITypeParameterSymbol`、filter 表达式、body、原变量名）、策略选择（D8：单子句带 filter 且非类型参数 → 保形归约①；多子句或类型参数 → 合并链②；纯具体类型无 filter 子句原样保留）、合并时的合成异常变量名 `__cs2jEx_n` 与各分支原变量名。
- 守卫扫描（D7）：递归 `when` 条件中的 `IsPatternExpressionSyntax`，凡出现即 PD010（见 D7）。

### 4.3 Rewriter（语句侧）

`CSharpSyntaxRewriter`，top-down 按计划做局部替换（计划以原始树节点为键，子树内递归 `Visit` 继续命中嵌套计划）：

- switch 节点 → `{ <gov 声明>;? if … else if … else …; <endlabel>: ; }`（governing 非简单标识符才注入声明；无 default 时无 else；标签用空语句承载，仅当确有 break 改写时生成）。break 改写由主 rewriter 的 `VisitBreakStatement` + 「break 归属栈」实现：栈顶是被归约 switch，只有当该 break 的最近 enclosing switch/循环（沿原始树祖先链）恰为栈顶 switch 时才改写为 `goto endlabel;`，嵌套 switch/循环/未归约 switch 的 break 不动（D12）。
- try 节点 → 按 CatchLoweringPlan 执行：策略①保形归约（catch 子句声明不动、filter 移入 body 的 `if (<filter>) { … } else throw;`——**末端 `else throw;` 用裸语句，不加块**，Formatter 不给裸 else 加括号）；策略②合并链（catch 列表替换为单 catch 链，末端同为裸 `else throw;`）。`finally` 原样保留。
- 所有新名字走确定性序号（`__cs2jGov_0`、`__cs2jEx_0`、`__cs2jSwitchEnd_0`、`__cs2jPat_0`），同输入同输出，不依赖访问顺序随机值。

### 4.4 PatternReducer（共用核心 + C37 扩展点）

归约结果形状（Subject 以参数传入而非入 context——递归归约逐层换 subject；修正评审发现的 context 携带 subject 不可行问题）：

```csharp
internal sealed record PatternReduction(
    ExpressionSyntax Condition,              // 归约出的布尔表达式（已在正确括号层级）
    IReadOnlyList<StatementSyntax> Prologue,  // 需要的前置声明（C03 基本为空；C37 表达式侧提升用）
    bool Supported);

internal sealed record PatternReductionContext(
    SemanticModel? Model,
    PatternDesugarOptions Options,
    List<PatternDesugarDiagnostic> Diagnostics,
    string? FilePath,
    Func<string, string> NewName);           // 前缀 → 确定性名（__cs2jPat_n 等）
```

扩展点一：**reducer 注册表**（按模式种类），C37 只加注册项、不改调用方：

```csharp
internal interface IPatternReducer
{
    bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx);
    PatternReduction Reduce(PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx);
}
```

C03 注册：`ConstantPattern`（含 null）、`TypePattern`、`DeclarationPattern`、`DiscardPattern`、`VarPattern`、`RelationalPattern`、`BinaryPattern(and/or)`、`UnaryPattern(not)`、`ParenthesizedPattern`、`RecursivePattern`（仅 PropertyPatternClause 子模式；类型与 Designation 按 D5 语义在 C# 侧重做）。C37 追加：`PositionalPattern`、`ListPattern`、`SlicePattern`，并把表达式上下文特有的绑定提升装入 `Prologue`。

扩展点二：**语句/表达式组件槽**，为 C37 的两个 rewriter 预留：

```csharp
internal interface IPatternDesugarComponent
{
    void Analyze(CompilationUnitSyntax root, SemanticModel? model, PatternDesugarPlanBuilder builder);
    SyntaxNode? Rewrite(SyntaxNode node, PatternDesugarRewriteContext ctx); // null = 不处理
}
```

`PatternDesugarPreprocessor` 持有有序组件列表；C03 注册 `SwitchStatementComponent`、`CatchClauseComponent`；C37 注册 `IsPatternExpressionComponent`、`SwitchExpressionComponent`。C03 合入时该接口与注册机制必须到位但**只含 C03 两实现**，C37 不改入口签名。

扩展点三：**共享命名与绑定作用域服务**（NameScope：前缀 → 确定性序号），C03 的分支块策略与 C37 的段首提升策略共用同一序号源，避免两个 worktree 合入时名字碰撞。

### 4.5 语义模型依赖清单

| 用途 | API |
|---|---|
| governing 类型 / 非整型判定 / object 型常量归约分支 | `GetTypeInfo(switch.Expression).Type`，`SpecialType` 白名单同 L33-40 |
| catch 类型参数判定 | `GetTypeInfo(catch.Declaration.Type).Type is ITypeParameterSymbol`（同 L152） |
| 递归模式成员解析（字段 vs 属性） | `ITypeSymbol.GetMembers` 沿基类链查 `IPropertySymbol`/`IFieldSymbol`（生成成员访问表达式，不调 getter 名——C# 侧保持 `p.X`，Java 命名归主转换器） |
| 常量标签值 | 语法即常量表达式；不做值折叠 |
| 模式变量符号 | `SemanticModel.GetDeclaredSymbol(label/designation)`，仅供冲突诊断，改名不在本 pass |

---

## 5. CLI 适配器与集成

新增 [Adapters/PatternDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/PatternDesugarPassAdapter.cs)（预计 60–90 行），结构对照 [EnumPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs)：

- 持有 `PatternDesugarPreprocessor`、累计 `PatternDesugarStatistics`/`Diagnostic`；`RewriteFileAsync` 调 `Preprocess(context.SourceCode, context.SemanticModel!, _options)`，Changed=false 返回 null（字节级复制）；**ResidualWhitelist 按相对路径子串匹配过滤该文件的 PD010**（白名单命中则降级不计错误，供 §7.3 双态夹具）。
- `AfterFilesAsync` 做跨文件 residual 汇总（PD020，Strict 决定 Error/Warning），与 enum 适配器 L79-92 同构。
- `OnRestoreFailed/OnBuildFailed/OnTestsFailed`（PD030）、`HasErrors`、`BuildChainSummaryLine()`（至少打印：transformed/clean、pattern switch 数、非整型 switch 数、catch filter 数、catch T 数、governing 临时变量数、residual/skipped）、`BuildChainDiagnosticLines` 全套实现。
- `DeleteBuildOutputsAfterRestore = true`、`VerifyBuild/Tests` 跟随构造参数（convert-project 取 `PassChainConfiguration.VerifyBuild/Tests`）。

接线点（翻转 PR 的完整改动面）：

1. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：新增 `PassIds.PatternDesugar`（`PassIds` 类内常量），描述符从 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L72-L81) 删除并在 `Implemented` 表中按 §3 锚点插入；**`PassStubCatalog.Ids.PatternDesugar` 常量在翻转时一并删除**（plan 锁死此选择：全仓仅 PassStubCatalog 自身引用该常量，无外部依赖；C37 worktree 基于合入后代码拉分支，直接使用 `PassIds.PatternDesugar`，不存在双写）。
2. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L93-L237)：`CreateAdapter` switch 加 `PassIds.PatternDesugar => new PatternDesugarPassAdapter(new PatternDesugarOptions(), …)`；不加 `BuildLegacyToggles`（无历史开关）。
3. 无 `Program.cs` 手写块：convert-project 已由注册表统一循环驱动（foundation plan 已完成），新 implemented pass 经 `--enable-pass` 与通用 `preprocess` verb 即达；保留目录、指纹令牌由 descriptor 自动驱动（`pass:pattern-desugar=on/off`）。
4. 不引支撑库：`PassSupportLibrary.None`，`IsExecutedSupportLibraryProject` 无需改。
5. 标准 spine 足够（prepare → compile → 逐文件 Rewrite → restore → verify → clean bin），不重写 `ExecuteAsync`。

---

## 6. 诊断策略

原则（catalog §4.12 第一原则）：**C# 侧无法等价表达时显式报错，禁止注释/占位/原样透传**。

| 码 | 级别 | 含义 |
|---|---|---|
| PD001 | Info | 文件已转换（verbose 逐文件状态：transformed/clean/dirty-no-rewrite，由适配器打印，与 enum 同构） |
| PD010 | Error（Strict 可降 Warning，但不允许默认关） | 不可归约模式：语句标签或守卫中出现位置/列表/Slice 模式，reducer 无分支的 PatternSyntax 种类，或 null 常量作用域非可空值类型 governing；消息含文件、行列、模式语法名、「将由 C37 支持」标注（适用时）；命中节点所在 switch/try 整体保持原样 |
| PD011 | Error | 被归约 switch 内含 `goto case`/`goto default`（D13）；该 switch 整体保持原样 |
| PD020 | Error/Warning（Strict） | 输出树 residual 复检仍有目标节点（不应发生；防御性闸门，同 enum residual；已报 PD010/PD011 的文件跳过） |
| PD030 | Error | restore/build/test 失败（适配器标准三映射） |

**恒真占位/兜底注释的消灭路径**（本 pass 不删转换器代码，但建立不可达性）：

- C03 默认开启后，语句侧 fail-fast 站点（SwitchAndResource L22/L39/L60/L144/L155）对预处理后输入成为不可达；表达式侧 L115/L505 的恒真兜底在 C37 合入前仍是现役代码——C03 通过 D7（守卫扫描 PD010）保证自己的产物不把表达式侧未知模式送进这两个兜底。
- 转换器侧兜底文本的删除列入 **C37 合入后的后续 PR**（对照 java-keywords 设计 §10 的冗余清单做法），C03/C37 两个 PR 均不动转换器，只在本设计与 C37 设计中登记删除项：L115、L505 两处 `_ =>` 兜底届时改为 `throw new NotSupportedException(...)`（fail-fast 安全网），SwitchAndResource 五个 normalization 异常保留为最后防线。
- residual 闸门（§7）在工程级证明「目标构造计数 0」，不靠人工读码。

---

## 7. 测试计划

### 7.1 Core 单测矩阵（新增 `tests/CSharpToJava.Tests/PatternDesugarCoreTests.cs`，对照 EnumPreprocessorUnitTests：内联源码 → `CSharpCompilation` → `Preprocess` → 归一化空白文本断言 + 输出可编译断言）

- **pattern switch → if 链**：声明+类型双标签（`case string s`/`case int i`）；`when` 两分支同名变量 `i`（块作用域隔离）；`case null` 常量；多标签同 section（析取加括号）；`when A || B` / `when A && B` 守卫整体括号（优先级回归）；无 default（无 else、落空即继续）；有 default（else）；section 终止混搭（break/return/throw/continue）；嵌套 switch（**内层循环/switch 的 break 不被外层改写劫持**，D12）；属性模式标签（公共字段与属性两种成员；带类型 `case P { X: 1 }` → `is P v && v.X == 1`、无类型 `{ X: 1 }` → 直接成员访问）；`not`/`and`/`or`/relational/var/discard 标签各一；object governing 上的常量标签（`case 1` → `is int v && v == 1` 编译性形态，D5）。
- **非整型 governing**：bool、long、ulong、float、double、decimal 各一（常量标签 → `==` 链）；**反例**：int/char/string/enum governing 且无模式标签 → 字节级 clean（D15）。
- **governing 单例化**：governing 为方法调用/属性访问时输出恰好一次调用（临时变量）；**简单标识符（局部/参数）不提升**，条件仍引用原名（D2 最小扰动，SwitchCaseWhenClause 断言依赖此点）。
- **绑定形态**：引用类型 `o is string && (s = (string)o) != null`；值类型 `o is int && (i = (int)o) == i`；类型参数 `is T && (ex = (T)(object)__cs2jEx_0) == ex`；`var v` → 块顶 `var v = o;` + 条件 `true`；同一 switch 的多个 section 共用**一份**提升声明（同 section 同名两 case、跨 section 同名 case 均只声明一次，D11 修订后的关键断言）。
- **break 处理**：section 尾部 `break` 消解（无 goto/label 残留）；section 中部 `break`（`if (x) { break; } …`）产 `goto __cs2jSwitchEnd_n;` + 链尾标签；嵌套循环/switch 的 break 不被劫持（D12）。
- **catch-when 策略①（保形归约）**：单 filter 子句（catch 子句原样保留、body 包 if/else throw、**裸 `else throw;`**、rethrow 变量为原 catch 变量 `ex`）；filter 中引用异常成员；`finally` 保留；裸 `throw;` 位于 catch 内。
- **catch-when 策略②（合并链）**：同类型两子句前带 filter 后不带（filter 假落入后子句的链式顺序）；多类型多 filter 顺序；无 filter 具体类型子句折入链；无类型 `catch { }` 末位；合并后体内引用原异常变量名（`is T <原名>` 绑定恢复）；末端裸 `else throw;`。
- **catch T**：仅约束形态 `where T : Exception`（无约束 `catch (T)` 是 CS0155，输入不合法，见 D9 修订）；`catch (T ex) when (…)` 组合；输出含 `catch (System.Exception __cs2jEx_0)` + `__cs2jEx_0 is T && (ex = (T)(object)__cs2jEx_0) == ex`。
- **诊断**：列表模式标签 → PD010；位置模式标签 → PD010；守卫内含 is 模式 → PD010；goto case → PD011；residual 人工注入 → PD020；非 Strict 下 PD020 为 Warning；无 SemanticModel 裸调路径（pattern 标签归约可用、D4 判定不可用）。
- 每个正例断言输出 `dotnet` 可编译（零 error）且不含 `CasePatternSwitchLabelSyntax`/catch filter 语法面（parse 后计数为 0）。

### 7.2 修订既有用例（D16）

- `tests/CSharpToJava.Tests/PatternMatchingTests.cs` 3 个语句侧失败例：新增测试辅助 `DesugarThenConvert(source)`（先 `PatternDesugarPreprocessor.Preprocess` 再 `ConversionPipeline.Convert`），Java 断言全部保留（`if (`、`instanceof String s`、`instanceof int i`、`else`、`DoesNotContain("switch (o)")`、`i > 0`、null 用例）。
- `CatchFilterRethrowTests`：走同一组合辅助；断言放宽为语义形态（`throw ex;` 存在、`else` 存在、无 `AssertionError`，D16②——逐字符 `else { throw ex; }` 不可再现），直接转绿。
- `SwitchCaseWhenClausePrecedenceTests` 2 个 case-when 例同辅助，断言更新为 `type == 1 && (Objects.equals(str, "A") || Objects.equals(str, "B"))` / `type == 1 && (m > 0 && ch != 5)`；同文件其余 4 个无关注释/无符号用例不动。
- `tests/CSharpToJava.ConversionTests/Categories/PatternMatchingTests.cs`：`PropertyPattern_ConvertsWithTodoMarker` 改为 `[Fact(Skip = "C37: expression-side recursive pattern reducer")]` 并改写用例体为 C37 目标断言草稿（`p.X` 成员比较、无 TODO 文本），由 C37 PR 取消 Skip；本 PR 同时把「断言含 TODO 文本」这一过时期望从仓内消灭。`IsDeclarationUsed…getLength/length` 差异另开 issue 并在用例旁注释，不计入 C03 红绿。
- C37 的 8 红中其余 7 例保持现状（C37 PR 转绿），但 `PatternMatchingNoCSharpResidue` 的空洞通过问题在 C37 设计中处理（恒真兜底删除后该用例自然变严格）。

### 7.3 项目级 fixture

新增 `tests/SamplePatternSolution/`（`SamplePatternLib` + `SamplePatternApp` + `SamplePatternSolution.sln`，net10.0，Content Include 进 ProjectTests，仿 SampleRecordSolution）：

- Lib：模式 switch（声明/类型/常量/null/属性/relational/and/or/not/when 全覆盖）、bool 与 long governing switch、`catch-when`（含同类型双 catch 顺序）、泛型方法内 `catch (T)`；配套 xunit 测试锁定运行时行为（filter 假必须继续传播、模式分支命中值）。
- App：跨文件调用，保证模式变量/异常变量不跨文件泄漏。
- 新增 `PatternDesugarProjectTests.cs`（仿 RecordReadOnlyProjectTests，经 `CliPreprocessChain` + `--enable-pass pattern-desugar` 驱动）：断言 `Success`、统计字段、中间树不含目标语法面、`dotnet build`/`dotnet test -c Release` 退出码 0、干净工程字节级复制（含 BOM 保持）。
- residual 闸门测试：对 fixture 输出树跑 Analyzer 复检，`ResidualCount == 0`；另备一个「白名单 skip」夹具（含列表模式）断言在显式 skip 配置下 pass 成功且 PD010 归白名单、不白名单时 PD010 失败——即「目标模式计数 0 或仅白名单 skip」的双态锁死。

### 7.4 注册表与一致性测试

- `PassStubCatalogTests`：stub 数 23→22；补一条 `pattern-desugar` 不在 stub 集且为 Implemented 的断言。
- `PassChainOrderTests`：`LockedChain` 插入新位（foreach-deconstruction 之后、strip-default 之前）、默认开切换时改 `EnabledByDefault`；末位仍是 java-keywords 的断言不受影响；**`BuildLegacyToggles_covers_every_registered_pass` 的排除集补入 `pattern-desugar`**（新 implemented pass 无 legacy toggle，与 generic-deepen 同列）；`ChainFactory_creates_an_adapter_for_every_implemented_pass` 随工厂分支自动覆盖新 id。
- CLI：`PreprocessVerbCLITests` 增加 `preprocess --pass pattern-desugar` 可达性、`--enable-pass/--disable-pass` 覆盖、保留目录 `.cs2j-pattern-desugar-src`、PD010 退出码语义。

### 7.5 全量验证

`dotnet build -c Release` 全绿；三个测试工程（CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests）`dotnet test -c Release` 全绿（ConversionTests 允许且仅允许 §7.2 登记的 C37 Skip 白名单）；csharp-sdk 基线项目转换不退化（默认关阶段先跑 opt-in 对比，默认开阶段跑全链）。

---

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足（catalog §4.10 的统一门槛具体化）：

1. §7.1-7.4 全绿；三工程 Release build/test 全绿。
2. 项目级 fixture 的 .NET 半区：中间树原项目测试全绿（证明 if 链/catch 链运行时语义等价，尤其 filter 假传播、同类型多 catch 顺序、governing 单次求值）。
3. residual 闸门：fixture 与 csharp-sdk 抽样项目输出树目标节点计数为 0，或仅余白名单 skip（每条 skip 有 PD010 编号与 C37 去向）。
4. csharp-sdk 基线：默认开 vs 默认关两次 convert-project，已转换成功的项目结果集不缩小、Java 侧测试不新增红；原失败项目中至少 pattern switch/catch-when 命中样本转为成功（收益证据）。
5. 转换器恒真兜底在本 pass 输出上不可达的论证成立（D7 守卫扫描 + 代码审查；C37 合入后再做 L115/L505 硬化）。
6. 性能：单环 restore/build/test 串行成本可接受；中间目录已接指纹（descriptor 驱动自动生效），无需额外汇编。

未满足前保持 opt-in（`--enable-pass pattern-desugar`），但 Status 已是 Implemented——stub 翻转与默认开是两个独立、可分别验收的事件。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 | Core 脚手架九文件（Options/Statistics/Diagnostic/Result/Preprocessor dirty-check/格式化/residual 复检）+ 组件计划模型 + reducer 接口与 C03 模式种类 + 全量 Core 单测 §7.1 前半（switch 族） | Core ~700 行 |
| M2 | catch-when/catch T 归约（D8-D10）、governing 单例化、break→goto 尾标签、PD010/PD011 闸门 + 对应单测 | Core ~400 行 |
| M3 | CLI 适配器 + 注册表翻转（descriptor 移位、PassIds、Factory、链序/stub 测试 23→22）+ 通用 verb/CLI 测试 | CLI ~120 行 + 测试改动 |
| M4 | §7.2 既有用例夹具化转绿、SamplePatternSolution fixture + 项目级测试、Release 全量验证、默认开门槛证据（§8） | fixture + 测试 |
| M5 | 实现计划文档 `docs/superpowers/plans/2026-09-28-c03-pattern-statement-desugar.md`（writing-plans 展开）、C37 扩展点接口冻结评审、转换器兜底硬化清单移交 C37 | 文档 |

---

## 10. 风险

1. **测试红灯与实现/预期双向脱节（catalog §4.15，主风险）**：`PropertyPattern_ConvertsWithTodoMarker` 断言的 `/* TODO: recursive pattern */` 在 src 已不存在，恒真兜底又让 `NoCSharpResidue` 空洞通过。缓解：D16 在 C03 PR 即消灭 TODO 文本断言、登记 C37 Skip；C37 以 11/11 全绿收口，不允许「改实现迁就过时断言」或反向。
2. **与 JavaGenericPatternRewriter 争抢语法面（§4.15）**：D14 以「先结构归约、后泛型提升」的顺序切割，并保留一条 case 归约后 rewriter 在 switch 上无活可干的测试；带 when 的泛型声明模式合取条件不受 rewriter 提升（与其既有 `&&` 条件行为同类，泛型恢复缺口预先存在，见 D14）。
3. **catch filter 语义近似**：C# 过滤器在栈展开前运行（filter 副作用/finally 时序与「filter 中再抛」语义与 Java 不可表达层不同）。if/rethrow 链是标准机械近似；风险接受条件：fixture 覆盖 filter 假传播与顺序，文档化「filter 内副作用依赖」为已知限制（与 java-keywords §9 同体例），不声称 100% 语义同构。
4. **表达式树禁区（§4 风险 8）**：C03 不重写守卫表达式内部、不改名、不生成表达式树节点变形，理论上不触碰该禁区；D7 扫描与 reducer 复用 catalog 要求的「表达式树节点内跳过」共享判定（C37/C08/C25 共用工具落地前，本 pass 对位于表达式树 lambda 内的 switch/catch——实际语言不允许——天然无命中，仍在评审中保留该检查项）。
5. **实验性 Lowering 不可复用（§4 风险 9）**：[LowerPatternMatch.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerPatternMatch.cs) 与 [VariableResolution.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/VariableResolution.cs) 仅 Java2 对比测试可达（IR 不同、生产不可达）；只借「模式节点分类归约 + 变量作用域两遍处理」的形态，禁止引用其代码或类型。
6. **decimal/浮点 equals 的 Java 半区（§4 风险 14）**：D4 在 C# 侧发 `==` 是 .NET 语义正确的；Java 半区依赖 numeric-boundary 族对 decimal 二元运算的后续显式化。默认开门槛 §8.4 要求至少一个 decimal governing 样本在 Java 半区验证；未满足前该类型可维持 PD010 白名单姿态。
7. **链位变更面**：RunAfterIds 从单锚扩三锚会改变 `--list-passes` 中 stub 的展示位置（stub 不执行，无功能影响），但必须与 C37 worktree 在同一契约上对齐，避免两边对锚点理解分叉（§11）。
8. **goto 消结依赖后序 pass**：D12 产物含 goto/label；若用户在链上禁用了 eliminate-goto，Java 转换仍会失败——这是显式且更早的失败，非静默回退；在该 pass 的诊断/文档中注明与 eliminate-goto 的配套关系。

---

## 11. 并行协调

### 11.1 与 C37 worktree 的精确关系

- **同一 pass id、同一中间目录、同一 Core 目录**：`pattern-desugar` / `.cs2j-pattern-desugar-src` / `src/CSharpToJava.Core/PatternDesugar/`。不允许 C37 另立 pass。
- **合并顺序：C03 先行合入并翻转 stub 为 Implemented（本设计）；C37 基于合入后的代码拉分支，只做三件事**：① 在 PatternReducer 注册 Positional/List/Slice reducer；② 新增并注册 `IsPatternExpressionComponent`、`SwitchExpressionComponent`；③ 放开 D7 守卫扫描的对应 PD010 闸门、取消 §7.2 登记的 Skip、转绿 8 个 ConversionTests 用例、硬化转换器 L115/L505 兜底。
- §4.4 的三个扩展点（`IPatternReducer`、`IPatternDesugarComponent`、NameScope 命名/绑定服务）在 C03 M1 冻结公开形状（internal 但同程序集可见），C37 设计评审须以该形状为准；如需改形状，在 C03 合入前由两份设计共同修订，不合入后再破接口。
- 两侧默认开策略统一在 §8 门槛下管理：C03 可独立默认开（fail-fast 现状使回退方向安全），C37 合入时不得把默认开关回退；PatternMatchingTests 11/11 全绿是 C37 的完成线，不是 C03 的阻断线。
- C37 的位置模式依赖 E06/E07，故本设计已把 RunAfter 锚点在 C03 翻转时一次扩足（§3），C37 不再改链位。

### 11.2 与族内其他条目的交互

- **C05 enum-declaration（enum-as-class）**：现网 enum governing switch 可直转 Java enum switch（D15 不动）；一旦 C05 把枚举类化，switch governing 须扩入归约集。交互方式：未来由 enum-declaration 或 pattern-desugar 任一侧扩 Analyzer 白名单，**不得两边各写一份 switch 归约**（§3.3 合并族原则）；本设计的 SwitchLoweringPlan 把 governing 判定集中在单一谓词，便于届时扩集。
- **C16/C40 foreach-source**：字符串 foreach（Loops L121）明确归 foreach-source，本 pass 不碰 `ForEachStatementSyntax`；两 pass 都在 ControlFlow stage，foreach-source 锚 foreach-deconstruction，与本 pass 的 RunAfter 同锚不同节点，无语法面竞争。
- **E14 eliminate-goto**：D12/D13 的 goto 尾标签是两 pass 的接口契约；pattern-desugar 拓扑序严格在前（现锚 null-coalesce/扩锚后仍在 strip-default 之前，goto 锚 make-readonly）。E14 若增强 `goto case` 能力，不得回流向本 pass 借结构。
- **E06 tuple / E07 foreach-deconstruction / generic**：仅作为锚点与输出消费方（`is T v`、元组成员比较的 C# 形态由它们在各自环节处理），本 pass 不直接依赖其 API。

---

## 12. Worktree 分配

- 分支：`feature/preproc-c03-pattern-desugar`
- 目录：`.worktrees/preproc-c03-pattern-desugar`
- base：`953293b9`（当前 HEAD；stub 注册位于其祖先 `67477551`）
- 创建命令：

```powershell
git worktree add .worktrees/preproc-c03-pattern-desugar -b feature/preproc-c03-pattern-desugar 953293b9
```
