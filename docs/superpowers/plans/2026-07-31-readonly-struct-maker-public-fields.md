# Implementation Plan: ReadOnlyStructMaker L4 Public Field Conversion

Date: 2026-07-31
Spec: `docs/superpowers/specs/2026-07-31-readonly-struct-maker-design-v3-public-fields.md`

## Goal & Scope

Add L4 (PublicFieldToProperty) conversion level to support structs like Point.cs that have public fields assigned externally.

## Implementation Steps

### Task 1: 扩展枚举和选项

**Files:**
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs`
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs`

- [ ] **Step 1: 添加新的 ConversionLevel 枚举值**

在 `ReadOnlyStructMakerDiagnostics.cs` 中添加:
```csharp
public enum ConversionLevel {
    Skip,                   // L0
    DirectAdd,              // L1
    PropertyConvert,        // L2
    DataContainer,          // L3
    PublicFieldToProperty,  // L4 (新增)
    MethodMigrate,          // L5
    NotConvertible,         // L7
}
```

- [ ] **Step 2: 添加新的 StructPattern 枚举值**

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

- [ ] **Step 3: 添加新的统计字段**

```csharp
public sealed class ReadOnlyStructMakerStatistics {
    // ... 现有字段 ...
    public int Level4_PublicFieldToProperty;
    public int PublicFieldsConverted;
    public int CallSitesRewritten;
}
```

- [ ] **Step 4: 添加新的选项**

```csharp
public sealed class ReadOnlyStructMakerOptions {
    // ... 现有选项 ...
    public bool EnablePublicFieldConversion { get; init; } = true;
}
```

- [ ] **Step 5: 运行测试验证**

```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMakerTests"
```

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/
git commit -m "feat(readonly-struct): add L4 PublicFieldToProperty enum and options"
```

---

### Task 2: 扩展 StructAnalyzer 分析逻辑

**Files:**
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`

- [ ] **Step 1: 添加公共字段检测逻辑**

在 `Analyze` 方法中，在现有 Pattern D 检测之前添加:

```csharp
// Pattern E: 有公共字段但无 mutating 方法
if (fields.Any(f => f.DeclaredAccessibility == Accessibility.PublicKey) && 
    mutatingMethods.Count == 0) {
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

- [ ] **Step 2: 运行测试验证**

```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMakerTests"
```

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs
git commit -m "feat(readonly-struct): L4 detection in StructAnalyzer"
```

---

### Task 3: 实现 PublicFieldToProperty 重写器

**Files:**
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs`

- [ ] **Step 1: 添加 ApplyPublicFieldToProperty 方法**

```csharp
private StructDeclarationSyntax ApplyPublicFieldToProperty(StructDeclarationSyntax node) {
    var structName = node.Identifier.Text;
    var publicFields = node.Members.OfType<FieldDeclarationSyntax>()
        .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        .ToList();
    
    if (publicFields.Count == 0) return node;
    
    var newMembers = new SyntaxList<MemberDeclarationSyntax>();
    var withMethods = new List<MethodDeclarationSyntax>();
    
    foreach (var member in node.Members) {
        if (member is FieldDeclarationSyntax field && 
            field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) {
            foreach (var variable in field.Declaration.Variables) {
                // 转为 get-only 属性
                var prop = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithAccessorList(SyntaxFactory.AccessorList(
                        SyntaxFactory.SingletonList(
                            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
                newMembers = newMembers.Add(prop);
                
                // 生成 WithXxx 方法
                withMethods.Add(GenerateWithMethod(structName, variable.Identifier.Text, field.Declaration.Type));
            }
        } else {
            newMembers = newMembers.Add(member);
        }
    }
    
    var result = node.WithMembers(newMembers);
    foreach (var method in withMethods) {
        result = result.AddMembers(method);
    }
    
    return result;
}

private static MethodDeclarationSyntax GenerateWithMethod(string structName, string fieldName, TypeSyntax fieldType) {
    var withMethodName = "With" + fieldName;
    var paramName = Char.ToLowerInvariant(fieldName[0]) + fieldName[1..];
    
    var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(fieldType);
    
    var body = SyntaxFactory.Block(
        SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator("result")
                        .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ThisExpression()))))),
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

- [ ] **Step 2: 在 VisitStructDeclaration 中添加 L4 处理**

