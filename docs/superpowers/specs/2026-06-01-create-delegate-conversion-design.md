# MethodInfo.CreateDelegate Conversion Design

## Problem

cs2j has no handling for `MethodInfo.CreateDelegate()`. When C# source code uses this API, the transpiler falls through to the generic method call path, producing incorrect Java code like `method.createDelegate(Runnable.class)` — but `java.lang.reflect.Method` has no such method.

## Approach: ReflectionHelper + Proxy

Add `createDelegate` methods to `ReflectionHelper.java` using `java.lang.reflect.Proxy`, and detect `CreateDelegate` calls in `InvocationExpressionTransformer` to emit `ReflectionHelper.createDelegate(...)`.

## Conversion Rules

| C# Call | Args | Java Output |
|---|---|---|
| `mi.CreateDelegate(typeof(Action))` | 1 | `ReflectionHelper.createDelegate(mi, Runnable.class)` |
| `mi.CreateDelegate(typeof(Func<int,string>), target)` | 2 | `ReflectionHelper.createDelegate(mi, target, Function.class)` |
| `mi.CreateDelegate<Action>()` | 0 (generic) | `ReflectionHelper.createDelegate(mi, Runnable.class)` |
| `mi.CreateDelegate<Action>(target)` | 1 (generic) | `ReflectionHelper.createDelegate(mi, target, Runnable.class)` |

## Detection Logic

In `InvocationExpressionTransformer`, detect `CreateDelegate` calls on `MethodInfo` receivers:

```csharp
if (originalMethodName == "CreateDelegate"
    && earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.Reflection.MethodInfo")
```

Handle two syntax forms:
1. Regular: `methodInfo.CreateDelegate(typeof(Action))` — `MemberAccessExpressionSyntax`
2. Generic: `methodInfo.CreateDelegate<Action>()` — `GenericNameSyntax` in MemberAccess

## ReflectionHelper.java Additions

```java
@SuppressWarnings("unchecked")
public static <T> T createDelegate(Method method, Class<T> delegateType) {
    return (T) Proxy.newProxyInstance(
        delegateType.getClassLoader(),
        new Class<?>[] { delegateType },
        (proxy, m, args) -> method.invoke(null, args)
    );
}

@SuppressWarnings("unchecked")
public static <T> T createDelegate(Method method, Object target, Class<T> delegateType) {
    return (T) Proxy.newProxyInstance(
        delegateType.getClassLoader(),
        new Class<?>[] { delegateType },
        (proxy, m, args) -> method.invoke(target, args)
    );
}
```

## Files to Modify

1. `InvocationExpressionTransformer.cs` — Add CreateDelegate detection and conversion
2. `ReflectionHelper.java` — Add createDelegate overloads
3. `DelegateInvocationRewriter.cs` — Verify CreateDelegate return value calls are not mis-rewritten

## Test Coverage

| Scenario | C# Input | Expected Java Output |
|---|---|---|
| Static no-arg delegate | `mi.CreateDelegate(typeof(Action))` | `ReflectionHelper.createDelegate(mi, Runnable.class)` |
| Static with-arg delegate | `mi.CreateDelegate(typeof(Func<int,string>))` | `ReflectionHelper.createDelegate(mi, Function.class)` |
| With target instance | `mi.CreateDelegate(typeof(Action), obj)` | `ReflectionHelper.createDelegate(mi, obj, Runnable.class)` |
| Generic no target | `mi.CreateDelegate<Action>()` | `ReflectionHelper.createDelegate(mi, Runnable.class)` |
| Generic with target | `mi.CreateDelegate<Action>(obj)` | `ReflectionHelper.createDelegate(mi, obj, Runnable.class)` |
| Custom delegate type | `mi.CreateDelegate(typeof(MyCallback))` | `ReflectionHelper.createDelegate(mi, MyCallback.class)` |
| Delegate invocation chain | `mi.CreateDelegate(typeof(Action))()` | `ReflectionHelper.createDelegate(mi, Runnable.class).run()` |

## Edge Cases

- CreateDelegate return value `.Invoke()` calls: return type is delegate, so `.Invoke()` maps to SAM method name. Current `IsMethodInfoReceiver` logic is safe (checks for `System.Reflection.MethodInfo` type, not delegate type).
- Non-MethodInfo `CreateDelegate` calls: detection verifies receiver type is `System.Reflection.MethodInfo`.
- Null target: `CreateDelegate(typeof(Action), null)` is valid C#. Java helper passes null to `method.invoke(null, args)` which correctly invokes static methods.
