# MSAGL → TypeScript 错误分析记录

目标：`E:\agl-master\GraphLayout\MSAGL`（只读）经 cs2j 转出的 TypeScript 工程全模块 `npx tsc --noEmit` 零错误。
每次迭代：cs2j 改动 → `dotnet build` → 全量重转（保留 node_modules）→ 收集全部 TS 诊断 → 按根因修复 → `dotnet test` 全绿 → 单根因单提交。

| 轮次 | 错误总数 | 本轮修复根因 | 涉及文件 | 提交 hash |
|------|---------|------------|---------|----------|
| 1 | 91148 → 2751 | MSBuild 零引用 → SolutionLoader 注入本机 ref pack | src/CSharpToJava.Core（SolutionLoader 相关） | b1b5e475 |
| 2 | 2751 → 2399 | Java 风味泛型类型映射缺失 | TypeMappings.json 相关 + NpmProjectPlan.GenerateGlobalTypeStub | f83831ec |
| 3 | 2399 → 2381 | FQCN 兜底误切泛型实参 | TsExpressionWriter / RegisterImports | 8cbc0ab1 |
| 4 | 2381 → 593 | raw 方法体 Java 文本未归一化 | TsRawStatementNormalizer / TsMemberWriter / TsExpressionWriter | 94bed66b |
| 4b | — | 重转保留 node_modules（省 npm install） | src/CSharpToJava.CLI/Program.cs | f1d8482d |
| 6 | 42 → 35 | raw 文本引用 cast 误判：控制流条件 `(X)` 被当强转丢弃（`if return true;`） | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests / TsEmitterIntegrationTests）+ 本文件 | e9677465 |
| 7 | 35 → 26 | 泛型声明/经典 for 头 let 化：类型正则吞掉 lambda `=>` 的 `>` | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests +4）| a5ff3437 |
| 9 | 20 → 15 | 构造器委托实参在成员位（`.` 后）被朴素替换 → 仅值位替换 | src/CSharpToJava.Core/TypeScript/Rewriters/TsOverloadDisambiguationRewriter.cs + tests/CSharpToTypeScript.Tests（TsOverloadRewriterTests +2）| 1418f93b |
| 8 | 26 → 20 | try-with-resources 无 catch/finally 时裸 `try {`（TS1472）→ 降级为块作用域 | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests +4）| 1daf46cb |
| 10 | 15 → 10 | `Type[]::new` 方法引用 FQCN 接收者回扫残缺（`.` 段未纳入） | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests +3）+ 本文件 | ec19886a |
| 11 | 10 → 8 | CRLF raw 文本尾注释声明未 let 化：declPattern 尾组吞不下行尾 `\r` | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests +6）+ 本文件 | bacd8673（代码+测试被并发会话提交扫入，见注） 
| 12 | 8 → 6 | `boolean[]` 声明未 let 化：TryMapType 数组后缀恒等映射返回原串、白名单不认 | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests +1）+ 本文件 | 07acb643（代码+测试被并发会话提交扫入，见注） |
| 13 | 6 → 4 | `new T[][]`（空括号）数组创建在 `[` 字面量前残留前缀：字面量模式贪婪体跨整行多声明只剥首前缀 | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests +3）+ 本文件 | 2ce395e8（代码+测试被并发会话提交扫入，见注） |
| 14 | 4 → 2 | out 形参 lambda 捕获 holder raw 文本：`@SuppressWarnings` 注解未剥 + cast lookahead 不认 `[`；剥注解+cast 后 let 化 | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests +3 / TsRefOutParamTests +1）+ 本文件 | 6b02774b |
| 15 | 语法 2 → 0；语义波 23043（预期转折点） | E 族：LINQ 重写产物方法引用形态未做 TS 归一化——空菱形构造剥离 + 表达式接收者 `::` 改写 + 裸 `HashSet` 局部映射 | src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs + tests/CSharpToTypeScript.Tests（TsRawStatementNormalizerTests +8 / TsRawBodyNormalizationTests +1）+ 本文件 | 本轮提交（见下） |
| 16 | 23043 → 22171 | raw 文本裸成员引用缺前缀（TS2663 3376 / TS2662 1600 同根：实例缺 `this.`、静态缺 `类名.`） | 新增 TsMemberContext.cs；TsRawStatementNormalizer.cs（新最终 pass NormalizeBareMemberReferences）；TsMemberWriter.cs / TsStatementWriter.cs / TsExpressionWriter.cs / TsTypeWriter.cs / TypeScriptEmitter.cs（成员上下文接线）；tests （TsRawStatementNormalizerTests / 新增 TsRawMemberContextTests / 3 例断言更新）+ 本文件 | 3cf39918 |
| 17 | 22171 → 15013 | 非自动属性访问器 getter/setter 形态调用点改写（项目级注册表 + raw/结构化 lambda 参数推断） | TsPropertyReconstructionRewriter.cs（ProjectAccessorRegistry / CollectProjectContexts / 访问器调用改写缺口 9 项 + lambda 族）/ TsEmitSupport.cs / ProjectTsPasses.cs / TsPropertyReconstructionRound17Tests.cs | cabeb448（主体被并发会话扫入，内容完整在 HEAD）+ 本轮提交（见下） |
| 18 | 15013 → 13278 | ①构造器重载调用点未改 Create$N 工厂（TS2673 769→238）；②FQCN 静态方法调用接收者未缩短（TS2304 'Microsoft' 1791→146） | ① ObjectCreationTransformer.cs / TsOverloadDisambiguationRewriter.cs + TsCtorFactoryCrossFileTests.cs；② TsRawStatementNormalizer.cs + TsRawStatementNormalizerTests | 42f4dffe + 7fccb22c |
| 19 | 13278 → 7502 | ①无初始化器引用类型字段宽化 `T | null`（TS2531 2922→15 / TS18047 771→116 / getter 内 TS2322）；②compat Java 集合 API 面缺口（TS2339 CSharpList .get/.set 等 328→0、CsKeyValuePair getKey/getValue 224→0、TS2693 new CSharpList/Stack/Dictionary 130→26） | ① src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs（EmitField）+ TsNullAssertionRound19Tests.cs + 3 例既有断言更新；② NpmProjectPlan.cs（global.d.ts 桩 Array 接口合并 + const 合并）/ TsRawStatementNormalizer.cs / TsExpressionWriter.cs（new CSharpList 改写）+ compat ArrayCompat.ts + CsKeyValuePair.ts + index.ts + TsCollectionCompatRound19Tests.cs | b52ea407 + 4424932a + fd623fe3 + cc02d254（另：并发会话先期提交 b289f65f 已收割 TS2551 FQCN 接收者族，纳入本轮起点 12633） |


> Round 11 注：A4 修复代码与 6 例测试被并发会话的 git add 整文件暂存扫入 
> bacd8673（其提交主题为 WeakReference 泛型映射）；行尾形态测试（`\r\r\n`）与
> 本节记录被扫入 e3494d79（其提交主题为 IEnumerator 冲突）。两提交均覆盖在
> 本轮验证链（全量重转 + tsc 10 → 8）之内。

> Round 13 注：A3 修复（空括号维度前缀剥离）与 3 例测试、临时复现工程 .scratch-a3 被并发会话的
> git add 整文件暂存扫入 2ce395e8（其提交主题为 Span/ArrayPool compat 修复）；A3 内容完整在 HEAD。
> 本节记录与表格行由独立提交收录，.scratch-a3 由本轮提交删除。

> Round 12 注：A2 修复（HasAcceptedIdentityArrayBase）与 1 例测试被并发会话的
> git add 整文件暂存扫入 07acb643（其提交主题为 reflection flag enums 映射）；
> 本节记录与表格行由独立提交收录。

## Round 15 — E 族：LINQ 重写产物方法引用形态 TS 归一化（语法清零，语义波涌现）

- **C# 构造**：`double l = u.Union(v).Count() - u.Intersect(v).Count();`（LayoutAlgorithmHelpers.cs:106）的
  Intersect 分支 LINQ 重写产物：`StreamSupport.stream(u.spliterator(), false).filter(new HashSet<>(v)::contains).size()`。
- **错误 TS 输出（修复前，本轮起点）**：`...filter(new HashSet<>(v)::contains)...` —— TS1005/TS1135 两个语法错误
  （全库仅存；TS1099 等空菱形类诊断在 native tsc 7.0.2 下被语法错误抑制，属语义类，见环境注记）。
- **正确 TS 输出**：`...filter((x) => new CSharpHashSet(v).contains(x))...`，整行与预期形态一致。
- **根因**：LINQ 重写（InvocationExpressionTransformer 的 Intersect/Except 分支，`BuildSetExprFromOther`）产出
  Java 方法引用形态 raw 文本，`TsRawStatementNormalizer` 未归一化，三个子缺陷同根因：
  1. 空菱形 `new X<>(` 未剥（`NormalizeGenericMethodInvocations` 只认 `.<T>method(`）；
  2. `TryRewriteMethodReference` 只认标识符/`this`/`Type[]` 接收者，表达式接收者 `new HashSet(v)::contains` 直通；
  3. 裸 `HashSet` 无全局桩（global.d.ts 只有 `CSharpHashSet<T>`）。
- **修复位置**：`src/CSharpToJava.Core/TypeScript/TsRawStatementNormalizer.cs`：
  - 新 pass `NormalizeDiamondConstructors`（置于方法引用归一化之前）：`\bnew\s+类型<>\(` → `new 类型(`；仅空菱形，
    `new HashSet<Node>(v)` 不误伤；点分 FQCN 覆盖。附带清掉全部 raw 文本 `new CSharpList<>(`/`new ObjectHolder<>()`，
    本应涌现的约 48 个 TS1099 一并消失（验证：重转后全库 `<>` 0 处、`::` 0 处）。
  - `TryRewriteMethodReference` 扩展表达式接收者：`new X(args)::m` / `(expr)::m` / `name(args)::m` /
    嵌套调用 / 成员链 `a.b().c(x)::m` → `(x) => expr.m(x)`。参数个数语义论证：Java 有界接收者方法引用把函数式
    接口实参传给实例方法；LINQ 重写只产单参 Predicate（`Set.contains`）形态，故用单参 `(x) =>`（任务预期形态）；
    spread `(...args) =>` 对固定参数个数实例方法在 strict 下产生 TS2556，单参形态对 0 参方法运行时只是多余实参
    无副作用。接收者回扫按 `()[]{}` 平衡，接收者切片含注释/引号不平衡时直通降级。
  - `RawCompatCollectionCtorNames` 局部映射（`HashSet` → `CSharpHashSet`）。选 (a) 的理由：raw 文本无类型映射
    回调；(b) 全局桩只覆盖类型位，值位 `new HashSet(v)` 仍是 TS2693，且影响其它生成物；(c) 改 LINQ 源头影响
    Java 目标（`java.util.HashSet` 导入）。
