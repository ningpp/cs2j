# ClassTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`

## 1. C# Language Feature Converted

`ClassTransformer` converts `ClassDeclarationSyntax` (regular classes) and `MergedTypeDeclaration` (partial classes assembled from multiple files) into `JavaClassDeclaration` nodes. It handles the full range of class-level C# features:

- **Inheritance chain**: base-class → `extends`, interfaces → `implements`.
- **Generics**: type parameters with constraints.
- **Partial class merging**: deduplication of members across partial declarations using a content-based key.
- **Member dispatch**: delegates each member (`MethodDeclarationSyntax`, `PropertyDeclarationSyntax`, `FieldDeclarationSyntax`, etc.) to the appropriate member transformer.
- **Bridge methods**: adds synthesized Java bridge methods for `IComparable`, `Iterator`, `Iterable`, `Cloneable`, `Comparable`, `List`, and `IRectangle` interfaces so generated code satisfies Java's interface contracts.

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `sealed`, `abstract`, and `static` modifiers are dropped

`ConvertModifiers()` maps C# modifiers to `JavaModifiers`, but the switch arms for `sealed`, `abstract`, and `static` class modifiers are absent. As a result:

- `sealed class Foo` → not `final class Foo` in Java. Any class that should be non-subclassable is silently made subclassable.
- `abstract class Foo` → the class is generated without `abstract`, causing compile errors when abstract methods exist with no body.
- `static class Foo` (utility classes) → not mapped to a Java class with a private constructor pattern or `final` modifier.

### Issue 2 — Static initializer blocks are silently dropped

C# `static MyClass() { ... }` is a static constructor. After merging partial types, nothing in `ProcessMember()` emits a Java `static { ... }` initializer block for `ConstructorDeclarationSyntax` nodes with `static` modifiers. The static initialization code is lost entirely.

### Issue 3 — `ICollection<T>` → `Iterable` substitution is too aggressive

```csharp
if (iface.Name == "ICollection" && ...)
    mappedIface = mappedIface.Replace("Collection", "Iterable");
```

This replacement is applied to **all** `ICollection<T>` implementations, even when a class provides its own full `ICollection<T>` implementation (e.g., `CollectionBase` subclasses). The generated class then claims to implement `Iterable<T>` while the body may have `add()`, `remove()`, and `size()` methods that don't satisfy `Iterable`'s `iterator()` contract.

### Issue 4 — `protected internal` modifier merges incorrectly

The individual modifier conversion maps both `protected` and `internal` to their respective `JavaModifiers` values, which can result in both `Protected` and `Public` being set simultaneously. While `FieldTransformer` includes a cleanup (`result &= ~JavaModifiers.Public` when `Protected` is also set), `ClassTransformer.ConvertModifiers()` does not apply this cleanup, meaning a `protected internal` class could emit `public protected` modifiers.

### Issue 5 — Partial type deduplication misses records and other member kinds

The deduplication key logic only covers `MethodDeclarationSyntax`, `PropertyDeclarationSyntax`, `FieldDeclarationSyntax`, and `ConstructorDeclarationSyntax`. Other member types such as `EventDeclarationSyntax`, `DelegateDeclarationSyntax`, and nested `TypeDeclarationSyntax` fall to the `other:{hashCode}` branch, which never deduplicates them — every partial declaration of that event or nested type will be included, possibly producing duplicate Java members.

### Issue 6 — `MarshalByRefObject` skip logic uses string comparison, not symbol equality

```csharp
if (baseType.Name != "MarshalByRefObject" &&
    baseType.ToDisplayString() != "System.MarshalByRefObject") continue;
```

This hardcoded comparison works for the common case but would silently fail if `MarshalByRefObject` appears via an alias or is referenced from a differently-named assembly.

## 3. Proposed Fixes

### Fix 1 — Map `sealed`, `abstract`, and `static` class modifiers

```csharp
SyntaxKind.SealedKeyword    => JavaModifiers.Final,
SyntaxKind.AbstractKeyword  => JavaModifiers.Abstract,
// static classes → emit private no-arg constructor and Final class
SyntaxKind.StaticKeyword    => JavaModifiers.Final,
```

For `static class`, additionally generate a private default constructor to prevent instantiation.

### Fix 2 — Emit Java `static { ... }` blocks for static constructors

In `ProcessMember`, detect `ConstructorDeclarationSyntax` with `static` modifier and emit a `JavaStaticInitializerBlock` instead of a `JavaConstructorDeclaration`.

### Fix 3 — Make `ICollection` substitution configurable or conditional

Only substitute `ICollection → Iterable` when the class does not declare any of the `ICollection<T>` members (`Add`, `Remove`, `Contains`, `Count`). Add an opt-out flag in `ConversionOptions`.

### Fix 4 — Apply `protected internal` cleanup in `ClassTransformer.ConvertModifiers()`

After the modifier loop, add:
```csharp
if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
    result &= ~JavaModifiers.Public;
```

### Fix 5 — Extend deduplication key to cover event, delegate, and nested type members

```csharp
EventDeclarationSyntax e => $"ev:{e.Identifier.Text}",
DelegateDeclarationSyntax d => $"del:{d.Identifier.Text}",
TypeDeclarationSyntax t => $"type:{t.Identifier.Text}",
```

### Fix 6 — Use semantic symbol comparison for `MarshalByRefObject` detection

```csharp
var isMarshallByRef = resolvedType.SpecialType == SpecialType.None &&
    resolvedType.ToDisplayString() == "System.MarshalByRefObject";
```

Or better, cache the symbol for `MarshalByRefObject` from the compilation and use `SymbolEqualityComparer.Default.Equals`.

## 4. Commit Changes

```
fix(class-transformer): map sealed/abstract/static modifiers, emit static initializer blocks, and fix protected-internal modifier merging
```
