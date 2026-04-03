# LINQ 重写引擎架构设计

## 1. 文档定位

本文定义 `cs2j` 中 LINQ 重写引擎的架构边界、执行主线和后续演进方向。目标不是把所有 LINQ 语义一次性做完，而是把“查询语法脱糖 + 方法链识别 + 过程式循环展开 + Java Stream 回退”收束为一套可扩展、可观测、可测试的转换子系统。

本文面向的问题域对应 issue #27：支持 20+ LINQ 操作符、支持查询语法脱糖，并在适合时把 LINQ 链重写为 Java 侧可维护的过程式代码。

## 2. 目标与非目标

### 2.1 目标

1. 支持 `from / where / orderby / select / group by` 等常见查询语法的前置脱糖。
2. 支持对常见 `System.Linq.Enumerable` 方法链进行统一识别，而不是在各个 transformer 中零散处理。
3. 在 `PreferStreamApi = false` 时，把可安全改写的 LINQ 链降级为过程式循环、累加器和中间集合构造。
4. 在 `PreferStreamApi = true` 或改写失败时，保留 Java Stream 生成路径，不因局部失败中断整个文件转换。
5. 让重写过程具备明确的 pass 落点、跳过诊断、rewrite 计数和测试边界。

### 2.2 非目标

1. 不要求覆盖全部 `System.Linq` API。
2. 不在本阶段处理需要完整透明标识符建模的复杂 query continuation、复杂 `join` / `let` 组合。
3. 不在本阶段把所有 LINQ 都改写为“最优” Java 代码，优先保证行为正确、可回退、可扩展。
4. 不把 LINQ 重写逻辑分散回 visitor 主线；它必须保留为独立子系统。

## 3. 当前基线

### 3.1 当前执行入口

当前 LINQ 重写已经进入显式 pass 主线，入口在 [SingleFileLinqDesugarPass](../src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs)。

该 pass 位于 `Desugar` 阶段，执行条件是：

- `EnableLinqRewrite = true`
- `EffectivePreferStreamApi = false`
- 当前文件可获得有效 `SemanticModel`

如果语义模型不存在，或者重写过程中出现受控异常，系统会记录 warning 并回退到后续 Java Stream 路径，而不是直接终止转换。

### 3.2 当前两阶段结构

当前实现已经形成“两阶段 LINQ 重写”：

1. **查询语法脱糖**
   由 [LinqQueryDesugarer](../src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs) 完成，把 query expression 转为等价的方法链。

2. **方法链过程式改写**
   由 [LinqRewriter](../src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) 完成，把可识别的 `Enumerable` 链改写为循环、条件判断、累加器和集合构造。

这种分层已经证明是合理的：第一层只做纯语法工作，第二层再使用语义模型做链识别、捕获变量分析和具体代码展开。

### 3.3 当前代码落点

LINQ 重写子系统主要位于 [src/CSharpToJava.Core/LinqRewrite](../src/CSharpToJava.Core/LinqRewrite/)：

- `LinqQueryDesugarer.cs`：查询表达式脱糖
- `LinqRewriter.cs`：主重写器、链识别、数据流分析、跳过诊断
- `LinqRewriter.Rules.cs`：按操作符组织的规则实现
- `LinqStep.cs`：LINQ 链步骤模型
- `Lambda.cs`：lambda/匿名函数包装
- `CanRewrapForeachVisitor.cs`：`foreach` 场景辅助判断

测试基线位于：

- [tests/CSharpToJava.Tests/LinqQueryDesugarTests.cs](../tests/CSharpToJava.Tests/LinqQueryDesugarTests.cs)
- [tests/CSharpToJava.Tests/LinqChainRefactoringTests.cs](../tests/CSharpToJava.Tests/LinqChainRefactoringTests.cs)
- [tests/CSharpToJava.Tests/LinqImportAndStreamTests.cs](../tests/CSharpToJava.Tests/LinqImportAndStreamTests.cs)

## 4. 当前基线存在的问题

1. **规则规模已经不小，但规则边界还不够显式。**
   目前 `LinqRewriter.Rules.cs` 已承载大量操作符实现，但“哪些属于过滤、投影、聚合、物化、排序”还主要体现在代码约定里。

2. **项目级能力仍通过单文件 pass 间接继承。**
   项目转换能复用单文件重写能力，但缺少一个从项目视角描述“哪些文件被重写、哪些被回退、统计如何汇总”的架构说明。

