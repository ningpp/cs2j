# ReadOnlyStructMaker V4 — True Readonly Struct 设计

**日期**: 2026-08-02
**范围**: L4 转换升级为真正 readonly struct（字段 final）
**状态**: 待用户审核

---

## 1. 背景与动机

### 1.1 问题

前一版本 (V3) 的 L4 转换存在一个关键缺陷：**不添加 `readonly` 修饰符**，仅将公共字段转为私有字段 + getter 方法。这不符合用户的核心需求：

> **必须将 Point.cs 这类 struct 转换为 readonly，原公共字段属性 X/Y 必须设置为 final**

当前 V3 输出：
```csharp
public struct Point {           // ❌ 没有 readonly
    private double X;           // ❌ 不是 readonly
    private double Y;
    public double getX() => X;
    public Point WithX(double x) { var result = this; result.X = x; return result; }
}
```

期望输出：
```csharp
public readonly struct Point {  // ✅ 有 readonly
    public double X { get; }    // ✅ get-only 属性（背后是 readonly 字段）
    public double Y { get; }
    public Point(double x, double y) { X = x; Y = y; }  // ✅ 构造函数可赋值
    public Point WithX(double x) => new Point(x, Y);    // ✅ 通过构造函数创建新实例
}
```

### 1.2 目标

L4 转换必须生成真正的 readonly struct：
1. 添加 `readonly` 修饰符
2. 公共字段 → get-only 属性（`{ get; }`，背后是 `readonly` 字段）
3. 构造函数可以初始化这些属性（C# readonly struct 允许构造函数赋值 get-only 属性）
4. `WithXxx` 方法通过构造函数创建新实例
5. **正确更新所有外部调用点**

### 1.3 C# readonly struct 语义要点

- **readonly struct 的字段是 readonly**：只能在构造函数或声明时赋值
- **get-only 属性有编译器生成的 readonly backing field**：构造函数可以赋值
- **WithXxx 方法不能修改 this**：必须创建新实例
- **新实例通过构造函数创建**：`new Point(x, this.Y)`

---

## 2. 转换策略

### 2.1 核心转换流程

```
┌─────────────────────────────────────────────────────────┐
│              L4 True Readonly 转换流程                    │
├─────────────────────────────────────────────────────────┤
│ 1. 分析阶段（不变）                                      │
│    ├─ 检测公共字段                                       │
│    └─ 检测外部字段赋值点                                  │
│                                                         │
│ 2. 重写 Struct（修改）                                    │
│    ├─ public field → get-only property (自动 backing)    │
│    ├─ 添加 readonly 修饰符                               │
│    ├─ 确保构造函数存在（如不存在则生成）                    │
│    └─ 生成 WithXxx(value) 方法（使用构造函数）            │
│                                                         │
│ 3. 更新调用点（修改）                                     │
│    ├─ obj.Field = value → obj = obj.WithField(value)    │
│    ├─ obj.Field += value → obj = obj.WithField(obj.Field + value) │
│    ├─ obj.Field++ → obj = obj.WithField(obj.Field + 1)  │
│    ├─ new S { F = v } → new S(f: v)                   │
│    └─ obj.Field (读取) → 保持属性访问不变                 │
└─────────────────────────────────────────────────────────┘
```

### 2.2 转换示例

**输入**:
```csharp
public struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
    public double Length => Math.Sqrt(X * X + Y * Y);
}

class User {
    void M() {
        var p = new Point();
        p.X = 5;
        p.Y = 10;
        var len = p.Length;
    }
}
```

**输出**:
```csharp
public readonly struct Point {
    public double X { get; }
    public double Y { get; }
    public Point(double x, double y) { X = x; Y = y; }
    public double Length => Math.Sqrt(X * X + Y * Y);
    
    public Point WithX(double x) => new Point(x, Y);
    public Point WithY(double y) => new Point(X, y);
}

class User {
    void M() {
        var p = new Point();
        p = p.WithX(5);
        p = p.WithY(10);
        var len = p.Length;
    }
}
```

### 2.3 关键设计决策

