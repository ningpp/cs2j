# C23 插值字符串显式展开（`expression-desugar` 锚点）预处理器设计

**日期**: 2026-09-29
**范围**: 新建 `src/CSharpToJava.Core/ExpressionDesugar/` 表达式族共享基建（表达式→语句临时提升、单次求值、共享 Options/Statistics/Diagnostic/规则接口）与 `expression-desugar` pass 脚手架，落地首个规则 C23：在 C# 层把插值字符串 `$"..."`（含对齐 `,n`、格式 `:fmt`、嵌套插值、verbatim/raw 插值的插值部分）显式展开为 `string.Concat` / `string.Format` / 常量折叠；**必须**经 SemanticModel 判定插值绑定的 handler，自定义 `InterpolatedStringHandler` 默认显式报错。不改任何 C#→Java 转换代码（Java 侧大 transformer 的下掉为默认开启后的独立 PR）
**状态**: 设计已确认，待实施
**注册表**: stub `expression-desugar`（[PassStubCatalog.cs:239-248](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L239-L248)，catalogItems `"C23, C24, C35, C36"`，Stage=Expression，RunAfter=`null-coalesce`，中间目录 `.cs2j-expression-desugar-src`）。本特性是该 pass 的**锚点**：翻转 stub 并把 catalogItems 收窄为 `"C23"`；C24 初始化器 / C35 空条件 / C36 集合表达式后续在同一 pass 内扩展（预留规则目录 `Interpolations/`、`Initializers/`、`NullConditional/`、`CollectionExpr/` 与码段）
**base**: `953293b9`（`docs: update doc base commit`，已校验存在）

---

## 1. 背景与目标

### 1.1 现状：一个 Java 侧的大 transformer 与多处静默语义缺口

插值字符串今天在 **C#→Java 主转换器内部**被直接翻译成 Java 表达式，核心实现集中在 [StringExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs)（catalog 记录 336 行，2026-09-29 实测 **381** 行），并由 [StringConcatRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/StringConcatRewriter.cs)（接线点 [ProjectPasses.cs:682](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L682)）做后置改写。2026-09-29 复核，证据如下：

| # | 站点 | 现状行为 | 后果 |
|---|---|---|---|
| S1 | 全 `src`/`tests` grep `InterpolatedStringHandler\|DefaultInterpolatedStringHandler` | **零命中**（仅 catalog 文档提及）；[StringExpressionTransformer.cs:31-134](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L31-L134) 纯语法分派，不查 SemanticModel 绑定 | 自定义 handler（日志/JSON 高频）被无脑降为 Java 拼接/`String.format`，**静默改变可观察行为**（catalog §4 风险 18 点名，见 §1.2） |
| S2 | [StringExpressionTransformer.cs:52-54](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L52-L54)/[:145-147](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L145-L147) | 只读 `FormatClause`，**全文件无 `AlignmentClause` 引用** | `$"{x,5}"` / `$"{x,-5:X}"` 的对齐宽度被**静默丢弃**，输出不做空格填充；全仓无任何对齐测试（grep 无命中） |
| S3 | [StringExpressionTransformer.cs:367-368](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L367-L368) | 不认识的格式说明符发 `%s /* TODO: format specifier … */` | catalog §4 风险 12 实名点名的静默错误输出站点：占位注释直接流入 Java 产物 |
| S4 | [StringExpressionTransformer.cs:224-258](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L224-L258) + [:342-370](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L342-L370) | 直接产 Java `String.format`，并把 C# composite 风格手工转成 printf `%` 风格（D/F/E/X/N/G/P 有损表，无对齐、无 M/R 等） | C# 侧 composite `{0:X}` 与 Java printf `%x` 两套小语言被混写在 Java 侧；[StringFormatDoubleTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StringFormatDoubleTests.cs) 仅剩一个空 helper（无任何 `[Fact]`），回归护栏名存实亡 |
| S5 | 文化语义 | Java `String.format` 走 `Locale.getDefault()`；bool/Double 差异靠 compat `csFormat` 事后打补丁（[StringHelper.java:1099-1146](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1099-L1146)）；`formatCs` 不支持对齐（[正则 :1263](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1263) 不含 `,alignment`）、`%s` null 印 `"null"` 而非 C# 的 `""` | 文化/null/对齐三处运行时差异分散在兼容层与转换器两侧，无法在 .NET 侧验证（最高风险，§2.3、§10 R1） |
| S6 | [StringExpressionTransformer.cs:213-218](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L213-L218)/[:268-293](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L268-L293) | 4 段阈值切 `StringBuilder`、无符号洞手工掩码（`& 0xFFFFFFFFL`/`Long.toUnsignedString`）、二元/三元表达式括号特判全部耦在 Java 表达式生成里 | 无法在 C# 中间树做 build/test 等价性验证，回归只能靠 Java 侧文本断言 |
| S7 | null 洞 | concat 路径最终经 [StringHelper.concat](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L7-L17)（[null 跳过 :1314-1317](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1314-L1317)）侥幸正确；format 路径 `%s` 对 null 印 `"null"` | `$"{(string)null}:{1:F2}"` 这类混合形态在 Java 半区与 C# 不一致 |
| S8 | raw/verbatim 文本规则 | raw 判定 [:49-50](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L49-L50)、`$@`/`@$` 折行/双引号规则 [:289-339](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L289-L339)；非插值 raw 字面量在 [LiteralExpressionTransformer.cs:233-255](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L233-L255) | 词法关注点（C43）与表达式关注点（C23）在 Java 侧耦合；本特性必须只消费已 parse 的 `InterpolatedStringExpression` 并划清边界（§11） |

### 1.2 handler 绑定为什么有正确性含义（catalog §4 风险 18）

C# 10 起插值字符串的降级目标由**使用点绑定**决定，不是固定的字符串拼接：

- **默认路径**：插值转换类型为 `string`，编译器构造 `System.Runtime.CompilerServices.DefaultInterpolatedStringHandler`，逐洞 `AppendFormatted` / `AppendLiteral`，最终 `ToStringAndClear()`；每个洞按文本顺序**求值一次**，null 洞追加 `""`，格式化使用 `CultureInfo.CurrentCulture`。
- **自定义 handler 路径**：使用点形参/返回类型是带 `[InterpolatedStringHandler]` 的类型（典型：日志 `LoggerMessage` 族 handler、`System.Text.Json`/Utf8Json writer handler、断言 handler）。编译器生成的形态是「构造 handler（构造参数经 `[InterpolatedStringHandlerArgument]` **先于插值求值**）→ 由构造函数 `out bool append` 条件守卫 → 守卫为 false 时**整段插值零求值** → 为 true 时逐洞追加 → 交给 API 消费」。差异至少有三处：
  1. **参数求值次数**：默认每洞恰好一次；自定义 handler 可以零次（日志级别关闭时副作用不发生），handler 自身也可多次读取被传入的参数；
  2. **条件构造**：`out bool` 守卫与 `InterpolatedStringHandlerArgument` 提前求值，改写了副作用顺序；
  3. **结果可能根本不是字符串**：handler 常以 `ref struct` 形式直接被 API 消费（写 JSON/写日志/写 buffer），没有等价的 `string` 值可替换。

catalog 四次补全实测：训练集 sdk 插值约 **650 处**，其中相当比例绑定自定义 handler（json/日志场景）。把它们统一降成 `string.Concat/Format` 会**强行求值参数并伪造一个 string 结果**——能编译、单测难抓，属风险 18 专属的正确性项。因此 handler 判定是本 pass 的前置门，不是优化项。

### 1.3 目标形态

预处理后的中间树满足：

1. 默认 handler 绑定的插值**零** `InterpolatedStringExpressionSyntax` 残留（除 §6 列明的显式豁免区，每处豁免都有诊断），产物是普通、可编译、可直接在 .NET 上跑测试的 C#：

   ```csharp
   var s = $"val={x}";                 // → var s = string.Concat("val=", x);
   var f = $"{x:F2}";                  // → var f = string.Format("{0:F2}", x);
   var a = $"[{x,5}][{y,-8:X}]";       // → var a = string.Format("[{0,5}][{1,-8:X}]", x, y);
   const string k = $"a{"b"}";         // → const string k = "ab";   （常量折叠）
   var n = $"{(string)null}";          // → var n = string.Concat((string)null);  // 结果 ""
   ```

2. 展开选择严格遵循 C# 语义：无格式/对齐走 `string.Concat`（null→`""`、实参按序各求值一次）；有格式/对齐走 `string.Format`（composite `{n[,alignment][:format]}`，`CurrentCulture`，与 `DefaultInterpolatedStringHandler` 同文化同 null 同填充）；**禁止** `FormattableString.Invariant` / `CultureInfo.InvariantCulture` 展开（会改变可观察文本）。
3. handler 绑定非默认 string 路径时，默认 **Error 显式失败**（非 strict 可降级为 Warning + 原样保留），消灭 S1/S2/S3 族静默输出。
4. `ExpressionDesugar/` 作为表达式族（catalog §3.3：C23+C24+C35+C36，含 C31 切片使用点）的**共享基建锚点**：单次求值 `TempLifter`、族级 Options/Statistics/Diagnostic/规则接口一次落地，C24/C35/C36 只加规则目录，不再复制骨架。
5. pass 默认**关**（opt-in 落地），满足 §8 门槛后单独翻默认开；Java 侧 [StringExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs) 本期**不删不改**，作为直连单文件路径的安全网。

### 1.4 非目标

