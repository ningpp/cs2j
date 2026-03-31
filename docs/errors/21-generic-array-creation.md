# 错误21: 泛型数组创建 (Generic Array Creation)

## 受影响文件
- `PortManager.java:[335,196]` — 无法创建泛型数组

## 错误信息
```
创建泛型数组
  AbstractMap.SimpleEntry<Point, FreePoint>[]::new
```

## 原始 C# 代码
```csharp
// PortManager.cs - RemoveStaleFreePoints()
private void RemoveStaleFreePoints() {
    if (this.freePointMap.Count > this.freePointLocationsUsedByRouteEdges.Count) {
        // ToArray() 创建 KeyValuePair<Point, FreePoint>[]
        var staleFreePairs = this.freePointMap
            .Where(kvp => !this.freePointLocationsUsedByRouteEdges.Contains(kvp.Key))
            .ToArray();
        foreach (var staleFreePair in staleFreePairs) {
            this.freePointMap.Remove(staleFreePair.Key);
        }
    }
}
```

C# 的 `ToArray()` 可以创建任意类型数组，包括泛型类型 `KeyValuePair<K,V>[]`。

## 生成的 Java 代码
```java
// PortManager.java
private void removeStaleFreePoints() {
    if (this.freePointMap.size() > this.freePointLocationsUsedByRouteEdges.getCount()) {
        var staleFreePairs = this.freePointMap.entrySet().stream()
            .filter(kvp -> !this.freePointLocationsUsedByRouteEdges.contains(kvp.getKey()))
            .toArray(AbstractMap.SimpleEntry<Point, FreePoint>[]::new);  // ← 泛型数组创建错误
        for (AbstractMap.SimpleEntry<Point, FreePoint> staleFreePair : staleFreePairs) {
            this.freePointMap.remove(staleFreePair.getKey());
        }
    }
}
```

## 错误原因分析

### 根本原因：Java 禁止创建参数化类型的数组

Java 不允许 `new T[]` 或 `new SomeType<A, B>[]` 形式的泛型数组创建，因为类型参数在运行时被擦除，无法保证数组的类型安全。

`toArray(AbstractMap.SimpleEntry<Point, FreePoint>[]::new)` 实际上等价于创建一个泛型参数化数组，Java 编译器禁止这种操作。

### 转换器缺陷
转换器在将 LINQ 的 `ToArray()` 转换为 Java Stream 的 `toArray()` 时：
1. 直接使用了参数化类型的数组构造函数引用
2. 没有考虑 Java 泛型数组的限制

### 正确的 Java 代码
```java
// 方案1: 使用 toList() 替代 toArray()
var staleFreePairs = this.freePointMap.entrySet().stream()
    .filter(kvp -> !this.freePointLocationsUsedByRouteEdges.contains(kvp.getKey()))
    .collect(Collectors.toList());
for (var staleFreePair : staleFreePairs) {
    this.freePointMap.remove(staleFreePair.getKey());
}

// 方案2: 使用原始类型数组
var staleFreePairs = this.freePointMap.entrySet().stream()
    .filter(kvp -> !this.freePointLocationsUsedByRouteEdges.contains(kvp.getKey()))
    .toArray(Map.Entry[]::new);

// 方案3: 直接用 removeIf 替代整个逻辑
this.freePointMap.entrySet().removeIf(
    kvp -> !this.freePointLocationsUsedByRouteEdges.contains(kvp.getKey()));
```