- **测试**：`TsRawStatementNormalizerTests` +8（菱形剥离+实参守卫、new/括号/调用/嵌套调用/成员链/带实参构造
  接收者、既有标识符/数组接收者形态守卫）；`TsRawBodyNormalizationTests` +1 端到端（Intersect 样本全链：
  断言 `new CSharpHashSet(v).contains`、无 `::`/`<>`，global.d.ts 桩用 type+const 同名声明合并覆盖值位，
  strict tsc 编译通过）。先红后绿（红：7 例失败 + E2E 编译失败迭代 3 次）。
- **验证链**：重建 → 删目录全量重转 → 独立 tsc：**语法错误 0**（67 个唯一错误码全部为语义类）；语义错误
  **23043** 条（预期转折点：TS 7.0.2 语法清零后语义诊断涌现）。`<>`/`::` 全库 0 处。CLI 转换 510/510 成功、
  自身诊断 errorCount 0（退出码 1 来自 CLI 后置 tsc 校验的语义波）。全量 `dotnet test` 绿：7 + 570 + 2455
  （CSharpToJava.Tests 较上轮 +3 为并发会话新增；CSharpToTypeScript.Tests +9 为本轮）。
- **语义波初步分类（23043 条，67 个错误码）**：
  - 成员访问：TS2551（5048，属性不存在/大小写）、TS2339（4092）、TS2349（71）、TS2393（36，重复实现）；
  - 名称解析：TS2663（3376，缺 `this.`）、TS2304（3155，未定义名：Stream/StreamSupport/MapHelper/StringBuilder 等
    Java 全局）、TS2662（1600，静态名缺限定）、TS2552（162）、TS2503（156）、TS2693（370，类型别名当值用）、
    TS2300/TS2307/TS2448 等；
  - 空值/null：TS2531（783，可能 null 解引用）、TS2322（713，null 赋值）、TS2345（551）、TS18047（441）、
    TS18046（299）、TS2532/TS18048/TS2721 等；
  - 隐式 any：TS7006（262）、TS7005（65）、TS7019（36）、TS7022/TS7034；
  - 参数个数：TS2554（196）、TS2556（17）；
  - 其它：TS2673（769）、TS2367（126）、TS2488（123）、TS2741/TS2739（缺属性）、TS2683/TS2684（this 推断）、
    TS2415（继承不兼容）等。
- **并发干扰**：本轮起点 HEAD `076e5adb`；期间并发会话提交 `b6e1798b`（IList 三重实现擦除冲突修复）并留下新的
  未提交改动（IdentifierExpressionTransformer.cs / config/TypeMappings.json / Java compat 文件）。其未提交改动
  曾致 `TsNumericSemanticsTests.Const_UInt_MaxValue...` 短暂失败，随后并发会话更新后全绿；用独立 worktree
  （HEAD+本轮 normalizer）验证该失败与本轮改动无关。并发未提交改动未纳入本轮提交。

## Round 13 — `new T[][]` 空括号数组创建在 `[` 字面量前残留前缀

- **C# 构造**：`double[,] XX = { { 1, 2 }, ... }, YY = { { 0.07, ... } };`（ProcrustesCircleConstraint.cs:212-213，`#else` 分支）。
- **错误 TS 输出**：`YY: number[][] = new double[][] [ [ 0.07, 2.62 ], ... ];`（TS1011 ×2，ProcrustesCircleConstraint.ts:118）。
- **正确 TS 输出**：`YY: number[][] = [ [ 0.07, 2.62 ], ... ];`。
- **根因**：`NormalizeArrayLiterals` 的数组字面量模式体为贪婪 `(?<body>\{[^\r\n]*\})`，单行多声明
  `double[][] XX = new double[][] { ... }, YY = new double[][] { ... };` 被整体命中（首个 `{` 到行末最后一个 `}`），
  花括号全部转方括号、但只剥掉第一个 `new double[][]` 前缀；`YY` 残留 `new double[][] [ ... ]` 形态——
  空括号维度不满足维度模式 `\[(?<dim>[^\]]+)\]`（非空）、方括号体不满足字面量模式（要求 `{...}`），两不沾。
- **修复位置**：数组字面量 pass 尾部新增空括号前缀剥离：`new TYPE(?:<>)?(?:[])+` 后紧跟 `[` 字面量时剥前缀。
  `(?:[])+` 要求至少一对空括号——`new double[2][]`（非空维度，已被维度 pass 转 `new Array(2)`）与
  `new String[] { ... }`（花括号体，被字面量 pass 消费）均不受影响。
- **测试**：`TsRawStatementNormalizerTests` +3（多声明两前缀全剥、`new double[] [ ... ]`/`new double[][] [ ... ]`
  单字面量剥前缀、`new double[2][]` 守卫走 `new Array(2)`）。
- **验证链**：重建 → 删目录全量重转 → 独立 tsc 6 → 4；剩余 4 错误为原 6 严格子集（E ×2、F ×2），无新增。
  全量 `dotnet test` 绿（7 + 557 + 2449；首轮偶发 1 例顺序相关失败，重跑全绿）。

## Round 14 — out 形参 lambda 捕获 holder raw 文本（注解未剥 + cast lookahead 不认 `[`）

- **C# 构造**：`HashSet<Node> addedNodes;` + `addedNodesByCluster.TryGetValue(cluster, out addedNodes)`
  （Relayout.cs:144-146），`addedNodes` 被 lambda 捕获（`newGraph.Nodes.Where(v => !addedNodes.Contains(...))`）
  且被外部重赋 → 声明后追加 lambda 捕获 holder。
- **错误 TS 输出**：`@SuppressWarnings("unchecked") CSharpHashSet<Node>[] _addedNodes = (CSharpHashSet<Node>[]) [ null ];`
  （Relayout.ts:106，TS1146/TS1005 ×2）。
- **正确 TS 输出**：`let _addedNodes: CSharpHashSet<Node>[] = [ null ];`。
- **根因链**（同源：`BuildLambdaCaptureHolderDeclaration` 的 raw Java holder 文本未归一化）：
  - ① `declPattern` 类型组要求行首即类型（`^` 锚定），`@SuppressWarnings("unchecked")` 注解导致整行不匹配；
  - ② `NormalizeReferenceCasts` lookahead `(?=[A-Za-z_(])` 不认 `[`：数组字面量 pass 已把
    `new Object[] { null }` 转 `[ null ]`，`(CSharpHashSet<Node>[]) [ null ]` 的 cast 因后继 `[` 不被剥；
  - ③ 剥注解+cast 后 `CSharpHashSet<Node>[]` 大写开头 + 数组后缀，`LooksLikeClassType` 放行，
    声明 let 化无需额外改动（`TryMapType` 数组后缀递归映射回拼 `[]`）。
- **修复位置**：`declPattern` 行首追加可选注解组 `(?<ann>@[A-Za-z_$][\w$]*(?:\([^)]*\))?[ \t]*)?`
  （仅吞本行水平空白，不跨行；非声明形态整行不匹配时原样直通）；`castPattern` lookahead 增 `[`
  （`(?=[A-Za-z_(\[])`）。管线顺序不变：`NormalizeRawDeclarations`（13）→ `NormalizeArrayLiterals`（16）
  → `NormalizeReferenceCasts`（17），let 化产物经数组字面量 pass 再剥 cast。
- **测试**：`TsRawStatementNormalizerTests` +3（注解+cast+let 化全链、`(X[]) [ ... ]` cast 剥离、
  注解非声明行直通守卫）；`TsRefOutParamTests` +1 端到端镜像 Relayout（TryGetValue out 形参 +
  lambda 捕获 → 断言 `let _addedNodes: CSharpHashSet<number>[] = [ null ];`、无 `@SuppressWarnings`、
  无 `(CSharpHashSet` 残留；不跑 strict tsc——见环境注记）。先红后绿（红：3 例失败，实际输出为
  未归一化 raw 文本）；全量 `dotnet test` 绿（7 + 561 + 2452，Java 目标无回归；CSharpToJava.Tests
  较上轮 +3 为并发会话新增用例）。
- **验证链**：重建 → 删目录全量重转 → 独立 tsc 4 → 2（实测；移除 F 族 Relayout.ts:106 两个错误，
  剩余 E ×2 为原 4 严格子集，无新增）。

> **环境注记（TS 7.0.2 原生编译器）**：实测当程序内存在任何语法错误时，`npx tsc --noEmit` 只报告
> 语法诊断、抑制全部语义诊断（探针验证：无语法错误的文件中的未定义名 `XYZUndefinedName123` /
> 类型不匹配均不报，语法错误照报）。因此各轮次"错误全为语法错误"的观测口径一致；语法清零后预计
> 涌现一批语义错误（`StreamSupport`/`Stream` 全局名未声明、`[ null ]` strict null 赋值 TS2322、
> 字段访问缺 `this.`、`CsSet.contains(...) == 0` boolean/number 比较等），属后续轮次范围。
> E2E 测试因此只断言文本形态，不跑 strict tsc。

## Round 12 — `boolean[]` 恒等映射数组声明未 let 化

