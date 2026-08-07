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


> Round 11 注：A4 修复代码与 6 例测试被并发会话的 git add 整文件暂存扫入 
> bacd8673（其提交主题为 WeakReference 泛型映射）；行尾形态测试（`\r\r\n`）与
> 本节记录被扫入 e3494d79（其提交主题为 IEnumerator 冲突）。两提交均覆盖在
> 本轮验证链（全量重转 + tsc 10 → 8）之内。

> Round 12 注：A2 修复（HasAcceptedIdentityArrayBase）与 1 例测试被并发会话的
> git add 整文件暂存扫入 07acb643（其提交主题为 reflection flag enums 映射）；
> 本节记录与表格行由独立提交收录。

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
