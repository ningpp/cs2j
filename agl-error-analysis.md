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