- **C# 构造**：`bool[] enqueueed = new bool[graph.NodeCount];`（ConnectedComponentCalculator.cs:8）。
- **错误 TS 输出**：`boolean[] enqueueed = new Array(graph.getNodeCount());`（TS1011/TS1005 ×2）。
- **正确 TS 输出**：`let enqueueed: boolean[] = new Array(graph.getNodeCount());`。
- **根因**：`TryRewriteDeclarators` 恒等检查对 `boolean[]` 命中——`TryMapType` 数组后缀递归映射 `boolean`→`boolean` 返回原串 `boolean[]`，`LooksLikeClassType` 为 false，且 `boolean[]` 不在白名单 → 拒改。
- **修复位置**：恒等检查追加 `HasAcceptedIdentityArrayBase`：剥 `[]` 后缀按基类型判定（白名单名或类名放行，`[]` 后缀由 mapType 回拼）；`int[]/String[]`（非恒等映射）行为不变，未知小写基类型（`quux[]`）仍拒改。
- **测试**：`TsRawStatementNormalizerTests.Array_Type_With_Whitelisted_Identity_Element_Is_Let_ified`（`boolean[]` 单维 + `boolean[][]` 多维）。
- **验证链**：重建 → 删目录全量重转 → 独立 tsc 8 → 6；剩余 6 错误为原 8 严格子集（A3 ×2、E ×2、F ×2），无新增。全量 `dotnet test` 绿（7 + 554 + 2443）。

## Round 1 — MSBuild 零引用 → 注入本机 ref pack

- **C# 构造**：MSAGL 工程依赖完整 BCL / NuGet（System.Drawing 等），MSBuild 未解析出任何引用程序集。
- **错误 TS 输出**：无法打开引用 / 类型缺失导致 9 万+ 错误。
- **正确输出**：引用注入后诊断归零。
- **根因**：SolutionLoader 解析引用为空时未提供兜底。
- **修复位置**：cs2j SolutionLoader 注入本机 ref pack；MSAGL 诊断 0，错误降至 2751。

## Round 2 — Java 风味泛型类型映射缺失

- **C# 构造**：`Class<?>`、`Map.Entry<K,V>`、`CSharpGenericIterable<T>`、`java.util.List<T>`、`Map<K,V>` 及项目 FQCN 出现在 TS 类型位。
- **错误 TS 输出**：`Class<?>` 等非法类型名（TS1005/TS2318 等）。
- **正确输出**：`any`、`CsKeyValuePair<K,V>`、`Iterable<T>`、`Array<T>`、`CSharpDictionary<K,V>`、FQCN → 末段。
- **根因**：TS 类型映射表缺 Java 风味条目。
- **修复位置**：类型映射 + `NpmProjectPlan.GenerateGlobalTypeStub` 增 `CSharpStack`/`CSharpICollection`/`CSharpGenericComparer`/`CsKeyValuePair` 全局别名。错误降至 2399。

## Round 3 — FQCN 兜底误切泛型实参

- **C# 构造**：`SymmetricTuple<Point>` 等嵌套泛型实参中的 FQCN。
- **错误 TS 输出**：FQCN 末段截取把 `SymmetricTuple<...Point>` 切坏成 `Point>`（错误数一度暴涨 15262）。
- **正确输出**：`SymmetricTuple<Point>` 完整保留。
- **根因**：FQCN 兜底按 `>` 截断，未感知泛型深度。
- **修复位置**：嵌套实参 compat 导入改用 `Symbol|module` 编码，`RegisterImports` 解析；本轮全量重转 2381。

## Round 4 — raw 方法体 Java 文本归一化

- **C# 构造**：方法体 / 字段初始化器中的 Java 风味直通文本：`double factor = 1.5f;`、经典 for 头、`new String[] { "a", "b" }`、`(Iterable<Edge>)(Iterable<?>)(expr)` 引用 cast、`new Microsoft.Msagl.Core.Geometry.Point(...)` FQCN new 表达式。
- **错误 TS 输出**：`double factor = 1.5f;`（TS1434）、`for (int i = 0; ...)`（TS1005）等。
- **正确 TS 输出**：`let factor: number = 1.5;`、`for (let i: number = 0; ...)`、`["a", "b"]`、去 cast、`new Point(...)`。
- **根因**：`TsMemberWriter.AppendMethodBody/AppendArrowBody` 原样输出 raw body；`JavaRawExpression` 在 `ToInlineString` 中原样输出绕过归一化；`EmitLiteral` 未剥 float 后缀。
- **修复位置**：
  - `TsRawStatementNormalizer`：新增 float 后缀、数组字面量、引用 cast 剥除、FQCN 表达式归一化；声明 let 化支持多变量/无初始化/泛型/数组/FQCN 类型；经典 for 头重写；`Normalize` 增 `normalizeChars` 开关（raw 表达式保留单引号字符字面量）。
  - `TsMemberWriter`：raw body / arrow body / 字段初始化器接入 `Normalize`。
  - `TsExpressionWriter`：`EmitLiteral` 剥 float 后缀；`EmitNew` 加类型映射；raw 表达式接入 `Normalize(normalizeChars:false)`。
- **测试**：`TsRawBodyNormalizationTests`（4 例）、`TsRawStatementNormalizerTests` +10 例；全量 `dotnet test` 绿（515+2424）。

## Round 5 进行中 — 当前 593 个错误全为语法错误

注意：TS 7 原生编译器在项目存在语法错误时只报语法错误、抑制语义错误；语法清零后语义层（未定义名、类型不匹配等）会浮现，需继续迭代。

按根因分类（文本出现次数为全库统计）：

| 根因 | 规模 | 错误码 |
|------|------|--------|
| 方法引用 `::`（`this::x`、`X[]::new`、`Integer::intValue`、`Runnable::run`） | ~103 处 | TS1005/TS1135/TS1128 |
| Stream 链（`Stream.concat`、`Arrays.stream`、`.stream()`、`.toArray(X[]::new)`、`IntStream.range(...).boxed()`） | ~144+ | TS1109/TS1005/TS1135 |
| Java 数组维度创建 `new T[n][]` / `new T[n]` | 45 处 | TS1011 |
| 声明未 let 化：尾 `//` 注释、FQCN 类型、`[]` 类型、嵌套泛型 | 37+ | TS1434/TS1005 |
| `catch (Exception __e)` 类型化捕获 | 10+ | TS1128/TS1005 |
| 增强 for 集合为带点方法调用（`for (T v : a.b.c())`） | 多处 | TS1005 |
| `synchronized (this)`、`0.0d`、`self.new X(...)`、`self.0`、`if return true;`、`Collections.<T>emptyList()`、Java lambda `->` | 少量 | 混合 |


## Round 6 — 引用 cast 归一化误删控制流条件

- **C# 构造**：`IEnumerator<T>` 实现类的迭代器桥接（`AddIteratorBridgeMethods`）生成 raw 方法体
  `if (_iteratorHasNext) return true;`（MSAGL 中 RBTreeEnumerator / PolylineIterator / PointNodesList /
  EmptyEnumerator / NetworkSimplex / PredEnumerator / SuccEnumerator 7 个类）。
- **错误 TS 输出**：`if return true;`（条件被整体删除）→ `error TS1005: '(' expected.`（7 处）。
- **正确 TS 输出**：`if (_iteratorHasNext) return true;`。
- **根因**：`TsRawStatementNormalizer.NormalizeReferenceCasts` 的 cast 正则
  `\(\s*(?<type>[A-Z_$]...)\s*\)\s*(?=[A-Za-z_(])` 把 `if (X) return/throw ...;` 中括号条件
  `(_iteratorHasNext) return` 误判为引用强转 `(Type) expr` 并丢弃（`_`/大写开头变量名 + 后随
  标识符即命中），且 `if` 条件本身位于控制流关键字之后，任何 `if (Foo) ...` / `while (Foo) ...`
  形态的 raw 语句都会中招。
- **修复位置**：`NormalizeReferenceCasts` 增两重守卫：
  ① `IsControlFlowCondition`——`(` 前一个词是 `if/while/switch/for/catch/synchronized` 时
  括号为条件表达式，不剥；
  ② `IsFollowedByStatementKeyword`——`)` 后紧跟 `return/throw/if/...` 等语句关键字时不剥。
  真正的表达式强转（`(Node) n`、`(Iterable<Edge>) es`、`f((Point) p)`）仍按原语义剥除。
- **测试**：`TsRawStatementNormalizerTests` 新增 3 例（条件保留 ×3 形态 + 真强转仍剥 ×3）；
  `TsEmitterIntegrationTests` 新增端到端 1 例（IEnumerator<int> 桥接输出含完整条件）。
- **验证**：`dotnet test --configuration Release` 全绿（7 + 534 + 2432）；全量重转后
  `npx tsc --noEmit` 42 → 35（恰好移除 7 个 TS1005，无新增错误；剩余 35 个仍全为语法错误）。
## Round 7 — 泛型声明/经典 for 头 let 化：类型正则吞掉 lambda `=>` 的 `>`

- **C# 构造**：泛型类型局部声明与经典 for 头，初始化器含 lambda：
  - `RBNode<ConeSide> rbNode = rightConeSides.FindLast(s => PointIsToTheRightOfSegment(site, s));`（LineSweeperForPortLocations.cs:533）
  - `Set<SymmetricTuple<Point>> segments = new Set<SymmetricTuple<Point>>(edges.Select(e => new SymmetricTuple<Point>(...)));`（GreedyNodeRailLevelCalculator.cs:88）
  - `Stream<LgNodeInfo> neighb = Enumerable.OrderBy(...);`（LgInteractor.cs:1985）
  - `for (RBNode<LinkedPoint> node = tree.FindFirst(p => left <= p.Point.X); ...; node = tree.Next(node))`（LinkedPointSplitter.cs:67/73）
- **错误 TS 输出**：以上声明/for 头原样保留为 Java 形态（`RBNode<ConeSide> rbNode = ...`），tsc 报 TS1005/TS1011（共 9 个错误，其中 LinkedPointSplitter 两个 for 头各 3 个）。
- **正确 TS 输出**：
  - `let rbNode: RBNode<ConeSide> = rightConeSides.findLast(s => pointIsToTheRightOfSegment(site, s));`
  - `let segments: Set<SymmetricTuple<Point>> = new Set<...>(Enumerable.select(edges, e => ...));`（类型注解内 FQCN 实参经映射缩短为 `Point`）
  - `let neighb: Stream<LgNodeInfo> = Enumerable.orderBy(...);`
  - `for (let node: RBNode<LinkedPoint> = tree.findFirst(p => left <= p.getPoint().X); node !== null && ...; node = tree.next(node))`
