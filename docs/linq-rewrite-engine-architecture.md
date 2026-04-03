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

## 2. 目标

### 2.1 总体目标

**支持所有 `System.Linq.Enumerable` API**——把 LINQ 方法链和查询语法全部降级为过程式 C# 代码，彻底消除 cs2j 主管线对 `System.Linq` 的依赖。

### 2.2 具体目标

1. 覆盖 `System.Linq.Enumerable` 的全部公开方法及其所有重载。
2. 覆盖 C# 查询语法的全部子句：`from`、`where`、`select`、`orderby`、`group by`、`let`、`join`、`into`（query continuation）、多 `from`。
3. 前处理的输出必须是语义等价的、可编译的 C# 代码。
4. 支持查询语法（`from…where…select`）和方法链语法（`.Where().Select()`）两种写法，产出一致的过程式代码。
5. 捕获的外部变量通过辅助方法参数显式传递，不依赖闭包。
6. 前处理不涉及任何 Java 语义——它只生产 C# 代码。
7. 单条链失败不阻断同文件其他链或整个文件的转换。

## 3. 需要覆盖的完整 API 清单

以下是 `System.Linq.Enumerable` 全部公开方法，按类别分组。每个方法需要支持其所有公开重载。

### 3.1 投影与变换

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `Select` | `(Func<T,R>)`, `(Func<T,int,R>)` | 投影变换，索引型重载需在循环中维护计数器 |
| `SelectMany` | `(Func<T,IEnumerable<R>>)`, `(Func<T,int,IEnumerable<R>>)`, `(Func<T,IEnumerable<C>>, Func<T,C,R>)` | 展平嵌套集合，查询语法的多 `from` 子句编译为此 |
| `Cast<R>` | `()` | 强制类型转换每个元素 |
| `OfType<R>` | `()` | 类型过滤 |

### 3.2 过滤

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `Where` | `(Func<T,bool>)`, `(Func<T,int,bool>)` | 条件过滤，索引型重载 |
| `Distinct` | `()`, `(IEqualityComparer<T>)` | 去重 |
| `DistinctBy` | `(Func<T,K>)`, `(Func<T,K>, IEqualityComparer<K>)` | 按 key 去重（.NET 6+） |
| `Skip` | `(int)` | 跳过前 N 个 |
| `SkipLast` | `(int)` | 跳过最后 N 个 |
| `SkipWhile` | `(Func<T,bool>)`, `(Func<T,int,bool>)` | 条件跳过 |
| `Take` | `(int)`, `(Range)` | 截取前 N 个，Range 重载（.NET 6+） |
| `TakeLast` | `(int)` | 截取最后 N 个 |
| `TakeWhile` | `(Func<T,bool>)`, `(Func<T,int,bool>)` | 条件截取 |
| `Append` | `(T)` | 在末尾追加一个元素 |
| `Prepend` | `(T)` | 在开头插入一个元素 |
| `DefaultIfEmpty` | `()`, `(T)` | 空序列返回包含默认值的单元素序列 |

### 3.3 排序

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `OrderBy` | `(Func<T,K>)`, `(Func<T,K>, IComparer<K>)` | 升序排序 |
| `OrderByDescending` | `(Func<T,K>)`, `(Func<T,K>, IComparer<K>)` | 降序排序 |
| `ThenBy` | `(Func<T,K>)`, `(Func<T,K>, IComparer<K>)` | 次级升序 |
| `ThenByDescending` | `(Func<T,K>)`, `(Func<T,K>, IComparer<K>)` | 次级降序 |
| `Order` | `()`, `(IComparer<T>)` | 自然排序（.NET 7+） |
| `OrderDescending` | `()`, `(IComparer<T>)` | 自然降序（.NET 7+） |
| `Reverse` | `()` | 反转序列 |