```csharp
var rewritten = result.Level switch {
    ConversionLevel.DirectAdd => ApplyDirectAdd(node),
    ConversionLevel.PropertyConvert => ApplyPropertyConvert(node),
    ConversionLevel.DataContainer => ApplyDataContainer(node),
    ConversionLevel.PublicFieldToProperty => ApplyPublicFieldToProperty(node),
    ConversionLevel.MethodMigrate => ApplyMethodMigrate(node, result),
    _ => node
};
```

- [ ] **Step 3: 运行测试验证**

```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMakerTests"
```

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs
git commit -m "feat(readonly-struct): L4 PublicFieldToProperty rewriter"
```

---

### Task 4: 实现调用点更新器

**Files:**
- Create: `src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs`

- [ ] **Step 1: 创建 AssignmentRewriter 类**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Rewrites external field assignments to WithXxx method calls:
/// - obj.Field = value → obj = obj.WithField(value)
/// - obj.Field += value → obj = obj.WithField(obj.Field + value)
/// - obj.Field++ → obj = obj.WithField(obj.Field + 1)
/// </summary>
public sealed class AssignmentRewriter : CSharpSyntaxRewriter {
    private readonly Dictionary<string, HashSet<string>> _structFields; // structName -> fieldNames
    private readonly SemanticModel _model;
    
    public AssignmentRewriter(Dictionary<string, HashSet<string>> structFields, SemanticModel model) {
        _structFields = structFields;
        _model = model;
    }
    
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node) {
        if (node.Left is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitAssignmentExpression(node);
        
        if (memberAccess.Name is not IdentifierNameSyntax fieldName)
            return base.VisitAssignmentExpression(node);
        
        var fieldNameText = fieldName.Identifier.Text;
        var receiverType = _model.GetTypeInfo(memberAccess.Expression).Type;
        
        if (receiverType == null)
            return base.VisitAssignmentExpression(node);
        
        // 检查是否是目标 struct 的公共字段
        if (!_structFields.TryGetValue(receiverType.Name, out var fields) || !fields.Contains(fieldNameText))
            return base.VisitAssignmentExpression(node);
        
        var withMethodName = "With" + fieldNameText;
        var receiver = memberAccess.Expression;
        
        // 处理不同类型的赋值
        var newValue = node.Kind() switch {
            SyntaxKind.SimpleAssignmentExpression => node.Right,
            SyntaxKind.AddAssignmentExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.AddExpression, memberAccess, node.Right),
            SyntaxKind.SubtractAssignmentExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.SubtractExpression, memberAccess, node.Right),
            SyntaxKind.MultiplyAssignmentExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.MultiplyExpression, memberAccess, node.Right),
            SyntaxKind.DivideAssignmentExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.DivideExpression, memberAccess, node.Right),
            _ => null
        };
        
        if (newValue == null)
            return base.VisitAssignmentExpression(node);
        
        // 生成: receiver = receiver.WithField(newValue)
        var withCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName(withMethodName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(newValue))));
        
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            receiver.WithoutTrivia(),
            withCall).WithTriviaFrom(node);
    }
    
    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node) {
        if (node.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
            return base.VisitPrefixUnaryExpression(node);
        
        if (node.Operand is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitPrefixUnaryExpression(node);
        
        // 类似处理...
        return HandleUnaryMutation(memberAccess, node, node.Kind() == SyntaxKind.PreIncrementExpression);
    }
    
    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node) {
        if (node.Kind() is not (SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression))
            return base.VisitPostfixUnaryExpression(node);
        
        if (node.Operand is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitPostfixUnaryExpression(node);
        
        return HandleUnaryMutation(memberAccess, node, node.Kind() == SyntaxKind.PostIncrementExpression);
    }
    
    private SyntaxNode? HandleUnaryMutation(MemberAccessExpressionSyntax memberAccess, 
        ExpressionSyntax originalNode, bool isIncrement) {
        if (memberAccess.Name is not IdentifierNameSyntax fieldName)
            return null;
        
        var fieldNameText = fieldName.Identifier.Text;
        var receiverType = _model.GetTypeInfo(memberAccess.Expression).Type;
        
        if (receiverType == null || !_structFields.TryGetValue(receiverType.Name, out var fields) || !fields.Contains(fieldNameText))
            return null;
        
        var withMethodName = "With" + fieldNameText;
        var receiver = memberAccess.Expression;
        
        // obj.Field++ → obj = obj.WithField(obj.Field + 1)
        var increment = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(1));
        
        var newValue = SyntaxFactory.BinaryExpression(
            isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
            memberAccess,
            increment);
        
        var withCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName(withMethodName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(newValue))));
        
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            receiver.WithoutTrivia(),
            withCall).WithTriviaFrom(originalNode);
    }
}
```

