# cs2j Preprocessor 盘点清单（C# → C# 前置转换候选）

> 盘点日期：2026-09-28
> 盘点范围：仓库 `d:\code\cs2j`（commit 工作区现状）。
> 本文档**只盘点、不设计、不实现**：所有条目均给出代码库真实证据（路径 + 行号）。
> 「preprocessor」在本文的含义：主转换（C#→Java）之前运行的、产出**语义等价且可编译 C#** 的纯源码重写通道；验证手段是中间目录 `dotnet build -c Release` / `dotnet test -c Release` 全绿。
> 2026-09-28 二次补全：新增候选 **C29、C31–C36**（七项）；修正 §0.4 计数（原写 22，实际 29 项）与优先级归属（C10 明细行本为 P2，已归位）。
> 2026-09-28 三次补全：新增候选 **C37–C42**（六项：模式表达式去糖、异步流/异步释放、终结器、foreach 迭代源二期、特殊数值类型、平台边界隔离）；为 C03/C15/C16/C18/C36 补充交叉证据；§0.4 计数更新为 42 项；§4.12「静默错误输出」扩充排查面（新增 5 处兜底/丢弃站点）；新增 §4.15（模式测试红灯与实现脱节）、§4.16（extern 行为矛盾）。
> 2026-09-28 四次补全（方向性输入整合）：新增 **§5**（正确性两半区与 EF Core 测试预言机、CsCore 目标子集、分层判据、统一 pass 契约与编排、特性位剪枝、Layer 0–5×条目×sdk 实测频率全景、三笔架构债）；新增候选 **C43–C46**（词法字面量归一、using 显式化、lambda 闭包提升、成员签名显式化；前两项经代码核实**已被转换器内联吸收**，列观察项）；为 C04/C10/C23/C26/C28/C32/C36/C41 补充 sdk 频率或语义证据（含已核实的 LinqRewriter 无 IQueryable 分流、插值 handler 判定缺失）；§0.4 计数更新为 **46 项**；§4 新增风险 17–19。
> 2026-09-29 五次补全（语言覆盖审计输入）：以 **C# 1.0–14 全语法/语义特性清单**（另册《CSharp转Java预处理器Pass清单》，58 pass 设计基线）对本清单 E01–E17 + C01–C46 做**差集审计**，新增候选 **C47–C52**（六项：相等性与字符串拼接调用点物化、params 集合显式化、C# 14 扩展成员、C# 14 `field` 关键字、C# 14 简单空条件赋值、版本防线升格与低频构造收编）；为 C02/C06/C11/C19/C23/C28/C29/C35/C36 九个既有条目补五次补全追加注记；§0.4 计数更新为 **52 项**；§4 新增风险 20–21（训练集与 .NET 10 目标的语言版本轴错配、相等性核实双向结局）；§5.5 全景表补 C# 13/14 构造行。**本批条目由语言层审计发现而非仓库 grep——各条目仓库证据状态以「验证」行动核实为准**（对齐 §5「方向性输入」处理原则：与代码证据冲突时以证据为准）。
> 2026-09-29 六次补全（官方文档核对，learn.microsoft.com）：对 **12 个官方页全文精读核对**（member-access-operators / keywords/extension / keywords/field / keywords/params / equality-operators / addition-operator / collection-expressions / nullable-value-types / foreach-in / yield / statements/lock + whats-new/csharp-14）。**修正两处事实错误**：① C47 ⑥ 委托 `==` 官方语义为**调用列表值相等**而非「引用比较、Java 同形」——直映 Java `==` 必错值；② C# 14 extension 块支持**扩展运算符**（原文遗漏）、**扩展索引器实为 C# 15 预览**（原文误归 C# 14）。**补盘两项 C# 14 特性**：简单 lambda 参数修饰符（归 C45）、用户定义复合赋值运算符（归 C14/C17——`a += b` 不再必然拆解）。**新增遗漏子项**：record `==` 值相等（C47⑦）、[Conditional] 调用点条件编译（C10）、[SetsRequiredMembers]（C27）、checked 用户定义运算符（C# 11，C14）、readonly 实例成员（C# 8，C07）、集合表达式 `with(...)` 元素（C# 14，C36）、非绑定 `typeof(List<>)`（C52）、[ThreadStatic] 单线程初始化语义（C09）、[EnumeratorCancellation]/WithCancellation（C38 交界）。为 C04/C07/C09/C10/C14/C16/C20/C22/C27/C29/C31/C36/C40/C45/C47/C49/C51/C52 共 **18 个条目**追加「六次补全（官方文档核对）」行；为全部 **E01–E17 + C01–C52（69 条）**追加「官方文档」索引行（**已核**＝本批全文精读，其余为权威 URL 索引）；§4 风险 21 更新（语言层语义已由官方文档确证，剩余核实收敛为仓库内联覆盖面）。计数不变（52 项）。
> 2026-09-30 七次勘误（维护者确认 + 主干对齐，证据核对不改盘点结论）：①维护者确认失败/跳过红测为**有意手动删除**，验收件随各通道实现补回（恢复属各通道任务，不再视为矛盾）；②**C11–C14 已由维护者手动合并主干 dev-3.x**：member-desugar 为 Implemented 描述符（默认关），其 catalogItems 已对齐 `"C11, C12, C13, C14"`；③**修正 E01 两处错误描述**——`#nullable`/`#pragma`/`#line`/`#error`/`#warning` 等活动指令实际**保留**（`IsRemovedDirective` 白名单 L78-86；`#warning` 另附一条诊断，L38-45），剥除的只有条件指令族（含未选分支）与可选 `#region`；同步修正 §1.7 L80、C36 三次补全行的相反表述；④C30 代码考古收口：PatchClosed* 曾由 2694f975 引入、被 c8d6d31f 架构性删除（均为 HEAD 祖先），非「未合入」；下游仓持有状态仍待 M1 核实门；⑤C47 核实门结论落账：**B 档部分覆盖、需补**（string `==`/`bool?`/元组已内联；`(object)` 逃逸、拼接 null 语义、委托值相等、record `==` 四子面静默错值/不可编译）；⑥勘正陈旧引用：C34 TypeMappings 实为 L3633-3639、C38 ForeachDeconstructionTests 实为 L162-181、C39 ClassTransformer 实为 L316-380、C42 诊断消息措辞（3001/3201 用 "is not supported by the Java-only conversion pipeline"，仅 3101/3102 含 "must be isolated"）。
> 2026-09-30 八次更新（第一批完成态核实）：经提交链与注册表双向核实，**C01/C02/C03/C37 均已实现并合入 dev-3.x**（C01 止于 48331c9c、在链默认开；C02 止于 ecc1376e、Implemented 默认关；C03+C37 止于 15d534a1、Implemented 默认关，catalogItems `C03, C37`）——三个条目的「缺失/部分」状态行与 §3.1 路线图 1/2/4 同步更正为已完成（各 pass 默认开启评审/sdk 基线等挂起项见各状态行）；对应四份设计文档加「2026-09-30 再基线」完成态记录。挂起未做：C01 M5（sdk 全量比对、ROI 旧码下线）、C02 M4 默认开启评审与 sdk 计数门、两个新 pass 的默认翻转。
> 2026-09-30 九次更新（第二批完成态核实 + worktree 清理）：经提交链与注册表双向核实，**C04 与 C11–C14 均已实现并合入 dev-3.x**（C04 止于 b784b47f、Implemented 默认关；C11–C14 共用 member-desugar 族 pass、Implemented 默认关，分别止于 8f6032da/146fdda2/129cb473/2d25700e）——五个条目的「缺失」状态行、§3.1 路线图 7/12、§5.5 频率表 C04 行同步更正；注册表补齐 catalogItems 映射 `C01`（ref-out-in）与 `C04`（iterator-lowering，C11–C14 已带联合映射）并有测试锁定；五份设计文档加完成态记录。C01–C03/C11–C14 的 **7 个使命完成 worktree 及分支当日删除**（实现提交均已在主干）。仍挂起：C04 §7.2 项目级 fixture 与 §8 默认开启五门槛（try/finally 两半区原型、sdk 2887 站点基线、EF Core 画像）、member-desugar 默认翻转评审、C46 签名收口。
> 2026-09-30 十次更新（C08 完成态补登 + C47/C49 占位补盘）：**C08/E17 JavaKeywords 经核实已完整落地并接入统一链**（Core 7 文件 `JavaKeywords/` + CLI 适配器 `JavaKeywordsPassAdapter` + 门面，PassCatalog Implemented 默认关；单测/项目/CLI/SampleJavaKeywordSolution 齐备）——九次更新漏登，现补正 §0.1/§0.2/E17/§3.1。同时**注册表新增两个 Stub 占位**：`extension-members`（catalogItems C49，RunAfter extension-methods）与 `equality-concat`（catalogItems C47，RunAfter member-desugar）；java-keywords 的 RunAfter 同步锚定这两个占位，链尾测试继续成立。计数不变（52 项）。

---

## 0. 摘要

### 0.1 已接入 `convert-project` 的 preprocessor：15 个

当前在 `src/CSharpToJava.CLI/Program.cs` 的 `ConvertProject` 中按固定顺序串联（入口块行号见 §2.A）：

| # | 名称 | 中间目录 | 默认 | 状态 |
|---|------|----------|------|------|
| E01 | ConditionalDirectiveStrip（条件编译指令剥离） | `.cs2j-conditional-src` | 开 | 已实现 |
| E02 | LocalFunctionLift（局部函数提升） | `.cs2j-local-fn-src` | 开 | 已实现（允许 residual，strict 可控） |
| E03 | NullCoalesceRewriter（`??` / `??=`） | `.cs2j-null-coalesce-src` | 开 | 已实现 |
| E04 | EnumPreprocessor（enum 位运算/HasFlag） | `.cs2j-enum-src` | 开 | 部分（仅运算，**不处理 enum 声明**，见 C05） |
| E05 | DelegatePreprocessor（委托 `+/-/+=/-=`、匿名方法） | `.cs2j-delegate-src` | 开 | 已实现 |
| E06 | TupleToStruct（值元组→生成 struct） | `.cs2j-tuple-src` | 开 | 已实现 |
| E07 | ForeachDeconstruction（解构 foreach） | `.cs2j-foreach-deconstruction-src` | 开 | 已实现（仅 KeyValuePair / Deconstruct 族） |
| E08 | DefaultParameterStripper（默认参数→重载） | `.cs2j-strip-default-src` | 开 | 已实现 |
| E09 | UnsignedTypeRewriter（无符号类型类化） | `.cs2j-unsigned-src` | 开 | 已实现（配套 Cs2jUnsignedMath 支撑库） |
| E10 | GenericPreprocess（泛型 P0 三模式） | `.cs2j-generic-src` | 开 | 部分（`default(T)`/`new T()`/`new T[n]`；`typeof(T)`/`is/as T` 明确未做，见 C02） |
| E11 | RecordReadOnly（record→readonly record） | `.cs2j-record-readonly-src` | **关（opt-in）** | 已实现（plan/回滚最成熟范式） |
| E12 | ArrayMethodRewriter（数组方法/多维数组） | `.cs2j-array-src` | **关（opt-in）** | 已实现（配套 Cs2jArray 支撑库） |
| E13 | ReadOnlyStructMaker（struct 只读化） | `.cs2j-readonly-src` | 开 | 已实现 |
| E14 | GotoEliminator（goto/label→状态机） | `.cs2j-no-goto-src` | 开 | 已实现 |
| E17 | JavaKeywords（Java 保留字归一化、operator true/false 降级） | `.cs2j-java-keywords-src` | **关（opt-in）** | 已实现（2026-09-30 补登） |

### 0.2 未接入但已存在/已设计：1 个

- **E15 RefOutInPreprocessor —— 已接入统一链（catalog id `ref-out-in`，catalogItems C01，默认开）**：第八次更新已核实，Core 9 文件（`src/CSharpToJava.Core/RefOutInPreprocessor/`）经 CLI 适配器 `RefOutInPassAdapter` 接入，配套 Cs2jRefOutIn 支撑库注入与项目级测试齐备；M5（sdk 全量比对、旧码下线）仍挂起。
- **E16 LinqRewrite / LinqQueryDesugarer —— 已实现但属"嵌入式通道"**：它本身是 `CSharpSyntaxRewriter`，但运行在主转换 pass 内部（`ProjectPasses.cs:164,301,351`、`SingleFilePasses.cs:40,64,82`），没有独立 verb、没有中间目录与 build/test 验证。规模极大（`LinqRewriter.Rules.cs` 2156 行、`LinqRewriter.cs` 1624 行）。
- ~~**E17 JavaKeywords —— 设计已确认，完全未实施**~~：**2026-09-30 第十次更新核实已完整落地**，见 §0.1 E17 行；Core 目录 `src/CSharpToJava.Core/JavaKeywords/`（7 文件）、设计文档状态行已加完成态记录。

### 0.3 实验性 Java2 管线下的 11 个 Lowering pass

`src/CSharpToJava.Core/Lowering/`（`LowerRefOut/LowerYield/LowerUsing/LowerProperty/LowerIndexer/LowerOperator/LowerEvent/LowerDelegate/LowerStruct/LowerPatternMatch/VariableResolution`）仅在 `NewConversionPipeline.cs:27-33` 注册；全仓搜索该管线**只被** `tests/CSharpToJava.Tests/Java2/PipelineComparisonTests.cs` 使用，不在生产路径（生产用 `ProjectConversionPipeline` + `Transformers/` + `Java/`）。它们的价值是**证明了各特性的目标降级形态**，是前置 preprocessor 的需求与形态证据（「半成品规格书」），而非可复用实现。四次补全将该现状定性为架构债 A（§5.7）：规则应移植到 Roslyn rewriter，用 .NET 测试直接验证。

### 0.4 建议新增候选：52 个（含对既有通道的扩展）

| 优先级 | 数量 | 编号 |
|--------|------|------|
| P0（消除现存 fail-fast / 完成半截工程） | 3 | C01 RefOutIn 接线收尾、C02 Generic 深化、C03 控制流去糖（pattern switch / catch-when 等） |
| P1（转换器最厚的复杂度块 / **静默错误输出族**，已有明确信号） | 9 | C04 yield（sdk 2887 处，方向性输入建议按频率升 P0，见条目注记）、C05 enum 声明归一、C06 unsafe/FFM、C07 struct 值语义补全、C08 JavaKeywords 落地、C31 Index/Range 归一、**C37 模式表达式去糖（`is` 表达式/switch 臂/位置与列表模式，实测测试红灯）**、**C47 相等性与字符串拼接调用点物化（string `==`/`+` 的 null/值语义，先核实）**、**C49 C# 14 扩展成员（.NET 10 目标直接命中；先决＝Roslyn 包版本支持 C# 14 语法树）** |
| P2（成员/数值/并发等中等收益） | 20 | C09 ThreadStatic、C10 JSON/Attribute 归一、C11 Property、C12 Indexer、C13 Event、C14 Operator、C15 checked/unchecked、C16 foreach 补全、C17 自增目标归一、C18 async/await、C19 扩展方法显式化、C26 LINQ 独立化、C30 closed generic metadata 前置化、C32 Lambda 可变捕获前置化、C34 decimal 运算归一、**C38 异步流/异步释放归一**、**C40 foreach 迭代源二期（枚举器适配/ref foreach）**、**C45 Lambda 闭包提升（display class 全量形态，sdk lambda 4966 处）**、**C48 params 集合显式化（C# 13，并入 E08 族）**、**C50 C# 14 `field` 关键字（随 C11 同批）** |
| P3（推测性/收益小或被兼容层充分吸收） | 20 | C20 partial 合并前置、C21 匿名类型提升、C22 `Nullable<T>` 提升、C23 插值字符串展开、C24 初始化器归一、C25 dynamic、C27 record `with`/init、C28 指令/using trivia 清扫、C29 lock/Monitor 归一（观察项）、C33 泛型型变/约束前置审计、C35 空条件 `?.` 去糖、C36 C# 12 集合表达式去糖、**C39 终结器消除、C41 特殊数值类型（Half/Int128/UInt128/nint/IntPtr）、C42 平台/互操作边界前置隔离**、**C43 词法字面量归一（raw string/u8/verbatim，已内联吸收，观察项）、C44 using 显式化（同步形态已内联，观察项）、C46 成员签名显式化（显式接口实现/new 隐藏/协变返回收口）**、**C51 C# 14 简单空条件赋值 `a?.b = c`**、**C52 版本防线升格与低频构造收编（C# 13/14 + C# 8–12 补盘，C36 防线升格的伞条目）** |

---

## 1. 分类目录

### 1.1 类型系统类
E09 UnsignedTypeRewriter（已实现）、E10 GenericPreprocess（部分）、**C02 Generic 深化（typeof/is-as/静态泛型字段）**、E04/C05 enum（运算已做、声明缺口）、E06 TupleToStruct（已实现）、**C21 匿名类型提升**、**C25 dynamic**、**C22 `Nullable<T>` 提升**、**C33 泛型型变/约束前置审计（out/in 声明被丢弃、struct/new() 约束仅告警）**、**C41 特殊数值类型（Half/Int128/UInt128 无映射）**。

### 1.2 值语义 / 结构体类
E13 ReadOnlyStructMaker（已实现）、E11 RecordReadOnly（已实现、opt-in）、**C07 struct 值语义补全（防御性 clone）**、**C06 的 ref struct 子项**、E15/C01 ref-out-in（Core 已实现未接线）、实验管线 `LowerStruct.cs`。

### 1.3 控制流类
E14 GotoEliminator（已实现）、E02 LocalFunctionLift（已实现）、**C03 控制流去糖（pattern/非整型 switch、catch-when、catch T）**、**C37 模式表达式去糖（`is` 表达式/switch 表达式臂/位置与列表模式；C03 的表达式侧姊妹项）**、**C04 yield 迭代器降级（sdk 2887 处，频率最高的结构性缺口）**、E07/C16/C40 foreach（部分/补全/迭代源归一）、**C18 async/await**、**C38 异步流（`await foreach`/`await using`）**、**C44 using 显式化（同步形态已内联 try-with-resources，ref struct 边角观察项）**。

### 1.4 成员 / 语法糖类
E08 默认参数剥离（已实现）、E05 委托（已实现）、**C11 Property 去糖**、**C12 Indexer 去糖**、**C13 Event 去糖**、**C14 Operator 去糖**、**C17 复杂自增目标归一**、**C19 扩展方法显式化**、**C27 record `with`/init**、**C15 checked/unchecked**、**C10 attribute 归一**、**C32 Lambda 可变捕获前置化**、**C45 Lambda 闭包提升（display class 全量形态，C32 的超集）**、**C46 成员签名显式化（显式接口实现/new 隐藏/协变返回，建议并入成员去糖族）**、**C34 decimal 运算归一**、**C35 空条件 `?.` 去糖**、**C39 终结器消除（`~T()` 被静默丢弃）**、**C47 相等性与字符串拼接调用点物化（string `==`/`!=` 值相等、`+` 的 null-as-empty、tuple/Nullable 提升）**、**C48 params 集合显式化（C# 13 params collections + 调用点展开）**、**C49 C# 14 扩展成员（extension 块的扩展属性/扩展运算符/静态扩展；索引器为 C# 15 预览）**、**C50 C# 14 `field` 关键字（半自动属性）**、**C51 C# 14 简单空条件赋值 `a?.b = c`**。

### 1.5 异步 / 并发类
**C18 async/await**、**C38 异步流/异步释放（`await foreach`/`await using` 关键字被语句转换器静默忽略；`IAsyncEnumerable`/`IAsyncDisposable` 无 TypeMappings 映射且 compat 无对应类）**、**C09 `[ThreadStatic]`**（现由转换器内联处理）、**C29 lock/Monitor 归一（观察项）**：lock 直映 `synchronized`（`StatementTransformer.SwitchAndResource.cs:286-293`）、volatile 直映 Java volatile（`FieldTransformer.cs:104,410`）、Interlocked 由 compat `Interlocked.java`/`InterlockedHelper.java` 吸收——均为非失败型，但 `Monitor.TryEnter` 超时/`Pulse/Wait` 语义在直映下静默丢失，登记为 C29。

### 1.6 反射 / 序列化 / 属性类
**C10 JSON/Attribute 归一**（`[JsonConverter]`/`[JsonPropertyName]` 等）、**C30 closed generic type metadata**（ROADMAP #10）、C02 的 `typeof(T)` 子项；compat 桥接见 `java/csharptojava-compat/.../Activator.java`、`TypeHelper`（设计文档引用）、`FieldInfo/MethodInfo/ConstructorInfo.java`、`JsonSerializer*.java`、`CompatJson*.java`、`DefaultContractResolver.java`。

### 1.7 编译指令 / 资源类
E01 ConditionalDirectiveStrip（已实现；剥 `#if` 族条件指令（含未选分支）+ 可选 `#region`；`#pragma`/`#nullable`/`#line`/`#error`/`#warning` 等活动指令**保留**，见 `ConditionalDirectiveStripper.cs:75-88`）、**C28 其余指令/using trivia 清扫（`#line`/`#error`/global using 别名/file-scoped namespace/extern alias）**、**C43 词法层字面量归一（raw string/u8/verbatim，sdk raw string 1345 处；已内联吸收，仅 CsCore 收敛观察项）**、**C20 partial 类型合并前置**。
注：`.resx → .properties` 属资源管线（`Program.cs:965-982`），不是 C#→C# preprocessor，不列入候选。

### 1.8 其他
**C06 unsafe/fixed/pointer/stackalloc/Span**、E12 ArrayMethodRewriter（已实现、opt-in；多维数组/Resize/Rank）、**C31 Index/Range（`^`/`..`/System.Index/System.Range，含 string 切片静默错误输出）**、**C23 插值字符串**、**C24 对象/集合初始化器**、**C36 C# 12 集合表达式/spread 去糖**、**C42 平台/互操作边界前置隔离（CS2J3001/3101/3102/3201-3203 诊断已存在，隔离通道缺失）**、nameof（转换器已处理，见 `InvocationExpressionTransformer.cs:37-88`，仅在 E17 改名时需联动字面量化）、E16/C26 LINQ、E17 Java 保留字、**C52 版本防线升格与低频构造收编（C# 13/14 + C# 8–12 补盘的伞条目：`allows ref struct`、`System.Threading.Lock`、隐式 Span 转换、unbound `nameof`、`>>>`、NRT `T?` 注解、`file` 类型、DIM 菱形、`delegate*` 等）**。

---

## 2. 候选条目详表

### 2.A 现存 preprocessor 现状（家底）

#### E01 ConditionalDirectiveStrip
| 字段 | 内容 |
|------|------|
| 分类 | 1.7 编译指令 |
| 目标模式 | `#if/#elif/#else/#endif` 按符号求值后剥除（条件分支默认剥 L87）；`#region` 受 `RemoveRegions` 开关控制（L77）；活动指令白名单 L78-86 **保留**：`#line`、`#pragma warning/checksum`、`#nullable`、`#error`、`#warning`、`#reference`、`#load`、`#!`（其中 `#warning` 保留并附一条 Warning 诊断，L38-45；`#error` 在 parse 期即 CS 错误，整文件中止） |
| 证据 | Core `ConditionalDirectiveStrip/ConditionalDirectiveStripper.cs:8-56,75-107`；CLI `ProjectConditionalDirectivePreprocessor.cs:35`（`.cs2j-conditional-src`）；接线 `Program.cs:174-206`；选项 `no-strip-conditional`（`Program.cs:4744-4746`）；verb `strip-conditional`（`Program.cs:32,44`，`StripConditionalCommand.cs`） |
| 状态 | 已实现，默认开。预处理链**第一环**（必须最先跑，后续所有语义分析都基于存活分支） |
| 验证 | 项目级测试 `ConditionalDirectiveStripTests.cs`、`StripConditionalCliTests.cs`、`DebugConditionalExclusionTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/preprocessor-directives（指令语义；C# 14 文件级 `#:` 指令见 whats-new/csharp-14） |

#### E02 LocalFunctionLift
| 字段 | 内容 |
|------|------|
| 分类 | 1.3 控制流 |
| 目标模式 | 局部函数（含捕获 by-value/by-ref、静态局部函数、嵌套）提升为类级 private 方法 |
| 证据 | Core `LocalFunctionLift/`（10 文件，Analyzer/Plan/Naming/Rewriter）；统计 `LocalFunctionsLifted/LocalFunctionsResidual/CapturesByValue/CapturesByRef`（`LocalFunctionLiftStatistics.cs:8-12`）；接线 `Program.cs:208-240`；CLI `ProjectLocalFunctionLiftPreprocessor.cs:36`；设计 `docs/superpowers/specs/2026-09-06-local-function-lift-design.md` |
| 与现有通道关系 | 转换器内仍保留残余路径（`StatementTransformer.LocalFunctions.cs` 全文 321 行、`MethodTransformer.cs:37-46`），preprocessor 未覆盖时兜底；preprocessor 失败可由 `--no-local-function-strict` 降级为 warning（`Program.cs:4760-4762`） |
| 状态 | 已实现，默认开；存在 residual 出口（严格模式闸门） |
| 验证 | `LocalFunctionLiftTests.cs`、`LocalFunctionHoistingTests.cs`、`HoistedLocalFunctionCaptureScopeTests.cs`、`GenericLocalFunctionRenamedCallSiteTests.cs` 等十余项 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/local-functions |