- **根因**：`TsRawStatementNormalizer` 声明 let 化的类型正则用 `(?:<[^;\r\n]*>)?` 匹配泛型后缀，`[^;\r\n]*>` 贪婪吞到初始化器里 lambda `=>` 的 `>`：`RBNode<ConeSide> rbNode = f(s =>` 整段被吞进 type 组，`TryRewriteDeclarators` 收到非声明文本（`pointIsToTheRightOfSegment(site, s))`）而放弃改写；`NormalizeRawDeclarations` 与 `TryRewriteClassicForHead` 两处同病。嵌套泛型（`Set<SymmetricTuple<...>>`）与 FQCN 实参同样被 `=>` 截断。
- **修复位置**：`TsRawStatementNormalizer.cs` 新增平衡尖括号泛型后缀模式 `GenericTypeSuffixPattern`（.NET 平衡组 `(?<Open><)|(?<-Open>>)`，内容排除 `;`/换行），只匹配类型自身的闭合 `>`（含一层嵌套泛型），替换 `NormalizeRawDeclarations` 与 `TryRewriteClassicForHead` 两处正则；lambda 完整保留在初始化器内。
- **测试**：`TsRawStatementNormalizerTests` 新增 4 例（泛型声明+lambda、嵌套泛型 FQCN 声明+lambda、`Stream<T>` 声明+lambda、经典 for 头泛型+lambda），先红后绿；全量 `dotnet test` 绿（7 + 538 + 2432）。
- **验证**：全量重转后 `npx tsc --noEmit` 35 → 26（移除 9 个错误，无新增）。

## Round 8 — try-with-resources 无 catch/finally：裸 `try {` 降级为块作用域

- **C# 构造**：`using` 语句（无 catch/finally）：
  - `using (Stream stream = File.OpenRead(fileName)) { ... }`（GeometryGraphReader.cs:118）
  - `using (TextReader reader = File.OpenText(fileName)) { ... }`（GeometryGraphReader.cs:128）
  - `using (Stream stream = File.Open(fileName, FileMode.Create)) { ... }`（GeometryGraphWriter.cs:119）
  - `using (var file = new StreamWriter(path)) { ... }` / `using (var file = new StreamReader(path)) { ... }` ×2（SteinerCdt.cs:52/98/130）
- **错误 TS 输出**：`try { let stream = StreamWrapper.of(FileHelper.openRead(fileName)); ... }` ——
  TS 不允许 try 无 catch/finally，tsc 报 TS1472 `'catch' or 'finally' expected.`（6 处：
  GeometryGraphReader.ts:98/105、GeometryGraphWriter.ts:89、SteinerCdt.ts:48/105/134）。
- **正确 TS 输出**：`{ let stream = StreamWrapper.of(FileHelper.openRead(fileName)); ... }` ——
  无 catch/finally 时去掉 `try` 关键字，资源声明与主体降级为普通块作用域；变量作用域与
  Java try-with-resources 一致（资源名仅块内可见）。有 catch/finally 时保持 `try {`（已工作）。
- **根因**：`TransformUsingStatement` 产出 Java raw 文本 `try (Type name = init) { body }`；
  `TsRawStatementNormalizer.NormalizeTryWithResources` 只把资源头改写为 `try { let ...;`，
  未检查 try 后是否真的存在 catch/finally。TS 中裸 `try {` 是语法错误（TS1472），
  而 C# `using`（无异常处理语义）在 Java 风味下被机械套上 try-with-resources 外壳。
- **修复位置**：`NormalizeTryWithResources` 在改写资源头后，用新增的 `FindMatchingBrace`
  找到主体块匹配的 `}`，`HasCatchOrFinallyAfter` 检查其后是否紧跟 `catch`/`finally`；
  两者皆无时把改写结果 `"try { let ...;"` 的前缀 `"try "` 剥掉，仅保留 `{ let ...;`。
  新增 `SkipComment`（不追加 StringBuilder 的注释跳过变体）供花括号匹配跳过注释。
- **降级语义论证**：TS 无资源跟踪语法；无 catch/finally 的 `try` 不承担任何异常流
  语义（不捕获、不清理、不重抛），唯一作用是把资源声明限定在块内——普通块 `{ }`
  完整保留该作用域语义。不引入 `finally { x.close?.(); }`：兼容库类型未必声明
  `close`，可选链 `close?.()` 在 TS 下仍做成员存在性检查，会制造新的编译错误；
  块作用域零风险且与"零错误编译"目标一致。
- **测试**：`TsRawStatementNormalizerTests` 新增 4 例（无 catch/finally 全语句 → 块作用域、
  带 catch 保持 try、带 finally 保持 try、主体含嵌套块的花括号匹配），先红后绿；
  保留 head 片段（无主体）回退用例。全量 `dotnet test` 绿（7 + 542 + 2432）。
- **验证**：全量重转后 `npx tsc --noEmit` 26 → 20（恰好移除 6 个 TS1472，无新增错误）。

## Round 9 — 构造函数委托实参在成员位被朴素替换

- **C# 构造**：构造器重载消歧把 `this(...)` 委托目标构造器体内联进 `Create$N` 工厂，
  委托实参按形参名替换目标体内的形参引用。目标体形如 `this.left = left;` /
  `this.center = center;` / `this.comparer = comparer;` /
  `this.clusterSettings = clusterSettings;`：
  - `Rectangle(IEnumerable<Point>) : this(0, 0, new Point(-1, -1))`（Rectangle.cs:219-220）
  - `Ellipse(Point center, ...) : this(new Point(centerX, centerY), ...)`（Ellipse.cs 委托）
  - `RbTree(Func<T,T,int> func) : this(new ComparerOnDelegate<T>(func))`（RBTree.cs:363）
  - `InitialLayoutByCluster(GeometryGraph, LayoutAlgorithmSettings) : this(graph, anyCluster => defaultSettings)`（InitialLayoutByCluster.cs:35）
- **错误 TS 输出**：
  - `self.0 = 0;` ×2（Rectangle.ts:389/391，成员 `left`/`bottom` 被 `0` 替换）
  - `self.new Point(centerX, centerY) = new Point(centerX, centerY).clone();`（Ellipse.ts:469）
  - `self.new ComparerOnDelegate<T>(func) = new ComparerOnDelegate<T>(func);`（RbTree.ts:371）
  - `self.anyCluster => defaultSettings = anyCluster => defaultSettings;`（InitialLayoutByCluster.ts:389）
  - tsc：TS1005/TS1434 共 5 个错误。
- **正确 TS 输出**：成员位（`.` 之后）不替换，仅值位替换——
  `self.left = 0;`、`self.center = new Point(centerX, centerY).clone();`、
  `self.comparer = new ComparerOnDelegate<T>(func);`、
  `self.clusterSettings = anyCluster => defaultSettings;`。
- **根因**：`TsOverloadDisambiguationRewriter.SubstituteCtorArguments` 把形参名在
  整个内联体内全量替换，未区分成员位与值位；`.` 之后的标识符是成员名而非形参引用。
  除本轮 5 个语法错误外，当委托实参是普通标识符（如 `this(comparer)` 传 `func`）时
  会静默产出 `self.func = func;` 这种赋错属性的有效但错误 TS，属潜在语义损坏源。
- **修复位置**：`SubstituteCtorArguments` 命中标识符后先检查 `code[start - 1] == '.'`，
  成员位原样保留（`self.left` 的 `left` 不替换）。执行顺序无变化：`SubstituteThis`
  （`this`→`self`）仍在 `InlineBody` 叶层先执行，成员位判定不受影响。
- **测试**：`TsOverloadRewriterTests` 新增 2 例端到端（镜像 Rectangle 的 `0`/`new Point`
  实参与 InitialLayoutByCluster/RbTree 的 lambda/`new Holder(func)` 实参），先红后绿，
  均含 `AssertCompilesWithCompat` tsc 校验；全量 `dotnet test` 绿（7 + 544 + 2433）。
- **验证**：全量重转后 `npx tsc --noEmit` 20 → 15（实测；移除 5 个错误恰为
  D 族：RbTree.ts:371 / Ellipse.ts:469 / Rectangle.ts:389,391 /
  InitialLayoutByCluster.ts:389，前后列表程序化对比新增 0）。

**修复前错误列表（20，供对照）**

- D（5）：RbTree.ts:371；Ellipse.ts:469；Rectangle.ts:389,391；InitialLayoutByCluster.ts:389
- B（5）：SplineRouter.ts:307（TS1003/TS1005）；MeshCreator.ts:1116（TS1003×1/TS1005×2）
- A2/A4（4）：ConnectedComponentCalculator.ts:12（TS1011/TS1005）；Tiling.ts:1187（TS1434）；ProcrustesCircleConstraint.ts:198（TS1434）
- A3（2）：ProcrustesCircleConstraint.ts:118（TS1011×2）
- E（2）：LayoutAlgorithmHelpers.ts:69（TS1005/TS1135）
- F（2）：Relayout.ts:106（TS1146/TS1005）

## Round 10 — `X[]::new` 方法引用 FQCN 接收者回扫残缺

- **C# 构造**：集合/LINQ `ToArray()` 经生成器产出 Java 方法引用实参，元素类型为命名空间限定名：
  - `multiEdges.Add(edgeGroup.ToArray())`（SplineRouter.cs:401）→ `Core.Layout.Point[]::new`
  - `Core.Geometry.Point[] points = list.ToArray()`（MeshCreator.cs:1289）→ `Core.Geometry.Point[]::new`
- **错误 TS 输出**：
  - `multiEdges.value.push(edgeGroup.toArray(Core.Layout.(n) => new Array(n)));`（SplineRouter.ts:307，TS1003/TS1005）
  - `let points: Point[] = list.toArray(Core.Geometry.(n) => new Array(n));`（MeshCreator.ts:1116，TS1003/TS1005×2）
