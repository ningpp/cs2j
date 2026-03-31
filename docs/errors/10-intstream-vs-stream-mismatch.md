# 错误10: flatMap 返回 Stream 而非 IntStream (Stream vs IntStream Mismatch)

## 受影响文件
- `LayeredLayoutEngine.java:[1254,50]`
- `Ordering.java:[843,43]`
- `RecoveryLayeredLayoutEngine.java:[924,50]`
- `TwoLayerFlatEdgeRouter.java:[123,55]`

## 错误信息
```
不兼容的类型: lambda 表达式中的返回类型错误
  java.util.stream.Stream<PolyIntEdge>无法转换为java.util.stream.IntStream

不兼容的类型: lambda 表达式中的返回类型错误
  不存在类型变量T的实例, 以使Stream<T>与IntStream一致
```

## 原始 C# 代码
```csharp
// LayeredLayoutEngine.cs
// C# LINQ — SelectMany 返回 IEnumerable<T>，会自动展开
static IEnumerable<IntPair> GetFlatPairs(int[] layer, int[] layering, 
    BasicGraphOnEdges<PolyIntEdge> intGraph) {
    return new Set<IntPair>(
        layer
            .Where(v => v < intGraph.NodeCount)
            .SelectMany(v => intGraph.OutEdges(v))  // 返回 IEnumerable<PolyIntEdge>
            .Where(edge => layering[edge.Source] == layering[edge.Target])
            .Select(edge => new IntPair(edge.Source, edge.Target)));
}
```

C# 中 `int[].Where()` 返回 `IEnumerable<int>`，接着 `SelectMany(v => ...)` 返回 `IEnumerable<PolyIntEdge>`，类型统一为引用类型序列。

## 生成的 Java 代码
```java
static Iterable<IntPair> getFlatPairs(int[] layer, int[] layering,
    BasicGraphOnEdges<PolyIntEdge> intGraph) {
    return new Set<IntPair>(Arrays.stream(layer)    // ← IntStream
        .filter(v -> v < intGraph.getNodeCount())   // ← IntStream
        .flatMap(v -> intGraph.outEdges(v).stream()) // ← 错误！IntStream.flatMap 需要返回 IntStream
        .filter(edge -> layering[edge.getSource()] == layering[edge.getTarget()])
        .map(edge -> new IntPair(edge.getSource(), edge.getTarget()))
        .collect(Collectors.toCollection(ArrayList::new)));
}
```

## 错误原因分析

### 根本原因：`Arrays.stream(int[])` 返回 `IntStream` 而非 `Stream<Integer>`

Java 中 `Arrays.stream(int[])` 返回原始类型的 `IntStream`。`IntStream.flatMap()` 期望 lambda 返回 `IntStream`，但 `intGraph.outEdges(v).stream()` 返回 `Stream<PolyIntEdge>`——这两者不兼容。

C# 不区分原始流和对象流，`int[]` 的 LINQ 操作和引用类型一样。

### 转换器缺陷
当 `int[]` 上的 LINQ 链中出现 `SelectMany` 等需要类型转换的操作时，转换器需要在合适的位置从 `IntStream` 切换到 `Stream<Integer>`（使用 `boxed()`），或直接使用 `Stream<Integer>` 开始。

### 正确的 Java 代码
```java
static Iterable<IntPair> getFlatPairs(int[] layer, int[] layering,
    BasicGraphOnEdges<PolyIntEdge> intGraph) {
    return new Set<IntPair>(Arrays.stream(layer)
        .filter(v -> v < intGraph.getNodeCount())
        .boxed()                                        // ← IntStream → Stream<Integer>
        .flatMap(v -> intGraph.outEdges(v).stream())    // 现在正确返回 Stream<PolyIntEdge>
        .filter(edge -> layering[edge.getSource()] == layering[edge.getTarget()])
        .map(edge -> new IntPair(edge.getSource(), edge.getTarget()))
        .collect(Collectors.toCollection(ArrayList::new)));
}
```
