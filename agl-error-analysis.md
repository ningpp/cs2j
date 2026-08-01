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
