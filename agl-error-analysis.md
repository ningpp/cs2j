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
