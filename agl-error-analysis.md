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
