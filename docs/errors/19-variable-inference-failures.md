# 错误19: 变量重复定义与局部类型推断失败 (Variable Already Defined & Type Inference Failure)

## 受影响文件
- `Nudger.java:[460,15]` — 变量 `_i` 已被定义
- `Nudger.java:[469,13]` — 无法推断局部变量类型（lambda 表达式）
- `Nudger.java:[747,13]` — 无法推断局部变量类型（方法引用）

## 错误信息
```
变量 _i 已在方法 showLongSegsWithIdealPositions(int) 中定义

无法推断局部变量 projectionToDir 的类型
  (lambda 表达式需要显式目标类型)

无法推断局部变量 projectionToPerp 的类型
  (lambda 表达式需要显式目标类型)
```

## 原始 C# 代码
```csharp
// Nudger.cs - ShowLongSegsWithIdealPositions
void ShowLongSegsWithIdealPositions(Direction dir) {
    var debCurves = GetObstacleBoundaries(Obstacles, "black");
    int i = 0;
    // 直接在 lambda 中使用闭包变量 i++
    debCurves.AddRange(LongestNudgedSegs.Select(
        ls => DebugCurveOfLongSeg(ls, DebugCurve.Colors[i++ % DebugCurve.Colors.Length], dir)));
    ...
}

// Nudger.cs - LineSegOfLongestSeg
static ICurve LineSegOfLongestSeg(LongestNudgedSegment ls, Direction dir) {
    // C# var 可以推断委托类型
    var projectionToDir = dir == Direction.East 
        ? (PointProjection)(p => p.X) 
        : (p => p.Y);
    ...
}

// Nudger.cs - CreateLongestNudgedSegments
void CreateLongestNudgedSegments() {
    // C# var 可以推断方法组转委托
    var projectionToPerp = NudgingDirection == Direction.East 
        ? (PointProjection)FreeSpaceFinder.MinusY 
        : FreeSpaceFinder.X;
    ...
}
```

## 生成的 Java 代码
```java
// Nudger.java - showLongSegsWithIdealPositions
void showLongSegsWithIdealPositions(int dir) {
    var debCurves = getObstacleBoundaries(getObstacles(), "black");
    int i = 0;
    int[] _i = { i };   // ← 第一次声明
    int[] _i = { i };   // ← 重复声明！
    debCurves.addAll(getLongestNudgedSegs().stream()
        .map(ls -> debugCurveOfLongSeg(ls, DebugCurve.Colors[_i[0]++ % DebugCurve.Colors.length], dir))
        .collect(Collectors.toCollection(ArrayList::new)));
}

// Nudger.java - lineSegOfLongestSeg
static ICurve lineSegOfLongestSeg(LongestNudgedSegment ls, int dir) {
    // Java var 无法推断函数式接口类型
    var projectionToDir = (dir == Direction.East 
        ? (PointProjection)((p -> p.X)) 
        : (p -> p.Y));   // ← 错误: 需要显式目标类型
}

// Nudger.java - createLongestNudgedSegments 
void createLongestNudgedSegments() {
    var projectionToPerp = (getNudgingDirection() == Direction.East 
        ? (PointProjection)(FreeSpaceFinder::minusY) 
        : FreeSpaceFinder::x);  // ← 错误: 需要显式目标类型
}
```

## 错误原因分析

### 问题1: `_i` 变量重复声明

转换器将 C# 闭包中捕获的可变变量 `i` 转换为数组 `int[] _i = { i }`（Java 闭包需要 effectively final 变量，数组元素可变）。但转换器错误地生成了**两次**相同的声明。

**根本原因**: 转换器在处理 lambda 闭包变量提升时重复执行了声明逻辑。

### 问题2: `var` 无法推断函数式接口类型

C# 的 `var` 配合显式委托转换 `(PointProjection)(p => p.X)` 可以正确推断类型。但 Java 的 `var` 无法推断 lambda 表达式或方法引用的目标函数式接口类型，因为 lambda/方法引用不是多态表达式（poly expression）在 `var` 上下文中。

**根本原因**: 转换器直接将 C# 的 `var` 翻译为 Java 的 `var`，但 Java 10 的 `var` 不支持用于 lambda 或方法引用赋值。

### 正确的 Java 代码
```java
// 问题1: 只生成一次 _i 声明
int[] _i = { i };

// 问题2: 使用显式类型替代 var
PointProjection projectionToDir = (dir == Direction.East 
    ? (PointProjection)(p -> p.getX()) 
    : (p -> p.getY()));

PointProjection projectionToPerp = (getNudgingDirection() == Direction.East 
    ? (PointProjection)(FreeSpaceFinder::minusY) 
    : FreeSpaceFinder::x);
```