3. **跳过原因可观测性还不够结构化。**
   目前已有 `SkippedLinqChains` 文本诊断，但还没有更稳定的分类，例如“不支持 query continuation”“匿名类型 record 不可用”“链上没有可降级节点”等。

4. **能力边界还没有文档化。**
   仓库已有实现和测试，但缺少一份专门描述 LINQ 引擎目标、分层、扩展方式和回退策略的设计文档。

## 5. 目标架构

### 5.1 总体主线

LINQ 重写引擎固定为以下四层：

1. **入口编排层**
   由 pipeline pass 决定是否启用 LINQ 重写，并负责在脱糖和改写之后刷新 `SyntaxTree`、`Compilation`、`Cs2jLibrary` 与 `SemanticModel`。

2. **查询脱糖层**
   只处理 query syntax 到方法链的纯语法变换，不引入任何 Java 目标语义。

3. **链分析与规则分发层**
   负责识别可改写的 `Enumerable` 链，抽取 `LinqStep` 序列，分析 lambda、捕获变量、返回类型和集合来源。

4. **展开与回退层**
   对支持的链生成过程式代码；对不支持链记录跳过原因并回退到常规 emit/Stream 路径。

### 5.2 入口编排层

入口编排层的职责必须保持非常清晰：

- 决定是否执行 LINQ 重写
- 先执行 query desugar，再执行 method-chain rewrite
- 每一阶段后刷新语义模型
- 统计 `RewriteCount`
- 把跳过原因写入 diagnostics

这意味着 LINQ 重写不应该直接修改 Java IR，也不应该在 Java emit 之后再做字符串级修补。它的正确位置就是 C# 语法树到 Java visitor 之间的 `Desugar` 阶段。

### 5.3 查询脱糖层

查询脱糖层以 [LinqQueryDesugarer](../src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs) 为核心，负责把：

- `where` 转为 `Where`
- `orderby` / `descending` 转为 `OrderBy` / `OrderByDescending` / `ThenBy` / `ThenByDescending`
- `select` 转为 `Select`
- `group ... by ...` 转为 `GroupBy`

该层只做“变成方法链”的工作，不负责是否最终生成循环。复杂语法如果无法纯语法降级，应明确保持原样并交给后续路径处理，而不是在这里做不可靠猜测。

### 5.4 链分析与规则分发层

该层以 [LinqRewriter](../src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) 和 [LinqStep](../src/CSharpToJava.Core/LinqRewrite/LinqStep.cs) 为核心，职责包括：

- 识别目标是否为支持的 `Enumerable` 方法
- 从终结节点向前回溯并组装完整链
- 判断链上是否包含值得过程化展开的节点
- 对 lambda 做数据流分析，识别外部变量捕获
- 识别匿名类型、返回类型、索引型 lambda、`foreach` 包裹场景

这层的输出不是 Java 代码，而是一个“已分类、已验证”的 LINQ 链执行意图。换句话说，它决定“能不能改、该走哪种规则”，而不是直接承担全部展开细节。

### 5.5 规则层分组

后续应把 LINQ 规则稳定划分为以下几类：

#### 流式中间操作

- `Where`
- `Select`
- `SelectMany`
- `Distinct`
- `Skip` / `Take`
- `SkipWhile` / `TakeWhile`
- `Cast`
- `OfType`
- `Concat`
- `Union`
- `Intersect`
- `Except`
- `OrderBy` / `ThenBy`

#### 终结聚合操作

- `Any`
- `All`
- `First` / `FirstOrDefault`
- `Last` / `LastOrDefault`
- `Single` / `SingleOrDefault`
- `Count` / `LongCount`
- `Contains`
- `ElementAt` / `ElementAtOrDefault`
- `Sum`
- `Average`
- `Min`
- `Max`

#### 物化操作

- `ToList`
- `ToArray`
- `ToDictionary`
- `GroupBy`
- `Reverse`

#### 命令式桥接操作

- `ForEach`
- `foreach` 场景重包裹

这类分组的目的不是重命名现有文件，而是明确未来扩展时应先确定规则类别，再补具体实现和测试。

### 5.6 展开层

展开层负责把链意图转换成以下目标结构之一：

1. `for` / `foreach` 循环
2. 条件过滤与提前返回
3. 累加器变量
4. 中间集合初始化与追加
5. 分组、字典、排序辅助结构
6. 需要时生成捕获变量辅助方法