#### E03 NullCoalesceRewriter
| 字段 | 内容 |
|------|------|
| 分类 | 1.4 语法糖（空合并） |
| 目标模式 | `??` → 显式三元；`??=` → if-赋值；复杂被提升表达式提临时变量；两遍（改写 + 临时变量提升） |
| 证据 | Core `NullCoalesceRewriter/`（`NullCoalesceRewriter.cs:35-45`，统计 L8-13）；接线 `Program.cs:242-278`；设计 `docs/superpowers/specs/2026-09-26-null-coalesce-preprocessor-design.md` |
| 状态 | 已实现，默认开 |
| 验证 | `NullCoalesceRewriterUnitTests.cs`、`NullCoalesceAssignmentParensTests.cs`、`NullCoalesceWithAsArrayTests.cs`、`CoalesceArgumentTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/null-coalescing-operator |

#### E04 EnumPreprocessor（注意：仅"运算"层）
| 字段 | 内容 |
|------|------|
| 分类 | 1.1 类型系统（enum） |
| 目标模式 | enum 上的 `\|/&/^/~`、复合赋值、`HasFlag`、可空 enum 提升 → 底层整数显式运算 + `(E)` 强转（窄底层类型包 `unchecked`） |
| 证据 | `EnumPreprocessor/EnumPreprocessor.cs:8-13`；`EnumSyntaxRewriter.cs:81-111,208,279-371`（全部围绕运算，**无 EnumDeclaration 改写**）；统计 `EnumPreprocessorStatistics.cs:9-17`；接线 `Program.cs:280-319` |
| 缺口 | `[Flags]` enum、显式底层类型、显式成员值三类**声明**仍被转换器直接拒绝（`Transformers/Type/EnumTransformer.cs:29-45`），见 **C05**；`++/--` enum 目标仍 fail-fast（`UnaryExpressionTransformer.cs:181-182`），见 C17 |
| 状态 | 已实现（窄），默认开 |
| 验证 | `EnumPreprocessorUnitTests.cs`、`ProjectEnumPreprocessorTests.cs`、`ByteEnumArithmeticWrapTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/enum；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/bitwise-and-shift-operators |

#### E05 DelegatePreprocessor
| 字段 | 内容 |
|------|------|
| 分类 | 1.4 成员/语法糖 |
| 目标模式 | 委托 `d1 + d2`/`d1 - d2`/`+=`/`-=` → `Cs2jPrelude.CsPrelude.Combine/Remove`（`DelegateSyntaxRewriter.cs:122,132`，支撑库 `src/Cs2jPrelude/CsPrelude.cs`，经 csproj 嵌入 `CSharpToJava.CLI.csproj:37-38`）；匿名方法 `delegate(...) {}` → lambda |
| 证据 | `DelegatePreprocessor/DelegatePreprocessor.cs:7-11`；统计 `DelegatePreprocessorStatistics.cs:8-13`；接线 `Program.cs:321-359`；设计 `docs/superpowers/specs/2026-09-26-delegate-preprocessor-design.md` |
| 与现有通道关系 | 只做委托组合/匿名方法；**event** 的 `+=/-=` 与字段式事件仍由 `EventFieldTransformer.cs`（447 行）处理 → C13；delegate 类型→函数式接口仍由 `Type/DelegateTransformer.cs` + 实验 `LowerDelegate.cs` 负责 |
| 状态 | 已实现，默认开；有 `SkippedUnsupported/ResidualCount` 残余统计 |
| 验证 | `DelegatePreprocessorUnitTests.cs`、`ProjectDelegatePreprocessorTests.cs`、`AnonymousMethodParameterTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/delegates-overview；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/addition-operator（委托组合 +/=，已核）；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/equality-operators（委托相等，已核） |

#### E06 TupleToStruct
| 字段 | 内容 |
|------|------|
| 分类 | 1.1 类型系统（tuple） |
| 目标模式 | 元组类型/字面量/命名元素/`ValueTuple` 工厂 → 生成的 `Cs2jTuples.*` readonly struct；解构声明/赋值/`out var`/foreach 解构展开；跨文件形状去重生成单一 struct 文件 |
| 证据 | `TupleToStruct/TupleToStructPreprocessor.cs:9-19`；`TupleShapeRegistry.cs`、`TupleStructFileContent.cs`；接线 `Program.cs:361-404`（注释 L363-364 明确「放在 default 参数剥离之前」）；默认命名空间 `Cs2jTuples`（`Program.cs:4752-4754`） |
| 状态 | 已实现，默认开 |
| 验证 | `TupleToStructProjectTests.cs`、`TupleToStructRewriterUnitTests.cs`、`MutableTupleAssignmentTests.cs`、`TupleDeconstructionDiscardTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/value-tuples |

#### E07 ForeachDeconstruction
| 字段 | 内容 |
|------|------|
| 分类 | 1.3 控制流 |
| 目标模式 | 解构 foreach：KeyValuePair 族（含实现 `IEnumerable<KeyValuePair<,>>`）与自定义 `Deconstruct` 族 → 普通 foreach + 显式局部声明 |
| 证据 | `ForeachDeconstruction/ForeachDeconstructionPreprocessor.cs:8-13,31-41`；族判定 `ForeachDeconstructionRewriter.cs:61-68,112-122`（Tuple 族显式 skip 并提示「run TupleToStruct first」——顺序依赖证据）；接线 `Program.cs:406-441` |
| 缺口 | 字符串 foreach 仍 fail-fast（`StatementTransformer.Loops.cs:121`）；非 KV/Deconstruct 的解构 foreach 残余 → **C16** |
| 状态 | 已实现，默认开；必须排在 E06 之后 |
| 验证 | `ForeachDeconstructionTests.cs`、`ProjectForeachDeconstructionPreprocessorTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/functional/deconstruct |

#### E08 DefaultParameterStripper
| 字段 | 内容 |
|------|------|
| 分类 | 1.4 成员/语法糖 |
| 目标模式 | 剥离方法默认参数并生成重载（两遍：Collect 建签名目录 → Strip + 改调用点具名实参）；跳过无 body/冲突/隐藏 abstract 等 |
| 证据 | `DefaultParameterStripper/DefaultParameterStripper.cs:12-35`；统计 `DefaultParameterStripperStatistics.cs:8-15`；接线 `Program.cs:443-477`；设计 `docs/superpowers/specs/2026-09-06-strip-default-parameter-design.md` |
| 状态 | 已实现，默认开；转换器侧另有 `Transformers/Utilities/DefaultParameterHelper.cs` 兜底 |
| 验证 | `DefaultParameterStripperTests.cs`、`DefaultParameterCatalogTests.cs`、`DefaultParameterOverloadBuilderTests.cs`、`StripDefaultProjectTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/named-and-optional-arguments；https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-12（primary constructor 吸收面） |

#### E09 UnsignedTypeRewriter
| 字段 | 内容 |
|------|------|
| 分类 | 1.1 类型系统（无符号） |
| 目标模式 | `byte/ushort/uint/ulong` 关键字 → `System.Byte/UInt16/UInt32/UInt64`（映射 compat `CSharpByte/CSharpUInt16/...`）；运算符→扩展方法（注入 Cs2jUnsignedMath 支撑库工程）；cast/数组/泛型实参/字面量/const/enum/后缀自增全覆盖 |
| 证据 | Core `UnsignedTypeRewriter/`（11 文件，含 `UnsignedPostfixRewriter.cs`、`UnsignedMathSyntax.cs`、`UnsignedLiteralSyntax.cs`）；统计 `UnsignedTypeRewriterStatistics.cs:8-13`；接线 `Program.cs:479-512`；支撑库过滤 `Program.cs:750-756`；背景 `prompts/unsigned-type-rewriter-prompt.md:5`（双重映射冲突）、`config/TypeMappings.json:24-46`（有损映射）；设计 `docs/superpowers/specs/2026-08-15-unsigned-math-helper-design.md`、`2026-09-24-unsigned-math-lib-project-design.md` |
| 状态 | 已实现，默认开。是「注入支撑库工程 + 过滤不转换」范式的样板（与 E12 同） |
| 验证 | `UnsignedTypeRewriterUnitTests.cs`、`UnsignedTypeProjectTests.cs`、`UnsignedConversionEndToEndTests.cs`、`tests/CSharpToJava.Tests/Cs2jUnsigned/*` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types |

#### E10 GenericPreprocess（部分实现）
| 字段 | 内容 |
|------|------|
| 分类 | 1.1 类型系统（泛型） |
| 目标模式（已做） | `default(T)` → `default` 字面量（歧义处包 `(object)`）；`new T()` → `Activator.CreateInstance<T>()`；`new T[n]` → `(T[])Array.CreateInstance(typeof(T), n)`；并为泛型 override 兄弟方法注入 `__cs2j_typeof_T` 哨兵以统一 Class token 注入 |
| 证据 | 类注释即覆盖清单：`GenericPreprocess/GenericPreprocessor.cs:8-24`（L18 明写「typeof(T) / is T / as T 暂不降级（仅统计）」）；override 同步 `GenericPreprocessor.cs:69-100`；接线 `Program.cs:514-551`；prompt `prompts/generic-preprocess-prompt.md:5-19` |
| 缺口 | 见 **C02**（`typeof(T)/is/as T`、泛型静态字段、closed metadata） |
| 状态 | 部分实现，默认开 |
| 验证 | `GenericPreprocessTests.cs`、`ProjectGenericPreprocessorTests.cs`、`GenericNewConstraintInliningTests.cs`、`DefaultValueProjectTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/types/generics（静态成员逐封闭类型实例化） |

#### E11 RecordReadOnly（opt-in）
| 字段 | 内容 |
|------|------|
| 分类 | 1.2 值语义（record） |
| 目标模式 | 可变 record class/struct → readonly record；三阶段（分析→计划→逐文件重写）+ plan 级原子回退 + 整树重编译基线比对；含接口 ripple、变异点改写、方法迁移 |
| 证据 | Core `RecordReadOnly/`（11 文件；`RecordReadOnlyPreprocessor.cs:9-26`、`RecordReadOnlyAnalyzer.cs` 887 行、`MutationSiteRewriter.cs` 985 行、`InterfaceRippler.cs` 530 行）；接线 `Program.cs:553-593`；开关默认 false（`Program.cs:4783-4785`）；overrides `config/RecordReadOnlyOverrides.json`；设计 `docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md` |
| 与现有通道关系 | 与 E13 同属值语义链；转换器 `Type/RecordTransformer.cs`（513 行，决策分支 L25-59）仍承担 record struct/abstract record/额外成员等 fallback |
| 状态 | 已实现，**默认关**；是 E17 JavaKeywords 明确要复刻的架构样板（JavaKeywords spec §1） |
| 验证 | `RecordReadOnlyProjectTests.cs`、`RecordReadOnlyCLITests.cs`、`RecordReadOnlyRewriterUnitTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/record（record == 值相等/with=clone+init，已核） |

#### E12 ArrayMethodRewriter（opt-in）
| 字段 | 内容 |
|------|------|
| 分类 | 1.8 其他（数组） |
| 目标模式 | 数组实例方法/静态调用、`Array.Resize`、多维数组 Rank/length、数组 foreach 展平等 → `Cs2jArray.ArrayHelper.XXX(...)`（注入 Cs2jArray 支撑工程） |
| 证据 | Core `ArrayMethodRewriter/`（7 文件；入口注释 `ArrayMethodRewriter.cs:8-13`；统计 `ArrayMethodRewriterStatistics.cs:8-13`）；接线 `Program.cs:595-634`；开关默认 false（`Program.cs:4791-4793`）；支撑库过滤 `Program.cs:755`；设计 `docs/superpowers/specs/2026-09-25-array-helper-preprocessor-design.md` |
| 状态 | 已实现，**默认关** |
| 验证 | `ArrayMethodRewriterUnitTests.cs`、`ArrayMethodProjectTests.cs`、`ArrayMethodConversionEndToEndTests.cs`、`ArrayHelperSupportFilesTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/arrays/ |

#### E13 ReadOnlyStructMaker
| 字段 | 内容 |
|------|------|
| 分类 | 1.2 值语义（struct） |
| 目标模式 | 可变 struct → readonly struct（装箱可达性分析、构造器生成/归一化、变异点改写、调用点更新、方法迁移），为 Java Value Class（JEP 401）映射铺路 |
| 证据 | Core `ReadOnlyStructMaker/`（13 文件；`ReadOnlyStructRewriter.cs` 1688 行、`StructAnalyzer.cs` 977 行、`CallSiteUpdater.cs` 691 行、`BoxingReachabilityAnalyzer.cs`）；CLI `ProjectReadOnlyStructPreprocessor.cs:49`（`.cs2j-readonly-src`）并写 `.cs2j-iface-members.json` 元数据（L1702-1707）；接线 `Program.cs:636-677`（注释 L638「runs BEFORE goto elimination」）；overrides `config/ReadOnlyStructOverrides.json`；VerifyBuild/VerifyTests 范式出处 |
| 与现有通道关系 | 与 C07（可变 struct clone）互补；转换器 `Type/StructTransformer.cs:14-19,60-67` 明确「值语义差异由 C#→C# 预处理器消除，只做一一映射」，ref struct 直接拒绝（L60-64） |
| 状态 | 已实现，默认开 |
| 验证 | `ReadOnlyStructMakerTests.cs`、`ReadOnlyStructMakerL4/L5Tests.cs`、`ReadOnlyStructPreprocessorVerifyTests.cs`、`ReadOnlyStructPreprocessorCallSiteTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/struct（readonly struct 节） |

#### E14 GotoEliminator
| 字段 | 内容 |
|------|------|
| 分类 | 1.3 控制流 |
| 目标模式 | `goto/label`、`goto case/default`（Pass1 去糖 `GotoCaseDesugarer` → Pass2 `StateMachineBuilder` 2076 行状态机）→ 无 goto C# |
| 证据 | `GotoEliminator/GotoEliminator.cs:8-39`；CLI `ProjectGotoPreprocessor.cs:44`；接线 `Program.cs:679-716`（预处理链最后一环，`UsingGotoPreprocessedSource` 标记 L715）；设计 `docs/superpowers/specs/2026-06-27-goto-eliminator-design.md`、`2026-06-03-goto-conversion-redesign.md` |
| 状态 | 已实现，默认开 |
| 验证 | `GotoEliminatorTests.cs`、`ProjectGotoPreprocessorTests.cs`、`ProjectGotoPreprocessorRestoreTests.cs`、`ConvertProjectGotoIntegrationTests.cs`、`ContinueInSwitchGotoCaseTests.cs` |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/goto |

#### E15 RefOutInPreprocessor（未接线的半截工程）
| 字段 | 内容 |
|------|------|
| 分类 | 1.2 值语义（ref/out/in） |
| 目标模式 | 统一 ByRef 位置模型：内部 ref/out/in 参数、ref 局部/ref 返回/ref 字段/委托 ref 参数/ref struct holder/数组元素代理；外部元数据符号与 extern/接口虚实现**锁定不改**（设计 D1/D6） |
| 证据 | Core `RefOutInPreprocessor/` 9 文件；统计 19 项（`RefOutInRewriterStatistics.cs:5-23`：RefParams/OutParams/RefReturns/RefLocals/RefFields/ArrayElementProxies/RefStructParams/CallSitesLifted/HoldersInjected/AccessorProxies/LockedContractMembers…）；入口 `RefOutInRewriter.cs:8-74`；设计 `docs/superpowers/specs/2026-09-28-ref-out-in-preprocessor-design.md:1-49`（状态「已评审」；D3「convert-project 默认开启；Java 产物暂不可编译为已知中间状态，Java 映射另开后续 spec」；L15 明说不依赖实验管线 `LowerRefOut.cs`） |
| 现状反差 | **无 CLI 包装、Program.cs 无接线**；全仓仅 `RefOutInRewriterUnitTests.cs` 引用。生产仍走 Java 侧 holder：`Transformers/Expression/Transformers/ArgumentTransformer.Ref.cs` + `HolderTypeResolver.cs`，项目级断言仍是 IntHolder/ObjectHolder（`tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs:24-40`）。转换器仍对 ref local（`StatementTransformer.Declarations.cs:30`）、lambda ref 参数（`LambdaTransformer.cs:99`）、using/foreach/循环条件中的 ref read-back（`SwitchAndResource.cs:236`、`Loops.cs:139,208`）fail-fast |
| 状态 | **部分实现**（Core + 单测；CLI 集成与 Java 映射缺口） |
| 验证 | 已具单测；接线后按中间目录 `dotnet build/test -c Release` + 设计 D5 的 csharp-sdk 32 项目基线比对 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/method-parameters（in/ref/out，已核） |

#### E16 LinqRewrite（嵌入式 C# 重写通道，非独立 preprocessor）
| 字段 | 内容 |
|------|------|
| 分类 | 1.8 LINQ |
| 目标模式 | 查询表达式去糖（`LinqQueryDesugarer`）+ 方法链 LINQ → 过程式 foreach（`LinqRewriter`，源自 roslyn-linq-rewrite，见文件头版权 `LinqRewriter.cs:1-21`）；Stream 偏好下旁路 |
| 证据 | `LinqRewrite/LinqRewriter.Rules.cs`（2156 行）、`LinqRewriter.cs`（1624 行）；调用点 `Pipeline/Passes/ProjectPasses.cs:164,301,351` 与 `SingleFilePasses.cs:40,64,82`；门控 `ConversionOptions.EnableLinqRewrite`（默认 true，`Context/ConversionOptions.cs:54`）+ `--no-linq-rewrite`（`Program.cs:4690-4691`） |
| 与 preprocessor 范式的差距 | 无独立 verb、无 `.cs2j-*-src` 中间树、不做 `dotnet build/test` 等价性验证；产物直接喂给主转换器。提升为独立通道的候选见 **C26** |
| 状态 | 已实现（嵌入式） |
| 验证 | `LinqProceduralPhase1..10Tests.cs`、`LinqChainRefactoringTests.cs`、`LinqQueryDesugarTests.cs`、`LinqObservabilityPhase10Tests.cs` 等数十项 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/linq/query-expression-overview（query→method 官方映射） |

#### E17 JavaKeywords（已实现，2026-09-30 补登）
| 字段 | 内容 |
|------|------|
| 分类 | 1.8 其他（命名安全） |
| 目标模式 | ① 全部符号种类中派生为 Java 保留字的标识符（含 `true/false/null/_/yield`、`@event`、PascalCase `Assert` 等）统一改名 `XxxValue` + 碰撞消歧 + `nameof` 字面量化（规则 JK-0~JK-4）；② `operator true/false` 声明与全部布尔使用点（`if/?:/when/&&/||`）编译器同构降级为 `IsTrue/IsFalse`（JK-5~JK-8） |
| 证据 | 完整设计 `docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md`；**实现已落地**：Core `src/CSharpToJava.Core/JavaKeywords/`（SyntaxFacts、KeywordRenamingRewriter、OperatorTrueFalseRewriter、options/statistics/diagnostics 等 7 文件）、CLI 门面 `ProjectJavaReservedWordPreprocessor` + 适配器 `JavaKeywordsPassAdapter`，统一链 Implemented（默认关）；转换器侧历史兜底 `Context/JavaNaming.cs:132-165` 等仍在 |
| 状态 | **已实现（默认关；默认开启收口随当日统一翻转）**；即 **C08**。九次更新漏登，第十次补正 |
| 验证 | 已建：`JavaReservedWord*Tests`（单测）、`JavaKeywordProjectTests`（项目级 SampleJavaKeywordSolution）、`JavaKeywordCLITests`、PassChainOrderTests；默认开启验证见任务一 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/（C# 关键字索引；Java 保留字以 docs.oracle.com JLS §3.9 为准） |

---

### 2.B 建议新增候选

#### C01 RefOutIn 接线收尾（P0）
| 字段 | 内容 |
|------|------|
| 名称 | `ProjectRefOutInPreprocessor`（复用 Core `RefOutInPreprocessor/`） |
| 分类 | 1.2 值语义 |
| 目标 C# 模式 | 内部 ref/out/in 全套（参数/局部/返回/字段/数组元素代理/ref struct holder）；锁定外部符号与 extern/接口虚契约 |
| 解决的转换难点 | 消除 `ArgumentTransformer.Ref.cs` + `HolderTypeResolver.cs` 的字符串拼接式 holder 降低，以及 5 处 fail-fast：`StatementTransformer.Declarations.cs:30`、`LambdaTransformer.cs:99,200`、`SwitchAndResource.cs:236`、`Loops.cs:139,208`；实验 `Lowering/LowerRefOut.cs` 随之彻底无意义 |
| 证据 | Core 9 文件 + 设计文档已评审（见 E15）；ROADMAP #3 |
| 与现有通道关系 | 依赖：应在 E06 TupleToStruct、E04 Enum、E05 Delegate、E09 Unsigned 之后（这些会引入新调用形态）；建议先于 E13 ReadOnlyStruct（holder struct 与只读化有交互，需原型确认）；**必须完成设计 D3 遗留的 Java 映射 spec**（ByRef 在 Java 侧如何落地）才能默认开启 |
| 优先级 | **P0**（半截工程，完成边际成本最低、fail-fast 面最广） |
| 状态 | **完成（2026-09-29）**：C# 语句级提升闭环（中间树 build/test 全绿、verify 默认开启）+ Java 侧 compat ByRef/RefStructByRef 层次与 TypeMappings J1-J4/J8 已接线；ByRefPath/LegacyHolderPath 测试分流（§7.4）。sdk「新增失败为零」基线比对与 ROI 旧码下线属 M5 后续 |
| 验证 | 中间树 build/test 全绿；`RefOutInRewriterUnitTests.cs` 已在；新增项目级 fixture 与 csharp-sdk 32 项目「新增失败为零」比对（设计 D5） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/method-parameters（已核）；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/ref |

#### C02 Generic 深化：typeof(T)/is-as T/静态泛型字段（P0）
| 字段 | 内容 |
|------|------|
| 名称 | `GenericPreprocess` 扩展（不新建通道） |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | `typeof(T)` 显式 Class token 化、`is T`/`as T`/类型参数 catch、引用封闭类型参数的**静态字段**（Java 静态成员不得引用类类型参数）、嵌套/递归泛型中的类型实参传播 |
| 解决的转换难点 | 转换器最厚的特判簇：Class token 注入/传播（`StaticFieldRuntimeClassTokenTests.cs`、`TrailingClassTokenCallSiteTests.cs`、`GenericMethodInferredTypeArgClassTokenTests.cs` 等 20+ 测试）、静态泛型字段硬拒绝（`FieldTransformer.cs:112-119`）、擦除冲突（`SameTypeErasureConflictTests.cs`、`CrossInheritanceErasureTests.cs`）；ROADMAP #5/#10 |
| 证据 | `GenericPreprocessor.cs:18`（明确未做）；prompt `prompts/generic-preprocess-prompt.md:7-15,49`；静态字段 fail-fast `FieldTransformer.cs:115-118`；closed metadata 设计 `docs/superpowers/specs/2026-09-22-closed-type-metadata-chain-design.md` |
| 与现有通道关系 | 是 E10 的直接续作；与 C30（closed metadata）互为表里——C02 在 C# 侧显式化 token，C30 在 Java 反射侧消费闭型；必须在 E09 Unsigned 之后（无符号类化改变泛型实参种类） |
| 优先级 | **P0** |
| 状态 | **已实现（2026-09-28/29，2026-09-30 核实）**：`generic-deepen` 已翻转为 Implemented（**默认关**，RunAfter=`generic`，catalogItems `C02`，PassCatalog.cs L167-178）；实现位于 `GenericPreprocess/GenericDeepen*`（10 文件）+ `GenericDeepenPassAdapter`，含 typeof(T)/is-as T/GD-FIELD 静态字段 holder 化、residual 闸门与 SampleGenericDeepenSolution 端到端；提交链 14fed211→db9bbd08→0c969001→ecc1376e。**M4 默认开启评审与 sdk 计数门仍挂起** |
| 验证 | 中间树 build/test；fixture 复用 `TypeOfGenericTests`、`StaticGenericFieldTests`、`NestedGenericMappingTests`、`CrossProjectGenericClassTokenTests` 的 C# 形态 |
| 五次补全追加 | 泛型静态字段的**逐封闭类型实例化语义**：C# 中 `C<int>` 与 `C<string>` 的静态字段互相独立，Java 擦除后共享——现条目只覆盖「引用 T 的静态字段」硬拒绝；**不引用 T 的静态字段**（如 `class C<T> { static int s_count; }`）是静默语义差，被计数器/缓存统计类代码依赖时错值。归入本通道审计子项：改写为 keyed 形态（`static readonly Dictionary<Type, …>`）或登记拒绝（见 C52 ③） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/types/generics；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/type-testing-and-cast（typeof/is/as） |

#### C03 控制流去糖：pattern switch / 非整型 switch / catch-when（P0）
| 字段 | 内容 |
|------|------|
| 名称 | `ControlFlowDesugar`（建议单通道多规则，参考 EnumPreprocessor 组织） |
| 分类 | 1.3 控制流 |
| 目标 C# 模式 | ① 带模式标签/`when` 的 switch 语句 → if-else 链；② 非整型（bool/long/float/double/decimal/string/枚举）governing 的 switch → if-链；③ `catch (T) when (…)` → catch + if/rethrow；④ 对类型参数的 catch；⑤ 字符串 foreach（可并入本通道或 C16） |
| 解决的转换难点 | 全部 5 类今天都是**转换即抛异常**：`StatementTransformer.SwitchAndResource.cs:25,40,60,144,155`；`Loops.cs:121`。switch *表达式*侧已自行降三元链（`ControlFlowTransformer.cs:449-507`），但 L505 对未知模式留 `/* TODO ... */ true`，statement 侧无对应能力 |
| 证据 | 同上行号；模式测试需求 `ConversionTests/Categories/PatternMatchingTests.cs`、`PatternBulkTests.cs`、`SwitchCaseWhenClausePrecedenceTests.cs`、`CatchFilterRethrowTests.cs` |
| 与现有通道关系 | 独立但建议排在 E14 Goto 之前（goto case 去糖与 switch 去糖会争抢同一语法面——实际应让 C03 先把 pattern switch 归一成普通 switch/if，再交 E14 处理残余 goto case）；无其他强依赖 |
| 优先级 | **P0**（每命中一处就是一次转换失败，且降级形态机械、可编译性易保持） |
| 状态 | **已实现（2026-09-28/29，2026-09-30 核实）**：`pattern-desugar` 已翻转 Implemented（**默认关**，catalogItems `C03, C37`，RunAfter=`{ null-coalesce, tuple, foreach-deconstruction }`，PassCatalog.cs L111-122）；Core `PatternDesugar/` 9 文件 + 适配器，含模式/非整型 switch、catch-when、类型参数 catch 与表达式侧（C37）；SamplePatternSolution 项目级守门齐备；提交链 aa9fc2df→700bfa7a→559dcce4→1b67b823→d06064ef→1e1fa112→15d534a1 |
| 验证 | 中间树 build/test；fixture 覆盖关系/属性/元组/逻辑/关系模式、when 守卫、类型参数 catch、string foreach |
| 三次补全追加 | **表达式侧不在本条目范围**：`is` 表达式、switch 表达式的模式编译见 **C37**（两者共用「模式→布尔表达式」归约器）。`PatternMatchingTests` 实测 8/11 失败（2026-09-28，`-c Release`）表明语句侧与表达式侧当前同时缺失，C03 与 C37 应同批交付，否则模式用例仍红（见 §4.15） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/selection-statements（switch 表达式/语句）；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/try-catch（异常过滤器） |

