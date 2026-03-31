# 错误04: 未实现 Iterator.next() 抽象方法 (Iterator Interface Not Implemented)

## 受影响文件
- `PolylineIterator.java:[57,8]`

## 错误信息
```
Microsoft.Msagl.Core.Geometry.Curves.PolylineIterator不是抽象的, 
并且未覆盖java.util.Iterator中的抽象方法next()
```

## 原始 C# 代码
```csharp
// PolylineIterator.cs
internal class PolylineIterator : IEnumerator<Point>
{
    Point IEnumerator<Point>.Current {
        get { return currentPolyPoint.Point; }
    }
    
    bool System.Collections.IEnumerator.MoveNext() {
        if (currentPolyPoint == null) {
            currentPolyPoint = polyline.StartPoint;
            return currentPolyPoint != null;
        }
        if (currentPolyPoint == polyline.EndPoint)
            return false;
        currentPolyPoint = currentPolyPoint.Next;
        return true;
    }
    
    void System.Collections.IEnumerator.Reset() {
        currentPolyPoint = null;
    }
    
    void IDisposable.Dispose() {
        GC.SuppressFinalize(this);
    }
}
```

C# 的 `IEnumerator<T>` 模式：先调用 `MoveNext()` 判断是否有下一个元素，然后通过 `Current` 属性获取值。

## 生成的 Java 代码
```java
public class PolylineIterator implements Iterator<Point> {
    public Point getCurrent() {
        return currentPolyPoint.getPoint();
    }
    public void close() { /* GC operation not needed in Java */ }
    public boolean hasNext() {
        // MoveNext 逻辑被放到了 hasNext
        if (currentPolyPoint == null) {
            currentPolyPoint = polyline.getStartPoint();
            return currentPolyPoint != null;
        }
        if (currentPolyPoint == polyline.getEndPoint()) return false;
        currentPolyPoint = currentPolyPoint.getNext();
        return true;
    }
    // 缺少 next() 方法！
}
```

## 错误原因分析

### 根本原因：IEnumerator → Iterator 映射不完整

C# 的 `IEnumerator<T>` 和 Java 的 `Iterator<T>` 有本质区别：

| C# IEnumerator<T>      | Java Iterator<T>    |
|------------------------|---------------------|
| `bool MoveNext()`      | `boolean hasNext()` |
| `T Current { get; }`   | `T next()`          |
| 先 MoveNext 再读 Current | next() 同时推进和返回值   |

转换器将 `MoveNext()` 映射为 `hasNext()`，将 `Current` 映射为 `getCurrent()`，但 Java `Iterator` 要求实现的是 `next()` 方法，而不是 `getCurrent()`。

### 转换器缺陷
转换器需要理解 C# 的 `MoveNext()/Current` 模式与 Java `hasNext()/next()` 模式的语义差异，生成一个合并了状态管理的正确实现。应该：
1. `hasNext()` 不移动游标，仅判断是否还有元素
2. `next()` 移动游标并返回当前元素

### 正确的 Java 代码
```java
public class PolylineIterator implements Iterator<Point> {
    private boolean hasAdvanced = false;
    private boolean hasMore = false;
    
    public boolean hasNext() {
        if (!hasAdvanced) {
            advance();
        }
        return hasMore;
    }
    
    public Point next() {
        if (!hasAdvanced) {
            advance();
        }
        if (!hasMore) throw new java.util.NoSuchElementException();
        hasAdvanced = false;
        return currentPolyPoint.getPoint();
    }
    
    private void advance() {
        if (currentPolyPoint == null) {
            currentPolyPoint = polyline.getStartPoint();
        } else {
            currentPolyPoint = currentPolyPoint.getNext();
        }
        hasMore = currentPolyPoint != null && currentPolyPoint != polyline.getEndPoint().getNext();
        hasAdvanced = true;
    }
}
```
