# Dot2Graph 迭代错误分析

## 说明
首次 `mvn clean package -e` 在 `msagltests` 模块测试阶段失败，无编译错误（`mvn -DskipTests` 可 BUILD SUCCESS）。因此将测试错误按相同流程处理。

## Iteration 1 — XmlException: Data at the root level is invalid
- **Java 文件**: `msagltests/src/test/java/Microsoft/Msagl/UnitTests/InitialLayoutTests.java`
- **行号**: 205（调用栈最终位于 `GeometryGraphReader.createFromFile` line 166，由 `MsaglTestBase.loadGraph` line 176 调用）
- **错误信息**: `dotnet.xml.XmlException: Data at the root level is invalid. Line 1, position 1.`
- **代码片段**:
  ```java
  var resolvedGraphFileName = resolveTestFilePath(geometryGraphFileName);
  GeometryGraph graph = null;
  settings.value = null;
  if (StringHelper.endsWith(resolvedGraphFileName, ".geom", true)) {
      graph = GeometryGraphReader.createFromFile(resolvedGraphFileName, settings);
      setupPorts(graph);
  }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\DebugHelpers\Persistence\GeometryGraphReader.cs` / `E:\agl-master\GraphLayout\Test\MSAGLTests\MsaglTestBase.cs`
- **根因分类**: Transformer（字节无符号语义丢失）
- **涉及组件**:
  - 转换器：`src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs`
  - 依赖产物：`D:\cs-xml-20260712\system-private-xml\src\main\java\dotnet\xml\XmlTextReaderImpl.java`
- **分析**:
  1. `.geom` 资源文件以 UTF-8 BOM（`EF BB BF`）开头， followed by `<?xml ...>`。
  2. `GeometryGraphReader.createFromFile` 把文件流交给转换后的 `dotnet.xml.XmlTextReaderImpl`。
  3. `XmlTextReaderImpl.detectEncoding()` 中有 `int first2Bytes = _ps.bytes[0] << 8 | _ps.bytes[1];`；Java 的 `byte` 是有符号的，移位时符号扩展，导致无法匹配 `case 0xEFBB`，UTF-8 BOM 未被识别。
  4. `eatPreamble()` 中有 `if (_ps.bytes[i] != preamble.get(i))`；`_ps.bytes[i]` 是有符号 byte（如 `-17`），而 `preamble.get(i)` 由 `MemoryExtensions.asSpan(byte[])` 转成了无符号 Integer（如 `239`），比较永远失败，BOM 未被吃掉。
  5. 两个位置都源于同一生成缺陷：C# `byte`（无符号）映射到 Java `byte`（有符号）后，在参与 int 运算/比较时未做 `& 0xFF` 无符号扩展。
  6. 当前转换器对 `(byte)expr` 强制转换会生成 `& 0xFF`，但对 `byte[]` 元素在表达式中直接使用时未自动加掩码，导致 csharpxml 的 XML 读取器在处理带 BOM 文件时出错。
- **状态**: ✅ Fixed
  - 修复 commit: ElementAccessTransformer 对 byte[] 读加 `& 0xFF`，BinaryExpressionTransformer 对 `& 0xFF` 子表达式补括号保持优先级；并跳过赋值/ref/out/++/-- 等 LHS 场景。
  - 验证: `ByteArrayElementInBitwiseExpression_AddsMask` Red→Green；全量 dotnet test 2148 通过；重新转换并安装 `D:\csharpxml`→`D:\cs-xml-20260712` 后，`XmlTextReaderImpl` BOM 检测代码已生成 `& 0xFF`。

## Iteration 2 — ClassCastException: [I cannot be cast to [Ljava.lang.Object;
- **Java 文件**: `automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/RTree.java`
- **行号**: 152
- **错误信息**: `java.lang.ClassCastException: class [I cannot be cast to class [Ljava.lang.Object;`
- **代码片段**:
  ```java
  public T[] getAllIntersecting(IRectangle<P> queryRegion) {
      return (_rootNode == null || getCount() == 0 ? (T[]) TypeHelper.newArrayInstance(tClass, 0) : StreamSupport.stream(_rootNode.getNodeItemsIntersectingRectangle(queryRegion).spliterator(), false).toArray(size -> (T[]) TypeHelper.newArrayInstance(tClass, size)));
  }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\Geometry\RTree\RTree.cs`
