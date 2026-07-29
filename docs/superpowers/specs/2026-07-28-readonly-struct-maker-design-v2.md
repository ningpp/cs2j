# ReadOnlyStructMaker V2 — C# Struct 转 ReadOnly Struct 预处理器设计

**日期**: 2026-07-28
**版本**: V2（基于 V1 演进）
**范围**: 新模块 `src/CSharpToJava.Core/ReadOnlyStructMaker/` + CLI 动词 `make-readonly` + 项目级预处理集成 + 单元测试
**状态**: 待用户审核
**参考**: V1 方案 `2026-07-26-readonly-struct-maker-design.md`（保留不变）

---

## 0. V2 演进概要

### 0.1 V1 的局限性

V1 方案采用二元判定：struct 要么完全可转换（所有字段 readonly、无 mutating 方法），要么不可转换。分析 MSAGL 代码库中的 19 个 struct 后发现：

| 模式 | 数量 | V1 处理 | V2 改进 |
|------|------|---------|---------|
| 完全不可变（readonly 字段 + 构造函数初始化） | 1 (Parallelogram) | ✅ 可转换 | ✅ 直接转换 (L1) |
| 已 readonly 字段 | 3 (ConstraintDirectionPair, ConstraintListForVariable, QpscVar) | ✅ 跳过 | ✅ 跳过 (L0) |
| 私有 setter（外部只读） | 2 (NeighborAndWeight, PointAndCrossings) | ❌ 不可转换 | ✅ 可转换 (L2) |
| 数据容器（公共字段或 get/set 属性，无 mutating 方法） | 5 (EdgeConstraints, PixelPoint, OverlappedEdge, StackStruct, PortObstacle) | ❌ 不可转换 | ✅ 可转换 (L3) |
| 含 mutating 方法（方法可迁移） | 5 (Rectangle, Size, BorderInfo, CompassVector, Complex) | ❌ 不可转换 | ✅ 方法迁移转换 (L5) |
| 含 mutating 方法（方法不可迁移） | 1 (Point) | ❌ 不可转换 | ❌ 不可转换 (L7) |
| 重度可变状态 | 2 (MatrixCell, ViolationCache) | ❌ 不可转换 | ❌ 不可转换 (L7) |

### 0.2 V2 核心改进

1. **多级转换策略**：不再二元判定，提供 7 种转换级别（L0-L5, L7）
2. **模式识别**：自动识别 6 种常见 struct 模式（A-F）
3. **方法迁移**：将 mutating 方法转换为返回新实例的纯方法，支持嵌套方法链迁移
4. **属性驱动控制**：通过特性精确控制转换行为
5. **属性 setter 迁移**：处理直接修改 backing field 的属性 setter
6. **调用点安全更新**：根据返回类型选择最佳迁移策略（void→返回值、其他→out 参数）

---

## 1. 背景与目标

### 1.1 任务

开发一个 C# 源代码预处理器，将项目中所有符合转换条件的普通 struct 自动转换为 readonly struct。从语言层面约束 struct 的不可变性，启用 JIT 运行时优化，减少不必要的内存复制开销。

### 1.2 项目背景

- 工作目录 `d:\code\cs2j` 是基于 Roslyn 的 C#→Java 转换器（net10.0，Roslyn `Microsoft.CodeAnalysis.CSharp` 4.12.0，xUnit 2.9.3）
- 现有 GotoEliminator 模块已实现 goto/label 预处理功能，CLI 对应 `eliminate-goto` 子命令
- 本功能与 GotoEliminator **完全独立**，但遵循相同的架构模式
- 目标代码库 MSAGL 包含 19 个 struct，覆盖多种典型模式

### 1.3 执行顺序

本功能必须在现有 goto 转状态机预处理步骤**之前**执行：

1. **第一阶段（本功能）**：普通 struct 自动转 readonly struct
2. **第二阶段（已有功能）**：goto 语句转状态机实现

### 1.4 强制约束

1. 输入输出均为标准 C#源码，与现有 C#→Java 流程完全独立
2. 所有转换在 C#语法树层面完成，输出合法的标准 C#代码
3. 幂等性——已不含可转换 struct 的文件输出与输入完全一致
4. 保留所有原有代码格式、注释、Whitespace 信息
5. 与现有 goto 预处理功能完全兼容
6. 语法重写阶段对每棵语法树单次遍历完成；调用图等分析结果缓存复用
7. **V2 新增**：转换后的代码语义等价——mutating 方法改为返回新实例，调用点同步更新

### 1.5 成功标准

- [ ] 所有符合条件的 struct 被成功转换为 readonly struct
- [ ] 不符合条件的 struct 不被修改，并通过诊断接口输出警告
- [ ] 转换后的 C# 代码编译通过
- [ ] 转换后的代码语义等价（mutating 方法调用点正确更新）
- [ ] 与现有 goto 预处理功能正确集成
- [ ] CLI 动词 `make-readonly` 正常工作
- [ ] `convert-project` 的 `--no-make-readonly` 选项正常工作
- [ ] 所有单元测试通过
- [ ] **V2 新增**：MSAGL 19 个 struct 中至少 **13** 个成功转换（3 个 L7 不可转换：Point 因公共字段无法安全 readonly、MatrixCell/ViolationCache 因重度可变状态）

---

## 2. MSAGL Struct 模式分析

### 2.1 模式分类

基于对 MSAGL 代码库 19 个 struct 的分析，识别出以下 6 种模式：

#### Pattern A: 完全不可变（Full Immutable）
```
struct Parallelogram {
    bool isSeg;           // 仅构造函数设置
    Point corner;         // 仅构造函数设置
    // ... 所有字段只在构造函数中赋值
}
```
- **特征**：所有字段在构造函数中初始化，无任何 mutating 方法
- **MSAGL 实例**：Parallelogram
- **转换策略**：直接添加 `readonly` 修饰符

#### Pattern B: 已 readonly 字段（Already Readonly）
```
struct ConstraintDirectionPair {
    internal readonly Constraint Constraint;
    internal readonly bool IsForward;
}
```
- **特征**：所有字段已标记 `readonly`
- **MSAGL 实例**：ConstraintDirectionPair, ConstraintListForVariable, QpscVar
- **转换策略**：直接添加 `readonly` 修饰符

#### Pattern C: 私有 setter（Private Setter）
```
struct NeighborAndWeight {
    internal Variable Neighbor { get; private set; }
    internal double Weight { get; private set; }
}
```
- **特征**：属性使用 `private set`，外部无法修改
- **MSAGL 实例**：NeighborAndWeight, PointAndCrossings
- **转换策略**：将 `private set` 移除，改为 `get` 仅在构造函数中初始化

