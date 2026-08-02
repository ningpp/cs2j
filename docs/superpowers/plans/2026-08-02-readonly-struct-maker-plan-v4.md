# ReadOnlyStructMaker V4 — True Readonly Struct 实现计划

**日期**: 2026-08-02
**设计文档**: `docs/superpowers/specs/2026-08-02-readonly-struct-maker-design-v4-true-readonly.md`

---

## 1. 目标

将 L4 转换升级为真正 readonly struct：
- 添加 `readonly` 修饰符
- 公共字段 → get-only 属性
- WithXxx 方法使用构造函数创建新实例
- 读取访问保持属性访问不变

---

## 2. 修改文件清单

| 文件 | 修改类型 | 说明 |
|------|----------|------|
| `ReadOnlyStructRewriter.cs` | 修改 | 重写 `ApplyPublicFieldToProperty` 方法 |
| `ReadOnlyStructMaker.cs` | 修改 | 传递 `propertyAccessStructs` 给 AssignmentRewriter |
| `AssignmentRewriter.cs` | 修改 | 修复读取访问转换逻辑 |
| `ReadOnlyStructMakerL4Tests.cs` | 修改/新增 | 更新现有测试，添加新场景 |

---

## 3. 实现步骤

### 步骤 1: 修改 `ReadOnlyStructRewriter.cs`

#### 1.1 重写 `ApplyPublicFieldToProperty` 方法

```csharp
private StructDeclarationSyntax ApplyPublicFieldToProperty(StructDeclarationSyntax node)
{
    var structName = node.Identifier.Text;
    
    // 1. 收集所有字段（公共和私有）
    var allFields = node.Members.OfType<FieldDeclarationSyntax>()
        .SelectMany(f => f.Declaration.Variables.Select(v => (Field: f, Variable: v)))
        .ToList();
    
    var publicFields = allFields.Where(f => f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))).ToList();
    if (publicFields.Count == 0) return node;

    // 2. 构建字段名→类型映射
    var fieldTypes = allFields.ToDictionary(
        f => f.Variable.Identifier.Text,
        f => f.Field.Declaration.Type);

    // 3. 转换成员：公共字段 → get-only 属性
    var newMembers = new SyntaxList<MemberDeclarationSyntax>();
    var publicFieldNames = new List<string>();

    foreach (var member in node.Members)
    {
        if (member is FieldDeclarationSyntax field &&
            field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        {
            foreach (var variable in field.Declaration.Variables)
            {
                var prop = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithAccessorList(SyntaxFactory.AccessorList(
                        SyntaxFactory.SingletonList(
                            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
                prop = prop.WithLeadingTrivia(field.GetLeadingTrivia());
                newMembers = newMembers.Add(prop);
                publicFieldNames.Add(variable.Identifier.Text);
            }
        }
        else
        {
            newMembers = newMembers.Add(member);
        }
    }

    var result = node.WithMembers(newMembers);

    // 4. 添加 readonly 修饰符
    result = ApplyDirectAdd(result);

    // 5. 确保构造函数存在
    var ctor = result.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
    if (ctor == null)
    {
        var ctorMembers = publicFieldNames.Select(n => (n, fieldTypes[n], true)).ToList();
        var newCtor = ConstructorGenerator.Generate(structName, ctorMembers);
        if (newCtor != null)
            result = result.AddMembers(newCtor);
    }

    // 6. 生成 WithXxx 方法
    foreach (var fieldName in publicFieldNames)
    {
        var withMethod = GenerateWithMethodUsingConstructor(
            structName, fieldName, fieldTypes, 
            result.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault());
        result = result.AddMembers(withMethod);
    }

    return result;
}
```

#### 1.2 新增 `ExtractConstructorParamToFieldMap` 方法

```csharp
/// <summary>
/// 从构造函数中提取参数名到字段名的映射。
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
        
        if (assign.Left is not MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } left)
            continue;
        if (left.Name is not IdentifierNameSyntax fieldName)
            continue;
        
        if (assign.Right is not IdentifierNameSyntax paramName)
            continue;
        
        map[paramName.Identifier.Text] = fieldName.Identifier.Text;
    }

    return map;
}
```

#### 1.3 新增 `GenerateWithMethodUsingConstructor` 方法

