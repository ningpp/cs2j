# C26 LINQ 通道独立化设计：嵌入式 E16 → 标准 preprocessor（★IQueryable 三路分流强制验收门）

**日期**: 2026-09-29
**范围**: 把注册表 stub `linq-channel`（catalog C26，P2，结构性候选）从 `Stub` 翻转为 `Implemented`；新增 Core 通道组件与 CLI 适配器，把 E16 的嵌入式 LINQ 重写提升为「独立中间目录 + 独立入口 + dotnet build/test 等价性闸门 + skipped-chain 报告」的标准 preprocessor，并在通道入口强制实施 receiver 三路分流。**嵌入式 E16 的摘除不在本特性 PR，归 M4 独立转换器 PR（§5.4）**
**状态**: 设计定稿待评审；实施在独立 worktree（见 §12），本文不改动任何代码
**对应基线**: `953293b9`（dev-3.x HEAD，完整 SHA `953293b908e9143437c45f287fb4277e890c398e`，"docs: update doc base commit"，2026-09-29 已核实存在）
**关联资料**: `prompts/preprocessor-catalog.md` C26（L566-577，含四次补全强制门全文）/C19（L484-494）/C21（L508-518）/§4 风险 17（L936，IQueryable 静默客户端求值）与风险 8（L927，表达式树共同禁区）/§5.1 .NET 半区与 EF Core 测试预言机（L946-955）；`.trae/documents/preprocessor-foundation_plan.md` L60-139（标准 spine/verify/中间目录契约/交付包模板）；姊妹设计 [2026-09-29-c19-extension-methods-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c19-extension-methods-design.md)（§3.4 EM-ORDER/EM-LINQ 结论）、[2026-09-29-c21-anonymous-type-lift-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c21-anonymous-type-lift-design.md)；范本 [2026-09-28-java-keywords-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md)

---

## 1. 背景与目标

### 1.1 现状：LINQ 重写是主转换器内的嵌入式通道，不是独立 preprocessor

E16 的全部资产位于 [LinqRewrite/](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/)（10 个文件），在**主转换期**以内嵌 pass 形态运行，没有独立中间目录、没有独立入口、没有 .NET 侧等价性闸门：

