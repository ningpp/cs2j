# Valhalla 核心 JEP 详解

---

## JEP 401: Value Classes and Objects (Preview)

**状态**: Submitted (更新于 2026/06/17)
**Owner**: Dan Smith
**Issue**: [JDK-8251554](https://bugs.openjdk.org/browse/JDK-8251554)

### 摘要
增强 Java 平台，引入 **value objects**: 只有 `final` 字段且缺乏对象身份的类实例。这是一个预览特性。

### 目标
1. 让开发者选择哪些对象需要身份，哪些不需要
2. 支持现有类的兼容迁移
3. 最大化 JVM 在内存布局、局部性和 GC 效率方面的优化自由度

### 核心概念

#### Value Class 声明
```java
value class ArrayCursor<T> {
    T[] array;
    int offset;

    public ArrayCursor(T[] array, int offset) {
        this.array = array;
        this.offset = offset;
    }
    // ...
}
```

#### 关键行为差异
- `==` 比较值而非身份
- 不可变 (字段隐式 final)
- 可自由复制 (无身份)
- 同步操作会抛出 `IllegalMonitorStateException`

#### JDK 中迁移为 Value Class 的类型
- `java.lang`: `Integer`, `Long`, `Float`, `Double`, `Byte`, `Short`, `Character`, `Boolean`
- `java.util`: `Optional`, `OptionalInt`, `OptionalLong`, `OptionalDouble`
- `java.time`: `LocalDate`, `LocalTime`, `LocalDateTime`, `ZonedDateTime`, `Duration`

#### Value Records
```java
value record NameAndScore(String name, int score) { }
```

---

## JEP 402: Enhanced Primitive Boxing (Preview)

**状态**: Draft
**Owner**: Dan Smith
**Issue**: [JDK-8259731](https://bugs.openjdk.org/browse/JDK-8259731)

### 摘要
使用装箱来支持将原始类型更像引用类型使用的语言增强。

### 目标
1. 允许原始值作为字段访问、方法调用的接收者
2. 允许原始返回类型覆盖引用返回类型的方法
3. 支持原始类型作为类型参数
4. 支持原始类型和引用类型数组之间的转换

### 关键特性

#### 成员访问
```java
int i = 12;
int iSize = i.SIZE;
double iAsDouble = i.doubleValue();
Supplier<String> iSupp = i::toString;
```

#### 原始类型参数
```java
interface Foo<T> {
    T* get();           // Foo<char> returns char
    T! getNonNull();    // Foo<char> returns char
    T? getOrNull();     // Foo<char> returns Character?
}

// 使用
List<int> list = ...;  // 未来支持
```

#### 协变数组
```java
int[] ints = new int[]{ 1, 2, 3 };
Object[] objs = ints;
assert objs[2] instanceof Integer;
```

### 依赖
- 依赖 JEP 401 (Value Classes and Objects)
- 依赖 Null-Restricted Value Class Types
- 未来依赖 JEP 218 (Specialized Generics) 进行性能优化

---

## JEP 218: Generics over Primitive Types

**状态**: Candidate
**Owner**: Brian Goetz
**Issue**: [JDK-8046267](https://bugs.openjdk.org/browse/JDK-8046267)

### 摘要
扩展泛型类型以支持原始类型的特化。

### 核心问题
- 当前泛型只能实例化引用类型
- `List<int>` 不可能，必须用 `List<Integer>`
- 装箱开销: 更多内存、更多间接引用、更多 GC

### 设计权衡
- **C++ 模式**: 每个实例化创建一个特化类 (高特异性，大代码体积)
- **Java 当前**: 一个类用于所有引用实例化 (高复用，不支持原始类型)
- **C# 模式**: 字节码统一，引用类型共享原生代码，每个结构类型特化

### 开放问题
1. 子类型关系: `List<int>` 和 `List` 的关系?
2. 类型表示: 特化类型在字节码中如何表示?
3. 反射: 特化类如何通过反射查看?
4. Null: 原始类型没有 `null`，如何处理 `Map.get` 的返回值?

---

## JEP draft: Null-Restricted Value Class Types (Preview)

**状态**: Draft (更新于 2026/05/28)
**Owner**: Dan Smith
**Issue**: [JDK-8316779](https://bugs.openjdk.org/browse/JDK-8316779)

### 核心概念

#### Null-Restricted 类型
- 使用 value class 名称表示的引用类型
- 不能存储 `null`
- 默认值是"零实例"而非 `null`

#### 用途
- 变量声明
- 数组分配
- 类型参数

#### 与 Primitive Classes 的关系
- Null-restricted 类型是 primitive class 的引用类型伴侣
- 允许扁平化存储 (flattened storage)
- 需要 `implicit` 构造函数

---

## Valhalla 的 UnsignedShort 示例 (来自设计文档)

State of Valhalla Part 2 中明确给出了无符号类型的示例:

```java
primitive UnsignedShort extends Number
                        implements Comparable<UnsignedShort> {
   // ...
}
```

这意味着:
- `UnsignedShort` 是一个 primitive class
- 它继承自 `Number`
- 它实现 `Comparable<UnsignedShort>`
- `UnsignedShort.ref <: Number` (引用类型是 Number 的子类型)
- `UnsignedShort` 原始类型不能为 null
- 数组 `UnsignedShort[]` 可以赋值给 `Object[]`

---

## 参考链接

- [JEP 401 完整文本](https://openjdk.org/jeps/401)
- [JEP 402 完整文本](https://openjdk.org/jeps/402)
- [JEP 218 完整文本](https://openjdk.org/jeps/218)
- [JEP 8316779 (Null-Restricted)](https://openjdk.org/jeps/8316779)
