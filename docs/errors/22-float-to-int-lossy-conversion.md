# 错误22: float/double 到 int 有损转换 (Lossy Conversion from float to int)

## 受影响文件
- `FakeBitmap.java:[133,85]` — `Math.signum` 返回 float/double，传给 int 参数
- `FakeBitmap.java:[203,85]` — 同上

## 错误信息
```
不兼容的类型: 从 double 到 int 可能有损
```

## 原始 C# 代码
```csharp
// FakeBitmap.cs
void ScanVertLine(int x, ref PixelPoint pixelInside, PixelPoint a, 
                  PixelPoint b, PixelPoint perp, int halfInterval) {
    ScanVertLineUp(x, pixelInside, a, perp, halfInterval);
    ScanVertLineDown(x, pixelInside, a, perp, halfInterval);
    // Math.Sign 返回 int (-1, 0, 1)
    UpdatePixelInsideForXCase(ref pixelInside, a, b, perp, halfInterval, 
                              Math.Sign(b.Y - a.Y));
}
```

C# 的 `Math.Sign(int)` 返回 `int`（-1、0 或 1）。

## 生成的 Java 代码
```java
// FakeBitmap.java
void scanVertLine(int x, ObjectHolder<PixelPoint> pixelInside, PixelPoint a, 
                  PixelPoint b, PixelPoint perp, int halfInterval) {
    scanVertLineUp(x, pixelInside.value, a, perp, halfInterval);
    scanVertLineDown(x, pixelInside.value, a, perp, halfInterval);
    // Math.signum 返回 double，但参数期望 int
    updatePixelInsideForXCase(pixelInside, a, b, perp, halfInterval, 
                              Math.signum(b.Y - a.Y));  // ← double 到 int 有损转换
}
```

## 错误原因分析

### 根本原因：`Math.Sign` 与 `Math.signum` 返回类型不同

| 方法 | 输入 | 返回类型 |
|------|------|---------|
| C# `Math.Sign(int)` | `int` | `int` |
| C# `Math.Sign(double)` | `double` | `int` |
| Java `Math.signum(double)` | `double` | `double` |
| Java `Math.signum(float)` | `float` | `float` |
| Java `Integer.signum(int)` | `int` | `int` |

C# 的 `Math.Sign` **总是返回 int**（无论输入类型），而 Java 的 `Math.signum` **返回与输入相同的浮点类型**。

### 转换器缺陷
转换器将 C# 的 `Math.Sign` 直接映射为 Java 的 `Math.signum`，没有考虑：
1. 当输入为 `int` 时，应使用 `Integer.signum(int)`（返回 int）
2. 当结果赋值给 `int` 变量时，需要添加显式 `(int)` 转换

### 正确的 Java 代码
```java
// 方案1: 使用 Integer.signum（输入为 int 时）
updatePixelInsideForXCase(pixelInside, a, b, perp, halfInterval, 
                          Integer.signum(b.Y - a.Y));

// 方案2: 添加显式强制转换
updatePixelInsideForXCase(pixelInside, a, b, perp, halfInterval, 
                          (int) Math.signum(b.Y - a.Y));
```
