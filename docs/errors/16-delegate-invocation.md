# 错误16: 委托调用方法未正确映射 (Delegate Invocation)

## 受影响文件
- `Polygon.java:[180,113]` — `ShowDebugCurves.Invoke(...)` 找不到
- `LgLayoutSettings.java:[139,88]` — `_handler.invoke()` 找不到

## 错误信息
```
找不到符号
  符号: 方法 Invoke(DebugCurve, DebugCurve, DebugCurve)
  位置: 接口 ShowDebugCurves

找不到符号
  符号: 方法 invoke()
  位置: 类型为LgLayoutEvent的变量 _handler
```

## 原始 C# 代码
```csharp
// Polygon.cs — 委托直接调用
LayoutAlgorithmSettings.ShowDebugCurves(
    new DebugCurve(100, 0.1, "red", a.Polyline),
    new DebugCurve(100, 0.1, "blue", b.Polyline),
    new DebugCurve(100, 0.1, "black", new LineSegment(p, q)));
// ShowDebugCurves 是 Action<DebugCurve[]> 类型的委托
// C# 中委托调用语法与方法调用相同

// LgLayoutSettings.cs — 事件触发
protected void FireViewerChangeTransformAndInvalidateGraph() {
    foreach (var handler in _listeners) handler();
    // handler 是 Action 类型委托
}
```

## 生成的 Java 代码
```java
// Polygon.java
LayoutAlgorithmSettings.getShowDebugCurves().Invoke(
    new DebugCurve(...), new DebugCurve(...), new DebugCurve(...));
// ← 错误: Java 函数式接口没有 Invoke 方法
// 应该用 accept() (Consumer) 或 apply() (Function)

// LgLayoutSettings.java
for (var _handler : _listeners) _handler.invoke();
// ← 错误: 函数式接口没有 invoke() 方法
// 如果是 Runnable 应该用 run()
```

## 错误原因分析

### 根本原因：C# 委托的隐式调用语法 vs Java 函数式接口的显式方法

C# 中委托类型可以直接使用函数调用语法 `delegate(args)` 或 `delegate.Invoke(args)`。

Java 中函数式接口必须通过特定方法调用：

| Java 函数式接口 | 调用方法 |
|----------------|---------|
| `Consumer<T>` | `.accept(T)` |
| `Function<T,R>` | `.apply(T)` |
| `Runnable` | `.run()` |
| `Supplier<T>` | `.get()` |
| params `Consumer<T[]>` | `.accept(T[])` |

### 转换器缺陷
转换器将 C# 委托调用 `delegate(args)` 转换为 `delegate.Invoke(args)` 或 `delegate.invoke()`，但 Java 函数式接口没有这些方法。需要根据委托的签名确定对应的 Java 函数式接口方法名。

### 正确的 Java 代码
```java
// ShowDebugCurves 映射为 Consumer<DebugCurve[]>
LayoutAlgorithmSettings.getShowDebugCurves().accept(
    new DebugCurve[] {
        new DebugCurve(...), 
        new DebugCurve(...), 
        new DebugCurve(...)
    });

// LgLayoutEvent 映射为 Runnable
for (var _handler : _listeners) _handler.run();
```