#### Pattern D: 数据容器（Data Container）
```
struct EdgeConstraints {
    public Direction Direction { get; set; }
    public double Separation { get; set; }
}

struct PixelPoint {
    internal int X;  // 公共字段
    internal int Y;
}
```
- **特征**：仅包含数据字段/属性（公共字段或 get/set 属性），无 mutating 方法，作为数据容器使用
- **MSAGL 实例**：EdgeConstraints, PixelPoint, OverlappedEdge, StackStruct, PortObstacle
- **转换策略**：
  - 有 get/set 属性的 → 改为 get-only 属性 + 构造函数
  - 有公共字段的 → 封装为 get-only 属性 + 构造函数
  - 安全约束：`public` 字段转为属性会破坏二进制兼容性，默认仅对 `internal`/`private` 字段启用 L3

#### Pattern E: 含 mutating 方法 - 可迁移（Mutable Methods - Migratable）
```
struct Rectangle {
    public void Add(Point point) { left = point.X; ... }
    public void Pad(double padding) { Left -= padding; ... }
}
```
- **特征**：包含修改自身状态的方法，但方法可迁移为返回新实例的纯方法
- **MSAGL 实例**：Rectangle, Size, BorderInfo, CompassVector, Complex
- **转换策略**：方法迁移——将 mutating 方法改为返回新实例的纯方法，调用点同步更新

#### Pattern F: 含 mutating 方法 - 不可迁移（Mutable Methods - Non-Migratable）
```
struct Point {
    public double x, y;
    // 若存在 ref this 传递、虚方法重写、或委托引用，则方法不可迁移
}
struct MatrixCell {
    internal double Value;        // 可变状态
    internal readonly uint Column;
    // 内部复杂逻辑，方法迁移不安全
}
struct ViolationCache {
    private Constraint[] constraints;
    private int numConstraints;
    internal void Clear() { ... }
    internal bool FilterBlock(Block b) { ... }
}
```
- **特征**：方法因以下原因不可迁移：
  - `ref this` / `out this` 传递
  - 虚方法重写或多态调用
  - 委托引用
  - 公共 API 签名无法更新
  - 重度可变状态（数组、索引跟踪等）
- **MSAGL 实例**：Point（如有 ref this）、MatrixCell、ViolationCache
- **转换策略**：不可转换，输出警告

### 2.2 模式判定优先级

```
1. 已经是 readonly struct → 跳过 (L0)
2. 是 ref struct → 跳过 (L0)
3. 是 partial struct → 跳过 (L0)
4. 标记 [DoNotMakeReadOnly] → 跳过 (L0)
5. 所有字段 readonly → Pattern A/B → 直接转换 (L1)
6. 所有属性 get-only 或 private set → Pattern C → 属性转换 (L2)
7. 无 mutating 方法，仅数据字段/属性 → Pattern D → 数据容器转换 (L3)
   - 注意：public 字段需显式启用（破坏二进制兼容性）
8. 有 mutating 方法，方法可迁移 → Pattern E → 方法迁移转换 (L5)
9. 有 mutating 方法，方法不可迁移 → Pattern F → 不可转换 (L7)
```

---

## 3. 关键设计决策

| # | 决策 | 选项 | 选择 | 理由 |
|---|------|------|------|------|
| D1 | 字段修改分析深度 | (a) 语法级；(b) 语义级（单方法）；(c) 数据流分析 | **(b) 语义级（单方法）** | 平衡精度与性能，跨方法数据流分析开销过大 |
| D2 | 接口方法分析 | (a) 纳入调用图；(b) 独立检测；(c) 跳过检测 | **(b) 独立检测** | 接口方法若为 mutating，直接判定 Pattern F 不可迁移 |
| D3 | partial struct 处理 | (a) 单文件独立分析；(b) 项目级合并分析；(c) 跳过 | **(c) 跳过** | 避免部分加 readonly 导致编译错误 |
| D4 | 诊断详细程度 | (a) 逐条列出；(b) 仅首个原因；(c) 仅是否可转换 | **(a) 逐条列出** | V2 需要更详细的诊断以支持多种模式 |
| D5 | 分析范围 | (a) 单文件；(b) 项目级编译后分析 | **(b) 项目级编译后分析** | 构建完整跨文件调用图 |
| **D6** | **mutating 方法处理** | **(a) 跳过不转换；(b) 方法迁移；(c) 改为扩展方法** | **(b) 方法迁移** | 保持语义等价，调用点自动更新 |
| **D7** | **数据容器处理** | **(a) 保持原样；(b) 改为构造函数初始化** | **(b) 改为构造函数初始化** | 真正的 readonly 语义 |
| **D8** | **公共字段处理** | **(a) 保持公共字段；(b) 改为属性** | **(b) 改为属性（仅 internal/private）** | readonly struct 不允许可变公共字段；public 字段转属性破坏二进制兼容性 |
| **D9** | **混合 readonly/mutable 处理** | **(a) 整体跳过；(b) 部分转换** | **(a) 整体跳过** | C# 不支持 partial readonly struct，含可变字段则不可转换 |
| **D10** | **非 void/非 struct 返回值处理** | **(a) 返回元组；(b) out 参数；(c) 不可迁移** | **(b) out 参数** | 元组会改变方法签名且破坏调用点简洁性；out 参数保持调用点可读性 |
| **D11** | **已有构造函数处理** | **(a) 始终生成新构造函数；(b) 检查已有签名避免重复** | **(b) 检查已有签名避免重复** | 避免 CS0111 编译错误（重复定义相同签名的构造函数） |
| **D12** | **继承成员命名冲突** | **(a) 忽略；(b) 检测并标记不可转换** | **(b) 检测并标记不可转换** | 字段名 `GetType`/`Equals` 等与 object 方法冲突 |

---

## 4. 转换规则

### 4.1 转换级别（V2 新增）

| 级别 | 名称 | 描述 | 适用模式 |
|------|------|------|----------|
| L0 | 跳过 | 无需转换（已 readonly/ref/partial/标记特性） | - |
| L1 | 直接添加 | 添加 `readonly` 修饰符，无需其他改动 | A, B |
| L2 | 属性转换 | 将 `private set` 改为 get-only | C |
| L3 | 数据容器转换 | 将 get/set 属性/公共字段改为 get-only + 构造函数 | D |
| L5 | 方法迁移 | 将 mutating 方法改为返回新实例的纯方法 | E |
| L7 | 不可转换 | 无法安全转换 | F |

**注意**：L4（字段封装）和 L6（部分转换）已移除：
- L4 合并入 L3（数据容器转换统一处理）
- L6 对 struct 无效——C# 不支持 `readonly struct` 包含可变字段

### 4.2 转换操作详解

#### L1: 直接添加 readonly

```csharp
// 转换前
struct Point { public readonly int X; public readonly int Y; }

// 转换后
readonly struct Point { public readonly int X; public readonly int Y; }
```

#### L2: 私有 setter 转换

