# AGL MSAGL Error Analysis

## Iteration 1 — Non-generic ICollection → CSharpCollection incompatible (3 errors)

- **Java 文件**: EdgeLabelPlacement.java (lines 190, 223, 388)
- **错误信息**: CSharpList<CSharpKeyValuePair<Double,Point>> cannot be converted to CSharpCollection
- **代码片段**:
  ```java
  CSharpList<CSharpKeyValuePair<Double, Point>> points = edgePoints.get(edge);
  int index = startIndex(label, points); // startIndex takes CSharpCollection
  ```
- **对应 C# 文件**: Core/Layout/EdgeLabelPlacement.cs
- **根因分类**: 类型映射缺失
- **涉及组件**: config/TypeMappings.json, java/csharptojava-compat/.../CSharpICollection.java
- **分析**: C# non-generic ICollection mapped to raw CSharpCollection, but CSharpList<T> doesn't implement CSharpCollection. Changed mapping to CSharpICollection<?> and added CSharpCollection-compatible methods (copyTo, getIsSynchronized, getSyncRoot) to CSharpICollection<T>.
- ✅ Fixed

## Remaining errors (11 unique)

### Category B: CSharpGenericIterable.from() covariance issue (2 errors)
- Cluster.java:143 — Node vs Cluster equality constraint
- MstOnDelaunayTriangulation.java:87 — IEdge vs IntPair equality constraint
- Root cause: C# IEnumerable<T> is covariant, Java CSharpGenericIterable<T> is invariant

### Category C: CSharpEnumerator → CSharpGenericEnumerator<?> incompatibility (1 error)
- Pred.java:70 — CSharpEnumerator cannot convert to CSharpGenericEnumerator<?>

### Category D: GroupBy return type mismatch — Set<Entry> vs CSharpGenericIterable<Entry> (2+2 errors)
- RecoveryLayeredLayoutEngine.java:1191, 1267 — Set<Entry<Double,CSharpGenericIList<Integer>>> vs CSharpGenericIterable<Entry<Double,List<Integer>>>
- PathRefiner.java:222 — Similar pattern

### Category E: Iterable/List → CSharpGenericIterable incompatibility (3 errors)
- GraphConnectedComponents.java:114 — get(int) not found on CSharpGenericIterable
- PathMerger.java:123 — Iterable<Point> vs CSharpGenericIterable<Point>
- PathRefiner.java:130 — List<LinkedPoint> vs CSharpGenericIterable<LinkedPoint>
- SplineRouter.java:170, 325 — List vs CSharpGenericIterable

## Iteration 2 — CSharpGenericIterable.from / CSharpGenericEnumerator.from covariance (Category B, 2 errors)

- **Java 文件**: Cluster.java:143 (and MstOnDelaunayTriangulation.java:87)
- **行号**: 143 (and 87)
- **错误信息**: 推论变量T具有不兼容的等式约束条件 Node, Cluster (incompatible equality constraints on inference variable T)
- **代码片段**:
  ```java
  // Cluster.java:143
  return Microsoft.Msagl.Core.Geometry.Rectangle.createFrom_CSharpGenericIterable_Rectangle(
      boundingBox_ProceduralLinq1(nodes, CSharpGenericIterable.from(clusters)));
  // helper signature:
  CSharpGenericIterable<Rectangle> boundingBox_ProceduralLinq1(CSharpList<Node> _linqitems, CSharpGenericIterable<Node> _concatSecond_1)
  ```
