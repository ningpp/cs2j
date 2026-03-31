# 错误11: 数组与 Iterable 互转失败 (Array-Iterable Conversion)

## 受影响文件
**数组无法转为 Iterable:**
- `ProcrustesCircleConstraint.java:[278,16]` — `Node[]` → `Iterable<Node>`
- `InitialLayoutByCluster.java:[121,21]` — `Cluster[]` → `Iterable<Cluster>`

**Iterable 无法转为数组:**
- `IncrementalDragger.java:[102,28/69]` — `Iterable<Node>` → `Node[]`

**for-each 不适用于 Stream:**
- `SplineRouter.java:[357,186]` — `Stream` 不是数组或 Iterable

**Stream 不能赋给 Iterable:**
- `EdgeConstraintGenerator.java:[82,77]`
- `LgNodeCollection.java:[67,100]`
- `SplineRouter.java:[212,76]`
- `PathMerger.java:[134,127]`

## 错误信息
```
不兼容的类型: Node[]无法转换为Iterable<Node>
不兼容的类型: Iterable<Node>无法转换为Node[]
不兼容的类型: Stream<T>无法转换为Iterable<T>
for-each 不适用于表达式类型 — 要求: 数组或Iterable, 找到: Stream
```

## 原始 C# 代码
```csharp
// C# 中数组实现了 IEnumerable<T>，LINQ 链返回 IEnumerable<T>
// ProcrustesCircleConstraint.cs
public IEnumerable<Node> Nodes {
    get { return V; }  // V 是 Node[]，可以直接返回 IEnumerable<Node>
}

// IncrementalDragger.cs
Node[] nodes = graph.Nodes.ToArray();  // Iterable → Array 需要 ToArray()

// EdgeConstraintGenerator.cs
foreach (var edge in graph.Edges.Where(e => ...)) { ... }
// Where() 返回 IEnumerable<Edge>，可以用 foreach
```

## 生成的 Java 代码
```java
// ProcrustesCircleConstraint.java — 数组不能直接作为 Iterable
public Iterable<Node> getNodes() {
    return V;  // ← 错误: Node[] 不是 Iterable<Node>
}

// IncrementalDragger.java — Iterable 不能直接赋给数组
Node[] nodes = graph.getNodes();  // ← 错误: 返回 Iterable<Node>，不是 Node[]

// EdgeConstraintGenerator.java — Stream 不能用于 for-each
for (Edge edge : graph.getEdges().stream().filter(e -> ...)) { }
// ← 错误: Stream 不是 Iterable
```

## 错误原因分析

### 根本原因：C# 数组/IEnumerable/LINQ 的隐式兼容在 Java 中不存在

1. **C# 数组实现 IEnumerable<T>**，Java 数组不实现 `Iterable<T>`
2. **C# LINQ 返回 IEnumerable<T>** 可以直接 foreach，**Java Stream 不是 Iterable**
3. **C# `ToArray()` 从 IEnumerable 创建数组**，Java 没有直接等价

### 转换器缺陷
转换器在以下场景缺少适配：

| 场景 | C# | Java 正确写法 |
|------|-----|------------|
| 数组→Iterable | 隐式 | `Arrays.asList(arr)` |
| Iterable→数组 | `.ToArray()` | `StreamSupport.stream(...).toArray(T[]::new)` |
| Stream→for-each | LINQ返回IEnumerable | `.collect(toList())` 后再遍历 |
| Stream→Iterable | 隐式 | `.collect(toList())` |

### 正确的 Java 代码
```java
// 数组 → Iterable
public Iterable<Node> getNodes() {
    return Arrays.asList(V);
}

// Iterable → 数组
Node[] nodes = StreamSupport.stream(graph.getNodes().spliterator(), false)
    .toArray(Node[]::new);

// Stream → for-each
for (Edge edge : graph.getEdges().stream()
        .filter(e -> ...)
        .collect(Collectors.toList())) { }
```
