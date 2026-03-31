# 错误12: Holder 类型不匹配 (Holder Type Mismatch)

## 受影响文件
- `ConstrainedOrdering.java:[143,78]` — `Anchor[]` 无法转为 `ObjectHolder<Anchor[]>`
- `ConstrainedOrdering.java:[455,50]` — `Integer` 无法转为 `IntHolder`
- `XCoordsWithAlignment.java:[217,40]` — `double` 无法转为 `DoubleHolder`
- `ObstaclePortEntrance.java:[93,157]` — `PointAndCrossingsList` 无法转为 `ObjectHolder<...>`
- `RouteSimplifier.java:[133,80]` — `_veHolder1` 未声明
- `SteinerCdt.java:[284,13]` — `_tHolder1` 未声明
- `PathFixer.java:[283,243]` — `_tHolder1` 未声明

## 错误信息
```
不兼容的类型: Anchor[]无法转换为ObjectHolder<Anchor[]>
不兼容的类型: Integer无法转换为IntHolder  
不兼容的类型: double无法转换为DoubleHolder
找不到符号: 变量 _veHolder1
```

## 原始 C# 代码
```csharp
// ConstrainedOrdering.cs — out 参数
LayeredLayoutEngine.CalculateAnchorSizes(database, 
    out database.anchors, // out Anchor[] 参数
    ProperLayeredGraph, originalGraph, intGraph, settings);

// ConstrainedOrdering.cs — out 参数在 TryGetValue 中
static bool TryGetBlockRoot(int v, out int blockRoot, LayerInfo layerInfo) {
    if (layerInfo.nodeToBlockRoot.TryGetValue(v, out blockRoot)) {
        // blockRoot 已被 TryGetValue 赋值
        return true;
    }
    ...
}

// XCoordsWithAlignment.cs — ref 参数
void SetXCoord(int v, ref double site, ...) {
    site = ...; // 修改 ref 参数
}
```

## 生成的 Java 代码
```java
// ConstrainedOrdering.java
LayeredLayoutEngine.calculateAnchorSizes(database, 
    /* out */ database.anchors,  // ← 错误：应该是 ObjectHolder<Anchor[]>
    ProperLayeredGraph, originalGraph, intGraph, settings);

// ConstrainedOrdering.java — TryGetValue 中的 out
static boolean tryGetBlockRoot(int v, IntHolder blockRoot, LayerInfo layerInfo) {
    if (layerInfo.nodeToBlockRoot.containsKey(v)) {
        blockRoot = layerInfo.nodeToBlockRoot.get(v);  // ← 错误：Integer 赋给 IntHolder
        return true;
    }
    ...
}

// RouteSimplifier.java — Holder 变量未声明
// 原始 C# 用了 out 参数但转换器没有生成 Holder 声明
rail.getStartEnd(_veHolder1, _tHolder1); // ← _veHolder1 未声明
```

## 错误原因分析

### 根本原因：C# ref/out 参数的 Holder 包装不完整

C# 的 `ref`/`out` 参数在 Java 中通过 `IntHolder`/`DoubleHolder`/`ObjectHolder<T>` 模拟。转换器存在几种缺陷：

1. **Holder 创建不一致**：有时在调用处传入原始值而非 Holder 对象
2. **Holder 赋值错误**：`blockRoot = value` 应写为 `blockRoot.value = value`
3. **Holder 变量声明遗漏**：转换器没有为某些 `out` 参数生成对应的 Holder 局部变量

### 转换器缺陷
转换器的 ref/out 处理流程不够健壮：
- 在调用处，必须先创建 `new IntHolder()` / `new ObjectHolder<>()`
- 在被调方法内，必须通过 `.value` 赋值和读取
- 每个使用 `out` 参数的调用点都必须生成 Holder 声明和后续赋值

### 正确的 Java 代码
```java
// 调用处
ObjectHolder<Anchor[]> _anchorsHolder = new ObjectHolder<>();
LayeredLayoutEngine.calculateAnchorSizes(database, 
    _anchorsHolder, ProperLayeredGraph, originalGraph, intGraph, settings);
database.anchors = _anchorsHolder.value;

// TryGetValue 中
blockRoot.value = layerInfo.nodeToBlockRoot.get(v);  // 注意用 .value

// 缺失的 Holder 声明
ObjectHolder<VisibilityEdge> _veHolder1 = new ObjectHolder<>();
ObjectHolder<Point> _tHolder1 = new ObjectHolder<>();
rail.getStartEnd(_veHolder1, _tHolder1);
```