```csharp
// 转换前
struct NeighborAndWeight {
    internal Variable Neighbor { get; private set; }
    internal double Weight { get; private set; }
    internal NeighborAndWeight(Variable n, double w) { Neighbor = n; Weight = w; }
}

// 转换后
readonly struct NeighborAndWeight {
    internal Variable Neighbor { get; }
    internal double Weight { get; }
    internal NeighborAndWeight(Variable n, double w) { Neighbor = n; Weight = w; }
}
```

#### L3: 数据容器转换

```csharp
// 转换前（EdgeConstraints - 属性模式）
struct EdgeConstraints {
    public Direction Direction { get; set; }
    public double Separation { get; set; }
}

// 转换后
readonly struct EdgeConstraints {
    public Direction Direction { get; }
    public double Separation { get; }
    public EdgeConstraints(Direction direction, double separation) {
        Direction = direction;
        Separation = separation;
    }
}
```

```csharp
// 转换前（PixelPoint - 字段模式）
struct PixelPoint {
    internal int X;
    internal int Y;
    internal PixelPoint(int x, int y) { X = x; Y = y; }
}

// 转换后
readonly struct PixelPoint {
    internal int X { get; }
    internal int Y { get; }
    // 已有构造函数保持不变
    internal PixelPoint(int x, int y) { X = x; Y = y; }
}
```

**实现要点**：
- 检查是否已存在相同签名的构造函数，避免重复生成（CS0111）
- 字段名与 `object` 继承成员冲突时（如 `GetType`、`Equals`、`GetHashCode`、`ToString`），标记为不可转换

#### L5: 方法迁移转换（V2 核心新功能）

```csharp
// 转换前
struct Rectangle {
    double left, right, top, bottom;
    public void Add(Point point) {
        if (!IsEmpty) {
            if (left > point.X) left = point.X;
            // ...
        }
    }
    public Rectangle Pad(double padding) {
        PadWidth(padding);
        PadHeight(padding);
        return this;  // 返回 this 的 mutating 方法
    }
}

// 转换后
readonly struct Rectangle {
    double left, right, top, bottom;
    public Rectangle Add(Point point) {
        var result = this;  // 创建副本
        if (!result.IsEmpty) {
            if (result.left > point.X) result.left = point.X;
            // ...
        }
        return result;
    }
    public Rectangle Pad(double padding) {
        var result = this;
        result = result.PadWidth(padding);
        result = result.PadHeight(padding);
        return result;
    }
}
```

**调用点同步更新**：

```csharp
// 转换前
var rect = new Rectangle();
rect.Add(point);           // 无返回值使用
var padded = rect.Pad(5);  // 返回 this

// 转换后
var rect = new Rectangle();
rect = rect.Add(point);    // 必须赋值
var padded = rect.Pad(5);  // 保持不变
```

### 4.3 方法迁移规则

#### 4.3.1 返回 void 的 mutating 方法

```csharp
// 转换前
public void Add(Point point) { left = point.X; }

// 转换后
public Rectangle Add(Point point) {
    var result = this;
    result.left = point.X;
    return result;
}
```

**调用点更新**：`rect.Add(point)` → `rect = rect.Add(point)`

#### 4.3.2 返回 this 的 mutating 方法

```csharp
// 转换前
public Rectangle Pad(double padding) {
    Left -= padding;
    Right += padding;
    return this;
}

// 转换后
public Rectangle Pad(double padding) {
    var result = this;
    result.Left -= padding;
    result.Right += padding;
    return result;
}
```

**调用点更新**：无需更新（语义兼容，原调用 `rect.Pad(5)` 现在返回新实例）

#### 4.3.3 返回其他值的 mutating 方法（使用 out 参数）

```csharp
// 转换前
public bool AddWithCheck(Point point) {
    bool wider;
    if (wider = (point.X < left)) left = point.X;
    return wider || higher;
}

// 转换后
public bool AddWithCheck(Point point, out Rectangle newStatus) {
    var result = this;
    bool wider;
    if (wider = (point.X < result.left)) result.left = point.X;
    newStatus = result;
    return wider || higher;
}
```

**调用点更新**：

```csharp
// 转换前
bool ok = rect.AddWithCheck(point);

// 转换后
bool ok = rect.AddWithCheck(point, out Rectangle newRect);
rect = newRect;  // 需要更新原变量状态
```

**注意**：当调用点不使用返回值时（如 `rect.AddWithCheck(point);`），需要编译器分析调用上下文：
- 若调用后 `rect` 不再被使用，可安全丢弃 `out` 参数
- 若调用后 `rect` 被读取，必须赋值：`rect = newRect;`

#### 4.3.4 不可迁移的方法判定

以下情况判定为不可迁移：
- 方法通过 `ref this` 或 `out this` 被外部调用
- 方法被委托（delegate）引用
- 方法被多态调用（virtual/override/interface）
- 方法签名在公共 API 中且调用点无法更新
- 方法包含 `stackalloc` 或指针操作
- 方法包含 `unsafe` 代码块
- 方法体超过 100 行且修改超过 3 个字段（人工审查标记）

---

## 5. 模块布局

```
src/CSharpToJava.Core/ReadOnlyStructMaker/
├── ReadOnlyStructMaker.cs                # 公共入口
├── ReadOnlyStructMakerOptions.cs         # 配置选项
├── ReadOnlyStructMakerDiagnostics.cs     # 诊断类型 + 结果类型
├── StructAnalyzer.cs                     # 核心分析器
├── CallGraphBuilder.cs                   # 调用图构建器
├── PatternRecognizer.cs                  # V2: 模式识别器
├── MethodMigrator.cs                     # V2: 方法迁移器
├── ConstructorGenerator.cs               # V2: 构造函数生成器
├── CallSiteUpdater.cs                    # V2: 调用点更新器
└── ReadOnlyStructRewriter.cs             # 语法重写器
```

---

## 6. 架构设计