- [ ] **Step 2: 在 ReadOnlyStructMaker 中集成 AssignmentRewriter**

修改 `ReadOnlyStructMaker.cs`:

```csharp
// 在 MakeReadOnly 方法中，L4 转换后添加调用点更新
if (options.EnablePublicFieldConversion) {
    var publicFieldStructs = new Dictionary<string, HashSet<string>>();
    
    foreach (var (structSyntax, result) in analysisResults.Where(kv => kv.Value.Level == ConversionLevel.PublicFieldToProperty)) {
        var structName = structSyntax.Identifier.Text;
        var publicFields = structSyntax.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
            .ToHashSet();
        publicFieldStructs[structName] = publicFields;
    }
    
    if (publicFieldStructs.Count > 0) {
        var assignmentRewriter = new AssignmentRewriter(publicFieldStructs, semanticModel);
        newRoot = (CompilationUnitSyntax)assignmentRewriter.Visit(newRoot)!;
    }
}
```

- [ ] **Step 3: 运行测试验证**

```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMakerTests"
```

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/
git commit -m "feat(readonly-struct): L4 assignment call site rewriter"
```

---

### Task 5: 实现对象初始化器转换

**Files:**
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs`

- [ ] **Step 1: 添加 VisitObjectCreationExpression 方法**

```csharp
public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node) {
    if (node.Initializer == null || !node.Initializer.Expressions.Any())
        return base.VisitObjectCreationExpression(node);
    
    var typeSymbol = _model.GetTypeInfo(node.Type).Type;
    if (typeSymbol == null || !_structFields.ContainsKey(typeSymbol.Name))
        return base.VisitObjectCreationExpression(node);
    
    var fields = _structFields[typeSymbol.Name];
    var hasPublicFieldInit = node.Initializer.Expressions
        .OfType<AssignmentExpressionSyntax>()
        .Any(a => a.Left is IdentifierNameSyntax id && fields.Contains(id.Identifier.Text));
    
    if (!hasPublicFieldInit)
        return base.VisitObjectCreationExpression(node);
    
    // 提取字段名到参数名的映射
    var arguments = new List<ArgumentSyntax>();
    foreach (var expr in node.Initializer.Expressions.OfType<AssignmentExpressionSyntax>()) {
        if (expr.Left is IdentifierNameSyntax id && fields.Contains(id.Identifier.Text)) {
            var paramName = ToCamelCase(id.Identifier.Text);
            arguments.Add(SyntaxFactory.Argument(expr.Right)
                .WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(paramName))));
        }
    }
    
    return node.WithInitializer(null)
        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
}

private static string ToCamelCase(string name) {
    if (string.IsNullOrEmpty(name)) return name;
    return char.ToLowerInvariant(name[0]) + name[1..];
}
```

- [ ] **Step 2: 运行测试验证**

```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMakerTests"
```

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs
git commit -m "feat(readonly-struct): L4 object initializer conversion"
```

---

### Task 6: 更新统计和诊断

**Files:**
- Modify: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs`

- [ ] **Step 1: 更新统计逻辑**

```csharp
if (changed) {
    foreach (var (syntax, result) in analysisResults.Where(kv => kv.Value.ShouldRewrite)) {
        stats.StructsConverted++;
        switch (result.Level) {
            case ConversionLevel.DirectAdd: stats.Level1_DirectAdd++; break;
            case ConversionLevel.PropertyConvert: stats.Level2_PropertyConvert++; break;
            case ConversionLevel.DataContainer: stats.Level3_DataContainer++; break;
            case ConversionLevel.PublicFieldToProperty: stats.Level4_PublicFieldToProperty++; break;
            case ConversionLevel.MethodMigrate: stats.Level5_MethodMigrate++; break;
        }
        // 统计公共字段数量
        if (result.Level == ConversionLevel.PublicFieldToProperty) {
            var publicFieldCount = syntax.Members.OfType<FieldDeclarationSyntax>()
                .Count(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)));
            stats.PublicFieldsConverted += publicFieldCount;
        }
        diagnostics.Add(new(ReadOnlyStructSeverity.Info, result.QualifiedName ?? "?",
            result.Reason, result.Level, result.Pattern));
    }
}
```

