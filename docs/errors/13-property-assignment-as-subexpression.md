# 错误13: 属性赋值用作子表达式 (Property Assignment as Subexpression)

## 受影响文件
- `FastIncrementalLayout.java:[567,35]`
- `DeviceIndependendZoomCalculatorForNodes.java:[185,41]` `[223,41]`
- `PhyloTree.java:[66,30]`
- `Centrality.java:[89,14]` `[96,14]`
- `CombinatorialNudger.java:[240,56]`
- `NodePositionsAdjuster.java:[446,70]`

## 错误信息
```
意外的类型
  需要: 变量
  找到:    值
```

## 原始 C# 代码
```csharp
// FastIncrementalLayout.cs
if (settings.Iterations++ == 0) { ... }

// DeviceIndependendZoomCalculatorForNodes.cs
int countForTile = tileTable[tuple]++ + 1;

// Centrality.cs
degree[e.Source]++;
degree[e.Target]++;
```

在 C# 中，属性/索引器可以使用 `++` 运算符，因为属性赋值表达式可以作为左值。`tileTable[tuple]++` 等价于读取值、加一、写回。

## 生成的 Java 代码
```java
// FastIncrementalLayout.java
if (settings.getIterations()++ == 0) { ... }
// ← 错误: getIterations() 返回的是值，不能用 ++

// DeviceIndependendZoomCalculatorForNodes.java
int countForTile = tileTable.get(tuple)++ + 1;
// ← 错误: tileTable.get(tuple) 返回的是值，不是变量

// Centrality.java
degree.get(e.getSource())++;
// ← 错误: get() 返回值不能用 ++ 修改
```

## 错误原因分析

### 根本原因：Java getter 返回值不是左值

C# 中属性的 `getter`/`setter` 可以透明地参与 `++`、`+=` 等复合赋值操作，编译器会自动展开为：
```csharp
// settings.Iterations++ 展开为:
var temp = settings.Iterations;
settings.Iterations = temp + 1;
// 表达式值为 temp (后缀++)
```

Java 的 `getXxx()` 返回一个中间值(rvalue)，不能对其应用 `++`。

### 转换器缺陷
转换器未正确处理属性/索引器上的 `++`、`--`、`+=` 等复合赋值操作。需要：
1. 识别 `property++` 模式
2. 展开为先读后写的形式

### 正确的 Java 代码
```java
// settings.Iterations++ == 0
int _temp = settings.getIterations();
settings.setIterations(_temp + 1);
if (_temp == 0) { ... }

// tileTable[tuple]++ + 1
int _temp2 = tileTable.get(tuple);
tileTable.put(tuple, _temp2 + 1);
int countForTile = _temp2 + 1;

// degree[e.Source]++
degree.set(e.getSource(), degree.get(e.getSource()) + 1);
// 或使用 Map 的 merge:
degree.merge(e.getSource(), 1, Integer::sum);
```
