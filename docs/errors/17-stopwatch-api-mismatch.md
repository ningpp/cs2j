# 错误17: Stopwatch API 映射不正确 (Stopwatch API Mismatch)

## 受影响文件
- `Timer.java:[62,31]` — `StopwatchHelper.Frequency` 找不到
- `Timer.java:[70,36]` — `StopwatchHelper.getTimestamp()` 找不到
- `Timer.java:[78,35]` — `StopwatchHelper.getTimestamp()` 找不到

## 错误信息
```
找不到符号
  符号: 变量 Frequency
  位置: 类 io.github.ningpp.compat.StopwatchHelper

找不到符号
  符号: 方法 getTimestamp()
  位置: 类 io.github.ningpp.compat.StopwatchHelper
```

## 原始 C# 代码
```csharp
// Timer.cs
using System.Diagnostics;

public class Timer {
    private readonly long freq;
    
    public Timer() {
        freq = Stopwatch.Frequency;      // 静态属性：每秒 tick 数
    }
    
    public long Start() {
        startTime = Stopwatch.GetTimestamp();  // 静态方法：当前时间 tick
        return startTime;
    }
    
    public long Stop() {
        stopTime = Stopwatch.GetTimestamp();
        return stopTime;
    }
}
```

`System.Diagnostics.Stopwatch` 提供高精度计时：
- `Stopwatch.Frequency`: 静态属性，每秒的 tick 数
- `Stopwatch.GetTimestamp()`: 静态方法，当前高精度时间戳

## 生成的 Java 代码
```java
// Timer.java
public Timer() {
    freq = StopwatchHelper.Frequency;        // ← 字段不存在
}

public long start() {
    startTime = StopwatchHelper.getTimestamp();  // ← 方法不存在
    return startTime;
}
```

## 错误原因分析

### 根本原因：compat 库的 StopwatchHelper 缺少需要的 API

转换器将 `System.Diagnostics.Stopwatch` 映射为 compat 库中的 `StopwatchHelper`，但 compat 库没有提供对应的静态字段 `Frequency` 和静态方法 `getTimestamp()`。

`StopwatchHelper` 可能只提供了实例级别的计时方法（`start()`/`stop()`），没有暴露底层的 tick 频率和时间戳。

### 转换器缺陷
这是 compat 库 API 不完整的问题。需要在 `StopwatchHelper` 中添加：
1. `public static final long Frequency` — 对应 Java 中为纳秒精度，值为 `1_000_000_000L`
2. `public static long getTimestamp()` — 使用 `System.nanoTime()`

### 正确的 Java 代码
```java
// 方案1: 直接使用 System.nanoTime()
public Timer() {
    freq = 1_000_000_000L; // 纳秒频率
}
public long start() {
    startTime = System.nanoTime();
    return startTime;
}

// 方案2: 在 StopwatchHelper 中补充静态 API
public class StopwatchHelper {
    public static final long Frequency = 1_000_000_000L;
    public static long getTimestamp() { return System.nanoTime(); }
}
```