### 3.4 集合运算

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `Concat` | `(IEnumerable<T>)` | 连接两个序列 |
| `Union` | `(IEnumerable<T>)`, `(IEnumerable<T>, IEqualityComparer<T>)` | 并集 |
| `UnionBy` | `(IEnumerable<T>, Func<T,K>)` | 按 key 并集（.NET 6+） |
| `Intersect` | `(IEnumerable<T>)`, `(IEnumerable<T>, IEqualityComparer<T>)` | 交集 |
| `IntersectBy` | `(IEnumerable<K>, Func<T,K>)` | 按 key 交集（.NET 6+） |
| `Except` | `(IEnumerable<T>)`, `(IEnumerable<T>, IEqualityComparer<T>)` | 差集 |
| `ExceptBy` | `(IEnumerable<K>, Func<T,K>)` | 按 key 差集（.NET 6+） |
| `Zip` | `(IEnumerable<S>, Func<T,S,R>)`, `(IEnumerable<S>)` | 配对合并 |

### 3.5 聚合

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `Aggregate` | `(Func<T,T,T>)`, `(S,Func<S,T,S>)`, `(S,Func<S,T,S>,Func<S,R>)` | 通用归约 |
| `AggregateBy` | `(Func<T,K>, S, Func<S,T,S>)` | 按 key 分组归约（.NET 9+） |
| `Sum` | `()`, `(Func<T,N>)` — N ∈ {int,long,float,double,decimal} + Nullable | 求和 |
| `Average` | 同 `Sum` 的数值类型重载 | 平均值 |
| `Min` | `()`, `(Func<T,R>)` | 最小值 |
| `MinBy` | `(Func<T,K>)` | 按 key 取最小元素（.NET 6+） |
| `Max` | `()`, `(Func<T,R>)` | 最大值 |
| `MaxBy` | `(Func<T,K>)` | 按 key 取最大元素（.NET 6+） |
| `Count` | `()`, `(Func<T,bool>)` | 计数 |
| `LongCount` | `()`, `(Func<T,bool>)` | long 计数 |
| `TryGetNonEnumeratedCount` | `(out int)` | 尝试不枚举取 count （.NET 6+） |

### 3.6 存在性与包含

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `Any` | `()`, `(Func<T,bool>)` | 是否有元素（满足条件） |
| `All` | `(Func<T,bool>)` | 是否全部满足 |
| `Contains` | `(T)`, `(T, IEqualityComparer<T>)` | 是否包含指定元素 |
| `SequenceEqual` | `(IEnumerable<T>)`, `(IEnumerable<T>, IEqualityComparer<T>)` | 序列相等判断 |

### 3.7 元素选取

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `First` | `()`, `(Func<T,bool>)` | 第一个元素 |
| `FirstOrDefault` | `()`, `(Func<T,bool>)`, `(T)`, `(Func<T,bool>, T)` | 第一个或默认 |
| `Last` | `()`, `(Func<T,bool>)` | 最后一个元素 |
| `LastOrDefault` | `()`, `(Func<T,bool>)`, `(T)`, `(Func<T,bool>, T)` | 最后一个或默认 |
| `Single` | `()`, `(Func<T,bool>)` | 唯一元素 |
| `SingleOrDefault` | `()`, `(Func<T,bool>)`, `(T)`, `(Func<T,bool>, T)` | 唯一或默认 |
| `ElementAt` | `(int)`, `(Index)` | 按索引取元素 |
| `ElementAtOrDefault` | `(int)`, `(Index)` | 按索引或默认 |

### 3.8 物化

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `ToList` | `()` | 物化为 List |
| `ToArray` | `()` | 物化为数组 |
| `ToDictionary` | `(Func<T,K>)`, `(Func<T,K>, Func<T,V>)`, + IEqualityComparer 重载 | 物化为字典 |
| `ToLookup` | `(Func<T,K>)`, `(Func<T,K>, Func<T,V>)`, + IEqualityComparer 重载 | 物化为 Lookup |
| `ToHashSet` | `()`, `(IEqualityComparer<T>)` | 物化为 HashSet |

### 3.9 分组与连接

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `GroupBy` | `(Func<T,K>)`, `(Func<T,K>,Func<T,E>)`, `(Func<T,K>,Func<K,IEnumerable<T>,R>)`, + IEqualityComparer 重载 | 分组 |
| `Join` | `(IEnumerable<I>, Func<O,K>, Func<I,K>, Func<O,I,R>)` + IEqualityComparer | 内连接。查询语法 `join` 编译为此 |
| `GroupJoin` | `(IEnumerable<I>, Func<O,K>, Func<I,K>, Func<O,IEnumerable<I>,R>)` | 组连接。查询语法 `join…into` 编译为此 |
| `Chunk` | `(int)` | 按大小分块（.NET 6+） |

