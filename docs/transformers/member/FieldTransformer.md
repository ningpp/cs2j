# FieldTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs`

## 1. C# Language Feature Converted

`FieldTransformer` converts C# field declarations to Java field declarations. The transformer handles:

- Single-variable and multi-variable field declarations (`int x, y, z;`)
- Access modifiers (`public`, `private`, `protected`, `internal`, `protected internal`)
- `static`, `readonly`, `const`, and `volatile` modifiers
- Initializer expressions (literals, new expressions, method calls)
- `[Obsolete]` → `@Deprecated`
- XML doc comments → Javadoc

```csharp
// C#
private static readonly int MaxRetries = 3;
public const string DefaultName = "default";

// Java
private static final int MaxRetries = 3;
public static final String DefaultName = "default";
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `protected internal` modifier is only cleaned up in `FieldTransformer`, not in `MethodTransformer` or `PropertyTransformer`

The C# modifier combination `protected internal` means "accessible to derived classes or classes in the same assembly". Java has no direct equivalent; the standard mapping is `protected`. `FieldTransformer` strips `internal` from the modifier list, but `MethodTransformer` and `PropertyTransformer` do not perform this cleanup. This inconsistency means the `internal` keyword leaks into generated Java method and property declarations.

### Issue 2 — Complex `const` expressions may produce Java-incompatible syntax

C# `const` fields can be initialized with non-trivial compile-time constant expressions:
```csharp
public const int MaxBits = sizeof(int) * 8;         // sizeof is C#-specific
public const long Mask   = ~0L & unchecked(-1L);    // unchecked
```

`FieldTransformer` passes the initializer through `ExpressionTransformerFacade` without special-casing `sizeof` or `unchecked`. The resulting Java code may contain C# keywords that are invalid in Java.

### Issue 3 — Hex literal initializers for `Double`/`Float` fields are not caught

In C# you can write:
```csharp
public const int Flags = 0xFF;   // hex integer — fine in Java
public const double Scale = 0x1p-52;  // hex float — Java 5+ supports it, but uncommented
```

`ExpressionTransformerHelpers.IsNumericLiteral` regex `^-?\d+(\.\d+)?[LlfFdD]?$` only matches decimal patterns. Hex literals like `0xFF` fall through the numeric-literal widening logic, so a `long` `const int Flags = 0xFF` would not be widened to `0xFFL` as expected.

### Issue 4 — Multi-variable declarations are split but initialiser expressions may reference earlier variables

```csharp
int a = 0, b = a + 1;   // 'b' initialiser depends on 'a'
```

When the transformer splits a multi-variable declaration into separate Java field declarations (`int a = 0;` and `int b = a + 1;`) it emits them in order, which is correct for Java fields. However, in Java the field initializers are evaluated in declaration order **only within a block** (instance initializer), not between field declarations. For `static final` fields this usually works; for instance fields the semantics diverge from C#.

### Issue 5 — `volatile` modifier is passed through but `volatile` Java semantics differ

C# `volatile` ensures a memory barrier on read/write. Java `volatile` has the same guarantee but also provides sequential consistency within the JMM (Java Memory Model). The transformer emits the `volatile` keyword on Java fields, which is correct, but does not add an import for `java.util.concurrent.atomic` or a comment when the field is of a non-primitive type (where `AtomicReference<T>` would be more idiomatic).

## 3. Proposed Fixes

### Fix 1 — Centralise `protected internal` cleanup into a shared modifier-normalisation helper

Create `ModifierNormalizer.Normalize(IList<string> modifiers)`:
```csharp
public static void Normalize(IList<string> modifiers)
{
    bool hasProtected = modifiers.Remove("protected");
    bool hasInternal  = modifiers.Remove("internal");
    if (hasProtected || hasInternal)
        modifiers.Insert(0, "protected"); // or "" for internal-only
}
```

Call this helper from `FieldTransformer`, `MethodTransformer`, and `PropertyTransformer`.

### Fix 2 — Special-case `sizeof` and `unchecked` in const initialisers

```csharp
if (initializer.Expression is SizeOfExpressionSyntax sizeOf)
    return $"/* sizeof({sizeOf.Type}) */ {GetPlatformSize(sizeOf.Type)}";
if (initializer.Expression is CheckedExpressionSyntax checked)
    return TransformExpression(checked.Expression, context); // strip the keyword
```

### Fix 3 — Extend `IsNumericLiteral` to cover hex and binary patterns

```csharp
private static readonly Regex NumericLiteralRegex = new Regex(
    @"^-?(0[xX][0-9a-fA-F]+|0[bB][01]+|\d+)(\.\d+)?([LlFfDdUu]{0,2})?$",
    RegexOptions.Compiled);
```

### Fix 4 — Emit a warning comment for cross-referencing initialisers in multi-variable fields

When any non-first variable's initialiser contains an `IdentifierNameSyntax` that matches a prior variable name in the same declaration, emit:
```java
// NOTE: Initializer order may differ from C# instance field semantics.
```

### Fix 5 — Add a comment for `volatile` non-primitive fields

When a field is `volatile` and its type is not a primitive (`int`, `long`, `boolean`, `double`, etc.), emit:
```java
// Consider replacing with AtomicReference<T> for idiomatic Java concurrency.
private volatile MyObj _obj;
```

## 4. Commit Changes

```
fix(field): centralise protected-internal cleanup, handle sizeof/unchecked in const initializers, extend numeric literal regex for hex/binary, and document volatile non-primitive fields
```
