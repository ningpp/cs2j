# 错误09: memberwiseClone() 方法不存在 (MemberwiseClone Not Mapped)

## 受影响文件
- `SugiyamaLayoutSettings.java:[299,25]`
- `LgLayoutSettings.java:[156,42]`
- `MdsLayoutSettings.java:[141,25]`
- `FastIncrementalLayoutSettings.java:[378,25]`
- `RankingLayoutSettings.java:[117,24]`

## 错误信息
```
找不到符号
  符号:   方法 memberwiseClone()
  位置: 类 Microsoft.Msagl.Layout.Layered.SugiyamaLayoutSettings
```

## 原始 C# 代码
```csharp
// SugiyamaLayoutSettings.cs
public override LayoutAlgorithmSettings Clone() {
    return MemberwiseClone() as LayoutAlgorithmSettings;
}
```

`MemberwiseClone()` 是 C# `System.Object` 的 `protected` 方法，执行浅拷贝（复制所有字段）。

## 生成的 Java 代码
```java
// SugiyamaLayoutSettings.java
public LayoutAlgorithmSettings clone() {
    var _asExpr11 = memberwiseClone();
    return (_asExpr11 instanceof LayoutAlgorithmSettings 
        ? (LayoutAlgorithmSettings)(_asExpr11) : null);
}
```

## 错误原因分析

### 根本原因：Java 没有等价的 MemberwiseClone 方法

C# 的 `MemberwiseClone()` 定义在 `System.Object` 中，Java 没有内建的等价方法。Java 的 `Object.clone()` 功能相似但需要实现 `Cloneable` 接口。

转换器将 `MemberwiseClone()` 直接音译为 `memberwiseClone()`，而该方法在 Java 类上并不存在。

### 转换器缺陷
转换器没有将 `MemberwiseClone()` 映射到 Java 的正确实现。应该：
1. 让类实现 `Cloneable` 接口
2. 调用 `super.clone()` 来实现浅拷贝
3. 或映射到一个 compat 库辅助方法

### 正确的 Java 代码
```java
// 方案1: 使用 Object.clone()
// 类需要添加 implements Cloneable
public LayoutAlgorithmSettings clone() {
    try {
        return (LayoutAlgorithmSettings) super.clone();
    } catch (CloneNotSupportedException e) {
        throw new RuntimeException(e);
    }
}

// 方案2: 通过 compat 库
public LayoutAlgorithmSettings clone() {
    return (LayoutAlgorithmSettings) ObjectHelper.memberwiseClone(this);
}
```
