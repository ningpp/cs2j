# 错误06: 构造器引用不明确 — RTree / Rectangle / Cluster (Ambiguous Constructor)

## 受影响文件
**RTree:**
- `EdgeLabelPlacement.java:[162,31]` `[165,31]`
- `SteinerCdt.java:[252,20]`
- `SplineRouter.java:[844,21]`

**Rectangle:**
- `GreedyNodeRailLevelCalculator.java:[116,23]`

**Cluster:**
- `GraphConnectedComponents.java:[195,22]`

## 错误信息
```
对RTree的引用不明确
  RTree(Iterable<SimpleEntry<IRectangle<P>,T>>) 和 RTree(RectangleNode<T,P>) 都匹配

对Rectangle的引用不明确
  Rectangle(Point) 和 Rectangle(Iterable<Point>) 都匹配

对Cluster的引用不明确
  Cluster(Point) 和 Cluster(Iterable<Node>) 都匹配
```

## 原始 C# 代码
```csharp
// EdgeLabelPlacement.cs — RTree 构造
var edgeObstacleMap = new RTree<IObstacle, Point>(
    edgeObstacles.Select(e => new KeyValuePair<IRectangle<Point>, IObstacle>(
        e.Rectangle, e)));

// GraphConnectedComponents.cs — Cluster 构造
var copy = new Cluster(top.Nodes.Select(v => GetCopy(v))) {
    UserData = top,
    ...
};

// GreedyNodeRailLevelCalculator.cs — Rectangle 构造
new Rectangle(nodes.Select(n => n.Center))
```

## 生成的 Java 代码
```java
// EdgeLabelPlacement.java
var edgeObstacleMap = new RTree<IObstacle, Point>(
    edgeObstacles.stream()
        .map(e -> new AbstractMap.SimpleEntry<IRectangle<Point>, IObstacle>(e.getRectangle(), e))
        .collect(Collectors.toCollection(ArrayList::new)));
// ↑ 表达式类型是 ArrayList，同时匹配 Iterable 和 RectangleNode

// GraphConnectedComponents.java
var copy = new Cluster(StreamSupport.stream(top.getNodes().spliterator(), false)
    .map(v -> getCopy(v))
    .collect(Collectors.toCollection(ArrayList::new)));
// ↑ ArrayList 同时匹配 Point(如果有单参数构造器) 和 Iterable<Node>
```

## 错误原因分析

### 根本原因：LINQ `.Select().ToList()` 结果的类型歧义

C# 中 LINQ 的 `Select().ToList()` 或 `.Select()` 返回 `IEnumerable<T>`，编译器可以通过泛型参数精确匹配重载。

Java 中 `stream().map().collect()` 返回 `ArrayList<T>`，而 `ArrayList` 同时是 `Collection`、`Iterable`、`List` 等——当存在多个接受不同集合的构造器时，编译器无法确定使用哪个。

### 转换器缺陷
转换器在生成 Java wrapper 类时创建了多个构造器重载。当传入 collect 结果（`ArrayList`）时产生歧义。解决方案：
1. 在调用处添加显式类型参数或类型转换
2. 减少构造器重载数量
3. 使用工厂方法来代替重载构造器

### 正确的 Java 代码
```java
// 方案: 显式转换为目标类型
var edgeObstacleMap = new RTree<IObstacle, Point>(
    (Iterable<AbstractMap.SimpleEntry<IRectangle<Point>, IObstacle>>) 
    edgeObstacles.stream()
        .map(e -> new AbstractMap.SimpleEntry<>(e.getRectangle(), e))
        .collect(Collectors.toCollection(ArrayList::new)));
```