| 决策 | V3 (旧) | V4 (新) | 理由 |
|------|---------|---------|------|
| 修饰符 | 不添加 readonly | **添加 readonly** | 用户明确要求 |
| 字段转换 | private 字段 | **get-only 属性** | readonly 语义 |
| WithXxx 实现 | `result = this; result.Field = value` | **`new S(value, this.OtherFields)`** | readonly 不允许修改字段 |
| 读取访问 | `obj.getField()` | **保持 `obj.Field`** | 属性访问更简洁 |
| 构造函数 | 保留原样 | **需要时生成** | WithXxx 需要构造函数 |

---

## 3. 实现方案

### 3.1 修改 ApplyPublicFieldToProperty

```csharp
private StructDeclarationSyntax ApplyPublicFieldToProperty(StructDeclarationSyntax node)
{
    var structName = node.Identifier.Text;
    
    // 1. 收集公共字段
    var publicFields = node.Members.OfType<FieldDeclarationSyntax>()
        .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        .SelectMany(f => f.Declaration.Variables)
        .ToList();
    
    if (publicFields.Count == 0) return node;

    // 2. 构建字段名→类型映射
    var fieldTypes = new Dictionary<string, TypeSyntax>();
    foreach (var field in node.Members.OfType<FieldDeclarationSyntax>())
    {
        foreach (var variable in field.Declaration.Variables)
        {
            fieldTypes[variable.Identifier.Text] = field.Declaration.Type;
        }
    }

    // 3. 转换成员
    var newMembers = new SyntaxList<MemberDeclarationSyntax>();
    var propertyNames = new List<string>();

    foreach (var member in node.Members)
    {
        if (member is FieldDeclarationSyntax field &&
            field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        {
            foreach (var variable in field.Declaration.Variables)
            {
                // public field → get-only property
                var prop = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithAccessorList(SyntaxFactory.AccessorList(
                        SyntaxFactory.SingletonList(
                            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
                
                // 保留原始的 leading trivia（preprocessor directives 等）
                prop = prop.WithLeadingTrivia(field.GetLeadingTrivia());
                newMembers = newMembers.Add(prop);
                propertyNames.Add(variable.Identifier.Text);
            }
        }
        else
        {
            newMembers = newMembers.Add(member);
        }
    }

    var result = node.WithMembers(newMembers);

    // 4. 添加 readonly 修饰符
    result = ApplyDirectAdd(result); // 复用 L1 的 readonly 添加逻辑

    // 5. 生成 WithXxx 方法（基于构造函数）
    foreach (var fieldName in propertyNames)
    {
        var withMethod = GenerateWithMethodUsingConstructor(structName, fieldName, fieldTypes);
        result = result.AddMembers(withMethod);
    }

    return result;
}

private MethodDeclarationSyntax GenerateWithMethodUsingConstructor(
    string structName, 
    string fieldName, 
    Dictionary<string, TypeSyntax> fieldTypes)
{
    var withMethodName = "With" + fieldName;
    var paramName = ToCamelCase(fieldName);

    // 构建构造函数参数：新值替换目标字段，其他字段从 this 获取
    var arguments = new List<ArgumentSyntax>();
    foreach (var (name, type) in fieldTypes)
    {
        ExpressionSyntax argValue;
        if (name == fieldName)
        {
            // 使用新值
            argValue = SyntaxFactory.IdentifierName(paramName);
        }
        else
        {
            // 从 this 获取原值
            argValue = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ThisExpression(),
                SyntaxFactory.IdentifierName(name));
        }
        arguments.Add(SyntaxFactory.Argument(argValue));
    }

    var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
        .WithType(fieldTypes[fieldName]);

    // public StructName WithXxx(type param) => new StructName(field1: param, field2: this.field2, ...);
    return SyntaxFactory.MethodDeclaration(SyntaxFactory.IdentifierName(structName), withMethodName)
        .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
        .AddParameterListParameters(param)
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
            SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(structName))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(arguments)))))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
}
```

### 3.2 处理构造函数场景

**场景 A：构造函数已存在（如 Point.cs）**