| 资产 | 规模/入口 | 职责与证据 |
|---|---|---|
| [LinqRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) | 1624 行，partial `CSharpSyntaxRewriter` | 过程式化主体重写器：方法链 → foreach/聚合循环 + 提取 `*_ProceduralLinqHelper`（[L1620-1667](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L1620-L1667)）；foreach 直接包裹（[L1599-1618](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L1599-L1618)）；构造仅吃 `SemanticModel + ConversionOptions`（[L43-47](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L43-L47)）。源自第三方 roslyn-linq-rewrite（文件头版权 L1-21） |
| [LinqRewriter.Rules.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs) | 2156 行 | 全部算子规则（Where/Select/Join/GroupJoin/聚合/排序/分组/集合运算…）与规则常量（如 `WhereMethod` 的 reduced 显示名，[L2309](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L2309)） |
| [LinqQueryDesugarer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs) | 233+ 行 | **纯语法**把 query expression（`from…where…select`）去糖为 member-access 方法链（[L19-37](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs#L19-L37)、[L108-120](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs#L108-L120)）；不查 SemanticModel |
| [LinqRewriteObservability.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriteObservability.cs) | 104 行 | `LinqSkipReason`（9 种）、`LinqSkipInfo`、`LinqOperatorOccurrence`、`LinqRewriteStatistics`（DesugaredQueryCount/RewrittenChainCount/RewrittenMethodCount/SkippedChains/EncounteredOperators + MergeFrom） |
| [LinqStep.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqStep.cs)、[Lambda.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/Lambda.cs)、[ExtensionMethods.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/ExtensionMethods.cs)、[CanRewrapForeachVisitor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/CanRewrapForeachVisitor.cs)、[LiteralString.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LiteralString.cs)、[ExitException.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/ExitException.cs) | 辅助 | 注意 [ExtensionMethods.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/ExtensionMethods.cs) 是第三方语法 helper（`WithStatic/WithRef`），与扩展方法语义解析无关（C19 设计 §1.1 已澄清） |

**调用点（嵌入式，主转换期运行）**：

- 项目路径：[ProjectPasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs) 的 `ProjectLinqDesugarPass`（[L151-243](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L151-L243)），门控一行 [L164](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L164)：`EnableLinqRewrite && !EffectivePreferStreamApi`；两阶段——并行纯语法 desugar（[L286-327](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L286-L327)）→ 重建 compilation → 逐树 `LinqRewriter`（[L329-385](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L329-L385)）→ 重建 Library/TypeGroups（[L262-284](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L262-L284)）；注册于 [ProjectConversionPipeline.cs:262-279](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs#L262-L279)（pass 链首位），统计经 `LastLinqStatistics`（[L341](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs#L341)）上抛。
- 单文件路径：[SingleFilePasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs) 的 `SingleFileLinqDesugarPass`（[L28-121](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L28-L121)），门控 [L40](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L40)，desugarer [L64](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L64)、rewriter [L82](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L82)；注册于 [ConversionPipeline.cs:415](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L415)。

**门控与报告**：

- `ConversionOptions.EnableLinqRewrite` 默认 **true**（[ConversionOptions.cs:53-54](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionOptions.cs#L53-L54)）；CLI `--no-linq-rewrite`（[Program.cs:4606-4607](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4606-L4607) 与 [L4667-4668](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4667-L4668)，`EnableLinqRewrite => !NoLinqRewrite`）。
- Stream 偏好选项 `PreferStreamApi`（[L60](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionOptions.cs#L60)）**已是死开关**：`EffectivePreferStreamApi => false` 硬编码（[L62-68](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionOptions.cs#L62-L68)）——当前事实上只有过程式化一条策略。
- 报告：`--linq-report`（[Program.cs:4685-4686](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4685-L4686)）→ `WriteLinqReport` 产 `cs2j-linq-report.json`（[L1330-1382](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L1330-L1382)：desugared/rewritten/skipped 计数 + skippedChains + uncoveredOperators），三种转换目标各调一次（[L384-385](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L384-L385)、[L627-628](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L627-L628)、[L811-812](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L811-L812)），统计靠 `MergeLinqStatistics` 跨项目聚合（[L1384-1395](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L1384-L1395)）。
- 失败不阻断：rewriter 内部 catch `InvalidCastException/NotSupportedException/ArgumentException` → 记 skipped chain + Warning，继续转换（[LinqRewriter.cs:106-120](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L106-L120)、[SingleFilePasses.cs:90-100](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L90-L100)）。

**测试现状**：tests 下 Linq/Query 相关 **35 个文件、167 个 `[Fact]/[Theory]`**（LinqProceduralPhase1-9、LinqChainRefactoringTests、LinqQueryDesugarTests、LinqObservabilityPhase10Tests、ConversionTests 的 LinqTests/LinqBulkTests 等），全部面向**嵌入式路径**；全 tests 目录**没有任何 IQueryable / AsQueryable / `Expression<Func>` 专项测试**（唯一命中是 [ArrayForeachNotCollectedTests.cs:71](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ArrayForeachNotCollectedTests.cs#L71) 一处注释性用例）。

### 1.2 通道化收益（catalog C26 立项理由）

1. **.NET 半区等价性可证**：过程式化产物是合法 C#，在独立中间目录 `.cs2j-linq-channel-src` 上跑 `dotnet build -c Release` + `dotnet test -c Release`（源项目自带 xUnit），降级正确性在 .NET 侧直接暴露——不再只靠 Java 侧迭代（foundation plan §5.1 两半区模型，catalog L946-955）。
2. **流水线一致性**：与已翻转的 18 个注册表 pass 共用 descriptor/拓扑/指纹/verify/`--list-passes`/CS2J9001 stub 语义；LINQ 是最后一个仍以手写内嵌形态存在的大型重写器。
3. **失败位置前移**：skipped chain、过程式化引入的编译错误在中间树当场暴露，而不是带着残余 LINQ 调用进入 Java emit。
4. **策略可独立演化**：Stream/Procedural 两路、未来 Queryable 查询提供方映射，都变成「通道产物」这一个可独立验证的输入面。

### 1.3 ★核心问题：缺 receiver 三路分流，EF 服务端查询会被静默改成客户端求值

catalog 四次补全（L574）的强制语义门：LINQ 必须在通道入口按 receiver 分三路——

1. `IEnumerable`/`IOrderedEnumerable` receiver → 现有过程式展开；
2. `IQueryable`/`IOrderedQueryable` receiver（含一切绑定到 `System.Linq.Queryable` 的调用）→ **绝不展开**，保留 `Queryable.xxx(...)` 交 Java 侧查询提供方；
3. `Expression<Func<...>>` 实参 → 保留表达式树节点构造（lambda 保持被引号包裹）。

**2026-09-28/29 复核证据**：

- [LinqRewrite/](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/) 全目录**无任何 IQueryable / Expression 树判定**（grep 命中的 `Expression` 均为 Roslyn 语法类型，与 `System.Linq.Expressions` 无关）。
- 主仓仅三处相关：①[ConversionPipeline.cs:177-183](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L177-L183) 补 `System.Linq.Queryable`/`System.Linq.Expressions` 程序集引用让 Roslyn 能解析符号（**仅引用，不做行为分流**）；②[TypeMappingService.cs:516-522](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L516-L522) 把 `Expression<TDelegate>` 包装剥成委托类型映射；③[ArgumentTransformer.Coercion.cs:597-600](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Coercion.cs#L597-L600) 一处类型名字枚举（含 IQueryable/IOrderedQueryable/ParallelQuery，用于集合 coercion，非 LINQ 分流）。
- **具体危险路径已定位**：rewriter 的算子身份判定 `GetMethodFullName` 首选语义符号的 reduced 显示名（[LinqRewriter.cs:788-795](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L788-L795)），`Queryable.Where` 的 reduced 显示名是 `System.Linq.IQueryable<TSource>.Where<TSource>(System.Linq.Expressions.Expression<...>)`，**正常解析时**不命中任何 `KnownMethods`（全部是 `System.Collections.Generic.IEnumerable<...>` 形态，如 [LinqRewriter.Rules.cs:2309](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L2309)）；但当语义符号解析失败（注释自述「common in project pipeline」），存在**按语法名猜 Enumerable 身份的 fallback**（[LinqRewriter.cs:796-844](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L796-L844)）：`n == null` 时仅凭 `ma.Name.Identifier.Text == "Where"/"Select"/...` 就构造 `IEnumerable<TSource>.Where(...)` 身份——Queryable 调用在此分支会被**误判为 Enumerable 并过程式化**。

后果（catalog §4 风险 17，L936）：`dbContext.Orders.Where(o => o.Total > 100)` 本应翻译为服务端 SQL，被展开成客户端 foreach 后**代码照样编译、业务单测用 in-memory 列表往往还能通过**，直到生产环境全表加载才暴露——这是「能编译、单测难抓」的静默语义错误。风险 8（L927）同理：表达式树内 lambda 被改写会改变树节点形态，破坏提供方的表达式检视。

### 1.4 目标与非目标

**目标**：

1. 翻转 `linq-channel` 为标准 preprocessor：中间目录 `.cs2j-linq-channel-src`、注册表拓扑、verify 默认双开、独立入口、指纹接入、skipped-chain JSON 报告。
2. **★三路分流门 100% 正确是通道化的前置验收条件**（不是可选项）：Enumerable 过程式化、Queryable 零误展开、Expression 树零破坏，并以防回归闸门测试永久锁死。
3. 通道化 Enumerable 路径与嵌入式 E16 **逐行为等价**（同一重写器、同一组规则），.NET 半区 build/test 全绿。
4. 封堵 [LinqRewriter.cs:796-844](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L796-L844) 的语法名猜测 fallback：解析失败时保守保留 + 显式诊断，禁止猜 Enumerable。

**非目标**：

- **不做 Java 侧查询提供方**：当前 [TypeMappings.json:3697-3702](file:///d:/code/cs2j/config/TypeMappings.json#L3697-L3702) 把 `IQueryable\`1` 映射为 `java.util.stream.Stream`，java/ 目录无 Queryable 兼容类（2026-09-29 grep 零命中）。通道 v1 的职责是**在 C# 侧保住 Queryable 形态不被破坏**，使未来的 Java 提供方映射成为可能；端到端「服务端查询下推 Java」是独立工作项（§2-D15、§11）。
- 不复活 Stream 策略（`EffectivePreferStreamApi` 硬编码 false 保持）；不重写算子规则；不搬移 3780 行第三方重写器（§2-D13）。
- 单文件直转模式不通道化（无工程树可做中间目录），保留嵌入式 E16（§5.5）。

---

## 2. 关键设计决策表

| 决策点 | 结论 | 证据/备注 |
|---|---|---|
| **D1 ★ReceiverKind 判定依据** | **以 SemanticModel 绑定到的被调方法符号所属静态类判定，不按 receiver 运行时/接口推断**：`GetSymbolInfo(invocation).Symbol`（含 `ReducedFrom` 归一）为 `IMethodSymbol`，`ContainingType` 全限定名 == `System.Linq.Enumerable` → **Enumerable 路**；== `System.Linq.Queryable` → **Queryable 路**；其余按 NonLinq/Ambiguous 处理。判定在**符号**上做，语法形状（名字/MemberAccess/参数 arity）永不参与路由 | LC-ROUTE；理由见下「★判定结论论证」 |
| D2 `Expression<Func<>>` 实参识别（第三路） | 对每个实参 `arg` 取 `semantic.GetTypeInfo(arg).ConvertedType`（lambda 用 ConvertedType 而非 Type）：若为 `INamedTypeSymbol { Name == "Expression", ContainingNamespace == "System.Linq.Expressions" }` 且有 1 个类型实参 → **Expression 路**。同时看形参类型（实参是标识符/方法组时 ConvertedType 同样有效：`Expression<Func<T,bool>> p = x => …; q.Where(p)` 中 `p` 的 ConvertedType 即 Expression<>） | 与 [TypeMappingService.cs:516-522](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L516-L522) 同一符号判定口径；BCL 中 Expression 实参只出现在 Queryable 算子与自定义树消费方法，两路用「参数位点」独立标记后取并集 |
| D3 自定义 IQueryable 实现 / 自定义 query provider | ①实现了 `IQueryable<T>` 的自定义类型（含 EF 的 DbSet/EntityQueryProvider）：其算子调用经重载决议**绑定到 `System.Linq.Queryable` 静态类**，D1 天然命中 Queryable 路，无需特判类型实现；②提供方自有扩展（`Include/ThenInclude/AsNoTracking` 等）绑定到**其他静态类** → NonLinq 类，rewriter 今天本就不认识（`IsSupportedMethod` false），通道**只计数/报告不触碰**（LC0030 Info）；③自定义 `IQueryProvider.CreateQuery` 直连不经过 LINQ 语法，不在通道面 | 关键推论：**判定被调方法归属比判定 receiver 是否实现某接口稳**——provider 作者不必向通道注册 |
| D4 query expression（`from…select`）的 queryable 形态 | **desugar 无条件照跑（阶段 1，纯语法），分类只作用于去糖后的方法链（阶段 2，重建编译后）**。query over IQueryable 去糖出的 `.Where(...)` 经重编译绑定为 Queryable.* → Queryable 路保留；query over IEnumerable 绑定 Enumerable → 过程式化。v1 **保留 reduced member-access 形态**（`q.Where(...)`），不 canonicalize 成 `global::System.Linq.Queryable.Where(q, …)`（那是 C19 EM-LINQ 明确豁免的形态，见 §11） | C# 规范的 query lowering 本来就是这组方法调用，去糖本身语义恒等；query syntax 无法存活到 Java，去糖后保留 Queryable.* 调用即满足 catalog 要求的产物形态 |
| D5 三路各自产物 | ①**Enumerable**：与今天完全相同的过程式化（foreach/聚合循环 + 提取 helper + foreach rewrap）；②**Queryable**：调用节点零改写，lambda 保持 quoted，整链原样流入下一环节（中间树仍可编译：Queryable/Expression 程序集已在引用集，TPA 全集见 [ProjectUnsignedTypePreprocessor.cs:81-113](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs#L81-L113)）；③**Expression 实参**：实参与外围调用零改写，表达式树构造节点序列保持 | ②的 Java 终态见 D15 |
| D6 混合链（一条链上 Enumerable/Queryable 跨界） | **v1 链级保守：一条 member-access LINQ 链中只要含任一 Queryable/Expression 路节点，整条链不展开**（含外层 Enumerable 算子），逐链报告 LC0020。`AsEnumerable()/AsQueryable()/ToList()/ToArray()/ToDictionary()` 的精确认证见 §4.2-D7；v2 再做「按边界算子切段、只展开 Enumerable 段、Queryable 段作不透明源表达式」 | 正确性优先于覆盖率；保守保留的链进 skipped/JSON 报告可盘点，误展开则是静默错误，两者不等价 |
| D7 边界算子语义（为什么符号判定忠实） | `q.AsEnumerable().Where(...)`：`AsEnumerable` 返回 IEnumerable，后续 Where **绑定 Enumerable**——C# 语义在此刻就是客户端求值，展开忠实；`e.AsQueryable().Where(...)`：绑定 Queryable，保留；`q.Where(...).ToList()`：`ToList/ToArray/ToDictionary` **只定义在 Enumerable**，IQueryable 经 IEnumerable 基接口适用，C# 调用本身强制枚举——展开它也忠实，但按 D6 v1 整链保留。**任何按「源变量类型是 IQueryable」的启发式都会在这三个算子上出错，符号归属不会** | 这是 D1 结论优于 receiver 类型判定的决定性论据 |
| D8 Stream 偏好旁路 | v1 **不设通道级 Stream 选项**：`PreferStreamApi`/`--prefer-stream-api` 已是死开关（[ConversionOptions.cs:62-68](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionOptions.cs#L62-L68) 硬编码 false），通道只产出过程式化一种 Envelope；Stream 是 Java emit 层映射话题，随 D15 单独评审。通道描述符不新增 options 字段，避免复活双策略复杂度 | 现状核实结论；`--prefer-stream-api/--prefer-procedural` CLI 选项在 M4 退役评审中处理（§5.4） |
| D9 通道化后默认开/关 | 翻转时 `defaultEnabled: false`（opt-in）；满足 §8 全部门槛（★三路分流 100%、IQueryable 零误展开、Release build/test 全绿、E16 双跑抑制验证）后翻 true | 对齐 catalog 风险 10 与 C02/C19/C21 既有惯例 |
| D10 嵌入式 E16：退役/并存 | **并存于整个 M1-M3（默认关期间）**：通道运行时由 CLI 单点抑制 E16（§5.3），通道不运行时 E16 原样。**E16 项目路径摘除归 M4 独立转换器 PR**，不进通道特性 PR；单文件路径长期保留 E16（§5.5） | 与 C19/C21「转换器侧旧资产另立 PR」同款风险隔离；167 个测试与三种 verb 依赖 E16 外形 |
| D11 skipped-chain / channel 报告格式 | 扩展现有 `LinqRewriteStatistics`（不破坏 `--linq-report` 消费者）：新增路由计数（`EnumerableChainsExpanded`/`QueryableChainsPreserved`/`ExpressionArgumentSites`/`MixedChainsPreserved`/`AmbiguousChains`/`ProviderCustomCalls`）与 `PreservedChains`（file/line/host/operator 序列/route）。通道在中间根写 `cs2j-linq-channel-report.json`（schema 见 §4.6）；`--linq-report` 在 M4 前继续出 E16 老报告，M4 切换为通道报告（字段超集，文件名沿用 `cs2j-linq-report.json`） | 复用 [LinqRewriteObservability.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriteObservability.cs) 与 [Program.cs:1330-1382](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L1330-L1382) 现有格式 |
| D12 失败语义 | 分流**判定**失败（符号歧义/解析失败）= 保守保留 + LC0010（默认 Warning，`--strict` 下 Error）；**「Queryable 节点被要求展开」= LC0011 硬 Error + 整文件回退**（防回归性质，任何正常输入都不应产生）；rewriter 规则展开异常维持今天的 skip+Warning 口径（不改变既有 167 测试行为） | catalog 风险 12 第一原则：无法等价时显式诊断，禁止静默透传；但不借通道化之机改变既有 skip 策略 |
| D13 重写器：直接引用还是搬迁 | **不搬迁**。`LinqRewriter` 留在 [LinqRewrite/](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/)（第三方版权、1624+2156 行、167 测试依赖其命名空间与行为）；通道 Core 新建独立目录 `LinqChannel/` 引用它。接入方式 = **最小 seam**：给 `LinqRewriter` 构造增加一个**可选**分类器参数（默认 null = 今天行为，零变化），并以新 partial 文件 `LinqRewriter.Gate.cs` 承载闸门检查（两个拦截点：`TryVisitInvocationExpression` 入口与 `TryVisitForEachStatement` 的 collection 判定），对既有大文件只加极小 hunk | LC-SEAM；搬迁改名回归（版权头/using/167 测试期望）是纯风险零收益；M5+ 可再评估命名空间归并 |
| D14 编译模型：逐文件还是整树 | **整树编译、两阶段、自定义 ExecuteAsync**（同 JavaKeywords 适配器形态）：阶段 1 全树纯语法 desugar → 落盘 → 重建编译；阶段 2 逐文件 `LinqRewriter`（吃各自 SemanticModel）。标准 spine 的单次编译不够用（desugar 后必须重绑符号才能判 Queryable），适配器自己驱动 stage helper（`BuildStandardCompilation` 从磁盘重读，见 [ProjectPassRunner.cs:136-155](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L136-L155)） | 与今天 `ProjectLinqDesugarPass` 的两阶段结构一一对应，等价性可比对 |
| D15 Java 半区边界（诚实范围声明） | 通道只保证 C# 半区：Queryable 链**形态不被破坏**。当前 Java 半区 `IQueryable\`1 → java.util.stream.Stream`（[TypeMappings.json:3697](file:///d:/code/cs2j/config/TypeMappings.json#L3697)）、`Expression` → compat `Expression`（[L3370-3388](file:///d:/code/cs2j/config/TypeMappings.json#L3370-L3388)，[Expression.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Expression.java) 存在），即 Queryable 保留后 Java 终态今天仍是 Stream 客户端形态。**EF 服务端下推需要独立的 Java 查询提供方/桥接设计，不在本通道承诺内**；通道的价值是消除「静默改成客户端 foreach」并把边界做成报告中可见的显式事实 | 不夸大收益；M0 对 EF Core 源码跑计数扫描（catalog §5.1 L955、风险 17 L936）为该后续工作定量 |

### ★D1 判定结论论证（评审锚点）

**结论：按符号绑定的被调方法所属静态类（`IMethodSymbol.ContainingType`，reduced 调用经 `ReducedFrom` 归一）判定 Enumerable vs Queryable；不按 receiver 类型的接口实现判定。**

1. **接口归属本身歧义**：`IQueryable<T> : IEnumerable<T>`，自定义 query provider 类型同时实现两者；`List<T>` 只实现 IEnumerable 但 `list.AsQueryable()` 的静态类型变成 IQueryable——「查 receiver 实现了哪些接口」给不出唯一答案，而 C# 重载决议已经基于 receiver **静态类型**与实参可转换性（`Func<>` vs `Expression<Func<>>`）做出了唯一选择，复用该选择比重建判定又稳又便宜。
2. **reduced / 非 reduced / 查询式去糖三态统一**：`src.Where(f)`（ReducedExtension，ContainingType 仍是 Queryable）、`Queryable.Where(src, f)`（普通静态调用）、query syntax 去糖后重编译绑定的调用——三种形态的 `ContainingType` 结论一致。
3. **边界算子天然正确**（D7）：`AsEnumerable/AsQueryable` 的语义翻转、仅存在于 Enumerable 的 `ToList/ToDictionary`，符号归属都与 C# 求值边界精确同构；receiver 启发式必然在这些点误判。
4. **封堵现有漏洞**：D1 判定前置到 `IsSupportedMethod` 之前，[LinqRewriter.cs:796-844](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L796-L844) 的 `n == null` 语法名 fallback 对 LINQ 链不再可达——符号拿不到时走 Ambiguous 保留（LC0010），绝不猜 Enumerable。

---

## 3. 注册表与链位契约

### 3.1 描述符（Stub → Implemented 翻转目标）

在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) `Implemented` 表**末尾、`java-keywords` 之前**插入：

```text
Id                          = "linq-channel"
DisplayName                 = "LINQ channelization"
IntermediateDirectoryName   = ".cs2j-linq-channel-src"
Stage                       = PassStage.ControlFlow          ← stub 原值维持（Stage 仅信息性）
Status                      = PassStatus.Implemented
DefaultEnabled              = false（初版 opt-in；过 §8 门槛后翻 true）
RunAfterIds                 = { "extension-methods",         ← stub 锚保留（C19，翻转后常量迁 PassIds）
                                "anonymous-type-lift",       ← stub 锚保留（C21，同上）
                                "eliminate-goto" }           ← LC-ORDER 新增已实现锚，见 §3.2
SupportLibrary              = PassSupportLibrary.None
NeedsSemanticModel          = true
VerifyBuildByDefault        = true
VerifyTestsByDefault        = true
CatalogItems                = "C26"
Description                 = "Channel LINQ by receiver: proceduralize Enumerable, preserve Queryable calls and Expression<Func> arguments."
```

`IntermediateDirectoryName` 常量放适配器 `LinqChannelPassAdapter.IntermediateDirectoryName`（无历史门面类，同 C02/C21 先例，不新建 `ProjectXxxPreprocessor.cs` 薄壳）。

### 3.2 stub 期拓扑：两个 RunAfter 锚也是 stub 时怎么办（LC-ORDER）

问题：翻转 C26 时 C19（`extension-methods`）与 C21（`anonymous-type-lift`）**大概率仍是 stub**（两者均未翻转）。注册表允许 Implemented 描述符 RunAfter stub id（`java-keywords` 已先例行：其 RunAfter 含 platform-isolation/concurrency-normalize/dynamic-desugar 三个 stub，[PassCatalog.cs:225-231](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L225-L231)），但仅有 stub 锚时，C26 在已实现子链中的相对位置只靠传递边与注册顺序 tie-break，存在漂移风险：

- C19 stub 的传递锚 = {delegate, tuple, ref-out-in, unsigned, array-method}；C21 stub 的传递锚 = {tuple}。它们只保证 C26 落在 `array-method` 之后，与 `make-readonly`/`iterator-lowering`/`eliminate-goto` 之间**无任何边**。

**决策（LC-ORDER）**：翻转时**追加已实现锚 `eliminate-goto`**。理由：

1. **等价于今天的事实顺序**：现状 eliminate-goto 在 CLI 预处理链尾运行，而 E16 LINQ 在**更晚的主转换 desugar 期**才运行（[ProjectPasses.cs:151-164](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L151-L164)）。「goto 在前、LINQ 在后」是现网行为，追加锚把该事实固化为拓扑边，通道化不引入链序回归。
2. goto 是纯语法 pass（descriptor `needsSemanticModel: false`），不改变 LINQ 调用的 reduced 糖形态，C19 EM-LINQ 所依赖的 `GetSymbolInfo` 完整性不受影响；C19 翻转后其位置在 eliminate-goto **之前**（EM-ORDER：iterator-lowering 之后、goto 之前），拓扑 `C19 → goto → C26` 无环、满足 C26 RunAfter C19 的注册契约。
3. `java-keywords` 仍是最后一个 pass：C26 不进 java-keywords 的 RunAfter 集合，描述符在 `Implemented` 表中注册于其前，现有「java-keywords 末位锁定」测试（[PassChainOrderTests.cs:70-75](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L70-L75)）保持成立；新增顺序断言显式锁定「已实现子链中 linq-channel 紧随 eliminate-goto 之后、java-keywords 之前」。
4. C21（Declarative，RunAfter=tuple）翻转后天然位于链前段，与 LC-ORDER 无交互。

备选（不推荐）：不追加锚、仅靠 Implemented 表注册顺序 tie-break——省一条边但把链位正确性押在表顺序上，C19/C21 翻转或未来新 pass 插入时可能静默漂移，被顺序测试漏过（测试只锁已实现子序列）。

### 3.3 Stub → Implemented 翻转清单

| 文件 | 改动 |
|---|---|
| [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L219-L228) | 删除 `linq-channel` stub 条目（L219-228）；`Ids.LinqChannel` 常量迁移到 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L25) 的 `PassIds`（同 C02 翻转先例）。**注意**：stub 表内没有其他条目引用该常量，可直接迁移 |
| [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | `PassIds` 增 `LinqChannel = "linq-channel"`；按 §3.1 位置插 Implemented 描述符 |
| `src/CSharpToJava.CLI/Preprocessing/Adapters/LinqChannelPassAdapter.cs` | **新增**适配器（§5.1） |
| [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L93-L237) | `PassChainConfiguration` 增 `LinqChannelOptionsFactory`（懒加载，同 JavaKeywordOptions 模式）；`CreateAdapter` switch 增分支；**不进 `BuildLegacyToggles`**（通道缺省态取描述符；`--no-linq-rewrite` 的映射在 M4 随 E16 退役处理，默认关期间两者独立，见 §5.3） |
| `src/CSharpToJava.Core/LinqChannel/*` | **新增** Core 组件（§4） |
| [LinqRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) + 新增 `LinqRewriter.Gate.cs` | 最小 seam（D13）：构造器一个可选参数 + 两个入口的闸门 hunk；默认 null 时逐字节保持今天行为 |
| 测试 | ①[PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L38) `LockedChain` 18 → 19（linq-channel 位于 IteratorLowering/EliminateGoto 之后、JavaKeywords 之前——stub 交织下已实现子序列中的精确相对位置）；`EnabledByDefault` 不变（默认关）；②[PassStubCatalogTests.cs:24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24) stub 计数 23 → 22；③`ProjectPreprocessorsConsistentTests` 追加 adapter/目录/描述符断言（仿 generic-deepen 段）；④Core 单测/fixture/residual/CLI（§7） |

不需要改：指纹逻辑（按注册表全集自动生成 `pass:linq-channel=on/off` 令牌，翻转造成一次性缓存失效，符合 foundation plan L72 既定口径）、preserve 目录机制、`ProjectPassRunner`（适配器自定义 ExecuteAsync 复用 stage helper）、通用 verb（`preprocess --pass linq-channel` 自动可达）。

### 3.4 独立入口命名

- v1 **只经通用入口**：`preprocess --pass linq-channel`（[PreprocessVerbOptions](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4399) / `PreprocessVerbAsync` [L3409](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3409)）与 convert-project 注册表链；**不新增** `ProjectLinqChannelPreprocessor` 薄门面、不新增专用动词（stub 是注册表先建的，无历史 verb，同 C02/C05/C21 决策）。
- 「独立 verb 命名」保留结论：若 M4 退役 E16 后需要与 `--linq-report` 搭配的便捷入口，再按 foundation plan 的「旧 verb 薄委托」模式新增动词 `linq-channel`（内部仅调 runner 指定 pass）；本设计不预支该动词。

### 3.5 中间目录与 verify

- 目录 `.cs2j-linq-channel-src`；输入 = 前序全部已启用 pass 的产物树（默认关期间即当前链 + goto 产物），输出交 java-keywords（默认关时直接）与主转换。
- `VerifyBuild/VerifyTests` **默认双开**（最严档，对齐 generic/generic-deepen/C19/C21）：过程式化是大体量语句改写，.NET 半区 build/test 是本通道存在的核心理由；`DeleteBuildOutputsAfterRestore = true`；`--no-verify-build/--no-verify-tests` 可关（[Program.cs:4773-4777](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L4773-L4777) 既有机制，catalog 风险 11）。
- 无支撑库：不增 `PassSupportLibrary` 枚举值，不触发支撑工程过滤，本通道不生成新文件（helper 方法注入在**改写文件内部**，是既有 rewriter 行为）。

---

## 4. Core 组件设计

### 4.1 目录与文件清单（全部新增于 `src/CSharpToJava.Core/LinqChannel/`）

| 文件 | 职责 |
|---|---|
| `LinqRoute.cs` | `enum LinqRouteKind { Enumerable, Queryable, ExpressionArgument, NonLinq, Ambiguous }` + `readonly record struct LinqRouteDecision(LinqRouteKind Kind, IMethodSymbol? BoundMethod, string? HostDisplay, LinqRouteAmbiguity? Ambiguity)` + `LinqRouteAmbiguity`（None/Unbound/Candidate/UnsupportedExpressionNode） |
| `LinqHosts.cs` | 常量 `EnumerableHost = "System.Linq.Enumerable"`、`QueryableHost = "System.Linq.Queryable"`、`ExpressionNamespace = "System.Linq.Expressions"`。**与 C19 EM-LINQ host 集合同源**：v1 两处各自一份常量（附注释交叉引用），C19 落地后评审提取共享常量（C19 设计 §11 已记录同一意图），不提前做大重构 |
| `LinqRouteClassifier.cs` | ★三路分流门主体（§4.2）：构造吃 `SemanticModel`；`ClassifyInvocation(InvocationExpressionSyntax)`、`ClassifyArgument(ExpressionSyntax)`、`ClassifyChain(IReadOnlyList<InvocationExpressionSyntax>)`、`IsExpressionQuoted(SyntaxNode)` |
| `LinqChannelOptions.cs` | `Strict`（歧义/残余是否升 Error，默认 false 对齐各 pass）、`ReportPath?`、`VerifyBuild/VerifyTests`（由适配器注入）；**不含 Stream 选项**（D8） |
| `LinqChannelStatistics.cs` | 通道统计：持有/聚合现有 `LinqRewriteStatistics` + §4.6 路由计数与 `PreservedChains`；`MergeFrom` 支持跨文件/跨项目 |
| `LinqChannelDiagnostic.cs` | 诊断类型（Severity/Code/Message/FilePath/Line）与码位常量表（§6，CS2J13xx） |
| `LinqChannelPlan.cs` | 单文件改写计划：文件内全部 LINQ 调用的路由决策集、待保留节点位置集、整链保留判定（D6）、Changed 预测；供 rewriter seam 与 residual 扫描共用 |
| `LinqChannelRewriter.cs` | 通道侧编排入口（非新重写器）：①对一个 SyntaxTree 跑 `LinqQueryDesugarer`（原样复用）；②重建编译后构造 `LinqRouteClassifier` + `LinqRewriter(semantic, options, gate)`，返回结果（root/统计/skipped/诊断）；③幂等自检 |
| `LinqChannelReport.cs` | 通道报告 JSON 序列化（§4.6），输出到中间根 |
| `ExpressionTreeFacts.cs` | 「节点是否处于 Expression<TDelegate> 引号内」共享判定（§4.4）；v1 落于本目录，C45（closure-lift 表达式树 lambda skip）落地时提升为共享组件（catalog 风险 8、§11） |

### 4.2 ★LinqRouteClassifier（QueryableGate）判定算法

```text
ClassifyInvocation(invocation):
  1. info = semantic.GetSymbolInfo(invocation)                 // reduced 与静态形态统一
  2. symbol = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol
     若 symbol?.MethodKind == ReducedExtension: symbol = symbol.ReducedFrom ?? symbol
  3. symbol == null:
       - info.CandidateReason ∈ {OverloadResolutionFailure, Ambiguous, …} → Ambiguous(Candidate)
       - 其他 → Ambiguous(Unbound)
       ★不再有任何「按语法名猜 Enumerable」回退（封堵 LinqRewriter.cs:796-844）
  4. host = symbol.ContainingType 的全限定显示名
       host == System.Linq.Enumerable → Enumerable
       host == System.Linq.Queryable  → Queryable
       其他 → NonLinq
  5. 逐实参 ClassifyArgument：任一实参 ConvertedType 为 Expression<> → 附加 ExpressionArgument 标记
     （Enumerable 宿主上出现该标记 = 不应发生的 BCL 形状，记 Ambiguous(UnsupportedExpressionNode)）

ClassifyChain(rewriter 已回溯出的 member-access LINQ 链):
  - 链上所有节点 ∈ {Enumerable, NonLinq}        → 可展开（维持今天行为）
  - 链上任一节点为 Queryable / ExpressionArgument → MixedChainsPreserved：整链保留（v1，D6）
  - 链上任一节点为 Ambiguous                      → 整链保留 + LC0010
```

要点：

- **判定点在 rewriter 展开决策之前**：seam 挂在 `TryVisitInvocationExpression`（[LinqRewriter.cs:123](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L123)）进入链回溯/规则匹配之前，以及 `TryVisitForEachStatement`（[L1604-1617](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L1604-L1617)）的 `IsSupportedMethod(collection)` 之前——两个入口都必须过门，foreach-rewrap 路径不能绕过。
- **Enumerable/Queryable 同名算子**（Where/Select/Join/GroupJoin/OrderBy/GroupBy/ThenBy/Skip/Take/Reverse/Concat/Union/Intersect/Except/…）全靠 host 区分，不维护算子名清单之外的任何特例；Queryable 独有的 `AsQueryable`、Enumerable 独有的 `AsEnumerable/ToList/ToArray/ToDictionary/ToHashSet/ToLookup` 自然分流（D7）。
- **let/into/join/嵌套查询**：desugar 后都是普通方法链（transparent identifier 由编译器/rewriter 既存逻辑消化，C21 设计 R7 已确认无创建语法节点），分类无需特殊条款；M3 矩阵逐一钉住。
- `NonLinq`（含 provider 自定义扩展）计数但不干预，保证第三方 `using` 进来的同名方法不会被误当 LINQ。

### 4.3 重写器 seam（D13 落法）

- [LinqRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) 改动仅两处小 hunk：
  1. 构造器增加可选参数 `LinqChannel.ILinqRewriteGate? gate = null`（存只读字段；null 时全部闸门检查短路返回「可展开」，**嵌入式 E16 不传该参数，行为零变化**）；
  2. 在 `TryVisitInvocationExpression` 与 `TryVisitForEachStatement` 的 `IsSupportedMethod` 判定前各加一个 `gate?.ShouldPreserve(...) == true` 早退（保留节点、记统计/诊断）。
- 新增 partial 文件 `LinqRewriter.Gate.cs`（同命名空间 `CSharpToJava.Core.LinqRewrite`，partial 类）：放 gate 字段、两个调用点的私有包装、以及「保留时的统计/诊断回传」。Core 内部引用（LinqChannel → LinqRewrite 同程序集），无跨工程问题。
- **不允许**在 Rules.cs（2156 行）内加任何 Queryable 名字特判；全部路由知识收敛在 `LinqChannel/`，Rules 保持「只懂怎么展开 Enumerable」。
- fallback 收口：[LinqRewriter.cs:796-844](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L796-L844) 的 `n == null` 语法名 switch 在 gate 启用时**整段不可达**（Ambiguous 已在外围保留）；加内部断言（gate 非 null 且 `n == null` 且处于 LINQ 链上下文 → 抛 LC0011 级不变量违规），防止未来规则改动重新打开洞口。

### 4.4 ExpressionTreePreservation（第三路）

- `ExpressionTreeFacts.IsQuotedLambda(model, lambda)`：从 lambda 节点取 `GetTypeInfo(lambda).ConvertedType`，判定是否 `Expression<TDelegate>`；并提供「祖先调用实参位点」反查（处理 `q.Where(x => …)` 这类 lambda 不直接带独立 ConvertedType 上下文的形态——以所在 invocation 的形参类型/绑定符号参数为准，与 Classifier D2 同源）。
- 保留语义：quoted lambda 的体内节点**一个都不许**被通道改写（不内联、不提临时变量、不重命名参数为展开形态）。Enumerable 路展开只可能遇到 `Func<>` lambda；quoted 只出现在 Queryable/NonLinq 调用，按 D6 整链保留后天然不可达——本组件的职责是把「不可达」做成**后置可验证事实**（residual 扫描重编译对比表达式树节点序列，§7.10），并为 C45/C19 提供共享判定（catalog 风险 8）。
- 表达式树内出现通道无法证明的节点（自定义调用、语句式 lambda 等）：LC0012 显式报错而非尝试改写。

### 4.5 LinqChannelPlan 与两阶段整树执行（D14）

通道一次执行对应适配器的自定义 `ExecuteAsync`：

1. **备树**：runner 标准备树（复制工程/csproj/非 cs 文件、排除计算）。
2. **阶段 1（纯语法，全树）**：对每个 cs 文件跑 `LinqQueryDesugarer`（直接复用现有类），Changed 文件落中间树；无 changed 则字节复制。确定性并行（沿用 [ProjectPassParallelism](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L179-L182) 口径，适配器内用 `OrderBy(ordinal ignore case)` 保证跨运行稳定）。
3. **重建编译 #2**：`context.BuildStandardCompilation()` 从中间树磁盘重读（[ProjectPassRunner.cs:142-146](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L142-L146)），Queryable/Expression 程序集经 TPA 全集天然可解析。
4. **阶段 2（语义，逐文件）**：每文件一个 `LinqRouteClassifier`（先 Analyze 建 `LinqChannelPlan`）+ 一个带 gate 的 `LinqRewriter`；Changed 落盘、clean 字节复制、BOM 保持（复用 `WriteRewrittenAsync`）。
5. **AfterFiles**：residual 扫描（§7.10，整树再编译一次做路由复核）+ 写 `cs2j-linq-channel-report.json`。
6. **restore → VerifyBuild/Tests → 删 bin**：复用 `context.Restore()` / `context.VerifyBuildAndTestsAsync()`，失败经 OnRestoreFailed/OnBuildFailed/OnTestsFailed 记 LC0050/LC0051/LC0052。

### 4.6 Options / Statistics / Report / Diagnostic

- **LinqChannelOptions**：`Strict`、`EnableQueryableGate`（M1 期可关以便分阶段落地，M3 起强制 true 且不再暴露关闭路径——★三门不可关是默认开前置）、报告开关（由 `--linq-report` 透传）。
- **统计 JSON（中间根 `cs2j-linq-channel-report.json`，M4 起也以 `cs2j-linq-report.json` 进目标根）**：

```json
{
  "schemaVersion": 2,
  "desugaredQueryCount": 0,
  "rewrittenChainCount": 0,
  "rewrittenMethodCount": 0,
  "routing": {
    "enumerableChainsExpanded": 0,
    "queryableChainsPreserved": 0,
    "expressionArgumentSites": 0,
    "mixedChainsPreserved": 0,
    "ambiguousChains": 0,
    "providerCustomCalls": 0
  },
  "skippedChains": [ { "reason": "...", "line": 0, "method": "...", "message": "..." } ],
  "preservedChains": [
    { "file": "相对路径", "line": 0, "route": "Queryable",
      "host": "System.Linq.Queryable", "operators": ["Where", "Select"] }
  ],
  "uncoveredOperators": [ { "operator": "...", "occurrences": 0 } ]
}
```

- 老字段（desugared/rewritten/skipped/uncovered）保持与 [Program.cs:1340-1377](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L1340-L1377) 同名同义，`schemaVersion` 与 `routing/preservedChains` 为超集追加。
- **Diagnostic**：适配器自有 `LinqChannelDiagnostic`（Severity/Code/Message/FilePath/Line + ToMsBuildString），不替换通用 `PassDiagnostic` 契约（foundation plan L56）。

---

## 5. CLI 适配器与集成

### 5.1 LinqChannelPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/LinqChannelPassAdapter.cs`，继承 [ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs)，形态对齐 [JavaKeywordsPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs)（**override `ExecuteAsync` 自驱两阶段**，`RewriteFileAsync` 抛 NotSupportedException）：

- `Descriptor => PassCatalog.ImplementedById(PassIds.LinqChannel)`；`LogPrefix "[linq-channel]"`；`CreateParseOptions` 用 `LanguageVersion.Latest`；`CompilationName "linq-channel-preprocess"`；global using 合成沿用 JavaKeywords 适配器同款 `ImplicitGlobalUsings.BuildForProject`（[JavaKeywordsPassAdapter.cs:289-305](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L289-L305)）。
- `VerifyBuild/VerifyTests` 取构造入参（convert-project 全局 verify 开关）；`DeleteBuildOutputsAfterRestore = true`。
- 持有聚合 `LinqChannelStatistics` 与诊断列表；`BuildChainSummaryLine` 形如：
  `linq-channel: N chains expanded (M methods), Q queryable chains preserved, E expression-arg sites, A ambiguous, S skipped, D queries desugared`。
- `BuildChainDiagnosticLines`：Warning/Error 常显，Info（providerCustomCalls/Info 诊断）仅 verbose。
- `OnRestoreFailed/OnBuildFailed/OnTestsFailed` → LC0050/LC0051/LC0052。

### 5.2 `--linq-report` 接入

- 默认关双轨期（M1-M3）：通道在**中间根**写 `cs2j-linq-channel-report.json`（无论 `--linq-report` 是否给，报告文件成本极低；`--linq-report` 仅控制是否额外把汇总打印/复制进最终目标根，M4 前的目标根报告继续由 E16 路径产）。
- M4（E16 项目路径退役）后：`Program.cs` 三处 `WriteLinqReport` 改为读通道报告（统计从适配器经 chain result 上抛，新增一个聚合通道，与现有 `MergeLinqStatistics` 同形）；`schemaVersion: 2` 字段超集保证老消费者不碎。

### 5.3 双跑抑制（并存期唯一新增的转换器侧接线）

通道与 E16 **绝不能对同一棵树各跑一次**（过程式化幂等性虽大体成立，但 skipped 链会被二次尝试、helper 会二次提取）。单点方案：

- convert-project 在注册表链执行后已有 `CliChainResult.ExecutedIntermediateDirectoryNames`（[CliPreprocessChain.cs:67](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs)）；当其中包含 `.cs2j-linq-channel-src` 时，在构造下游转换 options 处把 `EnableLinqRewrite` 置 false（一处条件，不写新开关）。
- 通道未启用时 E16 路径与今天**完全一致**（门控仍在 [ProjectPasses.cs:164](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L164)）。
- `--no-linq-rewrite` 在默认关期间语义不变（只关 E16）；M4 翻默认开时把它映射为 `--disable-pass linq-channel`（经 `BuildLegacyToggles` 一行映射），并在帮助文本标注。

### 5.4 旧调用点摘除归属：**转换器后续 PR（M4），不是本通道 PR 的一部分**

[ProjectPasses.cs:151-401](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs#L151-L401)（`ProjectLinqDesugarPass` + 两个 helper + Library 重建）、[ProjectConversionPipeline.cs:262-279,341](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs#L262-L279) 的注册与 `LastLinqStatistics`、[SingleFilePasses.cs:28-121](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L28-L121)、[ConversionPipeline.cs:415](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/ConversionPipeline.cs#L415)——摘除涉及 167 个测试的期望迁移、三 verb 统计外形、报告接线，风险半径独立且大。按 C19/C21 同款原则：**通道 PR 只加不改转换器**（除 §5.3 一处抑制条件与 §4.3 seam 默认参数）；M4 在通道翻默认开且门槛全过后，以独立 PR 退役项目路径 E16，届时：

- 删除项目路径内嵌 pass 与其重建逻辑（`PreDesugarCompilation` 等下游依赖点逐一盘点）；
- `--no-linq-rewrite` 改为注册表 toggle 映射；`--prefer-stream-api/--prefer-procedural` 死选项一并评审移除或重定义；
- 报告统一切 schema v2；相关 167 个测试中面向「转换器内嵌行为」的用例改为「通道中间树行为」断言。

### 5.5 单文件模式

单文件直转（无工程树、无中间目录契约）**不通道化**：继续走 [SingleFileLinqDesugarPass](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs#L28-L121) 嵌入式 E16。但 D1/D2 的分流判定以**共享 Core 组件**形态存在，M4 评审是否把 gate 也接到单文件 rewriter（低成本：同样传可选 classifier），使单文件路径同样获得「IQueryable 零误展开」保证——列为开放问题 Q4，不阻塞通道默认开。

---

## 6. 诊断策略

码位 CS2J13xx（CS2J11xx=C02、CS2J12xx=C19，本通道取 C26 下一空 band；只追加）：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1301（LC0010） | Warning（strict 下 Error） | LINQ 调用符号未绑定/候选歧义（Unbound/Candidate），链已**保守保留**未展开；消息含文件、行、CandidateReason、语法名。封堵语法名猜测后的显式出口 |
| CS2J1302（LC0011） | **Error + 整文件回退** | ★防回归不变量：路由结论为 Queryable/Expression 的节点进入了展开分支（或 gate 启用时 `n == null` fallback 被触及）。正常输入永不应产生；一旦产生即视为通道 bug，回退该文件全部改动并失败 |
| CS2J1303（LC0012） | Warning（strict 下 Error） | 表达式树引号内出现无法证明同一性的节点（自定义调用/语句 lambda），站点保留并报告 |
| CS2J1310（LC0020） | Info（verbose） | 混合链整链保留（D6）：含文件/行/跨界算子位置 |
| CS2J1320（LC0030） | Info（verbose） | 非 Enumerable/Queryable 宿主的提供方自定义扩展计数（Include 等），未触碰 |
| CS2J1340（LC0040） | Warning | residual 扫描发现应保留节点被改写/应展开节点仍绑定 Enumerable（§7.10），strict 下 Error |
| CS2J1350（LC0050） | Warning | 中间树 restore 失败（沿用各 pass 惯例） |
| CS2J1351（LC0051） | Error | 中间树 `dotnet build` 失败 |
| CS2J1352（LC0052） | Error | 中间树 `dotnet test` 失败 |

原则：①分流不明 = **保留 + 显式诊断**，永不猜 Enumerable（catalog 风险 12/17）；②规则展开自身的异常维持今天 skip+Warning 口径（`LinqSkipReason` 九类不动），避免借通道化改变既有行为；③所有诊断带文件/行，进 JSON 报告与链摘要。

---

## 7. 测试计划

### 7.1 Enumerable 过程式化全矩阵（回归等价性）

- **直接复用现有 167 个 `[Fact]/[Theory]`**（35 文件，见 §1.1）作为通道 rewriter seam 的行为基线：同一份输入在「E16 嵌入式」与「linq-channel 中间树产物再进转换器（E16 已抑制）」两路径下，过程式化片段（生成的 helper 名后缀、循环结构、捕获变量处理）做语义/规范化比对。
- 算子矩阵按 `KnownMethods` 全表补齐通道级用例：Where/Select/SelectMany/Cast/OfType/OrderBy/OrderByDescending/ThenBy/Join/GroupJoin/GroupBy/聚合（Sum/Average/Min/Max/Count/LongCount/Aggregate 双形态）/Any/All/First(OrDefault)/Last(OrDefault)/Single(OrDefault)/ElementAt(OrDefault)/Contains/SequenceEqual/Distinct(By)/Skip/Take/SkipLast/TakeLast/SkipWhile/TakeWhile/Chunk/Append/Prepend/DefaultIfEmpty/Concat/Union(By)/Intersect(By)/Except(By)/Reverse/AsEnumerable/ToList/ToArray/ToHashSet/ToDictionary。
- foreach rewrap 矩阵：直接 foreach over 链、`CanRewrapForeachVisitor` 全部 fail 条件（await/break/continue/return/goto/yield/嵌套 lambda，[CanRewrapForeachVisitor.cs:36-74](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/CanRewrapForeachVisitor.cs#L36-L74)）。

### 7.2 ★Queryable 保留矩阵（M3 核心）

- 方法链全算子：Where/Select/Join/GroupJoin/OrderBy/ThenBy/GroupBy/Skip/Take/Any/Count/First(OrDefault)/Single(OrDefault)/Distinct/Concat/Union/Reverse（在 Queryable 宿主存在的集合），每个用例断言中间树产物中调用仍**绑定 `System.Linq.Queryable`**（Roslyn 重解析 `ContainingType`）。
- query syntax 两源：`from x in queryable where … select …`（去糖后 Queryable 绑定保留）、`from x in enumerable …`（去糖后 Enumerable 并过程式化）。
- `let` / `into`（continuation）/多 from（SelectMany）/嵌套子查询/join-into 分组。
- Queryable 输入下 query expression 去糖计数与保留计数一致（desugar 照跑、rewrite 零触碰）。

### 7.3 Expression<Func<>> 实参矩阵

- 直接 lambda：`q.Where(x => x.A > 1)`、`q.OrderBy(x => x.Name)`、`q.Select((x,i) => …)`；
- 变量传入：`Expression<Func<T,bool>> p = x => …; q.Where(p);`；
- 表达式树**构造 API**：`Expression.Lambda<Func<…>>(Expression.Equal(…), …)`、`Expression.Property/Call/Constant` 手工建树——通道零触碰，节点构造序列改写前后逐节点同一；
- 树体内含用户方法调用/扩展方法（与 C19 EM-EXPRTREE 交叉）：保留前后 `MethodInfo` 同一性。

### 7.4 混合链与边界算子

- `q.Where(…).AsEnumerable().Select(…)`、`e.AsQueryable().Where(…)`、`q.Where(…).ToList().Select(…)`、`q.Where(…).AsQueryable()` 往返；v1 全部整链保留（LC0020 计数正确），并断言中间树可编译、运行时语义与源程序一致（AsEnumerable 客户端边界在 .NET 侧真实执行）。
- v2 切段若实施，再补「Enumerable 段展开 + Queryable 段不透明源」用例（本设计不承诺）。

### 7.5 自定义 query provider

- fixture 内手写最小 `IQueryable<T>` + `IQueryProvider`（**RecordingQueryProvider**：记录每次传入的 Expression，不依赖 EF Core NuGet 包）：断言过程式化零发生、provider 收到的表达式树形态与不经通道时逐节点一致。
- 提供方自定义扩展（静态类 `MyQueryableExtensions.Include`）：NonLinq 计数、零改写。
- 真实 EF Core：M0 仅对 EFCore/EFCore.Relational/EFCore.SqlServer 源码跑**分类器计数扫描**（不下转换、不进 CI 强依赖，catalog §5.1 L955）；fixture 中以「模拟 EF 形态」的最小 DbContext 抽象（DbSet 风格属性 + Recording provider）承担 CI 职责。

### 7.6 项目级 fixture（`tests/SampleLinqChannelSolution/`，Lib+App+Tests+sln，纳入 ProjectTests）

- **Lib**：Enumerable  LINQ 工具方法集（过程式化对象）；IQueryable 仓储接口 + RecordingQueryProvider 实现；Expression 树构造工具；混合链方法；查询语法两源。
- **Tests 工程（.NET 半区预言机）**：xUnit 行为断言——过程式化前后结果一致（排序/分组/join/聚合/延迟执行/抛异常时机）；Recording provider 断言 Queryable 链未被客户端化、Expression 形状一致。
- 断言：`Success`；路由统计精确（expanded/preserved/ambiguous 计数）；中间树 `.cs2j-linq-channel-src` 存在且 `dotnet build -c Release` 退出 0、`dotnet test -c Release` 通过；`cs2j-linq-channel-report.json` schema 字段齐；通道关闭时工程零触碰（字节复制）。

### 7.7 Release build/test 与全量回归

- `dotnet build -c Release`（CSharpToJavaConverter.slnx）+ Tests/ProjectTests/ConversionTests 三工程 `dotnet test -c Release` 全绿，存量红灯集不扩大。
- 幂等：通道产物作为源再跑一次通道 → 零改写、零新增 helper、报告计数归零（除 preserved 不变）。

### 7.8 .NET 半区等价性

Enumerable 用例的「源项目测试 vs 中间树项目测试」在同一套 xUnit 上结果一致（foundation plan §5.1）；重点钉过程式化历史高风险面：匿名投影（record 门控两条路径）、捕获变量流转、提取 helper 的静态上下文、nullable/值类型聚合、多重枚举副作用。

### 7.9 ★Queryable 保留断言（反射/符号检查，防回归闸门）

- **符号级**：对全部 Queryable 矩阵用例的中间树重编译，`GetSymbolInfo` 断言 `ContainingType == System.Linq.Queryable`，lambda 的 `ConvertedType` 仍为 `Expression<>`；计数断言「Queryable 节点被过程式化 = 0」。
- **运行时反射级**：RecordingQueryProvider 记录的 `Expression` 树与无通道基线逐节点比对（NodeType/Method/成员名），任何树形态差异即红。
- **负向回归**：构造 `n == null` 场景（残缺 metadata/未解析符号的 Queryable 风格调用）→ 断言产出 LC0010 且节点原样保留，**永不**出现 foreach 展开。

### 7.10 residual 闸门（AfterFiles 整树再编译）

①绑定 Enumerable 的可展开链残余（在支持矩阵内却未展开）= 0；②绑定 Queryable 的调用被改写 = 0（LC0040）；③Expression 引号内节点与基线序列同一；④提取的 `*_ProceduralLinqHelper` 只出现在发生 Enumerable 展开的类型中；⑤幂等（二次运行零差异）。

### 7.11 CLI 与一致性

- `preprocess --pass linq-channel` 单独可达；stub 期启用仍 CS2J9001（翻转前）；`--enable-pass/--disable-pass`、`--list-passes`（Implemented/默认关/目录/RunAfter 三锚）、preserve 目录、verify 失败退出码、strict 升级。
- [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 19 子序列与末位断言、[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) 22 stub 断言、`ProjectPreprocessorsConsistentTests` 目录断言、`ChainFactory_creates_an_adapter_for_every_implemented_pass` 自动覆盖新适配器。
- 双跑抑制（§5.3）：通道启用时 E16 统计为空且无二次 helper；通道关闭时 E16 统计与今天一致。
- [DualTargetCorpus](file:///d:/code/cs2j/tests/DualTargetCorpus) 增补 Enumerable/Queryable/混合链双目标用例。

---

## 8. 默认开启验收门槛

默认关落地；**全部**满足才评审翻 descriptor `defaultEnabled: true`：

1. **★三路分流判定 100% 正确（硬前提）**：§7.2/7.3/7.4/7.5 全矩阵通过；Queryable/Expression 站点误展开数 **= 0**，符号级 + 运行时双闸门常驻 CI；LC0011 在全部 fixture/sdk 扫描中零命中。
2. Enumerable 路径与 E16 行为等价：167 个存量测试零 diff（或差异逐项定性为修复并评审），§7.8 .NET 半区测试全绿。
3. 三工程 `dotnet build/test -c Release` 全绿、存量红灯不扩大；fixture build/test 通过；幂等成立。
4. M0 基线测量完成并回填：csharp-sdk 与 **EF Core 源码**的路由计数（Enumerable 链/Queryable 站点/Expression 站点/Ambiguous 站点密度），LC0010 命中站点全部完成人工分类，无未知形状静默透传（catalog 风险 17）。
5. §5.3 双跑抑制在 convert-project 全链路上验证（含指纹/增量：通道开关产生正确的一次性缓存失效与后续命中）。
6. 通道启用时主转换端到端冒烟：Enumerable 产物 Java 侧不退化，Queryable 保留站点的 Java 终态与现状（Stream 映射）一致或更优——**不要求** Java 提供方就位（D15），但要求通道不制造新的 Java 编译失败。
7. C19/C21 均为 stub 的当前拓扑下，LC-ORDER 顺序断言稳定（§3.2），且评审确认 C19/C21 翻转不需要改 C26 描述符（仅常量引用迁移）。

---

## 9. 里程碑

| 里程碑 | 内容 | 出口判据 |
|---|---|---|
| **M0 分流探针 + 基线测量** | 独立小工具/测试内实现 `LinqRouteClassifier` 原型（不接通道）：①核实 Queryable.* 符号 reduced 显示名与 `KnownMethods` 零交集、枚举 Enumerable/Queryable 同名算子全表；②量化项目管线 `n == null` fallback 命中率与真实 Queryable 误展开复现（构造最小复现钉死漏洞）；③csharp-sdk 路由计数；④拉 EF Core 源码跑同分类器计数（catalog §5.1）；⑤RecordingQueryProvider 可行性原型 | 基线数字回填本设计 §8；误展开最小复现存档；不改生产代码 |
| **M1 通道壳与报告** | LinqChannel/ 骨架（Options/Statistics/Diagnostic/Report/Plan）+ 适配器 + 注册表翻转（默认关，`EnableQueryableGate` 先行）；两阶段整树执行打通但**规则行为 = E16 镜像**（gate 只观测计数不拦截）；中间根 JSON 报告；verify 双开；fixture 骨架 | 通道产物与 E16 中间结果等价；`--list-passes`/独立入口可达；stub 计数/链序测试更新 |
| **M2 Enumerable 通道化** | seam 正式接入（gate 可切换）；Enumerable 全矩阵 + §7.8 等价性 + 167 测试双路径比对；双跑抑制（§5.3）；幂等/residual 的 Enumerable 部分 | Enumerable 站点在通道下与 E16 逐行为等价；Release build/test 全绿 |
| **M3 ★Queryable 门** | gate 强制开启并移除关闭路径；封堵 fallback（LC0011 不变量）；Queryable/Expression/混合链/自定义 provider 矩阵；RecordingQueryProvider fixture + 模拟 DbContext；query syntax 两源；residual 全门；strict 诊断 | **IQueryable 零误展开**；三门矩阵全绿；EF Core 计数扫描无未分类形状 |
| **M4 默认开 / 旧路径退役** | §8 门槛评审 → 翻默认开；独立转换器 PR 摘 E16 项目路径（§5.4）、`--no-linq-rewrite` 映射、报告切 schema v2、存量测试迁移；单文件是否接 gate（Q4）定稿；Java Queryable 提供方立项材料（计数/边界报告）交后续 | 默认链含 linq-channel；E16 项目代码删除且三工程全绿 |

---

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | **EF 画像错配 + IQueryable 盲区**（catalog 风险 17）：训练集 IQueryable 0 处、Expression 2 处，现有语料练不到 query pipeline；三门在真实 EF 代码上的形状覆盖未知 | catalog L936；tests 零 IQueryable 专项 | M0 必做 EF Core 源码计数扫描；RecordingQueryProvider + 模拟 DbContext 进 CI fixture；默认开门槛 1/4 硬锁 |
| R2 | **语法名 fallback 静默误展开**：[LinqRewriter.cs:796-844](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L796-L844) 在符号未解析时按名字猜 Enumerable，项目管线注释自述符号解析常失败 | 本设计 §1.3 已定位具体路径 | gate 前置使 fallback 对 LINQ 链不可达；LC0010 保守保留；LC0011 不变量 + 残缺 metadata 负向用例 |
| R3 | **表达式树共同禁区**（catalog 风险 8）：多个通道（C45/C19/本通道）都需要「树内跳过」，各写一份判定会漂移 | catalog L927；C02 已有 CS2J1110 先例 | `ExpressionTreeFacts` 单点实现，residual 做节点序列同一性断言；C45 落地时提升共享（§11） |
| R4 | **重写器 seam/搬迁回归**：3780 行第三方重写器、规则互相耦合（提取 helper/捕获分析/匿名消毒），167 测试之外仍可能有缝隙 | roslyn-linq-rewrite 血统；[LinqRewriter.cs:106-120](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L106-L120) 宽 catch | 不搬迁（D13）；gate 参数默认 null 保证 E16 零变化；M2 双路径等价比对；失败口径维持现状不扩大 |
| R5 | 混合链 v1 整链保留导致覆盖率回退（部分本可展开的 Enumerable 段被保留） | D6 保守策略 | LC0020 全部入报告可盘点；v2 切段独立设计；保留不产生错误代码（主转换现有 Stream 映射承接） |
| R6 | 双轨期双重 LINQ 处理或统计重复 | E16 与通道并存 | §5.3 单点抑制（以实际执行目录集为信号）；测试断言「通道启用时 E16 统计为空」 |
| R7 | **Java 半区无 Queryable 提供方**：保留后 IQueryable 今天仍映射 Stream，通道不直接产生「服务端执行」的端到端价值 | [TypeMappings.json:3697-3702](file:///d:/code/cs2j/config/TypeMappings.json#L3697-L3702)；java/ 无 Queryable 类 | D15 诚实范围声明；通道价值定位为止损（消除静默客户端化）+ 边界显式化；Java 提供方独立立项，M4 交付计数材料 |
| R8 | query over IQueryable 被去糖改变语法（query → 方法链），虽语义恒等但可能与下游（C19/主转换器 QueryExpressionTransformer）假设交互 | [LinqQueryDesugarer.cs:19-37](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs#L19-L37)；[QueryExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/QueryExpressionTransformer.cs) | desugar 是 C# 规范 lowering；.NET 半区 build/test 守门；M3 两源矩阵；Queryable 查询式残余由符号断言（不依赖语法形状） |
| R9 | 两阶段 + verify 的耗时（catalog 风险 11） | 每环一 restore 一 build/test | verify 可关；clean 工程零触碰；指纹缓存；无 LINQ 命中工程阶段 2 快检跳过 |
| R10 | LC-ORDER 与 C19 EM-ORDER 的跨设计次序耦合 | C19 在 goto 前、C26 在 goto 后；两者经注册边共存 | §3.2 已论证无环；顺序测试锁「C19 → goto → C26 → naming」；C19 翻转不改 C26 描述符（Q2 留评审） |
| R11 | 通道只在项目路径，单文件路径继续无门，文档/用户预期分叉 | §5.5 | Q4 定稿前文档明示；单文件接 gate 成本低（同 classifier），M4 可顺手收口 |

---

## 11. 并行协调

| 相邻工作 | 边界 | 顺序/依赖 |
|---|---|---|
| **C19 `extension-methods`（stub，姊妹设计已定稿）** | C19 以 **EM-LINQ 排除**让出 LINQ reduced 糖（host 集 Enumerable/Queryable 不选中），结论是**维持注册次序 C19 → C26**、不交换链序（C19 设计 §3.4 硬证据：LinqRewriter 结构性依赖 member-access reduced 形态，吃不下静态形态） | C26 RunAfter 保留 `extension-methods` 锚；★三路门在 reduced 形态上工作，C19 已确认不受影响；host 常量 v1 双份、C19 落地后提取共享；混合链上用户扩展节点策略（C19 §3.4-4）与本通道 D6 保守保留在 M3 做交叉用例；LC-ORDER 新增 goto 锚与 EM-ORDER 无冲突（R10） |
| **C21 `anonymous-type-lift`（stub，姊妹设计已定稿）** | C21 在 LINQ 前把匿名投影具名化；其 RunAfter=tuple、C26 已反向锚定 | C21 未落地时通道沿用 rewriter 既有 `object` 消毒/record 门控（[LinqRewriter.cs:632-665](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L632-L665)）；C21 落地默认开后，M4 对齐移除匿名分支（C21 §11 已把 LinqRewriter 匿名门控退役挂到 C26/M5）；transparent identifier 豁免两设计一致 |
| **E16 嵌入式 LINQ（现状）** | 通道化前唯一 LINQ 入口；项目路径 M4 摘除、单文件保留 | §5.3 双跑抑制是唯一并存期接线；M1-M3 不改 E16 默认行为；M4 摘除清单见 §5.4 |
| **C45 `closure-lift`（stub）/ C32** | 表达式树内 lambda skip 需要同一判定（catalog 风险 8 点名 C45） | `ExpressionTreeFacts` 按共享组件设计，v1 住 LinqChannel/，C45 翻转时提升到 Core 公共位置（如 `Core/Preprocessing/ExpressionTreeFacts.cs`），两处引用；C02 的 CS2J1110 判定届时一并归并评审 |
| **Java Stream / Queryable 映射（转换器 + compat）** | `IQueryable\`1→Stream` 现状、`PreferStreamApi` 死开关、compat 有 Expression.java 无 Queryable | 通道 v1 不改任何 Java 映射；M0 计数报告作为 Java 查询提供方立项输入；未来若新增 Queryable compat/映射，其输入契约 = 本通道 preservedChains 形态 |
| **C08/E17 java-keywords（链尾）** | Queryable/Expression 保留名是 BCL 名，通道产物中的标识符撞 Java 关键字由 C08 兜底 | LC-ORDER 保证 C26 在 C08 之前；末位锁定测试保持 |
| **E15 `eliminate-goto`（已实现）** | 纯语法；新增直接 RunAfter 锚 | 见 §3.2 LC-ORDER；M2 增「goto 含 LINQ 方法」组合用例（goto 先结构化、LINQ 后展开） |

**热文件清单**（合并冲突面）：[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、[PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)、[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs)、[Program.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs)（§5.3 一处 + M4 多处）、[LinqRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs)（seam 小 hunk，C21 M5 也会碰同文件——改动区域正交：gate 在入口、匿名消毒在辅助方法，合并时人工核对）、M4 才碰的 [ProjectPasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs)/[SingleFilePasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs)。

---

## 12. Worktree 分配

只写不执行（本设计不执行任何 git 写操作；以下命令在实施 PR 中由执行者运行）：

- 分支：`feature/preproc-c26-linq-channel`
- 目录：`.worktrees/preproc-c26-linq-channel`（`.worktrees/` 已在 [.gitignore](file:///d:/code/cs2j/.gitignore) 忽略）
- base：`953293b9`（`docs: update doc base commit`，已核实存在：`953293b908e9143437c45f287fb4277e890c398e`）

```bash
git worktree add .worktrees/preproc-c26-linq-channel -b feature/preproc-c26-linq-channel 953293b9
```

实施顺序建议：M0 探针（可在同一 worktree 的早期提交，纯测试/工具形态）→ M1 注册表翻转与通道壳 → M2 → M3 → §8 门槛评审后 M4 另开转换器 PR。

---

## 附：关键开放问题（评审决策点）

1. **Q1（LC-ORDER）**：C26 追加 `eliminate-goto` 锚（固化今天「goto 先、LINQ 后」事实顺序）还是只靠注册顺序 tie-break？本设计推荐追加（§3.2）。
2. **Q2（host 常量归属）**：Enumerable/Queryable host 集 v1 在 C19、C26 双份硬编码，何时提取共享常量？建议 C19 翻转时提取，本设计仅挂注释交叉引用。
3. **Q3（混合链切段）**：D6 v1 整链保留，v2 是否做 AsEnumerable/物化边界切段展开？取决于 M0/M3 在真实语料观察到的 mixedChainsPreserved 密度。
4. **Q4（单文件 gate）**：M4 是否把同一 classifier 接进 `SingleFileLinqDesugarPass`，使单文件直转同样获得 IQueryable 零误展开保证（成本低，需同步单文件测试期望）。
5. **Q5（Queryable 诊断严格度）**：LC0010（符号未绑定的 LINQ 风格调用）默认 Warning 还是直接 Error？Warning 与今天 skip 口径连续，Error 更符合风险 12 原则；建议默认 Warning、`--strict` Error，sdk/EF 计数复盘后再定默认。
