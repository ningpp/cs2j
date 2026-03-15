# ElementAccessTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs`

## 1. C# Language Feature Converted

`ElementAccessTransformer` converts C# element access expressions (`a[i]`, `a[i, j]`, `a[^1]`, `a[1..3]`) to Java equivalents. Depending on the type of the collection:

| C# access | Java equivalent |
|---|---|
| `array[i]` | `array[i]` |
| `list[i]` (`List<T>`) | `list.get(i)` |
| `dict[key]` (`Dictionary<K,V>`) | `dict.get(key)` |
| `array[^1]` (from-end) | `array[array.length - 1]` |
| `array[1..3]` (slice) | `Arrays.copyOfRange(array, 1, 3)` |
| Custom indexer `obj[key]` | `obj.get(key)` (or configured setter) |

```csharp
// C#
int last  = arr[^1];
var slice = arr[2..5];
string val = dict["key"];

// Java
int last  = arr[arr.length - 1];
int[] slice = Arrays.copyOfRange(arr, 2, 5);
String val = dict.get("key");
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Multi-argument indexer emits invalid `[]` syntax

For multi-dimensional array access:
```csharp
int val = matrix[row, col];
```

The transformer emits `matrix[row, col]` which is invalid Java. Java multi-dimensional arrays use `matrix[row][col]`. Additionally, `matrix[row, col]` on a non-array type (e.g., a custom 2D container with an indexer) should route to `matrix.get(row, col)` — a method call, not array syntax.

### Issue 2 — Standalone `^n` from-end index computes incorrect placeholder

When a `^n` index is used outside an explicit array-bracket context (e.g., stored as an `Index` struct):
```csharp
Index i = ^2;
```

The transformer emits `(-2)` which is an incomplete placeholder. The correct Java representation depends on the consuming context: when applied to an array of known name, it should be `arrayName.length - 2`. For standalone `Index` values (not in a subscript) there is no direct Java equivalent; a comment should be emitted.

### Issue 3 — Unknown collection types fall through to `.get()` unconditionally

When the element-access target's type cannot be determined from the semantic model (e.g., dynamic, `var`, or an unresolved generic), the transformer defaults to emitting `.get(index)`. This may produce incorrect Java if the target is:
- An array (should use `[i]`)
- A custom class with a different indexer method name
- A `String` (should use `.charAt(i)` for single character)

### Issue 4 — `String[i]` character access generates `.get(i)` instead of `.charAt(i)`

```csharp
char c = str[2];
```

`String` is not an array in either C# or Java, but C# allows `[]` indexing to get a `char`. In Java, this must be `.charAt(2)`. The transformer does not detect that the target is a `String` type and emits `str.get(2)`, which is a compile error (`String` has no `get` method).

### Issue 5 — Slice access does not import `java.util.Arrays`

When `Arrays.copyOfRange(...)` is emitted for slice operations, the transformer does not add `import java.util.Arrays;` to the compilation unit's import list, causing a compile error.

## 3. Proposed Fixes

### Fix 1 — Translate `[row, col]` to `[row][col]` for arrays, `.get(row, col)` for indexers

```csharp
if (args.Count > 1)
{
    if (targetIsArray)
        return string.Join("", args.Select(a => $"[{a}]").Prepend(target));
    else
        return $"{target}.get({string.Join(", ", args)})";
}
```

### Fix 2 — Emit a comment for standalone `^n` Index values

```csharp
if (context.IsStandaloneIndexContext)
    return $"/* C# Index: ^{n} — requires array length to resolve */";
else
    return $"{arrayName}.length - {n}";
```

### Fix 3 — Add type-specific fallback for unknown collections

```csharp
if (targetTypeStr == "String" || targetTypeStr == "string")
    return $"{target}.charAt({args[0]})";
if (targetIsArray)
    return $"{target}[{args[0]}]";
// Default:
return $"{target}.get({args[0]})";
```

### Fix 4 — Detect `String` type and emit `.charAt()`

Apply type detection using the Roslyn semantic model:
```csharp
var typeInfo = context.SemanticModel.GetTypeInfo(elementAccessExpr.Expression);
if (typeInfo.Type?.SpecialType == SpecialType.System_String)
    return $"{target}.charAt({args[0]})";
```

### Fix 5 — Add `java.util.Arrays` import for slice operations

```csharp
if (hasSlice)
    context.AddImport("java.util.Arrays");
```

## 4. Commit Changes

```
fix(element-access): fix multi-arg indexer to use [][] or .get(), annotate standalone ^n Index, detect String type for charAt(), and add Arrays import for slice operations
```
