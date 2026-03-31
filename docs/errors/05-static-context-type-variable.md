# 错误05: 静态上下文中引用非静态类型变量 (Type Variable in Static Context)

## 受影响文件
- `BasicGraphOnEdges.java:[129,14/35/66]`

## 错误信息
```
无法从静态上下文中引用非静态 类型变量 TEdge
```

## 原始 C# 代码
```csharp
// BasicGraphOnEdges.cs
internal class BasicGraphOnEdges<TEdge> where TEdge : IEdge {
    // 静态方法使用了类级别的泛型参数 TEdge
    internal static int VertexCount(IEnumerable edges) {
        int nov = 0;
        foreach (TEdge ie in edges) {  // ← 使用了 TEdge
            if (ie.Source >= nov) nov = ie.Source + 1;
            if (ie.Target >= nov) nov = ie.Target + 1;
        }
        return nov;
    }
}
```

C# 允许在静态方法中使用外层类的泛型参数 `TEdge`，因为 C# 的泛型是通过类型擦除后运行时 reification 实现的。

## 生成的 Java 代码
```java
public class BasicGraphOnEdges<TEdge extends IEdge> {
    public static int vertexCount(Iterable edges) {
        int nov = 0;
        for (TEdge ie : (Iterable<TEdge>)(Iterable<?>)((Iterable<TEdge>) (edges))) {
            // ↑ 错误：静态方法不能使用类级别的泛型参数 TEdge
            if (ie.getSource() >= nov) nov = ie.getSource() + 1;
            if (ie.getTarget() >= nov) nov = ie.getTarget() + 1;
        }
        return nov;
    }
}
```

## 错误原因分析

### 根本原因：Java 静态方法不能访问类级别的泛型参数

在 Java 中，类的泛型参数 `<TEdge>` 属于实例级别，静态方法无法访问。C# 允许这样做是因为其泛型在本质上不同（运行时会为每个类型参数产生不同的特化）。

### 转换器缺陷
转换器将 C# 的静态方法直接翻译为 Java 的静态方法，但没有检测到方法体中引用了类级别的泛型参数。正确做法是：
1. 将类级别的 `TEdge` 转为方法级别的泛型参数 `<TEdge>`
2. 或者使用通配符/原始类型

### 正确的 Java 代码
```java
// 方案1: 添加方法级泛型参数
public static <TEdge extends IEdge> int vertexCount(Iterable<TEdge> edges) {
    int nov = 0;
    for (TEdge ie : edges) {
        if (ie.getSource() >= nov) nov = ie.getSource() + 1;
        if (ie.getTarget() >= nov) nov = ie.getTarget() + 1;
    }
    return nov;
}

// 方案2: 使用 IEdge 作为边界类型
public static int vertexCount(Iterable edges) {
    int nov = 0;
    for (Object obj : edges) {
        IEdge ie = (IEdge) obj;
        if (ie.getSource() >= nov) nov = ie.getSource() + 1;
        if (ie.getTarget() >= nov) nov = ie.getTarget() + 1;
    }
    return nov;
}
```