#### C04 Yield 迭代器降级（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `IteratorLowering`（yield state machine） |
| 分类 | 1.3 控制流 |
| 目标 C# 模式 | `yield return` / `yield break` 的迭代器方法/属性 → 显式 `IEnumerator<T>`/`IEnumerable<T>` 实现（`_state/_current`、`MoveNext/Current/Dispose`）的可编译 C#（即 C# 编译器自身的 lowering 形态） |
| 解决的转换难点 | 当前硬失败：`StatementTransformer.Declarations.cs:167,171`；实验管线已有同形态证明 `Lowering/LowerYield.cs`（218 行，IR 级，仅测试可达）；Java 侧兜底桥 `IteratorBridgeTests.cs`、`YieldReturnRefArgTests.cs` 说明存量痛点 |
| 证据 | 见上；ROADMAP 未列但转换器明确「must be lowered by a preprocessor (iterator lowering)」 |
| 与现有通道关系 | 必须在 E02 LocalFunctionLift、C01 RefOutIn 之后（迭代器捕获与 ref 参数会进状态机字段）；try/yield 交互、`using` 内 yield 需与 IDisposable 形态联动（`LowerUsing.cs` 同构问题） |
| 优先级 | **P1**（收益大但状态机综合复杂，需先做原型验证 try-catch/finally 语义）。四次补全注记：sdk 实测 **2887 处**（训练集最高频的结构性缺口；lambda 4966、局部函数 3486 之后排第三的高频糖，但前两者已有 E02/E05 通道），方向性输入（§5.5 Layer 3）建议按频率列 **P0**；本清单暂保留 P1——try/finally 原型与 C01/C32/C45 的捕获字段依赖未收敛，待 EF Core 画像（§5.1）与原型验证后重估 |
| 状态 | **已实现（2026-09-29 落地 b784b47f，2026-09-30 核实）**：stub 已升迁 Implemented（**默认关**，Stage=StateMachine，RunAfter=`make-readonly`（closure-lift 仍为 stub，按设计 §3.3 由拓扑透传锚定），catalogItems `C04`，PassCatalog.cs L209-221）；Core `IteratorLowering/` 9 文件（Analyzer/CfgBuilder/StateMachinePlan/Rewriter/Naming/Options/Statistics/Diagnostic/Preprocessor）+ `IteratorLoweringPassAdapter`（残余门禁）+ PassChainFactory 接线，stub 计数 23→22；测试 `IteratorLoweringUnitTests`（ProjectTests）+ PassChainOrderTests 顺序断言（index < eliminate-goto）。**挂起**：§7.2 SampleIteratorSolution 项目级 fixture、§8 默认开启五门槛（try/finally 两半区原型、residual 为 0、sdk 2887 站点基线、EF Core 画像、opt-in 观察期）；CS2J0405 体内 lambda 捕获形态仍以 C32+C45 为前置 |
| 验证 | 中间树 build/test；fixture：嵌套循环 yield、try/finally 中 yield、带参迭代器、`yield break` 早退（单测已覆盖；项目级 SampleIteratorSolution fixture 待补，见状态行） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/yield（**已核**：位置限制/返回类型五类/迭代器内 using） |
| 六次补全（官方文档核对） | yield 位置限制（官方已核）：不得出现在带 in/ref/out 参数的方法、lambda/匿名方法、unsafe 块内（C# 13 起允许**方法含** unsafe 块、仅块内禁止）；yield return/break 不得在带 catch 的 try 与 catch/finally 块中、可在无 catch 的 try（含 finally）中；迭代器内 using 编译为 try-finally、与迭代器兼容（提前 break 仍正确释放——「yield+using Dispose 时序」子题的官方正向依据）；返回类型五类：IEnumerable<T>/IEnumerable/IAsyncEnumerable<T>/IEnumerator<T>/IEnumerator（async 迭代器 WithCancellation/[EnumeratorCancellation] 面归 C38） |

#### C05 Enum 声明归一（[Flags]/底层类型/显式值）（P1）
| 字段 | 内容 |
|------|------|
| 名称 | 扩展 `EnumPreprocessor`（不新建通道） |
| 分类 | 1.1 类型系统（enum） |
| 目标 C# 模式 | `[Flags]` enum、非 int 底层类型 enum、带显式值/重复值的 enum → 「静态字段 + 整数值 + 显式成员集合」的 enum-like class（或在 C# 侧改写为 struct/常量类），同时改写字典/switch/`ToString/Parse/HasFlag` 使用点 |
| 解决的转换难点 | 三类声明 100% 转换失败：`EnumTransformer.cs:29-45`；Java 侧桥已很厚（`EnumHelper.java`、`EnumHelperTryParseTests.cs`、`FlagsEnumToStringTests.cs`、`ReflectionFlagEnumMappingTests.cs`、`EnumValueMemberCollisionTests.cs`） |
| 证据 | 见上；E04 已把位运算使用点归一（`EnumSyntaxRewriter.cs`），声明侧是同一特性的另一半 |
| 与现有通道关系 | 并入 E04 同一 preprocessor；须在 E10 Generic 之前（enum 作泛型实参/数组元素）；与 C03 switch 去糖有交集（enum switch 不再被 C03 判为非法后仍需成员值比较） |
| 优先级 | **P1** |
| 状态 | 缺失（E04 只做运算） |
| 验证 | 中间树 build/test；fixture：`[Flags]` 位组合、byte 底层、显式跳跃值、enum 嵌套泛型集合 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/enum（[Flags]/底层类型/转换规则） |

#### C06 Unsafe/FFM 归一（pointer/fixed/unsafe/stackalloc/extern/ref struct）（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `UnsafeFfiNormalize`（单通道多规则） |
| 分类 | 1.8 / 1.2（ref struct 子项） |
| 目标 C# 模式 | 指针局部/参数/返回、`&` 取址/`->`/解引用/指针算术、`fixed` 块、`unsafe` 块、`stackalloc`、`[DllImport]/[LibraryImport] extern`、`ref struct`/`Span<T>` 边界 → 数组/Span/索引/MemorySegment（FFM）形态的可编译 C# |
| 解决的转换难点 | 今天的 fail-fast 群：`StatementTransformer.SwitchAndResource.cs:297,301`（fixed/unsafe）、`StatementTransformer.Declarations.cs:54`（指针局部）、`UnaryExpressionTransformer.cs:196`（取址/指针一元）、`ObjectCreationTransformer.cs:148`（stackalloc）、`ClassTransformer.cs:350`（extern/DllImport）、`StructTransformer.cs:60-64`（ref struct）；现有 FFM 兜底只覆盖 fixed *字段缓冲区*（`FieldTransformer.cs:70-101` + `Expression/Utilities/FfmHelper.cs` 346 行） |
| 证据 | 设计文档已有 4 份：`docs/superpowers/specs/2026-05-31-fixed-statement-ffm-design.md`、`2026-05-31-unsafe-method-pointer-params-design.md`、`2026-06-02-address-of-local-pointer-design.md`、`2026-05-30-csharp-stack-design.md`；测试簇 `FixedStatementFfmTests.cs`、`FixedPointerSpanRoundTripTests.cs`、`StackAllocArrayCreationTests.cs`、`PointerCastToIntegralTests.cs`、`UnsafeMethodPointerTests.cs`、`UnsafeReadUnalignedVectorTests.cs`；compat `Span.java`、`ReadOnlySpan.java`、`Unsafe.java`、`MemoryMarshal.java`、`GCHandle.java` |
| 与现有通道关系 | 依赖 C01（ref/out 位置模型是指针取址代理的基础）；与 E12 ArrayMethod、E09 Unsigned 同属「注入支撑形态」族；语义等价风险最高的一族（必须 extern/ABI 锁定，设计 D6 已有原则） |
| 优先级 | **P1**（高频基础设施代码命中，但等价性需逐项验证，建议按 fixed→stackalloc→指针→extern 分规则迭代） |
| 状态 | 缺失（仅有分散的转换器内 FFM 兜底） |
| 验证 | 中间树 build/test；fixture 即上述 4 份 spec 与现有 unsafe 测试簇 |
| 五次补全追加 | **`delegate*` 函数指针子项**（C# 9）：类型形态（`delegate*<int, void>`、`delegate* unmanaged[Cdecl]<…>` 调用约定修饰符）、`&Method` 取函数地址、间接调用 `fp(x)`——与既有指针规则同族划入本通道；Java 侧落点 MethodHandle 或显式拒绝，与 C42 FFM 边界同批选型。语言审计输入，仓库现状待核（见 C52） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/unsafe；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code（delegate* 函数指针节）；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/stackalloc |

#### C07 Struct 值语义补全（可变 struct 防御性 clone）（P1）
| 字段 | 内容 |
|------|------|
| 名称 | 扩展 `ReadOnlyStructMaker` 或新建 `StructValueSemantics` |
| 分类 | 1.2 值语义 |
| 目标 C# 模式 | 未被只读化的可变 struct 在变量初始化、赋值、属性/索引器取值、链式赋值、返回值等处插入显式 `Clone()`（C# 侧可定义 copy ctor/`Clone` 并保证编译通过） |
| 解决的转换难点 | 生产转换器今天靠 `Transformers/StructCloneHelper.cs`（399 行）在 Java 侧补 clone（`StructCloneReturnThisTests.cs`、`JaggedArraySelectCloneDebugTests.cs` 等大量回归）；实验管线同形 pass `Lowering/LowerStruct.cs:39-255`（含链式赋值展开 L139-166） |
| 证据 | `StructTransformer.cs:14-19`（值语义差异被明确划给预处理器）；`StructCloneHelper.cs` 全文；E13 的 `MethodMigrator.cs`/`AssignmentRewriter.cs` 已积累变异分析基础设施 |
| 与现有通道关系 | E13 的天然续作（只读化成功的类型不需要 clone；失败/跳过类型进入本通道）；应在 E06/E12 之后（tuple struct、数组元素是 clone 高频点） |
| 优先级 | **P1** |
| 状态 | 缺失（Java 侧代偿中） |
| 验证 | 中间树 build/test；fixture：属性返回 struct、结构体数组元素改值、链式赋值、readonly 字段内变异 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/struct（readonly struct 与 readonly 实例成员节） |
| 六次补全（官方文档核对） | 补子项：**readonly 实例成员**（C# 8 成员级 `readonly` 修饰——与 E13 的类型级 readonly struct 不同层；官方 struct 页已核）——含变异方法的 struct 经 readonly 成员/属性/只读字段调用触发防御性拷贝，与可变 clone 物化点存在交互（拷贝插入位置须避开 readonly 成员内的 this 视图），漏检方向是多余拷贝而非错值 |

#### C08 JavaKeywords 落地（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `JavaKeywords`（即 E17 的实施项） |
| 分类 | 1.8 命名安全 + 1.4（op_true/false） |
| 目标/证据/验证 | 全部见 E17 与设计文档 JK-0~JK-8、§8 测试规划 |
| 与现有通道关系 | 设计定死：**预处理链绝对最后一环**（E14 之后），分析对象是其它所有通道产出的最终中间树；默认关 + 独立 verb；落地后转换器 4 处兜底可评估退役（spec §10） |
| 优先级 | **P1**（设计/验收/里程碑已完备，实施风险低；直接消除 `true/false/null` 命名类 bug） |
| 状态 | 缺失（设计待实施） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/（上下文关键字全集） |

#### C09 `[ThreadStatic]` 前置化（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `ThreadStaticRewriter` |
| 分类 | 1.5 并发 |
| 目标 C# 模式 | `[ThreadStatic] static T x;` + 全部读写点 → `static readonly ThreadLocal<T> x = new ThreadLocal<T>();` + `.Value` 读写（语义等价的可编译 C#） |
| 解决的转换难点 | 现状在转换器内跨组件特判：字段声明改写（`FieldTransformer.cs:51-57,123-129`）+ 上下文注册表（`ConversionContext.cs:397-422`）+ 读写点在 Identifier/Assignment transformer 分散改 `.get()/.set()`；ROADMAP #6 明确要求前置化 |
| 证据 | 同上 |
| 与现有通道关系 | 须在 E13/C07 struct 通道之前或之后需原型确认（ThreadLocal<结构体> 的变异语义）；与 C02 静态字段归一有近邻关系（都是静态字段重写），可考虑合并为「静态字段归一」单通道 |
| 优先级 | **P2**（现路径可用，前置化主要是简化转换器、消除跨组件状态） |
| 状态 | 缺失（转换器内代偿） |
| 验证 | 中间树 build/test；fixture：线程内独立副本、静态构造初始化、跨类读写点 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/api/system.threadstaticattribute（**已核**：静态初始化器仅单线程执行） |
| 六次补全（官方文档核对） | ThreadStaticAttribute 官方语义（**已核**）：带初始化器的 `[ThreadStatic]` 字段**仅一个线程**（执行静态初始化器的线程，通常主线程）看到初始值，其余线程为 default(T)——与 `ThreadLocal<T>(() => init)` 的「每线程独立初始化」**不同**；本条目的 `new ThreadLocal<T>()` 无参物化恰好保真（初始值仅 default+单线程），但若源码带初始化器，须物化为「主线程 init、其余 default」的单线程语义而非 ThreadLocal 工厂；fixture 须含「静态初始化器 + 其他线程读取」用例 |

#### C10 JSON / Attribute 归一（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `AttributeNormalize`（先聚焦序列化 attribute 族） |
| 分类 | 1.6 反射/序列化 |
| 目标 C# 模式 | `[JsonConverter]`、`[JsonPropertyName]`、`[JsonIgnore]`、`[JsonInclude]`、`[JsonNumberHandling]`、`[JsonExtensionData]`、`[DataContract/DataMember]`、`[Obsolete]`、`[DefaultValue]` 等 → 统一形态（例如归一为转换器稳定识别的标记 attribute，或把构造期需要的反射信息改写为显式注册代码）；`[CallerMemberName/CallerFilePath/CallerLineNumber]` 族也应纳入（E08 已把携带 Caller attribute 的参数排除在剥离之外，`DefaultParameterVerifier.cs:16-17,113-115`——「调用点注入实参」语义在 Java 侧无对应，本通道需定保留/改写策略）；四次补全追加 **`[CallerArgumentExpression]`**——需在**每个调用点**注入实参表达式文本字面量（比 CallerMemberName 更依赖调用点改写，须同一通道处理） |
| 解决的转换难点 | attribute 在转换器里只有零星处理（全 Transformers 目录仅 `FieldTransformer.cs`/`PropertyTransformer.cs`/`EnumTransformer.cs` 7 处 AttributeListSyntax 命中），其余靠 compat 注解群硬桥：`CompatJsonConverter.java`、`CompatJsonPropertyName.java`、`CompatJsonIgnore.java`、`CompatJsonInclude.java`、`CompatJsonNumberHandling.java`、`CompatJsonExtensionData.java`、`CompatAttributeTypes.java`、`DefaultContractResolver.java`；映射配置 `config/TypeMappings.json:78-89,361-365`；ROADMAP #7「attribute(json)」 |
| 证据 | 测试 `JsonNumberHandlingAnnotationProjectTests.cs`、`ReflectionAttributeCompatTests.cs`、`SystemTextJsonDeserializeTypeReferenceTests.cs`、`DataContractSerializerCompatibilityTests.cs` |
| 与现有通道关系 | 应排在 C02（closed type token）之后；反射/字符串契约风险与 E17 nameof 同类（见 §4） |
| 优先级 | **P2**（桥接厚但当前可用；优先做「枚举化/白名单化」窄版本） |
| 状态 | 缺失 |
| 验证 | 中间树 build/test；fixture：STJ + Newtonsoft（`JsonConvert.java`/`JsonSerializerSettings.java`）+ DataContract 三套注解 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.conditionalattribute（**已核**：调用点按符号条件编译）；https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/reflection-and-attributes/ |
| 六次补全（官方文档核对） | 补子项：**[Conditional]**（Diagnostics，**已核**）——方法调用点按编译符号条件进入产物；E01 剥 `#if` 后调用点仍在源码中：C# 编译器在 Release 中间树构建时会静默消除 `[Conditional("DEBUG")]` 调用点，直译 Java 则**执行了本不应执行的调用**（静默行为差异）——须在 C# 侧按构建符号物化（符号已知→剥离调用点；未知→保留并告警）；[ModuleInitializer] 同类登记（Java 无模块初始化器对应） |

#### C11 Property 去糖（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（property 规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 显式/自动属性（含 init、表达式体、外显接口实现、hiding）→ 后备字段 + `GetX/SetX` 普通方法的可编译 C# |
| 解决的转换难点 | `Member/PropertyTransformer.cs`（192 行高密度单行代码，自动属性后备字段、init、显式接口、hiding、struct clone 耦合，见 L9-40 即有 10+ 分支）；实验 `Lowering/LowerProperty.cs`（255 行）证明目标形态；相关回归密集（`PropertyHidingTests.cs`、`ExplicitInterfacePropertyConflictTests.cs`、`AutoPropertyFieldAccessTests.cs`、`PropertySetterExpressionBodyTests.cs` 等） |
| 证据 | 同上 |
| 与现有通道关系 | 与 C12/C13/C14 同属一族，建议一个 preprocessor 四条规则；必须晚于 E13/C07（属性是 struct 变异迁移主战场）；被 E17 的改名表覆盖（JK-1 属性符号） |
| 优先级 | **P2** |
| 状态 | **已实现（2026-09-29，2026-09-30 核实）**：member-desugar 族**锚点 pass** 已翻转 Implemented（**默认关**，Stage=Declarative，RunAfter=`{ struct-value-semantics, closure-lift }`，catalogItems `C11, C12, C13, C14`，PassCatalog.cs L230-241）；Core `MemberDesugar/` 共享基建（规则发现/plan/回滚/命名/统计/诊断）+ 属性声明归约/使用点重写 + `MemberDesugarPassAdapter` + CLI 接线；SampleMemberDesugarSolution fixture（12/12 行为门 + e2e 绿）；提交链 7300f1bf→2c51aec3→26ea367c→ffa9cbe9→8f6032da（分支 feature/preproc-c11-property-desugar，已并入 dev-3.x）。默认翻转评审挂起 |
| 五次补全追加 | **C# 14 `field` 关键字**（半自动属性）是本通道必备子规则，单列 **C50**（产物形态与本通道完全同形，Java 侧零新增）；目标基线为 .NET 10 时 C11 实施必须一并覆盖，否则访问器体内 `field` 标识符解析失败 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/properties（自动属性/init/required/field） |

#### C12 Indexer 去糖（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（indexer 规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | `this[...]` 声明 → `GetItem/SetItem` 方法；调用点 `obj[i]`（含多维、字典式、ref 返回）→ 方法调用 |
| 解决的转换难点 | `Member/IndexerTransformer.cs`（304 行，显式接口/符号兜底/partial 合并分支 L25-40）+ `ElementAccessTransformer.cs`（343 行）的属性-数组二义；实验 `Lowering/LowerIndexer.cs:37-62` 即同形降级；测试簇 `DictionaryIndexerReadCheckedTests.cs`、`MultiArgIndexerCompoundAssignmentTests.cs`、`InterfaceIndexerReturnTypeTests.cs`、`ArrayIndexerAccessTests.cs` |
| 证据 | 同上 |
| 与现有通道关系 | 同 C11；复合赋值/自增（C17）依赖 indexer 先方法化 |
| 优先级 | **P2** |
| 状态 | **已实现（2026-09-29，2026-09-30 核实）**：member-desugar 族 Indexers 规则（Core `MemberDesugar/Indexers/`：声明归约 get_Item/set_Item + 两遍全调用点改写 + 索引器 residual 闸门覆盖 + 适配器摘要段）；不新增 pass/开关；SampleMemberDesugarSolution 索引器 fixture 与项目级门齐备；止于 146fdda2（分支 clean-c2，已并入 dev-3.x） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/indexers/ |

#### C13 Event 去糖（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（event 规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 字段式事件 + 显式 add/remove + 触发点 → 委托字段 + `AddX/RemoveX`/raise 方法；调用点统一改写（语义等价 C#） |
| 解决的转换难点 | `Member/EventFieldTransformer.cs`（447 行，后备存储判定 L15-44、监听器签名/接口限定）+ `Java/Rewriters/EventHandlerLambdaRewriter.cs` + 实验 `Lowering/LowerEvent.cs:35-47`（`add/remove/...Listeners.forEach` 形态）；测试 `EventDelegateInvokeMethodTests.cs`、`InterfaceEventListenerNamingTests.cs`、`EventHandlerEventArgsMappingTests.cs`、`EventAndStaticPropertyRegressionTests.cs` |
| 证据 | 同上 |
| 与现有通道关系 | 与 E05 DelegatePreprocessor 边界：E05 已处理委托 `+=` 组合；事件规则消费其产物。一族实现 |
| 优先级 | **P2** |
| 状态 | **已实现（2026-09-29，2026-09-30 核实）**：member-desugar 族 Events 规则（`MemberDesugar/Events/`：EventRule/EventPlan/EventFacts/EventDiagnostics + 声明扩展（add_X/remove_X）+ 使用点重写，property-first 两遍派发 + 适配器按条件注入 Cs2jPrelude 引用）；跨项目订阅 fixture 与行为测试绿；止于 129cb473（分支 clean-c3，已并入 dev-3.x） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/event（+=/-= 与 add/remove 访问器） |

#### C14 Operator 去糖（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（operator 规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 用户自定义 `op_*`（含隐式/显式转换、算术、比较；op_true/false 除外——归 E17）声明 → 命名方法；调用点按符号绑定改方法调用 |
| 解决的转换难点 | `Member/OperatorTransformer.cs`（290 行，`OpSymbolToJavaName` 表 L44-45 等）+ `BinaryExpressionTransformer.cs:133-144`（enum 操作数硬拒绝）+ `UnaryExpressionTransformer.cs:150-151`（decimal 一元硬拒绝）+ `Java/Rewriters/OperatorPrecedenceRewriter.cs`；实验 `Lowering/LowerOperator.cs:38-63`；测试 `OperatorMethodNameCollisionTests.cs`、`DelegateConstraintMappingTests.cs` |
| 证据 | 同上 |
| 与现有通道关系 | op_true/false 归 C08（已有精确绑定设计可复用其语义分类基建）；须晚于 E09（无符号扩展方法已接管数值运算）与 C05（enum 运算已被 E04 归并） |
| 优先级 | **P2** |
| 状态 | **已实现（M0-M4/Task 1-12 全链，2026-09-29 落地 2d25700e，2026-09-30 核实）**：member-desugar 族 Operators 规则（`MemberDesugar/Operators/`：运算符/转换声明归约为静态方法 + 二元/一元站点、OC-3 复合赋值内联/提升、OC-4/5 转换站点显式化、OC-6 六族提升公式 + skip 闸门族 + 诊断 CS2J5170-5183 + 工程性质/JK 组合测试）；Sample fixture 全绿，落地勘误见设计 §13.9；分支 clean-c4 已并入 dev-3.x。Java 半区消费清单（设计 §5.1）为后续 PR |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/operator-overloading（checked 用户定义运算符节，已核）；https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14（用户定义复合赋值运算符） |
| 六次补全（官方文档核对） | 补两个官方子项（**已核**）：① **checked 用户定义运算符**（C# 11）——`operator checked +` 显式声明（元数据名 `op_CheckedAddition` 族），checked/unchecked 上下文分别绑定 checked/普通实现，与本通道及 C15 联动；② **用户定义复合赋值运算符**（C# 14）——`operator +=` 等复合形态可直接声明，`a += b` **不再必然拆解为 `a = a + b`**（物化前须 SemanticModel 查显式复合运算符——C17 的拆解前提被 C# 14 打破） |

#### C15 checked / unchecked 归一（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `CheckedArithmetic`（可并入 C14 数值族或 E09 同族扩展） |
| 分类 | 1.4 / 数值 |
| 目标 C# 模式 | `checked(...)`/checked 块 → 显式溢出检查辅助调用（对应 Java `Math.*Exact`）；`unchecked(...)`/块 → 剥离 |
| 解决的转换难点 | 今天直接拒绝：`StatementTransformer.SwitchAndResource.cs:305,309`；与 E09 的无符号窄类型 `unchecked` 包裹（`EnumSyntaxRewriter.cs:208` 提到同款需求）是同一机制 |
| 证据 | 同上 |
| 与现有通道关系 | 小而独立；建议作为「数值边界归一」规则集与 E09/C17 同通道 |
| 优先级 | **P2**（规则简单、命中即失败，性价比高） |
| 状态 | 缺失 |
| 验证 | fixture：checked 表达式溢出、unchecked 常量折叠、嵌套块 |
| 三次补全追加 | 本条目并非「纯 fail-fast」：checked **表达式**今天**静默丢失语义**——产 `/* checked */ expr` 前缀（`TypeOperationTransformer.cs:31`），溢出检查消失；相关的 `sizeof(用户类型/Int128 等)` 产 `"/* sizeof */"` 占位（`TypeOperationTransformer.cs:155-164`）；`unchecked` 语句文本被 `ShouldEmitStatementText` 直接过滤丢弃（`StatementTransformer.cs:310-315`，以 `/* TODO: UncheckedStatement` 前缀判别）。三者应并入同一「数值语义显式化」规则集，且 default 策略改为显式报错而非占位（见 §4.12） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/checked（默认溢出上下文/常量折叠规则） |

