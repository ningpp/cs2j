# 错误01: 缺少返回语句 (Missing Return Statement)

## 受影响文件
- `Set.java:[85,5]`

## 错误信息
```
缺少返回语句
```

## 原始 C# 代码
```csharp
// Set.cs — ICollection<T>.Add 的显式接口实现
void System.Collections.Generic.ICollection<T>.Add(T t) { Insert(t); }
```

C# 中 `ICollection<T>.Add()` 返回 `void`。这是一个显式接口实现，调用者通过 `ICollection<T>` 引用来调用它。

## 生成的 Java 代码
```java
// Set.java
public boolean add(T t) {
    insert(t);
}
```

## 错误原因分析

### 根本原因：`ICollection<T>.Add()` 与 `Collection<T>.add()` 返回类型不匹配

C# 的 `ICollection<T>.Add(T)` 返回 `void`，但 Java 的 `Collection<T>.add(T)` 返回 `boolean`。

转换器在将 `ICollection<T>.Add` 映射为 Java `Collection.add` 时，错误地保留了方法体但改变了返回类型为 `boolean`，却没有在方法体末尾添加 `return` 语句。

### 转换器缺陷
转换器需要识别当 C# void 方法被映射到 Java 非 void 方法时，必须补充合适的 `return` 语句。对于 `Collection.add()`，应该在调用 `insert(t)` 后返回 `true`。

### 正确的 Java 代码
```java
public boolean add(T t) {
    insert(t);
    return true;
}
```