```csharp
// 输入
public Point(double xCoordinate, double yCoordinate) { X = xCoordinate; Y = yCoordinate; }
// 输出（无需修改）
public Point(double xCoordinate, double yCoordinate) { X = xCoordinate; Y = yCoordinate; }
```
构造函数保留原样，readonly struct 允许在构造函数中给 get-only 属性赋值。

**WithXxx 方法参数映射**：

需要从构造函数体中提取参数→字段映射：
```
X = xCoordinate  →  paramName "xCoordinate" maps to field "X"
Y = yCoordinate  →  paramName "yCoordinate" maps to field "Y"
```

生成 WithXxx 时使用构造函数的实际参数名：
```csharp
public Point WithX(double x) => new Point(xCoordinate: x, yCoordinate: this.Y);
public Point WithY(double y) => new Point(xCoordinate: this.X, yCoordinate: y);
```

**场景 B：无构造函数（需要生成）**
```csharp
// 输入
public struct Point {
    public double X;
    public double Y;
}
// 输出
public readonly struct Point {
    public double X { get; }
    public double Y { get; }
    public Point(double x, double y) { X = x; Y = y; }
    public Point WithX(double x) => new Point(x: x, y: this.Y);
    public Point WithY(double y) => new Point(x: this.X, y: y);
}
```

构造函数生成逻辑复用 `ConstructorGenerator.Generate`（生成 camelCase 参数名）。

### 3.2.1 构造函数参数→字段映射算法

从构造函数体中提取映射关系：

```csharp
/// <summary>
/// 从构造函数中提取参数名到字段名的映射。
/// 例如：X = xCoordinate; Y = yCoordinate;
/// 返回：{ "xCoordinate": "X", "yCoordinate": "Y" }
/// </summary>
private static Dictionary<string, string> ExtractConstructorParamToFieldMap(
    ConstructorDeclarationSyntax? ctor)
{
    var map = new Dictionary<string, string>();
    if (ctor?.Body == null) return map;

    foreach (var stmt in ctor.Body.Statements.OfType<ExpressionStatementSyntax>())
    {
        if (stmt.Expression is not AssignmentExpressionSyntax { Kind: SyntaxKind.SimpleAssignmentExpression } assign)
            continue;
        
        // 左侧必须是 this.FieldName
        if (assign.Left is not MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } left)
            continue;
        if (left.Name is not IdentifierNameSyntax fieldName)
            continue;
        
        // 右侧必须是参数名（标识符）
        if (assign.Right is not IdentifierNameSyntax paramName)
            continue;
        
        map[paramName.Identifier.Text] = fieldName.Identifier.Text;
    }

    return map;
}
```

**使用场景**：

```csharp
// Point.cs 构造函数
public Point(double xCoordinate, double yCoordinate) {
    X = xCoordinate;
    Y = yCoordinate;
}

// 提取映射
// xCoordinate → X
// yCoordinate → Y

// 生成 WithXxx 时使用构造函数参数名
public Point WithX(double x) => new Point(xCoordinate: x, yCoordinate: this.Y);
```

### 3.2.2 构造函数不存在时的处理

如果 struct 没有显式构造函数：
1. 使用 `ConstructorGenerator.Generate` 生成标准构造函数
2. 参数名为 camelCase（`X` → `x`）
3. 映射关系：参数名 camelCase → 字段名

```csharp
// 生成的构造函数
public Point(double x, double y) { X = x; Y = y; }

// 映射
// x → X
// y → Y

// WithXxx 方法
public Point WithX(double x) => new Point(x: x, y: this.Y);
```

### 3.3 修改 AssignmentRewriter

V4 的读取访问保持属性访问（`obj.Field`），不需要转为 `obj.getField()`。

需要在 `ReadOnlyStructMaker` 中：
- 标记 L4 structs 为 "property access"（类似 L5）
- AssignmentRewriter 对读取不做转换

