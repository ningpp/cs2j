# AGL Error Analysis

## Iteration 1 — EventArgs/EventHandler 找不到符号

- **Java 文件**: IViewer.java, IViewerObject.java
- **行号**: IViewer.java:33,34,46,47; IViewerObject.java:26,27,28,29
- **错误信息**: 找不到符号 - 类 EventArgs / 类 EventHandler
- **代码片段**:
  ```java
  public void addViewChangeEventListener(BiConsumer<Object, EventArgs> handler);
  public void addGraphChangedListener(EventHandler handler);
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\Drawing\LayoutEditing\IViewer.cs, IViewerObject.cs
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs
- **分析**: EventFieldTransformer 的 DetermineListenerSignature 方法在 fallback 路径中，当语义模型无法解析类型时：(1) 对于 EventHandler<T>，当 argTypeInfo.Type 为 null 时直接使用 argTypeSyntax.ToString() 而非 context.MapTypeFromSyntax()，导致 EventArgs 未被映射为 Object；(2) 对于非泛型 EventHandler，直接使用标识符文本 "EventHandler" 而未转换为 BiConsumer<Object, Object>。

✅ Fixed

## Iteration 3-5 — readonly struct 系列问题
- IsMutating 误判局部变量属性赋值为结构体变更
- readonly struct 构造函数链 this() 导致 final 字段双重赋值
- final 字段同时有初始化器和构造函数赋值
- 对象初始化器转换为 setter 调用但 readonly struct 无 setter
- ConstructorGenerator 生成 source=source 而非 this.source=source

✅ Fixed — BUILD SUCCESS

## Iteration 2 — 无法为 final 变量分配值
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/Curves/Parallelogram.java
- **行号**: 97-121, 191-197
- **错误信息**: 无法为 final 变量 corner/a/b/aRot/bRot/aPlusCorner/otherCorner/bPlusCorner 分配值
- **代码片段**:
  ```java
  private final Microsoft.Msagl.Core.Geometry.Point corner = new Microsoft.Msagl.Core.Geometry.Point();
  // ... in constructor:
  this.corner = corner.clone(); // ERROR: cannot assign to final variable
  this.aRot = new Point(-sideA.Y, sideA.X);
  aRot = aRot.normalize(); // ERROR: second assignment to final
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\Curves\Parallelogram.cs
- **根因分类**: Transformer (ReadOnlyStructMaker)
- **涉及组件**: src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs (AllFieldsOnlyAssignedInCtor)
- **分析**: ReadOnlyStructMaker 的 AllFieldsOnlyAssignedInCtor 仅检查字段是否在构造函数外被赋值，但未检查字段在构造函数内是否被多次赋值。Parallelogram 的构造函数中 aRot/bRot/abRot/baRot 均被赋值两次（如 aRot = new Point(...); aRot = aRot.Normalize();），标记为 readonly struct 后 Java 生成 final 字段，导致 Java 编译器报错。

✅ Fixed

## Iteration 7 — cannot find symbol (Point type missing)
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/ApproximateComparer.java
- **行号**: 77
- **错误信息**: 找不到符号 (cannot find symbol: class Point)
- **代码片段**:
  ```java
  public static boolean close(Point pointA, Point pointB, double tolerance) {
      return (pointA - pointB).getLength() <= tolerance;
  }
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\Point.cs
- **根因分类**: Transformer (ReadOnlyStructMaker)
- **涉及组件**: src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs (ApplyPublicFieldToProperty)
- **分析**: ApplyPublicFieldToProperty 将 public 字段转为 private 字段时，调用 .WithLeadingTrivia(Space) 和 .WithTrailingTrivia(Space) 剥离了所有 trivia（包括 #if/#else/#endif 预处理指令）。Point.cs 中 X/Y 字段位于 #if SHARPKIT/#else/#endif 块内，转换后 #endif 悬空，导致 Roslyn 解析失败，Point 类型未被转换输出。

✅ Fixed


## Iteration 8 — private access (X/Y in Point)
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/ApproximateComparer.java
- **行号**: 212
- **错误信息**: X 在 Microsoft.Msagl.Core.Geometry.Point 中是 private 访问控制
- **代码片段**:
  ```java
  // point.X accessed directly but X is private
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\ApproximateComparer.cs
- **根因分类**: Transformer (ReadOnlyStructMaker)
- **涉及组件**: src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs
- **分析**: L4 转换将 Point 的公共字段转为 private + getX()/getY() 方法，AssignmentRewriter 只重写了写入访问（obj.X = v → obj = obj.WithX(v)），未重写读取访问（obj.X → obj.getX()），导致其他文件中仍然直接访问 private 字段。

