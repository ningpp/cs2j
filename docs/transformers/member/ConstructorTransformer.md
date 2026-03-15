# ConstructorTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs`

## 1. C# Language Feature Converted

`ConstructorTransformer` converts C# constructor declarations to Java constructors. It handles:

- Instance constructors (`public MyClass(int x) { ... }`)
- Constructor initializers (`this(...)` and `base(...)` chains)
- Expression-body constructors (`public MyClass(int x) => this.x = x;`)
- `params` variadic parameters
- Visibility modifiers (`public`, `protected`, `private`, `internal` → `package-private`)
- XML doc comments → JavaDoc

```csharp
// C#
public class Foo {
    private readonly int _x;
    public Foo(int x) : base(x) { _x = x; }
    public Foo() : this(0) { }
}

// Java
public class Foo {
    private final int _x;
    public Foo(int x) { super(x); this._x = x; }
    public Foo() { this(0); }
}
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Static constructors are not handled

C# allows a single static constructor per class:
```csharp
static MyClass() { DefaultValue = ComputeDefault(); }
```

`ConstructorTransformer` is dispatched only for `ConstructorDeclarationSyntax`. It checks whether the constructor has a `StaticKeyword` modifier but has no code path to emit a Java `static { }` initializer block. The static constructor body is silently discarded. This is also mentioned in the `ClassTransformer` analysis; the fix belongs here at the member level.

### Issue 2 — `params` parameter does not set `IsVarArgs = true`

When a parameter has `ParamsKeyword`:
```csharp
public Foo(params int[] values) { }
```

The transformer creates a `JavaParameter` but does not set `IsVarArgs = true` on it. The Java code generator then emits `int[] values` instead of `int... values`, which breaks callers that pass comma-separated arguments instead of an array.

### Issue 3 — Redundant empty `super()` call is emitted

For constructors that have no `: base(...)` initializer and whose class does not extend an explicit base class, the transformer emits:
```java
super();  // explicit no-argument super call
```

Java constructors implicitly call `super()` when no explicit `super()` is present. Emitting a redundant `super();` is not harmful, but it adds noise and may trigger lint/style warnings. It should only be emitted when there are actual base arguments.

### Issue 4 — Expression-body constructors emit the expression as a statement without verification

```csharp
public Foo(int x) => _x = x;
```

The transformer wraps the expression body in a statement:
```java
_x = x;
```

This works when the expression is an assignment (the common case). However, if the expression evaluates to a value (e.g., a method call with a return type), emitting it as a bare statement has different semantics than the C# expression body, which must be a `void` result. The transformer should verify the expression is assignment-like or call-like before emitting it as a standalone statement.

### Issue 5 — `internal` visibility is mapped to `package-private` inconsistently

`internal` in C# means "visible within the assembly". The standard mapping to Java is `package-private` (no access modifier). `ConstructorTransformer` maps `internal` → empty string (package-private), which is correct; however, if the class has a `protected internal` constructor the transformer emits only `protected`, losing the `package-private` semantic. The fix should emit `protected` (the broader of the two in Java's access model) with a comment.

## 3. Proposed Fixes

### Fix 1 — Detect `static` keyword and emit `static { }` block

```csharp
if (ctorDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
{
    var staticBlock = new JavaStaticInitializerBlock();
    staticBlock.Statements = TransformStatements(ctorDecl.Body, context);
    return staticBlock;
}
```

### Fix 2 — Set `IsVarArgs` for `params` parameters

```csharp
if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
    javaParam.IsVarArgs = true;
```

### Fix 3 — Suppress implicit `super()` call

Only emit `super()` when the `: base(...)` initializer exists with arguments, or when the base-class has no no-argument constructor known from context.

### Fix 4 — Validate expression-body constructor

Before emitting the expression as a statement:
- Accept `AssignmentExpression`, `InvocationExpression`, `PostfixUnaryExpression`, `PrefixUnaryExpression`
- For all others, emit a comment: `// TODO: verify expression body semantics`

### Fix 5 — Handle `protected internal` correctly

```csharp
bool hasProtected = modifiers.Contains("protected");
bool hasInternal  = modifiers.Contains("internal") || modifiers.Contains(""); // package-private
if (hasProtected && hasInternal)
    return "protected"; // Java's broadest access that covers both
```

## 4. Commit Changes

```
fix(constructor): emit static initializer blocks, set IsVarArgs for params, suppress implicit super(), validate expression-body constructors, and handle protected internal
```