- **正确 TS 输出**：`...toArray((n) => new Array(n))` —— 点分类型名整体丢弃，只留数组构造 lambda。
- **根因**：`TsRawStatementNormalizer.TryRewriteMethodReference` 对 `Type[]::new` 回扫接收者时用
  `IsIdentifierChar`（不含 `.`），FQCN 只剩末段被吃（`Core.Layout.Point[]::new` 中仅 `Point[]` 被回扫），
  `.` 前缀残留在输出里，紧贴改写出的 `(n) => new Array(n)`。
- **修复位置**：`[]` 后缀接收者回扫后追加点分段回扫（`code[recvStart] == '.'` 时继续向前吃掉完整点分类型名），
  仅数组构造器场景生效；普通标识符/`this` 接收者路径不变（`Runnable::run`、`Integer::intValue`、`this::pack` 回归）。
- **测试**：`TsRawStatementNormalizerTests` 新增 3 例（FQCN 点分 `Microsoft.Msagl.Core.Geometry.Point[]::new`
  + `Core.Geometry.Point[]::new`、简单类型 `Point[]::new`/`Integer[]::new`、`Runnable::run`/`this::pack` 回归），
  先红后绿（红：`toArray(Microsoft.Msagl.Core.` 残留）；全量 `dotnet test` 绿（7 + 547 + 2436，
  CSharpToJava.Tests 首跑 1 个波动失败，重跑全绿 —— 其他会话并发构建干扰）。
- **验证**：全量重转后 `npx tsc --noEmit` 15 → 10（移除 B 族 5 个错误：SplineRouter.ts:307×2 /
  MeshCreator.ts:1116×3，前后列表程序化对比新增 0）。

**修复前错误列表（15，供对照）**

- B（5）：SplineRouter.ts:307（TS1003/TS1005）；MeshCreator.ts:1116（TS1003×1/TS1005×2）
- A2/A4（4）：ConnectedComponentCalculator.ts:12（TS1011/TS1005）；Tiling.ts:1187（TS1434）；ProcrustesCircleConstraint.ts:198（TS1434）
- A3（2）：ProcrustesCircleConstraint.ts:118（TS1011×2）
- E（2）：LayoutAlgorithmHelpers.ts:69（TS1005/TS1135）
- F（2）：Relayout.ts:106（TS1146/TS1005）

## Round 11 — CRLF raw 文本尾注释声明未 let 化（tail 正则吞不下行尾 `\r`）

- **C# 构造**：构造器内带尾注释的局部声明。构造器体经 `AppendLine()`（`\r\n`）展平为
  整段 raw 文本并再次展平时行尾残留 `\r`（管线内存行尾实测 `13,13`，生成文件 `\r\r\n`）：
  - `double r = 10*(double)n; // we make our circle a reasonable size to aid numerical precision`（ProcrustesCircleConstraint.cs:28）
  - `int m, n, k = 1; //node location and inddex`（Tiling.cs:70）
- **错误 TS 输出**：
  - `double r = 10 * (self.n as number); // we make our circle a reasonable size to aid numerical precision`（ProcrustesCircleConstraint.ts:198，TS1434）
  - `int m, n, k = 1; //node location and inddex`（Tiling.ts:1187，TS1434）
- **正确 TS 输出**（尾注释原样保留）：
  - `let r: number = 10 * (self.n as number); // we make our circle a reasonable size to aid numerical precision`
  - `let m: number, n: number, k: number = 1; //node location and inddex`
- **根因**：`TsRawStatementNormalizer.NormalizeRawDeclarations` 的 `declPattern` 尾组
  `(?<tail>\s*(?://[^\r\n]*)?)$`（Multiline）在尾注释后停在 `\r` 前：`[^\r\n]*` 不吞
  `\r`、可选注释组之后无 `\s*` 兜底，`$` 无法匹配剩余 `\r`，整行拒绝改写；无注释行
  tail 的 `\s*` 能吃掉 `\r` 所以正常。`TsStatementWriter` 直接以 `raw.Code`（整段展平
  文本）归一化，故受影响的是所有行尾为 `\r\n`/`\r\r\n` 的 raw 文本。
- **修复位置**：`declPattern` 尾组改为 `(?<tail>\s*(?://[^\r\n]*)?\s*)$`（注释后追加
  `\s*` 吞掉行尾 `\r`；贪婪 + 回溯保证 `$` 落在行尾 `\n` 前，字符串字面量内的 `\r`
  属 `decls` 的 `.+` 区域不受影响）。顺带核查 Normalize 内其余 `$` 锚定正则：经典 for
  头、try-with-resources、catch 均为 Trim 子串或扫描式输入，无 `$`+Multiline+
  `[^\r\n]` 组合，不受影响。
- **测试**：`TsRawStatementNormalizerTests` 新增 6 例（CRLF 尾注释单声明 / 多变量 /
  整段构造器体镜像 Tiling（`\r\r\n` 行尾）/ 无注释 CRLF 回归 / LF 尾注释回归 / 字符串
  字面量含 `\r` 不被触碰），先红后绿（红：4 例 CRLF 尾注释形态全部失败）；全量
  `dotnet test` 绿（7 + 553 + 2438，含其他会话未提交测试）。
- **验证**：全量重转后 `npx tsc --noEmit` 10 → 8（实测；移除 2 个错误恰为 A4 族：
  Tiling.ts:1187 / ProcrustesCircleConstraint.ts:198，前后列表程序化对比新增 0）。

**修复前错误列表（10，供对照）**

- A2（2）：ConnectedComponentCalculator.ts:12（TS1011/TS1005）
- A4（2）：Tiling.ts:1187（TS1434）；ProcrustesCircleConstraint.ts:198（TS1434）
- A3（2）：ProcrustesCircleConstraint.ts:118（TS1011×2）
- E（2）：LayoutAlgorithmHelpers.ts:69（TS1005/TS1135）
- F（2）：Relayout.ts:106（TS1146/TS1005）

**修复后错误列表（8）**

- A2（2）：ConnectedComponentCalculator.ts:12（TS1011/TS1005）
- A3（2）：ProcrustesCircleConstraint.ts:118（TS1011×2）
- E（2）：LayoutAlgorithmHelpers.ts:69（TS1005/TS1135）
- F（2）：Relayout.ts:106（TS1146/TS1005）

## Round 16 — TS2663/TS2662：raw 文本裸成员引用补前缀（实例 `this.` / 静态 `类名.`）

- **C# 构造**：`BinaryHeapWithComparer.cs` 的 `if (heapSize == A.Length - 1) { var newA = new T[A.Length*2]; ... }`
  （裸实例字段 `heapSize`/`A`、实例方法 `Less`）；`AlgorithmBase.cs` 静态字段 `threadStaticCancelToken` 与
  `LocalProgressStepSizeDefault`（字段初始化器引用静态成员）。
- **错误 TS 输出（修复前，本轮起点，独立 tsc 实测 23043 条）**：
  `var newA = TypeHelper.newArrayInstance(tClass, A.length * 2);`（TS2663，裸 `tClass`/`A` 应 `this.tClass`/`this.A`）；
  `private localProgressStepSize: number = LocalProgressStepSizeDefault;`（TS2662，应
  `AlgorithmBase.LocalProgressStepSizeDefault`）；`threadStaticCancelToken = oldCancelToken;`（TS2662，应
  `AlgorithmBase.threadStaticCancelToken`）。
- **正确 TS 输出**：`var newA = TypeHelper.newArrayInstance(this.tClass, this.A.length * 2);`；
  `private localProgressStepSize: number = AlgorithmBase.LocalProgressStepSizeDefault;` 等。
- **根因与理由**：raw Java 文本直通发射（`TsRawStatementNormalizer` 纯文本管线）无类成员知识：实例成员名
  从不加 `this.`、静态成员名从不加 `ClassName.`。结构化 IR 路径（访问器/结构化体）已正确加前缀，raw 文本路径
  （方法体/箭头体/字段初始化器/ctor raw 体/静态初始化块/`JavaRawStatement`/`JavaRawExpression`）全部缺失。
  TS2663 3376 + TS2662 1600 = 4976 条，散布 100+ 文件（LgInteractor 411、LayeredLayoutEngine 165、
  InteractiveEdgeRouter 141 等），系统性 raw 文本问题，非个别文件。
- **修复位置**：
  - 新增 `src/CSharpToJava.Core/TypeScript/TsMemberContext.cs`：`ClassName`/`InstanceMembers`/`StaticMembers`/
    `TypeParameters`/`TypeNames`/`SelfTypedMembers`；`From(cls, accessorRoles, isEnumTypeName)` 与发射点使用
    完全相同的命名函数（字段/方法 `SafeIdentifier`、访问器按 accessorRoles 属性名 `Name.Substring(3)`、
    嵌套类型 `SafeClassName`）；`isEnumTypeName` 谓词（`_flagsEnumNames`/`IsFlagsEnum`/`IsExplicitValueEnum`）
    覆盖 `[Flags]`/显式值枚举映射为 int 后 IR 类型丢失枚举名的 `Direction Direction` 自类型形态。
  - `TsTypeWriter.EmitClass` 每个类构建一次上下文 → `TsMemberWriter.SetMemberContext` 同步三个 writer；
    `EmitTestClass` 显式清空（测试类字段是 describe 作用域 let，禁前缀）。
  - `TsRawStatementNormalizer` 新最终 pass `NormalizeBareMemberReferences`（管线最末尾，扫描最终发射形态，
    避免干扰声明 let 化/for 头/方法引用/数组/cast 等既有 pass）：标识符 token 命中 InstanceMembers →
    `this.name`、StaticMembers → `ClassName.name`；遮蔽保护（参数/方法类型参数/结构化+raw 局部/`let a, b;`/
    Java `T parent, son;`/for 头/lambda 参数/catch 绑定，brace 作用域栈）；位置保护（字符串/字符/注释、
    `.` 后、`new`/`instanceof` 类型位、对象字面量键、类类型参数、嵌套类型名、自类型成员后跟 `.` 的类型限定
    静态访问）；空上下文/全空集合原样返回（既有测试零影响）。
  - 发射点全部接上：`AppendMethodBody`/`AppendArrowBody`（含结构化体方法级 `MethodShadowNames` 注入
    `_stmtWriter.MethodLocals`/`_exprWriter.MethodLocals`）、`EmitConstructor` raw 体（此前完全不归一化，
    本轮接入完整管线）、`NormalizeLiteral` 字段初始化器、`TsTypeWriter` 静态初始化块（整块归一化）、
    `TsStatementWriter.Emit` `JavaRawStatement`、`TsExpressionWriter.NormalizeRaw`。
  - 跨语句遮蔽：结构化体逐语句归一化时，前一 raw 语句声明的局部名（`var settings = ...;`）经新公开
    `CollectDeclaredLocalNames` 预收集进方法遮蔽集（GTreeOverlapRemoval 静态方法内 `settings` 不得补 `this.`）。
