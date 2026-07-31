# ReadOnlyStructMaker 支持 Rectangle 类 struct 的设计文档

## 1. 问题分析

### 1.1 现象
当对 MSAGL 项目的 `Rectangle.cs` 执行 `make-readonly` 时，出现警告：
```
[Warning] MSAGL\Core\Geometry\Rectangle.cs: Rectangle: has virtual/override methods
```

### 1.2 Rectangle.cs 特征分析
`Rectangle` struct 具有以下特征：
1. **`override string ToString()`** - 重写 `System.Object.ToString()`
2. **显式接口实现**：
   - `bool IRectangle<Point>.Contains(IRectangle<Point> rect)`
   - `IRectangle<Point> IRectangle<Point>.Intersection(IRectangle<Point> rectangle)`
   - `bool IRectangle<Point>.Intersects(IRectangle<Point> rectangle)`
3. **隐式接口实现**：
   - `IRectangle<Point> Unite(IRectangle<Point> rectangle)`
4. **大量可变方法**：`SetToEmpty()`, `Add()`, `PadWidth()`, `PadHeight()`, `ScaleAroundCenter()` 等
5. **大量属性 setter**：`Left`, `Right`, `Top`, `Bottom`, `LeftTop`, `RightBottom`, `Width`, `Height`, `Size`, `Center` 等

### 1.3 根因分析

#### 根因 1：`IsGenuinelyVirtualOrAbstract` 错误标记接口实现

**位置**：`StructAnalyzer.cs` 第 452-470 行

```csharp
private static bool IsGenuinelyVirtualOrAbstract(IMethodSymbol method)
{
    if (!method.IsVirtual && !method.IsOverride && !method.IsAbstract)
        return false;

    // Only excludes Object/ValueType overrides
    if (method.IsOverride)
    {
        var overriddenType = method.OverriddenMethod?.ContainingType?.SpecialType;
        if (overriddenType == SpecialType.System_Object ||
            overriddenType == SpecialType.System_ValueType)
            return false;
    }

    return true;
}
```

**问题**：
- 显式接口实现（如 `IRectangle<Point>.Contains`）在 Roslyn 中标记为 `IsVirtual = true`，但 `IsOverride = false`
- 当前逻辑只排除 Object/ValueType 重写，导致接口实现被错误标记为"真正虚拟"
- 结果：`CheckNonMigratable` 返回 `"has virtual/override methods"`，struct 被标记为 `NotConvertible`

**为什么接口实现应该被排除**：
- 接口实现不引入多态性（struct 是 sealed 的）
- 接口方法可以被 readonly struct 实现
- 接口调用通过接口分派，不受 readonly 影响

#### 根因 2：`MethodMigrator` 只处理字段访问，不处理属性访问

**位置**：`MethodMigrator.cs` 第 109-158 行

**问题**：
- `ImplicitFieldToResultRewriter` 只替换标识符匹配字段名的访问
- Rectangle 的方法通过属性（如 `Left -= padding`）修改状态，而非直接访问字段
- 迁移后 `Left -= padding` 仍修改 `this.Left`，而非 `result.Left`
- 结果：返回的 `result` 不包含预期的修改

## 2. 修复方案

### 2.1 修复 `IsGenuinelyVirtualOrAbstract`

**方案**：增加对接口实现（显式和隐式）的排除

```csharp
private static bool IsGenuinelyVirtualOrAbstract(IMethodSymbol method)
{
    if (!method.IsVirtual && !method.IsOverride && !method.IsAbstract)
        return false;

    // 显式接口实现是安全的 - 它们不引入多态性
    if (method.ExplicitInterfaceImplementations.Any())
        return false;

    // 隐式接口实现也是安全的 - 检查此方法是否实现了任何接口成员
    var containingType = method.ContainingType;
    if (containingType != null)
    {
        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                var impl = containingType.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(impl, method))
                    return false;
            }
        }
    }

    // Object/ValueType 重写是安全的
    if (method.IsOverride)
    {
        var overriddenType = method.OverriddenMethod?.ContainingType?.SpecialType;
        if (overriddenType == SpecialType.System_Object ||
            overriddenType == SpecialType.System_ValueType)
            return false;
    }

    return true;
}
```

**影响范围**：
- 只影响 `CheckNonMigratable` 方法
- 不影响 Object/ValueType 重写的现有逻辑
- 不影响真正的虚拟/抽象方法检测

### 2.2 修复 `MethodMigrator` 处理属性访问

**方案**：扩展 `ImplicitFieldToResultRewriter` 同时处理属性和字段访问

#### 2.2.1 修改 `ApplyMethodMigrate` 收集属性名

