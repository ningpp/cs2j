## Iteration 1 - cannot find nested class symbol
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Routing\Rectilinear\SegmentIntersector.java`
- **行号**: 57
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Routing/Rectilinear/SegmentIntersector.java:[57,55] 找不到符号  符号: 类 SegEvent`
- **代码片段**:
  ```java
  import Microsoft.Msagl.DebugHelpers.Timer;
  import Microsoft.Msagl.Core.Layout.Edge;
  
  public class SegmentIntersector implements Comparator<SegEvent> {
          // To be returned to caller; created in Generate() and used in ScanGenerate().
      // Accumulates the set of events and then sorts them by Y coord.
  private ArrayList<SegEvent> eventList = new ArrayList<SegEvent>();
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Routing\Rectilinear\SegmentIntersector.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Type\ClassTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Context\TypeMappingService.cs`
- **分析**: `ClassTransformer` 在生成类声明的 `implements` 子句时调用正文通用的 `context.MapType`，而 `TypeMappingService.TryMapNestedType` 会在当前外层类型内把 `SegmentIntersector.SegEvent` 缩短为 `SegEvent`；该简单名在 Java 类声明头尚不可见，必须生成 `SegmentIntersector.SegEvent`。

## Iteration 2 - Predicate delegate invocation uses apply
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Core\DataStructures\RbTree.java`
- **行号**: 82
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Core/DataStructures/RbTree.java:[82,15] 找不到符号  符号: 方法 apply(T)  位置: 类型为java.util.function.Predicate<T>的变量 p`
- **代码片段**:
  ```java
        return null;
        }
        RBNode<T> good = null;
        while (n != nil) {
        n = (p.apply(n.Item) ? (good = n).left : n.right);
        }
        return good;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\DataStructures\RBTree\RBTree.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Context\TypeMappingService.cs`, `D:\code\cs2j\config\TypeMappings.json`
- **分析**: `TypeMappingService` 已把 `System.Func<T,bool>` 类型特化为 Java `Predicate<T>`，但委托调用转换仍通过 `TypeMappings.json`/SAM 推断把 `Func.Invoke` 统一生成为 `apply`，导致生成的 `Predicate<T>` 调用不存在的 `apply(T)` 而不是 `test(T)`。

## Iteration 3 - Array.CreateInstance returns raw Object for CSharpArray local
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Core\DataStructures\Set.java`
- **行号**: 121
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Core/DataStructures/Set.java:[121,66] 不兼容的类型: java.lang.Object无法转换为io.github.ningpp.compat.CSharpArray`
- **代码片段**:
  ```java
        }
        public CSharpArray toArray(Class type) {
            try {
                CSharpArray ret = java.lang.reflect.Array.newInstance(type, this.size());
                int i = 0;
                for (T o : this) {
                java.lang.reflect.Array.set(ret, i++, o);
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\DataStructures\Set.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\config\TypeMappings.json`, `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\CSharpArray.java`
- **分析**: `System.Array` 类型映射为兼容层 `CSharpArray`，但 `InvocationExpressionTransformer` 将 `System.Array.CreateInstance(type, length)` 直接降为返回 `Object` 的 `java.lang.reflect.Array.newInstance(...)`，没有用 `CSharpArray.of(...)` 包装，导致 `CSharpArray ret = <Object>` 类型不匹配。

## Iteration 4 - uint increment mask assigns long to int
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Core\Geometry\ConstraintGenerator.java`
- **行号**: 211
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Core/Geometry/ConstraintGenerator.java:[211,50] 不兼容的类型: 从long转换到int可能会有损失`
- **代码片段**:
  ```java
        // Debug.Assert(null != initialCluster, "initialCluster must not be null");

        int _us1 = this.nextNodeId;
        this.nextNodeId = ((this.nextNodeId + 1) & 0xFFFFFFFFL);
        var nodNew = new OverlapRemovalNode(_us1, userData, position, positionP, size, sizeP, weight);
        initialCluster.addNode(nodNew);
        return nodNew;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\Geometry\OverlapRemoval\ConstraintGenerator.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\UnaryExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Utilities\ExpressionTransformerHelpers.cs`
- **分析**: `uint` 字段映射为 Java `int`，但 `UnaryExpressionTransformer` 为 `uint` 的 `++` 生成 `& 0xFFFFFFFFL` 掩码表达式后未转回 `int`；`0xFFFFFFFFL` 使右值成为 `long`，导致生成的 `this.nextNodeId = <long>` 不能赋给 Java `int` 字段。

## Iteration 5 - array CopyTo emitted as missing instance method
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Core\Geometry\MultidimensionalScaling.java`
- **行号**: 291
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Core/Geometry/MultidimensionalScaling.java:[291,13] 找不到符号  符号: 方法 copyTo(io.github.ningpp.compat.CSharpArray,int)  位置: 类 double[]`
- **代码片段**:
  ```java
        ValidateArg.isNotNull(d, "d");
        double[][] b = new double[d.length][];
        for (int i = 0; i < d.length; i++) {
        b[i] = new double[d[0].length];
        d[i].copyTo(CSharpArray.of(b[i]), 0);
        }
        squareEntries(b);
        doubleCenter(b);
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\Geometry\MultidimensionalScaling.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ArgumentTransformer.cs`
- **分析**: `InvocationExpressionTransformer` 的 `CopyTo` 特例只接受第一个参数语义类型为 `IArrayTypeSymbol`，但数组实例的 `System.Array.CopyTo(Array,int)` 参数类型是 `System.Array`；转换器因此退回普通方法调用，`ArgumentTransformer` 又按 `System.Array` 参数把目标数组包成 `CSharpArray.of(...)`，最终在 Java 数组上生成不存在的 `copyTo` 实例方法。