该层要坚持两个原则：

1. **优先生成结构稳定、可读的 Java 代码**
2. **一旦不能保证正确性，就立即回退，不做半成功展开**

### 5.7 回退与容错

回退策略是该架构的必要组成部分，而不是失败补丁：

- query desugar 失败：保留原语法树，交给后续 visitor/emit
- 链分析失败：记录跳过原因，保留原调用链
- 匿名类型或 record 能力不足：回退到 Stream/原有 emit 路径
- 单条链失败：不影响同文件其他链的转换

这保证了 LINQ 引擎是“增量提升能力”的系统，而不是“全有或全无”的系统。

## 6. 数据模型

### 6.1 `LinqStep`

`LinqStep` 是链分析层的最小稳定单元，至少表达：

- 方法名
- 参数列表
- 原始 invocation
- 可选 lambda 视图

后续若需要增强可观测性，可继续补充：

- 规则类别
- 是否终结操作
- 是否需要索引
- 是否需要 materialization

### 6.2 `Lambda`

`Lambda` 负责把不同形式的匿名函数归一为统一读取接口，降低规则实现对具体语法节点类型的耦合。后续所有新规则都应优先依赖统一 lambda 抽象，而不是在规则内部反复区分具体语法形态。

### 6.3 跳过诊断模型

当前已有 `SkippedLinqChains` 文本集合。后续建议在保持兼容的前提下，为跳过原因引入稳定分类，例如：

- `UnsupportedQueryClause`
- `UnsupportedMethodChain`
- `AnonymousTypeRequiresRecords`
- `SemanticModelUnavailable`
- `RuleExpansionFailed`

这样后续 CLI、测试和 observability 才能稳定统计“为什么没改写”。

## 7. 与 pipeline 的关系

LINQ 重写与整体 pipeline 的关系应固定为：

- 发生在 `Desugar`
- 先于 unsupported-domain / platform-boundary checks
- 先于 Java IR 生成
- 不直接依赖 Java 端 import 或 post-generation rewrite

这样做的原因是：LINQ 重写本质上还是 C# 到 C# 的规范化，不应该和 Java 目标侧的 emit 细节耦合。

对于项目级转换，虽然当前复用单文件能力已经可用，但架构上应明确把“每文件重写计数、回退计数、文件级 warning 汇总”视作项目级结果的一部分。

## 8. 可观测性与验收

### 8.1 需要稳定记录的指标

1. query desugar 次数
2. method-chain rewrite 次数
3. 跳过链数量
4. 按文件统计的 rewrite 数
5. 失败/回退原因分类

### 8.2 验收标准

完成该架构后，LINQ 子系统至少应满足：

1. 查询语法与方法链语法在可支持场景下产生一致结果。
2. 在 `PreferStreamApi = false` 时，常见链路可生成过程式 Java，而不是 `.stream()`.
3. 单条链改写失败不会破坏整个文件转换。
4. 项目级入口可复用同一套 LINQ pass，而不是维护第二套逻辑。
5. 每新增一个操作符类别，都能同时补上规则实现、跳过策略和测试样例。

## 9. 分阶段演进建议

### Phase A：文档化与规则分组

- 固化当前引擎的分层职责
- 补齐规则分类说明
- 明确支持、跳过、回退边界

### Phase B：结构化可观测性

- 为跳过原因增加稳定分类
- 在项目级结果中汇总重写统计
- 让 CLI 或 diagnostics 能输出更可解释的 LINQ 状态

### Phase C：规则扩展

- 扩展更多聚合和物化操作
- 收敛复杂 lambda、索引型操作和捕获变量场景
- 补齐复杂查询语法支持边界

### Phase D：工程化收束

- 进一步减少规则实现中的隐式约定
- 让新增操作符有更稳定的接入点
- 在不牺牲回退能力的前提下提升可维护性

## 10. 结论

`cs2j` 的 LINQ 重写引擎不应被看成若干零散的操作符特判，而应被看成一个独立的“C# 语义规范化子系统”：

- 前半段把 query syntax 归一为方法链
- 中段把方法链归一为规则可识别的步骤序列
- 后半段把可支持链降级为过程式代码，并把不可支持链安全回退

这套架构能同时服务三个目标：继续扩展 20+ 操作符支持、保持 Java Stream 回退能力、以及让后续维护者可以在明确边界内继续演进 LINQ 能力，而不是把复杂度散落到整个转换器里。
