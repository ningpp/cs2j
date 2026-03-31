# 错误15: 在非 Stream 类型上调用 collect()/stream() (Collect on Non-Stream)

## 受影响文件
- `LgInteractor.java:[2019,35]` — `.collect()` 在 `ArrayList` 上调用
- `InitialLayoutByCluster.java:[387,163]` — `.collect()` 在 `Iterable` 上调用
- `SplineRouter.java:[382,69]` `[721,59]` — `.collect()` 在 `SimpleEntry`/`ArrayList` 上调用
- `NodePositionsAdjuster.java:[262,213]` — `.collect()` 在 `Iterable` 上调用
- `RecoveryLayeredLayoutEngine.java:[1071,103]` `[1104,39]` — `.stream()` 在 `Integer` 上调用

## 错误信息
```
找不到符号
  符号:   方法 collect(Collector<...>)
  位置: 类型为ArrayList<T>的变量 xxx

找不到符号
  符号:   方法 stream()
  位置: 类 java.lang.Integer
```

## 原始 C# 代码
```csharp
// LgInteractor.cs — LINQ 链式调用
var neighb = ni.GeometryNode.OutEdges
    .Where(e => ...)
    .Select(e => ...)
    .ToList();
    
neighb.AddRange(ni.GeometryNode.InEdges
    .Where(e => ...)
    .Select(e => ...));

// InitialLayoutByCluster.cs — SelectMany 
graphs.SelectMany(g => g.Edges).ToCollection()

// RecoveryLayeredLayoutEngine.cs — GroupBy + SelectMany
dictionary.GroupBy(p => p.Key)
    .SelectMany(group => group.Select(p => p.Value))
```

## 生成的 Java 代码
```java
// LgInteractor.java — 错误地在 ArrayList 结果上再次调用 collect
ArrayList<LgNodeInfo> neighb = StreamSupport.stream((...).spliterator(), false)
    .collect(Collectors.toCollection(ArrayList::new));
neighb.collect(Collectors.toCollection(ArrayList::new));
// ↑ 错误: ArrayList 没有 collect 方法

// RecoveryLayeredLayoutEngine.java — stream() 在标量上调用
entry.getValue().stream()  // getValue() 返回 Integer，不是集合
```

## 错误原因分析

### 根本原因：C# LINQ 链与 Java Stream 的语义边界混淆

C# 中所有 LINQ 方法都返回 `IEnumerable<T>`，可以无限链式调用。Java 中：
- `Stream` 有 `.collect()`、`.map()` 等方法
- `ArrayList` / `Iterable` 没有 `.collect()` 方法
- `Integer` / 其他标量没有 `.stream()` 方法

转换器有时会在已经 `collect` 到 `ArrayList` 的结果上再次调用 Stream 方法，或在标量值上调用 `.stream()`。

### 转换器缺陷
1. **collect 后继续链式调用**：转换器在某些 LINQ 链中，先 collect 得到 `ArrayList`，然后又尝试对其调用 `.collect()`。应该在需要继续链式操作时保持 Stream 状态。
2. **GroupBy/SelectMany 映射错误**：`groupBy` + `flatMap` 的映射没有正确处理，导致在非集合类型上调用 `.stream()`。
3. **Select 在数组上的错误**：`_edges.Select(...)` 被保留为原样而不是转换为 `Arrays.stream(_edges).map(...)`。

### 正确的 Java 代码
```java
// 确保在 collect 后如需继续操作，要再次调用 .stream()
ArrayList<LgNodeInfo> neighb = ... .collect(Collectors.toCollection(ArrayList::new));
neighb.addAll(... .stream()
    .filter(e -> ...)
    .map(e -> ...)
    .collect(Collectors.toCollection(ArrayList::new)));

// GroupBy 场景需要正确使用 Collectors.groupingBy + flatMap
map.entrySet().stream()
    .flatMap(entry -> entry.getValue().stream())  // getValue() 必须返回 Collection
    .collect(Collectors.toList());
```