### 3.10 生成方法

| 方法 | 说明 |
|-----|-----|
| `Enumerable.Empty<T>()` | 空序列 → `new T[0]` 或 `Array.Empty<T>()` |
| `Enumerable.Range(start, count)` | 整数区间 → `for` 循环 |
| `Enumerable.Repeat(element, count)` | 重复元素 → `for` 循环填充数组 |

### 3.11 其他

| 方法 | 主要重载 | 说明 |
|-----|---------|-----|
| `AsEnumerable` | `()` | 编译时类型转为 `IEnumerable<T>`，运行时无操作 → 直接移除 |
| `ForEach` | `List<T>.ForEach(Action<T>)` | 非标准 LINQ，但常见 → 展开为 `foreach` |

### 3.12 查询语法子句对照

所有 C# 查询语法子句都需要脱糖为对应的方法链，然后走统一的方法链展开路径：

| 查询语法子句 | 编译为 | 说明 |
|------------|-------|-----|
| `from x in xs` | 源集合 | 初始 `from` 提供数据源 |
| `from y in x.Items` | `.SelectMany(x => x.Items, (x, y) => ...)` | 多 `from` → SelectMany |
| `where p` | `.Where(x => p)` | |
| `orderby k` / `orderby k descending` | `.OrderBy(x => k)` / `.OrderByDescending(x => k)` | |
| `select expr` | `.Select(x => expr)` | 恒等投影可省略 |
| `group expr by key` | `.GroupBy(x => key, x => expr)` | |
| `let y = expr` | `.Select(x => new { x, y = expr })` | 引入透明标识符 |
| `join y in ys on x.K equals y.K` | `.Join(ys, x => x.K, y => y.K, (x, y) => ...)` | 内连接 |
| `join y in ys on x.K equals y.K into g` | `.GroupJoin(ys, x => x.K, y => y.K, (x, g) => ...)` | 组连接 |
| `… into g …` (query continuation) | 包裹前段为子查询，`g` 作为后续 `from` 的数据源 | |

## 4. 当前实现状态

### 4.1 已实现的操作符（36 个 + 变体）

#### 中间操作（18 个）

`Where`、`Select`、`SelectMany`、`Cast`、`OfType`、`Distinct`、`Skip`、`Take`、`SkipWhile`、`TakeWhile`、`OrderBy`、`OrderByDescending`、`ThenBy`、`ThenByDescending`、`Concat`、`Union`、`Intersect`、`Except`

#### 终结操作（18 个 + 变体）

`Sum`（含 selector 重载和所有数值类型）、`Average`、`Min`、`Max`、`Count`、`LongCount`、`Any`（含 predicate 重载）、`All`、`Contains`、`First`/`FirstOrDefault`、`Last`/`LastOrDefault`、`Single`/`SingleOrDefault`、`ElementAt`/`ElementAtOrDefault`、`Aggregate`（含 seed 重载）、`ToList`、`ToArray`、`ToHashSet`、`ToDictionary`（双参数重载）、`Reverse`、`GroupBy`（含 element selector 重载）、`ForEach`

#### 查询语法（5 种子句）

`from`（初始）、`where`、`orderby`/`descending`、`select`、`group by`

### 4.2 已定义但未完整实现的操作符

| 操作符 | 状态 |
|-------|------|
| `Zip` | 有方法常量定义和 Stream 路径测试，但 `TryRewrite` 中无过程式展开规则 |
| `SequenceEqual` | 有方法常量定义和参数白名单，但 `TryRewrite` 中无过程式展开规则 |

### 4.3 尚未实现的操作符

以下操作符在当前代码中没有任何定义或处理：

