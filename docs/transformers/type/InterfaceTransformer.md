# InterfaceTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs`

## 1. C# Language Feature Converted

`InterfaceTransformer` converts C# `interface` declarations to Java `interface` declarations. It handles:

- Base-interface inheritance lists
- Generic type parameters (including `where` constraints as Java `extends` bounds)
- Interface member dispatch (methods, properties, events, indexers)
- `[Obsolete]` → `@Deprecated`
- JavaDoc comment generation from XML `<summary>` docs

```csharp
// C#
public interface IShape<T> where T : IComparable<T>
{
    string Name { get; }
    T Compute(T input);
}

// Java
public interface IShape<T extends Comparable<T>> {
    String getName();
    T compute(T input);
}
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — C# 8 default interface method implementations are silently discarded

C# 8 introduced default method implementations:
```csharp
public interface IPlugin {
    void Execute();
    void Log(string msg) => Console.WriteLine(msg);   // default impl
}
```

The transformer dispatches each member through `TransformInterfaceMethod`, which only emits `abstract` method signatures. The method body (the expression or block) is read from the syntax node but then thrown away. Java 8+ `default` methods are fully supported; the transformer should detect a body and emit `default { ... }` instead of an abstract signature.

### Issue 2 — Static and `const` interface members (C# 11 static abstracts) not handled

```csharp
public interface IFactory<T> {
    abstract static T Create();   // C# 11 static abstract
    const int MaxSize = 100;      // Not valid in C# but conceptually
}
```

`StaticKeyword` on an interface method is not detected by the dispatch in `TransformInterfaceMethod`. The method falls through to the generic branch and emits an `abstract` instance method. The `static` modifier and body are lost.

### Issue 3 — Nested type declarations inside interfaces are unhandled

```csharp
public interface IComplex {
    enum State { Active, Inactive }
    class Builder { ... }
}
```

When the member is a `TypeDeclarationSyntax`, the transformer has no branch for it and silently skips it. Java does allow nested types in interfaces; they should be emitted as `static` nested members.

### Issue 4 — New `TransformerFactory` allocated for every member

```csharp
foreach (var member in interfaceDecl.Members)
{
    var factory = new TransformerFactory(context);
    ...
}
```

A fresh `TransformerFactory` (and therefore a fresh `ConversionContext` wrapper) is instantiated per member, creating significant allocation pressure for interfaces with many members. `TransformerFactory` should be created once outside the loop.

### Issue 5 — Property accessor mapping doesn't set `abstract` on property methods

When a property is converted to a pair of `getXxx()`/`setXxx()` methods for an interface, those methods must be `abstract` (or have no body, which is the Java interface default). The transformer relies on a shared property-transformer path that may add a concrete body if the accessor has one.

### Issue 6 — Event declarations in interfaces emit incorrect boilerplate

An interface can declare events:
```csharp
public interface INotifier {
    event EventHandler Changed;
}
```

`EventFieldTransformer` generates an `ArrayList`-backed implementation (add/remove/fire methods with concrete bodies). This is appropriate for a class but not for an interface — an interface event should produce abstract `addChangedListener` / `removeChangedListener` signatures only.

## 3. Proposed Fixes

### Fix 1 — Emit `default` methods for members with bodies

```csharp
if (method.Body != null || method.ExpressionBody != null)
{
    javaMethod.Modifiers.Add("default");
    javaMethod.Body = TransformBody(method);
}
else
{
    // abstract (no-modifier in Java interface)
}
```

### Fix 2 — Handle `static abstract` and `static` interface members

Detect `StaticKeyword` on the member and add `static` to the emitted method. If it also has a body, emit `static { body }`.

### Fix 3 — Add nested type dispatch

```csharp
case TypeDeclarationSyntax nested:
    var nestedDoc = factory.GetTransformerFor(nested).Transform(nested, context);
    javaInterface.NestedTypes.Add((JavaTypeDeclaration)nestedDoc);
    break;
```

### Fix 4 — Move `TransformerFactory` creation outside the loop

```csharp
var factory = new TransformerFactory(context);
foreach (var member in interfaceDecl.Members) { ... }
```

### Fix 5 — Ensure property-getter/setter methods in interfaces have no body

After the property transformer runs, strip any auto-generated bodies if the source property had no accessor implementations.

### Fix 6 — Emit interface-compatible event signatures

Override the event generation path for interface context: produce only `void addXxxListener(...)` and `void removeXxxListener(...)` signatures without a body or backing list.

## 4. Commit Changes

```
fix(interface): emit default methods, handle static abstract members, nested types, and correct event/property interface stubs; fix per-member TransformerFactory allocation
```
