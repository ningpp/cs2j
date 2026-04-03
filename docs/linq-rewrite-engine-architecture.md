# LINQ 重写引擎架构设计 / LINQ Rewrite Engine Architecture Design

## 1. 核心思路

将 LINQ 转换拆成两个独立阶段：

1. **前处理（C# → C#）**：在 C# 语法树层面，把 LINQ 表达式重写为等价的过程式 C# 代码。这一步的输入和输出都是合法的 C# 代码。
2. **主转换（C# → Java）**：用 cs2j 主管线把不含 LINQ 的过程式 C# 代码转成 Java。

换句话说，如果原始 C# 项目叫 **A**，那么前处理会先生成一份不含 LINQ 的 C# 项目 **A1**，然后 cs2j 把 **A1** 转成 Java 项目。

这么做的好处是：cs2j 主管线不需要理解 LINQ 语义——它只需要处理普通的循环、条件和方法调用。LINQ 语义的复杂度被完全封装在前处理阶段。

### 示例

#### 原始 C# LINQ 代码（项目 A）

```csharp
public int Method1()
{
    var arr = new[] { 1, 2, 3, 4 };
    var q = 2;
    return arr.Where(x => x > q).Select(x => x + 3).Sum();
}
```

#### 前处理后的过程式 C# 代码（项目 A1）

```csharp
public int Method1()
{
    int[] arr = new[] { 1, 2, 3, 4 };
    int q = 2;
    return this.Method1_ProceduralLinq1(arr, q);
}

private int Method1_ProceduralLinq1(int[] _linqitems, int q)
{
    if (_linqitems == null) throw new ArgumentNullException();

    int num = 0;
    for (int i = 0; i < _linqitems.Length; i++)
    {
        int num2 = _linqitems[i];
        if (num2 > q)
            num += num2 + 3;
    }
    return num;
}
```

注意前处理后的输出仍然是完全合法的 C# 代码：用 `for` 循环代替了 `.Where().Select().Sum()` 链，捕获的外部变量 `q` 变成了辅助方法的参数。这份代码可以直接编译运行，也可以交给 cs2j 主管线做 Java 转换。

## 2. 目标与非目标

### 2.1 目标

1. 把常见 LINQ 链路降级为过程式 C# 代码，消除 cs2j 主管线对 `System.Linq` 的依赖。
2. 前处理的输出必须是语义等价的、可编译的 C# 代码。
3. 支持查询语法（`from…where…select`）和方法链语法（`.Where().Select()`）两种写法。
4. 捕获的外部变量通过辅助方法参数显式传递，不依赖闭包。
5. 不可改写的链路要安全回退，不阻断文件级转换。

### 2.2 非目标

1. 不要求覆盖全部 `System.Linq` API。
2. 不在本阶段处理 `join`、`let`、query continuation 等需要透明标识符的复杂查询。
3. 前处理不涉及任何 Java 语义——它只生产 C# 代码。

## 3. 前处理阶段内部结构

前处理阶段自身分为两步，都在 C# 语法树层面操作：

### 3.1 第一步：查询语法脱糖

由 [LinqQueryDesugarer](../src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs) 完成。

这是纯语法变换，不需要语义模型：

| 查询语法 | 脱糖结果 |
|---------|---------|
| `from x in xs where p select x` | `xs.Where(x => p)` |
| `from x in xs orderby x.Key` | `xs.OrderBy(x => x.Key)` |
| `from x in xs orderby x.A, x.B descending` | `xs.OrderBy(x => x.A).ThenByDescending(x => x.B)` |
| `from x in xs select f(x)` | `xs.Select(x => f(x))` |
| `from x in xs group x by x.Key` | `xs.GroupBy(x => x.Key)` |

如果查询包含 `let`、`join`、`into` 等复杂子句，脱糖器直接跳过，保留原语法树。

### 3.2 第二步：方法链展开为过程式代码

由 [LinqRewriter](../src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) 完成。

这一步需要 Roslyn 语义模型，做以下事情：

