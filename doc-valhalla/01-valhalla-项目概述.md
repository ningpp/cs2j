# Project Valhalla 项目概述

> 来源: https://openjdk.org/projects/valhalla/
> 设计文档: State of Valhalla (Brian Goetz, December 2021)

---

## 项目起源与目标

**Project Valhalla** 于 2014 年启动，目标是为 JVM 带来更灵活的扁平化数据类型，以恢复编程模型与现代硬件性能特征之间的对齐。

### 核心问题

Java 的类型系统存在根本性的分裂:

1. **原始类型 (primitives)**: `int`, `long`, `double` 等 — 无身份(identity)，高性能，但不能参与泛型
2. **对象类型 (objects)**: 有身份，可泛型化，但有间接引用和装箱开销

这种分裂导致:
- **内存布局问题**: 对象数组是指针数组，导致缓存不友好
- **装箱开销**: `List<Integer>` 需要装箱，浪费内存和 CPU
- **库设计困境**: `IntStream`, `IntPredicate` 等手写特化类型的组合爆炸
- **用户体验**: 开发者经常需要手写 `ArrayList<int>` 的等价物

### Valhalla 的三大核心特性

1. **Value Objects** — 无身份的不可变对象
2. **Primitive Classes** — 用户可定义的新原始类型
3. **Specialized Generics** — 支持原始类型的泛型特化

### 设计口号

> *"Codes like a class, works like an int."*
> （编码像类一样，工作像 int 一样）

---

## 三个交付阶段

### Phase 1: 无身份的 Value Objects
- JEP 401: Value Classes and Objects (Preview)
- 允许类声明放弃身份
- `Integer`, `LocalDate`, `Optional` 等迁移为 value class
- **当前状态: JEP 401 已 Submitted (2026/06/17 更新)**

### Phase 2: Primitive Classes + 迁移现有原始类型 + Universal Generics
- 用户可定义新的原始类型 (如 `Point`, `Complex`, `UnsignedShort`)
- 将 `int`, `long` 等现有原始类型统一为 primitive class
- 允许 `List<int>` 而非 `List<Integer>`
- **当前状态: 设计阶段，尚无确定的 JEP**

### Phase 3: Specialized Generics
- JVM 层面的泛型特化
- 类似 C++ 的模板特化，但保留 Java 的渐进迁移兼容性
- **当前状态: 远期规划**

---

## 与 C# 类型系统的对比

| 特性 | C# | Java (当前) | Java (Valhalla 后) |
|------|-----|------------|-------------------|
| 值类型 | `struct` | 无 | `value class` / `primitive class` |
| 无符号整数 | `byte`, `ushort`, `uint`, `ulong` | 无 | 通过库定义 primitive class |
| 半精度浮点 | `Half` (.NET 7+) | 无 | `value class Float16` (规划中) |
| 泛型值类型 | `List<int>` (特化) | `List<Integer>` (装箱) | `List<int>` (Phase 3) |
| 可空值类型 | `int?` | `Integer` (装箱) | `int.ref` (primitive class 的引用类型) |

---

## 关键设计决策

### 1. 无符号类型: 库而非语言特性

Brian Goetz 在设计文档中明确:
> *"there are infinitely many numeric types we might want to add to Java, but the proper way to do that is as libraries, not as language features"*

这意味着 Java 不会像 C# 那样添加 `uint` 关键字，而是通过 primitive class 机制让用户在库中定义。

### 2. Float16: 作为 Value Class

JEP 469 (Vector API) 提到 Float16 将作为 value object 实现，而非语言内置原始类型。

### 3. 渐进迁移

Valhalla 的设计核心是**向后兼容**:
- 现有代码不需要修改
- `Integer` 和 `int.ref` 最终会统一
- 装箱和拆箱的开销会大幅降低

---

## 参考链接

- [Valhalla 项目主页](https://openjdk.org/projects/valhalla/)
- [State of Valhalla Part 1: Background](https://openjdk.org/projects/valhalla/design-notes/state-of-valhalla/01-background)
- [State of Valhalla Part 2: The Language Model](https://openjdk.org/projects/valhalla/design-notes/state-of-valhalla/02-object-model)
- [State of Valhalla Part 3: The JVM Model](https://openjdk.org/projects/valhalla/design-notes/state-of-valhalla/03-vm-model)
- [JEP 401: Value Classes and Objects](https://openjdk.org/jeps/401)
- [JEP 402: Enhanced Primitive Boxing](https://openjdk.org/jeps/402)
- [JEP 218: Generics over Primitive Types](https://openjdk.org/jeps/218)