## Iteration 6 - ReadState enum member ambiguous between wildcard imports
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\DebugHelpers\Persistence\GeometryGraphReader.java`
- **行号**: 594
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/DebugHelpers/Persistence/GeometryGraphReader.java:[594,46] 对ReadState的引用不明确  io.github.ningpp.compat 中的类 io.github.ningpp.compat.ReadState 和 dotnet.xml 中的枚举 dotnet.xml.ReadState 都匹配`
- **代码片段**:
  ```java
        if (getXmlReader().getNodeType() == XmlNodeType.EndElement && Objects.equals(getXmlReader().getName(), "graph")) {
        return GeometryToken.End;
        }
        GeometryToken token;
        if (getXmlReader().getReadState() == ReadState.EndOfFile) {
        return GeometryToken.Graph;
        }
        ObjectHolder<GeometryToken> _tokenHolder1 = new ObjectHolder<>();
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\DebugHelpers\Persistence\GeometryGraphReader.cs`
- **根因分类**: 类型映射缺失
- **涉及组件**: `D:\code\cs2j\config\TypeMappings.json`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs`, `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\ReadState.java`
- **分析**: `System.Xml` using 会生成 `dotnet.xml.*`，兼容层又全局引入 `io.github.ningpp.compat.*`；两边都含 `ReadState`。`System.Xml.ReadState` 只靠命名空间映射到 `dotnet.xml`，没有显式类型映射，且缺引用场景下 `ReadState.EndOfFile` 不能走符号型 enum 处理，因此生成裸 `ReadState.EndOfFile` 并在两个 wildcard import 下产生 Java 歧义。

## Iteration 7 - Property getter lost in dictionary indexer assignment
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Routing\Spline\Bundling\NodePositionsAdjuster.java`
- **行号**: 268
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Routing/Spline/Bundling/NodePositionsAdjuster.java:[268,48] length 在 Microsoft.Msagl.Routing.Spline.Bundling.Metroline 中是 private 访问控制`
- **代码片段**:
  ```java
        polylineLength = new LinkedHashMap<Metroline, Double>();
        //create polylines
        for (Metroline metroline : metroGraphData.getMetrolines()) {
        polylineLength.put(metroline, metroline.length);
        for (PolylinePoint pp = metroline.getPolyline().getStartPoint(); pp.getNext() != null; pp = pp.getNext()) {
        var segment = new PointPair(pp.getPoint().clone(), pp.getNext().getPoint().clone());
        { Set<Metroline> _tc14 = segsToPolylines.get(segment); if (_tc14 == null) { _tc14 = new Set<Metroline>(); segsToPolylines.put(segment, _tc14); } _tc14.add(metroline); };
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Routing\Spline\Bundling\NodePositionsAdjuster.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs`
- **分析**: 同一 C# 文件早先有 `Station[] metroline` 参数，后面又在 `foreach (var metroline in metroGraphData.Metrolines)` 中复用名称；`IsDeclaredAsConcreteArray` 按整棵语法树查找早于访问点的同名声明，误把后者的 `Metroline.Length` 当成数组 `Length` 输出 `.length`，但 `Metroline` 的 Java auto-property backing field 是 private。

## Iteration 8 - StringReader constructor wrapped as TextReader
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout.Drawing\src\main\java\Microsoft\Msagl\Drawing\GraphReader.java`
- **行号**: 145
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout.Drawing/src/main/java/Microsoft/Msagl/Drawing/GraphReader.java:[145,31] 不兼容的类型: io.github.ningpp.compat.TextReader无法转换为java.io.StringReader`
- **代码片段**:
  ```java
            readEndElement();
            Class t = Class.forName(typeString);
            DataContractSerializer dcs = new DataContractSerializer(t);
            StringReader sr = new TextReader(new StringReader(serString));
            XmlReader xr = XmlReader.create(sr);
            return dcs.readObject(xr, true);
        } catch (Exception _e_cs2j) {
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\GraphReader.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ObjectCreationTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.Declarations.cs`, `D:\code\cs2j\config\TypeMappings.json`
- **分析**: `TypeMappings.json` 正确把局部变量声明 `System.IO.StringReader` 映射为 `java.io.StringReader`，但 `ObjectCreationTransformer` 对 `new StringReader(string)` 无条件返回 `new TextReader(new StringReader(...))`，声明和初始化表达式类型因此不一致。

## Iteration 9 - DataContractSerializer ReadObject overload missing
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout.Drawing\src\main\java\Microsoft\Msagl\Drawing\GraphReader.java`
- **行号**: 147
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout.Drawing/src/main/java/Microsoft/Msagl/Drawing/GraphReader.java:[147,23] 找不到符号  符号: 方法 readObject(dotnet.xml.XmlReader,boolean)  位置: 类型为io.github.ningpp.compat.DataContractSerializer的变量 dcs`
- **代码片段**:
  ```java
            DataContractSerializer dcs = new DataContractSerializer(t);
            StringReader sr = new StringReader(serString);
            XmlReader xr = XmlReader.create(new TextReader(sr));
            return dcs.readObject(xr, true);
        } catch (Exception _e_cs2j) {
            throw new RuntimeException(_e_cs2j);
        }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\GraphReader.cs`
- **根因分类**: Lowering
- **涉及组件**: `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\DataContractSerializer.java`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\config\TypeMappings.json`
- **分析**: 转换器将 `DataContractSerializer.ReadObject(XmlReader,bool)` 按普通实例方法输出为 `readObject(xr, true)`，但兼容运行时 `DataContractSerializer` 只有构造器，没有对应的 `readObject`/`writeObject` 表面，因此生成代码链接不到方法。

## Iteration 10 - Array.Copy casts arrays to CSharpArray
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\QUT.ShiftReduceParser\src\main\java\QUT\Gppg\PushdownPrefixState.java`
- **行号**: 49
- **错误信息**: `[ERROR] /D:/agl26/QUT.ShiftReduceParser/src/main/java/QUT/Gppg/PushdownPrefixState.java:[49,43] 不兼容的类型: T[]无法转换为io.github.ningpp.compat.CSharpArray`
- **代码片段**:
  ```java
        public void push(T value) {
        try {
            if (this.tos >= this.array.length) {
            T[] objArray = (T[]) java.lang.reflect.Array.newInstance(tClass, this.array.length * 2);
            System.arraycopy((CSharpArray)(this.array), 0, (CSharpArray)(objArray), 0, this.tos);
            this.array = objArray;
            }
            this.array[this.tos++] = clonePushValue(value);
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\tools\QUT.ShiftReduceParser\PushdownPrefixState.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\TypeOperationTransformer.cs`, `D:\code\cs2j\config\TypeMappings.json`
- **分析**: `Array.Copy` 被特例降低为 `System.arraycopy`，但该分支先完整转换实参；C# 的 `(Array)this.array` 因 `System.Array` 映射为 compat `CSharpArray` 而输出 `(CSharpArray)(this.array)`，传给 Java `System.arraycopy(Object,...)` 时既不需要包装也无法编译。

## Iteration 11 - String.ToLower culture argument remains CultureInfo
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout.Drawing\src\main\java\Microsoft\Msagl\Drawing\GraphWriter.java`
- **行号**: 65
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout.Drawing/src/main/java/Microsoft/Msagl/Drawing/GraphWriter.java:[65,96] 不兼容的类型: io.github.ningpp.compat.CultureInfo无法转换为java.util.Locale`
- **代码片段**:
  ```java
        writeEndElement();
    }
    public static String firstCharToLower(Tokens attrKind) {
        var attrString = attrKind.toString();
        attrString = attrString.substring(0, 0 + 1).toLowerCase(CultureInfo.getInvariantCulture()) + attrString.substring(1, 1 + attrString.length() - 1);
        return attrString;
    }
    void writeAttribute(Tokens attrKind, Object val) {
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\GraphWriter.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\config\TypeMappings.json`, `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\CultureInfo.java`
- **分析**: `System.String.ToLower(CultureInfo)` 被方法映射输出为 Java `String.toLowerCase(...)`，但实参仍按 C# `CultureInfo.InvariantCulture` 映射为 compat `CultureInfo.getInvariantCulture()`；Java `String.toLowerCase(Locale)` 需要 `java.util.Locale`，转换器缺少该 overload 的文化参数适配。
- **修复验证**: 新增 `ToLower_WithCultureInfo_ConvertsCultureArgumentToLocale` 红测，修复后聚焦测试与全量 `dotnet test` 通过；重新转换后 `GraphWriter.java:65` 错误消失，Maven 第一错推进到 `SvgGraphWriter.java:205`。

## Iteration 12 - typeof(T).Assembly.GetName emits Package.getPackage()
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout.Drawing\src\main\java\Microsoft\Msagl\Drawing\SvgGraphWriter.java`
- **行号**: 205
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout.Drawing/src/main/java/Microsoft/Msagl/Drawing/SvgGraphWriter.java:[205,78] 无法将类 java.lang.Package中的方法 getPackage应用到给定类型; 需要: java.lang.String 找到: 没有参数 原因: 实际参数列表和形式参数列表长度不同`
- **代码片段**:
  ```java
    static void writeGraphAttr(GraphAttr graphAttr) {
    }
    void open() {
        writeComment("SvgWriter version " + SvgGraphWriter.class.getPackage().getPackage().toString());
        var box = _graph.getBoundingBox().clone();
        xmlWriter.writeStartElement("svg", "http://www.w3.org/2000/svg");
        writeAttributeWithPrefix("xmlns", "xlink", "http://www.w3.org/1999/xlink");
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\SvgGraphWriter.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs`, `D:\code\cs2j\config\TypeMappings.json`, `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\AssemblyCompat.java`
- **分析**: `typeof(SvgGraphWriter).Assembly` 走通用 `System.Type.Assembly -> getPackage` 映射，产出 Java `Class.getPackage()`；随后 `Assembly.GetName()` 又映射为 `getPackage()`，导致在 `java.lang.Package` 上调用不存在的无参实例 `getPackage()`，转换器缺少 `typeof(T).Assembly` 到 `AssemblyCompat.fromClass(T.class)` 的成员访问特例。
- **修复验证**: 新增 `TypeOf_Assembly_GetName_Version_UsesAssemblyCompat` 红测，修复后聚焦测试与全量 `dotnet test` 通过；重新转换后 `SvgGraphWriter.java:205` 错误消失，`D:\agl26` 下 `mvn clean test-compile -e` 通过并输出 `BUILD SUCCESS`。

## Iteration 13 - XmlReader IsEmptyElement emitted as field access
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\DebugHelpers\Persistence\GeometryGraphReader.java`
- **行号**: 193
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/DebugHelpers/Persistence/GeometryGraphReader.java:[193,19] 找不到符号  符号: 变量 IsEmptyElement  位置: 类型为dotnet.xml.XmlReader的变量 reader`
- **代码片段**:
  ```java
      LayoutAlgorithmSettings readLayoutAlgorithmSettings(XmlReader reader) {
          LayoutAlgorithmSettings layoutSettings = null;
          checkToken(GeometryToken.LayoutAlgorithmSettings);
          if (reader.IsEmptyElement) {
          reader.read();
          return null;
          }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\DebugHelpers\Persistence\GeometryGraphReader.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs`
- **分析**: `System.Xml.XmlReader.IsEmptyElement` 在参数接收者 `reader` 上没有命中显式成员映射或 getter 回退，成员访问转换落到原始字段访问 `reader.IsEmptyElement`，而生成项目里的 compat `XmlReader` 暴露的是 Java getter `getIsEmptyElement()`。
- **修复验证**: 新增 `SystemXml_XmlReader_IsEmptyElement_OnParameter_GeneratesGetter` 红测，修复后聚焦测试、`UsingAlias_MappedFrameworkType_UsesConfiguredImport` 回归测试、`dotnet build` 与全量 `dotnet test` 通过；重新转换后 `GeometryGraphReader.java:193` 错误消失，Maven 第一错推进到 `DebugCurveCollection.java:76`。

## Iteration 14 - List constructor wraps array field through missing getter
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\DebugHelpers\DebugCurveCollection.java`
- **行号**: 76
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/DebugHelpers/DebugCurveCollection.java:[76,85] 找不到符号  符号: 方法 getDebugCurvesArray()  位置: 类型为Microsoft.Msagl.DebugHelpers.DebugCurveCollection的变量 debugCurveCollection`
- **代码片段**:
  ```java
          try {
              String jsonString = FileHelper.readAllText(fileName);
              var debugCurveCollection = JsonSerializer.deserialize(jsonString, DebugCurveCollection.class);
              return new ArrayList<DebugCurve>(ArrayHelper.toList(debugCurveCollection.getDebugCurvesArray()));
          } catch (RuntimeException e) {
              System.out.println(e.toString());
              return new ArrayList<DebugCurve>();
          }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\DebugHelpers\DebugCurveCollection.cs`
- **根因分类**: 语义丢失
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Pipeline\ConversionPipeline.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Pipeline\ProjectCompilationBuilder.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs`
- **分析**: 转换器构建 Roslyn 编译时缺少 `System.Text.Json.dll` 引用，导致 `var debugCurveCollection = JsonSerializer.Deserialize<DebugCurveCollection>(...)` 的局部变量类型不可用；成员访问退入 unresolved-type getter 回退，把 C# 公共字段 `DebugCurvesArray` 错误生成为 `getDebugCurvesArray()`。
- **修复验证**: 新增 `ListConstructor_FromPublicArrayField_UsesFieldAccess` 红测，修复后聚焦测试、`dotnet build` 与全量 `dotnet test` 通过；重新转换后 `DebugCurveCollection.java` 改为 `debugCurveCollection.DebugCurvesArray`，Maven 第一错推进到 `GeometryGraphReader.java:599`。

## Iteration 15 - Enum.TryParse missing enum class literal
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\DebugHelpers\Persistence\GeometryGraphReader.java`
- **行号**: 599
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/DebugHelpers/Persistence/GeometryGraphReader.java:[599,35] 对于tryParse(java.lang.String,boolean,io.github.ningpp.compat.ObjectHolder<Microsoft.Msagl.DebugHelpers.GeometryToken>), 找不到合适的方法`
- **代码片段**:
  ```java
          return GeometryToken.Graph;
          }
          ObjectHolder<GeometryToken> _tokenHolder1 = new ObjectHolder<>();
          var _ifCond81 = EnumHelper.tryParse(getXmlReader().getName(), true, _tokenHolder1);
          token = _tokenHolder1.value;
          if (_ifCond81) {
          return token;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\DebugHelpers\Persistence\GeometryGraphReader.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`
- **分析**: `Enum.TryParse(XmlReader.Name, true, out token)` 的泛型枚举类型由 `out token` 推断；当前转换器只有在 Roslyn `methodSymbol.TypeArguments` 可用时才给 `EnumHelper.tryParse` 追加 `GeometryToken.class`，项目转换中该语义信息缺失时生成了少一个参数的 helper 调用。
- **修复验证**: 新增 `EnumTryParse_IgnoreCase_InferredOutEnum_GeneratesClassArg` 红测，修复后聚焦测试、`dotnet build` 与全量 `dotnet test` 通过；重新转换后 `GeometryGraphReader.java:599` 改为追加 `GeometryToken.class`，Maven 第一错推进到 `SteinerCdt.java:151`。

## Iteration 16 - var array local indexed as list
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Miscellaneous\ConstrainedSkeleton\SteinerCdt.java`
- **行号**: 151
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Miscellaneous/ConstrainedSkeleton/SteinerCdt.java:[151,53] 找不到符号  符号:   方法 get(int)  位置: 类型为java.lang.String[]的变量 lineParsed`
- **代码片段**:
  ```java
              }
              line = StringHelper.trimStart(line, ' ');
              var lineParsed = line.split("\\s{2,}");
              int ind = MathHelper.parseInt(lineParsed.get(0)) - 1;
              if (ind < oldPoints) {
              _outPoints.put(ind, _visGraph.findVertex(_pointList.get(ind)));
              } else {
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Miscellaneous\ConstrainedSkeleton\SteinerCdt.cs`
- **根因分类**: 语义丢失
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Pipeline\VarTypeResolver.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Pipeline\ConversionPipeline.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Pipeline\ProjectCompilationBuilder.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Context\TypeMappingService.cs`
- **分析**: `var lineParsed = Regex.Split(...)` 的语义类型应为 `string[]`，但 `var` 局部类型缓存没有可靠记录初始化器/声明符号推断出的数组类型，导致 `ElementAccessTransformer` 在语义较弱时把 `lineParsed[0]` 退化为 Java List 风格的 `lineParsed.get(0)`。
- **修复验证**: 新增 `VarLocal_FromRegexSplit_UsesArrayBracketAccess` 红测，修复后该测试、`ValueListBuilder_MappedToCompatClass` 聚焦回归、`dotnet build` 与全量 `dotnet test` 通过；重新转换后 `SteinerCdt.java:152` 生成为 `lineParsed[0]`，Maven 第一错推进到 `MsmtRectilinearPath.java:103 getPoint()`。

## Iteration 17 - inherited field access emitted as getter
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Routing\Rectilinear\MsmtRectilinearPath.java`
- **行号**: 103
- **错误信息**: `[ERROR] /D:/agl26/AutomaticGraphLayout/src/main/java/Microsoft/Msagl/Routing/Rectilinear/MsmtRectilinearPath.java:[103,43] 找不到符号  符号:   方法 getPoint()  位置: 类型为Microsoft.Msagl.Routing.Rectilinear.VisibilityVertexRectilinear的变量 source`
- **代码片段**:
  ```java
              for (var pair : getPathStage_ProceduralLinq1(sources, targets, sources)) {
              var source = pair.sourceV;
              var target = pair.targetV;
              if (PointComparer.equal(source.getPoint(), target.getPoint())) {
              continue;
              }
              var sourceCostAdjustment = SsstRectilinearPath.manhattanDistance(source.getPoint(), sourceCenter) * interiorLengthAdjustment;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Routing\Rectilinear\MsmtRectilinearPath.cs`, `E:\agl-master\GraphLayout\MSAGL\Routing\Visibility\VisibilityVertex.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.Loops.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Pipeline\VarTypeResolver.cs`
- **分析**: `VisibilityVertex.Point` 是继承来的字段，Java 基类也生成为 `Point` 字段；但 LINQ/var 转换后 `source.Point` 的接收者类型没有被可靠恢复，成员访问 fallback 把大写成员当作属性 getter，生成了不存在的 `source.getPoint()`。
- **修复验证**: 新增 `ExtractedLinqMethod_VarAliasToInheritedField_UsesFieldAccess` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换后 `MsmtRectilinearPath.java:103` 生成为 `PointComparer.equal(source.Point, target.Point)`，Maven 第一错推进到 `GraphReader.java:40`。

## Iteration 18 - property setter emitted as getter assignment
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\automaticgraphlayout-drawing\src\main\java\Microsoft\Msagl\Drawing\GraphReader.java`
- **行号**: 40
- **错误信息**: `[ERROR] /D:/agl26/automaticgraphlayout-drawing/src/main/java/Microsoft/Msagl/Drawing/GraphReader.java:[40,43] 意外的类型  需要: 变量  找到:    值`
- **代码片段**:
  ```java
      public GraphReader(StreamWrapper streamP) {
          stream = streamP;
          XmlReaderSettings readerSettings = new XmlReaderSettings();
          readerSettings.getIgnoreWhitespace() = true;
          readerSettings.getIgnoreComments() = true;
          xmlReader = XmlReader.create(stream, readerSettings);
      }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\GraphReader.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\AssignmentTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\IdentifierExpressionTransformer.cs`
- **分析**: `System.Xml.XmlReaderSettings.IgnoreWhitespace = true` 的左侧属性符号在当前转换语义下没有被 `AssignmentTransformer` 识别为 `IPropertySymbol`；随后通用赋值路径先把左侧成员访问转换为 getter，产出无效的 `readerSettings.getIgnoreWhitespace() = true`。
- **修复验证**: 新增 `SystemXml_XmlReaderSettings_PropertyAssignment_GeneratesSetter` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换后 `GraphReader.java:40-41` 生成为 `readerSettings.setIgnoreWhitespace(true)` / `readerSettings.setIgnoreComments(true)`，Maven 第一错推进到 `GraphReader.java:124`。

## Iteration 19 - AddRange receives split array
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\automaticgraphlayout-drawing\src\main\java\Microsoft\Msagl\Drawing\GraphReader.java`
- **行号**: 124
- **错误信息**: `[ERROR] /D:/agl26/automaticgraphlayout-drawing/src/main/java/Microsoft/Msagl/Drawing/GraphReader.java:[124,66] 不兼容的类型: java.lang.String[]无法转换为java.util.Collection<? extends java.lang.String>`
- **代码片段**:
  ```java
          var listOfSubgraphs = xmlReader.getAttribute(Tokens.listOfSubgraphs.toString());
          var subgraphTempl = new SubgraphTemplate();
          if (!StringHelper.isNullOrEmpty(listOfSubgraphs)) {
          subgraphTempl.SubgraphIdList.addAll(listOfSubgraphs.split(" "));
          }
          var listOfNodes = xmlReader.getAttribute(Tokens.listOfNodes.toString());
          if (!StringHelper.isNullOrEmpty(listOfNodes)) {
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\GraphReader.cs`, `E:\agl-master\GraphLayout\Drawing\SubgraphTemplate.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ArgumentTransformer.cs`
- **分析**: `List<T>.AddRange(IEnumerable<T>)` 被映射为 Java `addAll(Collection<T>)`，但当参数是来自弱语义 `var` 的 `string.Split` 调用时，参数适配未识别 Java 表达式会返回数组，导致未包装的 `split(...)` 直接传给 `addAll`。
- **修复验证**: 新增 `AddRange_WithStringSplitArray_WrapsArrayForAddAll` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换后 `GraphReader.java:124/128` 生成为 `addAll(ArrayHelper.toList(...split(" ")))`，Maven 第一错推进到 `GraphReader.java:146` 的 `XmlReader.create(StringReader)` 重载匹配问题。

## Iteration 20 - XmlReader factory receives StringReader
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\automaticgraphlayout-drawing\src\main\java\Microsoft\Msagl\Drawing\GraphReader.java`
- **行号**: 146
- **错误信息**: `[ERROR] /D:/agl26/automaticgraphlayout-drawing/src/main/java/Microsoft/Msagl/Drawing/GraphReader.java:[146,37] 对于create(java.io.StringReader), 找不到合适的方法`
- **代码片段**:
  ```java
              Class t = Class.forName(typeString);
              DataContractSerializer dcs = new DataContractSerializer(t);
              StringReader sr = new StringReader(serString);
              XmlReader xr = XmlReader.create(sr);
              return dcs.readObject(xr, true);
          } catch (Exception _e_cs2j) {
              throw new RuntimeException(_e_cs2j);
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\GraphReader.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ArgumentTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Utilities\ExpressionTransformerHelpers.cs`
- **分析**: `XmlReader.Create(TextReader)` 被转换为 Java compat `XmlReader.create(TextReader)` 时，静态工厂调用路径没有在 `StringReader` 实参处应用已有的 `System.IO.StringReader` → compat `TextReader` 适配，导致 Java 收到 `java.io.StringReader` 而非 `io.github.ningpp.compat.TextReader`。
- **修复验证**: 新增 `XmlReaderCreate_WithStringReader_WrapsArgumentAsCompatTextReader` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换后 `GraphReader.java:147` 生成为 `XmlReader.create(new TextReader(sr))`，Maven 第一错推进到 `GraphWriter.java:111` 的 `XmlWriter.create(StringWriter)` 重载匹配问题。

## Iteration 21 - XmlWriter factory receives StringWriter
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\automaticgraphlayout-drawing\src\main\java\Microsoft\Msagl\Drawing\GraphWriter.java`
- **行号**: 111
- **错误信息**: `[ERROR] /D:/agl26/automaticgraphlayout-drawing/src/main/java/Microsoft/Msagl/Drawing/GraphWriter.java:[111,33] 对于create(java.io.StringWriter), 找不到合适的方法`
- **代码片段**:
  ```java
      private static StringWriter writeUserDataToStream(Object obj, ObjectHolder<DataContractSerializer> dcs) {
          dcs.value = new DataContractSerializer(obj.getClass());
          var sw = new StringWriter();
          XmlWriter xw = XmlWriter.create(sw);
          dcs.value.writeObject(xw, obj);
          xw.flush();
          return sw;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\GraphWriter.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ArgumentTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Utilities\ExpressionTransformerHelpers.cs`
- **分析**: `XmlWriter.Create(TextWriter)` 被转换为 Java compat `XmlWriter.create(PrintWriter)` 时，静态工厂调用路径没有在 `StringWriter` 实参处应用已有的 `System.IO.StringWriter` → `PrintWriter` 适配，导致 Java 收到 `java.io.StringWriter` 而非 `java.io.PrintWriter`。
- **修复验证**: 新增 `XmlWriterCreate_WithStringWriter_WrapsArgumentAsPrintWriter` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换后 `GraphWriter.java:112` 生成为 `XmlWriter.create(new PrintWriter(sw))`，Maven 第一错推进到 `Dot2Graph/AttributeValuePair.java:552` 的 `java.awt.Color`/MSAGL `Color` 类型混用问题。

## Iteration 22 - Alias simple-name collision maps qualified Color parameter
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\Dot2Graph\src\main\java\Dot2Graph\AttributeValuePair.java`
- **行号**: 552
- **错误信息**: `[ERROR] /D:/agl26/Dot2Graph/src/main/java/Dot2Graph/AttributeValuePair.java:[552,47] 找不到符号  符号: 方法 getA()  位置: 类型为java.awt.Color的变量 gleeColor`
- **代码片段**:
  ```java
        return couple;
    }
    public static Color msaglColorToDrawingColor(Color gleeColor) {
        return DrawingColor.fromArgb(gleeColor.getA(), gleeColor.getR(), gleeColor.getG(), gleeColor.getB());
    }
    public static boolean parseLineWidth(String v, IntHolder lw) {
        lw.value = 0;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\tools\Dot2Graph\AttributeValuePair.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Context\ConversionContext.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Context\TypeMappingService.cs`
- **分析**: `using Color = System.Drawing.Color` 注册后，类型映射仅用 `typeSymbol.Name == "Color"` 判断 alias，导致显式限定的 `Microsoft.Msagl.Drawing.Color` 参数也被误映射为 alias 目标 `System.Drawing.Color`/`java.awt.Color`；方法体成员访问仍按真实 MSAGL Color 符号生成 `getA/getR/getG/getB`，形成签名和语义不一致。
- **修复验证**: 新增 `UsingAlias_QualifiedSameSimpleNameParameter_DoesNotResolveToAliasTarget` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换后 `AttributeValuePair.java` 生成为 `msaglColorToDrawingColor(Microsoft.Msagl.Drawing.Color gleeColor)`，Maven 第一错推进到 `Microsoft/Msagl/Drawing/Graph.java:199` 的 `dotnet.system.Collections` 包不存在问题。

## Iteration 23 - Non-generic ArrayList falls through to dotnet.system package
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\automaticgraphlayout-drawing\src\main\java\Microsoft\Msagl\Drawing\Graph.java`
- **行号**: 199
- **错误信息**: `[ERROR] /D:/agl26/automaticgraphlayout-drawing/src/main/java/Microsoft/Msagl/Drawing/Graph.java:[199,52] 程序包dotnet.system.Collections不存在`
- **代码片段**:
  ```java
        if (node == null || !getNodeMap().containsKey(node.getId())) {
        return;
        }
        var delendi = new dotnet.system.Collections.ArrayList();
        for (Edge e : node.getInEdges()) { delendi.add(e); }
        for (Edge e : node.getOutEdges()) { delendi.add(e); }
        for (Edge e : node.getSelfEdges()) { delendi.add(e); }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Drawing\Graph.cs`
- **根因分类**: 类型映射缺失
- **涉及组件**: `D:\code\cs2j\config\TypeMappings.json`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ObjectCreationTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Context\TypeMappingService.cs`
- **分析**: `System.Collections.ArrayList` 只有 `Count` 方法映射，没有类型映射；对象创建转换时 `context.MapType` 未命中配置，回落到 `System` → `dotnet.system` 命名空间映射，生成了不存在的 `dotnet.system.Collections.ArrayList`。
- **修复验证**: 新增 `NonGenericArrayListCreation_MapsToJavaArrayList` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换后 `Graph.java:199` 生成为 `var delendi = new ArrayList();`，Maven 第一错推进到 `DgmlParser/DgmlParser.java:18` 的 `XDocument` 未解析问题。

## Iteration 24 - XDocument has no Java mapping or runtime bridge
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\dgmlparser\src\main\java\DgmlParser\DgmlParser.java`
- **行号**: 18
- **错误信息**: `[ERROR] /D:/agl26/dgmlparser/src/main/java/DgmlParser/DgmlParser.java:[18,9] 找不到符号  符号:   类 XDocument  位置: 类 DgmlParser.DgmlParser`
- **代码片段**:
  ```java
    public static Graph parse(String filename) {
        XDocument doc = XDocument.load(filename);
        var drawingGraph = new Graph();
        // Parse nodes
        var nodes = StreamSupport.stream(doc.descendants().spliterator(), false).filter(e -> Objects.equals(e.getName().LocalName, "Node")).collect(Collectors.toCollection(() -> new ArrayList<>()));
        for (var nodeElement : nodes) {
        String id = (nodeElement.attribute("Id") != null ? nodeElement.attribute("Id").getValue() : null);
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\tools\DgmlParser\DGMLParser.cs`
- **根因分类**: 类型映射缺失
- **涉及组件**: `D:\code\cs2j\config\TypeMappings.json`, `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat`
- **分析**: `System.Xml.Linq.XDocument`/`XElement`/`XAttribute`/`XName` 没有类型映射，也没有对应的 Java compat runtime 类；转换器保留了 `XDocument.load`、`descendants`、`attribute` 等 LINQ-to-XML API 形状，但生成项目依赖中不存在这些符号。
- **修复验证**: 新增 `XDocumentDescendants_WithNameAndAttribute_MapsToCompatTypes` 红测和 compat `XmlLinqTest` 红测；修复后 `dotnet build`、聚焦测试、全量 `dotnet test` 与 `mvn -f java\csharptojava-compat\pom.xml clean install` 均通过。重新转换后 `DgmlParser.java` 导入 `io.github.ningpp.compat.XDocument`，`dgmlparser` 模块在 Maven 中编译成功，第一错推进到 `msagltests/Constraints/ClusterDef.java:52` 的 `TestContext` 未解析问题。

## Iteration 25 - TestContext type lacks explicit import
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\Constraints\ClusterDef.java`
- **行号**: 52
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/Constraints/ClusterDef.java:[52,20] 找不到符号  符号:   类 TestContext  位置: 类 Microsoft.Msagl.UnitTests.Constraints.ClusterDef`
- **代码片段**:
  ```java
    private double rightResultPos;
    private double topResultPos;
    private double bottomResultPos;
    private boolean resultPosWasSet;
    private static TestContext testContext;
    private double positionX;
    private double positionY;
    private double desiredPosX;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\Infrastructure\Constraints\ClusterDef.cs`
- **根因分类**: 类型映射缺失
- **涉及组件**: `D:\code\cs2j\config\TypeMappings.json`, `D:\code\cs2j\src\CSharpToJava.Core\Context\TypeMappingService.cs`, `D:\code\cs2j\java\csharptojava-compat\src\main\java\Microsoft\VisualStudio\TestTools\UnitTesting\TestContext.java`
- **分析**: compat runtime 已提供 `Microsoft.VisualStudio.TestTools.UnitTesting.TestContext`，但 `TypeMappings.json` 没有 MSTest `TestContext` 的显式类型映射；字段/属性转换通过 `context.MapType` 保留裸 `TestContext`，没有注册对应 Java import。
- **修复验证**: 新增 `MSTestTestContextField_ImportsCompatType` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换并运行 Maven 后，`ClusterDef.java:52` 的 `TestContext` 未解析错误消失，第一错推进到 `TestFileStrings.java:7` 的 `dotnet.system.Text.RegularExpressions` 包缺失问题。

## Iteration 26 - RegexOptions import uses phantom dotnet.system package
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\Constraints\TestFileStrings.java`
- **行号**: 7
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/Constraints/TestFileStrings.java:[7,45] 程序包dotnet.system.Text.RegularExpressions不存在`
- **代码片段**:
  ```java
  import java.util.function.*;
  import java.util.stream.*;
  import java.io.*;
  import dotnet.system.Text.RegularExpressions.RegexOptions;
  import io.github.ningpp.compat.Regex;
  import Microsoft.Msagl.UnitTests.*;
  import Microsoft.Msagl.UnitTests.DelaunayTriangulation.*;
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\Infrastructure\Constraints\TestFileStrings.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Utilities\ExpressionTransformerHelpers.cs`, `D:\code\cs2j\config\TypeMappings.json`
- **分析**: `RegexOptions` 已在 `TypeMappings.json` 中映射到 `io.github.ningpp.compat.RegexOptions`，但 flags enum 静态成员 receiver 的保留 enum 类型路径绕过显式类型映射，直接按 `System` catch-all namespace mapping 合成 `dotnet.system.Text.RegularExpressions.RegexOptions`。
- **修复验证**: 新增 `ProjectRegexOptionsStaticMember_UsesCompatImportOnly` 红测；修复后 `dotnet build`、聚焦测试与全量 `dotnet test` 均通过。重新转换并运行 Maven 后，`TestFileStrings.java:7` 与 `RectFileStrings.java:7` 的 `dotnet.system.Text.RegularExpressions` 包缺失错误消失，第一错推进到 `MsaglTestBase.java:148` 的数组 `aggregate` 调用问题。

## Iteration 27 - LINQ Aggregate left as array instance method ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\MsaglTestBase.java`
- **行号**: 148
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/MsaglTestBase.java:[148,36] 找不到符号  符号:   方法 aggregate(java.lang.String,PathHelper::combine)  位置: 类型为java.lang.String[]的变量 relativePathSegments`
- **代码片段**:
  ```java
        if (StringHelper.isNullOrEmpty(baseDirectory)) {
        baseDirectory = java.nio.file.Paths.get((getTestContext() != null ? getTestContext().getTestRunDirectory() : AppContext.getBaseDirectory()), "Out").toString();
        }
        return relativePathSegments.aggregate(baseDirectory, PathHelper::combine);
    }
    String resolveTestFilePath(String filePath) {
        if (PathHelper.isPathRooted(filePath) || FileHelper.exists(filePath)) {
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\Infrastructure\MsaglTestBase.cs`
- **根因分类**: Lowering
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\LinqRewrite\LinqRewriter.cs`, `D:\code\cs2j\src\CSharpToJava.Core\LinqRewrite\LinqRewriter.Rules.cs`
- **分析**: `relativePathSegments.Aggregate(baseDirectory, Path.Combine)` 的第二个参数是方法组而不是 lambda；LINQ lowering 的 `AggregateWithSeed` 路径既没有把该终端视为可无 lambda 重写，也在规则中强制转换为 `AnonymousFunctionExpressionSyntax`，因此跳过 rewrite，普通 invocation fallback 将扩展方法错误输出为 `String[]` 实例调用。
- **修复验证**: 新增 `AggregateWithSeed_GetMethodFullNameUsesSeedOverloadWhenSyntaxHasSeedArgument` 红测，确认语义退化时两个参数的 `Aggregate` 曾被错判为 no-seed overload；修复后该聚焦测试、`FullyQualifiedName~AggregateWithSeed`、`dotnet build` 与全量 `dotnet test` 均通过。重新转换并运行 Maven 后，`relativePathSegments.aggregate(baseDirectory, PathHelper::combine)` 消失，第一错推进为 `MsaglTestBase.java:148` 的 `Object` 到 `String` 类型不兼容。

## Iteration 28 - Aggregate helper returns Object
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\MsaglTestBase.java`
- **行号**: 148
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/MsaglTestBase.java:[148,49] 不兼容的类型: java.lang.Object无法转换为java.lang.String`
- **代码片段**:
  ```java
        if (StringHelper.isNullOrEmpty(baseDirectory)) {
        baseDirectory = java.nio.file.Paths.get((getTestContext() != null ? getTestContext().getTestRunDirectory() : AppContext.getBaseDirectory()), "Out").toString();
        }
        return getDeploymentPath_ProceduralLinq1(relativePathSegments, baseDirectory, relativePathSegments, baseDirectory);
    }
    String resolveTestFilePath(String filePath) {
        if (PathHelper.isPathRooted(filePath) || FileHelper.exists(filePath)) {
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\Infrastructure\MsaglTestBase.cs`
- **根因分类**: 语义丢失
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\LinqRewrite\LinqRewriter.cs`, `D:\code\cs2j\src\CSharpToJava.Core\LinqRewrite\LinqRewriter.Rules.cs`
- **分析**: `baseDirectory` 来自 `TestContext?.DeploymentDirectory`，在缺少 MSTest 编译引用时 Roslyn 把该 `var` seed/capture 退化为 `object`；LINQ helper 直接采用退化语义类型，生成 `Object getDeploymentPath_ProceduralLinq1(... Object _seed)`，但 C# 方法返回和 `Path.Combine` 聚合结果实际是 `string`。
- **修复验证**: 新增 `ProjectAggregateWithSeed_MSTestConditionalAccessSeedKeepsStringHelperType` 红测，确认 unresolved/error Aggregate 返回类型曾生成 `Object` helper；修复后 `dotnet build`、该聚焦测试和全量 `dotnet test` 均通过。重新转换并运行 Maven 后，`MsaglTestBase.java:148` 的 helper 返回值类型错误消失，第一错推进为 `MsaglTestBase.java:156` 的 `Object` 到 `String` 类型不兼容。

## Iteration 29 - Implicit string array lowered as Object array ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\MsaglTestBase.java`
- **行号**: 156
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/MsaglTestBase.java:[156,50] 不兼容的类型: java.lang.Object无法转换为java.lang.String`
- **代码片段**:
  ```java
        if (PathHelper.isPathRooted(filePath) || FileHelper.exists(filePath)) {
        return filePath;
        }
        var baseDirectories = new Object[] { (getTestContext() != null ? getTestContext().getDeploymentDirectory() : null), (StringHelper.isNullOrEmpty((getTestContext() != null ? getTestContext().getTestRunDirectory() : null)) ? null : java.nio.file.Paths.get(getTestContext().getTestRunDirectory(), "Out").toString()), AppContext.getBaseDirectory() };
        for (var baseDirectory : resolveTestFilePath_ProceduralLinq1(baseDirectories, baseDirectories)) {
        var directPath = java.nio.file.Paths.get(baseDirectory, filePath).toString();
        if (FileHelper.exists(directPath)) {
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\Infrastructure\MsaglTestBase.cs`
- **根因分类**: 语义丢失
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ObjectCreationTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\LinqRewrite\LinqRewriter.cs`
- **分析**: `new[] { TestContext?.DeploymentDirectory, ..., AppContext.BaseDirectory }` 的元素实际都是 `string`/`null`，但 MSTest 未解析时语义模型把隐式数组元素类型退化为 `object`；数组创建和后续 `Where` helper 都沿用 `Object[]`/`List<Object>`，导致 `Path.Combine` 映射的 `Paths.get(Object, String)` 无法匹配 Java 的 `String` 参数。
- **修复验证**: 新增 `ProjectImplicitStringArray_WithUnresolvedConditionalAccessFirstElement_StaysStringArray` 红测，确认隐式字符串数组和根 `Where` helper 曾生成 `Object[]`/`List<Object>`；修复后 `dotnet build`、该聚焦测试和全量 `dotnet test` 均通过。重新转换并运行 Maven 后，`MsaglTestBase.java:156` 的 `Object` 到 `String` 类型错误消失，第一错推进为 `ResultVerifierBase.java:256` 的 `java.time.Duration` 到 `CSharpTimeSpan` 类型不兼容。

## Iteration 30 - Stopwatch elapsed returns Duration ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\Constraints\ResultVerifierBase.java`
- **行号**: 256
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/Constraints/ResultVerifierBase.java:[256,42] 不兼容的类型: java.time.Duration无法转换为io.github.ningpp.compat.CSharpTimeSpan`
- **代码片段**:
  ```java
        }
        }
        }
        CSharpTimeSpan ts = sw.getElapsed();
        writeLine("  Elapsed time: {0:00}:{1:00}:{2:00}.{3:000}", ts.getHours(), ts.getMinutes(), ts.getSeconds(), ts.getMilliseconds());
        if (hasResultDiff) {
        writeLine("  {0} X diff(s), {1} Y diff(s)", diffsX, diffsY);
        if (diffsX > 0) {
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\Infrastructure\Constraints\ResultVerifierBase.cs`
- **根因分类**: 类型映射缺失
- **涉及组件**: `D:\code\cs2j\config\TypeMappings.json`, `D:\code\cs2j\java\csharptojava-compat\src\main\java\io\github\ningpp\compat\StopwatchHelper.java`
- **分析**: 转换器把 `System.TimeSpan` 映射为 `CSharpTimeSpan`，但 `System.Diagnostics.Stopwatch.Elapsed` 对应的兼容运行时 `StopwatchHelper.getElapsed()` 仍返回 `java.time.Duration`，导致 `TimeSpan ts = sw.Elapsed` 生成的 `CSharpTimeSpan ts = sw.getElapsed()` 与 helper 返回类型不兼容。
- **修复验证**: 新增 `StopwatchElapsed_AssignedToTimeSpan_CompilesAgainstCompatRuntime` 红测，确认 `StopwatchHelper.getElapsed()` 的 `Duration` 返回类型无法赋给生成代码中的 `CSharpTimeSpan`；修复后 `dotnet build`、该聚焦测试、全量 `dotnet test`、兼容运行时 `mvn test install` 均通过。重新转换并运行 Maven 后，`ResultVerifierBase.java:256` 的 `Duration` 到 `CSharpTimeSpan` 类型错误消失，第一错推进为 `EdgeLabelPlacementTest.java:41` 的 `int[]` 到 `Iterable<?>` 类型不兼容。

## Iteration 31 - CollectionAssert primitive array expected ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\EdgeLabelPlacementTest.java`
- **行号**: 41
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/EdgeLabelPlacementTest.java:[41,35] 不兼容的类型: int[]无法转换为java.lang.Iterable<?>`
- **代码片段**:
  ```java
    @Timeout(120)
public void expandingSearchTest_IncreasingOnly() {
        int[] expected = new int[] { 0, 1, 2, 3, 4 };
        var r = StreamSupport.stream(EdgeLabelPlacement.expandingSearch(0, 0, 5).spliterator(), false).collect(Collectors.toCollection(() -> new ArrayList<>()));
        CollectionAssert.areEqual(expected, r);
    }
        /**
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\EdgeLabelPlacementTest.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\InvocationExpressionTransformer.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Utilities\ExpressionTransformerHelpers.cs`
- **分析**: `CollectionAssert.AreEqual(expected, r)` 被直接映射为兼容运行时的 `CollectionAssert.areEqual(expected, r)`，但 Java 兼容方法接收 `Iterable<?>`，而转换器没有像 `IEnumerable<T>` 参数和 `AddRange` 那样把 C# 数组参数包装成 Java collection，导致 primitive `int[]` 无法传给 `Iterable<?>`。
- **修复验证**: 新增 `CollectionAssertAreEqual_PrimitiveArrayExpected_WrapsArrayForIterable` 红测，确认 primitive array 参数曾直接输出为 `CollectionAssert.areEqual(expected, actual)`；修复后 `dotnet build`、该聚焦测试与全量 `dotnet test` 均通过。重新转换并运行 Maven 后，`EdgeLabelPlacementTest.java:41` 的 `int[]` 到 `Iterable<?>` 错误消失，第一错推进为 `SplineRouterTests.java:324` 的 `Object` 到 `String` 类型不兼容。

## Iteration 32 - Conditional string local inferred as Object
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\SplineRouterTests.java`
- **行号**: 324
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/SplineRouterTests.java:[324,40] 不兼容的类型: java.lang.Object无法转换为java.lang.String`
- **代码片段**:
  ```java
        router.run();
    }
    String getGeomGraphFileName(String graphName) {
        Object dirName = ((null != this.getTestContext()) ? this.getTestContext().getDeploymentDirectory() : System.getProperty("java.io.tmpdir"));
        return java.nio.file.Paths.get(dirName, graphName).toString();
    }
        @Test
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\SplineRouterTests.cs`
- **根因分类**: 语义丢失
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Statement\StatementTransformer.Declarations.cs`, `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ControlFlowTransformer.cs`
- **分析**: `var dirName = TestContext != null ? TestContext.DeploymentDirectory : Path.GetTempPath()` 两个分支实际都是 `string`，但 MSTest 引用缺失时 Roslyn 将条件表达式/local 退化为 `object`；转换器把该退化类型写成 `Object dirName`，随后 `Path.Combine`/`Paths.get` 需要 `String` 参数而编译失败。
- **修复验证**: 新增 `ProjectConditionalStringLocal_WithMSTestPropertyAndPathFallback_StaysString` 红测，确认退化条件表达式曾生成 `Object dirName`；修复后 `dotnet build`、该聚焦测试与全量 `dotnet test` 均通过。重新转换并运行 Maven 后，`SplineRouterTests.java:324` 的 `Object` 到 `String` 错误消失，第一错推进为 `Validate.java:59` 的 `boolean` 和 `int` 不可比较。

## Iteration 33 - Logical not on degraded bool invocation
- **状态**: ✅ Fixed
- **Java 文件**: `D:\agl26\msagltests\src\test\java\Microsoft\Msagl\UnitTests\Validate.java`
- **行号**: 59
- **错误信息**: `[ERROR] /D:/agl26/msagltests/src/test/java/Microsoft/Msagl/UnitTests/Validate.java:[59,45] 不可比较的类型: boolean和int`
- **代码片段**:
  ```java
    public static <T> void areEqual(T expected, T actual, String message) {
        try {
            Assert.areEqual(expected, actual, message);
        } catch (UnitTestAssertException ex) {
            if ((raiseInteractiveAssert(ex) == 0)) {
            throw ex;
            }
        }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\MSAGLTests\Infrastructure\Validate.cs`
- **根因分类**: Transformer
- **涉及组件**: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\UnaryExpressionTransformer.cs`
- **分析**: `if (!RaiseInteractiveAssert(ex))` 中的 `RaiseInteractiveAssert` 返回 `bool`，但该类还有 `RaiseInteractiveAssert(string)` 重载且 `UnitTestAssertException` 来自缺失 MSTest 元数据，导致语义解析无法可靠给出调用返回类型；`UnaryExpressionTransformer` 的降级逻辑只对显式 boolean 语义或少量名称前缀保留 `!`，于是把 boolean 调用误当数值表达式输出为 `raiseInteractiveAssert(ex) == 0`。
- **修复验证**: 新增 `ProjectLogicalNot_OnOverloadedBoolMethodWithDegradedCatchType_KeepsExclamation` 红测，确认项目转换路径曾把 `!RaiseInteractiveAssert(ex)` 输出为 `raiseInteractiveAssert(ex) == 0`；修复后 `dotnet build`、该聚焦测试与全量 `dotnet test` 均通过。重新转换并运行 `mvn clean test-compile -e` 后，`Validate.java:59` 的 `boolean` 和 `int` 不可比较错误消失，Maven 输出 `BUILD SUCCESS` 且退出码为 0。