1. **识别可改写的 LINQ 链**：从终结方法（如 `Sum()`、`First()`、`ToList()`）向前回溯，收集链上每一步。
2. **数据流分析**：用 `SemanticModel.AnalyzeDataFlow()` 识别 lambda 内引用的外部变量。
3. **生成辅助方法**：
   - 方法名格式为 `原方法名_ProceduralLinq序号`
   - 第一个参数是源集合
   - 后续参数是所有被捕获的外部变量
   - 被 lambda 修改的变量用 `ref` 传递
   - 方法体是展开后的 `for`/`foreach` 循环、条件判断和累加器
4. **替换原调用**：把原 LINQ 链替换为对辅助方法的调用。
5. **注入辅助方法**：把生成的辅助方法添加到当前类/结构体中。

### 3.3 展开规则分组

当前支持的 LINQ 操作符按角色分类：

#### 流式中间操作（在循环体内生成条件/变换）

- `Where` — 条件过滤
- `Select` — 投影变换
- `SelectMany` — 展平嵌套集合
- `Distinct` — 去重（需要 HashSet 辅助）
- `Skip` / `Take` — 跳过/截取（需要计数器）
- `SkipWhile` / `TakeWhile` — 条件跳过/截取
- `Cast` / `OfType` — 类型转换/过滤
- `Concat` / `Union` / `Intersect` / `Except` — 集合运算
- `OrderBy` / `ThenBy` — 排序

#### 终结聚合操作（决定循环的返回值）

- `Sum` / `Average` / `Min` / `Max` — 数值聚合
- `Count` / `LongCount` — 计数
- `Any` / `All` — 存在性判断
- `First` / `FirstOrDefault` / `Last` / `LastOrDefault` — 元素选取
- `Single` / `SingleOrDefault` — 唯一性选取
- `Contains` — 包含检查
- `ElementAt` / `ElementAtOrDefault` — 按索引选取

#### 物化操作（收集结果到集合）

- `ToList` / `ToArray` — 收集到列表/数组
- `ToDictionary` — 收集到字典
- `GroupBy` — 分组
- `Reverse` — 反转

#### 命令式操作

- `ForEach` — 对每个元素执行动作
- `foreach` 语句场景重写

### 3.4 回退策略

回退是架构的一等公民，不是异常处理：

- 查询脱糖失败 → 保留原查询语法，后续由 Java emit 时的 Stream 路径处理
- 链分析发现不支持的操作符 → 记录跳过原因，保留原调用链
- 匿名类型无法表达 → 回退到 Stream 路径
- 单条链失败 → 不影响同文件其他链

这保证前处理是渐进式的：每多支持一个操作符，就多一些链路被降级为过程式代码，剩余的走 Stream 回退。

## 4. 与 cs2j 主管线的关系

### 4.1 执行位置

前处理发生在 cs2j 管线的 `Desugar` 阶段，是所有 pass 中最先执行的。执行入口是 [SingleFileLinqDesugarPass](../src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs)。

```
管线执行顺序：
  1. SingleFileLinqDesugarPass     ← 前处理：LINQ → 过程式 C#
  2. SingleFileCompilationCheckPass
  3. SingleFileUnsupportedDomainCheckPass
  4. SingleFilePlatformBoundaryCheckPass
  5. SingleFileNativeInteropCheckPass
  6. SingleFileContextNormalizationPass
  7. SingleFileJavaEmitPass         ← 主转换：过程式 C# → Java
```

### 4.2 边界清晰

前处理只修改 C# 语法树。它：

- 不生成任何 Java 代码
- 不依赖 Java 侧的 import 或 type mapping
- 不修改 Java IR
- 在完成后重建 `Compilation` 和 `SemanticModel`，让后续 pass 看到的是干净的过程式 C# 语法树

这意味着如果 LINQ 前处理被完全关闭（`EnableLinqRewrite = false`），cs2j 的主管线行为不会受到任何影响——它会走原有的 Stream API 生成路径。

### 4.3 项目级转换

项目级转换复用同一套前处理能力。每个源文件独立执行前处理，文件之间互不干扰。项目级结果中应汇总每文件的重写计数和回退计数。

## 5. 数据模型

### 5.1 `LinqStep`

链分析层的最小单元。每个 `LinqStep` 表达链上的一步操作：

- 方法名（如 `Where`、`Sum`）
- 参数列表
- 原始 `InvocationExpressionSyntax`
- 可选的 `Lambda` 视图