| 类别 | 操作符 |
|-----|-------|
| 过滤 | `SkipLast`、`TakeLast`、`Append`、`Prepend`、`DefaultIfEmpty`、`DistinctBy` |
| 排序 | `Order`、`OrderDescending`（.NET 7+ 无参排序） |
| 集合运算 | `UnionBy`、`IntersectBy`、`ExceptBy` |
| 聚合 | `MinBy`、`MaxBy`、`AggregateBy`、`TryGetNonEnumeratedCount` |
| 元素选取 | `FirstOrDefault(defaultValue)`、`LastOrDefault(defaultValue)`、`SingleOrDefault(defaultValue)` — 带默认值的重载 |
| 物化 | `ToLookup`、`ToDictionary`（单参数 key-only 重载） |
| 连接 | `Join`、`GroupJoin`、`Chunk` |
| 生成 | `Enumerable.Empty`、`Enumerable.Range`、`Enumerable.Repeat` |
| 其他 | `AsEnumerable`（编译时无操作，运行时移除） |
| 索引型重载 | `Where((x,i)=>...)`、`Select((x,i)=>...)`、`SkipWhile((x,i)=>...)`、`TakeWhile((x,i)=>...)` |

### 4.4 尚未支持的查询语法子句

| 子句 | 编译目标 | 难点 |
|-----|---------|-----|
| `let y = expr` | `.Select(x => new { x, y })` | 引入透明标识符和匿名类型，后续子句需通过 `<>h__TransparentIdentifier` 访问 |
| `join … on … equals …` | `.Join(...)` | 四参数方法，需两侧 key selector 和 result selector |
| `join … into g` | `.GroupJoin(...)` | 同上，且 result selector 接收 `IEnumerable<T>` 参数 |
| 多 `from`（非初始） | `.SelectMany(...)` | 需要 result selector 保留外层范围变量 |
| `… into g …` (query continuation) | 子查询包裹 | 前段查询成为子表达式 |

## 5. 前处理阶段内部结构

前处理阶段分为两步，都在 C# 语法树层面操作：

### 5.1 第一步：查询语法脱糖

由 [LinqQueryDesugarer](../src/CSharpToJava.Core/LinqRewrite/LinqQueryDesugarer.cs) 完成。

这一步把查询表达式统一转换为方法链调用，让第二步只需要处理一种形式。

#### 当前已支持的脱糖

| 查询语法 | 脱糖结果 |
|---------|---------|
| `from x in xs where p select x` | `xs.Where(x => p)` |
| `from x in xs orderby x.Key` | `xs.OrderBy(x => x.Key)` |
| `from x in xs orderby x.A, x.B descending` | `xs.OrderBy(x => x.A).ThenByDescending(x => x.B)` |
| `from x in xs select f(x)` | `xs.Select(x => f(x))` |
| `from x in xs group x by x.Key` | `xs.GroupBy(x => x.Key)` |

#### 需要新增的脱糖

| 查询语法 | 脱糖结果 | 关键挑战 |
|---------|---------|---------|
| `let y = expr` | `xs.Select(x => new { x, y = expr })` + 后续子句透明标识符替换 | 需要引入匿名类型或显式类型，并重写后续所有 lambda 中对 `x`/`y` 的引用 |
| `join y in ys on x.K equals y.K` | `.Join(ys, x => x.K, y => y.K, (x, y) => new { x, y })` | 需要合成 result selector |
| `join y in ys on x.K equals y.K into g` | `.GroupJoin(ys, x => x.K, y => y.K, (x, g) => new { x, g })` | 同上 |
| `from y in x.Items` (非初始 from) | `.SelectMany(x => x.Items, (x, y) => new { x, y })` | 需要 result selector 保留外层范围变量 |
| `… into g select g.Count()` | 将 into 之前的查询包裹为子表达式，g 作为新的范围变量 | 需要递归处理 |

### 5.2 第二步：方法链展开为过程式代码

由 [LinqRewriter](../src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs) 完成。

这一步需要 Roslyn 语义模型，做以下事情：

1. **识别可改写的 LINQ 链**：从终结方法（如 `Sum()`、`First()`、`ToList()`）向前回溯，收集链上每一步为 `LinqStep`。
2. **数据流分析**：用 `SemanticModel.AnalyzeDataFlow()` 识别 lambda 内引用的外部变量。
3. **生成辅助方法**：
   - 方法名格式为 `原方法名_ProceduralLinq序号`
   - 第一个参数是源集合
   - 后续参数是所有被捕获的外部变量
   - 被 lambda 修改的变量用 `ref` 传递
   - 方法体是展开后的 `for`/`foreach` 循环、条件判断和累加器