### 6.1 执行流程

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Entry Point                                   │
│  ReadOnlyStructMaker.MakeReadOnly(compilation, options)              │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    Phase 1: 编译收集                                 │
│  • 使用已有 Compilation 获取 SemanticModel                           │
│  • 收集所有 StructDeclarationSyntax                                  │
│  • 识别已 readonly/ref/partial struct，标记跳过                      │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    Phase 2: 模式识别                                 │
│  • PatternRecognizer 分析每个 struct 的模式                          │
│  • 识别字段类型、属性类型、方法签名                                  │
│  • 初步分类：A/B/C/D/E/F/G/H                                        │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    Phase 3: 调用图构建                               │
│  • CallGraphBuilder 遍历所有 struct 的实例方法                        │
│  • 构建方法→被调用方法的映射关系                                      │
│  • 标记直接修改字段的方法                                            │
│  • 分析 mutating 方法的可迁移性                                       │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    Phase 4: 可转换性判定                             │
│  • StructAnalyzer 结合模式识别和调用图分析结果                        │
│  • 确定每个 struct 的转换级别（L0-L7）                                │
│  • 对 L5 级别验证方法迁移的安全性                                     │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    Phase 5: 语法重写                                 │
│  • ReadOnlyStructRewriter 根据转换级别执行不同重写                    │
│  • L1: 添加 readonly 修饰符                                          │
│  • L2: 移除 private set                                              │
│  • L3: 生成构造函数，转换属性                                        │
│  • L4: 封装字段为属性，生成构造函数                                   │
│  • L5: MethodMigrator 转换 mutating 方法                             │
│  • L6: 部分转换                                                      │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    Phase 6: 调用点更新（V2 新增）                     │
│  • CallSiteUpdater 遍历所有调用点                                     │
│  • 对 L5 转换的 mutating 方法调用点进行更新                           │
│  • void 返回值方法：添加赋值 `s = s.Method(args)`                    │
│  • this 返回值方法：保持不变（语义兼容）                              │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    Phase 7: 验证                                     │
│  • 编译验证转换后的代码                                               │
│  • 语义等价性检查                                                    │
│  • 输出诊断信息                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 6.2 核心组件

#### ReadOnlyStructMaker.cs — 公共入口

```csharp
public sealed class ReadOnlyStructMaker
{
    /// <summary>项目级入口：分析整个编译中的所有 struct 并转换为 readonly。</summary>
    public ReadOnlyStructMakerResult MakeReadOnly(
        CSharpCompilation compilation,
        ReadOnlyStructMakerOptions? options = null);
    
    /// <summary>单文件入口（用于测试）：分析单个文件中的 struct。</summary>
    public ReadOnlyStructMakerResult MakeReadOnly(
        SyntaxTree tree,
        SemanticModel semanticModel,
        ReadOnlyStructMakerOptions? options = null);
}
```

#### PatternRecognizer.cs — 模式识别器（V2 新增）

```csharp
internal sealed class PatternRecognizer
{
    /// <summary>分析 struct 的模式类型</summary>
    public StructPattern RecognizePattern(
        INamedTypeSymbol structSymbol, 
        StructDeclarationSyntax syntax);
}

internal enum StructPattern
{
    FullImmutable,          // Pattern A: 完全不可变
    AlreadyReadonly,        // Pattern B: 已 readonly 字段
    PrivateSetter,          // Pattern C: 私有 setter
    DataContainer,          // Pattern D: 数据容器（公共字段或 get/set 属性，无 mutating 方法）
    MutableMethods,         // Pattern E: 含 mutating 方法（可迁移）
    MutableMethodsNonMigratable, // Pattern F: 含 mutating 方法（不可迁移）
}

internal sealed record PatternAnalysisResult(
    StructPattern Pattern,
    IReadOnlyList<FieldInfo> Fields,
    IReadOnlyList<PropertyInfo> Properties,
    IReadOnlyList<MethodInfo> Methods,
    ConversionLevel SuggestedLevel);

internal enum ConversionLevel
{
    Skip,           // L0: 跳过（已 readonly/ref/partial/标记特性）
    DirectAdd,      // L1: 直接添加 readonly
    PropertyConvert,// L2: 属性转换（private set → get-only）
    DataContainer,  // L3: 数据容器转换（字段/属性 → get-only + 构造函数）
    MethodMigrate,  // L5: 方法迁移（mutating → 纯方法）
    NotConvertible, // L7: 不可转换
}
```

#### MethodMigrator.cs — 方法迁移器（V2 新增）

```csharp
internal sealed class MethodMigrator
{
    private readonly INamedTypeSymbol _structSymbol;
    private readonly CSharpCompilation _compilation;
    
    public MethodMigrator(INamedTypeSymbol structSymbol, CSharpCompilation compilation)
    {
        _structSymbol = structSymbol;
        _compilation = compilation;
    }

    /// <summary>
    /// 将 mutating 方法转换为返回新实例的纯方法。
    /// 返回转换后的方法语法节点和调用点更新信息。
    /// </summary>
    public MethodMigrationResult MigrateMethod(
        MethodDeclarationSyntax method,
        StructDeclarationSyntax structSyntax);
    
    /// <summary>分析方法是否可迁移</summary>
    public bool CanMigrate(IMethodSymbol methodSymbol);
}

internal sealed record MethodMigrationResult(
    MethodDeclarationSyntax MigratedMethod,
    MigrationType Type,
    IReadOnlyList<CallSiteUpdate> RequiredCallSiteUpdates);

internal enum MigrationType
{
    VoidToStruct,       // void → struct 返回值
    ThisToStruct,       // 返回 this → 返回新副本
    OtherReturnToOut,   // 其他返回值 → 原返回值 + out 参数
}

internal sealed record CallSiteUpdate(
    ExpressionSyntax CallSite,
    SyntaxTree Tree,
    string FilePath);
```

#### CallSiteUpdater.cs — 调用点更新器（V2 新增）

```csharp
internal sealed class CallSiteUpdater : CSharpSyntaxRewriter
{
    private readonly Dictionary<IMethodSymbol, MethodMigrationResult> _migratedMethods;
    private readonly CSharpCompilation _compilation;
    
    /// <summary>
    /// 更新 mutating 方法的调用点：
    /// - void 返回值：`s.Method(args)` → `s = s.Method(args)`
    /// - this 返回值：保持不变
    /// - 其他返回值：需要特殊处理
    /// </summary>
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node);
    
    /// <summary>
    /// 处理赋值链：`s = s.A().B()` → `s = s.A().B()`（无需改动）
    /// 处理独立调用：`s.A()` → `s = s.A()`
    /// </summary>
    private ExpressionStatementSyntax UpdateVoidCallSite(
        ExpressionStatementSyntax statement,
        InvocationExpressionSyntax invocation);
}
```

#### ConstructorGenerator.cs — 构造函数生成器（V2 新增）

```csharp
internal sealed class ConstructorGenerator
{
    /// <summary>
    /// 为 DTO/字段封装模式生成构造函数。
    /// 生成包含所有字段的构造函数参数。
    /// </summary>
    public ConstructorDeclarationSyntax GenerateConstructor(
        StructDeclarationSyntax structSyntax,
        PatternAnalysisResult pattern);
    
    /// <summary>
    /// 生成属性初始化代码
    /// </summary>
    private ParameterSyntax CreateParameter(IFieldSymbol field);
    private AssignmentExpressionSyntax CreateAssignment(IFieldSymbol field);
}
```

#### ReadOnlyStructMakerOptions.cs — 配置选项