- [ ] **Step 2: 运行测试验证**

```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMakerTests"
```

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs
git commit -m "feat(readonly-struct): L4 statistics tracking"
```

---

### Task 7: 编写单元测试

**Files:**
- Create: `tests/CSharpToJava.Tests/ReadOnlyStructMakerL4Tests.cs`

- [ ] **Step 1: 编写基础测试**

```csharp
using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public class ReadOnlyStructMakerL4Tests {
    private static ReadOnlyStructMakerResult RunMaker(string source, ReadOnlyStructMakerOptions? options = null) {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Core.ReadOnlyStructMaker.ReadOnlyStructMaker()
            .MakeReadOnly(tree, compilation.GetSemanticModel(tree), options);
    }

    [Fact]
    public void PublicFields_SimpleAssignment_Converted() {
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}
class User {
    void M() {
        var p = new Point();
        p.X = 5;
        p.Y = 10;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Point", result.OutputCode);
        Assert.Contains("p = p.WithX(", result.OutputCode);
        Assert.Contains("p = p.WithY(", result.OutputCode);
    }

    [Fact]
    public void PublicFields_ObjectInitializer_Converted() {
        var src = @"
struct Point {
    public double X;
    public double Y;
}
class User {
    void M() {
        var p = new Point { X = 5, Y = 10 };
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("new Point(x: 5, y: 10)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_CompoundAssignment_Converted() {
        var src = @"
struct Counter {
    public int Value;
    public Counter(int v) { Value = v; }
}
class User {
    void M() {
        var c = new Counter(0);
        c.Value += 5;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("c = c.WithValue(c.Value + 5)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_Increment_Converted() {
        var src = @"
struct Counter {
    public int Value;
    public Counter(int v) { Value = v; }
}
class User {
    void M() {
        var c = new Counter(0);
        c.Value++;
    }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("c = c.WithValue(c.Value + 1)", result.OutputCode);
    }

    [Fact]
    public void PublicFields_DisabledByOption_NoChange() {
        var src = @"
struct Point {
    public double X;
    public double Y;
}";
        var opts = new ReadOnlyStructMakerOptions { EnablePublicFieldConversion = false };
        var result = RunMaker(src, opts);
        Assert.False(result.Changed);
    }

    [Fact]
    public void PublicFields_WithMutatingMethod_NotL4() {
        var src = @"
struct S {
    public int X;
    public void Increment() { X++; }
}";
        var result = RunMaker(src);
        // 有 mutating 方法，应该走 L5 或 L7
        Assert.Contains(result.Diagnostics, d => 
            d.Level == ConversionLevel.MethodMigrate || d.Level == ConversionLevel.NotConvertible);
    }

    [Fact]
    public void PublicFields_StatisticsTracked() {
        var src = @"
struct Point {
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Level4_PublicFieldToProperty);
        Assert.Equal(2, result.Statistics.PublicFieldsConverted);
    }
}
```

- [ ] **Step 2: 运行测试**

```bash
dotnet test --filter "FullyQualifiedName~ReadOnlyStructMakerL4Tests"
```

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/ReadOnlyStructMakerL4Tests.cs
git commit -m "test(readonly-struct): L4 PublicFieldToProperty tests"
```

---

### Task 8: 端到端验证

- [ ] **Step 1: 运行所有测试**

```bash
dotnet test
```

- [ ] **Step 2: 验证 Point.cs 转换**

```bash
dotnet run --project src/CSharpToJava.CLI -- make-readonly -i "E:\agl-master\GraphLayout\MSAGL\Core\Geometry\Point.cs" -o Point_converted.cs
```

- [ ] **Step 3: 检查输出**

验证输出文件包含:
- `readonly struct Point`
- `public double X { get; }`
- `public double Y { get; }`
- `public Point WithX(double x)` 方法
- `public Point WithY(double y)` 方法

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(readonly-struct): L4 PublicFieldToProperty complete"
```

---

## Files Summary

| 操作 | 文件 |
|------|------|
| Modify | `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerDiagnostics.cs` |
| Modify | `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs` |
| Modify | `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs` |
| Modify | `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs` |
| Create | `src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs` |
| Modify | `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs` |
| Create | `tests/CSharpToJava.Tests/ReadOnlyStructMakerL4Tests.cs` |
