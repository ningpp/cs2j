# AssignmentTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs`

## 1. C# Language Feature Converted

`AssignmentTransformer` converts C# assignment expressions to Java assignment expressions. It covers:

- Simple assignment: `a = b`
- Compound arithmetic assignments: `+=`, `-=`, `*=`, `/=`, `%=`
- Bitwise compound assignments: `&=`, `|=`, `^=`, `<<=`, `>>=`
- Null-coalescing assignment: `??=` (C# 8)
- Event subscription/unsubscription: `event += handler`, `event -= handler`
- Property assignments (which in Java require a setter call)
- Indexer assignments (which in Java require a set method call)

```csharp
// C#
x += 5;
name ??= "default";
button.Click += OnClick;

// Java
x += 5;
if (name == null) name = "default";
button.addClickListener(this::onClick);
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Property assignments not converted to setter calls

When the left-hand side of an assignment is a C# property (e.g., `person.Name = "Alice"`), the transformer emits:
```java
person.Name = "Alice";
```

Java has no properties; `Name` should have been translated to `setName("Alice")`. The transformer does not distinguish between field access and property access. Without semantic model information this requires a heuristic (PascalCase, or has an associated getter method), which should at least be attempted.

### Issue 2 — Indexer assignments not detected

```csharp
dict["key"] = value;  // C# indexer assignment
```

The transformer emits `dict["key"] = value;` in Java, which is invalid (`[]` on a non-array). The correct Java translation is `dict.put("key", value)` (for `Map`) or `list.set(index, value)` (for `List`). The transformer does not recognise indexer LHS patterns.

### Issue 3 — `??=` (`CoalesceAssignmentExpression`, `SyntaxKind.CoalesceAssignmentExpression`) not registered

`CoalesceAssignmentExpression` (token `??=`) is not in the `ExpressionTransformerRegistry`. When `ExpressionTransformerFacade` encounters `??=`, it throws `NotSupportedException`. The transformer contains code to handle it (`case "??=":`) but the SyntaxKind is never routed here.

### Issue 4 — Event `+=`/`-=` assigns are not converted to listener methods

```csharp
button.Click += OnClick;
button.Click -= OnClick;
```

The transformer emits `button.Click += onClick;` in Java, which is invalid. These should be routed through the event listener translation:
```java
button.addClickListener(this::onClick);
button.removeClickListener(this::onClick);
```

But the transformer has no logic to detect event subscription context.

### Issue 5 — Left-side expression evaluated once vs twice in compound `??=`

The generated Java for `a ??= b` is:
```java
if (a == null) a = b;
```

If `a` is a complex expression (e.g., `GetContainer().Value ??= default`), the expression is emitted twice in the Java expansion (once in the `null` check and once in the assignment), causing double evaluation with potential side effects. The safe pattern is:
```java
// Only correct for simple identifiers; for complex expressions use a temp var:
// var _tmp = GetContainer(); if (_tmp.Value == null) _tmp.Value = default;
```

## 3. Proposed Fixes

### Fix 1 — Detect property assignments using semantic model

```csharp
if (context.SemanticModel.GetSymbolInfo(left).Symbol is IPropertySymbol prop)
{
    string setter = "set" + char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..];
    return $"{TransformExpression(((MemberAccessExpressionSyntax)left).Expression, context)}.{setter}({TransformExpression(right, context)})";
}
```

### Fix 2 — Detect indexer assignments

```csharp
if (left is ElementAccessExpressionSyntax ela &&
    context.SemanticModel.GetSymbolInfo(ela).Symbol is IPropertySymbol { IsIndexer: true })
{
    // Route through IndexerTransformer setter path
}
```

### Fix 3 — Register `CoalesceAssignmentExpression`

In `AssignmentTransformer`'s static constructor:
```csharp
ExpressionTransformerRegistry.Instance.Register(
    SyntaxKind.CoalesceAssignmentExpression, new AssignmentTransformer());
```

### Fix 4 — Detect event `+=`/`-=` using semantic model

```csharp
if (context.SemanticModel.GetSymbolInfo(left).Symbol is IEventSymbol evt)
{
    bool isAdd = assignmentExpr.Kind() == SyntaxKind.AddAssignmentExpression;
    string method = isAdd
        ? $"add{evt.Name}Listener"
        : $"remove{evt.Name}Listener";
    return $"{TransformExpression(receiver, context)}.{method}({TransformExpression(right, context)})";
}
```

### Fix 5 — Extract complex LHS of `??=` to a temp variable

```csharp
if (assignmentExpr.Kind() == SyntaxKind.CoalesceAssignmentExpression)
{
    if (left is not IdentifierNameSyntax)
    {
        // Emit temp-variable pre-statement and transform lhs only once
        // Use context.PreStatements injection
    }
}
```

## 4. Commit Changes

```
fix(assignment): convert property assignments to setter calls, translate indexer assignments to put/set, register ??= kind, convert event +=/-= to listener methods, and avoid double-evaluation in compound ??=
```