4. **替换原调用**：把原 LINQ 链替换为对辅助方法的调用。
5. **注入辅助方法**：把生成的辅助方法添加到当前类/结构体中。

### 5.3 展开规则分组

全部操作符按角色分为四组，展开策略不同：

#### 流式中间操作

在循环体内生成条件/变换语句，不改变整体循环结构：

| 操作 | 展开策略 |
|-----|---------|
| `Where` / `Where(index)` | `if (!predicate) continue;` |
| `Select` / `Select(index)` | 赋值变换 |
| `SelectMany` | 内层 `foreach` 嵌套 |
| `Cast<R>` | `(R)item` 强制转换 |
| `OfType<R>` | `if (item is R)` 类型检查 |
| `Distinct` | `HashSet.Add()` 判断 |
| `DistinctBy` | `HashSet.Add(keySelector(item))` |
| `Skip` / `Take` | 计数器 + `continue`/`break` |
| `SkipLast` / `TakeLast` | 环形缓冲区（Ring Buffer） |
| `SkipWhile` / `TakeWhile` | 布尔标记 + 条件 |
| `Append` / `Prepend` | 循环前后追加单元素 |
| `DefaultIfEmpty` | 空集合检测 + 默认值注入 |
| `Concat` | 两个序列顺序遍历 |
| `Union` / `UnionBy` | HashSet 辅助去重 + 两序列遍历 |
| `Intersect` / `IntersectBy` | 先物化第二个序列到 HashSet，再遍历第一个 |
| `Except` / `ExceptBy` | 同上，取反 |
| `Zip` | 双迭代器同步推进 |
| `OrderBy` / `ThenBy` 等 | 先物化到 `List<T>`，排序，再遍历 |
| `Chunk` | 按 size 分批收集 |

#### 终结聚合操作

决定循环的返回值类型和累加逻辑：

| 操作 | 展开策略 |
|-----|---------|
| `Sum` | `accumulator += item` |
| `Average` | `sum += item; count++` 最后 `sum / count` |
| `Min` / `Max` / `MinBy` / `MaxBy` | 比较更新最值 |
| `Count` / `LongCount` | 计数器自增 |
| `Any` | 找到即 `return true`，循环结束 `return false` |
| `All` | 找到不满足即 `return false`，循环结束 `return true` |
| `Contains` | 找到即 `return true` |
| `First` / `Last` / `Single` 及 OrDefault | 条件匹配 + 提前返回 / 抛异常 |
| `ElementAt` / `ElementAtOrDefault` | 计数到目标索引 |
| `Aggregate` | `accumulator = func(accumulator, item)` |
| `SequenceEqual` | 双迭代器逐元素比较 |

#### 物化操作

收集结果到目标集合：

| 操作 | 展开策略 |
|-----|---------|
| `ToList` | `List<T> result = new(); … result.Add(item)` |
| `ToArray` | 先收集到 List，最后 `.ToArray()` |
| `ToDictionary` | `Dictionary<K,V> result = new(); … result.Add(key, value)` |
| `ToLookup` | `Dictionary<K, List<V>>` 分组收集 |
| `ToHashSet` | `HashSet<T> result = new(); … result.Add(item)` |
| `GroupBy` | 按 key 分组到 `Dictionary<K, List<V>>`，转为 `IGrouping` |
| `Reverse` | 先收集到 List，再 `.Reverse()` |

#### 生成方法

不从现有集合出发，而是直接构造序列：

| 操作 | 展开策略 |
|-----|---------|
| `Enumerable.Empty<T>()` | `Array.Empty<T>()` 或 `new T[0]` |
| `Enumerable.Range(start, count)` | `for (int i = start; i < start + count; i++)` |
| `Enumerable.Repeat(element, count)` | `for (int i = 0; i < count; i++) yield element` |

### 5.4 回退策略

回退是架构的一等公民，不是异常处理：

- 查询脱糖失败 → 保留原查询语法，后续由 Java emit 时的 Stream 路径处理
- 链分析发现不支持的操作符 → 记录跳过原因，保留原调用链
- 匿名类型无法表达 → 回退到 Stream 路径
- 单条链失败 → 不影响同文件其他链