```csharp
private MethodDeclarationSyntax GenerateWithMethodUsingConstructor(
    string structName,
    string fieldName,
    Dictionary<string, TypeSyntax> fieldTypes,
    ConstructorDeclarationSyntax? ctor)
{
    var withMethodName = "With" + fieldName;
    var paramName = ToCamelCase(fieldName);

    // 提取参数→字段映射
    var paramToFieldMap = ExtractConstructorParamToFieldMap(ctor);
    
    // 构建反向映射：字段→参数
    var fieldToParamMap = paramToFieldMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
    
    // 如果没有映射（构造函数体不是简单赋值），使用 camelCase 参数名
    if (fieldToParamMap.Count == 0)
    {
        foreach (var name in fieldTypes.Keys)
            fieldToParamMap[name] = ToCamelCase(name);
    }

    // 构建构造函数参数
    var arguments = new List<ArgumentSyntax>();
    foreach (var (name, type) in fieldTypes)
    {
        ExpressionSyntax argValue;
        if (name == fieldName)
        {
            argValue = SyntaxFactory.IdentifierName(paramName); // 新值
        }
        else
        {
            argValue = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ThisExpression(),
                SyntaxFactory.IdentifierName(name));
        }
        
        // 使用命名参数
        var paramNameForCtor = fieldToParamMap.GetValueOrDefault(name, ToCamelCase(name));
        arguments.Add(SyntaxFactory.Argument(argValue)
            .WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(paramNameForCtor))));
    }

    var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
        .WithType(fieldTypes[fieldName]);

    return SyntaxFactory.MethodDeclaration(SyntaxFactory.IdentifierName(structName), withMethodName)
        .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
        .AddParameterListParameters(param)
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
            SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(structName))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(arguments)))))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace("        "));
}
```

#### 1.4 删除旧的 `GenerateWithMethod` 方法

旧的 V3 方法使用 `result = this; result.Field = value;` 模式，不再适用。

---

### 步骤 2: 修改 `ReadOnlyStructMaker.cs`

#### 2.1 修改 L4 处理逻辑

```csharp
// 在 ReadOnlyStructMaker.cs 中，修改 L4 调用点更新部分

// 更新 call sites for L4 public field assignments
if (options.EnablePublicFieldConversion)
{
    var publicFieldStructs = new Dictionary<string, HashSet<string>>();
    var propertyAccessStructs = new HashSet<string>(); // 新增

    foreach (var (structSyntax, result) in analysisResults.Where(kv => kv.Value.Level == ConversionLevel.PublicFieldToProperty))
    {
        var structName = structSyntax.Identifier.Text;
        var publicFields = structSyntax.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
            .ToHashSet();
        publicFieldStructs[structName] = publicFields;
        propertyAccessStructs.Add(structName); // L4 也使用属性访问
    }

    if (publicFieldStructs.Count > 0)
    {
        var assignmentRewriter = new AssignmentRewriter(
            publicFieldStructs, semanticModel, propertyAccessStructs); // 传递 propertyAccessStructs
        assignmentRewriter.BuildVariableTypeMap(root);
        newRoot = (CompilationUnitSyntax)assignmentRewriter.Visit(newRoot)!;
    }
}
```

---

### 步骤 3: 修改测试文件

#### 3.1 更新现有测试

由于 V4 行为变化，需要更新现有测试：

| 测试名 | V3 期望 | V4 期望 |
|--------|---------|---------|
| `PublicFields_SimpleAssignment_Converted` | `private double X;` | `public double X { get; }` |
| `PublicFields_CompoundAssignment_Converted` | `c.getValue() + 5` | `c.Value + 5` (属性访问) |
| `PublicFields_ReadAccess_ConvertedToGetter` | `p.getX()` | `p.X` (保持属性) |

#### 3.2 新增测试

| 测试名 | 验证内容 |
|--------|----------|
| `PublicFields_StructIsReadonly` | 验证输出包含 `readonly struct` |
| `PublicFields_WithXxxUsesConstructor` | 验证 WithXxx 使用 `new S(...)` |
| `PublicFields_PointCsPattern` | 验证 Point.cs 模式（非标准参数名） |
| `PublicFields_NoConstructor_GeneratesCtor` | 验证无构造函数时生成 |
| `PublicFields_Idempotent` | V4 幂等性 |

---

### 步骤 4: 运行测试验证

```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMakerL4Tests"
dotnet test  # 运行全部测试确保不回归
```

---

## 4. 风险与回滚

### 4.1 风险

1. **构造函数参数映射失败**：如果构造函数体不是简单赋值（如 `X = SomeFunc(x)`），映射为空
2. **命名参数顺序问题**：使用命名参数可以避免顺序问题
3. **属性读取与字段读取冲突**：确保 AssignmentRewriter 正确处理

### 4.2 回滚方案

如果 V4 实现有问题，可以回滚到 V3：
```bash
git checkout src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs
git checkout tests/CSharpToJava.Tests/ReadOnlyStructMakerL4Tests.cs
```

---

## 5. 时间估计

| 任务 | 估计时间 |
|------|----------|
| 修改 ReadOnlyStructRewriter.cs | 2 小时 |
| 修改 ReadOnlyStructMaker.cs | 30 分钟 |
| 修改 AssignmentRewriter.cs（如需要） | 30 分钟 |
| 修改测试 | 2 小时 |
| 运行测试验证 | 1 小时 |
| **总计** | **6 小时** |
