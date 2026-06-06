# TryFormat 数值类型支持 — 设计文档

**日期**: 2026-06-06 | **状态**: 已确认

---

## 背景

.NET 5+ 所有数值类型实现 `ISpanFormattable.TryFormat`，cs2j 当前不支持此调用。

## 目标

支持以下类型 `.TryFormat()` → `MathHelper.tryFormatXxx()` 转换：

`byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `object`

### 示例

```
C#:
  byte a = 255;
  Span<char> buf = stackalloc char[10];
  a.TryFormat(buf, out int w, "X2", null);

Java:
  byte a = (byte)255;
  Character[] _bufArr = new Character[10];
  Span<Character> buf = new Span<>(_bufArr);
  IntHolder _wH = new IntHolder();
  MathHelper.tryFormatByte(a, buf, _wH, "X2");
  int w = _wH.value;
```

## 不覆盖

- `DateTime`, `TimeSpan`, `Guid`, `BigInteger`, `Enum` — 暂不处理
- ref struct 生命周期限制 — 不模拟
- `IFormatProvider?` — 统一忽略

---

## Component 1: `Span<T>.java` (新增)

轻量数组切片容器，纯数据载体，不模拟 C# ref struct 生命周期。

```java
public final class Span<T> {
    private final T[] array;
    private final int offset;
    private final int length;

    public Span(T[] array)                          // 整数组
    public Span(T[] array, int offset, int length)  // 切片
    public int length()
    public T get(int i)
    public void set(int i, T value)
    public Span<T> slice(int start)                 // 同底层数组
    public Span<T> slice(int start, int length)     // 同底层数组
    public T[] toArray()                            // 拷贝
}
```

## Component 2: `MathHelper.java` (扩展)

新增 13 个静态方法，统一模式：`tryFormat<Type>(value, Span<Character> dest, IntHolder charsWritten, String format) → boolean`。

实现：format 为 null 走 `toString()`，否则走 `String.format(format, value)`，写入 dest 不超过长度，返回是否完整写入。

关键映射：
- `uint` / `ulong` → Java 用 `int`/`long` 传值，内部 `toUnsignedString`
- `decimal` → Java `BigDecimal`
- `object` → `String.valueOf`

## Component 3: `InvocationExpressionTransformer.cs` (修改)

在 `TransformMemberInvocation()` 中新增分支：
1. 识别方法名 `TryFormat`
2. 判断 receiver 类型为上述数值原语
3. 重写调用为 `MathHelper.tryFormatXxx(receiver, destSpan, charsWrittenHolder, formatStr)`
4. 忽略末尾 `IFormatProvider?` 参数
5. `ReadOnlySpan<char> format` → `String`

`out` 参数的三种模式（`out var` / `out existing` / `out _`）均复用现有 `ArgumentTransformer`，无需改动。

---

## 文件清单

| 文件 | 动作 |
|---|---|
| `java/.../compat/Span.java` | 新建 |
| `java/.../compat/MathHelper.java` | 扩展 13 个 tryFormat 方法 |
| `.../InvocationExpressionTransformer.cs` | 新增 TryFormat 分支 |
| `tests/.../TryFormatNumericTests.cs` | 新建测试 |

## 测试覆盖

| 场景 | 描述 |
|---|---|
| 基本调用 | `byte.TryFormat(buf, out w, null, null)` |
| 格式字符串 | `int.TryFormat(buf, out w, "X8", null)` |
| 浮点 | `double.TryFormat(buf, out w, "F2", null)` |
| 变量复用 | 非 `out var` 声明的已有变量作为 out 参数 |
| 返回值判断 | `if (!a.TryFormat(...)) throw ...` |
| 非数值类型 | 不触发 TryFormat 特殊处理 |
