# ReadOnlyStructMaker 支持 Rectangle 类 struct 的计划文档

## 1. 目标

使 `make-readonly` 命令能够正确处理 `Rectangle.cs` 这类具有接口实现的可变 struct。

## 2. 实现步骤

### Step 1: 修复 `IsGenuinelyVirtualOrAbstract` 方法

**文件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`

**修改**:
- 在 `IsGenuinelyVirtualOrAbstract` 方法中增加对显式和隐式接口实现的排除
- 显式接口实现检查：`method.ExplicitInterfaceImplementations.Any()`
- 隐式接口实现检查：遍历 `containingType.AllInterfaces`，使用 `FindImplementationForInterfaceMember` 查找实现

**风险评估**: 低 - 只增加排除条件，不影响现有逻辑

### Step 2: 提取 `ImplicitFieldToResultRewriter` 为独立类

**文件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs`

**修改**:
- 将 `ImplicitFieldToResultRewriter` 从 `MethodMigrator` 的私有嵌套类提取为独立的 `internal` 类
- 扩展构造函数接受 `propertyNames` 参数
- 修改 `VisitIdentifierName` 同时检查字段名和属性名

**风险评估**: 低 - 重构，不改变行为

### Step 3: 修改 `MethodMigrator` 构造函数

**文件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/MethodMigrator.cs`

**修改**:
- 增加 `propertyNames` 参数
- 传递 `propertyNames` 给 `ImplicitFieldToResultRewriter`

**风险评估**: 低 - 扩展参数

### Step 4: 修改 `ApplyMethodMigrate` 方法

**文件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs`

**修改**:
- 收集属性名（不只是字段名）
- 传递属性名给 `MethodMigrator`
- 在属性 setter 迁移中，使用 `ImplicitFieldToResultRewriter` 替换 setter body 中的访问

**风险评估**: 中 - 影响属性 setter 迁移逻辑

### Step 5: 修复属性 setter 迁移（补充）

**重要**: 对于 auto-property（无 setter body 的 property），现有代码生成的 `WithXxx` 方法使用 `prop.Identifier.Text = value`，迁移后仍修改 `this` 而非 `result`。这是一个已有的 bug，但本修复不解决它（因为 Rectangle 的所有 property 都有显式 setter body）。

**Rectangle 的 property 分析**:
- `Left`, `Right`, `Top`, `Bottom` - 有显式 setter body，访问字段
- `Width`, `Height` - 有显式 setter body，访问字段
- `LeftTop`, `RightBottom` - 有显式 setter body，访问字段
- `Size` - 有显式 setter body，访问 `Width`/`Height` 属性
- `Center` - 有显式 setter body，访问 `Center`/`LeftTop`/`RightBottom` 属性

所有 property 都有显式 setter body，因此本修复适用。

### Step 6: 添加单元测试

**文件**: `tests/CSharpToJava.Tests/ReadOnlyStructMakerTests.cs`

**新增测试**:
1. `ExplicitInterfaceImpl_WithMutatingMethod_IsMigrated` - 显式接口实现 + 可变方法
2. `ImplicitInterfaceImpl_WithMutatingMethod_IsMigrated` - 隐式接口实现 + 可变方法
3. `InterfaceImpl_OnlyPureOverrides_IsDirectConverted` - 只有纯接口实现的 struct
4. `RectangleLikeStruct_FullMigration` - Rectangle 类 struct 的完整迁移
5. `PropertyAccess_MigratedToResultProperty` - 属性访问被正确迁移到 result

### Step 7: 运行所有测试验证无回归

**命令**:
```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMaker"
```

## 3. 文件修改清单

| 文件 | 修改类型 | 说明 |
|------|---------|------|
| `StructAnalyzer.cs` | 修改 | 修复 `IsGenuinelyVirtualOrAbstract` |
| `MethodMigrator.cs` | 重构+修改 | 提取 `ImplicitFieldToResultRewriter`，增加 `propertyNames` |
| `ReadOnlyStructRewriter.cs` | 修改 | 收集属性名，修复属性 setter 迁移 |
| `ReadOnlyStructMakerTests.cs` | 新增测试 | 添加接口实现和属性访问测试 |

## 4. 验证标准

1. 所有现有单元测试通过
2. 新增测试全部通过
3. 对 `Rectangle.cs` 执行 `make-readonly` 不产生 `has virtual/override methods` 警告
4. 生成的代码语义正确（可变方法返回新 struct，调用点正确更新）