```csharp
public sealed class ReadOnlyStructMakerOptions
{
    /// <summary>禁止转换特性的名称（不含 Attribute 后缀），命名空间不限</summary>
    public string OptOutAttributeName { get; init; } = "DoNotMakeReadOnly";
    
    /// <summary>是否输出 Info 级跳过诊断（默认 true）</summary>
    public bool ReportSkipped { get; init; } = true;
    
    /// <summary>不可转换的 struct 是否视为致命错误（对应 CLI --strict）</summary>
    public bool Strict { get; init; }
    
    // V2 新增选项
    
    /// <summary>是否启用方法迁移（L5 转换），默认 true</summary>
    public bool EnableMethodMigration { get; init; } = true;
    
    /// <summary>是否启用 DTO 转换（L3 转换），默认 true</summary>
    public bool EnableDtoConversion { get; init; } = true;
    
    /// <summary>是否启用字段封装（L4 转换），默认 true</summary>
    public bool EnableFieldWrapping { get; init; } = true;
    
    /// <summary>是否启用部分转换（L6 转换），默认 false</summary>
    public bool EnablePartialConversion { get; init; } = false;
    
    /// <summary>方法迁移时是否更新调用点，默认 true</summary>
    public bool UpdateCallSites { get; init; } = true;
    
    /// <summary>转换模式白名单，空则全部允许</summary>
    public IReadOnlySet<ConversionLevel>? AllowedLevels { get; init; }
}
```

#### StructAnalyzer.cs — 结构分析器

```csharp
internal sealed class StructAnalyzer
{
    private readonly CallGraphBuilder _callGraph;
    private readonly PatternRecognizer _patternRecognizer;
    private readonly ReadOnlyStructMakerOptions _options;
    
    /// <summary>分析 struct 的转换级别</summary>
    public AnalyzeResult Analyze(INamedTypeSymbol structSymbol, StructDeclarationSyntax syntax);
}

internal sealed record AnalyzeResult(
    ConversionLevel Level,
    StructPattern Pattern,
    bool ShouldRewrite,
    string Reason,
    StructDeclarationSyntax? DeclarationSyntax,
    string? QualifiedName,
    IReadOnlyList<MethodMigrationInfo>? MethodMigrations)
{
    public static AnalyzeResult AlreadyReadOnly(string name) => 
        new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false, 
            "already readonly struct (skipped)", null, name, null);
    
    public static AnalyzeResult Skip(string name, string reason) => 
        new(ConversionLevel.Skip, StructPattern.HeavyMutable, false, reason, null, name, null);
    
    public static AnalyzeResult Fail(string name, string reason, StructDeclarationSyntax syntax) => 
        new(ConversionLevel.NotConvertible, StructPattern.HeavyMutable, false, reason, syntax, name, null);
    
    public static AnalyzeResult Success(string name, StructDeclarationSyntax syntax, 
        ConversionLevel level, StructPattern pattern) => 
        new(level, pattern, true, $"converted to readonly struct ({level})", syntax, name, null);
    
    // V2 新增：带方法迁移的成功结果
    public static AnalyzeResult SuccessWithMigration(string name, StructDeclarationSyntax syntax,
        IReadOnlyList<MethodMigrationInfo> migrations) =>
        new(ConversionLevel.MethodMigrate, StructPattern.MutableMethods, true,
            $"converted to readonly struct with {migrations.Count} method migrations", 
            syntax, name, migrations);
}

internal sealed record MethodMigrationInfo(
    IMethodSymbol Method,
    MigrationType Type,
    MethodDeclarationSyntax Syntax);
```

#### ReadOnlyStructRewriter.cs — 语法重写器

```csharp
internal sealed class ReadOnlyStructRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<StructDeclarationSyntax, AnalyzeResult> _analysisResults;
    private readonly ConstructorGenerator _ctorGenerator;
    private readonly CSharpCompilation _compilation;
    
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        if (!_analysisResults.TryGetValue(node, out var result) || !result.ShouldRewrite)
            return base.VisitStructDeclaration(node);
        
        return result.Level switch
        {
            ConversionLevel.DirectAdd => ApplyDirectAdd(node),
            ConversionLevel.PropertyConvert => ApplyPropertyConvert(node),
            ConversionLevel.DataContainer => ApplyDataContainerConvert(node, result),
            ConversionLevel.MethodMigrate => ApplyMethodMigrate(node, result),
            _ => base.VisitStructDeclaration(node)
        };
    }
    
    private StructDeclarationSyntax ApplyDirectAdd(StructDeclarationSyntax node)
    {
        // V1 逻辑：添加 readonly 修饰符
        if (node.Modifiers.Count > 0)
        {
            var trailingToken = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space);
            return node.AddModifiers(trailingToken);
        }
        var readonlyToken = SyntaxFactory.Token(
                node.Keyword.LeadingTrivia,
                SyntaxKind.ReadOnlyKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space));
        return node
            .WithKeyword(node.Keyword.WithLeadingTrivia())
            .AddModifiers(readonlyToken);
    }
    
    private StructDeclarationSyntax ApplyPropertyConvert(StructDeclarationSyntax node)
    {
        // L2: 移除 private set
        var rewriter = new PrivateSetRewriter();
        return (StructDeclarationSyntax)rewriter.Visit(node);
    }
    
    private StructDeclarationSyntax ApplyDataContainerConvert(StructDeclarationSyntax node, AnalyzeResult result)
    {
        // L3: 数据容器转换（统一处理属性模式和字段模式）
        var rewritten = node;
        
        // 1. 转换属性（get/set → get-only）
        rewritten = (StructDeclarationSyntax)new GetSetToGetOnlyRewriter().Visit(rewritten);
        
        // 2. 封装字段（公共字段 → get-only 属性）
        rewritten = (StructDeclarationSyntax)new FieldToPropertyRewriter().Visit(rewritten);
        
        // 3. 添加构造函数（仅在不存在等效签名时）
        var ctor = _ctorGenerator.GenerateConstructor(rewritten, result);
        if (ctor != null && !HasExistingConstructor(rewritten, ctor))
        {
            rewritten = rewritten.AddMembers(ctor);
        }
        
        // 4. 添加 readonly
        return ApplyDirectAdd(rewritten);
    }
    
    private StructDeclarationSyntax ApplyMethodMigrate(StructDeclarationSyntax node, AnalyzeResult result)
    {
        // L5: 方法迁移
        var rewritten = node;
        if (result.MethodMigrations != null)
        {
            // 关键修复：从原始 syntax tree 获取 semantic model，而非从 rewritten 节点
            // 因为 rewritten 节点不在原 compilation 中，无法获取有效语义
            var semanticModel = _compilation.GetSemanticModel(node.SyntaxTree);
            var structSymbol = (INamedTypeSymbol)semanticModel.GetDeclaredSymbol(node)!;
            
            var migrator = new MethodMigrator(structSymbol, _compilation);
            foreach (var migration in result.MethodMigrations)
            {
                var migrationResult = migrator.MigrateMethod(
                    migration.Syntax, 
                    rewritten);
                // 替换方法
                rewritten = rewritten.ReplaceNode(
                    migration.Syntax, 
                    migrationResult.MigratedMethod);
            }
        }
        return ApplyDirectAdd(rewritten);
    }
    
    private bool HasExistingConstructor(StructDeclarationSyntax node, ConstructorDeclarationSyntax newCtor)
    {
        var newParameters = newCtor.ParameterList.Parameters;
        return node.Members.OfType<ConstructorDeclarationSyntax>().Any(existing =>
        {
            var existingParams = existing.ParameterList.Parameters;
            if (existingParams.Count != newParameters.Count) return false;
            // 比较参数类型是否相同
            for (int i = 0; i < existingParams.Count; i++)
            {
                if (existingParams[i].Type?.ToString() != newParameters[i]?.Type?.ToString())
                    return false;
            }
            return true;
        });
    }
}

/// <summary>将 get/set 自动属性转换为 get-only 属性</summary>
internal sealed class GetSetToGetOnlyRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        // 移除 setter，保留 getter
        if (node.AccessorList?.Accessors.Any(a => a.Kind() == SyntaxKind.SetAccessorDeclaration) == true)
        {
            var getter = node.AccessorList.Accessors.First(a => a.Kind() == SyntaxKind.GetAccessorDeclaration);
            var newAccessorList = SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { getter }));
            return node.WithAccessorList(newAccessorList);
        }
        return base.VisitPropertyDeclaration(node);
    }
}

/// <summary>将公共字段封装为 get-only 属性</summary>
internal sealed class FieldToPropertyRewriter : CSharpSyntaxRewriter
{
    private static readonly HashSet<string> ObjectMemberNames = new()
    {
        "GetType", "Equals", "GetHashCode", "ToString"
    };
    
    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var propertyDeclarations = new List<MemberDeclarationSyntax>();
        foreach (var variable in node.Declaration.Variables)
        {
            var fieldName = variable.Identifier.Text;
            
            // 检查命名冲突
            if (ObjectMemberNames.Contains(fieldName))
            {
                // 跳过此字段（调用方应标记为不可转换）
                continue;
            }
            
            // 生成属性：public int X { get; }
            var property = SyntaxFactory.PropertyDeclaration(
                node.Declaration.Type,
                fieldName)
                .AddModifiers(node.Modifiers.ToArray())
                .AddAccessorListAccessors(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            
            propertyDeclarations.Add(property);
        }
        
        if (propertyDeclarations.Count == node.Declaration.Variables.Count)
        {
            return SyntaxFactory.List<MemberDeclarationSyntax>(propertyDeclarations);
        }
        
        return base.VisitFieldDeclaration(node);
    }
}

/// <summary>将 private set 属性转换为 get-only（用于 L2）</summary>
internal sealed class PrivateSetRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (node.AccessorList == null) return base.VisitPropertyDeclaration(node);
        
        var hasPrivateSetter = node.AccessorList.Accessors.Any(a => 
            a.Kind() == SyntaxKind.SetAccessorDeclaration &&
            a.Modifiers.Any(m => m.Kind() == SyntaxKind.PrivateKeyword));
            
        if (hasPrivateSetter)
        {
            var getter = node.AccessorList.Accessors.First(a => a.Kind() == SyntaxKind.GetAccessorDeclaration);
            var newAccessorList = SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { getter }));
            return node.WithAccessorList(newAccessorList);
        }
        
        return base.VisitPropertyDeclaration(node);
    }
}
```

