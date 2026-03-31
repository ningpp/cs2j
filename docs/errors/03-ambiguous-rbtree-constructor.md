# 错误03: 构造器引用不明确 — RbTree (Ambiguous Constructor - RbTree)

## 受影响文件
- `OverlapRemovalCluster.java:[935,39]`
- `LgPathRouter.java:[208,55]`
- `CdtSweeper.java:[80,37]`
- `CdtFront.java:[59,29]`
- `LinkedPointSplitter.java:[67,32]`
- `FreeSpaceFinder.java:[111,30]`（此处还涉及双重 Comparator 继承问题）

## 错误信息
```
对RbTree的引用不明确
  RbTree(java.util.function.BiFunction<T,T,java.lang.Integer>) 和
  RbTree(java.util.Comparator<T>) 都匹配
```

## 原始 C# 代码
```csharp
// C# 中 RbTree 接受一个委托
// RbTree<T>(Comparison<T> comparison) 
// Comparison<T> 签名: int Comparison(T x, T y)

RbTree<OverlapRemovalNode> nodeTree = 
    new RbTree<OverlapRemovalNode>((lhs, rhs) => lhs.CompareTo(rhs));
```

C# 的 `Comparison<T>` 委托签名为 `int (T, T)`，在 Java 中这既可以匹配 `BiFunction<T, T, Integer>` 也可以匹配 `Comparator<T>`。

## 生成的 Java 代码
```java
// RbTree 有两个构造器:
// RbTree(BiFunction<T, T, Integer> comparison)
// RbTree(Comparator<T> comparator)

RbTree<OverlapRemovalNode> nodeTree = 
    new RbTree<OverlapRemovalNode>((lhs, rhs) -> lhs.compareTo(rhs));
```

## 错误原因分析

### 根本原因：Lambda 同时匹配 BiFunction 和 Comparator

Java 中 `(lhs, rhs) -> lhs.compareTo(rhs)` 这个 lambda 表达式同时匹配：
- `Comparator<T>`: 函数式接口 `int compare(T, T)`
- `BiFunction<T, T, Integer>`: 函数式接口 `Integer apply(T, T)` 

两个构造器重载都是有效的目标类型，Java 编译器无法决定使用哪个，导致歧义。

### 转换器缺陷
转换器在生成 `RbTree` 的 Java wrapper 类时，提供了两个构造器重载。当 C# 代码传入 lambda 时，转换器应该：
1. 将 lambda 显式转换为目标类型，如 `(Comparator<T>) (lhs, rhs) -> ...`
2. 或者只保留一个构造器重载

### 正确的 Java 代码
```java
RbTree<OverlapRemovalNode> nodeTree = 
    new RbTree<OverlapRemovalNode>((Comparator<OverlapRemovalNode>) (lhs, rhs) -> lhs.compareTo(rhs));
```
