# 错误14: Map.Entry 与 SimpleEntry 类型不兼容 (Map.Entry vs SimpleEntry)

## 受影响文件
- `RecoveryLayeredLayoutEngine.java:[1071,63]` `[1103,63]`
- `NodePositionsAdjuster.java:[499,18]`
- `PathMerger.java:[105,171]`
- `PathRefiner.java:[136,83]`

## 错误信息
```
不兼容的类型: java.util.Map.Entry<K,V>无法转换为
  java.util.AbstractMap.SimpleEntry<K,V>
```

## 原始 C# 代码
```csharp
// RecoveryLayeredLayoutEngine.cs
// C# 的 Dictionary 遍历返回 KeyValuePair<K,V>
foreach (var kv in dictionary) {
    var key = kv.Key;
    var value = kv.Value;
}

// 或 LINQ GroupBy 场景
.GroupBy(pair => pair.Key)
.SelectMany(group => group.Select(item => ...))
```

C# 的 `KeyValuePair<K,V>` 是统一的键值对类型。

## 生成的 Java 代码
```java
// 转换器将 KeyValuePair 映射为 AbstractMap.SimpleEntry
// 但 Map.entrySet() 返回的是 Map.Entry 接口
for (AbstractMap.SimpleEntry<Double, Integer> kv : dictionary.entrySet()) {
    // ← 错误: entrySet() 返回 Set<Map.Entry<K,V>>
    // Map.Entry 不能转换为 SimpleEntry
}
```

## 错误原因分析

### 根本原因：Map.entrySet() 返回 Map.Entry，不是 SimpleEntry

转换器将 C# 的 `KeyValuePair<K,V>` 映射为 `AbstractMap.SimpleEntry<K,V>`，这在构造新键值对时是合理的。但当从 `Map.entrySet()` 遍历时，返回的是 `Map.Entry<K,V>` 接口，不保证实现类是 `SimpleEntry`。

| C# | Java |
|----|------|
| `KeyValuePair<K,V>` (struct) | `Map.Entry<K,V>` (interface) |
| `new KeyValuePair<K,V>(k, v)` | `new SimpleEntry<K,V>(k, v)` |
| foreach over Dictionary | `for (Map.Entry<K,V> e : map.entrySet())` |

### 转换器缺陷
转换器统一将 `KeyValuePair<K,V>` 映射为 `SimpleEntry<K,V>`，但在遍历 Map 时应使用 `Map.Entry<K,V>` 接口。转换器需要区分：
1. **创建**键值对 → `new SimpleEntry<>(k, v)` ✓
2. **接收**来自 Map 的键值对 → `Map.Entry<K,V>` ✓
3. 不能混用这两种类型

### 正确的 Java 代码
```java
// 遍历 Map 时使用 Map.Entry
for (Map.Entry<Double, Integer> kv : dictionary.entrySet()) {
    var key = kv.getKey();
    var value = kv.getValue();
}
```
