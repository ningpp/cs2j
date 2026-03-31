# 错误07: List.remove(int) 返回类型不兼容 (List Remove Return Type)

## 受影响文件
- `NodeCollection.java:[164,13]`
- `LgNodeCollection.java:[103,17]`
- `SimpleNodeCollection.java:[103,17]`

## 错误信息
```
NodeCollection中的remove(int)无法实现java.util.List中的remove(int)
  返回类型void与Node不兼容
```

## 原始 C# 代码
```csharp
// NodeCollection.cs — 实现 IList<Node>
public void RemoveAt(int index) {
    var node = nodes[index];
    DetouchNode(node);
    nodes.RemoveAt(index);
}
```

C# 的 `IList<T>.RemoveAt(int)` 返回 `void`。

## 生成的 Java 代码
```java
// NodeCollection.java — 实现 java.util.List<Node>
public void remove(int index) {
    var node = nodes.get(index);
    detouchNode(node);
    nodes.remove(index);
}
```

## 错误原因分析

### 根本原因：C# `IList<T>.RemoveAt(int)` 返回 void，Java `List<T>.remove(int)` 返回 T

| C# IList<T>                | Java List<T>             |
|----------------------------|--------------------------|
| `void RemoveAt(int index)` | `T remove(int index)`    |
| 不返回被删除的元素            | 返回被删除的元素            |

转换器将 C# 的 `RemoveAt` 重命名为 `remove`，但保留了 `void` 返回类型。由于 `NodeCollection implements List<Node>`，Java 要求 `remove(int)` 必须返回 `Node`。

### 转换器缺陷
转换器在映射 `IList<T>.RemoveAt` 到 `List<T>.remove` 时，没有同步修改返回类型。需要：
1. 将返回类型从 `void` 改为 `Node`
2. 在方法体末尾返回被删除的元素

### 正确的 Java 代码
```java
public Node remove(int index) {
    var node = nodes.get(index);
    detouchNode(node);
    nodes.remove(index);
    return node;
}
```