这保证前处理是渐进式的：每多支持一个操作符，就多一些链路被降级为过程式代码，剩余的走 Stream 回退。最终目标是消除所有回退。

## 6. 与 cs2j 主管线的关系

### 6.1 执行位置

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

### 6.2 边界清晰

前处理只修改 C# 语法树。它：

- 不生成任何 Java 代码
- 不依赖 Java 侧的 import 或 type mapping
- 不修改 Java IR
- 在完成后重建 `Compilation` 和 `SemanticModel`，让后续 pass 看到的是干净的过程式 C# 语法树

这意味着如果 LINQ 前处理被完全关闭（`EnableLinqRewrite = false`），cs2j 的主管线行为不会受到任何影响——它会走原有的 Stream API 生成路径。

### 6.3 项目级转换

项目级转换复用同一套前处理能力。每个源文件独立执行前处理，文件之间互不干扰。项目级结果中应汇总每文件的重写计数和回退计数。

## 7. 数据模型

### 7.1 `LinqStep`

链分析层的最小单元。每个 `LinqStep` 表达链上的一步操作：

- 方法名（如 `Where`、`Sum`）
- 参数列表
- 原始 `InvocationExpressionSyntax`
- 可选的 `Lambda` 视图

### 7.2 `Lambda`

把 `SimpleLambdaExpressionSyntax`、`ParenthesizedLambdaExpressionSyntax`、`AnonymousMethodExpressionSyntax` 归一为统一接口，提取 `Body` 和 `Parameters`。

### 7.3 `VariableCapture`

数据流分析的结果。每个被 lambda 捕获的外部变量记录为一个 `VariableCapture`：

- `Symbol`：原始符号
- `Changes`：是否被 lambda 修改（决定是否用 `ref` 传递）

### 7.4 辅助方法生成

`LinqRewriter` 在遍历过程中把生成的辅助方法暂存在 `methodsToAddToCurrentType` 列表中。当 visitor 离开类/结构体声明时，把所有暂存的辅助方法一次性注入到类型成员列表。

## 8. 可观测性

### 8.1 指标

前处理过程需要稳定记录以下指标：

1. 查询语法脱糖次数
2. 方法链展开次数
3. 跳过链数量及原因
4. 按文件统计的重写/回退分布
5. 按操作符统计的覆盖率（已支持/遇到但跳过）

### 8.2 跳过原因分类

当前已有 `SkippedLinqChains` 文本诊断。后续建议引入稳定分类：

- `UnsupportedQueryClause` — 查询子句尚未实现
- `UnsupportedMethodChain` — 链上存在尚未实现的操作符
- `UnsupportedOverload` — 操作符已支持但该重载未实现
- `AnonymousTypeRequiresRecords` — 匿名类型无法在当前配置下表达
- `SemanticModelUnavailable` — 语义模型不可用
- `RuleExpansionFailed` — 规则展开失败
- `TransparentIdentifierUnsupported` — `let`/`join` 引入的透明标识符未实现

## 9. 验收标准

1. `System.Linq.Enumerable` 全部公开方法的全部公开重载都能被展开为过程式 C# 代码。
2. 全部查询语法子句（`from`/`where`/`select`/`orderby`/`group by`/`let`/`join`/`into`/多 `from`）都能被脱糖并展开。
3. 查询语法和方法链语法对同一逻辑产生一致的过程式 C# 代码。
4. 前处理输出的 C# 代码可独立编译。
5. 前处理后的过程式 C# 代码能被 cs2j 主管线正确转换为 Java。
6. 单条链的改写失败不破坏同文件其他链或整个文件的转换。
7. `EnableLinqRewrite = false` 时，主管线行为不受任何影响。
8. 每个新增操作符都有对应的单元测试。

## 10. 当前代码落点

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

## 11. 分阶段演进计划

### Phase 1：补齐已定义操作符的过程式展开

**目标**：为所有已定义方法常量但缺少 `TryRewrite` 规则的操作符补全实现。

- `Zip` — 双迭代器同步遍历，元素配对
- `SequenceEqual` — 双迭代器逐元素比较
- `ToDictionary` 单参数重载 — `ToDictionary(keySelector)` 以元素自身为 value

### Phase 2：补齐索引型重载

**目标**：支持所有带 `(Func<T,int,R>)` 索引参数的重载。

