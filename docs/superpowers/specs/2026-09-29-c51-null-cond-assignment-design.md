# C51 C# 14 简单空条件赋值（`a?.b = c` 及复合形态）设计（`expression-desugar` 族 `NullConditional/Assignment` 规则）

**日期**: 2026-09-29
**范围**: 在 C35 落地的 `src/CSharpToJava.Core/ExpressionDesugar/NullConditional/` 目录内新增 `Assignment/` 子规则，把 C# 14 空条件赋值（成员 `a?.b = c`、索引 `a?[i] = v`、全部复合赋值 `a?.b += c`、含 `a?.b ??= c`、链式 `a?.b?.c = d`、结果被消费的值形态 `return a?.b = c`）在 C# 层归一为「段临时变量 + `if` 守卫语句 / null 三元」，保持接收者**严格单次求值**且 **RHS 与索引实参仅在接收者非 null 时求值**。不新增 pass、不翻转 stub、不改任何 C#→Java 转换代码
**状态**: 设计已确认，待实施（硬依赖 C23 锚点与 C35 分支先合入；M0 Roslyn 探针为开工前置）
**注册表**: 不新增条目；沿用 stub `expression-desugar`（[PassStubCatalog.cs:239-248](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L239-L248)，catalogItems `"C23, C24, C35, C36"` 已含 C35 与本项的编号位）。C51 落地时把 C35 已加宽的同一描述符 catalogItems 再追加为 `"C23, C35, C51"`（[C35 §3.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c35-null-conditional-design.md#L94-L99) 同一描述符、同一字符串的第二次加宽）
**base**: `9b99c60b33980c3c351b70d6b99e6b4bf57b30ae`（`docs: C46 成员签名显式化设计`，2026-09-29 已 `git rev-parse --verify` 校验存在；实施前先合入 C23+C35 分支，见 §12）

---

## 1. 背景与目标

### 1.1 C35 只覆盖读侧；赋值目标是 C# 14 新语法面

C35（[2026-09-29-c35-null-conditional-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c35-null-conditional-design.md)）把读侧空条件链 `a?.b.c?.d()`、`arr?[i]`、`?.Invoke()` 前置归一，并在决策表预留了 `?.=` 条件赋值行（NC-7 / CS2J0555，M2 里程碑），但**未给实施设计**。本任务（catalog C51，P3，随 C35 顺带收编）把该预留落地：赋值目标位置的 `?.` 是 C# 14 才合法的新语法，语义 = 「接收者非 null 才执行赋值，且 RHS 仅在此时求值」，静默丢守卫即可观察行为差异。

### 1.2 现状复核：转换器对赋值目标形态无分支，静默产错误 Java（非 fail-fast）

2026-09-29 对主工作区树（C23/C35 均未合入，`src/CSharpToJava.Core/ExpressionDesugar/` 不存在）复核：

| # | 站点 | 现状行为 |
|---|---|---|
| A1 | 整节点按 `ConditionalAccessExpression` 分派到 [ControlFlowTransformer.cs:314-337](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L314-L337)：`TransformConditionalAccess` 无脑产 `(obj != null ? whenNotNull : null/false/0)`；`whenNotNull` 交 facade | 空条件赋值在 Roslyn 语法树里整节点仍是 ConditionalAccess（§1.3 形态 F1），WhenNotNull 是 AssignmentExpression，被嵌进三元真分支文本；不产守卫语句，成员路径还丢失接收者（见 A2） |
| A2 | `ExpressionTransformerFacade.TransformWhenNotNull`（[ExpressionTransformerFacade.cs:96-388](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L96-L388)）**无 AssignmentExpression 分支**（全方法 grep `Assignment` 零命中），落入 [L383-387](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L383-L387) 默认 `Transform(expr)` | `a?.b = c` 的 WhenNotNull 交给 [AssignmentTransformer.cs:44-77](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L44-L77)；其属性路径仅在 `a.Left is MemberAccessExpressionSyntax ma`（[L84](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L84)）时取接收者，而此处左值是 `MemberBindingExpressionSyntax` → 产无接收者的 `setX(c)` 文本；索引路径要求 `ElementAccessExpressionSyntax`（[L63](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L63)），而此处是 `ElementBindingExpressionSyntax`（读侧 `?[]` 已确认零分支、测试以 TODO 锁定，[NullableTests.cs:25-31](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/NullableTests.cs#L25-L31)）→ TODO 注释进 Java |
| A3 | 语句层：`a?.b = c;` 外层是 CA 不是 Assignment，[StatementTransformer.ExpressionAndReturn.cs:58](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L58) 的赋值快路径不命中，走 `TransformConditionalAccessStatement`（[L317-361](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L317-L361)）拼 `if (obj != null) { … }` | 守卫骨架巧合正确，但块内仍是 A2 的错误目标文本；RHS 条件求值在字符串拼装路径下碰巧落在真分支内（Java 三元/if 惰性），属偶然保真而非设计 |
| A4 | E03 对 `a?.b ??= c` 的**具体破坏路径**（本任务新核实，§3.4 升级为硬依赖）：[NullCoalesceSyntaxRewriter.cs:133-218](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L133-L218) `VisitAssignmentExpression` 不检查祖先，默认遍历会进入 CA.WhenNotNull 内的 CoalesceAssignment；左值是 MemberBinding（非 Identifier），走成员路径产 `var ___tN = .b;` 与 `if (___tN == null) { .b = c; }`，并把 **IfStatement 返回到表达式槽位** | C# 14 合法输入（§1.3 形态 F7）经 E03 后成不可编译/结构损坏中间树；C35 §3.4 原把 E03 guard 列为「可选小补丁」，C51 必须将其升级为硬前置或自备形状防御（§3.4、§10 R3） |

### 1.3 ★官方限制集核实结果（2026-09-29 核；**两处推翻任务假设**）

来源：C# 14.0 特性规范 [null-conditional-assignment](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-14.0/null-conditional-assignment)、[member-access-operators](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/member-access-operators)、[What's new in C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14)。

- **F1 语法树形态（已定）**：整节点是 **`ConditionalAccessExpressionSyntax`**（`Expression`=接收者 `a`，`WhenNotNull`=AssignmentExpression）；**不是** AssignmentExpression 包 CA。规范 Implementation 节明确语法设计不变、WhenNotNull = `.b = c`（左为 MemberBinding，op token，右为 RHS），并记载了被否决的替代方案（让 `?.` 成为 `=` 的子节点）。索引形态 WhenNotNull = AssignmentExpression，其左为 `ElementBindingExpressionSyntax`（`[i]`）。M0 探针只需验证 Roslyn 4.12/升级版的精确字段外形，无需二选一。
- **F2 `a?.b = c`、`a?[i] = v` 合法**（引用类型接收者）；语句形态等价 `if (P is not null) P.A = B;`，值形态等价 `(P is null) ? (T?)null : (P.A = B)`——**P 只求值一次**。
- **F3 ★链式合法（推翻「不允许 a?.b.c = v」假设）**：文法为 `null_conditional_member_access assignment_operator expression`，标准 §11.7.7 的 null 条件成员访问允许 `?.` 后接普通成员链；规范复杂示例明确给出 `M(a?.b?.c = d);` → 双层 null 三元，故 `a?.b.c = d`（单 `?.` 后普通链）同属合法文法。C51 必须支持链，不得当非法形态拒绝；M0 探针钉两形的解析与诊断。
- **F4 复合赋值全部允许**：`+= -= *= /= %= &= |= ^= <<= >>=`，规范原文 "All forms of compound assignment are allowed"；事件访问 `c?.E += handler` 列为正文示例（event 复合目标合法）。属性/索引器复合的 get 一次、set 一次与普通复合赋值完全一致（守卫只包外层条件）。
- **F5 `++`/`--` 不允许**（`a?.b++`、`--a?.b` 编译错误；2024-10-28 LDM 决议）——编译器先拒，不在实现范围，仅安全网诊断登记。
- **F6 RHS 条件求值**：`values?[2] = GenerateNextIndex()` 中 values 为 null 时 `GenerateNextIndex` 零调用；**索引实参同理**（在 `P?[A] = B` 等价式中 A 与 B 都只在非 null 分支出现）。语句提升时 RHS 与全部索引实参必须整体在 if 体内，禁止提前到守卫外。
- **F7 ★`a?.b ??= c` 合法（推翻任务「待核实/可能非法」假设）**：规范复杂示例给出完整 lowering：语句形 = 嵌套 if（`if (a != null) if (a.b == null) a.b = c;`），值形 = 双层 null 三元。C51 须覆盖，并直接引出 A4 的 E03 顺序问题。
- **F8 不是变量**：不可 `M(ref a?.b)`、不可 ref 赋值 `a?.b = ref x`（后者另因 ref struct 不能进 nullable）、不可解构赋值 `(a?.b, c?.d) = (x, y)`——均编译错误，C51 只登记安全网。
- **F9 值类型接收者不工作**：非 nullable struct `MyStruct a; a?.b = c;` 不允许（`?.` 作用于非可空值类型）；`MyStruct? a; a?.b = c;` 语义上 `a.Value` 不是变量，无合理赋值语义。沿 C35 NC-7 策略：record struct 可写站点 0562 移交 make-readonly，其余 0561 Error，禁止产 `.Value.X =`（CS1612）。
- **F10 结果被消费时类型须可空化**：`(c?.field = t).ToString()` 在无约束泛型 `T` 上是编译错误（'T' cannot be made nullable）；值形态结果类型 lifted `T?`，真相取 `GetTypeInfo(chainRoot).Type`，与 C35 INV-3 同一来源。
- **F11 特殊合法目标**：ref 返回方法 `c?.M() = 42` 合法（WhenNotNull 为 `InvocationExpression` 包 MemberBinding 再居 Assignment 左；等价 `if (c != null) c.M() = 42;`）；RHS 可再含空条件赋值（右结合 `a?.b = c?.d = e?.f` 规范有示例）；委托/事件 `?.Invoke()` 是读侧/void 侧，归 C35 NC-5，不是本规则。
- **F12 静态成员目标**（如 `a?.Static = v`）：规范未单列；语义上守卫无害（接收者仍只求值一次、RHS 仍条件求值）。M0 探针钉编译器接受度/警告号；接受则机械降级并计 Info，不特殊拒绝。

### 1.4 ★Roslyn 版本与 LangVersion 核实

- 仓库钉版 [CSharpToJava.Core.csproj:12-14](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12-L14)：`Microsoft.CodeAnalysis.CSharp` / `.Workspaces` / `Workspaces.MSBuild` 均 **4.12.0**，TFM `net10.0`。
- C# 14 随 .NET 10 GA（Roslyn 4.14 世代 / VS 2026）；空条件赋值特性在 4.13/4.14 预览周期落地。**4.12.0 能否在 `LanguageVersion.Preview` 下解析本语法未经运行时证实**（本任务禁止 build），是 M0 第一探针项；现有测试 harness 的解析选项并不统一（[RecordReadOnlyRewriterUnitTests.cs:15-16](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs#L15-L16) 用 `LanguageVersion.Preview`、[IteratorLoweringUnitTests.cs:465](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs#L465) 用 `LanguageVersion.Latest`），中间工程则由 [ReadOnlyStructPassEngine.cs:775-790](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ReadOnlyStructPassEngine.cs#L775-L790) `EnsureLangVersionLatest` 注入 `<LangVersion>latest</LangVersion>`。
- 结论：M0 若证实 4.12.0 不解析（产乱序树/skipped tokens），**先决动作 = 升级三个 CodeAnalysis 包到 .NET 10 GA 对应版本**（与 C52 版本防线同批评审，全包齐升不单升），并统一 harness 解析选项为 C# 14；升级前遇到该语法按 §6 显式报码，绝不静默走 A2 兜底。

### 1.5 目标与非目标

目标：① 合法矩阵（F2–F4、F7、F11、链式 F3）全部显式化；② RHS/索引实参条件求值、接收者单次求值以行为探针锁死；③ C35 NC-7/CS2J0555 预留落地，复用 ChainSegmentAnalyzer 与 PendingAnchorReplacement；④ residual 可语法探针判定（CA.WhenNotNull IsAssignment）。

非目标：不翻 stub、不新增 pass/适配器/中间目录/支撑库；`++`/`--`、解构、ref 位置不实现（编译器先拒）；不做值类型接收者自降 `.Value` 回写（沿用 0561/0562，评审见 Q3）；不改 Java 侧 A1–A3 兜底（默认开后随 C35 整体退役）。

**硬不变式**：NA-INV-1 接收者恰好一次求值（null/非 null 两盘计数 =1）；NA-INV-2 null 时 RHS、全部索引实参、目标链后段 getter 零求值；NA-INV-3 属性/索引复合目标 get 一次 set 一次；NA-INV-4 值形态结果 lifted `T?`；NA-INV-5 幂等（二次运行零 CA-assignment 残留、零 diff）。

## 2. 关键设计决策表（精确语法矩阵与语义决策）

### 2.1 语法合法性矩阵（M0 逐格钉诊断号；本表以官方规范为准）

| # | 形态 | 合法性 | 降级产物 / 处置 |
|---|---|---|---|
| M1 | `a?.b = c;`（引用类型成员） | ✅ | NA-1：`[var __ed_s0 = a;] if (__ed_s0 != null) __ed_s0.b = c;` |
| M2 | `a?[i] = v;`（数组/索引器，单/多维） | ✅ | NA-2：`[var __ed_s0 = a;] if (__ed_s0 != null) __ed_s0[i] = v;`，i 与 v 全在体内 |
| M3 | `a?[GetIndex()] = GetValue();` | ✅ | NA-2：a 为 null 时 GetIndex/GetValue 均 0 次（探针断言）；二者整体在 if 体 |
| M4 | `a?.b += c` … `>>=`（10 个复合运算符逐运算符） | ✅ | NA-3：守卫体内**原样保留复合赋值**，不自行拆 get/set（交给后续 C17/Java 侧） |
| M5 | `c?.E += handler;`（event 复合） | ✅ | NA-3 事件子形：守卫体内普通 `+=`；handler 转换仅非 null 发生 |
| M6 | `a?.b ??= c;` / 值形态 | ✅（F7） | NA-6：守卫体内再嵌套 E03 同形 if（见 §3.4 模板），不依赖 E03 二次运行 |
| M7 | `a?.b.c = d;`（单 `?.` + 普通链尾） | ✅（F3，M0 复核） | NA-4：`if (__ed_s0 != null) __ed_s0.b.c = d;` |
| M8 | `a?.b?.c = d;`（多 `?.` 链） | ✅（F3 规范示例） | NA-4：段临时 + 嵌套 if（语句形）/嵌套三元（值形） |
| M9 | `x = a?.b = c;`、`return a?[i] = v;`、`M(a?.b = c)` | ✅（F2 值形态） | NA-5：`__ed_s0 != null ? __ed_s0.b = c : (T?)null`，类型取 GetTypeInfo |
| M10 | `c?.M() = 42;`（ref 返回方法目标） | ✅（F11） | NA-1 同形（WhenNotNull 调用式）；绑定 RefKind 校验，非 ref 返回为基线编译错误 |
| M11 | `a?.b = c?.d = e?.f;`（RHS 嵌套空条件赋值/读侧链） | ✅（F11） | NA-7：同一趟 visit 内层各自成计划，外层守卫体内嵌内层三元/if |
| M12 | `a?.b++;` / `--a?.b;` | ❌ 编译错误 | 不实现；0569 安全网（防解析差异下漏网）；M0 记录 CS 号 |
| M13 | `(a?.b, x) = (c, y);`（解构目标） | ❌ 编译错误（F8） | 0569 安全网 |
| M14 | `M(ref a?.b)` / `a?.b = ref x` / `ref var z = ref a?.b` | ❌ 编译错误（F8） | 复用 C35 CS2J0560 安全网 |
| M15 | struct 接收者：`MyStruct s; s?.b = c;` 与 `MyStruct? s; s?.b = c;` | ❌/无语义（F9） | record struct 可写 → CS2J0562 保留移交 make-readonly；其余 CS2J0561；禁 `.Value.X=` |
| M16 | 无约束泛型 `(c?.field = t).ToString()` | ❌ 编译错误（F10） | 语句形合法走 NA-1；值形不可空化 → 基线 build 拒绝，防御 0559 |
| M17 | `a?.Static = v;` | 待 M0 钉（F12） | 暂定机械降级 + Info 计数；若基线拒绝则归基线错误不处理 |
| M18 | 接收者为属性/方法调用（`GetA()?.B = c`、`a.Prop?.B = c`） | ✅ | 段临时必发（非内联白名单），getter 严格一次（NA-INV-1） |
| M19 | RHS 含读侧 `?.` 链（`a?.b = c?.d + 1`） | ✅ | NA-7：RHS 子树由同规则读侧终端（NC-1…NC-5）在守卫体内闭合 |

### 2.2 语义决策行

| 决策点 | 结论 |
|---|---|
| 语句/值形态分流 | 锚点是 `ExpressionStatementSyntax` 且整表达式为语句形 → NA-x 的 **if 守卫块**（规范语句形）；否则按规范值形态产 **null 三元**（AssignmentExpression 留在真分支内，赋值本身在 C# 中是有值表达式）；两形都只要求一个接收者段临时，不强制锚点替换（见 §4.3 与 Q1） |
| 接收者单次求值 | 完全沿用 C35 白名单策略：接收者为局部/参数/`this`/字段（IFieldSymbol/ILocalSymbol/IParameterSymbol）→ 零声明内联 `a != null`；其余（属性/方法调用/索引）→ `__ed_sN` 段临时在 if 外声明一次。白名单内重复的是简单名读，无用户副作用 |
| ★RHS 与索引实参位置 | 语句形：RHS 表达式节点与 ElementBinding 的全部 ArgumentSyntax **原样置于 if 块内**，禁止在守卫外预取/预生成；值形态：置于三元真分支。禁止把 RHS 提到段临时（即使「看起来更可读」）——这是本特性最高发语义错误（§10 R1） |
| 复合赋值展开度 | C51 **不拆**复合赋值：守卫体内是原运算符的普通复合赋值（`__ed_s0.b += c`）。get/set 次数因此天然与普通复合一致；属性/索引器方法化（C12）、自增目标/自定义复合运算符（C17/C14/C52）都在后续 pass 对守卫体内普通节点作业 |
| `??=` 组合 | C51 直接在守卫体内做 E03 同形两语句降级（§3.4），不依赖 E03 再跑一趟；同时要求 E03 侧合入「祖先在 CA 内的 CoalesceAssignment 跳过」guard（A4），未合入时 C51 形状探测到 E03 损坏产物 → CS2J0569 Error，不生成双求值/不可编译码 |
| 链式规划 | 不逐 CA 独立改写，复用 C35 `ChainSegmentAnalyzer` 的「最外层 CA 一个 ChainPlan」整体规划；Terminal 种类在 C35 `TerminalKind.Assign` 上扩展运算符种类与目标形态；嵌套内层 CA 标记已规划，一趟 visit 闭合 |
| 事件 `?.Invoke()` 边界 | `a?.Ev?.Invoke(x)` 归 C35 NC-5（读侧/void）；`a?.Ev += h` 归本规则 NA-3（赋值终端、event 符号）；同一 WhenNotNull 不会同时命中 |
| 临时/命名 | 段临时继续用 C35 子前缀 `__ed_s<N>`（不新增前缀）；赋值场景不引入结果临时（值形态直接用三元）；编号走文件上下文统一发号，与 C23/C35 同序号空间 |
| 判定真相源 | 运算符取语法 Kind；目标符号（IPropertySymbol.IsIndexer / IEventSymbol / IMethodSymbol RefKind）、接收者值类型判定、lifted 结果类型一律 SemanticModel/IOperation；M0 钉 `IConditionalAccessOperation.WhenNotNull` 是否为 `IAssignmentOperation` 的外形 |
| 幂等/residual | 产物中「WhenNotNull 为 AssignmentExpression 的 CA」归零；residual 闸门按该**语法形状**计数，不按 `?.` 文本（注释/字符串/`>=` 不误伤），与 C35 CS2J0566 同一闸门加一项 |

## 3. 链位与族契约

### 3.1 归属：C35 规则目录内的 `Assignment/` 子规则；零 stub、零注册

- 物理落点：`src/CSharpToJava.Core/ExpressionDesugar/NullConditional/Assignment/`（§4.1）。C35 的 `NullConditionalRule` TriggerSyntax 仍是 `ConditionalAccessExpression` 单一语法面——读侧与赋值侧在**同一条规则、同一个 ChainPlan 管线**内按 Terminal 分派，不新增 IExpressionDesugarRule 行、不新增 TriggerSyntax。catalog「C51 是 NullConditional/ 内 Assignment 规则文件」即此形态。
- 注册表零结构改动：唯一允许触碰是 C35 已翻转描述符 catalogItems 单行再加宽 `"C23, C35" → "C23, C35, C51"`；Status/Stage/RunAfter(`null-coalesce`)/目录名/指纹机制一律不动；`PassStubCatalogTests` 计数不变。
- 依赖：硬依赖 C23（族规则接口、`Shared/TempLifter`、Options/Statistics/Diagnostic、适配器）与 C35（`ChainSegmentAnalyzer`、`NullConditionalEmitter`、`PendingAnchorReplacement`、`NullConditionalSyntaxFacts`）；不引用 E03 类型。
- 接入点仅三处追加式（并入 C35 既有追加面，不新增热文件）：`NullConditionalModels.cs` 的 Terminal 赋值子形记录（只增字段）、Emitter 的 NA 渲染函数（新文件，§4.1）、统计 partial 加桶；RuleSet/Options 无新增行（NullConditional 规则行已含本终端）。

### 3.2 规则执行序：LHS 赋值终端不与读侧交叠，单趟 visit 即闭合

- 赋值终端（WhenNotNull 末节点 IsAssignment）与读侧终端（成员/调用/索引/Invoke）互斥，Analyzer 一次分类；不存在「先跑读侧把 LHS 改坏再跑赋值侧」的顺序问题。
- RHS 子树中的 CA（含 M11 嵌套赋值、M19 读侧链）由同趟自底向上 visit 先成内层计划，外层计划消费其产物节点；每次替换严格减少 CA-assignment 节点数，单趟终止（终止性证明要素写进单测，口径同 C35 §3.3）。
- 与 C35 读侧规则无固定点需求：产物只含 if/三元/普通成员访问/普通复合赋值，不引入新的条件访问节点。

### 3.3 与 C17（复合目标族）的顺序论证

链序事实：expression-desugar（Expression 阶段，null-coalesce 之后）远早于 numeric-boundary（unsigned 之后，C17 [§3.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md#L115-L120)）。

1. `a?.b += c`：C51 先变 `if (__ed_s0 != null) __ed_s0.b += c;`——守卫体内是**普通属性复合赋值**；C17 之后按其 IT-1 决策对属性目标做读-改-写展开时，节点位于普通 if 块内，其「条件执行祖先/锚点可行性」分析（[C17 §4.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c17-increment-target-design.md#L166-L196)）把它当普通块内语句处理即可，无需感知 `?.`。
2. `a?.b++` 非法（F5），故 C17 TargetKind.Conditional（其 Analyzer 表中「`?.` 条件访问目标 → CS2J5339 指路 C35」）在 C# 14 输入下对 `++/--` 不可达；C17 保留该指路作为旧 LangVersion/防御面，两族无代码依赖。
3. 用户定义复合赋值运算符（C52②、C17 决策 11，C# 14 `op_Checked...` 新形态）：C51 产物 `__ed_s0.b += c` 留在守卫体内，C17 查 SemanticModel 显式复合运算符的路径不变；RHS `c` 已被守卫保护，符合「仅非 null 时求值右操作数并执行用户运算符」。
4. get/set 次数：C51 不拆复合，C17/属性方法化各做一次 get + 一次 set，全链恰好各一（NA-INV-3 探针在 C17 合入前后两态各跑一次）。

### 3.4 与 C12（索引器方法化）的顺序论证

链序事实：expression-desugar 早于 member-desugar（[foundation plan Stub 表](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L92-L99)：expression-desugar 排 null-coalesce 后，member-desugar 排 struct/closure 后；C12 [§3.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c12-indexer-desugar-design.md#L77-L93) 随 member-desugar）。**C51 在先为正确序**：

- 语法面只有 C51 认识 `ElementBindingExpressionSyntax` 居 Assignment 左的形态；若 C12 在先，它面对的是「条件绑定」而非 ElementAccess，没有可方法化的 `set_Item(...)` 接收者结构（ElementBinding 无独立接收者子节点，接收者在 CA.Expression）。
- C51 产物为 `if (__ed_s0 != null) __ed_s0[i] = v;`——守卫体内是普通 ElementAccess 赋值，C12 IX 规则按其既有调用点改写方法化为 `__ed_s0.SetItem(i, v)`，对 i、v 的求值位置不变（都在块内，NA-INV-2 不破）。
- 复合 `dict?[k] += v` 同理：C51 守卫体 → C12 对普通索引复合走其 get/set 双方法形；C12 无需任何 `?.` 感知改动。联合 fixture 钉死两 pass 开关四象限。

### 3.5 `??=` 组合的交汇模板（NA-6）

```csharp
// before: a?.b ??= c;（语句形；E03 必须已跳过此内层节点，见 A4）
var __ed_s0 = a;
if (__ed_s0 != null)
{
    var ___t0 = __ed_s0.b;              // 与 E03 成员左值同形（其已知接收者重复近似在此不出现：
    if (___t0 == null)                  //   __ed_s0 是临时，重复读无副作用）
    {
        __ed_s0.b = c;                  // c 仅在「a 非 null 且 b 为 null」时求值
    }
}
// 值形态：return a?.b ??= c;
//   → return __ed_s0 == null ? (T?)null
//          : __ed_s0.b == null ? __ed_s0.b = c : __ed_s0.b;   // 规范 F7 原形
```

E03 guard 建议（独立小 PR，归 E03 既有测试守门，C35 §3.4 已提议、C51 升级为硬前置）：`VisitAssignmentExpression` 入口对 `node.IsKind(CoalesceAssignmentExpression)` 且「向上穿越到语句锚点前先遇 ConditionalAccessExpression」（实操判据：`node.Left is MemberBindingExpressionSyntax or ElementBindingExpressionSyntax` 即绑定节点）→ 计 `SkippedUnsupported` 并 `return node` 原样保留。C51 侧以 CS2J0569 对未打补丁的损坏形状（声明初值/if 左值出现裸 MemberBinding）做防御拒绝。

## 4. Core 组件设计

目录（全部为 C35 目录下新增文件；C35 既有文件仅允许只增不改的模型/统计扩展）：

```
src/CSharpToJava.Core/ExpressionDesugar/NullConditional/Assignment/
  NullConditionalAssignmentRule.cs     # 终端分派器：被 NullConditionalRule 在 Terminal=Assign 时调用；
                                       #   AnalyzeAssignmentTerminal（目标种类/运算符/值or语句/链段）+ Emit 入口
  AssignmentTargetModels.cs            # AssignmentTerminal 记录：OperatorKind(=|+=..|??=)、
                                       #   TargetShape(Member/Index/RefReturningMethod/Event/Unknown)、
                                       #   ContextShape(Statement/Value)、提升决策
  NullConditionalAssignmentEmitter.cs  # 纯函数渲染：if 守卫块（NA-1/2/3/4/6）、null 三元值形态（NA-5）、
                                       #   ??= 嵌套形（NA-6）；保证 RHS/索引实参节点整体在守卫/真分支内
  NullConditionalAssignmentFacts.cs    # 运算符白名单（10 复合 + ??=）、绑定节点判据、
                                       #   值类型接收者判定、静态成员判定、E03 损坏形状探测
  NullConditionalAssignmentStatistics.cs # partial 并入 NullConditionalStatistics（§5）
```

### 4.1 识别与计划（复用 C35 ChainSegmentAnalyzer，只增终端分支）

- 识别判据（语法，F1）：`ConditionalAccessExpressionSyntax ca`，`ca.WhenNotNull` 剥除括号后：
  - `AssignmentExpressionSyntax a`（M1–M9、M11）→ `a.Left` 为 `MemberBindingExpressionSyntax`（成员）、`ElementBindingExpressionSyntax`（索引）、或左子为绑定的 `InvocationExpressionSyntax`（M10 ref 返回方法）；
  - 多段链 M7/M8：Assignment 位于最深层 CA 的 WhenNotNull，外层 CA 由同一 ChainPlan 段序列覆盖；
  - `a.Left` 为 TupleExpression、或 op 为 `=` 且含 `ref` 修饰、或父位为 ref/out 实参 → 基线非法（M13/M14），登记安全网不改写。
- Analyzer 扩展输出：`AssignmentTerminal(OperatorKind, TargetShape, ContextShape, ResultType from GetTypeInfo(ca), AnchorStatement, ReceiverWhitelisted, SegmentPlan 引用)`；值/语句上下文判定复用 C35 的锚点搜索（ExpressionStatement → Statement；Return/实参/变量初值等 → Value）。
- 语法树适配策略：M0 钉死后仅支持 F1 形态（CA 包 Assignment）；若探针发现个别预览版把 op token 挂在不同位置，Facts 层集中适配（一个 `GetAssignmentTerminal(ca)` 入口返回 null 即不识别 → 0564/0569），规则主体不分支版本逻辑。
- IOperation 旁证：`GetOperation(ca)` 预期为 `IConditionalAccessOperation`，其 `WhenNotNull` 为 `IAssignmentOperation`（取 Target/Value 符号与 lifted 结果类型）；IOperation 与语法外形不一致时以语法+GetSymbolInfo 为准并计 0559，不猜。

### 4.2 守卫语句合成与插入点

- **语句形（NA-1/2/3/4/6）**：锚点 `ExpressionStatementSyntax` 整句替换为 Block（段声明（非白名单时）+ if 语句），复用 C35 `PendingAnchorReplacement`（[C35 §4.4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c35-null-conditional-design.md#L243-L250)）登记，Apply 序与生命周期完全不另造；段临时声明同时是 PendingTemp（Owner=NullConditional，复用 C23 `TempLifter`）。块模板：

```csharp
{
    var __ed_s0 = <receiver>;          // 仅非白名单接收者；白名单时本声明缺省
    if (__ed_s0 != null)
    {
        <__ed_s0 替换后的 WhenNotNull 赋值原节点>   // RHS、索引实参、复合右操作数原样在内
    }
}
```

- **值形态（NA-5）**：不替换锚点、不发结果临时；把 CA 节点替换为 `__ed_sN != null ? <WhenNotNull 赋值节点> : (T?)null`（引用类型终端省略 cast，类型真相取 GetTypeInfo；规范 F2 原形）。接收者非白名单时仅追加一个段 PendingTemp；真分支内 AssignmentExpression 直接作为条件表达式一支合法。Q1 登记「是否统一走锚点块+结果临时」备选（可读性 vs 最简产物）。
- 多段链 M8：语句形嵌套 if，每段接收者临时声明在上一层守卫块顶（内层临时随块嵌套）；值形嵌套三元，与规范 lowering 逐格对应。
- **不可提升位置**（字段/属性初始化器、特性实参、默认参数、常量、查询子句、表达式树）：与 C35 §3.5 同矩阵——接收者白名单且无 RHS/实参提升需求时内联零声明（值形态）；否则 CS2J0557 Error（非 strict 降 Warning 且 residual 配对）。表达式树内赋值本就基线非法，走 0565 配对。

### 4.3 规则全集 NA-1 ~ NA-8（before/after 要点）

- **NA-1 成员简单赋值（M1/M10/M18）**：`a?.b = c;` → 上模板；ref 返回方法目标同形，TargetShape=RefReturningMethod 时校验符号 RefKind，非 Ref 返回交基线编译错误。
- **NA-2 索引赋值（M2/M3）**：`a?[GetIndex()] = GetValue();` → `if (__ed_s0 != null) __ed_s0[GetIndex()] = GetValue();`——GetIndex/GetValue 节点原样在块内；多维/自定义索引器实参透传，不改写实参语义；`ref/out` 索引实参随绑定符号透传（基线合法才会出现）。
- **NA-3 复合赋值与事件（M4/M5）**：运算符白名单逐运算符（10 个 + `??=` 归 NA-6）；守卫体内保留复合赋值原节点；event 符号目标同形（后续 C13 事件方法化处理块内 `+=`）。
- **NA-4 链式目标（M7/M8）**：`a?.b?.c = d;` →
  ```csharp
  var __ed_s0 = a;
  if (__ed_s0 != null)
  {
      var __ed_s1 = __ed_s0.b;
      if (__ed_s1 != null) __ed_s1.c = d;   // d 只在最深守卫内求值
  }
  ```
  单 `?.` 普通链 `a?.b.c = d` 只一段临时；链中段接收者严格按段序、各求值一次。
- **NA-5 值形态（M9/M16）**：`return a?.b = c;` → `var __ed_s0 = a; return __ed_s0 != null ? __ed_s0.b = c : (T?)null;`；引用类型 T 时 null 分支裸 `null`；不可空化泛型由基线 build 拒（0559 防御）。
- **NA-6 `??=` 组合（M6）**：模板 §3.5；语句形/值形两式；内层临时命名沿用 E03 风格 `___tN`（同文件内由文件上下文发号避免与 E03 序号碰撞，登记为 C51 自有 `__ed_n<N>` 备选——M1 定一种，Q4）。
- **NA-7 RHS/内层组合（M11/M19）**：RHS 子树在同趟先闭合为读侧三元/内层守卫；外层守卫体内引用其产物；探针断言 `a?.b = c?.d = e?.f` 三层副作用次数与原生 C# 完全相等。
- **NA-8 报错与 residual**：非法目标 0569；ref 上下文复用 0560；值类型 0561/0562；不可提升 0557/0558；dynamic/error 0559；未知终端 0564；residual 0566 加判「CA.WhenNotNull IsAssignment」；每条 Error dirty-no-rewrite（同 C23/C35 入口约定）。

### 4.4 get/set 次数与事件语义

- 成员/索引复合：守卫只增外层一次分支判断，块内复合赋值的访问器调用次数与无守卫普通复合赋值完全相同（get×1、set×1）；`??=` NA-6 形态为 get×1（判空）+ 命中时 set×1，与原生 `??=` 语义一致。
- 事件 `c?.E += handler`：块内 `+=` 触发一次 add 访问器（非 null 时）；handler 的方法组转换/委托分配在块内发生，null 接收者零分配（探针用引用相等计数断言）。
- 订阅列表快照语义不适用（赋值左侧不是 Invoke）；接收者临时即条件判定快照。

## 5. CLI 集成（零结构改动）

1. 不新增适配器/verb/pass id；C51 经 C35 的 `ExpressionDesugarOptions.NullConditional` 组合面生效，不新增独立 Options 属性（赋值终端随 NullConditional 开关；如评审要求可只增一个 `NullConditionalAssignment` 子布尔，默认随母项 true——M1 定，倾向不加）。
2. 描述符唯一变化：catalogItems 第二次加宽（§3.1）；`--list-passes` 快照期望文本同步；中间目录 `.cs2j-expression-desugar-src`、指纹 `pass:expression-desugar=on/off`、clean 字节复制、BOM 保持全部沿用。
3. 无支撑库注入、无 TypeMappings/compat 改动；产物仅含 C# 原生 if/三元/复合赋值。
4. 统计（partial `NullConditionalAssignmentStatistics` 累加入族 `ExpressionDesugarStatistics`，适配器 C35 摘要行内追加一段 `assign=<member>/<index>/<compound>/<coalesce>/<value>`）：
   - `AssignmentMemberSites`、`AssignmentIndexSites`、`CompoundAssignmentSites`（子桶 `ByOperator: Dictionary<string,int>`）、`EventCompoundSites`、`CoalesceAssignmentSites`（NA-6）、`ValueFormAssignmentSites`、`AssignmentChainSites`（M7/M8）、`AssignmentRhsConditionalSites`（含条件 RHS/索引实参计数）、`AssignmentReceiverTempsLifted`、`StaticTargetSites`（M17 观察）、`SkippedAssignmentByReason: Dictionary<code,int>`。
5. fixture 复用 C35 建立的 `tests/SampleNullConditionalSolution/`（[C35 §7.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c35-null-conditional-design.md#L320-L326)），向其追加 `NullConditionalAssignment/` 子目录（Lib/App/Tests 各一，仿 C17 追加 C15 fixture 范式），不新建 sln；verify true/true、`DeleteBuildOutputsAfterRestore`、失败钩子 CS2J0520 全沿用族机制。
6. 入口仍仅 `--enable-pass expression-desugar` / `preprocess --pass expression-desugar`；不单开 verb、不做单文件模式（整编译 SemanticModel 依赖，理由同 C23/C35）。

## 6. 诊断策略

第一原则同 C35 §6：无法等价显式化即显式报码，禁止 TODO/占位/静默走 A2 兜底；strict 关时 Error 降 Warning 但逐条可见、可计数、与 residual 配对。码段在 C35 预留的 **CS2J0550–0569 内只占空位**，不交叉占用：

| 码 | 归属 | C51 用法 |
|---|---|---|
| CS2J0555 | C35 已定义 Info | 空条件赋值站点主计数（NA-1…NA-7 全部站点），分子按 §5 统计桶区分 |
| CS2J0557/0558 | C35 | 赋值终端落不可提升/无锚点位置（字段初始化器、特性实参等） |
| CS2J0559 | C35 | 接收者/目标类型不可判定（dynamic、IErrorTypeSymbol、不可空化泛型值形态） |
| CS2J0560 | C35 | ref/out/ref readonly 上下文安全网（M14；基线非法的防御镜像） |
| CS2J0561 | C35 | `Nullable<TStruct>`/值类型接收者条件赋值且不属 make-readonly 域（M15） |
| CS2J0562 | C35 Info | record struct 可写站点保留移交 make-readonly（M15；与 residual 配对） |
| CS2J0564 | C35 | WhenNotNull 出现未识别赋值终端/未知运算符/未知目标形态 |
| CS2J0565 | C35 Info | 表达式树祖先内跳过（基线非法，配对） |
| CS2J0566 | C35 | residual 闸门加判「CA.WhenNotNull 为 AssignmentExpression」未配对残留 |
| **CS2J0568**（建议占空） | C51 Info | NA-6：`??=` 与空条件赋值组合站点计数（F7 合法形态） |
| **CS2J0569**（建议占空） | C51 Error | 安全网：解构/ref 赋值目标（M13）、`++`/`--` 残留（M12）、E03 损坏形状（A4/§3.5）、解析版本不支持的空条件赋值 |

锚点登记：0568/0569 在 C35 文档中标注为「M3 固定点/新形态预留」。C51 落地时在 C35 码表加注占用锚点；**若 C35 M3 先占用**，C51 改为向族申请新子段 CS2J0570–0579（码段表单 PR 评审），不抢号。restore/build/test 失败仍复用族 CS2J0520。

Roslyn 版本防线（§1.4）：M0 若需升包，升级前规则对「解析树不含预期 F1 结构但源码文本匹配 `?.` 近邻赋值」的站点不猜测，按 0569 报「当前 Roslyn 版本不支持空条件赋值解析」并与 C52 版本扫描口径一致。

## 7. 测试计划

### 7.1 Core 单测（建议 `tests/CSharpToJava.ProjectTests/NullConditionalAssignmentUnitTests.cs`，仿 NullCoalesceRewriterUnitTests / C35 harness）

内联源码 → net10.0 `CSharpCompilation`（解析选项 M0 后统一钉 C# 14/Preview）→ 族 `Preprocess` → 忽略空白文本断言 + 输出可编译断言 + 二次运行幂等断言。矩阵：

1. **合法矩阵全集**：M1–M11、M17–M19 逐格；复合 10 个运算符各一例 + `??=`；成员（字段/属性）、数组、`List<T>`、自定义索引器（多维）、event `+=`、ref 返回方法；
2. **★RHS 条件求值（计数器行为断言）**：`values?[2] = GenerateNextIndex()`，静态探针类记录调用次数——null 盘 GenerateNextIndex=0、非 null 盘=1 且恰好一次；
3. **★索引实参条件求值**：`a?[GetIndex()] = GetValue()`——null 盘两计数均 0；非 null 盘各 1；多维逐实参断言；
4. **★接收者单次求值**：`GetA()?.B = c`、`a.Prop?[i] = v`、`GetA()?.b?.c = d`——接收者 getter/方法在两盘下严格 1 次；白名单（局部/参数/字段/this）零段临时、非白名单每非平凡段恰好一个 `__ed_s`；
5. **复合 get/set 次数**：属性/索引器 `?.P += x` 在两盘下 get/set 计数 = 非null 盘各 1、null 盘各 0；event add 访问器同法断言；`??=` 的 get×1+条件 set×1 分三盘（a null / b null / b 非null）；
6. **链式（M7/M8）**：`a?.b.c = d`、`a?.b?.c = d`、`a?[0]?.c += d`；段临时顺序、嵌套守卫、后段接收者 null 时 RHS 零求值；
7. **值形态（M9/M11/M19）**：`return`/变量初值/方法实参/三元分支内；结果 lifted `T?`（int?/bool?/DateTime?/引用类型）；`(c?.field = t)` 合法语句形与非法泛型值形态两盘；RHS 嵌套赋值与读侧 `?.` 链；
8. **非法形态确认编译器先拒（M0 探针归档 + 单测夹具）**：M12–M16 逐格先断言**原生编译诊断**（记录精确 CS 号），再断言本规则安全网码（0560/0561/0569）且不改写文件；
9. **E03 组合**：四象限（expression-desugar × null-coalesce）。E03 带 guard：`a?.b ??= c` 经 E03 节点保留 → C51 NA-6 正确；E03 无 guard（模拟旧版）：损坏形状 → 0569 Error；`a?.b ?? c`（读侧）仍归 C35 NC-6 不受影响；
10. **不可提升/防御**：字段初始化器白名单/非白名单两态、特性实参、默认参数、表达式树（0565）、dynamic（0559）、strict 开/关、行号路径附加；
11. **幂等/clean**：无空条件赋值文件 Changed=false 字节一致（含 BOM）；产物二次 Preprocess 零 diff；residual 语法探针（CA.WhenNotNull IsAssignment 计数）零误报（注释/字符串内 `?.` 文本不计）；
12. **.NET vs Java 行为预言机（.NET 半区）**：同一组探针用例在原生 C# 与预处理后中间树上各执行一次，逐值相等 + 副作用计数向量相等（至少六盘：接收者 null/非null × 值类型 lifted × 链中段 null × `??=` 三分支 × event 无订阅 × RHS 嵌套）。

### 7.2 项目级 fixture（追加 C35 的 `SampleNullConditionalSolution`）

新增 `NullConditionalAssignment/`：跨文件类的可写属性/索引器/event、`int?` lifted 值形态、`Dictionary?[k] += v`、record struct 条件赋值移交样本、ref 返回方法目标、RHS 为计数器方法。断言：`result.Success`；中间树不含「WhenNotNull 为赋值的 CA」（residual 口径）、含守卫 if 与 `__ed_s`；中间树 `dotnet build -c Release` + `dotnet test -c Release` 退出码 0；四象限组合与 C12（索引器方法化）开关矩阵全绿；strict 报错夹具不留半成品目录。

### 7.3 Java 半区与回归、CLI

- 预处理产物经完整转换跑 Java e2e：成员/索引/复合/`??=`/值形态五组运行时结果与副作用计数对齐 .NET 预言机；
- 默认关期现有护栏全绿（A1–A3 路径不动）：[ConditionalAccessMethodCasingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ConditionalAccessMethodCasingTests.cs)、[NullableTests.cs:25-31](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/NullableTests.cs#L25-L31) 保持；
- `PassStubCatalogTests` 计数不变；C23 的 ExpressionDesugarCLITests 扩用例：启用后无 assignment-terminal CA 残留、摘要行含 `assign=` 段、0569 失败退出非零；catalogItems 加宽的 `--list-passes` 快照同步。

### 7.4 验证命令（实施期执行；本设计任务未执行 build/test/git 写操作）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

## 8. 默认开启验收门槛

C51 不独立翻 pass，随 C35/expression-desugar 族门槛（[C35 §8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c35-null-conditional-design.md#L352-L362)）并附加：

1. **Roslyn 前提**：M0 证实当前包版本（或已评审升级后的版本）在 C# 14 解析选项下能正确解析全部合法形态；解析选项在 harness 与中间工程两侧统一；版本不支持时 0569 防线已就位；
2. §7.1 矩阵全绿，尤其四组计数探针（RHS、索引实参、接收者、get/set）六盘全对；fixture 与三测试工程 Release build/test 全绿；幂等零 diff；
3. residual 零「未解释空条件赋值」：每个 assignment-terminal CA 恰属于已归一（0555/0568）或登记保留（0562/0565）之一；
4. 四象限 × C12 × C17 组合（`dict?[k] += v` 经守卫→方法化→读改写全链）Java 半区 e2e 全绿；E03 guard 已合入或 0569 防御经评审接受为长期行为；
5. P3 低频项无独立门槛、无独立 sdk 扫描轮次：并入 C35 默认开前的 csharp-sdk 画像试跑，按命中数（预期极低）确认 0557/0561/0569 清单可接受；
6. C24/C36 未落地不阻塞（无 RuleSet/Options 新增行）。

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M0 ★先决探针（不翻任何注册项） | ① 4.12.0 在 Preview/CSharp14 下解析 F1–F11 的语法树外形钉死（含 WhenNotNull Assignment 字段、ElementBinding 左、ref-returning 调用左、链 M7/M8）；② 非法形态 M12–M16 精确 CS 诊断号归档；③ IOperation 外形（IConditionalAccessOperation.WhenNotNull=IAssignmentOperation）；④ 值类型两分支与静态成员 M17；⑤ 若不支持 → 输出包升级评审单（与 C52 联合），范围锁三个 CodeAnalysis 包齐升 | 探针 + 结论文档化，~150 行临时代码（不入库或入 tests 探针目录） |
| M1 简单成员/索引赋值 + RHS 守卫 | `Assignment/` 全部骨架；NA-1/2/5（成员/索引/值形态）+ 白名单内联 + 段临时；CS2J0555/0557/0559/0560/0561/0562/0566；RHS/索引实参/接收者三组计数探针；Core 单测主体 | Core ~350 行 |
| M2 复合/链/`??=`/组合收口 | NA-3/4/6/7/8（10 复合运算符+event、链 M7/M8、`??=` 与 E03 guard 对接、RHS 嵌套）；CS2J0564/0568/0569；fixture 追加与四象限、C12/C17 联合用例；Release build/test 全绿；Java 半区五组 e2e | ~300 行 + fixture |

M0–M2 全程 `DefaultEnabled=false`（随族）；每个里程碑自身 Release build/test 全绿，不留半残中间树。

## 10. 风险

| # | 风险（按严重度） | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **★RHS/索引实参被提前求值（最高发语义错误）**：把 RHS 提为守卫外临时、或在 Analyze 阶段提前渲染实参，都会让 null 接收者时发生可观察调用 | F6；任务书点名 | Emitter 节点原样置于块内/真分支，禁止 RHS 走 PendingTemp；§7.1-2/3 计数探针 null 盘零调用为硬验收；review 清单单列 |
| R2 | **语法树外形随预览版漂移**：F1 经规范定形，但 4.12.0 若仅部分支持，可能出现 token 挂位差异/skipped tokens | §1.4 | M0 先行钉死；Facts 单入口适配；不识别即 0569 不猜测；包升级评审与 C52 同批 |
| R3 | **E03 损坏 `?. ??=` 组合（A4）**：已在代码级证实访问路径会产出裸 MemberBinding 的 if/声明 | [NullCoalesceSyntaxRewriter.cs:133-218](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L133-L218) | guard 小 PR 列为 M1 合并前置（C35 已建提案）；C51 形状探测 0569 双保险；四象限测试锁 |
| R4 | **与 C12/C17 顺序假设破裂**：注册表未来重排（member-desugar 提前，见 C17 §3.3 同源议题）可能让方法化先于守卫 | §3.3/§3.4 论证依赖链序事实 | C51 两序不等价（C12 无法方法化 ElementBinding）——在 C35/C51 默认开评审中把「expression-desugar 先于 member-desugar」登记为硬链序约束，必要时补 RunAfter 边（届时单提案）；联合 fixture 守门 |
| R5 | **值类型接收者形态误判**：record struct 移交路径依赖 CA 节点在位（0562），普通 struct 误产 `.Value.X=` 即 CS1612 | F9；C35 R4 | 只取 SemanticModel 类型判定；禁发明 `.Value` 赋值；0561/0562 分流测试随 C35 联合 fixture |
| R6 | **链式目标的段序/嵌套块错误**：M7/M8 被任务书预设为非法，若按非法实现会拒绝合法 C# 14 代码 | F3 规范示例 | 本设计已按合法实现；M0 复核 `a?.b.c=d` 诊断；嵌套 if/三元模板 NA-4 与规范逐格对齐 |
| R7 | **值形态 lifted 类型错判**：三元 null 分支类型导致中间树不可编译或 Java 拆箱 NPE | F10；与 C35 R3 同源 | 类型只取 GetTypeInfo(ca)；`(T?)null` cast；引用类型不 cast；C22 组合回归 |
| R8 | **兄弟副作用顺序近似**：段临时前置到锚点前与同句前序副作用（其他实参）的次序差异 | 同 C35 R5/Q2（E03 同近似） | 次数为硬保证；顺序与 C35/E03 parity 并文档化；精确语句分裂列 Q2 不在 M1–M2 |
| R9 | **低频但静默**：P3 命中率低，画像可能零样本，默认开后才首遇 | catalog P3 定性 | 0569/residual 显式化兜底；并入 C35 画像轮；不新增独立门槛 |

## 11. 并行协调

| 相邻工作 | 边界与交接 |
|---|---|
| **C23 interpolation-expand（硬前置，锚点）** | C51 只消费族规则接口、`TempLifter`、文件上下文发号与适配器；不新增 RuleSet/Options 行；实施分支 base 上先合 C23 |
| **C35 null-conditional（硬前置，同目录）** | C51 是 C35 NC-7/CS2J0555 的实施落地与 `TerminalKind.Assign` 扩展；复用 ChainSegmentAnalyzer/Emitter/PendingAnchorReplacement/SyntaxFacts；对 C35 文件的修改限「只增字段/只增 partial 桶」；码段占用 0568/0569 需在 C35 码表登记锚点（§6）；C35 M3 若先占号则 C51 申请 0570 段；fixture 追加不新建 sln |
| **E03 null-coalesce（链上前驱）** | `a?.b ??= c` 的 guard 跳过补丁升级为 C51 M1 硬前置（§3.5）；lifter/类型不互相引用；0569 为未打补丁时防御 |
| **C17 increment-targets（numeric-boundary）** | C51 先：复合目标原样留守卫体，C17 后处理块内普通复合/自增展开；`a?.b++` 非法故两族目标集无交集；C17 既有 Conditional 指路（CS2J5339）保留为防御；用户定义复合赋值（C52②）经块内普通节点交 C17；联合 fixture 验证 get/set 次数 |
| **C12 indexer-desugar（member-desugar 族）** | C51 必须在先（语法面独占 ElementBinding 赋值识别）；C12 对守卫体内普通 ElementAccess 方法化，求值位置不变；开关四象限联合测试；未来链序重排需补硬约束（R4） |
| **C13 event-desugar / C11 property** | `c?.E += h` 守卫体内普通事件复合 → C13 后续方法化 add；属性 set/get 方法化同理；均无 `?.` 感知需求 |
| **E11/E13 record/make-readonly** | record struct 条件赋值 0562 保留 CA 节点移交（依赖节点在位，与 C35 §11 同口径）；副作用 RR051 行为不变；联合样本 `p?.X = 1/+= 1` |
| **C22 nullable-value** | 值形态 lifted `T?` 产物进入 C22 守卫规则；C51 不做 lifted 运算；0561 与 C22 边界沿用 C35 NC-7 |
| **C52 版本防线** | Roslyn 包升级（若 M0 需要）与 C52 同批评审、齐升；版本不支持时报码文案/口径与 C52 版本扫描一致；C#14 语法门禁共用一份「当前 Roslyn 支持的语法集」清单 |
| **Java 侧转换器（不同半区）** | A1–A3 兜底本期不改不动；默认开后随 C35 特判簇整体退役（AssignmentTransformer 的 MemberAccess 假设、ElementBinding TODO 锁定测试同批处理） |
| **热文件** | 无新增：RuleSet/Options/catalogItems 的追加已在 C35 PR 内预留（catalogItems 再改一字符串）；转换器侧零修改，仅记录 A1–A3 兜底点为退役清单 |

## 12. Worktree 分配（只写不执行；本设计任务未执行该命令、未执行任何 git 写操作）

- 分支：`feature/preproc-c51-null-cond-assignment`
- 目录：`.worktrees/preproc-c51-null-cond-assignment`
- base：`9b99c60b`（已校验存在）；**实施前先把 C23（`feature/preproc-c23-interpolation`）与 C35（`feature/preproc-c35-null-conditional`）分支合入本分支**——`ExpressionDesugar/` 族基建、`NullConditional/` 目录、ChainSegmentAnalyzer、PendingAnchorReplacement、CS2J0550-0569 码段与适配器均以二者落地树为前提；M0 探针建议在合入完成后的树上执行

```powershell
git worktree add .worktrees/preproc-c51-null-cond-assignment -b feature/preproc-c51-null-cond-assignment 9b99c60b
```

实施在该 worktree 内进行（合入 C23+C35、视 M0 结论合入 E03 guard 补丁后再开工）；本设计文档产出于主工作区，不创建 worktree、不执行 commit/add/checkout/reset/stash 等任何 git 写操作、未执行 dotnet build/test。

---

## 附：关键开放问题（实现前/默认开前需评审确认）

- **Q1 值形态的产物形态选择**：规范值形态 `__ed_s0 != null ? __ed_s0.b = c : (T?)null` 最省临时（NA-5 默认），但赋值表达式嵌三元可读性较差；备选是统一锚点块 + 结果临时（`T? __ed_r; if (...) __ed_r = __ed_s0.b = c; … return __ed_r;`，重度使用 PendingAnchorReplacement）。M1 按规范最简形实现，默认开前按画像可读性评审是否切换。
- **Q2 兄弟副作用精确顺序**：与 C35 Q2 同源——段临时前置相对同句前序实参的次序仅保 parity（E03 现状），不做语句分裂精确保序；确认 C51 不单独立项，挂 C35 Q2 统一决策。
- **Q3 值类型接收者终态**：维持「record struct 0562 移交 + 其余 0561」，还是在 M2 自降 `HasRequirements`/块临时回写收编普通 struct（C35 Q3 同议题）；按画像命中率与 E13 交互复杂度定，本规则不主动收编。
- **Q4 NA-6 内层临时命名与发号**：沿用 E03 风格 `___tN` 还是族内 `__ed_nN`，以及与 E03 序号空间的避让/统一时点（三套 lifter 上收 SingleEvaluationTempLifter 的共同技术债，见 C35 Q1②）。
- **Q5 Roslyn 升级范围**：M0 若判定 4.12.0 不支持，升级到的精确版本（.NET 10 GA 对应包）、是否同批纳入 C49/C50 等其他 C# 14 语法探针需求、以及 harness 解析选项统一（Preview vs CSharp14）的收口 PR 边界，需与 C52 联合评审。