#### C16 foreach 补全（string / 残余解构）（P2）
| 字段 | 内容 |
|------|------|
| 名称 | 扩展 `ForeachDeconstruction` |
| 分类 | 1.3 控制流 |
| 目标 C# 模式 | 字符串 foreach → `ToCharArray()` 后 foreach；E07 未覆盖的其它解构 foreach 形态 |
| 解决的转换难点 | `StatementTransformer.Loops.cs:121`（硬拒绝）、`Loops.cs:159`；E07 对 Tuple 族显式 skip（`ForeachDeconstructionRewriter.cs:67-68`）依赖 E06 先跑——顺序契约已在，但非 KV/Deconstruct 残余仍在 |
| 证据 | 同上；测试 `ArrayForeachNotCollectedTests.cs`、`ForeachNonGenericCollectionCastTests.cs` |
| 与现有通道关系 | E07 的直接补全；排在 E06 之后（现状已满足） |
| 优先级 | **P2**（工作量极小，顺手消灭一类 fail-fast） |
| 状态 | 缺失（窄） |
| 三次补全追加 | 本条目只覆盖「string + 残余解构」，**迭代源形态未覆盖**：模式化 `GetEnumerator`/显式 `IEnumerator` 适配、`ref`/`ref readonly` foreach 关键字在 `Loops.cs:104-155` 中均未检查（既不失败也不归一），且转换器注释自述「enumerator adapters … must be lowered by a preprocessor」（`Loops.cs:99-103`）——已登记为 **C40**，与 C16 同通道实施 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/foreach-in（**已核**：null 源 NRE/显式类型运行时检查） |
| 六次补全（官方文档核对） | foreach 官方语义（**已核**）：**集合为 null 抛 NRE**（与空集合零迭代是两个分支——物化产物不得吞 null）；显式迭代变量类型含到该类型的转换、失败抛 InvalidCastException（运行时检查，非恒为隐式装箱）；模式化 GetEnumerator 可为**扩展方法**（语言规范专节）——除 string/残余解构外的迭代源统一归 C40；break/continue 语义与 Java 一致 |

#### C17 复杂自增/自减目标归一（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `IncrementTarget`（数值族规则） |
| 分类 | 1.4 / 数值 |
| 目标 C# 模式 | 属性/事件/索引器/enum/decimal/无符号窄类型/IntPtr/自定义 `++/--` 目标 → 读-改-写显式语句 |
| 解决的转换难点 | `UnaryExpressionTransformer.cs:154-193` 对上述目标逐个抛异常（L169,173,176,182,184,189,192）；E09 只解决了无符号后缀（`UnsignedPostfixRewriter.cs`），其余目标仍硬失败 |
| 证据 | 同上 |
| 与现有通道关系 | 依赖 C11/C12（属性/索引器方法化后自增归约自然消失大半）；与 C05 enum、C14 decimal 运算同族 |
| 优先级 | **P2** |
| 状态 | 缺失 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/arithmetic-operators（前/后缀自增语义） |

#### C18 async / await 归一（P2，高不确定）
| 字段 | 内容 |
|------|------|
| 名称 | `AsyncNormalize`（形态需原型选型） |
| 分类 | 1.5 异步 |
| 目标 C# 模式 | 两条可选形态：① 轻量版——async lambda/async 局部函数提升为命名 async 方法、统一 `ConfigureAwait` 形态；② 重量版——源码级 async 状态机消解（生成显式状态机驱动 Task-like），使输入再无 `await` |
| 解决的转换难点 | async lambda **今天直接拒绝**（`LambdaTransformer.cs:52`）；await 映射为 `CompletableFuture.join()` 并带上下文特判（`ControlFlowTransformer.cs:360-397`）；方法级 async 标记（`MethodTransformer.cs:31`）；测试簇 `ConversionTests/Categories/AsyncAwaitTests.cs`、`ConfigureAwaitConversionTests.cs`、`GetResultConversionTests.cs`、`StreamAsyncTokenTests.cs`、`TaskRunGenericMappingTests.cs`；compat `Task.java`、`ValueTask.java`、`TaskFactory.java`、`CancellationToken*.java` |
| 证据 | 同上 |
| 与现有通道关系 | 必须在 E02 之后；形态①可先做且低风险，形态②等价性成本接近重写编译器 lowering |
| 优先级 | **P2**（需求真实——async lambda 即失败；但全量降级收益/成本存疑，先做原型，见 §4） |
| 状态 | 缺失 |
| 三次补全追加 | 本条目只覆盖 async **方法体**；**异步序列与释放**是独立缺口：`await foreach`（`Loops.cs:104-155`）与 `await using`（`SwitchAndResource.cs:198-244`）的关键字均未被检查（静默降级为同步形态），且 `IAsyncEnumerable`/`IAsyncDisposable` 无类型映射、compat 无对应类——已登记为 **C38**，须与 C18 的形态选型同批定案 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/await；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/async |

#### C19 扩展方法显式化（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `ExtensionMethodResolve` |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 把扩展方法调用 `x.M(a)` 按语义绑定改写为静态调用 `ExtType.M(x, a)`（含嵌套命名空间/using 引入的扩展） |
| 解决的转换难点 | 主转换需自建扩展方法索引与解析：`Pipeline/ExtensionMethodIndex.cs`、`LinqRewrite/ExtensionMethods.cs`、测试 `ExtensionMethodTests.cs`（Tests 与 ConversionTests 各一份）；Java 侧静态方法分派与 C# 扩展接收者语义在重载/泛型处反复出回归 |
| 证据 | 同上 |
| 与现有通道关系 | 须在所有会引入新扩展方法的通道**之后**（E09 注入 Cs2jUnsignedMath、E12 注入 Cs2jArray、E05 注入 Cs2jPrelude——它必须看得见这些绑定），故位置靠后；与 E16 LINQ 有交互（LINQ 即扩展方法） |
| 优先级 | **P2** |
| 状态 | 缺失 |
| 五次补全追加 | C# 14 extension 块引入的**扩展属性/扩展运算符/静态扩展**超出本条目「方法调用点」范围，已单列 **C49**（扩展索引器为 C# 15 预览，归 C52 防线）；两者共用扩展方法索引与接收者绑定基建，应同通道或紧邻批次实施 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/extension-methods；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/extension（C# 14，已核） |

#### C20 partial 类型/方法合并前置（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `PartialMerge` |
| 分类 | 1.7 |
| 目标 C# 模式 | partial class/struct/interface、partial 方法 → 合并后的单一类型声明 C#（中间树） |
| 解决的转换难点 | 现合并逻辑在主转换期：`PartialType/`（`PartialTypeMerger.cs` 300 行、`PartialMethodMerger.cs`）、`Context/PartialTypeMergeStore.cs`，大量测试（`PartialStructMergeTests.cs`、`PartialInterfaceTupleMappingTests.cs`）；每个下游 preprocessor 都要各自面对多部件语法 |
| 证据 | 同上 |
| 与现有通道关系 | 若前置，应放在**最前几位**（E01 之后），让后续全部通道只面对合并类型；但会与「逐文件重写 + 整树回滚」范式冲突（合并产物不可逆），需评估 |
| 优先级 | **P3**（当前路径成熟，前置主要是架构简化） |
| 状态 | 缺失 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/partial-type；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/partial-method（**已核**：C# 9 扩展形态/partial 事件构造器） |
| 六次补全（官方文档核对） | partial 方法两代形态（**已核**）：C# 9 扩展 partial 方法（非 void 返回/out 参数/访问修饰符/实现**可选**）与经典 void+private 形态并存——PartialMethodMerger 须按「实现可选性」双轨合并；partial 构造器/事件（C# 13）：恰好一个定义性 + 一个实现性声明、**仅实现性 partial 构造器可带初始化器**、类上主构造器语法至多一个 partial 部分声明、partial 事件的实现性声明须带 add/remove（定义性为字段式事件） |

#### C21 匿名类型提升（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `AnonymousTypeLift` |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | `new { X = 1, Y = 2 }` → 生成具名不可变类型（跨方法/跨文件形状去重，类似 E06 的 tuple shape registry） |
| 解决的转换难点 | 主转换器现合成 record：`Transformers/AnonymousTypeRecordSynthesizer.cs`；LINQ 与 foreach 高频耦合（`ForeachAnonymousTypeTests.cs`、`LinqAnonymousTypeExtractedMethodTests.cs`） |
| 证据 | 同上 |
| 与现有通道关系 | 应在 E16 LINQ 之前（LINQ 过程式化会大量暴露匿名形状）；可复用 E06 shape registry 基建 |
| 优先级 | **P3**（Java 28 record 路径已能吸收，仅低版本/复杂 LINQ 场景受益） |
| 状态 | 缺失 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/types/anonymous-types |

#### C22 `Nullable<T>` 值类型提升（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `NullableValueLift` |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | `T?`（值类型）上的运算/比较/`GetValueOrDefault`/`bool?` 三值逻辑 → 显式 `.HasValue/.Value` 或辅助调用 |
| 解决的转换难点 | 转换器侧特判分散（`NullableBoolComparisonTests.cs`、`NullableGetUnderlyingTypeTests.cs`、`NullableBaseTypeRuntimeClassTests.cs`、`GetValueOrDefaultMappingTests.cs`）；E04 已含「NullableLiftedRewritten」一类规则（`EnumPreprocessorStatistics.cs:15`）说明模式有效 |
| 证据 | 同上 |
| 与现有通道关系 | 与 E04 数值/枚举提升同族，可吸收 |
| 优先级 | **P3**（compat `Nullable*` + 现有特判基本覆盖） |
| 状态 | 缺失 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/nullable-value-types（**已核**：lifted 运算/bool? & | 例外） |
| 六次补全（官方文档核对） | 提升运算官方规则（**已核**）：一元/二元运算与用户定义转换均提升（任一 null → 结果 null）；**`bool?` 的 `&`/`|` 是例外**（三值逻辑——null 与 true/false 组合可产非 null 结果）；比较 `< > <= >=` 任一 null → **false**（含 `null >= null` 为 false）；`==` 双 null → true、单 null → false；此表与 C47④ 的调用点物化须同源 |

#### C23 插值字符串显式展开（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `InterpolationExpand` |
| 分类 | 1.8 表达式 |
| 目标 C# 模式 | `$"..."`（含插值、对齐、格式串、raw string）→ `string.Format`/`string.Concat` 显式调用或 DefaultInterpolatedStringHandler 形态 |
| 解决的转换难点 | `Expression/Transformers/StringExpressionTransformer.cs`（336 行）+ `Java/Rewriters/StringConcatRewriter.cs`；回归密集（`StringFormatDoubleTests.cs`、`StringConcat*` 等） |
| 证据 | 同上 |
| 四次补全追加 | sdk 实测 **650 处**。**必须用 SemanticModel 判定插值绑定的 handler**：默认 `DefaultInterpolatedStringHandler` → `string.Concat/Format`；自定义 `InterpolatedStringHandler`（json/日志场景在 sdk 高频使用）的追加语义、参数求值次数与条件构造完全不同，无脑 Concat 会改变可观察行为。raw string 插值 `$"""..."""` 与 C43 的词法边界须一并定义（raw 字面量本身已内联，见 C43） |
| 优先级 | **P3**（现路径成熟，前置收益主要是删掉一整个大 transformer；handler 判定子项有正确性含义，若 EF Core 画像中日志/JSON 插值高频应提前） |
| 状态 | 缺失 |
| 五次补全追加 | const 内插字符串（C# 10 `const string s = $"a{1}"`）补入本通道子规则：编译期常量折叠形态，Java 侧 `static final` 需保证常量性；与 `string.Concat` 物化（C47 ③）产物同族。语言审计输入，仓库现状待核（见 C52） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/tokens/interpolated-strings（DefaultInterpolatedStringHandler/自定义 handler 绑定） |

#### C24 对象/集合初始化器归一（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `InitializerDesugar` |
| 分类 | 1.8 表达式 |
| 目标 C# 模式 | 对象初始化器、集合初始化器、`with` 字典初始化、target-typed new → 构造 + 逐属性 set/Add 的语句序列 |
| 解决的转换难点 | `ObjectCreationTransformer.cs`（含 `stackalloc` 分支）、测试 `ObjectInitializerArrayToIListTests.cs`、`ObjectInitializerMethodChainTests.cs`、`TernaryObjectInitializerNullGuardTests.cs` |
| 证据 | 同上 |
| 优先级 | **P3** |
| 状态 | 缺失 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/object-and-collection-initializers |

#### C25 dynamic（P3，长期观察）
| 字段 | 内容 |
|------|------|
| 名称 | `DynamicDesugar` |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | `dynamic` 绑定点 → 显式反射/对象分派（仅在符号可静态推断处） |
| 解决的转换难点 | Transformers 内几乎无 dynamic 处理（仅 `ElementAccessTransformer.cs:275` 注释提及 unknown/dynamic fallback）；Java 无动态子类型；E17 设计把 dynamic 站点明确列为不可行（JK-8） |
| 证据 | 同上 |
| 优先级 | **P3**（静态不可证场景多，建议长期仅做「可证明子集 + 其余报错」） |
| 状态 | 缺失 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/interop/using-type-dynamic |

#### C26 LINQ 通道独立化（P2，结构性候选）
| 字段 | 内容 |
|------|------|
| 名称 | 复用 `LinqRewrite`，新增 `ProjectLinqPreprocessor` |
| 分类 | 1.8 LINQ |
| 目标模式 | 把 E16 的嵌入式重写提升为标准 preprocessor：独立中间目录 `.cs2j-linq-src`、独立 verb、`dotnet build/test` 等价性闸门、skipped-chain 报告（现有 `--linq-report` 已产统计，`Program.cs:875-876`） |
| 解决的转换难点 | 今天 LINQ 重写与主转换共享 SemanticModel、失败不阻断、无数 C# 侧等价性证明；独立后可在 .NET 侧直接验证过程式化正确性，并让 Stream/Procedural 两条策略变成两个可独立验证的输入 |
| 证据 | 调用点 `ProjectPasses.cs:164,286-351`；规则文件 2156 行；报告基建 `LinqRewrite/LinqRewriteObservability.cs` |
| 四次补全追加（强制语义门） | **LINQ 必须按 receiver 三路分流**：① `IEnumerable`/`IOrderedEnumerable` receiver → 现有过程式展开；② `IQueryable`/`IOrderedQueryable` receiver → **绝不展开**，保留 `Queryable.xxx(...)` 调用交 Java 侧查询提供方；③ `Expression<Func<...>>` 实参 → 保留表达式树节点构造。2026-09-28 核实：`LinqRewrite/` 全目录**无任何 IQueryable/Expression 树判定**（grep 命中均为无关的 `Lambda.cs` 与 visitor），主仓仅有 `Pipeline/ConversionPipeline.cs:178-182`（补 metadata 引用让 Roslyn 能解析 IQueryable 扩展方法/`Expression<>`）、`Context/TypeMappingService.cs:516`（剥 `Expression<>` 包装）、`ArgumentTransformer.Coercion.cs:599`（一处名字枚举）。不加分流会在 EF 代码上把服务端查询**静默改写为客户端求值**——能编译、单测难抓（§4 风险 17）。分流必须在通道入口用 SemanticModel 完成 |
| 与现有通道关系 | 依赖 C19（扩展方法解析）若同批做需排序：LINQ 先（消费扩展方法糖）、C19 在后；应在 E02/E03/E06 之后 |
| 优先级 | **P2**（不新增功能，但显著提升可验证性与流水线一致性；**三路分流门是通道化的前置验收条件**，不是可选项） |
| 状态 | 缺失（重写器存在，通道化未做；receiver 分流未做） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/linq/（标准查询运算符语义） |

#### C27 record `with` / init/required 补充去糖（P3）
| 字段 | 内容 |
|------|------|
| 名称 | 可并入 C11 `MemberDesugar` 或 E11 |
| 分类 | 1.4 / 1.2 |
| 目标 C# 模式 | `with` 表达式 → clone + 初始化；`init` 访问器、`required` 成员 → 普通可设置形态 |
| 解决的转换难点 | `Type/RecordTransformer.cs:25-59` 的额外成员 fallback；E11 已处理只读化但 `with`/init 使用点仍在表达式/属性 transformer 分散 |
| 证据 | `RecordTransformerTests.cs`、`RecordTupleTests.cs`、`PropertyTransformer.cs:21`（init 当 set 处理） |
| 优先级 | **P3** |
| 状态 | 缺失 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/init；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/required；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/with-expression |
| 六次补全（官方文档核对） | 补子项：**[SetsRequiredMembers]**（C# 11）——构造器标记后编译器视 required 成员已初始化（绕过「构造时必须设置」校验）；required 物化时该声明与豁免语义须一并登记，否则低层检查无法重建；`with` 的非破坏性变更 = clone（protected virtual `<Clone>$`）+ init 赋值，与 E11 record 链共用物化（官方 with-expression 页） |

#### C28 指令/using trivia 清扫扩展（P3）
| 字段 | 内容 |
|------|------|
| 名称 | 扩展 `ConditionalDirectiveStrip` |
| 分类 | 1.7 |
| 目标 C# 模式 | `#line`、`#error`（非恒剥场景）、`#define` 限界、extern alias、global using 注入/别名归一、文件作用域命名空间可选归一等 trivia 层清扫 |
| 解决的转换难点 | E01 已剥 `#pragma/#nullable/#region(可选)`（`ConditionalDirectiveStripper.cs:75-83`）；using 别名解析在转换器内有专门注册表（`Context/UsingAliasRegistry.cs`、测试 `UsingAliasResolutionTests.cs`、`GlobalAliasQualifiedNameTests.cs`） |
| 四次补全追加 | sdk 实测频率：file-scoped namespace **2**、global using/using static/extern alias **20+**（训练集低频但纯语法、幂等，是统一引擎「纯语法批只 parse 一次」的天然成员，§5.4）；与 C43 同属 Layer 0 |
| 证据 | 同上 |
| 优先级 | **P3**（整洁度/一致性，不解决 fail-fast） |
| 状态 | 缺失（窄扩展） |
| 五次补全追加 | C# 12 **alias any type**（`using Point = (int, int);`、`using Buf = byte[];`、`using P = int*;`）——别名可指向元组/数组/指针类型，UsingAliasRegistry 解析形态需覆盖（元组别名须在 E06 之后展开为 tuple struct 名）；别名限定符 `::`（`global::`/extern alias）表达式形态同属本通道。另：`file` **局部类型**（C# 11 修饰符，与 file-scoped namespace 不同特性）归 **C52** 收编 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/using-directive（global using/C# 12 alias any type） |

#### C29 lock / Monitor 并发原语归一（P3，观察项）
| 字段 | 内容 |
|------|------|
| 名称 | `ConcurrencyNormalize`（lock/volatile/Monitor 族，观察项；可与 C09 合并评估为「并发归一」单通道） |
| 分类 | 1.5 并发 |
| 目标 C# 模式 | `lock (o) {…}`、`volatile` 字段、`Monitor.Enter/TryEnter(timeout)/Exit/Pulse/Wait` → 统一可映射形态：lock→`synchronized`、volatile→Java volatile 直映保留；无法直映的 `TryEnter` 超时、`Pulse/Wait` → `ThreadHelper`/`Object.wait/notify` 显式调用形态 |
| 解决的转换难点 | 非失败型语义缺口：lock 直映 `synchronized`（`StatementTransformer.SwitchAndResource.cs:286-293`）、volatile 直映（`FieldTransformer.cs:104,410`）均可用，但 `Monitor.TryEnter` 超时分支与 `Pulse/Wait` 在直映下**静默丢失**（等价于死等/永不唤醒）；本条即 §1.5 原文「暂列观察项」的正式落点 |
| 证据 | 同上；compat `ThreadHelper.java`、`Interlocked.java`/`InterlockedHelper.java` 吸收 Interlocked/Thread API |
| 与现有通道关系 | 与 C09 ThreadStatic 同族；Interlocked/Volatile 静态 API 由 compat 吸收无需重复，仅 Monitor 实例语义需前置 |
| 优先级 | **P3**（现路径可用；价值在把静默语义缺口显式化） |
| 状态 | 缺失（转换器直映吸收中） |
| 验证 | 中间树 build/test；fixture：`Monitor.TryEnter` 超时分支、`Pulse/Wait` 生产者-消费者、`lock` 嵌套 |
| 五次补全追加 | **C# 13 `System.Threading.Lock`/`Sync` 目标类型 lock**：编译器把 `lock (l)`（l 为 Lock 类型）译为 `EnterScope()` 的 scope 释放语义，与直映 `synchronized` 的 Monitor 语义**不可混用**（错误嵌套/释放时序差）；.NET 9+ BCL 新代码已开始使用。本通道需在入口用 SemanticModel 区分 lock 目标类型：`object` → synchronized 直映、`Lock` → compat 锁形态或显式拒绝。语言审计输入，仓库现状待核（C52 ①） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock（**已核**：Lock/Monitor 双下推形态）；https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock |
| 六次补全（官方文档核对） | lock 官方双下推形态（**已核**）：接收者为 `System.Threading.Lock`（.NET 9+/C# 13）时精确等价 `using (x.EnterScope()) { … }`（`Lock.Scope` 为 ref struct，Dispose 释放）；否则精确等价 `object __lockObj = x; bool __lockWasTaken = false; try { Monitor.Enter(__lockObj, ref __lockWasTaken); … } finally { if (__lockWasTaken) Monitor.Exit(__lockObj); }`——可重入计数/异常释放由此两形态完整承载；**lock 体内不得 await**（跨 await 持锁须 fail-fast）；编译器对「已知 Lock 对象被转型后加锁」发警告（转换器同理应告警） |

#### C30 closed generic type metadata 前置化（P2，需先核实现状）
| 字段 | 内容 |
|------|------|
| 名称 | 不单独新建 C# 通道；作为 **C02 的产出契约**登记（备选：生成「闭型元数据注册」C# 代码） |
| 分类 | 1.6 反射/序列化 |
| 目标 C# 模式 | 让闭型泛型（如 `List<List<short>>`）的类型实参在 C# 阶段就显式可追溯（显式 Type token/注册代码），供 Java 反射侧零 ThreadLocal 消费 |
| 解决的转换难点 | 嵌套泛型 JSON 序列化长期靠 ThreadLocal 队列（设计 §1 描述的 `[String]` 误解析根因）；compat 已落 ThreadLocal-free 键控（`ConcurrentHashMapHelper.java:10-31,63-72`、设计列出的 `TypeHelper.closedCacheKey/createConverterForClosedType` 等 API） |
| 证据 | 设计 `docs/superpowers/specs/2026-09-22-closed-type-metadata-chain-design.md`；**2026-09-30 代码考古已收口**（详见 C30 设计 2026-09-29 §2）：PatchClosed* 系列曾由 2694f975 引入 `src/CSharpToJava.Core/Pipeline/ProjectPasses.cs`，后被 **c8d6d31f 架构性删除**（两提交均为 HEAD 祖先，git log/历史文件可证），当前 grep 不到属预期，非「未合入」；compat 纯函数（closedCacheKey/createConverterForClosedType 等）全部存活；唯一下游仓 `D:\dotnet-text-json` 的补丁持有状态仍待 M1 核实门（本机无该仓，无法证实）；ROADMAP #10 |
| 与现有通道关系 | 与 C02 一体两面；落地前需先做一次代码考古确认补丁去向，再决定是「C# 前置注册」还是「Java 侧反射闭型」路线 |
| 优先级 | **P2**（路线已被验证有效，但本仓工作量待核实） |
| 状态 | 路线 C 近期收口；compat 契约存活；生成码补丁已确认被 c8d6d31f 删除；M1 下游核实门未完成 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/types/generics（构造类型反射契约——工程条目，官方文档仅语言面） |

#### C31 Index / Range（`^` 与 `..`）归一（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `IndexRangeNormalize`（建议挂 E12 同范式：注入切片辅助——并入 Cs2jArray 或新建 Cs2jIndexRange） |
| 分类 | 1.8 表达式（数组/字符串/Span 切片） |
| 目标 C# 模式 | ① 元素访问 `x[^n]`（数组/字符串/带 Index 索引器的自定义类型）；② 切片 `x[lo..hi]`、`x[lo..]`、`x[..^n]`（合法接收者：数组、string、Span/Memory、自定义 slice 索引器）；③ 独立 `^n` / `lo..hi` 表达式与 `System.Index`/`System.Range` 类型的局部/参数/字段；④ 全部改写为显式 int 索引或辅助调用（数组 `Arrays.copyOfRange`、字符串 `substring`、Span → C06 形态） |
| 解决的转换难点 | 这是「静默错误输出」而非 fail-fast 的一族：`x[^n]` 元素访问已按接收者三类直映（`ElementAccessTransformer.cs:228-236`），但**切片不看接收者类型**一律产 `Arrays.copyOfRange(expr, lo, hi)`（`ElementAccessTransformer.cs:219-225`）——对 string 产出 `Arrays.copyOfRange("abc", …)`（不可编译 Java，String 非数组）；独立 `^n` 只输出注释（`ElementAccessTransformer.cs:294-299`）；独立范围表达式输出 `/* array */`/`/* length */` 占位（`ControlFlowTransformer.cs:640-650`）；`System.Index`/`System.Range` 在 `config/TypeMappings.json` 无映射；全仓无 Range/Index 专项测试 |
| 证据 | 同上；E12 的 `src/Cs2jArray/ArrayHelper.cs` 无任何 Slice/Range 辅助（grep 无命中），需随本通道补支撑库 |
| 与现有通道关系 | E12 ArrayMethod 同族（支撑库注入 + 过滤范式）；`arr[^1]++` 类复合目标并入 C17；Span 切片与 C06 衔接；排在 E06/E12 之后（tuple struct、数组辅助先定型） |
| 优先级 | **P1**（命中即产出不可编译/错误 Java，比 fail-fast 更危险，且修复面机械） |
| 状态 | 缺失 |
| 验证 | 中间树 build/test；fixture：数组/字符串的 `^` 与 `..`、组合 `..^n`、独立 Index/Range 变量、切片后 `.Length`/再赋值、C# 切片拷贝语义 vs Java subList 视图语义的显式化 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/member-access-operators（**已核**：^n=length−n/^0/…/countable/多维数组不支持） |
| 六次补全（官方文档核对） | `^n` = `length − n`（`^1` 末元素、`^0` = length：元素访问越界、Range 端点合法——`..` ≡ `0..^0`、`a..` ≡ `a..^0`、`..b` ≡ `0..b`，**已核**）；接收者须为 countable（有 int `Count`/`Length` 属性）；**多维数组不支持** Index/Range——`x[i, ^1]` 须判定拒绝；切片产生新集合（数组为拷贝）——「拷贝 vs subList 视图」差异的官方依据 |

