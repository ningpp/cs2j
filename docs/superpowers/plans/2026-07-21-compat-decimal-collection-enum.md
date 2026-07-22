# cs2j compat 桥接修复计划：Decimal / 集合 / 枚举运算符

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 通过补齐 `io.github.ningpp.compat` 桥接逻辑和 `CSharpToJava.Core` 转换器规则，消除 `d:\cs-xml-20260716` Maven 构建中剩余的 Decimal 赋值/运算、集合接口转换、枚举/事件运算符三类编译错误。

**Architecture:** 在 compat 层扩展类型适配器和接口包装方法，在转换器层通过语义模型识别 C# 特殊类型（Decimal、ICollection/IList、Flags enum、event）并生成对应的 Java 桥接调用；每个 Task 都遵循"写失败测试 → 最小实现 → Maven 验证 → 提交"的闭环。

**Tech Stack:** C# 10 / .NET 8 (Roslyn)、Java 17、Maven、xUnit、自定义 `io.github.ningpp.compat` 兼容库。

---

## 当前错误基线

运行 `cd d:\cs-xml-20260716; mvn clean package -e -l d:\code\cs2j\mvn-build.log` 后，剩余约 **308** 条 `[ERROR]`。本次计划聚焦以下三类：

1. **Decimal**
   - `int 无法转换为 io.github.ningpp.compat.Decimal`（`Compiler.java:1475` 等）
   - `Decimal * Decimal` 仍生成 `*` 导致"二元运算符 `*` 的操作数类型错误"（`SchemaCollectionCompiler.java:1118` 等）
2. **集合接口**
   - `XmlSchemaObjectCollection 无法转换为 CSharpICollection<?>`（`XmlSchemaInference.java:232` 等）
   - `java.util.Collection<Object> 无法转换为 CSharpICollection<?>`（`NameTable.java:60`）
   - `CSharpList<capture#1,?> 无法转换为 CSharpGenericIList<Object>`（`XmlSchemas.java:293`）
3. **枚举 / 事件运算符**
   - `Object & 0xFF`（`Datatype_unsignedByte.java:44`）
   - `(flags.getValue() & SoapAttributeFlags.Attribute.getValue()) == SoapAttributeFlags.Attribute`（`SoapReflectionImporter.java:605`）
   - `XmlAttributeFlags != 0`（`XmlReflectionImporter.java:323` 等）
   - `_events.getOnUnknownNode() + handler`（`XmlSerializer.java:332` 等，C# event `+=/-=` 被错误转成 `+`/`-`）

---

## Phase 1: Decimal

### Task 1: 修复 Decimal 二元运算符未被桥接的问题

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