在 `ReadOnlyStructRewriter.cs` 中：

```csharp
// 收集字段名和属性名
var fieldNames = rewritten.Members.OfType<FieldDeclarationSyntax>()
    .Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
    .ToHashSet();

var propertyNames = rewritten.Members.OfType<PropertyDeclarationSyntax>()
    .Where(p => !p.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
    .Select(p => p.Identifier.Text)
    .ToHashSet();

var migrator = new MethodMigrator(structName, fieldNames, propertyNames, migratedMethodNames);
```

#### 2.2.2 修改 `MethodMigrator` 构造函数

```csharp
private readonly HashSet<string> _propertyNames;

public MethodMigrator(string structName, HashSet<string>? fieldNames = null, 
    HashSet<string>? propertyNames = null, HashSet<string>? migratedMethodNames = null)
{
    _structName = structName;
    _fieldNames = fieldNames ?? new HashSet<string>();
    _propertyNames = propertyNames ?? new HashSet<string>();
    _migratedMethodNames = migratedMethodNames ?? new HashSet<string>();
}
```

#### 2.2.3 修改 `ImplicitFieldToResultRewriter` 处理属性

```csharp
// 在 VisitIdentifierName 中同时检查字段名和属性名
public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
{
    var name = node.Identifier.Text;
    if ((_fieldNames.Contains(name) || _propertyNames.Contains(name)) && !_localNames.Contains(name))
    {
        // Check it's not already qualified
        if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node)
            return base.VisitIdentifierName(node);

        var resultAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName("result"),
            node.WithoutTrivia())
            .WithTriviaFrom(node);
        return resultAccess;
    }
    return base.VisitIdentifierName(node);
}
```

### 2.3 修复属性 setter 迁移处理属性访问

**位置**：`ReadOnlyStructRewriter.cs` 的 `ApplyMethodMigrate` 方法

**问题**：属性 setter 迁移时，setter body 中的字段/属性访问没有被替换为 `result.field/property`

**方案**：在 setter body 插入 `var result = this;` 后，使用 `ImplicitFieldToResultRewriter` 替换访问

**实现细节**：
- `ImplicitFieldToResultRewriter` 当前是 `MethodMigrator` 的私有嵌套类
- 需要将其提取为独立的 `internal` 类（或 public），以便 `ReadOnlyStructRewriter` 也能使用
- 扩展构造函数接受 `propertyNames` 参数

```csharp
// 提取为独立类
internal sealed class ImplicitFieldToResultRewriter : CSharpSyntaxRewriter
{
    private readonly HashSet<string> _fieldNames;
    private readonly HashSet<string> _propertyNames;
    private readonly HashSet<string> _localNames = new();

    public ImplicitFieldToResultRewriter(HashSet<string> fieldNames, HashSet<string> propertyNames)
    {
        _fieldNames = fieldNames;
        _propertyNames = propertyNames;
    }

    // ... 其余代码不变，但 VisitIdentifierName 同时检查 _propertyNames
}
```

## 3. 兼容性分析

### 3.1 向后兼容
- 修复 1：只增加排除条件，不会导致原本通过的 case 失败
- 修复 2：扩展处理范围，原本通过字段访问的 case 仍然通过
- 新增的 Rectangle case 会被正确处理

### 3.2 测试影响
- `VirtualMethod_NotConvertible` 测试：仍然通过（纯 virtual 方法不受影响）
- `OverrideObjectMethod_WithMutatingMethod_IsMethodMigrated` 测试：仍然通过
- `GenuineVirtualMethod_StillNotConvertible` 测试：仍然通过（非 Object 重写的 override 不受影响）

## 4. 风险和限制

### 4.1 风险
- 修复 1 可能使一些原本被拒绝的 struct 被接受，但这些 struct 可能有其他问题
- 修复 2 可能引入新的 bug，如果属性名与局部变量名冲突

### 4.2 限制
- 此修复只处理单个文件内的转换
- 跨文件的调用点更新由 `CallSiteUpdater` 处理，不受影响
- 显式接口实现的调用点更新需要额外处理（如果调用者通过接口调用）

## 5. 验证计划

### 5.1 单元测试
1. 新增测试：struct 有显式接口实现 + 可变方法 → 应该被迁移
2. 新增测试：struct 有隐式接口实现 + 可变方法 → 应该被迁移
3. 新增测试：Rectangle 类 struct 的完整迁移
4. 确保现有测试全部通过

### 5.2 集成测试
1. 对 MSAGL 项目的 `Rectangle.cs` 执行 `make-readonly`
2. 验证没有 `has virtual/override methods` 警告
3. 验证生成的代码语义正确
