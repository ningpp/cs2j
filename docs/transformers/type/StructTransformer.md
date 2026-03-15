# StructTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs`

## 1. C# Language Feature Converted

`StructTransformer` converts C# `struct` declarations to Java classes. C# structs are value types: they are allocated on the stack, copied when passed to methods or assigned, and have no heap identity. Java has no native value types (prior to Project Valhalla), so the transformer maps a struct to a regular Java reference-type class.

The transformer handles:
- `readonly struct` → Java `final` class with all fields marked `final`
- `ref struct` (stack-only, no heap allocation)
- Interface implementation lists
- Generic type parameters with `where` constraints
- All member types (fields, methods, constructors, properties, operators)
- `[StructLayout]` attribute recognition (informational)

```csharp
// C#
public struct Point {
    public int X, Y;
    public Point(int x, int y) { X = x; Y = y; }
    public double Length => Math.Sqrt(X*X + Y*Y);
}

// Java
public class Point {
    public int X, Y;
    public Point(int x, int y) { this.X = x; this.Y = y; }
    public double getLength() { return Math.sqrt(X*X + Y*Y); }
}
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Value semantics (copy-by-value) completely lost

C# structs are **value types**: assignment copies the entire value, method parameters receive a copy, and mutation inside a method does not affect the caller's copy:
```csharp
var a = new Point(1, 2);
var b = a;          // b is a full copy
ModifyX(a);         // caller's 'a' is unchanged
```

The generated Java class is a **reference type**: assignment copies the reference, not the value. All downstream code that relies on copy-by-value semantics will be silently incorrect. The transformer does not emit any warning or clone method to assist callers.

### Issue 2 — `readonly struct` makes the whole class `final` instead of making fields `final`

```csharp
public readonly struct ImmutablePoint { public int X; public int Y; }
```

The transformer emits `public final class ImmutablePoint { public int X; public int Y; }`. In Java, `final` on a class means it cannot be subclassed — it does **not** make the fields immutable. The intended semantics require:
```java
public class ImmutablePoint {
    public final int X;
    public final int Y;
}
```

The `final` modifier should be applied to each **field**, not to the class declaration.

### Issue 3 — `ref struct` is not handled

C# 7.2 `ref struct` is a stack-only type that cannot be boxed, stored in arrays, or used as a generic type argument:
```csharp
public ref struct Span<T> { ... }
```

The transformer emits a plain Java class (or a `final` class if `readonly ref struct`), with no indication of the stack-only constraint. Java has no equivalent concept, so a best-effort mapping should add a code comment:
```java
// NOTE: Originally a C# ref struct (stack-only). Java does not enforce stack allocation.
```

Currently no such comment is emitted.

### Issue 4 — Default constructor behaviour differs

C# structs always have an implicit parameterless constructor that zeroes all fields. Java classes do not automatically generate a no-arg constructor when at least one explicit constructor is present. If the C# struct has explicit constructors, the transformer must emit an explicit zero-initialising no-arg constructor to preserve C# semantics:
```java
public Point() { X = 0; Y = 0; }
```

The transformer does not currently do this.

### Issue 5 — `static readonly` fields may miss the `static` modifier

`FieldTransformer` handles the `static` keyword on fields, but `StructTransformer` routes all members through the shared member-dispatch loop. In some edge cases where a `FieldDeclarationSyntax` inside a struct has `ReadOnlyKeyword` but the `static` modifier comes from the struct modifier (via partial type merging), the combined modifier list may be assembled in the wrong order and the `static` keyword may be dropped.

### Issue 6 — Operator overloads in structs are not given special treatment

C# structs frequently define arithmetic operators:
```csharp
public static Point operator +(Point a, Point b) => new Point(a.X+b.X, a.Y+b.Y);
```

`OperatorTransformer` maps these to static methods named `add`, `subtract`, etc., which is correct. However, because the struct is now a reference type in Java, `operator ==` → `equals` conflict is more dangerous: the semantic meaning of `==` for a struct in C# is value equality, but the generated `equals` override must use field comparison. The transformer does not verify or warn that `operator ==` and `operator !=` need to align with `hashCode`.

## 3. Proposed Fixes

### Fix 1 — Emit a `clone()` method and document value-semantic requirement

Add a `clone()` method to every generated struct class:
```java
public Point clone() {
    return new Point(this.X, this.Y);
}
```

Add a class-level Javadoc warning that value semantics must be managed manually by callers.

### Fix 2 — Apply `final` to fields, not the class, for `readonly struct`

```csharp
if (isReadOnly)
{
    // Do NOT add "final" to the class declaration
    foreach (var field in javaClass.Fields)
        field.Modifiers.Insert(0, "final");
}
```

### Fix 3 — Emit a comment for `ref struct`

```csharp
if (isRefStruct)
    javaClass.LeadingComment =
        "// NOTE: Originally a C# ref struct (stack-only). Java has no equivalent.";
```

### Fix 4 — Emit an explicit zero-initialising no-arg constructor when needed

After collecting all user-defined constructors, if the generated class has at least one explicit constructor but no no-arg constructor, emit:
```java
public StructName() { /* zero-initialized */ }
```

### Fix 5 — Verify `static` modifier propagation through partial merging

Add a unit test for a partial struct with `static readonly` fields to verify the modifier list is complete after merging.

### Fix 6 — Verify `operator ==` is always paired with `hashCode`

After `OperatorTransformer` runs, check if the class has an `equals` method but no `hashCode`, and emit a default `hashCode` using `Objects.hash(...)` over all fields.

## 4. Commit Changes

```
fix(struct): add clone() for value semantics, fix readonly to apply final on fields not class, handle ref struct with comment, emit no-arg constructor, and verify hashCode pairing with equals
```