- 不做 raw string / `u8` / verbatim **词法**归一（C43 域）；本特性只消费 Roslyn 已 parse 的语法节点。
- 不模拟任何具体自定义 handler（日志/JSON handler 的条件构造仿真不进 M1–M3，见 §9 M4 与开放问题 Q1）。
- 不改 Java 侧转换器、compat 库、`TypeMappings.json`（Java 半区缺口仅登记与联调，修复排期见 §8/§11；本 pass 产物在 C# 半区即语义完整）。
- 不处理 `nameof(...)`（其值是常量字符串，不产生插值节点）与显式 `new DefaultInterpolatedStringHandler(...)` 源码构造（非插值语法，无 `InterpolatedStringExpression`）。

## 2. 关键设计决策表

### 2.1 展开五路总览

HandlerBindingAnalyzer（§4.3）对每个 `InterpolatedStringExpressionSyntax` 给出绑定类别，规则按下表五路处置：

| 路径 | 触发条件 | 产物 | 诊断 |
|---|---|---|---|
| IE-FOLD 常量折叠 | `SemanticModel.GetConstantValue(node).HasValue`（全常量插值，含 const 符号传播） | 单个普通字符串字面量（`SyntaxFactory.Literal(value)`） | CS2J0503（Info，计数） |
| IE-CONCAT | 转换类型为 `string` 默认绑定；无任何对齐/格式子句；且所有洞类型在 Concat 等价白名单（§2.2 白名单行） | `string.Concat(seg0, seg1, …)`（空文本段剔除） | CS2J0501（Info） |
| IE-FORMAT | 默认绑定；存在对齐或格式子句，**或**存在非白名单 `IFormattable` 洞 | `string.Format(composite, h0, h1, …)`，composite 由文本段 ValueText 转义 + `{i[,align][:fmt]}` 合成 | CS2J0502（Info） |
| IE-KEEP | 转换类型为 `System.FormattableString` / `System.IFormattable`（显式运行时形态）；或非 strict 下遇自定义 handler / 表达式树豁免 | 节点原样保留，计入「已解释保留」而非未解释 residual | CS2J0511 / CS2J0510-Warn / CS2J0515（Info） |
| IE-ERROR | 自定义 handler（strict，默认）；ref struct/指针洞无法物化；对齐非常量等基线非法形态；绑定不可判定 | 不改写，整个 pass 失败 | CS2J0510 / CS2J0513 / CS2J0514 / CS2J0512（Error） |

### 2.2 决策表

