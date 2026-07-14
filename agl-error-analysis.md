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
- **对应 C# 文件**: `E:\agl-master\GraphLayout\tools\Dot2Graph\Parser.cs` / `E:\agl-master\GraphLayout\Test\MSAGLTests\SugiyamaLayoutTests.cs`
- **根因分类**: 待分析
- **涉及组件**: 待定位
- **分析**: 待分析
- **状态**: 🔄 In Progress