## Iteration 9 — cannot find symbol (setWeight)
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/BorderInfo.java
- **行号**: 88
- **错误信息**: 找不到符号 (cannot find symbol: method setWeight(double))
- **代码片段**:
  ```java
  this.setWeight(weight);
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\OverlapRemoval\BorderInfo.cs
- **根因分类**: Transformer (property assignment)
- **涉及组件**: C# → Java converter property assignment handling
- **分析**: ReadOnlyStructMaker L5 移除了 Weight 属性的 setter（使 struct readonly），但 C# 源码中仍有 this.Weight = weight 赋值。C# → Java 转换器将属性赋值转为 setWeight() 调用，但 Java 类中只有 getWeight() 和 withWeight()，无 setWeight()。

## Iteration 10 — incompatible types (Variable 无法转换为 double)
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/ProjectionSolver/Constraint.java
- **行号**: 202, 204
- **错误信息**: 不兼容的类型: Microsoft.Msagl.Core.ProjectionSolver.Variable无法转换为double (`Double.compare(this.getLeft(), other.getLeft())` 中 getLeft() 返回 Variable)
- **代码片段**:
  ```java
  public int compareTo(Constraint other) {
      ValidateArg.isNotNull(other, "other");
      int cmp = Double.compare(this.getLeft(), other.getLeft());   // ERROR: getLeft() is Variable
      if (0 == cmp) {
      cmp = Double.compare(this.getRight(), other.getRight());     // ERROR: getRight() is Variable
      }
      if (0 == cmp) {
      cmp = Double.compare(this.getGap(), other.getGap());         // OK: getGap() is double
      }
      return cmp;
  }
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\ProjectionSolver\Constraint.cs (CompareTo, line 169-182)
- **根因分类**: Transformer (InvocationExpressionTransformer primitive-detection fallback)
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs — `TryDetectPrimitiveByFieldDeclaration` (line 7345-7368)
- **分析**: C# 源码 `this.Left.CompareTo(other.Left)` 中 `Left` 字段类型为 `Variable`（非原始类型）。转换器的 `.CompareTo` 处理有多级 fallback 把原始类型接收者转为静态包装器调用（如 `Double.compare`）。当语义模型无法解析接收者类型时，最后的语法 fallback `TryDetectPrimitiveByFieldDeclaration` 遍历**整个编译的所有语法树**，仅按字段名 "Left" 查找原始类型字段。它在 `RectangularClusterBoundary.cs:131 public double Left;` 命中，错误地认定 `Constraint.Left` 是 double，从而生成 `Double.compare(this.getLeft(), other.getLeft())`。该 fallback 未将搜索范围限定在当前调用所在类型内，导致跨类型误判。
- **修复**: 在 InvocationExpressionTransformer 的原始类型检测 fallback 处，给语法 fallback `TryDetectPrimitiveByFieldDeclaration` 增加门控 `receiverSymbol == null`：仅当语义模型完全无法解析接收者类型时才执行按字段名的全局查找。若语义模型已将接收者解析为具体的非原始类型，则跳过该 fallback，直接走正常 `.compareTo()` 实例调用路径。Gap（double）仍由语义模型路径正确处理为 `Double.compare`，不受影响。

✅ Fixed — Constraint.java:202/204 错误消失（mvn 重新编译后首个错误变为 RTree.java:114 count private）。