- **对应 C# 文件**: Core/Layout/Cluster.cs (`nodes.Concat(clusters).Select(n => n.BoundingBox)`)
- **根因分类**: 转换器运行时依赖 (compat 库) — 类型协变缺失
- **涉及组件**: java/csharptojava-compat/.../CSharpGenericIterable.java (已含 `from(Iterable<? extends T>)` 但无法编译), java/csharptojava-compat/.../CSharpGenericEnumerator.java (`from(Iterator<T>)` 不协变)
- **分析**: C# 的 `IEnumerable<T>` 是协变的（`out T`），`nodes: List<Node>` 与 `clusters: List<Cluster>`（Cluster : Node）的 `Concat` 推断为 `IEnumerable<Node>`。转换器为第二个序列参数生成 `CSharpGenericIterable<Node>`，并用 `CSharpGenericIterable.from(clusters)` 桥接，期望 `CSharpGenericIterable<Cluster>` 可赋值给 `CSharpGenericIterable<Node>`。但运行时 compat 库中 `from` 实际编译为 `from(Iterable<T>)`（不协变），导致推导变量 T 同时被约束为 Node 与 Cluster 而失败。源码中的 `? extends T` 版本本应修复协变，却因内部调用 `CSharpGenericEnumerator.from(iterable.iterator())`（`iterator()` 为 `Iterator<? extends T>`）与 `CSharpGenericEnumerator.from(Iterator<T>)` 冲突而无法编译，导致本地仓库中的 jar 仍是旧的不协变版本。
- **修复**: 将 `CSharpGenericEnumerator.from` 改为 `from(Iterator<? extends T>)`（带 unchecked 转换），使 `CSharpGenericIterable.from(Iterable<? extends T>)` 可正常编译并真正实现协变。重建并安装 compat jar 到 D:\mvnrepo 后，Java 的目标类型推导 T=Node 成立，`clusters` 作为 `Iterable<? extends Node>` 通过，两个 Category B 错误同时消除。
- **验证**: 新增 `CSharpGenericIterableCovarianceTest`（`from`/`enumeratorFrom` 接受子类型集合/迭代器），`mvn test` 通过；agl 项目 `mvn clean package` 中 Cluster.java:143 与 MstOnDelaunayTriangulation.java:87 错误消失。
- ✅ Fixed

## Iteration 3 — CSharpEnumerator 不可赋值给 CSharpGenericEnumerator<?> (Category C, 1 error)

- **Java 文件**: Pred.java:70
- **行号**: 70
- **错误信息**: io.github.ningpp.compat.CSharpEnumerator 无法转换为 io.github.ningpp.compat.CSharpGenericEnumerator<?>
- **代码片段**:
  ```java
  // Pred.java:70  (iterator() 方法)
  return new PredEnumerator(CSharpEnumerator.from(e.iterator()));
  // PredEnumerator.java:56 构造函数
  public PredEnumerator(CSharpGenericEnumerator<?> edges) { this.edges = edges; }
  ```
- **对应 C# 文件**: Pred.cs (`new PredEnumerator(e)`，e 为 IEnumerator<Edge>)
- **根因分类**: 转换器运行时依赖 (compat 库) — 类型层级不一致
- **涉及组件**: java/csharptojava-compat/.../CSharpEnumerator.java（仅 extends Iterator<Object>，未实现 CSharpGenericEnumerator）
- **分析**: 转换器对 `PredEnumerator` 的构造函数参数（C# IEnumerator<Edge>）映射为 `CSharpGenericEnumerator<?>`，但对传入的枚举器值（C# 同一 IEnumerator<Edge>）却映射为非泛型 `CSharpEnumerator` 并包裹 `CSharpEnumerator.from(...)`。由于 `CSharpEnumerator` 与 `CSharpGenericEnumerator` 是并列接口，前者不能赋值给后者。对应 C# 中 `IEnumerator<T> : IEnumerator` 的继承关系，应让 `CSharpEnumerator` 成为 `CSharpGenericEnumerator<Object>` 的子类型。
- **修复**: 将 `CSharpEnumerator` 的父接口由 `Iterator<Object>` 改为 `CSharpGenericEnumerator<Object>`，使非泛型 `IEnumerator` 值可赋值给 `CSharpGenericEnumerator<?>` 参数（同时保留 moveNext/getCurrent/reset 等方法）。重建并安装 compat jar 后 Pred.java:70 错误消失。
- ✅ Fixed
