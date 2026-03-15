# EnumTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs`

## 1. C# Language Feature Converted

`EnumTransformer` converts C# `enum` declarations to Java equivalents via two different strategies:

**Standard enums** → Java `enum` declarations (via `JavaEnumDeclaration`):
```csharp
// C#
enum Color { Red, Green, Blue }

// Java
public enum Color { Red, Green, Blue }
```

**`[Flags]` enums** → Java classes with `public static final int` constants and bitwise helper methods:
```csharp
// C#
[Flags] enum FileMode { Read = 1, Write = 2, Execute = 4 }

// Java
public class FileMode {
    public static final int Read    = 1;
    public static final int Write   = 2;
    public static final int Execute = 4;
    public static int and(int a, int b) { return a & b; }
    public static int or(int a, int b)  { return a | b; }
    // ...
}
```

The transformer also registers `[Flags]` enums in the context so that downstream type resolution returns `"int"` wherever the enum type appears.

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Non-`[Flags]` enums lose explicit integer values

C# allows enums to have explicit integer values:
```csharp
enum Status { Open = 10, Closed = 20, Pending = 30 }
```

Java enums cannot have a plain integer ordinal assigned at the call site. The transformer currently emits a warning but still generates `{ Open, Closed, Pending }` without the values. Any C# code that casts the enum to `int` or uses the values numerically will break. A possible fix is to add a `value` field and `getValue()` method to the Java enum.

### Issue 2 — Duplicate `or` and `bitwiseOr` helper methods

```csharp
AddFlagsMethod("or",        "return a | b;", new JavaParameter("int", "a"), new JavaParameter("int", "b"));
AddFlagsMethod("bitwiseOr", "return a | b;", new JavaParameter("int", "a"), new JavaParameter("int", "b"));
```

Both `or` and `bitwiseOr` have identical bodies (`return a | b;`). Having two identical static methods increases the class footprint and confuses callers about which to use. One should be removed or renamed to indicate a different operation (e.g., `or` for logical-or semantics, `bitwiseOr` reserved for a different mask strategy).

### Issue 3 — No `has` (flag test) helper method

The most common operation on a `[Flags]` enum in C# is checking whether a flag is set:
```csharp
if ((mode & FileMode.Write) != 0) { ... }
```

The transformer generates `and`, `or`, `bitwiseOr`, and `complement` but not a `has(int flags, int flag)` method:
```java
public static boolean has(int flags, int flag) { return (flags & flag) != 0; }
```

Without this, callers must replicate the bitwise check inline every time.

### Issue 4 — `RegisterFlagsEnum` may not cover all type-reference sites

`context.RegisterFlagsEnum(enumDecl.Identifier.Text)` registers the type by **short name only**. If the same enum is referenced from a different namespace using a fully-qualified name (e.g., `MyNamespace.FileMode`), `MapType` may not recognise it as `"int"` and will emit the full enum type name, causing a Java compile error since the generated Java class has no `valueOf` method.

### Issue 5 — Binary literal conversion comment is misleading

The code converts C# binary literals (`0b...`) to decimal integers for "Java compatibility":
```csharp
// Binary literal: convert to decimal for Java compatibility
```

Java has supported binary integer literals (`0b...`) since Java 7. The comment is misleading: Java does support them. The conversion is unnecessary for Java 7+ and the comment should be corrected. The code itself is harmless (producing equivalent values) but creates confusion during maintenance.

### Issue 6 — `[Flags]` enum with `long` underlying type is silently truncated

C# allows:
```csharp
[Flags] enum BigFlags : long { A = 1L, B = 2L, ... }
```

The generated fields are `public static final int A = 1;` — truncating the `long` to `int`. For flags enums with more than 32 bits the generated Java code is semantically incorrect. The transformer should check the underlying type and use `long` constants accordingly.

## 3. Proposed Fixes

### Fix 1 — Preserve explicit enum values via a `value` field pattern

For Java enums with explicit values, generate:
```java
public enum Status {
    Open(10), Closed(20), Pending(30);
    private final int value;
    Status(int v) { this.value = v; }
    public int getValue() { return value; }
}
```

Emit this pattern whenever any member has an `EqualsValue` initializer.

### Fix 2 — Remove the duplicate `bitwiseOr` method

Keep `or(int a, int b)` and remove `bitwiseOr`. Update any generated call sites that use `bitwiseOr`.

### Fix 3 — Add `has(int flags, int flag)` helper

```csharp
AddFlagsMethod("has", "return (flags & flag) != 0;",
    new JavaParameter("int", "flags"),
    new JavaParameter("int", "flag"));
// Return type should be boolean, not int:
```

Override the `AddFlagsMethod` helper to support `boolean` return type for `has`.

### Fix 4 — Register with fully-qualified name

```csharp
var fqn = context.CurrentNamespace == null
    ? enumDecl.Identifier.Text
    : $"{context.CurrentNamespace}.{enumDecl.Identifier.Text}";
context.RegisterFlagsEnum(enumDecl.Identifier.Text);
context.RegisterFlagsEnum(fqn);
```

### Fix 5 — Fix misleading binary literal comment

```csharp
// Binary literal 0b... is valid in Java 7+ but we convert to decimal for clarity.
```

### Fix 6 — Use `long` for `[Flags]` enums with long underlying type

Check `enumDecl.BaseList` for `long`/`ulong` and switch the generated field and method types to `long`.

## 4. Commit Changes

```
fix(enum): preserve explicit enum values, remove duplicate bitwiseOr, add has() helper, and use long for 64-bit flags enums
```
