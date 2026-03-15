# ExpressionTransformerRegistry

**Source:** `src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs`

## 1. C# Language Feature Converted

`ExpressionTransformerRegistry` is the **central self-registration table** that maps Roslyn `SyntaxKind` enum values to `IExpressionTransformer` implementations. It does not itself convert any C# language feature — instead it enables the overall expression dispatch system by:

1. Providing a thread-safe `ConcurrentDictionary<SyntaxKind, IExpressionTransformer>` store.
2. Exposing a `Register(SyntaxKind[], IExpressionTransformer)` method used by each specialized transformer's own static constructor to self-register.
3. Exposing `GetTransformer(SyntaxKind)` for lookup by the `ExpressionTransformerFacade`.

The static constructor of the registry explicitly triggers initialization of all 13 specialized transformers to ensure they self-register before any `GetTransformer` call:

```csharp
static ExpressionTransformerRegistry()
{
    _ = BinaryExpressionTransformer.Instance;
    _ = UnaryExpressionTransformer.Instance;
    // … 11 more
}
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Multiple C# 9–11 expression kinds are not registered

The following `SyntaxKind` values can appear in real C# code but have no registered handler:

| `SyntaxKind` | C# Feature | Expected Java Output |
|---|---|---|
| `SuppressNullableWarningExpression` | `x!` | identity (strip the `!`) |
| `TupleExpression` | `(a, b)` | requires `Pair<A,B>` or record |
| `DeclarationExpression` | `out var x` | variable declaration |
| `RefExpression` | `ref x` | reference wrapper |
| `UnsignedRightShiftExpression` | `x >>> y` | `x >>> y` (Java supports `>>>`) |
| `StackAllocArrayCreationExpression` | `stackalloc T[n]` | `new T[n]` approximation |
| `ImplicitStackAllocArrayCreationExpression` | `stackalloc[]{ ... }` | `new T[]{ ... }` |
| `CollectionExpression` | `[1, 2, 3]` (C# 12) | `List.of(1, 2, 3)` |

All of these cause a `NotSupportedException` crash at runtime when encountered.

### Issue 2 — `ArgumentTransformer` is never registered

`ArgumentTransformer` exists in the codebase and its static constructor comments say "No self-registration needed". This means it contributes nothing to the registry and its `Transform()` method will never be called via the normal dispatch path. Named argument processing is therefore orphaned.

### Issue 3 — Static-constructor initialization order is brittle

The registry static constructor hard-codes the names of all 13 transformers. Adding a new transformer requires updating this list. If a developer creates a transformer but forgets to add its `Instance` touch here, it will never register.

### Issue 4 — `ConcurrentDictionary` is unnecessary overhead after initialization

All registrations happen in static constructors before any `GetTransformer` call is possible (CLR guarantees static constructor ordering). After initialization, the dictionary is read-only in practice. A plain `Dictionary<SyntaxKind, IExpressionTransformer>` would have slightly lower lookup overhead; however this is a minor concern.

### Issue 5 — No diagnostic when a kind is registered twice

`_transformers[kind] = transformer;` silently overwrites an existing registration. If two transformers both claim the same `SyntaxKind`, the latter wins with no warning during startup.

## 3. Proposed Fixes

### Fix 1 — Register all missing expression kinds

Add a catch-all fallback transformer for unhandled kinds:

```csharp
// In ExpressionTransformerRegistry static ctor
_ = SuppressNullableWarningExpressionTransformer.Instance;  // new
_ = TupleExpressionTransformer.Instance;                    // new
_ = DeclarationExpressionTransformer.Instance;              // new
```

For kinds that need only a simple rule (e.g., strip `!` from `SuppressNullableWarningExpression`), a small inline registration is sufficient:

```csharp
Register(new[] { SyntaxKind.SuppressNullableWarningExpression },
    new AnonymousTransformer(node =>
        facade.Transform(((PostfixUnaryExpressionSyntax)node).Operand, context)));
```

### Fix 2 — Implement and register `ArgumentTransformer` properly

Either rename `ArgumentTransformer` to a utility class (non-`IExpressionTransformer`) or register it for the `SyntaxKind` values relevant to argument expressions to make the class consistent with the rest of the design.

### Fix 3 — Use a `[TransformerRegistration]` attribute to auto-discover transformers

Scan the assembly for types with `[TransformerRegistration]` attribute in the registry static constructor, call `Instance` on each, and eliminate the hard-coded list:

```csharp
// Auto-discover all transformers that declare [TransformerRegistration]
foreach (var type in Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.GetCustomAttribute<TransformerRegistrationAttribute>() != null))
{
    _ = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
}
```

### Fix 4 — Warn on duplicate registration during debug builds

```csharp
public static void Register(SyntaxKind[] kinds, IExpressionTransformer transformer)
{
    foreach (var kind in kinds)
    {
        if (_transformers.ContainsKey(kind))
            Debug.WriteLine($"[ExpressionTransformerRegistry] WARNING: {kind} already registered; overwriting.");
        _transformers[kind] = transformer;
    }
}
```

## 4. Commit Changes

```
feat(registry): register missing C# 9-12 expression kinds (SuppressNullableWarning, Tuple, Declaration, stackalloc, Collection, >>>) and add duplicate-registration warning
```