- **根因分类**: Compat 库缺陷
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java`
- **分析**:
  C# `RTree<T,P>` 支持值类型 `T`（如 `RTree<int,Point>`）。Java 泛型 `T[]` 不能是原始类型数组；当 `tClass == Integer.class` 时，`TypeHelper.newArrayInstance` 返回 `int[]`，被强转为 `T[]`（即 `Object[]`）时抛出 `ClassCastException`。
  `TypeHelper.newArrayInstance` 仅从类型参数数组创建路径调用（`ObjectCreationTransformer` 和 `InvocationExpressionTransformer`），结果总是被强转为 `T[]`。原始数组（`int[]`）不能强转为 `Object[]`。
  非泛型原始数组创建（如 `new int[5]`）不经过此方法，转换器直接生成 `new int[5]`。
- **状态**: ✅ Fixed
  - 修复: 移除 `TypeHelper.newArrayInstance` 中的原始类型快捷路径，统一使用 `java.lang.reflect.Array.newInstance(componentType, length)` 返回包装类型数组（`Integer[]` 而非 `int[]`）。
  - 验证: `TypeParameterArrayInstantiatedWithPrimitive_RuntimeClassCreatesBoxedArray` Red→Green；全量 dotnet test 2149 通过；重新转换+编译 Dot2Graph，RTreeTest 5 测试通过，ClassCastException 已消失。

## Iteration 3 — NullPointerException: Parser.parse returned null
- **Java 文件**: `msagltests/src/test/java/Microsoft/Msagl/UnitTests/SugiyamaLayoutTests.java`
- **行号**: 93
- **错误信息**: `java.lang.NullPointerException: Cannot invoke "Microsoft.Msagl.Drawing.Graph.createGeometryGraph()" because "drawGraph" is null`
- **代码片段**:
  ```java
  drawGraph = Parser.parse(allFiles[next], _lineHolder2, _columnHolder2, _msgHolder2);
  // ...
  drawGraph.createGeometryGraph();  // NPE: drawGraph is null
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\tools\Dot2Graph\Parser.cs` / `E:\agl-master\GraphLayout\tools\Dot2Graph\PosData.cs` / `E:\agl-master\GraphLayout\tools\Dot2Graph\AttributeValuePair.cs`
- **根因分类**: Compat 库缺陷（运行时类型丢失）
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpGenericIterable.java`
- **分析**:
  1. C# `PosData.ControlPoints` 属性在 `edgeCurve == null` 时直接返回 `controlPoints`（`List<Point>` 字段），运行时类型为 `List<Point>`。
  2. `AttributeValuePair.CreatePosData()` 使用 `(ret.ControlPoints as List<P2>).Add(p)` 向列表添加点——`as` 转换成功是因为运行时类型确实是 `List<P2>`。
  3. 转换器将 C# `IEnumerable<T>` 返回值包装为 `CSharpGenericIterable.from(controlPoints)`。`from()` 方法将 `CSharpList` 包装为匿名内部类，改变了运行时类型。
  4. 生成代码中使用 `instanceof CSharpList` 判断是否可直接强转。由于 `from()` 返回的是匿名类而非 `CSharpList`，`instanceof CSharpList` 失败，走 `new CSharpList<>(...)` 复制路径。
  5. `createPosData()` 添加的点去了临时副本，原始 `controlPoints` 列表仍为空。
  6. 后续 `addNodeAttrs()` 的 `Pos` case 调用 `.get(0)` 访问空列表，抛出 `IndexOutOfBoundsException: Index 0 out of bounds for length 0`。
  7. `Parser.parse()` 捕获 `RuntimeException` 并调用 `yyerror()`，返回 `null`，导致上层 NPE。
- **修复**: 修改 `CSharpGenericIterable.from()` 方法，在输入已经是 `CSharpGenericIterable` 实例时直接返回原对象（passthrough），保留运行时类型，使 `instanceof CSharpList` 检查成功。
- **状态**: ✅ Fixed
  - 验证: `CSharpGenericIterableFrom_ReturnsOriginalWhenAlreadyCSharpGenericIterable` Red→Green；全量 dotnet test 2143 通过；`mvn clean package -e` BUILD SUCCESS，609 测试全部通过（387 skipped），b3.dot 成功解析。

