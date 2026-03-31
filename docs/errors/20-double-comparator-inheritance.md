# 错误20: 双重 Comparator 继承（类型擦除冲突）

## 受影响文件
- `FreeSpaceFinder.java:[94,8]` — 类同时实现了两个不同类型参数的 Comparator

## 错误信息
```
FreeSpaceFinder 无法使用不同的参数同时继承 java.util.Comparator:
  Comparator<AxisEdgesContainer> 和 Comparator<SweepEvent>
```

## 原始 C# 代码
```csharp
// FreeSpaceFinder.cs
internal class FreeSpaceFinder : LineSweeperBase, IComparer<AxisEdgesContainer> {
    // ...
}

// LineSweeperBase.cs (父类)
internal abstract class LineSweeperBase : IComparer<SweepEvent> {
    // ...
    public int Compare(SweepEvent x, SweepEvent y) { ... }
}
```

C# 允许一个类同时实现多个 `IComparer<T>` 接口（不同的 T），因为 .NET 泛型在运行时保留类型信息（reified generics）。

## 生成的 Java 代码
```java
// FreeSpaceFinder.java
public class FreeSpaceFinder extends LineSweeperBase 
        implements Comparator<AxisEdgesContainer> {  // ← 编译错误
    // ...
}

// LineSweeperBase.java (父类)
public abstract class LineSweeperBase implements Comparator<SweepEvent> {
    // ...
    public int compare(SweepEvent x, SweepEvent y) { ... }
}
```

## 错误原因分析

### 根本原因：Java 泛型类型擦除限制

Java 使用类型擦除（type erasure）实现泛型。在编译后，`Comparator<AxisEdgesContainer>` 和 `Comparator<SweepEvent>` 都变为原始类型 `Comparator`，这导致同一个类不能同时实现两个不同类型参数的同一泛型接口。

这是 C# 与 Java 泛型系统的根本差异：
- **C#**: 运行时保留泛型类型参数（reified generics），允许多重实现
- **Java**: 编译时擦除泛型类型参数（type erasure），禁止多重实现

### 转换器缺陷
转换器直接将 `IComparer<T>` 映射为 `Comparator<T>`，没有检测到父类已经实现了同一接口的不同参数化版本，也没有采取规避策略。

### 正确的 Java 代码
```java
// 方案1: 提取独立 Comparator 为内部类或字段
public class FreeSpaceFinder extends LineSweeperBase {
    
    private final Comparator<AxisEdgesContainer> axisEdgesContainerComparator = 
        (a, b) -> { /* 比较逻辑 */ };
    
    // 需要 Comparator<AxisEdgesContainer> 的地方使用字段
    // edgeContainersTree = new RbTree<>(axisEdgesContainerComparator);
}

// 方案2: 使用适配器模式
public class FreeSpaceFinder extends LineSweeperBase {
    
    Comparator<AxisEdgesContainer> asAxisEdgesComparator() {
        return (a, b) -> this.compareAxisEdgesContainers(a, b);
    }
}
```

### C# 原始用法
在 C# 中，`FreeSpaceFinder` 将自身 `this` 作为 `IComparer<AxisEdgesContainer>` 传给 `RbTree` 构造函数。Java 中需要改为传递额外的 Comparator 实例。