---

## 7. 诊断设计

### 7.1 诊断类型

```csharp
public enum ReadOnlyStructSeverity
{
    Info,       // 成功转换或跳过
    Warning,    // 无法转换或部分转换
    Error       // 处理异常
}

public sealed record ReadOnlyStructMakerDiagnostic(
    ReadOnlyStructSeverity Severity,
    string StructName,
    string Reason,
    ConversionLevel? Level,
    StructPattern? Pattern,
    string? FilePath = null,
    int? Line = null);

/// <summary>转换统计信息，遵循 GotoEliminatorStatistics 模式。</summary>
public sealed class ReadOnlyStructMakerStatistics
{
    public int StructsScanned;
    public int StructsConverted;
    public int StructsSkipped;
    public int StructsFailed;
    
    // 按转换级别统计
    public int Level0_Skipped;
    public int Level1_DirectAdd;
    public int Level2_PropertyConvert;
    public int Level3_DataContainer;
    public int Level5_MethodMigrate;
    
    public int MethodsMigrated;
    public int CallSitesUpdated;
    
    // 失败原因分类统计
    public int Failed_RefThisEscape;
    public int Failed_VirtualOrInterface;
    public int Failed_DelegateReferenced;
    public int Failed_ComplexMutableState;
    public int Failed_NameConflict;
}

public sealed record ReadOnlyStructMakerResult(
    string? OutputCode,
    bool Changed,
    IReadOnlyDictionary<string, string> ChangedFiles,
    IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics,
    ReadOnlyStructMakerStatistics Statistics);
```

### 7.2 诊断输出格式

**成功转换（L1 - 直接添加）**：
```
[Info] Struct 'Parallelogram' in Curves/Parallelogram.cs:43: converted to readonly struct (L1-DirectAdd, Pattern A-FullImmutable)
```

**成功转换（L2 - 属性转换）**：
```
[Info] Struct 'NeighborAndWeight' in ProjectionSolver/Variable.cs:107: converted to readonly struct (L2-PropertyConvert, Pattern C-PrivateSetter)
```

**成功转换（L3 - DTO 转换）**：
```
[Info] Struct 'EdgeConstraints' in Layout/EdgeConstraints.cs:16: converted to readonly struct (L3-DtoConvert, Pattern D-Dto)
```

**成功转换（L4 - 字段封装）**：
```
[Info] Struct 'PixelPoint' in OverlapRemovalFixedSegments/PixelPoint.cs:3: converted to readonly struct (L4-FieldWrap, Pattern E-PublicFields)
```

**成功转换（L5 - 方法迁移）**：
```
[Info] Struct 'Rectangle' in Geometry/Rectangle.cs:15: converted to readonly struct (L5-MethodMigrate, Pattern F-MutableMethods, 12 methods migrated, 47 call sites updated)
```

**部分转换（L6）**：
```
[Warning] Struct 'Struct 'MatrixCell' in ProjectionSolver/QPSC.cs:101: partially converted (L6-PartialConvert, Pattern G-Mixed): field 'Value' remains mutable
```

**不可转换（L7）**：
```
[Warning] Struct 'ViolationCache' in ProjectionSolver/ViolationCache.cs:33: not convertible (L7-NotConvertible, Pattern H-HeavyMutable): contains complex mutable state with array and index tracking
```

**跳过**：
```
[Info] Struct 'ConstraintDirectionPair' in ProjectionSolver/Block.cs:57: already readonly struct (skipped)
```

---

## 8. CLI 设计

### 8.1 新增 CLI 动词 `make-readonly`

