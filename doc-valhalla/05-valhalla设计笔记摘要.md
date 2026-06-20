# State of Valhalla 设计笔记摘要

> 来源: https://openjdk.org/projects/valhalla/design-notes/state-of-valhalla/
> 作者: Brian Goetz (Part 1-2), John Rose & Brian Goetz (Part 3)
> 日期: December 2021

---

## Part 1: The Road to Valhalla (背景)

### 核心问题

#### 1. 间接引用的成本
- 1990年代: 内存访问成本与算术运算相当
- 现代CPU: 一次缓存未命中可能消耗1000个算术指令周期
- JVM 的指针密集型布局 (小数据岛之间有大量间接引用) 不再适合现代硬件

**示例: Point 数组的内存布局**
```
当前 (有身份):
+--------------+
| Point[5]     |
+--------------+
| 87fa1a09  -----> +-------+
| 87fa1a09  -----> | Point |  (y=1996, m=1, d=23)
| 87fb4ad2  ---> +-------+    +-------+
| 00000000      | Point |    | Point |
| 87fb5366  --  +-------+    +-------+
+--------------+  | y=2026  | y=1996
                  | m=1     | m=1
                  | d=23    | d=23
                  +-------+  +-------+

期望 (无身份):
+----------+
| Point[5] |
+----------+
| x1  y1   |  (直接存储值，无指针)
| x2  y2   |
| x3  y3   |
| x4  y4   |
| x5  y5   |
+----------+
```

#### 2. 分裂类型系统的成本
- 原始类型不是对象
- 泛型只能用于引用类型
- Lambda 表达式继承了泛型的限制
- `java.util.function` 需要手写特化版本 (IntPredicate, IntToLongFunction 等)

#### 3. 装箱的成本
- 堆分配
- 间接引用
- 身份开销
- `int[]` 不是 `Integer[]`

### 根本原因: 对象身份

**身份** 要求:
- 每个对象实例有唯一标识
- 可变性需要身份 (区分"哪个"实例)
- 布局多态性 (子类共享超类布局前缀)

**身份的代价**:
- 对象必须在堆上分配
- 必须通过指针引用
- JVM 必须悲观地保留这种布局

---

## Part 2: The Language Model (语言模型)

### 当前原始类型与对象的差异

| 原始类型 | 对象 |
|---------|------|
| 无身份 (纯值) | 有身份 |
| `==` 比较值 | `==` 比较身份 |
| 内置 | 类中声明 |
| 不可空 | 可空 |
| 无成员 | 有成员 (字段、方法、构造函数) |
| 无超类型 | 类和接口继承 |
| 直接访问 | 通过对象引用访问 |
| 默认值是零 | 默认值是 `null` |
| 数组是单态的 | 数组是协变的 |
| 竞争下可撕裂 | 初始化安全保证 |
| 可转换为多态对象 | 多态 |

### Value Classes: 分离引用与身份

```java
value class ArrayCursor<T> {
    T[] array;
    int offset;

    public ArrayCursor(T[] array, int offset) {
        this.array = array;
        this.offset = offset;
    }

    public boolean hasNext() {
        return offset < array.length;
    }

    public T next() {
        return array[offset];
    }

    public ArrayCursor<T> advance() {
        return new ArrayCursor(array, offset+1);
    }
}
```

**Value Class 特性**:
- 实例无身份
- 字段隐式 final
- 仍是引用类型 (可空，默认 `null`)
- `==` 比较字段值
- 可自由复制

**性能优化示例**:
```java
for (ArrayCursor<T> c = Arrays.cursor(array);
     c.hasNext();
     c = c.advance()) {
    // use c.next();
}
// 预期: 不实际分配任何 cursor
// 字段被提升到寄存器，构造函数调用编译为寄存器递增
```

### Value Records
```java
value record NameAndScore(String name, int score) { }
```

### Primitive Classes: 用户定义的原始类型

```java
primitive class Point implements Serializable {
    int x;
    int y;

    Point(int x, int y) {
        this.x = x;
        this.y = y;
    }

    Point scale(int s) {
        return new Point(s*x, s*y);
    }
}
```

**Primitive Class 特性**:
- 不能为 `null`
- 默认值是零实例
- 可被撕裂 (在数据竞争下)
- 声明两种类型: 原始类型 (`Point`) 和引用类型 (`Point.ref`)
- 可继承和实现接口

**多态性**:
```java
primitive UnsignedShort extends Number
                        implements Comparable<UnsignedShort> {
   // ...
}
// UnsignedShort.ref <: Number
// UnsignedShort.ref <: Comparable<UnsignedShort>

UnsignedShort us = ...;
Number n = us;  // 自动转换为 UnsignedShort.ref
if (n instanceof UnsignedShort) {
    assert n.getClass() == UnsignedShort.class;
}
```

**数组协变**:
```java
// P[] <: P.ref[] <: Object[]
Point[] points = new Point[10];
Object[] objects = points;  // 合法
```

**默认值问题**:
- 原始类型的默认值是零实例
- 对于某些抽象 (如 `LocalDate`)，零不是合理的默认值
- 这些类型更适合 value class 而非 primitive class

**撕裂问题**:
- 原始类型在竞争下可被撕裂 (类似 `long`/`double`)
- 这是性能与正确性的权衡
- `volatile` 可以恢复原子性，但有性能影响

### 为什么需要引用类型?

1. **与其他引用类型互操作**: 原始类可以实现接口
2. **可空性**: `Map.get` 需要返回 `null`
3. **自引用类型**: 链表的 `next` 字段
4. **紧凑数组**: 某些算法更适合引用数组
5. **防止撕裂**: 引用类型是原子的
6. **与现有装箱一致**: 方法重载选择

```java
// Map.get 的签名变化
public V.ref get(K key);  // 返回引用类型，可为 null
```

### 原始类型与对象的统一

| 特性 | 原始类型 | 对象 |
|------|---------|------|
| 不可空；默认值是零 | ✓ | |
| 可空；默认值是 `null` | | ✓ |

差异大大减小。

---

## Part 3: The JVM Model (JVM 模型)

### Value Classes 在 JVM 层面

- 使用 `ACC_VALUE` 标志标记
- Value objects 可以自由复制和重新编码
- JVM 有更多灵活性来优化布局

### 关键 JVM 概念

1. **Null-free types**: 不包含 null 的类型
2. **Flat fields**: 扁平字段 (值直接存储，无指针)
3. **Scalarization**: 标量化 (将对象分解为标量值)
4. **Calling convention flattening**: 调用约定扁平化

### 与现有原始类型的统一

```java
// 将 int 声明为 primitive class
primitive class int {
    // 特殊关键字作为名称
    // 但行为类似标准 primitive class
}

// Integer 成为 int.ref 的别名
// Integer.ref == int.ref
```

---

## 关键设计决策总结

1. **身份成为可选**: 类可以选择是否有身份
2. **值语义**: `==` 对 value objects 比较值
3. **渐进迁移**: 现有代码不需要修改
4. **库而非语言特性**: 无符号类型等通过库定义
5. **两种类型**: 每个 primitive class 有原始类型和引用类型
6. **零默认值**: 原始类型的默认值是零实例
7. **允许撕裂**: 原始类型在竞争下可被撕裂

---

## 参考链接

- [Part 1: The Road to Valhalla](https://openjdk.org/projects/valhalla/design-notes/state-of-valhalla/01-background)
- [Part 2: The Language Model](https://openjdk.org/projects/valhalla/design-notes/state-of-valhalla/02-object-model)
- [Part 3: The JVM Model](https://openjdk.org/projects/valhalla/design-notes/state-of-valhalla/03-vm-model)
