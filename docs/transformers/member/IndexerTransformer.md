# IndexerTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Member/IndexerTransformer.cs`

## 1. C# Language Feature Converted

`IndexerTransformer` converts C# indexer declarations to Java methods. C# indexers allow instances to be accessed using array-bracket syntax:

```csharp
// C#
public class Container {
    private int[] _data;
    public int this[int index] {
        get => _data[index];
        set => _data[index] = value;
    }
}
```

Because Java has no direct equivalent of an indexer, the transformer generates a named getter method and (optionally) a named setter method:

```java
// Java
public class Container {
    private int[] _data;
    public int get(int index) { return _data[index]; }
    public void set(int index, int value) { _data[index] = value; }
}
```

The method names (`get`/`set`) are configurable per type-mapping configuration. The transformer handles:
- Single-parameter and multi-parameter indexers
- `get`-only and `set`-only accessors
- `init`-only (C# 9) accessors
- Expression-body accessors

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Method names `get`/`set` clash with `Map` interface in Java

Java's `java.util.Map` interface declares `get(Object key)` and `put(K key, V value)`. If the containing class implements `Map` (which is one common translation of `IDictionary`), naming the indexer methods `get` and `set` will:
- Conflict with `Map.get` (wrong signature or shadowing)
- Not satisfy the `Map.put` contract (wrong method name)

Similarly, `java.util.List` has `get(int index)` — if the class implements `List`, a custom `get(int)` may accidentally override it with incorrect semantics.

### Issue 2 — Multi-argument indexers erroneously emit bracket syntax

For multi-parameter indexers:
```csharp
public T this[int row, int col] { get => _matrix[row, col]; ... }
```

The generated Java should be:
```java
public T get(int row, int col) { return _matrix[row][col]; }
```

However, when the transformer processes the access expression inside the body, `ElementAccessTransformer` may still emit `matrix[row, col]` (C# multi-dim syntax) instead of producing a proper method call or `[][]]` chain.

### Issue 3 — Non-void setter return type is non-idiomatic Java

The C# indexer setter has an implicit `void` return type (the `value` keyword is used, not returned). The transformer correctly emits `void` for the setter. However, when the type mapping configuration overrides the setter name to something like `put`, the return type should match the `Map.put` contract (which returns the previous value). The transformer always emits `void` regardless of the configured method name.

### Issue 4 — `init` accessor is treated identically to `set`

C# 9 `init`-only accessors can only be called during object initialisation:
```csharp
public int this[int i] {
    get => _arr[i];
    init => _arr[i] = value;
}
```

The transformer currently maps `init` to `set` without distinguishing the restricted call context. The generated Java `set` method is callable from anywhere, losing the construction-only restriction. A comment should be emitted at minimum.

### Issue 5 — Read-only indexers on interfaces generate a setter stub

When an interface defines a get-only indexer, the transformer may generate both a `get` signature and a `set` signature depending on how the accessor list is processed. This causes an interface to declare a setter that implementing classes must implement, even though no setter was required in C#.

## 3. Proposed Fixes

### Fix 1 — Use context-aware method name selection

Check the implemented interfaces:
```csharp
bool implementsMap  = context.ImplementsInterface("Map");
bool implementsList = context.ImplementsInterface("List");
string getterName = implementsList ? "get"
                  : implementsMap  ? "get"
                  : "get";        // configurable via TypeMappings.json
string setterName = implementsMap ? "put" : "set";
```

When renaming to `put`, change the return type to the value type (not `void`) and add a `return null;` fallback.

### Fix 2 — Translate multi-argument indexer access in body

After transforming the accessor body, apply a post-pass that replaces any remaining `expr[a, b]` patterns with `expr[a][b]` for 2-D arrays or a helper method call.

### Fix 3 — Consider `put` return type

```csharp
if (setterName == "put")
{
    setterMethod.ReturnType = valueType;
    // Emit "return null;" as a placeholder; caller must add actual previous-value logic.
}
```

### Fix 4 — Emit a comment for `init`-only accessor

```csharp
if (accessor.IsKind(SyntaxKind.InitAccessorDeclaration))
    setterMethod.LeadingComment = "// C# init-only: should only be called during construction";
```

### Fix 5 — Skip setter generation for get-only indexers in interface context

```csharp
if (context.IsInInterfaceBody && setterAccessor == null)
    // Do not emit a setter
```

## 4. Commit Changes

```
fix(indexer): avoid Map/List method name collisions, handle multi-arg indexers, annotate init-only accessor, and skip setter in interface get-only indexers
```
