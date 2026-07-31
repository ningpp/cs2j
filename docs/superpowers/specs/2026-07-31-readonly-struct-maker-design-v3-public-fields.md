# ReadOnlyStructMaker V3 — Public Field 转 Property 设计

**日期**: 2026-07-31
**范围**: 新增 L4 转换级别 (PublicFieldToProperty)，支持公共字段被外部赋值的 struct
**状态**: 待用户审核

---

## 1. 背景与动机

### 1.1 问题

当前 `make-readonly` 功能对以下模式标记为 **不可转换 (NotConvertible)**：

```csharp
// MSAGL Point.cs — 公共字段被外部直接赋值
public struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}

// 外部代码大量直接赋值：
point.X = 5;
point.Y = 10;
arr[i].X = 3;
```

现有实现会在检测到外部公共字段赋值时拒绝转换：
```
[Warning] Point: public fields assigned externally
```

### 1.2 目标

新增 **L4: PublicFieldToProperty** 转换级别，支持将含公共字段的 struct 转换为 readonly struct：
1. 将公共字段转为 get-only 属性
2. 生成 `WithXxx(value)` 方法替代字段赋值
3. 生成构造函数支持初始化
4. **更新所有外部调用点** — 将 `obj.Field = value` 转换为 `obj = obj.WithField(value)`
5. 支持对象初始化器 `new S { Field = value }` → `new S(field: value)`

### 1.3 适用场景

| 场景 | 示例 | 支持 |
|------|------|------|
| 简单公共字段赋值 | `p.X = 5;` | ✅ |
| 数组元素字段赋值 | `arr[i].X = 5;` | ✅ |
| 对象初始化器 | `new Point { X = 1, Y = 2 }` | ✅ |
| ref 参数传递修改 | `M(ref p) { p.X++; }` | ⚠️ 需额外处理 |
| 公共字段读取 | `var x = p.X;` | ✅ 无需修改 |
| 复合赋值 | `p.X += 5;` | ✅ |

---

## 2. 转换策略

### 2.1 核心转换流程

```
┌─────────────────────────────────────────────────────────┐
│                   L4 转换流程                            │
├─────────────────────────────────────────────────────────┤
│ 1. 分析阶段                                             │
│    ├─ 检测公共字段                                       │
│    ├─ 检测外部字段赋值点                                  │
│    ├─ 检测对象初始化器                                    │
│    └─ 检测 ref/out 参数修改                              │
│                                                         │
│ 2. 重写 Struct                                          │
│    ├─ public field → get-only property                  │
│    ├─ 生成 WithXxx(value) 方法                          │
│    ├─ 添加构造函数（如不存在）                             │
│    └─ 添加 readonly 修饰符                               │
│                                                         │
│ 3. 更新调用点                                            │
│    ├─ obj.Field = value → obj = obj.WithField(value)   │
│    ├─ obj.Field += value → obj = obj.WithField(obj.Field + value) │
│    ├─ arr[i].Field = value → arr[i] = arr[i].WithField(value) │
│    ├─ new S { F = v } → new S(f: v)                   │
│    └─ ref 参数场景 → 标记警告                           │
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

// 调用代码
class User {
    void M() {
        var p = new Point();
        p.X = 5;
        p.Y = 10;
        arr[0].X = 3;
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
    
    public Point WithX(double value) {
        var result = this;
        result.X = value;  // 这里有问题，readonly 不能赋值
        return result;
    }
    // ... 问题：readonly struct 的字段不能在 WithXxx 中赋值
}
```

### 2.3 关键问题与解决方案

**问题 1**: readonly struct 的字段是 readonly，不能在 WithXxx 方法中赋值。

**解决方案 A (推荐)**: **不添加 readonly 修饰符**，仅做字段到属性的转换 + 调用点更新
- 优点：语义简单，无需克隆
- 缺点：struct 仍然可变，但只能通过 WithXxx 方法修改

**解决方案 B**: 使用 `Unsafe.AsRef` 或 `Unsafe.SkipInit` 进行原地修改（不推荐）

**解决方案 C**: 不转换这种 struct，保持 L7 不可转换

**决定**: 采用 **解决方案 A** — 不添加 `readonly` 修饰符，但将公共字段转为属性 + 生成 WithXxx 方法。这是一个中间步骤，用户可以选择后续手动添加 readonly。

---

## 3. 实现方案

### 3.1 新增转换级别

```csharp
public enum ConversionLevel {
    Skip,                // L0
    DirectAdd,           // L1
    PropertyConvert,     // L2
    DataContainer,       // L3
    PublicFieldToProperty, // L4 (新增)
    MethodMigrate,       // L5
    NotConvertible,      // L7
}
```

