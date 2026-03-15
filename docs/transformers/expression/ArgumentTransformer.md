# ArgumentTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs`

## 1. C# Language Feature Converted

`ArgumentTransformer` is intended to convert C# argument lists to Java argument lists. It should handle:

- Positional arguments
- Named arguments (`Foo(y: 2, x: 1)`)
- `ref`, `out`, `in` arguments (pass-by-reference)
- `out var` declarations at the call site
- `params` argument expansion
- Discards (`_`) in out positions

```csharp
// C# argument varieties
Foo(x: 1, y: 2);          // named
Bar(ref myVar);            // ref
Baz(out var result);       // out var
Qux(in readOnlyVal);       // in
dict.TryGetValue(k, out _); // discard
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — The transformer is entirely unimplemented (stub only)

`TransformArgumentList` returns a hard-coded `/* TODO: argument list */` comment:
```csharp
public string TransformArgumentList(ArgumentListSyntax args, ConversionContext context)
    => "/* TODO: argument list */";
```

No argument is actually transformed. This means **every call site** that routes through `ArgumentTransformer` produces a non-functional Java comment in place of the actual arguments.

### Issue 2 — `Transform()` throws `NotImplementedException`

The `IExpressionTransformer.Transform` entry point:
```csharp
public JavaSyntaxNode Transform(ExpressionSyntax node, ConversionContext context)
    => throw new NotImplementedException("ArgumentTransformer.Transform not implemented");
```

This throws unconditionally, making it impossible to use `ArgumentTransformer` through the standard `ExpressionTransformerFacade` dispatch path.

### Issue 3 — `ArgumentTransformer` is never registered in `ExpressionTransformerRegistry`

The static constructor of `ArgumentTransformer` does not register it, and `ExpressionTransformerRegistry` does not register it either. Even if `Transform` were implemented, there is no `SyntaxKind` that routes to it. Argument nodes (`ArgumentSyntax`) appear as children of `ArgumentListSyntax` rather than as standalone expressions, so they would not naturally be handled by the expression registry; but `ArgumentListSyntax` itself is also unregistered.

### Issue 4 — Named arguments are not reordered to positional in any call path

Since `ArgumentTransformer` is a stub, named arguments are never handled. The `InvocationExpressionTransformer` calls argument transformation but `ArgumentTransformer` is not involved, so named arguments appear literally in the generated Java (e.g., `foo(y: 2, x: 1)`) which is invalid Java syntax.

### Issue 5 — `ref`/`out` argument transformation (holder-object pattern) is orphaned here

The intended design (based on comments in `InvocationExpressionTransformer`) is that `ArgumentTransformer` handles `ref`/`out` by generating a holder-object wrapper. Since the transformer is unimplemented, `ref`/`out` arguments produce only comment annotations (`/* ref */`, `/* out */`) instead of functional holder objects.

## 3. Proposed Fixes

### Fix 1 — Implement `TransformArgumentList`

```csharp
public string TransformArgumentList(ArgumentListSyntax args, ConversionContext context)
{
    var transformed = args.Arguments.Select(arg => TransformSingleArgument(arg, context));
    return string.Join(", ", transformed);
}
```

### Fix 2 — Implement `Transform` for `ArgumentSyntax` nodes

```csharp
public JavaSyntaxNode Transform(ExpressionSyntax node, ConversionContext context)
{
    if (node is not ArgumentSyntax arg)
        throw new ArgumentException($"Expected ArgumentSyntax, got {node.GetType().Name}");
    return new JavaRawExpression(TransformSingleArgument(arg, context));
}
```

### Fix 3 — Reorder named arguments to positional using the semantic model

```csharp
if (arg.NameColon != null)
{
    // Look up the parameter index from the symbol info
    var paramName = arg.NameColon.Name.Identifier.Text;
    // Reorder arguments to match positional order
}
```

### Fix 4 — Implement the holder-object pattern for `ref`/`out` arguments

```java
// For: dict.TryGetValue(key, out var value)
// Generate:
Type[] _valueHolder = new Type[1];
boolean _result = dict.tryGetValue(key, _valueHolder);
Type value = _valueHolder[0];
```

This requires injection of pre-statements; plumb a `PreStatements` collection through `ConversionContext`.

### Fix 5 — Register `ArgumentTransformer` for use in the pipeline

Even if arguments are not standalone expressions, register the transformer so it can be unit-tested and called explicitly from `InvocationExpressionTransformer`.

## 4. Commit Changes

```
fix(argument): implement TransformArgumentList, reorder named args to positional, implement ref/out holder-object pattern, and register ArgumentTransformer in the pipeline
```
