# C35 空条件 `?.` / `?[]` 去糖前置化（`expression-desugar` 族规则）设计

**日期**: 2026-09-29
**范围**: 在 C23 锚点落地的 `expression-desugar` 族基建（`src/CSharpToJava.Core/ExpressionDesugar/`）内新增规则目录 `NullConditional/`，在 C# 层把空条件成员访问（`a?.b.c?.d()`）、条件索引（`arr?[i]`）、委托/事件 `?.Invoke()`、void 语句上下文显式化为「段临时变量 + null 检查三元 / if 语句」（**条件赋值目标 `a?.b = v` 已裁决移出本规则、单列 C51**，见 §3 NC-7 注记），保持接收者**严格单次求值**与 null 短路；值类型成员终端产 lifted `Nullable<T>` 形态。不注册新 pass、不翻转 stub、不改任何 C#→Java 转换代码（Java 侧特判簇的下掉为默认开启后的独立 PR）
**状态**: 设计已确认，待实施（C23 锚点已随 `dev-3.x-26100408` base 就位；C35 与 C51 在同一 worktree 内实施）
**注册表**: 不新增/不翻转任何条目；沿用 stub `expression-desugar`（[PassStubCatalog.cs:217-226](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L217-L226)，catalogItems `"C23, C24, C35, C36"`，Stage=Expression，RunAfter=`null-coalesce`，中间目录 `.cs2j-expression-desugar-src`）。C23 翻转时 catalogItems 收窄为 `"C23"`；本特性落地时在**同一描述符**上加宽为 `"C23, C35"`（[C23 §3.4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L193-L197)）
**base**: `e50d987470bde644137e07094d93fd1467c6ca9c`（`feat(preprocessing): 波次0接线激活 C24默认接通+C27/C50/C46 CLI开关+catalogItems订正`，`dev-3.x-26100408` 最新提交；2026-10-04 已 `git rev-parse --verify` 校验存在；C23 锚点已在该 base 内，C35 与 C51 在同一 worktree 内实施，见 §12）

---

## 1. 背景与目标

### 1.1 现状：facade 特判簇——非 fail-fast、但特判面最宽的一族

空条件今天由 **C#→Java 主转换器内部**直接消化，类型解析贯穿整个表达式转换器，2026-09-29 复核证据如下：