### 5.2 `Lambda`

把 `SimpleLambdaExpressionSyntax`、`ParenthesizedLambdaExpressionSyntax`、`AnonymousMethodExpressionSyntax` 归一为统一接口，提取 `Body` 和 `Parameters`。

### 5.3 `VariableCapture`

数据流分析的结果。每个被 lambda 捕获的外部变量记录为一个 `VariableCapture`：

- `Symbol`：原始符号
- `Changes`：是否被 lambda 修改（决定是否用 `ref` 传递）

### 5.4 辅助方法生成

`LinqRewriter` 在遍历过程中把生成的辅助方法暂存在 `methodsToAddToCurrentType` 列表中。当 visitor 离开类/结构体声明时，把所有暂存的辅助方法一次性注入到类型成员列表。

## 6. 可观测性

### 6.1 指标

前处理过程需要稳定记录以下指标：

1. 查询语法脱糖次数
2. 方法链展开次数
3. 跳过链数量及原因
4. 按文件统计的重写/回退分布

### 6.2 跳过原因分类

当前已有 `SkippedLinqChains` 文本诊断。后续建议引入稳定分类：

- `UnsupportedQueryClause` — 包含 `let`/`join`/`into` 等不支持子句
- `UnsupportedMethodChain` — 链上存在不支持的操作符
- `AnonymousTypeRequiresRecords` — 匿名类型无法在当前配置下表达
- `SemanticModelUnavailable` — 语义模型不可用
- `RuleExpansionFailed` — 规则展开失败

## 7. 验收标准

1. 查询语法和方法链语法在支持范围内产生一致的过程式 C# 代码。
2. 前处理输出的 C# 代码可独立编译。
3. 前处理后的过程式 C# 代码能被 cs2j 主管线正确转换为 Java。
4. 单条链的改写失败不破坏同文件其他链或整个文件的转换。
5. `EnableLinqRewrite = false` 时，主管线行为不受任何影响。

## 8. 当前代码落点

### 核心实现

| 文件 | 职责 |
|-----|-----|
| [LinqQueryDesugarer.cs](../src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs) | 查询语法 → 方法链（纯语法） |
| [LinqRewriter.cs](../src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) | 方法链 → 过程式代码 + 辅助方法 |
| [LinqRewriter.Rules.cs](../src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs) | 各操作符的展开规则 |
| [LinqStep.cs](../src/CSharpToJava.Core/LinqRewrite/LinqStep.cs) | 链步骤模型 |
| [Lambda.cs](../src/CSharpToJava.Core/LinqRewrite/Lambda.cs) | Lambda 统一包装 |
| [CanRewrapForeachVisitor.cs](../src/CSharpToJava.Core/LinqRewrite/CanRewrapForeachVisitor.cs) | foreach 安全性检查 |

### 管线入口

| 文件 | 职责 |
|-----|-----|
| [SingleFilePasses.cs](../src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs) | `SingleFileLinqDesugarPass`：前处理编排 |

### 测试

| 文件 | 覆盖内容 |
|-----|---------|
| [LinqQueryDesugarTests.cs](../tests/CSharpToJava.Tests/LinqQueryDesugarTests.cs) | 查询语法脱糖 + 过程式生成 |
| [LinqChainRefactoringTests.cs](../tests/CSharpToJava.Tests/LinqChainRefactoringTests.cs) | 复杂链路、Zip、匿名类型 |
| [LinqImportAndStreamTests.cs](../tests/CSharpToJava.Tests/LinqImportAndStreamTests.cs) | import 生成与 Stream 兼容性 |

## 9. 演进方向

### Phase A：扩展操作符覆盖

- 补齐更多终结和物化操作的展开规则
- 处理索引型 lambda（如 `Select((x, i) => ...)`）
- 支持更多集合运算

### Phase B：结构化可观测性

- 为跳过原因引入稳定分类枚举
- 在项目级结果中汇总重写统计
- 让 CLI 能输出 LINQ 前处理报告

### Phase C：复杂查询支持

- 探索 `let`、`join`、query continuation 的有限支持
- 处理复杂 lambda 捕获和多层嵌套场景
