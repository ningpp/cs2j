# InvocationExpressionTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`

## 1. C# Language Feature Converted

`InvocationExpressionTransformer` converts C# method invocation expressions to Java method calls. It covers:

- Instance and static method calls
- Generic method calls (`Sort<T>(...)`)
- Extension method calls (rewritten as static or instance calls)
- `nameof(x)` → `"x"` (string literal)
- `typeof(T).Name` → `T.class.getSimpleName()`
- Common BCL method substitutions (`Console.WriteLine` → `System.out.println`, etc.)
- `ref`/`out` argument annotation
- Named argument handling (partial)

```csharp
// C#
list.Add(item);
Console.WriteLine("Hello");
string s = nameof(MyClass);

// Java
list.add(item);
System.out.println("Hello");
String s = "MyClass";
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — No method-name mapping table applied at the call site

The type mappings JSON (`TypeMappings.json`) contains method name mappings (e.g., `Add` → `add`, `Remove` → `remove`, `Contains` → `contains`). `InvocationExpressionTransformer` does not consult this mapping when generating the method name. As a result, `list.Add(x)` generates `list.Add(x)` — which does not compile in Java.

### Issue 2 — `ref`/`out` arguments only get comment annotations with no holder-object pattern

```csharp
dict.TryGetValue(key, out var value);
```

The transformer emits:
```java
dict.tryGetValue(key, /* out */ value)
```

No holder-object (`Type[] valueHolder = new Type[1];`) is generated, and `value` is referenced without being declared. The Java code does not compile.

### Issue 3 — Named arguments not reordered to positional order

```csharp
Foo(y: 2, x: 1);
```

Java has no named arguments. The transformer must reorder the arguments to match the declaration order of the parameters, using the semantic model. Currently named arguments are either emitted as-is (invalid Java syntax) or emitted in source order without reordering.

### Issue 4 — `Arguments.IndexOf(arg)` is O(n) inside an O(n) argument loop

When processing each argument in a call, the code calls `invocationExpr.ArgumentList.Arguments.IndexOf(arg)` to get the argument position. `SeparatedSyntaxList.IndexOf` is O(n). Combined with the outer `foreach` over arguments, this creates an O(n²) loop for call sites with many arguments.

### Issue 5 — Extension method receiver not stripped from static-call path

When an extension method is called as:
```csharp
source.Sort();  // extension method Sort<T>(this IEnumerable<T> source, ...)
```

And the converter does not promote it to an instance method, it should generate:
```java
Extensions.Sort(source)
```

However, if the receiver (`source`) is also passed as the first argument, the argument list may contain `source` **twice** (once as the receiver and once as an explicit first argument).

### Issue 6 — `nameof` with member access only takes the last segment

```csharp
string s = nameof(MyClass.MyProperty);   // should produce "MyProperty"
string t = nameof(MyClass);              // should produce "MyClass"
```

The transformer takes the last `.`-separated segment, which is correct for the first case, but may break with complex `nameof` arguments in corner cases like generic types (`nameof(List<int>)`).

## 3. Proposed Fixes

### Fix 1 — Apply method-name mapping at call site

```csharp
string mappedMethod = context.TypeMappingRegistry.MapMethodName(
    receiverTypeName, originalMethodName);
```

For common BCL mappings (`Add`→`add`, `Remove`→`remove`), this resolves the name before emitting.

### Fix 2 — Implement holder-object pattern for `out`/`ref` arguments

Before the call, inject holder declarations into `context.PreStatements`:
```java
Type[] _holder = new Type[1];
```

Replace the `out var` argument with `_holder`, then after the call:
```java
Type value = _holder[0];
```

### Fix 3 — Reorder named arguments to positional using the semantic model

```csharp
var paramOrder = methodSymbol.Parameters.Select(p => p.Name).ToList();
var reordered  = namedArgs.OrderBy(a => paramOrder.IndexOf(a.NameColon.Name.Identifier.Text));
```

### Fix 4 — Replace `IndexOf` call with an indexed loop

```csharp
for (int i = 0; i < args.Count; i++)
{
    var arg = args[i];
    // use i directly instead of calling IndexOf
}
```

### Fix 5 — Guard against double-insertion of extension method receiver

```csharp
int startIdx = isExtensionInStaticPath ? 1 : 0;
for (int i = startIdx; i < args.Count; i++) { ... }
```

### Fix 6 — Handle `nameof` with generic type arguments

```csharp
// For nameof(List<int>), extract "List" only:
string namedStr = last.Contains('<') ? last[..last.IndexOf('<')] : last;
```

## 4. Commit Changes

```
fix(invocation): apply method-name mappings, implement out/ref holder pattern, reorder named args, fix O(n²) IndexOf loop, guard extension receiver double-pass, and handle generic nameof
```
