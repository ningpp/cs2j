# 错误24: 其他类型转换与符号解析问题 (Miscellaneous Type Conversion Issues)

本文档汇总了各种零散的类型不兼容、符号找不到等问题。每个问题出现频率较低（1-3 处），但根本原因各不相同。

---

## 24a: `int[]` 包装为 `Iterable<Integer>` 失败

### 受影响文件
- `VerticalConstraintsForSugiyama.java:[357,29]`

### C# 代码
```csharp
Iterable<int> UnglueNode(int node) {
    return new int[] { node };  // int[] 隐式转为 IEnumerable<int>
}
```

### Java 代码
```java
Iterable<Integer> unglueNode(int node) {
    return Arrays.asList(new int[] { node });  // ← 返回 List<int[]> 而非 List<Integer>
}
```

### 原因分析
`Arrays.asList(new int[] { node })` 不会自动装箱。`int[]` 作为单个对象被包装，返回 `List<int[]>` 而非 `List<Integer>`。应使用 `List.of(node)` 或 `Arrays.asList(Integer.valueOf(node))`。

---

## 24b: Holder 变量作用域错误（使用在声明之前）

### 受影响文件
- `GeometryGraphWriter.java:[713,49]` — `_previousInstructionRef` 未找到
- `GeometryGraphWriter.java:[721,49]` — `_previousInstructionRef2` 未找到
- `RouteSimplifier.java:[133,80]` — `_veHolder1` 未找到
- `SteinerCdt.java:[284,13]` — holder 变量未声明
- `PathFixer.java:[283,243]` — holder 变量未声明

### C# 代码
```csharp
// GeometryGraphWriter.cs
var previousInstruction = 'w';
for (int i = 0; i < curve.Segments.Count; i++) {
    var segment = curve.Segments[i];
    if (i != curve.Segments.Count - 1)
        yield return SegmentString(segment, ref previousInstruction);
    else { ... }
}
```

### Java 代码
```java
var previousInstruction = 'w';
for (int i = 0; i < curve.getSegments().size(); i++) {
    var segment = curve.getSegments().get(i);
    if (i != curve.getSegments().size() - 1) {
        _yieldResult.add(segmentString(segment, _previousInstructionRef));  // ← 此处引用
    } else {
        CharHolder _previousInstructionRef = new CharHolder(previousInstruction);  // ← 此处才声明
        ...
    }
}
```

### 原因分析
转换器将 `ref` 参数转为 Holder 包装器时，将 Holder 的**声明**放在了 `else` 分支内部，但在 `if` 分支中就已经使用了对它的引用。Holder 变量的声明应该提升到整个作用域的最前面。

---

## 24c: 向下转型缺失（VisibilityVertex → VisibilityVertexRectilinear）

### 受影响文件
- `MsmtRectilinearPath.java:[119,51]`
- `MsmtRectilinearPath.java:[120,51]`

### C# 代码
```csharp
// sources 和 targets 声明为 VisibilityVertexRectilinear
foreach (var source in sources) {
    foreach (var target in targets) { ... }
}
```

### Java 代码
```java
// 循环变量类型为 VisibilityVertex（父类型），但方法签名期望 VisibilityVertexRectilinear
for (VisibilityVertexRectilinear source : sources) {
    for (VisibilityVertexRectilinear target : targets) { ... }
}
```

### 原因分析
集合的元素类型被解析为父类 `VisibilityVertex`，但循环中使用的方法需要子类 `VisibilityVertexRectilinear`。转换器缺少显式向下转型 `(VisibilityVertexRectilinear)` 或集合的泛型类型参数不正确。

---

## 24d: `List<T>` 转 `ArrayList[]` 数组失败

### 受影响文件
- `PhyloTreeLayoutCalclulation.java:[451,52]`

### C# 代码
```csharp
List<int>[] layers = new List<int>[numberOfLayers];
for (int i = 0; i < numberOfLayers; i++)
    layers[i] = new List<int>();
```

### Java 代码
```java
ArrayList<Integer>[] layers = Arrays.asList(new ArrayList[numberOfLayers]);
// ← Arrays.asList 返回 List<ArrayList>，不是 ArrayList[]
```