#### C32 Lambda 可变捕获前置化（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `CaptureLift`（可变捕获 → 显式 holder） |
| 分类 | 1.4 / 闭包语义 |
| 目标 C# 模式 | 被 lambda/局部函数捕获且存在**外部再赋值**的局部与参数 → C# 侧显式单元素数组（或生成 holder 类）声明，全部读写点改 `holder[0]`（语义等价、可编译 C#） |
| 解决的转换难点 | 转换器内嵌整套捕获分析与运行时改写：`StatementTransformer.cs:68-82`（方法体级 `PreScanLambdaCaptures` 预扫描 + 参数 holder 前置发射）与 `:127,134`（第二份同构逻辑）、`Context/MethodConversionState.cs:314,442` 的捕获状态与 `PendingLambdaCaptureHolder`；与 E02 的捕获分析（`LocalFunctionLiftAnalyzer.cs`）重复建设；与 C01 的 lambda ref 参数 fail-fast（`LambdaTransformer.cs:99,200`）同一语义面 |
| 证据 | 同上；回归 `LambdaExternalReassignmentTests.cs`、`LinqRewriterVariableCaptureTests.cs` |
| 与现有通道关系 | 应在 E02 之后（复用其捕获分析基建）、C01 之后（ref 捕获与 ByRef 位置模型一致化）、C04 yield 之前（迭代器/lambda 共享捕获形态）。四次补全：登记的 **C45 是本通道的超集形态**（lambda → display class + 命名方法的全量 Roslyn lowering）；建议 C32 作为 C45 的第一阶段落地（只处理可变捕获 holder），不要建成两套捕获分析 |
| 优先级 | **P2**（现路径可用；价值 = 删除转换器内最难调试的隐式状态，统一与 E02 的两套捕获分析） |
| 状态 | 缺失（转换器内代偿） |
| 验证 | 中间树 build/test；fixture：lambda 内改捕获变量外部可见、多 lambda 共享同一变量、参数捕获、嵌套 lambda、与 E02 提升后的局部函数交互 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/lambda-expressions（捕获语义） |

#### C33 泛型型变与约束前置审计（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `VarianceConstraintNormalize` |
| 分类 | 1.1 类型系统 |
| 目标 C# 模式 | ① `out T`/`in T` 声明-site 型变：移除或改写为使用-site（`? extends/super`），并在 C# 侧校验型变使用点安全；② `where T : struct/unmanaged`、`new()` 约束显式化（剥离 + 构造点/装箱路径校验）；`notnull`/`default` 归一 |
| 解决的转换难点 | 型变声明被**直接丢弃仅告警**（`ClassTransformer.cs:183-192`、`InterfaceTransformer.cs:89-97`、`DelegateTransformer.cs:93-101`，诊断码 CS2J1003）——协变接口消费端在 Java 侧可能产生不被发现的违规写入；`struct`/`new()` 约束仅 `WarnConstraintDropped`（`ClassTransformer.cs:211-235`，L230/L232），`notnull`/`default` 静默忽略（L219-221） |
| 证据 | 同上 |
| 与现有通道关系 | 与 E10/C02 泛型族同批；定位是**审计型**通道（先做 C# 侧显式告警/拒绝 + 安全校验，再决定是否做使用-site 改写） |
| 优先级 | **P3**（丢失形态是「声明降级」而非编译失败） |
| 状态 | 缺失 |
| 验证 | fixture：协变接口作为生产者/消费者两侧使用、struct 约束泛型的 `default(T)`/装箱路径、new() 约束工厂 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/concepts/covariance-contravariance/（in/out 变型） |

#### C34 decimal 运算归一收尾（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `DecimalOps`（数值边界族规则；与 C15/C17 同通道或挂 E09 范式） |
| 分类 | 1.4 / 数值 |
| 目标 C# 模式 | decimal 上的 `+ - * / %`、比较、一元 `-`、`++/--`、`switch` governing、checked 溢出 → `io.github.ningpp.compat.Decimal` 显式方法调用（`add/subtract/multiply/divide/remainder/negate`），替代现保留的 `left op right` 原样输出 |
| 解决的转换难点 | 类型与字面量映射已就绪（`config/TypeMappings.json:3633-3639`；字面量 → `Decimal.parse("…")`，`LiteralExpressionTransformer.cs:83-84`），但二元路径仅补 import 后**原样保留 `+ - * /`**（`BinaryExpressionTransformer.cs:88-92`）——Java 无运算符重载，`Decimal` 引用上的 `+` 是**不可编译 Java**（静默错误输出）；一元与自增直接 fail-fast（`UnaryExpressionTransformer.cs:150-151`、`:154-193` 含 decimal 自增目标） |
| 证据 | compat `Decimal.java:261-314` 已备 `add/subtract/multiply/divide/remainder/negate` 实例方法——桥已建、C# 侧从未发射；设计 `docs/superpowers/specs/2026-06-04-csharp-decimal-design.md` |
| 与现有通道关系 | 形态完全复刻 E09「运算符→支撑库方法调用」样板；数值边界族（C15/C17）同通道；须在 E09 之后（无符号类化改变实参类型）；`++/--` 子项与 C17 合并实施 |
| 优先级 | **P2**（不可编译输出 + fail-fast 并存，方法面现成，边际成本低） |
| 状态 | 缺失（字面量/类型映射已就绪，运算层缺口） |
| 验证 | 中间树 build/test；fixture：四则/取模/比较、一元负号、`++/--`、decimal 常量字段、`ToString("F2")` 文化重载、switch on decimal |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/floating-point-numeric-types（decimal 节） |

#### C35 空条件 `?.` / `?[]` 去糖前置化（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `NullConditionalDesugar`（建议并入 E03 成为「null 糖归一」通道） |
| 分类 | 1.4 语法糖 |
| 目标 C# 模式 | `a?.b.c?.d()`、`arr?[i]`、`?.` 与 `??`/`??=` 组合 → 显式 null 检查语句/三元（保持接收者**单次求值**），归一到 E03 的产物形态 |
| 解决的转换难点 | 非失败型、但特判面最宽：`ControlFlowTransformer.TransformConditionalAccess`（`ControlFlowTransformer.cs:22,45,314+`）+ `ExpressionTransformerFacade` 4 处「所属 ConditionalAccess」owner 链回溯（`ExpressionTransformerFacade.cs:117-121,165-169,186-193,272`）——条件访问的类型解析贯穿整个表达式转换器；无 fail-fast（仅未知 kind 兜底，L56） |
| 证据 | 同上；E03 的两遍临时变量提升基建可直接复用 |
| 与现有通道关系 | E03 的姊妹规则；去糖顺序必须在 E03 之前或同通道内统一（两者改写会互相暴露中间形态） |
| 优先级 | **P3**（现路径成熟；收益 = 删掉 facade 的条件访问特判簇） |
| 状态 | 缺失（转换器内代偿） |
| 五次补全追加 | C# 14 **简单空条件赋值**（`a?.b = c` 及复合形态）补入本通道，单列 **C51**；E03+C35+C51 归一为完整「null 糖」通道后，Java 侧条件访问特判簇（facade 4 处 owner 链回溯）可整体退役 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/member-access-operators（**已核**：?./?[] 短路/单次求值） |

#### C36 C# 12 语法去糖（集合表达式/spread）（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `CSharp12Desugar`（集合表达式/spread/target-typed new；兼 C# 11/12 新语法防线） |
| 分类 | 1.8 表达式 |
| 目标 C# 模式 | 集合表达式 `[1,2,3]`（目标类型数组/List/IEnumerable）、spread 元素 `..e`、空 `[]` → 按 ConvertedType 显式构造：数组 `new T[]{…}`、`List<T>` 用**可变** `new List<T>{…}` |
| 解决的转换难点 | 现仅一条 delegate 规则：目标为数组产 `new T[]{…}`，否则兜底 `java.util.List.of(…)`（`ExpressionTransformerRegistry.cs:135-152`，`List.of` 在 L150）——`List.of` **不可变**，而 C# `List<int> l = [1,2]; l.Add(3)` 合法（静默语义差 → Java 运行时 `UnsupportedOperationException`）；spread 元素被 `OfType<ExpressionElementSyntax>()` **静默过滤丢失**（L139，全仓无 `SpreadElementSyntax` 处理）；E09 显式跳过集合表达式内部（`UnsignedTypeSyntaxRewriter.cs:447`），说明各 preprocessor 尚未把它当一等语法 |
| 证据 | 同上；测试仅 `LinqRewriterVariableCaptureTests.cs:11`（变量捕获形态）一处 |
| 与现有通道关系 | primary constructor 已由 E08 覆盖（`DefaultParameterDeclarationRewriter.cs:165,178-204`、`DefaultParameterOverloadBuilder.cs:140`），不列入；C# 11/12 其余新语法（`static abstract` interface members、`scoped`、泛型 attribute）无任何专项处理证据——本通道兼任「新版本语法盘点 + 显式拒绝」防线（见 §4.13） |
| 优先级 | **P3**（spread 丢失是数据级 bug，置于 P3 首位） |
| 状态 | 部分（窄规则已有；spread/可变语义缺口） |
| 验证 | 中间树 build/test；fixture：`List<int> l = [1,2]; l.Add(3)`、`[.. a, x]`、嵌套 `[[1],[2]]`、空 `[]`、IEnumerable 目标 |
| 三次补全追加 | 盘点时已核实**不在本通道**的同族语法：空宽容 `x!` 已被透明剥离（`UnaryExpressionTransformer.cs:54`，无需前置）；`#nullable`/`#pragma` 活动指令由 E01 **保留**（白名单口径见 E01 目标模式行）。原列「the C# 11/12 新语法防线」职责保持不变（`static abstract`/`scoped`/泛型 attribute 全仓 grep 无命中，维持「先盘点 + 显式拒绝」）；成员级静默丢弃（终结器 `~T()`）单列 **C39** |
| 四次补全追加 | 「cs2j 仓 grep 无命中」只说明本转换器源码不使用，**不代表输入不使用**：sdk 画像中 json/json-codegen 已使用接口 `static abstract/virtual` 成员（方向性输入 Layer 1，Java 无此特性，需 companion 模式，是独立难点而非仅拒绝项）；EF Core 画像扫描（§5.1）后须复核本防线优先级。另：`scoped` 与 C06 的 ref struct 子项同族 |
| 五次补全追加 | 防线范围升格：**C# 11/12 → C# 11+（含 13/14）**——`allows ref struct` 反约束、隐式 Span 转换（重载决议变化）、unbound `nameof`、partial 构造器、`System.Threading.Lock`、`\e`、DIM 补盘等的「盘点 + 显式拒绝」职责见伞条目 **C52**；子项按家族拆入 C06（delegate*）/C29（Lock）/C10（attribute 目标限定符、`[OverloadResolutionPriority]`）/C20（partial 构造器/属性/事件）/C23（const 内插）/C43（`\e`） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/collection-expressions（**已核**：目标类型五类/with(...)/inline array 限制）；https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-12 |
| 六次补全（官方文档核对） | 集合表达式目标类型官方清单（**已核**）：Span/ReadOnlySpan、数组、带 create 方法的类型（`[CollectionBuilder]`）、支持集合初始化器类型（IEnumerable+Add）、五个接口（IEnumerable/IReadOnlyCollection/IReadOnlyList/ICollection/IList）；**不能初始化 inline array**；**`with(...)` 元素（C# 14，本条目补盘）**——`[with(StringComparer.OrdinalIgnoreCase), "A", "B"]` 向 create 方法传构造实参，物化为显式工厂调用；目标为接口时具体集合类型由编译器选择——物化须钉死具体类型（与 `List.of` 不可变性问题同源） |

#### C37 模式表达式去糖（`is` 表达式 / switch 表达式臂 / 位置与列表模式）（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `PatternDesugar`（C03 的表达式侧姊妹规则；建议与 C03 单通道双入口，共用同一「模式→布尔表达式」归约器） |
| 分类 | 1.3 控制流（表达式） |
| 目标 C# 模式 | 模式匹配在**表达式位置**的全部形态 → 普通布尔表达式/语句前置：① 关系/逻辑模式 `x is > 0`、`x is >= 0 and <= 10`、`x is 1 or 2` → 比较与 `&&`/`||`；② 否定 `x is not null`、`x is not C` → `!= null`、`!(x instanceof C)`；③ 常量 `x is 1` → `x == 1`；④ 声明模式（条件中绑定且被使用）→ `x is C` + 段首 `C c = (C)x;`（泛型已是 `JavaGenericPatternRewriter` 的既有形态，须推广到非泛型并覆盖表达式位置）；⑤ 属性/递归模式 `x is { P: > 5 }` → `x != null && x.P > 5`（嵌套展开）；⑥ 位置模式 `p is (1, 2)`、`x is Point(1, _)` → 元组字段/E06·E07 命名契约上的比较；⑦ 列表模式 `x is [1, .., n]` → 长度+索引比较（或显式拒绝）；⑧ switch 表达式臂复用同一归约器（含 `when` 守卫） |
| 解决的转换难点 | ① `is` 表达式今天仅 3 条分支（DeclarationPattern、`is null` 常量、Discard），**其余全部兜底为 `/* TODO: unsupported pattern */ true`**（`TypeOperationTransformer.cs:107-117`，L115）——条件被替换成恒真 `true`，语义完全丢失（静默错误输出）；② switch 表达式臂覆盖较多（`ControlFlowTransformer.cs:474-507`）但未知模式落 `_ => $"/* TODO: pattern {…} */ true"`（L505，`ListPatternSyntax` 等无分支），递归模式只桥接属性子模式 getter（`:521-561`），**位置子模式无处理**；③ 实测证明缺口是系统性的：`PatternMatchingTests` **8/11 失败**（关系/逻辑/Or/常量/否定/not-type/声明模式使用/属性模式用例），且 `PropertyPattern_ConvertsWithTodoMarker` 期望的 `/* TODO: recursive pattern */` 文本在 src 全仓已不存在——实现与测试预期双向脱节 |
| 证据 | `TypeOperationTransformer.cs:107-117`（`IsPattern` 三分支与 L115 兜底）；`ControlFlowTransformer.cs:474-507`（`BuildSwitchArmCondition` 与 L505 兜底）、`:521-561`（属性子模式桥接）；`JavaGenericPatternRewriter.cs:110-129`（泛型声明模式提升——「instanceof + 强转声明」形态已被生产路径接受）；测试 `tests/CSharpToJava.ConversionTests/Categories/PatternMatchingTests.cs`（2026-09-28 `-c Release` 实测 8/11 失败）、`PatternBulkTests.cs` |
| 与现有通道关系 | 与 C03 是同一特性两侧：C03 = **语句**侧（pattern switch/`when`/catch filter），C37 = **表达式**侧（`is` 表达式/switch 表达式臂）；只做一侧则模式用例仍红，建议同批交付。须在 E06/E07（元组字段与解构命名契约）之后；改写引入的模式变量提升与 `JavaGenericPatternRewriter` 的既有提升**会争抢同一语法面**，实施时须合并为一条通道或明确先后（开放问题，见 §4.15） |
| 优先级 | **P1**（恒真占位是典型静默错误输出，测试红灯已持续；归约形态机械、可编译性易保持） |
| 状态 | **已实现（与 C03 同批，2026-09-30 核实）**：表达式侧 is-pattern/switch 表达式臂归约已在 `PatternDesugar/PatternReducer.cs`+`PatternDesugarComponents.cs` 落地，随 pattern-desugar 翻转（1b67b823，catalogItems 即 `C03, C37`）；PatternDesugarCoreTests 表达式矩阵 + SamplePatternSolution 端到端全绿 |
| 验证 | 中间树 build/test；以 `PatternMatchingTests` 11/11 全绿为门槛；fixture 覆盖关系/逻辑/否定/常量/属性/递归/位置/列表/元组模式、`is` 出现在 if/while/三元/return/实参处的绑定变量使用、switch 表达式 + `when` 组合 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/is；https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/functional/pattern-matching（模式全集） |

#### C38 异步流与异步释放归一（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `AsyncStreamNormalize`（C18 姊妹规则，建议并入同一异步通道） |
| 分类 | 1.5 异步 |
| 目标 C# 模式 | ① `await foreach (var x in source)` → 显式 `IAsyncEnumerator<T>` 驱动循环（`while (await e.MoveNextAsync())`）的同步可表达形态（或按 C18 形态①/②归一）；② `await using` / `IAsyncDisposable` → `using`/`IDisposable` 形态或显式 `await DisposeAsync()`；③ `IAsyncEnumerable<T>`/`IAsyncEnumerator<T>`/`IAsyncDisposable` 类型使用点归一（映射或显式拒绝） |
| 解决的转换难点 | 两个语句转换器**均不检查 `AwaitKeyword`**：`await foreach` 被按同步 foreach 处理（`StatementTransformer.Loops.cs:104-155` 全函数无该关键字判定；全仓 Core 仅 `TupleToStructPreprocessor.cs:1344`、`ForeachDeconstructionRewriter.cs:89` 两处提及且均为保留原文）；`await using` 被降为 try-with-resources（`StatementTransformer.SwitchAndResource.cs:198-244` 同款缺失）。类型面同样空白：`config/TypeMappings.json` 无 `IAsyncEnumerable/IAsyncEnumerator/IAsyncDisposable`（grep 无命中），`java/csharptojava-compat` 无任何异步序列类（grep 无命中）——命中时产物为不可编译 Java 或语义丢失 |
| 证据 | 上述行号；`docs/superpowers/specs/2026-07-02-csharp-collection-compat-design.md:12` 明确把 async interfaces（IAsyncEnumerable/IAsyncEnumerator）列为 Out（文档化范围外）；`tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs:162-181`（E07 保留 `await foreach` 关键字——异步 foreach 会原样流入主转换器）；测试簇 `StreamAsyncTokenTests.cs` |
| 与现有通道关系 | C18 的直接补全：C18 管 async **方法体**，C38 管异步**序列与释放**；两者共享「异步形态归一」选型（轻/重），必须在 C18 定案后同批实施，避免两套异步语义。与 C16/C40 的 foreach 通道在插入点上相邻（都改写迭代源） |
| 优先级 | **P2**（静默语义丢失 + 类型/兼容层空白；命中率低于 plain async，全量降级成本与 C18 同量级） |
| 状态 | 缺失（语句层静默降级 + 类型映射与 compat 双空白） |
| 验证 | 中间树 build/test；fixture：`await foreach`、`await using`、异步迭代器方法（`yield return` 于 `async IAsyncEnumerable`）、`IAsyncEnumerable<T>` 参数/返回值、`ConfigureAwait` 组合 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/generate-consume-asynchronous-stream（await foreach/异步迭代器/WithCancellation） |

#### C39 终结器（`~T()`）消除（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `FinalizerStrip`（或作为 C11 成员去糖的成员删除规则；建议显式拒绝而非静默删除） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | 终结器 `~T() { … }` → 显式删除 + 诊断（或按选型降级为 Java `finalize`/`Cleaner` 的 C# 可表达形态/Dispose 引导） |
| 解决的转换难点 | `ClassTransformer.ProcessMember`（`ClassTransformer.cs:317-381`）的成员 switch **既无 `DestructorDeclarationSyntax` 分支也无 default**——终结器被**静默丢弃**：无诊断、无注释、无占位；`Transformers/` 下全仓 `DestructorDeclaration` 仅 `LambdaTransformer.cs:306` 一处命中，且只服务 lambda 可见名分析，不产出成员 |
| 证据 | 上述行号（grep 复核） |
| 与现有通道关系 | 与 C11–C14 同属成员级规则；若采用「显式拒绝」策略可直接并入 C36 的「语法盘点 + 显式拒绝」防线。与 C07 的 Dispose/资源释放无直接关系（终结器是非确定性回收语义） |
| 优先级 | **P3**（静默丢弃属资源语义丢失，但不立即产生错误产物；命中率低） |
| 状态 | 缺失（静默丢弃，无任何信号） |
| 验证 | fixture：带终结器的类型（含继承链上的终结器）；断言中间树形态符合选型、产物无残留、且给出诊断 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/finalizers |

#### C40 foreach 迭代源二期：枚举器适配 / ref foreach（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `ForeachSourceNormalize`（扩展 E07/C16 通道） |
| 分类 | 1.3 控制流 |
| 目标 C# 模式 | ① 模式化 foreach（duck-typed `GetEnumerator`、显式 `IEnumerator`/`IEnumerator<T>` 实现，不实现 `IEnumerable`）→ 编译器 lowering 形态：`var e = src.GetEnumerator(); while (e.MoveNext()) { var item = e.Current; … }`（`GetEnumerator` 扩展方法形态同理）；② `ref`/`ref readonly` foreach（`foreach (ref var x in …)`）→ 显式索引/Copy 形态；③ 与 C38 的 `await foreach` 共用同一插入点与迭代源判定 |
| 解决的转换难点 | 转换器注释自述必须前置却没有对应通道：`StatementTransformer.Loops.cs:99-103`「Strings, dictionaries, LINQ streams, **enumerator adapters** and deconstructions must be lowered by a preprocessor」——strings/dictionaries/LINQ 已有 E04/E12/E16 分担，**enumerator adapters 无任何 E/C 条目覆盖**；`Loops.cs:104-155` 的 `RefKeyword`/`AwaitKeyword` 均未检查，ref foreach 静默按值语义处理。E07 只处理 KeyValuePair/Deconstruct 族（`ForeachDeconstructionRewriter.cs:67-68` 对 Tuple 显式 skip 依赖 E06） |
| 证据 | 上述行号；与 C16 的 string foreach（`Loops.cs:121` 硬拒绝）互为同族证据 |
| 与现有通道关系 | 与 C16 同通道实施（都属「迭代源显式化」）；排在 E06/E07 之后（元组/解构源已规范化）；与 C38 一起构成 foreach 完整规则集（同步源/异步源/ref 源/解构源） |
| 优先级 | **P2**（非 IEnumerable 源在 Java 侧无编译映射；ref foreach 静默错值） |
| 状态 | 缺失（窄） |
| 验证 | 中间树 build/test；fixture：duck-typed 枚举器类、显式 IEnumerator 实现、`GetEnumerator` 扩展方法、`ref`/`ref readonly` foreach、与非泛型集合/数组组合 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/foreach-in（**已核**：模式 GetEnumerator 可为扩展方法/ref 迭代变量/await foreach 模式） |
| 六次补全（官方文档核对） | foreach 官方规则（**已核**）：模式化枚举要求 public 无参 `GetEnumerator` + public `Current` 属性 + 返回 bool 的无参 `MoveNext`——**GetEnumerator 可为扩展方法**（语言规范专节，C# 14 extension 块同构）；`ref`/`ref readonly` 迭代变量仅当 `Current` 为 ref 返回（Span 族）；await foreach 同构模式：`GetAsyncEnumerator`（可为扩展成员）+ `MoveNextAsync` 返回可 await 的 bool + 可选 ConfigureAwait/WithCancellation/[EnumeratorCancellation]；null 集合抛 NRE |

#### C41 特殊数值类型归一（Half / Int128 / UInt128 / nint / IntPtr）（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `SpecialNumericType`（扩 E09 数值族；UInt128/nuint 与无符号族同源） |
| 分类 | 1.1 类型系统 / 数值 |
| 目标 C# 模式 | `Half`、`Int128`、`UInt128` 的类型使用点与运算 → 按选型降级（`Half` → `float` 或未来 value class `Float16`；`Int128` → `java.math.BigInteger` 或自定义 value class；`UInt128` → 无符号族扩展）**或显式拒绝**（默认策略建议后者）；四次补全追加 **`nint`/`nuint`/`IntPtr`/`UIntPtr`**（平台尺寸整数：32/64 位宽度依赖 ABI，Layer 5 项；与 E09 无符号族、C06 指针族双向交界） |
| 解决的转换难点 | `config/TypeMappings.json` 无 `Half`/`Int128`/`UInt128`（grep 无命中）；`java/csharptojava-compat` 无对应类（`BitOperations` 也没有；grep 无命中）；`doc-valhalla/04-float16分析.md`、`06-对cs2j的影响与建议.md` 已做 Half 分析（建议 `Half→float`、未来 Float16 value class）但未落映射。命中时走「未映射类型」路径，产物不可编译或类型错误（对照：`DateOnly/TimeOnly/Guid` 均已映射，`TypeMappings.json:184,191,205`） |
| 证据 | 上述 grep 结果与文档；ROADMAP 未列（不在十项内） |
| 与现有通道关系 | E09 的边界扩展；与 C34 decimal 同属「特殊数值显式化」；若采用显式拒绝策略并入 C36 防线（与 C15 的 `sizeof` 占位联动——`TypeOperationTransformer.cs:155-164` 对非内置类型直接产 `/* sizeof */`） |
| 优先级 | **P3**（观察项；命中率取决于下游库是否使用 .NET 7+ 数值类型） |
| 状态 | 缺失（无映射、无 compat、无显式拒绝） |
| 验证 | fixture：`Half`/`Int128`/`UInt128` 声明与运算、`Int128.Parse/MaxValue`；断言按选型映射或显式报错 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types（nint/nuint）；https://learn.microsoft.com/en-us/dotnet/api/system.half；https://learn.microsoft.com/en-us/dotnet/api/system.int128 |

