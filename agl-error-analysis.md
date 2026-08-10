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

## Iteration 11 — count 在 RectangleNode 中是 private 访问控制
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/RTree.java
- **行号**: 114, 259
- **错误信息**: count 在 Microsoft.Msagl.Core.Geometry.RectangleNode 中是 private 访问控制
- **代码片段**:
  ```java
  static <T, P> void addNodeToTreeRecursive(RectangleNode<T, P> newNode, RectangleNode<T, P> existingNode) {
      if (existingNode.getIsLeaf()) {
          existingNode.setLeft(new RectangleNode<T, P>(existingNode.getUserData(), existingNode.getRectangle()));
          existingNode.setRight(newNode);
          existingNode.count = 2;          // ERROR: count is private
          existingNode.setUserData(null);
      } else {
          existingNode.setCount(existingNode.getCount() + 1);   // OK: compound assignment uses setter
          ...
      }
  }
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\RTree\RTree.cs (line 88: `existingNode.Count = 2;`, line 270: `nodeForRebuild.Count = newNode.Count;`)
- **根因分类**: Transformer (AssignmentTransformer PropertyHasSetterInSyntax enclosing-type confusion)
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs — `PropertyHasSetterInSyntax` (line 1559-1600)
- **分析**: `RTree` 类自身有一个 get-only `Count` 属性（`public int Count { get { return _rootNode == null ? 0 : _rootNode.Count; } }`）。当转换器处理 `existingNode.Count = 2`（其中 `existingNode` 是 `RectangleNode<T,P>` 类型）时，`PropertyHasSetterInSyntax` 方法的第一个循环在**当前正在转换的 enclosing type**（即 `RTree`）的语法树中查找名为 `Count` 的属性。它找到了 `RTree.Count`（无 setter），错误地返回 `false`，导致转换器生成直接 backing-field 写入 `existingNode.count = 2` 而非 setter 调用 `existingNode.setCount(2)`。该方法未检查属性的实际声明类型（`prop.ContainingType`）是否与 enclosing type 一致，导致跨类型同名属性混淆。注意：复合赋值 `Count++` 走不同代码路径（不检查 `PropertyHasSetterInSyntax`），所以 `setCount(getCount() + 1)` 正确。
- **修复**: 在 `PropertyHasSetterInSyntax` 的第一个循环增加条件 `SymbolEqualityComparer.Default.Equals(enclosingType, prop.ContainingType)`：仅当属性声明在当前 enclosing type 中时，才搜索 enclosing type 的语法树。否则直接走 `prop.DeclaringSyntaxReferences` fallback，该 fallback 会正确找到 `RectangleNode.Count` 的 `{ get; set; }` 声明并返回 `true`。

✅ Fixed — RTree.java:114/259 错误消失（mvn 重新编译后首个错误变为 GraphForCycleRemoval.java:135 getCurrent$Class() 找不到符号）。

## Iteration 12 — getCurrent$Class() 找不到符号
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/GraphAlgorithms/GraphForCycleRemoval.java
- **行号**: 135
- **错误信息**: 找不到符号 — 方法 getCurrent$Class()，位置: CSharpGenericEnumerator<CSharpKeyValuePair<Integer, Set<Integer>>> 的变量 enumerator
- **代码片段**:
  ```java
  var enumerator = this.deltaDegreeBucketsForSourcesInConstrainedSubgraph.iterator();
  if (enumerator.moveNext()) {
      var bucketSet = enumerator.getCurrent$Class().getValue();  // ERROR: getCurrent$Class() not found
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\GraphAlgorithms\CycleRemoval\GraphForCycleRemoval.cs (line 96-98: `enumerator.Current.Value`)
- **根因分类**: Transformer (IdentifierExpressionTransformer GetPropertyGetterName $Class suffix)
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs — `GetPropertyGetterName` (line 70-104)
- **分析**: `SortedDictionary<K,V>.Enumerator` 是一个 struct，同时实现 `IEnumerator<KeyValuePair<K,V>>` 和 `IEnumerator`。它的 `Current` 属性返回 `KeyValuePair<K,V>`，而 `IEnumerator.Current`（显式接口实现）返回 `object`。`GetPropertyGetterName` 方法中，冲突检测循环先于 `IsEnumeratorLikeType` 特殊判断执行，检测到 `IEnumerator.Current`（返回类型 `object` 与 `KeyValuePair<K,V>` 不同）后立即返回 `getCurrent$Class`。但在 Java 中，`SortedDictionary` 的枚举器被映射为 `CSharpGenericEnumerator<T>`，该类只有 `getCurrent()` 方法，没有 `getCurrent$Class()`。
- **修复**: 在 `GetPropertyGetterName` 的冲突检测循环之前增加 `ImplementsGenericIEnumerator(containingType)` 检查：当属性名为 `Current` 且包含类型实现了 `IEnumerator<T>`（泛型）时，直接返回 `getCurrent()`（不加 `$Class` 后缀），因为 `CSharpGenericEnumerator<T>` 只有 `getCurrent()`。非泛型 `IEnumerator` 的 `$Class` 逻辑不受影响（仍走冲突检测循环）。

✅ Fixed — GraphForCycleRemoval.java:135 和 Ordering.java:650 的 getCurrent$Class() 错误消失（mvn 重新编译后首个错误变为 GeometryGraphReader.java:134 ignoreComments 找不到符号）。

## Iteration 13 — ignoreComments 找不到符号
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/DebugHelpers/Persistence/GeometryGraphReader.java
- **行号**: 134, 135
- **错误信息**: 找不到符号 — 变量 ignoreComments / ignoreWhitespace，位置: dotnet.xml.XmlReaderSettings 的变量 _obj145
- **代码片段**:
  ```java
  var _obj145 = new XmlReaderSettings();
  _obj145.ignoreComments = false;  // ERROR: ignoreComments 找不到
  _obj145.ignoreWhitespace = true; // ERROR: ignoreWhitespace 找不到
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\DebugHelpers\Persistence\GeometryGraphReader.cs (line 65: `new XmlReaderSettings { IgnoreComments = false, IgnoreWhitespace = true }`)
- **根因分类**: Transformer (ObjectCreationTransformer 未解析属性处理)
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs (line 1257-1278)
- **分析**: `XmlReaderSettings` 属于未引用的 `System.Xml.ReaderWriter.dll`，语义模型无法解析 `IgnoreComments` 属性，导致 `memberSymbol` 为 null。转换器默认使用直接字段写入（`_obj145.ignoreComments = false`），但 Java 兼容类 `XmlReaderSettings` 的字段为私有，仅提供 `setIgnoreComments()` 方法。
- **修复**: 在 ObjectCreationTransformer 的标识符初始化器处理中，当 `memberSymbol` 为 null（未解析）时，默认生成 setter 调用（`setIgnoreComments(false)`）而非直接字段写入。

✅ Fixed — GeometryGraphReader.java:134/135 错误消失（mvn 重新编译后首个错误变为 GeometryGraphReader.java:734 setInnerMargin 找不到符号）。

## Iteration 14 — setInnerMargin/setFixedPosition/setWeight 找不到符号
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/DebugHelpers/Persistence/GeometryGraphReader.java
- **行号**: 734, 735, 736
- **错误信息**: 找不到符号 — 方法 setInnerMargin(double) / setFixedPosition(double) / setWeight(double)，位置: Microsoft.Msagl.Core.Geometry.BorderInfo 的变量 _obj152
- **代码片段**:
  ```java
  var _obj152 = new BorderInfo();
  _obj152.setInnerMargin(getDoubleAttribute(GeometryToken.InnerMargin));     // ERROR
  _obj152.setFixedPosition(getDoubleAttribute(GeometryToken.FixedPosition)); // ERROR
  _obj152.setWeight(getDoubleAttribute(GeometryToken.Weight));               // ERROR
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\DebugHelpers\Persistence\GeometryGraphReader.cs (line 814-825: `new BorderInfo { InnerMargin = ..., FixedPosition = ..., Weight = ... }`)
- **根因分类**: Lowering (ReadOnlyStructMaker L5 migration 未更新 object initializer 调用点)
- **涉及组件**:
  - src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs — `ApplyMethodMigrate` (line 265-365): converts property setters to `WithXxx()` methods and removes setters
  - src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs — `MakeReadOnly`: only runs `AssignmentRewriter` for L4 structs, not L5
  - src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs — `UpdateCrossFileCallSites`: only handles void→struct method migrations, not property setter→WithXxx conversions
- **分析**: `BorderInfo` 是一个 C# struct，包含 mutating methods（`SetFixed`, `SetUnfixed`, `EnsureWeight`）和公共属性 setter（`InnerMargin`, `FixedPosition`, `Weight`）。L5 migration 将属性 setter 转换为 `WithXxx()` 方法并移除了 setter，但未更新跨文件的对象初始化器调用点。`GeometryGraphReader.cs` 中的 `new BorderInfo { InnerMargin = ..., ... }` 未被重写为构造函数调用，导致 Java 转换器生成 `setInnerMargin()` 调用，但 `BorderInfo.java` 只有 `withInnerMargin()` 方法。
- **修复**: 在 `ReadOnlyStructMaker.MakeReadOnly` 和 `ProjectReadOnlyStructPreprocessor.UpdateCrossFileCallSites` 中，L5 migration 后检测带有 `WithXxx` 方法的 struct，并运行 `AssignmentRewriter` 将对象初始化器转换为构造函数调用。

✅ Fixed — GeometryGraphReader.java:734/735/736 错误消失（mvn 重新编译后首个错误变为 Rectangle.java:138 double 无法转换为 Size）。

## Iteration 15 — ModifierKeys 二元运算符 '&' 操作数类型错误
- **Java 文件**: automaticgraphlayout-drawing/src/main/java/Microsoft/Msagl/Drawing/LayoutEditor.java
- **行号**: 555, 850, 1205
- **错误信息**: 二元运算符 '&' 的操作数类型错误（第一个类型: Microsoft.Msagl.Drawing.ModifierKeys，第二个类型: int）；1205 还有 ModifierKeys无法转换为int
- **代码片段**:
  ```java
  boolean modifierKeyWasUsed = (viewer.getModifierKeys() & ModifierKeys.Control) == ModifierKeys.Control || ...
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\Drawing\LayoutEditing\LayoutEditor.cs (line 531: `viewer.ModifierKeys & ModifierKeys.Control`)；类型定义 E:\agl-master\GraphLayout\Drawing\ModifierKeys.cs ([Flags] enum)
- **根因分类**: Lowering (flags 注册时序)
- **涉及组件**: src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs（新增 PreRegisterFlagsEnums）
- **分析**: `[Flags]` 枚举被转换为含 int 常量的常量类，属性/成员类型应映射为其底层 int。项目级转换中 `FromPartialTypeGroup` 会把成员节点重新挂到合成的 syntax tree（FilePath 为空），导致 `GetTypeInfo` 全部落到语法回退路径 `MapTypeFromSyntaxString`，它依赖按名称注册的 flags 注册表。类型组转换顺序由 Roslyn 命名空间遍历决定（非文件顺序），IViewer（接口）在 ModifierKeys（枚举）之前转换，注册表为空 → 属性类型映射为类名 `ModifierKeys` 而非 `int`，位运算 `&` 失败。
- **修复**: 在 `ProjectTypeEmitPass.Execute` 的转换循环前增加 `PreRegisterFlagsEnums`，扫描整个 compilation 的源码枚举并预先注册 `[Flags]` 枚举（简单名 + 全限定名），使 flags 映射与类型组顺序无关。
- **测试**: tests/CSharpToJava.Tests/FlagsEnumInterfacePropertyTests.cs（单文件 + 多文件"枚举声明在接口之后"两个用例，先红后绿）

✅ Fixed — IViewer.java:48 变为 `public int getModifierKeys();`，LayoutEditor.java:555/850/1205 错误消失（mvn 重新编译后首个错误变为 DgmlParser.java:36 Iterable<XElement> 无法转换为 CSharpGenericIterable<XElement>）。

## Iteration 16 — DgmlParser XDocument/XName 兼容层 API 不匹配
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/DebugHelpers/Persistence/DgmlParser.java
- **行号**: 36, 47, 68, 80
- **错误信息**: Iterable<XElement> 无法转换为 CSharpGenericIterable<XElement>；找不到符号 — 方法 getLocalName()，位置: Microsoft.Msagl.Core.DataStructures.XName
- **代码片段**:
  `java
  Iterable<XElement> enumerable = doc.Descendants().Where(...);   // ERROR: doc.Descendants() 返回 Iterable<XElement>
  XName name = element.getName(); name.getLocalName();            // ERROR: XName 没有 getLocalName()
  `
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\DebugHelpers\Persistence\DgmlParser.cs (doc.Descendants().Where(...) / e.Name.LocalName)
- **根因分类**: 类型映射缺失 (compat LINQ-to-XML facade 与生成代码的 API 契约不匹配)
- **涉及组件**:
  - java/csharptojava-compat/src/main/java/io/github/ningpp/compat/XDocument.java — descendants() 返回类型应为 CSharpGenericIterable<XElement>（过程化 LINQ lowering 要求）
  - java/csharptojava-compat/src/main/java/io/github/ningpp/compat/XName.java — 缺少 getLocalName() getter
  - java/csharptojava-compat/pom.xml — 之前提交的 Java 27 + --enable-preview 目标导致 AGL 消费端（Java 25）报"类文件使用 Java SE 27 的预览功能"
- **分析**: DgmlParser 的 C# 代码 doc.Descendants().Where(...) 被 lowering 成 CSharpGenericIterable 链式调用，但 compat 层 XDocument.descendants() 返回的是裸 Iterable；XName 只有公共字段 LocalName，而生成代码调用 getName().getLocalName()。此外 pom 的 Java 27 目标使安装的 jar 无法被 Java 25 的 AGL 项目消费。
- **修复**: XDocument.descendants() 改为返回 CSharpGenericIterable.fromCollection(result)；XName 增加 public String getLocalName()（保留公共字段 LocalName）；compat pom 回退到 <java.version>25</java.version> 并移除 --enable-preview 编译参数；重新 mvn -DskipTests install 到 D:\mvnrepo。转换器侧 ProjectCompilationBuilder 增加 System.Xml.Linq.dll 元数据引用以便测试编译。
- **测试**: tests/CSharpToJava.Tests/XmlLinqCompatibilityTests.cs（2 个用例，先红后绿：XDocumentDescendants_ReturnsGenericIterable 与项目模式 Pin ProjectMode_XmlLinqNameLocalName_AccessesCompatXNameField）

✅ Fixed — 9 个模块全部编译通过，mvn 构建推进到 msagltests 测试阶段，仅剩 2 个运行时断言失败（见 Iteration 17）。

## Iteration 17 — BorderInfo(double) 构造器链 this(...) 参数丢失
- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/BorderInfo.java
- **行号**: 81-84（1 参构造器）
- **错误信息**: 运行时断言失败（非编译错误）— msagltests 的 OverlapRemovalTests.test_Cluster_Nest_Margins / test_Cluster_Sibling_Margins 报 "Expected Cluster positions failed"，集群尺寸比预期小恰好等于各边 BorderInfo margin 之和
- **代码片段**:
  `java
  public BorderInfo(double innerMargin) {
      this.innerMargin = 0.0d;      // BUG: 参数被丢弃
      this.fixedPosition = 0.0d;
      this.borderWeight = 0.0d;
  }
  `
  期望: 	his(innerMargin, getNoFixedPosition(), getDefaultFreeWeight()) 的效果
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\OverlapRemoval\BorderInfo.cs (public BorderInfo(double innerMargin) : this(innerMargin, NoFixedPosition, DefaultFreeWeight) { })
- **根因分类**: Transformer (StructTransformer 只读结构体构造器链内联)
- **涉及组件**: src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs — FixReadonlyStructConstructorChaining (line 1231+)
- **分析**: 只读结构体的 1 参构造器 : this(innerMargin, NoFixedPosition, DefaultFreeWeight) 需要内联到 3 参目标构造器；但目标构造器自身带有 : this()（无参链），被目标搜索条件 (c.Initializer == null || !c.Initializer.StartsWith("this(")) 排除，导致找不到目标 → 构造器体只剩"全部字段赋默认值"的兜底初始化，margin 参数被静默丢弃。文件式测试用 3 参 BorderInfo 构造器所以不受影响；只有这两个内联测试用 1 参 
ew BorderInfo(4.0)，集群 margin 全部丢失 → X/Y 集群尺寸偏小、X 节点解整体偏离。
- **修复**: FixReadonlyStructConstructorChaining 分两步：① 先清除所有无参 	his() 链（Java 字段天然默认初始化，无参链无副作用）；② 对带参 	his(...) 链做不动点迭代内联（目标要求 Initializer==null，支持 A→B→C 传递链），保留原有"未赋值 final 字段补默认值"兜底。修复后字段恢复 final（只读语义），1 参构造器正确内联 	his.innerMargin = innerMargin; this.fixedPosition = getNoFixedPosition(); this.borderWeight = getDefaultFreeWeight();。
- **测试**: tests/CSharpToJava.Tests/StructCtorChainingTests.cs（先红后绿：断言 1 参构造器块内含三个链式赋值）

✅ Fixed — 重新转换 + mvn clean package -e：BUILD SUCCESS（OverlapRemovalTests 30/30 通过，msagltests 609 测试 0 失败）。

## Iteration 1 — 转换管道验证失败（dotnet test Assertion Failed） — ✅ Fixed
- **阶段**: Step 1 转换命令（make-readonly 预处理 VerifyTests 阶段），尚未生成 Java 文件
- **错误信息**: `dotnet test failed with exit code 1 after readonly transformation.` 测试主机进程崩溃：`Process terminated. Assertion Failed` at `FlipSwitcher.PolylinesIntersect` (FlipSwitcher.cs:265 `Debug.Assert(left0 != 0 && left1 != 0)`)
- **复现命令**: `dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s E:\agl-master\GraphLayout\ -d d:\cs-agl-260806 --extra-deps io.github.ningpp:system-private-xml:0.0.1-SNAPSHOT`
- **代码片段**:
  ```csharp
  // MSAGL/Routing/Spline/Bundling/FlipSwitcher.cs:265 (原始源码与只读转换副本完全相同)
  Debug.Assert(left0 != 0 && left1 != 0);
  return left0 == left1 ? 1 : 2;
  ```
- **对应 C# 文件**: `MSAGL/Routing/Spline/Bundling/FlipSwitcher.cs:265`
- **根因分类**: Transformer（验证步骤逻辑缺陷）
- **涉及组件**: `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs`（`VerifyBuildAndTestsAsync` / `RunDotNetCommandAsync`）
- **分析**: 验证测试以 Debug 配置（默认）运行 `dotnet test`，而 C# 源码自身存在 flaky `Debug.Assert`（路由后台线程偶发触发，与只读转换无关——已用 git clone 原始源码全量跑测试复现同样崩溃；单测单独运行及 Release 配置全量运行均通过），导致测试主机崩溃、验证失败、整个转换中止。


> **状态**: ✅ Fixed (2026-08-06) — VerifyBuildAndTestsAsync 现在以 -c Release 运行验证构建/测试，Debug.Assert 被编译掉，flaky 断言不再阻断转换。验证: 全量单测 2421 通过；mvn clean package -e BUILD SUCCESS。
## Iteration N — base.ToString 生成非法 super 参数 + const double.PositiveInfinity 渲染为 Infinity.0

- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Layout/Node.java:114; .../ProximityOverlapRemoval/MinimumSpanningTree/GTreeOverlapRemoval.java:276
- **行号**: Node.java:114; GTreeOverlapRemoval.java:276-277
- **错误信息**: 需要'.' / 需要')'或',' / 不是语句
- **代码片段**:
  `java
  return ObjectHelper.objectToString(super);          // Node.java:114
  t = Math.min(Infinity.0, t);                        // GTreeOverlapRemoval.java:276
  `
- **对应 C# 文件**: Core\Layout\Node.cs:81 (return base.ToString();); Core\Layout\ProximityOverlapRemoval\MST\GTreeOverlapRemoval.cs:281,293 (const double expandMax = double.PositiveInfinity; t = Math.Min(expandMax, t);)
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs:6512-6518; src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs:576-614 (RenderConstLocalValue)
- **分析**: (1) base.ToString() 解析到 System.Object.ToString() 时命中 ObjectHelper.objectToString 分支，receiver "base" 被 facade 渲染为 "super"，而 super 不能作为方法参数；(2) const 局部变量 double.PositiveInfinity 经 RenderConstLocalValue 用 d.ToString(InvariantCulture) 得到 "Infinity"，因无小数点追加 ".0" 成为非法字面量 "Infinity.0"。

## Iteration 2 — IRectangle add 桥接方法返回类型错误

- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Layout/LargeGraphLayout/Interval.java:61,158,159
- **行号**: Interval.java:158
- **错误信息**: Interval中的add(java.lang.Double)无法实现IRectangle中的add(P); 返回类型IRectangle<Double>与void不兼容
- **代码片段**:
  `java
  public IRectangle<Double> add(Double point) {
      return add((double) point);
  }
  `
- **对应 C# 文件**: Core\Geometry\IRectangle.cs:3 (void Add(P point);); Layout\LargeGraphLayout\Interval.cs:38 (public void Add(double v))
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs:3430-3436 (AddIRectangleBridgeMethods)
- **分析**: bd189ff3 曾基于"readonly 迁移会把 IRectangle.add 返回类型扩大为接口类型"的错误假设，将桥接方法返回类型从 void 改成 IRectangle<P>；但 C# 中 IRectangle.Add(P) 始终返回 void（readonly 迁移的显式接口实现也是 void），导致生成的桥接方法无法实现接口方法。已改回 void。

✅ Fixed (2026-08-09) — AddIRectangleBridgeMethods 桥接 add(BoxedType) 改回 void 返回。验证: mvn clean package -e → BUILD SUCCESS (EXIT=0)。

## Iteration 1 — incompatible types: __suppress__ cannot be converted to String (serialization ctor)
- **Java 文件**: qut-shiftreduceparser/src/main/java/QUT/Gppg/ShiftReduceParser.java:398,405,412
- **错误信息**: 不兼容的类型: io.github.ningpp.compat.__suppress__无法转换为java.lang.String
- **代码片段**:
  `
  java
  protected AcceptException(__suppress__ i, __suppress__ c) {
      super(i, c);
  }
  `
- **对应 C# 文件**: tools\QUT.ShiftReduceParser\ShiftReduceParser.cs:411-449 (AcceptException/AbortException/ErrorException)
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs:37-51
- **分析**: .NET 序列化构造函数 (SerializationInfo, StreamingContext) 若函数体只做 base 转发，转换器会按注释"让派生调用点可编译"发射 __suppress__ 参数版本；但 super(i, c) 的 Java 基类 (Exception→RuntimeException) 没有 (Object,Object) 构造函数，javac 必然报错。发射这个构造函数本身就是错误——Java 序列化从不使用该模式，应整体省略。

## Iteration 2 — 运行时 ClassCastException: Character 无法转换为 Number (MemoryMarshal.asBytes)
- **Java 文件**: msagltests (InitialLayoutTests/SplineRouterTests 等)
- **错误信息**: java.lang.ClassCastException: class java.lang.Character cannot be cast to class java.lang.Number
  at io.github.ningpp.compat.MemoryMarshal.asBytes(MemoryMarshal.java:13)
  at dotnet.xml.NameTable.computeHash32(NameTable.java:162)
- **对应 C# 文件**: D:\csharpxml\System\Xml\NameTable.cs:238 (MemoryMarshal.AsBytes(key.AsSpan()))
- **根因分类**: compat 库缺陷
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/MemoryMarshal.java
- **分析**: C# MemoryMarshal.AsBytes(ReadOnlySpan<char>) 按 UTF-16 小端语义每字符产出 2 字节；compat asBytes 无条件按 Number 处理（每元素 4 字节），char span 元素是 Character → ClassCastException。
- **修复**: asBytes 检测 Character 元素，按 UTF-16 小端 2 字节展开。
- **测试**: MemoryMarshalTest.asBytesReinterpretsCharacterSpanAsUtf16LittleEndianBytes（先红后绿）

## Iteration 3 — 找不到符号 XName.toXName / attribute(XName) 不兼容 String
- **Java 文件**: dgmlparser/src/main/java/DgmlParser/DgmlParser.java:38 等 10 处
- **错误信息**: 找不到符号 方法 toXName(java.lang.String) 位置: 类 io.github.ningpp.compat.XName；随后: 不兼容的类型: XName无法转换为java.lang.String
- **对应 C# 文件**: E:\agl-master\GraphLayout\tools\DgmlParser\DGMLParser.cs:34 (nodeElement.Attribute("Id"))
- **根因分类**: compat 库缺陷
- **涉及组件**: TypeOperationTransformer.cs:606 / ExpressionTransformerHelpers.cs:293（生成 to<Type> 工厂调用）；java/csharptojava-compat 的 XName.java、XElement.java
- **分析**: 转换器对 C# 隐式转换 string→XName 按约定合成 XName.toXName(...) 调用并传给 attribute(XName)；compat 缺 toXName 工厂且 attribute 只收 String。
- **修复**: XName 增加 static toXName(String) 工厂；XElement 增加 attribute(XName) 重载（委托 String 版本）。
- **测试**: XmlLinqTest.toXNameFactoryCreatesNameFromString / attributeAcceptsXNameFromToXNameFactory（先红后绿；首版测试自身有死循环 bug 已修正）

✅ Fixed (2026-08-09) — mvn clean package -e → BUILD SUCCESS (EXIT=0)，9 模块全部成功，609 测试 0 失败 0 错误。


## Iteration 8 (MSAGL ??????) ? ??????????? / ?????????

### Bug 8A ? ???????????? Class ?????ICurve.derivative ? ~108 ??
- **Java ??**: Microsoft/Msagl/Core/Geometry/Curves/ICurve.java????????/???
- **????**: `<P, TA> derivative(Class<TA>, Class<P>, double)` ? ??????????????? Class ??
- **????**:
  ```java
  Point<P, TA> derivative(Class<TA> _cs2j_TA, Class<P> _cs2j_P, double t);
  ```
- **?? C# ??**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\Curves\ICurve.cs
- **????**: ??????????????
- **????**: src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs (AddPendingRuntimeClassParameters)
- **??**: ?? CLI ?????? GetDeclaredSymbol ?? null?AddPendingRuntimeClassParameters ? sym==null ?? DrainClassTypeParams() ?? drain?? RectangleNodeUtils ??????? {TA,P} ???????????
- **??**: sym==null ????? pending ?????????????

### Bug 8B ? static ???????????? Class<?> ???RTree ~14 ??
- **Java ??**: Microsoft/Msagl/Core/Geometry/RTree.java
- **????**: ?? addNodeToTreeRecursive ?????????????????????? Class<?> clazz???????
- **????**:
  ```java
  static <T, P> void addNodeToTreeRecursive(RectangleNode<T, P> newNode, RectangleNode<T, P> existingNode, Class<?> clazz) { ... }
  addNodeToTreeRecursive(node, _rootNode); // ? clazz
  ```
- **?? C# ??**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\RTree\RTree.cs
- **????**: Transformer ????
- **????**: src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs (AddRuntimeClassParametersForTypeParameterArrays)
- **??**: static ???????????? Class<?> ????????? GetClassTypeTokensForCall ?? DeclaringMethod==originalDef ???? TP ?? token??? TP ? token?????? default(T)?null?typeof(T)????? tClass ?????????
- **??**: static ???????? TP??? TP ? AddRuntimeClassFields ??? static Class ??????

### Bug 8C ? erasure ?????_cs2j_ token ????? + MetadataName key ???RectangleNodeUtils ~6 ??
- **Java ??**: Microsoft/Msagl/Core/Geometry/RectangleNodeUtils.java?Routing/ShapeObstacleCalculator.java?Routing/Spline/Bundling/SdShortestPath.java
- **????**: crossRectangleNodes ????? 2 ? Class token?Shape.class, Point.class?? 3tp ??? Class ???2tp ???? token ????????Class<P> ?? Class<TA>?
- **????**:
  ```java
  // ???3tp?? Class ???
  public static <TA, TB, P> void crossRectangleNodes(RectangleNode<TA, P> a, RectangleNode<TB, P> b, BiConsumer<TA, TB> action)
  // ?????? 2 ? token?
  RectangleNodeUtils.crossRectangleNodes(Shape.class, Point.class, childrenShapeHierarchy, coupleHierarchy, ...);
  ```
- **?? C# ??**: E:\agl-master\GraphLayout\MSAGL\Core\Geometry\RTree\RectangleNodeUtils.cs
- **????**: Transformer ???? + ?????key ?????
- **????**: src/CSharpToJava.Core/Context/ConversionContext.cs?src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs (GetClassTypeTokensForCall)?src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs (AddPendingRuntimeClassParameters)
- **??**: (1) IMethodSymbol.MetadataName ????????????? "CrossRectangleNodes"??? MetadataName ? key ??/???? 2tp ? {TA,P} token ??? 3tp ????(2) AddPendingRuntimeClassParameters ? OrderBy+Insert(0) ????????????? token????????? Class<P> ?? Class<TA>?
- **??**: ?????/??????? ToDisplayString key??????????????????????????????????? SetMethodClassTypeParamNames ???????????? token ??

### ??
- Red: tests/CSharpToJava.Tests/AglGenericClassTokenRegressionTests.cs 3 ???????? / RTree static / erasure ????????
- ??: dotnet test 2634 ?? 0 ???
- mvn: D:\cs-msagl-0810 ? mvn clean package -e ? BUILD SUCCESS (EXIT=0)??? D:\cs-msagl-0810\mvn-build.log?

? Fixed (2026-08-10)