- `Where((x, i) => ...)` — 在循环中维护索引计数器
- `Select((x, i) => ...)` — 同上
- `SkipWhile((x, i) => ...)` — 同上
- `TakeWhile((x, i) => ...)` — 同上
- `SelectMany((x, i) => ...)` — 同上

### Phase 3：新增过滤/切片操作

**目标**：补齐常见的过滤和切片操作。

- `SkipLast(n)` — 环形缓冲区延迟输出
- `TakeLast(n)` — 环形缓冲区保留最后 N 个
- `Append(element)` — 循环后追加
- `Prepend(element)` — 循环前输出
- `DefaultIfEmpty()` / `DefaultIfEmpty(value)` — 空序列检测

### Phase 4：新增 .NET 6+ By 系列操作

**目标**：支持 .NET 6+ 引入的 `*By` 方法。

- `DistinctBy(keySelector)` — HashSet 辅助，按 key 去重
- `UnionBy` / `IntersectBy` / `ExceptBy` — 按 key 的集合运算
- `MinBy` / `MaxBy` — 按 key 取极值元素
- `Chunk(size)` — 按大小分块

### Phase 5：连接操作（Join / GroupJoin）

**目标**：支持 `Join` 和 `GroupJoin` 方法以及对应的查询语法子句。

- `Join(inner, outerKey, innerKey, resultSelector)` — 双层循环或 HashJoin
- `GroupJoin(inner, outerKey, innerKey, resultSelector)` — 先分组内层，再匹配
- 查询语法 `join … on … equals …` 脱糖
- 查询语法 `join … into g` 脱糖

### Phase 6：查询语法完整支持

**目标**：支持 `let` 子句、多 `from` 子句、query continuation。

- `let` 子句 → `.Select(x => new { x, y = expr })` + 透明标识符重写
- 多 `from` → `.SelectMany(x => collection, (x, y) => new { x, y })` + 透明标识符
- `into` (query continuation) → 子查询包裹
- 透明标识符消除：生成具名 C# 类替代匿名类型，或在展开时直接内联

### Phase 7：物化与生成方法

**目标**：补齐剩余物化和生成方法。

- `ToLookup(keySelector)` / `ToLookup(keySelector, elementSelector)` — `Dictionary<K, List<V>>` 收集
- `AsEnumerable()` — 直接移除（编译时类型擦除）
- `Enumerable.Empty<T>()` → `Array.Empty<T>()`
- `Enumerable.Range(start, count)` → `for` 循环
- `Enumerable.Repeat(element, count)` → `for` 循环

### Phase 8：带默认值的 OrDefault 重载 + Comparer 重载

**目标**：支持所有带自定义默认值和自定义比较器的重载。

- `FirstOrDefault(defaultValue)` / `LastOrDefault(defaultValue)` / `SingleOrDefault(defaultValue)` — 用用户指定默认值替代 `default(T)`
- `Distinct(IEqualityComparer)` / `Union(…, comparer)` / `Intersect(…, comparer)` / `Except(…, comparer)` — 传递 comparer 到 HashSet 构造器
- `OrderBy(…, IComparer)` / `ThenBy(…, IComparer)` — 传递 comparer 到排序方法
- `Contains(value, IEqualityComparer)` — 用 comparer.Equals 替代 == 判断

### Phase 9：.NET 7+/9+ 新增方法

**目标**：按需支持新版 .NET 引入的 LINQ 方法。

- `Order()` / `OrderDescending()` — 无参排序（.NET 7+）
- `AggregateBy(keySelector, seed, func)` — 按 key 分组归约（.NET 9+）
- `TryGetNonEnumeratedCount(out int)` — 特殊方法，编译为 `ICollection.Count` 检查
- `Index()` — 包裹为 `(index, element)` 元组（.NET 9+）
- `CountBy(keySelector)` — 按 key 计数（.NET 9+）

### Phase 10：结构化可观测性

**目标**：让 LINQ 前处理的覆盖率和效果完全可观测。

- 为跳过原因引入稳定分类枚举
- 在项目级结果中汇总重写统计
- 让 CLI 能输出 LINQ 前处理报告
- 输出"未覆盖操作符"清单，按使用频率排序
