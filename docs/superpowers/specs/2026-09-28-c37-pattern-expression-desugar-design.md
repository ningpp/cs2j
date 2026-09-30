# C37 模式表达式去糖（`is` 表达式 / switch 表达式臂 / 位置与列表模式）预处理器设计

**日期**: 2026-09-28
**范围**: 在 C03 已合入的同一 pass `pattern-desugar`（Core `PatternDesugar/` + CLI 适配器）上扩展**表达式侧**规则；不新增 pass、不新增中间目录、不翻转 stub、不修改任何 C#→Java 转换代码
**状态**: 设计稿，待评审（只调研与写文档；实现与 worktree 在 C03 合入后进行）
**注册表**: `pattern-desugar`（[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L71-L80)，catalogItems `C03, C37`，RunAfter=`null-coalesce`；C03 合入时翻转为 Implemented 并把 RunAfter 扩为 `{ null-coalesce, tuple, foreach-deconstruction }`，本任务不再动注册表）
**硬依赖**: 姊妹设计 [2026-09-28-c03-pattern-statement-desugar-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c03-pattern-statement-desugar-design.md)（下称「C03 设计」）先合入；本设计以其 §4.4 冻结的三个扩展点（`IPatternReducer`、`IPatternDesugarComponent`、命名/绑定服务）为准
**Catalog 条目**: `prompts/preprocessor-catalog.md` C37（P1，L782-794）、C03（P0，L304-317）、§3.3 控制流去糖族（L1076-1093）、§4 风险 15（L1113）/风险 8（L1106）、C35（L750-762）
**基线提交**: `67477551`（`feat(preprocessing): register 23 merged-family stub passes`，已核实为当前 HEAD `a37c20e3` 的祖先）；实现前先把 C03 分支合入本任务分支

---

## 1. 背景与目标

### 1.1 表达式侧是「静默错误输出」，不是 fail-fast

与 C03 的语句侧（每命中一处抛 `NotSupportedException`）不同，模式在**表达式位置**今天不报错——它被替换成恒真 `true` 或半残三元链流入 Java 产物（catalog §4.12 列举的典型静默错误输出，风险 15 本族）：

| 站点 | 现状 | 行为 |
|---|---|---|
| `is` 模式 | [TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L107-L117) L107-117 | 仅 3 条分支：`DeclarationPattern`、null 字面量常量、`DiscardPattern`；其余全部兜底为 L115 `/* TODO: unsupported pattern */ true`——条件恒真、语义完全丢失 |
| switch *表达式* | [ControlFlowTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L449-L507) L449-507 | 反向折叠三元链；`BuildSwitchArmCondition` 覆盖较多臂种类，但未知模式落 L505 `/* TODO: pattern {kind} */ true` |
| 递归/位置模式 | 同文件 L521-561 | `BuildRecursivePatternCondition` 只桥接 PropertyPatternClause 的 getter（`getX()`），**PositionalPatternClause 无任何分支**；Designation 靠 `(v = (T)x) != null` 副作用赋值（L551-556） |
| `var` 模式 | 同文件 L563-570 | `(v = x) != null || true` 的副作用近似；非泛型 `is var v` 在 is 表达式侧同样落恒真兜底 |
| 列表/slice 模式 | 全仓无分支 | `ListPatternSyntax`/`SlicePatternSyntax` 在 is 表达式侧落 L115、在 switch 臂落 L505 |

派发点只有两处（[TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L28) L28、[ControlFlowTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L48) L48），这也是本族归约器必须在进入主转换器前消灭上述语法面的证据。

### 1.2 PatternMatchingTests 表达式侧红灯（2026-09-28 复核）

- [PatternMatchingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/PatternMatchingTests.cs)（ConversionTests，11 例，直连单文件 `ConversionPipeline`）：**8 失败 / 3 通过**。其中表达式侧 7 例失败全部因恒真兜底：`IsConstantPattern`、`RelationalPattern`、`LogicalPattern`、`OrPattern`、`NegatedPattern`、`NotTypePattern`，以及 `PropertyPattern_ConvertsWithTodoMarker`——该用例期望的 `/* TODO: recursive pattern */ p` 文本在 src 全仓已不存在（风险 15 记载的「实现/预期双向脱节」）；`IsDeclarationUsed` 的失败是 `s.Length → s.length()/getLength()` 成员映射口径，非模式问题。
- 通过的 3 例中 `PatternMatchingNoCSharpResidue` 在恒真兜底下**空洞通过**（`is` 被整体替换为 `true`，自然无 C# 残留）——C37 消灭兜底后该断言自然变严格。
- [PatternMatchingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PatternMatchingTests.cs)（Tests，10 例）：3 失败均为语句侧（C03 范围）；**7 通过例覆盖 switch 表达式既有臂编译**（Not/Type/Relational/Declaration/RecursiveProperty）——C37 改造后这些用例改走「先去糖再转换」组合夹具，Java 侧断言必须全部保持。
- 另有 [PatternBulkTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/PatternBulkTests.cs) 10 例（多为 is 关系/逻辑/常量组合的 Java 文本期望），C37 同样要转绿或经组合夹具保持。

### 1.3 与 C03 语句侧的分工

C03 设计 §3「锚点职责边界」已定死：

- **C03（先合）**：创建 `PatternDesugar/` 全部脚手架、PatternReducer 基干与语句侧模式种类（常量/type/declaration/discard/var/relational/and-or/not/parenthesized/property）、两个语句组件（`SwitchStatementComponent`、`CatchClauseComponent`）、CLI 适配器、翻转 stub；位置/列表/slice 在 C03 阶段以 PD010 显式报错（C03 D6/D7）。
- **C37（本设计）**：只做三件事——① 在 PatternReducer 注册 Positional/List/Slice reducer；② 新增并注册 `IsPatternExpressionComponent`、`SwitchExpressionComponent` 及配套的表达式侧临时变量提升器；③ 放开 C03 D7 守卫扫描的 PD010 闸门、取消 C03 §7.2 登记的 Skip、转绿全部表达式侧红测、硬化转换器 L115/L505 兜底。

### 1.4 为什么表达式侧需要「表达式→语句」提升

归约产物不只是一个布尔表达式，下列形态必须产生**前置语句**（catalog L787④、L1087 的「单次求值临时变量提升」基建）：

