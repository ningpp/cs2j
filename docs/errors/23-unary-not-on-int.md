# 错误23: 对 int 使用一元 ! 运算符 (Unary ! on int)

## 受影响文件
- `LgInteractor.java:[2820,13]` — `!intersected.length > 0`
- `OverlapRemovalFixedSegmentsMst.java:[579,13]` — `!vOverlaps.length > 0`

## 错误信息
```
一元运算符 '!' 的操作数类型错误
  找到: int
  需要: boolean
```

## 原始 C# 代码
```csharp
// LgInteractor.cs
LgNodeInfo FindClosestNodeInfoForMouseClickOnLevel(Point mouseDownPositionInGraph, int iLevel) {
    var level = _lgData.Levels[iLevel];
    var intersected = level.NodeInfoTree.GetAllIntersecting(rect);
    // Any() 返回 bool，! 取反
    if (!intersected.Any()) {
        return null;
    }
    ...
}

// OverlapRemovalFixedSegmentsMst.cs
void SaveCurrentOverlapsSegments() {
    var vOverlaps = _segmentTree.GetAllIntersecting(v.rect);
    // Any() 返回 bool
    if (!vOverlaps.Any()) {
        continue;
    }
    ...
}
```

C# 的 `IEnumerable<T>.Any()` 返回 `bool`，`!` 运算符对 `bool` 类型合法。

## 生成的 Java 代码
```java
// LgInteractor.java
LgNodeInfo findClosestNodeInfoForMouseClickOnLevel(Point mouseDownPositionInGraph, int iLevel) {
    var level = _lgData.getLevels().get(iLevel);
    var intersected = level.getNodeInfoTree().getAllIntersecting(rect);
    // 转换器将 Any() 转为 .length > 0，但保留了 ! 运算符
    if (!intersected.length > 0) {  // ← ! 作用于 intersected.length (int)
        return null;
    }
}

// OverlapRemovalFixedSegmentsMst.java
void saveCurrentOverlapsSegments() {
    var vOverlaps = _segmentTree.getAllIntersecting(v.rect);
    if (!vOverlaps.length > 0) {  // ← ! 作用于 vOverlaps.length (int)
        continue;
    }
}
```

## 错误原因分析

### 根本原因：`Any()` 到 `.length > 0` 的转换不完整

转换器将 C# 的 `Any()` 翻译为 `.length > 0`，这在逻辑上是正确的等价表达。但当原始代码使用 `!collection.Any()` 时（任何求反情况），转换器生成了：

```java
!collection.length > 0
```

这在 Java 中解析为 `(!collection.length) > 0`，即先对 `int` 类型的 `length` 应用 `!` 运算符，这在 Java 中不合法（`!` 只能用于 `boolean`）。

### 转换器缺陷
1. 将 `!collection.Any()` 机械替换为 `!collection.length > 0`
2. 没有处理运算符优先级——需要用括号包裹或改用 `==` 比较

### 正确的 Java 代码
```java
// 方案1: 使用 == 0
if (intersected.length == 0) {
    return null;
}

// 方案2: 加括号
if (!(intersected.length > 0)) {
    return null;
}
```