#### C42 平台 / 互操作边界前置隔离（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `PlatformBoundaryIsolation`（诊断已存在、隔离通道缺失） |
| 分类 | 1.8 其他 / 边界 |
| 目标 C# 模式 | ① WinForms/XAML/Maui 命名空间族；② `Registry`/`EventLog`/`SerialPort`/`ManagementObject`/OS 探针（`OperatingSystem.Is*`、`RuntimeInformation.*`）；③ 互操作（`Marshal`、`SafeHandle`、`NativeLibrary`、`DllImport`/`LibraryImport`、`MarshalAs`）→ 在进入主转换**之前**隔离：替换为桩/抽象接口/显式抛错，或按既有诊断显式拒绝 |
| 解决的转换难点 | 三类 analyzer 已产诊断且措辞就是「隔离」要求：`UnsupportedDomainAnalyzer`（CS2J3001，`UnsupportedDomainPasses.cs:10-18` 黑名单、`:44` 消息）、`PlatformBoundaryAnalyzer`（CS2J3101/3102，`:106-129` 前缀表、`:146-153`、`:163-172`）、`NativeInteropAnalyzer`（CS2J3201-3203，`:245-251`、`:265` 属性表）——CS2J3101/3102 消息为「must be isolated before Java conversion」（L151/L168 等），CS2J3001（L44）与 CS2J3201-3203（L270 等）消息为「is not supported by the Java-only conversion pipeline」；但清单此前**没有通道承接「隔离」动作**（诊断只拦截错误输入，批量自动化隔离无从下手）。另存在行为矛盾：`ClassTransformer.cs:346-350` 对 extern/DllImport 抛异常，而 `UnsupportedDomainPasses.cs:379-381` 的 `IsPInvokeContainerFile` 注释称 ClassTransformer 会为 extern 发 stub（见 §4.16） |
| 证据 | 上述行号；`ClassTransformer.cs:348`（`IsExternLike`） |
| 与现有通道关系 | 与 C06（unsafe/FFM）是外部契约的两面：C06 改造指针/固定缓冲与调用形态，C42 负责边界的「隔离/拒绝」策略；与 C10（attribute 归一）在 P/Invoke 属性上有交集；与 C36 的「显式拒绝」防线共享诊断基建 |
| 优先级 | **P3**（现有诊断已阻止静默产坏码，通道价值在批量隔离自动化与诊断信息保留） |
| 状态 | 缺失（诊断存在，无隔离通道；extern 行为矛盾待澄清） |
| 验证 | fixture：WinForms using、Registry 访问、`DllImport` 声明、OS 探针；断言转换前显式报错（或隔离后产物无平台类型残留） |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/extern（DllImport/LibraryImport/extern alias） |

#### C43 词法层字面量归一（raw string / u8 / verbatim 组合）（P3，观察项）
| 字段 | 内容 |
|------|------|
| 名称 | `LexicalLiteralNormalize`（Layer 0；与 E01/C28 同属「纯语法批只 parse 一次」候选） |
| 分类 | 1.7 编译指令 / 词法 |
| 目标 C# 模式 | raw string `"""`（单行/多行）→ 普通字符串字面量（含转义归一）；`"x"u8` → 显式字节数组初始化；`@""`/`$@""`/`@$""` 组合 → 常规字面量（插值部分交 C23）；产物为不含新词法形态的可编译 C# |
| 解决的转换难点 | 方向性输入（sdk raw string **1345 处**，称「最高性价比的第一个 pass」）建议在词法层消除；**但 2026-09-28 核实这些形态转换器已全部内联吸收，并非缺口**：raw string → Java 15+ text block（`"""…"""`），低版本回落转义普通串（`LiteralExpressionTransformer.cs:233-245`）；`u8` → `new byte[]{ (byte)0xXX, …}`（`:288-294`）；verbatim `@`/`$@`/`@$` → 转义普通串（`:247-255`）。按 §5.3 分层判据（局部 1:1、不引入声明）可留 Java 层——现状已符合 |
| 证据 | 上随行号；本候选的唯一驱动力是 §5.2 CsCore 收敛（让主转换器输入不再含版本/词法条件分支）与统一引擎合批，而非正确性 |
| 与现有通道关系 | 若做，必须是预处理链**最前环之一**（E01 之后、一切语义 pass 之前），幂等；raw **插值**字符串 `$"""` 与 C23 的 handler 判定划界；产物形态需让低 Java 版本目标也不依赖 text block（把版本条件从转换器移到 C# 层决策） |
| 优先级 | **P3（观察项）**——现状无错误输出；仅在统一引擎落地、或需要「目标 Java 版本无关的中间树」时实施 |
| 状态 | 不缺（转换器内联吸收中）；无独立通道 |
| 验证 | 若做：中间树 build/test；fixture：多行 raw、含引号/换行 raw、u8 非 ASCII、`$@` 组合、raw 插值 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/tokens/raw-string；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/reference-types（u8 字面量节） |

#### C44 using 显式化（using 声明/语句 → try/finally）（P3，观察项）
| 字段 | 内容 |
|------|------|
| 名称 | `UsingDesugar`（Layer 3；实验管线 `Lowering/LowerUsing.cs` 为形态证据） |
| 分类 | 1.3 控制流 |
| 目标 C# 模式 | ① using 声明 `using var x = …;` → 包裹剩余作用域的 try/finally + `Dispose()`；② using 语句（含堆叠）→ 同形；③ ref struct / 非 `IDisposable` 模式（`Span`、自定义具备 `Dispose` 的 ref struct）的释放显式化；④ 与 C04（yield 内 using）、C38（`await using`）衔接 |
| 解决的转换难点 | 方向性输入 Layer 3 列为降级项；**2026-09-28 核实同步形态已内联吸收**：using 语句直映 try-with-resources，堆叠 using 收集为单一 resource 列表（`StatementTransformer.SwitchAndResource.cs:198-244`，`TransformUsingStatement:202`）；using 声明由语句级预扫描收集连续资源（`StatementTransformer.cs:168-170,242-244,343,444-490`，`BuildUsingDeclarationResources` 在 `SwitchAndResource.cs:246`）。真正缺口仅：ref struct disposables 不能进 try-with-resources 类型面（与 C06 同族）、`await using`（C38）、以及 yield/using 的 Dispose 时序（C04 原型子题） |
| 证据 | 上随行号；实验形态 `Lowering/LowerUsing.cs`（仅 Java2 测试可达，§0.3） |
| 与现有通道关系 | 不单独成链；其有效子规则分别归 C06（ref struct 释放）、C38（异步释放）、C04（yield+using）。CsCore 全量收敛时再把同步形态也搬到 C# 层 |
| 优先级 | **P3（观察项）**——同步 using 现路径语义正确，前置仅为收敛输入面 |
| 状态 | 不缺（同步形态内联）；ref struct/异步/yield 组合缺口登记在 C06/C38/C04 |
| 验证 | 若做：中间树 build/test；fixture：using 声明跨 return/异常、堆叠 using、ref struct 释放、迭代器内 using |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/using（**已核**：using 语句/声明 → try-finally 语义） |

#### C45 Lambda 闭包提升（lambda/匿名方法 → display class + 命名方法）（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `ClosureLift`（Layer 4 重量级 pass；C32 的超集，E02 的姊妹通道） |
| 分类 | 1.4 / 闭包语义 |
| 目标 C# 模式 | 按 Roslyn 编译器自身的 lowering：lambda/匿名方法（sdk **4966 处**，训练集第一高频糖）→ ① 生成 display class（静态 lambda/无捕获不生成）；② 捕获变量（含 `this`）提升为 display class 字段，ref 捕获显式判负；③ lambda 体提升为 display class 上的命名方法；④ 委托点改写为 `new Action<>(display实例)` / `new Func<>(…)` 形态；⑤ 子项：discard `_` → 真实临时参数、lambda 上 attribute 擦除/转注解（与 C10 交界）、静态 lambda 无类 |
| 解决的转换难点 | 今天 lambda 直映 Java lambda（可用但边角连续 fail-fast/隐式状态）：async lambda 直接拒绝（`LambdaTransformer.cs:52`）、ref 参数 fail-fast（`:99,200`）；方法体级捕获预扫描 + holder 前置发射（`StatementTransformer.cs:68-82,127,134`）、`MethodConversionState.cs:314,442` 的 `PendingLambdaCaptureHolder`；递归 lambda、多 lambda 共享捕获、捕获后被 E16 LINQ 过程式化暴露等形态分散特判。全量提升后这些统一在 C# 层可见、可被 .NET 测试验证 |
| 证据 | 上随行号；E02 已有成体系的捕获分析/命名基建（`LocalFunctionLift/` 10 文件，Analyzer/Plan/Naming/Rewriter）可直接复用形态；E05 已完成匿名方法 → lambda 的前半步；C32 已登记可变捕获 holder 子集 |
| 与现有通道关系 | 在 E02（局部函数先出方法体）、E05、C01（ByRef 位置模型）、C32（建议作为本 pass 第一阶段）之后；**必须在 C04 yield/C18 async 状态机之前**（捕获要先成为显式字段，状态机才知道保存什么）；与 E02 构成 fixpoint 环（lift 局部函数产生 lambda 捕获、lambda 提升可能再产生局部函数形态，§5.4）；LINQ 表达式树内的 lambda **跳过**（§4 风险 8、C26 三路分流） |
| 优先级 | **P2**（频率第一、收益大；但属生成新类型的重量级 pass，需按「无捕获静态方法 → 仅 this → 值捕获 → 可变捕获(C32) → 嵌套/递归」分阶段） |
| 状态 | 缺失（转换器直映 + holder 隐式状态代偿中） |
| 验证 | 中间树 build/test；fixture：无捕获/静态 lambda、捕获局部并外部再赋值、多 lambda 共享、this 捕获、嵌套与自递归、async lambda（与 C18 联合）、表达式树内 lambda 保持不动 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/lambda-expressions（**已核**：C# 14 简单 lambda 参数修饰符节） |
| 六次补全（官方文档核对） | **简单 lambda 参数修饰符**（C# 14 补盘项，**已核**）：参数修饰符 `scoped`/`ref`/`in`/`out`/`ref readonly` 可用于**省略参数类型**的 lambda（`TryParse<int> parse1 = (text, out result) => …`；此前修饰符必须伴随显式类型列表）；`params` 修饰仍需显式类型——本通道物化时统一改写为显式类型形态；ref/out 参数捕获与 C32 holder 衔接 |

#### C46 成员签名显式化（显式接口实现 / new 隐藏 / 协变返回）（P3）
| 字段 | 内容 |
|------|------|
| 名称 | 并入 **C11–C14 成员去糖族**（`MemberDesugar` 的签名规则，不新建通道） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | ① 显式接口实现 `void I.Foo()`（方法/属性/索引器/事件）→ 显式命名成员 + 接口映射声明；② `new` 隐藏成员 → 不依赖隐藏语义的显式形态（改名或桥接方法）；③ 协变返回（override 返回更派生类型）→ Java 可原生协变处保留，擦除冲突处显式桥接；④ 接口静态抽象/虚拟成员的 companion 形态（难点，暂归 C36 防线，本条不吞） |
| 解决的转换难点 | 现状仅在去重键里**识别**显式接口（`ClassTransformer.cs:303-308` 的 `MemberKey` 拼入 `ExplicitInterfaceSpecifier`），属性/索引器/事件各有 1–4 处零散分支（`PropertyTransformer.cs`、`IndexerTransformer.cs`、`EventFieldTransformer.cs`），**方法的显式接口实现无专门改写**（MethodTransformer 无 ExplicitInterfaceSpecifier 命中）：无碰撞时恰好能编译，碰撞/泛型重实现场景静默出错；主路径相关零散处理应按方向性输入收口到 pass |
| 证据 | 上随行号（2026-09-28 grep 复核）；测试面 `ExplicitInterfacePropertyConflictTests.cs`、`InterfaceIndexerReturnTypeTests.cs`、`InterfaceEventListenerNamingTests.cs` |
| 与现有通道关系 | 与 C11–C14 同一通道、同一 SemanticModel 符号表；被 C08/E17 改名表覆盖；须晚于 C20（partial 合并后签名才完整） |
| 优先级 | **P3**（多数形态巧合可用；价值在碰撞场景正确性与转换器收口） |
| 状态 | 缺失（零散识别/分支，无系统改写） |
| 验证 | 中间树 build/test；fixture：显式接口方法碰撞、同名不同接口、`new` 隐藏跨虚调用、协变返回 + 泛型擦除组合 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/new-modifier（new 隐藏）；https://learn.microsoft.com/en-us/dotnet/csharp/language-proposals/csharp-9.0/covariant-returns（协变返回提案）；https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/interfaces（显式接口实现） |

#### C47 相等性与字符串拼接调用点语义物化（string `==`/`!=`/`+`、tuple、Nullable 提升）（P1，先核实）
| 字段 | 内容 |
|------|------|
| 名称 | `EqualityConcatCallSite`（C14 声明侧的调用侧互补规则） |
| 分类 | 1.4 / 数值与相等性 |
| 目标 C# 模式 | 按操作数**语义类别**（SemanticModel 判定）而非语法形态物化：① string `==`/`!=` → `string.Equals(a, b)` / `!string.Equals(a, b)`（静态两参重载，null 操作数语义与 C# `==` 一致）；② 任一操作数静态类型为 `object` 的比较（含 `(object)a == b` 逃逸写法）→ `ReferenceEquals(a, b)`；③ string `+`（含多操作数链与 `+=`）→ `string.Concat(a, b[, c…])`——**C# 把 null 操作数当空串、Java `+` 产 `"null"` 字面量**，Concat 物化即编译器自身 lowering 同形；④ `bool?`/`Nullable<T>` 提升比较（与 C22 交界）；⑤ 元组 `==`（C# 9 字符串元组特例 `(a, b) == ("x", "y")`）→ 逐元素 Equals；⑥ 委托 `==` → **调用列表值相等**（六次补全修正：官方 equality-operators「Delegate equality」——同为 null，或调用列表**等长且逐项相等**（方法 + 接收者；多播 `(a+a)==(a+a)` 为 true 而 `ReferenceEquals` 为 false））→ `DelegateOps.equals(a, b)` 物化，Java `==` 同一性直映**错值**；⑦ **record `==`**（六次补全新增：record class/struct 的 `==` 为**值相等**——全部字段+自动属性逐项比较，官方「Record types equality」）→ 与 E11/C27 衔接，直映 `==` 错值 |
| 解决的转换难点 | 语言层经典**静默错值**双源：`(==)` C# 按静态类型做重载决议、string 绑定值相等运算符，Java `==` 对引用恒为同一性——直译后字面量比较因驻留侥幸通过、运行时构造串（Concat/ToString/Span→string）错值；`(+)` null 语义差同理。本清单 §4.12「静默错误输出」家族此前未收录，属五次补全语言覆盖审计新发现 |
| 证据 | 语言层：C# 语言规范运算符重载决议与 `+` 的 Concat lowering；Java 语言规范 `==` 同一性与字符串拼接 null 转换。**仓库现状待核实**（本条第一行动）：`BinaryExpressionTransformer` 已按操作数类型分派（C14 条目证据 `:133-144` 对 enum 操作数硬拒绝说明存在类型分派层）——string 分支是否存在、是否覆盖 `!=`/`(object)` 逃逸/`+` 的 null 语义/提升比较，需 grep 分派逻辑并跑「运行时构造等值串」「null + "a"」fixture；**若均已内联则本条降为观察项**（CsCore 收敛价值仍在：物化后 Java 侧删掉整个特判簇）。**2026-09-30 核实门结论（详见 C47 设计 §4.1）**：判据是 `GetSymbolInfo().Symbol` 而非公开 `BinaryOperatorKind`（Roslyn 4.12 五类相等公开 kind 全是 Equals/NotEquals、拼接是 Add）；string `==`（→`Objects.equals`，`BinaryExpressionTransformer.cs:96-104`）、`bool?` 特判、元组**已内联**；**四子面未覆盖、静默错值/不可编译**：`(object)` 逃逸比较、string `+`/`+=` null-as-empty、委托 `==` 调用列表值相等、record `==` 值相等；本条**不降观察项，按 P1 补全** |
| 与现有通道关系 | C14 管用户自定义 `op_*` 声明与调用点，本条管**内置类型上编译器隐式绑定**的调用点，两者拼满分派面；`+` 物化与 E03（`??`）、C23（插值→`string.Format/Concat`）产物同族；enum `==` 预期已被 E04/转换器吸收；LINQ 表达式树内跳过（§4 风险 8） |
| 优先级 | **P1**（核实门：已内联→观察项收档；未覆盖→与 C31/C34 同列 §4.12 最高危静默错误输出并立即补，见 §4 风险 21） |
| 状态 | **部分覆盖、需补**（2026-09-30 核实结论见设计 §4.1）：已内联 string `==`/`bool?`/元组（双侧 `bool?` 缺测试护栏）；`(object)` 逃逸、拼接 null 语义、委托值相等、record `==` 四子面静默错值/不可编译，按 P1 完整物化。**注册表占位已补（2026-09-30 第十次更新）**：stub id `equality-concat`（RunAfter member-desugar），启用时 fail-fast CS2J9001 直到实现 |
| 验证 | 核实行动见「证据」行；fixture：`new string("a".ToCharArray()) == "a"`、`!=`、`(object)s == t`、`null + "a"`（C# 得 "a"、Java 直译得 "nulla"）、多段 `+=`、`bool?` 三值、`(a,b)==("x","y")`、**委托值相等**（`(a+a)==(a+a)` 为 true；不同接收者的同方法 `o1.M==o2.M` 为 false）、**record `==`**（同值两实例为 true）；物化后中间树 build/test + Java 侧同语义用例 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/equality-operators（**已核**：字符串序数相等/委托调用列表相等/元组相等/record 值相等）；https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/addition-operator（**已核**：null→空串） |
| 六次补全（官方文档核对） | 官方 equality-operators/addition-operator 已核：① string `==` 为**大小写敏感的序数（ordinal）比较**（Java `String.equals` 同为序数——物化面干净）；② string `+` 任一操作数 null → 空串替换（官方原文「the string representation of null is an empty string」）；③ **委托 `==` = 调用列表值相等**（⑥ 原表述「引用比较」已修正）；④ 引用类型默认 `==` 为同一性**但可重载**（C14 运算符物化后仍需调用点绑定分类，`object` 静态类型除外）；⑤ 元组 `==` 逐元素；⑥ record `==` 值相等（新增 ⑦） |

#### C48 params 集合显式化（C# 13 params collections / params 调用点展开）（P2）
| 字段 | 内容 |
|------|------|
| 名称 | `ParamsCollections`（E08 参数族姊妹规则，可并入同一通道共享两遍范式） |
| 分类 | 1.4 成员/调用点 |
| 目标 C# 模式 | ① C# 13 `params` 集合（六次补全按官方 params 页精确化——**合法类型**：一维数组 `T[]`；`Span<T>`/`ReadOnlySpan<T>`；带 create 方法的类型（`[CollectionBuilder]`）；支持集合初始化器的类型（`IEnumerable<T>` + 可访问实例 `Add`——`List<T>` 即经此规则，**不含 StringBuilder**）；五个接口 `IEnumerable<T>`/`IReadOnlyCollection<T>`/`IReadOnlyList<T>`/`ICollection<T>`/`IList<T>`），调用点 `M(1, 2, 3)` 物化为显式集合构造 `M(new T[]{ 1, 2, 3 })`（Span 形态与 C06 选型衔接；实参形态三类：逗号列表/单个集合/空）；② 经典 `params T[]` 与**集合表达式组合**（`M([1, 2])`、`M(..span)`）的调用点统一为显式数组实参（与 C36 划界：C36 管目标类型推断，本条管 params 形态面）；③ 声明侧 params 修饰符语义登记（空实参 `M()` 合法、`IEnumerable` 展开语义） |
| 解决的转换难点 | Java varargs 仅一维数组末位参数，`params ReadOnlySpan<T>` 等集合形态无 Java 对应；E08 只处理默认值，**params 从未进入任何 E/C 条目**——五次补全语言审计差集发现（C# 13 特性全集 vs 本清单） |
| 证据 | 语言层：C# 13 params collections（官方 What's new in C# 13）；.NET 10 BCL（既定转译目标 Collections/Text/Xml 基线）公共 API 已使用 `params ReadOnlySpan<T>`。**仓库/训练集现状待核实**：D:\csharp-sdk（.NET 8/9 时代源码）预期低频；`ArgumentTransformer`/`ExpressionTransformerRegistry` 的 params 展开路径需 grep |
| 与现有通道关系 | 并入 E08 通道（「Collect 签名目录 → Strip + 改调用点」两遍范式直接复用）；Span 形态依赖 C06 选型先行；C36 集合表达式的调用点组合依赖本条先定 params 面貌 |
| 优先级 | **P2**（.NET 10 目标命中率待扫描计数；规则面小、范式现成） |
| 状态 | **待核实**（语言审计发现） |
| 验证 | 中间树 build/test；fixture：`params ReadOnlySpan<int>` 展开、`params IEnumerable<T>`/`List<T>`（可变集合语义）、空实参 `M()`、与默认值组合 `M(1, b: 2)`、`M([1,2])` 组合 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/params（**已核**：params collections 合法类型与调用形态） |

#### C49 C# 14 扩展成员归一（extension 块：扩展属性/扩展运算符/静态扩展）（P1）
| 字段 | 内容 |
|------|------|
| 名称 | `ExtensionMemberNormalize`（C19 的 C# 14 声明侧扩展 + 成员去糖族规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | C# 14 顶层非泛型静态类中的 `extension(ReceiverType receiver) { … }` 块（receiver 可泛型）：① 实例**扩展属性**（get/set/表达式体）；② **静态扩展运算符**（六次补全修正：`public static … operator +(...)` 等在 extension 块内声明，调用点 `a + b` 走用户定义运算符决议，官方 extension 页已核；扩展索引器为 C# 15 预览，归 C52 防线）；③ **静态**扩展方法/属性（调用点为**接收者类型名限定**新语法：`IEnumerable<int>.Generate(...)`）→ 全部物化为传统静态类 + 显式静态成员：属性调用点 `x.P` → `Ext.GetP(x)` / `Ext.SetP(x, v)`（与 C11/C12 成员方法化产物形态对齐）、运算符调用点并入 C14 声明物化、静态成员改传统类名调用 |
| 解决的转换难点 | C19 只覆盖**方法调用点**物化；C# 14 引入的扩展属性/扩展运算符/静态成员在 C19 设计视野之外：`x.P` 走 PropertyTransformer 普通成员查找将解析失败或静默错绑；声明侧亦无条目（C11 只处理类型内成员）。Java 无扩展成员任何形态 |
| 证据 | 语言层：C# 14 extension members（官方语言参考「Extension member declarations」：顶层非泛型静态类可用 extension 块声明扩展成员）；.NET 10 BCL 新 API 实际使用。**仓库现状（2026-09-30 已核实）**：`Pipeline/ExtensionMethodIndex.cs` 与 PropertyTransformer 尚不识别 extension 块语法；**先决已闭环**——Microsoft.CodeAnalysis 已由 4.12 升级至 **5.0.0**（探针实测 4.14 中 extension 块仍为预览：枚举最高 CSharp13、Latest 下 16 个语法错误；§4 风险 20，提交 3c0d37bd），声明/调用侧物化待实施 |
| 与现有通道关系 | 与 C19 一体（共用扩展方法索引与接收者绑定基建，同通道或紧邻批次）；产物与 C11/C12 对齐使 Java 侧零特判；须晚于 E13（struct receiver 扩展属性与只读化交互）；被 E17 改名表覆盖 |
| 优先级 | **P1**（.NET 10 目标基线直接命中；与 C50/C51 同属「Roslyn 版本先决」批） |
| 状态 | **先决批已完成（2026-09-30）**：Roslyn 升级 **5.0.0**（GA 节点 ExtensionBlockDeclarationSyntax，Latest 下零语法错误，提交 3c0d37bd）；空条件赋值语法树形状变化的 MS-5 回归已同步修复，全量 3365 测试通过；声明/调用侧物化待实施。**注册表占位已补（2026-09-30 第十次更新）**：stub id `extension-members`（RunAfter extension-methods），启用时 fail-fast CS2J9001 直到实现 |
| 验证 | 中间树 build/test；fixture：扩展属性 get/set、扩展运算符调用点、静态扩展方法（接收者类型名限定调用 `IEnumerable<int>.Generate(...)`）、泛型 receiver、`a.ExtProp.B` 链式、与 `using static`/别名组合 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/extension（**已核**：成员种类/签名唯一性/泛型参数位置/C# 15 扩展索引器）；https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14 |
| 六次补全（官方文档核对） | 官方 extension 页已核：成员种类 = 方法/属性/**运算符**/索引器（**索引器 C# 15 起为预览**——C# 14 基线 parse 层即拒绝，防线登记归 C52）；同一静态类内（含多个 extension 块）成员签名必须**唯一**（receiver 计入签名）；泛型参数位置规则：receiver 使用 → 声明在块上，仅成员使用 → 声明在成员上，不能两处同名；静态扩展调用语法为**接收者类型名限定**（含泛型类型名）；新形态与经典 `this` 参数静态方法 IL 相同、源兼容——**物化最简路径为改写回经典形态**（扩展属性/运算符生成静态方法对） |

