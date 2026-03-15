# DelegateTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Type/DelegateTransformer.cs`

## 1. C# Language Feature Converted

`DelegateTransformer` converts C# `delegate` declarations to Java `@FunctionalInterface` interfaces. The single abstract method (SAM) of the interface is always named `invoke`, replicating the delegate invocation contract:

```csharp
// C#
delegate void ShowGraph(GeometryGraph g);

// Java
@FunctionalInterface
public interface ShowGraph {
    void invoke(GeometryGraph g);
}
```

The transformer handles:
- Return types (including `void`).
- Parameter types with semantic model resolution.
- `ref`/`out` parameters via a holder-object wrapping strategy (`GetHolderType`).
- `params` (varargs) parameters.
- Generic type parameters on the delegate itself and inherited from enclosing types.
- Type parameter constraints propagated from `where` clauses.

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `goto nextParam;` is a code smell

The parameter processing loop uses a label + `goto` to skip to the next iteration when a `params` parameter is encountered:

```csharp
if (mod.IsKind(SyntaxKind.ParamsKeyword))
{
    var jp2 = new JavaParameter(javaType, paramName) { IsVarArgs = true };
    invokeMethod.Parameters.Add(jp2);
    goto nextParam;
}
// ...
invokeMethod.Parameters.Add(new JavaParameter(javaType, paramName));
nextParam:;
```

This pattern is fragile and hard to read. It exists because the inner `foreach (var mod in param.Modifiers)` loop breaks out to add the parameter after the modifier check, but when `params` is found the parameter must be added and the outer loop continued — impossible with a plain `break` because the inner loop would fall through to the unconditional `Add` below.

### Issue 2 — Duplicate type parameters when delegate is nested more than one level deep

The enclosing-class type-parameter collection loop:

```csharp
var cur = sym.ContainingType;
while (cur != null)
{
    foreach (var tp in cur.TypeParameters)
    {
        if (!allTypeParams.Contains(tp.Name))
        {
            allTypeParams.Insert(0, tp.Name);
            var jtp = new JavaTypeParameter(tp.Name);
            javaInterface.TypeParameters.Insert(0, jtp);  // ← Insert at position 0 each time
        }
    }
    cur = cur.ContainingType;
}
```

When walking a two-level nesting (`Outer<A>.Inner<B>.MyDelegate`), the loop first inserts type params from `Inner<B>` at index 0, then inserts `Outer<A>`'s params also at index 0 — reversing the order and potentially placing inner type params before outer ones. The guard `!allTypeParams.Contains(tp.Name)` prevents outright duplicates by name but does not preserve declaration order correctly.

### Issue 3 — `ref`/`out` conversion delegates to `GetHolderType` which may not exist

```csharp
javaType = GetHolderType(javaType);
```

`GetHolderType` is not shown in the visible file content. If this method produces a holder class name (e.g., `IntHolder`) that isn't included in the runtime classpath of the generated Java project, the generated code will fail to compile.

### Issue 4 — `out`-only and `in` parameters treated the same as `ref`

The loop:
```csharp
if (mod.IsKind(SyntaxKind.RefKeyword) || mod.IsKind(SyntaxKind.OutKeyword))
{
    javaType = GetHolderType(javaType);
    break;
}
```

C#'s `in` (read-only reference) is not checked here. If a delegate has an `in T param`, the parameter is treated as a plain by-value parameter, losing the intent that the value should not be modified (though this is a minor concern since Java's by-reference semantics differ anyway).

### Issue 5 — Generic delegate with return type that is itself a type parameter not fully resolved

When `node.ReturnType` is a type parameter name (e.g., `T`), `GetTypeInfo` may fail to resolve it via the semantic model in some edge cases, falling back to `"Object"`. The fallback is silent and loses generic type information in the method return type.

## 3. Proposed Fixes

### Fix 1 — Refactor parameter loop to eliminate `goto`

Restructure using a helper method or conditional variable:

```csharp
foreach (var param in node.ParameterList?.Parameters ?? ...)
{
    var (javaType, isVarArgs) = ResolveParamType(param, context);
    var paramName = ConversionContext.EscapeJavaKeyword(param.Identifier.Text);
    invokeMethod.Parameters.Add(new JavaParameter(javaType, paramName) { IsVarArgs = isVarArgs });
}
```

where `ResolveParamType` returns `(type, isVarArgs)` and handles the `ref`/`out`/`params` logic internally.

### Fix 2 — Fix enclosing type-parameter ordering

Build the enclosing type-param list from bottom to top (current nesting level first), then reverse before inserting:

```csharp
var enclosingParams = new List<JavaTypeParameter>();
var cur = sym.ContainingType;
while (cur != null)
{
    enclosingParams.InsertRange(0, cur.TypeParameters
        .Where(tp => !allTypeParams.Contains(tp.Name))
        .Select(tp => new JavaTypeParameter(tp.Name)));
    cur = cur.ContainingType;
}
javaInterface.TypeParameters.InsertRange(0, enclosingParams);
```

### Fix 3 — Document and validate `GetHolderType` contract

Add an XML doc comment on `GetHolderType` specifying the generated class name and ensure a corresponding holder class template is included in the runtime library emitted alongside the Java source.

### Fix 4 — Handle `in` modifier

```csharp
if (mod.IsKind(SyntaxKind.InKeyword))
    // in == read-only ref; in Java just pass by value
    break;
```

Making this explicit removes an implicit fall-through that could mis-process `in` parameters.

## 4. Commit Changes

```
fix(delegate): remove goto in parameter loop, fix enclosing type-parameter ordering, and handle in modifier
```