## 最终结果
- `mvn clean package -e` 在 `d:\Dot2Graph260713` 上 BUILD SUCCESS
- 测试总数: 609，通过: 609，失败: 0，错误: 0，跳过: 387
- 所有 9 个模块编译成功
- b3.dot 等 dot 文件成功解析

---

## MSAGL/GraphLayout 迭代错误分析

## Iteration 1 — 双精度科学计数法常量生成非法字面量 `1E-06.0`
- **Java 文件**: `automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/OverlapRemovalGlobalConfiguration.java`
- **行号**: 68
- **错误信息**: `/D:/agl-726/automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/OverlapRemovalGlobalConfiguration.java:[68,60] 需要';'`
- **代码片段**:
  ```java
  public static final double ClusterDefaultFreeWeight = 1E-06.0;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\Geometry\OverlapRemoval\OverlapRemovalGlobalConfiguration.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs`（`RenderConstantValue` 方法）
- **分析**: C# 常量 `public const double ClusterDefaultFreeWeight = 1e-6;` 的编译期值为 `double` 类型。`RenderConstantValue` 使用 `double.ToString(System.Globalization.CultureInfo.InvariantCulture)` 得到 `1E-06`，随后因为没有小数点而追加 `.0`，生成非法 Java 字面量 `1E-06.0`。同时影响 `EventComparisonEpsilon`、`LgPathRouter`、`OverlapRemovalFixedSegmentsMst`、`BundleBasesCalculator` 等同类科学计数法常量。
- **状态**: ✅ Fixed
  - 修复 commit: FieldTransformer.RenderConstantValue 对含 E/e 的科学计数法 double 字面量不再追加 `.0`。
  - 验证: `DoubleConstScientificNotation_DoesNotAppendDecimalZero` / `DoubleConstWithDecimal_DoesNotAppendExtraDecimalZero` Red→Green；全量 dotnet test 通过；重新转换后该错误已消失。

## Iteration 2 — 接口索引器返回类型丢失为 Object
- **Java 文件**: `automaticgraphlayout/src/main/java/Microsoft/Msagl/Miscellaneous/LayoutEditing/IncrementalDragger.java`
- **行号**: 184
- **错误信息**: `/D:/agl-726/automaticgraphlayout/src/main/java/Microsoft/Msagl/Miscellaneous/LayoutEditing/IncrementalDragger.java:[184,54] 不兼容的类型: java.lang.Object无法转换为Microsoft.Msagl.Core.Geometry.Point`
- **代码片段**:
  ```java
        var par = curve.getParameterAtLength(lenAtLabelAttachment);
        var tang = curve.derivative(par);
        var norm = Point.multiply(((lf.RightSide ? tang.rotate90Cw() : tang.rotate90Ccw())).normalize(), lf.NormalLength);
        edge.getLabel().setCenter(Point.add(curve.get(par), norm));
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\Geometry\Curves\ICurve.cs` / `E:\agl-master\GraphLayout\MSAGL\Miscellaneous\LayoutEditing\IncrementalDragger.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs`
- **分析**: C# 接口 `ICurve` 声明索引器 `Point this[double t] { get; }`。`IndexerTransformer` 在解析返回类型时仅使用 `context.GetTypeInfo(indexerDecl.Type)`；对于 partial/merged 后的接口语法节点，语义模型无法解析 `Point` 类型，回退为 `Object`，导致生成的 `ICurve.get(double)` 返回 `Object`。所有实现类虽可能生成正确的 `Point get(double)`，但接口方法签名错误，调用方 `curve.get(par)` 被当作 `Object`，无法传给需要 `Point` 的方法参数。
- **状态**: ✅ Fixed
  - 修复 commit: IndexerTransformer 优先解析 IPropertySymbol，在 `GetTypeInfo` 不可用时使用 `symbol.Type` 作为返回类型回退。
  - 验证: `InterfaceIndexer_WithConcreteReturnType_GeneratesTypedGetter` Red→Green；全量 dotnet test 2333 通过；重新转换后 `IncrementalDragger.java:184` 错误已消失。