#### C50 C# 14 `field` 关键字去糖（半自动属性）（P2，随 C11 同批）
| 字段 | 内容 |
|------|------|
| 名称 | `MemberDesugar`（C11 的 C# 14 子规则） |
| 分类 | 1.4 成员 |
| 目标 C# 模式 | `int P { get => field + 1; set => field = value; }`——访问器体内 `field` 上下文关键字（编译器合成后备字段的显式引用）→ 改写为显式私有后备字段 + 普通访问器体（即 C11 产物形态）；只读半自动（仅 get）、表达式体、属性初始化器组合全覆盖 |
| 解决的转换难点 | `field` 在访问器内是**语义上引用合成字段**的标识符而非普通表达式——转换器把它当普通标识符解析会解析为不存在符号或错绑同名局部/参数。C# 12/13 期间为 preview、C# 14 GA |
| 证据 | 语言层：C# 14 `field` 上下文关键字 GA（官方 What's new in C# 14）。**仓库现状待核实**：PropertyTransformer 对访问器体内标识符的解析路径；.NET 10 BCL 实际使用量在目标源码扫描中计数 |
| 与现有通道关系 | 并入 C11（`MemberDesugar` 属性规则；见 C11 五次补全追加行）；产物与 C11 完全同形故 Java 侧零新增；须在 E17 之前（`field` 撞 Java 侧改名规则时联动）；与 C27（init/required）组合形态同批验证 |
| 优先级 | **P2**（若 .NET 10 目标源码扫描显示 Collections/Text 高频使用则随 C11 同批，等价 P1） |
| 状态 | **待核实**（语言审计发现） |
| 验证 | 中间树 build/test；fixture：get/set 均用 `field`、仅 get、`field` 与属性初始化器、readonly struct 中 `field`、`field` 与 `??=`/复合赋值组合 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/field（**已核**：上下文关键字/单访问器体+编译器生成另一侧） |

#### C51 C# 14 简单空条件赋值（`a?.b = c` 及复合形态）（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `NullConditionalDesugar`（C35/E03 家族的 C# 14 追加规则，见 C35 五次补全追加行） |
| 分类 | 1.4 语法糖 |
| 目标 C# 模式 | ① 简单空条件赋值 `a?.b = c`、`a?[i] = v` → `if (a != null) { a.b = c; }`（接收者单次求值）；② 复合空条件赋值 `a?.b += c` → 同形 if + 复合赋值（与 C17 自增目标族同批）；③ 赋值右侧含 `?.` 链的组合 |
| 解决的转换难点 | C35 只覆盖**读取链** `a?.b.c?.d()`；赋值目标位置的 `?.` 是 C# 14 新语法——若条件访问改写不识别赋值目标形态，将落入未知 kind 兜底或产生错误 Java；空条件赋值语义 = 「非 null 才赋值」，静默丢失即行为差异 |
| 证据 | 语言层：C# 14 simple null-conditional assignment（官方 What's new in C# 14）。**仓库现状待核实**：C35 所列 `ControlFlowTransformer.TransformConditionalAccess` 与 `ExpressionTransformerFacade` owner 链回溯是否覆盖 AssignmentExpression 左侧 |
| 与现有通道关系 | 并入 C35（C35 升格为「null 糖归一」通道，含 E03/C35/C51 三规则）；接收者单次求值复用 E03 两遍临时变量提升基建 |
| 优先级 | **P3**（低频新语法；随 C35 顺带收编，无独立排期价值） |
| 状态 | **待核实**（语言审计发现） |
| 验证 | fixture：`a?.b = c`、`a?[i] = v`、`a?.b += c`、右侧同为 `?.` 链、接收者为属性/方法调用（单次求值验证）、**RHS 条件求值**（null 接收者时右侧不得执行——`values?[2] = GenerateNextIndex()` 形态）、ref/out 目标形态 fail-fast |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/member-access-operators（**已核**：空条件赋值限制全集）；https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14 |
| 六次补全（官方文档核对） | 官方 member-access-operators 已核（限制全集）：仅**引用类型**接收者；复合赋值（`+=` 等）**允许**；`++`/`--` **不允许**；**右侧仅在左侧非空时求值**（`values?[2] = GenerateNextIndex()` 中 RHS 条件调用——本条 ① 的 if 物化已保真）；空条件表达式**不是变量**——不可 ref 赋值/赋给 ref 变量/作 ref/out 实参（fail-fast 面） |

#### C52 版本防线升格与低频构造收编（C# 13/14 + C# 8–12 补盘）（P3）
| 字段 | 内容 |
|------|------|
| 名称 | `CSharp13PlusGuard`（C36 防线从「C# 11/12」升格为「C# 11+」的伞条目；收编项而非全量通道） |
| 分类 | 1.8 其他 / 版本边界 |
| 目标 C# 模式 | ① **C# 13**：`allows ref struct` 泛型反约束（`where T : allows ref struct`）、lock 目标类型 `System.Threading.Lock`（编译器把 `lock(l)` 译为 `EnterScope`，与 C29 的 synchronized 直映语义不同，见 C29 追加行）、`\e` 转义（词法，C43 同批）、method group 自然类型改进（影响 C45 委托绑定）、`[OverloadResolutionPriority]`（C10）、partial 属性/事件（C20，Layer 1 已登记）；② **C# 14**：隐式 Span 转换（`ReadOnlySpan<T>` ← `T[]`/字符串等接收点隐式化，**改变重载决议结果**——与 C06/C48 交界）、unbound generic `nameof`（`nameof(List<>)`）、partial 构造器（C20）、**简单 lambda 参数修饰符**（C45，六次补全补盘）、**用户定义复合赋值运算符**（C14/C17——`a += b` 不再必然拆解为 `a = a + b`，须先查 SemanticModel 显式复合运算符）、**文件级应用指令**（`#:package`/`#:sdk` 等，E01 识别）；③ **C# 8–12 补盘**：接口默认成员（DIM）完整形态面——非公共访问（private/protected/internal）默认实现、默认属性/事件、菱形**最具体重实现**（`I.M()` 显式语法，C46 交界；Java default 方法只覆盖公共实例方法子集）、`>>>` 无符号右移（与 Java `>>>` **1:1 直映**，仅登记不需通道）、NRT 引用类型 `T?` 注解的声明层剥离（`#nullable` 指令与 `!` 已处理；`?` 注解本体归词法批，C28/C43 同族）、`file` 局部类型（C# 11 修饰符——**与 file-scoped namespace 是两个不同特性**，Java 无对应需改名）、attribute 目标限定符（`[field:]`/`[return:]`/`[assembly:]`，C10）、别名限定符 `::`（`global::`/extern alias 表达式形态，C28）、`delegate*` 函数指针（C# 9，归 C06，见其追加行）、const 内插字符串（C# 10，C23 子规则）、lambda 参数默认值（C# 12，罕见）、泛型静态字段**逐封闭类型实例化**语义（C#：`C<int>`/`C<string>` 静态互相独立；Java 擦除后共享——非 T 引用型是静默语义差，见 C02 追加行） |
| 解决的转换难点 | 这些构造共同构成「转换器默认分支黑洞」（§4 风险 13 既有定性）；其中有正确性含义的子项：`System.Threading.Lock`（直映 synchronized 错语义：scope 释放且与 Monitor 不可混用）、隐式 Span 转换（静默选错重载）、DIM 菱形（Java 侧歧义或缺省）、泛型静态逐实例化（计数器/缓存统计错值） |
| 证据 | 语言层：C# 13/14 官方特性清单（What's new in C# 13/14）与语言参考；C36 三/四次补全已定性「仓 grep 无命中 ≠ 输入不使用」。**仓库现状待核实**：逐子项 grep（`\e`→C43、Lock→C29、delegate*→C06、DIM 菱形→与 C46 联测） |
| 与现有通道关系 | **伞条目**：子项按家族拆入 C06/C09/C10/C20/C23/C28/C29/C43/C46/C02；伞条目只保留「版本扫描 + 显式拒绝」职责（C36 升格）；每次 Roslyn 包升级或目标基线升级时重跑差集审计（本五次补全的方法论） |
| 优先级 | **P3**（防线价值为主；.NET 10 目标源码特性扫描（§4 风险 20）完成后按命中数拆入各家族） |
| 状态 | **待核实**（语言审计发现；C36 升格注记见其五次补全追加行） |
| 验证 | 中间树 build/test + 「未知语法显式报错」断言；fixture：`allows ref struct` 方法、`System.Threading.Lock` lock、`nameof(List<>)`、`file class`、`[return:]`、`delegate*` 声明、`const string s = $"a{1}"`、DIM 菱形重实现 |
| 官方文档 | https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-13；https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14（**已核**：特性全集比对基准） |
| 六次补全（官方文档核对） | whats-new/csharp-14 特性全集比对（**已核**）后补盘：① 简单 lambda 参数修饰符（C45）；② 用户定义复合赋值运算符（C14/C17）；③ 文件级应用指令 `#:package`/`#:sdk` 等（E01）；④ **扩展索引器为 C# 15 预览**（官方 extension 页——C# 14 基线防线上须显式拒绝而非误解析）；⑤ 非绑定 `typeof(List<>)`（非新语法但同族——C02 typeof 归一须覆盖泛型定义形态，区别于 C# 14 的 unbound `nameof`） |

---

## 3. 推荐实施路线

### 3.1 建议顺序（仅排序，不含实现设计）

0. **（观察项，先于一切语义 pass）** E01 之后可插入 **C43+C28 词法批**（raw string/u8/verbatim/namespace/using 归一）——纯语法、幂等、只 parse 一次；现状转换器已内联吸收，仅在统一引擎（§5.4）落地或需要版本无关中间树时实施。
1. ~~**C01 RefOutIn 接线收尾**~~ **已完成（2026-09-28/29，止于 48331c9c）**：在链默认开；M5 sdk 全量比对与 ROI 旧码下线为挂起后续。
2. ~~**C03 控制流去糖 + C37 模式表达式去糖**~~ **已完成（同批，止于 15d534a1）**：pattern-desugar Implemented 默认关，语句/表达式两侧共用归约器，SamplePatternSolution 守门齐备；默认翻转待评审。
3. **C31 Index/Range 归一**（P1，机械、消灭「静默错误输出」；挂 E12 支撑库范式）。
4. ~~**C02 Generic 深化**~~ **已完成（止于 ecc1376e）**：generic-deepen Implemented 默认关（typeof(T)/is-as T/静态字段 holder/residual 闸门/E2E）；M4 默认开启评审与 sdk 计数门挂起，C30 下游核实门仍独立挂起。
5. **C05 enum 声明归一**（P1，扩 E04；与 C03 同批验证 enum switch）。
6. **C07 struct 值语义补全**（P1，扩 E13，回收 StructCloneHelper）。
7. ~~**C04 yield 迭代器降级**~~ **已完成（自包含形态，2026-09-29 止于 b784b47f）**：iterator-lowering Implemented 默认关（RunAfter 锚 make-readonly；Core 9 文件 + 适配器 + 单测/顺序锁）；§7.2 项目级 fixture 与 §8 默认开启五门槛（try/finally 两半区原型、sdk 2887 站点基线、EF Core 画像）仍挂起；CS2J0405 体内 lambda 捕获形态仍以 C32+C45 为前置，故原「前置依赖」对**全量形态**继续成立。
8. ~~**C08 JavaKeywords 落地**~~ **已完成（2026-09-29 落地，2026-09-30 第十次更新补登）**：java-keywords Implemented（默认关；设计完备，链尾锚定）；默认开启随当日统一翻转收口。
9. **C06 unsafe/FFM**（P1，按 fixed→stackalloc→指针→extern 分规则滚动，依赖 C01）。
10. **C34 decimal 运算归一**、**C10 JSON/Attribute 窄版** 与 **C09 ThreadStatic**（P2，三者互不依赖可并行；C34 有 compat Decimal 现成方法面与 E09 范式）。
11. **C32 → C45 闭包提升族**（P2，先落可变捕获 holder（C32），再按无捕获→this→值捕获→嵌套递归分阶段全量 display class 提升（C45）；复用 E02 捕获分析基建，删转换器内隐式状态；是 C04 **CS2J0405 捕获形态全量化**与 C18 的前置——C04 自包含 M1 形态已先行落地，见路线 7）。
12. ~~**成员去糖族 C11→C12→C13→C14（+C46 签名收口）**~~ **C11–C14 已完成（2026-09-29，四规则随 member-desugar 同一 pass 顺序落地，分别止于 8f6032da/146fdda2/129cb473/2d25700e，Implemented 默认关，catalogItems `C11, C12, C13, C14`）**；默认翻转评审与 **C46 签名收口**（显式接口/new 隐藏/协变返回，晚于 C20）仍挂起。
13. **数值边界族 C15→C17**（P2，可挂在 E09/E04 既有通道内扩展；C34 的 `++/--` 子项并入此处兜底）；**foreach 通道 C16+C40**（P2，string/残余解构/枚举器适配/ref 源统一显式化）。
14. **C19 扩展方法显式化 → C26 LINQ 通道独立化**（P2，LINQ 先重写、扩展解析后固化；**C26 入口必须先落 IQueryable/Expression 三路分流门**）。
15. **C18 async → C38 异步流/异步释放**：先做形态①原型再决定是否投形态②；C38 与 C18 的形态选型同批定案；**C44 using 显式化**仅 ref struct/yield 组合子题随 C06/C04/C38 顺带处理，同步形态维持转换器内联。
16. P3 项按下游实际命中率排序：C36/C35/C33/C21/C22/C23/C24/C27/C20/C28/C29/C25/**C39/C41/C42/C43/C44/C46**（后三项为四次补全新增，其中 C43/C44 现状已内联吸收，默认不进链）。
17. **C47 相等性/拼接调用点补全**（核实门 2026-09-30 已关闭：string `==`/`bool?`/元组已内联；`(object)` 逃逸、拼接 null 语义、委托值相等、record `==` 四子面按 §4 风险 21 结论补全，以 `GetSymbolInfo().Symbol` 为判据；补回被删护栏用例）。
18. **C49 + C50（C# 14 成员面）**（先决＝验证 Roslyn 包版本对 C# 14 语法树的 parse 支持；.NET 10 目标扫描后定批）；**C48 params 族**（P2，并入 E08 同通道，先扫计数）；C51/C52 随 C35/C36 家族顺带收编（P3）。

### 3.2 与现有链的依赖/顺序总览

现状 `Program.cs` 顺序（已核对行号）：

```text
E01 strip-conditional (174)
 → E02 local-function-lift (208)
 → E03 null-coalesce (242)
 → E04 enum ops (280)
 → E05 delegate (321)
 → E06 tuple-to-struct (361)        [注释要求先于 E08：Program.cs:363-364]
 → E07 foreach-deconstruction (406) [Tuple 族依赖 E06：Rewriter:67-68]
 → E08 strip-default (443)
 → E09 unsigned-type (479)
 → E10 generic (514)
 → E11 record-readonly (553, opt-in)
 → E12 array-method (595, opt-in)
 → E13 make-readonly (636)          [注释要求先于 E14：Program.cs:638]
 → E14 eliminate-goto (679)
 → 主转换
```

候选插入建议（依赖理由见各条目）：

```text
E01 → [C43+C28(词法批，观察项)] → [C20 若做，放这里] → E02 → E03 → C03+C37(控制流/模式去糖) → C31(Index/Range) → E04+C05 → E05
 → E06 → E07+C16+C40(foreach 源) → E08+C48(params 族) → E09 → E10+C02(+C30) → C01(RefOutIn，位置需原型确认)
 → E11 → E12 → E13+C07 → C06(unsafe，含 delegate* 子项) → C32+C45(闭包提升，C04 全量捕获形态前置) → ~~C04(yield)~~(M1 自包含形态已落地默认关)
 → C34(decimal) → C47(相等性/拼接调用点，核实后进链) → C09/C10 → ~~C11→C12→C13→C14~~(已落地默认关；C46 签名收口待办) → C49+C50(C# 14 成员面，随成员去糖族) → C15/C17(数值族)
 → C19(扩展方法) → [E16/C26 LINQ 独立化（入口 IQueryable 三路分流）后移到此处] → C18+C38(异步/异步流；C44 仅 ref struct/using 子题)
 → E14 goto → C08(JavaKeywords，设计定死最后) → 主转换
（P3 观察项默认不进链：C29/C33/C35/C36/C39/C41/C42/C43/C44/C46/C51/C52 及 C20–C25/C27/C28 按下游命中率评估）
```

横切约束：
- 凡**注入支撑库**的通道（E05 Cs2jPrelude、E06 Cs2jTuples、E09 Cs2jUnsignedMath、E12 Cs2jArray、C01 Cs2jByRef、C31 Cs2jIndexRange 或并入 Cs2jArray、C34 复用 compat `Decimal` 不新建工程）都要复用 `ProjectPreprocessSource.cs` 的 restore/资源准备，并在主转换前过滤支撑工程（现做法见 `Program.cs:750-756`）。
- 凡**改名/改签名**的通道（C01、C02、C11-C14、C19、C08）必须基于 SemanticModel 的全编译符号表，不能文本替换——E11/C08 的 plan+回滚范式是指定骨架。

### 3.3 「同一底层难点」的合并建议

| 合并族 | 成员 | 共同底层 |
|--------|------|----------|
| 词法/指令归一 | E01 + C28 + **C43（raw/u8/verbatim，观察项）** | 纯语法、幂等、不需语义模型；统一引擎「一次 parse 合批」首批对象（§5.4） |
| 控制流去糖 | C03 + **C37（表达式侧，共用模式归约器）** + C16 + **C40（foreach 迭代源/ref 源）** + 部分 C05（enum switch） | 语句/模式到 if-链与布尔表达式的机械归约 |
| 成员去糖 | C11 + C12 + C13 + C14 + **C46（显式接口/new 隐藏/协变返回收口）** + **C49（C# 14 扩展成员）** + **C50（C# 14 `field`）**（+C27 + **C39 终结器删除**） | 「带访问器语法的成员 → 普通方法 + 后备字段」，对应实验 Lowering 四件套；同一 SemanticModel 符号表 |
| 闭包/状态机提升 | E02 + E05（匿名方法前半步）+ C32 + **C45（display class 全量提升）** + C04（迭代器）+ C18 的 async lambda 子项 | 捕获分析/确定性命名/holder 共享；fixpoint 互相喂送（§5.4）；C32 建议作为 C45 第一阶段 |
| 数值边界 | E09 后缀规则 + C15 + C17 + C22 + C34 decimal + **C41 特殊数值（Half/Int128/UInt128/nint/IntPtr）** + enum 运算（E04） + **C47（相等性/字符串拼接调用点）** | 数值运算在窄类型/特殊目标上的显式化；已有 UnsignedPostfix/EnumSyntaxRewriter 基建与 compat `Decimal` 方法面 |
| 值语义 | E11 + E13 + C07 + C01 的 ref struct 子项 + **C44 的 ref struct using 子题** | 复制/变异/只读边界；复用 StructAnalyzer/BoxingReachability/MethodMigrator |
| 泛型擦除补偿 | E10 + C02 + C09（可合并静态字段规则）+ C30 + C33（型变/约束审计） | reified→erased 的运行时信息显式贯穿 |
| 表达式/集合形态归一 | C23（**handler 语义判定**）+ C24 + C35 + **C51（C# 14 空条件赋值）** + C36（+C31 的切片使用点）+ **C48（params 调用点展开）** | 表达式糖 → 显式语句/构造/调用的机械归约；共用「单次求值临时变量提升」基建（E03 两遍范式） |
| 捕获/隐藏状态前移 | C32 + C09（已并入闭包族 C45 统一捕获分析） | 把转换器内的运行时隐式状态（lambda 捕获 holder、ThreadLocal）显式化为 C# 源码形态 |
| 异步族 | C18 + **C38（异步序列/释放）** | await 与异步迭代的显式状态/桥接归一；共用形态选型（轻/重）与 foreach 插入点 |
| 平台边界 | **C42（隔离/拒绝）** + C06 的 extern 子项 + C10（P/Invoke 属性） | 外部契约边界；共享 CS2J3001/3101/3102/3201-3203 诊断基建 |
| unsafe 家族 | C06 全部子规则 + E12 的 Span/数组交叉 + **C44 的 ref struct disposables 子项** + `[InlineArray]` 盘点 | 指针模型→索引/Span/FFM；共用 FfmHelper |
| enum | E04 + C05 + C17 enum 自增 + C22 可空 enum | 同一 EnumSyntaxRewriter 扩展，不新建通道 |
| 版本面 / 新语法防线 | C36（升格 C# 11+）+ **C52（伞条目）** + **C49/C50/C51（C# 14）** + **C48（C# 13）** | 版本升级差集审计 + 显式拒绝，防「默认分支黑洞」（§4 风险 13/20）；目标基线 .NET 10 的共同先决＝Roslyn 包版本支持 C# 14 语法树 |

---

## 4. 风险与开放问题

1. **C18 async 全量降级的等价性/成本**：源码级 async 状态机 ≈ 重写编译器 lowering，且与 `using`/try-finally/锁交互复杂；建议先做形态①（提升 async lambda、归一 ConfigureAwait），用真实命中率决定形态②去留。
2. **C04 yield 与 try/finally、using、ref 的组合**：Dispose 时序与 C01 holder、C06 Span 释放相互耦合，必须先出原型 + 专项 fixture，不能直接进默认链。（2026-09-30 更新：C04 自包含状态机已落地为 Implemented 默认关（b784b47f）；本风险即其 §8 默认开启门槛的未过项——try/finally 两半区原型与 SampleIteratorSolution 项目级 fixture 完成前不翻默认开）
3. **C06 unsafe 的 ABI/外部契约**：`[DllImport]`/`[LibraryImport] extern`、对外部接口的 ref 实现必须锁定（RefOutIn 设计 D6 已确立原则），任何指针改写都不能改变封送布局；fixed→MemorySegment 只在字段缓冲区被验证过（`FieldTransformer.cs:70-101`）。
4. **C05 enum-as-class 的外显契约**：`Enum.ToString/Parse/Format/GetValues`、switch、字典键、`[Flags]` 序列化都会随表示形态变化；compat `EnumHelper` 虽厚但仍是「Java enum」假设，前置归一后需要同步重定义桥接面。
5. **C01 RefOutIn 的 Java 映射缺口是真实阻断**：设计 D3 自述「Java 产物暂不可编译为已知中间状态」；`RefOutInProjectTests.cs:29-40` 现断言的是旧 IntHolder/ObjectHolder 路径——接线意味着要同时改测试断言与 holder 体系，必须先补 Java 侧 spec。
6. **C30 closed metadata 的代码考古**：设计声称 PatchClosed* 落在 `ProjectPasses.cs`，当前工作区不存在（全仓 grep 无命中），compat 侧却有 `ConcurrentHashMapHelper.putIfAbsentUntyped` 等落点。实施前必须先确认补丁是在下游仓、已回退还是未合入，避免重复建设。
7. **反射/字符串契约对所有「改名通道」的风险**：`GetMethod("True")`、XAML、JSON 按名匹配、表达式树对 LINQ 提供方的形态依赖——E17 spec §9 已文档化（nameof 字面量化、反射不保证）；C10/C11-C14 同理会触碰成员名，需沿用同一限制声明与 overrides 机制。
8. **表达式树与 dynamic 是多通道共同禁区**：C08（JK-8）、C25、C14 的转换运算符在 `Expression<T>` lambda 内改写会改变树形态；各通道需共享「表达式树节点内跳过」判定工具。
9. **实验性 Lowering 不是实现资产**：`Lowering/*` 仅被 Java2 对比测试引用（`NewConversionPipeline.cs:27-33` ↔ `PipelineComparisonTests.cs`），前置 preprocessor 只能借其**形态**，不能尝试共用代码（IR 不同、生产不可达）。
10. **opt-in 通道的长期默认化**：E11 RecordReadOnly、E12 ArrayMethod 当前默认关——新增通道若也长期 opt-in，会形成「多套事实标准」；建议每个 P0/P1 通道落地时同时定义默认开启的验收门槛（build/test 全绿 + csharp-sdk 基线不退化）。
11. **中间树验证成本**：每环都跑 restore + build + test 的串行链会显著拉长 convert-project 耗时；`VerifyBuild/VerifyTests` 已可关（`Program.cs:4773-4777`），但默认开启的通道越多越需要指纹缓存正确——新增中间目录须同步接入现有的 fingerprint/preserve 机制（现状通过 `opts.Source` 串联与指纹回填实现，见各块 `FingerprintSourcePath/FingerprintInputRoot` 赋值）。
12. **「静默错误输出」是一族独立于 fail-fast 的高危现状（C31/C34/C36/C37/C38/C39）**：这些构造今天不报错、却产出不可编译/错误 Java 或静默丢语义——切片对 string 产 `Arrays.copyOfRange("abc", …)`（`ElementAccessTransformer.cs:219-225`）、独立 `^n` 产注释（`:294-299`）、decimal 二元保留 `+`（`BinaryExpressionTransformer.cs:88-92`）、集合表达式 spread 被过滤（`ExpressionTransformerRegistry.cs:139`）。三次补全新增排查站点（兜底/丢弃注释的完整清单）：未支持模式恒真占位 `/* TODO: unsupported pattern */ true`（`TypeOperationTransformer.cs:115`）与 switch 臂同类兜底（`ControlFlowTransformer.cs:505`）、`await foreach`/`await using` 的 `AwaitKeyword` 被静默忽略（`Loops.cs:104-155`、`SwitchAndResource.cs:198-244`）、终结器被成员 switch 静默丢弃（`ClassTransformer.cs:317-381`，无 Destructor 分支也无 default）、语句级兜底注释 `/* TODO: {kind} - {node} */`（`StatementTransformer.cs:60`）与表达式级兜底 `/* TODO: {kind} */`（`ExpressionTransformerFacade.cs:35`）、checked 表达式前缀 `/* checked */`（`TypeOperationTransformer.cs:31`）、`__arglist`/`__makeref` 系占位（`ControlFlowTransformer.cs:52-53`）、未知格式串 `%s /* TODO: format specifier … */`（`StringExpressionTransformer.cs:368`）、catch 外 rethrow 注释（`StatementTransformer.ExpressionAndReturn.cs:88-91`）、构造器表达式体 TODO 注入（`ConstructorTransformer.cs:169`）。前置化设计的第一原则：C# 侧无法等价表达时**显式报错**，禁止注释/占位/原样透传流入 Java 产物；本清单此前各条多以 fail-fast 计风险，补全后应将「静默错误输出」单列排查面。
13. **C36 兼任 C# 11/12 新语法防线**：`static abstract` interface members、`scoped`、泛型 attribute 当前无任何专项处理证据（全仓 grep 无命中），会落入转换器默认分支；若输入库使用新语法，建议在 C36 内先做「语法盘点 + 显式拒绝」，再逐个收编。
14. **「映射存在 ≠ 语义可用」（C34/C10）**：compat `Decimal.java:261-314` 的 `add/subtract/...` 齐备但 C# 侧从未发射；C10 的注解桥同理。实施前需盘点方法面是否齐备（比较运算、`ToString` 文化重载、`switch` governing 等），避免生成不存在的方法名。
15. **模式匹配存在「测试红灯 + 实现/预期双向脱节」（C03/C37）**：`PatternMatchingTests` 实测 8/11 失败（2026-09-28 `-c Release`），其中 `PropertyPattern_ConvertsWithTodoMarker` 期望的 `/* TODO: recursive pattern */` 文本在 src 全仓已不存在——测试断言与实现输出已长期不同步。C03/C37 落地前必须先定死「模式→布尔表达式」的目标形态，并同步修订测试期望；同时澄清与 `JavaGenericPatternRewriter`（仅语句条件内泛型声明模式提升，`JavaGenericPatternRewriter.cs:110-129`）的分工，否则前置去糖与 rewriter 会争抢同一语法面。
16. **extern/DllImport 的处理行为自相矛盾（C42/C06）**：`ClassTransformer.cs:346-350` 对 extern/DllImport 直接抛异常，而 `UnsupportedDomainPasses.cs:379-381` 的 `IsPInvokeContainerFile` 注释称「ClassTransformer 会为 extern 发 stub」。两者不可能同时为真；实施 C42/C06 前须先澄清（查历史分支或对 P/Invoke 容器文件复现一次转换确认现状）。
17. **目标画像错配 + IQueryable/Expression 盲区（C26，四次补全）**：训练集 D:\csharp-sdk 中 `Expression<>` 仅 2 处、`IQueryable` **0 处**，而二者是 EF Core 的命门；现有 json/regex/uri/xml-tiny 练的是 BCL/Span/序列化肌肉，练不到「表达式树 + query pipeline + DI」。正式排期前必须先拉 EF Core 源码（EFCore.Relational、EFCore.SqlServer、Microsoft.Extensions.*）跑同一套特性扫描（§5.1、§5.6 债务 C）。同时已核实 LinqRewriter 无 receiver 分流（C26 条目证据），在 EF 代码上会把服务端查询静默改写为客户端求值——能编译、单测难抓；三路分流门是 C26 通道化的前置验收条件。
18. **插值字符串 handler 绑定（C23，四次补全）**：sdk 插值 650 处中相当比例绑定自定义 `InterpolatedStringHandler`（json/日志）；无脑降 `Concat/Format` 会改变参数求值次数与结果。C23 必须经 SemanticModel 判定 handler 类型后再选降级形态，不能纯语法展开。
19. **统一引擎替换手写链的迁移边界（§5.4，四次补全）**：分批重建语义模型、fixpoint、特性位剪枝是对现有 14 环「复制整树 → reparse → restore」串行链的结构性替换。应先以 E01+C28+C43 三个纯语法 pass 试点「一次 parse 内存合批 + 适配器包装」，验证指纹/回滚/落盘调试三不变后再推广；不一次性重排全部通道（避免改动规模超过问题半径）。
20. **训练集与目标基线的语言版本轴错配（五次补全，C47–C52 的共同背景）**：本清单全部频率证据来自 D:\csharp-sdk（json/regex/uri/xml-tiny，.NET 8/9 时代源码），对 C# 13/14 特性（params collections、`extension` 块、`field`、简单空条件赋值、隐式 Span 转换、`System.Threading.Lock`）**零样本**；而既定转译目标是 .NET 10 runtime（Collections → Text → Xml），其源码按 C# 14 编写——与债务 C 同族但**轴不同**（语言版本轴 vs 领域轴）。两个行动：① **先决检查**：cs2j 的 Microsoft.CodeAnalysis 包版本必须能 parse C# 14 语法树，否则 .NET 10 源码在 parse 层即全量失败（见 C49 条目状态行）；② 把 .NET 10 目标源码纳入 §5.1 特性扫描基线，与 EF Core 画像同批跑，再定 C48–C51 批次。
21. **C47 相等性核实的双向结局（五次补全）**：① 若 `BinaryExpressionTransformer` 已内联 string `==` → equals 分派——C47 降为观察项（CsCore 收敛价值仍在），记录证据后关闭；② 若未覆盖——「运行时构造的等值字符串」用例将**静默错值**且字面量单测因驻留侥幸通过（与 §4.12 家族同性质），应立即补并重估 P0。核实必须覆盖 `!=`、`(object)` 逃逸、`Nullable` 提升、元组特例、`+` 的 null 语义五个子面——只测 `==` 字面量会误判为已支持。**六次补全**：string（序数值相等）/委托（调用列表值相等）/拼接（null→空串）/元组/record 的 C# 侧语义已由官方文档确证——其中**委托 `==` 直映 Java 即静默错值**（`o1.M == o1.M` 两实例 C# true / Java false），不依赖仓库核实即可定性；剩余核实面收敛为「仓库内联是否已覆盖」。**七次勘误落账（2026-09-30）**：核实结论＝**部分覆盖（B 档）**——结局②成立但范围收敛为 4 子面（`(object)` 逃逸、拼接 null 语义、委托值相等、record `==`）；string `==`/`bool?`/元组已内联（结局①覆盖的部分）；判据为 `GetSymbolInfo().Symbol`，C47 按 P1 补全。

---

## 5. 目标子集 CsCore 与统一预处理引擎（2026-09-28 方向性设计输入）

> 本节是**方法论/方向性输入**（来源：2026-09-28 预处理器架构讨论），不是盘点结论，也不是已批准的实施计划；§1–§4 的条目与优先级仍以代码证据为准。方向性输入与代码证据冲突时以证据为准（典型：raw string 与同步 using 被其建议「首批降级」，但核实后均已被转换器内联吸收，故 C43/C44 列观察项）。

### 5.1 正确性两半区与 EF Core 测试预言机

现有 14 个 E 通道共享一个范式：产物仍是合法 C#，以中间目录 `dotnet build -c Release` + `dotnet test -c Release` 证明语义等价。由此把「正确性」切成两个独立收敛的半区：

- **.NET 半区**：C#→C# 降级产物在 .NET 上跑源项目测试全绿 → 证明 desugar 正确（降级 bug 在 .NET 侧暴露）；
- **Java 半区**：转译产物在 Java 上跑转译后的同一套测试全绿 → 证明翻译正确（翻译 bug 只在 Java 侧暴露）。

决定性推论：**EF Core 自带的数万条 xunit 测试，就是所有降级 pass 的免费正确性预言机**——同一套测试在两侧分别守门。工程推论：凡是能放 C#→C# 层做的 lowering，就绝不要放到 Java emit 层（后者只能靠 mvn 迭代逐错验证，一次抓一个错）。

前置行动（先于新 pass 正式排期）：拉取 EF Core 源码（EFCore、EFCore.Relational、EFCore.SqlServer 及 Microsoft.Extensions.* 依赖）跑与 csharp-sdk 同一份特性扫描，据结果重估 §0.4 优先级（C04/C18/C26 首当其冲）。

### 5.2 收敛终点：CsCore 最小语法面

所有预处理器的收敛终点是一份最小 C# 语法面，使主路径 translator 的输入只含能 1:1 映射 Java 的构造：

```text
CsCore 允许：类/接口/结构体外壳（经 ABI 决策后）、字段、普通方法、块语句、
  基础表达式（调用/成员/算术/new/三元/索引）、显式类型、try/catch/finally、
  while/for/if/switch(stmt)、委托调用、命名类型上 LINQ-to-objects 的已展开形式