| 决策点 | 结论 | 备注/证据 |
|---|---|---|
| 总体架构 | 语义驱动**逐文件** pass：dirty 检查 → 族级 Analyzer 建文件上下文 → 规则管线（C23 仅插值一条规则）自底向上改写并收集 `PendingTemp` → 族级 `TempLifter` 统一插语句 → 格式化 → residual 闸门；复刻 [NullCoalesceSyntaxRewriter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L13-L42) 两遍范式与 [IteratorLoweringPreprocessor](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringPreprocessor.cs#L12-L95) 的入口外形 | 无改名/签名变化、无跨文件生成、无支撑库；标准 spine 即可，不覆写 `ExecuteAsync` |
| 判定真相源 | 一律 SemanticModel/IOperation：`GetConstantValue`、`GetTypeInfo().ConvertedType`、`GetOperation(node)` 及其父链上的 `IInterpolatedStringHandlerCreationOperation`、handler 类型上的 `[InterpolatedStringHandler]`、构造参数上的 `[InterpolatedStringHandlerArgument]` | 禁止按 `$` 文本/参数名猜 handler；S1 根因即不看绑定。Roslyn 4.12（[Core.csproj:12](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12)）IOperation 外形以 M0 最小探针核实为准（§9），不凭记忆写规则 |
| **Concat vs Format 选择** | 无格式且无对齐且洞类型全在白名单 → `Concat`；否则 → `Format`。「否则」包括：任一对齐子句、任一格式子句、任一静态类型实现 `IFormattable` 且不在等价白名单 | 两种目标都是 BCL 原语，Java 侧已有路由：`Concat→StringHelper.concat`（[StringConcatRewriter.cs:31-45](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/StringConcatRewriter.cs#L31-L45) + [TypeMappings.json:6319-6323](file:///d:/code/cs2j/config/TypeMappings.json#L6319-L6323)），`Format→format`（[TypeMappings.json:6324-6328](file:///d:/code/cs2j/config/TypeMappings.json#L6324-L6328)，Java 半区缺口见 §10 R1） |
| **文化语义** | 展开**只用当前文化**：`string.Format(fmt, args)` 等价 `Format(CultureInfo.CurrentCulture, …)`，与默认 handler 的 `AppendFormatted` 文化一致；**绝不**发 `CultureInfo.InvariantCulture` / `FormattableString.Invariant`——`$"{d}"` 在 de-DE 下必须仍产出 de-DE 文本，换 invariant 是可观察行为改变。`FormattableString.Invariant($"...")` 是源码显式选择，其内部插值的 ConvertedType 为 `FormattableString`，走 IE-KEEP | C# 侧语义由中间树 xUnit 直接钉死；Java 侧 `Locale.getDefault()` 与 .NET `CurrentCulture` 不等价（R1），由双半区预言机与 compat 收敛，不在 C# pass 内偷换文化 |
| IFormattable 洞 | 默认 handler 对 `IFormattable` 洞调 `ToString(null, CurrentCulture)`，而 `Concat` 调无参 `ToString()`。白名单（可证明两者等价：基元、enum、`string`、`DateTime(Offset)`/`DateOnly/TimeOnly`/`TimeSpan`/`Guid`/`char` 等 BCL 类型，无参 `ToString()` 即「G + 当前文化」）内走 Concat；**用户自定义/未知 IFormattable 实现整串升级 Format 路径**（合成仅含 `{i}` 的 composite），保证文化分派不丢 | 宁可整串走 Format，不承担显式接口实现下两路径分叉的风险；白名单以 SemanticModel 符号判定，不认类型名文本 |
| **对齐空格填充** | `,n` 直接进 composite：`{i,n}`（n>0 左填充/右对齐，n<0 右填充/左对齐，填充符空格），与 `string.Format` 填充规则完全一致；对齐+格式合成为 `{i,n:fmt}`。对齐表达式 C# 要求编译期常量，取 `GetConstantValue` 的整数值；非常量属基线编译错误，防御性报 CS2J0514 | 消灭 S2：现状对齐被整体丢弃。不得自行造空格循环（重复求值/宽度计算都偏离 BCL） |
| 格式子句透传 | 洞格式串 `:fmt` **原样**进 composite（`{i:fmt}`），不做 C#→printf 翻译：composite 小语言与插值洞格式本就是同一套（D/F/E/X/N/G/P/M/R…），翻译是 Java 半区的事（现状 S4 的有损表是 Java 侧债） | S3 的 TODO 注释站点在新路径不被复刻；无法识别的说明符在 C# 侧仍合法，交 `string.Format` 运行时 |
| composite 文本转义 | Format 路径：文本段取 `InterpolatedStringTextSyntax.TextToken.ValueText`（已按 `$` 数量完成括号转义解析，raw 缩进亦已剥离），再把字面 `{`/`}` 翻倍为 `{{`/`}}`（composite 与插值共用括号转义）；Concat 路径：ValueText 直接进普通字面量，不翻倍 | 两种路径都用 `SyntaxFactory.Literal(valueText)` 重新发常规 C# 字符串字面量，引号/换行/反斜杠转义由工厂保证，不手写 |
| **副作用顺序与求值次数** | 默认 handler 每洞按文本顺序求值一次；`Concat`/`Format` 的实参求值即文本顺序、每个实参出现一次——规则保证洞表达式在产物中**零复制**，因此 C23 主路径不产生临时变量。`TempLifter` 的 C23 内真实消费者仅：① 非 strict 自定义 handler 远期白名单仿真（条件构造/out append，M4+）；② 锚点职责——为 C24/C35/C36 冻结并交付单次求值契约（§4.5） | 自定义 handler 可零次/多次求值且构造参数提前求值，是其必须 Error 而非展开的语义根据（§1.2） |
| **null 插值 → `""`** | C# `Concat(params object?[])` 与 `Format("{0}", null)` 对 null 实参均产空串；产物天然保持，**不**自插 `?? ""`（避免改变实参类型与求值结构）。nullable 值类型洞（`int?` 为 null）同样经装箱实参得到 `""` | Java 半区：concat 已正确（[StringHelper.java:1314-1317](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1314-L1317)）；format 路径 `%s` 印 `"null"` 是 S7 缺口，列入默认开门禁（§8 第 3 条） |
| **值类型装箱** | `Concat(object?[])`/`Format(object?[])` 对值类型洞隐式装箱，文本结果与 handler `AppendFormatted<T>` 一致（handler 对 IFormattable 同样经装箱接口），可观察行为无差；分配密度差异不在等价性范围 | 产物是普通 C#，装箱由编译器自然生成，不写显式 cast |
| 无符号整型洞（byte/ushort/uint/ulong） | 无格式路径不把无符号值直接交 `Concat(object)`（Java 半区擦除后会印负数）；对 `uint`/`ulong` 静态类型洞发 `x.ToString()` 实例调用——后位 unsigned pass 已拦截 `uint/ulong.ToString()` 改发 UnsignedMath 助手（[UnsignedTypeSyntaxRewriter.cs:1275-1301](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L1275-L1301)）；`byte`/`ushort` 洞发 `((uint)x).ToString()` 归入同一拦截；Format 路径保持实参原样（C# 侧 `Format` 本就印无符号），Java 半区由兼容层格式化路径负责 | 与 E09 的跨 pass 契约：expression-desugar 在 unsigned **之前**（链位 §3），只发 BCL/`ToString()` 形态，不直接引用 UnsignedMath 支撑库；四象限联调（§7.2） |
| ref struct / 指针洞 | ref struct 不能装箱进 `object`。`ReadOnlySpan<char>`/`Span<T>` 等可证具有非虚 `ToString()` 覆盖的洞 → 显式 `x.ToString()`（C# 侧与 handler 文本一致）；无法证明安全覆盖的 ref struct 洞、指针洞 → CS2J0513 Error（M1 不收编） | `DefaultInterpolatedStringHandler.AppendFormatted<T>` 经泛型实例化允许 ref struct T，目标两路径都不允许，属真实表达力边界 |
| 嵌套插值 | 洞中的 `$"..."` 自底向上先展开（内层为 Concat/Format 调用，类型 string，作为外层实参合法）；不做 Format 展平合并（嵌套 `string.Format` 调用保持嵌套），文本与求值顺序等价 | 深嵌套各洞仍只求值一次；Core 单测覆盖三层 |
| 空/纯文本插值 | `$""`、`$"abc"` 经 IE-FOLD 成 `""`/`"abc"` 字面量；连续洞、首尾文本按空段剔除规则拼段（`$"{a}{b}"`→`Concat(a, b)`） | 单洞 `$"{x}"` 白名单类型 → `Concat(x)`（单参 params 重载合法，null string→`""`） |
| **常量折叠** | 仅对 `GetConstantValue().HasValue` 的整串折叠为字面量；这是 `const string` 声明、常量默认参数等常量上下文的**唯一合法**展开（`Concat/Format` 非常量表达式，发进去会破坏基线可编译性）；含非常量洞不折叠，即便洞是简单名 | 折叠值以编译器常量值为准，不自算格式/对齐 |
| **自定义 handler 白名单/报错** | 默认 strict=Error：任何非 string 转换的插值（自定义 handler、ref handler 形参位）报 CS2J0510，消息带 handler 类型名与构造参数列表；非 strict（`HandlerPolicy=Keep`）降级 Warning 并原样保留节点（IE-KEEP）。M1–M3 **不做任何具体 handler 白名单仿真**；M4 依 sdk 画像评审 | 禁止静默错译是第一原则（§6）；Keep 是显式、有诊断、有计数的逃生门，不是静默透传 |
| `FormattableString` / `IFormattable` 绑定 | 转换类型为 `System.FormattableString`/`System.IFormattableString`（如 `FormattableString f = $"...";`、`FormattableString.Invariant($"...")` 实参）→ IE-KEEP + CS2J0511 Warning：这是用户显式选择的运行时对象（参数数组、composite、`Invariant`/`CurrentCulture` 成员都依赖该形态），展开即破坏 | residual 统计单列「kept-formattable」，不计未解释残留；Java 侧维持现状处理 |
| verbatim 插值 `$@""`/`@$""` | 与普通插值同规则；文本取 ValueText（折行、`""` 转义已由 Roslyn 解码），重发为常规字面量；不再在产物中保留 `@` | 消灭 S8 中 Java 侧的 `""`→引号/折行规则复刻 |
| raw 插值 `$"""..."""` / `$$"""..."""` | 本特性只消费已 parse 的 `InterpolatedStringExpressionSyntax`：多 `$` 决定洞括号层数（Roslyn 已解析出洞），文本 ValueText 已去缩进/去括号转义；产物一律常规字面量/Concat/Format。raw 定界符本身、非插值 raw 字面量归 C43，本 pass 不触碰 token 形态（M0 探针核实 1/2/3 个 `$` 的 ValueText 行为） | 与 C43 划界的关键：即便 C43 尚未实施，本 pass 也不依赖其产物——Roslyn 直接给出可用节点 |
| 表达式树 / 查询内插值 | `Expression<Func<...>>` lambda 内的插值：M1–M3 **跳过不改写** + CS2J0515（Info），走共享表达式树祖先判定（catalog §4 风险 8）；普通 IEnumerable LINQ（含查询理解式内的插值）正常展开。M4 评估是否降为编译器规范形（C# 编译器在表达式树中本就把插值降为 `string.Format` 调用，见 Q4） | linq-channel（C26）在后；IQueryable 三路分流归 C26，本 pass 不替它决策 |
| `nameof` / 普通字符串 | 不受影响：`nameof(x)` 与含 `{`/`%` 的普通字面量不产生插值节点，规则不触达 | 防误伤单测 |
| 幂等 | 产物零插值节点（豁免区除外），二次运行零 diff、Changed=false | 单测与项目级双断言 |

### 2.3 文化语义专论（设计侧论证，详见 §10 R1）

三层事实必须分开：

1. **C# 侧（本 pass 的正确性边界）**：默认插值与 `string.Format(CultureInfo.CurrentCulture, …)` 同族——当前文化、`IFormattable` 分派、对齐空格、null→`""` 完全一致；与 `string.Concat` 的唯一已知分叉是非白名单 `IFormattable` 的无参 `ToString()`，已由「非白名单即升 Format」堵死。因此 IE-CONCAT/IE-FORMAT/IE-FOLD 在 .NET 半区可证等价，且明确**不引入 invariant**。
2. **Java 侧（下游半区）**：`String.format` 用 `Locale.getDefault()`，与 .NET `CurrentCulture` 不逐文化对应；现有 [StringHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java) 用 `Locale.ROOT` 的 `csFormat`/`formatCs` 实际是 invariant 近似（为 JSON 场景故意为之，[:1131-1137](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1131-L1137)），且 `formatCs` 不吃对齐、不把 null 归一空串。
3. **结论**：本 pass 不在 C# 层替 Java 半区做文化妥协（发 invariant 会让 .NET 半区先错）；文化风险以「.NET 半区锁当前文化等价 + Java 半区固定 Locale 运行时预言机（§7.3）+ 默认开门禁前 compat 格式化能力盘点（§8 第 3 条）」三段式收敛。

### 2.4 目标形态示例

```csharp
// 1) 纯 Concat
$"a={a},b={b}"                         → string.Concat("a=", a, ",b=", b)
$"{a}{b}"                              → string.Concat(a, b)
$"x={(string)null}"                    → string.Concat("x=", (string)null)   // "x"

// 2) Format：格式 / 对齐 / 非白名单 IFormattable 任一命中即整串 Format
$"{d:F2}"                              → string.Format("{0:F2}", d)
$"[{i,5}]"                             → string.Format("[{0,5}]", i)
$"{x,-8:X}{customFormattable}"         → string.Format("{0,-8:X}{1}", x, customFormattable)
$"{{not a hole}}: {x}"                 → string.Format("{{not a hole}}: {0}", x)

// 3) 折叠
$""                                    → ""
const string q = $"id={constId}";      → const string q = "id=42"; // 值以编译器常量为准

// 4) 无符号（无格式路径）
$"{u}"   (uint u)                      → string.Concat(u.ToString())      // E09 后接 UnsignedMath.ToString*
$"{s}"   (ushort s)                    → string.Concat(((uint)s).ToString())

// 5) 报错 / 保留
_log.LogInformation($"user {userId}"); // 绑自定义 LoggerMessage handler → CS2J0510 Error（默认）
FormattableString f = $"{x}";          // IE-KEEP + CS2J0511，节点保留
Expression<Func<object>> e = () => $"{x}"; // CS2J0515 跳过（表达式树）
```

## 3. 注册表与链位契约

### 3.1 描述符翻转

实施完成后，把 [PassStubCatalog.cs:239-248](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L239-L248) 的 `expression-desugar` stub 删除，在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) `Implemented` 表登记（翻转范式同 iterator-lowering：[PassCatalog.cs:195-205](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L195-L205)）：

| 字段 | 值 |
|---|---|
| Id | `expression-desugar`（沿用 `PassStubCatalog.Ids.ExpressionDesugar` 常量，不迁入 `PassIds`——与 iterator-lowering 落地做法一致） |
| IntermediateDirectoryName | `.cs2j-expression-desugar-src`（与 stub 一致，零指纹迁移成本） |
| Stage | `Expression` |
| Status | `Implemented` |
| DefaultEnabled | **false**（落地即 opt-in；满足 §8 后单独 PR 翻 true） |
| RunAfterIds | `[ "null-coalesce" ]`（**维持 stub 锚点**，stub 期拓扑即最终拓扑，不借翻转重排） |
| SupportLibrary | `None`（仅发射 BCL `string.Concat/Format`；无支撑工程注入/过滤） |
| NeedsSemanticModel | `true`（handler 绑定强依赖整编译语义） |
| VerifyBuildByDefault / VerifyTestsByDefault | `true` / `true`（表达式语义改写，默认开中间树双验） |
| CatalogItems | `"C23"`（由 `"C23, C24, C35, C36"` **收窄**，见 §3.4） |
| Description | 注明「C23 interpolation expansion; C24/C35/C36 rule families pending in shared scaffolding」 |

### 3.2 拓扑位置与物理插入点

描述符**物理插入** `Implemented` 表 `null-coalesce` 条目（[PassCatalog.cs:56-66](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L56-L66)）之后、`enum` 条目之前。理由：`expression-desugar` 与 `enum` 都只声明 RunAfter `null-coalesce`，拓扑平局按注册表声明顺序裁决（[PassRegistry.cs:100-117](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L100-L117)，「ties keep declaration order」），插入位置即执行位置，无需改任何既有描述符。翻转后实现性子序列为：

```
… null-coalesce → expression-desugar(默认关) → enum → delegate → …
```

stub（`pattern-desugar` 等同锚 stub）仍按注册表机制 interleaving，但不过执行计划；`java-keywords` 末尾位置不受影响。

### 3.3 锚点翻转清单（实施 PR 的完整改动面，含三个测试文件断言）

1. 新增 `src/CSharpToJava.Core/ExpressionDesugar/` 全部组件（§4），含四个规则目录（本期仅 `Interpolations/` 有实现，其余三个仅占位目录 + 目录级注释说明归属，零代码）。
2. 新增 [Adapters/ExpressionDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ExpressionDesugarPassAdapter.cs)（§5）。
3. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删除 `ExpressionDesugar` stub 条目（L239-248）；`Ids.ExpressionDesugar` 常量**保留**。
4. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：按 §3.2 物理插入 Implemented 描述符。
5. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)：`CreateAdapter` switch 增 `expression-desugar` 分支（仿 [:213-220](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L213-L220)）；`BuildLegacyToggles` 按 iterator-lowering 先例（[:77](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L77)）增 `[PassStubCatalog.Ids.ExpressionDesugar] = false`（无 legacy `--no-xxx` 开关，通用 `--enable-pass`/`--pass` 唯一入口）。
6. **三个既有测试文件的断言必须同步更新**：
   - [PassStubCatalogTests.cs:24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24)：`Assert.Equal(23, ids.Length)` → **22**；补一条「`expression-desugar` 不在 stub 集合」断言（仿同文件既有 `Assert.Contains` 风格用 `Assert.DoesNotContain`）。
   - [PassChainOrderTests.cs:18-38](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L38)：`LockedChain` 在 `PassIds.NullCoalesce` 与 `PassIds.Enum` 之间插入 `PassStubCatalog.Ids.ExpressionDesugar`；`EnabledByDefault`（[:40-55](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L40-L55)）**不变**（本 pass 默认关）；`BuildLegacyToggles_covers_every_registered_pass`（[:170-182](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L170-L182)）因第 5 条已加 toggle 而无需开例外。
   - [ProjectPreprocessorsConsistentTests.cs:129-132](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs#L129-L132)：仿 generic-deepen 先例补 `Assert.Equal(".cs2j-expression-desugar-src", PassCatalog.ImplementedById(PassStubCatalog.Ids.ExpressionDesugar).IntermediateDirectoryName)`。
7. [PassListFormatterTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassListFormatterTests.cs)：若存在按状态计数/快照的断言，同步翻为 Implemented/默认关（实施时按实际断言面改）。
8. 转换器侧 S1–S8 分支本期**不删不改**（直连单文件入口仍需要安全网），下掉排期见 §8 第 6 条。
9. 通用 CLI 面天然可达：`--enable-pass expression-desugar`、`preprocess --pass expression-desugar -s src -d dst`、`--list-passes` 状态翻 Implemented（不新增独立 verb、不新增单文件模式——handler 判定强依赖整编译 SemanticModel，理由同 C31）。

### 3.4 catalogItems 收窄与 C24/C35/C36 再接策略

- 翻转即收窄：`"C23"`。注册表是活清单，C24/C35/C36 落地时**在同一描述符上把 catalogItems 依次加宽**（`"C23, C24"` → …），不新增 pass id、不拆链、不新增中间目录——这是「一个 expression-desugar pass + 多规则族」锚点设计的核心约定。
- 收窄期间 C24/C35/C36 的可见性不丢：描述符 Description 明示 pending 三族；本文件 §4 预留目录/码段；同日各设计文档（撰写时可能尚不存在，本文按锚点假设编写，见 §11）引用同一契约。
- 若某族落地时评审决定独立成 pass（当前无此计划），须另行注册新 stub 并改 §3 拓扑，不走隐式分叉。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/ExpressionDesugar/`。公开外形与 [NullCoalesceRewriter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceRewriter.cs) / [IteratorLowering](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringPreprocessor.cs) 同构：runner 标准 spine 逐文件驱动。

```
src/CSharpToJava.Core/ExpressionDesugar/
  ExpressionDesugarPreprocessor.cs   # 族入口：Preprocess(sourceCode, SemanticModel, Options) → ExpressionDesugarFileResult
                                     #   dirty 检查（任一启用规则有触发语法）→ 建文件上下文 → 规则管线 → TempLifter.Apply → 格式化 → 族 residual 闸门
  ExpressionDesugarOptions.cs        # 族选项（Strict 默认 true + 各规则子选项，组合而非继承，避免热文件争用）
  ExpressionDesugarStatistics.cs     # partial：通用五字段 + 族级 TempsLifted；各族在自己目录加 partial 计数器
  ExpressionDesugarDiagnostic.cs     # ExpressionDesugarSeverity / ExpressionDesugarDiagnostic record / 码段总表（CS2J05xx，§6）
  ExpressionDesugarFileResult.cs     # { OutputCode, Changed, Diagnostics, Statistics }
  ExpressionDesugarRuleSet.cs        # 规则清单与族内顺序（唯一清单文件，热冲突点，§11 给解法）
  Shared/
    ExpressionDesugarAnalyzer.cs     # 每文件一次的语义上下文：SemanticModel 包装、共享判定（表达式树祖先、常量上下文、
                                     #   语句锚点、可提升位置）、诊断收集器；规则只读消费
    TempLifter.cs                    # 【族基建】单次求值临时变量提升：PendingTemp + Collect + Apply（§4.5）
    ExpressionSyntaxFacts.cs         # 语法事实：内联白名单、表达式树判定、锚点搜索、临时名 __ed_* 与碰撞避让
  Interpolations/                    # === C23 本期所有权 ===
    HandlerBindingAnalyzer.cs        # SemanticModel/IOperation 绑定五分类（§4.3）
    InterpolationModels.cs           # HandlerKind / InterpolationPlan / HoleInfo / Segment 记录
    InterpolationRule.cs             # IExpressionDesugarRule 的插值实现（Analyze + Rewrite）
    ConcatEmitter.cs                 # IE-CONCAT：分段、空文本剔除、无符号/特殊洞包装、string.Concat 合成
    FormatEmitter.cs                 # IE-FORMAT：composite 合成（{i,align:fmt}、括号翻倍）、string.Format 合成
    InterpolationSyntaxFacts.cs      # 洞类型白名单、ref struct/指针判定、IFormattable 等价集、verbatim/raw token 分类
    InterpolationStatistics.cs       # partial 计数（§6）
  Initializers/                      # === C24 预留（占位目录，仅目录注释，零代码）===
  NullConditional/                   # === C35 预留（与 TempLifter 最密切，§11）===
  CollectionExpr/                    # === C36 预留（spread/目标类型构造）===
```

### 4.1 ExpressionDesugarPreprocessor（族入口）

- 签名与返回外形仿 [IteratorLoweringPreprocessor.Preprocess](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringPreprocessor.cs#L14-L95)：SemanticModel 为 null 或文件不含任一启用规则的触发语法 → clean 字节复制（`FilesSkippedClean=1`）。
- 流程：建 `ExpressionDesugarFileContext`（root、model、options、共享 diagnostics/stats、单个 `TempLifter` 实例）→ 按 `ExpressionDesugarRuleSet` 顺序跑规则：每规则先 `Analyze`（只产 plan + 诊断，可声明不改写）再 `Rewrite`（自底向上 `CSharpSyntaxRewriter`，表达式替换 + 向共享 `TempLifter` 登记 `PendingTemp`）→ 全部规则完成后 `TempLifter.Apply(root)` **一次**（保证 C23 与未来 C35 同文件协作时临时声明合并、顺序确定）→ `AdhocWorkspace` 格式化 → residual 扫描。
- 规则失败语义：任一规则产 Error 诊断时，文件结果 `Changed=false`（整文件保持原样，诊断上抛使运行失败），绝不返回半成品树（与 iterator 「dirty-no-rewrite」同形）。

### 4.2 共享组件

- **ExpressionDesugarOptions**（组合式，避免后续各族改构造函数）：
  `ExpressionDesugarOptions(bool Strict = true, InterpolationOptions? Interpolation = null, …/* 后续规则族追加属性 */)`；
  `InterpolationOptions(HandlerUnsupportedPolicy HandlerPolicy = Error, bool KeepFormattableString = true, bool RewriteInExpressionTrees = false, bool FoldConstants = true)`；
  `HandlerUnsupportedPolicy { Error, Keep }`。
- **ExpressionDesugarStatistics**：通用五字段（`FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount`）+ `TempsLifted`；标 `partial`，C23 计数见 §6，C24/C35/C36 各在本族目录加 `partial` 文件，不改共享文件。
- **ExpressionDesugarDiagnostic**：`record ExpressionDesugarDiagnostic(ExpressionDesugarSeverity Severity, string Code, string Message, string? FilePath = null, int Line = 0)`（仿 [IteratorLoweringDiagnostic](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringDiagnostic.cs)）；码段总表在共享文件注释中登记（§6），各族常量在本族目录定义。
- **ExpressionDesugarAnalyzer（共享）**：包装 SemanticModel，提供：
  1. 表达式树祖先判定（`IsInExpressionTree(node)`，与 C02/C19 共享口径对齐，见 §11；M1 内置最小实现并注释合并去向）；
  2. 常量上下文判定（`const` 声明初始值、默认参数、特性实参、`switch` 常量 case）；
  3. 语句锚点搜索与「不可提升位置」判定（字段/属性初始化器、特性、表达式树、查询子句）；
  4. 统一诊断收集（文件内行号由 SemanticModel 位置填充）。
- **规则接口**（最小契约，冻结后只增不改）：

  ```csharp
  internal interface IExpressionDesugarRule
  {
      ExpressionDesugarRuleId Id { get; }
      SyntaxKind? TriggerSyntax { get; }                 // dirty 检查用；null 表示恒参与
      void Analyze(ExpressionDesugarFileContext context, ExpressionDesugarRulePlan plan);
      CSharpSyntaxRewriter CreateRewriter(ExpressionDesugarFileContext context, ExpressionDesugarRulePlan plan);
  }
  ```

  规则不得自行插语句/自行格式化/自行写文件——提升走共享 `TempLifter`，输出由入口统一格式化。

### 4.3 HandlerBindingAnalyzer（SemanticModel 判定）

输入 `InterpolatedStringExpressionSyntax` + `ExpressionDesugarFileContext`，输出 `InterpolationBinding(HandlerKind Kind, object? Detail)`：

| Kind | 判据（按序短路） | 处置路径 |
|---|---|---|
| `Constant` | `model.GetConstantValue(node)` 有值（含全字面量/常量符号、纯文本 raw/verbatim 插值） | IE-FOLD |
| `DefaultString` | `GetTypeInfo(node).ConvertedType?.SpecialType == System_String`，且 `GetOperation(node)` 为默认插值操作、**父链无** `IInterpolatedStringHandlerCreationOperation` | IE-CONCAT / IE-FORMAT |
| `FormattableString` | ConvertedType 为 `System.FormattableString` / `System.IFormattable`（含 `FormattableString.Invariant(...)` 实参位） | IE-KEEP（0511） |
| `CustomHandler` | 父链存在 `IInterpolatedStringHandlerCreationOperation`，或 ConvertedType 是带 `[InterpolatedStringHandler]` 的类型且非 string（含形参类型为 `DefaultInterpolatedStringHandler`/自定义 handler 的显式 handler 使用点） | IE-ERROR 0510 / IE-KEEP（Keep 策略） |
| `Unknown` | error type、绑定不可解析（跨编译缺元数据且非表达式树豁免） | IE-ERROR 0512 |

实现约束：

- handler 类型/构造参数的属性名（`InterpolatedStringHandlerAttribute`、`InterpolatedStringHandlerArgumentAttribute`）按符号元数据全名判定；`IInterpolatedStringHandlerCreationOperation` 在 Roslyn 4.12 的精确属性外形（条件追加标志、构造操作、参数映射）由 **M0 最小探针**先核实再写规则（仿 C19 设计的 EM-COND 探针纪律：禁止凭记忆写 API）。
- 洞信息 `HoleInfo` 每个洞记录：`ExpressionSyntax Expression`、`ITypeSymbol? Type`、`int? ConstantAlignment`、`string? FormatText`、`bool IsWhitelistIFormattable`、`bool IsRefStructLike`、`bool IsUnsignedNumeric`、`bool IsPointer`、`bool IsDynamic`。
- 只依赖语义判定；`$` 数量、raw/verbatim token 种类只影响文本解码（`InterpolationSyntaxFacts`），不参与 handler 分类。
- 分析只读；`dynamic` 洞不单独报错（按 object 装箱走默认路径，与 handler 非泛型路径等价），但绑定类别 `Unknown` 必须报错而非猜测。

### 4.4 插值规则集（IE-1 ~ IE-6）

- **IE-1 绑定分类**：HandlerBindingAnalyzer 五分类（§4.3），产出 `InterpolationPlan(Path, Holes, TextSegments)`。
- **IE-2 常量折叠**：`Constant` → `SyntaxFactory.Literal(constantValue as string ?? "")` 整体替换。
- **IE-3 Concat 发射**：`DefaultString` 且无对齐/格式且洞全白名单 → 分段（文本段 ValueText 原样、洞表达式零复制复用）→ 空文本段剔除 → `string.Concat(...)`；无符号四件套与安全 ref struct 的 `ToString` 包装在入段前按 §2.2 处理；单参/多参统一 `Concat`。
- **IE-4 Format 发射**：命中格式/对齐/非白名单 IFormattable → composite 逐段构建（文本 ValueText 做 `{`/`}` 翻倍；洞发 `{i}`、`{i,align}`、`{i:fmt}`、`{i,align:fmt}`；对齐取常量整数值）→ `string.Format(compositeLiteral, holeExpressions...)`。
- **IE-5 保留/报错**：`FormattableString`（0511）、表达式树（0515，受 `RewriteInExpressionTrees=false` 控制）、CustomHandler（0510，按 policy）、ref struct/指针洞（0513）、非常量对齐（0514）、Unknown（0512）。
- **IE-6 residual 自检**：改写后输出树按**语法种类**统计 `InterpolatedStringExpressionSyntax`；与已登记豁免（表达式树/Keep/FormattableString）配对后仍有未解释节点 → CS2J0516（strict Error）。按语法节点不按 `$` 文本计数（避免误伤普通字符串/`$"..."` 注释）。

### 4.5 共享 TempLifter（族基建；C35 最密切的后续消费者）

形态复刻 [NullCoalesceSyntaxRewriter.PendingDeclaration](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L20-L30) 与 [NullCoalesceStatementLifter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs#L11-L38)，但泛化为族共享：

- `record PendingTemp(StatementSyntax Declaration, StatementSyntax AnchorStatement, int Order, ExpressionDesugarRuleId Owner)`：Pass1 只登记，不就地插语句。
- 锚点 = 最近外围 block 直接语句；同锚点多临时按 `Order` 稳定排序（求值顺序即插入顺序），不同规则共用同一序号空间（由文件上下文发号），保证未来 C23/C35 同句协同时顺序确定。
- Pass2（`VisitBlock`）在锚点语句前按序插入；无锚点位置（字段初始化器/特性/表达式树/查询子句）规则侧必须走「零临时」形态，C23 的 IE-CONCAT/IE-FORMAT 天然零临时故不受限；C24/C35 在这些位置需各自报显式诊断。
- 临时名前缀 `__ed_`（expression-desugar），文件内计数 + 同作用域碰撞避让；`__ed_` 非 Java 保留字，C08 无需特殊协调。
- 接口冻结点：`PendingTemp` 形状、`Collect/Apply` 两步、文件上下文发号器——C24/C35/C36 落地前不得改签名；NullCoalesce 既有 lifter 列为后续收敛候选（本期不动它，与 C31 设计 Q3 口径一致）。
- C23 主路径预期 `TempsLifted=0`；单测显式断言「Concat/Format 展开不产生临时」，防止规则意外复制表达式。

### 4.6 文件所有权表

| 文件/目录 | 本期所有者 | 后续扩展者 | 扩展规则 |
|---|---|---|---|
| `ExpressionDesugarPreprocessor.cs` / `ExpressionDesugarRuleSet.cs` / `Shared/*` | **C23（锚点）** | 全族评审 | 接口只增不改；加规则 = 清单加一行 + 新目录，不改入口流程 |
| `ExpressionDesugarOptions.cs` | C23 | C24/C35/C36 | 追加 nullable 子选项属性（组合式），不改既有属性语义 |
| `ExpressionDesugarStatistics.cs` / `ExpressionDesugarDiagnostic.cs` | C23 | C24/C35/C36 | partial 文件落各规则目录；码段只追加 |
| `Interpolations/*` | C23 | C23 | —— |
| `Initializers/*` | **C24**（文档同日另撰） | C24 | 占位目录本期零代码 |
| `NullConditional/*` | **C35**（姊妹文档，2026-09-29 撰写时尚不存在，按锚点假设） | C35 | TempLifter 的首个重度消费者 |
| `CollectionExpr/*` | **C36** | C36 | spread 语法节点与本族无交集，复用提升/绑定基建 |
| CLI `ExpressionDesugarPassAdapter.cs` | C23 | 全族 | 适配器单文件，新增规则一般无需改适配器（子选项经 Options 透传；需要新 CLI 开关时追加） |

## 5. CLI 适配器与集成

新增 [Adapters/ExpressionDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ExpressionDesugarPassAdapter.cs)，骨架复制 [NullCoalescePassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/NullCoalescePassAdapter.cs) 与 [IteratorLoweringPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/IteratorLoweringPassAdapter.cs)：

1. 持有 `ExpressionDesugarPreprocessor`、`ExpressionDesugarOptions`、累计 `ExpressionDesugarDiagnostic`/`ExpressionDesugarStatistics`；`Descriptor` 取 `PassCatalog.ImplementedById(PassStubCatalog.Ids.ExpressionDesugar)`。
2. `RewriteFileAsync`：调 `Preprocess(context.SourceCode, context.SemanticModel, _options)`，合并统计、给诊断附 `RelativePath`，Changed=false → null（clean 文件字节级复制，保 BOM 走 runner 默认）。
3. `AfterFilesAsync`：对中间树做全树 residual 复扫（仿 [NullCoalescePassAdapter.cs:74-92](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/NullCoalescePassAdapter.cs#L74-L92) 的 `CountResidual…`），按语法种类计数并与豁免配对；未解释残留 >0 时按 strict 发 CS2J0516。
4. `VerifyBuild/VerifyTests` 取 config 开关（默认 true/true）；`DeleteBuildOutputsAfterRestore = true`（双验后清 bin，同 null-coalesce/iterator）。
5. `HasErrors` = 含 Error 诊断；`BuildChainSummaryLine` 至少打印：插值站点、Concat、Format、折叠、对齐、格式、自定义 handler（错/保留）、FormattableString 保留、表达式树跳过、临时提升（预期 0）、residual；`BuildChainDiagnosticLines` 格式 `[expression-desugar] [severity] path: message`。
6. `OnRestoreFailed/OnBuildFailed/OnTestsFailed` 记 CS2J0520（restore 为 Warning，build/test 为 Error，仿 iterator 适配器）。
7. **支撑库：无**。`SupportLibrary.None`：不注入工程、不进支撑库过滤、`IsExecutedSupportLibraryProject` 无需改动；产物只含 BCL `System.String` 调用。
8. **指纹**：无需手写——[Program.cs:1765-1774](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L1765-L1774) 已对注册表每个 pass 自动生成 `pass:<id>=on/off` 令牌，翻转后自动纳入；无支撑库文件进快照问题；切换组合一次性缓存失效可接受（foundation plan 既定）。
9. **集成点**：仅 §3.3 第 4/5 两处（`PassCatalog` + `PassChainFactory`）；标准 spine「parse→compile→逐文件 rewrite→AfterFiles→restore→verify」（[ProjectPassAdapter.cs:268-269](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L268-L269)）无定制。
10. 通用 verb 为唯一项目级入口（`preprocess --pass expression-desugar`、`--enable-pass expression-desugar`、`--list-passes`）；**不做单文件模式**（handler 分类跨文件，且单文件场景自定义 handler/FormattableString 多落报错）。

## 6. 诊断策略

**第一原则（catalog §4 风险 12/18）**：无法在 C# 侧等价显式化时**显式报错**，禁止注释/占位/原样静默透传。S2（丢对齐）、S3（TODO 注释）、S1（handler 无脑降级）三类静默在本 pass 全部转为「正确展开」或「带码失败」。Error 诊断使运行退出非零；strict 关闭时 Error 降 Warning 但仍逐条可见、可计数。

码段 `CS2J05xx`（expression-desugar 族；只追加不改号；MSBuild 风格输出）：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J0501 | Info | 插值站点经 IE-CONCAT 显式化为 `string.Concat`（计数用） |
| CS2J0502 | Info | 插值站点经 IE-FORMAT 显式化为 `string.Format`（内部分别计对齐/格式/IFormattable 升级） |
| CS2J0503 | Info | 全常量插值折叠为字符串字面量 |
| CS2J0510 | Error（strict 默认）/Warning（Keep 策略） | 插值绑定自定义 `InterpolatedStringHandler`（含显式 handler 形参位）：消息含 handler 类型全名、构造签名与「参数求值次数/条件构造不一致，禁止降级 Concat/Format」说明，并给手工改写建议 |
| CS2J0511 | Warning | 绑定 `FormattableString`/`IFormattable`，节点原样保留（用户显式运行时形态） |
| CS2J0512 | Error | 绑定不可判定（error type/跨编译元数据缺失）；不猜测、不展开 |
| CS2J0513 | Error | ref struct（无可证非虚 `ToString` 覆盖）或指针洞无法物化为 `object` 实参 |
| CS2J0514 | Error | 对齐子句不是编译期常量（防御镜像；基线 C# 本应报错） |
| CS2J0515 | Info | 表达式树 lambda 内插值按策略跳过（M1–M3 默认跳过；M4 评审 Q4） |
| CS2J0516 | Error（strict）/Warning | residual 闸门：输出中仍残留无法与豁免配对的 `InterpolatedStringExpressionSyntax` |
| CS2J0520 | Warning/Error | restore/build/test 失败（适配器钩子记录） |

**码段划分（族预留）**：C23 占 `CS2J0501–CS2J0519` + 族公共 `CS2J0520/0529`；C24 预留 `CS2J0530–0549`；C35 预留 `CS2J0550–0569`；C36 预留 `CS2J0570–0589`；族基建保留 `CS2J0590–0599`。各族设计文档落地时在本段登记，不交叉占用。

统计（`InterpolationStatistics.cs`，partial 累加）：`InterpolationSites`、`ConcatExpansions`、`FormatExpansions`（子计 `ByAlignment`/`ByFormatClause`/`ByIFormattableUpgrade`）、`ConstantsFolded`、`AlignmentSites`、`FormatClauseSites`、`NestedInterpolationSites`、`UnsignedHoleWraps`、`RefStructToStringWraps`、`CustomHandlerSites`、`KeptFormattableStringSites`、`ExpressionTreeSitesSkipped`、`RefStructOrPointerRejections`、`SkippedByReason: Dictionary<code,int>`，加通用五字段与族 `TempsLifted`。

## 7. 测试计划

### 7.1 Core 单测 `tests/CSharpToJava.ProjectTests/InterpolationExpandUnitTests.cs`

内联源码 → net10.0 `CSharpCompilation`（Roslyn 4.12，含自定义 handler/FormattableString 夹具类型）→ `Preprocess` → 文本断言（忽略空白）+ 输出可编译断言 + 二次运行幂等断言。矩阵：

1. **基础分段**：`$""`、纯文本、单洞、多洞、连续洞、首尾文本、空文本段剔除、`$"{a}{b}"`、单洞 string/int；
2. **Concat/Format 选择**：无格式→Concat；仅格式→Format；仅对齐→Format；对齐+格式→Format；自定义 `IFormattable`（显式接口实现，无参 `ToString` 与 `ToString(null,provider)` 返回不同串的探针类型）→整串 Format 且结果经 provider 分派；白名单 BCL 类型（int/double/bool/enum/DateTime/Guid/TimeSpan）无格式→Concat；
3. **对齐**：正/负/零宽度、与格式组合、宽度大于/小于实际宽度的空格填充方向（.NET 行为断言而非文本猜测）、非常量对齐表达式→CS2J0514；
4. **格式**：D/F/E/X（大小写）/N/G/P/M（日期）/R 等说明符**原样透传** composite；未知说明符（如自定义 `:Q2`）不产 TODO 注释、C# 侧行为保持；composite 中字面 `{{`/`}}`、文本含 `%`、引号、反斜杠；
5. **文化（行为断言）**：在测试线程切 de-DE（或探针 `IFormatProvider` 自定义文化）：`$"{1.5}"`、`$"{DateTimeNow}"`、`$"{1.5:F2}"` 预处理后 .NET 结果与原生插值**逐串相等**；断言产物**不含** `InvariantCulture`/`FormattableString.Invariant`；
6. **嵌套**：洞内插值两/三层、Concat 套 Format、嵌套洞副作用顺序；
7. **副作用与单次求值（探针计数，非仅文本）**：`$"{Next()}-{Next()}"`（顺序、各 1 次）、洞内含赋值/后自增/三元/`await`/条件访问式实参、接收者为方法返回；断言 Concat/Format 展开**零临时变量**；
8. **null**：null string、null object、`int?` 无值 → 结果 `""`；与文本/格式混合；
9. **值类型面**：int/long/double/float/bool（`True`/`False` 文本）/char/enum/DateTime/自定义 struct（override ToString）/显式 IFormattable 探针/无符号 byte/ushort/uint/ulong（无格式走 `ToString()` 包装、有格式走 Format 原样）/`ReadOnlySpan<char>` 洞走 `ToString()`/未知 ref struct 与指针洞→CS2J0513；
10. **常量折叠**：全字面量、const 符号组合、`const string` 声明上下文必须可编译、默认参数上下文；含非常量洞不折叠；折叠值与 `GetConstantValue` 一致；
11. **raw/verbatim**：`$@""`（内嵌引号 `""`、折行）、`@$""`、单行/多行 `$""""""`、`$$""""""`（多美元括号层数）、raw 缩进剥离、含换行/引号/花括号文本；产物中无 `@`/`"""` 词法；
12. **自定义 handler**：探针定义带 `[InterpolatedStringHandler]` 的 handler（构造 `out bool append`、`[InterpolatedStringHandlerArgument]` 前置参数、append=false 时洞零求值）→ 默认 CS2J0510 Error 且文件不改写；Keep 策略下 Warning + 节点保留 + residual 配对豁免；日志风格（无 string 结果）与 JSON writer 风格两类夹具；
13. **FormattableString**：变量/参数/`Invariant(...)` 实参位 → CS2J0511 保留，二次运行稳定；
14. **表达式树**：`Expression<Func<string>>` 内插值 → CS2J0515 跳过且无 residual 报错；普通 LINQ（IEnumerable 链与查询理解式）内插值正常展开；
15. **防误伤**：`nameof(x)`、含 `{`/`%` 的普通字面量、显式 `new DefaultInterpolatedStringHandler(...)` 均不触达；
16. **幂等/clean**：无插值文件 Changed=false；展开产物二次 Preprocess 零 diff；
17. **诊断闸门**：CS2J0516 构造（人为保留未配对节点）、strict 开/关两态、统计分类计数正确、行号/文件路径附加正确。

### 7.2 项目级 fixture `tests/SampleInterpolationSolution/`

仿 SampleArraySolution：`SampleInterpolationLib` + `SampleInterpolationTests` + sln，`Content Include` 进 ProjectTests，net10.0。内容：覆盖跨文件类型的插值、自定义 handler 夹具类型（单独文件/条件编译，确保默认 strict 下该用例走独立期望或独立 sln，避免整树 fail——建议拆「正向 sln」与「handler 报错 sln」两个夹具）、clean 工程字节级复制（含 BOM 保持）。断言：

- 正向 sln：`result.Success`；中间树文本含 `string.Concat`/`string.Format`、**不含** `InterpolatedString` 语法节点（豁免区除外）；中间树 `dotnet build -c Release` 与 `dotnet test -c Release` 退出码均 0；
- 报错 sln：handler 用例 strict 下 `Success=false`、CS2J0510 出现、退出非零且**不残留半成品目录**（stub/失败语义先例已有）；
- **四象限组合**（expression-desugar × unsigned 两 pass on/off）各跑一次：无符号洞在 unsigned 开时由其 `ToString()` 拦截链路接走、关时 C# 半区仍正确；目录/指纹互不干扰；
- 与 make-readonly 的组合样本：含被插值洞引用的 struct，验证显式 Concat/Format 装箱点对只读分析无负作用（§11）。

### 7.3 两半区运行时结果对照（预言机）

- **.NET 半区（本 pass 的主正确性证据，catalog §5.1）**：同一套 xUnit 断言在「原生 C#」与「预处理后中间树」上各跑一次，逐串相等——基础、对齐方向、格式说明符全集、文化（显式固定 `CultureInfo` 与自定义探针 provider 双文化）、null、枚举、无符号、嵌套、副作用计数、`const` 折叠。
- **Java 半区**：仿 [ArrayMethodConversionEndToEndTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/ArrayMethodConversionEndToEndTests.cs) 增 e2e：经完整转换跑 Java 测试，固定 `Locale`（与 .NET 固定文化对照），钉 `Concat` 路径（null→`""`、无符号四件套、bool 大小写、double 整数形态）与 `Format` 路径（D/X/F/对齐）。已知缺口（`%s` null、`formatCs` 不支持对齐）在默认开门禁前列为必须关闭项或显式受限集（§8 第 3 条、Q2）。
- 现有护栏不被默认关破坏：[StringTests.StringInterpolation_ConvertsToConcatenation](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/StringTests.cs#L25-L31) 等直连转换断言保持绿色（pass 默认关，输入仍含插值）；[StringFormatDoubleTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StringFormatDoubleTests.cs) 空壳在本期补入实质用例（或由新 e2e 取代并登记删除）。

### 7.4 一致性 / CLI / 链序

- §3.3 第 6 条三个测试文件断言更新（PassStubCatalog 23→22、LockedChain 插入位、一致性目录名）；
- 新增 `ExpressionDesugarCLITests`（仿 `PreprocessVerbCLITests`/`JavaKeywordCLITests` 项目级驱动）：默认关不出目录不耗时；`--enable-pass`/`preprocess --pass` 执行；`--list-passes` 显示 Implemented/默认关/catalogItems `C23`；strict 报错退出码非零且不留半成品目录；
- residual 闸门测试（AfterFiles 全树复扫配对逻辑）。

### 7.5 residual「零插值」口径

默认开验收时，训练画像上的每个 `InterpolatedStringExpressionSyntax` 必须恰属于：已展开（0501/0502/0503）、显式保留（0510-Warn/0511/0515）三类之一，且抽样可复核；**不允许**出现无诊断的静默残留（0516 兜底）。

### 7.6 验证命令（实施期执行；本设计任务不执行任何 build/test/git 写操作）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

## 8. 默认开启验收门槛

opt-in 落地后翻 `DefaultEnabled=true` 的全部条件：

1. §7.1 矩阵全绿；fixture 与三个测试工程 Release `build`/`test` 全绿；幂等零 diff；
2. residual 零「未解释插值」：画像中每处插值都有展开或显式保留诊断；CS2J0510 在画像上的命中清单逐条评审（日志/JSON handler 的真实分布）；
3. .NET 半区与原生 C# 行为等价（文化、对齐、null、无符号、副作用次数全矩阵）；Java 半区：Concat 组全绿，Format 组的对齐/null/文化缺口已由 compat 能力盘点关闭或明确受限集（依赖 `StringHelper` 格式化侧的既有 `formatCs/csFormat` 资产，是否扩展/改路由见 Q2）；
4. 指纹/保留目录/落盘行为与既有 pass 一致；无支撑库所以无注入/过滤回归；
5. csharp-sdk 画像（约 650 处插值）以 opt-in 跑一轮：命中率/五路分布/CS2J0510 清单/基线 build/test 不退化（catalog §4 风险 10）；
6. Java 侧 [StringExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs) 冗余分支的下掉有跟进 PR 排期（直连路径保留为安全网或移除，二选一并附测试）；S3 的 TODO 占位产出在默认链路上确认不再可达；
7. C24/C35/C36 锚点未落地不阻塞本 pass 默认开：规则集单规则运行、占位目录零行为、族接口冻结说明经评审。

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M0 探针 + 骨架（不翻转 stub） | ① Roslyn 4.12 探针：`IInterpolatedStringHandlerCreationOperation` 精确外形、自定义 handler 的 `GetOperation`/`GetTypeInfo` 表现、FormattableString 绑定形状；② raw 1/2/3 个 `$` 的 ValueText/括号行为探针；③ `ExpressionDesugar/` 全部共享骨架（Options/Statistics/Diagnostic/Result/规则接口/TempLifter/Analyzer）+ 四规则目录占位 + 空适配器（不接线）；骨架单测（临时提升/锚点/碰撞） | Core ~500 行 |
| M1 IE-FOLD + IE-CONCAT + 绑定门 + 翻转 | HandlerBindingAnalyzer 五分类；常量折叠；Concat 发射（白名单、无符号 `ToString` 包装、ref struct 白名单）；CS2J0501/0503/0510/0511/0512/0513；适配器接线 + §3.3 翻转清单（默认关）；Core 单测主体 + 报错 sln 夹具；Release build/test 全绿 | Core ~700 行 |
| M2 IE-FORMAT + 对齐/格式/composite | FormatEmitter（`{i,align:fmt}`、括号翻倍、IFormattable 升级）；CS2J0502/0514/0516；嵌套/副作用/文化探针单测；正向 fixture 双验；四象限（unsigned）组合；residual 全树复扫 | ~500 行 |
| M3 raw/verbatim 全矩阵 + Java 半区 | raw/verbatim/多美元矩阵；表达式树跳过（0515）；.NET 半区预言机全量；Java 半区 e2e（Concat 全组 + Format 受限组）；CLI/链序/一致性测试；空壳 StringFormatDoubleTests 处置；Release 三工程全绿 | ~400 行 + fixture |
| M4 画像评审 + 默认开 | sdk 650 处画像试跑；CS2J0510 清单评审与 Q1/Q2/Q4 决策（自定义 handler 白名单是否做、表达式树是否降编译器规范形）；满足 §8 后翻默认开；Java 侧大 transformer 下掉 PR 排期 | 扫描/评审为主 |

M0–M4 全程 `DefaultEnabled=false`；每个里程碑自身 Release build/test 全绿，不留半残中间树；M0 不翻转 stub（stub 被显式启用仍 CS2J9001）。

## 10. 风险

| # | 风险（按严重度） | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **文化语义错配（最高风险）**：C# 默认插值是 CurrentCulture；Java `String.format` 走默认 Locale，compat 现有 `formatCs/csFormat` 是 Locale.ROOT 近似且不吃对齐、null 印 `"null"`。展开后 Format 调用在 Java 半区可能产出文化差异文本 | [StringHelper.java:1099-1146](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1099-L1146)/[:1241-1312](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java#L1241-L1312)；S5 | C# 半区绝不用 invariant（不把文化债前置成错译）；双半区固定文化预言机；默认开门禁把 compat 格式化能力（对齐/null/文化路由）列为硬项（Q2）；风险敞口只在 Java 半区且可定位 |
| R2 | **自定义 handler 静默错译**：650 处中日志/JSON 高频；Concat 强行求值、伪造 string | catalog §4 风险 18；S1（全 src/tests 零 handler 判定） | handler 绑定为前置门；默认 CS2J0510 Error；Keep 仅非 strict 显式逃生；M4 画像清单评审；M1–M3 不仿真 |
| R3 | **对齐现状静默丢弃**：`,n` 被忽略无人报错，无测试 | S2；grep 全仓无对齐用例 | IE-FORMAT 透传 `{i,n}` + .NET 行为断言；对齐方向/宽度矩阵单测 |
| R4 | **null format 洞 Java 印 `"null"`** | S7；`formatCs` 无 null 归一 | C# 半区由 `string.Format` 保证；Java 半区缺口列入 §8 第 3 条；混合 null+格式用例 e2e 钉死 |
| R5 | **无符号洞跨 pass 契约**：C23 在 unsigned 之前，直接发 UnsignedMath 会打乱支撑库注入顺序；发 `ToString()` 依赖 E09 拦截（仅 uint/ulong 有分支） | [UnsignedTypeSyntaxRewriter.cs:1279-1301](file:///d:/code/cs2j/src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs#L1279-L1301) | byte/ushort 统一 `((uint)x).ToString()` 归 uint 拦截；四象限组合 fixture；E09 关闭时 .NET 半区仍必须绿（`ToString()` 本身即 BCL 无符号文本） |
| R6 | **表达式树/Linq 形状**：改写表达式树内插值会改变 IQueryable 提供方看到的树 | catalog §4 风险 8/17；E16 [LinqRewriter](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) grep 无插值处理 | M1–M3 共享判定跳过（0515）；IQueryable 三路分流归 C26；Q4 评审 M4 是否降编译器规范形 |
| R7 | **格式小语言翻译债外溢**：现状 Java 侧有损转换表 + TODO 站点（S3/S4），若误把翻译搬进 C# pass 会把债固化 | [StringExpressionTransformer.cs:342-370](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L342-L370) | composite 原样透传，翻译只留在 Java 半区一处（compat），默认开后删 Java 侧重表 |
| R8 | **装箱点显形影响 make-readonly**：插值本就经 handler 装箱 struct，但现转换器不可见；展开为 Concat/Format 后装箱点对 [BoxingReachabilityAnalyzer](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/CallSiteUpdater.cs#L471) 显形（其已列 Format/Concat/Join，[BoxingReachabilityAnalyzer.cs:341](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/BoxingReachabilityAnalyzer.cs#L341)） | 链序 expression-desugar 早于 make-readonly | 理论上只提升分析精度（装箱本就存在）；fixture 放 struct 插值组合；默认开前跑 make-readonly 基线不退化 |
| R9 | **raw/词法越界**：误碰 C43 职责（定界符/u8/缩进策略）会与未来 lexical-normalize 争抢 | catalog C43（784-795 行）；S8 | 只消费 ValueText/已 parse 洞；产物全常规词法；M0 探针锁定行为；C43 文档对齐边界 |
| R10 | **默认关双轨期行为分叉**：pass 开/关两套字符串路径，画像统计与 Java 侧断言可能只覆盖一条 | catalog §4 风险 10 | 默认关不改既有测试；新行为全部走 opt-in 测试；§8 门槛控制默认化；大 transformer 下掉放在最后 |
| R11 | **ref struct/指针洞表达力边界**：handler 泛型允许、目标两路径不允许 | §2.2 决策行 | 白名单 `ToString()` + CS2J0513；不发明 helper；C06 域（指针）不越界 |
| R12 | **composite 转义错误**：括号翻倍作用在错误文本层（Token.Text vs ValueText）、raw 多美元、`%` 处理位置混淆会产生运行时格式异常 | §2.2 composite 行 | 统一 ValueText→翻倍；`%` 不在 C# composite 转义（Java 半区 formatCs 已 replace）；花括号/百分号专项单测 |
| R13 | **链耗时/指纹**：新增语义 pass + 中间目录拉长 convert-project | catalog §4 风险 11 | clean 文件字节复制零成本；verify 可关；指纹自动接入；无支撑库快照负担 |
| R14 | **Roslyn API 外形记忆偏差**：handler creation operation 的条件/参数属性在 4.12 的确切外形可能与设计假设不同 | C19 EM-COND 同类教训 | M0 探针先行（§9），规则代码必须以探针结论为准 |

## 11. 并行协调

| 相邻工作 | 边界 | 接口约定/合并顺序 |
|---|---|---|
| **C24 InitializerDesugar / C35 NullConditionalDesugar / C36 CSharp12Desugar（同一 pass）** | 共用 pass id、中间目录、适配器与全部 `Shared/`；C23 是锚点，先落基建与规则接口；C35 是 `TempLifter` 最密切的重度消费者（`?.`/`?[]` 接收者单次求值），C24 初始化器提升、C36 集合目标构造均会产生语句前置 | 目录/码段/Options 组合属性/partial 统计/规则接口按 §4.6 预留；各族只加本规则目录 + `ExpressionDesugarRuleSet.cs` 一行 + catalogItems 加宽；**热文件冲突解法**：`RuleSet`/Options/Statistics 冲突全部是追加式（加行/加 partial），约定后落地者 rebase 取并集，禁止修改既有规则行；共享接口变更需全族评审 |
| **C35 姊妹文档** | 2026-09-29 撰写时 `2026-09-29-c35-null-conditional-design.md` **尚不存在**（已核对 specs 目录）；本文按锚点假设编写：C35 将消费同一 `TempLifter`（PendingTemp/Collect/Apply + `__ed_` 命名 + 锚点规则）与共享表达式树判定 | C35 文档落地时若发现锚点/锚语句需求不同，以其文档提差异，由族评审演进接口（只增不改）；C23 M1 冻结契约前预留 review 窗口 |
| **C43 lexical-normalize（raw/u8/verbatim）** | C43 管词法定界符与非插值字面量；C23 只管已 parse 插值的**插值部分**；raw 插值的文本缩进/括号归一由 Roslyn ValueText 承担，双方都不做对方的词法变换 | 两 pass 语法面不相交（节点种类不同）；链位上 C43 属 Lexical 前段、C23 属 Expression，即便同树也幂等；边界在两文档互引 |
| **E16 LinqRewriter / C26 linq-channel（含 IQueryable 分流门）** | 现状 E16 对插值无特殊处理（grep 证据）；C23 先于 linq-channel，展开后 E16 看到的是普通 BCL 调用，无语法面争抢；表达式树内跳过与 C26 IQueryable/Expression 三路分流共用同一禁区判定 | 共享 `IsInExpressionTree` 口径（M1 C23 内置最小实现，注释登记与 C02/C19/C26 的合并去向）；C23 不替 C26 做 queryable 决策 |
| **C19 extension-methods** | C23 发射的 `string.Concat/Format` 是 BCL 静态调用，对 C19 不可见；插值**洞内**的 reduced 扩展方法调用保持原节点作为实参，由链后 C19（锚点：所有注入支撑库 pass 之后）归约为静态调用 | 链序保证 C19 在 expression-desugar 之后看到实参；无需 RunAfter 边；联合用例：`$"{arr.MyExt()}"` 先变 `Concat(arr.MyExt())` 再变 `Concat(Ext.MyExt(arr))` |
| **E09 unsigned** | 见 R5：无符号洞发 `ToString()` 交 E09 拦截；C23 不引用 UnsignedMath 支撑库 | 四象限组合为发布硬测试；E09 关闭时纯 BCL 形态在 .NET 半区自洽 |
| **make-readonly（E13）** | 见 R8：装箱点显形 | 组合 fixture + 基线不退化 |
| **C08 java-keywords（链尾）** | `__ed_*` 临时名与 `Concat/Format` 非 Java 保留字 | 无协调；若撞名由 C08 改名表统一覆盖 |
| **Java 侧转换器/StringHelper（同仓不同半区）** | [StringExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs) 本期不动；默认开后下掉 S1–S8 分支；[StringConcatRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/StringConcatRewriter.cs) 继续服务显式 Concat；compat `formatCs` 的对齐/null/文化增强（Q2）是 Java 半区独立工作项，不进本 pass 目录 | 默认开门禁（§8 第 3/6 条）协调排期；两阶段合并不影响 pass 自身落地 |
| **注册表/链序测试** | 本分支独占注册表变更 = 删一行 stub + 加一行 Implemented + 工厂一分支 + 三处断言 | 与同期翻转其他 stub 的工作在 PassStubCatalogTests 计数（23→22 与他族翻转可加性：注意若他族先合，基数再变，以合入时实际值为准）/LockedChain 上小冲突，按注册表评审节奏 rebase |

## 12. Worktree 分配（只写不执行；本设计任务未执行该命令）

- 分支：`feature/preproc-c23-interpolation`
- 目录：`.worktrees/preproc-c23-interpolation`
- base：`953293b9`（`docs: update doc base commit`，已 `git rev-parse --verify` 校验存在）

```powershell
git worktree add .worktrees/preproc-c23-interpolation -b feature/preproc-c23-interpolation 953293b9
```

实施在该 worktree 内进行；本设计文档产出于主工作区，不创建 worktree、不执行任何 git 写操作。

---

## 附：开放问题（实现前/默认开前需评审确认）

- **Q1 自定义 handler 的终态策略**：M1–M3 默认 Error、非 strict Keep。M4 画像后需定：是否对高频具体 handler（如 Microsoft.Extensions.Logging 条件 handler、System.Text.Json writer handler）做**受控仿真白名单**（模拟构造参数提前求值 + `out bool append` 守卫 + 零求值语义），还是永久「Error/Keep 二选一」。仿真成本高且每家 handler 追加语义不同，倾向后者，需要 sdk 命中数据支撑。
- **Q2 Java 半区格式化落点**：扩 `StringHelper.formatCs`（补对齐 `,n`、null→`""`、文化路由）并把转换链上 C# 源码里的 `string.Format`（含本 pass 新增与用户既有 `{0}` 直调）统一路由过去？还是维持 `TypeMappings` 的 `Format→format` 直映、仅对本 pass 产物特判？前者语义集中但触及现有 `{0}` 直调行为面，后者留双轨。需在 §8 第 3 条前定案。
- **Q3 Concat 等价白名单的边界**：当前以「基元/enum/已知 BCL 文化等价类型」为白名单、其余 `IFormattable` 一律升 Format。白名单是否收紧为「仅基元 + string」（更保守、Format 更多）或放宽到「所有非显式接口实现类型」（更激进），影响性能形态与画像中 Format/Concat 比例，由 M2 实测数据评审。
- **Q4 表达式树内插值是否在 M4 收编**：C# 编译器在表达式树中本就把插值降为 `string.Format("{0}", x)` 调用节点；将树上插值显式化为该规范形理论上不改变树形状（相对编译器原生 lowering），但需先证明与 Roslyn 生成形态逐节点一致。M1–M3 维持跳过（0515）。
- **Q5 默认开与 Java 侧大 transformer 下掉的节奏**：是否要求「下掉 [StringExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs) 插值分支」作为翻默认开的同 PR 硬前提，还是默认开后保留一个版本的双轨（直连入口安全网）再删。倾向前者由独立 PR 完成、后者作为翻默认开前提（§8 第 6 条），需评审确认风险敞口。