```csharp
[Verb("make-readonly", HelpText = "Convert eligible structs to readonly structs")]
class MakeReadOnlyOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    
    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }
    
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    
    [Option("strict", Default = false, HelpText = "Treat unconvertible structs as fatal")]
    public bool Strict { get; set; }
    
    // V2 新增选项
    
    [Option("no-method-migration", Default = false, HelpText = "Disable method migration (L5)")]
    public bool NoMethodMigration { get; set; }
    
    [Option("no-dto-conversion", Default = false, HelpText = "Disable DTO conversion (L3)")]
    public bool NoDtoConversion { get; set; }
    
    [Option("no-field-wrapping", Default = false, HelpText = "Disable field wrapping (L4)")]
    public bool NoFieldWrapping { get; set; }
    
    [Option("enable-partial", Default = false, HelpText = "Enable partial conversion (L6)")]
    public bool EnablePartial { get; set; }
    
    [Option("no-update-call-sites", Default = false, HelpText = "Don't update call sites for migrated methods")]
    public bool NoUpdateCallSites { get; set; }
    
    [Option("min-level", Default = "L1", HelpText = "Minimum conversion level to apply (L1-L6)")]
    public string MinLevel { get; set; } = "L1";
}
```

### 8.2 convert-project 集成

在 `ConvertProjectOptions` 中添加：

```csharp
[Option("no-make-readonly", Default = false, HelpText = "Disable the default readonly-struct preprocessing step")]
public bool NoMakeReadOnly { get; set; }

public bool MakeReadOnly => !NoMakeReadOnly;

// V2 新增
[Option("readonly-min-level", Default = "L1", HelpText = "Minimum conversion level for readonly struct")]
public string ReadOnlyMinLevel { get; set; } = "L1";

[Option("readonly-no-method-migration", Default = false)]
public bool ReadOnlyNoMethodMigration { get; set; }
```

### 8.3 预处理流程集成

```
原始 C# 源码
     │
     ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 1: ReadOnlyStructMaker（如果启用）                    │
│  • 编译项目获取 CSharpCompilation                             │
│  • 模式识别 + 调用图分析                                      │
│  • 多级转换（L1-L6）                                          │
│  • 方法迁移 + 调用点更新                                      │
│  • 输出到中间目录 .cs2j-readonly-src/                         │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 2: GotoEliminator（如果启用）                         │
│  • 从 Phase 1 输出目录读取                                   │
│  • 转换后输出到 .cs2j-no-goto-src/                           │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
             后续 C# → Java 转换
```

---

## 9. 关键场景处理

### 9.1 递归方法调用

与 V1 相同，使用 `visited` 集合防止无限递归。

### 9.2 显式接口方法实现

V2 新增：接口方法若为 mutating，判定为不可迁移（保守策略）。

### 9.3 this 作为 ref/out 传递

V2 新增：若检测到 `ref this` 或 `out this` 传递，该 struct 的 L5 转换被禁止。

### 9.4 嵌套 struct

与 V1 相同，每个 struct 独立分析。

### 9.5 泛型 struct

与 V1 相同，泛型参数不影响转换判定。

### 9.6 void mutating 方法调用点（V2 新增）

```csharp
// 转换前（void mutating 方法）
var rect = new Rectangle();
rect.Add(point);           // void 返回
rect.Pad(5);               // void 返回

// 转换后（L5 方法迁移 - void 改为返回新实例）
var rect = new Rectangle();
rect = rect.Add(point);    // 必须赋值
rect = rect.Pad(5);        // 必须赋值
```

### 9.7 返回 this 的方法调用点（V2 新增）

```csharp
// 转换前（返回 this 的 mutating 方法）
var rect = new Rectangle();
var padded = rect.Pad(5);  // Pad 返回 this

// 转换后（语义兼容 - 返回新实例）
var rect = new Rectangle();
var padded = rect.Pad(5);  // 保持不变，padded 是新实例
```

### 9.8 嵌套方法链迁移（V2 新增）

```csharp
// 转换前（Pad 内部调用 PadWidth/PadHeight）
rect.Pad(5);  // 内部调用 PadWidth(5) 和 PadHeight(5)

// 转换后（所有方法同步迁移）
rect = rect.Pad(5);  // Pad、PadWidth、PadHeight 都返回新实例
// 调用点只需更新最外层
```

### 9.9 属性 setter 迁移（V2 新增）

```csharp
// 转换前（属性 setter 修改 backing field）
rect.Center = new Point(10, 20);  // setter 修改 left/right/top/bottom
rect.LeftTop = new Point(0, 0);  // setter 修改 left 和 top

// 转换后（改为 With 方法）
rect = rect.WithCenter(new Point(10, 20));
rect = rect.WithLeftTop(new Point(0, 0));
```

---

## 10. 测试策略

### 10.1 单元测试

| 测试场景 | 测试名称 | 预期结果 | 转换级别 |
|----------|----------|----------|----------|
| 完全不可变 struct | FullImmutable_ConvertsDirectly | Changed=true | L1 |
| 已 readonly 字段 | AlreadyReadonly_Skips | Changed=false, Info | L0 |
| 私有 setter | PrivateSetter_Converts | Changed=true | L2 |
| DTO 模式 | DtoStruct_Converts | Changed=true | L3 |
| 公共字段 | PublicFields_Converts | Changed=true | L4 |
| mutating 方法 | MutableMethod_Migrates | Changed=true, 方法签名变更 | L5 |
| 混合模式 | MixedStruct_PartialConvert | Changed=true, Warning | L6 |
| 重度可变 | HeavyMutable_Fails | Changed=false, Warning | L7 |
| 已经是 readonly | AlreadyReadOnly_Skips | Changed=false, Info | L0 |
| ref struct | RefStruct_Skips | Changed=false, Info | L0 |
| partial struct | PartialStruct_Skips | Changed=false, Info | L0 |
| [DoNotMakeReadOnly] | DoNotMakeReadOnlyAttribute_Skips | Changed=false, Warning | L0 |
| 嵌套 struct | NestedStruct_ConvertsIndependently | 分别判断 | - |
| 泛型 struct | GenericStruct_ConvertsSuccessfully | 正确处理 | - |
| 含 Attribute | StructWithAttribute_PreservesAttribute | Attribute 保留 | - |
| 含 XML 注释 | StructWithXmlDoc_PreservesComments | XML 注释保留 | - |
| 空 struct | EmptyStruct_ConvertsSuccessfully | 可转换 | L1 |
| 静态方法修改静态字段 | StaticMethodOnly_ConvertsSuccessfully | 不影响 | - |
| this 以 ref/out 传递 | RefThisEscape_SkipsL5 | L5 被禁止 | - |
| 调用链间接修改 | CallChainModifiesField_Skips | 递归检测成功 | - |
| **void mutating 调用点** | **VoidCallSite_AssignsResult** | **s = s.Method()** | **L5** |
| **嵌套方法链迁移** | **NestedMethodChain_MigratesAll** | **所有方法同步迁移** | **L5** |
| **属性 setter 迁移** | **PropertySetter_ConvertsToWithMethod** | **WithXxx() 方法** | **L5** |
| **构造函数生成** | **GeneratedConstructor_InitializesAll** | **所有字段初始化** | **L3/L4** |