### 3.2 新增 StructPattern

```csharp
public enum StructPattern {
    FullImmutable,               // A
    AlreadyReadonly,             // B
    PrivateSetter,               // C
    DataContainer,               // D
    PublicFields,                // E (新增)
    MutableMethods,              // F
    MutableMethodsNonMigratable, // G
}
```

### 3.3 新增选项

```csharp
public sealed class ReadOnlyStructMakerOptions {
    // ... 现有选项 ...
    
    /// <summary>是否启用公共字段转属性转换 (L4)，默认 true</summary>
    public bool EnablePublicFieldConversion { get; init; } = true;
    
    /// <summary>
    /// 转换模式：
    /// - "property-only": 仅转为属性，不添加 readonly
    /// - "full-readonly": 完全 readonly + WithXxx 方法（未来实现）
    /// </summary>
    public string PublicFieldConversionMode { get; init; } = "property-only";
}
```

### 3.4 分析阶段扩展

在 `StructAnalyzer.cs` 中新增检测逻辑：

```csharp
// 新增模式 E: 有公共字段但无 mutating 方法
if (fields.Any(f => f.DeclaredAccessibility == Accessibility.PublicKey)) {
    // 检查是否有外部赋值
    if (_callGraph.HasExternalPublicFieldAssignment(symbol, root)) {
        if (_options.EnablePublicFieldConversion) {
            return new(ConversionLevel.PublicFieldToProperty, StructPattern.PublicFields, true,
                "public fields with external assignments - will convert to properties", name);
        }
        return new(ConversionLevel.NotConvertible, StructPattern.PublicFields, false,
            "public fields assigned externally (public field conversion disabled)", name);
    }
}
```

### 3.5 重写阶段扩展

在 `ReadOnlyStructRewriter.cs` 中新增 `ApplyPublicFieldToProperty`:

```csharp
private StructDeclarationSyntax ApplyPublicFieldToProperty(StructDeclarationSyntax node) {
    var structName = node.Identifier.Text;
    var publicFields = node.Members.OfType<FieldDeclarationSyntax>()
        .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        .ToList();
    
    if (!publicFields.Count == 0) return node;
    
    var newMembers = new SyntaxList<MemberDeclarationSyntax>();
    var withMethods = new List<MethodDeclarationSyntax>();
    
    foreach (var member in node.Members) {
        if (member is FieldDeclarationSyntax field && 
            field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) {
            // 将每个公共字段转为 get-only 属性
            foreach (var variable in field.Declaration.Variables) {
                var prop = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithAccessorList(SyntaxFactory.AccessorList(
                        SyntaxFactory.SingletonList(
                            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
                newMembers = newMembers.Add(prop);
                
                // 生成 WithXxx 方法
                var withMethod = GenerateWithMethod(structName, variable.Identifier.Text, field.Declaration.Type);
                withMethods.Add(withMethod);
            }
        } else {
            newMembers = newMembers.Add(member);
        }
    }
    
    var result = node.WithMembers(newMembers);
    
    // 添加 WithXxx 方法
    foreach (var method in withMethods) {
        result = result.AddMembers(method);
    }
    
    // 注意：不添加 readonly 修饰符，因为字段仍可写
    return result;
}

private MethodDeclarationSyntax GenerateWithMethod(string structName, string fieldName, TypeSyntax fieldType) {
    var withMethodName = "With" + fieldName;
    var paramName = Char.ToLowerInvariant(fieldName[0]) + fieldName[1..];
    
    var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
        .WithType(fieldType);
    
    var body = SyntaxFactory.Block(
        SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator("result")
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.ThisExpression()))))),
        SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("result"),
                    SyntaxFactory.IdentifierName(fieldName)),
                SyntaxFactory.IdentifierName(paramName))),
        SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result")));
    
    return SyntaxFactory.MethodDeclaration(SyntaxFactory.IdentifierName(structName), withMethodName)
        .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
        .AddParameterListParameters(param)
        .WithBody(body);
}
```

### 3.6 调用点更新

需要扩展 `CallSiteUpdater` 来处理：

1. **简单赋值**: `obj.Field = value` → `obj = obj.WithField(value)`
2. **复合赋值**: `obj.Field += value` → `obj = obj.WithField(obj.Field + value)`
3. **前置/后置递增**: `obj.Field++` → `obj = obj.WithField(obj.Field + 1)`
4. **数组元素**: `arr[i].Field = value` → `arr[i] = arr[i].WithField(value)`
5. **对象初始化器**: `new S { F = v, F2 = v2 }` → `new S(f: v, f2: v2)`

