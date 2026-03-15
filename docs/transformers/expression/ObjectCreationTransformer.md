# ObjectCreationTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs`

## 1. C# Language Feature Converted

`ObjectCreationTransformer` converts C# object creation expressions to Java `new` expressions. It handles:

- Simple object creation: `new Foo(args)`
- Target-typed `new` (C# 9): `Foo x = new(args)`
- Array creation: `new int[5]`, `new int[] { 1, 2, 3 }`, `new int[,]` (multi-dim)
- Object initializers: `new Foo { X = 1, Y = 2 }`
- Collection initializers: `new List<int> { 1, 2, 3 }`
- Stack-alloc: `stackalloc int[5]`
- Anonymous-type creation: `new { Name = "Alice", Age = 30 }`

```csharp
// C#
var p = new Point(1, 2);
var list = new List<int> { 1, 2, 3 };
var arr  = new int[5];

// Java
Point p = new Point(1, 2);
List<Integer> list = new ArrayList<>(Arrays.asList(1, 2, 3));
int[] arr = new int[5];
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Object initializers use double-brace pattern (memory leak, prevents `final` fields)

For object initializers, the transformer generates Java's double-brace initialization:
```java
new Foo() {{
    setX(1);
    setY(2);
}}
```

Double-brace initialization creates an anonymous subclass of `Foo` on every call. This:
1. Causes a memory leak when the object is serialized or used in a collection (the anonymous class holds a reference to the outer `this`).
2. Creates a new `.class` file per usage site, bloating the JVM classloader.
3. Prevents `Foo` from being `final` (anonymous classes cannot extend final classes).
4. May break `equals()` checks that test the runtime class.

The correct pattern is to create the object and then call setters separately as statements.

### Issue 2 — Collection initializer expressions are not all in the switch

```csharp
new Dictionary<string, int> { { "a", 1 }, { "b", 2 } }
new HashSet<int> { 1, 2, 3 }
```

The switch for collection initializer kinds handles `List`-like and `Dictionary`-like patterns, but `HashSet` (and other set types) may fall through to a default that produces incorrect Java. `HashSet` should use `new HashSet<>(Arrays.asList(...))` or `Set.of(...)`.

### Issue 3 — Multi-dimensional array `new int[3, 4]` mapped to flat

```csharp
int[,] matrix = new int[3, 4];
```

The transformer emits `new int[3]` or `new int[3, 4]` (invalid Java). The correct Java equivalent for a 2D array is `new int[3][4]`. For jagged arrays (`int[][]`) the translation is different. The transformer does not distinguish or handle multi-dimensional arrays correctly.

### Issue 4 — Anonymous-type creation is not implemented

```csharp
var person = new { Name = "Alice", Age = 30 };
```

`AnonymousObjectCreationExpressionSyntax` has no implementation; it falls through to a `/* TODO: anonymous type */` comment. In Java, anonymous types do not exist. The closest equivalent is a `java.util.Map.of("Name", "Alice", "Age", 30)` for data-only objects, or a generated `record` for structured access. Neither is emitted.

### Issue 5 — `stackalloc` has no Java equivalent and is silently dropped

```csharp
Span<int> buffer = stackalloc int[256];
```

`StackAllocArrayCreationExpression` is not handled; the transformer emits a placeholder or nothing. A `new int[256]` on the heap is semantically equivalent for correctness (different for performance). A comment should be emitted explaining the semantics difference.

## 3. Proposed Fixes

### Fix 1 — Replace double-brace with sequential setter calls

The transformer should return the `new` expression as-is and inject setter calls as pre/post-statements:
```csharp
string tmpVar = context.GenerateTempVarName();
context.PostStatements.Add($"var {tmpVar} = new {typeName}({args});");
foreach (var init in initializer)
    context.PostStatements.Add($"{tmpVar}.{setter}({value});");
return tmpVar;
```

Or for inline contexts, emit a builder-style chain when a builder exists.

### Fix 2 — Add `HashSet` and other set types to the collection initializer switch

```csharp
case "HashSet" when context.Options.JavaVersion >= JavaVersion.Java9:
    return $"new HashSet<>(Set.of({items}))";
case "HashSet":
    return $"new HashSet<>(Arrays.asList({items}))";
```

### Fix 3 — Translate multi-dimensional arrays to jagged Java arrays

For `new T[d1, d2]` emit `new T[d1][d2]`. For `new T[d1, d2, d3]` emit `new T[d1][d2][d3]`. The dimensions are in a comma-separated rank specifier.

### Fix 4 — Translate anonymous types to `Map.of` or an inline record

```csharp
if (anonExpr.Initializers.All(HasSimpleStringKey))
{
    var pairs = anonExpr.Initializers.Select(i => $"\"{i.Name}\", {TransformExpression(i.Expression, context)}");
    return $"Map.of({string.Join(", ", pairs)})";
}
// fallback: /* TODO: anonymous type - consider extracting a record */
```

### Fix 5 — Map `stackalloc` to heap allocation with comment

```csharp
case StackAllocArrayCreationExpressionSyntax stackAlloc:
    string javaAlloc = TransformAsArrayCreation(stackAlloc, context);
    return $"/* C# stackalloc — allocated on heap in Java */ {javaAlloc}";
```

## 4. Commit Changes

```
fix(object-creation): replace double-brace with post-statements, add HashSet/set initializers, fix multi-dim array to jagged syntax, translate anonymous types to Map.of, and map stackalloc to heap with comment
```