```csharp
// 在 ReadOnlyStructMaker.cs 中
// 修改 L4 处理逻辑：将 L4 structs 标记为 property access
var publicFieldStructs = new Dictionary<string, HashSet<string>>();
var propertyAccessStructs = new HashSet<string>(); // 新增

foreach (var (structSyntax, result) in analysisResults.Where(kv => kv.Value.Level == ConversionLevel.PublicFieldToProperty))
{
    var structName = structSyntax.Identifier.Text;
    var publicFields = /* ... */;
    publicFieldStructs[structName] = publicFields;
    propertyAccessStructs.Add(structName); // L4 也使用属性访问
}

// 传递 propertyAccessStructs
var assignmentRewriter = new AssignmentRewriter(publicFieldStructs, semanticModel, propertyAccessStructs);
```

### 3.4 处理对象初始化器

对象初始化器 `new Point { X = 5, Y = 10 }` 需要转为构造函数调用：
```csharp
// 输入
new Point { X = 5, Y = 10 }
// 输出
new Point(x: 5, y: 10)
```

这部分逻辑 V3 已实现，V4 保持不变。

### 3.5 处理嵌套成员访问

对于 `container.Point.X = 5` 这种场景：

```csharp
// 输入
container.Point.X = 5;
// 输出
container.Point = container.Point.WithX(5);
```

AssignmentRewriter 已支持通过 `IsModifiableLValue` 检测。但需要注意：
- 如果 `container.Point` 是属性（get-only），无法赋值 → 需要标记警告
- 如果 `container.Point` 是字段，可以赋值 → 正常转换

---

## 4. 诊断与统计

### 4.1 诊断输出

```
[Info] Struct 'Point' in Geometry/Point.cs:12: converted to readonly with get-only properties (L4)
[Info]   - Field 'X' → Property 'X { get; }' + WithX(double x) method
[Info]   - Field 'Y' → Property 'Y { get; }' + WithY(double y) method
[Info]   - Updated 47 external assignment call sites
[Warning] Property 'container.Point' at GeometryUtils.cs:156: cannot assign readonly struct property
```

### 4.2 新增诊断

```csharp
// 当 get-only 属性被外部赋值时
public enum ConversionLevel
{
    // ...
    PublicFieldToReadOnly, // L4 (rename from PublicFieldToProperty)
}
```

（注意：可以选择重命名转换级别以反映新语义）

---

## 5. 边界情况处理

### 5.1 无构造函数的 struct

如果 struct 没有显式构造函数且公共字段未初始化：
- 生成默认构造函数 `public StructName(type1 p1, type2 p2, ...)`
- 确保 WithXxx 方法可用

### 5.2 多字段 struct

WithXxx 方法必须包含所有字段的值：
```csharp
public readonly struct Rect {
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    
    public Rect(double x, double y, double width, double height) { ... }
    
    public Rect WithX(double x) => new Rect(x, Y, Width, Height);
    public Rect WithY(double y) => new Rect(X, y, Width, Height);
    // ...
}
```

### 5.3 复合赋值

```csharp
// 输入
p.X += 5;
// 输出
p = p.WithX(p.X + 5);  // 读取保持属性访问
```

### 5.4 ref/out 参数

```csharp
void UpdatePoint(ref Point p) { p.X++; }
```
处理策略：标记警告，不自动更新（与 V3 相同）。

### 5.5 数组元素赋值

```csharp
points[i].X = 5;
// 输出
points[i] = points[i].WithX(5);
```

---

## 6. 成功标准

- [ ] Point.cs 成功转换为 readonly struct
- [ ] X/Y 字段变为 `{ get; }` 属性
- [ ] WithX/WithY 方法使用构造函数创建新实例
- [ ] 所有外部赋值调用点正确更新为 `p = p.WithX(value)`
- [ ] 读取访问保持 `p.X` 不变
- [ ] 转换后代码编译通过
- [ ] 单元测试覆盖所有场景
- [ ] 现有测试不回归

---

## 7. 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| 构造函数参数顺序不确定 | WithXxx 生成错误 | 使用命名参数或确保字段顺序一致 |
| get-only 属性被外部赋值 | 编译错误 | AssignmentRewriter 转换所有赋值点 |
| 嵌套属性不可赋值 | 无法转换 | 标记警告，用户手动处理 |
| WithXxx 与现有方法重命名 | 编译错误 | 检测重命名并生成唯一名称 |
