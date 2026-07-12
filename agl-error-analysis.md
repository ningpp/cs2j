# AGL Error Analysis

## Iteration 1 — IncompatibleTypes: CSharpGenericEnumerator vs CSharpEnumerator
- **Java 文件**: D:/agl202607/automaticgraphlayout/src/main/java/Microsoft/Msagl/Layout/Layered/NetworkSimplex.java (lines 216, 217, 240, 241, 252, 253, 609) and Succ.java (line 64)
- **行号**: 216, 217, 240, 241, 252, 253, 609 (NetworkSimplex), 64 (Succ)
- **错误信息**: 不兼容的类型: 不存在类型变量T的实例, 以使io.github.ningpp.compat.CSharpGenericEnumerator<T>与io.github.ningpp.compat.CSharpEnumerator一致
- **代码片段**:
  ```java
  CSharpEnumerator outEnum = CSharpGenericEnumerator.from(this.graph.outEdges(v).iterator());
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Layout\Layered\Layering\NetworkSimplex.cs
- **根因分类**: Transformer
- **涉及组件**: CSharpEnumerator.java, CSharpGenericEnumerator.java (compat library), InvocationExpressionTransformer.cs (GetEnumerator logic)
- **分析**: C# 中 IEnumerator<T> 继承 IEnumerator，所以 IEnumerator<T>.GetEnumerator() 返回值可赋给 IEnumerator 变量。但 Java 中 CSharpEnumerator extends CSharpGenericEnumerator<Object>，导致 CSharpGenericEnumerator<T> 不是 CSharpEnumerator 的子类型，无法赋值。转换器在 GetEnumerator() 调用时生成 CSharpGenericEnumerator.from()，但变量声明为 CSharpEnumerator，类型不兼容。

## Iteration 2 — ArrayIndexOutOfBoundsException: CSharpList.removeRange semantic mismatch
- **Java 文件**: d:/agl202607/automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/ProjectionSolver/Block.java (line 561)
- **行号**: 561
- **错误信息**: java.lang.ArrayIndexOutOfBoundsException: arraycopy: length -130 is negative
- **代码片段**:
  ```java
  this.getVariables().subList(lastKeepIndex + 1, lastKeepIndex + 1 + (this.getVariables().size() - lastKeepIndex - 1)).clear();
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\ProjectionSolver\Block.cs (line 720)
- **根因分类**: 兼容库缺陷
- **涉及组件**: CSharpList.java (removeRange override), InvocationExpressionTransformer.cs (RemoveRange→subList.clear)
- **分析**: CSharpList.removeRange(int index, int count) 覆写了 ArrayList.removeRange(int fromIndex, int toIndex)，但将参数解释为 C# 语义(index, count)而非 Java 语义(fromIndex, toIndex)。当 subList().clear() 内部调用 removeRange(from, to) 时，CSharpList 的覆写错误地将 toIndex 当作 count，执行 super.removeRange(from, from+to)，导致数组越界。
- **状态**: ✅ Fixed (commit 962f9eb0)