**Context:**
`BinaryExpressionTransformer.TryTransformDecimalBinaryExpression` 已经存在，但 `SchemaCollectionCompiler.java:1118` 中的 `baseParticle.getMinOccurs() * baseGroupBase.getMinOccurs()` 没有被转成 `.multiply()`。原因是 `IsDecimalExpression` 只判断 `SpecialType == System_Decimal`，没有处理 `Nullable<Decimal>`；另外当 Roslyn 语义模型只返回转换后类型（ConvertedType）时也可能漏判。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Decimal_MultiplyNullableDecimal_BridgedToMethodCall()
{
    var result = Convert("""
        using System;
        using System.Xml.Schema;

        class Sample
        {
            Decimal Calc(XmlSchemaParticle p1, XmlSchemaParticle p2)
            {
                return p1.MinOccurs * p2.MinOccurs;
            }
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains(".multiply(", result.GeneratedCode);
    Assert.DoesNotContain(" * ", result.GeneratedCode);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~Decimal_MultiplyNullableDecimal_BridgedToMethodCall"`
Expected: FAIL，生成代码仍为 `p1.getMinOccurs() * p2.getMinOccurs()`

- [ ] **Step 3: 修复 Decimal 检测逻辑**

在 `BinaryExpressionTransformer.cs` 中，修改 `IsDecimalExpression` 以展开 `Nullable<T>`：

```csharp
private static bool IsDecimalExpression(ExpressionSyntax expression, ConversionContext context)
    => context.SemanticModel != null
        && ExpressionTransformerHelpers.IsDecimalType(context.GetTypeInfo(expression).Type);
```

（`ExpressionTransformerHelpers.IsDecimalType` 已经会 `UnwrapNullable`，所以直接复用它。）

同时确保 `TryTransformDecimalBinaryExpression` 的入口不被提前返回阻断。检查 `TransformBinaryExpression` 中 `TryTransformDecimalBinaryExpression` 调用位置是否在 `return $"{left} {op} {right}"` 之前，且 `left`/`right` 未被 `WrapOperandIfNeeded` 提前包装导致方法调用失效。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~Decimal_MultiplyNullableDecimal_BridgedToMethodCall"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs
git commit -m "fix(transformer): bridge Decimal*Decimal even when operands come from Nullable<Decimal>"
```

---

### Task 2: 修复 int 字面量到 Decimal 的赋值转换

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs`
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`（如需要）
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

**Context:**
`Compiler.java:1475` 生成 `minOccurs.value = 0;`，其中 `.value` 是 `ObjectHolder<Decimal>` 的 `Decimal` 类型字段。C# 源码中 `minOccurs.Value = 0` 是 Decimal 属性被赋 int 字面量。Java 没有隐式转换，需要生成 `minOccurs.value = new Decimal(0);` 或 `Decimal.valueOf(0)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Decimal_AssignIntLiteral_WrappedInDecimalConstructor()
{
    var result = Convert("""
        using System;

        class Sample
        {
            void M(decimal d)
            {
                d = 0;
            }
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Matches(@"d\s*=\s*(new\s+Decimal\s*\(\s*0\s*\)|Decimal\.valueOf\s*\(\s*0\s*\))", result.GeneratedCode);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~Decimal_AssignIntLiteral_WrappedInDecimalConstructor"`
Expected: FAIL，生成 `d = 0;`

- [ ] **Step 3: 实现赋值侧 Decimal 包装**

在 `AssignmentTransformer.TransformAssignment` 中，对 `=` 右侧进行目标类型检测：

```csharp
// Inside TransformAssignment, before rendering the RHS for simple assignments
if (op == "=" && context.SemanticModel != null)
{
    var leftType = context.GetTypeInfo(leftNode).Type;
    var rightType = context.GetTypeInfo(rightNode).Type;
    if (ExpressionTransformerHelpers.IsDecimalType(leftType)
        && !ExpressionTransformerHelpers.IsDecimalType(rightType)
        && ExpressionTransformerHelpers.IsNumericOrCharType(rightType))
    {
        var rightExpr = facade.Transform(rightNode, context);
        rightExpr = ExpressionTransformerHelpers.ToDecimalExpression(rightNode, rightExpr, rightType);
        // ... render assignment using wrapped rightExpr
    }
}
```

注意：不要破坏 `ObjectHolder<Decimal>.value = ...` 这种通过 MemberAccess 的赋值。该逻辑同样适用，因为 `leftNode` 的 `TypeInfo.Type` 仍是 `Decimal`。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~Decimal_AssignIntLiteral_WrappedInDecimalConstructor"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs
git commit -m "fix(transformer): wrap int assignments to Decimal variables in Decimal.valueOf"
```

---

## Phase 2: 集合接口适配

### Task 3: 让 CSharpICollection.from 支持任意 java.util.Collection

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpICollection.java`
- Test: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpICollectionTest.java`（如存在；否则在 `tests` 下新建或复用已有测试项目）

**Context:**
`CSharpICollection.from(Object)` 目前只接受 `CSharpICollection` 或 `CSharpCollection`。生成代码会把 `XmlSchemaObjectCollection`（C# 自定义集合，Java 中是一个普通类）和 `java.util.Collection` 直接赋值给 `CSharpICollection<?>`，需要包装。

- [ ] **Step 1: 写失败测试**

```java
@Test
void from_javaUtilCollection_adaptsToCSharpICollection() {
    java.util.List<String> list = java.util.Arrays.asList("a", "b");
    CSharpICollection<Object> adapted = CSharpICollection.from(list);
    assertEquals(2, adapted.getCount());
    assertTrue(adapted.contains("a"));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `mvn test -pl java/csharptojava-compat -Dtest=CSharpICollectionTest`
Expected: FAIL，`IllegalArgumentException: Unsupported collection type`

- [ ] **Step 3: 扩展 from 方法**

在 `CSharpICollection.java` 中，在 `from` 方法里增加 `java.util.Collection` 分支：

```java
static CSharpICollection<Object> from(Object collection) {
    if (collection == null) {
        return null;
    }
    if (collection instanceof CSharpICollection) {
        @SuppressWarnings("unchecked")
        CSharpICollection<Object> result = (CSharpICollection<Object>) collection;
        return result;
    }
    if (collection instanceof CSharpCollection) {
        return fromCSharpCollection((CSharpCollection) collection);
    }
    if (collection instanceof java.util.Collection) {
        return fromJavaCollection((java.util.Collection<?>) collection);
    }
    throw new IllegalArgumentException("Unsupported collection type: " + collection.getClass().getName());
}

static CSharpICollection<Object> fromJavaCollection(java.util.Collection<?> collection) {
    return new CSharpICollection<Object>() {
        @Override public CSharpGenericEnumerator<Object> iterator() {
            return CSharpGenericEnumerator.from(collection.iterator());
        }
        @Override public int getCount() { return collection.size(); }
        @Override public boolean contains(Object o) { return collection.contains(o); }
        @Override public boolean add(Object item) { return collection.add(item); }
        @Override public boolean remove(Object o) { return collection.remove(o); }
        @Override public void clear() { collection.clear(); }
    };
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `mvn test -pl java/csharptojava-compat -Dtest=CSharpICollectionTest`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpICollection.java java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpICollectionTest.java
git commit -m "feat(compat): CSharpICollection.from adapts java.util.Collection"
```

---

### Task 4: 转换器对 ICollection 赋值/参数生成 CSharpICollection.from

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs`
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（如参数需要）
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

**Context:**
即使 `CSharpICollection.from` 支持了 `java.util.Collection`，如果生成代码写成 `CSharpICollection<?> c = someList;` 仍然编译失败。转换器必须在目标类型为 `CSharpICollection<?>` 且右侧不是其子类型时生成 `CSharpICollection.from(rhs)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void ICollection_Assignment_FromList_WrappedWithFrom()
{
    var result = Convert("""
        using System;
        using System.Collections;
        using System.Collections.Generic;

        class Sample
        {
            void M(List<int> list)
            {
                ICollection c = list;
            }
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains("CSharpICollection.from(list)", result.GeneratedCode);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~ICollection_Assignment_FromList_WrappedWithFrom"`
Expected: FAIL，生成 `CSharpICollection<?> c = list;`

- [ ] **Step 3: 在 AssignmentTransformer 中插入 from 包装**

在 `TransformAssignment` 的 `=` 分支中，检测左侧目标类型：

```csharp
if (op == "=" && context.SemanticModel != null)
{
    var leftType = context.GetTypeInfo(leftNode).ConvertedType ?? context.GetTypeInfo(leftNode).Type;
    if (leftType != null
        && (leftType.ToDisplayString() == "System.Collections.ICollection"
            || leftType.ToDisplayString().StartsWith("System.Collections.Generic.ICollection<")))
    {
        var rightType = context.GetTypeInfo(rightNode).Type;
        if (rightType != null && !IsAssignableToCSharpICollection(rightType, context))
        {
            context.AddImport("io.github.ningpp.compat.CSharpICollection");
            var rightExpr = facade.Transform(rightNode, context);
            return $"{left} = CSharpICollection.from({rightExpr})";
        }
    }
}
```

其中辅助方法判断右侧类型是否已经是 `CSharpICollection` 兼容子类型：

```csharp
private static bool IsAssignableToCSharpICollection(ITypeSymbol type, ConversionContext context)
{
    var javaType = context.MapType(type);
    return javaType.Contains("CSharpICollection") || javaType.Contains("CSharpCollection");
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~ICollection_Assignment_FromList_WrappedWithFrom"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs
git commit -m "fix(transformer): wrap ICollection assignments in CSharpICollection.from"
```

---

### Task 5: 转换器对 IList 赋值生成 CSharpGenericIList.from

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

**Context:**
`XmlSchemas.java:293` 出现 `CSharpList<capture#1,?> 无法转换为 CSharpGenericIList<Object>`。C# `System.Collections.IList` 映射到 `CSharpGenericIList<Object>`，而具体列表类型（如 `CSharpList<T>`）因 Java 泛型不变性无法直接赋值。转换器需要生成 `CSharpGenericIList.from(rhs)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void IList_Assignment_FromCSharpList_WrappedWithFrom()
{
    var result = Convert("""
        using System;
        using System.Collections;
        using System.Collections.Generic;

        class Sample
        {
            void M(List<string> list)
            {
                IList ilist = list;
            }
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains("CSharpGenericIList.from(list)", result.GeneratedCode);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~IList_Assignment_FromCSharpList_WrappedWithFrom"`
Expected: FAIL

- [ ] **Step 3: 在 AssignmentTransformer 中插入 IList 包装**

与 Task 4 类似，检测左侧类型为 `System.Collections.IList` 或 `System.Collections.Generic.IList<T>` 时，若右侧不是 `CSharpGenericIList` 兼容类型，则生成 `CSharpGenericIList.from(rhs)`。

```csharp
if (leftType != null
    && (leftType.ToDisplayString() == "System.Collections.IList"
        || leftType.ToDisplayString().StartsWith("System.Collections.Generic.IList<")))
{
    var rightType = context.GetTypeInfo(rightNode).Type;
    if (rightType != null && !IsAssignableToCSharpGenericIList(rightType, context))
    {
        context.AddImport("io.github.ningpp.compat.CSharpGenericIList");
        var rightExpr = facade.Transform(rightNode, context);
        return $"{left} = CSharpGenericIList.from({rightExpr})";
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~IList_Assignment_FromCSharpList_WrappedWithFrom"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs
git commit -m "fix(transformer): wrap IList assignments in CSharpGenericIList.from"
```

---

## Phase 3: 枚举与事件运算符

### Task 6: 修复 Object & int（无符号字节比较）

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

**Context:**
`Datatype_unsignedByte.java:44` 生成 `Integer.compare(((value1 & 0xFF)), (value2 & 0xFF))`，其中 `value1`/`value2` 是 `Object`。C# 源码中它们实际是被装箱的 `byte/sbyte`，与 `0xFF` 进行按位与。转换器需要把 `Object & int` 转成 `(((Number)value1).intValue() & 0xFF)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Object_BitwiseAnd_Int_CastToNumber()
{
    var result = Convert("""
        using System;

        class Sample
        {
            int Compare(object v1, object v2)
            {
                return ((v1 & 0xFF)).CompareTo((v2 & 0xFF));
            }
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains("((Number)v1).intValue() & 0xFF", result.GeneratedCode);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~Object_BitwiseAnd_Int_CastToNumber"`
Expected: FAIL

- [ ] **Step 3: 在按位与分支中处理 Object 左操作数**

在 `TransformBinaryExpression` 的 `IsBitwiseOp(op)` 路径中（或在 `TryTransformEnumBitwiseOperation` 之后），增加：

```csharp
if (op == "&" && context.SemanticModel != null)
{
    var leftType = context.GetTypeInfo(node.Left).Type;
    var rightType = context.GetTypeInfo(node.Right).Type;
    if (leftType?.SpecialType == SpecialType.System_Object
        && ExpressionTransformerHelpers.IsNumericOrCharType(rightType))
    {
        var leftExpr = facade.Transform(node.Left, context);
        var rightExpr = facade.Transform(node.Right, context);
        return $"(((Number){leftExpr}).intValue() & {rightExpr})";
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~Object_BitwiseAnd_Int_CastToNumber"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs
git commit -m "fix(transformer): cast boxed Object to Number before bitwise-and with int"
```

---

### Task 7: 修复 Flags enum 比较（intValue & enumValue == enumValue）

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

**Context:**
`SoapReflectionImporter.java:605` 生成 `(flags.getValue() & SoapAttributeFlags.Attribute.getValue()) == SoapAttributeFlags.Attribute`。右侧是 enum，左侧是 int。Java 中 `==` 不能比较 `int` 与 enum 实例。应转成 `(flags.getValue() & SoapAttributeFlags.Attribute.getValue()) == SoapAttributeFlags.Attribute.getValue()`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void FlagsEnum_IntComparedToEnum_ComparesValues()
{
    var result = Convert("""
        using System;

        [Flags]
        enum MyFlags { A = 1, B = 2 }

        class Sample
        {
            bool Check(MyFlags flags)
            {
                return (flags & MyFlags.A) == MyFlags.A;
            }
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains(".getValue() == MyFlags.A.getValue()", result.GeneratedCode);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~FlagsEnum_IntComparedToEnum_ComparesValues"`
Expected: FAIL

- [ ] **Step 3: 在等号比较中补齐 enum 值比较**

在 `TransformBinaryExpression` 的 `==`/`!=` 分支（已有 string、pointer、event 比较之后），检测一侧为 `int`/`long`/numeric、另一侧为 enum 的情况：

```csharp
if ((op == "==" || op == "!=") && context.SemanticModel != null)
{
    var leftType = context.GetTypeInfo(node.Left).Type as INamedTypeSymbol;
    var rightType = context.GetTypeInfo(node.Right).Type as INamedTypeSymbol;
    bool leftIsEnum = leftType?.TypeKind == TypeKind.Enum;
    bool rightIsEnum = rightType?.TypeKind == TypeKind.Enum;
    bool leftIsNumeric = ExpressionTransformerHelpers.IsNumericOrCharType(leftType);
    bool rightIsNumeric = ExpressionTransformerHelpers.IsNumericOrCharType(rightType);

    if ((leftIsEnum && rightIsNumeric) || (leftIsNumeric && rightIsEnum))
    {
        var leftExpr = facade.Transform(node.Left, context);
        var rightExpr = facade.Transform(node.Right, context);
        if (leftIsEnum) leftExpr = $"{leftExpr}.getValue()";
        if (rightIsEnum) rightExpr = $"{rightExpr}.getValue()";
        return op == "=="
            ? $"{leftExpr} == {rightExpr}"
            : $"{leftExpr} != {rightExpr}";
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~FlagsEnum_IntComparedToEnum_ComparesValues"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs
git commit -m "fix(transformer): compare enum values when enum is compared with numeric"
```

---

### Task 8: 修复 event +=/-= 在字段访问上的处理

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

**Context:**
`XmlSerializer.java:332` 生成 `_events.getOnUnknownNode() + handler`，因为 C# 源码是 `_events.OnUnknownNode += handler`。转换器目前只在 `leftNode is MemberAccessExpressionSyntax` 时处理 event `+=/-=`，但这里左侧是 `_events.OnUnknownNode`（MemberAccess），应该被识别为 event。问题可能是 `_events` 字段类型中的 event 没有被 Roslyn 解析为 `IEventSymbol`，或者事件访问被当作属性访问。

需要调试具体原因，但至少需要在 `MemberAccess` 左侧路径中，对 `setOnXxx`/`getOnXxx` 模式进行兜底：如果左侧是 `obj.setOnXxx(rhs)` 的 `+=` 且右侧是委托/方法引用，应转成 `obj.addXxxListener(rhs)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void EventField_PlusEquals_WrappedInAddListener()
{
    var result = Convert("""
        using System;
        using System.Xml.Serialization;

        class Sample
        {
            XmlSerializerEvents _events = new XmlSerializerEvents();

            void M(XmlNodeEventHandler handler)
            {
                _events.OnUnknownNode += handler;
            }
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains("addOnUnknownNodeListener", result.GeneratedCode);
    Assert.DoesNotContain(" + ", result.GeneratedCode);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~EventField_PlusEquals_WrappedInAddListener"`
Expected: FAIL

- [ ] **Step 3: 兜底处理 MemberAccess event 赋值**

在 `TransformAssignment` 的 `+=`/`-=` 分支中，对 `MemberAccessExpressionSyntax` 增加兜底逻辑：

```csharp
if ((op == "+=" || op == "-=") && leftNode is MemberAccessExpressionSyntax evtMa)
{
    var symbol = context.GetSymbolInfo(leftNode).Symbol;
    if (symbol is IEventSymbol evt)
    {
        // existing logic
    }
    else if (evtMa.Name.Identifier.Text.StartsWith("On")
             || evtMa.Name.Identifier.Text.StartsWith("on"))
    {
        // Fallback: treat as event if semantic model failed but name matches event pattern
        var receiver = facade.Transform(evtMa.Expression, context);
        var handler = EnsureValidEventHandler(facade.Transform(rightNode, context), rightNode, context);
        string eventName = evtMa.Name.Identifier.Text;
        string method = op == "+="
            ? $"add{eventName}Listener"
            : $"remove{eventName}Listener";
        return $"{receiver}.{method}({handler})";
    }
}
```

如果该兜底仍不够，可能需要进一步在 `MemberAccessExpressionTransformer` 中识别 event 访问并避免生成 `getOnXxx()` 调用。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~EventField_PlusEquals_WrappedInAddListener"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs
git commit -m "fix(transformer): fallback event +=/-= for MemberAccess event-like names"
```

---

## 跨阶段集成验证

- [ ] **Step 9: 安装 compat 并重新生成**

Run:
```powershell
mvn clean install -DskipTests -f d:\code\cs2j\java\csharptojava-compat\pom.xml
dotnet run --project d:\code\cs2j\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj -- convert-project -s d:\csharpxml -d d:\cs-xml-20260716 --extra-deps io.github.ningpp:system-private-uri:0.0.1-SNAPSHOT
```

Expected: compat 安装成功，1206 个文件生成成功。

- [ ] **Step 10: Maven 验证**

Run:
```powershell
cd d:\cs-xml-20260716
mvn clean package -e -l d:\code\cs2j\mvn-build.log
```

Expected: 构建仍可能失败，但本次计划涉及的以下错误模式应消失：
- `int 无法转换为 Decimal`
- `Decimal * Decimal` / `Decimal + Decimal` 等运算符类型错误
- `XmlSchemaObjectCollection 无法转换为 CSharpICollection`
- `CSharpList<capture#1,?> 无法转换为 CSharpGenericIList<Object>`
- `Object & 0xFF`
- enum 与 int 直接 `==`/`!=`
- event `+`/`-` 运算符错误

- [ ] **Step 11: 全量单元测试无回归**

Run: `dotnet test d:\code\cs2j`
Expected: 全部通过，0 失败。

---

## Phase 4: 反射包装类收尾

### Task 9: 修复 DataAttribute 抽象方法签名与 MethodInfo 包装类不一致

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/csharp/xunit/Sdk/DataAttribute.java`
- Verify: `d:\cs-xml-20260716\modulecore\src\test\java\OLEDB\Test\ModuleCore\XmlTestsAttribute.java`

**Context:**
当前 `DataAttribute` 抽象方法仍使用原生 `java.lang.reflect.Method`，而项目已统一将 `System.Reflection.MethodInfo` 映射到 compat 包装类 `io.github.ningpp.compat.MethodInfo`。生成的 `XmlTestsAttribute.getData(MethodInfo testMethod)` 因签名不匹配而无法覆盖 `DataAttribute.getData(Method testMethod)`，导致 `mvn clean package -e` 在 `modulecore` 的 testCompile 阶段失败。

- [ ] **Step 1: 修改 DataAttribute 签名**

将 `DataAttribute.java` 中的参数类型从 `java.lang.reflect.Method` 改为 `io.github.ningpp.compat.MethodInfo`：

```java
package csharp.xunit.Sdk;

import io.github.ningpp.compat.CSharpGenericIterable;
import io.github.ningpp.compat.MethodInfo;

public abstract class DataAttribute {
    public abstract CSharpGenericIterable<Object[]> getData(MethodInfo testMethod);
}
```

- [ ] **Step 2: 安装 compat 并验证 modulecore 编译**

Run:
```powershell
mvn clean install -DskipTests -f d:\code\cs2j\java\csharptojava-compat\pom.xml
cd d:\cs-xml-20260716
mvn clean test-compile -pl modulecore -am -e -l d:\code\cs2j\mvn-task9.log
```

Expected: `modulecore` testCompile 成功，不再报 `XmlTestsAttribute` 未覆盖 `getData(java.lang.reflect.Method)`。

- [ ] **Step 3: 提交**

```bash
git add java/csharptojava-compat/src/main/java/csharp/xunit/Sdk/DataAttribute.java
mvn clean install -DskipTests -f d:\code\cs2j\java\csharptojava-compat\pom.xml
git commit -m "fix(compat): DataAttribute.getData uses compat MethodInfo wrapper"
```

---

## Phase 5: 剩余错误扫尾

### Task 10: 全量 Maven 构建并提取剩余错误清单

**Files:**
- Output: `d:\code\cs2j\mvn-build.log`

**Context:**
Task 9 已解除 `modulecore` 阻塞，需要重新运行全量 `mvn clean package -e` 以获取下一批编译错误，并按出现次数/模块聚合输出，便于逐个派发修复任务。

- [ ] **Step 1: 运行全量 Maven 构建**

Run:
```powershell
cd d:\cs-xml-20260716
mvn clean package -e -l d:\code\cs2j\mvn-build.log
```

- [ ] **Step 2: 提取并聚合错误**

解析 `d:\code\cs2j\mvn-build.log`，输出：
1. 总 `[ERROR]` 行数。
2. 按模块分组的首个错误示例。
3. 按错误消息去重后的前 20 条唯一错误（含文件路径和行号）。

- [ ] **Step 3: 报告并停止（本 Task 不修复）**

将聚合结果返回给父代理，不提交代码。

---

### Task 11: 修复 C# `decimal` 关键字静态成员访问映射

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（如需要）
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

**Context:**
Task 10 显示 130 条错误来自生成代码使用小写 `decimal.negate` / `decimal.truncate` / `decimal.Zero` 等。C# 中 `decimal` 是 `System.Decimal` 的关键字别名，转换器应将其映射到 compat 包装类 `io.github.ningpp.compat.Decimal`。当前 `TransformPredefinedType` 对 `decimal` 返回小写 `decimal`，导致静态成员访问和调用生成非法 Java 标识符。

- [ ] **Step 1: 写失败测试**

在 `CSharpXmlCompileRegressionTests.cs` 中添加：

```csharp
[Fact]
public void DecimalStatic_FieldZero_MappedToDecimalWrapper()
{
    var result = Convert("""
        using System;

        class Sample
        {
            Decimal M() => decimal.Zero;
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains("Decimal.ZERO", result.GeneratedCode);
    Assert.DoesNotContain("decimal.ZERO", result.GeneratedCode);
    Assert.DoesNotContain("decimal.Zero", result.GeneratedCode);
}

[Fact]
public void DecimalStatic_Negate_MappedToDecimalWrapper()
{
    var result = Convert("""
        using System;

        class Sample
        {
            Decimal M(Decimal d) => decimal.Negate(d);
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains("Decimal.negate(d)", result.GeneratedCode);
    Assert.DoesNotContain("decimal.negate", result.GeneratedCode);
}

[Fact]
public void DecimalStatic_Truncate_MappedToDecimalWrapper()
{
    var result = Convert("""
        using System;

        class Sample
        {
            Decimal M(Decimal d) => decimal.Truncate(d);
        }
        """);

    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    Assert.Contains("Decimal.truncate(d)", result.GeneratedCode);
    Assert.DoesNotContain("decimal.truncate", result.GeneratedCode);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~DecimalStatic_"`
Expected: FAIL，生成代码仍含 `decimal.Zero` / `decimal.negate` / `decimal.truncate`。

- [ ] **Step 3: 修复 TransformPredefinedType**

修改 `IdentifierExpressionTransformer.cs`：

1. 让 `Transform` 在调用 `TransformPredefinedType` 时传入 `context` 以便添加 import。
2. 在 `TransformPredefinedType` 中对 `decimal` 返回 `"Decimal"` 并添加 `io.github.ningpp.compat.Decimal` import。

参考改动：

```csharp
public string Transform(ExpressionSyntax node, ConversionContext context)
    => node.Kind() switch
    {
        SyntaxKind.IdentifierName => TransformIdentifier((IdentifierNameSyntax)node, context),
        SyntaxKind.PredefinedType => TransformPredefinedType((PredefinedTypeSyntax)node, context),
        SyntaxKind.GenericName => TransformGenericName((GenericNameSyntax)node, context),
        SyntaxKind.SimpleMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
        SyntaxKind.PointerMemberAccessExpression => TransformPointerMemberAccess((MemberAccessExpressionSyntax)node, context),
        _ => throw new NotSupportedException($"Identifier expression kind {node.Kind()} not supported.")
    };

private string TransformPredefinedType(PredefinedTypeSyntax node, ConversionContext context)
{
    var typeName = node.Keyword.Text;

    if (typeName == "decimal")
    {
        context.AddImport("io.github.ningpp.compat.Decimal");
        return "Decimal";
    }

    // Fix 3: generic type arguments require boxed types (e.g., List<Integer> not List<int>)
    if (node.Parent is TypeArgumentListSyntax)
        return ExpressionTransformerHelpers.BoxedTypeName(node);

    // Non-generic context: use Java primitive / value types
    return typeName switch
    {
        "int" => "int",
        "long" => "long",
        "short" => "short",
        "byte" => "int",
        "sbyte" => "byte",
        "uint" => "int",
        "ulong" => "long",
        "ushort" => "short",
        "float" => "float",
        "double" => "double",
        "bool" => "boolean",
        "char" => "char",
        "string" => "String",
        "object" => "Object",
        "void" => "void",
        _ => typeName
    };
}
```

注意：当 `node.Parent is TypeArgumentListSyntax` 时，`BoxedTypeName` 已经返回 `"Decimal"`，因此 `decimal` 分支应放在该检查之前。

如果仅修改 `TransformPredefinedType` 不足以覆盖 `decimal.Negate(d)`（例如 invocation 路径使用了其他 receiver 解析），则额外在 `InvocationExpressionTransformer.TransformMemberInvocation` 中确保 `memberAccess.Expression` 为 `PredefinedTypeSyntax` 且 keyword 为 `decimal` 时，receiver 强制为 `"Decimal"` 并添加 import。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~DecimalStatic_"`
Expected: PASS。

- [ ] **Step 5: 重新生成并验证 Maven**

Run:
```powershell
mvn clean install -DskipTests -f d:\code\cs2j\java\csharptojava-compat\pom.xml
dotnet run --project d:\code\cs2j\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj -- convert-project -s d:\csharpxml -d d:\cs-xml-20260716 --extra-deps io.github.ningpp:system-private-uri:0.0.1-SNAPSHOT
cd d:\cs-xml-20260716
mvn clean package -e -l d:\code\cs2j\mvn-task11.log
```

Expected: `decimal` 相关 `找不到符号` 错误消失（预计减少 130 条）。

- [ ] **Step 6: 提交**

```bash
git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs
if needed: git add src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
git commit -m "fix(transformer): map decimal keyword static access to compat Decimal wrapper"
```

---

### Task 12: 重新生成目标项目并获取最新错误基线

**Files:**
- Output: `d:\code\cs2j\mvn-build.log`

**Context:**
Task 11 的回归测试已通过，但子代理报告的 Maven 验证基于未重新生成的旧代码（`Numeric10FacetsChecker.java` 时间戳未更新）。需要重新运行完整转换 + Maven 构建，获得真实的剩余错误清单，再决定下一批修复任务。

- [ ] **Step 1: 安装 compat 并重新生成 Java 项目**

Run:
```powershell
mvn clean install -DskipTests -f d:\code\cs2j\java\csharptojava-compat\pom.xml
dotnet run --project d:\code\cs2j\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj -- convert-project -s d:\csharpxml -d d:\cs-xml-20260716 --extra-deps io.github.ningpp:system-private-uri:0.0.1-SNAPSHOT
```

Expected: 转换成功，1206 个文件生成。可抽查 `Numeric10FacetsChecker.java` 确认 `decimal.negate` 已变为 `Decimal.negate`。

- [ ] **Step 2: 运行全量 Maven 构建**

Run:
```powershell
cd d:\cs-xml-20260716
mvn clean package -e -l d:\code\cs2j\mvn-build.log
```

- [ ] **Step 3: 提取并聚合剩余错误**

解析 `d:\code\cs2j\mvn-build.log`，返回：
1. 总 `[ERROR]` 行数。
2. 按模块分组的首个错误示例。
3. 按错误消息去重后的前 20 条唯一错误（含文件路径和行号）。
4. 与 Task 10 相比明显减少/消失的错误类别。

- [ ] **Step 4: 报告并停止（本 Task 不修复）**

将聚合结果返回给父代理，不提交代码。

---

## Self-Review Checklist

1. **Spec coverage:** 每个已识别的错误类别（Decimal 赋值/运算、ICollection/IList 适配、enum 比较/Object & int/event +=-=）都有对应 Task。
2. **Placeholder scan:** 无 TBD/TODO；每个 Step 包含完整测试代码或实现代码。
3. **类型一致性:**
   - `CSharpICollection.from(Object)` 返回 `CSharpICollection<Object>`
   - `CSharpGenericIList.from(Object)` 返回 `CSharpGenericIList<Object>`
   - `ExpressionTransformerHelpers.IsDecimalType` 已经 unwrap nullable
4. **Gap:** 若 Task 8 的兜底仍无法覆盖所有 event 场景，需要回到 `MemberAccessExpressionTransformer` 继续深入。计划在 Task 8 的验证步骤中显式检查这一点。