| # | 站点 | 现状行为 | 后果 |
|---|---|---|---|
| S1 | [ControlFlowTransformer.cs:314-337](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L314-L337) `TransformConditionalAccess`（注册 [L22](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L22)、分派 [L45](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L45)） | 产 `({objExpr} != null ? {whenNotNull} : null)`；值类型非 `Nullable<T>` 消费上下文时 false 分支改发基元默认值（判定 [L322-335](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L322-L335)；字面量 `false/0/0.0/Decimal.ZERO` 在 [L339-350](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L339-L350)） | Java 侧内联三元，接收者文本 `objExpr` 在条件与真分支重复出现；带副作用接收者被求值两次；lifted 判定（`ConvertedType`）与 Java 拆箱特判耦在字符串发射里 |
| S2 | [ExpressionTransformerFacade.cs:96-388](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L96-L388) `TransformWhenNotNull` | 四处「所属 ConditionalAccess」owner 链回溯：tuple ItemN 回退取接收者类型（[L117-123](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L117-L123)）、属性映射取接收者具体类型（[L165-174](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L165-L174)）、TypeMappings 兜底回溯（[L189-199](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L189-L199)）、`?.Select/.Where` 取接收者类型（[L271-275](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L271-L275)）；另有嵌套 CA 分支（[L379-381](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L379-L381)）与 `MethodInfo?.Invoke` 特判（[L297-317](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L297-L317)、[L394-413](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L394-L413)） | 「条件访问的接收者类型」这一事实被全表达式转换器反复重新发现；语义解析失败时靠 owner 链猜测，是 catalog C35 条目点名的特判簇本体 |
| S3 | void/语句上下文由语句层另起一套：[StatementTransformer.ExpressionAndReturn.cs:21-24](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L21-L24)、`TransformConditionalAccessStatement` [L317-361](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L317-L361) | `obj?.M();` 转 `if (obj != null) { … }`；字段式事件 `evt?.Invoke(args)` 特判为 fire 方法 + 监听器非空（[L324-333](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L324-L333)）；委托字段 getter/SAM 推导（[L371-383](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L371-L383)） | 值路径三元与语句路径 if 两套实现同构但分立；嵌套链递归重拼（[L352-355](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L352-L355)） |
| S4 | `?[]`（`ElementBindingExpressionSyntax`） | 2026-09-30 复核：全 `src/` grep `ElementBinding` 已**非零命中**——member-desugar 族 [IndexerRule.cs:97-100](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerRule.cs#L97-L100) 分派 `ElementBindingExpressionSyntax`、[IndexerUseSiteRewriter.cs:166-183](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerUseSiteRewriter.cs#L166-L183) 把有 plan 的外部索引器 `r?[i]` 改写成 `r?.get_Item(i)`（IX-9）；但 `TransformWhenNotNull` 仍只处理 `ElementAccessExpressionSyntax`（[L374-377](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L374-L377)），无 ElementBinding 分支，无 plan 的形态（如数组 `a?[0]`）落入 facade 总兜底 `/* TODO: ElementBindingExpression – … */`（[ExpressionTransformerFacade.cs:29-38](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L29-L38)） | catalog §4 风险 12 族静默错误输出；现有测试 [NullableTests.cs:25-31](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/NullableTests.cs#L25-L31) 竟以 `AssertConversion(result, "TODO: ElementBindingExpression")` **锁定错误产物** |
| S5 | 无 fail-fast 正面清单 | 表达式未知 kind 仅 facade 发 TODO 注释（[L35](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L35)）；ControlFlowTransformer 未知 kind 在 [L56](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L56) `throw new NotSupportedException` | 条件访问各形态靠散落分支覆盖，漏一个即静默 TODO 进 Java 产物 |
| S6 | 旁路消费者 | [TupleToStructPreprocessor.cs:650-673](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L650-L673) 重建 `t?.Item8` 成员链；[LinqRewriter.cs:77-83](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L77-L83) 与 [HIRExpressionGenerator.cs:406](file:///d:/code/cs2j/src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs#L406) 各有 CA 访问者；[RecordReadOnly/MutationSiteRewriter.cs:1031-1097](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs#L1031-L1097) 已收编 `p?.X = 1`（record struct → switch/with，副作用接收者 RR051，[L213](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs#L213)） | 前置归一必须给每条旁路明确交接结论（§11），不能各写一套 |

### 1.2 目标形态

预处理后的中间树满足：

1. 用户代码范围内**零** `ConditionalAccessExpressionSyntax` / `MemberBindingExpressionSyntax`（条件段）/ `ElementBindingExpressionSyntax` 残留（除 §6 列明的显式登记豁免，每处豁免都有诊断配对），产物是普通、可编译、可直接在 .NET 上跑测试的 C#：

   ```csharp
   var y = a?.B;                       // → var __ed_s0 = a; var y = __ed_s0 != null ? __ed_s0.B : null;
   var z = arr?[i];                    // → var __ed_s0 = arr; var z = __ed_s0 != null ? __ed_s0[i] : null;
   a?.M(F());                          // → var __ed_s0 = a; if (__ed_s0 != null) __ed_s0.M(F());
   int? n = s?.Length;                 // → int? n = (__ed_s0 != null ? (int?)__ed_s0.Length : null);
   a?.b = v;                           // → var __ed_s0 = a; if (__ed_s0 != null) __ed_s0.b = v;
   Changed?.Invoke(x);                 // → var __ed_s0 = Changed; if (__ed_s0 != null) __ed_s0.Invoke(x);
   ```

2. 长链逐段临时变量、严格按 `?.` 文本顺序求值，null 短路：前段为 null 时后段的 getter、实参、索引运算元**一律不求值**。
3. 与 E03（`??`/`??=`）的组合以「E03 产物 → 本规则」为唯一交汇方向（§3.4），本规则产物不再生任何 `??` 节点。
4. 本规则是 C23 冻结的族 `TempLifter`（[C23 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L291-L300)）的**首个重度消费者**；除段临时声明外，还需「锚点语句替换」（void/if 形态与结果临时块形态），该能力以只增不改方式扩在 `NullConditional/` 目录内（§4.4）。
5. pass 默认关跟随 C23 锚点（opt-in）；满足 §8 门槛后随族评审翻默认开；Java 侧 S1–S6 分支本期**不删不改**，作为直连单文件路径安全网。

### 1.3 与 E03 的分工表

| 维度 | E03 `NullCoalesceRewriter`（已实现，链上更早） | C35 `NullConditional`（本特性，族规则） |
|---|---|---|
| 语法面 | `CoalesceExpression`（`??`）、`CoalesceAssignmentExpression`（`??=`） | `ConditionalAccessExpression`、`MemberBindingExpression`、`ElementBindingExpression` |
| 产物形态 | 临时 + `t != null ? t : r` 三元；`??=` → if-赋值（[NullCoalesceSyntaxRewriter.cs:44-218](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L44-L218)） | 段临时 + 嵌套 null 三元 / if 语句；lifted `T?` 终端 |
| 临时机制 | 自有两遍：Pass1 收 `PendingDeclaration`，Pass2 [NullCoalesceStatementLifter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs#L11-L38) 锚点前插 | 消费 C23 族 `Shared/TempLifter`（形态复刻 E03，见 [C23 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L291-L300)）；另加语句替换能力 |
| 交接 | 先跑；`a?.b ?? c` 被提为 `var ___t = a?.b; ___t != null ? ___t : c`，条件访问节点**原样保留一次** | 后跑；把 E03 临时初值中的 CA 单落点归一成段临时三元，接收者仍只求值一次（§3.4 NC-6） |
| 命名 | `___tN` | 族前缀 `__ed_`（C23 冻结），C35 子前缀 `__ed_s`（段）/`__ed_r`（结果） |

### 1.4 硬不变式（测试以探针行为断言，非文本断言）

- **INV-1 单次求值**：链上每个接收者表达式（含方法调用、属性 getter、索引接收者）在产物中恰好出现一次求值；计数探针（§7.1）在 null/非 null 两路径下均严格为 1。
- **INV-2 null 短路**：任一 `?.`/`?[]` 段接收者为 null 时，其后所有段成员访问、方法实参、索引运算元零求值；短路顺序与 C# 语义逐格一致。
- **INV-3 lifted 类型**：终端成员/索引/调用的静态类型为非可空值类型 `T` 时，整个条件表达式结果类型为 `T?`（IOperation/`GetTypeInfo` 取真相，不猜）；引用类型终端保持原类型；void 终端仅允许语句形态。
- **INV-4 段序确定**：同锚点多临时按「段 0 接收者 → 守卫 → 段 1 接收者 …」稳定排序，与 E03/C23/C31 共用同一序号空间（文件上下文发号）。
- **INV-5 幂等**：产物零条件访问语法节点（豁免区除外），二次运行零 diff、Changed=false。

### 1.5 非目标

- 不翻转 `expression-desugar` stub、不新增 pass id、不改链序（C23 锚点职责）；C23 未合入前本特性不可实施。
- 不重写 `??`/`??=`（E03 所有权；本规则只消费其产物）；不做 lifted 运算符（C22 所有权，[C22 §1.4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c22-nullable-value-design.md#L82-L82) 明确 `?.`/`?[]` 归 C35）；不做 `^`/`..`（C31）与 `?.` 的联合 lowering 算法（只保证组合可工作，§11）。
- 不模拟表达式树 lowering（`?.` 本就不能进表达式树，基线编译错误，仅防御性跳过）；不改 Java 侧转换器、compat、TypeMappings。
- M1–M3 不做跨语句实参顺序的语句分裂优化（Q2）。

## 2. 关键设计决策表

| 决策点 | 结论 | 备注/证据 |
|---|---|---|
| 总体架构 | 语义驱动**逐文件**族规则：C23 族入口统一 dirty 检查 → 文件上下文 → 规则管线中 C35 `Analyze`（链段规划）+ `Rewrite`（自底向上，值形态登记 `PendingTemp`、语句形态直接替换锚点语句）→ 族 `TempLifter.Apply` 一次 → 格式化 → residual 闸门；复刻 E03 两遍范式但复用族基建 | 不自行插文件/格式化；规则接口按 [C23 §4.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L249-L259) 冻结契约，只增不改 |
| 判定真相源 | 一律 SemanticModel/IOperation：`GetOperation(ca)` 的 `IConditionalAccessOperation`/`IConditionalAccessInstanceOperation` 取段接收者与终端类型；`GetTypeInfo(node).Type` 取 lifted 结果类型；`GetSymbolInfo(binding)` 取成员/方法/索引器/事件符号 | 禁止按标识符文本猜成员；S2 四处 owner 回溯的根因就是缺统一段模型。Roslyn 4.12（[Core.csproj](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj)）IOperation 外形由 M0 最小探针核实（同 C23 纪律） |
| **值上下文 → 临时 + 三元（NC-VALUE）** | 规范两形状：① 单段且接收者在**内联白名单**（局部/参数/`this`/字段，即 `IFieldSymbol`/`ILocalSymbol`/`IParameterSymbol`，不含属性）→ 零声明内联 `a != null ? a.B : null`；② 其余 → 段临时前置 + 守卫内对段临时做成员访问，长链用「段临时条件预取 + 嵌套三元」（§4.3 NC-2） | 白名单内重复的是简单名读，无用户副作用；白名单外宁可多临时不可双求值（INV-1） |
| 值类型终端 → `Nullable<T>` | 终端绑定符号返回非可空值类型 `T` 时，结果临时/三元类型显式发 `T?`（类型名取 `ITypeSymbol.ToDisplayString()`），真分支按需加 `(T?)` 保形 cast、null 分支 `(T?)null`；终端本身已 `T?`/引用类型则不 cast | 与 C22 lifted 形态同构；cast 只作用于条件访问终端，后续 lifted 运算由 C22 守卫规则消费（§11） |
| **void/语句上下文 → if（NC-VOID）** | 最外层 CA 是 `ExpressionStatementSyntax` 的表达式且终端为 void（void 方法/void 委托 Invoke）→ 整句替换为「段临时声明 + 嵌套 if 块」，实参写在最深守卫块内；值返回的调用被语句丢弃时同形（结果不取） | 现状 S3 已在 Java 侧做同构 if；C# 侧先归一，Java 侧只见普通 if/调用。表达式体 void 成员 M1 报 CS2J0558，M2 收编（arrow→block） |
| **链式逐段临时变量与短路顺序** | `ChainSegmentAnalyzer` 以**最外层 CA 为一个计划**整体解析段序列（不逐 CA 独立改写——内层 CA 的前导 `MemberBinding` 绑定的是外层占位接收者，独立改写无法绑定）；段临时按序声明、每段一个 null 守卫；后段接收者表达式只出现在上一段守卫为真的位置 | 嵌套 CA 在语法上表现为 WhenNotNull 内的 CA 与 `MemberBindingExpression` 前导的 `MemberAccessExpression`；M0 探针核实节点外形后写规划器 |
| 长链值形态（多段） | 「条件预取 + 嵌套三元」：`T1? __ed_s1 = null; if (__ed_s0 != null) __ed_s1 = __ed_s0.b.c; … __ed_s0 != null ? (__ed_s1 != null ? __ed_s1.d() : null) : null`；结果消费点无副作用、只引用临时 | 预取 if 保证每个非平凡接收者求值一次且被守卫；三元只比较临时。锚点语句被替换为「声明 + 预取 + 原句（含三元）」的块（§4.4） |
| **委托 `?.Invoke()`（订阅列表 null 语义）** | 与普通条件方法调用同规则：接收者段临时即订阅列表快照（`var __ed_s0 = Changed; if (__ed_s0 != null) __ed_s0.Invoke(args);`），天然复刻编译器「拷贝调用列表再判空」的线程安全语义；事件符号（`IEventSymbol`）与委托字段/属性同形，不特殊发 fire 方法（那是 Java 侧 S3 的职责） | 实参在守卫块内，null 时零求值（INV-2）；`?.BeginInvoke`/自定义方法按普通调用处理；`MethodInfo?.Invoke` 反射特判归主转换器，C# 形态普通调用 |
| **`?[]` 与索引实参单次求值** | `arr?[i]` → 段临时 + `__ed_s0 != null ? __ed_s0[i] : null`；索引运算元在真分支内出现一次，故「非 null 才求值、且只求值一次」自然成立；多维参数、自定义索引器、`ref/out` 实参位按绑定符号透传，不发明语义 | 赋值目标 `arr?[i] = v` 进 NC-ASSIGN；`?[^n]` 见与 C31 交界（§11）：本规则只剥条件外壳，hat 留给 C31 |
| **`?.=` 条件赋值** | `a?.b = v` / `a?.M(...) = ` 不合法形态外，合法目标（成员/索引/`+=`/`-=`/`++`/`--`）→ `var __ed_s0 = a; if (__ed_s0 != null) __ed_s0.b = v;`（复合/自增同形，写在守卫块内，目标只读一次） | 接收者为 `Nullable<Struct>` 的**值类型目标**：禁止发 `.Value.X =`（CS1612 不可编译）；record struct 形态移交 make-readonly 既有 MS-5 路径（CS2J0562 登记保留节点），其余 CS2J0561 Error（§4.3 NC-7、§11） |
| **与 `??`/`??=` 组合的固定点交汇** | 唯一方向：E03 先跑（链序事实），其产物中 `??` 左操作数被提为**单个**临时初值，CA 在初值中只出现一次——C35 对该单落点做标准 NC-VALUE，INV-1 不破；C35 产物不含任何 `??` 节点，故两语法面不互相再触发，无需跨 pass 固定点（§3.4 终止性论证） | `a?.b ?? c` 组合样例见 §3.4；`??=` 左值含 CA 是 E03 现状双求值短板的放大点，结论与 E03 加一行 guard 的对接建议见 §3.4/§11 |
| ref/ref readonly 返回条件访问 | `ref var x = ref a?.b;` 在 C# 基线即编译错误（ref 上下文不允许 null 条件）；本规则遇 ref/out/`RefExpression` 祖先位的 CA 防御性报 CS2J0560，不产出半成品 | 不应可达；可达即说明基线形态异常，显式失败优于猜测 |
| 表达式树 | `Expression<Func<...>>` lambda 内的 `?.`/`?[]` 本就被 C# 编译器拒绝；经共享 `IsInExpressionTree` 祖先判定命中则 CS2J0565 Info 跳过并与 residual 配对（catalog §4 风险 8 共享口径） | 不发明编译器规范形；与 C23/C26/C45 共用同一判定工具 |
| **不可提升上下文** | 无语句锚点位置（实例/静态字段初始化器、属性初始化器、特性实参、默认参数值、常量上下文、查询子句表达式内）仅允许**零声明**内联形态（接收者白名单 + 实参白名单）；否则 CS2J0557 Error，消息给出手工改写建议 | 与 E03「Cannot insert temp variable」Warning+skip 不同，本规则默认 Error（消灭静默 skip）；非 strict 降 Warning 但保留节点并计入 residual 配对 |
| `dynamic`/error type 接收者 | 段接收者或终端类型不可判定（`IErrorTypeSymbol`/dynamic 绑定且无法取得静态段类型）→ CS2J0559 Error，不猜测、不内联 | dynamic 上的 `?.` 语义为运行期绑定，无法静态保证形态 |
| 幂等 | 产物按语法种类复扫零 CA/MemberBinding(条件)/ElementBinding；二次运行 Changed=false | residual 闸门 CS2J0566，不按文本 `?.` 计数（避免误伤注释/字符串） |

## 3. 链位与族契约

### 3.1 所属：`expression-desugar` 族 `NullConditional/` 规则，不翻转、不另立 pass

- 物理落点 `src/CSharpToJava.Core/ExpressionDesugar/NullConditional/`（C23 锚点预留目录，[C23 §4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L199-L228)）；C23 未合入前该目录不存在，本特性分支基于「C23 分支合入后」的树实施（§12）。
- 注册表零结构改动：不动 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、不动描述符 RunAfter/Stage/目录名；落地时仅把 C23 已翻转的 Implemented 描述符 catalogItems 由 `"C23"` 加宽为 `"C23, C35"`（[C23 §3.4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L193-L197) 约定的同一描述符加宽）。
- 规则接入三处追加式热文件：`ExpressionDesugarRuleSet.cs` +1 行（NullConditional 规则）、`ExpressionDesugarOptions.cs` +1 个 nullable 子选项属性、partial 统计文件落在 `NullConditional/` 本目录；禁止修改 C23 既有规则行（§11 热文件解法）。
- 依赖方向：**硬依赖 C23 锚点**（共享 Analyzer/TempLifter/Options/Statistics/Diagnostic/规则接口与适配器）；E03 是链序上的前驱数据关系而非代码依赖（不引用 `NullCoalesceRewriter` 类型）。

### 3.2 ★与 E03 的顺序结论：不反向挂 E03，以注册表现状消费 E03 产物

catalog C35 条目建议「并入 E03 成为 null 糖归一通道」并要求论证去糖顺序。结论：**逻辑上是一条 null 糖归一通道，物理上保持两个相邻 pass、维持注册表现状（E03 → expression-desugar），不把 C35 挂进 E03**。理由：

1. **锚点已冻结**：C23 设计已确认 `expression-desugar` RunAfter=`null-coalesce`、物理插在 null-coalesce 与 enum 之间（[C23 §3.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L164-L172)），码段 CS2J0550-0569、目录 `NullConditional/`、族 `TempLifter` 全部按此拓扑预留；反向挂 E03 意味着推翻锚点、在 E03 内重长链段分析与族 Options，破坏「一个 expression-desugar pass + 多规则族」约定。
2. **两遍临时提升可直接复用对接，无需同进程**：E03 的 [PendingDeclaration](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L27-L30) + [StatementLifter](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs#L21-L38) 形态已被 C23 族 `TempLifter` 原样泛化（PendingTemp/Collect/Apply + 文件上下文发号，[C23 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L291-L300)）；C35 作为该基建首个重度消费者即完成事实上的统一。E03 自有 lifter 的后续收敛（上收共享组件）已在 [C23 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L299-L299) 与 [C31 Q3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L337-L337) 登记为独立后续项，本特性不动 E03 代码（唯二例外建议见 §3.4 末与 §11，均为可选小补丁）。
3. **语义论证（E03 先跑不破坏 C35 正确性）**：
   - `??`：E03 把左操作数整体提入临时初值（[L75-96](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L75-L96)），CA 在其中**恰好一个落点**；C35 再对该落点做段规划，接收者求值次数不变（§3.4 NC-6 探针验证）。
   - `??=`：E03 对成员/索引左值的现状形态是「`var ___t = left; if (___t == null) { left = right; }`」（[L177-218](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L177-L218)），`left` 在声明与赋值中各出现一次——对普通成员目标本就存在接收者重复求值的近似；若 `left` 含 CA，C35 事后会分别改写两处而产生两个段计划。结论：该交叉形态不由链序兜，而由 §3.4 的 E03 前置 guard 建议 + CS2J0563 防御码闭合。
4. **无环与终止性**：两族语法Kind 不相交（`CoalesceExpression/CoalesceAssignmentExpression` vs `ConditionalAccessExpression/MemberBindingExpression/ElementBindingExpression`）；E03 产物不引入任何条件访问节点，C35 产物（三元/if/普通成员访问/调用/索引）不引入任何 `??` 节点——跨 pass 不存在互相再触发，单趟即终止，无环、无需固定点迭代。

### 3.3 pass 内多趟方案：规则单趟 + 链整体单趟 visit，不做规则级固定点

- C35 规则在 `ExpressionDesugarRuleSet` 中**单趟**执行：`Analyze` 对每个链根产一个 `ChainPlan`，`Rewrite` 自底向上访问但只在**链根**整体替换（嵌套 CA 标记为已规划、跳过），因此任意深度嵌套链在一次 visit 内闭合，替换结果不含 CA 节点，不需要第二趟。
- 族规则间（C23 Interpolation ↔ C35 NullConditional）亦无需固定点：C23 洞表达式零复制（[C23 §2.2 副作用行](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L95-L95)），其产物 `string.Concat/Format(...)` 不含 `?.`；C35 产物不含插值节点。建议 RuleSet 中 NullConditional 排在 Interpolation **之后**（追加行自然在其后）：插值先归一为 BCL 调用，C35 再处理调用实参内的 CA，语义与「C23 先跑」的链位事实一致；两种顺序经分析等价（副作用顺序边界见 §10 R5 与 Q2），以追加顺序为准，不调整 C23 行。
- 终止性证明要素（写进单测）：每次替换严格减少 CA/ElementBinding 节点数（n→0），段临时初值与 if/三元内只含普通表达式，故 visit + 一次 `TempLifter.Apply` 后语法目标节点归零。

### 3.4 组合形态交汇样例（E03 产物 → C35）

```csharp
// 输入
var x = a?.b ?? c;

// E03（已实现 pass，先跑）产物：
var ___t1 = a?.b;
var x = ___t1 != null ? ___t1 : c;

// C35（本规则，后跑）产物：
var __ed_s0 = a;
var ___t1 = __ed_s0 != null ? __ed_s0.b : null;   // CA 单落点被归一；接收者 1 次
var x = ___t1 != null ? ___t1 : c;
```

- `??` 右操作数含 `?.`（`c ?? a?.b`）同理：E03 提右操作数进三元，C35 归一其中单落点。
- `??=` 左值含 CA（`a?.b ??= v`，若基线可编译）：**建议**在 E03 侧增加一行前置 guard——`VisitAssignmentExpression`/`RewriteCoalesceAssignStatement` 发现 `Left` 子树含 `ConditionalAccessExpressionSyntax` 时跳过改写并计 `SkippedUnsupported`（与现有无锚点 skip 同形），使原始节点留给 C35 归一成「段临时 + `if (__ed_s0 != null && __ed_s0.b == null) __ed_s0.b = v;`」。该建议属 E03 的小补丁（独立 PR、其既有测试守门）；本特性不对其形成硬依赖：C35 对 E03 已产出的「同形 CA 双落点」模式以 CS2J0563 Error 显式拒绝（形状匹配：声明初值与 if 赋值内 CA 结构同构），不静默生成双求值码。

### 3.5 不可提升位置清单（无语句锚点）

实例/静态字段与属性初始化器、特性实参、默认参数值、`const`/枚举初始值、`switch` 常量 case、查询表达式子句内、表达式树 lambda（基线非法，防御）、未被语句承载的表达式体成员（M1 报错）。处置矩阵：接收者与全部实参均在简单白名单 → 允许内联零声明；否则 CS2J0557/0558。

## 4. Core 组件设计

目录（全部为 C23 锚点目录下的新增文件，不改 C23 文件逻辑，接口只增不改）：

```
src/CSharpToJava.Core/ExpressionDesugar/NullConditional/
  NullConditionalRule.cs          # IExpressionDesugarRule 实现：TriggerSyntax=ConditionalAccessExpression；
                                  #   Analyze 建 ChainPlan[]，CreateRewriter 返回 NullConditionalRewriter
  ChainSegmentAnalyzer.cs         # 最外层 CA → ChainPlan（段序列、终端种类、值/void 上下文、锚点语句、
                                  #   lifted 结果类型 ITypeSymbol、内联白名单判定、不可提升判定）
  NullConditionalModels.cs        # ChainPlan / ChainSegment / TerminalKind(Member/Call/Index/Invoke/Assign) /
                                  #   LoweringShape(Inline/TempTernary/GuardedBlock/StatementIf) 记录
  NullConditionalEmitter.cs       # 纯函数渲染：段临时声明、守卫三元、嵌套 if 块、条件赋值块、(T?) 保形 cast
  NullConditionalRewriter.cs      # CSharpSyntaxRewriter：链根整体替换；值形态向共享 TempLifter 登记 PendingTemp；
                                  #   void/长链块形态经 PendingAnchorReplacement 替换锚点语句（§4.4）
  NullConditionalSyntaxFacts.cs   # 内联白名单符号集、表达式树/不可提升位置判定（调共享 Analyzer）、
                                  #   __ed_s/__ed_r 命名与碰撞避让（复用 ExpressionSyntaxFacts）
  PendingAnchorReplacement.cs     # 【只增能力】锚点语句 → 替换语句/块 的登记表 + Apply（与 TempLifter 同趟）
  NullConditionalStatistics.cs    # partial 族统计（§6）
```

### 4.1 ChainSegmentAnalyzer

- 输入：最外层 `ConditionalAccessExpressionSyntax`（链根判据：祖先链上不存在「以本节点为 WhenNotNull 组成部分的外层 CA」；实现上在 rewriter 自底向上访问时由「父段是否已认领」标记判定，M0 探针锁定精确语法外形）+ 文件上下文。
- 输出 `ChainPlan`：
  - `Segments`：每段一个 `ChainSegment`，记录原始 CA 的 `Expression`（去掉前导绑定后的接收者表达式）、段绑定种类（`MemberBinding` 起头的成员链 / `ElementBinding`）、绑定符号（`ISymbol`）、段非空时静态类型（`GetTypeInfo`/IOperation）；
  - `Terminal`：WhenNotNull 末端种类——成员取值 / 调用（方法符号、实参表）/ 索引（实参表）/ Invoke / 赋值目标（含 `=`/复合/自增）；
  - `ResultType`：链整体类型（`GetTypeInfo(chainRoot).Type`）——值类型终端为 `Nullable<T>`，直接作为结果临时声明类型与 cast 真相源；
  - `ContextKind`：Value / VoidStatement / AssignmentTarget；
  - `AnchorStatement` 与 `Hoistable`：最近外围 block 直接语句（复用共享锚点搜索）；无锚点 → 仅当全白名单允许 Inline，否则给 CS2J0557/0558；
  - `Shape` 选择：单段白名单→Inline；单段非白名单值上下文→TempTernary；多段值上下文→GuardedBlock（条件预取+嵌套三元）；void 语句/条件赋值→StatementIf。
- 只读不改；dynamic/error 接收者不给 plan，转 CS2J0559。

### 4.2 对 C23 `TempLifter` 与 E03 lifter 的统一复用方案

- 段临时一律走 C23 冻结的 `PendingTemp(Declaration, AnchorStatement, Order, Owner)` + `Collect/Apply`：`Owner=NullConditional`，序号由文件上下文统一发号，与同句中的 C23/C24/C31 风格临时（未来）共享确定序（[C23 §4.5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L291-L300)）。
- 机制等价于 E03 两遍：Pass1（本规则 rewriter）只登记/替换，`TempLifter.Apply` 在族入口统一执行一次；`VisitBlock` 锚点前插声明的算法与 [NullCoalesceStatementLifter.cs:21-38](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceStatementLifter.cs#L21-L38) 同构（内嵌块作用域天然正确）。
- 命名：段接收者 `__ed_s<N>`、链结果 `__ed_r<N>`；碰撞避让复用 `ExpressionSyntaxFacts`；不新增命名前缀（保持 C23 `__ed_` 统一，C08 无需特殊协调）。
- E03 自有 lifter 本期不迁移（与 [C31 §4.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L199-L199) 同口径），其收敛为独立后续项；本特性产出后，表达式族三条线（E03、index-range、expression-desugar）的临时提升形态已全部同构，收敛为机械工作。

### 4.3 规则全集 NC-1 ~ NC-12（before/after）

- **NC-1 单段条件成员（值，内联白名单）**
  ```csharp
  // before: var y = a?.B;（a 为局部/参数/字段）
  var y = a != null ? a.B : null;                 // 零声明；a 为简单名读，无用户副作用
  ```
- **NC-2 长链条件成员/调用（多段，GuardedBlock）**
  ```csharp
  // before: var x = a?.b.c?.d();
  var __ed_s0 = a;
  C? __ed_s1 = null;
  if (__ed_s0 != null) __ed_s1 = __ed_s0.b.c;    // 段 1 接收者只在此求值一次，且被守卫
  var x = __ed_s0 != null
      ? (__ed_s1 != null ? __ed_s1.d() : null)
      : null;                                     // 消费点只读临时；d 的实参在真分支内求值
  ```
  单段非白名单（`Get()?.B`）走 TempTernary：`var __ed_s0 = Get(); var x = __ed_s0 != null ? __ed_s0.B : null;`。
- **NC-3 条件方法调用（值/void 两上下文）**
  ```csharp
  // before: var s = a?.M(F(), G());              → TempTernary，实参各一次且仅非 null 时求值
  var __ed_s0 = a;
  var s = __ed_s0 != null ? __ed_s0.M(F(), G()) : null;
  // before: a?.M(F());                            → StatementIf
  var __ed_s0 = a;
  if (__ed_s0 != null) { __ed_s0.M(F()); }
  ```
- **NC-4 条件索引 `?[]`**
  ```csharp
  // before: var z = arr?[Next()];
  var __ed_s0 = arr;
  var z = __ed_s0 != null ? __ed_s0[Next()] : null;   // Next 仅非 null 时求值一次
  // 多维/自定义索引器按 IPropertySymbol/索引器原样透传，不改写实参语义
  ```
- **NC-5 委托/事件 `?.Invoke()`**
  ```csharp
  // before: Changed?.Invoke(x, Next());
  var __ed_s0 = Changed;                          // 订阅列表快照一次（线程安全语义）
  if (__ed_s0 != null) { __ed_s0.Invoke(x, Next()); }
  // 值返回委托：var r = d?.Invoke(1); → __ed_s0 != null ? __ed_s0.Invoke(1) : null
  ```
- **NC-6 与 `??` 组合（消费 E03 产物）**：样例与不变式见 §3.4；规则对「E03 临时初值中的 CA 单落点」直接套用 NC-1/2/4，触发 CS2J0567 Info 计数；探针同时断言 E03 临时与 C35 段临时的求值次数。
- **NC-7 条件赋值目标**

  > **归属裁决（2026-09-30）**：NC-7 从本规则移出、与 catalog **C51** 对齐——赋值目标（含复合/自增形态）是 C# 14 新语法面，由同一目录 `ExpressionDesugar/NullConditional/` 下的 **`Assignment/` 子规则**承接（见 [C51 设计](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c51-null-cond-assignment-design.md)）；**不新增 pass、catalog 编号 C51 单独保留**。下列 NC-7 原文仅作历史设计依据保留，实施以 C51 文档为准。
  ```csharp
  // before: a?.b = v;                  → var __ed_s0 = a; if (__ed_s0 != null) __ed_s0.b = v;
  // before: arr?[i] = v;              → var __ed_s0 = arr; if (__ed_s0 != null) __ed_s0[ i ] = v;
  // before: a?.b += 1; / a?.b++;      → 守卫块内做复合/自增（M2；目标在块内求值一次）
  ```
  接收者静态类型为 `Nullable<TStruct>` 的成员目标：record struct 可写形态保持节点 + CS2J0562（make-readonly MS-5 既收编，链上该 pass 在 expression-desugar 之后，仍看得见原 CA）；其余值类型目标 CS2J0561 Error（绝不发 `p.Value.X =` 式 CS1612 不可编译码）。
- **NC-8 void/语句上下文嵌套链**：多段 void 链产嵌套 if 块（段临时在外层块顶、内层声明随守卫嵌套），等价替代现状 Java 侧 [L352-355](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L352-L355) 的递归重拼；表达式体 void 成员 M1 CS2J0558、M2 改写为 block body。
- **NC-9 lifted 值类型终端**
  ```csharp
  // before: int? n = s?.Length;（Length : int）
  var __ed_s0 = s;
  int? n = __ed_s0 != null ? (int?)__ed_s0.Length : (int?)null;
  // bool?/DateTime?/枚举? 同形；终端已 T? 或引用类型时不加 cast
  ```
  cast 与结果类型一律取自 `GetTypeInfo(chainRoot).Type`；lifted 后的后续运算（`(s?.Length) + 1`）不在本规则展开，交 C22（链序上 C22 在 numeric-boundary，晚于本规则）。
- **NC-10 嵌套与混合**：`a?.M()?.P?[i]`、`?[]` 后再 `?.`、链内 LINQ 式调用（`c?.Where(...)` 保持普通调用实参原样）、条件访问作 `?.Invoke` 实参——同一 ChainPlan 段序列覆盖；三元/`??`/插值洞/集合初始化器内的 CA 均作为值形态落点处理。
- **NC-11 报错/跳过/移交**：CS2J0557（不可提升+非白名单）、CS2J0558（无锚点 void）、CS2J0559（dynamic/error）、CS2J0560（ref 上下文）、CS2J0561（值类型条件赋值）、CS2J0562（移交 E11）、CS2J0563（E03 双落点防御）、CS2J0565（表达式树）；每条 Error 不改写文件（dirty-no-rewrite 语义同 C23 入口约定）。
- **NC-12 residual 自检**：族入口 residual 闸门按语法种类统计 CA / ElementBinding / 条件性 MemberBinding（MemberBinding 仅出现在 CA 内，按祖先配对）；与 0562/0565 登记豁免配对后仍有残余 → CS2J0566（strict Error）。

### 4.4 PendingAnchorReplacement（只增能力，归 C35 目录）

族 `TempLifter` 只解决「锚点前插声明」；NC-VOID、GuardedBlock 与条件赋值需要**替换锚点语句本身**（如把 `return a?.b.c?.d();` 替换为「段声明 + 预取 if + `return __ed_r;`」的块；把 `a?.M();` 替换为 if 块）。设计：

- `record PendingAnchorReplacement(StatementSyntax Anchor, Func<StatementSyntax, StatementSyntax> Replace)`；规则在 Pass1 登记，与 PendingTemp 同集合、同 Owner/Order 管理。
- Apply 顺序在 `TempLifter.Apply` 同一轮 block 遍历中：先做锚点替换（替换产物已内联本链全部声明），再做其余 PendingTemp 前插；同一锚点被 C35 替换后，其他规则（C23 无临时、C24 未来）的 PendingTemp 以替换后语句为锚点——锚点相等性用「原始语句对象引用」配对，替换映射对 lifter 可见。
- 能力放在 `NullConditional/` 本目录、不进 `Shared/`（C23 锚点零临时不需要它）；M2 复盘后若 C24 初始化器也需要，再经族评审上收（只增不改）。
- 可替换锚点白名单（M1–M2）：`ExpressionStatementSyntax`、`ReturnStatementSyntax`、`LocalDeclarationStatementSyntax`（初值含链）、`ThrowExpressionSyntax` 承载语句；其余（for/while 增量子句、using 声明、switch arm 表达式等）M1 报 CS2J0557 并指路手工改写，M3 按画像扩面。

## 5. CLI 集成

零注册表结构改动，全部增量挂在 C23 已落地的适配器上：

1. **不新增适配器文件、不动 PassChainFactory/PassCatalog 结构**：[ExpressionDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ExpressionDesugarPassAdapter.cs)（C23 新增）已持有族 Options/统计/诊断；C35 子选项经 `ExpressionDesugarOptions.NullConditional`（组合属性，[C23 §4.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L238-L241)）透传，规则在 `ExpressionDesugarRuleSet` 生效，适配器摘要行追加 C35 计数即可（小改，归 C35 PR）。
2. 描述符仅 catalogItems 加宽 `"C23" → "C23, C35"`；`--list-passes` 自动反映；中间目录、指纹令牌（注册表自动 `pass:expression-desugar=on/off`）、clean 字节复制、BOM 保持全部沿用族机制。
3. **无支撑库**：产物只含 C# 原生三元/if/成员访问/索引/调用与 `(T?)` cast；不注入工程、不进支撑库过滤、不改 TypeMappings/compat。
4. **统计汇入族**：C35 计数通过 partial `NullConditionalStatistics` 累加入族 `ExpressionDesugarStatistics`（族五字段 + `TempsLifted` 共享），适配器 `BuildChainSummaryLine` 增「NC sites/index/invoke/assign/void/lifted/skipped/residual」一段；诊断行前缀沿用 `[expression-desugar]`。
5. **fixture/指纹复用**：项目级 fixture 复用 C23 的 `SampleInterpolationSolution` 模式新建独立夹具（§7.2）；verify 默认 true/true、`DeleteBuildOutputsAfterRestore=true` 随族描述符；restore/build/test 失败钩子沿用族 CS2J0520。
6. 入口：仅通用 `--enable-pass expression-desugar` / `preprocess --pass expression-desugar`；不单开 verb、不做单文件模式（段类型判定依赖整编译 SemanticModel，理由同 C23/C31）。

## 6. 诊断策略

**第一原则（catalog §4 风险 12）**：无法等价显式化时显式报错，禁止 TODO/占位/静默 skip。S4 的 `TODO: ElementBindingExpression` 与 S2 各处 owner 链猜测兜底在本路径全部转为「正确归一」或「带码失败」；strict 关闭时 Error 降 Warning 但逐条可见、可计数、与 residual 配对。

码段 `CS2J0550–CS2J0569`（C23 §6 已为 C35 预留，只追加不交叉占用；MSBuild 风格）：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J0550 | Info | 条件访问站点已显式化（NC-1/2/3 总数，计数用） |
| CS2J0551 | Info | 条件索引 `?[]` 站点已显式化（NC-4） |
| CS2J0552 | Info | void/语句上下文改写为 if 形态（NC-8） |
| CS2J0553 | Info | 委托/事件 `?.Invoke()` 形态（NC-5） |
| CS2J0554 | Info | lifted 值类型终端 `T?` 形态（NC-9，分子计 cast 数） |
| CS2J0555 | Info | 条件赋值/复合/自增目标改写（NC-7 非值类型部分） |
| CS2J0557 | Error（strict）/Warning | 不可提升上下文（字段/属性初始化器、特性实参、默认参数等）且接收者/实参非内联白名单 |
| CS2J0558 | Error/Warning | 无语句锚点的 void 条件调用（M1：表达式体 void 成员等；M2 收编后窄化） |
| CS2J0559 | Error | 段接收者/终端类型不可判定（dynamic、error type、跨编译元数据缺失），不猜测 |
| CS2J0560 | Error | ref/out/ref readonly 上下文中的条件访问（基线非法的防御镜像） |
| CS2J0561 | Error | `Nullable<TStruct>` 接收者的条件赋值目标且不属 make-readonly 覆盖域 |
| CS2J0562 | Info | record struct 条件赋值站点保留并移交 make-readonly（MS-5/RR051 既有路径），与 residual 配对 |
| CS2J0563 | Error | E03 `??=` 产物中条件访问双落点形状（理论上在 E03 guard 补丁后不可达；见 §3.4） |
| CS2J0564 | Error | WhenNotNull 出现未知终端形态/未知语法 kind——禁止静默兜底（镜像消灭 S5） |
| CS2J0565 | Info | 表达式树祖先内条件访问跳过（基线本不可编译，防御性配对） |
| CS2J0566 | Error（strict）/Warning | residual 闸门：输出残留未配对的 CA/ElementBinding/条件 MemberBinding |
| CS2J0567 | Info | 与 E03 `??` 产物组合站点（NC-6 计数） |
| CS2J0568 | Info | C51（NA-6）：`a?.b ??= c` 空条件合并赋值站点；守卫体内原 `??=` 降级为纯 `=` |
| CS2J0569 | Error | C51 安全网：原生非法/受损/未知赋值形态（++/-- CS1059、解构 CS0131、`=ref`、非成员/索引的 `??=` 目标、未知运算符/目标），dirty-no-rewrite |

restore/build/test 失败复用族公共 CS2J0520（Warning/Error），不另占码。

统计（partial）：`ConditionalAccessSites`、`ConditionalIndexSites`、`InvokeSites`（子计 `EventSites`/`DelegateSites`）、`VoidStatementSites`、`AssignmentTargetSites`、`LiftedValueTypeSites`、`CoalesceCompositionSites`、`InlinedSites`、`SegmentTempsLifted`、`AnchorReplacements`、`ChainsByLength: Dictionary<int,int>`、`SkippedByReason: Dictionary<code,int>`，加族五字段与 `TempsLifted`。

## 7. 测试计划

### 7.1 Core 单测（建议 `tests/CSharpToJava.ProjectTests/NullConditionalDesugarUnitTests.cs`，仿 NullCoalesceRewriterUnitTests 外形）

内联源码 → net10.0 `CSharpCompilation`（Roslyn 4.12）→ 族 `Preprocess` → 忽略空白文本断言 + 输出可编译断言 + 二次运行幂等断言。矩阵：

1. **基础成员**：`a?.B`、`this?.B`、`(await Get())?.B`（不含 await 改写，只测形态）、静态成员接收者；内联白名单 vs 段临时两形状；
2. **长链**：`a?.b.c?.d()`、三段链 `a?.b?.c?.d`、`?[]` 与 `?.` 交替 `a?[0]?.M()`、链尾再索引/再成员；逐段临时顺序断言；
3. **方法调用**：实参 0/1/多个、实参为三元/lambda/`out`/具名实参；泛型方法；扩展方法接收者保持；
4. **`?.Invoke`**：委托字段/属性/局部、`Func<int>` 有返回值、`Action` void、**字段式事件**触发、自定义委托类型；快照语义探针；
5. **`?[]`**：一维数组、`List<T>`/自定义索引器、多维（合法形态透传）、索引实参为方法调用；
6. **与 `??` 组合**：`a?.b ?? c`、`c ?? a?.b`、`arr?[0] ?? d`、嵌套 `a?.b ?? c?.d ?? e`——先以 E03 预处理产出作输入夹具（直接串两个 Core 入口），断言 INV-1/INV-2；
7. **void 上下文**：`a?.M();`、多段 void 嵌套 if、值返回调用被语句丢弃、事件 Invoke 语句、`if` 条件/循环体内锚点；
8. **条件赋值**：`a?.b = v`、`arr?[i] = v`、`a?.b += 1`、`a?.b++`/`++a?.b`（M2）、链式目标 `a?.b.c = v`；值类型接收者两分支（record struct → 0562 保留；普通 struct → 0561 Error）；
9. **lifted 值类型**：`s?.Length`（int?）、`a?.Count` 用于对象三元、bool?/DateTime?/enum?/自定义 struct 成员终端、终端已 `T?` 不双 cast、引用类型终端无 cast；
10. **嵌套/混合**：条件访问作插值洞（与 C23 组合）、初始化器元素、`??` 内、三元分支内、lambda 体内、`?.` 实参是另一个 `?.`；
11. **★副作用单次求值计数探针（行为断言，非文本）**：探针类型以静态计数器记录每次 getter/方法/索引器调用：
    - 接收者 `Get()?.B`、`GetArr()?[Next()]`、`a?.b.c?.d()` 在结果 null 与非 null 两盘下，逐接收者严格 1 次、被短路段 getter 0 次、实参 `Next()` 仅在全链非 null 时 1 次；
    - 条件赋值 `Get()?.B = v` 接收者 1 次；`?.Invoke` 参数在 null 订阅列表下 0 次；
    - 与 E03 串联后（NC-6 夹具）计数仍严格不变；
    - 内联白名单形状声明数=0；非白名单形状每个非平凡段恰好一个 `__ed_s` 临时；
12. **不可提升/报错**：字段初始化器内白名单通过/非白名单 CS2J0557；特性实参、默认参数；表达式树夹具 CS2J0565；dynamic CS2J0559；构造的 ref 位 CS2J0560；E03 双落点形状 CS2J0563；未知 WhenNotNull 合成节点 CS2J0564；strict 开/关两态；行号/路径附加正确；
13. **表达式树 skip 配对**：residual 不因 0565/0562 误报；
14. **幂等/clean**：无 `?.`/`?[]` 文件 Changed=false 字节一致（含 BOM）；产物二次 Preprocess 零 diff；
15. **.NET vs Java 行为对照预言机（.NET 半区）**：同一组探针用例在「原生 C#」与「预处理后中间树」上各执行一次，逐值相等 + 计数向量相等（null/非null/值类型 lifted/短路/事件无订阅者五盘）。

### 7.2 项目级 fixture `tests/SampleNullConditionalSolution/`

仿 SampleArraySolution：`SampleNullConditionalLib` + `SampleNullConditionalTests` + sln，`Content Include` 进 ProjectTests，net10.0。内容：跨文件类层次长链、字段式事件触发（无订阅/订阅中抛异常两盘）、索引器、`int?` lifted 终端、与插值/`??` 混用、record struct 条件赋值样本。断言：

- `result.Success`；中间树文本**不含** `?.`/`?[` 语法（豁免样本除外）、含 `__ed_s` 段临时；中间树 `dotnet build -c Release` 与 `dotnet test -c Release` 退出码均 0；
- **四象限组合**（expression-desugar × null-coalesce on/off）：E03 关时 `a?.b ?? c` 中 `??` 残留但 CA 已归一（residual 口径按规则独立计数），E03 开时全归一；目录/指纹互不干扰；
- 与 record-readonly/make-readonly 的组合样本（条件赋值移交路径）；报错夹具（不可提升上下文）strict 下 Success=false、不留半成品目录。

### 7.3 Java 半区与回归

- 仿 ArrayMethod e2e 增转换链路用例：预处理后中间树经完整转换跑 Java 测试，钉引用类型链、lifted `Integer/Boolean` 终端、事件 no-listener、`?[]` 四组运行时结果；
- 现有护栏：[ConditionalAccessMethodCasingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ConditionalAccessMethodCasingTests.cs)、[ConditionalAccessPropertyMappingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ConditionalAccessPropertyMappingTests.cs)、[DelegateInvokeMappingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/DelegateInvokeMappingTests.cs) 保持绿色（pass 默认关）；默认开门禁前把 [NullableTests.cs:25-31](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/NullableTests.cs#L25-L31) 的 TODO 锁定断言改为「预处理后正确 get 形态」，并登记直连路径下 ElementBinding TODO 的最终删除。

### 7.4 一致性 / CLI / 链序

- 不新增 pass 故 `PassStubCatalogTests` 计数**不变**；`ProjectPreprocessorsConsistentTests` 无需新增条目；仅当 catalogItems 加宽被 `--list-passes` 快照断言覆盖时同步期望文本；
- 族 CLI 测试（C23 的 ExpressionDesugarCLITests）扩用例：启用后 `?[`/`?.` 零残留、0557 失败退出非零且不留半成品目录、摘要行含 C35 段；
- residual 闸门配对逻辑单测（0562/0565 豁免、合成未配对节点 → 0566）。

### 7.5 residual「零条件访问」口径

默认开验收时，画像中每个 CA/ElementBinding 必须恰属于：已归一（0550-0555/0567）、显式登记保留（0562 移交/0565 表达式树）两类之一；任何无诊断残留触发 0566。按语法节点计数，不按 `?.` 文本（注释/字符串/`>=` 等不误伤）。

### 7.6 验证命令（实施期执行；本设计任务不执行 build/test/git 写操作）

```powershell
dotnet build -c Release CSharpToJavaConverter.slnx
dotnet test -c Release tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
dotnet test -c Release tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
dotnet test -c Release tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj
```

## 8. 默认开启验收门槛

本规则不独立翻 pass；随 `expression-desugar` 族门槛（[C23 §8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L412-L422)）并附加：

1. §7.1 矩阵全绿（尤其计数探针两盘）；fixture 与三测试工程 Release build/test 全绿；幂等零 diff；
2. residual 零「未解释条件访问」：画像中每处 `?.`/`?[]` 均有归一或 0562/0565 配对；CS2J0557/0559/0561 命中清单逐条评审；
3. .NET 半区与原生 C# 行为等价（单次求值、短路、lifted、事件快照四族探针）；Java 半区：引用链/lifted/事件/`?[]` e2e 全绿，S1 的基元默认值 false 分支与 lifted 三元在预处理路径上行为一致；
4. 四象限（expression-desugar × null-coalesce）+ 与 C31（`?[^n]`）、make-readonly（条件赋值）组合样本全绿；指纹/目录/落盘与族机制一致；
5. csharp-sdk 画像 opt-in 试跑一轮：站点数/形状分布/段临时规模/0557-0563 清单/基线 build/test 不退化；
6. Java 侧 S1–S5 特判簇（`TransformConditionalAccess`、`TransformWhenNotNull` owner 回溯四处、语句层 if 路径）在预处理链路上成为死代码并有下掉/保留安全网的跟进 PR 排期；S4 TODO 锁定测试同步改写；
7. C24/C36 未落地不阻塞：RuleSet 追加行与 Options 组合属性经评审不影响 C23 单规则运行。

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M1 单段链 + 骨架接线 + `??` 组合 | M0 Roslyn 探针（CA/MemberBinding/ElementBinding 嵌套外形、IConditionalAccessOperation 段类型、lifted 结果类型）；`NullConditional/` 全组件 + PendingAnchorReplacement；NC-1/3（值）/9/6（消费 E03 产物夹具）；CS2J0550/0554/0557/0559/0566；Core 单测主体 + 计数探针 | Core ~700 行 |
| M2 `?[]` / Invoke / 条件赋值 / void | NC-4/5/7/8；0551/0552/0553/0555/0558/0560/0561/0562/0563/0565；表达式体 void 成员收编；复合/自增目标；fixture 与四象限（E03）组合；Release build/test 全绿 | ~600 行 + fixture |
| M3 长链固定点 + E03/C31 组合 + Java 半区 | NC-2 GuardedBlock 全矩阵（段序/深层嵌套/混合 `?[]`）；NC-10/12；`?[^n]` 与 C31 联合用例（§11）；插值洞组合；Java 半区 e2e；画像试跑；CS2J0567/0568 收口；NullableTests TODO 断言改写排期 | ~400 行 |

M1–M3 全程随族 `DefaultEnabled=false`；每个里程碑自身 Release build/test 全绿，不留半残中间树；M0 不翻转任何注册表条目（沿用 C23 翻转后的 Implemented 描述符）。

## 10. 风险

| # | 风险（按严重度） | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **单次求值被破坏（最高风险）**：现状 S1 内联三元天然重复接收者；长链独立逐 CA 改写会重复非平凡段接收者（`__ed_s0.b.c` 两次） | [ControlFlowTransformer.cs:336](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L336)；E03 成员左值亦有重复近似 | 链根整体规划 + 段临时条件预取；★计数探针两盘断言（§7.1-11）；白名单外禁止内联；0563 拒绝 E03 双落点 |
| R2 | **null 短路顺序错误**：段临时若全部无条件预取，会提前求值后段 getter/实参，可观察行为改变（尤其 `?.Invoke(F())`） | INV-2 | 后段接收者放进上一段守卫内预取；方法/索引实参只出现在真分支；探针断言 null 盘下实参计数 0 |
| R3 | **lifted 类型错判**：值类型终端漏 `T?` 会让 null 分支不可编译或被 Java 拆箱 NPE；现状 Java 侧靠 ConvertedType 发基元默认值（S1）打补丁 | [ControlFlowTransformer.cs:322-335](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L322-L335)（默认值字面量在 [:339-350](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L339-L350)）；C22 边界 | 类型只取 IOperation/GetTypeInfo；结果显式 `T?` 临时 + `(T?)` cast；与 C22 组合单测（lifted 终端再参与运算） |
| R4 | **值类型条件赋值产出不可编译 C#**：`p.Value.X = 1` 触发 CS1612 | make-readonly MS-5 只覆盖 record struct 且依赖 CA 节点在位 | 分层：record struct 0562 移交；其余 0561 Error；禁止发明 `.Value` 赋值；组合 fixture 守门 |
| R5 | **固定点/求值顺序**：族规则顺序与「临时前置到锚点」会把复杂接收者的求值移到同语句兄弟副作用之前（如插值洞/实参列表中前序 `F()`）——E03 现状即同近似 | E03 anchor-before-statement；[C23 §2.2 副作用行](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L95-L95) | INV-1 次数为硬保证；跨兄弟顺序与 E03 保持 parity 并显式文档化；精确语句分裂列 Q2，M1–M3 不做；探针覆盖 `M(F(), Get()?.b, G())` |
| R6 | **临时变量爆炸与可读性**：长链每段 1 接收者 + 1 预取 + 结果临时，深链方法体膨胀 | GuardedBlock 形态 | 白名单内联零声明；临时名/块经 AdhocWorkspace 格式化；画像统计段长分布（ChainsByLength）；不合并不同段临时（合并会破坏短路） |
| R7 | **`?[^n]` 跨 pass 顺序陷阱**：C31 的索引运算元提升若走出三元真分支/if 块，会把 `n` 变成无条件求值 | [C31 Q5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L339-L339)、IR130 | 链序 C35 先于 C31（注册事实）；常量 hat 由 C31 折叠无临时；联合 fixture 钉「null 接收者时 `n` 不求值」；必要时向 C31 提「守卫块内锚点」增强需求（Q4） |
| R8 | **旁路消费者回归**：TupleToStruct 重建 `t?.ItemN`、LinqRewriter/RecordReadOnly/HIR 都访问 CA | S6 六处 | expression-desugar 链位在 E06/E11 之前：TupleToStruct 后续看到的是普通成员访问（联合用例 `t?.Item8`）；make-readonly 依赖的 CA 在 0562 路径保留；逐条 §11 列交界并配样本 |
| R9 | **无锚点上下文静默 skip 回潮**：E03 对无锚点仅 Warning+保留，若 C35 沿用则 S4 族静默延续 | E03 [L104-112](file:///d:/code/cs2j/src/CSharpToJava.Core/NullCoalesceRewriter/NullCoalesceSyntaxRewriter.cs#L104-L112) | 默认 Error（0557/0558）；非 strict 才降 Warning 且必须 residual 配对；闸门 0566 |
| R10 | **链耗时/指纹/双轨期**：新增语义规则与族共用一次遍历，边际成本低；默认关期两套条件访问路径 | catalog §4 风险 10/11 | 族级 dirty 检查（无 CA 文件字节复制）；指纹自动；默认关不改既有测试；§8 控制默认化 |
| R11 | **Roslyn API 外形偏差**：嵌套 CA 中内层前导 MemberBinding 的精确父结构、IConditionalAccessOperation 在 4.12 的外形可能与假设有差 | 同 C23 R14 教训 | M0 探针先行，规划器代码以探针结论为准 |

## 11. 并行协调

| 相邻工作 | 边界 | 接口约定/合并顺序 |
|---|---|---|
| **C23 interpolation-expand（锚点，硬前置）** | C35 只在 C23 落地后的 `ExpressionDesugar/` 内加目录与追加行；共享 Analyzer/TempLifter/Options/Statistics/Diagnostic/适配器；`PendingAnchorReplacement` 落本规则目录，不上收 Shared | 实施分支 base 上先合 C23（§12）；RuleSet/Options/统计/摘要行为追加式改动，落地者 rebase 取并集，禁改 C23 规则行；共享接口变更需全族评审 |
| **E03 null-coalesce（链上前驱）** | `??`/`??=` 归 E03；C35 只消费 E03 产物中的 CA 单落点（NC-6）；不引用 E03 类型 | 顺序结论 §3.2；可选 E03 小补丁（含 CA 的 `??=` 左值 skip + SkippedUnsupported）独立 PR、其测试守门；0563 为未打补丁时的防御；lifter 后续统一上收为共同技术债，不双向依赖 |
| **C31 index-range（`?[^n]` 交叉结论）** | C31 拥有 `^`/`..`；C35 拥有条件外壳。**边界结论（答 [C31 Q5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c31-index-range-design.md#L339-L339)）**：维持 C31 M1–M3 对 CA 祖先站点 IR130 指路 C35 不变（C35 未启用时的正确防线）；两 pass 都启用时，因链序 expression-desugar（null-coalesce 后）远早于 index-range（array-method 后），`arr?[^n]` 先被 C35 剥成 `__ed_s0 != null ? __ed_s0[^n] : null`，C31 看到的是普通三元内元素访问，走其常规 IR-1，**无需 C31 改算法**；联合要求仅一项：C31 提升 hat 运算元时不得移出守卫分支（R7/Q4），M3 联合 fixture 钉死 | residual 按语法种类天然分界；C35 关闭时 C31 IR130 仍有效，开关四象限独立成立 |
| **C22 nullable-value（lifted 交界）** | C35 管条件访问产 `T?`；C22 管 `T?` 上的 lifted 运算/解包（[C22 §1.4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c22-nullable-value-design.md#L82-L82)、§11 C35 行已约定「产 `T?` 后按一般规则处理」） | 链序 C35 早于 numeric-boundary；C35 产物的 `(int?)` 终端进入 C22 守卫规则无需感知 `?.`；`?.` 目标自增归本规则 NC-7（引用类型/登记路径），C22 CS2J5355 指路不重复 |
| **E11 record-readonly / E13 make-readonly** | 二者在链上晚于 expression-desugar，其 MS-5 条件赋值收编依赖原 CA 节点 | record struct 条件赋值 C35 保留 + CS2J0562 配对；副作用接收者 RR051 行为不变；联合 fixture 覆盖 `p?.X = 1/+= 1/++`；若未来 MS-5 扩面到普通 struct，同步收窄 0561 |
| **E06 tuple-to-struct** | 现有 `t?.Item8` 链重建（[TupleToStructPreprocessor.cs:650-673](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L650-L673)）发生在链更后段；C35 归一后其输入是普通成员访问，VisitConditionalAccess 在预处理路径不再触发 | 联合用例 `t?.Item8?.ToString()`；确认 tuple pass 对普通访问路径等价，无 RunAfter 边需求 |
| **C26 linq-channel / C45 closure-lift（表达式树 skip）** | `?.` 不进表达式树（基线非法）；共享 `IsInExpressionTree` 判定口径（C23 M1 内置最小实现，登记合并去向） | C35 直接消费共享 Analyzer；0565 配对；不替 C26 做 queryable 决策 |
| **C24 initializer / C36 collection-expr（同族后续）** | 集合初始化器元素与集合表达式内的 CA 在值上下文按 NC-10 处理；两族落地只加规则行 | 追加式热文件解法；PendingAnchorReplacement 若被 C24 需要再上收 Shared（族评审） |
| **C08 java-keywords（链尾）** | `__ed_s/__ed_r` 与产物标识符非 Java 保留字 | 无需协调；撞名由 C08 改名表统一覆盖 |
| **Java 侧转换器（同仓不同半区）** | S1–S5 本期不动；默认开后下掉 `TransformConditionalAccess`/owner 回溯/语句 if 路径，ElementBinding TODO 兜底同步删除 | §8 第 6 条排期；直连单文件入口保留安全网或移除二选一附测试 |
| **热文件追加式解法** | 本分支对共享文件仅三处追加（RuleSet 一行、Options 一属性、catalogItems 一字符串）+ 适配器摘要一小段；其余全在新目录 | 与 C24/C36 并行时 rebase 取并集；不修改既有规则行与 C23 断言；PassStubCatalogTests 计数不变 |

## 12. Worktree 分配（2026-10-04 更新 base；C35 与 C51 共用同一 worktree）

- 分支：`feature/preproc-c35-c51-null-conditional`
- 目录：`.worktrees/preproc-c35-c51-null-conditional`
- base：`e50d987470bde644137e07094d93fd1467c6ca9c`（`dev-3.x-26100408` 最新提交，2026-10-04 已校验存在）；C23 锚点（`ExpressionDesugar/` 族 Shared 基建、规则接口、码段与适配器）已在 base 内，**无需另合 C23 分支**；C35 先落地 `NullConditional/` 读侧规则，C51 随后在**同一 worktree、同一分支**的 `NullConditional/Assignment/` 子目录内接续实施，实现一部分提交一部分

```powershell
git worktree add .worktrees/preproc-c35-c51-null-conditional -b feature/preproc-c35-c51-null-conditional e50d987470bde644137e07094d93fd1467c6ca9c
```

实施全部在该 worktree 内进行（C23 已在 base 内，直接开工）；本设计文档的 base/worktree 更新产出于主工作区。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令的 base 于 2026-10-04 更新为 `dev-3.x-26100408` 最新提交 `e50d9874`（C23 锚点已在 base 内，无需再 rebase/合分支；C35 与 C51 共用同一 worktree，见 §12），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（按原题号）：**

- **Q1 链序与 E03 归一**：维持注册表现状（C35 作 expression-desugar 族规则消费 E03 产物，不反向挂边、不新建 null 糖 pass）；① **采纳 §3.4 E03 小补丁**（含 CA 的 `??=` 左值 skip 修复），随 C35 M2 合入，CS2J0563 不作为长期行为；② 三套同构 lifter 上收 `Core/Preprocessing/Expressions/SingleEvaluationTempLifter` 的时点定为**本 pass 默认开前**（不允许三套命名/逻辑漂移随默认开固化）。
- **Q2 兄弟副作用精确顺序**：M3 前凭画像定论——存在真实「同句前序副作用 + 条件访问」混合站点则实现语句分裂精确保序，并作为默认开硬门；无命中则永久与 E03 parity 并文档化限制。
- **Q3 值类型接收者条件赋值**：**M3 收编普通 struct**（自降 `HasValue`/块临时 + 回写 `var q = p.Value; q.X = v; p = q;`）；record struct 维持移交 make-readonly（0562），其余 0561 在 M3 前为 v1 行为。
- **Q4 `?[^n]` 联合 lowering**：C35 对非白名单索引实参**自发块内临时（GuardedBlock）作为默认路径**，不依赖 C31 增强 lifter；C31 dominated 插点增强为可选优化；M3 两族联合 fixture 为硬验收。
- **Q5 void 无锚点面扩面节奏**：M2 收编表达式体 void 成员、switch arm 承载的条件调用；using 声明等其余形态 M3；**默认开前 CS2J0557/0558 清单必须全部有主**（已支持或显式诊断），不允许静默残留。

---

### 原问题记录（裁决前）

- **Q1（链序结论）与 E03 的最终归一形态**：本设计推荐「保持注册表现状——C35 作为 expression-desugar 族规则消费 E03 产物，不反向挂 E03、不新建 null 糖 pass」，理由是锚点/码段/基建已冻结、两遍 lifter 已被族 TempLifter 同构复用、两语法面不相交使单趟无环终止。待评审确认两点：① 是否采纳 §3.4 对 E03 的可选小补丁（含 CA 的 `??=` 左值 skip），还是保留 CS2J0563 硬拒绝作为长期行为；② E03/index-range/expression-desugar 三套同构 lifter 上收 `Core/Preprocessing/Expressions/SingleEvaluationTempLifter` 的时点（建议默认开后独立 PR）。
- **Q2 锚点前插临时与兄弟实参的求值顺序**：INV-1 保证求值次数，但「接收者临时提前到锚点语句之前」相对同句前序副作用（其他实参、插值洞）与 E03 现状同为近似。M3 前需凭画像数据决定是否实现「语句分裂」精确保序（把锚点调用拆成前序实参临时 + 段临时 + 调用），还是永久与 E03 parity 并文档化限制。
- **Q3 值类型接收者条件赋值的终态**：M1–M3 采用「record struct 移交 make-readonly（0562）+ 其余 CS2J0561 Error」。是否在 M3 自降 `HasValue`/块临时 + 回写（`var q = p.Value; q.X = v; p = q;`）收编普通 struct，取决于画像中普通 struct（非 record）条件赋值命中率及其与 E13 的交互复杂度。
- **Q4 `?[^n]` 联合 lowering 的守卫保持**：C31 在三元真分支/if 块内提升 hat 运算元时需要「 dominated 插点」能力，当前其锚点搜索取最近语句（可能提分支外）。是否由 C31 增强 lifter，还是 C35 对 `?[]` 的非白名单索引实参自行发块内临时（Shape 升级为 GuardedBlock）作为默认路径，需两族联合评审；M3 fixture 为硬验收。
- **Q5 void 无锚点面与锚点白名单扩面节奏**：表达式体 void 成员、switch arm/using 声明等承载的条件调用，M1 报错/M2 部分收编的边界是否与画像命中率匹配；默认开前需一轮 sdk 扫描确认 CS2J0557/0558 清单可接受。