### 原因分析
C# 数组 `new List<int>[n]` 被错误翻译为 `Arrays.asList(new ArrayList[n])`。应使用 `new ArrayList[numberOfLayers]` 直接创建数组（虽然有 unchecked 警告），或使用 `List<List<Integer>>`。

---

## 24e: 数组上调用 `.spliterator()` 实例方法

### 受影响文件
- `MdsGraphLayout.java:[189,36]`

### C# 代码
```csharp
Parallel.ForEach(graphs, this.LayoutConnectedGraphWithMds);
```

### Java 代码
```java
StreamSupport.stream(graphs.spliterator(), true)
    .forEach(this::layoutConnectedGraphWithMds);
// ← graphs 是 GeometryGraph[]，数组没有 .spliterator() 实例方法
```

### 原因分析
Java 数组没有 `spliterator()` 实例方法。应使用 `Arrays.spliterator(graphs)` 或 `Arrays.stream(graphs).parallel().forEach(...)`。

---

## 24f: 抽象方法签名不匹配

### 受影响文件
- `VisibilityGraphGenerator.java:[71,8]`

### 原因分析
父类或接口声明的抽象方法签名与转换后的方法签名不匹配。通常由于参数类型映射不一致（如 C# 中使用 `Direction` 枚举而 Java 转为 `int`），导致子类没有正确覆盖抽象方法。

---

## 24g: Object 类型上调用特定方法

### 受影响文件
- `GeometryGraphReader.java:[1378,44]` — 将 `Object` 赋值给 `double`
- `MultiScaleLayout.java:[188,44]` — 在 `Object` 上调用 `getLength()`
- `GeometryGraphReader.java:[1448,22]` — 在静态上下文调用非静态 `close()`

### 原因分析
这些都与对象初始化器类型丢失（错误08）相关。当类型被错误推断为 `Object` 时，所有属性访问和方法调用都失败。`close()` 错误则是因为 XmlReader 被映射为 Object 后，静态/实例方法上下文混乱。

---

## 24h: 集合方法名称映射错误

### 受影响文件
- `NodePositionsAdjuster.java:[554,33]` — `exists()` 方法不存在
- `NodePositionsAdjuster.java:[558,30]` — `exists()` 方法不存在
- `SplineRouter.java:[211,60]` — `Select()` 方法不存在
- `SingleSourceDistances.java:[132,15]` — `Map.get()` 参数错误

### C# 代码
```csharp
// C# LINQ
list.Exists(x => condition)    // List<T>.Exists 方法
array.Select(x => transform)   // LINQ Select
dict[key]                       // Dictionary 索引器
```

### Java 代码
```java
list.exists(x -> condition);    // ← ArrayList 没有 exists() 方法
array.Select(x -> transform);   // ← Select 未转为 stream().map()
map.get(key1, key2);            // ← Map.get 只接受单参数
```

### 原因分析
- `Exists()` 应映射为 `stream().anyMatch()`
- `Select()` 应映射为 `Arrays.stream().map()`
- 多参数索引器被错误地直接翻译为 `get()` 调用

---

## 24i: `int` 到 `Double`（装箱类型）赋值

### 受影响文件
- `Centrality.java:[78,20]`

### C# 代码
```csharp
Dictionary<Node, double> result = new Dictionary<Node, double>();
result[node] = 0;  // int 隐式转 double
```

### Java 代码
```java
LinkedHashMap<Node, Double> result = new LinkedHashMap<>();
result.put(node, 0);  // ← int 0 不能自动装箱为 Double
```

### 原因分析
C# 中 `int` 可以隐式转换为 `double`，进而装箱为 `Double`。Java 中 `int 0` 不能直接装箱为 `Double`（只能装箱为 `Integer`）。应使用 `0.0` 或 `(double) 0`。

---

## 24j: `Polyline` 不兼容 `Collection<Point>`

### 受影响文件
- `ClusterConvexHull.java:[119,72]`

### 原因分析
C# 中 `Polyline` 实现 `IEnumerable<Point>`（可枚举其控制点）。Java 中 `Polyline` 可能没有实现 `Collection<Point>` 接口。需要显式调用 `.getPoints()` 或类似方法获取点集合。