1. **绑定变量在条件外被使用**：`if (o is Point p && p.X > 0) {}`、switch 臂 `string s => s.Trim()`——声明模式经 `is T v` 一次绑定，但递归/var/位置/列表模式的绑定需要显式赋值或 out 临时。
2. **位置模式的 Deconstruct 调用**：`x is Point(1, _)` 必须先 `x.Deconstruct(out var d0, out var d1);` 再比较，是语句不是表达式。
3. **列表模式的长度/索引与元素单例化**：每个被多次引用的索引元素只取一次。
4. **governing/subject 单次求值**：`Foo() switch { … }`、`GetP() is { X: 1 }` 中的方法调用只能出现一次（C# 模式的确定语义）。
5. **switch 表达式需要结果变量**：三元链装不下带前置语句的臂时，提升为 `T __cs2jSw_n; if (…) { …; __cs2jSw_n = arm; } …`，原表达式位置替换为标识符；表达式体成员/lambda 随之把 `=> expr` 展开为块体。

目标形态总纲：**普通布尔表达式 + 确定性命名的单例临时变量**；switch 表达式默认三元链、需要前置语句时提升为 if-else 块（§2 D2-D4）。产物为可编译 C#，.NET 半区由原项目测试守门，Java 半区由现有转换器既有形态消费（`is T v`/三元/instanceof 均为生产已接受路径）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 备注 |
|---|---|---|---|
| D1 | 总体形态 | 语义驱动逐文件 pass，复用 C03 已建的 dirty check → Analyze → Rewrite → Formatter → residual 复检外形；表达式侧以**两个 IPatternDesugarComponent + 一个两遍提升器**接入，不改入口签名 | C03 设计 §4.1/§4.4；干净文件字节级保留 |
| D2 | is 表达式默认产物 | reducer 输出 `PatternReduction(Condition, Prologue, Supported)`：无 Prologue 时原地把 `x is <pattern>` 替换为普通布尔表达式；有 Prologue 时按 D9 插入前置语句 | 归约只含 `==`/`!=`/`is T`/`is T v`/`>` 等比较、`&&`/`||`/`!`、成员/字段访问、类型转换与方法调用 |
| D3 | switch 表达式形态 A：三元链 | 臂条件与臂值全部无 Prologue、无泛型声明绑定时，保持现有「反向折叠」序：`(cond && (when) ? armValue : <rest>)`，末臂 discard 直接作 else 值；governing 单例化为链前临时 | C# 条件表达式 true 分支可见条件中 `is T v` 的模式变量，臂值引用绑定合法；现有 7 个 Tests 用例证明 Java 侧已消费该形态 |
| D4 | switch 表达式形态 B：块提升 | 任一臂需要 Prologue（位置/列表/var 绑定）、含泛型声明模式（D12）、或原表达式无法就地插入语句时：声明结果临时 `T __cs2jSw_n;`，臂 → `if (cond) { <prologue>; __cs2jSw_n = arm; } else if …`，原表达式替换为 `__cs2jSw_n`；表达式体方法/属性/lambda 同步展开为块体并 `return`；无 discard 末臂时末支为 `throw new global::System.Runtime.CompilerServices.SwitchExpressionException()` | 结果临时类型取 `SemanticModel.GetTypeInfo(switchExpr).Type` 显式声明（不能用 `var;`）；throw 形态精确保留 C# 非穷尽语义，比任何近似值安全 |
| D5 | subject/governing 单次求值 | ① switch governing 一律提升 `__cs2jGov_n`（与 C03 D11 同策略，简单标识符也提升，确定性优先）；② is 表达式**选择性提升**：subject 非简单表达式（invocation/element access/conditional access/assignment 等，谓词参照 [JavaKeywordSyntaxFacts.IsSimpleExpression](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs#L45-L54)）**或**归约后引用超过一次（or/常量装箱/递归多成员）时提升 `__cs2jPat_n`，其余直接用原表达式 | 避免给 `x is > 0` 这类无副作用用例制造噪声；计数进 Statistics |
| D6 | 常量模式 | subject 静态类型即可直接比较时 → `subj == c`（null 常量 → `subj == null`，沿用 C03 D5）；subject 为 `object`/接口等装箱上下文时 → `subj is <常量类型> __cs2jC_n && __cs2jC_n == c`（先类型测试再拆箱比较，精确复刻 C# 常量模式「类型不符即假、null 即假」语义；字符串同形走 `is string t && t == c`） | 禁止发 `object.Equals(…)` 近似——它对 NewSpan 等场景语义不等价；类型符号由 `GetTypeInfo(subject)` 与常量表达式类型共同判定 |
| D7 | 关系/逻辑/not/parenthesized | relational → `subj > c` 等（运算符文本透传）；装箱 subject 先绑定数值临时 `subj is int __n && __n > c`；and/or → 括号合取/析取（沿用 C03 括号层级）；not → `!(…)`；parenthesized 透传 | 与 C03 reducer 同一批注册项，表达式侧不另写 |
| D8 | 属性/递归模式 | 引用类型 subject 先 `subj != null`（值类型省略）；带类型的递归模式归约为 `subj is T v`（v 即 Designation，无 Designation 时用 discard 式 `subj is T`）；各 PropertySubpatternClause 子模式以 `v.Member`（公共字段或属性，符号由 SemanticModel 解析，C# 侧保持 `p.X` 不调 getter 名）为新 subject 递归归约后合取；嵌套递归同构展开 | C# 侧不出现 `getX()`；L521-561 的 getter 桥接只留在 Java 路径供历史产物对照 |
| D9 | 模式变量作用域与 Prologue 插入位 | 绑定全部经 `is T v`（声明/递归模式）或显式赋值临时（var/位置/列表）获得，作用域分三档：① **分支块内**（switch 块提升臂、被包裹的 if 分支）——同名变量靠块作用域隔离；② **锚点语句前**（subject/结果/out 临时）——仿 [NullCoalesceSyntaxRewriter PendingDeclaration](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L27-L30) 的两遍插入；③ **静态类型保证时无条件前置**，否则用守卫包裹进嵌套块（D10） | 不做「链外预声明 + 赋值」，避开确定赋值/null 态问题（同 C03 D11） |
| D10 | 位置模式（PositionalPatternClause） | 按 SemanticModel 解析的主体符号三分流：① **元组形态（E06 产物）**：E06 已把 ValueTuple 分发为生成 struct（Item1…字段），子模式 → `v.Item1 == …` 字段访问链；② **Deconstruct 形态**：仿照 [ForeachDeconstructionRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L155-L172) L155-172 的 `HasDeconstruct` 检测找实例 `Deconstruct(out …)`，生成 `v.Deconstruct(out T0 __d0, out T1 __d1);`（单次调用）+ 各子模式对 `__dN` 递归归约；③ record 主构造位置模式走同一 Deconstruct 路径（record 合成 Deconstruct）。subject 静态类型不保证时，类型/null 守卫与 Deconstruct 一起进**守卫分支块**（`if (subj is Point v) { v.Deconstruct(…); if (子条件) 原分支体 }`），不在无保护锚点前调用 | 扩展方法 Deconstruct 一期不支持（E07 同口径）→ PD010 并标注；位置子模式为 discard 不发比较；`or` 两侧位置模式各自一份 out 临时，禁止合并求值 |
| D11 | 列表模式 | SemanticModel 判定可数/可索引形态：数组 → `Length`/`[]`；string → `Length`/`[]`；具备可访问 `Length` 或 `Count` int 属性且有 `this[int]` 索引器的类型（含 `Span`/`ReadOnlySpan`/`IList<T>`/`IReadOnlyList<T>`，按编译器同款成员探测）→ 用探测到的成员名。无 Slice：`count == n && s[0] …`；有无命名 Slice：固定元素定前后下标，长度约束改为 `count >= before+after`；被多次引用或子模式复杂的索引元素提升为单次取值临时；可空引用类型 subject 前置 null 检查；元素子模式递归归约 | 索引统一发 `s[s.Count - k]` 形式，**不发 hat 表达式**（`^k` 属 C31 范围，避免给后链制造未支持语法） |
| D12 | Slice 与列表模式边界 | 无 designation 的 `..` 一期支持（仅长度约束）；带变量的 slice（`.. var mid`）需要范围/切片语义（与 C31/C36 交界），一期 **PD010 显式报错**；探测不到可数+索引器成员的主体 → PD010；列表模式作用于 IEnumerable（无索引器）→ PD010，不发 `ElementAt` 近似 | 禁止长度/索引静默近似；报错消息给出模式种类与缺失成员 |
| D13 | discard / var 模式 | discard → `true`（不产绑定）；var discard 同；`var v` → Prologue `var v = subj;` + 条件 `true`（在三元形态中无 Prologue 位，故含 var 绑定的 switch 表达式强制走 D4 块提升；is 表达式中 var 绑定走锚点前/守卫块） | 消灭 L563-570 `(v = x) != null || true` 副作用近似 |
| D14 | null 检查顺序 | 统一顺序：null/常量 null 判定 → 类型测试（隐含非空）→ Deconstruct/成员/索引访问；引用类型递归/位置/列表模式的 null 检查由 reducer 自动前置，值类型省略；`not null` → `subj != null`、`not T` → `!(subj is T)` | 顺序错会导致对 null 解引用/NPE，属语义红线，fixture 必测 |
| D15 | when 守卫（switch 表达式臂） | 守卫原样保留并整体括入 `(armCond && (<when>))`，守卫中的模式节点由本 pass 同一 reducer 递归归约（即放开 C03 D7 闸门中属 C37 的种类）；守卫可见臂绑定（块提升时绑定声明在臂块顶部，守卫在其后） | 括号不变式同 C03 D3（SwitchCaseWhenClausePrecedenceTests 教训） |
| D16 | 表达式树 skip | 位于 `Expression<TDelegate>` lambda 内的模式节点**整体跳过、不改写**（一期实际语言面极窄：表达式树不支持 is 模式/switch 表达式，命中即 C# 编译错误；判定仍必做）。判定复用 [JavaKeywordSyntaxFacts.IsExpressionTreeLambda](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs#L57-L58) 的 ConvertedType 前缀法（内部委托 [ExpressionTreeFacts](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs#L15-L18)），Analyzer 自 lambda 节点向其子树打 skip 区；skip 计 Info（PD012），不计 residual | 风险 8 的多通道共享判定工具落地前不新建公共副本，评审中确认提升为共享 helper 的时机（§11） |
| D17 | 不可提升上下文 | 字段/属性初始化器、特性参数、默认参数值、构造器初始化器中的 switch 表达式：三元形态（无 Prologue）可就地保留；必须块提升才能归约时 **PD010 显式报错**，不产出半成品；表达式体方法/属性/lambda 一律展开为块体（D4），不报错 | 锚点探测沿用 E03 [FindAnchorStatement](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L312-L322) 返回 null 的同款判定，但 E03 现状是 Warning+skip（L107-113），本族按风险 15 升为 Error |
| D18 | 与 JavaGenericPatternRewriter 分工 | 输出保留非泛型 `subj is T v`（主转换器 L118-125 已映射 Java 16 instanceof 绑定）；**泛型声明模式只允许出现在块提升形态的 if 条件中**——由 [JavaGenericPatternRewriter](file:///d:/code/cs2j/src/CSharpToJava.Core/Rewriters/JavaGenericPatternRewriter.cs#L110-L129) 的 VisitIf/While/Do/For 四个条件语句入口（L20-56）自然完成「TypePattern + 段首强转声明」；三元形态的选择谓词发现泛型声明模式（借用其 IsGenericTypeSyntax 判定思路）即改走 D4 块提升，不让泛型绑定进入三元条件（rewriter 不访问三元表达式） | 落实风险 15 的分工要求：C37 不接管泛型提升，只通过形态选择保证两组件作用节点不重叠；须回归 rewriter 四个入口 |
| D19 | 既有窄分支去留 | 归约后 is 表达式输出允许残留三类**主转换器已支持**的语法面：`is T`、`is T v`（非泛型）、`is null`；residual 闸门按此白名单计数。switch 表达式输出中残留数必须为 0（主转换器虽有半残三元链，但其产物含 TODO 文本，不允许作为本 pass 的输出终点） | 白名单即 residual 测试的断言基准 |
| D20 | 转换器兜底硬化 | L115 与 L505 两处 `_ => … true` 在本特性合入后改为 `throw new NotSupportedException("CS2J normalization required: pattern … must be lowered by pattern-desugar")`（fail-fast 安全网），作为**本特性的收尾 PR**；C03 设计 §6 已把该项登记移交 C37 | 与 SwitchAndResource 五个 normalization 异常同体例；转换即抛优于静默真 |
| D21 | 测试期望同步 | ① ConversionTests 7 个表达式侧红例 + PropertyPattern（取消 C03 登记的 Skip、删过时 TODO 文本断言）转绿，走 `DesugarThenConvert` 组合夹具；② Tests 工程 7 个 switch 表达式/is 既有例同夹具、Java 断言全保留；③ PatternBulkTests 同夹具；④ `IsDeclarationUsed` 的 length/getLength 差异保持 C03 登记的独立 issue，不进本 pass | PatternMatchingTests 11/11 全绿是 C37 完成线（C03 设计 §11.1） |

---

## 3. 链位与族契约

**零注册表改动**。本任务在 C03 合入后的代码上开发：

| 契约项 | 值 | 说明 |
|---|---|---|
| Pass Id | `pattern-desugar` | 不变；不新增任何 pass id / stub / 中间目录 |
| 目录 | `src/CSharpToJava.Core/PatternDesugar/`、`.cs2j-pattern-desugar-src` | 与 C03 共用 |
| Status | `Implemented`（C03 已翻转） | C37 不触碰 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L71-L80) 与 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 描述符表 |
| RunAfterIds | `{ null-coalesce, tuple, foreach-deconstruction }` | C03 翻转时一次扩足（C03 设计 §3）；C37 **不再改链**。位置模式 D10 直接依赖该锚点：E06 tuple-to-struct 已把元组分发为 ItemN 字段 struct，E07 已确立 Deconstruct 命名/检测契约——本任务在二者之后消费其产物，与 catalog L790 一致 |
| CatalogItems | `C03, C37` | 同一 id 覆盖两条 catalog 的注册语义不变 |
| 接入方式 | C03 冻结的组件槽：`IsPatternExpressionComponent`、`SwitchExpressionComponent` 追加进 `PatternDesugarPreprocessor` 的有序组件列表；Positional/List/Slice reducer 追加进 reducer 注册表 | 符合 C03 §4.4「C37 只加注册项、不改调用方」 |
| 链序测试 | 无新增断言义务 | [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L57) 的 `LockedChain`/`EnabledByDefault` 已在 C03 PR 处理；C37 仅可能配合默认开动作（§8）改 `EnabledByDefault`，且不得把顺序改回 |

依赖声明：**C03 分支先合入是本任务的硬前置**。分支策略为基线 `67477551` + 合入 C03 分支后开发（§12），不从裸 `67477551` 拉分支，避免对 `PatternReducer.cs`/`PatternDesugarAnalyzer.cs` 等热文件做反向合并。

---

## 4. Core 组件设计

全部位于 C03 新建的 `src/CSharpToJava.Core/PatternDesugar/`（命名空间 `CSharpToJava.Core.PatternDesugar`）。C37 **新增 5 个文件、小改 4 个 C03 文件**，不新建目录：

```
src/CSharpToJava.Core/PatternDesugar/
  # ── C37 新增 ──
  ExpressionPatternRewriter.cs     # IsPatternExpressionComponent：is 表达式归约 + 装箱临时/守卫包裹
  SwitchExpressionLowerer.cs       # SwitchExpressionComponent：三元链/块提升选型、臂归约、=> 块体展开
  PatternTempLifter.cs             # 第二遍提升器：PendingDeclaration 锚点插入、结果临时、表达式体展开
  PositionalPatternReducer.cs      # IPatternReducer：元组 ItemN / Deconstruct / record 位置模式
  ListPatternReducer.cs            # IPatternReducer：数组/string/可数+索引器、Slice（无命名）
  # ── C03 已有、C37 小改 ──
  PatternDesugarPreprocessor.cs    # 组件列表注册两个新组件；reducer 注册表追加三项
  PatternDesugarAnalyzer.cs        # 表达式侧扫描、表达式树 skip 区、表达式体成员登记
  PatternDesugarStatistics.cs      # 表达式侧计数字段（§5）
  PatternDesugarDiagnostic.cs      # 无新码段则不动；PD012 见 §6
  PatternReducer.cs                # 原则上只追加注册；PatternReduction 形状不动（Prologue 已预留）
```

### 4.1 `ExpressionPatternRewriter`（is 表达式组件）

实现 C03 §4.4 的 `IPatternDesugarComponent`：

- **Analyze**：扫描 `IsPatternExpressionSyntax`，跳过：① D16 表达式树 skip 区；② Pattern 已是「白留三形态」且归约无变化的节点（`is T`、非泛型 `is T v`、`is null`、`is _`）——这些文件可能整体 clean。对剩余节点产出 `IsPatternPlan`：subject 类型符号、归约得到的 `PatternReduction`、所需临时清单、插入档（锚点前/守卫块/仅表达式替换）、不可提升标记（D17）。
- **Rewrite**：自底向上（先处理嵌套 is，如 `when`/属性子模式内层）：无 Prologue → 直接替换为 `Condition`；有 Prologue → 生成 `PendingDeclaration`（复用 C03 扩展点三的序号服务，名字段 `__cs2jPat_n`/`__cs2jC_n`/`__cs2jD_n`）并把节点替换为条件表达式；需要守卫包裹时（D10 静态类型不保证 + Deconstruct）登记一条「if 条件分裂」重写，把原 `if (cond) body` 重写为 `if (subj is T v) { v.Deconstruct(…); if (子条件) body }`，while/do/for 条件同构（与 JavaGenericPatternRewriter 四入口同覆盖面，但插入的是 C# 块语句）。
- 装箱常量/关系模式（D6/D7）产生的隐式绑定临时一律 `is T __n` 形态，不写显式强转。

### 4.2 `SwitchExpressionLowerer`（switch 表达式组件）

- **Analyze**：枚举全部 `SwitchExpressionSyntax`（含嵌套于其它 switch 臂/对象初始化器/查询中的），逐臂调 PatternReducer，得到「三元可行 / 必须块提升」选型（D3/D4/D12/D18）；登记结果临时类型、非穷尽末支、when 守卫内归约计划、表达式体成员/lambda 展开计划。
- **Rewrite（形态 A）**：保持 [ControlFlowTransformer.cs L454-471](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L454-L471) 已验证的反向折叠顺序：discard 末臂作根值，其余臂 `(cond ? armValue : rest)`；但条件/臂值由 reducer 在 **C# 语法树层**生成（SyntaxFactory 构造，非字符串拼接），governing 临时声明进 PendingDeclaration。
- **Rewrite（形态 B）**：把含 switch 表达式的锚点语句整体重写为「结果临时声明 + if/else-if 赋值链 + 锚点语句（表达式位换成结果变量）」三语句序列；臂块内先放 Prologue 再赋值；表达式体方法/属性/索引器/get 访问器与箭头 lambda 展开为块体并补 `return __cs2jSw_n;`；void 上下文不会出现 switch 表达式（编译器基线保证）。
- 非穷尽：无 discard/var 兜底臂时末支 `throw new SwitchExpressionException()`（D4），using 别名由输出侧常规解析，必要时用 fully-qualified `global::System.Runtime.CompilerServices.SwitchExpressionException`。
- 嵌套 switch 表达式：内层先于外层处理（自底向上），内层块提升后的结果临时对外层只是一个标识符。

### 4.3 `PatternTempLifter`（两遍提升器）

形态复刻 E03 两遍法（[NullCoalesceStatementLifter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs) 的锚点前插入）但服务两类锚点：

1. **块锚点**：`PendingDeclaration(Declaration, AnchorStatement, Order)` 插入最近外层 Block 的锚点语句之前（E03 同款）。
2. **成员块锚点**（C37 新增）：箭头成员/lambda 展开时，把整个成员体替换为 Block 后，临时声明进新块的语句列表头部；结果临时的声明-赋值跨语句由 lowerer 保证确定赋值（每分支都赋值或末尾 throw，结构上穷尽 if/else-if/else）。

提升器是幂等的纯语句插入，名字全部来自 C03 扩展点三的确定性序号源（同输入同输出，不依赖访问顺序）。无法找到锚点且非三元可行的计划不在提升器兜底，统一由 Analyzer 阶段 PD010（D17）。

### 4.4 reducer 新增注册项

在 C03 `PatternReducer` 的种类注册表追加（`IPatternReducer.CanReduce/Reduce`，不改接口）：

- `PositionalPatternReducer`：`PositionalPatternSyntax`（含 `PositionalPatternClauseSyntax`，含递归模式上的位置子句）。主体符号解析顺序：E06 生成 struct/元组类型（`IsTupleType` 或生成 struct 形态，ItemN 字段）→ 实例 Deconstruct（含 record 合成方法）→ 不支持 → PD010。输出的 Condition 只引用 out 临时/字段，Prologue 承载至多一次 Deconstruct 调用。
- `ListPatternReducer`：`ListPatternSyntax` + 子节点 `SlicePatternSyntax`。成员探测：数组/string 特判，其余按「可访问无参 int `Length`/`Count` 属性 + `this[int]` 索引器」编译器同款规则用 SemanticModel 查找；产出长度条件、索引条件、元素单例临时（Prologue）；带名 slice → PD010（D12）。
- 两个 reducer 的子模式递归回调同一 PatternReducer，因此位置/列表内可嵌套关系、常量、属性、or/not、甚至嵌套位置/列表（`is [[1]]`）。

C03 已注册的 10 类 reducer（常量/type/declaration/discard/var/relational/binary/unary/parenthesized/recursive-property）原样复用；表达式侧与语句侧的差异只在 Prologue 的消费方式（C03 语句侧基本为空，C37 消费它）。

### 4.5 Analyzer 扩展与语义模型依赖

在 `PatternDesugarAnalyzer` 增加（与 C03 的 switch/catch 计划并列，不改其计划模型）：

- `ExpressionSideScan`：is 表达式/switch 表达式清单、表达式树 skip 区（D16）、箭头成员登记、按文件的临时序号预算。
- 守卫内 is 模式扫描（C03 D7 的 C37 侧）：C03 对「位置/列表等 C37 种类」报 PD010 的判定表，C37 合入后从报错集移除并交给对应 reducer。

| 用途 | API |
|---|---|
| subject/常量装箱判定 | `GetTypeInfo(subject).Type`、`GetTypeInfo(constantExpr).Type`，值类型/引用类型/null 性 |
| 位置模式成员解析 | `GetTypeInfo(pattern).Type`（主体类型）→ tuple/struct ItemN 字段或 `GetMembers("Deconstruct")` 的 out 参数签名；参数类型用于 out 临时声明 |
| 列表可数/索引器 | 数组符号特判；`GetMembers` 查 `Length`/`Count` 属性与 `this[]`（参数 int、返回非 void） |
| switch 结果类型 | `GetTypeInfo(switchExpression).Type`（D4 结果临时） |
| 模式成员（字段 vs 属性） | 沿用 C03 §4.5 的 `GetSymbolInfo`/`GetDeclaredSymbol` |
| 表达式树判定 | lambda 节点 `GetTypeInfo(lambda).ConvertedType` 前缀 `global::System.Linq.Expressions.Expression<`（D16） |

### 4.6 residual 复检

在 C03 语义 residual 复检上扩计数（输出树，表达式树 skip 区除外）：

- `SwitchExpressionSyntax` 计数必须为 **0**。
- `IsPatternExpressionSyntax` 仅允许 D19 白留三形态（TypePattern、非泛型 DeclarationPattern、null 常量/Discard）；其余 PatternSyntax 种类（Relational/Binary/Unary/Recursive/Positional/List/Slice/非 null Constant/Var）计数必须为 **0**。
- 不为 0 → PD020（Strict=Error，否则 Warning），与 C03 同一闸门。

---

## 5. CLI 集成

**零注册表、零工厂改动**：C03 已新增 [Adapters/PatternDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/PatternDesugarPassAdapter.cs)（对照 [EnumPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs)），`PassChainFactory.CreateAdapter` 分支已在 C03 PR 落地。C37 对 CLI 侧的全部改动仅：

1. `PatternDesugarPassAdapter.BuildChainSummaryLine()` 追加表达式侧计数（C03 已建 Statistics 对象，新增字段不破坏外形）：
   `is-expr reduced`、`switch-expr ternary`、`switch-expr lifted`、`positional`、`list`、`property`、`pattern temps`、`arrow bodies expanded`、`expression-tree skipped`、`residual`。
2. `AfterFilesAsync` 的 PD020 residual 汇总自动覆盖表达式侧（同一计数器累加，C03 已有代码，无需新增分支）。
3. 若新增 PD012（Info），其显示遵循适配器既有「Info 仅 verbose」规则，不改退出码语义。
4. 无 `Program.cs` 改动、无独立 verb、无支撑库（`PassSupportLibrary.None` 不变）、保留目录名/指纹令牌 `pass:pattern-desugar=…` 不变（descriptor 驱动，C37 不引起指纹失效）。

---

## 6. 诊断策略

原则同 C03 §6（catalog §4.12 第一原则）：**无法等价表达时显式报错，禁止注释/占位/原样透传**。

| 码 | 级别 | C37 含义 |
|---|---|---|
| PD001 | Info | 逐文件 transformed/clean/dirty-no-rewrite（verbose） |
| PD010 | Error（Strict 可降 Warning，不允许默认关） | 表达式侧不可归约：扩展方法 Deconstruct 位置模式（D10）、带名 slice / 不可数列表主体（D12）、不可提升上下文中的块提升需求（D17）、reducer 无分支的 PatternSyntax 种类；消息含文件、行列、模式语法名与缺失条件 |
| PD012 | Info（新增，verbose） | 表达式树 lambda 内模式节点跳过（D16），消息含外层 lambda 位置 |
| PD020 | Error/Warning（Strict） | §4.6 residual 复检不为 0（含 switch 表达式残留、非白留 is 模式残留） |
| PD030 | Error | restore/build/test 失败（适配器标准三映射，C03 已建） |

**恒真占位消灭路径**（与 C03 §6 登记一致，C37 是执行方）：

1. C37 默认开启后，[TypeOperationTransformer.cs L115](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L115) 与 [ControlFlowTransformer.cs L505](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L505) 对预处理后输入成为不可达；收尾 PR（D20）把两处兜底改为 `NotSupportedException` fail-fast，并删除 L521-561 中已被 C# 侧归约取代的位置/属性桥接分支（属性 getter 桥接在去糖产物上同样不可达，按「先加异常观察一个版本、再删分支」两步走，避免影响未启用 pass 的旧链）。
2. residual 闸门在工程级证明目标语法面计数为 0，不靠人工读码。
3. 转换器改动属 C37 交付的一部分但独立成 PR，与 Core/CLI PR 分开评审（保证中间树先稳定）。

---

## 7. 测试计划

### 7.1 Core 单测（新增 `tests/CSharpToJava.Tests/PatternExpressionDesugarCoreTests.cs`，内联源码 → `CSharpCompilation` → `PatternDesugarPreprocessor.Preprocess` → 归一化空白断言 + 输出可编译断言；与 C03 的 `PatternDesugarCoreTests.cs` 同夹具）

- **is 各模式**：常量（int/string/null/装箱 `object o is 1` 产 `is int t && t == 1`）、关系（`> < >= <=`，int 直比 + object 装箱绑定）、`and`/`or`/括号嵌套（`>= 0 and <= 10`、`1 or 2 or 3`）、`not null`/`not T`、discard、`var v`（Prologue 赋值）、type/declaration 白留不改（字节级/节点级不变）。
- **属性/递归**：`p is { X: 1 }`（引用类型含 null 前置）、值类型 struct 无 null 检查、公共字段与属性两种成员、嵌套 `{ A: { B: > 0 } }`、带类型与 Designation `o is Point { X: 0 } p`（产 `o is Point p && p.X == 0`）。
- **位置**：元组/生成 struct（Item1/Item2 字段链）、`Point(1, _)`（单次 Deconstruct + out 临时 + discard 无比较）、record 位置模式、subject 为 object 时的守卫分支块（`if (o is Point(…))` 双层 if 形态）、`or` 两侧各自 Deconstruct（调用计数断言）、扩展方法 Deconstruct → PD010。
- **列表**：数组（Length）、`List<T>`（Count+索引器）、string、无 slice 精确长度、`[1, .., n]` 最小长度与尾下标 `count - 1`（断言无 `^` hat 语法）、嵌套 `is [[1]]`、`.. var mid` → PD010、`IEnumerable` 主体 → PD010、可空主体 null 前置、索引元素单次取值（用带副作用索引器的夹具断言取值次数）。
- **switch 表达式全臂**：纯三元形态（常量/type/关系/not/and-or/discard 末臂）、when 守卫整体括号（`string s when A || B` 形态）、非穷尽无 discard（末支 `throw new SwitchExpressionException()`）、嵌套 switch 表达式、null 臂。
- **块提升**：位置臂、列表臂、`var` 臂、泛型声明臂（D18：选块提升且输出可被 JavaGenericPatternRewriter 消费——组合断言）、结果临时类型正确（值类型/可空/引用类型各一）、表达式体方法/属性/lambda 展开为块体 + return。
- **模式变量作用域**：if 条件绑定在体中使用、三元 true 分支绑定可见、两个分支同名 `i` 块隔离、守卫引用绑定、臂值引用绑定。
- **单次求值**：subject/governing 为方法调用/属性访问/带副作用索引器时输出中恰好一次调用（临时提升）；简单标识符不提升（D5 选择性断言）；Deconstruct 恰好一次。
- **上下文**：is/switch 出现在 if/while/do/for 条件、三元、return、实参、字段初始化器（三元可行通过；必须块提升 → PD010）、表达式体成员。
- **表达式树**：`Expression<Func<…>>` 内出现的模式节点保持原样 + PD012（构造合法 C# 的近似形态：表达式树内仅放可编译的简单节点，skip 行为用「外层 lambda 被改写、内层模式不动」的可编译夹具验证）。
- **诊断**：各类 PD010 消息含行列与模式名；residual 人工注入 → PD020；非 Strict 为 Warning。
- 每个正例断言输出 `dotnet` 可编译（零 error）且 §4.6 residual 计数为 0。

### 7.2 修订既有用例（D21）

- 新增测试辅助 `DesugarThenConvert(source)`（先 `PatternDesugarPreprocessor.Preprocess` 再 `ConversionPipeline.Convert`），与 C03 §7.2 同一辅助、同一文件，两个 worktree 共用。
- ConversionTests [PatternMatchingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/PatternMatchingTests.cs)：7 个表达式侧红例转绿（Java 文本断言保留：`o == 5`、`x > 0`、`(x >= 0 && x <= 10)`、`!(o == null)`、`(o == 1 || o == 2)`、`!(o instanceof String)` 等）；`PropertyPattern_ConvertsWithTodoMarker` 取消 C03 登记的 Skip、删除过时 TODO 文本断言，改为断言成员比较产物；`PatternMatchingNoCSharpResidue` 在兜底硬化后自然变严，保持断言文本不改。
- [PatternBulkTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/PatternBulkTests.cs)：同夹具全绿，Java 期望文本不变。
- Tests 工程 [PatternMatchingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PatternMatchingTests.cs)：7 个既有通过例（switch 表达式 Not/Type/Relational/Declaration/RecursiveProperty + is 两例）走组合夹具，断言（`instanceof String s`、`x > 0`、`getX()` 等）全部保留——证明去糖前后 Java 产物不回归。

### 7.3 项目级 fixture

扩展 C03 §7.3 新建的 `tests/SamplePatternSolution/`（不新建第二套 sln）：

- Lib 增加表达式侧文件：is 全模式（含在 if/while/三元/return/实参位置的绑定使用）、switch 表达式（全臂种类 + when + 非穷尽）、位置模式（record `Point`/元组/自定义 Deconstruct）、属性模式、列表模式（数组/List/string/slice）、表达式体成员中的 switch 表达式（块展开）。
- xunit 运行时行为测试锁语义：分支命中值、null 不落分支、governing/subject 副作用计数为 1、非穷尽 switch 抛 `SwitchExpressionException`、filter/守卫短路。
- App 跨文件调用，保证临时变量不跨文件泄漏。
- `PatternDesugarProjectTests`（C03 建）扩断言：Success、表达式侧统计字段、中间树无 switch 表达式与非白留 is 模式、`dotnet build`/`dotnet test -c Release` 退出码 0、干净工程字节级复制。
- residual 闸门：表达式侧白名单 skip 双态锁死复用 C03 夹具机制（带名 slice 用例显式 skip 配置下通过且计白名单，否则 PD010 失败）。

### 7.4 注册表与 CLI

- 无注册表测试改动（stub 数、链序均由 C03 PR 锁定）；仅在默认开动作发生（§8）时同步 `EnabledByDefault`。
- CLI：C03 已加的 `preprocess --pass pattern-desugar` 可达性测试上，追加表达式侧统计行出现、PD010/PD012 退出码与 verbose 行为断言。

### 7.5 全量验证

`dotnet build -c Release` 全绿；三个测试工程（CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests）`dotnet test -c Release` 全绿；**PatternMatchingTests 11/11（ConversionTests）与 Tests 工程 10/10 全绿**为硬门槛；csharp-sdk 基线项目转换不退化（默认关阶段先 opt-in 对比，默认开阶段跑全链）。

---

## 8. 默认开启验收门槛

**建议：默认开动作（`pattern-desugar` descriptor `defaultEnabled: false → true`）由 C37 合入时一次性完成，不建议 C03 先行单独默认开。** 理由：catalog §3.1 第 2 条与风险 15 均要求 C03/C37「同批交付、PatternMatchingTests 全绿为门槛」，语句侧先行默认开虽不产生回退（fail-fast 方向安全，C03 §8 保留该选项），但表达式侧恒真兜底仍是现役代码，两个语义面分开默认开会形成中间态事实标准。若 C03 基于自身门槛已提前默认开，C37 合入时**不得回退为关**，仅在本特性门槛满足后保持开启。

C37 翻转默认开必须同时满足：

1. §7.1-7.5 全绿；三工程 Release build/test 全绿；两组 PatternMatchingTests 全绿（11/11、10/10）。
2. 项目级 fixture .NET 半区：表达式侧运行时行为测试全绿（尤其 subject 单次求值、null 不落分支、非穷尽异常、守卫短路）。
3. residual 闸门：fixture 与 csharp-sdk 抽样输出树 switch 表达式计数 0、非白留 is 模式计数 0，或仅余带 PD010 编号的白名单 skip（带名 slice、扩展方法 Deconstruct）。
4. csharp-sdk 基线：默认开 vs 默认关两次 convert-project，成功项目集不缩小、Java 侧测试不新增红；至少有 is/switch 表达式命中样本由失败/恒真转为成功（收益证据）。
5. D20 兜底硬化 PR 合入（L115/L505 fail-fast），恒真文本在 src 全仓 grep 为 0。
6. JavaGenericPatternRewriter 四入口回归通过（D18 形态选择不制造 rewriter 不认识的新条件形态）。
7. 性能：单环 restore/build/test 串行成本可接受；无新增中间目录、无新增指纹段失效面。

Status 在 C37 全程保持 C03 已翻转的 Implemented——C37 不产生任何注册表演播事件，默认开是唯一可能的开关变更，且通过通用 `--enable-pass/--disable-pass` 与链序测试管理。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 | 基于合入 C03 的分支：reducer 注册 Positional/List（无命名 slice）；ExpressionPatternRewriter（is 归约 + 选择性 subject 临时 + 守卫包裹）；PatternTempLifter 块/成员两类锚点；§7.1 is 族 + 位置/列表单测 | Core ~600 行 |
| M2 | SwitchExpressionLowerer：三元链（C# 语法树层）、块提升与结果临时、表达式体展开、when 放开、非穷尽 throw、泛型臂选型（D18）；§7.1 switch 族与提升单测 | Core ~450 行 |
| M3 | PD010/PD012 诊断、表达式树 skip 区、§4.6 residual 扩计数；适配器统计行；§7.2 既有用例组合夹具化、11/11 + 10/10 转绿 | 测试 + 适配器小改 |
| M4 | SamplePatternSolution 表达式侧 fixture + 项目级测试、Release 全量验证、sdk 基线对比、默认开门槛证据（§8）；D20 转换器兜底硬化独立 PR | fixture + 测试 + 转换器 2 处 |
| M5 | 实现计划文档 `docs/superpowers/plans/2026-09-28-c37-pattern-expression-desugar.md`（writing-plans 展开）；PD010 白名单（带名 slice、扩展方法 Deconstruct）移交 C31/C36 的去向登记 | 文档 |

---

## 10. 风险

1. **测试红灯 + 实现/预期双向脱节（catalog §4 风险 15，主风险）**：恒真兜底让 7 个表达式侧用例长期红、让 NoCSharpResidue 空洞通过、让 PropertyPattern 断言追索不存在的 TODO 文本。缓解：D19/D21 先定死目标白留形态，组合夹具统一红绿，禁止改实现迁就过时断言；11/11 全绿收口。
2. **表达式树禁区（§4 风险 8）**：is 模式/switch 表达式在表达式树内当前语言即不可编译，真实命中近零，但 skip 判定缺失会在未来 C26/C45 落地时互相改写树形态。缓解：D16 判定必做、PD012 留痕、评审中确定把 `IsExpressionTreeLambda` 提升为共享 helper 的归属（建议随 C26 三路分流门一起），在此之前不复制第二份判定。
3. **三元/块提升选型与 JavaGenericPatternRewriter 的接缝（风险 15 分工项）**：rewriter 只访问四个语句条件入口，三元条件不在其覆盖面；泛型声明模式漏入三元会在 Java 侧丢泛型实参导致成员编译失败。缓解：D18 选型谓词 + 四入口回归测试 + 一条「泛型臂必走块提升」的单测锁死。
4. **Deconstruct 契约依赖 E06/E07**：位置模式依赖 E06 元组 struct 化与 E07 的 Deconstruct 检测口径；链位虽在二者之后，但扩展方法 Deconstruct 两 pass 都不支持。缓解：D10 同口径 PD010 显式报错，不静默生成不存在的方法名（对照风险 14「映射存在 ≠ 语义可用」）；fixture 覆盖实例 Deconstruct/record/元组三类已支持形态。
5. **单次求值与确定赋值**：subject 多引用（or/装箱/递归）、Deconstruct 短路、元素索引副作用——任何重复求值都是行为差异。缓解：D5/D9/D11 的临时策略 + 带副作用夹具的调用/取值计数断言；块提升结果临时由穷尽 if/else 结构保证确定赋值。
6. **非穷尽 switch 表达式的 throw 形态下游兼容性**：`SwitchExpressionException` 经主转换器映射 Java 类型的路径未经验证；若 Java 半区不可编译，默认开门槛 §8.4 会抓住。备选（评审项）：对非穷尽臂发 PD010 白名单姿态，待验证后放开——不允许发静默近似值。
7. **不可提升上下文（D17）**：字段初始化器中的位置/列表模式 switch 表达式一期只能报错，是已知能力缺口；须确认 sdk 画像命中率（§8.4 基线扫描登记），高频则在后续迭代引入初始化器→构造体提升（超出本特性范围）。
8. **实验性 Lowering 不可复用（§4 风险 9）**：[LowerPatternMatch.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerPatternMatch.cs) 仅 Java2 对比测试可达；只借「模式分类归约 + 变量两遍处理」形态，禁止引用其代码/类型（与 C03 §10.5 同声明）。
9. **装箱常量/关系模式的语义精度**：`object o is 1` 的 C# 语义含类型测试，简单 `(int)o == 1` 会对错误类型抛 InvalidCastException 而非返回 false。缓解：D6/D7 强制 `is T tmp && tmp == c` 形态，单测覆盖错型/null 两类假值。
10. **链上禁用 eliminate-goto / C03 未启用的组合**：C37 自身不产 goto（块提升是结构化 if/else），但语句侧 C03 产物依赖 goto 后序；文档与诊断中注明同 C03 的配套关系；本 pass 单独启用而 C03 组件同在一个 pass 内，不存在两侧版本错配，仅需记录与 goto pass 的拓扑关系。

---

## 11. 并行协调

### 11.1 与 C03（硬依赖）

- C03 分支**先合入**；本任务分支从 `67477551` 建立后第一件事是合入 C03 分支（§12），不在裸基线上开发。
- 接口冻结点：C03 M1 交付的 `IPatternReducer`、`IPatternDesugarComponent`、命名/绑定序号服务（C03 §4.4）即 C37 的契约；C37 评审时如发现形状不足，回到两份设计共同修订，不在 C03 合入后破接口。
- 热文件冲突解法：`PatternReducer.cs`（reducer 注册表）、`PatternDesugarPreprocessor.cs`（组件列表）、`PatternDesugarAnalyzer.cs`、`PatternDesugarStatistics.cs` 是两侧共同热点——C03 以「注册表 + 有序组件列表」把扩展做成**追加注册项**（无调用方分支修改），C37 的 diff 因此只含追加行；若 C03 评审改动这些形状，以合入后的形状回读本设计 §4 再实现。
- C03 D7 守卫 PD010 闸门与 C03 §7.2 的 Skip/夹具是给 C37 的移交清单，本设计 §6/§7 逐项接收并关闭。

### 11.2 与族内/邻接条目

- **E06 tuple-to-struct / E07 foreach-deconstruction（Deconstruct）**：RunAfter 锚点已在 C03 翻转时扩足，C37 不改链；位置模式消费 E06 的 ItemN 字段 struct 与 E07 的 Deconstruct 检测口径（D10），不直接引用其内部类型。E06 自身对 tuple var-pattern 有部分改写（[TupleToStructPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L1367-L1416) L1367-1416），链序保证其先于本 pass；fixture 须含一例两 pass 串联的元组位置模式验证无重复改写。
- **C35 null-conditional / E03 null-coalesce（临时提升范式）**：D9/§4.3 的两遍提升直接复用 E03 的 `PendingDeclaration` + StatementLifter 形态；C35 落地时同样复用该基建（catalog L757、L1087），三方不得各写一套锚点插入。差异：E03 对无锚点是 Warning+skip，本族按风险 15 升 Error（D17）。
- **C31 index-range**：列表模式索引统一发 `count - k` 不发 hat（D11），即不依赖 C31；带名 slice 的范围语义登记为 C31/C36 去向（PD010 白名单）。
- **C26/C45（表达式树）**：D16 的表达式树 skip 判定是风险 8 共享工具的又一消费者；不在本特性内新建公共 helper，评审与 C26 三路分流门统一归属。
- **C05 enum-declaration**：枚举常量模式走 D6 普通 `==`；C05 类化 enum 后常量比较口径由 enum 族自身保证，本 pass 不枚举特化。
- **JavaGenericPatternRewriter（转换器内）**：D18 形态选择是唯一接缝；转换器侧 rewriter 代码本特性不动，M4 仅做回归验证。

### 11.3 合并顺序建议

C03 合入 → C37 M1-M3（Core + 红测转绿）→ C37 M4（fixture/门槛/默认开）→ D20 转换器硬化 PR。默认开与兜底硬化不得先于全绿门槛。

---

## 12. Worktree 分配

- 分支：`feature/preproc-c37-pattern-expr`
- 目录：`.worktrees/preproc-c37-pattern-expr`
- base：`67477551`（实现前先合入 C03 分支 `feature/preproc-c03-pattern-desugar`）
- 创建命令：

```powershell
git worktree add .worktrees/preproc-c37-pattern-expr -b feature/preproc-c37-pattern-expr 67477551
```

创建后在该 worktree 内合入 C03 分支（`git merge feature/preproc-c03-pattern-desugar`，或 C03 合入主干后 rebase 到合入提交），再开始 M1；不创建第二个 worktree、不改动注册表与链顺序文件（默认开门槛触发的 `EnabledByDefault` 断言更新除外）。