- **测试（先红后绿）**：`TsRawStatementNormalizerTests` 单元用例（`A.length`→`this.A.length`、
  `tClass`→`this.tClass`、静态 `threadStaticCancelToken`→`AlgorithmBase.threadStaticCancelToken`、遮蔽族
  （参数/局部/多声明/lambda 参数/for 头/catch）、`new X(...)`/字符串/注释/`.` 后/类型参数不加前缀、
  已有 `this.x` 不重复加、无上下文原样回归守卫）；新增 `TsRawMemberContextTests` 端到端
  （BinaryHeapWithComparer/AlgorithmBase 最小镜像：`this.A`/`this.tClass`/`ClassName.staticX`、Flags 枚举
  自类型属性类型限定访问保持裸、跨语句 raw 局部遮蔽、ctor raw 体前缀）；3 例既有断言随正确发射更新
  （`this.b(...)`/`this.m(...)`/`this._iteratorHasNext`）。红：新用例首跑失败（缺前缀/过前缀），实现后全绿。
- **验证链**：`dotnet build --configuration Release` 0 错误 → 删 `D:\msags-ts-202608` 全量重转 →
  独立 tsc 7.0.2：**23043 → 22171**（−872）；TS2663 **3376 → 112**（−96.7%）、TS2662 **1600 → 18**
  （−98.9%）；语法 0。行三元组对比（path+行+CODE）：消失 1600、迁移 1694、**新增 106**，逐条核验
  **0 例过前缀回归**；8 例既往过前缀行转正（GTreeOverlapRemoval.ts:34/284、ScanDirection.ts:16/17/24/70/74、
  VertexEntry.ts:18）。
- **残留 TS2663（112）形态（下轮候选）**：无类上下文发射路径（写专用接口形态：OptimalPacking/
  OptimalColumnPacking/OptimalRectanglePacking 字段转 converted 形态）；属性 getter 体字段简化名与发射名
  不一致（RectanglePacking.ts `bestPacking` 私有字段 getter 重命名形态）；DebugHelpers/DoubleStreamElement
  `Value`；基类声明字段在派生类 raw 体（KDTree `med`/`multipoleCoefficients`、FullVisibilityGraphGenerator
  `ObsTree`/`HorizontalScanSegments`）；静态方法内引用实例成员（MaxSeparationConstraint）；
  EdgeLabelPlacement.startListenToLocalProgress 委托。
- **残留 TS2662（18）形态**：全部为**基类静态成员**（VisibilityGraphGenerator 的 `SentinelOffset`/
  `NewVisibilityGraph`/`debug_AssertGraphIsRectilinear`，基类 `delta`/`overlapP`/`vectorScaledVectorSubtract`/
  `assert_Rounded`）——上下文只含当前类，不含基类链。
- **dotnet test**：全绿 —— 7（项目级）+ 570（CSharpToJava.ProjectTests）+ 2462（CSharpToJava.Tests）+
  604（CSharpToTypeScript.Tests，含本轮过滤集 126/126）。CLI 转换 510/510 成功。
- **提交**：代码+测试 `3cf39918`；本文档独立提交。
- **并发会话干扰**：基线 `5570661a` 后并发会话已提交 7+ 改动（HEAD `116fcc59`），其工作区未提交文件
  （config/TypeMappings.json、IdentifierExpressionTransformer.cs、compat RuntimeFeature.java/Timeout.java、
  TimeoutAndRuntimeFeatureMappingTests.cs 等）本轮一律不碰不纳，提交仅显式 `git add` 本轮 12 个文件。


## Round 17 — 非自动属性访问器 getter/setter 形态调用点改写（TS2551 6,070 → 1,019）

- **C# 构造**：MSAGL 大量**非自动属性**（显式 get/set 体），如 `Edge.cs` `public Node Source { get { return source; } set { source = value; } }`；
  调用点 raw 文本保留 Java getter 形态（`item.getSource()`、`this.EdgeGeometry.getSourcePort()`、`typeof ApproximateComparer` 静态
  `getTolerance()`、裸 `getSweepDirection()`）与结构化/raw lambda 参数（`ret.sort((a, b) => b.getZoomLevel()...)`、
  `Enumerable.selectMany(modifiedNodes, v => v.getAllClusterAncestors())`）。
- **错误 TS 输出（修复前，本轮起点 22,171）**：TS2551 6,070（770 个成员名，getter/setter 形态占绝大多数：getSource 254、
  getTarget 220、getCenter 160、getBoundingBox 130、getLength 107、getUserData 83…）；TS2304 裸 getter 族
  （getSweepDirection 48 / getDirectionPerp 22 / getObstacles 10）；TS2339 4,566。
- **根因与缺口环节定位**：`TsPropertyReconstructionRewriter` 对非自动属性访问器的调用点改写存在覆盖缺口，逐环定位：
  1. **项目级注册表缺失**——per-unit 分析看不到跨文件类型的 AccessorMap（EdgeCollection.item.getSource() 不改写）；
  2. 同名多类型覆盖（Edge Layout/Tiling）无候选列表；
  3. 泛型实例化/约束（BasicGraph<Node,TEdge>、TEdge extends IEdge）不解析；
  4. 局部变量收集只覆盖结构化 raw 顶层（for/while/if 整段 raw 内声明不收集）；
  5. Java 形态声明（`Node n = new Node();` 嵌套整段 raw for）不识别（真实管线 LayeredLayoutEngine pushLayers 形态）；
  6. forEach VariableType 为空（Java 文本路径）不从 Collection 推断；
  7. 非 new 初始化链（`let bounds = this.curve.BoundingBox.clone();`）不推断类型；
  8. 括号表达式接收者 `(Point.op_Subtract(a,b)).getLength()` 链式段解析缺括号段；
  9. 裸访问器链首段 `getEdgeGeometry().getSourcePort()` 首段方法调用不回退访问器；`NormalizeTypeName` 不剥尾部 ` | null`。
  lambda 族另有三处：**raw 双括号/裸参数/逗号分隔调用名回扫缺口**（`sort((a, b) ->` / `sort(a ->` /
  `Enumerable.sum(ret, n ->` 形态）、**BuildMethodContext 期间 `_methodStack` 未 push**（简单标识符须先查变量表）、
  **结构化语句内 lambda 不在 raw 文本**（赋值/声明初始化，需语句树遍历）+ **构造函数路径无任何收集** +
  `IterableElementType` 被 `NormalizeTypeName` 剥泛型实参/不剥 ` | null`。
- **修复内容**（`src/CSharpToJava.Core/TypeScript/Rewriters/TsPropertyReconstructionRewriter.cs` 为主）：
  - 新增 `ProjectAccessorRegistry`（全名键 + 简单名候选导入/同包消歧）、`CollectProjectContexts`；`ProjectTsPasses.cs`
    两阶段发射、`TsEmitSupport.cs` 参数透传；
  - `TryGetTypeContexts` 候选列表、`ResolveTypeParameter`/`NormalizeTypeName` 泛型约束与 ` | null` 剥离；
  - `CollectRawLocalsRecursive`/`CollectRawLocalsFromStatement` 递归进 for/while/if/block/catch；
    `TryCollectJavaStyleDeclaration`（Java 形态声明）、`ResolveForEachElementType`、`ResolveExpressionTextType`/
    `ReadRawChainText`/`ReadExpressionUpToDelimiter`（非 new 初始化链）、`RawChainSegment.IsParen`（括号段）；
  - lambda 族：`TryCollectLambdaParamTypes`（白名单 + 接收者/首参数元素类型推断，双括号/裸参数/逗号分隔统一
    `TryFindLambdaCallBefore` 回扫）、`ResolveLambdaSourceType`（变量表优先）、`CollectStructuredLambdaParams` 语句树
    遍历（结构化 lambda）、`VisitConstructorDeclaration` 补同套收集、`IterableElementType` 仅剥尾部可空修饰；
    `LambdaMethodWhitelist` 增 selectMany/thenBy/firstOrDefault 等；
  - 测试：`tests/CSharpToTypeScript.Tests/TsPropertyReconstructionRound17Tests.cs`（81 → 85 例，新增
    EndToEnd_Lambda_Param_Getter / EndToEnd_Lambda_FormB / RawLambda_FormB_Array_And_Param_Collection /
    StructuredLambda_Param_Element / CtorRawBody_Lambda_Param_Element；此前 24 例覆盖跨文件/泛型/嵌套 raw/
    Java 形态声明/括号接收者等）。
- **先红后绿**：探针 Round17ReproProbe11（`ret.sort((a, b) => b.getZoomLevel()...)` 端到端）首跑红
  （输出仍 getter 形态），修复后绿；RawLambda_FormB/CtorRawBody 用例先红（selectMany/lbs/构造器形态残留
  getter）后绿；红态证据均保留在验证链的 loop17d/e 中间态。
- **验证链**：`dotnet build --configuration Release` 0 错误 → 整删 `D:\msags-ts-202608` 全量重转
  （510/510 成功）→ 独立 tsc 7.0.2：**22,171 → 15,013**（−7,158）；TS2551 **6,070 → 1,019**（−83.2%；
  getset 族 5,908 → 850，**−85.6%**）；TS2339 4,566 → 1,923（−2,643，getter→属性后成员名命中正确）；TS2304
  3,118 → 2,986，**裸 getter getSweepDirection/getDirectionPerp/getObstacles 全清零**；语法 0。