```csharp
// 新增赋值重写器
public sealed class AssignmentRewriter : CSharpSyntaxRewriter {
    private readonly Dictionary<string, string> _publicFields; // fieldName -> structName
    private readonly SemanticModel _model;
    
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node) {
        if (node.Left is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is IdentifierNameSyntax fieldName) {
            
            if (_publicFields.TryGetValue(fieldName.Identifier.Text, out var structName)) {
                // 检查左侧表达式的类型是否为 struct
                if (IsStructType(memberAccess.Expression, structName)) {
                    // obj.Field = value → obj = obj.WithField(value)
                    var withCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            memberAccess.Expression.WithoutTrivia(),
                            SyntaxFactory.IdentifierName("With" + fieldName.Identifier.Text)),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(node.Right))));
                    
                    return SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        memberAccess.Expression.WithoutTrivia(),
                        withCall);
                }
            }
        }
        return base.VisitAssignmentExpression(node);
    }
}
```

### 3.7 对象初始化器处理

```csharp
public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node) {
    if (node.Initializer == null || !node.Initializer.Expressions.Any()) 
        return base.VisitObjectCreationExpression(node);
    
    // 检查类型是否为要转换的 struct
    var typeSymbol = _model.GetTypeInfo(node.Type).Type;
    if (typeSymbol == null || !_publicFields.Values.Contains(typeSymbol.Name))
        return base.VisitObjectCreationExpression(node);
    
    // 提取初始值设定项中的字段名
    var fieldNames = node.Initializer.Expressions
        .OfType<AssignmentExpressionSyntax>()
        .ToDictionary(a => ((IdentifierNameSyntax)a.Left).Identifier.Text, a => a.Right);
    
    // 检查是否有对应的公共字段
    var hasPublicFieldInit = fieldNames.Keys.Any(f => _publicFields.ContainsKey(f));
    if (!hasPublicFieldInit)
        return base.VisitObjectCreationExpression(node);
    
    // new S { F = v } → new S(f: v)
    var arguments = new List<ArgumentSyntax>();
    foreach (var (fieldName, value) in fieldNames) {
        if (_publicFields.ContainsKey(fieldName)) {
            arguments.Add(SyntaxFactory.Argument(value)
                .WithNameColon(SyntaxFactory.NameColon(
                    SyntaxFactory.IdentifierName(ToCamelCase(fieldName)))));
        }
    }
    
    return node.WithInitializer(null)
        .WithArgumentList(SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(arguments)));
}
```

---

## 4. 诊断与统计

### 4.1 新增诊断类型

```csharp
public sealed class ReadOnlyStructMakerStatistics {
    // 现有字段...
    
    public int Level4_PublicFieldToProperty;  // 新增
    public int PublicFieldsConverted;          // 新增
    public int CallSitesRewritten;             // 新增
}
```

### 4.2 诊断输出

```
[Info] Struct 'Point' in Geometry/Point.cs:12: converted public fields to properties (L4)
[Info]   - Field 'X' → Property 'X { get; }' + WithX(double x) method
[Info]   - Field 'Y' → Property 'Y { get; }' + WithY(double y) method
[Info]   - Updated 47 external assignment call sites
[Warning] Ref parameter 'ref Point p' at GeometryUtils.cs:156: cannot update call site
```

---

## 5. 边界情况处理

### 5.1 ref/out 参数

当 struct 通过 ref/out 参数传递时，外部代码可以直接修改其字段：
```csharp
void UpdatePoint(ref Point p) { p.X++; }
```

**处理策略**:
- 检测 ref/out 参数使用
- 标记为警告，不自动更新调用点
- 用户需手动修改这些代码

### 5.2 数组元素赋值

```csharp
points[i].X = 5;
```

**处理**: 转换为 `points[i] = points[i].WithX(5);`

### 5.3 嵌套成员访问

```csharp
container.Point.X = 5;
```

**处理**: 需要找到最终拥有 struct 的变量，转换为:
```csharp
container.Point = container.Point.WithX(5);
```

### 5.4 链式调用

```csharp
GetPoint().X = 5;  // 无法赋值，GetPoint() 返回临时值
```

**处理**: 不处理（这本身就是编译错误）

---

## 6. 限制与注意事项

1. **不添加 readonly 修饰符**: 当前版本不添加 readonly，只做字段到属性的转换
2. **公共 API 变化**: 公共字段转为属性是源兼容但不二进制兼容的变更
3. **ref 参数场景**: 需要手动处理
4. **性能考虑**: WithXxx 方法会创建 struct 副本

---

## 7. 成功标准

- [ ] Point.cs 类 struct 成功转换
- [ ] 所有外部赋值调用点正确更新
- [ ] 对象初始化器正确转换
- [ ] 转换后代码编译通过
- [ ] 单元测试覆盖所有场景
- [ ] 现有测试不回归
