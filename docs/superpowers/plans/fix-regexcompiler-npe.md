# Fix Plan: RegexCompiler.java:76 NPE

## 问题描述

`RegexCompiler` 静态初始化器在第 76 行抛出 `NullPointerException`，导致 `ExceptionInInitializerError`，
后续所有使用 `RegexCompiler` 的测试（约 2444 个）全部抛出 `NoClassDefFoundError`。

## 根因分析

第 76 行代码：
```java
private static final MethodInfo s_memoryMarshalGetReference =
    TypeHelper.getMethod(MemoryMarshal.class, "getReference", new Class[] { ReadOnlySpan.class })
        .makeGenericMethod(Character.class);
```

调用链：
1. `TypeHelper.getMethod(MemoryMarshal.class, "getReference", ReadOnlySpan.class)` 
   → 内部调用 `MemoryMarshal.class.getMethod("getReference", ReadOnlySpan.class)`
   → `MemoryMarshal` 只有 `getReference(Span<T>)`，没有 `getReference(ReadOnlySpan<T>)` 重载
   → 抛出 `NoSuchMethodException`
   → `TypeHelper.getMethod()` 返回 `null`
2. `null.makeGenericMethod(Character.class)` → **NPE**

### 连带问题（同一静态初始化器中）

| 行号 | 反射调用 | 缺失的方法 | 是否 NPE |
|------|---------|-----------|---------|
| 76 | `MemoryMarshal.getReference(ReadOnlySpan)` | `getReference(ReadOnlySpan<T>)` | ✅ NPE |
| 77 | `MemoryExtensions.indexOf(ReadOnlySpan, Object)` | 泛型 `indexOf(ReadOnlySpan<T>, T)` | ✅ NPE |
| 78 | `MemoryExtensions.indexOfAny(ReadOnlySpan, Object, Object)` | `indexOfAny(ReadOnlySpan<T>, T, T)` | ✅ NPE |
| 79 | `MemoryExtensions.indexOfAny(ReadOnlySpan, Object, Object, Object)` | `indexOfAny(ReadOnlySpan<T>, T, T, T)` | ✅ NPE |
| 82 | `MemoryExtensions.startsWith(ReadOnlySpan, ReadOnlySpan)` | `startsWith(ReadOnlySpan<T>, ReadOnlySpan<T>)` | ✅ NPE |

> 注意：lines 68-75, 80-81, 83-87 也有 `getMethod()` 返回 null 的情况（如 `Character.isDigit(Character)` vs `isDigit(char)`），
> 但这些行**不链式调用 `.makeGenericMethod()`**，所以只是将字段设为 null，不会立即 NPE。
> 这些 null 字段可能在后续 `RegexCompiler` 运行时引发其他错误，但不在本次修复范围内。

## 修复方案

在 compat 库中添加缺失的方法重载，使反射查找能成功匹配。

### 修改 1: `MemoryMarshal.java`

添加 `getReference(ReadOnlySpan<T>)` 重载（C# 中 `MemoryMarshal.GetReference<T>` 同时有 `Span<T>` 和 `ReadOnlySpan<T>` 重载）。

```java
public static <T> T getReference(ReadOnlySpan<T> span) {
    return span.length() > 0 ? span.get(0) : null;
}
```

### 修改 2: `MemoryExtensions.java`

添加 4 个泛型方法（类型擦除后签名为 `ReadOnlySpan, Object` 等可被 `getMethod()` 精确匹配）：

1. `<T> int indexOf(ReadOnlySpan<T> span, T value)` — 搜索第一个匹配元素的索引
2. `<T> int indexOfAny(ReadOnlySpan<T> span, T value0, T value1)` — 搜索任一匹配元素
3. `<T> int indexOfAny(ReadOnlySpan<T> span, T value0, T value1, T value2)` — 搜索任一匹配元素
4. `<T> boolean startsWith(ReadOnlySpan<T> span, ReadOnlySpan<T> value)` — 检查前缀匹配

> 这些泛型方法类型擦除后签名为 `indexOf(ReadOnlySpan, Object)` 等，
> 不会与已有的 `indexOf(ReadOnlySpan, Character)` 和 `indexOf(ReadOnlySpan, Integer)` 冲突。
> `Class.getMethod("indexOf", ReadOnlySpan.class, Object.class)` 会精确匹配到泛型版本。

## 验证步骤

1. 修改 compat 库源码
2. 重新构建 compat 库 (`mvn install`)
3. 重新运行 `mvn clean package` 确认 NPE 消失
4. 确认测试错误数从 ~4811 减少（至少减少 ~2444 个 NoClassDefFoundError）