### 10.2 MSAGL 集成测试

| 测试场景 | 测试名称 | 预期结果 |
|----------|----------|----------|
| Parallelogram 转换 | Msagl_Parallelogram_Converts | L1 转换成功 |
| Point 转换 | Msagl_Point_Converts | L4 转换成功 |
| Rectangle 转换 | Msagl_Rectangle_Migrates | L5 转换成功，方法迁移 |
| ViolationCache 跳过 | Msagl_ViolationCache_Skips | L7 不可转换 |
| 全量 MSAGL 转换 | Msagl_All19Structs_Converts14 | 至少 14 个成功（含 1 个 L6 部分转换） |

### 10.3 验证方法

转换后的 C# 代码必须编译通过：

```csharp
[Fact]
public void ConvertedCode_CompilesWithoutErrors()
{
    var input = "struct Point { public readonly int X; public readonly int Y; }";
    var tree = CSharpSyntaxTree.ParseText(input);
    var compilation = CSharpCompilation.Create("test")
        .AddSyntaxTrees(tree)
        .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    var result = new ReadOnlyStructMaker().MakeReadOnly(
        tree, compilation.GetSemanticModel(tree));
    
    Assert.True(result.Changed);
    var outputTree = CSharpSyntaxTree.ParseText(result.OutputCode!);
    Assert.False(outputTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error));
    Assert.Contains("readonly struct", result.OutputCode!);
}

[Fact]
public void MethodMigration_PreservesSemantics()
{
    var input = @"
struct Counter {
    public int Value;
    public void Increment() { Value++; }
    public Counter Add(int n) { Value += n; return this; }
}";
    var tree = CSharpSyntaxTree.ParseText(input);
    var compilation = CSharpCompilation.Create("test")
        .AddSyntaxTrees(tree)
        .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    var result = new ReadOnlyStructMaker().MakeReadOnly(
        tree, compilation.GetSemanticModel(tree));

    Assert.True(result.Changed);
    Assert.Contains("readonly struct", result.OutputCode);
    // void 方法迁移：签名从 void 改为返回 struct 实例
    Assert.Contains("public Counter Increment()", result.OutputCode);
    // 返回 this 的方法迁移：签名保持，但方法体改为返回新实例
    Assert.Contains("public Counter Add(", result.OutputCode);
}
```

---

## 11. 性能考虑

| 方面 | 措施 |
|------|------|
| 编译获取 | 本预处理在 convert-project 流程中早于主转换的编译发生，需自行经 SolutionLoader 加载项目获得 CSharpCompilation |
| 调用图缓存 | 每个 struct 的调用图只构建一次，避免重复分析 |
| 模式缓存 | 模式识别结果缓存，避免重复分析 |
| 短路判定 | 发现第一个不满足条件立即停止该 struct 的分析 |
| 并行处理 | 各 struct 分析相互独立，可并行处理 |
| 增量处理 | 通过 fingerprint 机制跳过未变更的文件 |
| **V2 新增：调用点索引** | **构建方法→调用点的反向索引，加速 L5 调用点更新** |

---

## 12. 文件清单

### 新增文件

| 路径 | 说明 |
|------|------|
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs` | 公共入口 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs` | 配置选项（V2 扩展） |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs` | 诊断类型 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs` | 核心分析器 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs` | 调用图构建器 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/PatternRecognizer.cs` | **V2: 模式识别器** |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs` | **V2: 方法迁移器** |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ConstructorGenerator.cs` | **V2: 构造函数生成器** |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/CallSiteUpdater.cs` | **V2: 调用点更新器** |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs` | 语法重写器（V2 扩展） |
| `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs` | 项目级预处理器 |
| `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs` | 单元测试 |

### 修改文件

| 路径 | 说明 |
|------|------|
| `src/CSharpToJava.CLI/Program.cs` | 新增 `make-readonly` 动词和 `MakeReadOnlyOptions`；`ConvertProjectOptions` 新增选项 |
| `README.md` | 更新文档 |

---

## 13. 与 V1 的对比

| 方面 | V1 | V2 |
|------|----|----|
| 转换判定 | 二元（可转换/不可转换） | 多级（L0-L7） |
| 模式识别 | 无 | 8 种模式（A-H） |
| mutating 方法 | 跳过不转换 | 方法迁移转换 |
| DTO 模式 | 不可转换 | 构造函数初始化转换 |
| 公共字段 | 不可转换 | 属性封装转换 |
| 私有 setter | 不可转换 | 直接转换 |
| 调用点更新 | 无 | 自动更新 |
| 诊断详细程度 | 仅首个原因 | 逐条列出 + 模式信息 |
| CLI 选项 | 基础 | 细粒度控制 |
| 适用 struct 数量（MSAGL） | ~4/19 | ~14/19（含 1 个 L6 部分转换） |

---

## 14. 风险与缓解

| 风险 | 影响 | 缓解措施 |
|------|------|----------|
| 方法迁移改变公共 API | 高 | 默认仅对 `internal`/`private` 方法启用 L5 |
| 调用点更新遗漏 | 高 | 编译验证 + 语义等价性检查 |
| 构造函数生成不完整 | 中 | 分析所有字段/属性，确保全覆盖 |
| 性能开销（调用图分析） | 中 | 缓存 + 并行处理 |
| 泛型约束冲突 | 低 | 转换后验证 `where T : struct` 约束 |
| **公共字段转属性破坏二进制兼容** | **高** | **L4 默认仅对 `internal` 字段启用，public 字段需显式确认** |
| **嵌套方法链遗漏迁移** | **中** | **递归分析方法调用图，确保链上所有方法同步迁移** |
| **属性 setter 逻辑复杂** | **中** | **F2 模式需人工审查，复杂 setter 标记为不可迁移** |

---

## 15. 实施计划

### Phase 1: 基础框架（L0-L2）
- 实现 PatternRecognizer
- 实现 L0/L1/L2 转换
- 基础诊断输出

### Phase 2: DTO 和字段转换（L3-L4）
- 实现 ConstructorGenerator
- 实现 L3/L4 转换
- 属性/字段重写器

### Phase 3: 方法迁移（L5）
- 实现 MethodMigrator
- 实现 CallSiteUpdater
- L5 转换 + 调用点更新

### Phase 4: 部分转换和优化（L6）
- 实现 L6 部分转换
- 性能优化
- 增量处理

### Phase 5: CLI 集成和测试
- CLI 动词实现
- convert-project 集成
- MSAGL 集成测试
