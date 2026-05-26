# Design: Generic `new()` Constraint Call-Site Inlining

## Problem

C# generic methods with `where TC : ICollection<TS>, new()` instantiate type parameters
via `new TC()`. The CLR resolves the concrete type at runtime. Java has no `new T()`
equivalent (type erasure). The converter currently hardcodes `(TC) new ArrayList<>()`,
which produces `ClassCastException` when `TC` is bound to a non-ArrayList type at the
call site.

### Concrete Example

C# `CollectionUtilities.AddToMap` uses `new TC()` where `TC : ICollection<TS>, new()`.
When called from `BundleRouter`, `TC` is bound to `Set<EdgeGeometry>` (MSAGL custom
`Set` class, extends `AbstractCollection`). The converter emits `(TC) new ArrayList<>()`,
which fails at runtime because `ArrayList` cannot be cast to `Set`.

### Affected Code

- [ObjectCreationTransformer.cs:208-221](src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L208-L221) — hardcodes `new ArrayList<>()` for collection-constrained type params
- [CollectionUtilities.cs:15](E:/agl-master/GraphLayout/MSAGL/Core/DataStructures/CollectionUtilities.cs#L15) — sole `new()` constraint usage in MSAGL
- [BundleRouter.cs:272-279](E:/agl-master/GraphLayout/MSAGL/Routing/Spline/Bundling/BundleRouter.cs#L272-L279) — call site where inlining should trigger

## Design: Call-Site Inlining via `InvocationExpressionTransformer`

### Architecture

Two coordinated changes, both in the converter:

**Change 1 — `ObjectCreationTransformer`**: Replace the hardcoded `new ArrayList<>()`
with a diagnostic fallback so uncovered scenarios are observable.

**Change 2 — `InvocationExpressionTransformer`** (core): When processing a generic
method call, if the callee has `new()`-constrained type parameters used in `new T()`
expressions AND concrete type arguments are resolvable at the call site, generate an
inlined block with concrete types substituted — instead of emitting the method call.

### Data Flow

```
Call-site (BundleRouter.cs)               Callee (CollectionUtilities.cs)
┌────────────────────────────┐            ┌──────────────────────────────┐
│ CollectionUtilities.       │            │ AddToMap<TS,T,TC>(          │
│   AddToMap(res,            │ --detect─▶ │   Dictionary<T,TC>, T, TS) │
│            cdtEdge, edge)  │  new()     │   where TC : ICollection<TS>│
│                            │  constraint│          , new()            │
│ TC binding = Set<EdgeGeom> │            │ {                            │
│                            │  ◀─inline─ │   TC tc;                    │
│ Emit:                      │  substitute│   if (!dict.TryGet(...))    │
│  Set<EdgeGeom> tc;         │            │     tc = new TC();  ← key   │
│  tc = res.get(cdtEdge);    │            │   tc.Add(value);            │
│  if (tc == null) {         │            │ }                            │
│    tc = new Set<>();       │            └──────────────────────────────┘
│    res.put(cdtEdge, tc);   │
│  }                         │
│  tc.add(edge);             │
└────────────────────────────┘
```

### Implementation Steps

#### Step 1: Detection helper in `ObjectCreationTransformer`

Add a public static method:
```csharp
public static bool HasNewConstraintObjectCreation(IMethodSymbol method)
```
- Iterates the method's type parameters
- For each parameter with `HasConstructorConstraint == true`
- Checks if the method body syntax contains `ObjectCreationExpression` nodes
  whose type resolves to that type parameter
- Returns true if any such pattern exists

#### Step 2: Type binding resolution

In `InvocationExpressionTransformer`, after obtaining `IMethodSymbol` for the call:
- Call `HasNewConstraintObjectCreation(methodSymbol.OriginalDefinition)`
- If true, use `methodSymbol.TypeArguments` to build a binding map:
  `Dictionary<ITypeParameterSymbol, ITypeSymbol>`
- Verify all bound types are concrete (not type parameters themselves)

#### Step 3: Method body inlining

New method `TryInlineNewConstraintCall`:
1. Get the method body syntax from `IMethodSymbol.DeclaringSyntaxReferences`
2. Walk the body to identify the `new T()` pattern and surrounding control flow
3. For `AddToMap`-like patterns (TryGetValue → if-null → new TC().Add):
   - Map `TC` → concrete Java type via `context.MapType(boundType)`
   - Map `TS` → concrete Java element type
   - Generate inline block:
     ```java
     { ConcreteType tc; tc = dict.get(key);
       if (tc == null) { tc = new ConcreteType(); dict.put(key, tc); }
       tc.add(value); }
     ```
4. Return the generated Java string or null if pattern unrecognized

#### Step 4: Hook into `InvocationExpressionTransformer`

In both `TransformMemberInvocation` and the `GenericNameSyntax` path:
- After resolving `methodSymbol`, before generating the normal call string
- Check `HasNewConstraintObjectCreation(methodSymbol)`
- If true and binding is concrete, call `TryInlineNewConstraintCall(...)`
- If inlining succeeds, return the inlined block as the expression/statement

#### Step 5: Fallback improvement in `ObjectCreationTransformer`

Replace the current hardcoded fallback:
```csharp
// Before:
return $"({typeName}) new ArrayList<>()";

// After:
context.AddDiagnostic(DiagnosticSeverity.Warning,
    $"new() constraint on '{typeParameter.Name}' fell back to ArrayList; " +
    "call-site inlining should have handled this.");
return $"({typeName}) new ArrayList<>()";
```

### Scope and Risk

- Currently one affected call path: `CollectionUtilities.AddToMap` → called from
  `BundleRouter.GetPathsOnCdtEdge`
- The mechanism is general and applies automatically to any generic method with
  `new()` constraints encountered during conversion
- If type binding at a call site is unresolved (nested generics, error types), the
  fallback still emits `new ArrayList<>()` with a diagnostic — correct behavior
  degrades gracefully
- No changes to C# source or generated Java output required

## Verification

1. Build the converter with the changes
2. Re-convert the MSAGL GraphLayout project to `E:\z5`
3. Verify `CollectionUtilities.java` no longer emits `(TC) new ArrayList<>()`
   (or emits it with a diagnostic, but call sites all use inlined versions)
4. Verify `BundleRouter.java` generates correct inline `Set<>` construction
5. Run `dotnet test` to verify no regressions
6. Compile and run the generated Java project to confirm `ClassCastException` is gone