- **迁移分析**：getter→属性访问揭示下游可空性/类型错误（同一位置换码）：TS2531 +52、TS18047 +308、TS2345 +112、
  TS2322 +92、TS2488 +64、TS2739 +24、TS2741 +33 等；固定位置中 1,319 处转 TS2531（`X.Source` 为 `Node | null`）、
  713 处同行仍 TS2551（同行其他 getter）、507 处转 TS2304、305 处转 TS18047、289 处转 TS2339、200 处转 TS2345——
  属正常揭示，非回归。新增回归核对：相对 R16 无错误行新增 70 处 getset 位置，其中 69 处在本轮 9 缺口中间态
  （loop17d）已存在；唯一新增 TransientGraphUtility.ts:437 为揭示型迁移（select lambda 参数推断使 crossingsArray
  类型化，`s` 不再 any，暴露 `s.getIsTransparent()`）。
- **残留 TS2551（1,019）形态（下轮候选）**：① compat 命名族（排除项 169：values 49、keySet 45、clear 34、
  type 11、module 9、spliterator 6、signum 等，索引器 get_Item/set_Item、set$ 混名同族）；② lambda-line 148
  （多为同行非 lambda：curve.getParEnd() 参数接收者、e.setSourcePort 的 forEach 元素；真 lambda 残余：
  Enumerable.toList/toArray 链式推断 var 局部、内部 filter lambda 未白名单、initialize 等 Func 末参形态、
  thenBy 嵌套调用首参数）；③ 非 lambda 702：参数/局部接收者接口或未注册类型（BorderInfo `bi`、Curve `md`）、
  字典 `.get(x)` 链接收者（edgeInfo.getZoomLevel() 45）、forEach 元素类型擦除（`for (const c of ...)` 集合泛型
  擦除 → c.getSegments()）、`typeof Class` 静态、成员链接收者（segment.value.getParEnd()）。
- **dotnet test**：全绿 —— 7（项目级）+ 633（CSharpToTypeScript.Tests，含本轮过滤集 85/85）+
  2471（CSharpToJava.Tests）；2 例环境相关顺序失败（SystemPrivateXmlRuntimeResourceTests /
  ToStringFormatTests）隔离重跑通过，与本轮无关。
- **提交**：Round 17 主体（ProjectAccessorRegistry/结构化 lambda 推断等）被并发会话整文件暂存扫入
  `cabeb448`（其提交主题为 mutable ValueTuple lowering），经核对**内容完整在 HEAD**（TryFindLambdaCallBefore/
  CollectStructuredLambdaParams/IterableElementType 修复/白名单/4 例 lambda 测试均在）；本轮独立提交补
  构造函数收集 + CtorRawBody 测试；本文档独立提交。
- **并发会话干扰**：MethodMigrator.cs 中途出现不存在的 `LambdaExpressionParameterSyntax` 破坏构建 3 次
  （并发会话迭代中），轮询等待其修复后继续；RefLambdaParameterProjectTests 中途失败、随后被并发会话修复；
  cabeb448 扫入本轮大部分改动（见上）；并发会话其他文件（LambdaTransformer.cs / InvocationExpressionTransformer.cs /
  StructCloneHelper.cs / compat Java / re-error-analysis.md 等）一律不碰不纳。

## Round 18 — 双根因：构造器重载调用点 Create\ 工厂改写（TS2673）+ FQCN 静态调用接收者缩短（TS2304 'Microsoft'）

本轮为子代理执行轮（报告丢失，由提交、临时错误清单与独立复核重建）。实测口径：tsc 错误行数（每行一错，含一行多错）。

### 根因 A — 构造器重载调用点未改 Create\ 工厂（42f4dffe）

- **C# 构造**：① 仅含显式构造器 + 隐式无参构造器的 struct（MSAGL Point：484 处 TS2673）；② 对象初始化器新建
  （ConvexHull/CurveFactory 等的 \
ew Polyline(points) { Closed = true }\ 形态，100+ 处 TS2673）。
- **错误 TS 输出（修复前）**：\
ew Point(...)\ 直呼声明侧已工厂化的类（IR 层 TsCtorFactoryRewriter 已把重载
  构造器改私有合成构造器 + Create\ 工厂），语法层 TsOverloadDisambiguationRewriter 只覆盖 ≥2 显式构造器且
  无初始化器的场景 → \error TS2673: Constructor of class X is private.\（全库 769 条）。
- **正确 TS 输出**：\Point.create\(...)\（与 IR 层同算法：形参个数稳定排序、隐式零参构造器优先）。
- **根因**：IR 层工厂化与语法层调用点改写不同步——struct 隐式构造器、带对象初始化器的调用点被漏改。
- **修复位置**：\ObjectCreationTransformer.TryBuildTsCtorFactoryCall\ 独立重算 Create 序号（与 IR 重写器同算法）
  并覆盖三条发射路径（普通实参 / 零参 / 对象初始化器前置语句）；TsOverloadDisambiguationRewriter 同步调整。
  TS 专属，Java 目标未动。
- **测试**：\TsCtorFactoryCrossFileTests.cs\ +2（struct 隐式构造器同/跨文件、对象初始化器新建），先红后绿。
- **验证**：TS2673 769 → 238（−531）。

### 根因 B — FQCN 静态方法调用接收者未缩短（7fccb22c）

- **C# 构造**：struct 运算符 / 静态调用发射为 raw Java 文本时保留全限定接收者：
  \ct * ct\ → \Microsoft.Msagl.Core.Geometry.Point.op_Multiply(ct, ct)\（ClosestPointOnCurve 等）。
- **错误 TS 输出（修复前）**：\error TS2304: Cannot find name 'Microsoft'.\（1,791 条，TS2304 头号族，
  占全库 ~60%）；NormalizeFqcnExpressions 只缩短末段后随 \.\（成员访问）或 \
