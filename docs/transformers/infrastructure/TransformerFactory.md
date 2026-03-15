# TransformerFactory

**Source:** `src/CSharpToJava.Core/Transformers/TransformerFactory.cs`

## 1. C# Language Feature Converted

`TransformerFactory` is not itself a converter of any particular C# language construct. It is the **central factory** that instantiates every specialized transformer in the pipeline. The conversion pipeline calls factory methods such as `CreateClassTransformer()`, `CreateMethodTransformer()`, etc., to obtain the appropriate transformer for a given C# syntax node before delegating conversion work to it.

## 2. Deep Analysis of Current Code Issues

### Issue 1 — No instance caching; new object per call

Every factory method uses `new XyzTransformer()` directly:

```csharp
public ITypeTransformer CreateClassTransformer() => new ClassTransformer();
public IMemberTransformer CreateMethodTransformer() => new MethodTransformer();
```

Each call to a factory method allocates a fresh transformer object even though all transformers are stateless. This means consumers that call the factory in a tight loop (e.g., `InterfaceTransformer.ProcessInterfaceMember` creates a new `TransformerFactory` **per member**) generate unnecessary garbage-collection pressure.

### Issue 2 — Concrete return types break polymorphism for two methods

```csharp
public DelegateTransformer CreateDelegateTransformer() => new DelegateTransformer();
public EventFieldTransformer CreateEventFieldTransformer() => new EventFieldTransformer();
```

These two methods return the concrete class, not an interface. Every other factory method returns `ITypeTransformer`, `IMemberTransformer`, etc. The inconsistency forces callers to depend on the concrete type, making future refactoring or testing harder.

### Issue 3 — `OperatorTransformer` has no factory method

`OperatorTransformer` is used directly in `ClassTransformer` via `new OperatorTransformer()` but there is no `CreateOperatorTransformer()` factory method. This means the factory pattern is bypassed for operator conversion and any future centralisation (e.g., adding a logging decorator) would miss operator transformations.

### Issue 4 — No support for replacing transformers (no DI seam)

The factory is a concrete class with no interface and no constructor injection. Unit tests cannot substitute a mock transformer, and there is no way to override a single transformer implementation without subclassing the entire factory.

### Issue 5 — `ExpressionTransformerFacade.Instance` is referenced directly in the factory

```csharp
public IExpressionTransformer CreateExpressionTransformer() => ExpressionTransformerFacade.Instance;
```

This is the only factory method that correctly returns a singleton. The same pattern should be applied to all transformers.

## 3. Proposed Fixes

### Fix 1 — Add static singleton instances for all transformers

Replace every `new XyzTransformer()` with a lazily created singleton held in a `static readonly` field:

```csharp
private static readonly ITypeTransformer _classTransformer = new ClassTransformer();
public ITypeTransformer CreateClassTransformer() => _classTransformer;
```

Because all transformers are stateless, sharing a single instance is safe across threads.

### Fix 2 — Return `IMemberTransformer` for `EventFieldTransformer`; extract interface if needed

Define an `IEventTransformer` interface (or reuse `IMemberTransformer`) and have `CreateEventFieldTransformer()` return the interface type. Similarly for `DelegateTransformer`.

### Fix 3 — Add `CreateOperatorTransformer()`

```csharp
private static readonly OperatorTransformer _operatorTransformer = new OperatorTransformer();
public OperatorTransformer CreateOperatorTransformer() => _operatorTransformer;
```

Update all `new OperatorTransformer()` call sites in `ClassTransformer` and `StructTransformer` to use the factory.

### Fix 4 — Extract `ITransformerFactory` interface

Introduce an interface so consumers can depend on abstraction and tests can inject mock transformer instances.

## 4. Commit Changes

```
refactor(factory): cache transformer instances, add missing OperatorTransformer factory method, and extract ITransformerFactory interface
```
