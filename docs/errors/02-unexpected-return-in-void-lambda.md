# 错误02: Lambda 中意外的返回值 (Unexpected Return Value in Lambda)

## 受影响文件
- `OverlapRemovalCluster.java:[341,39]`

## 错误信息
```
不兼容的类型: 意外的返回值
```

## 原始 C# 代码
```csharp
// OverlapRemovalCluster.cs
public void Generate(Solver solver, OverlapRemovalParameters parameters, bool isHorizontal) {
    ProcessClusterHierarchy(this, cluster => {
        cluster.IsInSolver = cluster.GenerateWorker(solver, parameters, isHorizontal);
    });
    ProcessClusterHierarchy(this, cluster => cluster.SqueezeNonFixedBorderPositions());
}
```

其中 `ProcessClusterHierarchy` 接受一个 `Action<OverlapRemovalCluster>` (即 void 返回的委托)。
`cluster.IsInSolver = cluster.GenerateWorker(...)` 是一个赋值语句，在 C# 中赋值表达式也有值但作为语句使用时返回 void。

## 生成的 Java 代码
```java
public void generate(Solver solver, OverlapRemovalParameters parameters, boolean isHorizontal) {
    processClusterHierarchy(this, cluster -> {
        var _chainVal6 = cluster.generateWorker(solver, parameters, isHorizontal);
        cluster.setIsInSolver(_chainVal6);
        return _chainVal6;    // ← 错误：Consumer<T> lambda 不应有返回值
    });
    processClusterHierarchy(this, cluster -> cluster.squeezeNonFixedBorderPositions());
}
```

## 错误原因分析

### 根本原因：属性赋值链式转换误加了 return 语句

转换器在处理 C# 赋值表达式 `cluster.IsInSolver = cluster.GenerateWorker(...)` 时，使用了"链式赋值"模式：先保存返回值到临时变量 `_chainVal6`，调用 setter，然后 **return 该值**。

但此处 lambda 的目标接口是 `Consumer<T>`（对应 C# 的 `Action<T>`），其 `accept()` 方法返回 `void`。在 void 上下文中添加 `return _chainVal6` 导致编译错误。

### 转换器缺陷
转换器在生成属性赋值的"链式值"代码时，没有检查当前上下文是否要求 void 返回。当赋值出现在 `Action<T>` lambda 或 void 方法中时，不应产生 `return` 语句。

### 正确的 Java 代码
```java
processClusterHierarchy(this, cluster -> {
    var _chainVal6 = cluster.generateWorker(solver, parameters, isHorizontal);
    cluster.setIsInSolver(_chainVal6);
    // 不应有 return 语句
});
```