CsCore 禁止：所有语法糖（§5.5 全景表逐行对应 pass 消除）
```

### 5.3 分层判据（什么必须放 C#→C# 层）

| 判据 | 归属 |
|---|---|
| 引入新类型/新成员，或改变控制流模型（状态机、闭包类、属性→方法组、事件→字段+add/remove） | **必须 C#→C#**（结构性、跨声明、可被 .NET 测试验证） |
| 局部 1:1 表达式替换，不引入声明（`??`、`?.`、简单插值、`nameof`、`!`、raw/u8/verbatim 字面量） | 可留 Java AST 层（本仓多数已内联，见 §5.5「现状」列）；为收敛输入面也可做最简单的 C# pass |
| 依赖目标语言 ABI 决策（泛型擦除、值类型、无符号） | C# 层做「令牌显式化」，Java 层做映射（E09/E10 已是这个分工） |

### 5.4 统一 pass 契约与编排（草案 × 本仓既有资产）

```csharp
interface ICs2jPreprocessPass {
    string Name { get; }
    PreprocessStage Stage { get; }          // Lexical / Declarative / Expression / ControlFlow / StateMachine / ABI
    bool NeedsSemanticModel { get; }        // 纯语法 pass 合批跑，只 parse 一次
    SugarFeature Consumes { get; }          // 消除哪种特性（用于 residual 自检）
    SugarFeature[] Produces { get; }        // 可能引入哪种特性（lift 局部函数 → lambda 捕获）
    IReadOnlyList<Type> RunAfter { get; }   // 顺序契约，替代 Program.cs 手写链
    AnalyzeResult Analyze(CSharpCompilation c, ...);   // 纯分析（SemanticModel）
    CompilationUnitSyntax Rewrite(CompilationUnitSyntax root, AnalyzeResult plan);
}
```

| 编排器职责 | 本仓既有资产（直接复用） | 缺口 |
|---|---|---|
| Analyze→Plan→Rewrite 三阶段 | E02（Analyzer/Plan/Naming/Rewriter）、E11（三阶段 + plan 级原子回滚）、E13（Analyze→计划→逐文件重写） | 无统一接口；统计/计划类型各通道自有 |
| residual 自检标准件 | E02 严格模式闸门 + residual 统计；E14 `ContainsGotoOrLabel` 后置自检 | 非标准件，多数通道无「目标模式计数=0 或仅白名单 skip」自检 |
| 验证门 | `ProjectPreprocessSource.cs` 的 restore/布局 + VerifyBuild/VerifyTests；各块 FingerprintSourcePath/FingerprintInputRoot 指纹/preserve 回填 | 每环复制整树 + reparse + restore，14 环串行拉长耗时（§4 风险 11、§5.6 债务 B） |
| 确定性命名服务 | E02 Naming、E06 TupleShapeRegistry（跨文件形状去重） | 缺跨通道统一生成名/保留名栈（`get_X`、`<>h__displayClass_N`、状态机字段），快照稳定性与用户代码防撞无统一保证 |
| 支撑库注入/过滤 | E05 Cs2jPrelude、E09 Cs2jUnsignedMath、E12 Cs2jArray（`Program.cs:750-756` 过滤） | 无契约声明，靠注释约定 |
| 分批重建语义模型 | 无 | 纯语法批共享一次 parse；语义批之后以 Roslyn `ReplaceSyntaxTree` 增量重建 compilation（复用 metadata references） |
| fixpoint 定点迭代 | 无 | lift 局部函数产生闭包类、async/yield 状态机产生 lambda——跑到「无变化」为止，带环检测与最大轮次 |
| 特性位剪枝 | 无 | `ConditionalWeakTable<SyntaxNode, SugarFeatureMask>` 自底向上缓存特性位（await/yield/lambda/closure/span…），pass 入口查掩码跳过不含目标特性的子树；EF Core 数千文件规模的必需性能措施 |
| 内存串联/落盘调试 | 仅每 pass 落盘 `.cs2j-<pass>-src` | 默认内存串联，按需逐 pass 落盘两模式 |

落地约束（最小侵入）：先用适配器把 E01+C28+C43 三个纯语法 pass 包装为统一接口、一次 parse 合批跑，验证指纹/回滚/落盘调试三不向后再推广语义批；不要求现有通道重写、不一次性重排 14 环（§4 风险 19）。

### 5.5 Layer 0–5 × 条目 × sdk 实测频率全景

扫描基线：`D:\csharp-sdk`（json 392 + regex 78 + uri 52 + xml-tiny 774 文件），2026-09-28。「现状」列：**已实现**＝有独立 preprocessor；**内联**＝主转换器 1:1 处理（按 §5.3 判据可留 Java 层）；**缺口**＝fail-fast/静默错误输出/无处理。

**Layer 0 — 词法/指令归一（纯语法、幂等、最先跑）**

| 构造 | sdk 频率 | 条目 | 现状（证据） |
|---|---|---|---|
| raw string `"""` | 1345 | C43（观察） | 内联：`LiteralExpressionTransformer.cs:233-245`（Java15+ text block，低版本转义串） |
| `"x"u8` / `@""` / `$@""` | — | C43 / C23 | 内联：u8→`new byte[]{}`（`:288-294`）、verbatim 转义（`:247-255`） |
| file-scoped namespace / global using / using static / extern alias | 2 / 20+ | C28 | using 别名注册表兜底（`UsingAliasRegistry.cs`） |
| `#if` / `#nullable` / NRT `!` | 大量 / `!` 203 | E01 | 已实现（指令剥离）；`!` 透明剥离（`UnaryExpressionTransformer.cs:54`） |
| `checked`/`unchecked` | 47 | C15 | fail-fast 与 `/* checked */` 静默占位并存 |
| NRT 引用类型 `T?` 注解（声明层）/ `\e` 转义（C# 13） | —（待扫 .NET 10 基线） | C52（词法批候选，C43/C28 同族） | `#nullable` 指令与 `!` 已由 E01/内联处理；`?` 注解本体现状待核 |

**Layer 1 — 声明层（一进多出，全部可被 .NET 测试验证）**

| 构造 | sdk 频率 | 条目 | 现状 |
|---|---|---|---|
| 属性（auto/computed/abstract/override/显式接口/hiding） | 海量 | C11 | 缺口；`LowerProperty.cs` 形态证据 |
| field-like 事件 / 自定义 add-remove | 42 | C13（`+=/-=` 组合在 E05） | 缺口；`LowerEvent.cs` |
| 索引器 | 多 | C12 | 缺口；`LowerIndexer.cs` |
| 运算符 / 隐式显式转换（op_true/false 除外） | 多 | C14 | 缺口；`LowerOperator.cs` |
| primary constructor（class/record/struct） | 20 | E08 | 据称已覆盖（`DefaultParameterDeclarationRewriter.cs:178-204`），独立通道前先复核 |
| record / `with` | 83 / 16 | E11 + C27 | E11 opt-in；with/init 使用点缺口 |
| `init` / `required` | 17 / 80 | C27 | 零散内联，无系统去糖 |
| partial 方法/属性/事件（C#13 成员级） | 5+ | C20 | 类型级合并已有，成员级缺 |
| 匿名类型 | 中 | C21 | 转换器合成 record |
| tuple 元素名 | — | E06 | 已实现（Item1/2 + 形状去重） |
| 局部函数 / static 局部函数 | 3486 | E02 | 已实现（residual 闸门）；状态机 pass 后需 fixpoint 再跑一轮 |
| 默认参数 | — | E08 | 已实现 |
| 显式接口实现 / new 隐藏 / 协变返回 | — | C46 | 仅去重键识别，方法级无改写 |
| 接口 static abstract/virtual 成员 | 少（json/json-codegen 有使用） | C36 防线 | 无处理；companion 模式是独立难点 |
| 扩展属性/扩展运算符/静态扩展（C# 14 `extension` 块；索引器为 C# 15 预览） | —（训练集无 C# 14；.NET 10 目标待扫） | C49 | 待核实；C19 只覆盖方法调用点，声明侧无处理 |
| `field` 关键字（C# 14 半自动属性） | —（同上） | C50 | 待核实；预期由 C11 产物形态吸收 |

**Layer 2 — 表达式层**

| 构造 | sdk 频率 | 条目 | 现状 |
|---|---|---|---|
| `??` / `??=` | 182 | E03 | 已实现 |
| `?.` / `?[]` | 72 | C35 | 内联特判面最宽（ConditionalAccess owner 链回溯） |
| is-not / and-or / relational 模式 | 623 / 262 / 72 | C03 + C37 | 多处恒真占位，测试 8/11 红 |
| 属性/位置/列表/ discard 模式 | 少量 | C37 | 仅泛型声明模式有 rewriter；位置/列表无 |
| switch 表达式 | 14–15 | C37 | 未知臂 `true` 兜底 |
| 字符串插值（含自定义 handler） | 650 | C23 | 内联但**无 handler 绑定判定**（风险 18） |
| `^x` / `x..y` | 456 / 564 | C31 | 静默错误输出（string 切片产 `Arrays.copyOfRange`） |
| 集合表达式 / spread `..e` | spread 1 | C36 | spread 被静默过滤；`List.of` 不可变语义差 |
| 对象/集合初始化器 | 海量粗扫 | C24 | 内联，无独立归一 |
| target-typed `new()` / `default` 字面量 | default 277 | C24 / E10 | `default(T)` 已处理；字面量判定零散 |
| 解构（tuple/Deconstruct/foreach 解构） | 少 | E06 / E07 / C40 | 元组与 KVP/Deconstruct foreach 已做 |
| `nameof` / `[CallerMemberName]` / `[CallerArgumentExpression]` | nameof 1272 | 内联 + C10 | nameof 已处理；Caller* 族缺调用点注入 |
| 泛型 `default(T)`/`new T()`/`new T[n]`/`typeof(T)`/`is T`/`as T` | — | E10 + C02 | 前 3 模式随 E10 已做；后 3 已由 C02 generic-deepen 承接（Implemented 默认关，ecc1376e，M4 默认开启门挂起） |
| discard `_` / lambda 上 attribute / static lambda | — | C45 / C10 | 随闭包提升与 attribute 归一处理 |
| params 集合（C# 13 `params ReadOnlySpan<T>` 等）/ params 调用点展开 | —（.NET 10 目标待扫） | C48 | 待核实；E08 只处理默认值；Java varargs 仅数组 |
| 简单空条件赋值 `a?.b = c`（C# 14） | — | C51 | 待核实；C35 只覆盖读取链 |

**Layer 3 — 控制流层**

| 构造 | sdk 频率 | 条目 | 现状 |
|---|---|---|---|
| `foreach`（数组快速路径/ref struct enumerator/await foreach 分型） | 海量 | C16 + C40 | 常规已处理；枚举器适配/ref 源缺；await 源在 C38 |
| `yield return/break` | **2887** | C04 | **已落地（iterator-lowering Implemented 默认关，b784b47f）**；pass 关闭时仍 fail-fast；sdk 最高频结构性缺口（P0 建议，见条目，默认开启门槛挂起） |
| `lock` | 32 | C29 | synchronized 直映；`Monitor.TryEnter` 超时/`Pulse/Wait` 静默丢失 |
| using 语句 / using 声明 | 多 | 内联 + C44（观察） | try-with-resources + 连续资源预扫描已做 |
| `await using` | — | C38 | AwaitKeyword 不检查，静默降级 |
| `catch when()` | 待精确计数 | C03 | fail-fast（注意过滤器重复求值语义） |
| goto / label / goto case | — | E14 | 已实现 |

**Layer 4 — 状态机与闭包（重量级 pass，生成新类型）**

| 构造 | sdk 频率 | 条目 | 现状 |
|---|---|---|---|
| lambda / 匿名方法 → display class | **4966** | C45（C32 为可变捕获子集；E05 仅匿名方法→lambda） | Java lambda 直映 + 转换器内 holder；async/ref lambda fail-fast |
| async/await；`Task<`/ConfigureAwait/ValueTask | await 1297 / Task< 311 / 789 / 11 | C18 | await→join 特判；async lambda 拒绝；MethodTransformer 43 处 async 特判 |
| await foreach / IAsyncEnumerable / IAsyncDisposable | — | C38 | 语句静默 + 类型映射/compat 双空白 |
| LINQ（IEnumerable / IQueryable / Expression） | Expression<> 2、IQueryable 0 | E16 + C26 | 过程式展开存在；**无 receiver 三路分流**（风险 17） |

**Layer 5 — 类型模型 / ABI（不是糖，每个都是 EF 级拦路虎；靠显式 ABI 决策 + compat 底座，不指望「降成普通 C#」）**

| 构造 | sdk 频率 | 条目 | 现状 |
|---|---|---|---|
| `Span` / `ReadOnlySpan` / `Memory` | 266 / 722 / 82 | C06 | ref struct 直接拒绝；compat 有 Span.java 等 |
| `stackalloc` / `ref struct` | 101 / 15 | C06 | fail-fast；仅 fixed 字段缓冲区有 FFM 兜底 |
| ref 返回 / `scoped` | 526 | E15/C01 + C06 | Core 已实现未接线；scoped 无处理 |
| `unsafe` / 指针 / `fixed` | 255 / 1136 / 135 | C06 | fail-fast 群；4 份 spec 已写；`delegate*` 函数指针子项待核（五次补全） |
| 无符号类型 | — | E09 | 已实现（Cs2jUnsignedMath 范式） |
| decimal | — | C34 | 二元 `+ - * /` 原样输出（不可编译 Java）；compat 方法面齐备 |
| `IntPtr`/`nint`/`Half`/`Int128`/`UInt128` | — | C41 | 无映射、无 compat、无显式拒绝 |
| 多维数组 / 数组协变 | — | E12（协变待补） | opt-in；多维/Rank 已处理，协变无 |
| `Nullable<T>` | — | C22 | compat + 零散特判 |
| 泛型逆变协变 / 约束 | — | C33 | 型变声明丢弃仅告警（CS2J1003） |
| `[InlineArray]` | — | C06 待盘点 | 无处理证据 |
| `System.Threading.Lock`（C# 13 lock 目标类型）/ 隐式 Span 转换（C# 14） | — | C29 / C52 | 待核实；Lock 直映 synchronized 语义错（scope 释放语义）；隐式转换改变重载决议 |

### 5.6 三笔架构债（本仓对照）

- **债务 A：主路径上有一整套 lowering 规则是「死代码」，但它们是新 pass 的半成品规格书。** `NewConversionPipeline.cs:27-33` 注册的 11 个 IR lowering pass（property/event/indexer/operator/refout/struct/using/yield/delegate/pattern match/变量解析）仅 `PipelineComparisonTests` 可达；生产主路径是 `ProjectConversionPipeline` → TypeGroupResolver → 巨型 transformer（`InvocationExpressionTransformer.cs` 已 12,927 行），边翻译边降级。规则移植到 Roslyn rewriter 后即可用 .NET 测试直接验证形态；**只能借形态，不能共用 IR 代码**（§4 风险 9）。
- **债务 B：预处理链是 `Program.cs` 手写的 14 段串联。** 每段独立复制整棵源树到 `.cs2j-*-src` → reparse → restore → 改写 → 下一段再复制一遍（`Program.cs:174-716`）；没有统一注册、没有顺序契约（只靠注释，如 `Program.cs:363-364,638`）、没有 fixpoint、residual 检查非标准件。加到 20+ pass 时会崩（慢、不可调试、顺序靠口口相传）——§5.4 的契约/编排是还债方向，迁移边界见风险 19。
- **债务 C：训练集与目标难度画像不匹配。** sdk 高频项（lambda 4966、局部函数 3486、yield 2887、await 1297、raw string 1345、ReadOnlySpan 722、指针 1136）练的是 BCL 数值/Span/序列化肌肉；而 `Expression<>` 2 处、`IQueryable` 0 处——恰是 EF Core 命门（表达式树 + query pipeline + DI）。还债动作就是 §5.1 的 EF Core 画像扫描，先扫再排期。（五次补全补注：错配有**两个轴**——领域轴（EF Core）与**语言版本轴**（.NET 10 源码用 C# 13/14，训练集零样本，见 §4 风险 20）；还债动作同一条：把目标源码纳入特性扫描基线。）

---

## 附：ROADMAP 十项待办对照

| ROADMAP 条目 | 现状 | 对应本清单 |
|--------------|------|------------|
| 1. pre process `default(T)` | 已实现（`GenericPreprocessor.cs:13-14`） | E10 |
| 2. pre process struct | 大部分实现：只读化（E13）已做；可变 struct clone 仍在 Java 侧 | E13 + C07；ref struct 在 C06 |
| 3. pre process ref/out | Core 完成、CLI/Java 未接 | E15 + C01 |
| 4. pre process goto | 已实现 | E14 |
| 5. some generics | 仅 3 模式；typeof/is-as/静态字段未做 | E10 + C02 |
| 6. `[ThreadStatic]` | 转换器内联处理中，未前置 | C09 |
| 7. attribute(json) | compat 注解桥接为主，无前置通道 | C10 |
| 8. method parameter default value | 已实现 | E08 |
| 9. local function | 已实现（带 residual 闸门） | E02 |
| 10. closed generic type metadata（Json、去 ThreadLocal） | 设计定稿、compat 有落点；cs2j 生成码补丁本树未见 | C30 + C02 |

> 注：三次/四次补全新增的 **C37–C46**、五次补全新增的 **C47–C52 均不在 ROADMAP 十项内**（ROADMAP 未覆盖模式表达式去糖、异步流/异步释放、foreach 迭代源二期、终结器、特殊数值类型、平台边界隔离、词法字面量归一、using 显式化、lambda 闭包提升、成员签名显式化；五次补全另增：相等性/字符串拼接调用点物化、params 集合、C# 14 扩展成员/`field`/空条件赋值、版本防线升格）；其中 C37 的测试红灯（8/11 失败）、C39 的静默丢弃、LinqRewriter 无 IQueryable 分流（C26）为盘点新发现并经 grep 核实；**C47–C52 为语言覆盖审计发现，仓库证据以各条目「验证」行动为准**。方向性输入（CsCore、统一引擎、EF Core 预言机、Layer×频率全景、三笔架构债）见 §5。