ew \（创建位）的链，
  以 \(\ 结尾的静态调用直通。
- **正确 TS 输出**：\Point.op_Multiply(ct, ct)\。
- **修复位置**：\TsRawStatementNormalizer.NormalizeFqcnExpressions\ 增 \fter == '('\ 非 new 分支——末段视为
  方法名、类型前缀经 mapType oracle 缩短。守卫：类型前缀必须 ≥2 个点（FQCN 形态），短链 \Timer.GlobalTick.add(...)\
  不误伤（回归由 TsEventEmissionTests 捕获，先红后绿）；\java./io.github./System.\ 前缀与单段前缀经 oracle 恒等
  返回不动。
- **测试**：\TsRawStatementNormalizerTests\ +2（含嵌套括号接收者的静态调用缩短；守卫：Stream.concat /
  System.arraycopy / io.github FQCN / throw new java.util. / Timer.GlobalTick + new FQCN 回归），先红后绿。
- **验证**：TS2304 'Microsoft' 1,791 → 146（−1,645）。

### 验证链（独立复核）

- 15,013 → 13,278 错误行（−1,735）；唯一三元组 14,786 → 13,053（−1,733）。语法 0。
- 修复后主要错误族：TS2531 2,959、TS2339 1,969、TS2304 1,343、TS2551 1,318、TS2345 918、TS18047 826、
  TS2322 820、TS2693 370、TS18046 304、TS2673 238、TS7006 221、TS2488 195。
- **TS2551 1019 → 1318（+299，揭示型迁移非回归）**：新增 356 处全为 getter/setter 形态调用点（getLength 135 /
  getLengthSquared 17 / setClosed 17 / setUserData 13 / getDirection 12 / getIsEmpty 8 / setPolyline 8 /
  getCenter 7 / setLength 7 等）——FQCN 接收者缩短后类型解析成功，先前被 TS2304 掩盖的
  \x.getLength()\ 直呼形态浮现（Round 17 重写器对这些接收者当时无法解析未改写）。**下轮主候选**：
  让 TsPropertyReconstructionRewriter 覆盖此类接收者（缩短后/类型化后的调用点），预计可一次收割数百条。
- **dotnet test 全绿**：7 + 638 + 2475（独立复核确认）。
- **提交**：42f4dffe（A）、7fccb22c（B），本文档独立提交。
- **并发干扰**：无（本轮执行期间无并发提交；仓库现存未提交改动属其他会话工作区，不碰不纳）。

## Round 19 — 无初始化器引用类型字段 `= null` 宽化（TS2531 2,922 → 15 / TS18047 771 → 116）

本轮起点前并发会话已提交 `b289f65f`（Round 18 预测的 TS2551 FQCN/compat/Create$N 接收者族，
getset 1,145 → 约 450；`getSource 135` 等大项清零），并留下 `e4f137e5`（clean 脚本）、`09cd49e7`
（局部函数提升，Java 侧）。实测起点：**12,633 错误行**（13,278 已含 b289f65f 的 −645）。

- **C# 构造**：无初始化器引用类型字段，由构造器赋值（C# 非可空上下文缺省非空）：
  `readonly List<ICurve> segs;`（Curve.cs:19）、`IComparer<T> comparer;`（BinaryHeapWithComparer.cs:104）、
  `Node source;`（Edge.cs:56）、`ICurve curve;`（CurvatureDerivative.cs:8）、`Curve curve;`（RoundedRect.cs:12）。
- **错误 TS 输出（修复前）**：`private segs: CSharpList<ICurve> | null = null;` —— 每个解引用点
  `this.segs.get(i).reverse()` 报 TS2531 "Object is possibly 'null'."（全库 2,922，186 文件）；
  `this.comparer.compare(...)` 同（BinaryHeapWithComparer 5 处）；TS18047 "'x.parent' is possibly 'null'"
  771；属性 getter `get Curve(): Curve { return this.curve; }` 内部 `return this.curve`（字段
  `Curve | null`）TS2322（Edge.ts:71 等 ~350 条）。
- **正确 TS 输出**：`private segs: CSharpList<ICurve> = null!;`（非空断言保持类型 T，满足
  strictPropertyInitialization）——与既有类型参数路径（Task 41 `Box<T> _value` → `T = null!`）
  同一惯例；显式 `= null` 初始化器（`int? x = null`、`string Label = null`，C# 真实可空）维持
  `T | null` 宽化不变。
- **根因与理由**：`TsMemberWriter.EmitField` 对"无初始化器且默认零值为 null"的字段一律
  `T | null` 宽化（注释自述"strictNullChecks 下 null 不可赋给非空类型 TS2322"）——但 C# 非可空
  上下文中无初始化器字段语义是非空（构造器定赋值），宽化是转换器对 strictPropertyInitialization
  的过度保守：每个读取点失实报错（TS2531/TS18047）、getter 直返字段反而在 getter 内部失实
  TS2322。`null!` 非空断言是 TS 对"构造器赋值"的标准惯用法，且与类型参数分支完全同构。
- **修复位置**：`src/CSharpToJava.Core/TypeScript/TsMemberWriter.cs` `EmitField`：新增
  `isDefaultedNullField = !hasInitializer && DefaultValueForType(tsType) == "null" && 类型不在
  (null/any/undefined/void)`——该形态跳过 `| null` 宽化（保持 T），初始化器改发射
  `isTypeParameter || isDefaultedNullField ? " = null!"`；显式 null 初始化器路径（emitsNull 且
  hasInitializer）仍走宽化。any/null/undefined/void 吸收 null，保持裸 `= null`（`any` 回归由
  Class_Wildcard_Type_Maps_To_Any 捕获）。
- **测试（先红后绿）**：新增 `tests/CSharpToTypeScript.Tests/TsNullAssertionRound19Tests.cs` 5 例
  （无初始化器字段 → `= null!` 且无 `| null`、显式 `= null` 保持宽化、值类型缺省零值不变
  （int 0/bool false/double 0/int? null）、getter 直返构造器赋值字段端到端 tsc 编译、
  非空字段 `!= null` 比较编译）；3 例既有断言随正确发射更新（TsBasicClassEmissionTests 改名
  String_Field_Default_Null_Emits_NonNull_Assertion、TsDelegateToFunctionTypeTests 4 个委托字段、
  TsJavaFlavorTypeMappingTests 10 个 Java 风味类型字段）。红：新 5 例首跑 5 失败 → 修复后全绿。
- **验证链（预测仿真先行）**：实现前先在 scratch 目录（.sim-a，已随 clean 脚本删除）对生成
  产物机械套用修复并独立 tsc：预测 12,633 → 8,143 行、三元组 −3,791（新增 82）。实修后
  重建 → 删目录全量重转（510/510）→ 独立 tsc：**12,633 → 8,159 行（−4,474）**；对 Round 18
  基线 **13,278 → 8,159（−5,119）**；三元组 10,898 → 7,122（移除 3,852 / **新增 76**：
  TS2322 50 / TS2741 18 / TS2740 7 / TS2739 1，全部为对现非空成员的 null 赋值/对象字面量
  揭示，非既有正确代码回归）。TS2531 **2,922 → 15**（−99.5%）、TS18047 **771 → 116**、
  TS2322 833 → 526（getter 内部 ~350 清零、新增 50）、TS2345 931 → 319。语法 0。
- **dotnet test 全绿**：7 + 658（CSharpToTypeScript.Tests，含本轮 5）+ 2487（CSharpToJava.Tests；
  较上轮 +12 为并发会话新增）。
- **提交**：b52ea407（代码+测试）；本文档独立提交。
- **并发干扰**：ClassTransformer.cs 途中 2 次构建中断（CS1002/CS7036，并发会话迭代中），
  轮询后其修复完成，未触碰其任何文件；其未提交文件（Pipeline 三件、TypeOperationTransformer、
  SwitchAndResource、Rewriters/、ReadOnlyDictInterfaceTests 等）一律不碰不纳。
- **下轮候选**：① TS2339 2,012 —— CSharpList `.get(i)` 328 + CsKeyValuePair `getKey/getValue`
  224（compat API 缺口：CSharpList<T> = Array<T> 别名无 get/set、compat CsKeyValuePair 无
  getKey/getValue；本轮 null 修复后 ~40 处此前被 TS2531 掩盖的 `.get` 调用点浮现）；
  ② TS2551 612 残量 getset 调用点（Edge.getSource/getTarget 16、TreeNode.type/module 20…）+
  TS2339 Edge.NodeId 103；③ TS2304 1,394 残量 Java 全局名（Stream/StreamSupport/MapHelper…）。

## Round 19（根因 B）— compat Java 集合 API 面（TS2339 2,012 → 1,316 / TS2693 364 → 296）

- **C# 构造**：`List<T>`/`Stack<T>` 索引器与成员方法映射为 Java 风味调用点（`A[0]` →
  `.get(0)`、`A[0]=x` → `.set(0,x)`、`RemoveAt` → `.removeAt`、`Peek` → `.peek`），
  LINQ/字典迭代元素访问 `kv.getKey()/getValue()`（Map.Entry 形态）；`new List<T>(coll)` →
  `new CSharpList<T>(coll)` 值位（probe：`this.B = new CSharpList<Integer>(other);`）。
- **错误 TS 输出（修复前）**：`let x: number = this.A.get(0);` → TS2339 "Property 'get' does
  not exist on type 'CSharpList<number>'"（get 328/set 22/remove 28/addRange 23/…，合计 ~460）；
  `kv.getKey()` → TS2339 224；`new CSharpList<Integer>(other)` → TS2693（CSharpList 89 /
  CSharpStack 31 / CSharpDictionary 10）。
- **正确 TS 输出**：同一调用点编译通过（Array 方法面声明）；`new CSharpList<X>()` → `[]`、
  `new CSharpList<X>(e)` → `Array.from(e)`（数字字面量 → `new Array(n)`）、
  `new CSharpDictionary<K,V>(n)` → compat 类构造（const 合并）。
- **根因**：`CSharpList<T> = Array<T>`/`CSharpStack<T> = Array<T>` 是纯类型别名，无运行时值
  与方法面；compat `CsKeyValuePair` 只有 key/value 字段无 Java 访问器。三个子面同一根因
  （compat API 缺口）：
  1. **方法面**——global.d.ts 桩增 `interface Array<T>` 合并声明（get/set/add/addAll/remove/
     removeAt/insert/removeRange/size/toArray/iterator/spliterator/exists/ensureCapacity/peek），
     与 compat 运行时 `ArrayCompat.ts`（Array.prototype 非枚举 polyfill，spec §4.3.5 IList/
     Stack 语义：get=下标、set=下标写、add=push、remove=按值删、removeAt=splice、size=length、
     toArray=拷贝/生成器、iterator/spliterator=Symbol.iterator、exists=some、peek=末元素）一一
     对应；compat 根入口副作用导入（实证：全库 140 个使用该面的文件全部已 import compat，
     任意 compat 导入即安装 polyfill）。
  2. **构造值位**——raw 文本 `NormalizeCompatListConstructors` 与结构化 `EmitNew` 同规改写
     `new CSharpList/Stack<X>()` → `[]`、单实参 → `Array.from(e)`（数字字面量 `new Array(n)`，
     Java ArrayList(int) 容量语义）；`CSharpDictionary/CSharpHashSet` 值位经桩 `declare const`
     同名合并解析为 compat 类构造器（type+const 同名声明，Round 15 测试桩同模式）。
  3. **CsKeyValuePair**——compat 类增 `getKey()/getValue()`。
- **测试（先红后绿）**：compat vitest `ArrayCompat.test.ts` 15 例（get/set/add/addAll/remove/
  removeAt/insert/removeRange/size/toArray/iterator/exists/ensureCapacity/peek）+ CsKeyValuePair
  getKey/getValue；cs2j `TsCollectionCompatRound19Tests.cs` 6 例（Array.from/[]/new Array 形态、
  多实参/非集合构造守卫、端到端 List 探针 strict tsc 编译含桩）。红：6 例全败 + compat 模块
  不存在；绿后 compat tsc 干净 + vitest 218/218。
- **验证链**：重建 → 删目录全量重转 → 独立 tsc：**8,159 → 7,502 行（−657）**；三元组
  −649 / +121 全为揭示型迁移（TS2551 42：列表元素类型 getter 形态调用点浮现——元素接收者
  属 TsPropertyReconstruction 残量族；TS2556 17 spread、TS2345 16、TS2322 9、TS2769 8
  容量表达式、TS18046 7 unknown 等——均非既有正确代码回归）。TS2339 **2,012 → 1,316**
  （CSharpList 方法面 328→0、CsKeyValuePair 224→0）、TS2693 **364 → 296**（构造值位
  130→26，残量为 Double/Integer 装箱族）。语法 0。compat 复制品（dest/compat）含新 dist
  （ArrayCompat.d.ts + index.js 副作用）实证。
- **中途修正**：fd623fe3 曾以 typeof 分发三元处理容量表达式（`Array.from(typeof (e) ===
  "number" ? new Array(e) : (e))`）——实参三处重复导致实参内部诊断三倍化（+42 quads，
  TS7006/TS2304 在 lambda/名字位），cc02d254 回退为单次 `Array.from(e)`；8 个容量表达式位
  （TS2769，`new CSharpList<X>(a.length + b.length - 1)` 形态）登记为下轮候选（需类型感知
  的容量/集合分派）。往返验证 7,502 与回退前一致。
- **dotnet test 全绿**：7 + 664（CSharpToTypeScript.Tests，含本轮 6）+ 2492（CSharpToJava.Tests；
  较上轮 +5 为并发会话新增）。
- **提交**：4424932a（主修复）、fd623fe3 + cc02d254（容量表达式往返）、本文档独立提交。
- **并发干扰**：InvocationExpressionTransformer.cs 途中构建中断（CS0841，并发会话迭代中），
  轮询后恢复；其未提交文件一律不碰不纳。
- **下轮候选（更新）**：① TS2339 1,316 —— Edge.NodeId 103/Selected 27/Used 22（属性名映射）、
  ParallelogramLeaf getLow/getHigh 52、`accept` ShowDebugCurvesEnumeration 37；② TS2551 663
  （列表元素/字典值接收者 getter 形态 + 本轮新浮现 42）；③ TS2304 1,393（Stream/StreamSupport/
  MapHelper 等 Java 全局名）+ TS2693 装箱族（Double 148/Integer 62 等，new Double(x) → x）。
