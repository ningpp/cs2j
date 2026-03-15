# RecordTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs`

## 1. C# Language Feature Converted

`RecordTransformer` converts C# `record` declarations to Java equivalents. It selects between two strategies based on the target Java version:

**Java 16+ mode** → Java `record`:
```csharp
// C#
public record Point(int X, int Y);

// Java 16+
public record Point(int X, int Y) { }
```

**Below Java 16 (or `--no-records` flag)** → Immutable class with `final` fields, constructor, getters, and overridden `equals`, `hashCode`, `toString`:
```csharp
// Java 8 compatibility
public final class Point {
    private final int X;
    private final int Y;
    public Point(int X, int Y) { this.X = X; this.Y = Y; }
    public int getX() { return X; }
    public int getY() { return Y; }
    @Override public boolean equals(Object o) { ... }
    @Override public int hashCode() { ... }
    @Override public String toString() { ... }
}
```

It also handles `record struct`, base-record delegation (`record Derived(int A) : Base(A)`), and any extra members declared in the record body.

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `CreateJavaRecord` does not build the Java record component list

`CreateJavaRecord` sets `IsRecord = true` on a `JavaClassDeclaration` node but does not populate its positional parameter list. Java records require the parameter list in the class header:
```java
public record Point(int x, int y) { }
```

Without the component list, the generated code emits `public record Point { }`, which is a valid Java record but has no accessor methods, no canonical constructor, and no `equals`/`hashCode`/`toString` implementations — defeating the purpose of using a Java record.

### Issue 2 — `GenerateObjectMethods` does not handle array-typed fields correctly

The generated `equals()` method compares fields using `Objects.equals(this.X, other.X)`. For array fields this comparison checks reference identity, not structural equality. The correct comparison is `Arrays.equals(this.data, other.data)` (or `Arrays.deepEquals` for multidimensional arrays). The transformer does not detect array types in the parameter list and never emits `Arrays.equals`.

Similarly, `hashCode()` should use `Arrays.hashCode(data)` for array components; the current generated code passes array references to `Objects.hash(...)`, producing hashes based on identity rather than content.

### Issue 3 — Record base-class constructor delegation is not implemented

```csharp
public record Derived(int A, int B) : Base(A);
```

In C#, the `(A)` call site after the base type name is the argument list for the base record constructor. The transformer reads `BaseList` and converts base type names, but does not extract the argument list for the base initializer call. The generated Java constructor will call `super()` without arguments, breaking the inheritance chain if `Base` lacks a no-argument constructor.

### Issue 4 — `record struct` follows wrong code path

C# 10 introduced `record struct`:
```csharp
public record struct MutablePoint(int X, int Y);
```

`RecordTransformer.IsRecord(decl)` is true for both `record class` and `record struct`. However, `record struct` has value semantics while `record class` has reference semantics. Neither `CreateJavaRecord` nor `CreateImmutableClass` currently distinguishes between them; both produce a reference-typed Java class/record. The struct semantics (default mutable fields for `record struct`, `with` expression support) are lost.

### Issue 5 — Extra members in the record body are processed by ClassTransformer logic

When the record has additional members (e.g. a computed property `public string FullName => $"{First} {Last}";`), they are dispatched through the same member-transformation loop used by `ClassTransformer`. This is largely correct but may emit duplicate getters if the same property name already exists as a positional component accessor.

## 3. Proposed Fixes

### Fix 1 — Populate the Java record component list

```csharp
if (context.Options.UseJavaRecords && recordDecl.ParameterList != null)
{
    foreach (var param in recordDecl.ParameterList.Parameters)
    {
        javaRecord.RecordComponents.Add(new JavaRecordComponent(
            MapType(param.Type, context),
            param.Identifier.Text));
    }
}
```

### Fix 2 — Detect array fields and emit `Arrays.equals` / `Arrays.hashCode`

In `GenerateObjectMethods`, inspect each positional parameter type:
```csharp
bool isArray = param.Type is ArrayTypeSyntax;
string equalExpr = isArray
    ? $"Arrays.equals(this.{name}, other.{name})"
    : $"Objects.equals(this.{name}, other.{name})";
```

Add `import java.util.Arrays;` to generated imports when array parameters are present.

### Fix 3 — Emit base constructor arguments

```csharp
if (baseArgs != null && baseArgs.Arguments.Count > 0)
{
    var argList = string.Join(", ",
        baseArgs.Arguments.Select(a => TransformExpression(a.Expression, context)));
    ctor.Initializer = $"super({argList})";
}
```

### Fix 4 — Distinguish `record struct` from `record class`

Check for `StructKeyword` in `RecordDeclarationSyntax.ClassOrStructKeyword` and route to a separate `CreateMutableRecordStruct` path that generates a mutable Java class (non-`final` fields, all-args constructor, no `equals`/`hashCode` unless explicitly overridden).

### Fix 5 — Skip positional components when processing extra members

Before dispatching extra members, collect the set of positional parameter names. When a property/method with the same name already appears as a component accessor, skip re-generating the getter.

## 4. Commit Changes

```
fix(record): populate Java record components, fix array equals/hashCode, emit base constructor args, and distinguish record struct from record class
```
